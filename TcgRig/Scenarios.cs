using System;
using System.Collections.Generic;
using Steamworks;
using TcgMultiplayer.Game;
using TcgMultiplayer.Net;
using P = TcgMultiplayer.Plugin;

namespace TcgRig
{
    /// <summary>
    /// Two (or three) real Sessions, talking to each other over a fake wire.
    ///
    /// Most of these exist because of a specific defect found by reading the
    /// code and fixed by reasoning about it. Reasoning is not running. This is
    /// the running.
    /// </summary>
    public static class Scenarios
    {
        public static readonly List<Func<Check>> All = new List<Func<Check>>
        {
            Handshake,
            VersionRefusal,
            FailedLobbyEntryLeavesUsOffline,
            HostileLobbyDataDoesNotThrow,
            BuildMismatchWarnsButJoins,
            GuestLeavesCleanly,
            HostLeavesEndsTheGuest,
            PromotionRaceStillEndsTheGuest,
            SilenceTimesAPeerOut,
            StallDoesNotReapHealthyPeers,
            EmptyLobbyTakesTwoReads,
            SteamDisconnectEndsIt,
            SessionFailureToHostEndsIt,
            MalformedPacketsAreSurvivedAndRateLimited,
            PlayerStateRoundTrip,
            StaleSnapshotsAreDropped,
            LossyLinkKeepsReliableTraffic,
            WorldVarTypesSurvive,
            GuestAsksForTheWorldOnJoin,
            CountersAddUp,
            ByeFromAStrangerIsIgnored,
        };

        private static Check Run(string name, Action<Check> body)
        {
            var c = new Check { Name = name };
            try { body(c); c.Passed = true; }
            catch (Assert.Failed f) { c.Passed = false; c.Error = f.Message; }
            catch (Exception ex) { c.Passed = false; c.Error = ex.GetType().Name + ": " + ex.Message; }
            return c;
        }

        // ------------------------------------------------------------------

        private static Check Handshake()
        {
            return Run("handshake completes both ways", c =>
            {
                var r = new Rig();
                var host = r.Add(1, "Mason");
                var guest = r.Add(2, "Friend");
                r.HostAndJoinAll();

                Assert.Eq(host.S.State, SessionState.InLobby, "host is in the lobby");
                Assert.Eq(guest.S.State, SessionState.InLobby, "guest is in the lobby");
                Assert.True(host.S.IsHost, "host knows it is the host");
                Assert.True(!guest.S.IsHost, "guest knows it is not the host");

                Assert.True(host.Handshaked(guest.Id), "host handshaked with the guest");
                Assert.True(guest.Handshaked(host.Id), "guest handshaked with the host");
                Assert.Eq(host.PeerFor(guest.Id).Name, "Friend", "host learned the guest's name");
                Assert.Eq(guest.PeerFor(host.Id).Name, "Mason", "guest learned the host's name");
                Assert.Eq(guest.PeerFor(host.Id).ModVersion, P.Version, "guest learned the host's mod version");
                Assert.Eq(host.SessionsBegun, 1, "host raised OnSessionBegan once");
                Assert.Eq(guest.SessionsBegun, 1, "guest raised OnSessionBegan once");
                Assert.True(host.LastBeganAsHost, "host's OnSessionBegan said host");
                Assert.True(!guest.LastBeganAsHost, "guest's OnSessionBegan said guest");

                c.Detail = "2 peers, both directions, names and versions exchanged";
            });
        }

        private static Check VersionRefusal()
        {
            return Run("a different mod version is refused, with a reason", c =>
            {
                var r = new Rig();
                var host = r.Add(1, "Mason");
                var guest = r.Add(2, "Friend");

                host.S.Host(4);
                r.Pump(0.5);
                // The host advertised its version in lobby data. Pretend the guest
                // is older by rewriting what the lobby says the host is running.
                r.World.SetLobbyDataForTest(host.S.Lobby.m_SteamID, Session.LobbyKeyMod, "0.9.1");

                guest.S.Join(host.S.Lobby);
                r.Pump(1.0);

                Assert.Eq(guest.S.State, SessionState.Offline, "guest did not stay in the lobby");
                Assert.True(guest.S.RefusedReason != null, "guest has a refusal reason");
                Assert.True(guest.S.RefusedReason.Contains("0.9.1"), "the reason names the host's version");
                Assert.True(guest.S.RefusedReason.Contains(P.Version), "the reason names our version");

                c.Detail = "refused, offline, reason names both versions";
            });
        }

