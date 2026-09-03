using System;
using System.Collections.Generic;
using UnityEngine;

namespace TcgMultiplayer
{
    /// <summary>
    /// Frame timing, and how much of it is our fault.
    ///
    /// The Coin Game is CPU-bound, not GPU-bound — the arcade runs some 4,500
    /// PlayMaker state machines over 160,000 loaded objects, on Unity's Mono
    /// backend. Which means the honest question about any mod running inside it
    /// is not "does it work" but "what did it cost", and the honest way to
    /// answer that is to measure rather than to reassure.
    ///
    /// Every subsystem already runs through Guard, so Guard times them on the
    /// way past. The overlay shows the total against the frame, so if the mod is
    /// costing anything it is visible, in milliseconds, while you play.
    /// </summary>
    internal static class Perf
    {
        private const int Window = 120;          // ~2 seconds at 60 fps

        private static readonly float[] _frames = new float[Window];
        private static int _cursor;
        private static bool _filled;

        private static float _worstRecent;
        private static float _worstResetAt;

        public static float LastFrameMs { get; private set; }
        public static float ModMsThisFrame { get; private set; }

        private static float _modAccum;
        private static readonly Dictionary<string, float> _cost =
            new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> _costSmoothed =
            new Dictionary<string, float>(StringComparer.Ordinal);

        /// <summary>Called by Guard for each subsystem it runs.</summary>
        public static void Record(string name, float ms)
        {
            _cost[name] = ms;
            _modAccum += ms;
        }

        /// <summary>Called once at the end of the mod's own update.</summary>
        public static void EndFrame()
        {
            ModMsThisFrame = _modAccum;
            _modAccum = 0f;

            // Exponential smoothing: raw per-frame numbers flicker too fast to read.
            foreach (var kv in _cost)
            {
                float prev;
                _costSmoothed[kv.Key] = _costSmoothed.TryGetValue(kv.Key, out prev)
                    ? prev + (kv.Value - prev) * 0.05f
                    : kv.Value;
            }
            _cost.Clear();

            float dt = Time.unscaledDeltaTime * 1000f;
            LastFrameMs = dt;

            _frames[_cursor] = dt;
            _cursor = (_cursor + 1) % Window;
            if (_cursor == 0) _filled = true;

            // A rolling worst-case, reset each second. Average frame rate hides
            // exactly the thing people mean by "not smooth"; the spikes are the
            // complaint, so the spikes are what gets shown.
            if (dt > _worstRecent) _worstRecent = dt;
            if (Time.unscaledTime - _worstResetAt > 1f)
            {
                _worstResetAt = Time.unscaledTime;
                WorstMs = _worstRecent;
                _worstRecent = 0f;
            }
        }

        public static float WorstMs { get; private set; }

        public static float AverageMs
        {
            get
            {
                int n = _filled ? Window : _cursor;
                if (n == 0) return 0f;
                float sum = 0f;
                for (int i = 0; i < n; i++) sum += _frames[i];
                return sum / n;
            }
        }

        public static float Fps
        {
            get { var a = AverageMs; return a > 0.001f ? 1000f / a : 0f; }
        }

        /// <summary>Managed heap, in MB. Climbing steadily means something is littering.</summary>
        public static float HeapMb
        {
            get
            {
                try { return GC.GetTotalMemory(false) / (1024f * 1024f); }
                catch { return 0f; }
            }
        }

        public static string Summary
        {
            get
            {
                return Fps.ToString("0") + " fps  ·  " + AverageMs.ToString("0.0") + " ms avg  ·  "
                     + "worst " + WorstMs.ToString("0.0") + " ms";
            }
        }

        public static string ModCostLine
        {
            get
            {
                return "this mod: " + ModMsThisFrame.ToString("0.00") + " ms/frame  ("
                     + (AverageMs > 0.001f ? (ModMsThisFrame / AverageMs * 100f).ToString("0.0") : "0")
                     + "% of the frame)  ·  heap " + HeapMb.ToString("0") + " MB";
            }
        }

        /// <summary>Per-subsystem breakdown, worst first. Only the ones costing anything.</summary>
        public static List<string> Breakdown
        {
            get
            {
                var list = new List<KeyValuePair<string, float>>(_costSmoothed);
                list.Sort((a, b) => b.Value.CompareTo(a.Value));
                var outp = new List<string>();
                for (int i = 0; i < list.Count && i < 6; i++)
                {
                    if (list[i].Value < 0.005f) break;
                    outp.Add("   " + list[i].Key.PadRight(16) + list[i].Value.ToString("0.00") + " ms");
                }
                return outp;
            }
        }
    }
}
