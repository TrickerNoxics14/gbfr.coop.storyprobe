using System;
using System.Collections.Generic;

namespace gbfr.coop.storyprobe;

/// <summary>
/// Packs small messages into the game's own network sync flags.
///
/// The game keeps one 64-bit flag mask per player and sends every change of
/// the local player's flags to the other players over its own online session
/// (reliable, in order). Only ids 0..0x25 exist; anything higher aborts the
/// receiver. The ids below have no use anywhere in the game code (2026-11-05
/// audit of every caller), so the game never reads or writes them.
///
/// Each flag change carries one of 16 symbols (which data id, value 0 or 1),
/// so a byte is two changes. A change of <see cref="StartId"/> starts a frame.
/// Frame: version, flags, type, length, payload, checksum.
/// </summary>
public static class RelayCodec
{
    public static readonly int[] DataIds = { 0x01, 0x02, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x12 };
    public const int StartId = 0x1B;
    public const byte Version = 1;
    public const int MaxPayload = 24;

    public const byte FlagFromLeader = 1;

    public const byte TypeHello = 1;      // payload: none, or flags u8 (bit 0: sender owns the lobby); presence, every 20 s
    public const byte TypeSkipCut = 2;    // payload: quest u32
    public const byte TypeSkipStage = 3;  // payload: quest u32
    public const byte TypeReady = 4;      // payload: chapter-select key u32
    public const byte TypeWaiting = 5;    // payload: chapter-select key u32
    public const byte TypeSignal = 6;     // payload: quest u32, signal hash low 32 bits u32
    public const byte TypeInteract = 7;   // payload: object net id u64, pressing player net id u64 (guest -> host)

    public const byte HelloOwnsLobby = 1;

    public static bool IsRelayId(int id)
    {
        if (id == StartId) return true;
        foreach (var d in DataIds) if (d == id) return true;
        return false;
    }

    public static byte Checksum(IReadOnlyList<byte> b, int count)
    {
        byte c = 0x5A;
        for (int i = 0; i < count; i++) c ^= b[i];
        return c;
    }

    /// <summary>The flag changes that carry one frame, in send order.</summary>
    public static List<(int id, bool value)> Encode(byte flags, byte type, byte[] payload)
    {
        payload ??= Array.Empty<byte>();
        if (payload.Length > MaxPayload) throw new ArgumentException("payload too long");
        var bytes = new List<byte>(payload.Length + 5) { Version, flags, type, (byte)payload.Length };
        bytes.AddRange(payload);
        bytes.Add(Checksum(bytes, bytes.Count));

        var ops = new List<(int, bool)>(1 + bytes.Count * 2) { (StartId, true) };
        foreach (var b in bytes)
        {
            ops.Add(Symbol(b >> 4));
            ops.Add(Symbol(b & 0xF));
        }
        return ops;
    }

    static (int, bool) Symbol(int nibble) => (DataIds[nibble >> 1], (nibble & 1) != 0);

    public static byte[] U32(uint v) => new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) };
    public static byte[] U32(uint a, uint b) { var r = new byte[8]; U32(a).CopyTo(r, 0); U32(b).CopyTo(r, 4); return r; }
    public static byte[] U64(ulong a, ulong b) { var r = new byte[16]; for (int i = 0; i < 8; i++) { r[i] = (byte)(a >> (8 * i)); r[8 + i] = (byte)(b >> (8 * i)); } return r; }
    public static ulong ReadU64(byte[] p, int at) => ReadU32(p, at) | (ulong)ReadU32(p, at + 4) << 32;
    public static uint ReadU32(byte[] p, int at) => (uint)(p[at] | p[at + 1] << 8 | p[at + 2] << 16 | p[at + 3] << 24);

    public sealed record Frame(byte Flags, byte Type, byte[] Payload)
    {
        public bool FromLeader => (Flags & FlagFromLeader) != 0;
    }

    /// <summary>
    /// Rebuilds frames from one sender's flag changes. Not thread-safe; keep
    /// one per sender and feed it under a lock. Garbage (a bad version, length
    /// or checksum, or data with no start) is dropped and counted.
    /// </summary>
    public sealed class Decoder
    {
        readonly List<byte> _bytes = new();
        int _high = -1;
        bool _inFrame, _skipping;   // _skipping: rest of a bad frame, already counted
        public int Errors { get; private set; }

        public Frame Feed(int id, bool value)
        {
            if (id == StartId)
            {
                if (_inFrame && _bytes.Count > 0) Errors++;   // previous frame cut short
                _bytes.Clear(); _high = -1; _inFrame = value; _skipping = false;
                return null;
            }
            var k = Array.IndexOf(DataIds, id);
            if (k < 0) return null;
            if (!_inFrame) { if (!_skipping) { Errors++; _skipping = true; } return null; }

            var nibble = k << 1 | (value ? 1 : 0);
            if (_high < 0) { _high = nibble; return null; }
            _bytes.Add((byte)(_high << 4 | nibble));
            _high = -1;

            if (_bytes.Count == 1 && _bytes[0] != Version) return Fail();
            if (_bytes.Count == 4 && _bytes[3] > MaxPayload) return Fail();
            if (_bytes.Count >= 5 && _bytes.Count == _bytes[3] + 5)
            {
                _inFrame = false; _skipping = true;
                if (_bytes[^1] != Checksum(_bytes, _bytes.Count - 1)) return Fail();
                var payload = _bytes.GetRange(4, _bytes[3]).ToArray();
                return new Frame(_bytes[1], _bytes[2], payload);
            }
            return null;
        }

        Frame Fail() { Errors++; _inFrame = false; _skipping = true; _bytes.Clear(); _high = -1; return null; }
    }
}
