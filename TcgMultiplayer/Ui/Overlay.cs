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
        private Rect _rect = new Rect(24, 24, 460, 430);
        private Vector2 _scroll;
        private string _chatDraft = "";
        private string _joinDraft = "";
        private bool _showJoinField;

        private GUIStyle _mono, _head, _dim, _alert;
        private bool _stylesReady;
        private bool _showAdvanced;

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
            _rect = GUI.Window(WinId, _rect, DrawWindow, "TcgMultiplayer " + Plugin.Version);
        }

        private void EnsureStyles()
        {
            if (_stylesReady) return;
            _mono = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true, richText = false };
            _head = new GUIStyle(GUI.skin.label) { fontSize = 13 };
            _head.normal.textColor = new Color(1f, 0.86f, 0.55f);   // brass, matching the plan page
            _dim = new GUIStyle(GUI.skin.label) { fontSize = 11 };
            _dim.normal.textColor = new Color(0.72f, 0.72f, 0.70f);
            _alert = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
            _alert.normal.textColor = new Color(1f, 0.55f, 0.45f);
            _stylesReady = true;
        }

        private void DrawWindow(int id)
        {
            GUILayout.Space(2);

            // One line, at the top, saying what is actually true right now.
            // Without it, opening this at the main menu shows a wall of red
            // MISSING lines — every check that can only pass once a save is
            // loaded, failing exactly as designed, and reading to anyone sane
            // as "this mod is broken".
            var dx = Health.Now(_s, _mc);
            GUILayout.Label(dx.Headline, dx.IsBad ? _alert : _head);
            if (!string.IsNullOrEmpty(dx.NextStep)) GUILayout.Label(dx.NextStep, _dim);
            GUILayout.Space(6);

            if (!_s.Ready)
            {
                GUI.DragWindow(new Rect(0, 0, 10000, 20));
                return;
            }

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
                if (GUILayout.Button("Try those again", GUILayout.Height(20))) Guard.ResetAll();
                GUILayout.Label("Send MelonLoader\\Latest.log with a bug report — it has the detail.", _dim);
                GUILayout.Space(6);
            }

            GUILayout.Label("You: " + _s.SelfName + "   (" + _s.SelfId.m_SteamID + ")", _dim);
            GUILayout.Label("Session: " + StateLine(), _head);
            GUILayout.Space(4);

            // ---- controls -------------------------------------------------
            GUILayout.BeginHorizontal();
            if (_s.State == SessionState.Offline)
            {
                if (GUILayout.Button("Host", GUILayout.Height(24))) _s.Host(Plugin.MaxPlayers);
                if (GUILayout.Button(_showJoinField ? "Cancel join" : "Join by lobby ID", GUILayout.Height(24)))
                    _showJoinField = !_showJoinField;
            }
            else
            {
                if (GUILayout.Button("Invite friend", GUILayout.Height(24))) _s.OpenInviteOverlay();
                if (GUILayout.Button("Leave", GUILayout.Height(24))) _s.Leave();
            }
            GUILayout.EndHorizontal();

            if (_s.State == SessionState.Offline && _showJoinField)
            {
                GUILayout.BeginHorizontal();
                GUI.SetNextControlName("joinField");
                _joinDraft = GUILayout.TextField(_joinDraft ?? "", GUILayout.Height(22));
                if (GUILayout.Button("Go", GUILayout.Width(44), GUILayout.Height(22)))
                {
                    ulong lobbyId;
                    if (ulong.TryParse((_joinDraft ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out lobbyId))
                        _s.Join(new Steamworks.CSteamID(lobbyId));
                }
                GUILayout.EndHorizontal();
                GUILayout.Label("Paste a lobby ID, or just use Host + Invite friend.", _dim);
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
            if (GUILayout.Button("Rescan", GUILayout.Width(64), GUILayout.Height(20))) _mc.RebuildNow();
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
            }

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
            if (GUILayout.Button("Back up my save now", GUILayout.Height(22))) SaveGuard.Backup();

            // ---- session report ---------------------------------------------
            GUILayout.Space(6);
            GUILayout.Label("Last session report", _head);
            GUILayout.Label(SessionReport.Status, _dim);
            GUILayout.BeginHorizontal();
            GUI.enabled = !string.IsNullOrEmpty(SessionReport.Last);
            if (GUILayout.Button("Copy report", GUILayout.Height(22)))
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
            if (GUILayout.Button("Run checks", GUILayout.Height(22)))
                SelfTest.LastDiagnosis = Health.Now(_s, _mc);
                SelfTest.RunAll(_s, _mc, _world);
            GUI.enabled = SelfTest.HasRun;
            if (GUILayout.Button("Copy result", GUILayout.Height(22)))
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

            // ---- solo test harness ----------------------------------------
            // Folded away by default. It's genuinely useful — it's how most of
            // this was built without a second copy of the game — but a player
            // who just installed a co-op mod should not be met with a button
            // marked "Fake: friend takes it".
            GUILayout.Space(6);
            _showAdvanced = GUILayout.Toggle(_showAdvanced, "  Testing tools (no second player needed)");
            if (!_showAdvanced)
            {
                GUILayout.Label(Plugin.ToggleKeyName + " closes this. " + InputLock.Status
                          + (TypingInABox ? " — typing, so the game isn't listening" : "") + ".", _dim);
                DrawChat();
                GUI.DragWindow(new Rect(0, 0, 10000, 20));
                return;
            }

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
                if (GUILayout.Button("Record a round", GUILayout.Height(22)) && near != null)
                    _mc.Rehearse.StartRecording(near.Id, near.Label);
                GUI.enabled = true;
            }

            GUI.enabled = _mc.Rehearse.HasTape && !_mc.Rehearse.Playing && !_mc.Rehearse.Recording;
            if (GUILayout.Button("Replay as a friend", GUILayout.Height(22)))
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
            if (GUILayout.Button("Fake: friend takes it", GUILayout.Height(22))) _mc.SimulateRemoteClaim(near);
            if (GUILayout.Button("Fake: friend leaves", GUILayout.Height(22))) _mc.SimulateRemoteRelease(near);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Label("\"Friend takes it\" should make the cabinet refuse your card. It will "
                          + "never freeze a machine you are standing in.", _dim);

            if (GUILayout.Button("Release everything (stuck in a machine?)", GUILayout.Height(22)))
                _mc.ReleaseEverything();

            GUILayout.Label(Plugin.ToggleKeyName + " closes this. " + InputLock.Status
                          + (TypingInABox ? " — typing, so the game isn't listening" : "") + ".", _dim);
            DrawChat();
            GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }

        private void DrawChat()
        {
            GUILayout.Space(6);
            GUILayout.Label("Log", _head);
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(120));
            for (int i = 0; i < _s.Chat.Count; i++) GUILayout.Label(_s.Chat[i], _mono);
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            GUI.SetNextControlName("chatField");
            _chatDraft = GUILayout.TextField(_chatDraft ?? "", GUILayout.Height(22));
            var send = GUILayout.Button("Send", GUILayout.Width(56), GUILayout.Height(22));
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
