using System;
using System.Collections.Generic;
using System.Text;
namespace HawChow.GTFOCombatPanel;

// ---------------------------------------------------------------------------------------------------
// AnchorCameraLadder.cs - which camera really draws the flashlight icon, and how its world geometry is
// turned into window pixels.
//
// The bug this replaces: `PanelBehaviour.ReadAnchor` asked exactly one question -
// `layer.GuiLayerBase.GetWorldCamera()` - and treated a null answer as "the anchor does not exist".
// That field is only ever written by `GuiLayerBase.SetWorldCamera`, so a null there is a *state of the
// HUD*, not a timeout: the real log of one expedition answered null for thirty-nine minutes while the
// icon's renderer was present the whole time (`icon=ok`, `camera=null`). The results page in this same
// build already knew better (`ResultsDisplay.TryScreenRect` walks CM_PageBase.Camera ->
// canvas.worldCamera -> Camera.main -> Camera.allCameras), but that ladder was never applied to the
// in-level HUD, and a ladder is not a proof either: any camera in it will happily project a point that
// the camera never draws.
//
// What replaces it: a *validated* choice. Every candidate is asked two things the HUD cannot fake -
// is it alive (enabled, active in hierarchy), and does its culling mask contain the icon's own layer -
// and only a camera that passes both may project world geometry. The choice is ordered (the HUD's own
// world camera first, then the icon canvas' own `worldCamera`, then the game's GUI camera chain) and
// ties inside one source go to the camera with the greatest depth, i.e. the one drawn last over the
// others. 1.19.22 narrows that proof to what it really proves: a *canvas-drawn* element (one with a
// `CanvasRenderer`) is projected by its canvas, not culled through the camera mask, so a UI element
// under a screen-space-camera canvas resolves through that canvas' worldCamera without the mask proof.
//
// A screen-space-overlay canvas is the one case in which no camera is involved at all, and it is
// handled as what it is rather than as a missing camera: an overlay canvas draws its own UI elements
// with Unity's camera-less projection, so a UI element (one with a RectTransform) is projected that
// way. An element that is *world geometry* - a `SpriteRenderer` with a layer - is not drawn by an
// overlay canvas at all, so for it the honest answer used to be "no projection exists", which the
// caller reported as such instead of inventing coordinates.
//
// 1.19.27 replaces the gate that produced that dead end for both of this build's own anchors. The
// ladder used to authorize a camera-less answer only for an object carrying a `CanvasRenderer`, and
// neither of these two objects has one: the flashlight icon is a `SpriteStatusIcon` value struct with
// two `SpriteRenderer`s, and the row's `Background Fade` is a `SpriteRenderer` on an object that the
// prefab switches off. Fourteen recorded logs agree - `anchor=measured` never once, `flashlightActive`
// both false *and* true, two and three candidate cameras offered, and the same
// `no projection: no live camera in the read chain has the element's layer in its culling mask` every
// time, with `iconCanvasDrawn=false` sitting in the line as if it were the fault. It was not: a
// `CanvasRenderer` is a property of the prefab, not a state of the HUD. What an element inside a
// screen-space canvas really does have is a `RectTransform`, and Unity's own layout is then the
// projection: the pivot's screen point plus the element's own `rect`, scaled by the canvas' scale
// factor, *is* the drawn rectangle - no camera involved, because a screen-space canvas is composited
// in window pixels either way. That is one of the two answers, and the camera proof keeps its place:
// a camera that can be shown to draw the element still wins, and only when none can does the canvas
// geometry answer. What may *not* happen is the rectangle being taken on trust - it goes through the
// same `ScreenBounds` sanity gate (finite, a real extent, on the window) as every other measurement,
// and through the caller's own settle window afterwards.
// 1.19.28 fixes the three things that kept the ladder from ever answering on this build, and all three are read from
// `resources.assets` instead of guessed:
//
//   constant   the row's `Background Fade` was matched against the *log token* (`BackgroundFade` = "background-fade")
//              instead of the prefab's own object name (`BackgroundFadeObject` = "Background Fade"), so the walk could
//              never hit and `background-fade=missing` was this build's own constant, not a state of the HUD;
//   camera     the three candidates this build offered contain no camera that draws its HUD. The object that really
//              owns the HUD camera is the CellUI canvas: `GuiLayerBase.m_cellUICanvas : UI_Canvas`, whose
//              `m_UICamera : Camera` is what the prefab is rendered with. `GuiLayerBase.m_layerCanvas` is null for the
//              whole life of this build's HUD, and `m_cellUICanvas` is the same object that owns the HUD's
//              `m_rectTransform`, so "no Camera in the hierarchy" and "the HUD has a canvas" are not contradictory;
//   geometry   the canvas-geometry answer projected the element through a *null* camera, which is only the identity for
//              a real screen-space overlay canvas. This build's HUD carries no `Canvas` component at all, so the null
//              was never the right argument: the rectangle is the element's own `RectTransform` projected through a
//              real camera, and a null camera is refused outside a genuine overlay canvas (see
//              `AnchorCanvasProjection.CameraLegitimate`).
//
// Free of Unity and BepInEx on purpose: the decision is asserted headlessly in tests/GTFOCombatPanel.Smoke.
// ---------------------------------------------------------------------------------------------------

// The render mode of the canvas the icon belongs to, as this module needs to reason about it.
public enum AnchorCanvasMode
{
    // No canvas could be read at all. Not the same as overlay: nothing is known, so only a validated
    // camera may be used.
    Unknown = 0,
    // ScreenSpaceOverlay: drawn after every camera, without one.
    Overlay = 1,
    // ScreenSpaceCamera and WorldSpace both draw through `canvas.worldCamera`, which is the camera this
    // code must then validate.
    ScreenSpaceCamera = 2,
    WorldSpace = 3
}

// Where a candidate camera came from, in the order the ladder prefers them.
public enum AnchorCameraSource
{
    None = 0,
    // `GuiLayerBase.GetWorldCamera()` - the HUD's own answer, when it gives one.
    HudWorldCamera = 1,
    // `Canvas.worldCamera` of the canvas the icon lives under.
    CanvasWorldCamera = 2,
    // The game's GUI camera chain (`CM_PageBase.Camera`), the one the result page already uses.
    PageCamera = 3,
    // `Camera.main`.
    MainCamera = 4,
    // Any other live camera, ordered by depth.
    AllCameras = 5,
    // `Canvas.worldCamera` of the nearest ancestor canvas of the element itself (1.19.26). The HUD's own layer canvas can be
    // null on a frame while the icon already exists - a recorded 1.19.25 level logged `layerCanvas=none` - and the camera that
    // really draws the element is then only reachable from the element's own hierarchy. Offered as its own source so the log
    // still says which of the two canvases answered, and validated by the same culling-mask rule as every other world-sprite
    // candidate: a camera that merely happens to be an ancestor's is not proof that it draws the element.
    ElementCanvas = 6,
    // 1.19.28: `GuiLayerBase.m_cellUICanvas.m_UICamera` - the CellUI canvas' own camera, which is the one this build's HUD
    // is really rendered with. It is a candidate like every other one and is proven by the same culling-mask rule; the source
    // exists on its own so a log line can say that *this* camera answered rather than "the deepest camera that draws layer 5",
    // and so the rectangle a screen-space canvas is projected through can name it too (`AnchorCanvasProjection.CameraLegitimate`).
    // `GuiLayerBase.m_layerCanvas` (offered as `CanvasWorldCamera`) is null for the whole life of this build's HUD and
    // `GetWorldCamera()` (`HudWorldCamera`) always answers the same null, so this is the only one of the three that can be
    // a live camera at all.
    CellUiCamera = 7
}

// How the icon's geometry becomes window pixels.
public enum AnchorProjection
{
    None = 0,
    // Project through a validated camera - the only kind of projection a SpriteRenderer has.
    Camera = 1,
    // Unity's camera-less overlay projection, valid only for a RectTransform inside an overlay canvas that
    // really carries a `CanvasRenderer` (i.e. one the canvas itself draws).
    CameraLessOverlay = 2,
    // 1.19.27, corrected in 1.19.28: the element's own `RectTransform` inside a screen-space canvas. Unity laid that
    // rectangle out itself, so it *is* the drawn rectangle; what changed is how it becomes window pixels. 1.19.27 multiplied
    // the element's `rect` by a scale and placed it at the pivot's screen point - with a null camera whenever the canvas had
    // no `worldCamera`, which is only the identity for a real screen-space *overlay* canvas. This build's HUD has no `Canvas`
    // component at all, so 1.19.28 projects the element's four corners through a real camera (the CellUI canvas' `m_UICamera`
    // or any camera the mask proof accepted) and refuses a null camera outside a genuine overlay canvas. The rectangle still
    // has to pass the caller's `ScreenBounds` sanity gate before it may be used.
    Rect = 3
}

// One camera the caller could offer, reduced to the four facts the choice needs. `Alive` is
// "the component exists, is enabled and is active in the hierarchy"; a camera that fails it is never
// chosen however well its mask matches. `TargetTexture` is "this camera renders into a RenderTexture", which
// makes its `WorldToScreenPoint` answer in that texture's pixels instead of the window's.
public readonly struct AnchorCameraCandidate
{
    public readonly AnchorCameraSource Source;
    public readonly bool Alive;
    public readonly int CullingMask;
    public readonly int Depth;
    public readonly bool TargetTexture;

    public AnchorCameraCandidate(AnchorCameraSource source, bool alive, int cullingMask, int depth, bool targetTexture = false)
    {
        Source = source; Alive = alive; CullingMask = cullingMask; Depth = depth; TargetTexture = targetTexture;
    }

    public static AnchorCameraCandidate None { get { return new AnchorCameraCandidate(AnchorCameraSource.None, false, 0, 0); } }
}

