using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HawChow.GTFOCombatPanel;

// 1.19.62: one squad-wide first reveal, and the round's created-enemy figure - on the page, not in a log.
//
// The recorded Survival FX.3 host archive read human 21 + bots 21 / 21 / 15 = 78 first marks for a round whose
// command spawns created 21 enemies: the tracker built a `ScanReveals` per player, so every tracker was "first"
// inside a namespace of its own and the same enemy was claimed four times. The fix is the production factory
// `SquadScanLedger` - the session source `CombatTracker.Ledger` now uses - and this file drives that factory
// rather than hand-built sessions, so a regression that puts the per-player ledger back turns these assertions red.
//
// The second half is the round's created-enemy figure: one global count, fed by each enemy's own successful
// `EnemyAgent.Setup`, independent of kills, scans and survival. It reaches the screen as one line on the settlement
// header (`ImpactBoardLayout.Figure` -> `ResultChromeLayout.PlanHeader` -> `DrawChromeLabel`), and the archive under
// its own `spawns` object with the same value, or the explicit unknown instead of a guessed zero.
internal static class Release11962Scenarios
{
    private const int Enemies = 21;
    // Four seats, interleaved: the recorded squad was one human and three bots.
    private static readonly ulong[] Seats = { 0x11UL, 0x22UL, 0x33UL, 0x44UL };
    private const int FirstEnemy = 1000;

    internal static void RunInto(Action<bool, string> check)
    {
        SquadRevealIsOneForever(check);
        TheFactoryIsTheOnlyRevealSource(check);
        CreatedEnemyFigure(check);
        TheWholeRoundReachesTheDisplayAndTheArchive(check);
        ARoundWhoseRoleMovedIsNotTheSquadTotal(check);
        TheSettlementSeesOneGlobalFigure(check);
        TheFigureIsOnTheBoardOnce(check);
        TheHeaderCarriesTheFigure(check);
        TheArchiveCarriesTheSameFigure(check);
        APendingHumanTagKeepsItsFirstMark(check);
        TheFigureCannotBreakTheDeathPath(check);
        TheWiringIsTheProductionOne(check);
    }

    // ---- one squad reveal, four interleaved players, twenty-one enemies ------------------------------
    //
    // The production factory, not a hand-built session: 21 enemies, four players sweeping in an interleaved order,
    // every enemy re-swept by somebody else afterwards, four enemies nobody ever tags and no kill anywhere. The
    // first marks must add up to the 21 enemies the squad really revealed - not to 78 - while each player keeps
    // the cumulative and scan figures their own sweeps produced.
    private static void SquadRevealIsOneForever(Action<bool, string> check)
    {
        var squad = new SquadScanLedger();
        var sessions = new ScanSession[Seats.Length];
        var counters = new ImpactCounters[Seats.Length];
        var swept = new HashSet<int>[Seats.Length];
        for (int seat = 0; seat < Seats.Length; seat++)
        {
            sessions[seat] = squad.For(Seats[seat]);
            counters[seat] = new ImpactCounters();
            swept[seat] = new HashSet<int>();
        }
        check(sessions[0] != sessions[1] && squad.For(Seats[0]) == sessions[0],
            "the factory hands one session per seat, and the same seat always gets the same session");

        // Pass one: seat (index % 4) reveals its own enemies, 21 of them, interleaved.
        var owner = new Dictionary<int, int>();
        for (int index = 0; index < Enemies; index++)
        {
            int seat = index % Seats.Length;
            int enemy = FirstEnemy + index;
            owner[enemy] = seat;
            swept[seat].Add(enemy);
            check(sessions[seat].MarkEnemy(enemy, counters[seat]),
                "enemy " + enemy + " is a first mark for seat " + seat + ", the sweep that revealed it");
        }

        // Pass two: every enemy is swept again, twice, by two other seats. A repeat is a repeat for the squad.
        for (int index = 0; index < Enemies; index++)
        {
            int enemy = FirstEnemy + index;
            for (int offset = 1; offset <= 2; offset++)
            {
                int seat = (owner[enemy] + offset) % Seats.Length;
                swept[seat].Add(enemy);
                ScanMark mark = sessions[seat].Observe(enemy, counters[seat]);
                check(mark == ScanMark.Repeat,
                    "seat " + seat + " sweeping enemy " + enemy + " again is a squad repeat, not a second first mark");
            }
        }

        long firstMarks = counters.Sum(counter => counter.ScanFirstMarks);
        long marks = counters.Sum(counter => counter.ScanMarks);
        check(firstMarks == Enemies,
            "the squad's first marks are the enemies it really revealed: " + firstMarks + " of " + Enemies
            + " (the recorded per-player ledgers read 78 here)");
        check(counters.All(counter => counter.ScanFirstMarks <= Enemies),
            "no seat can claim more first marks than the round had enemies");
        for (int seat = 0; seat < Seats.Length; seat++)
            check(counters[seat].ScanMarks == swept[seat].Count,
                "seat " + seat + " keeps its own cumulative marks (" + counters[seat].ScanMarks + " = the "
                + swept[seat].Count + " enemies it swept), which exclusive first marks never move");
        check(marks >= firstMarks,
            "the cumulative figure is not held down to the first-mark figure: " + marks + " >= " + firstMarks);
        check(counters.All(counter => counter.ScanUses == 0 && !counter.ScanEstimated),
            "a mark is not an activation: exclusivity moves neither the scan count nor the estimate flag of a human sweep");

        // Enemies nobody tagged (created, never tagged, never killed) cannot appear in any first-mark figure.
        check(firstMarks == Enemies && squad.Count == Enemies,
            "enemies nobody tagged are nobody's first mark: " + squad.Count + " revealed of " + (Enemies + 4) + " created");

        // The next expedition is a fresh namespace: the same enemy is a first mark again, and every figure restarts.
        squad.Clear();
        check(squad.Count == 0, "an expedition reset empties the reveal namespace");
        var nextCounters = new ImpactCounters();
        ScanSession next = squad.For(Seats[0]);
        check(next.MarkEnemy(FirstEnemy, nextCounters) && nextCounters.ScanFirstMarks == 1,
            "after the reset the same enemy is a first mark for the new round");
        check(nextCounters.ScanMarks == 1 && nextCounters.ScanUses == 0,
            "and the new round starts at its own cumulative and scan figures");

        // A bot's marks stay an estimate, exactly as they were: `BeginBotTag` marks the row and the board prints it
        // with `~`. What changed is only that the squad's namespace decides who was first.
        var botCounters = new ImpactCounters();
        ScanSession bot = squad.For(Seats[1]);
        bot.BeginBotTag(1f, 8.5f, botCounters);
        check(botCounters.ScanEstimated,
            "a bot's sweep is still an estimate and is still printed with a tilde");
        check(!bot.MarkEnemy(FirstEnemy, botCounters),
            "the bot sweeping an enemy the squad already revealed claims no first mark");
        check(botCounters.ScanFirstMarks == 0 && botCounters.ScanMarks == 1 && botCounters.ScanUses == 0,
            "the bot keeps its own cumulative mark and invents no activation");
        check(bot.MarkEnemy(FirstEnemy + 1, botCounters) && botCounters.ScanFirstMarks == 1,
            "an enemy the squad had not revealed is still a first mark for the bot that swept it");
    }