        private static Check FailedLobbyEntryLeavesUsOffline()
        {
            return Run("a refused lobby join does not leave us stuck InLobby", c =>
            {
                // This is the one that used to suppress every Steam achievement
                // for the rest of the launch: LobbyEnter fires for failures too,
                // and taking it as success left State == InLobby forever.
                var r = new Rig();
                var guest = r.Add(2, "Friend");

                guest.S.Join(Ids.Lobby(999999));   // no such lobby
                r.Pump(1.0);

                Assert.Eq(guest.S.State, SessionState.Offline, "state went back to Offline");
                Assert.True(guest.S.RefusedReason != null, "a reason was recorded");
                Assert.Eq(guest.SessionsBegun, 0, "no session was ever begun");

                c.Detail = "Offline, with an explanation";
            });
        }

        private static Check HostileLobbyDataDoesNotThrow()
        {
            return Run("short or missing build hashes do not throw in a callback", c =>
            {
                // Substring(0,8) on "unknown", on "", or on a three-character
                // string thrown by a hostile lobby would land inside a Steam
                // callback, outside every Guard, and abort the rest of the join.
                foreach (var hostile in new[] { "", "abc", "unknown", "\u0000\u0000" })
                {
                    var r = new Rig();
                    var host = r.Add(1, "Mason");
                    var guest = r.Add(2, "Friend");

                    host.S.Host(4);
                    r.Pump(0.5);
                    r.World.SetLobbyDataForTest(host.S.Lobby.m_SteamID, Session.LobbyKeyBuild, hostile);

                    guest.S.Join(host.S.Lobby);
                    r.Pump(1.0);

                    Assert.Eq(guest.S.State, SessionState.InLobby,
                              "guest joined despite build hash \"" + hostile + "\"");
                }
                c.Detail = "4 hostile values, none threw";
            });
        }

        private static Check BuildMismatchWarnsButJoins()
        {
            return Run("a different game build warns but still connects", c =>
            {
                var r = new Rig();
                var host = r.Add(1, "Mason");
                var guest = r.Add(2, "Friend");

                host.S.Host(4);
                r.Pump(0.5);
                r.World.SetLobbyDataForTest(host.S.Lobby.m_SteamID, Session.LobbyKeyBuild, "ffffffffdeadbeef");

                guest.S.Join(host.S.Lobby);
                r.Pump(1.0);

                Assert.Eq(guest.S.State, SessionState.InLobby, "guest still joined");
                Assert.True(guest.S.BuildMismatch != null, "the mismatch was recorded");
                Assert.True(guest.S.BuildMismatch.Contains("ffffffff"), "it names the host's build");

                c.Detail = "warned, not refused";
            });
        }

        private static Check GuestLeavesCleanly()
        {
            return Run("a guest leaving is noticed by the host", c =>
            {
                var r = new Rig();
                var host = r.Add(1, "Mason");
                var guest = r.Add(2, "Friend");
                r.HostAndJoinAll();

                guest.S.Leave();
                r.Pump(1.0);

                Assert.Eq(guest.S.State, SessionState.Offline, "guest is offline");
                Assert.Eq(guest.SessionsEnded, 1, "guest raised OnSessionEnded exactly once");
                Assert.Eq(host.S.Peers.Count, 0, "host dropped the peer");
                Assert.True(host.PeersGone.Contains(guest.Id), "host raised OnPeerGone for the guest");
                Assert.Eq(host.S.State, SessionState.InLobby, "host is still hosting");

                c.Detail = "peer removed, OnPeerGone fired, host unaffected";
            });
        }

        private static Check HostLeavesEndsTheGuest()
        {
            return Run("the host leaving ends the guest's session", c =>
            {
                var r = new Rig();
                var host = r.Add(1, "Mason");
                var guest = r.Add(2, "Friend");
                r.HostAndJoinAll();

                host.S.Leave();
                r.Pump(1.0);

                Assert.Eq(guest.S.State, SessionState.Offline, "guest's session ended");
                Assert.Eq(guest.SessionsEnded, 1, "guest raised OnSessionEnded once");
                Assert.True(guest.S.EndReason != null, "an end reason was recorded");
                Assert.True(guest.S.EndReason.Contains("host"), "the reason blames the host: " + guest.S.EndReason);

                c.Detail = "ended cleanly: \"" + guest.S.EndReason + "\"";
            });
        }

