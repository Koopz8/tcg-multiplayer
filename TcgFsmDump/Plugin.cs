using System;
using System.Collections.Generic;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(TcgFsmDump.Plugin), "TcgFsmDump", TcgFsmDump.Plugin.Version, "Mason")]
[assembly: MelonGame("devotid", "TheCoinGame")]

namespace TcgFsmDump
{
    public class Plugin : MelonMod
    {
        public const string Version = "0.2.0";

        private static Plugin _instance;
        private HarmonyLib.Harmony _harmony;

        // --- preferences -------------------------------------------------
        private static MelonPreferences_Entry<bool> _pDumpActions;
        private static MelonPreferences_Entry<bool> _pDumpValues;
        private static MelonPreferences_Entry<bool> _pIncludeInactive;
        private static MelonPreferences_Entry<bool> _pAutoDump;
        private static MelonPreferences_Entry<float> _pAutoDumpDelay;
        private static MelonPreferences_Entry<bool> _pTraceStates;
        private static MelonPreferences_Entry<bool> _pTraceSystemEvents;
        private static MelonPreferences_Entry<string> _pMutedEvents;
        private static MelonPreferences_Entry<string> _pDumpKey;
        private static MelonPreferences_Entry<string> _pTraceKey;

        public static bool DumpActions { get { return _pDumpActions == null || _pDumpActions.Value; } }
        public static bool DumpVariableValues { get { return _pDumpValues == null || _pDumpValues.Value; } }
        public static bool IncludeInactive { get { return _pIncludeInactive == null || _pIncludeInactive.Value; } }
        public static bool TraceStates { get { return _pTraceStates == null || _pTraceStates.Value; } }
        public static bool TraceSystemEvents { get { return _pTraceSystemEvents != null && _pTraceSystemEvents.Value; } }

        private static HashSet<string> _muted;

        public static bool IsEventMuted(string name)
        {
            if (_muted == null)
            {
                _muted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var raw = _pMutedEvents != null ? _pMutedEvents.Value : "";
                if (!string.IsNullOrEmpty(raw))
                    foreach (var part in raw.Split(','))
                    {
                        var t = part.Trim();
                        if (t.Length > 0) _muted.Add(t);
                    }
            }
            return _muted.Contains(name);
        }

        // --- scheduling --------------------------------------------------
        private float _autoDumpAt = -1f;
        private string _pendingSceneName;

        public override void OnInitializeMelon()
        {
            _instance = this;

            var cat = MelonPreferences.CreateCategory("TcgFsmDump", "TCG FSM Dump");
            _pDumpActions = cat.CreateEntry("DumpActions", true, "Dump action type names",
                "Lists the PlayMaker actions inside each state. Forces PlayMaker's lazy action load — turn off if a scene misbehaves during a dump.");
            _pDumpValues = cat.CreateEntry("DumpVariableValues", true, "Dump variable values",
                "Records the current value of every FSM variable, not just its name and type.");
            _pIncludeInactive = cat.CreateEntry("IncludeInactive", true, "Include inactive objects",
                "Also sweeps FSMs on disabled GameObjects, which PlayMaker's own list leaves out.");
            _pAutoDump = cat.CreateEntry("AutoDumpOnSceneLoad", true, "Auto-dump on scene load",
                "Writes a dump automatically a few seconds after each scene finishes loading.");
            _pAutoDumpDelay = cat.CreateEntry("AutoDumpDelaySeconds", 6f, "Auto-dump delay (s)",
                "Wait this long after a scene loads so late Awake/Start work has settled.");
            _pTraceStates = cat.CreateEntry("TraceStates", true, "Trace state changes",
                "Log every FSM state entry while tracing is on.");
            _pTraceSystemEvents = cat.CreateEntry("TraceSystemEvents", false, "Trace system events",
                "Include UPDATE / FIXED_UPDATE / etc. Floods the log — leave off unless you need it.");
            _pMutedEvents = cat.CreateEntry("MutedEvents", "", "Muted events (comma separated)",
                "Event names to leave out of the trace once you know they're noise.");
            _pDumpKey = cat.CreateEntry("DumpKey", "F7", "Dump hotkey");
            _pTraceKey = cat.CreateEntry("TraceKey", "F8", "Trace toggle hotkey");

            _harmony = new HarmonyLib.Harmony("com.mason.tcgfsmdump");
            try
            {
                EventTracer.ApplyPatches(_harmony);
            }
            catch (Exception ex)
            {
                Warn("Harmony patching failed: " + ex);
            }

            Log("Ready. " + _pDumpKey.Value + " = dump scene, " + _pTraceKey.Value + " = toggle live trace.");
            Log("Output folder: " + Paths.OutputDir);
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            _muted = null; // let a preference edit take effect on the next scene
            if (_pAutoDump != null && _pAutoDump.Value)
            {
                _autoDumpAt = Time.realtimeSinceStartup + Mathf.Max(0.5f, _pAutoDumpDelay.Value);
                _pendingSceneName = sceneName;
            }
        }

        public override void OnUpdate()
        {
            if (_autoDumpAt > 0f && Time.realtimeSinceStartup >= _autoDumpAt)
            {
                _autoDumpAt = -1f;
                RunDump(_pendingSceneName, "auto");
            }

            if (Hotkeys.Down(_pDumpKey != null ? _pDumpKey.Value : "F7"))
                RunDump(null, "hotkey");

            if (Hotkeys.Down(_pTraceKey != null ? _pTraceKey.Value : "F8"))
                EventTracer.Toggle();

            EventTracer.Tick();
        }

        public override void OnApplicationQuit()
        {
            if (EventTracer.Enabled) EventTracer.Stop();
        }

        private void RunDump(string sceneName, string reason)
        {
            try
            {
                var label = sceneName;
                if (string.IsNullOrEmpty(label))
                    label = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

                var t0 = Time.realtimeSinceStartup;
                var file = FsmDumper.Dump(label);
                var ms = (Time.realtimeSinceStartup - t0) * 1000f;
                Log(string.Format("Dumped '{0}' ({1}) in {2:0}ms -> {3}", label, reason, ms, file));
            }
            catch (Exception ex)
            {
                Warn("Dump failed: " + ex);
            }
        }

        internal static void Log(string msg)
        {
            if (_instance != null) _instance.LoggerInstance.Msg(msg);
            else MelonLogger.Msg(msg);
        }

        internal static void Warn(string msg)
        {
            if (_instance != null) _instance.LoggerInstance.Warning(msg);
            else MelonLogger.Warning(msg);
        }
    }
}
