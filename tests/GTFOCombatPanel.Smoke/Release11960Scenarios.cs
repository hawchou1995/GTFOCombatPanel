using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HawChow.GTFOCombatPanel;

// 1.19.60: the two rules the recorded 2026-11-24 client session moved out of the frame loop and onto the
// boundary where the question really belongs.
//
//   A. *which rows the public statistics may draw.* The recording put two seats that run no mod on the level HUD
//      with a bare row - dashes for a weapon, zeros for a kill - because this machine's own medkit had opened a row
//      for them in the capture store, and every row in that store was projected onto the board. The rule now lives
//      in `SquadRowEligibility`, asked at `SquadExchange.Project`, and the assertions below drive the *production*
//      chain a player's screen is built from: the capture store -> the projection -> `SettlementRows` -> the report
//      -> the board -> the archived JSON. `Project` alone is asserted only where its own answer is the question.
//
//   B. *the client's automatic first board.* `ResultEarlyBoardPolicy.Allows` gained `client`/`boundsFinal`, so a
//      client page's early board is fitted into the strip the mature board will use - the recorded 626 px strip at
//      12 px no longer becomes 534 px at 10 px a second later. The assertions below are the recorded change
//      sequence in both directions: the unsettled strip is refused and *would* have cost a re-measure, the final
//      strip is allowed and costs none, and nothing anywhere caps the glyph to make that true.
//
//   C. *the client failure page.* Checked, not changed: its upper edge is the live title, its lower wall is the
//      waiting text `m_returnToLobby_text`, a higher real interaction still tightens it, `PageFloor` only caps, and
//      an unmeasurable wall is still a refusal. The assertions pin that contract and pin the same row eligibility
//      on the failure board, which is the one thing this release does change there - through the shared projection.
internal static class Release11960Scenarios
{
    // ---- the recorded squad: an unmodded host, this machine, one modded peer, one unmodded teammate -----
    private const ulong Host = 0x11UL;          // the host, which runs no mod: it answers no packet at all
    private const ulong Local = 0x21UL;         // this machine: a client, CombatPanel installed
    private const ulong Peer = 0x31UL;          // the other client that really runs CombatPanel
    private const ulong Teammate = 0x41UL;      // the teammate who runs no mod either
    private const ulong Bot = 0x51UL;           // a bot seat, which only a host can measure

    internal static void RunInto(Action<bool, string> check)
    {
        AClientDrawsOnlyTheRowsItCanAccountFor(check);
        TheReceiversOwnLedgerSurvivesAndALateRowMergesOnIt(check);
        AHostKeepsEverySeatItMeasuredIncludingItsBot(check);
        AnUnverifiedPeerNeverPutsStatisticsOnTheBoard(check);
        TheFallbackNeverPutsABareRowBack(check);
        AClientBoardWaitsForTheFinalStripAndNeverShrinksOnce(check);
        TheSuccessGateHasNoMovingEdgeLeft(check);
        TheClientFailurePageKeepsItsOwnWallsAndOnlyCaps(check);
        TheFailureUpperBoundIsOneRealBaseline(check);
        TheArchiveWritesOneSupplyKeyPerPlayer(check);
        TheUnitySideIsWiredToTheseDecisions(check);
    }

    // ---------------------------------------------------------------------------------------------------
    // A: the row set, driven all the way to the archived JSON
    // ---------------------------------------------------------------------------------------------------
    //
    // The scenario is the recorded one, in the order the recording produced it:
    //   1. mid-run the local player applies *one* medkit to the unmodded teammate;
    //   2. later five more to the host;
    //   3. and on every frame in between the level HUD asks the projection for its rows.
    private static void AClientDrawsOnlyTheRowsItCanAccountFor(Action<bool, string> check)
    {
        SquadExchange exchange = ClientExchange(out CombatStatsStore store);

        // This machine's own row: the one row a client always measures itself.
        CombatPlayer own = store.GetOrCreate(Local);
        own.Name = "A1pha";
        own.DisplaySlot = 1;
        own.Total.AddAccuracy(40, 30, 6);
        own.Total.AddEnemyDamage(900);

        // The other modded client's own row, which really arrived this run. Its packet is the one the exchange
        // gate accepts: the sender is describing itself.
        CombatPlayer peerRow = new CombatPlayer(Peer) { Name = "柳", DisplaySlot = 2 };
        peerRow.Total.AddAccuracy(30, 20, 4);
        peerRow.Total.AddEnemyDamage(700);
        check(exchange.OnSnapshot(Peer, 1, SyncProtocol.Capture(peerRow, 0, 1, 2, 3, 0, false), 1f).Accepted,
            "the row the other installed client describes about itself is accepted, which is the row this machine may draw");

        // Nothing has been handed out yet: the two rows a client can account for.
        List<CombatPlayer> before = exchange.Project(store.Snapshot(), Local);
        check(before.Count == 2 && Row(before, Local) != null && Row(before, Peer) != null
              && Row(before, Host) == null && Row(before, Teammate) == null,
            "mid-run, before any pack is applied, a client draws exactly itself and the peer whose row it received");

        // 1: one medkit to the teammate who runs no mod. This is the credit the recording shows as `1`.
        check(ApplyPack(store, Local, Teammate) == 1,
            "the client's own medkit credits the unmodded teammate's row in the capture store");
        List<CombatPlayer> afterOne = exchange.Project(store.Snapshot(), Local);
        check(afterOne.Count == 2 && Row(afterOne, Teammate) == null,
            "and one applied medkit does not put that seat on the board: a supply credit is not statistical evidence");

        // 2: five more to the host, which is the other figure the recording shows.
        for (int pack = 0; pack < 5; pack++) ApplyPack(store, Local, Host);
        store.TryGet(Host, out CombatPlayer hostCapture);
        check(hostCapture != null && hostCapture.Impact.SelfMedicalUses == 5 && hostCapture.Impact.SelfMedicalKnown,
            "five medkits really were applied to the host in this run, so the case under test is the recorded one");
        List<CombatPlayer> afterFive = exchange.Project(store.Snapshot(), Local);
        check(afterFive.Count == 2 && Row(afterFive, Host) == null && Row(afterFive, Teammate) == null,
            "and five applied medkits do not put the host on the board either - the number of packs is not the question");

        // 3: the whole chain a player's screen and the archive are built from, on both result pages.
        var settlement = new SettlementRows();
        List<CombatPlayer> shown = settlement.Update(afterFive, 4);
        ImpactReport report = Report(shown);
        check(report != null && report.Entries.Count == 2,
            "the report the settlement page scores holds two players, not four: a bare row contributes nothing to any metric");
        check(report.Entries.TrueForAll(entry => entry.Input != null && (entry.Input.Lookup == Local || entry.Input.Lookup == Peer)),
            "and the two players in it are the ones this machine can account for");

        foreach (bool succeeded in new[] { true, false })
        {
            ImpactBoardLayout board = ImpactBoard.Build(report, true, succeeded);
            string json = ResultsLog.Build(succeeded, new DateTime(2026, 11, 24, 22, 18, 57, DateTimeKind.Utc),
                "1.19.60", board, report);
            using var document = JsonDocument.Parse(json);
            int players = document.RootElement.GetProperty("players").GetArrayLength();
            check(players == 2,
                "the archived settlement JSON of a " + (succeeded ? "success" : "failure") + " page holds two players, not four (found " + players + ")");
        }

        // The summary the board divides by is the two rows, and nothing was invented for the two it cannot see.
        double total = 0;
        foreach (CombatPlayer row in afterFive) total += Math.Max(0, row.Total.EnemyDamage);
        check(Math.Abs(total - 1600) < 0.01,
            "the squad total is the two rows this machine holds (900 + 700), with no contribution from a row that was never measured");
        check(Row(afterFive, Local) != null && Row(afterFive, Local).Impact.Kills == 0,
            "a real zero on a drawn row is still a zero - this rule deletes nothing and renames nothing");
        string[] peerCells = CombatStatsFormatter.Fields(Row(afterFive, Peer).Total, PanelColumn.Accuracy, null);
        check(peerCells.All(cell => !cell.Contains("NaN") && !cell.Contains("Infinity")),
            "and an admitted row is never rendered as a NaN or an infinity");

        // The rule itself, in the four answers it may give. Nothing here is a roster, a handshake, a pack count,
        // a zero or a name.
        check(SquadRowEligibility.Draws(true, false, false), "this machine's own row is always drawn");
        check(SquadRowEligibility.Draws(false, true, false), "a row this run really received is drawn");
        check(SquadRowEligibility.Draws(false, false, true), "and so is a seat this machine is itself the authority for");
        check(!SquadRowEligibility.Draws(false, false, false), "and nothing else is");
    }

