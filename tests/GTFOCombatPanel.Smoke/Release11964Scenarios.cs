using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using HawChow.GTFOCombatPanel;

// 1.19.64: the scan cell prints its three figures - scans, first marks, cumulative marks - on every scanner row,
// on the board and in the archive.
//
// The rule is the one the user asked for after reading a live board: the Dauda row read `扫描 2 次 / 首次 7 只`
// with no cumulative figure at all, because `CombatStrings.ToolScan` dropped the third clause whenever the two
// mark counts were equal, and a row with no evidence collapsed to a single `扫描 —`. Every assertion below is
// driven through the production chain - `ImpactScoring.Evaluate` -> `ImpactBoard.Build` -> `ResultsLog.Build` -
// so the pre-1.19.64 shapes turn these assertions red:
//   * equal counts print three clauses, differing counts print three clauses;
//   * every way a scanner row can carry data - the holder's measured count, a counted zero, an owner-reported
//     count, the host's round estimate, marks alone, or no evidence at all - still prints three fields;
//   * the trusted zero is a `0` and the genuine absence is a labelled placeholder, never one for the other;
//   * the `~` estimate marker, the round estimate's host-side slot and the scoring sentence stay as they were;
//   * the settlement archive stores the same cell text the board drew, so the file cannot drop the figure either.
internal static class Release11964Scenarios
{
    internal static void RunInto(Action<bool, string> check)
    {
        EqualCountsPrintTheCumulativeFigure(check);
        EveryScannerCarrierPrintsThreeFields(check);
        TheTrustedZeroAndThePlaceholderAreDifferent(check);
        TheMarkerAndTheSentenceAreUntouched(check);
        TheEstimateStaysHostSide(check);
        TheArchiveIsTheBoardText(check);
        TheOldShapesAreGone(check);
        ASlotWithMoreThanOneWeaponNamesTheOthers(check);
        ASlotWithOneWeaponIsUnchanged(check);
        DuplicatesAndPlaceholdersAreNotWeapons(check);
        TheArchiveCarriesEachObservedRow(check);
        DaudaRowPrintsAllThreeScanFigures(check);
        KnownUsesIsThisMachinesOwnLedger(check);
        TheComparisonLineNamesBothMarkFigures(check);
        TheBoardAndArchiveNoLongerFoldSilently(check);
        SlotWeaponEntriesKeepTheirObservation(check);
        AttachingSlotWeaponsTwiceIsIdempotent(check);
    }

    // ---- the row the user read: seven first marks, seven cumulative marks ---------------------------------
    private static void EqualCountsPrintTheCumulativeFigure(Action<bool, string> check)
    {
        string previous = CombatStrings.Language;
        try
        {
            var dauda = new ImpactInput { Name = "Dauda", ToolName = "Bio Tracker" };
            dauda.Counters.ScanUses = 2; dauda.Counters.ScanUsesKnown = true;
            dauda.Counters.ScanFirstMarks = 7; dauda.Counters.ScanMarks = 7;
            CombatStrings.Language = "zh";
            string chinese = Cell(dauda);
            check(chinese == "扫描 2 次 / 首次 7 只 / 累计 7 只",
                "an equal mark pair prints its cumulative clause on the board: " + chinese);
            check(Fields(chinese) == 3, "the scanner cell is three fields: " + chinese);
            CombatStrings.Language = "en";
            string english = Cell(dauda);
            check(english == "scan 2x / first 7 / cumulative 7",
                "the English cell is the same three fields, in the same order: " + english);

            // A differing pair keeps the same shape: nothing about the cell depends on whether the two figures agree.
            dauda.Counters.ScanMarks = 13;
            CombatStrings.Language = "zh";
            string differing = Cell(dauda);
            check(differing == "扫描 2 次 / 首次 7 只 / 累计 13 只",
                "differing counts print the same three fields: " + differing);
            CombatStrings.Language = "en";
            string differingEnglish = Cell(dauda);
            check(differingEnglish == "scan 2x / first 7 / cumulative 13",
                "in English too: " + differingEnglish);
        }
        finally { CombatStrings.Language = previous; }
    }

