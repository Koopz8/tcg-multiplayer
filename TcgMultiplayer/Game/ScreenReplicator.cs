using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace TcgMultiplayer.Game
{
    /// <summary>
    /// The cabinet's screen: the score, the timer, the tickets, the "insert
    /// card" prompt — every piece of text under the machine root.
    ///
    /// Events into a spectated machine are muted (the round logic and the
    /// display live in the same FSMs, and replaying all of it made the watcher
    /// play its own round), and letting the two display-looking FSMs through
    /// changed nothing on the screen: the numbers are written by the coins and
    /// the joystick controller, which have to stay muted. So this stops
    /// caring who writes the text and mirrors the text.
    ///
    /// The owner walks the machine for text components — UI Text, TextMesh,
    /// TextMeshPro, anything with a string "text" property, found by
    /// reflection so no extra assembly is referenced — and sends the ones
    /// that changed, keyed by path under the root, plus whether the object is
    /// switched on (menus come and go by SetActive). Everything is sent as a
    /// keyframe every few seconds for a watcher who walked up late. The
    /// watcher writes the same strings into its own components by the same
    /// path and puts every one of them back on release.
    /// </summary>
    internal sealed class ScreenReplicator
    {
        private const float PollEvery = 0.2f;
        private const float KeyframeEvery = 5f;
        private const byte FlagKeyframe = 1;

        private sealed class Field
        {
            public string Key;
            public Component Comp;
            public PropertyInfo Text;
            public string LastSent;
            public bool LastActive;
            /// <summary>The text's own transform, then its parents up to the machine root.</summary>
            public Transform[] Chain;
            public Vector3[] LastPos;
            public bool[] LastOn;
        }

        private const int MaxChain = 6;

        private static Transform[] ChainOf(Transform t, Transform root)
        {
            var list = new List<Transform>(MaxChain);
            while (t != null && t != root && list.Count < MaxChain) { list.Add(t); t = t.parent; }
            return list.ToArray();
        }

        // ---------------------------------------------------------- owner side
        private readonly List<Field> _mine = new List<Field>(64);
        private uint _mineMachine;
        private float _nextPollAt, _nextKeyframeAt, _nextRescanAt;
        private bool _described;

        // -------------------------------------------------------- watcher side
        private sealed class Watched
        {
            public readonly Dictionary<string, Field> ByKey = new Dictionary<string, Field>();
            public readonly Dictionary<Field, string> OriginalText = new Dictionary<Field, string>();
            public readonly Dictionary<GameObject, bool> OriginalActive = new Dictionary<GameObject, bool>();
            public readonly Dictionary<Transform, Vector3> OriginalPos = new Dictionary<Transform, Vector3>();
            /// <summary>Where we last put each object, to notice something on our side moving it back.</summary>
            public readonly Dictionary<Transform, Vector3> Placed = new Dictionary<Transform, Vector3>();
            public int OverrideLogs;
            public float RescanAt;
            public bool Described;
        }
        private readonly Dictionary<uint, Watched> _watched = new Dictionary<uint, Watched>();

        public int FieldsFound, Sent, Applied, Unmatched, Moves, MovesOverridden;
        private int _moveLogs;
        private const int MoveLogCap = 12;

        // ---------------------------------------------------------- discovery

        private static readonly Dictionary<Type, PropertyInfo> _textProp = new Dictionary<Type, PropertyInfo>();

        private static PropertyInfo TextPropertyOf(Type t)
        {
            PropertyInfo pi;
            if (_textProp.TryGetValue(t, out pi)) return pi;
            pi = null;
            try
            {
                // Text, TextMesh, TextMeshPro and TextMeshProUGUI all expose
                // "text" as a read/write string. Plain Transform, renderers
                // and the like don't, and are skipped.
                var p = t.GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.PropertyType == typeof(string) && p.CanRead && p.CanWrite
                    && (t.Name.IndexOf("Text", StringComparison.Ordinal) >= 0))
                    pi = p;
            }
            catch { pi = null; }
            _textProp[t] = pi;
            return pi;
        }

        private static void Gather(Transform root, List<Field> into)
        {
            into.Clear();
            if (root == null) return;
            WalkChildren(root, root, "", into);
        }

        private static void WalkChildren(Transform root, Transform parent, string prefix, List<Field> into)
        {
            Dictionary<string, int> seen = parent.childCount > 1 ? new Dictionary<string, int>(parent.childCount) : null;
            for (int i = 0; i < parent.childCount; i++)
            {
                var t = parent.GetChild(i);
                int n = 0;
                if (seen != null) { seen.TryGetValue(t.name, out n); seen[t.name] = n + 1; }
                string key = n == 0 ? prefix + t.name : prefix + t.name + "#" + n;

                var comps = t.GetComponents<Component>();
                for (int c = 0; c < comps.Length; c++)
                {
                    var comp = comps[c];
                    if (comp == null) continue;
                    var pi = TextPropertyOf(comp.GetType());
                    if (pi == null) continue;
                    into.Add(new Field { Key = key + ":" + comp.GetType().Name, Comp = comp, Text = pi, Chain = ChainOf(t, root) });
                }
                if (t.childCount > 0) WalkChildren(root, t, key + "/", into);
            }
        }

        private static string ReadText(Field f)
        {
            try { return f.Text.GetValue(f.Comp, null) as string ?? ""; }
            catch { return ""; }
        }

        private static void WriteText(Field f, string s)
        {
            try { f.Text.SetValue(f.Comp, s, null); } catch { }
        }

        // -------------------------------------------------------------- owner

        /// <summary>Called every frame while we own a machine; returns a packet when there is something to say.</summary>
        public byte[] Poll(Machine m)
        {
            if (m == null || m.Root == null) return null;
            float now = Time.time;
            if (now < _nextPollAt) return null;
            _nextPollAt = now + PollEvery;

            bool fresh = _mineMachine != m.Id;
            if (fresh || now >= _nextRescanAt)
            {
                // Rescanned with the keyframe: a menu that is instantiated
                // on card insert isn't there to find before it.
                _mineMachine = m.Id;
                _nextRescanAt = now + KeyframeEvery;
                var before = _mine.Count;
                Gather(m.Root, _mine);
                FieldsFound = _mine.Count;
                if (!_described || _mine.Count != before)
                {
                    _described = true;
                    var sb = new StringBuilder();
                    sb.Append("Screen of ").Append(m.Label).Append(": ").Append(_mine.Count).Append(" text fields. ");
                    for (int i = 0; i < _mine.Count && i < 8; i++)
                    {
                        var txt = ReadText(_mine[i]);
                        if (txt.Length > 24) txt = txt.Substring(0, 24) + "…";
                        sb.Append(_mine[i].Key).Append("=\"").Append(txt.Replace("\n", "\\n")).Append("\" ");
                    }
                    Plugin.Log(sb.ToString());
                }
                fresh = true;
            }
            if (_mine.Count == 0) return null;

            bool keyframe = fresh || now >= _nextKeyframeAt;
            if (keyframe) _nextKeyframeAt = now + KeyframeEvery;

            var changed = new List<Field>();
            for (int i = 0; i < _mine.Count; i++)
            {
                var f = _mine[i];
                if (f.Comp == null) continue;
                var txt = ReadText(f);
                bool active = f.Comp.gameObject.activeInHierarchy;

                // The score marker on a pusher is a box that slides up a ladder
                // as the number climbs. Its text mirrored fine and it sat at
                // the bottom, because the slide is the box's transform, not
                // its string. So the text's object and its parents go too.
                bool moved = false;
                if (f.LastPos == null || f.LastPos.Length != f.Chain.Length)
                {
                    f.LastPos = new Vector3[f.Chain.Length];
                    f.LastOn = new bool[f.Chain.Length];
                    moved = true;
                }
                for (int c = 0; c < f.Chain.Length; c++)
                {
                    if (f.Chain[c] == null) continue;
                    // Menus are panels switched on and off; the results screen
                    // that stayed up on the watcher through the second round
                    // was one we had switched on and never off. Each level's
                    // own flag goes, and the watcher sets it exactly.
                    bool on = f.Chain[c].gameObject.activeSelf;
                    if (on != f.LastOn[c]) { f.LastOn[c] = on; moved = true; }
                    var lp = f.Chain[c].localPosition;
                    if ((lp - f.LastPos[c]).sqrMagnitude > 1e-6f)
                    {
                        if (!fresh && _moveLogs < MoveLogCap && f.Comp.gameObject.activeInHierarchy)
                        {
                            _moveLogs++;
                            Plugin.Log("Screen move on " + m.Label + ": " + f.Key + " level " + c + " (" + f.Chain[c].name + ") "
                                       + f.LastPos[c].ToString("F1") + " -> " + lp.ToString("F1"));
                        }
                        f.LastPos[c] = lp; moved = true;
                    }
                }

                if (keyframe || moved || txt != f.LastSent || active != f.LastActive)
                {
                    f.LastSent = txt;
                    f.LastActive = active;
                    changed.Add(f);
                }
            }
            if (changed.Count == 0) return null;

            var buf = new System.IO.MemoryStream(256);
            var w = new System.IO.BinaryWriter(buf, Encoding.UTF8);
            w.Write((byte)(keyframe ? FlagKeyframe : 0));
            w.Write((ushort)changed.Count);
            for (int i = 0; i < changed.Count; i++)
            {
                var f = changed[i];
                WriteStr(w, f.Key);
                w.Write((byte)(f.LastActive ? 1 : 0));
                WriteStr(w, f.LastSent);
                w.Write((byte)f.Chain.Length);
                for (int c = 0; c < f.Chain.Length; c++)
                {
                    var lp = f.LastPos[c];
                    w.Write((byte)(f.LastOn[c] ? 1 : 0));
                    w.Write(lp.x); w.Write(lp.y); w.Write(lp.z);
                }
            }
            Sent += changed.Count;
            return buf.ToArray();
        }

        // ------------------------------------------------------------ watcher

        public void Apply(Machine m, byte[] data)
        {
            if (m == null || m.Root == null || data == null || data.Length < 3) return;
            float now = Time.time;

            Watched w;
            if (!_watched.TryGetValue(m.Id, out w))
            {
                w = new Watched();
                _watched[m.Id] = w;
            }

            var r = new System.IO.BinaryReader(new System.IO.MemoryStream(data), Encoding.UTF8);
            bool keyframe = (r.ReadByte() & FlagKeyframe) != 0;
            int count = r.ReadUInt16();

            if (keyframe || now >= w.RescanAt || w.ByKey.Count == 0)
            {
                w.RescanAt = now + KeyframeEvery;
                var found = new List<Field>();
                Gather(m.Root, found);
                for (int i = 0; i < found.Count; i++)
                    if (!w.ByKey.ContainsKey(found[i].Key)) w.ByKey[found[i].Key] = found[i];
                if (!w.Described)
                {
                    w.Described = true;
                    Plugin.Log("Screen of " + m.Label + " here: " + found.Count + " text fields to write into.");
                }
            }

            int applied = 0, missing = 0;
            for (int i = 0; i < count; i++)
            {
                var key = ReadStr(r);
                bool active = r.ReadByte() != 0;
                var txt = ReadStr(r);
                int chain = r.ReadByte();
                var pos = new Vector3[chain];
                var on = new bool[chain];
                for (int c = 0; c < chain; c++)
                {
                    on[c] = r.ReadByte() != 0;
                    pos[c] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                }

                Field f;
                if (!w.ByKey.TryGetValue(key, out f) || f.Comp == null) { missing++; continue; }

                for (int c = 0; c < chain && c < f.Chain.Length; c++)
                {
                    var tr = f.Chain[c];
                    if (tr == null) continue;
                    if (!w.OriginalPos.ContainsKey(tr)) w.OriginalPos[tr] = tr.localPosition;

                    if ((tr.localPosition - pos[c]).sqrMagnitude > 1e-6f)
                    {
                        Moves++;
                        if (_moveLogs < MoveLogCap && (tr.localPosition - pos[c]).sqrMagnitude > 1f)
                        {
                            _moveLogs++;
                            Plugin.Log("Screen: moving " + tr.name + " (" + key + " level " + c + ") "
                                       + tr.localPosition.ToString("F1") + " -> " + pos[c].ToString("F1"));
                        }
                        tr.localPosition = pos[c];
                    }
                    w.Placed[tr] = pos[c];
                    if (tr.gameObject.activeSelf != on[c]) SetActiveRemembering(w, tr.gameObject, on[c]);
                }

                if (!w.OriginalText.ContainsKey(f)) w.OriginalText[f] = ReadText(f);
                WriteText(f, txt);

                applied++;
            }
            Applied += applied;
            Unmatched += missing;
        }

        private static void SetActiveRemembering(Watched w, GameObject go, bool on)
        {
            if (go == null || go.activeSelf == on) return;
            if (!w.OriginalActive.ContainsKey(go)) w.OriginalActive[go] = go.activeSelf;
            go.SetActive(on);
        }

        /// <summary>
        /// Every frame, after the cabinet's own FSMs and animators: put each
        /// placed object back where the owner has it. The watcher's idle logic
        /// was returning the score marker to its rest position between our
        /// packets, five times a second, and it always ran after us.
        /// </summary>
        public void LateRender()
        {
            if (_watched.Count == 0) return;
            foreach (var kv in _watched)
            {
                var w = kv.Value;
                foreach (var p in w.Placed)
                {
                    var tr = p.Key;
                    if (tr == null) continue;
                    if ((tr.localPosition - p.Value).sqrMagnitude > 1e-6f) { tr.localPosition = p.Value; MovesOverridden++; }
                }
            }
        }

        /// <summary>Puts every string and every switch back the way the watcher had it.</summary>
        public void Release(uint machineId)
        {
            Watched w;
            if (!_watched.TryGetValue(machineId, out w)) return;
            foreach (var kv in w.OriginalText)
                if (kv.Key.Comp != null) WriteText(kv.Key, kv.Value);
            foreach (var kv in w.OriginalActive)
                if (kv.Key != null) kv.Key.SetActive(kv.Value);
            foreach (var kv in w.OriginalPos)
                if (kv.Key != null) kv.Key.localPosition = kv.Value;
            _watched.Remove(machineId);
        }

        public void ReleaseAll()
        {
            var ids = new List<uint>(_watched.Keys);
            foreach (var id in ids) Release(id);
        }

        // ------------------------------------------------------------ strings

        private static void WriteStr(System.IO.BinaryWriter w, string s)
        {
            var b = Encoding.UTF8.GetBytes(s ?? "");
            if (b.Length > ushort.MaxValue) b = Encoding.UTF8.GetBytes((s ?? "").Substring(0, 1024));
            w.Write((ushort)b.Length);
            w.Write(b);
        }

        private static string ReadStr(System.IO.BinaryReader r)
        {
            int n = r.ReadUInt16();
            return n == 0 ? "" : Encoding.UTF8.GetString(r.ReadBytes(n));
        }
    }
}