// The verdict, with the words a log line needs: which source answered, and - when none did - which of
// the two possible reasons it was. `Usable` is the only thing a caller may branch on; everything else
// is diagnostics.
public readonly struct AnchorCameraChoice
{
    public readonly AnchorProjection Projection;
    public readonly AnchorCameraSource Source;
    public readonly int Index;      // index into the candidate list, -1 for a camera-less projection
    public readonly string Reason;

    public AnchorCameraChoice(AnchorProjection projection, AnchorCameraSource source, int index, string reason)
    {
        Projection = projection; Source = source; Index = index; Reason = reason ?? string.Empty;
    }

    public bool Usable { get { return Projection != AnchorProjection.None; } }

    public static AnchorCameraChoice Unusable(string reason)
    {
        return new AnchorCameraChoice(AnchorProjection.None, AnchorCameraSource.None, -1, reason);
    }

    // The one line the anchor's own diagnostic prints when it first resolves or first fails. It names
    // the source, so "the anchor locked" can never again be printed without saying through what.
    public static string Describe(AnchorCameraChoice choice)
    {
        switch (choice.Projection)
        {
            case AnchorProjection.Camera:
                return "projected through " + Name(choice.Source);
            case AnchorProjection.CameraLessOverlay:
                return "projected without a camera (screen-space overlay canvas)";
            case AnchorProjection.Rect:
                return "the element's own RectTransform layout rectangle, projected by the camera that draws its canvas (or without one, inside a real screen-space overlay canvas)";
            default:
                return "no projection: " + (choice.Reason.Length > 0 ? choice.Reason : "no candidate camera");
        }
    }

    // 1.19.27: the one word a log line prints for *how* a rectangle was obtained, so the three answers a reader
    // has to tell apart can never be confused again - a camera-proven projection, a rectangle computed from the
    // canvas' own geometry, and the configured `PositionX/PositionY` when neither of them answered at all.
    // `CameraLessOverlay` keeps its own word because it is a third, different proof (Unity's canvas draws the
    // element, and composites it without a camera), not a camera projection.
    public static string Provenance(AnchorProjection projection)
    {
        switch (projection)
        {
            case AnchorProjection.Camera: return "camera";
            case AnchorProjection.CameraLessOverlay: return "canvas-overlay";
            case AnchorProjection.Rect: return "canvas-geometry";
            default: return "config";
        }
    }

    // 1.19.29: the provenance of the rectangle the table is *using*, never of a rule that was only granted.
    //
    // The recorded 1.19.28 level printed `anchorSource=canvas-geometry` while both elements' own gates in the same line said
    // `refused` and the round's source was `config`. The field was `Provenance(choice.Projection)` - the *authorization* the
    // ladder handed out - and an authorization measures nothing by itself: the caller still has to find a camera to project
    // the four corners with, and that camera did not exist. One word therefore claimed an answer that had not happened. The
    // word for "nothing answered" is `config`, because that is what the table is really on, so that is what the line says
    // now; the rule that was tried keeps its own word beside it in `Authorization`.
    public static string Provenance(AnchorProjection projection, bool measured)
    {
        return measured ? Provenance(projection) : Provenance(AnchorProjection.None);
    }

    // What an attempt was authorized by, whatever came of it: the same word when that rule really produced the rectangle, and
    // `<word>(refused)` when the ladder granted the rule but the projection itself did not answer. `none` is the ladder's own
    // "no projection", which authorizes nothing at all.
    public static string Authorization(AnchorProjection projection, bool measured)
    {
        if (projection == AnchorProjection.None) return "none";
        return measured ? Provenance(projection) : Provenance(projection) + "(refused)";
    }

    // `Describe`, told whether the choice really measured anything.
    //
    // The single-argument form is the *authorization* sentence: it names the rule the ladder granted, which is what a lock
    // line wants to print. Printed after a refusal - the per-attempt failure line and the give-up line both do that - it read
    // as a statement of fact ("the element's own RectTransform layout rectangle, projected by the camera that draws its
    // canvas") while nothing had been projected at all. This overload keeps that sentence for a rule that answered, and says
    // out loud that it did not answer otherwise.
    public static string Describe(AnchorCameraChoice choice, bool measured)
    {
        if (choice.Projection == AnchorProjection.None || measured) return Describe(choice);
        switch (choice.Projection)
        {
            case AnchorProjection.Camera:
                return "authorized by " + Name(choice.Source) + ", but that projection answered no usable rectangle";
            case AnchorProjection.CameraLessOverlay:
                return "authorized by the screen-space overlay canvas, but that projection answered no usable rectangle";
            default:
                return "the canvas-geometry authorization (the element's own RectTransform, projected by the camera that draws "
                    + "its canvas or without one inside a real screen-space overlay canvas) was granted, but there was no camera "
                    + "to project it with, so it refused";
        }
    }

    // 1.19.35: the HUD's own manual canvas, used *instead of* the ladder's refusal, never beside a granted rule.
    //
    // The recorded 2026-11-13 levels printed `flashlightElementCanvas=missing`, `renderModeSource=layer-property`,
    // `authorization=canvas-geometry(refused)` and `anchorSource=config` on every attempt: the ladder's last word is decided
    // by the element's canvas mode, and with no ancestor `Canvas` that mode is `GuiLayerBase.CanvasRenderMode` - an enum on
    // the *layer*, i.e. what the HUD would like, not how anything composites this element. It answered `screen-space-camera`,
    // the geometry rule needs an overlay-or-screen-space-camera canvas *with a camera it can name*, and the only camera that
    // draws layer 5 was the switched-off RenderTexture camera the ladder refuses - so the branch that consults the manual
    // canvas was never even reached, although that chain is the one object that can be shown to composite this HUD.
    //
    // This factory is the *attempt*, not an answer: it hands the caller the geometry rule with the manual canvas as its
    // authority, and the caller refuses again unless every fact of that chain holds and the projected rectangle passes
    // `ScreenBounds`. `anchorSource=` therefore still reads `config` when the projection produces nothing.
    public static AnchorCameraChoice ManualCanvas()
        => new AnchorCameraChoice(AnchorProjection.Rect, AnchorCameraSource.None, -1,
            "the element has no ancestor Canvas, so the ladder's refusal rests on the layer's own mode; this HUD's manual "
            + "canvas is verified for this window and is the object that composites the element");

    // A one-word token per candidate source, so the per-candidate diagnostic stays one line and names a source rather than a
    // camera. `AnchorCameraChoice.Name` is the sentence form for the one camera that answered; this is the field form for all
    // of them.
    public static string Token(AnchorCameraSource source)
    {
        switch (source)
        {
            case AnchorCameraSource.HudWorldCamera: return "hud";
            case AnchorCameraSource.CanvasWorldCamera: return "canvas";
            case AnchorCameraSource.CellUiCamera: return "cellui";
            case AnchorCameraSource.ElementCanvas: return "element-canvas";
            case AnchorCameraSource.PageCamera: return "page";
            case AnchorCameraSource.MainCamera: return "main";
            case AnchorCameraSource.AllCameras: return "all";
            default: return "none";
        }
    }

    public static string Name(AnchorCameraSource source)
    {
        switch (source)
        {
            case AnchorCameraSource.HudWorldCamera: return "the HUD's own world camera";
            case AnchorCameraSource.CanvasWorldCamera: return "the icon canvas' worldCamera";
            case AnchorCameraSource.PageCamera: return "the game's GUI camera";
            case AnchorCameraSource.MainCamera: return "Camera.main";
            case AnchorCameraSource.AllCameras: return "the deepest camera that draws the icon's layer";
            case AnchorCameraSource.ElementCanvas: return "the element's own ancestor canvas worldCamera";
            case AnchorCameraSource.CellUiCamera: return "the CellUI canvas' own camera (GuiLayerBase.m_cellUICanvas.m_UICamera)";
            default: return "no camera";
        }
    }
}

public static class AnchorCameraLadder
{
    // Whether a culling mask contains a layer. Layer 0 is Unity's Default and is a legal layer, so the
    // check is on the layer's range and never on the layer number being non-zero.
    public static bool Renders(int cullingMask, int layer)
        => layer >= 0 && layer < 32 && (cullingMask & (1 << layer)) != 0;

    // 1.19.31: the preference order, stated as its own decision instead of being read off the enum's numbering.
    //
    // The enum's values were appended over several rounds (`ElementCanvas = 6`, `CellUiCamera = 7`) so that appending could not
    // silently re-order anything, and the side effect was that the game's GUI camera, `Camera.main` and *every other camera* were
    // all preferred over the two canvas cameras that actually belong to this HUD and to the element itself. A camera's name, its
    // depth or its position in this list is never evidence that it draws the element - only the culling mask is (`Select`) - but
    // among cameras that all pass that proof the one that is the element's *own* canvas has to come first, because it is the
    // object Unity composites that element with. The HUD's own two answers follow, and the cameras that belong to the game's
    // pages rather than to this HUD come last.
    public static int Rank(AnchorCameraSource source)
    {
        switch (source)
        {
            case AnchorCameraSource.ElementCanvas: return 1;    // the canvas the element itself lives under
            case AnchorCameraSource.HudWorldCamera: return 2;   // `GuiLayerBase.GetWorldCamera()`, the HUD's own answer
            case AnchorCameraSource.CanvasWorldCamera: return 3; // `m_layerCanvas.worldCamera`
            case AnchorCameraSource.CellUiCamera: return 4;      // `m_cellUICanvas.m_UICamera`
            case AnchorCameraSource.PageCamera: return 5;        // `CM_PageBase.Camera`, the game's GUI camera
            case AnchorCameraSource.MainCamera: return 6;
            case AnchorCameraSource.AllCameras: return 7;
            default: return 8;
        }
    }

