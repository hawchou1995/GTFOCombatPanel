using System;
using System.Collections.Generic;
using System.IO;

using HawChow.GTFOCombatPanel;

// 1.19.41: the three settlement-placement gates this round adds, driven the way the display drives them.
//
//   A  the client failure page's first placement.  `LogOutput.snapshot.log` 4426-4441 (2560x1440, client, run 4): two
//      frames whose scan was complete but which found no pressable control at all were answered as "this page has no
//      wall", the strip was the whole screen foot (381..1429) and a 1987x990 board was drawn; the next frame found the
//      page's own control at 1175/1181/1189, the strip tightened to 802 px, the standing board really reached that
//      control and was taken away with the vanilla page restored. 1.19.45 (user correction) settles what the wall is:
//      the client's non-clickable "waiting for the host" line - the exact counterpart of the host's pressable button -
//      and a pressable control only when it reaches higher. `ResultClientPageActionPolicy.Pending` therefore answers
//      "no wall at all", and `ResultClientWallGate.Observe` is fed that one combined reading through the single entry
//      point production calls (`ResultClientFailureStrip.Decide` / `Measure`).
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
    // scaled by height, the waiting-status line at 1147 - the page's own non-clickable text, and the wall (1.19.45:
    // "his lower bound is nothing but the host's clickable button becoming the client's non-clickable hint text").
    private const float Width = 2560f, Height = 1440f;
    private const float TitleBottom = 359.5f;
    private const float Gap = 16f, MinimumStrip = 96f, WaitingText = 1147f;
    // The pressable controls the scan found on that page, in the three readings the log recorded - all *below* the
    // waiting line, so none of them tightens the band; `ControlAboveText` is a control that reaches higher than the
    // line and therefore does.
    private const float WallHigh = 1189f, WallMid = 1181f, WallLow = 1175f, ControlAboveText = 1050f;
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

    // A. 客机失败页：下界 = 那行不可点击的等待文字自己的上沿（或更靠上的可按控件），先稳定两帧再落板
    // ---------------------------------------------------------------------------------------------------
    // 1.19.45（用户纠正，覆盖 1.19.38/1.19.41/1.19.42 的相反口径）：客机失败页没有「返回大厅」按钮，但它有那行
    // 「正在等待厅主」提示文字的**实际屏幕矩形**——那就是这一页的墙，和主机页那条按钮上沿同一性质。真正可按的
    // 控件照旧是墙，两者取更靠上的那一堵（更安全的一堵）；页面固定几何（1429.33）只做封顶，永远不是「量到了墙」。
    // 门仍然是「同一堵墙、两帧、都可回答」，但它现在读的是这个合成后的读数，所以「一个可按控件都没有」的帧不再
    // 是不可回答——记录下来的那一页本来就有那行字。
    private static void ClientFailureFirstPlacement(Action<bool, string> check)
    {
        // The display's own figure: the design clearance of 16 px at 1080p, scaled to this window.
        float clearance = ResultsBounds.Clearance(Height);
        float fixedTop = TitleBottom + Gap * (Height / 1080f);
        float textFoot = WaitingText - clearance;

        // 1. 这一页的墙就是那行不可点击的提示文字。
        check(!ResultClientPageActionPolicy.Pending(WaitingText, float.NaN)
              && !ResultClientPageActionPolicy.Pending(WaitingText, WallMid)
              && ResultClientPageActionPolicy.Pending(float.NaN, float.NaN),
            "the client page's wall question is answered by the waiting-status line's own rectangle: a page with no pressable control at all is not 'pending' while that line is measurable, and only a frame that measures neither the line nor a control is");

        // 2. 门：同一堵墙、两帧、都可回答。
        var gate = new ResultClientWallGate();
        var textOnly = new ResultClientFailureEvidence(TitleBottom, Width, Height, Gap, MinimumStrip, WaitingText, float.NaN);
        check(ResultClientFailureStrip.Decide(100, gate, textOnly, out ResultClientBandReading noBand, out ResultsSafeArea fixedPage)
              == ResultClientStripState.WallNotSettled
              && !gate.Settled && gate.Samples == 1 && !noBand.Fits && near(gate.Wall, WaitingText),
            "the first frame that measures the waiting line is a reading, not an agreement: the wall is recorded (" + WaitingText
            + ") and no band is handed out yet");
        check(near(fixedPage.Top, fixedTop) && near(fixedPage.Bottom, ResultsGuestPageGeometry.PageFloor(Height)),
            "and the page's fixed geometry is still read on it (" + fixedTop.ToString("0.00") + "..1429.33) - as the band's cap, never as a wall");
        check(ResultClientFailureStrip.Decide(101, gate, textOnly, out ResultClientBandReading settled, out _)
              == ResultClientStripState.Placed && gate.Settled,
            "the second identical reading of that same wall is the placement frame - `Settled`, never a count of frames, is what released it");
        check(near(settled.Top, fixedTop) && near(settled.Bottom, textFoot) && near(settled.Height, textFoot - fixedTop),
            "the band is the page's fixed top down to the line's own top less the page clearance ("
            + fixedTop.ToString("0.00") + ".." + textFoot.ToString("0.00") + ", " + (textFoot - fixedTop).ToString("0.0")
            + " px): the recorded page's own geometry, never the 1049 px full-page band the recorded run-3 board was drawn in");
        check(settled.WaitingTextTightened && !settled.PressableTightened && settled.WallMeasured
              && near(settled.WallTop, WaitingText) && !settled.WaitingTextCovered,
            "and it names the wall that produced the foot: the waiting line's own top, so the board never stands on the text the player is reading");

        // f5 and on: the same reading, repaint after repaint, and the board does not move.
        var boardStrip = new BoardStrip(settled.Top, settled.Bottom, true, WaitingText, float.NaN, true,
            ResultFooterSource.ClientNoFooterSafeArea, ResultsFontGrowth.FillCeiling(ResultFooterSource.ClientNoFooterSafeArea, true));
        int font = ResultsBandFit.FontForBand(settled.Height, Rows, PanelDensity.Compact, 0);
        float boardHeight = ResultsBandFit.RowHeight(font, PanelDensity.Compact) * Rows;
        bool boardFits = ResultBoardHold.TryPlace(Width, Height, boardStrip, 1160f, boardHeight, 0f, out LayoutRect placed, 2f);
        int wobbles = 0, accepted = 0;
        for (int frame = 105; frame < 121; frame++)
        {
            ResultClientStripState state = ResultClientFailureStrip.Decide(frame, gate, textOnly,
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
        check(accepted == 16 && placed.Bottom <= WaitingText - ResultBoardHold.ButtonClearance + 0.5f,
            "and every one of those repaints accepts the board at the very rectangle the first visible frame placed it at, clear of the waiting line above it");

        // 3. 一个更高的可按控件：门记住本页最高的一堵墙，条带只会收紧，`Measure` 从不撤板。
        check(ResultClientFailureStrip.Measure(122, gate, Evidence(ControlAboveText, 0), out ResultClientBandReading tightened, out _)
              == ResultClientStripState.Placed && near(tightened.Bottom, ControlAboveText - clearance)
              && tightened.PressableTightened && !tightened.WaitingTextTightened,
            "a pressable control that reaches higher than the line tightens the band through `Measure` (" + tightened.Bottom.ToString("0.00")
            + ") and never takes the standing board away - the higher of the two walls wins, because it is the safer edge");

        // 4. 更低的控件不改变任何东西：那行字还是更靠上的那一堵墙。
        check(ResultClientFailureStrip.Measure(123, gate, Evidence(WallLow, 0), out ResultClientBandReading lower, out _)
              == ResultClientStripState.Placed && near(lower.Bottom, ControlAboveText - clearance),
            "a control below the waiting line changes nothing at all: the line is still the higher wall, and the highest wall this page ever showed is what the band stands on");

        // 5. 之后那一帧两个读数都量不到：本页见过的最高墙还记得住，板子不会被撤掉。
        var blind = new ResultClientFailureEvidence(TitleBottom, Width, Height, Gap, MinimumStrip, float.NaN, float.NaN);
        check(ResultClientFailureStrip.Measure(124, gate, blind, out ResultClientBandReading heldBlind, out _)
              == ResultClientStripState.Placed && near(heldBlind.Bottom, ControlAboveText - clearance),
            "a later frame whose line and control are both unmeasurable still hands out the tightened band: `Measure` reads the highest wall this page ever showed, so nothing a page switches off or stops drawing can withdraw a board that is already up");

        // 6. 画面固定下沿只是封顶：没有墙时它不会自己变成一条带宽 1049px 的带。
        ResultPageWalls floorOnly = ResultPageLowerBoundPolicy.Resolve(guestFailure: true, screenHeight: Height,
            footerControlTop: float.NaN, scanPressableTop: float.NaN, footerTextTop: float.NaN, footerTextSource: ResultBoundSource.None);
        check(Math.Abs(floorOnly.PageFloor - 1429.333f) <= 0.01f
              && !float.IsFinite(floorOnly.PressableTop) && !float.IsFinite(floorOnly.FooterTextTop)
              && !ResultFailureWallPolicy.WallMeasured(floorOnly, float.NaN, float.NaN, float.NaN),
            "the page's own screen-derived foot (1429.333) is a cap and not a wall: with nothing measured the fail-open gate is not satisfied by it either - that reading is exactly what drew the run-3 board over the control at 1177");
        ResultPageWalls textWall = ResultPageLowerBoundPolicy.Resolve(true, Height, float.NaN, float.NaN, WaitingText,
            ResultBoundSource.None);
        check(near(textWall.FooterTextTop, WaitingText) && textWall.FooterTextSource == ResultBoundSource.FooterText
              && ResultFailureWallPolicy.WallMeasured(textWall, float.NaN, float.NaN, float.NaN),
            "the same page with the waiting line measured hands it over as the footer wall (FooterText, 1147) and that IS a wall - host and client differ in what they draw there, never in whether it bounds the board");
    }

    // ---------------------------------------------------------------------------------------------------
    // A (escape): 一条墙都量不到的客机失败页不由帧数作答——保留原版；只有玩家自己的 F8 才给出那条带
    // ---------------------------------------------------------------------------------------------------
    private static void ClientFailureEscape(Action<bool, string> check)
    {
        // 1.19.45：一页从头到尾量不到任何墙（那行字和可按控件都不可测）。严格门永远不交条带，这是对的；
        // 而 fail-open 那一边**不能**因为 60 次拒收就把屏幕下界当墙——那正是 run 3 画出 band (488..1429) 的原因。
        float boardFloor = ResultsFontGrowth.MinimumBoardHeight(Rows, PanelGeometry.MinimumRowEm, PanelDensity.Compact);
        var gate = new ResultClientWallGate();
        var wallLess = new ResultClientFailureEvidence(TitleBottom, Width, Height, Gap, MinimumStrip, float.NaN, float.NaN);
        int misses = 0, strictPlacements = 0, budgetFrames = 0, answered = 0, refusedByWallPolicy = 0;
        for (int frame = 0; frame < 200; frame++)
        {
            if (ResultClientFailureStrip.Decide(3000 + frame, gate, wallLess, out ResultClientBandReading band, out _) == ResultClientStripState.Placed
                && band.Fits) strictPlacements++;
            misses = ResultPlacementPolicy.Spend(misses);
            if (ResultPlacementPolicy.Decide(false, misses, false) != ResultPlacementOutcome.PlacedFallback) continue;
            budgetFrames++;
            // The escape's own gates, fed exactly as `FallbackPlacement` feeds them from the page's scan.
            bool pageReadable = ResultFallbackPolicy.PageReadable(true, true, 0);
            if (!ResultFallbackPolicy.MayAnswer(misses, false, pageReadable)) continue;
            ResultPageWalls walls = ResultPageLowerBoundPolicy.Resolve(true, Height, float.NaN, float.NaN, float.NaN,
                ResultBoundSource.None);
            bool wallMeasured = ResultFailureWallPolicy.WallMeasured(walls, float.NaN, float.NaN, float.NaN);
            if (ResultFailureWallPolicy.Waits(wallMeasured, frame, guestFailure: true)) { refusedByWallPolicy++; continue; }
            answered++;  // 只有玩家自己的 F8 会走到这里：本过程喂的是一张没有任何墙的页面。
        }
        check(strictPlacements == 0,
            "two hundred frames of a client page that measures no wall at all place nothing strictly: this gate is never satisfied by waiting");
        check(budgetFrames > 0 && refusedByWallPolicy == budgetFrames && answered == 0,
            "and the refusal budget does not buy a board either: " + refusedByWallPolicy + " of " + budgetFrames
            + " budget windows reach the fail-open gate and every one of them is refused by `ResultFailureWallPolicy` - no frame count and no timeout turns 'no wall seen' into 'this page has no wall'");
        check(!gate.Settled && !float.IsFinite(gate.Wall),
            "and none of those frames was called settled: the gate never agreed on a wall (there was none)");

        // 玩家自己的 F8 仍然被回答：那一条带会用到屏幕自己的下界，日志必须把它说成「墙没被观测到」。
        bool waived = ResultFallbackBandPolicy.TryResolve(Height, TitleBottom, ResultBoundSource.TitleAnchor,
            ResultPageLowerBoundPolicy.Resolve(true, Height, float.NaN, float.NaN, float.NaN, ResultBoundSource.None),
            float.NaN, float.NaN, float.NaN, boardFloor, out ResultFallbackPlan waivedBand);
        check(waived && waivedBand.PageBand && waivedBand.Bottom > 1400f
              && ResultFailureWallPolicy.AnswerNote(false, 0, true).Contains("wallUnobserved=true")
              && ResultFailureWallPolicy.AnswerNote(false, 0, true).Contains("F8")
              && ResultFailureWallPolicy.AnswerNote(false, 0, true).Contains("screen-derived geometry"),
            "the player's own F8 is still answered, and the band it answers with is the page's own screen-derived geometry (bottom="
            + waivedBand.Bottom.ToString("0") + ") - the log line says the wall was never observed, so a successful fallback is never dressed up as the page's proven geometry");
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
        check(ResultClientFailureStrip.Decide(600, null, Evidence(WallHigh, 0), out ResultClientBandReading noWall, out _)
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
        // 这一页量不到任何墙：门不回答任何问题（两帧都拒绝）。
        check(ResultClientFailureStrip.Decide(400, gate, NoWall(), out _, out _) == ResultClientStripState.WallNotSettled
              && ResultClientFailureStrip.Decide(401, gate, NoWall(), out _, out _) == ResultClientStripState.WallNotSettled,
            "a client failure page that measures no wall at all starts by refusing, exactly as it does everywhere else in this file");
        gate.Forget();
        check(!gate.Settled && !float.IsFinite(gate.Wall) && gate.Samples == 0,
            "the next result screen inherits neither the previous page's wall nor its agreement");
        check(ResultClientFailureStrip.Decide(500, gate, NoWall(), out _, out _) == ResultClientStripState.WallNotSettled,
            "and the wall question is asked again on it: the first frame of the second expedition is refused exactly like the first one's");
        // 第二局自己量到那行等待文字：两帧之后它按自己的墙落板，而不是上一局的。
        check(ResultClientFailureStrip.Decide(501, gate, Evidence(WallMid, 0), out _, out _) == ResultClientStripState.WallNotSettled
              && ResultClientFailureStrip.Decide(502, gate, Evidence(WallMid, 0), out ResultClientBandReading second, out _) == ResultClientStripState.Placed
              && near(second.Bottom, Math.Min(WaitingText, WallMid) - ResultsBounds.Clearance(Height)),
            "and the same sequence places it on its own page's wall (the waiting line at 1147, which is higher than the control at 1181), never on the previous page's");

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

        // A: 客机失败页的墙 = 等待文字 / 可按控件里更靠上的那一堵；生产在落板前先算出这个读数、按页打印一次诊断，
        //    然后再问共享判定；`Measure`（站着的板）喂同一份事实。
        int pendingAt = display.IndexOf("bool wallPending = ResultClientPageActionPolicy.Pending(", StringComparison.Ordinal);
        int decideAt = display.IndexOf("ResultClientFailureStrip.Decide(Time.frameCount, ClientWall, clientEvidence", StringComparison.Ordinal);
        check(pendingAt >= 0 && decideAt > pendingAt
              && display.Contains("bool wallPending = ResultClientPageActionPolicy.Pending(waitingTextTop, pressableTop);")
              && display.Contains("hidden={controls.Hidden}") && display.Contains("hiddenPageActions={controls.HiddenPageActions}")
              && display.Contains("HeadingReports.Add(\"failure-client-wall-pending-to-fail-open\")"),
            "the client failure branch feeds the honest predicate the two readings that really are walls (the waiting line's own top and the highest pressable control), prints hidden/hiddenPageActions/waitingText/pressable once per page - including the once-per-page line for a page that stays pending until the fail-open budget answers it - and only then asks the shared decision");
        check(geometry.Contains("public static bool Pending(float waitingTextTop, float pressableTop)")
              && geometry.Contains("=> !float.IsFinite(waitingTextTop) && !float.IsFinite(pressableTop);")
              && geometry.Contains("bool answerable = scanComplete && parentCoverageComplete && readable && unmeasuredControls == 0;")
              && geometry.Contains("wall.Observe(frame, true, true, evidence.WallTop, evidence.UnmeasurableControls)")
              && geometry.Contains("evidence.WallTop, evidence.UnmeasurableControls);"),
            "the predicate is exactly 'this page measured no wall at all' - the waiting line counts, which is the whole correction - the gate makes a frame with no wall unanswerable, and both the first placement and the standing board (`Measure`) are fed the same combined reading");

        // B: the reveal is asked before the section is measured, the gate is a policy call with an escape, and the
        // per-repaint safety checks do not ask it at all.
        int revealAsked = display.IndexOf("bool revealDone = CompletionReady(page);", StringComparison.Ordinal);
        int regionAt = display.IndexOf("bool completionMeasured = SuccessRegion(page, out ResultsSafeArea area", StringComparison.Ordinal);
        int gateAt = display.IndexOf("if (ResultRevealGate.BlocksPlacement(revealDone, CompletionTargets.Ledger.MayStillComplete,", StringComparison.Ordinal);
        check(revealAsked >= 0 && regionAt > revealAsked && gateAt > regionAt
              && display.Contains("CompletionTargets.Ledger.InFlight, Forced)")
              && display.Contains("HeadingReports.Add(\"success-reveal-pending\")"),
            "the success placement asks the native ledger first, measures the section second and refuses the frame on the gate third - once per page, under its own reason - and the gate is asked with the in-flight count (F1), so one aborted target cannot release the board while the rest of the reveal still runs");
        check(!Body(display, "private static bool BoardStillSafe()").Contains("ResultRevealGate")
              && !Body(display, "private static bool BoardStillSafe()").Contains("MayStillComplete"),
            "`BoardStillSafe` does not ask the reveal gate: a board that is already standing is never taken away because the page's reveal moved on");
        // 1.19.43: the ledger's "no further reveal can arrive" answer now has a third source - a native routine that
        // really ended without revealing its target - and a target whose BlinkIn call never scheduled a routine is not
        // counted at all. Both are facts about the ledger, never a frame count, and `Ready` is still never faked.
        check(ledger.Contains("public bool RevealImpossible => _missing || Aborted > 0;")
              && ledger.Contains("public bool MayStillComplete => !RevealImpossible;")
              && ledger.Contains("private static bool Counted(Entry entry) => !entry.Excluded;")
              && ledger.Contains("public int CloseSchedule(IEnumerable<long> required)")
              && ledger.Contains("public bool FinishRoutine(long routine, bool naturallyRevealed)")
              && !Body(ledger, "public void Finish(").Contains("RevealImpossible")
              && !Body(ledger, "public void Require(").Contains("MayStillComplete")
              && !Body(ledger, "public void Finish(").Contains("Done = true")
              && !Body(ledger, "public void Clear(").Contains("Scheduled"),
            "the ledger gained the read-only answers this gate needs plus two native facts about its own targets; `Finish` still never fakes a completion, and `Require`/`Clear` are untouched");
        check(final.Contains("public static class ResultRevealGate")
              && Body(final, "public static class ResultRevealGate").Contains("!forced && !revealFinished && (mayStillComplete || inFlight > 0)")
              && Body(final, "public static class ResultRevealGate").Contains("BlocksPlacement(revealFinished, mayStillComplete, inFlight: 0, forced)")
              && !Body(final, "public static class ResultRevealGate").Contains("MissBudget")
              && !Body(final, "public static class ResultRevealGate").Contains("MaximumWaitFrames"),
            "the gate itself is facts about the ledger and no budget: not forced, not finished, and still able to finish or still running (F1) - the three-argument form delegates to it with no in-flight work, and there is no frame count anywhere in it");

        // B2: the fail-open decision asks the reveal too, through one pure function, and `Prepare` really calls it.
        int settlementAt = display.IndexOf("ResultSettlementGate.Decision settlement = ResultSettlementGate.Decide(Forced, succeeded, RevealFinished,", StringComparison.Ordinal);
        int fallbackAt = display.IndexOf("if (!FallbackPlacement(page, succeeded, out _, out room, out strip, out string fallbackWhy))", StringComparison.Ordinal);
        int revealLogAt = display.IndexOf("HeadingReports.Add(\"fail-open-reveal-unobservable\")", StringComparison.Ordinal);
        string prepare = Body(display, "private static void Prepare(Component page, bool succeeded)");
        string revealStall = Body(display, "private static bool RevealStalled(out float stalledSeconds, out float limitSeconds)");
        check(settlementAt >= 0 && fallbackAt > settlementAt && revealLogAt > settlementAt && prepare.Contains("ResultSettlementGate.Decide("),
            "the fail-open path in `Prepare` asks the settlement gate before it invents a band, and its reveal-unobservable answer has its own once-per-page line");
        check(revealStall.Length > 0
              && revealStall.Contains("RevealClock.Observe(now, ledger.Completed, ledger.InFlight, ledger.Count);")
              && !revealStall.Contains("MissBudget") && !revealStall.Contains("MaximumWaitFrames"),
            "the reveal clock is fed with the ledger's own facts - completed, in flight, scheduled - so any native movement defers it, and it carries no budget of its own: the only number that answers it is the stall bound in real seconds, read from the page's own blink duration (F1-STALL)");
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
        check(display.Contains("CompletionFinalBounds.Placed(parts, RevealFinished, contentSettled, out int finalDeepest, out _finalUndrawn)")
              && display.Contains("IconRowBottom(success, RevealFinished, ContentSettled)")
              && display.Contains("IconRowBottom(page, RevealFinished, ContentSettled)"),
            "the section's edge and the icon row the band hangs from read the same reveal state, so the two cannot disagree about which reading the board was placed from");
        check(display.Contains("var texts = element.GetComponentsInChildren<TMP_Text>(true);")
              && display.Contains("if (TryTextRect(text, out LayoutRect caption))")
              && display.Contains("private static bool TryTextRect(TMP_Text text, out LayoutRect rect)")
              && Body(display, "private static bool TryTextRect(").Contains("text.textBounds")
              && display.Contains("private static Camera PageCamera(Component component, out bool overlay)"),
            "the completion parts are measured through Renderer bounds and through every TextMeshPro text under them, projected through one camera ladder: a TextMeshProUGUI caption carries no bounds-carrying renderer, and its own laid-out extent is what the band must clear");
        // F2: a visible `Graphic` (Image/RawImage/TextMeshProUGUI) carries a CanvasRenderer, not a Renderer, and a child
        // that cannot be projected must leave the part *incomplete* rather than silently out of the reading.
        check(display.Contains("var graphics = element.GetComponentsInChildren<Graphic>(true);")
              && display.Contains("CompletionCandidatePolicy.Classify(")
              && display.Contains("else unmeasured++;")
              && Body(display, "private static CompletionPart Read(").Contains("unmeasured, drawn)"),
            "every visible Graphic is a candidate too, a candidate that cannot be projected is counted as unmeasured, and the part is built with that count so `ContentComplete` can refuse a reading taken without it");
        check(!Body(display, "private static float IconRowBottom(").Contains("m_sectorsWhiteBox"),
            "and the plate behind the row is still not one of the parts the edge is computed from");
    }

    // One frame of the recorded client page, as production's own measurement hands it over.
    private static ResultClientFailureEvidence Evidence(float pressableTop, int unmeasurable)
        => new ResultClientFailureEvidence(TitleBottom, Width, Height, Gap, MinimumStrip, WaitingText, pressableTop,
            true, true, unmeasurable);

    // 一帧连墙都没有：那行等待文字和可按控件都不可测。这是「墙还没出现」，不是「这一页没有墙」。
    private static ResultClientFailureEvidence NoWall()
        => new ResultClientFailureEvidence(TitleBottom, Width, Height, Gap, MinimumStrip, float.NaN, float.NaN);

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
