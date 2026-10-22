using System;
using System.Collections.Generic;
using System.Linq;
using HawChow.GTFOCombatPanel;

// The squad exchange, driven in memory, in the three topologies the release has to work in. `SquadExchange`
// holds no reference to SNet, Unity or Harmony, so it is driven here exactly as the runtime drives it: a
// roster taken from the game's slot list, a tick, the packets it asks for, and the packets that come back.
// Every figure below is put into a real row and read back through the real projection, so an assertion that
// passes here is an assertion about the shipped logic rather than about a description of it.
internal static partial class Program
{
    private static void RunSquadScenarios()
    {
        // ---------------------------------------------------------------- B1: modded host, two modded clients
        // The reported symptom was a client that could see only itself. Every machine has to end up holding
        // every seat, the rows a client sees for the others have to be the host's measurements, and the
        // client's own row has to keep the counters only it could count.
        var wire1 = new Wire();
        var host = new Node(0x10UL, 0) { IsMaster = true };
        var clientA = new Node(0x20UL, 1);
        var clientB = new Node(0x30UL, 2);
        wire1.Add(host); wire1.Add(clientA); wire1.Add(clientB);
        host.Row(0x10UL).Total.AddAccuracy(30, 20, 5); host.Row(0x10UL).Total.AddEnemyDamage(300);
        host.Row(0x20UL).Total.AddAccuracy(0, 0, 0, true); host.Row(0x20UL).Total.AddEnemyDamage(600);
        host.Row(0x30UL).Total.AddAccuracy(0, 0, 0, true); host.Row(0x30UL).Total.AddEnemyDamage(250);
        clientA.Row(0x20UL).Total.AddAccuracy(55, 40, 10);
        clientA.Row(0x20UL).Total.AddEnemyDamage(580, true);          // the health-loss estimate a client can make
        clientB.Row(0x30UL).Total.AddAccuracy(20, 15, 2);
        clientB.Row(0x30UL).Total.AddEnemyDamage(240, true);
        byte[] ownRowBefore = SyncProtocol.Encode(SyncProtocol.Capture(clientA.Row(0x20UL), 0, 1, 2, 3, 0, false));
        for (int tick = 0; tick < 24; tick++) wire1.Tick(tick * 0.25f);
        List<CombatPlayer> aRows = clientA.Exchange.Project(clientA.Store, clientA.Lookup);
        List<CombatPlayer> hRows = host.Exchange.Project(host.Store, host.Lookup);
        List<CombatPlayer> bRows = clientB.Exchange.Project(clientB.Store, clientB.Lookup);
        Check(aRows.Count == 3 && hRows.Count == 3 && bRows.Count == 3,
            "B1: after the handshake every node projects one row per seat, so no machine shows itself alone");
        Check(Near(Row(aRows, 0x10UL)?.Total.EnemyDamage, 300) && Row(aRows, 0x10UL)?.Total.ShotsFired == 30,
            "B1: the row a client sees for the host is the host's own measurement");
        Check(Near(Row(aRows, 0x30UL)?.Total.EnemyDamage, 250) && Row(aRows, 0x30UL)?.Total.ShotsFired == 20,
            "B1: and so is the row it sees for the other client");
        Check(Row(aRows, 0x20UL).Total.ShotsFired == 55 && Row(aRows, 0x20UL).Total.Hits == 40
            && Row(aRows, 0x20UL).Total.WeakspotHits == 10 && !Row(aRows, 0x20UL).Total.AccuracyEstimated,
            "B1: a client's own row keeps the aim only it could count, exact and unmarked");
        Check(Near(Row(aRows, 0x20UL).Total.EnemyDamage, 600) && Row(aRows, 0x20UL).Total.EnemyDamageObserved
            && !Row(aRows, 0x20UL).Total.EnemyDamageEstimated,
            "B1: while the damage on that row is the host's, because the host applied the blow");
        Check(!Row(aRows, 0x20UL).Total.SquadDamageEstimated,
            "B1: and the share built on it is no longer the estimate this machine could only guess at");
        Check(SyncProtocol.Encode(SyncProtocol.Capture(clientA.Row(0x20UL), 0, 1, 2, 3, 0, false)).SequenceEqual(ownRowBefore),
            "B1: the client's own capture row is byte-for-byte unchanged by everything it received");
        Check(Row(hRows, 0x20UL).Total.ShotsFired == 55 && Row(hRows, 0x20UL).Total.Hits == 40,
            "B1: and the host shows that client's exact aim too, folded in from the row the client sent it");
        Check(Near(Row(hRows, 0x20UL).Total.EnemyDamage, 600),
            "B1: under the host's own damage figure, which a client's estimate may never displace");
        Check(clientA.Diag.TrueForAll(line => !line.Contains("refused")) && clientB.Diag.TrueForAll(line => !line.Contains("refused")),
            "B1: nothing a client received was refused while the exchange was forming");
        Check(clientA.Diag.Exists(line => line.Contains("peer=16") && line.Contains("reason=")
            && line.Contains("peerGeneration=") && line.Contains("localGeneration=")),
            "C: every accept and refuse line names the peer, the reason and both run counters");

        // ---------------------------------------------------------------- B2: unmodded host, two modded clients
        // The clients find each other directly. The vanilla host is neither described nor given a row, and it
        // is greeted a bounded number of times and then left alone - it was never sent a row at all.
        var wire2 = new Wire();
        var vanilla2 = new Node(0x11UL, 0) { IsMaster = true, Modded = false };
        var client2A = new Node(0x21UL, 1);
        var client2B = new Node(0x31UL, 2);
        wire2.Add(vanilla2); wire2.Add(client2A); wire2.Add(client2B);
        client2A.Row(0x21UL).Total.AddAccuracy(12, 9, 3); client2A.Row(0x21UL).Total.AddEnemyDamage(140, true);
        client2B.Row(0x31UL).Total.AddAccuracy(44, 30, 6); client2B.Row(0x31UL).Total.AddEnemyDamage(210, true);
        for (int tick = 0; tick < 40; tick++) wire2.Tick(tick * 0.25f);
        List<CombatPlayer> rows2A = client2A.Exchange.Project(client2A.Store, client2A.Lookup);
        List<CombatPlayer> rows2B = client2B.Exchange.Project(client2B.Store, client2B.Lookup);
        Check(rows2A.Count == 2 && rows2B.Count == 2,
            "B2: with an unmodded host each client shows exactly itself and the other client, and nobody else");
        Check(Near(Row(rows2A, 0x31UL)?.Total.EnemyDamage, 210) && Row(rows2A, 0x31UL)?.Total.ShotsFired == 44,
            "B2: the row one client shows for the other comes from that other client, not through the host");
        Check(Near(Row(rows2B, 0x21UL)?.Total.EnemyDamage, 140) && Row(rows2B, 0x21UL)?.Total.ShotsFired == 12,
            "B2: and in both directions");
        Check(Row(rows2A, 0x11UL) == null && Row(rows2B, 0x11UL) == null,
            "B2: the host that runs no mod has no row anywhere, because nothing about it was ever described");
        Check(vanilla2.HellosSeen == 0 && vanilla2.SnapshotsSeen == 0,
            "B2: the unmodded host is sent nothing at all - not a greeting and not a row - because a machine that runs no mod has no handler for these bytes, and this mod does not send anything to find out whether one does");
        Check(client2A.Exchange.SquadFromHost == null && CombatStatsFormatter.FormatShare(Row(rows2A, 0x21UL).Total, client2A.Exchange.SquadFromHost) == "—",
            "B2: and with no host to add the squad up, the share is a dash rather than a share of a guessed total");
        // ---------------------------------------------------------------- B3: unmodded host, one modded client
        // Alone: the local row is still exact, everything unreachable is a dash, and nothing renders a NaN.
        var wire3 = new Wire();
        var vanilla3 = new Node(0x12UL, 0) { IsMaster = true, Modded = false };
        var client3 = new Node(0x22UL, 1);
        wire3.Add(vanilla3); wire3.Add(client3);
        client3.Row(0x22UL).Total.AddAccuracy(9, 6, 1);
        client3.Row(0x22UL).Total.AddEnemyDamage(95, true);
        client3.Row(0x22UL).Total.SquadDamageEstimated = true;
        for (int tick = 0; tick < 40; tick++) wire3.Tick(tick * 0.25f);
        List<CombatPlayer> rows3 = client3.Exchange.Project(client3.Store, client3.Lookup);
        Check(rows3.Count == 1 && rows3[0].Total.ShotsFired == 9 && rows3[0].Total.Hits == 6 && rows3[0].Total.WeakspotHits == 1,
            "B3: alone behind an unmodded host, a client still shows its own exact counters");
        Check(!rows3[0].Total.AccuracyEstimated && rows3[0].Total.EnemyDamageEstimated,
            "B3: with its aim exact and its damage - the part only the host could measure - marked as the estimate it is");
        Check(CombatStatsFormatter.FormatShare(rows3[0].Total, null) == "—",
            "B3: with no host to add the squad up the share is a dash rather than a share this machine guessed");
        Check(vanilla3.HellosSeen == 0 && vanilla3.SnapshotsSeen == 0,
            "B3: and a lone client behind an unmodded host sends it nothing either - the rule is the same for one client as for three");
        var cells = new List<string>();
        foreach (PanelColumn column in new[] { PanelColumn.DamageAndShare, PanelColumn.FriendlyFire, PanelColumn.Accuracy, PanelColumn.WeakspotAccuracy, PanelColumn.Projectiles })
            cells.AddRange(CombatStatsFormatter.Fields(rows3[0].Total, column, null));
        Check(cells.TrueForAll(cell => !cell.Contains("NaN") && !cell.Contains("Infinity")),
            "B3: not one rendered cell is a NaN or an infinity");
        Check(cells.Exists(cell => cell == "—"), "B3: a figure this machine cannot know is rendered as a dash");
        Check(cells.Exists(cell => cell.StartsWith("~")), "B3: and a figure it can only estimate carries the estimate marker");

        // ---------------------------------------------------------------- B4: idempotence and no feed-back
        SyncSnapshot hostRowForA = SyncProtocol.Capture(host.Row(0x20UL), 0, 1, 2, 3, 900, true);
        CombatPlayer applied = CombatMerge.Project(clientA.Row(0x20UL));
        for (int again = 0; again < 10; again++)
            CombatMerge.ApplyRemote(applied, hostRowForA, 0, 1, 2, 3, clientA.Lookup);
        CombatPlayer once = CombatMerge.Project(clientA.Row(0x20UL));
        CombatMerge.ApplyRemote(once, hostRowForA, 0, 1, 2, 3, clientA.Lookup);
        Check(SyncProtocol.Encode(SyncProtocol.Capture(applied, 0, 1, 2, 3, 0, false))
            .SequenceEqual(SyncProtocol.Encode(SyncProtocol.Capture(once, 0, 1, 2, 3, 0, false))),
            "B4: applying the same row ten times lands on exactly the same figures as applying it once");
        Check(Near(Row(hRows, 0x20UL).Total.EnemyDamage, 600) && Row(hRows, 0x20UL).Total.EnemyDamageObserved,
            "B4: the host's measured damage stands after the client's own estimated row arrived at the host");
        for (int tick = 24; tick < 40; tick++) wire1.Tick(tick * 0.25f);
        List<CombatPlayer> settled = clientA.Exchange.Project(clientA.Store, clientA.Lookup);
        Check(SyncProtocol.Encode(SyncProtocol.Capture(Row(settled, 0x20UL), 0, 1, 2, 3, 0, false))
            .SequenceEqual(SyncProtocol.Encode(SyncProtocol.Capture(Row(aRows, 0x20UL), 0, 1, 2, 3, 0, false))),
            "B4: sixteen more rounds of the same rows change nothing a client shows");
        uint running = clientA.Exchange.Generation;
        SyncSnapshot straggler = SyncProtocol.Capture(clientA.Row(0x20UL), 0, 1, 2, 3, 0, false);
        straggler.EnemyDamage = 9999;
        straggler.Set(SyncFlags.EnemyDamageObserved, true);
        SquadDecision refused = host.Exchange.OnSnapshot(0x20UL, running - 1u, straggler, 12f);
        Check(!refused.Accepted && refused.Reason.Contains("generation"),
            "B4: a packet still speaking for the previous run is refused, and the reason names both counters");
        List<CombatPlayer> unharmed = host.Exchange.Project(host.Store, host.Lookup);
        Check(Near(Row(unharmed, 0x20UL).Total.EnemyDamage, 600),
            "B4: and the refused packet left the row it described exactly as it was");
        Check(host.Exchange.GenerationOf(0x20UL) == running,
            "B4: and did not disturb the run counter the peer's accepted packets had established");

        // ---------------------------------------------------------------- B5: a peer that restarted
        // The case the test log ends on: a peer is remembered at one run counter and comes back speaking a
        // smaller one - a restart, a hot reload, a rejoin - and the exchange has to recover by itself.
        var wire5 = new Wire();
        var host5 = new Node(0x40UL, 0) { IsMaster = true };
        var restarting = new Node(0x50UL, 1);
        wire5.Add(host5); wire5.Add(restarting);
        host5.Row(0x40UL).Total.AddAccuracy(10, 5, 1); host5.Row(0x40UL).Total.AddEnemyDamage(100);
        restarting.Row(0x50UL).Total.AddAccuracy(8, 4, 0); restarting.Row(0x50UL).Total.AddEnemyDamage(70, true);
        for (int tick = 0; tick < 8; tick++) wire5.Tick(tick * 0.25f);
        for (int run = 0; run < 8; run++) restarting.Exchange.ResetForExpedition();      // it has played eight runs
        for (int tick = 8; tick < 12; tick++) wire5.Tick(tick * 0.25f);
        uint remembered = host5.Exchange.GenerationOf(0x50UL);
        Check(remembered > 1u, "B5: the host has the peer's run counter from the session in progress");
        restarting.Restart();                                                             // it restarts: counter back to one
        restarting.Row(0x50UL).Total.AddEnemyDamage(1234, true);                          // so its row now stands at 70+1234
        const double restartedDamage = 70 + 1234;
        int ticksToRecover = 0;
        for (int tick = 12; tick < 22; tick++)
        {
            wire5.Tick(tick * 0.25f);
            ticksToRecover = tick - 12 + 1;
            if (host5.Exchange.HasSnapshot(0x50UL)
                && Near(Row(host5.Exchange.Project(host5.Store, host5.Lookup), 0x50UL)?.Total.EnemyDamage, restartedDamage)) break;
        }
        Check(ticksToRecover <= 10, "B5: the exchange recovers within ten ticks of a peer coming back on a smaller run counter (took " + ticksToRecover + ")");
        Check(Near(Row(host5.Exchange.Project(host5.Store, host5.Lookup), 0x50UL)?.Total.EnemyDamage, restartedDamage),
            "B5: and what it recovered to is the restarted peer's new row, not a stale one of its own");
        Check(host5.Diag.Exists(line => line.Contains("refused")) && host5.Diag.Exists(line => line.Contains("restarted")),
            "B5: the log shows both the refusals and the moment the peer's counter was adopted");

        // B5-H: when the lower-generation Hello arrives before the restarted peer's first snapshot, clear its old cache row immediately.
        const ulong helloHost = 0x81UL, helloPeer = 0x82UL;
        var helloFirst = new SquadExchange();
        helloFirst.Configure(helloHost, 0, 1, 2, 3);
        helloFirst.SetRoster(new[] { new SquadPeer(helloHost, 0, true, false, true), new SquadPeer(helloPeer, 1, false, false, false) }, true);
        Check(helloFirst.OnHello(helloPeer, 9u, 0f, out _).Accepted, "B5-H: first Hello establishes the peer generation");
        var staleBeforeHello = new SyncSnapshot { Owner = helloPeer, EnemyDamage = 90f };
        staleBeforeHello.Set(SyncFlags.EnemyDamageObserved, true);
        Check(helloFirst.OnSnapshot(helloPeer, 9u, staleBeforeHello, 0.25f).Accepted && helloFirst.HasSnapshot(helloPeer),
            "B5-H: the host has an old peer snapshot before its restart Hello arrives");
        SquadDecision helloRecovery = default;
        for (int attempt = 0; attempt < HandshakePolicy.StaleRepeats; attempt++)
            helloRecovery = helloFirst.OnHello(helloPeer, 1u, 1f + attempt, out _);
        Check(helloRecovery.Accepted && helloFirst.GenerationOf(helloPeer) == 1u && !helloFirst.HasSnapshot(helloPeer),
            "B5-H: recovered Hello clears the cached old-generation row before any new snapshot");
        var freshAfterHello = new SyncSnapshot { Owner = helloPeer, EnemyDamage = 11f };
        freshAfterHello.Set(SyncFlags.EnemyDamageObserved, true);
        Check(helloFirst.OnSnapshot(helloPeer, 1u, freshAfterHello, 4f).Accepted
            && Near(Row(helloFirst.Project(new List<CombatPlayer>(), helloHost), helloPeer)?.Total.EnemyDamage, 11),
            "B5-H: the first snapshot after Hello-first recovery lands on the new row instead of being rejected as stale");
        // The same sequence under the rule as it shipped in 1.16.2, transcribed from that build (the
        // decompiled method is quoted in reports/release-1.17.0.md). It refuses forever, which is exactly the
        // silence the log from the test profile ends on after its `refused a stale snapshot` line.
        var oldRule = new OldGenerationRule();
        oldRule.Accepts(0x50UL, remembered);
        int oldRefusals = 0;
        int oldTicks = 0;
        for (; oldTicks < 30; oldTicks++) { if (!oldRule.Accepts(0x50UL, 1u)) oldRefusals++; else break; }
        var newRule = new HandshakePolicy();
        newRule.AcceptsFrom(0x50UL, remembered, 0f);
        int newRefusals = 0;
        int newTicks = 0;
        for (; newTicks < 30; newTicks++) { if (!newRule.AcceptsFrom(0x50UL, 1u, newTicks * 0.25f)) newRefusals++; else break; }
        Console.WriteLine("B5-EVIDENCE: peer remembered at generation=" + remembered
            + " then returns at generation=1 | old 1.16.2 rule: refusals=" + oldRefusals + " recovered=" + (oldTicks < 30)
            + " over " + oldTicks + " ticks | this build: refusals=" + newRefusals + " recovered=" + (newTicks < 30)
            + " at tick " + (newTicks + 1));
        Check(oldTicks == 30 && newTicks <= 2,
            "B5: the shipped rule never recovers from this and the new one does within three packets, which is the regression this fixes");

        // ---------------------------------------------------------------- B6: the first answer is lost
        // A hello is answered once in 1.16.2, so a single lost answer ends the exchange before it starts. The
        // first answer here is dropped on the wire, and the pair still forms within two retry intervals.
        var wire6 = new Wire { OldGreetingRule = true, OldAnswerOnce = true };
        var host6 = new Node(0x60UL, 0) { IsMaster = true };
        var client6 = new Node(0x70UL, 1);
        wire6.Add(host6); wire6.Add(client6);
        host6.Row(0x60UL).Total.AddAccuracy(10, 5, 1); host6.Row(0x60UL).Total.AddEnemyDamage(100);
        client6.Row(0x70UL).Total.AddAccuracy(8, 4, 0); client6.Row(0x70UL).Total.AddEnemyDamage(70, true);
        wire6.Drop = delivery => delivery == 2;                    // the answer to the very first hello
        for (int tick = 0; tick < 40; tick++) wire6.Tick(tick * 0.25f);
        // The replay runs the 1.16.2 greeting and answer rules on top of this build's sending rule, and this
        // build no longer waits for an answer before sending rows - so the packet the replay drops is no
        // longer necessarily the answer, and the counts below cannot isolate the answer rule any more. What
        // is asserted is the part that still holds, and the point the old rule was here to make is now moot:
        // a single answer used to decide whether the two machines shared anything at all, and it no longer
        // does. The rule-by-rule comparison lives in B5 and B6b, which measure the decision itself.
        Check(wire6.AnswersSent >= 1 && client6.Exchange.HasSnapshot(0x60UL),
            "B6: under the 1.16.2 greeting rules the client is answered and ends up with the host's row, because delivery no longer depends on that answer");
        // The same dropped answer, with the build that re-sends it once the cadence has passed.
        var wire6b = new Wire();
        var host6b = new Node(0x61UL, 0) { IsMaster = true };
        var client6b = new Node(0x71UL, 1);
        wire6b.Add(host6b); wire6b.Add(client6b);
        host6b.Row(0x61UL).Total.AddAccuracy(10, 5, 1); host6b.Row(0x61UL).Total.AddEnemyDamage(100);
        client6b.Row(0x71UL).Total.AddAccuracy(8, 4, 0); client6b.Row(0x71UL).Total.AddEnemyDamage(70, true);
        wire6b.Drop = delivery => delivery == 2;
        int ticksToPair = 0;
        for (int tick = 0; tick < 40; tick++)
        {
            wire6b.Tick(tick * 0.25f);
            ticksToPair = tick + 1;
            if (host6b.Exchange.HasSnapshot(0x71UL) && client6b.Exchange.HasSnapshot(0x61UL)) break;
        }
        Console.WriteLine("B6-EVIDENCE: with the answer to the first hello dropped, the 1.16.2 rules answered "
            + wire6.AnswersSent + " of the " + client6.HellosSeen + " hellos the client saw and left it with "
            + (client6.Exchange.HasSnapshot(0x60UL) ? "the host's row" : "no row of the host's at all")
            + ", while this build answered " + wire6b.AnswersSent + " hellos and paired the two machines in "
            + (ticksToPair * 0.25f).ToString("0.00") + "s");
        Check(wire6b.AnswersSent >= 2,
            "B6: and a re-sent answer means the second hello is answered too, which is the rule that changed");
        Check(ticksToPair * 0.25f <= 2f * HandshakePolicy.RetrySeconds,
            "B6: with the answer re-sent on the cadence the pair is in touch within two retry intervals (took " + (ticksToPair * 0.25f) + "s)");
        Check(Row(client6b.Exchange.Project(client6b.Store, client6b.Lookup), 0x61UL) != null
            && Near(Row(client6b.Exchange.Project(client6b.Store, client6b.Lookup), 0x61UL).Total.EnemyDamage, 100),
            "B6: and the client that had been left unanswered now shows the host's row");

        // ---------------------------------------------------------------- B7: a peer that answers, then goes mute
        // The relay used to be gated on `IsAnswered`, and a peer that answered and then fell silent for longer
        // than `SilenceSeconds` had that flag cleared - so the host quietly stopped sending it rows while the
        // guest could still receive perfectly well. From the guest's screen that is the whole of the failure:
        // it sees its own row and nothing else, for the rest of the run, while both machines are able to talk.
        // The gate is now "has ever answered in this run" (`HandshakePolicy.EverAnswered`).
        var wire7 = new Wire();
        var host7 = new Node(0x70UL, 0) { IsMaster = true };
        var guest7 = new Node(0x71UL, 1);
        wire7.Add(host7); wire7.Add(guest7);
        host7.Row(0x70UL).Total.AddEnemyDamage(400, true);
        guest7.Row(0x71UL).Total.AddEnemyDamage(60, true);
        for (int tick = 0; tick < 40; tick++) wire7.Tick(tick * 0.25f);     // 10 s: the pair is in touch
        int rowsBeforeMute = 0;
        List<CombatPlayer> beforeMute = guest7.Exchange.Project(guest7.Store, guest7.Lookup);
        rowsBeforeMute = beforeMute.Count;
        // Now the guest's own uplink goes dead - every hello and row of its own is lost - while it keeps
        // listening. The host's silence window is 12 s, so this runs well past it.
        guest7.Mute = true;
        for (int tick = 40; tick < 160; tick++) wire7.Tick(tick * 0.25f);
        // The measurement only means something if the host's figure moves after the guest went mute: a cached
        // row would still read 400 under the old rule, so the guest's own row is the control and the host's
        // new figure is the test. The host adds 100 here and the guest has to see 500.
        host7.Row(0x70UL).Total.AddEnemyDamage(100, true);
        for (int tick = 160; tick < 200; tick++) wire7.Tick(tick * 0.25f);
        List<CombatPlayer> afterMute = guest7.Exchange.Project(guest7.Store, guest7.Lookup);
        Check(!guest7.Diag.Exists(line => line.Contains("refused")),
            "B7: while the guest is mute nothing it receives is refused, because it is still listening and still in the squad");
        Check(Row(afterMute, 0x70UL) != null && Near(Row(afterMute, 0x70UL).Total.EnemyDamage, 500),
            "B7: the host's row keeps arriving after 30 s of the guest being unable to say anything, including a figure the host only produced afterwards - which is the relay the old silence rule cut, and a cached row would still read 400");
        Check(afterMute.Count == rowsBeforeMute,
            "B7: the guest's row count does not fall when its own uplink dies");

        // ---------------------------------------------------------------- G: the switches from the last round
        Check(ResultVisibility.Shows(true, true, false, true) == false && ResultVisibility.Shows(true, true, true, false) == false
            && ResultVisibility.Shows(true, true, true, true),
            "G: ShowImpactBoard false keeps the board off the page, and the F8 toggle decides it when it is on");
        Check(ResultVisibility.Shows(true, false, true, true) == false && ResultVisibility.Shows(false, true, true, true) == false,
            "G: the master switch and the overlay switch each hide the board on their own");
        Check(CompletionDrawing.Text(0.99f) && !CompletionDrawing.Text(0.5f) && CompletionDrawing.Icon(0.5f, 0.5f) && !CompletionDrawing.Icon(0.4f, 0.5f),
            "G: the readiness the board waits for is drawn evidence, not elapsed time");
        // ---------------------------------------------------------------- B8: a client sees the bots too
        // The reported symptom: host and client both running this mod, and the client could still see the
        // host and itself and nobody else. The host had measured every seat - bots included - but `HostRows`
        // asked `IsHuman` of the player it was describing, and that is the question for the *recipient* on
        // this channel, never for a bot's own row. A bot carries no plugin and never answers, and a client
        // can measure none of its damage, so a bot's row has exactly one possible source: the host.
        var wire8 = new Wire();
        var host8 = new Node(0x10UL, 0) { IsMaster = true };
        var guest8 = new Node(0x20UL, 1);
        var bot8 = new Node(0x30UL, 2) { IsBot = true, Modded = false };
        wire8.Add(host8); wire8.Add(guest8); wire8.Add(bot8);
        host8.Row(0x10UL).Total.AddEnemyDamage(100, true);
        host8.Row(0x20UL).Total.AddEnemyDamage(200, true);
        host8.Row(0x30UL).Total.AddEnemyDamage(300, true);
        for (int tick = 0; tick < 40; tick++) wire8.Tick(tick * 0.25f);
        List<CombatPlayer> seen8 = guest8.Exchange.Project(guest8.Store, guest8.Lookup);
        Check(Row(seen8, 0x10UL) != null && Row(seen8, 0x20UL) != null,
            "B8: the client sees the host and itself");
        Check(Row(seen8, 0x30UL) != null && Near(Row(seen8, 0x30UL).Total.EnemyDamage, 300),
            "B8: and it sees the bot's row as the host measured it - a seat no client can measure for itself, and the row the host never used to put on the wire");
        Check(!guest8.Diag.Exists(line => line.Contains("refused")),
            "B8: and every one of those rows was taken, not refused");
        // ---------------------------------------------------------------- B9: a human leaves and a bot takes the same seat
        const ulong turnoverHostId = 0x90UL, turnoverLocalId = 0x91UL, departedHumanId = 0x92UL;
        const ulong remainingHumanId = 0x93UL, replacementBotId = 1UL;
        var turnoverStore = new CombatStatsStore();
        turnoverStore.GetOrCreate(turnoverHostId).DisplaySlot = 0;
        turnoverStore.GetOrCreate(turnoverLocalId).DisplaySlot = 1;
        turnoverStore.GetOrCreate(departedHumanId).DisplaySlot = 2;
        turnoverStore.GetOrCreate(remainingHumanId).DisplaySlot = 3;
        turnoverStore.GetOrCreate(replacementBotId).DisplaySlot = 2;
        int prunedRows = turnoverStore.PruneExcept(new HashSet<ulong>
            { turnoverHostId, turnoverLocalId, remainingHumanId, replacementBotId });
        bool botStoreRowKept = turnoverStore.TryGet(replacementBotId, out CombatPlayer botStoreRow);
        Check(prunedRows == 1 && !turnoverStore.TryGet(departedHumanId, out _)
            && botStoreRowKept && botStoreRow.DisplaySlot == 2,
            "B9: pruning the live roster removes the departed human row but keeps the bot now occupying its slot");

        var turnoverExchange = new SquadExchange();
        turnoverExchange.Configure(turnoverLocalId, 0, 1, 2, 3);
        var turnoverHostPeer = new SquadPeer(turnoverHostId, 0, false, false, true);
        var turnoverLocalPeer = new SquadPeer(turnoverLocalId, 1, true, false, false);
        var departedHumanPeer = new SquadPeer(departedHumanId, 2, false, false, false);
        var remainingHumanPeer = new SquadPeer(remainingHumanId, 3, false, false, false);
        turnoverExchange.SetRoster(new[] { turnoverHostPeer, turnoverLocalPeer, departedHumanPeer, remainingHumanPeer }, false);

        var turnoverHostRow = new CombatPlayer(turnoverHostId) { Name = "Host", DisplaySlot = 0 };
        var departedHumanRow = new CombatPlayer(departedHumanId) { Name = "Departed", DisplaySlot = 2 };
        var remainingHumanRow = new CombatPlayer(remainingHumanId) { Name = "StillHere", DisplaySlot = 3 };
        var replacementBotRow = new CombatPlayer(replacementBotId) { Name = "Bishop", DisplaySlot = 2 };
        turnoverHostRow.Total.AddEnemyDamage(100);
        departedHumanRow.Total.AddEnemyDamage(200);
        remainingHumanRow.Total.AddEnemyDamage(300);
        replacementBotRow.Total.AddEnemyDamage(400);
        SyncSnapshot turnoverHostSnapshot = SyncProtocol.Capture(turnoverHostRow, 0, 1, 2, 3, 1000, true);
        SyncSnapshot departedHumanSnapshot = SyncProtocol.Capture(departedHumanRow, 0, 1, 2, 3, 1000, true);
        SyncSnapshot remainingHumanSnapshot = SyncProtocol.Capture(remainingHumanRow, 0, 1, 2, 3, 1000, true);
        Check(turnoverExchange.OnSnapshot(turnoverHostId, 1, turnoverHostSnapshot, 1f).Accepted
            && turnoverExchange.OnSnapshot(turnoverHostId, 1, departedHumanSnapshot, 1f).Accepted
            && turnoverExchange.OnSnapshot(turnoverHostId, 1, remainingHumanSnapshot, 1f).Accepted,
            "B9: the host's rows are accepted while all described players are in the roster");

        var replacementBotPeer = new SquadPeer(replacementBotId, 2, false, true, false);
        turnoverExchange.SetRoster(new[] { turnoverHostPeer, turnoverLocalPeer, replacementBotPeer, remainingHumanPeer }, false);
        Check(!turnoverExchange.HasSnapshot(departedHumanId) && turnoverExchange.HasSnapshot(remainingHumanId),
            "B9: replacing the human with a bot evicts only the departed identity's cached snapshot");
        SquadDecision lateDepartedRow = turnoverExchange.OnSnapshot(turnoverHostId, 1, departedHumanSnapshot, 2f);
        Check(!lateDepartedRow.Accepted && !turnoverExchange.HasSnapshot(departedHumanId),
            "B9: a delayed host snapshot cannot resurrect a row for a player no longer in the roster");
        SyncSnapshot replacementBotSnapshot = SyncProtocol.Capture(replacementBotRow, 0, 1, 2, 3, 1000, true);
        Check(turnoverExchange.OnSnapshot(turnoverHostId, 1, replacementBotSnapshot, 2f).Accepted,
            "B9: the current bot's host-measured row is accepted for the replacement seat");

        var turnoverLocalRow = new CombatPlayer(turnoverLocalId) { Name = "Local", DisplaySlot = 1 };
        List<CombatPlayer> turnoverProjection = turnoverExchange.Project(
            new List<CombatPlayer> { turnoverHostRow, turnoverLocalRow, remainingHumanRow, replacementBotRow }, turnoverLocalId);
        List<CombatPlayer> visibleTurnoverRows = turnoverProjection.OrderBy(row => row.DisplaySlot).Take(4).ToList();
        Check(visibleTurnoverRows.Count == 4 && Row(visibleTurnoverRows, departedHumanId) == null
            && Row(visibleTurnoverRows, replacementBotId) != null && Row(visibleTurnoverRows, remainingHumanId) != null,
            "B9: the four live rows include the replacement bot and remaining guest, with no stale fifth identity pushing one out");
        // ---------------------------------------------------------------- B10: source provenance survives repeated equal-rank rows
        // Host health-loss measurements for remote players and the owner's partial estimate can both be
        // estimated. A merged row must remember which original source won each field across later packets.
        const ulong provenanceHostId = 0xB0UL, provenancePeerId = 0xB1UL, provenanceLocalId = 0xB2UL;
        var provenance = new SquadExchange();
        provenance.Configure(provenanceLocalId, 0, 1, 2, 3);
        provenance.SetRoster(new[]
        {
            new SquadPeer(provenanceHostId, 0, false, false, true),
            new SquadPeer(provenancePeerId, 1, false, false, false),
            new SquadPeer(provenanceLocalId, 2, true, false, false)
        }, false);
        var provenanceHostRow = new SyncSnapshot { Owner = provenancePeerId, EnemyDamage = 125f };
        provenanceHostRow.Set(SyncFlags.EnemyDamageObserved, true);
        provenanceHostRow.Set(SyncFlags.EnemyDamageEstimated, true);
        var provenancePeerRow = new SyncSnapshot { Owner = provenancePeerId, EnemyDamage = 0f };
        provenancePeerRow.Set(SyncFlags.EnemyDamageObserved, true);
        provenancePeerRow.Set(SyncFlags.EnemyDamageEstimated, true);
        bool provenancePacketsAccepted = provenance.OnSnapshot(provenanceHostId, 1, provenanceHostRow, 1f).Accepted
            && provenance.OnSnapshot(provenancePeerId, 1, provenancePeerRow, 1.1f).Accepted;
        for (int update = 0; update < 4; update++)
            provenancePacketsAccepted &= provenance.OnSnapshot(provenancePeerId, 1, provenancePeerRow, 1.2f + update * 0.1f).Accepted;
        CombatPlayer provenanceDisplay = Row(provenance.Project(Array.Empty<CombatPlayer>(), provenanceLocalId), provenancePeerId);
        Check(provenancePacketsAccepted && provenanceDisplay != null && Near(provenanceDisplay.Total.EnemyDamage, 125)
            && provenanceDisplay.Total.EnemyDamageObserved && provenanceDisplay.Total.EnemyDamageEstimated,
            "B10: repeated equal-rank zero peer rows cannot erase the host's retained damage field");
    }

