using System;
using System.Text;

namespace HawChow.GTFOCombatPanel;

public enum SquadPacketKind
{
    // Not ours at all: hand the bytes straight back to the game.
    Foreign = 0,
    // Ours by key and magic, but unusable: the length, the version or the body is wrong. It is still
    // consumed, because letting a half-read packet fall through to the game's replication layer is what
    // makes the game look for a replicator that does not exist.
    Malformed,
    VersionMismatch,
    Hello,
    Snapshot,
    Unknown
}

// One decoded squad packet.
public readonly struct SquadPacket
{
    public readonly SquadPacketKind Kind;
    public readonly ulong Sender;
    public readonly uint Generation;
    public readonly uint Version;
    public readonly SyncSnapshot Snapshot;

    internal SquadPacket(SquadPacketKind kind, ulong sender, uint generation, uint version, in SyncSnapshot snapshot)
    { Kind = kind; Sender = sender; Generation = generation; Version = version; Snapshot = snapshot; }

    public bool IsOurs { get { return Kind != SquadPacketKind.Foreign; } }
}

// The wire format of the squad channel, kept free of Unity, SNet and Harmony so every length and version
// rule is asserted headlessly. The one thing this used to get wrong is the length: the receive prefix is
// handed the packet's declared size as well as the backing array, and the array the game caches is at
// least as large as the packet - often larger. Every bound below is therefore taken from the declared
// length, and the array is only checked to be at least that long.
//
//   key(2) magic(4) kind(2) protocol(4) generation(4) | body
//
// The generation is the host's run counter: it is bumped when a new expedition starts, so a packet that
// was still in flight from the previous run cannot land in the new run's counters.
public static class SquadPackets
{
    // Above the range the replication layer assigns to itself, so no vanilla replicator answers to it.
    public const ushort Key = 0xFFFE;
    public const int HeaderBytes = 16;
    public const int HelloBodyBytes = 8;                       // the sender's own lookup
    public const int MaxPacketBytes = 4096;                   // a sanity bound, not a protocol limit
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("CMPL");

    public static int HelloBytes { get { return HeaderBytes + HelloBodyBytes; } }
    public static int SnapshotBytes { get { return HeaderBytes + SyncProtocol.SnapshotSize; } }

    public static byte[] Hello(ulong sender, uint generation, uint version)
    {
        var packet = new byte[HelloBytes];
        WriteHeader(packet, SyncProtocol.KindHello, generation, version);
        WriteU64(packet, HeaderBytes, sender);
        return packet;
    }

    public static byte[] Snapshot(in SyncSnapshot snapshot, uint generation, uint version)
    {
        byte[] body = SyncProtocol.Encode(snapshot);
        var packet = new byte[HeaderBytes + body.Length];
        WriteHeader(packet, SyncProtocol.KindSnapshot, generation, version);
        Buffer.BlockCopy(body, 0, packet, HeaderBytes, body.Length);
        return packet;
    }

    // The whole decision, from raw bytes to a decoded packet. `length` is what the network layer declared;
    // `buffer` may be longer. Nothing past `length` is ever read.
    public static SquadPacket Parse(byte[] buffer, int length)
    {
        if (buffer == null) return new SquadPacket(SquadPacketKind.Foreign, 0, 0, 0, default);
        if (length < 6 || length > MaxPacketBytes || buffer.Length < length) return new SquadPacket(SquadPacketKind.Foreign, 0, 0, 0, default);
        // Own key and magic first: a packet that is not ours is never touched, so another mod's channel
        // and the game's own traffic pass through exactly as before.
        if (ReadU16(buffer, 0) != Key) return new SquadPacket(SquadPacketKind.Foreign, 0, 0, 0, default);
        for (int index = 0; index < Magic.Length; index++)
            if (buffer[2 + index] != Magic[index]) return new SquadPacket(SquadPacketKind.Foreign, 0, 0, 0, default);
        if (length < HeaderBytes) return new SquadPacket(SquadPacketKind.Malformed, 0, 0, 0, default);

        ushort kind = ReadU16(buffer, 6);
        uint protocol = ReadU32(buffer, 8);
        uint generation = ReadU32(buffer, 12);
        if (protocol != SyncProtocol.Version) return new SquadPacket(SquadPacketKind.VersionMismatch, 0, generation, protocol, default);
        switch (kind)
        {
            case SyncProtocol.KindHello:
                // Exactly one body, not at least one: the envelope states how many bytes it carries, and a hello
                // that carries anything else is a build that disagrees about the format. It is consumed either
                // way (Kind != Foreign), so a malformed packet of ours never falls through to the game.
                if (length != HelloBytes) return new SquadPacket(SquadPacketKind.Malformed, 0, generation, protocol, default);
                return new SquadPacket(SquadPacketKind.Hello, ReadU64(buffer, HeaderBytes), generation, protocol, default);
            case SyncProtocol.KindSnapshot:
                // The length the packet declares is what the network layer actually received; the backing array
                // may be larger, so the body is that declared remainder and nothing else. A body of any other
                // size is refused rather than decoded through the first N bytes of whatever it holds.
                if (length - HeaderBytes != SyncProtocol.SnapshotSize)
                    return new SquadPacket(SquadPacketKind.Malformed, 0, generation, protocol, default);
                return SyncProtocol.Decode(buffer, HeaderBytes, length - HeaderBytes, out SyncSnapshot snapshot)
                    ? new SquadPacket(SquadPacketKind.Snapshot, 0, generation, protocol, snapshot)
                    : new SquadPacket(SquadPacketKind.Malformed, 0, generation, protocol, default);
            default:
                return new SquadPacket(SquadPacketKind.Unknown, 0, generation, protocol, default);
        }
    }

    private static void WriteHeader(byte[] packet, ushort kind, uint generation, uint version)
    {
        WriteU16(packet, 0, Key);
        for (int index = 0; index < Magic.Length; index++) packet[2 + index] = Magic[index];
        WriteU16(packet, 6, kind);
        WriteU32(packet, 8, version);
        WriteU32(packet, 12, generation);
    }

    private static void WriteU16(byte[] buffer, int at, ushort value)
    {
        buffer[at] = (byte)value; buffer[at + 1] = (byte)(value >> 8);
    }

    private static void WriteU32(byte[] buffer, int at, uint value)
    {
        for (int index = 0; index < 4; index++) buffer[at + index] = (byte)(value >> (8 * index));
    }

    private static void WriteU64(byte[] buffer, int at, ulong value)
    {
        for (int index = 0; index < 8; index++) buffer[at + index] = (byte)(value >> (8 * index));
    }

    private static ushort ReadU16(byte[] buffer, int at) => (ushort)(buffer[at] | buffer[at + 1] << 8);

    private static uint ReadU32(byte[] buffer, int at)
    {
        uint value = 0;
        for (int index = 0; index < 4; index++) value |= (uint)buffer[at + index] << (8 * index);
        return value;
    }

    private static ulong ReadU64(byte[] buffer, int at)
    {
        ulong value = 0;
        for (int index = 0; index < 8; index++) value |= (ulong)buffer[at + index] << (8 * index);
        return value;
    }
}