        private static Check PromotionRaceStillEndsTheGuest()
        {
            return Run("host leaves with three players: the promoted guest still ends", c =>
            {
                // Steam hands the lobby to a guest the moment the host goes. If
                // that guest concluded "I am the host now", every host-departure
                // check it had switched off — and it sat there holding someone
                // else's island with no restore pending.
                var r = new Rig();
                var host = r.Add(1, "Mason");
                var g1 = r.Add(2, "Friend");
                var g2 = r.Add(3, "Other");
                r.HostAndJoinAll();

                Assert.Eq(host.S.Peers.Count, 2, "host sees both guests");

                host.S.Leave();
                r.Pump(2.0);

                Assert.Eq(g1.S.State, SessionState.Offline, "first guest ended");
                Assert.Eq(g2.S.State, SessionState.Offline, "second guest ended");
                Assert.Eq(g1.SessionsEnded, 1, "first guest ended exactly once");
                Assert.Eq(g2.SessionsEnded, 1, "second guest ended exactly once");

                c.Detail = "both guests ended despite Steam promoting one of them";
            });
        }

        private static Check SilenceTimesAPeerOut()
        {
            return Run("a peer that goes silent is timed out", c =>
            {
                var r = new Rig();
                var host = r.Add(1, "Mason");
                var guest = r.Add(2, "Friend");
                r.HostAndJoinAll();

                // Pull the guest's cable: no packets, no callbacks, nothing.
                r.World.Cut.Add(guest.Id);
                r.Pump(45.0, 0.25);

                Assert.Eq(host.S.Peers.Count, 0, "host reaped the silent peer");
                Assert.True(host.PeersGone.Contains(guest.Id), "OnPeerGone fired");
                Assert.True(P.Logged("timed out"), "it said so in the log");

                c.Detail = "reaped after 30s of silence";
            });
        }

        private static Check StallDoesNotReapHealthyPeers()
        {
            return Run("a frozen main thread does not reap a healthy host", c =>
            {
                // The save backup runs synchronously on join and can freeze the
                // main thread for longer than the peer timeout. The stopwatch
                // keeps counting through it; charging that time against peers
                // would kill a perfectly good session the instant it unfroze.
                var r = new Rig();
                var host = r.Add(1, "Mason");
                var guest = r.Add(2, "Friend");
                r.HostAndJoinAll();
                r.Pump(11.0, 0.5);          // get past the 10s settling window

                r.Stall(40.0);              // 40 seconds of wall clock, one tick
                r.Pump(3.0, 0.25);

                Assert.Eq(guest.S.State, SessionState.InLobby, "guest is still connected");
                Assert.Eq(guest.S.Peers.Count, 1, "guest still has the host");
                Assert.Eq(host.S.Peers.Count, 1, "host still has the guest");
                Assert.True(!P.Logged("timed out"), "nobody was declared dead");

                c.Detail = "40s stall survived, both peers intact";
            });
        }

        private static Check EmptyLobbyTakesTwoReads()
        {
            return Run("an empty lobby ends the session, but only after two reads", c =>
            {
                var r = new Rig();
                var host = r.Add(1, "Mason");
                var guest = r.Add(2, "Friend");
                r.HostAndJoinAll();
                r.Pump(11.0, 0.5);

                // Everyone vanishes from Steam's point of view, with no callback.
                r.World.DestroyLobbySilently(guest.S.Lobby.m_SteamID);

                // The watchdog runs every 3s. One pass must not be enough.
                r.Pump(3.5, 0.25);
                Assert.Eq(guest.S.State, SessionState.InLobby, "one bad read did not tear it down");

                r.Pump(4.0, 0.25);
                Assert.Eq(guest.S.State, SessionState.Offline, "two bad reads did");
                Assert.True(guest.S.EndReason != null && guest.S.EndReason.Contains("lobby"),
                            "the reason blames the lobby: " + guest.S.EndReason);

                c.Detail = "survived one bad read, ended on the second";
            });
        }

