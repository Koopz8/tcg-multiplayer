using System;
using System.Collections.Generic;
using System.Linq;
using Steamworks;
using TcgMultiplayer.Net;

namespace TcgRig
{
    /// <summary>
    /// A fake Steam, good enough to run real Sessions against.
    ///
    /// It owns a virtual clock, a set of lobbies with members and data, and a
    /// message bus with latency, jitter, loss and a switch for cutting a peer
    /// off mid-session. Time only moves when the test says so, so a thirty
    /// second timeout costs a microsecond to exercise.
    ///
    /// Deliberate simplifications, so nobody reads more into a green run than
    /// is there:
    ///   * AcceptSession is recorded but never gates delivery. Steam holds the
    ///     first message until the far side accepts; here it goes straight
    ///     through. The accept path still runs, it just cannot fail.
    ///   * RTT is not simulated. Ping/Pong round-trips, but the number it
    ///     produces is meaningless.
    ///   * Nothing here proves Steam will deliver a byte between two houses.
    ///     That is still the one thing only two real players can tell us.
    /// </summary>
    /// <summary>
    /// Steam IDs are not arbitrary numbers: CSteamID.IsValid() checks the
    /// universe and account-type bits, and Session leans on it in Track() and
    /// in Leave(). Toy ids like 1 and 2 are rejected as invalid, so the fake
    /// has to mint structurally real ones.
    /// </summary>
    public static class Ids
    {
        public static CSteamID User(uint account)
        {
            return new CSteamID(new AccountID_t(account),
                                EUniverse.k_EUniversePublic,
                                EAccountType.k_EAccountTypeIndividual);
        }

        public static CSteamID Lobby(uint account)
        {
            return new CSteamID(new AccountID_t(account),
                                (uint)EChatSteamIDInstanceFlags.k_EChatInstanceFlagLobby,
                                EUniverse.k_EUniversePublic,
                                EAccountType.k_EAccountTypeChat);
        }
    }

    public sealed class LoopbackWorld
    {
        public double Now { get; private set; }

        // ---- network conditions ------------------------------------------
        public int LatencyMs = 0;
        public int JitterMs = 0;
        /// <summary>0..1. Applies to unreliable channels only, as Steam does.</summary>
        public double LossUnreliable = 0.0;
        /// <summary>Deliver some unreliable packets late enough to arrive out of order.</summary>
        public bool ReorderUnreliable = false;

        private readonly Random _rng;

        public LoopbackWorld(int seed = 12345) { _rng = new Random(seed); }

        // ---- lobbies ------------------------------------------------------
        private sealed class FakeLobby
        {
            public ulong Id;
            public ulong Owner;
            public int MaxMembers;
            public bool Joinable = true;
            public bool Destroyed;
            public readonly List<ulong> Members = new List<ulong>();
            public readonly Dictionary<string, string> Data = new Dictionary<string, string>();
        }

        private readonly Dictionary<ulong, FakeLobby> _lobbies = new Dictionary<ulong, FakeLobby>();
        private readonly Dictionary<ulong, LoopbackLobby> _backends = new Dictionary<ulong, LoopbackLobby>();
        private uint _nextLobbyAccount = 5000;

        /// <summary>Peers whose packets vanish in both directions — a yanked cable.</summary>
        public readonly HashSet<ulong> Cut = new HashSet<ulong>();

        /// <summary>
        /// Pairs whose messaging session has been closed. Steam drops anything
        /// still in the pipe when you close a session; without modelling that,
        /// a peer's own trailing packets arrive after its goodbye and re-create
        /// it as a fresh peer that then sits there until the silence timeout.
        /// </summary>
        private readonly HashSet<string> _closed = new HashSet<string>();

        private static string Pair(ulong a, ulong b) { return a < b ? a + "|" + b : b + "|" + a; }

        internal void CloseBetween(ulong a, ulong b)
        {
            _closed.Add(Pair(a, b));
            _inFlight.RemoveAll(m => Pair(m.From, m.To) == Pair(a, b));
            foreach (var kv in _arrived)
                kv.Value.RemoveAll(m => Pair(m.From, m.To) == Pair(a, b));
        }

        internal void OpenBetween(ulong a, ulong b) { _closed.Remove(Pair(a, b)); }

