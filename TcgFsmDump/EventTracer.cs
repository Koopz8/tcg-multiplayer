using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using HutongGames.PlayMaker;
using UnityEngine;

namespace TcgFsmDump
{
    /// <summary>
    /// Live trace of what the FSM layer actually does while you play.
    ///
    /// The static dump tells you what graphs exist. This tells you which of
    /// their events and states fire when you drop a coin — i.e. exactly which
    /// handful of the thousands of names are worth replicating.
    /// </summary>
    internal static class EventTracer
    {
        private static bool _enabled;
        private static StreamWriter _writer;
        private static readonly object _lock = new object();
        private static float _lastFlush;
        private static int _lines;

        // Suppress a repeat of the same (fsm, event) inside one frame — PlayMaker
        // fans a single logical event out to many receivers.
        private static readonly HashSet<string> _seenThisFrame = new HashSet<string>();
        private static int _frameOfSeen = -1;

        public static bool Enabled { get { return _enabled; } }
        public static int LineCount { get { return _lines; } }
        public static string CurrentFile { get; private set; }

        public static void ApplyPatches(HarmonyLib.Harmony harmony)
        {
            var processEvent = AccessTools.Method(typeof(Fsm), "ProcessEvent",
                new[] { typeof(FsmEvent), typeof(FsmEventData) });
            if (processEvent != null)
            {
                harmony.Patch(processEvent, prefix: new HarmonyMethod(
                    typeof(EventTracer).GetMethod(nameof(OnProcessEvent), BindingFlags.NonPublic | BindingFlags.Static)));
            }
            else
            {
                Plugin.Warn("Fsm.ProcessEvent not found — event tracing disabled. PlayMaker version changed?");
            }

            var onEnter = AccessTools.Method(typeof(FsmState), "OnEnter", Type.EmptyTypes);
            if (onEnter != null)
            {
                harmony.Patch(onEnter, prefix: new HarmonyMethod(
                    typeof(EventTracer).GetMethod(nameof(OnStateEnter), BindingFlags.NonPublic | BindingFlags.Static)));
            }
            else
            {
                Plugin.Warn("FsmState.OnEnter not found — state tracing disabled.");
            }
        }

        public static void Toggle()
        {
            if (_enabled) Stop(); else Start();
        }

        public static void Start()
        {
            lock (_lock)
            {
                if (_enabled) return;
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                CurrentFile = Path.Combine(Paths.OutputDir, "trace_" + stamp + ".log");
                _writer = new StreamWriter(CurrentFile, false, new UTF8Encoding(false));
                _writer.WriteLine("# TcgFsmDump trace  " + DateTime.Now.ToString("u", CultureInfo.InvariantCulture));
                _writer.WriteLine("# columns: time  frame  kind  fsmPath|fsmName  detail");
                _lines = 0;
                _enabled = true;
            }
            Plugin.Log("Trace ON  ->  " + CurrentFile);
        }

        public static void Stop()
        {
            lock (_lock)
            {
                if (!_enabled) return;
                _enabled = false;
                if (_writer != null)
                {
                    _writer.WriteLine("# " + _lines + " lines");
                    _writer.Flush();
                    _writer.Dispose();
                    _writer = null;
                }
            }
            Plugin.Log("Trace OFF (" + _lines + " lines)  ->  " + CurrentFile);
        }

        public static void Tick()
        {
            if (!_enabled) return;
            if (Time.realtimeSinceStartup - _lastFlush < 2f) return;
            _lastFlush = Time.realtimeSinceStartup;
            lock (_lock) { if (_writer != null) _writer.Flush(); }
        }

        // ---------------- Harmony hooks ----------------

        private static void OnProcessEvent(Fsm __instance, FsmEvent fsmEvent)
        {
            if (!_enabled || __instance == null || fsmEvent == null) return;
            try
            {
                if (fsmEvent.IsSystemEvent && !Plugin.TraceSystemEvents) return;

                var name = fsmEvent.Name;
                if (string.IsNullOrEmpty(name)) return;
                if (Plugin.IsEventMuted(name)) return;

                var owner = Owner(__instance);
                var key = owner + "" + __instance.Name + "" + name;
                if (!FirstThisFrame(key)) return;

                Write("EVENT", owner + "|" + __instance.Name, name + "   [state: " + ActiveState(__instance) + "]");
            }
            catch { }
        }

        private static void OnStateEnter(FsmState __instance)
        {
            if (!_enabled || __instance == null) return;
            if (!Plugin.TraceStates) return;
            try
            {
                var fsm = __instance.Fsm;
                if (fsm == null) return;
                var owner = Owner(fsm);
                Write("STATE", owner + "|" + fsm.Name, "-> " + __instance.Name);
            }
            catch { }
        }

        // ---------------- plumbing ----------------

        private static bool FirstThisFrame(string key)
        {
            int f = Time.frameCount;
            if (f != _frameOfSeen) { _seenThisFrame.Clear(); _frameOfSeen = f; }
            return _seenThisFrame.Add(key);
        }

        private static string Owner(Fsm fsm)
        {
            try
            {
                var go = fsm.GameObject;
                return go != null ? SceneId.Path(go.transform) : "<no gameobject>";
            }
            catch { return "<unknown>"; }
        }

        private static string ActiveState(Fsm fsm)
        {
            try { return fsm.ActiveState != null ? fsm.ActiveState.Name : "-"; }
            catch { return "-"; }
        }

        private static void Write(string kind, string who, string detail)
        {
            lock (_lock)
            {
                if (_writer == null) return;
                _writer.Write(Time.realtimeSinceStartup.ToString("0.000", CultureInfo.InvariantCulture));
                _writer.Write('\t');
                _writer.Write(Time.frameCount.ToString(CultureInfo.InvariantCulture));
                _writer.Write('\t');
                _writer.Write(kind);
                _writer.Write('\t');
                _writer.Write(who);
                _writer.Write('\t');
                _writer.WriteLine(detail);
                _lines++;
            }
        }
    }
}
