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

        /// <summary>
        /// GameObjects we switched on so a streamed body would draw, per machine,
        /// in the order we switched them on. Put back exactly on release.
        /// </summary>
        private readonly Dictionary<uint, List<GameObject>> _switchedOn = new Dictionary<uint, List<GameObject>>();

        /// <summary>Bodies already counted toward the on-screen figures, so each is counted once.</summary>
        private readonly Dictionary<uint, HashSet<Rigidbody>> _counted = new Dictionary<uint, HashSet<Rigidbody>>();
        private readonly HashSet<uint> _visibilitySaid = new HashSet<uint>();

        /// <summary>The gap we last reported per machine, so a CHANGING one is said again.</summary>
        private readonly Dictionary<uint, int> _mismatchSaid = new Dictionary<uint, int>();

        public int BodiesSent, BodiesApplied, CountMismatches;

        /// <summary>
        /// Of the bodies we are placing: how many were drawing already when the
        /// first pose for them arrived, how many were switched off and we turned
        /// on, and how many have no renderer under them at all. This is the
        /// measurement 0.11.7 exists for — see the note in Unpack.
        /// </summary>
        public int PlacedAlreadyOnScreen, PlacedSwitchedOn, PlacedNothingToDraw;

        /// <summary>
        /// Is the stream actually carrying motion? Sender side: of the bodies in
        /// the last packet, how many had moved more than a millimetre since the
        /// packet before. Receiver side: of the poses in the last packet, how
        /// many differed from the previous packet's. A pusher being played has
        /// dozens of coins on the move every frame; a stream that says 0 here
        /// while someone plays is a stream of furniture, and the coins live
        /// somewhere the walk doesn't reach.
        /// </summary>
        public int SentMovedLastPacket, SentBodiesLastPacket, PacketsSent;
        public int RecvChangedLastPacket, RecvPosesLastPacket, PacketsReceived;
        public int SentMovedPeak, RecvChangedPeak;

        private readonly List<Vector3> _lastSentPos = new List<Vector3>(128);
        private uint _lastSentMachine;
        private readonly Dictionary<uint, List<Vector3>> _lastRecvPos = new Dictionary<uint, List<Vector3>>();
        private readonly HashSet<uint> _describedRoot = new HashSet<uint>();
        private float _nextSendSummaryAt, _nextRecvSummaryAt;

        /// <summary>
        /// One line per machine per session about what the walk actually found
        /// under its root: each top-level child, how many rigidbodies it holds,
        /// and the first few bodies by name. Said on both ends, because whether
        /// the two walks are looking at the same objects is the whole question.
        /// </summary>
        private void DescribeRoot(Machine m, List<Rigidbody> bodies, string side)
        {
            if (!_describedRoot.Add(m.Id)) return;
            var sb = new System.Text.StringBuilder();
            sb.Append(side).Append(" walk of ").Append(m.Label).Append(": ").Append(bodies.Count).Append(" bodies. ");
            for (int i = 0; i < m.Root.childCount; i++)
            {
                var c = m.Root.GetChild(i);
                int count = 0;
                for (int j = 0; j < bodies.Count; j++)
                    if (bodies[j] != null && bodies[j].transform.IsChildOf(c)) count++;
                if (count == 0) continue;
                sb.Append(c.name).Append(c.gameObject.activeInHierarchy ? "" : " (off)").Append('=').Append(count).Append("  ");
            }
            sb.Append("First: ");
            for (int i = 0; i < bodies.Count && i < 6; i++)
                if (bodies[i] != null) sb.Append(bodies[i].name).Append(bodies[i].isKinematic ? "[k] " : " ");
            Plugin.Log(sb.ToString());
        }
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
            DescribeRoot(m, _bodies, "Owner");

            var origin = m.Root.position;
            var inv = Quaternion.Inverse(m.Root.rotation);

            if (_lastSentMachine != m.Id) { _lastSentPos.Clear(); _lastSentMachine = m.Id; }
            int moved = 0;

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
                if (i < _lastSentPos.Count)
                {
                    if ((lp - _lastSentPos[i]).sqrMagnitude > 0.001f * 0.001f) moved++;
                    _lastSentPos[i] = lp;
                }
                else _lastSentPos.Add(lp);

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
            PacketsSent++;
            SentBodiesLastPacket = _bodies.Count;
            SentMovedLastPacket = moved;
            if (moved > SentMovedPeak) SentMovedPeak = moved;
            float now = Time.time;
            if (now >= _nextSendSummaryAt)
            {
                _nextSendSummaryAt = now + 5f;
                Plugin.Log("Streaming " + m.Label + ": " + moved + " of " + _bodies.Count
                           + " bodies moved in the last packet (peak " + SentMovedPeak + ", " + PacketsSent + " packets).");
            }
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
            DescribeRoot(m, _scratch, "Watcher");

            List<Vector3> lastRecv;
            if (!_lastRecvPos.TryGetValue(m.Id, out lastRecv))
            {
                lastRecv = new List<Vector3>(count);
                _lastRecvPos[m.Id] = lastRecv;
            }
            int changed = 0;

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

                // Re-said when the gap MOVES, not once per machine. A gap that
                // grows round after round is the watcher accumulating parts of
                // its own, which is a different fault from the two sides simply
                // holding different numbers of coins — and saying it once hides
                // exactly that.
                int gap = _scratch.Count - count;
                int said;
                if (!_mismatchSaid.TryGetValue(m.Id, out said) || Mathf.Abs(gap - said) >= 16)
                {
                    _mismatchSaid[m.Id] = gap;
                    Plugin.Log("Physics: " + m.Label + " has " + _scratch.Count
                               + " moving parts here and " + count + " on the player's screen. "
                               + "Driving the " + n + " they share.");
                }
            }

            bodies.Clear();
            bodies.AddRange(_scratch);

            // EVERYTHING under a spectated machine stops simulating, not only
            // the parts we have a pose for.
            //
            // The first version froze the driven ones and left the rest alone,
            // on the grounds that a coin nobody is sending us a pose for should
            // not hang in the air. That was the wrong worry. What the spare
            // ones actually do is keep falling, keep triggering the machine's
            // own collectors, and keep the watcher's copy of the round running
            // — and since the mirror is replaying the owner's events into that
            // same machine, the watcher spawns its own coins on top. In the
            // trace the watcher held 590 moving parts to the owner's 334, and
            // its FSM count climbed 268 -> 712 -> 925 -> 1246 across three
            // rounds without ever coming down.
            //
            // A body we cannot place is better still than moving on its own:
            // still is at least consistent with a machine somebody else is
            // playing, and it stops the two copies drifting further apart
            // every second.
            for (int i = 0; i < bodies.Count; i++)
                if (bodies[i] != null && !bodies[i].isKinematic) bodies[i].isKinematic = true;
            _madeKinematic.Add(m.Id);

            // Fifth answer to "the watcher sees nothing happen in the cabinet".
            //
            // By 0.11.6 the frames were arriving, being applied, and landing on
            // bodies that were no longer fighting them — and the tray still did
            // not move. The owner's log has the pusher doing "Load Objects From
            // Array" on card insert and "Save Objects To Array" on the way out:
            // a pusher puts its coins AWAY between rounds. The watcher never
            // inserts a card, so on its side the coins are still stored — the
            // Rigidbody is there for the walk to find and for us to move, and
            // the GameObject it sits on is switched off, so nothing draws.
            //
            // So switch on what we have a pose for (and any parent between it
            // and the machine root, since a whole container can be off), and
            // COUNT: how many were already drawing, how many we turned on, and
            // how many have nothing to draw regardless. That's what the panel
            // line is for. Mostly switched-off means this was the fault. Mostly
            // already-on-screen and still nothing means the hypothesis is wrong
            // and the physics path is not where the answer is.
            //
            // None of the machine's own logic runs to do this — no event, no
            // state, just SetActive on the object we're already moving. They
            // go back exactly as they were on release.
            HashSet<Rigidbody> counted;
            if (!_counted.TryGetValue(m.Id, out counted))
            {
                counted = new HashSet<Rigidbody>();
                _counted[m.Id] = counted;
            }
            List<GameObject> switchedOn;
            if (!_switchedOn.TryGetValue(m.Id, out switchedOn))
            {
                switchedOn = new List<GameObject>();
                _switchedOn[m.Id] = switchedOn;
            }
            int onScreen = 0, turnedOn = 0, undrawable = 0;
            for (int i = 0; i < n; i++)
            {
                var rb = bodies[i];
                if (rb == null || !counted.Add(rb)) continue;

                bool wasDrawing = rb.gameObject.activeInHierarchy;
                var t = rb.transform;
                while (t != null && t != m.Root)
                {
                    if (!t.gameObject.activeSelf)
                    {
                        t.gameObject.SetActive(true);
                        switchedOn.Add(t.gameObject);
                    }
                    t = t.parent;
                }

                if (rb.GetComponentInChildren<Renderer>(true) == null) undrawable++;
                else if (wasDrawing) onScreen++;
                else turnedOn++;
            }
            PlacedAlreadyOnScreen += onScreen;
            PlacedSwitchedOn += turnedOn;
            PlacedNothingToDraw += undrawable;

            if (_visibilitySaid.Add(m.Id))
                Plugin.Log("Placing " + n + " bodies in " + m.Label + ": " + onScreen + " were already on screen, "
                           + turnedOn + " were switched off and we turned on"
                           + (undrawable > 0 ? ", " + undrawable + " have nothing to draw" : "") + ".");

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

                if (i < lastRecv.Count)
                {
                    if ((lp - lastRecv[i]).sqrMagnitude > 0.001f * 0.001f) changed++;
                    lastRecv[i] = lp;
                }
                else lastRecv.Add(lp);

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
            PacketsReceived++;
            RecvPosesLastPacket = count;
            RecvChangedLastPacket = changed;
            if (changed > RecvChangedPeak) RecvChangedPeak = changed;
            if (now >= _nextRecvSummaryAt)
            {
                _nextRecvSummaryAt = now + 5f;
                Plugin.Log("Watching " + m.Label + ": " + changed + " of " + count
                           + " incoming poses changed in the last packet (peak " + RecvChangedPeak + ", "
                           + PacketsReceived + " packets, placing " + n + ").");
            }
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

            // Whatever we switched on goes back off, newest first, so a parent
            // we turned on after its child is switched off after it too.
            List<GameObject> switchedOn;
            if (_switchedOn.TryGetValue(machineId, out switchedOn))
            {
                for (int i = switchedOn.Count - 1; i >= 0; i--)
                    if (switchedOn[i] != null) switchedOn[i].SetActive(false);
                _switchedOn.Remove(machineId);
            }
            _counted.Remove(machineId);
            _visibilitySaid.Remove(machineId);
            _lastRecvPos.Remove(machineId);
        }

        public void ReleaseAll()
        {
            var ids = new List<uint>(_spectated.Keys);
            foreach (var id in ids) ReleaseMachine(id);
        }

        public int SpectatedMachines { get { return _spectated.Count; } }

        /// <summary>
        /// Are we being sent this machine's moving parts?
        ///
        /// Asked by the event mirror, which must keep its hands off a machine
        /// whose contents arrive over the wire — see the note in Unpack about
        /// a watcher running its own round.
        /// </summary>
        public bool IsSpectating(uint machineId) { return _spectated.ContainsKey(machineId); }

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
