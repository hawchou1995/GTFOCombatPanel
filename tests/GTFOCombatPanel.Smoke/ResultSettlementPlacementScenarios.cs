using System;
using System.Collections.Generic;
using System.IO;

using HawChow.GTFOCombatPanel;

// 1.19.41: the three settlement-placement gates this round adds, driven the way the display drives them.
//
//   A  the client failure page's first placement.  `LogOutput.snapshot.log` 4426-4441 (2560x1440, client, run 4): two
//      frames whose scan was complete but which found no pressable control at all were answered as "this page has no
//      wall", the strip was the whole fixed page (381..1429) and a 1987x990 board was drawn; the next frame found the
//      page's own control at 1175/1181/1189, the strip tightened to 802 px, the standing board really reached that
//      control and was taken away with the vanilla page restored. The item that was missing is asserted here:
//      `ResultClientPageActionPolicy.Pending` + `ResultClientWallGate.Observe(..., pageActionsPending)`, through the one
//      entry point production calls (`ResultClientFailureStrip.Decide` / `Measure`).
//   B  the success page's first placement.  `LogOutput.snapshot.log` 6657-6674: the native BlinkIn ledger read
//      `completed=0/6` on the frame the board was placed and `6/6` a few lines later, and the strip was re-measured.
//      `ResultRevealGate` refuses that first placement while the ledger can still finish, and answers at once when it
//      cannot - so the gate is a statement about the ledger and never about how many frames have passed.
//   C  the success page's upper bound.  On that page the completion section's container ended at 313 while the captions
//      its partition icons draw ended at 352, and the panel's own top edge pressed on them.
//      `CompletionFinalBounds.Placed` is the deeper of the two once the reveal has finished, and the laid-out slot alone
//      before that.
//
// Nothing here re-implements the production order: every assertion below calls the functions `ResultsDisplay` calls,
// and the last block reads the display's own source to show it calls them in that order.
internal static class ResultSettlementPlacementScenarios
{
    // The recorded client failure page: 2560x1440, the mission-failed title laid out to 359.5, the client gap of 16 px
    // scaled by height, the waiting-status line at 1147 (measured, logged, never a wall).
    private const float Width = 2560f, Height = 1440f;
    private const float TitleBottom = 359.5f;
    private const float Gap = 16f, MinimumStrip = 96f, WaitingText = 1147f;
    // The pressable control the scan found on that page, in the three readings the log recorded.
    private const float WallHigh = 1189f, WallMid = 1181f, WallLow = 1175f;
    private const int Rows = 30;

    public static void RunInto(Action<bool, string> check)
    {
        ClientFailureFirstPlacement(check);
        ClientFailureEscape(check);
        SuccessRevealGate(check);
        SettlementGateDecidesTheFailOpenPath(check);
        SuccessUpperBound(check);
        PageChangeResets(check);
        ProductionWiring(check);
        Console.WriteLine("WIRING: SETTLEMENT_PLACEMENT_GATES (static source check plus the production policies above)");
    }

