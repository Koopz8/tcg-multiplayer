using System;
using System.Collections.Generic;
using UnityEngine;

namespace TcgMultiplayer.Game
{
    /// <summary>
    /// Solo verification for the parts of the mod that normally need two people.
    ///
    /// Machine ownership, spectating and the wallet guard all only fire when
    /// someone *else* is playing — which, with one copy of the game, is never. So
    /// Rehearsal records the FSM events of a round you play yourself, hands the
    /// machine to a fake peer, and replays the recording through the real
    /// spectator path at the real timings.
    ///
    /// What that actually exercises: the machine registry resolved the right
    /// FSMs, event replay drives the cabinet, and the wallet guard holds your
    /// balance still while someone else's round plays out. If your coins move
    /// during a rehearsal, the guard is broken — and you can see that alone, in
    /// about a minute, instead of discovering it with a friend three milestones
    /// later.
    /// </summary>
    internal sealed class Rehearsal
    {
        private struct Beat
        {
            public float T;        // seconds since recording started
            public uint FsmId;
            public string Event;
        }

        public const ulong FakePeerId = 2UL;      // not a valid SteamID
        public const string FakePeerName = "Rehearsal";

        private struct Frame
        {
            public float T;
            public byte[] Payload;
        }

        private readonly List<Beat> _tape = new List<Beat>(256);
        private readonly List<Frame> _film = new List<Frame>(512);
        private int _filmCursor;
        private float _recordStart;
        private uint _recordedMachine;
        private string _recordedLabel = "";

        // playback
        private Machine _held;          // the machine we borrowed; always given back
        private bool _playing;
        private int _cursor;
        private float _playStart;
        private ulong _savedOwner;
        private string _savedOwnerName;
        private bool _savedOwnedByMe;

        public bool Recording { get; private set; }
        public bool Playing { get { return _playing; } }
        public int TapeLength { get { return _tape.Count; } }
        public string RecordedLabel { get { return _recordedLabel; } }
        public int WalletMovedDuringPlayback { get; private set; }

        public bool HasTape { get { return (_tape.Count > 0 || _film.Count > 0) && _recordedMachine != 0; } }
        public int FilmLength { get { return _film.Count; } }

        // ------------------------------------------------------------ recording

        public void StartRecording(uint machineId, string label)
        {
            _tape.Clear();
            _film.Clear();
            _recordedMachine = machineId;
            _recordedLabel = label ?? "";
            _recordStart = Time.time;
            Recording = true;
            Plugin.Log("Rehearsal: recording " + _recordedLabel);
        }

        public void StopRecording()
        {
            if (!Recording) return;
            Recording = false;
            Plugin.Log("Rehearsal: recorded " + _tape.Count + " events and "
                       + _film.Count + " physics frames from " + _recordedLabel);
        }

        public void Note(uint machineId, uint fsmId, string evt)
        {
            if (!Recording || machineId != _recordedMachine) return;
            if (_tape.Count >= 2048) return;                 // a round is dozens, not thousands
            _tape.Add(new Beat { T = Time.time - _recordStart, FsmId = fsmId, Event = evt });
        }

        /// <summary>
        /// Physics goes on the same tape, so a rehearsal replays the coins as well
        /// as the logic — otherwise M6 would be the one layer that still needs a
        /// second person to test.
        /// </summary>
        public void NoteFrame(uint machineId, byte[] payload)
        {
            if (!Recording || machineId != _recordedMachine || payload == null) return;
            if (_film.Count >= 4096) return;
            _film.Add(new Frame { T = Time.time - _recordStart, Payload = payload });
        }

        // ------------------------------------------------------------- playback

        /// <summary>
        /// Hands the machine to a fake peer for the duration, so the replay takes
        /// the genuine spectator branch rather than a special case.
        /// </summary>
        public bool Play(Machine m)
        {
            if (_playing || !HasTape || m == null || m.Id != _recordedMachine) return false;

            _held = m;
            _savedOwner = m.Owner;
            _savedOwnerName = m.OwnerName;
            _savedOwnedByMe = m.OwnedByMe;

            m.Owner = FakePeerId;
            m.OwnerName = FakePeerName;
            m.OwnedByMe = false;

            _playing = true;
            _cursor = 0;
            _filmCursor = 0;
            _playStart = Time.time;
            WalletMovedDuringPlayback = 0;
            Plugin.Log("Rehearsal: replaying " + _tape.Count + " events on " + _recordedLabel + " as " + FakePeerName);
            return true;
        }

        public void Stop(Machine m)
        {
            if (!_playing) return;
            _playing = false;

            // Give the machine back to whoever had it, using our own reference —
            // an earlier version relied on the caller passing it and handed null
            // on the failure path, which left the cabinet owned by a peer that
            // does not exist and frozen forever.
            var target = m ?? _held;
            if (target != null)
            {
                target.Owner = _savedOwner;
                target.OwnerName = _savedOwnerName;
                target.OwnedByMe = _savedOwnedByMe;
            }
            _held = null;
            Plugin.Log("Rehearsal: done. Wallet moved " + WalletMovedDuringPlayback
                       + " time(s) during playback" + (WalletMovedDuringPlayback == 0
                            ? " — guard held." : " — GUARD LEAKED."));
        }

        /// <summary>Feeds due events and physics frames back through the real spectator handlers.</summary>
        public void Tick(Machine m, Action<uint, uint, string> applyAsRemote,
                         Action<Machine, byte[]> applyPhysics, Func<int> walletRestores)
        {
            if (!_playing) return;
            if (m == null) { Stop(null); return; }   // still hands the machine back

            int before = walletRestores();
            float now = Time.time - _playStart;

            while (_cursor < _tape.Count && _tape[_cursor].T <= now)
            {
                var b = _tape[_cursor++];
                applyAsRemote(m.Id, b.FsmId, b.Event);
            }

            while (_filmCursor < _film.Count && _film[_filmCursor].T <= now)
            {
                var f = _film[_filmCursor++];
                if (applyPhysics != null) applyPhysics(m, f.Payload);
            }

            WalletMovedDuringPlayback += Mathf.Max(0, walletRestores() - before);

            if (_cursor >= _tape.Count && _filmCursor >= _film.Count) Stop(m);
        }

        public uint RecordedMachine { get { return _recordedMachine; } }
    }
}
