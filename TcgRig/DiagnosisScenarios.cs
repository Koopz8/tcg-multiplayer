using System;
using System.Collections.Generic;
using TcgMultiplayer;

namespace TcgRig
{
    /// <summary>
    /// The panel's one-line verdict. Pure logic, so it can be pinned down
    /// exactly — which matters more than usual, because this is the first thing
    /// a stranger from Nexus reads when something looks wrong.
    /// </summary>
    public static class DiagnosisScenarios
    {
        public static readonly List<Func<Check>> All = new List<Func<Check>>
        {
            MainMenuIsNotAnError,
            BepInExBeatsEverything,
            SteamIsGivenAGracePeriod,
            RetiredSubsystemsAreSurfaced,
            CompatFailuresOnlyCountWithASave,
            ReadyAndConnectedReadDifferently,
            EveryVerdictSaysSomething,
            EconomyGlobalsAreNotASaveSignal,
            MachinesGetAGracePeriod,
        };

        private static Check Run(string name, Action<Check> body)
        {
            var c = new Check { Name = name };
            try { body(c); c.Passed = true; }
            catch (Assert.Failed f) { c.Passed = false; c.Error = f.Message; }
            catch (Exception ex) { c.Passed = false; c.Error = ex.GetType().Name + ": " + ex.Message; }
            return c;
        }

        /// <summary>A healthy, in-game, nobody-connected baseline.</summary>
        private static Signals Healthy()
        {
            return new Signals
            {
                SteamReady = true,
                InWorld = true,
                EconomyReadable = true,
                MachinesFound = 14,
                SecondsInWorld = 120,
                CompatTotal = 12,
                CompatFailed = 0,
                BepInExPresent = false,
                RetiredSubsystems = 0,
                InSession = false,
                SecondsSinceLoad = 120,
            };
        }

        private static Check MainMenuIsNotAnError()
        {
            return Run("the main menu reads as waiting, not broken", c =>
            {
                // The screenshot that started this: a wall of red MISSING lines
                // at the main menu, every one of them working as designed.
                var s = Healthy();
                s.InWorld = false;
                s.MachinesFound = 0;
                s.SecondsInWorld = 0;
                s.CompatFailed = 9;        // all of them, because nothing is bound yet
                // The economy globals ARE readable at the title screen. That is
                // the whole trap this scenario exists to hold shut.
                s.EconomyReadable = true;

                var d = Diagnosis.Of(s);
                Assert.Eq(d.Verdict, Verdict.Waiting, "verdict is Waiting");
                Assert.True(!d.IsBad, "it is not flagged as a problem");
                Assert.True(d.Headline.IndexOf("not in the game", StringComparison.OrdinalIgnoreCase) >= 0,
                            "the headline says we're not in the game: " + d.Headline);
                Assert.True(d.NextStep != null && d.NextStep.IndexOf("Load", StringComparison.OrdinalIgnoreCase) >= 0,
                            "it tells them to load the game");
                Assert.True(d.Headline.IndexOf("MISSING", StringComparison.Ordinal) < 0,
                            "it does not shout about the failed checks");

                c.Detail = "\"" + d.Headline + "\"";
            });
        }

        private static Check BepInExBeatsEverything()
        {
            return Run("two mod loaders outrank every other complaint", c =>
            {
                // Worth being first: if this is the problem, the player may be
                // reading the log precisely because nothing loaded last time.
                var s = Healthy();
                s.BepInExPresent = true;
                s.InWorld = false;
                s.SteamReady = false;
                s.RetiredSubsystems = 3;
                s.CompatFailed = 9;

                var d = Diagnosis.Of(s);
                Assert.Eq(d.Verdict, Verdict.Broken, "verdict is Broken");
                Assert.True(d.Headline.IndexOf("BepInEx", StringComparison.Ordinal) >= 0,
                            "it names BepInEx");
                Assert.True(d.NextStep.IndexOf("Pick one", StringComparison.OrdinalIgnoreCase) >= 0,
                            "it says what to do");

                c.Detail = "wins over no-Steam, no-save, 3 retired and 9 compat failures";
            });
        }