    // Candidates in the order the ladder prefers them: `Rank` first, and within one source a window camera before a RenderTexture
    // one, then the camera drawn last (greatest depth), which is the one whose pixels the player actually sees.
    public static List<int> Order(IReadOnlyList<AnchorCameraCandidate> candidates)
    {
        var order = new List<int>();
        if (candidates == null) return order;
        for (int index = 0; index < candidates.Count; index++) order.Add(index);
        order.Sort((left, right) =>
        {
            AnchorCameraCandidate a = candidates[left], b = candidates[right];
            int bySource = Rank(a.Source).CompareTo(Rank(b.Source));
            if (bySource != 0) return bySource;
            // At the same source a window camera comes first: a camera that renders into a RenderTexture answers in that
            // texture's pixels, not in the window's, so it is the weaker answer of the two (see `Select`).
            int byTexture = (a.TargetTexture ? 1 : 0).CompareTo(b.TargetTexture ? 1 : 0);
            if (byTexture != 0) return byTexture;
            int byDepth = b.Depth.CompareTo(a.Depth);
            if (byDepth != 0) return byDepth;
            return left.CompareTo(right);
        });
        return order;
    }

    // The decision itself.
    //
    // Rules, in order:
    //   1. only a live camera whose culling mask contains the element's layer may project the element;
    //   2. a camera that renders into a RenderTexture is never used: its pixels are not window pixels, and no
    //      conversion from them has been verified in this build;
    //   3. the first such camera in the ladder's own order wins - this is the "the camera that really draws this
    //      canvas" test the old single-field read never made;
    //   4. nothing in the ladder qualifies, but Unity's own canvas draws the element inside an overlay canvas: the
    //      camera-less overlay projection is the correct one, and a null camera is then legitimate;
    //   5. nothing in the ladder qualifies either, but the element carries a `RectTransform` inside a screen-space
    //      canvas (overlay *or* screen-space-camera): the canvas' own geometry is the drawn rectangle, and no camera
    //      has to prove anything (1.19.27);
    //   6. otherwise there is no projection, and the reason says which of the dead ends it was.
    //
    // `elementLayer` is the element's own layer (-1 when it could not be read, which is treated as "unknown" and therefore as
    // a failure of the mask test). `canvasDrawn` is whether Unity's own canvas draws this object - that is, whether a
    // `CanvasRenderer` is on it; it decides rule 4 and the camera branch of rule 3 only. `hasRectTransform` is the fact that
    // authorizes rule 5, and it is deliberately a *separate* argument: a `CanvasRenderer` is a property of the prefab (neither
    // of this build's two anchors carries one, which is why the old gate was false on every recorded level), while a
    // `RectTransform` plus a screen-space canvas is enough to compute the rectangle Unity itself laid out. A world-space
    // canvas is not: its elements are drawn by a camera, so the mask proof keeps applying to it and rule 5 refuses it.
    //
    // The five-argument form is the proof-only ladder every earlier caller and test uses: it asks for rules 1-4, and then
    // rule 5 for an element nobody said carries a `RectTransform` (which is to say, not at all).
    public static AnchorCameraChoice Select(IReadOnlyList<AnchorCameraCandidate> candidates, int elementLayer,
        AnchorCanvasMode mode, bool canvasDrawn, out int inspected)
        => Select(candidates, elementLayer, mode, canvasDrawn, hasRectTransform: false, out inspected);

    public static AnchorCameraChoice Select(IReadOnlyList<AnchorCameraCandidate> candidates, int elementLayer,
        AnchorCanvasMode mode, bool canvasDrawn, bool hasRectTransform, out int inspected)
    {
        inspected = 0;
        List<int> order = Order(candidates);
        inspected = order.Count;
        // 1.19.22: an element Unity's own canvas draws (a CanvasRenderer on the object, i.e. a UI graphic) is
        // projected *by its canvas*, not by "a camera whose culling mask contains the element's layer". A
        // screen-space-camera canvas does that through its own worldCamera, which legitimately does not need the
        // the element's layer in its culling mask - the mask governs world geometry, and a canvas draw is not culled
        // through it. That camera must still be alive and answer in window pixels; the culling-mask proof stays for
        // genuine world-space renderers, and a world-space canvas owns no drawing camera of its own, so it keeps the
        // proof too. (The flashlight icon is none of these - a SpriteRenderer has no CanvasRenderer on it - so its
        // world-sprite rule is the loop below.)
        if (canvasDrawn && mode == AnchorCanvasMode.ScreenSpaceCamera)
        {
            for (int step = 0; step < order.Count; step++)
            {
                int index = order[step];
                AnchorCameraCandidate candidate = candidates[index];
                // 1.19.26: the element's own ancestor canvas is a canvas of the element too - when the HUD's layer canvas is
                // null on this frame it is the only one that can say which camera draws it.
                if (candidate.Source != AnchorCameraSource.CanvasWorldCamera
                    && candidate.Source != AnchorCameraSource.ElementCanvas) continue;
                if (!candidate.Alive) continue;
                if (candidate.TargetTexture) continue;
                return new AnchorCameraChoice(AnchorProjection.Camera, candidate.Source, index,
                    "the element is drawn by Unity's canvas, which a screen-space-camera canvas projects through its own worldCamera; a canvas draw is not culled through the camera's culling mask");
            }
        }
        for (int step = 0; step < order.Count; step++)
        {
            int index = order[step];
            AnchorCameraCandidate candidate = candidates[index];
            if (!candidate.Alive) continue;
            if (!Renders(candidate.CullingMask, elementLayer)) continue;
            // A camera that renders into a RenderTexture does not answer in window pixels: its `WorldToScreenPoint` is in that
            // texture's space, and nothing in this build has verified a conversion from it. Such a camera is therefore never
            // chosen - `Order` already puts window cameras ahead of it, and the reasons below name it when it is the only one.
            if (candidate.TargetTexture) continue;
            return new AnchorCameraChoice(AnchorProjection.Camera, candidate.Source, index,
                "the camera's culling mask contains the element's layer");
        }
        if (canvasDrawn && mode == AnchorCanvasMode.Overlay)
        {
            // A screen-space overlay canvas is composited after every camera; its own elements are drawn without one, so
            // `RectTransformUtility.WorldToScreenPoint(null, ...)` is the correct projection and not a missing camera.
            return new AnchorCameraChoice(AnchorProjection.CameraLessOverlay, AnchorCameraSource.None, -1,
                "the element is drawn by Unity's canvas inside a screen-space overlay canvas");
        }
        // 1.19.27, the rule this round exists for. A camera-less answer used to require a `CanvasRenderer` on the object, and
        // both of this build's anchors are the opposite of that: the flashlight icon is a `SpriteStatusIcon` value struct with
        // two `SpriteRenderer`s and the row's `Background Fade` is a `SpriteRenderer` the prefab switches off, so the old gate
        // was false on every frame of every recorded level and the anchor never once measured anything. A `RectTransform`
        // inside a screen-space canvas is the fact that really decides it: Unity laid that rectangle out itself and composites
        // the canvas in window pixels, so the pivot's screen point plus the element's own `rect` *is* the drawn rectangle. No
        // camera is asked for, and none is invented - the caller still has to pass the rectangle through `ScreenBounds`.
        if (AnchorCanvasProjection.Authorized(hasRectTransform, mode))
            return new AnchorCameraChoice(AnchorProjection.Rect, AnchorCameraSource.None, -1,
                "the element carries a RectTransform inside a screen-space canvas, so Unity's own canvas layout is the drawn rectangle and no camera has to prove anything");
        // A canvas-drawn element under a screen-space-camera canvas whose own camera was offered but could not be used
        // has that camera as its real dead end - the honest reason names it instead of a mask test that never applied
        // to the element.
        if (canvasDrawn && mode == AnchorCanvasMode.ScreenSpaceCamera
            && CanvasCameraOffered(candidates, out bool canvasTextureOnly))
            return AnchorCameraChoice.Unusable(canvasTextureOnly
                ? "the element's canvas is screen-space-camera and its own worldCamera renders into a RenderTexture, whose pixels are not window pixels, and no other live camera could be shown to draw the element"
                : "the element's canvas is screen-space-camera but its own worldCamera is not a live window camera, and no other live camera could be shown to draw the element");
        if (elementLayer < 0 || elementLayer >= 32)
            return AnchorCameraChoice.Unusable("the element's layer could not be read, so no camera can be shown to draw it");
        if (mode == AnchorCanvasMode.Overlay)
            return AnchorCameraChoice.Unusable(OnlyRenderTextures(candidates, elementLayer)
                ? "the element's canvas is screen-space overlay, which draws no world geometry, and the only cameras that could draw its layer render into a RenderTexture, whose pixels are not window pixels"
                : "the element's canvas is screen-space overlay - which draws no camera - while the element is world geometry; nothing can be shown to draw it");
        return AnchorCameraChoice.Unusable(OnlyRenderTextures(candidates, elementLayer)
            ? "the only live cameras whose culling mask contains the element's layer render into a RenderTexture, whose pixels are not window pixels"
            : "no live camera in the read chain has the element's layer in its culling mask");
    }