    // ---- the production factory is the only reveal source -------------------------------------------
    //
    // The old design is reproduced here as the mutation it is: two sessions built without the factory each own a
    // `ScanReveals`, so one enemy becomes two first marks. That is the red half; the factory is the green half.
    private static void TheFactoryIsTheOnlyRevealSource(Action<bool, string> check)
    {
        var one = new ImpactCounters();
        var two = new ImpactCounters();
        new ScanSession().MarkEnemy(5001, one);
        new ScanSession().MarkEnemy(5001, two);
        check(one.ScanFirstMarks == 1 && two.ScanFirstMarks == 1,
            "two sessions that do not share a namespace both claim the same enemy - the per-player ledger this release removes");

        var squad = new SquadScanLedger();
        var first = new ImpactCounters();
        var second = new ImpactCounters();
        squad.For(Seats[0]).MarkEnemy(5001, first);
        squad.For(Seats[1]).MarkEnemy(5001, second);
        check(first.ScanFirstMarks == 1 && second.ScanFirstMarks == 0,
            "the factory gives the squad one first mark for one enemy, and the second sweep is a repeat");
        check(second.ScanMarks == 1,
            "while the second seat still counts it cumulatively inside its own sweep window");
        check(squad.Count == 1, "and the shared namespace remembers exactly one revealed enemy");

        var nameless = squad.For(0);
        var namelessCounters = new ImpactCounters();
        nameless.MarkEnemy(5002, namelessCounters);
        check(namelessCounters.ScanFirstMarks == 1 && squad.Count == 1,
            "a nameless session neither reads nor writes the squad's revealed set");
    }

