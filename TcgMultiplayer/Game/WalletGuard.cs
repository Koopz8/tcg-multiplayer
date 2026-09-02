using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;

namespace TcgMultiplayer.Game
{
    /// <summary>
    /// Keeps each player's economy their own.
    ///
    /// Wallets are per-player by default — every client runs its own copy of the
    /// game with its own PlayMaker globals, so nothing needs synchronising. What
    /// breaks that is M3's spectating: a spectator replays the machine owner's FSM
    /// events, and the payout is one of those events. Left alone, everybody
    /// watching a Skee Ball win gets paid for it.
    ///
    /// So rather than trying to blacklist every event that might touch money —
    /// and inevitably missing one — we snapshot the economy globals immediately
    /// before applying a mirrored event and restore them immediately after. It
    /// costs a few dozen field reads per event (machine events are bursty, a few
    /// dozen per round) and it closes every payout path at once, including the
    /// ones nobody has found yet.
    ///
    /// The dump of all 658 PlayMaker globals is what made the prefix list below
    /// possible: the economy partitions cleanly by name.
    /// </summary>
    internal sealed class WalletGuard
    {
        /// <summary>
        /// Globals a spectator must never have changed by someone else's round.
        /// Prefix match, case-insensitive. Configurable, because the list is a
        /// judgement call and a game patch can add to it.
        /// </summary>
        public const string DefaultProtectedPrefixes =
            "COINS ,TICKETS ,CHIPS ,GAMECARDS ,PRIZE CREDITS ,XP Points,BUSPASS ,BUSSPASS ," +
            "RIDEPASS ,LOTTO_,PLAYCOUNT_,TICKETCOUNT_,INVENTORYSPOT_,ARRAY_INVENTORY," +
            "SANDCASTLE COINS WON,HEALTH Balance,ENERGY Balance,BATTERY POWER," +
            "GARY FOOD,FISH FOOD,CUCKOO Game Credits,DUNKO Game Credits," +
            "MERMAIDS Game Credits,Lava Mayhem Game Credits,PRIZES Purchased,PRIZES Sold," +
            "DRINK purchased,FOOD Purchased,FIREWORKS Purchased,FUEL Purchased";

        private readonly List<NamedVariable> _protected = new List<NamedVariable>();
        private readonly List<object> _snapshot = new List<object>();
        private string[] _prefixes;
        private bool _resolved;

        public int ProtectedCount { get { return _protected.Count; } }
        public int RestoreCount { get; private set; }
        public bool Available { get { return _resolved && _protected.Count > 0; } }

        public void Configure(string prefixCsv)
        {
            var raw = string.IsNullOrEmpty(prefixCsv) ? DefaultProtectedPrefixes : prefixCsv;
            var parts = raw.Split(',');
            var list = new List<string>(parts.Length);
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.Length > 0) list.Add(t);
            }
            _prefixes = list.ToArray();
            _resolved = false;
            _protected.Clear();
        }

        /// <summary>
        /// Binds to the live global variables. Called lazily — PlayMaker's globals
        /// are not populated until the game has loaded a save.
        /// </summary>
        public bool Resolve()
        {
            if (_resolved) return _protected.Count > 0;
            if (_prefixes == null) Configure(null);

            try
            {
                var globals = FsmVariables.GlobalVariables;
                if (globals == null) return false;

                var all = globals.GetAllNamedVariables();
                if (all == null || all.Length == 0) return false;

                _protected.Clear();
                foreach (var v in all)
                {
                    if (v == null || string.IsNullOrEmpty(v.Name)) continue;
                    if (!Matches(v.Name)) continue;

                    // Only value types round-trip safely through RawValue. Arrays and
                    // object references are left alone rather than risking a bad write.
                    switch (v.VariableType)
                    {
                        case VariableType.Int:
                        case VariableType.Float:
                        case VariableType.Bool:
                        case VariableType.String:
                            _protected.Add(v);
                            break;
                    }
                }

                _resolved = true;
                Plugin.Log("Wallet guard: protecting " + _protected.Count + " economy globals of " + all.Length + ".");
                return _protected.Count > 0;
            }
            catch (Exception ex)
            {
                Plugin.Warn("Wallet guard could not read globals: " + ex.Message);
                return false;
            }
        }

        private bool Matches(string name)
        {
            for (int i = 0; i < _prefixes.Length; i++)
                if (name.StartsWith(_prefixes[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // --------------------------------------------------------- snapshot pair

        public void Snapshot()
        {
            if (!Resolve()) return;
            _snapshot.Clear();
            for (int i = 0; i < _protected.Count; i++)
            {
                try { _snapshot.Add(_protected[i].RawValue); }
                catch { _snapshot.Add(null); }
            }
        }

        /// <summary>Puts back anything the replayed event moved. Returns how many it had to.</summary>
        public int Restore()
        {
            if (_snapshot.Count != _protected.Count) return 0;

            int changed = 0;
            for (int i = 0; i < _protected.Count; i++)
            {
                var saved = _snapshot[i];
                if (saved == null) continue;
                try
                {
                    var now = _protected[i].RawValue;
                    if (Equals(now, saved)) continue;
                    _protected[i].RawValue = saved;
                    changed++;
                }
                catch { }
            }
            RestoreCount += changed;
            return changed;
        }

        // ------------------------------------------------------------ scoreboard

        public int ReadInt(string globalName, int fallback = 0)
        {
            if (!Resolve()) return fallback;
            try
            {
                var v = FsmVariables.GlobalVariables.GetFsmInt(globalName);
                return v != null ? v.Value : fallback;
            }
            catch { return fallback; }
        }

        public const string GlobalCoins = "COINS Balance";
        public const string GlobalTickets = "TICKETS Balance";
        public const string GlobalTicketsSession = "TICKETS Won CurrentSession";

        public int Coins { get { return ReadInt(GlobalCoins); } }
        public int Tickets { get { return ReadInt(GlobalTickets); } }
        public int TicketsThisSession { get { return ReadInt(GlobalTicketsSession); } }
    }
}
