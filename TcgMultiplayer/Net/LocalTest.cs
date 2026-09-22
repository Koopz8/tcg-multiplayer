using System;
using System.IO;
using UnityEngine;

namespace TcgMultiplayer.Net
{
    /// <summary>
    /// Local test mode: a second peer without a second person.
    ///
    /// Two copies of the game on one PC, talking over loopback. It is the only
    /// way to actually run the things built since the last release — a vehicle's
    /// position stream and riding in one have no solo path through them at all,
    /// and the offline rig can prove the rules but cannot show you a car.
    ///
    /// Two things had to be true before this could work, and neither is a
    /// bypass of anything:
    ///
    ///  * The game ships with Unity's "Force Single Instance" flag set, so a
    ///    second launch focuses the first window instead of opening. That flag
    ///    lives as one line in TheCoinGame_Data/boot.config.
    ///  * Without steam_appid.txt the exe asks Steam to relaunch it, and Steam
    ///    focuses the running copy. That file is the documented Steamworks way
    ///    to run a build directly; Steam still has to be running, and still has
    ///    to be an account that owns the game, for the API to initialise.
    ///
    /// Which window you are is decided by which port you managed to bind, so
    /// nothing has to be configured per window — both instances read the same
    /// config file and still end up with different identities.
    /// </summary>
    internal static class LocalTest
    {
        /// <summary>Command line that forces it on, for a shortcut to the second window.</summary>
        public const string CommandLineFlag = "--tcgmp-lan";

        public static bool Active { get; private set; }
        public static LanTransport Transport { get; private set; }
        public static LanLobbyBackend Lobby { get; private set; }
        public static string Refusal { get; private set; }

        public static int Slot { get { return Transport != null ? Transport.Slot : -1; } }
        public static bool IsHostWindow { get { return Active && LanAddressing.IsHostSlot(Slot); } }

        public static bool RequestedOnCommandLine()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length; i++)
                    if (string.Equals(args[i], CommandLineFlag, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Where the presence files live. Beside the game rather than in the
        /// save folder, so nothing in here can ever be mistaken for save data.
        /// </summary>
        public static string Folder
        {
            get
            {
                try
                {
                    var root = Directory.GetParent(Application.dataPath);
                    return Path.Combine(root != null ? root.FullName : ".", "TcgMultiplayer_LocalTest");
                }
                catch { return "TcgMultiplayer_LocalTest"; }
            }
        }

        /// <summary>
        /// Stand it up. Returns false — with a reason in <see cref="Refusal"/> —
        /// if anything at all is not right, and the caller then runs the normal
        /// Steam path as though this had never been asked for.
        ///
        /// The guest-window save block is a hard precondition, not a
        /// best-effort. Two processes sharing one save folder is how saves get
        /// corrupted, and the person running this has one copy of the game and
        /// nothing to fall back on.
        /// </summary>
        public static bool TryStart(HarmonyLib.Harmony harmony, bool requireSaveBlock,
                                    out ITransport net, out ILobbyBackend lobby)
        {
            net = null; lobby = null;
            Refusal = null;

            var transport = new LanTransport();
            if (!transport.Bind())
            {
                Refusal = transport.LastError ?? "could not bind a local port";
                transport.Dispose();
                return false;
            }

            if (!LanAddressing.IsHostSlot(transport.Slot))
            {
                // Window 2 and up should not write the save. Whether failing to
                // guarantee that is fatal depends on whether there is a save
                // worth guarding — on a machine with nothing to lose, refusing
                // to start is a safety feature that can only get in the way.
                bool blocked = Game.SaveBlock.Engage(harmony);
                if (blocked) Game.SaveBlock.ActivateForGuestWindow();

                if (!blocked)
                {
                    if (requireSaveBlock)
                    {
                        Refusal = "this window can't be stopped from writing to your save, "
                                + "so it won't start (" + Game.SaveBlock.Detail + "). "
                                + "Turn off GuestWindowMustNotSave if you have no save to protect.";
                        transport.Dispose();
                        return false;
                    }

                    Plugin.Warn("Local test mode: the save block could NOT be applied ("
                                + Game.SaveBlock.Detail + "), and you have told the mod to start "
                                + "anyway. This window CAN write to the save folder. Fine on a "
                                + "machine with nothing in it; do not do this on a real save.");
                }

                // One more layer, and cheap: a copy of the save folder, taken
                // before the guest window has had any chance to touch it.
                try { Game.SaveGuard.BackupOnce(); } catch { }
            }

            var backend = new LanLobbyBackend(transport, Folder);
            if (!backend.Start())
            {
                Refusal = "the local lobby folder could not be set up";
                transport.Dispose();
                return false;
            }

            Transport = transport;
            Lobby = backend;
            Active = true;
            net = transport;
            lobby = backend;

            Plugin.Log("LOCAL TEST MODE — " + LanAddressing.NameForSlot(transport.Slot)
                       + ". Steam is not being used for networking in this window.");
            return true;
        }

        public static void Tick()
        {
            if (Active && Lobby != null) Lobby.Tick();
        }

        public static void Stop()
        {
            try { if (Lobby != null) Lobby.LeaveLobby(LanLobbyBackend.TheLobby); } catch { }
            try { if (Transport != null) Transport.Dispose(); } catch { }
            Transport = null;
            Lobby = null;
            Active = false;
        }

        public static string Status
        {
            get
            {
                if (!Active) return Refusal != null ? "off — " + Refusal : "off";
                return LanAddressing.NameForSlot(Slot)
                     + " · " + (Lobby != null ? Lobby.LiveWindows : 0) + " window(s) seen"
                     + " · " + Game.SaveBlock.Status;
            }
        }
    }
}
