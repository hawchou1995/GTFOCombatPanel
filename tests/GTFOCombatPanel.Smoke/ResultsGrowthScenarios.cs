using System;

using HawChow.GTFOCombatPanel;

// The success board is drawn in the strip between the page's completion section and the button that
// sends the squad home, and it was the wrong size in exactly the way a guess is wrong: it was laid out
// at the in-level sharing size and then multiplied by 2.5, so on a 2560x1440 page a 2512px wide, ~940px
// tall strip held a ~600x370 table with most of the room unused. What replaced the guess is a search:
// the table is measured at a candidate glyph size, the strip's height and the page's width say whether
// that size uses the room, and the largest size that does is the one drawn.
//
// Everything asserted here runs against `ResultsFontGrowth.FitResultFontSize` and the column-width rule in
// `PanelGeometry`, both of which are pure arithmetic - no Mathf, no Screen, no texture - so the whole of
// the decision is pinned headlessly, on measurements this file makes itself. `Run()` counts its own
// failures; `RunInto(Check)` feeds the same assertions to the smoke suite's own checker.
internal static class ResultsGrowthScenarios
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

    // The smoke suite's shape: one boolean per property, with the number that makes it true in the text.
    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));

        // ---- a strip with room: the glyph grows until the table is inside the 92-96% band ----------
        //
        // 12 rows at 1.5 em per row is 18 em of table height, and the table is 40 em wide. At the
        // sharing size of 15px that is 600x270 in a strip 800px tall and a page 2500px wide, which is
        // the small table the capture showed. The band's ceiling is 0.96 * 800 = 768px, so 42px is the
        // largest glyph whose table (18 * 42 = 756px) still fits it; 43px would be 774.
        ResultTableSize large = Measure(12, 1.5f, 40f, 42);
        int largeFont = ResultsFontGrowth.FitResultFontSize(15, 15, int.MaxValue, 800f, 2500f, Table(12, 1.5f, 40f));
        check(largeFont == 42, $"an 800px strip and 2500px of width grow a 12-row board from 15px to 42px (got {largeFont}px)");
        check(large.Height >= 800f * 0.85f,
            $"the grown board uses the strip: {large.Height:0}px of 800px is {large.Height * 100f / 800f:0}%, at least the 85% this pins");
        check(large.Height <= 800f * ResultsFontGrowth.StripFillCeiling + 0.5f,
            $"and never presses past the band's ceiling: {large.Height:0}px <= {800f * ResultsFontGrowth.StripFillCeiling:0}px");
        check(large.Width <= 2500f, $"the grown board stays inside the width it was fitted to: {large.Width:0}px <= 2500px");

        // ---- a short strip: growth stops instead of overflowing --------------------------------
        //
        // The same table in a 200px strip: the ceiling is 192px, so 10px (180px tall) is the last size
        // that fits and 11px (198px) is not.
        ResultTableSize small = Measure(12, 1.5f, 40f, 10);
        int smallFont = ResultsFontGrowth.FitResultFontSize(5, 5, int.MaxValue, 200f, 2500f, Table(12, 1.5f, 40f));
        check(smallFont == 10, $"a 200px strip stops the climb at 10px (got {smallFont}px)");
        check(small.Height <= 200f, $"and the board that grew into it still fits the strip: {small.Height:0}px <= 200px");
        check(Measure(12, 1.5f, 40f, smallFont + 1).Height > 200f * ResultsFontGrowth.StripFillCeiling,
            "the size the search refused really would have left the band, so the stop was a measurement, not a guess");

        // ---- a board that already fills the strip is left exactly where it is -------------------
        //
        // Idempotence is what makes a refresh safe: the second call is given the size the first one
        // returned, and must hand the same size back rather than growing a board that is already full.
        int settled = ResultsFontGrowth.FitResultFontSize(42, 42, int.MaxValue, 800f, 2500f, Table(12, 1.5f, 40f));
        check(settled == 42, $"a board already inside the band is not grown again (42px in, {settled}px out)");
        int settledAgain = ResultsFontGrowth.FitResultFontSize(settled, settled, int.MaxValue, 800f, 2500f, Table(12, 1.5f, 40f));
        check(settledAgain == settled, "and asking a third time still answers the same size");

        // ---- the readability ceiling ----------------------------------------
        //
        // A strip 5000px tall would accept anything, so the only bound left is 3.5 times the 15px seed,
        // which is 52px. The caller passing `int.MaxValue` must not be able to talk the search out of it.
        int capped = ResultsFontGrowth.FitResultFontSize(15, 15, int.MaxValue, 5000f, 100000f, Table(1, 0.1f, 1f));
        check(capped == 52, $"an unbounded strip stops at 3.5x the sharing size: 15px -> 52px (got {capped}px)");
        check(capped <= (int)Math.Floor(15 * ResultsFontGrowth.MaximumSeedMultiple),
            $"the cap is the floor of 3.5 * 15 = {Math.Floor(15 * ResultsFontGrowth.MaximumSeedMultiple)}, and {capped}px is not past it");
        int callerCap = ResultsFontGrowth.FitResultFontSize(15, 15, 24, 5000f, 100000f, Table(1, 0.1f, 1f));
        check(callerCap == 24, $"a caller's own ceiling of 24px is honoured as well (got {callerCap}px)");

        // ---- the width can be the thing that stops the climb --------------------------------
        //
        // Strip 2000px, page 1200px: the board is nowhere near the strip's band, but 30px is exactly
        // 1200px wide, so that is where the growth ends. The width is used, never overrun.
        int widthBound = ResultsFontGrowth.FitResultFontSize(15, 15, int.MaxValue, 2000f, 1200f, Table(12, 1.5f, 40f));
        ResultTableSize widthBoundSize = Measure(12, 1.5f, 40f, widthBound);
        check(widthBound == 30, $"a narrow page stops the climb at the width, not at the strip: 30px (got {widthBound}px)");
        check(widthBoundSize.Width <= 1200f,
            $"and that board is still inside the page width: {widthBoundSize.Width:0}px <= 1200px");
        check(widthBoundSize.Width >= 1200f * ResultsFontGrowth.WidthFillRatio,
            $"the width really is used: {widthBoundSize.Width:0}px is {widthBoundSize.Width * 100f / 1200f:0}% of it");

        // ---- degenerate input: every one of them keeps the sharing size ---------------------
        //
        // A page whose edges could not be measured has nothing to fit to, so the board must not move on
        // the strength of a number that is not there. Each of these is a state the result pages really
        // reach: an unmeasured strip, an unmeasured button, a window that has not been sized yet, and a
        // font atlas that has not loaded, which is a measure that throws or answers nothing.
        check(ResultsFontGrowth.FitResultFontSize(15, 15, int.MaxValue, 0f, 2500f, Table(12, 1.5f, 40f)) == 15,
            "a strip with no height keeps the sharing size");
        check(ResultsFontGrowth.FitResultFontSize(15, 15, int.MaxValue, -40f, 2500f, Table(12, 1.5f, 40f)) == 15,
            "a negative strip keeps the sharing size");
        check(ResultsFontGrowth.FitResultFontSize(15, 15, int.MaxValue, float.NaN, 2500f, Table(12, 1.5f, 40f)) == 15,
            "a strip that could not be measured at all keeps the sharing size");
        check(ResultsFontGrowth.FitResultFontSize(15, 15, int.MaxValue, 800f, 0f, Table(12, 1.5f, 40f)) == 15,
            "a page with no width keeps the sharing size");
        check(ResultsFontGrowth.FitResultFontSize(15, 15, int.MaxValue, 800f, 10f, Table(12, 1.5f, 40f)) == 15,
            "a page narrower than the board cannot be grown into, so the sharing size stands");
        check(ResultsFontGrowth.FitResultFontSize(15, 15, int.MaxValue, 800f, 2500f, null) == 15,
            "a caller with no measurement at all keeps the sharing size");
        check(ResultsFontGrowth.FitResultFontSize(15, 15, int.MaxValue, 800f, 2500f,
                size => throw new InvalidOperationException("no atlas")) == 15,
            "a measure that throws keeps the sharing size instead of taking the page down");
        check(ResultsFontGrowth.FitResultFontSize(15, 15, int.MaxValue, 800f, 2500f,
                size => ResultTableSize.None) == 15,
            "a measure that reports no extent keeps the sharing size");
        check(ResultsFontGrowth.FitResultFontSize(15, 15, int.MaxValue, 800f, 2500f,
                size => new ResultTableSize(600f, 0f)) == 15,
            "a table measured with no height keeps the sharing size");
        check(ResultsFontGrowth.FitResultFontSize(15, 15, int.MaxValue, 800f, 2500f,
                size => new ResultTableSize(float.NaN, float.NaN)) == 15,
            "a table measured as not-a-number keeps the sharing size");
        int unseeded = ResultsFontGrowth.FitResultFontSize(0, 0, int.MaxValue, 800f, 2500f, Table(12, 1.5f, 40f));
        check(unseeded >= 1 && unseeded <= (int)Math.Floor(1 * ResultsFontGrowth.MaximumSeedMultiple),
            $"a glyph size of zero or less is clamped to a drawable one and still bounded by the cap ({unseeded}px)");
        check(ResultsFontGrowth.FitResultFontSize(15, 15, int.MaxValue, float.PositiveInfinity, 2500f, Table(12, 1.5f, 40f)) == 15,
            "a strip that measured as infinite is treated as unmeasured, so the sharing size stands");

        // ---- a measure that is not monotone cannot make the search spin ---------------------
        //
        // The bisection assumes a taller glyph makes a taller table, which is true of the real renderer.
        // A caller whose measurement is noise must still come back: the iteration bound is the promise.
        int measurements = 0;
        int noisy = ResultsFontGrowth.FitResultFontSize(15, 15, int.MaxValue, 800f, 2500f, size =>
        {
            measurements++;
            // Only the sharing size is reported as fitting; every larger probe claims a 5000px tall table.
            return new ResultTableSize(40f * size, size == 15 ? 270f : 5000f);
        });
        check(noisy == 15, $"a measurement that answers nonsense cannot talk the search into a bigger board ({noisy}px)");
        check(measurements <= ResultsFontGrowth.MaximumMeasurements + 2,
            $"and it is probed a bounded number of times ({measurements} probes, bound {ResultsFontGrowth.MaximumMeasurements + 2})");

        // ---- the board's width: its own cells, one width for the value columns --------------------------
        //
        // The font fit is bounded by the strip more often than by the width, so the board that comes out of it
        // is narrower than the page. 1.19.5 drew it at exactly the width its cells measured and 1.19.7 stretched
        // it across the page; that stretch is what this branch removes, so a wide page leaves the board its own
        // size again. What survives is the rule behind the shared player columns - `#1`..`#N` line up because
        // every value column is given the widest measurement of them - and a label column measured on its own.
        // The fixture's value columns are deliberately unequal, so the one width they come out with is a
        // measurement of them rather than a width the fixture handed in already equal.
        float[] unequalPlayers = { 6.75f * 24f, 7.15f * 24f, 6.1f * 24f, 6.8f * 24f };
        PanelGrid natural = Board(SettlementWidths(5.4f * 24f, unequalPlayers, 24), 24);
        float naturalWidth = natural.Width;
        bool equalPlayers = true;
        for (int column = 2; column < natural.Columns; column++)
            equalPlayers &= Math.Abs(natural.CellWidths[column] - natural.CellWidths[1]) <= 0.001f;
        check(equalPlayers,
            $"the player columns print at one width, the widest of {natural.Columns - 1} unequal columns");
        float padding = natural.CellWidths[0] - 5.4f * 24f;
        bool measuredValues = true;
        for (int column = 1; column < natural.Columns; column++)
            measuredValues &= Math.Abs(natural.CellWidths[column] - (7.15f * 24f + padding)) <= 0.01f;
        check(measuredValues,
            $"every value column is drawn at the widest natural width, 7.15em plus one cell padding: {natural.CellWidths[1]:0.###}px");
        check(naturalWidth <= 2512f + 0.01f,
            $"the board's own width fits the page without being stretched: {naturalWidth:0.###}px <= 2512px");
        // A larger glyph widens the board through the cells it actually prints, and nothing widens it behind
        // the layout's back: the width the board is drawn at is the width it measured.
        PanelGrid wider = Board(SettlementWidths(5.4f * 48f, new[] { 6.75f * 48f, 7.15f * 48f, 6.1f * 48f, 6.8f * 48f }, 48), 48);
        check(wider.Width > naturalWidth,
            "a larger glyph widens the board only through the cells it actually prints, never through padding");

        // ---- the two numbers the capture showed, end to end ---------------------------------
        //
        // The strip in the 2560x1440 capture is about 940px between the completion section and the lobby
        // button, and the page offers 2560 - 48 = 2512px. The 12x5 board is grown until it fills that strip
        // and is then drawn at its own measured width, so neither the icons above it nor the button below it
        // are reached and the columns carry no padding the figures did not ask for.
        int captureFont = ResultsFontGrowth.FitResultFontSize(15, 15, int.MaxValue, 940f, 2512f, Table(12, 1.5f, 40f));
        ResultTableSize capture = Measure(12, 1.5f, 40f, captureFont);
        check(capture.Height >= 940f * 0.92f && capture.Height <= 940f * ResultsFontGrowth.StripFillCeiling + 0.5f,
            $"the captured strip takes the board to {captureFont}px, {capture.Height:0}px of 940px ({capture.Height * 100f / 940f:0}% of it)");
        check(capture.Width <= 2512f, $"and the board is still inside the page's width: {capture.Width:0}px");
        check(capture.Height > 370f && capture.Width > 600f,
            "which is a materially larger board than the 600x370 one the capture showed");

        // ---- 1.19.7: the glyph size is the largest one that fits, whichever way that is ---------------
        //
        // `FitResultFontSize` above only ever climbs: a seed table taller than the strip is returned
        // unchanged and `HudTable.BuildGrid` then squeezes the finished table into the box it was given,
        // which is exactly how the settlement board came out at ~9px with a band of empty page above the
        // footer. `FitIntoStrip` looks downwards as well, so the size that is drawn is the size that fills
        // the strip instead of the size that had to be crushed into it.
        //
        // 60px of 12x1.5em rows is 1080px in a strip of 940px, whose band's ceiling is 0.96 * 940 + 0.5 =
        // 902.9px; 50px is 900px and 51px is 918px, so 50px is the largest glyph that fits.
        ResultTableSize tooTall = Measure(12, 1.5f, 40f, 60);
        int shrunkFont = ResultsFontGrowth.FitIntoStrip(60, ResultsFontGrowth.MinimumReadablePixels, int.MaxValue, 940f, 2512f, Table(12, 1.5f, 40f));
        ResultTableSize shrunk = Measure(12, 1.5f, 40f, shrunkFont);
        check(shrunkFont != 60, $"种子表格比条带高时，字号不再原样返回（60px -> {shrunkFont}px）");
        check(shrunkFont == 50, $"940px 的条带把 1080px 的表格收到 50px（实际 {shrunkFont}px）");
        check(tooTall.Height > 940f * ResultsFontGrowth.StripFillCeiling,
            $"种子本身确实超标：{tooTall.Height:0}px > {940f * ResultsFontGrowth.StripFillCeiling:0}px");
        check(shrunk.Height >= 940f * 0.92f && shrunk.Height <= 940f * 0.965f,
            $"缩小后的表格高度落在条带 92%–96.5% 之间：{shrunk.Height:0}px 是 940px 的 {shrunk.Height * 100f / 940f:0.0}%");
        check(shrunk.Width <= 2512f, $"缩小后的表格仍在页面宽度内：{shrunk.Width:0}px <= 2512px");
        check(Measure(12, 1.5f, 40f, shrunkFont + 1).Height > 940f * ResultsFontGrowth.StripFillCeiling,
            "被拒绝的下一档字号真的会越界，所以停下来是一次测量而不是一次猜测");

        // The other direction is unchanged and still ends inside the band: 15px grows to 42px in the 800px
        // strip, which is 756px of it (94.5%), and 43px would be 774px and outside the ceiling.
        int grownFont = ResultsFontGrowth.FitIntoStrip(15, ResultsFontGrowth.MinimumReadablePixels, int.MaxValue, 800f, 2500f, Table(12, 1.5f, 40f));
        ResultTableSize grown = Measure(12, 1.5f, 40f, grownFont);
        check(grownFont == 42, $"种子远小于条带时仍向上生长到 42px（实际 {grownFont}px）");
        check(grownFont >= 15, "向上生长不会让字号变小");
        check(grown.Height >= 800f * 0.92f && grown.Height <= 800f * 0.965f,
            $"生长后的表格高度落在条带 92%–96.5% 之间：{grown.Height:0}px 是 800px 的 {grown.Height * 100f / 800f:0.0}%");
        // A strip taller than anything the table could need stops at the width instead: 40em per glyph in a
        // 1200px page is 30px, and 31px would be 1240px.
        int widthStopped = ResultsFontGrowth.FitIntoStrip(15, ResultsFontGrowth.MinimumReadablePixels, int.MaxValue, 5000f, 1200f, Table(12, 1.5f, 40f));
        ResultTableSize widthStoppedSize = Measure(12, 1.5f, 40f, widthStopped);
        check(widthStopped == 30, $"条带够高时生长撞在宽度上限上的 30px（实际 {widthStopped}px）");
        check(widthStoppedSize.Width <= 1200f + ResultsFontGrowth.WidthSlack,
            $"撞上宽度上限的表格没有越界：{widthStoppedSize.Width:0}px <= 1200px");
        check(widthStoppedSize.Height < 5000f * 0.92f,
            $"这次停下来的是宽度而不是高度：{widthStoppedSize.Height:0}px 远小于 5000px 条带的 92%");
        // The same inputs asked twice answer the same size, which is what makes a refresh safe.
        int again = ResultsFontGrowth.FitIntoStrip(shrunkFont, ResultsFontGrowth.MinimumReadablePixels, int.MaxValue, 940f, 2512f, Table(12, 1.5f, 40f));
        check(again == shrunkFont, $"同一段条带再问一次得到同一个字号（{shrunkFont}px -> {again}px）");

        // ---- 1.19.7: an unmeasurable page keeps the sharing size --------------------------------
        //
        // Every one of these is a state a result page really reaches: an unmeasured strip, a window that has
        // not been sized yet, and a font atlas that has not loaded - a measure that answers nothing or throws.
        // None of them may resize the board, and none of them may take the page down.
        check(ResultsFontGrowth.FitIntoStrip(15, 6, int.MaxValue, 0f, 2500f, Table(12, 1.5f, 40f)) == 15,
            "条带高度为 0 时保留共享字号");
        check(ResultsFontGrowth.FitIntoStrip(15, 6, int.MaxValue, -40f, 2500f, Table(12, 1.5f, 40f)) == 15,
            "条带高度为负时保留共享字号");
        check(ResultsFontGrowth.FitIntoStrip(15, 6, int.MaxValue, float.NaN, 2500f, Table(12, 1.5f, 40f)) == 15,
            "条带高度不可测（NaN）时保留共享字号");
        check(ResultsFontGrowth.FitIntoStrip(15, 6, int.MaxValue, float.PositiveInfinity, 2500f, Table(12, 1.5f, 40f)) == 15,
            "条带高度为无穷大时视为不可测，保留共享字号");
        check(ResultsFontGrowth.FitIntoStrip(15, 6, int.MaxValue, 800f, 0f, Table(12, 1.5f, 40f)) == 15,
            "页面宽度为 0 时保留共享字号");
        check(ResultsFontGrowth.FitIntoStrip(15, 6, int.MaxValue, 800f, -120f, Table(12, 1.5f, 40f)) == 15,
            "页面宽度为负时保留共享字号");
        check(ResultsFontGrowth.FitIntoStrip(15, 6, int.MaxValue, 800f, float.NaN, Table(12, 1.5f, 40f)) == 15,
            "页面宽度不可测（NaN）时保留共享字号");
        check(ResultsFontGrowth.FitIntoStrip(15, 6, int.MaxValue, 800f, 2500f, null) == 15,
            "没有任何测量时保留共享字号");
        check(ResultsFontGrowth.FitIntoStrip(15, 6, int.MaxValue, 800f, 2500f,
                size => throw new InvalidOperationException("no atlas")) == 15,
            "测量抛异常时保留共享字号，而不是把页面带崩");
        check(ResultsFontGrowth.FitIntoStrip(15, 6, int.MaxValue, 800f, 2500f, size => ResultTableSize.None) == 15,
            "测量报告没有尺寸时保留共享字号");
        check(ResultsFontGrowth.FitIntoStrip(15, 6, int.MaxValue, 800f, 2500f,
                size => new ResultTableSize(600f, 0f)) == 15,
            "测量报告没有高度时保留共享字号");
        check(ResultsFontGrowth.FitIntoStrip(15, 6, int.MaxValue, 800f, 2500f,
                size => new ResultTableSize(float.NaN, float.NaN)) == 15,
            "测量报告 NaN 时保留共享字号");
        // A strip too short to hold the table at any readable size is the caller's shrink, not a 1px font.
        int unreadable = ResultsFontGrowth.FitIntoStrip(15, 6, int.MaxValue, 40f, 2500f, Table(40, 1.5f, 40f));
        check(unreadable == 15, $"连可读下限都装不下的条带保留共享字号，交给调用方自己缩（{unreadable}px）");
        check(unreadable >= ResultsFontGrowth.MinimumReadablePixels,
            $"并且绝不会返回小于可读下限的字号（下限 {ResultsFontGrowth.MinimumReadablePixels}px）");

        // ---- 1.19.7: the strip's lower bound is the lobby button itself -------------------------
        //
        // A result page also carries controls that are switched on and drawn yet sit far above the footer.
        // Taking the highest of them is what cost the board ~180px of page and made it draw small; when the
        // button the player presses can be measured, that button is the only wall.
        check(ResultsRegion.LobbyEdge(true, 1000f, 820f, 1440f) == 1000f,
            "大厅按钮可测时，即使按钮带更高（820 < 1000），下边界仍是按钮本身 1000");
        check(ResultsRegion.LobbyEdge(true, 1000f, 1240f, 1440f) == 1000f,
            "大厅按钮可测时，即使按钮带更低（1240 > 1000），下边界仍是按钮本身 1000");
        check(ResultsRegion.LobbyEdge(true, 1000f, float.NaN, 1440f) == 1000f,
            "大厅按钮可测时，NaN 的按钮带不会传染给下边界");
        check(ResultsRegion.LobbyEdge(false, 1000f, 820f, 1440f) == 820f,
            "大厅按钮不可测时回落到按钮带 820");
        check(ResultsRegion.LobbyEdge(true, float.NaN, 820f, 1440f) == 820f,
            "大厅按钮上边缘为 NaN 时回落到按钮带 820");
        check(ResultsRegion.LobbyEdge(true, 0f, 820f, 1440f) == 820f,
            "大厅按钮上边缘为 0 时视为不可测，回落到按钮带 820");
        check(ResultsRegion.LobbyEdge(true, -30f, 820f, 1440f) == 820f,
            "大厅按钮上边缘为负时视为不可测，回落到按钮带 820");
        check(ResultsRegion.LobbyEdge(true, 2000f, 820f, 1440f) == 820f,
            "大厅按钮上边缘落在窗口之外时视为不可测，回落到按钮带 820");
        check(!float.IsFinite(ResultsRegion.LobbyEdge(false, 1000f, float.NaN, 1440f)),
            "两侧都没有测量时不凭空造出一个边界");

        // ---- 1.19.7 (H1): both named buttons bound the strip, an unnamed control above them does not ----
        //
        // The failure strip has two buttons this build knows by name: "send every prisoner back to the
        // lobby" and "restart checkpoint". The higher of them is what the lobby side is given, so a page that
        // lays the restart button above the lobby button cannot be covered either - and a control the page
        // shows but this build cannot name still may not cost the board its height while one of them is
        // readable. Both are folded with `HighestTop`, whose NaN semantics are unchanged.
        float restartFirst = ResultsRegion.HighestTop(700f, 1000f);
        check(restartFirst == 700f && ResultsRegion.LobbyEdge(true, restartFirst, 500f, 1440f) == 700f,
            "重开检查点排在返回大厅之上时，下边界取两者更高的 700，既不取 1000，也不回落到更高但未命名的按钮带 500");
        float lobbyFirst = ResultsRegion.HighestTop(1100f, 700f);
        check(lobbyFirst == 700f && ResultsRegion.LobbyEdge(true, lobbyFirst, 500f, 1440f) == 700f,
            "返回大厅排在重开检查点之上时，下边界仍取两者更高的 700（参数顺序不影响取更高者）");
        check(ResultsRegion.HighestTop(float.NaN, 1000f) == 1000f && ResultsRegion.HighestTop(1000f, float.NaN) == 1000f,
            "两个已命名按钮只有一个可测时，另一个的 NaN 不会吞掉它的边缘");
        float neither = ResultsRegion.HighestTop(float.NaN, float.NaN);
        check(!float.IsFinite(neither) && ResultsRegion.LobbyEdge(false, neither, 500f, 1440f) == 500f,
            "两个已命名按钮都不可测时，下边界交回页面按钮带 500，未命名控件照样被让开");

        // ---- one width for the value columns, the label on its own, whatever N is ----------------------
        //
        // 1.19.5 drew the board at its own measured width - about 850px on a 2512px page - and 1.19.7 stretched
        // it across the page; this branch takes the stretch back out. What is pinned here is the rule that
        // survives it: the board is one label column and N value columns, and the width they share is the
        // widest of the value columns' own measurements, never a share of the page. A full squad is four value
        // columns and a squad that lost players has fewer, so the rule is exercised from one to seven of them
        // instead of being pinned to the five columns of the capture.
        for (int players = 1; players <= 7; players++)
        {
            var naturals = new float[players];
            for (int index = 0; index < players; index++) naturals[index] = (5.4f + index * 0.35f) * 24f;
            float widest = Math.Max(naturals[players - 1], 4f * 24f);
            float[] widths = SettlementWidths(9.25f * 24f, naturals, 24);
            bool shared = Math.Abs(widths[0] - 9.25f * 24f) <= 0.001f;
            for (int column = 1; column < widths.Length; column++)
                shared &= Math.Abs(widths[column] - widest) <= 0.001f;
            check(shared,
                $"{players} 个数值列全部等于其原始自适应宽度中的最大值 {widest:0.##}px（标签列保留自己的 {widths[0]:0.##}px）：{string.Join(" / ", widths)}");
        }
        // The shared width is a measurement, not a default: two value columns of different natural widths both
        // come out at the wider one, so the narrower column's own width is not what gets drawn.
        float[] twoValues = SettlementWidths(4.0f * 24f, new[] { 6.2f * 24f, 7.15f * 24f }, 24);
        check(Math.Abs(twoValues[1] - 7.15f * 24f) <= 0.001f && Math.Abs(twoValues[2] - 7.15f * 24f) <= 0.001f,
            $"窄的那一列被放宽到宽的那一列，而不是各按自己：{twoValues[1]:0.##}px / {twoValues[2]:0.##}px");

        // The label column is the one column outside that rule: as wide as its own content, floored at 4 em, so
        // a short label is not squeezed below its floor and a long one does not drag the figures wider with it.
        float[] shortLabel = SettlementWidths(1.4f * 24f, new[] { 6.2f * 24f, 7.15f * 24f }, 24);
        check(Math.Abs(shortLabel[0] - 4f * 24f) <= 0.001f,
            $"标签列比 4em 下限还窄时按下限画，而不是跟着内容缩：{shortLabel[0]:0.##}px = {4f * 24f:0}px");
        float[] longLabel = SettlementWidths(11.5f * 24f, new[] { 6.2f * 24f, 7.15f * 24f }, 24);
        check(Math.Abs(longLabel[0] - 11.5f * 24f) <= 0.001f
              && Math.Abs(longLabel[1] - 7.15f * 24f) <= 0.001f && Math.Abs(longLabel[2] - 7.15f * 24f) <= 0.001f,
            $"标签列按自己的内容 {longLabel[0]:0.##}px 测量，数值列仍是它们的 {longLabel[1]:0.##}px，互不牵连");

        // And nothing takes the page's width: the board built from the capture's fixture keeps the width its
        // cells measured and stays well inside a 2512px page, which is the point of dropping the fill.
        PanelGrid pageBoard = Board(SettlementWidths(5.4f * 24f, new[] { 6.75f * 24f, 7.15f * 24f, 6.1f * 24f, 6.8f * 24f }, 24), 24);
        check(pageBoard.Width < 2512f - 1f,
            $"结算板按自己的测量宽度 {pageBoard.Width:0.##}px 画，不再被拉满 2512px 的页面");
        check(pageBoard.Rows == 12 && pageBoard.Columns == 5,
            "列组成与行数没有被这一改动动过：1 个标签列 + 4 个数值列，12 行");

        // ---- 1.19.7: the readability floor comes from the button the player presses ---------------------
        //
        // A 28-row board in a 470px strip is the shape that used to be drawn at ~10px above an empty band: the
        // fit shrank the table to the absolute 6px minimum and stopped there, and nothing tied the size to the
        // control underneath it. The lobby button measured 120px tall on the page that produced the capture, so
        // the floor is 0.15 * 120 = 18px and the fit is asked for that instead.
        int buttonFloor = ResultsFontGrowth.ReadabilityFloor(120f);
        check(buttonFloor >= 18,
            $"120px 高的返回大厅按钮给出 ≥18px 的可读下限（实际 {buttonFloor}px）");
        check(buttonFloor <= ResultsFontGrowth.MaximumReadabilityFloor,
            "按钮高度给出的下限仍被上限截住，不会把结算板画成海报");
        check(ResultsFontGrowth.ReadabilityFloor(float.NaN) == ResultsFontGrowth.MinimumReadablePixels
              && ResultsFontGrowth.ReadabilityFloor(0f) == ResultsFontGrowth.MinimumReadablePixels
              && ResultsFontGrowth.ReadabilityFloor(-40f) == ResultsFontGrowth.MinimumReadablePixels,
            "按钮高度不可测、为 0 或为负时退回今天的绝对下限，页面行为与今天完全一致");
        int oldShortStrip = ResultsFontGrowth.FitIntoStrip(18, ResultsFontGrowth.MinimumReadablePixels, int.MaxValue, 470f, 2512f, Table(28, 1.5f, 40f));
        int newShortStrip = ResultsFontGrowth.FitIntoStrip(18, buttonFloor, int.MaxValue, 470f, 2512f, Table(28, 1.5f, 40f));
        check(oldShortStrip < buttonFloor,
            $"同一条 470px 条带按今天 6px 的绝对下限会缩到 {oldShortStrip}px，那正是「小小的」那一版");
        // The floor is what the board would like, never what it may cost. 28 rows at the 18px floor are 756px
        // of a 470px strip: the size the floor asks for cannot be placed, and a board that cannot be placed is
        // a board nobody sees - which is the regression this pins. The size is therefore asked for twice, and
        // what is asserted is the second answer: a table that really fits the strip and is therefore drawn.
        float shortCeiling = 470f * ResultsFontGrowth.StripFillCeiling + 0.5f;
        check(Measure(28, 1.5f, 40f, newShortStrip).Height > shortCeiling,
            $"按 18px 下限算出的表格本身就是 {Measure(28, 1.5f, 40f, newShortStrip).Height:0}px，放不进 470px 的条带（上限 {shortCeiling:0}px），整块不画就是这么来的");
        int drawnShortFont = ResultsFontGrowth.FitBoardIntoStrip(18, buttonFloor, int.MaxValue, 470f, 2512f,
            Table(28, 1.5f, 40f), out int shortFloor);
        ResultTableSize drawnShort = Measure(28, 1.5f, 40f, drawnShortFont);
        check(shortFloor == ResultsFontGrowth.MinimumReadablePixels && drawnShortFont < buttonFloor,
            $"装不下可读下限时退回绝对下限 {ResultsFontGrowth.MinimumReadablePixels}px 重算，最后画的是 {drawnShortFont}px（下限 {buttonFloor}px）");
        check(drawnShort.Height <= shortCeiling,
            $"退回下限后产出的表格高度不超过条带的 96%+0.5，结算板一定画得出来：{drawnShort.Height:0}px <= {shortCeiling:0}px");
        check(ResultsFontGrowth.FitBoardIntoStrip(18, buttonFloor, int.MaxValue, 470f, 2512f, Table(28, 1.5f, 40f), out _)
              == drawnShortFont,
            "同一段条带、同一个下限问两次得到同一个字号，刷新不会自己改变尺寸");
        // The other half of the same rule: a strip tall enough to hold the readable size keeps it and is raised
        // above it. 28 rows at 18px are 756px of a 1400px strip, so the floor is honoured and the search goes on
        // to 32px of it - the floor is not a cap.
        int tallShortFont = ResultsFontGrowth.FitBoardIntoStrip(18, buttonFloor, int.MaxValue, 1400f, 2512f,
            Table(28, 1.5f, 40f), out int tallFloor);
        ResultTableSize drawnTall = Measure(28, 1.5f, 40f, tallShortFont);
        check(tallFloor == buttonFloor && tallShortFont >= buttonFloor,
            $"条带够高时字号确实被抬到可读下限以上：{tallShortFont}px >= {buttonFloor}px（用的下限 {tallFloor}px）");
        check(drawnTall.Height >= 1400f * ResultsFontGrowth.StripFillFloor && drawnTall.Height <= 1400f * ResultsFontGrowth.StripFillCeiling + 0.5f,
            $"高条带里的表格落在 92%–96% 的带子里：{drawnTall.Height:0}px 是 1400px 的 {drawnTall.Height * 100f / 1400f:0.0}%");
    }

    // The table the real renderer measures: `rows` rows of `rowEm` ems each, the whole row `emWidth` ems
    // wide. Both extents grow with the glyph size, exactly as `PanelGeometry.Build` does, which is what
    // makes this a fair stand-in for the board without needing Unity.
    private static Func<int, ResultTableSize> Table(int rows, float rowEm, float emWidth)
        => size => new ResultTableSize(emWidth * size, rows * rowEm * size);

    private static ResultTableSize Measure(int rows, float rowEm, float emWidth, int fontSize)
        => Table(rows, rowEm, emWidth)(fontSize);

    // The widths a settlement board is drawn with, given the width every column measured on its own: the
    // board's own rule, headless. Column 0 is the row label; every column after it is a player's figures.
    private static float[] SettlementWidths(float labelNatural, float[] valueNaturals, int fontSize)
    {
        var columns = new PanelColumn[valueNaturals.Length + 1];
        var measured = new float[valueNaturals.Length + 1];
        columns[0] = PanelColumn.ImpactLabel;
        measured[0] = labelNatural;
        for (int index = 0; index < valueNaturals.Length; index++)
        {
            columns[index + 1] = PanelColumn.ImpactValue;
            measured[index + 1] = valueNaturals[index];
        }
        return PanelGeometry.SettlementColumnWidths(columns, measured, fontSize);
    }

    // The grid those widths produce, through the same `PanelGeometry.Build` the renderer calls: the label
    // column reads from the left and every value column hugs its right edge.
    private static PanelGrid Board(float[] widths, int fontSize, int rows = 12)
    {
        var metrics = new PanelColumnMetrics[widths.Length];
        var aligned = new bool[widths.Length];
        for (int index = 0; index < widths.Length; index++)
        {
            metrics[index] = new PanelColumnMetrics(new[] { widths[index] });
            aligned[index] = index > 0;
        }
        return PanelGeometry.Build(metrics, 0f, fontSize, rows, PanelDensity.Compact, 1f, aligned);
    }
}
