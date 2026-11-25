using System;
using System.IO;

using HawChow.GTFOCombatPanel;

// The settlement pages' own two controls: the edge the board is measured down to, and the wheel that scales it.
//
// Both exist because of what a real page does rather than because of what a layout would like. The success page
// reveals its footer from zero alpha, so its lobby button is laid out exactly where the player will press it while
// the page's own visibility test still says "not a bound" - and treating that answer as "no geometry at all" is
// what dropped a 30-row board onto the historical 0.66 fraction of the window and drew it at 6px. The wheel is the
// player's own size preference, one per page, and it is bounded by the very strip the board was measured into.
//
// Everything asserted here runs against `ResultsRegion.ResolveLobbyEdge`, `ResultsRegion.Resolve`,
// `ResultsZoom` and `ResultAnchorRecovery`, all of which are pure arithmetic - no Mathf, no Screen, no texture.
internal static class ResultsPageScenarios
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

    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));

        // ---- the board's lower bound: the button's pressability and its geometry are two questions ---------
        //
        // The reported page: the completion section ends at 410, the lobby button's own top edge is at 953 of a
        // 1080-high window, and the button's canvas group is still at zero alpha while the page fades its footer
        // in. The first answer is therefore "not a bound", the second one is the edge that matters.
        ResultsRegion.LobbyEdgeReading pressable = ResultsRegion.ResolveLobbyEdge(true, true, 953f, true, 953f, 900f, 1080f);
        check(pressable.Source == ResultsRegion.LobbyEdgeSource.Pressable && near(pressable.Top, 953f)
              && pressable.Measured && pressable.Pressable && pressable.FromButton,
            $"a button the player can press is the bound, and it wins over the drawn band: {pressable.Source} at {pressable.Top:0}");

        ResultsRegion.LobbyEdgeReading laidOut = ResultsRegion.ResolveLobbyEdge(true, false, float.NaN, true, 953f, 900f, 1080f);
        check(laidOut.Source == ResultsRegion.LobbyEdgeSource.Layout && near(laidOut.Top, 953f)
              && laidOut.Measured && laidOut.FromButton && !laidOut.Pressable,
            $"a switched-on button whose reveal has not finished still hands over its own laid-out edge, which is what the success page failed to read: {laidOut.Source} at {laidOut.Top:0}");

        // A control the page has switched off is not part of the frame, whatever rectangle it still holds: a stale
        // rect on a deactivated footer must never win over the band the page really drew, or over the caller's own
        // fallback when there is no band either.
        ResultsRegion.LobbyEdgeReading staleRect = ResultsRegion.ResolveLobbyEdge(false, false, float.NaN, true, 500f, 900f, 1080f);
        check(staleRect.Source == ResultsRegion.LobbyEdgeSource.Band && near(staleRect.Top, 900f),
            $"an inactive control's stale rectangle does not bound the board - the drawn band does ({staleRect.Source} at {staleRect.Top:0})");
        ResultsRegion.LobbyEdgeReading staleAndStalePress = ResultsRegion.ResolveLobbyEdge(false, true, 500f, true, 500f, 900f, 1080f);
        check(staleAndStalePress.Source == ResultsRegion.LobbyEdgeSource.Band && near(staleAndStalePress.Top, 900f),
            "and neither a stale pressable reading nor a stale layout reading of that inactive control can win");
        ResultsRegion.LobbyEdgeReading staleNoBand = ResultsRegion.ResolveLobbyEdge(false, true, 500f, true, 500f, float.NaN, 1080f);
        check(!staleNoBand.Measured && staleNoBand.Source == ResultsRegion.LobbyEdgeSource.None,
            "with no band either, an inactive control leaves the caller on its own historical fallback rather than on a stale edge");
        ResultsRegion.LobbyEdgeReading activeAlphaZero = ResultsRegion.ResolveLobbyEdge(true, false, float.NaN, true, 953f, 900f, 1080f);
        check(activeAlphaZero.Source == ResultsRegion.LobbyEdgeSource.Layout && near(activeAlphaZero.Top, 953f),
            "while an active control's laid-out edge is used even though nothing about it is drawn yet");

        ResultsRegion.LobbyEdgeReading bandOnly = ResultsRegion.ResolveLobbyEdge(true, false, float.NaN, false, float.NaN, 900f, 1080f);
        check(bandOnly.Source == ResultsRegion.LobbyEdgeSource.Band && near(bandOnly.Top, 900f)
              && bandOnly.Measured && !bandOnly.FromButton,
            $"a page whose own button cannot be read at all keeps the band of everything it drew: {bandOnly.Source} at {bandOnly.Top:0}");

        ResultsRegion.LobbyEdgeReading nothing = ResultsRegion.ResolveLobbyEdge(true, false, float.NaN, false, float.NaN, float.NaN, 1080f);
        check(!nothing.Measured && nothing.Source == ResultsRegion.LobbyEdgeSource.None,
            "a page that hands no geometry at all is not given an invented bound");

        // An edge that is not a coordinate on this page is not evidence: it falls back, never forwards.
        check(ResultsRegion.ResolveLobbyEdge(true, false, float.NaN, true, 0f, 900f, 1080f).Source == ResultsRegion.LobbyEdgeSource.Band
              && ResultsRegion.ResolveLobbyEdge(true, false, float.NaN, true, -30f, 900f, 1080f).Source == ResultsRegion.LobbyEdgeSource.Band
              && ResultsRegion.ResolveLobbyEdge(true, false, float.NaN, true, 1200f, 900f, 1080f).Source == ResultsRegion.LobbyEdgeSource.Band
              && ResultsRegion.ResolveLobbyEdge(true, false, float.NaN, true, float.NaN, 900f, 1080f).Source == ResultsRegion.LobbyEdgeSource.Band,
            "a layout edge at zero, negative, past the bottom of the window or not a number falls back to the drawn band");
        check(!ResultsRegion.ResolveLobbyEdge(true, false, float.NaN, true, 953f, 900f, 0f).Measured,
            "a window that has not been sized yet yields no bound at all");
        check(ResultsRegion.ResolveLobbyEdge(true, true, float.NaN, true, 953f, 900f, 1080f).Source == ResultsRegion.LobbyEdgeSource.Layout,
            "a pressable reading with no coordinate is not a bound either, so the layout edge still gets its turn");

        // ---- and the strip those two edges produce ----------------------------------------------------------
        //
        // Measured: 410 + 16 .. 953 - 16, which is the 511px the page really offers. Unmeasured: the historical
        // fraction, 0.66 * 1080 = 712.8, which is the 287px the board was drawn in - 224px of page the player was
        // looking at and the board was told it could not have.
        ResultsSafeArea measured = ResultsRegion.Resolve(true, 410f, true, 953f, 1080f, 16f);
        ResultsSafeArea fallback = ResultsRegion.Resolve(true, 410f, false, 0f, 1080f, 16f);
        check(near(measured.Top, 426f) && near(measured.Bottom, 937f) && near(measured.Height, 511f),
            $"the completion section and the button's own edge define a 511px strip (top {measured.Top:0}, bottom {measured.Bottom:0}, height {measured.Height:0})");
        check(near(fallback.Bottom, 1080f * ResultsRegion.FallbackBottomFraction) && Math.Abs(fallback.Height - 287f) <= 0.5f,
            $"without a button the same page keeps the historical 0.66 fraction, which is the 287px strip of the report (height {fallback.Height:0.0})");
        check(measured.Height - fallback.Height > 220f,
            $"the layout edge gives the board {measured.Height - fallback.Height:0}px the fallback threw away, and that difference is the reported 317 physical px at 4/3 scaling");

        // ---- the board that strip produces, placed against it ------------------------------------------------
        //
        // 30 rows at the 1.5 em row height are 45 em tall, so 10px is 450px of the 511px strip (88%) and 11px would
        // be 495px, past the 0.96 ceiling of 491.1px. That is a table a player can read, and it is the size the fix
        // has to produce; the 287px strip can only ever hold 6px.
        int readable = ResultsFontGrowth.FitBoardIntoStrip(15, ResultsFontGrowth.MinimumReadablePixels, int.MaxValue,
            511f, 1392f, Table(30, 1.5f, 40f), out int readableFloor);
        int crushed = ResultsFontGrowth.FitBoardIntoStrip(15, ResultsFontGrowth.MinimumReadablePixels, int.MaxValue,
            287f, 1392f, Table(30, 1.5f, 40f), out _);
        check(readable >= 10, $"a 511px strip carries a 30-row board at {readable}px, not the 6px of the report");
        check(readableFloor == ResultsFontGrowth.MinimumReadablePixels,
            $"an unmeasurable button height keeps the absolute readable floor ({readableFloor}px) rather than inventing one");
        check(crushed == 6 && Measure(30, 1.5f, 40f, crushed).Height == 270f,
            $"the 287px strip still yields exactly 6px and a 270px table, which is the failure mode this pins ({crushed}px)");
        check(Measure(30, 1.5f, 40f, readable).Height <= 511f * ResultsFontGrowth.StripFillCeiling + 0.5f,
            $"the board drawn in the measured strip stays inside its 96% ceiling: {Measure(30, 1.5f, 40f, readable).Height:0}px <= {511f * ResultsFontGrowth.StripFillCeiling + 0.5f:0}px");

        var strip = new BoardStrip(426f, 937f, true, 953f, 120f);
        bool placed = ResultBoardHold.TryPlace(1440f, 1080f, strip, 600f, 450f, 0f, out LayoutRect board, 2f, bottomAligned: true);
        check(placed && near(board.Y, 489f) && near(board.Bottom, 939f),
            $"a 450px board is bottom-aligned into the measured strip: y={board.Y:0}, bottom={board.Bottom:0}");
        check(board.Bottom <= 953f - 16f + 2f,
            $"and it stops clear of the button's own top edge by the page gap: {board.Bottom:0} <= {953f - 16f + 2f:0}");
        check(Math.Abs(953f - board.Bottom) <= 24f,
            $"there is no dead band between the board and the button any more: {953f - board.Bottom:0}px of page, not the 238px the report measured");

        // The same board placed against the fallback strip is the reported picture: 270px at (24,445), 238 UI px of
        // empty page above the button. Pinned so a build that goes back to the fraction cannot pass.
        var fallbackStrip = new BoardStrip(fallback.Top, fallback.Bottom, false, float.NaN, float.NaN);
        bool fallbackPlaced = ResultBoardHold.TryPlace(1440f, 1080f, fallbackStrip, 1392f, 270f, 0f, out LayoutRect crushedBoard, 2f, bottomAligned: true);
        check(fallbackPlaced && Math.Abs(crushedBoard.X - 24f) <= 1f && Math.Abs(crushedBoard.Y - 445f) <= 1f,
            $"the board laid out on the fallback sits exactly where the report shows it, at ({crushedBoard.X:0},{crushedBoard.Y:0})");
        check(953f - crushedBoard.Bottom > 200f,
            $"and that placement leaves {953f - crushedBoard.Bottom:0}px of empty page above the button, which is what the fix removes");

        // ---- the one second look a board placed against a guess is allowed ----------------------------------
        //
        // The board is 6px because the button could not be measured, the button becomes readable a moment later, and
        // the page is re-measured exactly once. A board whose strip was measured is never touched, and a page that
        // already spent its retry does not get another.
        check(ResultAnchorRecovery.ShouldRetry(0, true, true),
            "a board placed on the fallback is re-measured once the button's own rectangle is readable");
        check(!ResultAnchorRecovery.ShouldRetry(0, false, true),
            "a board whose strip was measured is never re-measured, so a settled board cannot jump");
        check(!ResultAnchorRecovery.ShouldRetry(0, true, false),
            "a board placed on the fallback whose button is still unreadable has nothing to recover to");
        int spent = ResultAnchorRecovery.Record(0);
        check(spent == 1, $"the retry is recorded ({spent})");
        check(!ResultAnchorRecovery.ShouldRetry(spent, true, true),
            "and it happens exactly once: the same page is never re-measured a second time");
        check(ResultAnchorRecovery.Record(spent) == spent && ResultAnchorRecovery.Record(-4) == 0,
            "the budget saturates, so no sequence of calls can buy a second or a third attempt");
        check(ResultAnchorRecovery.LatchFallback(0, false) && !ResultAnchorRecovery.LatchFallback(0, true)
              && !ResultAnchorRecovery.LatchFallback(1, false),
            "only a placement against the fallback, on a page that has not spent its retry, arms the second look");

        // ---- the manual multipliers that stayed, and the wheel that no longer writes them ------------------
        check(near(ResultsZoom.Default, 1f) && ResultsZoom.Minimum < ResultsZoom.Default && ResultsZoom.Maximum > ResultsZoom.Default,
            $"the manual multiplier's own default is 1.0 inside a bounded range ({ResultsZoom.Minimum}..{ResultsZoom.Maximum})");
        check(ResultsZoom.Section == "Results" && ResultsZoom.SuccessKey == "SuccessFontScale" && ResultsZoom.FailureKey == "FailureFontScale",
            "the two preferences are stored under readable keys in the section the rest of the result settings live in");
        check(ResultsZoom.KeyFor(ResultsZoom.Page(true)) == ResultsZoom.SuccessKey
              && ResultsZoom.KeyFor(ResultsZoom.Page(false)) == ResultsZoom.FailureKey,
            "the success page and the failure page each read their own key, so one page's preference cannot move the other");
        check(near(ResultsZoom.Clamp(float.NaN), ResultsZoom.Default)
              && near(ResultsZoom.Clamp(-3f), ResultsZoom.Minimum)
              && near(ResultsZoom.Clamp(9f), ResultsZoom.Maximum),
            "a multiplier nobody could read falls back to 1.0, and one out of range to the bound it passed");

        // ---- the wheel scrolls the table: frozen header, whole rows, both ends reachable --------------------
        //
        // The figures are the field report's: four player columns and thirty rows, a sixteen-pixel glyph in the fixed
        // layout (twenty-four pixels of row at the face's 1.5 em), and a plate that only ever holds the rows the page
        // really has room for. What used to happen here was a *scale*: the wheel multiplied the glyph until the whole
        // table fitted, which is how a short page ended up with a six-pixel table and nothing left to scroll.
        const int BoardRows = 30;
        const float RowHeight16 = 24f;
        check(ResultsScroll.FrozenRows == 1 && ResultsScroll.WheelRowsPerNotch == 1,
            "one row is frozen (the header that carries the four player names) and one notch moves the table by one row");
        check(ResultsScroll.Direction(-1f) == 1 && ResultsScroll.Direction(1f) == -1
              && ResultsScroll.Direction(0f) == 0 && ResultsScroll.Direction(float.NaN) == 0
              && ResultsScroll.Direction(0.00001f) == 0,
            "a notch up moves the table towards its head, a notch down towards its foot, and a resting trackpad is not a notch");
        check(ResultsScroll.VisibleRows(RowHeight16 * 6.5f, RowHeight16) == 6
              && ResultsScroll.VisibleRows(float.NaN, RowHeight16) == ResultsScroll.MinimumVisibleRows,
            "a plate holds whole rows only, and a room nobody could measure still holds the minimum");
        check(near(ResultsScroll.PlateHeight(6, RowHeight16, RowHeight16 * 6.5f), RowHeight16 * 6f),
            "the plate is exactly the rows it holds, so nothing is drawn between it and the button underneath it");
        check(ResultsScroll.Clamp(0, BoardRows, ResultsScroll.FrozenRows, 5) == ResultsScroll.FrozenRows,
            "the table cannot be scrolled above its frozen header row");
        int footFirst = ResultsScroll.Clamp(BoardRows, BoardRows, ResultsScroll.FrozenRows, 5);
        check(footFirst == ResultsScroll.MaximumFirst(BoardRows, ResultsScroll.FrozenRows, 5)
              && footFirst + 5 >= BoardRows,
            $"the foot of the table is reachable: first={footFirst} with five slots lands on row {footFirst + 5} of {BoardRows}");
        // Every row, on a plate of any height a page can offer - from two rows to the whole table - and on both kind
        // of page: the geometry does not know which result page it is drawing, so one walk covers success and failure.
        bool allReachable = true;
        for (int slots = 1; slots <= BoardRows; slots++)
        {
            var seen = new bool[BoardRows];
            for (int row = 0; row < BoardRows; row++) if (row < ResultsScroll.FrozenRows) seen[row] = true;
            int walk = ResultsScroll.FrozenRows;
            for (int guard = 0; guard < BoardRows * 3; guard++)
            {
                var view = new ScrollViewport(BoardRows, ResultsScroll.FrozenRows, slots + ResultsScroll.FrozenRows, walk);
                for (int slot = 0; slot < view.Visible; slot++)
                {
                    int row = view.RowAt(slot);
                    if (row >= 0 && row < BoardRows) seen[row] = true;
                }
                int next = ResultsScroll.Next(walk, BoardRows, ResultsScroll.FrozenRows, slots, 1f);
                if (next == walk) break;
                walk = next;
            }
            int counted = 0;
            for (int row = 0; row < BoardRows; row++) if (seen[row]) counted++;
            allReachable &= counted == BoardRows;
        }
        check(allReachable, $"every one of the {BoardRows} rows is reachable on a plate of any height, from two rows up");
        // The same walk on a smaller page: a 1280x720 window and a 2560x1440 one are the same rule with different
        // room, so the reach does not depend on the resolution the player happens to run.
        check(ResultsScroll.VisibleRows(RowHeight16 * 10.5f, RowHeight16) == 10
              && ResultsScroll.MaximumFirst(BoardRows, ResultsScroll.FrozenRows, 9) == 21,
            "a taller window holds more rows and reaches the foot sooner, on the same table");
        // The glyph is the size the page's own strip really holds, and never a size raised back up to the comfort
        // figure the button's height would have liked (1.19.45: that is how a board fitted to thirty rows came back at
        // 16 px and showed twenty-four of them).
        check(ResultsScroll.FontFor(13, 1f) == 13,
            "a strip that honestly holds a 13 px table draws it at 13 px: the fit is the answer, not a preference");
        check(ResultsScroll.FontFor(16, 1f) == 16 && ResultsScroll.FontFor(16, 0.5f) == 8,
            "the fitted size is drawn as it is, and an explicit multiplier is applied to it - the wheel never multiplies it");
        check(ResultsScroll.FontFor(13, 1.5f) == 20,
            "a player who asked for a larger board gets one: the multiplier is honoured, and the overflow is scrolled");
        check(ResultsScroll.FontFor(2, 1f) == ResultsFontGrowth.MinimumReadablePixels,
            "no caller can talk the board below the absolute readable minimum");

        // ---- the recorded failure strip: all thirty metrics on the first screen ------------------------------
        //
        // Log 2904/3357/3358: room=646 px, header=53 px, thirty rows at compact density. The page's own line says
        // `room=594px for 30 rows would allow a 13px glyph, below the 16px this page would have liked` - so the
        // readable floor that page really asked for is 16, not a figure derived from any button height here. Those
        // two numbers are taken from the log verbatim; nothing in this regression guesses a native control.
        float failureHeader = ResultChromeMetrics.For(2560f, 1440f).HeaderHeight;
        float failureRoom = 646f - failureHeader;
        const int RecordedRequested = 16;   // log 3357: "below the 16px this page would have liked"
        int failureFloor = ResultsBandFit.FloorForBand(failureRoom, BoardRows, PanelDensity.Compact,
            RecordedRequested, out bool failureSqueezed);
        check(failureSqueezed && failureFloor == 13,
            "the recorded 646 px strip minus its " + failureHeader.ToString("0") + " px header leaves "
            + failureRoom.ToString("0") + " px, which allows a " + failureFloor + " px glyph for " + BoardRows
            + " rows - below the " + RecordedRequested + " px that page asked for, so the band is squeezed");
        // A squeezed band is the one the production fits against itself (`squeezed ? BandFillCeiling : fillCeiling`).
        int failureFitted = ResultsFontGrowth.FitBoardIntoStrip(17, failureFloor, int.MaxValue, failureRoom, 2512f,
            size => new ResultTableSize(433f + size, ResultsBandFit.RowHeight(size, PanelDensity.Compact) * BoardRows),
            out _, ResultsFontGrowth.BandFillCeiling);
        int failureFont = ResultsScroll.FontFor(failureFitted, 1f);
        check(failureFont == failureFloor,
            "a band that cannot hold the comfort glyph is fitted against itself, so the drawn glyph is the "
            + failureFloor + " px the strip really holds (fitted=" + failureFitted + ", font=" + failureFont + ")");
        float failureRowHeight = ResultsBandFit.RowHeight(failureFont, PanelDensity.Compact);
        ScrollViewport failureView = ResultsScroll.Viewport(BoardRows, ResultsScroll.FrozenRows, failureRoom,
            failureRowHeight, ResultsScroll.FrozenRows);
        check(failureView.Visible == BoardRows && !failureView.Overflows,
            "and all " + BoardRows + " metrics are on the first screen at that size: visible=" + failureView.Visible
            + " of " + BoardRows + ", plate=" + ResultsScroll.PlateHeight(failureView.Visible, failureRowHeight, failureRoom).ToString("0")
            + " px of " + failureRoom.ToString("0") + " px");
        // ---- the other branch: a strip that CAN hold the comfort glyph keeps the 0.96 ceiling -----------------
        //
        // These two must never be mixed: an unsqueezed band keeps `StripFillCeiling`, and only a squeezed one is
        // fitted against its own 100%. 800 px holds 16 px comfortably (16 x 1.5 em x 30 = 720), so the floor is the
        // page's own requested figure and the fit must stay inside 96% of the strip.
        const float RoomyStrip = 800f;
        int roomyFloor = ResultsBandFit.FloorForBand(RoomyStrip, BoardRows, PanelDensity.Compact,
            RecordedRequested, out bool roomySqueezed);
        check(!roomySqueezed && roomyFloor == RecordedRequested,
            "an 800 px strip holds the " + RecordedRequested + " px the page asked for, so it is NOT squeezed (floor="
            + roomyFloor + ", squeezed=" + roomySqueezed + ")");
        int roomyFitted = ResultsFontGrowth.FitBoardIntoStrip(17, roomyFloor, int.MaxValue, RoomyStrip, 2512f,
            size => new ResultTableSize(433f + size, ResultsBandFit.RowHeight(size, PanelDensity.Compact) * BoardRows),
            out _, ResultsFontGrowth.StripFillCeiling);
        int roomyFont = ResultsScroll.FontFor(roomyFitted, 1f);
        check(roomyFont >= RecordedRequested
              && ResultsBandFit.RowHeight(roomyFont, PanelDensity.Compact) * BoardRows <= RoomyStrip * ResultsFontGrowth.StripFillCeiling + 0.5f,
            "and there the fit stays inside the 96% comfort ceiling instead of the band's own foot (glyph="
            + roomyFont + " px, table="
            + (ResultsBandFit.RowHeight(roomyFont, PanelDensity.Compact) * BoardRows).ToString("0")
            + " px of " + (RoomyStrip * ResultsFontGrowth.StripFillCeiling).ToString("0") + " px allowed)");
        check(ResultsScroll.Viewport(BoardRows, ResultsScroll.FrozenRows, RoomyStrip,
                  ResultsBandFit.RowHeight(roomyFont, PanelDensity.Compact), ResultsScroll.FrozenRows).Visible == BoardRows,
            "and all " + BoardRows + " rows are still on that screen: the comfort branch loses nothing either");
        // A genuinely short strip, and an explicit multiplier, are the two cases that really do scroll.
        const float ShortStrip = 200f;
        int shortFont = ResultsScroll.FontFor(
            ResultsBandFit.FloorForBand(ShortStrip, BoardRows, PanelDensity.Compact,
                ResultsFontGrowth.ReadabilityFloor(float.NaN), out _), 1f);
        ScrollViewport shortView = ResultsScroll.Viewport(BoardRows, ResultsScroll.FrozenRows, ShortStrip,
            ResultsBandFit.RowHeight(shortFont, PanelDensity.Compact), ResultsScroll.FrozenRows);
        check(shortView.Overflows,
            "a " + ShortStrip.ToString("0") + " px strip cannot hold " + BoardRows
            + " rows even at the readable minimum, so that board scrolls (visible=" + shortView.Visible + ")");
        ScrollViewport zoomedView = ResultsScroll.Viewport(BoardRows, ResultsScroll.FrozenRows, failureRoom,
            ResultsBandFit.RowHeight(ResultsScroll.FontFor(13, 1.5f), PanelDensity.Compact), ResultsScroll.FrozenRows);
        check(zoomedView.Overflows,
            "and so does an explicitly enlarged board: the multiplier is honoured and its overflow scrolls (visible="
            + zoomedView.Visible + ")");
        // One notch per frame: IMGUI can deliver the same gesture to its layout pass and its repaint.
        var latch = new ResultScrollLatch();
        check(latch.Claim(7) && latch.SameFrame(7) && !latch.Claim(7) && latch.Claim(8) && !latch.SameFrame(7),
            "one frame moves the table once: the second copy of the same gesture is consumed without moving it again");
        latch.Forget();
        check(!latch.SameFrame(7) && latch.Claim(7), "a new page forgets the frame the last gesture belonged to");

        // ---- the wiring the two rules above depend on, read from the production sources --------------------
        Wiring(check);
        // ---- the client failure page: the board's first visible layout is its final one ---------------------
        ClientStrip(check);
        // ---- 兜底：拒收预算不是「看不见这一页」的许可证 ------------------------------------------------------
        FailOpenEscape(check);
        // ---- 1.19.47：自动模式的最新用户合同 ---------------------------------------------------------------
        AutoModeContract(check);
    }


    // The production sources are read here the way the review-fix scenarios read them: not to pin wording, but to
    // pin that the decisions asserted above are the decisions the runtime actually reaches - the geometry question
    // asked behind the active gate, the wheel read before the Repaint gate, the rectangle the board was drawn at,
    // the two independent settings and the bounded second look on the refresh path.
    private static void Wiring(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the settlement-page regression locates the production sources");
        if (root == null) return;
        string results = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        string panel = File.ReadAllText(Path.Combine(root, "PanelBehaviour.cs"));
        string plugin = File.ReadAllText(Path.Combine(root, "Plugin.cs"));
        string policy = File.ReadAllText(Path.Combine(root, "ResultsPagePolicy.cs"));

        check(results.Contains("bool buttonActive = ButtonActive(success.m_btnLeaveExpedition)")
              && results.Contains("TryScreenRect(success.m_btnLeaveExpedition")
              && results.Contains("ResultsRegion.ResolveLobbyEdge(buttonActive"),
            "SuccessRegion asks the laid-out rectangle as well as the visibility of the lobby button, through the active gate");
        // 1.19.59: the wheel is no longer read from IMGUI at all - it is settled on the frame from the game's own readings,
        // through the routing the player's other panel uses (`ResultWheel.cs`). `Update` is the frame driver and
        // `CM_PageBase.UpdateButtonPress` the native second entry; `OnGUI` is drawing only.
        check(panel.Contains("ResultsDisplay.PollWheel();")
              && !panel.Contains("EventType.ScrollWheel")
              && !panel.Contains("ResultsScrollEvent"),
            "the frame driver settles the settlement wheel (`PollWheel`) and `OnGUI` reads no scroll event at all");
        check(results.Contains("if (!_scrollLatch.Claim(frame)) return;")
              && results.Contains("internal static void PollWheel()"),
            "the two entries into one frame's wheel (the frame driver and the page's press dispatch) share the same-frame latch");
        check(results.Contains("float native = ReadNativeScroll(page);") && results.Contains("float physical = ReadPhysicalWheel();")
              && results.Contains("menu.ScrollInput") && results.Contains("Input.mouseScrollDelta"),
            "the router's two sources are the page's own `ScrollInput` and the physical `mouseScrollDelta`, one of them per frame");
        check(results.Contains("_hitRect = ResultChromeLayout.BoardOf(") && results.Contains("if (!_scrollValid || _hitSucceeded != succeeded) return false;"),
            "the wheel is judged against the plate the board was last drawn at, through the scroll flag an early board also earns");
        check(Count(results, "ClearHit();") >= 3 && results.Contains("ClearHit();"),
            "the cached wheel rectangle is dropped when the board changes page or is hidden");
        check(plugin.Contains("SuccessFontScale = Config.Bind(") && plugin.Contains("FailureFontScale = Config.Bind(")
              && plugin.Contains("internal static float ResultFontScale(bool succeeded)")
              && !panel.Contains("PersistResultFontScale") && !results.Contains("PersistResultFontScale"),
            "the two manual multipliers are still read from the file, and the wheel no longer has a save path at all - a scroll never writes the config");
        check(Count(results, "FitBoard(content, tableRoom, boardWidth, succeeded") >= 2
              && results.Contains("ResultsScroll.FontFor(fitted, Plugin.ResultFontScale(succeeded))")
              && !results.Contains("ResultsZoom.ScaleSize("),
            "both placement paths fit the board to its own strip and scroll the overflow, and the old scale walk is gone from the production code");
        check(results.Contains("ResultsScroll.Viewport(grid.Rows, ResultsScroll.FrozenRows") 
              && results.Contains("HudTable.Draw(_grid, _origin.X, _origin.Y, _content, opacity, _scrollFirst, _scrollVisible, _scrollPlate)"),
            "the adaptive board is drawn through its scroll window, and the plate it occupies is measured from the rows its strip really holds");
        check(results.Contains("if (!FallbackRecoveryDue(succeeded, _page) && RefreshBoardData")
              && results.Contains("ResultAnchorRecovery.ShouldRetry(_fallbackRecoveries")
              && results.Contains("ResultAnchorRecovery.Record(_fallbackRecoveries)")
              && results.Contains("ResultAnchorRecovery.LatchFallback(_fallbackRecoveries")
              && policy.Contains("public const int MaximumRecoveries = 1;"),
            "the fallback recovery hangs off the refresh path and stays bounded to one attempt per page");

        // ---- review #1: a refreshed snapshot is committed as a PAIR, or not at all ------------------------
        //
        // The table is drawn from `_grid` + `_content`, and they are only consistent because the placement writes both, on
        // a frame it really placed the board. The refresh path used to measure a new grid, throw the placement's answer
        // away and then swap `_content` regardless - the old geometry drawing the new text. The four things that keep that
        // from happening again are pinned here, in the order the method runs them.
        check(results.Contains("if (measured == null) return true;"),
            "a snapshot that cannot be measured at the drawn font adopts nothing and still answers 'handled' - the board already on the page is kept, not re-placed");
        check(results.Contains("if (GridCovers(_grid, measured))")
              && results.Contains("boardWidth, float.PositiveInfinity, _fontFloor);"),
            "the coverage question is asked of an UNBOUNDED measurement at the drawn font, so a grid that came back smaller is never mistaken for one that covers");
        check(results.Contains("if (!Placement(page, succeeded, out _, out float room, out BoardStrip strip) || !(room > 0f)) return true;"),
            "a page that hands no strip back adopts nothing and is still 'handled' before anything is written - the fall-through the review found is gone, and a good board is not reset for a frame that measured no strip");
        check(results.Contains("return resized != null && Fit(strip, resized, content, board, report, succeeded, header);"),
            "and the tail is the fit's own answer: no `_content` write follows it, so a refused growth keeps the pair on the page (`Fit` returns true for 'handled, kept')");
        check(!results.Contains("bool fitted = Fit(strip, resized, content, board, report, succeeded, header);")
              && !results.Contains("ResultsRefreshPolicy"),
            "the discarded-answer shape and the boolean wrapper the review rejected are both absent from the production source");

        // The stale-hit residual, pinned by position rather than by wording: a repaint drops the cached rectangle
        // before it can reach any of its early returns, and only a frame that really drew the board re-earns it. A
        // pointer left where the board used to be therefore cannot keep scaling a board that is not on the screen.
        int draw = results.IndexOf("internal static void Draw()", StringComparison.Ordinal);
        int invalidateForRepaint = results.IndexOf("InvalidateHitForRepaint();", StringComparison.Ordinal);
        int noGridReturn = results.IndexOf("if (_grid == null || _content == null || _page == null", StringComparison.Ordinal);
        int boardDrawn = results.IndexOf("HudTable.Draw(_grid, _origin.X", StringComparison.Ordinal);
        int hitEarned = results.IndexOf("_hitValid = true;", StringComparison.Ordinal);
        check(draw >= 0 && invalidateForRepaint > draw && noGridReturn > invalidateForRepaint
              && boardDrawn > noGridReturn && hitEarned > boardDrawn,
            "every repaint drops the cached wheel rectangle before the no-grid/no-placement return, and only a board that was really drawn re-earns it");

        // 1.19.59: one frame's wheel is claimed once (`ResultScrollLatch.Claim`), before any reading is taken, so the frame
        // driver and the page's own press dispatch can never both move the table for the same notch.
        int poll = results.IndexOf("internal static void PollWheel()", StringComparison.Ordinal);
        int frameLock = results.IndexOf("if (!_scrollLatch.Claim(frame)) return;", StringComparison.Ordinal);
        int rectangleGate = results.IndexOf("if (!_scrollValid || _hitSucceeded != succeeded) return false;", StringComparison.Ordinal);
        check(poll >= 0 && frameLock > poll && rectangleGate > frameLock,
            "the same-frame latch is asked before anything is read and before the rectangle's validity, so one notch can never be applied twice");

        // ...and dropping the stale rectangle must not drop the lock: only the full reset - F8, leaving the page, a
        // different screen - resets the frame it belongs to.
        int clearHit = results.IndexOf("private static void ClearHit()", StringComparison.Ordinal);
        int invalidateDefinition = results.IndexOf("private static void InvalidateHitForRepaint()", StringComparison.Ordinal);
        string clearBody = Between(results, clearHit, invalidateDefinition);
        string invalidateBody = Between(results, invalidateDefinition, results.IndexOf("// Everything the board caches for one result page", StringComparison.Ordinal));
        check(clearBody.Contains("_hitFrame = -1;") && !invalidateBody.Contains("_hitFrame")
              && invalidateBody.Contains("_hitValid = false;"),
            "the per-repaint invalidation keeps the gesture lock; only ClearHit resets the frame it belongs to");
    }

    // ---------------------------------------------------------------------------------------------------
    // 客机失败页：第一帧可见的布局就必须是最终几何
    // ---------------------------------------------------------------------------------------------------
    //
    // The recorded client failure page (`LogOutput.log`, 2560x1440, expedition failed, run 4 and run 5) read:
    //
    //   failure band: top=381 (title-anchor), bottom=1429 (screen-foot), room=1049px
    //   influence board ... 1160x990 px at (700,381) ... glyph 22px filling 94% of the 1049px strip
    //   failure board kept where it stands: ... buttonTop=1176, wall=1170, need=990px, have=1049px
    //   failure board deferred: an interaction the scan can see now reaches the placed board (count=1, room=798)
    //   result board deferred: ... vanilla is restored and the strip is measured again
    //
    // i.e. the page's pressable control was switched on one frame *after* the board had been fitted to the full
    // fixed-page band, so the board the player saw was drawn once at a size the page never offered and then taken
    // away. Every assertion below therefore drives `ResultClientFailureStrip` - the one entry point the runtime
    // calls - frame by frame, instead of a hand-copied order of `ResultsGuestPageGeometry` and
    // `ResultClientFailureBand` calls that would stay green after the runtime stopped asking them that way.
    private const float RecordedWidth = 2560f, RecordedHeight = 1440f;
    // The mission-failed title's own laid-out bottom on that page: the fixed geometry built from it is the
    // 380.83..1429.33 band the log printed as 381..1429.
    private const float RecordedTitleBottom = 359.5f;
    // The waiting-status line the page laid out at 1147: from this release it is the page's lower wall (the host's
    // pressable button, made non-clickable), and the band's foot is that line less the clearance.
    private const float RecordedWaitingText = 1147f;
    // The pressable control the scan found on the *second* frame of that page - below the line, so it never tightens
    // the band; `HigherControl` is one that reaches above the line and therefore does.
    private const float RecordedWall = 1176f, HigherControl = 1100f;
    private const float RecordedGap = 16f, RecordedMinimumStrip = 96f;
    // The design clearance at 1440: 16 px x 1440/1080.
    private const float RecordedClearance = 21.3333f;
    private const int RecordedRows = 30;

    // One frame of that page as the production measurement hands it over. `scanComplete`/`parentCoverage` are the
    // scan's own structural verdicts, which production passes from `FooterControls`; an inactive control is counted
    // as hidden by that scan and leaves both true, which is why they are not the same fact as "nothing is pressable".
    private static ResultClientFailureEvidence Evidence(float pressableTop, int unmeasurable,
        bool scanComplete = true, bool parentCoverage = true)
        => new ResultClientFailureEvidence(RecordedTitleBottom, RecordedWidth, RecordedHeight, RecordedGap,
            RecordedMinimumStrip, RecordedWaitingText, pressableTop, scanComplete, parentCoverage, unmeasurable);

    // The placement step production really takes: a band a decision handed out, fitted the way `FitBoard` fits it and
    // placed the way `ResultBoardHold.TryPlace` places it. "No rectangle" is what "nothing was committed" means.
    private static bool ResultClientBoard(ResultClientBandReading reading, out LayoutRect rect)
    {
        rect = default;
        if (!reading.Fits) return false;
        var strip = new BoardStrip(reading.Top, reading.Bottom, reading.PressableTightened, float.NaN, float.NaN, true,
            ResultFooterSource.ClientNoFooterSafeArea, ResultsFontGrowth.FillCeiling(ResultFooterSource.ClientNoFooterSafeArea, false));
        float height = ResultsBandFit.RowHeight(
            ResultsBandFit.FontForBand(reading.Height, RecordedRows, PanelDensity.Compact, 0), PanelDensity.Compact) * RecordedRows;
        return ResultBoardHold.TryPlace(RecordedWidth, RecordedHeight, strip, 900f, height, 0f, out rect, 2f);
    }

    private static void ClientStrip(Action<bool, string> check)
    {
        // ---- 初始未就绪: the first frame the page exists is not a placement frame ---------------------------
        var gate = new ResultClientWallGate();
        ResultClientStripState first = ResultClientFailureStrip.Decide(101, gate, Evidence(float.NaN, 0),
            out ResultClientBandReading firstBand, out ResultsSafeArea firstPage);
        check(first == ResultClientStripState.WallNotSettled,
            "the first frame on which a client failure page has read its waiting line is a reading, not an agreement - the gate needs the same wall twice before anything is fitted");
        check(near(firstPage.Top, 380.83f) && near(firstPage.Bottom, 1429.33f),
            "the page's fixed geometry is still read on that frame (380.83..1429.33 of a 2560x1440 window), so the wait is about the wall and not about a page nobody could measure");
        check(!firstBand.Fits && near(gate.Wall, RecordedWaitingText) && gate.Samples == 1,
            "no band is handed out yet, and the wall this page has shown is the waiting line at 1147 - not the page's screen-derived foot, which only caps the band");

        // ---- 锚点稳定: the wall the page really has, read the same way twice --------------------------------
        ResultClientStripState changing = ResultClientFailureStrip.Decide(102, gate, Evidence(RecordedWall, 0),
            out ResultClientBandReading settled, out _);
        check(changing == ResultClientStripState.Placed && gate.Settled,
            "the second identical reading of that wall is the placement frame, and it is a settled wall rather than any kind of count");
        check(near(settled.Top, 380.83f) && near(settled.Bottom, RecordedWaitingText - RecordedClearance)
              && near(settled.Height, 744.83f),
            "the band is the page's own top down to the waiting line less the clearance (380.83..1125.67, 744px): the foot is the edge the page really draws");
        check(settled.WaitingTextTightened && !settled.PressableTightened && !settled.WaitingTextCovered
              && near(settled.WallTop, RecordedWaitingText),
            "the foot came from the waiting line itself, the pressable control at 1176 sits below it and changes nothing, and the text is never covered");

        // The board the placement really builds from that band - the same arithmetic `FitBoard` uses before the text
        // measurement refines it - so the assertions below are about a board of the band's own size, not a made-up one.
        int font = ResultsBandFit.FontForBand(settled.Height, RecordedRows, PanelDensity.Compact, 0);
        float boardHeight = ResultsBandFit.RowHeight(font, PanelDensity.Compact) * RecordedRows;
        check(font >= ResultsFontGrowth.MinimumReadablePixels && boardHeight <= settled.Height + 0.5f,
            "the settled band can hold the board at a readable glyph, so the tightened wall does not buy the placement with an unreadable table");
        var settledStrip = new BoardStrip(settled.Top, settled.Bottom, true, RecordedWall, float.NaN, true,
            ResultFooterSource.ClientNoFooterSafeArea, ResultsFontGrowth.FillCeiling(ResultFooterSource.ClientNoFooterSafeArea, true));
        check(ResultBoardHold.TryPlace(RecordedWidth, RecordedHeight, settledStrip, 1160f, boardHeight, 0f, out LayoutRect placed, 2f)
              && near(placed.Y, settled.Top),
            "the board hangs from the band's own top edge, as the runtime's first placement does");

        // ---- 首显后多帧不移动: thirty-eight more repaints of the same page --------------------------------
        bool stable = true, wallNeverLifts = true, perRepaintCheckNeverRefuses = true;
        for (int frame = 104; frame < 142; frame++)
        {
            ResultClientStripState state = ResultClientFailureStrip.Decide(frame, gate, Evidence(RecordedWall, 0),
                out ResultClientBandReading band, out _);
            stable &= state == ResultClientStripState.Placed
                && near(band.Top, settled.Top) && near(band.Bottom, settled.Bottom);
            bool fits = ResultBoardHold.TryPlace(RecordedWidth, RecordedHeight, settledStrip, 1160f, boardHeight, 0f, out LayoutRect rect, 2f);
            stable &= fits && near(rect.Y, placed.Y) && near(rect.Bottom, placed.Bottom);
            // The two predicates the runtime asks of a standing board every repaint: the wheel's hold, and
            // `BoardStillSafe`'s own `HeldGridFits`. Neither may refuse - a refusal here is the recorded withdrawal.
            wallNeverLifts &= IsHoldInPlace(placed, boardHeight, RecordedWall, settled.Top);
            perRepaintCheckNeverRefuses &= fits
                && ResultBoardHold.HeldGridFits(settledStrip,
                    new BoardStrip(settled.Top, settled.Bottom, true, RecordedWall, float.NaN, true, ResultFooterSource.ClientNoFooterSafeArea),
                    rect, boardHeight, RecordedHeight, 2f);
        }
        check(stable, "every repaint after the first visible one measures the same band and fits the board to the same rectangle - no second layout");
        check(wallNeverLifts, "the pressable wall never has to lift the standing board: it was already inside the band when it appeared");
        check(perRepaintCheckNeverRefuses, "the per-repaint safety check accepts the standing board on every one of those frames, so nothing is deferred and the vanilla page never comes back");

        // ---- 页面切换: a different screen starts from nothing ------------------------------------------------
        gate.Forget();
        check(gate.Samples == 0 && !gate.Settled && !float.IsFinite(gate.Wall),
            "a different result screen inherits neither the previous page's wall nor its agreement");
        check(ResultClientFailureStrip.Decide(201, gate, Evidence(900f, 0), out _, out _) == ResultClientStripState.WallNotSettled,
            "the next client failure page is not placed on its first reading either");
        check(ResultClientFailureStrip.Decide(202, gate, Evidence(900f, 0), out ResultClientBandReading nextBand, out _) == ResultClientStripState.Placed
              && near(nextBand.Bottom, 900f - RecordedClearance),
            "and it is placed on its own wall, never on the recorded page's 1176");

        // ---- 近控件空间不足: small from the first frame, and never withdrawn --------------------------------
        // A pressable control at 620 leaves 217.8 px: less than the 270 px readable board, more than the 96 px the
        // client strip has to offer at all. The old build fitted a 1049 px board first and lost it here; the band
        // the placement uses is now already this one.
        var tightGate = new ResultClientWallGate();
        check(ResultClientFailureStrip.Decide(301, tightGate, Evidence(620f, 0), out _, out _) == ResultClientStripState.WallNotSettled,
            "a page whose control leaves only about 218 px is not placed on its first reading either");
        check(ResultClientFailureStrip.Decide(302, tightGate, Evidence(620f, 0), out ResultClientBandReading tightBand, out _) == ResultClientStripState.Placed
              && near(tightBand.Bottom, 620f - RecordedClearance),
            "it is placed on the second identical reading, at the band that control leaves");
        check(tightBand.Height < ResultsFontGrowth.MinimumBoardHeight(RecordedRows, PanelGeometry.MinimumRowEm, PanelDensity.Compact),
            "that band is shorter than the readable board - exactly the case the old build answered by withdrawing the board");
        int tightFont = ResultsBandFit.FontForBand(tightBand.Height, RecordedRows, PanelDensity.Compact, 0);
        float tightHeight = ResultsBandFit.RowHeight(tightFont, PanelDensity.Compact) * RecordedRows;
        var tightStrip = new BoardStrip(tightBand.Top, tightBand.Bottom, true, 620f, float.NaN, true,
            ResultFooterSource.ClientNoFooterSafeArea, ResultsFontGrowth.FillCeiling(ResultFooterSource.ClientNoFooterSafeArea, true));
        bool tightPlaced = ResultBoardHold.TryPlace(RecordedWidth, RecordedHeight, tightStrip, 900f, tightHeight, 0f, out LayoutRect tightRect, 2f);
        check(tightPlaced && tightRect.Bottom <= 620f - ResultBoardHold.ButtonClearance,
            "the smaller board really clears the control the player has to press");
        bool tightStable = true;
        for (int frame = 303; frame < 331; frame++)
        {
            ResultClientStripState state = ResultClientFailureStrip.Decide(frame, tightGate, Evidence(620f, 0),
                out ResultClientBandReading band, out _);
            // No second, tighter band, no smaller glyph, and the per-repaint check keeps accepting the board.
            tightStable &= state == ResultClientStripState.Placed
                && near(band.Top, tightBand.Top) && near(band.Bottom, tightBand.Bottom)
                && ResultsBandFit.FontForBand(band.Height, RecordedRows, PanelDensity.Compact, 0) == tightFont
                && ResultBoardHold.HeldGridFits(tightStrip, tightStrip, tightRect, tightHeight, RecordedHeight, 2f)
                && IsHoldInPlace(tightRect, tightHeight, 620f, tightBand.Top);
        }
        check(tightStable, "when the space beside the control is short the board is small from its first visible frame and is never withdrawn and re-fitted-again");

        // A wall that leaves less than the client board's own minimum is refused as a band: this build keeps the
        // vanilla page and re-measures at the existing cadence instead of inventing a band from the page's foot (the
        // reading that put the recorded board over a control) or squeezing a board nobody could read into it.
        var close = new ResultClientWallGate();
        ResultClientFailureStrip.Decide(401, close, Evidence(450f, 0), out _, out _);
        check(ResultClientFailureStrip.Decide(402, close, Evidence(450f, 0), out ResultClientBandReading closeBand, out _)
                  == ResultClientStripState.BandUnusable && !closeBand.Fits,
            "a wall that would leave the band shorter than the client board's own minimum answers `client-band-unusable`: the vanilla page is kept, never a band invented from the screen's foot and never a board drawn outside the wall");
        // The page whose own fixed geometry cannot hold that minimum is refused as a page, on every frame: that is the
        // answer that never becomes "a board appeared and was taken back".
        var lowAnchor = new ResultClientFailureEvidence(1350f, RecordedWidth, RecordedHeight, RecordedGap,
            RecordedMinimumStrip, RecordedWaitingText, float.NaN);
        check(ResultClientFailureStrip.Decide(411, new ResultClientWallGate(), lowAnchor, out _, out _) == ResultClientStripState.GeometryUnusable
              && ResultClientFailureStrip.Decide(412, new ResultClientWallGate(), lowAnchor, out _, out _) == ResultClientStripState.GeometryUnusable,
            "a client page whose own fixed geometry cannot hold the minimum board is refused as a page, the same way on every frame");

        // ---- 真正安全变化: a control that really appears later is still honoured, once ----------------------
        // The same page as above, replayed on its own gate so this block stands on its own: the board is up in the
        // recorded band, and only then is a control switched on higher up the page.
        var drift = new ResultClientWallGate();
        ResultClientFailureStrip.Decide(150, drift, Evidence(RecordedWall, 0), out _, out _);
        check(ResultClientFailureStrip.Decide(151, drift, Evidence(RecordedWall, 0), out ResultClientBandReading onScreen, out _) == ResultClientStripState.Placed,
            "the recorded page places on its second identical reading, as it does everywhere else in this file");
        var onScreenStrip = new BoardStrip(onScreen.Top, onScreen.Bottom, true, RecordedWall, float.NaN, true,
            ResultFooterSource.ClientNoFooterSafeArea, ResultsFontGrowth.FillCeiling(ResultFooterSource.ClientNoFooterSafeArea, true));
        int onScreenFont = ResultsBandFit.FontForBand(onScreen.Height, RecordedRows, PanelDensity.Compact, 0);
        float onScreenHeight = ResultsBandFit.RowHeight(onScreenFont, PanelDensity.Compact) * RecordedRows;
        check(ResultBoardHold.TryPlace(RecordedWidth, RecordedHeight, onScreenStrip, 1160f, onScreenHeight, 0f, out LayoutRect standing, 2f),
            "and the board of that band is on the page");
        check(ResultClientFailureStrip.Measure(152, drift, Evidence(1100f, 0), out ResultClientBandReading moved, out _) == ResultClientStripState.Placed
              && near(moved.Bottom, 1100f - RecordedClearance),
            "a standing board is measured against the highest wall this page has shown, so a control switched on higher up tightens the band at once");
        check(!ResultBoardHold.HeldGridFits(onScreenStrip, new BoardStrip(moved.Top, moved.Bottom, true, 1100f),
                standing, onScreenHeight, RecordedHeight, 2f),
            "that control really reaches the standing board, so the per-repaint refusal still fires on a genuine change - one deferral, not a loop");
        check(!drift.Settled,
            "the moved wall is not called settled: the next placement waits for a second identical reading instead of chasing a moving control");
        check(ResultClientFailureStrip.Decide(153, drift, Evidence(1100f, 0), out ResultClientBandReading remeasured, out _) == ResultClientStripState.Placed
              && near(remeasured.Bottom, 1100f - RecordedClearance) && drift.Settled,
            "and the board is placed against the new wall on the very frame that wall reads the same twice, at its final geometry");

        // ---- 活动但不可测的控件不是「没有控件」，而且帧数从不授权落板 ------------------------------------
        //
        // Three horizons, because the previous design released at a fixed count: 30 frames, 120 frames and 500.
        // Each one drives the production entry point and asks about the *outcome*, not a bool: no placement state, no
        // band handed out (`reading.Fits` false), and therefore no rectangle from the placement step - nothing committed.
        foreach (int horizon in new[] { 30, 120, 500 })
        {
            var blind = new ResultClientWallGate();
            int placedFrames = 0;
            bool anyBand = false, committed = false;
            for (int frame = 0; frame < horizon; frame++)
            {
                ResultClientStripState state = ResultClientFailureStrip.Decide(1000 + frame, blind, Evidence(float.NaN, 1),
                    out ResultClientBandReading band, out _);
                if (state == ResultClientStripState.Placed) placedFrames++;
                if (band.Fits) anyBand = true;
                if (ResultClientBoard(band, out _)) committed = true;
            }
            check(placedFrames == 0 && !anyBand && !committed && !blind.Settled && blind.Samples == horizon,
                horizon + " frames of an active control nobody could measure authorise no placement at all - no state, no band, no rectangle, nothing committed; the sample count is a count and never a readiness signal");
        }

        // A single unmeasurable frame inside an otherwise empty page breaks the streak: the empty set has to be
        // consecutive, not merely frequent. (The page's own animation is the only thing that can produce this.)
        var streak = new ResultClientWallGate();
        check(ResultClientFailureStrip.Decide(1101, streak, Evidence(float.NaN, 0), out _, out _) == ResultClientStripState.WallNotSettled
              && ResultClientFailureStrip.Decide(1102, streak, Evidence(float.NaN, 1), out _, out _) == ResultClientStripState.WallNotSettled
              && !streak.Settled
              && ResultClientFailureStrip.Decide(1103, streak, Evidence(float.NaN, 0), out _, out _) == ResultClientStripState.WallNotSettled
              && ResultClientFailureStrip.Decide(1104, streak, Evidence(float.NaN, 0), out ResultClientBandReading afterBlind, out _) == ResultClientStripState.Placed
              && near(afterBlind.Bottom, RecordedWaitingText - RecordedClearance),
            "one active-but-unmeasurable frame resets the streak: the readiness signal is two consecutive complete scans of the same wall, not a count of them");

        // ---- 扫描看不见这一页：既不是「空集合」，也不是「inactive 控件」 ----------------------------------
        // 200 frames of a scan that could not see the whole page: nothing is placed and the answer names the scan.
        var unseeing = new ResultClientWallGate();
        int unseenPlaced = 0;
        for (int frame = 1201; frame <= 1400; frame++)
            if (ResultClientFailureStrip.Decide(frame, unseeing, Evidence(float.NaN, 0, scanComplete: false), out _, out _)
                == ResultClientStripState.Placed) unseenPlaced++;
        check(unseenPlaced == 0 && !unseeing.Settled,
            "200 frames of an incomplete scan place nothing: the scan cannot claim this page has no controls, so 'empty' is not a reading it may give");
        check(ResultClientFailureStrip.Decide(1401, unseeing, Evidence(float.NaN, 0, scanComplete: false), out _, out _) == ResultClientStripState.ScanIncomplete
              && ResultClientFailureStrip.Decide(1402, unseeing, Evidence(float.NaN, 0, parentCoverage: false), out _, out _) == ResultClientStripState.ScanIncomplete,
            "and the answer is named as a scan that could not see the page - for a missing registry/tree and for missing action-parent coverage alike");

        // ---- 只有那行等待文字：一个独立可信的就绪条件，两帧 -------------------------------------------------
        // A page whose controls the game switched off is a *complete* scan with nothing pressable and nothing
        // unmeasurable (the scan counts those as hidden), so its second identical complete frame is the board's frame -
        // and the wall that releases it is the waiting line the page really draws.
        var nothing = new ResultClientWallGate();
        check(ResultClientFailureStrip.Decide(701, nothing, Evidence(float.NaN, 0), out _, out _) == ResultClientStripState.WallNotSettled
              && ResultClientFailureStrip.Decide(702, nothing, Evidence(float.NaN, 0), out ResultClientBandReading quiet, out _) == ResultClientStripState.Placed
              && near(quiet.Bottom, RecordedWaitingText - RecordedClearance) && quiet.WaitingTextTightened
              && ResultClientBoard(quiet, out _),
            "inactive controls do not make the scan incomplete: two complete readings of the waiting line are the readiness signal, and the board is placed above that line");

        // ---- 两次读数之后控件才亮：条带收紧一次，下一帧落定，不活锁 ------------------------------------------
        // This is the honest boundary: a control can be switched on after the page has already been read twice. The
        // standing board is then measured against the higher wall at once, the per-repaint refusal fires once because a
        // control really reaches it, and the very next frame places against the tightened band.
        var late = new ResultClientWallGate();
        ResultClientStripState lateFirst = ResultClientFailureStrip.Decide(1501, late, Evidence(float.NaN, 0), out _, out _);
        ResultClientStripState lateSecond = ResultClientFailureStrip.Decide(1502, late, Evidence(float.NaN, 0),
            out ResultClientBandReading wide, out _);
        bool wideFitted = ResultClientBoard(wide, out LayoutRect wideBoard);
        check(lateFirst == ResultClientStripState.WallNotSettled && lateSecond == ResultClientStripState.Placed && wideFitted,
            "the two readings of the waiting line place the board, and the band it is fitted into ends above that line");
        var wideStrip = new BoardStrip(wide.Top, wide.Bottom, true, RecordedWaitingText, float.NaN, true,
            ResultFooterSource.ClientNoFooterSafeArea, ResultsFontGrowth.FillCeiling(ResultFooterSource.ClientNoFooterSafeArea, true));
        float wideHeight = ResultsBandFit.RowHeight(
            ResultsBandFit.FontForBand(wide.Height, RecordedRows, PanelDensity.Compact, 0), PanelDensity.Compact) * RecordedRows;
        check(ResultClientFailureStrip.Measure(1503, late, Evidence(HigherControl, 0), out ResultClientBandReading lateTightened, out _)
              == ResultClientStripState.Placed && near(lateTightened.Bottom, HigherControl - RecordedClearance)
              && !ResultBoardHold.HeldGridFits(wideStrip, new BoardStrip(lateTightened.Top, lateTightened.Bottom, true, HigherControl),
                  wideBoard, wideHeight, RecordedHeight, 2f),
            "a control switched on above the line after those two readings really reaches the standing board, so the existing overlap refusal fires - and it fires because the control is there, not because a count expired");
        check(ResultClientFailureStrip.Decide(1504, late, Evidence(HigherControl, 0), out ResultClientBandReading lateSettled, out _)
              == ResultClientStripState.Placed && late.Settled && near(lateSettled.Bottom, HigherControl - RecordedClearance),
            "the very next frame is the second reading of the tightened wall, and the board is placed at that geometry - one frame without a board, not many");
        var lateStrip = new BoardStrip(lateSettled.Top, lateSettled.Bottom, true, HigherControl, float.NaN, true,
            ResultFooterSource.ClientNoFooterSafeArea, ResultsFontGrowth.FillCeiling(ResultFooterSource.ClientNoFooterSafeArea, true));
        float lateHeight = ResultsBandFit.RowHeight(
            ResultsBandFit.FontForBand(lateSettled.Height, RecordedRows, PanelDensity.Compact, 0), PanelDensity.Compact) * RecordedRows;
        bool latePlaced = ResultBoardHold.TryPlace(RecordedWidth, RecordedHeight, lateStrip, 1160f, lateHeight, 0f,
            out LayoutRect lateRect, 2f) && lateRect.Bottom <= HigherControl - ResultBoardHold.ButtonClearance;
        int lateRefusals = 0;
        for (int frame = 1505; frame < 1535; frame++)
        {
            if (ResultClientFailureStrip.Decide(frame, late, Evidence(HigherControl, 0), out ResultClientBandReading band, out _)
                != ResultClientStripState.Placed || !near(band.Bottom, lateSettled.Bottom)) lateRefusals++;
            if (!ResultBoardHold.HeldGridFits(lateStrip, lateStrip, lateRect, lateHeight, RecordedHeight, 2f)) lateRefusals++;
        }
        check(latePlaced && lateRefusals == 0,
            "and it stays: thirty further repaints neither re-measure nor refuse it, so a control that arrives late costs exactly one deferral instead of a per-frame withdrawal loop");

        // ---- the runtime really asks this, in this order ---------------------------------------------------
        string root = SourceRoot();
        check(root != null, "the client-strip regression locates the production sources");
        if (root == null) return;
        string results = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        string geometry = File.ReadAllText(Path.Combine(root, "GuestPageGeometry.cs"));
        check(results.Contains("ResultClientFailureStrip.Decide(Time.frameCount, ClientWall,")
              && results.Contains("ResultClientFailureStrip.Measure(Time.frameCount, ClientWall,")
              && results.Contains("ClientWall.Forget();"),
            "FailureStrip asks the shared client-strip entry point for its first placement and for a standing board, and a page change drops the wall with the page");
        check(!results.Contains("ResultClientFailureBand.TryResolve(guestArea")
              && geometry.Contains("public static ResultClientStripState Decide(")
              && geometry.Contains("public static ResultClientStripState Measure("),
            "the band is no longer resolved inline beside the gate that exists to protect it: one call order, and it is the one these assertions drive");
        // (a client page has no outer complete-scan gate to carry them), and no frame count appears in the gate at all.
        // 1.19.45 re-point: the client branch hands the scan's own verdicts plus the two readings that really are walls
        // (the waiting line's own top and the highest pressable control), and it reports the wait once per page.
        check(results.Contains("controls.Complete, controls.ParentCoverageComplete, controls.Unmeasurable, slot.Source)")
              && results.Contains("bool wallPending = ResultClientPageActionPolicy.Pending(slot.HostSlotTop, slot.HintGlyphTop);")
              && results.Contains("ResultClientStripState.ScanIncomplete")
              && results.Contains("HeadingReports.Add(\"failure-client-wall-not-settled\")"),
            "the client branch hands the scan's completeness, parent coverage and unmeasurable count to the decision, names an incomplete scan as its own answer, and reports the wait once per page; 1.19.61 asks the wait about the two native slot readings (the return slot's own layout geometry and the waiting line's rendered glyphs) and hands the winning source to the decision, so a page with neither is the only 'no evidence' answer");
        check(!geometry.Contains("SampleBudget")
              && geometry.Contains("bool answerable = scanComplete && parentCoverageComplete")
              && geometry.Contains("public int Samples { get { return _samples; } }"),
            "no fixed frame budget exists in the client wall gate any more: the sample count is a count, and only two consecutive answerable complete readings release a placement");
    }

    // The wheel's own hold, driven the way `ResultsDisplay.Draw` drives it for a standing failure board: the button
    // the player has to press is the only wall, and the board's own band top is the other bound.
    private static bool IsHoldInPlace(LayoutRect placed, float height, float buttonTop, float bandTop)
        => ResultBoardHold.TryHold(new BoardStrip(0f, 0f, true, buttonTop), placed, RecordedHeight, out LayoutRect held, bandTop)
           && near(held.Y, placed.Y) && near(held.Height, height);

    // ---------------------------------------------------------------------------------------------------
    // 兜底：60 帧拒收预算不是「这一页看不见」的许可证
    // ---------------------------------------------------------------------------------------------------
    //
    // `ResultFallbackPolicy.ShouldFallback` 说的是「这一页已经被拒收够多次了，值得一块画在几何证明不了的页面上的板」。
    // 那是对**等待**的陈述，不是对**板下面站着什么**的陈述：fail-open 的条带是从窗口和扫描**读到**的控件推出来的，
    // 所以一个扫描看不见的控件（结构不完整 / action 父节点没覆盖）或量不到矩形的 active 控件，正好是这条编出来的条带
    // 能压上去的洞。这里的 200 帧循环就是那个洞的入口级证明：门一直拒、预算真的用完、兜底的算术真的能交出一个矩形，
    // 而 `MayAnswer` 在每一个那样的窗口上都说不能，于是生产保留原版页面（每页一条 `ReportHeldBack`，重试走既有节流）。
    // 同时断言另一半：页面**确实**被完整读到、只是几何真的冲突时，兜底照旧在 60 帧后真的落一块板——不是把兜底删掉。
    private static void FailOpenEscape(Action<bool, string> check)
    {
        // ---- the budget's own boundaries ---------------------------------------------------------------
        check(!ResultFallbackPolicy.MayAnswer(0, false, true)
              && !ResultFallbackPolicy.MayAnswer(ResultFallbackPolicy.MissBudget - 1, false, true)
              && ResultFallbackPolicy.MayAnswer(ResultFallbackPolicy.MissBudget, false, true)
              && ResultFallbackPolicy.MayAnswer(100000, false, true),
            "a page the scan read whole is still answered by the refusal budget exactly as before - 60 refusals, no timeout");
        check(!ResultFallbackPolicy.MayAnswer(0, false, false)
              && !ResultFallbackPolicy.MayAnswer(ResultFallbackPolicy.MissBudget, false, false)
              && !ResultFallbackPolicy.MayAnswer(100000, false, false),
            "a page whose own scan could not read it whole is never answered by that budget, however many refusals it costs");
        check(ResultFallbackPolicy.MayAnswer(0, true, false) && ResultFallbackPolicy.MayAnswer(100000, true, false),
            "the player's own F8 is still answered on such a page: that press is not a budget deciding anything");

        // ---- 200 frames per unreadable shape, in the order `Prepare` asks them --------------------------
        foreach (string shape in new[] { "active-but-unmeasurable", "scan-incomplete", "parent-coverage-incomplete" })
        {
            var gate = new ResultClientWallGate();
            int misses = 0, placedFrames = 0, budgetWindows = 0, wouldHaveCommitted = 0, allowedWindows = 0;
            for (int frame = 0; frame < 200; frame++)
            {
                ResultClientFailureEvidence evidence = Shape(shape);
                // The escape carries the same three facts its own scan reports; deriving the answer from them (not from
                // the shape's name) is what makes this the production question and not a restatement of it.
                bool pageReadable = shape == "active-but-unmeasurable" ? ResultFallbackPolicy.PageReadable(true, true, 1)
                    : shape == "scan-incomplete" ? ResultFallbackPolicy.PageReadable(false, true, 0)
                    : ResultFallbackPolicy.PageReadable(true, false, 0);
                ResultClientStripState state = ResultClientFailureStrip.Decide(2000 + frame, gate, evidence,
                    out ResultClientBandReading band, out _);
                if (state == ResultClientStripState.Placed && ResultClientBoard(band, out _)) placedFrames++;
                misses = ResultPlacementPolicy.Spend(misses);
                if (ResultPlacementPolicy.Decide(false, misses, false) != ResultPlacementOutcome.PlacedFallback) continue;
                budgetWindows++;
                // The escape's own arithmetic, at its worst: no measured wall at all, so its band is invented from the
                // window - exactly the shape the recorded page's second frame became.
                if (ResultFallbackLayout.TryResolve(RecordedHeight, RecordedTitleBottom, float.NaN, float.NaN, float.NaN,
                        ResultFallbackPolicy.MissBudget, out ResultFallbackPlan plan)
                    && ResultFallbackLayout.TryPlaceInBand(RecordedWidth, RecordedHeight, plan.Top, plan.Bottom, 900f,
                        ResultsFontGrowth.MinimumBoardHeight(RecordedRows, PanelGeometry.MinimumRowEm, PanelDensity.Compact), 0f,
                        out LayoutRect escaped))
                    wouldHaveCommitted++;
                if (ResultFallbackPolicy.MayAnswer(misses, false, pageReadable)) allowedWindows++;
            }
            check(placedFrames == 0,
                shape + ": 200 frames of a page the strict path cannot read place no board at all");
            check(budgetWindows > 0 && wouldHaveCommitted == budgetWindows,
                shape + ": the refusal budget really expires and the fail-open band really would have committed a rectangle on every one of those frames - so the window this guard closes is real, not vacuous (" + wouldHaveCommitted + " of " + budgetWindows + ")");
            check(allowedWindows == 0,
                shape + ": and the escape is withheld on every one of those frames, so production keeps the vanilla page and logs once per page");
        }

        // ---- the reasonable fallback is not removed -----------------------------------------------------
        check(ResultFallbackLayout.TryResolve(RecordedHeight, RecordedTitleBottom, RecordedWall, float.NaN, float.NaN,
                ResultsFontGrowth.MinimumBoardHeight(RecordedRows, PanelGeometry.MinimumRowEm, PanelDensity.Compact),
                out ResultFallbackPlan readable)
              && readable.Placed
              && ResultFallbackLayout.TryPlaceInBand(RecordedWidth, RecordedHeight, readable.Top, readable.Bottom, 900f,
                  ResultsFontGrowth.MinimumBoardHeight(RecordedRows, PanelGeometry.MinimumRowEm, PanelDensity.Compact), 0f,
                  out LayoutRect readableRect)
              && ResultFallbackPolicy.MayAnswer(ResultFallbackPolicy.MissBudget, false, true),
            "a page the scan did read whole, whose measured wall really conflicts with the board, still gets the fallback board after the same 60 refusals");

        // ---- what "readable" means, and that an inactive control is not an unread one --------------------
        check(ResultFallbackPolicy.PageReadable(true, true, 0)
              && !ResultFallbackPolicy.PageReadable(true, true, 1)
              && !ResultFallbackPolicy.PageReadable(false, true, 0)
              && !ResultFallbackPolicy.PageReadable(true, false, 0),
            "readability is the scan's own three facts: complete, parent coverage complete, and no active control left with no rectangle - an inactive control is counted as hidden by that scan and never appears here");

        // ---- production asks it where the escape's own scan is, before any band is invented -------------
        string root = SourceRoot();
        check(root != null, "the fail-open escape regression locates the production sources");
        if (root == null) return;
        string results = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        int guard = results.IndexOf("if (!ResultFallbackPolicy.MayAnswer(_strictMisses, Forced, pageReadable))", StringComparison.Ordinal);
        int readableAt = results.IndexOf("bool pageReadable = ResultFallbackPolicy.PageReadable(controls.Complete, controls.ParentCoverageComplete,", StringComparison.Ordinal);
        // 1.19.40 re-point: the band the escape invents is resolved by `ResultFallbackBandPolicy` (FallbackLayout.cs),
        // which keeps the page's own band first and the invented tiers after it. The gate above still has to be asked
        // first in the display, and the policy is still only reached after it.
        int bandAt = results.IndexOf("ResultFallbackBandPolicy.TryResolve(Screen.height, anchorBottom, anchorSource", StringComparison.Ordinal);
        string bandSource = File.ReadAllText(Path.Combine(root, "FallbackLayout.cs"));
        int pageBandAt = bandSource.IndexOf("ResultsBounds.TryResolve(screenHeight, anchorBottom, anchorSource", StringComparison.Ordinal);
        int inventedAt = bandSource.IndexOf("ResultFallbackLayout.TryResolve(screenHeight, anchorBottom, walls.PressableTop", StringComparison.Ordinal);
        check(guard >= 0 && readableAt >= 0 && readableAt < guard && bandAt > guard
              && pageBandAt > 0 && inventedAt > pageBandAt
              && results.Contains("why = ResultFallbackPolicy.UnreadablePage +"),
            "the escape's own scan is asked before it invents a band from the window, and its refusal names that fault");
        // 1.19.43: the same helper now carries a third reason - a failure page whose own lower bound has not been
        // observed yet - so the assertion names the helper and all three reasons instead of one string.
        check(results.Contains("ReportHeldBack(\"prepare\", wallUnobserved ? \"fail-open-wall-unobserved\"")
              && results.Contains(": pageReadable ? \"fallback-unavailable\" : \"fail-open-page-unreadable\","),
            "the refusal is reported through the once-per-page helper, under its own reason (including the failure page's unobserved wall), so it cannot flood a live log");
        check(!results.Contains("_failureInventoryKnown"),
            "no cached copy of that fact exists beside the scan: the escape reads it where it measures it, so no caller order can bypass it");
        // Why the final-geometry gate could never have been the answer here: the page's "ever measured" latch is only
        // fed after the strip answers, and a gate refusal returns before it. So `FailOpenAllowed` is `true` on exactly
        // these pages, and this escape needed a question of its own.
        int placementAt = results.IndexOf("private static bool Placement(Component page, bool succeeded", StringComparison.Ordinal);
        int failureStripAt = placementAt < 0 ? -1
            : results.IndexOf("if (!FailureStrip(page", placementAt, StringComparison.Ordinal);
        int finalLatchAt = failureStripAt < 0 ? -1
            : results.IndexOf("NoteFinalGeometry(room > 0f", failureStripAt, StringComparison.Ordinal);
        check(placementAt >= 0 && failureStripAt > placementAt && finalLatchAt > failureStripAt,
            "a strip refusal returns before the page's 'ever measured its final geometry' latch is fed, which is why the fail-open decision looked satisfied on these pages");
    }

    // One frame of each shape of "the page could not answer", as production's own measurement would hand it over.
    private static ResultClientFailureEvidence Shape(string shape)
        => shape == "active-but-unmeasurable" ? Evidence(float.NaN, 1)
            : shape == "scan-incomplete" ? Evidence(float.NaN, 0, scanComplete: false)
            : Evidence(float.NaN, 0, parentCoverage: false);

    // ---------------------------------------------------------------------------------------------------
    // 1.19.47：自动模式的最新用户合同
    // ---------------------------------------------------------------------------------------------------
    //
    // 两页的上界问的不是同一个问题，这是这条合同的核心：失败页的上界是窗口自己的上沿安全留边——原生「任务失败」标题
    // 只是说明文字，不再卡表的位置，也不再通过 `ResultsBandFit` 卡字号；成功页的上界必须仍是原生完成信息（关卡名 / 分区框
    // / 已清分区图标及其说明）的实测下界，测不到就保留原版页，绝不用窗口比例或等比规则换掉它。
    //
    // 两页共用的是其余部分，并且都断言在这里：底对齐到真实下界（`TryPlace(..., bottomAligned: true)`）、字号按分辨率等比
    // 设目标（1080=12px、1440=16px，窗口高度的 1/90）、目标只封顶不改行数（放不下就整行 fit，再不够就交给滚轮）、下界只认
    // 页面真正画出来的那堵墙（页面自己的脚和空扫描都不是）。
    private static void AutoModeContract(Action<bool, string> check)
    {
        // ---- 目标字号：同一条归一化比例，1080 12px / 1440 16px -----------------------------------------
        check(ResultAutoBoardContract.TargetGlyphPixels(1080f) == 12
              && ResultAutoBoardContract.TargetGlyphPixels(1440f) == 16
              && ResultAutoBoardContract.TargetGlyphPixels(2160f) == 24,
            $"the automatic mode's target is the same share of the window in every frame: 12px at 1080p, 16px at 1440p, 24px at 2160p (got {ResultAutoBoardContract.TargetGlyphPixels(1080f)}/{ResultAutoBoardContract.TargetGlyphPixels(1440f)}/{ResultAutoBoardContract.TargetGlyphPixels(2160f)})");
        check(ResultAutoBoardContract.TargetGlyphPixels(720f) == 8
              && ResultAutoBoardContract.TargetGlyphPixels(float.NaN) == ResultAutoBoardContract.BaselineGlyphPixels
              && ResultAutoBoardContract.TargetGlyphPixels(0f) == ResultAutoBoardContract.BaselineGlyphPixels,
            "a 720-high window aims at 8px, and a window whose height nobody could read keeps the baseline instead of inventing one");
        check(ResultAutoBoardContract.TargetGlyphPixels(100000f) == ResultAutoBoardContract.MaximumGlyphPixels,
            "a pathological window height is capped at " + ResultAutoBoardContract.MaximumGlyphPixels + "px, so no page can turn the table into a poster");

        // ---- 上界：失败页看窗口上沿，成功页看完成信息 ---------------------------------------------------
        bool failureAnchored = ResultAutoBoardContract.TryUpperAnchor(false, 1440f, float.NaN,
            out float failureAnchor, out ResultBoundSource failureSource);
        check(failureAnchored && Math.Abs(failureAnchor - 1440f / 135f) <= 0.01f
              && failureSource == ResultBoundSource.ScreenTopMargin,
            "the failure page's upper edge is the window's own safe top margin (8 design px scaled: " + (1440f / 135f).ToString("0.00")
            + " at 1440) and it is an edge even when no completion section, title or other anchor was read");
        check(ResultAutoBoardContract.TopMargin(1080f) == 8f
              && Math.Abs(ResultAutoBoardContract.TopMargin(1440f) - 10.6667f) <= 0.01f,
            "that margin is the same 8/1080 of the window at both reference frames");
        bool successAnchored = ResultAutoBoardContract.TryUpperAnchor(true, 1440f, 519f,
            out float successAnchor, out ResultBoundSource successSource);
        check(successAnchored && Math.Abs(successAnchor - 519f) <= 0.01f && successSource == ResultBoundSource.IconRow,
            "the success page's upper edge is the completion section's own measured lower edge and nothing else");
        check(!ResultAutoBoardContract.TryUpperAnchor(true, 1440f, float.NaN, out float refusedAnchor, out ResultBoundSource refusedSource)
              && !float.IsFinite(refusedAnchor) && refusedSource == ResultBoundSource.None,
            "a success page that cannot measure that edge gets no anchor at all, so the vanilla screen is kept rather than the level name and sector icons covered");
        check(!ResultAutoBoardContract.TryUpperAnchor(true, 1440f, 1439f, out _, out _),
            "and a section edge that lies on the window's own foot is not an edge either");

        // ---- 下界：原版等待提示 / 可按控件才是墙，空扫描与屏幕自己的页脚永远不是 ----------------------------
        //
        // 1.19.47 只换了上界，下界一个字都没松：这一页一堵墙都量不到时仍然明确回答「不可信」，而页面自己的
        // 下沿（`ResultsGuestPageGeometry.PageFloor`）只做封顶。这两条用生产里那两个入口直接驱动。
        var fixedPage = new ResultsSafeArea(32f, ResultsGuestPageGeometry.PageFloor(1440f));
        check(!ResultClientFailureBand.TryResolve(fixedPage, 1440f, 96f, float.NaN, float.NaN, float.NaN, out _),
            "a client failure page with no waiting-status line and no pressable control still gets no band: an empty scan is not a wall");
        check(ResultClientFailureBand.TryResolve(fixedPage, 1440f, 96f, 1207f, float.NaN, 1207f, out ResultClientBandReading clientWallBand)
              && clientWallBand.WaitingTextTightened
              && Math.Abs(clientWallBand.Bottom - (1207f - ResultsBounds.Clearance(1440f))) <= 0.01f,
            $"the waiting-status line the page really draws is still the wall, pulled up by the same clearance: bottom={clientWallBand.Bottom:0} of a {1207f - ResultsBounds.Clearance(1440f):0} foot");

        // ---- 字号：目标只封顶，整行 fit 次之，滚动是最后一条 ----------------------------------------------
        //
        // 生产里这条算术就是 `ResultsBandFit.FontForBand` 与 `ResultAutoBoardContract.TargetGlyphPixels` 的组合
        // （`FitBoard` 先搜索进带、再被目标封顶），所以这里直接断言这两处，不再经过只给测试看的 helper。
        int bandFit1122 = ResultsBandFit.FontForBand(1122f, 30, PanelDensity.Compact, int.MaxValue);
        check(Math.Min(ResultAutoBoardContract.TargetGlyphPixels(1440f), bandFit1122) == 16,
            $"a 1122px band holds the 16px target over 30 rows - the band alone allows {bandFit1122}px - so the target is what is drawn");
        int tightFit = ResultsBandFit.FontForBand(480f, 30, PanelDensity.Compact, int.MaxValue);
        check(tightFit == 10 && ResultsBandFit.RowHeight(tightFit, PanelDensity.Compact) * 30 <= 480f + 0.5f,
            $"a 480px band answers the largest size that fits whole rows - {tightFit}px, {ResultsBandFit.RowHeight(tightFit, PanelDensity.Compact) * 30:0}px of 480px - instead of dropping a row");
        int tinyFit = ResultsBandFit.FontForBand(200f, 30, PanelDensity.Compact, int.MaxValue);
        check(tinyFit == 4 && tinyFit < ResultsFontGrowth.MinimumReadablePixels,
            $"a very short legal safe area goes below the readable floor it would have liked and still keeps all thirty rows ({tinyFit}px)");

        // ---- 同一个 roster，两种分辨率：归一化位置与大小一致 ------------------------------------------
        //
        // 回放日志里的两帧（scratch/survival-retest-20261122-225706-193/LogOutput.log:3320 与 :4943）：两条带的下界都是
        // 主机「返回大厅」按钮上沿减 MessageGap，上界都是上面那条窗口上沿，room 就分别是 806px 与 1082px。
        const float wall1080 = 846f, wall1440 = 1130f, messageGap = 16f;
        float top1080 = ResultAutoBoardContract.TopMargin(1080f) + ResultsBounds.Clearance(1080f);
        float top1440 = ResultAutoBoardContract.TopMargin(1440f) + ResultsBounds.Clearance(1440f);
        float bottom1080 = wall1080 - messageGap, bottom1440 = wall1440 - messageGap;
        int glyph1080 = Math.Min(ResultAutoBoardContract.TargetGlyphPixels(1080f),
            ResultsBandFit.FontForBand(bottom1080 - top1080, 30, PanelDensity.Compact, int.MaxValue));
        int glyph1440 = Math.Min(ResultAutoBoardContract.TargetGlyphPixels(1440f),
            ResultsBandFit.FontForBand(bottom1440 - top1440, 30, PanelDensity.Compact, int.MaxValue));
        check(glyph1080 == 12 && glyph1440 == 16,
            $"the same 30-row board is drawn at the target in both frames: 12px at 1080 (got {glyph1080}) and 16px at 1440 (got {glyph1440})");
        float height1080 = ResultsBandFit.RowHeight(glyph1080, PanelDensity.Compact) * 30;
        float height1440 = ResultsBandFit.RowHeight(glyph1440, PanelDensity.Compact) * 30;
        float plateTop1080 = bottom1080 - height1080, plateTop1440 = bottom1440 - height1440;
        check(Math.Abs(height1440 / 1440f - height1080 / 1080f) <= 0.001f,
            $"the board is the same share of the window in both frames: {height1080:0}px of 1080 is {height1080 * 100f / 1080f:0.0}%, {height1440:0}px of 1440 is {height1440 * 100f / 1440f:0.0}%");
        check(Math.Abs(plateTop1440 / 1440f - plateTop1080 / 1080f) <= 0.01f,
            $"and it starts at the same normalized height above the wall: {plateTop1080:0}/1080 = {plateTop1080 / 1080f:0.0000}, {plateTop1440:0}/1440 = {plateTop1440 / 1440f:0.0000} (the residual is the unscaled {messageGap:0}px message gap)");

        // The bottom alignment is the production call, driven the way `Fit` drives it.
        var strip = new BoardStrip(top1080, bottom1080, true, wall1080, 96f, true,
            ResultFooterSource.NamedControl, ResultsFontGrowth.BandFillCeiling);
        bool bottomPlaced = ResultBoardHold.TryPlace(1920f, 1080f, strip, 900f, height1080, 0f,
            out LayoutRect bottomBoard, 0f, bottomAligned: true);
        check(bottomPlaced && Math.Abs(bottomBoard.Bottom - bottom1080) <= 0.01f
              && Math.Abs(bottomBoard.Y - plateTop1080) <= 0.01f,
            $"the board's lower edge is the measured wall and it grows upward from it: bottom={bottomBoard.Bottom:0} of the {bottom1080:0} wall, top={bottomBoard.Y:0}");

        // ---- the wiring: the two rules above are what the runtime really asks --------------------------
        string root = SourceRoot();
        check(root != null, "the automatic-mode contract locates the production sources");
        if (root == null) return;
        string display = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        string bounds = File.ReadAllText(Path.Combine(root, "ResultsBounds.cs"));
        check(display.Contains("int autoTarget = ResultAutoBoardContract.TargetGlyphPixels(Screen.height);")
              && display.Contains("fitted = autoTarget;"),
            "the fit is capped by the resolution-scaled target before the player's own zoom and before the width loop spend anything");
        // 1.19.60: the failure page's upper edge is one rule for both roles. 1.19.47 took the mission-failed title out of
        // the *client* page's anchor and left it in the host's - a contract the recorded 1.19.59 client page disproved:
        // strict band 32..1185.7 against fail-open 488.33..1185.7 on one page, with the strict board bottom-aligned and
        // therefore covering the lower 40-90 px of the title. Both checks below keep their own meaning (one shared
        // anchor, a measured control carrying its own source, no clearance invented twice) and only name the helper that
        // now answers it. The host call site and its inline maximum are unchanged.
        // 1.19.60 (independent review F2): the client branch no longer hands the gap-included `FooterControls.BoardTop` to
        // this helper - that put the same spacing on twice (504.33 instead of the fail-open's 488.33) and named a measured
        // control that nobody had measured. It hands the gap-free edge over instead, and `TryFailureAnchor` refuses to name
        // a control within half a pixel of the title. The rest of the assertion keeps its own meaning: one shared anchor,
        // a measured control carrying its own source, and the host call site plus its inline maximum unchanged.
        check(display.Contains("ResultAutoBoardContract.TryUpperAnchor(false, Screen.height, float.NaN, out float screenAnchor, out bandTopSource)")
              && display.Contains("float rawControlTop = float.IsFinite(controls.BoardTop) ? controls.BoardTop - MessageGap : float.NaN;")
              && display.Contains("ResultAutoBoardContract.TryFailureAnchor(Screen.height, titleBottom, rawControlTop,")
              && display.Contains("out float guestAnchor, out bandTopSource)")
              && display.Contains("bandTopSource = ResultBoundSource.MeasuredControl;")
              && display.Contains("ResultsBounds.TryResolve(Screen.height, top, bandTopSource,"),
            "both failure roles use the common anchor - the host inline, the client through the one helper with a gap-free control edge - and a measured control still carries its own source");
        check(!display.Contains("float guestAnchor = titleBottom + configuredOffset;"),
            "the client's anchor is never the title plus a gap the strip would add again: the strip adds the clearance exactly once (1.19.60)");
        check(Count(display, "bottomAligned: true") >= 2 && !display.Contains("bottomAligned: false"),
            "both placement entry points hand `TryPlace` the bottom-aligned rule, so a held board, a re-measure and a fresh page cannot disagree about it");
        // 1.19.60: the policy now carries the failure page's title edge - once, in `TryFailureAnchor`, where both walls
        // (title, measured control) and the window margin are weighed. It still carries no helper the runtime never
        // calls, which is what this assertion has always been about.
        check(bounds.Contains("public static bool TryUpperAnchor(bool successPage, float screenHeight, float completionBottom,")
              && bounds.Contains("public static bool TryFailureAnchor(float screenHeight, float titleBottom, float measuredControlTop,")
              && bounds.Contains("public static int TargetGlyphPixels(float screenHeight)")
              && bounds.Contains("public static float TopMargin(float screenHeight)")
              && !bounds.Contains("FitGlyph") && !bounds.Contains("LowerWall"),
            "the policy a result page is wired to answers the upper edge for both page kinds - the success section, or the failure page's title and measured control over the window margin - and carries no helper the runtime never calls");
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj"))) directory = directory.Parent;
        return directory != null ? directory.FullName : null;
    }

    private static int Count(string text, string needle)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(needle)) return 0;
        int total = 0, index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0) { total++; index += needle.Length; }
        return total;
    }

    // The text between two source anchors, so one method's body can be asserted without pinning the file's line
    // numbers; an anchor that is not found yields an empty slice, which every assertion here treats as a failure.
    private static string Between(string text, int start, int end)
        => start >= 0 && end > start ? text.Substring(start, end - start) : string.Empty;

    // The same stand-in the growth scenarios measure with: `rows` rows of `rowEm` ems, the whole table `emWidth`
    // ems wide, both growing with the glyph size exactly as `PanelGeometry.Build` does.
    private static Func<int, ResultTableSize> Table(int rows, float rowEm, float emWidth)
        => size => new ResultTableSize(emWidth * size, rows * rowEm * size);

    private static ResultTableSize Measure(int rows, float rowEm, float emWidth, int fontSize)
        => Table(rows, rowEm, emWidth)(fontSize);

    private static bool near(float actual, float expected) => Math.Abs(actual - expected) <= 0.01f;
}
