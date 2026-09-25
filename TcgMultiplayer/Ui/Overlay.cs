using System;
using System.Globalization;
using TcgMultiplayer.Game;
using TcgMultiplayer.Net;
using UnityEngine;

namespace TcgMultiplayer.Ui
{
    /// <summary>
    /// A deliberately plain IMGUI panel. M1 has no gameplay to show, so the
    /// overlay IS the deliverable: it has to make "are we actually connected,
    /// and how fast" answerable at a glance.
    /// </summary>
    internal sealed class Overlay
    {
        private const int WinId = 0x7C6;

        private readonly Session _s;
        private readonly AvatarDirector _av;
        private readonly MachineDirector _mc;
        private readonly WorldState _world;
        private Rect _rect = new Rect(24, 24, 460, 560);
        private Vector2 _scroll;
        // The body scrolls. Without this the panel simply clipped at 430px and
        // everything past "Shared island" — self-test, performance, monitor,
        // health — was unreachable, which is why the monitor controls looked
        // like they had not been added at all.
        private Vector2 _bodyScroll;
        private string _chatDraft = "";
        private string _joinDraft = "";
        private bool _showJoinField;

        private GUIStyle _mono, _head, _dim, _alert, _win, _btn;
        private Texture2D _bgTex;
        private bool _stylesReady;
        private bool _showAdvanced;
        // Performance and health are for when something is wrong. Folded away
        // by default so the panel opens on the four things you actually use.
        private bool _showDiagnostics;

        public bool Visible;

        /// <summary>
        /// True while the caret is in the chat or lobby-ID box.
        ///
        /// This is what game input is suppressed for — and only this. Suppressing
        /// it for the whole time the panel is open disables every Rewired map at
        /// once, which stops the player moving, stops the game's own menus
        /// responding, and reads for all the world like the game has frozen.
        /// Updated during Draw, because focus is only knowable inside OnGUI.
        /// </summary>
        public bool TypingInABox { get; private set; }

        public Overlay(Session s, AvatarDirector av, MachineDirector mc, WorldState world)
        { _s = s; _av = av; _mc = mc; _world = world; }

        public void Draw()
        {
            if (!Visible) { TypingInABox = false; return; }
            EnsureStyles();
            var focused = GUI.GetNameOfFocusedControl();
            TypingInABox = focused == "chatField" || focused == "joinField";
            ClampToScreen();
            _rect = GUI.Window(WinId, _rect, DrawWindow,
                               "TcgMultiplayer " + Plugin.Version + "  ·  built " + Plugin.BuildStamp,
                               _win);
        }