    // ---------------------------------------------------------------------------------------------------
    // A. 客机失败页：两帧「完整扫描但一个可按控件都没有」不是最终
    // ---------------------------------------------------------------------------------------------------
    private static void ClientFailureFirstPlacement(Action<bool, string> check)
    {
        // The display's own figure: the design clearance of 16 px at 1080p, scaled to this window.
        float clearance = ResultsBounds.Clearance(Height);

        // The predicate itself, at the one fact it can honestly read (1.19.42, review F3): a page that has measured no
        // pressable control is pending whatever its own actions' active states say - those carry no per-frame information
        // on a client, where the go-to-lobby button is permanently inactive - and the frame a pressable control is really
        // measured is not pending, because that wall answers for itself.
        check(ResultClientPageActionPolicy.Pending(float.NaN)
              && !ResultClientPageActionPolicy.Pending(WallMid)
              && !ResultClientPageActionPolicy.Pending(WallHigh)
              && ResultClientPageActionPolicy.Pending(float.NaN),
            "the page-action item is exactly 'this page has not measured a pressable control': pending without one, not pending with one - the old predicate's four other inputs were a tautology on a client page (HiddenPageActions is always >= 1 there) and could also answer 'not pending' on a frame nobody had measured anything, which was the wrong way round");

        var gate = new ResultClientWallGate();
        // f0/f1: complete scans, nothing pressable, the page's own actions still missing. Two identical readings of that
        // shape are exactly what the recorded page placed a 990 px board from.
        var pendingEmpty = new ResultClientFailureEvidence(TitleBottom, Width, Height, Gap, MinimumStrip, WaitingText,
            float.NaN, true, true, 0, pageActionsPending: true);
        check(ResultClientFailureStrip.Decide(100, gate, pendingEmpty, out ResultClientBandReading noBand, out ResultsSafeArea fixedPage)
              == ResultClientStripState.WallNotSettled
              && !gate.Settled && gate.Samples == 1 && !noBand.Fits,
            "the first frame of the recorded client page - a complete scan, no pressable control, the page's own actions not yet in place - is not a placement frame and hands out no band");
        check(near(fixedPage.Top, 380.83f) && near(fixedPage.Bottom, 1429.33f),
            "and the page's fixed geometry is still read on it: the wait is about the page's own actions, not about a page nobody could measure");
        check(ResultClientFailureStrip.Decide(101, gate, pendingEmpty, out _, out _) == ResultClientStripState.WallNotSettled
              && !gate.Settled && gate.Samples == 2,
            "the second identical empty scan is still not final: two agreeing 'nothing pressable' readings on a page whose own result actions are missing are exactly what the recorded board was drawn from, and the sample count authorises nothing");

        // f2: the page's action switched on, its rectangle not readable yet - the activeness changed, the frame still
        // answers nothing.
        var actionActiveUnmeasured = new ResultClientFailureEvidence(TitleBottom, Width, Height, Gap, MinimumStrip,
            WaitingText, float.NaN, true, true, 1, pageActionsPending: false);
        check(ResultClientFailureStrip.Decide(102, gate, actionActiveUnmeasured, out _, out _) == ResultClientStripState.WallNotSettled,
            "the frame on which the page's own action is switched on but cannot be measured yet is not a placement frame either: an active control with no rectangle is not 'this page has no wall'");

        // f3/f4: the real control's own edge, in the recorded coordinates.
        check(ResultClientFailureStrip.Decide(103, gate, Evidence(WallHigh, 0, false), out ResultClientBandReading first, out _)
              == ResultClientStripState.WallNotSettled
              && near(gate.Wall, WallHigh) && !first.Fits,
            "the first frame on which the page's own control is really measured is a changed reading, not an agreement - the wall is recorded and no band is handed out");
        check(ResultClientFailureStrip.Decide(104, gate, Evidence(WallHigh, 0, false), out ResultClientBandReading settled, out _)
              == ResultClientStripState.Placed && gate.Settled,
            "the second identical complete reading of that wall is the placement frame, and `Settled` - not a count of frames - is what released it");
        check(near(settled.Top, 380.83f) && near(settled.Bottom, WallHigh - clearance)
              && near(settled.Height, WallHigh - clearance - 380.83f),
            "the band is the page's fixed top and the control's own edge less the page clearance (380.83.."
            + (WallHigh - clearance).ToString("0.00") + ", " + (WallHigh - clearance - 380.83f).ToString("0.0")
            + " px): the ~800 px room of the recorded page, never the 1049 px full-page band the recorded board was drawn in");
        check(settled.PressableTightened && settled.Height > 750f && settled.Height < 810f,
            "that band is the recorded page's ~800 px room rather than the whole page, so the board is small from its first visible frame");

        // f5 and on: the same reading, repaint after repaint, and the board does not move.
        var boardStrip = new BoardStrip(settled.Top, settled.Bottom, true, WallHigh, float.NaN, true,
            ResultFooterSource.ClientNoFooterSafeArea, ResultsFontGrowth.FillCeiling(ResultFooterSource.ClientNoFooterSafeArea, true));
        int font = ResultsBandFit.FontForBand(settled.Height, Rows, PanelDensity.Compact, 0);
        float boardHeight = ResultsBandFit.RowHeight(font, PanelDensity.Compact) * Rows;
        bool boardFits = ResultBoardHold.TryPlace(Width, Height, boardStrip, 1160f, boardHeight, 0f, out LayoutRect placed, 2f);
        int wobbles = 0, accepted = 0;
        for (int frame = 105; frame < 121; frame++)
        {
            ResultClientStripState state = ResultClientFailureStrip.Decide(frame, gate, Evidence(WallHigh, 0, false),
                out ResultClientBandReading band, out _);
            if (state != ResultClientStripState.Placed || !near(band.Top, settled.Top) || !near(band.Bottom, settled.Bottom)) wobbles++;
            if (ResultBoardHold.TryPlace(Width, Height, boardStrip, 1160f, boardHeight, 0f, out LayoutRect again, 2f)
                && near(again.Y, placed.Y) && near(again.Bottom, placed.Bottom)) accepted++;
            // The two per-repaint checks the runtime really runs on a standing board: neither may refuse it.
            if (!ResultBoardHold.HeldGridFits(boardStrip, boardStrip, placed, boardHeight, Height, 2f)) wobbles++;
            if (!ResultBoardHold.TryHold(boardStrip, placed, Height, out LayoutRect held, settled.Top) || !near(held.Y, placed.Y)) wobbles++;
        }
        check(boardFits && wobbles == 0,
            "sixteen further repaints measure the same band, fit the same rectangle and pass both per-repaint checks: nothing swings back and forth, and the vanilla page never comes back");
        check(accepted == 16,
            "and every one of those repaints accepts the board at the very rectangle the first visible frame placed it at");

        // A later, higher control: the gate keeps the highest wall it has seen, so the band may only tighten - and
        // `Measure` (a standing board) never withdraws anything.
        check(ResultClientFailureStrip.Measure(122, gate, Evidence(WallLow, 0, true), out ResultClientBandReading tightened, out _)
              == ResultClientStripState.Placed && near(tightened.Bottom, WallLow - clearance),
            "a control that arrives later and higher tightens the band through `Measure` and never takes the standing board away");

        // The item added this round belongs to the first placement only: a page that switches its own actions off again
        // after the board stands is not a reason to withdraw it.
        ResultClientStripState afterActionsOff = ResultClientFailureStrip.Measure(123, gate,
            new ResultClientFailureEvidence(TitleBottom, Width, Height, Gap, MinimumStrip, WaitingText, float.NaN,
                true, true, 0, pageActionsPending: true), out ResultClientBandReading heldBand, out _);
        check(afterActionsOff == ResultClientStripState.Placed && near(heldBand.Bottom, WallLow - clearance),
            "a later frame whose page actions are off again - and whose pressable reading is empty - still hands out the tightened band: `Measure` does not read the page-action item, so nothing a page switches off can withdraw a board that is already up");
    }

