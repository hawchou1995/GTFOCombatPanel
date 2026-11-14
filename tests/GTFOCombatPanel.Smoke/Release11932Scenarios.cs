using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// 1.19.32: the anchor could never be measured on this game's HUD, because the rule that authorizes a projection asked for something
// this HUD does not have.
//
// The 2026-11-13 Test levels (LogOutput.log:2166 / :2644) answered, for both native elements, at the same moment:
//
//   flashlightElementCanvas=missing, background-fadeElementCanvas=missing        no ancestor `Canvas` at all
//   canvasRenderMode=screen-space-camera, renderModeSource=layer-property       the mode is an enum on the layer, not a canvas fact
//   cellsui camera: #0:cellui alive=no,alive=no,mask=0x00000020,texture=yes      the only camera that draws layer 5 is switched off
//                                                                              and renders into a RenderTexture
//   authorization=canvas-geometry(refused), anchorSource=config                 so every attempt refused and kept PositionX/Y
//
// and the elements themselves were perfectly readable the whole time (`flashlightRect=(-210.59,-110.16,210.59,110.16)/pivot(1,1)`,
// `background-fadeRect=(-300,-54.96,300,109.92)/pivot(1,0.5)`, `background-fadeBounds=250x30`). The HUD is a world-space surface -
// SpriteRenderers and world-space TMP under a `UI_Canvas` MonoBehaviour - so `AnchorElementCanvasPolicy` correctly refuses a `Canvas`
// that is not there, and the ladder correctly refuses a RenderTexture camera, and the union of both correct answers was "nothing".
//
// What this round adds is the one object that *is* this HUD's compositor: `GuiLayerBase.m_cellUICanvas.m_UICamera`, rendering that
// canvas into a texture of `m_pixelWidth x m_pixelHeight`. That camera's `WorldToScreenPoint` answers in the window's pixel grid when
// the texture *is* the window's pixel size and the canvas says it is laid out for exactly that many pixels. 1.19.35: the recorded
// log does **not** carry that texture size. What it prints is `cellUiPixel=2560x1440` - the canvas' own declared pixel grid, a
// different read - and the window it was drawn on was 2560x1440, so the numbers in `Recorded()` below are a *synthetic input* built
// from that pair, not a measurement. The production line now prints the camera's own render target beside them
// (`cellUiCanvas=...,cellUiPixel=...,cellUiCameraPixel=...` and, per element, `target=`), which is what a real run has to be compared
// against. Nothing here claims that equal pixel sizes prove the final composite has no offset or flip; that is a runtime observation
// and the only thing that could show it.
//
// What cannot be asserted here: Unity. `Camera.rect`, `Camera.pixelWidth`, `Transform.IsChildOf`, `UI_Canvas.m_pixelWidth` and the
// real render-texture blit are runtime reads. The assertions below drive the *policies* those reads feed, the recorded numbers they
// will produce, and the production source that performs them.
internal static class Release11932Scenarios
{
    private const int IconLayer = 5;

    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        TheRecordedLevelNowHasAProjection(check);
        TheLadderRefusalCannotVetoAVerifiedManualCanvas(check);
        TheManualCanvasDecidesOnlyWhenEveryFactHolds(check);
        ARealCanvasKeepsItsOwnAuthority(check);
        TheRectangleItWillProduce(check);
        ACameraIsItsNativePointer(check);
        Wiring(check);
    }

    // ---- 1. the recorded level, driven through production -------------------------------------------------------------------

    // The recorded level's own facts, field for field: no ancestor canvas, layer 5, the CellUI camera switched off, that camera's mask
    // containing exactly layer 5, the canvas laid out for 2560x1440, and the camera filling its texture. `enabled=no` is in there on
    // purpose: it is the recorded value and it must not be able to refuse this.
    //
    // 1.19.35: the camera's render target (`cameraPixelWidth/Height`) is a *synthetic* input. The recorded line prints
    // `cellUiPixel=2560x1440` - the canvas' declared grid - on a 2560x1440 window and never the texture the camera renders into, so
    // 2560x1440 is the value that would have to be read for the chain to hold; it is not a value that was read. The other facts in this
    // struct are the recorded ones.
    private static AnchorManualCanvasFacts Recorded(bool cameraEnabled = false)
        => new AnchorManualCanvasFacts(
            canvasPresent: true, cameraPresent: true, elementInsideCanvas: true, elementLayer: IconLayer,
            cameraDrawsLayer: true, cameraMask: 1 << IconLayer,
            cameraPixelWidth: 2560, cameraPixelHeight: 1440,
            canvasPixelWidth: 2560f, canvasPixelHeight: 1440f,
            windowWidth: 2560, windowHeight: 1440,
            viewportX: 0f, viewportY: 0f, viewportWidth: 1f, viewportHeight: 1f,
            cameraEnabled: cameraEnabled, cameraActive: true, cameraOrthographic: true, cameraOrthographicSize: 720f);

    private static void TheRecordedLevelNowHasAProjection(Action<bool, string> check)
    {
        AnchorManualCanvasFacts recorded = Recorded();
        check(recorded.Verified && AnchorManualCanvasPolicy.Refusal(recorded) == "none",
            "the recorded chain is complete: it is projected, and nothing refuses it");

        AnchorElementCanvasPolicy.CameraOrigin origin = AnchorElementCanvasPolicy.Project(
            elementCanvasPresent: false, AnchorCanvasMode.ScreenSpaceCamera,
            canvasCameraPresent: false, canvasCameraAlive: false, canvasCameraWindow: false, manual: recorded);
        check(origin == AnchorElementCanvasPolicy.CameraOrigin.ManualCanvas
              && AnchorElementCanvasPolicy.PassesCamera(origin)
              && AnchorElementCanvasPolicy.CanProject(origin),
            "an element with no ancestor Canvas is projected by the HUD's own manual canvas - the camera is passed, and the answer may produce a rectangle");

        // The ladder's own authorization is untouched: it still grants the canvas-geometry rule for a RectTransform, which is what
        // sends the attempt into the branch the manual canvas is consulted from.
        var ladderOnly = new List<AnchorCameraCandidate>
        {
            new AnchorCameraCandidate(AnchorCameraSource.CellUiCamera, alive: false, cullingMask: 1 << IconLayer, depth: -1, targetTexture: true)
        };
        AnchorCameraChoice choice = AnchorCameraLadder.Select(ladderOnly, IconLayer, AnchorCanvasMode.ScreenSpaceCamera,
            canvasDrawn: false, hasRectTransform: true, out _);
        check(choice.Projection == AnchorProjection.Rect,
            "and the ladder still refuses the switched-off RenderTexture camera itself, granting only the geometry rule - the manual canvas never relaxes that");

        // The word the log prints is still an *answer* only when a rectangle came out of it, and `config` otherwise.
        check(AnchorCameraChoice.Provenance(AnchorProjection.Rect, measured: true) == "canvas-geometry"
              && AnchorCameraChoice.Provenance(AnchorProjection.Rect, measured: false) == "config",
            "so a manual-canvas projection that answers reads `anchorSource=canvas-geometry`, and one that does not still reads `config`");

        check(AnchorElementCanvasPolicy.SourceName(ancestorCanvasPresent: false) == "layer-property",
            "and the render mode is still reported as the layer's property on this HUD, because it is");

        // A camera that is switched on and one that is switched off are the same answer here, and that is the whole point: the
        // recorded camera is `enabled=no`, and it is the HUD's only compositor.
        check(AnchorManualCanvasPolicy.Describe(Recorded(cameraEnabled: false)) == AnchorManualCanvasPolicy.Describe(Recorded(cameraEnabled: true)).Replace("enabled=yes", "enabled=no"),
            "`enabled` is printed and is never an input: the switched-off recorded camera verifies exactly like a switched-on one");
        check(AnchorManualCanvasPolicy.Describe(recorded).Contains("origin=manual-canvas", StringComparison.Ordinal)
              && AnchorManualCanvasPolicy.Describe(recorded).Contains("targetMatchesWindow=yes", StringComparison.Ordinal)
              && AnchorManualCanvasPolicy.Describe(recorded).Contains("canvasPixelsMatchWindow=yes", StringComparison.Ordinal),
            "and the line says so in its own words: " + AnchorManualCanvasPolicy.Describe(recorded));
    }

    // ---- 1b. 1.19.35: a layer enum may not veto the manual canvas, and `not-observed` is not `absent` ----------------------------

    // The recorded 2026-11-13 Test run loaded a build without this round's manual-canvas authority at all (the installed DLL,
    // SHA256 977F6C71...60E6C, contains neither `AnchorManualCanvasFacts` nor the string `manual-canvas`), so the in-game line
    // `authorization=canvas-geometry(refused)` is *not* evidence about the code below - it is evidence that the code below was never
    // in the game. What can be asserted is the decision itself.
    private static void TheLadderRefusalCannotVetoAVerifiedManualCanvas(Action<bool, string> check)
    {
        var ladderOnly = new List<AnchorCameraCandidate>
        {
            new AnchorCameraCandidate(AnchorCameraSource.CellUiCamera, alive: false, cullingMask: 1 << IconLayer, depth: -1, targetTexture: true)
        };
        // The defect: with no ancestor canvas the ladder's last word is decided by the *layer's* render-mode property, and `Unknown`
        // (an unreadable enum) or `WorldSpace` (a mode this HUD never asked for) refuses the geometry rule before the manual canvas is
        // consulted - even though every fact of that chain holds.
        foreach (AnchorCanvasMode mode in new[] { AnchorCanvasMode.Unknown, AnchorCanvasMode.WorldSpace })
        {
            AnchorCameraChoice refused = AnchorCameraLadder.Select(ladderOnly, IconLayer, mode, canvasDrawn: false,
                hasRectTransform: true, out _);
            check(!refused.Usable,
                mode + ": the ladder really does answer no projection on that layer property, which is the dead end this round has to get past");
            check(AnchorElementCanvasPolicy.ManualAuthorized(elementCanvasPresent: false, Recorded()),
                mode + ": and a verified manual chain is allowed to authorize the attempt anyway, because it is a stronger fact than the enum");
        }

        // The attempt itself: the geometry rule, authorized by the manual canvas, may produce a rectangle.
        AnchorElementCanvasPolicy.CameraOrigin origin = AnchorElementCanvasPolicy.Project(
            elementCanvasPresent: false, AnchorCanvasMode.WorldSpace,
            canvasCameraPresent: false, canvasCameraAlive: false, canvasCameraWindow: false, manual: Recorded());
        check(origin == AnchorElementCanvasPolicy.CameraOrigin.ManualCanvas
              && AnchorElementCanvasPolicy.PassesCamera(origin) && AnchorElementCanvasPolicy.CanProject(origin),
            "so an element whose only canvas fact is a layer property is projected by the verified manual canvas, with its camera passed");

        // Nothing else is widened. A real ancestor canvas keeps its own rule; a chain that is not verified authorizes nothing; and an
        // unreadable chain (the default struct) is a refusal, not a zero.
        check(!AnchorElementCanvasPolicy.ManualAuthorized(elementCanvasPresent: true, Recorded()),
            "an element under a real ancestor Canvas is never handed to the manual canvas - its own canvas decides");
        var noCanvas = new AnchorManualCanvasFacts(false, true, true, IconLayer, true, 1 << IconLayer, 2560, 1440, 2560f, 1440f, 2560, 1440, 0f, 0f, 1f, 1f);
        var wrongTarget = new AnchorManualCanvasFacts(true, true, true, IconLayer, true, 1 << IconLayer, 1920, 1080, 2560f, 1440f, 2560, 1440, 0f, 0f, 1f, 1f);
        check(!AnchorElementCanvasPolicy.ManualAuthorized(false, noCanvas)
              && !AnchorElementCanvasPolicy.ManualAuthorized(false, wrongTarget)
              && !AnchorElementCanvasPolicy.ManualAuthorized(false, AnchorManualCanvasFacts.None),
            "an absent, mismatched or unreadable manual chain authorizes no attempt at all: the override cannot be talked into a projection");

        // The word the log prints is still an answer only when a rectangle came out of it.
        AnchorCameraChoice attempt = AnchorCameraChoice.ManualCanvas();
        check(attempt.Usable && attempt.Projection == AnchorProjection.Rect
              && AnchorCameraChoice.Provenance(attempt.Projection, measured: false) == "config"
              && AnchorCameraChoice.Authorization(attempt.Projection, measured: false) == "canvas-geometry(refused)",
            "the manual-canvas attempt is the geometry rule: it measures nothing until a rectangle passes the gate, so an attempt is never printed as an answer");

        // 1.19.35 (A3): three answers, not two. A missing renderer never reads the chain, and that is not a HUD without one.
        check(AnchorManualCanvasPolicy.Field(observed: false, Recorded()) == "not-observed",
            "a chain that was never read prints `not-observed`, even though the facts a previous element read would have verified");
        check(AnchorManualCanvasPolicy.Field(observed: true, AnchorManualCanvasFacts.None) == "absent",
            "`absent` stays what it was: the HUD really has no `m_cellUICanvas`");
        check(AnchorManualCanvasPolicy.Field(observed: true, Recorded()).StartsWith("verified=yes", StringComparison.Ordinal)
              && AnchorManualCanvasPolicy.Field(observed: true, Recorded()).Contains("target=2560x1440", StringComparison.Ordinal),
            "and a read chain prints every fact it was read from, including the camera's own render target: " + AnchorManualCanvasPolicy.Field(true, Recorded()));
        check(AnchorManualCanvasPolicy.Field(false, AnchorManualCanvasFacts.None) == "not-observed",
            "an unread chain is `not-observed` and not `absent` even when nothing else is known about it");
    }

    // ---- 2. one missing fact refuses, and names itself ----------------------------------------------------------------------

    private static void TheManualCanvasDecidesOnlyWhenEveryFactHolds(Action<bool, string> check)
    {
        // A HUD that names no manual canvas at all is not a "refused chain": there is nothing to ask, so the answer is the one this
        // build gave before this round (`None`), and the refusal text still says which object was missing.
        var noCanvasFacts = new AnchorManualCanvasFacts(false, true, true, IconLayer, true, 1 << IconLayer, 2560, 1440, 2560f, 1440f, 2560, 1440, 0f, 0f, 1f, 1f);
        check(AnchorElementCanvasPolicy.Project(false, AnchorCanvasMode.ScreenSpaceCamera, false, false, false, noCanvasFacts)
                  == AnchorElementCanvasPolicy.CameraOrigin.None
              && AnchorManualCanvasPolicy.Refusal(noCanvasFacts).Contains("m_cellUICanvas is absent", StringComparison.Ordinal),
            "a HUD whose `m_cellUICanvas` is missing authorizes nothing - the pre-existing answer - and names that object");

        var cases = new (string Name, AnchorManualCanvasFacts Facts, string Word)[]
        {
            ("no camera", new AnchorManualCanvasFacts(true, false, true, IconLayer, true, 0, 0, 0, 2560f, 1440f, 2560, 1440, float.NaN, float.NaN, float.NaN, float.NaN), "names no m_UICamera"),
            ("not inside the canvas", new AnchorManualCanvasFacts(true, true, false, IconLayer, true, 1 << IconLayer, 2560, 1440, 2560f, 1440f, 2560, 1440, 0f, 0f, 1f, 1f), "not a descendant"),
            ("the camera does not draw the layer", new AnchorManualCanvasFacts(true, true, true, IconLayer, false, 1 << 4, 2560, 1440, 2560f, 1440f, 2560, 1440, 0f, 0f, 1f, 1f), "does not draw the element's layer"),
            ("a partial viewport", new AnchorManualCanvasFacts(true, true, true, IconLayer, true, 1 << IconLayer, 2560, 1440, 2560f, 1440f, 2560, 1440, 0f, 0.5f, 1f, 0.5f), "part of its texture"),
            ("a texture that is not the window", new AnchorManualCanvasFacts(true, true, true, IconLayer, true, 1 << IconLayer, 1920, 1080, 2560f, 1440f, 2560, 1440, 0f, 0f, 1f, 1f), "1920x1080 texture while the window is 2560x1440"),
            ("a canvas laid out for another size", new AnchorManualCanvasFacts(true, true, true, IconLayer, true, 1 << IconLayer, 2560, 1440, 1920f, 1080f, 2560, 1440, 0f, 0f, 1f, 1f), "laid out for")
        };
        foreach ((string name, AnchorManualCanvasFacts facts, string word) in cases)
        {
            string refusal = AnchorManualCanvasPolicy.Refusal(facts);
            AnchorElementCanvasPolicy.CameraOrigin origin = AnchorElementCanvasPolicy.Project(
                elementCanvasPresent: false, AnchorCanvasMode.ScreenSpaceCamera,
                canvasCameraPresent: false, canvasCameraAlive: false, canvasCameraWindow: false, manual: facts);
            check(!facts.Verified && origin == AnchorElementCanvasPolicy.CameraOrigin.ManualCanvasRefused
                  && !AnchorElementCanvasPolicy.PassesCamera(origin)
                  && !AnchorElementCanvasPolicy.CanProject(origin)
                  && refusal.Contains(word, StringComparison.Ordinal),
                name + " refuses, passes no camera and names the fact that was missing: " + refusal);
        }

        check(AnchorManualCanvasPolicy.Refusal(AnchorManualCanvasFacts.None).Contains("m_cellUICanvas is absent", StringComparison.Ordinal),
            "the `no manual canvas at all` fact is the first thing it says, so a HUD without one is not blamed on the camera");

        // A hierarchy with no manual canvas is the pre-existing behaviour, unchanged: `None`, and no camera invented for it.
        check(AnchorElementCanvasPolicy.Project(false, AnchorCanvasMode.Overlay, true, true, true) == AnchorElementCanvasPolicy.CameraOrigin.None
              && AnchorElementCanvasPolicy.Project(false, AnchorCanvasMode.ScreenSpaceCamera, true, true, true, AnchorManualCanvasFacts.None) == AnchorElementCanvasPolicy.CameraOrigin.None,
            "an element with no ancestor Canvas *and* no manual canvas is still `none`: nothing is authorized for it, before this round and after it");

        // An unreadable chain is refused rather than assumed: NaN viewport and 0-pixel texture are the two shapes a throwing read leaves.
        check(!new AnchorManualCanvasFacts(true, true, true, IconLayer, true, 1 << IconLayer, 0, 0, float.NaN, float.NaN, 2560, 1440, float.NaN, float.NaN, float.NaN, float.NaN).Verified,
            "a chain whose numbers could not be read is not verified, so an unreadable HUD keeps the configured position instead of a guess");
    }

    // ---- 3. a real canvas keeps its own authority ----------------------------------------------------------------------------

    private static void ARealCanvasKeepsItsOwnAuthority(Action<bool, string> check)
    {
        // An element *under* a real `Canvas` is composited by that canvas, and a manual canvas may never stand in for its dead camera:
        // that would project an element through an object that does not own it.
        AnchorElementCanvasPolicy.CameraOrigin underCanvas = AnchorElementCanvasPolicy.Project(
            elementCanvasPresent: true, AnchorCanvasMode.ScreenSpaceCamera,
            canvasCameraPresent: true, canvasCameraAlive: false, canvasCameraWindow: true, manual: Recorded());
        check(underCanvas == AnchorElementCanvasPolicy.CameraOrigin.ElementCanvasRefused
              && !AnchorElementCanvasPolicy.CanProject(underCanvas),
            "an element under a real screen-space-camera canvas whose own camera is dead keeps that refusal - the manual canvas cannot substitute itself");

        AnchorElementCanvasPolicy.CameraOrigin overlay = AnchorElementCanvasPolicy.Project(
            elementCanvasPresent: true, AnchorCanvasMode.Overlay,
            canvasCameraPresent: true, canvasCameraAlive: true, canvasCameraWindow: true, manual: Recorded());
        check(overlay == AnchorElementCanvasPolicy.CameraOrigin.Overlay
              && !AnchorElementCanvasPolicy.PassesCamera(overlay)
              && AnchorElementCanvasPolicy.CanProject(overlay),
            "and an overlay canvas still passes NO camera, even with a verified manual canvas beside it: window pixels are never projected twice");

        AnchorElementCanvasPolicy.CameraOrigin world = AnchorElementCanvasPolicy.Project(
            elementCanvasPresent: true, AnchorCanvasMode.WorldSpace,
            canvasCameraPresent: true, canvasCameraAlive: true, canvasCameraWindow: true, manual: Recorded());
        check(world == AnchorElementCanvasPolicy.CameraOrigin.None,
            "a world-space canvas keeps its refusal too: this round adds one authority, it does not weaken the others");
    }

    // ---- 4. the rectangle this will produce ---------------------------------------------------------------------------------

    // The recorded level's own numbers, reduced the way `PanelBehaviour` reduces them once the manual canvas' camera has projected the
    // four corners. `position=(1266.67,425.67)` is the icon row's pivot (its right/top edge) and the window y is mirrored at the end, so
    // the rectangle is x 1056.08..1266.67, y 1014.33..1124.49 of a 2560x1440 window - a real window rectangle, printed here so the next
    // real run can be compared against it instead of guessed at.
    private static void TheRectangleItWillProduce(Action<bool, string> check)
    {
        check(AnchorRectProjection.TryRectFromPivot(1f, 1f, 210.59f, 110.16f, out float minX, out float maxX, out float minY, out float maxY),
            "the icon row's declared rectangle is a rectangle");

        var xs = new List<float>(4);
        var ys = new List<float>(4);
        for (int corner = 0; corner < 4; corner++)
        {
            float localX = (corner & 1) == 0 ? minX : maxX;
            float localY = (corner & 2) == 0 ? minY : maxY;
            xs.Add(1266.67f + localX);
            ys.Add(1440f - (425.67f + localY));
        }
        bool usable = AnchorRectProjection.TryFromCorners(xs, ys, 2560f, 1440f, out LayoutRect rect, out AnchorScreenRect raw);
        check(usable && rect.Width > 210f && rect.Width < 211f && rect.Height > 110f && rect.Height < 111f,
            "the recorded icon row lands as a 210.59 x 110.16 window rectangle instead of being refused: " + raw.Describe());
        check(usable && Math.Abs(rect.Right - 1266.67f) < 0.5f && Math.Abs(rect.Bottom - 1124.49f) < 0.5f,
            "with its right edge on the row's own pivot and its bottom on the row's top edge in window pixels: " + rect.X.ToString("0.##") + "," + rect.Y.ToString("0.##"));

        // The row's `Background Fade` is the fallback, and it is a different rectangle: 66.65 local units to the left and 6.45 above the
        // icon row, which the same projection produces when the icon row is unmeasurable.
        check(AnchorRectProjection.TryRectFromPivot(1f, 0.5f, 250f, 30f, out float bMinX, out float bMaxX, out float bMinY, out float bMaxY),
            "the row's Background Fade quad is a rectangle too");
        var bx = new List<float>(4);
        var by = new List<float>(4);
        for (int corner = 0; corner < 4; corner++)
        {
            bx.Add(1196.67f + ((corner & 1) == 0 ? bMinX : bMaxX));
            by.Add(1440f - (410.67f + ((corner & 2) == 0 ? bMinY : bMaxY)));
        }
        check(AnchorRectProjection.TryFromCorners(bx, by, 2560f, 1440f, out LayoutRect fallback, out _)
              && fallback.Width > 249f && fallback.Width < 251f && fallback.Height > 29f && fallback.Height < 31f,
            "and it is 250 x 30 window pixels, the sliced gradient it really draws, not its 300 x 109.92 layout rectangle");

        // The gate is still the gate: the same camera pointed at an off-window element refuses.
        check(!AnchorRectProjection.TryFromCorners(new[] { 4000f, 4100f, 4000f, 4100f }, new[] { 100f, 100f, 200f, 200f }, 2560f, 1440f, out _, out _),
            "a manual-canvas rectangle that lands off the window is refused by `ScreenBounds`, exactly like every other measurement");
    }

    // ---- 5. a camera is its native pointer ----------------------------------------------------------------------------------

    private static void ACameraIsItsNativePointer(Action<bool, string> check)
    {
        check(!AnchorCameraIdentity.Same(0L, 0L) && AnchorCameraIdentity.Same(0x1234L, 0x1234L)
              && !AnchorCameraIdentity.Same(0x1234L, 0x5678L),
            "two wrappers are the same camera when their native pointers are equal, and two unreadable pointers are never the same camera");

        // The recorded defect: the proof compared wrappers, so a camera that *was* offered read as not offered. The camera at index 0
        // is the same native object the panel read from `m_UICamera`, which is now what the lookup asks.
        var candidates = new List<AnchorCameraCandidate>
        {
            new AnchorCameraCandidate(AnchorCameraSource.CellUiCamera, alive: false, cullingMask: 1 << IconLayer, depth: -1, targetTexture: true),
            new AnchorCameraCandidate(AnchorCameraSource.MainCamera, alive: true, cullingMask: 0x464FDF17, depth: 0)
        };
        var pointers = new long[] { 0x2A00000000L, 0x2B00000000L };
        check(AnchorCameraIdentity.IndexOf(candidates, pointers, AnchorCameraSource.CellUiCamera, 0x2A00000000L) == 0,
            "the CellUI camera that was offered is found by its native pointer, so `offered=yes,mask=0x00000020,texture=yes` is what the line can say");
        check(AnchorCameraIdentity.IndexOf(candidates, pointers, AnchorCameraSource.CellUiCamera, 0x2A00000000L) == 0
              && AnchorCameraIdentity.IndexOf(candidates, new long[] { 0x2B00000000L, 0x2A00000000L }, AnchorCameraSource.CellUiCamera, 0x2A00000000L) == 1,
            "and it is found at whatever index it really sits at, instead of at a guessed one");

        check(AnchorCameraIdentity.IndexOf(candidates, pointers, AnchorCameraSource.CellUiCamera, 0L) == 0,
            "an unreadable pointer falls back to the candidate's own source - the ladder offers exactly one CellUI camera");
        check(AnchorCameraIdentity.IndexOf(candidates, pointers, AnchorCameraSource.None, 0L) == -1
              && AnchorCameraIdentity.IndexOf(null, pointers, AnchorCameraSource.CellUiCamera, 0L) == -1,
            "and nothing at all is found when neither a pointer nor a source names one");
        check(AnchorCameraIdentity.IndexOf(candidates, pointers, AnchorCameraSource.CellUiCamera, 0x2C00000000L) == -1,
            "a *known* pointer that matches no candidate is not offered, whatever source a candidate carries: a name never overrides an identity");
        check(AnchorCameraIdentity.IndexOf(candidates, new long[] { 0x2A00000000L }, AnchorCameraSource.CellUiCamera, 0x2B00000000L) == -1
              && AnchorCameraIdentity.IndexOf(candidates, new long[] { 0L, 0L }, AnchorCameraSource.CellUiCamera, 0x2A00000000L) == -1,
            "and an unreadable candidate list cannot make one either");
    }

    // ---- 6. wiring, read from the production source --------------------------------------------------------------------------

    private static void Wiring(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the 1.19.32 anchor regression locates the production sources");
        if (root == null) return;

        string panel = Code(File.ReadAllText(Path.Combine(root, "PanelBehaviour.cs")));
        check(!panel.Contains("ReferenceEquals(cameras[index], cellUi)", StringComparison.Ordinal),
            "the CellUI proof no longer compares two Il2Cpp wrappers with `ReferenceEquals` - the comparison that printed `offered=no` beside `each=#0:cellui`");
        check(panel.Contains("AnchorCameraIdentity.IndexOf(candidates, PointersOf(cameras), AnchorCameraSource.CellUiCamera, pointer)", StringComparison.Ordinal),
            "it asks the native pointer instead, through the production policy");
        check(!panel.Contains("cameras.Contains(camera)", StringComparison.Ordinal)
              && panel.Contains("AnchorCameraIdentity.Same(PointerOf(cameras[index]), pointer)", StringComparison.Ordinal),
            "and the candidate de-duplication is by native pointer too, so one camera cannot be offered twice as `#1:main` and `#2:all`");

        check(panel.Contains("CellUiRead cellUi = CellUiRead.Read(layer);", StringComparison.Ordinal)
              && panel.Contains("try { cellUiCamera = canvas.m_UICamera; } catch { cellUiCamera = null; }", StringComparison.Ordinal)
              && panel.Contains("try { root = canvas.m_rectTransform; } catch { root = null; }", StringComparison.Ordinal)
              && panel.Contains("try { pixelWidth = canvas.m_pixelWidth; pixelHeight = canvas.m_pixelHeight; }", StringComparison.Ordinal),
            "the manual canvas is read once per attempt: its camera, its root and its declared pixel size");
        check(panel.Contains("try { inside = element != null && Root != null && element.IsChildOf(Root); }", StringComparison.Ordinal)
              && panel.Contains("bool draws = Camera != null && AnchorCameraLadder.Renders(Mask, layer);", StringComparison.Ordinal),
            "and each element is asked its own two questions - does it live under that root, and does that camera draw its layer");
        check(panel.Contains("Rect viewport = cellUiCamera.rect;", StringComparison.Ordinal)
              && panel.Contains("cameraPixelsWide = cellUiCamera.pixelWidth;", StringComparison.Ordinal)
              && panel.Contains("cameraPixelsHigh = cellUiCamera.pixelHeight;", StringComparison.Ordinal),
            "with the viewport and the render texture's own pixel size, which are what make those pixels this window's");

        check(panel.Contains("AnchorManualCanvasFacts manual = cellUi.Facts(element, layer);", StringComparison.Ordinal)
              && panel.Contains("CameraWindowCamera(canvasCamera), manual)", StringComparison.Ordinal)
              && panel.Contains("if (origin == AnchorElementCanvasPolicy.CameraOrigin.ManualCanvas) projection = cellUi.Camera;", StringComparison.Ordinal),
            "the sixth authorization input reaches the single place a projection camera is chosen, and the manual canvas' camera is the camera used");
        check(panel.Contains("AnchorElementCanvasPolicy.PassesCamera(origin) ? canvasCamera : null", StringComparison.Ordinal),
            "while the canvas rule still passes only `Canvas.worldCamera`: the two authorities are never mixed into one argument");
        check(panel.Contains("AnchorManualCanvasPolicy.Refusal(manual)", StringComparison.Ordinal)
              && panel.Contains("{ManualCanvasDiagnostic(AnchorElementNames.Flashlight, icon.Manual, icon.ManualObserved)}", StringComparison.Ordinal)
              && panel.Contains("{ManualCanvasDiagnostic(AnchorElementNames.BackgroundFade, row.Manual, row.ManualObserved)}", StringComparison.Ordinal),
            "a refused chain names the one fact that was missing, and both elements' chains are printed once per round");
        // 1.19.35: the ladder's refusal may be overridden only by a *verified* manual chain, and the line prints three answers.
        check(panel.Contains("if (!choice.Usable && AnchorElementCanvasPolicy.ManualAuthorized(elementCanvas != null, manual))", StringComparison.Ordinal)
              && panel.Contains("choice = AnchorCameraChoice.ManualCanvas();", StringComparison.Ordinal),
            "the manual canvas is consulted *before* the ladder's no-projection early return, so the layer enum can no longer veto it");
        int manualCheck = panel.IndexOf("AnchorElementCanvasPolicy.ManualAuthorized(elementCanvas != null, manual)", StringComparison.Ordinal);
        int noProjection = panel.IndexOf("AnchorSampleFailure.NoProjection", StringComparison.Ordinal);
        check(manualCheck >= 0 && noProjection > manualCheck,
            "and it is consulted before that return, not after it: " + manualCheck + " < " + noProjection);
        check(panel.Contains("=> element + \"ManualCanvas=\" + AnchorManualCanvasPolicy.Field(observed, facts);", StringComparison.Ordinal)
              && panel.Contains("manualObserved: true", StringComparison.Ordinal),
            "one word formatter decides `not-observed` / `absent` / the facts, and the measurement records whether the chain was read at all");
        check(panel.Contains("if (camera != null) target = camera.pixelWidth + \"x\" + camera.pixelHeight;", StringComparison.Ordinal)
              && panel.Contains("\",cellUiCameraPixel=\" + target", StringComparison.Ordinal),
            "and the camera's own render target is printed beside the canvas' declared pixel grid, which is what a real run has to be compared against");
        check(panel.Contains("if (camera == null) return RectTransformUtility.WorldToScreenPoint(null, world);", StringComparison.Ordinal)
              && panel.Contains("Vector2 screen = ProjectPoint(camera, world);", StringComparison.Ordinal),
            "and the corner projection is the camera's own `WorldToScreenPoint` whenever there is one - a camera-less identity is passed only for a real overlay canvas");

        string ladder = File.ReadAllText(Path.Combine(root, "AnchorCameraLadder.cs"));
        check(ladder.Contains("public readonly struct AnchorManualCanvasFacts", StringComparison.Ordinal)
              && ladder.Contains("public static class AnchorManualCanvasPolicy", StringComparison.Ordinal)
              && ladder.Contains("public static class AnchorCameraIdentity", StringComparison.Ordinal),
            "the three decisions live in the production policy, Unity-free, so the cases above are the runtime's own");
        check(ladder.Contains("ManualCanvas = 4,", StringComparison.Ordinal) && ladder.Contains("ManualCanvasRefused = 5", StringComparison.Ordinal)
              && ladder.Contains("if (manual.CanvasPresent) return manual.Verified ? CameraOrigin.ManualCanvas : CameraOrigin.ManualCanvasRefused;", StringComparison.Ordinal),
            "with their own origin words, so a log can tell a manual-canvas projection from a canvas one and from a refusal");
        check(ladder.Contains("if (elementCanvasPresent)", StringComparison.Ordinal)
              && ladder.Contains("bool canvasCameraPresent, bool canvasCameraAlive, bool canvasCameraWindow)", StringComparison.Ordinal)
              && ladder.Contains("return manual.Verified ? CameraOrigin.ManualCanvas", StringComparison.Ordinal),
            "and a real ancestor canvas is asked first - the manual canvas is only reached where there is no `Canvas` at all");

        Console.WriteLine("WIRING: RELEASE_11932_MANUAL_CANVAS_PROJECTION (static source check; the chain, its refusals, the recorded rectangle and the camera identity are the assertions above)");
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------------

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
}
