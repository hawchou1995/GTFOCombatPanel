using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// 1.19.27: the in-level anchor had never measured anything, on any recorded level, and the log said so every time.
//
// Fourteen log snapshots (1.19.19 / 1.19.20 / 1.19.24 / 1.19.25 / 1.19.26, from two machines) contain `anchor=measured` zero
// times and `flashlight anchor locked` zero times. Every one of them ends the same way:
//
//   flashlight anchor camera: canvasRenderMode=screen-space-camera, iconLayer=5, canvasWorldCamera=none,
//   layerCanvas=none,layerCanvasRenderMode=not-read,layerCanvasRenderModeProperty=ScreenSpaceCamera,
//   iconHasRectTransform=true,iconCanvasDrawn=false,flashlight=found,flashlightLayer=5,flashlightCanvasRenderer=false,
//   flashlightBounds=32.5x32.5, background-fade=missing, ... no projection: no live camera in the read chain has the
//   element's layer in its culling mask.
//
// Two structural mistakes keep it there, and both are asserted here:
//
//   A. the row's `Background Fade` was looked for the way this build had always looked for it, and reported `missing` on
//      the prefab that carries it (it is switched off in the prefab, so a walk that asks only for active children cannot
//      see it) - and once a lookup forgets the element it can never answer again;
//   B. the ladder made a `CanvasRenderer` the *gate* for any camera-less answer, and this prefab's two anchors have none -
//      那一对元素在结构上就没有 `CanvasRenderer`，所以那道闸门对这一对恒为假，恒假的东西不是环境问题。
//
// What is asserted: an element that carries a `RectTransform` inside a screen-space canvas is measured from Unity's own
// canvas layout and needs no camera proof (rule 5 of `AnchorCameraLadder.Select`), the rectangle that comes out of that
// arithmetic is the right window rectangle (anchored top-right, pivot not 0.5 included), it still passes the same
// `ScreenBounds` sanity gate, a camera that can be proven still wins over it, the log can name which of the three answers
// (camera, canvas geometry, configured position) produced the rectangle, and the per-round log contract is untouched.
internal static class Release11927Scenarios
{
    private const int IconLayer = 5;
    private const float WindowWidth = 1920f;
    private const float WindowHeight = 1080f;

    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        TheInactiveRowIsFoundByTheWalksOwnTerms(check);
        OnlyAScreenSpaceCanvasWithARectTransformIsAuthorized(check);
        TheCanvasRectangleIsUnitysOwnLayout(check);
        TheSanityGateStillRefusesUnusableRectangles(check);
        AProvenCameraStillWinsAndTheLogNamesTheSource(check);
        TheTwoOldFailureReasonsAreGone(check);
        TheRoundAndTheHudAreUntouched(check);
        Wiring(check);
    }

    // ---- A. the element lookup ------------------------------------------------------------------------------------

    // The recorded `background-fade=missing` is the lookup's own report, and the element it could not find is the row's
    // `Background Fade`, which the prefab switches off (`m_IsActive=False`). `GetComponentsInChildren<RectTransform>(true)`
    // is what returns it; `(false)` - active children only - is what never can. Both halves are asserted: the production
    // walk really asks for inactive children (source check) and a lookup that is handed such a walk's answer ends up
    // `Found` rather than `Missing` (runtime check on the production `AnchorElementLookup`).
    private static void TheInactiveRowIsFoundByTheWalksOwnTerms(Action<bool, string> check)
    {
        // The hierarchy the icon display really has, as the walk hands it back: one object carries the row's own name and
        // the prefab left it switched off - that is the whole reason the old lookup answered `missing`.
        var display = new[]
        {
            new WalkedObject("Slots", activeInHierarchy: true),
            new WalkedObject(AnchorElementNames.BackgroundFadeObject, activeInHierarchy: false),
            new WalkedObject("FlashLightIcon_:Enabled", activeInHierarchy: true)
        };

        check(Walk(display, includeInactive: true) >= 0,
            "with inactive children included - what `GetComponentsInChildren<RectTransform>(true)` asks for - the row's inactive Background Fade is among the objects the walk returns");
        check(Walk(display, includeInactive: false) < 0,
            "and with active children only it is not, which is exactly the walk the recorded logs can only have made: the element is present in the prefab and switched off");

        // The production cache, driven with that answer: a hit is a hit whatever the object's own active state is.
        var lookup = new AnchorElementLookup();
        object owner = new object();
        check(lookup.Due(owner, 10), "the first sampling attempt of a level walks the icon display");
        lookup.Record(owner, found: Walk(display, includeInactive: true) >= 0, walked: true);
        check(lookup.Hit && lookup.Describe() == "yes(found)",
            "an inactive element the walk really returned is recorded as a hit, not as a miss: the lookup asks about the name, never about `activeSelf`");
        check(!lookup.Due(owner, 4000),
            "and the hit is cached for the life of the display, so the level costs one walk of this subtree instead of one per attempt");

        // The production walk itself, read back from the panel (it cannot be compiled without GTFO's assemblies).
        string panel = Source("PanelBehaviour.cs");
        string walk = Between(panel, "    private static RectTransform BackgroundFade(PlayerGuiLayer layer)",
            "    private static Renderer BackgroundFadeRenderer(PlayerGuiLayer layer)");
        check(walk.Length > 0, "the background lookup is found in the panel");
        check(walk.Contains("owner.GetComponentsInChildren<RectTransform>(true)") || walk.Contains("owner.GetComponentsInChildren<RectTransform>(true);"),
            "and it includes inactive children - the `true` argument is the fix, a walk that asks for active children cannot see this prefab's row");
        check(CountOf(panel, "GetComponentsInChildren<RectTransform>(") == 1,
            "exactly one recursive walk exists in the whole panel, and it is that one");
        check(!Code(walk).Contains("activeInHierarchy") && !Code(walk).Contains("activeSelf"),
            "the walk does not filter on the element's own active state: `Background Fade` is switched off in the prefab, so a filter here would make the element unreachable for ever");
        // 1.19.28: the walk no longer spells the comparison itself - it calls the production predicate, and that is what the
        // next two checks pin. The line this replaces asserted the *log token* `AnchorElementNames.BackgroundFade` ("background-
        // fade") as the match, which is exactly the defect 1.19.28 fixes: a test that asserts the wrong constant is how a green
        // suite shipped a build whose walk could not hit on any frame of any level.
        check(walk.Contains("if (!AnchorElementMatch.IsBackgroundFade(candidate.name)) continue;"),
            "the walk matches through `AnchorElementMatch.IsBackgroundFade`, the one production predicate, so the constant can only be wrong in one place and the regression suite drives that place");
        check(!walk.Contains("AnchorElementNames.BackgroundFade,"),
            "and it never compares against the log token again: `AnchorElementNames.BackgroundFade` is the word `[DIAG]` lines print, not an object name");

        // The root it starts from, and the null chain that has to stay a `Missing`/`Unreadable` answer rather than a throw.
        check(panel.Contains("layer.Inventory.m_iconDisplay.transform"),
            "the walk starts at `GuiManager.Current -> m_playerLayer -> Inventory -> m_iconDisplay`, the chain the reference mods read the row out of - not at a scene scan");
        check(panel.Contains("var layer = GuiManager.Current?.m_playerLayer;") && panel.Contains("layer.Inventory?.m_iconDisplay == null"),
            "and every hop of that chain is null-checked before the HUD is read, so a level whose HUD is not up yet answers `unreadable`/`missing` and retries instead of throwing");
        check(walk.Contains("if (owner == null)")
              && walk.Contains("ForgetBackgroundFade();")
              && walk.Contains("return null;"),
            "a null display forgets the cache and answers null - the `Unreadable`/`Missing` contract, never an exception and never a latch that closes the door for the level");
    }

    // ---- B. the authorization -------------------------------------------------------------------------------------

    // Rule 5 of the ladder, as a policy: a `RectTransform` plus a screen-space canvas authorizes the canvas-geometry
    // answer, and nothing else does. A world-space canvas keeps the camera proof, and an element with no RectTransform
    // has no geometry to compute - in that case the old dead ends stand, which is what the 1.19.26 suite already asserts.
    private static void OnlyAScreenSpaceCanvasWithARectTransformIsAuthorized(Action<bool, string> check)
    {
        check(AnchorCanvasProjection.Authorized(hasRectTransform: true, AnchorCanvasMode.ScreenSpaceCamera),
            "a RectTransform inside a screen-space-camera canvas is authorized: Unity's own layout already put it in window pixels");
        check(AnchorCanvasProjection.Authorized(hasRectTransform: true, AnchorCanvasMode.Overlay),
            "a RectTransform inside an overlay canvas is authorized too - that canvas is composited without a camera at all");
        check(!AnchorCanvasProjection.Authorized(hasRectTransform: false, AnchorCanvasMode.ScreenSpaceCamera)
              && !AnchorCanvasProjection.Authorized(hasRectTransform: false, AnchorCanvasMode.Overlay),
            "without a RectTransform there is no geometry to compute, so the authorization is refused whatever the canvas is");
        check(!AnchorCanvasProjection.Authorized(hasRectTransform: true, AnchorCanvasMode.WorldSpace),
            "a world-space canvas is refused: its elements live in world units, and only a camera can say where those land");
        check(!AnchorCanvasProjection.Authorized(hasRectTransform: true, AnchorCanvasMode.Unknown),
            "and a canvas mode nobody could read is not screen-space either - nothing is known, so only a proven camera may answer");

        // The recorded level's own shape: screen-space-camera canvas, no camera anywhere in the chain, CanvasRenderer false,
        // element present and inactive. This is the call the old build refused, and it is the whole defect.
        var none = new List<AnchorCameraCandidate>();
        AnchorCameraChoice geometry = AnchorCameraLadder.Select(none, IconLayer, AnchorCanvasMode.ScreenSpaceCamera,
            canvasDrawn: false, hasRectTransform: true, out int inspected);
        check(geometry.Usable && geometry.Projection == AnchorProjection.Rect && geometry.Index == -1,
            "the exact recording - screen-space-camera canvas, three dead cameras' worth of nothing, `canvasDrawn=false`, a RectTransform - now answers the canvas-geometry projection instead of `no projection`");
        check(inspected == 0, "and it did not need a camera to be inspected to say so");
        check(geometry.Reason.Contains("RectTransform") && geometry.Reason.Contains("screen-space canvas"),
            "the reason says which fact authorized it: " + geometry.Reason);

        // The gate is not "anything goes": the proof-only form of the call (no RectTransform fact) is unchanged, which is
        // what keeps a world sprite from borrowing this answer.
        check(!AnchorCameraLadder.Select(none, IconLayer, AnchorCanvasMode.ScreenSpaceCamera, canvasDrawn: false, out _).Usable,
            "the same element offered without the RectTransform fact is still refused: the five-argument ladder is the proof-only one every earlier caller uses");
        check(!AnchorCameraLadder.Select(none, IconLayer, AnchorCanvasMode.WorldSpace, canvasDrawn: false, hasRectTransform: true, out _).Usable,
            "and a RectTransform under a world-space canvas is refused in the real call as well");

        // A camera that can be proven still wins over the geometry: two answers, the provable one is taken.
        var hud = new List<AnchorCameraCandidate> { new AnchorCameraCandidate(AnchorCameraSource.HudWorldCamera, true, 1 << IconLayer, -2) };
        AnchorCameraChoice both = AnchorCameraLadder.Select(hud, IconLayer, AnchorCanvasMode.ScreenSpaceCamera,
            canvasDrawn: false, hasRectTransform: true, out _);
        check(both.Usable && both.Projection == AnchorProjection.Camera && both.Source == AnchorCameraSource.HudWorldCamera,
            "when a live camera really draws the element's layer, that camera is the answer - the canvas geometry is the fallback, never the preference");
        // ... and a camera that cannot be proven does not block the geometry either.
        var blind = new List<AnchorCameraCandidate> { new AnchorCameraCandidate(AnchorCameraSource.AllCameras, true, 1 << 3, 0) };
        AnchorCameraChoice afterBlind = AnchorCameraLadder.Select(blind, IconLayer, AnchorCanvasMode.ScreenSpaceCamera,
            canvasDrawn: false, hasRectTransform: true, out _);
        check(afterBlind.Usable && afterBlind.Projection == AnchorProjection.Rect,
            "a live camera whose culling mask misses the element's layer is refused as before, and the geometry answer is then taken instead of the dead end");
    }

    // ---- the arithmetic -------------------------------------------------------------------------------------------

    // 1.19.28 re-point: the arithmetic is `AnchorRectProjection.TryFromCorners` - the element's own four corners, projected by a
    // camera, reduced to the window rectangle they cover - and no longer `AnchorCanvasRect.TryFromPivot` (pivot screen point plus
    // `rect` times a scale). The *answers* the 1.19.27 suite pinned are the same rectangles; what changed underneath them is that
    // the projection is Unity's own (each corner goes through the camera) instead of a "pixels per local unit" number this build
    // had to invent. The synthetic camera below is an orthographic one on the canvas plane: `pivotScreen + local * scale`.
    private static void TheCanvasRectangleIsUnitysOwnLayout(Action<bool, string> check)
    {
        // Anchored top-right with the pivot at (1,1): a 150 x 30 rectangle hanging left and down from the pivot.
        // 2 px per local unit, pivot at (1800, 1000) in a 1920x1080 window (screen y upwards).
        check(Screen(1800f, 1000f, 1f, 1f, 150f, 30f, 2f, WindowWidth, WindowHeight, out LayoutRect corner)
              && Near(corner.X, 1500f) && Near(corner.Y, 80f) && Near(corner.Width, 300f) && Near(corner.Height, 60f),
            $"an element anchored to the top-right corner with its pivot at 1,1 is measured as the rectangle Unity laid out: ({corner.X:0},{corner.Y:0}) {corner.Width:0}x{corner.Height:0}, expected (1500,80) 300x60");

        // A pivot that is not the centre: 0.25, 0.75 of a 200 x 40 rectangle, one pixel per local unit.
        check(Screen(500f, 400f, 0.25f, 0.75f, 200f, 40f, 1f, 1000f, 800f, out LayoutRect offsetPivot)
              && Near(offsetPivot.X, 450f) && Near(offsetPivot.Y, 390f) && Near(offsetPivot.Width, 200f) && Near(offsetPivot.Height, 40f),
            $"a non-centred pivot is placed by its own rect, not by its centre: ({offsetPivot.X:0},{offsetPivot.Y:0}) {offsetPivot.Width:0}x{offsetPivot.Height:0}, expected (450,390) 200x40");

        // The row's own `Background Fade` as the prefab declares it - 300 x 109.92, pivot 1,0.5 - on a 1920x1080 window at
        // 2 px per unit: its right edge is the pivot's own x, which is the edge the table hangs from.
        check(Screen(1700f, 600f, 1f, 0.5f, 300f, 109.92f, 2f, WindowWidth, WindowHeight, out LayoutRect fade)
              && Near(fade.Right, 1700f) && Near(fade.Width, 600f),
            $"the row's Background Fade measures its declared 300 local units from the pivot's own right edge (right={fade.Right:0}, width={fade.Width:0})");

        // A canvas scale that is not 1 is a real scale: the same element at 1 px per unit is half as wide.
        check(Screen(1800f, 1000f, 1f, 1f, 150f, 30f, 1f, WindowWidth, WindowHeight, out LayoutRect scaleOne)
              && Near(scaleOne.Width, 150f),
            "the canvas' own scale is applied: the same rectangle at 1 px per local unit is 150 px wide, not 300");

        // 1.19.28: the two resolutions this build really runs at, so the numbers are not only true of 1920x1080.
        check(Screen(2400f, 1350f, 1f, 0.5f, 300f, 109.92f, 2f, 2560f, 1440f, out LayoutRect wide)
              && Near(wide.Right, 2400f) && Near(wide.Width, 600f) && Near(wide.Y, 1440f - 1350f - 109.92f),
            $"a 2560x1440 window answers the same rectangle in its own pixels (right={wide.Right:0}, width={wide.Width:0}, top={wide.Y:0})");
        check(Screen(1500f, 850f, 1f, 0.5f, 300f, 109.92f, 2f, 1600f, 900f, out LayoutRect small)
              && Near(small.Right, 1500f) && Near(small.Width, 600f) && Near(small.Y, 900f - 850f - 109.92f),
            $"and a 1600x900 window answers it as well (right={small.Right:0}, width={small.Width:0}, top={small.Y:0})");
    }

    // The sanity gate is not bypassed by the new path: finite corners, a real extent, and something a player could see. 1.19.28
    // additionally asserts that the *refused* numbers are still reported (`AnchorScreenRect`), because a diagnostic line that can
    // only say "unmeasurable" cannot tell an off-window rectangle from one built out of NaN.
    private static void TheSanityGateStillRefusesUnusableRectangles(Action<bool, string> check)
    {
        check(!Corners(new[] { float.NaN, float.NaN, float.NaN, float.NaN }, new[] { 400f, 400f, 410f, 410f }, out _, out AnchorScreenRect nanRaw),
            "a corner that is not a number is not a rectangle");
        check(!nanRaw.Finite && float.IsNaN(nanRaw.Left) && nanRaw.Describe().Contains("NaN"),
            "and the raw numbers say NaN rather than zero, so the line can print why it was refused: " + nanRaw.Describe());
        check(!Corners(new[] { 400f, 410f, 400f, 410f }, new[] { float.NaN, float.NaN, 410f, 410f }, out _, out _),
            "nor is one of its own extents");
        check(!Corners(new[] { 400f, 400f, 400f, 400f }, new[] { 400f, 400f, 400f, 400f }, out _, out _),
            "a zero-width, zero-height rectangle is refused: an element with no extent is unmeasurable, not a dot at its pivot");
        check(!Corners(new[] { 0.2f, 0.3f, 0.2f, 0.3f }, new[] { 400f, 400f, 401f, 401f }, out _, out _),
            "a rectangle thinner than a pixel is refused, exactly as the renderer-bounds path refuses one");
        check(!Corners(new[] { -9000f, -8990f, -9000f, -8990f }, new[] { 9000f, 9000f, 9010f, 9010f }, out _, out AnchorScreenRect offWindow),
            "a rectangle entirely off the window is refused");
        check(offWindow.Finite && Near(offWindow.Left, -9000f) && Near(offWindow.Top, 9000f),
            "and its numbers are still reported, which is how an off-window rectangle is told from a NaN one: " + offWindow.Describe());
        check(!Corners(new[] { -2040f, 3960f, -2040f, 3960f }, new[] { -2040f, -2040f, 3960f, 3960f }, out _, out _),
            "and so is one that covers the whole window - that is not a measurement of anything either");
        check(!Corners(new[] { 400f, 410f, 400f, 410f }, new[] { 400f, 400f, 410f, 410f }, WindowWidth, 0f, out _, out _)
              && !Corners(new[] { 400f, 410f, 400f, 410f }, new[] { 400f, 400f, 410f, 410f }, float.NaN, WindowHeight, out _, out _),
            "an unreadable window is refused by the same gate");
        check(Corners(new[] { 760f, 780f, 760f, 780f }, new[] { 480f, 480f, 500f, 500f }, out LayoutRect real, out AnchorScreenRect realRaw)
              && Near(real.Width, 20f) && Near(real.Height, 20f) && realRaw.Finite && Near(realRaw.Width, 20f),
            "while a real one inside the window is answered, and its raw numbers agree with it");


        // The settle/alpha gates downstream are untouched - the rectangle is still only *a* sample.
        string panel = Source("PanelBehaviour.cs");
        check(panel.Contains("bool still = !float.IsNaN(_anchorLastX) && Mathf.Abs(rect.X - _anchorLastX) < 2f && Mathf.Abs(rect.Y - _anchorLastY) < 2f;")
              && panel.Contains("if (_anchorSettle >= 1f || _anchorSampled >= 6f)"),
            "a measured rectangle still has to hold still for the settle window before the anchor locks - the canvas-geometry path buys no shortcut through it");
        check(panel.Contains("if (_anchorSource.Observe(sample.Source))"),
            "and a change of native element still restarts that window");
    }

    // ---- the three answers, and the words for them ----------------------------------------------------------------

    // One word per answer, so a log reader can tell a camera projection from the canvas' geometry from the configured
    // fallback, and "no answer" still means the configured coordinates.
    private static void AProvenCameraStillWinsAndTheLogNamesTheSource(Action<bool, string> check)
    {
        check(AnchorCameraChoice.Provenance(AnchorProjection.Camera) == "camera",
            "a proven camera projection is logged as `anchorSource=camera`");
        check(AnchorCameraChoice.Provenance(AnchorProjection.Rect) == "canvas-geometry",
            "the rectangle Unity's own layout produced is logged as `anchorSource=canvas-geometry`");
        check(AnchorCameraChoice.Provenance(AnchorProjection.CameraLessOverlay) == "canvas-overlay",
            "the camera-less overlay answer keeps a word of its own, because it is a third, different proof");
        check(AnchorCameraChoice.Provenance(AnchorProjection.None) == "config",
            "and when nothing answered the word is `config`: the table is on PositionX/PositionY, which is exactly what it always meant");

        check(AnchorCameraChoice.Describe(new AnchorCameraChoice(AnchorProjection.Rect, AnchorCameraSource.None, -1, string.Empty)).Contains("RectTransform"),
            "the anchor's own note names the RectTransform when the canvas geometry answered, so the lock line says so");
        check(AnchorCameraChoice.Describe(AnchorCameraChoice.Unusable("nothing at all")).StartsWith("no projection:"),
            "an unusable ladder still starts with `no projection:` - the 1.19.26 wording that a log reader greps for");
        check(AnchorSamplePolicy.Describe(AnchorSampleFailure.NoProjection).Contains("no camera that draws"),
            "the give-up reason still names the missing camera proof first");
        check(AnchorSamplePolicy.Describe(AnchorSampleFailure.NoProjection).Contains("RectTransform")
              && AnchorSamplePolicy.Describe(AnchorSampleFailure.NoProjection).Contains("culling mask"),
            "and it now says the other half too: no canvas geometry either, because the element carries no RectTransform on a screen-space canvas - the canvas mode and the culling masks were read as well, so `no camera can draw this icon` is never the whole conclusion again");
        check(AnchorSamplePolicy.Describe(AnchorSampleFailure.NoProjection) != AnchorSamplePolicy.Describe(AnchorSampleFailure.NoCamera),
            "the two dead ends stay distinguishable in the log");
        check(AnchorSamplePolicy.Describe(AnchorSampleFailure.Unmeasurable).Contains("off screen"),
            "an unmeasurable sample still says its geometry projected off screen");

        // The panel prints the word beside the facts, and an element that is inactive is not itself a reason.
        string panel = Source("PanelBehaviour.cs");
        // 1.19.29 re-point: `anchorSource=` is the rectangle the table is *using*, and an authorization is not an answer. The
        // recorded 1.19.28 line said `anchorSource=canvas-geometry` beside two `gate=refused`s and `iconSource=config`; the
        // un-measured form is therefore asserted *absent*, which is what keeps that from coming back.
        check(panel.Contains("anchorSource={AnchorCameraChoice.Provenance(choice.Projection, measured)}")
              && panel.Contains("authorization={AnchorCameraChoice.Authorization(choice.Projection, measured)}"),
            "the camera line carries `anchorSource=<camera|canvas-geometry|config>` for the rectangle that answered, and `authorization=` for the rule this attempt was granted");
        check(!panel.Contains("anchorSource={AnchorCameraChoice.Provenance(choice.Projection)}"),
            "and the authorisation-only form is gone: a rule that produced no rectangle can never be printed as the anchor's source");
        // The needles are built from char literals so this file never has to spell an escaped quote twice: what is asserted is
        // the production source text `,authorization=" + AnchorCameraChoice.Authorization(choice.Projection, measured)`.
        string authorizationField = ",authorization=" + '"' + " + AnchorCameraChoice.Authorization(choice.Projection, measured)";
        string canvasRendererField = '"' + ",iconCanvasRenderer=" + '"';
        check(panel.Contains(authorizationField) && panel.Contains(canvasRendererField),
            "and the diagnostic names the authorization once more beside the canvas reads, with the CanvasRenderer fact renamed: it is a property of this prefab, not a fault of the level");
        check(!panel.Contains("iconCanvasDrawn="),
            "the old `iconCanvasDrawn=` field is gone, because a false there read like the failure and was in fact the prefab's own shape");
    }

    // The two readings that used to *look* like the cause, and are not: an inactive element, and the absence of a
    // `CanvasRenderer` (the recorded line's `flashlightActive=false` / `canvasRenderer=false`).
    private static void TheTwoOldFailureReasonsAreGone(Action<bool, string> check)
    {
        // Neither fact is an input of the authorization at all: `Authorized` takes the RectTransform and the canvas mode.
        check(AnchorCanvasProjection.Authorized(hasRectTransform: true, AnchorCanvasMode.ScreenSpaceCamera),
            "an element the prefab switched off (`flashlightActive=false` on the recorded level) is still authorized - the element's active state is not part of this decision, and the ladder is never handed it");

        // The exact recorded shape again, now stated as "the two readings that used to be fatal are not read at all".
        var none = new List<AnchorCameraCandidate>();
        AnchorCameraChoice inactive = AnchorCameraLadder.Select(none, IconLayer, AnchorCanvasMode.ScreenSpaceCamera,
            canvasDrawn: false, hasRectTransform: true, out _);
        check(inactive.Usable,
            "`flashlightCanvasRenderer=false` no longer closes the camera-less path: the recorded level's own readings now answer a rectangle");

        // The ladder takes no `active` argument anywhere - a first parameter list that grew one would turn this red.
        string ladder = Source("AnchorCameraLadder.cs");
        string select = Between(ladder, "    public static AnchorCameraChoice Select(IReadOnlyList<AnchorCameraCandidate> candidates, int elementLayer,\n        AnchorCanvasMode mode, bool canvasDrawn, bool hasRectTransform, out int inspected)",
            "        inspected = 0;");
        check(select.Length > 0, "the six-argument ladder is found in production");
        check(!select.Contains("active"), "and its inputs are the layer, the canvas mode, the CanvasRenderer fact and the RectTransform fact - no active state, nothing else");

        // The panel hands the ladder exactly those facts.
        check(Source("PanelBehaviour.cs").Contains("AnchorCameraLadder.Select(candidates, layer, elementMode, canvasDrawn, element != null, out int inspected)"),
            "the panel passes `element != null` as the RectTransform fact, keeps `canvasDrawn` for the older rules, and asks with the element's own canvas mode");
        // 1.19.28 re-point: the rectangle is projected through a *real* camera now, and the scale arithmetic 1.19.27 introduced is
        // gone with the null-camera assumption it was built on. Both branches are asserted, because which one answered is the whole
        // point: the proven camera when the ladder found one, and the canvas' own camera for the geometry answer otherwise.
        check(Source("PanelBehaviour.cs").Contains("measured = TryScreenRectFromLayout(element, camera, out rect, out raw, out why);"),
            "the rectangle is the element's own RectTransform, projected by the camera the ladder accepted when it proved one");
        // 1.19.30 re-point: the geometry answer is projected by `projection`, which is the ladder's camera when it had one and the
        // element's own ancestor canvas `worldCamera` otherwise (`AnchorElementCanvasPolicy`). Both are still a real camera - the
        // one `RectTransformUtility` documents for that element - and both are still gated below.
        check(Source("PanelBehaviour.cs").Contains("measured = TryScreenRectFromLayout(element, projection, out rect, out raw, out why);"),
            "and by the camera that element's own canvas is composited by when the ladder answered the geometry rule instead");
        check(!Source("PanelBehaviour.cs").Contains("geometryScale"),
            "1.19.28: the invented `geometryScale` is gone - no caller has to guess how many pixels a local unit is worth");
    }

    // ---- contracts that must not move -----------------------------------------------------------------------------

    // The HUD is still IMGUI in screen space, the anchor still locks once, the round is still twelve attempts with one line
    // each and one give-up line, and the configured fallback is still what a level with no answer draws.
    private static void TheRoundAndTheHudAreUntouched(Action<bool, string> check)
    {
        string panel = Source("PanelBehaviour.cs");
        check(panel.Contains("if (Plugin.AnchorToFlashlight.Value && !_anchorLocked) SampleAnchor(layer, Time.unscaledTime);"),
            "a locked anchor is still never sampled again");
        check(panel.Contains("_anchorLocked = true;") && panel.Contains("_anchorFallback = false;"),
            "and the lock still clears the configured fallback, so a measured table never jumps back");
        check(panel.Contains("HudTable.Draw(_grid, _origin.X, _origin.Y, _content);"),
            "the table itself is still drawn by IMGUI inside OnGUI: this round changed how the anchor is measured, not how the HUD is drawn");
        check(!panel.Contains("Instantiate(") && !panel.Contains("new GameObject("),
            "nothing in the panel clones a prefab or creates a uGUI object - there is no uGUI injection here");
        check(panel.Contains("if (!_anchorPolicy.Due(now)) return;")
              && panel.Contains("if (_anchorPolicy.DueToReport)")
              && panel.Contains("if (_anchorPolicy.FreshWarning)"),
            "one line per attempt, identical warnings suppressed once per round: the log cadence is the policy's, untouched");
        check(panel.Contains("the flashlight anchor fast retries ended after")
              && panel.Contains("automatic HUD placement still waits for proof"),
            "the recovery log does not claim a configured rectangle was drawn when projection was refused");
        check(panel.Contains("TryResolveOrigin") && panel.Contains("float fractionX = Plugin.PositionX.Value;"),
            "the configured position is still what a level with no measured rectangle uses");
        check(CountOf(panel, "_anchorUnavailable = false;") == 3,
            "the give-up is still cleared in exactly three places, so the watch contract of 1.19.26 is unchanged");
    }

    // ---- wiring, read from the production source ------------------------------------------------------------------

    private static void Wiring(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the 1.19.27 anchor regression locates the production sources");
        if (root == null) return;

        string ladder = File.ReadAllText(Path.Combine(root, "AnchorCameraLadder.cs"));
        check(ladder.Contains("public static bool Authorized(bool hasRectTransform, AnchorCanvasMode mode)")
              && ladder.Contains("hasRectTransform && (mode == AnchorCanvasMode.Overlay || mode == AnchorCanvasMode.ScreenSpaceCamera)"),
            "the authorization is one expression over two facts, in the production policy");
        check(ladder.Contains("if (AnchorCanvasProjection.Authorized(hasRectTransform, mode))"),
            "and the ladder asks it exactly once, after the camera rules");
        // 1.19.28 re-point: the scale arithmetic is gone, and the triangle the geometry answer has to pass is now "a real camera (or a
        // real overlay canvas) -> the element's own four corners -> `ScreenBounds`". All three legs are asserted where they live.
        check(ladder.Contains("public static bool CameraLegitimate(bool hasCamera, AnchorCanvasMode mode)")
              && ladder.Contains("=> hasCamera || mode == AnchorCanvasMode.Overlay;"),
            "a null camera is legitimate only inside a genuine screen-space overlay canvas, in one expression in the production policy");
        check(ladder.Contains("return ScreenBounds.TryFromCorners(xs, ys, screenWidth, screenHeight, out rect);"),
            "the rectangle goes through `ScreenBounds` - finite, a real extent, on the window - like every other measurement in this build");
        check(ladder.Contains("if (AnchorCanvasProjection.Authorized(hasRectTransform, mode))"),
            "and the geometry answer is still authorized by the RectTransform fact plus a screen-space canvas, never by a camera");

        string panel = File.ReadAllText(Path.Combine(root, "PanelBehaviour.cs"));
        check(CountOf(panel, "= RectTransformUtility.WorldToScreenPoint(null, world);") == 1,
            "the camera-less overlay projection still exists exactly once, in the branch that is allowed to use it");
        check(panel.Contains("if (!measured && choice.Projection == AnchorProjection.Rect)")
              && panel.Contains("bool projectable = AnchorCanvasProjection.CameraLegitimate(projection != null, elementMode)")
              && panel.Contains("&& AnchorElementCanvasPolicy.CanProject(origin);"),
            "and the panel reaches the geometry answer only when the ladder chose it, refuses a null camera outside a real overlay canvas, and refuses a canvas that may not project at all");
        // 1.19.32 re-point: the corner projection moved into `ProjectPoint`, which is the same real-camera call for every non-null
        // camera (`RectTransformUtility.WorldToScreenPoint` forwards to `Camera.WorldToScreenPoint`) and the camera-less overlay call
        // only for a null one. The claim is unchanged and now holds for the manual canvas too, whose camera is not a `Canvas.worldCamera`.
        check(panel.Contains("Vector2 screen = ProjectPoint(camera, world);")
              && panel.Contains("Vector3 screen = camera.WorldToScreenPoint(world);")
              && CountOf(panel, "if (camera == null) return RectTransformUtility.WorldToScreenPoint(null, world);") == 1,
            "1.19.28: every corner of the element's own rect is projected by the real camera - the null camera 1.19.27 passed is gone from this path");
        check(panel.Contains("private static Canvas AncestorCanvas(Component element)")
              && CountOf(panel, "node.GetComponent<Canvas>()") == 1,
            "the canvas is asked of the component, once, by the one bounded walk both the camera candidate and the geometry use");
        check(!Code(panel).Contains("TryCast<Canvas"),
            "and never cast to: a Transform is a sibling component of the canvas, not a kind of it");

        string policy = File.ReadAllText(Path.Combine(root, "AnchorSamplePolicy.cs"));
        check(policy.Contains("case AnchorSampleFailure.NoProjection: return \"no camera that draws the icon could be shown to exist, and the element carries no RectTransform"),
            "the give-up wording names both missing answers, in one place");

        Console.WriteLine("WIRING: RELEASE_11927_CANVAS_GEOMETRY_ANCHOR (static source check; the authorization, the rectangle arithmetic and the sanity gate are the runtime assertions above)");
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    // One object a recursive walk would hand back. `includeInactive` is the argument the *production* walk passes to
    // `GetComponentsInChildren<RectTransform>`; the active state is the object's own, exactly as Unity reports it.
    private readonly struct WalkedObject
    {
        public readonly string Name;
        public readonly bool ActiveInHierarchy;
        public WalkedObject(string name, bool activeInHierarchy) { Name = name; ActiveInHierarchy = activeInHierarchy; }
    }

    // The walk, reduced to the one question this round is about: which object the production predicate selects. 1.19.28 drives
    // `AnchorElementMatch.IsBackgroundFade` itself - the same call the panel makes - so the name comparison is no longer written
    // twice (a copy can only ever agree with itself; that is how the wrong constant stayed green for four rounds).
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

    // A synthetic orthographic camera on the canvas plane: one local unit is `scale` pixels, and the element's pivot sits at
    // `pivotScreenX/Y` in Unity's own screen coordinates (y upwards). The rect comes from the layout's pivot and size and is then
    // reduced exactly like production does - corners through the camera, y mirrored into window pixels, `ScreenBounds` at the end.
    private static bool Screen(float pivotScreenX, float pivotScreenY, float pivotX, float pivotY, float width, float height,
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

    // Four already-projected corners in window pixels (y from the top), through the same production reduction.
    private static bool Corners(float[] xs, float[] ys, out LayoutRect rect) => Corners(xs, ys, WindowWidth, WindowHeight, out rect, out _);

    private static bool Corners(float[] xs, float[] ys, out LayoutRect rect, out AnchorScreenRect raw)
        => Corners(xs, ys, WindowWidth, WindowHeight, out rect, out raw);

    private static bool Corners(float[] xs, float[] ys, float windowWidth, float windowHeight,
        out LayoutRect rect, out AnchorScreenRect raw)
        => AnchorRectProjection.TryFromCorners(xs, ys, windowWidth, windowHeight, out rect, out raw);

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
