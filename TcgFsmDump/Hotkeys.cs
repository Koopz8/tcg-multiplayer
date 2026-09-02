using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace TcgFsmDump
{
    /// <summary>
    /// The Coin Game ships both the legacy Input module and the Input System
    /// package. If the project is set to "Input System (New)" only, touching
    /// UnityEngine.Input throws. So: try legacy once, fall back to the Input
    /// System by reflection, and if neither answers, hotkeys are simply off.
    /// </summary>
    internal static class Hotkeys
    {
        private enum Mode { Unknown, Legacy, InputSystem, None }
        private static Mode _mode = Mode.Unknown;

        private static Type _keyboardType, _keyEnumType;
        private static PropertyInfo _currentProp, _wasPressedProp;
        private static PropertyInfo _keyIndexer;
        private static readonly Dictionary<string, object> _keyCache = new Dictionary<string, object>();

        public static bool Down(string keyName)
        {
            if (_mode == Mode.None) return false;

            if (_mode == Mode.Unknown || _mode == Mode.Legacy)
            {
                try
                {
                    KeyCode code;
                    try { code = (KeyCode)Enum.Parse(typeof(KeyCode), keyName, true); }
                    catch { _mode = Mode.None; return false; }

                    bool down = Input.GetKeyDown(code);
                    _mode = Mode.Legacy;
                    return down;
                }
                catch (Exception)
                {
                    // Legacy input disabled by project settings — go the other way.
                    _mode = Mode.InputSystem;
                }
            }

            return InputSystemDown(keyName);
        }

        private static bool InputSystemDown(string keyName)
        {
            try
            {
                if (_keyboardType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var t = asm.GetType("UnityEngine.InputSystem.Keyboard", false);
                        if (t == null) continue;
                        _keyboardType = t;
                        _keyEnumType = asm.GetType("UnityEngine.InputSystem.Key", false);
                        _currentProp = t.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
                        break;
                    }
                    if (_keyboardType == null || _keyEnumType == null || _currentProp == null)
                    {
                        _mode = Mode.None;
                        return false;
                    }
                }

                var kb = _currentProp.GetValue(null, null);
                if (kb == null) return false;

                object keyValue;
                if (!_keyCache.TryGetValue(keyName, out keyValue))
                {
                    keyValue = Enum.Parse(_keyEnumType, keyName, true);
                    _keyCache[keyName] = keyValue;
                }

                if (_keyIndexer == null)
                {
                    foreach (var p in _keyboardType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        var idx = p.GetIndexParameters();
                        if (idx.Length == 1 && idx[0].ParameterType == _keyEnumType) { _keyIndexer = p; break; }
                    }
                    if (_keyIndexer == null) { _mode = Mode.None; return false; }
                }

                var control = _keyIndexer.GetValue(kb, new[] { keyValue });
                if (control == null) return false;

                if (_wasPressedProp == null || _wasPressedProp.DeclaringType != control.GetType())
                    _wasPressedProp = control.GetType().GetProperty("wasPressedThisFrame");

                if (_wasPressedProp == null) { _mode = Mode.None; return false; }
                return (bool)_wasPressedProp.GetValue(control, null);
            }
            catch
            {
                _mode = Mode.None;
                return false;
            }
        }

        public static string ActiveBackend
        {
            get
            {
                switch (_mode)
                {
                    case Mode.Legacy: return "legacy Input";
                    case Mode.InputSystem: return "Input System package";
                    case Mode.None: return "unavailable";
                    default: return "not yet probed";
                }
            }
        }
    }
}
