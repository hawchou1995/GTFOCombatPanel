using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// ---------------------------------------------------------------------------------------------------
// Release11953Scenarios.cs - the last open item of the 1.19.51 review
// ---------------------------------------------------------------------------------------------------
//
// 1.19.52 raised `completion` to the drawn floor (333) but the *band* was still resolved from `IconRowBottom()`, i.e.
// from the laid-out container (233): the region line said 349, the pre-paint check demanded 349, and the board was
// placed from 249 - so the check refused it on every repaint. 1.19.53 makes both read one pure production decision
// (`ResultSuccessBandAnchor.Resolve`) and asks the pre-paint check about the real plate (`board.Y`, header included).
//
// This file replays the recorded page from its *raw* readings - own198/visual333/drawn6/hidden14 for the icon part and
// own233 for the header part - through the same production functions the runtime calls, in the same order:
//
//   CompletionFinalBounds.Placed / DrawnFloor  ->  ResultSuccessBandAnchor.Resolve  ->  ResultsBounds.TryResolve
//     ->  ResultBoardHold.TryPlace (bottom-aligned, BoardSlack)  ->  the pre-paint gate  ->  ResultsScroll rows
//
// 233 / 333 / 349 appear in this file only as *expected* values; no computation is fed them.
internal static class Release11953Scenarios
{
    private const float Width = 1920f, Height = 1080f;
    private const float Gap = 16f;
    private const float BoardSlack = 2f;          // ResultsDisplay.BoardSlack
    private const int Rows = 30;
    private const float GridWidth = 330f;         // the recorded grid (1 player, 12 px glyph)
    private static readonly LayoutRect LeaveButton = new LayoutRect(752f, 973f, 440f, 80f);
    private const float WaitingText = 982f;
    private static readonly LayoutRect SideNotice = new LayoutRect(217f, 455f, 503f, 60f);

    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        TheBandHangsFromTheDrawnFloorAndNotFromTheContainer(check);
        ThePrePaintGateAsksAboutThePlateNotTheBand(check);
        AShortBandShowsEveryRowAtTheLargestGlyphThatFits(check);
        TheColumnIsTheMeasuredWidth(check);
        TheVersionCarrierIsThisFix(check);
    }

    // ---------- F1: one anchor, from the page's own raw readings ----------

    private static void TheBandHangsFromTheDrawnFloorAndNotFromTheContainer(Action<bool, string> check)
    {
        // The recorded readings, exactly as the log printed them (L4811).
        var parts = new[]
        {
            Part(own: 233f, visual: 233f, hidden: 0, unmeasured: 0, drawn: 5),   // sectorHeader
            Part(own: 198f, visual: 333f, hidden: 14, unmeasured: 0, drawn: 6)   // sectorIconMain
        };
        float layoutEdge = CompletionFinalBounds.Placed(parts, revealFinished: true, contentSettled: false, out _, out _);
        float drawnFloor = CompletionFinalBounds.DrawnFloor(parts);
        check(Near(layoutEdge, 233f) && Near(drawnFloor, 333f),
            "the section's laid-out edge is 233 and the pixels it has really painted reach 333 ("
                + layoutEdge.ToString("0") + " / " + drawnFloor.ToString("0") + ")");

        // The *old* wiring: the band used the laid-out edge alone *and* the scan's lowest wall was the interaction at
        // 455 (no column in 1.19.50). This is the red half - a build that reuses 233 gets a 249 band while the same
        // frame knows the floor is 333.
        var controls = new List<ResultActionBounds> { new ResultActionBounds(SideNotice), new ResultActionBounds(LeaveButton) };
        Check(ResultInteractionStripPolicy.TryResolve(layoutEdge, Gap, controls, out ResultInteractionStrip oldStrip), check,
            "the old scan resolves a strip with no column at all");
        Check(ResultsBounds.TryResolve(Height, layoutEdge, ResultBoundSource.IconRow, oldStrip.FooterTop, WaitingText,
            ResultBoundSource.FooterTextControl, oldStrip.FooterTop, out ResultsBand oldBand), check,
            "the old anchor still resolves a band");
        check(Near(oldStrip.FooterTop, 455f) && Near(oldBand.Top, 249f) && Near(oldBand.Bottom, 439f),
            "and that band is 249..439 - it starts above the captions the page painted to 333 and ends at the "
                + "interaction at " + oldStrip.FooterTop.ToString("0"));

        // The production decision, from the same raw readings.
        check(ResultSuccessBandAnchor.Resolve(true, layoutEdge, drawnFloor, out float anchor, out bool fromDrawn)
              && fromDrawn,
            "the production anchor takes the drawn floor when the icon row was measured this frame");
        check(Near(anchor, 333f),
            "so the band hangs from 333, not 233: " + anchor.ToString("0"));
        Check(ResultsBounds.TryResolve(Height, anchor, ResultBoundSource.IconRow, LeaveButton.Y, WaitingText,
            ResultBoundSource.FooterTextControl, LeaveButton.Y, out ResultsBand band), check, "and the band resolves");
        check(Near(band.Top, 349f) && Near(band.Bottom, 957f) && band.Height > oldBand.Height + 300f,
            "the band is 349..957 = " + band.Height.ToString("0") + " px against the old "
                + oldBand.Height.ToString("0") + " px - the one number the fix is about");

        // A row that was *not* measured this frame must not be passed off as a current measurement: the caller keeps the
        // last-trusted reading it had, and the drawn floor alone never invents an anchor.
        check(!ResultSuccessBandAnchor.Resolve(false, float.NaN, drawnFloor, out float unknown, out _)
              && float.IsNaN(unknown),
            "an unmeasured row answers no anchor at all, even with a drawn floor in hand");
        check(!ResultSuccessBandAnchor.Resolve(false, 233f, drawnFloor, out float stale, out _)
              && Near(stale, 233f),
            "and a stale row is not combined with this frame's pixels - the caller keeps its own last-trusted figure");
        check(ResultSuccessBandAnchor.Resolve(true, 400f, float.NaN, out float noFloor, out bool drewNothing)
              && Near(noFloor, 400f) && !drewNothing,
            "a NaN drawn floor contributes nothing (the row's own edge stands)");
        check(ResultSuccessBandAnchor.Resolve(true, 400f, 380f, out float lower, out _) && Near(lower, 400f),
            "and the answer never rises *above* the row's own edge on the floor's account");

        // The production region reads that decision, and no longer resolves the band from `iconRow` alone.
        string success = MethodBody(ProductionText("ResultsDisplay.cs"), "private static bool SuccessRegion(");
        check(success.Contains("ResultSuccessBandAnchor.Resolve(iconRowCurrent, iconRow, _drawnFloorCompletion")
              && success.Contains("ResultsBounds.TryResolve(Screen.height, bandAnchor, bandAnchorSource,"),
            "the success region resolves its band from the combined anchor, not from `float.IsFinite(iconRow) ? iconRow : ...`");
        check(!success.Contains("float bandAnchor = float.IsFinite(iconRow) ? iconRow : _iconRowBottom;"),
            "the 1.19.52 wiring that reused the container edge is gone");
    }

    // ---------- F1: the pre-paint gate about the real plate ----------

    private static void ThePrePaintGateAsksAboutThePlateNotTheBand(Action<bool, string> check)
    {
        ResultChromeMetrics metrics = ResultChromeMetrics.For(Width, Height);
        // The plate each band really holds: the old 190 px strip clipped it to eight rows (186 px with the header), the
        // fixed 608 px strip holds all thirty (582 px).
        float oldPlate = Math.Min(Rows * 18f, ResultsScroll.PlateHeight(
            ResultsScroll.Viewport(Rows, ResultsScroll.FrozenRows, 190f - metrics.HeaderHeight, 18f,
                ResultsScroll.FrozenRows).Visible, 18f, 190f - metrics.HeaderHeight));
        float newPlate = Math.Min(Rows * 18f, ResultsScroll.PlateHeight(
            ResultsScroll.Viewport(Rows, ResultsScroll.FrozenRows, 608f - metrics.HeaderHeight, 18f,
                ResultsScroll.FrozenRows).Visible, 18f, 608f - metrics.HeaderHeight));

        // The board the old 249 band would have produced, and the one the fixed band produces.
        Check(ResultBoardHold.TryPlace(Width, Height, new BoardStrip(249f, 439f, true, LeaveButton.Y, 80f), GridWidth,
            oldPlate + metrics.HeaderHeight, 0f, out LayoutRect covering, BoardSlack, bottomAligned: true), check,
            "the old band places the eight-row board it really held (" + oldPlate.ToString("0") + " px + header)");
        float drawnFloor = 333f;
        check(covering.Y < drawnFloor + Gap - 0.5f,
            "and that plate starts at " + covering.Y.ToString("0") + ", inside the captions (" + (drawnFloor + Gap).ToString("0")
                + ") - which is what the gate must refuse");

        Check(ResultBoardHold.TryPlace(Width, Height, new BoardStrip(349f, 957f, true, LeaveButton.Y, 80f), GridWidth,
            newPlate + metrics.HeaderHeight, 0f, out LayoutRect clear, BoardSlack, bottomAligned: true), check,
            "the fixed band places the whole board");
        check(clear.Y >= drawnFloor + Gap - 0.5f,
            "and this one starts at " + clear.Y.ToString("0") + ", clear of the captions - so the gate keeps it");

        string display = ProductionText("ResultsDisplay.cs");
        // 1.19.55 (D): the same gate about the same plate and the same `MessageGap`, with one stated exemption - an early
        // board was placed from the section's laid-out slot while the reveal was still drawing, so the live drawn floor
        // walks down under it and must not take it away (that is the "drawn first, moved after" defect this gate exists
        // to prevent on the mature path). The mature placement is judged by this line exactly as before.
        check(display.Contains("if (!_earlyBoard && float.IsFinite(_drawnFloorCompletion) && board.Y < _drawnFloorCompletion + MessageGap - 0.5f)"),
            "the gate asks about `board.Y` - the plate, header included - and the same MessageGap the placement leaves, and only the early board is exempt (1.19.55)");
        check(!display.Contains("_holdStrip.Top < _drawnFloorCompletion"),
            "it no longer judges the strip's top edge as a proxy for the board");
    }

    // ---------- the header is reserved in the fit's box ----------

    private static void AShortBandShowsEveryRowAtTheLargestGlyphThatFits(Action<bool, string> check)
    {
        ResultChromeMetrics metrics = ResultChromeMetrics.For(Width, Height);
        const float lowerBand = 426f;                                   // the free band under the recorded interaction
        int target = ResultAutoBoardContract.TargetGlyphPixels(Height);

        // 1.19.54: `FitBoard` 收到的是**正文高度**，标题由调用方恰好扣一次。把整条带当正文（1.19.50/1.19.52 的失败页与
        // fail-open 实参语义）会答出 9 px，而它的板面把表裁到 28 行；这就是“少扣一次标题”的代价，也是成功页必须补扣的理由。
        float fullRoom = FitGlyph(lowerBand, lowerBand, out int fullVisible, out float fullPlate);
        check(Math.Abs(fullRoom - 9f) < 0.5f && fullVisible < Rows,
            "a fit given the whole band as if it were body height answers " + fullRoom.ToString("0") + " px, and its plate clips the table to "
                + fullVisible + " of " + Rows + " rows");

        float tableRoom = Math.Max(1f, lowerBand - metrics.HeaderHeight);   // the caller's `room - header`, exactly as `TryFit` does
        float glyph = FitGlyph(tableRoom, lowerBand, out int visible, out float tableHeight);
        check(glyph <= target && glyph >= ResultsFontGrowth.MinimumReadablePixels,
            "reserving the header first answers " + glyph.ToString("0") + " px, still between the readable floor and the"
                + " window's target");
        check(visible == Rows,
            "and that glyph shows every row, not a clipped table: " + visible + " of " + Rows);
        Check(ResultBoardHold.TryPlace(Width, Height, new BoardStrip(531f, 957f, true, LeaveButton.Y, 80f), GridWidth,
            tableHeight + metrics.HeaderHeight, 0f, out LayoutRect board, BoardSlack, bottomAligned: true), check,
            "the placement accepts the whole board (" + tableHeight.ToString("0") + " px table + "
                + metrics.HeaderHeight.ToString("0") + " px header in a 426 px band)");
        check(board.Y >= 531f - 0.5f && board.Bottom <= LeaveButton.Y - ResultBoardHold.ButtonClearance + 0.5f,
            "inside that band, clear of the interaction above it and of the button below: "
                + board.Y.ToString("0") + ".." + board.Bottom.ToString("0"));

        // A band that genuinely cannot hold the table at the readable floor keeps its rows on the wheel - the header
        // reservation must not become a licence to shrink below the floor.
        FitGlyph(90f - metrics.HeaderHeight, 90f, out int tinyVisible, out _);
        check(tinyVisible < Rows && tinyVisible >= ResultsScroll.MinimumVisibleRows,
            "a 90 px band still cannot show the whole table (" + tinyVisible + " rows), and nothing pretends it can");
    }

    // ---------- F6 ----------

    private static void TheColumnIsTheMeasuredWidth(Action<bool, string> check)
    {
        string display = ProductionText("ResultsDisplay.cs");
        check(!display.Contains("Math.Min(_widthProbe, Math.Max(1f, Screen.width - 48f))"),
            "the column no longer clamps the measured width to `screen - 48`, a box the placement never used");
        check(display.Contains("float width = _widthProbe;")
              && display.Contains("x = Math.Max(0f, Math.Min(x, Screen.width - width));"),
            "it uses the measured width with the placement's own centring and the same clamp");
        check(display.Contains("ResultSuccessBandAnchor.Resolve(iconRowCurrent, iconRow, _drawnFloorCompletion")
              && Tally(display, "ResultSuccessBandAnchor.Resolve(") == 1,
            "one decision, one reader");
    }

    // ---------- the version ----------

    private static void TheVersionCarrierIsThisFix(Action<bool, string> check)
    {
        string info = ProductionText("PluginInfo.cs");
        const string carrier = "internal const string Version = \"";
        int at = info.IndexOf(carrier, StringComparison.Ordinal);
        string version = at < 0 ? string.Empty
            : info.Substring(at + carrier.Length, info.IndexOf('"', at + carrier.Length) - at - carrier.Length);
        check(version.Length > 0 && char.IsDigit(version[0]) && version != "1.19.52", "this fix's carrier is its own patch number, not the 1.19.52 it was cut from: " + version);
        check(ProductionText(Path.Combine("Thunderstore", "manifest.json")).Contains("\"version_number\": \"" + version + "\""),
            "the manifest carries it");
        check(ProductionText(Path.Combine("Thunderstore", "CHANGELOG.md")).Contains("## " + version),
            "the changelog has this build's own section, ahead of 1.19.52's");
        check(ProductionText("README.md").Contains("源码版本为 **" + version + "**"),
            "and the README's current state names it");
    }

    // ---------- helpers ----------

    private static CompletionPart Part(float own, float visual, int hidden, int unmeasured, int drawn)
        => new CompletionPart(present: true, active: true, alpha: 1f, ownBottom: own, visualBottom: visual,
            scheduled: false, measured: true, hidden: hidden, unmeasured: unmeasured, drawn: drawn);

    // `FitBoard`'s search on a box, with the same table content the recording describes: 30 rows, the compact row pitch,
    // and a natural width that grows with the glyph (330 px at 12 px).
    private static float FitGlyph(float box, float band, out int visible, out float tableHeight)
    {
        int floor = ResultsBandFit.FloorForBand(box, Rows, PanelDensity.Compact, ResultsFontGrowth.MinimumReadablePixels,
            out _);
        int fitted = ResultsFontGrowth.FitBoardIntoStrip(17, Math.Max(floor, ResultsFontGrowth.MinimumReadablePixels),
            int.MaxValue, box, Width - 48f, size => Table(size), out int usedFloor);
        int target = ResultAutoBoardContract.TargetGlyphPixels(Height);
        if (fitted > target) fitted = target;
        // The glyph the runtime draws: `ResultsScroll.FontFor(fitted, scale)` with no player scale, floored by the band's
        // own answer - `floor = min(bandFloor, fontSize)`. `usedFloor` is that band answer.
        int glyph = Math.Max(ResultsBandFit.AbsoluteMinimumPixels, Math.Min(usedFloor <= 0 ? fitted : usedFloor, fitted));
        if (usedFloor >= ResultsFontGrowth.MinimumReadablePixels && fitted >= usedFloor) glyph = fitted;
        float row = ResultsBandFit.RowHeight(glyph, PanelDensity.Compact);
        visible = ResultsScroll.Viewport(Rows, ResultsScroll.FrozenRows, Math.Max(1f, band - 42f), row,
            ResultsScroll.FrozenRows).Visible;
        tableHeight = Math.Min(Rows * row, ResultsScroll.PlateHeight(visible, row, Math.Max(1f, band - 42f)));
        return fitted;
    }

    private static ResultTableSize Table(int fontPixels)
    {
        if (fontPixels < 1) return ResultTableSize.None;
        float row = ResultsBandFit.RowHeight(fontPixels, PanelDensity.Compact);
        return new ResultTableSize(GridWidth * (fontPixels / 12f), Rows * row);
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
