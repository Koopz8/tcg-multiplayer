using System;
using System.Collections.Generic;

namespace TcgMultiplayer
{
    /// <summary>One monitor, as far as the decision below is concerned.</summary>
    public struct DisplaySpec
    {
        public string Name;
        public int Width, Height;

        public override string ToString()
        {
            return (string.IsNullOrEmpty(Name) ? "display" : Name) + " " + Width + "x" + Height;
        }
    }

    public struct DisplayPlan
    {
        /// <summary>Index to move to, or -1 to leave the window where it is.</summary>
        public int Target;
        /// <summary>Why, in words, for the panel and the log.</summary>
        public string Why;

        public bool ShouldMove { get { return Target >= 0; } }
    }

    /// <summary>
    /// Which monitor should the game be on?
    ///
    /// The base game has no setting for this, so the mod remembers one. The
    /// awkward part isn't moving the window, it's that a saved preference has to
    /// survive the monitor being unplugged, a new one being added, the order
    /// changing, and two identical monitors being indistinguishable by size.
    ///
    /// Names are more stable than indices, so a name match wins; the index is
    /// the fallback. No Unity in here, so all of that is testable.
    /// </summary>
    public static class DisplayChoice
    {
        /// <param name="displays">The monitors as the OS reports them right now.</param>
        /// <param name="savedName">Remembered monitor name, or null/empty if none.</param>
        /// <param name="savedIndex">Remembered index, or -1.</param>
        /// <param name="currentIndex">Where the window is now, or -1 if unknown.</param>
        public static DisplayPlan Resolve(IList<DisplaySpec> displays, string savedName,
                                          int savedIndex, int currentIndex)
        {
            if (displays == null || displays.Count == 0)
                return Plan(-1, "No monitors reported yet.");

            bool haveName = !string.IsNullOrEmpty(savedName);
            bool haveIndex = savedIndex >= 0;

            if (!haveName && !haveIndex)
                return Plan(-1, "No preferred monitor saved — leaving the window where the game put it.");

            // Name first: it survives the order changing, which an index does not.
            var byName = new List<int>();
            if (haveName)
                for (int i = 0; i < displays.Count; i++)
                    if (string.Equals(displays[i].Name, savedName, StringComparison.Ordinal))
                        byName.Add(i);

            int target;
            string why;

            if (byName.Count == 1)
            {
                target = byName[0];
                why = "Matched your saved monitor by name (" + savedName + ").";
            }
            else if (byName.Count > 1)
            {
                // Two monitors of the same model report the same name. Prefer the
                // remembered slot if it is one of them, otherwise the first.
                if (haveIndex && byName.Contains(savedIndex))
                {
                    target = savedIndex;
                    why = "Several monitors are called \"" + savedName + "\" — used the one in the saved slot.";
                }
                else
                {
                    target = byName[0];
                    why = "Several monitors are called \"" + savedName + "\" — used the first.";
                }
            }
            else if (haveIndex && savedIndex < displays.Count)
            {
                target = savedIndex;
                why = haveName
                    ? "Couldn't find \"" + savedName + "\" — fell back to monitor " + (savedIndex + 1) + "."
                    : "Used saved monitor " + (savedIndex + 1) + ".";
            }
            else
            {
                return Plan(-1, haveName
                    ? "Your saved monitor (\"" + savedName + "\") isn't connected, and slot "
                      + (savedIndex + 1) + " doesn't exist either. Left it alone."
                    : "Saved monitor " + (savedIndex + 1) + " isn't connected. Left it alone.");
            }

            if (target == currentIndex)
                return Plan(-1, "Already on " + displays[target] + ".");

            return Plan(target, why);
        }

        /// <summary>Where the window goes, and at what size, once a monitor is chosen.</summary>
        public struct Placement
        {
            public int X, Y;              // desktop coordinates for the window's top-left
            public int Width, Height;     // the resolution to run at
            public bool ChangesResolution;
            public string Why;
        }

