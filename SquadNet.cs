using System;
using System.Collections.Concurrent;
using System.Threading;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using SNetwork;

namespace HawChow.GTFOCombatPanel;

// The host speaks to a client over the game's own byte channel, because the game's custom-data entry
// point cannot carry a type it has never seen: `SNet.SetupCustomData<A>` asks IL2CPP for A's pointer
// store, and a mod-defined struct has none, which is the TypeInitializationException that stopped the
// first attempt. One level lower the game takes an arbitrary run of bytes:
//
//   send    `SNet.Core.SendBytes(bytes, quality, channel, player)` puts that run on the wire.
//   receive every packet passes `SNet_Replication.RecieveBytes`, which this mod takes in a Harmony
//           prefix. A packet of ours is recognised by an unused key plus a magic string, and returning
//           false from the prefix keeps the game from ever looking for a replicator that does not exist.
//
// The bytes are assembled and taken apart by `SquadPackets`, which has no Unity, SNet or Harmony
// reference and is therefore asserted headlessly, including every length and version rule.
//
// The receive callback is queued, not run in place. The replication layer's thread is not this mod's to
// assume: a decoded packet is parked in a bounded queue (256) and drained from `CombatSync.Tick`, which
// the main thread calls. Nothing in the queue path touches a Unity or game object - it holds only the
// snapshot struct - so whichever thread the packet arrives on, the handlers that read game state always
// run on the main thread. A full queue drops its oldest entry rather than growing.
internal static class SquadNet
{
    // Handed the decoded packet. Set by CombatSync.
    internal static Action<ulong, uint, ulong> OnHello;
    internal static Action<ulong, uint, SyncSnapshot> OnSnapshot;
    internal static Action<ulong, uint> OnProtocolMismatch;

    internal const int MaxQueue = 256;
    private struct Queued { public SquadPacketKind Kind; public ulong Sender; public ulong HelloOwner; public uint Generation; public uint Version; public SyncSnapshot Snapshot; }
    private static readonly ConcurrentQueue<Queued> Queue = new ConcurrentQueue<Queued>();
    private static int _queued;
    private static bool _loggedOverflow, _loggedReadError;

    internal static void SendHello(SNet_Player target, ulong owner, uint generation)
    {
        if (target == null) return;
        Send(SquadPackets.Hello(owner, generation, SyncProtocol.Version), target);
    }

    internal static void SendSnapshot(SNet_Player target, in SyncSnapshot snapshot, uint generation)
    {
        if (target == null) return;
        Send(SquadPackets.Snapshot(snapshot, generation, SyncProtocol.Version), target);
    }

    private static void Send(byte[] packet, SNet_Player target)
    {
        var channelType = SNet_ChannelType.GameOrderCritical;      // ordered: a snapshot must not overtake the one before it
        SNet.GetSendSettings(ref channelType, out SNet_SendGroup _, out SNet_SendQuality quality, out int channel);
        Il2CppStructArray<byte> bytes = packet;
        SNet.Core.SendBytes(bytes, quality, channel, target);
    }

    // Called from the receive prefix, on whatever thread the replication layer uses. True means the packet
    // was ours and is consumed; false means it belongs to the game (or to another mod) and is passed on
    // untouched.
    internal static bool TryAccept(Il2CppStructArray<byte> bytes, uint size, ulong sender)
    {
        try
        {
            if (bytes == null) return false;
            byte[] buffer = bytes;
            // The declared size is what the network layer actually received; the backing array is a cache and
            // may be larger, so every bound is taken from the size and never from buffer.Length.
            int declared = size > int.MaxValue ? int.MaxValue : (int)size;
            SquadPacket packet = SquadPackets.Parse(buffer, declared);
            if (!packet.IsOurs) return false;
            // Ours, so it is consumed here whatever happens next: handing a half-read packet to the game
            // would make it look for a replicator that does not exist.
            Enqueue(packet, sender);
            return true;
        }
        catch (Exception error)
        {
            // A packet that announced itself as ours but blew up while being read is still consumed. Only a
            // reference to the exception message is kept; no game state is touched here.
            if (!_loggedReadError)
            {
                _loggedReadError = true;
                Plugin.Logger?.LogWarning("a squad packet could not be read; it was consumed rather than handed to the game: " + error.Message);
            }
            return true;
        }
    }

    private static void Enqueue(in SquadPacket packet, ulong sender)
    {
        if (packet.Kind == SquadPacketKind.Foreign) return;
        while (Interlocked.Increment(ref _queued) > MaxQueue)
        {
            if (Queue.TryDequeue(out _)) Interlocked.Decrement(ref _queued);
            else { Interlocked.Decrement(ref _queued); break; }
            if (!_loggedOverflow)
            {
                _loggedOverflow = true;
                Plugin.Logger?.LogWarning("[DIAG] the squad packet queue is full and the oldest packet was dropped; the queue is bounded so a stalled main thread cannot grow it without limit.");
            }
        }
        Queue.Enqueue(new Queued
        {
            // Transport identity is not the body identity: a host relays rows owned by other players,
            // and snapshot packets intentionally carry no sender field (Parse leaves it zero).
            Kind = packet.Kind, Sender = sender, HelloOwner = packet.Sender, Generation = packet.Generation,
            Version = packet.Version, Snapshot = packet.Snapshot
        });
    }

    // Drains the queue. Called from the main thread by CombatSync.Tick, so every handler it invokes may
    // touch game state. Each packet is guarded on its own: one bad row cannot stop the rest.
    internal static void Drain()
    {
        int budget = MaxQueue;
        while (budget-- > 0 && Queue.TryDequeue(out Queued queued))
        {
            Interlocked.Decrement(ref _queued);
            try
            {
                switch (queued.Kind)
                {
                    case SquadPacketKind.Hello:
                        OnHello?.Invoke(queued.Sender, queued.Generation, queued.HelloOwner);
                        break;
                    case SquadPacketKind.Snapshot:
                        OnSnapshot?.Invoke(queued.Sender, queued.Generation, queued.Snapshot);
                        break;
                    case SquadPacketKind.VersionMismatch:
                        OnProtocolMismatch?.Invoke(queued.Sender, queued.Version);
                        break;
                }
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("a queued squad packet could not be applied and was skipped: " + error.Message);
            }
        }
    }

    internal static int QueuedCount { get { return _queued; } }
}

// Every packet the network layer delivers passes here. Ours are consumed before the game can try to
// match them to a replicator; everything else is handed straight back with `true`.
//
// Priority.High, because this method is crowded: the client log this release was cut from shows four
// other prefixes on it (GTFO-API's network API, the AWO replicators, AddEvents, DamageSync). A prefix
// that returns false for a packet it does not recognise - or that throws before returning - takes the
// bytes out of everyone behind it, and until now this reader could be last in that queue. Reading first
// means no other mod can swallow this mod's packets before they are seen.
[HarmonyPatch(typeof(SNet_Replication), "RecieveBytes")]
internal static class SNetReplicationRecieveBytesPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    [HarmonyWrapSafe]
    private static bool Prefix(Il2CppStructArray<byte> bytes, uint size, ulong messagerID)
        => !SquadNet.TryAccept(bytes, size, messagerID);
}