    // ---- the round's created-enemy figure ----------------------------------------------------------
    //
    // 1.19.62 (review of the previous round): the figure is driven by the production lifecycle calls, in the order
    // `CombatTracker` makes them - `ObserveState`'s reset edge is `Spawns.Begin`, `ObserveEnemySpawn` is `Spawns.Observe`,
    // `Tick` is `NoteRole`, the settlement's `SpawnTotal` is `Total`. Nothing here reaches into the ledger's state; the
    // sequence below is the round as the game reports it.
    private static void CreatedEnemyFigure(Action<bool, string> check)
    {
        // The host, from the round's own start: the `Generating` edge, before the level exists.
        var host = new SquadSpawnLedger();
        host.Begin(isMaster: true, fromGenerating: true);
        check(host.Armed && host.FromGenerating, "the Generating edge arms the round on this machine");
        check(host.Total(true).Scope == SpawnScope.Squad && host.Total(true).Created == 0,
            "a proven round with nothing created yet is a real zero, not an unknown");

        // An enemy the level sets up BEFORE its first in-level frame. The old rule dropped exactly this and then could
        // never claim the whole level; it is this round's enemy like any other.
        check(host.Observe(true, 500), "a preset enemy set up before the level's first frame is this round's enemy");
        // The level's first frame: the role is sampled, and the round is not cleared.
        host.NoteRole(true);
        check(host.Count == 1 && host.FromGenerating,
            "Generating -> InLevel is the same round: entering the level clears nothing");

        // Then the dynamic waves of the recorded round: 7 + 8 + 6.
        int identity = 7000;
        foreach (int wave in new[] { 7, 8, 6 })
            for (int index = 0; index < wave; index++)
                check(host.Observe(true, identity++), "wave " + wave + ": enemy " + (identity - 1) + " was created");

        SpawnTotal total = host.Total(true);
        check(total.Created == 22 && total.Scope == SpawnScope.Squad && total.Known,
            "1 preset + 7 + 8 + 6 = the round's 22 created enemies, and the figure is the squad's: " + total.Created);

        check(!host.Observe(true, identity - 1), "a repeated Setup of one instance is not a second enemy");
        check(host.Total(true).Created == 22, "and the figure stays at 22");
        check(host.Observe(true, identity++), "a new instance at a recycled address is a new created enemy");
        check(host.Total(true).Created == 23, "and it counts: " + host.Total(true).Created);

        // The next round begins at its own edge and empties the figure.
        host.Begin(isMaster: true, fromGenerating: true);
        check(host.Count == 0 && host.Total(true).Scope == SpawnScope.Squad && host.Total(true).Created == 0,
            "a new expedition starts at its own count, and its zero is a measured zero");
        host.Observe(true, identity++);
        check(host.Total(true).Created == 1, "and counts only what that round created");

        // A machine that only walked into a running level: it begins there, and it can only report what it saw.
        var midRound = new SquadSpawnLedger();
        midRound.Begin(isMaster: true, fromGenerating: false);
        midRound.Observe(true, 8001);
        midRound.Observe(true, 8002);
        check(!midRound.FromGenerating && midRound.Total(true).Scope == SpawnScope.Local && midRound.Total(true).Created == 2,
            "a mid-round machine counts its own observations and never calls them the round's total");
        check(!midRound.Total(true).Known, "and it says so");

        // A machine that never saw a round edge at all: unknown, never a zero.
        var none = new SquadSpawnLedger();
        check(!none.Observe(true, 8101), "without a round edge nothing is counted as the round's");
        check(!none.Total(true).Any && !none.Total(true).Known,
            "so its figure is unknown: " + SettlementSpawns.CountText(none.Total(true)));
        check(SettlementSpawns.CountText(none.Total(true)) == CombatStrings.Placeholder,
            "and the placeholder is a dash, never a zero");

        // A client that was there for the whole round keeps its own observation - even when the number happens to match.
        var client = new SquadSpawnLedger();
        client.Begin(isMaster: false, fromGenerating: true);
        for (int index = 0; index < Enemies; index++) client.Observe(false, 8200 + index);
        check(client.Total(false).Scope == SpawnScope.Local && client.Total(false).Created == Enemies,
            "a client keeps its own 21 observations and labels them local");
        check(!client.Total(false).Known, "which is not the squad's total");

        // A machine that stopped being the host cannot publish the previous host's total.
        var migrated = new SquadSpawnLedger();
        migrated.Begin(isMaster: true, fromGenerating: true);
        migrated.Observe(true, 8301);
        check(migrated.Total(false).Scope == SpawnScope.Local,
            "a machine that is no longer the host publishes its own observation, not the round's total");

        var empty = new SquadSpawnLedger();
        check(!empty.Total(true).Any && !empty.Total(true).Known && empty.Count == 0,
            "a ledger that observed nothing answers unknown rather than zero");
        empty.Reset();
        check(!empty.Total(true).Any, "and a reset session claims nothing either");
    }

    // ---- the whole round, read back as the page and the archive show it ------------------------------
    //
    // The reviewer's sequence, end to end and on the production entries: `Generating` -> an early `Setup` -> `InLevel` ->
    // the 7/8/6 waves -> the settlement's `Total` -> the *real* board model and the *real* archive builder. The point is
    // that the number the player sees and the number the file keeps are the same number this lifecycle produced.
    private static void TheWholeRoundReachesTheDisplayAndTheArchive(Action<bool, string> check)
    {
        string language = CombatStrings.Language;
        try
        {
            CombatStrings.Language = "zh";
            var ledger = new SquadSpawnLedger();
            ledger.Begin(isMaster: true, fromGenerating: true);            // CombatTracker.ObserveState: the reset edge
            ledger.Observe(true, 4000);                                     // a preset enemy, before the level's first frame
            ledger.NoteRole(true);                                          // CombatTracker.Tick: the level's first frame
            int identity = 7000;
            foreach (int wave in new[] { 7, 8, 6 })
                for (int index = 0; index < wave; index++) ledger.Observe(true, identity++);
            SpawnTotal total = ledger.Total(true);                          // CombatTracker.SpawnTotal at settlement

            var inputs = new[]
            {
                new ImpactInput { Name = "A", Counters = new ImpactCounters { ScanFirstMarks = 7 } },
                new ImpactInput { Name = "B", Counters = new ImpactCounters { ScanFirstMarks = 8 } },
                new ImpactInput { Name = "C", Counters = new ImpactCounters { ScanFirstMarks = 6 } },
                new ImpactInput { Name = "D" },
            };
            foreach (bool succeeded in new[] { true, false })
            {
                string where = succeeded ? "success" : "failure";
                ImpactReport report = ImpactScoring.Evaluate(inputs, ImpactScoring.DefaultWeights, 1.0, 1.0);
                report.Spawns = total;                                      // what ResultsDisplay.BuildReport does
                ImpactBoardLayout board = ImpactBoard.Build(report, true, succeeded);
                check(board.Figure == "累计刷怪量：22",
                    "the " + where + " page shows the round's own figure: " + board.Figure);
                using (JsonDocument document = JsonDocument.Parse(ResultsLog.Build(succeeded,
                    new DateTime(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc), "1.19.62", board, report, default, 9)))
                {
                    JsonElement spawns = document.RootElement.GetProperty(SettlementSpawns.JsonKey);
                    check(spawns.GetProperty("created").GetInt64() == 22
                        && spawns.GetProperty("scope").GetString() == "squad"
                        && spawns.GetProperty("known").GetBoolean(),
                        "and the " + where + " archive keeps the same 22 as the squad's total");
                }
            }

            // The same round, on a machine that only walked into it: the page says so, and the file says so.
            var midRound = new SquadSpawnLedger();
            midRound.Begin(isMaster: true, fromGenerating: false);
            midRound.Observe(true, 9100);
            ImpactReport partial = ImpactScoring.Evaluate(inputs, ImpactScoring.DefaultWeights, 1.0, 1.0);
            partial.Spawns = midRound.Total(true);
            ImpactBoardLayout partialBoard = ImpactBoard.Build(partial, true, false);
            check(partialBoard.Figure == "累计刷怪量：1（本机观测）",
                "an incomplete round is marked as this machine's own observation on the page: " + partialBoard.Figure);
            using (JsonDocument document = JsonDocument.Parse(ResultsLog.Build(false,
                new DateTime(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc), "1.19.62", partialBoard, partial, default, 9)))
            {
                JsonElement spawns = document.RootElement.GetProperty(SettlementSpawns.JsonKey);
                check(spawns.GetProperty("created").GetInt64() == 1
                    && spawns.GetProperty("scope").GetString() == "local"
                    && !spawns.GetProperty("known").GetBoolean(),
                    "and the archive never calls it the squad's total");
            }
        }
        finally { CombatStrings.Language = language; }
    }

