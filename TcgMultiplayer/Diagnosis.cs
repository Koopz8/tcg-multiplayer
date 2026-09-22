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

        /// <summary>
        /// The player exists in a scene. This, not the economy globals, is what
        /// "in the game rather than the menu" means: the 254 economy globals are
        /// PlayMaker globals and they read perfectly well at the title screen,
        /// which is how the panel came to announce "save loaded" over the main
        /// menu with a wallet showing $30.
        /// </summary>
        public bool InWorld;

        /// <summary>Economy globals resolved. True at the menu too — not a save signal.</summary>
        public bool EconomyReadable;

        public int MachinesFound;
        /// <summary>How long we have been in a scene. Machines register a few seconds after a load.</summary>
        public double SecondsInWorld;
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

        /// <summary>How long after entering a scene before an empty machine list is odd.</summary>
        public const double MachineGraceSeconds = 15.0;

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

            // Almost everything the mod binds to only exists once you are in a
            // scene. Saying so is the whole point of this class.
            if (!s.InWorld)
                return Make(Verdict.Waiting,
                    "You're not in the game yet — most checks below can't run from the main menu.",
                    "Load your save, then open this panel again. Nothing here is a problem yet.");

            if (s.RetiredSubsystems > 0)
                return Make(Verdict.Warning,
                    s.RetiredSubsystems == 1
                        ? "One part of the mod switched itself off after repeated errors."
                        : s.RetiredSubsystems + " parts of the mod switched themselves off after repeated errors.",
                    "Press \"Try those again\" below. If it comes back, send MelonLoader\\Latest.log.");

            if (!s.EconomyReadable)
                return Make(Verdict.Warning,
                    "In the game, but the economy globals aren't readable.",
                    "The wallet guard can't protect your money without them. Worth reporting "
                    + "with your MelonLoader log.");

            if (s.CompatTotal > 0 && s.CompatFailed > 0)
                return Make(Verdict.Warning,
                    s.CompatFailed + " of " + s.CompatTotal
                        + " things the mod looks for aren't in this build of the game.",
                    "Multiplayer will be unreliable. This usually means the game updated — "
                    + "check for a newer mod version, and report the game version with the list below.");

            if (s.MachinesFound == 0)
                // They register a few seconds after a scene load, so zero is
                // normal for a moment and only a problem if it persists.
                return s.SecondsInWorld < MachineGraceSeconds
                    ? Make(Verdict.Waiting, "Looking for machines...", null)
                    : Make(Verdict.Warning,
                        "In the game, but no machines were found.",
                        "If you're outdoors this is normal — walk into the arcade. If you're "
                        + "standing among the cabinets, press Rescan, and report it if that "
                        + "doesn't help.");

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
