using System;
using System.Reflection;
using UnityEngine;

namespace TcgMultiplayer.Game
{
    /// <summary>
    /// Finds and reads the local player.
    ///
    /// Correction to the M0 write-up: there IS a C# movement class after all —
    /// UnitySampleAssets.Characters.FirstPerson.RigidbodyFirstPersonController,
    /// living in Assembly-CSharp-firstpass (not Assembly-CSharp, which is why the
    /// first sweep missed it). It exposes Velocity / Grounded / Jumping / Running,
    /// which is exactly the animation state a remote avatar needs.
    ///
    /// Read by reflection: firstpass is a game assembly that will churn between
    /// builds, and a missing property should degrade the animation, not kill the mod.
    /// </summary>
    internal sealed class PlayerRig
    {
        public const string RootPath = "PLAYER";
        public const string MeshChild = "LARRY Mesh";
        public const string CameraChild = "Main Player-Camera";

        public Transform Root { get; private set; }
        public Transform Mesh { get; private set; }
        public Transform Cam { get; private set; }
        public bool Valid { get { return Root != null && Mesh != null; } }

        private Component _controller;
        private PropertyInfo _pVelocity, _pGrounded, _pJumping, _pRunning;
        private bool _controllerProbed;

        private Vector3 _lastPos;
        private float _lastPosTime;
        private float _fallbackSpeed;

        public bool Acquire()
        {
            if (Valid) return true;

            var root = GameObject.Find(RootPath);
            if (root == null) return false;

            Root = root.transform;
            Mesh = Root.Find(MeshChild);
            Cam = Root.Find(CameraChild);

            if (Mesh == null)
            {
                Plugin.Warn("Found PLAYER but no '" + MeshChild + "' child — avatar cloning will not work.");
                return false;
            }

            ProbeController();
            _lastPos = Root.position;
            _lastPosTime = Time.time;
            Plugin.Log("Player rig acquired" + (_controller != null ? " (with movement controller)" : " (transform only)"));
            return true;
        }

        public void Forget()
        {
            Root = null; Mesh = null; Cam = null;
            _controller = null; _controllerProbed = false;
            _pVelocity = _pGrounded = _pJumping = _pRunning = null;
        }

        private void ProbeController()
        {
            if (_controllerProbed) return;
            _controllerProbed = true;
            try
            {
                var comps = Root.GetComponents<Component>();
                foreach (var c in comps)
                {
                    if (c == null) continue;
                    var t = c.GetType();
                    if (t.Name != "RigidbodyFirstPersonController") continue;
                    _controller = c;
                    _pVelocity = t.GetProperty("Velocity", BindingFlags.Public | BindingFlags.Instance);
                    _pGrounded = t.GetProperty("Grounded", BindingFlags.Public | BindingFlags.Instance);
                    _pJumping = t.GetProperty("Jumping", BindingFlags.Public | BindingFlags.Instance);
                    _pRunning = t.GetProperty("Running", BindingFlags.Public | BindingFlags.Instance);
                    break;
                }
                if (_controller == null)
                    Plugin.Warn("RigidbodyFirstPersonController not on PLAYER — falling back to transform-delta speed.");
            }
            catch (Exception ex) { Plugin.Warn("Controller probe failed: " + ex.Message); }
        }

        // ------------------------------------------------------------ sampling

        public PlayerState Sample()
        {
            var s = new PlayerState();
            if (!Valid) return s;

            // Sample the mesh, not the PLAYER root. The remote body is a clone of
            // "LARRY Mesh", which sits at a local offset inside PLAYER — driving the
            // clone from the root's position would plant it at the wrong height.
            s.Pos = Mesh.position;
            s.Yaw = Mesh.eulerAngles.y;
            s.Pitch = Cam != null ? NormalisePitch(Cam.localEulerAngles.x) : 0f;

            float speed;
            if (_controller != null && _pVelocity != null)
            {
                try
                {
                    var v = (Vector3)_pVelocity.GetValue(_controller, null);
                    speed = new Vector2(v.x, v.z).magnitude;
                }
                catch { speed = FallbackSpeed(); }
            }
            else speed = FallbackSpeed();

            s.Speed = speed;
            s.Grounded = ReadBool(_pGrounded, true);
            s.Running = ReadBool(_pRunning, false);
            s.Jumping = ReadBool(_pJumping, false);
            return s;
        }

        private float FallbackSpeed()
        {
            var now = Time.time;
            var dt = now - _lastPosTime;
            if (dt > 0.01f)
            {
                var d = Root.position - _lastPos;
                d.y = 0f;
                _fallbackSpeed = d.magnitude / dt;
                _lastPos = Root.position;
                _lastPosTime = now;
            }
            return _fallbackSpeed;
        }

        private bool ReadBool(PropertyInfo p, bool fallback)
        {
            if (_controller == null || p == null) return fallback;
            try { return (bool)p.GetValue(_controller, null); }
            catch { return fallback; }
        }

        private static float NormalisePitch(float x)
        {
            return x > 180f ? x - 360f : x;
        }
    }

    public struct PlayerState
    {
        public Vector3 Pos;
        public float Yaw;
        public float Pitch;
        public float Speed;
        public bool Grounded;
        public bool Running;
        public bool Jumping;

        public byte Flags
        {
            get
            {
                byte f = 0;
                if (Grounded) f |= 1;
                if (Running) f |= 2;
                if (Jumping) f |= 4;
                return f;
            }
            set
            {
                Grounded = (value & 1) != 0;
                Running = (value & 2) != 0;
                Jumping = (value & 4) != 0;
            }
        }
    }
}
