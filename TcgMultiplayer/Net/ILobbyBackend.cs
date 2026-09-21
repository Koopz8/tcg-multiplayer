using System;
using Steamworks;

namespace TcgMultiplayer.Net
{
    /// <summary>
    /// Everything a Session needs from Steam that isn't bytes on the wire: who
    /// am I, who else is here, who owns this lobby, and the handful of events
    /// that say when any of that changed.
    ///
    /// The events are deliberately plain — ulong and uint rather than Steam's
    /// callback structs — so a fake can raise them. The Steam implementation
    /// registers the real callbacks and forwards, unchanged.
    /// </summary>
    public interface ILobbyBackend
    {
        bool Ready { get; }
        CSteamID SelfId { get; }
        string SelfName { get; }
        string NameOf(CSteamID id);

        /// <summary>Wire up callbacks. Returns false if Steam isn't up yet.</summary>
        bool Start();

        void CreateLobby(int maxPlayers);
        void JoinLobby(CSteamID lobby);
        void LeaveLobby(CSteamID lobby);
        void OpenInviteOverlay(CSteamID lobby);

        void SetLobbyData(CSteamID lobby, string key, string value);
        string GetLobbyData(CSteamID lobby, string key);
        void SetLobbyJoinable(CSteamID lobby, bool joinable);

        CSteamID GetLobbyOwner(CSteamID lobby);
        int GetNumLobbyMembers(CSteamID lobby);
        CSteamID GetLobbyMemberByIndex(CSteamID lobby, int index);

        /// <summary>lobbyId, ok. ok=false means creation failed.</summary>
        Action<ulong, bool> LobbyCreated { get; set; }
        /// <summary>lobbyId, chat-room-enter response. 1 = success, anything else is a refusal.</summary>
        Action<ulong, uint> LobbyEntered { get; set; }
        /// <summary>lobbyId, who changed, state-change flags. Bit 1 = entered.</summary>
        Action<ulong, ulong, uint> LobbyChatUpdate { get; set; }
        /// <summary>A friend asked us to join their lobby.</summary>
        Action<CSteamID> JoinRequested { get; set; }
        /// <summary>A peer wants to open a messaging session with us.</summary>
        Action<CSteamID> SessionRequest { get; set; }
        /// <summary>The messaging session with a peer failed. Second arg is the reason, for the log.</summary>
        Action<CSteamID, string> SessionFailed { get; set; }
        /// <summary>Steam itself dropped us.</summary>
        Action Disconnected { get; set; }
    }

    /// <summary>
    /// The real thing. Every method is the call Session used to make inline —
    /// moved, not changed.
    /// </summary>
    public sealed class SteamLobbyBackend : ILobbyBackend
    {
        private Callback<LobbyEnter_t> _cbLobbyEnter;
        private Callback<LobbyChatUpdate_t> _cbLobbyChat;
        private Callback<GameLobbyJoinRequested_t> _cbJoinRequested;
        private Callback<SteamNetworkingMessagesSessionRequest_t> _cbSessionRequest;
        private Callback<SteamNetworkingMessagesSessionFailed_t> _cbSessionFailed;
        private Callback<SteamServersDisconnected_t> _cbDisconnected;
        private CallResult<LobbyCreated_t> _crLobbyCreated;

        public bool Ready { get; private set; }
        public CSteamID SelfId { get; private set; }
        public string SelfName { get; private set; }

        public Action<ulong, bool> LobbyCreated { get; set; }
        public Action<ulong, uint> LobbyEntered { get; set; }
        public Action<ulong, ulong, uint> LobbyChatUpdate { get; set; }
        public Action<CSteamID> JoinRequested { get; set; }
        public Action<CSteamID> SessionRequest { get; set; }
        public Action<CSteamID, string> SessionFailed { get; set; }
        public Action Disconnected { get; set; }

        public bool Start()
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

                _cbLobbyEnter = Callback<LobbyEnter_t>.Create(cb =>
                {
                    if (LobbyEntered != null) LobbyEntered(cb.m_ulSteamIDLobby, cb.m_EChatRoomEnterResponse);
                });
                _cbLobbyChat = Callback<LobbyChatUpdate_t>.Create(cb =>
                {
                    if (LobbyChatUpdate != null)
                        LobbyChatUpdate(cb.m_ulSteamIDLobby, cb.m_ulSteamIDUserChanged, cb.m_rgfChatMemberStateChange);
                });
                _cbJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(cb =>
                {
                    if (JoinRequested != null) JoinRequested(cb.m_steamIDLobby);
                });
                _cbSessionRequest = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(cb =>
                {
                    if (SessionRequest != null) SessionRequest(cb.m_identityRemote.GetSteamID());
                });
                _cbSessionFailed = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(cb =>
                {
                    if (SessionFailed != null)
                        SessionFailed(cb.m_info.m_identityRemote.GetSteamID(), cb.m_info.m_eEndReason.ToString());
                });
                _cbDisconnected = Callback<SteamServersDisconnected_t>.Create(cb =>
                {
                    if (Disconnected != null) Disconnected();
                });
                _crLobbyCreated = CallResult<LobbyCreated_t>.Create((cb, ioFailure) =>
                {
                    bool ok = !ioFailure && cb.m_eResult == EResult.k_EResultOK;
                    if (LobbyCreated != null) LobbyCreated(cb.m_ulSteamIDLobby, ok);
                });

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

        public void CreateLobby(int maxPlayers)
        {
            var call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, maxPlayers);
            _crLobbyCreated.Set(call);
        }

        public void JoinLobby(CSteamID lobby) { SteamMatchmaking.JoinLobby(lobby); }
        public void LeaveLobby(CSteamID lobby) { SteamMatchmaking.LeaveLobby(lobby); }
        public void OpenInviteOverlay(CSteamID lobby) { SteamFriends.ActivateGameOverlayInviteDialog(lobby); }

        public void SetLobbyData(CSteamID lobby, string key, string value) { SteamMatchmaking.SetLobbyData(lobby, key, value); }
        public string GetLobbyData(CSteamID lobby, string key) { return SteamMatchmaking.GetLobbyData(lobby, key); }
        public void SetLobbyJoinable(CSteamID lobby, bool joinable) { SteamMatchmaking.SetLobbyJoinable(lobby, joinable); }

        public CSteamID GetLobbyOwner(CSteamID lobby) { return SteamMatchmaking.GetLobbyOwner(lobby); }
        public int GetNumLobbyMembers(CSteamID lobby) { return SteamMatchmaking.GetNumLobbyMembers(lobby); }
        public CSteamID GetLobbyMemberByIndex(CSteamID lobby, int index) { return SteamMatchmaking.GetLobbyMemberByIndex(lobby, index); }

        public string NameOf(CSteamID id)
        {
            try
            {
                var n = SteamFriends.GetFriendPersonaName(id);
                return string.IsNullOrEmpty(n) ? id.m_SteamID.ToString() : n;
            }
            catch { return id.m_SteamID.ToString(); }
        }
    }
}
