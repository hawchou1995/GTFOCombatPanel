using System;
using System.IO;
using HawChow.GTFOCombatPanel;

// ---------------------------------------------------------------------------------------------------
// Release11954Scenarios.cs - the FitBoard input contract, and the failure page back at 1.19.50's numbers
// ---------------------------------------------------------------------------------------------------
//
// `FitBoard` is shared by both result pages. Its parameter is the height the *table* may use - the band minus the header
// the board reserves - and exactly one caller-side deduction feeds it:
//
//   TryFit            (both pages)   FitBoard(content, room - header, ...)     <- 1.19.50 unchanged
//   the resize path   (both pages)   FitBoard(content, room - header, ...)     <- 1.19.50 unchanged
//   TrySmallerGrid    (the fallback) FitBoard(_content, room - header on success, room unchanged on failure, ...)
//
// 1.19.53 deducted the header a second time *inside* `FitBoard`, i.e. both result pages were deducted twice and the
// fallback deducted twice on success. On the two recorded failure pages the automatic target caps the glyph, so the
// doubled deduction is *masked* there (asserted below) - it costs one whole glyph step wherever the search binds, e.g.
// 8 px -> 7 px on the 426 px short band. 1.19.54 removes the inner deduction and puts the one deduction where the
// argument comes from; the failure page is then byte for byte 1.19.50's arithmetic (same argument, same text lines).
internal static class Release11954Scenarios
{
    private const int Rows = 30;
    private const float GridWidth = 330f;

    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        TheFitTakesBodyHeightAndNobodyDeductsTwice(check);
        TheFailurePageKeepsIts11950NumbersAtBothResolutions(check);
        TheAutoTargetOnlyCapsAndNeverRaisesTheSearchAnswer(check);
        TheVersionCarrierIsThisFix(check);
    }

    // ---------- the contract ----------

    private static void TheFitTakesBodyHeightAndNobodyDeductsTwice(Action<bool, string> check)
    {
        string display = ProductionText("ResultsDisplay.cs");
        check(display.Contains("private static PanelGrid FitBoard(TableContent content, float bodyRoom,"),
            "`FitBoard`'s parameter is named for what it is: the body height the caller hands it");
        string fit = MethodBody(display, "private static PanelGrid FitBoard(");
        check(!fit.Contains("ChromeHeader()") && !fit.Contains("room - header"),
            "and nothing is reserved inside it - no `ChromeHeader()`, no `room - header`, so a caller cannot be deducted twice");
        check(Tally(display, "FitBoard(content, tableRoom, boardWidth, succeeded, strip.ButtonHeight, strip.FillCeiling)") == 2,
            "both strict call sites (the fit and the resize path) hand it the 1.19.50 `tableRoom`, unchanged: "
                + Tally(display, "FitBoard(content, tableRoom, boardWidth, succeeded, strip.ButtonHeight, strip.FillCeiling)"));
        check(display.Contains("FitBoard(_content, body, boardWidth, _succeeded);")
              && display.Contains("if (_succeeded && body - header > 0f) body -= header;"),
            "the fallback deducts the header on the success page only and hands the failure page the band it always did");
        check(display.Contains("float tableRoom = room - header;"),
            "and the strict paths still compute their body height as `room - header`");
    }

    // ---------- the failure page, both recorded resolutions ----------

    private static void TheFailurePageKeepsIts11950NumbersAtBothResolutions(Action<bool, string> check)
    {
        ResultChromeMetrics hd = ResultChromeMetrics.For(1920f, 1080f);
        float body1080 = (842f - 24f) - hd.HeaderHeight;      // the recorded r3 band 24..842
        int glyph1080 = FitGlyph(body1080, 1080f, out int rows1080, out float plate1080);
        check(Near(body1080, 776f) && glyph1080 == 12 && rows1080 == Rows && Near(plate1080 + hd.HeaderHeight, 582f),
            "the recorded 1080 failure page (band 24..842, body " + body1080.ToString("0") + "): " + glyph1080 + " px, "
                + rows1080 + "/" + Rows + " rows, plate " + (plate1080 + hd.HeaderHeight).ToString("0")
                + " px - the recorded 330x582");

        ResultChromeMetrics qhd = ResultChromeMetrics.For(2560f, 1440f);
        float body1440 = (1121f - 32f) - qhd.HeaderHeight;    // the recorded r2 band 32..1121
        int glyph1440 = FitGlyph(body1440, 1440f, out int rows1440, out float plate1440);
        check(Near(body1440, 1036f) && glyph1440 == 16 && rows1440 == Rows
              && Near(plate1440 + qhd.HeaderHeight, 773f),
            "the recorded 1440 failure page (band 32..1121, body " + body1440.ToString("0") + "): " + glyph1440 + " px, "
                + rows1440 + "/" + Rows + " rows, plate " + (plate1440 + qhd.HeaderHeight).ToString("0")
                + " px - the recorded 433x773");

        // The two recorded failure pages are tall enough that the automatic target caps the glyph either way, so a second
        // header's worth of body height is invisible *on them*. Said plainly, because it is the reason this defect could
        // pass a failure-page check: the contract shows up where the search is the binding constraint.
        int masked1080 = FitGlyph(body1080 - hd.HeaderHeight, 1080f, out _, out _);
        int masked1440 = FitGlyph(body1440 - qhd.HeaderHeight, 1440f, out _, out _);
        check(masked1080 == glyph1080 && masked1440 == glyph1440,
            "a second deduction is masked by the target cap on both recorded failure pages ("
                + masked1080 + " / " + masked1440 + " px), which is exactly why the contract has to be asserted, not inferred");

        // Where the search binds, one header of body height is one whole glyph step: the 426 px band's body is 384 px
        // (8 px) and a doubled 342 px answers 7 px for the same table.
        ResultChromeMetrics hd1080 = ResultChromeMetrics.For(1920f, 1080f);
        float shortBody = 426f - hd1080.HeaderHeight;
        int shortGlyph = FitGlyph(shortBody, 1080f, out int shortRows, out _);
        int doubledGlyph = FitGlyph(shortBody - hd1080.HeaderHeight, 1080f, out int doubledRows, out _);
        check(shortGlyph == 8 && doubledGlyph == 7 && shortRows == Rows && doubledRows == Rows,
            "on the short band the same table is " + shortGlyph + " px with one deduction and " + doubledGlyph
                + " px with two - the size the player reads, not the row count, is what a double deduction costs");
    }

    // ---------- the automatic target only caps ----------

    private static void TheAutoTargetOnlyCapsAndNeverRaisesTheSearchAnswer(Action<bool, string> check)
    {
        ResultChromeMetrics hd = ResultChromeMetrics.For(1920f, 1080f);
        float body = 426f - hd.HeaderHeight;
        int glyph = FitGlyph(body, 1080f, out int visible, out float plate);
        check(glyph == 8 && visible == Rows && Near(plate, 360f),
            "a 426 px lower band gives the table " + body.ToString("0") + " px of body: " + glyph + " px, "
                + visible + "/" + Rows + " rows, a " + plate.ToString("0") + " px plate - the whole table, not a clipped one");
        check(ResultsScroll.FontFor(glyph, 1f) == glyph,
            "`ResultsScroll.FontFor` keeps the search's answer at scale 1 - the automatic target is a ceiling, it cannot raise "
                + "the glyph back to 12: " + ResultsScroll.FontFor(glyph, 1f));
        check(ResultsScroll.FontFor(glyph, 1.5f) > glyph,
            "and the player's own scale still applies on top of it (" + ResultsScroll.FontFor(glyph, 1.5f) + " px at 1.5)");
        string display = ProductionText("ResultsDisplay.cs");
        check(display.Contains("if (autoTarget > 0 && fitted > autoTarget)") && display.Contains("fitted = autoTarget;"),
            "the runtime's target is written as a `>` clamp, never as an assignment that could raise a short band's answer");
    }

    // ---------- the version ----------

    // 1.19.55: this check no longer pins the number it was written for. The contract is what matters - the carrier is a
    // patch number newer than the last delivery, and it is one string in the plugin, the manifest, the changelog and the
    // README. Pinning "1.19.54" here would make every later release edit an old test until it says true again, which is
    // how a carrier check stops carrying anything.
    private static void TheVersionCarrierIsThisFix(Action<bool, string> check)
    {
        string info = ProductionText("PluginInfo.cs");
        const string carrier = "internal const string Version = \"";
        int at = info.IndexOf(carrier, StringComparison.Ordinal);
        string version = at < 0 ? string.Empty
            : info.Substring(at + carrier.Length, info.IndexOf('"', at + carrier.Length) - at - carrier.Length);
        check(version.Length > 0 && char.IsDigit(version[0]), "the carrier is a real number: " + version);
        check(Newer(version, "1.19.54"), "this fix's carrier is its own, newer patch number: " + version);
        check(ProductionText(Path.Combine("Thunderstore", "manifest.json")).Contains("\"version_number\": \"" + version + "\""),
            "the manifest carries it");
        check(ProductionText(Path.Combine("Thunderstore", "CHANGELOG.md")).Contains("## " + version),
            "the changelog has this build's own section");
        check(ProductionText("README.md").Contains("源码版本为 **" + version + "**"),
            "and the README's current state names it");
    }

    // Strictly newer, as a dotted number: the patch digit is the one a delivery moves.
    private static bool Newer(string version, string than)
    {
        string[] left = version.Split('.'), right = than.Split('.');
        if (left.Length == 0 || right.Length == 0) return false;
        for (int index = 0; index < Math.Max(left.Length, right.Length); index++)
        {
            int a = index < left.Length && int.TryParse(left[index], out int parsedLeft) ? parsedLeft : 0;
            int b = index < right.Length && int.TryParse(right[index], out int parsedRight) ? parsedRight : 0;
            if (a != b) return a > b;
        }
        return false;
    }

    // ---------- helpers ----------

    // `FitBoard` on a body height, with the same table content the settlement page builds: 30 rows on the compact row
    // pitch, a natural width that grows with the glyph (330 px at 12 px), and the window's automatic target as a cap.
    private static int FitGlyph(float bodyRoom, float screenHeight, out int visible, out float tableHeight)
    {
        int floor = ResultsBandFit.FloorForBand(bodyRoom, Rows, PanelDensity.Compact,
            ResultsFontGrowth.MinimumReadablePixels, out _);
        int fitted = ResultsFontGrowth.FitBoardIntoStrip(17, Math.Max(floor, ResultsFontGrowth.MinimumReadablePixels),
            int.MaxValue, bodyRoom, 1872f, size => Table(size), out int usedFloor);
        int target = ResultAutoBoardContract.TargetGlyphPixels(screenHeight);
        if (target > 0 && fitted > target) fitted = target;
        // The glyph the runtime draws: `ResultsScroll.FontFor(fitted, scale=1)`, floored by the band's own answer.
        int glyph = ResultsScroll.FontFor(fitted, 1f);
        if (usedFloor >= ResultsFontGrowth.MinimumReadablePixels && fitted >= usedFloor) glyph = fitted;
        float row = ResultsBandFit.RowHeight(glyph, PanelDensity.Compact);
        visible = ResultsScroll.Viewport(Rows, ResultsScroll.FrozenRows, Math.Max(1f, bodyRoom), row,
            ResultsScroll.FrozenRows).Visible;
        tableHeight = Math.Min(Rows * row, ResultsScroll.PlateHeight(visible, row, Math.Max(1f, bodyRoom)));
        return glyph;
    }

    private static ResultTableSize Table(int fontPixels)
    {
        if (fontPixels < 1) return ResultTableSize.None;
        float row = ResultsBandFit.RowHeight(fontPixels, PanelDensity.Compact);
        return new ResultTableSize(GridWidth * (fontPixels / 12f), Rows * row);
    }

    private static bool Near(float left, float right) => Math.Abs(left - right) <= 0.51f;

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
