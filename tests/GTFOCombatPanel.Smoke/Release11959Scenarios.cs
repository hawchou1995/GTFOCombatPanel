using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// 1.19.59: the settlement board's own arithmetic, its hiding half and its cursor, driven through the production code.
//
// Everything here is a *behaviour* assertion on the functions the runtime calls - `ResultsScroll`, `ResultChromeMetrics`,
// `ResultVanillaClassify`, `ResultVanillaHidePolicy`, `ResultChromeLayout.Conflicts`/`TryToggle`, `ResultPointerSpace` -
// with the numbers the recorded 2026-11-24 Test run really produced (r2 640x480 failure page, r3/r4 2560x1440). The two
// source checks at the end exist only for the Unity side, which cannot be instantiated headlessly, and they name the one
// call each.
internal static class Release11959Scenarios
{
    internal static void RunInto(Action<bool, string> check)
    {
        APlateIsWholeRowsAndSaysHowManyItHolds(check);
        AStripTooShortForTwoRowsIsRefusedRatherThanDrawn(check);
        TheWheelReachesTheLastRowOfALimitedTable(check);
        TheBlocksThePlayerAskedToReplaceMakeWay(check);
        TheCursorAndTheGesturesShareOnePoint(check);
        ANativeControlMovesThePlaceRatherThanBlinkingIt(check);
        TheUnitySideIsWiredToTheseDecisions(check);
    }

    // ---- (2) the plate is whole rows, and the count it reports is the count it draws -----------------------
    //
    // 1.19.58 reported two rows for any room under two rows and then clipped the plate to the room, so a 12 px
    // offer was drawn as one slot with three pixels of the second row showing: "标题 + 碎行". The recorded pages:
    //   r2 640x480 failure - 276 px band, 36 px header, 9 px pitch, 26 of 30 rows;
    //   r3 2560x1440 failure - 634 px band, 53 px header, 18 px pitch, 30 of 30 rows;
    //   r4 2560x1440 success - 973 px band, 53 px header, 24 px pitch, 30 of 30 rows.
    private static void APlateIsWholeRowsAndSaysHowManyItHolds(Action<bool, string> check)
    {
        check(ResultsScroll.VisibleRows(12f, 18f) == 1,
            "a 12px offer holds one whole 18px row, not the two rows 1.19.58 reported and then clipped");
        check(ResultsScroll.PlateHeight(2, 18f, 12f) == 18f,
            "and the plate is one whole row tall (18px), never the 12px a clipped row was drawn at");
        // The invariant the log line and `DrawWindow` both read: the rows reported are the rows drawn.
        bool consistent = true;
        float[] rooms = { 12f, 48f, 96f, 234f, 276f, 540f, 620f, 634f, 720f, 901f, 973f, 1440f };
        float[] pitches = { 9f, 12f, 15f, 18f, 24f, 30f };
        foreach (float room in rooms)
            foreach (float pitch in pitches)
            {
                int rows = ResultsScroll.VisibleRows(room, pitch);
                float plate = ResultsScroll.PlateHeight(rows, pitch, room);
                if (Math.Abs(plate - rows * pitch) > 0.001f) consistent = false;
                // A room smaller than one whole row has nothing to draw: the fit refuses it (`PlateFits`) rather than
                // cutting the row, so that is the only case in which the plate may be taller than the room.
                if (plate > room + 0.001f && room >= pitch) consistent = false;
                if (rows < 1) consistent = false;
            }
        check(consistent,
            "for every room and pitch this mod draws at, the plate is exactly the reported rows times the row pitch and never taller than the room");

        // The windows the player runs, through the production chrome metrics: the header the plate hangs under.
        ResultChromeMetrics small = ResultChromeMetrics.For(640f, 480f);
        check(small.HeaderHeight > 0f && small.HeaderHeight < 48f,
            "the 640x480 header is a real band and still leaves the 48px floor something to hold: " + small.HeaderHeight.ToString("0.#") + "px");
        ResultChromeMetrics large = ResultChromeMetrics.For(2560f, 1440f);
        check(large.HeaderHeight > small.HeaderHeight,
            "and the 2560x1440 header is taller than the 640x480 one: " + large.HeaderHeight.ToString("0.#") + "px");
        // r2's own figures: 276px room, 36px header, 9px pitch.
        float r2Rows = ResultsScroll.VisibleRows(ResultsScroll.RowRoom(276f, 36f), 9f);
        check(r2Rows == 26,
            "the recorded r2 640x480 failure page holds 26 whole rows in its 276px band under a 36px header (got " + r2Rows + ")");
        // r4's: 973px room, 53px header, 24px pitch at the 16px glyph the 1440 window aims at.
        float r4Pitch = ResultsBandFit.RowHeight(16f, PanelDensity.Compact);
        float r4Rows = ResultsScroll.VisibleRows(ResultsScroll.RowRoom(973f, 53f), r4Pitch);
        check(r4Rows >= 30,
            "and the recorded r4 2560x1440 success page holds all 30 rows at the 16px target glyph (got " + r4Rows + " of a " + r4Pitch.ToString("0.#") + "px pitch)");
    }

