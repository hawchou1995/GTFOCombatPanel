using System;
using System.IO;
using HawChow.GTFOCombatPanel;

// 1.19.62 (unreleased): a second sweep of enemies the squad already knows is still the second batch.
//
// This is the integration regression for the one rule a mark-count reading of the estimate would get wrong: the
// host's batch figure is neither the first-mark figure nor the cumulative figure. A guest's second sweep marks only
// old enemies, both visible totals stay exactly where they were, and the batch figure must still rise by one.
//
// Nothing here calls the ledger with a hand-made timestamp and no enemy identity. The marks come out of the
// production `ScanSession` - the same `BeginConfirmWindow` -> `ObserveConfirmed` -> `ScanMark` path
// `CombatTracker.CreditExecutedScanTag` runs - and go into the production `ScanRoundEstimates.CreditConfirmed`,
// the one entry that decides whether a confirmed mark may open a batch. Narrowing that entry to `ScanMark.First`
// turns the assertions below red; the scratch mutation is recorded in the receipt.
internal static partial class Program
{
    private static void RunScanRoundRepeatScenarios()
    {
        Console.WriteLine("---- 1.19.62: a second sweep of old enemies is still the second batch ----");
        ScanRoundEstimates.Reset();
        ScanRoundComparison.Reset();

        // One row: another machine's human, which is the only row the host estimates.
        const ulong guest = 0x5BUL;
        const int a = 0xA1D, b = 0xB2E, c = 0xC3F;
        var session = new ScanSession();
        var counters = new ImpactCounters();
        double captured = 100.0; // the monotonic capture stamp a confirmation arrives with

        // ---- first sweep: three new enemies, one batch ----------------------------------------------------
        foreach (int enemy in new[] { a, b, c })
        {
            ScanMark first = session.ObserveConfirmed(enemy, counters, true);
            Check(first == ScanMark.First, "the first sweep reveals enemy " + enemy + " for the squad");
            ScanRoundEstimates.CreditConfirmed(true, false, false, guest, first, captured);
            captured += 0.04; // one native pass: one sweep's confirmations arrive microseconds apart
        }
        Check(counters.ScanFirstMarks == 3 && counters.ScanMarks == 3,
            "three new enemies are three first marks and three cumulative marks: " + counters.ScanFirstMarks + "/" + counters.ScanMarks);
        Check(ScanRoundEstimates.TryRounds(guest, out long firstBatch) && firstBatch == 1,
            "and one sweep of marks is one estimated scan: " + firstBatch);
        Check(ScanRoundEstimates.Display(counters, guest, true) == 1,
            "which is the figure the board draws for a row with no measured count of its own");

        // The two totals exactly as the board and the archive freeze them before the second sweep.
        long frozenFirst = counters.ScanFirstMarks, frozenMarks = counters.ScanMarks;

        // ---- second sweep: only old enemies, and the visible totals cannot move ----------------------------
        //
        // One cooldown later - the vanilla Bio Tracker's 8.5 s, well past the 2.0 s cluster - the same guest sweeps
        // C, A, B and A again, the last one a repeat inside its own sweep. `BeginConfirmWindow` is deliberately not
        // called between the two sweeps: every identity is already in the open window, so this is precisely the case
        // where the cumulative figure stays flat and a rule keyed on either total would count nothing.
        captured += 8.5;
        int confirmations = 0, opened = 0;
        foreach (int enemy in new[] { c, a, b, a })
        {
            ScanMark repeat = session.ObserveConfirmed(enemy, counters, true);
            Check(repeat == ScanMark.Repeat, "an enemy the squad already knows is a repeat, never a new first mark");
            confirmations++;
            if (ScanRoundEstimates.CreditConfirmed(true, false, false, guest, repeat, captured)) opened++;
            captured += 0.04;
        }
        Check(confirmations == 4 && opened == 1,
            "all four repeat confirmations are accepted and exactly one of them opens the batch: opened=" + opened);
        Check(counters.ScanFirstMarks == frozenFirst && counters.ScanMarks == frozenMarks,
            "the repeat sweep moves neither total, which is why neither total may gate the estimate: "
            + counters.ScanFirstMarks + "/" + counters.ScanMarks);
        Check(ScanRoundEstimates.TryRounds(guest, out long rounds) && rounds == firstBatch + 1 && rounds == 2,
            "the whole second sweep adds exactly one scan: " + rounds);

        // ---- what the board prints, and what the archive freezes ------------------------------------------
        Check(ScanRoundEstimates.Display(counters, guest, true) == 2,
            "the host's own board draws two scans for that row");
        var row = new ImpactInput
        {
            Name = "Scout", ToolName = "Bio Tracker", Lookup = guest, Counters = counters,
            ScanRoundsEstimated = ScanRoundEstimates.Display(counters, guest, true)
        };
        var entry = new ImpactEntry(row, null, null, 50, 1, ImpactRating.Scan, (int)ImpactMetric.ToolEfficiency);
        string previous = CombatStrings.Language;
        try
        {
            CombatStrings.Language = "zh";
            string chinese = ImpactBoard.ToolEfficiencyText(entry);
            Check(chinese == "扫描 2 次 / 首次 3 只 / 累计 3 只",
                "the count slot reads 扫描 2 次 and both marks it froze are printed even though they are equal: " + chinese);
            Check(!chinese.Contains("~") && !chinese.Contains("约") && !chinese.Contains("估算")
                && !chinese.Contains("轮次") && !chinese.Contains("扫描 —"),
                "and nothing on that cell calls the figure an estimate or adds a 轮次 clause: " + chinese);
            CombatStrings.Language = "en";
            string english = ImpactBoard.ToolEfficiencyText(entry);
            Check(english == "scan 2x / first 3 / cumulative 3", "the English cell is the same three clauses: " + english);
        }
        finally { CombatStrings.Language = previous; }

        // ---- a confirmation that never happened, and one the session rejected ------------------------------
        Check(ScanRoundEstimates.Display(counters, guest, true) == 2,
            "with no new native confirmation the figure does not move: the board still draws two");
        ScanMark rejected = session.ObserveConfirmed(a, counters, false);
        Check(rejected == ScanMark.Rejected
            && !ScanRoundEstimates.CreditConfirmed(true, false, false, guest, rejected, captured),
            "a mark the session did not confirm is rejected and opens no batch");
        Check(ScanRoundEstimates.TryRounds(guest, out long afterReject) && afterReject == rounds,
            "and the rejected mark leaves the figure exactly where it was: " + afterReject);
        Check(counters.ScanFirstMarks == frozenFirst && counters.ScanMarks == frozenMarks,
            "with neither total touched either");

        // ---- the gates that keep every other row out of this figure ---------------------------------------
        Check(!ScanRoundEstimates.CreditConfirmed(false, false, false, guest, ScanMark.First, captured + 30),
            "a machine that is not the host opens no batch");
        Check(!ScanRoundEstimates.CreditConfirmed(true, true, false, guest, ScanMark.First, captured + 31),
            "the host's own scanner is measured locally and is never estimated");
        Check(!ScanRoundEstimates.CreditConfirmed(true, false, true, guest, ScanMark.Repeat, captured + 32),
            "a bot's mark opens no batch");
        Check(ScanRoundEstimates.TryRounds(guest, out long gated) && gated == rounds,
            "and none of those three calls moved the figure: " + gated);

        // ---- the wiring this regression rides on ----------------------------------------------------------
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string estimates = File.ReadAllText(Path.Combine(root, "ScanRoundEstimates.cs"));
        string tracker = File.ReadAllText(Path.Combine(root, "CombatTracker.cs"));
        Check(estimates.Contains("public static bool CreditConfirmed(bool isMaster, bool isLocal, bool isBot, ulong lookup, ScanMark mark, double receivedAt)")
            && estimates.Contains("mark != ScanMark.Rejected && Credit(isMaster, isLocal, isBot, lookup, receivedAt)"),
            "the production entry accepts every confirmed mark and refuses only a rejected one");
        Check(!estimates.Contains("mark == ScanMark.First") && !estimates.Contains("ScanMark.First &&"),
            "that gate is not narrowed to first marks: a repeat is what makes the second sweep count");
        Check(tracker.Contains("probe.Session.BeginConfirmWindow();")
            && tracker.Contains("ScanMark mark = probe.Session.ObserveConfirmed(enemy.GetInstanceID(), player.Impact, true);")
            && tracker.Contains("ScanRoundEstimates.CreditConfirmed(SNet.IsMaster, owner.IsLocal, owner.IsBot, lookup, mark, receivedAt)"),
            "the drain reads the mark off the production session and hands that same mark to the gate this regression drives");

        ScanRoundEstimates.Reset();
        ScanRoundComparison.Reset();
    }
}
