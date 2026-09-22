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
        private Vector3 _fallbackVel;
        private float _lastYaw;
        private float _turnRate;

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
            _lastPos = Mesh.position;
            _lastPosTime = Time.time;
            _lastYaw = Mesh.eulerAngles.y;
            Plugin.Log("Player rig acquired" + (_controller != null ? " (with movement controller)" : " (transform only)"));
            CompatCheck.Set("player rig (" + RootPath + "/" + MeshChild + ")", true, null);
            CompatCheck.Set("movement controller", _controller != null,
                            _controller == null ? "falling back to transform deltas" : null);
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

            // The rig's locomotion is a 2D blend (MoveSpeedX strafe, MoveSpeedY
            // forward), so a single scalar speed is not enough — velocity has to
            // cross the wire in the body's own frame.
            Vector3 world;
            if (_controller != null && _pVelocity != null)
            {
                try { world = (Vector3)_pVelocity.GetValue(_controller, null); }
                catch { world = FallbackVelocity(); }
            }
            else world = FallbackVelocity();

            var local = Mesh.InverseTransformDirection(new Vector3(world.x, 0f, world.z));
            s.VelX = local.x;
            s.VelZ = local.z;
            s.Turn = TurnRate(s.Yaw);

            s.Grounded = ReadBool(_pGrounded, true);
            s.Running = ReadBool(_pRunning, false);
            s.Jumping = ReadBool(_pJumping, false);
            return s;
        }

        private Vector3 FallbackVelocity()
        {
            var now = Time.time;
            var dt = now - _lastPosTime;
            if (dt > 0.01f)
            {
                var d = Mesh.position - _lastPos;
                d.y = 0f;
                _fallbackVel = d / dt;
                _lastPos = Mesh.position;
                _lastPosTime = now;
            }
            return _fallbackVel;
        }

        private float TurnRate(float yaw)
        {
            var dt = Time.deltaTime;
            if (dt > 0.0001f)
            {
                var delta = Mathf.DeltaAngle(_lastYaw, yaw);
                // Heavily smoothed: mouse-look yaw is noisy frame to frame, and the
                // Turn parameter only needs the general direction of the lean.
                _turnRate = Mathf.Lerp(_turnRate, delta / dt, 0.25f);
            }
            _lastYaw = yaw;
            return _turnRate;
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
        /// <summary>
        /// Where the body is. World space normally — but when <see cref="Attached"/>
        /// is set this is in the vehicle's own space instead, and the receiver
        /// resolves it against their copy of that vehicle.
        ///
        /// That swap is the whole trick. Sending a passenger's world position
        /// while the car they are in is also being streamed means two
        /// independently-interpolated streams have to agree, twenty times a
        /// second, about where a seat is — and they never quite do, so the
        /// passenger shivers in their seat and slides out of it on every corner.
        /// Sent in the car's frame, the offset is a constant and the body is
        /// welded to the seat for free.
        /// </summary>
        public Vector3 Pos;
        public float Yaw;
        public float Pitch;
        public float VelX;     // strafe, in the body's own frame
        public float VelZ;     // forward
        public float Turn;     // yaw rate, deg/s
        public bool Grounded;
        public bool Running;
        public bool Jumping;

        /// <summary>NetId of the vehicle or ride this player is aboard. 0 = on foot.</summary>
        public uint Attached;
        /// <summary>Which seat, when attached. 0 is the driver.</summary>
        public byte Seat;

        public float Speed { get { return Mathf.Sqrt(VelX * VelX + VelZ * VelZ); } }

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
