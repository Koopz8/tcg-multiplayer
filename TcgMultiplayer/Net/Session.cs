using System;
using System.Collections.Generic;
using System.Diagnostics;
using Steamworks;

namespace TcgMultiplayer.Net
{
    public enum SessionState { Offline, Creating, Joining, InLobby }

    public sealed class Peer
    {
        public CSteamID Id;
        public string Name = "?";
        public string ModVersion = "";
        public bool Handshaked;
        public float RttMs = -1f;
        public double LastHeardAt;
        public long PendingPingTick;
        public ushort LastStateSeq;
        public bool HasState;
        // Their wallet, for the scoreboard only. Nothing authoritative rides on this.
        public int Coins, Tickets, TicketsSession;
        public bool HasWallet;
        public int BadPackets;
    }

    /// <summary>
    /// M1: get two copies of the game talking. Lobby lifecycle, a handshake, a
    /// ping/pong round-trip and a chat line. No gameplay state crosses the wire
    /// yet — the point is to prove the transport in isolation.
    /// </summary>
    public sealed class Session
    {
        public const string LobbyKeyMod = "tcgmp_version";
        public const string LobbyKeyHost = "tcgmp_host";
        public const string LobbyKeyBuild = "tcgmp_build";

        public SessionState State { get; private set; }
        public CSteamID Lobby { get; private set; }
        public bool IsHost { get; private set; }
        public CSteamID SelfId { get; private set; }
        public string SelfName { get; private set; }

        public readonly List<Peer> Peers = new List<Peer>();
        public readonly List<string> Chat = new List<string>();

        /// <summary>Raised for every remote player snapshot. AvatarDirector listens.</summary>
        public Action<CSteamID, Game.PlayerState, ushort> OnPlayerState;
        /// <summary>Raised when a peer leaves, so their body can be removed.</summary>
        public Action<CSteamID> OnPeerGone;
        /// <summary>Host only: someone wants (or is releasing) a machine.</summary>
        public Action<CSteamID, uint, bool> OnMachineClaim;
        /// <summary>The host's ruling on who owns a machine. 0 = free.</summary>
        public Action<uint, ulong, string> OnMachineOwner;
        /// <summary>An FSM event the machine's owner wants spectators to replay.</summary>
        public Action<CSteamID, uint, uint, string> OnMachineEvent;
        /// <summary>Shared island progression changed. Last arg: true if this is the host's ruling.</summary>
        public Action<CSteamID, string, object, bool> OnWorldVar;
        /// <summary>Host only: this peer just joined and wants the island's current state.</summary>
        public Action<CSteamID> OnWorldSnapshotRequest;
        /// <summary>Rigidbody poses inside a machine someone else is playing.</summary>
        public Action<CSteamID, uint, byte[]> OnMachinePhysics;
        /// <summary>What the cabinet's screen says, for a machine someone else is playing.</summary>
        public Action<CSteamID, uint, byte[]> OnMachineScreen;
        /// <summary>A loose item (tickets, a prize) appearing, moving or vanishing near a peer.</summary>
        public Action<CSteamID, byte[]> OnLooseItem;
        /// <summary>Where a vehicle someone else is driving has got to.</summary>
        public Action<CSteamID, uint, ObjectPose> OnObjectState;
        /// <summary>Host only: someone wants a seat in something, or wants out of it.</summary>
        public Action<CSteamID, uint, bool> OnSeatRequest;
        /// <summary>The host's ruling on who is sitting where. Seat 0 is the driver.</summary>
        public Action<uint, ulong[]> OnSeatGrant;
        /// <summary>A session has begun. Argument: true if we are the host.</summary>
        public Action<bool> OnSessionBegan;
        /// <summary>A session has ended, however it ended. Nothing may assume a clean exit.</summary>
        public Action OnSessionEnded;

        /// <summary>
        /// Set when we walked away from a lobby because it was not compatible.
        /// The overlay shows this; it is the difference between "it didn't work"
        /// and "here is exactly what to fix".
        /// </summary>
        public string RefusedReason { get; private set; }

        /// <summary>Same build of the game as the host? Null until we've been in a lobby.</summary>
        public string BuildMismatch { get; private set; }

        // Counters for the end-of-session report. Cheap, and the difference
        // between a tester saying "it went weird" and a tester handing over
        // something you can act on.
        public int PacketsSent, PacketsReceived;
        public long BytesSent, BytesReceived;
        public DateTime StartedAt;
        public int PeakPeers;

        /// <summary>How the session finished. Set by whichever path ended it.</summary>
        public string EndReason { get; private set; }

        public void NoteEndReason(string why)
        {
            if (string.IsNullOrEmpty(EndReason)) EndReason = why;
        }

        // Steam sits behind these two. In a real session they are the Steam
        // implementations and nothing is different; in the test rig they are
        // fakes, which is the only way this class has ever been run with a
        // second peer at the other end.
        private readonly ITransport _net;
        private readonly ILobbyBackend _lobby;

        private CSteamID _hostId;
        private bool _wasHostAtJoin;
        private double _nextLobbyWatchAt, _lastTickAt, _lastTickGap;
        private int _emptyLobbyReads;
        private readonly List<SteamTransport.Received> _inbox = new List<SteamTransport.Received>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _nextPingAt;

