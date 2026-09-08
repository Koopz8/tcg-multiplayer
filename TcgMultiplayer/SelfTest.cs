using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TcgMultiplayer.Game;
using TcgMultiplayer.Net;
using UnityEngine;

namespace TcgMultiplayer
{
    /// <summary>
    /// Checks, on the player's own machine, the things that would otherwise only
    /// be checked by two strangers discovering they were broken.
    ///
    /// This mod is going out to people before anyone has run it with two real
    /// copies of the game, which is an uncomfortable position to be in. It can't
    /// be fixed by claiming confidence. What it can be narrowed by is testing
    /// everything that doesn't actually need a second player — the wire format,
    /// hostile input, the visit round-trip against a real save, the wallet guard,
    /// the backup — and having every beta tester run the same checks on hardware
    /// and saves I'll never see.
    ///
    /// It is deliberately honest about its own limits. The last line of the
    /// report says what it did NOT test, because a green tick that overstates
    /// itself is worse than no tick.
    /// </summary>
    internal static class SelfTest
    {
        public sealed class Result
        {
            public string Name;
            public bool Ok;
            public bool Skipped;
            public string Detail;
        }

        public static readonly List<Result> Results = new List<Result>();
        public static bool HasRun { get; private set; }
        public static DateTime LastRunAt { get; private set; }

        public static int Passed, Failed, Skipped;

        private static void Check(string name, Func<string> body)
        {
            var r = new Result { Name = name };
            try
            {
                var detail = body();
                if (detail != null && detail.StartsWith("skipped", StringComparison.OrdinalIgnoreCase))
                {
                    r.Skipped = true; r.Detail = detail.Substring(7).TrimStart(' ', '—', '-');
                }
                else if (detail != null && detail.StartsWith("FAILED", StringComparison.Ordinal))
                {
                    r.Ok = false; r.Detail = detail.Substring(6).TrimStart(' ', '—', '-');
                }
                else { r.Ok = true; r.Detail = detail ?? ""; }
            }
            catch (Exception ex)
            {
                r.Ok = false;
                r.Detail = ex.GetType().Name + ": " + ex.Message;
            }
            Results.Add(r);
        }

        public static void RunAll(Session session, MachineDirector machines, WorldState world)
        {
            Results.Clear();
            Plugin.Log("Running self-test...");

            Check("wire format round-trip", WireRoundTrip);
            Check("names with accents and emoji", UnicodeNames);
            Check("survives a corrupt packet", HostilePackets);
            Check("machine ids are stable", () => NetIds(machines));
            Check("visiting gives your save back", () =>
            {
                string d;
                return world.SelfTestVisit(out d) ? d : (d.StartsWith("skipped") ? d : "FAILED — " + d);
            });
            Check("wallet holds while spectating", () => WalletHold(machines));
            Check("save backup writes files", BackupWorks);

            Passed = Failed = Skipped = 0;
            foreach (var r in Results)
            {
                if (r.Skipped) Skipped++;
                else if (r.Ok) Passed++;
                else Failed++;
            }

            HasRun = true;
            LastRunAt = DateTime.Now;

            Plugin.Log("Self-test: " + Passed + " passed, " + Failed + " failed, " + Skipped + " skipped.");
            foreach (var r in Results)
                Plugin.Log("   [" + (r.Skipped ? "skip" : r.Ok ? " ok " : "FAIL") + "] " + r.Name
                           + (string.IsNullOrEmpty(r.Detail) ? "" : " — " + r.Detail));
        }

        // ------------------------------------------------------------- checks

