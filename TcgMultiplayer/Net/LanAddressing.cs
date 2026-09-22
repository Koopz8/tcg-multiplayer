using System;
using System.Globalization;

namespace TcgMultiplayer.Net
{
    /// <summary>
    /// Who is who when there is no Steam.
    ///
    /// The point of all this is to get a second peer without a second person.
    /// Two copies of the game on one PC can talk to each other over loopback,
    /// which exercises everything the in-process rig cannot reach: two real
    /// Unity worlds, two real player rigs, a car driven in one window and
    /// watched in the other.
    ///
    /// The trick that makes it simple is deriving the address from the
    /// identity. Slot 0 is account 900000 on port 27600, slot 1 is 900001 on
    /// 27601, and so on — so knowing who a peer is tells you where to send to,
    /// and there is no discovery protocol to write or get wrong.
    ///
    /// Slots are claimed by binding: the first instance to start gets 27600 and
    /// is the host, the second gets 27601. Nothing has to be configured per
    /// window, which matters because both instances read the same config file.
    ///
    /// No Unity, no sockets, no files in here — the rig sweeps it.
    /// </summary>
    public static class LanAddressing
    {
        public const int BasePort = 27600;

        /// <summary>
        /// Account numbers for the fake identities. Far away from anything real:
        /// a genuine Steam account ID is a 32-bit value in active use, and these
        /// must never be mistaken for one.
        /// </summary>
        public const uint BaseAccount = 900000u;

        public const int MaxSlots = 4;

        public static uint AccountForSlot(int slot)
        {
            if (slot < 0 || slot >= MaxSlots) return 0u;
            return BaseAccount + (uint)slot;
        }

        /// <summary>Which slot is this account, or -1 if it isn't one of ours.</summary>
        public static int SlotForAccount(uint account)
        {
            if (account < BaseAccount) return -1;
            long slot = (long)account - BaseAccount;
            return slot < MaxSlots ? (int)slot : -1;
        }

        public static int PortForSlot(int slot)
        {
            return slot < 0 || slot >= MaxSlots ? -1 : BasePort + slot;
        }

        public static int PortForAccount(uint account)
        {
            return PortForSlot(SlotForAccount(account));
        }

        /// <summary>
        /// Is this one of our synthetic identities? Used to keep local-test
        /// plumbing from ever pointing at a real Steam user by accident.
        /// </summary>
        public static bool IsLocalAccount(uint account)
        {
            return SlotForAccount(account) >= 0;
        }

        /// <summary>Slot 0 hosts. Not a vote — just the first window you opened.</summary>
        public const int HostSlot = 0;

        public static bool IsHostSlot(int slot) { return slot == HostSlot; }

        public static string NameForSlot(int slot)
        {
            if (slot < 0) return "Local player";
            return slot == HostSlot ? "Local host (window 1)" : "Local guest (window " + (slot + 1) + ")";
        }
    }

    /// <summary>One instance's presence, as written to and read from its own file.</summary>
    public struct LanMember
    {
        public uint Account;
        public string Name;
        /// <summary>Unix milliseconds when this instance last said it was alive.</summary>
        public long Heartbeat;

        public int Slot { get { return LanAddressing.SlotForAccount(Account); } }

        public override string ToString()
        {
            return "slot " + Slot + " \"" + Name + "\"";
        }
    }

    /// <summary>
    /// Presence, without a server to keep it.
    ///
    /// Each instance writes only its OWN file and reads the others'. That is
    /// deliberate: two processes appending to one shared list is a file-locking
    /// problem, and a file-locking problem in a test harness costs more time
    /// than the thing it is meant to be testing. One writer per file means
    /// there is no contention to handle at all.
    ///
    /// Liveness is a heartbeat rather than a goodbye, because the window you
    /// most want to test closing is the one that crashed.
    /// </summary>
    public static class LanMembership
    {
        /// <summary>How long a window can go quiet before we treat it as gone.</summary>
        public const long StaleMs = 6000;

        /// <summary>How often each instance refreshes its own file.</summary>
        public const long HeartbeatMs = 1000;

        public static bool IsLive(long heartbeat, long nowMs)
        {
            return nowMs - heartbeat < StaleMs;
        }

        public static string FileNameFor(uint account)
        {
            return "member_" + account.ToString(CultureInfo.InvariantCulture) + ".txt";
        }

        public static string Encode(LanMember m)
        {
            // Pipe-separated and one line long: this gets written once a second
            // by two processes and read by both, so it wants to be something
            // that cannot be half-parsed.
            return m.Account.ToString(CultureInfo.InvariantCulture) + "|"
                 + Sanitise(m.Name) + "|"
                 + m.Heartbeat.ToString(CultureInfo.InvariantCulture);
        }

        public static bool TryDecode(string line, out LanMember m)
        {
            m = new LanMember();
            if (string.IsNullOrEmpty(line)) return false;

            var parts = line.Trim().Split('|');
            if (parts.Length < 3) return false;

            uint account;
            long beat;
            if (!uint.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out account)) return false;
            if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out beat)) return false;
            if (!LanAddressing.IsLocalAccount(account)) return false;

            m.Account = account;
            m.Name = parts[1];
            m.Heartbeat = beat;
            return true;
        }

        private static string Sanitise(string s)
        {
            if (string.IsNullOrEmpty(s)) return "player";
            s = s.Replace('|', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (s.Length > 32) s = s.Substring(0, 32);
            return s.Length == 0 ? "player" : s;
        }

        public static long NowMs()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }
    }
}
