using System;
using System.Collections.Generic;
using HawChow.GTFOCombatPanel;

// 1.19.43: the three findings the PE Pure 1.19.40 run produced, replayed on that run's own numbers.
//
//   * 局内表格 "band=none rows=0 skipped=0" while a perfectly measurable flashlight row was in the same reading:
//     one slot row of the equipment column threw, the throw escaped to the reader's outer catch, and the whole column
//     became "unmeasured" - so `HudPlacement` had nothing to avoid and the table fell back to the configured position
//     on top of the right-hand column (log 2221/2222). The repair isolates one row; the assertions below are the two
//     halves of it: the band survives a bad row, and a partial band still answers a placement that clears the column.
//   * 结算板 "1750x900 px at (405,994)" refused as `board-outside-strip` although `have=232 need=135`: the per-repaint
//     safety check judged the board by the table's *unscrolled* height instead of the plate it is really drawn into.
//     The recorded figures are the fixture here, and the production `BoardOf`/`HeldGridFits`/`OverlapsBoard` are the
//     functions under test.
//   * 结算页按钮 "clicked and nothing happened": the toggle's hit rectangle was opened from the *decision* rather than
//     from the paint, so a repaint that booked the control and did not paint it left a hot rectangle with no button
//     under it, and a page that drew both controls logged no click at all - which the bounded press probe now answers.
internal static class PePure11943Scenarios
{
    // The recorded in-level window and rows (log 2220/2221/2222).
    private const float ScreenWidth = 2560f;
    private const float ScreenHeight = 1440f;
    private const float AnchorX = 2336.08f;
    private const float AnchorY = 294.33f;
    private const float AnchorWidth = 210.59f;
    private const float AnchorHeight = 110.16f;
    // The recorded table on that page: 671x150 at (1812,526) - the configured fallback the fix keeps off the column.
    private const float TableWidth = 671f;
    private const float TableHeight = 150f;
    private const float ConfiguredX = 1812.2f;
    private const float ConfiguredY = 525.6f;
    // The recorded settlement strip and the plate the fit really placed (log 3251/3256).
    private const float StripTop = 941f;
    private const float StripBottom = 1173f;
    private const float PlateY = 994f;
    private const float PlateWidth = 1750f;
    private const float PlateHeight = 150f;
    private const float TableUnscrolledHeight = 900f;
    private const float WaitingLineTop = 1195f;

    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        OneBadSlotRowCostsOneRowNotTheColumn(check);
        TheRecordedColumnStillPlacesTheTableOffIt(check);
        TheReaderIsolatesEverySlotRow(check);
        TheRecordedFailureBoardIsJudgedByItsPlate(check);
        TheBoardStillSafeJudgesThePlateNotTheTable(check);
        WhetherAVanillaBlockCarriesAnActionIsProtected(check);
        TheToggleIsOnlyClickableAfterItWasPainted(check);
        ThePressProbeIsBounded(check);
        // 1.19.46: the two medium findings of the UI review - the refusals print the board that is on the page, and the
        // column's camera is re-proved from the new level's own HUD instead of waiting for a configuration reload.
        TheRefusalsPrintTheVisibleBoard(check);
        TheColumnCameraIsReprovedWithoutUnlocking(check);
    }

    // ---------- 1: the equipment column survives one unreadable row ----------

    private static void OneBadSlotRowCostsOneRowNotTheColumn(Action<bool, string> check)
    {
        // The reading the log should have produced: the flashlight row measured, one slot row stated as unread.
        var rows = new List<HudObstacle>
        {
            new HudObstacle("flashlight", new LayoutRect(AnchorX, AnchorY, AnchorWidth, AnchorHeight), drawn: true),
            HudObstacle.Unread("slot:Weapon", drawn: true)
        };
        EquipmentBandReading band = EquipmentBand.Read(rows, ScreenWidth, ScreenHeight);
        Console.WriteLine("PE-PURE-BAND: " + band.Describe());
        check(band.Measured, "a column with one unreadable row is still a measured column: " + band.Describe());
        check(!band.Complete, "the unreadable drawn row is what makes it a partial reading: " + band.Describe());
        check(band.Used == 1 && band.Skipped == 1,
            "the flashlight row is used and the bad row is counted as skipped, which is the pair the log showed as rows=0 skipped=0: "
                + band.Describe());
        check(Near(band.Rect.X, AnchorX) && Near(band.Rect.Bottom, AnchorY + AnchorHeight),
            "and the band is the rectangle the rows that *could* be read really cover: " + band.Describe());

        // The pre-fix answer: `EquipmentBandReading.None` - measured false, used 0, skipped 0. Printed beside a
        // measurable row it is the contradiction log 2221 carries.
        EquipmentBandReading none = EquipmentBandReading.None;
        check(!none.Measured && none.Used == 0 && none.Skipped == 0,
            "the reading the defect answered with is 'band=none rows=0 skipped=0' - measured nowhere, nothing skipped");
        float overlap;
        var configured = new LayoutRect(ConfiguredX, ConfiguredY, TableWidth, TableHeight);
        check(HudFallback.Clear(configured, none.Measured, none.Rect, out overlap),
            "and on that answer the configured position is let through, because a column nobody measured is all anybody has");
        // The overlap the screenshot shows is with the rows *below* the anchor row - the ones this reading lost. What
        // the numbers can say without inventing a row is the horizontal one: the configured table reaches into the
        // column's x-range, so any row the reader dropped is a row the table can be drawn over.
        check(configured.Right > band.Rect.X && configured.X < band.Rect.Right,
            "and that rectangle lies inside the equipment column's own x-range, so the rows the reading lost are the rows it "
                + "was drawn over: configured right=" + configured.Right.ToString("0") + " column left=" + band.Rect.X.ToString("0"));
    }

    private static void TheRecordedColumnStillPlacesTheTableOffIt(Action<bool, string> check)
    {
        var rows = new List<HudObstacle>
        {
            new HudObstacle("flashlight", new LayoutRect(AnchorX, AnchorY, AnchorWidth, AnchorHeight), drawn: true),
            HudObstacle.Unread("slot:Weapon", drawn: true)
        };
        EquipmentBandReading band = EquipmentBand.Read(rows, ScreenWidth, ScreenHeight);
        PanelPlacement placement;
        bool placed = HudPlacement.TryResolve(band.Rect, band.Complete, TableWidth, TableHeight,
            band.Rect.Right, HudPlacement.Gap, ScreenWidth, ScreenHeight, out placement);
        check(placed, "a partial reading still answers a placement for the recorded 671x150 table");
        check(placement.Kind != PanelPlacementKind.BelowBand,
            "never the rung that needs the column's bottom edge, which this reading does not prove: "
                + PanelPlacement.Name(placement.Kind));
        check(placement.Kind == PanelPlacementKind.AboveBand,
            "on the recorded page it is the rung above the column, right-aligned with the anchor row: "
                + PanelPlacement.Name(placement.Kind));
        check(Near(placement.Rect.Y, AnchorY - HudPlacement.Gap - TableHeight),
            "hanging one gap above the column's top edge: y=" + placement.Rect.Y.ToString("0.##"));
        check(Near(placement.Overlap, 0f) && HudPlacement.Area(placement.Rect, band.Rect) == 0f,
            "and it covers nothing the HUD is drawing: " + placement.Describe());
        check(HudPlacement.OnWindow(placement.Rect, ScreenWidth, ScreenHeight),
            "while staying inside the window: " + placement.Describe());
        // The avoidance this page really gets is the vertical one: the column's own top edge is the separation, and the
        // table stays right-aligned with the anchor row. Nothing here is a global "move everything to the left" - the
        // rung, the gap and the alignment are the ladder's own, which is why the same code answers `beside-band` on a
        // page whose column reaches the top of the window (asserted by the 1.19.41 suite).
        check(Near(placement.Rect.Right, band.Rect.Right),
            "the table is still right-aligned with the anchor row, not shifted somewhere else: right="
                + placement.Rect.Right.ToString("0.##"));
        check(placement.Rect.Bottom <= band.Rect.Y - HudPlacement.Gap + 0.01f,
            "and the separation is the ladder's own edge plus the mod's gap, not a hard-coded move: bottom="
                + placement.Rect.Bottom.ToString("0.##"));
    }

    private static void TheReaderIsolatesEverySlotRow(Action<bool, string> check)
    {
        string panel = Source("PanelBehaviour.cs");
        string reader = Between(panel, "private EquipmentBandReading ReadEquipmentBand(PlayerGuiLayer layer)",
            "private string Rows(EquipmentBandReading reading)");
        check(reader.Length > 0, "the row reader is where the test found it");
        string loop = Between(reader, "for (int index = 0; index < count; index++)",
            "// 1.19.41: the inventory holds more rows");
        check(loop.Length > 0, "the slot loop is where the test found it");
        check(loop.Contains("bool drawn = true;"),
            "a slot row is treated as drawn until this build says otherwise, so an unreadable row cannot shrink the band");
        check(loop.Contains("catch") && loop.Contains("if (drawn) _obstacles.Add(HudObstacle.Unread(name, true));"),
            "each row is wrapped on its own and a throw becomes one unread row, which is what makes the reading partial");
        check(Count(loop, "HudObstacle.Unread(name, true)") >= 2,
            "both the projection refusal and the throw are stated as unread rows, never dropped");
        check(loop.Contains("string name = \"slot#\" + index;"),
            "a row that could not even be named still gets a name, so the log line can tell the reader which index failed");
        check(reader.Contains("return EquipmentBandReading.None;"),
            "an inventory-level failure still answers 'no band at all' - the fix narrows one row, it does not swallow the column");
        check(Count(reader, "for (int index = 0; index < count; index++)") == 1,
            "and there is still exactly one pass over the slots");
    }

    // ---------- 2: the settlement board is judged by the plate it draws ----------

    private static void TheRecordedFailureBoardIsJudgedByItsPlate(Action<bool, string> check)
    {
        ResultChromeMetrics metrics = ResultChromeMetrics.For(ScreenWidth, ScreenHeight);
        check(metrics.HeaderHeight > 50f && metrics.HeaderHeight < 56f,
            "the recorded 2560x1440 window reserves a 53px header band, which is where the plate starts: "
                + metrics.HeaderHeight.ToString("0.#"));
        var plate = new LayoutRect((ScreenWidth - PlateWidth) * 0.5f, PlateY, PlateWidth, PlateHeight);
        LayoutRect board = ResultChromeLayout.BoardOf(plate, metrics);
        Console.WriteLine("PE-PURE-PLATE: plate=" + Rect(plate) + " board=" + Rect(board)
            + " strip=" + StripTop.ToString("0") + ".." + StripBottom.ToString("0"));
        check(Near(board.Y, StripTop, 0.6f) && Near(board.Y + metrics.HeaderHeight, plate.Y),
            "the board hangs from the plate's own top edge, one header above the table: " + Rect(board)
                + " header=" + metrics.HeaderHeight.ToString("0.##") + " (the strip edge is a measured float, so the comparison is sub-pixel)");
        check(board.Bottom <= StripBottom + 2f && board.Y >= StripTop - 2f,
            "so the rectangle the page is judged by is inside the 941..1173 strip it was measured into: " + Rect(board));
        check(Near(board.X, plate.X) && Near(board.Width, PlateWidth),
            "and it keeps the plate's own width and place: " + Rect(board));

        // The rectangle the defect judged instead: the table at its full 30-row height.
        var unscrolled = new LayoutRect(plate.X, plate.Y, plate.Width, TableUnscrolledHeight);
        check(unscrolled.Bottom > StripBottom + 700f,
            "the unscrolled table's foot is 1894 on this page - 721px below the strip, which is the refusal log 3256 "
                + "carries as boardBottom=1894");
        var strip = new BoardStrip(StripTop, StripBottom, buttonMeasured: true, buttonTop: 1174f);
        check(ResultBoardHold.HeldGridFits(strip, strip, board, board.Height, ScreenHeight, 2f),
            "the drawn board clears the strip it was measured into and the button under it");
        check(!ResultBoardHold.HeldGridFits(strip, strip, unscrolled, unscrolled.Height, ScreenHeight, 2f),
            "while the table's own height does not - which is exactly what the per-repaint check asked before this round");

        // The scan's interaction test, on the same two rectangles: the page's waiting line was read at 1195 on that run.
        var waiting = new LayoutRect(1100f, WaitingLineTop, 500f, 30f);
        check(!ResultInteractionStripPolicy.OverlapsBoard(waiting, board, ResultBoardHold.ButtonClearance),
            "the waiting line the scan measured does not reach the board that is really drawn");
        check(ResultInteractionStripPolicy.OverlapsBoard(waiting, unscrolled, ResultBoardHold.ButtonClearance),
            "but it does reach the unscrolled table, which is the deferral log 3252 records (count=1)");

        // The plate is whole rows of the room the strip leaves below the header, so nothing is drawn outside it.
        float tableRoom = ResultsScroll.RowRoom(StripBottom - StripTop, metrics.HeaderHeight);
        int visible = ResultsScroll.VisibleRows(tableRoom, 30f);
        float drawn = ResultsScroll.PlateHeight(visible, 30f, tableRoom);
        Console.WriteLine("PE-PURE-PLATE-ROWS: room=" + tableRoom.ToString("0") + "px visible=" + visible + " plate=" + drawn.ToString("0"));
        check(visible >= 2 && visible < 30,
            "a 30-row table in this strip is a handful of whole rows plus the wheel, never a squeezed glyph: visible=" + visible);
        check(drawn + metrics.HeaderHeight <= StripBottom - StripTop + 0.5f,
            "and the plate plus its header is no taller than the strip: " + (drawn + metrics.HeaderHeight).ToString("0"));
    }

    private static void TheBoardStillSafeJudgesThePlateNotTheTable(Action<bool, string> check)
    {
        string display = Source("ResultsDisplay.cs");
        string stillSafe = Between(display, "private static bool BoardStillSafe()", "private static bool BoardSettled(");
        check(stillSafe.Length > 0, "`BoardStillSafe` is where the test found it");
        check(stillSafe.Contains("ResultChromeLayout.BoardOf(new LayoutRect(_origin.X, _origin.Y, _grid.Width, plateHeight), ChromeMetrics())"),
            "the rectangle the per-repaint check judges is the plate the table is drawn into, header included");
        check(stillSafe.Contains("float plateHeight = _scrollPlate > 0f ? _scrollPlate : _origin.Height;"),
            "and the plate's height is the one the fit recorded, never `_grid.Height`");
        check(!stillSafe.Contains("new LayoutRect(_origin.X, _origin.Y, _grid.Width, _grid.Height)"),
            "the unscrolled table height is gone from every refusal in that method");
        check(Count(stillSafe, "HeldGridFits(_holdStrip, current, board, board.Height") == 1
                && Count(stillSafe, "HeldGridFits(_holdStrip, failureStrip, board, board.Height") == 1
                && !stillSafe.Contains("HeldGridFits(_holdStrip, current, board, _grid.Height")
                && !stillSafe.Contains("HeldGridFits(_holdStrip, failureStrip, board, _grid.Height"),
            "both growth checks are handed the drawn height, so a scrollable grid is not refused for being taller than its plate");
        // The header is drawn inside the plate, so it is part of what may cover a control - that part is unchanged.
        check(stillSafe.Contains("inspectBoard") || stillSafe.Contains(", board, true)"),
            "the interaction scan is still fed the same rectangle the refusals use");
        // The strict/interaction refusals themselves are untouched: the same three orders the earlier rounds assert.
        check(stillSafe.IndexOf("if (successControls.OverlapsBoard)", StringComparison.Ordinal)
                < stillSafe.IndexOf("board.Y < safeTop - BoardSlack", StringComparison.Ordinal)
                && stillSafe.IndexOf("if (failureControls.OverlapsBoard)", StringComparison.Ordinal)
                < stillSafe.IndexOf("board.Y < stripTop - BoardSlack", StringComparison.Ordinal),
            "and every refusal still asks the page's own controls before the band, in the order the earlier rounds fixed");
        // A board that does not fit at all still leaves the vanilla page alone: nothing here draws a fake 900px table.
        check(display.Contains("private static float PlateFor(PanelGrid grid, float room, float header, out int visible, out int first)")
                && display.Contains("float total = plateHeight + header;"),
            "the placement's own height is the plate plus the header, so the fit can never commit a table taller than its band");
    }

    // ---------- 2b: a vanilla block that carries an action is protected ----------

    private static void WhetherAVanillaBlockCarriesAnActionIsProtected(Action<bool, string> check)
    {
        string display = Source("ResultsDisplay.cs");
        check(display.Contains("private static bool CarriesAction(Component block, Component page, bool succeeded)"),
            "the hide path asks whether a block carries one of the page's own actions");
        check(display.Contains("CM_Item[] items = block.GetComponentsInChildren<CM_Item>(true);"),
            "`CM_Item` is the type the interaction scan already reads, so the hide path and the scan agree on what an action is");
        // 1.19.57: the guard this asserts is the *current* one. 1.19.56 replaced `if (CarriesAction(block))` with the plan
        // (`PlanOfBlock`, the same classifier the scan asks) and a branch-by-branch fallback, so the order to protect is
        // page root -> plan -> branch-by-branch/drawing-only -> move/switch off, in that order and all of it before anything
        // moves.
        // 1.19.59: the move itself lives in `MoveOffPage`, and the third answer - a block that may not be moved because the
        // page hung a real control inside it - switches the drawing off instead (`SuppressVisual`). Both are asked after the
        // page-root guard and after the plan, and neither can take a control off the page.
        string hide = Between(display, "private static void Hide(Component block, string what, Component page, bool succeeded, bool visualOnly = false)",
            "[HarmonyPatch(typeof(CM_PageExpeditionSuccess)");
        check(hide.Length > 0, "`Hide` is where the test found it");
        int shared = hide.IndexOf("if (SharedOrRoot(block))", StringComparison.Ordinal);
        int plan = hide.IndexOf("VanillaHidePlan plan = PlanOfBlock(block, page, succeeded);", StringComparison.Ordinal);
        int branches = hide.IndexOf("HideSafeBranches(block, what, page, succeeded)) return;", StringComparison.Ordinal);
        int move = hide.IndexOf("MoveOffPage(block, what);", StringComparison.Ordinal);
        int drawing = hide.IndexOf("SuppressVisual(block, what);", StringComparison.Ordinal);
        check(shared >= 0 && plan > shared && branches > plan && move > plan && drawing > branches && drawing > move,
            "and it is asked *before* anything is moved or switched off, so a block holding a button is never taken off the page"
                + " (page root -> plan -> branch by branch -> drawing only)");
        string moveOff = Between(display, "private static void MoveOffPage(Component block, string what)",
            "private static void SuppressVisual(Component block, string what)");
        check(moveOff.Contains("target.localPosition = new Vector3") && moveOff.Contains("host.SetActive(false)"),
            "the move and the switch-off are still the same two writes, in the one method that performs them");
        // 1.19.59 (independent review): the drawing half records its state in the ledger and re-applies it through
        // `ReapplyDrawing`, which is alpha and renderer enablement alone - the property this assertion is about.
        string suppress = Between(display, "private static void SuppressVisual(Component block, string what)",
            "private static Component LivePage(");
        check(suppress.Contains("Hidden.HoldVisual(") && suppress.Contains("ReapplyDrawing(record);")
                && !suppress.Contains("SetActive(false)") && !suppress.Contains("localPosition ="),
            "and the drawing-only half moves nothing and deactivates nothing: it records alpha and renderer states and writes nothing else, so every control the page hung inside the block keeps answering");
        check(Count(hide, "if (SharedOrRoot(block))") == 1,
            "the page-root protection it has always had is untouched and still asked first");
        // Switching pages must put every block back, which is the other half of 'the buttons work'.
        check(display.Contains("if (previous != null) RestoreHidden();")
                && display.Contains("if (_page != null) RestoreHidden();"),
            "leaving the page, or replacing it, still restores every block this mod moved");
    }

    // ---------- 3: the controls' hit rectangles ----------

    private static void TheToggleIsOnlyClickableAfterItWasPainted(Action<bool, string> check)
    {
        string display = Source("ResultsDisplay.cs");
        string drawChrome = Between(display, "private static void DrawChrome(bool boardUp)", "private static void FinishChromeDraw()");
        check(drawChrome.Length > 0, "`DrawChrome` is where the test found it");
        check(drawChrome.Contains("_chromeTogglePending = decision.ToggleDrawn;"),
            "the toggle is booked by the repaint that decided on it");
        check(!drawChrome.Contains("_chromeToggleValid = decision.ToggleDrawn;"),
            "and that booking no longer opens the hit rectangle - which is what left a click box with no button under it");
        string finish = Between(display, "private static void FinishChromeDraw()", "private static void ReportChrome(");
        int paint = finish.IndexOf("DrawChromeControl(_chromeToggleRect", StringComparison.Ordinal);
        int valid = finish.IndexOf("_chromeToggleValid = true;", StringComparison.Ordinal);
        check(paint >= 0 && valid > paint,
            "`FinishChromeDraw` paints the control first and only then makes it clickable, the rule the close already followed");
        check(finish.Contains("_chromeTogglePending && _chromeToggleRect.Width > 0f"),
            "a booked rectangle with no size is neither painted nor made clickable");
        check(display.Contains("_chromeHeaderPending = false; _chromeTogglePending = false;"),
            "every repaint starts with nothing booked, so a frame that never reaches `DrawChrome` leaves nothing clickable");

        // The input gate itself: a press only answers for a rectangle this repaint really painted.
        var gate = new ResultChromeInputGate();
        check(!gate.Fire(focused: true, rectFresh: true, inside: false, pressed: false, held: false, released: false) && gate.Armed,
            "a quiet frame arms the gate without firing");
        check(!gate.Fire(true, rectFresh: false, inside: true, pressed: true, held: true, released: false),
            "a press on a rectangle this repaint did not paint is refused");
        check(gate.LastWhy == "stale", "and the decision says so in the words the click log prints: " + gate.LastWhy);
        check(gate.Fire(true, rectFresh: true, inside: true, pressed: true, held: true, released: false),
            "a press on a painted rectangle fires once");
        check(!gate.Fire(true, rectFresh: true, inside: true, pressed: false, held: true, released: false),
            "holding it does not fire again");
        check(!gate.Fire(true, rectFresh: true, inside: true, pressed: false, held: false, released: true),
            "and the release is not a second click");
    }

    private static void ThePressProbeIsBounded(Action<bool, string> check)
    {
        string display = Source("ResultsDisplay.cs");
        check(display.Contains("private const int PressProbeReports = 4;"),
            "the left-edge probe is bounded by a named count, not by a latch that could grow");
        check(display.Contains("if (!down || toggleInside || closeInside) return;"),
            "it reports only presses that fell outside both controls - a hit is the click log's business");
        check(display.Contains("if (_chromePressProbes >= PressProbeReports) return;"),
            "and it goes silent once its budget is spent");
        check(display.Contains("ReportChromePressProbe(frame.ToggleInside, false, frame.Down, frame.Fresh, frame.Focused,")
                && display.Contains("frame.SameWindow, frame.PointerValid, frame.PointerX, frame.PointerY);"),
            "the production input path calls it on the frame of the press, with the close slot handed over as 'not drawn' - this build paints one control, so a second rectangle cannot be probed");
        check(display.Contains("_chromePressProbes = 0;"),
            "the count belongs to the page it was measured on, so the next result page is reported again");
        check(display.Contains("settlement board press probe {_chromePressProbes}/{PressProbeReports}"),
            "the line names its own budget so a reader can tell a spent probe from a page nobody pressed");
    }

    // ---------- 3b: the refusals print the board that is on the page (UI review F2) ----------

    private static void TheRefusalsPrintTheVisibleBoard(Action<bool, string> check)
    {
        string display = Source("ResultsDisplay.cs");
        // 1.19.46 (UI review F2): the growth refusal judged the plate but still *printed* `board.Y + _grid.Height`, the
        // unscrolled table's foot - a number nothing paints, and on the recorded page 721px below the strip. The figure the
        // line carries is the visible board's own bottom edge, and the full grid height is a separate field so the two can
        // never be read as one.
        check(!display.Contains("boardBottom=\" + (board.Y + _grid.Height)"),
            "no refusal line derives its boardBottom from the unscrolled table height any more");
        check(Count(display, "boardBottom=\" + board.Bottom.ToString(\"0\") + \",visibleHeight=\" + board.Height.ToString(\"0\")") == 2,
            "both `grid-would-reach-edge` lines print the visible board's bottom edge and its drawn height");
        check(Count(display, ",fullGrid=\" + _grid.Height.ToString(\"0\")") == 2,
            "and state the full grid height separately, never as the board's bottom edge");
        check(Count(display, "HeldGridFits(_holdStrip, current, board, board.Height") == 1
                && Count(display, "HeldGridFits(_holdStrip, failureStrip, board, board.Height") == 1,
            "the judgement itself was already the plate's (1.19.43) and still is");
        // The growth refresh: the same source for the plate it judges and the plate it draws.
        string growth = Between(display, "if (_holdValid && _page != null && ResultVisibility.SamePage(_holdPage, _page.Pointer))",
            "// The one line a refused fit must never be silent about");
        check(growth.Length > 0, "the growth/refresh branch is where the test found it");
        // 1.19.59: the room it is measured into is the larger of this frame's strip and the strip the standing board was
        // placed into (so a breathing edge cannot ratchet the table down a row per repaint), and the plate that room
        // produces is re-placed against *this* frame's strip before it is committed (`TryFitHeld`): a wall that really
        // tightened is re-adapted to or refused, never crossed.
        check(growth.Contains("bool standing = TryFitHeld(grid, Math.Max(heldRoom, frameRoom), headerNow, strip,")
                && growth.Contains("|| TryFitHeld(grid, frameRoom, headerNow, strip,"),
            "a refreshed grid is measured into the larger room and, when that does not fit this frame, into the room this frame really offers");
        check(growth.Contains("_scrollVisible = keptVisible; _scrollFirst = keptFirst;")
                && growth.Contains("_scrollPlate = keptPlate;")
                && growth.Contains("_holdBoard = keptRect;"),
            "and the visible rows, the plate and the rectangle travel together with the grid that was committed, so no frame draws new rows at the old plate");
    }

    // ---------- 3c: the column's camera is re-proved from this level's own HUD (HUD review closure) ----------

    private static void TheColumnCameraIsReprovedWithoutUnlocking(Action<bool, string> check)
    {
        string panel = Source("PanelBehaviour.cs");
        string watch = Between(panel, "private void WatchEquipmentBand(PlayerGuiLayer layer, float now)", "private const int BandCameraReproofLimit");
        check(watch.Length > 0, "the band watch is where the test found it");
        check(watch.Contains("if (!_bandCameraProved || !BandCameraAlive(_bandCamera)) ReprooveBandCamera(layer);"),
            "the poll re-proves the camera before it reads the column whenever the round has none, or the one it had is gone");
        check(watch.IndexOf("ReprooveBandCamera(layer);", StringComparison.Ordinal)
                < watch.IndexOf("EquipmentBandReading reading = ReadEquipmentBand(layer);", StringComparison.Ordinal),
            "and it does that *before* the read, so the same poll projects through the camera it just proved");
        string reproof = Between(panel, "private void ReprooveBandCamera(PlayerGuiLayer layer)", "private EquipmentBandReading ReadEquipmentBand(PlayerGuiLayer layer)");
        check(reproof.Contains("try { ReadAnchor(layer); } catch { }"),
            "the re-proof is the existing anchor measurement - the one function allowed to decide which camera projects this HUD");
        check(reproof.Contains("if (!_bandCameraProved) return;"),
            "a read that proves nothing changes nothing: the round stays unproved instead of falling back to a guess");
        check(reproof.Contains("_bandCameraReproofs >= BandCameraReproofLimit"),
            "the attempts are bounded per round, so a HUD that cannot prove a camera is not walked four times a second");
        check(Between(panel, "private void ForgetBandRound()", "private void Update()").Contains("_bandCameraReproofs = 0;"),
            "and a new round (a level change, a resolution change, a config reload) earns its own attempts");
        // The two things that must NOT be needed: no unlock, and no empty-reference overlay guess.
        string level = Between(panel, "if (inLevel != _inLevel)", "bool visible = inLevel ?");
        check(!level.Contains("_anchorLocked = false;"),
            "the locked anchor keeps its place: no re-proof path unlocks it");
        check(panel.Contains("_bandCameraProved = measuredAnything;")
                && panel.Contains("if (camera == null) return true;"),
            "the overlay answer is only 'proved' when a round measured it, and it is never inferred from a missing camera");
    }

    // ---------- helpers ----------

    private static string Rect(LayoutRect rect)
        => "(" + rect.X.ToString("0") + "," + rect.Y.ToString("0") + "," + rect.Width.ToString("0") + "x" + rect.Height.ToString("0") + ")";

    private static int Count(string text, string needle)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0) { count++; index += needle.Length; }
        return count;
    }

    private static bool Near(float a, float b) => Near(a, b, 0.01f);

    private static bool Near(float a, float b, float tolerance) => Math.Abs(a - b) <= tolerance;

    private static string Source(string name)
    {
        var directory = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "GTFOCombatPanel.csproj")))
            directory = directory.Parent;
        if (directory == null) throw new InvalidOperationException("The plugin source directory could not be found from " + AppContext.BaseDirectory);
        string path = System.IO.Path.Combine(directory.FullName, name);
        if (!System.IO.File.Exists(path)) throw new System.IO.FileNotFoundException("Production source is missing", path);
        return System.IO.File.ReadAllText(path);
    }

    private static string Between(string text, string from, string to)
    {
        int start = text.IndexOf(from, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        int end = text.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        if (end < 0) end = text.Length;
        return text.Substring(start, end - start);
    }
}
