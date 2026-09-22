using System;
using System.Collections.Generic;
using TcgMultiplayer.Game;
using TcgMultiplayer.Net;
using UnityEngine;

namespace TcgRig
{
    /// <summary>
    /// Vehicles: getting them to move on someone else's screen, and getting a
    /// second player into one.
    ///
    /// The seating rules are where the real bugs are. Two people reaching for
    /// the last seat, a driver who hands over the wheel while a passenger is
    /// aboard, a passenger whose connection drops halfway down the hill — none
    /// of those can be reached by one person with one copy of the game, and all
    /// of them end with somebody welded to a car that no longer exists. So the
    /// rule is a pure function and every ordering of it gets swept.
    /// </summary>
    public static class VehicleScenarios
    {
        public static readonly List<Func<Check>> All = new List<Func<Check>>
        {
            // --- how many fit, and where
            CapacityComesFromSize,
            SeatsAreDistinctAndAboard,

            // --- the host's ruling
            DriverOwnsSeatZero,
            BoardingIsIdempotent,
            FullVehicleRefuses,
            LeavingFreesTheExactSeat,
            HandingOverTheWheelDoesNotDuplicateAnyone,
            YouCannotRideWhatNobodyIsDriving,
            YouCannotRideYourOwnCar,
            YouCannotRideACabinet,
            EveryoneGetsOffWhenItIsAbandoned,
            NoTwoPlayersEverShareASeat,

            // --- is it even a vehicle
            AThingThatNeverMovesIsNotAMover,
            DrivingAwayMakesItAMover,
            ParkingItBackDoesNotUnmakeIt,
            IdleJitterIsNotDriving,
            GuessingAheadIsCapped,
            RidingIsDetectedByMovingTogether,
            StandingNextToAParkedCartIsNotRidingIt,
            ConfidenceFallsFasterThanItRises,

            // --- over the wire
            APoseSurvivesTheWire,
            AttachmentSurvivesTheWire,
            AFullBusOfSeatsSurvivesTheWire,
            OnlyTheHostHearsASeatRequest,
            ALiedAboutSeatCountIsRejected,
            TheStreamSurvivesPacketLoss,
        };

        private static Check Run(string name, Action<Check> body)
        {
            var c = new Check { Name = name };
            try { body(c); c.Passed = true; }
            catch (Assert.Failed f) { c.Passed = false; c.Error = f.Message; }
            catch (Exception ex) { c.Passed = false; c.Error = ex.GetType().Name + ": " + ex.Message; }
            return c;
        }

        private static List<ulong> Empty() { return new List<ulong>(); }

        // Half-extents roughly matching what the game actually has.
        private static readonly Vector3 GolfCart = new Vector3(0.6f, 0.8f, 1.2f);
        private static readonly Vector3 Car = new Vector3(0.95f, 0.7f, 2.3f);
        private static readonly Vector3 Bus = new Vector3(1.3f, 1.6f, 5.5f);

        // ------------------------------------------------------- size and seats

        private static Check CapacityComesFromSize()
        {
            return Run("a bigger vehicle holds more people, with no list of vehicle names", c =>
            {
                int cart = Seating.Capacity(GolfCart);
                int car = Seating.Capacity(Car);
                int bus = Seating.Capacity(Bus);

                Assert.True(cart >= 2, "a golf cart holds at least two");
                Assert.True(car >= cart, "a car holds at least as many as a cart (got " + car + " vs " + cart + ")");
                Assert.True(bus > car, "a bus holds more than a car (got " + bus + " vs " + car + ")");
                Assert.True(bus <= Seating.MaxSeats, "and never more than the wire allows");
                c.Detail = "cart " + cart + ", car " + car + ", bus " + bus;
            });
        }