    // ---- a round whose role moved is never the squad's total ----------------------------------------
    //
    // 1.19.62 (review of the previous round): comparing only the round's first and last role would let a machine that was
    // demoted and promoted again inside one round publish a complete total it never had. The role is sampled every
    // in-level frame, so one disagreeing sample is enough to keep the figure honest.
    private static void ARoundWhoseRoleMovedIsNotTheSquadTotal(Action<bool, string> check)
    {
        var host = new SquadSpawnLedger();
        host.Begin(isMaster: true, fromGenerating: true);
        for (int index = 0; index < Enemies; index++) host.Observe(true, 9000 + index);
        check(host.Total(true).Scope == SpawnScope.Squad,
            "a host that owned the round's start and still hosts carries the squad's total");
        host.NoteRole(false);
        host.NoteRole(true);
        check(host.Total(true).Scope == SpawnScope.Local && host.Total(true).Created == Enemies,
            "host -> guest -> host is this machine's own observation, never a proven total");
        check(!host.Total(true).Known, "and it says it is not the squad's");

        // A role sample is only meaningful inside a round this machine began: before that it decides nothing.
        var early = new SquadSpawnLedger();
        early.NoteRole(false);
        check(!early.Unproven, "a role read before the round began decides nothing");
        early.Begin(isMaster: true, fromGenerating: true);
        early.Observe(true, 9500);
        check(early.Total(true).Scope == SpawnScope.Squad, "so the figure is still the squad's");

        // A machine that began mid-round cannot claim a total even when it is the host.
        var joined = new SquadSpawnLedger();
        joined.Begin(isMaster: true, fromGenerating: false);
        joined.Observe(true, 9600);
        check(joined.Total(true).Scope == SpawnScope.Local && !joined.Total(true).Known,
            "the host of a round it did not see begin publishes its own observation, not the round's total");
    }

    // ---- the settlement sees one global figure, and the board is untouched ---------------------------
    private static void TheSettlementSeesOneGlobalFigure(Action<bool, string> check)
    {
        var squadTotal = new SpawnTotal(21, SpawnScope.Squad);
        string squadLine = SettlementSpawns.Describe(squadTotal);
        check(squadLine.Contains("created=21") && squadLine.Contains("scope=squad"),
            "the settlement line prints the one figure and its scope: " + squadLine);
        check(squadLine.Contains("never a kill count") && squadLine.Contains("never a scan count"),
            "and says which question it answers, so it can never be read as a kill or a scan count");
        string localLine = SettlementSpawns.Describe(new SpawnTotal(15, SpawnScope.Local));
        check(localLine.Contains("created=15") && localLine.Contains("scope=local"),
            "a local observation prints its own number and says it is not the squad total");
        string unknownLine = SettlementSpawns.Describe(SpawnTotal.Unknown);
        check(unknownLine.Contains("created=" + CombatStrings.Placeholder) && unknownLine.Contains("scope=unknown"),
            "an unknown figure prints the dash: " + unknownLine);

        SettlementSpawns.Reset();
        var report = Report(SpawnScope.Squad, 21);
        string once = SettlementSpawns.Line(report, 7);
        check(once.Length > 0 && SettlementSpawns.Line(report, 7).Length == 0,
            "the settlement writes the line once per expedition, not once per save call");
        report.Spawns = new SpawnTotal(22, SpawnScope.Squad);
        check(SettlementSpawns.Line(report, 7).Length > 0,
            "and writes it again when the figure really changes");
        SettlementSpawns.Reset();
        report.Spawns = new SpawnTotal(21, SpawnScope.Squad);
        check(SettlementSpawns.Line(report, 8).Length > 0,
            "a new expedition starts its own line");

        ImpactBoardLayout board = ImpactBoard.Build(report, true, true);
        check(board.Rows == ImpactBoard.RowCount(true) && board.ColumnCount == report.Entries.Count + 1,
            "the settlement board keeps its row count and one column per player plus the label column");
        check(board.Joined(0, 0) == CombatStrings.ImpactRowLabels()[0] && board.Joined(0, 1).StartsWith("#1 "),
            "and its label column and its player cells still read exactly as they did");
    }

