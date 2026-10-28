using System;
using System.IO;
using HawChow.GTFOCombatPanel;

// The defect 1.19.12 fixes, driven through the real policies it ships with: both settlement pages were measured on
// the first frame on which anything at all could be read, and the board that came out of that measurement kept its
// size for the life of the page.
//
// On the failure page that frame is the first one on which the title's alpha left zero - while the title is still
// fading in, and before the footer button has been drawn at all. The strip is then built from that moving title edge
// and from the historical fraction of the window instead of the button, the table is fitted into the shorter strip, and
// because `ResultBoardHold.TryHold` only ever lifts a board and never enlarges one, the small table stays small until
// something tears the whole layout down. Only F8 did, which is why the board looked "fixed" by a toggle.
//
// What is asserted here is the rule that replaced it, and it is deliberately unforgiving:
//
//   * the failure page's own title has to be *fully drawn* - its alpha (multiplied by every canvas group above it) at
//     `ResultTitleGate.OpaqueAlpha` - and its measured bottom edge has to have been read at the same value twice. A
//     rectangle that has stopped moving while the text is still fading in is not an anchor: that is the reported
//     shape, and geometry alone must never be enough.
//   * an edge the page has not handed over - a footer button that was never drawn, a completion section nobody could
//     read - is never turned into "ready" by the passage of frames. The budget only tells the caller to retry at a
//     bounded cadence; it never buys a placement.
//   * a page without a real stable anchor therefore keeps the game's own screen: nothing is drawn, nothing is hidden.
//     The moment a real anchor is measurable and still, the board is placed without an F8 toggle.
//
// Everything below runs against the real `ResultTitleGate`, `ResultEdgeGate`, `ResultRetryCadence`,
// `ResultAnchorDrift`, `FailureAlignment`, `ResultBoardHold` and `ResultAnchorRecovery` of this build, plus a managed
// replay of one page's frames. The one section that reads source text instead is at the end and says so.
internal static class Release11912Scenarios
{
    public static int Run()
    {
        int failures = 0;
        RunInto((condition, description) =>
        {
            if (condition) return;
            failures++;
            Console.WriteLine("FAIL: " + description);
        });
        return failures;
    }

    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));

        FailureTitleNeedsFullOpacityOverASettledEdge(check);
        FailurePageWaitsForThePagesOwnTwoEdges(check);
        NoAnchorKeepsTheVanillaPage(check);
        RetriesAreBoundedNotForgiving(check);
        ABlindFrameKeepsTheLastRealEdgeWithoutCompletingTheTitle(check);
        AFallenBackStripIsMeasuredAgain(check);
        APageThatMovedIsFittedAgainWithoutF8(check);
        SuccessPageWaitsForSettledEdges(check);
        RefusedFitStillKeepsTheVanillaPage(check);
        AGrownBoardCannotCoverTheLobbyButton(check);
        Wiring(check);
    }

    // ------------------------------------------------------- the failure page's own title, reading by reading
    //
    // The reported shape is a title whose rectangle is already in its final place while the text is still fading in.
    // Every one of those readings used to be a legal measurement of "the title is here"; none of them is now.
    private static void FailureTitleNeedsFullOpacityOverASettledEdge(Action<bool, string> check)
    {
        var gate = new ResultTitleGate();
        bool settledWhileFadingIn = false;
        for (int reading = 0; reading < 30; reading++)
            settledWhileFadingIn |= gate.Observe(true, reading * 0.02f, true, 612f); // 0% .. 58%, rock-steady edge
        check(!settledWhileFadingIn,
            "a title whose rectangle has stopped moving but whose text is still fading in is never an anchor");
        check(gate.Misses == 30 && Math.Abs(gate.Edge - 612f) <= 0.01f,
            $"and the edge it did read is kept for a later measurement rather than thrown away: {gate.Edge:0} after {gate.Misses} readings");
        check(gate.OpaqueFrames == 0, "none of those readings was at full opacity");

        // Held at exactly half opacity for a long time: the frame the report was taken on.
        gate.Forget();
        bool settledAtHalf = false;
        for (int reading = 0; reading < 60; reading++) settledAtHalf |= gate.Observe(true, 0.5f, true, 612f);
        check(!settledAtHalf, "half opacity over a stable rectangle is still not drawn text");
        check(gate.OpaqueFrames == 0, "and no reading of it counted as opaque");

        // The fade finishes. The edge has been stable all along, so the reading on which the text becomes fully drawn is
        // the reading that settles the title - one frame later than the text, never one frame earlier.
        check(!gate.Observe(true, 0.97f, true, 612f), "just below full opacity over a stable edge is still not the title");
        check(gate.Observe(true, 1f, true, 612f),
            "the reading the text reaches full opacity over a stable edge settles it");
        check(gate.OpaqueFrames == 1, $"and the opaque readings are counted: {gate.OpaqueFrames}");

        // A title seen for the first time needs both together: full opacity *and* a second reading at that edge.
        var fresh = new ResultTitleGate();
        check(!fresh.Observe(true, 1f, true, 612f), "the first reading of a title is one reading, full opacity or not");
        check(fresh.Observe(true, 1f, true, 612f), "and the second reading at the same edge settles it");

        // The threshold is full opacity, not "nearly", however long the edge has been stable.
        var boundary = new ResultTitleGate();
        bool belowFull = false;
        for (int reading = 0; reading < 20; reading++) belowFull |= boundary.Observe(true, 0.98f, true, 612f);
        check(!belowFull, "alpha just below full opacity is not full opacity, however many readings hold it");
        check(boundary.Observe(true, ResultTitleGate.OpaqueAlpha, true, 612f),
            "and that same stable edge settles the moment the alpha reaches full opacity");

        // An alpha nobody could read is not evidence that the text is on the screen.
        var unreadable = new ResultTitleGate();
        bool settledUnreadable = false;
        for (int reading = 0; reading < 10; reading++) settledUnreadable |= unreadable.Observe(true, float.NaN, true, 612f);
        check(!settledUnreadable, "an unreadable alpha is not completion");

        // Full opacity over an edge that is still travelling is not settled either.
        var moving = new ResultTitleGate();
        bool settledWhileMoving = false;
        for (int reading = 0; reading < 10; reading++) settledWhileMoving |= moving.Observe(true, 1f, true, 600f + 4f * reading);
        check(!settledWhileMoving, "full opacity over an edge that is still moving is not settled");

        // A title the page has switched off, and a title whose rectangle cannot be read, are both not anchors.
        var inactive = new ResultTitleGate();
        check(!inactive.Observe(false, 1f, true, 612f), "a title switched off by the page is not an anchor");
        var blind = new ResultTitleGate();
        check(!blind.Observe(true, 1f, false, float.NaN), "a title whose rectangle cannot be read is not an anchor");
    }

    // ------------------------------------------------------- one failure page, replayed reading by reading
    //
    // 1920x1440: the title's rectangle settles at 460 early on but its text keeps fading in, then it reaches full
    // opacity, and the footer's lobby button only appears at reading 41 at 1200 with a height of 96. The placement used
    // to happen on reading 1; it must now happen on the first reading on which *both* of the page's own edges are real.
    private static void FailurePageWaitsForThePagesOwnTwoEdges(Action<bool, string> check)
    {
        var page = new FailurePage();
        int firstPlaced = 0;
        bool placedWhileFadingIn = false, placedWithoutTheFooter = false, settledTitleWithoutBoard = false;
        for (int reading = 1; reading <= 60; reading++)
        {
            bool fading = reading <= 30;
            float alpha = fading ? 0.02f * reading : 1f;
            float titleBottom = reading <= 6 ? 400f + 10f * reading : 460f;
            bool buttonDrawn = reading >= 41;
            float buttonTop = buttonDrawn ? 1200f : float.NaN;
            bool placed = page.Step(true, alpha, true, titleBottom, buttonDrawn, buttonTop, buttonDrawn ? 96f : float.NaN);
            if (placed)
            {
                if (firstPlaced == 0) firstPlaced = reading;
                if (fading) placedWhileFadingIn = true;
                if (!buttonDrawn) placedWithoutTheFooter = true;
            }
            else if (page.TitleSettled && !page.Placed && reading >= 31 && reading < 41)
            {
                // The title is done and there is still no button: exactly the shape that used to produce a 0.66-of-the-
                // window strip and an unreadable table.
                settledTitleWithoutBoard = true;
            }
        }

        check(firstPlaced > 0, "the failure page still gets its board once both of its edges are real");
        check(!placedWhileFadingIn, "nothing is placed while the title's text is still fading in, however still its rectangle is");
        check(!placedWithoutTheFooter, "and nothing is placed before the footer button has been drawn");
        check(settledTitleWithoutBoard, "the title alone is not enough: the board waits for the button it has to clear");
        check(firstPlaced == 42,
            $"the board lands on the second reading of a still button, after a full-opacity title: reading {firstPlaced}");

        // The size it lands at is the size the settled page allows, at the readable floor of the button it has to clear.
        check(page.Room > 700f && page.Room < 715f, $"the strip is the real one between title and button: {page.Room:0}px");
        check(page.Glyph == 15, $"the board is fitted at the size that strip allows: {page.Glyph}px");
        check(page.Board.Y >= 460f + 16f - 0.01f,
            $"the board never covers the page's own title: board top {page.Board.Y:0} against a title ending at 460");
        check(page.Board.Bottom <= 1200f - ResultBoardHold.ButtonClearance,
            $"and it clears the lobby button by its own clearance line: bottom {page.Board.Bottom:0} against a button at 1200");

        // What the same page produced when it was measured on its first readable reading, for the record: the button
        // could not be read, so the strip stopped at the historical 0.66 of the window. This is the small board the
        // report is about - pinned as the answer nothing may fall back to, not as a behaviour anything relies on.
        float fractionStrip = 1440f * ResultsRegion.FallbackBottomFraction - (400f + 16f);
        int fractionGlyph = FailurePage.GlyphFor(fractionStrip, float.NaN);
        check(fractionGlyph < page.Glyph,
            $"a strip taken before the footer was measured fits a smaller table ({fractionGlyph}px) than the settled page does ({page.Glyph}px)");
        check(fractionGlyph == 11, $"and that is the ~11px table of the report, not an approximation: {fractionGlyph}px");
    }

    // ------------------------------------------------------- no real anchor means the game's own screen
    //
    // The interaction that matters: while the page's own edges are unanswered, nothing is drawn *and nothing is
    // hidden*; the moment they answer, a board is placed without any toggle. The same composition the runtime uses.
    private static void NoAnchorKeepsTheVanillaPage(Action<bool, string> check)
    {
        // Failure page: a settled title, and a footer that is never handed over.
        var title = new ResultTitleGate();
        bool titleSettled = false;
        for (int reading = 0; reading < 3; reading++) titleSettled |= title.Observe(true, 1f, true, 612f);
        check(titleSettled, "the failure page's title is settled");

        var footer = new ResultEdgeGate();
        bool everPlaced = false, everHid = false;
        for (int reading = 0; reading < 400; reading++)
        {
            bool settled = titleSettled && footer.Observe(false, float.NaN);
            everPlaced |= settled;
            everHid |= ResultBoardLatch.HidesVanilla(settled);
        }
        check(!everPlaced, "400 readings without a footer button never place a board");
        check(!everHid, "and the game's own failure screen is never hidden for a board nobody can draw");
        check(footer.Exhausted, "the gate reports its budget spent, so the caller throttles instead of placing");

        // ...and the moment the same footer is really there, still, the placement goes through with no toggle at all.
        footer.Forget();
        check(!footer.Observe(true, 1200f) && footer.Observe(true, 1200f),
            "a footer that finally arrives settles on its second reading, and the board follows");

        // Success page: the same rule for its own two edges.
        var completion = new ResultEdgeGate();
        bool upperSettled = false;
        for (int reading = 0; reading < ResultEdgeGate.SettleAttempts + 40; reading++) upperSettled |= completion.Observe(false, float.NaN);
        check(!upperSettled && completion.Exhausted, "a completion section nobody can read is never settled, budget or not");
        check(!completion.Observe(true, 410f) && completion.Observe(true, 410f),
            "the same section settles once it is really read twice at one value");
    }

    // ------------------------------------------------------- waiting is bounded, forgiving is not
    private static void RetriesAreBoundedNotForgiving(Action<bool, string> check)
    {
        var cadence = new ResultRetryCadence();
        int everyFrame = 0;
        for (int frame = 1; frame <= ResultRetryCadence.EveryFrameFrames; frame++)
            if (cadence.Due(frame)) everyFrame++;
        check(everyFrame == ResultRetryCadence.EveryFrameFrames,
            $"while the page's reveal runs it is measured on every frame: {everyFrame}");
        check(cadence.Throttled, "then the every-frame window is spent");

        int throttled = 0;
        for (int frame = ResultRetryCadence.EveryFrameFrames + 1; frame <= ResultRetryCadence.EveryFrameFrames + 300; frame++)
            if (cadence.Due(frame)) throttled++;
        check(throttled == 300 / ResultRetryCadence.RetryEveryFrames,
            $"after that it is re-measured once every {ResultRetryCadence.RetryEveryFrames} frames: {throttled} of 300");

        // The cadence only ever answers "look again"; it never answers "ready", so it cannot rescue a page whose
        // anchors are missing. That is the gates' job and they say no.
        var gate = new ResultEdgeGate();
        bool ready = false;
        for (int frame = 1; frame <= 2000; frame++)
            if (cadence.Due(frame)) ready |= gate.Observe(false, float.NaN);
        check(!ready, "two thousand frames of bounded retries still never turn a missing edge into a placement");

        // A fresh attempt - a new page, F8 on, a configuration change - is measured on the next frame again.
        cadence.Forget();
        check(!cadence.Throttled && cadence.Due(2001), "forgetting the cadence measures on the next frame again");
        cadence.Forget();
        for (int frame = 1; frame <= ResultRetryCadence.EveryFrameFrames + 100; frame++) cadence.Due(frame);
        check(cadence.Due(5), "a frame counter that restarted is treated as due, not as a reason to skip for ever");
    }

    // ------------------------------------------------------- the last real edge outlives a blind frame
    //
    // A title that has been measured, and then cannot be read on a reading or two (a TMP rebuild, a camera that is not
    // assigned yet), must leave the edge it did measure standing - for the drift test, the diagnostic line and any later
    // real measurement - while never counting as a completed title on its own.
    private static void ABlindFrameKeepsTheLastRealEdgeWithoutCompletingTheTitle(Action<bool, string> check)
    {
        var gate = new ResultTitleGate();
        bool settled = false;
        for (int reading = 0; reading < 3; reading++) settled |= gate.Observe(true, 1f, true, 612f);
        check(settled && Math.Abs(gate.Edge - 612f) <= 0.01f && gate.EdgeAge == 0,
            $"a settled title hands over its own bottom edge: {gate.Edge:0} at age {gate.EdgeAge}");

        check(!gate.Observe(true, 1f, false, float.NaN),
            "a reading in which the title's renderer cannot be read is not an anchor");
        check(Math.Abs(gate.Edge - 612f) <= 0.01f && gate.EdgeAge == 1,
            $"and it leaves the edge the title really measured standing: {gate.Edge:0} at age {gate.EdgeAge}");
        check(!gate.Observe(true, 1f, false, float.NaN) && Math.Abs(gate.Edge - 612f) <= 0.01f && gate.EdgeAge == 2,
            "however many such readings follow, until the page is forgotten");

        // Readable again: the fresh reading replaces the older one, and the stability count starts over.
        check(!gate.Observe(true, 1f, true, 624f), "a title read again after a blind frame is one reading, not an anchor");
        check(Math.Abs(gate.Edge - 624f) <= 0.01f && gate.EdgeAge == 0,
            $"the anchor follows the title again once it can be read: {gate.Edge:0} at age {gate.EdgeAge}");
        check(gate.Observe(true, 1f, true, 624f), "and settles on the next reading at that same value");

        // Why the fraction of the window is not a substitute for that edge, through the real arithmetic: with no
        // completion line remembered in this window it sits *above* the title and would put the board on top of it.
        FailureAlignment.Forget();
        bool fractionWay = FailureAlignment.Resolve(false, float.NaN, 1440f, 0f, 16f, true, 1200f,
            out float fractionLine, out ResultsSafeArea fractionStrip);
        check(fractionWay && fractionLine < 612f,
            $"the historical fraction sits above this page's title ({fractionLine:0} against a title ending at 612)");
        bool measuredWay = FailureAlignment.Resolve(true, gate.Edge, 1440f, 0f, 16f, true, 1200f,
            out float titleLine, out ResultsSafeArea measuredStrip);
        check(measuredWay && Math.Abs(titleLine - 624f) <= 0.01f,
            $"the edge the title really gave is the line the strip starts under: {titleLine:0}");
        check(measuredStrip.Top >= titleLine + 16f - 0.01f && measuredStrip.Top > fractionStrip.Top,
            $"so the board starts below the title and below where the fraction would have started it: {measuredStrip.Top:0} vs {fractionStrip.Top:0}");
        FailureAlignment.Forget();
    }

    // ------------------------------------------------------- the one second look at a strip that was a guess
    private static void AFallenBackStripIsMeasuredAgain(Action<bool, string> check)
    {
        var measured = new BoardStrip(496f, 1184f, true, 1200f, 96f, true);
        check(!ResultAnchorRecovery.LatchFallback(0, measured.ButtonMeasured && measured.AnchorMeasured && float.IsFinite(measured.ButtonHeight)),
            "a strip measured from the anchor, the named button and that button's own height is never re-measured away from");

        // The reachable case: the bound came from the drawn band of the page's own buttons, so its top edge is real but
        // the height the readability floor is taken from is not. It keeps one second look for the moment that named
        // control's own rectangle becomes readable.
        var fromBand = new BoardStrip(496f, 1184f, true, 1200f, float.NaN, true);
        check(ResultAnchorRecovery.LatchFallback(0, fromBand.ButtonMeasured && fromBand.AnchorMeasured && float.IsFinite(fromBand.ButtonHeight)),
            "a strip whose button height could only come from the drawn band keeps its one second look");

        var guessedButton = new BoardStrip(496f, 950f, false, float.NaN, float.NaN, true);
        check(ResultAnchorRecovery.LatchFallback(0, guessedButton.ButtonMeasured && guessedButton.AnchorMeasured && float.IsFinite(guessedButton.ButtonHeight)),
            "a board laid out without the button keeps its one second look, exactly as it did before this release");

        var guessedAnchor = new BoardStrip(396f, 1184f, true, 1200f, 96f, false);
        check(ResultAnchorRecovery.LatchFallback(0, guessedAnchor.ButtonMeasured && guessedAnchor.AnchorMeasured && float.IsFinite(guessedAnchor.ButtonHeight)),
            "and so does a board whose upper edge came from a title that had not settled");

        check(ResultAnchorRecovery.ShouldRetry(0, true, true) && !ResultAnchorRecovery.ShouldRetry(1, true, true),
            "that second look is still a per-page budget of one");
    }

    // ------------------------------------------------------- the page moved after the board was placed
    //
    // This is the half that makes an F8 toggle unnecessary: the strip the board was fitted into is compared against
    // what the page measures now, behind a threshold and two samples of hysteresis, and a real move spends one
    // bounded re-measure.
    private static void APageThatMovedIsFittedAgainWithoutF8(Action<bool, string> check)
    {
        var drift = new ResultAnchorDrift();
        check(!drift.Armed && !drift.Due(500f, 700f),
            "a board nobody recorded a strip for is never re-measured");

        drift.Record(496f, 688f);
        check(drift.Armed, "the strip a placed board was measured into is what it is compared against");
        check(!drift.Due(497.5f, 689f),
            "a pixel of the page's own animation is not a move, so a settled board is left alone");
        check(!drift.Due(560f, 688f),
            "the first sample of a real move is not enough either: one frame cannot rebuild the layout");
        check(drift.Due(560f, 688f),
            "two samples in a row are a page that has really moved");

        // The footer of another mod (or a smaller window) leaves the strip at 828px instead of 688. The board has to be
        // fitted again against that, and the table it produces is the larger one - the small grid is never kept.
        var lower = new ResultAnchorDrift();
        lower.Record(496f, 688f);
        check(!lower.Due(496f, 828f) && lower.Due(496f, 828f),
            "a footer that lands 140px lower is a real move after two samples");
        lower.Spent();
        int before = FailurePage.GlyphFor(688f, 96f);
        int after = FailurePage.GlyphFor(828f, 96f);
        check(after > before,
            $"the re-measure fits the table against the strip the page offers now ({after}px) instead of keeping the smaller grid ({before}px)");

        // Budgeted: a page that keeps moving is left alone after two re-measures rather than rebuilt for ever.
        var budget = new ResultAnchorDrift();
        budget.Record(496f, 688f);
        int granted = 0;
        for (int sample = 0; sample < 60 && granted < 4; sample++)
        {
            if (!budget.Due(496f, 900f)) continue;
            granted++;
            budget.Spent();
        }
        check(granted == ResultAnchorDrift.MaximumRecoveries,
            $"one page may be re-measured {ResultAnchorDrift.MaximumRecoveries} times and no more: {granted}");
    }

    // ------------------------------------------------------- the success page's two edges
    private static void SuccessPageWaitsForSettledEdges(Action<bool, string> check)
    {
        // The completion section's own bottom edge: the native BlinkIn has finished, but the pages lay their own
        // layout out for a reading or two after that, and this board is measured once and then kept.
        var upper = new ResultEdgeGate();
        check(!upper.Observe(true, 410f),
            "the first reading of the completion section is a reading, not a settled upper bound");
        check(upper.Observe(true, 410f), "the same edge on the next reading settles it");
        upper.Forget();
        var jitter = new ResultEdgeGate();
        bool settledWhileMoving = false;
        for (int reading = 0; reading < 400; reading++) settledWhileMoving |= jitter.Observe(true, 410f + (reading % 2) * 6f);
        check(!settledWhileMoving, "a completion section whose own edge never holds still is never an anchor");

        // The footer: a button the page never draws is waited for, and the budget is never a placement.
        var footer = new ResultEdgeGate();
        bool readyWhileMissing = false;
        for (int reading = 0; reading < ResultEdgeGate.SettleAttempts + 200; reading++) readyWhileMissing |= footer.Observe(false, float.NaN);
        check(!readyWhileMissing, "a button the page never hands over is never ready, however many readings pass");
        check(footer.Exhausted, "the budget is reported, so the caller can retry at a bounded cadence instead of every frame");

        // A hover that keeps nudging the edge: read every frame, never the same value twice.
        footer.Forget();
        bool readyWhileNudging = false;
        for (int reading = 0; reading < 400; reading++) readyWhileNudging |= footer.Observe(true, 1200f + (reading % 2) * 9f);
        check(!readyWhileNudging, "an edge that is read but never holds still is not settled either");

        // ...and a still one settles on its second reading, without any further help.
        footer.Forget();
        check(!footer.Observe(true, 1200f) && footer.Observe(true, 1200f),
            "a real, still edge settles on its second reading");
        footer.Forget();
        check(!footer.Observe(false, float.NaN), "and an edge read as missing is never carried over from before");
    }

    // ------------------------------------------------------- a refused fit still leaves the vanilla page
    private static void RefusedFitStillKeepsTheVanillaPage(Action<bool, string> check)
    {
        var strip = new BoardStrip(496f, 1184f, true, 1200f, 96f, true);
        bool placed = ResultBoardHold.TryPlace(1920f, 1440f, strip, 840f, 630f, 0f, out LayoutRect board, 2f, bottomAligned: false);
        check(placed && ResultBoardLatch.Placed(placed, true, true) && ResultBoardLatch.HidesVanilla(true),
            "a board that really fits is a placement, and a placement is what the game's own blocks make way for");
        check(board.Y >= strip.Top - 0.01f, $"hanging from the anchor rather than the button: y={board.Y:0}");
        check(board.Bottom <= 1200f - ResultBoardHold.ButtonClearance,
            $"and clear of the button it never covers: bottom {board.Bottom:0}");

        var shortStrip = new BoardStrip(496f, 560f, true, 560f, 96f, true);
        bool refused = ResultBoardHold.TryPlace(1920f, 1440f, shortStrip, 840f, 630f, 0f, out _, 0f, bottomAligned: false);
        check(!refused && !ResultBoardLatch.Placed(refused, false, false) && !ResultBoardLatch.HidesVanilla(false),
            "a strip that cannot hold the board is refused, and a refused frame hides nothing - the 1.19.11 latch is untouched");
    }

    // ------------------------------------------------------- a table that grew after the board was placed
    private static void AGrownBoardCannotCoverTheLobbyButton(Action<bool, string> check)
    {
        // The strip the board was really measured into, and the place it keeps on a 1440-high page.
        var measured = new BoardStrip(496f, 1184f, true, 1200f, 96f, true);
        var held = new LayoutRect(0f, 496f, 840f, 400f);

        check(ResultBoardHold.HeldGridFits(measured, measured, held, 400f, 1440f, 2f),
            "the grid that was really measured fits the place it was measured into");
        check(ResultBoardHold.HeldGridFits(measured, measured, held, 688f, 1440f, 2f),
            "a snapshot that grows the table up to the strip's own lower edge is still accepted");
        check(!ResultBoardHold.HeldGridFits(measured, measured, held, 700f, 1440f, 2f),
            "a snapshot that would reach the lobby button's clearance line keeps the grid already on the page instead");
        check(!ResultBoardHold.HeldGridFits(measured, measured, held, 0f, 1440f, 2f)
              && !ResultBoardHold.HeldGridFits(measured, measured, new LayoutRect(0f, float.NaN, 840f, 400f), 400f, 1440f, 2f),
            "a grid with no height, and a place with no origin, are not drawn at all");

        // The button moved up under the board (another mod re-laid the footer out): this reading's button is the wall.
        var moved = new BoardStrip(496f, 1100f, true, 1120f, 96f, true);
        check(ResultBoardHold.HeldGridFits(measured, moved, held, 620f, 1440f, 2f),
            "a growth that still stops short of the button that moved up is kept");
        check(!ResultBoardHold.HeldGridFits(measured, moved, held, 640f, 1440f, 2f),
            "a growth that would pass that button is refused, even though the strip the board was measured into is taller");

        // A page whose button cannot be read this reading: the strip the board was measured into is still the contract.
        var blind = new BoardStrip(496f, 1184f, false, float.NaN, float.NaN, true);
        check(ResultBoardHold.HeldGridFits(measured, blind, held, 688f, 1440f, 2f)
              && !ResultBoardHold.HeldGridFits(measured, blind, held, 700f, 1440f, 2f),
            "with no button to read, the board still may not run past the strip it was measured into");

        check(ResultBoardHold.TryHold(measured, held, 1440f, out LayoutRect kept) && Math.Abs(kept.Y - held.Y) <= 0.01f,
            "the refusal leaves the board exactly where it was, so nothing jumps when a snapshot arrives");
    }

    // ------------------------------------------------------- the wiring, read from the production source
    //
    // A static source check only: it says the runtime asks the policies above, in the order that matters. It is not a
    // runtime behaviour test and does not pretend to be one.
    private static void Wiring(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the 1.19.12 regression locates the production sources");
        if (root == null) return;
        string results = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        string policy = File.ReadAllText(Path.Combine(root, "ResultsPagePolicy.cs"));
        string layout = File.ReadAllText(Path.Combine(root, "LayoutPolicy.cs"));
        string visibility = File.ReadAllText(Path.Combine(root, "ResultVisibility.cs"));

        check(policy.Contains("public sealed class ResultTitleGate")
              && policy.Contains("public sealed class ResultEdgeGate")
              && policy.Contains("public sealed class ResultRetryCadence")
              && policy.Contains("public sealed class ResultAnchorDrift"),
            "the four settle policies exist as Unity-free code this file drives");

        // Full opacity is the only way in: no "alpha stopped moving" shortcut, and no timeout that turns into ready.
        check(policy.Contains("bool opaque = float.IsFinite(visible) && visible >= OpaqueAlpha;")
              && !policy.Contains("AlphaFrames")
              && !policy.Contains("|| _attempts >= SettleAttempts"),
            "the title gate requires full opacity, and the edge gate never answers ready on a timeout");

        check(results.Contains("private static readonly ResultEdgeGate Footer = new ResultEdgeGate();")
              && results.Contains("private static readonly ResultTitleGate Title = new ResultTitleGate();")
              && results.Contains("private static readonly ResultEdgeGate Completion = new ResultEdgeGate();")
              && results.Contains("private static readonly ResultRetryCadence Retry = new ResultRetryCadence();"),
            "the runtime holds one gate per edge and one retry cadence");

        int stripDefinition = results.IndexOf("private static bool FailureStrip(", StringComparison.Ordinal);
        int titleGate = results.IndexOf("anchorSettled = Title.Observe(active, alpha, measured", StringComparison.Ordinal);
        int titleRefusal = results.IndexOf("if (!anchorSettled)", StringComparison.Ordinal);
        int titleEdge = results.IndexOf("float titleBottom = float.IsFinite(Title.Edge) ? Title.Edge : rect.Bottom;", StringComparison.Ordinal);
        check(stripDefinition >= 0 && titleGate > stripDefinition && titleRefusal > titleGate && titleEdge > titleRefusal,
            "`FailureStrip` places nothing until the title gate is settled, and then starts the strip at that same edge");

        check(results.Contains("private static bool ButtonSettled(bool measured, float top) => Footer.Observe(measured, top);"),
            "the footer is asked through one call on both pages, so a missing button can never be papered over");

        int refresh = results.IndexOf("internal static void Refresh(eGameStateName state)", StringComparison.Ordinal);
        int cadence = results.IndexOf("if (!Retry.Due(Time.frameCount)) return;", StringComparison.Ordinal);
        int prepare = results.IndexOf("Prepare(page, succeeded);", StringComparison.Ordinal);
        check(refresh >= 0 && cadence > refresh && prepare > cadence,
            "a page whose anchors have not arrived is measured at the bounded cadence, before any placement work");

        int recovery = results.IndexOf("private static bool FallbackRecoveryDue(", StringComparison.Ordinal);
        int recoveryAsk = results.IndexOf("ResultAnchorRecovery.ShouldRetry(_fallbackRecoveries", StringComparison.Ordinal);
        int driftAsk = results.IndexOf("AnchorDriftDue(page, succeeded)", StringComparison.Ordinal);
        int rearm = results.IndexOf("Retry.Forget();", StringComparison.Ordinal);
        check(recovery >= 0 && recoveryAsk > recovery && driftAsk > recoveryAsk && rearm > 0,
            "the refresh path asks both halves - the second look and the page that moved - and re-arms the cadence");

        check(!results.Contains("WaitForTitle") && !layout.Contains("WaitForTitle"),
            "no bounded give-up on the title survives anywhere: the wait cannot become a placement");

        check(results.Contains("_placedOnFallback = ResultAnchorRecovery.LatchFallback(_fallbackRecoveries,")
              && results.Contains("strip.ButtonMeasured && strip.AnchorMeasured && float.IsFinite(strip.ButtonHeight));"),
            "a strip whose button height came from the drawn band is latched as one that may be measured once more");

        int fit = results.IndexOf("private static bool Fit(BoardStrip strip", StringComparison.Ordinal);
        int record = results.IndexOf("Drift.Record(strip.Top, strip.Bottom - strip.Top);", StringComparison.Ordinal);
        int hold = results.IndexOf("_holdStrip = strip;", StringComparison.Ordinal);
        check(fit >= 0 && record > fit && hold > fit,
            "the strip a placed board was measured into is recorded in the one place that really places a board");

        check(visibility.Contains("public readonly bool AnchorMeasured;")
              && visibility.Contains("bool anchorMeasured = true)"),
            "the strip carries whether its upper edge was measured in its settled state, defaulting to the old answer");

        check(results.Contains("internal static void BeginFailurePage(CM_PageExpeditionFail page)")
              && results.Contains("private static void Prefix(CM_PageExpeditionFail __instance) => ResultsDisplay.BeginFailurePage(__instance);"),
            "a new visit to the failure page drops the settle state the previous visit spent - the page object is reused");

        // What this release must not have changed: the success board still hangs from its upper edge, the fit-failure
        // latch still records a placement only after a fit that really placed one, and the growth guard is still asked.
        check(results.Contains("bottomAligned: false") && !results.Contains("bottomAligned: true"),
            "the success board still hangs from the completion section's upper edge");
        int latch = results.IndexOf("ResultBoardLatch.Placed(Fit(strip, grid, content, board, report, succeeded)", StringComparison.Ordinal);
        int record_ = results.IndexOf("_placed = true; _placedSucceeded = succeeded;", StringComparison.Ordinal);
        check(latch >= 0 && record_ > latch,
            "a placement is still recorded only after the latch has accepted the fit");
        int heldCheck = results.IndexOf("ResultBoardHold.HeldGridFits(_holdStrip, strip, _holdBoard, grid.Height, Screen.height, BoardSlack)", StringComparison.Ordinal);
        int heldKeep = results.IndexOf("_origin = _holdBoard;", StringComparison.Ordinal);
        check(heldCheck > fit && heldKeep > heldCheck && visibility.Contains("public static bool HeldGridFits("),
            "the hold branch refuses a grid that would reach the button or run past the strip it was measured into");

        Console.WriteLine("WIRING: RESULT_PAGE_SETTLE (static source check; not a runtime behaviour test)");
    }

    // ------------------------------------------------------- one failure page, driven through the real policies
    private sealed class FailurePage
    {
        public const int Height = 1440;
        private const float Gap = 16f;

        private readonly ResultTitleGate _title = new ResultTitleGate();
        private readonly ResultEdgeGate _footer = new ResultEdgeGate();

        public int Frames { get; private set; }
        public bool Placed { get; private set; }
        public bool TitleSettled { get; private set; }
        public LayoutRect Board { get; private set; }
        public float Room { get; private set; }
        public int Glyph { get; private set; }

        // One reading of the page as the game draws it: the title's opacity and measured bottom edge, and the footer
        // button's own top edge and height (NaN for a control that cannot be measured on this reading).
        public bool Step(bool titleActive, float titleAlpha, bool titleMeasured, float titleBottom,
                         bool buttonDrawn, float buttonTop, float buttonHeight)
        {
            Frames++;
            Placed = false;
            TitleSettled = _title.Observe(titleActive, titleAlpha, titleMeasured, titleMeasured ? titleBottom : float.NaN);
            if (!TitleSettled) return false;
            bool buttonMeasured = buttonDrawn && float.IsFinite(buttonTop);
            if (!_footer.Observe(buttonMeasured, buttonTop)) return false;
            if (!FailureAlignment.Resolve(true, _title.Edge, Height, 0f, Gap, buttonMeasured, buttonTop,
                out _, out ResultsSafeArea strip)) return false;
            float top = strip.Top, bottom = Math.Min(strip.Bottom, Height - 8f);
            Room = bottom - top;
            if (!(Room > 0f)) return false;
            Glyph = GlyphFor(Room, buttonHeight);
            ResultTableSize size = Table(Glyph);
            bool placed = ResultBoardHold.TryPlace(1920f, Height,
                new BoardStrip(top, bottom, buttonMeasured, buttonTop, buttonHeight, TitleSettled),
                size.Width, size.Height, 0f, out LayoutRect board, 2f, bottomAligned: false);
            if (!placed) return false;
            Board = board;
            Placed = true;
            return true;
        }

        // A 30-row settlement table at the same 1.5 em rows and 60 em width the real board measures at.
        public static ResultTableSize Table(int glyph) => new ResultTableSize(60f * glyph, 45f * glyph);

        // The glyph size this strip produces, through the real search and the real readability floor.
        public static int GlyphFor(float room, float buttonHeight)
            => ResultsFontGrowth.FitBoardIntoStrip(17, ResultsFontGrowth.ReadabilityFloor(buttonHeight), int.MaxValue,
                room, 1872f, Table, out _);
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj"))) directory = directory.Parent;
        return directory != null ? directory.FullName : null;
    }
}
