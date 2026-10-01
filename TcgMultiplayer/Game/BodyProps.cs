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
    ///
    /// WHICH WAS WRONG, and switched off here until it can be done by name.
    /// "Walk both the same way and the nth object here is the nth object there"
    /// was the entire premise and it only holds when both sides are looking at
    /// the same hierarchy. They are not. The owner packs from the LIVE mesh in
    /// their scene; the watcher's body is a clone of a loaded ASSET, because
    /// two people usually pick different characters —
    ///
    ///   Local guest (window 2) is playing as MANDY Mesh.
    ///   Character "MANDY Mesh": cloning MANDY Mesh (loaded asset).
    ///
    /// A live mesh picks up children at runtime that the asset has never had,
    /// and one extra child at any level shifts every bit after it. From there
    /// the mask is switching whatever happens to be at that index, and most of
    /// what is under a character is bones. Switch a bone's object off and it
    /// stops animating, its children keep whatever transform they had, and the
    /// skinned mesh draws against a frozen bone: eyes that float away from the
    /// head and tilt with every step.
    ///
    /// "29 parts switched over 26 updates" looked like one prop per update. It
    /// was 29 wrong nodes. A count being small is not the same as it being
    /// right, and I should have sent something that could prove the two bodies
    /// matched instead of assuming it.
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

        /// <summary>
        /// Off until the mirroring is done by name with a check that the two
        /// bodies agree. Nothing is sent and nothing is applied while this is
        /// false, so a body is left exactly as its source had it.
        /// </summary>
        public const bool Mirror = false;

        /// <summary>
        /// Said once, the first time a body is built: what the live mesh here has
        /// that the clone does not, and what each side thinks is switched on.
        /// This is the measurement that should have come before the fix — it
        /// names the objects involved instead of leaving me to guess which of
        /// them is a ticket pile.
        /// </summary>
        public static void Compare(Transform live, Transform clone)
        {
            if (live == null || clone == null) return;

            var a = new List<Transform>(256); Walk(live, a);
            var b = new List<Transform>(256); Walk(clone, b);

            int diverge = -1;
            for (int i = 0; i < a.Count && i < b.Count; i++)
                if (a[i] == null || b[i] == null || a[i].name != b[i].name) { diverge = i; break; }

            Plugin.Log("Body walk: " + a.Count + " objects under our own " + live.name
                       + ", " + b.Count + " under the clone of " + clone.name
                       + (diverge < 0
                          ? (a.Count == b.Count ? " — same names all the way down." : " — same as far as the shorter one goes.")
                          : " — they stop matching at " + diverge + " (\"" + (a[diverge] != null ? a[diverge].name : "?")
                            + "\" here, \"" + (b[diverge] != null ? b[diverge].name : "?") + "\" there)."));

            Describe("ours", a);
            Describe("theirs", b);
        }

        private static void Describe(string side, List<Transform> all)
        {
            var sb = new System.Text.StringBuilder();
            int said = 0;
            for (int i = 0; i < all.Count && said < 30; i++)
            {
                var t = all[i];
                if (t == null) continue;
                var r = t.GetComponent<Renderer>();
                if (r == null) continue;
                said++;
                sb.Append(i).Append(':').Append(t.name)
                  .Append(t.gameObject.activeSelf ? "" : " (object off)")
                  .Append(r.enabled ? "" : " (renderer off)")
                  .Append("  ");
            }
            Plugin.Log("Things you can see, " + side + ": " + (said == 0 ? "none" : sb.ToString()));
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