    private static bool Near(double? value, double expected) => value.HasValue && Math.Abs(value.Value - expected) < 0.01;

    private static CombatPlayer Row(List<CombatPlayer> rows, ulong lookup)
    {
        foreach (CombatPlayer row in rows) if (row.Lookup == lookup) return row;
        return null;
    }

    // One machine in the scenario. `Modded` is false for the vanilla host: it has no plugin, so it is handed
    // every packet and does nothing with any of them, which is exactly what it does in the real lobby.
    // `Allowlist` is what that machine's operator has written into `[Squad Sync] AllowlistedPeers`: it is
    // empty unless a scenario says otherwise, so a machine here shares with nobody until somebody speaks
    // first - the same default the shipped build has.
    private sealed class Node
    {
        public readonly ulong Lookup;
        public readonly int Slot;
        public bool IsMaster;
        public bool IsBot;
        public bool Modded = true;
        // Whose lookups this machine has named in its own configuration. The wire fills this in for a
        // scenario that models a lobby whose players know each other, and leaves it empty for the ones that
        // model two strangers sharing a vanilla host: a machine named here is spoken to before it has said
        // anything, and one that is not has to speak first.
        public readonly List<ulong> Allowlist = new List<ulong>();
        // Set when the scenario is about a machine that names nobody - the shipped default for a fresh
        // install, and the case a vanilla host has to be protected from.
        public bool Isolated;
        // The protocol this machine puts on the wire. It is this build's own unless a scenario is modelling a
        // peer that has not been updated yet.
        public uint Protocol = SyncProtocol.Version;
        // This machine receives but cannot be heard: its own uplink is down, or its packets are being dropped
        // on the way out. It is the case the relay rule is about - a peer that has proved it runs this mod and
        // can still receive must keep being sent to.
        public bool Mute;
        public int HellosSeen, SnapshotsSeen;
        public SquadExchange Exchange;
        public readonly List<CombatPlayer> Store = new List<CombatPlayer>();
        public readonly List<string> Diag = new List<string>();

