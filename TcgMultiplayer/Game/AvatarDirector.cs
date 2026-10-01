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
        private string _announcedCharacter = "";
        private byte[] _sentProps;
        private float _nextPropsAt, _nextPropsResendAt, _nextReapplyAt;
        private readonly Dictionary<ulong, byte[]> _theirProps = new Dictionary<ulong, byte[]>();
        public int PropsSwitched;
        private bool _comparedBody;

        // Mirror mode plumbing
        private readonly Queue<KeyValuePair<float, byte[]>> _mirrorQueue = new Queue<KeyValuePair<float, byte[]>>();
        public bool MirrorEnabled;
        public float MirrorDelay = 1.5f;

        private float _nextSendAt;
        private ushort _seq;
        private float _nextAcquireAt;

        public float SendRate = 15f;

        public int AvatarCount { get { return _avatars.Count; } }

        /// <summary>
        /// A peer's body, which is a clone of whichever character mesh they
        /// picked — so its own transform is the root that a bone path from their
        /// machine resolves against. Used to hang what they are carrying off
        /// their hand.
        /// </summary>
        public Transform MeshOf(ulong peer)
        {
            RemoteAvatar a;
            if (_avatars.TryGetValue(peer, out a) && a.Alive) return a.Go.transform;
            return null;
        }
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

        /// <summary>Set by Plugin: where a remote player says their seat is.</summary>
        public Action<uint, byte, Vector3> OnRemoteSeat;

        public AvatarDirector(Session session)
        {
            _session = session;
            _session.OnPlayerState += OnPlayerState;
            _session.OnPeerGone += id => Despawn(id.m_SteamID);

            // Someone told us who they are, late. If we already built them a
            // body from the wrong source, drop it - the next position packet
            // is a few milliseconds away and rebuilds it as the right one.
            _session.OnCharacter += (id, who) =>
            {
                if (!_avatars.ContainsKey(id.m_SteamID)) return;
                Plugin.Log("Rebuilding their body as " + who + ".");
                Despawn(id.m_SteamID);
            };

            _session.OnBodyProps += (id, mask) =>
            {
                if (mask == null) return;
                // Kept whether or not their body exists yet: a mask that arrives
                // first is applied the moment the body is built, so nobody is
                // ever briefly holding somebody else's tickets.
                _theirProps[id.m_SteamID] = mask;
                ApplyProps(id.m_SteamID);
            };
        }

        /// <summary>
        /// Switches a body's parts to match the last mask its owner sent. Called
        /// when a mask arrives and when a body is built, because either can be
        /// second.
        /// </summary>
        private void ApplyProps(ulong peer)
        {
            byte[] mask;
            RemoteAvatar a;
            if (!_theirProps.TryGetValue(peer, out mask)) return;
            if (!_avatars.TryGetValue(peer, out a) || !a.Alive) return;
            PropsSwitched += BodyProps.Apply(a.Go.transform, mask);
        }

        // ----------------------------------------------------------------- pump

        public void Tick()
        {
            if (!_rig.Valid && Time.time >= _nextAcquireAt)
            {
                _nextAcquireAt = Time.time + 1f;
                _rig.Acquire();
            }

            // You join from the menu and load in afterwards, so at handshake
            // time there is no player yet and nobody knows which character
            // they are. Say so as soon as we do, and again if it ever changes.
            if (_session.State == SessionState.InLobby
                && PlayerRig.LocalCharacter.Length > 0
                && PlayerRig.LocalCharacter != _announcedCharacter)
            {
                _announcedCharacter = PlayerRig.LocalCharacter;
                _session.SendCharacter(_announcedCharacter);
                Plugin.Log("Told everyone we're playing as " + _announcedCharacter + ".");
            }

            // What's switched on under our own mesh. A prize or a ticket pile in
            // the hand is a prop that was always part of the character, so this
            // is what tells everyone else whether it is showing — and, at the
            // start, that it is NOT, which is the bug it was written for.
            if (_session.State == SessionState.InLobby && _rig.Valid && Time.time >= _nextPropsAt)
            {
                _nextPropsAt = Time.time + 0.25f;
                var mask = BodyProps.Pack(_rig.Mesh);
                if (mask != null && (!SameMask(mask, _sentProps) || Time.time >= _nextPropsResendAt))
                {
                    _sentProps = mask;
                    _nextPropsResendAt = Time.time + 10f;   // and for whoever just loaded in
                    _session.SendBodyProps(mask);
                }
            }

            // Put everyone's body back to what its owner last said, every couple
            // of seconds, whether or not a new message arrived. Twice now
            // something else has quietly switched a prop back on — the asset's
            // own defaults, then the detail-level swap — and a correction that
            // only runs when a packet lands can't catch either. Walking a
            // hundred-odd transforms a body costs nothing next to being wrong
            // about what somebody is holding.
            if (_session.State == SessionState.InLobby && Time.time >= _nextReapplyAt)
            {
                _nextReapplyAt = Time.time + 2f;
                foreach (var kv in _theirProps) ApplyProps(kv.Key);
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

                // Show them as the character THEY picked, not as whoever we
                // happen to be playing. Their handshake said which one.
                //
                // Positions ride the unreliable channel and the handshake the
                // reliable one, so a position CAN arrive first. Spawning then
                // would clone the wrong body and cache it for the whole
                // session, so a peer we know about but haven't handshaked
                // with yet waits a beat. The mirror's fake peer isn't in the
                // list at all, and still spawns immediately.
                string character = "";
                bool known = false;
                foreach (var p in _session.Peers)
                    if (p.Id.m_SteamID == key)
                    {
                        known = true;
                        if (!p.Handshaked) return;
                        character = p.Character;
                        break;
                    }
                if (!known) character = "";

                var source = AvatarFactory.SourceFor(character, _rig.Mesh);
                av = new RemoteAvatar();
                if (!av.Spawn(source, label)) return;
                _avatars[key] = av;
                ApplyProps(key);
                // Once per session: what our own mesh has that their clone does
                // not. The blind bitmask assumed these two were the same shape.
                if (!_comparedBody)
                {
                    _comparedBody = true;
                    BodyProps.Compare(_rig.Mesh, av.Go.transform);
                }
            }
            av.Label = label;
            av.Push(st);

            // Every snapshot that says "I am in seat N of vehicle X, here" is
            // also the answer to where that seat IS. Worth much more than a
            // guess from the bounding box, so pass it on.
            if (st.Attached != 0 && OnRemoteSeat != null)
                OnRemoteSeat(st.Attached, st.Seat, st.Pos);
        }

        private void Despawn(ulong key)
        {
            RemoteAvatar av;
            if (!_avatars.TryGetValue(key, out av)) return;
            av.Despawn();
            _avatars.Remove(key);
        }

        private static bool SameMask(byte[] a, byte[] b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        public void DespawnAll()
        {
            _scratch.Clear();
            foreach (var kv in _avatars) _scratch.Add(kv.Key);
            foreach (var k in _scratch) Despawn(k);
            _mirrorQueue.Clear();
            // Next session announces again from scratch.
            _announcedCharacter = "";
            _sentProps = null;
            _theirProps.Clear();
            BodyProps.Switched = 0;
            BodyProps.Applied = 0;
            BodyProps.Skipped = 0;
            BodyProps.Forget();
            _comparedBody = false;
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
