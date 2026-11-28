using System;
using System.IO;
using System.Collections.Generic;
using HawChow.GTFOCombatPanel;

// 1.19.62 (unreleased): the host's estimate of Bio Tracker scan batches, and the walls around it.
//
// What is asserted here is the whole feature as rules rather than as a run: one short cluster of a player's
// confirmed marks is one batch however many marks it holds; a cluster far enough away is the next batch; neither
// the number of marks nor elapsed time is ever turned into batches; only the host estimates, and only for another
// machine's human; a measured activation figure always wins over the estimate, including a counted zero and
// including a figure smaller than the estimate; the estimate never enters a counter, a snapshot, a merge, a
// settlement cache or a score; and a machine that is not the host prints nothing at all.
//
// One cell is new and only one: a row with no measured activation figure that this host did estimate prints the
// batch count in the slot a measured count occupies, as a plain number (`扫描 2 次` / `scan 2x`, no `~`, no 约, no
// （估算）/est., no 轮次 clause). 1.19.64 keeps that figure exactly where it is and makes every scanner cell print
// its first and its cumulative marks unconditionally, so the cells asserted below carry three clauses even when
// the last two figures are equal.
internal static partial class Program
{
    private static void RunScanRoundEstimateScenarios()
    {
        // ---- one cluster of marks is one round ------------------------------------------------------------
        Console.WriteLine("---- 1.19.62: the host's scan estimate, its 2.0 s cluster, and the count slot it fills ----");
        var ledger = new ScanRoundEstimateLedger();
        const ulong scout = 0x51UL;
        for (int mark = 0; mark < 5; mark++) ledger.Credit(scout, 100.0 + 0.05 * mark);
        Check(ledger.TryGet(scout, out long rounds) && rounds == 1,
            "five marks of one sweep are one round, not five: the estimate is not a mark count");
        for (int mark = 0; mark < 3; mark++) ledger.Credit(scout, 130.0 + 0.05 * mark);
        Check(ledger.TryGet(scout, out rounds) && rounds == 2,
            "5+3=2: five marks of one sweep and three of the next are two rounds");
        Check(ledger.Marks(scout) == 8, "the mark count is kept for the one comparison line, and never decides a round");

        // The same enemy marked again by a later sweep is the next round: the ledger is keyed by player, so an
        // enemy identity can neither merge two sweeps into one nor split one sweep into several.
        var sameEnemy = new ScanRoundEstimateLedger();
        sameEnemy.Credit(scout, 10.0);
        sameEnemy.Credit(scout, 10.3);
        Check(sameEnemy.TryGet(scout, out rounds) && rounds == 1, "one enemy marked twice in one sweep is still one round");
        sameEnemy.Credit(scout, 60.0);
        Check(sameEnemy.TryGet(scout, out rounds) && rounds == 2,
            "the same enemy marked again by a later sweep is a second round");

        // ---- two remote scanners at once ------------------------------------------------------------------
        var squad = new ScanRoundEstimateLedger();
        const ulong first = 0x61UL, second = 0x62UL;
        for (int mark = 0; mark < 4; mark++)
        {
            squad.Credit(first, 20.0 + 0.05 * mark);
            squad.Credit(second, 20.0 + 0.05 * mark);
        }
        Check(squad.TryGet(first, out long firstRounds) && firstRounds == 1
            && squad.TryGet(second, out long secondRounds) && secondRounds == 1 && squad.Count == 2,
            "two remote humans scanning at the same moment are one round each, never one shared round");

        // ---- a long silence adds nothing, and neither does elapsed time ------------------------------------
        var silence = new ScanRoundEstimateLedger();
        silence.Credit(scout, 5.0);
        Check(silence.TryGet(scout, out rounds) && rounds == 1, "the first cluster opens one round");
        for (double t = 6.0; t < 600.0; t += 8.5) { /* 70 cooldowns roll over with no mark at all */ }
        Check(silence.TryGet(scout, out rounds) && rounds == 1,
            "a silent expedition adds nothing, however many cooldowns elapsed: time is not a round");
        silence.Credit(scout, 600.0);
        Check(silence.TryGet(scout, out rounds) && rounds == 2, "and the next real mark opens the next round");

        // ---- a delayed or batched drain -------------------------------------------------------------------
        var drained = new ScanRoundEstimateLedger();
        drained.Credit(scout, 100.0);
        drained.Credit(scout, 100.4);
        Check(drained.TryGet(scout, out rounds) && rounds == 1,
            "marks captured within the window are one round whatever the wall clock says when they are drained");
        var outOfOrder = new ScanRoundEstimateLedger();
        outOfOrder.Credit(scout, 200.0);
        outOfOrder.Credit(scout, 150.0);
        Check(outOfOrder.TryGet(scout, out rounds) && rounds == 1,
            "an older stamp arriving after a newer one continues the window instead of opening a second round");
        outOfOrder.Credit(scout, 201.5);
        Check(outOfOrder.TryGet(scout, out rounds) && rounds == 1,
            "and it never drags the window back, so the next mark inside 2 s is still the same round");

        // ---- a new expedition starts from nothing ----------------------------------------------------------
        drained.Clear();
        Check(!drained.TryGet(scout, out _) && drained.Count == 0 && drained.Marks(scout) == 0,
            "a new expedition clears the estimate: no round survives a reset");

        // ---- only the host, only another machine's human ---------------------------------------------------
        ScanRoundEstimates.Reset();
        Check(!ScanRoundEstimates.Credit(false, false, false, 44, 1.0) && ScanRoundEstimates.Count == 0,
            "a client estimates nothing: the ledger stays empty");
        Check(!ScanRoundEstimates.Credit(true, true, false, 44, 2.0) && ScanRoundEstimates.Count == 0,
            "the host's own scanner is measured locally and is never estimated");
        Check(!ScanRoundEstimates.Credit(true, false, true, 44, 3.0) && ScanRoundEstimates.Count == 0,
            "a bot's mark is never estimated");
        Check(!ScanRoundEstimates.Credit(true, false, false, 0, 4.0) && !ScanRoundEstimates.Credit(true, false, false, 44, double.NaN)
            && ScanRoundEstimates.Count == 0,
            "an empty owner or an unusable stamp estimates nothing");
        Check(ScanRoundEstimates.Credit(true, false, false, 44, 5.0) && ScanRoundEstimates.Count == 1
            && ScanRoundEstimates.TryRounds(44, out long hostRounds) && hostRounds == 1,
            "the host estimates one round for a remote human from a confirmed native mark");
        Check(ScanRoundEstimates.Display(new ImpactCounters(), 44, true) == 1,
            "and that row, which has no measured figure of its own, shows it");
        // The figure belongs to the host that grouped it. The moment this machine is not the host - a host migration,
        // a demotion, an unreadable role - the same row answers 0, so no board draws a stale estimate.
        Check(ScanRoundEstimates.Display(new ImpactCounters(), 44, false) == 0
            && ScanRoundEstimates.TryRounds(44, out hostRounds) && hostRounds == 1,
            "a machine that is no longer the host draws no estimate, and the ledger keeps the rounds it saw");
        Check(ScanRoundEstimates.Display(new ImpactCounters(), 44, true) == 1,
            "and the figure is back for a caller that can still claim the host role");

        // ---- a measured figure always wins, including a counted zero ---------------------------------------
        var measured = new ImpactCounters { ScanUses = 0, ScanUsesKnown = true };
        Check(ledger.Display(measured, scout) == 0,
            "a counted zero from the holder's own cooldown wins over an estimate of two rounds");
        var ownerReported = new ImpactCounters { ScanUses = 1 };
        Check(ledger.Display(ownerReported, scout) == 0,
            "the holder's own reported figure wins even when it is smaller than the estimate");
        var unknown = new ImpactCounters { ScanFirstMarks = 8, ScanMarks = 12 };
        Check(ledger.Display(unknown, scout) == 2, "a row with marks and no measured figure keeps the estimate");
        Check(unknown.ScanUses == 0 && !unknown.ScanUsesKnown && !unknown.ScanEstimated,
            "reading the estimate never writes a measured activation count, a known flag or an estimate flag into the row");
        Check(unknown.ScanFirstMarks == 8 && unknown.ScanMarks == 12,
            "reading the estimate never touches the first or cumulative mark counts either");
        Check(ledger.Display(null, scout) == 0 && ledger.Display(new ImpactCounters(), 0) == 0,
            "no counters and no owner show nothing");

        // ---- the tool cell: the estimate fills the count slot, and nothing marks it -------------------------
        string previous = CombatStrings.Language;
        try
        {
            // A mark-only row this host estimated three sweeps for: the cell reads exactly like a measured one.
            var estimatedCell = new ImpactInput { Name = "Scout", ToolName = "Bio Tracker", ScanRoundsEstimated = 3 };
            estimatedCell.Counters.ScanFirstMarks = 2; estimatedCell.Counters.ScanMarks = 5;
            CombatStrings.Language = "zh";
            string chinese = ImpactBoard.ToolEfficiencyText(new ImpactEntry(estimatedCell, null, null, 50, 1, ImpactRating.Scan, (int)ImpactMetric.ToolEfficiency));
            Check(chinese == "扫描 3 次 / 首次 2 只 / 累计 5 只",
                "the estimate fills the activation-count slot in the exact form a measured count prints in: " + chinese);
            Check(!chinese.Contains("~") && !chinese.Contains("约") && !chinese.Contains("估算")
                && !chinese.Contains("轮次") && !chinese.Contains("扫描 —"),
                "and the Chinese cell carries no marker, no 约, no 估算, no 轮次 clause and no dash: " + chinese);
            CombatStrings.Language = "en";
            string english = ImpactBoard.ToolEfficiencyText(new ImpactEntry(estimatedCell, null, null, 50, 1, ImpactRating.Scan, (int)ImpactMetric.ToolEfficiency));
            Check(english == "scan 3x / first 2 / cumulative 5",
                "the English cell is that same measured form, with no (est.) of its own: " + english);
            Check(!english.Contains("~") && !english.Contains("rounds") && !english.Contains("est.")
                && !english.Contains("Scan —") && !english.Contains("scan —"),
                "and no English marker, no rounds clause and no dash beside it: " + english);

            // Equal first and cumulative marks print the third clause too (1.19.64), and the count slot is still the
            // host's estimate.
            var equalCell = new ImpactInput { Name = "Scout", ToolName = "Bio Tracker", ScanRoundsEstimated = 2 };
            equalCell.Counters.ScanFirstMarks = 4; equalCell.Counters.ScanMarks = 4;
            CombatStrings.Language = "zh";
            string equalText = ImpactBoard.ToolEfficiencyText(new ImpactEntry(equalCell, null, null, 50, 1, ImpactRating.Scan, (int)ImpactMetric.ToolEfficiency));
            Check(equalText == "扫描 2 次 / 首次 4 只 / 累计 4 只",
                "an equal mark pair still prints its cumulative clause, with the estimate in the count slot: " + equalText);

            // Nothing was observed: each of the three fields keeps its own labelled placeholder, and the row gains no
            // number at all, whatever the ledger holds.
            var unseen = new ImpactInput { Name = "Scout", ToolName = "Bio Tracker" };
            string unseenCell = ImpactBoard.ToolEfficiencyText(new ImpactEntry(unseen, null, null, 50, 1, ImpactRating.Scan, (int)ImpactMetric.ToolEfficiency));
            Check(unseenCell == "扫描 — 次 / 首次 — 只 / 累计 — 只" && !unseenCell.Contains("轮次") && !unseenCell.Contains("rounds"),
                "a row nobody observed shows the three labelled placeholders and no estimate: " + unseenCell);

            // The owner's own figure wins, even when it is smaller than the estimate this host accumulated.
            var countedCell = new ImpactInput { Name = "Scout", ToolName = "Bio Tracker", ScanRoundsEstimated = 3 };
            countedCell.Counters.ScanUses = 2; countedCell.Counters.ScanUsesKnown = true;
            countedCell.Counters.ScanFirstMarks = 7; countedCell.Counters.ScanMarks = 7;
            string countedText = ImpactBoard.ToolEfficiencyText(new ImpactEntry(countedCell, null, null, 50, 1, ImpactRating.Scan, (int)ImpactMetric.ToolEfficiency));
            Check(countedText == "扫描 2 次 / 首次 7 只 / 累计 7 只",
                "a row with a measured count prints that count and no competing estimate, and its cumulative clause stays: " + countedText);

            // A counted zero wins the same way, and it prints as the zero it is.
            var zeroCell = new ImpactInput { Name = "Scout", ToolName = "Bio Tracker", ScanRoundsEstimated = 3 };
            zeroCell.Counters.ScanUsesKnown = true;
            zeroCell.Counters.ScanFirstMarks = 7; zeroCell.Counters.ScanMarks = 7;
            string zeroText = ImpactBoard.ToolEfficiencyText(new ImpactEntry(zeroCell, null, null, 50, 1, ImpactRating.Scan, (int)ImpactMetric.ToolEfficiency));
            Check(zeroText == "扫描 0 次 / 首次 7 只 / 累计 7 只",
                "a known zero beats the estimate, and the cell prints the zero rather than the estimate: " + zeroText);

            // A smaller owner-reported count wins too.
            var reportedCell = new ImpactInput { Name = "Scout", ToolName = "Bio Tracker", ScanRoundsEstimated = 3 };
            reportedCell.Counters.ScanUses = 1;
            reportedCell.Counters.ScanFirstMarks = 7; reportedCell.Counters.ScanMarks = 7;
            string reportedText = ImpactBoard.ToolEfficiencyText(new ImpactEntry(reportedCell, null, null, 50, 1, ImpactRating.Scan, (int)ImpactMetric.ToolEfficiency));
            Check(reportedText == "扫描 1 次 / 首次 7 只 / 累计 7 只",
                "an owner-reported count smaller than the estimate still wins: " + reportedText);

            // The rows the estimate never touches keep the same three clauses they now all carry: a bot's sweep
            // still leads with the estimate marker, the reason line keeps the same marker, and a mark-only row with
            // no estimate at all is still `扫描 — 次` beside the marks it proved.
            var botCell = new ImpactInput { Name = "Bot", ToolName = "Bio Tracker" };
            botCell.Counters.CreditScan(3, true);
            var botEntry = new ImpactEntry(botCell, null, null, 50, 1, ImpactRating.Scan, (int)ImpactMetric.ToolEfficiency);
            Check(ImpactBoard.ToolEfficiencyText(botEntry) == "~扫描 1 次 / 首次 3 只 / 累计 3 只",
                "a bot's estimated row prints all three fields behind its marker: " + ImpactBoard.ToolEfficiencyText(botEntry));
            Check(ImpactBoard.ReasonText(botEntry, 0) == "~扫描 1 次，首次标记 3 只",
                "and its tag sentence keeps the marker it always carried: " + ImpactBoard.ReasonText(botEntry, 0));
            var plainMarkCell = new ImpactInput { Name = "Mate", ToolName = "Bio Tracker" };
            plainMarkCell.Counters.ScanFirstMarks = 3; plainMarkCell.Counters.ScanMarks = 3;
            Check(ImpactBoard.ToolEfficiencyText(new ImpactEntry(plainMarkCell, null, null, 50, 1, ImpactRating.Scan, (int)ImpactMetric.ToolEfficiency)) == "扫描 — 次 / 首次 3 只 / 累计 3 只",
                "a mark-only row this host did not estimate keeps the dash for its count and both marks it proved");
        }
        finally { CombatStrings.Language = previous; }

        // ---- scoring and serialization isolation -----------------------------------------------------------
        var plainRow = new ImpactInput { Name = "Scout", ToolName = "Bio Tracker" };
        plainRow.Counters.ScanFirstMarks = 4; plainRow.Counters.ScanMarks = 6;
        var estimatedRow = new ImpactInput { Name = "Scout", ToolName = "Bio Tracker", ScanRoundsEstimated = 3 };
        estimatedRow.Counters.ScanFirstMarks = 4; estimatedRow.Counters.ScanMarks = 6;
        ImpactReport plainReport = ImpactScoring.Evaluate(new[] { plainRow }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        ImpactReport estimatedReport = ImpactScoring.Evaluate(new[] { estimatedRow }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        Check(plainReport.Entries.Count == 1 && estimatedReport.Entries.Count == 1
            && plainReport.Entries[0].Score.Equals(estimatedReport.Entries[0].Score)
            && plainReport.Entries[0].Rank == estimatedReport.Entries[0].Rank
            && plainReport.Entries[0].Rating == estimatedReport.Entries[0].Rating,
            "the estimate changes no score, no rank and no title");

        var wireRow = new CombatPlayer(0x71UL);
        wireRow.Impact.ScanFirstMarks = 4; wireRow.Impact.ScanMarks = 6;
        var wireInput = ImpactInput.From(wireRow.Name, wireRow.Total, wireRow.Impact, false);
        wireInput.Lookup = wireRow.Lookup;
        wireInput.ScanRoundsEstimated = ScanRoundEstimates.Display(wireRow.Impact, wireRow.Lookup, true);
        SyncSnapshot wireSnapshot = SyncProtocol.Capture(wireRow, 0, 1, 2, 3, 0, false);
        Check(wireSnapshot.ScanUses == 0 && !wireSnapshot.Has(SyncFlags.ScanEstimated),
            "the estimate writes no activation count and no estimate flag into the squad snapshot");
        Check(wireSnapshot.ScanFirstMarks == 4 && wireSnapshot.ScanMarks == 6,
            "the marks the snapshot carries are exactly the ones the row proved");
        var wireRestored = new CombatPlayer(0x71UL);
        SyncProtocol.Apply(wireRestored, wireSnapshot, 0, 1, 2, 3);
        Check(wireRestored.Impact.ScanUses == 0 && !wireRestored.Impact.ScanUsesKnown
            && wireRestored.Impact.ScanFirstMarks == 4 && wireRestored.Impact.ScanMarks == 6,
            "and applying it restores the measured marks alone, with no invented activation count");
        var wireMerged = SyncProtocol.Combine(wireSnapshot, true, new SyncSnapshot { Owner = 0x71UL }, false);
        Check(wireMerged.ScanUses == 0 && wireMerged.ScanFirstMarks == 4 && wireMerged.ScanMarks == 6,
            "a merge carries the measured figures and nothing the estimate invented");

        var mergeRow = new CombatPlayer(0x72UL);
        mergeRow.Impact.ScanFirstMarks = 2;
        CombatMerge.ApplyRemote(mergeRow, new SyncSnapshot { Owner = 0x72UL, ScanFirstMarks = 3, ScanMarks = 3 }, 0, 1, 2, 3, 0UL);
        Check(mergeRow.Impact.ScanUses == 0 && !mergeRow.Impact.ScanUsesKnown,
            "an incoming remote snapshot cannot hand a row an activation count this host estimated");

        var cached = new SettlementRows();
        var cachedRow = new CombatPlayer(0x73UL);
        cachedRow.Impact.ScanFirstMarks = 2; cachedRow.Impact.ScanMarks = 5;
        List<CombatPlayer> cachedRows = cached.Update(new[] { cachedRow }, 9);
        Check(cachedRows.Count == 1 && cachedRows[0].Impact.ScanUses == 0 && !cachedRows[0].Impact.ScanUsesKnown
            && cachedRows[0].Impact.ScanFirstMarks == 2 && cachedRows[0].Impact.ScanMarks == 5,
            "the settlement cache keeps the measured marks and carries no activation count from the estimate");

        // ---- the one comparison line -----------------------------------------------------------------------
        //
        // 1.19.62 (review finding): this line reads the ledger's own figure, so it has to survive the case it exists for -
        // the owner's measured count finally arriving. The old expectation ("no line left once the owner's figure has
        // arrived") was that bug written down as a test: `LedgerFigure` went through the board's measured-first rule and
        // answered 0 exactly then.
        ScanRoundEstimates.Reset(); ScanRoundComparison.Reset();
        ScanRoundEstimates.Credit(true, false, false, 55, 40.0);
        ScanRoundEstimates.Credit(true, false, false, 55, 60.0);
        ScanRoundEstimates.Credit(true, false, false, 55, 80.0);
        var comparedRow = new ImpactInput { Name = "Scout", ToolName = "Bio Tracker" };
        comparedRow.Lookup = 55; comparedRow.Counters.ScanFirstMarks = 2;
        var otherRow = new ImpactInput { Name = "Mate", ToolName = "Sentry Gun" };
        otherRow.Lookup = 56;
        ImpactReport compared = ImpactScoring.Evaluate(new[] { comparedRow, otherRow }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        List<string> unknownLines = ScanRoundComparison.Lines(compared, 3, true);
        Check(unknownLines.Count == 1 && unknownLines[0].Contains("estimate=3")
            && unknownLines[0].Contains("measured=— (unknown)")
            && unknownLines[0].Contains("Scout") && !unknownLines[0].Contains("Mate"),
            "one line per estimated player states both figures - and only that player, never one the host grouped nothing for: "
            + string.Join(" | ", unknownLines));
        // A migrated machine prints nothing, and it does not consume the latch: the line it already wrote is not repeated
        // when it becomes the host again inside the same run.
        Check(ScanRoundComparison.Lines(compared, 3, false).Count == 0,
            "a machine that is no longer the host prints no comparison line, so a migrated client never logs the old host's figure");
        Check(ScanRoundComparison.Lines(compared, 3, true).Count == 0,
            "and the latch survived that client frame, so the same run does not write the same line twice");
        // The owner's own figure arrives: the board keeps printing it (measured-first, unchanged) and the diagnostic now
        // states both numbers instead of disappearing.
        comparedRow.Counters.ScanUses = 2;
        Check(ScanRoundEstimates.Display(comparedRow.Counters, 55, true) == 0,
            "the board itself still refuses the estimate once the row has its own count");
        List<string> ownerLines = ScanRoundComparison.Lines(compared, 3, true);
        Check(ownerLines.Count == 1 && ownerLines[0].Contains("estimate=3")
            && ownerLines[0].Contains("measured=2 (owner-reported)"),
            "the line survives the owner's figure arriving and states both: estimate=3 vs owner-reported=2: "
            + string.Join(" | ", ownerLines));
        // A counted zero is the same case, one number lower.
        comparedRow.Counters.ScanUses = 0; comparedRow.Counters.ScanUsesKnown = true;
        Check(ScanRoundEstimates.Display(comparedRow.Counters, 55, true) == 0,
            "a counted zero is still the board's own figure, not the estimate");
        List<string> zeroLines = ScanRoundComparison.Lines(compared, 3, true);
        Check(zeroLines.Count == 1 && zeroLines[0].Contains("estimate=3")
            && zeroLines[0].Contains("measured=0 (holder's own cooldown)"),
            "and the line compares it too: estimate=3 vs the holder's own counted zero: "
            + string.Join(" | ", zeroLines));
        Check(ScanRoundComparison.Lines(compared, 4, true).Count == 1, "a new run writes the line again");

        // ---- the wiring the runtime really uses ------------------------------------------------------------
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string tracker = File.ReadAllText(Path.Combine(root, "CombatTracker.cs"));
        string patches = File.ReadAllText(Path.Combine(root, "RemoteScanPatches.cs"));
        string saver = File.ReadAllText(Path.Combine(root, "ResultsSaver.cs"));
        string estimates = File.ReadAllText(Path.Combine(root, "ScanRoundEstimates.cs"));
        string sync = File.ReadAllText(Path.Combine(root, "SyncProtocol.cs"));
        string mergeSource = File.ReadAllText(Path.Combine(root, "CombatMerge.cs"));
        string settlement = File.ReadAllText(Path.Combine(root, "SettlementRows.cs"));
        Check(tracker.Contains("ScanRoundEstimates.CreditConfirmed(SNet.IsMaster, owner.IsLocal, owner.IsBot, lookup, mark, receivedAt)"),
            "the host gate, the remote-human gate and the confirmed mark are the ones the estimate reads - through the one entry that decides which mark may open a batch");
        Check(patches.Contains("CreditExecutedScanTag(enemy, owner, entry.Owner == 0, entry.Time)"),
            "the stamp handed to the ledger is the pipeline's capture time, not the main-thread drain time");
        Check(tracker.Contains("ScanTagPipeline.Drain();") && patches.Contains("Time = Environment.TickCount64 / 1000.0;"),
            "and that capture time is still the monotonic stamp taken when the mark was received");
        // 1.19.62 (review finding): the save hands the diagnostic its own role and the diagnostic reads the ledger
        // directly, so the line survives the owner's figure arriving; and the loop stays pure logging - it must not
        // gate the record that is written below it.
        Check(saver.Contains("ScanRoundComparison.Lines(report, run, isMaster)")
            && saver.Contains("internal static void Save(Component page, bool succeeded, ImpactBoardLayout board, ImpactReport report, uint run, bool isMaster)"),
            "the settlement save is where the comparison line is written, and it passes the role the diagnostic gates on");
        Check(estimates.Contains("ScanRoundEstimates.TryRounds(entry.Input.Lookup, out long grouped) ? grouped : 0")
            && !estimates.Contains("internal static long LedgerFigure("),
            "the line reads the ledger's own figure, and the board's measured-first helper it used to sit on is gone");
        string resultsSource = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        Check(resultsSource.Contains("ResultsSaver.Save(page, succeeded, board, report, CombatSync.Generation, HostProven())"),
            "the role is the one read the board already owns, so a migrated machine hands the diagnostic a false host");
        Check(saver.IndexOf("ScanRoundComparison.Lines(", StringComparison.Ordinal) < saver.IndexOf("if (newScreen)", StringComparison.Ordinal)
            && saver.Contains("string json = ResultsLog.Build(succeeded, writtenAt, PluginInfo.Version, board, report, metadata, run);"),
            "and the log loop still sits before the write and cannot change what that write contains");
        Check(!sync.Contains("ScanRound") && !mergeSource.Contains("ScanRound") && !settlement.Contains("ScanRound"),
            "no snapshot, no merge and no settlement cache rule mentions the estimate: it never travels");
        Check(tracker.Contains("ScanRoundEstimates.Reset(); ScanRoundComparison.Reset();"),
            "a new expedition clears both the estimate and its comparison latch");
        // 1.19.62 (unreleased): the display wiring itself - the host role the board reads, the count slot the figure
        // lands in, and the absence of the estimate wording this build deliberately does not print.
        string display = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        string board = File.ReadAllText(Path.Combine(root, "ImpactBoard.cs"));
        string strings = File.ReadAllText(Path.Combine(root, "Localization.cs"));
        Check(display.Contains("ScanRoundEstimates.Display(player.Impact, player.Lookup, HostProven())")
            && display.Contains("private static bool HostProven() => _roleReadable && Role() == ResultPageRole.Host;"),
            "the board asks the ledger with the role this file already reads once, and an unreadable role is never a host");
        Check(board.Contains("private static string ScanCell(ImpactCounters counters, ImpactEntry entry)")
            && board.Contains("CombatStrings.ToolScan(false, Count(entry.Input.ScanRoundsEstimated),")
            && !board.Contains("RoundsEstimate("),
            "the estimate reaches that same slot as a plain count, and no second 轮次 segment is appended to the cell");
        // The strings file keeps the estimate marker of every other row untouched: only the fallback branch above
        // passes `estimated: false`, so `ToolScan` still leads with `~` for the rows it always did. 1.19.64 removed
        // the equality test that used to shorten the cell, so the three clauses are now unconditional.
        Check(strings.Contains("(estimated ? \"~\" : string.Empty) + Compound(")
            && !strings.Contains("SameMarkCount") && !strings.Contains("轮次") && !strings.Contains("估算")
            && !strings.Contains("rounds ~"),
            "ToolScan keeps the baseline marker rule, prints all three fields and never calls the figure rounds or an estimate");
    }
}
