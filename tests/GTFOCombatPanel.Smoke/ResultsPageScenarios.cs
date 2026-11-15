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

        // ---- the wheel: one page's preference, one step, and never past the strip ---------------------------
        check(near(ResultsZoom.Default, 1f) && ResultsZoom.Minimum < ResultsZoom.Default && ResultsZoom.Maximum > ResultsZoom.Default,
            $"the wheel's own default is 1.0 inside a bounded range ({ResultsZoom.Minimum}..{ResultsZoom.Maximum})");
        check(ResultsZoom.Section == "Results" && ResultsZoom.SuccessKey == "SuccessFontScale" && ResultsZoom.FailureKey == "FailureFontScale",
            "the two preferences are stored under readable keys in the section the rest of the result settings live in");
        check(ResultsZoom.KeyFor(ResultsZoom.Page(true)) == ResultsZoom.SuccessKey
              && ResultsZoom.KeyFor(ResultsZoom.Page(false)) == ResultsZoom.FailureKey
              && ResultsZoom.Page(true) != ResultsZoom.Page(false),
            "the success page and the failure page each read and write their own key, so one page's setting cannot move the other");
        check(ResultsZoom.KeyFor(ResultPageKind.Success) != ResultsZoom.KeyFor(ResultPageKind.Failure),
            "and the two keys are distinct");

        // Direction: a notch up enlarges. The convention IMGUI reports the wheel with lives in one constant, and
        // this is the assertion that would catch it being read backwards.
        check(ResultsZoom.Direction(-1f) == 1 && ResultsZoom.Direction(1f) == -1,
            $"a notch up is a positive step and a notch down a negative one (up={ResultsZoom.Direction(-1f)}, down={ResultsZoom.Direction(1f)})");
        check(ResultsZoom.Direction(0f) == 0 && ResultsZoom.Direction(float.NaN) == 0 && ResultsZoom.Direction(0.00001f) == 0,
            "a wheel that did not move - a resting trackpad, or not a number at all - is not a notch");

        check(near(ResultsZoom.Next(1f, -1f), 1.1f) && near(ResultsZoom.Next(1f, 1f), 0.9f),
            $"one notch is exactly one step: 1.0 -> {ResultsZoom.Next(1f, -1f):0.##} up, 1.0 -> {ResultsZoom.Next(1f, 1f):0.##} down");
        check(near(ResultsZoom.Next(ResultsZoom.Maximum, -1f), ResultsZoom.Maximum)
              && near(ResultsZoom.Next(ResultsZoom.Minimum, 1f), ResultsZoom.Minimum),
            $"the wheel stops at {ResultsZoom.Minimum} and {ResultsZoom.Maximum} instead of wrapping round them");
        check(near(ResultsZoom.Next(1f, 0f), 1f), "a wheel that did not move leaves the stored scale exactly as it was");
        int steps = 0;
        for (float scale = ResultsZoom.Minimum; scale < ResultsZoom.Maximum - 0.0001f; scale = ResultsZoom.Next(scale, -1f)) steps++;
        check(steps == (int)Math.Round((ResultsZoom.Maximum - ResultsZoom.Minimum) / ResultsZoom.Step),
            $"the range is a whole number of steps the wheel can reach from either bound ({steps} notches)");

        check(near(ResultsZoom.Clamp(float.NaN), ResultsZoom.Default)
              && near(ResultsZoom.Clamp(-3f), ResultsZoom.Minimum)
              && near(ResultsZoom.Clamp(9f), ResultsZoom.Maximum),
            "a stored value nobody could read falls back to 1.0, and one out of range to the bound it passed");
        check(near(ResultsZoom.Snap(1.04f), 1f) && near(ResultsZoom.Snap(1.07f), 1.1f) && near(ResultsZoom.Snap(0.5f), 0.5f),
            $"every stored scale is a value the wheel can produce again ({ResultsZoom.Snap(1.04f):0.##}, {ResultsZoom.Snap(1.07f):0.##})");

        // The size the board is drawn at: the wheel scales the size the page's strip produced. Asked without a box,
        // the answer is conservative - scaled down, never enlarged - because a size nobody measured cannot be
        // promised to fit; asked with the page's own strip and width, a notch up is allowed the room the fit's
        // comfort band leaves free, and refused the moment the board would reach the button.
        check(ResultsZoom.ScaleSize(10, 1f) == 10, "a scale of 1.0 draws the board at the size its own strip produced");
        check(ResultsZoom.ScaleSize(10, ResultsZoom.Maximum) == 10,
            "a scale above 1 is clamped onto the strip's own size, so the wheel can never make the board cover the button");
        check(ResultsZoom.ScaleSize(20, 0.55f) == 11 && ResultsZoom.ScaleSize(10, 0.5f) == ResultsFontGrowth.MinimumReadablePixels,
            $"a smaller scale draws a smaller board, and never below the readable minimum ({ResultsZoom.ScaleSize(20, 0.55f)}px, {ResultsZoom.ScaleSize(10, 0.5f)}px)");
        check(ResultsZoom.ScaleSize(10, float.NaN) == 10,
            "a scale that could not be read keeps the board at the strip's own size");
        int zoomedOut = ResultsZoom.ScaleSize(readable, 0.5f);
        check(Measure(30, 1.5f, 40f, zoomedOut).Height <= 511f * ResultsFontGrowth.StripFillCeiling + 0.5f,
            $"the zoomed-out board still fits the strip it is drawn in: {Measure(30, 1.5f, 40f, zoomedOut).Height:0}px of 511px, drawn at {zoomedOut}px");
        check(zoomedOut < readable && zoomedOut >= ResultsFontGrowth.MinimumReadablePixels,
            $"and the wheel really changes the board it is aimed at ({readable}px -> {zoomedOut}px)");
        int zoomedIn = ResultsZoom.ScaleSize(readable, ResultsZoom.Maximum, 511f, 1392f, size => Measure(30, 1.5f, 40f, size));
        check(zoomedIn > readable && Measure(30, 1.5f, 40f, zoomedIn).Height <= 511f + 0.5f,
            $"a notch up really enlarges the board inside the strip it is drawn in ({readable}px -> {zoomedIn}px, {Measure(30, 1.5f, 40f, zoomedIn).Height:0}px of 511px)");
        check(zoomedIn == 11,
            $"and it stops one step above the fit, where the next size (12px, 540px) would pass the button ({zoomedIn}px)");
        check(ResultsZoom.ScaleSize(readable, 1f, 511f, 1392f, size => Measure(30, 1.5f, 40f, size)) == readable,
            "a scale of 1.0 asked with the box returns exactly the size the strip produced");
        check(ResultsZoom.ScaleSize(10, ResultsZoom.Maximum, 287f, 1392f, size => Measure(30, 1.5f, 40f, size)) == 6,
            "a strip that cannot hold a bigger board than 6px refuses the enlargement instead of covering the button");
        check(ResultsZoom.ScaleSize(10, 2f, 511f, 1392f, size => ResultTableSize.None) == 10
              && ResultsZoom.ScaleSize(10, 2f, 511f, 1392f, size => throw new InvalidOperationException("no atlas")) == 10,
            "a measurement that reports nothing or throws keeps the strip's own size rather than guessing a bigger one");

        // ---- the floor of the wheel, and a box that cannot hold even that --------------------------------
        //
        // A size nobody can read is not a zoom level: the absolute minimum is the floor of every answer, including a
        // size handed in below it, and the size handed back can never be one the grid builder would drop.
        check(ResultsZoom.ScaleSize(1, 1f) == ResultsFontGrowth.MinimumReadablePixels
              && ResultsZoom.ScaleSize(0, 0.5f) == ResultsFontGrowth.MinimumReadablePixels
              && ResultsZoom.ScaleSize(-40, -1f) == ResultsFontGrowth.MinimumReadablePixels,
            $"a fitted size below the readable minimum is raised to it rather than drawn ({ResultsZoom.ScaleSize(1, 1f)}px)");
        check(ResultsZoom.ScaleSize(ResultsFontGrowth.MinimumReadablePixels, 0.5f, 511f, 1392f,
                  size => Measure(30, 1.5f, 40f, size)) == ResultsFontGrowth.MinimumReadablePixels,
            "and a zoomed-out board already at the floor stays exactly on the floor");
        int tooShort = ResultsZoom.ScaleSize(10, 2f, 20f, 1392f, size => Measure(30, 1.5f, 40f, size));
        check(tooShort == ResultsFontGrowth.MinimumReadablePixels,
            $"a strip too short for even the floor answers with the readable minimum instead of a size nothing can draw ({tooShort}px)");
        bool staysInside = true, staysReadable = true;
        for (int fitted = ResultsFontGrowth.MinimumReadablePixels; fitted <= 40; fitted++)
        {
            for (float scale = ResultsZoom.Minimum; scale <= ResultsZoom.Maximum + 0.001f; scale += ResultsZoom.Step)
            {
                int size = ResultsZoom.ScaleSize(fitted, scale, 511f, 1392f, probe => Measure(30, 1.5f, 40f, probe));
                staysInside &= Measure(30, 1.5f, 40f, size).Height <= 511f + 0.5f;
                staysReadable &= size >= ResultsFontGrowth.MinimumReadablePixels;
            }
        }
        check(staysInside && staysReadable,
            "every fitted size and every notch of the range answers with a board that still fits the strip and is still readable");

        // ---- the wiring the two rules above depend on, read from the production sources --------------------
        Wiring(check);
        // ---- the client failure page: the board's first visible layout is its final one ---------------------
        ClientStrip(check);
        // ---- 兜底：拒收预算不是「看不见这一页」的许可证 ------------------------------------------------------
        FailOpenEscape(check);
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
        check(panel.IndexOf("ResultsZoomEvent()", StringComparison.Ordinal) >= 0
              && panel.IndexOf("ResultsZoomEvent()", StringComparison.Ordinal) < panel.IndexOf("EventType.Repaint", StringComparison.Ordinal),
            "OnGUI reads the wheel before the Repaint gate, so the existing draw path is untouched");
        check(results.Contains("_hitRect = new LayoutRect(") && results.Contains("if (_hitFrame == frame) return true;")
              && results.Contains("if (!_hitValid || _hitSucceeded != succeeded) return false;"),
            "the wheel is judged against the rectangle the board was last drawn at, with the gesture locked per frame");
        check(Count(results, "ClearHit();") >= 3 && results.Contains("ClearHit();"),
            "the cached wheel rectangle is dropped when the board changes page or is hidden");
        check(panel.Contains("ResultsDisplay.ZoomAt(current.mousePosition, succeeded, Time.frameCount)")
              && panel.Contains("current.Use();"),
            "a notch that landed on the board is consumed rather than handed to the page underneath");
        check(plugin.Contains("SuccessFontScale = Config.Bind(") && plugin.Contains("FailureFontScale = Config.Bind(")
              && plugin.Contains("page == ResultPageKind.Success ? SuccessFontScale : FailureFontScale")
              && plugin.Contains("ConfigWatcher.RecordOwnSave();")
              && plugin.Contains("<= 0.0005f) return false;"),
            "the two pages persist independent keys through the plugin's own save path, and a notch that changes nothing writes nothing");
        check(Count(results, "FitBoard(content, room, boardWidth, succeeded") >= 2
              && results.Contains("ResultsZoom.ScaleSize(fitted, Plugin.ResultFontScale(succeeded)"),
            "both placement paths fit the board through the wheel scale of the page it belongs to");
        check(results.Contains("if (!FallbackRecoveryDue(succeeded, _page) && RefreshBoardData")
              && results.Contains("ResultAnchorRecovery.ShouldRetry(_fallbackRecoveries")
              && results.Contains("ResultAnchorRecovery.Record(_fallbackRecoveries)")
              && results.Contains("ResultAnchorRecovery.LatchFallback(_fallbackRecoveries")
              && policy.Contains("public const int MaximumRecoveries = 1;"),
            "the fallback recovery hangs off the refresh path and stays bounded to one attempt per page");

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

        // The gesture lock is asked before the rectangle is: the notch that changed the scale is the one that took the
        // rectangle away, so the rest of that same gesture has to be answered by the frame it belongs to.
        int zoomAt = results.IndexOf("internal static bool ZoomAt(", StringComparison.Ordinal);
        int frameLock = results.IndexOf("if (_hitFrame == frame) return true;", StringComparison.Ordinal);
        int rectangleGate = results.IndexOf("if (!_hitValid || _hitSucceeded != succeeded) return false;", StringComparison.Ordinal);
        check(zoomAt >= 0 && frameLock > zoomAt && rectangleGate > frameLock,
            "the same-frame gesture lock is asked before the rectangle's validity, so a rebuild cannot cut one wheel gesture in half");

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
    // The waiting-status line the page laid out at 1147: measured, logged, never a wall.
    private const float RecordedWaitingText = 1147f;
    // The pressable control the scan found on the *second* frame of that page.
    private const float RecordedWall = 1176f;
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
            "the frame on which a client failure page has not shown a pressable control yet is not a placement frame - that frame is what drew the recorded 990px board the run then lost");
        check(near(firstPage.Top, 380.83f) && near(firstPage.Bottom, 1429.33f),
            "the page's fixed geometry is still read on that frame (380.83..1429.33 of a 2560x1440 window), so the wait is about the wall and not about a page nobody could measure");
        check(!firstBand.Fits && !float.IsFinite(gate.Wall) && gate.Samples == 1,
            "no band is handed out and no wall is invented while the page has not shown a pressable control");

        // ---- 锚点稳定: the wall the page really has, read the same way twice --------------------------------
        ResultClientStripState changing = ResultClientFailureStrip.Decide(102, gate, Evidence(RecordedWall, 0),
            out _, out _);
        check(changing == ResultClientStripState.WallNotSettled && near(gate.Wall, RecordedWall),
            "the frame the control appears is a changed reading, not an agreement: the page is still revealing, so the board is not fitted against it yet");
        ResultClientStripState settledState = ResultClientFailureStrip.Decide(103, gate, Evidence(RecordedWall, 0),
            out ResultClientBandReading settled, out _);
        check(settledState == ResultClientStripState.Placed && gate.Settled,
            "the second identical complete reading is the placement frame, and it is a settled wall rather than any kind of count");
        check(near(settled.Top, 380.83f) && near(settled.Bottom, RecordedWall - RecordedClearance) && near(settled.Height, 773.83f),
            "the band is the tightened one the recorded page only produced *after* the board was already up (380.83..1154.67, 773px)");
        check(settled.PressableTightened && settled.WaitingTextCovered,
            "the foot came from a pressable control, and the waiting-status line crossing it is still only a logged fact");

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

        // A wall that leaves less than the client board's own minimum is not a wall for this band at all: that is the
        // documented rule of `ResultClientFailureBand` (a control that close to the title does not shorten the band
        // under a drawable board). The page keeps its fixed geometry, and the control is protected by the interaction
        // scan's overlap refusal - never by a band too short to hold a board.
        var close = new ResultClientWallGate();
        ResultClientFailureStrip.Decide(401, close, Evidence(450f, 0), out _, out _);
        check(ResultClientFailureStrip.Decide(402, close, Evidence(450f, 0), out ResultClientBandReading closeBand, out _) == ResultClientStripState.Placed
              && !closeBand.PressableTightened && near(closeBand.Bottom, 1429.33f),
            "a control that would leave the band shorter than the client board's own minimum does not shorten it - the interaction scan is the protection there, not a band nobody could draw in");
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
              && near(afterBlind.Bottom, 1429.33f),
            "one active-but-unmeasurable frame resets the empty set: the readiness signal is two consecutive complete scans, not a count of them");

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

        // ---- 真的没有可按控件：独立可信的就绪条件，一个额外帧-----------------------------------------------
        // A page whose controls the game switched off is a *complete* scan with nothing pressable and nothing
        // unmeasurable (the scan counts those as hidden), so its second identical complete frame is the board's frame.
        var nothing = new ResultClientWallGate();
        check(ResultClientFailureStrip.Decide(701, nothing, Evidence(float.NaN, 0), out _, out _) == ResultClientStripState.WallNotSettled
              && ResultClientFailureStrip.Decide(702, nothing, Evidence(float.NaN, 0), out ResultClientBandReading quiet, out _) == ResultClientStripState.Placed
              && near(quiet.Bottom, 1429.33f) && !quiet.PressableTightened
              && ResultClientBoard(quiet, out _),
            "inactive controls do not make the scan incomplete: two complete empty scans are the readiness signal, and the board is placed in the page's full fixed band");

        // ---- 两次空扫描之后控件才亮：一次遮挡拒绝，下一帧落定，不活锁 --------------------------------------
        // This is the honest boundary the native signals cannot close: `FailureSequenceReadiness` only finishes after
        // the failure coroutine ends, and the recorded page switched a control on while that coroutine was still
        // running. So this sequence is asserted for what production really does with it.
        var late = new ResultClientWallGate();
        ResultClientStripState lateFirst = ResultClientFailureStrip.Decide(1501, late, Evidence(float.NaN, 0), out _, out _);
        ResultClientStripState lateSecond = ResultClientFailureStrip.Decide(1502, late, Evidence(float.NaN, 0),
            out ResultClientBandReading wide, out _);
        bool wideFitted = ResultClientBoard(wide, out LayoutRect wideBoard);
        check(lateFirst == ResultClientStripState.WallNotSettled && lateSecond == ResultClientStripState.Placed && wideFitted,
            "the two empty scans place the board in the full band, exactly as the recorded page did");
        var wideStrip = new BoardStrip(wide.Top, wide.Bottom, false, float.NaN, float.NaN, true,
            ResultFooterSource.ClientNoFooterSafeArea, ResultsFontGrowth.FillCeiling(ResultFooterSource.ClientNoFooterSafeArea, false));
        float wideHeight = ResultsBandFit.RowHeight(
            ResultsBandFit.FontForBand(wide.Height, RecordedRows, PanelDensity.Compact, 0), PanelDensity.Compact) * RecordedRows;
        check(ResultClientFailureStrip.Measure(1503, late, Evidence(RecordedWall, 0), out ResultClientBandReading lateTightened, out _)
              == ResultClientStripState.Placed && near(lateTightened.Bottom, RecordedWall - RecordedClearance)
              && !ResultBoardHold.HeldGridFits(wideStrip, new BoardStrip(lateTightened.Top, lateTightened.Bottom, true, RecordedWall),
                  wideBoard, wideHeight, RecordedHeight, 2f),
            "a control switched on after those two scans really reaches the standing board, so the existing overlap refusal fires - and it fires because the control is there, not because a count expired");
        check(ResultClientFailureStrip.Decide(1504, late, Evidence(RecordedWall, 0), out ResultClientBandReading lateSettled, out _)
              == ResultClientStripState.Placed && late.Settled && near(lateSettled.Bottom, RecordedWall - RecordedClearance),
            "the very next frame is the second reading of the tightened wall, and the board is placed at that geometry - one frame without a board, not many");
        var lateStrip = new BoardStrip(lateSettled.Top, lateSettled.Bottom, true, RecordedWall, float.NaN, true,
            ResultFooterSource.ClientNoFooterSafeArea, ResultsFontGrowth.FillCeiling(ResultFooterSource.ClientNoFooterSafeArea, true));
        float lateHeight = ResultsBandFit.RowHeight(
            ResultsBandFit.FontForBand(lateSettled.Height, RecordedRows, PanelDensity.Compact, 0), PanelDensity.Compact) * RecordedRows;
        bool latePlaced = ResultBoardHold.TryPlace(RecordedWidth, RecordedHeight, lateStrip, 1160f, lateHeight, 0f,
            out LayoutRect lateRect, 2f) && lateRect.Bottom <= RecordedWall - ResultBoardHold.ButtonClearance;
        int lateRefusals = 0;
        for (int frame = 1505; frame < 1535; frame++)
        {
            if (ResultClientFailureStrip.Decide(frame, late, Evidence(RecordedWall, 0), out ResultClientBandReading band, out _)
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
        // The two facts this round exists for, pinned where they live: the scan's own verdicts travel into the decision
        // (a client page has no outer complete-scan gate to carry them), and no frame count appears in the gate at all.
        // 1.19.41 re-point: the same two facts, plus the page-action item this round adds - the client branch also hands
        // the decision whether this page's own result actions are still missing, which is what the recorded page's first
        // two frames needed (``ResultClientPageActionPolicy`` in GuestPageGeometry.cs).
        check(results.Contains("controls.Complete, controls.ParentCoverageComplete, controls.Unmeasurable, pageActionsPending)")
              && results.Contains("bool pageActionsPending = ResultClientPageActionPolicy.Pending(")
              && results.Contains("ResultClientStripState.ScanIncomplete")
              && results.Contains("HeadingReports.Add(\"failure-client-wall-not-settled\")"),
            "the client branch hands the scan's completeness, parent coverage, unmeasurable count and the page's own still-missing result actions to the decision, names an incomplete scan as its own answer, and reports the wait once per page");
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