        // ---- the bus ------------------------------------------------------
        private sealed class Msg
        {
            public ulong From, To;
            public byte[] Data;
            public int Channel;
            public bool Reliable;
            public double DeliverAt;
            public long Seq;
        }

        private readonly List<Msg> _inFlight = new List<Msg>();
        private readonly Dictionary<ulong, List<Msg>> _arrived = new Dictionary<ulong, List<Msg>>();
        private readonly Dictionary<string, double> _lastReliableAt = new Dictionary<string, double>();
        private long _seq;

        // Deferred callbacks, so joining a lobby is asynchronous the way Steam's is.
        private readonly List<KeyValuePair<double, Action>> _pending = new List<KeyValuePair<double, Action>>();

        public int InFlightCount { get { return _inFlight.Count; } }

        private void Later(Action a, double delaySeconds = 0.05)
        {
            _pending.Add(new KeyValuePair<double, Action>(Now + delaySeconds, a));
        }

        /// <summary>Move the world forward. Callbacks fire and packets land.</summary>
        public void Advance(double seconds)
        {
            Now += seconds;

            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (_pending[i].Key > Now) continue;
                var a = _pending[i].Value;
                _pending.RemoveAt(i);
                a();
            }

            for (int i = _inFlight.Count - 1; i >= 0; i--)
            {
                var m = _inFlight[i];
                if (m.DeliverAt > Now) continue;
                _inFlight.RemoveAt(i);
                if (Cut.Contains(m.To) || Cut.Contains(m.From)) continue;
                List<Msg> q;
                if (!_arrived.TryGetValue(m.To, out q)) { q = new List<Msg>(); _arrived[m.To] = q; }
                q.Add(m);
            }
        }

        public LoopbackLobby AddPeer(CSteamID id, string name)
        {
            var b = new LoopbackLobby(this, id, name);
            _backends[id.m_SteamID] = b;
            return b;
        }

        public string NameOf(ulong id)
        {
            LoopbackLobby b;
            return _backends.TryGetValue(id, out b) ? b.SelfName : id.ToString();
        }

        // ---- transport ----------------------------------------------------

        internal bool Send(ulong from, ulong to, byte[] data, int channel, bool reliable)
        {
            if (Cut.Contains(from) || Cut.Contains(to)) return false;
            if (_closed.Contains(Pair(from, to))) return false;
            if (!reliable && _rng.NextDouble() < LossUnreliable) return true;   // dropped, but "sent"

            double jitter = JitterMs > 0 ? _rng.NextDouble() * JitterMs : 0;
            double at = Now + (LatencyMs + jitter) / 1000.0;

            if (reliable)
            {
                // Reliable means in-order too. Never let one overtake its predecessor.
                var key = from + ">" + to + ":" + channel;
                double prev;
                if (_lastReliableAt.TryGetValue(key, out prev) && prev > at) at = prev;
                _lastReliableAt[key] = at + 0.0001;
            }
            else if (ReorderUnreliable && _rng.NextDouble() < 0.3)
            {
                at += (LatencyMs + 10) / 1000.0;    // this one arrives late
            }

            _inFlight.Add(new Msg
            {
                From = from, To = to, Data = data, Channel = channel,
                Reliable = reliable, DeliverAt = at, Seq = _seq++,
            });
            return true;
        }

        internal void Drain(ulong owner, int channel, List<SteamTransport.Received> into)
        {
            List<Msg> q;
            if (!_arrived.TryGetValue(owner, out q)) return;
            for (int i = 0; i < q.Count; i++)
            {
                if (q[i].Channel != channel) continue;
                into.Add(new SteamTransport.Received
                {
                    From = new CSteamID(q[i].From),
                    Data = q[i].Data,
                    Channel = channel,
                });
                q[i] = null;
            }
            q.RemoveAll(m => m == null);
        }

        /// <summary>Inject a packet that isn't a packet, from a peer that is real.</summary>
        public void InjectRaw(CSteamID fromId, CSteamID toId, byte[] data, int channel)
        {
            _inFlight.Add(new Msg
            {
                From = fromId.m_SteamID, To = toId.m_SteamID, Data = data, Channel = channel,
                Reliable = true, DeliverAt = Now, Seq = _seq++,
            });
        }