    // Whether a CanvasWorldCamera candidate was offered at all, and whether every such candidate renders into a
    // RenderTexture - the two dead ends the canvas-camera refusal above names apart.
    private static bool CanvasCameraOffered(IReadOnlyList<AnchorCameraCandidate> candidates, out bool textureOnly)
    {
        textureOnly = false;
        if (candidates == null) return false;
        bool any = false;
        for (int index = 0; index < candidates.Count; index++)
        {
            AnchorCameraCandidate candidate = candidates[index];
            // A canvas of the element's own hierarchy is a canvas camera too: when the HUD's layer canvas was null, this is
            // the candidate the dead-end message has to speak about.
            if (candidate.Source != AnchorCameraSource.CanvasWorldCamera
                && candidate.Source != AnchorCameraSource.ElementCanvas) continue;
            any = true;
            if (!candidate.TargetTexture) return true;
        }
        if (any) textureOnly = true;
        return any;
    }

    // Whether the cameras that could draw this layer are all RenderTexture cameras - the case in which the honest answer is
    // "no projection", not a rectangle measured in some texture's pixels.
    private static bool OnlyRenderTextures(IReadOnlyList<AnchorCameraCandidate> candidates, int layer)
    {
        if (candidates == null) return false;
        bool any = false;
        for (int index = 0; index < candidates.Count; index++)
        {
            AnchorCameraCandidate candidate = candidates[index];
            if (!candidate.Alive) continue;
            if (!Renders(candidate.CullingMask, layer)) continue;
            any = true;
            if (!candidate.TargetTexture) return false;
        }
        return any;
    }

    // The mask/layer test spelled out for the caller's own candidate list, so the log line can say how
    // many candidates were offered and how many were alive.
    public static string Inspect(IReadOnlyList<AnchorCameraCandidate> candidates)
    {
        if (candidates == null || candidates.Count == 0) return "candidates=0";
        int alive = 0;
        for (int index = 0; index < candidates.Count; index++) if (candidates[index].Alive) alive++;
        return "candidates=" + candidates.Count + ",alive=" + alive;
    }

    // 1.19.29: every candidate's own facts, in the ladder's own order.
    //
    // The 1.19.28 line printed only `candidates=4,alive=3`, and every report written from it had to *guess* which of the four
    // was the dead one - a count cannot name a camera. `cellUiMaskProof=` could not settle it either: it answered one word for
    // three different questions (was the CellUI camera offered at all, was it alive, did its culling mask contain the layer),
    // and the recorded level's `no` was read as a mask fact when it may have been a liveness fact. This formatter answers them
    // one candidate at a time. It is pure - every value is already in the candidate struct - so the regression suite drives it
    // directly; the Unity-side reads a candidate cannot carry (`enabled`, `activeInHierarchy`, the camera's name) are printed
    // beside it by the panel for the one camera the whole question is about.
    public static string Detail(IReadOnlyList<AnchorCameraCandidate> candidates, int elementLayer)
    {
        if (candidates == null || candidates.Count == 0) return "each=none";
        List<int> order = Order(candidates);
        var text = new StringBuilder("each=");
        for (int step = 0; step < order.Count; step++)
        {
            AnchorCameraCandidate candidate = candidates[order[step]];
            if (step > 0) text.Append(';');
            text.Append('#').Append(order[step]).Append(':')
                .Append(AnchorCameraChoice.Token(candidate.Source))
                .Append(",alive=").Append(candidate.Alive ? "yes" : "no")
                .Append(",mask=0x").Append(candidate.CullingMask.ToString("X8"))
                .Append(",rendersLayer=").Append(Renders(candidate.CullingMask, elementLayer) ? "yes" : "no")
                .Append(",depth=").Append(candidate.Depth)
                .Append(",texture=").Append(candidate.TargetTexture ? "yes" : "no");
        }
        return text.ToString();
    }
}

// ---------------------------------------------------------------------------------------------------
// 1.19.27: the canvas-geometry authorization, and the arithmetic that turns a RectTransform into window pixels.
// ---------------------------------------------------------------------------------------------------

// Whether an element may be measured from the canvas' own layout instead of from a camera that proves it draws it.
//
// The two facts are exactly the two the runtime reads: a `RectTransform` on the object (Unity laid the element out, and the
// declared rectangle is therefore real geometry rather than a container someone guessed at), and a canvas whose render mode
// is one of the two screen-space modes. Overlay has no camera at all; screen-space-camera has one, but its canvas is composited
// in window pixels all the same, so `RectTransformUtility.WorldToScreenPoint` answers in them either way. A world-space canvas
// is refused: its elements live in world units and only a camera can say where they land, which is the proof the ladder keeps.
//
// Free of Unity on purpose, so the three cases are asserted headlessly.
public static class AnchorCanvasProjection
{
    public static bool Authorized(bool hasRectTransform, AnchorCanvasMode mode)
        => hasRectTransform && (mode == AnchorCanvasMode.Overlay || mode == AnchorCanvasMode.ScreenSpaceCamera);

    // 1.19.28: whether the camera the geometry answer would project through may be used at all.
    //
    // A *null* camera is only the identity projection for a real screen-space `Overlay` canvas: that canvas is composited
    // after every camera and its elements are laid out in window pixels. Every other canvas - screen-space-camera above all -
    // is projected by a camera, and passing `null` there silently reinterprets window pixels as if the canvas were an overlay
    // one. This build's HUD has no `Canvas` component at all (the CellUI canvas is a `UI_Canvas` MonoBehaviour), so its mode is
    // only ever known from `GuiLayerBase.CanvasRenderMode`, and the null camera 1.19.27 used is exactly the case this refuses.
    public static bool CameraLegitimate(bool hasCamera, AnchorCanvasMode mode)
        => hasCamera || mode == AnchorCanvasMode.Overlay;
}

