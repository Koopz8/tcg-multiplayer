using System;
using System.Collections.Generic;
using UnityEngine;

namespace TcgMultiplayer.Game
{
    /// <summary>
    /// Who is sitting where, in something that holds more than one person.
    ///
    /// The game has no passenger concept at all. Its own model is one card, one
    /// machine, one player — which is why a cabinet works and a car does not:
    /// if a second player gets into the driver's seat on their own machine,
    /// they start driving their copy of a car that is already being driven by
    /// someone else's stream, and the two fight.
    ///
    /// So riding along is the mod's, not the game's. The driver claims the
    /// vehicle exactly as they claim a cabinet. A passenger never claims
    /// anything — they take a seat, their own movement is switched off, and
    /// they are carried by the vehicle. Nothing about the game's own graph
    /// changes, which is why this works on all four vehicles without one line
    /// of per-vehicle code.
    ///
    /// Seat 0 is the driver, and is held by whoever owns the machine. Seats
    /// 1..n-1 are passengers. All of it is arbitrated by the host for the same
    /// reason ownership is: two people reaching for the last seat in the same
    /// frame must not both get it.
    ///
    /// Pure. No Unity beyond Vector3, so the rig sweeps every ordering.
    /// </summary>
    public static class Seating
    {
        public const int MaxSeats = 8;
        public const int DriverSeat = 0;

        /// <summary>
        /// How many people fit, from the thing's own size. Guessed from bounds
        /// rather than configured per vehicle, for the same reason mover
        /// classification is: a table of names is a thing that goes stale.
        /// A golf cart gets two, a car gets three or four, the bus gets a lot.
        /// </summary>
        public static int Capacity(Vector3 extents)
        {
            // Footprint, not volume — a tall machine is not a roomy one.
            float area = Mathf.Max(0f, extents.x * 2f) * Mathf.Max(0f, extents.z * 2f);
            int seats = 1 + Mathf.FloorToInt(area / 3f);
            return Mathf.Clamp(seats, 2, MaxSeats);
        }

        /// <summary>
        /// Is this big enough that a person could plausibly sit in it?
        ///
        /// "It moves" is not the same as "you can ride it", and the first build
        /// of this conflated them. The very first real run offered seats in a
        /// bus's card-reader arm (it moves because the bus does) and in the
        /// player's own CONTROLLERS object (it moves because the player does).
        /// Both were two-seaters as far as the capacity maths was concerned,
        /// because a thing with almost no footprint clamps to the minimum.
        ///
        /// A floor check fixes that without another list of names: a golf cart
        /// is about 1.2 x 2.4 m, and nothing you can sit in is smaller than a
        /// doormat.
        /// </summary>
        public static bool FitsAPerson(Vector3 extents)
        {
            float footprint = Mathf.Max(0f, extents.x * 2f) * Mathf.Max(0f, extents.z * 2f);
            return footprint >= 1.5f && extents.y * 2f >= 0.8f;
        }

        /// <summary>Is this SteamID sitting anywhere in here?</summary>
        public static bool Contains(IList<ulong> seats, ulong who)
        {
            if (seats == null || who == 0) return false;
            for (int i = 0; i < seats.Count; i++) if (seats[i] == who) return true;
            return false;
        }

        public static int SeatOf(IList<ulong> seats, ulong who)
        {
            if (seats == null || who == 0) return -1;
            for (int i = 0; i < seats.Count; i++) if (seats[i] == who) return i;
            return -1;
        }

        public static int Used(IList<ulong> seats)
        {
            if (seats == null) return 0;
            int n = 0;
            for (int i = 0; i < seats.Count; i++) if (seats[i] != 0) n++;
            return n;
        }