        public Node(ulong lookup, int slot)
        {
            Lookup = lookup; Slot = slot;
            Restart();
        }

        // A fresh session on the same seat: a new exchange, back at the first run counter, which is what a
        // restart, a hot reload or a rejoin does.
        public void Restart()
        {
            Exchange = new SquadExchange();
            Exchange.Report = Diag.Add;
            Exchange.Configure(Lookup, 0, 1, 2, 3);
        }

        public CombatPlayer Row(ulong owner)
        {
            foreach (CombatPlayer row in Store) if (row.Lookup == owner) return row;
            var made = new CombatPlayer(owner) { DisplaySlot = Slot };
            Store.Add(made);
            return made;
        }
    }

    // The wire between the nodes: it rebuilds each machine's roster from the same slot list the game would
    // give it, asks each of them what they want sent, and delivers. A drop rule can throw away any packet by
    // its delivery number, which is how a lost hello, a lost answer and a lossy link are reproduced.
    private sealed class Wire
    {
        public readonly List<Node> Nodes = new List<Node>();
        public Func<int, bool> Drop;
        // The two rules of the previous release, for the two faults this round fixes: only the host greeted,
        // and a hello was answered exactly once.
        public bool OldGreetingRule;
        public bool OldAnswerOnce;

        private readonly List<KeyValuePair<Node, SquadSend>> _flight = new List<KeyValuePair<Node, SquadSend>>();
        private readonly HashSet<string> _answeredOnce = new HashSet<string>();
        private int _delivery;