    // ---- every way a scanner row can carry data ------------------------------------------------------------
    private static void EveryScannerCarrierPrintsThreeFields(Action<bool, string> check)
    {
        string previous = CombatStrings.Language;
        try
        {
            CombatStrings.Language = "zh";
            var cases = new List<KeyValuePair<string, ImpactInput>>();

            var measured = new ImpactInput { Name = "Measured", ToolName = "Bio Tracker" };
            measured.Counters.ScanUses = 4; measured.Counters.ScanUsesKnown = true;
            measured.Counters.ScanFirstMarks = 9; measured.Counters.ScanMarks = 13;
            cases.Add(new KeyValuePair<string, ImpactInput>("a holder's measured count", measured));

            var countedZero = new ImpactInput { Name = "Zero", ToolName = "Bio Tracker" };
            countedZero.Counters.ScanUsesKnown = true;
            cases.Add(new KeyValuePair<string, ImpactInput>("a counted zero", countedZero));

            var ownerReported = new ImpactInput { Name = "Reported", ToolName = "Bio Tracker" };
            ownerReported.Counters.ScanUses = 1;
            ownerReported.Counters.ScanFirstMarks = 3; ownerReported.Counters.ScanMarks = 3;
            cases.Add(new KeyValuePair<string, ImpactInput>("an owner-reported count", ownerReported));

            var estimated = new ImpactInput { Name = "Remote", ToolName = "Bio Tracker", ScanRoundsEstimated = 3 };
            estimated.Counters.ScanFirstMarks = 2; estimated.Counters.ScanMarks = 5;
            cases.Add(new KeyValuePair<string, ImpactInput>("the host's round estimate", estimated));

            var marksOnly = new ImpactInput { Name = "Marks", ToolName = "Bio Tracker" };
            marksOnly.Counters.ScanFirstMarks = 3; marksOnly.Counters.ScanMarks = 3;
            cases.Add(new KeyValuePair<string, ImpactInput>("marks and no readable count", marksOnly));

            // Every confirmation of an enemy the squad already knows: a first-mark zero beside a cumulative figure.
            var repeatsOnly = new ImpactInput { Name = "Repeats", ToolName = "Bio Tracker" };
            repeatsOnly.Counters.ScanMarks = 5;
            cases.Add(new KeyValuePair<string, ImpactInput>("marks made only of repeats", repeatsOnly));

            var quiet = new ImpactInput { Name = "Quiet", ToolName = "Bio Tracker" };
            cases.Add(new KeyValuePair<string, ImpactInput>("no evidence at all", quiet));

            foreach (KeyValuePair<string, ImpactInput> pair in cases)
            {
                string cell = Cell(pair.Value);
                check(Fields(cell) == 3, "every scanner carrier prints three fields - " + pair.Key + ": " + cell);
                check(cell.Contains("首次") && cell.Contains("累计"),
                    "and the two mark clauses are both on it - " + pair.Key + ": " + cell);
            }
            string repeatCell = Cell(repeatsOnly);
            check(repeatCell == "扫描 — 次 / 首次 0 只 / 累计 5 只",
                "a sweep made entirely of repeats prints a counted first-mark zero beside its cumulative figure: " + repeatCell);

            // A tool that is not a scanner is untouched by any of this.
            var sentry = new ImpactInput { Name = "Sentry", ToolName = "Sentry Gun" };
            sentry.Counters.SentryShots = 4; sentry.Counters.SentryHits = 3; sentry.Counters.SentryDamage = 60;
            string sentryCell = Cell(sentry);
            check(!sentryCell.Contains("扫描") && !sentryCell.Contains("累计") && !sentryCell.Contains("首次"),
                "a non-scanner tool cell carries no scan wording: " + sentryCell);
            // A row with no tool at all is the one case the board has always drawn as a single dash: no scanner
            // wording and no invented zero is added to it.
            var plainTool = new ImpactInput { Name = "Plain" };
            check(Cell(plainTool) == CombatStrings.Placeholder,
                "a row with no tool and no data is still the one dash it always was: " + Cell(plainTool));
        }
        finally { CombatStrings.Language = previous; }
    }

    // ---- the trusted zero and the genuine absence are different things -------------------------------------
    private static void TheTrustedZeroAndThePlaceholderAreDifferent(Action<bool, string> check)
    {
        string previous = CombatStrings.Language;
        try
        {
            var known = new ImpactInput { Name = "Known", ToolName = "Bio Tracker" };
            known.Counters.ScanUsesKnown = true;
            var unknown = new ImpactInput { Name = "Unknown", ToolName = "Bio Tracker" };

            CombatStrings.Language = "zh";
            string zeroCell = Cell(known);
            check(zeroCell == "扫描 0 次 / 首次 0 只 / 累计 0 只",
                "a cooldown this machine read counts a real zero in all three fields: " + zeroCell);
            string unknownCell = Cell(unknown);
            check(unknownCell == "扫描 — 次 / 首次 — 只 / 累计 — 只",
                "a row nothing measured prints the three labelled placeholders: " + unknownCell);
            check(!unknownCell.Contains("0") && !unknownCell.Contains("~"),
                "and it invents no zero and claims no estimate: " + unknownCell);

            CombatStrings.Language = "en";
            string unknownEnglish = Cell(unknown);
            check(unknownEnglish == "scan —x / first — / cumulative —",
                "the English placeholders are the same three fields: " + unknownEnglish);
            string zeroEnglish = Cell(known);
            check(zeroEnglish == "scan 0x / first 0 / cumulative 0",
                "and the counted zero is a zero in English too: " + zeroEnglish);
        }
        finally { CombatStrings.Language = previous; }
    }

    // ---- the marker the row already had, and the sentence that scores it -----------------------------------
    private static void TheMarkerAndTheSentenceAreUntouched(Action<bool, string> check)
    {
        string previous = CombatStrings.Language;
        try
        {
            CombatStrings.Language = "zh";
            var bot = new ImpactInput { Name = "Bot", ToolName = "Bio Tracker" };
            bot.Counters.CreditScan(3, true);
            var entry = new ImpactEntry(bot, null, null, 50, 1, ImpactRating.Scan, (int)ImpactMetric.ToolEfficiency);
            string cell = ImpactBoard.ToolEfficiencyText(entry);
            check(cell == "~扫描 1 次 / 首次 3 只 / 累计 3 只",
                "a bot row keeps the estimate marker in front of three fields: " + cell);
            string sentence = ImpactBoard.ReasonText(entry, 0);
            check(sentence == "~扫描 1 次，首次标记 3 只",
                "the scoring sentence keeps its own marker and wording: " + sentence);
            check(!sentence.Contains("累计"),
                "no new clause is invented for the sentence, which is a scoring template: " + sentence);
        }
        finally { CombatStrings.Language = previous; }
    }

