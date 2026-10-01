using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace TcgMultiplayer.Game
{
    /// <summary>
    /// One row per machine you've watched somebody else play, so a single
    /// walk round the arcade says which cabinets spectate properly and which
    /// don't.
    ///
    /// Spectating was proved on a coin pusher and a claw machine, which is
    /// two of the thirty-odd interactables the registry finds. The rest have
    /// never been watched by anyone. Checking them one at a time by eye means
    /// remembering what thirty cabinets looked like; this writes it down
    /// instead, and lands in the session report that bug reports already ask
    /// for.
    ///
    /// The verdict column is deliberately blunt. "ok" means parts matched and
    /// something moved; anything else names what was missing, which is the
    /// first question to ask of a cabinet that looked dead.
    /// </summary>
    internal sealed class WatchReport
    {
        private sealed class Row
        {
            public string Label;
            public int LocalBodies, TheirBodies, Matched;
            public int MotionPeak, Packets;
            public int ScreenFields, ScreenApplied;
            public int Muted, LetThrough;
            public float LastAt;
        }

        private readonly Dictionary<uint, Row> _rows = new Dictionary<uint, Row>();

        public int Count { get { return _rows.Count; } }

        /// <summary>
        /// Cleared when a session starts. The director lives for the whole game
        /// launch, so without this a second session's report would still carry
        /// the first one's rows — and a cabinet that spectated fine an hour ago
        /// reading "ok" is worse than no row at all.
        /// </summary>
        public void Reset() { _rows.Clear(); }

        private Row Get(uint id, string label)
        {
            Row r;
            if (!_rows.TryGetValue(id, out r))
            {
                r = new Row { Label = label };
                _rows[id] = r;
            }
            if (!string.IsNullOrEmpty(label)) r.Label = label;
            r.LastAt = Time.time;
            return r;
        }

        public void Bodies(uint id, string label, int local, int theirs, int matched)
        {
            var r = Get(id, label);
            r.LocalBodies = local;
            r.TheirBodies = theirs;
            r.Matched = matched;
        }

        public void Packet(uint id, int changed)
        {
            var r = Get(id, null);
            r.Packets++;
            if (changed > r.MotionPeak) r.MotionPeak = changed;
        }

        public void Screen(uint id, int fields, int applied)
        {
            var r = Get(id, null);
            if (fields > r.ScreenFields) r.ScreenFields = fields;
            if (applied > r.ScreenApplied) r.ScreenApplied = applied;
        }

        public void Event(uint id, bool letThrough)
        {
            var r = Get(id, null);
            if (letThrough) r.LetThrough++; else r.Muted++;
        }

        private static string Verdict(Row r)
        {
            if (r.Packets == 0) return "never streamed";
            if (r.TheirBodies == 0) return "no moving parts (events only)";
            if (r.Matched == 0) return "NOTHING MATCHED - nothing of theirs lines up with ours";
            if (r.MotionPeak == 0) return "NOTHING MOVED - parts matched but none of them ever changed";
            if (r.ScreenFields == 0) return "ok, but no screen text found";
            if (r.ScreenApplied == 0) return "ok, but none of the screen text could be written here";
            return "ok";
        }

        public string Text()
        {
            if (_rows.Count == 0) return null;
            var sb = new StringBuilder();
            foreach (var kv in _rows)
            {
                var r = kv.Value;
                sb.Append("   ").Append((r.Label ?? "?").PadRight(26));
                sb.Append(r.Matched).Append('/').Append(r.TheirBodies).Append(" parts matched");
                sb.Append(" (").Append(r.LocalBodies).Append(" here)");
                sb.Append(", motion peak ").Append(r.MotionPeak);
                sb.Append(", screen ").Append(r.ScreenApplied).Append('/').Append(r.ScreenFields);
                if (r.LetThrough > 0) sb.Append(", ").Append(r.LetThrough).Append(" events let through");
                sb.Append("\n      ").Append(Verdict(r)).Append('\n');
            }
            return sb.ToString();
        }
    }
}