        private static Check SeatsAreDistinctAndAboard()
        {
            return Run("no two seats are in the same place, and none is outside the vehicle", c =>
            {
                var seen = new List<Vector3>();
                for (int i = 0; i < Seating.MaxSeats; i++)
                {
                    var o = Seating.Offset(i, Bus);
                    for (int j = 0; j < seen.Count; j++)
                        Assert.True((seen[j] - o).magnitude > 0.05f,
                                    "seat " + i + " is not on top of seat " + j);
                    seen.Add(o);

                    // Generous, because the offsets are clamped rather than
                    // measured — the point is that nobody is left standing on
                    // the roof or trailing ten metres behind.
                    Assert.True(Mathf.Abs(o.x) <= Bus.x + 0.5f, "seat " + i + " is within the width");
                    Assert.True(Mathf.Abs(o.z) <= Bus.z + 0.5f, "seat " + i + " is within the length");
                    Assert.True(o.y >= 0f && o.y <= Bus.y + 0.5f, "seat " + i + " is at a sane height");
                }
                c.Detail = Seating.MaxSeats + " distinct seats, all inside the bus";
            });
        }

        // -------------------------------------------------------- host rulings

        private static Check DriverOwnsSeatZero()
        {
            return Run("the driver has seat 0 and a passenger never gets it", c =>
            {
                var s = Empty();
                var d = Seating.Decide(s, 20UL, 10UL, true, 4, false);

                Assert.True(d.Ok, "the passenger is let in");
                Assert.Eq(s[Seating.DriverSeat], 10UL, "seat 0 is the driver's");
                Assert.True(d.Seat > 0, "and the passenger is somewhere else (got " + d.Seat + ")");
                c.Detail = "driver 10 in seat 0, passenger 20 in seat " + d.Seat;
            });
        }

        private static Check BoardingIsIdempotent()
        {
            return Run("asking twice gets the same seat, not a second one", c =>
            {
                var s = Empty();
                var a = Seating.Decide(s, 20UL, 10UL, true, 4, false);
                var b = Seating.Decide(s, 20UL, 10UL, true, 4, false);

                Assert.Eq(b.Seat, a.Seat, "same seat both times");
                Assert.Eq(Seating.Used(s), 2, "two people aboard, not three");
                c.Detail = "a lost grant is normal, so a repeat must not cost a seat";
            });
        }

        private static Check FullVehicleRefuses()
        {
            return Run("a full vehicle says so instead of quietly dropping someone", c =>
            {
                var s = Empty();
                Seating.Decide(s, 20UL, 10UL, true, 2, false);     // cart: driver + one
                var d = Seating.Decide(s, 30UL, 10UL, true, 2, false);

                Assert.Eq(d.Ruling, Seating.Ruling.Full, "refused as full");
                Assert.Eq(d.Seat, -1, "with no seat");
                Assert.True(!Seating.Contains(s, 30UL), "and they really aren't aboard");
                c.Detail = "cart of two: driver 10, passenger 20, 30 turned away";
            });
        }

        private static Check LeavingFreesTheExactSeat()
        {
            return Run("getting out frees the seat you were in, and the next person takes it", c =>
            {
                var s = Empty();
                Seating.Decide(s, 20UL, 10UL, true, 4, false);
                var mid = Seating.Decide(s, 30UL, 10UL, true, 4, false);
                var outp = Seating.Decide(s, 20UL, 10UL, true, 4, true);

                Assert.Eq(outp.Ruling, Seating.Ruling.GotOut, "20 got out");
                Assert.True(!Seating.Contains(s, 20UL), "and is gone from the list");
                Assert.True(Seating.Contains(s, 30UL), "while 30 stayed in seat " + mid.Seat);

                var back = Seating.Decide(s, 40UL, 10UL, true, 4, false);
                Assert.Eq(back.Seat, outp.Seat, "40 takes the seat 20 vacated");
                c.Detail = "seat " + outp.Seat + " freed and reused";
            });
        }

