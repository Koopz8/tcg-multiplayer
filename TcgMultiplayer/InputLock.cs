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
        private static float _nextProbeAt;
        private static bool _loggedFailure;

        /// <summary>How long to wait before probing again after a failure.</summary>
        private const float RetrySeconds = 5f;

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
                if (_available) return _locked ? "game input suppressed" : "game input active";
                return "can't reach Rewired yet — typing may also move you";
            }
        }

        /// <summary>
        /// Finds Rewired's map helper, and keeps trying if it isn't there yet.
        ///
        /// The first version latched: one probe, and whatever it found — or
        /// didn't — was the answer for the rest of the session. That is wrong,
        /// because Rewired doesn't have a player 0 until the game has got going,
        /// and the overlay can be opened before then. A probe at the main menu
        /// permanently disabled input suppression, so from that point on typing
        /// in chat also drove the player, with only one line in a log file to
        /// say why. Success latches; failure is just "not yet".
        /// </summary>
        private static bool Resolve()
        {
            if (_available) return true;

            float now;
            try { now = UnityEngine.Time.realtimeSinceStartup; } catch { now = 0f; }
            if (_resolved && now < _nextProbeAt) return false;

            _resolved = true;
            _nextProbeAt = now + RetrySeconds;

            try
            {
                Type reInput = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    reInput = asm.GetType("Rewired.ReInput", false);
                    if (reInput != null) break;
                }
                if (reInput == null) return Fail("Rewired.ReInput not found");

                var playersProp = reInput.GetProperty("players", BindingFlags.Public | BindingFlags.Static);
                var players = playersProp != null ? playersProp.GetValue(null, null) : null;
                if (players == null) return Fail("Rewired has no players object yet");

                var getPlayer = players.GetType().GetMethod("GetPlayer", new[] { typeof(int) });
                var player = getPlayer != null ? getPlayer.Invoke(players, new object[] { 0 }) : null;
                if (player == null) return Fail("Rewired player 0 doesn't exist yet");

                // Each step guarded on its own. The original chained these and
                // dereferenced whatever came back, which is where the null
                // reference came from when the player wasn't ready.
                var controllersProp = player.GetType().GetProperty("controllers");
                var controllers = controllersProp != null ? controllersProp.GetValue(player, null) : null;
                if (controllers == null) return Fail("no controllers on Rewired player 0 yet");

                var mapsProp = controllers.GetType().GetProperty("maps");
                _mapHelper = mapsProp != null ? mapsProp.GetValue(controllers, null) : null;
                if (_mapHelper == null) return Fail("no map helper on Rewired controllers yet");

                _setAllMapsEnabled = _mapHelper.GetType().GetMethod("SetAllMapsEnabled", new[] { typeof(bool) });
                if (_setAllMapsEnabled == null) return Fail("SetAllMapsEnabled not found on this Rewired version");

                _available = true;
                if (_loggedFailure) Plugin.Log("Rewired is up — the overlay will suppress game input from now on.");
                return true;
            }
            catch (Exception ex)
            {
                return Fail(ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Reports once, then stays quiet — this runs every time the overlay opens.</summary>
        private static bool Fail(string why)
        {
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                Plugin.Warn("Can't suppress game input yet (" + why + "). Will keep trying; "
                            + "until it works, typing in chat may also move your player.");
            }
            return false;
        }
    }
}