        private static Check SteamIsGivenAGracePeriod()
        {
            return Run("a missing Steam is patience at first and a problem later", c =>
            {
                var s = Healthy();
                s.SteamReady = false;

                s.SecondsSinceLoad = 2;
                var early = Diagnosis.Of(s);
                Assert.Eq(early.Verdict, Verdict.Waiting, "two seconds in, just waiting");
                Assert.True(!early.IsBad, "nothing alarming yet");

                s.SecondsSinceLoad = Diagnosis.SettleSeconds + 5;
                var late = Diagnosis.Of(s);
                Assert.Eq(late.Verdict, Verdict.Broken, "half a minute in, that's a problem");
                Assert.True(late.NextStep.IndexOf("Steam", StringComparison.Ordinal) >= 0,
                            "it tells them to launch through Steam");

                c.Detail = "quiet for " + Diagnosis.SettleSeconds + "s, then explicit";
            });
        }

        private static Check RetiredSubsystemsAreSurfaced()
        {
            return Run("a subsystem switching itself off is a warning with a fix", c =>
            {
                var s = Healthy();
                s.RetiredSubsystems = 1;
                var one = Diagnosis.Of(s);
                Assert.Eq(one.Verdict, Verdict.Warning, "one retired is a warning");
                Assert.True(one.Headline.IndexOf("One part", StringComparison.Ordinal) >= 0,
                            "singular reads naturally: " + one.Headline);

                s.RetiredSubsystems = 3;
                var many = Diagnosis.Of(s);
                Assert.True(many.Headline.IndexOf("3 parts", StringComparison.Ordinal) >= 0,
                            "plural reads naturally: " + many.Headline);
                Assert.True(many.NextStep.IndexOf("Try those again", StringComparison.Ordinal) >= 0,
                            "it points at the button that fixes it");

                c.Detail = "singular and plural both read properly";
            });
        }

        private static Check CompatFailuresOnlyCountWithASave()
        {
            return Run("compat failures are only reported once they can be trusted", c =>
            {
                var s = Healthy();
                s.CompatFailed = 4;

                s.InWorld = false;
                Assert.Eq(Diagnosis.Of(s).Verdict, Verdict.Waiting,
                          "at the menu, they are not evidence of anything");

                s.InWorld = true;
                var d = Diagnosis.Of(s);
                Assert.Eq(d.Verdict, Verdict.Warning, "with a save, they are");
                Assert.True(d.Headline.IndexOf("4 of 12", StringComparison.Ordinal) >= 0,
                            "it gives the numbers: " + d.Headline);
                Assert.True(d.NextStep.IndexOf("updated", StringComparison.OrdinalIgnoreCase) >= 0,
                            "it names the likely cause");

                c.Detail = "suppressed at the menu, explicit in game";
            });
        }

        private static Check ReadyAndConnectedReadDifferently()
        {
            return Run("ready alone and ready connected say different things", c =>
            {
                var alone = Diagnosis.Of(Healthy());
                Assert.Eq(alone.Verdict, Verdict.Ready, "alone is Ready");
                Assert.True(alone.NextStep != null && alone.NextStep.IndexOf("Host", StringComparison.Ordinal) >= 0,
                            "and nudges them to host: " + alone.NextStep);

                var s = Healthy();
                s.InSession = true;
                var joined = Diagnosis.Of(s);
                Assert.Eq(joined.Verdict, Verdict.Ready, "connected is Ready");
                Assert.True(joined.NextStep == null, "and has nothing to nag about");
                Assert.True(joined.Headline.IndexOf("Connected", StringComparison.Ordinal) >= 0,
                            "it says so: " + joined.Headline);

                c.Detail = "\"" + alone.Headline + "\" vs \"" + joined.Headline + "\"";
            });
        }