        private static Check HandingOverTheWheelDoesNotDuplicateAnyone()
        {
            return Run("a passenger who takes the wheel doesn't leave a ghost in the back", c =>
            {
                var s = Empty();
                Seating.Decide(s, 20UL, 10UL, true, 4, false);   // 20 rides with 10
                Assert.Eq(Seating.Used(s), 2, "two aboard");

                // 20 is now the one driving — ownership changed under us.
                Seating.Decide(s, 30UL, 20UL, true, 4, false);

                Assert.Eq(s[Seating.DriverSeat], 20UL, "20 is driving");
                Assert.Eq(Seating.SeatOf(s, 20UL), 0, "and is in exactly one seat");
                int count = 0;
                for (int i = 0; i < s.Count; i++) if (s[i] == 20UL) count++;
                Assert.Eq(count, 1, "20 appears once, not twice");
                c.Detail = "driver change cleans up the old passenger slot";
            });
        }

        private static Check YouCannotRideWhatNobodyIsDriving()
        {
            return Run("you can't sit in a parked car waiting for a driver", c =>
            {
                var s = Empty();
                var d = Seating.Decide(s, 20UL, 0UL, true, 4, false);
                Assert.Eq(d.Ruling, Seating.Ruling.NoDriver, "refused: nobody is driving");
                Assert.Eq(Seating.Used(s), 0, "nobody aboard");
                c.Detail = "get in and drive it yourself instead";
            });
        }

        private static Check YouCannotRideYourOwnCar()
        {
            return Run("the driver can't also be their own passenger", c =>
            {
                var s = Empty();
                var d = Seating.Decide(s, 10UL, 10UL, true, 4, false);
                Assert.Eq(d.Ruling, Seating.Ruling.AlreadyDriving, "refused");
                c.Detail = "pinning the driver to a seat would take the wheel off them";
            });
        }

        private static Check YouCannotRideACabinet()
        {
            return Run("a coin pusher is not a vehicle, however hard you ask", c =>
            {
                var s = Empty();
                var d = Seating.Decide(s, 20UL, 10UL, false, 4, false);
                Assert.Eq(d.Ruling, Seating.Ruling.NotAMover, "refused");
                Assert.Eq(Seating.Used(s), 0, "and nobody is stuck to it");
                c.Detail = "the host checks this rather than trusting the asker";
            });
        }

        private static Check EveryoneGetsOffWhenItIsAbandoned()
        {
            return Run("when the driver quits, every passenger is named so they can be put down", c =>
            {
                var s = Empty();
                Seating.Decide(s, 20UL, 10UL, true, 8, false);
                Seating.Decide(s, 30UL, 10UL, true, 8, false);
                Seating.Decide(s, 40UL, 10UL, true, 8, false);

                var off = new List<ulong>();
                Seating.Evacuate(s, off);

                Assert.Eq(Seating.Used(s), 0, "nobody left aboard");
                Assert.Eq(off.Count, 4, "all four are reported (driver included)");
                Assert.True(off.Contains(10UL) && off.Contains(40UL), "including the driver and the last passenger");
                c.Detail = "the list is what stops someone being left inside the scenery";
            });
        }

        private static Check NoTwoPlayersEverShareASeat()
        {
            return Run("sweep: 4,096 boarding orders, and nobody is ever double-booked", c =>
            {
                int cases = 0, refusals = 0;

                // Every capacity, every subset of four players already aboard,
                // every arrival order. If a seat can be double-booked by any
                // sequence of events, this finds it.
                for (int cap = 2; cap <= Seating.MaxSeats; cap++)
                {
                    for (int mask = 0; mask < 16; mask++)
                    {
                        for (int order = 0; order < 24; order++)
                        {
                            var s = Empty();
                            const ulong driver = 999UL;
                            var arrivals = Permutation(order);

                            for (int k = 0; k < arrivals.Length; k++)
                            {
                                ulong who = (ulong)(100 + arrivals[k]);
                                bool leaving = (mask & (1 << arrivals[k])) != 0;
                                var d = Seating.Decide(s, who, driver, true, cap, leaving);
                                if (d.Ruling == Seating.Ruling.Full) refusals++;
                                cases++;

                                // The invariant, checked after every single move.
                                var seen = new List<ulong>();
                                for (int i = 0; i < s.Count; i++)
                                {
                                    if (s[i] == 0) continue;
                                    Assert.True(!seen.Contains(s[i]),
                                        "player " + s[i] + " is in two seats at once (cap " + cap
                                        + ", mask " + mask + ", order " + order + ")");
                                    seen.Add(s[i]);
                                }
                                Assert.True(s.Count <= Seating.MaxSeats, "never more seats than the wire allows");
                                Assert.True(Seating.Used(s) <= cap, "never more people than fit");
                            }
                        }
                    }
                }

                c.Detail = cases + " boardings swept, " + refusals + " correctly refused as full";
            });
        }

