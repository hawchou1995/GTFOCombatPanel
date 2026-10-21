using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HawChow.GTFOCombatPanel;

// Protocol 7: the kill-state split, the player's own supply consumption and the health it lost, plus the
// "this total covers only the rows I could measure" bit that keeps a host-scoped denominator from being read
// as the whole squad's. Everything below is driven through the shipped types - SyncProtocol, SquadPackets,
// SquadNet's queue and SquadExchange - so an assertion that passes here is an assertion about the code that
// ships. Nothing here claims a real multiplayer session: the transport is a managed double whose packets are
// framed, parsed, queued and dispatched by the production path, and `SNet` itself is a stub (TransportStubs).
internal static partial class Program
{
    private static void RunSyncRevisionScenarios()
    {
        SyncRevisionRoundTrip();
        SyncRevisionOldProtocol();
        SyncRevisionTopologies();
        SyncRevisionReconnect();
        SyncRevisionPartialDenominator();
        SyncRevisionArchive();
        SquadNet.OnHello = null; SquadNet.OnSnapshot = null; SquadNet.OnProtocolMismatch = null;
    }

    // ---------------------------------------------------------------- every new field, both ways

    private static void SyncRevisionRoundTrip()
    {
        Check(SyncProtocol.Version == 7, "R1: the wire is protocol 7, so a v6 peer is refused by name instead of decoded");
        Check(SyncProtocol.SnapshotSize == 352 && SquadPackets.SnapshotBytes == 368 && SquadPackets.HelloBytes == 24,
            "R1: the snapshot grew from 304 to exactly 352 bytes and the envelope states that length (" + SyncProtocol.SnapshotSize + ")");

        var source = new CombatPlayer(0x1001UL) { Name = "Owner" };
        source.Total.AddAccuracy(40, 25, 9);
        source.Total.AddEnemyDamage(512.5);
        source.Total.AddFriendlyFireDamage(12.5);
        ImpactCounters impact = source.Impact;
        impact.MinorKills = 9; impact.EliteKills = 4; impact.BossKills = 2;
        impact.MeleeKills = 3; impact.ScoutKills = 1; impact.MeleeKillsObserved = true; impact.ScoutKillsObserved = true;
        impact.SleepingMinorKills = 5;
        impact.SleepingEliteKills = 3;
        impact.SleepingBossKills = 1;
        impact.SleepingMeleeKills = 2;
        impact.SleepingScoutKills = 1;
        impact.UnknownStateMinorKills = 2;
        impact.UnknownStateEliteKills = 1;
        impact.UnknownStateBossKills = 1;
        impact.UnknownStateMeleeKills = 1;
        impact.UnknownStateScoutKills = 0;
        impact.SelfSupplyUses = 6;
        impact.SelfAmmoUses = 4;
        impact.HealthLost = 148.5;

        SyncSnapshot captured = SyncProtocol.Capture(source, 0, 1, 2, 3, 1234, true);
        Check(captured.SleepingMinorKills == 5 && captured.SleepingEliteKills == 3 && captured.SleepingBossKills == 1
            && captured.SleepingMeleeKills == 2 && captured.SleepingScoutKills == 1,
            "R1: capture carries the sleeping part of every kill tier");
        Check(captured.UnknownStateMinorKills == 2 && captured.UnknownStateEliteKills == 1 && captured.UnknownStateBossKills == 1
            && captured.UnknownStateMeleeKills == 1 && captured.UnknownStateScoutKills == 0,
            "R1: capture carries the kills whose pre-death state could not be observed, apart from both awake and sleeping");
        Check(captured.SelfSupplyUses == 6 && captured.SelfAmmoUses == 4, "R1: capture carries the player's own consumption pair");
        Check(Math.Abs(captured.HealthLost - 148.5f) < 0.01f, "R1: capture carries the health the player actually lost");
        Check(captured.Has(SyncFlags.SquadDamageKnown) && !captured.Has(SyncFlags.SquadDamagePartial),
            "R1: a complete host total is stamped as measured, not as partial");

        byte[] wire = SyncProtocol.Encode(captured);
        Check(wire.Length == 352, "R1: one snapshot is exactly 352 bytes on the wire");
        Check(SyncProtocol.Decode(wire, 0, wire.Length, out SyncSnapshot read)
            && read.SleepingMinorKills == 5 && read.SleepingEliteKills == 3 && read.SleepingBossKills == 1
            && read.SleepingMeleeKills == 2 && read.SleepingScoutKills == 1
            && read.UnknownStateMinorKills == 2 && read.UnknownStateEliteKills == 1 && read.UnknownStateBossKills == 1
            && read.UnknownStateMeleeKills == 1 && read.UnknownStateScoutKills == 0
            && read.SelfSupplyUses == 6 && read.SelfAmmoUses == 4
            && Math.Abs(read.HealthLost - 148.5f) < 0.01f,
            "R1: every v7 field survives the snapshot wire unchanged");
        Check(SyncProtocol.Encode(read).SequenceEqual(wire), "R1: decoding a packet and re-encoding it is byte-identical");
        Check(CounterRoundTrips(), "R1: each of the thirteen new figures survives on its own, so no two fields share a slot");

        var restored = new CombatPlayer(0x1001UL);
        SyncProtocol.Apply(restored, read, 0, 1, 2, 3);
        Check(restored.Impact.SleepingMinorKills == 5 && restored.Impact.SleepingMeleeKills == 2 && restored.Impact.SleepingScoutKills == 1
            && restored.Impact.UnknownStateMinorKills == 2 && restored.Impact.UnknownStateScoutKills == 0,
            "R1: apply restores the kill-state split exactly, including a real zero");
        Check(restored.Impact.SelfSupplyUses == 6 && restored.Impact.SelfAmmoUses == 4 && Math.Abs(restored.Impact.HealthLost - 148.5) < 0.01,
            "R1: apply restores the consumption pair and the health lost");

        // A second description of the same row: the host counts the kills, the owner counts its own backpack.
        var hostSide = new SyncSnapshot { Owner = 0x1001UL };
        hostSide.SleepingMinorKills = 5; hostSide.SleepingEliteKills = 3;
        hostSide.UnknownStateMinorKills = 2; hostSide.UnknownStateEliteKills = 1; hostSide.UnknownStateBossKills = 1;
        hostSide.UnknownStateMeleeKills = 1;
        hostSide.HealthLost = 148.5f;
        var peerSide = new SyncSnapshot { Owner = 0x1001UL };
        peerSide.SleepingMinorKills = 0; peerSide.SleepingEliteKills = 0;
        peerSide.SelfSupplyUses = 6; peerSide.SelfAmmoUses = 4;
        SyncSnapshot combined = SyncProtocol.Combine(hostSide, true, peerSide, false);
        Check(combined.SleepingMinorKills == 5 && combined.SleepingEliteKills == 3 && combined.UnknownStateMinorKills == 2,
            "R1: an empty peer description cannot erase the host's kill-state counts");
        Check(combined.SelfSupplyUses == 6 && combined.SelfAmmoUses == 4, "R1: the owner's own consumption pair is carried by the merge");
        Check(Math.Abs(combined.HealthLost - 148.5f) < 0.01f, "R1: a peer's zero health loss never erases the host's figure");
        SyncSnapshot reversed = SyncProtocol.Combine(peerSide, false, hostSide, true);
        Check(Math.Abs(reversed.HealthLost - 148.5f) < 0.01f && reversed.SleepingMinorKills == 5,
            "R1: and the same holds whichever description arrives first");
        SyncSnapshot hostWins = SyncProtocol.Combine(
            new SyncSnapshot { Owner = 0x1001UL, HealthLost = 20f }, true,
            new SyncSnapshot { Owner = 0x1001UL, HealthLost = 99f }, false);
        Check(Math.Abs(hostWins.HealthLost - 20f) < 0.01f, "R1: when both sides report health lost the host's figure stands");

        var display = new CombatPlayer(0x1001UL);
        CombatMerge.ApplyRemote(display, captured, 0, 1, 2, 3, 0x1001UL);
        Check(display.Impact.SleepingMinorKills == 5 && display.Impact.SleepingEliteKills == 3 && display.Impact.SleepingBossKills == 1
            && display.Impact.SleepingMeleeKills == 2 && display.Impact.SleepingScoutKills == 1
            && display.Impact.UnknownStateMinorKills == 2 && display.Impact.UnknownStateMeleeKills == 1
            && Math.Abs(display.Impact.HealthLost - 148.5) < 0.01,
            "R1: the in-level display projection receives the kill-state split and the health lost");
        var ownDisplay = new CombatPlayer(0x1001UL);
        ownDisplay.Impact.SelfSupplyUses = 2; ownDisplay.Impact.SelfAmmoUses = 1;
        CombatMerge.ApplyRemote(ownDisplay, captured, 0, 1, 2, 3, 0x1001UL);
        Check(ownDisplay.Impact.SelfSupplyUses == 2 && ownDisplay.Impact.SelfAmmoUses == 1,
            "R1: a row about this machine's own player keeps the consumption it counted itself");
        var otherDisplay = new CombatPlayer(0x1001UL);
        CombatMerge.ApplyRemote(otherDisplay, captured, 0, 1, 2, 3, 0x2002UL);
        Check(otherDisplay.Impact.SelfSupplyUses == 6 && otherDisplay.Impact.SelfAmmoUses == 4,
            "R1: a row about another player takes that player's own consumption pair");
        Check(CombatMerge.PreferClientOwned(captured, peerSide).SelfSupplyUses == 6,
            "R1: what the host relays about a client carries the client's own consumption pair");
    }