        /// <summary>
        /// Seconds elapsed, normally straight off the stopwatch above. The test
        /// rig replaces it with a clock it controls, so the thirty-second peer
        /// timeout and the stall detector can be exercised in a millisecond
        /// instead of in real time. Nothing sets this in a real session.
        /// </summary>
        public Func<double> ClockOverride;

        private double Now
        {
            get { return ClockOverride != null ? ClockOverride() : _clock.Elapsed.TotalSeconds; }
        }

        public bool Ready { get; private set; }

        // ---------------------------------------------------------------- setup

        /// <summary>The normal one: real Steam at both ends.</summary>
        public Session() : this(SteamTransportBackend.Instance, new SteamLobbyBackend()) { }

        /// <summary>For the test rig, which supplies a loopback pair instead.</summary>
        public Session(ITransport transport, ILobbyBackend lobby)
        {
            _net = transport;
            _lobby = lobby;
        }

        public bool Init()
        {
            if (!_lobby.Start()) return false;

            SelfId = _lobby.SelfId;
            SelfName = _lobby.SelfName;

            _lobby.LobbyCreated = OnLobbyCreated;
            _lobby.LobbyEntered = OnLobbyEnter;
            _lobby.LobbyChatUpdate = OnLobbyChatUpdate;
            _lobby.JoinRequested = OnJoinRequested;
            _lobby.SessionRequest = OnSessionRequest;
            _lobby.SessionFailed = OnSessionFailed;
            _lobby.Disconnected = OnSteamDisconnected;

            Ready = true;
            return true;
        }

        // ------------------------------------------------------------- commands

        public void Host(int maxPlayers)
        {
            if (!Ready || State != SessionState.Offline) return;
            State = SessionState.Creating;
            IsHost = true;
            _wasHostAtJoin = true;      // decided here, not inferred from Steam later
            Log("Creating lobby...");
            _lobby.CreateLobby(maxPlayers);
        }

        public void Join(CSteamID lobby)
        {
            if (!Ready || State != SessionState.Offline) return;
            State = SessionState.Joining;
            IsHost = false;
            _wasHostAtJoin = false;
            Log("Joining lobby " + lobby.m_SteamID + "...");
            _lobby.JoinLobby(lobby);
        }

        public void Leave()
        {
            if (State == SessionState.Offline) return;

            // Fired first and unconditionally: whatever needs to put the player's
            // own world back has to run before the lobby is gone, and has to run
            // on every exit path — the button, a dropped host, quitting the game.
            if (OnSessionEnded != null)
            {
                try { OnSessionEnded(); }
                catch (Exception ex) { Log("Session-end handler threw: " + ex.Message); }
            }

            // Leave() is now reached from three Steam callbacks as well as the
            // button. An exception escaping this loop would land in the game's
            // own callback pump, outside every Guard, and would leave Peers
            // populated with State still InLobby — a session that cannot be left.
            foreach (var p in Peers)
            {
                try
                {
                    Send(p, Op.Bye, null);
                    _net.CloseSession(p.Id);
                    if (OnPeerGone != null) OnPeerGone(p.Id);
                }
                catch (Exception ex) { Log("Tidy-up for " + p.Name + " threw: " + ex.Message); }
            }
            Peers.Clear();
            if (Lobby.IsValid()) _lobby.LeaveLobby(Lobby);
            Lobby = default(CSteamID);
            State = SessionState.Offline;
            IsHost = false;
            _hostId = default(CSteamID);
            _wasHostAtJoin = false;
            _emptyLobbyReads = 0;
            _lastTickAt = 0;
            Log("Left the session.");
        }

        public void OpenInviteOverlay()
        {
            if (State != SessionState.InLobby) return;
            _lobby.OpenInviteOverlay(Lobby);
        }

        public void SendChat(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            Log(SelfName + ": " + text);
            foreach (var p in Peers)
                Send(p, Op.Chat, w => w.Str(text));
        }

        // --------------------------------------------------- player snapshots

        public void BroadcastPlayerState(Game.PlayerState st, ushort seq)
        {
            if (State != SessionState.InLobby || Peers.Count == 0) return;
            using (var w = new PacketWriter(Op.PlayerState))
            {
                WritePlayerState(w, st, seq);
                var bytes = w.ToArray();
                foreach (var p in Peers)
                    _net.Send(p.Id, bytes, SteamTransport.ChannelState, false);
            }
        }

        public static void WritePlayerState(PacketWriter w, Game.PlayerState st, ushort seq)
        {
            w.U16(seq)
             .F32(st.Pos.x).F32(st.Pos.y).F32(st.Pos.z)
             .F32(st.Yaw).F32(st.Pitch)
             .F32(st.VelX).F32(st.VelZ).F32(st.Turn)
             .U8(st.Flags)
             .U32(st.Attached).U8(st.Seat);
        }

