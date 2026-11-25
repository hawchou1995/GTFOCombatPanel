using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// ---------------------------------------------------------------------------------------------------
// Release11950Scenarios.cs - the three findings of the independent review of 1.19.49.
//
//   1. `TryFallbackSpot` reads `_obstacles`, and `ForgetBandRound` did not clear it. The committed band has its own window
//      guard (`_anchorScreenWidth == Screen.width`), the *rows* never had one: after a new level, a configuration reload or a
//      resolution change - and before the next successful read - the conservative place was chosen against rectangles from a
//      scene that is no longer loaded (and, after a resize, in the previous window's pixels), and the line counted them as
//      "known row(s)". The fix is one statement in the round reset; the *hazard* is replayed below on the production
//      `HudFallbackSpot` with the recorded coordinates, so the regression shows what the stale list would answer.
//   2. `ReportFallbackSpot` printed the constant `HudFallbackSpot.Margin`, while `TryResolve` keeps
//      `min(Margin, free/2 ...)` - so on a narrow window or under a tall table the line stated a margin the place never had.
//      The fix is one shared calculation (`HudFallbackSpot.EffectiveMargin`), used by the placement and by the line; here it
//      is asserted runnably, including the 1400 px table on the recorded 1440 px window.
//   3. The absence vocabulary has ten reachable keys and the ledger held eight, so the ninth and tenth first lines - and the
//      deferral is the only one that says "this poll drew nothing" - could be silently refused. The fix is a closed vocabulary
//      whose size *is* the bound (`HudAbsence.Capacity`), and a key outside it is refused rather than counted. Asserted
//      runnably: all ten claim true in the worst order, the eleventh does not, and repeats stay suppressed.
//
// `HudFallbackSpot`, `HudAbsence`, `HudAbsenceLedger`, `HudObstacle` and `EquipmentBand` are production types compiled into
// this suite. `PanelBehaviour` is not linked here (it binds Unity and Il2Cpp), so the two wiring facts - the reset clearing
// `_obstacles`, and the line printing `EffectiveMargin` - are asserted against that file's source text, the same split every
// HUD suite in this project uses.
internal static partial class Program
{
    private static void RunRelease11950Scenarios()
    {
        TheRoundResetDropsThePreviousLevelsRows();
        TheReportedMarginIsTheMarginTheTableKept();
        EveryAbsenceReasonCanStillWriteItsFirstLine();
        TheVersionCarrierIsTheReviewedBuild();
    }

    // ---------- 1. the round owns the obstacle list ----------

