using System;
using System.Collections.Generic;
using Steamworks;
using TcgMultiplayer.Game;
using TcgMultiplayer.Net;

namespace TcgRig
{
    /// <summary>
    /// A node is one player: a real Session, a fake Steam behind it, and a
    /// notebook of everything its callbacks fired.
    /// </summary>
    public sealed class Node
    {
        public ulong Id;
        public CSteamID Sid;
        public string Name;
        public Session S;
        public LoopbackLobby Lobby;

        public int SessionsBegun, SessionsEnded;
        public bool LastBeganAsHost;
        public readonly List<ulong> PeersGone = new List<ulong>();
        public readonly List<string> WorldVars = new List<string>();
        public readonly List<ulong> SnapshotRequests = new List<ulong>();
        public readonly List<PlayerState> States = new List<PlayerState>();
        public readonly List<string> OwnerRulings = new List<string>();
        public readonly List<string> Claims = new List<string>();
        public readonly List<uint> PoseIds = new List<uint>();
        public readonly List<Session.ObjectPose> Poses = new List<Session.ObjectPose>();
        public readonly List<string> SeatRequests = new List<string>();
        public readonly List<string> SeatGrants = new List<string>();

        public Peer PeerFor(ulong id)
        {
            foreach (var p in S.Peers) if (p.Id.m_SteamID == id) return p;
            return null;
        }

        public bool Handshaked(ulong id)
        {
            var p = PeerFor(id);
            return p != null && p.Handshaked;
        }
    }

    public sealed class Rig
    {
        public readonly LoopbackWorld World;
        public readonly List<Node> Nodes = new List<Node>();

        /// <summary>Set by --seed, so the lossy/jittery cases can be swept.</summary>
        public static int DefaultSeed = 12345;

        public Rig(int seed = 0)
        {
            World = new LoopbackWorld(seed == 0 ? DefaultSeed : seed);
            TcgMultiplayer.Plugin.Reset();
        }

        public Node Add(uint account, string name)
        {
            var sid = Ids.User(account);
            ulong id = sid.m_SteamID;
            var lobby = World.AddPeer(sid, name);
            var n = new Node { Id = id, Sid = sid, Name = name, Lobby = lobby };
            n.S = new Session(new LoopbackTransport(World, id), lobby);
            n.S.ClockOverride = () => World.Now;

            n.S.OnSessionBegan += host => { n.SessionsBegun++; n.LastBeganAsHost = host; };
            n.S.OnSessionEnded += () => { n.SessionsEnded++; };
            n.S.OnPeerGone += who => n.PeersGone.Add(who.m_SteamID);
            n.S.OnWorldVar += (who, name2, val, asHost) =>
                n.WorldVars.Add(name2 + "=" + val + (asHost ? " (host)" : ""));
            n.S.OnWorldSnapshotRequest += who => n.SnapshotRequests.Add(who.m_SteamID);
            n.S.OnPlayerState += (who, st, seq) => n.States.Add(st);
            n.S.OnMachineOwner += (mid, owner, oname) => n.OwnerRulings.Add(mid + ":" + owner);
            n.S.OnMachineClaim += (who, mid, release) =>
                n.Claims.Add(who.m_SteamID + ":" + mid + (release ? ":release" : ":claim"));
            n.S.OnObjectState += (who, mid, pose) => { n.PoseIds.Add(mid); n.Poses.Add(pose); };
            n.S.OnSeatRequest += (who, mid, leave) =>
                n.SeatRequests.Add(who.m_SteamID + ":" + mid + (leave ? ":out" : ":in"));
            n.S.OnSeatGrant += (mid, seats) => n.SeatGrants.Add(mid + ":" + string.Join(",", Array.ConvertAll(seats, x => x.ToString())));

            if (!n.S.Init()) throw new Exception("Init failed for " + name);
            Nodes.Add(n);
            return n;
        }

        /// <summary>Advance the world and tick every session, in small steps.</summary>
        public void Pump(double seconds, double step = 0.05)
        {
            double left = seconds;
            while (left > 0)
            {
                double dt = Math.Min(step, left);
                World.Advance(dt);
                foreach (var n in Nodes) n.S.Tick();
                left -= dt;
            }
        }

        /// <summary>
        /// A frozen main thread: wall-clock time passes but no ticks happen.
        /// This is what a scene load or a synchronous save backup looks like
        /// from the session's point of view.
        /// </summary>
        public void Stall(double seconds)
        {
            World.Advance(seconds);
            foreach (var n in Nodes) n.S.Tick();
        }

        /// <summary>Host on the first node, join everyone else to it, settle.</summary>
        public Node HostAndJoinAll(int maxPlayers = 4)
        {
            var host = Nodes[0];
            host.S.Host(maxPlayers);
            Pump(0.5);

            for (int i = 1; i < Nodes.Count; i++)
            {
                Nodes[i].S.Join(host.S.Lobby);
                Pump(0.5);
            }
            Pump(1.0);
            return host;
        }
    }

    // ---------------------------------------------------------------- results

    public sealed class Check
    {
        public string Name;
        public bool Passed;
        public string Detail;
        public string Error;
    }

    public static class Assert
    {
        public sealed class Failed : Exception
        {
            public Failed(string m) : base(m) { }
        }

        public static void True(bool cond, string what)
        {
            if (!cond) throw new Failed(what);
        }

        public static void Eq(object a, object b, string what)
        {
            if (!Equals(a, b)) throw new Failed(what + " (expected " + b + ", got " + a + ")");
        }

        public static void Near(float a, float b, float tol, string what)
        {
            if (Math.Abs(a - b) > tol) throw new Failed(what + " (expected ~" + b + ", got " + a + ")");
        }
    }
}
