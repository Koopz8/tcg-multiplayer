using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using Steamworks;
using TcgMultiplayer.Net;
using UnityEngine;

namespace TcgMultiplayer.Game
{
    /// <summary>
    /// The island's shared progression.
    ///
    /// Wallets are per-player (see WalletGuard) — but the world is not. If a guest
    /// unlocks Larry's Arcade or buys the van, that has to happen on everyone's
    /// island, or the group immediately desyncs into four private versions of the
    /// same place. So the economy globals split cleanly in two:
    ///
    ///   player-owned  -> never crosses the wire, and is defended from spectating
    ///   world-owned   -> host-authoritative, replicated to everyone
    ///
    /// Anyone may change world state; the change is routed through the host, who
    /// applies it and broadcasts the result. A joiner is sent the whole set on
    /// arrival, so they walk into the host's island rather than their own.
    ///
    /// The names below came out of the 658-global dump; they are the ones that
    /// describe the place rather than the person.
    ///
    /// VISITING
    /// --------
    /// Walking into someone else's island used to be a one-way door: the host's
    /// unlocks were written straight into the guest's globals, the game saved at
    /// some point, and the guest went home owning things they never bought. For
    /// a mod anyone can download that is the worst kind of bug — it costs the
    /// player their own progression and there is no undo.
    ///
    /// So a guest's stay is now explicitly a visit. Their own values are taken
    /// aside on arrival, the host's island is laid over the top for the duration,
    /// and on the way out their own progression is put back — with anything they
    /// personally unlocked while they were there re-applied on top, because they
    /// did earn that. The host is unaffected; it is their island either way.
    ///
    /// SaveGuard covers what this cannot: a crash mid-visit never reaches the
    /// restore, so there is a copy of the save from before anyone connected.
    /// </summary>
    internal sealed class WorldState
    {
        /// <summary>
        /// Prefix match, case-insensitive. Deliberately narrow: anything not
        /// listed stays private to each player, which is the safer default.
        /// </summary>
        public const string DefaultWorldPrefixes =
            "SURVIVOR_,LOOT BOX Unlocked,GOLFCART Purchased,CAR 1 Purchased,CAR 2 Purchased," +
            "VAN Purchased,IsVanPurchased,LAMBO IsPurchased,SUPERKART IsPurchased";

        private readonly Session _session;
        private readonly List<NamedVariable> _vars = new List<NamedVariable>();
        private readonly Dictionary<string, NamedVariable> _byName =
            new Dictionary<string, NamedVariable>(StringComparer.Ordinal);
        private readonly Dictionary<string, object> _last = new Dictionary<string, object>(StringComparer.Ordinal);

        private string[] _prefixes;
        private bool _resolved;
        private bool _applying;          // suppresses echo while we write a remote value
        private float _nextPollAt;

        // --- visiting someone else's island -------------------------------
        private readonly Dictionary<string, object> _ownWorld =
            new Dictionary<string, object>(StringComparer.Ordinal);
        private readonly HashSet<string> _earnedWhileVisiting =
            new HashSet<string>(StringComparer.Ordinal);
        private bool _visiting;
        private bool _visitPending;      // joined, but the save wasn't loaded yet

        /// <summary>Guests keep their own progression across a visit. Off means the old behaviour.</summary>
        public bool ProtectGuestProgression = true;

        public bool Visiting { get { return _visiting; } }
        public bool VisitPending { get { return _visitPending; } }
        public int EarnedWhileVisiting { get { return _earnedWhileVisiting.Count; } }
        public int StashedCount { get { return _ownWorld.Count; } }

        public int TrackedCount { get { return _vars.Count; } }
        public int ChangesSent, ChangesApplied;
        public bool Available { get { return _resolved && _vars.Count > 0; } }

        public WorldState(Session session)
        {
            _session = session;
            _session.OnWorldVar += OnRemoteChange;
            _session.OnWorldSnapshotRequest += OnSnapshotRequest;
        }

        public void Configure(string prefixCsv)
        {
            var raw = string.IsNullOrEmpty(prefixCsv) ? DefaultWorldPrefixes : prefixCsv;
            var list = new List<string>();
            foreach (var p in raw.Split(','))
            {
                var t = p.Trim();
                if (t.Length > 0) list.Add(t);
            }
            _prefixes = list.ToArray();
            _resolved = false;
            _vars.Clear();
            _byName.Clear();
            _last.Clear();
        }

