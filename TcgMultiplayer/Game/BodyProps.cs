using System.Collections.Generic;
using UnityEngine;

namespace TcgMultiplayer.Game
{
    /// <summary>
    /// Which parts of a player's body are switched on, as one bit each.
    ///
    /// Both characters turned up holding tickets neither of them had. The
    /// reason is that the pile in a player's hand is not a loose object that
    /// gets picked up — the floor pile is DESTROYED on pickup, and what appears
    /// in the hand is a prop that was always parented to the character and
    /// simply gets switched on. A remote body is a clone of that character
    /// mesh, so it was cloned with whatever props happened to be hanging off it,
    /// visible, forever.
    ///
    /// The fix needs no knowledge of which objects those are. The watcher's
    /// body is a clone of the same mesh, so it has the same hierarchy in the
    /// same order; walk both the same way and the nth object here is the nth
    /// object there. So the owner sends a bit per object and the watcher
    /// applies it. No names, no list to keep up to date when the game adds a
    /// prize, and it covers everything a character can be shown holding rather
    /// than only the ones we thought to look for.
    ///
    /// Bones are always on and cost a bit each, which is the price of not
    /// having to know anything: a whole body is about thirty bytes, sent only
    /// when something changes.
    /// </summary>
    internal static class BodyProps
    {
        /// <summary>Beyond this a mesh is not a character and we leave it alone.</summary>
        public const int MaxNodes = 2048;

        private static readonly List<Transform> Scratch = new List<Transform>(256);

        /// <summary>
        /// How many parts of other people's bodies we've switched this session.
        /// A handful per body is right — the props. Thousands would mean the two
        /// walks have drifted apart and we're switching bones on and off.
        /// </summary>
        public static int Switched;
        public static int Applied;

        /// <summary>
        /// Depth-first, children in order, the root itself excluded. Both ends
        /// must walk identically — this is the only copy of the walk, which is
        /// the point.
        /// </summary>
        private static void Walk(Transform t, List<Transform> into)
        {
            for (int i = 0; i < t.childCount; i++)
            {
                if (into.Count >= MaxNodes) return;
                var c = t.GetChild(i);
                into.Add(c);
                Walk(c, into);
            }
        }

        public static byte[] Pack(Transform mesh)
        {
            if (mesh == null) return null;
            Scratch.Clear();
            Walk(mesh, Scratch);
            if (Scratch.Count == 0) return null;

            var mask = new byte[(Scratch.Count + 7) / 8];
            for (int i = 0; i < Scratch.Count; i++)
                if (Scratch[i] != null && Scratch[i].gameObject.activeSelf)
                    mask[i >> 3] |= (byte)(1 << (i & 7));
            return mask;
        }

        /// <summary>
        /// Applies a mask to a body. Returns how many objects it switched, which
        /// is the number worth watching: it should be a handful on the first
        /// mask and near zero afterwards.
        /// </summary>
        public static int Apply(Transform body, byte[] mask)
        {
            if (body == null || mask == null) return 0;
            Scratch.Clear();
            Walk(body, Scratch);

            Applied++;
            int changed = 0;
            int bits = mask.Length * 8;
            for (int i = 0; i < Scratch.Count && i < bits; i++)
            {
                var t = Scratch[i];
                if (t == null) continue;
                bool on = (mask[i >> 3] & (1 << (i & 7))) != 0;
                if (t.gameObject.activeSelf == on) continue;
                t.gameObject.SetActive(on);
                changed++;
            }
            Switched += changed;
            return changed;
        }
    }
}
