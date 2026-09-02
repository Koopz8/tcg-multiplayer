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

        public int Rebuild()
        {
            Clear();

            List<PlayMakerFSM> all;
            try { all = new List<PlayMakerFSM>(PlayMakerFSM.FsmList); }
            catch (Exception ex) { Plugin.Warn("Could not read FsmList: " + ex.Message); return 0; }

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
                };
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
