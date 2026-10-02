using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
    /// <summary>
    /// What a part looks like beyond where it is: whether its renderer or UI
    /// graphic is switched on, and a UI graphic's colour. Stackem Up's board is
    /// a canvas of images lit and unlit by the game; none of them move.
    /// Reached by name and reflection — the UI module isn't referenced.
    /// </summary>
    internal static class Visual
    {
        private static readonly Dictionary<Type, PropertyInfo> _color = new Dictionary<Type, PropertyInfo>();

        public static bool HasCanvas(Transform t)
        {
            var cs = t.GetComponents<Component>();
            for (int i = 0; i < cs.Length; i++) if (cs[i] != null && cs[i].GetType().Name == "CanvasRenderer") return true;
            return false;
        }

        /// <summary>The UI graphic on this object, if any (Image, RawImage, Text, TextMeshProUGUI).</summary>
        public static Behaviour Graphic(Transform t)
        {
            var bs = t.GetComponents<Behaviour>();
            for (int i = 0; i < bs.Length; i++)
            {
                var b = bs[i];
                if (b == null) continue;
                var n = b.GetType().Name;
                if (n == "Image" || n == "RawImage" || n == "Text" || n == "TextMeshProUGUI") return b;
            }
            return null;
        }

        public static bool Shown(Transform t)
        {
            var r = t.GetComponent<Renderer>();
            if (r != null && !r.enabled) return false;
            var g = Graphic(t);
            if (g != null && !g.enabled) return false;
            return true;
        }

        public static void Show(Transform t, bool on, Dictionary<Component, bool> was)
        {
            var r = t.GetComponent<Renderer>();
            if (r != null && r.enabled != on) { if (!was.ContainsKey(r)) was[r] = r.enabled; r.enabled = on; }
            var g = Graphic(t);
            if (g != null && g.enabled != on) { if (!was.ContainsKey(g)) was[g] = g.enabled; g.enabled = on; }
        }

        public static void RestoreShown(Dictionary<Component, bool> was)
        {
            foreach (var kv in was)
            {
                var r = kv.Key as Renderer;
                if (r != null) { r.enabled = kv.Value; continue; }
                var b = kv.Key as Behaviour;
                if (b != null) b.enabled = kv.Value;
            }
            was.Clear();
        }

        private static PropertyInfo ColorProp(Behaviour g)
        {
            PropertyInfo p;
            var ty = g.GetType();
            if (!_color.TryGetValue(ty, out p))
            {
                p = ty.GetProperty("color", BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.PropertyType != typeof(Color)) p = null;
                _color[ty] = p;
            }
            return p;
        }

        public static bool TryColor(Transform t, out Color c)
        {
            c = Color.white;
            var g = Graphic(t);
            if (g == null) return false;
            var p = ColorProp(g);
            if (p == null) return false;
            try { c = (Color)p.GetValue(g, null); return true; } catch { return false; }
        }

        public static void SetColor(Transform t, Color c, Dictionary<Behaviour, Color> was)
        {
            var g = Graphic(t);
            if (g == null) return;
            var p = ColorProp(g);
            if (p == null) return;
            try
            {
                if (!was.ContainsKey(g)) was[g] = (Color)p.GetValue(g, null);
                p.SetValue(g, c, null);
            }
            catch { }
        }

        public static void RestoreColors(Dictionary<Behaviour, Color> was)
        {
            foreach (var kv in was)
            {
                if (kv.Key == null) continue;
                var p = ColorProp(kv.Key);
                if (p != null) { try { p.SetValue(kv.Key, kv.Value, null); } catch { } }
            }
            was.Clear();
        }

        public static bool Same(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) < 0.004f && Mathf.Abs(a.g - b.g) < 0.004f
                && Mathf.Abs(a.b - b.b) < 0.004f && Mathf.Abs(a.a - b.a) < 0.004f;
        }
    }

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
            public bool Vis, SentVis;
            public Color Col, SentCol;
            public bool HasCol;
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
                bool vis = Visual.Shown(p.T);
                Color col = p.Col;
                bool colChanged = p.HasCol && Visual.TryColor(p.T, out col) && !Visual.Same(col, p.Col);
                if ((lp - p.Pos).sqrMagnitude > 0.001f * 0.001f || Quaternion.Angle(lr, p.Rot) > 0.5f || on != p.On
                    || vis != p.Vis || colChanged)
                {
                    p.Vis = vis;
                    p.Col = col;
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
            bool draws = t.GetComponent<Renderer>() != null || Visual.HasCanvas(t);
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
            {
                var np = new Part { T = t, Key = key, Pos = t.localPosition, Rot = t.localRotation, On = t.gameObject.activeSelf };
                np.Vis = Visual.Shown(t);
                np.HasCol = Visual.TryColor(t, out np.Col);
                _parts.Add(np);
            }
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
                    || p.T.gameObject.activeSelf != p.SentOn
                    || p.Vis != p.SentVis || (p.HasCol && !Visual.Same(p.Col, p.SentCol)))
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
                p.SentVis = p.Vis;
                p.SentCol = p.Col;
                w.Write(p.Id);
                w.Write((byte)((define ? 1 : 0) | (p.T.gameObject.activeSelf ? 2 : 0) | (p.Vis ? 4 : 0) | (p.HasCol ? 8 : 0)));
                if (define) WriteStr(w, p.Key);
                w.Write(p.SentPos.x); w.Write(p.SentPos.y); w.Write(p.SentPos.z);
                var q = p.SentRot;
                w.Write(Q(q.x)); w.Write(Q(q.y)); w.Write(Q(q.z)); w.Write(Q(q.w));
                if (p.HasCol)
                {
                    w.Write((byte)Mathf.RoundToInt(Mathf.Clamp01(p.Col.r) * 255f));
                    w.Write((byte)Mathf.RoundToInt(Mathf.Clamp01(p.Col.g) * 255f));
                    w.Write((byte)Mathf.RoundToInt(Mathf.Clamp01(p.Col.b) * 255f));
                    w.Write((byte)Mathf.RoundToInt(Mathf.Clamp01(p.Col.a) * 255f));
                }
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
