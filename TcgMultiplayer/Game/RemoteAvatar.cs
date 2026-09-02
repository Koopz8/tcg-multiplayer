using System;
using System.Collections.Generic;
using UnityEngine;

namespace TcgMultiplayer.Game
{
    /// <summary>
    /// One other player's body. Buffers incoming snapshots and renders the past,
    /// which is the standard trade: a fixed delay bought in exchange for smooth
    /// motion instead of teleporting on every packet.
    ///
    /// Timeline is local arrival time, not a synced clock — there is no clock
    /// sync yet, and for interpolation there doesn't need to be.
    /// </summary>
    internal sealed class RemoteAvatar
    {
        private struct Snap
        {
            public float T;          // local time it arrived
            public Vector3 Pos;
            public float Yaw;
            public float Pitch;
            public float Speed;
            public byte Flags;
        }

        public string Label;
        public GameObject Go { get; private set; }
        public bool Alive { get { return Go != null; } }
        public int Buffered { get { return _snaps.Count; } }
        public float LastPacketAge { get { return Time.time - _lastRecv; } }

        private readonly List<Snap> _snaps = new List<Snap>(32);
        private Transform _tf;
        private Animator _animator;
        private AnimatorBinding _bind;
        private float _lastRecv = -999f;
        private bool _warnedNoBinding;

        public static float InterpDelay = 0.12f;

        public bool Spawn(Transform sourceMesh, string label)
        {
            Label = label;
            Go = AvatarFactory.Build(sourceMesh, label);
            if (Go == null) return false;

            _tf = Go.transform;
            _animator = Go.GetComponentInChildren<Animator>();
            _bind = AvatarFactory.BindAnimator(_animator);
            if (_animator != null)
            {
                _animator.applyRootMotion = false;   // we drive position ourselves
                Plugin.Log("Avatar animator binding for " + label + ": " + _bind);
            }
            return true;
        }

        public void Despawn()
        {
            if (Go != null) UnityEngine.Object.Destroy(Go);
            Go = null; _tf = null; _animator = null;
            _snaps.Clear();
        }

        public void Push(Vector3 pos, float yaw, float pitch, float speed, byte flags)
        {
            _lastRecv = Time.time;
            _snaps.Add(new Snap { T = _lastRecv, Pos = pos, Yaw = yaw, Pitch = pitch, Speed = speed, Flags = flags });

            // Keep a second of history; anything older can never be rendered.
            while (_snaps.Count > 2 && _snaps[0].T < _lastRecv - 1.0f) _snaps.RemoveAt(0);
        }

        public void Render()
        {
            if (_tf == null || _snaps.Count == 0) return;

            float renderAt = Time.time - InterpDelay;

            // Not enough history yet, or we've stalled: hold the newest sample.
            if (_snaps.Count == 1 || renderAt >= _snaps[_snaps.Count - 1].T)
            {
                Apply(_snaps[_snaps.Count - 1], 1f, _snaps[_snaps.Count - 1]);
                return;
            }
            if (renderAt <= _snaps[0].T)
            {
                Apply(_snaps[0], 1f, _snaps[0]);
                return;
            }

            for (int i = 0; i < _snaps.Count - 1; i++)
            {
                var a = _snaps[i];
                var b = _snaps[i + 1];
                if (renderAt < a.T || renderAt > b.T) continue;

                float span = b.T - a.T;
                float t = span > 0.0001f ? (renderAt - a.T) / span : 1f;
                Apply(a, t, b);
                return;
            }
        }

        private void Apply(Snap a, float t, Snap b)
        {
            var pos = Vector3.Lerp(a.Pos, b.Pos, t);
            float yaw = Mathf.LerpAngle(a.Yaw, b.Yaw, t);
            float speed = Mathf.Lerp(a.Speed, b.Speed, t);

            _tf.position = pos;
            _tf.rotation = Quaternion.Euler(0f, yaw, 0f);

            if (_animator == null) return;
            if (!_bind.Any)
            {
                if (!_warnedNoBinding)
                {
                    _warnedNoBinding = true;
                    Plugin.Warn("No usable animator parameters on the rig — " + Label + " will slide rather than walk.");
                }
                return;
            }

            try
            {
                bool grounded = (b.Flags & 1) != 0;
                bool running = (b.Flags & 2) != 0;

                if (_bind.SpeedParam != null) _animator.SetFloat(_bind.SpeedParam, speed);
                if (_bind.WalkParam != null) _animator.SetBool(_bind.WalkParam, speed > 0.15f);
                if (_bind.RunParam != null) _animator.SetBool(_bind.RunParam, running);
                if (_bind.GroundedParam != null) _animator.SetBool(_bind.GroundedParam, grounded);
            }
            catch { /* a wrong-typed parameter shouldn't kill the frame */ }
        }

        /// <summary>Screen position for the nameplate, or null if off-screen/behind.</summary>
        public Vector2? NameplatePoint(Camera cam, out float distance)
        {
            distance = 0f;
            if (_tf == null || cam == null) return null;

            var world = _tf.position + Vector3.up * 2.0f;
            distance = Vector3.Distance(cam.transform.position, world);

            var sp = cam.WorldToScreenPoint(world);
            if (sp.z <= 0f) return null;
            if (sp.x < -100f || sp.x > Screen.width + 100f) return null;
            if (sp.y < -100f || sp.y > Screen.height + 100f) return null;

            return new Vector2(sp.x, Screen.height - sp.y);
        }
    }
}
