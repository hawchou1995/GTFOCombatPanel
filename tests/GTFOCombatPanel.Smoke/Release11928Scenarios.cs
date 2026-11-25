using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// 1.19.28: the flashlight anchor, fixed on three pieces of evidence instead of on a guess.
//
//   1. `background-fade=missing` was this build's own constant. `PanelBehaviour` compared the walked object's name against
//      `AnchorElementNames.BackgroundFade` - the *log token* "background-fade" - while the prefab object is
//      `AnchorElementNames.BackgroundFadeObject`, "Background Fade". Nothing threw, so the failed-lookup line never printed and
//      the fallback source could never be measured. The suite that was supposed to catch it re-implemented the walk with the
//      right constant, so it was green for four rounds while every real level reported `missing`.
//   2. The camera ladder offered `GetWorldCamera()`, `m_layerCanvas.worldCamera` and the ancestor canvas of the icon - three
//      cameras of which none is this build's HUD camera. That object is the CellUI canvas: `GuiLayerBase.m_cellUICanvas :
//      UI_Canvas`, whose `m_UICamera : Camera` draws the HUD. `m_layerCanvas` is null for its whole life, and "no `Canvas` in the
//      prefab's hierarchy" plus "canvasRenderMode=screen-space-camera" are therefore not contradictory: the mode comes from
//      `GuiLayerBase.CanvasRenderMode`, an enum on the class.
//   3. The canvas-geometry answer projected the element through a *null* camera, which is only the identity inside a real
//      screen-space overlay canvas. This build has none, so the rectangle 1.19.27 computed was never a window rectangle, and the
//      geometry that was measured was the icon's 65 px `SpriteRenderer` (32.5 world units at 2 pixels per unit), not the icon
//      row's 210.59 x 110.16 layout rectangle.
//
// What is asserted below: the constant (through the production predicate, not a copy of the walk), the CellUI camera candidate and
// the culling-mask proof it still has to pass, the four-corner projection with hand-checkable numbers at two window sizes, the
// refusal of a null camera outside a real overlay canvas, the honest reading of the two flashlight sprites, and the bounded log.
internal static class Release11928Scenarios
{
    private const int IconLayer = 5;

    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        TheConstantIsThePrefabsNameNotTheLogToken(check);
        TheCellUiCanvasCameraIsACandidateAndStillProven(check);
        TheElementLayoutRectangleIsProjectedByARealCamera(check);
        ANullCameraIsOnlyTheIdentityInsideARealOverlayCanvas(check);
        TheFlashlightSpritesAreNamedForWhatTheyAre(check);
        TheLogStaysBounded(check);
        Wiring(check);
    }

    // ---- 1. the constant ------------------------------------------------------------------------------------------

    // The walk asks production code; production code compares against the prefab's name. Both halves are asserted, and the second
    // one is the one that can go red: `AnchorElementMatch.IsBackgroundFade` is the single place the constant appears, so a build
    // that swaps it back to the log token fails here on the very next run instead of after four expeditions.
    private static void TheConstantIsThePrefabsNameNotTheLogToken(Action<bool, string> check)
    {
        check(AnchorElementNames.BackgroundFade == "background-fade",
            "the token `AnchorElementNames.BackgroundFade` is still the word the [DIAG] lines print");
        check(AnchorElementNames.BackgroundFadeObject == "Background Fade",
            "and `AnchorElementNames.BackgroundFadeObject` is still the prefab object's own name");

        check(AnchorElementMatch.IsBackgroundFade(AnchorElementNames.BackgroundFadeObject),
            "the production predicate matches the prefab's name - the object the walk really returns");
        check(!AnchorElementMatch.IsBackgroundFade(AnchorElementNames.BackgroundFade),
            "and does not match the log token: that one comparison was the whole recorded `background-fade=missing`");
        check(!AnchorElementMatch.IsBackgroundFade("background fade")
              && !AnchorElementMatch.IsBackgroundFade("Background Fade ")
              && !AnchorElementMatch.IsBackgroundFade("Background Fade (1)")
              && !AnchorElementMatch.IsBackgroundFade(null),
            "the match stays exact and ordinal - no case folding, no prefix, no trailing space, no null match");

        // The prefab's hierarchy as `GetComponentsInChildren<RectTransform>(true)` hands it back, with the recorded active state
        // in it: the row's own object is switched off, and a walk of this subtree must select *it*.
        var walked = new[]
        {
            new WalkedObject("FlashLightIcon_:Enabled", true),
            new WalkedObject("FlashLightIcon_:Disabled", true),
            new WalkedObject("Background Small", true),
            new WalkedObject(AnchorElementNames.BackgroundFadeObject, false),
            new WalkedObject("MeleeStamina", false)
        };
        int hit = Walk(walked, includeInactive: true);
        check(hit == 3 && walked[hit].Name == AnchorElementNames.BackgroundFadeObject && !walked[hit].ActiveInHierarchy,
            "an inactive child named \"Background Fade\" is selected by the production predicate - the object the prefab switches off is the one this build falls back to");
        check(Walk(new[] { new WalkedObject(AnchorElementNames.BackgroundFade, false) }, includeInactive: true) < 0,
            "while an object named with the log token matches nothing, which is what the recorded levels were looking for");

        // The panel must not spell the comparison itself: one predicate, one constant.
        string panel = Code(Source("PanelBehaviour.cs"));
        string walk = Between(panel, "    private static RectTransform BackgroundFade(PlayerGuiLayer layer)",
            "    private static Renderer BackgroundFadeRenderer(PlayerGuiLayer layer)");
        check(walk.Length > 0 && walk.Contains("if (!AnchorElementMatch.IsBackgroundFade(candidate.name)) continue;"),
            "the production walk compares through `AnchorElementMatch.IsBackgroundFade` and nothing else");
        check(!walk.Contains("AnchorElementNames.BackgroundFade") && !walk.Contains("AnchorElementNames.BackgroundFadeObject"),
            "and the walk names no constant of its own - the comparison, and therefore the constant, lives once, in the predicate");
        check(Source("AnchorCameraLadder.cs").Contains("=> string.Equals(objectName, AnchorElementNames.BackgroundFadeObject, StringComparison.Ordinal);"),
            "which compares against `BackgroundFadeObject` with an ordinal comparison, in production code the suite drives directly");
    }

    // ---- 2. the CellUI camera -------------------------------------------------------------------------------------

    // The candidate that can actually exist on this build. It is offered like every other camera and is *proven* like every other
    // camera: a mask that does not contain the element's layer is still refused, so nothing was relaxed to make this work.
    private static void TheCellUiCanvasCameraIsACandidateAndStillProven(Action<bool, string> check)
    {
        string panel = Code(Source("PanelBehaviour.cs"));
        check(panel.Contains("layer.GuiLayerBase.m_cellUICanvas.m_UICamera"),
            "the CellUI canvas' own camera - `GuiLayerBase.m_cellUICanvas : UI_Canvas` -> `m_UICamera : Camera` - is read in the panel");
        check(panel.Contains("Add(cameras, candidates, AnchorCameraSource.CellUiCamera, cellUi);"),
            "and is offered as its own candidate source, so a log line can say that this camera answered");
        check(panel.Contains("AnchorCameraSource.CellUiCamera"),
            "through `AnchorCameraSource.CellUiCamera`");
        check(AnchorCameraChoice.Name(AnchorCameraSource.CellUiCamera).Contains("CellUI"),
            "whose name for the log says CellUI: " + AnchorCameraChoice.Name(AnchorCameraSource.CellUiCamera));
        // 1.19.31 re-point: the geometry answer no longer picks a camera out of the ladder (`GeometryCamera`/`CameraOf` are gone - that
        // is exactly the "some camera the HUD offered" substitution this round removes). The CellUI camera stays a *candidate* for the
        // culling-mask projection, which is what this file is about; the geometry camera comes from the element's own canvas.
        check(!Code(panel).Contains("CameraOf(") && !Code(panel).Contains("GeometryCamera"),
            "the geometry answer no longer borrows a camera from the ladder: there is no `GeometryCamera`/`CameraOf` left to choose one");

        // The proof itself, driven: the CellUI camera with a mask that misses layer 5 is refused exactly like any other camera.
        var blind = new List<AnchorCameraCandidate> { new AnchorCameraCandidate(AnchorCameraSource.CellUiCamera, true, 1 << 3, 0) };
        AnchorCameraChoice refused = AnchorCameraLadder.Select(blind, IconLayer, AnchorCanvasMode.ScreenSpaceCamera,
            canvasDrawn: false, hasRectTransform: true, out _);
        check(refused.Projection == AnchorProjection.Rect,
            "a CellUI camera whose culling mask does not contain layer 5 is still refused as a *camera* - the geometry answer is then the only one left");
        check(!AnchorCameraLadder.Select(blind, IconLayer, AnchorCanvasMode.ScreenSpaceCamera, canvasDrawn: false, hasRectTransform: false, out _).Usable,
            "and with no RectTransform either there is no answer at all, so the mask proof was not relaxed to let this camera through");

        var sees = new List<AnchorCameraCandidate> { new AnchorCameraCandidate(AnchorCameraSource.CellUiCamera, true, 1 << IconLayer, 0) };
        AnchorCameraChoice accepted = AnchorCameraLadder.Select(sees, IconLayer, AnchorCanvasMode.ScreenSpaceCamera, canvasDrawn: false, out _);
        check(accepted.Usable && accepted.Projection == AnchorProjection.Camera && accepted.Source == AnchorCameraSource.CellUiCamera,
            "a CellUI camera whose mask really contains layer 5 wins as a proven camera projection, like any other");

        var dead = new List<AnchorCameraCandidate> { new AnchorCameraCandidate(AnchorCameraSource.CellUiCamera, false, 1 << IconLayer, 0) };
        check(AnchorCameraLadder.Select(dead, IconLayer, AnchorCanvasMode.ScreenSpaceCamera, canvasDrawn: false, out _).Projection != AnchorProjection.Camera,
            "and a CellUI camera that is not alive is never used, whatever its mask says");

        check(panel.Contains("cellUiCanvas=") && panel.Contains("cellUiCamera=") && panel.Contains("cellUiMaskProof="),
            "the diagnostic line names the cellUI canvas, its camera and whether that camera passed the mask proof");
    }

    // ---- 3. the rectangle -----------------------------------------------------------------------------------------

    // The element's own `RectTransform`, projected by a real camera, four corners, window pixels. Numbers that can be worked out by
    // hand, at the two window sizes this build really runs at, with a pivot that is not 0.5 and a scale that is not 1.
    private static void TheElementLayoutRectangleIsProjectedByARealCamera(Action<bool, string> check)
    {
        // `RectTransform.rect` as Unity reports it for a declared size and pivot: already relative to that pivot.
        check(AnchorRectProjection.TryRectFromPivot(1f, 0.5f, 300f, 109.92f, out float minX, out float maxX, out float minY, out float maxY)
              && Near(minX, -300f) && Near(maxX, 0f) && Near(minY, -54.96f) && Near(maxY, 54.96f),
            "a pivot of 1,0.5 on a 300 x 109.92 element reports xMin=-300, xMax=0, yMin=-54.96, yMax=54.96 - the shape the prefab's Background Fade really has");
        check(AnchorRectProjection.TryRectFromPivot(0f, 1f, 100f, 100f, out float left, out float right, out float bottom, out float top)
              && Near(left, 0f) && Near(right, 100f) && Near(bottom, -100f) && Near(top, 0f),
            "and a pivot of 0,1 puts the whole element to the right of and below its own origin, which is what Unity does");
        check(!AnchorRectProjection.TryRectFromPivot(0.5f, 0.5f, 0f, 10f, out _, out _, out _, out _)
              && !AnchorRectProjection.TryRectFromPivot(0.5f, float.NaN, 10f, 10f, out _, out _, out _, out _),
            "a zero or unreadable size is not a rectangle at all");

        // The icon row's own rectangle (210.59 x 110.16, pivot 1,0.5) on a 2560x1440 window at 2 pixels per local unit. The pivot's
        // screen point is 2400,1300 in Unity's coordinates (y upwards), so the row is 421.18 x 220.32 window pixels hanging left of
        // and around that point.
        check(CornersOf(2400f, 1300f, 1f, 0.5f, 210.59f, 110.16f, 2f, 2560f, 1440f, out LayoutRect icon)
              && Near(icon.Right, 2400f) && Near(icon.Width, 421.18f) && Near(icon.Height, 220.32f)
              && Near(icon.Y, 1440f - 1300f - 110.16f),
            $"the icon row is measured as its own layout rectangle, right edge on its pivot: ({icon.X:0.##},{icon.Y:0.##}) {icon.Width:0.##}x{icon.Height:0.##}");

        // The same row on a 1600x900 window: a different window, the same local geometry, and therefore a different place.
        check(CornersOf(1500f, 820f, 1f, 0.5f, 210.59f, 110.16f, 2f, 1600f, 900f, out LayoutRect small)
              && Near(small.Right, 1500f) && Near(small.Width, 421.18f) && Near(small.Y, 900f - 820f - 110.16f),
            $"and on a 1600x900 window it lands where that window puts it (right={small.Right:0.##}, top={small.Y:0.##}, width={small.Width:0.##})");

        // A pivot that is not 0.5 and a canvas scale that is not 1: 120 x 40 with the pivot at 0.25,0.75, one and a half pixels per
        // unit. `xMin = -0.25*120 = -30`, `xMax = 0.75*120 = 90`, `yMin = -0.75*40 = -30`, `yMax = 0.25*40 = 10`.
        check(CornersOf(800f, 600f, 0.25f, 0.75f, 120f, 40f, 1.5f, 1920f, 1080f, out LayoutRect offset)
              && Near(offset.X, 800f - 45f) && Near(offset.Width, 180f) && Near(offset.Height, 60f)
              && Near(offset.Y, 1080f - 600f - 15f),
            $"a pivot at 0.25,0.75 and a scale of 1.5 are both applied: ({offset.X:0.##},{offset.Y:0.##}) {offset.Width:0.##}x{offset.Height:0.##}, expected ({(800f - 45f):0.##},{(1080f - 600f - 15f):0.##}) 180x60");

        // The projection is a *camera* projection: the production call is the projection camera's own `WorldToScreenPoint`, and the
        // four corners come from the element's own `rect` through its own transform. 1.19.32 moved that call into `ProjectPoint` so
        // that a camera which is not a `Canvas.worldCamera` - the HUD's manual canvas - can project the same corners; for a canvas
        // camera the two calls are the same call (`RectTransformUtility.WorldToScreenPoint` forwards to `Camera.WorldToScreenPoint`).
        string panel = Code(Source("PanelBehaviour.cs"));
        check(panel.Contains("Vector2 screen = ProjectPoint(camera, world);")
              && panel.Contains("Vector3 screen = camera.WorldToScreenPoint(world);"),
            "the corners are projected through the projection camera's own `WorldToScreenPoint` - Unity's own call for an element this camera renders");
        check(panel.Contains("(corner & 2) == 0 ? local.yMin : local.yMax, 0f));"),
            "and they are the four corners of the element's own declared rect, not a pivot point plus a scale");
        check(panel.Contains("bool usable = AnchorRectProjection.TryFromCorners(xs, ys, Screen.width, Screen.height, out rect, out raw);"),
            "the raw window rectangle and the gate's verdict are answered together, so the log can print the numbers it refused");
        check(!panel.Contains("geometryScale"),
            "and no pixels-per-local-unit is invented anywhere on this path");
    }

    // ---- 4. the null camera ----------------------------------------------------------------------------------------

    // 1.19.27 passed `null` whenever the canvas had no `worldCamera`, which this build's HUD always answers. A null camera is only
    // the identity projection inside a real screen-space *overlay* canvas; everywhere else it silently reinterprets window pixels.
    private static void ANullCameraIsOnlyTheIdentityInsideARealOverlayCanvas(Action<bool, string> check)
    {
        check(!AnchorCanvasProjection.CameraLegitimate(hasCamera: false, AnchorCanvasMode.ScreenSpaceCamera),
            "a screen-space-camera canvas with no camera is refused: that is the recorded shape, and the null 1.19.27 passed is exactly this case");
        check(!AnchorCanvasProjection.CameraLegitimate(hasCamera: false, AnchorCanvasMode.WorldSpace),
            "a world-space canvas with no camera is refused too - its elements are world units and only a camera can place them");
        check(!AnchorCanvasProjection.CameraLegitimate(hasCamera: false, AnchorCanvasMode.Unknown),
            "and a canvas mode nobody could read is not an overlay by default");
        check(AnchorCanvasProjection.CameraLegitimate(hasCamera: false, AnchorCanvasMode.Overlay),
            "a real screen-space overlay canvas is the one case where a null camera is the correct argument");
        check(AnchorCanvasProjection.CameraLegitimate(hasCamera: true, AnchorCanvasMode.ScreenSpaceCamera)
              && AnchorCanvasProjection.CameraLegitimate(hasCamera: true, AnchorCanvasMode.WorldSpace),
            "and any real camera is legitimate as far as this rule is concerned - whether it draws the element is the ladder's question");

        string panel = Code(Source("PanelBehaviour.cs"));
        string rectBranch = Between(panel, "if (!measured && choice.Projection == AnchorProjection.Rect)",
            "string geometry = GeometryNote(element, used, raw, measured ? \"ok\" : \"refused\", why);");
        check(rectBranch.Length > 0, "the geometry branch is found in the panel");
        // 1.19.30 re-point: the gate is now asked about `projection`, the camera `AnchorElementCanvasPolicy` chose (the ladder's, or
        // the element's own ancestor canvas `worldCamera`), and it is still asked *before* the projection.
        check(rectBranch.IndexOf("AnchorCanvasProjection.CameraLegitimate(projection != null, elementMode)", StringComparison.Ordinal) >= 0
              && rectBranch.IndexOf("AnchorCanvasProjection.CameraLegitimate(projection != null, elementMode)", StringComparison.Ordinal)
                 < rectBranch.IndexOf("TryScreenRectFromLayout(element, projection", StringComparison.Ordinal),
            "the gate is asked *before* the projection, so a null camera outside an overlay canvas cannot produce a rectangle at all");
        check(CountOf(panel, "= RectTransformUtility.WorldToScreenPoint(null, world);") == 1,
            "the null-camera projection still exists exactly once, in the overlay branch, and is no longer a fallback for anything");
        check(!panel.Contains("if (element != null) TryCanvasGeometry("),
            "the 1.19.27 `TryCanvasGeometry` reader - which took `canvas.worldCamera` and fell back to a null projection - is gone");
    }

    // ---- 5. which sprite is drawn ---------------------------------------------------------------------------------

    // `SpriteStatusIcon.Enabled` and `.Disabled` are both created with the prefab; `SetActiveSprite(bool)` only flips their own
    // `enabled` flags. So `Enabled != null ? Enabled : Disabled` can never answer `Disabled`, and the object it hands back is the
    // 65 px sprite - not the anchor rectangle.
    private static void TheFlashlightSpritesAreNamedForWhatTheyAre(Action<bool, string> check)
    {
        check(AnchorFlashlightIcon.Drawn(enabledRendererOn: false, disabledRendererOn: true) == AnchorIconSprite.Disabled,
            "the recorded level (`flashlightActive=false`) is drawing the `_Disabled` sprite");
        check(AnchorFlashlightIcon.Drawn(enabledRendererOn: true, disabledRendererOn: false) == AnchorIconSprite.Enabled,
            "and a level with the flashlight on draws the `Enabled` one");
        check(AnchorFlashlightIcon.Drawn(enabledRendererOn: false, disabledRendererOn: false) == AnchorIconSprite.Enabled,
            "a HUD that has switched neither on - a level still coming up - answers `Enabled`, the field the read chain uses for its facts");
        check(AnchorFlashlightIcon.Name(AnchorIconSprite.Disabled) == "FlashLightIcon_Disabled"
              && AnchorFlashlightIcon.Name(AnchorIconSprite.Enabled) == "FlashLightIcon_:Enabled",
            "and the two objects are named exactly as the prefab names them");
        check(!AnchorFlashlightIcon.FallbackReachable(enabledRendererPresent: true),
            "`Enabled ?? Disabled` is unreachable while the Enabled renderer exists - which is always, so it says nothing about the HUD");
        check(AnchorFlashlightIcon.FallbackReachable(enabledRendererPresent: false),
            "it is only reachable for an interop build that answers the field as null, which is not a state of this prefab");

        string panel = Code(Source("PanelBehaviour.cs"));
        check(panel.Contains("sprite = status.Enabled != null ? status.Enabled : status.Disabled;"),
            "the panel still reads that expression - it is where the layer and the CanvasRenderer fact come from");
        check(panel.Contains("drawn = AnchorFlashlightIcon.Drawn(enabledOn, disabledOn);"),
            "but which sprite is *drawn* is answered from the renderers' own flags, through the policy above");
        check(panel.Contains("iconRect = display.m_rectTrans;"),
            "and the rectangle that is measured is the icon row's own `m_rectTrans` - 210.59 x 110.16, the object the HUD lays out");
        check(panel.Contains("layoutIsGeometry: true") && panel.Contains("layoutIsGeometry: false"),
            "the icon row is measured as its layout rectangle while the row's Background Fade keeps its sprite geometry - the two are told apart by an explicit fact, not by an accident");
        check(panel.Contains("measuredFrom=") && panel.Contains("AnchorSourceLadder.GeometryName(source)"),
            "and every line that names a source also names the rectangle, so `anchor=flashlight` can no longer be read as \"the icon was measured\"");
        check(AnchorSourceLadder.GeometryName(AnchorElementSource.Flashlight).Contains("layout rectangle")
              && AnchorSourceLadder.GeometryName(AnchorElementSource.BackgroundFade).Contains("SpriteRenderer")
              && AnchorSourceLadder.GeometryName(AnchorElementSource.None).Contains("PositionX/PositionY"),
            "the three answers a reader has to tell apart are named apart: " + AnchorSourceLadder.GeometryName(AnchorElementSource.Flashlight)
            + " / " + AnchorSourceLadder.GeometryName(AnchorElementSource.BackgroundFade) + " / " + AnchorSourceLadder.GeometryName(AnchorElementSource.None));
    }

    // ---- 6. the bounded log --------------------------------------------------------------------------------------

    // One line per attempt (and, on an answer that cannot change, one line per *round*), one give-up line per level, and no line at
    // all from the watch - the 1.19.22 / 1.19.26 contract, driven on the production policy over a synthetic clock.
    private static void TheLogStaysBounded(Action<bool, string> check)
    {
        int lines = Round(measured: false, out bool abandoned, out int attempts, out int watches);
        check(abandoned && attempts == AnchorSamplePolicy.Budget,
            $"a HUD that never answers spends exactly the budget ({attempts} attempt(s))");
        check(lines == 1,
            $"and costs one failure line for the whole round ({lines}), because an answer that cannot change says nothing new");
        check(watches > 0,
            $"after the give-up the round keeps watching instead of going silent ({watches} watch attempt(s))");

        int measuredLines = Round(measured: true, out bool locked, out int measuredAttempts, out _);
        check(!locked && measuredAttempts > 1 && measuredLines == 0,
            $"a HUD that answers writes no failure line at all ({measuredAttempts} attempt(s))");

        string panel = Code(Source("PanelBehaviour.cs"));
        check(panel.Contains("if (_anchorPolicy.DueToReport)") && panel.Contains("if (_anchorPolicy.FreshWarning)"),
            "the panel asks the policy whether this attempt owes a line, and whether that line says anything new");
        check(CountOf(panel, "the flashlight anchor could not be measured yet (") == 1,
            "there is exactly one per-attempt failure line in the panel");
        check(CountOf(panel, "the flashlight anchor fast retries ended after") == 1
              && panel.Contains("if (gaveUp)"),
            "and exactly one give-up line, written only on the failure that spent the budget");
        check(panel.Contains("if (_loggedAnchorCamera) return;"),
            "the camera/geometry diagnostic is printed once per measurement round, not once per attempt");
        check(panel.Contains("private static void ReportCamera(") && panel.Contains("_loggedAnchorCamera = true;"),
            "which is the latch that bounds it");

    }

    // ---- wiring, read from the production source ------------------------------------------------------------------

    private static void Wiring(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the 1.19.28 anchor regression locates the production sources");
        if (root == null) return;

        string ladder = File.ReadAllText(Path.Combine(root, "AnchorCameraLadder.cs"));
        check(ladder.Contains("public static AnchorScreenRect Raw(IReadOnlyList<float> xs, IReadOnlyList<float> ys)"),
            "the raw window rectangle is a production type the diagnostic reads, not a value the log recomputes");
        // 1.19.31 re-point: the enum value `7` was never the contract - the ranking is. The CellUI camera has its own source, its own
        // log token, and it is ranked *after* the element's own canvas and the HUD's own answers but *before* the game's page camera,
        // which is the property this build actually relies on.
        check(AnchorCameraChoice.Token(AnchorCameraSource.CellUiCamera) == "cellui"
              && AnchorCameraLadder.Rank(AnchorCameraSource.CellUiCamera) < AnchorCameraLadder.Rank(AnchorCameraSource.PageCamera)
              && AnchorCameraLadder.Rank(AnchorCameraSource.CellUiCamera) > AnchorCameraLadder.Rank(AnchorCameraSource.ElementCanvas),
            "the CellUI camera is its own source, ranked below the element's own canvas and above the game's page camera");
        check(!ladder.Contains("public static class AnchorCanvasRect"),
            "and the 1.19.27 scale arithmetic is gone rather than left behind unused");

        string panel = File.ReadAllText(Path.Combine(root, "PanelBehaviour.cs"));
        check(panel.Contains("IconElement: {icon.Geometry}. RowElement: {row.Geometry}."),
            "the diagnostic line prints both elements' numbers - rectangle, pivot, position, camera, raw screen rect and the gate's word");
        check(panel.Contains("cellUiCanvas=") && panel.Contains("cellUiMaskProof="),
            "and the CellUI facts that decide whether this build has a camera at all");

        Console.WriteLine("WIRING: RELEASE_11928_ANCHOR_CONSTANT_CAMERA_AND_LAYOUT_RECT (static source check; the predicate, the ladder, the projection and the log budget are the runtime assertions above)");
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private readonly struct WalkedObject
    {
        public readonly string Name;
        public readonly bool ActiveInHierarchy;
        public WalkedObject(string name, bool activeInHierarchy) { Name = name; ActiveInHierarchy = activeInHierarchy; }
    }

    // One walk of the icon display's subtree, asking production code what to select.
    private static int Walk(IReadOnlyList<WalkedObject> objects, bool includeInactive)
    {
        if (objects == null) return -1;
        for (int index = 0; index < objects.Count; index++)
        {
            if (!includeInactive && !objects[index].ActiveInHierarchy) continue;
            if (!AnchorElementMatch.IsBackgroundFade(objects[index].Name)) continue;
            return index;
        }
        return -1;
    }

    // A synthetic orthographic camera on the canvas plane: `pivotScreen` is the element's pivot in Unity's screen coordinates
    // (y upwards), one local unit is `scale` pixels, and the corners are reduced by production code into a window rectangle.
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

    // One measurement round on the production policy, driven on a 0.25 s clock: how many failure lines it owed, and how many
    // watch attempts it made after giving up.
    private static int Round(bool measured, out bool abandoned, out int attempts, out int watches)
    {
        var policy = new AnchorSamplePolicy();
        int lines = 0;
        float now = 0f;
        for (int frame = 0; frame < 4000; frame++)
        {
            now = frame * 0.25f;
            if (!policy.Due(now)) continue;
            AnchorSample sample = policy.Attempt(now,
                () => measured
                    ? AnchorSample.Of(new LayoutRect(100f, 200f, 300f, 111f), AnchorElementSource.Flashlight)
                    : AnchorSample.Missing(AnchorSampleFailure.NoProjection), out _);
            if (!sample.Measured && policy.DueToReport)
            {
                if (policy.FreshWarning) lines++;
                policy.Reported();
            }
            if (measured && policy.Attempts >= 4) break;
            if (!measured && policy.Watches >= 3) break;
        }
        abandoned = policy.Abandoned;
        attempts = policy.Attempts;
        watches = policy.Watches;
        return lines;
    }

    private static bool Near(float left, float right) => Math.Abs(left - right) < 0.01f;

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

    private static int CountOf(string text, string token) => text.Split(new[] { token }, StringSplitOptions.None).Length - 1;

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
