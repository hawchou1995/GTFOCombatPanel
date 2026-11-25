using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// What this round changes: two defects the deployed 1.19.20 build shows in real profile logs, fixed on top of that
// build without touching anything it published.
//
// 1. The in-level flashlight anchor never locked. `PanelBehaviour.OnGUI` re-anchors whenever the recorded window size
//    differs from the current one, and that baseline was written in exactly one place - the branch in which the anchor
//    locked. An anchor that never locked therefore left it at 0, 0 never equals `Screen.width`, so every repaint
//    restarted the retry policy: one recorded expedition holds 9,528 lines of "attempt 1 of 12", the twelve-attempt
//    budget can never be spent, the give-up line is unreachable and the logging latch is cleared four times a second.
//    The camera read was the reason the anchor never locked: it asked one field (`GuiLayerBase.GetWorldCamera`),
//    treated null as "the icon does not exist", and never asked the question that separates the two cases - does a live
//    camera's culling mask actually contain the icon's layer. A screen-space-overlay canvas is the one case in which a
//    null camera is the *correct* answer, and only for a UI element of that canvas; the flashlight icon is a
//    `SpriteRenderer`, so an overlay canvas with no camera that draws its layer has no projection at all and the table
//    keeps PositionX/PositionY instead of inventing a rectangle from the icon column's RectTransform.
//
// 2. The settlement board changed size and place after it first appeared, and on a real success page it was finally
//    withdrawn. Sixty refused frames were read as proof that the page had settled; the upper bound was the completion
//    section's *drawn* edge, which the native `SetActiveBlink` reveal walks (250 -> 292 -> 314 -> 366 on the recorded
//    page), and the fail-open path drew a board against whatever that edge happened to be. Nothing required the strip
//    to be final before the first board, and the per-repaint safety re-check moved the board whenever its band slid a
//    dozen pixels.
//
// Every assertion below either drives the real policy classes of this build or reads the production sources and says so.
// None of it is a substitute for looking at a real results page: GAME_RUNTIME=NOT_TESTED.

internal static class AnchorAndFinalGeometryScenarios
{
    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        ABudgetIsSpentAndThenGivenUp(check);
        ALockedAnchorStillReanchorsOnAResolutionChange(check);
        OnlyACameraThatReallyDrawsTheIconMayProjectIt(check);
        AnOverlayCanvasIsProjectedWithoutACamera(check);
        AWorldSpriteUnderAnOverlayCanvasHasNoProjection(check);
        AnUnreadableLayerProvesNothing(check);
        TheFailureReasonsNameTheirDeadEnd(check);
        TheAnchorNeverInventsCoordinates(check);
        TheAnchorSamplingStateDoesNotSurviveALevel(check);

