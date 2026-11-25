using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// 1.19.21: the settlement board is drawn at its final layout geometry from the very first frame the results page
// exists. The recorded 1.19.20 success page (2560x1440, client) drew its completion section over several frames -
// the section's *drawn* edge walked 250 -> 272 -> 314 -> 366, the band followed it, and the board was re-fitted and
// finally withdrawn after sixty strict refusals - while the section's layout had ended at 366 all along. The failure
// page of the same run placed a board with `boardBottom=1388`, 263 px past its band, and only faded it to 0.55.
//
// The invariants driven here, all through the real policies of this build (no Unity):
//
//   I1  a page whose native elements are laid out places the board on the first evaluation - no title-opacity gate,
//       no settle frames, no two-sample latch, no native Blink completion, no waiting-text alpha/active/text test.
//   I2  the band computed on a "present but nothing drawn yet" page is numerically identical to the band computed on
//       the same page fully drawn, and no policy in the chain changes its answer afterwards.
//   I3  the board is always inside its band (`board.Y >= band.Top - slack`, `board.Bottom <= band.Bottom + slack`);
//       the *page's own* band that is too short for the board at the absolute minimum font is refused
//       (`squeezed`/`clamped` reported) and the vanilla page stays - never a board drawn past that band and merely
//       faded. (1.19.22: the invented fail-open band obeys the same invariant from the other side - it is sized from
//       the board, extends past the measured wall and says so; that half is driven by Release11922Scenarios.)
//   I4  the same numbers hold at 1920x1080 and 2560x1440: the clearance is the design value scaled by
//       `screenHeight/1080` (`ResultsBounds.Clearance`), and both roles - the host with the measurable
//       "leave expedition" button, the client with only the waiting-status block's laid-out rectangle - band the
//       board by their own layout edges.
//   I5  the settle refusals that printed `have=`/`need=` as if they were the reason, and the placed frames that
//       printed `boardBand=unresolved`, are gone (static source checks at the end).
//
// The historical numbers are used directly: icon-row final edge 366 at 1440 (the layout end of its four icon
// slots), waiting text top 1373 (client), button top 683 (host). At 1080p the same layout scales by 1080/1440.
internal static class Release11921Scenarios
{
    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        TheBandReadsTheFinalLayoutOnAnUndrawnPage(check);
        FullyDrawnPagesReadTheVerySameBand(check);
        TheHistoricalWalkReadsTheLayoutEndFromFrameOne(check);
        ALaidOutButNotYetSwitchedOnSectionReadsTheFinalEdge(check);
        AnUnscheduledSlotContributesNothing(check);
        TheLedgerIsWhatMarksASlotAsScheduled(check);
        TheFailureTitleLayoutIsTheUpperEdgeAtAlphaZero(check);
        TheClientWaitingLayoutRectIsTheLowerEdge(check);
        TheHostButtonLayoutRectIsTheLowerEdge(check);
        ABandTooShortForTheBoardRefusesInsteadOfFading(check);
        APageWithoutAnyGeometryKeepsTheVanillaPageAndBoundedRetries(check);
        Wiring(check);
    }

    private const float RowEm = 1.5f;          // the row height HudTable really builds a settlement row at
    private const int Rows = 30;               // ImpactBoard.RowCount(true)

    // The completion section of the recorded success page at the reveal's own in-between state: the header and the
    // four cleared-sector icon slots, all present and laid out, none switched on yet (`activeInHierarchy` false) - and
    // all scheduled by this page's completion ledger, which is what distinguishes them from an unused slot. `OwnBottom`
    // is each part's laid-out slot; `VisualBottom` is NaN while nothing has drawn.
    private static List<CompletionPart> LaidOutParts()
    {
        return new List<CompletionPart>
        {
            new CompletionPart(true, false, 0f, 240f, float.NaN, true),   // sectorHeader, laid out, reveal not switched it on
            new CompletionPart(true, false, 0f, 272f, float.NaN, true),   // sectorIconMain
            new CompletionPart(true, false, 0f, 314f, float.NaN, true),   // sectorIconSecond
            new CompletionPart(true, false, 0f, 366f, float.NaN, true),   // sectorIconThird
            new CompletionPart(true, false, 0f, 366f, float.NaN, true)    // sectorIconAllCompleted - the row's final depth
        };
    }

    // The same parts with the reveal finished: every slot draws exactly where it was laid out. (The white plate
    // `m_sectorsWhiteBox` is deliberately not among these parts: the band excludes it, the plate draws far below the
    // row.)
    private static List<CompletionPart> FullyDrawnParts()
    {
        return new List<CompletionPart>
        {
            new CompletionPart(true, true, 1f, 240f, 240f),
            new CompletionPart(true, true, 1f, 272f, 272f),
            new CompletionPart(true, true, 1f, 314f, 314f),
            new CompletionPart(true, true, 1f, 366f, 366f),
            new CompletionPart(true, true, 1f, 366f, 366f)
        };
    }

    // The record's own drawn-edge walk: the reveal switches the slots on one after another, and the drawn edge
    // (alpha above the visible threshold, measured pixels) reads 250, 272, 314, 366. The final layout reads 366 on
    // every one of those frames.
    private static void TheHistoricalWalkReadsTheLayoutEndFromFrameOne(Action<bool, string> check)
    {
        float[] drawnEdge = { 250f, 250f, 250f, 272f, 292f, 314f, 314f, 366f, 366f, 366f, 366f, 366f };
        var parts = LaidOutParts();
        foreach (float drawn in drawnEdge)
        {
            float band = CompletionFinalBounds.Bound(parts, drawn, out _, out _);
            check(Near(band, 366f, 0.6f),
                $"the sequence 250/272/314/366 of the historical log all read the layout end 366 from frame one: drawn={drawn:0} band={band:0}");
        }
        check(Near(CompletionFinalBounds.Bound(null, 250f, out _, out _), 250f, 0.6f),
            "and a section that handed over no layout at all keeps whatever drawn reading exists - the lower safety net is untouched");
        // The policy takes the deepest layout slot among the parts it is handed; the white box is excluded by the
        // caller's own part list, which the wiring section below reads from the source.
        var withPlate = new List<CompletionPart>(parts) { new CompletionPart(true, true, 0f, 800f, float.NaN) };
        check(Near(CompletionFinalBounds.Bottom(withPlate, out _, out _), 800f, 0.6f),
            "the edge is the deepest laid-out slot among the given parts, so a caller that never hands the white plate in as a part is never pushed down by it");
    }

    // I1's hard half from the code review: the reveal switches the slots on one after another, so on the first
    // frames the recorded page's parts are present and laid out but *switched off* - the fixtures above model that
    // state. They must still contribute their laid-out slots (the band reads 366 from the first frame), while a
    // slot this page never schedules must contribute nothing. This is exactly the 250 -> 272 -> 314 -> 366 walk
    // not re-entering the band through a slot that is off at the moment of the reading.
    private static void ALaidOutButNotYetSwitchedOnSectionReadsTheFinalEdge(Action<bool, string> check)
    {
        float undrawn = CompletionFinalBounds.Bottom(LaidOutParts(), out _, out _);
        float drawn = CompletionFinalBounds.Bottom(FullyDrawnParts(), out _, out _);
        check(Near(undrawn, 366f, 0.6f) && Near(drawn, 366f, 0.6f) && Near(undrawn, drawn, 0.01f),
            $"a page whose slots are all present and laid out but not switched on yet already reads the fully-drawn edge: undrawn={undrawn:0}, drawn={drawn:0} - the band hangs from the slots' own layout, never from how much has been switched on");
        float bandForOffButScheduled = CompletionFinalBounds.Bound(LaidOutParts(), 250f, out _, out _);
        check(Near(bandForOffButScheduled, 366f, 0.6f),
            $"and that held even while the deepest slot was not switched on and only 250 px had drawn: band={bandForOffButScheduled:0}px - the edge can no longer jump ~126 px down when the last slot comes on");
    }

    // A short run: the fourth icon slot is present on the page and laid out, but the page never schedules its
    // reveal (its BlinkIn never ran). It must contribute nothing - the edge must read exactly what the same run
    // with that icon absent reads, otherwise the unused slot drags the band down for the whole page.
    private static void AnUnscheduledSlotContributesNothing(Action<bool, string> check)
    {
        var withUnused = new List<CompletionPart>
        {
            new CompletionPart(true, true, 1f, 240f, 240f),
            new CompletionPart(true, true, 1f, 272f, 272f),
            new CompletionPart(true, true, 1f, 314f, 314f),
            new CompletionPart(true, false, 1f, 366f, 366f)      // the never-scheduled fourth icon, switched off
        };
        var without = new List<CompletionPart>
        {
            new CompletionPart(true, true, 1f, 240f, 240f),
            new CompletionPart(true, true, 1f, 272f, 272f),
            new CompletionPart(true, true, 1f, 314f, 314f)
        };
        float withEdge = CompletionFinalBounds.Bottom(withUnused, out _, out _);
        float withoutEdge = CompletionFinalBounds.Bottom(without, out _, out _);
        check(float.IsFinite(withEdge) && Near(withEdge, withoutEdge, 0.01f) && Near(withEdge, 314f, 0.6f),
            $"a short run whose fourth icon is present but never scheduled reads the same edge as the same run with that icon absent: {withEdge:0} vs {withoutEdge:0} - a slot the page never schedules can neither deepen the band nor withhold the board");
    }

    // The flag production stamps each part with comes from `BlinkCompletionLedger`, headlessly: a pointer the page
    // never scheduled answers not-scheduled, one its BlinkIn really scheduled answers scheduled, and a ledger that
    // overflowed its target budget cannot prove an absence and answers the conservative direction.
    private static void TheLedgerIsWhatMarksASlotAsScheduled(Action<bool, string> check)
    {
        var ledger = new BlinkCompletionLedger();
        check(!ledger.IsScheduled(0x1000), "a pointer this page never scheduled answers not-scheduled");
        ledger.Require(0x1000, false);
        check(ledger.IsScheduled(0x1000), "a pointer this page's BlinkIn really scheduled answers scheduled");
        var full = new BlinkCompletionLedger();
        for (int index = 0; index < 70; index++) full.Require(0x2000 + index, false);
        check(full.IsScheduled(0x7AAAAAAA),
            "a ledger that stopped recording past its target budget cannot prove a part was never scheduled, so it answers scheduled rather than let the band hang above a slot the page is about to draw under");
    }

    // I1/I2/I4 for the success page, driven once per resolution. The strip the board hangs from is the band itself:
    // top = the icon row's final layout edge + the scaled clearance, bottom = the highest lower layout wall minus the
    // same clearance - and the fit places the board inside it on the first evaluation, with no gate anywhere in the
    // chain.
    private static void TheBandReadsTheFinalLayoutOnAnUndrawnPage(Action<bool, string> check)
    {
        foreach (float screenHeight in new[] { 1440f, 1080f })
        {
            float layoutScale = screenHeight / 1440f;   // the recorded page's layout, scaled to this resolution
            float screenWidth = screenHeight == 1440f ? 2560f : 1920f;
            float clearance = ResultsBounds.Clearance(screenHeight);
            float iconRow = 366f * layoutScale;                    // the final icon-row edge
            float waitingText = 1373f * layoutScale;               // the client's waiting block, laid out
            // The band must not depend on whether the parts have drawn: it reads the parts' own layout slots.
            float layoutEdge = CompletionFinalBounds.Bottom(LaidOutParts(), out _, out int laidOut) * layoutScale;
            check(Near(layoutEdge, iconRow, 0.6f) && laidOut == 5,
                $"at {screenHeight:0}p the row's layout edge is the final one from the first frame ({layoutEdge:0.#}px, {laidOut} parts answered from layout alone)");
            bool resolved = ResultsBounds.TryResolve(screenHeight, iconRow, ResultBoundSource.IconRow,
                float.NaN, waitingText, ResultBoundSource.FooterTextControl, float.NaN, out ResultsBand band);
            check(resolved && band.Fits, $"a {screenHeight:0}p client page whose waiting block is laid out hands the band over at once");
            check(Near(band.Top, iconRow + clearance, 0.6f),
                $"band top = final icon-row edge + the scaled clearance: top={band.Top:0.#} vs {iconRow + clearance:0.#}px");
            check(Near(band.Bottom, waitingText - clearance, 0.6f),
                $"band bottom = waiting block's laid-out top - the same clearance: bottom={band.Bottom:0.#}px");
            check(band.BottomSource == ResultBoundSource.FooterTextControl,
                "the lower wall is named for what it really is: the control's laid-out rectangle");
            BoardFitsBand(check, screenWidth, screenHeight, band, firstEvaluation: true);
        }
    }

    // I2: the same page fully drawn reads numerically the same band and places the same rectangle.
    private static void FullyDrawnPagesReadTheVerySameBand(Action<bool, string> check)
    {
        foreach (float screenHeight in new[] { 1440f, 1080f })
        {
            float layoutScale = screenHeight / 1440f;   // the recorded page's layout, scaled to this resolution
            // The layout edge the band hangs from, computed from the two part sets: the first is the page the moment
            // it is laid out with nothing drawn, the second the same page fully drawn.
            float undrawnEdge = CompletionFinalBounds.Bottom(LaidOutParts(), out _, out _) * layoutScale;
            float drawnEdge = CompletionFinalBounds.Bottom(FullyDrawnParts(), out _, out _) * layoutScale;
            check(Near(undrawnEdge, drawnEdge, 0.01f) && Near(drawnEdge, 366f * layoutScale, 0.6f),
                $"I2 at {screenHeight:0}p: the section's edge computed from a page where nothing has drawn yet ({undrawnEdge:0.#}) equals the edge on the same page fully drawn ({drawnEdge:0.#}) - both are the final layout");
            ResultsBounds.TryResolve(screenHeight, undrawnEdge, ResultBoundSource.IconRow,
                float.NaN, 1373f * layoutScale, ResultBoundSource.FooterTextControl, float.NaN, out ResultsBand undrawnBand);
            var undrawnBoard = BoardInBand(2560f, screenHeight, undrawnBand);
            ResultsBounds.TryResolve(screenHeight, drawnEdge, ResultBoundSource.IconRow,
                float.NaN, 1373f * layoutScale, ResultBoundSource.FooterTextControl, float.NaN, out ResultsBand drawnBand);
            var drawnBoard = BoardInBand(2560f, screenHeight, drawnBand);
            check(Near(undrawnBand.Top, drawnBand.Top, 0.01f) && Near(undrawnBand.Bottom, drawnBand.Bottom, 0.01f),
                $"and the band built from it is identical on both pages ({undrawnBand.Top:0.#}..{undrawnBand.Bottom:0.#} vs {drawnBand.Top:0.#}..{drawnBand.Bottom:0.#})");
            check(undrawnBoard.Y == drawnBoard.Y && undrawnBoard.Bottom == drawnBoard.Bottom,
                "and the board rectangle it places is the same one - the placement never moves with the reveal's progress");
            // I3, on the placed rectangle: always inside the band, whatever drew.
            check(undrawnBoard.Y >= undrawnBand.Top - 2f && undrawnBoard.Bottom <= undrawnBand.Bottom + 2f,
                "the board sits inside its band on both pages");
        }
    }

    // I1/I3:I4 for the failure page: the title's laid-out rectangle is the upper edge at alpha 0 - the alpha is not
    // even an input.
    private static void TheFailureTitleLayoutIsTheUpperEdgeAtAlphaZero(Action<bool, string> check)
    {
        foreach (float screenHeight in new[] { 1440f, 1080f })
        {
            float layoutScale = screenHeight / 1440f;   // the recorded page's layout, scaled to this resolution
            float titleBottom = 460f * layoutScale;      // the title's laid-out screen rectangle
            float buttonTop = 1151f * layoutScale;       // the host's leave/restart button, laid out
            bool resolved = ResultsBounds.TryResolve(screenHeight, titleBottom, ResultBoundSource.TitleAnchor,
                buttonTop, float.NaN, ResultBoundSource.FooterTextControl, float.NaN, out ResultsBand band);
            check(resolved && Near(band.Top, titleBottom + ResultsBounds.Clearance(screenHeight), 0.6f),
                $"the failure page's upper edge is the title's layout bottom + the scaled clearance, at alpha 0 and on the first frame: top={band.Top:0.#}px at {screenHeight:0}p");
            check(band.BottomSource == ResultBoundSource.FooterControl
                  && Near(band.Bottom, buttonTop - ResultsBounds.Clearance(screenHeight), 0.6f),
                "and its lower edge is the button the host lays out, by the same clearance - no alpha, text or settle frame appears anywhere in that arithmetic");
            BoardFitsBand(check, 2560f, screenHeight, band, firstEvaluation: true);
        }
        // The anchor policy itself: the laid-out rectangle is an anchor source of its own, for the active title and
        // for the inactive one, before the native failure sequence and without asking for text.
        check(FailureTitleAnchorPolicy.Select(true, false, true, true, true, true, false)
                == FailureTitleAnchorKind.MissionFailed
            && FailureTitleAnchorPolicy.Select(true, false, true, false, true, true, false)
                == FailureTitleAnchorKind.MissionTitleLayout,
            "FailureTitleAnchorPolicy.Select accepts the layout-geometry anchor source: the active title stays the drawn-text candidate and the laid-out rectangle of an inactive one is a named anchor of its own");
    }

    // I4, client role: only the waiting block's laid-out rectangle exists - it is the whole lower edge.
    private static void TheClientWaitingLayoutRectIsTheLowerEdge(Action<bool, string> check)
    {
        foreach (float screenHeight in new[] { 1440f, 1080f })
        {
            float layoutScale = screenHeight / 1440f;   // the recorded page's layout, scaled to this resolution
            bool resolved = ResultsBounds.TryResolve(screenHeight, 366f * layoutScale, ResultBoundSource.IconRow,
                float.NaN, 1373f * layoutScale, ResultBoundSource.FooterTextControl, float.NaN, out ResultsBand band);
            check(resolved && Near(band.Bottom, 1373f * layoutScale - ResultsBounds.Clearance(screenHeight), 0.6f),
                $"on a client page with only the waiting text's layout rect, the lower edge is that rect's top minus the clearance ({band.Bottom:0.#}px at {screenHeight:0}p) - without an alpha > 0.02, an activeInHierarchy or a non-empty text test anywhere");
            BoardFitsBand(check, 2560f, screenHeight, band, firstEvaluation: true);
        }
    }

    // I4, host role: `LobbyEdgeSource.Layout` - the leave-expedition button's laid-out rectangle.
    private static void TheHostButtonLayoutRectIsTheLowerEdge(Action<bool, string> check)
    {
        foreach (float screenHeight in new[] { 1440f, 1080f })
        {
            float layoutScale = screenHeight / 1440f;   // the recorded page's layout, scaled to this resolution
            bool resolved = ResultsBounds.TryResolve(screenHeight, 366f * layoutScale, ResultBoundSource.IconRow,
                683f * layoutScale, float.NaN, ResultBoundSource.FooterTextControl, float.NaN, out ResultsBand band);
            check(resolved && Near(band.Bottom, 683f * layoutScale - ResultsBounds.Clearance(screenHeight), 0.6f),
                $"on a host page the button's laid-out rect is the lower edge on the first frame ({band.Bottom:0.#}px at {screenHeight:0}p, the 683px wall of the recorded page)");
            BoardFitsBand(check, 2560f, screenHeight, band, firstEvaluation: true);
        }
    }

    // I3's hard half: a 100 px band can never hold a 30-row board at the absolute minimum glyph (135 px), so the fit
    // reports `squeezed`, a page band short of `MinimumBand` reports `clamped`, the placement refuses, and the vanilla
    // page stays - there is no path that draws the board past the *page's own* band and merely lowers its opacity
    // (the invented fail-open band is the 1.19.22 exception, and it extends instead of fading).
    private static void ABandTooShortForTheBoardRefusesInsteadOfFading(Action<bool, string> check)
    {
        int floor = ResultsBandFit.FloorForBand(100f, Rows, PanelDensity.Compact,
            ResultsFontGrowth.MinimumReadablePixels, out bool squeezed);
        check(squeezed && floor == ResultsBandFit.AbsoluteMinimumPixels,
            $"a 100 px band over {Rows} rows reports squeezed and walks the floor to the {ResultsBandFit.AbsoluteMinimumPixels} px absolute minimum glyph: floor={floor}");
        float smallest = ResultsBandFit.MinimumDrawableHeight(Rows, PanelDensity.Compact);
        check(smallest > 100f + 0.5f,
            $"the shortest grid this build can draw is {smallest:0}px, which a 100 px band cannot hold at any glyph size");
        var shortBand = new BoardStrip(300f, 400f, true, 400f, 96f, true);
        bool placed = ResultBoardHold.TryPlace(1920f, 1440f, shortBand, 800f, smallest, 0f, out _);
        check(!placed && !ResultBoardLatch.Placed(placed, false, false),
            "the placement refuses the board the band cannot hold, and the vanilla page stays - the taller-than-band board is never drawn past the band and merely faded");
        bool clamped = ResultsBounds.TryResolve(1440f, 366f, ResultBoundSource.IconRow,
            float.NaN, 200f, ResultBoundSource.FooterTextControl, float.NaN, out ResultsBand squeezedBand)
            && squeezedBand.Clamped && squeezedBand.Height >= ResultsBounds.MinimumBand - 0.5f;
        check(clamped,
            "and a band the page collapsed below the minimum is reported clamped, with the lower edge held - never drawn into");
        check(!ResultFallbackTier.AcceptsPageBand(100f, smallest),
            "the fail-open tier agrees: a band half the size of the smallest drawable grid is not a band");
    }

    // (g): a page that hands over no geometry at all keeps the 1.19.17/1.19.18 safety chain - the strict path refuses
    // (deferred, so nothing is hidden), the retries stay bounded, and the fail-open band answers instead - all of it
    // with no settle gate turning a missing edge into a placement.
    private static void APageWithoutAnyGeometryKeepsTheVanillaPageAndBoundedRetries(Action<bool, string> check)
    {
        check(ResultsUpperBound.Decide(false, false) == ResultsUpperBound.Decision.Deferred
              && ResultsUpperBound.Decide(false, true) == ResultsUpperBound.Decision.Deferred,
            "a page whose completion section hands over no geometry is deferred by the strict path - never placed from a guess");
        check(!ResultFallbackPolicy.ShouldFallback(0, false)
              && !ResultFallbackPolicy.ShouldFallback(ResultFallbackPolicy.MissBudget - 1, false)
              && ResultFallbackPolicy.ShouldFallback(ResultFallbackPolicy.MissBudget, false)
              && ResultFallbackPolicy.ShouldFallback(0, true),
            "the fail-open path still answers only after the bounded refusal budget or F8");
        bool answered = ResultFallbackLayout.TryResolve(1440f, float.NaN, float.NaN, float.NaN, float.NaN,
            ResultsFontGrowth.MinimumBoardHeight(Rows, RowEm, PanelDensity.Compact), out ResultFallbackPlan plan);
        check(answered && plan.Placed && plan.Reason == ResultFallbackReason.ScreenSafeArea,
            "and that path still places a real board from the window's own band when the page offered nothing - the 1.19.17/1.19.18 safety assertions stay alive");
        var gate = new ResultEdgeGate();
        bool everReady = false;
        for (int frame = 0; frame < ResultEdgeGate.SettleAttempts + 200; frame++) everReady |= gate.Observe(false, float.NaN);
        check(!everReady,
            "no number of readings turns the missing edge into a settled one: the gates stay gates and the retries stay bounded");
        var moving = new ResultEdgeGate();
        bool settledWhileMoving = false;
        for (int frame = 0; frame < 400; frame++) settledWhileMoving |= moving.Observe(true, 400f + (frame % 2) * 6f);
        check(!settledWhileMoving,
            "and an edge the page never holds still is not settled either - a give-up is the fail-open path's to say, never the gate's");
        var retry = new ResultRetryCadence();
        for (int frame = 0; frame < 400; frame++) retry.Due(frame);
        check(retry.Throttled && retry.Due(1000) && retry.Due(5000),
            "and the cadence keeps measuring the page at its bounded rate instead of giving up on it for good");
    }

    // The fit the runtime composes from the band: glyph from the band, the grid at that glyph, TryPlace into the band.
    // `firstEvaluation` records I1 - the very first computation places, with no gate feeding anything.
    private static void BoardFitsBand(Action<bool, string> check, float screenWidth, float screenHeight, ResultsBand band, bool firstEvaluation)
    {
        int floor = ResultsBandFit.FloorForBand(band.Height, Rows, PanelDensity.Compact,
            ResultsFontGrowth.MinimumReadablePixels, out bool squeezed);
        int font = ResultsBandFit.FontForBand(band.Height, Rows, PanelDensity.Compact, floor);
        float rowHeight = ResultsBandFit.RowHeight(font, PanelDensity.Compact);
        float gridHeight = rowHeight * Rows;
        var strip = new BoardStrip(band.Top, band.Bottom, true, band.Bottom, float.NaN, true);
        bool placed = ResultBoardHold.TryPlace(screenWidth, screenHeight, strip, 600f, gridHeight, 0f, out LayoutRect board, 2f);
        check(placed, $"the {band.Height:0}px band at {screenHeight:0}p places the board on the {(firstEvaluation ? "first evaluation, with no settle or alpha gate in the chain" : "evaluation")}");
        check(board.Y >= band.Top - 2f && board.Bottom <= band.Bottom + 2f,
            $"and it is inside the band: board {board.Y:0.#}..{board.Bottom:0.#}px vs band {band.Top:0.#}..{band.Bottom:0.#}px");
        check(!squeezed || font < ResultsFontGrowth.MinimumReadablePixels,
            "a squeezed band reports it; the board is shrunk through the band-fit floor, never drawn past the band");
    }

    // The policy chain for one band, so two pages can be compared byte for byte (I2).
    private static LayoutRect BoardInBand(float screenWidth, float screenHeight, ResultsBand band)
    {
        int floor = ResultsBandFit.FloorForBand(band.Height, Rows, PanelDensity.Compact,
            ResultsFontGrowth.MinimumReadablePixels, out _);
        int font = ResultsBandFit.FontForBand(band.Height, Rows, PanelDensity.Compact, floor);
        float gridHeight = ResultsBandFit.RowHeight(font, PanelDensity.Compact) * Rows;
        var strip = new BoardStrip(band.Top, band.Bottom, true, band.Bottom, float.NaN, true);
        ResultBoardHold.TryPlace(screenWidth, screenHeight, strip, 600f, gridHeight, 0f, out LayoutRect board, 2f);
        return board;
    }

    // ---- I5 and the wiring, read from the production source -----------------------------------------------
    private static void Wiring(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the 1.19.21 regression locates the production sources");
        if (root == null) return;
        string display = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        string policy = File.ReadAllText(Path.Combine(root, "ResultsPagePolicy.cs"));
        string final = File.ReadAllText(Path.Combine(root, "ResultFinalLayout.cs"));

        // 1.19.41 re-point: while the page's own reveal is running the band's upper bound is the parts' layout slots and
        // nothing drawn (exactly what 1.19.21 pinned); once that reveal has finished it is the deeper of those slots and
        // the pixels really drawn under them, which is the caption row the panel's top used to press on.
        check(display.Contains("CompletionFinalBounds.Placed(parts, RevealFinished, contentSettled, out int finalDeepest, out _finalUndrawn)")
              && !display.Contains("CompletionFinalBounds.Bound(finalParts")
              && !display.Contains("_finalIncludeLaidOut ="),
            "the band's upper bound is the parts' own layout slots while the reveal runs, and once it has finished - or the ledger has proved no reveal is coming (F3) - the pixels each part really draws; both readings from one call, so the display cannot mix them by hand");
        string iconRow = Between(display, "private static float IconRowBottom(Component page, bool revealFinished, bool contentSettled)", "    // The waiting-status block a client's success page draws where the host's button would be.");
        check(iconRow.Length > 0 && iconRow.Contains("Read(success.m_sectorHeader)")
              && !iconRow.Contains("Read(success.m_sectorsWhiteBox)"),
            "the icon row's own band excludes the white plate - the part list the band is computed from is the label and the four icon slots only");
        check(display.Contains("CompletionReady(page);") && !display.Contains("revealReady")
              && !display.Contains("completion-edge-unsettled") && !display.Contains("title-edge-unsettled"),
            "the placement depends on neither the native Blink completion nor the settle frames - the refusal lines that printed have=/need= as if they were the reason are gone with them");
        check(display.Contains("titleBottom = missionRect.Bottom;") && policy.Contains("MissionTitleLayout = 3")
              && display.Contains("edgeFrom="),
            "the failure page's upper edge is the title's laid-out screen rect, and the [DIAG] line names whether an edge came from drawn text or from layout");
        check(display.Contains("protectWaitingStatus = guestStatus != null;")
              && !display.Contains("guestStatus != null && guestStatus.gameObject.activeInHierarchy\n                    && TryScreenRect(guestStatus"),
            "the waiting block's laid-out rectangle is the lower wall without an activeInHierarchy or non-empty-text test");
        // 1.19.47 (review #4): the fade moved into the per-repaint fail-open re-check (`FadeInPlace`), so the "never
        // placed and merely faded" invariant is asked of the *fit* body - the one path that places into the page's own band.
        int fitStart = display.IndexOf("private static bool Fit(BoardStrip strip", StringComparison.Ordinal);
        int fitEnd = display.IndexOf("private static bool Placement(Component page, bool succeeded", StringComparison.Ordinal);
        string fitBody = fitStart >= 0 && fitEnd > fitStart ? display.Substring(fitStart, fitEnd - fitStart) : string.Empty;
        check(display.Contains("total <= strip.Bottom - strip.Top + 0.5f")
              && fitBody.Length > 0 && !fitBody.Contains("ResultFallbackLayout.OverlapOpacity"),
            "1.19.22 re-point: the hard band invariant covers the page's own band - the fit never places a taller-than-band board into it and merely lowers its opacity; the invented fail-open band is sized from the board instead, and the faint tier now belongs to the fail-open re-check (`FadeInPlace`, review #4)");
        check(display.Contains("band=({strip.Top:0}..{strip.Bottom:0}), bandSource={strip.FooterSource}")
              && display.Contains("boardBand={DescribeBand(_holdValid ? _holdStrip.FooterSource : footerSource)}"),
            "a placed frame prints the band it really used, never `boardBand=unresolved`");
        check(final.Contains("ResultFinalGate") && policy.Contains("public sealed class ResultTitleGate"),
            "the settle latches survive as the drawn-pixel route's diagnostics, exactly as the brief asks");
        Console.WriteLine("WIRING: RESULT_FINAL_LAYOUT_BAND (static source check; not a runtime behaviour test)");
    }

    private static bool Near(float left, float right, float tolerance) => Math.Abs(left - right) <= tolerance;

    private static string Between(string text, string from, string to)
    {
        int start = text.IndexOf(from, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        int end = text.IndexOf(to, start, StringComparison.Ordinal);
        return end < 0 ? text.Substring(start) : text.Substring(start, end - start);
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj"))) directory = directory.Parent;
        return directory != null ? directory.FullName : null;
    }
}