// ---------------------------------------------------------------------------------------------------
// 1.19.32: the HUD's own manual canvas - the authority for an element whose ancestors carry no `Canvas` at all.
// ---------------------------------------------------------------------------------------------------
//
// The 2026-11-13 Test levels answered `flashlightElementCanvas=missing` and `background-fadeElementCanvas=missing` while both
// elements' own `RectTransform`s read perfectly (210.59 x 110.16 / 300 x 109.92) and `background-fadeBounds=250x30` was even
// measurable. That is not a HUD which is still coming up: this game's whole in-level HUD is a *world-space* surface -
// `SpriteRenderer`s, world-space TMP, and a `UI_Canvas` MonoBehaviour - and carries no uGUI `Canvas` anywhere in either element's
// ancestry. The three installed mods that put text beside this exact row (`Hikaria.AccuracyTracker`, `Dinorush.StatDisplay`,
// `catrice.AccuracyShow`) never meet the question at all: they clone the row's own `Background Fade` and set a `localPosition`,
// so whatever draws the row draws their text too. This build cannot take that route - the table is IMGUI in window pixels - so
// it needs the one object that really turns *that* hierarchy into window pixels.
//
// The object is `GuiLayerBase.m_cellUICanvas : UI_Canvas`, and it is not a `Canvas`: its `m_UICamera` renders the canvas into a
// render texture sized `m_pixelWidth x m_pixelHeight`, and its `m_rectTransform` is the root the HUD is laid out under. A camera
// that renders into a render texture is not automatically a window camera - which is exactly why `AnchorCameraLadder.Select`
// refuses one - but when that texture's pixel size *is* the window's pixel size, and the canvas says it is laid out for exactly
// that many pixels, that camera's `WorldToScreenPoint` answers in the window's own pixel grid: same width, same height, same
// origin (bottom-left), same aspect. Nothing is converted and nothing is invented, because the two grids are the same grid.
// Every one of those facts is read per attempt, printed, and one that is missing or mismatched refuses: the recorded
// `authorization=canvas-geometry(refused)` may become a camera projection only on a HUD that can show the chain.
//
// What this deliberately does *not* use is `camera.enabled`. It is false on the recorded HUD
// (`cellUiMaskProof=...enabled=no,activeInHierarchy=yes`), because a manual canvas is rendered on demand rather than by the
// player loop: `enabled` says who calls `Render`, not whether the pixels land in the window. The structural proof above takes
// its place, and a camera whose chain cannot be shown refuses with the one fact that was missing.
public readonly struct AnchorManualCanvasFacts
{
    // The objects: `GuiLayerBase.m_cellUICanvas`, its `m_UICamera`, and whether the element really lives under the canvas' root.
    public readonly bool CanvasPresent;
    public readonly bool CameraPresent;
    public readonly bool ElementInsideCanvas;
    // The element's own layer, the camera's culling mask, and whether that mask contains that layer - the same proof the ladder
    // asks of every camera, because it is the proof that this camera draws this object at all.
    public readonly int ElementLayer;
    public readonly int CameraMask;
    public readonly bool CameraDrawsLayer;
    // The two pixel grids that have to be the same grid: the texture the camera renders into, and the window.
    public readonly int CameraPixelWidth, CameraPixelHeight;
    public readonly int WindowWidth, WindowHeight;
    // The canvas' own statement of the pixel grid it is laid out for (`UI_Canvas.m_pixelWidth/m_pixelHeight`, floats).
    public readonly float CanvasPixelWidth, CanvasPixelHeight;
    // The camera's viewport inside that texture: anything but the whole of it makes its pixels a sub-rectangle of the window.
    public readonly float ViewportX, ViewportY, ViewportWidth, ViewportHeight;
    // Read only so the line can say them. None of them is an input of the verdict.
    public readonly bool CameraEnabled, CameraActive, CameraOrthographic;
    public readonly float CameraOrthographicSize;

    public AnchorManualCanvasFacts(bool canvasPresent, bool cameraPresent, bool elementInsideCanvas, int elementLayer,
        bool cameraDrawsLayer, int cameraMask, int cameraPixelWidth, int cameraPixelHeight,
        float canvasPixelWidth, float canvasPixelHeight, int windowWidth, int windowHeight,
        float viewportX, float viewportY, float viewportWidth, float viewportHeight,
        bool cameraEnabled = false, bool cameraActive = false, bool cameraOrthographic = false, float cameraOrthographicSize = 0f)
    {
        CanvasPresent = canvasPresent; CameraPresent = cameraPresent; ElementInsideCanvas = elementInsideCanvas;
        ElementLayer = elementLayer; CameraDrawsLayer = cameraDrawsLayer; CameraMask = cameraMask;
        CameraPixelWidth = cameraPixelWidth; CameraPixelHeight = cameraPixelHeight;
        CanvasPixelWidth = canvasPixelWidth; CanvasPixelHeight = canvasPixelHeight;
        WindowWidth = windowWidth; WindowHeight = windowHeight;
        ViewportX = viewportX; ViewportY = viewportY; ViewportWidth = viewportWidth; ViewportHeight = viewportHeight;
        CameraEnabled = cameraEnabled; CameraActive = cameraActive;
        CameraOrthographic = cameraOrthographic; CameraOrthographicSize = cameraOrthographicSize;
    }

    // No manual canvas was offered. Every property below is then false, so `Project` answers exactly what it answered before
    // this struct existed.
    public static AnchorManualCanvasFacts None { get { return default; } }

    // The camera draws the whole texture - nothing is rendered into a corner of it.
    public bool FullViewport
        => About(ViewportX, 0f) && About(ViewportY, 0f) && About(ViewportWidth, 1f) && About(ViewportHeight, 1f);

    // The texture the camera renders into is the window, pixel for pixel.
    public bool RenderTargetMatchesWindow
        => CameraPresent && CameraPixelWidth > 0 && CameraPixelHeight > 0
           && CameraPixelWidth == WindowWidth && CameraPixelHeight == WindowHeight;

    // The canvas itself is laid out for exactly that many pixels.
    public bool CanvasPixelsMatchWindow
        => CanvasPresent && WindowWidth > 0 && WindowHeight > 0
           && Near(CanvasPixelWidth, WindowWidth) && Near(CanvasPixelHeight, WindowHeight);

    // The whole chain, and the only thing the decision branches on.
    public bool Verified
        => CanvasPresent && CameraPresent && ElementInsideCanvas && CameraDrawsLayer
           && FullViewport && RenderTargetMatchesWindow && CanvasPixelsMatchWindow;

    private static bool About(float value, float expected)
        => float.IsFinite(value) && Math.Abs(value - expected) <= 1e-4f;

    private static bool Near(float value, float expected)
        => float.IsFinite(value) && Math.Abs(value - expected) < 0.5f;
}

// The verdict and the words for it: which single fact refused, or the whole set when nothing did. Pure, so the recorded shape
// and every refusal are asserted headlessly instead of inferred from a log line.
public static class AnchorManualCanvasPolicy
{
    // The first fact that is *not* proven, as the sentence a failure line carries; `none` when the chain really is complete.
    public static string Refusal(AnchorManualCanvasFacts facts)
    {
        if (!facts.CanvasPresent)
            return "GuiLayerBase.m_cellUICanvas is absent, so this HUD names no manual canvas at all";
        if (!facts.CameraPresent)
            return "GuiLayerBase.m_cellUICanvas names no m_UICamera, so the manual canvas has no camera";
        if (!facts.ElementInsideCanvas)
            return "the element is not a descendant of GuiLayerBase.m_cellUICanvas.m_rectTransform, so that canvas' camera cannot be shown to draw it";
        if (!facts.CameraDrawsLayer)
            return "the manual canvas' camera does not draw the element's layer " + facts.ElementLayer
                + " (culling mask 0x" + facts.CameraMask.ToString("X8") + ")";
        if (!facts.FullViewport)
            return "the manual canvas' camera renders into part of its texture (viewport ("
                + Number(facts.ViewportX) + "," + Number(facts.ViewportY) + ","
                + Number(facts.ViewportWidth) + "," + Number(facts.ViewportHeight) + ")), so its pixels are not this window's";
        if (!facts.RenderTargetMatchesWindow)
            return "the manual canvas' camera renders into a " + facts.CameraPixelWidth + "x" + facts.CameraPixelHeight
                + " texture while the window is " + facts.WindowWidth + "x" + facts.WindowHeight
                + ", and no conversion from that texture's pixels has been verified";
        if (!facts.CanvasPixelsMatchWindow)
            return "the manual canvas is laid out for " + Number(facts.CanvasPixelWidth) + "x" + Number(facts.CanvasPixelHeight)
                + " pixels while the window is " + facts.WindowWidth + "x" + facts.WindowHeight;
        return "none";
    }

    // Every fact the decision was taken from, as one log segment: the numbers that refused a chain are printed even when it
    // refused, so the next run can be compared rather than guessed at.
    public static string Describe(AnchorManualCanvasFacts facts)
    {
        string viewport = "(" + Number(facts.ViewportX) + "," + Number(facts.ViewportY) + ","
            + Number(facts.ViewportWidth) + "," + Number(facts.ViewportHeight) + ")";
        return "verified=" + (facts.Verified ? "yes" : "no")
            + ",canvas=" + (facts.CanvasPresent ? "present" : "missing")
            + ",camera=" + (facts.CameraPresent ? "present" : "missing")
            + ",inside=" + (facts.ElementInsideCanvas ? "yes" : "no")
            + ",layer=" + facts.ElementLayer
            + ",mask=0x" + facts.CameraMask.ToString("X8")
            + ",drawsLayer=" + (facts.CameraDrawsLayer ? "yes" : "no")
            + ",viewport=" + viewport
            + ",target=" + facts.CameraPixelWidth + "x" + facts.CameraPixelHeight
            + ",canvasPixels=" + Number(facts.CanvasPixelWidth) + "x" + Number(facts.CanvasPixelHeight)
            + ",window=" + facts.WindowWidth + "x" + facts.WindowHeight
            + ",targetMatchesWindow=" + (facts.RenderTargetMatchesWindow ? "yes" : "no")
            + ",canvasPixelsMatchWindow=" + (facts.CanvasPixelsMatchWindow ? "yes" : "no")
            + ",enabled=" + (facts.CameraEnabled ? "yes" : "no")
            + ",activeInHierarchy=" + (facts.CameraActive ? "yes" : "no")
            + ",orthographic=" + (facts.CameraOrthographic ? "yes" : "no")
            + ",orthographicSize=" + Number(facts.CameraOrthographicSize)
            + (facts.Verified ? ",origin=manual-canvas" : ",origin=refused(" + Refusal(facts) + ")");
    }

    // 1.19.35: the word the one-shot line prints for one element's chain. There are three answers, not two: the chain was
    // never read on this attempt (a missing renderer returns before `CellUiRead.Facts`), the HUD really has no
    // `m_cellUICanvas` (`absent`), or it was read - and then `Describe` prints every fact it was read from. Folding the first
    // into `absent` claimed a read of the HUD that never happened.
    public static string Field(bool observed, AnchorManualCanvasFacts facts)
        => !observed ? "not-observed" : facts.CanvasPresent ? Describe(facts) : "absent";

    private static string Number(float value)
        => float.IsFinite(value) ? value.ToString("0.##") : (float.IsNaN(value) ? "NaN" : (value > 0f ? "+inf" : "-inf"));
}

// ---------------------------------------------------------------------------------------------------
// 1.19.32: which camera *is* which, across Il2Cpp wrappers.
// ---------------------------------------------------------------------------------------------------
//
// Il2CppInterop hands out a new managed wrapper for every read of a native object: `m_cellUICanvas.m_UICamera` is
// `new Camera(pointer)` on every access, and so is every element of `Camera.allCameras`. Two wrappers for one native camera are
// therefore never `ReferenceEquals`. The 2026-11-13 line is exactly that defect:
// `cellUiMaskProof=no(offered=no,alive=no,enabled=no,activeInHierarchy=yes,mask=0x00000000,rendersLayer=no,texture=no)` beside
// `each=#0:cellui,alive=no,mask=0x00000020,rendersLayer=yes,depth=-1,texture=yes` - the proof said the camera had not been
// offered while the same line listed it. `PanelBehaviour.CellUiDiagnostic` compared with `ReferenceEquals`, and
// `PanelBehaviour.Add`'s de-duplication used `List<Camera>.Contains`, which is the same reference comparison
// (`Il2CppObjectBase` overrides neither `object.Equals` nor `GetHashCode`, and `UnityEngine.Object`'s
// `Equals(Il2CppSystem.Object)` is not that method) - which is why `Camera.main` and the same camera out of `Camera.allCameras`
// could both be offered as `#1:main` and `#2:all` with identical mask and depth.
//
// The identity that is reliable is the native pointer, read through `Il2CppObjectBase.Pointer`. That read throws for a collected
// object; the caller catches it and passes 0, and 0 never matches anything - two unreadable pointers are not "the same camera".
// When the wanted camera's pointer is unknown, the candidate's own source is the fallback (the ladder offers exactly one
// `CellUiCamera`), and a *known* pointer that matches no candidate is a `-1` whatever source a candidate carries: a name is never
// allowed to override an identity.
public static class AnchorCameraIdentity
{
    public static bool Same(long left, long right) => left != 0L && left == right;

