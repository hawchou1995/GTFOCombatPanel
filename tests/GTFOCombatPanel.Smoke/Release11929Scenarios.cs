using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// 1.19.29: an anchor's *answer* and an anchor's *authorization* are two different things, and the recorded level had only the
// second one.
//
// Two fields of the 1.19.28 camera line said things the rest of the same line contradicted:
//
//   1. `anchorSource=canvas-geometry` beside `IconElement: ...,gate=refused(...)` / `RowElement: ...,gate=refused(...)` and
//      `iconSource=config`. The field was `AnchorCameraChoice.Provenance(choice.Projection)`, i.e. the rule the ladder *granted*.
//      Granting a rule measures nothing: `PanelBehaviour.MeasureElement` still has to project the element's four corners with a
//      real camera, and on that level `projectCamera=none` says it had none. So the one word a reader greps for the answer was
//      printing the attempt.
//   2. `cellUiMaskProof=no` - one word for three questions (was the CellUI camera offered, was it alive, did its culling mask
//      contain layer 5). The recorded line's own `cellUiCamera=CellUI_Camera(Clone)` proves the object existed, so the report that
//      followed had to *guess* which of the three the `no` was about, and printed the guess as the cause.
//
// What is asserted below: the word an answer gets versus the word an authorization gets (including on the exact recorded shape),
// that a failed attempt can never reach a lock, that the refusals that must stay refusals still are (no camera, wrong layer, no
// RectTransform, invalid rectangle, off-window rectangle), that the fallback and its watch recovery are untouched, and that the
// diagnostic now names every candidate and both elements' ancestor canvas - the two reads the recorded line was missing.
//
// What cannot be asserted here: the Unity side. `Camera.enabled`, `Camera.isActiveAndEnabled`, `Canvas.renderMode` and
// `Canvas.pixelRect` are read at runtime and cannot be faked headlessly; the assertions below drive the *policies* those reads
// feed, and check the source for the calls that make them.
internal static class Release11929Scenarios
{
    private const int IconLayer = 5;

    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        TheAnswerIsNeverAnAuthorization(check);
        TheRecordedFailureModeIsReproduced(check);
        AFailedAttemptNeverLocksAnywhere(check);
        ARefusedProjectionStaysRefused(check);
        TheFallbackAndItsRecoveryAreUnchanged(check);
        TheElementCanvasIsWhatTheGeometryAnswerProjectsThrough(check);
        Wiring(check);
    }

    // ---- 1. the answer, and the authorization ---------------------------------------------------------------

    private static void TheAnswerIsNeverAnAuthorization(Action<bool, string> check)
    {
        check(AnchorCameraChoice.Provenance(AnchorProjection.Camera, measured: true) == "camera"
              && AnchorCameraChoice.Provenance(AnchorProjection.CameraLessOverlay, measured: true) == "canvas-overlay"
              && AnchorCameraChoice.Provenance(AnchorProjection.Rect, measured: true) == "canvas-geometry",
            "a rule that really produced the rectangle keeps the three words the log has always used");

        check(AnchorCameraChoice.Provenance(AnchorProjection.Camera, measured: false) == "config"
              && AnchorCameraChoice.Provenance(AnchorProjection.CameraLessOverlay, measured: false) == "config"
              && AnchorCameraChoice.Provenance(AnchorProjection.Rect, measured: false) == "config",
            "and a rule that produced nothing is `config` for every one of them: the table is on PositionX/PositionY, which is exactly what config has always meant here");

        check(AnchorCameraChoice.Authorization(AnchorProjection.Rect, measured: false) == "canvas-geometry(refused)"
              && AnchorCameraChoice.Authorization(AnchorProjection.Camera, measured: false) == "camera(refused)",
            "the rule a failed attempt was granted keeps its own word, and carries `(refused)` so it cannot be read as an answer");
        check(AnchorCameraChoice.Authorization(AnchorProjection.Rect, measured: true) == "canvas-geometry"
              && AnchorCameraChoice.Authorization(AnchorProjection.Camera, measured: true) == "camera",
            "while a rule that answered prints the bare word");
        check(AnchorCameraChoice.Authorization(AnchorProjection.None, measured: false) == "none"
              && AnchorCameraChoice.Authorization(AnchorProjection.None, measured: true) == "none",
            "and a ladder that authorized nothing says `none` either way - there is no word to earn");

        // The note the per-attempt failure line and the give-up line print.
        var geometry = new AnchorCameraChoice(AnchorProjection.Rect, AnchorCameraSource.None, -1, string.Empty);
        string refusedNote = AnchorCameraChoice.Describe(geometry, measured: false);
        check(!refusedNote.StartsWith("the element's own RectTransform layout rectangle, projected by", StringComparison.Ordinal)
              && refusedNote.Contains("refused", StringComparison.Ordinal)
              && refusedNote.Contains("RectTransform", StringComparison.Ordinal),
            "a geometry attempt that refused describes itself as an authorization that refused, never as a projection: " + refusedNote);
        check(AnchorCameraChoice.Describe(geometry, measured: true) == AnchorCameraChoice.Describe(geometry),
            "and an attempt that answered keeps the single-argument sentence, so the lock line is unchanged");

        var camera = new AnchorCameraChoice(AnchorProjection.Camera, AnchorCameraSource.CellUiCamera, 0, string.Empty);
        check(AnchorCameraChoice.Describe(camera, measured: true).StartsWith("projected through", StringComparison.Ordinal)
              && AnchorCameraChoice.Describe(camera, measured: true) == AnchorCameraChoice.Describe(camera),
            "a proven camera projection still reads `projected through <source>`, which is what the lock line prints");
        check(AnchorCameraChoice.Describe(camera, measured: false).StartsWith("authorized by", StringComparison.Ordinal)
              && AnchorCameraChoice.Describe(camera, measured: false).Contains("no usable rectangle", StringComparison.Ordinal),
            "and a camera that was authorized but answered no usable rectangle says both halves");
        check(AnchorCameraChoice.Describe(AnchorCameraChoice.Unusable("nothing at all"), measured: false).StartsWith("no projection:", StringComparison.Ordinal),
            "the unusable ladder keeps the `no projection:` wording a log reader greps for");
    }

    // ---- 2. the recorded shape, driven through production ------------------------------------------------------------------

    // The recorded level, as far as its own line states it: four candidates of which exactly one is not alive
    // (`candidates=4,alive=3`), a CellUI camera object that exists, a `RectTransform` on both elements, a screen-space-camera
    // canvas, and no camera whose mask contains layer 5. What the line does *not* state is which candidate was the dead one -
    // that is the guess this round removes from the source of the log.
    private static void TheRecordedFailureModeIsReproduced(Action<bool, string> check)
    {
        var recorded = new List<AnchorCameraCandidate>
        {
            // index 0: the CellUI camera, the object `cellUiCamera=CellUI_Camera(Clone)` names. Its mask does contain layer 5 -
            // the recorded line cannot say either way, and that is the point: the mask is not what refused it here.
            new AnchorCameraCandidate(AnchorCameraSource.CellUiCamera, alive: false, cullingMask: 1 << IconLayer, depth: 0),
            // the three the count says were alive, none of them drawing layer 5.
            new AnchorCameraCandidate(AnchorCameraSource.PageCamera, alive: true, cullingMask: 1 << 3, depth: 1),
            new AnchorCameraCandidate(AnchorCameraSource.MainCamera, alive: true, cullingMask: (1 << 0) | (1 << 3), depth: 0),
            new AnchorCameraCandidate(AnchorCameraSource.AllCameras, alive: true, cullingMask: 1 << 4, depth: 2)
        };

        check(AnchorCameraLadder.Inspect(recorded) == "candidates=4,alive=3",
            "the reconstructed list reproduces the recorded count, so the rest of this scenario is about that same level");

        AnchorCameraChoice choice = AnchorCameraLadder.Select(recorded, IconLayer, AnchorCanvasMode.ScreenSpaceCamera,
            canvasDrawn: false, hasRectTransform: true, out int inspected);
        check(inspected == 4 && choice.Projection == AnchorProjection.Rect && choice.Index < 0,
            "the ladder grants the canvas-geometry rule and nothing else - which is what the recorded `projection=canvas-geometry` was");
        check(choice.Reason.Contains("RectTransform", StringComparison.Ordinal),
            "and its reason names the RectTransform, not a camera: " + choice.Reason);

        // The gate the panel asks before it may project anything with that rule, on the recorded facts: the element's canvas is
        // screen-space-camera, so the only camera that may project it is that canvas' own - and the refusal is the one the line printed.
        bool legitimate = AnchorCanvasProjection.CameraLegitimate(hasCamera: false, AnchorCanvasMode.ScreenSpaceCamera)
            && AnchorElementCanvasPolicy.CanProject(AnchorElementCanvasPolicy.CameraOrigin.None);
        check(!legitimate,
            "the production gate then refuses to project through a null camera on a screen-space-camera canvas - the recorded `gate=refused`");

        check(AnchorCameraChoice.Provenance(choice.Projection, measured: false) == "config",
            "so the anchor's own source is `config`: the recorded `anchorSource=canvas-geometry` was the authorization, never the answer");
        check(AnchorCameraChoice.Authorization(choice.Projection, measured: false) == "canvas-geometry(refused)",
            "and the attempt's authorization is printed as refused beside it");
        check(AnchorCameraChoice.Describe(choice, measured: false).Contains("was granted, but there was no camera", StringComparison.Ordinal),
            "with the words the failure line now prints: the authorization was granted, the camera to project it was not there");

        // The candidate formatter names the dead one instead of counting it.
        string detail = AnchorCameraLadder.Detail(recorded, IconLayer);
        check(detail.StartsWith("each=#", StringComparison.Ordinal) && CountOf(detail, ";") == 3,
            "the per-candidate field has one entry per offered camera: " + detail);
        check(detail.Contains("#0:cellui,alive=no", StringComparison.Ordinal),
            "and it names the CellUI camera as the one that is not alive, which the recorded count could not: " + detail);
        check(detail.Contains("#0:cellui,alive=no,mask=0x00000020,rendersLayer=yes", StringComparison.Ordinal),
            "together with the fact that is *not* the reason - its mask does contain layer 5, so `cellUiMaskProof=no` was never a mask fact: " + detail);
        check(detail.Contains("#1:page,alive=yes", StringComparison.Ordinal) && detail.Contains("rendersLayer=no", StringComparison.Ordinal),
            "and it says which of the living cameras do not draw the icon's layer");
        check(AnchorCameraLadder.Detail(new List<AnchorCameraCandidate>(), IconLayer) == "each=none",
            "an empty list is said to be empty rather than printed as nothing at all");

        // The reverse: the same list with a living CellUI camera is a real answer, and it is named as one.
        var living = new List<AnchorCameraCandidate>
        {
            new AnchorCameraCandidate(AnchorCameraSource.CellUiCamera, alive: true, cullingMask: 1 << IconLayer, depth: 0)
        };
        AnchorCameraChoice proven = AnchorCameraLadder.Select(living, IconLayer, AnchorCanvasMode.ScreenSpaceCamera,
            canvasDrawn: false, hasRectTransform: true, out _);
        check(proven.Projection == AnchorProjection.Camera && proven.Source == AnchorCameraSource.CellUiCamera
              && AnchorCameraChoice.Provenance(proven.Projection, measured: true) == "camera",
            "a CellUI camera that is alive and really draws layer 5 is measured as `anchorSource=camera` - the other legitimate answer is untouched");
    }

    // ---- 3. nothing that failed can be locked on -----------------------------------------------------------------

    private static void AFailedAttemptNeverLocksAnywhere(Action<bool, string> check)
    {
        check(!AnchorSample.Missing(AnchorSampleFailure.Unmeasurable).Measured
              && !AnchorSample.Missing(AnchorSampleFailure.NoProjection).Measured
              && !AnchorSample.Missing(AnchorSampleFailure.NoIcon).Measured
              && !AnchorSample.Thrown("the read itself threw").Measured,
            "a refused projection, a missing element and a throwing read are all still `not measured`, so none of them is a rectangle");
        check(!AnchorSample.Of(new LayoutRect(100f, 200f, 0f, 40f)).Measured
              && !AnchorSample.Of(new LayoutRect(100f, 200f, 40f, 0f)).Measured,
            "and a rectangle with no area is not a measurement either, whatever produced it");
        check(AnchorSample.Of(new LayoutRect(100f, 200f, 210.59f, 110.16f), AnchorElementSource.Flashlight).Measured,
            "while a real rectangle is, so the guard above is not refusing everything");

        string panel = Code(Source("PanelBehaviour.cs"));
        int guard = panel.IndexOf("if (!sample.Measured)", StringComparison.Ordinal);
        int fallback = panel.IndexOf("_anchorFallback = true;", StringComparison.Ordinal);
        int lockTaken = panel.IndexOf("_anchorLocked = true;", StringComparison.Ordinal);
        check(guard >= 0 && fallback > guard && lockTaken > guard,
            "the panel arms the configured position on the failing branch, and the only lock in the file is on the answering branch below it");
        check(CountOf(panel, "_anchorLocked = true;") == 1,
            "there is exactly one place that locks, so a second path to a lock would fail here");
        check(panel.Contains("if (!TryResolveOrigin(_grid, out var origin)) { _grid = null; _content = null; _origin = default; return; }", StringComparison.Ordinal)
              && panel.Contains("return false;   // still settling", StringComparison.Ordinal),
            "and a round that has not answered draws nothing rather than jumping to a guess");
    }

    // ---- 4. the refusals that must stay refusals -----------------------------------------------------------------

    private static void ARefusedProjectionStaysRefused(Action<bool, string> check)
    {
        var none = new List<AnchorCameraCandidate>();
        check(!AnchorCameraLadder.Select(none, IconLayer, AnchorCanvasMode.ScreenSpaceCamera, canvasDrawn: false, hasRectTransform: false, out _).Usable,
            "no camera and no RectTransform is still no answer at all");
        check(AnchorCameraLadder.Select(none, IconLayer, AnchorCanvasMode.ScreenSpaceCamera, canvasDrawn: false, hasRectTransform: true, out _).Projection == AnchorProjection.Rect,
            "a RectTransform on a screen-space canvas is still the geometry authorization - the rule this round did not change");

        check(!AnchorCanvasProjection.CameraLegitimate(hasCamera: false, AnchorCanvasMode.ScreenSpaceCamera)
              && !AnchorCanvasProjection.CameraLegitimate(hasCamera: false, AnchorCanvasMode.WorldSpace)
              && !AnchorCanvasProjection.CameraLegitimate(hasCamera: false, AnchorCanvasMode.Unknown)
              && AnchorCanvasProjection.CameraLegitimate(hasCamera: false, AnchorCanvasMode.Overlay),
            "a null camera is still legitimate only inside a real screen-space overlay canvas - the protection is not relaxed by this round");

        var blind = new List<AnchorCameraCandidate>
        {
            new AnchorCameraCandidate(AnchorCameraSource.PageCamera, alive: true, cullingMask: 1 << 3, depth: 0),
            new AnchorCameraCandidate(AnchorCameraSource.MainCamera, alive: true, cullingMask: 1 << 4, depth: 1)
        };
        check(AnchorCameraLadder.Select(blind, IconLayer, AnchorCanvasMode.Unknown, canvasDrawn: false, hasRectTransform: false, out _).Projection == AnchorProjection.None,
            "cameras that do not have the element's layer in their mask still project nothing");
        check(!AnchorCameraLadder.Select(blind, -1, AnchorCanvasMode.Unknown, canvasDrawn: false, hasRectTransform: false, out _).Usable,
            "an unreadable layer is still a refusal, not a wildcard");
        var textured = new List<AnchorCameraCandidate>
        {
            new AnchorCameraCandidate(AnchorCameraSource.MainCamera, alive: true, cullingMask: 1 << IconLayer, depth: 0, targetTexture: true)
        };
        check(AnchorCameraLadder.Select(textured, IconLayer, AnchorCanvasMode.Unknown, canvasDrawn: false, hasRectTransform: false, out _).Projection == AnchorProjection.None,
            "and a camera that renders into a RenderTexture is still never used, however well its mask matches");

        // The rectangle itself has to survive `ScreenBounds`: non-finite, no area, off the window, or the whole window.
        check(!AnchorRectProjection.TryFromCorners(new[] { float.NaN, 0f, 10f, 10f }, new[] { 0f, 0f, 10f, 10f }, 2560f, 1440f, out _, out _),
            "a corner that is not a number refuses the rectangle");
        check(!AnchorRectProjection.TryFromCorners(new[] { 100f, 100f, 100f, 100f }, new[] { 200f, 200f, 240f, 240f }, 2560f, 1440f, out _, out _),
            "a rectangle with no width is not a rectangle");
        check(!AnchorRectProjection.TryFromCorners(new[] { 2600f, 2700f, 2600f, 2700f }, new[] { 100f, 100f, 200f, 200f }, 2560f, 1440f, out _, out _),
            "and one that lies entirely outside the window is refused rather than clamped into it");
        check(!AnchorRectProjection.TryFromCorners(new[] { 0f, 2560f, 0f, 2560f }, new[] { 0f, 0f, 1440f, 1440f }, 2560f, 1440f, out _, out _),
            "and so is one that covers the whole window, which is what a projection that silently used the window's own size would produce");
    }

    // ---- 5. the fallback, and the watch that may still answer -------------------------------------------------

    private static void TheFallbackAndItsRecoveryAreUnchanged(Action<bool, string> check)
    {
        var policy = new AnchorSamplePolicy();
        int attempts = 0;
        bool gaveUp = false;
        float now = 0f;
        for (int frame = 0; frame < 4000 && !policy.Abandoned; frame++)
        {
            now = frame * AnchorSamplePolicy.SettleInterval;
            if (!policy.Due(now)) continue;
            policy.Attempt(now, () => AnchorSample.Missing(AnchorSampleFailure.Unmeasurable), out gaveUp);
            attempts++;
        }
        check(policy.Abandoned && attempts == AnchorSamplePolicy.Budget,
            $"a HUD that can never answer still spends exactly the budget ({attempts} attempt(s)) and then gives up");
        check(gaveUp, "and the failure that spends the last retry is the one that reports the give-up, so the caller writes it once");

        // The retry schedule and the watch, as real time branches rather than as a state that happens to make a predicate true.
        //
        // Before the give-up: `Due` refuses the tick after each failure and grants it once the delay the policy itself reported has
        // elapsed - that is what "at most twelve attempts, spaced out" means, and it can only be asserted on real times.
        var schedule = new AnchorSamplePolicy();
        check(schedule.Due(0f), "the first attempt of a round is owed immediately");
        float first = 0f;
        schedule.Attempt(first, () => AnchorSample.Missing(AnchorSampleFailure.Unmeasurable), out _);
        float afterFailure = AnchorSamplePolicy.DelayAfter(schedule.Failures);
        check(!schedule.Due(first + afterFailure * 0.5f) && schedule.Due(first + afterFailure),
            $"a failed attempt is not retried before its own delay ({afterFailure:0.##}s) and is retried after it");
        check(!schedule.WatchDue(first),
            "and a round that still has budget behind it never spends the low-rate watch - `WatchDue` answers false while it is not abandoned");

        float watchAt = now + AnchorSamplePolicy.WatchInterval;
        check(policy.WatchDue(watchAt) && !policy.WatchDue(watchAt + 0.1f),
            "after the give-up the watch is owed once per interval and not on the next poll, so it is a cadence and not a poll");
        AnchorSample late = policy.Attempt(watchAt,
            () => AnchorSample.Of(new LayoutRect(100f, 200f, 210.59f, 110.16f), AnchorElementSource.Flashlight), out bool revived);
        check(late.Measured && !revived && !policy.Abandoned && policy.Failures == 0,
            "a watch sample that finally measures ends the give-up and clears the failure streak");
        check(policy.Attempts == 0,
            "and the revived round owes its own attempt numbering again, so its first failure line says attempt 1");
        check(policy.Due(watchAt + 0.01f),
            "the revived round is measuring again, so its own settle cadence - not the watch - decides the next attempt");
        check(!policy.WatchDue(watchAt + AnchorSamplePolicy.WatchInterval),
            "and it is no longer abandoned, so the watch is silent until a round gives up again");
    }

    // ---- 6. the element's own canvas is the authority (1.19.30) ---------------------------------------------------------

    // The reference mod (`catrice.AccuracyShow`) places its text by *parenting* a clone into the HUD hierarchy and setting a
    // `localPosition` - no camera, no canvas read, no world-to-screen call - because the object it clones is rendered by the same
    // hierarchy it is parented into. Its text component is `TMPro.TextMeshPro` (the MeshRenderer variant, per its own decompiled
    // source and this build's interop: `TMPro.TextMeshPro : TMPro.TMP_Text`), NOT `TextMeshProUGUI`, so the mod's existence is *not*
    // evidence that a `UnityEngine.Canvas` sits above the inventory row - the reference path does not need one.
    //
    // That literal copy is not available to this build anyway: the table is an IMGUI surface whose columns, right-aligned numeric
    // slots, background plate, window clamping, F8 toggle and `PositionX/PositionY/OffsetX/OffsetY` fallback all live in screen
    // pixels (`HudTable.Draw`), and a native clone would give those up and be clipped by its canvas. What is taken instead is the
    // only native fact that can answer "how does this element become window pixels": the element's own ancestor `Canvas`, whose
    // `renderMode` decides whether that is an identity projection (overlay) or a camera projection (screen-space-camera) - and the
    // whole decision is pure policy, driven here.
    private static void TheElementCanvasIsWhatTheGeometryAnswerProjectsThrough(Action<bool, string> check)
    {
        // ---- which render mode the element is composited in ----
        check(AnchorElementCanvasPolicy.Mode(true, AnchorCanvasMode.Overlay, AnchorCanvasMode.ScreenSpaceCamera) == AnchorCanvasMode.Overlay,
            "a real screen-space overlay canvas wins over the layer's enum - the case the recorded `canvasRenderMode=screen-space-camera` could not even express");
        check(AnchorElementCanvasPolicy.Mode(true, AnchorCanvasMode.ScreenSpaceCamera, AnchorCanvasMode.Overlay) == AnchorCanvasMode.ScreenSpaceCamera,
            "and a screen-space-camera canvas wins too, whatever the layer asked for");
        check(AnchorElementCanvasPolicy.Mode(true, AnchorCanvasMode.WorldSpace, AnchorCanvasMode.ScreenSpaceCamera) == AnchorCanvasMode.WorldSpace,
            "a world-space canvas stays world-space");
        check(AnchorElementCanvasPolicy.Mode(false, AnchorCanvasMode.Overlay, AnchorCanvasMode.ScreenSpaceCamera) == AnchorCanvasMode.ScreenSpaceCamera
              && AnchorElementCanvasPolicy.Mode(false, AnchorCanvasMode.Overlay, AnchorCanvasMode.Unknown) == AnchorCanvasMode.Unknown,
            "and a hierarchy with no `Canvas` at all keeps exactly the fallback it had, `Unknown` included");
        check(!AnchorCanvasProjection.Authorized(hasRectTransform: true, AnchorCanvasMode.WorldSpace)
              && !AnchorCanvasProjection.Authorized(hasRectTransform: true, AnchorCanvasMode.Unknown),
            "the geometry authorization still refuses a world-space or unreadable canvas, so a real canvas cannot smuggle one in");

        // ---- which camera, decided by that canvas and by nothing else ----
        //
        // The recorded 1.19.30 defect: `Overlay` with a live ladder camera passed that camera, so an overlay canvas' window-pixel
        // coordinates were projected through a camera a second time. The overlay verdict must not depend on any camera at all.
        AnchorElementCanvasPolicy.CameraOrigin overlayWithLiveCamera = AnchorElementCanvasPolicy.Project(
            true, AnchorCanvasMode.Overlay, true, true, true);
        check(overlayWithLiveCamera == AnchorElementCanvasPolicy.CameraOrigin.Overlay
              && !AnchorElementCanvasPolicy.PassesCamera(overlayWithLiveCamera)
              && AnchorElementCanvasPolicy.CanProject(overlayWithLiveCamera),
            "an overlay canvas passes NO camera even when a live one is available - the identity projection is the only correct argument there");
        check(AnchorElementCanvasPolicy.Project(true, AnchorCanvasMode.Overlay, false, false, false) == AnchorElementCanvasPolicy.CameraOrigin.Overlay,
            "and the overlay verdict does not change when that camera is dead: the camera is not an input of an overlay canvas at all");

        check(AnchorElementCanvasPolicy.Project(true, AnchorCanvasMode.ScreenSpaceCamera, true, true, true) == AnchorElementCanvasPolicy.CameraOrigin.ElementCanvas
              && AnchorElementCanvasPolicy.PassesCamera(AnchorElementCanvasPolicy.CameraOrigin.ElementCanvas)
              && AnchorCanvasProjection.CameraLegitimate(hasCamera: true, AnchorCanvasMode.ScreenSpaceCamera),
            "a screen-space-camera canvas passes exactly its own live window camera - the one Unity documents for it");
        check(AnchorElementCanvasPolicy.Project(true, AnchorCanvasMode.ScreenSpaceCamera, true, false, true) == AnchorElementCanvasPolicy.CameraOrigin.ElementCanvasRefused
              && AnchorElementCanvasPolicy.Project(true, AnchorCanvasMode.ScreenSpaceCamera, true, true, false) == AnchorElementCanvasPolicy.CameraOrigin.ElementCanvasRefused
              && !AnchorElementCanvasPolicy.CanProject(AnchorElementCanvasPolicy.CameraOrigin.ElementCanvasRefused)
              && !AnchorElementCanvasPolicy.PassesCamera(AnchorElementCanvasPolicy.CameraOrigin.ElementCanvasRefused),
            "a switched-off camera, or one that renders into a RenderTexture, is refused rather than replaced by another camera");
        check(AnchorElementCanvasPolicy.Project(true, AnchorCanvasMode.ScreenSpaceCamera, false, false, false) == AnchorElementCanvasPolicy.CameraOrigin.None
              && !AnchorElementCanvasPolicy.CanProject(AnchorElementCanvasPolicy.CameraOrigin.None),
            "a screen-space-camera canvas with no camera at all is `none`, and no camera is invented for it");

        check(AnchorElementCanvasPolicy.Project(false, AnchorCanvasMode.Overlay, true, true, true) == AnchorElementCanvasPolicy.CameraOrigin.None
              && !AnchorElementCanvasPolicy.CanProject(AnchorElementCanvasPolicy.CameraOrigin.None),
            "an element with NO ancestor canvas has no geometry answer, not even when a camera exists - world coordinates are not window pixels");
        check(AnchorElementCanvasPolicy.Project(true, AnchorCanvasMode.WorldSpace, true, true, true) == AnchorElementCanvasPolicy.CameraOrigin.None
              && AnchorElementCanvasPolicy.Project(true, AnchorCanvasMode.Unknown, true, true, true) == AnchorElementCanvasPolicy.CameraOrigin.None,
            "nor has a world-space canvas or one whose mode nobody could read");

        check(AnchorElementCanvasPolicy.Name(AnchorElementCanvasPolicy.CameraOrigin.ElementCanvas) != AnchorElementCanvasPolicy.Name(AnchorElementCanvasPolicy.CameraOrigin.None)
              && AnchorElementCanvasPolicy.Name(AnchorElementCanvasPolicy.CameraOrigin.Overlay) != AnchorElementCanvasPolicy.Name(AnchorElementCanvasPolicy.CameraOrigin.None)
              && AnchorElementCanvasPolicy.SourceName(true) == "element-canvas"
              && AnchorElementCanvasPolicy.SourceName(false) == "layer-property",
            "and the log has a word for each of them, including where the render mode came from");

        // ---- the two elements are independent ----
        //
        // They are resolved and authorized one at a time, so an overlay icon row beside a screen-space-camera `Background Fade` (or the
        // reverse) can neither be authorized together nor projected with one another's camera.
        AnchorElementCanvasPolicy.CameraOrigin iconOrigin = AnchorElementCanvasPolicy.Project(true, AnchorCanvasMode.Overlay, false, false, false);
        AnchorElementCanvasPolicy.CameraOrigin rowOrigin = AnchorElementCanvasPolicy.Project(true, AnchorCanvasMode.ScreenSpaceCamera, true, false, true);
        check(iconOrigin == AnchorElementCanvasPolicy.CameraOrigin.Overlay && AnchorElementCanvasPolicy.CanProject(iconOrigin)
              && !AnchorElementCanvasPolicy.PassesCamera(iconOrigin)
              && rowOrigin == AnchorElementCanvasPolicy.CameraOrigin.ElementCanvasRefused && !AnchorElementCanvasPolicy.CanProject(rowOrigin),
            "an overlay element and a screen-space-camera element under different canvases are decided apart: one projects with no camera, the other refuses");
        check(AnchorElementCanvasPolicy.Mode(true, AnchorCanvasMode.WorldSpace, AnchorCanvasMode.Overlay) == AnchorCanvasMode.WorldSpace
              && !AnchorCanvasProjection.Authorized(hasRectTransform: true, AnchorCanvasMode.WorldSpace),
            "and one element's overlay canvas never authorizes the other's world-space one");

        // ---- the rectangle the overlay answer produces ----
        check(AnchorCanvasProjection.CameraLegitimate(hasCamera: false, AnchorCanvasMode.Overlay),
            "an overlay canvas makes the null camera legitimate - Unity composites it in window pixels, with no guess involved");
        check(CornersOf(2400f, 1300f, 1f, 0.5f, 210.59f, 110.16f, 1f, 2560f, 1440f, out LayoutRect overlayRect)
              && overlayRect.Width > 200f && overlayRect.Height > 100f
              && overlayRect.Right <= 2560f && overlayRect.Bottom <= 1440f,
            $"the icon row on such a canvas measures to a real window rectangle: ({overlayRect.X:0.##},{overlayRect.Y:0.##}) {overlayRect.Width:0.##}x{overlayRect.Height:0.##}");
        check(AnchorCameraChoice.Provenance(AnchorProjection.Rect, measured: false) == "config"
              && AnchorCameraChoice.Authorization(AnchorProjection.Rect, measured: false) == "canvas-geometry(refused)",
            "and every refusal above still leaves the table on `config` with the attempt reported as `canvas-geometry(refused)`");
    }

    // ---- wiring, read from the production source ------------------------------------------------------------------

    private static void Wiring(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the 1.19.29 anchor regression locates the production sources");
        if (root == null) return;

        string panel = Code(File.ReadAllText(Path.Combine(root, "PanelBehaviour.cs")));
        check(panel.Contains("anchorSource={AnchorCameraChoice.Provenance(choice.Projection, measured)}", StringComparison.Ordinal),
            "the camera line's source word is the measured answer, so `canvas-geometry` can only appear when that geometry produced a rectangle");
        check(!panel.Contains("anchorSource={AnchorCameraChoice.Provenance(choice.Projection)}", StringComparison.Ordinal),
            "and the authorisation-only form is gone - it is the exact text that printed `canvas-geometry` for a refused attempt");
        check(panel.Contains("authorization={AnchorCameraChoice.Authorization(choice.Projection, measured)}", StringComparison.Ordinal),
            "the rule the attempt was granted is printed beside it, with `(refused)` on it when it did not answer");
        check(panel.Contains("AnchorCameraChoice.Describe(icon.Choice, icon.Measured)", StringComparison.Ordinal),
            "and the icon-row fallback line describes *that* refusal as a refusal too, since it exists because of one");
        check(panel.Contains("_anchorCameraNote = AnchorCameraChoice.Describe(decided.Choice, decided.Measured);", StringComparison.Ordinal),
            "and the note the per-attempt and give-up lines print is told whether the attempt answered");

        check(panel.Contains("{AnchorCameraLadder.Detail(candidates, iconLayer)}", StringComparison.Ordinal),
            "the one-shot line prints every candidate's own facts");
        check(panel.Contains("if (_loggedAnchorCamera) return;", StringComparison.Ordinal)
              && CountOf(panel, "[DIAG] flashlight anchor camera:") == 1,
            "and still writes exactly one such line per measurement round, so nothing here can flood a log");
        check(panel.Contains("offered=", StringComparison.Ordinal) && panel.Contains(",activeInHierarchy=", StringComparison.Ordinal)
              && panel.Contains("cellUiMaskProof=", StringComparison.Ordinal),
            "the CellUI camera's proof is split into the questions it used to fold into one word");
        check(panel.Contains("{ElementCanvasDiagnostic(AnchorElementNames.Flashlight, iconCanvas)}", StringComparison.Ordinal)
              && panel.Contains("{ElementCanvasDiagnostic(AnchorElementNames.BackgroundFade, rowCanvas)}", StringComparison.Ordinal),
            "and both elements' own ancestor canvas is printed - the object 1.19.30 also reads the mode and the camera from");
        check(panel.Contains("cellUi.m_pixelWidth", StringComparison.Ordinal) && panel.Contains("cellUi.m_rectTransform", StringComparison.Ordinal),
            "with the CellUI canvas' pixel size and laid-out rectangle, which is what a camera-free mapping would have to agree with");
        // 1.19.30: the element's own canvas is the input to the decision, not only a description of it.
        check(panel.Contains("Canvas iconCanvas = RootCanvas(AncestorCanvas(sprite));", StringComparison.Ordinal)
              && panel.Contains("Canvas rowCanvas = RootCanvas(AncestorCanvas(background));", StringComparison.Ordinal)
              && panel.Contains("private static Canvas RootCanvas(Canvas canvas)", StringComparison.Ordinal),
            "the nearest ancestor canvas of each element is read once per attempt by the walk that already existed, and resolved to its root");
        check(panel.Contains("CanvasMode(layer, canvas, iconCanvas != null ? iconCanvas : rowCanvas)", StringComparison.Ordinal)
              && panel.Contains("AnchorElementCanvasPolicy.Mode(present, ancestor, fallback)", StringComparison.Ordinal),
            "and the render mode the geometry answer reasons about comes from it (`AnchorElementCanvasPolicy.Mode`), not from the layer's enum");
        check(panel.Contains("if (elementCanvas != null) { canvasCamera = elementCanvas.worldCamera; canvasCameraPresent = canvasCamera != null; }", StringComparison.Ordinal)
              && panel.Contains("AnchorElementCanvasPolicy.Project(", StringComparison.Ordinal)
              && panel.Contains("CameraAlive(canvasCamera), CameraWindowCamera(canvasCamera)", StringComparison.Ordinal)
              && panel.Contains("AnchorElementCanvasPolicy.PassesCamera(origin) ? canvasCamera : null", StringComparison.Ordinal),
            "the geometry answer's camera is that canvas' own `worldCamera`, and the argument passed is `null` unless the canvas says it needs one");
        check(panel.Contains("AnchorElementCanvasPolicy.CanProject(origin)", StringComparison.Ordinal)
              && panel.Contains("private static Canvas RootCanvas(Canvas canvas)", StringComparison.Ordinal)
              && panel.Contains("Canvas canvas = RootCanvas(AncestorCanvas(element));", StringComparison.Ordinal),
            "and one canvas identity serves both: the root canvas the ladder proves as a candidate is the same one the geometry answer reads mode and camera from");
        check(panel.Contains("renderModeSource=", StringComparison.Ordinal),
            "and the line says where the render mode came from, so `canvasRenderMode=` can no longer be read as a canvas fact when it was an enum");
        check(panel.Contains("layerCanvasScaler=", StringComparison.Ordinal)
              && panel.Contains("m_canvasScaler != null", StringComparison.Ordinal),
            "and whether the layer carries a `CanvasScaler` - a hint at best, printed as one");

        string ladder = File.ReadAllText(Path.Combine(root, "AnchorCameraLadder.cs"));
        check(ladder.Contains("public static string Provenance(AnchorProjection projection, bool measured)", StringComparison.Ordinal)
              && ladder.Contains("public static string Authorization(AnchorProjection projection, bool measured)", StringComparison.Ordinal),
            "the two words live in the production policy, beside the third one they are derived from");
        check(ladder.Contains("public static string Describe(AnchorCameraChoice choice, bool measured)", StringComparison.Ordinal),
            "and a refusal is described by the same place that describes an answer");
        check(ladder.Contains("public static string Detail(IReadOnlyList<AnchorCameraCandidate> candidates, int elementLayer)", StringComparison.Ordinal)
              && ladder.Contains("public static string Token(AnchorCameraSource source)", StringComparison.Ordinal),
            "the per-candidate field is pure policy too, so it is asserted here instead of through a log line nobody can run headlessly");

        Console.WriteLine("WIRING: RELEASE_11929_ANCHOR_ANSWER_VERSUS_AUTHORIZATION (static source check; the words, the recorded shape, the gates and the fallback are the runtime assertions above)");
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

    // The text with its comment lines removed, so a comment may name a call this build must not make.
    private static string Code(string text)
    {
        var kept = new List<string>();
        if (text == null) return string.Empty;
        foreach (string line in text.Split('\n'))
            if (!line.TrimStart().StartsWith("//", StringComparison.Ordinal)) kept.Add(line);
        return string.Join("\n", kept);
    }

    private static int CountOf(string text, string token) => text == null || token == null ? 0 : text.Split(new[] { token }, StringSplitOptions.None).Length - 1;

    // A synthetic orthographic camera on the canvas plane: `pivotScreenX/Y` is the element's pivot in Unity's screen coordinates
    // (y upwards), one local unit is `scale` pixels, and the four corners are reduced by production code into a window rectangle -
    // the same reduction `TryScreenRectFromLayout` performs once a real camera has projected them.
    private static bool CornersOf(float pivotScreenX, float pivotScreenY, float pivotX, float pivotY, float width, float height,
        float scale, float windowWidth, float windowHeight, out LayoutRect rect)
    {
        rect = default;
        if (!AnchorRectProjection.TryRectFromPivot(pivotX, pivotY, width, height, out float minX, out float maxX, out float minY, out float maxY))
            return false;
        var xs = new float[4];
        var ys = new float[4];
        for (int corner = 0; corner < 4; corner++)
        {
            float localX = (corner & 1) == 0 ? minX : maxX;
            float localY = (corner & 2) == 0 ? minY : maxY;
            xs[corner] = pivotScreenX + localX * scale;
            ys[corner] = windowHeight - (pivotScreenY + localY * scale);
        }
        return AnchorRectProjection.TryFromCorners(xs, ys, windowWidth, windowHeight, out rect, out _);
    }
}
