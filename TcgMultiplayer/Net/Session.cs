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

        private Callback<LobbyEnter_t> _cbLobbyEnter;
        private Callback<LobbyChatUpdate_t> _cbLobbyChat;
        private Callback<GameLobbyJoinRequested_t> _cbJoinRequested;
        private Callback<SteamNetworkingMessagesSessionRequest_t> _cbSessionRequest;
        private Callback<SteamNetworkingMessagesSessionFailed_t> _cbSessionFailed;
        private CallResult<LobbyCreated_t> _crLobbyCreated;

        private readonly List<SteamTransport.Received> _inbox = new List<SteamTransport.Received>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _nextPingAt;

        public bool Ready { get; private set; }

        // ---------------------------------------------------------------- setup

        public bool Init()
        {
            if (!SteamBridge.Initialized)
            {
                Plugin.Warn("Steam is not initialised yet — will retry.");
                return false;
            }

            try
            {
                SelfId = SteamUser.GetSteamID();
                SelfName = SteamFriends.GetPersonaName();

                _cbLobbyEnter = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
                _cbLobbyChat = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
                _cbJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequested);
                _cbSessionRequest = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
                _cbSessionFailed = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(OnSessionFailed);
                _crLobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);

                Ready = true;
                Plugin.Log("Steam ready as " + SelfName + " (" + SelfId.m_SteamID + ")");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Warn("Steam init failed: " + ex);
                return false;
            }
        }

        // ------------------------------------------------------------- commands

        public void Host(int maxPlayers)
        {
            if (!Ready || State != SessionState.Offline) return;
            State = SessionState.Creating;
            IsHost = true;
            Log("Creating lobby...");
            var call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, maxPlayers);
            _crLobbyCreated.Set(call);
        }

        public void Join(CSteamID lobby)
        {
            if (!Ready || State != SessionState.Offline) return;
            State = SessionState.Joining;
            IsHost = false;
            Log("Joining lobby " + lobby.m_SteamID + "...");
            SteamMatchmaking.JoinLobby(lobby);
        }

        public void Leave()
        {
            if (State == SessionState.Offline) return;
            foreach (var p in Peers)
            {
                Send(p, Op.Bye, null);
                SteamTransport.CloseSession(p.Id);
                if (OnPeerGone != null) OnPeerGone(p.Id);
            }
            Peers.Clear();
            if (Lobby.IsValid()) SteamMatchmaking.LeaveLobby(Lobby);
            Lobby = default(CSteamID);
            State = SessionState.Offline;
            IsHost = false;
            Log("Left the session.");
        }

        public void OpenInviteOverlay()
        {
            if (State != SessionState.InLobby) return;
            SteamFriends.ActivateGameOverlayInviteDialog(Lobby);
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
                    SteamTransport.Send(p.Id, bytes, SteamTransport.ChannelState, false);
            }
        }

        public static void WritePlayerState(PacketWriter w, Game.PlayerState st, ushort seq)
        {
            w.U16(seq)
             .F32(st.Pos.x).F32(st.Pos.y).F32(st.Pos.z)
             .F32(st.Yaw).F32(st.Pitch)
             .F32(st.VelX).F32(st.VelZ).F32(st.Turn)
             .U8(st.Flags);
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
                    SteamTransport.Send(p.Id, bytes, SteamTransport.ChannelControl, true);
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
                    SteamTransport.Send(p.Id, bytes, SteamTransport.ChannelState, false);
            }
        }

        private Peer HostPeer()
        {
            if (!Lobby.IsValid()) return null;
            var owner = SteamMatchmaking.GetLobbyOwner(Lobby);
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
                    SteamTransport.Send(p.Id, bytes, SteamTransport.ChannelControl, true);
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
                        SteamTransport.Send(p.Id, bytes, SteamTransport.ChannelControl, true);
                    }
                }
                else
                {
                    var host = HostPeer();
                    if (host != null) SteamTransport.Send(host.Id, bytes, SteamTransport.ChannelControl, true);
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
            SteamTransport.Poll(SteamTransport.ChannelControl, _inbox);
            SteamTransport.Poll(SteamTransport.ChannelPing, _inbox);
            SteamTransport.Poll(SteamTransport.ChannelState, _inbox);
            for (int i = 0; i < _inbox.Count; i++) Handle(_inbox[i]);

            var now = _clock.Elapsed.TotalSeconds;
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
        }

        // ------------------------------------------------------------ callbacks

        private void OnLobbyCreated(LobbyCreated_t cb, bool ioFailure)
        {
            if (ioFailure || cb.m_eResult != EResult.k_EResultOK)
            {
                Log("Lobby creation failed: " + (ioFailure ? "IO failure" : cb.m_eResult.ToString()));
                State = SessionState.Offline;
                IsHost = false;
                return;
            }
            Lobby = new CSteamID(cb.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(Lobby, LobbyKeyMod, Plugin.Version);
            SteamMatchmaking.SetLobbyData(Lobby, LobbyKeyHost, SelfName);
            SteamMatchmaking.SetLobbyJoinable(Lobby, true);
            // LobbyEnter also fires for the creator, so peer setup happens there.
        }

        private void OnLobbyEnter(LobbyEnter_t cb)
        {
            Lobby = new CSteamID(cb.m_ulSteamIDLobby);
            State = SessionState.InLobby;
            IsHost = SteamMatchmaking.GetLobbyOwner(Lobby) == SelfId;

            var hostVersion = SteamMatchmaking.GetLobbyData(Lobby, LobbyKeyMod);
            if (!string.IsNullOrEmpty(hostVersion) && hostVersion != Plugin.Version)
                Log("WARNING: host runs TcgMultiplayer " + hostVersion + ", you run " + Plugin.Version);

            Log("In lobby " + Lobby.m_SteamID + (IsHost ? " (host)" : " (client)"));
            RefreshPeers();
            foreach (var p in Peers) SendHello(p);
        }

        private void OnLobbyChatUpdate(LobbyChatUpdate_t cb)
        {
            if (new CSteamID(cb.m_ulSteamIDLobby) != Lobby) return;
            var who = new CSteamID(cb.m_ulSteamIDUserChanged);

            const uint entered = 1;   // k_EChatMemberStateChangeEntered
            if ((cb.m_rgfChatMemberStateChange & entered) != 0)
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
                    SteamTransport.CloseSession(p.Id);
                    if (OnPeerGone != null) OnPeerGone(p.Id);
                    Peers.Remove(p);
                }
            }
            IsHost = SteamMatchmaking.GetLobbyOwner(Lobby) == SelfId;
        }

        private void OnJoinRequested(GameLobbyJoinRequested_t cb)
        {
            Log("Join request from " + SteamFriends.GetFriendPersonaName(cb.m_steamIDFriend));
            if (State != SessionState.Offline) Leave();
            Join(cb.m_steamIDLobby);
        }

        private void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t cb)
        {
            var peer = cb.m_identityRemote.GetSteamID();
            // Only talk to people who are actually in our lobby.
            if (Find(peer) == null && !IsLobbyMember(peer))
            {
                Plugin.Warn("Refused session from non-member " + peer.m_SteamID);
                return;
            }
            SteamTransport.AcceptSession(peer);
            Track(peer);
        }

        private void OnSessionFailed(SteamNetworkingMessagesSessionFailed_t cb)
        {
            var peer = cb.m_info.m_identityRemote.GetSteamID();
            Log("Session failed with " + NameOf(peer) + ": " + cb.m_info.m_eEndReason);
        }

        // ------------------------------------------------------------- messages

        private void Handle(SteamTransport.Received r)
        {
            var peer = Track(r.From);
            if (peer == null) return;
            peer.LastHeardAt = _clock.Elapsed.TotalSeconds;

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
                            if (!IsHost) RequestWorldSnapshot();
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

                        case Op.Bye:
                            Log(peer.Name + " disconnected.");
                            SteamTransport.CloseSession(peer.Id);
                            if (OnPeerGone != null) OnPeerGone(peer.Id);
                            Peers.Remove(peer);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Warn("Malformed packet from " + peer.Name + ": " + ex.Message);
            }
        }

        private void SendHello(Peer p)
        {
            SteamTransport.AcceptSession(p.Id);
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
                SteamTransport.Send(p.Id, w.ToArray(), channel, reliable);
            }
        }

        // --------------------------------------------------------------- peers

        private void RefreshPeers()
        {
            int n = SteamMatchmaking.GetNumLobbyMembers(Lobby);
            for (int i = 0; i < n; i++)
            {
                var m = SteamMatchmaking.GetLobbyMemberByIndex(Lobby, i);
                if (m != SelfId) Track(m);
            }
        }

        private bool IsLobbyMember(CSteamID who)
        {
            if (!Lobby.IsValid()) return false;
            int n = SteamMatchmaking.GetNumLobbyMembers(Lobby);
            for (int i = 0; i < n; i++)
                if (SteamMatchmaking.GetLobbyMemberByIndex(Lobby, i) == who) return true;
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
            p = new Peer { Id = id, Name = NameOf(id), LastHeardAt = _clock.Elapsed.TotalSeconds };
            Peers.Add(p);
            return p;
        }

        /// <summary>Sequence comparison that survives the ushort wrap.</summary>
        private static bool Newer(ushort a, ushort b)
        {
            return (ushort)(a - b) < 0x8000;
        }

        private static string NameOf(CSteamID id)
        {
            try
            {
                var n = SteamFriends.GetFriendPersonaName(id);
                return string.IsNullOrEmpty(n) ? id.m_SteamID.ToString() : n;
            }
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