    // The index of the candidate that *is* the camera `source`/`pointer` describes, or -1 when it was not offered at all.
    public static int IndexOf(IReadOnlyList<AnchorCameraCandidate> candidates, IReadOnlyList<long> pointers,
        AnchorCameraSource source, long pointer)
    {
        if (candidates == null) return -1;
        if (pointer != 0L)
        {
            if (pointers == null) return -1;
            int count = Math.Min(candidates.Count, pointers.Count);
            for (int index = 0; index < count; index++) if (Same(pointers[index], pointer)) return index;
            return -1;
        }
        if (source == AnchorCameraSource.None) return -1;
        for (int index = 0; index < candidates.Count; index++) if (candidates[index].Source == source) return index;
        return -1;
    }
}

// The rectangle a UI element's own `RectTransform` really occupies on the window, once its four corners have been projected.
//
// 1.19.28 replaces the scale arithmetic 1.19.27 used (`pivot screen point + rect * scale`): that form is only correct for an
// axis-aligned element projected orthographically through the camera it names, and it had to invent a "pixels per local unit"
// number. The projection itself belongs to Unity, so the caller projects the four corners - `rect.xMin/xMax`, `rect.yMin/yMax`
// taken through the element's own transform, which already carries the pivot, every parent's scale and the canvas' scale - and
// this class only reduces those four points to the window rectangle they cover. Screen y counts upwards in the corners and
// downwards in the answer, exactly like every other measurement in this build.
//
// The result is *not* trusted: it goes through `ScreenBounds.TryFromCorners`, which refuses a rectangle built from a
// non-finite corner, one narrower or shorter than a pixel, one entirely off the window, and one that covers the whole window.
// A measurement that fails that gate is unmeasurable - never a small rectangle at the origin. The *raw* rectangle is answered
// beside the verdict (`AnchorScreenRect`) so a diagnostic line can print the numbers that were refused, which is how a
// rectangle that is merely off the window is told apart from one built out of NaN.
public readonly struct AnchorScreenRect
{
    public readonly float Left, Top, Width, Height;
    // Whether every one of the four numbers is a real number. A `false` here is the "the projection itself answered NaN"
    // case, printable as it stands instead of being folded into "unmeasurable".
    public readonly bool Finite;

    public AnchorScreenRect(float left, float top, float width, float height, bool finite)
    {
        Left = left; Top = top; Width = width; Height = height; Finite = finite;
    }

    // `none` is "no projection was attempted at all" - the default struct - while a projection that answered NaN prints the NaN
    // itself, which is exactly the difference a reader of the diagnostic line has to be able to see.
    public string Describe()
        => !Finite && Left == 0f && Top == 0f && Width == 0f && Height == 0f
            ? "none"
            : "(" + Number(Left) + "," + Number(Top) + "," + Number(Width) + "," + Number(Height) + ")";

    private static string Number(float value)
        => float.IsFinite(value) ? value.ToString("0.##") : (float.IsNaN(value) ? "NaN" : (value > 0f ? "+inf" : "-inf"));
}

public static class AnchorRectProjection
{
    // The rectangle Unity's own layout declares for an element of `width` x `height` with this pivot, in the element's own
    // local space: already relative to the pivot, exactly what `RectTransform.rect` reports. `pivot 1,0.5` on a 300 x 54.96
    // element gives `xMin = -300, xMax = 0, yMin = -27.48, yMax = 27.48`, which is the shape this build's own prefab has.
    public static bool TryRectFromPivot(float pivotX, float pivotY, float width, float height,
        out float minX, out float maxX, out float minY, out float maxY)
    {
        minX = maxX = minY = maxY = 0f;
        if (!float.IsFinite(pivotX) || !float.IsFinite(pivotY) || !float.IsFinite(width) || !float.IsFinite(height)) return false;
        float w = Math.Abs(width), h = Math.Abs(height);
        if (!(w > 0f) || !(h > 0f)) return false;
        minX = -pivotX * w; maxX = (1f - pivotX) * w;
        minY = -pivotY * h; maxY = (1f - pivotY) * h;
        return true;
    }

    // The window rectangle the projected corners cover, *before* any judgement, so a refused measurement can still print the
    // numbers it was refused for.
    public static AnchorScreenRect Raw(IReadOnlyList<float> xs, IReadOnlyList<float> ys)
    {
        if (xs == null || ys == null || xs.Count == 0 || xs.Count != ys.Count)
            return new AnchorScreenRect(float.NaN, float.NaN, float.NaN, float.NaN, false);
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        bool finite = true;
        for (int corner = 0; corner < xs.Count; corner++)
        {
            float x = xs[corner], y = ys[corner];
            if (!float.IsFinite(x) || !float.IsFinite(y)) { finite = false; continue; }
            x0 = Math.Min(x0, x); x1 = Math.Max(x1, x);
            y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
        }
        if (!finite) return new AnchorScreenRect(float.NaN, float.NaN, float.NaN, float.NaN, false);
        return new AnchorScreenRect(x0, y0, x1 - x0, y1 - y0, true);
    }

    // The verdict and the numbers together: `true` only when the rectangle may be used, and `raw` always. The gate is
    // `ScreenBounds` itself - the same one every renderer-bounds measurement in this build goes through - so nothing here
    // loosens it; this class only says what the four corners were.
    public static bool TryFromCorners(IReadOnlyList<float> xs, IReadOnlyList<float> ys,
        float screenWidth, float screenHeight, out LayoutRect rect, out AnchorScreenRect raw)
    {
        raw = Raw(xs, ys);
        return ScreenBounds.TryFromCorners(xs, ys, screenWidth, screenHeight, out rect);
    }
}

// ---------------------------------------------------------------------------------------------------
// The two native elements this build may anchor on, and what follows from a change between them.
// ---------------------------------------------------------------------------------------------------

// Which native element a measured rectangle came from. `Flashlight` is the exact icon the table has always hung under;
// `BackgroundFade` is the same equipment row's own `Background Fade`, used only when the icon itself cannot be measured.
public enum AnchorElementSource
{
    None = 0,
    Flashlight = 1,
    BackgroundFade = 2
}

// The names this build matches, and nothing else. The two prefab object names are recorded for the reader; the lookup matches
// the background's exact name only, from `m_iconDisplay` downwards and with inactive children included - the same walk
// AoiYuki's mods do (`GetComponentsInChildren<RectTransform>(true)`), which is what makes the name unique: `resources.assets`
// holds three objects called `Background Fade` and only the one under this display is the equipment row's.
public static class AnchorElementNames
{
    // The two words every log line uses for a source.
    public const string Flashlight = "flashlight";
    public const string BackgroundFade = "background-fade";
    // The prefab object names.
    public const string BackgroundFadeObject = "Background Fade";
    public const string FlashlightEnabledObject = "FlashLightIcon_:Enabled";
    public const string FlashlightDisabledObject = "FlashLightIcon_Disabled";
}

// The order one measurement attempt walks, and the one consequence a change of source has for the caller.
public static class AnchorSourceLadder
{
    // The flashlight icon is measured first; the row's background only when the icon cannot be measured at all. That order is
    // what keeps a working table exactly where it has always been - the fallback is a fallback, never a preference.
    public static AnchorElementSource First(bool flashlightMeasured, bool backgroundMeasured)
        => flashlightMeasured ? AnchorElementSource.Flashlight
         : backgroundMeasured ? AnchorElementSource.BackgroundFade
         : AnchorElementSource.None;

    // The two elements do not share a position: the background's right edge is 66.65 local units left of the flashlight icon's
    // and its bottom edge 6.45 units above it. A sample that changes source is therefore a jump, not drift, and the settle
    // window has to start again - measuring the jump as movement and then locking on the far side of it would freeze the
    // table on a rectangle read while the HUD was still coming up.
    public static bool RestartsSettle(AnchorElementSource previous, AnchorElementSource current)
        => previous != current && previous != AnchorElementSource.None && current != AnchorElementSource.None;

    // The source word a log line prints. `None` is the configured position: no native element answered this round.
    public static string Name(AnchorElementSource source)
    {
        switch (source)
        {
            case AnchorElementSource.Flashlight: return AnchorElementNames.Flashlight;
            case AnchorElementSource.BackgroundFade: return AnchorElementNames.BackgroundFade;
            default: return "config";
        }
    }

