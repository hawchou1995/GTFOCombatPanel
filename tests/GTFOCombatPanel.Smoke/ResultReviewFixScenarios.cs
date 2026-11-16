using System;
using System.IO;
using HawChow.GTFOCombatPanel;

internal static partial class Program
{
    private static void RunResultReviewFixScenarios()
    {
        var gate = new ResultClientWallGate();
        Check(!gate.Observe(1, true, true, 500f, 1) && !float.IsFinite(gate.Wall),
            "review: active-unmeasurable controls cannot poison retained wall with a high transient reading");
        Check(!gate.Observe(2, false, true, 510f) && !float.IsFinite(gate.Wall),
            "review: incomplete scan cannot write retained wall");
        Check(!gate.Observe(3, true, true, 1207f) && gate.Observe(4, true, true, 1207f)
            && near(gate.Wall, 1207f), "review: trustworthy text wall recovers after incomplete high samples");
        Check(!gate.Observe(5, true, true, 1237f) && near(gate.Wall, 1207f),
            "review: a moving footer does not relax its protection on one sample");
        Check(gate.Observe(6, true, true, 1237f) && near(gate.Wall, 1237f),
            "review: stable final footer releases the stale early-animation edge");
        Check(!gate.Observe(7, true, true, 1177f) && near(gate.Wall, 1177f),
            "review: a trustworthy late control tightens protection immediately");
        Check(!gate.Observe(8, true, false, 400f) && near(gate.Wall, 1177f),
            "review: uncovered action parent cannot poison an already placed wall");
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "ResultsDisplay.cs"))) root = Directory.GetParent(root)?.FullName;
        Check(root != null, "review wiring root found");
        if (root == null) return;
        string display = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        int begin = display.IndexOf("private static bool PlaceFixedLayout(", StringComparison.Ordinal);
        int end = display.IndexOf("private static PanelGrid FixedGrid(", begin, StringComparison.Ordinal);
        string place = display.Substring(begin, end - begin);
        Check(place.Contains("_placed = true;") && place.Contains("_placedSucceeded = succeeded;"),
            "review static wiring: fixed placement enters normal data refresh rather than a dead branch");
        begin = display.IndexOf("private static bool FallbackRecoveryDue(", StringComparison.Ordinal);
        end = display.IndexOf("private static bool AnchorReadable(", begin, StringComparison.Ordinal);
        Check(display.Substring(begin, end - begin).Contains("if (_fixedPlaced) return false;"),
            "review static wiring: fixed placement is never re-anchored by adaptive recovery");
        Check(display.Contains("GuestClient() && !succeeded && !Forced && !_fallbackPlaced && !ClientWall.Settled"),
            "review static wiring: refusal budget cannot bypass the first client-wall stability gate");
        begin = display.IndexOf("private static void AbandonFixedLayout()", StringComparison.Ordinal);
        end = display.IndexOf("private static ResultFixedDrawPlan FixedLayoutDecision(", begin, StringComparison.Ordinal);
        Check(end > begin && display.Substring(begin, end - begin).Contains("Retry.Forget();"),
            "review static wiring: dropping a fixed grid resets the retry cadence instead of inheriting the 30-frame one");
        Console.WriteLine("RESULT_REVIEW_FIXES: PASS (production wall gate and static Unity wiring; no live rendering claim)");
    }
}