    // ---------------------------------------------------------------------------------------------------
    // A: the receiver's own ledger is kept, and a trusted row that arrives late merges on top of it
    // ---------------------------------------------------------------------------------------------------
    private static void TheReceiversOwnLedgerSurvivesAndALateRowMergesOnIt(Action<bool, string> check)
    {
        SquadExchange exchange = ClientExchange(out CombatStatsStore store);
        CombatPlayer own = store.GetOrCreate(Local);
        own.Name = "A1pha";
        own.Total.AddEnemyDamage(900);

        ApplyPack(store, Local, Teammate);
        List<CombatPlayer> beforeLate = exchange.Project(store.Snapshot(), Local);
        check(beforeLate.Count == 1 && Row(beforeLate, Teammate) == null,
            "the unmodded teammate has no row on the board while all this machine holds is its own supply credit");

        // The credit is still where it was written: the row in the capture store is untouched by the projection.
        store.TryGet(Teammate, out CombatPlayer credited);
        check(credited != null && credited.Impact.SelfMedicalUses == 1 && credited.Impact.SelfMedicalKnown,
            "the receiver's raw supply ledger is preserved: the pack it received is still on its own row in the capture store");

        // A host that does run this build describes that seat a moment later - the qualified late row.
        CombatPlayer teammateRow = new CombatPlayer(Teammate) { Name = "Rook", DisplaySlot = 3 };
        teammateRow.Total.AddAccuracy(10, 8, 1);
        teammateRow.Total.AddEnemyDamage(120);
        check(exchange.OnSnapshot(Host, 1, SyncProtocol.Capture(teammateRow, 0, 1, 2, 3, 0, false), 12f).Accepted,
            "a row the authoritative host describes about a slotted teammate is accepted - the legal late row");

        List<CombatPlayer> afterLate = exchange.Project(store.Snapshot(), Local);
        CombatPlayer drawn = Row(afterLate, Teammate);
        check(afterLate.Count == 2 && drawn != null,
            "the late trusted row merges normally: the seat it describes is drawn from the frame it arrives on");
        check(Math.Abs(drawn.Total.EnemyDamage - 120) < 0.01 && drawn.Total.EnemyDamageObserved,
            "and the damage drawn for it is the host's measurement, not this machine's own supply bookkeeping");

        var settlement = new SettlementRows();
        List<CombatPlayer> shown = settlement.Update(afterLate, 5);
        CombatPlayer settled = shown.Find(row => row != null && row.Lookup == Teammate);
        ImpactReport report = Report(shown);
        string json = ResultsLog.Build(true, new DateTime(2026, 11, 24, 22, 18, 57, DateTimeKind.Utc), "1.19.60",
            ImpactBoard.Build(report, true, true), report);
        using var document = JsonDocument.Parse(json);
        check(document.RootElement.GetProperty("players").GetArrayLength() == 2 && settled != null
              && Tally(json, "\"place\":") == 2,
            "and the settlement page and its archive follow it: two players again, with the late row among them");
    }

    // ---------------------------------------------------------------------------------------------------
    // A: the authority keeps every seat it measured, bots included (this is what may not regress)
    // ---------------------------------------------------------------------------------------------------
    private static void AHostKeepsEverySeatItMeasuredIncludingItsBot(Action<bool, string> check)
    {
        var exchange = new SquadExchange();
        exchange.Configure(Host, 0, 1, 2, 3);
        exchange.SetRoster(new[]
        {
            new SquadPeer(Host, 0, true, false, true),
            new SquadPeer(Local, 1, false, false, false),
            new SquadPeer(Teammate, 2, false, false, false),
            new SquadPeer(Bot, 3, false, true, false),
        }, true);
        var store = new CombatStatsStore();
        ulong[] seats = { Host, Local, Teammate, Bot };
        for (int index = 0; index < seats.Length; index++)
        {
            CombatPlayer row = store.GetOrCreate(seats[index]);
            row.DisplaySlot = index;
            row.Total.AddEnemyDamage(100 * (index + 1));
        }
        List<CombatPlayer> rows = exchange.Project(store.Snapshot(), Host);
        check(rows.Count == 4,
            "a host draws every seat it really measured - four players including the bot, whose row no client could ever produce");
        check(Row(rows, Bot) != null && Math.Abs(Row(rows, Bot).Total.EnemyDamage - 400) < 0.01,
            "and the bot's own figures are the host's measurement of it");

        // The same four rows, from the same store, on a machine that is *not* the authority: only what a client
        // can account for survives. That is the whole difference the rule encodes.
        exchange.SetRoster(new[]
        {
            new SquadPeer(Host, 0, false, false, true),
            new SquadPeer(Local, 1, true, false, false),
            new SquadPeer(Teammate, 2, false, false, false),
            new SquadPeer(Bot, 3, false, true, false),
        }, false);
        store.GetOrCreate(Host).DisplaySlot = 0;
        List<CombatPlayer> demoted = exchange.Project(store.Snapshot(), Host);
        check(demoted.Count == 1 && Row(demoted, Host) != null,
            "a machine that is no longer the authority draws only its own row from that same store - a bare row can never leak back through a role change");
    }

    // ---------------------------------------------------------------------------------------------------
    // A: a peer that cannot prove itself puts no statistics on the board
    // ---------------------------------------------------------------------------------------------------
    private static void AnUnverifiedPeerNeverPutsStatisticsOnTheBoard(Action<bool, string> check)
    {
        SquadExchange exchange = ClientExchange(out CombatStatsStore store);
        store.GetOrCreate(Local).Total.AddEnemyDamage(500);

        // A client may only ever describe itself. This is the receiver-side gate, unchanged by this release: an
        // unproven peer describing a third player is refused, so no row exists for that seat.
        CombatPlayer invented = new CombatPlayer(Teammate) { Name = "NotVerified", DisplaySlot = 3 };
        invented.Total.AddEnemyDamage(4000);
        check(!exchange.OnSnapshot(Peer, 1, SyncProtocol.Capture(invented, 0, 1, 2, 3, 0, false), 2f).Accepted,
            "a peer that is not the host may not describe somebody else, so that row is refused at the gate");
        check(exchange.Project(store.Snapshot(), Local).Count == 1,
            "and no statistics reach the board for the seat nobody has proved anything about");

        // A peer outside the roster is refused too, so an unknown identity cannot appear as a row either.
        CombatPlayer stranger = new CombatPlayer(0x99UL) { Name = "Stranger" };
        stranger.Total.AddEnemyDamage(777);
        check(!exchange.OnSnapshot(0x99UL, 1, SyncProtocol.Capture(stranger, 0, 1, 2, 3, 0, false), 3f).Accepted,
            "a machine outside this roster is refused as well");
        check(exchange.Project(store.Snapshot(), Local).Count == 1,
            "and the board is still the one row this machine measured itself");
    }

