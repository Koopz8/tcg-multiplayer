using System;
using System.Text.RegularExpressions;
using MelonLoader;
using Steamworks;
using TcgMultiplayer.Game;
using TcgMultiplayer.Net;
using TcgMultiplayer.Ui;
using UnityEngine;

[assembly: MelonInfo(typeof(TcgMultiplayer.Plugin), "TcgMultiplayer", TcgMultiplayer.Plugin.Version, "Mason")]
[assembly: MelonGame("devotid", "TheCoinGame")]

namespace TcgMultiplayer
{
    public class Plugin : MelonMod
    {
        public const string Version = "0.6.0";

        private static Plugin _instance;

        private static MelonPreferences_Entry<string> _pToggleKey;
        private static MelonPreferences_Entry<int> _pMaxPlayers;
        private static MelonPreferences_Entry<bool> _pOpenOnStart;
        private static MelonPreferences_Entry<bool> _pSuppressInput;
        private static MelonPreferences_Entry<float> _pSendRate;
        private static MelonPreferences_Entry<float> _pInterpDelay;
        private static MelonPreferences_Entry<float> _pMirrorDelay;
        private static MelonPreferences_Entry<float> _pAnimSpeedScale;

        public static int MaxPlayers { get { return _pMaxPlayers != null ? Mathf.Clamp(_pMaxPlayers.Value, 2, 8) : 4; } }
        public static string ToggleKeyName { get { return _pToggleKey != null ? _pToggleKey.Value : "F9"; } }

        private Session _session;
        private Overlay _overlay;
        private AvatarDirector _avatars;
        private MachineDirector _machines;
        private WorldState _world;
        private HarmonyLib.Harmony _harmony;
        private bool _initTried;
        private float _nextInitTry;
        private bool _handledLaunchLobby;

        public override void OnInitializeMelon()
        {
            _instance = this;

            var cat = MelonPreferences.CreateCategory("TcgMultiplayer", "TCG Multiplayer");
            _pToggleKey = cat.CreateEntry("ToggleKey", "F9", "Overlay toggle key");
            _pMaxPlayers = cat.CreateEntry("MaxPlayers", 4, "Max players",
                "Lobby size cap, 2-8. The bandwidth model is designed around 4.");
            _pOpenOnStart = cat.CreateEntry("OpenOverlayOnStart", true, "Open overlay at startup",
                "Handy while the mod is still a debug tool.");
            _pSuppressInput = cat.CreateEntry("SuppressGameInputWhileOpen", true, "Suppress game input while overlay is open",
                "Disables Rewired's input maps so typing in chat doesn't also drive the player.");

            _pSendRate = cat.CreateEntry("SnapshotHz", 15f, "Snapshot rate (Hz)",
                "How often the local player's position is sent. 15 is plenty for walking speed.");
            _pInterpDelay = cat.CreateEntry("InterpolationDelaySeconds", 0.12f, "Interpolation delay (s)",
                "Remote bodies render this far in the past so motion stays smooth between packets.");
            _pMirrorDelay = cat.CreateEntry("MirrorDelaySeconds", 1.5f, "Mirror delay (s)",
                "Solo test mode: how far behind you the mirrored ghost walks.");

            _pAnimSpeedScale = cat.CreateEntry("AnimatorSpeedScale", 1f, "Animator speed scale",
                "Multiplies the value fed to the walk/run blend. Raise it if remote bodies "
                + "glide with their legs barely moving, lower it if they sprint on the spot.");

            _session = new Session();
            _avatars = new AvatarDirector(_session);
            _avatars.SendRate = _pSendRate.Value;
            _avatars.MirrorDelay = _pMirrorDelay.Value;
            RemoteAvatar.InterpDelay = Mathf.Clamp(_pInterpDelay.Value, 0.02f, 1f);
            RemoteAvatar.SpeedScale = Mathf.Clamp(_pAnimSpeedScale.Value, 0.05f, 20f);
            var pWalletPrefixes = cat.CreateEntry("ProtectedEconomyGlobals", WalletGuard.DefaultProtectedPrefixes,
                "Protected economy globals (prefixes)",
                "Wallets are per-player. These PlayMaker globals are snapshotted and restored "
                + "around every mirrored machine event, so watching someone else play can never "
                + "pay you. Comma separated, prefix match.");

            var pWorldPrefixes = cat.CreateEntry("SharedWorldGlobals", WorldState.DefaultWorldPrefixes,
                "Shared world globals (prefixes)",
                "The island's progression - area unlocks, doors, vehicles bought. Anyone can "
                + "change these; the host arbitrates and everyone converges. Everything NOT "
                + "listed here stays private to each player.");

            _machines = new MachineDirector(_session);
            _machines.Wallet.Configure(pWalletPrefixes.Value);
            _world = new WorldState(_session);
            _world.Configure(pWorldPrefixes.Value);
            _overlay = new Overlay(_session, _avatars, _machines, _world) { Visible = _pOpenOnStart.Value };

            _harmony = new HarmonyLib.Harmony("com.mason.tcgmultiplayer");
            try { _machines.ApplyPatches(_harmony); }
            catch (Exception ex) { Warn("Harmony patching failed: " + ex); }

            Log("Loaded. " + ToggleKeyName + " toggles the overlay.");
        }