    // ---- the figure the player sees, on the real board model ----------------------------------------
    //
    // 1.19.62 (review of the previous round): the requirement is a *visible* line on both result pages, so the assertion
    // runs on the model the renderer consumes (`ImpactBoard.Build`) rather than on a log. It is one string for the whole
    // squad: a four-player board carries it exactly once, never once per column, and it never enters a cell - which is
    // why the board keeps the rows and columns it always had. The header band then draws that exact string.
    private static void TheFigureIsOnTheBoardOnce(Action<bool, string> check)
    {
        string language = CombatStrings.Language;
        try
        {
            var inputs = new[]
            {
                new ImpactInput { Name = "A", Counters = new ImpactCounters { ScanFirstMarks = 7 } },
                new ImpactInput { Name = "B", Counters = new ImpactCounters { ScanFirstMarks = 8 } },
                new ImpactInput { Name = "C", Counters = new ImpactCounters { ScanFirstMarks = 6 } },
                new ImpactInput { Name = "D", Counters = new ImpactCounters { ScanFirstMarks = 0 } },
            };

            foreach (string locale in new[] { "zh", "en" })
            {
                CombatStrings.Language = locale;
                ImpactReport report = ImpactScoring.Evaluate(inputs, ImpactScoring.DefaultWeights, 1.0, 1.0);
                report.Spawns = new SpawnTotal(21, SpawnScope.Squad);
                foreach (bool succeeded in new[] { true, false })
                {
                    string where = locale + "/" + (succeeded ? "success" : "failure");
                    ImpactBoardLayout board = ImpactBoard.Build(report, true, succeeded);
                    string expected = locale == "zh" ? "累计刷怪量：21" : "Spawned: 21";
                    check(board.Figure == expected,
                        "the " + where + " board carries the run's created-enemy figure as one line: " + board.Figure);
                    check(board.ColumnCount == 5,
                        "four players are four value columns plus the label column (" + where + ")");
                    int copies = 0;
                    for (int row = 0; row < board.Rows; row++)
                        for (int column = 0; column < board.ColumnCount; column++)
                            if (board.Joined(row, column) == board.Figure) copies++;
                    check(copies == 0,
                        "the figure is not a cell, so it is never repeated per column (" + where + ")");
                    check(board.Rows == ImpactBoard.RowCount(true),
                        "and the board keeps its row count - the figure is one line beside the grid, not a row of it (" + where + ")");
                }
            }

            CombatStrings.Language = "zh";
            ImpactReport local = ImpactScoring.Evaluate(inputs, ImpactScoring.DefaultWeights, 1.0, 1.0);
            local.Spawns = new SpawnTotal(15, SpawnScope.Local);
            check(ImpactBoard.Build(local, true, true).Figure == "累计刷怪量：15（本机观测）",
                "a figure this machine cannot prove says so on the page");
            CombatStrings.Language = "en";
            check(ImpactBoard.Build(local, true, false).Figure == "Spawned: 15 (this machine)",
                "and it says so in the build's English wording too");
            ImpactReport unknown = ImpactScoring.Evaluate(inputs, ImpactScoring.DefaultWeights, 1.0, 1.0);
            unknown.Spawns = SpawnTotal.Unknown;
            string unknownFigure = ImpactBoard.Build(unknown, true, false).Figure;
            check(unknownFigure == "Spawned: " + CombatStrings.Placeholder + " (unknown)",
                "an unprovable figure is the explicit unknown on the page, never a zero: " + unknownFigure);

            CombatStrings.Language = "zh";
            ImpactReport squadReport = ImpactScoring.Evaluate(inputs, ImpactScoring.DefaultWeights, 1.0, 1.0);
            squadReport.Spawns = new SpawnTotal(21, SpawnScope.Squad);
            ImpactBoardLayout squadBoard = ImpactBoard.Build(squadReport, true, true);
            using (JsonDocument document = JsonDocument.Parse(ResultsLog.Build(true,
                new DateTime(2026, 11, 27, 9, 0, 0, DateTimeKind.Utc), "1.19.62", squadBoard, squadReport, default, 3)))
            {
                JsonElement spawns = document.RootElement.GetProperty(SettlementSpawns.JsonKey);
                check(spawns.GetProperty("created").GetInt64() == 21
                    && squadBoard.Figure.EndsWith("21", StringComparison.Ordinal),
                    "the archived figure and the page's line are the same number");
                check(spawns.GetProperty("scope").GetString() == "squad" && squadBoard.Figure.Contains("累计刷怪量"),
                    "and the same claim about who may speak for the squad");
            }
        }
        finally { CombatStrings.Language = language; }
    }

