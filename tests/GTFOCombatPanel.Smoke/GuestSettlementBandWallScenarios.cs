using System;
using HawChow.GTFOCombatPanel;

// Two review questions about the client settlement band, answered against the production rules the runtime calls:
//
//  1. what the *default* comfort ceiling answers, so the file a build ships can be compared with the file in the tree. The
//     figures here were measured by compiling all three real `LayoutPolicy.cs` revisions in a scratch project (the pre-20:25
//     file, the 20:25 half-finished write, and the current one) and calling `ResultsFontGrowth` in each of them;
//  2. which rule protects the waiting-status line when the wall moves *up* under a board that already fills its band, and
//     which rule protects it when the wall appears again after being unreadable.
//
// Nothing here runs Unity: `HudTable.Draw`, IMGUI and the native page are not reachable from this project, so no assertion
// below counts draw calls or reads a pixel back. Every figure is a production type's own answer.

internal static class GuestSettlementBandWallScenarios
{
    private const int Seed = 17;        // FontSizeForSharing on the recorded page (config FontSize=17)
    private const int Floor = 6;        // ResultsFontGrowth.MinimumReadablePixels
    private const float BoardWidth = 2512f; // Mathf.Max(320f, 2560 - 48)
    private const float ScreenWidth = 2560f, ScreenHeight = 1440f;
    private const float MessageGap = 16f;   // ResultsDisplay.MessageGap, pinned in GuestSettlementBandScenarios

    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        TheComfortCeilingAnswersWhatItAlwaysDid(check);
        AWallThatMovesUpIsCaughtBeforeItIsCovered(check);
        AWallThatReappearsIsMeasuredAgainNotCovered(check);
    }

    // The comfort ceiling answers exactly what the file that shipped answered.
    //
    // The 20:25 write added both the `fillCeiling` parameter and an `if (Filled(...)) break;` to `FitIntoStrip`'s bisection.
    // That break is a stop at 92% of the box, so it changed the default 0.96 answer itself: 800px of a 12-row table went from
    // 42px to 41px, and 1600px of the 30-row settlement board from 34px to 33px. Measured against all three revisions, the
    // figures below are the *pre-20:25* answers - the ones every installed build has. The break is gone because it was never
    // in a shipped byte, not because a number was inconvenient.
    private static void TheComfortCeilingAnswersWhatItAlwaysDid(Action<bool, string> check)
    {
        int twelveRows = ResultsFontGrowth.FitIntoStrip(Seed, Floor, int.MaxValue, 800f, BoardWidth, Table(12, 1.5f, 40f));
        check(twelveRows == 42,
            $"the 800px strip of a 12-row table still grows to {twelveRows}px - the figure `ResultsGrowthScenarios` has pinned since 1.19.7");
        int thirtyRows = ResultsFontGrowth.FitIntoStrip(Seed, Floor, int.MaxValue, 1600f, BoardWidth, Table(30, 1.5f, 40f));
        check(thirtyRows == 34,
            $"a 1600px strip of the 30-row board still answers {thirtyRows}px (1530px of it) - the pre-20:25 figure; the half-finished write answered 33px through its early stop, and no shipped build ever had that");
        int shortRoom = ResultsFontGrowth.FitIntoStrip(Seed, Floor, int.MaxValue, 800f, BoardWidth, Table(30, 1.5f, 40f));
        check(shortRoom == 17,
            $"a 30-row board in 800px is answered by the strip itself ({shortRoom}px = 765px, the largest that fits 768.5px), not by the comfort band");
        check(ResultsFontGrowth.FitIntoStrip(Seed, Floor, int.MaxValue, 800f, BoardWidth, Table(12, 1.5f, 40f),
                  ResultsFontGrowth.BandFillCeiling) == 44
              && ResultsFontGrowth.FitIntoStrip(Seed, Floor, int.MaxValue, 1600f, BoardWidth, Table(30, 1.5f, 40f),
                  ResultsFontGrowth.BandFillCeiling) == 35,
            "the whole-band ceiling only ever answers the next fitting step up (44px and 35px), never a table outside its box");
        // The same question through the call the renderer really makes.
        check(ResultsFontGrowth.FitBoardIntoStrip(Seed, Floor, int.MaxValue, 540f, BoardWidth, Table(30, 1.5f, 40f), out _) == 11
              && ResultsFontGrowth.FitBoardIntoStrip(Seed, Floor, int.MaxValue, 1600f, BoardWidth, Table(30, 1.5f, 40f), out _) == 34,
            "and `FitBoardIntoStrip` with its default ceiling answers 11px in the 540px fixed strip and 34px in 1600px, as the pre-20:25 file did");
    }

    // A wall that moves *up* under a board that already fills its band. Nothing here assumes the glyph size stays put: the
    // re-fit is free to answer a smaller table, and every assertion is about the two edges and about not covering the line.
    private static void AWallThatMovesUpIsCaughtBeforeItIsCovered(Action<bool, string> check)
    {
        float clearance = ResultsBounds.Clearance(ScreenHeight);
        int rows = ImpactBoard.RowCount(true);
        float ceiling = ResultsFontGrowth.FillCeiling(ResultFooterSource.ClientNoFooterSafeArea, footIsMeasuredWall: true);
        int floor = ResultsBandFit.FloorForBand(316.333f, rows, PanelDensity.Compact,
            ResultsFontGrowth.ReadabilityFloor(float.NaN), out _);
        int font = ResultsFontGrowth.FitBoardIntoStrip(Seed, floor, int.MaxValue, 316.333f, BoardWidth, Real(rows), out _, ceiling);
        ResultTableSize standing = Real(rows)(font);
        var band = new BoardStrip(889.333f, 1205.667f, false, float.NaN, float.NaN, true,
            ResultFooterSource.ClientNoFooterSafeArea, ceiling);
        check(ResultBoardHold.TryPlace(ScreenWidth, ScreenHeight, band, standing.Width, standing.Height, 0f, out LayoutRect placed),
            "the recorded band's board stands where the placement put it");

        // The waiting line animates 17px *up*, from 1227 to 1210 - the direction a full band has no room for.
        ResultsBounds.TryResolve(ScreenHeight, 868f, ResultBoundSource.TitleAnchor, float.NaN, 1210f, float.NaN,
            out ResultsBand moved);
        check(near(moved.Top, 889.333f, 0.01f) && near(moved.Bottom, 1210f - clearance, 0.01f) && near(moved.Height, 299.333f, 0.02f),
            $"a wall at 1210 pulls the band's foot up to {moved.Bottom:0.##} (room {moved.Height:0.##}px) and leaves its top alone");

        var line = new LayoutRect(0f, 1210f, ScreenWidth, 40f);
        check(!ResultInteractionStripPolicy.OverlapsBoard(line, placed, 0f),
            $"the standing board's foot ({placed.Bottom:0.##}) is still above the line itself, so no pixel of the line is covered on this frame");
        check(ResultInteractionStripPolicy.OverlapsBoard(line, placed, MessageGap),
            "but it is inside the design clearance the scan keeps for a protected interaction, so the repaint's own `OverlapsBoard` refuses the board before it is drawn - that is the safety net here");

        // The drift rule is deliberately not what answers this: 17px is under its threshold, so nothing is re-measured on a
        // guess and no budget is spent on the page's own animation.
        var drift = new ResultAnchorDrift();
        drift.Record(0, 889.333f, 316.333f);
        check(!ResultAnchorDrift.Moved(moved.Height, 316.333f)
              && !drift.Due(1, 889.333f, moved.Height) && !drift.Due(2, 889.333f, moved.Height),
            "a 17px move is under `ResultAnchorDrift`'s max(12px, 8%) threshold, so the drift rule does not answer it at all");
        check(drift.Recoveries == 0,
            "and none of the page's re-measure budget is spent on it - the per-repaint refusal costs nothing");

        // What the page is fitted with once the standing board is refused: the smaller room, measured, not guessed.
        int refitFloor = ResultsBandFit.FloorForBand(moved.Height, rows, PanelDensity.Compact,
            ResultsFontGrowth.ReadabilityFloor(float.NaN), out _);
        int refitFont = ResultsFontGrowth.FitBoardIntoStrip(Seed, refitFloor, int.MaxValue, moved.Height, BoardWidth, Real(rows), out _, ceiling);
        ResultTableSize refit = Real(rows)(refitFont);
        var refitBand = new BoardStrip(moved.Top, moved.Bottom, false, float.NaN, float.NaN, true,
            ResultFooterSource.ClientNoFooterSafeArea, ceiling);
        check(refit.Height <= moved.Height + 0.5f,
            $"the re-fit stays inside the smaller band ({refit.Height:0}px of {moved.Height:0.##}px), whatever glyph size that costs");
        check(ResultBoardHold.TryPlace(ScreenWidth, ScreenHeight, refitBand, refit.Width, refit.Height, 0f, out LayoutRect refitted),
            "and the placement accepts it");
        check(refitted.Bottom < 1210f - MessageGap,
            $"the re-fitted board's foot ({refitted.Bottom:0.##}) clears the line at 1210 by more than the design clearance, so nothing is covered and nothing needs fading");
        check(!ResultInteractionStripPolicy.OverlapsBoard(line, refitted, MessageGap),
            "the per-repaint check that refused the old board passes on the new one, so the deferral cannot loop");
        check(ResultBoardHold.HeldGridFits(band, refitBand, refitted, refit.Height, ScreenHeight, 2f),
            "and the held-grid contract holds it too, so a refreshed snapshot may keep this rectangle");
        // Which rule really protects the line, stated as a fact rather than assumed: the held-grid contract compares against the
        // strip the board was *measured* into, and that foot has not moved.
        check(ResultBoardHold.HeldGridFits(band, refitBand, placed, standing.Height, ScreenHeight, 2f),
            "the held-grid contract alone would still accept the old board - its measured strip's foot is unchanged - so the interaction scan, not the strip, is what stops the line being covered");
    }

    // The other direction: the wall was unreadable, the band fell back to the fixed page geometry, and the waiting line then
    // appears inside the board that was placed without it.
    private static void AWallThatReappearsIsMeasuredAgainNotCovered(Action<bool, string> check)
    {
        int rows = ImpactBoard.RowCount(true);
        int comfortFloor = ResultsBandFit.FloorForBand(540f, rows, PanelDensity.Compact,
            ResultsFontGrowth.ReadabilityFloor(float.NaN), out _);
        int comfortFont = ResultsFontGrowth.FitBoardIntoStrip(Seed, comfortFloor, int.MaxValue, 540f, BoardWidth,
            Real(rows), out _, ResultsFontGrowth.StripFillCeiling);
        ResultTableSize wide = Real(rows)(comfortFont);
        var wideBand = new BoardStrip(889.333f, 1429.333f, false, float.NaN, float.NaN, true,
            ResultFooterSource.ClientNoFooterSafeArea, ResultsFontGrowth.StripFillCeiling);
        check(ResultBoardHold.TryPlace(ScreenWidth, ScreenHeight, wideBand, wide.Width, wide.Height, 0f, out LayoutRect widePlaced),
            "a page whose lower wall could not be read is placed inside the fixed page geometry, as before");

        var line = new LayoutRect(0f, 1227f, ScreenWidth, 40f);
        check(ResultInteractionStripPolicy.OverlapsBoard(line, widePlaced, 0f),
            $"the line appearing at 1227 falls inside that board ({widePlaced.Y:0.##}..{widePlaced.Bottom:0.##}), so drawing it would cover the line - the deferral is mandatory here, not cosmetic");

        var drift = new ResultAnchorDrift();
        drift.Record(0, 889.333f, 540f);
        check(!drift.Due(1, 889.333f, 316.333f) && drift.Due(2, 889.333f, 316.333f),
            "the band shrinking from 540px to 316.33px is past the drift threshold, so the page is measured again on the second sample");
        drift.Spent();
        check(drift.Recoveries == 1,
            "and that re-measure is spent from the page's bounded budget");

        float ceiling = ResultsFontGrowth.FillCeiling(ResultFooterSource.ClientNoFooterSafeArea, footIsMeasuredWall: true);
        int floor = ResultsBandFit.FloorForBand(316.333f, rows, PanelDensity.Compact,
            ResultsFontGrowth.ReadabilityFloor(float.NaN), out _);
        int font = ResultsFontGrowth.FitBoardIntoStrip(Seed, floor, int.MaxValue, 316.333f, BoardWidth, Real(rows), out _, ceiling);
        ResultTableSize fitted = Real(rows)(font);
        var band = new BoardStrip(889.333f, 1205.667f, false, float.NaN, float.NaN, true,
            ResultFooterSource.ClientNoFooterSafeArea, ceiling);
        check(ResultBoardHold.TryPlace(ScreenWidth, ScreenHeight, band, fitted.Width, fitted.Height, 0f, out LayoutRect placed),
            "the re-fit lands in the band the line really offers");
        check(placed.Bottom < 1227f - MessageGap && !ResultInteractionStripPolicy.OverlapsBoard(line, placed, MessageGap),
            $"and its foot ({placed.Bottom:0.##}) clears the line by more than the design clearance, so the line is never covered");
        check(fitted.Height <= 316.333f + 0.5f,
            $"whatever size the re-fit answered ({fitted.Height:0}px), it is inside the band");
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    // `ResultsGrowthScenarios`' own stand-in for a table: both extents grow with the glyph size exactly as
    // `PanelGeometry.Build` does.
    private static Func<int, ResultTableSize> Table(int rows, float rowEm, float emWidth)
        => size => new ResultTableSize(emWidth * size, rows * rowEm * size);

    // `ResultsDisplay.NaturalSize`, headless: the settlement columns the renderer builds, through `PanelGeometry.Build`.
    private static Func<int, ResultTableSize> Real(int rows)
        => size =>
        {
            var columns = new[] { PanelColumn.ImpactLabel, PanelColumn.ImpactValue };
            var measured = new[]
            {
                PanelGeometry.EstimateTextWidth("TOTAL", size),
                PanelGeometry.EstimateTextWidth("#1 1,234.5 42.1%", size)
            };
            float[] widths = PanelGeometry.SettlementColumnWidths(columns, measured, size);
            var metrics = new[]
            {
                new PanelColumnMetrics(new[] { widths[0] }),
                new PanelColumnMetrics(new[] { widths[1] })
            };
            PanelGrid grid = PanelGeometry.Build(metrics, PanelGeometry.FieldGap(size, PanelDensity.Compact), size, rows,
                PanelDensity.Compact, 1f, new[] { false, true });
            return grid == null ? ResultTableSize.None : new ResultTableSize(grid.Width, grid.Height);
        };

    private static bool near(float a, float b, float tolerance) => Math.Abs(a - b) <= tolerance;
}