    // ---- (2) a strip that cannot hold the header and two whole rows is refused, not drawn ------------------
    private static void AStripTooShortForTwoRowsIsRefusedRatherThanDrawn(Action<bool, string> check)
    {
        ResultChromeMetrics small = ResultChromeMetrics.For(640f, 480f);
        float header = small.HeaderHeight;
        // The 48px band the old rule clamped to, at r2's own 9px pitch.
        check(ResultsScroll.PlateFits(ResultsBounds.MinimumBand, 9f, header, ResultsScroll.MinimumVisibleRows) == false,
            "a 48px band cannot hold the 36px header and two 9px rows, so the fit refuses it instead of drawing a broken row");
        check(ResultsScroll.PlateFits(ResultsBounds.MinimumBand + header + 2f * 9f, 9f, header, ResultsScroll.MinimumVisibleRows),
            "while the smallest band that really can is accepted - the rule is the arithmetic, not a magic height");
        check(ResultsScroll.RowRoom(276f, header) > 0f
              && ResultsScroll.PlateFits(276f, 9f, header, ResultsScroll.MinimumVisibleRows),
            "and r2's real 276px band is a board: the refusal only ever names a band that has nothing to draw");
    }

    // ---- (2) a limited table is scrolled to its own last row, in whole rows --------------------------------
    private static void TheWheelReachesTheLastRowOfALimitedTable(Action<bool, string> check)
    {
        // r2: 30 rows, one frozen header row and a plate of 26 whole rows.
        var view = new ScrollViewport(30, ResultsScroll.FrozenRows, 26, ResultsScroll.FrozenRows);
        check(view.Overflows, "a 30-row table in a 26-row plate overflows, so the wheel has something to move");
        check(view.ScrollSlots == 25, "the frozen header keeps slot 0 and 25 rows scroll under it: " + view.ScrollSlots);
        int last = ResultsScroll.MaximumFirst(30, ResultsScroll.FrozenRows, view.ScrollSlots);
        var end = view.At(last);
        check(end.AtEnd && end.RowAt(end.Visible - 1) == 29,
            "at the end of the scroll the plate's last slot really shows row 29 - the table's own foot, whole");
        // One notch per gesture, clamped at both ends. `ResultsScroll.Direction` reads IMGUI's own convention: a positive
        // delta is a notch *away* from the player, i.e. towards the table's foot.
        int first = ResultsScroll.FrozenRows;
        for (int notch = 0; notch < 40; notch++) first = ResultsScroll.Next(first, 30, ResultsScroll.FrozenRows, view.ScrollSlots, 1f);
        check(first == last, "and a wheel held against the foot stops at the foot instead of wrapping past it: " + first);
        for (int notch = 0; notch < 40; notch++) first = ResultsScroll.Next(first, 30, ResultsScroll.FrozenRows, view.ScrollSlots, -1f);
        check(first == ResultsScroll.FrozenRows, "and a wheel held against the head stops at the head");
        // A page that offers all 30 rows overflows nothing: the wheel is never taken from the page for it.
        var whole = new ScrollViewport(30, ResultsScroll.FrozenRows, 30, ResultsScroll.FrozenRows);
        check(!whole.Overflows, "a plate that holds every row does not overflow, so nothing of the page's wheel is swallowed");
        // A page that changes resolution keeps the player's place, clamped to the new viewport.
        check(ResultsScroll.OffsetAfterReset(3, true, 30, 1, 25) == 3,
            "a re-measure of the same page keeps the row the player had scrolled to");
        check(ResultsScroll.OffsetAfterReset(25, true, 30, 1, 8) == 22,
            "and clamps one that the new plate cannot show down to the last first-row it can: " + ResultsScroll.OffsetAfterReset(25, true, 30, 1, 8));
    }

