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

    // The same stand-in the growth scenarios measure with: `rows` rows of `rowEm` ems, the whole table `emWidth`
    // ems wide, both growing with the glyph size exactly as `PanelGeometry.Build` does.
    private static Func<int, ResultTableSize> Table(int rows, float rowEm, float emWidth)
        => size => new ResultTableSize(emWidth * size, rows * rowEm * size);

    private static ResultTableSize Measure(int rows, float rowEm, float emWidth, int fontSize)
        => Table(rows, rowEm, emWidth)(fontSize);

    private static bool near(float actual, float expected) => Math.Abs(actual - expected) <= 0.01f;
}
