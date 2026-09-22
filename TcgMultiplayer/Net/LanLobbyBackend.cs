using System;
using System.Collections.Generic;
using System.IO;
using Steamworks;

namespace TcgMultiplayer.Net
{
    /// <summary>
    /// Everything Steam would tell a Session about who else is here, worked out
    /// from a folder of small files instead.
    ///
    /// There is no server and no handshake to write: each window writes one file
    /// saying "I am slot N, I was alive at T", and reads the others'. One writer
    /// per file, so there is nothing to lock. Liveness is the heartbeat going
    /// stale rather than a goodbye message, because the window you most want to
    /// be able to test closing is the one that crashed without saying anything.
    ///
    /// Slot 0 owns the lobby. That is not an election, it is just whichever
    /// window you opened first — and it is stable for the same reason the slot
    /// is: it was claimed by binding a port.
    /// </summary>
    public sealed class LanLobbyBackend : ILobbyBackend
    {
        /// <summary>Fixed: there is only ever one local lobby, so it needs no id negotiation.</summary>
        public static readonly CSteamID TheLobby =
            new CSteamID(new AccountID_t(LanAddressing.BaseAccount),
                         (uint)EChatSteamIDInstanceFlags.k_EChatInstanceFlagLobby,
                         EUniverse.k_EUniversePublic,
                         EAccountType.k_EAccountTypeChat);

        private readonly LanTransport _net;
        private readonly string _dir;
        private readonly Dictionary<uint, LanMember> _live = new Dictionary<uint, LanMember>();
        private readonly List<uint> _order = new List<uint>();
        private readonly List<uint> _scratch = new List<uint>();

        private long _nextBeatAt;
        private long _nextScanAt;
        private bool _inLobby;

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

        public int Slot { get { return _net != null ? _net.Slot : -1; } }
        public bool IsHostSlot { get { return LanAddressing.IsHostSlot(Slot); } }
        public string Folder { get { return _dir; } }
        public int LiveWindows { get { return _live.Count; } }

        public LanLobbyBackend(LanTransport net, string folder)
        {
            _net = net;
            _dir = folder;
        }

        public bool Start()
        {
            if (_net == null || !_net.Bound) return false;

            try
            {
                Directory.CreateDirectory(_dir);
                SelfId = _net.SelfId;
                SelfName = LanAddressing.NameForSlot(_net.Slot);

                Sweep(true);
                Beat();
                Ready = true;
                Plugin.Log("Local lobby ready as " + SelfName + " (" + SelfId.m_SteamID + "), folder " + _dir);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Warn("Local lobby could not start: " + ex.Message);
                return false;
            }
        }

        // ------------------------------------------------------------- ticking

        /// <summary>
        /// Called every frame by the plugin. Cheap: the heartbeat writes once a
        /// second and the scan reads once a second, and neither does anything at
        /// all in between.
        /// </summary>
        public void Tick()
        {
            if (!Ready) return;

            long now = LanMembership.NowMs();
            if (now >= _nextBeatAt) Beat();
            if (now >= _nextScanAt) Sweep(false);
        }

        private void Beat()
        {
            _nextBeatAt = LanMembership.NowMs() + LanMembership.HeartbeatMs;
            try
            {
                var m = new LanMember
                {
                    Account = _net.Account,
                    Name = SelfName,
                    Heartbeat = LanMembership.NowMs(),
                };
                File.WriteAllText(Path.Combine(_dir, LanMembership.FileNameFor(_net.Account)),
                                  LanMembership.Encode(m));
            }
            catch { /* a missed beat is caught by the next one */ }
        }