        public override void OnUpdate()
        {
            // Steam belongs to the game; wait for its SteamManager rather than
            // initialising anything ourselves.
            if (!_session.Ready && Time.realtimeSinceStartup >= _nextInitTry)
            {
                _nextInitTry = Time.realtimeSinceStartup + 2f;
                if (_session.Init()) TryLaunchLobby();
                else if (!_initTried) { _initTried = true; }
            }

            if (Hotkeys.Down(ToggleKeyName))
            {
                _overlay.Visible = !_overlay.Visible;
                ApplyInputLock();
            }

            if (_overlay.Visible) FreeCursor();
            _session.Tick();
            _avatars.Tick();
            _machines.Tick();
            _world.Tick();
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            // Scenes load additively and PLAYER is rebuilt, so every cached
            // transform and every cloned body is stale from here.
            _avatars.OnSceneChanged();
            _machines.OnSceneChanged();
        }

        public override void OnGUI()
        {
            // The trace showed an FSM re-issuing "Cursor LOCKED" every frame, so a
            // one-shot unlock loses the fight. OnGUI runs after every Update, so
            // reasserting here is what actually makes the overlay clickable.
            if (_overlay.Visible) FreeCursor();
            _avatars.DrawNameplates();
            _overlay.Draw();
        }

        private static void FreeCursor()
        {
            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
            if (!Cursor.visible) Cursor.visible = true;
        }

        public override void OnApplicationQuit()
        {
            try { _avatars.DespawnAll(); } catch { }
            try { _session.Leave(); } catch { }
            InputLock.Set(false);
        }

        private void ApplyInputLock()
        {
            bool want = _overlay.Visible && (_pSuppressInput == null || _pSuppressInput.Value);
            InputLock.Set(want);
        }

        /// <summary>
        /// Clicking "Join Game" on a friend while the game is closed launches it
        /// with +connect_lobby &lt;id&gt;. If the game is already running, the
        /// GameLobbyJoinRequested callback covers it instead.
        /// </summary>
        private void TryLaunchLobby()
        {
            if (_handledLaunchLobby) return;
            _handledLaunchLobby = true;
            try
            {
                var cmd = Environment.CommandLine ?? "";
                string launch;
                if (SteamApps.GetLaunchCommandLine(out launch, 1024) > 0 && !string.IsNullOrEmpty(launch))
                    cmd += " " + launch;

                var m = Regex.Match(cmd, @"\+connect_lobby\s+(\d{5,})");
                if (!m.Success) return;

                ulong id;
                if (!ulong.TryParse(m.Groups[1].Value, out id)) return;

                Log("Launched with +connect_lobby " + id);
                _session.Join(new CSteamID(id));
                _overlay.Visible = true;
                ApplyInputLock();
            }
            catch (Exception ex) { Warn("Launch lobby parse failed: " + ex.Message); }
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