    // 1.19.28: *what rectangle* the sample is a measurement of, in the words a log line prints. The source word above answers
    // "which element of the row", and on its own it was read as "the flashlight icon" - which is exactly what this build is
    // not measuring any more. The three answers a reader has to be able to tell apart are:
    //
    //   the icon row's `PUI_InventoryIconDisplay` layout rectangle   the `RectTransform` of `m_iconDisplay`, projected by a
    //                                                                camera. It is the row's declared 210.59 x 110.16 area, not
    //                                                                the 65 px sprite inside it (32.5 world units at PPU 2) and
    //                                                                not the 100 x 100 icon object either;
    //   the row's `Background Fade` sprite geometry                    that object's own `SpriteRenderer` quad (250 x 30 for the
    //                                                                sliced gradient), projected by a camera;
    //   the configured position                                        `PositionX/PositionY`, when neither could be projected.
    public static string GeometryName(AnchorElementSource source)
    {
        switch (source)
        {
            case AnchorElementSource.Flashlight:
                return "the icon row's PUI_InventoryIconDisplay layout rectangle";
            case AnchorElementSource.BackgroundFade:
                return "the row's Background Fade SpriteRenderer geometry";
            default:
                return "the configured PositionX/PositionY";
        }
    }
}

// 1.19.28: the one name comparison the background walk makes, as production code the tests can drive.
//
// The defect this replaces was a one-word mismatch inside `PanelBehaviour`: the walk compared the object's name against
// `AnchorElementNames.BackgroundFade` - the *log token* "background-fade" - instead of `AnchorElementNames.BackgroundFadeObject`,
// the prefab's own object name "Background Fade". Nothing threw, nothing logged a failed lookup, and the walk reported
// `background-fade=missing` on every frame of every recorded level: the constant was wrong, not the HUD. Because the two
// constants live ten lines apart and both look right at the call site, the comparison now exists in exactly one place and the
// regression suite drives *this* method rather than a copy of the walk - a test that re-implements the walk can only ever
// agree with itself.
public static class AnchorElementMatch
{
    public static bool IsBackgroundFade(string objectName)
        => string.Equals(objectName, AnchorElementNames.BackgroundFadeObject, StringComparison.Ordinal);
}

// 1.19.28: which of `SpriteStatusIcon`'s two renderers the HUD is drawing, and why neither of them is a source of geometry.
//
// `Enabled` and `Disabled` are the two `SpriteRenderer`s of the prefab's `FlashLightIcon_:Enabled` / `FlashLightIcon_Disabled`
// objects. Both exist for the whole life of the HUD (`SetActiveSprite(bool)` flips their own `enabled` flags; it creates
// neither), so the `Enabled != null ? Enabled : Disabled` read this build makes can only ever answer `Enabled` - the fallback
// is unreachable and is *not* a state of the HUD. Whichever one is drawn, the object it lives on is the 65 px sprite
// (32.5 world units at 2 pixels per unit), which is not the icon row's layout rectangle and must therefore never be handed to
// the anchor as one.
public enum AnchorIconSprite
{
    Enabled = 1,
    Disabled = 2
}

public static class AnchorFlashlightIcon
{
    // Which renderer the HUD is drawing, from the game's own per-renderer flags. Neither on - a HUD still coming up - answers
    // `Enabled`, the field this build reads its layer from; it is a diagnostic word only and never geometry.
    public static AnchorIconSprite Drawn(bool enabledRendererOn, bool disabledRendererOn)
        => enabledRendererOn || !disabledRendererOn ? AnchorIconSprite.Enabled : AnchorIconSprite.Disabled;

    // Whether the `?? Disabled` fallback can be answered at all: it cannot while the `Enabled` renderer exists, which is always.
    public static bool FallbackReachable(bool enabledRendererPresent) => !enabledRendererPresent;

    public static string Name(AnchorIconSprite sprite)
        => sprite == AnchorIconSprite.Disabled ? AnchorElementNames.FlashlightDisabledObject : AnchorElementNames.FlashlightEnabledObject;
}

// The rectangle a `SpriteRenderer` really draws, in the renderer's own local space.
//
// Sliced/tiled drawing takes its extent from `SpriteRenderer.size` and not from the sprite's own size, and the sprite's pivot
// decides where the transform's origin sits inside that extent. Both facts together are what describe the drawn quad of an
// *inactive* sprite, whose `Renderer.bounds` are not guaranteed to be that quad - the row's `Background Fade` is exactly such
// an object (draw mode sliced, 250 x 30, pivot 1,0.5).
public readonly struct AnchorQuad
{
    public readonly float MinX, MinY, MaxX, MaxY;

    public AnchorQuad(float minX, float minY, float maxX, float maxY)
    {
        MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
    }

    // A quad that is finite and has a real extent in both directions. Anything else is not geometry, and the caller must treat
    // it as unmeasurable rather than as a small rectangle at the origin.
    public bool Valid
    {
        get
        {
            return float.IsFinite(MinX) && float.IsFinite(MinY) && float.IsFinite(MaxX) && float.IsFinite(MaxY)
                && MaxX > MinX && MaxY > MinY;
        }
    }

    public float Width { get { return MaxX - MinX; } }
    public float Height { get { return MaxY - MinY; } }
}

public static class AnchorSpriteQuad
{
    // `pivotX/pivotY` are the sprite's pivot as a fraction of its own extent - 1,0.5 for `UI_Gradient_64x64`, 0.5,0.5 for the
    // flashlight icons - which is what `Sprite.bounds` gives without needing the pixels-per-unit. A flip mirrors the quad about
    // the transform's origin, exactly as Unity mirrors the mesh.
    public static AnchorQuad FromSprite(float width, float height, float pivotX, float pivotY, bool flipX = false, bool flipY = false)
    {
        float w = Math.Abs(width);
        float h = Math.Abs(height);
        if (!float.IsFinite(w) || !float.IsFinite(h) || w <= 0f || h <= 0f) return default;
        if (!float.IsFinite(pivotX)) pivotX = 0.5f;
        if (!float.IsFinite(pivotY)) pivotY = 0.5f;
        float minX = -pivotX * w, maxX = (1f - pivotX) * w;
        float minY = -pivotY * h, maxY = (1f - pivotY) * h;
        if (flipX) { float swap = minX; minX = -maxX; maxX = -swap; }
        if (flipY) { float swap = minY; minY = -maxY; maxY = -swap; }
        return new AnchorQuad(minX, minY, maxX, maxY);
    }
}

// ---------------------------------------------------------------------------------------------------
// 1.19.30, corrected in 1.19.31: the element's *own* canvas is the authority for the geometry answer.
// ---------------------------------------------------------------------------------------------------

// Which canvas the geometry answer reasons about, and which camera it may be projected through.
//
// The recorded 1.19.28/1.29 levels could not answer either question, because this build only ever read:
//   * the render mode from `GuiLayerBase.CanvasRenderMode` - an enum on the *layer*, used because `m_layerCanvas` is null for
//     this HUD's whole life. That is not a canvas fact at all: it says what mode the layer *would* like, not what the object
//     the element lives under really is;
//   * the projection camera from `m_cellUICanvas.m_UICamera` and `m_layerCanvas.worldCamera` - the first of which is the object
//     the recorded level shows was not alive, and the second of which cannot exist.
//
// `RectTransformUtility.WorldToScreenPoint` documents exactly one camera for a canvas element: the canvas' own `worldCamera`, and
// `null` - the identity - for a genuine screen-space overlay canvas. The object that names it is the element's own ancestor
// `Canvas` (resolved to its root, because a nested canvas inherits its root's mode and camera), which this build already walks to
// for its camera *candidate* but never asked for the two facts it is authoritative about. Both functions below are pure, so the
// whole decision - including the case with no canvas and the case with no camera - is asserted headlessly.
//
// 1.19.31 fixes two defects of the first attempt at this: a live camera was preferred over the canvas (which on an *overlay*
// canvas double-projects window pixels through a camera), and the mode/camera could be read from two different canvases (the
// root for the mode, the nearest for the camera). Now there is no camera input at all: the canvas decides, and one canvas identity
// answers both questions.
public static class AnchorElementCanvasPolicy
{
    // Which render mode the geometry answer reasons about: the element's own canvas when there is one, and the caller's fallback
    // (the layer's own enum, then `Unknown`) only when the hierarchy really carries no `Canvas` at all.
    //
    // This is a *stronger* fact than the fallback, never a weaker one: a `Canvas` component has a real `renderMode` that Unity
    // itself uses to composite the element, while the layer enum is what the HUD asked for. It also only ever answers for objects
    // that have a canvas, so a hierarchy with none keeps exactly the behaviour it had.
    public static AnchorCanvasMode Mode(bool ancestorCanvasPresent, AnchorCanvasMode ancestorMode, AnchorCanvasMode fallbackMode)
        => ancestorCanvasPresent ? ancestorMode : fallbackMode;