        /// <summary>
        /// Read everyone's file and work out who arrived and who went away.
        /// Raises the same two events Steam would, so nothing above this has to
        /// know it isn't talking to Steam.
        /// </summary>
        private void Sweep(bool quiet)
        {
            _nextScanAt = LanMembership.NowMs() + LanMembership.HeartbeatMs;

            string[] files;
            try { files = Directory.GetFiles(_dir, "member_*.txt"); }
            catch { return; }

            long now = LanMembership.NowMs();
            var seen = new Dictionary<uint, LanMember>();

            for (int i = 0; i < files.Length; i++)
            {
                string text;
                try { text = File.ReadAllText(files[i]); }
                catch { continue; }          // being rewritten right now; next sweep

                LanMember m;
                if (!LanMembership.TryDecode(text, out m)) continue;
                if (!LanMembership.IsLive(m.Heartbeat, now))
                {
                    // Tidy up after a window that crashed. Best effort: if this
                    // fails the stale entry is simply ignored again next sweep.
                    try { File.Delete(files[i]); } catch { }
                    continue;
                }
                seen[m.Account] = m;
            }

            // Arrivals.
            foreach (var kv in seen)
            {
                if (_live.ContainsKey(kv.Key)) { _live[kv.Key] = kv.Value; continue; }

                _live[kv.Key] = kv.Value;
                if (!_order.Contains(kv.Key)) _order.Add(kv.Key);
                _order.Sort();

                if (quiet || kv.Key == _net.Account) continue;
                if (!_inLobby) continue;
                if (LobbyChatUpdate != null)
                    LobbyChatUpdate(TheLobby.m_SteamID, IdOf(kv.Key).m_SteamID, 1u);   // bit 1 = entered
            }

            // Departures.
            _scratch.Clear();
            foreach (var kv in _live) if (!seen.ContainsKey(kv.Key)) _scratch.Add(kv.Key);

            for (int i = 0; i < _scratch.Count; i++)
            {
                uint gone = _scratch[i];
                _live.Remove(gone);
                _order.Remove(gone);

                if (quiet) continue;
                if (_inLobby && LobbyChatUpdate != null)
                    LobbyChatUpdate(TheLobby.m_SteamID, IdOf(gone).m_SteamID, 2u);     // left
            }
        }

        private static CSteamID IdOf(uint account)
        {
            return new CSteamID(new AccountID_t(account),
                                EUniverse.k_EUniversePublic,
                                EAccountType.k_EAccountTypeIndividual);
        }

        // ---------------------------------------------------------- ILobbyBackend

        public void CreateLobby(int maxPlayers)
        {
            // Nothing to create — the lobby is a fixed id and a folder that
            // already exists. Answered on the spot rather than next frame,
            // because the caller is a state machine expecting a callback and
            // there is nothing here that can fail slowly.
            _inLobby = true;
            if (LobbyCreated != null) LobbyCreated(TheLobby.m_SteamID, true);
            if (LobbyEntered != null) LobbyEntered(TheLobby.m_SteamID, 1u);
        }

        public void JoinLobby(CSteamID lobby)
        {
            _inLobby = true;
            Sweep(true);
            if (LobbyEntered != null) LobbyEntered(TheLobby.m_SteamID, 1u);

            // Tell the joiner about everyone already here. Steam delivers these
            // as part of entering; the folder has to be read for them.
            foreach (var kv in _live)
            {
                if (kv.Key == _net.Account) continue;
                if (LobbyChatUpdate != null)
                    LobbyChatUpdate(TheLobby.m_SteamID, IdOf(kv.Key).m_SteamID, 1u);
            }
        }

        public void LeaveLobby(CSteamID lobby)
        {
            _inLobby = false;
            try { File.Delete(Path.Combine(_dir, LanMembership.FileNameFor(_net.Account))); }
            catch { }
        }

        public void OpenInviteOverlay(CSteamID lobby)
        {
            Plugin.Log("Local test mode: there is nobody to invite — open a second window instead.");
        }

        // Lobby data is written only by the host and read by everyone, so it has
        // exactly one writer too.
        public void SetLobbyData(CSteamID lobby, string key, string value)
        {
            try { File.WriteAllText(DataPath(key), value ?? ""); } catch { }
        }

        public string GetLobbyData(CSteamID lobby, string key)
        {
            try { return File.Exists(DataPath(key)) ? File.ReadAllText(DataPath(key)) : ""; }
            catch { return ""; }
        }

        private string DataPath(string key)
        {
            var safe = key ?? "k";
            foreach (var c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            return Path.Combine(_dir, "data_" + safe + ".txt");
        }

        public void SetLobbyJoinable(CSteamID lobby, bool joinable) { }

        public CSteamID GetLobbyOwner(CSteamID lobby)
        {
            // Slot 0 if it is here; otherwise the lowest slot that is, so the
            // session still has an arbitrator when window 1 is closed first.
            for (int slot = 0; slot < LanAddressing.MaxSlots; slot++)
            {
                uint acc = LanAddressing.AccountForSlot(slot);
                if (_live.ContainsKey(acc)) return IdOf(acc);
            }
            return SelfId;
        }

        public int GetNumLobbyMembers(CSteamID lobby) { return _order.Count; }

        public CSteamID GetLobbyMemberByIndex(CSteamID lobby, int index)
        {
            if (index < 0 || index >= _order.Count) return default(CSteamID);
            return IdOf(_order[index]);
        }

        public string NameOf(CSteamID id)
        {
            LanMember m;
            if (_live.TryGetValue(id.GetAccountID().m_AccountID, out m) && !string.IsNullOrEmpty(m.Name))
                return m.Name;
            return LanAddressing.NameForSlot(LanAddressing.SlotForAccount(id.GetAccountID().m_AccountID));
        }
    }
}