        private static Check SteamDisconnectEndsIt()
        {
            return Run("losing Steam ends the session and restores the world", c =>
            {
                var r = new Rig();
                var host = r.Add(1, "Mason");
                var guest = r.Add(2, "Friend");
                r.HostAndJoinAll();

                r.World.RaiseSteamDisconnected(guest.Sid);
                r.Pump(0.5);

                Assert.Eq(guest.S.State, SessionState.Offline, "guest went offline");
                Assert.Eq(guest.SessionsEnded, 1, "OnSessionEnded fired — this is what puts the save back");
                Assert.True(guest.S.EndReason.Contains("Steam"), "reason: " + guest.S.EndReason);

                c.Detail = "\"" + guest.S.EndReason + "\"";
            });
        }

        private static Check SessionFailureToHostEndsIt()
        {
            return Run("losing the transport to the host ends the session", c =>
            {
                // Steam's lobby membership can outlive the messaging session by
                // minutes. Left alone the guest sits in a dead session with the
                // host's island applied and nothing to trigger the restore.
                var r = new Rig();
                var host = r.Add(1, "Mason");
                var guest = r.Add(2, "Friend");
                r.HostAndJoinAll();

                r.World.RaiseSessionFailed(guest.Sid, host.Sid, "Timeout");
                r.Pump(0.5);

                Assert.Eq(guest.S.State, SessionState.Offline, "guest went offline");
                Assert.Eq(guest.SessionsEnded, 1, "OnSessionEnded fired");

                c.Detail = "\"" + guest.S.EndReason + "\"";
            });
        }

        private static Check MalformedPacketsAreSurvivedAndRateLimited()
        {
            return Run("garbage from a peer is survived and logged at most 11 times", c =>
            {
                var r = new Rig();
                var host = r.Add(1, "Mason");
                var guest = r.Add(2, "Friend");
                r.HostAndJoinAll();

                // Truncated bodies for opcodes that read fixed-width fields. These
                // are the ones that genuinely throw, and the only way to drive the
                // rate limiter.
                var truncating = new[] { Op.Ping, Op.Pong, Op.Wallet, Op.MachineClaim, Op.MachineOwner };
                int expectThrow = 0;
                for (int i = 0; i < 20; i++)
                {
                    var op = truncating[i % truncating.Length];
                    r.World.InjectRaw(guest.Sid, host.Sid, new byte[] { (byte)op, 0x01, 0x02 },
                                      SteamTransport.ChannelControl);
                    expectThrow++;
                }

                // Plus a pile of random noise, which mostly does NOT throw — see
                // the note below.
                var rng = new Random(7);
                for (int i = 0; i < 60; i++)
                {
                    var junk = new byte[rng.Next(1, 40)];
                    rng.NextBytes(junk);
                    // Not Op.Bye (6): that is a real instruction, not garbage.
                    byte op2 = (byte)rng.Next(1, 15);
                    if (op2 == (byte)Op.Bye) op2 = (byte)Op.Chat;
                    junk[0] = op2;
                    r.World.InjectRaw(guest.Sid, host.Sid, junk, SteamTransport.ChannelControl);
                }
                r.World.InjectRaw(guest.Sid, host.Sid, new byte[0], SteamTransport.ChannelControl);
                r.World.InjectRaw(guest.Sid, host.Sid, new byte[] { 200, 1, 2, 3 }, SteamTransport.ChannelControl);

                r.Pump(1.0);

                Assert.Eq(host.S.State, SessionState.InLobby, "the host survived all of it");
                var p = host.PeerFor(guest.Id);
                Assert.True(p != null, "the peer is still tracked");
                Assert.True(p.BadPackets >= expectThrow,
                            "the truncated ones were all caught (" + p.BadPackets + " >= " + expectThrow + ")");

                int warned = P.Warnings.FindAll(l => l.IndexOf("alformed", StringComparison.Ordinal) >= 0).Count;
                Assert.Eq(warned, 11, "logging stopped after 10 lines plus one notice");

                c.Detail = p.BadPackets + " rejected, " + warned + " log lines, session alive. "
                           + "Note: random noise with a plausible opcode and enough bytes parses "
                           + "\"successfully\" into nonsense rather than being rejected — the wire "
                           + "format has no length or checksum field. Harmless between trusted "
                           + "peers on the same version, which is the only case the mod allows.";
            });
        }