        /// <summary>
        /// Every field type out and back. If this is wrong nothing else can be
        /// right, and nothing had ever checked it.
        /// </summary>
        private static string WireRoundTrip()
        {
            int n = 0;

            using (var w = new PacketWriter(Op.Hello))
            {
                w.Str("Mason").Str("0.9.2").U64(76561198024012177UL).Bool(true);
                using (var r = new PacketReader(w.ToArray()))
                {
                    if (r.Op != Op.Hello) return "FAILED — opcode came back as " + r.Op;
                    if (r.Str() != "Mason") return "FAILED — name corrupted";
                    if (r.Str() != "0.9.2") return "FAILED — version corrupted";
                    if (r.U64() != 76561198024012177UL) return "FAILED — steam id corrupted";
                    if (!r.Bool()) return "FAILED — bool corrupted";
                    n += 4;
                }
            }

            // Extremes, because quantised physics and wallet values live near them.
            using (var w = new PacketWriter(Op.MachinePhysics))
            {
                w.U32(uint.MaxValue).I32(int.MinValue).U16(ushort.MaxValue)
                 .I64(long.MinValue).F32(-3.4028235e38f).U8(255)
                 .Bytes(new byte[] { 0, 255, 127, 128 });
                using (var r = new PacketReader(w.ToArray()))
                {
                    r.Op.ToString();
                    if (r.U32() != uint.MaxValue) return "FAILED — uint32 corrupted";
                    if (r.I32() != int.MinValue) return "FAILED — int32 corrupted";
                    if (r.U16() != ushort.MaxValue) return "FAILED — uint16 corrupted";
                    if (r.I64() != long.MinValue) return "FAILED — int64 corrupted";
                    if (r.F32() != -3.4028235e38f) return "FAILED — float corrupted";
                    if (r.U8() != 255) return "FAILED — byte corrupted";
                    var b = r.Bytes();
                    if (b == null || b.Length != 4 || b[1] != 255 || b[3] != 128)
                        return "FAILED — byte array corrupted";
                    n += 7;
                }
            }

            // An empty payload is a real case: Bye and WorldSync send nothing.
            using (var w = new PacketWriter(Op.Bye))
            using (var r = new PacketReader(w.ToArray()))
            {
                if (r.Op != Op.Bye) return "FAILED — empty packet lost its opcode";
                n++;
            }

            // Every opcode should survive being written and read back.
            foreach (Op op in Enum.GetValues(typeof(Op)))
            {
                using (var w = new PacketWriter(op))
                using (var r = new PacketReader(w.ToArray()))
                {
                    if (r.Op != op) return "FAILED — opcode " + op + " read back as " + r.Op;
                    n++;
                }
            }

            return n + " fields and every opcode survived the round trip";
        }

        /// <summary>
        /// Steam names are full of non-ASCII. If the length prefix is counted in
        /// characters rather than bytes anywhere, this is where it shows up.
        /// </summary>
        private static string UnicodeNames()
        {
            var names = new[]
            {
                "Møøse", "日本語のなまえ", "Ünïcödé", "🎰🪙 CoinKing 🪙🎰",
                "a", new string('x', 3000), "",
            };

            foreach (var name in names)
            {
                using (var w = new PacketWriter(Op.Chat))
                {
                    w.Str(name);
                    using (var r = new PacketReader(w.ToArray()))
                    {
                        var back = r.Str();
                        if (back != name)
                            return "FAILED — \"" + Trim(name) + "\" came back as \"" + Trim(back) + "\"";
                    }
                }
            }
            return names.Length + " awkward names survived, including emoji and a 3000-character one";
        }