    // ---- the host's estimate is a side computation and never travels ---------------------------------------
    private static void TheEstimateStaysHostSide(Action<bool, string> check)
    {
        ScanRoundEstimates.Reset();
        const ulong guest = 0x91UL;
        ScanRoundEstimates.Credit(true, false, false, guest, 100.0);
        ScanRoundEstimates.Credit(true, false, false, guest, 130.0);

        var counters = new ImpactCounters();
        counters.ScanFirstMarks = 2; counters.ScanMarks = 5;
        check(ScanRoundEstimates.Display(counters, guest, true) == 2,
            "the host's estimate is drawn in the count slot of a row it grouped");
        check(ScanRoundEstimates.Display(counters, guest, false) == 0,
            "a machine that is not the host draws nothing rather than the previous host's figure");

        var remote = new ImpactInput
        {
            Name = "Remote", ToolName = "Bio Tracker", Lookup = guest, Counters = counters,
            ScanRoundsEstimated = ScanRoundEstimates.Display(counters, guest, true)
        };
        string previous = CombatStrings.Language;
        try
        {
            CombatStrings.Language = "zh";
            string cell = Cell(remote);
            check(cell == "扫描 2 次 / 首次 2 只 / 累计 5 只",
                "the estimate fills the count slot as a plain number, beside both mark clauses: " + cell);
            check(!cell.Contains("~") && !cell.Contains("约") && !cell.Contains("估算") && !cell.Contains("轮次"),
                "and it carries no marker, no 约, no 估算 and no second 轮次 clause: " + cell);
        }
        finally { CombatStrings.Language = previous; }

        check(counters.ScanUses == 0 && !counters.ScanUsesKnown && !counters.ScanEstimated,
            "reading the estimate writes no measured count, no known flag and no estimate flag into the row");
        check(counters.ScanFirstMarks == 2 && counters.ScanMarks == 5,
            "and it touches neither mark total");

        var wireRow = new CombatPlayer(guest);
        wireRow.Impact.ScanFirstMarks = 2; wireRow.Impact.ScanMarks = 5;
        SyncSnapshot snapshot = SyncProtocol.Capture(wireRow, 0, 1, 2, 3, 0, false);
        check(snapshot.ScanUses == 0 && !snapshot.Has(SyncFlags.ScanEstimated),
            "the squad snapshot carries no activation count and no estimate flag");
        check(snapshot.ScanFirstMarks == 2 && snapshot.ScanMarks == 5,
            "it carries exactly the marks the row proved");
    }

    // ---- the settlement archive is the text the board drew --------------------------------------------------
    private static void TheArchiveIsTheBoardText(Action<bool, string> check)
    {
        string previous = CombatStrings.Language;
        try
        {
            CombatStrings.Language = "zh";
            var dauda = new ImpactInput { Name = "Dauda", ToolName = "Bio Tracker" };
            dauda.Counters.ScanUses = 2; dauda.Counters.ScanUsesKnown = true;
            dauda.Counters.ScanFirstMarks = 7; dauda.Counters.ScanMarks = 7;
            ImpactReport report = ImpactScoring.Evaluate(new[] { dauda }, ImpactScoring.DefaultWeights, 1.0, 1.0);
            ImpactBoardLayout board = ImpactBoard.Build(report, false, true);
            int row = ToolRow(board);
            check(row > 0, "the settlement board carries the tool-efficiency row");
            string cell = row < 0 ? string.Empty : board.Joined(row, 1);
            check(cell == "扫描 2 次 / 首次 7 只 / 累计 7 只",
                "the board draws all three fields for that row: " + cell);

            string json = ResultsLog.Build(true, new DateTime(2026, 12, 1, 9, 0, 0, DateTimeKind.Utc), "1.19.64",
                board, report, default, 7);
            check(json.Length > 0 && json.Contains(cell),
                "the archive stores the same cell text, so the file cannot drop the cumulative figure either");
            check(json.Contains("\"version\": \"1.19.64\""),
                "and the archive names the build that wrote it");

            // The English page and the archive agree the same way.
            CombatStrings.Language = "en";
            ImpactBoardLayout englishBoard = ImpactBoard.Build(report, false, true);
            int englishRow = ToolRow(englishBoard);
            string englishCell = englishRow < 0 ? string.Empty : englishBoard.Joined(englishRow, 1);
            check(englishCell == "scan 2x / first 7 / cumulative 7",
                "the English board draws the same three fields: " + englishCell);
            string englishJson = ResultsLog.Build(true, new DateTime(2026, 12, 1, 9, 0, 0, DateTimeKind.Utc), "1.19.64",
                englishBoard, report, default, 7);
            check(englishJson.Contains(englishCell),
                "and the English archive stores that same text");
        }
        finally { CombatStrings.Language = previous; }
    }

    // ---- the shapes this delivery removed, asserted absent ---------------------------------------------------
    private static void TheOldShapesAreGone(Action<bool, string> check)
    {
        string previous = CombatStrings.Language;
        try
        {
            CombatStrings.Language = "zh";
            var equal = new ImpactInput { Name = "Equal", ToolName = "Bio Tracker" };
            equal.Counters.ScanUses = 2; equal.Counters.ScanUsesKnown = true;
            equal.Counters.ScanFirstMarks = 7; equal.Counters.ScanMarks = 7;
            check(Cell(equal) != "扫描 2 次 / 首次 7 只",
                "the two-clause board cell the old rule produced is gone: " + Cell(equal));
            var quiet = new ImpactInput { Name = "Quiet", ToolName = "Bio Tracker" };
            check(Cell(quiet) != "扫描 —",
                "and a scanner row no longer collapses to one dash: " + Cell(quiet));

            string strings = Source("Localization.cs");
            check(strings.Length > 0, "the strings file is readable from the test runner");
            check(!strings.Contains("SameMarkCount"),
                "the equality test that shortened the scan cell is gone from the strings file");
            check(strings.Contains("Pick(\"累计 \" + totalMarks + \" 只\""),
                "and the cumulative clause is printed unconditionally");
            check(strings.Contains("(estimated ? \"~\" : string.Empty) + Compound("),
                "the estimate marker still leads the cell exactly as before");

            string board = Source("ImpactBoard.cs");
            check(board.Length > 0, "the board file is readable from the test runner");
            check(!board.Contains("? counters.ScanUses > 0 || counters.ScanFirstMarks > 0 || counters.ScanUsesKnown"),
                "the gate that dropped an all-zero scan row to one dash is gone");
            check(board.Contains(": CombatStrings.ToolScanUnavailable;"),
                "and the row with no evidence still has a cell of its own");
        }
        finally { CombatStrings.Language = previous; }
    }

