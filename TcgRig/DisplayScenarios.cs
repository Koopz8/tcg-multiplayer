using System;
using System.Collections.Generic;
using TcgMultiplayer;

namespace TcgRig
{
    /// <summary>
    /// Picking a monitor. Moving the window is the easy half; the hard half is a
    /// saved preference surviving monitors being unplugged, reordered, added, or
    /// being two of the same model with the same name.
    ///
    /// This is also the half that strands someone with the game on a screen they
    /// cannot see, so it is worth pinning down.
    /// </summary>
    public static class DisplayScenarios
    {
        public static readonly List<Func<Check>> All = new List<Func<Check>>
        {
            NoPreferenceLeavesItAlone,
            NameMatchWins,
            AlreadyThereIsNotAMove,
            UnpluggedMonitorFallsBackToSlot,
            NothingMatchesLeavesItAlone,
            IdenticalMonitorsUseTheSavedSlot,
            ReorderedMonitorsStillMatchByName,
            CycleWraps,
            EveryPlanExplainsItself,
        };

        private static Check Run(string name, Action<Check> body)
        {
            var c = new Check { Name = name };
            try { body(c); c.Passed = true; }
            catch (Assert.Failed f) { c.Passed = false; c.Error = f.Message; }
            catch (Exception ex) { c.Passed = false; c.Error = ex.GetType().Name + ": " + ex.Message; }
            return c;
        }

        private static List<DisplaySpec> Setup(params string[] names)
        {
            var l = new List<DisplaySpec>();
            foreach (var n in names) l.Add(new DisplaySpec { Name = n, Width = 2560, Height = 1440 });
            return l;
        }

        private static Check NoPreferenceLeavesItAlone()
        {
            return Run("with nothing saved, the window is left where the game put it", c =>
            {
                var d = Setup("Main", "Side");
                var p = DisplayChoice.Resolve(d, "", -1, 0);
                Assert.True(!p.ShouldMove, "no move");
                Assert.True(p.Why.IndexOf("No preferred", StringComparison.OrdinalIgnoreCase) >= 0,
                            "and it says why: " + p.Why);
                c.Detail = "\"" + p.Why + "\"";
            });
        }

        private static Check NameMatchWins()
        {
            return Run("a saved monitor is found by name", c =>
            {
                var d = Setup("Side", "Main", "Vertical");
                var p = DisplayChoice.Resolve(d, "Main", 0, 2);
                Assert.True(p.ShouldMove, "it moves");
                Assert.Eq(p.Target, 1, "to the one actually called Main");
                c.Detail = "index 1, not the stale saved index 0";
            });
        }

        private static Check AlreadyThereIsNotAMove()
        {
            return Run("being on the right monitor already is not a move", c =>
            {
                // Otherwise the window gets shoved every single launch.
                var d = Setup("Main", "Side");
                var p = DisplayChoice.Resolve(d, "Main", 0, 0);
                Assert.True(!p.ShouldMove, "nothing to do");
                Assert.True(p.Why.IndexOf("Already", StringComparison.OrdinalIgnoreCase) >= 0,
                            "and it says so: " + p.Why);
                c.Detail = "\"" + p.Why + "\"";
            });
        }

        private static Check UnpluggedMonitorFallsBackToSlot()
        {
            return Run("an unplugged monitor falls back to the saved slot", c =>
            {
                var d = Setup("Main", "Side");          // "Vertical" is gone
                var p = DisplayChoice.Resolve(d, "Vertical", 1, 0);
                Assert.True(p.ShouldMove, "it still moves");
                Assert.Eq(p.Target, 1, "to the saved slot");
                Assert.True(p.Why.IndexOf("Vertical", StringComparison.Ordinal) >= 0,
                            "and names what it couldn't find: " + p.Why);
                c.Detail = "\"" + p.Why + "\"";
            });
        }

        private static Check NothingMatchesLeavesItAlone()
        {
            return Run("if the saved monitor and slot are both gone, nothing moves", c =>
            {
                // The alternative is shoving the window onto an arbitrary screen,
                // which is exactly the complaint this feature exists to fix.
                var d = Setup("Main");
                var p = DisplayChoice.Resolve(d, "Vertical", 2, 0);
                Assert.True(!p.ShouldMove, "left alone");
                Assert.True(p.Why.IndexOf("isn't connected", StringComparison.OrdinalIgnoreCase) >= 0,
                            "and explains: " + p.Why);
                c.Detail = "\"" + p.Why + "\"";
            });
        }

        private static Check IdenticalMonitorsUseTheSavedSlot()
        {
            return Run("two monitors of the same model are told apart by slot", c =>
            {
                var d = Setup("ASUS VG248", "ASUS VG248", "Main");
                var p = DisplayChoice.Resolve(d, "ASUS VG248", 1, 2);
                Assert.True(p.ShouldMove, "it moves");
                Assert.Eq(p.Target, 1, "to the saved one of the two");

                // And if the saved slot isn't one of them, take the first rather
                // than give up.
                var p2 = DisplayChoice.Resolve(d, "ASUS VG248", 2, 2);
                Assert.Eq(p2.Target, 0, "falls back to the first match");

                c.Detail = "slot disambiguates, first match as fallback";
            });
        }

        private static Check ReorderedMonitorsStillMatchByName()
        {
            return Run("reordering monitors does not send the game to the wrong one", c =>
            {
                // The index alone would be wrong here. This is why name wins.
                var before = Setup("Main", "Side");
                var after = Setup("Side", "Main");

                var p = DisplayChoice.Resolve(after, "Main", 0, 0);
                Assert.Eq(p.Target, 1, "followed the name, not the old index");
                Assert.True(before[0].Name == "Main" && after[1].Name == "Main",
                            "sanity: the monitor did move slots");

                c.Detail = "saved slot 0, correct answer slot 1";
            });
        }

        private static Check CycleWraps()
        {
            return Run("the blind cycle key wraps and copes with one monitor", c =>
            {
                Assert.Eq(DisplayChoice.Next(3, 0), 1, "0 -> 1");
                Assert.Eq(DisplayChoice.Next(3, 2), 0, "wraps at the end");
                Assert.Eq(DisplayChoice.Next(1, 0), -1, "one monitor: nowhere to go");
                Assert.Eq(DisplayChoice.Next(0, -1), -1, "no monitors: nowhere to go");
                Assert.Eq(DisplayChoice.Next(2, -1), 0, "unknown current: start at the first");
                c.Detail = "wraps, and never returns an index it shouldn't";
            });
        }

        private static Check EveryPlanExplainsItself()
        {
            return Run("every combination produces a sentence and a valid target", c =>
            {
                var layouts = new List<List<DisplaySpec>>
                {
                    new List<DisplaySpec>(),
                    Setup("Main"),
                    Setup("Main", "Side"),
                    Setup("Dup", "Dup", "Main"),
                };
                int n = 0;
                foreach (var d in layouts)
                foreach (var name in new[] { null, "", "Main", "Gone", "Dup" })
                foreach (var saved in new[] { -1, 0, 1, 5 })
                foreach (var cur in new[] { -1, 0, 1 })
                {
                    var p = DisplayChoice.Resolve(d, name, saved, cur);
                    n++;
                    Assert.True(!string.IsNullOrWhiteSpace(p.Why), "always a reason");
                    Assert.True(p.Why.Length > 10, "a sentence, not a token");
                    if (p.ShouldMove)
                    {
                        Assert.True(p.Target < d.Count, "never points past the end");
                        Assert.True(p.Target != cur, "never a pointless move");
                    }
                }
                c.Detail = n + " combinations, every target in range";
            });
        }
    }
}
