using System;
using System.Collections.Generic;
using UnityEngine;

namespace TcgMultiplayer.Game
{
    /// <summary>
    /// When does a player start and stop occupying a machine?
    ///
    /// This looks like bookkeeping and is actually the load-bearing question in
    /// the whole mod. Nothing about a machine is sent to anyone unless somebody
    /// owns it: not the coins, not a single mirrored event. Get the end of the
    /// lease wrong and spectating does not degrade, it vanishes — which is
    /// exactly what happened.
    ///
    /// THE BUG THIS EXISTS TO STOP: "Card Removed" was read as "the player has
    /// finished". It isn't. In this game you insert your card, it is charged,
    /// and it comes straight back out — and then you play for two minutes with
    /// the card in your pocket. The lease was being handed back two seconds
    /// after it was taken, every single time, and for the rest of the round the
    /// machine was ownerless and therefore silent. In the trace:
    ///
    ///     09:59:42  Machine Treasure 04 is yours.
    ///     09:59:44  Machine free: Treasure 04          &lt;- still playing
    ///     10:01:32  (round finally ends)
    ///
    /// So leaving is only ever the states that mean leaving, plus one thing no
    /// state name can be wrong about: the player is no longer anywhere near it.
    /// </summary>
    public static class Occupancy
    {
        /// <summary>The player is now using this thing.</summary>
        private static readonly HashSet<string> Claims = new HashSet<string>(StringComparer.Ordinal)
        {
            "Card Inserted", "Turn On MECH", "Ready To Play",
        };

        /// <summary>
        /// The player is done with this thing.
        ///
        /// Note what is NOT here. "Card Removed", "Card Removed Inside" and
        /// "Card Removed Inside 2" are the card coming back out of the slot at
        /// the START of a round, not the player walking off.
        /// </summary>
        private static readonly HashSet<string> Releases = new HashSet<string>(StringComparer.Ordinal)
        {
            "Turn Off MECH", "Button Exit Machine",
            "Send Explore Mode Event", "Send Explore Mode",
            "Off of Ride and Done", "Off of Bus and Done", "PLAYER HIT EXIT",
        };

        public static bool IsClaim(string state)
        {
            return state != null && Claims.Contains(state);
        }

        public static bool IsRelease(string state)
        {
            return state != null && Releases.Contains(state);
        }

        /// <summary>
        /// How far from a machine you own you can get before we take it that
        /// you are done with it, whatever its graph did or didn't say.
        ///
        /// The backstop for every exit path we haven't named — and there will
        /// be some, across 72 controllers. Generous enough that leaning back
        /// from a cabinet, or a big ride moving you around, never trips it.
        /// </summary>
        public const float WalkAwayDistance = 12f;

        public static bool WalkedAway(float distance)
        {
            return distance > WalkAwayDistance;
        }

        /// <summary>
        /// Don't hand a machine back the instant the player steps off the mark.
        /// They lean, they get bumped, a ride swings them out and back. Only a
        /// sustained absence ends the lease.
        /// </summary>
        public const float WalkAwayGrace = 2f;
    }
}
