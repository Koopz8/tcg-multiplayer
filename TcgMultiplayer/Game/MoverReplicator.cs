using System;
using System.Collections.Generic;
using TcgMultiplayer.Net;
using UnityEngine;

namespace TcgMultiplayer.Game
{
    /// <summary>
    /// Vehicles: the golf cart, the two cars, the van, the Lambo, the superkart
    /// and the bus.
    ///
    /// The mod already had two kinds of replication and neither of them could
    /// carry a car. Event mirroring sends the machine's logic, which for a
    /// vehicle is "engine on" and nothing about where it went. The rigidbody
    /// stream sends everything inside a machine relative to that machine's own
    /// root, quantised to a millimetre over a 32-metre box — perfect for coins
    /// in a pusher, and for a car it describes the wheels beautifully while
    /// saying nothing at all about the car. So a friend driving past you was a
    /// body sliding along the road with the vehicle still parked in its bay.
    ///
    /// This is the missing third kind: the root itself, in world space, from
    /// whoever is driving. It composes with the other two rather than replacing
    /// them — this carries the car, the rigidbody stream carries the wheels
    /// relative to it, the event mirror carries the horn.
    ///
    /// Only the thing you are driving is streamed, and only while you are
    /// driving it, which is the same interest management the cabinets get for
    /// free: one machine at a time, per player.
    /// </summary>
    internal sealed class MoverReplicator
    {
        /// <summary>Owner-side send rate. Cars turn faster than coin pushers settle.</summary>
        public float SendRate = 20f;

        /// <summary>
        /// Render this far in the past, so there is always a real sample ahead
        /// to interpolate toward. Slightly longer than the avatar delay:
        /// a vehicle that stutters is much more obvious than a body that does.
        /// </summary>
        public static float InterpDelay = 0.15f;

        /// <summary>Hard cap on guessing ahead when the stream stalls.</summary>
        public const float MaxExtrapolate = 0.4f;

        public int PosesSent, PosesApplied, Movers;

        private struct Snap
        {
            public float T;
            public Vector3 Pos;
            public Quaternion Rot;
            public Vector3 Vel;
        }

        private sealed class Tracked
        {
            public readonly List<Snap> Snaps = new List<Snap>(24);
            public float LastRecv = -999f;
            public bool MadeKinematic;
            public Rigidbody Body;
        }

        private readonly Dictionary<uint, Tracked> _spectated = new Dictionary<uint, Tracked>();
        private readonly Dictionary<uint, MoverTrack.Watch> _watch = new Dictionary<uint, MoverTrack.Watch>();
        private readonly List<uint> _scratchIds = new List<uint>();

        private float _nextSendAt;
        private float _nextWatchAt;

        // Owner-side velocity, differentiated from the root rather than read off
        // a rigidbody: several of these vehicles are moved by their PlayMaker
        // graph with no rigidbody involved at all.
        private uint _velFor;
        private Vector3 _lastPos;
        private float _lastPosAt;
        private Vector3 _vel;

        // ------------------------------------------------------- classification

        /// <summary>
        /// Watch every interactable's root and notice which ones travel. Cheap
        /// enough to just do — 72 transform reads once a second is nothing —
        /// and it means no list of vehicle names to go stale.
        /// </summary>
        public void Watch(IEnumerable<Machine> machines, Transform playerRoot)
        {
            if (Time.time < _nextWatchAt) return;
            _nextWatchAt = Time.time + 1f;

            int movers = 0;
            foreach (var m in machines)
            {
                if (m == null || m.Root == null) continue;

                // Anything carried by the player moves exactly as much as the
                // player does, which is a lot. Streaming its position to
                // everyone would be describing our own walk in the most
                // expensive way available.
                if (playerRoot != null && m.Root.IsChildOf(playerRoot)) continue;

                MoverTrack.Watch w;
                _watch.TryGetValue(m.Id, out w);
                w = MoverTrack.Note(w, m.Moving.position);
                _watch[m.Id] = w;

                if (w.IsMover && !m.IsMover)
                {
                    m.IsMover = true;
                    Measure(m);
                    Plugin.Log("Mover: " + m.Label + " travels, so its position will be shared."
                               + (m.Rideable
                                  ? " Seats " + m.Capacity + "."
                                  : " Too small to ride."));
                }
                // Only ever set it, never clear it. Clearing is what produced
                // "Mover: CONTROLLERS travels (its driver said so)" once a
                // second forever: a watcher's own copy of a vehicle never moves
                // under its own power, so its local watch says "not a mover"
                // and stamped that over what the driver had just told us.
                if (w.IsMover) { m.IsMover = true; movers++; }
                else if (m.IsMover) movers++;
            }
            Movers = movers;
        }

