using System;
using UnityEngine;

namespace TcgMultiplayer.Game
{
    /// <summary>
    /// Does this interactable move through the world, or does it stay where the
    /// level designer put it?
    ///
    /// It matters because the two need completely different replication. A
    /// cabinet never moves, so everything inside it can be sent relative to its
    /// root and quantised into a couple of bytes. A golf cart's whole point is
    /// that it goes somewhere, and "relative to its root" tells a spectator
    /// nothing at all — which is exactly the bug this class exists to fix:
    /// before it, a friend driving the Lambo was a body gliding down the road
    /// with the car still parked where they found it.
    ///
    /// The classification is empirical: watch the root, and if it ever gets far
    /// enough from where we first saw it, it moves. No list of vehicle names.
    /// The game is PlayMaker graphs in scene assets and devotid can rename any
    /// object in any patch without it looking like a breaking change, so a
    /// hardcoded list is a thing that silently stops being true. Watching is
    /// always true.
    ///
    /// Once something is a mover it stays one. A car parked back in its bay is
    /// still a car.
    ///
    /// No Unity calls in here beyond the Vector3 struct, so the rig can sweep it.
    /// </summary>
    public static class MoverTrack
    {
        /// <summary>
        /// How far from first sight before we call it a mover. Generous on
        /// purpose: a ride's root can drift a few centimetres from a physics
        /// settle or an idle animation, and calling that a vehicle would put a
        /// pointless stream on the wire for every carousel on the island.
        /// </summary>
        public const float MoveThreshold = 2.5f;

        /// <summary>What we know about one interactable's movement so far.</summary>
        public struct Watch
        {
            public bool HasOrigin;
            public Vector3 Origin;
            /// <summary>Furthest it has ever been from where we first saw it.</summary>
            public float Furthest;
            public bool IsMover;

            public override string ToString()
            {
                if (!HasOrigin) return "unseen";
                return (IsMover ? "mover" : "fixed") + ", max " + Furthest.ToString("0.0") + "m";
            }
        }

        /// <summary>
        /// Feed the root's current world position. Returns the updated watch —
        /// a struct, so the caller keeps it and there is no hidden state here.
        /// </summary>
        public static Watch Note(Watch w, Vector3 pos)
        {
            if (!w.HasOrigin)
            {
                w.HasOrigin = true;
                w.Origin = pos;
                return w;
            }

            float d = (pos - w.Origin).magnitude;
            if (d > w.Furthest) w.Furthest = d;
            if (d >= MoveThreshold) w.IsMover = true;
            return w;
        }

        /// <summary>
        /// Where something is now, given where it was and how fast it was going.
        ///
        /// Used when a stream stalls. Capped hard: guessing is fine for the
        /// tenth of a second between packets, and awful after that — an
        /// uncapped guess sends a disconnected friend's car off through the
        /// scenery forever, which reads as a much worse bug than a car that
        /// stopped.
        /// </summary>
        public static Vector3 Extrapolate(Vector3 from, Vector3 velocity, float dt, float cap)
        {
            if (dt <= 0f) return from;
            if (cap > 0f && dt > cap) dt = cap;
            return from + velocity * dt;
        }

        /// <summary>
        /// Is this stream stale enough to stop trusting? Past this the object
        /// should be handed back to local physics rather than held in place by
        /// a mod that is no longer being told anything.
        /// </summary>
        public const float StaleSeconds = 3f;

        public static bool IsStale(float lastPacketAge) { return lastPacketAge >= StaleSeconds; }
    }
}