        private static Check EconomyGlobalsAreNotASaveSignal()
        {
            return Run("readable economy globals do not mean you're in the game", c =>
            {
                // Shipped wrong once. The panel sat over the main menu announcing
                // "Save loaded, but no machines were found" with a wallet showing
                // $30, because the 254 economy globals are PlayMaker globals and
                // they resolve at the title screen like any other.
                var s = Healthy();
                s.InWorld = false;
                s.EconomyReadable = true;    // exactly what the menu looks like
                s.MachinesFound = 0;

                var d = Diagnosis.Of(s);
                Assert.Eq(d.Verdict, Verdict.Waiting, "still just waiting");
                Assert.True(d.Headline.IndexOf("machines", StringComparison.OrdinalIgnoreCase) < 0,
                            "it does not complain about machines: " + d.Headline);
                Assert.True(d.Headline.IndexOf("Save loaded", StringComparison.OrdinalIgnoreCase) < 0,
                            "and it does not claim a save is loaded: " + d.Headline);

                c.Detail = "\"" + d.Headline + "\"";
            });
        }

        private static Check MachinesGetAGracePeriod()
        {
            return Run("an empty machine list is patience first, a warning later", c =>
            {
                // They register a few seconds after a scene load, so zero is
                // normal for a moment.
                var s = Healthy();
                s.MachinesFound = 0;

                s.SecondsInWorld = 2;
                Assert.Eq(Diagnosis.Of(s).Verdict, Verdict.Waiting, "two seconds in, still looking");

                s.SecondsInWorld = Diagnosis.MachineGraceSeconds + 5;
                var late = Diagnosis.Of(s);
                Assert.Eq(late.Verdict, Verdict.Warning, "twenty seconds in, worth saying");
                Assert.True(late.NextStep.IndexOf("outdoors", StringComparison.OrdinalIgnoreCase) >= 0,
                            "and it allows for simply being outside: " + late.NextStep);

                c.Detail = "quiet for " + Diagnosis.MachineGraceSeconds + "s, then explains";
            });
        }

        private static Check EveryVerdictSaysSomething()
        {
            return Run("no combination of signals produces an empty or vague verdict", c =>
            {
                // Brute force every flag combination. The panel must never show
                // a blank line, and must never claim Ready while something is off.
                int checkedCount = 0;
                foreach (bool steam in new[] { false, true })
                foreach (bool inWorld in new[] { false, true })
                foreach (bool econ in new[] { false, true })
                foreach (bool bep in new[] { false, true })
                foreach (int retired in new[] { 0, 2 })
                foreach (int failed in new[] { 0, 3 })
                foreach (int machines in new[] { 0, 14 })
                foreach (bool inSession in new[] { false, true })
                foreach (double age in new[] { 1.0, 300.0 })
                {
                    var s = new Signals
                    {
                        SteamReady = steam, InWorld = inWorld, EconomyReadable = econ,
                        BepInExPresent = bep,
                        RetiredSubsystems = retired, CompatTotal = 12, CompatFailed = failed,
                        MachinesFound = machines, InSession = inSession,
                        SecondsSinceLoad = age, SecondsInWorld = age,
                    };
                    var d = Diagnosis.Of(s);
                    checkedCount++;

                    Assert.True(!string.IsNullOrWhiteSpace(d.Headline),
                                "every combination has a headline");
                    Assert.True(d.Headline.Length > 15, "and it is a sentence, not a token");

                    if (d.Verdict == Verdict.Ready)
                    {
                        Assert.True(steam && inWorld && econ && !bep && retired == 0
                                    && failed == 0 && machines > 0,
                                    "Ready is only claimed when everything really is ready");
                    }
                    if (d.Verdict == Verdict.Broken)
                        Assert.True(!string.IsNullOrEmpty(d.NextStep),
                                    "anything Broken tells the player what to do");
                }

                c.Detail = checkedCount + " signal combinations, all coherent";
            });
        }
    }
}