        public static Game.PlayerState ReadPlayerState(PacketReader r, out ushort seq)
        {
            seq = r.U16();
            var st = new Game.PlayerState();
            st.Pos = new UnityEngine.Vector3(r.F32(), r.F32(), r.F32());
            st.Yaw = r.F32();
            st.Pitch = r.F32();
            st.VelX = r.F32();
            st.VelZ = r.F32();
            st.Turn = r.F32();
            st.Flags = r.U8();
            st.Attached = r.U32();
            st.Seat = r.U8();
            return st;
        }

        // --------------------------------------------------- machine ownership

        public void SendMachineClaim(uint machineId, bool release = false)
        {
            var host = HostPeer();
            if (host == null) return;
            SendOn(host, Op.MachineClaim, SteamTransport.ChannelControl, true,
                   w => w.U32(machineId).Bool(release));
        }

        public void SendMachineOwner(uint machineId, ulong owner, string ownerName, CSteamID? onlyTo = null)
        {
            if (State != SessionState.InLobby) return;
            foreach (var p in Peers)
            {
                if (onlyTo.HasValue && p.Id != onlyTo.Value) continue;
                SendOn(p, Op.MachineOwner, SteamTransport.ChannelControl, true,
                       w => w.U32(machineId).U64(owner).Str(ownerName ?? ""));
            }
        }

        public void SendMachineEvent(uint machineId, uint fsmId, string evt)
        {
            if (State != SessionState.InLobby) return;
            using (var w = new PacketWriter(Op.MachineEvent))
            {
                w.U32(machineId).U32(fsmId).Str(evt);
                var bytes = w.ToArray();
                // Reliable and ordered: a dropped machine event desyncs the cabinet
                // for the rest of the round, unlike a dropped position snapshot.
                foreach (var p in Peers)
                    _net.Send(p.Id, bytes, SteamTransport.ChannelControl, true);
            }
        }

        /// <summary>
        /// Unreliable and unordered: physics is a stream of "here is the truth
        /// right now", so a dropped frame costs nothing and a late one is worse
        /// than useless.
        /// </summary>
        public void SendMachinePhysics(uint machineId, byte[] payload)
        {
            if (State != SessionState.InLobby || Peers.Count == 0 || payload == null) return;
            using (var w = new PacketWriter(Op.MachinePhysics))
            {
                w.U32(machineId).Bytes(payload);
                var bytes = w.ToArray();
                foreach (var p in Peers)
                    _net.Send(p.Id, bytes, SteamTransport.ChannelState, false);
            }
        }

        public void SendMachineScreen(uint machineId, byte[] payload)
        {
            if (State != SessionState.InLobby || Peers.Count == 0 || payload == null) return;
            using (var w = new PacketWriter(Op.MachineScreen))
            {
                w.U32(machineId).Bytes(payload);
                var bytes = w.ToArray();
                // Reliable: a score that skips a beat is fine, a score that
                // never arrives is a blank screen.
                foreach (var p in Peers)
                    _net.Send(p.Id, bytes, SteamTransport.ChannelControl, true);
            }
        }

        public void SendLooseItem(byte[] payload, bool reliable)
        {
            if (State != SessionState.InLobby || Peers.Count == 0 || payload == null) return;
            using (var w = new PacketWriter(Op.LooseItem))
            {
                w.Bytes(payload);
                var bytes = w.ToArray();
                foreach (var p in Peers)
                    _net.Send(p.Id, bytes, reliable ? SteamTransport.ChannelControl : SteamTransport.ChannelState, reliable);
            }
        }

        // ------------------------------------------------------ moving objects

        /// <summary>
        /// A vehicle's pose, in world space.
        ///
        /// World space, and not quantised into the machine's own frame like the
        /// coins are, because the whole point of a car is that it leaves the
        /// place it started. Thirty bytes at 20 Hz is 600 B/s for the one thing
        /// each player is driving, which is nothing next to the 18 KB/s a busy
        /// coin pusher already costs.
        /// </summary>
        public struct ObjectPose
        {
            public UnityEngine.Vector3 Pos;
            public UnityEngine.Quaternion Rot;
            public UnityEngine.Vector3 Vel;

            /// <summary>
            /// How many parents above the machine root this pose describes.
            ///
            /// Only the driver can work out which object actually travels — it
            /// takes watching what moves with you, and a watcher has no motion
            /// to watch. Without this they apply the pose to the machine root,
            /// which on a vehicle is a control panel bolted to it, and the cart
            /// never moves on their screen.
            /// </summary>
            public byte BodyUp;
        }

        /// <summary>
        /// Unreliable, like the rigidbody stream and for the same reason: a
        /// dropped frame is a frame of interpolation, and a late one is worse
        /// than no frame at all.
        /// </summary>
        public void SendObjectState(uint machineId, ObjectPose pose)
        {
            if (State != SessionState.InLobby || Peers.Count == 0) return;
            using (var w = new PacketWriter(Op.ObjectState))
            {
                w.U32(machineId)
                 .F32(pose.Pos.x).F32(pose.Pos.y).F32(pose.Pos.z)
                 .F32(pose.Rot.x).F32(pose.Rot.y).F32(pose.Rot.z).F32(pose.Rot.w)
                 .F32(pose.Vel.x).F32(pose.Vel.y).F32(pose.Vel.z)
                 .U8(pose.BodyUp);
                var bytes = w.ToArray();
                foreach (var p in Peers)
                    _net.Send(p.Id, bytes, SteamTransport.ChannelState, false);
            }
        }