    // ---------------------------------------------------------------------------------------------------
    // A (escape): the existing 60 refusals still answer, and the answer is fail-open
    // ---------------------------------------------------------------------------------------------------
    private static void ClientFailureEscape(Action<bool, string> check)
    {
        // A page whose own actions never come and which never shows a pressable control: the strict gate refuses for
        // ever, which is correct because nothing has been measured. The *existing* budget answers instead. What must not
        // happen is a page with no board at all, and what must not be claimed is that it settled.
        float boardFloor = ResultsFontGrowth.MinimumBoardHeight(Rows, PanelGeometry.MinimumRowEm, PanelDensity.Compact);
        var gate = new ResultClientWallGate();
        int misses = 0, strictPlacements = 0, budgetFrames = 0, answered = 0, committed = 0;
        for (int frame = 0; frame < 200; frame++)
        {
            ResultClientStripState state = ResultClientFailureStrip.Decide(3000 + frame, gate,
                new ResultClientFailureEvidence(TitleBottom, Width, Height, Gap, MinimumStrip, WaitingText, float.NaN,
                    true, true, 0, pageActionsPending: true), out ResultClientBandReading band, out _);
            if (state == ResultClientStripState.Placed && band.Fits) strictPlacements++;
            misses = ResultPlacementPolicy.Spend(misses);
            if (ResultPlacementPolicy.Decide(false, misses, false) != ResultPlacementOutcome.PlacedFallback) continue;
            budgetFrames++;
            // The escape's own gate, fed exactly as `FallbackPlacement` feeds it from the page's scan.
            bool pageReadable = ResultFallbackPolicy.PageReadable(true, true, 0);
            if (!ResultFallbackPolicy.MayAnswer(misses, false, pageReadable)) continue;
            answered++;
            ResultPageWalls walls = ResultPageLowerBoundPolicy.Resolve(true, Height, float.NaN, float.NaN, float.NaN,
                ResultBoundSource.None);
            if (ResultFallbackBandPolicy.TryResolve(Height, TitleBottom, ResultBoundSource.TitleAnchor, walls, float.NaN,
                    float.NaN, float.NaN, boardFloor, out ResultFallbackPlan plan)
                && ResultFallbackLayout.TryPlaceInBand(Width, Height, plan.Top, plan.Bottom, 900f, boardFloor, 0f,
                    out LayoutRect _)) committed++;
        }
        check(strictPlacements == 0,
            "two hundred frames of a client page that never shows a control and never finishes its own actions place nothing strictly: this gate is never satisfied by waiting");
        check(budgetFrames > 0 && answered == budgetFrames && committed == budgetFrames,
            "and the existing refusal budget answers every one of those frames after " + ResultFallbackPolicy.MissBudget
            + " refusals - the fail-open band really commits a rectangle (" + answered + " of " + budgetFrames
            + " windows) - so an extreme page is drawn rather than left empty");
        check(!gate.Settled && !float.IsFinite(gate.Wall),
            "and none of those frames was called settled: the gate never agreed on a wall (there was none), which is exactly why the answer had to come from the refusal budget and is logged as fail-open rather than as a proven page");
    }

    // ---------------------------------------------------------------------------------------------------
    // B. 成功页：首放要等原生 reveal 账本，但不等任何帧数
    // ---------------------------------------------------------------------------------------------------
    private static void SuccessRevealGate(Action<bool, string> check)
    {
        // The production ledger and the production latch, fed exactly as `ResultsDisplay.CompletionReady` feeds them.
        var ledger = new BlinkCompletionLedger();
        var readiness = new CompletionReadiness();
        readiness.Bind(new IntPtr(0x51CCE55));

        bool RevealDone()
        {
            if (readiness.Ready) return true;
            var evidence = new CompletionEvidence(ledger.Artwork > 0, ledger.NamesReady,
                ledger.Artwork > 0, ledger.ArtworkReady, ledger.Artwork > 0);
            return readiness.Observe(evidence);
        }

        // The recorded page's six scheduled targets: two captions and four pieces of artwork.
        long[] targets = { 31, 32, 41, 42, 43, 44 };
        bool[] names = { true, true, false, false, false, false };
        for (int index = 0; index < targets.Length; index++)
        {
            ledger.Require(targets[index], names[index]);
            ledger.Start(targets[index], 900 + index);
        }

        // A strip measured from the section's own edge. The placement only has to be *allowed*, and the rectangle it
        // produces must not change once it is: what matters is whether a rectangle appears, not its size.
        var strip = new BoardStrip(373.33f, 1265.67f, false, float.NaN, float.NaN, true,
            ResultFooterSource.ClientNoFooterSafeArea, ResultsFontGrowth.FillCeiling(ResultFooterSource.ClientNoFooterSafeArea, true));
        float boardHeight = ResultsFontGrowth.MinimumBoardHeight(Rows, PanelGeometry.MinimumRowEm, PanelDensity.Compact);

        int placements = 0, firstPlacementFrame = -1, movedAfter = 0;
        LayoutRect board = default;
        for (int frame = 0; frame < 10; frame++)
        {
            // `0/6`, then `3/6`, then the last three - the recorded order.
            if (frame == 1) { ledger.Finish(31, 900, true); ledger.Finish(41, 902, true); ledger.Finish(42, 903, true); }
            if (frame == 2) { ledger.Finish(32, 901, true); ledger.Finish(43, 904, true); ledger.Finish(44, 905, true); }
            bool revealDone = RevealDone();
            if (ResultRevealGate.BlocksPlacement(revealDone, ledger.MayStillComplete, forced: false)) continue;
            placements++;
            if (!ResultBoardHold.TryPlace(Width, Height, strip, 1160f, boardHeight, 0f, out LayoutRect rect, 2f))
            {
                movedAfter++;
                continue;
            }
            if (firstPlacementFrame < 0) { firstPlacementFrame = frame; board = rect; }
            else if (!near(rect.Y, board.Y) || !near(rect.Bottom, board.Bottom)) movedAfter++;
        }
        check(firstPlacementFrame == 2 && movedAfter == 0,
            "the board first appears on exactly the frame the native ledger reads 6/6 (frame " + firstPlacementFrame
            + " of ten, " + placements + " frames allowed in all): nothing on 0/6 or 3/6, and every later frame places the same rectangle, so the reveal costs no re-measure and no visible move");
        check(ledger.Ready && ledger.Completed == 6 && ledger.Count == 6,
            "the ledger itself is unchanged by this round: `Ready` still means every scheduled target really finished");

        // Escape 1: a target the page handed over with no object - the reveal can never finish, so the board must not wait.
        var missing = new BlinkCompletionLedger();
        missing.Require(0, true);
        check(missing.RevealImpossible && !missing.MayStillComplete
              && !ResultRevealGate.BlocksPlacement(false, missing.MayStillComplete, forced: false),
            "a ledger that was handed a target with no object can never finish, and says so: the placement answers at once, exactly as every earlier build did");
        // Escape 2: more targets than the ledger can hold - the recorded set is incomplete, so an absence cannot be proven.
        var overspent = new BlinkCompletionLedger();
        for (long target = 1; target <= 70; target++) overspent.Require(target, false);
        check(overspent.RevealImpossible && !overspent.MayStillComplete
              && !ResultRevealGate.BlocksPlacement(false, overspent.MayStillComplete, forced: false),
            "a page that scheduled more targets than the ledger holds gets the same answer: it cannot prove completion, so it does not withhold the board");
        // Escape 3: F8.
        var fresh = new BlinkCompletionLedger();
        fresh.Require(31, true);
        check(!ResultRevealGate.BlocksPlacement(false, fresh.MayStillComplete, forced: true),
            "F8 never waits for the reveal: that press is the player's own answer");
        // Escape 4: nothing has been observed yet. Not ready - but *still possibly complete*, so the gate must not release
        // a page it knows nothing about, and no frame count is anywhere in that answer.
        var unknown = new BlinkCompletionLedger();
        check(!unknown.Ready && unknown.MayStillComplete && !unknown.RevealImpossible
              && ResultRevealGate.BlocksPlacement(false, unknown.MayStillComplete, forced: false),
            "a page whose reveal has not been observed yet is not released either: 'nothing seen' is not 'nothing to see', and there is no frame count, empty scan or alpha approximation in this answer");
    }

