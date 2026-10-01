using System;
using System.Collections.Generic;
using UnityEngine;

namespace TcgMultiplayer.Game
{
    /// <summary>
    /// On the owner's side: which parts of the machine you're playing MOVED
    /// during your round without being sent to anyone?
    ///
    /// Only rigidbodies go over the physics stream, and only text (and the
    /// parents of text) over the screen mirror. Treasure's shovel carriage was
    /// neither until the countdown happened to ride on it, and for every
    /// cabinet nobody has watched yet there could be an arm, a wheel or a
    /// flipper doing the same: moving for the player, parked for the watcher.
    /// This finds them from the player's side, with no names and no second
    /// person — one round on each cabinet and the log says which ones need
    /// streaming.
    /// </summary>
    internal sealed class UnsentMovers
    {
        private sealed class Part
        {
            public Transform T;
            public Vector3 Pos;
            public Quaternion Rot;
            public int Moves;
        }

        private const float SampleEvery = 0.25f;
        private const int MaxParts = 3000;

        private readonly List<Part> _parts = new List<Part>(256);
        private uint _machine;
        private string _label;
        private float _nextAt;

        public void Tick(Machine m, Func<Transform, bool> sentByScreen)
        {
            if (m == null || m.Root == null) return;
            if (m.Id != _machine) { End(); Begin(m, sentByScreen); }
            float now = Time.time;
            if (now < _nextAt) return;
            _nextAt = now + SampleEvery;

            for (int i = 0; i < _parts.Count; i++)
            {
                var p = _parts[i];
                if (p.T == null) continue;
                var lp = p.T.localPosition;
                var lr = p.T.localRotation;
                if ((lp - p.Pos).sqrMagnitude > 0.001f * 0.001f || Quaternion.Angle(lr, p.Rot) > 0.5f)
                {
                    p.Moves++;
                    p.Pos = lp;
                    p.Rot = lr;
                }
            }
        }

        private void Begin(Machine m, Func<Transform, bool> sentByScreen)
        {
            _parts.Clear();
            _machine = m.Id;
            _label = m.Label;
            Walk(m.Root, m.Root, sentByScreen);
        }

        /// <summary>
        /// Everything that draws, or has something drawn under it, and isn't a
        /// rigidbody or riding on one. Returns whether anything at or below
        /// draws.
        /// </summary>
        private bool Walk(Transform t, Transform root, Func<Transform, bool> sentByScreen)
        {
            if (t.GetComponent<Rigidbody>() != null) return false;     // streamed, with everything on it
            bool draws = t.GetComponent<Renderer>() != null;
            for (int c = 0; c < t.childCount; c++)
                if (Walk(t.GetChild(c), root, sentByScreen)) draws = true;
            if (draws && t != root && _parts.Count < MaxParts && (sentByScreen == null || !sentByScreen(t)))
                _parts.Add(new Part { T = t, Pos = t.localPosition, Rot = t.localRotation });
            return draws;
        }

        /// <summary>Says what moved and wasn't sent, once per round.</summary>
        public void End()
        {
            if (_machine == 0) return;
            var moved = new List<Part>();
            for (int i = 0; i < _parts.Count; i++)
                if (_parts[i].T != null && _parts[i].Moves >= 2) moved.Add(_parts[i]);

            // Only the top of a moving chain: a carriage and the three things
            // bolted to it are one part.
            var tops = new List<Part>();
            var movedSet = new HashSet<Transform>();
            for (int i = 0; i < moved.Count; i++) movedSet.Add(moved[i].T);
            for (int i = 0; i < moved.Count; i++)
            {
                bool underMoved = false;
                var a = moved[i].T.parent;
                while (a != null) { if (movedSet.Contains(a)) { underMoved = true; break; } a = a.parent; }
                if (!underMoved) tops.Add(moved[i]);
            }
            tops.Sort((x, y) => y.Moves.CompareTo(x.Moves));

            if (tops.Count == 0)
                Plugin.Log("Unsent movers on " + _label + ": none - of " + _parts.Count
                           + " drawn parts that aren't bodies or screen, nothing moved during your round.");
            else
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < tops.Count && i < 12; i++)
                    sb.Append("  ").Append(NetId.Path(tops[i].T)).Append(" (moved in ").Append(tops[i].Moves).Append(" samples)");
                Plugin.Log("Unsent movers on " + _label + ": " + tops.Count + " parts moved for you and NOT for anyone watching:" + sb);
            }
            Count += tops.Count;
            _parts.Clear();
            _machine = 0;
        }

        public int Count;
    }
}
