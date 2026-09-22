using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;

namespace TcgMultiplayer.Game
{
    /// <summary>
    /// One interactable thing a player can occupy: an arcade cabinet, a carnival
    /// ride, a food booth, a vending machine, a lotto menu, or a vehicle.
    /// </summary>
    internal sealed class Machine
    {
        public uint Id;                 // FNV-1a of the root's hierarchy path
        public string Path;             // for logs and the overlay
        public string Label;            // short, human-readable
        public Transform Root;
        public PlayMakerFSM Controller; // the "Coin Machine Canvas CNTLR"

        /// <summary>Every FSM under this machine, by NetId. The mirror addresses these.</summary>
        public readonly Dictionary<uint, PlayMakerFSM> Fsms = new Dictionary<uint, PlayMakerFSM>();

        public ulong Owner;             // SteamID, 0 = free
        public string OwnerName;
        public bool OwnedByMe;

        /// <summary>
        /// The local player is physically inside this thing right now. Tracked
        /// separately from network ownership, because freezing a cabinet someone
        /// is standing at traps them in it.
        /// </summary>
        public bool LocallyOccupied;

        /// <summary>
        /// Does this controller actually have a "Machine Is Frozen" state? Only 14
        /// of the 72 do. Sending FREEZE MACHINE to the rest does nothing useful and
        /// risks parking them somewhere unexpected.
        /// </summary>
        public bool SupportsFreeze;

        public bool Frozen;

        /// <summary>
        /// Does this thing travel? Worked out by watching it, not from a list of
        /// names — see MoverTrack. A mover has its world pose streamed by
        /// whoever is driving, and can be ridden in.
        /// </summary>
        public bool IsMover;

        /// <summary>
        /// Who is aboard, by seat. Index 0 is the driver and mirrors Owner;
        /// 1 and up are passengers, who never own anything. Empty for the 60-odd
        /// cabinets nobody can ride.
        /// </summary>
        public readonly List<ulong> Seats = new List<ulong>();

        /// <summary>
        /// The thing that actually travels, which is NOT always Root.
        ///
        /// A machine is rooted wherever the game's card-reader FSM lives, and on
        /// a vehicle that turns out to be a sub-object:
        /// PLAYER_Vehicles/GOLF CART/GOLFCART_Vehicle/CONTROLLERS — the steering
        /// controls, five centimetres across, two levels below the cart. Sending
        /// that object's position to everyone else moved a tiny invisible cube
        /// around the island while the cart it belongs to stayed parked.
        ///
        /// Worked out by walking up from Root while each parent is still moving
        /// with the player, so it needs no names and finds the right object on
        /// anything you can climb into.
        /// </summary>
        public Transform Body;

        /// <summary>
        /// How many parents above Root the body sits.
        ///
        /// Sent on the wire, because only the driver can work the body out —
        /// they are the one who can see what moves with them. A watcher has no
        /// motion to observe, so without this they resolve the body to Root and
        /// dutifully move the cart's 5cm control panel instead of the cart.
        /// Everyone has the same hierarchy, so "go up N parents" lands on the
        /// same object for one byte.
        /// </summary>
        public int BodyUp;

        /// <summary>Root, unless we've found the larger thing it is bolted to.</summary>
        public Transform Moving { get { return Body != null ? Body : Root; } }

        /// <summary>Half-extents of the thing, for working out where seats are.</summary>
        public Vector3 Extents = new Vector3(0.9f, 0.8f, 1.8f);
        public bool MeasuredExtents;

        public int Capacity { get { return Seating.Capacity(Extents); } }

        /// <summary>
        /// Moving is not the same as rideable. A bus's card reader travels
        /// because the bus does; the player's own controllers travel because the
        /// player does. Neither is something you can sit in.
        /// </summary>
        public bool Rideable
        {
            get { return IsMover && MeasuredExtents && Seating.FitsAPerson(Extents); }
        }

        /// <summary>
        /// Where the driver actually sits, in this thing's own space, as
        /// reported by whoever is driving. Passengers are placed relative to it
        /// rather than to a guess from the bounding box.
        /// </summary>
        public Vector3 DriverLocal;
        public bool HasDriverLocal;

        /// <summary>Seat the local player is in, or -1.</summary>
        public int MySeat = -1;
    }