    // ---------------------------------------------------------------------------------------------------
    // B: the client's first board is the final board, in both directions
    // ---------------------------------------------------------------------------------------------------
    private static void AClientBoardWaitsForTheFinalStripAndNeverShrinksOnce(Action<bool, string> check)
    {
        // The recorded change sequence, in the pixels the page produced: the early board was fitted into a 626 px
        // strip, and the mature board into a 534 px one after the completion section drew what its layout does not
        // include. The floor is the same table at the same row count.
        const float UnsettledRoom = 626f;
        const float FinalRoom = 534f;
        const float BoardFloor = 420f;

        check(!ResultEarlyBoardPolicy.Allows(true, true, true, false, UnsettledRoom, BoardFloor, hostProven: false, boundsFinal: false),
            "a client page whose own edges can still move does not enter early any more: that is the strip that produced 1151x540 and then 949x450");
        check(ResultEarlyBoardPolicy.Allows(true, true, true, false, FinalRoom, BoardFloor, hostProven: false, boundsFinal: true),
            "the same page, once its reveal has answered and its lower wall is a control it measured, allows the early board");
        check(ResultEarlyBoardPolicy.Allows(true, true, true, false, UnsettledRoom, BoardFloor, hostProven: true, boundsFinal: false),
            "and a host page is deliberately unchanged: it still enters early on the strip it can already measure");
        check(!ResultEarlyBoardPolicy.Allows(true, true, true, false, FinalRoom - BoardFloor, BoardFloor, hostProven: false, boundsFinal: true),
            "a final strip that cannot hold the table's own floor is still no place for any board");

        // Positive direction: with the final strip the first board and the mature board are the *same* fit, because
        // they are the same strip through the same function - so no re-measure is due afterwards.
        var drift = new ResultAnchorDrift();
        drift.Record(FinalRoom, FinalRoom);
        check(!drift.Due(FinalRoom, FinalRoom),
            "the mature measurement after an early board on the final strip is not a move, so no second fit is spent");

        // Negative direction: the old sequence (placed from the moving strip, measured again at the final one) is
        // exactly the move the player saw. The drift policy needs two samples before it spends a re-measure - which
        // is why this is asked twice here - and it then says yes: that re-fit is the shrink this release removes by
        // no longer placing the board on the moving strip in the first place.
        var oldSequence = new ResultAnchorDrift();
        oldSequence.Record(UnsettledRoom, UnsettledRoom);
        bool firstSample = oldSequence.Due(FinalRoom, FinalRoom);
        bool secondSample = oldSequence.Due(FinalRoom, FinalRoom);
        check(!firstSample && secondSample,
            "and the sequence this release removes - placed at 626, measured again at 534 - still registers as a move on its second sample, which is why it is refused");

        // The glyph is the strip's own answer in both placements, and nothing caps it.
        int earlyGlyph = ResultsBandFit.FontForBand(FinalRoom, 30, PanelDensity.Compact, 0);
        int matureGlyph = ResultsBandFit.FontForBand(FinalRoom, 30, PanelDensity.Compact, 0);
        check(earlyGlyph == matureGlyph && earlyGlyph > 0,
            "the first board and the mature board answer the same glyph for the same strip and the same rows (" + earlyGlyph + "px)");
        check(ResultsBandFit.FontForBand(900f, 30, PanelDensity.Compact, 0) > 13,
            "and no glyph cap was introduced to make that true: a taller final strip is still spent on a larger table");
        check(earlyGlyph < ResultsBandFit.FontForBand(UnsettledRoom, 30, PanelDensity.Compact, 0),
            "which is exactly the shrink the recording shows, and the reason the moving strip is no longer used");
    }