        public void SendSeatRequest(uint machineId, bool leave)
        {
            var host = HostPeer();
            if (host == null) return;
            SendOn(host, Op.SeatRequest, SteamTransport.ChannelControl, true,
                   w => w.U32(machineId).Bool(leave));
        }

        /// <summary>Reliable: a lost seat grant leaves someone welded to a car nobody is driving.</summary>
        public void SendSeatGrant(uint machineId, ulong[] seats, CSteamID? onlyTo = null)
        {
            if (State != SessionState.InLobby || seats == null) return;
            int n = Math.Min(seats.Length, Game.Seating.MaxSeats);
            foreach (var p in Peers)
            {
                if (onlyTo.HasValue && p.Id != onlyTo.Value) continue;
                SendOn(p, Op.SeatGrant, SteamTransport.ChannelControl, true, w =>
                {
                    w.U32(machineId).U8((byte)n);
                    for (int i = 0; i < n; i++) w.U64(seats[i]);
                });
            }
        }

        private Peer HostPeer()
        {
            if (!Lobby.IsValid()) return null;
            var owner = _lobby.GetLobbyOwner(Lobby);
            for (int i = 0; i < Peers.Count; i++) if (Peers[i].Id == owner) return Peers[i];
            return null;
        }

        // -------------------------------------------------------------- wallets

        public void BroadcastWallet(int coins, int tickets, int session)
        {
            if (State != SessionState.InLobby || Peers.Count == 0) return;
            using (var w = new PacketWriter(Op.Wallet))
            {
                w.I32(coins).I32(tickets).I32(session);
                var bytes = w.ToArray();
                foreach (var p in Peers)
                    _net.Send(p.Id, bytes, SteamTransport.ChannelControl, true);
            }
        }

        // ----------------------------------------------------------- world state

        /// <summary>
        /// Typed on the wire so a bool doesn't arrive as an int and silently
        /// unlock something. 0 = int, 1 = float, 2 = bool.
        /// </summary>
        public void SendWorldVar(string name, object value, bool asHost = false, CSteamID? onlyTo = null)
        {
            if (State != SessionState.InLobby) return;

            byte kind; int i = 0; float f = 0f; bool b = false;
            if (value is bool) { kind = 2; b = (bool)value; }
            else if (value is float || value is double) { kind = 1; f = Convert.ToSingle(value); }
            else { kind = 0; i = Convert.ToInt32(value); }

            using (var w = new PacketWriter(Op.WorldVar))
            {
                w.Str(name).U8(kind).Bool(asHost).I32(i).F32(f).Bool(b);
                var bytes = w.ToArray();

                if (asHost)
                {
                    foreach (var p in Peers)
                    {
                        if (onlyTo.HasValue && p.Id != onlyTo.Value) continue;
                        _net.Send(p.Id, bytes, SteamTransport.ChannelControl, true);
                    }
                }
                else
                {
                    var host = HostPeer();
                    if (host != null) _net.Send(host.Id, bytes, SteamTransport.ChannelControl, true);
                }
            }
        }

        /// <summary>Joiner: ask the host for the island as it currently stands.</summary>
        public void RequestWorldSnapshot()
        {
            var host = HostPeer();
            if (host == null) return;
            SendOn(host, Op.WorldSync, SteamTransport.ChannelControl, true, null);
        }

        // ---------------------------------------------------------------- pump

        public void Tick()
        {
            if (!Ready || State == SessionState.Offline) return;

            _inbox.Clear();
            _net.Poll(SteamTransport.ChannelControl, _inbox);
            _net.Poll(SteamTransport.ChannelPing, _inbox);
            _net.Poll(SteamTransport.ChannelState, _inbox);
            for (int i = 0; i < _inbox.Count; i++) Handle(_inbox[i]);

            var now = Now;
            if (now >= _nextPingAt)
            {
                _nextPingAt = now + 1.0;
                var tick = _clock.ElapsedTicks;
                foreach (var p in Peers)
                {
                    p.PendingPingTick = tick;
                    SendOn(p, Op.Ping, SteamTransport.ChannelPing, false, w => w.I64(tick));
                }
            }

            // How long this frame actually took. A long scene load, or the save
            // backup running synchronously on join, can stall the main thread for
            // longer than the peer timeout — and the Stopwatch keeps counting
            // through it. Charging frozen wall-clock time against peers would
            // reap a perfectly healthy host the moment the game unfroze.
            _lastTickGap = _lastTickAt > 0 ? now - _lastTickAt : 0;
            _lastTickAt = now;

            // Credit the frozen time straight back to the peers rather than
            // relying on the watchdog happening to run on the same tick. The
            // stall may end two seconds before a scheduled pass, by which point
            // the gap has been forgotten but the silence has not.
            if (_lastTickGap > StallSeconds)
                foreach (var p in Peers) if (p.LastHeardAt > 0) p.LastHeardAt += _lastTickGap;

            if (now >= _nextLobbyWatchAt) { _nextLobbyWatchAt = now + 3.0; WatchLobby(now); }
        }

