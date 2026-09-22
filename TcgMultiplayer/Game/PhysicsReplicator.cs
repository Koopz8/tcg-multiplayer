using System;
using System.Collections.Generic;
using UnityEngine;

namespace TcgMultiplayer.Game
{
    /// <summary>
    /// The moving parts inside a machine: the claw arm, the puck, the pins, and
    /// above all the coins.
    ///
    /// Event mirroring already carries the machine's *logic*, which is enough for
    /// the cabinets with no rigidbodies at all (Speed Drop, Big Bass, the vending
    /// and bulk-candy machines). It is not enough for a claw or a pusher, where
    /// the whole point is where the physical objects ended up — and PhysX is not
    /// deterministic across machines, so both sides simulating the same round get
    /// different answers within a second.
    ///
    /// So the owner simulates and everyone else watches: rigidbodies under the
    /// owned machine are snapshotted, quantised and sent; spectators go kinematic
    /// and interpolate. The measured worst case is 100 bodies (Claw Machine Balls)
    /// and 63-66 coins per pusher — at 14 bytes a body, 20 Hz, that is around
    /// 18 KB/s for the busiest machine in the game, and only while someone is
    /// actually playing it.
    ///
    /// Addressing is by ordinal in a deterministic traversal, not by path: coins
    /// are runtime clones and share a name, so paths cannot tell them apart.
    ///
    /// The two sides will not hold the same number of coins — they are different
    /// save files that have been played different amounts — and the first version
    /// treated that as corruption and dropped the frame. It dropped every frame
    /// ever sent. The counts are not going to converge, so the overlap is driven
    /// and the difference is reported: coins are interchangeable, and a tray that
    /// moves is the entire point.
    /// </summary>
    internal sealed class PhysicsReplicator
    {
        public const float PosScale = 1000f;      // 1 mm, ±32.7 m from the machine root
        public const float RotScale = 32767f;

        private readonly List<Rigidbody> _bodies = new List<Rigidbody>(128);
        private readonly List<Rigidbody> _scratch = new List<Rigidbody>(128);

        // Spectator side
        private sealed class Target
        {
            public Vector3 Pos;
            public Quaternion Rot;
            public Vector3 FromPos;
            public Quaternion FromRot;
            public float At;
        }
        private readonly Dictionary<uint, List<Target>> _targets = new Dictionary<uint, List<Target>>();
        private readonly Dictionary<uint, List<Rigidbody>> _spectated = new Dictionary<uint, List<Rigidbody>>();
        private readonly HashSet<uint> _madeKinematic = new HashSet<uint>();

        /// <summary>Machines we have already said the body counts differ on. Once each is plenty.</summary>
        private readonly HashSet<uint> _mismatchLogged = new HashSet<uint>();

        public int BodiesSent, BodiesApplied, CountMismatches;
        public float LastPacketBytes;
        public float SendRate = 20f;
        public float InterpDelay = 0.1f;

        private float _nextSendAt;

        // ------------------------------------------------------------ gathering

        /// <summary>
        /// Depth-first, in sibling order — the same walk on both machines, so
        /// index N means the same coin on both. Sleeping bodies are included:
        /// a coin that has settled still has to be in the right place for a
        /// spectator who only just walked up.
        /// </summary>
        public static void Gather(Transform root, List<Rigidbody> into)
        {
            into.Clear();
            if (root == null) return;

            // The root's OWN rigidbody is deliberately skipped. Everything here
            // is expressed relative to the root, so the root relative to itself
            // is always the origin — harmless for a cabinet, and actively wrong
            // for a vehicle, where writing that back pins the car to wherever it
            // was when the packet was unpacked and undoes the world-space stream
            // that is carrying it. Both ends skip it, so the indices still line
            // up coin for coin.
            for (int i = 0; i < root.childCount; i++) Walk(root.GetChild(i), into);
        }