    // ---------------------------------------------------------------------------------------------------
    // C: the client failure page - its own walls, and PageFloor as a cap only
    // ---------------------------------------------------------------------------------------------------
    private static void TheClientFailurePageKeepsItsOwnWallsAndOnlyCaps(Action<bool, string> check)
    {
        // 2560x1440, the recorded failure page: the fixed page geometry only reaches 1429.33, the live title is the
        // upper edge (through `ResultsBounds`, asserted below), and the wall is the waiting text
        // `m_returnToLobby_text`, which the recording measured at 1207.
        const float ScreenHeight = 1440f;
        const float WaitingTextTop = 1207f;
        float pageFloor = ResultsGuestPageGeometry.PageFloor(ScreenHeight);
        check(Math.Abs(pageFloor - 1429.33f) < 0.5f,
            "the client failure page's own floor on a 1440 window is the window's foot margin (" + pageFloor.ToString("0.##") + ")");

        ResultsSafeArea fixedPage = new ResultsSafeArea(381f, pageFloor);
        check(ResultClientFailureBand.TryResolve(fixedPage, ScreenHeight, 96f, WaitingTextTop, float.NaN, float.NaN,
                out ResultClientBandReading byText)
              && Math.Abs(byText.Strip.Bottom - (WaitingTextTop - ResultsBounds.Clearance(ScreenHeight))) < 0.5f
              && byText.WaitingTextTightened,
            "the client failure strip stops at the waiting text's own top edge minus the page's clearance - the lower wall the page really draws");

        // A real interaction above that text is a wall too, and the band stops above it.
        check(ResultClientFailureBand.TryResolve(fixedPage, ScreenHeight, 96f, WaitingTextTop, 900f, float.NaN,
                out ResultClientBandReading byControl)
              && Math.Abs(byControl.Strip.Bottom - (900f - ResultsBounds.Clearance(ScreenHeight))) < 0.5f
              && byControl.PressableTightened,
            "and a higher interaction the page really carries tightens the strip further, never the other way round");

        // PageFloor only caps: a wall below it cannot push the strip past the page's own foot.
        check(ResultClientFailureBand.TryResolve(fixedPage, ScreenHeight, 96f, 1600f, float.NaN, float.NaN,
                out ResultClientBandReading capped)
              && Math.Abs(capped.Strip.Bottom - pageFloor) < 0.5f,
            "a wall below the page's own floor only caps the strip at that floor - it never becomes the wall");

        // An unmeasurable wall is a refusal, not a strip that runs to the foot of the window.
        check(!ResultClientFailureBand.TryResolve(fixedPage, ScreenHeight, 96f, float.NaN, float.NaN, float.NaN,
                out ResultClientBandReading nothing),
            "with neither the waiting text nor a control readable there is no strip at all: an unmeasured lower bound never stands in for a final one");

        // 1.19.60: the client page's UPPER edge is the live title, exactly as the host page's is. The recorded 1.19.59
        // page answered this twice - 10.67 (window margin) on the strict path and 467 (the title's bottom) on the
        // fail-open path - so the two bands were ~456 px apart on one page and the strict board, bottom-aligned to its
        // wall, covered the lower part of the title the player is reading.
        const float TitleBottom = 467f;
        check(ResultAutoBoardContract.TryFailureAnchor(ScreenHeight, TitleBottom, float.NaN,
                out float titleAnchor, out ResultBoundSource titleSource)
              && Math.Abs(titleAnchor - TitleBottom) < 0.5f && titleSource == ResultBoundSource.TitleAnchor,
            "a client failure page's upper edge is the live mission-failed title's own bottom, not the window margin it used to take");
        check(ResultAutoBoardContract.TryFailureAnchor(ScreenHeight, float.NaN, float.NaN,
                out float marginAnchor, out ResultBoundSource marginSource)
              && Math.Abs(marginAnchor - ResultAutoBoardContract.TopMargin(ScreenHeight)) < 0.5f
              && marginSource == ResultBoundSource.ScreenTopMargin,
            "with the title unmeasurable the window's own margin stands alone - no title is ever invented");

        // Both paths that place a board on this page now start from the same edge: the strict band adds this page's
        // clearance to that anchor, and the fail-open band reaches the same number through `ResultsBounds`.
        check(ResultsBounds.TryResolve(ScreenHeight, titleAnchor, titleSource, float.NaN, WaitingTextTop,
                ResultBoundSource.FooterText, float.NaN, out ResultsBand agreed)
              && Math.Abs(agreed.Top - (titleAnchor + ResultsBounds.Clearance(ScreenHeight))) < 0.5f
              && Math.Abs(agreed.Top - 488.33f) < 0.5f,
            "the strict band and the fail-open band now start at the same edge (488.33 on the recorded 1440 page) instead of 32 and 488.33");
        // And the fail-open side is the same *inputs*, not a coincidence: it reads `m_missionFailed_text`'s own rectangle
        // and hands it to `ResultsBounds`, which adds this page's clearance exactly once - the same anchor, the same gap,
        // the same band top. The source check below pins the call the strict client branch makes.
        check(ResultsBounds.TryResolve(ScreenHeight, TitleBottom, ResultBoundSource.TitleAnchor, float.NaN, WaitingTextTop,
                ResultBoundSource.FooterText, float.NaN, out ResultsBand failOpen)
              && Math.Abs(failOpen.Top - agreed.Top) < 0.5f,
            "and the fail-open path resolves that same band from that same measured title edge, so F8, the refusal budget and the second look all land on one upper bound");
        // The lower-bound policy names the waiting text as the wall and the page floor as a cap.
        ResultPageWalls walls = ResultPageLowerBoundPolicy.Resolve(true, ScreenHeight, float.NaN, float.NaN,
            WaitingTextTop, ResultBoundSource.None);
        check(walls.GuestFailure && Math.Abs(walls.FooterTextTop - WaitingTextTop) < 0.5f
              && walls.FooterTextSource == ResultBoundSource.FooterText,
            "the client failure page hands its waiting text out as the footer wall, named as the text the page draws");
        check(Math.Abs(walls.CapBottom(ScreenHeight) - pageFloor) < 0.5f,
            "and its page floor is a cap on the strip, never the wall itself");

        // A control the scan sees reaching *past* the title's edge (deeper into the page, `FooterControls.BoardTop`) is
        // a wall too: the band may not start above it. A control standing above the title changes nothing - it is inside
        // the region the title already protects.
        check(ResultAutoBoardContract.TryFailureAnchor(ScreenHeight, TitleBottom, 520f,
                out float controlAnchor, out ResultBoundSource controlSource)
              && Math.Abs(controlAnchor - 520f) < 0.5f && controlSource == ResultBoundSource.MeasuredControl,
            "and a real interaction reaching past the title tightens the client's upper edge further, which that branch did not do at all");
        check(ResultAutoBoardContract.TryFailureAnchor(ScreenHeight, TitleBottom, 400f,
                out float aboveAnchor, out ResultBoundSource aboveSource)
              && Math.Abs(aboveAnchor - TitleBottom) < 0.5f && aboveSource == ResultBoundSource.TitleAnchor,
            "while a control standing above the title changes nothing: it is inside the region the title already protects");
        check(ResultsBounds.TryResolve(ScreenHeight, 300f, ResultBoundSource.TitleAnchor, float.NaN, WaitingTextTop,
                ResultBoundSource.FooterText, float.NaN, out ResultsBand band)
              && Math.Abs(band.Top - (300f + ResultsBounds.Clearance(ScreenHeight))) < 0.5f
              && band.TopSource == ResultBoundSource.TitleAnchor
              && Math.Abs(band.Bottom - (WaitingTextTop - ResultsBounds.Clearance(ScreenHeight))) < 0.5f
              && band.BottomSource == ResultBoundSource.FooterText,
            "the failure band's upper edge is the live title and its lower edge is the waiting text, both measured");

        // The same row eligibility on a failure page: the shared projection is the one answer both pages read.
        SquadExchange exchange = ClientExchange(out CombatStatsStore store);
        CombatPlayer own = store.GetOrCreate(Local);
        own.Name = "A1pha";
        own.Total.AddEnemyDamage(310);
        ApplyPack(store, Local, Teammate);
        List<CombatPlayer> rows = exchange.Project(store.Snapshot(), Local);
        var settlement = new SettlementRows();
        ImpactReport report = Report(settlement.Update(rows, 8));
        string failureJson = ResultsLog.Build(false, new DateTime(2026, 11, 24, 22, 18, 57, DateTimeKind.Utc),
            "1.19.60", ImpactBoard.Build(report, true, false), report);
        using var document = JsonDocument.Parse(failureJson);
        check(document.RootElement.GetProperty("result").GetString() == "failure"
              && document.RootElement.GetProperty("players").GetArrayLength() == 1,
            "a client failure board and its archive carry the same one row this machine can account for, not the seat its medkit happened to heal");
        check(rows.TrueForAll(row => row.Lookup == Local),
            "and the failure page reads its rows from the same projection as the level HUD - one rule, both screens");
    }

