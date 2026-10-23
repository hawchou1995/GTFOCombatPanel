using System;
using System.Collections.Generic;
using HawChow.GTFOCombatPanel;

// The transport's two shapes, asserted against the production receive path.
//
// GTFO-API's free-sized event cannot tell its handler how long a packet is: `NetworkAPI_Impl.HandlePacket`
// derives the payload from `packetData.Length - HeaderSize`, and `packetData` is the array the game's
// replication layer cached - the same array that is at least as long as the packet and often longer, which is
// why the raw byte channel is read against its declared `size`. The payload that arrives here can therefore
// end in stale bytes, and the packet has to be read as the length its own envelope names.
//
// Nothing below simulates native marshalling or Steam; what it pins is the rule, the figures that survive it,
// and what the route memory is allowed to learn.
internal static partial class Program
{
    private static byte[] Padded(byte[] packet, int pad)
    {
        var buffer = new byte[packet.Length + pad];
        Buffer.BlockCopy(packet, 0, buffer, 0, packet.Length);
        for (int index = packet.Length; index < buffer.Length; index++) buffer[index] = 0xAB;
        return buffer;
    }

    private static void RunSquadTransportFramingScenarios()
    {
        const ulong host = 0x10, local = 0x20, other = 0x30;
        SquadNet.OnHello = null; SquadNet.OnSnapshot = null; SquadNet.OnProtocolMismatch = null;
        SquadNet.ResetRoutes();

        // The padded payload is exactly what the previous release refused. This is the defect the assertions
        // below pin: `Parse` is right to demand one exact body, and the caller must tell it which bytes are
        // the packet.
        byte[] hello = SquadPackets.Hello(host, 9, SyncProtocol.Version);
        byte[] paddedHello = Padded(hello, 64);
        Check(SquadPackets.Parse(paddedHello, paddedHello.Length).Kind == SquadPacketKind.Malformed,
            "F: a padded GTFO-API payload is Malformed when the whole buffer is declared as the packet");
        Check(SquadPackets.FramedLength(paddedHello, paddedHello.Length) == SquadPackets.HelloBytes,
            "F: the envelope states the length of a padded hello");
        Check(SquadPackets.FramedLength(hello, hello.Length) == SquadPackets.HelloBytes,
            "F: the envelope states the length of an unpadded hello");
        Check(SquadPackets.FramedLength(new byte[] { 1, 2, 3 }, 3) == 0,
            "F: a buffer with no complete header of ours is not framed");
        byte[] foreign = Padded(hello, 8);
        foreign[0] = 0x00; foreign[1] = 0x00;
        Check(SquadPackets.FramedLength(foreign, foreign.Length) == 0, "F: another key is never framed as ours");

        ulong helloSender = 0, helloOwner = 0;
        SquadNet.OnHello = (sender, generation, owner) => { helloSender = sender; helloOwner = owner; };
        int unusableAtStart = SquadNet.UnusablePackets;
        Check(SquadNet.TryAcceptApiPayload(host, paddedHello, paddedHello.Length),
            "F: a padded hello arriving over GTFO-API is still a packet of ours");
        SquadNet.Drain();
        Check(helloSender == host && helloOwner == host,
            "F: a padded hello reaches the receive handler with its transport sender and declared owner");
        Check(SquadNet.UnusablePackets == unusableAtStart, "F: a padded hello is not counted as unusable");
        // One padded row, every figure intact: the host's numbers are what a client could never measure.
        var measured = new List<CombatPlayer> { new CombatPlayer(other) { Name = "peer", DisplaySlot = 1 } };
        measured[0].Total.AddEnemyDamage(640);
        measured[0].Total.AddAccuracy(55, 40, 10);
        measured[0].Impact.CreditScan(2);
        measured[0].Impact.RecordSupply(1, 2);
        SyncSnapshot own = SyncProtocol.Capture(measured[0], 0, 1, 2, 3, 640, true);
        byte[] rowPacket = SquadPackets.Snapshot(own, 9, SyncProtocol.Version);
        byte[] paddedRow = Padded(rowPacket, 96);
        Check(SquadPackets.Parse(paddedRow, paddedRow.Length).Kind == SquadPacketKind.Malformed,
            "F: a padded row is Malformed when the whole buffer is declared as the packet");
        Check(SquadPackets.FramedLength(paddedRow, paddedRow.Length) == SquadPackets.SnapshotBytes,
            "F: the envelope states the length of a padded row");

        // The client side: a host and one client, the host's row about the client's squadmate arriving over
        // GTFO-API with a pad on it. This is the path the in-level panel depends on.
        var exchange = new SquadExchange();
        exchange.Configure(local, 0, 1, 2, 3);
        exchange.SetRoster(new[]
        {
            new SquadPeer(host, 0, false, false, true),
            new SquadPeer(local, 1, true, false, false),
            new SquadPeer(other, 2, false, false, false)
        }, false);
        SyncSnapshot received = default;
        bool rowAccepted = false;
        float now = 5f;
        SquadNet.OnSnapshot = (sender, generation, snapshot) =>
        {
            received = snapshot;
            rowAccepted = exchange.OnSnapshot(sender, generation, snapshot, now).Accepted;
        };
        Check(SquadNet.TryAcceptApiPayload(host, paddedRow, paddedRow.Length),
            "F: a padded row arriving over GTFO-API is still a packet of ours");
        SquadNet.Drain();
        Check(rowAccepted, "F: a padded row from the host is accepted by the exchange");
        Check(received.Owner == other && Math.Abs(received.EnemyDamage - 640) < 0.5 && received.Shots == 55
            && received.Hits == 40 && received.ScanUses == 1 && received.ScanMarks == 2 && received.SupplyUses == 1,
            "F: a padded row carries its owner, damage, aim, scans and supply uses through the API path");
        var projected = exchange.Project(new List<CombatPlayer>(), local);
        Check(projected.Count == 1 && Near(Row(projected, other).Total.EnemyDamage, 640),
            "F: the padded row reaches the display projection with its damage");
        Check(Row(projected, other).Impact.ScanUses == 1 && Row(projected, other).Impact.ScanMarks == 2
            && Row(projected, other).Impact.SupplyUses == 1,
            "F: the padded row reaches the display projection with its scans and supply uses");

        // What the route memory may learn. A packet that did not decode proves the peer runs this build, but
        // it proves nothing about which transport can carry a packet this machine can read, and learning it
        // is how one unusable packet switched the working route off for the rest of the run.
        SquadNet.ResetRoutes();
        Check(SquadNet.RouteOf(host) == null, "F: no route is known before a packet has been read");
        byte[] truncated = new byte[SquadPackets.SnapshotBytes - 8];
        Buffer.BlockCopy(rowPacket, 0, truncated, 0, truncated.Length);
        int unusableBefore = SquadNet.UnusablePackets;
        Check(SquadNet.TryAcceptApiPayload(host, truncated, truncated.Length),
            "F: a truncated packet of ours is consumed rather than handed to the game");
        SquadNet.Drain();
        Check(SquadNet.RouteOf(host) == null, "F: a packet that did not decode teaches no route");
        Check(SquadNet.UnusablePackets == unusableBefore + 1, "F: a packet that did not decode is counted as unusable");

        Check(SquadNet.TryAcceptApiPayload(host, paddedRow, paddedRow.Length), "F: a second padded row is ours");
        SquadNet.Drain();
        Check(SquadNet.RouteOf(host) == true, "F: a decoded packet over GTFO-API is remembered as that transport");
        SquadNet.ResetRoutes();
        Check(SquadNet.RouteOf(host) == null, "F: a new expedition forgets every route");

        // The raw channel is unchanged: it is read against the size the network layer declared.
        Check(SquadPackets.Parse(rowPacket, rowPacket.Length).Kind == SquadPacketKind.Snapshot,
            "F: the raw channel reads an exact packet as a row");
        Check(SquadNet.TryAccept(rowPacket, (uint)rowPacket.Length, host),
            "F: the raw channel still claims its own packet");
        SquadNet.Drain();
        Check(SquadNet.RouteOf(host) == false, "F: a decoded packet over the raw channel is remembered as that transport");
        Check(!SquadNet.TryAcceptApiPayload(host, new byte[] { 1, 2, 3 }, 3), "F: a foreign payload is not claimed");

        SquadNet.OnHello = null; SquadNet.OnSnapshot = null; SquadNet.OnProtocolMismatch = null;
        SquadNet.ResetRoutes();
        Console.WriteLine("TRANSPORT-FRAMING-EVIDENCE: production SquadNet receive path reads a padded GTFO-API payload as the packet its envelope declares, keeps a route only from a decoded packet, and reports a packet it could not use (managed boundary doubles; no native/Steam claim).");
    }
}
