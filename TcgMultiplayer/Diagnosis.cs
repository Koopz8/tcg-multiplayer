using System;

namespace TcgMultiplayer
{
    public enum Verdict
    {
        /// <summary>Nothing is wrong, there just isn't a save loaded yet.</summary>
        Waiting,
        /// <summary>Everything the mod needs is present.</summary>
        Ready,
        /// <summary>Works, but something is off and the player should know.</summary>
        Warning,
        /// <summary>Will not work until the player does something.</summary>
        Broken,
    }

    /// <summary>
    /// What one line should the panel say at the top?
    ///
    /// This exists because of a screenshot. Opening the panel at the main menu
    /// produced a wall of red MISSING lines — every check that can only pass
    /// once a save is loaded, failing exactly as designed, and reading to
    /// anyone sane as "this mod is broken". The mod was fine. The panel was
    /// lying by omission.
    ///
    /// Deliberately free of Unity and Steam so the rig can test it. The caller
    /// gathers the facts; this decides what they mean.
    /// </summary>
    public struct Signals
    {
        public bool SteamReady;
        /// <summary>Economy globals readable — the honest proxy for "a save is loaded".</summary>
        public bool SaveLoaded;
        public int MachinesFound;
        public int CompatTotal;
        public int CompatFailed;
        /// <summary>Another mod loader in the same game folder.</summary>
        public bool BepInExPresent;
        /// <summary>Subsystems the circuit breaker has retired for this session.</summary>
        public int RetiredSubsystems;
        public bool InSession;
        /// <summary>How long the mod has been alive. Nothing is alarming in the first few seconds.</summary>
        public double SecondsSinceLoad;
    }

    public struct Diagnosis
    {
        public Verdict Verdict;
        /// <summary>One line. What is true right now.</summary>
        public string Headline;
        /// <summary>One line. What to do about it, or null if there is nothing to do.</summary>
        public string NextStep;

        public bool IsBad { get { return Verdict == Verdict.Warning || Verdict == Verdict.Broken; } }

        /// <summary>Grace period before a missing Steam is worth mentioning.</summary>
        public const double SettleSeconds = 20.0;

        public static Diagnosis Of(Signals s)
        {
            // Worst first. Two mod loaders is the one that ends with the game
            // starting with no mods at all and no hint why.
            if (s.BepInExPresent)
                return Make(Verdict.Broken,
                    "BepInEx is installed in the same folder as MelonLoader.",
                    "Two mod loaders usually means the game starts with none of them. "
                    + "Pick one: the other Coin Game mods use BepInEx, this one uses MelonLoader.");

            if (!s.SteamReady)
                return s.SecondsSinceLoad < SettleSeconds
                    ? Make(Verdict.Waiting, "Waiting for the game to start Steam.", null)
                    : Make(Verdict.Broken,
                        "The game never started Steam, so multiplayer can't run.",
                        "Launch from Steam rather than from the .exe, and make sure Steam is "
                        + "running and signed in.");

            // Almost everything the mod binds to only exists once a save is
            // loaded. Saying so is the whole point of this class.
            if (!s.SaveLoaded)
                return Make(Verdict.Waiting,
                    "No save loaded yet — most checks below can't run from the main menu.",
                    "Load your game, then open this panel again. Nothing here is a problem yet.");

            if (s.RetiredSubsystems > 0)
                return Make(Verdict.Warning,
                    s.RetiredSubsystems == 1
                        ? "One part of the mod switched itself off after repeated errors."
                        : s.RetiredSubsystems + " parts of the mod switched themselves off after repeated errors.",
                    "Press \"Try those again\" below. If it comes back, send MelonLoader\\Latest.log.");

            if (s.CompatTotal > 0 && s.CompatFailed > 0)
                return Make(Verdict.Warning,
                    s.CompatFailed + " of " + s.CompatTotal
                        + " things the mod looks for aren't in this build of the game.",
                    "Multiplayer will be unreliable. This usually means the game updated — "
                    + "check for a newer mod version, and report the game version with the list below.");

            if (s.MachinesFound == 0)
                return Make(Verdict.Warning,
                    "Save loaded, but no machines were found.",
                    "Walk into the arcade and open this again. If it stays at zero, that's a bug "
                    + "worth reporting.");

            if (s.InSession)
                return Make(Verdict.Ready,
                    "Connected. " + s.MachinesFound + " machines tracked.", null);

            return Make(Verdict.Ready,
                "Ready. " + s.MachinesFound + " machines tracked, nobody connected yet.",
                "Press Host, then Invite friend.");
        }

        private static Diagnosis Make(Verdict v, string headline, string next)
        {
            return new Diagnosis { Verdict = v, Headline = headline, NextStep = next };
        }

        /// <summary>For the log and the copyable self-test report.</summary>
        public override string ToString()
        {
            return Headline + (string.IsNullOrEmpty(NextStep) ? "" : "  " + NextStep);
        }
    }
}