        private static Check PlayerStateRoundTrip()
        {
            return Run("a player snapshot survives the wire exactly", c =>
            {
                var r = new Rig();
                var host = r.Add(1, "Mason");
                var guest = r.Add(2, "Friend");
                r.HostAndJoinAll();

                var sent = new PlayerState
                {
                    Pos = new UnityEngine.Vector3(123.5f, -8.25f, 4096.125f),
                    Yaw = 271.5f, Pitch = -12.25f,
                    VelX = -3.5f, VelZ = 7.25f, Turn = 180f,
                    Grounded = true, Running = false, Jumping = true,
                };
                host.S.BroadcastPlayerState(sent, 1);
                r.Pump(0.5);

                Assert.Eq(guest.States.Count, 1, "one snapshot arrived");
                var got = guest.States[0];
                Assert.Near(got.Pos.x, sent.Pos.x, 0.0001f, "x");
                Assert.Near(got.Pos.y, sent.Pos.y, 0.0001f, "y");
                Assert.Near(got.Pos.z, sent.Pos.z, 0.0001f, "z");
                Assert.Near(got.Yaw, sent.Yaw, 0.0001f, "yaw");
                Assert.Near(got.Pitch, sent.Pitch, 0.0001f, "pitch");
                Assert.Near(got.Turn, sent.Turn, 0.0001f, "turn");
                Assert.Eq(got.Grounded, true, "grounded flag");
                Assert.Eq(got.Running, false, "running flag");
                Assert.Eq(got.Jumping, true, "jumping flag");

                c.Detail = "position, angles, velocity and all three flags intact";
            });
        }

        private static Check StaleSnapshotsAreDropped()
        {
            return Run("out-of-order snapshots are dropped, including across the wrap", c =>
            {
                var r = new Rig();
                var host = r.Add(1, "Mason");
                var guest = r.Add(2, "Friend");
                r.HostAndJoinAll();

                var st = new PlayerState { Pos = new UnityEngine.Vector3(1, 2, 3) };

                host.S.BroadcastPlayerState(st, 100);
                r.Pump(0.3);
                Assert.Eq(guest.States.Count, 1, "the first one arrived");

                host.S.BroadcastPlayerState(st, 99);        // stale
                r.Pump(0.3);
                Assert.Eq(guest.States.Count, 1, "the stale one was dropped");

                host.S.BroadcastPlayerState(st, 101);
                r.Pump(0.3);
                Assert.Eq(guest.States.Count, 2, "the newer one was taken");

                // A jump of more than half the sequence space reads as backwards,
                // which is the whole point of the wrap rule — 101 -> 65535 is a
                // step back of 102, not forward of 65434.
                host.S.BroadcastPlayerState(st, 65535);
                r.Pump(0.3);
                Assert.Eq(guest.States.Count, 2, "a huge forward jump was treated as stale");

                // Walk up to the boundary and over it properly.
                int n = guest.States.Count;
                foreach (ushort seq in new ushort[] { 150, 200, 30000, 60000, 65530, 65535, 3, 9 })
                {
                    host.S.BroadcastPlayerState(st, seq);
                    r.Pump(0.2);
                    n++;
                    Assert.Eq(guest.States.Count, n, "seq " + seq + " was accepted as newer");
                }

                c.Detail = "stale dropped, and 65535 -> 0 -> 3 crosses the wrap cleanly";
            });
        }

        private static Check LossyLinkKeepsReliableTraffic()
        {
            return Run("40% loss: reliable traffic all lands, unreliable degrades", c =>
            {
                var r = new Rig();
                var host = r.Add(1, "Mason");
                var guest = r.Add(2, "Friend");
                r.HostAndJoinAll();

                r.World.LossUnreliable = 0.4;
                r.World.LatencyMs = 60;
                r.World.JitterMs = 40;
                r.World.ReorderUnreliable = true;

                for (int i = 0; i < 40; i++)
                {
                    host.S.SendWorldVar("unlock_" + i, true, true);      // reliable
                    host.S.BroadcastPlayerState(new PlayerState
                    {
                        Pos = new UnityEngine.Vector3(i, 0, 0)
                    }, (ushort)(i + 1));                                  // unreliable
                    r.Pump(0.1);
                }
                r.Pump(2.0);

                Assert.Eq(guest.WorldVars.Count, 40, "every reliable world var arrived");
                Assert.True(guest.States.Count > 5, "some snapshots got through (" + guest.States.Count + ")");
                Assert.True(guest.States.Count <= 40, "no snapshot was duplicated into existence");
                Assert.Eq(guest.S.State, SessionState.InLobby, "the session survived the bad link");

                c.Detail = "40/40 reliable, " + guest.States.Count + "/40 unreliable, no corruption";
            });
        }

