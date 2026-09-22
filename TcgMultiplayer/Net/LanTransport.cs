using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Steamworks;

namespace TcgMultiplayer.Net
{
    /// <summary>
    /// Bytes between two copies of the game on the same PC, over 127.0.0.1.
    ///
    /// This is the third <see cref="ITransport"/>. The first is Steam, the
    /// second is the rig's in-process fake, and this one sits between them: two
    /// real processes, two real Unity worlds, one real socket — but no second
    /// person and no second copy of the game.
    ///
    /// It exists because every feature built since the last release needs a
    /// second peer to run at all. A vehicle's position stream has no solo path
    /// through it; neither does riding in one. The rig proves the rules, and
    /// cannot show you a car.
    ///
    /// HONEST LIMITS. This is a loopback test transport, not LAN play:
    ///
    ///  * "Reliable" is not implemented. There are no acks and no resends. On
    ///    127.0.0.1 a datagram is a memory copy inside the kernel and does not
    ///    get dropped or reordered, so reliable and unreliable are the same
    ///    thing here and the code above is none the wiser. Point this at a real
    ///    network and that stops being true — a dropped machine event would
    ///    desync a cabinet for the rest of the round with nothing to notice it.
    ///  * Nothing is encrypted or authenticated. It binds to loopback only, so
    ///    nothing off this machine can reach it, and that is the whole of the
    ///    security model.
    ///
    /// Turning it into real LAN play means an ack/resend layer on the control
    /// channel. That is a real piece of work and is deliberately not pretended
    /// at here.
    /// </summary>
    public sealed class LanTransport : ITransport, IDisposable
    {
        private const int MaxDatagram = 64 * 1024;
        private const int HeaderBytes = 5;        // channel:1 + fromAccount:4

        private UdpClient _sock;
        private readonly List<SteamTransport.Received> _inbox = new List<SteamTransport.Received>(64);
        private readonly HashSet<ulong> _open = new HashSet<ulong>();

        public int Slot { get; private set; }
        public uint Account { get; private set; }
        public bool Bound { get { return _sock != null; } }
        public string LastError { get; private set; }

        public int PacketsIn, PacketsOut, Malformed;

        /// <summary>
        /// Claim a slot by binding its port. The first window to start gets slot
        /// 0 and hosts; the next gets slot 1. Binding IS the claim, so two
        /// instances reading the same config file still end up with different
        /// identities without anything being configured per window.
        /// </summary>
        public bool Bind()
        {
            for (int slot = 0; slot < LanAddressing.MaxSlots; slot++)
            {
                int port = LanAddressing.PortForSlot(slot);
                try
                {
                    var sock = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
                    sock.Client.Blocking = false;

                    // Windows returns WSAECONNRESET from ReceiveFrom when a
                    // previous datagram bounced off a closed port. On a
                    // connectionless socket that is noise, not an error, and
                    // left alone it makes the whole receive path throw the
                    // moment the other window is closed.
                    try { sock.Client.IOControl(unchecked((int)0x9800000C), new byte[] { 0, 0, 0, 0 }, null); }
                    catch { /* not Windows, or not supported — harmless */ }

                    _sock = sock;
                    Slot = slot;
                    Account = LanAddressing.AccountForSlot(slot);
                    LastError = null;
                    Plugin.Log("Local test transport: slot " + slot + " on port " + port
                               + " (" + LanAddressing.NameForSlot(slot) + ")");
                    return true;
                }
                catch (SocketException)
                {
                    // Port taken — another window already has this slot.
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Plugin.Warn("Local test transport could not bind port " + port + ": " + ex.Message);
                    return false;
                }
            }

            LastError = "all " + LanAddressing.MaxSlots + " local slots are in use";
            Plugin.Warn("Local test transport: " + LastError);
            return false;
        }

        public CSteamID SelfId
        {
            get
            {
                return new CSteamID(new AccountID_t(Account),
                                    EUniverse.k_EUniversePublic,
                                    EAccountType.k_EAccountTypeIndividual);
            }
        }

