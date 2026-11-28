using System;
using System.IO;
using HawChow.GTFOCombatPanel;

// 1.19.68: the recorded 2560x1440 host success page (`snap_test_20261128_132100`, log 7194-7226 and 7846-7980)
// placed its board 2 px below the edge of the window and every repaint refused it, so that page never drew one
// frame of table. The numbers are the record's, not made up:
//
//   7195  success band: top=874 ... bottom=1432 (source=screen-foot), room=558px, screen=2560x1440, role=host
//   7198  the board entered early on strip=(874..1432) while the page's native reveal was still running
//   7224  influence board ... 874x495 px at (843,939), band=(846..1432), bandSource=InteractiveBand,
//         blankAbove=93px, blankBelow=0px, 53px header  -> the whole board is 548 px (495 plate + 53 header)
//   7225  success-strip board not drawn: reason=grid-would-reach-edge, top=846, bottom=1432,
//         detail=boardBottom=1434,visibleHeight=548,heldStripBottom=1432
//   7850-7980  the same 1434 against narrower strips (967/976/964/960/955/954..1432) and smaller boards
//         (413, 458) - every one of them refused for the same 2 px
//
// The arithmetic is one line in `ResultBoardHold.TryPlace`: `min(strip.Bottom, screenHeight - 8) + slack`. A strip
// whose foot is only the window's own edge therefore ends at 1432 + BoardSlack 2 = 1434, while the checks that judge
// the standing plate (`HeldGridFits`, and `BoardStillSafe` through it) take `screenHeight - 8` as a hard limit with no
// tolerance at all. The placement accepts a plate the very next gate refuses - on every repaint.
//
// What this file pins, through the production functions (`ResultBoardHold.TryPlace`, `ResultBoardHold.HeldGridFits`,
// `ResultChromeLayout.PlateInsideBand`) and the production call sites in `ResultsDisplay.cs`:
//
//   * the old branch (no cap, i.e. the arithmetic 1.19.67 shipped) still reproduces the record: bottom=1434 into a
//     strip ending at 1432, and the production gate still refuses it - the gate was not loosened;
//   * the host success placement now stops on the window's edge (1432) and that same gate accepts it, for the
//     recorded 846..1432 / 548 board and for the later 964..1432 / 413 boards;
//   * a strip that ends *on a page element* (the measured footer text at 1287 of the 1.19.67 record, or a band at
//     1400) is placed bit-for-bit as before - the cap is inert there;
//   * a board that cannot stand inside the window's edge is refused, never leaned past it;
//   * a button that arrives later still vetoes the plate, and the failure page / a client call site keep the exact
//     arithmetic they had (the cap is asked for by `succeeded && !GuestClient()` only).
internal static class Release11968Scenarios
{
    internal static void RunInto(Action<bool, string> check)
    {
        const float width = 2560f, height = 1440f, screenFoot = height - 8f; // 1432
        const float header = 53f, plateHeight = 495f;
        const float boardWidth = 874f;
        float total = plateHeight + header; // 548: the record's visibleHeight

        check(Math.Abs(screenFoot - 1432f) < 0.01f, "the 1440p window's own foot is 1432 (log 7195: bottom=1432 source=screen-foot)");

        // The strip the record placed into, and the one it was judged against - the page's own band, no button under it.
        var strip = new BoardStrip(846f, screenFoot, false, float.NaN, float.NaN, true, ResultFooterSource.InteractiveBand);
        var current = new BoardStrip(846f, screenFoot, false, float.NaN, float.NaN, true, ResultFooterSource.InteractiveBand);
        float slack = ResultBoardHold.PlacementSlack(false, 2f); // a host success page keeps BoardSlack
        check(slack == 2f, "a host success placement still asks for the 2px tolerance (PlacementSlack is not changed)");

        // ---- the old branch: the record's own refusal, reproduced through the production gate -----------------------
        bool oldPlaced = ResultBoardHold.TryPlace(width, height, strip, boardWidth, total, 0f,
            out LayoutRect oldBoard, slack, bottomAligned: true);
        check(oldPlaced && Math.Abs(oldBoard.Bottom - 1434f) < 0.01f && Math.Abs(oldBoard.Y - 886f) < 0.01f,
            "the old arithmetic reproduces the record: a 548px board bottom-aligned to 1434 at y=886 (log 7225: boardBottom=1434)");
        check(oldPlaced && !ResultBoardHold.HeldGridFits(strip, current, oldBoard, oldBoard.Height, height, slack),
            "and the production per-repaint check refuses exactly that plate - log 7225's grid-would-reach-edge, unchanged");
        check(oldPlaced && ResultChromeLayout.PlateInsideBand(oldBoard, strip.Top, strip.Bottom, height, slack),
            "the band check alone would have let it through - which is why the 1432 cap is the edge that has to bind");

        // ---- the new placement: a host success strip whose foot is the window's edge stops there -------------------
        bool placed = ResultBoardHold.TryPlace(width, height, strip, boardWidth, total, 0f,
            out LayoutRect board, slack, bottomAligned: true, screenEdgeCeiling: true);
        check(placed && Math.Abs(board.Bottom - screenFoot) < 0.01f,
            "the host success board now ends on the window's own edge (1432), not 2px past it (1434)");
        check(placed && Math.Abs(board.Y - 884f) < 0.01f && Math.Abs(board.Width - boardWidth) < 0.01f
            && Math.Abs(board.Height - total) < 0.01f,
            "and it keeps the recorded size, bottom-aligned: 874x548 at y=884 (the plate's own origin moves 2px up with it)");
        check(placed && ResultBoardHold.HeldGridFits(strip, current, board, board.Height, height, slack),
            "the per-repaint check now accepts the placement - this is the draw the recorded page never got");
        check(placed && ResultChromeLayout.PlateInsideBand(board, strip.Top, strip.Bottom, height, slack),
            "and the drawn plate still sits inside its own band (846..1432)");

        // ---- the record's later, narrower strips: same 1434, same 2 px -----------------------------------------------
        // log 7850/7913/7919: heldStrip=964..1432 with a 413px board (360 plate + 53 header) placed at y=1021.
        const float laterTotal = 413f;
        var laterStrip = new BoardStrip(964f, screenFoot, false, float.NaN, float.NaN, true, ResultFooterSource.InteractiveBand);
        var laterCurrent = new BoardStrip(964f, screenFoot, false, float.NaN, float.NaN, true, ResultFooterSource.InteractiveBand);
        bool laterOld = ResultBoardHold.TryPlace(width, height, laterStrip, boardWidth, laterTotal, 0f,
            out LayoutRect laterOldBoard, slack, bottomAligned: true);
        check(laterOld && Math.Abs(laterOldBoard.Bottom - 1434f) < 0.01f && Math.Abs(laterOldBoard.Y - 1021f) < 0.01f,
            "the second recorded geometry reproduces too: a 413px board at y=1021 ending at 1434 (log 7850)");
        check(laterOld && !ResultBoardHold.HeldGridFits(laterStrip, laterCurrent, laterOldBoard, laterTotal, height, slack),
            "and it is refused by the same check on every one of the record's repaints");
        bool laterPlaced = ResultBoardHold.TryPlace(width, height, laterStrip, boardWidth, laterTotal, 0f,
            out LayoutRect laterBoard, slack, bottomAligned: true, screenEdgeCeiling: true);
        check(laterPlaced && Math.Abs(laterBoard.Bottom - screenFoot) < 0.01f && Math.Abs(laterBoard.Y - 1019f) < 0.01f,
            "the same strip now places the 413px board at y=1019, ending on 1432");
        check(laterPlaced && ResultBoardHold.HeldGridFits(laterStrip, laterCurrent, laterBoard, laterTotal, height, slack),
            "and the narrowed strip accepts it, so the wheel/board of that page is no longer refused for 2px");

        // The tightly fitted frame: the board is exactly as tall as the room under the window's edge. It must still
        // place, on the edge, instead of being refused for the 2px it would have taken past it.
        var flushStrip = new BoardStrip(884f, screenFoot, false, float.NaN, float.NaN, true, ResultFooterSource.InteractiveBand);
        bool flushPlaced = ResultBoardHold.TryPlace(width, height, flushStrip, boardWidth, total, 0f,
            out LayoutRect flushBoard, slack, bottomAligned: true, screenEdgeCeiling: true);
        check(flushPlaced && Math.Abs(flushBoard.Y - 884f) < 0.01f && Math.Abs(flushBoard.Bottom - screenFoot) < 0.01f,
            "a board exactly as tall as the room under the edge places on the edge (y=884, bottom=1432)");

        // ---- an impossible strip is refused, never leaned past the edge ----------------------------------------------
        var tightStrip = new BoardStrip(1200f, screenFoot, false, float.NaN, float.NaN, true, ResultFooterSource.InteractiveBand);
        check(!ResultBoardHold.TryPlace(width, height, tightStrip, boardWidth, total, 0f, out _,
                slack, bottomAligned: true, screenEdgeCeiling: true),
            "a 548px board cannot stand between 1200 and the window's edge, and the capped placement refuses it");
        check(!ResultBoardHold.TryPlace(width, height, tightStrip, boardWidth, total, 0f, out _,
                slack, bottomAligned: true),
            "the old arithmetic refuses that strip too - the cap does not invent an answer");

        // ---- a strip that ends on a page element keeps its exact geometry -------------------------------------------
        // The 1.19.67 run's real footer text control gave its success page a measured foot (`freeze_20261128_122457`,
        // log 13809), and that page's plate is the recorded 1297x720 at (631,556) in a 315..1274 band. That foot is a
        // *page element*; the cap may not touch it, so the plate has to come back identical.
        var measuredStrip = new BoardStrip(315f, 1274f, true, 1287f, 40f, true, ResultFooterSource.NamedControl);
        var measuredCurrent = new BoardStrip(315f, 1274f, true, 1287f, 40f, true, ResultFooterSource.NamedControl);
        const float measuredWidth = 1297f, measuredHeight = 720f;
        bool measuredOld = ResultBoardHold.TryPlace(width, height, measuredStrip, measuredWidth, measuredHeight, 0f,
            out LayoutRect measuredOldBoard, slack, bottomAligned: true);
        bool measuredNew = ResultBoardHold.TryPlace(width, height, measuredStrip, measuredWidth, measuredHeight, 0f,
            out LayoutRect measuredNewBoard, slack, bottomAligned: true, screenEdgeCeiling: true);
        check(measuredOld && measuredNew && Same(measuredOldBoard, measuredNewBoard)
            && Math.Abs(measuredNewBoard.Y - 556f) < 0.01f && Math.Abs(measuredNewBoard.Bottom - 1276f) < 0.01f,
            "the recorded real-footer success plate is bit-for-bit unchanged (1297x720 at y=556, foot 1274 + the same 2px)");
        check(measuredNew && ResultBoardHold.HeldGridFits(measuredStrip, measuredCurrent, measuredNewBoard, measuredNewBoard.Height, height, slack),
            "and that plate still passes the per-repaint check, as it did before this change");
        // Any strip that ends above the window's edge is untouched: 1400 is a page element, not the screen's foot.
        var nearEdge = new BoardStrip(846f, 1400f, false, float.NaN, float.NaN, true, ResultFooterSource.InteractiveBand);
        check(ResultBoardHold.TryPlace(width, height, nearEdge, boardWidth, total, 0f, out LayoutRect nearOld, slack, bottomAligned: true)
            && ResultBoardHold.TryPlace(width, height, nearEdge, boardWidth, total, 0f, out LayoutRect nearNew, slack,
                bottomAligned: true, screenEdgeCeiling: true)
            && Same(nearOld, nearNew) && Math.Abs(nearNew.Bottom - 1402f) < 0.01f,
            "a strip whose foot is a page element at 1400 is placed by the same arithmetic with or without the cap (1402)");

        // ---- review F1: the strip's foot does not have to read at or past the edge -----------------------------------
        // The cap is the window's own edge, not a comparison against the strip's own foot. A foot 1 px, half a pixel or
        // the smallest float step under 1432 takes the same 2 px past the edge, and the production gate refuses every
        // one of those plates - which is what the first revision's `strip.Bottom >= screenFoot` guard let through.
        float[] nearFeet = { screenFoot - 1f, screenFoot - 0.5f, MathF.BitDecrement(screenFoot) };
        int nearCappedOnEdge = 0, nearAccepted = 0, nearUncappedOverflowed = 0, nearUncappedRefused = 0;
        foreach (float foot in nearFeet)
        {
            var nearStrip = new BoardStrip(846f, foot, false, float.NaN, float.NaN, true, ResultFooterSource.InteractiveBand);
            var nearCurrent = new BoardStrip(846f, foot, false, float.NaN, float.NaN, true, ResultFooterSource.InteractiveBand);
            // What the first revision placed for this foot: min(foot, 1432) + 2 with no cap, because foot is under 1432.
            bool uncappedPlaced = ResultBoardHold.TryPlace(width, height, nearStrip, boardWidth, total, 0f,
                out LayoutRect uncapped, slack, bottomAligned: true);
            if (uncappedPlaced && uncapped.Bottom > screenFoot) nearUncappedOverflowed++;
            if (uncappedPlaced && !ResultBoardHold.HeldGridFits(nearStrip, nearCurrent, uncapped, uncapped.Height, height, slack))
                nearUncappedRefused++;
            bool cappedPlaced = ResultBoardHold.TryPlace(width, height, nearStrip, boardWidth, total, 0f,
                out LayoutRect capped, slack, bottomAligned: true, screenEdgeCeiling: true);
            if (cappedPlaced && Math.Abs(capped.Bottom - screenFoot) < 0.01f) nearCappedOnEdge++;
            if (cappedPlaced && ResultBoardHold.HeldGridFits(nearStrip, nearCurrent, capped, capped.Height, height, slack))
                nearAccepted++;
        }
        check(nearUncappedOverflowed == nearFeet.Length && nearUncappedRefused == nearFeet.Length,
            "a foot 1px / 0.5px / one float step under the edge still took the 2px past it and was refused by the real gate - the defect review F1 found");
        check(nearCappedOnEdge == nearFeet.Length,
            "the opt-in placement now stops every one of those feet on the window's edge (1432), not on foot + 2");
        check(nearAccepted == nearFeet.Length,
            "and HeldGridFits accepts all three - the same draw the recorded page never got, now at these readings too");

        // ---- a control that arrives later still vetoes the plate -----------------------------------------------------
        var lateButton = new BoardStrip(846f, screenFoot, true, 1420f, 40f, true, ResultFooterSource.NamedControl);
        check(placed && !ResultBoardHold.HeldGridFits(strip, lateButton, board, board.Height, height, slack),
            "a button that reads 1420 after the board was placed still refuses it - HeldGridFits is not loosened");
        var lateButtonHigher = new BoardStrip(846f, screenFoot, true, 1400f, 40f, true, ResultFooterSource.NamedControl);
        check(placed && !ResultBoardHold.HeldGridFits(strip, lateButtonHigher, board, board.Height, height, slack),
            "and one that arrives higher (1400) refuses it as well");
        var lowButton = new BoardStrip(846f, screenFoot, true, 1460f, 40f, true, ResultFooterSource.NamedControl);
        check(placed && ResultBoardHold.HeldGridFits(strip, lowButton, board, board.Height, height, slack),
            "a button that reads below the window's own foot does not add a second, tighter wall");

        // ---- the failure page and a client call site keep what they had ---------------------------------------------
        check(ResultBoardHold.PlacementSlack(true, 2f) == 0f,
            "a host failure plate still takes no tolerance past the band it measured");
        var failureBand = new BoardStrip(504f, 1122.67f, true, 1144f, 40f, true, ResultFooterSource.NamedControl);
        check(ResultBoardHold.TryPlace(width, height, failureBand, boardWidth, total, 0f, out LayoutRect failureOld,
                ResultBoardHold.PlacementSlack(true, 2f), bottomAligned: true)
            && ResultBoardHold.TryPlace(width, height, failureBand, boardWidth, total, 0f, out LayoutRect failureNew,
                ResultBoardHold.PlacementSlack(true, 2f), bottomAligned: true, screenEdgeCeiling: true)
            && Same(failureOld, failureNew),
            "the recorded host failure band places identically - its foot is not the window's edge, so the cap is inert there");
        check(ResultBoardHold.TryPlace(width, height, strip, boardWidth, total, 0f, out LayoutRect clientBoard,
                slack, bottomAligned: true)
            && Math.Abs(clientBoard.Bottom - 1434f) < 0.01f,
            "a call site that does not ask for the cap (what a client page's placement still does) is bit-for-bit unchanged");

        // ---- the production wiring, pinned in the source itself ------------------------------------------------------
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "ResultsDisplay.cs"))) root = root.Parent;
        check(root != null, "the smoke suite can inspect the production call sites");
        if (root == null) return;
        string display = File.ReadAllText(Path.Combine(root.FullName, "ResultsDisplay.cs"));
        string visibility = File.ReadAllText(Path.Combine(root.FullName, "ResultVisibility.cs"));
        check(CountOf(display, "screenEdgeCeiling: succeeded && !GuestClient())") == 1,
            "the fit that first measures a host success plate asks for the screen-edge cap");
        check(CountOf(display, "screenEdgeCeiling: _succeeded && !GuestClient())") == 1,
            "the standing-board re-fit asks for it with the same role test");
        check(CountOf(display, "screenEdgeCeiling") == 2,
            "and those are the only two placements that ask for it - nothing else in the display changed");
        check(CountOf(display, "ResultBoardHold.HeldGridFits(_holdStrip, current, board, board.Height, Screen.height, BoardSlack)") == 1,
            "the success per-repaint check still judges the standing board with the same call");
        check(CountOf(display, "if (!ResultBoardHold.TryPlace(Screen.width, Screen.height, strip, grid.Width, total,") == 1,
            "the fit is still the one placement that produces a host success board");
        check(CountOf(visibility, "if (screenEdgeCeiling) bottom = Math.Min(bottom, screenFoot);") == 1,
            "the cap answers the window's own edge unconditionally when the placement opts in - no foot comparison to fall under");
        check(CountOf(visibility, "if (screenEdgeCeiling && strip.Bottom >= screenFoot)") == 0,
            "the first revision's guard (review F1) is gone from the placement: no foot within 2px of the edge can slip past it");
        check(CountOf(visibility, "float limit = screenHeight - 8f;") == 1,
            "HeldGridFits still takes the window's edge as a hard limit - the gate was not widened");
        check(visibility.Contains("=> hostFailure ? 0f : (float.IsFinite(slack) && slack > 0f ? slack : 0f);"),
            "PlacementSlack is unchanged: the failure page's zero tolerance and every other page's BoardSlack stay as they were");
        check(visibility.Contains("if (!(bottom > top) || top + height > bottom) return false;"),
            "the fit still refuses a board taller than the room it was given");
    }

    private static bool Same(LayoutRect left, LayoutRect right)
        => Math.Abs(left.X - right.X) < 0.01f && Math.Abs(left.Y - right.Y) < 0.01f
            && Math.Abs(left.Width - right.Width) < 0.01f && Math.Abs(left.Height - right.Height) < 0.01f;

    private static int CountOf(string text, string needle)
    {
        int count = 0, at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
        return count;
    }
}
