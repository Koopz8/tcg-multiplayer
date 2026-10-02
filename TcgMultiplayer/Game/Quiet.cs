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
                    if (f == null) continue;
                    if (_pending.Remove(f)) { if (!fsmWas.ContainsKey(f)) fsmWas[f] = true; continue; }
                    if (!f.enabled) continue;
                    if (!fsmWas.ContainsKey(f)) fsmWas[f] = true;
                    f.enabled = false;
                    FsmsSilenced++;
                }
            }
            catch { }
            go.SetActive(true);
        }

        /// <summary>After the objects are back as they were — enabling an FSM on a switched-off object runs nothing.</summary>
        /// <summary>
        /// Never switch an FSM back on while its object is still on. Physics
        /// and the screen mirror each switch things on and each put them back,
        /// one after the other — and skee ball's round controller, still on
        /// because the screen hadn't let go of it yet, got its FSM re-enabled by
        /// physics letting go first. It started up on the watcher: the power
        /// gauge appeared and the arm locked toward the card reader. So an FSM
        /// whose object is still on waits, and is put back the moment its
        /// object goes off (or now, if it stays off).
        /// </summary>
        public static void Restore(Dictionary<Behaviour, bool> fsmWas)
        {
            foreach (var kv in fsmWas)
            {
                var b = kv.Key;
                if (b == null) continue;
                if (b.gameObject.activeInHierarchy && kv.Value) { _pending.Add(b); continue; }
                try { b.enabled = kv.Value; } catch { }
            }
            fsmWas.Clear();
        }

        private static readonly List<Behaviour> _pending = new List<Behaviour>();
        private static float _pendingAt;
        public static int PendingFsms { get { return _pending.Count; } }

        /// <summary>Every frame or so: put back any waiting FSM whose object has gone off.</summary>
        public static void Tick()
        {
            if (_pending.Count == 0) return;
            float now = Time.time;
            if (now < _pendingAt) return;
            _pendingAt = now + 0.25f;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var b = _pending[i];
                if (b == null) { _pending.RemoveAt(i); continue; }
                if (b.gameObject.activeInHierarchy) continue;
                try { b.enabled = true; } catch { }
                _pending.RemoveAt(i);
            }
        }

        /// <summary>If we switch something on again for a new lease, it's ours again, not pending.</summary>
        public static void Forget(Behaviour b) { _pending.Remove(b); }

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
