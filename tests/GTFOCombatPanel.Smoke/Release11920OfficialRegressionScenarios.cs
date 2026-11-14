using System;
using System.IO;
using HawChow.GTFOCombatPanel;

// The three blocks of official 1.19.20 test coverage this workspace's test tree is missing, restored *here* rather than by
// editing the workspace's own `Release11916Scenarios.cs` - the one file the user has diverged from, and a file this round
// must not change.
//
// The independent verification measured the gap exactly: the workspace suite runs 2792 assertions where the official
// 1.19.20 tree runs 2828, and the 36 are two whole scenario methods plus six assertions from `Wiring`:
//
//   * `TheKillTierRowsAreOnlySpentWhenTheyBuyAGlyph`   8 check sites; its two loops of six and five make 17 runs
//   * `TheRoomIsTheBoundAndTheCliffIsGone`            13 sites, 13 runs  (the 269/286 px cliff 1.19.20 removed)
//   * the band block of `Wiring`                       6 sites, 6 runs    (`stripFloor` + five policy answers)
//
// 17 + 13 + 6 = 36. The bodies are the official ones and drive the same production policies the runtime calls -
// `ResultsFontGrowth.DrawableBoardHeight`, `ImpactBoard.SplitKillTiersFor`, `ResultsBandFit.MinimumDrawableHeight`,
// `ResultBoardHold.TryPlace` and `ResultPageRolePolicy.UsesBandBelowActions`. Nothing here replaces or weakens a
// workspace assertion: this file only adds, and `Program.Main` runs it after the file it complements.
internal static class Release11920OfficialRegressionScenarios
{
    // The numbers the official scenarios were written against: the host run the 1.19.20 release came from.
    private const float ScreenWidth = 2560f;
    private const float ScreenHeight = 1440f;
    private const float BoardFloorFromLog = 270f;
    private const int RowsFromLog = 30;

    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        TheKillTierRowsAreOnlySpentWhenTheyBuyAGlyph(check);
        TheRoomIsTheBoundAndTheCliffIsGone(check);
        TheBandPolicyIsAskedWithTheRoomFigure(check);
    }

    // ------------------------------------------------------- B: the kill-tier rows are a trade, not a default
    //
    // The split board is three kill rows rather than one, and those two extra rows only mean anything if they buy a
    // bigger glyph: `glyph = floor(room / rows / emPerGlyph)`. On a page with room to spare they buy nothing worth
    // having, so the board must not shed them. On a genuinely cramped page they can be worth a whole pixel of readable
    // text, and that is the only case in which this build collapses them.
    private static void TheKillTierRowsAreOnlySpentWhenTheyBuyAGlyph(Action<bool, string> check)
    {
        float emPerGlyph = ResultsBandFit.EmPerGlyphPixel(PanelDensity.Compact, 1.5f);
        check(Math.Abs(emPerGlyph - 1.5f) < 0.01f,
            $"one glyph pixel costs 1.5 row pixels at compact density: emPerGlyph={emPerGlyph:0.##}");
        check(ImpactBoard.RowCount(true) == 30 && ImpactBoard.RowCount(false) == 28,
            $"the split board is 30 rows and the collapsed one 28: {ImpactBoard.RowCount(true)}/{ImpactBoard.RowCount(false)}");

        // Every page that has room for a readable table keeps all 30 rows - the behaviour this build inherits.
        foreach (float room in new[] { 270f, 286f, 400f, 588f, 800f, 1080f })
            check(ImpactBoard.SplitKillTiersFor(room, emPerGlyph),
                $"a {room:0}px page has room to spare and keeps its three kill tiers");

        // A page too short for either board keeps them too: the rule only spends them when they buy something.
        foreach (float room in new[] { 100f, 135f, 150f, 180f, 200f })
            check(ImpactBoard.SplitKillTiersFor(room, emPerGlyph),
                $"a {room:0}px page gains nothing from two fewer rows and keeps its three kill tiers");

        // The cramped band the log printed: 269 px gives a 5 px glyph at 30 rows and a 6 px one at 28, so the rows are
        // spent - that is the page this rule is for.
        check(!ImpactBoard.SplitKillTiersFor(269f, emPerGlyph),
            "a 269px page buys a 5px -> 6px glyph with the two rows, so the collapsed board is chosen");

        // An unmeasured room is not a licence to change the board's content.
        check(ImpactBoard.SplitKillTiersFor(float.NaN, emPerGlyph)
              && ImpactBoard.SplitKillTiersFor(0f, emPerGlyph)
              && ImpactBoard.SplitKillTiersFor(-10f, emPerGlyph),
            "an unmeasured, zero or negative room keeps the richer board");
        check(ImpactBoard.SplitKillTiersFor(269f, float.NaN) && ImpactBoard.SplitKillTiersFor(269f, 0f),
            "an unmeasured glyph cost keeps the richer board");

        // The two boards hold the same fields; only the kill tiers are merged.
        check(ImpactBoard.MetricRowCount(true) - ImpactBoard.MetricRowCount(false) == 2,
            "collapsing the tiers costs exactly two metric rows");
    }

    // ------------------------------------------------------- the room is the bound, and the cliff is gone
    //
    // The host run this release came from, in the numbers the log printed: the success page measured a strip of 269 px
    // on one frame and 286 px on another, and the two frames ended in opposite answers - 269 px drew no board at all
    // (`success board deferred: need=270px ... have=240px`) while 286 px drew a 6 px table. Nothing about the page
    // changed between them except seventeen pixels, and the figure that decided it was a *readable* preference
    // (30 rows x 6 px x 1.5 em = 270 px) rather than the room the table can really be drawn in.
    //
    // `DrawableBoardHeight` answers the second question. These assertions pin its answers: the readable floor is
    // returned untouched when the room can hold it, the shortest drawable grid is returned when it cannot, and an
    // unmeasured room falls back to the readable floor so a page whose geometry could not be read behaves exactly as
    // it did before this existed.
    private static void TheRoomIsTheBoundAndTheCliffIsGone(Action<bool, string> check)
    {
        float readable = ResultsFontGrowth.MinimumBoardHeight(RowsFromLog, 1.5f, PanelDensity.Compact);
        check(Math.Abs(readable - BoardFloorFromLog) < 0.01f,
            $"the readable floor is still the {BoardFloorFromLog:0}px it always was: floor={readable:0.##}");

        // The shortest grid this build can draw: 30 rows at the 3 px absolute minimum, 1.5 em per row = 135 px.
        float shortest = ResultsBandFit.MinimumDrawableHeight(RowsFromLog, PanelDensity.Compact);
        check(Math.Abs(shortest - 135f) < 0.01f,
            $"the shortest grid this build can draw for {RowsFromLog} rows is 135px: shortest={shortest:0.##}");

        // The two strips the log printed, and the answer each of them now gets.
        float tight = ResultsFontGrowth.DrawableBoardHeight(RowsFromLog, 1.5f, PanelDensity.Compact, 269f);
        float roomy = ResultsFontGrowth.DrawableBoardHeight(RowsFromLog, 1.5f, PanelDensity.Compact, 286f);
        check(Math.Abs(tight - shortest) < 0.01f,
            $"a 286px strip is judged by the same room rule, so the two frames no longer disagree: floor={roomy:0.##}");
        check(tight <= 269f + 0.5f && roomy <= 286f + 0.5f,
            $"neither floor is taller than the strip that produced it: tight={tight:0.##} of 269, roomy={roomy:0.##} of 286");

        check(Math.Abs(roomy - readable) < 0.01f,
            $"a 286px strip can hold the readable table, so it keeps the 270px floor: floor={roomy:0.##}");
        // The two answers now differ by the real reason - 286 px can hold a readable table and 269 px cannot - instead
        // of by seventeen pixels straddling a preference. Both are placements; neither is a blank page.
        check(tight < roomy,
            $"the two frames no longer straddle a cliff: 269px asks for {tight:0.##}px and 286px asks for {roomy:0.##}px");
        check(tight <= 269f + 0.5f && roomy <= 286f + 0.5f,
            $"neither floor is taller than the strip that produced it: tight={tight:0.##} of 269, roomy={roomy:0.##} of 286");

        // The property the bug was about: a 269 px strip is no longer refused. The board that fits it is the shortest
        // grid, and the production placement accepts it in the strip the page measured.
        var strip = new BoardStrip(999f, 1268f, false, float.NaN, float.NaN, true, ResultFooterSource.ScreenSafeArea);
        check(Math.Abs((strip.Bottom - strip.Top) - 269f) < 0.01f,
            $"the strip is the 269px one the log printed: ({strip.Top:0}..{strip.Bottom:0})");
        check(ResultBoardHold.TryPlace(ScreenWidth, ScreenHeight, strip, 529f, shortest, 0f, out _),
            "the 269px strip that drew nothing now holds the shortest grid the board can be built at");

        // A room that can hold the readable table keeps it: the fix lowers the judgement figure, never the target.
        float large = ResultsFontGrowth.DrawableBoardHeight(RowsFromLog, 1.5f, PanelDensity.Compact, 588f);
        check(Math.Abs(large - readable) < 0.01f,
            $"a 588px strip is still judged against the 270px readable floor: floor={large:0.##}");

        // No measurement is not a licence to lower the bar.
        check(Math.Abs(ResultsFontGrowth.DrawableBoardHeight(RowsFromLog, 1.5f, PanelDensity.Compact, float.NaN) - readable) < 0.01f
              && Math.Abs(ResultsFontGrowth.DrawableBoardHeight(RowsFromLog, 1.5f, PanelDensity.Compact, 0f) - readable) < 0.01f
              && Math.Abs(ResultsFontGrowth.DrawableBoardHeight(RowsFromLog, 1.5f, PanelDensity.Compact, -40f) - readable) < 0.01f,
            "an unmeasured, zero or negative room keeps the readable floor, so an unreadable page behaves as it did before");

        // A band shorter than the shortest grid is still refused - by the real figure, not by a preference.
        check(Math.Abs(ResultsFontGrowth.DrawableBoardHeight(RowsFromLog, 1.5f, PanelDensity.Compact, 100f) - shortest) < 0.01f,
            "a 100px strip is judged against the shortest grid, so a refusal is about the room and not about a glyph preference");
        var cramped = new BoardStrip(400f, 500f, false, float.NaN, float.NaN, true, ResultFooterSource.ScreenSafeArea);
        check(!ResultBoardHold.TryPlace(ScreenWidth, ScreenHeight, cramped, 529f, shortest, 0f, out _),
            "a 100px strip still cannot hold a 135px grid: the floor was lowered to the room, not removed");
    }

    // ------------------------------------------------------- the band policy is asked with the room figure
    //
    // The six `Wiring` assertions this workspace's copy of `Release11916Scenarios.cs` does not carry: the figure the band
    // policy is asked with has to be the one resolved from the strip this page measured, and the escape hatch has to be
    // answerable at all - the run this came from measured an 85-88 px band against a 270 px readable floor, a comparison
    // no page could ever win.
    private static void TheBandPolicyIsAskedWithTheRoomFigure(Action<bool, string> check)
    {
        string results = Source("ResultsDisplay.cs");

        check(results.Contains("float stripFloor = area.Fits")
              && results.Contains("ResultsFontGrowth.DrawableBoardHeight(stripRows, stripRowEm, Plugin.Density, anchoredHeight)"),
            "the figure the band policy is asked with is resolved from the strip the page measured, not from the readable floor");

        // The policy fires exactly when the strip cannot hold the board *and* the band under the page's actions can:
        // 100 px of strip against a 135 px drawable grid, with a 200 px band beneath the controls, is that case.
        check(ResultPageRolePolicy.UsesBandBelowActions(ResultPageRole.Host, 100f, 135f, 200f),
            "a 100 px strip that cannot hold the board steps into a 200 px band when 135 px is the drawable floor");
        // A strip that already holds the board needs no escape hatch: the board simply stands in it.
        check(!ResultPageRolePolicy.UsesBandBelowActions(ResultPageRole.Host, 269f, 135f, 200f),
            "a 269 px strip that already holds the board is used as it is, without stepping around the page's actions");
        // The run this came from measured an 85-88 px band against a 135 px drawable grid, so on that page the answer
        // stays no - honestly. What this fixes is that the question is answerable at all.
        check(!ResultPageRolePolicy.UsesBandBelowActions(ResultPageRole.Host, 100f, 135f, 88f),
            "an 88 px band is refused against a 135 px drawable grid instead of against a 270 px preference");
        check(!ResultPageRolePolicy.UsesBandBelowActions(ResultPageRole.Host, 100f, 135f, 134f),
            "a band still shorter than the drawable floor is not offered: the floor was lowered to the room, not removed");
        check(!ResultPageRolePolicy.UsesBandBelowActions(ResultPageRole.Client, 100f, 135f, 1000f),
            "a client page never uses the host's band escape hatch, whatever band it has");
    }

    // The production source is found by walking up from this assembly until the plugin's own `.csproj` is beside it - the
    // same walk the other scenario files do, so this file works wherever the suite is run from.
    private static string Source(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj"))) directory = directory.Parent;
        if (directory == null) throw new InvalidOperationException("The plugin source directory could not be found from " + AppContext.BaseDirectory);
        string path = Path.Combine(directory.FullName, name);
        if (!File.Exists(path)) throw new FileNotFoundException("Production source is missing", path);
        return File.ReadAllText(path);
    }
}