        private static void Walk(Transform t, List<Rigidbody> into)
        {
            var rb = t.GetComponent<Rigidbody>();
            if (rb != null) into.Add(rb);
            for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), into);
        }

        // -------------------------------------------------------------- sending

        public bool ShouldSend(float now)
        {
            if (now < _nextSendAt) return false;
            _nextSendAt = now + 1f / Mathf.Max(1f, SendRate);
            return true;
        }

        /// <summary>Packs the machine's bodies relative to its own root, so the numbers stay small.</summary>
        public byte[] Pack(Machine m)
        {
            if (m == null || m.Root == null) return null;
            Gather(m.Root, _bodies);
            if (_bodies.Count == 0) return null;

            var origin = m.Root.position;
            var inv = Quaternion.Inverse(m.Root.rotation);

            var buf = new byte[4 + _bodies.Count * 14];
            int o = 0;
            WriteU16(buf, ref o, (ushort)_bodies.Count);
            WriteU16(buf, ref o, 0);   // reserved: flags / future compression

            for (int i = 0; i < _bodies.Count; i++)
            {
                var rb = _bodies[i];
                Vector3 lp; Quaternion lr;
                if (rb == null) { lp = Vector3.zero; lr = Quaternion.identity; }
                else
                {
                    lp = inv * (rb.transform.position - origin);
                    lr = inv * rb.transform.rotation;
                }

                WriteI16(buf, ref o, Quant(lp.x, PosScale));
                WriteI16(buf, ref o, Quant(lp.y, PosScale));
                WriteI16(buf, ref o, Quant(lp.z, PosScale));
                WriteI16(buf, ref o, Quant(lr.x, RotScale));
                WriteI16(buf, ref o, Quant(lr.y, RotScale));
                WriteI16(buf, ref o, Quant(lr.z, RotScale));
                WriteI16(buf, ref o, Quant(lr.w, RotScale));
            }

            BodiesSent += _bodies.Count;
            LastPacketBytes = buf.Length;
            return buf;
        }

        // ------------------------------------------------------------ receiving

        public void Unpack(Machine m, byte[] data)
        {
            if (m == null || m.Root == null || data == null || data.Length < 4) return;

            int o = 0;
            int count = ReadU16(data, ref o);
            ReadU16(data, ref o);
            if (data.Length < 4 + count * 14) return;

            List<Rigidbody> bodies;
            if (!_spectated.TryGetValue(m.Id, out bodies))
            {
                bodies = new List<Rigidbody>(count);
                _spectated[m.Id] = bodies;
            }
            Gather(m.Root, _scratch);

            // The two sides rarely agree about how many things are inside a
            // machine, and the original answer to that was to throw the whole
            // frame away. On the panel that read:
            //
            //     physics: 0 bodies sent, 0 applied · 38 count mismatches
            //
            // Nothing had ever been applied. The assumption was that the event
            // mirror keeps the counts equal; it doesn't, and it can't. Two
            // copies of the game have been played different amounts, so their
            // pushers hold different numbers of coins — and the mismatch is not
            // a transient a retry fixes, it is the steady state.
            //
            // So drive what both sides have. A pusher's coins are
            // interchangeable: nobody can tell which local coin took which
            // remote coin's place, only whether the tray looks right. Where the
            // watcher has spare coins the stream doesn't reach, those keep
            // simulating locally, which looks like coins rather than like a
            // bug.
            int n = _scratch.Count < count ? _scratch.Count : count;
            if (_scratch.Count != count)
            {
                CountMismatches++;
                if (_mismatchLogged.Add(m.Id))
                    Plugin.Log("Physics: " + m.Label + " has " + _scratch.Count
                               + " moving parts here and " + count + " on the player's screen. "
                               + "Driving the " + n + " they share.");
            }

            bodies.Clear();
            bodies.AddRange(_scratch);

            // Spectated bodies must stop simulating, or local physics fights the
            // incoming stream and everything jitters. Only the ones we actually
            // drive, though: freezing a coin nothing is sending us a pose for
            // leaves it hanging in the air for the rest of the round.
            for (int i = 0; i < n; i++)
                if (bodies[i] != null && !bodies[i].isKinematic) bodies[i].isKinematic = true;
            _madeKinematic.Add(m.Id);

            List<Target> targets;
            if (!_targets.TryGetValue(m.Id, out targets))
            {
                targets = new List<Target>(count);
                _targets[m.Id] = targets;
            }
            while (targets.Count < n) targets.Add(new Target());

            var origin = m.Root.position;
            var rot = m.Root.rotation;
            float now = Time.time;

            for (int i = 0; i < count; i++)
            {
                // Every body's 14 bytes still has to be read to stay in step
                // with the buffer, even the ones we have nowhere to put.
                var lp = new Vector3(
                    Dequant(ReadI16(data, ref o), PosScale),
                    Dequant(ReadI16(data, ref o), PosScale),
                    Dequant(ReadI16(data, ref o), PosScale));
                var lr = new Quaternion(
                    Dequant(ReadI16(data, ref o), RotScale),
                    Dequant(ReadI16(data, ref o), RotScale),
                    Dequant(ReadI16(data, ref o), RotScale),
                    Dequant(ReadI16(data, ref o), RotScale));

                if (i >= n) continue;

                var t = targets[i];
                var rb = bodies[i];
                t.FromPos = rb != null ? rb.transform.position : origin;
                t.FromRot = rb != null ? rb.transform.rotation : rot;
                t.Pos = origin + rot * lp;
                t.Rot = rot * Normalise(lr);
                t.At = now;
            }

            BodiesApplied += n;
        }

        /// <summary>Eases each body toward its last received pose; called every frame.</summary>
        public void Render()
        {
            if (_targets.Count == 0) return;
            float now = Time.time;

            foreach (var kv in _targets)
            {
                List<Rigidbody> bodies;
                if (!_spectated.TryGetValue(kv.Key, out bodies)) continue;
                var targets = kv.Value;

                for (int i = 0; i < bodies.Count && i < targets.Count; i++)
                {
                    var rb = bodies[i];
                    if (rb == null) continue;
                    var t = targets[i];

                    float k = InterpDelay <= 0f ? 1f : Mathf.Clamp01((now - t.At) / InterpDelay);
                    rb.transform.position = Vector3.Lerp(t.FromPos, t.Pos, k);
                    rb.transform.rotation = Quaternion.Slerp(t.FromRot, t.Rot, k);
                }
            }
        }

        /// <summary>Hands the machine back to local physics when we stop spectating it.</summary>
        public void ReleaseMachine(uint machineId)
        {
            List<Rigidbody> bodies;
            if (_spectated.TryGetValue(machineId, out bodies))
            {
                for (int i = 0; i < bodies.Count; i++)
                    if (bodies[i] != null) bodies[i].isKinematic = false;
                _spectated.Remove(machineId);
            }
            _targets.Remove(machineId);
            _madeKinematic.Remove(machineId);
        }

        public void ReleaseAll()
        {
            var ids = new List<uint>(_spectated.Keys);
            foreach (var id in ids) ReleaseMachine(id);
        }

        public int SpectatedMachines { get { return _spectated.Count; } }

        // -------------------------------------------------------------- packing

        private static short Quant(float v, float scale)
        {
            return (short)Mathf.Clamp(Mathf.RoundToInt(v * scale), short.MinValue, short.MaxValue);
        }

        private static float Dequant(short v, float scale) { return v / scale; }

        private static Quaternion Normalise(Quaternion q)
        {
            float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (m < 0.0001f) return Quaternion.identity;
            return new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m);
        }

        private static void WriteU16(byte[] b, ref int o, ushort v) { b[o++] = (byte)(v & 0xFF); b[o++] = (byte)(v >> 8); }
        private static void WriteI16(byte[] b, ref int o, short v) { WriteU16(b, ref o, unchecked((ushort)v)); }
        private static ushort ReadU16(byte[] b, ref int o) { ushort v = (ushort)(b[o] | (b[o + 1] << 8)); o += 2; return v; }
        private static short ReadI16(byte[] b, ref int o) { return unchecked((short)ReadU16(b, ref o)); }
    }
}