        /// <summary>How long a peer can say nothing before we treat them as gone.</summary>
        private const double PeerTimeoutSeconds = 30.0;

        /// <summary>A frame longer than this means the clock ran while the game didn't.</summary>
        private const double StallSeconds = 2.0;

        /// <summary>
        /// The callbacks cover the polite exits. This covers the rest — being
        /// dropped, kicked, or quietly starved of packets — because a guest whose
        /// session ends without anyone noticing keeps the host's progression, and
        /// that is the one failure this mod must not have.
        /// </summary>
        private void WatchLobby(double now)
        {
            if (State != SessionState.InLobby) return;

            // The owner can read as nil for a moment after entering. If it did,
            // every host-departure check downstream is inert — _hostId matches
            // nobody — so a guest would sit in the host's island with no way to
            // notice they had gone. Keep re-resolving until it answers.
            if (!_hostId.IsValid())
            {
                var owner = _lobby.GetLobbyOwner(Lobby);
                if (owner.IsValid())
                {
                    _hostId = owner;    // _wasHostAtJoin stays as Host()/Join() set it
                    Log("Resolved the host late: " + NameOf(owner));
                }
            }

            // Are we even still in this lobby? Steam answers honestly here even
            // when the chat-update callback never arrived — but one bad read
            // would tear a healthy lobby down for everyone, so it takes two.
            bool gone = !Lobby.IsValid() || _lobby.GetNumLobbyMembers(Lobby) == 0;
            _emptyLobbyReads = gone ? _emptyLobbyReads + 1 : 0;
            if (_emptyLobbyReads >= 2)
            {
                NoteEndReason("the lobby disappeared");
                    Chat.Add("[!] The lobby is gone. Session ended.");
                Log("Lobby empty or invalid on two consecutive checks — ending the session.");
                Leave();
                return;
            }
            if (gone) return;   // one bad read: wait and look again

            if (!_wasHostAtJoin && _hostId.IsValid() && !IsLobbyMember(_hostId))
            {
                NoteEndReason("the host vanished from the lobby");
                    Chat.Add("[!] The host is no longer in the lobby. Session ended.");
                Log("Host missing from lobby membership — ending the session.");
                Leave();
                return;
            }

            // Skip the silence check on the pass after a stall: the peers were
            // never given a chance to be heard from.
            if (_lastTickGap > StallSeconds) return;

            for (int i = Peers.Count - 1; i >= 0; i--)
            {
                var p = Peers[i];
                if (!p.Handshaked || p.LastHeardAt <= 0) continue;
                if (now - p.LastHeardAt < PeerTimeoutSeconds) continue;

                Log(p.Name + " timed out after " + PeerTimeoutSeconds + "s of silence.");
                Chat.Add("[!] " + p.Name + " timed out.");
                _net.CloseSession(p.Id);
                if (OnPeerGone != null) OnPeerGone(p.Id);
                Peers.RemoveAt(i);

                if (!_wasHostAtJoin && p.Id == _hostId) { Leave(); return; }
            }
        }

        // ------------------------------------------------------------ callbacks

        private void OnLobbyCreated(ulong lobbyId, bool ok)
        {
            if (!ok)
            {
                Log("Lobby creation failed.");
                State = SessionState.Offline;
                IsHost = false;
                return;
            }
            Lobby = new CSteamID(lobbyId);
            _lobby.SetLobbyData(Lobby, LobbyKeyMod, Plugin.Version);
            _lobby.SetLobbyData(Lobby, LobbyKeyHost, SelfName);
            _lobby.SetLobbyData(Lobby, LobbyKeyBuild, CompatCheck.GameHash ?? "unknown");
            _lobby.SetLobbyJoinable(Lobby, true);
            // LobbyEnter also fires for the creator, so peer setup happens there.
        }

