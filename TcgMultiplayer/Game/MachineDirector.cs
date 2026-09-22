using System;
using System.Collections.Generic;
using HarmonyLib;
using HutongGames.PlayMaker;
using Steamworks;
using TcgMultiplayer.Net;
using UnityEngine;

namespace TcgMultiplayer.Game
{
    /// <summary>
    /// Machine occupancy and spectating.
    ///
    /// Two jobs, and the first is a prerequisite for the second:
    ///
    /// 1. OWNERSHIP. The host is authoritative. When you insert your card, we ask
    ///    the host to grant the machine; only on the grant does play proceed.
    ///    Everyone else gets the game's own "FREEZE MACHINE" event on that
    ///    cabinet, so it visibly refuses their card. Two players grabbing the same
    ///    machine in the same instant cannot both win.
    ///
    /// 2. SPECTATING. The owner mirrors every non-system FSM event fired inside
    ///    the machine's subtree; spectators replay them on their own copy of the
    ///    same FSMs. Events rather than states, because a PlayMaker graph fed the
    ///    same events walks the same path — and because the M0 trace showed a
    ///    machine's whole round is a couple of dozen events, not a stream.
    ///
    /// Divergence is still possible where a graph rolls dice locally
    /// (ActionRandomPrizeSelection is the obvious one). That's the known limit of
    /// event mirroring and where per-machine adapters will eventually earn their
    /// keep — but it costs nothing to find out empirically which machines drift.
    /// </summary>
    internal sealed class MachineDirector
    {
        private static MachineDirector _live;   // Harmony patches are static; this is the bridge

        private readonly Session _session;
        private readonly MachineRegistry _registry = new MachineRegistry();
        public readonly WalletGuard Wallet = new WalletGuard();
        public readonly Rehearsal Rehearse = new Rehearsal();
        public readonly PhysicsReplicator Physics = new PhysicsReplicator();

        /// <summary>Vehicles: the world pose of whatever is being driven.</summary>
        public readonly MoverReplicator Movers = new MoverReplicator();

        /// <summary>The local player's seat in someone else's vehicle, if any.</summary>
        public readonly RideAlong Ride = new RideAlong();

        /// <summary>Set by Plugin. The avatar side owns the rig; seating needs to move it.</summary>
        public Func<PlayerRig> RigSource;

        private readonly List<ulong> _evacuated = new List<ulong>();

        // Riding detection: who is carrying the player, worked out from motion
        // rather than from any FSM state name. See RideDetect.
        private readonly Dictionary<uint, int> _rideConfidence = new Dictionary<uint, int>();
        private readonly Dictionary<uint, Vector3> _lastRootPos = new Dictionary<uint, Vector3>();
        private readonly List<uint> _rideScratch = new List<uint>();
        private Vector3 _lastPlayerPos;
        private bool _havePlayerPos;
        private float _nextRideSampleAt;

        /// <summary>The thing currently carrying the local player, or 0.</summary>
        public uint RidingMachine { get; private set; }

        // Set while we're replaying a mirrored event, so it isn't re-broadcast.
        private static bool _applying;

        private float _nextRebuildAt;
        private float _lastRebuildAt = -999f;
        private int _pendingFsmCount = -1;

        /// <summary>Never walk the whole FSM list more often than this.</summary>
        private const float MinRebuildInterval = 8f;
        private bool _dirty = true;
        private float _nextWalletAt;
        private int _lastCoins = int.MinValue, _lastTickets = int.MinValue;
        private float _nextScanCheckAt;
        private int _lastFsmListCount = -1;

        public int MachineCount { get { return _registry.Count; } }
        public bool TryGetMachine(uint id, out Machine m) { return _registry.TryGet(id, out m); }
        public IEnumerable<Machine> Machines { get { return _registry.All; } }
        public bool Enabled = true;
        public int MirroredEventsSent, MirroredEventsApplied;

        /// <summary>Machine we currently believe we own, 0 if none.</summary>
        public uint MyMachine;

        public MachineDirector(Session session)
        {
            _session = session;
            _session.OnMachineClaim += OnClaimRequest;
            _session.OnMachineOwner += OnOwnerAnnounced;
            _session.OnMachineEvent += OnMirroredEvent;
            _session.OnMachinePhysics += OnPhysics;
            _session.OnObjectState += OnObjectState;
            _session.OnSeatRequest += OnSeatRequest;
            _session.OnSeatGrant += OnSeatGrant;
            _session.OnPeerGone += OnPeerGone;
            _live = this;
        }

        // --------------------------------------------------------------- setup

