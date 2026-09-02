using System;
using System.Globalization;
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
        private Rect _rect = new Rect(24, 24, 460, 430);
        private Vector2 _scroll;
        private string _chatDraft = "";
        private string _joinDraft = "";
        private bool _showJoinField;

        private GUIStyle _mono, _head, _dim;
        private bool _stylesReady;

        public bool Visible;

        public Overlay(Session s) { _s = s; }

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
                    GUILayout.Label(p.Handshaked ? "ready" : "handshaking", _dim, GUILayout.Width(90));
                    GUILayout.Label(p.RttMs >= 0 ? p.RttMs.ToString("0") + " ms" : "-", _mono, GUILayout.Width(60));
                    GUILayout.Label(Net.SteamTransport.ConnectionState(p.Id), _dim);
                    GUILayout.EndHorizontal();
                }
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
