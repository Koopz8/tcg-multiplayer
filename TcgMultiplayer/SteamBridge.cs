using System;
using System.Reflection;

namespace TcgMultiplayer
{
    /// <summary>
    /// The game ships Steamworks.NET's SteamManager inside HLG.Runtime.dll: it
    /// calls SteamAPI.Init() and pumps SteamAPI.RunCallbacks() every frame.
    ///
    /// So we do NOT init Steam and we do NOT pump callbacks — either would
    /// double-dispatch every callback in the game. We just wait for its
    /// Initialized flag, read by reflection so we take no build-fragile
    /// reference on HLG.Runtime.
    /// </summary>
    internal static class SteamBridge
    {
        private static PropertyInfo _initialized;
        private static bool _warned;

        public static bool Initialized
        {
            get
            {
                if (_initialized == null && !Resolve()) return false;
                try { return (bool)_initialized.GetValue(null, null); }
                catch { return false; }
            }
        }

        private static bool Resolve()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t;
                    try { t = asm.GetType("SteamManager", false); }
                    catch { continue; }
                    if (t == null) continue;

                    var p = t.GetProperty("Initialized",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (p == null || p.PropertyType != typeof(bool)) continue;

                    _initialized = p;
                    Plugin.Log("Found SteamManager in " + asm.GetName().Name);
                    return true;
                }
            }
            catch { }

            if (!_warned)
            {
                _warned = true;
                Plugin.Warn("SteamManager not found yet — waiting for the game to initialise Steam.");
            }
            return false;
        }
    }
}
