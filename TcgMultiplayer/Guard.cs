using System;
using System.Collections.Generic;

namespace TcgMultiplayer
{
    /// <summary>
    /// Keeps a bug in this mod from becoming a bug in someone's game.
    ///
    /// MelonLoader calls OnUpdate and OnGUI every frame. An exception thrown in
    /// there isn't caught by anything above us, so on a public release a single
    /// bad frame becomes sixty log lines a second, a log file that grows without
    /// bound, and a framerate somewhere near zero. The player's report is "the
    /// mod broke my game", and it is nearly true.
    ///
    /// So every per-frame subsystem runs through here. The first few failures
    /// are logged in full because that is what makes them fixable. After that
    /// the subsystem is switched off, once, with a line saying what died and
    /// what still works — the rest of the mod keeps running, and the game keeps
    /// running at full speed.
    /// </summary>
    internal static class Guard
    {
        /// <summary>Failures tolerated before a subsystem is retired for the session.</summary>
        public const int Tolerance = 5;

        private sealed class Slot
        {
            public int Failures;
            public bool Disabled;
            public string LastError;
        }

        private static readonly Dictionary<string, Slot> _slots =
            new Dictionary<string, Slot>(StringComparer.Ordinal);

        private static Slot Get(string name)
        {
            Slot s;
            if (!_slots.TryGetValue(name, out s)) { s = new Slot(); _slots[name] = s; }
            return s;
        }

        private static readonly System.Diagnostics.Stopwatch _watch = new System.Diagnostics.Stopwatch();

        /// <summary>
        /// Runs <paramref name="body"/>, absorbing anything it throws and timing
        /// it on the way past. Everything per-frame already comes through here,
        /// so this is the cheapest honest place to measure what the mod costs.
        /// </summary>
        public static void Run(string name, Action body)
        {
            var slot = Get(name);
            if (slot.Disabled) return;

            long start = _watch.IsRunning ? _watch.ElapsedTicks : Restart();
            try { body(); }
            catch (Exception ex)
            {
                slot.Failures++;
                slot.LastError = ex.GetType().Name + ": " + ex.Message;

                if (slot.Failures <= Tolerance)
                {
                    Plugin.Warn(name + " threw (" + slot.Failures + " of " + Tolerance + "): " + ex);
                }
                else
                {
                    slot.Disabled = true;
                    Plugin.Warn(name + " has failed " + slot.Failures + " times and is now switched off "
                                + "for the rest of this session. The rest of the mod carries on. "
                                + "Last error: " + slot.LastError);
                }
            }
            finally
            {
                Perf.Record(name, (float)((_watch.ElapsedTicks - start) * 1000.0
                                          / System.Diagnostics.Stopwatch.Frequency));
            }
        }

        private static long Restart()
        {
            _watch.Start();
            return _watch.ElapsedTicks;
        }

        public static bool IsDisabled(string name)
        {
            Slot s;
            return _slots.TryGetValue(name, out s) && s.Disabled;
        }

        /// <summary>True if anything at all has fallen over. The overlay shows this.</summary>
        public static bool AnythingBroken
        {
            get
            {
                foreach (var kv in _slots) if (kv.Value.Disabled) return true;
                return false;
            }
        }

        /// <summary>One line per broken subsystem, for the overlay and for bug reports.</summary>
        public static List<string> Broken
        {
            get
            {
                var list = new List<string>();
                foreach (var kv in _slots)
                    if (kv.Value.Disabled) list.Add(kv.Key + " — " + kv.Value.LastError);
                return list;
            }
        }

        /// <summary>Lets the player retry after a hiccup rather than restarting the game.</summary>
        public static int ResetAll()
        {
            int n = 0;
            foreach (var kv in _slots)
                if (kv.Value.Disabled) { kv.Value.Disabled = false; kv.Value.Failures = 0; n++; }
            if (n > 0) Plugin.Log("Re-enabled " + n + " subsystem(s) that had been switched off.");
            return n;
        }
    }
}
