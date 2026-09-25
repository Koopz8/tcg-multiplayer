using System;
using System.Collections.Generic;
using System.Text;
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
    /// and 63-66 coins per pusher — at 15 bytes a body, 20 Hz, that is around
    /// 18 KB/s for the busiest machine in the game, and only while someone is
    /// actually playing it.
    ///
    /// Addressing was by ordinal in a deterministic traversal, on the grounds
    /// that coins are runtime clones sharing a name, so a path can't tell them
    /// apart. The ordinal broke the moment the two walks differed at the FRONT:
    /// the owner's walk starts with two bodies under POWER CNTRLR — their own
    /// card and its ring clasp, parented into the reader on insert — that the
    /// watcher does not have. So the watcher's body 0, the pusher arm, took the
    /// card's pose, and the arm appeared outside the cabinet at the card slot.
    ///
    /// Now each body has a key: its path under the machine root, with an
    /// ordinal only where siblings share a name ("OBJECTS/CHST_1(Clone)#17").
    /// The owner sends the key list as a manifest with the first packet, again
    /// whenever the list changes, and every few seconds as a keyframe; every
    /// packet in between is poses in manifest order. The watcher resolves the
    /// manifest against its own walk and drives what it can match. Coins are
    /// still interchangeable — coin #17 here stands in for coin #17 there —
    /// but the card only matches a card, and the arm only matches the arm.
    /// </summary>
    internal sealed class PhysicsReplicator
    {
        public const float PosScale = 1000f;      // 1 mm, ±32.7 m from the machine root
        public const float RotScale = 32767f;

        private const ushort FlagManifest = 1;
        private const float ManifestKeyframeEvery = 5f;
        private const float SummaryEvery = 5f;

        // ---------------------------------------------------------- owner side
        private readonly List<Rigidbody> _bodies = new List<Rigidbody>(128);
        private readonly List<Rigidbody> _lastSentBodies = new List<Rigidbody>(128);
        private readonly List<string> _keys = new List<string>(128);
        private readonly List<Vector3> _lastSentPos = new List<Vector3>(128);
        private uint _lastSentMachine;
        private float _nextManifestAt;

        // -------------------------------------------------------- watcher side
        private sealed class Target
        {
            public Vector3 Pos;
            public Quaternion Rot;
            public Vector3 FromPos;
            public Quaternion FromRot;
            public float At;
        }
        private sealed class Watched
        {
            /// <summary>Everything under the root here — all of it goes kinematic.</summary>
            public readonly List<Rigidbody> Local = new List<Rigidbody>(128);
            /// <summary>Local body per manifest slot, null where we have no counterpart.</summary>
            public readonly List<Rigidbody> Aligned = new List<Rigidbody>(128);
            public readonly List<Target> Targets = new List<Target>(128);
            public readonly List<Vector3> LastPos = new List<Vector3>(128);
            /// <summary>Every object whose active flag we changed, with what it was. Restored on release.</summary>
            public readonly Dictionary<GameObject, bool> Originals = new Dictionary<GameObject, bool>();
            /// <summary>
            /// Every body under the root as it was when we started watching:
            /// where it sat, whether it was kinematic. Put back exactly on
            /// release, so the watcher's own machine is untouched by having
            /// been used as a screen for somebody else's round.
            /// </summary>
            public readonly Dictionary<Rigidbody, Snapshot> Before = new Dictionary<Rigidbody, Snapshot>();
            /// <summary>
            /// Colliders we switched off under the machine, to go back on at
            /// release. A body we are placing by hand is scenery to the
            /// watcher — it must not be grabbable, and the watcher's own
            /// player must not be able to nudge it. On the claw, a ball the
            /// owner won was driven down the chute into the bin, and until the
            /// next manifest hid it the watcher could pick it up: a copy of a
            /// prize that then also came out of the owner's machine.
            /// </summary>
            public readonly List<Collider> CollidersOff = new List<Collider>(256);
            public readonly HashSet<Rigidbody> AlignedSet = new HashSet<Rigidbody>();
            public readonly HashSet<Rigidbody> Counted = new HashSet<Rigidbody>();
            public int Matched, Unmatched;
            public bool HaveManifest, VisibilitySaid, RootDescribed;
            public string LastMismatch;
        }
        private struct Snapshot
        {
            public Vector3 Pos;
            public Quaternion Rot;
            public bool Kinematic;
        }
        private readonly Dictionary<uint, Watched> _watched = new Dictionary<uint, Watched>();
        private readonly List<Rigidbody> _scratch = new List<Rigidbody>(128);
        private readonly List<string> _scratchKeys = new List<string>(128);
        private readonly Dictionary<string, Rigidbody> _byKey = new Dictionary<string, Rigidbody>(1024);
        private readonly HashSet<uint> _describedOwner = new HashSet<uint>();

        // ------------------------------------------------------------ counters
        public int BodiesSent, BodiesApplied, CountMismatches, ManifestsSent, ManifestsReceived, WaitingForManifest;
        public float LastPacketBytes;
        public float SendRate = 20f;
        public float InterpDelay = 0.1f;

        /// <summary>
        /// Of the bodies we are placing: how many were drawing already when the
        /// first pose for them arrived, how many were switched off and we turned
        /// on, and how many have no renderer under them at all. 0.11.7's
        /// measurement; it answered "334 already on screen, 0 switched off", so
        /// stored coins were not the fault. Kept because it's cheap and it will
        /// say so again if a different machine stores its parts.
        /// </summary>
        public int PlacedAlreadyOnScreen, PlacedSwitchedOn, PlacedNothingToDraw;

        /// <summary>
        /// Is the stream actually carrying motion? Sender: of the bodies in the
        /// last packet, how many moved more than a millimetre since the packet
        /// before. Receiver: of the poses in the last packet, how many differed
        /// from the previous packet's. 0.11.8's measurement — it found the
        /// stream stopping after ONE packet, because a registry rebuild two
        /// seconds into the round handed back a Machine that nobody owned.
        /// </summary>
        public int SentMovedLastPacket, SentBodiesLastPacket, PacketsSent;
        public int RecvChangedLastPacket, RecvPosesLastPacket, PacketsReceived;
        public int SentMovedPeak, RecvChangedPeak;

        private float _nextSendAt, _nextSendSummaryAt, _nextRecvSummaryAt;

        // ------------------------------------------------------------ gathering

        /// <summary>
        /// Depth-first, in sibling order. Sleeping bodies are included: a coin
        /// that has settled still has to be in the right place for a spectator
        /// who only just walked up. Pass <paramref name="keys"/> to also get
        /// each body's key (path under the root, "#n" where siblings share a
        /// name); it costs a dictionary per parent, so the owner only asks for
        /// it when a manifest is due.
        /// </summary>
        public static void Gather(Transform root, List<Rigidbody> into, List<string> keys)
        {
            into.Clear();
            if (keys != null) keys.Clear();
            if (root == null) return;

            // The root's OWN rigidbody is deliberately skipped. Everything here
            // is expressed relative to the root, so the root relative to itself
            // is always the origin — harmless for a cabinet, and actively wrong
            // for a vehicle, where writing that back pins the car to wherever it
            // was when the packet was unpacked and undoes the world-space stream
            // that is carrying it.
            WalkChildren(root, "", into, keys);
        }

        private static void WalkChildren(Transform parent, string prefix, List<Rigidbody> into, List<string> keys)
        {
            Dictionary<string, int> seen = null;
            if (keys != null && parent.childCount > 1) seen = new Dictionary<string, int>(parent.childCount);

            for (int i = 0; i < parent.childCount; i++)
            {
                var t = parent.GetChild(i);
                string key = null;
                if (keys != null)
                {
                    int n = 0;
                    if (seen != null)
                    {
                        seen.TryGetValue(t.name, out n);
                        seen[t.name] = n + 1;
                    }
                    key = n == 0 ? prefix + t.name : prefix + t.name + "#" + n;
                }

                var rb = t.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    into.Add(rb);
                    if (keys != null) keys.Add(key);
                }
                if (t.childCount > 0) WalkChildren(t, keys != null ? key + "/" : null, into, keys);
            }
        }

        /// <summary>
        /// One line per machine per session about what the walk actually found
        /// under its root: each top-level child, how many rigidbodies it holds,
        /// and the first few bodies by name. Said on both ends, because whether
        /// the two walks are looking at the same objects is the whole question —
        /// and it was this line that showed the owner's walk starting with the
        /// owner's own card.
        /// </summary>
        private static void DescribeRoot(Machine m, List<Rigidbody> bodies, string side)
        {
            var sb = new StringBuilder();
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
            float now = Time.time;

            // A manifest goes out when the set of bodies changed (a coin was
            // collected, a card went in), when the machine changed, and every
            // few seconds regardless so a watcher who missed one, or who walked
            // up late, gets a fresh one soon.
            bool newMachine = _lastSentMachine != m.Id;
            Gather(m.Root, _bodies, null);
            if (_bodies.Count == 0) return null;

            bool changed = newMachine || _bodies.Count != _lastSentBodies.Count;
            if (!changed)
                for (int i = 0; i < _bodies.Count; i++)
                    if (!ReferenceEquals(_bodies[i], _lastSentBodies[i])) { changed = true; break; }

            bool manifest = changed || now >= _nextManifestAt;
            if (manifest)
            {
                Gather(m.Root, _bodies, _keys);
                _lastSentBodies.Clear();
                _lastSentBodies.AddRange(_bodies);
                _nextManifestAt = now + ManifestKeyframeEvery;
                ManifestsSent++;
            }
            if (newMachine)
            {
                _lastSentPos.Clear();
                _lastSentMachine = m.Id;
                if (_describedOwner.Add(m.Id)) DescribeRoot(m, _bodies, "Owner");
            }
            else if (changed) _lastSentPos.Clear();

            var origin = m.Root.position;
            var inv = Quaternion.Inverse(m.Root.rotation);

            int size = 4 + _bodies.Count * 15;
            byte[][] keyBytes = null;
            if (manifest)
            {
                keyBytes = new byte[_bodies.Count][];
                for (int i = 0; i < _bodies.Count; i++)
                {
                    keyBytes[i] = Encoding.UTF8.GetBytes(_keys[i]);
                    size += 2 + keyBytes[i].Length;
                }
            }

            var buf = new byte[size];
            int o = 0;
            WriteU16(buf, ref o, (ushort)_bodies.Count);
            WriteU16(buf, ref o, manifest ? FlagManifest : (ushort)0);
            if (manifest)
            {
                for (int i = 0; i < _bodies.Count; i++)
                {
                    WriteU16(buf, ref o, (ushort)keyBytes[i].Length);
                    Buffer.BlockCopy(keyBytes[i], 0, buf, o, keyBytes[i].Length);
                    o += keyBytes[i].Length;
                }
            }

            int moved = 0;
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

                // One flag byte per body. Bit 0: is it switched on over there.
                // A pusher's collected coins fall out of the tray, get counted,
                // and are put away — and the watcher was drawing every one of
                // them wherever the owner's copy happened to be lying, which
                // was all over the arcade floor.
                buf[o++] = (byte)(rb != null && rb.gameObject.activeInHierarchy ? 1 : 0);
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
            if (now >= _nextSendSummaryAt)
            {
                _nextSendSummaryAt = now + SummaryEvery;
                Plugin.Log("Streaming " + m.Label + ": " + moved + " of " + _bodies.Count
                           + " bodies moved in the last packet (peak " + SentMovedPeak + ", " + PacketsSent
                           + " packets, " + ManifestsSent + " manifests).");
            }
            return buf;
        }

        // ------------------------------------------------------------ receiving

        public void Unpack(Machine m, byte[] data)
        {
            if (m == null || m.Root == null || data == null || data.Length < 4) return;

            int o = 0;
            int count = ReadU16(data, ref o);
            int flags = ReadU16(data, ref o);
            bool manifest = (flags & FlagManifest) != 0;

            Watched w;
            if (!_watched.TryGetValue(m.Id, out w))
            {
                w = new Watched();
                _watched[m.Id] = w;
            }

            // Our own walk, every packet: everything under the root goes
            // kinematic, matched or not. The first version froze the driven
            // ones and left the rest alone, and the rest kept falling, kept
            // triggering the machine's collectors, and kept the watcher's copy
            // of the round running underneath the one it was being shown. A
            // body we cannot place is better still than moving on its own.
            Gather(m.Root, _scratch, manifest ? _scratchKeys : null);
            w.Local.Clear();
            w.Local.AddRange(_scratch);
            for (int i = 0; i < w.Local.Count; i++)
            {
                var rb = w.Local[i];
                if (rb == null) continue;
                if (!w.Before.ContainsKey(rb))
                {
                    w.Before[rb] = new Snapshot { Pos = rb.transform.position, Rot = rb.transform.rotation, Kinematic = rb.isKinematic };
                    var cols = rb.GetComponentsInChildren<Collider>(true);
                    for (int c = 0; c < cols.Length; c++)
                    {
                        // Only colliders that belong to THIS body — a child
                        // rigidbody's colliders are its own and get their turn.
                        if (cols[c] == null || !cols[c].enabled) continue;
                        if (cols[c].attachedRigidbody != rb) continue;
                        cols[c].enabled = false;
                        w.CollidersOff.Add(cols[c]);
                    }
                }
                if (!rb.isKinematic) rb.isKinematic = true;
            }
            if (!w.RootDescribed)
            {
                w.RootDescribed = true;
                DescribeRoot(m, _scratch, "Watcher");
                Plugin.Log("Switched off " + w.CollidersOff.Count + " colliders on " + w.Before.Count + " bodies in " + m.Label
                           + " while it's being played — they're scenery here until release.");
            }

            if (manifest)
            {
                ManifestsReceived++;
                _byKey.Clear();
                for (int i = 0; i < _scratch.Count; i++)
                    if (_scratch[i] != null && !_byKey.ContainsKey(_scratchKeys[i])) _byKey[_scratchKeys[i]] = _scratch[i];

                w.Aligned.Clear();
                int matched = 0;
                string firstMiss = null;
                for (int i = 0; i < count; i++)
                {
                    if (o + 2 > data.Length) return;
                    int len = ReadU16(data, ref o);
                    if (o + len > data.Length) return;
                    var key = Encoding.UTF8.GetString(data, o, len);
                    o += len;

                    Rigidbody rb;
                    if (_byKey.TryGetValue(key, out rb)) { w.Aligned.Add(rb); matched++; }
                    else { w.Aligned.Add(null); if (firstMiss == null) firstMiss = key; }
                }
                // The watcher sees the owner's tray and nothing else. A body of
                // ours the manifest doesn't name — our surplus coins (two saves
                // hold different amounts), or one we were driving whose owner
                // copy has since been collected and destroyed — has nothing to
                // follow, so it goes out of sight until release. Left visible,
                // the surplus sat frozen wherever the first packet caught them,
                // including mid-air in the idle machine's attract animation.
                w.AlignedSet.Clear();
                for (int i = 0; i < w.Aligned.Count; i++)
                    if (w.Aligned[i] != null) w.AlignedSet.Add(w.Aligned[i]);
                for (int i = 0; i < w.Local.Count; i++)
                {
                    var rb = w.Local[i];
                    if (rb != null && !w.AlignedSet.Contains(rb)) SetActiveRemembering(w, rb.gameObject, false);
                }

                w.HaveManifest = true;
                w.Matched = matched;
                w.Unmatched = count - matched;
                while (w.Targets.Count < count) w.Targets.Add(new Target());

                // Said when the picture changes, not once per packet: two saves
                // holding different numbers of coins is the steady state, and a
                // gap that MOVES is the interesting part.
                string mismatch = _scratch.Count + "/" + count + "/" + matched;
                if (mismatch != w.LastMismatch)
                {
                    w.LastMismatch = mismatch;
                    if (_scratch.Count != count) CountMismatches++;
                    Plugin.Log("Physics: " + m.Label + " has " + _scratch.Count + " moving parts here and " + count
                               + " on the player's screen. Matched " + matched + " by name"
                               + (count - matched > 0 ? ", " + (count - matched) + " have no counterpart here (e.g. " + firstMiss + ")" : "")
                               + ".");
                }
            }
            else if (!w.HaveManifest)
            {
                // Poses in an order we haven't been told yet. The next keyframe
                // is at most a few seconds away.
                WaitingForManifest++;
                return;
            }
            if (w.Aligned.Count != count) return;   // manifest and packet disagree; wait for the next keyframe
            if (data.Length < o + count * 15) return;

            // 0.11.7's question, still asked: of the bodies we place, were they
            // drawing already? Switch on any that weren't (and any parent up to
            // the root), and put them back on release.
            int onScreen = 0, turnedOn = 0, undrawable = 0;
            for (int i = 0; i < count; i++)
            {
                var rb = w.Aligned[i];
                if (rb == null || !w.Counted.Add(rb)) continue;

                bool wasDrawing = rb.gameObject.activeInHierarchy;
                var t = rb.transform.parent;
                while (t != null && t != m.Root)
                {
                    if (!t.gameObject.activeSelf) SetActiveRemembering(w, t.gameObject, true);
                    t = t.parent;
                }

                if (rb.GetComponentInChildren<Renderer>(true) == null) undrawable++;
                else if (wasDrawing) onScreen++;
                else turnedOn++;
            }
            PlacedAlreadyOnScreen += onScreen;
            PlacedSwitchedOn += turnedOn;
            PlacedNothingToDraw += undrawable;
            if (!w.VisibilitySaid)
            {
                w.VisibilitySaid = true;
                Plugin.Log("Placing " + w.Matched + " bodies in " + m.Label + ": " + onScreen + " were already on screen, "
                           + turnedOn + " were switched off and we turned on"
                           + (undrawable > 0 ? ", " + undrawable + " have nothing to draw" : "") + ".");
            }

            var origin = m.Root.position;
            var rot = m.Root.rotation;
            float now = Time.time;
            int changed = 0, applied = 0;

            for (int i = 0; i < count; i++)
            {
                bool on = (data[o++] & 1) != 0;
                var lp = new Vector3(
                    Dequant(ReadI16(data, ref o), PosScale),
                    Dequant(ReadI16(data, ref o), PosScale),
                    Dequant(ReadI16(data, ref o), PosScale));
                var lr = new Quaternion(
                    Dequant(ReadI16(data, ref o), RotScale),
                    Dequant(ReadI16(data, ref o), RotScale),
                    Dequant(ReadI16(data, ref o), RotScale),
                    Dequant(ReadI16(data, ref o), RotScale));

                if (i < w.LastPos.Count)
                {
                    if ((lp - w.LastPos[i]).sqrMagnitude > 0.001f * 0.001f) changed++;
                    w.LastPos[i] = lp;
                }
                else w.LastPos.Add(lp);

                var rb = w.Aligned[i];
                if (rb == null) continue;
                if (rb.gameObject.activeSelf != on) SetActiveRemembering(w, rb.gameObject, on);

                var t = w.Targets[i];
                t.FromPos = rb.transform.position;
                t.FromRot = rb.transform.rotation;
                t.Pos = origin + rot * lp;
                t.Rot = rot * Normalise(lr);
                t.At = now;
                applied++;
            }

            BodiesApplied += applied;
            PacketsReceived++;
            RecvPosesLastPacket = count;
            RecvChangedLastPacket = changed;
            if (changed > RecvChangedPeak) RecvChangedPeak = changed;
            if (now >= _nextRecvSummaryAt)
            {
                _nextRecvSummaryAt = now + SummaryEvery;
                Plugin.Log("Watching " + m.Label + ": " + changed + " of " + count
                           + " incoming poses changed in the last packet (peak " + RecvChangedPeak + ", "
                           + PacketsReceived + " packets, placing " + applied + ").");
            }
        }

        /// <summary>Eases each body toward its last received pose; called every frame.</summary>
        public void Render()
        {
            if (_watched.Count == 0) return;
            float now = Time.time;

            foreach (var kv in _watched)
            {
                var w = kv.Value;
                for (int i = 0; i < w.Aligned.Count && i < w.Targets.Count; i++)
                {
                    var rb = w.Aligned[i];
                    if (rb == null) continue;
                    var t = w.Targets[i];
                    if (t.At <= 0f) continue;

                    float k = InterpDelay <= 0f ? 1f : Mathf.Clamp01((now - t.At) / InterpDelay);
                    rb.transform.position = Vector3.Lerp(t.FromPos, t.Pos, k);
                    rb.transform.rotation = Quaternion.Slerp(t.FromRot, t.Rot, k);
                }
            }
        }

        /// <summary>Hands the machine back to local physics when we stop spectating it.</summary>
        public void ReleaseMachine(uint machineId)
        {
            Watched w;
            if (!_watched.TryGetValue(machineId, out w)) return;

            // Everything we switched on or off goes back exactly as it was.
            foreach (var kv in w.Originals)
                if (kv.Key != null) kv.Key.SetActive(kv.Value);

            for (int i = 0; i < w.CollidersOff.Count; i++)
                if (w.CollidersOff[i] != null) w.CollidersOff[i].enabled = true;

            // And every body goes back where it was, as it was — not merely
            // "not kinematic". Some of them were kinematic to begin with, and a
            // coin we drove around the owner's tray belongs back in ours.
            foreach (var kv in w.Before)
            {
                var rb = kv.Key;
                if (rb == null) continue;
                rb.transform.position = kv.Value.Pos;
                rb.transform.rotation = kv.Value.Rot;
                rb.isKinematic = kv.Value.Kinematic;
                if (!kv.Value.Kinematic)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    rb.WakeUp();
                }
            }

            _watched.Remove(machineId);
        }

        private static void SetActiveRemembering(Watched w, GameObject go, bool on)
        {
            if (go == null || go.activeSelf == on) return;
            if (!w.Originals.ContainsKey(go)) w.Originals[go] = go.activeSelf;
            go.SetActive(on);
        }

        public void ReleaseAll()
        {
            var ids = new List<uint>(_watched.Keys);
            foreach (var id in ids) ReleaseMachine(id);
        }

        public int SpectatedMachines { get { return _watched.Count; } }

        /// <summary>
        /// Are we being sent this machine's moving parts?
        ///
        /// Asked by the event mirror, which must keep its hands off a machine
        /// whose contents arrive over the wire — see the note in Unpack about
        /// a watcher running its own round.
        /// </summary>
        public bool IsSpectating(uint machineId) { return _watched.ContainsKey(machineId); }

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