        private void OnLobbyEnter(ulong lobbyId, uint enterResponse)
        {
            // Steam fires this for failed joins too — full, gone, banned, rate
            // limited. Taking it as success meant sitting in a lobby that does
            // not exist: Host greyed out, achievements suppressed for the rest
            // of the launch, and no explanation anywhere.
            const uint enterSuccess = 1;   // k_EChatRoomEnterResponseSuccess
            if (enterResponse != enterSuccess)
            {
                RefusedReason = "Steam wouldn't let you into that lobby (code "
                                + enterResponse + "). It may be full, "
                                + "already closed, or the ID may be stale.";
                Log("Lobby join rejected by Steam: response " + enterResponse);
                State = SessionState.Offline;
                Lobby = default(CSteamID);
                return;
            }

            Lobby = new CSteamID(lobbyId);
            State = SessionState.InLobby;
            _hostId = _lobby.GetLobbyOwner(Lobby);
            IsHost = _hostId == SelfId;

            // _wasHostAtJoin is deliberately NOT derived from the owner read.
            // Host() and Join() already know the answer with certainty, and the
            // owner can read as nil for a moment here — or, if the real host quit
            // inside that window, can read as us, latching "I was the host" onto
            // a guest and switching off every host-departure check they have.

            RefusedReason = null;
            BuildMismatch = null;

            // A different mod version is not a warning, it is an incompatibility.
            // The wire format is ours and it changes between releases, so carrying
            // on would mean two games confidently misreading each other's packets
            // — and the symptoms would look like everything except the real cause.
            // Better to stop here and say exactly what is wrong.
            var hostVersion = _lobby.GetLobbyData(Lobby, LobbyKeyMod);
            if (!IsHost && !string.IsNullOrEmpty(hostVersion) && hostVersion != Plugin.Version)
            {
                RefusedReason = "The host is running TcgMultiplayer " + hostVersion
                                + " and you have " + Plugin.Version
                                + ". You both need the same version — whoever is older should update.";
                Log("Refused to join: " + RefusedReason);
                Leave();
                return;
            }

            // A different game build is survivable — machines are matched by
            // hierarchy path, and a patch usually leaves most of them alone — so
            // this one warns rather than refuses. But it warns where it will
            // actually be seen.
            // hostBuild is lobby data, which means it is whatever the other end
            // put there — length included. Our own hash is the literal string
            // "unknown" when the assembly could not be read. Substring(0,8) on
            // either would throw, inside a Steam callback, outside every Guard,
            // and would abort the rest of this handler: no backup, no stash, no
            // handshake, and a player stuck in a lobby with no peers.
            var hostBuild = _lobby.GetLobbyData(Lobby, LobbyKeyBuild);
            var myBuild = CompatCheck.GameHash;
            if (IsRealHash(hostBuild) && IsRealHash(myBuild) && hostBuild != myBuild)
            {
                BuildMismatch = "Host is on game build " + Short(hostBuild)
                                + ", you are on " + Short(myBuild)
                                + ". Some machines or unlocks may not line up.";
                Log("WARNING: " + BuildMismatch);
                Chat.Add("[!] " + BuildMismatch);
            }

            Log("In lobby " + Lobby.m_SteamID + (IsHost ? " (host)" : " (client)"));

            // Membership and ownership can both read oddly for a second or two
            // right after entering. Give Steam time to settle before the watchdog
            // is allowed to conclude the lobby is dead.
            _nextLobbyWatchAt = Now + 10.0;

            StartedAt = DateTime.Now;
            PacketsSent = PacketsReceived = 0;
            BytesSent = BytesReceived = 0;
            PeakPeers = 0;
            EndReason = null;

            if (OnSessionBegan != null) OnSessionBegan(IsHost);

            RefreshPeers();
            foreach (var p in Peers) SendHello(p);
        }

        private static bool IsRealHash(string h)
        {
            return !string.IsNullOrEmpty(h) && h != "unknown" && h.Length >= 8;
        }

        private static string Short(string h)
        {
            return string.IsNullOrEmpty(h) ? "?" : (h.Length <= 8 ? h : h.Substring(0, 8));
        }

        private void OnLobbyChatUpdate(ulong lobbyId, ulong whoChanged, uint stateChange)
        {
            if (new CSteamID(lobbyId) != Lobby) return;
            var who = new CSteamID(whoChanged);

            const uint entered = 1;   // k_EChatMemberStateChangeEntered
            if ((stateChange & entered) != 0)
            {
                var p = Track(who);
                if (p != null) { Log(p.Name + " joined."); SendHello(p); }
            }
            else
            {
                var p = Find(who);
                if (p != null)
                {
                    Log(p.Name + " left.");
                    _net.CloseSession(p.Id);
                    if (OnPeerGone != null) OnPeerGone(p.Id);
                    Peers.Remove(p);
                }

                // There is no host migration, and pretending otherwise would be
                // worse than not having it: machine ownership and the island's
                // progression are both the host's to arbitrate, and Steam handing
                // a guest the lobby doesn't hand them the world. So when the host
                // goes, the session ends — cleanly, which is what puts everyone's
                // own progression back.
                //
                // Compared against _wasHostAtJoin, not IsHost. With three or more
                // players, Steam promotes a guest the moment the host leaves; if
                // another member's update was processed first, IsHost had already
                // flipped to true and this check silently stopped firing — leaving
                // that player holding the host's island with no restore pending,
                // and handing it out to the next joiner as though it were theirs.
                if (!_wasHostAtJoin && who == _hostId)
                {
                    NoteEndReason("the host left");
                    Chat.Add("[!] The host left. Ending the session.");
                    Log("Host left the lobby — leaving.");
                    Leave();
                    return;
                }
            }
            IsHost = _lobby.GetLobbyOwner(Lobby) == SelfId;
        }

        /// <summary>
        /// Steam dropped us. The lobby is gone whether or not anything told us
        /// politely, so this has to end the session — it is the path that leaves
        /// a guest holding someone else's progression if it is missed.
        /// </summary>
        private void OnSteamDisconnected()
        {
            if (State == SessionState.Offline) return;
            NoteEndReason("lost the connection to Steam");
                    Chat.Add("[!] Lost the connection to Steam. Session ended.");
            Log("SteamServersDisconnected — ending the session.");
            Leave();
        }

        private void OnJoinRequested(CSteamID lobby)
        {
            Log("Join request for lobby " + lobby.m_SteamID);
            if (State != SessionState.Offline) Leave();
            Join(lobby);
        }

