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

            // Polling rather than hooking: unlocks are set from PlayMaker actions
            // scattered across the game, and half a second of latency on "the
            // arcade is now unlocked" is imperceptible.
            for (int i = 0; i < _vars.Count; i++)
            {
                var v = _vars[i];
                object now;
                try { now = v.RawValue; } catch { continue; }

                object was;
                if (_last.TryGetValue(v.Name, out was) && Equals(was, now)) continue;
                _last[v.Name] = now;

                if (_applying) continue;             // it was us, applying a remote value
                _session.SendWorldVar(v.Name, now);
                ChangesSent++;
                Plugin.Log("World change: " + v.Name + " = " + now);
            }
        }

        // ------------------------------------------------------------- receiving

        private void OnRemoteChange(CSteamID from, string name, object value, bool fromHost)
        {
            if (!Resolve()) return;

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

        public string DebugLine
        {
            get
            {
                if (!Available) return "world globals not readable yet";
                return TrackedCount + " shared  ·  " + ChangesSent + " sent, " + ChangesApplied + " applied";
            }
        }
    }
}