    private static void TheRoundResetDropsThePreviousLevelsRows()
    {
        string panel = ProductionText("PanelBehaviour.cs");
        string reset = Segment(panel, "private void ForgetBandRound()", "private void Update()");
        Check(reset.Length > 0 && reset.Contains("_obstacles.Clear();"),
            "the round reset drops the rows of the round before it: `_obstacles.Clear();` is in ForgetBandRound");

        // Two clears only: one at the start of a read (that read is the round's own evidence) and this one at the reset. A
        // third would mean something else thinks it owns the list; none would mean the hazard below is live again.
        Check(Tally(panel, "_obstacles.Clear();") == 2,
            "exactly two clears - the start of a read and the round reset - so the list belongs to one round");
        Check(Segment(panel, "private EquipmentBandReading ReadEquipmentBand(", "Camera camera = _bandCamera;")
                .Contains("_obstacles.Clear();"),
            "and the read still starts from an empty buffer, so a row is never counted twice");

        // The committed band keeps its own, separate guard; that is what the rows lacked.
        string fallback = Segment(panel, "private bool TryFallbackSpot(", "private void ReportFallbackSpot(");
        Check(fallback.Contains("if (_bandWatch.Has && _anchorScreenWidth == Screen.width && _anchorScreenHeight == Screen.height)")
              && fallback.Contains("for (int index = 0; index < _obstacles.Count; index++)"),
            "the band is guarded by the window it was measured on while the rows were read straight out of the list - the gap "
                + "the reset now closes before the next poll can ask");

        // What the stale list would do, on the production place-finder and the recorded 2K page. The previous level's column
        // (the recorded one, x=2336..2546) and the new level's column (a different HUD, x=1200..1560, higher up) with the
        // player's own row at 300: the stale rows sit where the new page has nothing, so they move the place.
        var previousLevel = new[]
        {
            new HudObstacle("flashlight", new LayoutRect(2336.08f, 294.33f, 210.59f, 110.16f), drawn: true),
            new HudObstacle("slot:GearStandard", new LayoutRect(2336f, 404f, 210f, 52f), drawn: true),
            new HudObstacle("slot:GearSpecial", new LayoutRect(2336f, 456f, 210f, 48f), drawn: true)
        };
        var thisLevel = new[]
        {
            new HudObstacle("flashlight", new LayoutRect(1200f, 120f, 210f, 110f), drawn: true)
        };
        var staleObstacles = Usable(previousLevel);
        var currentObstacles = Usable(thisLevel);
        Check(staleObstacles.Count == 3 && currentObstacles.Count == 1,
            "the two rounds describe different columns: " + staleObstacles.Count + " row(s) inherited, "
                + currentObstacles.Count + " row(s) really read");
        bool stale = HudFallbackSpot.TryResolve(300f, staleObstacles, 459f, 60f, 2560f, 1440f,
            out LayoutRect staleSpot, out string staleWhy, out _);
        bool fresh = HudFallbackSpot.TryResolve(300f, currentObstacles, 459f, 60f, 2560f, 1440f,
            out LayoutRect freshSpot, out string freshWhy, out _);
        // The player's own row (300) is free of this level's column but sits inside the inherited one, so the inherited list
        // answers "below what was measured" (y=512, under the old column's bottom at 504) while this level's own row answers the
        // conservative place at the player's row. That difference *is* the hazard the round reset removes.
        Check(stale && staleWhy == "below-measured" && Near(staleSpot.Y, 512f),
            "the inherited rows push the place below a column that is not on this page: y="
                + staleSpot.Y.ToString("0.##") + " (" + staleWhy + ")");
        Check(fresh && freshWhy == "right-row" && Near(freshSpot.Y, 300f),
            "while this round's own single row leaves the player's row in place: y="
                + freshSpot.Y.ToString("0.##") + " (" + freshWhy + ")");
        Check(!Near(staleSpot.Y, freshSpot.Y),
            "so the same page is answered 212 px apart (" + staleSpot.Y.ToString("0.##") + " vs "
                + freshSpot.Y.ToString("0.##") + ") purely by what the previous round left behind");
        Check(fresh && Near(freshSpot.Right, 2560f - HudFallbackSpot.Margin),
            "and with only this round's rows the place is the right margin the rest of the suite asserts");

        // The line's own count is affected the same way: `clearOf=` reports how many rectangles the place was judged against,
        // and the inherited list would have reported three rows on a page where this round read one.
        Check(staleObstacles.Count != currentObstacles.Count,
            "and the inherited list would be counted in the line itself: clearOf=" + staleObstacles.Count
                + " against clearOf=" + currentObstacles.Count);
    }

    private static List<LayoutRect> Usable(IReadOnlyList<HudObstacle> rows)
    {
        var obstacles = new List<LayoutRect>();
        for (int index = 0; index < rows.Count; index++)
        {
            HudObstacle row = rows[index];
            if (!row.Drawn || !row.Measured) continue;
            LayoutRect rect = row.Rect;
            if (!(rect.Width > 1f) || !(rect.Height > 1f)) continue;
            if (!float.IsFinite(rect.X) || !float.IsFinite(rect.Y)) continue;
            obstacles.Add(rect);
        }
        return obstacles;
    }

    // ---------- 2. the margin the line prints is the margin the place kept ----------