        /// <summary>
        /// Give this player a seat. Returns the seat index, or -1 if it's full.
        ///
        /// Already aboard returns the seat they already have rather than a
        /// second one — a duplicate boarding request is the normal consequence
        /// of a dropped grant, not an error, and it must be idempotent.
        ///
        /// The list is resized to <paramref name="capacity"/> here rather than
        /// by the caller, so a machine whose bounds were not known at first
        /// contact still ends up with the right number of seats.
        /// </summary>
        public static int Assign(IList<ulong> seats, ulong who, int capacity)
        {
            if (seats == null || who == 0) return -1;

            int existing = SeatOf(seats, who);
            if (existing >= 0) return existing;

            capacity = Mathf.Clamp(capacity, 1, MaxSeats);
            while (seats.Count < capacity) seats.Add(0UL);

            // Passengers first: seat 0 belongs to whoever owns the machine, and
            // handing it to a passenger would make two people the driver.
            for (int i = 1; i < seats.Count && i < capacity; i++)
                if (seats[i] == 0) { seats[i] = who; return i; }

            return -1;
        }

        /// <summary>Put the owner in the driver's seat, displacing whatever was there.</summary>
        public static void SetDriver(IList<ulong> seats, ulong who)
        {
            if (seats == null) return;
            while (seats.Count < 1) seats.Add(0UL);

            // If they were riding as a passenger and have now taken the wheel,
            // don't leave a ghost of them in the back.
            for (int i = 1; i < seats.Count; i++) if (seats[i] == who) seats[i] = 0;
            seats[DriverSeat] = who;
        }

        /// <summary>Take this player out. Returns the seat freed, or -1.</summary>
        public static int Release(IList<ulong> seats, ulong who)
        {
            int s = SeatOf(seats, who);
            if (s >= 0) seats[s] = 0;
            return s;
        }

        /// <summary>
        /// Everyone gets off. Returns who was aboard, so each of them can be put
        /// back on their feet — a passenger left parented to a vehicle that has
        /// stopped being replicated is stuck inside the scenery, which is the
        /// single worst outcome this feature can produce.
        /// </summary>
        public static void Evacuate(IList<ulong> seats, List<ulong> who)
        {
            if (who != null) who.Clear();
            if (seats == null) return;
            for (int i = 0; i < seats.Count; i++)
            {
                if (seats[i] == 0) continue;
                if (who != null) who.Add(seats[i]);
                seats[i] = 0;
            }
        }

        // ------------------------------------------------------- the host's call

        public enum Ruling
        {
            Granted,
            GotOut,
            /// <summary>Asked to get out of something they were never in.</summary>
            NotAboard,
            /// <summary>It doesn't go anywhere — there is nothing to ride.</summary>
            NotAMover,
            /// <summary>Nobody is driving. You don't sit in a parked car waiting.</summary>
            NoDriver,
            /// <summary>They're the one driving it.</summary>
            AlreadyDriving,
            Full,
        }

        public struct Decision
        {
            public Ruling Ruling;
            /// <summary>Seat given, or freed, or -1.</summary>
            public int Seat;
            /// <summary>Did the seating actually change? Only then is a broadcast worth sending.</summary>
            public bool Changed;

            public bool Ok { get { return Ruling == Ruling.Granted || Ruling == Ruling.GotOut; } }

            public override string ToString()
            {
                return Ruling + (Seat >= 0 ? " (seat " + Seat + ")" : "") + (Changed ? " changed" : "");
            }
        }

        /// <summary>
        /// The whole of the host's seating rule, with no Unity and no network in
        /// it — which is the only reason every ordering of it can be swept in a
        /// millisecond instead of hoped about.
        ///
        /// <paramref name="seats"/> is modified in place when the answer is yes.
        /// </summary>
        public static Decision Decide(IList<ulong> seats, ulong who, ulong owner,
                                      bool isMover, int capacity, bool leaving)
        {
            var d = new Decision { Seat = -1 };
            if (seats == null || who == 0) { d.Ruling = Ruling.NotAboard; return d; }

            if (leaving)
            {
                int freed = Release(seats, who);
                d.Seat = freed;
                d.Changed = freed >= 0;
                d.Ruling = freed >= 0 ? Ruling.GotOut : Ruling.NotAboard;
                return d;
            }

            if (!isMover) { d.Ruling = Ruling.NotAMover; return d; }
            if (owner == 0) { d.Ruling = Ruling.NoDriver; return d; }
            if (owner == who) { d.Ruling = Ruling.AlreadyDriving; return d; }

            // Keep the driver's seat honest before handing out any others: if
            // ownership changed hands while someone was aboard, seat 0 may be
            // holding a player who is no longer driving.
            int before = Used(seats);
            SetDriver(seats, owner);

            int s = Assign(seats, who, capacity);
            if (s < 0) { d.Ruling = Ruling.Full; d.Changed = Used(seats) != before; return d; }

            d.Ruling = Ruling.Granted;
            d.Seat = s;
            d.Changed = true;
            return d;
        }

