using System;
using System.Reflection;

namespace TcgMultiplayer
{
    /// <summary>
    /// While the overlay is open we want typing to go to the chat box, not to the
    /// player. The game drives input through Rewired, and its own
    /// ControllerDisconnectView does exactly this when it needs to steal input —
    /// so we borrow the same move: SetAllMapsEnabled(false), then restore.
    ///
    /// Reached by reflection: Rewired_Core.dll is large and its API surface moves
    /// between versions, and a missing method here should cost us a warning, not
    /// the mod. This is also the groundwork for M2, where remote avatars must be
    /// cut off from local input entirely.
    /// </summary>
    internal static class InputLock
    {
        private static bool _resolved, _available, _locked;
        private static object _mapHelper;
        private static MethodInfo _setAllMapsEnabled;

        public static bool Locked { get { return _locked; } }

        public static void Set(bool locked)
        {
            if (locked == _locked) return;
            if (!Resolve()) { _locked = locked; return; }

            try
            {
                _setAllMapsEnabled.Invoke(_mapHelper, new object[] { !locked });
                _locked = locked;
            }
            catch (Exception ex)
            {
                Plugin.Warn("Input lock failed: " + ex.Message);
                _available = false;
                _locked = locked;
            }
        }

        public static string Status
        {
            get
            {
                if (!_resolved) return "not probed";
                return _available ? (_locked ? "game input suppressed" : "game input active")
                                  : "Rewired not reachable";
            }
        }

        private static bool Resolve()
        {
            if (_resolved) return _available;
            _resolved = true;

            try
            {
                Type reInput = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    reInput = asm.GetType("Rewired.ReInput", false);
                    if (reInput != null) break;
                }
                if (reInput == null) { Plugin.Warn("Rewired.ReInput not found; overlay will not suppress game input."); return false; }

                var playersProp = reInput.GetProperty("players", BindingFlags.Public | BindingFlags.Static);
                var players = playersProp != null ? playersProp.GetValue(null, null) : null;
                if (players == null) return false;

                var getPlayer = players.GetType().GetMethod("GetPlayer", new[] { typeof(int) });
                var player = getPlayer != null ? getPlayer.Invoke(players, new object[] { 0 }) : null;
                if (player == null) return false;

                var controllers = player.GetType().GetProperty("controllers").GetValue(player, null);
                _mapHelper = controllers.GetType().GetProperty("maps").GetValue(controllers, null);
                if (_mapHelper == null) return false;

                _setAllMapsEnabled = _mapHelper.GetType().GetMethod("SetAllMapsEnabled", new[] { typeof(bool) });
                _available = _setAllMapsEnabled != null;
                return _available;
            }
            catch (Exception ex)
            {
                Plugin.Warn("Rewired probe failed: " + ex.Message);
                return false;
            }
        }
    }
}
