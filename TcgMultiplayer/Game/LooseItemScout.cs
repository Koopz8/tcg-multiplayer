using System;
using System.Collections.Generic;
using UnityEngine;

namespace TcgMultiplayer.Game
{
    /// <summary>
    /// Diagnostic, owner side. What loose things appear in the world while a
    /// player plays — tickets out of a dispenser, a prize out of a chute — and
    /// what happens to them when they're picked up.
    ///
    /// Most machines pay out in tickets that land on the floor and get picked
    /// up by hand, and none of that is under a machine's root, so the walk
    /// never sees it. This scans for rigidbodies that are new since the last
    /// scan and live outside every machine and outside PLAYER, then follows
    /// each one for a while and says when something about it changes. What
    /// it prints is what an item sync has to reproduce.
    /// </summary>
    internal sealed class LooseItemScout
    {
        private const float ScanEvery = 2f;
        private const int LogCap = 60;
        private const float FollowFor = 60f;

        private sealed class Seen
        {
            public Rigidbody Body;
            public string Name;
            public string LastSaid;
            public float StopAt;
        }

        private readonly HashSet<int> _known = new HashSet<int>();
        private readonly List<Seen> _following = new List<Seen>();
        private float _nextScanAt;
        private bool _primed;
        private int _logs;

        /// <summary>A new scene is a new world; start the baseline over.</summary>
        public void Reset()
        {
            _known.Clear();
            _following.Clear();
            _primed = false;
        }

        public void Tick(IEnumerable<Machine> machines, PlayerRig rig)
        {
            // Not until the player is standing in the arcade. The title
            // screen's character picker is full of loose props — trash bags,
            // robo minions — and the first version spent its whole log budget
            // on them before a single ticket came out of a machine.
            if (rig == null || !rig.Valid) return;

            float now = Time.time;
            if (now < _nextScanAt) return;
            _nextScanAt = now + ScanEvery;

            Rigidbody[] all;
            try { all = UnityEngine.Object.FindObjectsOfType<Rigidbody>(); }
            catch { return; }

            var roots = new List<Transform>();
            foreach (var m in machines) if (m != null && m.Root != null) roots.Add(m.Root);
            Transform player = rig != null && rig.Valid ? rig.Root : null;

            for (int i = 0; i < all.Length; i++)
            {
                var rb = all[i];
                if (rb == null) continue;
                int id = rb.GetInstanceID();
                if (_known.Contains(id)) continue;
                _known.Add(id);
                if (!_primed) continue;                     // the first scan is the baseline

                var t = rb.transform;
                bool underMachine = false;
                for (int r = 0; r < roots.Count; r++) if (t.IsChildOf(roots[r])) { underMachine = true; break; }
                if (underMachine) continue;
                if (player != null && t.IsChildOf(player)) continue;
                if (t.root != null && t.root.name.StartsWith("PLAYER Picker", StringComparison.Ordinal)) continue;

                if (_logs < LogCap)
                {
                    _logs++;
                    var s = new Seen { Body = rb, Name = rb.name, StopAt = now + FollowFor };
                    _following.Add(s);
                    s.LastSaid = Shape(rb);
                    Plugin.Log("New loose body: " + rb.name + " -> " + Describe(rb, player));
                }
            }
            _primed = true;

            for (int i = _following.Count - 1; i >= 0; i--)
            {
                var s = _following[i];
                if (now >= s.StopAt) { _following.RemoveAt(i); continue; }
                if (s.Body == null)
                {
                    Plugin.Log("Loose body " + s.Name + ": destroyed.");
                    _following.RemoveAt(i);
                    continue;
                }
                var shape = Shape(s.Body);
                if (shape == s.LastSaid) continue;
                s.LastSaid = shape;
                Plugin.Log("Loose body " + s.Name + ": " + Describe(s.Body, player));
            }
        }

        private static string Shape(Rigidbody rb)
        {
            var d = Describe(rb, null);
            int at = d.LastIndexOf(", at ", StringComparison.Ordinal);
            return at > 0 ? d.Substring(0, at) : d;
        }

        private static string Describe(Rigidbody rb, Transform player)
        {
            if (rb == null) return "destroyed";
            var go = rb.gameObject;
            string where;
            try { where = NetId.Path(rb.transform); } catch { where = "?"; }
            int colsOn = 0;
            var cols = rb.GetComponentsInChildren<Collider>(true);
            for (int c = 0; c < cols.Length; c++) if (cols[c] != null && cols[c].enabled) colsOn++;
            string held = player != null && rb.transform.IsChildOf(player) ? ", UNDER PLAYER" : "";
            var fsms = go.GetComponents<PlayMakerFSM>();
            string fsm = fsms.Length > 0 ? ", fsm \"" + fsms[0].FsmName + "\"" : ", no fsm";
            return where + held + (go.activeInHierarchy ? "" : " (off)") + ", layer " + LayerMask.LayerToName(go.layer)
                   + ", tag " + go.tag + fsm + ", kinematic=" + rb.isKinematic + ", sleeping=" + rb.IsSleeping()
                   + ", " + colsOn + "/" + cols.Length + " colliders on, at " + rb.transform.position.ToString("F1");
        }
    }
}