    // One figure at a time, each in its own slot: a field read back from the wrong offset would break here
    // even though a full row would still look plausible.
    private static bool CounterRoundTrips()
    {
        var expected = new long[13];
        for (int index = 0; index < 13; index++)
        {
            // A fresh row per figure: nothing an earlier iteration set can be mistaken for this one's value,
            // whatever a future Reset() does or does not clear.
            var source = new CombatPlayer(0x1100UL);
            expected[index] = 1000 + index * 7;
            switch (index)
            {
                case 0: source.Impact.SleepingMinorKills = expected[index]; break;
                case 1: source.Impact.SleepingEliteKills = expected[index]; break;
                case 2: source.Impact.SleepingBossKills = expected[index]; break;
                case 3: source.Impact.SleepingMeleeKills = expected[index]; break;
                case 4: source.Impact.SleepingScoutKills = expected[index]; break;
                case 5: source.Impact.UnknownStateMinorKills = expected[index]; break;
                case 6: source.Impact.UnknownStateEliteKills = expected[index]; break;
                case 7: source.Impact.UnknownStateBossKills = expected[index]; break;
                case 8: source.Impact.UnknownStateMeleeKills = expected[index]; break;
                case 9: source.Impact.UnknownStateScoutKills = expected[index]; break;
                case 10: source.Impact.SelfSupplyUses = expected[index]; break;
                case 11: source.Impact.SelfAmmoUses = expected[index]; break;
                case 12: source.Impact.HealthLost = expected[index] + 0.5; break;
            }
            byte[] bytes = SyncProtocol.Encode(SyncProtocol.Capture(source, 0, 1, 2, 3, 0, false));
            if (!SyncProtocol.Decode(bytes, 0, bytes.Length, out SyncSnapshot read)) return false;
            long[] seen =
            {
                read.SleepingMinorKills, read.SleepingEliteKills, read.SleepingBossKills, read.SleepingMeleeKills, read.SleepingScoutKills,
                read.UnknownStateMinorKills, read.UnknownStateEliteKills, read.UnknownStateBossKills, read.UnknownStateMeleeKills, read.UnknownStateScoutKills,
                read.SelfSupplyUses, read.SelfAmmoUses, (long)read.HealthLost
            };
            for (int slot = 0; slot < seen.Length; slot++)
                if (seen[slot] != (slot == index ? expected[index] : 0)) return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- the previous protocol

    private static void SyncRevisionOldProtocol()
    {
        var exchange = new SquadExchange();
        exchange.Configure(0x3001UL, 0, 1, 2, 3);
        exchange.SetRoster(new[]
        {
            new SquadPeer(0x3001UL, 0, true, false, true),
            new SquadPeer(0x3002UL, 1, false, false, false)
        }, true);
        ulong mismatchSender = 0;
        uint mismatchVersion = 0;
        SquadNet.OnProtocolMismatch = (sender, version) => { mismatchSender = sender; mismatchVersion = version; };
        SquadNet.OnSnapshot = (sender, generation, snapshot) => exchange.OnSnapshot(sender, generation, snapshot, 1f);

        SyncSnapshot row = SyncProtocol.Capture(new CombatPlayer(0x3002UL), 0, 1, 2, 3, 0, false);
        byte[] v6Hello = SquadPackets.Hello(0x3002UL, 1, 6);
        byte[] v6Row = SquadPackets.Snapshot(row, 1, 6);
        Check(SquadPackets.Parse(v6Hello, v6Hello.Length).Kind == SquadPacketKind.VersionMismatch,
            "R2: a v6 hello is refused by name rather than decoded");
        Check(SquadPackets.Parse(v6Row, v6Row.Length).Kind == SquadPacketKind.VersionMismatch,
            "R2: and so is a v6 row, whose body is 304 bytes where this build reads 352");
        Check(SquadPackets.Parse(v6Row, v6Row.Length).IsOurs && SquadPackets.Parse(v6Hello, v6Hello.Length).IsOurs,
            "R2: both are still recognised as this mod's packets, so neither is handed back to the game's replication layer");
        Check(SquadNet.TryAccept(v6Row, (uint)v6Row.Length, 0x3002UL), "R2: the transport consumes a v6 row instead of passing it on");
        SquadNet.Drain();
        Check(mismatchSender == 0x3002UL && mismatchVersion == 6,
            "R2: the mismatch diagnostic names the real sender and the version it actually speaks");
        Check(!exchange.HasSnapshot(0x3002UL) && exchange.GenerationOf(0x3002UL) == 0u,
            "R2: a refused protocol leaves no cached row and no adopted run counter behind");

        byte[] foreign = { 0x01, 0x02, 0x43, 0x4D, 0x50, 0x4C, 0x01, 0x00, 0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        Check(SquadPackets.Parse(foreign, foreign.Length).Kind == SquadPacketKind.Foreign && !SquadNet.TryAccept(foreign, (uint)foreign.Length, 0x3002UL),
            "R2: a packet with another mod's key is handed straight back, so nothing here touches the native channel");

        // Exact lengths, both kinds, both directions.
        byte[] v7Row = SquadPackets.Snapshot(row, 1, SyncProtocol.Version);
        Check(SquadPackets.Parse(v7Row, v7Row.Length).Kind == SquadPacketKind.Snapshot, "R2: the current row still parses");
        Check(SquadPackets.Parse(v7Row, v7Row.Length - 1).Kind == SquadPacketKind.Malformed,
            "R2: a body one byte short of a snapshot is refused instead of being half-read");
        Check(SquadPackets.Parse(v7Row, v7Row.Length - SyncProtocol.SnapshotSize).Kind == SquadPacketKind.Malformed,
            "R2: a snapshot declared with no body at all is refused");
        byte[] padded = new byte[v7Row.Length + 8];
        Buffer.BlockCopy(v7Row, 0, padded, 0, v7Row.Length);
        Check(SquadPackets.Parse(padded, padded.Length).Kind == SquadPacketKind.Malformed,
            "R2: a body longer than one snapshot is refused, so a build that added a field can never be read as though it had not");
        Check(!SyncProtocol.Decode(v7Row, 16, v7Row.Length - 16 + 4, out _) && !SyncProtocol.Decode(v7Row, 16, v7Row.Length - 17, out _),
            "R2: the decoder itself insists on exactly one snapshot rather than at least one");
        byte[] hello = SquadPackets.Hello(0x3002UL, 1, SyncProtocol.Version);
        Check(SquadPackets.Parse(hello, hello.Length).Kind == SquadPacketKind.Hello, "R2: the hello still parses");
        byte[] longHello = new byte[hello.Length + 4];
        Buffer.BlockCopy(hello, 0, longHello, 0, hello.Length);
        Check(SquadPackets.Parse(longHello, longHello.Length).Kind == SquadPacketKind.Malformed,
            "R2: a hello that carries more than its own lookup is refused");
        Check(SquadPackets.Parse(hello, hello.Length - 1).Kind == SquadPacketKind.Malformed,
            "R2: a hello one byte short is refused");
        SquadNet.OnProtocolMismatch = null; SquadNet.OnSnapshot = null;
    }

    // ---------------------------------------------------------------- the three topologies, plus a late join

    private static void SyncRevisionTopologies()
    {
        // T1: modded host, two modded clients. The host is the authority on the kill-state split; each client
        // keeps the consumption pair only it can count.
        var wire = new Wire();
        var host = new Node(0x40UL, 0) { IsMaster = true };
        var clientA = new Node(0x41UL, 1);
        var clientB = new Node(0x42UL, 2);
        wire.Add(host); wire.Add(clientA); wire.Add(clientB);
        host.Row(0x40UL).Total.AddEnemyDamage(400);
        host.Row(0x41UL).Total.AddEnemyDamage(300);
        host.Row(0x42UL).Total.AddEnemyDamage(200);
        host.Row(0x41UL).Impact.MinorKills = 4; host.Row(0x41UL).Impact.SleepingMinorKills = 3;
        host.Row(0x41UL).Impact.UnknownStateMinorKills = 1; host.Row(0x41UL).Impact.HealthLost = 120;
        clientA.Row(0x41UL).Impact.SelfSupplyUses = 5; clientA.Row(0x41UL).Impact.SelfAmmoUses = 3;
        for (int tick = 0; tick < 24; tick++) wire.Tick(tick * 0.25f);
        List<CombatPlayer> seenByA = clientA.Exchange.Project(clientA.Store, clientA.Lookup);
        List<CombatPlayer> seenByHost = host.Exchange.Project(host.Store, host.Lookup);
        Check(Row(seenByA, 0x41UL) != null && Row(seenByA, 0x41UL).Impact.SleepingMinorKills == 3
            && Row(seenByA, 0x41UL).Impact.UnknownStateMinorKills == 1
            && Math.Abs(Row(seenByA, 0x41UL).Impact.HealthLost - 120) < 0.01,
            "T1: a client is told the pre-death state of its own kills and the health it lost, from the host that saw them");
        Check(Row(seenByA, 0x41UL).Impact.SelfSupplyUses == 5 && Row(seenByA, 0x41UL).Impact.SelfAmmoUses == 3,
            "T1: and its own consumption pair survives everything the host said about that row");
        Check(Row(seenByA, 0x42UL) != null && Row(seenByA, 0x42UL).Total.EnemyDamage > 0 && Row(seenByA, 0x40UL) != null,
            "T1: every seat is on the client's screen, including the rows only the host could describe");
        Check(Row(seenByHost, 0x41UL).Impact.SelfSupplyUses == 5 && Row(seenByHost, 0x41UL).Impact.SelfAmmoUses == 3,
            "T1: and the host folds that same pair into the row it shows for the client");
        Check(clientA.Exchange.SquadFromHost.HasValue && !clientA.Exchange.SquadFromHostPartial,
            "T1: a host whose store covers every seat states a complete squad total");

        // T2: a host that runs no mod. The two clients find each other directly, the vanilla host is not given
        // a row, and no total is ever claimed on its behalf.
        var wire2 = new Wire();
        var vanilla = new Node(0x50UL, 0) { IsMaster = true, Modded = false };
        var client2A = new Node(0x51UL, 1);
        var client2B = new Node(0x52UL, 2);
        wire2.Add(vanilla); wire2.Add(client2A); wire2.Add(client2B);
        client2A.Row(0x51UL).Total.AddEnemyDamage(140);
        client2B.Row(0x52UL).Total.AddEnemyDamage(210);
        client2B.Row(0x52UL).Impact.SelfSupplyUses = 2; client2B.Row(0x52UL).Impact.SelfAmmoUses = 1;
        for (int tick = 0; tick < 40; tick++) wire2.Tick(tick * 0.25f);
        List<CombatPlayer> crossA = client2A.Exchange.Project(client2A.Store, client2A.Lookup);
        Check(Row(crossA, 0x52UL) != null && Near(Row(crossA, 0x52UL).Total.EnemyDamage, 210)
            && Row(crossA, 0x52UL).Impact.SelfSupplyUses == 2 && Row(crossA, 0x52UL).Impact.SelfAmmoUses == 1,
            "T2: two clients behind an unmodified host exchange their own rows - v7 fields included - without it");
        Check(Row(crossA, 0x50UL) == null && !client2A.Exchange.SquadFromHost.HasValue,
            "T2: the host that runs no mod is never described and never claims a squad total");

        // T3: a client that joins while the others are already exchanging.
        var wire3 = new Wire();
        var host3 = new Node(0x60UL, 0) { IsMaster = true };
        var early = new Node(0x61UL, 1);
        wire3.Add(host3); wire3.Add(early);
        host3.Row(0x60UL).Total.AddEnemyDamage(100);
        host3.Row(0x61UL).Total.AddEnemyDamage(150);
        for (int tick = 0; tick < 16; tick++) wire3.Tick(tick * 0.25f);
        var late = new Node(0x62UL, 2);
        wire3.Add(late);
        late.Row(0x62UL).Total.AddEnemyDamage(90);
        late.Row(0x62UL).Impact.SelfSupplyUses = 1; late.Row(0x62UL).Impact.SelfAmmoUses = 1;
        host3.Row(0x62UL).Total.AddEnemyDamage(80);
        host3.Row(0x62UL).Impact.SleepingMinorKills = 2;
        int ticksToSee = 0;
        for (int tick = 16; tick < 40; tick++)
        {
            wire3.Tick(tick * 0.25f);
            ticksToSee = tick - 16 + 1;
            List<CombatPlayer> rows = late.Exchange.Project(late.Store, late.Lookup);
            if (Row(rows, 0x60UL) != null && Row(rows, 0x61UL) != null && Row(rows, 0x62UL).Impact.SleepingMinorKills == 2) break;
        }
        List<CombatPlayer> lateRows = late.Exchange.Project(late.Store, late.Lookup);
        List<CombatPlayer> earlyRows = early.Exchange.Project(early.Store, early.Lookup);
        Console.WriteLine("LATE-JOIN-EVIDENCE: the late joiner's rows settled after " + ticksToSee + " ticks ("
            + (ticksToSee * 0.25f).ToString("0.00") + "s) of the exchange.");
        Check(Row(lateRows, 0x60UL) != null && Row(lateRows, 0x61UL) != null && Row(lateRows, 0x62UL) != null,
            "T3: a client that joins mid-level ends up holding every seat, itself included");
        Check(Row(lateRows, 0x62UL).Impact.SleepingMinorKills == 2,
            "T3: and the host's description of its kills reaches it, on the same run counter the others are on");
        Check(Row(earlyRows, 0x62UL) != null && Row(earlyRows, 0x62UL).Impact.SelfAmmoUses == 1,
            "T3: while the client that was already there learns the newcomer's own consumption pair");
        Check(late.Exchange.SquadFromHost.HasValue && !late.Exchange.SquadFromHostPartial,
            "T3: the late joiner is given the host's total by the ordinary route, once every seat has a row");
    }

    // ---------------------------------------------------------------- restart, duplicates, stragglers

    private static void SyncRevisionReconnect()
    {
        var wire = new Wire();
        var host = new Node(0x70UL, 0) { IsMaster = true };
        var guest = new Node(0x71UL, 1);
        wire.Add(host); wire.Add(guest);
        host.Row(0x70UL).Total.AddEnemyDamage(100);
        guest.Row(0x71UL).Total.AddEnemyDamage(70);
        guest.Row(0x71UL).Impact.SleepingMinorKills = 1;
        for (int tick = 0; tick < 8; tick++) wire.Tick(tick * 0.25f);
        for (int run = 0; run < 6; run++) guest.Exchange.ResetForExpedition();
        for (int tick = 8; tick < 12; tick++) wire.Tick(tick * 0.25f);
        uint before = host.Exchange.GenerationOf(0x71UL);
        guest.Restart();
        guest.Row(0x71UL).Impact.SleepingMinorKills = 4;
        guest.Row(0x71UL).Impact.UnknownStateEliteKills = 2;
        guest.Row(0x71UL).Impact.HealthLost = 88;
        guest.Row(0x71UL).Total.AddEnemyDamage(500);
        int ticksToRecover = 0;
        for (int tick = 12; tick < 24; tick++)
        {
            wire.Tick(tick * 0.25f);
            ticksToRecover = tick - 12 + 1;
            List<CombatPlayer> rows = host.Exchange.Project(host.Store, host.Lookup);
            if (Row(rows, 0x71UL) != null && Row(rows, 0x71UL).Impact.SleepingMinorKills == 4) break;
        }
        List<CombatPlayer> recovered = host.Exchange.Project(host.Store, host.Lookup);
        Console.WriteLine("RECONNECT-EVIDENCE: peer remembered at generation=" + before + ", restarted at generation="
            + guest.Exchange.Generation + ", recovered in " + ticksToRecover + " ticks with sleepingKills="
            + Row(recovered, 0x71UL).Impact.SleepingMinorKills + ".");
        Check(Row(recovered, 0x71UL).Impact.SleepingMinorKills == 4 && Row(recovered, 0x71UL).Impact.UnknownStateEliteKills == 2
            && Math.Abs(Row(recovered, 0x71UL).Impact.HealthLost - 88) < 0.01,
            "R3: a peer that restarts has its new run's figures adopted, not the old run's kept");
        Check(host.Exchange.GenerationOf(0x71UL) == 1u, "R3: and the counter it insists on is the one the host now holds for it");

        // Duplicates and out-of-order traffic, on the cache and on the exchange.
        var exchange = new SquadExchange();
        exchange.Configure(0x80UL, 0, 1, 2, 3);
        exchange.SetRoster(new[]
        {
            new SquadPeer(0x80UL, 0, true, false, true),
            new SquadPeer(0x81UL, 1, false, false, false)
        }, true);
        var first = new SyncSnapshot { Owner = 0x81UL, HealthLost = 40f };
        first.SleepingMinorKills = 3;
        var second = new SyncSnapshot { Owner = 0x81UL, HealthLost = 55f };
        second.SleepingMinorKills = 5;
        Check(exchange.OnSnapshot(0x81UL, 4u, first, 1f).Accepted, "R3: the first row of a run is taken");
        for (int again = 0; again < 6; again++)
            Check(exchange.OnSnapshot(0x81UL, 4u, first, 1.1f + again * 0.1f).Accepted, "R3: a duplicate of the same row is still taken and still lands on the same figures");
        SquadDecision straggler = exchange.OnSnapshot(0x81UL, 3u, second, 2f);
        List<CombatPlayer> afterStraggler = exchange.Project(Array.Empty<CombatPlayer>(), 0x80UL);
        Check(!straggler.Accepted && Row(afterStraggler, 0x81UL).Impact.SleepingMinorKills == 3
            && Math.Abs(Row(afterStraggler, 0x81UL).Impact.HealthLost - 40) < 0.01,
            "R3: a row still speaking for the previous run is refused and cannot roll the figures back");
        Check(exchange.OnSnapshot(0x81UL, 4u, second, 3f).Accepted
            && Row(exchange.Project(Array.Empty<CombatPlayer>(), 0x80UL), 0x81UL).Impact.SleepingMinorKills == 5,
            "R3: the newest row of the run in hand is then taken, so recovery is not a refusal that sticks");
    }

    // ---------------------------------------------------------------- a denominator that is not the squad

    private static void SyncRevisionPartialDenominator()
    {
        // The host has a figure for itself and for one client, and none for the other: its total covers only
        // its own store. That total used to travel as though it were the squad's, which is how a client ends up
        // dividing real damage by a denominator that is short of the squad it is describing.
        var exchange = new SquadExchange();
        exchange.Configure(0x90UL, 0, 1, 2, 3);
        exchange.SetRoster(new[]
        {
            new SquadPeer(0x90UL, 0, true, false, true),
            new SquadPeer(0x91UL, 1, false, false, false),
            new SquadPeer(0x92UL, 2, false, false, false)
        }, true);
        var hostRow = new CombatPlayer(0x90UL) { DisplaySlot = 0 };
        hostRow.Total.AddEnemyDamage(100);
        var known = new CombatPlayer(0x91UL) { DisplaySlot = 1 };
        known.Total.AddEnemyDamage(600);
        var measured = new List<CombatPlayer> { hostRow, known };      // 0x92 has no row on the host at all
        List<SquadSend> sends = exchange.Tick(0f, measured);
        SyncSnapshot aboutHost = default;
        bool found = false;
        foreach (SquadSend send in sends)
            if (send.Kind == SquadSendKind.Snapshot && send.Snapshot.Owner == 0x90UL) { aboutHost = send.Snapshot; found = true; break; }
        Check(found && aboutHost.Has(SyncFlags.SquadDamageKnown) && aboutHost.Has(SyncFlags.SquadDamagePartial),
            "P1: a host whose store is missing a slotted player marks its total as partial instead of claiming the squad's");
        Check(Math.Abs(aboutHost.SquadEnemyDamage - 700f) < 0.01f,
            "P1: and the figure it states is still its own sum - the number is not invented or rounded up to the squad");

        var client = new SquadExchange();
        client.Configure(0x91UL, 0, 1, 2, 3);
        client.SetRoster(new[]
        {
            new SquadPeer(0x90UL, 0, false, false, true),
            new SquadPeer(0x91UL, 1, true, false, false),
            new SquadPeer(0x92UL, 2, false, false, false)
        }, false);
        Check(client.OnSnapshot(0x90UL, 1u, aboutHost, 1f).Accepted, "P1: the client takes the host's row and its total");
        Check(client.SquadFromHost.HasValue && Near(client.SquadFromHost, 700) && client.SquadFromHostPartial,
            "P1: and knows that the total it must divide by covers only part of the squad");

        var ownRow = new CombatPlayer(0x91UL) { DisplaySlot = 1 };
        ownRow.Total.AddEnemyDamage(600);
        CombatPlayer projected = CombatMerge.Project(ownRow);
        projected.Total.SquadDamageEstimated = client.SquadFromHostPartial;
        Check(CombatStatsFormatter.FormatShare(projected.Total, client.SquadFromHost).StartsWith("~"),
            "P1: so the share it prints says it is an estimate rather than reading as the squad's measurement");
        projected.Total.SquadDamageEstimated = false;
        Check(!CombatStatsFormatter.FormatShare(projected.Total, client.SquadFromHost).StartsWith("~"),
            "P1: and a complete total still prints without the estimate marker");

        // The same host, once every seat has a measured row: the total is the squad's and says so.
        var full = new List<CombatPlayer> { hostRow, known };
        var lateRow = new CombatPlayer(0x92UL) { DisplaySlot = 2 };
        lateRow.Total.AddEnemyDamage(50);
        full.Add(lateRow);
        List<SquadSend> complete = exchange.Tick(2f, full);
        SyncSnapshot completeHost = default;
        foreach (SquadSend send in complete)
            if (send.Kind == SquadSendKind.Snapshot && send.Snapshot.Owner == 0x90UL) { completeHost = send.Snapshot; break; }
        Check(completeHost.Has(SyncFlags.SquadDamageKnown) && !completeHost.Has(SyncFlags.SquadDamagePartial),
            "P1: once every seat has a figure the total is stated as the squad's again, with no marker");
        SyncSnapshot peerWithoutTotal = new SyncSnapshot { Owner = 0x90UL };
        SyncSnapshot keptPartial = SyncProtocol.Combine(aboutHost, true, peerWithoutTotal, false);
        Check(keptPartial.Has(SyncFlags.SquadDamageKnown) && keptPartial.Has(SyncFlags.SquadDamagePartial),
            "P1: a peer description that carries no total cannot clear the partial bit from the total that is held");

        // A row that exists but carries no figure yet is not a gap in the denominator: that player has simply
        // contributed nothing so far. Marking the whole board as an estimate until the last player's first shot
        // would be a marker with no meaning, so presence - not "has a figure" - is what decides the bit.
        var silent = new CombatPlayer(0x92UL) { DisplaySlot = 2 };
        List<SquadSend> unobserved = exchange.Tick(4f, new List<CombatPlayer> { hostRow, known, silent });
        SyncSnapshot unobservedHost = default;
        foreach (SquadSend send in unobserved)
            if (send.Kind == SquadSendKind.Snapshot && send.Snapshot.Owner == 0x90UL) { unobservedHost = send.Snapshot; break; }
        Check(unobservedHost.Has(SyncFlags.SquadDamageKnown) && !unobservedHost.Has(SyncFlags.SquadDamagePartial),
            "P1: a seat whose row exists but has no figure yet is not a gap, so the total is not marked as partial for it");
    }

    // ---------------------------------------------------------------- the settlement file

    private static void SyncRevisionArchive()
    {
        var counters = new ImpactCounters
        {
            MinorKills = 9, EliteKills = 2, BossKills = 1,
            SleepingMinorKills = 5, SleepingEliteKills = 1, SleepingBossKills = 1,
            SleepingMeleeKills = 2, SleepingScoutKills = 1,
            UnknownStateMinorKills = 2, UnknownStateEliteKills = 1,
            SelfSupplyUses = 6, SelfAmmoUses = 4, HealthLost = 148.5
        };
        var input = new ImpactInput { Name = "Runner", Lookup = 0xA0UL, Counters = counters };
        input.EnemyDamageObserved = true; input.EnemyDamage = 1200;
        var report = ImpactScoring.Evaluate(new[] { input }, ImpactScoring.DefaultWeights, 1, 1);
        var board = ImpactBoard.Build(report, true, true);
        var metadata = ExpeditionMetadata.Create("Survival", "FX6", "Circle");
        using JsonDocument json = JsonDocument.Parse(ResultsLog.Build(true, new DateTime(2026, 10, 21), "1.17.31", board, report, metadata, 7));
        JsonElement root = json.RootElement;
        Check(root.GetProperty("run").GetUInt32() == 7,
            "A1: the settlement file names the run its figures belong to, so a straggler's row can be told from a fresh one");
        JsonElement player = root.GetProperty("players")[0];
        JsonElement written = player.GetProperty("counters");
        Check(written.GetProperty("sleepingMinorKills").GetInt64() == 5 && written.GetProperty("sleepingEliteKills").GetInt64() == 1
            && written.GetProperty("sleepingBossKills").GetInt64() == 1 && written.GetProperty("sleepingMeleeKills").GetInt64() == 2
            && written.GetProperty("sleepingScoutKills").GetInt64() == 1,
            "A1: the archived row carries the sleeping part of every kill tier as a figure");
        Check(written.GetProperty("unknownStateMinorKills").GetInt64() == 2 && written.GetProperty("unknownStateEliteKills").GetInt64() == 1
            && written.GetProperty("unknownStateBossKills").GetInt64() == 0,
            "A1: and the unknown-state kills, which are neither awake nor sleeping, including a real zero");
        Check(written.GetProperty("selfSupplyUses").GetInt64() == 6 && written.GetProperty("selfAmmoUses").GetInt64() == 4,
            "A1: and the player's own total and ammo-only consumption");
        Check(written.GetProperty("healthLost").GetDouble() == 148.5,
            "A1: and the health it actually lost, in the same pinned format as the score");
        Check(player.GetProperty("score").ValueKind == JsonValueKind.Number && player.GetProperty("name").GetString() == "Runner",
            "A1: the existing shape of the file is unchanged around the new block");
        using JsonDocument withoutCounters = JsonDocument.Parse(ResultsLog.Build(true, new DateTime(2026, 10, 21), "1.17.31", board,
            report, metadata));
        Check(withoutCounters.RootElement.GetProperty("run").GetUInt32() == 0
            && withoutCounters.RootElement.GetProperty("players")[0].TryGetProperty("counters", out _),
            "A1: a settlement written without a run counter still names the field, and still carries its counters");
    }
}
