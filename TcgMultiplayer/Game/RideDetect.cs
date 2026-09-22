using System;
using UnityEngine;

namespace TcgMultiplayer.Game
{
    /// <summary>
    /// Am I inside this thing?
    ///
    /// The first answer to that was "the game's own controller FSM entered Card
    /// Inserted", which is exactly right for a cabinet and wrong for everything
    /// you climb into. You do not put a card in a golf cart, you press E — so
    /// the cart was never claimed, nothing about it was ever sent, and a driver
    /// appeared to everyone else as a body gliding down the road with no vehicle
    /// under it. Meanwhile the thing that DID get claimed was CONTROLLERS, the
    /// object the player carries, because it moves.
    ///
    /// Rather than hunt for the right state name per vehicle — and re-hunt it
    /// every time devotid renames one — this watches for the only thing that is
    /// actually true of riding something: you and it move as one body. Two
    /// objects whose positions change by the same vector, several samples
    /// running, are not two objects.
    ///
    /// It needs no names, it covers vehicles and rides and anything added later,
    /// and it is pure arithmetic, so the rig can sweep it.
    /// </summary>
    public static class RideDetect
    {
        /// <summary>How often to compare. Fast enough to catch pulling away, slow enough to be free.</summary>
        public const float SampleInterval = 0.1f;

        /// <summary>
        /// The machine has to actually be going somewhere. Standing next to a
        /// parked cart also produces two identical deltas — both zero — and
        /// that must not read as being aboard it.
        /// </summary>
        public const float MinMotion = 0.03f;

        /// <summary>
        /// How far apart the two deltas may be and still count as one body.
        /// Generous: the player rig and the vehicle root are updated by
        /// different systems in the same frame, so they disagree slightly even
        /// when one is definitely carrying the other.
        /// </summary>
        public const float Tolerance = 0.12f;

        public const int NeedSamples = 4;
        public const int MaxConfidence = 10;

        /// <summary>
        /// Once aboard, stay aboard while within this of the root. Leaving is
        /// judged by distance rather than by the motion test, because a parked
        /// vehicle you are sitting in produces no motion to test.
        /// </summary>
        public const float HoldDistance = 6f;

        /// <summary>Do these two look like one thing carrying the other?</summary>
        public static bool MovesWith(Vector3 playerDelta, Vector3 machineDelta)
        {
            if (machineDelta.magnitude < MinMotion) return false;
            return (playerDelta - machineDelta).magnitude <= Tolerance;
        }

        /// <summary>
        /// Confidence rises slowly and falls fast. A false positive puts someone
        /// in a vehicle they are standing next to; a false negative just means
        /// waiting another tenth of a second.
        /// </summary>
        public static int Advance(int confidence, bool together)
        {
            if (together) return Math.Min(confidence + 1, MaxConfidence);
            return Math.Max(0, confidence - 2);
        }

        public static bool IsRiding(int confidence) { return confidence >= NeedSamples; }

        public static bool StillAboard(float distanceToRoot) { return distanceToRoot <= HoldDistance; }
    }
}