        public void ApplyPatches(HarmonyLib.Harmony harmony)
        {
            var processEvent = AccessTools.Method(typeof(Fsm), "ProcessEvent",
                new[] { typeof(FsmEvent), typeof(FsmEventData) });
            if (processEvent == null)
            {
                Plugin.Warn("Fsm.ProcessEvent not found — machine mirroring disabled.");
                CompatCheck.Set("PlayMaker hooks (Fsm.ProcessEvent, FsmState.OnEnter)", false,
                                "PlayMaker version changed");
                return;
            }
            harmony.Patch(processEvent, prefix: new HarmonyMethod(
                typeof(MachineDirector).GetMethod(nameof(OnProcessEvent),
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));

            // Occupancy is read straight off the game's own lifecycle states rather
            // than from any input of ours: whatever route the player took to put a
            // card in, the controller lands in "Card Inserted".
            var onEnter = AccessTools.Method(typeof(FsmState), "OnEnter", Type.EmptyTypes);
            if (onEnter == null)
            {
                Plugin.Warn("FsmState.OnEnter not found — machine occupancy will not be detected.");
                return;
            }
            harmony.Patch(onEnter, prefix: new HarmonyMethod(
                typeof(MachineDirector).GetMethod(nameof(OnStateEnter),
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));

            CompatCheck.Set("PlayMaker hooks (Fsm.ProcessEvent, FsmState.OnEnter)", true, null);
        }

        // States that mean "a player is now occupying this thing" / "...has left it".
        private static readonly HashSet<string> ClaimStates = new HashSet<string>(StringComparer.Ordinal)
        {
            "Card Inserted", "Turn On MECH", "Ready To Play",
        };

        private static readonly HashSet<string> ReleaseStates = new HashSet<string>(StringComparer.Ordinal)
        {
            "Card Removed", "Card Removed Inside", "Card Removed Inside 2",
            "Turn Off MECH", "Button Exit Machine", "Send Explore Mode Event", "Send Explore Mode",
            "Off of Ride and Done", "Off of Bus and Done", "PLAYER HIT EXIT",
        };

        private static void OnStateEnter(FsmState __instance)
        {
            var live = _live;
            if (live == null || _applying || !live.Enabled || __instance == null) return;

            try
            {
                var fsm = __instance.Fsm;
                if (fsm == null) return;
                var owner = fsm.Owner as PlayMakerFSM;
                if (owner == null) return;

                // Only the machine's own controller decides occupancy.
                string fsmName;
                try { fsmName = owner.FsmName; } catch { return; }
                if (fsmName != MachineRegistry.ControllerFsmName) return;

                var m = live._registry.Lookup(owner);
                if (m == null) return;

                var state = __instance.Name;
                if (ClaimStates.Contains(state))
                {
                    m.LocallyOccupied = true;
                    live.RequestClaim(m);
                }
                else if (ReleaseStates.Contains(state))
                {
                    m.LocallyOccupied = false;
                    live.ReleaseIfMine(m);
                }
            }
            catch { }
        }

        public void OnSceneChanged()
        {
            Physics.ReleaseAll();
            Movers.ReleaseAll();
            Ride.Leave(RigSource != null ? RigSource() : null, "the scene changed");
            _registry.Clear();
            _dirty = true;
            MyMachine = 0;
            _nextRebuildAt = Time.time + 3f;

            // Classification is per-object and the objects are gone. Keeping it
            // would mean a door in the new scene inheriting "is a vehicle" from
            // whatever hashed to the same id in the old one.
            Movers.ForgetClassification();
        }

        public void Tick()
        {
            if (_dirty && Time.time >= _nextRebuildAt)
            {
                _dirty = false;
                _lastFsmListCount = FsmListCount();
                _registry.Rebuild();
            }

            // A one-shot scan after the scene loads misses almost everything.
            // Most of the island starts deactivated ("CARNIE Starts OFF",
            // "OUTSIDE Starts OFF", the arcade interior), and PlayMaker only
            // registers an FSM once its GameObject has actually awoken — so
            // machines appear in FsmList as you walk into them. Watching the list
            // size is a cheap way to notice a new district coming online.
            // ...but the count is noisy in a way the first version didn't allow
            // for. Coin pushers spawn and despawn COIN FSMs in batches — there
            // are 195 of them in the arcade alone — so a busy machine trips a
            // 32-FSM threshold every two seconds, and each trip walks all ~4,500
            // FSMs twice. That is a hitch you can feel, caused entirely by
            // watching for something that hasn't happened.
            //
            // So the count has to settle before it counts: two consecutive
            // samples that agree with each other, and a floor between rebuilds.
            // A district coming online is a step change that stays; coins are
            // churn that doesn't.
            if (Time.time >= _nextScanCheckAt)
            {
                _nextScanCheckAt = Time.time + 2f;
                int n = FsmListCount();

                if (_lastFsmListCount < 0)
                {
                    _lastFsmListCount = n;
                    _registry.Rebuild();
                    _lastRebuildAt = Time.time;
                }
                else if (Mathf.Abs(n - _lastFsmListCount) > 32)
                {
                    bool settled = Mathf.Abs(n - _pendingFsmCount) <= 32;
                    _pendingFsmCount = n;

                    if (settled && Time.time - _lastRebuildAt >= MinRebuildInterval)
                    {
                        _lastFsmListCount = n;
                        _lastRebuildAt = Time.time;
                        _registry.Rebuild();
                    }
                }
                else _pendingFsmCount = n;
            }

            // Belt and braces: if anything ever leaves a machine owned by the fake
            // rehearsal peer while no rehearsal is running, hand it straight back.
            // A cabinet owned by a peer that does not exist can never be released
            // by the normal path.
            if (!Rehearse.Playing)
            {
                foreach (var m in _registry.All)
                {
                    if (m.Owner != Rehearsal.FakePeerId) continue;
                    Plugin.Warn("Reclaiming " + m.Label + " from a stale rehearsal peer.");
                    Grant(m, 0, null);
                }
            }

            // Only the machine you're actually playing gets its physics streamed,
            // and only while someone is playing it — which is the interest
            // management the plan called for, arrived at for free: one machine is
            // occupied at a time, per player.
            if (MyMachine != 0 && Physics.ShouldSend(Time.time))
            {
                Machine mine;
                if (_registry.TryGet(MyMachine, out mine) && mine.OwnedByMe)
                {
                    var payload = Physics.Pack(mine);
                    if (payload != null)
                    {
                        // Recorded offline too, so a rehearsal replays the coins.
                        Rehearse.NoteFrame(mine.Id, payload);
                        if (_session.State == SessionState.InLobby && _session.Peers.Count > 0)
                            _session.SendMachinePhysics(mine.Id, payload);
                    }
                }
            }

            // Which of the 72 interactables actually travel. Recomputed at a
            // gentle pace rather than once, because most of the island starts
            // deactivated and a vehicle you have never walked near has never
            // been in FsmList to watch.
            var rigNow = RigSource != null ? RigSource() : null;
            Movers.Watch(_registry.All, rigNow != null ? rigNow.Root : null);

            // The vehicle you are driving, in world space. Separate from the
            // rigidbody stream above and sent alongside it: that one carries the
            // wheels relative to the car, this one carries the car.
            uint streamId = RidingMachine != 0 ? RidingMachine : MyMachine;
            if (streamId != 0 && _session.State == SessionState.InLobby
                && _session.Peers.Count > 0 && Movers.ShouldSend(Time.time))
            {
                Machine mine;
                if (_registry.TryGet(streamId, out mine) && mine.OwnedByMe && mine.IsMover)
                    _session.SendObjectState(mine.Id, Movers.Pack(mine));
            }

            // Vehicles first, then the things inside them: the wheels are packed
            // relative to the body, so the body has to be in the right place
            // before they are placed against it.
            DetectRiding();

            Movers.Render(LookupMachine);
            Physics.Render();

            TickRide();

            if (Rehearse.Playing)
            {
                Machine rm;
                if (_registry.TryGet(Rehearse.RecordedMachine, out rm))
                    Rehearse.Tick(rm, ApplyAsRemote, (mm, pay) => Physics.Unpack(mm, pay), () => Wallet.RestoreCount);
                else Rehearse.Stop(null);
            }

            // Scoreboard only — nothing authoritative rides on these numbers, so
            // send on change and otherwise no more than once a second.
            if (_session.State == SessionState.InLobby && Time.time >= _nextWalletAt)
            {
                _nextWalletAt = Time.time + 1f;
                int c = Wallet.Coins, t = Wallet.Tickets;
                if (c != _lastCoins || t != _lastTickets)
                {
                    _lastCoins = c; _lastTickets = t;
                    _session.BroadcastWallet(c, t, Wallet.TicketsThisSession);
                }
            }
        }

        private static int FsmListCount()
        {
            try { return PlayMakerFSM.FsmList.Count; } catch { return -1; }
        }

        public void RebuildNow()
        {
            _dirty = false;
            _registry.Rebuild();
        }

        // ----------------------------------------------------- ownership (local)

        /// <summary>Called when the local player's card goes into a machine.</summary>
        public void RequestClaim(Machine m)
        {
            if (m == null || !Enabled) return;

            // Never claim something the player is carrying. CONTROLLERS lives
            // under the player rig and moves whenever they do, which made it
            // look like an occupied machine and got it claimed, frozen for
            // everyone else and streamed as a vehicle.
            var rig = RigSource != null ? RigSource() : null;
            if (rig != null && rig.Valid && rig.Root != null && m.Root != null
                && m.Root.IsChildOf(rig.Root))
                return;
            if (m.Owner != 0 && m.Owner != _session.SelfId.m_SteamID)
            {
                // Do NOT freeze here. If the player has already got far enough into
                // the machine to be entering a claim state, freezing traps them —
                // "Machine Is Frozen" is a dead end in the game's own graph, with no
                // way out but UNFREEZE. Refusing the claim is enough; they keep
                // control of their own exit.
                Plugin.Log("Machine " + m.Label + " is in use by "
                           + (m.OwnerName ?? "someone else") + " — not claiming.");
                return;
            }

            if (_session.State != SessionState.InLobby)
            {
                // Solo: nothing to arbitrate, take it.
                Grant(m, _session.SelfId.m_SteamID, _session.SelfName);
                return;
            }

            if (_session.IsHost) DecideClaim(_session.SelfId, m.Id);
            else _session.SendMachineClaim(m.Id);
        }

        public void ReleaseIfMine(Machine m)
        {
            if (m == null || !m.OwnedByMe) return;
            if (_session.State == SessionState.InLobby)
            {
                if (_session.IsHost) DecideRelease(m.Id);
                else _session.SendMachineClaim(m.Id, release: true);
            }
            else Grant(m, 0, null);
        }

        // ------------------------------------------------------ ownership (host)

        private void OnClaimRequest(CSteamID from, uint machineId, bool release)
        {
            if (!_session.IsHost) return;
            if (release) DecideRelease(machineId);
            else DecideClaim(from, machineId);
        }

        private void DecideClaim(CSteamID who, uint machineId)
        {
            Machine m;
            if (!_registry.TryGet(machineId, out m)) return;

            // First claim wins, arbitrated in one place so it can't double-book.
            if (m.Owner != 0 && m.Owner != who.m_SteamID)
            {
                _session.SendMachineOwner(machineId, m.Owner, m.OwnerName, onlyTo: who);
                return;
            }

            var name = NameOf(who);
            Grant(m, who.m_SteamID, name);
            _session.SendMachineOwner(machineId, who.m_SteamID, name);
        }

        private void DecideRelease(uint machineId)
        {
            Machine m;
            if (!_registry.TryGet(machineId, out m)) return;
            Grant(m, 0, null);
            _session.SendMachineOwner(machineId, 0, null);
        }

        private void OnOwnerAnnounced(uint machineId, ulong owner, string ownerName)
        {
            Machine m;
            if (!_registry.TryGet(machineId, out m)) return;
            Grant(m, owner, ownerName);
        }

        private void Grant(Machine m, ulong owner, string ownerName)
        {
            m.Owner = owner;
            m.OwnerName = ownerName;
            m.OwnedByMe = owner != 0 && owner == _session.SelfId.m_SteamID;

            if (m.Id == MyMachine && !m.OwnedByMe) MyMachine = 0;
            if (m.OwnedByMe)
            {
                MyMachine = m.Id;
                Physics.ReleaseMachine(m.Id);
                Movers.Release(m.Id);            // we drive it now, nobody streams it to us
            }

            // Seat 0 follows ownership, always. The driver's seat is not
            // something to be requested — it belongs to whoever holds the
            // machine, and that is decided one place up.
            if (m.Seats.Count > 0 || owner != 0) Seating.SetDriver(m.Seats, owner);

            if (owner == 0)
            {
                Unfreeze(m);
                Physics.ReleaseMachine(m.Id);    // local simulation resumes
                Movers.Release(m.Id);

                // Nobody is driving, so nobody is a passenger. Everyone gets put
                // back on their feet rather than left pinned to a parked car.
                Seating.Evacuate(m.Seats, _evacuated);
                m.MySeat = -1;
                if (Ride.Riding == m.Id)
                    Ride.Leave(RigSource != null ? RigSource() : null, "the driver got out");

                Plugin.Log("Machine free: " + m.Label);
            }
            else if (!m.OwnedByMe)
            {
                Freeze(m);
                Plugin.Log("Machine " + m.Label + " taken by " + (ownerName ?? owner.ToString()));
            }
            else
            {
                Plugin.Log("Machine " + m.Label + " is yours.");
            }
        }

        private void OnPeerGone(CSteamID who)
        {
            // Never leave a cabinet locked because someone disconnected mid-round.
            foreach (var m in _registry.All)
            {
                if (m.Owner == who.m_SteamID)
                {
                    Grant(m, 0, null);
                    if (_session.IsHost) _session.SendMachineOwner(m.Id, 0, null);
                    continue;
                }

                // A passenger who dropped out mid-drive leaves a seat behind
                // that nothing else will ever free, and with it a car that can
                // never be got into again.
                if (Seating.Release(m.Seats, who.m_SteamID) < 0) continue;
                if (_session.IsHost) _session.SendSeatGrant(m.Id, m.Seats.ToArray());
            }
        }

        // -------------------------------------------------------------- freezing

        private void Freeze(Machine m)
        {
            if (m == null || m.Frozen) return;
            // Two hard rules, both learned the hard way:
            //  - never freeze a machine the local player is standing in, or they
            //    cannot leave it;
            //  - only freeze controllers that actually have the Frozen state
            //    (14 of 72 do) — the rest just swallow the event.
            if (m.LocallyOccupied) return;
            if (!m.SupportsFreeze) return;

            // Third rule, learned from two windows: never freeze a VEHICLE.
            //
            // Freezing exists so a cabinet visibly refuses someone else's card.
            // A vehicle's controller is not just a card reader — it is what the
            // game drives the whole vehicle through, and "Machine Is Frozen" is
            // a dead-end state in that graph. Putting a parked cart into it while
            // another player is standing next to it locked THAT player in place:
            // stuck while the cart sat still, free the moment its owner drove
            // off, stuck again when it stopped. Exactly the pattern reported.
            //
            // Nothing is lost by skipping it. Ownership already refuses the
            // claim, which is the whole point; the freeze was only ever the
            // visible half of that.
            if (m.IsMover)
            {
                Plugin.Log("Not freezing " + m.Label + " — it's a vehicle, and freezing one "
                           + "traps whoever is standing near it.");
                return;
            }

            m.Frozen = true;
            SendToController(m, "FREEZE MACHINE");
        }

        private void Unfreeze(Machine m)
        {
            if (m == null || !m.Frozen) return;
            m.Frozen = false;
            SendToController(m, "UNFREEZE MACHINE");
        }

        /// <summary>
        /// Escape hatch. Releases every machine and unfreezes anything we froze —
        /// bound to a hotkey and a button, because being stuck in a cabinet with no
        /// way out is the worst failure this mod can have.
        /// </summary>
        public int ReleaseEverything()
        {
            int n = 0;
            foreach (var m in _registry.All)
            {
                if (m.Frozen) { Unfreeze(m); n++; }
                if (m.Owner != 0)
                {
                    m.Owner = 0; m.OwnerName = null; m.OwnedByMe = false;
                    n++;
                }
                m.LocallyOccupied = false;
                m.MySeat = -1;
                m.Seats.Clear();
            }
            MyMachine = 0;
            Physics.ReleaseAll();
            Movers.ReleaseAll();

            // The panic key has to get you out of a moving vehicle too. Being
            // stuck as a passenger is exactly the kind of stuck it exists for.
            if (Ride.Riding != 0 || Ride.Pending != 0)
            {
                Ride.Leave(RigSource != null ? RigSource() : null, "panic key");
                n++;
            }

            if (n > 0) Plugin.Log("Released everything (" + n + " changes).");
            return n;
        }

        private void SendToController(Machine m, string evt)
        {
            if (m.Controller == null) return;
            try
            {
                _applying = true;                     // our own nudge isn't gameplay to mirror
                m.Controller.Fsm.Event(evt);
            }
            catch (Exception ex) { Plugin.Warn("Could not send '" + evt + "' to " + m.Label + ": " + ex.Message); }
            finally { _applying = false; }
        }

        // ------------------------------------------------------------- mirroring

        private static void OnProcessEvent(Fsm __instance, FsmEvent fsmEvent)
        {
            var live = _live;
            if (live == null || _applying || !live.Enabled) return;
            if (__instance == null || fsmEvent == null) return;

            try
            {
                if (fsmEvent.IsSystemEvent) return;
                var owner = __instance.Owner as PlayMakerFSM;
                if (owner == null) return;

                var m = live._registry.Lookup(owner);
                if (m == null) return;

                // Only the machine's owner narrates it.
                if (!m.OwnedByMe) return;

                var name = fsmEvent.Name;
                if (string.IsNullOrEmpty(name)) return;

                var fsmId = live._registry.IdOf(owner);

                // Recording works offline — that's the point of Rehearsal.
                live.Rehearse.Note(m.Id, fsmId, name);

                if (live._session.State != SessionState.InLobby || live._session.Peers.Count == 0) return;
                live._session.SendMachineEvent(m.Id, fsmId, name);
                live.MirroredEventsSent++;
            }
            catch { /* never let a hook take down the frame */ }
        }

        // --------------------------------------------------------- riding along

        private Machine LookupMachine(uint id)
        {
            Machine m;
            return _registry.TryGet(id, out m) ? m : null;
        }

        /// <summary>
        /// Work out what, if anything, is carrying the player, by watching
        /// whether the two move as one body.
        ///
        /// This replaces relying on the game's card-insert states, which are
        /// right for cabinets and silent for everything you climb into — so a
        /// golf cart was never claimed and never replicated, while CONTROLLERS
        /// (an object the player carries, so it moves whenever they do) was
        /// claimed instead.
        /// </summary>
        private void DetectRiding()
        {
            if (Time.time < _nextRideSampleAt) return;
            _nextRideSampleAt = Time.time + RideDetect.SampleInterval;

            var rig = RigSource != null ? RigSource() : null;
            if (rig == null || !rig.Valid || rig.Mesh == null)
            {
                _havePlayerPos = false;
                return;
            }

            var here = rig.Mesh.position;
            if (!_havePlayerPos)
            {
                _havePlayerPos = true;
                _lastPlayerPos = here;
                SnapshotRoots();
                return;
            }

            var playerDelta = here - _lastPlayerPos;
            _lastPlayerPos = here;

            // Already aboard something: hold on until we're plainly away from
            // it. A parked vehicle you're sitting in produces no motion to
            // test, so distance is the only honest measure of getting out.
            if (RidingMachine != 0)
            {
                Machine cur;
                if (_registry.TryGet(RidingMachine, out cur) && cur.Moving != null
                    && RideDetect.StillAboard(Vector3.Distance(here, cur.Moving.position)))
                {
                    ResolveBody(cur, playerDelta);
                    SnapshotRoots();
                    return;
                }

                Plugin.Log("Ride: got out of " + (cur != null ? cur.Label : "it") + ".");
                var was = RidingMachine;
                RidingMachine = 0;
                _rideConfidence.Clear();
                Machine leave;
                if (_registry.TryGet(was, out leave)) ReleaseIfMine(leave);
                SnapshotRoots();
                return;
            }

            var playerRoot = rig.Root;

            foreach (var m in _registry.All)
            {
                if (m == null || m.Root == null) continue;

                // Things the player carries move exactly with the player by
                // definition. That is what claimed CONTROLLERS the first time.
                if (playerRoot != null && m.Root.IsChildOf(playerRoot)) continue;

                Vector3 was;
                if (!_lastRootPos.TryGetValue(m.Id, out was)) continue;

                bool together = RideDetect.MovesWith(playerDelta, m.Root.position - was);

                int c;
                _rideConfidence.TryGetValue(m.Id, out c);
                c = RideDetect.Advance(c, together);
                _rideConfidence[m.Id] = c;

                if (!RideDetect.IsRiding(c)) continue;

                // It demonstrably travels, and it is carrying us.
                if (!m.IsMover)
                {
                    m.IsMover = true;
                    MoverReplicator.Measure(m);
                }

                RidingMachine = m.Id;

                // The full path matters here, not just the label. In this game
                // the "machine" is whatever object carries the card-reader FSM,
                // and on a vehicle that may well be a reader bolted to the
                // vehicle rather than the vehicle itself — in which case
                // streaming this root moves the reader and leaves the cart
                // behind. Worth being able to see which it is.
                Plugin.Log("Ride: you're in " + m.Label + " (it's moving with you). path=" + m.Path
                           + " extents=" + m.Extents + " rideable=" + m.Rideable);
                RequestClaim(m);
                break;
            }

            SnapshotRoots();
        }

        private readonly Dictionary<int, Vector3> _lastAncestorPos = new Dictionary<int, Vector3>();

        /// <summary>Remember where the chain is now, so the next sample has something to compare with.</summary>
        private void SnapshotAncestors(Machine m)
        {
            if (m == null || m.Root == null) return;
            var t = m.Root.parent;
            int guard = 0;
            while (t != null && guard++ < 8)
            {
                _lastAncestorPos[t.GetInstanceID()] = t.position;
                t = t.parent;
            }
        }

        /// <summary>
        /// Find the thing that actually travels.
        ///
        /// The machine is rooted where the game's card-reader FSM lives, and on
        /// the golf cart that is
        /// PLAYER_Vehicles/GOLF CART/GOLFCART_Vehicle/CONTROLLERS — the steering
        /// controls, five centimetres across. Streaming that sent an invisible
        /// cube around the island and left the cart parked.
        ///
        /// So walk up while each parent is still moving with us and take the
        /// highest one that is. The first ancestor that ISN'T moving with us is
        /// the scenery the vehicle is standing in, and everything below it is
        /// the vehicle. No names, so it works on all of them.
        /// </summary>
        private void ResolveBody(Machine m, Vector3 playerDelta)
        {
            if (m == null || m.Root == null) return;

            // A sample where nothing moved carries no information. The first
            // version concluded from it anyway: MovesWith is false when there
            // is no motion, so the climb broke immediately and the body fell
            // back to Root. Every time the cart paused, the answer collapsed
            // from GOLFCART_Vehicle to its 5cm control panel again.
            if (playerDelta.magnitude < RideDetect.MinMotion)
            {
                SnapshotAncestors(m);
                return;
            }

            var best = m.Root;
            var t = m.Root.parent;
            int guard = 0;
            int up = 0, bestUp = 0;

            while (t != null && guard++ < 8)
            {
                up++;
                Vector3 was;
                if (_lastAncestorPos.TryGetValue(t.GetInstanceID(), out was))
                {
                    if (!RideDetect.MovesWith(playerDelta, t.position - was)) break;
                    best = t;
                    bestUp = up;
                }
                _lastAncestorPos[t.GetInstanceID()] = t.position;
                t = t.parent;
            }

            // Only ever climb. Going back down is always wrong — it means we
            // learned nothing this sample, not that the vehicle shrank.
            if (m.Body != null && bestUp <= m.BodyUp)
            {
                SnapshotAncestors(m);
                return;
            }

            m.Body = best;
            m.BodyUp = bestUp;

            // The size is the vehicle's now, not the control panel's, so seats
            // and whether anyone can ride it both have to be worked out again.
            m.MeasuredExtents = false;
            MoverReplicator.Measure(m);
            Plugin.Log("Ride: the thing that actually moves is " + NetId.Path(best)
                       + " — extents " + m.Extents + ", seats " + m.Capacity
                       + ", rideable " + m.Rideable + ".");
        }

        private void SnapshotRoots()
        {
            _rideScratch.Clear();
            foreach (var m in _registry.All)
                if (m != null && m.Root != null) _lastRootPos[m.Id] = m.Root.position;
        }

        /// <summary>
        /// What the local player is aboard, for the avatar stream — as a
        /// passenger OR as the driver.
        ///
        /// The driver case was missed first time round and it is the common one.
        /// When you get into a vehicle the game parks the PLAYER object where
        /// you were standing and drives the vehicle instead, so the rig's
        /// position stops changing. Everyone else sees your body standing on the
        /// pavement by the cart, frozen, while you drive away — which is exactly
        /// what happened the first time two windows were ever connected.
        ///
        /// Treating the driver as attached at seat 0 fixes it with the machinery
        /// that was already there for passengers.
        /// </summary>
        public Attachment CurrentAttachment
        {
            get
            {
                // Passenger first: if we're riding in someone else's vehicle
                // that is where we are, whatever else we might own.
                var riding = Ride.Current;
                if (riding.Any)
                {
                    Machine rm;
                    if (_registry.TryGet(riding.Machine, out rm))
                    {
                        riding.LocalPos = SeatOffset(rm, riding.Seat);
                        riding.LocalYaw = 0f;
                    }
                    return riding;
                }

                // Driving: only for things that travel. Standing at a coin
                // pusher is not being inside it, and pinning a body to a
                // cabinet's notional seat would look worse than leaving it be.
                // What is carrying us, which is not the same question as what we
                // own — and is answered by watching, not by FSM state names.
                uint id = RidingMachine != 0 ? RidingMachine : MyMachine;
                if (id == 0) return new Attachment();

                Machine mine;
                if (!_registry.TryGet(id, out mine)) return new Attachment();
                if (!mine.IsMover || !mine.OwnedByMe) return new Attachment();

                var a = new Attachment
                {
                    Machine = mine.Id,
                    Seat = (byte)Seating.DriverSeat,
                    LocalPos = SeatOffset(mine, Seating.DriverSeat),
                    LocalYaw = 0f,
                };

                // Prefer where the game has actually put the driver over where
                // we guessed a seat might be. It moves the player into the seat
                // itself, and it knows exactly where that is — the guessed
                // offset was only ever needed because the rig was thought to be
                // parked, and it isn't. Expressed in the vehicle's frame, so it
                // still rides along perfectly.
                var rig = RigSource != null ? RigSource() : null;
                if (rig != null && rig.Valid && rig.Mesh != null && mine.Moving != null)
                {
                    var local = mine.Moving.InverseTransformPoint(rig.Mesh.position);

                    // Sanity: if the rig has wandered off — parked where they
                    // boarded, or mid-teleport — fall back to the guess rather
                    // than seating someone ten metres behind the cart.
                    float reach = mine.Extents.magnitude + 2f;
                    if (local.magnitude <= reach)
                    {
                        a.LocalPos = local;
                        a.LocalYaw = Mathf.DeltaAngle(mine.Moving.eulerAngles.y, rig.Mesh.eulerAngles.y);
                    }
                }

                return a;
            }
        }

        /// <summary>
        /// A remote player has told us where their seat is, in the vehicle's own
        /// space. The driver's is the useful one: every passenger seat is placed
        /// relative to it, which beats guessing from a bounding box because the
        /// game seats the driver itself and we are simply reading it back.
        /// </summary>
        public void NoteRemoteSeat(uint machineId, byte seat, Vector3 local)
        {
            Machine m;
            if (!_registry.TryGet(machineId, out m)) return;
            if (seat != Seating.DriverSeat) return;
            m.DriverLocal = local;
            m.HasDriverLocal = true;
        }

        /// <summary>Where a seat is, preferring the driver's real position over a guess.</summary>
        private static Vector3 SeatOffset(Machine m, int seat)
        {
            return m.HasDriverLocal
                ? Seating.OffsetFrom(m.DriverLocal, seat, m.Extents)
                : Seating.Offset(seat, m.Extents);
        }

        /// <summary>Can the local player get into this thing right now, and why not?</summary>
        public bool CanRide(Machine m, out string why)
        {
            why = null;
            if (m == null) { why = "nothing there"; return false; }
            if (!m.IsMover) { why = "that doesn't go anywhere"; return false; }
            if (!m.Rideable) { why = "that moves, but it's not something you can sit in"; return false; }
            if (_session.State != SessionState.InLobby) { why = "you're not in a session"; return false; }
            if (m.OwnedByMe) { why = "you're driving it"; return false; }
            if (m.Owner == 0) { why = "nobody is driving it — get in and drive"; return false; }
            if (Seating.Used(m.Seats) >= m.Capacity) { why = "it's full"; return false; }
            return true;
        }

        /// <summary>Ask to get in. The host decides which seat, or that there isn't one.</summary>
        public void RequestRide(Machine m)
        {
            string why;
            if (!CanRide(m, out why)) { Ride.Status = why; return; }

            Ride.Pending = m.Id;
            Ride.PendingSince = Time.time;
            Ride.Status = "asking to get into " + m.Label + "...";

            if (_session.IsHost) DecideSeat(_session.SelfId, m.Id, false);
            else _session.SendSeatRequest(m.Id, false);
        }

        public void LeaveRide()
        {
            uint id = Ride.Riding != 0 ? Ride.Riding : Ride.Pending;
            if (id == 0) return;

            // Get out locally first and tell the host afterwards. Waiting for a
            // round trip to stand up again is how someone ends up welded to a
            // car while their connection decides what it thinks.
            Ride.Leave(RigSource != null ? RigSource() : null, "you got out");

            if (_session.State != SessionState.InLobby) return;
            if (_session.IsHost) DecideSeat(_session.SelfId, id, true);
            else _session.SendSeatRequest(id, true);
        }

        private void OnSeatRequest(CSteamID from, uint machineId, bool leave)
        {
            if (!_session.IsHost) return;
            DecideSeat(from, machineId, leave);
        }

        private void DecideSeat(CSteamID who, uint machineId, bool leave)
        {
            Machine m;
            if (!_registry.TryGet(machineId, out m)) return;

            // The rule itself lives in Seating, where it can be tested. All this
            // does is turn the ruling into packets.
            var d = Seating.Decide(m.Seats, who.m_SteamID, m.Owner, m.Rideable, m.Capacity, leave);

            if (!d.Ok)
            {
                Plugin.Log("Seat refused on " + m.Label + ": " + d.Ruling);
                // Answer anyway, so the asker stops waiting on us rather than
                // sitting on a pending request until it times out.
                _session.SendSeatGrant(m.Id, m.Seats.ToArray(), who);
                return;
            }

            ApplySeats(m, m.Seats.ToArray());
            _session.SendSeatGrant(m.Id, m.Seats.ToArray());
        }

        private void OnSeatGrant(uint machineId, ulong[] seats)
        {
            Machine m;
            if (!_registry.TryGet(machineId, out m)) return;
            ApplySeats(m, seats);
        }

        /// <summary>
        /// Adopt the host's seating for a machine, and move ourselves in or out
        /// to match it. The host's list is the truth — if we think we're aboard
        /// and it doesn't, we get out.
        /// </summary>
        private void ApplySeats(Machine m, ulong[] seats)
        {
            m.Seats.Clear();
            if (seats != null) m.Seats.AddRange(seats);

            int mine = Seating.SeatOf(m.Seats, _session.SelfId.m_SteamID);
            m.MySeat = mine;

            var rig = RigSource != null ? RigSource() : null;

            if (mine > 0)
            {
                if (Ride.Riding != m.Id || Ride.Seat != mine)
                {
                    if (!Ride.Board(m, mine, rig))
                        Ride.Status = "couldn't take the seat — the player rig isn't ready";
                }
            }
            else if (Ride.Riding == m.Id)
            {
                Ride.Leave(rig, "the host says you're not in it");
            }

            if (Ride.Pending == m.Id && mine <= 0)
            {
                Ride.Pending = 0;
                if (Ride.Riding == 0) Ride.Status = m.Label + " is full.";
            }
        }

        /// <summary>
        /// Every frame: hold the seat, and bail out the moment holding it stops
        /// being safe. Four separate things end a ride and they all land here,
        /// so there is no path that leaves someone stuck inside the scenery.
        /// </summary>
        private void TickRide()
        {
            var rig = RigSource != null ? RigSource() : null;

            if (Ride.RequestExpired(Time.time))
            {
                Ride.Pending = 0;
                Ride.Status = "no answer from the host — try again";
            }

            if (Ride.Riding == 0) return;

            Machine m;
            if (!_registry.TryGet(Ride.Riding, out m)) { Ride.Leave(rig, "the vehicle is gone"); return; }
            if (m.Owner == 0) { Ride.Leave(rig, "the driver got out"); return; }
            if (_session.State != SessionState.InLobby) { Ride.Leave(rig, "the session ended"); return; }
            if (!Ride.Hold(m, rig)) Ride.Leave(rig, "lost the seat");
        }

        private void OnObjectState(CSteamID from, uint machineId, Session.ObjectPose pose)
        {
            Machine m;
            if (!_registry.TryGet(machineId, out m)) return;

            // If we own it we are the one driving, and someone else's idea of
            // where it is does not get to overrule the wheel in our hands.
            if (m.OwnedByMe) return;
            if (m.Owner != 0 && m.Owner != from.m_SteamID) return;

            // Being sent one of these IS the evidence that this thing travels.
            // Without it each side has to watch its own copy move before it
            // believes it — and a cart parked on our screen never will, so a
            // guest would refuse to let anyone ride a vehicle it had not
            // personally seen move.
            if (!m.IsMover)
            {
                m.IsMover = true;
                MoverReplicator.Measure(m);
                Plugin.Log("Mover: " + m.Label + " travels (its driver said so).");
            }

            Movers.Push(m, pose);
        }

        private void OnPhysics(CSteamID from, uint machineId, byte[] payload)
        {
            Machine m;
            if (!_registry.TryGet(machineId, out m)) return;
            if (m.OwnedByMe) return;             // we're the one simulating it
            Physics.Unpack(m, payload);
        }

        /// <summary>The genuine spectator path, reachable by Rehearsal so a solo
        /// test exercises the same code a real peer would.</summary>
        public void ApplyAsRemote(uint machineId, uint fsmId, string evt)
        {
            ApplyMirrored(machineId, fsmId, evt);
        }

        private void OnMirroredEvent(CSteamID from, uint machineId, uint fsmId, string evt)
        {
            ApplyMirrored(machineId, fsmId, evt);
        }

        private void ApplyMirrored(uint machineId, uint fsmId, string evt)
        {
            Machine m;
            if (!_registry.TryGet(machineId, out m)) return;
            if (m.OwnedByMe) return;               // we're the narrator, not the audience

            PlayMakerFSM fsm;
            if (!m.Fsms.TryGetValue(fsmId, out fsm) || fsm == null) return;

            // Wallets are per-player. Someone else's round must not pay us, so the
            // economy is snapshotted around the replay and put back afterwards —
            // which closes every payout path rather than the ones we happened to
            // think of.
            Wallet.Snapshot();
            try
            {
                _applying = true;
                fsm.Fsm.Event(evt);
                MirroredEventsApplied++;
            }
            catch (Exception ex) { Plugin.Warn("Mirror apply failed on " + m.Label + ": " + ex.Message); }
            finally
            {
                _applying = false;
                Wallet.Restore();
            }
        }

        // ----------------------------------------------------------------- misc

        private string NameOf(CSteamID id)
        {
            if (id == _session.SelfId) return _session.SelfName;
            foreach (var p in _session.Peers) if (p.Id == id) return p.Name;
            return id.m_SteamID.ToString();
        }

        /// <summary>Solo test: pretend a friend just took this machine.</summary>
        public void SimulateRemoteClaim(Machine m)
        {
            if (m == null) return;
            Grant(m, Rehearsal.FakePeerId, Rehearsal.FakePeerName);
        }

        /// <summary>Solo test: give it back.</summary>
        public void SimulateRemoteRelease(Machine m)
        {
            if (m == null) return;
            Grant(m, 0, null);
        }

        /// <summary>Nearest machine to a point, for the overlay's manual claim buttons.</summary>
        public Machine Nearest(Vector3 from, float maxDistance)
        {
            Machine best = null;
            float bestSq = maxDistance * maxDistance;
            foreach (var m in _registry.All)
            {
                if (m.Moving == null) continue;
                var d = (m.Moving.position - from).sqrMagnitude;
                if (d < bestSq) { bestSq = d; best = m; }
            }
            return best;
        }
    }
}
