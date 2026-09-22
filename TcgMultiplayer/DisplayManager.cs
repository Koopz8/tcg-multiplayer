using System;
using System.Collections.Generic;
using UnityEngine;

namespace TcgMultiplayer
{
    /// <summary>
    /// Moves the game window between monitors, which the base game has no
    /// setting for.
    ///
    /// The Unity side only. Every decision about which monitor to use lives in
    /// DisplayChoice, where it can be tested.
    ///
    /// The escape hatch matters more than the feature: if the window ends up on
    /// a monitor you cannot see, you cannot read the panel to fix it. So the
    /// cycle key works blind — press it until the game appears — and nothing is
    /// ever moved on startup unless you explicitly asked for it.
    /// </summary>
    internal static class DisplayManager
    {
        private static readonly List<DisplayInfo> _layout = new List<DisplayInfo>();
        private static readonly List<DisplaySpec> _specs = new List<DisplaySpec>();

        public static string Status = "not checked yet";
        public static bool Supported { get; private set; }

        private static bool _appliedOnce;
        private static float _applyAt = -1f;

        public static IList<DisplaySpec> Displays { get { return _specs; } }
        public static int Count { get { return _specs.Count; } }

        public static void Refresh()
        {
            try
            {
                _layout.Clear();
                Screen.GetDisplayLayout(_layout);
                _specs.Clear();
                for (int i = 0; i < _layout.Count; i++)
                    _specs.Add(new DisplaySpec
                    {
                        Name = _layout[i].name,
                        Width = _layout[i].width,
                        Height = _layout[i].height,
                    });
                Supported = _specs.Count > 0;
            }
            catch (Exception ex)
            {
                Supported = false;
                Status = "this build of Unity won't report the monitors: " + ex.Message;
            }
        }

        /// <summary>Which monitor is the window on? -1 if it can't be told.</summary>
        public static int Current
        {
            get
            {
                try
                {
                    if (_specs.Count == 0) Refresh();
                    var here = Screen.mainWindowDisplayInfo;
                    return DisplayChoice.IndexOf(_specs, here.name, here.width, here.height);
                }
                catch { return -1; }
            }
        }

        public static string NameOf(int index)
        {
            if (index < 0 || index >= _specs.Count) return "?";
            var d = _specs[index];
            return (string.IsNullOrEmpty(d.Name) ? "Monitor " + (index + 1) : d.Name)
                   + "  " + d.Width + "x" + d.Height;
        }

        /// <summary>
        /// Move the window. Fullscreen is dropped to windowed for the move and
        /// put back afterwards — moving an exclusive-fullscreen window between
        /// monitors is the case drivers are worst at.
        /// </summary>
        public static bool MoveTo(int index)
        {
            try
            {
                if (_specs.Count == 0) Refresh();
                if (index < 0 || index >= _layout.Count)
                {
                    Status = "no monitor " + (index + 1) + " to move to.";
                    return false;
                }

                var target = _layout[index];

                // Top-left of the target's work area, nudged in so the title bar
                // lands on-screen rather than under a taskbar.
                var pos = new Vector2Int(target.workArea.x + 40, target.workArea.y + 40);

                // Moving the window does NOT resize the backbuffer. Land a
                // 2560x1440 window on a 1080p monitor and the game carries on
                // rendering at the old size, which looks like a smeared double
                // image of the last frame. So the move is only half the job:
                // the resolution has to follow it.
                _pendingMove = Screen.MoveMainWindowTo(target, pos);
                _pendingMode = Screen.fullScreenMode;
                _pendingWidth = target.width;
                _pendingHeight = target.height;
                _pendingIndex = index;

                Status = "moving to " + NameOf(index) + "...";
                Plugin.Log("Display: " + Status);
                return true;
            }
            catch (Exception ex)
            {
                Status = "couldn't move the window: " + ex.Message;
                Plugin.Warn("Display: " + Status);
                return false;
            }
        }

        private static AsyncOperation _pendingMove;
        private static FullScreenMode _pendingMode;
        private static int _pendingWidth, _pendingHeight, _pendingIndex = -1;

        /// <summary>
        /// Finish a move once the window has actually landed. Doing this on a
        /// timer instead was what left the display smeared: the resolution was
        /// being set while the window was still in flight.
        /// </summary>
        private static void FinishMoveIfDone()
        {
            if (_pendingMove == null) return;
            if (!_pendingMove.isDone) return;

            var op = _pendingMove;
            _pendingMove = null;

            try
            {
                if (Screen.width != _pendingWidth || Screen.height != _pendingHeight)
                    Screen.SetResolution(_pendingWidth, _pendingHeight, _pendingMode);

                Status = "moved to " + NameOf(_pendingIndex)
                       + " (" + _pendingWidth + "x" + _pendingHeight + ")";
                Plugin.Log("Display: " + Status);
            }
            catch (Exception ex)
            {
                Status = "moved, but couldn't match the resolution: " + ex.Message;
                Plugin.Warn("Display: " + Status);
            }
        }

        /// <summary>Cycle to the next monitor. Usable without being able to see the game.</summary>
        public static bool MoveToNext()
        {
            Refresh();
            int next = DisplayChoice.Next(_specs.Count, Current);
            if (next < 0)
            {
                Status = "only one monitor, nowhere to move.";
                Plugin.Log("Display: " + Status);
                return false;
            }
            return MoveTo(next);
        }

        /// <summary>Remember the monitor the window is on now.</summary>
        public static void Remember(Action<string, int> save)
        {
            Refresh();
            int cur = Current;
            if (cur < 0) { Status = "can't tell which monitor this is, so nothing was saved."; return; }
            save(_specs[cur].Name, cur);
            Status = "will open on " + NameOf(cur) + " from now on.";
            Plugin.Log("Display: " + Status);
        }

        public static void Forget(Action<string, int> save)
        {
            save("", -1);
            Status = "no longer moving the window on startup.";
            Plugin.Log("Display: " + Status);
        }

        /// <summary>
        /// Called once a few seconds after load. Late on purpose: the game sets
        /// its own resolution during startup, and moving the window before it
        /// has finished is how you end up fighting it.
        /// </summary>
        public static void ScheduleStartupApply(float delaySeconds)
        {
            _applyAt = Time.realtimeSinceStartup + delaySeconds;
        }

        public static void Tick(string savedName, int savedIndex)
        {
            FinishMoveIfDone();

            if (_appliedOnce || _applyAt < 0 || Time.realtimeSinceStartup < _applyAt) return;
            _appliedOnce = true;

            Refresh();
            if (!Supported) return;

            var plan = DisplayChoice.Resolve(_specs, savedName, savedIndex, Current);
            Status = plan.Why;
            if (!plan.ShouldMove) { Plugin.Log("Display: " + plan.Why); return; }

            Plugin.Log("Display: " + plan.Why);
            MoveTo(plan.Target);
        }
    }
}
