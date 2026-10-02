using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace TcgMultiplayer.Game
{
    /// <summary>
    /// Parts of a machine that move but are neither rigidbodies (the physics
    /// stream) nor text or its parents (the screen mirror): claw and key-game
    /// carriages, Speed Drop's turntable, Cuckoo's clock arms, the skee-ball
    /// throw arm, the ticket track. 0.16.13 only listed them; the sweep found
    /// one on almost every cabinet, and on the claw and Prizemaster it was the
    /// arm itself — the whole point of watching.
    ///
    /// The owner samples every drawn, non-body part under the machine root on
    /// the screen mirror's clock. Anything whose LOCAL pose changes is a mover
    /// from then on, and rides in the screen packet (reliable, ordered) as a
    /// number plus a pose; the path goes once per keyframe. Local poses, so a
    /// part moved only by its parent costs nothing.
    /// </summary>
    internal sealed class UnsentMovers
    {
        private sealed class Part
        {
            public Transform T;
            public string Key;
            public Vector3 Pos;
            public Quaternion Rot;
            public int Moves;
            public bool Live;
            public bool On;
            public ushort Id;
            public bool Defined;
            public Vector3 SentPos;
            public Quaternion SentRot;
            public bool SentOn;
        }

        private const int MaxParts = 3000;
        private const int MaxLive = 400;

        private readonly List<Part> _parts = new List<Part>(256);
        private readonly List<Part> _live = new List<Part>(64);
        private uint _machine;
        private string _label;
        private Transform _root;
        private readonly HashSet<Transform> _known = new HashSet<Transform>();
        private float _rescanAt;
        private Func<Transform, bool> _sentByScreen;
        /// <summary>A round spawns things (Speed Drop's balls); walk again now and then to pick them up.</summary>
        private const float RescanEvery = 2f;

        /// <summary>Called on the screen mirror's clock while we own a machine.</summary>
        public void Sample(Machine m, Func<Transform, bool> sentByScreen)
        {
            if (m == null || m.Root == null) return;
            if (m.Id != _machine) { End(); Begin(m, sentByScreen); }
            _sentByScreen = sentByScreen;
            if (Time.time >= _rescanAt)
            {
                _rescanAt = Time.time + RescanEvery;
                Walk(_root, _root, "", sentByScreen);
            }

            for (int i = 0; i < _parts.Count; i++)
            {
                var p = _parts[i];
                if (p.T == null) continue;
                var lp = p.T.localPosition;
                var lr = p.T.localRotation;
                bool on = p.T.gameObject.activeSelf;
                if ((lp - p.Pos).sqrMagnitude > 0.001f * 0.001f || Quaternion.Angle(lr, p.Rot) > 0.5f || on != p.On)
                {
                    p.Pos = lp;
                    p.Rot = lr;
                    p.On = on;
                    // A part that has left the machine (the player's own club
                    // card goes back to their hand) is not the machine's.
                    if (!p.T.IsChildOf(_root)) continue;
                    p.Moves++;
                    if (!p.Live && _live.Count < MaxLive)
                    {
                        p.Live = true;
                        p.Id = (ushort)_live.Count;
                        _live.Add(p);
                    }
                }
            }
        }

        private void Begin(Machine m, Func<Transform, bool> sentByScreen)
        {
            _parts.Clear();
            _live.Clear();
            _machine = m.Id;
            _label = m.Label;
            _root = m.Play;
            _known.Clear();
            Walk(_root, _root, "", sentByScreen);
            _rescanAt = Time.time + RescanEvery;
        }

        private bool Walk(Transform t, Transform root, string key, Func<Transform, bool> sentByScreen)
        {
            if (t.GetComponent<Rigidbody>() != null) return false;     // streamed, with everything on it
            if (PhysicsReplicator.IsPlayersHands(t.name) || t.name.IndexOf("PlayersClubCard", StringComparison.OrdinalIgnoreCase) >= 0
                || t.name.StartsWith("TCGMP_", StringComparison.Ordinal)) return false;
            bool draws = t.GetComponent<Renderer>() != null;
            Dictionary<string, int> seen = t.childCount > 1 ? new Dictionary<string, int>(t.childCount) : null;
            for (int c = 0; c < t.childCount; c++)
            {
                var ch = t.GetChild(c);
                int n = 0;
                if (seen != null) { seen.TryGetValue(ch.name, out n); seen[ch.name] = n + 1; }
                string ck = (key.Length == 0 ? "" : key + "/") + (n == 0 ? ch.name : ch.name + "#" + n);
                if (Walk(ch, root, ck, sentByScreen)) draws = true;
            }
            if (draws && t != root && _parts.Count < MaxParts && (sentByScreen == null || !sentByScreen(t)) && _known.Add(t))
                _parts.Add(new Part { T = t, Key = key, Pos = t.localPosition, Rot = t.localRotation, On = t.gameObject.activeSelf });
            return draws;
        }

        /// <summary>
        /// The movers that need saying in this packet: every live one on a
        /// keyframe, otherwise those that moved since we last said. Written
        /// after the screen's own fields; returns how many.
        /// </summary>
        public int Write(BinaryWriter w, bool keyframe)
        {
            _send.Clear();
            for (int i = 0; i < _live.Count; i++)
            {
                var p = _live[i];
                if (p.T == null || !p.T.IsChildOf(_root)) continue;
                if (keyframe || !p.Defined
                    || (p.T.localPosition - p.SentPos).sqrMagnitude > 0.0005f * 0.0005f
                    || Quaternion.Angle(p.T.localRotation, p.SentRot) > 0.2f
                    || p.T.gameObject.activeSelf != p.SentOn)
                    _send.Add(p);
            }
            w.Write((ushort)_send.Count);
            for (int i = 0; i < _send.Count; i++)
            {
                var p = _send[i];
                bool define = keyframe || !p.Defined;
                p.Defined = true;
                p.SentPos = p.T.localPosition;
                p.SentRot = p.T.localRotation;
                p.SentOn = p.T.gameObject.activeSelf;
                w.Write(p.Id);
                w.Write((byte)((define ? 1 : 0) | (p.T.gameObject.activeSelf ? 2 : 0)));
                if (define) WriteStr(w, p.Key);
                w.Write(p.SentPos.x); w.Write(p.SentPos.y); w.Write(p.SentPos.z);
                var q = p.SentRot;
                w.Write(Q(q.x)); w.Write(Q(q.y)); w.Write(Q(q.z)); w.Write(Q(q.w));
            }
            Sent += _send.Count;
            return _send.Count;
        }
        private readonly List<Part> _send = new List<Part>(64);
        public int Sent;
        public bool AnyLive { get { return _live.Count > 0; } }

        private static short Q(float v) { return (short)Mathf.Clamp(Mathf.RoundToInt(v * 32767f), -32767, 32767); }
        public static float DeQ(short v) { return v / 32767f; }

        private static void WriteStr(BinaryWriter w, string s)
        {
            var b = System.Text.Encoding.UTF8.GetBytes(s ?? "");
            w.Write((ushort)b.Length);
            w.Write(b);
        }

        /// <summary>Says what moved, once per lease.</summary>
        public void End()
        {
            if (_machine == 0) return;
            var tops = new List<Part>();
            var liveSet = new HashSet<Transform>();
            for (int i = 0; i < _live.Count; i++) if (_live[i].T != null) liveSet.Add(_live[i].T);
            for (int i = 0; i < _live.Count; i++)
            {
                var p = _live[i];
                if (p.T == null || p.Moves < 2) continue;
                bool under = false;
                var a = p.T.parent;
                while (a != null) { if (liveSet.Contains(a)) { under = true; break; } a = a.parent; }
                if (!under) tops.Add(p);
            }
            tops.Sort((x, y) => y.Moves.CompareTo(x.Moves));
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < tops.Count && i < 10; i++)
                sb.Append("  ").Append(tops[i].Key).Append(" (").Append(tops[i].Moves).Append(")");
            Plugin.Log("Movers on " + _label + ": " + _live.Count + " parts that aren't bodies or screen moved and were sent"
                       + (_live.Count >= MaxLive ? " (hit the cap of " + MaxLive + ")" : "") + "." + sb);
            Count += _live.Count;
            _parts.Clear();
            _live.Clear();
            _known.Clear();
            _machine = 0;
        }

        public int Count;
    }
}