        private void OnSessionRequest(CSteamID peer)
        {
            // Only talk to people who are actually in our lobby.
            if (Find(peer) == null && !IsLobbyMember(peer))
            {
                Plugin.Warn("Refused session from non-member " + peer.m_SteamID);
                return;
            }
            _net.AcceptSession(peer);
            Track(peer);
        }

        private void OnSessionFailed(CSteamID peer, string why)
        {
            Log("Session failed with " + NameOf(peer) + ": " + why);

            // Losing the transport to the host is losing the session, even though
            // Steam's lobby membership can outlive it by minutes. Left alone, a
            // guest sits in a dead session with the host's island applied and
            // nothing to trigger the restore.
            if (State != SessionState.Offline && !_wasHostAtJoin && peer == _hostId)
            {
                NoteEndReason("lost the connection to the host");
                    Chat.Add("[!] Lost the connection to the host. Session ended.");
                Leave();
            }
        }

        // ------------------------------------------------------------- messages

        private void Handle(SteamTransport.Received r)
        {
            var peer = Track(r.From);
            if (peer == null) return;
            peer.LastHeardAt = Now;
            PacketsReceived++;
            BytesReceived += r.Data != null ? r.Data.Length : 0;
            if (Peers.Count > PeakPeers) PeakPeers = Peers.Count;

            try
            {
                using (var pr = new PacketReader(r.Data))
                {
                    switch (pr.Op)
                    {
                        case Op.Hello:
                            peer.Name = pr.Str();
                            peer.ModVersion = pr.Str();
                            peer.Handshaked = true;
                            Log("Handshake with " + peer.Name + " (mod " + peer.ModVersion + ")");
                            SendOn(peer, Op.HelloAck, SteamTransport.ChannelControl, true,
                                   w => w.Str(SelfName).Str(Plugin.Version));
                            break;

                        case Op.HelloAck:
                            peer.Name = pr.Str();
                            peer.ModVersion = pr.Str();
                            peer.Handshaked = true;
                            Log("Connected to " + peer.Name + " (mod " + peer.ModVersion + ")");
                            // Now that we can talk, pull the host's island down so we
                            // arrive in their world rather than our own.
                            if (!_wasHostAtJoin) RequestWorldSnapshot();
                            break;

                        case Op.Ping:
                        {
                            long tick = pr.I64();
                            SendOn(peer, Op.Pong, SteamTransport.ChannelPing, false, w => w.I64(tick));
                            break;
                        }

                        case Op.Pong:
                        {
                            long tick = pr.I64();
                            if (tick == peer.PendingPingTick)
                            {
                                double ms = (_clock.ElapsedTicks - tick) * 1000.0 / Stopwatch.Frequency;
                                peer.RttMs = peer.RttMs < 0 ? (float)ms : peer.RttMs * 0.7f + (float)ms * 0.3f;
                            }
                            break;
                        }

                        case Op.Chat:
                            Log(peer.Name + ": " + pr.Str());
                            break;

                        case Op.PlayerState:
                        {
                            ushort seq;
                            var st = ReadPlayerState(pr, out seq);
                            // Snapshots are unreliable and can arrive out of order;
                            // drop anything older than the newest we've applied.
                            if (!Newer(seq, peer.LastStateSeq)) break;
                            peer.LastStateSeq = seq;
                            if (OnPlayerState != null) OnPlayerState(peer.Id, st, seq);
                            break;
                        }

                        case Op.MachineClaim:
                        {
                            uint mid = pr.U32();
                            bool rel = pr.Bool();
                            if (OnMachineClaim != null) OnMachineClaim(peer.Id, mid, rel);
                            break;
                        }

                        case Op.MachineOwner:
                        {
                            uint mid = pr.U32();
                            ulong owner = pr.U64();
                            string oname = pr.Str();
                            if (OnMachineOwner != null) OnMachineOwner(mid, owner, oname);
                            break;
                        }

                        case Op.MachineEvent:
                        {
                            uint mid = pr.U32();
                            uint fid = pr.U32();
                            string evt = pr.Str();
                            if (OnMachineEvent != null) OnMachineEvent(peer.Id, mid, fid, evt);
                            break;
                        }

                        case Op.Wallet:
                            peer.Coins = pr.I32();
                            peer.Tickets = pr.I32();
                            peer.TicketsSession = pr.I32();
                            peer.HasWallet = true;
                            break;

                        case Op.WorldVar:
                        {
                            string name = pr.Str();
                            byte kind = pr.U8();
                            bool asHost = pr.Bool();
                            int iv = pr.I32();
                            float fv = pr.F32();
                            bool bv = pr.Bool();
                            object val = kind == 2 ? (object)bv : kind == 1 ? (object)fv : (object)iv;
                            if (OnWorldVar != null) OnWorldVar(peer.Id, name, val, asHost);
                            break;
                        }

                        case Op.WorldSync:
                            if (OnWorldSnapshotRequest != null) OnWorldSnapshotRequest(peer.Id);
                            break;

                        case Op.MachinePhysics:
                        {
                            uint mid = pr.U32();
                            var payload = pr.Bytes();
                            if (OnMachinePhysics != null) OnMachinePhysics(peer.Id, mid, payload);
                            break;
                        }

                        case Op.MachineScreen:
                        {
                            uint mid = pr.U32();
                            var payload = pr.Bytes();
                            if (OnMachineScreen != null) OnMachineScreen(peer.Id, mid, payload);
                            break;
                        }

                        case Op.LooseItem:
                        {
                            var payload = pr.Bytes();
                            if (OnLooseItem != null) OnLooseItem(peer.Id, payload);
                            break;
                        }

                        case Op.ObjectState:
                        {
                            uint mid = pr.U32();
                            var pose = new ObjectPose();
                            pose.Pos = new UnityEngine.Vector3(pr.F32(), pr.F32(), pr.F32());
                            pose.Rot = new UnityEngine.Quaternion(pr.F32(), pr.F32(), pr.F32(), pr.F32());
                            pose.Vel = new UnityEngine.Vector3(pr.F32(), pr.F32(), pr.F32());
                            pose.BodyUp = pr.U8();
                            if (OnObjectState != null) OnObjectState(peer.Id, mid, pose);
                            break;
                        }

                        case Op.SeatRequest:
                        {
                            uint mid = pr.U32();
                            bool leave = pr.Bool();
                            if (OnSeatRequest != null) OnSeatRequest(peer.Id, mid, leave);
                            break;
                        }

                        case Op.SeatGrant:
                        {
                            uint mid = pr.U32();
                            int n = pr.U8();
                            if (n > Game.Seating.MaxSeats) { peer.BadPackets++; break; }
                            var seats = new ulong[n];
                            for (int i = 0; i < n; i++) seats[i] = pr.U64();
                            if (OnSeatGrant != null) OnSeatGrant(mid, seats);
                            break;
                        }

                        case Op.Bye:
                            // Only from someone we actually shook hands with. A
                            // single stray byte decoding as this opcode would
                            // otherwise drop a live peer — and misdecoded traffic
                            // is exactly what a version mismatch produces.
                            if (!peer.Handshaked) break;
                            Log(peer.Name + " disconnected.");
                            _net.CloseSession(peer.Id);
                            if (OnPeerGone != null) OnPeerGone(peer.Id);
                            Peers.Remove(peer);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                // Rate-limited on purpose. A peer sending garbage — broken, on a
                // different version, or being deliberate — could otherwise write
                // a log line per packet for as long as they felt like it, which
                // fills the disk and buries the entry that would explain it.
                peer.BadPackets++;
                if (peer.BadPackets <= 10)
                    Plugin.Warn("Malformed packet from " + peer.Name + ": " + ex.Message);
                else if (peer.BadPackets == 11)
                    Plugin.Warn("Further malformed packets from " + peer.Name
                                + " will not be logged. They are probably on a different version.");
            }
        }

        private void SendHello(Peer p)
        {
            _net.AcceptSession(p.Id);
            SendOn(p, Op.Hello, SteamTransport.ChannelControl, true,
                   w => w.Str(SelfName).Str(Plugin.Version));
        }

        private void Send(Peer p, Op op, Action<PacketWriter> fill)
        {
            SendOn(p, op, SteamTransport.ChannelControl, true, fill);
        }

        private void SendOn(Peer p, Op op, int channel, bool reliable, Action<PacketWriter> fill)
        {
            using (var w = new PacketWriter(op))
            {
                if (fill != null) fill(w);
                var bytes = w.ToArray();
                PacketsSent++;
                BytesSent += bytes.Length;
                _net.Send(p.Id, bytes, channel, reliable);
            }
        }

        // --------------------------------------------------------------- peers

        private void RefreshPeers()
        {
            int n = _lobby.GetNumLobbyMembers(Lobby);
            for (int i = 0; i < n; i++)
            {
                var m = _lobby.GetLobbyMemberByIndex(Lobby, i);
                if (m != SelfId) Track(m);
            }
        }

        private bool IsLobbyMember(CSteamID who)
        {
            if (!Lobby.IsValid()) return false;
            int n = _lobby.GetNumLobbyMembers(Lobby);
            for (int i = 0; i < n; i++)
                if (_lobby.GetLobbyMemberByIndex(Lobby, i) == who) return true;
            return false;
        }

        private Peer Find(CSteamID id)
        {
            for (int i = 0; i < Peers.Count; i++) if (Peers[i].Id == id) return Peers[i];
            return null;
        }

        private Peer Track(CSteamID id)
        {
            if (id == SelfId || !id.IsValid()) return null;
            var p = Find(id);
            if (p != null) return p;
            p = new Peer { Id = id, Name = NameOf(id), LastHeardAt = Now };
            Peers.Add(p);
            return p;
        }

        /// <summary>Sequence comparison that survives the ushort wrap.</summary>
        private static bool Newer(ushort a, ushort b)
        {
            return (ushort)(a - b) < 0x8000;
        }

        private string NameOf(CSteamID id)
        {
            try { return _lobby.NameOf(id); }
            catch { return id.m_SteamID.ToString(); }
        }

        private void Log(string line)
        {
            Chat.Add(line);
            if (Chat.Count > 200) Chat.RemoveRange(0, Chat.Count - 200);
            Plugin.Log(line);
        }
    }
}
