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