    // ---------------------------------------------------------------------------------------------------
    // A: the fallback paths - a host with no network keeps its squad, and no path re-admits a bare row
    // ---------------------------------------------------------------------------------------------------
    //
    // `CombatSync.DisplayRows` has one `return` and no try/catch, and `Project` has one `return`; there is no branch
    // anywhere that answers with the raw capture store. These assertions drive the three states an unreadable network
    // can leave behind - no roster at all, no cache, and an own-lookup read that came back 0 - because those are the
    // ones in which a filter applied unthinkingly would take a host's squad away.
    private static void TheFallbackNeverPutsABareRowBack(Action<bool, string> check)
    {
        // (1) A host whose squad sync never armed: no roster was ever handed over (`SetRoster` is the only writer of
        //     the exchange's own record of the role), no row was ever cached, and every seat is one this machine
        //     measured itself. The live answer is passed in the way the production call site passes it.
        var lonelyHost = new SquadExchange();
        lonelyHost.Configure(Host, 0, 1, 2, 3);
        var hostStore = new CombatStatsStore();
        foreach (ulong seat in new[] { Host, Local, Teammate, Bot })
            hostStore.GetOrCreate(seat).Total.AddEnemyDamage(100);
        check(lonelyHost.Project(hostStore.Snapshot(), Host, true).Count == 4,
            "a host whose network is off keeps every seat it measured: with no roster and no cache, all four rows are its own measurement");
        check(!lonelyHost.RoleIsMaster && lonelyHost.Project(hostStore.Snapshot(), Host).Count == 1,
            "while the exchange's own record - which only a roster read can set - still answers client, which is why the live fact is passed in rather than guessed");

        // (2) The same machine, after the roster really arrived: it is the authority in both forms, so the answer must
        //     not change. This is the frame the fallback exists for - a host whose network hiccups keeps its squad.
        lonelyHost.SetRoster(new[]
        {
            new SquadPeer(Host, 0, true, false, true),
            new SquadPeer(Local, 1, false, false, false),
            new SquadPeer(Teammate, 2, false, false, false),
            new SquadPeer(Bot, 3, false, true, false),
        }, true);
        check(lonelyHost.RoleIsMaster && lonelyHost.Project(hostStore.Snapshot(), Host).Count == 4,
            "and the same four rows once the roster is readable - the host's own record says master too, and its answer never depends on the network");

        // (3) A client whose own-lookup read failed for the frame (`OwnLookup()` answers 0 when `SNet.LocalPlayer`
        //     throws). Its own row must still be recognised as its own, or the HUD would show only what it received.
        var client = new SquadExchange();
        client.Configure(Local, 0, 1, 2, 3);
        client.SetRoster(new[]
        {
            new SquadPeer(Host, 0, false, false, true),
            new SquadPeer(Local, 1, true, false, false),
            new SquadPeer(Teammate, 2, false, false, false),
        }, false);
        var clientStore = new CombatStatsStore();
        clientStore.GetOrCreate(Local).Total.AddEnemyDamage(500);
        ApplyPack(clientStore, Local, Teammate);          // the supply credit that must stay off the board
        List<CombatPlayer> unread = client.Project(clientStore.Snapshot(), 0UL);
        check(unread.Count == 1 && Row(unread, Local) != null,
            "a client whose own-lookup read came back 0 still draws its own row - the exchange's own record of the local lookup is the fallback, and only that");
        check(Row(unread, Teammate) == null,
            "and the seat its medkit happened to heal is still not on the board: the fallback changes whose row the own-lookup means, never which rows are evidence");
        check(!SquadRowEligibility.Draws(false, false, false),
            "no state of the network makes a row this machine neither measured, received nor is the authority for eligible");
    }

    // ---------------------------------------------------------------------------------------------------
    // B (independent review F1): the success page's gate, driven by the production predicate
    // ---------------------------------------------------------------------------------------------------
    //
    // Every assertion below calls the real `ResultRevealStatePolicy.Observe` with readings taken from a real
    // `BlinkCompletionLedger`, a real `ResultFinalGate` and the real `ResultEarlyBoardPolicy` - no boolean formula is
    // re-derived here, so a change in the production rule makes these assertions fail rather than silently agree.
    private static void TheSuccessGateHasNoMovingEdgeLeft(Action<bool, string> check)
    {
        // (a) A live page that has handed the ledger nothing yet. `Required == 0` is *not-started*, never "settled":
        //     this is the F1 defect exactly - the page registers its six targets inside the native `BlinkIn`.
        var empty = new BlinkCompletionLedger();
        var latch = new ResultFinalGate();
        check(ResultRevealStatePolicy.Observe(false, empty.Required, empty.Count, empty.Completed, empty.InFlight,
                  empty.MayStillComplete, empty.Settled, 0) == RevealObservation.NotStarted,
            "a page that has registered nothing yet is `not-started`, so a client's early board cannot stand on it");
        check(!ResultEarlyBoardPolicy.Allows(true, true, true, false, 626f, 420f, hostProven: false, boundsFinal: false),
            "and the early board is refused in that state: 626 px of a strip whose edges may still move is not a place to draw a table");

        // (b) The bounded retry for a page whose native callback never comes: the same empty ledger, watched past the
        //     whole budget, is `unobservable` - a named state that still demands the page's own geometry.
        int budget = ResultRevealStatePolicy.UnobservableObservations;
        check(ResultRevealStatePolicy.Observe(false, 0, 0, 0, 0, true, true, budget) == RevealObservation.Unobservable,
            "an empty ledger past the whole observation budget is `unobservable` - the bounded retry, not a completion");
        check(!ResultRevealStatePolicy.Answered(RevealObservation.Unobservable)
              && ResultRevealStatePolicy.Bounded(RevealObservation.Unobservable),
            "`unobservable` is bounded and never answered: nothing may call it a finished reveal");
        bool firstRead = latch.Observe(694f, 1296f);
        bool secondRead = latch.Observe(694f, 1296f);
        check(!firstRead && secondRead && latch.Ready,
            "the page's own geometry latch is what an unobservable page still has to pass: one reading is not settled, two agreeing readings are");

        // (c) The same page, a moment later: six targets arrive. It leaves `not-started` immediately and stays `running`
        //     until the ledger's own answer - the "initially empty, then registered" sequence F1 asked for.
        var ledger = new BlinkCompletionLedger();
        for (long target = 1; target <= 6; target++) ledger.Require(target, target % 2 == 0);
        check(ledger.Required == 6 && ledger.Count == 6 && ledger.Completed == 0,
            "the six targets are registered and none has finished: the recorded 0/6 state");
        check(ResultRevealStatePolicy.Observe(false, ledger.Required, ledger.Count, ledger.Completed, ledger.InFlight,
                  ledger.MayStillComplete, ledger.Settled, budget) == RevealObservation.Running,
            "once targets exist the budget is out of the answer: the state is `running`, not `unobservable`");
        check(ResultRevealGate.BlocksPlacement(false, ledger.MayStillComplete, ledger.InFlight, false),
            "and the gate holds the mature board while the reveal can still finish");
        check(!ResultEarlyBoardPolicy.Allows(true, true, true, false, 534f, 420f, hostProven: false, boundsFinal: false),
            "so no early board enters on a running reveal without final bounds");

        // (d) The reveal really finishes: every target done.
        for (long target = 1; target <= 6; target++)
        {
            ledger.Start(target, target * 10);
            ledger.Finish(target, target * 10, true);
        }
        check(ledger.Ready && ledger.InFlight == 0,
            "six of six finished and nothing in flight is what the page reports at the end of its reveal");
        check(ResultRevealStatePolicy.Observe(true, ledger.Required, ledger.Count, ledger.Completed, ledger.InFlight,
                  ledger.MayStillComplete, ledger.Settled, 0) == RevealObservation.Finished,
            "and the production predicate calls it `finished`, which is the only state allowed a board on the ledger's word");

        // (e) The same-frame steady page: the reveal answered and the strip the page measures is the final one - the
        //     early board is allowed with a measured wall and refused with an unproven one.
        check(ResultEarlyBoardPolicy.Allows(true, true, true, false, 534f, 420f, hostProven: false, boundsFinal: true),
            "a page whose bounds are final enters early (no overlap, complete scan, strip holds the table's own floor)");
        check(!ResultEarlyBoardPolicy.Allows(true, true, true, false, 534f, 420f, hostProven: false, boundsFinal: false),
            "and a strip with no measured lower wall is still refused, even though the reveal has answered");
        check(!ResultEarlyBoardPolicy.Allows(true, true, true, true, 900f, 300f, hostProven: false, boundsFinal: true),
            "a real crossing control refuses the early board absolutely, final bounds or not");
        check(!ResultEarlyBoardPolicy.Allows(true, true, false, false, 900f, 300f, hostProven: true, boundsFinal: true),
            "and an unfinished scan is no early board even for a proven host: the privilege never buys a hole in the scan");
        check(ResultEarlyBoardPolicy.Allows(true, true, true, false, 534f, 420f, hostProven: true, boundsFinal: false),
            "while a proven host keeps its 1.19.55 privilege on the strip it can already read");

        // (f) An unreadable role is not the host: it gets the stricter answer, on the same measured facts.
        ResultPageRole unreadable = ResultPageRolePolicy.Unreadable;
        check(!ResultPageRolePolicy.IsClient(unreadable),
            "an unreadable role still takes the host's *measuring* path (it measures and refuses rather than handing out a client safe area)");
        check(!ResultEarlyBoardPolicy.Allows(true, true, true, false, 534f, 420f, hostProven: false, boundsFinal: false),
            "but it is not a proven host, so it may not take the host's early-display privilege: without final bounds it waits");

        // (g) The fail-open route cannot answer the same transient state on budget alone: it needs a readable page, and the
        //     page's own measured wall on a client failure page. Both are asked here through their own policies.
        check(!ResultFallbackPolicy.ShouldFallback(0, false)
              && !ResultFallbackPolicy.ShouldFallback(ResultFallbackPolicy.MissBudget - 1, false)
              && ResultFallbackPolicy.ShouldFallback(ResultFallbackPolicy.MissBudget, false),
            "the refusal budget is unchanged: only the last refusal of it may release the fail-open board");
        check(!ResultFallbackPolicy.PageReadable(false, true, 0) && !ResultFallbackPolicy.PageReadable(true, true, 1),
            "and an unreadable page (an incomplete or uncovered scan, an active control with no rectangle) never becomes answerable, budget or not");
        check(!ResultFallbackPolicy.MayAnswer(ResultFallbackPolicy.MissBudget, false, false),
            "so the budget alone buys nothing on a page this build could not read");
        check(ResultFailureWallPolicy.Waits(false, ResultFailureWallPolicy.UnobservedBudget, true),
            "and a client failure page with no measured wall waits through its whole budget instead of being answered by it");

        // (h) The second look still needs two agreeing samples of a moved strip.
        var secondLook = new ResultAnchorDrift();
        secondLook.Record(768f, 534f);
        check(!secondLook.Due(768f, 534f),
            "a page that measures the same strip costs no second look: the recorded re-fit only happened because the strip had moved");
    }

