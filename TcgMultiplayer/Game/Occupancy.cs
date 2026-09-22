using System;
using System.Collections.Generic;

namespace TcgMultiplayer.Game
{
    /// <summary>
    /// When does a player start and stop occupying a machine?
    ///
    /// This looks like bookkeeping and is actually the load-bearing question in
    /// the whole mod. Nothing about a machine is sent to anyone unless somebody
    /// owns it: not the coins, not a single mirrored event. Get the end of the
    /// lease wrong and spectating does not degrade, it vanishes.
    ///
    /// It was got wrong twice, in opposite directions, and both times the
    /// symptom was "watching does nothing" rather than anything that looked
    /// like a lease bug:
    ///
    ///   1. "Card Removed" was read as the player finishing. It isn't — the
    ///      card is charged and comes straight back out at the START of a
    ///      round.
    ///   2. With those removed, something else in the list still fired about a
    ///      second later. Same two-second lease, same silent round:
    ///
    ///          10:08:19.99  Machine Treasure 04 is yours.
    ///          10:08:22.17  Machine free: Treasure 04     &lt;- still playing
    ///
    /// Twice is a pattern, and the pattern is that these names describe what
    /// the CABINET is doing, not what the PLAYER is doing. A machine handing
    /// control back to you so you can play it looks, in state names, exactly
    /// like a machine you have walked away from.
    ///
    /// So the lease no longer ends on a state name. It ends on the one thing
    /// no name can be wrong about: you are not there any more.
    /// </summary>
    public static class Occupancy
    {
        /// <summary>
        /// The player is now using this thing. Claiming on a name is fine —
        /// the cost of a wrong claim is bounded and obvious, and in the traces
        /// these have always fired exactly when someone started playing.
        /// </summary>
        private static readonly HashSet<string> Claims = new HashSet<string>(StringComparer.Ordinal)
        {
            "Card Inserted", "Turn On MECH", "Ready To Play",
        };

        public static bool IsClaim(string state)
        {
            return state != null && Claims.Contains(state);
        }

        /// <summary>
        /// States that sound like leaving. Kept because they are worth knowing
        /// about, and used for nothing but a note in the log — every time one
        /// of these has been given authority over the lease it has ended a
        /// round that was still being played.
        /// </summary>
        private static readonly HashSet<string> Departures = new HashSet<string>(StringComparer.Ordinal)
        {
            "Card Removed", "Card Removed Inside", "Card Removed Inside 2",
            "Turn Off MECH", "Button Exit Machine",
            "Send Explore Mode Event", "Send Explore Mode",
            "Off of Ride and Done", "Off of Bus and Done", "PLAYER HIT EXIT",
        };

        public static bool LooksLikeLeaving(string state)
        {
            return state != null && Departures.Contains(state);
        }

        /// <summary>
        /// How far from a machine you hold you can get before we take it that
        /// you are done with it.
        ///
        /// Generous: leaning back from a cabinet, stepping aside to let someone
        /// see, or a ride swinging you around must never trip it. Walking to
        /// the next machine must.
        /// </summary>
        public const float WalkAwayDistance = 12f;

        public static bool WalkedAway(float distance)
        {
            return distance > WalkAwayDistance;
        }

        /// <summary>
        /// And it has to last. A single frame in which the player is measured
        /// somewhere odd — mid-teleport, mid-scene-load — is not someone
        /// leaving.
        /// </summary>
        public const float WalkAwayGrace = 2f;
    }
}