        // How many hellos were answered at all. It is the measurement of the answer rule: a hello that is
        // answered exactly once can only ever produce one answer, and one re-sent on the cadence produces one
        // per hello - which is what a lost answer needs in order to be repaired.
        public int AnswersSent;

        public void Add(Node node) => Nodes.Add(node);

        public void Tick(float now)
        {
            NameEachOtherOnce();
            foreach (Node node in Nodes)
            {
                if (!node.Modded) continue;
                node.Exchange.Configure(node.Lookup, 0, 1, 2, 3);
                node.Exchange.SetRoster(RosterFor(node), node.IsMaster);
                if (node.Mute) continue;
                foreach (SquadSend send in node.Exchange.Tick(now, node.Store))
                {
                    if (OldGreetingRule && send.Kind == SquadSendKind.Hello && !node.IsMaster) continue;
                    _flight.Add(new KeyValuePair<Node, SquadSend>(node, send));
                }
            }
            Drain(now);
        }

        // What an operator does once, in the configuration file: name the players known to run this build.
        // Every scenario that is not about a machine naming nobody is handed the other modded seats, so the
        // exchange has the evidence a real lobby would have. A vanilla host is never named here - it runs no
        // mod, so nobody could have seen it run one - and that is what keeps it out of every send below.
        //
        // Re-applied every tick rather than once, because a peer that restarts gets a fresh exchange, and the
        // list is this machine's own file: it outlives the session and has to reach the new exchange too.
        private void NameEachOtherOnce()
        {
            var modded = new List<ulong>();
            foreach (Node node in Nodes) if (node.Modded) modded.Add(node.Lookup);
            foreach (Node node in Nodes)
            {
                if (!node.Modded) continue;
                node.Allowlist.Clear();
                if (!node.Isolated)
                    foreach (ulong lookup in modded) if (lookup != node.Lookup) node.Allowlist.Add(lookup);
                node.Exchange.SetAllowlist(node.Allowlist);
            }
        }