    // ---- the line reaches the screen through the existing header draw --------------------------------
    //
    // The band already carries the name and the key hint, and the plan is what measures them (`TextFits` with the band's
    // own fonts). The figure is the third run: planned after the name, in the room the name left, drawn by the same
    // `DrawChromeLabel` call. A default of null is the pre-1.19.62 plan exactly, which keeps every recorded header
    // assertion honest and keeps the production call in `DrawChromeHeader` the only thing that passes a figure.
    private static void TheHeaderCarriesTheFigure(Action<bool, string> check)
    {
        ResultChromeMetrics metrics = ResultChromeMetrics.For(1920f, 1080f);
        var band = new LayoutRect(320f, 240f, 1280f, metrics.HeaderHeight);
        string figure = "累计刷怪量：21";
        ResultChromeLayout.ResultChromeHeaderPlan plan = ResultChromeLayout.PlanHeader(band, metrics, default, false, figure);
        // A concrete sample for the reviewer: the band's own description of the runs it just measured, on a recorded
        // 1920-wide plate. The figure rectangle is the one `DrawChromeHeader` paints with `DrawChromeLabel`.
        Console.WriteLine("[BOARD] settlement header: " + plan.Describe() + " figure=\"" + plan.FigureLabel + "\"");
        check(plan.Drawn && plan.HasFigure && plan.FigureLabel == figure,
            "the band plans the figure it was handed: " + (plan.HasFigure ? plan.FigureLabel : "none"));
        check(plan.HasTitle && plan.HasHint,
            "and the name and the key hint keep their own room: the figure never displaces them");
        float ink = ResultChromeLayout.TextEm(plan.FigureLabel) * metrics.HeaderHintFont;
        check(ink <= plan.Figure.Width + 0.001f && plan.Figure.X >= plan.Title.Right,
            "the figure's own box holds its ink and starts after the name, so nothing overlaps");
        check(!plan.HasHint || plan.Figure.Right <= plan.Hint.X + 0.001f,
            "and it ends before the key hint begins");

        ResultChromeLayout.ResultChromeHeaderPlan plain = ResultChromeLayout.PlanHeader(band, metrics, default, false);
        check(!plain.HasFigure && plain.FigureLabel == null && plain.HasTitle && plain.HasHint,
            "a caller that passes no figure gets the pre-1.19.62 band");
        check(plain.Title.X == plan.Title.X && plain.Title.Width == plan.Title.Width
            && plain.Hint.X == plan.Hint.X && plain.Yield == plan.Yield,
            "and the figure changed none of the existing runs");
        // 1.19.62 (review of the previous round): the figure's room must stop at the key hint. The plan's `right` is the
        // band's own edge, not the hint's - measuring the figure against it handed the figure the hint's rectangle on a
        // narrow band (the recorded defect). These two loops are the assertion: at every width and in both languages a
        // planned figure ends before the hint begins, and the figure is still planned at 715 px.
        foreach (string label in new[] { "累计刷怪量：21", "Spawned: 21" })
        {
            int dropped = 0, titles = 0;
            for (float width = 200f; width <= 620f; width += 20f)
            {
                var probe = new LayoutRect(320f, 240f, width, metrics.HeaderHeight);
                ResultChromeLayout.ResultChromeHeaderPlan withFigure = ResultChromeLayout.PlanHeader(probe, metrics, default, false, label);
                ResultChromeLayout.ResultChromeHeaderPlan without = ResultChromeLayout.PlanHeader(probe, metrics, default, false);
                if (!withFigure.HasFigure) dropped++;
                if (!withFigure.HasTitle) titles++;
                check(withFigure.Yield == without.Yield && withFigure.HasHint == without.HasHint
                    && withFigure.HasTitle == without.HasTitle && withFigure.TitleLabel == without.TitleLabel
                    && withFigure.Hint.X == without.Hint.X && withFigure.Title.X == without.Title.X,
                    width.ToString("0") + "px/" + label + ": the figure changes nothing but itself");
                check(!withFigure.HasFigure || !withFigure.HasHint || withFigure.Figure.Right <= withFigure.Hint.X + 0.001f,
                    width.ToString("0") + "px/" + label + ": the figure never runs into the key hint (figure.Right="
                    + withFigure.Figure.Right.ToString("0.#") + ", hint.X=" + withFigure.Hint.X.ToString("0.#") + ")");
                check(!withFigure.HasFigure || withFigure.Figure.X >= withFigure.Title.Right,
                    width.ToString("0") + "px/" + label + ": and never into the name it sits beside");
            }
            // The figure is the lowest-priority run: on a band too narrow for all three it is the one that goes, and the
            // name and the key hint stay exactly the plain plan's (asserted width by width above). Reported, not asserted:
            // how many of those widths really had to drop it.
            Console.WriteLine("[BOARD] " + label + ": figure planned on " + (22 - dropped) + " of the 22 widths 200..620 px"
                + " (name dropped on " + titles + "); 715 px always has room for all three");
            var wide = new LayoutRect(320f, 240f, 715f, metrics.HeaderHeight);
            ResultChromeLayout.ResultChromeHeaderPlan at715 = ResultChromeLayout.PlanHeader(wide, metrics, default, false, label);
            check(at715.HasFigure && at715.HasHint && at715.HasTitle
                && at715.Figure.Right <= at715.Hint.X + 0.001f && at715.Figure.X >= at715.Title.Right,
                "715px/" + label + ": the figure is planned and clear of both other runs: " + at715.Describe());
        }
        string display = ProductionText("ResultsDisplay.cs");
        check(display.Contains("_chromeToggleValid, _content != null ? _content.Figure : null);"),
            "the settlement's own header draw hands the plan the board's figure");
        check(display.Contains("if (plan.HasFigure) DrawChromeLabel(plan.Figure, metrics.HeaderHintFont, plan.FigureLabel, ResultChromeTheme.HeaderHint, false);"),
            "and paints it with the same label call the name and the key hint use");
        check(ProductionText("ImpactBoard.cs").Contains("Figure = SettlementSpawns.Figure(report.Spawns)"),
            "the board model fills that line from the one squad figure");
    }

