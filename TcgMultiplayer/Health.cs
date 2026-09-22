using System;
using System.IO;
using UnityEngine;

namespace TcgMultiplayer
{
    /// <summary>
    /// Gathers the facts Diagnosis reasons about. Everything Unity-shaped and
    /// untestable lives here so that everything decidable lives there.
    /// </summary>
    internal static class Health
    {
        private static float _startedAt = -1f;
        private static int _bepInEx = -1;    // -1 unknown, 0 no, 1 yes

        public static void NoteStart() { _startedAt = Time.realtimeSinceStartup; }

        /// <summary>
        /// The other Coin Game mods use BepInEx. Two loaders proxying the game's
        /// startup usually means it launches with neither, and the player has no
        /// way at all to find that out — the mod that would have told them is one
        /// of the ones that didn't load. Checked once; the answer cannot change
        /// while the game is running.
        /// </summary>
        public static bool BepInExPresent
        {
            get
            {
                if (_bepInEx >= 0) return _bepInEx == 1;
                _bepInEx = 0;
                try
                {
                    var game = Directory.GetParent(Application.dataPath);
                    if (game != null)
                    {
                        if (Directory.Exists(Path.Combine(game.FullName, "BepInEx")) ||
                            File.Exists(Path.Combine(game.FullName, "winhttp.dll")))
                            _bepInEx = 1;
                    }
                }
                catch { }
                if (_bepInEx == 1)
                    Plugin.Warn("BepInEx found in the game folder alongside MelonLoader. "
                              + "Two mod loaders in one folder usually means neither runs.");
                return _bepInEx == 1;
            }
        }

        private static float _enteredWorldAt = -1f;

        public static Signals Gather(Net.Session session, Game.MachineDirector machines,
                                     Game.AvatarDirector avatars)
        {
            int total = 0, failed = 0;
            try
            {
                foreach (var i in CompatCheck.Items) { total++; if (!i.Ok) failed++; }
            }
            catch { }

            bool economy = false;
            int machineCount = 0;
            try
            {
                if (machines != null)
                {
                    economy = machines.Wallet != null && machines.Wallet.Available;
                    machineCount = machines.MachineCount;
                }
            }
            catch { }

            // The player rig only exists in a playable scene. The economy globals
            // do not make that distinction — they read fine at the title screen.
            bool inWorld = false;
            try { inWorld = avatars != null && avatars.RigReady; } catch { }

            if (inWorld && _enteredWorldAt < 0) _enteredWorldAt = Time.realtimeSinceStartup;
            else if (!inWorld) _enteredWorldAt = -1f;

            return new Signals
            {
                SteamReady = session != null && session.Ready,
                InWorld = inWorld,
                EconomyReadable = economy,
                MachinesFound = machineCount,
                SecondsInWorld = _enteredWorldAt < 0 ? 0 : Time.realtimeSinceStartup - _enteredWorldAt,
                CompatTotal = total,
                CompatFailed = failed,
                BepInExPresent = BepInExPresent,
                RetiredSubsystems = SafeBrokenCount(),
                InSession = session != null && session.State == Net.SessionState.InLobby,
                SecondsSinceLoad = _startedAt < 0 ? 0 : Time.realtimeSinceStartup - _startedAt,
            };
        }

        private static int SafeBrokenCount()
        {
            try { return Guard.Broken.Count; }
            catch { return 0; }
        }

        public static Diagnosis Now(Net.Session session, Game.MachineDirector machines,
                                    Game.AvatarDirector avatars)
        {
            return Diagnosis.Of(Gather(session, machines, avatars));
        }
    }
}