    /// <summary>
    /// Finds the game's own occupancy controllers and treats each one as a machine.
    ///
    /// M0's central finding: "Coin Machine Canvas CNTLR" is a single FSM type with
    /// 72 instances, always sitting at &lt;thing&gt;/POWER CNTRLR, covering every
    /// arcade cabinet, carnival ride and game, food booth, vending machine, lotto
    /// menu, the home claw machine and all four vehicles. Its states are already a
    /// session lifecycle — Card Inserted, Ready To Play, Card Removed, Machine Is
    /// Frozen — so occupancy is something the game models for us.
    ///
    /// Adapters key on that FSM name plus hierarchy path, never on the owning
    /// object's FSM name: 2,356 graphs in this game are called just "FSM".
    /// </summary>
    internal sealed class MachineRegistry
    {
        public const string ControllerFsmName = "Coin Machine Canvas CNTLR";

        private readonly Dictionary<uint, Machine> _byId = new Dictionary<uint, Machine>();
        private readonly Dictionary<int, Machine> _byFsmInstance = new Dictionary<int, Machine>();
        private readonly Dictionary<int, uint> _fsmIdByInstance = new Dictionary<int, uint>();
        private readonly Dictionary<Transform, Machine> _byRoot = new Dictionary<Transform, Machine>();
        private readonly List<PlayMakerFSM> _all = new List<PlayMakerFSM>(6000);

        public int Count { get { return _byId.Count; } }
        public IEnumerable<Machine> All { get { return _byId.Values; } }

        public bool TryGet(uint id, out Machine m) { return _byId.TryGetValue(id, out m); }

        /// <summary>Which machine does this FSM belong to, if any? Hot path — dictionary only.</summary>
        public Machine Lookup(PlayMakerFSM fsm)
        {
            if (fsm == null) return null;
            Machine m;
            return _byFsmInstance.TryGetValue(fsm.GetInstanceID(), out m) ? m : null;
        }

        /// <summary>The NetId this FSM was filed under, without rebuilding its path.</summary>
        public uint IdOf(PlayMakerFSM fsm)
        {
            uint id;
            return _fsmIdByInstance.TryGetValue(fsm.GetInstanceID(), out id) ? id : 0u;
        }

        public void Clear()
        {
            _byId.Clear();
            _byFsmInstance.Clear();
            _fsmIdByInstance.Clear();
            _byRoot.Clear();
        }

        /// <summary>
        /// What we learned about a machine that a rescan must not throw away.
        ///
        /// Rebuild replaces every Machine object, and the registry rebuilds
        /// whenever a district comes online — which is constantly while driving
        /// around. Without this, the body we worked out (and the fact that the
        /// thing travels at all) is lost every few seconds and has to be
        /// rediscovered, and in the gap the vehicle's pose goes to its control
        /// panel again.
        /// </summary>
        private struct Learned
        {
            public Transform Body;
            public int BodyUp;
            public bool IsMover;

            /// <summary>
            /// Whether WE put this controller into "Machine Is Frozen".
            ///
            /// Losing this is worse than losing the rest, because it is the
            /// only record that there is something to undo. A rebuild handed
            /// back a Machine with Frozen false while the graph was still sat
            /// in the frozen state, so Unfreeze returned early, the release
            /// never went out, and the machine stayed dead for the rest of the
            /// session. That is how a stuck player became a permanently stuck
            /// player.
            /// </summary>
            public bool Frozen;
        }

        private readonly Dictionary<uint, Learned> _learned = new Dictionary<uint, Learned>();