    // ---- (1) the blocks the player asked to replace make way, without taking a control off the page --------
    private static void TheBlocksThePlayerAskedToReplaceMakeWay(Action<bool, string> check)
    {
        // The recorded r2 5845 / r3 10703 / r4 15978 line: 'artifact summary holds a control the player can press, so it is
        // left exactly where the page put it'. The "control" was a registered `CM_Item` inside one of the page's own
        // presentation blocks - the `i` and the `1.0` the player named.
        var internalEntry = new VanillaNodeFacts(insideDecorationBlock: true, containerRoot: false,
            registered: true, active: true, hasButton: false, visualOnly: true);
        check(ResultVanillaClassify.Of(internalEntry) == VanillaNodeKind.Visual,
            "a registered decoration inside one of the page's own blocks is the block's drawing, not a playable button");
        check(ResultVanillaClassify.IsWall(ResultVanillaClassify.Of(internalEntry)) == false,
            "and it is not a wall for the interaction scan, so it cannot compress the board or defer it on hover");
        var realButton = new VanillaNodeFacts(true, false, true, true, true, true);
        check(ResultVanillaClassify.Of(realButton) == VanillaNodeKind.Action && ResultVanillaClassify.IsWall(ResultVanillaClassify.Of(realButton)),
            "while a `Button` inside the block is still an action and still a wall, inside or out");
        var thirdParty = new VanillaNodeFacts(false, false, true, true, false, true);
        check(ResultVanillaClassify.Of(thirdParty) == VanillaNodeKind.Action,
            "and a registered control *outside* the page's blocks keeps the 1.19.54 fail-safe: an unknown third-party control is a wall");

        // The plans the production hide runs on.
        check(ResultVanillaClassify.PlanForBlock(VanillaNodeKind.Visual, 0, 0, 0) == VanillaHidePlan.WholeBlock,
            "a block whose whole body is the page's drawing is put away whole, as 1.19.40 did");
        check(ResultVanillaClassify.PlanForBlock(VanillaNodeKind.Visual, 2, 0, 1) == VanillaHidePlan.VisualOnly,
            "a block that really carries a control gives up its drawing only - nothing is moved and nothing is switched off");
        check(ResultVanillaClassify.PlanForBlock(VanillaNodeKind.Visual, 3, 2, 2) == VanillaHidePlan.SafeBranches,
            "a mixed block still gives up the branches that carry no action at all");
        check(ResultVanillaClassify.PlanForBlock(VanillaNodeKind.Unknown, 2, 0, 1) == VanillaHidePlan.Nothing
              && ResultVanillaClassify.PlanForBlock(VanillaNodeKind.Action, 2, 0, 1) == VanillaHidePlan.Nothing,
            "and a block this build cannot describe, or whose own node is a control, is left exactly where the game put it");

        // The early board (r4's success page never matured: `nativeFooterVisible=false` for the whole page).
        check(ResultEarlyBoardPolicy.HidesVanilla(true, true),
            "a board that is really painted stands in for the page's blocks even while it is early, which is what the r4 success page needed");
        check(!ResultEarlyBoardPolicy.HidesVanilla(true, false) && !ResultEarlyBoardPolicy.HidesVanilla(false, false),
            "and never before it is on the page");
        check(ResultEarlyBoardPolicy.Interactive(true) == false && ResultEarlyBoardPolicy.Interactive(false),
            "the press surface is still the mature board's alone: an early board takes no click and no page button's press");
    }

