using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace TcgMultiplayer.Game
{
    /// <summary>
    /// Stops the second window writing to the save.
    ///
    /// Local test mode runs two copies of the game on one PC, and they share one
    /// save folder. Two processes writing the same EasySave files is a textbook
    /// way to corrupt them, and the failure is silent until the next load. So
    /// the guest window does not save at all — not "should not", does not.
    ///
    /// This FAILS CLOSED. If the patches can't be applied, local test mode
    /// refuses to start on a guest slot rather than starting without the
    /// protection. A test harness is not worth someone's save file, and the
    /// whole point of the thing is to be run by someone who has no second copy
    /// of the game and therefore nothing to fall back on.
    ///
    /// The host window is untouched and saves exactly as it always has.
    /// </summary>
    internal static class SaveBlock
    {
        public static bool Engaged { get; private set; }
        public static int Blocked { get; private set; }
        public static string Detail { get; private set; }

        private static readonly List<string> _hooked = new List<string>();

        /// <summary>
        /// The game's own save entry points. Two of them, belt and braces: the
        /// PlayMaker action the dev's graphs call, and EasySave's own auto-save
        /// manager, which fires on quit and on a timer without asking anyone.
        /// </summary>
        private static readonly string[] SaveActionTypes =
        {
            "ActionTCGSaveData",
            "HLG.PlayMaker.CustomActions.ActionTCGSaveData",
        };

        private static readonly string[] AutoSaveTypes =
        {
            "ES3AutoSaveMgr",
        };

        /// <summary>
        /// Patch everything we can find. Returns false if nothing was hooked,
        /// which the caller must treat as "do not run a guest window".
        /// </summary>
        public static bool Engage(HarmonyLib.Harmony harmony)
        {
            if (Engaged) return true;

            _hooked.Clear();

            foreach (var name in SaveActionTypes)
                TryPatchMethod(harmony, name, "OnEnter");

            foreach (var name in AutoSaveTypes)
            {
                TryPatchMethod(harmony, name, "Save");
                TryPatchMethod(harmony, name, "OnApplicationQuit");
                TryPatchMethod(harmony, name, "OnApplicationPause");
            }

            Engaged = _hooked.Count > 0;
            Detail = Engaged
                ? "blocking " + string.Join(", ", _hooked.ToArray())
                : "found none of the game's save entry points";

            if (Engaged) Plugin.Log("Save block engaged: " + Detail + ".");
            else Plugin.Warn("Save block COULD NOT engage: " + Detail + ".");

            CompatCheck.Set("save block (for local test windows)", Engaged, Detail);
            return Engaged;
        }

        private static void TryPatchMethod(HarmonyLib.Harmony harmony, string typeName, string methodName)
        {
            try
            {
                var t = FindType(typeName);
                if (t == null) return;

                var m = AccessTools.Method(t, methodName, Type.EmptyTypes);
                if (m == null) return;

                harmony.Patch(m, prefix: new HarmonyMethod(
                    typeof(SaveBlock).GetMethod(nameof(Refuse),
                        BindingFlags.NonPublic | BindingFlags.Static)));

                _hooked.Add(t.Name + "." + methodName);
            }
            catch (Exception ex)
            {
                Plugin.Warn("Save block could not patch " + typeName + "." + methodName + ": " + ex.Message);
            }
        }

        private static Type FindType(string name)
        {
            var t = AccessTools.TypeByName(name);
            if (t != null) return t;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    t = asm.GetType(name, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// The prefix itself. Returning false skips the original, which is the
        /// save. Deliberately does nothing else — this runs on the game's own
        /// save path and must never be the thing that throws.
        /// </summary>
        private static bool Refuse()
        {
            if (!Active) return true;
            Blocked++;
            return false;
        }

        /// <summary>
        /// Set once, at startup, by whoever knows which window this is. Not a
        /// toggle: a guest window is a guest window for its whole life, and
        /// letting this be turned off mid-run would defeat the point.
        /// </summary>
        public static bool Active { get; private set; }

        public static void ActivateForGuestWindow()
        {
            if (Active) return;
            Active = true;
            Plugin.Log("This window will NOT write to your save. It is a local test guest.");
        }

        public static string Status
        {
            get
            {
                if (!Active) return "saving normally";
                return "saving blocked (" + Blocked + " writes refused) — this is a test window";
            }
        }
    }
}
