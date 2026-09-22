using System;
using System.Collections.Generic;
using Steamworks;
using TcgMultiplayer.Net;
using UnityEngine;

namespace TcgMultiplayer.Game
{
    /// <summary>
    /// Owns the local player rig, every remote body, and the sample/send loop.
    ///
    /// Also owns Mirror mode, which is not a toy: with only one copy of the game
    /// there is no second client to test against, so Mirror feeds the local
    /// player's own state through the real serializer and the real replication
    /// path, delivers it back after a delay, and spawns a real remote avatar from
    /// it. Everything except Steam's own delivery is exercised — and if the ghost
    /// walks your path correctly, M2 works.
    /// </summary>
    internal sealed class AvatarDirector
    {
        public const ulong MirrorPeerId = 1UL;   // not a valid SteamID, so it can't collide

        private readonly Session _session;
        private readonly PlayerRig _rig = new PlayerRig();
        private readonly Dictionary<ulong, RemoteAvatar> _avatars = new Dictionary<ulong, RemoteAvatar>();
        private readonly List<ulong> _scratch = new List<ulong>();

        // Mirror mode plumbing
        private readonly Queue<KeyValuePair<float, byte[]>> _mirrorQueue = new Queue<KeyValuePair<float, byte[]>>();
        public bool MirrorEnabled;
        public float MirrorDelay = 1.5f;

        private float _nextSendAt;
        private ushort _seq;
        private float _nextAcquireAt;

        public float SendRate = 15f;

        public int AvatarCount { get { return _avatars.Count; } }
        public bool RigReady { get { return _rig.Valid; } }

        /// <summary>The local player rig. Seating needs to move it; nothing else writes to it.</summary>
        public PlayerRig Rig { get { return _rig; } }

        /// <summary>
        /// Set by Plugin. What the local player is riding, and how to find any
        /// vehicle by id. Injected rather than referenced because the avatar
        /// side is built before the machine side and must keep working if the
        /// machine side never comes up at all.
        /// </summary>
        public Func<Attachment> LocalAttachment;
        public Func<uint, Transform> AttachmentRoot;

        public AvatarDirector(Session session)
        {
            _session = session;
            _session.OnPlayerState += OnPlayerState;
            _session.OnPeerGone += id => Despawn(id.m_SteamID);
        }

        // ----------------------------------------------------------------- pump

        public void Tick()
        {
            if (!_rig.Valid && Time.time >= _nextAcquireAt)
            {
                _nextAcquireAt = Time.time + 1f;
                _rig.Acquire();
            }

            bool sending = _rig.Valid && (MirrorEnabled || _session.State == SessionState.InLobby);

            if (sending && Time.time >= _nextSendAt)
            {
                _nextSendAt = Time.time + 1f / Mathf.Max(1f, SendRate);
                var state = StampAttachment(_rig.Sample());
                unchecked { _seq++; }

                if (_session.State == SessionState.InLobby)
                    _session.BroadcastPlayerState(state, _seq);

                if (MirrorEnabled)
                {
                    // Same bytes a peer would receive — the point is that Mirror
                    // does not take a shortcut past the wire format.
                    using (var w = new PacketWriter(Op.PlayerState))
                    {
                        Session.WritePlayerState(w, state, _seq);
                        _mirrorQueue.Enqueue(new KeyValuePair<float, byte[]>(Time.time + MirrorDelay, w.ToArray()));
                    }
                }
            }

            DrainMirror();

            foreach (var kv in _avatars) kv.Value.Render(AttachmentRoot);
        }

        /// <summary>
        /// If we're riding in something, rewrite the snapshot into that thing's
        /// own frame before it goes out. Everything downstream — the wire, the
        /// mirror, the remote body — then agrees on what the numbers mean, and
        /// a passenger stays welded to their seat instead of being the result
        /// of two interpolators trying to agree twenty times a second.
        /// </summary>
        private PlayerState StampAttachment(PlayerState st)
        {
            if (LocalAttachment == null) return st;

            var a = LocalAttachment();
            if (!a.Any) return st;

            var root = AttachmentRoot != null ? AttachmentRoot(a.Machine) : null;
            if (root == null) return st;     // can't express it relatively: send it as-is

            st.Attached = a.Machine;
            st.Seat = a.Seat;

            // The seat, not the rig. Measuring off the rig is what put a frozen
            // body on the pavement while the driver drove off: the game parks
            // PLAYER where you got in and moves the vehicle instead, so the rig
            // reports the same spot forever.
            st.Pos = a.LocalPos;
            st.Yaw = a.LocalYaw;

            // Standing still in a seat, so the walk blend has to be told that
            // rather than inferring it from a position that never changes.
            st.VelX = 0f; st.VelZ = 0f; st.Turn = 0f;
            st.Grounded = true; st.Running = false; st.Jumping = false;
            return st;
        }