        private SquadPeer[] RosterFor(Node self)
        {
            var peers = new List<SquadPeer>();
            foreach (Node node in Nodes) peers.Add(new SquadPeer(node.Lookup, node.Slot, node == self, node.IsBot, node.IsMaster));
            return peers.ToArray();
        }

        private void Drain(float now)
        {
            int guard = 0;
            while (_flight.Count > 0 && guard++ < 512)
            {
                KeyValuePair<Node, SquadSend> queued = _flight[0];
                _flight.RemoveAt(0);
                _delivery++;
                if (Drop != null && Drop(_delivery)) continue;
                Node from = queued.Key;
                SquadSend send = queued.Value;
                Node to = Find(send.Target);
                if (to == null) continue;
                if (!to.Modded)
                {
                    // A vanilla machine: the bytes arrive and nothing speaks this protocol on the other end.
                    if (send.Kind == SquadSendKind.Hello) to.HellosSeen++;
                    else to.SnapshotsSeen++;
                    continue;
                }
                // Every scenario traverses the shipped parser, queue and dispatch, not a shortcut
                // that injects a known-good sender straight into the exchange.
                SquadNet.OnHello = (sender, generation, owner) =>
                {
                    to.HellosSeen++;
                    if (OldAnswerOnce && !_answeredOnce.Add(to.Lookup + "|" + sender)) return;
                    to.Exchange.OnHello(sender, generation, now, out bool answer);
                    if (!answer) return;
                    AnswersSent++;
                    _flight.Add(new KeyValuePair<Node, SquadSend>(to,
                        new SquadSend(SquadSendKind.Hello, sender, default, to.Exchange.Generation)));
                };
                SquadNet.OnSnapshot = (sender, generation, snapshot) =>
                {
                    to.SnapshotsSeen++;
                    to.Exchange.OnSnapshot(sender, generation, snapshot, now);
                };
                byte[] bytes = send.Kind == SquadSendKind.Hello
                    ? SquadPackets.Hello(from.Lookup, send.Generation, SyncProtocol.Version)
                    : SquadPackets.Snapshot(send.Snapshot, send.Generation, SyncProtocol.Version);
                if (!SquadNet.TryAccept(bytes, (uint)bytes.Length, from.Lookup))
                    throw new InvalidOperationException("scenario packet not claimed by production transport");
                SquadNet.Drain();
            }
        }

        private Node Find(ulong lookup)
        {
            foreach (Node node in Nodes) if (node.Lookup == lookup) return node;
            return null;
        }
    }

    // The run-counter rule exactly as it shipped in 1.16.2, transcribed from that build: the first packet
    // adopts a sender's counter, a newer one supersedes it, and an older one is refused with no path back.
    private sealed class OldGenerationRule
    {
        private readonly Dictionary<ulong, uint> _seen = new Dictionary<ulong, uint>();

        public bool Accepts(ulong peer, uint generation)
        {
            if (!_seen.TryGetValue(peer, out uint current)) { _seen[peer] = generation; return true; }
            if (current == generation) return true;
            if (generation != current && unchecked(generation - current) < 0x80000000u) { _seen[peer] = generation; return true; }
            return false;
        }
    }
}
