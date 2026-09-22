using System;
using UnityEngine;

namespace TcgMultiplayer.Game
{
    /// <summary>Which thing the local player is riding, and in which seat.</summary>
    public struct Attachment
    {
        public uint Machine;
        public byte Seat;
        public bool Any { get { return Machine != 0; } }
    }

    /// <summary>
    /// Getting into a friend's car.
    ///
    /// The obvious implementation is the wrong one. If the passenger enters the
    /// vehicle through the game — walks up, presses the use key, the graph seats
    /// them — then their copy of the car starts being driven locally, by them,
    /// while the driver's stream is also writing to it. Two authorities, one
    /// transform, and the car tears itself in half.
    ///
    /// So a passenger never touches the vehicle's graph at all. They are moved
    /// to a seat and held there: their own body is switched off physically and
    /// re-pinned to the vehicle every frame. To the game nothing has happened —
    /// the player simply happens to be standing somewhere that moves.
    ///
    /// Deliberately NOT done by reparenting the player under the vehicle. A
    /// parented transform survives its parent being deactivated, despawned or
    /// unloaded on a scene change in ways that leave the player somewhere
    /// unreachable; re-pinning every frame stops the instant anything goes
    /// wrong, which is exactly the failure mode you want. For the same reason
    /// this does not touch the Rewired input lock: disabling the input maps
    /// would also kill the game's own menus, and a passenger who cannot open
    /// the pause menu is a passenger who thinks the game has frozen.
    /// </summary>
    internal sealed class RideAlong
    {
        public uint Riding;
        public int Seat = -1;
        public string Status = "";

        /// <summary>Machine we have asked to board and are waiting on the host about.</summary>
        public uint Pending;
        public float PendingSince;

        private Rigidbody _body;
        private bool _wasKinematic;

        public bool Active { get { return Riding != 0; } }

        public Attachment Current
        {
            get
            {
                var a = new Attachment();
                if (Riding != 0 && Seat >= 0) { a.Machine = Riding; a.Seat = (byte)Seat; }
                return a;
            }
        }

        /// <summary>
        /// Take a seat. Called once the host has said which one — never
        /// speculatively, or two passengers end up in the same lap.
        /// </summary>
        public bool Board(Machine m, int seat, PlayerRig rig)
        {
            if (m == null || m.Root == null || rig == null || !rig.Valid) return false;
            if (seat < 0) return false;

            if (Riding != 0 && Riding != m.Id) Leave(rig, "moved to another seat");

            Riding = m.Id;
            Seat = seat;
            Pending = 0;
            m.MySeat = seat;

            // Stop our own physics fighting the pin. Without this the character
            // controller keeps trying to fall, wins for one frame in every few,
            // and the passenger vibrates through the floor of the car.
            _body = rig.Root.GetComponent<Rigidbody>();
            if (_body != null)
            {
                _wasKinematic = _body.isKinematic;
                _body.isKinematic = true;
            }

            Status = "riding in " + m.Label + ", seat " + (seat + 1) + " of " + m.Capacity;
            Plugin.Log("Ride: " + Status);
            return true;
        }

        /// <summary>
        /// Get out — by choice, because the driver left, because the vehicle
        /// went away, or because the panic key was pressed. Every one of those
        /// has to land in the same place, so there is exactly one way out.
        /// </summary>
        public void Leave(PlayerRig rig, string why)
        {
            if (Riding == 0) return;

            if (_body != null)
            {
                try { _body.isKinematic = _wasKinematic; } catch { }
                _body = null;
            }

            Plugin.Log("Ride: got out (" + (why ?? "no reason given") + ").");
            Riding = 0;
            Seat = -1;
            Pending = 0;
            Status = "on foot";
        }

        /// <summary>
        /// Hold the player in their seat. Called every frame while riding.
        /// Returns false if the ride can no longer be held, which the caller
        /// turns into a dismount — a passenger pinned to a vehicle that has
        /// stopped existing is the worst thing this feature can do to someone.
        /// </summary>
        public bool Hold(Machine m, PlayerRig rig)
        {
            if (Riding == 0) return true;
            if (m == null || m.Root == null || rig == null || !rig.Valid || rig.Mesh == null) return false;

            try
            {
                var target = m.Root.TransformPoint(Seating.Offset(Seat, m.Extents));

                // The seat offset positions the BODY, but what we can actually
                // move is the PLAYER root, and the mesh hangs off it at an
                // offset. Carry that offset across or everyone rides half a
                // metre underground.
                var meshToRoot = rig.Root.position - rig.Mesh.position;
                rig.Root.position = target + meshToRoot;
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Warn("Ride: could not hold the seat: " + ex.Message);
                return false;
            }
        }

        /// <summary>A request that never got an answer shouldn't hang around forever.</summary>
        public const float RequestTimeout = 4f;

        public bool RequestExpired(float now)
        {
            return Pending != 0 && now - PendingSince >= RequestTimeout;
        }
    }
}