        /// <summary>
        /// Where a seat sits, in the vehicle's own space. Two per row, front to
        /// back, derived from the bounds — we have no idea where the actual
        /// seats are and no way to find out that survives a patch. Being
        /// roughly right and never wrong beats being exactly right until the
        /// next update.
        /// </summary>
        /// <summary>
        /// Where a passenger sits, given where the DRIVER actually is.
        ///
        /// Much better than guessing from bounds, and it costs nothing: the
        /// driver's position is not a guess at all — the game seats them itself
        /// and we read it back. Mirror it across the vehicle's centreline for
        /// the seat beside them, and step backwards for the rows behind. A
        /// vehicle's seats are laid out either side of its driver, whatever
        /// shape it is.
        /// </summary>
        public static Vector3 OffsetFrom(Vector3 driverLocal, int seat, Vector3 extents)
        {
            if (seat <= DriverSeat) return driverLocal;

            int row = seat / 2;
            int side = seat % 2;

            // Beside the driver is the driver's position mirrored on X. Rows
            // behind step back by a share of the vehicle's length.
            float rowGap = Mathf.Clamp(extents.z * 0.5f, 0.35f, 1.4f);

            return new Vector3(
                side == 0 ? driverLocal.x : -driverLocal.x,
                driverLocal.y,
                driverLocal.z - row * rowGap);
        }

        /// <summary>
        /// How far outside the vehicle to stand someone down, beyond its own
        /// half-width. Roughly a body's width, so the character controller is
        /// not left intersecting the bodywork it was just sitting inside.
        /// </summary>
        public const float ExitClearance = 1.1f;

        /// <summary>
        /// Where to put someone when they get out, in the vehicle's own space.
        ///
        /// Letting go of a passenger where they were sitting leaves them inside
        /// the cart's colliders. A kinematic body does not push anything out of
        /// itself, so the player simply cannot walk: stuck while the cart is
        /// parked around them, free the moment it drives off, stuck again when
        /// it stops. Every report of that was this, and it kept being blamed on
        /// whatever else had changed that day.
        ///
        /// So step out sideways, clear of the widest part, on the side they
        /// were sitting — passengers do not climb over the driver — and level
        /// with the vehicle's base rather than its seats.
        /// </summary>
        public static Vector3 ExitOffset(int seat, Vector3 extents)
        {
            if (seat < 0) seat = 0;

            float side = Mathf.Abs(extents.x) + ExitClearance;
            bool left = (seat % 2) == 0;          // matches which side Offset seats them

            return new Vector3(left ? -side : side, 0f, 0f);
        }

        public static Vector3 Offset(int seat, Vector3 extents)
        {
            if (seat < 0) seat = 0;

            float halfWidth = Mathf.Clamp(extents.x * 0.45f, 0.25f, 0.8f);
            float frontZ = Mathf.Clamp(extents.z * 0.45f, 0.2f, 2.0f);
            float depth = Mathf.Clamp(extents.z * 0.9f, 0.4f, 6.0f);

            int row = seat / 2;
            int side = seat % 2;

            int rows = Mathf.Max(1, (MaxSeats + 1) / 2);
            float rowGap = rows > 1 ? depth / (rows - 1) : 0f;

            return new Vector3(
                side == 0 ? -halfWidth : halfWidth,
                // Sat on it, not standing in the footwell. Cosmetic either way:
                // the body is a clone with no collider.
                Mathf.Clamp(extents.y * 0.2f, 0.1f, 0.9f),
                frontZ - row * rowGap);
        }
    }
}