    // ---- the archive carries the same value, or the explicit unknown ---------------------------------
    private static void TheArchiveCarriesTheSameFigure(Action<bool, string> check)
    {
        foreach (bool succeeded in new[] { true, false })
        {
            string where = succeeded ? "success" : "failure";
            ImpactReport squadReport = Report(SpawnScope.Squad, 21);
            using (JsonDocument document = JsonDocument.Parse(Archive(succeeded, squadReport)))
            {
                JsonElement spawns = document.RootElement.GetProperty(SettlementSpawns.JsonKey);
                check(spawns.GetProperty("created").GetInt64() == 21,
                    "the " + where + " archive stores the settlement's created-enemy figure (21)");
                check(spawns.GetProperty("scope").GetString() == "squad" && spawns.GetProperty("known").GetBoolean(),
                    "with the scope that says it is the squad's total (" + where + ")");
                JsonElement players = document.RootElement.GetProperty("players");
                check(players.GetArrayLength() == squadReport.Entries.Count,
                    "and every player row is still archived exactly once (" + where + ")");
                for (int index = 0; index < players.GetArrayLength(); index++)
                    check(players[index].TryGetProperty("counters", out _) && players[index].TryGetProperty("place", out _),
                        "player " + index + " keeps its own keys; the figure was never pushed into a row (" + where + ")");
            }

            ImpactReport localReport = Report(SpawnScope.Local, 15);
            using (JsonDocument document = JsonDocument.Parse(Archive(succeeded, localReport)))
            {
                JsonElement spawns = document.RootElement.GetProperty(SettlementSpawns.JsonKey);
                check(spawns.GetProperty("created").GetInt64() == 15
                    && spawns.GetProperty("scope").GetString() == "local"
                    && !spawns.GetProperty("known").GetBoolean(),
                    "an observation a machine cannot prove is archived as local, not as the squad's total (" + where + ")");
            }

            ImpactReport unknownReport = Report(SpawnScope.Unknown, 0);
            using (JsonDocument document = JsonDocument.Parse(Archive(succeeded, unknownReport)))
            {
                JsonElement spawns = document.RootElement.GetProperty(SettlementSpawns.JsonKey);
                check(spawns.GetProperty("created").ValueKind == JsonValueKind.Null,
                    "an unknown figure is archived as null, never as a guessed zero (" + where + ")");
                check(spawns.GetProperty("scope").GetString() == "unknown" && !spawns.GetProperty("known").GetBoolean(),
                    "and says so in both of its other fields (" + where + ")");
            }
        }
    }

    // ---- an executed human tag is applied before a bot's tag-state snapshot --------------------------
    //
    // 1.19.62 (review of the previous round): the old guard asked a *request* ledger that only a client ever fills
    // (`ScanTagPipeline.Request` runs only when `!SNet.IsMaster`), while a bot's tag only runs on the host - so it could
    // never fire. The rule is order now: `CreditBotScan` drains the executed-and-not-yet-credited confirmations before
    // it looks at the bot's snapshot, so a human tag the game really executed takes the squad's first reveal and the bot
    // sees a repeat. A bot that really tagged first keeps it, and the human's later execution is the repeat: the same
    // marks are credited either way, only the owner of the single first reveal follows the real order. The real-callback
    // proof (host `DoTagEnemy` -> bot on the same target -> drain) lives in `tests/GTFOCombatPanel.Scan`; this drives
    // both orders through the shared session and pins the production wiring.
    private static void APendingHumanTagKeepsItsFirstMark(Action<bool, string> check)
    {
        // Order A: the human's execution is applied first (that is the drain `CreditBotScan` now makes).
        var squad = new SquadScanLedger();
        var humanCounters = new ImpactCounters();
        check(squad.For(0x11UL).ObserveConfirmed(9001, humanCounters, true) == ScanMark.First,
            "the human's executed tag takes the squad's first reveal when it is applied first");
        var botCounters = new ImpactCounters();
        ScanSession bot = squad.For(0x22UL);
        bot.BeginBotTag(1f, 8.5f, botCounters);
        check(!bot.MarkEnemy(9001, botCounters) && botCounters.ScanFirstMarks == 0 && botCounters.ScanMarks == 1,
            "the bot's snapshot of the same enemy is a repeat: it keeps its own cumulative mark and takes no first mark");
        check(botCounters.ScanUses == 0 && botCounters.ScanEstimated,
            "and it invents no activation while the sweep stays an estimate");

        // Order B: the bot really tagged first - it keeps the first reveal, the human's later execution is the repeat.
        var reverse = new SquadScanLedger();
        var reverseBot = new ImpactCounters();
        ScanSession firstBot = reverse.For(0x22UL);
        firstBot.BeginBotTag(2f, 8.5f, reverseBot);
        check(firstBot.MarkEnemy(9001, reverseBot) && reverseBot.ScanFirstMarks == 1,
            "a bot that really tagged first keeps the squad's first reveal");
        var lateHuman = new ImpactCounters();
        check(reverse.For(0x11UL).ObserveConfirmed(9001, lateHuman, true) == ScanMark.Repeat
            && lateHuman.ScanFirstMarks == 0 && lateHuman.ScanMarks == 1,
            "and the human's later execution is the repeat, with its own cumulative mark");

        check(humanCounters.ScanMarks + botCounters.ScanMarks == 2 && reverseBot.ScanMarks + lateHuman.ScanMarks == 2,
            "both orders credit the same marks: only which side holds the single first mark follows the order");

        string tracker = ProductionText("CombatTracker.cs");
        int sweep = tracker.IndexOf("internal static void CreditBotScan(", StringComparison.Ordinal);
        int drain = sweep < 0 ? -1 : tracker.IndexOf("ScanTagPipeline.Drain();", sweep, StringComparison.Ordinal);
        int marks = sweep < 0 ? -1 : tracker.IndexOf("MarkEnemies(found, probe.Session", sweep, StringComparison.Ordinal);
        check(sweep >= 0 && drain > sweep && marks > drain,
            "the production bot sweep drains the executed confirmations before it looks at its own snapshot");
        check(!tracker.Contains("HasPendingClaim") && !tracker.Contains("ObserveDeferred"),
            "and the dead client-request guard is gone rather than left behind");
    }