    private static void TheReportedMarginIsTheMarginTheTableKept()
    {
        // The recorded page: the table keeps all 24 px, so the constant happened to be right there.
        Check(Near(HudFallbackSpot.EffectiveMargin(459f, 120f, 2560f, 1440f), HudFallbackSpot.Margin),
            "the recorded 2560x1440 page keeps the full margin, which is why the old line looked correct");

        // The two cases where it did not: a tall table on a 1440 px window, and a narrow window.
        float tall = HudFallbackSpot.EffectiveMargin(459f, 1400f, 2560f, 1440f);
        float narrow = HudFallbackSpot.EffectiveMargin(459f, 120f, 500f, 720f);
        Check(Near(tall, 20f) && Near(narrow, 20.5f),
            "a 1400 px table on a 1440 px window keeps 20 px and a 500 px window 20.5 px, not the constant: tall="
                + tall.ToString("0.##") + " narrow=" + narrow.ToString("0.##"));
        Check(!Near(tall, HudFallbackSpot.Margin) && !Near(narrow, HudFallbackSpot.Margin),
            "so a line that printed `Margin` described a margin neither page had");

        // The place itself is right-aligned by that same figure - one calculation, not two.
        HudFallbackSpot.TryResolve(100f, Array.Empty<LayoutRect>(), 459f, 1400f, 2560f, 1440f,
            out LayoutRect tallPlace, out string tallWhy, out _);
        Check(tallWhy == "right-row" && Near(tallPlace.Right, 2560f - tall),
            "and `TryResolve` right-aligns by `EffectiveMargin`, so the placement and the report cannot disagree: right="
                + tallPlace.Right.ToString("0.##"));
        HudFallbackSpot.TryResolve(100f, Array.Empty<LayoutRect>(), 459f, 120f, 500f, 720f,
            out LayoutRect narrowPlace, out string narrowWhy, out _);
        Check(narrowWhy == "right-row" && Near(narrowPlace.Right, 500f - narrow) && narrowPlace.X > 0f,
            "the narrow window keeps the same figure: right=" + narrowPlace.Right.ToString("0.##")
                + " left=" + narrowPlace.X.ToString("0.##"));

        // A table that only just fits keeps nothing, and nothing is what the line says.
        Check(Near(HudFallbackSpot.EffectiveMargin(2560f, 1440f, 2560f, 1440f), 0f),
            "a table exactly the size of the window keeps no margin at all");
        Check(Near(HudFallbackSpot.EffectiveMargin(float.NaN, 120f, 2560f, 1440f), 0f),
            "and a size that cannot be read answers zero rather than a number nobody measured");

        // The production line uses it, and no longer spells the constant.
        string panel = ProductionText("PanelBehaviour.cs");
        string report = Segment(panel, "private void ReportFallbackSpot(", "private void DeferFallback(");
        Check(report.Contains("HudFallbackSpot.EffectiveMargin(spot.Width, spot.Height, Screen.width, Screen.height)"),
            "`ReportFallbackSpot` prints the effective margin of the rectangle it just placed");
        Check(!report.Contains("HudFallbackSpot.Margin.ToString"),
            "and the constant is no longer printed anywhere in that line");
        Check(ProductionText("HudAvoidance.cs").Contains("float margin = EffectiveMargin(width, height, screenWidth, screenHeight);"),
            "`TryResolve` takes its margin from the same function - one calculation shared by the placement and the report");
    }

    // ---------- 3. every reason can still write its first line ----------

