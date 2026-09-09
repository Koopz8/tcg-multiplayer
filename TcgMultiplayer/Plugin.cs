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
        public const string Version = "0.9.8";

        private static Plugin _instance;

        private static MelonPreferences_Entry<string> _pToggleKey;
        private static MelonPreferences_Entry<int> _pMaxPlayers;
        private static MelonPreferences_Entry<bool> _pOpenOnStart;
        private static MelonPreferences_Entry<bool> _pSuppressInput;
        private static MelonPreferences_Entry<float> _pSendRate;
        private static MelonPreferences_Entry<float> _pInterpDelay;
        private static MelonPreferences_Entry<float> _pMirrorDelay;
        private static MelonPreferences_Entry<float> _pAnimSpeedScale;
        private static MelonPreferences_Entry<string> _pPanicKey;
        private static MelonPreferences_Entry<bool> _pLockStats;
        private static MelonPreferences_Entry<bool> _pBackupSave;
        private static MelonPreferences_Entry<bool> _pProtectGuest;
        private static MelonPreferences_Entry<string> _pSelfTestKey;
        private static MelonPreferences_Entry<bool> _pConfineCursor;
        private bool _overlayWasVisible;

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
            _pOpenOnStart = cat.CreateEntry("OpenOverlayOnStart", false, "Open overlay at startup",
                "Off by default so the game starts the way you expect. Press the toggle key when you want it.");
            // Key name kept from when this meant "while the panel is open", so
            // nobody's existing config resets. What it does is narrower now.
            _pSuppressInput = cat.CreateEntry("SuppressGameInputWhileOpen", true, "Suppress game input while typing",
                "Stops your keypresses reaching the game while the caret is in the chat or lobby-ID "
                + "box, so typing doesn't also walk your character. Only while you're actually typing.");

            _pSendRate = cat.CreateEntry("SnapshotHz", 15f, "Snapshot rate (Hz)",
                "How often the local player's position is sent. 15 is plenty for walking speed.");
            _pInterpDelay = cat.CreateEntry("InterpolationDelaySeconds", 0.12f, "Interpolation delay (s)",
                "Remote bodies render this far in the past so motion stays smooth between packets.");
            _pMirrorDelay = cat.CreateEntry("MirrorDelaySeconds", 1.5f, "Mirror delay (s)",
                "Solo test mode: how far behind you the mirrored ghost walks.");

            _pLockStats = cat.CreateEntry("BlockSteamStatsInSession", true,
                "Block Steam achievements and leaderboards during a session",
                "Strongly recommended. Spectating replays a friend's round on your machine, so "
                + "without this their win can post to your leaderboard. Single player is unaffected.");
            _pPanicKey = cat.CreateEntry("ReleaseEverythingKey", "F11", "Release-everything key",
                "Unfreezes every machine and clears all ownership. Use it if a cabinet ever "
                + "refuses to let you out.");
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

            _pBackupSave = cat.CreateEntry("BackupSaveBeforeSession", true, "Back up the save before a session",
                "Copies your save folder aside the first time you host or join. Five copies are kept. "
                + "Leave this on — a crash mid-session is the one case the mod can't tidy up after itself.");

            _pConfineCursor = cat.CreateEntry("ConfineCursorToWindow", true, "Keep the cursor in the window",
                "While the panel is open, the mouse stays inside the game window instead of "
                + "wandering onto another monitor. Turn this off if you'd rather be able to drag "
                + "the pointer out to a second screen.");

            _pSelfTestKey = cat.CreateEntry("SelfTestKey", "F10", "Self-test key",
                "Runs the built-in checks and shows the result. Safe to press any time you "
                + "aren't in a session.");

            _pProtectGuest = cat.CreateEntry("GuestKeepsOwnProgression", true, "Guests keep their own progression",
                "Visiting someone's island borrows their unlocks for the visit and gives you yours back "
                + "when you leave, keeping anything you unlocked yourself. Turning this off means their "
                + "unlocks follow you home permanently.");

            _machines = new MachineDirector(_session);
            _machines.Wallet.Configure(pWalletPrefixes.Value);
            _world = new WorldState(_session);
            _world.Configure(pWorldPrefixes.Value);
            _world.ProtectGuestProgression = _pProtectGuest.Value;
            _overlay = new Overlay(_session, _avatars, _machines, _world) { Visible = _pOpenOnStart.Value };

            // The save is copied aside before anyone connects, and a guest's own
            // progression is stashed and put back around the visit. Both hang off
            // the session lifecycle so every exit path is covered — the Leave
            // button, a host that vanishes, and quitting the game outright.
            _session.OnSessionBegan += isHost =>
            {
                if (_pBackupSave == null || _pBackupSave.Value) SaveGuard.BackupOnce();
                if (!isHost) _world.BeginVisit();
            };
            _session.OnSessionEnded += () =>
            {
                // Capture before the restore, so the report can say whether the
                // player was still mid-visit when it ended — which is exactly the
                // state a crash would have left them in.
                SessionReport.Capture(_session, _machines, _world);
                _world.EndVisit();
                SaveGuard.ArmForNextSession();
            };

            _harmony = new HarmonyLib.Harmony("com.mason.tcgmultiplayer");
            try { _machines.ApplyPatches(_harmony); }
            catch (Exception ex) { Warn("Harmony patching failed: " + ex); }

            // A friend's round can play out on your machine, so Steam must not
            // hear about it. Blocked while a session or a rehearsal is running;
            // single player is untouched.
            StatsLock.ShouldBlock = () =>
                (_pLockStats == null || _pLockStats.Value)
                && (_session.State == SessionState.InLobby || _machines.Rehearse.Playing);
            try { StatsLock.Apply(_harmony); }
            catch (Exception ex) { Warn("Stats lock failed: " + ex); }

            // Blocks the game's cursor-lock while the panel is open, rather than
            // undoing it every frame — see CursorGuard for why that flickered.
            CursorGuard.ShouldHold = () => _overlay != null && _overlay.Visible;
            CursorGuard.Desired = (_pConfineCursor == null || _pConfineCursor.Value)
                ? CursorLockMode.Confined : CursorLockMode.None;
            try { CursorGuard.Apply(_harmony); }
            catch (Exception ex) { Warn("Cursor guard failed: " + ex); }

            BuildTickDelegates();
            CompatCheck.ComputeGameHash();

            Log("Loaded. " + ToggleKeyName + " toggles the overlay, "
                + (_pPanicKey != null ? _pPanicKey.Value : "F11") + " releases every machine.");
        }

        // Delegates are built once, not per frame. `Guard.Run("net", () => ...)`
        // reads nicely but allocates a fresh closure on every call — seven of
        // them a frame, four hundred a second, in a game already leaning on
        // Unity's incremental collector. Cached here they cost nothing.
        private Action _doInput, _doNet, _doAvatars, _doMachines, _doWorld,
                       _doHealth, _doScene, _doNameplates, _doOverlay;

        private void BuildTickDelegates()
        {
            _doInput = TickInput;
            _doNet = () => _session.Tick();
            _doAvatars = () => _avatars.Tick();
            _doMachines = () => _machines.Tick();
            _doWorld = () => _world.Tick();
            _doHealth = CompatCheck.ReportOnce;
            _doScene = () => { _avatars.OnSceneChanged(); _machines.OnSceneChanged(); };
            _doNameplates = () => { if (_overlay.Visible) FreeCursor(); _avatars.DrawNameplates(); };
            _doOverlay = () => _overlay.Draw();
        }

        private void TickInput()
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
                _overlay.Visible = !_overlay.Visible;

            if (Hotkeys.Down(_pPanicKey != null ? _pPanicKey.Value : "F11"))
            {
                _machines.ReleaseEverything();
                // The panic key means "give me my game back", so it also drops
                // the input lock. If suppression ever sticks with the panel
                // closed, this is the way out that doesn't involve task manager.
                InputLock.Set(false);
                InputLock.Tick();
            }

            if (Hotkeys.Down(_pSelfTestKey != null ? _pSelfTestKey.Value : "F10"))
            {
                SelfTest.RunAll(_session, _machines, _world);
                _overlay.Visible = true;
            }

            if (_overlay.Visible) FreeCursor();
            else if (_overlayWasVisible) CursorGuard.Release();   // hand the pointer back once
            _overlayWasVisible = _overlay.Visible;

            // Evaluated every frame rather than on toggle: what it depends on is
            // whether the caret is in a text box, which changes without anything
            // being toggled. Rewired also isn't ready the moment the panel first
            // opens, so applying the lock has to keep retrying.
            ApplyInputLock();
            InputLock.Tick();
        }

        // Everything below runs every frame, and nothing above us catches what it
        // throws. Each subsystem is isolated so one of them failing costs that
        // feature rather than the player's game, and timed so the cost is visible.
        public override void OnUpdate()
        {
            Guard.Run("input", _doInput);
            Guard.Run("networking", _doNet);
            Guard.Run("player bodies", _doAvatars);
            Guard.Run("machines", _doMachines);
            Guard.Run("world state", _doWorld);

            if (Time.frameCount % 600 == 0) Guard.Run("health check", _doHealth);

            Perf.EndFrame();
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            // Scenes load additively and PLAYER is rebuilt, so every cached
            // transform and every cloned body is stale from here.
            Guard.Run("scene change", _doScene);
        }

        public override void OnGUI()
        {
            // The trace showed an FSM re-issuing "Cursor LOCKED" every frame, so a
            // one-shot unlock loses the fight. OnGUI runs after every Update, so
            // reasserting here is what actually makes the overlay clickable.
            //
            // Guarded separately from the overlay: an exception thrown out of OnGUI
            // lands in the middle of Unity's own IMGUI pass, and taking the game's
            // UI down with us is not an acceptable way to fail.
            Guard.Run("nameplates", _doNameplates);
            Guard.Run("overlay", _doOverlay);
        }

        /// <summary>
        /// Puts the pointer back once. With the cursor guard patched in, nothing
        /// takes it away again, so this settles instead of strobing — the
        /// per-frame re-assert only matters as a fallback when the patch failed.
        /// </summary>
        private static void FreeCursor()
        {
            var want = CursorGuard.Desired;
            if (Cursor.lockState != want) Cursor.lockState = want;
            if (!Cursor.visible) Cursor.visible = true;
        }

        public override void OnApplicationQuit()
        {
            // Leave() first: it fires OnSessionEnded, which is what puts a guest's
            // own progression back before the game gets a chance to save on the
            // way out. The order matters more than it looks.
            try { _session.Leave(); } catch (Exception ex) { Warn("Leave on quit failed: " + ex.Message); }
            try { _machines.ReleaseEverything(); } catch { }
            try { _avatars.DespawnAll(); } catch { }
            try { InputLock.Set(false); } catch { }
        }

        /// <summary>
        /// Suppress the game's input only while the player is actually typing
        /// into one of the panel's boxes.
        ///
        /// It used to be "whenever the panel is open", which turned out to be the
        /// wrong rule the moment the lock started working: disabling every
        /// Rewired map stops the player moving AND stops the game's own menus
        /// responding, so opening the panel looked exactly like the game had
        /// hung. The point was only ever to keep WASD in the chat box.
        /// </summary>
        private void ApplyInputLock()
        {
            bool want = _overlay.Visible
                        && _overlay.TypingInABox
                        && (_pSuppressInput == null || _pSuppressInput.Value);
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
