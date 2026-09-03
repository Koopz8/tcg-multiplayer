using System;
using System.Reflection;
using HarmonyLib;
using Steamworks;

namespace TcgMultiplayer
{
    /// <summary>
    /// Stops a multiplayer session writing to Steam achievements, stats or
    /// leaderboards.
    ///
    /// This was flagged as a release blocker in the very first teardown and it is
    /// not a nicety. The Coin Game submits to around 48 leaderboards, and M3 makes
    /// a friend's round play out on your machine — so without this, watching
    /// someone win Skee Ball can post their score under your name. That is a cheat
    /// vector, and it is the fastest way for a mod to get disowned by the
    /// developer it depends on.
    ///
    /// Everything routes through Steamworks.NET's SteamUserStats in the end — the
    /// PlayMaker action pack, HLG's achievement service, and LapinerTools'
    /// leaderboard uploader all call the same handful of methods — so patching
    /// there closes every route at once rather than chasing each caller.
    /// </summary>
    internal static class StatsLock
    {
        /// <summary>Set by the plugin: true while a session or a rehearsal is running.</summary>
        public static Func<bool> ShouldBlock;

        public static int BlockedAchievements, BlockedStats, BlockedLeaderboards;
        public static bool Patched { get; private set; }

        public static bool Active
        {
            get { return ShouldBlock != null && ShouldBlock(); }
        }

        public static int TotalBlocked
        {
            get { return BlockedAchievements + BlockedStats + BlockedLeaderboards; }
        }

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            int ok = 0;

            ok += Patch(harmony, typeof(SteamUserStats), "SetAchievement",
                        new[] { typeof(string) }, nameof(BlockAchievement));
            ok += Patch(harmony, typeof(SteamUserStats), "IndicateAchievementProgress",
                        new[] { typeof(string), typeof(uint), typeof(uint) }, nameof(BlockAchievement));
            ok += Patch(harmony, typeof(SteamUserStats), "SetStat",
                        new[] { typeof(string), typeof(int) }, nameof(BlockStat));
            ok += Patch(harmony, typeof(SteamUserStats), "SetStat",
                        new[] { typeof(string), typeof(float) }, nameof(BlockStat));
            ok += Patch(harmony, typeof(SteamUserStats), "StoreStats",
                        Type.EmptyTypes, nameof(BlockStoreStats));
            ok += Patch(harmony, typeof(SteamUserStats), "UploadLeaderboardScore",
                        new[] { typeof(SteamLeaderboard_t), typeof(ELeaderboardUploadScoreMethod),
                                typeof(int), typeof(int[]), typeof(int) }, nameof(BlockLeaderboard));

            Patched = ok > 0;
            Plugin.Log("Stats lock: " + ok + " Steam entry points guarded"
                       + (ok < 6 ? " (some not found — the Steamworks version may have moved)" : "") + ".");
        }

        private static int Patch(HarmonyLib.Harmony h, Type type, string method, Type[] args, string prefix)
        {
            try
            {
                var target = AccessTools.Method(type, method, args);
                if (target == null)
                {
                    Plugin.Warn("Stats lock: could not find " + type.Name + "." + method);
                    return 0;
                }
                h.Patch(target, prefix: new HarmonyMethod(
                    typeof(StatsLock).GetMethod(prefix, BindingFlags.NonPublic | BindingFlags.Static)));
                return 1;
            }
            catch (Exception ex)
            {
                Plugin.Warn("Stats lock: failed to patch " + method + " — " + ex.Message);
                return 0;
            }
        }

        // Prefixes return false to skip the original. The game reads these results
        // and mostly ignores them, so reporting failure is safer than reporting a
        // success that never happened.

        private static bool BlockAchievement(ref bool __result)
        {
            if (!Active) return true;
            BlockedAchievements++;
            __result = false;
            return false;
        }

        private static bool BlockStat(ref bool __result)
        {
            if (!Active) return true;
            BlockedStats++;
            __result = false;
            return false;
        }

        private static bool BlockStoreStats(ref bool __result)
        {
            if (!Active) return true;
            BlockedStats++;
            __result = false;
            return false;
        }

        private static bool BlockLeaderboard(ref SteamAPICall_t __result)
        {
            if (!Active) return true;
            BlockedLeaderboards++;
            __result = default(SteamAPICall_t);   // k_uAPICallInvalid
            return false;
        }

        public static string Status
        {
            get
            {
                if (!Patched) return "NOT ACTIVE — patches failed, do not release";
                if (!Active) return "idle (single player — achievements work normally)";
                return "blocking: " + BlockedAchievements + " achievements, "
                       + BlockedStats + " stats, " + BlockedLeaderboards + " leaderboard uploads";
            }
        }
    }
}