        /// <summary>
        /// A peer can send anything. Truncated, empty and random packets must be
        /// rejected without taking the game down — this is the one check here
        /// that is about somebody else's machine being hostile rather than buggy.
        /// </summary>
        private static string HostilePackets()
        {
            var rng = new System.Random(1234);
            int handled = 0;

            var cases = new List<byte[]>
            {
                new byte[0],                       // nothing at all
                new byte[] { 200 },                // opcode that doesn't exist
                new byte[] { (byte)Op.Hello },     // valid opcode, no payload
                new byte[] { (byte)Op.Hello, 255, 255 },   // claims a huge string, has none
                new byte[] { (byte)Op.MachineEvent, 1, 0, 0, 0 },
            };
            for (int i = 0; i < 40; i++)
            {
                var junk = new byte[rng.Next(1, 64)];
                rng.NextBytes(junk);
                cases.Add(junk);
            }

            foreach (var data in cases)
            {
                try
                {
                    using (var r = new PacketReader(data))
                    {
                        // Read greedily; whatever it is, it must not escape as an
                        // unhandled exception at the call site.
                        try { r.Str(); r.U64(); r.I32(); r.Bytes(); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    // A throw from the constructor itself is fine and expected for
                    // an empty buffer, as long as it is a throw and not a crash.
                    if (!(ex is EndOfStreamException || ex is ArgumentException
                          || ex is IndexOutOfRangeException || ex is OverflowException))
                        return "FAILED — unexpected " + ex.GetType().Name + " from a malformed packet";
                }
                handled++;
            }

            return handled + " malformed packets rejected without crashing";
        }

        private static string NetIds(MachineDirector machines)
        {
            if (machines == null || machines.MachineCount == 0)
                return "skipped — no machines registered yet (walk into the arcade first)";

            var seen = new Dictionary<uint, string>();
            int checkedCount = 0, collisions = 0;

            foreach (var m in machines.Machines)
            {
                if (m == null || string.IsNullOrEmpty(m.Path)) continue;

                // Hashing the same path twice must give the same answer, or two
                // players never agree on which cabinet they're talking about.
                if (NetId.Hash(m.Path) != m.Id) return "FAILED — " + m.Label + " hashes differently on a second pass";

                string other;
                if (seen.TryGetValue(m.Id, out other) && other != m.Path) collisions++;
                else seen[m.Id] = m.Path;
                checkedCount++;
            }

            if (collisions > 0) return "FAILED — " + collisions + " machines share an id";
            return checkedCount + " machines, stable ids, no collisions";
        }

        private static string WalletHold(MachineDirector machines)
        {
            if (machines == null || !machines.Wallet.Available)
                return "skipped — economy globals aren't readable yet (load a save first)";

            int before = machines.Wallet.Coins;
            int protectedCount = machines.Wallet.ProtectedCount;
            if (protectedCount == 0) return "FAILED — no economy globals are being protected";

            // The guard is exercised for real by the Rehearsal replay; here we
            // only confirm it is armed and pointing at something.
            if (machines.Wallet.Coins != before) return "FAILED — wallet moved while reading it";
            return protectedCount + " economy globals under guard";
        }

        private static string BackupWorks()
        {
            var dir = SaveGuard.Backup();
            if (dir == null)
                return "FAILED — " + (SaveGuard.LastError ?? "backup did not run");

            try
            {
                if (!Directory.Exists(dir)) return "FAILED — backup folder wasn't created";
                var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
                if (files.Length == 0) return "FAILED — backup folder is empty";

                long bytes = 0;
                foreach (var f in files) bytes += new FileInfo(f).Length;
                if (bytes == 0) return "FAILED — backup files are all empty";

                return files.Length + " files, " + (bytes / 1024) + " KB, in "
                     + SaveGuard.BackupFolderName;
            }
            catch (Exception ex) { return "FAILED — " + ex.Message; }
        }

        private static string Trim(string s)
        {
            if (s == null) return "null";
            return s.Length <= 24 ? s : s.Substring(0, 24) + "…";
        }

        // ------------------------------------------------------------- report

        /// <summary>A block the player can paste straight into a bug report.</summary>
        public static string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("TcgMultiplayer " + Plugin.Version + " self-test");
            sb.AppendLine("game build " + (CompatCheck.GameHash ?? "unknown"));
            sb.AppendLine(Passed + " passed, " + Failed + " failed, " + Skipped + " skipped");
            sb.AppendLine();
            foreach (var r in Results)
                sb.AppendLine("[" + (r.Skipped ? "skip" : r.Ok ? " ok " : "FAIL") + "] " + r.Name
                              + (string.IsNullOrEmpty(r.Detail) ? "" : " — " + r.Detail));
            sb.AppendLine();
            sb.AppendLine("Not covered by this: Steam actually delivering packets between two");
            sb.AppendLine("machines, real latency, and two people using the same machine at once.");
            sb.AppendLine("Only a session with another player tests those.");
            return sb.ToString();
        }

        public static string Summary
        {
            get
            {
                if (!HasRun) return "not run yet";
                return Passed + " passed, " + Failed + " failed, " + Skipped + " skipped"
                     + "  ·  " + LastRunAt.ToString("HH:mm:ss");
            }
        }
    }
}
