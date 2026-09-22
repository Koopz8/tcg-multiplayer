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
        public const string Version = "0.11.5";

        /// <summary>
        /// When this DLL was written, read off the file itself. Shown in the
        /// panel title because "I rebuilt and nothing changed" is nearly always
        /// "the new DLL never reached the game folder", and that is invisible
        /// otherwise — the version string looks identical either way.
        /// </summary>
        public static string NextMonitorKeyName
        {
            get
            {
                var i = _instance;
                return i != null && i._pDisplayKey != null ? i._pDisplayKey.Value : "F8";
            }
        }

        /// <summary>Panel hooks. The manager does the work; these own the settings.</summary>
        public static void RememberMonitor()
        {
            var i = _instance;
            if (i == null) return;
            DisplayManager.Remember((name, index) =>
            {
                if (i._pDisplayName != null) i._pDisplayName.Value = name;
                if (i._pDisplayIndex != null) i._pDisplayIndex.Value = index;
                MelonPreferences.Save();
            });
        }

        public static void ForgetMonitor()
        {
            var i = _instance;
            if (i == null) return;
            DisplayManager.Forget((name, index) =>
            {
                if (i._pDisplayName != null) i._pDisplayName.Value = name;
                if (i._pDisplayIndex != null) i._pDisplayIndex.Value = index;
                MelonPreferences.Save();
            });
        }

        public static string BuildStamp
        {
            get
            {
                if (_buildStamp != null) return _buildStamp;
                try
                {
                    var path = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    _buildStamp = System.IO.File.GetLastWriteTime(path).ToString("d MMM HH:mm");
                }
                catch { _buildStamp = "?"; }
                return _buildStamp;
            }
        }
        private static string _buildStamp;

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

        private static MelonPreferences_Entry<string> _pRideKey;
        private MelonPreferences_Entry<string> _pDisplayKey;
        private MelonPreferences_Entry<string> _pDisplayName;
        private MelonPreferences_Entry<int> _pDisplayIndex;

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

            var pLocalTest = cat.CreateEntry("LocalTestMode", false, "Local test mode (two windows on one PC)",
                "Runs the mod's networking over loopback instead of Steam, so two copies of the game "
                + "on this PC can play together. The first window is the host, the second is a guest "
                + "and CANNOT write to your save. For testing without a second person - turn it off "
                + "for real play. Requires launching the second copy from TheCoinGame.exe directly.");

            // Harmony has to exist before the session, because local test mode
            // needs the save block patched before a guest window is allowed to
            // come up at all.
            _harmony = new HarmonyLib.Harmony("com.mason.tcgmultiplayer");

            var pGuestMustNotSave = cat.CreateEntry("GuestWindowMustNotSave", true,
                "Guest test window must not be able to save",
                "Local test mode only. Two windows share one save folder, so the guest window is "
                + "stopped from writing to it. With this on, a guest window that can't be stopped "
                + "refuses to start at all. Turn it off only if you have no save worth protecting - "
                + "then it warns instead of refusing.");

            bool wantLocal = pLocalTest.Value || LocalTest.RequestedOnCommandLine();
            ITransport lanNet = null;
            ILobbyBackend lanLobby = null;
            if (wantLocal && !LocalTest.TryStart(_harmony, pGuestMustNotSave.Value, out lanNet, out lanLobby))
            {
                Warn("Local test mode did not start: " + LocalTest.Refusal + ". Using Steam as normal.");
                lanNet = null; lanLobby = null;
            }

            _session = lanNet != null && lanLobby != null
                ? new Session(lanNet, lanLobby)
                : new Session();

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

            // The base game has no monitor setting, so the mod keeps one.
            // Name first because it survives the monitor order changing; the
            // index is only a fallback.
            _pDisplayKey = cat.CreateEntry("NextMonitorKey", "F8", "Move to next monitor",
                "Cycles the game window between monitors. Works without being able to see "
                + "the game, which is the point of it.");
            _pDisplayName = cat.CreateEntry("PreferredMonitorName", "", "Preferred monitor (name)",
                "Set from the panel. Clear this if the game ever opens somewhere you can't see it.");
            _pDisplayIndex = cat.CreateEntry("PreferredMonitorIndex", -1, "Preferred monitor (slot)",
                "Fallback for when the name doesn't match. -1 means no preference.");

            _pProtectGuest = cat.CreateEntry("GuestKeepsOwnProgression", true, "Guests keep their own progression",
                "Visiting someone's island borrows their unlocks for the visit and gives you yours back "
                + "when you leave, keeping anything you unlocked yourself. Turning this off means their "
                + "unlocks follow you home permanently.");

            _pRideKey = cat.CreateEntry("RideAlongKey", "F7", "Get in / out of a friend's vehicle",
                "Walk up to something a friend is driving and press this to ride along. "
                + "Press it again to get out. F11 also gets you out of anything.");

            _machines = new MachineDirector(_session);
            _machines.Wallet.Configure(pWalletPrefixes.Value);
            _machines.RigSource = () => _avatars.Rig;

            // The two halves find each other through delegates rather than
            // references: a passenger's position is sent in their vehicle's
            // frame, so the avatar side has to be able to ask the machine side
            // where that vehicle is — while still working on its own if the
            // machine side never comes up.
            _avatars.LocalAttachment = () => _machines.CurrentAttachment;
            _avatars.OnRemoteSeat = (id, seat, local) => _machines.NoteRemoteSeat(id, seat, local);
            _avatars.AttachmentRoot = id =>
            {
                Machine m;
                return _machines.TryGetMachine(id, out m) && m.Moving != null ? m.Moving : null;
            };
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
            Health.NoteStart();
            // Late on purpose — the game sets its own resolution while starting,
            // and moving the window before it has finished just fights it.
            DisplayManager.ScheduleStartupApply(6f);
            // Read once, early, and say so loudly: if the player has BepInEx in
            // the same folder they may well be reading this log precisely because
            // nothing loaded the last time they tried.
            if (Health.BepInExPresent)
                Warn("Pick one mod loader. The other Coin Game mods use BepInEx; this one uses MelonLoader.");

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
            // The local lobby's presence files are read and written here, on the
            // same beat as the session, so a window that closed is noticed by
            // the same pass that would have noticed a Steam member leaving.
            _doNet = () => { LocalTest.Tick(); _session.Tick(); };
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

            if (Hotkeys.Down(_pRideKey != null ? _pRideKey.Value : "F7"))
                ToggleRide();

            DisplayManager.Tick(_pDisplayName != null ? _pDisplayName.Value : "",
                                _pDisplayIndex != null ? _pDisplayIndex.Value : -1);

            if (Hotkeys.Down(_pDisplayKey != null ? _pDisplayKey.Value : "F8"))
                DisplayManager.MoveToNext();

            if (Hotkeys.Down(_pSelfTestKey != null ? _pSelfTestKey.Value : "F10"))
            {
                SelfTest.LastDiagnosis = Health.Now(_session, _machines, _avatars);
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
            // Drops our presence file and frees the port, so the slot is
            // available again immediately rather than after a heartbeat timeout.
            try { LocalTest.Stop(); } catch { }
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
        /// <summary>
        /// One key for both directions. Riding is a thing you are or aren't, and
        /// giving it a separate get-out key means someone standing next to a car
        /// they're already in has to remember which is which.
        /// </summary>
        private void ToggleRide()
        {
            if (_machines.Ride.Riding != 0 || _machines.Ride.Pending != 0)
            {
                _machines.LeaveRide();
                return;
            }

            var rig = _avatars.Rig;
            if (rig == null || !rig.Valid) return;

            // Whatever is nearest and rideable. Deliberately generous on range:
            // you cannot stand on a moving car's bumper, so "near enough to get
            // in" has to mean near enough to chase.
            var near = _machines.Nearest(rig.Root.position, 8f);
            string why;
            if (_machines.CanRide(near, out why))
            {
                _machines.RequestRide(near);
                _overlay.Visible = true;
            }
            else
            {
                _machines.Ride.Status = why ?? "nothing to get into here";
                Log("Ride: " + _machines.Ride.Status);
            }
        }

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
            LocalTest.Tee(msg);
        }

        internal static void Warn(string msg)
        {
            if (_instance != null) _instance.LoggerInstance.Warning(msg);
            else MelonLogger.Warning(msg);
            LocalTest.Tee("WARN  " + msg);
        }
    }
}