    // ---- the figure sits beside the death accounting and cannot break it ----------------------------
    //
    // The `EnemyAgent.Setup` postfix is also what prepares a fresh death ledger entry for a pooled pointer. The new
    // figure has to be invisible to that: it runs after it, in its own guard, and its interop identity read is guarded on
    // its own so an unreadable enemy counts nothing instead of throwing the reset away.
    private static void TheFigureCannotBreakTheDeathPath(Action<bool, string> check)
    {
        string tracker = ProductionText("CombatTracker.cs");
        int start = tracker.IndexOf("internal static void ObserveEnemySpawn(EnemyAgent agent)", StringComparison.Ordinal);
        check(start >= 0, "the spawn hook is where this reads it");
        int end = tracker.IndexOf("private static void ResolvePendingEnemySpawn", start, StringComparison.Ordinal);
        check(end > start, "and it ends before the death-side resolver");
        if (start < 0 || end <= start) return;
        string body = tracker.Substring(start, end - start);
        int reset = body.IndexOf("PendingEnemySpawns.Add(agent.Pointer);", StringComparison.Ordinal);
        int resolve = body.IndexOf("ResolvePendingEnemySpawn(agent.Damage);", StringComparison.Ordinal);
        int figure = body.IndexOf("Spawns.Observe(", StringComparison.Ordinal);
        int guardedIdentity = body.IndexOf("LogImpact(\"enemy spawn identity\", error)", StringComparison.Ordinal);
        check(reset >= 0 && resolve > reset && figure > resolve,
            "the spawn reset the death path needs runs first, and the created-enemy figure is taken after it");
        check(guardedIdentity > 0 && guardedIdentity < figure,
            "the instance identity is read in its own guard, so an unreadable one counts nothing instead of throwing");
        check(body.Contains("Spawns.Observe(SNet.IsMaster, identity);"),
            "and the ledger is handed the guarded value, never a call that could throw inside it");
    }

    // ---- the wiring is the production one -----------------------------------------------------------
    //
    // The factory regression is a one-line one: the tracker's `Ledger` building a fresh reveal set per player is
    // exactly what the recorded 78 came from, so the source that must not come back is asserted here beside the
    // behaviour it produced above.
    private static void TheWiringIsTheProductionOne(Action<bool, string> check)
    {
        string tracker = ProductionText("CombatTracker.cs");
        check(tracker.Contains("probe = new ScanProbe(ScanSquad.For(owner.Lookup));"),
            "every scan probe takes its session from the one squad factory");
        check(!tracker.Contains("new ScanReveals()"),
            "and no probe builds a reveal namespace of its own - the per-player ledger that produced the 78 first marks");
        check(tracker.Contains("private static readonly SquadScanLedger ScanSquad = new SquadScanLedger();"),
            "the tracker holds exactly one reveal namespace for the expedition");
        // 1.19.62 (review of the previous round): the round's figure is BEGUN on the expedition reset edge - the missing
        // call that made every production figure 'unknown' - and its role is sampled on every in-level frame afterwards.
        check(tracker.Contains("Spawns.Begin(SNet.IsMaster, state == eGameStateName.Generating);"),
            "the expedition edge begins the round's figure, and says which edge raised it");
        check(tracker.Contains("Spawns.NoteRole(SNet.IsMaster);"),
            "and every in-level frame samples the role that figure belongs to");
        check(!tracker.Contains("Spawns.ObserveLevelStart"),
            "the old first-in-level-frame start (which dropped every preset spawn) is gone");
        check(tracker.Contains("internal static SpawnTotal SpawnTotal => Spawns.Total(SNet.IsMaster);"),
            "the settlement's figure is read from that one ledger");

        string display = ProductionText("ResultsDisplay.cs");
        check(display.Contains("report.Spawns = CombatTracker.SpawnTotal;"),
            "the settlement report both pages build carries the one global figure");
        check(display.Contains("Figure = board.Figure"),
            "and the table the renderer draws carries the board's figure line");
        string log = ProductionText("ResultsLog.cs");
        check(log.Contains("AppendSpawns(text, report.Spawns);"),
            "the archive writes it beside the player rows");
        string saver = ProductionText("ResultsSaver.cs");
        check(saver.Contains("string spawns = SettlementSpawns.Line(report, run);"),
            "and the settlement writes its one line from the same report");
    }

    // ---- helpers ------------------------------------------------------------------------------------

    // A real report with two players, as the settlement builds it.
    private static ImpactReport Report(SpawnScope scope, long created)
    {
        var inputs = new[]
        {
            new ImpactInput { Name = "A", Counters = new ImpactCounters { ScanFirstMarks = 7, ScanMarks = 9 } },
            new ImpactInput { Name = "B", Counters = new ImpactCounters { ScanFirstMarks = 8, ScanMarks = 14 } },
        };
        ImpactReport report = ImpactScoring.Evaluate(inputs, ImpactScoring.DefaultWeights, 1.0, 1.0);
        report.Spawns = new SpawnTotal(created, scope);
        return report;
    }

    // The real archive: the board the settlement builds plus `ResultsLog.Build`, exactly as `ResultsSaver.Save` calls it.
    private static string Archive(bool succeeded, ImpactReport report)
    {
        ImpactBoardLayout board = ImpactBoard.Build(report, true, succeeded);
        return ResultsLog.Build(succeeded, new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc), "1.19.62",
            board, report, default, 7);
    }

    private static string ProductionText(string name)
    {
        string root = SourceRoot();
        return root == null ? string.Empty : File.ReadAllText(Path.Combine(root, name));
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj")))
            directory = directory.Parent;
        return directory?.FullName;
    }
}
