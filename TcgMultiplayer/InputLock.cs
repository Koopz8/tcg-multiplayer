using System;
using System.Reflection;

namespace TcgMultiplayer
{
    /// <summary>
    /// While the overlay is open, typing should go to the chat box and not to
    /// the player. The game drives input through Rewired, and its own
    /// ControllerDisconnectView does exactly this when it needs to steal input —
    /// Assembly-CSharp really does call SetAllMapsEnabled — so we borrow the
    /// same move.
    ///
    /// Everything here is reflection, for two reasons. Rewired_Core.dll ships
    /// obfuscated, so its internal type names are mangled and only the public
    /// surface survives; and that surface is not shaped the way the docs imply —
    /// `controllers` is not a property, which is precisely what the first
    /// version assumed and why it silently never worked. So each hop is looked
    /// up as a property OR a field, and if the name misses entirely we fall back
    /// to finding the member by its type. ControllerHelper and MapHelper kept
    /// their type names through the obfuscator even though their members didn't.
    ///
    /// The other half of the old bug was the retry. Rewired has no player 0 with
    /// controllers until the game has properly started, and the overlay can be
    /// opened before then — but Set() early-returned once the desired state
    /// matched, so a failed probe was never followed by a second attempt. Intent
    /// and application are now separate: Set() records what we want, Tick()
    /// keeps trying until the world can deliver it.
    /// </summary>
    internal static class InputLock
    {
        private static bool _want;        // what the overlay asked for
        private static bool _applied;     // what Rewired has actually been told
        private static bool _available;
        private static bool _probed, _loggedFailure;
        private static float _nextProbeAt;

        private static object _mapHelper;
        private static MethodInfo _setAllMapsEnabled;

        private const float RetrySeconds = 3f;

        public static bool Locked { get { return _applied; } }

        /// <summary>Records intent. Applying it is Tick's job.</summary>
        public static void Set(bool locked) { _want = locked; }

        /// <summary>Called every frame. Cheap when there is nothing to do.</summary>
        public static void Tick()
        {
            if (_want == _applied) return;
            if (!Resolve()) return;

            try
            {
                _setAllMapsEnabled.Invoke(_mapHelper, new object[] { !_want });
                _applied = _want;
            }
            catch (Exception ex)
            {
                Plugin.Warn("Input lock failed: " + ex.Message);
                _available = false;
                _mapHelper = null;
                _applied = _want;   // don't spin on a broken handle
            }
        }

        public static string Status
        {
            get
            {
                if (!_probed) return "not probed";
                if (_available) return _applied ? "game input suppressed" : "game input active";
                return "can't reach Rewired yet — typing may also move you";
            }
        }

        // ------------------------------------------------------------- probing

        private static bool Resolve()
        {
            if (_available && _mapHelper != null && _setAllMapsEnabled != null) return true;

            float now;
            try { now = UnityEngine.Time.realtimeSinceStartup; } catch { now = 0f; }
            if (_probed && now < _nextProbeAt) return false;

            _probed = true;
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

                var controllers = Read(player, "controllers", "ControllerHelper");
                if (controllers == null) return Fail("couldn't reach the controller helper on player 0");

                _mapHelper = Read(controllers, "maps", "MapHelper");
                if (_mapHelper == null) return Fail("couldn't reach the map helper");

                _setAllMapsEnabled = FindSetAllMapsEnabled(_mapHelper.GetType());
                if (_setAllMapsEnabled == null) return Fail("SetAllMapsEnabled not found on this Rewired build");

                _available = true;
                if (_loggedFailure) Plugin.Log("Rewired is reachable now — the overlay will suppress game input.");
                return true;
            }
            catch (Exception ex) { return Fail(ex.GetType().Name + ": " + ex.Message); }
        }

        /// <summary>
        /// Reads a member by name, as a property or a field, and failing that by
        /// looking for one whose type name contains <paramref name="typeHint"/>.
        /// Obfuscation renames members but left these helper types alone.
        /// </summary>
        private static object Read(object target, string name, string typeHint)
        {
            if (target == null) return null;
            var t = target.GetType();
            const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            try
            {
                var p = t.GetProperty(name, Any);
                if (p != null && p.CanRead) { var v = p.GetValue(target, null); if (v != null) return v; }

                var f = t.GetField(name, Any);
                if (f != null) { var v = f.GetValue(target); if (v != null) return v; }

                foreach (var pp in t.GetProperties(Any))
                {
                    if (pp.PropertyType.Name.IndexOf(typeHint, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (pp.GetIndexParameters().Length != 0 || !pp.CanRead) continue;
                    var v = pp.GetValue(target, null); if (v != null) return v;
                }
                foreach (var ff in t.GetFields(Any))
                {
                    if (ff.FieldType.Name.IndexOf(typeHint, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var v = ff.GetValue(target); if (v != null) return v;
                }
            }
            catch { }
            return null;
        }

        /// <summary>By name if it survived, otherwise the only public void(bool) on the type.</summary>
        private static MethodInfo FindSetAllMapsEnabled(Type mapHelper)
        {
            var byName = mapHelper.GetMethod("SetAllMapsEnabled", new[] { typeof(bool) });
            if (byName != null) return byName;

            MethodInfo only = null;
            foreach (var m in mapHelper.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.ReturnType != typeof(void)) continue;
                var ps = m.GetParameters();
                if (ps.Length != 1 || ps[0].ParameterType != typeof(bool)) continue;
                if (only != null) return null;   // ambiguous, don't guess
                only = m;
            }
            return only;
        }

        private static bool Fail(string why)
        {
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                Plugin.Warn("Can't suppress game input yet (" + why + "). Still trying; until it "
                            + "works, typing in chat may also move your player.");
            }
            return false;
        }
    }
}