        // ---- lobby operations ---------------------------------------------

        internal void CreateLobby(ulong who, int maxMembers)
        {
            var l = new FakeLobby { Id = Ids.Lobby(_nextLobbyAccount++).m_SteamID, Owner = who, MaxMembers = maxMembers };
            l.Members.Add(who);
            _lobbies[l.Id] = l;

            var backend = _backends[who];
            Later(() =>
            {
                if (backend.LobbyCreated != null) backend.LobbyCreated(l.Id, true);
                // Steam raises LobbyEnter for the creator too.
                if (backend.LobbyEntered != null) backend.LobbyEntered(l.Id, 1);
            });
        }

        internal void JoinLobby(ulong who, ulong lobbyId)
        {
            var backend = _backends[who];
            FakeLobby l;
            bool ok = _lobbies.TryGetValue(lobbyId, out l) && !l.Destroyed && l.Joinable
                      && l.Members.Count < l.MaxMembers;

            Later(() =>
            {
                if (!ok)
                {
                    // 2 == k_EChatRoomEnterResponseDoesntExist, near enough.
                    if (backend.LobbyEntered != null) backend.LobbyEntered(lobbyId, 2);
                    return;
                }

                var others = l.Members.ToList();
                if (!l.Members.Contains(who)) l.Members.Add(who);

                if (backend.LobbyEntered != null) backend.LobbyEntered(lobbyId, 1);
                foreach (var m in others)
                {
                    var b = _backends[m];
                    if (b.LobbyChatUpdate != null) b.LobbyChatUpdate(lobbyId, who, 1);   // entered
                }
            });
        }

        internal void LeaveLobby(ulong who, ulong lobbyId)
        {
            FakeLobby l;
            if (!_lobbies.TryGetValue(lobbyId, out l)) return;
            if (!l.Members.Remove(who)) return;

            // Steam promotes the next member when the owner walks away. This is
            // the behaviour that made the host-departure check so delicate.
            if (l.Owner == who && l.Members.Count > 0) l.Owner = l.Members[0];
            if (l.Members.Count == 0) l.Destroyed = true;

            var remaining = l.Members.ToList();
            Later(() =>
            {
                foreach (var m in remaining)
                {
                    LoopbackLobby b;
                    if (!_backends.TryGetValue(m, out b)) continue;
                    if (b.LobbyChatUpdate != null) b.LobbyChatUpdate(lobbyId, who, 2);   // left
                }
            });
        }

        /// <summary>Make a lobby evaporate without telling anyone — a Steam hiccup.</summary>
        public void DestroyLobbySilently(ulong lobbyId)
        {
            FakeLobby l;
            if (_lobbies.TryGetValue(lobbyId, out l)) { l.Members.Clear(); l.Destroyed = true; }
        }

        /// <summary>Drop a member without the chat update ever arriving.</summary>
        public void RemoveMemberSilently(ulong lobbyId, ulong who)
        {
            FakeLobby l;
            if (!_lobbies.TryGetValue(lobbyId, out l)) return;
            l.Members.Remove(who);
            if (l.Owner == who && l.Members.Count > 0) l.Owner = l.Members[0];
        }

        public void RaiseSteamDisconnected(CSteamID who2)
        {
            var b = _backends[who2.m_SteamID];
            if (b.Disconnected != null) b.Disconnected();
        }

        public void RaiseSessionFailed(CSteamID who2, CSteamID peer, string why)
        {
            var b = _backends[who2.m_SteamID];
            if (b.SessionFailed != null) b.SessionFailed(peer, why);
        }

        /// <summary>Rewrite what a lobby claims about itself — versions, build hashes.</summary>
        public void SetLobbyDataForTest(ulong lobbyId, string k, string v) { SetLobbyData(lobbyId, k, v); }

        internal void SetLobbyData(ulong lobbyId, string k, string v)
        {
            FakeLobby l;
            if (_lobbies.TryGetValue(lobbyId, out l)) l.Data[k] = v;
        }

        internal string GetLobbyData(ulong lobbyId, string k)
        {
            FakeLobby l; string v;
            if (_lobbies.TryGetValue(lobbyId, out l) && l.Data.TryGetValue(k, out v)) return v;
            return "";
        }

