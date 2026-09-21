using System;
using System.Collections.Generic;

// The rig links Session.cs straight out of the mod. These stand in for the
// three statics it reaches for that would otherwise drag MelonLoader and the
// Unity runtime in with them. Keeping them here rather than behind #if means
// the shipped file has no test scaffolding in it at all.
namespace TcgMultiplayer
{
    internal static class Plugin
    {
        public static string Version = "0.9.8";

        /// <summary>Everything the sessions logged, for assertions and for the -v dump.</summary>
        public static readonly List<string> Lines = new List<string>();
        public static readonly List<string> Warnings = new List<string>();
        public static bool Echo;

        public static void Log(string s)
        {
            Lines.Add(s);
            if (Echo) Console.WriteLine("    log: " + s);
        }

        public static void Warn(string s)
        {
            Warnings.Add(s);
            Lines.Add("WARN " + s);
            if (Echo) Console.WriteLine("    warn: " + s);
        }

        public static void Reset() { Lines.Clear(); Warnings.Clear(); }

        public static bool Logged(string fragment)
        {
            return Lines.Exists(l => l.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static int WarnCount(string fragment)
        {
            return Warnings.FindAll(l => l.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0).Count;
        }
    }

    internal static class CompatCheck
    {
        public static string GameHash = "c5a1700dfaf5e872";
        public static void Set(string what, bool ok, string detail) { }
    }

    internal static class SteamBridge
    {
        public static bool Initialized { get { return true; } }
    }
}