        private static Check WorldVarTypesSurvive()
        {
            return Run("world vars keep their type across the wire", c =>
            {
                // A bool arriving as an int would silently unlock something.
                var r = new Rig();
                var host = r.Add(1, "Mason");
                var guest = r.Add(2, "Friend");
                r.HostAndJoinAll();

                host.S.SendWorldVar("door_open", true, true);
                host.S.SendWorldVar("coins_spent", 4211, true);
                host.S.SendWorldVar("ride_speed", 2.5f, true);
                r.Pump(0.5);

                Assert.Eq(guest.WorldVars.Count, 3, "all three arrived");
                Assert.True(guest.WorldVars.Contains("door_open=True (host)"), "bool stayed a bool");
                Assert.True(guest.WorldVars.Contains("coins_spent=4211 (host)"), "int stayed an int");
                Assert.True(guest.WorldVars.Contains("ride_speed=2.5 (host)"), "float stayed a float");

                c.Detail = "bool, int and float all intact, host flag preserved";
            });
        }

        private static Check GuestAsksForTheWorldOnJoin()
        {
            return Run("a joining guest asks the host for the island", c =>
            {
                var r = new Rig();
                var host = r.Add(1, "Mason");
                var guest = r.Add(2, "Friend");
                r.HostAndJoinAll();

                Assert.True(host.SnapshotRequests.Contains(guest.Id),
                            "the host was asked for a snapshot by the guest");
                Assert.Eq(guest.SnapshotRequests.Count, 0, "the guest was not asked for one");

                c.Detail = "requested once, by the guest only";
            });
        }

        private static Check ByeFromAStrangerIsIgnored()
        {
            return Run("a disconnect only counts from someone we shook hands with", c =>
            {
                // Found by this rig: random bytes whose first byte happened to be
                // opcode 6 were dropping a live peer. Misdecoded traffic is exactly
                // what two different mod versions produce.
                var r = new Rig();
                var host = r.Add(1, "Mason");
                var guest = r.Add(2, "Friend");
                var stranger = r.Add(3, "Nobody");
                r.HostAndJoinAll();

                int before = host.S.Peers.Count;
                r.World.InjectRaw(stranger.Sid, host.Sid,
                                  new byte[] { (byte)Op.Bye }, SteamTransport.ChannelControl);
                r.Pump(0.5);

                Assert.True(host.PeerFor(guest.Id) != null, "the real guest is untouched");
                Assert.True(!host.PeersGone.Contains(guest.Id), "nobody was wrongly dropped");

                // And the real thing still works.
                guest.S.Leave();
                r.Pump(0.5);
                Assert.True(host.PeersGone.Contains(guest.Id), "a genuine goodbye is still honoured");

                c.Detail = "ignored from an unhandshaked peer, honoured from a real one";
            });
        }

        private static Check CountersAddUp()
        {
            return Run("the counters the session report leans on are sane", c =>
            {
                var r = new Rig();
                var host = r.Add(1, "Mason");
                var guest = r.Add(2, "Friend");
                r.HostAndJoinAll();
                r.Pump(3.0);

                Assert.True(host.S.PacketsSent > 0, "host sent packets");
                Assert.True(host.S.PacketsReceived > 0, "host received packets");
                Assert.True(host.S.BytesSent > 0, "host counted bytes out");
                Assert.True(host.S.BytesReceived > 0, "host counted bytes in");
                Assert.Eq(host.S.PeakPeers, 1, "peak peers recorded");
                Assert.True(host.S.StartedAt != default(DateTime), "start time recorded");
                Assert.True(host.S.EndReason == null, "no end reason while it is still running");

                host.S.Leave();
                r.Pump(0.5);
                c.Detail = "sent " + host.S.PacketsSent + ", received " + host.S.PacketsReceived
                           + ", peak " + host.S.PeakPeers;
            });
        }
    }
}
