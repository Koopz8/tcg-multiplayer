using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace TcgMultiplayer
{
    /// <summary>
    /// Answers "is this mod still talking to the game it was written against?"
    ///
    /// Almost everything here binds to names rather than to code: FSM names, state
    /// names, event names, PlayMaker global names, the path to the player mesh.
    /// The developer can rename any of those in a patch without it looking like a
    /// breaking change from their side — and the failure mode would otherwise be
    /// silent, with machines that never claim and a wallet guard that never fires.
    ///
    /// So the mod states plainly what it can and cannot find, instead of quietly
    /// doing nothing. Two players also compare a hash of the game assembly, so a
    /// mismatched pair find out at the handshake rather than through an hour of
    /// strange desyncs.
    /// </summary>
    internal static class CompatCheck
    {
        public sealed class Item
        {
            public string What;
            public bool Ok;
            public string Detail;
        }

        private static readonly List<Item> _items = new List<Item>();
        public static IEnumerable<Item> Items { get { return _items; } }

        public static string GameHash { get; private set; }
        public static bool Healthy { get; private set; }
        private static bool _reported;

        public static void Set(string what, bool ok, string detail)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].What != what) continue;
                _items[i].Ok = ok;
                _items[i].Detail = detail;
                Recompute();
                return;
            }
            _items.Add(new Item { What = what, Ok = ok, Detail = detail });
            Recompute();
        }

        private static void Recompute()
        {
            bool all = true;
            for (int i = 0; i < _items.Count; i++) if (!_items[i].Ok) all = false;
            Healthy = all && _items.Count > 0;
        }

        /// <summary>
        /// FNV-1a over Assembly-CSharp.dll. Not a security measure — just a cheap,
        /// stable way for two players to notice they are on different builds.
        /// </summary>
        public static void ComputeGameHash()
        {
            try
            {
                var managed = Path.Combine(Application.dataPath, "Managed");
                var dll = Path.Combine(managed, "Assembly-CSharp.dll");
                if (!File.Exists(dll)) { GameHash = "unknown"; return; }

                var bytes = File.ReadAllBytes(dll);
                unchecked
                {
                    ulong h = 14695981039346656037UL;
                    for (int i = 0; i < bytes.Length; i++)
                    {
                        h ^= bytes[i];
                        h *= 1099511628211UL;
                    }
                    GameHash = h.ToString("x16");
                }
                Plugin.Log("Game build fingerprint: " + GameHash + " (" + (bytes.Length / 1024) + " KB)");
            }
            catch (Exception ex)
            {
                GameHash = "unknown";
                Plugin.Warn("Could not fingerprint the game assembly: " + ex.Message);
            }
        }

        /// <summary>Logs the verdict once, the first time everything has had a chance to resolve.</summary>
        public static void ReportOnce()
        {
            if (_reported || _items.Count == 0) return;
            _reported = true;

            if (Healthy)
            {
                Plugin.Log("Compatibility: all checks passed on build " + GameHash + ".");
                return;
            }

            Plugin.Warn("Compatibility: this build of the game does not match what the mod expects.");
            foreach (var i in _items)
                if (!i.Ok) Plugin.Warn("  MISSING: " + i.What + (string.IsNullOrEmpty(i.Detail) ? "" : " — " + i.Detail));
            Plugin.Warn("  Multiplayer will be unreliable. Report the game version with this list.");
        }

        public static string Summary
        {
            get
            {
                if (_items.Count == 0) return "not checked yet";
                int bad = 0;
                foreach (var i in _items) if (!i.Ok) bad++;
                return bad == 0
                    ? "all " + _items.Count + " checks passed"
                    : bad + " of " + _items.Count + " checks FAILED — see the log";
            }
        }
    }
}
