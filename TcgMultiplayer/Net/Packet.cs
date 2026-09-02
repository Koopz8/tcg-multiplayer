using System;
using System.IO;
using System.Text;

namespace TcgMultiplayer.Net
{
    public enum Op : byte
    {
        Hello = 1,      // client -> everyone: who I am, what I'm running
        HelloAck = 2,   // reply, so both sides know the handshake completed
        Ping = 3,       // carries a stopwatch tick the peer echoes back
        Pong = 4,
        Chat = 5,
        Bye = 6,
        PlayerState = 7,   // channel 2, unreliable: position, yaw, pitch, velocity, flags
        MachineClaim = 8,  // client -> host: I put my card in / I'm done
        MachineOwner = 9,  // host -> all: this machine belongs to X (0 = free)
        MachineEvent = 10, // owner -> spectators: replay this FSM event
        Wallet = 11,       // periodic: my coins and tickets, for the scoreboard
        WorldVar = 12,     // shared island progression: unlock, vehicle purchase
        WorldSync = 13,    // joiner -> host: send me the island as it stands
    }

    /// <summary>
    /// Wire format: one opcode byte, then payload. Deliberately hand-rolled and
    /// tiny — every byte here is going to be sent 20 times a second later on.
    /// </summary>
    public sealed class PacketWriter : IDisposable
    {
        private readonly MemoryStream _ms;
        private readonly BinaryWriter _w;

        public PacketWriter(Op op)
        {
            _ms = new MemoryStream(64);
            _w = new BinaryWriter(_ms, Encoding.UTF8);
            _w.Write((byte)op);
        }

        public PacketWriter Str(string s)
        {
            if (s == null) s = "";
            var b = Encoding.UTF8.GetBytes(s);
            if (b.Length > ushort.MaxValue) throw new ArgumentException("string too long");
            _w.Write((ushort)b.Length);
            _w.Write(b);
            return this;
        }

        public PacketWriter I64(long v) { _w.Write(v); return this; }
        public PacketWriter U64(ulong v) { _w.Write(v); return this; }
        public PacketWriter I32(int v) { _w.Write(v); return this; }
        public PacketWriter U16(ushort v) { _w.Write(v); return this; }
        public PacketWriter U32(uint v) { _w.Write(v); return this; }
        public PacketWriter Bool(bool v) { _w.Write(v); return this; }
        public PacketWriter F32(float v) { _w.Write(v); return this; }
        public PacketWriter U8(byte v) { _w.Write(v); return this; }

        public byte[] ToArray() { _w.Flush(); return _ms.ToArray(); }
        public void Dispose() { _w.Close(); _ms.Dispose(); }
    }

    public sealed class PacketReader : IDisposable
    {
        private readonly MemoryStream _ms;
        private readonly BinaryReader _r;

        public Op Op { get; private set; }

        public PacketReader(byte[] data)
        {
            _ms = new MemoryStream(data, false);
            _r = new BinaryReader(_ms, Encoding.UTF8);
            Op = (Op)_r.ReadByte();
        }

        public string Str()
        {
            int n = _r.ReadUInt16();
            return Encoding.UTF8.GetString(_r.ReadBytes(n));
        }

        public long I64() { return _r.ReadInt64(); }
        public ulong U64() { return _r.ReadUInt64(); }
        public int I32() { return _r.ReadInt32(); }
        public ushort U16() { return _r.ReadUInt16(); }
        public uint U32() { return _r.ReadUInt32(); }
        public bool Bool() { return _r.ReadBoolean(); }
        public float F32() { return _r.ReadSingle(); }
        public byte U8() { return _r.ReadByte(); }

        public void Dispose() { _r.Close(); _ms.Dispose(); }
    }
}
