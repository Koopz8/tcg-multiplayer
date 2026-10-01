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
        private static int _told;

        /// <summary>
        /// Every object that draws something, or contains something that draws,
        /// by name — minus any name that turns up more than once, because then
        /// it doesn't identify anything. Both ends build this the same way from
        /// their own body.
        ///
        /// It used to require a Renderer on the object itself. That missed a
        /// prop whose own object is the thing switched on and off while the
        /// drawing happens on a child called something generic like "MESH" —
        /// and a name like MESH appears many times, so it was being skipped as
        /// ambiguous. Hence "0 things switched" while the tickets were plainly
        /// still there.
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
                if (t == null) continue;
                if (t.GetComponentInChildren<Renderer>(true) == null) continue;
                var n = Key(t, root);
                if (string.IsNullOrEmpty(n) || n.Length > 200) continue;
                if (seenTwice.Contains(n)) continue;
                if (into.ContainsKey(n)) { into.Remove(n); seenTwice.Add(n); continue; }
                into[n] = t;
            }
        }

        /// <summary>
        /// A part's name with its parent's in front of it. The bare name was not
        /// enough: the tickets live in a group called "UnCountedTickets Group"
        /// whose own switch we could see, but the piles inside it draw from
        /// children called things like MESH — a name that appears all over a
        /// character, so it was thrown out as ambiguous and never mirrored. The
        /// group would come on over there with nothing inside it to show.
        ///
        /// Not the full path, which would be both long to send and fragile: the
        /// live body has objects the asset never had, so paths further up differ
        /// between the two. One step is enough to tell MESH from MESH and short
        /// enough to send a hundred of them.
        /// </summary>
        /// <summary>
        /// Bit 0: the object is switched on. Bit 1: its renderer is drawing.
        /// Bit 2: it has no renderer of its own, so say nothing about drawing.
        /// Switching the object off is not the only way the game hides what's in
        /// your hand — it also just turns the renderer off, and a clone from the
        /// asset arrives with it on.
        /// </summary>
        private static byte StateOf(Transform t)
        {
            byte flags = 0;
            if (t == null) return 4;
            if (t.gameObject.activeSelf) flags |= 1;
            var rend = t.GetComponent<Renderer>();
            if (rend == null) flags |= 4;
            else if (rend.enabled) flags |= 2;
            return flags;
        }

        private static readonly Dictionary<string, byte> _lastPacked = new Dictionary<string, byte>(128);
        private static int _toldPack;

        private static string Key(Transform t, Transform root)
        {
            var p = t.parent;
            if (p == null || p == root) return t.name;
            return p.name + "/" + t.name;
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

            // What changed on OUR body since last time, said out loud. Without
            // this there is no way to tell "the owner never noticed the tickets
            // appear" from "the owner said so and the watcher ignored it", and
            // guessing between those two has already cost two builds.
            if (_toldPack < 20)
            {
                var flipped = new StringBuilder();
                foreach (var kv in found)
                {
                    byte now = StateOf(kv.Value);
                    byte was;
                    if (_lastPacked.TryGetValue(kv.Key, out was) && was == now) continue;
                    if (_lastPacked.Count > 0)
                        flipped.Append(kv.Key).Append((now & 1) != 0 ? " on" : " off")
                               .Append((now & 4) == 0 ? ((now & 2) != 0 ? "/drawn" : "/hidden") : "").Append("  ");
                    _lastPacked[kv.Key] = now;
                }
                if (flipped.Length > 0)
                {
                    _toldPack++;
                    Plugin.Log("On our own body: " + flipped);
                }
            }

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
                byte flags = StateOf(kv.Value);
                w.Write(flags);
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

            var want = new Dictionary<string, byte>(64);
            try
            {
                var r = new System.IO.BinaryReader(new System.IO.MemoryStream(data), Encoding.UTF8);
                int n = r.ReadByte();
                for (int i = 0; i < n; i++)
                {
                    int len = r.ReadByte();
                    if (len == 0) continue;
                    var name = Encoding.UTF8.GetString(r.ReadBytes(len));
                    want[name] = r.ReadByte();
                }
            }
            catch { return 0; }

            var mine = new Dictionary<string, Transform>(64);
            Collect(body, mine);

            Applied++;
            int changed = 0;
            StringBuilder said = _told < 20 ? new StringBuilder() : null;
            foreach (var kv in mine)
            {
                byte flags;
                if (!want.TryGetValue(kv.Key, out flags)) { Skipped++; continue; }
                var t = kv.Value;
                if (t == null) continue;

                bool wantOn = (flags & 1) != 0;
                if (t.gameObject.activeSelf != wantOn)
                {
                    if (said != null) said.Append(kv.Key).Append(wantOn ? " on" : " off").Append("  ");
                    t.gameObject.SetActive(wantOn);
                    changed++;
                }

                if ((flags & 4) == 0)
                {
                    var rend = t.GetComponent<Renderer>();
                    bool wantDrawn = (flags & 2) != 0;
                    if (rend != null && rend.enabled != wantDrawn)
                    {
                        if (said != null) said.Append(kv.Key).Append(wantDrawn ? " drawn" : " hidden").Append("  ");
                        rend.enabled = wantDrawn;
                        changed++;
                    }
                }
            }
            if (said != null && changed > 0)
            {
                _told++;
                Plugin.Log("Put right on their body: " + said);
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

            Describe("ours", mineByName);
            Describe("theirs", theirsByName);
        }

        /// <summary>
        /// The exact set the mirroring works from, with what each one is doing.
        /// Printed in full rather than the first eighty, because the first
        /// eighty twice failed to reach the thing we were looking for.
        /// </summary>
        private static void Describe(string side, Dictionary<string, Transform> named)
        {
            var sb = new StringBuilder();
            foreach (var kv in named)
            {
                var t = kv.Value;
                if (t == null) continue;
                sb.Append(kv.Key);
                if (!t.gameObject.activeSelf) sb.Append("(off)");
                var r = t.GetComponent<Renderer>();
                if (r != null && !r.enabled) sb.Append("(hidden)");
                sb.Append("  ");
            }
            Plugin.Log("Body parts we can name, " + side + " (" + named.Count + "): " + sb);
        }

        public static void Forget() { _told = 0; _toldPack = 0; _lastPacked.Clear(); }
    }
}
