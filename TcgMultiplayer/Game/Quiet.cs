using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace TcgMultiplayer.Game
{
    /// <summary>
    /// Switching something on for the watcher without running it.
    ///
    /// To show a part the owner's round switched on, the watcher switches on
    /// that part and its parents. On skee ball that parent is "GAME CNTRL_SKEE
    /// BALL Starts OFF" — the round controller itself — and switching it on
    /// started its FSM on the watcher's side: it read the WATCHER's buttons,
    /// switched on the machine's first-person hands, and locked the watcher's
    /// right arm aiming down the lane until the session ended. So every FSM
    /// under something we switch on is switched off first, and put back
    /// exactly as it was on release. The objects draw; nothing in them runs.
    /// </summary>
    internal static class Quiet
    {
        public static int FsmsSilenced;

        public static void Activate(GameObject go, Dictionary<Behaviour, bool> fsmWas)
        {
            if (go == null || go.activeSelf) return;
            try
            {
                var fsms = go.GetComponentsInChildren<PlayMakerFSM>(true);
                for (int i = 0; i < fsms.Length; i++)
                {
                    var f = fsms[i];
                    if (f == null || !f.enabled) continue;
                    if (!fsmWas.ContainsKey(f)) fsmWas[f] = true;
                    f.enabled = false;
                    FsmsSilenced++;
                }
            }
            catch { }
            go.SetActive(true);
        }

        /// <summary>After the objects are back as they were — enabling an FSM on a switched-off object runs nothing.</summary>
        public static void Restore(Dictionary<Behaviour, bool> fsmWas)
        {
            foreach (var kv in fsmWas)
                if (kv.Key != null) { try { kv.Key.enabled = kv.Value; } catch { } }
            fsmWas.Clear();
        }

        // ------------------------------------------------ 2D physics, by name
        // Speed Drop's balls are Rigidbody2D, and the 2D physics module isn't
        // one we reference, so "simulated" is reached by reflection.
        private static PropertyInfo _simulated;

        /// <summary>Stops a 2D body simulating on the watcher's side; returns true if it changed anything.</summary>
        public static bool Hold2D(Transform t, Dictionary<Component, bool> was)
        {
            if (t == null) return false;
            try
            {
                var c = t.GetComponent("Rigidbody2D");
                if (c == null) return false;
                if (_simulated == null) _simulated = c.GetType().GetProperty("simulated");
                if (_simulated == null) return false;
                bool on = (bool)_simulated.GetValue(c, null);
                if (!on) return false;
                if (!was.ContainsKey(c)) was[c] = true;
                _simulated.SetValue(c, false, null);
                return true;
            }
            catch { return false; }
        }

        public static void Restore2D(Dictionary<Component, bool> was)
        {
            foreach (var kv in was)
                if (kv.Key != null && _simulated != null) { try { _simulated.SetValue(kv.Key, kv.Value, null); } catch { } }
            was.Clear();
        }
    }
}