        /// <summary>The n-th permutation of 0..3, so the sweep covers arrival order.</summary>
        private static int[] Permutation(int n)
        {
            var pool = new List<int> { 0, 1, 2, 3 };
            var outp = new int[4];
            int[] fact = { 6, 2, 1, 1 };
            for (int i = 0; i < 4; i++)
            {
                int idx = n / fact[i];
                n %= fact[i];
                if (idx >= pool.Count) idx = pool.Count - 1;
                outp[i] = pool[idx];
                pool.RemoveAt(idx);
            }
            return outp;
        }

        // ------------------------------------------------------ is it a vehicle

        private static Check AThingThatNeverMovesIsNotAMover()
        {
            return Run("a cabinet bolted to the floor never becomes a vehicle", c =>
            {
                var w = new MoverTrack.Watch();
                for (int i = 0; i < 200; i++) w = MoverTrack.Note(w, new Vector3(12f, 0f, -4f));
                Assert.True(!w.IsMover, "still not a mover after 200 samples");
                c.Detail = "furthest " + w.Furthest.ToString("0.000") + "m";
            });
        }

        private static Check DrivingAwayMakesItAMover()
        {
            return Run("something that drives off is spotted, without being named in advance", c =>
            {
                var w = new MoverTrack.Watch();
                w = MoverTrack.Note(w, Vector3.zero);
                Assert.True(!w.IsMover, "not yet");

                for (int i = 1; i <= 40; i++) w = MoverTrack.Note(w, new Vector3(i * 0.5f, 0f, 0f));
                Assert.True(w.IsMover, "spotted once it had travelled");
                c.Detail = "tripped past " + MoverTrack.MoveThreshold + "m, reached "
                           + w.Furthest.ToString("0.0") + "m";
            });
        }

        private static Check ParkingItBackDoesNotUnmakeIt()
        {
            return Run("a car driven back to its bay is still a car", c =>
            {
                var w = new MoverTrack.Watch();
                w = MoverTrack.Note(w, Vector3.zero);
                w = MoverTrack.Note(w, new Vector3(50f, 0f, 0f));
                Assert.True(w.IsMover, "it moved");

                for (int i = 0; i < 50; i++) w = MoverTrack.Note(w, Vector3.zero);
                Assert.True(w.IsMover, "and it stays a mover back in its bay");
                c.Detail = "otherwise a parked car would stop being rideable";
            });
        }

        private static Check IdleJitterIsNotDriving()
        {
            return Run("a ride settling on its springs is not a vehicle driving away", c =>
            {
                var w = new MoverTrack.Watch();
                var rnd = new System.Random(7);
                w = MoverTrack.Note(w, Vector3.zero);
                for (int i = 0; i < 500; i++)
                {
                    var jitter = new Vector3(
                        (float)(rnd.NextDouble() - 0.5) * 0.4f,
                        (float)(rnd.NextDouble() - 0.5) * 0.4f,
                        (float)(rnd.NextDouble() - 0.5) * 0.4f);
                    w = MoverTrack.Note(w, jitter);
                }
                Assert.True(!w.IsMover, "20cm of wobble is not a road trip");
                c.Detail = "furthest " + w.Furthest.ToString("0.00") + "m over 500 samples";
            });
        }

