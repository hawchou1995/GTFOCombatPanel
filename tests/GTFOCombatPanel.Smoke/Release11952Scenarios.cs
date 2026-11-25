using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// ---------------------------------------------------------------------------------------------------
// Release11952Scenarios.cs - the 1.19.51 review: F1 drawn-pixel floor, F2/F6 the board's own column,
// F3 the production placement path, F4 the re-aligned width, F5 the band below a real mid-column control
// ---------------------------------------------------------------------------------------------------
//
// Everything below runs on the production policies that decide these questions - `CompletionFinalBounds`,
// `LayoutColumn`, `ResultInteractionStripPolicy`, `ResultBoardBands`, `ResultsBounds`, `ResultSuccessBandChoice`,
// `ResultsBandFit`, `ResultsFontGrowth`, `ResultScroll`, `ResultBoardHold.TryPlace` (bottom-aligned, with the
// production `BoardSlack` of 2 px) and `ResultChromeLayout.BoardOf` / `ResultChromeMetrics.For(w, h).HeaderHeight`
// (the same header the runtime reserves) - so the numbers are the ones the page would really get, not a second
// placement model written for the test.
//
// The recorded page (`Survival_LogOutput.log`, canvas units on a 1920x1080 window):
//   L4811  sectorHeader   own=233 visual=233 hidden=0  drawn=5 complete=true
//          sectorIconMain own=198 visual=333 hidden=14 drawn=6 complete=false   <- 6 children really drawn to 333
//   L4814  band 249..439 = 190 px; L4837 plate (795,238,330x186); L4812/L4813 the 12 px glyph kept, rows on the wheel
//
// What that log does *not* contain is the identity or the horizontal extent of the interaction at y=455, and this
// suite therefore runs both hypotheses it leaves open:
//   H1  the interaction stands beside the board's column (it can never be covered) -> it may not end the band;
//   H2  the interaction really spans the board's column (a wide container, a full-width control) -> it stays a wall,
//       the strip above it is 90 px, and the board goes to the free band below the page's actions (F5).
// Neither hypothesis is claimed as observed anywhere in this file or in the release notes.
internal static class Release11952Scenarios
{
    private const float Width = 1920f, Height = 1080f;
    private const float Gap = 16f;
    private const float BoardSlack = 2f;            // ResultsDisplay.BoardSlack
    private const int Rows = 30;
    private const float BoardWidth = 330f;          // the recorded grid (1 player, 12 px glyph)
    private const float RowHeight = 18f;            // ResultsBandFit.RowHeight(12, Compact)
    private const float GridHeight = Rows * RowHeight;
    // The page's own footer control, as the toggle's overlap line recorded it, and the waiting-status line under it.
    private static readonly LayoutRect LeaveButton = new LayoutRect(752f, 973f, 440f, 80f);
    private const float WaitingText = 982f;
    // The interaction at 455: its horizontal extent is unknown. Both readings are exercised below.
    private static readonly LayoutRect SideNotice = new LayoutRect(217f, 455f, 503f, 60f);   // beside the board
    private static readonly LayoutRect WideControl = new LayoutRect(700f, 455f, 500f, 60f);  // across the column

    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        TheDrawnPixelsAreTheStripFloorAndNotANewEdge(check);
        TheRecordedPageUnderH1ShowsAllThirtyRows(check);
        TheSamePageAt1440ShowsAllThirtyRows(check);
        TheRecordedPageUnderH2UsesTheBandBelowTheControl(check);
        AShortWindowStillShowsEveryRow(check);
        TheFourPixelGlyphCannotHappenInProduction(check);
        TheFailurePageAndTheFixedModeAreUntouched(check);
        F8IsAnsweredByTheSameBandAndTheSamePlacement(check);
        TheVersionCarrierIsThisFix(check);
    }

    // ---------- F1 ----------

    private static void TheDrawnPixelsAreTheStripFloorAndNotANewEdge(Action<bool, string> check)
    {
        // The recorded readings, in the order CompletionFinalBounds sees them.
        var parts = new[]
        {
            Part(present: true, active: true, scheduled: false, own: 233f, visual: 233f, measured: true,
                hidden: 0, unmeasured: 0, drawn: 5, alpha: 1f),                     // sectorHeader
            Part(present: true, active: true, scheduled: false, own: 198f, visual: 333f, measured: true,
                hidden: 14, unmeasured: 0, drawn: 6, alpha: 1f)                     // sectorIconMain
        };
        float placed = CompletionFinalBounds.Placed(parts, revealFinished: true, contentSettled: false,
            out int deepest, out _);
        check(Near(placed, 233f),
            "the section's own edge is still the container of the incomplete part's sibling (233), which is what 1.19.50"
                + " read and what the recorded band 249..439 was hung from: " + placed.ToString("0"));
        float floor = CompletionFinalBounds.DrawnFloor(parts);
        check(Near(floor, 333f),
            "the drawn floor is the pixels the section has really painted - the icon part drew six children to 333"
                + " while its container ends at 198: " + floor.ToString("0"));
        check(!Near(floor, 198f) && !Near(floor, 233f),
            "so it is neither the container that ends above the captions (198) nor the other part's edge (233)");
        float completion = Math.Max(placed, floor);
        check(Near(completion + Gap, 349f),
            "the strip now hangs from 349 (333 + the 16 px clearance), not from 249 - which is the whole of F1: the"
                + " board's top edge may not sit on 主要/精英 the page has already drawn");
        check(CompletionFinalBounds.ContentComplete(parts[1]) == false,
            "and nothing here claims the section is complete: the part keeps `complete=false` in the log, the floor is"
                + " only a floor");

        // A part that drew nothing, is switched off, has an unreadable alpha or an unmeasured bottom contributes
        // nothing: the floor can never be invented.
        check(float.IsNaN(CompletionFinalBounds.DrawnFloor(new[]
              {
                  Part(present: true, active: true, scheduled: false, own: 500f, visual: 900f, measured: true,
                      hidden: 0, unmeasured: 0, drawn: 0, alpha: 1f)
              })),
            "a part that drew nothing contributes nothing, however deep its container is");
        check(float.IsNaN(CompletionFinalBounds.DrawnFloor(new[]
              {
                  Part(present: true, active: false, scheduled: true, own: 500f, visual: 900f, measured: true,
                      hidden: 0, unmeasured: 0, drawn: 3, alpha: 1f)
              })),
            "an inactive part contributes nothing - its pixels are not on the page");
        check(float.IsNaN(CompletionFinalBounds.DrawnFloor(new[]
              {
                  Part(present: true, active: true, scheduled: false, own: 500f, visual: float.NaN, measured: true,
                      hidden: 0, unmeasured: 0, drawn: 3, alpha: 1f)
              })),
            "a NaN drawn bottom contributes nothing (no NaN ever reaches the band)");
        check(float.IsNaN(CompletionFinalBounds.DrawnFloor(new[]
              {
                  Part(present: true, active: true, scheduled: false, own: 500f, visual: 900f, measured: true,
                      hidden: 0, unmeasured: 0, drawn: 3, alpha: 0f)
              })),
            "and a fully faded part contributes nothing either");
        check(float.IsNaN(CompletionFinalBounds.DrawnFloor(Array.Empty<CompletionPart>())),
            "an empty section answers no floor at all, so the caller keeps the edge it had");
    }

    // ---------- H1: the interaction at 455 stands beside the board ----------

    private static void TheRecordedPageUnderH1ShowsAllThirtyRows(Action<bool, string> check)
    {
        var controls = new List<ResultActionBounds>
        {
            new ResultActionBounds(SideNotice), new ResultActionBounds(LeaveButton)
        };
        // The board's column is the table's own width (330, centred), measured by the two-phase probe (F2) - never the
        // 440 px footer button, which is a different control.
        var column = Centred(BoardWidth);
        check(ResultInteractionStripPolicy.TryResolve(349f - Gap, Gap, controls, out ResultInteractionStrip strip, column)
              && strip.BeyondColumn == 1 && Near(strip.FooterTop, LeaveButton.Y),
            "H1: with the board's own column the interaction beside it no longer ends the strip (besideColumn="
                + strip.BeyondColumn + "), so the wall is the page's leave button at " + strip.FooterTop.ToString("0"));
        check(Near(strip.BoardTop, 349f),
            "and the strip still starts under the drawn captions: " + strip.BoardTop.ToString("0"));

        Check(ResultsBounds.TryResolve(Height, 333f, ResultBoundSource.IconRow, strip.FooterTop, WaitingText,
            ResultBoundSource.FooterTextControl, strip.FooterTop, out ResultsBand band), check, "the page resolves a band");
        check(Near(band.Top, 349f) && Near(band.Bottom, 957f),
            "the band is 349..957 (the button's own clearance line wins over the waiting line at 966): "
                + band.Top.ToString("0") + ".." + band.Bottom.ToString("0"));

        // F3: the production placement, bottom-aligned, with the production slack and the production header.
        ResultChromeMetrics metrics = ResultChromeMetrics.For(Width, Height);
        check(Near(metrics.HeaderHeight, 42f),
            "the header the runtime reserves at 1920x1080 is 42 px: " + metrics.HeaderHeight.ToString("0"));
        int visible = ResultsScroll.Viewport(Rows, ResultsScroll.FrozenRows, band.Height - metrics.HeaderHeight,
            RowHeight, ResultsScroll.FrozenRows).Visible;
        float plateHeight = Math.Min(GridHeight, ResultsScroll.PlateHeight(visible, RowHeight,
            band.Height - metrics.HeaderHeight));
        check(visible == Rows && Near(plateHeight, GridHeight),
            "the plate holds all " + visible + " of " + Rows + " rows (" + plateHeight.ToString("0") + " px)");
        Check(ResultBoardHold.TryPlace(Width, Height, new BoardStrip(band.Top, band.Bottom, true, LeaveButton.Y, 80f),
            BoardWidth, plateHeight + metrics.HeaderHeight, 0f, out LayoutRect board, BoardSlack, bottomAligned: true),
            check, "the production placement accepts the whole board");
        check(Near(board.X, 795f) && Near(board.Right, 1125f),
            "the board is centred: x " + board.X.ToString("0") + ".." + board.Right.ToString("0"));
        // `TryPlace` spends the production slack on the strip's foot, exactly as the runtime call does, so the board's
        // foot is the wall plus that tolerance and its top follows from the height it holds.
        float foot = band.Bottom + BoardSlack;
        check(Near(board.Bottom, foot) && Near(board.Y, foot - (plateHeight + metrics.HeaderHeight)),
            "and bottom-aligned to the wall it must clear: the board is ("
                + board.X.ToString("0") + "," + board.Y.ToString("0") + ","
                + board.Width.ToString("0") + "x" + board.Height.ToString("0") + ") - the physical place, "
                + "not a top-aligned box");
        ResultChromeLayout.BoardOf(new LayoutRect(board.X, board.Y + metrics.HeaderHeight, board.Width, plateHeight), metrics);
        LayoutRect reconstructed = ResultChromeLayout.BoardOf(
            new LayoutRect(board.X, board.Y + metrics.HeaderHeight, board.Width, plateHeight), metrics);
        check(Near(reconstructed.Y, board.Y) && Near(reconstructed.Bottom, board.Bottom),
            "the table origin + the reserved header reconstructs the same board rect ("
                + reconstructed.Y.ToString("0") + ".." + reconstructed.Bottom.ToString("0") + ")");

        // The protected objects, by their own rectangles.
        check(SideNotice.Right < board.X - 0.5f,
            "the interaction beside the column is left of the board and never covered: its right edge "
                + SideNotice.Right.ToString("0") + " < " + board.X.ToString("0"));
        check(board.Bottom <= LeaveButton.Y - ResultBoardHold.ButtonClearance + 0.5f,
            "and the board's foot keeps the clearance above the button the player has to press: "
                + board.Bottom.ToString("0") + " <= " + LeaveButton.Y.ToString("0") + " - "
                + ResultBoardHold.ButtonClearance.ToString("0"));
        check(column.Reaches(LeaveButton) && !column.Reaches(SideNotice),
            "the rule that produced this: the button reaches the column, the notice does not");
    }

    // ---------- the same page at 1440 ----------

    private static void TheSamePageAt1440ShowsAllThirtyRows(Action<bool, string> check)
    {
        const float scale = 4f / 3f;
        ResultChromeMetrics metrics = ResultChromeMetrics.For(2560f, 1440f);
        check(Near(metrics.HeaderHeight, 53f),
            "the header at 2560x1440 is 53 px (the chrome's own rule, not 42x4/3): " + metrics.HeaderHeight.ToString("0"));
        float rowHeight = ResultsBandFit.RowHeight(ResultAutoBoardContract.TargetGlyphPixels(1440f), PanelDensity.Compact);
        check(ResultAutoBoardContract.TargetGlyphPixels(1440f) == 16 && Near(rowHeight, 24f),
            "a 1440-high window aims at 16 px / 24 px rows: " + rowHeight.ToString("0.##"));
        var controls = new List<ResultActionBounds>
        {
            new ResultActionBounds(new LayoutRect(SideNotice.X * scale, SideNotice.Y * scale,
                SideNotice.Width * scale, SideNotice.Height * scale)),
            new ResultActionBounds(new LayoutRect(LeaveButton.X * scale, LeaveButton.Y * scale,
                LeaveButton.Width * scale, LeaveButton.Height * scale))
        };
        Check(ResultInteractionStripPolicy.TryResolve(333f * scale, Gap * scale, controls,
            out ResultInteractionStrip strip, Centred(BoardWidth * scale, 2560f)), check, "the 1440 strip resolves");
        Check(ResultsBounds.TryResolve(1440f, 333f * scale, ResultBoundSource.IconRow, strip.FooterTop,
            WaitingText * scale, ResultBoundSource.FooterTextControl, strip.FooterTop, out ResultsBand band),
            check, "and its band");
        int visible = ResultsScroll.Viewport(Rows, ResultsScroll.FrozenRows, band.Height - metrics.HeaderHeight,
            rowHeight, ResultsScroll.FrozenRows).Visible;
        check(visible == Rows,
            "all " + visible + " of " + Rows + " rows fit the 1440 band (" + band.Height.ToString("0") + " px)");
        float width = BoardWidth * scale;
        Check(ResultBoardHold.TryPlace(2560f, 1440f, new BoardStrip(band.Top, band.Bottom, true, LeaveButton.Y * scale, 80f * scale),
            width, Rows * rowHeight + metrics.HeaderHeight, 0f, out LayoutRect board, BoardSlack, bottomAligned: true),
            check, "and the production placement accepts it");
        check(Near(board.Y, band.Bottom + BoardSlack - (Rows * rowHeight + metrics.HeaderHeight))
              && Near(board.Bottom, band.Bottom + BoardSlack),
            "bottom-aligned at 1440 as well: (" + board.X.ToString("0") + "," + board.Y.ToString("0") + ","
                + board.Width.ToString("0") + "x" + board.Height.ToString("0") + ")");
    }

    // ---------- H2: the interaction really spans the column ----------

    private static void TheRecordedPageUnderH2UsesTheBandBelowTheControl(Action<bool, string> check)
    {
        var controls = new List<ResultActionBounds>
        {
            new ResultActionBounds(WideControl), new ResultActionBounds(LeaveButton)
        };
        var column = Centred(BoardWidth);
        check(column.Reaches(WideControl),
            "H2: a control that really spans the board's column is still a wall");
        check(ResultInteractionStripPolicy.TryResolve(WideControl.Y - Gap, Gap, controls, out ResultInteractionStrip strip, column)
              && strip.BeyondColumn == 0 && Near(strip.FooterTop, WideControl.Y),
            "so the strip ends above it again (y=" + strip.FooterTop.ToString("0") + ", besideColumn=0)");
        Check(ResultsBounds.TryResolve(Height, 333f, ResultBoundSource.IconRow, strip.FooterTop, WaitingText,
            ResultBoundSource.FooterTextControl, strip.FooterTop, out ResultsBand band), check, "and the band is short");
        check(Near(band.Top, 349f) && Near(band.Bottom, 439f),
            "349..439 = " + band.Height.ToString("0") + " px is the strip the section and that control leave - too short"
                + " for the table, and the reason the free band below it has to be considered at all");

        // The free band the scan measures under every interaction (production policy, production gap).
        var actions = new List<ResultActionBounds>
        {
            new ResultActionBounds(WideControl), new ResultActionBounds(LeaveButton)
        };
        check(ResultBoardBands.TryBelowActions(band.Top, Height, actions, Gap, out ResultBoardBand below),
            "the page leaves a free band below its measured actions");
        check(Near(below.Top, WideControl.Bottom + Gap) && Near(below.Bottom, LeaveButton.Y - Gap),
            "it runs from the control's own foot plus the clearance to the button's clearance line: "
                + below.Top.ToString("0") + ".." + below.Bottom.ToString("0") + " (" + below.Height.ToString("0") + " px)");

        ResultChromeMetrics metrics = ResultChromeMetrics.For(Width, Height);
        check(ResultSuccessBandChoice.PrefersBelow(band.Height, below.Height, Rows, metrics.HeaderHeight, RowHeight,
                out int rowsAbove, out int rowsBelow),
            "F5: the band below shows strictly more of the table (" + rowsBelow + " rows) than the strip above ("
                + rowsAbove + " rows), so the board belongs there");
        check(rowsAbove < Rows && rowsBelow > rowsAbove,
            "the comparison is made of rows at the window's own glyph, never of a height forced into a band: "
                + rowsAbove + " -> " + rowsBelow + " of " + Rows);
        check(!ResultSuccessBandChoice.PrefersBelow(band.Height, below.Height, rowsAbove, metrics.HeaderHeight, RowHeight,
                out _, out _),
            "and it does not fire when the strip above already shows everything it has to show");
        check(!ResultSuccessBandChoice.PrefersBelow(band.Height, float.NaN, Rows, metrics.HeaderHeight, RowHeight,
                out _, out _),
            "an unmeasured band never wins");

        // The board really stands in the lower band and clears both controls - and 582 is never pushed into 90.
        check(!ResultBoardHold.TryPlace(Width, Height, new BoardStrip(band.Top, band.Bottom, true, LeaveButton.Y, 80f),
                BoardWidth, GridHeight + metrics.HeaderHeight, 0f, out _, BoardSlack, bottomAligned: true),
            "the 540+42 px table is not forced into the 90 px strip (the placement refuses it, as it must)");
        check(!ResultBoardHold.TryPlace(Width, Height, new BoardStrip(band.Top, band.Bottom, true, LeaveButton.Y, 80f),
                BoardWidth, below.Height, 0f, out _, BoardSlack, bottomAligned: true),
            "and not into the 426 px one either - the fit sizes the table, it never squeezes a height into a band");
        int fitted = FitForBand(below.Height, metrics.HeaderHeight, out int fittedVisible, out float fittedPlate);
        check(fitted >= ResultsFontGrowth.MinimumReadablePixels && fitted <= ResultAutoBoardContract.TargetGlyphPixels(Height),
            "the table the lower band really holds is drawn at " + fitted + " px, between the readable floor and the"
                + " window's target");
        check(ResultBoardHold.TryPlace(Width, Height, new BoardStrip(below.Top, below.Bottom, true, LeaveButton.Y, 80f),
                BoardWidth, fittedPlate + metrics.HeaderHeight, 0f, out LayoutRect placed, BoardSlack, bottomAligned: true),
            "and it is placed inside that band");
        check(placed.Y >= WideControl.Bottom + Gap - 0.5f && placed.Bottom <= LeaveButton.Y - ResultBoardHold.ButtonClearance + 0.5f,
            "clear of the control above it (" + (WideControl.Bottom + Gap).ToString("0") + ") and of the button below ("
                + (LeaveButton.Y - ResultBoardHold.ButtonClearance).ToString("0") + "): board "
                + placed.Y.ToString("0") + ".." + placed.Bottom.ToString("0"));
        // Whatever the glyph, the rows that no longer fit are reached: bounded, whole rows, to the last one.
        ScrollViewport view = ResultsScroll.Viewport(Rows, ResultsScroll.FrozenRows, below.Height - metrics.HeaderHeight,
            RowHeight, ResultsScroll.FrozenRows);
        int first = view.First;
        for (int notch = 0; notch < 200 && !view.AtEnd; notch++)
        {
            first = ResultsScroll.Next(first, Rows, ResultsScroll.FrozenRows, view.ScrollSlots, 1f);
            view = view.At(first);
        }
        check(view.AtEnd && view.RowAt(view.Visible - 1) == Rows - 1,
            "and if the lower band is still short, the wheel reaches the table's last row ("
                + fittedVisible + " rows at 12 px, " + view.First + ".." + (view.First + view.Visible - 1) + " at the end)");
    }

    // ---------- a small window ----------

    private static void AShortWindowStillShowsEveryRow(Action<bool, string> check)
    {
        const float scale = 2f / 3f;
        ResultChromeMetrics metrics = ResultChromeMetrics.For(1280f, 720f);
        float rowHeight = ResultsBandFit.RowHeight(ResultAutoBoardContract.TargetGlyphPixels(720f), PanelDensity.Compact);
        check(ResultAutoBoardContract.TargetGlyphPixels(720f) == 8 && Near(rowHeight, 12f),
            "a 720-high window aims at 8 px / 12 px rows");
        var controls = new List<ResultActionBounds>
        {
            new ResultActionBounds(new LayoutRect(SideNotice.X * scale, SideNotice.Y * scale,
                SideNotice.Width * scale, SideNotice.Height * scale)),
            new ResultActionBounds(new LayoutRect(LeaveButton.X * scale, LeaveButton.Y * scale,
                LeaveButton.Width * scale, LeaveButton.Height * scale))
        };
        Check(ResultInteractionStripPolicy.TryResolve(333f * scale, Gap * scale, controls,
            out ResultInteractionStrip strip, Centred(BoardWidth * scale)), check, "the small window resolves a strip");
        Check(ResultsBounds.TryResolve(720f, 333f * scale, ResultBoundSource.IconRow, strip.FooterTop, WaitingText * scale,
            ResultBoundSource.FooterTextControl, strip.FooterTop, out ResultsBand band), check, "and a band");
        int visible = ResultsScroll.Viewport(Rows, ResultsScroll.FrozenRows, band.Height - metrics.HeaderHeight,
            rowHeight, ResultsScroll.FrozenRows).Visible;
        check(visible == Rows,
            "every row still fits the small window: " + visible + " of " + Rows + " at " + rowHeight.ToString("0")
                + " px rows in a " + band.Height.ToString("0") + " px band");
    }

    // ---------- the 4 px claim ----------

    private static void TheFourPixelGlyphCannotHappenInProduction(Action<bool, string> check)
    {
        // F5's report quoted a 4 px glyph for a 190 px band over 30 rows. That is what the *arithmetic* of the band
        // allows, and it is not what the build does: the readability floor is 6 px, `FitBoard` keeps the readable glyph
        // and the plate clips + scrolls, which is exactly what L4812/L4813 of the recording say ("below the 6px this
        // page would have liked; the table keeps the readable glyph ... reached with the wheel"). No font rule changes
        // this round, so this is listed as counter-evidence rather than a fix.
        check(ResultsBandFit.FontForBand(190f, Rows, PanelDensity.Compact, 0) == 4,
            "a 190 px band over 30 rows arithmetically allows a 4 px glyph - which is where the report's figure came from");
        check(ResultsFontGrowth.MinimumReadablePixels == 6,
            "but the build's readable floor is 6 px, and it is not changed this round");
        float floor = ResultsBandFit.FloorForBand(190f, Rows, PanelDensity.Compact,
            ResultsFontGrowth.MinimumReadablePixels, out bool squeezed);
        check(squeezed && floor == 4,
            "`FloorForBand` says the band is squeezed at 4 px - the caller's answer to that is the readable glyph plus"
                + " a clipped plate, not a 4 px table: floor=" + floor);
        check(ResultAutoBoardContract.TargetGlyphPixels(1080f) == 12 && ResultAutoBoardContract.TargetGlyphPixels(1440f) == 16,
            "the automatic target is untouched (12 px at 1080, 16 px at 1440)");
    }

    // ---------- the pages that must not move ----------

    private static void TheFailurePageAndTheFixedModeAreUntouched(Action<bool, string> check)
    {
        string display = ProductionText("ResultsDisplay.cs");
        string layout = ProductionText("ResultFinalLayout.cs");
        check(layout.Contains("public static float DrawnFloor(IReadOnlyList<CompletionPart> parts)"),
            "the drawn floor lives in the production completion bounds, one rule for every caller");
        check(Tally(display, "CompletionFinalBounds.DrawnFloor(") == 1
              && MethodBody(display, "private static bool SuccessRegion(").Contains("CompletionFinalBounds.DrawnFloor("),
            "and only the success region reads it - the failure page has no completion section to floor against");
        check(MethodBody(display, "private static bool FailureStrip(").Contains("LayoutColumn.None")
              || !MethodBody(display, "private static bool FailureStrip(").Contains("BoardColumn("),
            "the failure strip never reads a board column");
        check(MethodBody(display, "private static bool FallbackPlacement(").Contains("succeeded ? BoardColumn() : LayoutColumn.None"),
            "the fail-open band reads it on the success page only (F2), and keeps `LayoutColumn.None` on the failure page");
        check(Tally(display, "BoardColumn()") == 4,
            "one reader and three call sites (success region, fail-open band, fit probe): " + Tally(display, "BoardColumn()"));
        check(MethodBody(display, "private static ResultFixedDrawPlan FixedLayoutDecision(LayoutRect rect, out string evidence)")
                .Contains("ScanFooterControls(_page, rect.Y, MessageGap, waiting, rect, inspectBoard: true)"),
            "the fixed-mode scan is byte for byte the call it was - no column, no drawn floor");
        check(display.Contains("succeeded && !measuredColumn.Known && grid != null && grid.Width > 0f && _columnProbes < MaximumColumnProbes"),
            "F2: the two-phase probe is bounded per page and only runs while the column is unknown");
        // 1.19.59: the width is no longer re-aligned by a second search after the plate was judged - `TryFitHeld` places the
        // candidate at the *current* grid's width through the same bottom-aligned search, so a wider table is either placed
        // inside this frame's strip at its own width or refused.
        string held = MethodBody(display, "private static bool TryFitHeld(PanelGrid grid, float room, float header, BoardStrip strip,");
        check(held.Contains("ResultBoardHold.TryPlace(Screen.width, Screen.height, strip, grid.Width, total,")
              && held.Contains("ResultChromeLayout.PlateInsideBand(candidate, strip.Top, strip.Bottom, Screen.height, BoardSlack)"),
            "F4: the candidate plate is placed at the refreshed width and must sit inside this frame's band before it is committed");
    }

    // ---------- F8 ----------

    private static void F8IsAnsweredByTheSameBandAndTheSamePlacement(Action<bool, string> check)
    {
        string success = MethodBody(ProductionText("ResultsDisplay.cs"), "private static bool SuccessRegion(");
        check(!success.Contains("Forced"),
            "F8 does not change a single figure of the band: the forced press is answered by the same measured strip,"
                + " the same drawn floor and the same placement - it only skips the reveal gate");
        check(!success.Contains("FixedFontPixels") && !success.Contains("SuccessFontScale"),
            "and nothing in the band or the placement reads a configured font size or scale - this round changes no"
                + " config value and no scoring rule");
    }

    // ---------- the version ----------

    private static void TheVersionCarrierIsThisFix(Action<bool, string> check)
    {
        string info = ProductionText("PluginInfo.cs");
        const string carrier = "internal const string Version = \"";
        int at = info.IndexOf(carrier, StringComparison.Ordinal);
        string version = at < 0 ? string.Empty
            : info.Substring(at + carrier.Length, info.IndexOf('"', at + carrier.Length) - at - carrier.Length);
        check(version.Length > 0 && char.IsDigit(version[0]) && version != "1.19.51", "this fix's carrier is its own patch number, not the 1.19.51 it was cut from: " + version);
        check(ProductionText(Path.Combine("Thunderstore", "manifest.json")).Contains("\"version_number\": \"" + version + "\""),
            "the manifest carries it");
        check(ProductionText(Path.Combine("Thunderstore", "CHANGELOG.md")).Contains("## " + version),
            "the changelog has this build's own section, ahead of 1.19.51's");
        check(ProductionText("README.md").Contains("源码版本为 **" + version + "**"),
            "and the README's current state names it");
    }

    // ---------- helpers ----------

    private static CompletionPart Part(bool present, bool active, bool scheduled, float own, float visual, bool measured,
        int hidden, int unmeasured, int drawn, float alpha)
        => new CompletionPart(present: present, active: active, alpha: alpha, ownBottom: own, visualBottom: visual,
            scheduled: scheduled, measured: measured, hidden: hidden, unmeasured: unmeasured, drawn: drawn);

    // The board's own column: what `ResultBoardHold.TryPlace` produces for a table of this width, centred in the window.
    private static LayoutColumn Centred(float width, float screenWidth = Width)
    {
        float x = (screenWidth - width) * 0.5f;
        return new LayoutColumn(x, x + width);
    }

    // `FitBoard`'s answer for a band, on a table whose row pitch is the compact one and whose width is the recorded one
    // at 12 px: the largest glyph between the readable floor and the window's target that fits the band, and the plate
    // the rows it produces really occupy.
    private static int FitForBand(float room, float header, out int visible, out float plate)
    {
        int target = ResultAutoBoardContract.TargetGlyphPixels(Height);
        float bandRoom = room - header;
        int floor = ResultsBandFit.FloorForBand(room, Rows, PanelDensity.Compact, ResultsFontGrowth.MinimumReadablePixels,
            out _);
        float box = Math.Max(1f, room - header);
        int fitted = ResultsFontGrowth.FitBoardIntoStrip(17, Math.Max(floor, ResultsFontGrowth.MinimumReadablePixels),
            int.MaxValue, box, Width - 48f, size => Table(size), out int usedFloor);
        if (fitted > target) fitted = target;
        int glyph = Math.Max(ResultsBandFit.AbsoluteMinimumPixels, Math.Min(usedFloor, fitted));
        visible = ResultsScroll.Viewport(Rows, ResultsScroll.FrozenRows, bandRoom,
            ResultsBandFit.RowHeight(glyph, PanelDensity.Compact), ResultsScroll.FrozenRows).Visible;
        plate = Math.Min(Rows * ResultsBandFit.RowHeight(glyph, PanelDensity.Compact),
            ResultsScroll.PlateHeight(visible, ResultsBandFit.RowHeight(glyph, PanelDensity.Compact), bandRoom));
        return fitted;
    }

    private static ResultTableSize Table(int fontPixels)
    {
        if (fontPixels < 1) return ResultTableSize.None;
        float row = ResultsBandFit.RowHeight(fontPixels, PanelDensity.Compact);
        return new ResultTableSize(BoardWidth * (fontPixels / 12f), Rows * row);
    }

    private static bool Near(float left, float right) => Math.Abs(left - right) <= 0.51f;

    private static void Check(bool condition, Action<bool, string> check, string description)
        => check(condition, description);

    private static int Tally(string text, string needle)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(needle)) return 0;
        int count = 0, at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
        return count;
    }

    private static string MethodBody(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        if (at < 0) return string.Empty;
        int open = source.IndexOf('{', at);
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

    private static string ProductionText(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj")))
            directory = directory.Parent;
        if (directory == null) throw new InvalidOperationException("the production source root was not found");
        return File.ReadAllText(Path.Combine(directory.FullName, name));
    }
}