        // ---------------------------------------------------------- ITransport

        public bool Send(CSteamID to, byte[] data, int channel, bool reliable)
        {
            if (_sock == null || data == null || data.Length == 0) return false;

            int port = LanAddressing.PortForAccount(to.GetAccountID().m_AccountID);
            if (port < 0) return false;                 // not one of our windows
            if (data.Length + HeaderBytes > MaxDatagram) return false;

            var buf = new byte[data.Length + HeaderBytes];
            buf[0] = (byte)channel;
            buf[1] = (byte)(Account & 0xFF);
            buf[2] = (byte)((Account >> 8) & 0xFF);
            buf[3] = (byte)((Account >> 16) & 0xFF);
            buf[4] = (byte)((Account >> 24) & 0xFF);
            Buffer.BlockCopy(data, 0, buf, HeaderBytes, data.Length);

            try
            {
                _sock.Send(buf, buf.Length, new IPEndPoint(IPAddress.Loopback, port));
                PacketsOut++;
                return true;
            }
            catch (SocketException)
            {
                // The other window isn't listening. Not fatal and not worth a
                // log line every frame — the session's own peer timeout is what
                // notices a window that has gone away.
                return false;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        public void Poll(int channel, List<SteamTransport.Received> into)
        {
            Drain();

            for (int i = _inbox.Count - 1; i >= 0; i--)
            {
                if (_inbox[i].Channel != channel) continue;
                into.Add(_inbox[i]);
                _inbox.RemoveAt(i);
            }
        }

        /// <summary>
        /// Empty the socket into the inbox. Done once per poll rather than per
        /// channel, because the socket carries all three mixed together and
        /// draining it three times would reorder them against each other.
        /// </summary>
        private void Drain()
        {
            if (_sock == null) return;

            var any = new IPEndPoint(IPAddress.Loopback, 0);
            int guard = 0;

            while (guard++ < 512)
            {
                byte[] buf;
                try
                {
                    if (_sock.Available <= 0) return;
                    buf = _sock.Receive(ref any);
                }
                catch (SocketException)
                {
                    return;   // would-block, or a stale ICMP bounce
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    return;
                }

                if (buf == null || buf.Length < HeaderBytes) { Malformed++; continue; }

                uint from = (uint)(buf[1] | (buf[2] << 8) | (buf[3] << 16) | (buf[4] << 24));
                if (!LanAddressing.IsLocalAccount(from)) { Malformed++; continue; }

                var payload = new byte[buf.Length - HeaderBytes];
                Buffer.BlockCopy(buf, HeaderBytes, payload, 0, payload.Length);

                _inbox.Add(new SteamTransport.Received
                {
                    From = new CSteamID(new AccountID_t(from),
                                        EUniverse.k_EUniversePublic,
                                        EAccountType.k_EAccountTypeIndividual),
                    Data = payload,
                    Channel = buf[0],
                });
                PacketsIn++;
            }
        }

        public bool AcceptSession(CSteamID peer)
        {
            _open.Add(peer.m_SteamID);
            return true;
        }

        public void CloseSession(CSteamID peer)
        {
            _open.Remove(peer.m_SteamID);

            // Anything already queued from that window is stale the moment the
            // session is closed. Leaving it in the inbox is how a peer that has
            // just left gets processed back into existence.
            for (int i = _inbox.Count - 1; i >= 0; i--)
                if (_inbox[i].From == peer) _inbox.RemoveAt(i);
        }

        public string ConnectionState(CSteamID peer)
        {
            int slot = LanAddressing.SlotForAccount(peer.GetAccountID().m_AccountID);
            if (slot < 0) return "not a local window";
            return (_open.Contains(peer.m_SteamID) ? "open" : "idle")
                   + " · loopback:" + LanAddressing.PortForSlot(slot);
        }

        public void Dispose()
        {
            try { if (_sock != null) _sock.Close(); }
            catch { }
            _sock = null;
            _inbox.Clear();
            _open.Clear();
        }
    }
}