        /// <summary>
        /// How big the thing is, in its own frame. Only used to decide how many
        /// people fit and roughly where they sit, so renderer bounds are plenty
        /// — and renderers are the one thing every vehicle in this game
        /// definitely has, unlike colliders, seat markers or a consistent name.
        /// </summary>
        public static void Measure(Machine m)
        {
            if (m == null || m.Root == null) return;
            m.MeasuredExtents = true;

            try
            {
                var target = m.Moving;
                if (target == null) return;
                // Mesh renderers only. A ParticleSystemRenderer's bounds cover
                // wherever its particles have drifted, and the cart's exhaust
                // smoke measured it as nine metres tall - which then said the
                // golf cart seats eight.
                var rends = target.GetComponentsInChildren<Renderer>();
                if (rends == null || rends.Length == 0) return;

                bool any = false;
                Vector3 min = Vector3.zero, max = Vector3.zero;

                for (int i = 0; i < rends.Length; i++)
                {
                    if (rends[i] == null) continue;

                    // Mesh renderers only. A ParticleSystemRenderer's bounds
                    // cover wherever its particles have drifted, and the cart's
                    // exhaust smoke measured the thing as nine metres tall —
                    // which then declared that a golf cart seats eight.
                    if (!(rends[i] is MeshRenderer) && !(rends[i] is SkinnedMeshRenderer)) continue;

                    var b = rends[i].bounds;
                    var c = b.center;
                    var e = b.extents;

                    // World AABB corners, pulled into the vehicle's own frame, so
                    // a car parked at an angle doesn't measure as a wide one.
                    for (int k = 0; k < 8; k++)
                    {
                        var corner = new Vector3(
                            c.x + ((k & 1) == 0 ? -e.x : e.x),
                            c.y + ((k & 2) == 0 ? -e.y : e.y),
                            c.z + ((k & 4) == 0 ? -e.z : e.z));
                        var local = target.InverseTransformPoint(corner);

                        if (!any) { min = max = local; any = true; continue; }
                        min = Vector3.Min(min, local);
                        max = Vector3.Max(max, local);
                    }
                }

                if (!any) return;
                var size = (max - min) * 0.5f;
                m.Extents = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
            }
            catch (Exception ex) { Plugin.Warn("Could not measure " + m.Label + ": " + ex.Message); }
        }

        public MoverTrack.Watch WatchOf(uint id)
        {
            MoverTrack.Watch w;
            _watch.TryGetValue(id, out w);
            return w;
        }

        public void ForgetClassification() { _watch.Clear(); }

        // -------------------------------------------------------------- sending

        public bool ShouldSend(float now)
        {
            if (now < _nextSendAt) return false;
            _nextSendAt = now + 1f / Mathf.Max(1f, SendRate);
            return true;
        }

        /// <summary>The pose of something we are driving, for the wire.</summary>
        public Session.ObjectPose Pack(Machine m)
        {
            var pose = new Session.ObjectPose();
            if (m == null || m.Root == null) return pose;

            // The body, not the root. On a vehicle the root is the control
            // panel the card-reader FSM lives on, and sending its pose moved a
            // 5cm object around the island while the cart stayed parked.
            var body = m.Moving;
            if (body == null) return pose;

            var pos = body.position;
            float now = Time.time;

            if (_velFor != m.Id) { _velFor = m.Id; _lastPos = pos; _lastPosAt = now; _vel = Vector3.zero; }
            else
            {
                float dt = now - _lastPosAt;
                if (dt > 0.01f)
                {
                    _vel = (pos - _lastPos) / dt;
                    _lastPos = pos;
                    _lastPosAt = now;
                }
            }

            pose.Pos = pos;
            pose.Rot = body.rotation;
            pose.Vel = _vel;

            // Which object this pose is about. The watcher can't work it out —
            // it takes seeing what moves with you, and they aren't moving.
            pose.BodyUp = (byte)Mathf.Clamp(m.BodyUp, 0, 8);

            PosesSent++;
            return pose;
        }

        // ------------------------------------------------------------ receiving

