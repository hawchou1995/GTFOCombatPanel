using System;
using System.IO;
using HawChow.GTFOCombatPanel;

internal static partial class Program
{
    private static void RunClientReviveScenarios()
    {
        const ulong target = 0x5101, local = 0x5102, remote = 0x5103;
        var ledger = new ReviveAttributionLedger();
        Check(ledger.Observe(target, local, true, 1), "local rescuer can be paired with a remote target");
        Check(ledger.Observe(target, local, false, 2), "local rescue release retains its completion grace");
        Check(ledger.TryTakeSuccessfulRevive(target, 2.1, out ulong rescuer) && rescuer == local,
            "remote target completion returns the observed local rescuer");
        Check(!ledger.TryTakeSuccessfulRevive(target, 2.2, out _), "duplicate completion cannot credit twice");
        ledger.Observe(target, local, true, 10);
        ledger.Observe(target, remote, true, 11);
        ledger.Observe(target, local, false, 12);
        Check(ledger.TryTakeSuccessfulRevive(target, 13, out rescuer) && rescuer == remote,
            "remote takeover replaces the local candidate; old local release cannot reclaim credit");
        Check(!ReviveAttributionLedger.CanTrackOwner(true, false, false), "client does not own a remote rescuer row");
        Check(ReviveAttributionLedger.CanTrackOwner(true, false, true)
            && ReviveAttributionLedger.CanTrackOwner(true, true, false), "local and host rescuer ownership stays unchanged");
        ledger.Observe(target, local, true, 20);
        Check(!ledger.TryTakeSuccessfulRevive(target, 41, out _), "expired attempt cannot receive late completion credit");
        ledger.Observe(target, local, true, 50);
        ledger.Observe(target, local, false, 51);
        Check(!ledger.TryTakeSuccessfulRevive(target, 55, out _), "cancelled attempt without timely success expires");
        Check(!ledger.TryTakeSuccessfulRevive(target, 56, out _), "completion without state credits nobody");
        ledger.Observe(target, local, true, 60);
        ledger.Clear();
        Check(!ledger.TryTakeSuccessfulRevive(target, 61, out _), "new expedition clears pending attribution");
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "CombatTracker.cs")))
            root = Directory.GetParent(root)?.FullName;
        Check(root != null, "revive wiring source root found");
        if (root == null) return;
        string tracker = File.ReadAllText(Path.Combine(root, "CombatTracker.cs"));
        int start = tracker.IndexOf("internal static void ObserveReviveState(", StringComparison.Ordinal);
        int confirm = tracker.IndexOf("internal static void ConfirmRevive(", StringComparison.Ordinal);
        int end = tracker.IndexOf("// Everything the board scores", confirm, StringComparison.Ordinal);
        string observe = tracker.Substring(start, confirm - start);
        string complete = tracker.Substring(confirm, end - confirm);
        Check(observe.Contains("if (!targetOwner.IsInSlot || !sourceOwner.IsInSlot) return;")
            && !observe.Contains("if (!ShouldTrack(sourceOwner)) return;"),
            "static wiring: remote takeover reaches the production ledger, not just the test");
        Check(complete.Contains("if (target == null || !target.IsInSlot) return;")
            && !complete.Contains("ShouldTrack(target)"),
            "static wiring: production completion accepts remote targets");
        Check(complete.IndexOf("TryTakeSuccessfulRevive", StringComparison.Ordinal)
                < complete.IndexOf("if (!ShouldTrack(reviver)) return;", StringComparison.Ordinal)
            && complete.IndexOf("if (!ShouldTrack(reviver)) return;", StringComparison.Ordinal)
                < complete.IndexOf("CreditRevive()", StringComparison.Ordinal),
            "static wiring: matched completion and rescuer ownership precede the one credit call");
        Console.WriteLine("CLIENT_REVIVE: PASS (production ledger scenarios plus static Unity wiring; not a live-game test)");
    }
}
