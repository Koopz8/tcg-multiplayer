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
        private Rect _rect = new Rect(24, 24, 460, 430);
        private Vector2 _scroll;
        private string _chatDraft = "";
        private string _joinDraft = "";
        private bool _showJoinField;

        private GUIStyle _mono, _head, _dim;
        private bool _stylesReady;

        public bool Visible;

        public Overlay(Session s, AvatarDirector av, MachineDirector mc) { _s = s; _av = av; _mc = mc; }

        public void Draw()
        {
            if (!Visible) return;
            EnsureStyles();
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
            _stylesReady = true;
        }

        private void DrawWindow(int id)
        {
            GUILayout.Space(2);

            if (!_s.Ready)
            {
                GUILayout.Label("Waiting for the game to initialise Steam...", _mono);
                GUI.DragWindow(new Rect(0, 0, 10000, 20));
                return;
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

            GUILayout.BeginHorizontal();
            bool mirror = GUILayout.Toggle(_av.MirrorEnabled, "  Mirror me (solo test)", GUILayout.Width(190));
            if (mirror != _av.MirrorEnabled) _av.MirrorEnabled = mirror;
            GUILayout.Label("delay", _dim, GUILayout.Width(38));
            _av.MirrorDelay = Mathf.Round(GUILayout.HorizontalSlider(_av.MirrorDelay, 0.25f, 5f) * 4f) / 4f;
            GUILayout.Label(_av.MirrorDelay.ToString("0.00") + "s", _mono, GUILayout.Width(48));
            GUILayout.EndHorizontal();
            GUILayout.Label("Mirror replays your own movement through the real wire format, so the "
                          + "avatar path can be tested without a second copy of the game.", _dim);

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
                    GUILayout.Label(m.OwnedByMe ? "yours" : ("in use by " + (m.OwnerName ?? "?")), _dim);
                    GUILayout.EndHorizontal();
                    if (++shown >= 6) break;
                }
                if (shown == 0) GUILayout.Label("All free.", _dim);
                GUILayout.Label("mirrored events: " + _mc.MirroredEventsSent + " sent, "
                              + _mc.MirroredEventsApplied + " applied", _dim);
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

            // ---- log ------------------------------------------------------
            GUILayout.Space(6);
            GUILayout.Label("Log", _head);
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(120));
            for (int i = 0; i < _s.Chat.Count; i++) GUILayout.Label(_s.Chat[i], _mono);
            GUILayout.EndScrollView();

            // ---- chat -----------------------------------------------------
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

            GUILayout.Label(Plugin.ToggleKeyName + " closes this. " + InputLock.Status + ".", _dim);
            GUI.DragWindow(new Rect(0, 0, 10000, 20));
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