        public int Rebuild()
        {
            // Remember before the old objects go.
            foreach (var kv in _byId)
            {
                var m = kv.Value;
                if (m.Body == null && !m.IsMover && !m.Frozen) continue;
                _learned[kv.Key] = new Learned
                {
                    Body = m.Body,
                    BodyUp = m.BodyUp,
                    IsMover = m.IsMover,
                    Frozen = m.Frozen,
                };
            }

            Clear();

            // Reused across rebuilds rather than reallocated. In the arcade this
            // list holds ~4,500 entries, and a fresh one each time is 4,500
            // references of pure garbage handed to the collector for nothing.
            _all.Clear();
            try { _all.AddRange(PlayMakerFSM.FsmList); }
            catch (Exception ex) { Plugin.Warn("Could not read FsmList: " + ex.Message); return 0; }
            var all = _all;

            // Pass 1: every controller becomes a machine, rooted at its parent.
            foreach (var fsm in all)
            {
                if (fsm == null) continue;
                string name;
                try { name = fsm.FsmName; } catch { continue; }
                if (name != ControllerFsmName) continue;

                var go = fsm.gameObject;
                if (go == null) continue;

                // The controller lives at <machine>/POWER CNTRLR; a few sit directly
                // on the machine object, so fall back to the controller's own object.
                var root = go.transform.parent != null ? go.transform.parent : go.transform;

                var path = NetId.Path(root);
                var id = NetId.Hash(path);
                if (_byId.ContainsKey(id)) continue;

                var machine = new Machine
                {
                    Id = id,
                    Path = path,
                    Label = ShortLabel(path),
                    Root = root,
                    Controller = fsm,
                    SupportsFreeze = HasFrozenState(fsm),
                };
                // Put back what we already knew about this one.
                Learned known;
                if (_learned.TryGetValue(id, out known))
                {
                    machine.IsMover = known.IsMover;
                    machine.Frozen = known.Frozen;
                    if (known.Body != null)
                    {
                        machine.Body = known.Body;
                        machine.BodyUp = known.BodyUp;
                    }
                }

                _byId[id] = machine;
                _byRoot[root] = machine;
            }

            // Pass 2: file every FSM under a machine root against that machine.
            foreach (var fsm in all)
            {
                if (fsm == null) continue;
                GameObject go;
                try { go = fsm.gameObject; } catch { continue; }
                if (go == null) continue;

                var m = FindOwningMachine(go.transform);
                if (m == null) continue;

                var fid = FsmId(fsm);
                if (!m.Fsms.ContainsKey(fid)) m.Fsms[fid] = fsm;
                _byFsmInstance[fsm.GetInstanceID()] = m;
                _fsmIdByInstance[fsm.GetInstanceID()] = fid;
            }

            Plugin.Log("Machine registry: " + _byId.Count + " interactables, "
                       + _byFsmInstance.Count + " FSMs mapped.");

            if (_byId.Count > 0)
                CompatCheck.Set("machine controllers (" + ControllerFsmName + ")", true,
                                _byId.Count + " found");
            return _byId.Count;
        }

        /// <summary>
        /// Walks up to the nearest machine root. Machines never nest, so first hit
        /// wins. Compares Transform references rather than rebuilding a path string
        /// per ancestor — this runs for every FSM in the scene, and there are 4,533
        /// of them in the arcade with paths ten levels deep.
        /// </summary>
        private Machine FindOwningMachine(Transform t)
        {
            var cur = t;
            int guard = 0;
            while (cur != null && guard++ < 64)
            {
                Machine m;
                if (_byRoot.TryGetValue(cur, out m)) return m;
                cur = cur.parent;
            }
            return null;
        }

        private static bool HasFrozenState(PlayMakerFSM fsm)
        {
            try
            {
                var states = fsm.FsmStates;
                if (states == null) return false;
                foreach (var st in states)
                    if (st != null && st.Name == "Machine Is Frozen") return true;
            }
            catch { }
            return false;
        }

        /// <summary>Gives the FSM the same NetId the registry filed it under.</summary>
        public static uint FsmId(PlayMakerFSM fsm)
        {
            return NetId.Hash(NetId.Path(fsm.transform) + "|" + SafeName(fsm));
        }

        private static string SafeName(PlayMakerFSM fsm)
        {
            try { return fsm.FsmName ?? ""; } catch { return ""; }
        }

        /// <summary>
        /// "LARRYS ARCADE_INSIDE/MACHINES/Dunko 02/Machine_1" -> "Dunko 02 (Machine_1)".
        /// </summary>
        private static string ShortLabel(string path)
        {
            var parts = path.Split('/');
            if (parts.Length == 0) return path;

            var last = parts[parts.Length - 1];
            if (parts.Length >= 2 && last.StartsWith("Machine", StringComparison.OrdinalIgnoreCase))
                return parts[parts.Length - 2] + " (" + last + ")";
            return last;
        }
    }
}