    // ---------------------------------------------------------------------------------------------------
    // B2. 成功页的 fail-open 决定也要过 reveal 门（独立评审 F1；把它抽成 `ResultSettlementGate` 的纯函数是 F4）
    // ---------------------------------------------------------------------------------------------------
    //
    // 记录下来的那一页：fail-open 在账本还是 0/6 时就被 60 次拒收放行，板上界用的是完成区**容器**的 313；reveal 随后
    // 把题注的 352 交出来，站着的板被重新量到低约 39px（日志写 region re-measured）。所以 fail-open 的决定现在也问同一个
    // reveal 账本：未完成就不落板；只有账本一次都没被观察到时，才由既有的 give-up 预算放行，并明确写成
    // reveal-unobservable 而不是 settled。
    private static void SettlementGateDecidesTheFailOpenPath(Action<bool, string> check)
    {
        // The completion section of the recorded page: the container ends at 313, the caption its icon draws at 352.
        var parts = new List<CompletionPart>
        {
            new CompletionPart(true, true, 1f, 313f, 313f, true),
            new CompletionPart(true, true, 1f, 313f, 352f, true)
        };
        // The strip the board hangs from, through the arithmetic the placement itself uses.
        float BandTop(bool revealFinished)
        {
            float edge = CompletionFinalBounds.Placed(parts, revealFinished, out _, out _);
            return ResultsBounds.TryResolve(Height, edge, ResultBoundSource.IconRow, 1287f, float.NaN, float.NaN, out ResultsBand band)
                ? band.Top : float.NaN;
        }
        // `ResultFinalPolicy`'s own facts on that page: the container reading was readable and identical on every frame, so
        // its latch had settled - which is exactly why the fail-open path answered without ever asking the reveal.
        bool everMeasurable = true, finalMeasurable = true, finalSettled = true;
        const int finalWait = 0;

        // 1. The reveal is running (3 of 6) and the refusal budget is spent: no board.
        var ledger = new BlinkCompletionLedger();
        long[] targets = { 31, 32, 41, 42, 43, 44 };
        bool[] names = { true, true, false, false, false, false };
        for (int index = 0; index < targets.Length; index++)
        {
            ledger.Require(targets[index], names[index]);
            ledger.Start(targets[index], 900 + index);
        }
        ledger.Finish(31, 900, true);
        ledger.Finish(41, 902, true);
        ledger.Finish(42, 903, true);

        int misses = 0, answered = 0, refused = 0;
        for (int frame = 0; frame < 400; frame++)
        {
            misses = ResultPlacementPolicy.Spend(misses);
            if (ResultPlacementPolicy.Decide(false, misses, false) != ResultPlacementOutcome.PlacedFallback) continue;
            ResultSettlementGate.Decision decision = ResultSettlementGate.Decide(false, true, ledger.Ready,
                ledger.RevealImpossible, ledger.Count, 0, everMeasurable, finalMeasurable, finalSettled, finalWait);
            if (decision.MayFailOpen) answered++; else refused++;
        }
        check(misses >= ResultFallbackPolicy.MissBudget && refused > 0 && answered == 0 && !ledger.Ready && ledger.Completed == 3,
            "with the native reveal at 3 of 6, four hundred frames past " + ResultFallbackPolicy.MissBudget
            + " refusals place nothing at all (" + refused + " refusals asked, " + answered
            + " answered): the fail-open band is withheld by the same ledger that withholds the strict placement, so no board is drawn at the container's 313 and then re-measured at 352");

        // 2. The reveal finishes: one placement, and its upper bound is the edge the reveal really draws to.
        ledger.Finish(32, 901, true);
        ledger.Finish(43, 904, true);
        ledger.Finish(44, 905, true);
        ResultSettlementGate.Decision finished = ResultSettlementGate.Decide(false, true, ledger.Ready,
            ledger.RevealImpossible, ledger.Count, 0, everMeasurable, finalMeasurable, finalSettled, finalWait);
        float layoutTop = BandTop(false), finalTop = BandTop(true);
        float boardHeight = ResultsFontGrowth.MinimumBoardHeight(Rows, PanelGeometry.MinimumRowEm, PanelDensity.Compact);
        var strip = new BoardStrip(finalTop, 1265.67f, false, float.NaN, float.NaN, true,
            ResultFooterSource.ClientNoFooterSafeArea, ResultsFontGrowth.FillCeiling(ResultFooterSource.ClientNoFooterSafeArea, true));
        int placements = 0, moved = 0;
        LayoutRect board = default;
        for (int frame = 0; frame < 10; frame++)
        {
            if (!ResultBoardHold.TryPlace(Width, Height, strip, 1160f, boardHeight, 0f, out LayoutRect rect, 2f)) continue;
            if (placements++ == 0) board = rect;
            else if (!near(rect.Y, board.Y) || !near(rect.Bottom, board.Bottom)) moved++;
        }
        check(finished.MayFailOpen && ledger.Ready && ledger.Completed == 6
              && near(layoutTop, 334.33f) && near(finalTop, 373.33f) && near(finalTop - layoutTop, 352f - 313f),
            "the frame the ledger reads 6/6 answers, and the strip it answers on hangs from the caption's real edge: "
            + layoutTop.ToString("0.00") + " while the reveal was running, " + finalTop.ToString("0.00")
            + " once it finished - the recorded page's 39 px, taken before the first board rather than after it");
        check(placements == 10 && moved == 0 && near(board.Y, finalTop),
            "and every repaint after it places the same rectangle at " + board.Y.ToString("0.00")
            + ": one placement at the edge the reveal produced, instead of a board drawn at 313 and moved down to 352");

        // 3. A reveal that was never observed at all: the existing give-up budget answers, and says what it is.
        var empty = new BlinkCompletionLedger();
        bool early = ResultSettlementGate.Decide(false, true, false, empty.RevealImpossible, empty.Count,
            ResultFinalPolicy.MaximumWaitFrames - 1, everMeasurable, finalMeasurable, finalSettled, finalWait).MayFailOpen;
        ResultSettlementGate.Decision unobserved = ResultSettlementGate.Decide(false, true, false, empty.RevealImpossible,
            empty.Count, ResultFinalPolicy.MaximumWaitFrames, everMeasurable, finalMeasurable, finalSettled, finalWait);
        string unobservedWhy = ResultSettlementGate.Why(unobserved.Why, empty.Completed, empty.Count, ResultFinalPolicy.MaximumWaitFrames);
        check(empty.Count == 0 && !early && unobserved.MayFailOpen && unobserved.Unobservable && unobserved.RevealHeld
              && unobserved.Why == ResultSettlementGate.Answer.RevealUnobservable
              && unobservedWhy.Contains("reveal-unobservable") && unobservedWhy.Contains("not a settled page"),
            "a page the game never scheduled a single BlinkIn for is answered too - but only after " + ResultFinalPolicy.MaximumWaitFrames
            + " frames (the give-up budget this build already had, not a new number) and with a line that calls it a reveal-unobservable fail-open rather than a settled page");

        // 4. The two escapes the reveal gate always had, and the failure page, which never feeds that ledger.
        var broken = new BlinkCompletionLedger();
        broken.Require(0, true);
        var overspent = new BlinkCompletionLedger();
        for (long target = 1; target <= 70; target++) overspent.Require(target, false);
        check(ResultSettlementGate.Decide(true, true, false, false, ledger.Count, 0, everMeasurable, finalMeasurable, finalSettled, finalWait).Why
                  == ResultSettlementGate.Answer.Forced
              && ResultSettlementGate.Decide(true, true, false, false, 0, 0, everMeasurable, finalMeasurable, finalSettled, finalWait).MayFailOpen,
            "F8 answers on the very frame it is pressed, whatever the reveal is doing - and on a page that has no reveal to observe as well");
        check(ResultSettlementGate.Decide(false, true, false, broken.RevealImpossible, broken.Count, 0, everMeasurable, finalMeasurable, finalSettled, finalWait).Why
                  == ResultSettlementGate.Answer.RevealImpossible
              && ResultSettlementGate.Decide(false, true, false, overspent.RevealImpossible, overspent.Count, 0, everMeasurable, finalMeasurable, finalSettled, finalWait).MayFailOpen,
            "a ledger that can no longer finish - a target handed over with no object, or more targets than it holds - still answers at once");
        check(ResultSettlementGate.Decide(false, false, false, false, 0, 0, false, false, false, 0).MayFailOpen
              && !ResultSettlementGate.Decide(false, false, false, false, 0, 0, true, true, false, 0).MayFailOpen,
            "a failure page (the completion ledger is the success page's, so the reveal is not enforced there) keeps exactly the answer it had: nothing measurable, fail open; readable but still moving, wait");

        // 5. F5: the gate object the first placement needs is refused, never dereferenced.
        check(ResultClientFailureStrip.Decide(600, null, Evidence(WallHigh, 0, false), out ResultClientBandReading noWall, out _)
              == ResultClientStripState.WallNotSettled && !noWall.Fits,
            "a missing wall gate is refused by the first placement instead of throwing: no gate means no agreement, and no agreement means no band");
    }
    // ---------------------------------------------------------------------------------------------------
    // C. 成功页上界：容器底边与真实绘制底边的更深者
    // ---------------------------------------------------------------------------------------------------
    private static void SuccessUpperBound(Action<bool, string> check)
    {
        // The recorded success page: the completion section's own container ends at 313, the captions its partition
        // icons draw end at 352.
        var parts = new List<CompletionPart>
        {
            new CompletionPart(true, true, 1f, 313f, 313f, true),   // sectorHeader
            new CompletionPart(true, true, 1f, 313f, 352f, true),   // the icon whose 主要/精英 caption draws to 352
            new CompletionPart(true, true, 1f, 313f, float.NaN, true),
            new CompletionPart(true, true, 1f, 313f, float.NaN, true),
            new CompletionPart(true, true, 1f, 313f, float.NaN, true)
        };
        check(near(CompletionFinalBounds.Placed(parts, false, out int beforeDeepest, out _), 313f) && beforeDeepest == 0,
            "while the reveal is still running the section's edge is the laid-out slot alone (313) - the reading 1.19.21 pinned, and the one the recorded page was placed from");
        check(near(CompletionFinalBounds.Placed(parts, true, out int afterDeepest, out _), 352f) && afterDeepest == 1,
            "once the reveal has finished it is the deeper of that slot and the pixels really drawn under it (352), and the deepest part is the caption that draws there - this is the row the panel's own top edge was pressing on");

        // 313 and 352 are two upper bounds for the same window: the band hanging from them moves down by exactly the
        // caption's own height, and nothing else about it changes.
        check(ResultsBounds.TryResolve(Height, 313f, ResultBoundSource.IconRow, 1287f, float.NaN, float.NaN, out ResultsBand layoutBand)
              && ResultsBounds.TryResolve(Height, 352f, ResultBoundSource.IconRow, 1287f, float.NaN, float.NaN, out ResultsBand revealBand)
              && near(layoutBand.Top, 334.33f) && near(revealBand.Top, 373.33f)
              && near(revealBand.Top - layoutBand.Top, 352f - 313f),
            "the strip the board hangs from moves down by the caption's own 39 px (334.33 -> 373.33 on this window): the board starts below the captions instead of on top of them");

        // A scheduled slot the reveal has switched on is measured from what it draws, however deep that is.
        var deep = new List<CompletionPart>
        {
            new CompletionPart(true, true, 1f, 313f, 313f, true),
            new CompletionPart(true, true, 1f, 320f, 900f, true)
        };
        check(near(CompletionFinalBounds.Placed(deep, true, out _, out _), 900f),
            "a part that really draws deeper than its own container contributes that drawn edge once the reveal has finished");
        // A scheduled slot the reveal has not switched on yet contributes its laid-out slot and never its drawn pixels:
        // that is the "not arrived yet" half of the same rule, and it is what keeps the band from following the reveal.
        var notSwitchedOn = new List<CompletionPart>
        {
            new CompletionPart(true, true, 1f, 313f, 313f, true),
            new CompletionPart(true, false, 0f, 320f, 900f, true)
        };
        check(near(CompletionFinalBounds.Placed(notSwitchedOn, true, out _, out _), 320f),
            "a slot the page scheduled but has not switched on yet contributes its own laid-out rectangle (320) and never the pixels it would draw (900): the reveal's own progress is not the section's edge");
        // A part this page never scheduled at all - an unused sector icon of a short run - contributes nothing, drawn or
        // laid out, which is what keeps it from deepening the band.
        var unused = new List<CompletionPart>
        {
            new CompletionPart(true, true, 1f, 313f, 313f, true),
            new CompletionPart(true, false, 0f, 320f, 900f, false)
        };
        check(near(CompletionFinalBounds.Placed(unused, true, out _, out _), 313f),
            "and a slot the page never scheduled contributes nothing at all, drawn or laid out - an unused sector icon cannot deepen the band or lift it");

        // The full-screen plate: a background can never become the section's edge, because the measurement every part
        // goes through rejects a box that covers the window.
        var plateXs = new List<float>();
        var plateYs = new List<float>();
        for (int corner = 0; corner < 4; corner++)
        {
            plateXs.Add((corner & 1) == 0 ? 0f : Width);
            plateYs.Add((corner & 2) == 0 ? 0f : Height);
        }
        check(!ScreenBounds.TryFromCorners(plateXs, plateYs, Width, Height, out LayoutRect _),
            "a part that projects to the whole window is not a measurement of anything: the full-screen plate or background cannot enter the section's edge");
        check(ScreenBounds.TryFromCorners(new List<float> { 0f, Width, 0f, Width }, new List<float> { 1200f, 1200f, 1339f, 1339f },
                Width, Height, out LayoutRect content),
            "and a box that does not cover the window is still a measurement (a content band " + content.Height.ToString("0")
            + " px tall here), which is what makes the rejection above about the plate rather than about any wide part");
    }

