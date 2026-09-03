using System;
using System.IO;
using UnityEngine;

namespace TcgMultiplayer.Game
{
    /// <summary>
    /// Copies the player's save aside before a session starts.
    ///
    /// WorldState hands a guest the host's island for the duration of a visit
    /// and puts their own progression back when they leave. That restore covers
    /// every ordinary exit, but it cannot cover a crash, a power cut, or the
    /// game writing a save at an awkward moment — and the cost of being wrong is
    /// somebody's hundred hours.
    ///
    /// A backup is far cheaper than being clever. The game keeps its save in
    /// Unity's persistent data folder (both EasySave 2 and 3 ship with the game
    /// and default there), so the whole folder is copied to a timestamped
    /// directory before anyone connects. Five are kept.
    /// </summary>
    internal static class SaveGuard
    {
        public const string BackupFolderName = "TcgMultiplayer_SaveBackups";

        /// <summary>Refuse to copy a folder larger than this — something is not what we think it is.</summary>
        private const long SizeCapBytes = 256L * 1024 * 1024;

        private const int KeepMostRecent = 5;

        public static string LastBackupPath { get; private set; }
        public static string LastError { get; private set; }
        public static bool BackedUpThisSession { get; private set; }

        public static string SaveFolder
        {
            get
            {
                try { return Application.persistentDataPath; }
                catch { return null; }
            }
        }

        /// <summary>
        /// Called when a session is about to begin. Once per session, not once
        /// per launch — someone who plays with a friend, leaves, grinds solo for
        /// two hours and then joins a second lobby needs a backup that includes
        /// those two hours, not the one from before dinner.
        /// </summary>
        public static bool BackupOnce()
        {
            if (BackedUpThisSession) return true;
            BackedUpThisSession = true;   // set first: a failure should not retry every frame
            return Backup() != null;
        }

        /// <summary>Called when a session ends, so the next one backs up again.</summary>
        public static void ArmForNextSession()
        {
            BackedUpThisSession = false;
        }

        /// <summary>Forced backup, for the overlay button. Always makes a new copy.</summary>
        public static string Backup()
        {
            LastError = null;
            try
            {
                var src = SaveFolder;
                if (string.IsNullOrEmpty(src) || !Directory.Exists(src))
                {
                    LastError = "could not locate the save folder";
                    Plugin.Warn("Save backup skipped: " + LastError + ".");
                    return null;
                }

                var root = Path.Combine(src, BackupFolderName);
                long size = MeasureExcluding(src, root);
                if (size > SizeCapBytes)
                {
                    LastError = "save folder is " + (size / (1024 * 1024)) + " MB, larger than expected";
                    Plugin.Warn("Save backup skipped: " + LastError + ". Back it up yourself before playing.");
                    return null;
                }

                var dest = Path.Combine(root, DateTime.Now.ToString("yyyy-MM-dd_HHmmss"));
                Directory.CreateDirectory(dest);

                int files = CopyExcluding(src, dest, root);
                Prune(root);

                LastBackupPath = dest;
                Plugin.Log("Save backed up (" + files + " files, " + (size / 1024) + " KB) to " + dest);
                return dest;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Plugin.Warn("Save backup failed: " + ex.Message
                            + ". Playing on, but back your save up by hand if it matters to you.");
                return null;
            }
        }

        private static long MeasureExcluding(string dir, string skip)
        {
            long total = 0;
            foreach (var f in Directory.GetFiles(dir))
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
            foreach (var d in Directory.GetDirectories(dir))
            {
                if (SamePath(d, skip)) continue;
                total += MeasureExcluding(d, skip);
            }
            return total;
        }

        private static int CopyExcluding(string src, string dest, string skip)
        {
            int n = 0;
            Directory.CreateDirectory(dest);

            foreach (var f in Directory.GetFiles(src))
            {
                try
                {
                    File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
                    n++;
                }
                catch (Exception ex) { Plugin.Warn("Could not copy " + Path.GetFileName(f) + ": " + ex.Message); }
            }

            foreach (var d in Directory.GetDirectories(src))
            {
                if (SamePath(d, skip)) continue;
                n += CopyExcluding(d, Path.Combine(dest, Path.GetFileName(d)), skip);
            }
            return n;
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Keep the newest few. Old backups are not a service we are offering.</summary>
        private static void Prune(string root)
        {
            try
            {
                var dirs = Directory.GetDirectories(root);
                if (dirs.Length <= KeepMostRecent) return;
                Array.Sort(dirs, StringComparer.Ordinal);   // names are sortable timestamps
                for (int i = 0; i < dirs.Length - KeepMostRecent; i++)
                {
                    try { Directory.Delete(dirs[i], true); } catch { }
                }
            }
            catch { }
        }

        public static string Status
        {
            get
            {
                if (!string.IsNullOrEmpty(LastError)) return "FAILED — " + LastError;
                if (string.IsNullOrEmpty(LastBackupPath)) return "not yet (runs when you host or join)";
                return "saved to " + BackupFolderName + "\\" + Path.GetFileName(LastBackupPath);
            }
        }
    }
}
