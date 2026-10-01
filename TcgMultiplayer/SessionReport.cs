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
    /// Writes down what actually happened, every time a session ends.
    ///
    /// A beta tester's report is usually "we played for a bit and it went a bit
    /// weird near the coin pusher". That is not their fault — they were playing
    /// a game, not taking notes — but it is unfixable. The numbers that would
    /// make it fixable all exist while the session is running and then vanish
    /// the moment it ends.
    ///
    /// So they get written to a file instead, one per session, next to the
    /// MelonLoader log. A tester sends the file, or presses Copy and pastes it.
    /// Two of these from opposite sides of the same session is usually enough to
    /// find a desync without anyone having to reproduce it.
    ///
    /// Nothing is transmitted anywhere. It is a text file on their disk that
    /// they choose to send, which is the only version of this worth building.
    /// </summary>
    internal static class SessionReport
    {
        public const string FolderName = "TcgMultiplayer_Reports";

        public static string Last { get; private set; }
        public static string LastPath { get; private set; }

        private static string Folder
        {
            get
            {
                try
                {
                    var baseDir = Path.GetDirectoryName(Application.dataPath);   // the game folder
                    return Path.Combine(baseDir ?? ".", FolderName);
                }
                catch { return FolderName; }
            }
        }

        public static void Capture(Session s, MachineDirector m, WorldState w)
        {
            try
            {
                Last = Build(s, m, w);
                LastPath = Write(Last, s);
                Plugin.Log("Session report written to " + (LastPath ?? "(memory only)"));
            }
            catch (Exception ex)
            {
                Plugin.Warn("Could not write the session report: " + ex.Message);
            }
        }

        private static string Write(string text, Session s)
        {
            try
            {
                var dir = Folder;
                Directory.CreateDirectory(dir);
                Prune(dir);

                var name = "session_" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss")
                         + (s != null && s.IsHost ? "_host" : "_guest") + ".txt";
                var path = Path.Combine(dir, name);
                File.WriteAllText(path, text, Encoding.UTF8);
                return path;
            }
            catch (Exception ex)
            {
                Plugin.Warn("Report folder not writable (" + ex.Message + ") — kept in memory, use Copy.");
                return null;
            }
        }

        /// <summary>Keep the last 20. Nobody needs a year of these.</summary>
        private static void Prune(string dir)
        {
            try
            {
                var files = Directory.GetFiles(dir, "session_*.txt");
                if (files.Length < 20) return;
                Array.Sort(files, StringComparer.Ordinal);
                for (int i = 0; i < files.Length - 19; i++)
                {
                    try { File.Delete(files[i]); } catch { }
                }
            }
            catch { }
        }

        private static string Build(Session s, MachineDirector m, WorldState w)
        {
            var sb = new StringBuilder();
            void Line(string k, object v) { sb.AppendLine(k.PadRight(24) + v); }

            sb.AppendLine("The Coin Game - Multiplayer : session report");
            sb.AppendLine("===========================================");
            sb.AppendLine();

            Line("mod version", Plugin.Version);
            Line("game build", CompatCheck.GameHash ?? "unknown");
            Line("health", CompatCheck.Summary);
            sb.AppendLine();

            // --- the session
            if (s != null)
            {
                var dur = s.StartedAt == default(DateTime)
                    ? TimeSpan.Zero : DateTime.Now - s.StartedAt;
                Line("role", s.IsHost ? "host" : "guest");
                Line("started", s.StartedAt == default(DateTime) ? "?" : s.StartedAt.ToString("HH:mm:ss"));
                Line("lasted", FormatDuration(dur));
                Line("ended because", string.IsNullOrEmpty(s.EndReason) ? "you pressed Leave, or quit" : s.EndReason);
                Line("most players at once", s.PeakPeers + 1);
                Line("packets", s.PacketsSent + " sent, " + s.PacketsReceived + " received");
                Line("data", (s.BytesSent / 1024) + " KB sent, " + (s.BytesReceived / 1024) + " KB received");

                // What the bytes actually were. Before this the totals only
                // counted the handful of packets that went through SendOn, so
                // the heavy traffic - positions, coin poses, the cabinet
                // screen - was invisible and a busy session looked free.
                {
                    var secs = dur.TotalSeconds > 1 ? dur.TotalSeconds : 1;
                    var names = System.Enum.GetNames(typeof(Net.Op));
                    var values = (Net.Op[])System.Enum.GetValues(typeof(Net.Op));
                    bool any = false;
                    for (int i = 0; i < values.Length; i++)
                    {
                        int op = (int)values[i];
                        if (op < 0 || op >= 64 || s.BytesOutByOp[op] <= 0) continue;
                        if (!any) { sb.AppendLine("   what was sent:"); any = true; }
                        Line("      " + names[i],
                             (s.BytesOutByOp[op] / 1024) + " KB over " + s.PacketsOutByOp[op] + " packets"
                             + "  (" + (s.BytesOutByOp[op] / secs / 1024).ToString("0.0") + " KB/s)");
                    }
                }

                if (!string.IsNullOrEmpty(s.BuildMismatch)) Line("BUILD MISMATCH", s.BuildMismatch);
                if (!string.IsNullOrEmpty(s.RefusedReason)) Line("REFUSED", s.RefusedReason);

                sb.AppendLine();
                sb.AppendLine("Players");
                if (s.Peers.Count == 0) sb.AppendLine("   nobody else connected");
                foreach (var p in s.Peers)
                {
                    sb.AppendLine("   " + p.Name.PadRight(20)
                        + "mod " + (string.IsNullOrEmpty(p.ModVersion) ? "?" : p.ModVersion)
                        + "   " + (p.Handshaked ? "handshaked" : "NEVER HANDSHAKED")
                        + "   " + (p.RttMs >= 0 ? p.RttMs.ToString("0") + " ms" : "no ping")
                        + (p.BadPackets > 0 ? "   " + p.BadPackets + " BAD PACKETS" : ""));
                }
            }
            sb.AppendLine();

            // --- gameplay
            if (m != null)
            {
                sb.AppendLine("Machines");
                Line("   registered", m.MachineCount);
                Line("   events mirrored", m.MirroredEventsSent + " sent, " + m.MirroredEventsApplied + " applied");
                Line("   physics bodies", m.Physics.BodiesSent + " sent, " + m.Physics.BodiesApplied + " applied");
                Line("   count mismatches", m.Physics.CountMismatches
                     + (m.Physics.CountMismatches > 0 ? "   <-- the two sides disagreed about a machine" : ""));
                if (m.Physics.UnknownKeys > 0)
                    Line("   waited on a key", m.Physics.UnknownKeys
                         + " packets arrived naming a body we hadn't been told about yet");
                if (m.Physics.StandInsBuilt > 0 || m.Physics.StandInMisses > 0)
                    Line("   stood in for", m.Physics.StandInsBuilt + " objects their round spawned and ours didn't"
                         + (m.Physics.StandInMisses > 0
                            ? ", " + m.Physics.StandInMisses + " WITH NO PREFAB TO SHOW" : ""));
                sb.AppendLine();

                // One row per cabinet you watched somebody else play. Only two
                // machine types have ever been checked by eye, so this is how
                // the other twenty-eight get checked.
                var watched = m.Watch.Text();
                if (watched != null)
                {
                    sb.AppendLine("Machines you watched someone else play");
                    sb.Append(watched);
                    sb.AppendLine();
                }

                if (m.Items.Carried > 0 || m.Items.CarryMisses > 0 || m.Items.Shown > 0)
                    Line("   loose items", m.Items.Shown + " of theirs shown, "
                         + m.Items.Carried + " of those were in their hands"
                         + (m.Items.CarryMisses > 0
                            ? ", " + m.Items.CarryMisses + " arrived before we had a body to hang them on" : ""));
                sb.AppendLine();

                if (m.RoundsBlockedByOwner > 0)
                    Line("   rounds that couldn't run", m.RoundsBlockedByOwner
                         + "   <-- you started a machine somebody else still held");
                sb.AppendLine();

                sb.AppendLine("Your money");
                if (!m.Wallet.Available) sb.AppendLine("   economy globals were never readable");
                else
                {
                    Line("   protected globals", m.Wallet.ProtectedCount);
                    Line("   payouts blocked", m.Wallet.RestoreCount
                         + (m.Wallet.RestoreCount > 0 ? "   (this is the guard working, not an error)" : ""));
                }
                sb.AppendLine();
            }

            if (w != null)
            {
                sb.AppendLine("Shared island");
                Line("   tracked globals", w.TrackedCount);
                Line("   changes", w.ChangesSent + " sent, " + w.ChangesApplied + " applied");
                // Captured deliberately BEFORE the restore runs, so a guest's
                // report always says yes and used to shout about it — a false
                // alarm on every guest report ever written. It is only evidence
                // of a problem in a report that survived a crash.
                Line("   still visiting?", w.Visiting
                     ? "yes - your own progression goes back on the moment this report is written"
                     : "no");
                sb.AppendLine();
            }

            if (Game.BodyProps.Applied > 0)
                Line("Bodies tidied up", Game.BodyProps.Switched + " things switched over "
                     + Game.BodyProps.Applied + " passes; last pass left "
                     + Game.BodyProps.Skipped + " parts alone, having no match on the other body");
            sb.AppendLine();

            Line("Steam stats", StatsLock.Status);
            sb.AppendLine();

            // --- anything that fell over
            sb.AppendLine("Problems");
            if (Guard.AnythingBroken)
            {
                foreach (var b in Guard.Broken) sb.AppendLine("   BROKE: " + b);
            }
            else sb.AppendLine("   nothing switched itself off");
            sb.AppendLine();

            Line("worst frame", Perf.WorstMs.ToString("0.0") + " ms");
            Line("mod cost", Perf.ModMsThisFrame.ToString("0.00") + " ms/frame");
            sb.AppendLine();

            sb.AppendLine("Self-test: " + SelfTest.Summary);
            sb.AppendLine();
            sb.AppendLine("Send this together with MelonLoader\\Latest.log, and ideally with the");
            sb.AppendLine("same two files from whoever you were playing with — one side of a");
            sb.AppendLine("desync rarely explains itself.");

            return sb.ToString();
        }

        private static string FormatDuration(TimeSpan t)
        {
            if (t.TotalSeconds < 1) return "moments";
            if (t.TotalMinutes < 1) return (int)t.TotalSeconds + "s";
            return (int)t.TotalMinutes + "m " + t.Seconds + "s";
        }

        public static string Status
        {
            get
            {
                if (string.IsNullOrEmpty(Last)) return "none yet (written when a session ends)";
                return LastPath != null
                    ? "saved to " + FolderName + "\\" + Path.GetFileName(LastPath)
                    : "held in memory — use Copy";
            }
        }
    }
}