    // ---------------------------------------------------------------------------------------------------
    // The archive: one supply key per player, in both languages, on both result pages
    // ---------------------------------------------------------------------------------------------------
    //
    // The recorded success page wrote each player's supply cell twice - once under this build's four-field label and
    // once under the released three-field one - and the two disagreed about what their second field meant. This drives
    // the real `ResultsLog.Build` and *enumerates* every property of every player object, which is the only way to say
    // "exactly one such key exists" rather than "the one I looked for is there".
    private static void TheArchiveWritesOneSupplyKeyPerPlayer(Action<bool, string> check)
    {
        const string LegacyZh = "补给效率 / 本人 / 弹药";
        const string LegacyEn = "Supply efficiency / self / ammo";
        string language = CombatStrings.Language;
        try
        {
            foreach (string locale in new[] { "zh", "en" })
            {
                CombatStrings.Language = locale;
                foreach (bool succeeded in new[] { true, false })
                {
                    ImpactCounters known = KnownSupplyCounters(out ImpactCounters unknown);
                    var inputs = new[]
                    {
                        new ImpactInput { Name = "A", Counters = known },
                        new ImpactInput { Name = "B", Counters = unknown },
                    };
                    ImpactReport report = ImpactScoring.Evaluate(inputs, ImpactScoring.DefaultWeights, 1.0, 1.0);
                    ImpactBoardLayout board = ImpactBoard.Build(report, true, succeeded);
                    string json = ResultsLog.Build(succeeded, new DateTime(2026, 11, 24, 22, 18, 57, DateTimeKind.Utc),
                        "1.19.60", board, report);
                    string where = locale + "/" + (succeeded ? "success" : "failure");
                    check(!json.Contains(LegacyZh) && !json.Contains(LegacyEn),
                        "no archived player carries the released three-field key any more (" + where + ")");
                    using var document = JsonDocument.Parse(json);
                    JsonElement players = document.RootElement.GetProperty("players");
                    check(players.GetArrayLength() == 2, "both players are archived (" + where + ")");
                    for (int index = 0; index < players.GetArrayLength(); index++)
                    {
                        JsonElement player = players[index];
                        int supplyKeys = 0;
                        string supplyCell = null;
                        foreach (JsonProperty property in player.EnumerateObject())
                        {
                            if (property.Name == LegacyZh || property.Name == LegacyEn) supplyKeys += 100;
                            if (property.Name == CombatStrings.SupplyRowLabel) { supplyKeys++; supplyCell = property.Value.GetString(); }
                        }
                        check(supplyKeys == 1,
                            "player " + index + " is archived under exactly one supply key, the four-field one (" + where + ", found " + supplyKeys + ")");
                        JsonElement counters = player.GetProperty("counters");
                        // The total and the ammo part are the row's own measurement in both cases (58 = 47 + the rest);
                        // the rest is where the two rows differ: the split row names 0 tool and 11 medical, and the row
                        // whose split never arrived keeps 0 medical beside `selfMedicalKnown: false` - the documented
                        // shape, an absence a reader can see, never a zero it has to guess at.
                        long medical = index == 0 ? 11 : 0;
                        bool totals = counters.GetProperty("selfSupplyUses").GetInt64() == 58
                            && counters.GetProperty("selfAmmoUses").GetInt64() == 47
                            && counters.GetProperty("selfToolUses").GetInt64() == 0
                            && counters.GetProperty("selfMedicalUses").GetInt64() == medical;
                        check(totals,
                            "and every figure the alias quoted is still there as a number: total 58, ammo 47, tool 0, medical " + medical + " (" + where + ", player " + index + ")");
                        if (index == 0)
                        {
                            check(supplyCell == "93.6% / 47 / 0 / 11",
                                "the current cell reads 93.6% with its three kinds in the recorded order (" + where + "): " + supplyCell);
                            bool flags = counters.GetProperty("selfSupplyKindsKnown").GetBoolean()
                                && counters.GetProperty("selfMedicalKnown").GetBoolean();
                            check(flags,
                                "and the two flags that say the split and the medical part were really observed travel beside it (" + where + ")");
                        }
                        else
                        {
                            check(supplyCell == "93.6% / 47 / ? / ?",
                                "a row whose split never arrived keeps its two unknown kinds as `?`, never as a zero (" + where + "): " + supplyCell);
                            bool unproven = !counters.GetProperty("selfSupplyKindsKnown").GetBoolean()
                                && !counters.GetProperty("selfMedicalKnown").GetBoolean();
                            check(unproven,
                                "with both flags false, which is what tells a reader the `?` is an absence rather than a measurement (" + where + ")");
                        }
                    }
                }
            }
        }
        finally { CombatStrings.Language = language; }
    }

    // 93.6% over 58 packs split 47 ammo / 0 tool / 11 medical - the recorded success row - and, beside it, the same
    // total with the split withdrawn (a v8 peer's row), which must print `?` for the two kinds it cannot name.
    private static ImpactCounters KnownSupplyCounters(out ImpactCounters unknown)
    {
        var known = new ImpactCounters();
        known.RecordSupply(936, 1000);
        for (int pack = 0; pack < 47; pack++) known.CreditSelfSupply(SupplyPackKind.Ammo);
        for (int pack = 0; pack < 11; pack++) known.CreditSelfSupply(SupplyPackKind.Medical);
        unknown = new ImpactCounters();
        unknown.RecordSupply(936, 1000);
        unknown.SelfSupplyUses = 58;
        unknown.SelfAmmoUses = 47;
        unknown.SelfSupplyKindsKnown = false;
        return known;
    }