        public bool Resolve()
        {
            if (_resolved) return _vars.Count > 0;
            if (_prefixes == null) Configure(null);

            try
            {
                var globals = FsmVariables.GlobalVariables;
                if (globals == null) return false;
                var all = globals.GetAllNamedVariables();
                if (all == null || all.Length == 0) return false;

                _vars.Clear(); _byName.Clear(); _last.Clear();
                foreach (var v in all)
                {
                    if (v == null || string.IsNullOrEmpty(v.Name)) continue;
                    if (!Matches(v.Name)) continue;
                    if (v.VariableType != VariableType.Int &&
                        v.VariableType != VariableType.Bool &&
                        v.VariableType != VariableType.Float) continue;

                    _vars.Add(v);
                    _byName[v.Name] = v;
                    try { _last[v.Name] = v.RawValue; } catch { }
                }

                _resolved = true;
                Plugin.Log("World state: tracking " + _vars.Count + " shared progression globals.");
                return _vars.Count > 0;
            }
            catch (Exception ex)
            {
                Plugin.Warn("World state could not read globals: " + ex.Message);
                return false;
            }
        }

        private bool Matches(string name)
        {
            for (int i = 0; i < _prefixes.Length; i++)
                if (name.StartsWith(_prefixes[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ----------------------------------------------------------------- pump

        public void Tick()
        {
            if (_session.State != SessionState.InLobby) return;
            if (Time.time < _nextPollAt) return;
            _nextPollAt = Time.time + 0.5f;
            if (!Resolve()) return;

            // Save just finished loading? This is where a pending visit is taken.
            if (_visitPending && !TryStash()) return;

            ScanLocalChanges(true);
        }

        /// <summary>
        /// Finds globals that changed on this machine since the last look. Split
        /// out of Tick so the self-test can drive the real earn-detection path
        /// rather than a lookalike written for the test.
        /// </summary>
        private void ScanLocalChanges(bool send)
        {
            // Polling rather than hooking: unlocks are set from PlayMaker actions
            // scattered across the game, and half a second of latency on "the
            // arcade is now unlocked" is imperceptible.
            for (int i = 0; i < _vars.Count; i++)
            {
                var v = _vars[i];
                object now;
                try { now = v.RawValue; } catch { continue; }

                object was;
                if (!_last.TryGetValue(v.Name, out was))
                {
                    // No baseline — a RawValue read failed back in Resolve. Seed
                    // it rather than reporting a change: while visiting, a false
                    // change is recorded as something the guest earned, and they
                    // would take one of the host's unlocks home with them.
                    _last[v.Name] = now;
                    continue;
                }
                if (Equals(was, now)) continue;
                _last[v.Name] = now;

                if (_applying) continue;             // it was us, applying a remote value

                // A change that started here, on this machine, is something the
                // player did — so it survives the trip home even though the rest
                // of the host's island does not.
                if (_visiting) _earnedWhileVisiting.Add(v.Name);

                if (!send) continue;
                _session.SendWorldVar(v.Name, now);
                ChangesSent++;
                Plugin.Log("World change: " + v.Name + " = " + now);
            }
        }

        // ---------------------------------------------------------- visiting

        /// <summary>
        /// Called when this player joins a lobby they don't own. Takes their own
        /// progression aside before the host's snapshot lands on top of it.
        /// </summary>
        public void BeginVisit()
        {
            if (_visiting || _visitPending) return;
            if (!ProtectGuestProgression) return;

            // The commonest way to join is accepting an invite from the main
            // menu, or +connect_lobby at launch — both before a save is loaded,
            // when the PlayMaker globals hold nothing worth keeping. Failing here
            // and giving up would silently restore exactly the behaviour this
            // exists to prevent, so the stash is marked pending instead and taken
            // the moment the globals become readable. Until then no host value is
            // allowed to land.
            _visitPending = true;
            if (!TryStash())
                Plugin.Log("Visiting: your save isn't loaded yet, so your progression will be "
                           + "stashed as soon as it is. Nothing from the host is applied before then.");
        }

        /// <summary>
        /// Takes the stash if the globals are readable. Called from BeginVisit,
        /// from the poll, and — critically — before any remote value is applied.
        /// </summary>
        private bool TryStash()
        {
            if (!_visitPending) return _visiting;
            if (!Resolve()) return false;

            _ownWorld.Clear();
            _earnedWhileVisiting.Clear();

            for (int i = 0; i < _vars.Count; i++)
            {
                try { _ownWorld[_vars[i].Name] = _vars[i].RawValue; } catch { }
            }

            _visitPending = false;
            _visiting = true;
            Plugin.Log("Visiting: stashed " + _ownWorld.Count + " of your own progression values. "
                       + "They go back when you leave.");

            // The host's snapshot is sent once, on HelloAck. If our save wasn't
            // loaded then, every value in it was refused — and nothing would ever
            // ask again, leaving us playing our own island while everyone else
            // played the host's, with no symptom until a door disagreed. Ask now.
            if (!_session.IsHost)
            {
                try { _session.RequestWorldSnapshot(); }
                catch (Exception ex) { Plugin.Warn("Could not re-request the island: " + ex.Message); }
            }
            return true;
        }

        /// <summary>
        /// Called on leaving, being disconnected, or quitting. Puts the player's
        /// own island back, keeping anything they unlocked themselves.
        /// </summary>
        public void EndVisit()
        {
            _visitPending = false;
            if (!_visiting) return;
            _visiting = false;

            int restored = 0, kept = 0, unchanged = 0;
            try
            {
                _applying = true;
                foreach (var kv in _ownWorld)
                {
                    // Earned it themselves: leave the session value in place.
                    if (_earnedWhileVisiting.Contains(kv.Key)) { kept++; continue; }

                    NamedVariable v;
                    if (!_byName.TryGetValue(kv.Key, out v) || v == null) continue;
                    try
                    {
                        // Already the value we stashed — two players at a similar
                        // point in the game, or two fresh saves. Nothing to undo,
                        // and counting it matters: without it the check below
                        // would tell a perfectly fine player their save is broken.
                        if (Equals(v.RawValue, kv.Value)) { unchanged++; continue; }
                        v.RawValue = kv.Value;
                        _last[kv.Key] = kv.Value;
                        restored++;
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Plugin.Warn("Restoring your progression hit a snag: " + ex.Message); }
            finally { _applying = false; }

            Plugin.Log("Visit over: put back " + restored + " of your own values, kept " + kept
                       + " you unlocked yourself.");

            // Restoring nothing at all, when we stashed something and kept
            // nothing, means the writes went somewhere that no longer matters —
            // a rebuilt global table, stale variable references. Cheap detector
            // for a failure that would otherwise be completely silent.
            if (restored == 0 && kept == 0 && unchanged == 0 && _ownWorld.Count > 0)
                Plugin.Warn("Your progression was stashed but nothing was restored. If your unlocks "
                            + "look wrong, close the game and restore from a save backup — see the "
                            + SaveGuard.BackupFolderName + " folder.");
            _ownWorld.Clear();
            _earnedWhileVisiting.Clear();
        }

        // ------------------------------------------------------------- receiving

        private void OnRemoteChange(CSteamID from, string name, object value, bool fromHost)
        {
            if (!Resolve()) return;

            // Nothing from the host touches our globals until our own copy is
            // safely aside. If we can't stash yet, we drop the change — the host
            // re-broadcasts world state on request and the poll picks up the rest,
            // so the cost is a moment's staleness rather than someone's save.
            if (_visitPending && !TryStash())
            {
                Plugin.Log("Held off applying " + name + " — your progression isn't stashed yet.");
                return;
            }

            // The host is the single point that decides, exactly as with machine
            // ownership: a guest's change is a request, and only the host's
            // broadcast makes it real for everyone.
            if (_session.IsHost && !fromHost)
            {
                Apply(name, value);
                _session.SendWorldVar(name, value, asHost: true);
                return;
            }

            if (!fromHost) return;      // a peer's request is not ours to act on
            Apply(name, value);
        }

        private void Apply(string name, object value)
        {
            NamedVariable v;
            if (!_byName.TryGetValue(name, out v) || v == null) return;

            try
            {
                _applying = true;
                v.RawValue = Coerce(v, value);
                _last[name] = v.RawValue;
                ChangesApplied++;
            }
            catch (Exception ex) { Plugin.Warn("World apply failed for " + name + ": " + ex.Message); }
            finally { _applying = false; }
        }

        /// <summary>Values arrive as int/float/bool; make sure the type matches the variable.</summary>
        private static object Coerce(NamedVariable v, object value)
        {
            switch (v.VariableType)
            {
                case VariableType.Int: return Convert.ToInt32(value);
                case VariableType.Float: return Convert.ToSingle(value);
                case VariableType.Bool: return Convert.ToBoolean(value);
                default: return value;
            }
        }

        // -------------------------------------------------------------- snapshot

        /// <summary>Host: hand a joiner the whole island so they don't arrive in their own.</summary>
        private void OnSnapshotRequest(CSteamID who)
        {
            if (!_session.IsHost || !Resolve()) return;

            // Steam can promote a guest to lobby owner. That makes them the owner
            // of a chat room, not the owner of an island — the globals they are
            // holding are the old host's, borrowed. Serving those to a joiner
            // would spread someone else's progression to a third player.
            if (_visiting || _visitPending)
            {
                Plugin.Warn("Refused to serve a world snapshot: these aren't our unlocks to hand out.");
                return;
            }

            int sent = 0;
            for (int i = 0; i < _vars.Count; i++)
            {
                object val;
                try { val = _vars[i].RawValue; } catch { continue; }
                _session.SendWorldVar(_vars[i].Name, val, asHost: true, onlyTo: who);
                sent++;
            }
            Plugin.Log("Sent world snapshot (" + sent + " values) to a joiner.");
        }

        // ------------------------------------------------------------ self-test

        /// <summary>
        /// Proves, against this machine's real save, that a visit gives your own
        /// progression back.
        ///
        /// This is the property the whole mod is staked on, and until now the
        /// only way to check it was to find a second person and a second copy of
        /// the game. It runs entirely locally in a few milliseconds: stash, fake
        /// a host pushing every value the other way, unlock one thing "yourself",
        /// leave, and check what you're holding.
        ///
        /// The real values are captured before anything is touched and forced
        /// back in a finally, so a failure reports rather than damages.
        /// </summary>
        internal bool SelfTestVisit(out string detail)
        {
            detail = "";
            if (_session != null && _session.State != SessionState.Offline)
            {
                detail = "skipped — leave the session first";
                return false;
            }
            if (!Resolve() || _vars.Count == 0)
            {
                detail = "skipped — world globals aren't readable yet (load a save first)";
                return false;
            }

            var truth = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var v in _vars)
            {
                try { truth[v.Name] = v.RawValue; } catch { }
            }
            if (truth.Count == 0) { detail = "skipped — couldn't read any globals"; return false; }

            bool wasVisiting = _visiting, wasPending = _visitPending;
            string earnedName = null;

            try
            {
                _visiting = false; _visitPending = false;
                _ownWorld.Clear(); _earnedWhileVisiting.Clear();

                // 1. arrive
                _visitPending = true;
                if (!TryStash()) { detail = "FAILED — could not stash your progression"; return false; }
                if (_ownWorld.Count != truth.Count)
                {
                    detail = "FAILED — stashed " + _ownWorld.Count + " of " + truth.Count + " values";
                    return false;
                }

                // 2. the host's island lands on top of ours
                foreach (var v in _vars) Apply(v.Name, Flip(v));

                // 3. we unlock one thing ourselves, through the real detection path
                var mine = _vars[_vars.Count / 2];
                earnedName = mine.Name;
                try { mine.RawValue = Flip(mine); } catch { }
                ScanLocalChanges(false);
                if (!_earnedWhileVisiting.Contains(earnedName))
                {
                    detail = "FAILED — a change you made yourself wasn't recorded as yours";
                    return false;
                }
                object earnedValue = mine.RawValue;

                // 4. go home
                EndVisit();

                // 5. check what we're holding
                int wrong = 0; string firstWrong = null;
                foreach (var kv in truth)
                {
                    NamedVariable v;
                    if (!_byName.TryGetValue(kv.Key, out v) || v == null) continue;
                    object now;
                    try { now = v.RawValue; } catch { continue; }

                    bool ok = kv.Key == earnedName ? Equals(now, earnedValue) : Equals(now, kv.Value);
                    if (!ok) { wrong++; if (firstWrong == null) firstWrong = kv.Key; }
                }

                if (wrong > 0)
                {
                    detail = "FAILED — " + wrong + " of " + truth.Count
                           + " values came back wrong (first: " + firstWrong + ")";
                    return false;
                }

                detail = truth.Count + " values restored, 1 kept as earned";
                return true;
            }
            catch (Exception ex)
            {
                detail = "FAILED — " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            finally
            {
                // Whatever happened above, the player's own numbers go back.
                try
                {
                    _applying = true;
                    foreach (var kv in truth)
                    {
                        NamedVariable v;
                        if (!_byName.TryGetValue(kv.Key, out v) || v == null) continue;
                        try { v.RawValue = kv.Value; _last[kv.Key] = kv.Value; } catch { }
                    }
                }
                catch { }
                finally { _applying = false; }

                _ownWorld.Clear();
                _earnedWhileVisiting.Clear();
                _visiting = wasVisiting;
                _visitPending = wasPending;
            }
        }

        private static object Flip(NamedVariable v)
        {
            switch (v.VariableType)
            {
                case VariableType.Bool: return !Convert.ToBoolean(v.RawValue);
                case VariableType.Int: return Convert.ToInt32(v.RawValue) + 1;
                case VariableType.Float: return Convert.ToSingle(v.RawValue) + 1f;
                default: return v.RawValue;
            }
        }

        public string DebugLine
        {
            get
            {
                if (!Available) return "world globals not readable yet";
                var s = TrackedCount + " shared  ·  " + ChangesSent + " sent, " + ChangesApplied + " applied";
                if (_visiting)
                    s += "  ·  visiting (" + _ownWorld.Count + " of yours stashed, "
                       + _earnedWhileVisiting.Count + " earned here)";
                return s;
            }
        }
    }
}
