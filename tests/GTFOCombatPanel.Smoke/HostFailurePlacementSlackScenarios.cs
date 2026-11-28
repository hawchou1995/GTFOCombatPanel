using System;
using System.IO;
using HawChow.GTFOCombatPanel;

// The host failure page of the recorded 1.19.66 run (2560x1440, 4 players, `evidence-combatpanel-20261128-112653`,
// log 14693-14912): the fit placed its plate **2 px below the band it had just measured** and the draw wall refused
// that same plate on 49 consecutive repaints, so the page never drew one frame of board.
//
// Every number below is the record's, not a made-up one:
//
//   14693  influence board ... 960x540 px at (800,584) of 2560x1440 ... band=(504..1122), bandSource=NamedControl,
//          53px header
//   14694  draw board not drawn: reason=host-failure-button-wall ... plateBottom=1124, actionTop=1144
//   14912  the last of 49 such lines, four geometries: (504..1122, plateBottom=1124, actionTop=1144) 14x,
//          (504..1123, plateBottom=1125, actionTop=1144) 29x (+4 with have=618), one with actionTop=1145
//
// The band's foot is already `actionTop - ResultsBounds.Clearance(1440)` = 1144 - 21.333 = 1122.67, and the wall only
// tolerates `+ 0.5`, so the plate the fit produced (1122.67 + BoardSlack 2 = 1124.67) could never pass it. This suite
// asserts both halves of the answer: the *old* arithmetic is still refused by the real gate (so the gate was not
// loosened and the late-control protection still bites) and the *production* placement now stops on the band it
// measured. It calls the production placement (`ResultBoardHold.TryPlace`) and the production draw wall
// (`ResultHostFailureBand.Allows`) - not a copy of either - and then pins the call sites in `ResultsDisplay.cs`.
internal static class HostFailurePlacementSlackScenarios
{
    internal static void RunInto(Action<bool, string> check)
    {
        const float width = 2560f, height = 1440f, actionTop = 1144f, header = 53f, plateHeight = 540f;
        float clearance = ResultsBounds.Clearance(height);
        check(Math.Abs(clearance - 21.3333f) < 0.01f, "the 1440p design clearance is 21.33px (log 14019: clearance=21.3px)");

        // The page's own band: the scan's measured control at 504 on top, the native action button minus this page's
        // clearance underneath. `ResultPageLowerBoundPolicy` answers the walls exactly as the host branch of
        // `FailureStrip` asks for them (guestFailure=false, the native button as the pressable top, no text wall).
        var walls = ResultPageLowerBoundPolicy.Resolve(false, height, actionTop, float.NaN, float.NaN, ResultBoundSource.None);
        check(walls.PressableTop == actionTop && !walls.GuestFailure && !float.IsFinite(walls.PageFloor),
            "a host failure page's only lower wall is the native action button (no page floor, no text wall)");
        bool resolved = ResultsBounds.TryResolve(height, 504f - clearance, ResultBoundSource.MeasuredControl,
            walls.PressableTop, walls.FooterTextTop, walls.FooterTextSource, float.NaN, out ResultsBand band);
        check(resolved && Math.Abs(band.Top - 504f) < 0.01f && Math.Abs(band.Bottom - (actionTop - clearance)) < 0.01f,
            "the recorded band is reproduced: top=504, bottom=1122.67 (1144 - 21.33)");

        var strip = new BoardStrip(band.Top, band.Bottom, true, actionTop);
        float total = plateHeight + header; // 593: the record's 540px plate under its 53px header
        const float boardWidth = 960f;

        // ---- old: the fit took `BoardSlack` past the foot of the band it had just measured ---------------------------------
        bool oldPlaced = ResultBoardHold.TryPlace(width, height, strip, boardWidth, total, 0f,
            out LayoutRect oldPlate, 2f, bottomAligned: true);
        check(oldPlaced && Math.Abs(oldPlate.Bottom - (band.Bottom + 2f)) < 0.01f,
            "the old 2px slack reproduces the record's plateBottom=1124 out of a band ending at 1122.67");
        check(oldPlaced && !ResultHostFailureBand.Allows(oldPlate, actionTop, height),
            "and the real draw wall refuses it - the 49 refusals of log 14694-14912, reproduced");
        check(oldPlaced && !ResultHostFailureBand.Allows(oldPlate, actionTop + 1f, height),
            "the record's actionTop=1145 frames refuse the same plate too");

        // ---- new: the production placement for a host failure stops on the band's own foot -------------------------------
        float hostSlack = ResultBoardHold.PlacementSlack(true, 2f);
        check(hostSlack == 0f, "a host failure plate takes no slack past the band it measured");
        bool placed = ResultBoardHold.TryPlace(width, height, strip, boardWidth, total, 0f,
            out LayoutRect plate, hostSlack, bottomAligned: true);
        check(placed && Math.Abs(plate.Bottom - band.Bottom) < 0.01f,
            "the new plate stops on the band's own foot (1122.67, not 1124.67)");
        check(placed && Math.Abs(plate.Y - (band.Bottom - total)) < 0.01f && plate.Width == boardWidth,
            "and it keeps the recorded size and bottom-aligned origin (960 wide, top = foot - 593)");
        check(placed && ResultHostFailureBand.Allows(plate, actionTop, height),
            "the real draw wall now allows the recorded host failure plate (this is the draw that never happened)");
        check(placed && ResultHostFailureBand.Allows(plate, actionTop + 1f, height),
            "a button that reads one pixel lower (the record's actionTop=1145) allows it as well");

        // The record's fourth geometry - a frame whose band and button both read lower - must behave the same, because
        // the strip's foot and the wall come from one reading. This is the frame-to-frame variation, not a new rule.
        bool everyDriftPasses = true;
        for (int step = -6; step <= 6; step++)
        {
            float drift = step * 0.1f;
            var frameStrip = new BoardStrip(band.Top, band.Bottom + drift, true, actionTop + drift);
            if (!ResultBoardHold.TryPlace(width, height, frameStrip, boardWidth, total, 0f,
                    out LayoutRect framePlate, ResultBoardHold.PlacementSlack(true, 2f), bottomAligned: true)
                || !ResultHostFailureBand.Allows(framePlate, actionTop + drift, height))
                everyDriftPasses = false;
        }
        check(everyDriftPasses,
            "every sub-pixel drift of the record's 1122/1123 and 1144/1145 readings places a plate the wall still allows");

        // ---- a normal allow, unchanged -----------------------------------------------------------------------------------
        bool shortPlaced = ResultBoardHold.TryPlace(width, height, strip, boardWidth, 300f, 0f,
            out LayoutRect shortPlate, hostSlack, bottomAligned: true);
        check(shortPlaced && ResultHostFailureBand.Allows(shortPlate, actionTop, height),
            "a host failure plate that fits well above the wall is allowed, as before");

        // ---- the late control still refuses ------------------------------------------------------------------------------
        check(placed && !ResultHostFailureBand.Allows(plate, actionTop - 2f, height),
            "a button that arrives 2px higher after the plate was placed still refuses it - the late-control protection is unchanged");
        check(placed && !ResultHostFailureBand.Allows(plate, float.NaN, height),
            "a lost button reading still keeps the held plate from drawing");
        check(!ResultHostFailureBand.Allows(default, actionTop, height),
            "a plate with no height is never allowed");

        // The wall itself was not widened: it still answers at half a pixel across the line and refuses past it.
        float wall = actionTop - clearance;
        check(ResultHostFailureBand.Allows(new LayoutRect(0f, wall - 100f + 0.4f, boardWidth, 100f), actionTop, height)
            && !ResultHostFailureBand.Allows(new LayoutRect(0f, wall - 100f + 1.2f, boardWidth, 100f), actionTop, height),
            "the draw wall still answers at the same half-pixel tolerance - the placement moved, the gate did not");

        // ---- other pages keep the tolerance they had ----------------------------------------------------------------------
        check(ResultBoardHold.PlacementSlack(false, 2f) == 2f,
            "success and client pages keep the 2px placement tolerance they had");
        check(ResultBoardHold.PlacementSlack(false, float.NaN) == 0f,
            "an unreadable slack is still no slack");
        var successStrip = new BoardStrip(315f, 1274f, true, 1287f);
        bool successPlaced = ResultBoardHold.TryPlace(width, height, successStrip, 1297f, 720f, 0f,
            out LayoutRect successPlate, ResultBoardHold.PlacementSlack(false, 2f), bottomAligned: true);
        check(successPlaced && Math.Abs(successPlate.Y - 556f) < 0.01f && Math.Abs(successPlate.Bottom - 1276f) < 0.01f,
            "the recorded success plate is unchanged (log 7954: 1297x720 at (631,556) in a 315..1274 band)");

        // ---- the call sites, pinned in the production file itself ---------------------------------------------------------
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "ResultsDisplay.cs"))) root = root.Parent;
        check(root != null, "the smoke suite can inspect the display call sites");
        if (root == null) return;
        string display = File.ReadAllText(Path.Combine(root.FullName, "ResultsDisplay.cs"));
        string fallback = File.ReadAllText(Path.Combine(root.FullName, "FallbackLayout.cs"));
        // The three placements that can produce a host failure plate: the first placement in Draw, the standing-board
        // re-fit, and the fit. All three ask the one policy, with the page's own role.
        check(CountOf(display, "out LayoutRect fitted, ResultBoardHold.PlacementSlack(!_succeeded && !GuestClient(), BoardSlack)") == 1,
            "the draw's first placement takes a host failure plate's slack from the page's role");
        check(CountOf(display, "out LayoutRect candidate, ResultBoardHold.PlacementSlack(!_succeeded && !GuestClient(), BoardSlack)") == 1,
            "the standing-board re-fit takes it the same way");
        check(CountOf(display, "out LayoutRect plate, ResultBoardHold.PlacementSlack(!succeeded && !GuestClient(), BoardSlack)") == 1,
            "and so does the fit that first measures the plate");
        check(CountOf(display, "out LayoutRect plate, BoardSlack, bottomAligned: true))") == 1,
            "the client's own move-into-a-tighter-band path keeps its raw slack (client behaviour unchanged)");
        check(CountOf(display, "ResultHostFailureBand.Allows(_holdBoard, actionTop, Screen.height)") == 1
            && CountOf(display, "if (hostFailure) _holdValid = false;") == 1,
            "the draw path still asks the wall before it paints and still drops an unproven fail-open hold");
        check(fallback.Contains("plate.Bottom <= nativeButtonTop - ResultsBounds.Clearance(screenHeight) + 0.5f"),
            "the host failure wall is untouched: same edge, same half-pixel tolerance");
    }

    private static int CountOf(string text, string needle)
    {
        int count = 0, at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
        return count;
    }
}