    // ---------------------------------------------------------------------------------------------------
    // The two places the Unity side has to be wired to these decisions
    // ---------------------------------------------------------------------------------------------------
    private static void TheUnitySideIsWiredToTheseDecisions(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the 1.19.60 regression locates the production sources");
        if (root == null) return;

        string exchange = Code(File.ReadAllText(Path.Combine(root, "SquadExchange.cs")));
        check(exchange.Contains("if (!SquadRowEligibility.Draws(capture.Lookup == self, Cache.Has(capture.Lookup), localIsAuthority)) continue;"),
            "the one projection boundary asks the eligibility rule - the store, the roster, a handshake, a pack count, a zero and a name are all absent from the question");
        check(exchange.Contains("internal List<CombatPlayer> Project(IReadOnlyList<CombatPlayer> measured, ulong local, bool localIsAuthority)")
              && exchange.Contains("=> Project(measured, local, _localIsMaster);")
              && exchange.Contains("ulong self = local != 0UL ? local : _local;"),
            "and the live role is passed in by the caller, with the exchange's own record and its own lookup as the two documented fallbacks");

        string sync = Code(File.ReadAllText(Path.Combine(root, "CombatSync.cs")));
        check(sync.Contains("Exchange.Project(CombatTracker.Store.Snapshot(), OwnLookup(), LocalIsAuthority())")
              && sync.Contains("catch { return Exchange.RoleIsMaster; }"),
            "the HUD hands the live authority answer in, and a throwing read falls back to the exchange's record: a host never loses its squad to a closed network");
        check(!sync.Contains("catch") || Tally(sync, "return CombatTracker.Store.Snapshot()") == 0,
            "and no branch in `DisplayRows` answers with the raw capture store: the only rows it can return came through the projection");

        string display = Code(File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs")));
        check(display.Contains("bool clientBoundsFinal = revealSettled && ResultFooterProvenance.MeasuredLowerWall(footerSource);")
              && Tally(display, "HostProven(), clientBoundsFinal)") == 2,
            "both early entries hand the proven-host/final-bounds pair to the policy, and the bounds are decided from the page's own reveal state and its measured wall");
        check(display.Contains("if (!successNoFooterReady && GuestClient() && !clientBoundsFinal)"),
            "a page whose strip is already final is not held back by the no-footer proof, so waiting for the final strip can never cost the board");
        // F1: the shared predicate is asked once, with the ledger's four readings and the page's own observation count.
        check(display.Contains("RevealObservation revealState = ResultRevealStatePolicy.Observe(revealDone, revealRequired,")
              && display.Contains("bool revealSettled = ResultRevealStatePolicy.Answered(revealState)")
              && !display.Contains("(CompletionTargets.Ledger.Count == 0 && FinalLayout.Ready)"),
            "the empty-ledger case is answered by the shared `ResultRevealStatePolicy` (NotStarted / Running / Finished / Unobservable), not by a local Count==0 formula");
        check(display.Contains("if (revealState != RevealObservation.Finished && HeadingReports.Add(\"success-reveal-state\"))"),
            "and the state is named in the log once per page, so `unobservable` can never be read as a completion");
        // F2: the control edge handed to the anchor is the raw one - the gap the scan already applied is removed, because
        // the strip adds this page's clearance itself exactly once.
        check(display.Contains("float rawControlTop = float.IsFinite(controls.BoardTop) ? controls.BoardTop - MessageGap : float.NaN;")
              && display.Contains("ResultAutoBoardContract.TryFailureAnchor(Screen.height, titleBottom, rawControlTop,"),
            "the client failure anchor reads a gap-free control edge, so one clearance is applied once instead of twice");

        string bounds = Code(File.ReadAllText(Path.Combine(root, "ResultsBounds.cs")));
        check(bounds.Contains("measuredControlTop > anchor + 0.5f"),
            "and `TryFailureAnchor` can only report a measured control when the control is really deeper than the title, so a gap cannot be dressed up as one");

        string policy = Code(File.ReadAllText(Path.Combine(root, "ResultVisibility.cs")));
        check(policy.Contains("public static bool Allows(bool localData, bool completionMeasured, bool scanComplete, bool overlapsControl,")
              && policy.Contains("float room, float floor, bool hostProven, bool boundsFinal)")
              && policy.Contains("return hostProven || boundsFinal;"),
            "the policy's own answer is one added condition and nothing else - and it needs a *proven* host, not merely \"not a client\"");
        check(policy.Contains("public static bool MeasuredLowerWall(ResultFooterSource source)")
              && policy.Contains("=> source == ResultFooterSource.NamedControl || source == ResultFooterSource.InteractiveBand;"),
            "and only a control the page really measured counts as a final lower wall");

        string finalLayout = Code(File.ReadAllText(Path.Combine(root, "ResultFinalLayout.cs")));
        check(finalLayout.Contains("public const int UnobservableObservations = 40;")
              && finalLayout.Contains(": RevealObservation.NotStarted;")
              && finalLayout.Contains("? RevealObservation.Unobservable")
              && finalLayout.Contains("public static bool Answered(RevealObservation state) => state == RevealObservation.Finished;"),
            "the four-state predicate lives in one place, with the bounded observation budget and `Answered` kept apart from `Bounded`");

        string log = Code(File.ReadAllText(Path.Combine(root, "ResultsLog.cs")));
        check(!log.Contains("SupplyRowLabelLegacy") && !log.Contains("CombatStrings.SupplyCell(efficiency, entry.Input.Counters.SelfSupplyUses"),
            "the archive writes one supply key per player: the released three-field alias is gone from the writer, and its totals live on in `counters`");
    }

    // ------------------------------------------------------------------------------------ helpers ----

    // ---------------------------------------------------------------------------------------------------
    // C (independent review F2): the failure page's upper bound is one real baseline, and one gap
    // ---------------------------------------------------------------------------------------------------
    //
    // The strict client branch used to hand `FooterControls.BoardTop` to `ResultAutoBoardContract.TryFailureAnchor`. That
    // value already carries the gap the scan was run with (`ScanFooterControls(page, titleBottom, MessageGap, …)` builds
    // it as `anchorBottom + gap`, or `control.Bottom + gap`), and the guest strip adds this page's clearance itself, so
    // the same spacing landed twice: 467 + 16 + 21.33 = 504.33 on the recorded 1440p page instead of the fail-open's
    // 488.33 - and with no control in the way at all it was reported as a `MeasuredControl`. The assertions below drive
    // the real chain (`TryFailureAnchor` -> `ResultsGuestPageGeometry.TryResolve` -> `ResultClientFailureBand`) and the
    // real fail-open band, in both cases: no control, and a control that really reaches past the title.
    private static void TheFailureUpperBoundIsOneRealBaseline(Action<bool, string> check)
    {
        const float ScreenWidth = 2560f, ScreenHeight = 1440f;
        const float TitleBottom = 467f, WaitingTextTop = 1207f, MessageGap = 16f;
        float clearance = ResultsBounds.Clearance(ScreenHeight);
        ResultPageWalls walls = ResultPageLowerBoundPolicy.Resolve(true, ScreenHeight, float.NaN, float.NaN,
            WaitingTextTop, ResultBoundSource.None);

        // (a) A page with no control reaching past the title. `BoardTop` is `title + gap`; the raw edge is that minus the
        //     same gap, and it is the anchor the title itself gives.
        float plainBoardTop = TitleBottom + MessageGap;
        check(ResultAutoBoardContract.TryFailureAnchor(ScreenHeight, TitleBottom, plainBoardTop - MessageGap,
                  out float plainAnchor, out ResultBoundSource plainSource)
              && Math.Abs(plainAnchor - TitleBottom) < 0.5f && plainSource == ResultBoundSource.TitleAnchor,
            "with no control in the way the anchor is the title itself, named `TitleAnchor`: no measured control is invented");
        check(ResultsGuestPageGeometry.TryResolve(plainAnchor, ScreenWidth, ScreenHeight, MessageGap, 96f, out ResultsSafeArea plainArea)
              && Math.Abs(plainArea.Top - (TitleBottom + clearance)) < 0.6f
              && Math.Abs(plainArea.Top - 488.33f) < 0.6f,
            "and the strict strip starts at 488.33 on that page - the title plus exactly one clearance (" + plainArea.Top.ToString("0.##") + ")");
        check(ResultFallbackBandPolicy.TryResolve(ScreenHeight, TitleBottom, ResultBoundSource.TitleAnchor, walls,
                  float.NaN, plainArea.Top, plainArea.Bottom, 420f, out ResultFallbackPlan failOpen)
              && Math.Abs(failOpen.Top - plainArea.Top) < 0.6f,
            "the fail-open band starts at that same edge from the same measured title, so the two routes share one baseline");

        // (b) The wiring this release removes, kept as the RED case it was found as: the whole `BoardTop` (gap included)
        //     handed over as if it were a control.
        check(ResultAutoBoardContract.TryFailureAnchor(ScreenHeight, TitleBottom, plainBoardTop,
                  out float doubled, out ResultBoundSource doubledSource)
              && Math.Abs(doubled - (TitleBottom + MessageGap)) < 0.5f
              && doubledSource == ResultBoundSource.MeasuredControl,
            "handing the gap-included `BoardTop` over is the defect F2 found: the anchor moves by one gap and a control is claimed that nobody measured");
        check(ResultsGuestPageGeometry.TryResolve(doubled, ScreenWidth, ScreenHeight, MessageGap, 96f, out ResultsSafeArea doubledArea)
              && Math.Abs(doubledArea.Top - (TitleBottom + MessageGap + clearance)) < 0.6f,
            "which puts the strict strip at 504.33 - two spacings - while the fail-open stays at 488.33");

        // (c) A control that really reaches past the title: raw bottom 560. One gap, one source, and the strip moves down.
        const float ControlBottom = 560f;
        check(ResultAutoBoardContract.TryFailureAnchor(ScreenHeight, TitleBottom, ControlBottom + MessageGap - MessageGap,
                  out float controlAnchor, out ResultBoundSource controlSource)
              && Math.Abs(controlAnchor - ControlBottom) < 0.5f && controlSource == ResultBoundSource.MeasuredControl,
            "a control that really reaches past the title is the deeper edge, and it is named as the measured control");
        check(ResultsGuestPageGeometry.TryResolve(controlAnchor, ScreenWidth, ScreenHeight, MessageGap, 96f, out ResultsSafeArea controlArea)
              && Math.Abs(controlArea.Top - (ControlBottom + clearance)) < 0.6f,
            "and the board starts one clearance below it, never two (" + controlArea.Top.ToString("0.##") + ")");
        check(controlArea.Top > plainArea.Top + 60f,
            "so a real blocking control moves the strict strip down instead of letting the board cross it");
        // A control within a hair of the anchor is not a control: `TryFailureAnchor` may only name one that is really deeper.
        check(ResultAutoBoardContract.TryFailureAnchor(ScreenHeight, TitleBottom, TitleBottom + 0.4f,
                  out float nearAnchor, out ResultBoundSource nearSource)
              && Math.Abs(nearAnchor - TitleBottom) < 0.5f && nearSource == ResultBoundSource.TitleAnchor,
            "and a reading within half a pixel of the title is not a measured control either");
    }

    // The recorded squad from this machine's side: an unmodded host (which never answers), this machine, one
    // other installed client, and one unmodded teammate. The roster is the game's own slot list.
    private static SquadExchange ClientExchange(out CombatStatsStore store)
    {
        store = new CombatStatsStore();
        var exchange = new SquadExchange();
        exchange.Configure(Local, 0, 1, 2, 3);
        exchange.SetRoster(new[]
        {
            new SquadPeer(Host, 0, false, false, true),
            new SquadPeer(Local, 1, true, false, false),
            new SquadPeer(Peer, 2, false, false, false),
            new SquadPeer(Teammate, 3, false, false, false),
        }, false);
        return exchange;
    }

    // One real application: the native bracket names the pack's user and the receiver, the ledger proves it, and
    // the credit is written to the receiver's own row - the production path (`SupplyTracker.EndApply` ->
    // `MedicalPackCredit.Apply` -> `CandidateFor`) with `ShouldTrack` deliberately not part of it.
    private static int ApplyPack(CombatStatsStore store, ulong giver, ulong receiver)
    {
        var ledger = new PackApplyLedger();
        PackBracket bracket = ledger.Begin(giver, false);
        ledger.NoteGive(bracket, receiver, 0.25f);
        PackApplyResult applied = ledger.End(bracket, null);
        if (applied.Outcome != PackApplyOutcome.Applied) return 0;
        var credit = new MedicalPackCredit();
        // `SupplyTracker.CandidateFor` reaches the row through `CombatTracker.ObservePlayer`, which *creates* the
        // receiver's row in the capture store when the pack lands. That is the whole reason the row exists at all,
        // so the helper creates it the same way instead of skipping a lookup it has not seen yet.
        return credit.Apply(applied, lookup =>
            new MedicalRowCandidate { Known = true, InSlot = true, Row = store.GetOrCreate(lookup).Impact });
    }

    // The report `ResultsDisplay.BuildReport` builds: one input per settled row, in display order, scored with
    // the default weights. Nothing here invents a figure the page would not have.
    private static ImpactReport Report(List<CombatPlayer> rows)
    {
        var inputs = new List<ImpactInput>();
        foreach (CombatPlayer player in rows.Where(row => row != null).OrderBy(row => row.DisplaySlot))
        {
            var input = ImpactInput.From(player.Name, player.Total, player.Impact, player.SupportRole);
            input.Lookup = player.Lookup;
            inputs.Add(input);
        }
        return ImpactScoring.Evaluate(inputs, ImpactScoring.DefaultWeights, 1.0, 1.0);
    }

    private static CombatPlayer Row(List<CombatPlayer> rows, ulong lookup)
    {
        foreach (CombatPlayer row in rows) if (row != null && row.Lookup == lookup) return row;
        return null;
    }

    private static int Tally(string text, string needle)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(needle)) return 0;
        int count = 0, at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
        return count;
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj")))
            directory = directory.Parent;
        return directory?.FullName;
    }

    // The text with its comment lines removed, so a comment may name a call this build must not make.
    private static string Code(string text)
    {
        if (text == null) return string.Empty;
        var kept = new System.Text.StringBuilder(text.Length);
        foreach (string line in text.Split('\n'))
        {
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
            kept.Append(line).Append('\n');
        }
        return kept.ToString();
    }
}