    private static void EveryAbsenceReasonCanStillWriteItsFirstLine()
    {
        Check(HudAbsence.Capacity == 10 && HudAbsence.Capacity == HudAbsence.SpotReasons.Length + 5,
            "the vocabulary is ten keys - three anchor answers, two deferrals and the five spot reasons: " + HudAbsence.Capacity);

        // The worst order for an eight-slot ledger: the seven keys a busy round produces first, then the anchor page's own
        // "no free rung", and only then the deferral - the one line that says this poll drew nothing.
        string[] firstLines =
        {
            HudAbsence.AnchorUnmeasured, HudAbsence.AnchorPartial,
            HudAbsence.Spot("right-row"), HudAbsence.Spot("below-measured"), HudAbsence.Spot("above-measured"),
            HudAbsence.Spot("right-column"), HudAbsence.Spot("least-overlap"), HudAbsence.AnchorNoFreePlace,
            HudAbsence.DeferOverlap, HudAbsence.DeferIncomplete
        };
        var ledger = new HudAbsenceLedger();
        int reported = 0;
        for (int index = 0; index < firstLines.Length; index++)
        {
            bool claimed = ledger.Claim(firstLines[index]);
            if (claimed) reported++;
            Check(claimed, "reason " + (index + 1) + " of " + firstLines.Length + " still writes its first line: "
                + firstLines[index]);
        }
        Check(reported == HudAbsence.Capacity && ledger.Count == HudAbsence.Capacity,
            "all ten reasons are reportable through one ledger - which is what a cap of eight refused: the ninth and tenth "
                + "first lines, the deferral among them (" + reported + " of " + firstLines.Length + ")");

        // Suppression is still bounded and still per round.
        bool repeated = false;
        for (int index = 0; index < firstLines.Length; index++) repeated |= ledger.Claim(firstLines[index]);
        Check(!repeated, "every one of them is silent the second time it is asked");
        Check(!ledger.Claim(HudAbsence.Spot("right-row")), "a repeat inside the round writes nothing");
        Check(!ledger.Claim("spot:left-row") && !ledger.Claim("anchor:whatever") && !ledger.Claim("")
              && !ledger.Claim(null) && !ledger.Claim(HudAbsence.Spot(null)),
            "and nothing outside the vocabulary can claim a slot - not a stale reason name, not an invented one, not a null");
        Check(ledger.Count == HudAbsence.Capacity,
            "so the eleven-slot claim changed nothing: the round still holds exactly its ten lines");

        // A new round is the only thing that re-arms them, and it re-arms all of them.
        ledger.Clear();
        Check(ledger.Count == 0 && !ledger.Any, "the round reset empties the ledger");
        bool refilled = true;
        for (int index = 0; index < firstLines.Length; index++) refilled &= ledger.Claim(firstLines[index]);
        Check(refilled, "and the next round can write every one of them again");

        // The vocabulary is closed in both directions: the five spot reasons are the five `HudFallbackSpot` can answer, and
        // `none` - the value left behind by a refusal - is not one of them, so a caller cannot report a place nobody placed.
        foreach (string reason in HudAbsence.SpotReasons)
            Check(HudAbsence.IsSpotReason(reason), "`" + reason + "` is one of the spot reasons");
        Check(!HudAbsence.IsSpotReason("none") && !HudAbsence.IsSpotReason(null)
              && !HudAbsence.IsKnown("none"),
            "`none` is not a reason and not a key: a refused `TryResolve` never becomes a line");
        string hud = ProductionText("HudAvoidance.cs");
        foreach (string reason in HudAbsence.SpotReasons)
            Check(hud.Contains("reason = \"" + reason + "\""),
                "and `HudFallbackSpot` really answers `" + reason + "`, so the vocabulary matches the class it describes");

        // The panel uses that ledger and nothing else.
        string panel = ProductionText("PanelBehaviour.cs");
        Check(panel.Contains("private readonly HudAbsenceLedger _absence = new HudAbsenceLedger();")
              && Segment(panel, "private void ForgetBandRound()", "private void Update()").Contains("_absence.Clear();")
              && Tally(panel, "_absence.Clear();") == 1,
            "the panel owns one ledger and clears it with the round");
    }

    // ---------- 4. the reviewed build's number ----------

    private static void TheVersionCarrierIsTheReviewedBuild()
    {
        string info = ProductionText("PluginInfo.cs");
        const string carrierMark = "internal const string Version = \"";
        int at = info.IndexOf(carrierMark);
        string version = at < 0 ? string.Empty
            : info.Substring(at + carrierMark.Length,
                info.IndexOf('"', at + carrierMark.Length) - at - carrierMark.Length);
        Check(version.Length > 0 && char.IsDigit(version[0]) && version != "1.19.49",
            "this review's carrier is its own number, not the 1.19.49 it reviews: " + version);
        // 1.19.51 keeps this file's claim rather than pinning a number a later bump would have to edit: the point
        // is that one number is carried by every surface, and that it is not the reviewed build's number.
        Check(ProductionText(Path.Combine("Thunderstore", "manifest.json")).Contains("\"version_number\": \"" + version + "\""),
            "the manifest carries it");
        Check(ProductionText(Path.Combine("Thunderstore", "CHANGELOG.md")).Contains("## " + version),
            "the changelog has this build's own section, ahead of 1.19.49's");
        string readme = ProductionText("README.md");
        Check(readme.Contains("源码版本为 **" + version + "**"),
            "and the README's current state names it");
        Check(readme.Contains("1.19.49 是只做过托管验证的评审构建"),
            "the README says plainly that 1.19.49 was a review build - never packaged, never published - so it cannot be read "
                + "as a released version");
    }
}
