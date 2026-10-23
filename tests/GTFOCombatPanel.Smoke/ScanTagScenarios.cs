using System;
using System.IO;
using HawChow.GTFOCombatPanel;

internal static partial class Program
{
    private static void ScanTagScenarios()
    {
        var ledger = new ScanTagLedger();
        var enemy = new IntPtr(100);
        Check(ledger.Confirm(enemy, 0, 0) == 0, "passive radar/unsourced confirmation cannot manufacture a scan");
        ledger.Request(enemy, 11, 1);
        Check(ledger.Confirm(enemy, 0, 2) == 11, "actual request survives empty later candidate lists and late confirmation");
        Check(ledger.Confirm(enemy, 0, 2) == 0, "duplicate late confirmation cannot consume the request twice");
        ledger.Request(enemy, 11, 5);
        Check(ledger.Confirm(enemy, 0, 4) == 0 && ledger.Confirm(enemy, 0, 6) == 11,
            "a confirmation preceding the local request cannot be attributed to that request");
        ledger.Request(enemy, 11, 10);
        Check(ledger.Confirm(enemy, 0, 21) == 0, "expired requests cannot credit unrelated future marks");
        ledger.Request(enemy, 11, 30); ledger.Clear();
        Check(ledger.Confirm(enemy, 0, 31) == 0, "expedition reset clears pending local attribution");
        Check(ledger.Confirm(enemy, 22, 32) == 22, "host accepts the native validation owner without a remote scanner instance");
        Check(ledger.Confirm(IntPtr.Zero, 22, 32) == 0 && ledger.Confirm(enemy, 22, double.NaN) == 0,
            "invalid native identity/time cannot create a scan");
        ledger.Request(IntPtr.Zero, 11, 33); ledger.Request(enemy, 0, 33);
        Check(ledger.Confirm(enemy, 0, 34) == 0, "unowned requests are rejected");
        ledger.Request(enemy, 11, 40, 1001);
        Check(ledger.Confirm(enemy, 0, 41, 1002) == 0, "reused native address with a different Unity instance cannot consume an old request");
        Check(ledger.Confirm(enemy, 22, 41, 1002) == 22 && ledger.Confirm(enemy, 0, 42, 1001) == 0,
            "authoritative confirmation consumes stale pending attribution too");
        ledger.Request(enemy, 11, 50, 1001);
        Check(ledger.Confirm(enemy, 0, 60, 1001) == 11, "request lifetime boundary is accepted, later timestamps expire");
        var a = new ScanSession(); var b = new ScanSession();
        var ac = new ImpactCounters(); var bc = new ImpactCounters();
        a.ObserveConfirmed(100, ac, true); b.ObserveConfirmed(100, bc, true);
        a.ObserveConfirmed(100, ac, true);
        Check(ac.ScanFirstMarks == 1 && bc.ScanFirstMarks == 1 && ac.ScanMarks == 1,
            "two scanners retain independent first marks while duplicate execution stays deduplicated");
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string tracker = File.ReadAllText(Path.Combine(root, "CombatTracker.cs"));
        string patches = File.ReadAllText(Path.Combine(root, "RemoteScanPatches.cs"));
        Check(!tracker.Contains("SweepCandidates") && !tracker.Contains("LocalSweepOwner") && !tracker.Contains("CreditRemoteScanTag"),
            "production no longer guesses confirmed ownership from overwritten radar candidates or remote scanner casts");
        Check(patches.Contains("\"WantToTagEnemy\"") && patches.Contains("\"DoTagEnemyValidation\"") && patches.Contains("\"DoTagEnemy\""),
            "production observes native request validation and execution stages");
        Check(tracker.Contains("ScanTagPipeline.Drain();") && tracker.Contains("ScanTagPipeline.Reset();"),
            "production drains on the main thread and resets the scan pipeline per expedition");
    }
}