        internal void SetJoinable(ulong lobbyId, bool joinable)
        {
            FakeLobby l;
            if (_lobbies.TryGetValue(lobbyId, out l)) l.Joinable = joinable;
        }

        internal ulong Owner(ulong lobbyId)
        {
            FakeLobby l;
            return _lobbies.TryGetValue(lobbyId, out l) ? l.Owner : 0UL;
        }

        internal int MemberCount(ulong lobbyId)
        {
            FakeLobby l;
            return _lobbies.TryGetValue(lobbyId, out l) ? l.Members.Count : 0;
        }

        internal ulong MemberAt(ulong lobbyId, int i)
        {
            FakeLobby l;
            if (!_lobbies.TryGetValue(lobbyId, out l) || i < 0 || i >= l.Members.Count) return 0UL;
            return l.Members[i];
        }

        /// <summary>The peer whose SessionRequest callback should fire on first contact.</summary>
        internal LoopbackLobby Backend(ulong id)
        {
            LoopbackLobby b;
            return _backends.TryGetValue(id, out b) ? b : null;
        }
    }

    public sealed class LoopbackTransport : ITransport
    {
        private readonly LoopbackWorld _world;
        private readonly ulong _self;
        private readonly HashSet<ulong> _sawFirstContact = new HashSet<ulong>();

        public LoopbackTransport(LoopbackWorld world, ulong self) { _world = world; _self = self; }

        public bool Send(CSteamID to, byte[] data, int channel, bool reliable)
        {
            // Mirror Steam: the far side is told somebody wants to talk to them
            // the first time a message shows up.
            var other = _world.Backend(to.m_SteamID);
            if (other != null && !_sawFirstContact.Contains(to.m_SteamID))
            {
                _sawFirstContact.Add(to.m_SteamID);
                if (other.SessionRequest != null) other.SessionRequest(new CSteamID(_self));
            }
            return _world.Send(_self, to.m_SteamID, data, channel, reliable);
        }

        public void Poll(int channel, List<SteamTransport.Received> into)
        {
            _world.Drain(_self, channel, into);
        }

        public bool AcceptSession(CSteamID peer)
        {
            _world.OpenBetween(_self, peer.m_SteamID);
            return true;
        }

        public void CloseSession(CSteamID peer)
        {
            _world.CloseBetween(_self, peer.m_SteamID);
        }
        public string ConnectionState(CSteamID peer) { return "Connected"; }
    }

    public sealed class LoopbackLobby : ILobbyBackend
    {
        private readonly LoopbackWorld _world;

        public LoopbackLobby(LoopbackWorld world, CSteamID self, string name)
        {
            _world = world;
            SelfId = self;
            SelfName = name;
        }

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

        public bool Start() { Ready = true; return true; }

        public void CreateLobby(int maxPlayers) { _world.CreateLobby(SelfId.m_SteamID, maxPlayers); }
        public void JoinLobby(CSteamID lobby) { _world.JoinLobby(SelfId.m_SteamID, lobby.m_SteamID); }
        public void LeaveLobby(CSteamID lobby) { _world.LeaveLobby(SelfId.m_SteamID, lobby.m_SteamID); }
        public void OpenInviteOverlay(CSteamID lobby) { }

        public void SetLobbyData(CSteamID lobby, string key, string value) { _world.SetLobbyData(lobby.m_SteamID, key, value); }
        public string GetLobbyData(CSteamID lobby, string key) { return _world.GetLobbyData(lobby.m_SteamID, key); }
        public void SetLobbyJoinable(CSteamID lobby, bool joinable) { _world.SetJoinable(lobby.m_SteamID, joinable); }

        public CSteamID GetLobbyOwner(CSteamID lobby) { return new CSteamID(_world.Owner(lobby.m_SteamID)); }
        public int GetNumLobbyMembers(CSteamID lobby) { return _world.MemberCount(lobby.m_SteamID); }
        public CSteamID GetLobbyMemberByIndex(CSteamID lobby, int index) { return new CSteamID(_world.MemberAt(lobby.m_SteamID, index)); }

        public string NameOf(CSteamID id) { return _world.NameOf(id.m_SteamID); }
    }
}