        private void DrainMirror()
        {
            if (!MirrorEnabled)
            {
                if (_mirrorQueue.Count > 0) _mirrorQueue.Clear();
                if (_avatars.ContainsKey(MirrorPeerId)) Despawn(MirrorPeerId);
                return;
            }

            while (_mirrorQueue.Count > 0 && _mirrorQueue.Peek().Key <= Time.time)
            {
                var bytes = _mirrorQueue.Dequeue().Value;
                try
                {
                    using (var pr = new PacketReader(bytes))
                    {
                        if (pr.Op != Op.PlayerState) continue;
                        ushort seq;
                        var st = Session.ReadPlayerState(pr, out seq);
                        Deliver(MirrorPeerId, "Mirror (you, " + MirrorDelay.ToString("0.0") + "s ago)", st);
                    }
                }
                catch (Exception ex) { Plugin.Warn("Mirror decode failed: " + ex.Message); }
            }
        }

        private void OnPlayerState(CSteamID from, PlayerState st, ushort seq)
        {
            var name = from.m_SteamID.ToString();
            foreach (var p in _session.Peers)
                if (p.Id == from) { name = p.Name; break; }

            Deliver(from.m_SteamID, name, st);
        }

        private void Deliver(ulong key, string label, PlayerState st)
        {
            RemoteAvatar av;
            if (!_avatars.TryGetValue(key, out av))
            {
                if (!_rig.Valid) return;   // nothing to clone from yet
                av = new RemoteAvatar();
                if (!av.Spawn(_rig.Mesh, label)) return;
                _avatars[key] = av;
            }
            av.Label = label;
            av.Push(st);
        }

        private void Despawn(ulong key)
        {
            RemoteAvatar av;
            if (!_avatars.TryGetValue(key, out av)) return;
            av.Despawn();
            _avatars.Remove(key);
        }

        public void DespawnAll()
        {
            _scratch.Clear();
            foreach (var kv in _avatars) _scratch.Add(kv.Key);
            foreach (var k in _scratch) Despawn(k);
            _mirrorQueue.Clear();
        }

        /// <summary>
        /// Scenes load additively here and the PLAYER object is rebuilt, so both
        /// the rig reference and every clone go stale on a scene change.
        /// </summary>
        public void OnSceneChanged()
        {
            DespawnAll();
            _rig.Forget();
            _nextAcquireAt = Time.time + 2f;
        }

        // -------------------------------------------------------------- display

        public void DrawNameplates()
        {
            if (_avatars.Count == 0) return;

            var cam = Camera.main;
            if (cam == null && _rig.Cam != null) cam = _rig.Cam.GetComponent<Camera>();
            if (cam == null) return;

            foreach (var kv in _avatars)
            {
                var av = kv.Value;
                if (!av.Alive) continue;

                float dist;
                var pt = av.NameplatePoint(cam, out dist);
                if (!pt.HasValue) continue;
                if (dist > 60f) continue;

                var text = av.Label;
                var size = GUI.skin.label.CalcSize(new GUIContent(text));
                var rect = new Rect(pt.Value.x - size.x * 0.5f - 6f, pt.Value.y - size.y * 0.5f - 3f,
                                    size.x + 12f, size.y + 6f);

                var prev = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.45f);
                GUI.Box(rect, GUIContent.none);
                GUI.color = new Color(1f, 0.86f, 0.55f, 1f);
                GUI.Label(new Rect(rect.x + 6f, rect.y + 3f, size.x, size.y), text);
                GUI.color = prev;
            }
        }

        public string DebugLine
        {
            get
            {
                if (!_rig.Valid) return "player rig not found yet";
                var s = _avatars.Count + " avatar(s)";
                foreach (var kv in _avatars)
                    s += "   " + kv.Value.Label + ": " + kv.Value.Buffered + " buffered, "
                         + (kv.Value.LastPacketAge * 1000f).ToString("0") + "ms since packet";
                return s;
            }
        }
    }
}