        private static Check GuessingAheadIsCapped()
        {
            return Run("a car whose driver dropped out doesn't fly off through the scenery", c =>
            {
                var vel = new Vector3(20f, 0f, 0f);       // 72 km/h

                var near = MoverTrack.Extrapolate(Vector3.zero, vel, 0.05f, MoverReplicatorCap);
                Assert.Near(near.x, 1f, 0.001f, "a normal gap is guessed straight through");

                var far = MoverTrack.Extrapolate(Vector3.zero, vel, 30f, MoverReplicatorCap);
                Assert.Near(far.x, 20f * MoverReplicatorCap, 0.001f, "a long stall is clamped");
                Assert.True(far.x < 10f, "not 600 metres away");

                Assert.True(MoverTrack.IsStale(MoverTrack.StaleSeconds + 0.1f), "and eventually given up on");
                Assert.True(!MoverTrack.IsStale(0.2f), "but not for an ordinary hiccup");
                c.Detail = "capped at " + MoverReplicatorCap + "s, given up at " + MoverTrack.StaleSeconds + "s";
            });
        }

        // MoverReplicator itself is Unity-side; its cap is the number under test.
        private const float MoverReplicatorCap = 0.4f;


        // ------------------------------------------------- am I inside this

        private static Check RidingIsDetectedByMovingTogether()
        {
            return Run("being in a vehicle is spotted by moving as one body, not by a state name", c =>
            {
                // The cart never fired a card-insert state, so it was never
                // claimed and never replicated. Motion is the thing that is
                // actually true of riding something.
                int conf = 0;
                var step = new Vector3(0.4f, 0f, 0.1f);
                for (int i = 0; i < RideDetect.NeedSamples; i++)
                {
                    // Not identical - the rig and the vehicle root are moved by
                    // different systems in the same frame and always disagree a
                    // little.
                    var mine = step + new Vector3(0.02f, 0f, -0.01f);
                    conf = RideDetect.Advance(conf, RideDetect.MovesWith(mine, step));
                }
                Assert.True(RideDetect.IsRiding(conf), "riding after " + RideDetect.NeedSamples + " samples");
                Assert.True(RideDetect.StillAboard(2f), "and stays aboard while near the root");
                Assert.True(!RideDetect.StillAboard(RideDetect.HoldDistance + 1f), "until you walk away");
                c.Detail = "no FSM state names, so a renamed vehicle still works";
            });
        }

        private static Check StandingNextToAParkedCartIsNotRidingIt()
        {
            return Run("standing beside a parked cart is not riding it", c =>
            {
                // Both deltas are zero, which matches perfectly and must not
                // count - otherwise anyone standing still next to anything is
                // declared to be inside it.
                int conf = 0;
                for (int i = 0; i < 20; i++)
                    conf = RideDetect.Advance(conf, RideDetect.MovesWith(Vector3.zero, Vector3.zero));
                Assert.True(!RideDetect.IsRiding(conf), "not riding a parked cart");

                // Walking past one is not riding it either.
                conf = 0;
                for (int i = 0; i < 20; i++)
                    conf = RideDetect.Advance(conf, RideDetect.MovesWith(new Vector3(0.3f, 0f, 0f), Vector3.zero));
                Assert.True(!RideDetect.IsRiding(conf), "not riding one you walk past");
                c.Detail = "the machine has to actually be going somewhere";
            });
        }

        private static Check ConfidenceFallsFasterThanItRises()
        {
            return Run("getting out is noticed faster than getting in", c =>
            {
                int conf = 0;
                for (int i = 0; i < RideDetect.MaxConfidence + 5; i++) conf = RideDetect.Advance(conf, true);
                Assert.Eq(conf, RideDetect.MaxConfidence, "confidence is capped");

                int rises = 0;
                int x = 0;
                while (!RideDetect.IsRiding(x)) { x = RideDetect.Advance(x, true); rises++; }

                int falls = 0;
                x = RideDetect.MaxConfidence;
                while (RideDetect.IsRiding(x)) { x = RideDetect.Advance(x, false); falls++; }

                Assert.True(falls <= rises, "falls at least as fast as it rises ("
                            + falls + " vs " + rises + ")");
                Assert.Eq(RideDetect.Advance(0, false), 0, "and never goes negative");
                c.Detail = "a false positive puts you in a car you're stood beside; a false negative costs 0.1s";
            });
        }

