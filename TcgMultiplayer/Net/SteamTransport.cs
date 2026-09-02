using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Steamworks;

namespace TcgMultiplayer.Net
{
    /// <summary>
    /// Thin wrapper over SteamNetworkingMessages. Steam gives us NAT traversal,
    /// relay fallback and authenticated identity for free, so this layer only has
    /// to marshal bytes and hand them up.
    ///
    /// Note: the game's own SteamManager (in HLG.Runtime) already calls
    /// SteamAPI.RunCallbacks() every frame, so we must NOT pump callbacks
    /// ourselves — registering Callback&lt;T&gt; handlers is enough.
    /// </summary>
    public static class SteamTransport
    {
        public const int ChannelControl = 0;   // reliable, ordered
        public const int ChannelPing = 1;      // unreliable, no delay

        private const int SendReliable = Constants.k_nSteamNetworkingSend_Reliable;
        private const int SendUnreliableNoDelay = Constants.k_nSteamNetworkingSend_UnreliableNoDelay;

        private static readonly IntPtr[] _recvBuf = new IntPtr[64];

        public struct Received
        {
            public CSteamID From;
            public byte[] Data;
            public int Channel;
        }

        public static bool Send(CSteamID to, byte[] data, int channel, bool reliable)
        {
            if (data == null || data.Length == 0) return false;

            var id = new SteamNetworkingIdentity();
            id.Clear();
            id.SetSteamID(to);

            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                var flags = reliable ? SendReliable : SendUnreliableNoDelay;
                var result = SteamNetworkingMessages.SendMessageToUser(
                    ref id, handle.AddrOfPinnedObject(), (uint)data.Length, flags, channel);

                if (result != EResult.k_EResultOK && result != EResult.k_EResultNoConnection)
                    Plugin.Warn("Send to " + to.m_SteamID + " returned " + result);

                return result == EResult.k_EResultOK;
            }
            catch (Exception ex)
            {
                Plugin.Warn("Send threw: " + ex.Message);
                return false;
            }
            finally
            {
                handle.Free();
            }
        }

        /// <summary>Drains one channel. Steam owns the message memory, so release every pointer.</summary>
        public static void Poll(int channel, List<Received> into)
        {
            int n;
            try { n = SteamNetworkingMessages.ReceiveMessagesOnChannel(channel, _recvBuf, _recvBuf.Length); }
            catch (Exception ex) { Plugin.Warn("Receive threw: " + ex.Message); return; }

            for (int i = 0; i < n; i++)
            {
                var ptr = _recvBuf[i];
                if (ptr == IntPtr.Zero) continue;
                try
                {
                    var msg = SteamNetworkingMessage_t.FromIntPtr(ptr);
                    var bytes = new byte[msg.m_cbSize];
                    if (msg.m_cbSize > 0) Marshal.Copy(msg.m_pData, bytes, 0, msg.m_cbSize);
                    into.Add(new Received
                    {
                        From = msg.m_identityPeer.GetSteamID(),
                        Data = bytes,
                        Channel = channel,
                    });
                }
                catch (Exception ex) { Plugin.Warn("Unmarshal failed: " + ex.Message); }
                finally { SteamNetworkingMessage_t.Release(ptr); }
            }
        }

        public static bool AcceptSession(CSteamID peer)
        {
            var id = new SteamNetworkingIdentity();
            id.Clear();
            id.SetSteamID(peer);
            try { return SteamNetworkingMessages.AcceptSessionWithUser(ref id); }
            catch (Exception ex) { Plugin.Warn("AcceptSession threw: " + ex.Message); return false; }
        }

        public static void CloseSession(CSteamID peer)
        {
            var id = new SteamNetworkingIdentity();
            id.Clear();
            id.SetSteamID(peer);
            try { SteamNetworkingMessages.CloseSessionWithUser(ref id); }
            catch { }
        }

        public static string ConnectionState(CSteamID peer)
        {
            var id = new SteamNetworkingIdentity();
            id.Clear();
            id.SetSteamID(peer);
            try
            {
                SteamNetConnectionInfo_t info;
                SteamNetConnectionRealTimeStatus_t status;
                var state = SteamNetworkingMessages.GetSessionConnectionInfo(ref id, out info, out status);
                return state.ToString().Replace("k_ESteamNetworkingConnectionState_", "");
            }
            catch { return "?"; }
        }
    }
}
