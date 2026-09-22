using System;
using System.Collections.Generic;
using TcgMultiplayer.Net;

namespace TcgRig
{
    /// <summary>
    /// Local test mode: two copies of the game on one PC, so there can be a
    /// second peer without a second person.
    ///
    /// The socket and the files can't be tested here — they're real I/O in a
    /// real process. What can be tested is the part that decides who is who and
    /// who is still alive, and that is the part where being wrong is expensive:
    /// a mistake in the addressing sends packets to a port nobody is listening
    /// on, and a mistake in the liveness rule either strands a window that
    /// closed or evicts one that is still there.
    /// </summary>
    public static class LocalTestScenarios
    {
        public static readonly List<Func<Check>> All = new List<Func<Check>>
        {
            IdentityAndAddressAgree,
            SlotsAreBounded,
            RealSteamAccountsAreNeverLocal,
            PresenceSurvivesARoundTrip,
            AGarbledPresenceFileIsIgnored,
            AWindowThatStopsBeatingGoesAway,
            EverySlotIsDistinctAndAddressable,
        };

        private static Check Run(string name, Action<Check> body)
        {
            var c = new Check { Name = name };
            try { body(c); c.Passed = true; }
            catch (Assert.Failed f) { c.Passed = false; c.Error = f.Message; }
            catch (Exception ex) { c.Passed = false; c.Error = ex.GetType().Name + ": " + ex.Message; }
            return c;
        }

        private static Check IdentityAndAddressAgree()
        {
            return Run("knowing who a window is tells you where to send to it", c =>
            {
                for (int slot = 0; slot < LanAddressing.MaxSlots; slot++)
                {
                    uint acc = LanAddressing.AccountForSlot(slot);
                    Assert.Eq(LanAddressing.SlotForAccount(acc), slot, "account round-trips to its slot");
                    Assert.Eq(LanAddressing.PortForAccount(acc), LanAddressing.PortForSlot(slot),
                              "and to the same port either way");
                }
                c.Detail = "no discovery protocol needed — the address IS the identity";
            });
        }

        private static Check SlotsAreBounded()
        {
            return Run("asking for a slot that doesn't exist gets nothing, not a wild port", c =>
            {
                Assert.Eq(LanAddressing.AccountForSlot(-1), 0u, "negative slot");
                Assert.Eq(LanAddressing.AccountForSlot(LanAddressing.MaxSlots), 0u, "past the last slot");
                Assert.Eq(LanAddressing.PortForSlot(-1), -1, "no port for a negative slot");
                Assert.Eq(LanAddressing.PortForSlot(99), -1, "no port for slot 99");
                Assert.Eq(LanAddressing.PortForAccount(12345u), -1, "no port for a stranger");
                c.Detail = "a bad slot can never resolve to a real port to send at";
            });
        }

        private static Check RealSteamAccountsAreNeverLocal()
        {
            return Run("a real Steam account is never mistaken for a local test window", c =>
            {
                // Mason's own, and a few shapes of real account ID.
                uint[] real = { 64012177u, 1u, 100000u, 899999u, uint.MaxValue };
                foreach (var a in real)
                    Assert.True(!LanAddressing.IsLocalAccount(a),
                                "account " + a + " is not one of ours");

                Assert.True(LanAddressing.IsLocalAccount(LanAddressing.BaseAccount), "but ours are");
                c.Detail = "keeps loopback plumbing from ever pointing at a real user";
            });
        }

        private static Check PresenceSurvivesARoundTrip()
        {
            return Run("a window's presence file says the same thing after being read back", c =>
            {
                var m = new LanMember
                {
                    Account = LanAddressing.AccountForSlot(1),
                    Name = "Local guest (window 2)",
                    Heartbeat = 1737000000000L,
                };

                LanMember back;
                Assert.True(LanMembership.TryDecode(LanMembership.Encode(m), out back), "it decodes");
                Assert.Eq(back.Account, m.Account, "same window");
                Assert.Eq(back.Name, m.Name, "same name");
                Assert.Eq(back.Heartbeat, m.Heartbeat, "same heartbeat");
                Assert.Eq(back.Slot, 1, "same slot");
                c.Detail = "one line, written once a second, read by the other window";
            });
        }

