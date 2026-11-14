using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// 1.19.26: the flashlight anchor's second source (the row's own `Background Fade`) was unreachable for a whole expedition
// because the lookup remembered its first miss forever.
//
// The recorded 1.19.25 level logged, on its very first sampling attempt:
//
//   flashlight anchor camera: ... background-fade=missing ... no projection: no live camera in the read chain has the
//   element's layer in its culling mask
//   the flashlight anchor could not be measured yet (...attempt 1 of 12, triedBackgroundFade=yes(missing), source=config)
//
// and from then on `AnchorElementLookup` answered `yes(missing)` from its cache without touching the hierarchy, so the row
// that appeared a moment later could never be found - while the twelve attempts of the round went on spending themselves on
// the icon alone. What is asserted here is the cache's new contract (a miss is retried a bounded, spaced number of times and
// is then retired; a hit is answered once per display; a walk that threw claims nothing) and the rules that were already right
// and must stay right: a SpriteRenderer in layer 5 has no camera-less projection, a screen-space-camera canvas with no usable
// camera is never answered with the overlay projection, and the second source is only ever used when it was really measured.
internal static class Release11926AnchorScenarios
{
    private const int IconLayer = 5;

    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        ALateElementIsFoundOnTheMissesOwnBudget(check);
        TheMissBudgetCannotBecomeAPerFrameScan(check);
        ASpriteWithNoCameraIsRefused(check);
        OnlyARealOverlayMayProjectWithoutACamera(check);
        TheFallbackIsOnlyUsedWhenItWasReallyMeasured(check);
        TheAncestorCanvasIsAskedOfTheComponentNotCast(check);
        TheLatchAndTheRoundAreUntouched(check);
        Wiring(check);
    }

    // Attempt 1 of a level happens before the row exists; attempt 2 happens after it appeared. The miss must not stand, and the
    // hit that follows it must be cached exactly as before.
    private static void ALateElementIsFoundOnTheMissesOwnBudget(Action<bool, string> check)
    {
        var lookup = new AnchorElementLookup();
        object hud = new object();

        check(lookup.Due(hud, 100),
            "the first sampling attempt of a level walks the icon display, as it always did");
        lookup.Record(hud, found: false, walked: true);
        check(lookup.Missing && lookup.Describe() == "yes(missing,still-looking)",
            "the row was not there yet: the walk is a real miss, and the log now says it is still being looked for");

        check(!lookup.Due(hud, 115),
            $"the retry is spaced: fifteen frames later the same miss does not walk the hierarchy again (every {AnchorElementLookup.RetryFrames} frames)");
        check(lookup.Due(hud, 130),
            "half a second later the next attempt walks again - the second source that appeared in the meantime can still be found");

        lookup.Record(hud, found: true, walked: true);
        check(lookup.Hit && !lookup.Due(hud, 200) && !lookup.Due(hud, 5000) && lookup.Describe() == "yes(found)",
            "the element that was finally found is cached for the life of the display: a hit costs no further walk, ever");

        var throwing = new AnchorElementLookup();
        throwing.Record(hud, found: false, walked: false);
        check(throwing.Unreadable && throwing.Due(hud, 10) && throwing.Due(hud, 10),
            "a walk that threw is still retried on every attempt: it never spent the miss budget and it never claims the element is absent");

        var recovered = new AnchorElementLookup();
        recovered.Record(hud, found: false, walked: false);
        recovered.Record(hud, found: false, walked: true);
        check(recovered.Missing && recovered.Due(hud, 40),
            "and a miss recorded after a throw starts its own bounded retry, so one bad frame cannot decide the level has no background");
    }

    // The budget is a budget: a level that never grows the row costs a bounded number of walks, and a caller that asks on every
    // frame cannot turn it into a per-frame scan.
    private static void TheMissBudgetCannotBecomeAPerFrameScan(Action<bool, string> check)
    {
        var lookup = new AnchorElementLookup();
        object hud = new object();
        lookup.Record(hud, found: false, walked: true);
        int walks = 0;
        for (int frame = 0; frame < 3600; frame++)
        {
            if (!lookup.Due(hud, frame)) continue;
            walks++;
            lookup.Record(hud, found: false, walked: true);
        }
        check(walks > AnchorElementLookup.MissWalks
              && walks <= AnchorElementLookup.MissWalks + 3600 / AnchorElementLookup.WatchFrames + 10,
            $"sixty seconds of asking every frame costs {walks} walk(s): the fast {AnchorElementLookup.MissWalks}, then at most one per {AnchorElementLookup.WatchFrames} frames - never a per-frame scan");
        check(lookup.MissRetired && lookup.Describe() == "yes(missing,slow-watch)",
            "the spent fast budget is reported as such, so the log can tell the fast tier from the slow watch");

        // 1.19.26: the slow tier. After the fast budget a miss is still retried - that is the whole point, because a row can
        // appear minutes into a level - but only once per `WatchFrames`.
        var slow = new AnchorElementLookup();
        slow.Record(hud, found: false, walked: true);
        int slowWalks = 0;
        for (int frame = 0; frame < 1200; frame++) if (slow.Due(hud, frame)) slowWalks++;
        check(slowWalks > AnchorElementLookup.MissWalks && slowWalks <= 1200 / AnchorElementLookup.WatchFrames + 10,
            $"after the fast budget the miss is retried on the slow tier: {slowWalks} walk(s) in twenty seconds, at most one per {AnchorElementLookup.WatchFrames} frames, never per frame");

        lookup.Forget();
        check(lookup.Due(hud, 100000) && lookup.Outcome == AnchorLookupOutcome.None,
            "a re-anchor - a configuration reload, a resolution change, a new level, a HUD rebuild - opens it again");

        check(AnchorElementLookup.MissWalks <= AnchorSamplePolicy.Budget && AnchorElementLookup.RetryFrames > 1,
            $"the budget never exceeds the anchor's own attempt budget ({AnchorElementLookup.MissWalks} <= {AnchorSamplePolicy.Budget}) and the walks are spaced, never one per frame");
    }

    // A SpriteRenderer is world geometry: it has no camera-less projection, and a camera that does not draw its layer is not one.
    private static void ASpriteWithNoCameraIsRefused(Action<bool, string> check)
    {
        var none = new List<AnchorCameraCandidate>();
        AnchorCameraChoice bare = AnchorCameraLadder.Select(none, IconLayer, AnchorCanvasMode.ScreenSpaceCamera, false, out _);
        check(!bare.Usable && bare.Projection == AnchorProjection.None,
            "the flashlight icon is a SpriteRenderer with no CanvasRenderer, and with no camera offered it has no projection at all");

        var blind = new List<AnchorCameraCandidate> { new AnchorCameraCandidate(AnchorCameraSource.AllCameras, true, 1 << 3, 0) };
        AnchorCameraChoice refused = AnchorCameraLadder.Select(blind, IconLayer, AnchorCanvasMode.ScreenSpaceCamera, false, out _);
        check(!refused.Usable && refused.Reason.Contains("culling mask"),
            "a live camera whose culling mask does not contain the sprite's layer is refused, and the reason says which test failed: " + refused.Reason);

        var hudSees = new List<AnchorCameraCandidate> { new AnchorCameraCandidate(AnchorCameraSource.HudWorldCamera, true, 1 << IconLayer, 0) };
        AnchorCameraChoice accepted = AnchorCameraLadder.Select(hudSees, IconLayer, AnchorCanvasMode.ScreenSpaceCamera, false, out _);
        check(accepted.Usable && accepted.Projection == AnchorProjection.Camera && accepted.Source == AnchorCameraSource.HudWorldCamera,
            "the one projection a sprite does have is a live window camera that really draws its layer");

        var textureOnly = new List<AnchorCameraCandidate> { new AnchorCameraCandidate(AnchorCameraSource.AllCameras, true, 1 << IconLayer, 0, targetTexture: true) };
        check(!AnchorCameraLadder.Select(textureOnly, IconLayer, AnchorCanvasMode.Overlay, false, out _).Usable,
            "a camera that renders into a RenderTexture answers in that texture's pixels and is never used as a window camera");
    }

    // The camera-less projection belongs to Unity's own canvas and to a real screen-space overlay: nothing else may borrow it.
    private static void OnlyARealOverlayMayProjectWithoutACamera(Action<bool, string> check)
    {
        var none = new List<AnchorCameraCandidate>();

        AnchorCameraChoice overlay = AnchorCameraLadder.Select(none, IconLayer, AnchorCanvasMode.Overlay, canvasDrawn: true, out _);
        check(overlay.Usable && overlay.Projection == AnchorProjection.CameraLessOverlay,
            "a UI element that Unity's canvas really draws, inside a screen-space overlay canvas, is the one legitimate null-camera projection");

        check(!AnchorCameraLadder.Select(none, IconLayer, AnchorCanvasMode.Overlay, canvasDrawn: false, out _).Usable,
            "a SpriteRenderer under that same canvas is world geometry: the canvas does not draw it, so it gets no projection at all");

        AnchorCameraChoice cameraMode = AnchorCameraLadder.Select(none, IconLayer, AnchorCanvasMode.ScreenSpaceCamera, canvasDrawn: true, out _);
        check(!cameraMode.Usable && cameraMode.Projection != AnchorProjection.CameraLessOverlay,
            "a screen-space-camera canvas with no usable camera is never answered with the overlay projection - that is the false positive this rule exists for");

        check(!AnchorCameraLadder.Select(none, IconLayer, AnchorCanvasMode.Unknown, canvasDrawn: true, out _).Usable,
            "and a canvas mode that could not be read is not Overlay either: nothing is known, so only a validated camera may be used");
    }

    // The second source is a different element, and it may only be used when its own renderer was really measured.
    private static void TheFallbackIsOnlyUsedWhenItWasReallyMeasured(Action<bool, string> check)
    {
        string panel = Source("PanelBehaviour.cs");
        check(panel.Contains("AnchorSourceLadder.First(icon.Measured, row.Measured)"),
            "the two native sources are chosen by whether each was really measured, never by whether its object merely exists");
        check(panel.Contains("if (icon.Measured) return AnchorSample.Of(icon.Rect, AnchorElementSource.Flashlight);")
              && panel.Contains("if (row.Measured)")
              && panel.Contains("return AnchorSample.Of(row.Rect, AnchorElementSource.BackgroundFade);"),
            "the row's Background Fade is returned only inside a `row.Measured` branch, and it is reported as itself, never as the icon");
        // 1.19.28 re-point: the sentence now names the two *rectangles* (the row's background SpriteRenderer, the icon row's layout
        // rectangle) instead of "the flashlight icon", which was the object this build stopped measuring. The claim is the same.
        check(panel.Contains("this is the row's background SpriteRenderer, not the icon row's layout rectangle, and")
              && panel.Contains("PositionX/PositionY/OffsetX/OffsetY keep the meaning they always had"),
            "and the one line that names the fallback says it is the row's background and not the icon row, and that the offsets are unchanged");

        string measure = Between(panel, "    private static AnchorElementMeasurement MeasureElement(Renderer renderer, List<Camera> cameras,",
            "    private static AnchorElementFacts Facts(string name");
        check(measure.Length > 0, "the one measurement body both elements share is found");
        check(measure.Contains("canvasDrawn = renderer.gameObject.GetComponent<CanvasRenderer>() != null;"),
            "whether Unity's canvas draws an element is asked of a CanvasRenderer - a RectTransform on a SpriteRenderer is not that fact");
        // 1.19.28 re-point: the sprite path also answers the raw window rectangle now (so the diagnostic line can print the numbers
        // a refused projection was refused for). The claim is unchanged: a sprite is projected through a camera the ladder proved.
        check(measure.Contains("measured = TryScreenRect(renderer, camera, out rect, out raw) || TryScreenRectFromSprite(renderer, camera, out rect, out raw);"),
            "a sprite is measured through a validated camera, from its own renderer bounds or from its own sprite geometry");
        check(measure.Contains("if (!measured && choice.Projection == AnchorProjection.CameraLessOverlay)")
              && measure.Contains("measured = TryScreenRectWithoutCamera(renderer, out rect);"),
            "and the null-camera path is only reached when the ladder really chose the camera-less overlay projection");
        check(measure.Contains("if (!choice.Usable)") && measure.Contains("AnchorSampleFailure.NoProjection"),
            "a refused ladder is a NoProjection failure, never a guessed position");

        // The camera-less projection itself: RectTransformUtility with a null camera, on a real RectTransform, with no ratio guess.
        string withoutCamera = Between(panel, "    private static bool TryScreenRectWithoutCamera(Renderer renderer, out LayoutRect rect)",
            "    private static List<PanelColumn> Columns()");
        check(withoutCamera.Contains("RectTransformUtility.WorldToScreenPoint(null, world)")
              && withoutCamera.Contains("element.rect")
              && !withoutCamera.Contains("Screen.width *"),
            "it is Unity's own null-camera projection of a RectTransform's four corners - no invented ratio, no scaled local position");
        // 1.19.28 re-point: both sprite paths answer the raw rectangle as well, so their signatures grew one `out`. The claim is
        // unchanged and is the one that matters: a sprite is never projected through a null camera.
        string withCamera = Between(panel, "    private static bool TryScreenRect(Renderer renderer, Camera camera, out LayoutRect rect, out AnchorScreenRect raw)",
            "    private static bool TryScreenRectFromSprite(Renderer source, Camera camera, out LayoutRect rect, out AnchorScreenRect raw)");
        check(withCamera.Length > 0 && withCamera.Contains("camera.WorldToScreenPoint(point)") && !withCamera.Contains("WorldToScreenPoint(null"),
            "and the sprite path always projects through the camera the ladder accepted, never through null");
    }

    // The element's own ancestor canvas, reached through the component that really carries it.
    //
    // A `Transform` is one component of a GameObject and a `Canvas` is another: they are siblings, not a base class and its
    // subclass, so `transform.TryCast<Canvas>()` asks the wrong component and can only answer null - the candidate would never
    // fire, the `layerCanvas=none` frame would keep its dead end, and nothing in a compile would say so, because `TryCast<T>`
    // is generic and unconstrained. The `TryCast<RectTransform>()` this build uses elsewhere is a genuine downcast (a
    // `RectTransform` *is* the `Transform`), which is exactly why that example cannot be carried over to `Canvas`. So the walk
    // has to ask the object: `GetComponent<Canvas>()`, level by level, up the parents.
    //
    // The checks below are on the call itself, not on the existence of `Canvas.worldCamera` (which the interop build proves
    // anyway): reverting the line to `TryCast<Canvas>()` - or adding such a cast anywhere in the panel - turns this red.
    private static void TheAncestorCanvasIsAskedOfTheComponentNotCast(Action<bool, string> check)
    {
        string panel = Source("PanelBehaviour.cs");
        string walk = Between(panel, "    private static Camera AncestorCanvasWorldCamera(Component element)",
            "    // The one line that says how the anchor was resolved");
        check(walk.Length > 0, "the ancestor-canvas walk is found in the production source");

        // A comment may name the wrong call in order to explain why it is wrong; only real code may not contain it. These two
        // strings are the walk and the whole panel with their comment lines removed.
        string walkCode = Code(walk), panelCode = Code(panel);
        check(walkCode.Contains("canvas = node.GetComponent<Canvas>();"),
            "the canvas is asked of each level's component, because a Transform is a component of the canvas' GameObject and not the canvas itself");
        check(walkCode.IndexOf("TryCast<Canvas", StringComparison.Ordinal) < 0,
            "and never cast to: TryCast<Canvas>() on a Transform asks the wrong component and can only answer null");
        check(panelCode.IndexOf("TryCast<Canvas", StringComparison.Ordinal) < 0,
            "no other line of code in the panel makes that cast either, so the candidate cannot be dead in a second place");

        check(walk.Contains("try { canvas = node.GetComponent<Canvas>(); } catch { canvas = null; }"),
            "a component read that throws on a destroyed object is caught, so one bad frame cannot fail the measurement");
        check(walkCode.Contains("while (node != null && guard++ < 32)") && walkCode.Contains("node = node.parent;"),
            "the walk climbs the parents and is bounded, so a deep HUD cannot turn it into a loop");
        check(walk.Contains("canvas.worldCamera"),
            "and the camera is taken from the canvas that was really found");
        check(walk.Contains("catch { node = null; }") && walk.Contains("catch { return null; }"),
            "a destroyed transform ends the walk and a canvas without a usable camera answers null, never a guess");

        check(panel.Contains("Add(cameras, candidates, AnchorCameraSource.ElementCanvas, elementCanvas);"),
            "the camera found that way is offered to the ladder as its own candidate, where the culling-mask proof still decides");
        check(panel.Contains("Camera elementCanvas = null;") && panel.Contains("ElementCanvasCamera(sprite, background)"),
            "and it is asked for both native elements, the icon first and the row second");
    }

    // The anchor's own latch, the settle window and the honest give-up are untouched by this round.
    private static void TheLatchAndTheRoundAreUntouched(Action<bool, string> check)
    {
        string panel = Source("PanelBehaviour.cs");
        check(panel.Contains("if (Plugin.AnchorToFlashlight.Value && !_anchorLocked) SampleAnchor(layer, Time.unscaledTime);"),
            "a locked anchor is never re-sampled: the table stays where it was frozen - and the spent budget is no longer a second gate");
        check(panel.Contains("if (_anchorSettle >= 1f || _anchorSampled >= 6f)")
              && panel.Contains("_anchorLocked = true;")
              && panel.Contains("_anchorFallback = false;"),
            "the lock is still taken only after the settle window, and the configured fallback is dropped with it");
        check(panel.Contains("if (_anchorSource.Observe(sample.Source))"),
            "a sample that changes source restarts the settle window instead of being measured as movement");
        check(panel.Contains("bool still = !float.IsNaN(_anchorLastX) && Mathf.Abs(rect.X - _anchorLastX) < 2f && Mathf.Abs(rect.Y - _anchorLastY) < 2f;"),
            "and stillness is still the anchor's own two-pixel comparison, so the visual stability of a locked table is unchanged");

        check(CountOf(panel, "_anchorUnavailable = false;") == 3,
            "the give-up is cleared in exactly three places: entering a level, a re-anchor, and a sample that measured something - the watch that found the element again");
        check(panel.Contains("_anchorPolicy.Restart();") && panel.Contains("_loggedAnchorCamera = false;"),
            "a re-anchor really starts a new round - its own attempts, its own failure lines, its own camera report");
        check(panel.Contains("the flashlight anchor was given up on for this level after")
              && panel.Contains("The HUD is still looked at once every")
              && panel.Contains("the table keeps the configured PositionX/PositionY"),
            "a real give-up is still named out loud, says the low-rate watch goes on, and keeps the configured position on screen");
        check(panel.Contains("_anchorFallback = true;")
              && panel.Contains("triedBackgroundFade={background}")
              && panel.Contains("source=config"),
            "and the retries are drawn from the configured position while they run, with the lookup's own state in the line");
    }

    // ---- wiring, read from the production source -----------------------------------------------

    private static void Wiring(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the 1.19.26 anchor regression locates the production sources");
        if (root == null) return;

        string policy = File.ReadAllText(Path.Combine(root, "AnchorSamplePolicy.cs"));
        check(policy.Contains("public const int MissWalks = 12;") && policy.Contains("public const int RetryFrames = 30;"),
            "the lookup carries its own two budgets, and both are the runtime assertions above");
        check(policy.Contains("public bool Due(object owner, int frame)")
              && policy.Contains("int spacing = _walks < MissWalks ? RetryFrames : WatchFrames;")
              && policy.Contains("if (frame == int.MinValue) return _walks < MissWalks ? Started(owner, frame, newOwner: false) : false;"),
            "a miss is spaced were the walk is granted - fast, then slow - and a caller with no clock is never handed the slow tier");

        string panel = File.ReadAllText(Path.Combine(root, "PanelBehaviour.cs"));
        check(panel.Contains("if (!_backgroundFadeLookup.Due(owner, Time.frameCount)) return _backgroundFade;"),
            "the panel hands the lookup its frame counter, which is what spaces the retries");
        check(panel.Contains("if (_backgroundFade == null && _backgroundFadeLookup.Hit) _backgroundFadeLookup.Forget();"),
            "and a cached hit whose element Unity has destroyed is forgotten instead of being answered for the rest of the level");

        Console.WriteLine("WIRING: RELEASE_11926_ANCHOR_MISS_RETRY (static source check; the lookup's budgets and the camera/canvas rules are the runtime assertions above)");
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

    private static int CountOf(string text, string token) => text.Split(new[] { token }, StringSplitOptions.None).Length - 1;

    // The text with its comment lines removed. A comment may name a call in order to explain why that call is wrong, so the
    // "this code must not do X" checks have to read the code that really runs, not the prose beside it.
    private static string Code(string text)
    {
        var kept = new List<string>();
        if (text == null) return string.Empty;
        foreach (string line in text.Split('\n'))
            if (!line.TrimStart().StartsWith("//", StringComparison.Ordinal)) kept.Add(line);
        return string.Join("\n", kept);
    }
}