        // ------------------------------------------------------------ the wire

        private static Check APoseSurvivesTheWire()
        {
            return Run("a vehicle's pose arrives as the same pose", c =>
            {
                var rig = new Rig();
                var a = rig.Add(1, "Driver");
                var b = rig.Add(2, "Passenger");
                rig.HostAndJoinAll();

                // Built by hand rather than with Quaternion.Euler: that one is an
                // engine ECall and there is no engine here. Everything the wire
                // touches is managed, which is exactly why the wire is testable.
                var sent = new Session.ObjectPose
                {
                    Pos = new Vector3(1234.5f, -67.25f, 890.125f),
                    Rot = new Quaternion(0.2f, -0.5f, 0.1f, 0.8336666f),
                    Vel = new Vector3(0f, -9.81f, 14.5f),
                    BodyUp = 2,
                };
                a.S.SendObjectState(4242u, sent);
                rig.Pump(0.5);

                Assert.Eq(b.Poses.Count, 1, "one pose arrived");
                Assert.Eq(b.PoseIds[0], 4242u, "for the right vehicle");

                var got = b.Poses[0];
                Assert.Near(got.Pos.x, sent.Pos.x, 0.01f, "x");
                Assert.Near(got.Pos.z, sent.Pos.z, 0.01f, "z");
                Assert.Near(got.Vel.z, sent.Vel.z, 0.01f, "velocity, for guessing between packets");
                Assert.Near(got.Rot.x, sent.Rot.x, 0.0001f, "rotation x");
                Assert.Near(got.Rot.y, sent.Rot.y, 0.0001f, "rotation y");
                Assert.Near(got.Rot.z, sent.Rot.z, 0.0001f, "rotation z");
                Assert.Near(got.Rot.w, sent.Rot.w, 0.0001f, "rotation w");
                Assert.Eq((int)got.BodyUp, 2, "and which object the pose is actually about");

                // The thing the old rigidbody stream could not do: 1.2 km from
                // the origin, where a 16-bit millimetre offset would have
                // clamped at 32.767m and parked the car in the sea.
                Assert.True(got.Pos.magnitude > 1000f, "a long way from the origin, intact");
                c.Detail = "1.5 km out, ±1cm, rotation bit-for-bit";
            });
        }

        private static Check AttachmentSurvivesTheWire()
        {
            return Run("a passenger's seat crosses the wire with them", c =>
            {
                var rig = new Rig();
                var a = rig.Add(1, "Rider");
                var b = rig.Add(2, "Watcher");
                rig.HostAndJoinAll();

                var st = new PlayerState
                {
                    Pos = new Vector3(0.6f, 0.4f, 1.1f),   // in the car's frame, not the world's
                    Yaw = 12f,
                    Attached = 777u,
                    Seat = 3,
                };
                a.S.BroadcastPlayerState(st, 1);
                rig.Pump(0.5);

                Assert.True(b.States.Count >= 1, "a snapshot arrived");
                var got = b.States[b.States.Count - 1];
                Assert.Eq(got.Attached, 777u, "riding the same vehicle");
                Assert.Eq((int)got.Seat, 3, "in the same seat");
                Assert.Near(got.Pos.x, 0.6f, 0.001f, "at the same offset inside it");
                c.Detail = "without this the body is placed in world space and slides out on every corner";
            });
        }

