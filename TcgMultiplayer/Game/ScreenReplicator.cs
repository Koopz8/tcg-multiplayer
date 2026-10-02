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
        /// <summary>
        /// Was 0.2 s, which is fine for numbers and fatal for anything that
        /// slides: Treasure's countdown rides on the shovel's X/Y carriage, so
        /// the carriage went with it — snapped into place five times a second
        /// while the shovel under it (streamed as a body, twenty times a
        /// second) moved smoothly. "The top half of the arm is choppy". Ten a
        /// second, eased on the watcher's side, keeps the two together.
        /// </summary>
        private const float PollEvery = 0.1f;
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
        private readonly HashSet<Transform> _mineChains = new HashSet<Transform>();
        /// <summary>The machine whose screen we last scanned as owner.</summary>
        public uint MineMachine { get { return _mineMachine; } }
        /// <summary>Is this transform moved for the watcher by the screen mirror (a text or one of its parents)?</summary>
        public bool SentByScreen(Transform t) { return t != null && _mineChains.Contains(t); }
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
            /// <summary>Slides in progress: each placed object eases from where it's drawn to where the owner has it.</summary>
            public readonly Dictionary<Transform, Ease> Easing = new Dictionary<Transform, Ease>();
            public float Gap = PollEvery, LastPacketAt = -1f;
            public readonly Dictionary<ushort, Transform> MoverIds = new Dictionary<ushort, Transform>();
            public Dictionary<string, Transform> AllByKey;
            public readonly Dictionary<Transform, Quaternion> OriginalRot = new Dictionary<Transform, Quaternion>();
            public int MoverMisses;
            public readonly Dictionary<Behaviour, bool> FsmWas = new Dictionary<Behaviour, bool>();
            public readonly Dictionary<Component, bool> Sim2DWas = new Dictionary<Component, bool>();
            public readonly Dictionary<Component, bool> ShownWas = new Dictionary<Component, bool>();
            public readonly Dictionary<Behaviour, Color> ColorWas = new Dictionary<Behaviour, Color>();
            /// <summary>Objects we built because the owner's round spawned them and ours didn't.</summary>
            public readonly List<GameObject> Spawned = new List<GameObject>();
            public readonly HashSet<string> SpawnGaveUp = new HashSet<string>();
        }
        private sealed class Ease
        {
            public Vector3 From, To;
            public float At, Dur;
            public bool Turns;
            public Quaternion FromRot, ToRot;
        }

        /// <summary>Set by the director; the owner's moving-part sampler.</summary>
        public UnsentMovers Movers;

        /// <summary>
        /// Further than this in one packet is a jump, not a slide — the
        /// scrolling player name wraps from one side of its panel to the other
        /// (130 canvas units), and easing that would drag it back across the
        /// screen. The shovel's carriage moves centimetres per packet.
        /// </summary>
        private const float SnapBeyond = 20f;
        private readonly Dictionary<uint, Watched> _watched = new Dictionary<uint, Watched>();

        /// <summary>Per-machine spectating summary; see WatchReport.</summary>
        public WatchReport Report;

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
                Gather(m.Play, _mine);
                FieldsFound = _mine.Count;
                _mineChains.Clear();
                for (int i = 0; i < _mine.Count; i++)
                    for (int c = 0; c < _mine[i].Chain.Length; c++)
                        if (_mine[i].Chain[c] != null) _mineChains.Add(_mine[i].Chain[c]);
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
            if (Movers != null) Movers.Sample(m, SentByScreen);
            if (changed.Count == 0 && (Movers == null || !Movers.AnyLive)) return null;

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
            // Moving parts that are neither bodies nor text, after the fields.
            // An old watcher stops reading at the end of the fields and never
            // sees them, which is the right failure.
            int movers = Movers != null ? Movers.Write(w, keyframe) : 0;
            if (changed.Count == 0 && movers == 0) return null;
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

            if (w.LastPacketAt >= 0f)
            {
                float gap = now - w.LastPacketAt;
                if (gap > 0.01f && gap < 1f) w.Gap = Mathf.Lerp(w.Gap, gap, 0.1f);
            }
            w.LastPacketAt = now;

            var r = new System.IO.BinaryReader(new System.IO.MemoryStream(data), Encoding.UTF8);
            bool keyframe = (r.ReadByte() & FlagKeyframe) != 0;
            int count = r.ReadUInt16();

            if (keyframe || now >= w.RescanAt || w.ByKey.Count == 0)
            {
                w.RescanAt = now + KeyframeEvery;
                var found = new List<Field>();
                Gather(m.Play, found);
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
                        Ease e;
                        bool known = w.Easing.TryGetValue(tr, out e);
                        var from = tr.localPosition;
                        if (!known || (pos[c] - from).magnitude > SnapBeyond)
                        {
                            tr.localPosition = pos[c];
                            from = pos[c];
                        }
                        if (!known) { e = new Ease(); w.Easing[tr] = e; }
                        e.From = from;
                        e.To = pos[c];
                        e.At = now;
                        e.Dur = w.Gap * 1.1f;
                    }
                    else if (!w.Easing.ContainsKey(tr))
                    {
                        w.Easing[tr] = new Ease { From = pos[c], To = pos[c], At = now, Dur = 0f };
                    }
                    w.Placed[tr] = tr.localPosition;
                    if (tr.gameObject.activeSelf != on[c]) SetActiveRemembering(w, tr.gameObject, on[c]);
                }

                if (!w.OriginalText.ContainsKey(f)) w.OriginalText[f] = ReadText(f);
                WriteText(f, txt);

                applied++;
            }
            if (r.BaseStream.Position + 2 <= r.BaseStream.Length) ApplyMovers(m, w, r, now);

            Applied += applied;
            Unmatched += missing;
            if (Report != null) Report.Screen(m.Id, w.ByKey.Count, applied);
        }

        private void ApplyMovers(Machine m, Watched w, System.IO.BinaryReader r, float now)
        {
            int n = r.ReadUInt16();
            for (int i = 0; i < n; i++)
            {
                ushort id = r.ReadUInt16();
                byte flags = r.ReadByte();
                string key = (flags & 1) != 0 ? ReadStr(r) : null;
                var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var rot = new Quaternion(UnsentMovers.DeQ(r.ReadInt16()), UnsentMovers.DeQ(r.ReadInt16()),
                                         UnsentMovers.DeQ(r.ReadInt16()), UnsentMovers.DeQ(r.ReadInt16()));
                bool on = (flags & 2) != 0;
                bool vis = (flags & 4) != 0;
                bool hasCol = (flags & 8) != 0;
                Color col = Color.white;
                if (hasCol) col = new Color(r.ReadByte() / 255f, r.ReadByte() / 255f, r.ReadByte() / 255f, r.ReadByte() / 255f);

                Transform tr;
                if (key != null)
                {
                    if (w.AllByKey == null) w.AllByKey = IndexAll(m.Play);
                    if ((!w.AllByKey.TryGetValue(key, out tr) || tr == null) && SpawnFor(m, w, key))
                        w.AllByKey.TryGetValue(key, out tr);
                    if (tr == null)
                    {
                        w.MoverIds.Remove(id);
                        if (w.MoverMisses++ < 4)
                            Plugin.Log("Mover on " + m.Label + " we haven't got: " + key);
                        continue;
                    }
                    w.MoverIds[id] = tr;
                }
                else if (!w.MoverIds.TryGetValue(id, out tr) || tr == null) continue;

                // A part that moves for the player is on for the player, and
                // so is everything it hangs from. The skee-ball throw arm sits
                // under a controller that only switches on for a round.
                if (tr.gameObject.activeSelf != on) SetActiveRemembering(w, tr.gameObject, on);
                Visual.Show(tr, vis, w.ShownWas);
                if (hasCol) Visual.SetColor(tr, col, w.ColorWas);
                if (on)
                {
                    var a = tr.parent;
                    while (a != null && a != m.Play) { if (!a.gameObject.activeSelf) SetActiveRemembering(w, a.gameObject, true); a = a.parent; }
                }

                if (!w.OriginalPos.ContainsKey(tr)) w.OriginalPos[tr] = tr.localPosition;
                if (!w.OriginalRot.ContainsKey(tr)) w.OriginalRot[tr] = tr.localRotation;
                // A 2D body (Speed Drop's balls) would keep falling under our
                // hand and snap back every physics step.
                Quiet.Hold2D(tr, w.Sim2DWas);

                float mag = Mathf.Sqrt(rot.x * rot.x + rot.y * rot.y + rot.z * rot.z + rot.w * rot.w);
                if (mag > 0.0001f) rot = new Quaternion(rot.x / mag, rot.y / mag, rot.z / mag, rot.w / mag);

                Ease e;
                bool known = w.Easing.TryGetValue(tr, out e);
                if (!known) { e = new Ease(); w.Easing[tr] = e; tr.localPosition = pos; tr.localRotation = rot; }
                e.Turns = true;
                e.From = tr.localPosition;
                e.FromRot = tr.localRotation;
                e.To = pos;
                e.ToRot = rot;
                e.At = now;
                e.Dur = w.Gap * 1.1f;
                w.Placed[tr] = tr.localPosition;
                MoversApplied++;
            }
        }
        public int MoversApplied;

        /// <summary>
        /// A moving part whose path we haven't got, because the owner's round
        /// spawned the thing it belongs to — Stackem Up builds a fresh game
        /// board from a prefab every round. Find the first missing step of the
        /// path; if it's a "(Clone)", build it from the prefab under the part
        /// of the path we do have, with every FSM in it switched off, and index
        /// it. Destroyed on release.
        /// </summary>
        private static bool SpawnFor(Machine m, Watched w, string key)
        {
            var parts = key.Split('/');
            string have = "";
            Transform parent = m.Play;
            for (int i = 0; i < parts.Length; i++)
            {
                string next = have.Length == 0 ? parts[i] : have + "/" + parts[i];
                Transform t;
                if (w.AllByKey.TryGetValue(next, out t) && t != null) { have = next; parent = t; continue; }

                string seg = parts[i];
                int hash = seg.IndexOf('#');
                string name = hash >= 0 ? seg.Substring(0, hash) : seg;
                if (!name.EndsWith("(Clone)", StringComparison.Ordinal) || w.SpawnGaveUp.Contains(next)) return false;
                var src = LooseItems.FindPrefab(name.Substring(0, name.Length - "(Clone)".Length), true);
                if (src == null)
                {
                    w.SpawnGaveUp.Add(next);
                    Plugin.Log("Can't show " + m.Label + "'s " + name + ": no loaded prefab by that name.");
                    return false;
                }
                GameObject go;
                try { go = UnityEngine.Object.Instantiate(src, parent, false); }
                catch (Exception ex) { w.SpawnGaveUp.Add(next); Plugin.Warn("Couldn't build " + name + " for " + m.Label + ": " + ex.Message); return false; }
                go.name = name;
                var fsms = go.GetComponentsInChildren<PlayMakerFSM>(true);
                for (int f = 0; f < fsms.Length; f++) if (fsms[f] != null) fsms[f].enabled = false;
                w.Spawned.Add(go);
                MoverSpawns++;
                Plugin.Log("Built " + m.Label + "'s " + name + " from its prefab - the owner's round made one and ours didn't.");
                w.AllByKey = IndexAll(m.Play);
                return true;
            }
            return false;
        }
        public static int MoverSpawns;

        /// <summary>Every transform under the root by the same path the owner names them by.</summary>
        private static Dictionary<string, Transform> IndexAll(Transform root)
        {
            var map = new Dictionary<string, Transform>(1024);
            IndexChildren(root, "", map);
            return map;
        }

        private static void IndexChildren(Transform t, string key, Dictionary<string, Transform> map)
        {
            Dictionary<string, int> seen = t.childCount > 1 ? new Dictionary<string, int>(t.childCount) : null;
            for (int c = 0; c < t.childCount; c++)
            {
                var ch = t.GetChild(c);
                int n = 0;
                if (seen != null) { seen.TryGetValue(ch.name, out n); seen[ch.name] = n + 1; }
                string ck = (key.Length == 0 ? "" : key + "/") + (n == 0 ? ch.name : ch.name + "#" + n);
                if (!map.ContainsKey(ck)) map[ck] = ch;
                IndexChildren(ch, ck, map);
            }
        }

        private static void SetActiveRemembering(Watched w, GameObject go, bool on)
        {
            if (go == null || go.activeSelf == on) return;
            if (!w.OriginalActive.ContainsKey(go)) w.OriginalActive[go] = go.activeSelf;
            if (on) Quiet.Activate(go, w.FsmWas);
            else go.SetActive(false);
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
                float now = Time.time;
                foreach (var p in w.Easing)
                {
                    var tr = p.Key;
                    if (tr == null) continue;
                    var e = p.Value;
                    float k = e.Dur <= 0.0001f ? 1f : Mathf.Clamp01((now - e.At) / e.Dur);
                    var want = Vector3.LerpUnclamped(e.From, e.To, k);
                    if (e.Turns) tr.localRotation = Quaternion.Slerp(e.FromRot, e.ToRot, k);
                    Vector3 placed;
                    if (w.Placed.TryGetValue(tr, out placed) && (tr.localPosition - placed).sqrMagnitude > 1e-6f) MovesOverridden++;
                    tr.localPosition = want;
                    _placedScratch.Add(new KeyValuePair<Transform, Vector3>(tr, want));
                }
                for (int i = 0; i < _placedScratch.Count; i++) w.Placed[_placedScratch[i].Key] = _placedScratch[i].Value;
                _placedScratch.Clear();
            }
        }

        private readonly List<KeyValuePair<Transform, Vector3>> _placedScratch = new List<KeyValuePair<Transform, Vector3>>(64);

        /// <summary>Puts every string and every switch back the way the watcher had it.</summary>
        public void Release(uint machineId)
        {
            Watched w;
            if (!_watched.TryGetValue(machineId, out w)) return;
            foreach (var kv in w.OriginalText)
                if (kv.Key.Comp != null) WriteText(kv.Key, kv.Value);
            foreach (var kv in w.OriginalActive)
                if (kv.Key != null) kv.Key.SetActive(kv.Value);
            Quiet.Restore(w.FsmWas);
            Quiet.Restore2D(w.Sim2DWas);
            Visual.RestoreShown(w.ShownWas);
            Visual.RestoreColors(w.ColorWas);
            for (int i = 0; i < w.Spawned.Count; i++)
                if (w.Spawned[i] != null) { try { UnityEngine.Object.Destroy(w.Spawned[i]); } catch { } }
            w.Spawned.Clear();
            foreach (var kv in w.OriginalRot)
                if (kv.Key != null) kv.Key.localRotation = kv.Value;
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