    // ---------------------------------------------------------------------------------------------------
    // 换页 / 第二局：门与账本都从零开始
    // ---------------------------------------------------------------------------------------------------
    private static void PageChangeResets(Action<bool, string> check)
    {
        var gate = new ResultClientWallGate();
        check(ResultClientFailureStrip.Decide(400, gate, Evidence(float.NaN, 0, true), out _, out _) == ResultClientStripState.WallNotSettled
              && ResultClientFailureStrip.Decide(401, gate, Evidence(float.NaN, 0, true), out _, out _) == ResultClientStripState.WallNotSettled,
            "a client failure page starts by refusing, exactly as it does everywhere else in this file");
        gate.Forget();
        check(!gate.Settled && !float.IsFinite(gate.Wall) && gate.Samples == 0,
            "the next result screen inherits neither the previous page's wall nor its agreement");
        check(ResultClientFailureStrip.Decide(500, gate, Evidence(float.NaN, 0, true), out _, out _) == ResultClientStripState.WallNotSettled,
            "and the page-action item is asked again on it: the first frame of the second expedition is refused exactly like the first one's");
        check(ResultClientFailureStrip.Decide(501, gate, Evidence(WallMid, 0, false), out _, out _) == ResultClientStripState.WallNotSettled
              && ResultClientFailureStrip.Decide(502, gate, Evidence(WallMid, 0, false), out ResultClientBandReading second, out _) == ResultClientStripState.Placed
              && near(second.Bottom, WallMid - ResultsBounds.Clearance(Height)),
            "and the same sequence places it on its own wall, never on the previous page's");

        // The first run's own page: one caption target and one piece of artwork, both really finished.
        var ledger = new BlinkCompletionLedger();
        ledger.Require(31, true);
        ledger.Start(31, 700);
        ledger.Finish(31, 700, true);
        ledger.Require(41, false);
        ledger.Start(41, 701);
        ledger.Finish(41, 701, true);
        var readiness = new CompletionReadiness();
        readiness.Bind(new IntPtr(0x51CCE55));
        check(ledger.Ready && readiness.Observe(new CompletionEvidence(ledger.Names > 0, ledger.NamesReady,
                  ledger.Artwork > 0, ledger.ArtworkReady, ledger.Artwork > 0)),
            "the first run's own ledger can complete its reveal, and the latch answers so");
        ledger.Clear();
        readiness.Forget();
        check(!ledger.Ready && ledger.Count == 0 && ledger.MayStillComplete && !readiness.Ready,
            "a new page visit clears the reveal ledger and the latch even when the page object is reused, and the fresh ledger is 'not observed yet' rather than 'impossible'");
        // 第二局：它自己的两个目标（一个题注、一个图标），并且这两个都必须自己完成。
        ledger.Require(55, true);
        ledger.Start(55, 800);
        ledger.Require(56, false);
        ledger.Start(56, 801);
        check(ResultRevealGate.BlocksPlacement(Reveal(), ledger.MayStillComplete, forced: false),
            "and the second run's own reveal withholds its board until its own ledger completes, with nothing carried over from the first");
        ledger.Finish(55, 800, true);
        check(ResultRevealGate.BlocksPlacement(Reveal(), ledger.MayStillComplete, forced: false),
            "the caption alone does not release it: the artwork target is still running");
        ledger.Finish(56, 801, true);
        check(!ResultRevealGate.BlocksPlacement(Reveal(), ledger.MayStillComplete, forced: false),
            "and its own ledger releasing is what releases the board");

        // The latch, fed exactly as `ResultsDisplay.CompletionReady` feeds it from this page's own ledger.
        bool Reveal()
        {
            if (readiness.Ready) return true;
            return readiness.Observe(new CompletionEvidence(ledger.Artwork > 0, ledger.NamesReady,
                ledger.Artwork > 0, ledger.ArtworkReady, ledger.Artwork > 0));
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // The runtime really asks these, in this order
    // ---------------------------------------------------------------------------------------------------
    private static void ProductionWiring(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the settlement placement regression locates the production sources");
        if (root == null) return;
        string display = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        string geometry = File.ReadAllText(Path.Combine(root, "GuestPageGeometry.cs"));
        string final = File.ReadAllText(Path.Combine(root, "ResultFinalLayout.cs"));
        string ledger = File.ReadAllText(Path.Combine(root, "BlinkCompletionLedger.cs"));

        // A: the client branch derives the item from the one fact it can honestly read, prints the rest once for the
        // record, and only then asks the shared decision; the standing-board path is given the same evidence without it.
        int pendingAt = display.IndexOf("bool pageActionsPending = ResultClientPageActionPolicy.Pending(", StringComparison.Ordinal);
        int decideAt = display.IndexOf("ResultClientFailureStrip.Decide(Time.frameCount, ClientWall, clientEvidence", StringComparison.Ordinal);
        check(pendingAt >= 0 && decideAt > pendingAt
              && display.Contains("bool pageActionsPending = ResultClientPageActionPolicy.Pending(pressableTop);")
              && !display.Contains("ResultClientPageActionPolicy.Pending(\n                    controls.HiddenPageActions,")
              && display.Contains("hidden={controls.Hidden},") && display.Contains("hiddenPageActions={controls.HiddenPageActions},pressable=")
              && display.Contains("HeadingReports.Add(\"failure-client-page-actions-pending-to-fail-open\")"),
            "the client failure branch asks the honest predicate with the only fact it reads (the frame's pressable top), prints hidden/hiddenPageActions/pressable once per page - including the once-per-page line for a page that stays pending until the fail-open budget answers it - and only then asks the shared decision");
        check(geometry.Contains("public static bool Pending(float pressableTop) => !float.IsFinite(pressableTop);")
              && geometry.Contains("bool answerable = scanComplete && parentCoverageComplete && !pageActionsPending && (readable || unmeasuredControls == 0)")
              && geometry.Contains("pageActionsPending: false);"),
            "the predicate is exactly 'no pressable control measured' (the four inputs that had no per-frame information on a client page are gone), the gate makes a pending frame unanswerable, and `Measure` - the standing board - passes the item as false, so nothing a page switches off later can withdraw a board that is already up");

        // B: the reveal is asked before the section is measured, the gate is a policy call with an escape, and the
        // per-repaint safety checks do not ask it at all.
        int revealAsked = display.IndexOf("bool revealDone = CompletionReady(page);", StringComparison.Ordinal);
        int regionAt = display.IndexOf("bool completionMeasured = SuccessRegion(page, out ResultsSafeArea area", StringComparison.Ordinal);
        int gateAt = display.IndexOf("if (ResultRevealGate.BlocksPlacement(revealDone, CompletionTargets.Ledger.MayStillComplete, Forced))", StringComparison.Ordinal);
        check(revealAsked >= 0 && regionAt > revealAsked && gateAt > regionAt
              && display.Contains("HeadingReports.Add(\"success-reveal-pending\")"),
            "the success placement asks the native ledger first, measures the section second and refuses the frame on the gate third - once per page, under its own reason");
        check(!Body(display, "private static bool BoardStillSafe()").Contains("ResultRevealGate")
              && !Body(display, "private static bool BoardStillSafe()").Contains("MayStillComplete"),
            "`BoardStillSafe` does not ask the reveal gate: a board that is already standing is never taken away because the page's reveal moved on");
        check(ledger.Contains("public bool RevealImpossible => _missing;")
              && ledger.Contains("public bool MayStillComplete => !_missing;")
              && !Body(ledger, "public void Finish(").Contains("RevealImpossible")
              && !Body(ledger, "public void Require(").Contains("MayStillComplete"),
            "the ledger gained only the two read-only answers this gate needs; `Require`, `Start`, `Finish` and `Ready` are untouched");
        check(final.Contains("public static class ResultRevealGate")
              && Body(final, "public static class ResultRevealGate").Contains("!forced && !revealFinished && mayStillComplete")
              && !Body(final, "public static class ResultRevealGate").Contains("MissBudget")
              && !Body(final, "public static class ResultRevealGate").Contains("MaximumWaitFrames"),
            "the gate itself is three facts and no budget: not forced, not finished, and still able to finish - there is no frame count anywhere in it");

        // B2: the fail-open decision asks the reveal too, through one pure function, and `Prepare` really calls it.
        int settlementAt = display.IndexOf("ResultSettlementGate.Decision settlement = ResultSettlementGate.Decide(Forced, succeeded, RevealFinished,", StringComparison.Ordinal);
        int fallbackAt = display.IndexOf("if (!FallbackPlacement(page, succeeded, out _, out room, out strip, out string fallbackWhy))", StringComparison.Ordinal);
        int revealLogAt = display.IndexOf("HeadingReports.Add(\"fail-open-reveal-unobservable\")", StringComparison.Ordinal);
        string prepare = Body(display, "private static void Prepare(Component page, bool succeeded)");
        string revealWait = Body(display, "private static int RevealWaitFrames()");
        check(settlementAt >= 0 && fallbackAt > settlementAt && revealLogAt > settlementAt && prepare.Contains("ResultSettlementGate.Decide("),
            "the fail-open path in `Prepare` asks the settlement gate before it invents a band, and its reveal-unobservable answer has its own once-per-page line");
        check(revealWait.Contains("CompletionTargets.Ledger.Count > 0") && !revealWait.Contains("MissBudget")
              && !revealWait.Contains("MaximumWaitFrames"),
            "the reveal wait clock only runs while the ledger is empty and carries no budget of its own: the one number that releases it is `ResultFinalPolicy.MaximumWaitFrames`, read in the settlement gate - never a second, shorter count here or the 60 refusals there");
        string completion = File.ReadAllText(Path.Combine(root, "CompletionTargets.cs"));
        check(completion.Contains("RequireOptional(icon, icon.m_bgHolder, false, \"m_bgHolder\")")
              && completion.Contains("RequireOptional(icon, icon.m_skullHolder, false, \"m_skullHolder\")")
              && !completion.Contains("target != null ? target.Pointer.ToInt64() : 0")
              && Body(completion, "internal static void Require(GameObject target, bool name)").Contains("if (target == null) return;")
              && Body(completion, "private static void RequireOptional(").Contains("Plugin.Logger?.LogWarning")
              && Body(completion, "internal static void BeginPage()").Contains("_skipReported = false;"),
            "a sector icon's optional holder that this build does not have is skipped and reported once per page - never turned into `Ledger.Require(0, ...)`, which marked the whole ledger impossible and silently released every gate on that page");
        check(Body(final, "public static class ResultSettlementGate").Contains("ResultFinalPolicy.MaximumWaitFrames")
              && !Body(final, "public static class ResultSettlementGate").Contains("MissBudget")
              && !Body(final, "public static class ResultSettlementGate").Contains(">= 60,"),
            "the settlement gate uses the give-up budget and never the 60-refusal fail-open budget: sixty refused frames are not evidence that a page's reveal finished");

        // C: the section's edge and the icon row are one call, and the TMP text is measured in its own right.
        check(display.Contains("CompletionFinalBounds.Placed(parts, RevealFinished, out int finalDeepest, out _finalUndrawn)")
              && display.Contains("IconRowBottom(success, RevealFinished)")
              && display.Contains("IconRowBottom(page, RevealFinished)"),
            "the section's edge and the icon row the band hangs from read the same reveal state, so the two cannot disagree about which reading the board was placed from");
        check(display.Contains("var texts = element.GetComponentsInChildren<TMP_Text>(true);")
              && display.Contains("if (TryTextRect(text, out LayoutRect drawn))")
              && display.Contains("private static bool TryTextRect(TMP_Text text, out LayoutRect rect)")
              && Body(display, "private static bool TryTextRect(").Contains("text.textBounds")
              && display.Contains("private static Camera PageCamera(Component component, out bool overlay)"),
            "the completion parts are measured through Renderer bounds and through every TextMeshPro text under them, projected through one camera ladder: a TextMeshProUGUI caption carries no bounds-carrying renderer, and its own laid-out extent is what the band must clear");
        check(!Body(display, "private static float IconRowBottom(").Contains("m_sectorsWhiteBox"),
            "and the plate behind the row is still not one of the parts the edge is computed from");
    }

    // One frame of the recorded client page, as production's own measurement hands it over.
    private static ResultClientFailureEvidence Evidence(float pressableTop, int unmeasurable, bool pending)
        => new ResultClientFailureEvidence(TitleBottom, Width, Height, Gap, MinimumStrip, WaitingText, pressableTop,
            true, true, unmeasurable, pending);

    // One method's or type's body by brace pairing: what belongs to it, never what it means.
    private static string Body(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        int open = source.IndexOf('{', start);
        if (open < 0) return string.Empty;
        int depth = 0;
        for (int index = open; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0) return source.Substring(open, index - open + 1);
            }
        }
        return string.Empty;
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj"))) directory = directory.Parent;
        return directory != null ? directory.FullName : null;
    }

    private static bool near(float actual, float expected) => Math.Abs(actual - expected) <= 0.02f;
}