    // ---- 1.19.64: a slot that really held more than one weapon ---------------------------------------------
    //
    // The recorded Survival r3 session: slot 2 fired `收束器霰弹枪`, `HEL步枪` and `战斗霰弹枪`, slot 1 fired
    // `短管霰弹枪` and `独头弹霰弹枪`, and the board drew one name and one set of figures per slot while
    // `CombatPlayer.Total` counted every row. Nothing below is a sum: each cell keeps the drawn row's own figures,
    // and the rows the slot cannot draw are named, counted and archived as themselves.
    private static void ASlotWithMoreThanOneWeaponNamesTheOthers(Action<bool, string> check)
    {
        CombatPlayer player = TwoGunSlots();
        ImpactInput input = ProjectedInput(player);
        check(input.PrimaryOthers == 1 && input.SecondaryOthers == 2,
            "each gun slot counts the measured weapons the board cannot draw: " + input.PrimaryOthers + "/" + input.SecondaryOthers);
        check(input.SlotWeapons.Count == 5,
            "the carried rows are the drawn two plus the three the slots folded: " + input.SlotWeapons.Count);
        bool placeholderCarried = false, outsideGuns = false;
        for (int index = 0; index < input.SlotWeapons.Count; index++)
        {
            SlotWeapon weapon = input.SlotWeapons[index];
            if (weapon.ArchetypeId == 0) placeholderCarried = true;
            if (weapon.Slot != 1 && weapon.Slot != 2) outsideGuns = true;
        }
        check(!placeholderCarried, "a (slot, 0) placeholder row is never carried as a weapon of its own");
        check(!outsideGuns, "and nothing outside the two gun slots is carried");

        ImpactReport report = ImpactScoring.Evaluate(new[] { input }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        string previous = CombatStrings.Language;
        try
        {
            CombatStrings.Language = "zh";
            ImpactBoardLayout chinese = ImpactBoard.Build(report, false, true);
            check(chinese.Joined((int)ImpactRowKind.PrimaryWeapon, 1) == "短管霰弹枪（另有 1 把）",
                "the primary row says the slot held another measured weapon: " + chinese.Joined((int)ImpactRowKind.PrimaryWeapon, 1));
            check(chinese.Joined((int)ImpactRowKind.SecondaryWeapon, 1) == "收束器霰弹枪（另有 2 把）",
                "and the secondary row says how many its slot holds: " + chinese.Joined((int)ImpactRowKind.SecondaryWeapon, 1));
            string secondaryDamage = chinese.Joined((int)ImpactRowKind.SecondaryDamage, 1);
            check(secondaryDamage.StartsWith("643.9", StringComparison.Ordinal),
                "the figures beside the name are the drawn row's own, not the slot's: " + secondaryDamage);
            check(!secondaryDamage.Contains("2641.8") && !secondaryDamage.Contains("2,641.8"),
                "the two rows the slot cannot draw are never added into that cell: " + secondaryDamage);
            CombatStrings.Language = "en";
            ImpactBoardLayout english = ImpactBoard.Build(report, false, true);
            check(english.Joined((int)ImpactRowKind.SecondaryWeapon, 1) == "收束器霰弹枪 (+2 more)",
                "the English row carries the same count: " + english.Joined((int)ImpactRowKind.SecondaryWeapon, 1));
        }
        finally { CombatStrings.Language = previous; }

        // The player's own figures are what they always were: the total counts every row, and the score, rank and
        // title are decided from the same input as a board built without any of this.
        check(Math.Abs(player.Total.EnemyDamage - 2798.1) < 0.05,
            "the player total still counts every measured row: " + player.Total.EnemyDamage);
        string totalCell = ImpactBoard.MetricFields(report.Entries[0], ImpactMetric.TotalDamage, KillTier.Minor, false, true)[0];
        check(totalCell.StartsWith("2,798.1", StringComparison.Ordinal),
            "and the total-damage cell is that same total: " + totalCell);
        // The projection that decides the score never reads the carried rows: the same player scored from the same
        // input, once with the slot rows attached and once without, is the same score, rank and title.
        ImpactInput unattached = ProjectedInput(player, attach: false);
        ImpactReport unattachedReport = ImpactScoring.Evaluate(new[] { unattached }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        check(unattached.SlotWeapons.Count == 0 && unattached.PrimaryOthers == 0,
            "the comparison input carries nothing, as every pre-1.19.64 row did");
        check(unattachedReport.Entries[0].Score.Equals(report.Entries[0].Score)
            && unattachedReport.Entries[0].Rank == report.Entries[0].Rank
            && unattachedReport.Entries[0].Rating == report.Entries[0].Rating,
            "the slot's extra rows change no score, no rank and no title");
    }

    private static void ASlotWithOneWeaponIsUnchanged(Action<bool, string> check)
    {
        var player = new CombatPlayer(0xA22UL);
        player.Weapon(1, 0, "GearStandard");
        WeaponCombatStats primary = player.Weapon(1, 501, "突击步枪", 11, true);
        player.RecordAccuracy(primary, 20, 10, 2);
        player.RecordDamage(primary, 88.0, false);
        ImpactInput input = ProjectedInput(player);
        check(input.PrimaryOthers == 0 && input.SecondaryOthers == 0 && input.SlotWeapons.Count == 0,
            "a player whose slots each hold one weapon carries nothing at all");
        ImpactReport report = ImpactScoring.Evaluate(new[] { input }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        string previous = CombatStrings.Language;
        try
        {
            CombatStrings.Language = "zh";
            ImpactBoardLayout board = ImpactBoard.Build(report, false, true);
            check(board.Joined((int)ImpactRowKind.PrimaryWeapon, 1) == "突击步枪",
                "the single weapon's name row is the same cell it always was: " + board.Joined((int)ImpactRowKind.PrimaryWeapon, 1));
            check(board.Joined((int)ImpactRowKind.SecondaryWeapon, 1) == CombatStrings.Placeholder,
                "and a slot with no row keeps its dash: " + board.Joined((int)ImpactRowKind.SecondaryWeapon, 1));
            string json = ResultsLog.Build(true, new DateTime(2026, 12, 1, 10, 0, 0, DateTimeKind.Utc), "1.19.64",
                board, report, default, 4);
            check(!json.Contains("\"weapons\":"), "a single-weapon player writes no per-weapon block at all");
        }
        finally { CombatStrings.Language = previous; }
    }

    private static void DuplicatesAndPlaceholdersAreNotWeapons(Action<bool, string> check)
    {
        var player = new CombatPlayer(0xA33UL);
        WeaponCombatStats first = player.Weapon(2, 303, "收束器霰弹枪", 70, true);
        WeaponCombatStats again = player.Weapon(2, 303, "收束器霰弹枪", 70, true);
        check(ReferenceEquals(first, again), "the same weapon identity is one row, however many snapshots named it");
        player.RecordDamage(first, 100.0, false);
        player.RecordAccuracy(again, 10, 5, 0);
        check(first.Counters.EnemyDamage == 100.0 && first.Counters.ShotsFired == 10,
            "a second report of that identity lands on the same row instead of a second one");
        player.Weapon(2, 999, "收束器霰弹枪", 71, true);   // the same name under another archetype id
        player.Weapon(2, 0, "GearSpecial");                // the row the slot was opened with
        check(WeaponRows.Others(player.Weapons, 2, first).Count == 0,
            "a placeholder row and a same-name copy are not \"another weapon\" of that slot");
        ImpactInput input = ProjectedInput(player);
        check(input.SecondaryOthers == 0 && input.SlotWeapons.Count == 0,
            "so a slot whose extra rows are duplicates prints no marker and carries no block");
    }

    private static void TheArchiveCarriesEachObservedRow(Action<bool, string> check)
    {
        CombatPlayer player = TwoGunSlots();
        ImpactInput input = ProjectedInput(player);
        ImpactReport report = ImpactScoring.Evaluate(new[] { input }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        ImpactBoardLayout board = ImpactBoard.Build(report, false, true);
        string json = ResultsLog.Build(true, new DateTime(2026, 12, 1, 10, 0, 0, DateTimeKind.Utc), "1.19.64",
            board, report, default, 4);
        int at = json.IndexOf("\"weapons\": [", StringComparison.Ordinal);
        check(at >= 0, "the archive carries one entry per measured row of a folded slot");
        int end = at < 0 ? -1 : json.IndexOf("]", at, StringComparison.Ordinal);
        string block = at < 0 || end < 0 ? string.Empty : json.Substring(at, end - at);
        // One reproducible sample of the block in every suite log, so the file's own shape can be read without a run.
        Console.WriteLine("[WEAPONS] " + block.Replace("\n", " "));
        check(Occurrences(block, "\"slot\":") == 5, "five measured rows: the drawn two and the three folded ones");
        check(block.Contains("\"name\": \"HEL步枪\"") && block.Contains("\"damage\": 1897.9"),
            "each of those rows keeps its own figures: " + block);
        check(block.Contains("\"shown\": true") && block.Contains("\"shown\": false"),
            "and the row the cells belong to is marked as the drawn one");
        check(!block.Contains("2798.1") && !block.Contains("2641.8"),
            "neither the player total nor a slot sum is written into that block");
        using (JsonDocument document = JsonDocument.Parse(json))
        {
            JsonElement players = document.RootElement.GetProperty("players");
            JsonElement weapons = players[0].GetProperty("weapons");
            check(weapons.GetArrayLength() == 5, "the block is valid JSON with one object per measured row");
        }
    }

    // ---- 1.19.64: the scan figures, the known-count contract and the diagnostic line ------------------------
    private static void DaudaRowPrintsAllThreeScanFigures(Action<bool, string> check)
    {
        // The recorded r3 row read `~扫描 3 次 / 首次 26 只`: first and cumulative were equal and the cumulative
        // clause was dropped. It is printed now, and the estimate marker stays where it was.
        var dauda = new ImpactInput { Name = "Dauda", ToolName = "Bio Tracker" };
        dauda.Counters.ScanUses = 3;
        dauda.Counters.ScanEstimated = true;
        dauda.Counters.ScanFirstMarks = 26;
        dauda.Counters.ScanMarks = 26;
        string previous = CombatStrings.Language;
        try
        {
            CombatStrings.Language = "zh";
            string cell = Cell(dauda);
            check(cell == "~扫描 3 次 / 首次 26 只 / 累计 26 只",
                "the recorded Dauda row prints all three figures behind its marker: " + cell);
            check(Fields(cell) == 3, "and the cell is three fields: " + cell);
            CombatStrings.Language = "en";
            string english = Cell(dauda);
            check(english == "~scan 3x / first 26 / cumulative 26",
                "the English row is the same three figures: " + english);
        }
        finally { CombatStrings.Language = previous; }
    }

    private static void KnownUsesIsThisMachinesOwnLedger(Action<bool, string> check)
    {
        // `ScanUsesKnown` is not on the wire: the squad snapshot carries the two counts, the activation figure and the
        // estimate flag, and nothing sets a receiving row`s known flag. Only two places in the mod set it, and both
        // are this machine`s own scanner ledger - the holder`s readable cooldown and a bot`s readable scan start - so
        // a known count is never a remote number with no marks behind it. These two assertions are the contract the
        // board relies on; the behavioural checks below are what it prints.
        string sync = Source("SyncProtocol.cs");
        string session = Source("ScanSession.cs");
        check(sync.Length > 0 && !sync.Contains("ScanUsesKnown = true"),
            "the snapshot path never hands a row a known-count flag");
        check(Occurrences(session, "counters.ScanUsesKnown = true;") == 2,
            "exactly two producers set it, both in this machine's own scanner session");

        var knownZero = new ImpactInput { Name = "Holder", ToolName = "Bio Tracker" };
        knownZero.Counters.ScanUsesKnown = true;
        string previous = CombatStrings.Language;
        try
        {
            CombatStrings.Language = "zh";
            check(Cell(knownZero) == "扫描 0 次 / 首次 0 只 / 累计 0 只",
                "a holder whose cooldown was read prints its counted zero, and its own zero marks: " + Cell(knownZero));
        }
        finally { CombatStrings.Language = previous; }

        // A remote row: the owner's own machine reported three scans and no mark. Those figures arrived together in
        // one snapshot, so the zero is that machine's own count - it is not this build filling in a mark it never saw.
        var owner = new CombatPlayer(0xB1UL);
        owner.Impact.ScanUses = 3;
        SyncSnapshot snapshot = SyncProtocol.Capture(owner, 0, 1, 2, 3, 0, false);
        var received = new CombatPlayer(0xB1UL);
        SyncProtocol.Apply(received, snapshot, 0, 1, 2, 3);
        check(received.Impact.ScanUses == 3 && received.Impact.ScanFirstMarks == 0 && received.Impact.ScanMarks == 0
            && !received.Impact.ScanUsesKnown,
            "the received row carries the owner's count and the owner's marks, and no known flag of ours");
        var remote = new ImpactInput { Name = "Remote", ToolName = "Bio Tracker", Lookup = 0xB1UL, Counters = received.Impact };
        try
        {
            CombatStrings.Language = "zh";
            check(Cell(remote) == "扫描 3 次 / 首次 0 只 / 累计 0 只",
                "it prints that owner's own zero, because the same packet carried it: " + Cell(remote));
        }
        finally { CombatStrings.Language = previous; }

        // A row nothing at all was observed for keeps three labelled placeholders: no count is filled in for it,
        // which is the one thing this build must never do.
        var silent = new ImpactInput { Name = "Silent", ToolName = "Bio Tracker", Lookup = 0xB2UL };
        try
        {
            CombatStrings.Language = "zh";
            check(Cell(silent) == "扫描 — 次 / 首次 — 只 / 累计 — 只",
                "a row nobody measured prints three placeholders and no invented zero: " + Cell(silent));
        }
        finally { CombatStrings.Language = previous; }
    }

    private static void TheComparisonLineNamesBothMarkFigures(Action<bool, string> check)
    {
        ScanRoundEstimates.Reset(); ScanRoundComparison.Reset();
        const ulong guest = 0xC1UL;
        ScanRoundEstimates.Credit(true, false, false, guest, 50.0);
        ScanRoundEstimates.Credit(true, false, false, guest, 70.0);
        var row = new ImpactInput { Name = "Dauda", ToolName = "Bio Tracker" };
        row.Lookup = guest;
        row.Counters.ScanFirstMarks = 3; row.Counters.ScanMarks = 26;
        ImpactReport report = ImpactScoring.Evaluate(new[] { row }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        List<string> lines = ScanRoundComparison.Lines(report, 9, true);
        check(lines.Count == 1, "one line for the one player this host estimated");
        string line = lines.Count == 1 ? lines[0] : string.Empty;
        check(line.Contains("estimate=2"), "the estimate it prints is the ledger's own figure: " + line);
        check(line.Contains("firstMarks=3") && line.Contains("cumulativeMarks=26"),
            "both mark figures are named, so neither can be read as the other: " + line);
        check(!line.Contains("; marks="), "and no bare `marks=` label survives: " + line);
    }

    private static void TheBoardAndArchiveNoLongerFoldSilently(Action<bool, string> check)
    {
        // The pre-1.19.64 shapes, asserted absent: this block is what fails if the slot is folded back into one
        // name with no trace of the rows behind it.
        CombatPlayer player = TwoGunSlots();
        ImpactInput input = ProjectedInput(player);
        ImpactReport report = ImpactScoring.Evaluate(new[] { input }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        string previous = CombatStrings.Language;
        try
        {
            CombatStrings.Language = "zh";
            ImpactBoardLayout board = ImpactBoard.Build(report, false, true);
            check(board.Joined((int)ImpactRowKind.SecondaryWeapon, 1) != input.SecondaryName,
                "the secondary name row is no longer the bare name the old board drew: "
                + board.Joined((int)ImpactRowKind.SecondaryWeapon, 1));
            string json = ResultsLog.Build(true, new DateTime(2026, 12, 1, 10, 0, 0, DateTimeKind.Utc), "1.19.64",
                board, report, default, 4);
            check(json.Contains("\"weapons\": ["),
                "and the archive is no longer one folded key per slot with nothing behind it");
        }
        finally { CombatStrings.Language = previous; }
        check(Source("ResultsDisplay.cs").Contains("input.AttachSlotWeapons(player.Weapons, primary, secondary,"),
            "the settlement projection is the production one the board and the archive share");
        check(Source("ResultsLog.cs").Contains("AppendSlotWeapons(text, entry.Input);"),
            "and the archive writes that block from those same rows");
    }

    // ---- 1.19.64 (independent review): every figure carries the observation it came from ------------------
    //
    // The archive block used to write `0` for every figure it had not measured and carried no flags at all, so a row
    // that measured only damage read as a row with 0 shots, 0 hits and 0 damage, and a row whose damage was estimated
    // read as if its accuracy had been too. The block separates damage from accuracy, names the estimate per field,
    // and writes `null` for anything it never saw - while a counted zero keeps being the zero it is.
    private static void SlotWeaponEntriesKeepTheirObservation(Action<bool, string> check)
    {
        var player = new CombatPlayer(0xD1UL);
        player.Name = "Mixed";
        player.Weapon(2, 900, "空枪", 1, true);                     // drawn: counted nothing at all
        WeaponCombatStats damageOnly = player.Weapon(2, 901, "伤害枪", 2);
        player.RecordDamage(damageOnly, 120.5, false);
        WeaponCombatStats accuracyOnly = player.Weapon(2, 902, "命中枪", 3);
        player.RecordAccuracy(accuracyOnly, 20, 7, 2);
        WeaponCombatStats estimatedDamage = player.Weapon(2, 903, "估算枪", 4);
        player.RecordDamage(estimatedDamage, 33.0, false, true);    // damage estimated, accuracy measured
        player.RecordAccuracy(estimatedDamage, 10, 4, 1);
        WeaponCombatStats hitsNoShots = player.Weapon(2, 904, "命中无发射", 5);
        player.AdjustAccuracy(hitsNoShots, 0, 3, 1);                // hits seen, shot count never read (not estimated)
        WeaponCombatStats estimatedAccuracy = player.Weapon(2, 906, "盲算枪", 7);
        player.RecordDamage(estimatedAccuracy, 50.0, false);        // exact damage
        player.RecordAccuracy(estimatedAccuracy, 0, 2, 0, true);    // estimated accuracy

        ImpactInput input = ProjectedInput(player);
        check(input.SecondaryOthers == 5 && input.SlotWeapons.Count == 6,
            "the block carries the drawn row plus the five measured ones: " + input.SlotWeapons.Count);
        string previous = CombatStrings.Language;
        try
        {
            CombatStrings.Language = "zh";
            ImpactReport report = ImpactScoring.Evaluate(new[] { input }, ImpactScoring.DefaultWeights, 1.0, 1.0);
            ImpactBoardLayout board = ImpactBoard.Build(report, false, true);
            string json = ResultsLog.Build(true, new DateTime(2026, 12, 1, 11, 0, 0, DateTimeKind.Utc), "1.19.64",
                board, report, default, 5);
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                Dictionary<string, JsonElement> entries = SlotWeaponEntries(document);
                check(entries.Count == 6, "every carried row is one JSON entry: " + entries.Count);

                // A row that measured nothing: every figure null, and neither field called an estimate.
                JsonElement silent = entries["空枪"];
                check(IsNull(silent, "damage") && IsNull(silent, "shots") && IsNull(silent, "hits") && IsNull(silent, "weakspots"),
                    "a row that measured nothing writes null for every figure: " + silent);
                check(!silent.GetProperty("damageEstimated").GetBoolean() && !silent.GetProperty("accuracyEstimated").GetBoolean(),
                    "and no field of it is marked as estimated");

                // Damage only: its own damage, and no accuracy figures at all instead of three zeros.
                JsonElement damage = entries["伤害枪"];
                check(IsNumber(damage, "damage", 120.5) && !damage.GetProperty("damageEstimated").GetBoolean(),
                    "a damage-only row writes its damage as the measured figure it is: " + damage);
                check(IsNull(damage, "shots") && IsNull(damage, "hits") && IsNull(damage, "weakspots"),
                    "and writes no accuracy figures rather than three zeros: " + damage);

                // Accuracy only: the mirror image.
                JsonElement accuracy = entries["命中枪"];
                check(IsNull(accuracy, "damage") && IsNumber(accuracy, "shots", 20) && IsNumber(accuracy, "hits", 7)
                    && IsNumber(accuracy, "weakspots", 2),
                    "an accuracy-only row writes its three figures and no damage: " + accuracy);
                check(!accuracy.GetProperty("damageEstimated").GetBoolean() && !accuracy.GetProperty("accuracyEstimated").GetBoolean(),
                    "with no estimate flag on either field");

                // The estimate is named per field, in both directions.
                JsonElement estimatedRow = entries["估算枪"];
                check(estimatedRow.GetProperty("damageEstimated").GetBoolean()
                    && !estimatedRow.GetProperty("accuracyEstimated").GetBoolean() && IsNumber(estimatedRow, "shots", 10),
                    "an estimated damage is flagged on damage alone, never on the accuracy beside it: " + estimatedRow);
                JsonElement blindAccuracyRow = entries["盲算枪"];
                check(!blindAccuracyRow.GetProperty("damageEstimated").GetBoolean()
                    && blindAccuracyRow.GetProperty("accuracyEstimated").GetBoolean()
                    && IsNumber(blindAccuracyRow, "damage", 50.0),
                    "and an estimated accuracy is flagged on accuracy alone: " + blindAccuracyRow);

                // Hits without a readable shot count: the hits are kept, the shot figure is absent.
                JsonElement noShots = entries["命中无发射"];
                check(IsNull(noShots, "shots") && IsNumber(noShots, "hits", 3) && IsNumber(noShots, "weakspots", 1),
                    "hits seen without a shot sample keep the hits and write no shots figure: " + noShots);

                // A counted zero is a zero, in every field. The slot's drawn row here counted 0 damage and 0/0/0, so
                // each of its figures is the measured zero it is - never null, and never called an estimate.
                var zeroPlayer = new CombatPlayer(0xD2UL);
                zeroPlayer.Name = "Zero";
                WeaponCombatStats zeroDrawn = zeroPlayer.Weapon(2, 900, "零枪", 1, true);
                zeroPlayer.RecordDamage(zeroDrawn, 0.0, false);
                zeroPlayer.RecordAccuracy(zeroDrawn, 0, 0, 0);
                WeaponCombatStats zeroOther = zeroPlayer.Weapon(2, 901, "补枪", 2);
                zeroPlayer.RecordDamage(zeroOther, 12.0, false);
                ImpactInput zeroInput = ProjectedInput(zeroPlayer);
                check(zeroInput.SecondaryOthers == 1 && zeroInput.SlotWeapons.Count == 2,
                    "the counted-zero row is the drawn one, and its slot carries one other measured weapon");
                ImpactReport zeroReport = ImpactScoring.Evaluate(new[] { zeroInput }, ImpactScoring.DefaultWeights, 1.0, 1.0);
                string zeroJson = ResultsLog.Build(true, new DateTime(2026, 12, 1, 11, 0, 0, DateTimeKind.Utc), "1.19.64",
                    ImpactBoard.Build(zeroReport, false, true), zeroReport, default, 5);
                using (JsonDocument zeroDocument = JsonDocument.Parse(zeroJson))
                {
                    JsonElement zero = SlotWeaponEntries(zeroDocument)["零枪"];
                    check(IsNumber(zero, "damage", 0.0) && IsNumber(zero, "shots", 0) && IsNumber(zero, "hits", 0)
                        && IsNumber(zero, "weakspots", 0),
                        "a counted zero stays a zero in every field: " + zero);
                    check(!zero.GetProperty("damageEstimated").GetBoolean() && !zero.GetProperty("accuracyEstimated").GetBoolean(),
                        "and a counted zero is not an estimate either: " + zero);
                }
            }
        }
        finally { CombatStrings.Language = previous; }
    }

    private static void AttachingSlotWeaponsTwiceIsIdempotent(Action<bool, string> check)
    {
        CombatPlayer player = TwoGunSlots();
        ImpactInput input = ProjectedInput(player);
        int firstCount = input.SlotWeapons.Count;
        string first = SlotWeaponJson(input);
        check(first.Length > 0, "the first call writes the block");
        input.AttachSlotWeapons(player.Weapons, WeaponRows.For(player.Weapons, 1), WeaponRows.For(player.Weapons, 2), 1, 2);
        check(input.SlotWeapons.Count == firstCount && input.PrimaryOthers == 1 && input.SecondaryOthers == 2,
            "attaching a second time rewrites the same rows instead of appending a copy: " + input.SlotWeapons.Count);
        check(SlotWeaponJson(input) == first,
            "and the block of the second call is the block of the first, byte for byte");
    }

    // The `weapons` block of one input's archive, exactly as `ResultsLog` writes it, plus the JSON readers the review's
    // assertions use. They read the file the settlement saver would write, not an intermediate string of our own.
    private static string SlotWeaponJson(ImpactInput input)
    {
        ImpactReport report = ImpactScoring.Evaluate(new[] { input }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        ImpactBoardLayout board = ImpactBoard.Build(report, false, true);
        string json = ResultsLog.Build(true, new DateTime(2026, 12, 1, 11, 0, 0, DateTimeKind.Utc), "1.19.64",
            board, report, default, 5);
        int at = json.IndexOf("\"weapons\": [", StringComparison.Ordinal);
        int end = at < 0 ? -1 : json.IndexOf("]", at, StringComparison.Ordinal);
        return at < 0 || end < 0 ? string.Empty : json.Substring(at, end - at);
    }

    private static Dictionary<string, JsonElement> SlotWeaponEntries(JsonDocument document)
    {
        var entries = new Dictionary<string, JsonElement>();
        JsonElement weapons = document.RootElement.GetProperty("players")[0].GetProperty("weapons");
        foreach (JsonElement entry in weapons.EnumerateArray())
            entries[entry.GetProperty("name").GetString() ?? string.Empty] = entry;
        return entries;
    }

    private static bool IsNull(JsonElement entry, string name)
        => entry.GetProperty(name).ValueKind == JsonValueKind.Null;

    private static bool IsNumber(JsonElement entry, string name, double expected)
    {
        JsonElement value = entry.GetProperty(name);
        return value.ValueKind == JsonValueKind.Number && Math.Abs(value.GetDouble() - expected) < 0.05;
    }

    // ---- the recorded session's rows, built the way the settlement projection builds them -------------------
    private static CombatPlayer TwoGunSlots()
    {
        var player = new CombatPlayer(0xA11UL);
        player.Name = "Brav0";
        player.Weapon(1, 0, "GearStandard");
        WeaponCombatStats shortShell = player.Weapon(1, 101, "短管霰弹枪", 51, true);
        player.RecordAccuracy(shortShell, 120, 48, 0);
        player.RecordDamage(shortShell, 116.3, false);
        WeaponCombatStats slug = player.Weapon(1, 202, "独头弹霰弹枪", 44);
        player.RecordAccuracy(slug, 10, 6, 1);
        player.RecordDamage(slug, 40.0, false);
        player.Weapon(2, 0, "GearSpecial");
        WeaponCombatStats choke = player.Weapon(2, 303, "收束器霰弹枪", 70, true);
        player.RecordAccuracy(choke, 192, 151, 0);
        player.RecordDamage(choke, 643.9, false);
        WeaponCombatStats hel = player.Weapon(2, 404, "HEL步枪", 18);
        player.RecordAccuracy(hel, 1, 1, 0);
        player.RecordDamage(hel, 1897.9, false);
        WeaponCombatStats combat = player.Weapon(2, 505, "战斗霰弹枪", 45);
        player.RecordAccuracy(combat, 6, 6, 0);
        player.RecordDamage(combat, 100.0, false);
        return player;
    }

    // The same four steps `ResultsDisplay.BuildReport` takes for one player, minus the Unity page it needs: the row
    // `WeaponRows.For` draws for each gun slot, the input the board is built from, and the slot rows that projection
    // carries. The board and the archive below are then the production ones.
    private static ImpactInput ProjectedInput(CombatPlayer player, bool attach = true)
    {
        WeaponCombatStats primary = WeaponRows.For(player.Weapons, 1);
        WeaponCombatStats secondary = WeaponRows.For(player.Weapons, 2);
        ImpactInput input = ImpactInput.From(player.Name, player.Total, player.Impact, false,
            primary != null ? primary.Counters : null, secondary != null ? secondary.Counters : null);
        input.PrimaryName = primary != null ? primary.Name : string.Empty;
        input.SecondaryName = secondary != null ? secondary.Name : string.Empty;
        if (attach) input.AttachSlotWeapons(player.Weapons, primary, secondary, 1, 2);
        return input;
    }

    private static int Occurrences(string text, string needle)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(needle)) return 0;
        int count = 0, at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
        return count;
    }
    // ---- helpers -------------------------------------------------------------------------------------------
    private static ImpactBoardLayout Board(ImpactInput input)
        => ImpactBoard.Build(ImpactScoring.Evaluate(new[] { input }, ImpactScoring.DefaultWeights, 1.0, 1.0), false, true);

    private static int ToolRow(ImpactBoardLayout board)
    {
        string label = CombatStrings.MetricLabel(ImpactMetric.ToolEfficiency);
        for (int row = 1; row < board.Rows; row++)
            if (string.Equals(board.Joined(row, 0), label, StringComparison.Ordinal)) return row;
        return -1;
    }

    // The cell the settlement board draws for this row, read off the board itself rather than off the formatter.
    private static string Cell(ImpactInput input)
    {
        ImpactBoardLayout board = Board(input);
        int row = ToolRow(board);
        return row < 0 ? string.Empty : board.Joined(row, 1);
    }

    private static int Fields(string cell)
        => string.IsNullOrEmpty(cell) ? 0 : cell.Split(CombatStrings.CellSeparator).Length;

    private static string Source(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj")))
            directory = directory.Parent;
        return directory == null ? string.Empty : File.ReadAllText(Path.Combine(directory.FullName, file));
    }
}
