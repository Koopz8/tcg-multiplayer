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
        /// <summary>This packet carries only some of the bodies, each with its index.</summary>
        private const ushort FlagSparse = 2;
        /// <summary>
        /// A share of the bodies goes out every packet whether they moved or
        /// not, cycling through the list, so that at 20 Hz every body is
        /// refreshed inside a second. Without it an unreliable packet carrying
        /// the last pose of something that then stopped would strand it there
        /// until the next keyframe.
        ///
        /// Forty, not twenty: the slice became the floor of the bill once the
        /// still bodies stopped going out. On Cuckoo it was eleven bodies a
        /// packet, 3.7 of the 4.7 KB/s the machine cost. Sweeping over two
        /// seconds instead of one halves that, and the only thing it delays is
        /// repairing a body whose final pose was in a lost packet.
        /// </summary>
        private const int RefreshSlice = 40;
        private const float ManifestKeyframeEvery = 5f;
        private const float SummaryEvery = 5f;

        // ---------------------------------------------------------- owner side
        private readonly List<Rigidbody> _bodies = new List<Rigidbody>(128);
        private readonly List<Rigidbody> _lastSentBodies = new List<Rigidbody>(128);
        private readonly List<string> _keys = new List<string>(128);
        /// <summary>
        /// The pose each body was last SENT at, keyed by its number rather than
        /// its slot. Slots shift the moment a chip spawns in the middle of the
        /// walk, which used to invalidate every cached pose and force a full
        /// dump of all of them. A number doesn't shift, so a chip appearing
        /// costs one body's worth of packet instead of two hundred.
        /// </summary>
        private readonly Dictionary<ushort, Vector3> _sentPos = new Dictionary<ushort, Vector3>(1024);
        private readonly Dictionary<ushort, bool> _sentOn = new Dictionary<ushort, bool>(1024);
        private readonly List<int> _send = new List<int>(128);

        // ---- the key dictionary
        //
        // A manifest used to spell out every body's full path, and a manifest
        // goes out whenever the set of bodies changes. On Cuckoo that is every
        // time a chip spawns or gets collected: 183 manifests in three minutes,
        // each re-sending the same two hundred and fifty strings, which was half
        // of all the physics traffic. "OBJECTS/CHIP_1(Clone)#37" is 24 bytes and
        // it does not change.
        //
        // So each path is given a number once and the manifest sends numbers.
        // Definitions go out when the key is new, and all of them again on the
        // five-second keyframe, because this is the unreliable channel and a
        // definition can be lost like anything else.
        private readonly Dictionary<string, ushort> _keyIds = new Dictionary<string, ushort>(1024);
        private readonly List<string> _keyList = new List<string>(1024);
        private readonly List<ushort> _ids = new List<ushort>(256);
        private readonly List<ushort> _defs = new List<ushort>(256);
        private readonly List<ushort> _recentDefs = new List<ushort>(64);
        private byte _keyGen;
        private float _nextKeyframeAt;
        /// <summary>
        /// Recently defined keys are repeated on every manifest, so a lost
        /// definition is usually repaired in the next packet rather than waiting
        /// for the keyframe. A spawned chip is exactly the case that matters:
        /// its definition is new, and it is also the thing moving.
        /// </summary>
        private const int RecentRepeat = 16;
        private int _refreshCursor, _sizeComplaints;
        private uint _lastSentMachine;

        // -------------------------------------------------------- watcher side
        private sealed class Target
        {
            public Vector3 Pos;
            public Quaternion Rot;
            public Vector3 FromPos;
            public Quaternion FromRot;
            public float At;
            /// <summary>When FromPos was true, on the same clock as At.</summary>
            public float FromAt;
            public float Dur = 0.05f;
            /// <summary>What Render last put on screen, so LateRender can put it back.</summary>
            public Vector3 DrawnPos;
            public Quaternion DrawnRot;
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
            /// <summary>FSMs silenced under anything we switched on; see Quiet.</summary>
            public readonly Dictionary<Behaviour, bool> FsmWas = new Dictionary<Behaviour, bool>();
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
            /// <summary>Last walk, by key, to name whatever leaves the machine on OUR side.</summary>
            public readonly Dictionary<string, Rigidbody> LastLocalByKey = new Dictionary<string, Rigidbody>(256);
            public int LastLocalCount = -1, DepartureLogs;
            public readonly HashSet<Rigidbody> AlignedSet = new HashSet<Rigidbody>();
            public readonly HashSet<Rigidbody> Counted = new HashSet<Rigidbody>();
            public int Matched, Unmatched, Stood;
            public bool HaveManifest, VisibilitySaid, RootDescribed;
            public string LastMismatch;
            /// <summary>
            /// Bodies the owner has that we never will, one per manifest key,
            /// built once and reused. Destroyed on release.
            /// </summary>
            public readonly Dictionary<string, Rigidbody> StandIns = new Dictionary<string, Rigidbody>();
            /// <summary>Built, but not yet told where it goes.</summary>
            public readonly HashSet<Rigidbody> StandInWaiting = new HashSet<Rigidbody>();
            public readonly HashSet<string> StandInGaveUp = new HashSet<string>();
            /// <summary>Path per number, as the owner has defined them. See the key dictionary.</summary>
            public readonly Dictionary<ushort, string> KeyNames = new Dictionary<ushort, string>(1024);
            public int KeyGen = -1;
            public bool StandInCapSaid;
            public string Label;
            /// <summary>
            /// Where Render last put each body. If it's somewhere else by the
            /// next frame, something on this side moved it after us — and every
            /// counter upstream of here would still say the pose arrived.
            /// </summary>
            public readonly Dictionary<Rigidbody, Vector3> Wrote = new Dictionary<Rigidbody, Vector3>(512);
            public int Overridden, Writes, LateFixes;
            /// <summary>Running average of the time between pose packets.</summary>
            public float Gap = 0.05f, LastPacketAt = -1f;
            public string OverriddenExample;
            public float OverriddenWorst;
        }
        private struct Snapshot
        {
            public Vector3 Pos;
            public Quaternion Rot;
            public bool Kinematic;
            public RigidbodyInterpolation Interp;
        }
        private readonly Dictionary<uint, Watched> _watched = new Dictionary<uint, Watched>();
        private readonly List<Rigidbody> _scratch = new List<Rigidbody>(128);
        private readonly List<string> _scratchKeys = new List<string>(128);
        private readonly Dictionary<string, Rigidbody> _byKey = new Dictionary<string, Rigidbody>(1024);
        private readonly HashSet<uint> _describedOwner = new HashSet<uint>();

        /// <summary>
        /// Bodies that left an OWNED machine's walk — a won prize, a ball that
        /// went out through the chute. Followed for a while and described,
        /// because the next thing to build is showing these to other players
        /// and nobody has looked at what the game does with one yet.
        /// </summary>
        private sealed class Departed
        {
            public string Key;
            public Rigidbody Body;
            public float NextSayAt, StopAt;
            public string LastSaid;
        }
        private readonly List<Departed> _departed = new List<Departed>();
        private readonly Dictionary<string, Rigidbody> _lastOwnedByKey = new Dictionary<string, Rigidbody>(256);
        private int _departedLogs;

        private static string Describe(Rigidbody rb)
        {
            if (rb == null) return "destroyed";
            var go = rb.gameObject;
            string where;
            try { where = NetId.Path(rb.transform); } catch { where = "?"; }
            int colsOn = 0;
            var cols = rb.GetComponentsInChildren<Collider>(true);
            for (int c = 0; c < cols.Length; c++) if (cols[c] != null && cols[c].enabled) colsOn++;
            return where + (go.activeInHierarchy ? "" : " (off)") + ", layer " + LayerMask.LayerToName(go.layer)
                   + ", kinematic=" + rb.isKinematic + ", sleeping=" + rb.IsSleeping()
                   + ", " + colsOn + "/" + cols.Length + " colliders on, at " + rb.transform.position.ToString("F1");
        }

        /// <summary>Owner side, every packet: notice what left, and keep an eye on it.</summary>
        private void TrackDepartures(Machine m)
        {
            float now = Time.time;
            if (_keys.Count == _bodies.Count && _lastOwnedByKey.Count > 0)
            {
                var present = new HashSet<string>(_keys);
                foreach (var kv in _lastOwnedByKey)
                {
                    if (present.Contains(kv.Key)) continue;
                    if (_departedLogs >= 8) break;
                    _departedLogs++;
                    _departed.Add(new Departed { Key = kv.Key, Body = kv.Value, NextSayAt = now, StopAt = now + 40f });
                    Plugin.Log("Left " + m.Label + " (owner): " + kv.Key + " -> " + Describe(kv.Value));
                }
            }
            if (_keys.Count == _bodies.Count)
            {
                _lastOwnedByKey.Clear();
                for (int i = 0; i < _bodies.Count; i++)
                    if (!_lastOwnedByKey.ContainsKey(_keys[i])) _lastOwnedByKey[_keys[i]] = _bodies[i];
            }
            for (int i = _departed.Count - 1; i >= 0; i--)
            {
                var d = _departed[i];
                if (now >= d.StopAt) { _departed.RemoveAt(i); continue; }
                if (now < d.NextSayAt) continue;
                d.NextSayAt = now + 2f;
                if (d.Body == null)
                {
                    // The first version trimmed "destroyed" at ", at " and
                    // threw every frame for the rest of the session — which
                    // took the whole machines subsystem down with it after six
                    // frames. A destroyed body is said once and dropped.
                    Plugin.Log("Departed " + d.Key + ": destroyed.");
                    _departed.RemoveAt(i);
                    continue;
                }
                var said = Describe(d.Body);
                // Only when something about it changed — a held ball says the
                // same thing thirty times otherwise.
                int at = said.LastIndexOf(", at ", StringComparison.Ordinal);
                var shape = at > 0 ? said.Substring(0, at) : said;
                if (shape == d.LastSaid) continue;
                d.LastSaid = shape;
                Plugin.Log("Departed " + d.Key + ": " + said);
            }
        }

        // ------------------------------------------------------------ counters
        /// <summary>Per-machine spectating summary, so a sweep of the arcade writes itself down.</summary>
        public WatchReport Report;

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

        /// <summary>
        /// The player's club card, and the ring clasp hanging off it, are not
        /// part of the cabinet — they are the card the player pushed into the
        /// slot, and they sit under POWER CNTRLR/CARD SPOT IN for as long as
        /// the round lasts.
        ///
        /// A watcher has no card in that machine, so these two bodies could
        /// never match, and they turned up as "2 have no counterpart here" on
        /// every single cabinet in the sweep. On Speed Drop, which has no
        /// moving parts of its own, they were the ONLY two bodies in the
        /// manifest, so the report called a working machine "NOTHING MATCHED".
        /// They also account for the count-mismatch alarm firing seventy times
        /// in one session while nothing was actually wrong.
        ///
        /// These are the same two bodies that broke ordinal addressing back in
        /// 0.11, which is twice now that the owner's card has cost a day. It
        /// does not get walked any more.
        /// </summary>
        private static bool IsOwnersCard(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.IndexOf("PlayersClubCard", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Anything the mod itself put under the machine — a stand-in for a
        /// body the owner has and we don't. It must never enter either walk:
        /// it isn't part of this machine, and letting it in would make our
        /// body count drift upward every round.
        /// </summary>
        private static bool IsOurs(string name)
        {
            return !string.IsNullOrEmpty(name) && name.StartsWith(OursPrefix, StringComparison.Ordinal);
        }

        private const string OursPrefix = "TCGMP_Stand_";

        /// <summary>
        /// "Machine HANDS-Joystick": the first-person hands a cabinet shows on
        /// the PLAYER's arms. They belong to whoever is at the machine on each
        /// side, never to the stream.
        /// </summary>
        public static bool IsPlayersHands(string name)
        {
            return !string.IsNullOrEmpty(name) && name.StartsWith("Machine HANDS", StringComparison.OrdinalIgnoreCase);
        }

        private static void WalkChildren(Transform parent, string prefix, List<Rigidbody> into, List<string> keys)
        {
            Dictionary<string, int> seen = null;
            if (keys != null && parent.childCount > 1) seen = new Dictionary<string, int>(parent.childCount);

            for (int i = 0; i < parent.childCount; i++)
            {
                var t = parent.GetChild(i);
                if (IsOwnersCard(t.name) || IsOurs(t.name) || IsPlayersHands(t.name)) continue;
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

        /// <summary>
        /// Numbers for the current walk, and definitions for the ones the
        /// watcher cannot be assumed to know yet: new keys always, every key on
        /// a keyframe, plus a repeat of the most recent ones so a lost
        /// definition is normally repaired by the next packet.
        /// </summary>
        private void BuildIds(bool keyframe)
        {
            _ids.Clear();
            _defs.Clear();
            for (int i = 0; i < _keys.Count; i++)
            {
                ushort id;
                bool isNew = false;
                if (!_keyIds.TryGetValue(_keys[i], out id))
                {
                    if (_keyList.Count >= 65000)
                    {
                        // Sixty-five thousand distinct paths in one lease means
                        // something is spawning without end; start the numbering
                        // over rather than wrap around onto live numbers.
                        _keyIds.Clear(); _keyList.Clear(); _recentDefs.Clear();
                        _sentPos.Clear(); _sentOn.Clear();
                        _keyGen++;
                        keyframe = true;
                        i = -1;
                        _ids.Clear(); _defs.Clear();
                        continue;
                    }
                    id = (ushort)_keyList.Count;
                    _keyList.Add(_keys[i]);
                    _keyIds[_keys[i]] = id;
                    isNew = true;
                    _recentDefs.Add(id);
                }
                _ids.Add(id);
                if (keyframe || isNew) _defs.Add(id);
            }
            if (_recentDefs.Count > RecentRepeat * 4)
                _recentDefs.RemoveRange(0, _recentDefs.Count - RecentRepeat * 4);
            if (!keyframe)
            {
                for (int r = Mathf.Max(0, _recentDefs.Count - RecentRepeat); r < _recentDefs.Count; r++)
                    if (!_defs.Contains(_recentDefs[r])) _defs.Add(_recentDefs[r]);
            }

            // Poses for numbers no longer in the walk are dead weight; the
            // keyframe is a cheap moment to drop them.
            if (keyframe && _sentPos.Count > _ids.Count * 2 + 64)
            {
                _live.Clear();
                for (int i = 0; i < _ids.Count; i++) _live.Add(_ids[i]);
                _dropped.Clear();
                foreach (var kv in _sentPos) if (!_live.Contains(kv.Key)) _dropped.Add(kv.Key);
                for (int i = 0; i < _dropped.Count; i++) { _sentPos.Remove(_dropped[i]); _sentOn.Remove(_dropped[i]); }
            }
        }

        private readonly HashSet<ushort> _live = new HashSet<ushort>();
        private readonly List<ushort> _dropped = new List<ushort>();

        private static Vector3 PoseOf(Rigidbody rb, Vector3 origin, Quaternion inv)
        {
            if (rb == null) return Vector3.zero;
            return inv * (rb.transform.position - origin);
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
            Gather(m.Play, _bodies, null);
            if (_bodies.Count == 0) return null;

            bool changed = newMachine || _bodies.Count != _lastSentBodies.Count;
            if (!changed)
                for (int i = 0; i < _bodies.Count; i++)
                    if (!ReferenceEquals(_bodies[i], _lastSentBodies[i])) { changed = true; break; }

            // The keyframe clock runs on its own. Resetting it on every
            // change-driven manifest would mean a full set of definitions
            // almost never went out, because during a round the set changes
            // about once a second — and then a lost definition would never be
            // repaired.
            bool keyframe = now >= _nextKeyframeAt;
            bool manifest = changed || keyframe;
            if (keyframe) _nextKeyframeAt = now + ManifestKeyframeEvery;
            if (manifest)
            {
                Gather(m.Play, _bodies, _keys);
                _lastSentBodies.Clear();
                _lastSentBodies.AddRange(_bodies);
                ManifestsSent++;
            }
            TrackDepartures(m);
            if (newMachine)
            {
                _sentPos.Clear();
                _sentOn.Clear();
                _refreshCursor = 0;
                _lastSentMachine = m.Id;
                _keyIds.Clear();
                _keyList.Clear();
                _recentDefs.Clear();
                _ids.Clear();
                _keyGen++;              // tells the watcher to forget the old numbers
                _nextKeyframeAt = 0f;   // and get a full set straight away
                keyframe = true;
                manifest = true;
                if (_keys.Count != _bodies.Count) Gather(m.Play, _bodies, _keys);
                if (_describedOwner.Add(m.Id)) DescribeRoot(m, _bodies, "Owner");
            }

            // The numbering has to exist before we can decide who goes in the
            // packet, because that decision is now made per number.
            if (manifest) BuildIds(keyframe);
            if (_ids.Count != _bodies.Count) return null;   // no mapping, nothing safe to send

            var origin = m.Root.position;
            var inv = Quaternion.Inverse(m.Root.rotation);

            // Which bodies are worth a packet. A cabinet's bodies are mostly
            // coins and chips lying still: the host log read "5 of 206 bodies
            // moved in the last packet" and we were sending all 206, fifteen
            // bytes each, twenty times a second — sixty kilobytes a second for
            // one machine, again for every extra person watching. So a
            // non-keyframe packet carries the ones that moved, the ones that
            // appeared or vanished, and a rolling slice of the rest.
            //
            // The comparison is against the pose last SENT, not last frame, so
            // a body that moves and then stops has its final resting pose sent
            // once and is then quiet — which is the whole point.
            // A keyframe forgets what it has sent, so it carries every pose
            // again. 0.14.3 made manifests sparse along with everything else,
            // and that quietly broke watching a machine at rest: the watcher
            // builds a stand-in for each body it hasn't got, and a stand-in has
            // no position until a pose for it arrives. On a pusher between
            // rounds nothing moves, so 326 chests were created and then never
            // placed — "0 of 332 bodies moved, 9 sent" one packet after a
            // manifest that had just caused 326 of them to be built.
            //
            // This is what a keyframe is for. Every five seconds costs about a
            // kilobyte a second on the heaviest cabinet in the arcade, and in
            // exchange anything stranded for any reason fixes itself.
            if (keyframe) { _sentPos.Clear(); _sentOn.Clear(); }

            int moved = 0;
            _send.Clear();
            const bool sparse = true;   // every packet names what it carries now
            int slice = _bodies.Count / RefreshSlice + 1;
            if (_refreshCursor >= _bodies.Count) _refreshCursor = 0;
            int sliceFrom = _refreshCursor;
            int sliceTo = sliceFrom + slice;
            _refreshCursor = sliceTo >= _bodies.Count ? 0 : sliceTo;

            for (int i = 0; i < _bodies.Count; i++)
            {
                var rb = _bodies[i];
                Vector3 lp = PoseOf(rb, origin, inv);
                bool on = rb != null && rb.gameObject.activeInHierarchy;
                ushort id = _ids[i];

                Vector3 was;
                bool wasOn;
                bool known = _sentPos.TryGetValue(id, out was) && _sentOn.TryGetValue(id, out wasOn);
                bool isMoved = !known
                               || (lp - was).sqrMagnitude > 0.001f * 0.001f
                               || _sentOn[id] != on;
                if (isMoved && known) moved++;

                if (isMoved || (i >= sliceFrom && i < sliceTo)) _send.Add(i);
            }
            if (_send.Count == 0 && !manifest) return null;

            int size = (sparse ? 6 : 4) + _send.Count * (sparse ? 17 : 15);
            byte[][] defBytes = null;
            int[] defShared = null;
            if (manifest)
            {
                // Each definition drops the part of the path it shares with the
                // one before it. Sibling coins differ in their last character.
                defBytes = new byte[_defs.Count][];
                defShared = new int[_defs.Count];
                byte[] prev = null;
                for (int d = 0; d < _defs.Count; d++)
                {
                    var full = Encoding.UTF8.GetBytes(_keyList[_defs[d]]);
                    int shared = 0;
                    if (prev != null)
                    {
                        int max = Mathf.Min(255, Mathf.Min(prev.Length, full.Length));
                        while (shared < max && prev[shared] == full[shared]) shared++;
                    }
                    defShared[d] = shared;
                    var suffix = new byte[full.Length - shared];
                    Buffer.BlockCopy(full, shared, suffix, 0, suffix.Length);
                    defBytes[d] = suffix;
                    prev = full;
                    size += 5 + suffix.Length;
                }
                size += 3 + _ids.Count * 2;     // generation, definition count, the numbers
            }

            var buf = new byte[size];
            int o = 0;
            WriteU16(buf, ref o, (ushort)_bodies.Count);
            // Manifests are sparse too — every packet names its bodies now —
            // and the flag has to say so. It didn't: since the key dictionary
            // went in, every keyframe was written with an index before each
            // pose and read without one, so all 332 Treasure chests were put
            // two bytes out of step every five seconds and flew apart until
            // the next few packets put them back. The writer's size check
            // couldn't see it; the reader's (below) now would.
            WriteU16(buf, ref o, (ushort)((manifest ? FlagManifest : 0) | (sparse ? FlagSparse : 0)));
            if (manifest)
            {
                buf[o++] = _keyGen;
                WriteU16(buf, ref o, (ushort)_defs.Count);
                for (int d = 0; d < _defs.Count; d++)
                {
                    buf[o++] = (byte)defShared[d];
                    WriteU16(buf, ref o, _defs[d]);
                    WriteU16(buf, ref o, (ushort)defBytes[d].Length);
                    Buffer.BlockCopy(defBytes[d], 0, buf, o, defBytes[d].Length);
                    o += defBytes[d].Length;
                }
                for (int i = 0; i < _ids.Count; i++) WriteU16(buf, ref o, _ids[i]);
            }
            if (sparse) WriteU16(buf, ref o, (ushort)_send.Count);

            for (int k = 0; k < _send.Count; k++)
            {
                int i = _send[k];
                var rb = _bodies[i];
                Vector3 lp; Quaternion lr;
                if (rb == null) { lp = Vector3.zero; lr = Quaternion.identity; }
                else
                {
                    lp = inv * (rb.transform.position - origin);
                    lr = inv * rb.transform.rotation;
                }
                bool on = rb != null && rb.gameObject.activeInHierarchy;
                _sentPos[_ids[i]] = lp;
                _sentOn[_ids[i]] = on;

                if (sparse) WriteU16(buf, ref o, (ushort)i);

                // One flag byte per body. Bit 0: is it switched on over there.
                // A pusher's collected coins fall out of the tray, get counted,
                // and are put away — and the watcher was drawing every one of
                // them wherever the owner's copy happened to be lying, which
                // was all over the arcade floor.
                buf[o++] = (byte)(on ? 1 : 0);
                WriteI16(buf, ref o, Quant(lp.x, PosScale));
                WriteI16(buf, ref o, Quant(lp.y, PosScale));
                WriteI16(buf, ref o, Quant(lp.z, PosScale));
                WriteI16(buf, ref o, Quant(lr.x, RotScale));
                WriteI16(buf, ref o, Quant(lr.y, RotScale));
                WriteI16(buf, ref o, Quant(lr.z, RotScale));
                WriteI16(buf, ref o, Quant(lr.w, RotScale));
            }

            // The packet is sized up front and then written; if those two ever
            // disagree the reader gets garbage and the symptom shows up as
            // bodies in the wrong place, which is a long way from the cause.
            // Two machine types' worth of offset arithmetic is not something to
            // find out about from a screenshot.
            if (o != buf.Length && _sizeComplaints < 3)
            {
                _sizeComplaints++;
                Plugin.Warn("Physics packet for " + m.Label + " was sized " + buf.Length + " bytes and wrote "
                            + o + " — " + _send.Count + " of " + _bodies.Count + " bodies"
                            + (manifest ? ", keyframe" : "") + (sparse ? ", sparse" : "") + ".");
            }

            BodiesSent += _send.Count;
            LastPacketBytes = buf.Length;
            PacketsSent++;
            SentBodiesLastPacket = _send.Count;
            SentMovedLastPacket = moved;
            if (moved > SentMovedPeak) SentMovedPeak = moved;
            if (now >= _nextSendSummaryAt)
            {
                _nextSendSummaryAt = now + SummaryEvery;
                Plugin.Log("Streaming " + m.Label + ": " + moved + " of " + _bodies.Count
                           + " bodies moved, " + _send.Count + " sent in " + buf.Length + " bytes (peak "
                           + SentMovedPeak + ", " + PacketsSent + " packets, " + ManifestsSent + " manifests).");
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
                w.Label = m.Label;
                _watched[m.Id] = w;
                HideRestingDisplay(w, m);
            }

            // Our own walk, every packet: everything under the root goes
            // kinematic, matched or not. The first version froze the driven
            // ones and left the rest alone, and the rest kept falling, kept
            // triggering the machine's collectors, and kept the watcher's copy
            // of the round running underneath the one it was being shown. A
            // body we cannot place is better still than moving on its own.
            Gather(m.Play, _scratch, manifest ? _scratchKeys : null);

            // A body leaving OUR walk while we spectate is a fault: nothing
            // of ours should be running the machine. On the claw, the
            // watcher's walk went 204 -> 203 the moment the owner won a ball,
            // and that ball was then a pickup on the watcher's floor. Say
            // which key went and where the object is now.
            if (w.LastLocalCount >= 0 && _scratch.Count < w.LastLocalCount && w.DepartureLogs < 6)
            {
                if (!manifest) Gather(m.Play, _scratch, _scratchKeys);
                var present = new HashSet<string>(_scratchKeys);
                foreach (var kv in w.LastLocalByKey)
                {
                    if (present.Contains(kv.Key) || kv.Value == null) continue;
                    if (w.DepartureLogs >= 6) break;
                    var rb = kv.Value;
                    var go = rb.gameObject;
                    string where;
                    try { where = NetId.Path(rb.transform); } catch { where = "?"; }
                    int colsOn = 0;
                    var cols = rb.GetComponentsInChildren<Collider>(true);
                    for (int c = 0; c < cols.Length; c++) if (cols[c] != null && cols[c].enabled) colsOn++;
                    w.DepartureLogs++;
                    Plugin.Log("Left our walk of " + m.Label + ": " + kv.Key + " is now at " + where
                               + (go.activeInHierarchy ? "" : " (off)") + ", layer " + LayerMask.LayerToName(go.layer)
                               + ", kinematic=" + rb.isKinematic + ", " + colsOn + " of " + cols.Length + " colliders on.");
                }
            }
            if (manifest || w.LastLocalCount != _scratch.Count)
            {
                if (_scratchKeys.Count != _scratch.Count) Gather(m.Play, _scratch, _scratchKeys);
                w.LastLocalByKey.Clear();
                for (int i = 0; i < _scratch.Count; i++)
                    if (!w.LastLocalByKey.ContainsKey(_scratchKeys[i])) w.LastLocalByKey[_scratchKeys[i]] = _scratch[i];
            }
            w.LastLocalCount = _scratch.Count;

            w.Local.Clear();
            w.Local.AddRange(_scratch);
            for (int i = 0; i < w.Local.Count; i++)
            {
                var rb = w.Local[i];
                if (rb == null) continue;
                if (!w.Before.ContainsKey(rb))
                {
                    w.Before[rb] = new Snapshot { Pos = rb.transform.position, Rot = rb.transform.rotation, Kinematic = rb.isKinematic, Interp = rb.interpolation };
                    // A body we place by hand must not also be placed by the
                    // physics interpolator, which writes the transform every
                    // frame from where the rigidbody was at the last step.
                    if (rb.interpolation != RigidbodyInterpolation.None)
                    {
                        rb.interpolation = RigidbodyInterpolation.None;
                        InterpolationOff++;
                    }
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
                SayUnsentParents(m, _scratch);
                Plugin.Log("Switched off " + w.CollidersOff.Count + " colliders on " + w.Before.Count + " bodies in " + m.Label
                           + " while it's being played — they're scenery here until release.");
            }

            if (manifest)
            {
                ManifestsReceived++;
                _byKey.Clear();
                for (int i = 0; i < _scratch.Count; i++)
                    if (_scratch[i] != null && !_byKey.ContainsKey(_scratchKeys[i])) _byKey[_scratchKeys[i]] = _scratch[i];

                // The owner's numbering, and whatever it has told us the numbers
                // mean. A generation change means the owner started on a
                // different machine and the old numbers are meaningless.
                if (o + 3 > data.Length) return;
                byte gen = data[o++];
                if (gen != w.KeyGen) { w.KeyNames.Clear(); w.KeyGen = gen; }

                int defCount = ReadU16(data, ref o);
                byte[] prevKey = null;
                for (int d = 0; d < defCount; d++)
                {
                    if (o + 5 > data.Length) return;
                    int shared = data[o++];
                    ushort defId = (ushort)ReadU16(data, ref o);
                    int suffixLen = ReadU16(data, ref o);
                    if (o + suffixLen > data.Length) return;
                    if (prevKey == null ? shared != 0 : shared > prevKey.Length) return;

                    var full = new byte[shared + suffixLen];
                    if (shared > 0) Buffer.BlockCopy(prevKey, 0, full, 0, shared);
                    Buffer.BlockCopy(data, o, full, shared, suffixLen);
                    o += suffixLen;
                    prevKey = full;
                    w.KeyNames[defId] = Encoding.UTF8.GetString(full);
                }

                w.Aligned.Clear();
                int matched = 0, stood = 0;
                string firstMiss = null;
                for (int i = 0; i < count; i++)
                {
                    if (o + 2 > data.Length) return;
                    ushort id = (ushort)ReadU16(data, ref o);
                    string key;
                    if (!w.KeyNames.TryGetValue(id, out key))
                    {
                        // A definition went missing. Nothing is applied from a
                        // manifest we can't read; the keyframe redefines the lot
                        // within five seconds, and the repeat of recent keys
                        // usually fixes it in the next packet.
                        UnknownKeys++;
                        // Half an alignment is worse than none: if the next
                        // packet's body count happened to match, poses would be
                        // written against the wrong bodies.
                        w.Aligned.Clear();
                        w.HaveManifest = false;
                        if (_unknownKeySaid < 3)
                        {
                            _unknownKeySaid++;
                            Plugin.Warn("Waiting on " + m.Label + ": the owner used body number " + id
                                        + " and we haven't been told what that is yet.");
                        }
                        return;
                    }

                    Rigidbody rb;
                    if (_byKey.TryGetValue(key, out rb)) { w.Aligned.Add(rb); matched++; }
                    else
                    {
                        var stand = StandIn(w, m, key);
                        w.Aligned.Add(stand);
                        if (stand != null) stood++;
                        else if (firstMiss == null) firstMiss = key;
                    }
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

                // Back-to-back rounds share one lease, and round two can spawn
                // fewer objects than round one. A stand-in the new manifest
                // doesn't name is never placed again, so it would just sit where
                // it was last driven — a chip parked in mid-air that nobody owns.
                if (w.StandIns.Count > 0)
                {
                    foreach (var kv in w.StandIns)
                    {
                        var srb = kv.Value;
                        if (srb == null) continue;
                        bool wanted = w.AlignedSet.Contains(srb) && !w.StandInWaiting.Contains(srb);
                        if (srb.gameObject.activeSelf != wanted) srb.gameObject.SetActive(wanted);
                    }
                }

                w.HaveManifest = true;
                w.Matched = matched;
                w.Stood = stood;
                if (Report != null) Report.Bodies(m.Id, m.Label, _scratch.Count, count, matched, stood);
                w.Unmatched = count - matched;
                while (w.Targets.Count < count) w.Targets.Add(new Target());

                // A manifest can renumber the slots — a chip spawning in the
                // middle of the walk pushes every later body down one. Now that
                // a manifest no longer carries every pose, a slot's old target
                // would be driving whatever body has landed in that slot, so
                // every target is parked until a pose for it actually arrives.
                // Nothing moves on its own in the meantime; they are all
                // kinematic while we watch.
                for (int i = 0; i < w.Targets.Count; i++) w.Targets[i].At = 0f;
                for (int i = 0; i < w.LastPos.Count; i++)
                    w.LastPos[i] = new Vector3(-99999f, -99999f, -99999f);

                // Said when the picture changes, not once per packet: two saves
                // holding different numbers of coins is the steady state, and a
                // gap that MOVES is the interesting part.
                string mismatch = _scratch.Count + "/" + count + "/" + matched + "/" + stood;
                if (mismatch != w.LastMismatch)
                {
                    w.LastMismatch = mismatch;
                    // Standing in for a body is an explanation, so it is not a
                    // mismatch. Counting raw body totals made the alarm fire 48
                    // times in a session where every single object was accounted
                    // for, which teaches a tester to ignore the line.
                    if (count - matched - stood > 0) CountMismatches++;
                    Plugin.Log("Physics: " + m.Label + " has " + _scratch.Count + " moving parts here and " + count
                               + " on the player's screen. Matched " + matched + " by name"
                               + (stood > 0 ? ", " + stood + " stood in for" : "")
                               + (count - matched - stood > 0
                                  ? ", " + (count - matched - stood) + " have no counterpart here (e.g. " + firstMiss + ")" : "")
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

            // A sparse packet names the bodies it carries; a keyframe carries
            // all of them in order, as every packet used to.
            bool sparse = (flags & FlagSparse) != 0;
            int poses = count;
            if (sparse)
            {
                if (o + 2 > data.Length) return;
                poses = ReadU16(data, ref o);
                if (poses > count) return;
            }
            if (data.Length < o + poses * (sparse ? 17 : 15)) return;

            // One slot per body, not per pose, now that poses arrive out of
            // order. Seeded far away so the first pose for a body always counts
            // as a change rather than silently matching a zero.
            while (w.LastPos.Count < count) w.LastPos.Add(new Vector3(-99999f, -99999f, -99999f));

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
                while (t != null && t != m.Play)
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
            if (w.LastPacketAt >= 0f)
            {
                float gap = now - w.LastPacketAt;
                if (gap > 0.005f && gap < 0.5f) w.Gap = Mathf.Lerp(w.Gap, gap, 0.1f);
            }
            w.LastPacketAt = now;

            for (int k = 0; k < poses; k++)
            {
                int i = sparse ? ReadU16(data, ref o) : k;
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

                // The bytes are read before this check so a bad index costs one
                // body, not the rest of the packet.
                if (i < 0 || i >= count) continue;

                if ((lp - w.LastPos[i]).sqrMagnitude > 0.001f * 0.001f) changed++;
                w.LastPos[i] = lp;

                var rb = w.Aligned[i];
                if (rb == null) continue;
                if (rb.gameObject.activeSelf != on) SetActiveRemembering(w, rb.gameObject, on);

                var t = w.Targets[i];
                var newPos = origin + rot * lp;
                var newRot = rot * Normalise(lr);

                // The first pose a stand-in ever gets puts it there outright.
                // Easing it in from wherever the prefab was would drag it across
                // the room in front of everyone.
                bool first = w.StandInWaiting.Remove(rb);
                if (first)
                {
                    rb.transform.position = newPos;
                    rb.transform.rotation = newRot;
                }

                // Ease from where it's drawn to the new pose over one packet
                // interval, measured, not a fixed 0.1 s. Packets come every
                // ~50 ms, so the old fixed ease never finished: each packet
                // restarted it with half the distance left, and the speed of
                // anything moving steadily — the Treasure shovel — lurched
                // twenty times a second. Taking exactly one interval means a
                // steady mover is drawn at a steady speed.
                t.FromPos = rb.transform.position;
                t.FromRot = rb.transform.rotation;
                t.FromAt = now;
                t.Dur = w.Gap * 1.1f;
                t.Pos = newPos;
                t.Rot = newRot;
                t.At = now;
                applied++;
            }

            // The reader's half of the size check. Every byte of a packet
            // should be spoken for by the time the poses are read; if not, the
            // two sides disagree about the format, and the bodies are being
            // placed from the wrong bytes no matter what the counters say.
            if (o != data.Length)
            {
                ReadMismatches++;
                if (_readComplaints < 3)
                {
                    _readComplaints++;
                    Plugin.Warn("Physics packet for " + m.Label + " was " + data.Length + " bytes and we read " + o
                                + " - " + poses + " of " + count + " bodies" + (manifest ? ", manifest" : "")
                                + (sparse ? ", sparse" : "") + ". The two sides disagree about the format.");
                }
            }

            BodiesApplied += applied;
            PacketsReceived++;
            RecvPosesLastPacket = poses;
            RecvChangedLastPacket = changed;
            if (changed > RecvChangedPeak) RecvChangedPeak = changed;
            if (Report != null) Report.Packet(m.Id, changed);
            if (now >= _nextRecvSummaryAt)
            {
                _nextRecvSummaryAt = now + SummaryEvery;
                Plugin.Log("Watching " + m.Label + ": " + poses + " of " + count
                           + " bodies came in, " + changed + " of them had moved (peak " + RecvChangedPeak + ", "
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

                    var tr = rb.transform;
                    Vector3 last;
                    if (w.Wrote.TryGetValue(rb, out last))
                    {
                        float off = (tr.position - last).magnitude;
                        if (off > 0.001f)
                        {
                            w.Overridden++;
                            if (off > w.OverriddenWorst)
                            {
                                w.OverriddenWorst = off;
                                w.OverriddenExample = rb.name;
                            }
                        }
                    }

                    float k = t.Dur <= 0.0001f ? 1f : Mathf.Clamp01((now - t.FromAt) / t.Dur);
                    var pos = Vector3.Lerp(t.FromPos, t.Pos, k);
                    var rotq = Quaternion.Slerp(t.FromRot, t.Rot, k);
                    tr.position = pos;
                    tr.rotation = rotq;
                    t.DrawnPos = pos;
                    t.DrawnRot = rotq;
                    w.Wrote[rb] = pos;
                    w.Writes++;
                }
            }

            if (now >= _nextSeenAt)
            {
                _nextSeenAt = now + SummaryEvery;
                foreach (var kv in _watched) SaySeen(kv.Value);
            }
        }

        private float _nextSeenAt;

        /// <summary>
        /// Only rigidbodies are streamed. A part that moves because its PARENT
        /// is moved — Treasure's shovel hangs off X/Y/Z carriages the joystick
        /// controller slides around — is placed right, but the carriages
        /// themselves, if they draw anything, stay parked on the watcher's
        /// side. This names any such parent once, so we know whether there's
        /// a visible arm being left behind.
        /// </summary>
        private static void SayUnsentParents(Machine m, List<Rigidbody> bodies)
        {
            var named = new HashSet<Transform>();
            string list = null;
            for (int i = 0; i < bodies.Count; i++)
            {
                var rb = bodies[i];
                if (rb == null) continue;
                var t = rb.transform.parent;
                while (t != null && t != m.Play)
                {
                    if (named.Add(t) && t.GetComponent<Rigidbody>() == null)
                    {
                        var r = t.GetComponent<Renderer>();
                        int kids = 0;
                        for (int c = 0; c < t.childCount; c++)
                        {
                            var ch = t.GetChild(c);
                            if (ch.GetComponent<Renderer>() != null && ch.GetComponent<Rigidbody>() == null) kids++;
                        }
                        if (r != null || kids > 0)
                            list = (list == null ? "" : list + "  ") + t.name + (r != null ? "(draws)" : "") + (kids > 0 ? "(+" + kids + " drawn children)" : "");
                    }
                    t = t.parent;
                }
            }
            Plugin.Log(list == null
                ? "Parents of " + m.Label + "'s moving parts: none draw anything, so nothing's left behind."
                : "Parents of " + m.Label + "'s moving parts that draw but aren't sent: " + list);
        }

        public int InterpolationOff;

        /// <summary>
        /// Runs after the cabinet's own LateUpdate logic. Treasure's joystick
        /// controller positions the shovel's carriage in LateUpdate, after our
        /// Update had placed the shovel — so on those frames the frame that got
        /// drawn was the controller's, not ours: "something here moved a body
        /// after we placed it", 20-odd times every five seconds, always the
        /// shovel. Putting the pose back here means what's drawn is ours.
        /// </summary>
        public void LateRender()
        {
            if (_watched.Count == 0) return;
            foreach (var kv in _watched)
            {
                var w = kv.Value;
                for (int i = 0; i < w.Aligned.Count && i < w.Targets.Count; i++)
                {
                    var rb = w.Aligned[i];
                    if (rb == null) continue;
                    var t = w.Targets[i];
                    if (t.At <= 0f) continue;
                    var tr = rb.transform;
                    if ((tr.position - t.DrawnPos).sqrMagnitude > 0.000001f)
                    {
                        w.LateFixes++;
                        tr.position = t.DrawnPos;
                        tr.rotation = t.DrawnRot;
                    }
                }
            }
        }

        /// <summary>
        /// A cabinet at rest doesn't draw its coins and chests one by one. The
        /// OBJECTS controller saves where everything lies, destroys the real
        /// objects, and draws the lot as ONE combined mesh (BATCHER_n, kept in
        /// its "CombinedMesh" variable). A card going in spawns the real objects
        /// back from the saved list and throws the combined mesh away.
        ///
        /// The watcher's round is muted, so neither half of that happens here:
        /// the watcher's own resting layout stays drawn, frozen, and the
        /// owner's pieces are drawn moving on top of it. That's "the bits fall
        /// but some are frozen" — the frozen ones were never the owner's at
        /// all. So the resting display goes out of sight while we watch, and
        /// comes back exactly as it was on release.
        /// </summary>
        private void HideRestingDisplay(Watched w, Machine m)
        {
            var seen = new HashSet<GameObject>();
            int renderers = 0;
            string names = null;
            try
            {
                foreach (var kv in m.Fsms)
                {
                    var fsm = kv.Value;
                    if (fsm == null) continue;
                    HutongGames.PlayMaker.FsmGameObject v = null;
                    try { v = fsm.FsmVariables.FindFsmGameObject("CombinedMesh"); } catch { }
                    var go = v != null ? v.Value : null;
                    if (go == null || !seen.Add(go)) continue;
                    // Never anything we place: a body under it would be hidden
                    // along with it.
                    if (go.GetComponentInChildren<Rigidbody>(true) != null) continue;
                    if (!go.activeSelf) continue;
                    renderers += go.GetComponentsInChildren<Renderer>(true).Length;
                    names = names == null ? go.name : names + ", " + go.name;
                    SetActiveRemembering(w, go, false);
                }
            }
            catch (Exception ex)
            {
                Plugin.Warn("Couldn't look for " + m.Label + "'s resting display: " + ex.Message);
                return;
            }

            RestingDisplaysHidden += seen.Count > 0 && names != null ? 1 : 0;
            Plugin.Log(names != null
                ? "Hid " + m.Label + "'s resting display (" + names + ", " + renderers + " renderers) - it's this side's last layout, frozen, and the owner's pieces are drawn instead."
                : "No resting display on " + m.Label + " to hide (no CombinedMesh in use).");
        }
        public int RestingDisplaysHidden;
        public int ReadMismatches;
        private int _readComplaints;

        /// <summary>
        /// "Watching X: N bodies came in" counts what arrived. It can't tell
        /// you whether anyone could see it, and a watcher looking at a dead
        /// machine while every counter said ok is exactly how 0.16.7 got
        /// reported. So this asks the picture itself: of the bodies we're
        /// placing, how many are switched on, how many have a renderer on, how
        /// many a camera is actually drawing — and did something on this side
        /// move them back after we placed them.
        /// </summary>
        private void SaySeen(Watched w)
        {
            int placed = 0, active = 0, drawn = 0, onCamera = 0;
            for (int i = 0; i < w.Aligned.Count && i < w.Targets.Count; i++)
            {
                var rb = w.Aligned[i];
                if (rb == null || w.Targets[i].At <= 0f) continue;
                placed++;
                if (!rb.gameObject.activeInHierarchy) continue;
                active++;
                bool anyOn = false, anySeen = false;
                var rs = rb.GetComponentsInChildren<Renderer>(false);
                for (int r = 0; r < rs.Length; r++)
                {
                    if (rs[r] == null || !rs[r].enabled) continue;
                    anyOn = true;
                    if (rs[r].isVisible) { anySeen = true; break; }
                }
                if (anyOn) drawn++;
                if (anySeen) onCamera++;
            }
            if (placed == 0 && w.Writes == 0) return;

            float fps = Time.unscaledDeltaTime > 0f ? 1f / Time.unscaledDeltaTime : 0f;
            Plugin.Log("Seen on " + (w.Label ?? "?") + ": " + placed + " placed, " + active + " switched on, "
                       + drawn + " with a renderer on, " + onCamera + " on a camera. "
                       + (w.Overridden > 0
                          ? w.Overridden + " times something here moved a body after we placed it (worst " + w.OverriddenWorst.ToString("0.00")
                            + " m, " + w.OverriddenExample + ")"
                          : "Nothing here moved them after we did")
                       + ". " + w.LateFixes + " put back after the cabinet's late update. " + w.Writes + " writes. This window: " + fps.ToString("0") + " fps, "
                       + (Application.isFocused ? "focused" : "not focused") + ", packets every " + (w.Gap * 1000f).ToString("0") + " ms"
                       + (InterpolationOff > 0 ? ", " + InterpolationOff + " bodies had physics interpolation switched off" : "") + ".");
            w.Overridden = 0; w.Writes = 0; w.LateFixes = 0; w.OverriddenWorst = 0f; w.OverriddenExample = null;
        }

        /// <summary>Hands the machine back to local physics when we stop spectating it.</summary>
        /// <summary>
        /// A machine's round can CREATE bodies. Cuckoo went from 174 bodies to
        /// 214 while the host played it — forty chips spawned into the playfield
        /// — and the watcher had none of them, because the watcher's copy of the
        /// round is muted precisely so it doesn't spawn its own. So the watcher
        /// stood there seeing the pusher sweep back and forth over an empty
        /// playfield while the owner saw it shoving forty chips around.
        ///
        /// They can't be matched, so they get built: the prefab is found by
        /// name, cloned, stripped to its renderers and driven like any other
        /// body. Exactly what loose tickets on the floor already do, which is
        /// where the prefab lookup comes from.
        ///
        /// Only "(Clone)" keys qualify. A key that misses without being a clone
        /// is a structural difference between the two scenes — the claw's rope
        /// links were one — and inventing an object for that would be guessing.
        /// </summary>
        private Rigidbody StandIn(Watched w, Machine m, string key)
        {
            Rigidbody have;
            if (w.StandIns.TryGetValue(key, out have) && have != null) return have;
            if (w.StandInGaveUp.Contains(key)) return null;

            int cut = key.LastIndexOf('/');
            string leaf = cut >= 0 ? key.Substring(cut + 1) : key;
            int hash = leaf.IndexOf('#');
            if (hash >= 0) leaf = leaf.Substring(0, hash);
            if (!leaf.EndsWith("(Clone)", StringComparison.Ordinal)) { w.StandInGaveUp.Add(key); return null; }

            // A cap, because the cost of being wrong about this is a machine
            // that grows clones forever and a frame rate nobody can explain.
            if (w.StandIns.Count >= StandInCap)
            {
                if (!w.StandInCapSaid)
                {
                    w.StandInCapSaid = true;
                    Plugin.Warn("Not showing any more of " + m.Label + "'s spawned objects — already standing in for "
                                + StandInCap + ", which is more than a cabinet should ever hold.");
                }
                return null;
            }

            string prefab = leaf.Substring(0, leaf.Length - "(Clone)".Length);
            var src = LooseItems.FindPrefab(prefab);
            if (src == null)
            {
                w.StandInGaveUp.Add(key);
                StandInMisses++;
                if (_standInMissSaid.Add(prefab))
                    Plugin.Warn("No loaded prefab called \"" + prefab + "\" to stand in for one of "
                                + m.Label + "'s spawned objects.");
                return null;
            }

            GameObject go;
            try { go = UnityEngine.Object.Instantiate(src); }
            catch (Exception ex)
            {
                w.StandInGaveUp.Add(key);
                Plugin.Warn("Couldn't clone " + prefab + " for " + m.Label + ": " + ex.Message);
                return null;
            }

            go.name = OursPrefix + w.StandIns.Count;
            // Off until a pose for it turns up. A stand-in has no idea where it
            // belongs when it is made, and the prefab's own position is usually
            // nowhere useful — three hundred chests appearing in a heap for a
            // moment is worse than three hundred appearing a little late.
            go.SetActive(false);
            AvatarFactory.Strip(go);
            try { go.transform.SetParent(m.Root, true); } catch { }

            // Strip takes the rigidbody with everything else; it gets one back,
            // kinematic, because every path downstream of here expects to be
            // handed a Rigidbody and to place it by hand.
            var rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.detectCollisions = false;

            w.StandIns[key] = rb;
            w.StandInWaiting.Add(rb);
            StandInsBuilt++;
            if (StandInsBuilt <= 8)
                Plugin.Log("Standing in for " + m.Label + "'s " + prefab + " — the owner's round spawned it and ours didn't.");
            return rb;
        }

        /// <summary>
        /// Cuckoo's round spawns about two hundred chips and coins, and the
        /// first cap of 128 was reached 1.3 seconds into the round — so seventy
        /// of them stayed invisible to the watcher, which was the whole thing
        /// this was built to fix. A hundred and twenty-eight stand-ins cost
        /// 0.04 ms a frame, so the ceiling is somewhere far above this.
        /// </summary>
        private const int StandInCap = 512;
        private readonly HashSet<string> _standInMissSaid = new HashSet<string>();
        private int _unknownKeySaid;
        public int UnknownKeys;
        public int StandInsBuilt, StandInMisses;

        public void ReleaseMachine(uint machineId)
        {
            Watched w;
            if (!_watched.TryGetValue(machineId, out w)) return;

            // Everything we switched on or off goes back exactly as it was.
            foreach (var kv in w.Originals)
                if (kv.Key != null) kv.Key.SetActive(kv.Value);
            Quiet.Restore(w.FsmWas);

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
                rb.interpolation = kv.Value.Interp;
                if (!kv.Value.Kinematic)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    rb.WakeUp();
                }
            }

            // The stand-ins were never part of this machine, so they leave with
            // the lease rather than being put back.
            foreach (var kv in w.StandIns)
            {
                if (kv.Value == null) continue;
                try { UnityEngine.Object.Destroy(kv.Value.gameObject); } catch { }
            }
            w.StandIns.Clear();

            _watched.Remove(machineId);
        }

        private static void SetActiveRemembering(Watched w, GameObject go, bool on)
        {
            if (go == null || go.activeSelf == on) return;
            if (!w.Originals.ContainsKey(go)) w.Originals[go] = go.activeSelf;
            if (on) Quiet.Activate(go, w.FsmWas);
            else go.SetActive(false);
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
