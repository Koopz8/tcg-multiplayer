using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace TcgMultiplayer.Game
{
    /// <summary>
    /// Which things on a player's body are showing, mirrored to everyone else.
    ///
    /// The first attempt sent one bit per object in walk order, on the grounds
    /// that a clone of a mesh has the same hierarchy as the mesh. It does not,
    /// because the clone is built from the loaded ASSET and the mask is packed
    /// from the LIVE mesh in the owner's scene. The measurement that should
    /// have come first says how wrong that was:
    ///
    ///   Body walk: 145 objects under our own LARRY Mesh, 130 under the clone
    ///   of the guest's — they stop matching at 2 ("L_WatchMenu LSTNR" here,
    ///   "Pigtails_1" there).
    ///
    /// Index two. Everything after it was switching whatever happened to sit at
    /// that number, which on a character is mostly bones, which is why eyes
    /// ended up sitting in the grass while the body walked off.
    ///
    /// So this goes by NAME, and only for names that exist exactly once on both
    /// bodies. A name the other side doesn't have is never touched, which is
    /// what keeps Mandy's pigtails on Mandy and Larry's sunglasses on Larry; a
    /// name that appears twice is skipped rather than guessed at. Nothing can
    /// be switched that both sides didn't already agree exists.
    ///
    /// The same measurement named the things in question. A character carries a
    /// drawer of them — WOODBAT, CHEESYPOOFS, LEMONADE, JERRYS JERKY, LASER TAG
    /// PHASER, TV Remote, a flashlight — nearly all switched off at any moment,
    /// and the asset ships with some of them switched ON. That is the phantom
    /// tickets: not something the player picked up, just a prop that was on by
    /// default in the asset and switched off at runtime in the real game, so
    /// the clone kept showing it. Telling the clone what the owner's copy
    /// actually has switched on settles it, and settles every other item in
    /// that drawer at the same time without naming any of them here.
    /// </summary>
    internal static class BodyProps
    {
        public const int MaxNodes = 4096;
        public const int MaxNames = 250;

        public static int Switched, Applied, Skipped;

        /// <summary>
        /// Every object with something to draw, by name — minus any name that
        /// turns up more than once, because then it doesn't identify anything.
        /// Both ends build this the same way from their own body.
        /// </summary>
        private static void Collect(Transform root, Dictionary<string, Transform> into)
        {
            into.Clear();
            if (root == null) return;
            var stack = new List<Transform>(256);
            Walk(root, stack);

            var seenTwice = new HashSet<string>();
            for (int i = 0; i < stack.Count; i++)
            {
                var t = stack[i];
                if (t == null || t.GetComponent<Renderer>() == null) continue;
                var n = t.name;
                if (string.IsNullOrEmpty(n) || n.Length > 200) continue;
                if (seenTwice.Contains(n)) continue;
                if (into.ContainsKey(n)) { into.Remove(n); seenTwice.Add(n); continue; }
                into[n] = t;
            }
        }

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
            var found = new Dictionary<string, Transform>(64);
            Collect(mesh, found);
            if (found.Count == 0) return null;

            var ms = new System.IO.MemoryStream(1024);
            var w = new System.IO.BinaryWriter(ms, Encoding.UTF8);
            int n = found.Count < MaxNames ? found.Count : MaxNames;
            w.Write((byte)n);
            int wrote = 0;
            foreach (var kv in found)
            {
                if (wrote++ >= n) break;
                var name = Encoding.UTF8.GetBytes(kv.Key);
                if (name.Length > 200) { w.Write((byte)0); continue; }
                w.Write((byte)name.Length);
                w.Write(name);
                w.Write((byte)(kv.Value != null && kv.Value.gameObject.activeSelf ? 1 : 0));
            }
            return ms.ToArray();
        }

        /// <summary>
        /// Switches this body's things to match what the owner says theirs are
        /// doing. A name we don't have, or have twice, is left alone — so the
        /// worst this can do is nothing.
        /// </summary>
        public static int Apply(Transform body, byte[] data)
        {
            if (body == null || data == null || data.Length < 1) return 0;

            var want = new Dictionary<string, bool>(64);
            try
            {
                var r = new System.IO.BinaryReader(new System.IO.MemoryStream(data), Encoding.UTF8);
                int n = r.ReadByte();
                for (int i = 0; i < n; i++)
                {
                    int len = r.ReadByte();
                    if (len == 0) continue;
                    var name = Encoding.UTF8.GetString(r.ReadBytes(len));
                    want[name] = r.ReadByte() != 0;
                }
            }
            catch { return 0; }

            var mine = new Dictionary<string, Transform>(64);
            Collect(body, mine);

            Applied++;
            int changed = 0;
            foreach (var kv in mine)
            {
                bool on;
                if (!want.TryGetValue(kv.Key, out on)) { Skipped++; continue; }
                var t = kv.Value;
                if (t == null || t.gameObject.activeSelf == on) continue;
                t.gameObject.SetActive(on);
                changed++;
            }
            Switched += changed;
            return changed;
        }

        /// <summary>
        /// Said once per session, the first time a body is built. This is what
        /// turned the bitmask from a plausible idea into a measured mistake, so
        /// it stays in.
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
                       + ", " + b.Count + " under the clone"
                       + (diverge < 0
                          ? " — same names as far as the shorter one goes."
                          : " — they stop matching at " + diverge + " (\"" + (a[diverge] != null ? a[diverge].name : "?")
                            + "\" here, \"" + (b[diverge] != null ? b[diverge].name : "?") + "\" there)."));

            var mineByName = new Dictionary<string, Transform>(64); Collect(live, mineByName);
            var theirsByName = new Dictionary<string, Transform>(64); Collect(clone, theirsByName);
            int shared = 0;
            foreach (var kv in mineByName) if (theirsByName.ContainsKey(kv.Key)) shared++;
            Plugin.Log("Named things we can both place: " + shared + " of " + mineByName.Count
                       + " here and " + theirsByName.Count + " there. Only those get mirrored.");

            Describe("ours", a);
            Describe("theirs", b);
        }

        private static void Describe(string side, List<Transform> all)
        {
            var sb = new StringBuilder();
            int said = 0;
            for (int i = 0; i < all.Count && said < 80; i++)
            {
                var t = all[i];
                if (t == null) continue;
                var r = t.GetComponent<Renderer>();
                if (r == null) continue;
                said++;
                sb.Append(t.name)
                  .Append(t.gameObject.activeSelf ? "" : " (off)")
                  .Append(r.enabled ? "" : " (renderer off)")
                  .Append("  ");
            }
            Plugin.Log("Things you can see, " + side + ": " + (said == 0 ? "none" : sb.ToString()));
        }
    }
}