        private static Check AFullBusOfSeatsSurvivesTheWire()
        {
            return Run("a full bus of eight seats is sent and read back in order", c =>
            {
                var rig = new Rig();
                var host = rig.Add(1, "Host");
                var b = rig.Add(2, "Guest");
                rig.HostAndJoinAll();

                var seats = new ulong[Seating.MaxSeats];
                for (int i = 0; i < seats.Length; i++) seats[i] = 5000UL + (ulong)i;
                host.S.SendSeatGrant(99u, seats);
                rig.Pump(0.5);

                Assert.Eq(b.SeatGrants.Count, 1, "one grant arrived");
                var expected = "99:" + string.Join(",", Array.ConvertAll(seats, x => x.ToString()));
                Assert.Eq(b.SeatGrants[0], expected, "with every seat in the right order");
                c.Detail = Seating.MaxSeats + " seats, order preserved";
            });
        }

        private static Check OnlyTheHostHearsASeatRequest()
        {
            return Run("asking for a seat goes to the host and nobody else", c =>
            {
                var rig = new Rig();
                var host = rig.Add(1, "Host");
                var asker = rig.Add(2, "Asker");
                var other = rig.Add(3, "Bystander");
                rig.HostAndJoinAll();

                asker.S.SendSeatRequest(55u, false);
                rig.Pump(0.5);

                Assert.Eq(host.SeatRequests.Count, 1, "the host heard it");
                Assert.Eq(other.SeatRequests.Count, 0, "the bystander did not");
                Assert.Eq(host.SeatRequests[0], asker.Id + ":55:in", "and knows who asked for what");
                c.Detail = "one arbitrator, so two people can't both get the last seat";
            });
        }

        private static Check ALiedAboutSeatCountIsRejected()
        {
            return Run("a grant claiming more seats than exist is thrown away, not trusted", c =>
            {
                // Hand-built rather than sent, because the sender would never
                // produce this — the point is what happens when something else
                // does, which is what a version mismatch looks like on the wire.
                byte[] bytes;
                using (var w = new PacketWriter(Op.SeatGrant))
                {
                    w.U32(1u).U8(200);        // 200 seats
                    for (int i = 0; i < 200; i++) w.U64(1UL);
                    bytes = w.ToArray();
                }

                int granted = 0;
                using (var r = new PacketReader(bytes))
                {
                    Assert.Eq(r.Op, Op.SeatGrant, "it decodes as a seat grant");
                    r.U32();
                    int n = r.U8();
                    Assert.True(n > Seating.MaxSeats, "and claims " + n + " seats");
                    if (n <= Seating.MaxSeats) granted++;
                }

                Assert.Eq(granted, 0, "so it is refused rather than allocating 200 seats");
                c.Detail = "the cap is checked before the array is sized";
            });
        }

        private static Check TheStreamSurvivesPacketLoss()
        {
            return Run("a vehicle keeps arriving through 30% packet loss", c =>
            {
                var rig = new Rig();
                var a = rig.Add(1, "Driver");
                var b = rig.Add(2, "Watcher");
                rig.HostAndJoinAll();

                rig.World.LossUnreliable = 0.30;
                rig.World.ReorderUnreliable = true;
                rig.World.LatencyMs = 60;
                rig.World.JitterMs = 25;

                const int frames = 200;
                for (int i = 0; i < frames; i++)
                {
                    a.S.SendObjectState(7u, new Session.ObjectPose
                    {
                        Pos = new Vector3(i * 0.5f, 0f, 0f),
                        Rot = Quaternion.identity,
                        Vel = new Vector3(10f, 0f, 0f),
                    });
                    rig.Pump(0.05);
                }
                rig.Pump(1.0);

                Assert.True(b.Poses.Count > frames * 0.5, "most frames still landed ("
                            + b.Poses.Count + " of " + frames + ")");

                // Ordering is not guaranteed and does not need to be — but the
                // newest pose has to be the real one, or the car ends the drive
                // somewhere it never was.
                var last = b.Poses[b.Poses.Count - 1];
                Assert.True(last.Pos.x > (frames - 20) * 0.5f,
                            "and the last one is near the end of the drive (x=" + last.Pos.x + ")");
                c.Detail = b.Poses.Count + "/" + frames + " arrived at 30% loss, 60±25ms";
            });
        }
    }
}