        private void EnsureStyles()
        {
            if (_stylesReady) return;

            // The default IMGUI window is translucent grey, which over a neon
            // arcade floor is close to unreadable. One flat dark pixel, stretched.
            _bgTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _bgTex.SetPixel(0, 0, new Color(0.08f, 0.08f, 0.10f, 0.97f));
            _bgTex.Apply();
            _bgTex.hideFlags = HideFlags.HideAndDontSave;

            var brass = new Color(1f, 0.86f, 0.55f);

            _win = new GUIStyle(GUI.skin.window);
            _win.normal.background = _bgTex;
            _win.onNormal.background = _bgTex;
            _win.normal.textColor = brass;
            _win.onNormal.textColor = brass;
            // Border and padding are left exactly as the skin has them. Zeroing
            // the border is what drew the title straight through the first line
            // of content — the title is painted inside the border region.
            _win.fontSize = 12;

            // Every text style wraps. Anything that doesn't wrap runs off the
            // right edge and drags a horizontal scrollbar in with it.
            _mono = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true, richText = false };
            _head = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true };
            _head.normal.textColor = brass;
            _head.margin = new RectOffset(0, 0, 4, 2);
            _dim = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true };
            _dim.normal.textColor = new Color(0.72f, 0.72f, 0.70f);
            _alert = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
            _alert.normal.textColor = new Color(1f, 0.55f, 0.45f);

            _btn = new GUIStyle(GUI.skin.button) { fontSize = 12, wordWrap = false };
            _btn.padding = new RectOffset(8, 8, 4, 4);

            _stylesReady = true;
        }

        /// <summary>
        /// Keep the panel on the screen and no taller than it. Moving the game
        /// between monitors changes the resolution under us, so a size that was
        /// fine a second ago can be taller than the display now.
        /// </summary>
        /// <summary>
        /// The size the player actually asked for, by dragging the grip.
        ///
        /// Kept apart from the drawn size because the drawn size gets clamped to
        /// whatever the screen is right now, and "right now" briefly includes
        /// being minimised. Minimising reports a screen a few pixels tall, the
        /// old code squashed the panel to its 360x260 floor to fit, and since
        /// that clamp could only ever shrink, restoring the window left the
        /// panel squashed forever — everything crammed into a strip with
        /// scrollbars through the middle of it. Clamping a remembered size
        /// instead of the live one means the panel comes back.
        /// </summary>
        private float _wantW = 460f, _wantH = 560f;

        private void ClampToScreen()
        {
            // Minimised, or mid-resolution-change. Anything computed from this
            // is a number to throw away, not to save.
            if (Screen.width < 200 || Screen.height < 200) return;

            float maxH = Mathf.Max(260f, Screen.height - 60f);
            float maxW = Mathf.Max(360f, Screen.width - 60f);

            _rect.width = Mathf.Min(_wantW, maxW);
            _rect.height = Mathf.Min(_wantH, maxH);
            _rect.x = Mathf.Clamp(_rect.x, 0f, Mathf.Max(0f, Screen.width - _rect.width));
            _rect.y = Mathf.Clamp(_rect.y, 0f, Mathf.Max(0f, Screen.height - _rect.height));
        }

        private const float TitleH = 22f;
        private const float Pad = 10f;

        /// <summary>
        /// GUILayout inside a GUI.Window grows the window to fit its widest
        /// child. One long unwrapped line was enough to stretch this panel to
        /// most of the screen — and once the window is bigger than the content,
        /// the scroll view never scrolls and the bottom falls off the display.
        ///
        /// Laying the body out inside a fixed Rect breaks that feedback loop:
        /// the window size is decided here, and the content has to live in it.
        /// </summary>
        private void DrawWindow(int id)
        {
            var body = new Rect(Pad, TitleH,
                                Mathf.Max(120f, _rect.width - Pad * 2f),
                                Mathf.Max(60f, _rect.height - TitleH - Pad));

            GUILayout.BeginArea(body);
            DrawBody();
            GUILayout.EndArea();

            ResizeGrip();
            GUI.DragWindow(new Rect(0, 0, 10000, TitleH));
        }

        private bool _resizing;

        /// <summary>Bottom-right corner drags the panel bigger or smaller.</summary>
        private void ResizeGrip()
        {
            var grip = new Rect(_rect.width - 18f, _rect.height - 18f, 16f, 16f);
            GUI.Label(grip, "◢", _dim);

            var e = Event.current;
            if (e == null) return;

            if (e.type == EventType.MouseDown && grip.Contains(e.mousePosition))
            {
                _resizing = true;
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && _resizing)
            {
                // Drag sets the WANTED size. The drawn size follows from it once
                // the screen has been taken into account.
                _wantW = Mathf.Max(340f, _rect.width + e.delta.x);
                _wantH = Mathf.Max(220f, _rect.height + e.delta.y);
                _rect.width = _wantW;
                _rect.height = _wantH;
                e.Use();
            }
            else if (e.type == EventType.MouseUp && _resizing)
            {
                _resizing = false;
                e.Use();
            }
        }

        private void DrawBody()
        {

            // One line, at the top, saying what is actually true right now.
            // Without it, opening this at the main menu shows a wall of red
            // MISSING lines — every check that can only pass once a save is
            // loaded, failing exactly as designed, and reading to anyone sane
            // as "this mod is broken".
            var dx = Health.Now(_s, _mc, _av);
            GUILayout.Label(dx.Headline, dx.IsBad ? _alert : _head);
            if (!string.IsNullOrEmpty(dx.NextStep)) GUILayout.Label(dx.NextStep, _dim);

            // "I can't move" is the single most confusing thing this mod can do
            // to someone, because there are three unrelated reasons for it and
            // none of them announce themselves. Say which one it is.
            var held = WhyCantIMove();
            if (held != null)
            {
                GUILayout.Label("You can't move: " + held, _alert);
                if (GUILayout.Button("Unstick me (same as F11)", _btn, GUILayout.Height(22)))
                {
                    _mc.ReleaseEverything();
                    InputLock.Set(false);
                    InputLock.Tick();
                }
            }

            GUILayout.Space(6);

            if (!_s.Ready) return;

            // ---- anything actually wrong, first and unmissable ------------
            // These used to be console warnings, which on a public release means
            // nobody ever saw them. If something is going to ruin the session,
            // it belongs at the top of the panel in words the player can act on.
            if (!string.IsNullOrEmpty(_s.RefusedReason))
            {
                GUILayout.Label("COULD NOT JOIN", _head);
                GUILayout.Label(_s.RefusedReason, _alert);
                GUILayout.Space(6);
            }
            if (!string.IsNullOrEmpty(_s.BuildMismatch))
            {
                GUILayout.Label(_s.BuildMismatch, _alert);
                GUILayout.Space(4);
            }
            if (Guard.AnythingBroken)
            {
                GUILayout.Label("Some of the mod switched itself off after repeated errors:", _alert);
                foreach (var b in Guard.Broken) GUILayout.Label("   " + b, _dim);
                if (GUILayout.Button("Try those again", _btn, GUILayout.Height(20))) Guard.ResetAll();
                GUILayout.Label("Send MelonLoader\\Latest.log with a bug report — it has the detail.", _dim);
                GUILayout.Space(6);
            }

            // Vertical only. A horizontal scrollbar here means something
            // isn't wrapping, and the fix is the wrapping, not the bar.
            _bodyScroll = GUILayout.BeginScrollView(_bodyScroll, false, true,
                                                   GUIStyle.none, GUI.skin.verticalScrollbar);

            GUILayout.Label("You: " + _s.SelfName + "   (" + _s.SelfId.m_SteamID + ")", _dim);
            GUILayout.Label("Session: " + StateLine(), _head);
            GUILayout.Space(4);

            // ---- controls -------------------------------------------------
            GUILayout.BeginHorizontal();
            if (_s.State == SessionState.Offline)
            {
                // In local test mode there is exactly one lobby and its id is a
                // constant, so making someone read a 17-digit number off one
                // window and type it into the other would be a nonsense.
                if (Net.LocalTest.Active && !Net.LocalTest.IsHostWindow)
                {
                    if (GUILayout.Button("Join window 1", _btn, GUILayout.Height(24)))
                        _s.Join(Net.LanLobbyBackend.TheLobby);
                }
                else
                {
                    if (GUILayout.Button("Host", _btn, GUILayout.Height(24))) _s.Host(Plugin.MaxPlayers);
                }

                if (GUILayout.Button(_showJoinField ? "Cancel join" : "Join by lobby ID", _btn, GUILayout.Height(24)))
                    _showJoinField = !_showJoinField;
            }
            else
            {
                if (GUILayout.Button("Invite friend", _btn, GUILayout.Height(24))) _s.OpenInviteOverlay();
                if (GUILayout.Button("Leave", _btn, GUILayout.Height(24))) _s.Leave();
            }
            GUILayout.EndHorizontal();

            if (_s.State == SessionState.Offline && _showJoinField)
            {
                GUILayout.BeginHorizontal();
                GUI.SetNextControlName("joinField");
                _joinDraft = GUILayout.TextField(_joinDraft ?? "", GUILayout.Height(22));
                if (GUILayout.Button("Go", _btn, GUILayout.Width(44), GUILayout.Height(22)))
                {
                    ulong lobbyId;
                    if (ulong.TryParse((_joinDraft ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out lobbyId))
                        _s.Join(new Steamworks.CSteamID(lobbyId));
                }
                GUILayout.EndHorizontal();
                GUILayout.Label("Paste a lobby ID, or just use Host + Invite friend.", _dim);
            }

            // ---- local test mode --------------------------------------------
            if (Net.LocalTest.Active)
            {
                GUILayout.Space(6);
                GUILayout.Label("Local test mode", _head);
                GUILayout.Label(Net.LocalTest.Status, _mono);
                GUILayout.Label(Net.LocalTest.IsHostWindow
                    ? "This is window 1. Host here, then open a second copy of the game and Join in it."
                    : "This is a test window. It will NOT write to your save.", _dim);
                if (Net.LocalTest.Transport != null)
                    GUILayout.Label("loopback: " + Net.LocalTest.Transport.PacketsOut + " out, "
                                  + Net.LocalTest.Transport.PacketsIn + " in"
                                  + (Net.LocalTest.Transport.Malformed > 0
                                     ? "  ·  " + Net.LocalTest.Transport.Malformed + " malformed" : ""), _dim);
            }
            else if (!string.IsNullOrEmpty(Net.LocalTest.Refusal))
            {
                GUILayout.Space(6);
                GUILayout.Label("Local test mode", _head);
                GUILayout.Label("Did not start: " + Net.LocalTest.Refusal, _dim);
            }

            // ---- monitor ----------------------------------------------------
            GUILayout.Space(6);
            GUILayout.Label("Monitor", _head);
            if (!DisplayManager.Supported)
            {
                if (GUILayout.Button("Look for monitors", _btn, GUILayout.Height(20)))
                    DisplayManager.Refresh();
                GUILayout.Label(DisplayManager.Status, _dim);
            }
            else
            {
                int here = DisplayManager.Current;
                for (int i = 0; i < DisplayManager.Count; i++)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label((i == here ? "> " : "   ") + DisplayManager.NameOf(i),
                                    i == here ? _mono : _dim);
                    GUILayout.FlexibleSpace();
                    GUI.enabled = i != here;
                    if (GUILayout.Button("Move here", _btn, GUILayout.Width(88), GUILayout.Height(20)))
                        DisplayManager.MoveTo(i);
                    GUI.enabled = true;
                    GUILayout.EndHorizontal();
                }

                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Remember this one", _btn, GUILayout.Height(22)))
                    Plugin.RememberMonitor();
                if (GUILayout.Button("Forget", _btn, GUILayout.Width(70), GUILayout.Height(22)))
                    Plugin.ForgetMonitor();
                GUILayout.EndHorizontal();

                GUILayout.Label("Now running at " + Screen.width + "x" + Screen.height
                              + "  ·  " + DisplayManager.Describe(Screen.fullScreenMode), _mono);

                GUILayout.BeginHorizontal();
                DrawModeButton("Fullscreen", FullScreenMode.ExclusiveFullScreen);
                DrawModeButton("Borderless", FullScreenMode.FullScreenWindow);
                DrawModeButton("Windowed", FullScreenMode.Windowed);
                GUILayout.EndHorizontal();

                if (GUILayout.Button("Match this monitor's resolution", _btn, GUILayout.Height(22)))
                    DisplayManager.MatchMonitor();

                GUILayout.Label(DisplayManager.Status, _dim);
                GUILayout.Label(Plugin.NextMonitorKeyName + " cycles monitors — works even when you can't "
                              + "see the game.", _dim);
            }

            // ---- peers ----------------------------------------------------
            GUILayout.Space(6);
            GUILayout.Label("Peers (" + _s.Peers.Count + ")", _head);
            if (_s.Peers.Count == 0)
            {
                GUILayout.Label(_s.State == SessionState.InLobby
                    ? "Nobody else here yet. Use Invite friend."
                    : "Not in a session.", _dim);
            }
            else
            {
                foreach (var p in _s.Peers)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(p.Name, _mono, GUILayout.Width(150));
                    GUILayout.Label(p.Handshaked ? "ready" : "handshaking", _dim, GUILayout.Width(74));
                    GUILayout.Label(p.RttMs >= 0 ? p.RttMs.ToString("0") + " ms" : "-", _mono, GUILayout.Width(54));
                    GUILayout.Label(p.HasWallet
                        ? ("$" + (p.Coins / 100f).ToString("0.00") + "  " + p.Tickets + "t")
                        : "-", _mono, GUILayout.Width(110));
                    GUILayout.Label(Net.SteamTransport.ConnectionState(p.Id), _dim);
                    GUILayout.EndHorizontal();
                }
            }

            // ---- avatars --------------------------------------------------
            GUILayout.Space(6);
            GUILayout.Label("Avatars", _head);
            GUILayout.Label(_av.DebugLine, _dim);

            if (_showAdvanced)
            {
                GUILayout.BeginHorizontal();
                bool mirror = GUILayout.Toggle(_av.MirrorEnabled, "  Mirror me (solo test)", GUILayout.Width(190));
                if (mirror != _av.MirrorEnabled) _av.MirrorEnabled = mirror;
                GUILayout.Label("delay", _dim, GUILayout.Width(38));
                _av.MirrorDelay = Mathf.Round(GUILayout.HorizontalSlider(_av.MirrorDelay, 0.25f, 5f) * 4f) / 4f;
                GUILayout.Label(_av.MirrorDelay.ToString("0.00") + "s", _mono, GUILayout.Width(48));
                GUILayout.EndHorizontal();
                GUILayout.Label("Mirror replays your own movement through the real wire format, so the "
                              + "avatar path can be tested without a second copy of the game.", _dim);
            }

            // ---- machines -------------------------------------------------
            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Machines (" + _mc.MachineCount + ")", _head);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Rescan", _btn, GUILayout.Width(64), GUILayout.Height(20))) _mc.RebuildNow();
            GUILayout.EndHorizontal();

            if (_mc.MachineCount == 0)
            {
                GUILayout.Label("None found yet — machines are registered a few seconds after a scene loads.", _dim);
            }
            else
            {
                int shown = 0;
                foreach (var m in _mc.Machines)
                {
                    if (m.Owner == 0) continue;      // only list what's occupied
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(m.Label, _mono, GUILayout.Width(200));
                    GUILayout.Label(m.OwnedByMe ? "yours" : ("in use by " + (m.OwnerName ?? "?"))
                        + (m.Frozen ? " · frozen" : ""), _dim);
                    GUILayout.EndHorizontal();
                    if (++shown >= 6) break;
                }
                if (shown == 0) GUILayout.Label("All free.", _dim);
                GUILayout.Label("mirrored events: " + _mc.MirroredEventsSent + " sent, "
                              + _mc.MirroredEventsApplied + " applied", _dim);
                GUILayout.Label("physics: " + _mc.Physics.BodiesSent + " bodies sent, "
                              + _mc.Physics.BodiesApplied + " applied, "
                              + _mc.Physics.LastPacketBytes.ToString("0") + " B/frame"
                              + (_mc.Physics.SpectatedMachines > 0
                                 ? "  ·  spectating " + _mc.Physics.SpectatedMachines : "")
                              + (_mc.Physics.CountMismatches > 0
                                 ? "  ·  " + _mc.Physics.CountMismatches + " count mismatches" : ""), _dim);
                if (_mc.Physics.PlacedAlreadyOnScreen + _mc.Physics.PlacedSwitchedOn + _mc.Physics.PlacedNothingToDraw > 0)
                    GUILayout.Label("of what we're placing: " + _mc.Physics.PlacedAlreadyOnScreen
                                  + " were already on screen, " + _mc.Physics.PlacedSwitchedOn
                                  + " were switched off and we turned on"
                                  + (_mc.Physics.PlacedNothingToDraw > 0
                                     ? ", " + _mc.Physics.PlacedNothingToDraw + " have nothing to draw" : ""), _dim);
                if (_mc.Physics.PacketsSent > 0)
                    GUILayout.Label("streaming out: " + _mc.Physics.SentMovedLastPacket + " of "
                                  + _mc.Physics.SentBodiesLastPacket + " bodies moved last packet (peak "
                                  + _mc.Physics.SentMovedPeak + ", " + _mc.Physics.PacketsSent + " packets)", _dim);
                if (_mc.Screen.Sent + _mc.Screen.Applied > 0)
                    GUILayout.Label("screen: " + _mc.Screen.FieldsFound + " text fields, " + _mc.Screen.Sent + " sent, "
                                  + _mc.Screen.Applied + " applied"
                                  + (_mc.Screen.Unmatched > 0 ? ", " + _mc.Screen.Unmatched + " with nowhere to go" : ""), _dim);
                if (_mc.Physics.PacketsReceived > 0)
                    GUILayout.Label("streaming in: " + _mc.Physics.RecvChangedLastPacket + " of "
                                  + _mc.Physics.RecvPosesLastPacket + " poses changed last packet (peak "
                                  + _mc.Physics.RecvChangedPeak + ", " + _mc.Physics.PacketsReceived + " packets"
                                  + (_mc.Physics.WaitingForManifest > 0 ? ", " + _mc.Physics.WaitingForManifest + " before a manifest" : "") + ")", _dim);
            }

            // ---- vehicles -------------------------------------------------
            DrawVehicles();

            // ---- wallet ---------------------------------------------------
            GUILayout.Space(6);
            GUILayout.Label("Your wallet", _head);
            if (!_mc.Wallet.Available)
            {
                GUILayout.Label("economy globals not readable yet (load a save first)", _dim);
            }
            else
            {
                GUILayout.Label("$" + (_mc.Wallet.Coins / 100f).ToString("0.00")
                              + "   " + _mc.Wallet.Tickets + " tickets"
                              + "   (" + _mc.Wallet.TicketsThisSession + " this session)", _mono);
                GUILayout.Label(_mc.Wallet.ProtectedCount + " economy globals protected"
                              + (_mc.Wallet.RestoreCount > 0
                                 ? "  ·  " + _mc.Wallet.RestoreCount + " payouts blocked from spectating"
                                 : ""), _dim);
            }

            // ---- shared island --------------------------------------------
            GUILayout.Space(6);
            GUILayout.Label("Shared island", _head);
            GUILayout.Label(_world.DebugLine, _dim);
            GUILayout.Label("Unlocks, doors and vehicles are shared and host-arbitrated. "
                          + "Money, tickets, prizes and inventory stay yours.", _dim);

            if (_world.VisitPending)
            {
                GUILayout.Label("Waiting for your save to load before anything from the host is "
                              + "applied. Your progression is safe until then.", _mono);
            }
            else if (_world.Visiting)
            {
                GUILayout.Label("You're visiting. " + _world.StashedCount + " of your own unlocks are "
                              + "held aside and come back when you leave"
                              + (_world.EarnedWhileVisiting > 0
                                 ? ", plus the " + _world.EarnedWhileVisiting + " you've unlocked here"
                                 : "") + ".", _mono);
            }
            else if (!_world.ProtectGuestProgression)
            {
                GUILayout.Label("Guest protection is OFF — a host's unlocks will follow you home.", _alert);
            }

            // ---- your save -------------------------------------------------
            GUILayout.Space(6);
            GUILayout.Label("Your save", _head);
            GUILayout.Label("Backup: " + SaveGuard.Status, _dim);
            if (GUILayout.Button("Back up my save now", _btn, GUILayout.Height(22))) SaveGuard.Backup();

            // ---- session report ---------------------------------------------
            GUILayout.Space(6);
            GUILayout.Label("Last session report", _head);
            GUILayout.Label(SessionReport.Status, _dim);
            GUILayout.BeginHorizontal();
            GUI.enabled = !string.IsNullOrEmpty(SessionReport.Last);
            if (GUILayout.Button("Copy report", _btn, GUILayout.Height(22)))
            {
                try { GUIUtility.systemCopyBuffer = SessionReport.Last; }
                catch (Exception ex) { Plugin.Warn("Copy failed: " + ex.Message); }
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            // ---- self-test --------------------------------------------------
            GUILayout.Space(6);
            GUILayout.Label("Self-test", _head);
            GUILayout.Label(SelfTest.Summary, _mono);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Run checks", _btn, GUILayout.Height(22)))
            {
                SelfTest.LastDiagnosis = Health.Now(_s, _mc, _av);
                SelfTest.RunAll(_s, _mc, _world);
            }
            GUI.enabled = SelfTest.HasRun;
            if (GUILayout.Button("Copy result", _btn, GUILayout.Height(22)))
            {
                try { GUIUtility.systemCopyBuffer = SelfTest.Report(); }
                catch (Exception ex) { Plugin.Warn("Copy failed: " + ex.Message); }
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (SelfTest.HasRun)
            {
                foreach (var r in SelfTest.Results)
                {
                    var tag = r.Skipped ? "skip" : r.Ok ? " ok " : "FAIL";
                    GUILayout.Label("[" + tag + "] " + r.Name
                                  + (string.IsNullOrEmpty(r.Detail) ? "" : " — " + r.Detail),
                                  r.Ok || r.Skipped ? _dim : _alert);
                }
                GUILayout.Label("Doesn't test Steam delivery or two people on one machine — "
                              + "only a real session does that.", _dim);
            }
            else
            {
                GUILayout.Label("Checks the wire format, hostile packets, machine ids, the wallet "
                              + "guard, the save backup, and that visiting gives your own "
                              + "progression back. Takes a moment. Load a save first.", _dim);
            }

            // ---- diagnostics (folded) ---------------------------------------
            GUILayout.Space(6);
            _showDiagnostics = GUILayout.Toggle(_showDiagnostics,
                "  Diagnostics — frame times, what the mod could and couldn't find");
            if (_showDiagnostics)
            {
                // ---- performance ------------------------------------------------
                GUILayout.Space(6);
                GUILayout.Label("Performance", _head);
                GUILayout.Label(Perf.Summary, _mono);
                GUILayout.Label(Perf.ModCostLine, _dim);
                foreach (var b in Perf.Breakdown) GUILayout.Label(b, _dim);
                if (Perf.WorstMs > 100f)
                    GUILayout.Label("Worst frame over 100 ms — that's a visible hitch. If the breakdown "
                                  + "above is near zero, it isn't this mod.", _dim);

                // ---- health ---------------------------------------------------
                GUILayout.Space(6);
                GUILayout.Label("Health", _head);
                GUILayout.Label("build " + (CompatCheck.GameHash ?? "?") + "  ·  " + CompatCheck.Summary, _mono);
                // The per-item list is only meaningful once there is a save to bind
                // against. Before that every line is a false alarm.
                if (dx.Verdict == Verdict.Waiting)
                {
                    GUILayout.Label("The list below fills in once a save is loaded.", _dim);
                }
                else
                {
                    foreach (var c in CompatCheck.Items)
                        if (!c.Ok) GUILayout.Label("   MISSING: " + c.What
                                                   + (string.IsNullOrEmpty(c.Detail) ? "" : " — " + c.Detail), _dim);
                }
                GUILayout.Label("Steam stats: " + StatsLock.Status, _dim);
                GUILayout.Label("Cursor: " + CursorGuard.Status, _dim);
            }

            // ---- solo test harness ----------------------------------------
            // Folded away by default. It's genuinely useful — it's how most of
            // this was built without a second copy of the game — but a player
            // who just installed a co-op mod should not be met with a button
            // marked "Fake: friend takes it".
            GUILayout.Space(6);
            _showAdvanced = GUILayout.Toggle(_showAdvanced, "  Testing tools (no second player needed)");
            if (_showAdvanced)
            {
            GUILayout.Label("Ownership, spectating and the wallet guard only fire when someone "
                          + "else plays. These stand in for that friend.", _dim);

            var near = _mc.Nearest(PlayerPos(), 12f);
            GUILayout.Label(near != null ? "nearest: " + near.Label : "no machine within 12m", _mono);

            GUILayout.BeginHorizontal();
            if (_mc.Rehearse.Recording)
            {
                if (GUILayout.Button("Stop recording (" + _mc.Rehearse.TapeLength + ")", GUILayout.Height(22)))
                    _mc.Rehearse.StopRecording();
            }
            else
            {
                GUI.enabled = near != null;
                if (GUILayout.Button("Record a round", _btn, GUILayout.Height(22)) && near != null)
                    _mc.Rehearse.StartRecording(near.Id, near.Label);
                GUI.enabled = true;
            }

            GUI.enabled = _mc.Rehearse.HasTape && !_mc.Rehearse.Playing && !_mc.Rehearse.Recording;
            if (GUILayout.Button("Replay as a friend", _btn, GUILayout.Height(22)))
            {
                Machine tape;
                if (_mc.TryGetMachine(_mc.Rehearse.RecordedMachine, out tape)) _mc.Rehearse.Play(tape);
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (_mc.Rehearse.HasTape)
            {
                GUILayout.Label(_mc.Rehearse.TapeLength + " events + " + _mc.Rehearse.FilmLength
                    + " physics frames from " + _mc.Rehearse.RecordedLabel
                    + (_mc.Rehearse.Playing ? "  ·  replaying…" : "")
                    + (!_mc.Rehearse.Playing && _mc.Rehearse.WalletMovedDuringPlayback == 0 && _mc.Rehearse.TapeLength > 0
                        ? "  ·  last replay: wallet held" : "")
                    + (_mc.Rehearse.WalletMovedDuringPlayback > 0
                        ? "  ·  GUARD LEAKED " + _mc.Rehearse.WalletMovedDuringPlayback + "x" : ""),
                    _mono);
            }

            GUILayout.BeginHorizontal();
            GUI.enabled = near != null;
            if (GUILayout.Button("Fake: friend takes it", _btn, GUILayout.Height(22))) _mc.SimulateRemoteClaim(near);
            if (GUILayout.Button("Fake: friend leaves", _btn, GUILayout.Height(22))) _mc.SimulateRemoteRelease(near);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Label("\"Friend takes it\" should make the cabinet refuse your card. It will "
                          + "never freeze a machine you are standing in.", _dim);

            if (GUILayout.Button("Release everything (stuck in a machine?)", _btn, GUILayout.Height(22)))
                _mc.ReleaseEverything();
            }

            // The scroll closes here, so the log and the chat box stay pinned to
            // the bottom of the panel instead of scrolling away with everything
            // else. It also means no path can return past an unclosed scroll
            // view, which is what the old early-return did once this was added.
            GUILayout.EndScrollView();

            GUILayout.Label(Plugin.ToggleKeyName + " closes this. " + InputLock.Status
                          + (TypingInABox ? " — typing, so the game isn't listening" : "") + ".", _dim);
            DrawChat();
        }

        /// <summary>
        /// Vehicles, and getting into one someone else is driving.
        ///
        /// Which things count as vehicles is worked out by watching them move,
        /// so this list fills in as you walk past them rather than being there
        /// from the start. That is worth saying on screen — an empty list looks
        /// broken otherwise.
        /// </summary>
        private void DrawVehicles()
        {
            GUILayout.Space(6);
            GUILayout.Label("Vehicles and rides (" + _mc.Movers.Movers + ")", _head);

            if (_mc.Movers.Movers == 0)
            {
                GUILayout.Label("Nothing has been seen moving yet. Anything that travels — the cart, "
                              + "the cars, the kart, the bus — is spotted the first time it does.", _dim);
            }

            if (_mc.Ride.Active)
            {
                Machine riding;
                var label = _mc.TryGetMachine(_mc.Ride.Riding, out riding) ? riding.Label : "something";
                GUILayout.Label("You are riding in " + label + ", seat " + (_mc.Ride.Seat + 1) + ".", _mono);
                if (GUILayout.Button("Get out", _btn, GUILayout.Height(22))) _mc.LeaveRide();
            }
            else
            {
                var near = _mc.Nearest(PlayerPos(), 8f);
                string why;
                bool can = _mc.CanRide(near, out why);

                GUI.enabled = can;
                if (GUILayout.Button(can ? "Ride along in " + near.Label : "Ride along",
                                     _btn, GUILayout.Height(22)) && can)
                    _mc.RequestRide(near);
                GUI.enabled = true;

                if (!can && !string.IsNullOrEmpty(why)) GUILayout.Label(why, _dim);
            }

            if (!string.IsNullOrEmpty(_mc.Ride.Status)) GUILayout.Label(_mc.Ride.Status, _dim);

            // Who's aboard what. Only listed while someone actually is — four
            // empty vehicles is noise.
            foreach (var m in _mc.Machines)
            {
                if (!m.IsMover || Seating.Used(m.Seats) == 0) continue;
                GUILayout.BeginHorizontal();
                GUILayout.Label(m.Label, _mono, GUILayout.Width(200));
                GUILayout.Label(Seating.Used(m.Seats) + " of " + m.Capacity + " aboard"
                              + (_mc.Movers.IsSpectating(m.Id) ? " · following" : ""), _dim);
                GUILayout.EndHorizontal();
            }
        }

        /// <summary>
        /// One screen-mode button. The mode you are already in is shown pressed
        /// and does nothing, rather than re-applying a resolution change for no
        /// reason — SetResolution is not free and it flickers.
        /// </summary>
        private void DrawModeButton(string label, FullScreenMode mode)
        {
            bool here = Screen.fullScreenMode == mode;
            GUI.enabled = !here;
            if (GUILayout.Button(here ? "> " + label : label, _btn, GUILayout.Height(22)) && !here)
                DisplayManager.SetMode(mode);
            GUI.enabled = true;
        }

        /// <summary>
        /// The three reasons the mod can be holding you still, in the order
        /// they'd surprise you. Null when nothing of ours is in the way — which
        /// is itself useful to know, because then it isn't us.
        /// </summary>
        private string WhyCantIMove()
        {
            if (_mc.Ride.Active)
            {
                Machine m;
                var what = _mc.TryGetMachine(_mc.Ride.Riding, out m) ? m.Label : "something";
                return "you're riding in " + what + ". F7 gets you out.";
            }

            if (InputLock.Locked)
                return "the panel has your keyboard while the caret is in a text box. "
                     + "Click away from the chat box, or press " + Plugin.ToggleKeyName + ".";

            // The one that took a week to find. A machine the mod has frozen so
            // it refuses someone else's card can take the player standing at it
            // down with it, and from the outside that is indistinguishable from
            // the game hanging. Name it.
            foreach (var m in _mc.Machines)
            {
                if (m == null || !m.Frozen) continue;
                return "the mod has " + m.Label + " locked because "
                     + (m.OwnerName ?? "another player") + " is using it, and it has taken you "
                     + "with it. Unstick me, below, or F11.";
            }

            return null;
        }

        private void DrawChat()
        {
            GUILayout.Space(6);
            GUILayout.Label("Log", _head);
            _scroll = GUILayout.BeginScrollView(_scroll, false, true,
                                                GUIStyle.none, GUI.skin.verticalScrollbar,
                                                GUILayout.MinHeight(110));
            for (int i = 0; i < _s.Chat.Count; i++) GUILayout.Label(_s.Chat[i], _mono);
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            GUI.SetNextControlName("chatField");
            _chatDraft = GUILayout.TextField(_chatDraft ?? "", GUILayout.Height(22));
            var send = GUILayout.Button("Send", _btn, GUILayout.Width(56), GUILayout.Height(22));
            GUILayout.EndHorizontal();

            var e = Event.current;
            bool enter = e != null && e.type == EventType.KeyDown &&
                         (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) &&
                         GUI.GetNameOfFocusedControl() == "chatField";

            if ((send || enter) && !string.IsNullOrEmpty(_chatDraft))
            {
                _s.SendChat(_chatDraft);
                _chatDraft = "";
                if (enter) e.Use();
                GUI.FocusControl("chatField");
            }
        }

        private static Vector3 PlayerPos()
        {
            var cam = Camera.main;
            return cam != null ? cam.transform.position : Vector3.zero;
        }

        private string StateLine()
        {
            switch (_s.State)
            {
                case SessionState.Offline: return "offline";
                case SessionState.Creating: return "creating lobby...";
                case SessionState.Joining: return "joining...";
                case SessionState.InLobby:
                    return (_s.IsHost ? "hosting" : "connected") + "  ·  lobby " + _s.Lobby.m_SteamID;
                default: return "?";
            }
        }
    }
}
