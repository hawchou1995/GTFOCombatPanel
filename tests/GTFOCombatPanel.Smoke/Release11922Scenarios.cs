using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// 1.19.22: F8 always ends in a drawn board, and the flashlight anchor never burns twelve identical warnings.
//
// The player log this round answers: twenty-four F8 presses on result pages, 48 `[DIAG] F8 on a result page` lines in
// strict on/off alternation, `ToggleState` never leaving true, and never a board - while `Forced` was demonstrably
// set. The press was fine; two refusals *downstream* of it deleted the board every time: the fit refused a grid
// taller than the fail-open band (a 48 px minimum band against the 135 px smallest drawable grid), and the failure
// page's `TryHold` - now bounded by the band's own top - refused every "the button moved up" and the refusal threw
// the placement away (`DeferBoardForInteraction`), which is a re-place/re-refuse loop. The per-page log latches then
// made every repeat silent, so the player saw "F8 does nothing, and nothing in the log either".
//
// The rule implemented and driven here (all through the real policies of this build, no Unity):
//
//   the page's band (strict path)   measured from the page; a board that does not fit is refused and reported -
//                                   never drawn outside it. Untouched.
//   the invented band (fail-open)   sized *from the board*: at least the smallest drawable grid plus the clearance,
//                                   extended across the highest measured wall when it has to be, reported honestly as
//                                   `band=extended, covers=...`, and drawn through the existing opacity path.
//   a standing board               never disappears because a control moved: `TryHold` refusing keeps the rectangle
//                                   and fades it instead of deferring.
//   a forced frame                 never ends silently: a refusal prints once per press even when the per-page
//                                   latch has already fired (never once per frame - a held F8 stays bounded), and a
//                                   press that produced a board prints that.
//
// The anchor half: `AnchorCameraLadder.Select` now projects a canvas-drawn UI element (RectTransform + CanvasRenderer)
// through its canvas' own worldCamera without the culling-mask proof - that proof governs world geometry and never
// applied to a canvas draw. World-space renderers keep the proof. A genuinely unmeasurable element (the recorded
// flashlight icon is one: a SpriteRenderer, so it has no CanvasRenderer) keeps the retry budget, the configured
// coordinates and the `DueToReport` one-line-per-attempt machinery, and prints at most two lines per level: the first
// failure with its reason, and the give-up.
internal static class Release11922Scenarios
{
    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        AForcedBoardFitsABandShorterThanItsSmallestGrid(check);
        AForcedBoardExistsWhenNothingIsMeasurable(check);
        AStandingFailureBoardSurvivesAButtonThatMovedUp(check);
        ASuccessBoardTallerThanItsBandDrawsFromAnExtendedBand(check);
        AForcedFrameNeverEndsWithoutALine(check);
        AUiElementResolvesThroughItsCanvasCameraWithoutTheMaskProof(check);
        AWorldSpaceRendererStillRequiresTheCullingProof(check);
        ADeadAnchorCostsTwoLinesPerLevel(check);
        Wiring(check);
    }

    // The 30-row board's own floor: 30 rows x 3 px x 1.5 em = 135 px, the figure the 1.19.18/1.19.21 suites already
    // pin. A 100 px band can never hold it - which is exactly the refusal the recorded page hit.
    private static float BoardFloor() => ResultsBandFit.MinimumDrawableHeight(30, PanelDensity.Compact);

    // (i) the recorded case: a free band of 100 px (540..640) on a page whose anchor is at 500 and waiting text at
    // 620. The page's band cannot hold the board's floor, so the invented band is extended from the board and the
    // board is drawn inside it - the F8 press ends with a board, never with the old refusal loop.
    private static void AForcedBoardFitsABandShorterThanItsSmallestGrid(Action<bool, string> check)
    {
        float floor = BoardFloor();
        check(floor > 100f + 0.5f && Near(floor, 135f, 0.5f),
            $"the smallest drawable 30-row grid is {floor:0}px, taller than the 100 px band the page left");
        bool resolved = ResultFallbackLayout.TryResolve(1440f, 500f, float.NaN, 620f, 540f, 640f, floor,
            out ResultFallbackPlan plan);
        check(resolved && plan.Placed && plan.Extended,
            "a 100 px band shorter than the smallest drawable grid does not refuse a forced board: the invented band is extended from the board");
        check(plan.Reason == ResultFallbackReason.OverlapsControls && Near(plan.Opacity, ResultFallbackLayout.OverlapOpacity, 0.001f),
            "the extended band stands over the page at the reduced opacity - the tier that already exists for a board that crosses what the page drew");
        check(plan.Height >= floor + ResultFallbackLayout.ControlClearance - 0.01f,
            $"the band reserves the board's floor plus the clearance: room={plan.Height:0}px for floor {floor:0} + {ResultFallbackLayout.ControlClearance:0}px");
        check(plan.Bottom > 620f + 0.5f,
            $"and it really reaches past the measured wall it now crosses: band bottom {plan.Bottom:0} > waiting text 620");
        bool placed = ResultFallbackLayout.TryPlaceInBand(2560f, 1440f, plan.Top, plan.Bottom, 1800f, floor, 0f, out LayoutRect rect);
        check(placed && rect.Y >= plan.Top - 0.01f && rect.Bottom <= plan.Bottom + 0.01f,
            $"the board the band was sized for sits inside it: board {rect.Y:0.#}..{rect.Bottom:0.#} vs band {plan.Top:0.#}..{plan.Bottom:0.#}");
        string described = ResultFallbackLayout.Describe(plan, 1440f, floor);
        check(described.Contains("band=extended") && described.Contains("covers=the waiting-status text") && described.Contains("need=135px"),
            $"the band reports itself honestly as extended, naming what it crosses: {described}");
    }

    // (ii) a page with no measurable geometry at all: the screen's own safe area answers, no extension is needed and
    // none is reported.
    private static void AForcedBoardExistsWhenNothingIsMeasurable(Action<bool, string> check)
    {
        float floor = BoardFloor();
        bool resolved = ResultFallbackLayout.TryResolve(1440f, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, floor,
            out ResultFallbackPlan plan);
        check(resolved && plan.Placed && plan.Reason == ResultFallbackReason.ScreenSafeArea && !plan.Extended,
            "a page that hands over no geometry at all answers from the window's own safe area, full opacity, no extension needed");
        check(Near(plan.Top, ResultFallbackLayout.SafeTop(1440f), 0.5f) && Near(plan.Bottom, 1432f, 0.5f),
            $"the band is the screen's own: {plan.Top:0.#}..{plan.Bottom:0.#} of 1440");
        bool placed = ResultFallbackLayout.TryPlaceInBand(2560f, 1440f, plan.Top, plan.Bottom, 1800f, floor, 0f, out LayoutRect rect);
        check(placed && rect.Y >= plan.Top - 0.01f && rect.Bottom <= plan.Bottom + 0.01f && rect.Bottom <= 1440f,
            "and the board sits inside that band, inside the window");
        check(!ResultFallbackLayout.Describe(plan, 1440f, floor).Contains("band=extended"),
            "a band that was not extended is not reported as extended");
    }

    // (iii) the failure page whose button reading moves up after placement. `TryHold` still refuses (its own contract
    // is untouched); the caller keeps the standing rectangle and draws it faint instead of deferring it away.
    private static void AStandingFailureBoardSurvivesAButtonThatMovedUp(Action<bool, string> check)
    {
        float floor = BoardFloor();
        bool resolved = ResultFallbackLayout.TryResolve(1440f, 200f, 700f, float.NaN, float.NaN, floor, out ResultFallbackPlan plan);
        check(resolved && plan.Reason == ResultFallbackReason.ScreenSafeArea && !plan.Extended,
            "the failure page's first placement: the safe area between the title (216) and the button's wall (694) holds the floor without an extension");
        var strip = new BoardStrip(plan.Top, plan.Bottom, false, float.NaN, float.NaN, true, ResultFooterSource.ScreenSafeArea);
        bool placed = ResultBoardHold.TryPlace(2560f, 1440f, strip, 1800f, floor, 0f, out LayoutRect standing);
        check(placed, "the board is placed inside that band");
        // The button moves up past the standing board: its top reads 300, so its clearance wall is 294, above the
        // board's foot 351. Lifting the board would cross its band's top (216), so TryHold refuses.
        bool heldOk = ResultBoardHold.TryHold(new BoardStrip(plan.Top, plan.Bottom, true, 300f), standing, 1440f, out _, plan.Top);
        check(!heldOk,
            "TryHold refuses when the moved button's clearance wall lifts past a board that cannot be lifted inside its band top - the refusal itself is unchanged");
        float opacity = ResultFallbackOpacity.Resolve(1f, standing.Bottom, plan.Top, 300f - ResultBoardHold.ButtonClearance);
        check(Near(opacity, ResultFallbackLayout.OverlapOpacity, 0.001f),
            $"and the standing board's rectangle is kept and drawn faint (opacity {opacity:0.00}) - the board never disappears because a control moved");
        string display = Source("ResultsDisplay.cs");
        string hold = Between(display, "if (!ResultBoardHold.TryHold(new BoardStrip(_holdBoard.Y, _holdBoard.Bottom, buttonMeasured, buttonTop),", "else if (_fallbackPlaced && buttonMeasured");
        check(hold.Length > 0 && !hold.Contains("DeferBoardForInteraction();") && hold.Contains("failure-hold-kept"),
            "the production Draw keeps the standing board on a TryHold refusal: no DeferBoardForInteraction in that branch, and the one bounded 'kept' line is the report");
    }

    // (iv) the success page whose board is taller than its band. The page's own band refuses (strict invariant kept);
    // the invented band is extended across the footer wall and the board is drawn inside it.
    private static void ASuccessBoardTallerThanItsBandDrawsFromAnExtendedBand(Action<bool, string> check)
    {
        float floor = BoardFloor();
        var pageBand = new BoardStrip(380f, 480f, true, 480f, 96f, true);
        bool strictPlaced = ResultBoardHold.TryPlace(2560f, 1440f, pageBand, 1800f, floor, 0f, out _);
        check(!strictPlaced,
            "the page's own 100 px band still refuses the 135 px board: the strict invariant - a board is never drawn outside the band the page measured - is untouched");
        check(!ResultFallbackTier.AcceptsPageBand(100f, floor),
            "and the fail-open tier agrees it is not a band, so the invented band answers instead");
        bool resolved = ResultFallbackLayout.TryResolve(1440f, 420f, 480f, float.NaN, float.NaN, float.NaN, floor, out ResultFallbackPlan plan);
        check(resolved && plan.Extended && plan.Height >= floor + ResultFallbackLayout.ControlClearance - 0.01f,
            $"the success page's invented band is extended from the board: room={plan.Height:0}px");
        check(plan.Bottom > 480f - ResultFallbackLayout.ControlClearance + 0.5f,
            $"and it reaches past the measured footer wall it now crosses: bottom={plan.Bottom:0}");
        bool placed = ResultFallbackLayout.TryPlaceInBand(2560f, 1440f, plan.Top, plan.Bottom, 1800f, floor, 0f, out LayoutRect rect);
        check(placed && rect.Y >= plan.Top - 0.01f && rect.Bottom <= plan.Bottom + 0.01f,
            $"the taller-than-band board is drawn inside the extended band: board {rect.Y:0.#}..{rect.Bottom:0.#} vs band {plan.Top:0.#}..{plan.Bottom:0.#}");
        string described = ResultFallbackLayout.Describe(plan, 1440f, floor);
        check(described.Contains("band=extended") && described.Contains("covers=the measured footer control") && described.Contains("need=135px"),
            $"and the band reports what it crosses: {described}");
    }

    // The emission decision a forced refusal is gated by: once per page per reason on ordinary frames, and once per
    // F8 press on forced frames. The press's own pending flag widens the gate for exactly the refusal that spends
    // it, so a held F8 prints one line per press - never one line per frame - and every press still gets its answer.
    private static void AForcedFrameNeverEndsWithoutALine(Action<bool, string> check)
    {
        var reports = new HashSet<string>();
        check(ResultReportPolicy.OncePerPage(reports, "held-back-prepare-fit-refused", false, false),
            "a page's first refusal of a reason prints");
        check(!ResultReportPolicy.OncePerPage(reports, "held-back-prepare-fit-refused", false, false),
            "the per-page latch silences the repeat on an ordinary frame");
        check(ResultReportPolicy.OncePerPage(reports, "held-back-prepare-fit-refused", true, true),
            "a forced press that still owes its answer re-arms the very same reason: even a page that already fired that latch gets its refusal line");
        check(!ResultReportPolicy.OncePerPage(reports, "held-back-prepare-fit-refused", true, false),
            "but the repaint after that press was answered is silent again - the forced exception is one line per press, never per frame (the 1.19.25 log-flood fix)");
        check(ResultReportPolicy.OncePerPage(reports, "held-back-prepare-fit-refused", true, true),
            "a new F8 press re-arms the exception, so a press is never answered with silence");
        check(ResultReportPolicy.OncePerPage(null, "other", false, false),
            "a report set that does not exist is tolerated");
    }

    // The projection rule: a UI element (CanvasRenderer on the object) under a screen-space-camera canvas resolves
    // through that canvas's own camera even when the camera's culling mask lacks the element's layer.
    private static void AUiElementResolvesThroughItsCanvasCameraWithoutTheMaskProof(Action<bool, string> check)
    {
        var uiCandidates = new List<AnchorCameraCandidate>
        {
            new AnchorCameraCandidate(AnchorCameraSource.AllCameras, true, 1 << 5, -100),
            new AnchorCameraCandidate(AnchorCameraSource.CanvasWorldCamera, true, 1 << 3, 10)   // the mask lacks the element's layer 5
        };
        AnchorCameraChoice choice = AnchorCameraLadder.Select(uiCandidates, 5, AnchorCanvasMode.ScreenSpaceCamera, canvasDrawn: true, out _);
        check(choice.Usable && choice.Projection == AnchorProjection.Camera && choice.Source == AnchorCameraSource.CanvasWorldCamera && choice.Index == 1,
            $"a UI element under a screen-space-camera canvas resolves through that canvas's own camera even though its culling mask lacks the element's layer ({AnchorCameraChoice.Describe(choice)})");
        check(choice.Reason.Contains("culling mask") && choice.Reason.Contains("canvas"),
            "and the reason says why the mask proof does not apply: the canvas projects its own elements");

        var deadCanvas = new List<AnchorCameraCandidate>
        {
            new AnchorCameraCandidate(AnchorCameraSource.CanvasWorldCamera, false, 1 << 3, 10)
        };
        AnchorCameraChoice dead = AnchorCameraLadder.Select(deadCanvas, 5, AnchorCanvasMode.ScreenSpaceCamera, canvasDrawn: true, out _);
        check(!dead.Usable && dead.Reason.Contains("worldCamera"),
            "a canvas whose own camera is not a live window camera still answers 'no projection', with the canvas camera named as the dead end");
        var textureCanvas = new List<AnchorCameraCandidate>
        {
            new AnchorCameraCandidate(AnchorCameraSource.CanvasWorldCamera, true, 1 << 3, 10, targetTexture: true)
        };
        AnchorCameraChoice texture = AnchorCameraLadder.Select(textureCanvas, 5, AnchorCanvasMode.ScreenSpaceCamera, canvasDrawn: true, out _);
        check(!texture.Usable && texture.Reason.Contains("RenderTexture"),
            "and a canvas camera that renders into a RenderTexture is refused with that reason");
    }

    // The other half of the rule: a genuine world-space renderer keeps the culling-mask proof.
    private static void AWorldSpaceRendererStillRequiresTheCullingProof(Action<bool, string> check)
    {
        var spriteCandidates = new List<AnchorCameraCandidate>
        {
            new AnchorCameraCandidate(AnchorCameraSource.AllCameras, true, 1 << 5, -100),
            new AnchorCameraCandidate(AnchorCameraSource.CanvasWorldCamera, true, 1 << 3, 10)
        };
        AnchorCameraChoice world = AnchorCameraLadder.Select(spriteCandidates, 5, AnchorCanvasMode.ScreenSpaceCamera, canvasDrawn: false, out _);
        check(world.Usable && world.Source == AnchorCameraSource.AllCameras,
            "a genuine world-space renderer is still projected by a camera whose culling mask really contains its layer");
        var blind = new List<AnchorCameraCandidate> { new AnchorCameraCandidate(AnchorCameraSource.CanvasWorldCamera, true, 1 << 3, 10) };
        AnchorCameraChoice refused = AnchorCameraLadder.Select(blind, 5, AnchorCanvasMode.ScreenSpaceCamera, canvasDrawn: false, out _);
        check(!refused.Usable && refused.Reason.Contains("culling mask"),
            "and the same element with no camera that draws its layer is still refused with the culling-mask reason - the UI exception never reaches a world sprite");
    }

    // The recorded flood: twelve identical warnings per level. The budget, the fallback and the DueToReport contract
    // stay; the identical repeats are suppressed, so the level costs two lines - the first failure and the give-up.
    private static void ADeadAnchorCostsTwoLinesPerLevel(Action<bool, string> check)
    {
        var policy = new AnchorSamplePolicy();
        int failureLines = 0, giveUps = 0;
        for (int attempt = 0; attempt < AnchorSamplePolicy.Budget; attempt++)
        {
            policy.Attempt(attempt, () => AnchorSample.Missing(AnchorSampleFailure.NoProjection), out bool gaveUp);
            if (policy.DueToReport)
            {
                if (policy.FreshWarning) failureLines++;
                policy.Reported();
            }
            if (gaveUp) giveUps++;
        }
        check(policy.Abandoned && failureLines == 1 && giveUps == 1,
            $"a genuinely unmeasurable anchor costs a level exactly two lines - the first failure with its reason, and the give-up ({failureLines} + {giveUps}) - never twelve identical warnings");
        check(policy.Attempts == AnchorSamplePolicy.Budget && policy.Failures == AnchorSamplePolicy.Budget,
            "the retry budget itself is untouched: twelve measurements are still spent");
        var changing = new AnchorSamplePolicy();
        int printed = 0;
        foreach (AnchorSampleFailure reason in new[] { AnchorSampleFailure.NoCamera, AnchorSampleFailure.NoProjection, AnchorSampleFailure.NoProjection })
        {
            changing.Attempt(0f, () => AnchorSample.Missing(reason), out _);
            if (changing.DueToReport)
            {
                if (changing.FreshWarning) printed++;
                changing.Reported();
            }
        }
        check(printed == 2,
            $"when the reason really changes the new answer gets its own line ({printed} lines for NoCamera then NoProjection twice) - only identical repeats are suppressed");
        var restarted = new AnchorSamplePolicy();
        restarted.Attempt(0f, () => AnchorSample.Missing(AnchorSampleFailure.NoCamera), out _);
        if (restarted.DueToReport) restarted.Reported();
        restarted.Restart();
        restarted.Attempt(0f, () => AnchorSample.Missing(AnchorSampleFailure.NoCamera), out _);
        check(restarted.DueToReport && restarted.FreshWarning,
            "a new level starts its own round, so its first failure prints again - the cap belongs to the level, not to the session");
    }

    // ---- wiring, read from the production source -----------------------------------------------

    private static void Wiring(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the 1.19.22 regression locates the production sources");
        if (root == null) return;
        string display = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        string fallback = File.ReadAllText(Path.Combine(root, "FallbackLayout.cs"));
        string panel = File.ReadAllText(Path.Combine(root, "PanelBehaviour.cs"));
        string ladder = File.ReadAllText(Path.Combine(root, "AnchorCameraLadder.cs"));
        string info = File.ReadAllText(Path.Combine(root, "PluginInfo.cs"));

        check(fallback.Contains("band=extended") && fallback.Contains("floor + ControlClearance")
              && fallback.Contains("ResultFallbackPlan(top, covered, OverlapOpacity, ResultFallbackReason.OverlapsControls, extended, crosses)"),
            "the invented band is sized from the board in ResultFallbackLayout.TryResolve and reports itself as extended");
        check(display.Contains("total <= strip.Bottom - strip.Top + 0.5f"),
            "the strict band invariant is untouched: a board taller than the page's own band is still refused");
        check(display.Contains("ResultReportPolicy.OncePerPage(HeadingReports") && display.Contains("ReportForcedDraw("),
            "the forced-frame exception is wired into every refusal gate and the drawn half into the fit/draw path");
        check(display.Contains("forcedByF8=true"),
            "forced frames are marked in the log");
        string hold = Between(display, "if (!ResultBoardHold.TryHold(new BoardStrip(_holdBoard.Y, _holdBoard.Bottom, buttonMeasured, buttonTop),", "else if (_fallbackPlaced && buttonMeasured");
        check(hold.Length > 0 && !hold.Contains("DeferBoardForInteraction();") && hold.Contains("need=") && hold.Contains("have="),
            "the Draw hold-refusal branch keeps the standing board (no defer) and prints need/have");
        check(panel.Contains("_anchorPolicy.FreshWarning") && panel.Contains("if (_anchorPolicy.DueToReport)"),
            "the anchor failure line keeps the DueToReport contract and adds the FreshWarning cap");
        check(ladder.Contains("canvasDrawn && mode == AnchorCanvasMode.ScreenSpaceCamera"),
            "the canvas-projection rule for screen-space-camera UI elements is in the ladder");
        // 1.19.49: one number has to cover the plugin, the manifest and the docs, so it is read and compared instead of being
        // frozen at this round's own value (the forced-frame and anchor contracts themselves are unchanged: the forced refusal
        // is still one line per press).
        const string carrierMark = "internal const string Version = \"";
        int carrierAt = info.IndexOf(carrierMark);
        string carrier = carrierAt < 0 ? string.Empty
            : info.Substring(carrierAt + carrierMark.Length,
                info.IndexOf('"', carrierAt + carrierMark.Length) - carrierAt - carrierMark.Length);
        check(carrier.Length > 0
                && File.ReadAllText(Path.Combine(root, "Thunderstore", "manifest.json")).Contains("\"version_number\": \"" + carrier + "\""),
            "the version carrier and the Thunderstore manifest carry one number: " + carrier);
        Console.WriteLine("WIRING: RELEASE_11922_FORCED_FRAME_AND_ANCHOR (static source check; not a runtime behaviour test)");
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private static string Source(string name)
    {
        string root = SourceRoot();
        if (root == null) throw new InvalidOperationException("The plugin source directory could not be found from " + AppContext.BaseDirectory);
        string path = Path.Combine(root, name);
        if (!File.Exists(path)) throw new FileNotFoundException("Production source is missing", path);
        return File.ReadAllText(path);
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj"))) directory = directory.Parent;
        return directory != null ? directory.FullName : null;
    }

    private static string Between(string text, string from, string to)
    {
        int start = text.IndexOf(from, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        int end = text.IndexOf(to, start, StringComparison.Ordinal);
        return end < 0 ? text.Substring(start) : text.Substring(start, end - start);
    }

    private static bool Near(float left, float right, float tolerance) => Math.Abs(left - right) <= tolerance;
}