    // ---- (3) one point for the cursor, the hover, the click and the wheel ----------------------------------
    private static void TheCursorAndTheGesturesShareOnePoint(Action<bool, string> check)
    {
        // A camera that renders this whole window: the projection is a window point and the mod proves it.
        var whole = new ResultPointerViewport(2560f, 1440f, 0f, 0f, 2560f, 1440f, 2560f, 1440f);
        check(whole.Whole, "a camera that renders the whole window proves the raster: space=direct");
        check(whole.TryWindow(1280f, 720f, out float x, out float y) && Math.Abs(x - 1280f) < 0.001f && Math.Abs(y - 720f) < 0.001f,
            "and the conversion is the one documented flip: a point at the middle of the raster stays in the middle of the window");
        check(ResultPointerSpace.TryWindow(whole, 1280f, 720f, 1f, out _, out _, out bool proven, out _) && proven,
            "which is the proof `settlement space: space=direct` reports");

        // The letterboxed / render-texture camera the recorded session could not explain.
        var letterbox = new ResultPointerViewport(1920f, 1080f, 0f, 120f, 1920f, 840f, 1920f, 1080f);
        check(!letterbox.Whole, "a camera that does not render the whole window proves nothing about the window's raster");
        check(ResultPointerSpace.TryWindow(letterbox, 960f, 540f, 1f, out _, out _, out proven, out string note) && !proven
              && note == "unproven",
            "and the conversion says so instead of claiming a mapping was validated");
        var renderTexture = new ResultPointerViewport(float.NaN, float.NaN, 0f, 0f, 1920f, 1080f, 1920f, 1080f);
        check(!renderTexture.Whole && !renderTexture.Usable,
            "a camera that renders into a texture has no window raster at all: the pointer falls back to the labelled physical mouse");

        // The same half-open rule for every gesture: the right and bottom edges belong to the page.
        var board = new LayoutRect(1069f, 593f, 422f, 675f);
        check(ResultChromeLayout.Inside(board, board.X, board.Y)
              && ResultChromeLayout.Inside(board, board.Right - 0.5f, board.Bottom - 0.5f)
              && !ResultChromeLayout.Inside(board, board.Right, board.Y)
              && !ResultChromeLayout.Inside(board, board.X, board.Bottom),
            "the board's hit test is half open, so its edge pixel is the page's and never the board's");
    }

    // ---- (4) a native control that stands on the toggle moves it once, then withholds it -------------------
    private static void ANativeControlMovesThePlaceRatherThanBlinkingIt(Action<bool, string> check)
    {
        ResultChromeMetrics metrics = ResultChromeMetrics.For(2560f, 1440f);
        // The recorded r4 pair: toggle (913,1323,121x37) and the page's own rectangle (1013,1254,587x107).
        var place = new LayoutRect(913f, 1323f, 121f, 37f);
        var recorded = new List<LayoutRect> { new LayoutRect(1013f, 1254f, 587f, 107f) };
        check(ResultChromeLayout.Conflicts(place, recorded, ResultChromeLayout.ConflictTolerance(metrics)),
            "the recorded r4 rectangle really covers the committed place - 21 x 37 px deep - so it is a conflict, not breathing");
        // The same page one frame later: the rectangle slid 24px down (the page's own hover animation). Still deep.
        var slid = new List<LayoutRect> { new LayoutRect(1013f, 1278f, 587f, 107f) };
        check(ResultChromeLayout.Conflicts(place, slid, ResultChromeLayout.ConflictTolerance(metrics)),
            "and the page sliding it under the pointer does not make it less of a conflict"); 
        // A neighbour that only touches an edge is not.
        var neighbour = new List<LayoutRect> { new LayoutRect(place.Right + 1f, place.Y, 200f, 40f) };
        check(!ResultChromeLayout.Conflicts(place, neighbour, ResultChromeLayout.ConflictTolerance(metrics)),
            "while a rectangle one pixel away - the gap the toggle keeps - is a neighbour and never a conflict");
        // The move budget is finite and named.
        check(ResultChromeSlot.ConflictMoves >= 1 && ResultChromeSlot.ConflictFrames >= 2,
            "the place may move aside a bounded number of times, and only after the overlap has stood for several frames");
        // The one search that moves it: the commit's own, asked with the covering rectangle as an obstacle.
        var obstacles = new List<LayoutRect> { recorded[0], new LayoutRect(860f, 990f, 200f, 32f), new LayoutRect(575f, 390f, 770f, 500f) };
        var anchor = new LayoutRect(1200f, 1323f, 200f, 37f);
        check(ResultChromeLayout.TryToggle(anchor, obstacles, 2560f, 1440f, metrics, out LayoutRect moved, out _, ResultChromeLayout.CommitClearance(metrics))
              && !ResultChromeLayout.Conflicts(moved, recorded, ResultChromeLayout.ConflictTolerance(metrics)),
            "and the place the same search finds with that rectangle as an obstacle is clear of it: moved=("
                + moved.X.ToString("0") + "," + moved.Y.ToString("0") + "," + moved.Width.ToString("0") + "x" + moved.Height.ToString("0") + ")");
    }