        private static Check AGarbledPresenceFileIsIgnored()
        {
            return Run("a half-written or nonsense presence file is ignored, not half-believed", c =>
            {
                string[] junk =
                {
                    "", "   ", "nonsense", "900001", "900001|name",
                    "|x|1", "abc|x|1", "900001|x|notanumber",
                    "12345|RealPerson|1737000000000",     // not one of our accounts
                };

                LanMember m;
                foreach (var j in junk)
                    Assert.True(!LanMembership.TryDecode(j, out m), "rejected: \"" + j + "\"");

                // A name containing the separator must not be able to forge fields.
                var sneaky = new LanMember
                {
                    Account = LanAddressing.AccountForSlot(0),
                    Name = "evil|900003|999",
                    Heartbeat = 5L,
                };
                LanMember back;
                Assert.True(LanMembership.TryDecode(LanMembership.Encode(sneaky), out back), "still decodes");
                Assert.Eq(back.Account, LanAddressing.AccountForSlot(0), "as the right window");
                Assert.Eq(back.Heartbeat, 5L, "with the right heartbeat, not the one in the name");
                c.Detail = "the separator is stripped from names on the way out";
            });
        }

        private static Check AWindowThatStopsBeatingGoesAway()
        {
            return Run("a window that crashed is noticed, and one that's just busy isn't", c =>
            {
                long now = 1737000000000L;

                Assert.True(LanMembership.IsLive(now, now), "a beat from right now is live");
                Assert.True(LanMembership.IsLive(now - LanMembership.HeartbeatMs, now),
                            "and so is one from a beat ago");
                Assert.True(LanMembership.IsLive(now - (LanMembership.StaleMs - 1), now),
                            "still live just inside the window — a frame hitch isn't a crash");
                Assert.True(!LanMembership.IsLive(now - LanMembership.StaleMs, now),
                            "gone once it's past the limit");
                Assert.True(!LanMembership.IsLive(now - 60000, now), "and long gone after a minute");

                Assert.True(LanMembership.StaleMs > LanMembership.HeartbeatMs * 3,
                            "the timeout leaves room for several missed beats ("
                            + LanMembership.StaleMs + "ms vs " + LanMembership.HeartbeatMs + "ms)");
                c.Detail = "liveness is a heartbeat, because a crashed window never says goodbye";
            });
        }

        private static Check EverySlotIsDistinctAndAddressable()
        {
            return Run("sweep: no two windows can ever collide on an identity or a port", c =>
            {
                var accounts = new List<uint>();
                var ports = new List<int>();

                for (int slot = 0; slot < LanAddressing.MaxSlots; slot++)
                {
                    uint acc = LanAddressing.AccountForSlot(slot);
                    int port = LanAddressing.PortForSlot(slot);

                    Assert.True(!accounts.Contains(acc), "slot " + slot + " has its own identity");
                    Assert.True(!ports.Contains(port), "slot " + slot + " has its own port");
                    Assert.True(port >= LanAddressing.BasePort, "port is in range");
                    Assert.True(acc >= LanAddressing.BaseAccount, "account is in range");

                    accounts.Add(acc);
                    ports.Add(port);
                }

                Assert.True(LanAddressing.IsHostSlot(0), "window 1 hosts");
                for (int slot = 1; slot < LanAddressing.MaxSlots; slot++)
                    Assert.True(!LanAddressing.IsHostSlot(slot), "window " + (slot + 1) + " does not");

                c.Detail = LanAddressing.MaxSlots + " windows, ports "
                           + LanAddressing.BasePort + "-" + (LanAddressing.BasePort + LanAddressing.MaxSlots - 1);
            });
        }
    }
}
