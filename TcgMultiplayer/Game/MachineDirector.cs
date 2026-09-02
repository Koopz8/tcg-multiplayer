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

        // Set while we're replaying a mirrored event, so it isn't re-broadcast.
        private static bool _applying;

        private float _nextRebuildAt;
        private bool _dirty = true;
        private float _nextWalletAt;
        private int _lastCoins = int.MinValue, _lastTickets = int.MinValue;
        private float _nextScanCheckAt;
        private int _lastFsmListCount = -1;

        public int MachineCount { get { return _registry.Count; } }
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
                if (ClaimStates.Contains(state)) live.RequestClaim(m);
                else if (ReleaseStates.Contains(state)) live.ReleaseIfMine(m);
            }
            catch { }
        }

        public void OnSceneChanged()
        {
            _registry.Clear();
            _dirty = true;
            MyMachine = 0;
            _nextRebuildAt = Time.time + 3f;
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
            if (Time.time >= _nextScanCheckAt)
            {
                _nextScanCheckAt = Time.time + 2f;
                int n = FsmListCount();
                if (_lastFsmListCount < 0 || Mathf.Abs(n - _lastFsmListCount) > 32)
                {
                    _lastFsmListCount = n;
                    _registry.Rebuild();
                }
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
            if (m.Owner != 0 && m.Owner != _session.SelfId.m_SteamID)
            {
                Freeze(m);
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
            if (m.OwnedByMe) MyMachine = m.Id;

            if (owner == 0)
            {
                Unfreeze(m);
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
                if (m.Owner != who.m_SteamID) continue;
                Grant(m, 0, null);
                if (_session.IsHost) _session.SendMachineOwner(m.Id, 0, null);
            }
        }

        // -------------------------------------------------------------- freezing

        private void Freeze(Machine m)
        {
            SendToController(m, "FREEZE MACHINE");
        }

        private void Unfreeze(Machine m)
        {
            SendToController(m, "UNFREEZE MACHINE");
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
                if (live._session.State != SessionState.InLobby || live._session.Peers.Count == 0) return;

                var name = fsmEvent.Name;
                if (string.IsNullOrEmpty(name)) return;

                live._session.SendMachineEvent(m.Id, live._registry.IdOf(owner), name);
                live.MirroredEventsSent++;
            }
            catch { /* never let a hook take down the frame */ }
        }

        private void OnMirroredEvent(CSteamID from, uint machineId, uint fsmId, string evt)
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

        /// <summary>Nearest machine to a point, for the overlay's manual claim buttons.</summary>
        public Machine Nearest(Vector3 from, float maxDistance)
        {
            Machine best = null;
            float bestSq = maxDistance * maxDistance;
            foreach (var m in _registry.All)
            {
                if (m.Root == null) continue;
                var d = (m.Root.position - from).sqrMagnitude;
                if (d < bestSq) { bestSq = d; best = m; }
            }
            return best;
        }
    }
}