    // ---- the two places the Unity side has to be wired to these decisions ----------------------------------
    private static void TheUnitySideIsWiredToTheseDecisions(Action<bool, string> check)
    {
        string root = SourceRoot();
        if (root == null) { check(false, "the 1.19.59 scenarios locate the production sources"); return; }
        string display = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        string chrome = File.ReadAllText(Path.Combine(root, "ResultSettlementChrome.cs"));
        string visibility = File.ReadAllText(Path.Combine(root, "ResultVisibility.cs"));
        // The drawing half really is the whole block's, not one component's.
        check(display.Contains("CanvasRenderer[] canvas = block.GetComponentsInChildren<CanvasRenderer>(true);")
              && display.Contains("Renderer[] renderers = block.GetComponentsInChildren<Renderer>(true);")
              && display.Contains("private static void ReapplyDrawing(ResultVanillaHold record)"),
            "the drawing half switches off the whole block's renderers and canvas, so a world-space artifact text goes with the UI parts");
        check(display.Contains("if (previous != null) RestoreHidden();") && display.Contains("VanillaHideExpired();")
              && display.Contains("private const float VanillaHideGrace = 3f;"),
            "and it is given back by every real end of the stand-in (page change, leaving the screens, F8 off, a board that stopped being drawn) - never per frame");
        // 1.19.59 second pass: "no board is being drawn" is *evidence*, not a frame budget. A refusal the page really
        // answered twice gives the blocks back; a page object that has gone gives them back at once; the timer is only the
        // last resort for a page that says nothing at all.
        check(display.Contains("private static void NoteVanillaRefusal()")
              && display.Contains("if (_vanillaRefusals < 2) return;")
              && display.Contains("NoteVanillaRefusal();"),
            "a placement the page refused twice in a row gives the page's own blocks back (evidence, not a frame count)");
        check(display.Contains("if (_page == null && Hidden.Count > 0) RestoreHidden();"),
            "and a frame with no page to draw a board on gives them back immediately, so nothing can stay hidden for ever");
        check(display.Contains("private static bool HasButton(Component node)")
              && visibility.Contains("return hasButton ? VanillaHidePlan.Nothing : VanillaHidePlan.VisualOnly;")
              && display.Contains("if (HasButton(block))"),
            "and a block that really carries a `Button` is never answered with the drawing-only plan: an invisible-but-pressable control is not an improvement");
        int defer = display.IndexOf("private static void DeferBoardForInteraction()", StringComparison.Ordinal);
        int deferEnd = defer >= 0 ? display.IndexOf("private static void ", defer + 40, StringComparison.Ordinal) : -1;
        check(defer > 0 && deferEnd > defer && !display.Substring(defer, deferEnd - defer).Contains("RestoreHidden();"),
            "the deferral path no longer puts the page's blocks back on the frame a board is deferred");
        check(chrome.Contains("toggle.MoveAside(next,") && chrome.Contains("public const int ConflictMoves = 2;")
              && chrome.Contains("decision.ToggleMoved = aside;"),
            "and a standing conflict moves the toggle once instead of pausing and releasing it at the page's own animation rate");
    }

    private static string SourceRoot()
    {
        try
        {
            var folder = new DirectoryInfo(AppContext.BaseDirectory);
            for (int step = 0; step < 8 && folder != null; step++)
            {
                string candidate = Path.Combine(folder.FullName, "ResultsDisplay.cs");
                if (File.Exists(candidate)) return folder.FullName;
                folder = folder.Parent;
            }
        }
        catch { }
        return null;
    }
}