        /// <summary>
        /// Place the window on a monitor without wrecking the player's settings.
        ///
        /// The first version of this got both halves wrong, and the symptom was
        /// a game that came back cropped and at the wrong resolution:
        ///
        /// 1. It forced the resolution to the target monitor's NATIVE size on
        ///    every move. Unity persists resolution, so one press of the cycle
        ///    key silently and permanently overrode whatever the player had
        ///    chosen in the game's own video settings — and it stayed overridden
        ///    on later launches, where the mod looked innocent because it hadn't
        ///    moved anything.
        /// 2. It then placed that full-monitor-sized window 40 px in from the
        ///    corner, so 40 px of it hung off the right and bottom edges. That
        ///    is not a subtle bug: it is literally cropping the game.
        ///
        /// The rule now: moving a window between monitors is a move. It changes
        /// the resolution only when the current one genuinely cannot fit, and it
        /// never pushes the window off the screen it was asked to move to.
        /// </summary>
        public static Placement Place(int currentWidth, int currentHeight,
                                      int workX, int workY, int workWidth, int workHeight,
                                      int monitorWidth, int monitorHeight,
                                      bool fullscreen)
        {
            var p = new Placement();

            // Guard against a monitor the OS reports as nonsense rather than
            // propagating a zero into Screen.SetResolution.
            if (monitorWidth <= 0) monitorWidth = Math.Max(1, currentWidth);
            if (monitorHeight <= 0) monitorHeight = Math.Max(1, currentHeight);
            if (workWidth <= 0) workWidth = monitorWidth;
            if (workHeight <= 0) workHeight = monitorHeight;

            if (fullscreen)
            {
                // Fullscreen at anything other than the monitor's native size is
                // the scaled, slightly-soft, sometimes-letterboxed picture people
                // describe as "not my resolution". Here matching IS the right
                // thing — and the window fills the monitor, so there is no offset.
                p.X = workX;
                p.Y = workY;
                p.Width = monitorWidth;
                p.Height = monitorHeight;
                p.ChangesResolution = currentWidth != monitorWidth || currentHeight != monitorHeight;
                p.Why = p.ChangesResolution
                    ? "Fullscreen, so the resolution now matches the monitor (" + monitorWidth + "x" + monitorHeight + ")."
                    : "Moved. Already at the monitor's own resolution.";
                return p;
            }

            // Windowed: keep what the player chose. Shrink only if it genuinely
            // will not fit, and say so when we do.
            p.Width = currentWidth > 0 ? Math.Min(currentWidth, workWidth) : workWidth;
            p.Height = currentHeight > 0 ? Math.Min(currentHeight, workHeight) : workHeight;
            p.ChangesResolution = p.Width != currentWidth || p.Height != currentHeight;

            // Centred in the work area, never negative, never hanging off the
            // edge. A window the same size as the screen lands exactly on it.
            p.X = workX + Math.Max(0, (workWidth - p.Width) / 2);
            p.Y = workY + Math.Max(0, (workHeight - p.Height) / 2);

            p.Why = p.ChangesResolution
                ? "Moved. " + currentWidth + "x" + currentHeight + " doesn't fit, so it's now "
                  + p.Width + "x" + p.Height + "."
                : "Moved, and your resolution is untouched (" + p.Width + "x" + p.Height + ").";
            return p;
        }

        /// <summary>Next monitor along, wrapping. The blind escape hatch behind the hotkey.</summary>
        public static int Next(int count, int current)
        {
            if (count <= 1) return -1;
            if (current < 0) return 0;
            return (current + 1) % count;
        }

        /// <summary>Which entry in the layout is the window on? -1 if it can't be told.</summary>
        public static int IndexOf(IList<DisplaySpec> displays, string name, int width, int height)
        {
            if (displays == null) return -1;

            for (int i = 0; i < displays.Count; i++)
                if (string.Equals(displays[i].Name, name, StringComparison.Ordinal)
                    && displays[i].Width == width && displays[i].Height == height)
                    return i;

            // Same name, different reported size — still almost certainly it.
            for (int i = 0; i < displays.Count; i++)
                if (string.Equals(displays[i].Name, name, StringComparison.Ordinal))
                    return i;

            return -1;
        }

        private static DisplayPlan Plan(int target, string why)
        {
            return new DisplayPlan { Target = target, Why = why };
        }
    }
}
