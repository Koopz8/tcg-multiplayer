using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace TcgMultiplayer
{
    /// <summary>
    /// Stops the cursor flickering while the panel is open.
    ///
    /// The trace from the very first teardown showed an FSM re-issuing
    /// "Cursor LOCKED" around forty times a second. The original answer was to
    /// re-assert CursorLockMode.None every Update and again in OnGUI, which
    /// works in the sense that the cursor is usable — but it is a fight at frame
    /// rate, and you can see it: the game locks, we unlock, the game locks
    /// again, and the pointer strobes.
    ///
    /// Reverting a change every frame is the wrong shape. Better to stop the
    /// change happening: prefix the two Cursor setters and, while the panel is
    /// open, drop any attempt to lock the cursor or hide it. Nothing to undo, so
    /// nothing to flicker.
    ///
    /// If the patch can't be applied — these are thin wrappers over engine
    /// internals and Unity has moved them before — we say so and fall back to
    /// the old re-assert, which is uglier but still usable.
    /// </summary>
    internal static class CursorGuard
    {
        /// <summary>Set by the plugin: true while the overlay wants the cursor.</summary>
        public static Func<bool> ShouldHold;

        /// <summary>
        /// What we want the cursor to be while we're holding it.
        ///
        /// Confined, not None: None lets the pointer wander off the game window
        /// entirely, which on a second monitor means clicking the panel drops you
        /// out of the game. Confined keeps it visible and freely movable but
        /// bounded by the window, which is what a mouse-driven overlay wants.
        /// </summary>
        public static CursorLockMode Desired = CursorLockMode.Confined;

        public static bool Patched { get; private set; }
        public static int BlockedLocks, BlockedHides;

        private static bool Holding
        {
            get
            {
                try { return ShouldHold != null && ShouldHold(); }
                catch { return false; }
            }
        }

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            int ok = 0;
            ok += Patch(harmony, "set_lockState", typeof(CursorLockMode), nameof(BlockLock));
            ok += Patch(harmony, "set_visible", typeof(bool), nameof(BlockHide));

            Patched = ok == 2;
            if (Patched)
                Plugin.Log("Cursor guard active — the panel won't fight the game for the pointer.");
            else
                Plugin.Warn("Cursor guard could not be applied (" + ok + " of 2). Falling back to "
                            + "re-asserting the cursor each frame; it will work, but may flicker.");
        }

        private static int Patch(HarmonyLib.Harmony h, string setter, Type argType, string prefix)
        {
            try
            {
                var target = AccessTools.Method(typeof(Cursor), setter, new[] { argType });
                if (target == null) { Plugin.Warn("Cursor guard: " + setter + " not found."); return 0; }

                h.Patch(target, prefix: new HarmonyMethod(
                    typeof(CursorGuard).GetMethod(prefix, BindingFlags.NonPublic | BindingFlags.Static)));
                return 1;
            }
            catch (Exception ex)
            {
                Plugin.Warn("Cursor guard: could not patch " + setter + " — " + ex.Message);
                return 0;
            }
        }

        // Returning false skips the original setter, so the game's own attempt
        // simply never lands while we're holding the pointer.

        private static bool BlockLock(CursorLockMode value)
        {
            // While holding, only the mode we asked for gets through. That means
            // blocking the game re-locking to the centre AND blocking it setting
            // None, which would let the pointer leave the window.
            if (!Holding || value == Desired) return true;
            BlockedLocks++;
            return false;
        }

        /// <summary>
        /// Hands the pointer back. Called once when the panel closes, so the game
        /// isn't left with a confine it never asked for — after this its own
        /// cursor handling takes over again on the next frame.
        /// </summary>
        public static void Release()
        {
            try { Cursor.lockState = CursorLockMode.None; }
            catch { }
        }

        private static bool BlockHide(bool value)
        {
            if (!Holding || value) return true;
            BlockedHides++;
            return false;
        }

        public static string Status
        {
            get
            {
                if (!Patched) return "not active — cursor may flicker";
                if (!Holding) return "idle (the game controls the pointer)";
                return (Desired == CursorLockMode.Confined ? "held inside the window" : "free")
                     + "  ·  " + BlockedLocks + " locks, " + BlockedHides + " hides ignored";
            }
        }
    }
}
