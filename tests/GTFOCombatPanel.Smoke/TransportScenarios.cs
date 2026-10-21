using System;
using System.Collections.Generic;
using System.Linq;
using HawChow.GTFOCombatPanel;

internal static partial class Program
{
    private static void DeliverTransport(byte[] bytes, ulong sender)
    {
        Check(SquadNet.TryAccept(bytes, (uint)bytes.Length, sender), "T: production receive claims panel bytes");
        SquadNet.Drain();
        Check(SquadNet.QueuedCount == 0, "T: production queue drains on main thread");
    }

    private static void RunTransportScenarios()
    {
        const ulong host = 0x10, local = 0x20, other = 0x30, outsider = 0x99;
        var exchange = new SquadExchange();
        exchange.Configure(local, 0, 1, 2, 3);
        exchange.SetRoster(new[] { new SquadPeer(host, 0, false, false, true),
            new SquadPeer(local, 1, true, false, false), new SquadPeer(other, 2, false, false, false) }, false);
        var measured = new List<CombatPlayer> { new CombatPlayer(local) };
        measured[0].Total.AddAccuracy(55, 40, 10);
        var decisions = new List<SquadDecision>();
        ulong helloSender = 0, helloOwner = 0, mismatchSender = 0;
        float now = 1f;
        SquadNet.OnHello = (sender, generation, owner) =>
        {
            helloSender = sender; helloOwner = owner;
            decisions.Add(exchange.OnHello(sender, generation, now, out _));
        };
        SquadNet.OnSnapshot = (sender, generation, row) => decisions.Add(exchange.OnSnapshot(sender, generation, row, now));
        SquadNet.OnProtocolMismatch = (sender, version) => mismatchSender = sender;
        DeliverTransport(SquadPackets.Hello(host, 9, SyncProtocol.Version), host);
        Check(helloSender == host && helloOwner == host && decisions.Last().Accepted, "T: hello retains transport identity and declared owner");
        var row = new SyncSnapshot { Owner = other, EnemyDamage = 250, Shots = 20, Hits = 15 };
        row.Set(SyncFlags.EnemyDamageObserved, true);
        row.Set(SyncFlags.AccuracyObserved, true);
        DeliverTransport(SquadPackets.Snapshot(row, 9, SyncProtocol.Version), host);
        Check(decisions.Last().Accepted && decisions.Last().Peer == host,
            "T: real receive -> queue -> callback preserves host sender, not zero or row owner");
        foreach (ulong owner in new[] { host, local })
        {
            row.Owner = owner; row.EnemyDamage = owner == host ? 300 : 600;
            DeliverTransport(SquadPackets.Snapshot(row, 9, SyncProtocol.Version), host);
        }
        var projected = exchange.Project(measured, local);
        Check(projected.All(p => CombatStatsFormatter.Fields(p.Total, PanelColumn.Projectiles, null).Length > 0),
            "T: every received player has renderable in-level cells");
        var inputs = projected.Select(p => ImpactInput.From("peer-" + p.Lookup, p.Total, p.Impact, p.SupportRole, null, null)).ToList();
        foreach (bool succeeded in new[] { false, true })
        {
            var report = ImpactScoring.Evaluate(inputs, ImpactScoring.DefaultWeights, 1, 1, succeeded);
            var board = ImpactBoard.Build(report, true, succeeded);
            using var json = System.Text.Json.JsonDocument.Parse(ResultsLog.Build(succeeded, new DateTime(2026, 10, 17), "regression", board, report));
            Check(report.Entries.Count == 3 && board.ColumnCount == 4 && json.RootElement.GetProperty("players").GetArrayLength() == 3,
                "T: received squad projection reaches " + (succeeded ? "success" : "failure") + " board and JSON archive");
        }
        Check(projected.Count == 3 && Near(Row(projected, other).Total.EnemyDamage, 250), "T: host's three rows survive actual receive queue");
        Check(Row(projected, local).Total.ShotsFired == 55 && Near(Row(projected, local).Total.EnemyDamage, 600), "T: local exact aim survives host damage projection");
        for (int i = 0; i < 5; i++) DeliverTransport(SquadPackets.Snapshot(row, 9, SyncProtocol.Version), host);
        Check(Near(Row(exchange.Project(measured, local), local).Total.EnemyDamage, 600) && measured[0].Total.EnemyDamage == 0,
            "T: repeated transport snapshots neither accumulate nor mutate capture store");
        row.Owner = host;
        DeliverTransport(SquadPackets.Snapshot(row, 9, SyncProtocol.Version), other);
        Check(!decisions.Last().Accepted, "T: client cannot impersonate host by describing host row");
        DeliverTransport(SquadPackets.Snapshot(row, 9, SyncProtocol.Version), outsider);
        Check(!decisions.Last().Accepted, "T: non-roster transport sender rejected");
        DeliverTransport(SquadPackets.Snapshot(row, 9, SyncProtocol.Version), 0);
        Check(!decisions.Last().Accepted, "T: unknown transport sender is not guessed from row owner");
        DeliverTransport(SquadPackets.Snapshot(row, 8, SyncProtocol.Version), host);
        Check(!decisions.Last().Accepted, "T: old in-flight generation rejected through receive path");
        exchange.ResetForExpedition(); now = 3;
        DeliverTransport(SquadPackets.Snapshot(row, 10, SyncProtocol.Version), host);
        Check(decisions.Last().Accepted && exchange.GenerationOf(host) == 10 && exchange.Generation != 10,
            "T: independent client and host run counters need not be equal in next expedition");
        now = 4;
        DeliverTransport(SquadPackets.Snapshot(row, 1, SyncProtocol.Version), host);
        now = 5;
        DeliverTransport(SquadPackets.Snapshot(row, 1, SyncProtocol.Version), host);
        now = 6;
        DeliverTransport(SquadPackets.Snapshot(row, 1, SyncProtocol.Version), host);
        Check(decisions.Last().Accepted && exchange.GenerationOf(host) == 1, "T: restarted host generation recovery crosses actual queue");
        DeliverTransport(SquadPackets.Hello(host, 1, SyncProtocol.Version + 1), host);
        Check(mismatchSender == host, "T: protocol mismatch diagnostic keeps actual sender");
        DeliverTransport(SquadPackets.Hello(other, 1, SyncProtocol.Version), host);
        Check(helloSender == host && helloOwner == other, "T: hello claim never overwrites transport sender");

        // Host receives exact client aim through the same production path.
        var hostExchange = new SquadExchange();
        hostExchange.Configure(host, 0, 1, 2, 3);
        hostExchange.SetRoster(new[] { new SquadPeer(host, 0, true, false, true), new SquadPeer(local, 1, false, false, false) }, true);
        SquadNet.OnSnapshot = (sender, generation, snapshot) => decisions.Add(hostExchange.OnSnapshot(sender, generation, snapshot, now));
        row = SyncProtocol.Capture(measured[0], 0, 1, 2, 3, 0, false);
        DeliverTransport(SquadPackets.Snapshot(row, 2, SyncProtocol.Version), local);
        Check(decisions.Last().Accepted && Row(hostExchange.Project(new List<CombatPlayer>(), host), local).Total.Hits == 40,
            "T: client own exact aim reaches host cache via production transport");
        SquadNet.OnHello = null; SquadNet.OnSnapshot = null; SquadNet.OnProtocolMismatch = null;
        Console.WriteLine("TRANSPORT-EVIDENCE: production SquadNet.TryAccept -> queue -> Drain -> SquadExchange verified (managed boundary doubles; no native/Steam claim).");
    }
}
