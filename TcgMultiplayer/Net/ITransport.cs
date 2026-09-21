using System.Collections.Generic;
using Steamworks;

namespace TcgMultiplayer.Net
{
    /// <summary>
    /// The four things a Session needs from whatever is carrying its bytes.
    ///
    /// This exists so the session can be driven without Steam. Everything above
    /// the wire — the handshake, ownership arbitration, the timeouts, the whole
    /// 900-line state machine — used to be untestable because it could only run
    /// with two real people in front of two real Steam clients. With the bytes
    /// behind an interface, a second peer can be stood up inside one process and
    /// all of that becomes ordinary, repeatable test code.
    ///
    /// The Steam implementation below is a pure forward to the existing static
    /// class. Nothing about the real path changes.
    /// </summary>
    public interface ITransport
    {
        bool Send(CSteamID to, byte[] data, int channel, bool reliable);
        void Poll(int channel, List<SteamTransport.Received> into);
        bool AcceptSession(CSteamID peer);
        void CloseSession(CSteamID peer);
        string ConnectionState(CSteamID peer);
    }

    public sealed class SteamTransportBackend : ITransport
    {
        public static readonly SteamTransportBackend Instance = new SteamTransportBackend();

        public bool Send(CSteamID to, byte[] data, int channel, bool reliable)
        {
            return SteamTransport.Send(to, data, channel, reliable);
        }

        public void Poll(int channel, List<SteamTransport.Received> into)
        {
            SteamTransport.Poll(channel, into);
        }

        public bool AcceptSession(CSteamID peer) { return SteamTransport.AcceptSession(peer); }
        public void CloseSession(CSteamID peer) { SteamTransport.CloseSession(peer); }
        public string ConnectionState(CSteamID peer) { return SteamTransport.ConnectionState(peer); }
    }
}