        TheFinalEdgeIncludesPartsThatAreLaidOutButNotDrawn(check);
        TheFinalEdgeNeverFollowsTheDrawnPixels(check);
        TheGateNeedsTwoAgreeingReadings(check);
        TheFailOpenPathWaitsForFinalGeometry(check);
        TheGateWaitsThroughADroppedRead(check);
        ANeverMeasurablePageStillGetsItsBoard(check);
        TheWaitIsBoundedWhenThePageGoesBlind(check);
        F8AnswersDuringABlindFrame(check);
        TheGiveUpIsBoundedAndSaysSo(check);
        AStandingBoardMovesOnlyWhenItLeavesItsBand(check);
        TheBoardAppearsOnceAtItsFinalGeometry(check);
        ThePublishedGeometryPolicyIsStillInPlace(check);
    }

    // ---- 1. the flashlight anchor ---------------------------------------------------------------------------------

    // The production gate, modelled exactly as `PanelBehaviour` now runs it: `OnGUI` re-anchors when the recorded window
    // size differs from the window, `UnlockAnchor` is what records it, and `Refresh` only samples while the anchor is
    // neither locked nor given up on. Before the fix the baseline was written only where the anchor locked, so it
    // stayed 0 and every repaint began a new round.
    private static void ABudgetIsSpentAndThenGivenUp(Action<bool, string> check)
    {
        int fixedAttempts = SimulateDeadHud(true, out int fixedFailures, out bool fixedAbandoned, out int fixedLines, out int fixedSamples);
        int oldAttempts = SimulateDeadHud(false, out int oldFailures, out bool oldAbandoned, out int oldLines, out int oldSamples);

        check(fixedFailures == AnchorSamplePolicy.Budget && fixedAbandoned,
            $"an anchor that can never be measured spends the whole {AnchorSamplePolicy.Budget}-attempt budget and gives up (failures={fixedFailures}, abandoned={fixedAbandoned})");
        check(fixedAttempts == AnchorSamplePolicy.Budget,
            $"every attempt of the round is a fresh measurement (attempts={fixedAttempts}), not the same first attempt repeated");
        check(fixedSamples == AnchorSamplePolicy.Budget,
            $"and the HUD is read exactly {AnchorSamplePolicy.Budget} times in forty seconds ({fixedSamples}), because a spent budget answers Due() with false");
        check(fixedLines == 2,
            $"1.19.22: a whole dead round costs exactly two lines - the first failure with its reason and the give-up ({fixedLines} total) - the twelve identical warnings are suppressed by FreshWarning while the per-attempt report contract stays spent");
        check(!oldAbandoned && oldFailures <= 1 && oldSamples > 1000,
            $"the old baseline is what produced the flood: every repaint restarted the round, so no round ever spent the budget (reads={oldSamples}, attempts={oldAttempts} in the round that was running at the end, failures={oldFailures}, abandoned={oldAbandoned})");

        // The schedule itself, as the failure line quotes it: 0.5 s doubling to a 4 s ceiling. Twelve *failures* are
        // waited as **eleven** intervals - the delay of the failure that spends the budget is never waited through -
        // so the round backs off 35.5 s (1.19.59: the line used to quote the nominal 39.5 s sum, which counted a delay
        // nothing ever waited for). The cadence itself is unchanged; only the figure the give-up line reports is.
        check(AnchorSamplePolicy.DelayAfter(1) == 0.5f && AnchorSamplePolicy.DelayAfter(2) == 1f && AnchorSamplePolicy.DelayAfter(3) == 2f,
            "the first three retries are half a second, one second and two seconds");
        check(AnchorSamplePolicy.DelayAfter(4) == 4f && AnchorSamplePolicy.DelayAfter(40) == 4f,
            "and the doubling stops at the four second ceiling instead of growing without bound");
        check(Math.Abs(0.5f + 1f + 2f + 9 * 4f - 39.5f) < 1e-4f,
            "the nominal sum of all twelve delays is still 39.5 s - the figure the release note quotes");
        check(Math.Abs(0.5f + 1f + 2f + 8 * 4f - 35.5f) < 1e-4f,
            "while the eleven intervals that are really waited between twelve attempts sum to 35.5 s");
        check(AnchorSamplePolicy.DelayAfter(0) == AnchorSamplePolicy.SettleInterval,
            "a measurable anchor goes back to the ordinary settle cadence");

        var spent = new AnchorSamplePolicy();
        for (int attempt = 0; attempt < AnchorSamplePolicy.Budget; attempt++)
            spent.Attempt(attempt, () => AnchorSample.Missing(AnchorSampleFailure.NoCamera), out _);
        check(spent.Abandoned && !spent.Due(12f) && spent.Due(13f),
            "1.19.26: a round that spent its budget refuses the settle-cadence poll and waits out one WatchInterval before its next look");
        spent.Attempt(13f, () => AnchorSample.Missing(AnchorSampleFailure.NoCamera), out bool watchGaveUp);
        check(!watchGaveUp && spent.Watches == 1 && spent.Abandoned,
            "and that look is a watch: it spends nothing, answers no give-up and prints nothing, so a level is abandoned exactly once");
        spent.Restart();
        check(spent.Due(0f) && spent.Attempts == 0 && spent.Failures == 0 && !spent.Abandoned,
            "and a restart - a new level, a configuration reload, a resolution change - grants a fresh round immediately");
    }

    private static int SimulateDeadHud(bool baselineWrittenByUnlock, out int failures, out bool abandoned, out int logLines, out int hudReads)
    {
        const float Frame = 1f / 60f;
        const int Frames = 60 * 40;
        const int Width = 2560, Height = 1440;
        var policy = new AnchorSamplePolicy();
        int baselineWidth = 0, baselineHeight = 0;
        bool reanchor = true;               // the panel re-anchors on its first frame
        bool locked = false, unavailable = false;
        int lines = 0, reads = 0;
        float now = 0f;
        for (int frame = 0; frame < Frames; frame++)
        {
            now += Frame;
            if (reanchor || baselineWidth != Width || baselineHeight != Height)
            {
                reanchor = false;
                policy.Restart();
                locked = false;
                unavailable = false;
                if (baselineWrittenByUnlock) { baselineWidth = Width; baselineHeight = Height; }
            }
            // `Refresh`: the sampling guard the production `Refresh` uses.
            if (locked || unavailable) continue;
            if (!policy.Due(now)) continue;
            reads++;
            policy.Attempt(now, () => AnchorSample.Missing(AnchorSampleFailure.NoProjection), out bool gaveUp);
            if (policy.DueToReport) { if (policy.FreshWarning) lines++; policy.Reported(); }   // 1.19.22: identical warnings print once per round, and every attempt's report is still spent
            if (!gaveUp) continue;
            unavailable = true;
            lines++;                        // the give-up line
            break;
        }
        failures = policy.Failures;
        abandoned = policy.Abandoned;
        logLines = lines;
        hudReads = reads;
        return policy.Attempts;
    }

    private static void ALockedAnchorStillReanchorsOnAResolutionChange(Action<bool, string> check)
    {
        var policy = new AnchorSamplePolicy();
        policy.Attempt(0f, () => AnchorSample.Of(new LayoutRect(1800f, 500f, 40f, 40f)), out bool gaveUp);
        check(!gaveUp && policy.Failures == 0 && policy.LastFailure == AnchorSampleFailure.None,
            "a measurable anchor clears the failure streak, so the next sample follows the settle cadence");
        check(!policy.Due(0.2f) && policy.Due(0.25f),
            "and it is not read again inside the settle interval");
        policy.Restart();
        check(policy.Attempts == 0 && policy.Failures == 0 && !policy.Abandoned && policy.Due(1f),
            "a resolution change grants a fresh round that is due immediately");

        // The baseline is what makes the comparison meaningful: it is written by the re-anchor itself, so the guard in
        // `OnGUI` compares two figures from the same moment and only fires for a real change.
        string panel = Source("PanelBehaviour.cs");
        check(CountOf(panel, "_anchorScreenWidth = Screen.width") >= 2,
            "the resolution baseline is written by `UnlockAnchor` as well as by the lock, which is the whole of the re-anchor loop's fix");
        check(!panel.Contains("_anchorScreenWidth = Screen.height"),
            "the width baseline is a width and not a height: the two fields are never crossed");
        check(panel.Contains("if (_reanchorRequested || _anchorScreenWidth != Screen.width || _anchorScreenHeight != Screen.height)"),
            "the comparison in `OnGUI` is unchanged, so a resolution change still re-anchors");
        // 1.19.26: the gate carries the lock only. A spent budget is the policy's own state now - it answers on the watch
        // cadence rather than closing the only door to `SampleAnchor`, which is what made a late row unreachable.
        check(panel.Contains("if (Plugin.AnchorToFlashlight.Value && !_anchorLocked) SampleAnchor(layer, Time.unscaledTime);"),
            "a locked anchor is still never sampled again, and a given-up one is left to the policy's own watch cadence instead of a second gate");
    }

    // The camera: a candidate may only project the icon when it is alive *and* its culling mask really contains the
    // icon's layer. The recorded HUD's own world camera answered null for forty minutes while the icon's renderer was
    // present the whole time; a camera that would happily project a point it never draws is not that camera.
    private static void OnlyACameraThatReallyDrawsTheIconMayProjectIt(Action<bool, string> check)
    {
        const int IconLayer = 21;
        int mask = 1 << IconLayer;
        var hudBlind = new List<AnchorCameraCandidate>
        {
            new AnchorCameraCandidate(AnchorCameraSource.HudWorldCamera, true, 1 << 5, 0),
            new AnchorCameraCandidate(AnchorCameraSource.CanvasWorldCamera, true, mask, 1)
        };
        AnchorCameraChoice choice = AnchorCameraLadder.Select(hudBlind, IconLayer, AnchorCanvasMode.ScreenSpaceCamera, false, out int inspected);
        check(choice.Usable && choice.Projection == AnchorProjection.Camera && choice.Source == AnchorCameraSource.CanvasWorldCamera && choice.Index == 1,
            $"a live camera whose culling mask misses the icon's layer does not get to project it - the next candidate that really draws it wins ({AnchorCameraChoice.Describe(choice)})");
        check(inspected == 2, "the diagnostic says how many candidates were inspected");

        var deadFirst = new List<AnchorCameraCandidate>
        {
            new AnchorCameraCandidate(AnchorCameraSource.HudWorldCamera, false, mask, 10),
            new AnchorCameraCandidate(AnchorCameraSource.PageCamera, true, mask, 0)
        };
        choice = AnchorCameraLadder.Select(deadFirst, IconLayer, AnchorCanvasMode.WorldSpace, false, out _);
        check(choice.Source == AnchorCameraSource.PageCamera,
            $"a disabled camera is not a camera: the game's GUI camera answers instead ({AnchorCameraChoice.Describe(choice)})");

        var twoOfOneSource = new List<AnchorCameraCandidate>
        {
            new AnchorCameraCandidate(AnchorCameraSource.AllCameras, true, mask, -100),
            new AnchorCameraCandidate(AnchorCameraSource.AllCameras, true, mask, 50)
        };
        check(AnchorCameraLadder.Select(twoOfOneSource, IconLayer, AnchorCanvasMode.ScreenSpaceCamera, false, out _).Index == 1,
            "within one source the camera with the greater depth wins, because it is the one composited over the other");

        var hudWins = new List<AnchorCameraCandidate>
        {
            new AnchorCameraCandidate(AnchorCameraSource.HudWorldCamera, true, mask, 0),
            new AnchorCameraCandidate(AnchorCameraSource.CanvasWorldCamera, true, mask, 9)
        };
        check(AnchorCameraLadder.Select(hudWins, IconLayer, AnchorCanvasMode.ScreenSpaceCamera, false, out _).Source == AnchorCameraSource.HudWorldCamera,
            "when the HUD's own camera really draws the icon it is still the first answer, so the ordinary case is unchanged");

        var noneSee = new List<AnchorCameraCandidate>
        {
            new AnchorCameraCandidate(AnchorCameraSource.HudWorldCamera, true, 1 << 3, 0),
            new AnchorCameraCandidate(AnchorCameraSource.MainCamera, true, 1 << 4, 0)
        };
        choice = AnchorCameraLadder.Select(noneSee, IconLayer, AnchorCanvasMode.ScreenSpaceCamera, false, out _);
        check(!choice.Usable && choice.Reason.Contains("culling mask"),
            $"no live camera draws the icon's layer: no projection exists and the reason says which test failed ({choice.Reason})");
        check(AnchorCameraLadder.Inspect(noneSee) == "candidates=2,alive=2" && AnchorCameraLadder.Inspect(null) == "candidates=0",
            "the diagnostic can say how many cameras were offered and how many were alive");
    }

    // A screen-space-overlay canvas is composited without any camera, so a UI element inside it has a legitimate
    // camera-less projection - and that is the only case in which a null camera is the right answer.
    private static void AnOverlayCanvasIsProjectedWithoutACamera(Action<bool, string> check)
    {
        AnchorCameraChoice choice = AnchorCameraLadder.Select(new List<AnchorCameraCandidate>(), 5, AnchorCanvasMode.Overlay, true, out int inspected);
        check(choice.Usable && choice.Projection == AnchorProjection.CameraLessOverlay && inspected == 0,
            $"a UI element of a screen-space overlay canvas is drawn without a camera, so a null camera there is legitimate ({AnchorCameraChoice.Describe(choice)})");
        check(AnchorCameraChoice.Describe(choice).Contains("without a camera"),
            "and the diagnostic says so instead of reporting a missing camera");

        var blind = new List<AnchorCameraCandidate> { new AnchorCameraCandidate(AnchorCameraSource.MainCamera, true, 0, 0) };
        check(AnchorCameraLadder.Select(blind, 5, AnchorCanvasMode.Overlay, true, out _).Projection == AnchorProjection.CameraLessOverlay,
            "a world camera that draws nothing cannot override the overlay canvas' own camera-less projection");
    }

    private static void AWorldSpriteUnderAnOverlayCanvasHasNoProjection(Action<bool, string> check)
    {
        AnchorCameraChoice choice = AnchorCameraLadder.Select(new List<AnchorCameraCandidate>(), 5, AnchorCanvasMode.Overlay, false, out _);
        check(!choice.Usable,
            "the flashlight icon is a SpriteRenderer: an overlay canvas draws no world sprite, so no projection exists and none is invented");
        check(choice.Reason.Contains("overlay") && choice.Reason.Contains("world geometry"),
            $"and the reason names the canvas mode rather than the icon ({choice.Reason})");
        check(AnchorCameraChoice.Describe(choice).StartsWith("no projection:"),
            "the line a player reads begins by saying there is no projection at all");

        AnchorCameraChoice cameraDrawn = AnchorCameraLadder.Select(new List<AnchorCameraCandidate>(), 5, AnchorCanvasMode.ScreenSpaceCamera, false, out _);
        check(!cameraDrawn.Usable && cameraDrawn.Reason.Contains("live camera"),
            "a camera-drawn canvas with no camera at all is reported as such, not as an overlay");
        AnchorCameraChoice worldSpace = AnchorCameraLadder.Select(new List<AnchorCameraCandidate>(), 5, AnchorCanvasMode.WorldSpace, false, out _);
        check(!worldSpace.Usable && worldSpace.Reason.Contains("live camera"),
            "and a world-space canvas is judged the same way: a camera has to be shown to draw the icon");
    }

    private static void AnUnreadableLayerProvesNothing(Action<bool, string> check)
    {
        int mask = 1 << 21;
        var hudSees = new List<AnchorCameraCandidate> { new AnchorCameraCandidate(AnchorCameraSource.HudWorldCamera, true, mask, 0) };
        AnchorCameraChoice choice = AnchorCameraLadder.Select(hudSees, -1, AnchorCanvasMode.Unknown, false, out _);
        check(!choice.Usable && choice.Reason.Contains("layer"),
            $"an icon whose layer could not be read leaves no camera provable, however well a mask happens to match ({choice.Reason})");
        check(AnchorCameraLadder.Select(hudSees, 21, AnchorCanvasMode.Unknown, false, out _).Usable,
            "an unreadable canvas mode does not forbid a camera that really draws the icon's layer - only an overlay canvas does");
        check(AnchorCameraLadder.Renders(1 << 0, 0) && !AnchorCameraLadder.Renders(1 << 0, 1),
            "layer 0 is a legal layer, so the mask test is on the layer's range and never on it being non-zero");
        check(!AnchorCameraLadder.Renders(0, 31) && !AnchorCameraLadder.Renders(1 << 2, 32) && !AnchorCameraLadder.Renders(1 << 2, -1),
            "an out-of-range layer renders nothing rather than reading bits that are not there");
    }

    private static void TheFailureReasonsNameTheirDeadEnd(Action<bool, string> check)
    {
        check(AnchorSamplePolicy.Describe(AnchorSampleFailure.NoProjection) != AnchorSamplePolicy.Describe(AnchorSampleFailure.NoCamera),
            "a camera chain that cannot draw the icon and a HUD camera that answered null are two different log lines");
        check(AnchorSamplePolicy.Describe(AnchorSampleFailure.NoProjection).Contains("culling mask"),
            "the new reason says what was tested, so the next log answers the question the old one could not");
        check(AnchorSamplePolicy.Describe(AnchorSampleFailure.NoIcon) != AnchorSamplePolicy.Describe(AnchorSampleFailure.Unmeasurable),
            "a missing renderer and an unprojectable rectangle stay distinguishable too");

        var policy = new AnchorSamplePolicy();
        policy.Attempt(0f, () => AnchorSample.Missing(AnchorSampleFailure.NoProjection), out _);
        check(policy.LastFailure == AnchorSampleFailure.NoProjection,
            "the policy records the new reason, so the failure line prints it");

        // A read that throws is one more failed measurement, not a free one - and its message survives to the line.
        var throwing = new AnchorSamplePolicy();
        throwing.Attempt(0f, () => throw new InvalidOperationException("the HUD exploded"), out _);
        check(throwing.Failures == 1 && throwing.Attempts == 1,
            "a read that throws spends one measurement of the budget instead of leaving it untouched");
        AnchorSample thrown = throwing.Attempt(0f, () => throw new InvalidOperationException("again"), out _);
        check(thrown.Error != null && thrown.Error.Contains("again") && !thrown.Measured,
            "and the exception's own message rides along, so the line can name the read that threw");
    }

    private static void TheAnchorNeverInventsCoordinates(Action<bool, string> check)
    {
        string panel = Source("PanelBehaviour.cs");
        check(panel.Contains("AnchorCameraLadder.Select(") && panel.Contains("m_layerCanvas") && panel.Contains("CanvasRenderMode"),
            "the anchor resolves its camera through the ladder, from the icon canvas' own render mode");
        check(panel.Contains("CM_PageBase.Camera") && panel.Contains("camera.cullingMask") && panel.Contains("camera.isActiveAndEnabled"),
            "the game's GUI camera is a candidate and every candidate is checked for being alive and for its culling mask");
        // 1.19.28 re-point: the NoProjection answer carries the geometry note the diagnostic line prints, so the exact call has one
        // more argument. The claim is unchanged - a refused ladder is reported as a failure, never as a guessed rectangle.
        check(panel.Contains("return new AnchorElementMeasurement(true, layer, canvasDrawn, choice, false, default, AnchorSampleFailure.NoProjection,"),
            "when no projection can be shown to exist the anchor reports it, so the table keeps PositionX/PositionY");
        check(panel.Contains("var status = display != null ? display.FlashLightIcon : null;"),
            "the element's renderer is still read (its layer and its canvas fact come from it), only now through the display it belongs to");
        check(panel.Contains("iconRect = display.m_rectTrans;")
              && panel.Contains("if (iconRect == null) iconRect = display.transform.TryCast<RectTransform>();"),
            "1.19.28: the rectangle that is *measured* is the icon row's own `m_iconDisplay.m_rectTrans` layout rectangle - the display's own transform is only the fallback for a build whose interop answers the field as null");
        check(panel.Contains("Vector3 world = element.TransformPoint(new Vector3(")
              && panel.Contains("(corner & 1) == 0 ? local.xMin : local.xMax,"),
            "and that rectangle's own four corners go through the element's transform, which already carries its pivot and every parent scale - no guessed pixels-per-unit");
        check(!panel.Contains("m_iconDisplay.RectTrans") && !panel.Contains("iconDisplay.RectTrans"),
            "and never a field the interop does not have: `m_rectTrans` is the display's own public RectTransform");
        check(panel.Contains("RectTransformUtility.WorldToScreenPoint(null, world)"),
            "an overlay canvas' own UI element is projected with Unity's camera-less call rather than with a guessed camera");
        check(!panel.Contains("catch { camera = null; }"),
            "the read no longer folds an exception into a null camera, which is what made the thrown-read suffix unreachable");
        check(panel.Contains("the read itself threw") && panel.Contains("AnchorSamplePolicy.Describe(failure)"),
            "and the failure line still carries both the reason and the exception's own message");

        // The zero-parallax contract: the origin is still computed from the anchor or from the configured fractions, and
        // nothing about a camera may enter it. A table that moved with the HUD would be the whole point of the mod lost.
        string origin = Between(panel, "private bool TryResolveOrigin(PanelGrid grid, out LayoutRect origin)", "private static int FontSize()");
        check(origin.Length > 0 && !origin.Contains("Camera") && origin.Contains("Plugin.PositionX.Value") && origin.Contains("Plugin.PositionY.Value"),
            "the origin is resolved from the icon's own edge or from the configured PositionX/PositionY - never from a camera projection");
        check(origin.Contains("return false;   // still settling: draw nothing rather than jump"),
            "and the settle window still draws nothing rather than jumping to the configured position");
        check(panel.Contains("Plugin.Logger.LogInfo($\"[DIAG] table "),
            "the draw line that names the anchor is still the one `Verify.ps1` reads");
    }

    // ---- 2. the settlement board's final geometry -----------------------------------------------------------------

    private static void TheFinalEdgeIncludesPartsThatAreLaidOutButNotDrawn(Action<bool, string> check)
    {
        // The recorded section: the icon row draws, the sector box's own part is active and laid out but draws nothing
        // yet, one part the page switched off entirely is not part of the page, and one is missing from this build.
        var drawnRow = new CompletionPart(true, true, 1f, 300f, 260f);
        var laidOutOnly = new CompletionPart(true, true, 0f, 520f, float.NaN);
        var switchedOff = new CompletionPart(true, false, 1f, 9999f, 9999f);
        var absent = CompletionPart.Missing;
        var parts = new List<CompletionPart> { drawnRow, laidOutOnly, switchedOff, absent };

        float drawn = CompletionBounds.Deepest(parts, out int drawnDeepest);
        float final = CompletionFinalBounds.Bottom(parts, out int finalDeepest, out int laidOut);
        check(Near(final, 520f) && finalDeepest == 1 && laidOut == 1,
            $"a part that is on the page and laid out counts from its own laid-out slot whether or not its pixels have arrived: final={final:0}, deepest={finalDeepest}, laidOut={laidOut}");
        check(Near(drawn, 260f) && drawnDeepest == 0,
            $"while the drawn-only rule keeps the pixels a part really occupies: drawn={drawn:0}");
        check(Near(CompletionFinalBounds.Bound(parts, drawn, out _, out _), 520f),
            "the strip is built from the deeper of the two, so the first board already sits under the section's final edge");
        check(!float.IsFinite(CompletionFinalBounds.PartBottom(switchedOff, out _)),
            "a part the page switched off and never scheduled (its BlinkIn never ran) contributes nothing, so a hidden prefab's old geometry cannot push the board down");
        check(!float.IsFinite(CompletionFinalBounds.PartBottom(absent, out _)),
            "and a part this build does not have contributes nothing either");
        check(!float.IsFinite(CompletionFinalBounds.Bottom(null, out _, out _)) && !float.IsFinite(CompletionFinalBounds.Bound(null, float.NaN, out _, out _)),
            "an empty section answers nothing rather than zero, so nothing is placed against it");
        check(Near(CompletionFinalBounds.Bound(null, 260f, out _, out _), 260f),
            "and a section nobody could read never raises an edge that was measured another way");
    }

    private static void TheFinalEdgeNeverFollowsTheDrawnPixels(Action<bool, string> check)
    {
        // A part that already draws: its own laid-out slot can sit tens of pixels below its last drawn pixel, and
        // switching the band from the slot to the drawn pixel when the part starts drawing is exactly the upward
        // jump that re-measured a correctly placed board. The band now hangs from the slot always; the drawn
        // figure stays a diagnostic only.
        var padded = new CompletionPart(true, true, 1f, 610f, 400f);
        check(Near(CompletionFinalBounds.PartBottom(padded, out bool laidOutOnly), 610f) && !laidOutOnly,
            $"the final edge of a drawing part is its laid-out slot ({CompletionFinalBounds.PartBottom(padded, out _):0}) - the 400 px of drawn pixels never shrink it, so the band cannot rise when the reveal draws something");
        var faded = new CompletionPart(true, true, 0f, 610f, float.NaN);
        check(Near(CompletionFinalBounds.PartBottom(faded, out bool fromLayout), 610f) && fromLayout,
            "a part still at zero alpha but laid out answers from the same slot, and it is reported as not yet drawn");
        check(Near(CompletionBounds.PartBottom(padded), 400f),
            "while the drawn-only rule keeps the pixels a part really occupies - exactly the diagnostic the band no longer reads: 400");
        check(!float.IsFinite(CompletionBounds.PartBottom(faded)),
            "and the drawn rule still refuses to count a part the player cannot see");

    }
    // The stability latch the drawn-pixel route still keeps. 1.19.21 re-point: the *placement* no longer waits for
    // this latch - readable final layout geometry answers the strict path on its first frame - and these assertions
    // pin the latch's own contract (two agreeing samples, a moving page reopens it, an unreadable frame proves
    // nothing), which the fail-open policy and the [DIAG] lines keep relying on.
    private static void TheGateNeedsTwoAgreeingReadings(Action<bool, string> check)
    {
        var gate = new ResultFinalGate();
        check(!gate.Observe(250f, 1244f) && !gate.Ready && gate.Samples == 1,
            "one reading of a page that is still revealing itself is not a settled *reading*");
        check(gate.Observe(250f, 1244f) && gate.Ready,
            $"the same strip read twice is settled (samples={gate.Samples})");
        check(gate.Observe(250.5f, 1244.5f) && gate.Ready,
            "a reading inside the tolerance is the same edge, so a hover that moves a button by a fraction of a pixel cannot restart the wait");
        check(!gate.Observe(314f, 1274f) && gate.Samples == 1 && !gate.Ready,
            "a section that moved by tens of pixels is not settled any more, and the count starts again from that reading");
        check(!gate.Observe(float.NaN, float.NaN) && gate.Samples == 0,
            "a frame the page could not be measured does not count as agreement - a page must not become 'stable' by failing to be read");
        check(!gate.Observe(500f, 400f) && gate.Samples == 0,
            "and an inverted strip is not a strip: the gate closes rather than accepting a negative height");
        gate.Observe(272f, 1244f);
        gate.Forget();
        check(!gate.Ready && gate.Samples == 0 && !float.IsFinite(gate.Top),
            "a different page forgets the latch entirely");
    }

    // `FailOpenAllowed(everMeasurable, measurableThisFrame, settled, forced, waitedFrames)`. 1.19.21 re-point note:
    // the *strict* placement no longer appears in this table at all (it places from readable final layout on the
    // first frame); what these assertions pin is the fail-open path's own remaining rule.
    private static void TheFailOpenPathWaitsForFinalGeometry(Action<bool, string> check)
    {
        check(ResultFinalPolicy.FailOpenAllowed(false, false, false, false, 0),
            "a page that has never handed over its final geometry keeps the fail-open band it always had - there is nothing to wait for");
        check(!ResultFinalPolicy.FailOpenAllowed(true, true, false, false, 0),
            "1.19.21 re-point: with the strict path now placing from readable final layout on its first frame, this refusal is the *fail-open* path's own remaining rule - a page the strict path rejected despite measurable geometry is still not answered with the screen's own band at a moving edge");
        check(!ResultFinalPolicy.FailOpenAllowed(true, true, false, false, ResultFinalPolicy.MaximumWaitFrames - 1),
            "and that fail-open wait is not a single frame of patience either");
        check(ResultFinalPolicy.FailOpenAllowed(true, true, true, false, 0),
            "a page whose geometry has settled is judged by the ordinary path again");
        check(ResultFinalPolicy.FailOpenAllowed(true, true, false, true, 0) && ResultFinalPolicy.FailOpenAllowed(false, false, false, true, 0),
            "F8 never waits for anything - the key is the player's own answer and outranks the latch");
        check(ResultFinalPolicy.WhyWaiting(true, true, false, 250f, 1244f, 270f, 7).Contains("has not read the same twice"),
            "the waiting line says a moving edge is why nothing is drawn, so the delay is explainable from the log");
        check(ResultFinalPolicy.WhyWaiting(true, true, false, 250f, 1244f, 270f, 7).Contains("waited=7"),
            "the waiting line carries how long the wait has run, so a long wait is visible before it is given up on");
        check(ResultFinalPolicy.WhyWaiting(false, false, false, float.NaN, float.NaN, 270f, 0).Contains("never handed over"),
            "and a page that never measured anything says that instead");
    }

    // The independent review's first finding, as a sequence: the page measures its strip, drops one reading, comes back and
    // settles. A single blind frame must not be read as "this page has no geometry" - that is the frame on which the old
    // rule answered from the screen band, drew a board at a size the page never asked for, and re-fitted it a moment later.
    // `everMeasurable` is what makes the difference, and these are the four states the fail-open path passes through.
    // 1.19.21 re-point: the strict placement never waits through these states any more (readable final layout places on
    // the first frame); the dropped-read latch remains the fail-open path's own guard against a false "no geometry".
    private static void TheGateWaitsThroughADroppedRead(Action<bool, string> check)
    {
        check(!ResultFinalPolicy.FailOpenAllowed(true, true, false, false, 0),
            "a page that has measured its strip but not settled it is not answered from the screen band");
        check(!ResultFinalPolicy.FailOpenAllowed(true, false, false, false, 1),
            "a dropped read on a page that already measured is not treated as a page with no geometry");
        check(!ResultFinalPolicy.FailOpenAllowed(true, true, false, false, 2),
            "and the frame it comes back on is waited for as well");
        check(ResultFinalPolicy.FailOpenAllowed(true, true, true, false, 3),
            "the first settled reading is the one the board may be placed on");
        check(ResultFinalPolicy.WhyWaiting(true, false, false, 250f, 1244f, 270f, 1).Contains("could not this frame"),
            "the waiting line names a dropped read instead of claiming the page never had geometry");
        check(!ResultFinalPolicy.WhyWaiting(true, false, false, 250f, 1244f, 270f, 1).Contains("settled"),
            "and it never reports that frame as settled");
    }

    // The other side of the latch: a client page with no footer to read, or a failure page whose own strips cannot be
    // resolved, has never handed over final geometry. It keeps the immediate fail-open answer it has always had - a board
    // is never withheld from a page that has nothing to wait for.
    private static void ANeverMeasurablePageStillGetsItsBoard(Action<bool, string> check)
    {
        check(ResultFinalPolicy.FailOpenAllowed(false, false, false, false, 0)
              && ResultFinalPolicy.FailOpenAllowed(false, false, false, false, 1)
              && ResultFinalPolicy.FailOpenAllowed(false, false, false, false, 240),
            "a page that has never measured its own geometry is answered by the screen band at once, and stays answered");
        check(!ResultFinalPolicy.GivenUp(false, false, false, 10000),
            "and it is never reported as having been given up on: it was never waiting");
        check(ResultFinalPolicy.WhyWaiting(false, false, false, float.NaN, float.NaN, 270f, 0).Contains("never handed over"),
            "the log line for it says the page never handed geometry over, which is the truth for those pages");
    }

    // A page that measured its strip and then went blind for good: it waits for the same bounded number of frames as one
    // that keeps moving, and is then answered - with a line that says the wait was abandoned, never that it settled.
    private static void TheWaitIsBoundedWhenThePageGoesBlind(Action<bool, string> check)
    {
        int last = ResultFinalPolicy.MaximumWaitFrames;
        check(!ResultFinalPolicy.FailOpenAllowed(true, false, false, false, last - 1),
            "a page that stopped answering is waited for up to the bound, not answered on its first blind frame");
        check(ResultFinalPolicy.FailOpenAllowed(true, false, false, false, last)
              && ResultFinalPolicy.GivenUp(true, false, false, last),
            "at the bound it is answered, and the answer is recorded as the give-up");
        check(ResultFinalPolicy.WhyGivenUp(float.NaN, float.NaN, last, false).Contains("stopped answering")
              && ResultFinalPolicy.WhyGivenUp(float.NaN, float.NaN, last, false).Contains("not a settled page"),
            "the give-up line says the page stopped answering, and that this is not a settled page");
        check(!ResultFinalPolicy.GivenUp(true, true, false, 10000)
              && ResultFinalPolicy.FailOpenAllowed(true, true, true, false, 0),
            "a page that settled is answered because it settled, never through the bound");
    }

    private static void F8AnswersDuringABlindFrame(Action<bool, string> check)
    {
        check(ResultFinalPolicy.FailOpenAllowed(true, false, false, true, 0),
            "F8 answers on a blind frame exactly as it does on any other: the key outranks the latch");
        check(!ResultFinalPolicy.GivenUp(true, false, true, 10000),
            "and an F8 answer is never recorded as the give-up");
    }

    private static void TheGiveUpIsBoundedAndSaysSo(Action<bool, string> check)
    {
        check(ResultFinalPolicy.GivenUp(true, false, false, ResultFinalPolicy.MaximumWaitFrames)
              && ResultFinalPolicy.FailOpenAllowed(true, true, false, false, ResultFinalPolicy.MaximumWaitFrames),
            "a page that never stops moving is answered after the bounded wait rather than leaving the screen empty");
        check(!ResultFinalPolicy.GivenUp(true, false, false, 0) && !ResultFinalPolicy.GivenUp(true, true, false, 10000),
            "the give-up only ever applies to a page that measured its strip and never settled it");
        check(!ResultFinalPolicy.GivenUp(true, false, true, 10000),
            "and never to an F8 override, which is not a wait at all");
        check(ResultFinalPolicy.WhyGivenUp(314f, 1274f, ResultFinalPolicy.MaximumWaitFrames).Contains("not a settled page"),
            "the give-up line says in words that the page was not settled, so a give-up can never be read as stability");
        check(ResultFinalPolicy.MaximumWaitFrames >= 60 && ResultFinalPolicy.MaximumWaitFrames <= 600,
            "the bounded wait is seconds of patience, longer than the reveal it waits out and shorter than a player's");
    }

    private static void AStandingBoardMovesOnlyWhenItLeavesItsBand(Action<bool, string> check)
    {
        // The recorded repaint: the band slid from 272..1244 to 284..1252 while the board stood at 272 with a bottom of
        // 1184. The old rule moved the board for the twelve pixels; the round before this one forbade that. Both answers were
        // wrong for the same reason - the question was asked about the band's foot alone. The band's *top* is the page's own
        // anchor, so 284 over 272 with the board's foot still inside 1252 means the anchor moved down into the board. That is
        // exactly the case the board has to follow, and it follows it to the band's own top (284), never past it.
        check(ResultLateControlPolicy.MustMove(272f, 1184f, 284f, 1252f),
            "a band whose top slid twelve pixels down into a standing board moves it - its foot never moved, which is why the old rule could not see it");
        check(!ResultLateControlPolicy.MustMove(284f, 1184f, 284f, 1252f),
            "once the board stands at that band's top the same band does not move it again");
        check(!ResultLateControlPolicy.MustMove(284f, 1252f, 284f, 1252f),
            "a board whose foot is exactly the band's foot still fits");
        check(!ResultLateControlPolicy.MustMove(400f, 900f, 100f, 1244f),
            "a board inside both edges of its band is left exactly where it is");
        check(ResultLateControlPolicy.MustMove(400f, 1355f, 100f, 1277f),
            "a board that really reaches past its band's foot has to move - that is the case in which it would cover what the band ends at");
        check(!ResultLateControlPolicy.MustMove(272.2f, 1184f, 272.6f, 1252f),
            "a band that jitters by less than the half-pixel tolerance does not move a table the player is reading");
        check(ResultLateControlPolicy.MustMove(272f, 1184f, 272.6f, 1252f),
            "and one that really passes that tolerance does");
        check(!ResultLateControlPolicy.MustMove(float.NaN, 1184f, 100f, 1252f)
              && !ResultLateControlPolicy.MustMove(272f, float.NaN, 100f, 1252f),
            "an unmeasurable board edge never moves anything: nothing is known, so nothing is changed");
        check(!ResultLateControlPolicy.MustMove(272f, 1184f, float.NaN, float.NaN),
            "a band with no measurable edges is not a band, so it cannot move the board either");
        check(!ResultLateControlPolicy.MustMove(1000f, 1100f, float.NaN, 0f),
            "a band that does not exist is not a band to leave");
    }

    // The recorded success page: the completion section's *drawn* edge walks as the native reveal switches its parts on,
    // while the layout it ends at is fixed from the start. The board must therefore appear once, at the final geometry,
    // from the first frame the strip reads the same twice - instead of being re-fitted at every drawn edge the reveal
    // produced, which is the sequence the log recorded.
    private static void TheBoardAppearsOnceAtItsFinalGeometry(Action<bool, string> check)
    {
        float[] drawnEdge = { 250f, 250f, 250f, 272f, 292f, 314f, 314f, 366f, 366f, 366f, 366f, 366f, 366f, 366f, 366f };
        const float finalEdge = 366f;     // what the section's layout says from the first frame
        const float gap = 21.3f;
        const float bottom = 1244f;

        var gate = new ResultFinalGate();
        int draws = 0, oldDraws = 0, withdrawals = 0;
        float firstTop = float.NaN, lastTop = float.NaN, oldTop = float.NaN;
        for (int frame = 0; frame < drawnEdge.Length; frame++)
        {
            // The production strip: `SuccessRegion` builds it from the *final* edge, so the reveal cannot move it.
            float stripTop = finalEdge + gap;
            bool settled = gate.Observe(stripTop, bottom);
            if (ResultFinalPolicy.FailOpenAllowed(true, true, settled, false, 0) && settled)
            {
                if (draws == 0) { draws++; firstTop = stripTop; }
                if (float.IsFinite(lastTop) && !Near(stripTop, lastTop)) withdrawals++;   // a page that moved after the board stood
                lastTop = stripTop;
            }
            // The rule this replaces: the strip was the drawn edge, so the board followed the reveal.
            float oldStripTop = drawnEdge[frame] + gap;
            if (oldDraws == 0 || !Near(oldStripTop, oldTop)) oldDraws++;
            oldTop = oldStripTop;
        }

        check(draws == 1,
            $"the board is shown exactly once over the whole reveal ({draws} draw(s)), instead of being re-fitted as the drawn edge walks");
        check(Near(firstTop, finalEdge + gap) && Near(lastTop, firstTop),
            $"and the one geometry it is shown at is the page's final one (top={firstTop:0}), not the mid-reveal {drawnEdge[0] + gap:0} the log recorded");
        check(withdrawals == 0,
            "a board fitted to the final geometry has nothing to be re-measured away from, so it is never taken away afterwards");
        check(oldDraws == 5,
            $"the rule this replaces laid the same board out five times over one reveal - at the drawn edge 250, 272, 292, 314 and 366 ({oldDraws} layouts for {drawnEdge.Length} frames), which is the sequence the log recorded");
        check(gate.Ready && gate.Samples > 2,
            "a page that stopped moving keeps the gate open, so the per-repaint re-checks have something to compare against");

        // And the pathological page: layout that keeps moving. It must be waited out, then answered by the give-up, and
        // the give-up must be the thing the log says - never a claim that the page settled.
        var restless = new ResultFinalGate();
        int restlessDraws = 0;
        int frames = 0;
        for (; frames < ResultFinalPolicy.MaximumWaitFrames + 10; frames++)
        {
            float moving = 300f + frames * 2f;                  // two pixels per frame, past the one-pixel tolerance, for ever
            bool settled = restless.Observe(moving, bottom);
            if (ResultFinalPolicy.FailOpenAllowed(true, true, settled, false, frames)) restlessDraws++;
        }
        check(restlessDraws == 10,
            $"a page whose own layout never stops moving is answered only after the bounded wait ({restlessDraws} frames of drawing out of {frames})");
        check(!restless.Ready && ResultFinalPolicy.GivenUp(true, restless.Ready, false, frames - 1),
            "and that answer is the give-up, not a settled strip: the latch never opened");
    }
    // Everything 1.19.20 published stays exactly where it was - the row/column policy, the font growth, the kill-tier
    // Everything 1.19.20 published stays exactly where it was - the row/column policy, the font growth, the kill-tier
    // split, the roles, the interaction proof, F8 and the vanilla page - and the two new policies are wired into the
    // production paths rather than merely implemented.
    private static void ThePublishedGeometryPolicyIsStillInPlace(Action<bool, string> check)
    {
        string display = Source("ResultsDisplay.cs");
        string board = Source("ImpactBoard.cs");
        string layout = Source("LayoutPolicy.cs");
        string panel = Source("PanelBehaviour.cs");
        string ladder = Source("AnchorCameraLadder.cs");
        string final = Source("ResultFinalLayout.cs");
        string info = Source("PluginInfo.cs");

        check(ladder.Contains("public static AnchorCameraChoice Select(") && final.Contains("public sealed class ResultFinalGate"),
            "the two policies this round adds are their own files, so both can be asserted without Unity");
        // 1.19.49: one number has to cover the plugin, the manifest and the docs (the settlement board is sized by what the page
        // really draws, with the anchor contract asserted above unchanged), so the carrier is read and compared with the
        // manifest rather than frozen at this round's own value.
        const string carrierMark = "internal const string Version = \"";
        int carrierAt = info.IndexOf(carrierMark);
        string carrier = carrierAt < 0 ? string.Empty
            : info.Substring(carrierAt + carrierMark.Length,
                info.IndexOf('"', carrierAt + carrierMark.Length) - carrierAt - carrierMark.Length);
        check(carrier.Length > 0 && Source(Path.Combine("Thunderstore", "manifest.json")).Contains("\"version_number\": \"" + carrier + "\""),
            "the version carrier and the Thunderstore manifest carry one number: " + carrier);

        // 1.19.20's own increment, untouched: the split kill tiers and the drawable floor.
        check(board.Contains("public static bool SplitKillTiersFor(float room, float emPerGlyph)") && board.Contains("private static int GlyphFor("),
            "the published kill-tier rule is still in ImpactBoard - the half-finished work this round replaces had deleted it");
        check(layout.Contains("public static float DrawableBoardHeight(int rows, float rowEm, PanelDensity density, float room)"),
            "and ResultsFontGrowth still answers the drawable floor for the room the page measured");
        check(display.Contains("private static float _readableFloor = float.NaN;")
              && display.Contains("_readableFloor = ResultsFontGrowth.MinimumBoardHeight(rows, rowEm, Plugin.Density);"),
            "both floors are still set for every page visit, so a page is judged by the room it measured");
        check(display.Contains("bool splitTiers = fixedLayout || ImpactBoard.SplitKillTiersFor(strict ? room : float.NaN,")
              && display.Contains("ImpactBoard.Build(report, splitTiers, succeeded)")
              && CountOf(display, "ImpactBoard.Build(report, true, succeeded)") == 2,
            "the board is still built with the row count the page's own room produced - the two hard-coded split builds are RefreshBoardData's, exactly as 1.19.20 shipped it, and 1.19.23's hidden-screen ArchiveScreen, which mirrors that same split rule so a hidden settlement page archives the same board shape");
        check(display.Contains("ResultsFontGrowth.DrawableBoardHeight(rows, rowEm, Plugin.Density, room)")
              && display.Contains("stripFloor = area.Fits")
              && display.Contains("ResultsFontGrowth.DrawableBoardHeight(stripRows, stripRowEm, Plugin.Density, anchoredHeight)"),
            "and both places that judge a strip - Prepare and SuccessRegion - still use the room figure");
        check(Math.Abs(ResultsFontGrowth.MinimumBoardHeight(30, PanelGeometry.MinimumRowEm, PanelDensity.Compact) - 270f) < 0.5f,
            "the readable floor of the split board is still the 270 px the recorded log printed");
        check(Math.Abs(ResultsFontGrowth.MinimumBoardHeight(ImpactBoard.RowCount(true), PanelGeometry.MinimumRowEm, PanelDensity.Compact) - 270f) < 0.5f,
            "and the board's own row count is still thirty rows, so the floor does not move with the squad size");
        check(ResultsFontGrowth.DrawableBoardHeight(30, PanelGeometry.MinimumRowEm, PanelDensity.Compact, 269f)
              <= ResultsFontGrowth.DrawableBoardHeight(30, PanelGeometry.MinimumRowEm, PanelDensity.Compact, 286f),
            "a 269 px strip is still not judged harder than a 286 px one - the cliff 1.19.20 removed stays removed");

        // The final geometry: the reveal is asked first, then the section is measured, then that strip feeds the latch.
        // 1.19.21 measured first and asked afterwards; 1.19.41 inverts that on purpose, because the edge the board hangs
        // from now depends on whether the reveal has finished (`CompletionFinalBounds.Placed`) and the first placement
        // waits for it (`ResultRevealGate`) - measuring before asking would make the section and the gate describe two
        // different frames.
        int revealAsked = display.IndexOf("bool revealDone = CompletionReady(page);", StringComparison.Ordinal);
        int sectionMeasured = display.IndexOf("bool completionMeasured = SuccessRegion(page, out ResultsSafeArea area", StringComparison.Ordinal);
        check(revealAsked >= 0 && sectionMeasured > revealAsked
            && display.Contains("ResultRevealGate.BlocksPlacement(revealDone, CompletionTargets.Ledger.MayStillComplete,")
            && display.Contains("CompletionTargets.Ledger.InFlight, Forced)")
            && !display.Contains("bool revealReady = CompletionReady(page);")
            && !display.Contains("ReportHeldBack(\"success-anchor\", \"completion-not-ready\""),
            "1.19.41: the success placement asks the native reveal before it measures the section, so the frame the ledger completes is the frame the section is measured as final; 1.19.43 adds the in-flight count (F1), so one aborted target cannot release the board while the rest of the reveal still runs; the gate is a policy call with an explicit escape, not a frame count and not an alpha test");
        check(display.Contains("NoteFinalGeometry(completionMeasured && area.Fits);")
              && display.Contains("if (area.Fits) FinalLayout.Observe(area.Top, area.Bottom);"),
            "that strip is what the final-geometry latch is fed, once per measurement");
        check(display.Contains("private static void NoteFinalGeometry(bool measurable)")
              && display.Contains("if (measurable) _finalEverMeasurable = true;")
              && display.Contains("if (!_finalEverMeasurable || FinalLayout.Ready)"),
            "the latch remembers that this page measured once, and the wait clock follows the latch rather than one frame");
        check(display.Contains("CompletionFinalBounds.Placed(parts, RevealFinished, contentSettled, out int finalDeepest, out _finalUndrawn)")
              && !display.Contains("CompletionFinalBounds.Bound(finalParts"),
            "1.19.41: the success page's strip is the parts' own layout slots while the native reveal runs, and once it has finished (or the ledger has proved no reveal is coming, F3) the pixels each part really draws (`CompletionFinalBounds.Placed` -> `FinalEdge`) - the container's own edge alone was what let the panel's top press on the sector icons and their captions");
        check(display.Contains(", final edge") && display.Contains("laidOutParts="),
            "and the region line names the part it measured and how many parts are still only laid out");
        // 1.19.42: the fail-open decision is one policy call (`ResultSettlementGate`), which asks the page's own reveal
        // before it asks `ResultFinalPolicy` - the two questions the recorded page answered separately.
        int gate = display.IndexOf("ResultSettlementGate.Decision settlement = ResultSettlementGate.Decide(Forced, succeeded, RevealFinished,", StringComparison.Ordinal);
        int fallback = display.IndexOf("if (!FallbackPlacement(page, succeeded, out _, out room, out strip, out string fallbackWhy))", StringComparison.Ordinal);
        check(gate >= 0 && fallback > gate
              && display.Contains("ResultSettlementGate.Why(settlement.Why, CompletionTargets.Ledger.Completed, CompletionTargets.Ledger.Count,")
              && display.Contains("CompletionTargets.Ledger.InFlight, stallSeconds, stallLimit)"),
            "the fail-open path asks the settlement gate - the reveal's ledger (with its in-flight count, F1, and its stall clock, F1-STALL) and the final-geometry latch in one call - before it answers, and both a withheld frame and an unobservable one are explained in the log rather than silently drawn");
        check(display.Contains("ResultFinalPolicy.WhyWaiting(") && display.Contains("\"final-geometry-pending\"")
              && display.Contains("settlement.Why == ResultSettlementGate.Answer.GivenUp") && display.Contains("ResultFinalPolicy.WhyGivenUp("),
            "a waiting frame and a given-up frame are both explained in the log, neither pretends to be the other, and the given-up line is now chosen by the settlement gate's own answer rather than by a second, independent call to the policy");
        check(display.Contains("NoteFinalGeometry(room > 0f && float.IsFinite(top) && float.IsFinite(failureBottom));"),
            "the failure page's own strip feeds the same latch, so its band is read twice too");
        check(display.Contains("if (succeeded && !CompletionReady(page))"),
            "the drift re-measure stands down while the success page's own reveal is still running");
        check(display.Contains("ResultLateControlPolicy.MustMove(_origin.Y, _origin.Bottom, strip.Top, strip.Bottom)")
              && display.Contains("bool moved = refitted || sizeChanged || horizontal || (intrudes && vertical);"),
            "a standing fail-open board asks about both edges of its band, and about the rectangle that is on the screen rather than the one it was just moved to - and a re-fit (review #4's smaller legal grid) is a move in its own right");

        // Everything the board already promised: the native reveal, the interaction proof, the two roles, the wheel,
        // F8, and the vanilla page being restored rather than overlaid.
        check(display.Contains("BoardStillSafe()") && display.Contains("DeferBoardForInteraction();")
              && display.Contains("ResultInteractionStripPolicy.OverlapsBoard("),
            "the per-repaint collision and readiness checks are untouched");
        check(display.Contains("GuestClient()") && display.Contains("ResultNoFooterGate")
              && display.Contains("UsesBandBelowActions(") && display.Contains("m_lobbyButtonVisible"),
            "the host/client branches, the no-footer proof and the band below the page's actions all still exist");
        check(display.Contains("ToggleForcedFallback") && display.Contains("Forced"),
            "F8 still forces the board and still outranks every gate");
        check(display.Contains("ApplyVanillaBlocks(vanillaPage, vanillaSucceeded)") && display.Contains("RestoreHidden();"),
            "the vanilla blocks are still put away (after the table drew, 1.19.47 review #3) and still restored - the page"
                + " handed over is the live one (`LivePage`, 1.19.57), never the cache a repaint may already have cleared");
        check(display.Contains("ResultsScroll.FontFor(fitted, Plugin.ResultFontScale(succeeded))")
              && display.Contains("float fittedCeiling = squeezed ? ResultsFontGrowth.BandFillCeiling : fillCeiling;"),
            "the board is drawn at the size its own strip really fits - a band that cannot hold the comfort glyph is fitted against itself - and any overflow is scrolled");
        check(display.Contains("Completion.Observe(Time.frameCount, true, area.Top)")
              && display.Contains("Footer.Observe(Time.frameCount, measured, top)")
              && display.Contains("Title.Observe(Time.frameCount, active, alpha, measured"),
            "the 1.19.12 settle gates all still feed the placement path as the drawn-pixel route's diagnostics - and none of them is a placement precondition any more (1.19.21)");

        // The anchor's own wiring, read back from the production source.
        // 1.19.31 re-point: the ladder is asked with the *element's own* canvas mode, not with a mode shared by the two elements.
        check(panel.Contains("AnchorCameraLadder.Select(candidates, layer, elementMode, canvasDrawn, element != null, out int inspected)"),
            "the panel asks the ladder which camera draws the element - with that element's own canvas mode, and whether its RectTransform geometry may answer instead");
        check(CountOf(panel, "_anchorScreenWidth = Screen.width") >= 2,
            "and the resolution baseline is still written where the comparison can see it");
    }

    // The HUD's anchor state across levels (the independent review's second finding), modelled on the fields the panel keeps
    // and the two conditions `SampleAnchor` locks on: "one second still" (four consecutive samples within 2 px, the first of
    // them having nothing to compare against) and "six seconds sampled" (twenty-four samples). A level entered with the
    // previous level's accumulators still in place locks on its own first sample, because both conditions are already met by
    // the old scene's numbers.
    private static void TheAnchorSamplingStateDoesNotSurviveALevel(Action<bool, string> check)
    {
        int carried = SamplesUntilLock(true, out float carriedSampled);
        int fresh = SamplesUntilLock(false, out float freshSampled);
        check(carried == 1,
            $"carried over from the previous level, the first still-looking sample of the new one locks the anchor (samples={carried})");
        check(carriedSampled >= 6f,
            $"because the sampled-time accumulator was already at {carriedSampled:0.00}s of the six that force a lock");
        check(fresh == 5,
            $"a level that starts from its own state locks after its own still samples - the first has nothing to compare against, then four at 0.25 s make the second (samples={fresh})");
        check(freshSampled < 6f && freshSampled >= 1f,
            $"and it is nowhere near the six-second cap when it does ({freshSampled:0.00}s of sampling)");
        // A HUD that never holds still must not lock on its first sample in the new level either: the six-second cap is what
        // answers it, twenty-four samples later, with the last rectangle it saw.
        check(SamplesUntilLock(true, out _, settleEveryFrame: false) == 1
              && SamplesUntilLock(false, out _, settleEveryFrame: false) == 24,
            "a HUD that never settles locks on the six-second cap in the new level (24 samples), not on its first sample via the previous level's numbers");

        // The production source is what this model describes.
        string panel = Source("PanelBehaviour.cs");
        string entry = Between(panel, "                if (inLevel)\n                {", "            bool visible = inLevel ?");
        check(entry.Length > 0,
            "the level-entry branch was found in PanelBehaviour (the model above describes a block this build really has)");
        check(entry.Contains("_anchorSettle = 0f;") && entry.Contains("_anchorSampled = 0f;")
              && entry.Contains("_anchorLastX = float.NaN;") && entry.Contains("_anchorLastY = float.NaN;"),
            "the level-entry branch drops the previous level's settle window, sample count and last pixel");
        check(!entry.Contains("_anchorLocked = false;"),
            "and it deliberately leaves a locked anchor alone: a lock is only ever taken from a settled rectangle");

        // A re-anchor - a configuration reload or a resolution change - resets the same four, which is the regression this
        // must not break: `UnlockAnchor` is the one place a mid-level re-measure starts.
        string unlock = Between(panel, "    private void UnlockAnchor()", "    private void Update()");
        check(unlock.Contains("_anchorSettle = 0f;") && unlock.Contains("_anchorSampled = 0f;")
              && unlock.Contains("_anchorLastX = float.NaN;") && unlock.Contains("_anchorLastY = float.NaN;"),
            "a re-anchor resets the same four, so a resolution change or a config reload starts a clean window too");

        // The lock line must not present sampling ticks as elapsed time.
        check(panel.Contains("sample(s) and {_anchorSampled:0.00}s of sampling time (not wall clock"),
            "the lock line reports how many samples it took and says its time is sampling time, not wall clock");
        check(panel.Contains("{_anchorPolicy.Waited:0.#}s of waiting"),
            "the one figure presented as waiting is the policy's own accumulated retry delay, on the give-up line");
        check(!panel.Contains("locked after {_anchorSampled:0.00}s at"),
            "the old line that printed sampling ticks as if they were elapsed seconds is gone");

        // The camera diagnostic the independent report asked for, and the projection it must not invent: the panel's own
        // rectangle is never used as an icon anchor, so a null camera still means "keep PositionX/PositionY".
        check(panel.Contains("layerCanvas=") && panel.Contains("layerCanvasRenderModeProperty=") && panel.Contains("iconHasRectTransform="),
            "the camera line names the canvas reads it decided from, so 'no canvas' and 'unreadable render mode' are distinguishable");
        check(!panel.Contains("panel-rect") && !panel.Contains("m_iconDisplay.RectTrans"),
            "and nothing projects the icon panel's rectangle as if it were the icon");
        check(CountOf(panel, "= RectTransformUtility.WorldToScreenPoint(null, world);") == 1,
            "the camera-less projection exists exactly once, in the overlay branch that is allowed to use it");
    }

    // The production lock arithmetic of `PanelBehaviour.SampleAnchor`, on the fields it keeps: a sample counts as "still"
    // when it is within 2 px of the previous one, `_anchorSettle` grows by one SampleInterval per still sample and is reset
    // by a moving one, `_anchorSampled` grows by one SampleInterval per sample whatever it does, and the anchor locks at
    // "settle >= 1" or "sampled >= 6". `keepState` is the old behaviour (nothing reset at level entry); the fresh run is
    // what the fix does.
    private static int SamplesUntilLock(bool keepState, out float sampledAtLock, bool settleEveryFrame = true)
    {
        const float interval = 0.25f;   // AnchorSamplePolicy.SettleInterval, one per `SampleAnchor` attempt
        float settle = keepState ? 1f : 0f;
        float sampled = keepState ? 5.75f : 0f;
        float lastX = keepState ? 1848f : float.NaN;
        int samples = 0;
        for (int attempt = 0; attempt < 40; attempt++)
        {
            samples++;
            sampled += interval;
            // The icon sits at the same place on a 2560x1440 window: 1848 px is where the recorded log's table started.
            float x = settleEveryFrame ? 1848f : 1848f + attempt * 4f;
            bool still = !float.IsNaN(lastX) && Math.Abs(x - lastX) < 2f;
            settle = still ? settle + interval : 0f;
            lastX = x;
            if (settle >= 1f || sampled >= 6f)
            {
                sampledAtLock = sampled;
                return samples;
            }
        }
        sampledAtLock = sampled;
        return 0;
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

    private static int CountOf(string source, string needle)
    {
        int count = 0, at = -1;
        while ((at = source.IndexOf(needle, at + 1, StringComparison.Ordinal)) >= 0) count++;
        return count;
    }

    private static bool Near(float left, float right) => Math.Abs(left - right) <= 0.6f;
}