    // What the geometry answer may project with.
    //
    // 1.19.31: the *canvas* decides, and nothing else. There is deliberately no "the ladder had a camera" input any more:
    //
    //   * on a genuine screen-space **overlay** canvas the element's world coordinates are already window pixels, so passing any
    //     camera (a live one included) makes `RectTransformUtility.WorldToScreenPoint` project pixels through a camera - a double
    //     projection that lands nowhere near the element. The overlay answer is therefore "no camera", always;
    //   * on a **screen-space-camera** canvas the only camera that may project the element is *that canvas'* camera. A camera the
    //     ladder accepted for some other reason (its mask happened to contain the layer, or it is the HUD's CellUI camera while
    //     the element sits under a different canvas) is a different object and says nothing about this element's canvas, so it is
    //     not used as a substitute: the caller refuses and keeps `PositionX/PositionY` instead of moving the table to a guess.
    public enum CameraOrigin
    {
        // Nothing can be projected: the element has no ancestor `Canvas` at all, its canvas is world-space, or its mode could not
        // be read. The geometry answer then has no rectangle.
        None = 0,
        // A genuine screen-space overlay canvas: Unity composites it in window pixels, so `null` is the correct camera and must be
        // the one passed - this is the one case where a live camera may exist and still may not be used.
        Overlay = 1,
        // The element's canvas names a live camera that does not render into a RenderTexture: project through exactly that camera.
        ElementCanvas = 2,
        // The canvas names a camera that must not be used - it is not alive, or it renders into a RenderTexture whose pixels are
        // not window pixels. Named apart from `None` so the log can say *why* there is nothing to project with.
        ElementCanvasRefused = 3,
        // 1.19.32: the element has no ancestor `Canvas` at all - which is this game's HUD, not a HUD that is still coming up - and
        // the projection is the HUD's own manual canvas instead: `GuiLayerBase.m_cellUICanvas : UI_Canvas`, whose `m_UICamera`
        // renders that canvas into a render texture of the window's own pixel size. See `AnchorManualCanvasPolicy`.
        ManualCanvas = 4,
        // The manual canvas exists and names a camera, but the chain could not be shown to land on the window pixel grid. Named
        // apart from `None` and from `ElementCanvasRefused` because the repair is a different one.
        ManualCanvasRefused = 5
    }

    // The decision. `elementMode` is the element's *own* canvas render mode (`Mode` above, or the layer fallback when the element
    // has no canvas), and the three `canvas*` facts describe that same canvas' `worldCamera` - one object, one identity, so the
    // mode and the camera can never come from two different canvases.
    //
    // 1.19.32: the element's ancestors may carry no `Canvas` at all - which is this game's HUD, whose whole HUD is SpriteRenderers
    // and world-space TMP under `UI_Canvas`, not uGUI - and then the object that composites it is that `UI_Canvas` itself, whose
    // `m_UICamera` renders it into a render texture. That is a *different* authority from a `Canvas`, so it is asked as its own
    // set of facts (`AnchorManualCanvasFacts`) and answered with its own words; a real ancestor canvas always wins, because a
    // canvas the element really lives under says how Unity itself composites that element.
    public static CameraOrigin Project(bool elementCanvasPresent, AnchorCanvasMode elementMode,
        bool canvasCameraPresent, bool canvasCameraAlive, bool canvasCameraWindow)
        => Project(elementCanvasPresent, elementMode, canvasCameraPresent, canvasCameraAlive, canvasCameraWindow,
            AnchorManualCanvasFacts.None);

    public static CameraOrigin Project(bool elementCanvasPresent, AnchorCanvasMode elementMode,
        bool canvasCameraPresent, bool canvasCameraAlive, bool canvasCameraWindow, AnchorManualCanvasFacts manual)
    {
        if (elementCanvasPresent)
        {
            switch (elementMode)
            {
                case AnchorCanvasMode.Overlay:
                    return CameraOrigin.Overlay;
                case AnchorCanvasMode.ScreenSpaceCamera:
                    if (!canvasCameraPresent) return CameraOrigin.None;
                    return canvasCameraAlive && canvasCameraWindow ? CameraOrigin.ElementCanvas : CameraOrigin.ElementCanvasRefused;
                default:
                    // World-space elements live in world units and only a camera can say where they land; a mode nobody could read proves
                    // nothing either. Neither is geometry this rule may turn into window pixels.
                    return CameraOrigin.None;
            }
        }
        // No ancestor `Canvas`: the only object that can still be shown to composite this element is the HUD's own manual canvas.
        // A manual canvas that is absent is `None` - exactly the answer this overload gave before it existed - and one that is
        // present but cannot be shown to land on this window is its own refusal, never somebody else's camera.
        if (manual.CanvasPresent) return manual.Verified ? CameraOrigin.ManualCanvas : CameraOrigin.ManualCanvasRefused;
        return CameraOrigin.None;
    }

    // Whether the caller must pass a camera to `RectTransformUtility.WorldToScreenPoint`. Kept beside the decision so the verdict
    // and the argument can never disagree: only a screen-space-camera canvas with a live window camera, or the manual canvas'
    // own camera, passes one. An overlay canvas never does.
    public static bool PassesCamera(CameraOrigin origin)
        => origin == CameraOrigin.ElementCanvas || origin == CameraOrigin.ManualCanvas;

    public static string Name(CameraOrigin origin)
    {
        switch (origin)
        {
            case CameraOrigin.Overlay: return "the element's canvas is a screen-space overlay canvas, which is composited without a camera";
            case CameraOrigin.ElementCanvas: return "the element's own canvas worldCamera";
            case CameraOrigin.ElementCanvasRefused: return "the element's own canvas worldCamera (not a live window camera)";
            case CameraOrigin.ManualCanvas: return "the HUD's own manual canvas (GuiLayerBase.m_cellUICanvas.m_UICamera, rendering into a render texture of the window's size)";
            case CameraOrigin.ManualCanvasRefused: return "the HUD's own manual canvas, whose camera could not be shown to answer in window pixels";
            case CameraOrigin.None: return "none";
            default: return "none";
        }
    }

    // The one word a log line prints for where the render mode came from, so `canvasRenderMode=` can no longer be read as a
    // property of a `Canvas` when it was an enum on the layer.
    public static string SourceName(bool ancestorCanvasPresent) => ancestorCanvasPresent ? "element-canvas" : "layer-property";

    // Whether that origin may produce a rectangle at all: the answers Unity (or the game's manual canvas) really composites an
    // element with. Everything else (no canvas, world-space, a canvas whose camera is unusable, a manual canvas that cannot be
    // tied to this window) is a refusal, and the caller keeps its configured position.
    public static bool CanProject(CameraOrigin origin)
        => origin == CameraOrigin.Overlay || origin == CameraOrigin.ElementCanvas || origin == CameraOrigin.ManualCanvas;

    // 1.19.35: whether the manual canvas may override the *ladder's* refusal for this element.
    //
    // `AnchorCameraLadder.Select` ends with "no projection" whenever no live window camera can be shown to draw the element's
    // layer and the element's canvas mode is not one the geometry rule accepts. With no ancestor `Canvas` that mode is the
    // layer's own enum - `GuiLayerBase.CanvasRenderMode` - which is not a fact about how the element is composited at all, and
    // the recorded 2026-11-13 levels are exactly that shape: `renderModeSource=layer-property`, `layerCanvas=none`, both
    // element canvases `missing`, the only camera that draws layer 5 switched off and rendering into a RenderTexture.
    //
    // The manual canvas is a *stronger* fact than that enum: it is the object that composites the HUD, and `Verified` means
    // every part of its chain was read and matches this window (the element really lives under the canvas' root, that camera
    // draws the element's layer, it fills its texture, the texture is the window's pixel size, and the canvas is laid out for
    // that same size). So it may authorize an *attempt* where the ladder's last word rests on the layer property - and nowhere
    // else:
    //   * an element with a real ancestor `Canvas` keeps that canvas' rule (`false` here), because a canvas the element lives
    //     under is what Unity itself composites it with;
    //   * a manual chain that is not verified authorizes nothing (`false`), so the caller refuses with the fact that was
    //     missing rather than projecting through a camera nobody proved draws it;
    //   * nothing about the rectangle is relaxed: the attempt still has to project four corners and pass `ScreenBounds`.
    public static bool ManualAuthorized(bool elementCanvasPresent, AnchorManualCanvasFacts manual)
        => !elementCanvasPresent && manual.Verified;
}

// Whether the camera a round proved for the equipment column may still project through this poll.
//
// This is the decision `PanelBehaviour.BandCameraAlive` makes, kept here - free of Unity and of BepInEx - so that it can be
// asserted headlessly instead of only being read back out of the source. The facts it takes are exactly the ones that method
// reads: whether this round proved a camera at all (`proved`), whether that proof came from the HUD's own manual canvas
// (`manualCanvasProved`, set only from `AnchorCameraChoice.ManualCanvas`), the camera's own `isActiveAndEnabled`, its
// `gameObject.activeInHierarchy`, and whether those facts could be read at all (`readable` false = the wrapper threw).
//
// The rule, and why it is typed rather than general: an enabled, active camera is alive as it always was. A *disabled* camera
// is alive only when this round proved it through the manual-canvas chain AND it is still active in the hierarchy - because a
// manual canvas is rendered on demand, so `enabled` names who calls `Render` rather than whether the pixels land in the window
// (log 2827: `enabled=no, activeInHierarchy=yes`). Nothing else is relaxed: a camera no round proved, an ordinary disabled
// camera, a camera whose object is no longer active, and an unreadable wrapper all answer false.
//
// A `null` camera is deliberately not this function's business: `null` is the *proved* screen-space-overlay answer (Unity
// composites without a camera), and the caller answers `true` for it before asking anything here.
public static class BandCameraLiveness
{
    public static bool Alive(bool proved, bool manualCanvasProved, bool enabledAndActive, bool activeInHierarchy, bool readable)
    {
        if (!readable) return false;
        if (enabledAndActive) return true;
        return proved && manualCanvasProved && activeInHierarchy;
    }
}
