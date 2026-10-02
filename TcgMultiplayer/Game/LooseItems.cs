using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace TcgMultiplayer.Game
{
    /// <summary>
    /// Loose items: the tickets a machine pays out onto the floor, a prize
    /// that came out of a claw, anything the game spawns as a rigidbody near a
    /// player that isn't part of a machine.
    ///
    /// What the scout found: a ticket payout is TICKET_PILE_03(Clone), spawned
    /// under DYNAMICOBJECTS at the machine's feet on layer TICKET with a
    /// "TICKETPILE FSM", and destroyed the moment it's picked up — the tickets
    /// go to the wallet, and the pile in the hand is a mesh, not a body. So
    /// the sync is: the owner announces an item appearing near it (a prefab
    /// name and a pose), streams its pose while it moves, and says when it's
    /// gone. The watcher finds the prefab the game already has loaded,
    /// instantiates a copy with everything stripped but the renderers, and
    /// drives it. Nobody else can pick it up — it's a picture of someone
    /// else's tickets.
    ///
    /// Everyone is an owner of what spawns near them and a watcher of
    /// everyone else's, at the same time.
    /// </summary>
    internal sealed class LooseItems
    {
        private const float ScanEvery = 1f;
        private const float PoseRate = 10f;
        private const float ResendEvery = 10f;
        private const float NearRadius = 15f;

        public const byte KindSpawn = 1, KindPose = 2, KindGone = 3, KindHeld = 4;

        // ---------------------------------------------------------- owner side
        private sealed class Mine
        {
            public uint Id;
            public Rigidbody Body;
            public string Prefab;
            public Vector3 LastPos;
            public Quaternion LastRot;
            public float NextPoseAt;
            public bool Hidden;          // under PLAYER: announced gone, still tracked
            public string HeldBone;      // where under the mesh, while it's being carried
            public float NextHeldAt;
        }
        private readonly HashSet<int> _known = new HashSet<int>();
        private readonly List<Mine> _mine = new List<Mine>();
        private float _nextScanAt, _nextResendAt;
        private bool _primed;
        private uint _nextId = 1;

        // -------------------------------------------------------- watcher side
        private sealed class Theirs
        {
            public GameObject Go;
            public Vector3 Pos;
            public Quaternion Rot;
            /// <summary>Parented to a bone on their body; the animation carries it, we don't.</summary>
            public bool Held;
        }
        private readonly Dictionary<ulong, Dictionary<uint, Theirs>> _theirs = new Dictionary<ulong, Dictionary<uint, Theirs>>();
        private readonly Dictionary<string, GameObject> _prefabs = new Dictionary<string, GameObject>();
        private readonly HashSet<string> _prefabMisses = new HashSet<string>();
        private readonly HashSet<string> _boneMisses = new HashSet<string>();
        private readonly HashSet<string> _carryPathMisses = new HashSet<string>();

        private static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var hit = FindDeep(root.GetChild(i), name);
                if (hit != null) return hit;
            }
            return null;
        }

        public int Tracked, Shown, Spawned, Gone, PrefabMisses;

        /// <summary>Called with (reliable, payload) when there is something to send.</summary>
        public Action<bool, byte[]> Send;

        /// <summary>
        /// A peer's body, so a thing they are carrying can be hung off the right
        /// bone of it. Null while they have no body yet — the item waits on the
        /// floor version until the next re-announce.
        /// </summary>
        public Func<ulong, Transform> PeerMesh;
        public int Carried, CarryMisses;

        public void Reset()
        {
            _known.Clear();
            _mine.Clear();
            _primed = false;
            DropAllTheirs();
        }

        // -------------------------------------------------------------- owner

        public void TickOwner(IEnumerable<Machine> machines, PlayerRig rig, ulong selfId)
        {
            if (rig == null || !rig.Valid) return;
            float now = Time.time;

            if (now >= _nextScanAt)
            {
                _nextScanAt = now + ScanEvery;
                Scan(machines, rig, now);
            }

            bool resend = now >= _nextResendAt;
            if (resend) _nextResendAt = now + ResendEvery;

            for (int i = _mine.Count - 1; i >= 0; i--)
            {
                var it = _mine[i];
                if (it.Body == null)
                {
                    // Picked up (a ticket pile is destroyed on pickup) or
                    // otherwise gone. Say so and forget it.
                    if (!it.Hidden) Emit(true, Gone1(it.Id));
                    _mine.RemoveAt(i);
                    Gone++;
                    continue;
                }

                var t = it.Body.transform;
                bool underPlayer = rig.Root != null && t.IsChildOf(rig.Root);
                if (underPlayer != it.Hidden)
                {
                    // Picked up but kept. It used to be announced as gone, which
                    // is why a friend holding a hundred tickets appeared to be
                    // holding nothing — they left the floor for everyone and
                    // never came back until they were dropped. Now where it sits
                    // on the body goes out instead.
                    it.Hidden = underPlayer;
                    it.HeldBone = underPlayer ? BoneUnderMesh(t, rig) : null;
                    if (underPlayer && it.HeldBone != null) Emit(true, Held1(it, rig));
                    else
                    {
                        if (underPlayer && _carryPathMisses.Add(it.Prefab ?? "?"))
                        {
                            // Worth knowing rather than guessing: if picked-up
                            // things are not parented under the character mesh,
                            // this is the line that says where they went instead.
                            string where;
                            try { where = NetId.Path(t); } catch { where = "?"; }
                            Plugin.Log("Picked up " + it.Prefab + " but it isn't under the character mesh ("
                                       + where + "), so it just disappears for everyone else.");
                        }
                        Emit(true, underPlayer ? Gone1(it.Id) : Spawn1(it));
                    }
                    if (!underPlayer) { it.LastPos = t.position; it.LastRot = t.rotation; }
                    continue;
                }
                if (it.Hidden)
                {
                    // Held items are re-announced on the resend, and otherwise
                    // only when the offset changes — swapping a prize between
                    // hands, or the game re-seating it. The bone animation is
                    // what carries it on the watcher's side, so there is nothing
                    // to stream frame by frame.
                    if (it.HeldBone == null) continue;
                    var bone = BoneUnderMesh(t, rig);
                    if (bone != it.HeldBone)
                    {
                        it.HeldBone = bone;
                        if (bone == null) { Emit(true, Gone1(it.Id)); continue; }
                        Emit(true, Held1(it, rig));
                        it.NextHeldAt = now + 1f;
                        continue;
                    }
                    if (resend || now >= it.NextHeldAt)
                    {
                        it.NextHeldAt = now + 2f;
                        Emit(true, Held1(it, rig));
                    }
                    continue;
                }

                if (resend) Emit(true, Spawn1(it));

                if (now < it.NextPoseAt) continue;
                bool moved = (t.position - it.LastPos).sqrMagnitude > 0.0001f
                          || Quaternion.Angle(t.rotation, it.LastRot) > 0.5f;
                if (!moved) continue;
                it.NextPoseAt = now + 1f / PoseRate;
                it.LastPos = t.position;
                it.LastRot = t.rotation;
                Emit(false, Pose1(it));
            }
            Tracked = _mine.Count;
        }

        private void Scan(IEnumerable<Machine> machines, PlayerRig rig, float now)
        {
            Rigidbody[] all;
            try { all = UnityEngine.Object.FindObjectsOfType<Rigidbody>(); }
            catch { return; }

            var roots = new List<Transform>();
            foreach (var m in machines) if (m != null && m.Root != null) roots.Add(m.Root);
            var player = rig.Root;

            for (int i = 0; i < all.Length; i++)
            {
                var rb = all[i];
                if (rb == null) continue;
                int id = rb.GetInstanceID();
                if (_known.Contains(id)) continue;
                _known.Add(id);
                if (!_primed) continue;                     // first scan is the baseline

                var t = rb.transform;
                // Only what the game spawned: a prefab instance. The island's
                // hand-placed litter never changes and is already there for
                // everyone.
                if (!rb.name.EndsWith("(Clone)", StringComparison.Ordinal)) continue;
                var root = t.root;
                if (root != null && root.name.StartsWith("TCGMP_", StringComparison.Ordinal)) continue;
                if (root != null && root.name.StartsWith("PLAYER", StringComparison.Ordinal)) continue;
                if (player != null && t.IsChildOf(player)) continue;
                bool underMachine = false;
                for (int r = 0; r < roots.Count; r++) if (t.IsChildOf(roots[r])) { underMachine = true; break; }
                if (underMachine) continue;
                if (player != null && (t.position - player.position).sqrMagnitude > NearRadius * NearRadius) continue;
                if (rb.GetComponentInChildren<Renderer>(true) == null) continue;

                var it = new Mine
                {
                    Id = _nextId++,
                    Body = rb,
                    Prefab = rb.name.Substring(0, rb.name.Length - "(Clone)".Length),
                    LastPos = t.position,
                    LastRot = t.rotation,
                };
                _mine.Add(it);
                Spawned++;
                Plugin.Log("Loose item " + it.Id + " appeared: " + it.Prefab + " on layer " + LayerMask.LayerToName(rb.gameObject.layer)
                           + " at " + t.position.ToString("F1") + " — telling everyone.");
                Emit(true, Spawn1(it));
            }
            _primed = true;
        }

        private void Emit(bool reliable, byte[] payload)
        {
            if (Send != null && payload != null) Send(reliable, payload);
        }

        private static byte[] Spawn1(Mine it)
        {
            var ms = new MemoryStream(64);
            var w = new BinaryWriter(ms, Encoding.UTF8);
            w.Write(KindSpawn); w.Write(it.Id);
            var name = Encoding.UTF8.GetBytes(it.Prefab ?? "");
            w.Write((ushort)name.Length); w.Write(name);
            var t = it.Body.transform;
            WritePose(w, t.position, t.rotation, (byte)it.Body.gameObject.layer);
            return ms.ToArray();
        }

        private static byte[] Pose1(Mine it)
        {
            var ms = new MemoryStream(40);
            var w = new BinaryWriter(ms);
            w.Write(KindPose); w.Write(it.Id);
            var t = it.Body.transform;
            WritePose(w, t.position, t.rotation, (byte)it.Body.gameObject.layer);
            return ms.ToArray();
        }

        /// <summary>
        /// Where the item sits under the player's mesh, as a path of bone names:
        /// "Reference/Hips/Spine/Chest/RightShoulder/RightArm/RightForeArm/RightHand".
        /// The mesh itself is left out of the path because the watcher's copy of
        /// this player may be a different character, and its own mesh is the root
        /// the path is resolved against.
        /// </summary>
        private static string BoneUnderMesh(Transform t, PlayerRig rig)
        {
            if (rig == null || rig.Mesh == null || t == null) return null;
            var parent = t.parent;
            if (parent == null || !parent.IsChildOf(rig.Mesh)) return null;
            if (parent == rig.Mesh) return "";
            var sb = new StringBuilder(96);
            BuildPath(parent, rig.Mesh, sb);
            return sb.ToString();
        }

        private static void BuildPath(Transform t, Transform stopAt, StringBuilder sb)
        {
            if (t == null || t == stopAt) return;
            BuildPath(t.parent, stopAt, sb);
            if (sb.Length > 0) sb.Append('/');
            sb.Append(t.name);
        }

        private static byte[] Held1(Mine it, PlayerRig rig)
        {
            var ms = new MemoryStream(96);
            var w = new BinaryWriter(ms, Encoding.UTF8);
            w.Write(KindHeld); w.Write(it.Id);
            var name = Encoding.UTF8.GetBytes(it.Prefab ?? "");
            w.Write((ushort)name.Length); w.Write(name);
            var bone = Encoding.UTF8.GetBytes(it.HeldBone ?? "");
            w.Write((ushort)bone.Length); w.Write(bone);
            var t = it.Body.transform;
            // The offset within the bone, so a ticket pile sits in the hand the
            // same way it does on the owner's screen.
            w.Write(t.localPosition.x); w.Write(t.localPosition.y); w.Write(t.localPosition.z);
            w.Write(t.localRotation.x); w.Write(t.localRotation.y);
            w.Write(t.localRotation.z); w.Write(t.localRotation.w);
            return ms.ToArray();
        }

        private static byte[] Gone1(uint id)
        {
            var ms = new MemoryStream(8);
            var w = new BinaryWriter(ms);
            w.Write(KindGone); w.Write(id);
            return ms.ToArray();
        }

        private static void WritePose(BinaryWriter w, Vector3 p, Quaternion q, byte layer)
        {
            w.Write(p.x); w.Write(p.y); w.Write(p.z);
            w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w);
            w.Write(layer);
        }

        // ------------------------------------------------------------ watcher

        public void Receive(ulong from, byte[] data)
        {
            if (data == null || data.Length < 5) return;
            var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
            byte kind = r.ReadByte();
            uint id = r.ReadUInt32();

            Dictionary<uint, Theirs> set;
            if (!_theirs.TryGetValue(from, out set))
            {
                set = new Dictionary<uint, Theirs>();
                _theirs[from] = set;
            }

            switch (kind)
            {
                case KindSpawn:
                {
                    int n = r.ReadUInt16();
                    var prefab = Encoding.UTF8.GetString(r.ReadBytes(n));
                    var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    var rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    r.ReadByte();
                    Theirs th;
                    if (set.TryGetValue(id, out th) && th.Go != null)
                    {
                        // A resend for anyone who joined late; we already have it.
                        // Or a prize they were carrying and have put back down,
                        // in which case it has to come off the hand first.
                        if (th.Held)
                        {
                            th.Held = false;
                            try { th.Go.transform.SetParent(null, true); } catch { }
                        }
                        th.Pos = pos; th.Rot = rot;
                        return;
                    }
                    var go = Build(prefab, id, from);
                    if (go == null) return;
                    go.transform.position = pos;
                    go.transform.rotation = rot;
                    set[id] = new Theirs { Go = go, Pos = pos, Rot = rot };
                    Shown++;
                    break;
                }
                case KindPose:
                {
                    var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    var rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    Theirs th;
                    if (!set.TryGetValue(id, out th) || th.Go == null) return;   // the spawn is on its way
                    th.Pos = pos; th.Rot = rot;
                    break;
                }
                case KindHeld:
                {
                    int pn = r.ReadUInt16();
                    var prefab = Encoding.UTF8.GetString(r.ReadBytes(pn));
                    int bn = r.ReadUInt16();
                    var bonePath = Encoding.UTF8.GetString(r.ReadBytes(bn));
                    var lp = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    var lr = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

                    var mesh = PeerMesh != null ? PeerMesh(from) : null;
                    if (mesh == null) { CarryMisses++; return; }   // no body yet; a re-announce follows

                    Transform bone = mesh;
                    if (bonePath.Length > 0)
                    {
                        bone = mesh.Find(bonePath);
                        if (bone == null)
                        {
                            // Characters may not share a skeleton. Better in the
                            // wrong hand than invisible, so fall back to the last
                            // name in the path, then to the body itself.
                            int cut = bonePath.LastIndexOf('/');
                            var leaf = cut >= 0 ? bonePath.Substring(cut + 1) : bonePath;
                            bone = FindDeep(mesh, leaf) ?? mesh;
                            if (_boneMisses.Add(bonePath))
                                Plugin.Log("Couldn't find \"" + bonePath + "\" on a peer's body for " + prefab
                                           + "; hanging it off " + bone.name + " instead.");
                        }
                    }

                    Theirs th;
                    if (!set.TryGetValue(id, out th) || th.Go == null)
                    {
                        var made = Build(prefab, id, from);
                        if (made == null) return;
                        th = new Theirs { Go = made };
                        set[id] = th;
                        Shown++;
                    }

                    if (!th.Held) Carried++;
                    th.Held = true;
                    var tr = th.Go.transform;
                    tr.SetParent(bone, false);
                    tr.localPosition = lp;
                    tr.localRotation = lr;
                    break;
                }
                case KindGone:
                {
                    Theirs th;
                    if (set.TryGetValue(id, out th))
                    {
                        if (th.Go != null) UnityEngine.Object.Destroy(th.Go);
                        set.Remove(id);
                    }
                    break;
                }
            }
        }

        /// <summary>Eases everyone else's items toward their last pose; every frame.</summary>
        public void Render()
        {
            foreach (var kv in _theirs)
            {
                foreach (var it in kv.Value.Values)
                {
                    if (it.Go == null) continue;
                    if (it.Held) continue;   // the bone it hangs off is doing the work
                    var t = it.Go.transform;
                    t.position = Vector3.Lerp(t.position, it.Pos, 0.35f);
                    t.rotation = Quaternion.Slerp(t.rotation, it.Rot, 0.35f);
                }
            }
        }

        public void ForgetPeer(ulong peer)
        {
            Dictionary<uint, Theirs> set;
            if (!_theirs.TryGetValue(peer, out set)) return;
            foreach (var it in set.Values) if (it.Go != null) UnityEngine.Object.Destroy(it.Go);
            _theirs.Remove(peer);
        }

        public void DropAllTheirs()
        {
            foreach (var kv in _theirs)
                foreach (var it in kv.Value.Values) if (it.Go != null) UnityEngine.Object.Destroy(it.Go);
            _theirs.Clear();
        }

        /// <summary>
        /// The prefab the game spawned from is still loaded — the machine's
        /// FSM holds a reference to it — so it can be found among all loaded
        /// objects by name, as the one that isn't in the scene. Cloned, and
        /// stripped to its renderers like a remote player body.
        /// </summary>
        private GameObject Build(string prefab, uint id, ulong from)
        {
            GameObject src;
            if (!_prefabs.TryGetValue(prefab, out src) || src == null)
            {
                src = FindPrefab(prefab);
                if (src == null)
                {
                    PrefabMisses++;
                    if (_prefabMisses.Add(prefab))
                        Plugin.Warn("No loaded prefab called \"" + prefab + "\" to show someone else's item with.");
                    return null;
                }
                _prefabs[prefab] = src;
            }

            GameObject go;
            try { go = UnityEngine.Object.Instantiate(src); }
            catch (Exception ex) { Plugin.Warn("Couldn't clone " + prefab + ": " + ex.Message); return null; }
            go.name = "TCGMP_Item_" + from + "_" + id;
            go.SetActive(true);
            int stripped = AvatarFactory.Strip(go);
            if (_prefabs.Count <= 8)
                Plugin.Log("Showing " + prefab + " for a peer (stripped " + stripped + " components).");
            return go;
        }

        internal static GameObject FindPrefab(string name) { return FindPrefab(name, false); }

        /// <summary>
        /// A loaded prefab asset by name. Cached, hits and misses both: a claw
        /// cabinet asks for the same few names sixty times in one manifest, and
        /// each uncached ask walks every object Unity has loaded. allowUi also
        /// accepts a prefab that draws through a canvas (Stackem Up's board).
        /// </summary>
        internal static GameObject FindPrefab(string name, bool allowUi)
        {
            GameObject best;
            string ck = (allowUi ? "ui:" : "") + name;
            if (_prefabCache.TryGetValue(ck, out best) && (best != null || Time.time < MissUntil(ck))) return best;
            best = null;
            try
            {
                var all = Resources.FindObjectsOfTypeAll<GameObject>();
                for (int i = 0; i < all.Length; i++)
                {
                    var go = all[i];
                    if (go == null || go.name != name) continue;
                    if (go.scene.IsValid()) continue;               // a scene instance, not the asset
                    if (go.transform.parent != null) continue;      // a child inside some other prefab
                    if (go.GetComponentInChildren<Renderer>(true) == null && !(allowUi && DrawsOnCanvas(go))) continue;
                    best = go;
                    break;
                }
            }
            catch { }
            _prefabCache[ck] = best;
            if (best == null) _prefabMissUntil[ck] = Time.time + 30f;
            return best;
        }
        private static readonly Dictionary<string, GameObject> _prefabCache = new Dictionary<string, GameObject>();
        private static readonly Dictionary<string, float> _prefabMissUntil = new Dictionary<string, float>();
        private static float MissUntil(string k) { float t; return _prefabMissUntil.TryGetValue(k, out t) ? t : 0f; }

        internal static bool DrawsOnCanvas(GameObject go)
        {
            var comps = go.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < comps.Length; i++)
                if (comps[i] != null && comps[i].GetType().Name == "CanvasRenderer") return true;
            return false;
        }
    }
}
