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
            MovingDoesNotTouchYourResolution,
            AWindowIsNeverPlacedOffTheScreen,
            FullscreenMatchesTheMonitor,
            AResolutionTooBigForTheMonitorIsShrunk,
            EveryPlacementLandsOnTheMonitor,
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

        // ------------------------------------------------- placing the window
        //
        // These exist because the first version of the placement code shipped
        // and came back as "the game is cropped and isn't my natural
        // resolution". It forced the monitor's native resolution on every move
        // — which Unity then SAVED, so it outlived the move and made the mod
        // look innocent on the next launch — and it placed that full-size
        // window 40px in from the corner, hanging two edges off the screen.

        private static Check MovingDoesNotTouchYourResolution()
        {
            return Run("moving to another monitor leaves your chosen resolution alone", c =>
            {
                // Player runs 1600x900 windowed; target monitor is 1920x1080.
                var p = DisplayChoice.Place(1600, 900, 0, 0, 1920, 1040, 1920, 1080, false);

                Assert.Eq(p.Width, 1600, "width untouched");
                Assert.Eq(p.Height, 900, "height untouched");
                Assert.True(!p.ChangesResolution, "and nothing is reported as changed");
                c.Detail = "\"" + p.Why + "\"";
            });
        }

        private static Check AWindowIsNeverPlacedOffTheScreen()
        {
            return Run("a window the size of the screen lands ON the screen, not 40px past it", c =>
            {
                // The exact case that cropped the game: window as big as the work area.
                var p = DisplayChoice.Place(1920, 1080, 0, 0, 1920, 1080, 1920, 1080, false);

                Assert.Eq(p.X, 0, "left edge on the monitor");
                Assert.Eq(p.Y, 0, "top edge on the monitor");
                Assert.True(p.X + p.Width <= 1920, "right edge on the monitor");
                Assert.True(p.Y + p.Height <= 1080, "bottom edge on the monitor");
                c.Detail = "was (40,40) with a 1920x1080 window — 40px off two edges";
            });
        }

        private static Check FullscreenMatchesTheMonitor()
        {
            return Run("fullscreen does match the monitor, because there it's the right thing", c =>
            {
                var p = DisplayChoice.Place(1600, 900, 0, 0, 1920, 1040, 1920, 1080, true);

                Assert.Eq(p.Width, 1920, "native width");
                Assert.Eq(p.Height, 1080, "native height");
                Assert.True(p.ChangesResolution, "and it says it changed something");
                Assert.Eq(p.X, 0, "filling the monitor, so no offset");
                c.Detail = "fullscreen at a non-native size is the soft, letterboxed picture";
            });
        }

        private static Check AResolutionTooBigForTheMonitorIsShrunk()
        {
            return Run("1440p on a 1080p monitor is shrunk to fit, and says so", c =>
            {
                var p = DisplayChoice.Place(2560, 1440, 0, 0, 1920, 1040, 1920, 1080, false);

                Assert.True(p.Width <= 1920, "fits the width");
                Assert.True(p.Height <= 1040, "fits the work area height");
                Assert.True(p.ChangesResolution, "reported as a change");
                Assert.True(p.Why.IndexOf("doesn't fit", StringComparison.OrdinalIgnoreCase) >= 0,
                            "and explains itself: " + p.Why);
                c.Detail = "\"" + p.Why + "\"";
            });
        }

        private static Check EveryPlacementLandsOnTheMonitor()
        {
            return Run("sweep: no combination of resolution and monitor puts the window off-screen", c =>
            {
                int[] w = { 800, 1280, 1600, 1920, 2560, 3440 };
                int[] h = { 600, 720, 900, 1080, 1440 };
                int[] mw = { 1280, 1920, 2560, 3840 };
                int[] mh = { 720, 1080, 1440, 2160 };
                int[] ox = { 0, -1920, 2560 };          // monitors left of and right of the primary
                int n = 0;

                foreach (int cw in w)
                foreach (int ch in h)
                foreach (int m1 in mw)
                foreach (int m2 in mh)
                foreach (int x in ox)
                foreach (bool full in new[] { false, true })
                {
                    // Work area is the monitor minus a taskbar.
                    var p = DisplayChoice.Place(cw, ch, x, 0, m1, m2 - 40, m1, m2, full);
                    n++;

                    Assert.True(p.Width > 0 && p.Height > 0, "a usable resolution");
                    Assert.True(p.Width <= m1 && p.Height <= m2,
                                "never bigger than the monitor (" + p.Width + "x" + p.Height
                                + " on " + m1 + "x" + m2 + ")");
                    Assert.True(p.X >= x, "never left of the monitor");
                    Assert.True(p.X + p.Width <= x + m1,
                                "never past its right edge (" + p.X + "+" + p.Width + " on " + m1 + ")");
                    Assert.True(p.Y >= 0 && p.Y + p.Height <= m2, "never past top or bottom");
                    Assert.True(!string.IsNullOrEmpty(p.Why), "and always says what it did");
                }

                c.Detail = n + " placements, every one fully on its monitor";
            });
        }
    }
}