        public void Push(Machine m, Session.ObjectPose pose)
        {
            if (m == null || m.Root == null) return;

            // Adopt the driver's answer about which object actually travels.
            // Without this the watcher applies the pose to the machine root,
            // which on a vehicle is the control panel bolted to it — the cart
            // never moves and a tiny invisible object tours the island.
            if (m.Body == null && pose.BodyUp > 0)
            {
                var up = m.Root;
                for (int i = 0; i < pose.BodyUp && up != null && up.parent != null; i++) up = up.parent;

                if (up != null && up != m.Root)
                {
                    m.Body = up;
                    m.BodyUp = pose.BodyUp;
                    m.MeasuredExtents = false;
                    Measure(m);
                    Plugin.Log("Mover: " + m.Label + " really moves as " + NetId.Path(up)
                               + " (its driver said so). extents " + m.Extents
                               + ", seats " + m.Capacity + ", rideable " + m.Rideable + ".");
                }
            }

            if (m.Moving == null) return;

            Tracked t;
            if (!_spectated.TryGetValue(m.Id, out t))
            {
                t = new Tracked();
                _spectated[m.Id] = t;
            }

            // Whatever was driving this locally has to stop, or the game's own
            // graph and the incoming stream fight over the same transform and
            // the car shakes itself apart.
            if (!t.MadeKinematic)
            {
                t.MadeKinematic = true;
                t.Body = m.Moving.GetComponent<Rigidbody>();
                if (t.Body != null) t.Body.isKinematic = true;
            }

            t.LastRecv = Time.time;
            t.Snaps.Add(new Snap { T = t.LastRecv, Pos = pose.Pos, Rot = pose.Rot, Vel = pose.Vel });
            while (t.Snaps.Count > 2 && t.Snaps[0].T < t.LastRecv - 1f) t.Snaps.RemoveAt(0);
            PosesApplied++;
        }

        /// <summary>
        /// Drive every spectated root toward where its owner says it is.
        /// Called every frame, before the rigidbody stream renders, so the
        /// wheels land relative to a body that is already in the right place.
        /// </summary>
        public void Render(Func<uint, Machine> lookup)
        {
            if (_spectated.Count == 0) return;

            float now = Time.time;
            _scratchIds.Clear();

            foreach (var kv in _spectated)
            {
                var t = kv.Value;

                // Nobody has told us anything for a long time. Holding a car
                // still forever because its driver crashed out is worse than
                // handing it back to the game.
                if (MoverTrack.IsStale(now - t.LastRecv)) { _scratchIds.Add(kv.Key); continue; }

                var m = lookup(kv.Key);
                if (m == null || m.Moving == null) { _scratchIds.Add(kv.Key); continue; }
                if (t.Snaps.Count == 0) continue;

                Vector3 pos; Quaternion rot;
                Resolve(t, now, out pos, out rot);

                // Moving, NOT Root. This line is why the cart never moved on the
                // watching screen even after both sides had correctly worked out
                // that GOLFCART_Vehicle is the thing that travels: the answer was
                // right and then thrown away here, and the 5cm control panel got
                // the pose instead.
                m.Moving.position = pos;
                m.Moving.rotation = rot;
            }

            for (int i = 0; i < _scratchIds.Count; i++) Release(_scratchIds[i]);
        }

        private static void Resolve(Tracked t, float now, out Vector3 pos, out Quaternion rot)
        {
            float renderAt = now - InterpDelay;
            var last = t.Snaps[t.Snaps.Count - 1];

            if (t.Snaps.Count == 1 || renderAt >= last.T)
            {
                // Ahead of the newest sample: guess, briefly. A car at 15 m/s
                // covers a quarter of a metre between packets, and freezing it
                // for that quarter-second reads as a stutter on every packet.
                pos = MoverTrack.Extrapolate(last.Pos, last.Vel, renderAt - last.T, MaxExtrapolate);
                rot = last.Rot;
                return;
            }

            if (renderAt <= t.Snaps[0].T) { pos = t.Snaps[0].Pos; rot = t.Snaps[0].Rot; return; }

            for (int i = 0; i < t.Snaps.Count - 1; i++)
            {
                var a = t.Snaps[i];
                var b = t.Snaps[i + 1];
                if (renderAt < a.T || renderAt > b.T) continue;

                float span = b.T - a.T;
                float k = span > 0.0001f ? (renderAt - a.T) / span : 1f;
                pos = Vector3.Lerp(a.Pos, b.Pos, k);
                rot = Quaternion.Slerp(a.Rot, b.Rot, k);
                return;
            }

            pos = last.Pos; rot = last.Rot;
        }

        /// <summary>Hands a vehicle back to the game.</summary>
        public void Release(uint machineId)
        {
            Tracked t;
            if (!_spectated.TryGetValue(machineId, out t)) return;
            if (t.Body != null) t.Body.isKinematic = false;
            _spectated.Remove(machineId);
        }

        public void ReleaseAll()
        {
            var ids = new List<uint>(_spectated.Keys);
            for (int i = 0; i < ids.Count; i++) Release(ids[i]);
        }

        public int SpectatedMovers { get { return _spectated.Count; } }

        public bool IsSpectating(uint machineId) { return _spectated.ContainsKey(machineId); }

        public float AgeOf(uint machineId)
        {
            Tracked t;
            if (!_spectated.TryGetValue(machineId, out t)) return -1f;
            return Time.time - t.LastRecv;
        }
    }
}
