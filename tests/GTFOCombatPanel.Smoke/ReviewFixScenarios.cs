using System;
using System.IO;
using HawChow.GTFOCombatPanel;

internal static partial class Program
{
    private static void RunReviewFixScenarios()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "GTFOCombatPanel.csproj"))) root = root.Parent;
        Check(root != null, "review regression locates production source");
        string tracker = File.ReadAllText(Path.Combine(root.FullName, "CombatTracker.cs"));
        Check(tracker.Contains("if (SNet.IsMaster && alive == 1 && !_loneSurvivorAwarded"),
            "only authority may award lone survivor; client-local alive count cannot represent the squad");
        string sync = File.ReadAllText(Path.Combine(root.FullName, "CombatSync.cs"));
        Check(sync.Contains("ReportedSync.Count >= 256 || !ReportedSync.Add(text)") && sync.Contains("ReportedSync.Clear()")
            && !sync.Contains("Plugin.Logger.LogInfo($\"[DIAG] squad sync row:"), "runtime packet diagnostics are bounded and deduplicated per expedition");
        string scout = File.ReadAllText(Path.Combine(root.FullName, "ImpactPatches.cs"));
        Check(scout.Contains("typeof(ES_ScoutScream), \"DoStartScream\"") && !scout.Contains("typeof(ES_ScoutScream), \"ActivateState\""),
            "Scout charge-up activation is not the actual scream witness");
        string supply = File.ReadAllText(Path.Combine(root.FullName, "SupplyTracker.cs"));
        Check(supply.Contains("SNet.IsMaster && ScanReceiveScopePatch.Depth > 0"), "stale last sender outside receive dispatch cannot supply a pack giver");
        var cache = new SettlementRows();
        var old = new CombatPlayer(301);
        old.Impact.MinorKills = 4; old.Impact.SleepingMinorKills = 2; old.Impact.UnknownStateMinorKills = 1;
        cache.Update(new[] { old }, 1);
        var next = cache.Update(new[] { new CombatPlayer(301) }, 1)[0];
        Check(next.Impact.MinorKills == 4 && next.Impact.SleepingMinorKills == 2 && next.Impact.UnknownStateMinorKills == 1,
            "settlement retains legacy kill subsets with their total as one group");
    }
}
