using System;
using HawChow.GTFOCombatPanel;

// The in-level anchor's second native source, and the rule that decides when a camera-less projection is legitimate at all.
//
// Everything here is asserted against the production code the runtime calls - `AnchorCameraLadder.Select`,
// `AnchorSourceLadder`, `AnchorSourceTracker`, `AnchorSpriteQuad` and `ResultFinalPolicy` - and, where the fact only exists
// inside `PanelBehaviour` (which cannot be compiled without GTFO's assemblies), against that file's own source text. The
// distinction matters: a headless test can prove a decision and can prove that the panel is wired to it, but it cannot prove
// that Unity's `SpriteRenderer.bounds` of an inactive object return this or that value. Those limits are named in the
// assertions that depend on them.
internal static class BackgroundFadeAnchorScenarios
{
    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        ASpriteWithARectTransformIsNotACanvasElement(check);
        ACanvasRendererIsWhatMakesOverlayProjectionLegal(check);
        ARenderTextureCameraIsNeverUsed(check);
        TheSameRowsBackgroundIsTheFallbackAndReportsItself(check);
        NeitherElementMeansTheConfiguredPosition(check);
        AnInactiveBackgroundIsMeasuredFromItsSpriteGeometry(check);
        AChangeOfSourceRestartsTheSettleWindow(check);
        OneAttemptTriesBothElementsForOneRetry(check);
        TheSecondSourceIsCachedAndInvalidatedWithTheRound(check);
        TheFallbackIsNamedOncePerRound(check);
        ASettledPageThatDropsAReadIsStillAnswered(check);
    }

    // The independent report's §6 defect, as a decision: "has a RectTransform" is not "Unity's canvas draws it".
    // The flashlight icon and the row's `Background Fade` both carry a RectTransform and neither carries a CanvasRenderer,
    // so under an overlay canvas with no validated camera the honest answer is "no projection", never
    // `RectTransformUtility.WorldToScreenPoint(null, ...)` on a 100x100 declaration.
    private static void ASpriteWithARectTransformIsNotACanvasElement(Action<bool, string> check)
    {
        var none = new AnchorCameraCandidate[0];
        AnchorCameraChoice overlay = AnchorCameraLadder.Select(none, 5, AnchorCanvasMode.Overlay, canvasDrawn: false, out int inspected);
        check(!overlay.Usable && overlay.Projection == AnchorProjection.None,
            "a SpriteRenderer in layer 5 under an overlay canvas is not projected at all when no camera draws it");
        check(overlay.Reason.Contains("overlay") && overlay.Reason.Contains("world geometry"),
            "and the reason says the canvas is overlay while the element is world geometry, not that a camera was missing: " + overlay.Reason);
        check(inspected == 0, $"no candidate was offered, so nothing can have been inspected ({inspected})");

        // A live camera that claims the layer still wins, whatever the canvas mode says - the mask is the proof.
        var hud = new[] { new AnchorCameraCandidate(AnchorCameraSource.HudWorldCamera, true, 1 << 5, -2) };
        AnchorCameraChoice camera = AnchorCameraLadder.Select(hud, 5, AnchorCanvasMode.Overlay, canvasDrawn: false, out _);
        check(camera.Usable && camera.Projection == AnchorProjection.Camera && camera.Index == 0,
            "the HUD's own camera is used as soon as its culling mask really contains layer 5, even under an overlay canvas");
        check(!AnchorCameraLadder.Renders(1 << 4, 5) && AnchorCameraLadder.Renders(1 << 5, 5),
            "the mask test is on the element's own layer and not on the camera's name");

        // An unreadable layer is a dead end of its own, and the reason must say so rather than guess.
        AnchorCameraChoice unknown = AnchorCameraLadder.Select(none, -1, AnchorCanvasMode.ScreenSpaceCamera, canvasDrawn: false, out _);
        check(!unknown.Usable && unknown.Reason.Contains("layer could not be read"),
            "an unreadable layer says exactly that instead of inventing a camera: " + unknown.Reason);
    }

    // A real CanvasRenderer is the one thing that makes a null-camera projection legitimate: Unity composites an overlay
    // canvas without a camera. That is a *different* object from a SpriteRenderer that happens to have a RectTransform.
    private static void ACanvasRendererIsWhatMakesOverlayProjectionLegal(Action<bool, string> check)
    {
        var none = new AnchorCameraCandidate[0];
        AnchorCameraChoice drawn = AnchorCameraLadder.Select(none, 5, AnchorCanvasMode.Overlay, canvasDrawn: true, out _);
        check(drawn.Usable && drawn.Projection == AnchorProjection.CameraLessOverlay && drawn.Index == -1,
            "an element Unity's canvas really draws is projected without a camera inside an overlay canvas");
        check(drawn.Reason.Contains("Unity's canvas"),
            "and the line says it is the canvas that draws it: " + drawn.Reason);

        // Not under any other canvas mode: a screen-space-camera canvas needs its camera, CanvasRenderer or not.
        AnchorCameraChoice cameraMode = AnchorCameraLadder.Select(none, 5, AnchorCanvasMode.ScreenSpaceCamera, canvasDrawn: true, out _);
        check(!cameraMode.Usable,
            "the same element under a screen-space-camera canvas is not projected without the camera that canvas uses");

        // And the production panel decides this from the component, not from the RectTransform it also has.
        string panel = Source("PanelBehaviour.cs");
        check(panel.Contains("canvasDrawn = renderer.gameObject.GetComponent<CanvasRenderer>() != null;"),
            "the panel asks whether a CanvasRenderer draws the object, which is the only fact that makes overlay projection legal");
        check(panel.Contains("AnchorCameraLadder.Select(candidates, layer, elementMode, canvasDrawn, element != null, out int inspected)"),
            "and that is the fact it hands the ladder as the CanvasRenderer one - with the element's own canvas mode, and the element's RectTransform offered separately (1.19.27), so neither fact is made to stand for the other");
        check(panel.Contains("iconHasRectTransform="),
            "the camera line still reports the RectTransform fact, because it is a fact about the object - it just no longer decides anything");
    }

    // A camera that renders into a RenderTexture answers in that texture's pixels. Until a conversion from them has been
    // verified - none has - such a camera may not be used, and the line has to say which dead end that was.
    private static void ARenderTextureCameraIsNeverUsed(Action<bool, string> check)
    {
        var onlyTexture = new[] { new AnchorCameraCandidate(AnchorCameraSource.AllCameras, true, 1 << 5, -1, targetTexture: true) };
        AnchorCameraChoice refused = AnchorCameraLadder.Select(onlyTexture, 5, AnchorCanvasMode.ScreenSpaceCamera, canvasDrawn: false, out _);
        check(!refused.Usable, "a camera that only draws into a RenderTexture never projects the anchor");
        check(refused.Reason.Contains("RenderTexture"),
            "and the reason names the RenderTexture rather than claiming no camera exists: " + refused.Reason);

        var both = new[]
        {
            new AnchorCameraCandidate(AnchorCameraSource.HudWorldCamera, true, 1 << 5, -2, targetTexture: true),
            new AnchorCameraCandidate(AnchorCameraSource.HudWorldCamera, true, 1 << 5, -3, targetTexture: false)
        };
        AnchorCameraChoice windowCamera = AnchorCameraLadder.Select(both, 5, AnchorCanvasMode.ScreenSpaceCamera, canvasDrawn: false, out _);
        check(windowCamera.Usable && windowCamera.Index == 1,
            "with a window camera available as well, that one is chosen even though it is drawn earlier (index 1, not 0)");

        string panel = Source("PanelBehaviour.cs");
        check(panel.Contains("cameraTargetTexture={(targetTexture ? \"Set\" : \"none\")}"),
            "the camera line prints whether the camera it used renders into a RenderTexture, so the assumption is visible in the log");
    }

    // The order the attempt walks, and that the fallback is reported as itself.
    private static void TheSameRowsBackgroundIsTheFallbackAndReportsItself(Action<bool, string> check)
    {
        check(AnchorSourceLadder.First(true, true) == AnchorElementSource.Flashlight,
            "the exact flashlight icon wins whenever it can be measured, so a working table never moves");
        check(AnchorSourceLadder.First(true, false) == AnchorElementSource.Flashlight,
            "and it is used on its own when the background is not there at all");
        check(AnchorSourceLadder.First(false, true) == AnchorElementSource.BackgroundFade,
            "the same row's Background Fade answers only when the icon itself could not be measured");
        check(AnchorSourceLadder.First(false, false) == AnchorElementSource.None,
            "with neither element readable, no source is claimed at all");

        check(AnchorSourceLadder.Name(AnchorElementSource.Flashlight) == "flashlight"
              && AnchorSourceLadder.Name(AnchorElementSource.BackgroundFade) == "background-fade"
              && AnchorSourceLadder.Name(AnchorElementSource.None) == "config",
            "the three sources have three distinct words in the log: flashlight, background-fade, config");
        check(AnchorElementNames.BackgroundFadeObject == "Background Fade",
            "the object the lookup matches is named exactly as the prefab names it - AoiYuki's mods match the same string");

        AnchorSample sample = AnchorSample.Of(new LayoutRect(100f, 200f, 250f, 30f), AnchorElementSource.BackgroundFade);
        check(sample.Measured && sample.Source == AnchorElementSource.BackgroundFade,
            "a rectangle from the row's background carries that source through the sample, so nothing downstream can call it the icon");
        check(!AnchorSample.Missing(AnchorSampleFailure.NoProjection).Measured
              && AnchorSample.Missing(AnchorSampleFailure.NoProjection).Source == AnchorElementSource.None,
            "a failure names no source: it is not a statement about either element");

        string panel = Source("PanelBehaviour.cs");
        // 1.19.28 re-point: the words changed with the geometry (the fallback is the row's `Background Fade` SpriteRenderer, and the
        // first source is the icon row's layout rectangle), but the claim is the same: the fallback line still says, in words, that
        // this is the row's background and not the icon row.
        check(panel.Contains("anchoring on the same HUD ") && panel.Contains("this is the row's background SpriteRenderer, not the icon row's layout rectangle, and ")
              && panel.Contains("PositionX/PositionY/OffsetX/OffsetY keep the meaning they always had."),
            "the one line printed when the fallback is used says, in words, that this is the row's background and not the icon row");
        check(panel.Contains("anchor={AnchorSourceLadder.Name(sample.Source)}"),
            "and the lock line names the source it locked on, so 'the table locked' can never be read without it");
    }

    // No element, no invented coordinates: the configured position and an honest reason.
    private static void NeitherElementMeansTheConfiguredPosition(Action<bool, string> check)
    {
        check(AnchorSourceLadder.First(false, false) == AnchorElementSource.None,
            "two unreadable elements answer no source, which is the configured position");
        check(AnchorSample.Missing(AnchorSampleFailure.NoIcon).Failure == AnchorSampleFailure.NoIcon,
            "and the reason the caller keeps is the one that says neither renderer was there");
        check(AnchorSamplePolicy.Describe(AnchorSampleFailure.NoProjection).Contains("no camera that draws"),
            "the projection failure says the camera chain and the canvas mode were read, not that no camera exists");
        check(AnchorSamplePolicy.Describe(AnchorSampleFailure.Unmeasurable).Contains("off screen"),
            "and an element whose geometry projected off screen says that instead");

        string panel = Source("PanelBehaviour.cs");
        check(panel.Contains("if (!icon.Present && !row.Present) return AnchorSample.Missing(AnchorSampleFailure.NoIcon);"),
            "the panel only says \"no icon\" when neither element exists on the HUD at all");
        check(panel.Contains("return AnchorSample.Missing(failure == AnchorSampleFailure.None ? AnchorSampleFailure.Unmeasurable : failure);"),
            "everything else is reported as the dead end it really was, and no coordinate is invented");
    }

    // The inactive background's geometry: the drawn quad comes from the sprite and the renderer's size, never from the
    // RectTransform that also sits on the object.
    private static void AnInactiveBackgroundIsMeasuredFromItsSpriteGeometry(Action<bool, string> check)
    {
        // Background Fade: sliced, 250 x 30, sprite `UI_Gradient_64x64` with pivot (1,0.5) - the numbers the prefab gives.
        AnchorQuad fade = AnchorSpriteQuad.FromSprite(250f, 30f, 1f, 0.5f);
        check(fade.Valid && Math.Abs(fade.MinX + 250f) < 0.01f && Math.Abs(fade.MaxX) < 0.01f,
            $"the background's quad starts 250 units left of its origin and ends on it ({fade.MinX:0.##}..{fade.MaxX:0.##})");
        check(Math.Abs(fade.MinY + 15f) < 0.01f && Math.Abs(fade.MaxY - 15f) < 0.01f,
            $"and it is centred vertically, 30 units tall ({fade.MinY:0.##}..{fade.MaxY:0.##})");
        check(Math.Abs(fade.Width - 250f) < 0.01f && Math.Abs(fade.Height - 30f) < 0.01f,
            "the extent is the 250 x 30 the renderer really draws, not the 300 x 109.92 the RectTransform declares");
        check(!(Math.Abs(fade.Width - 300f) < 0.01f) && !(Math.Abs(fade.Height - 109.92f) < 0.01f),
            "the declared container rectangle is explicitly not what is measured");

        // The flashlight icon: sliced, 25 x 25 at localScale 1.3, pivot (0.5,0.5) - symmetric about its own origin.
        AnchorQuad icon = AnchorSpriteQuad.FromSprite(32.5f, 32.5f, 0.5f, 0.5f);
        check(icon.Valid && Math.Abs(icon.MinX + 16.25f) < 0.01f && Math.Abs(icon.MaxY - 16.25f) < 0.01f,
            "the icon's quad is the 32.5 x 32.5 it draws, centred on its transform");

        // A mirrored sprite is mirrored about the origin - Unity does that to the mesh, so the quad has to follow.
        AnchorQuad flipped = AnchorSpriteQuad.FromSprite(250f, 30f, 1f, 0.5f, flipX: true);
        check(Math.Abs(flipped.MinX) < 0.01f && Math.Abs(flipped.MaxX - 250f) < 0.01f,
            "a horizontal flip mirrors the quad about the transform's origin instead of moving it");

        // Geometry that is not geometry must not read as a small rectangle at the origin.
        check(!AnchorSpriteQuad.FromSprite(0f, 30f, 1f, 0.5f).Valid
              && !AnchorSpriteQuad.FromSprite(float.NaN, 30f, 1f, 0.5f).Valid
              && !AnchorSpriteQuad.FromSprite(250f, float.PositiveInfinity, 1f, 0.5f).Valid,
            "a zero, NaN or infinite extent is unmeasurable rather than a degenerate quad the caller might project");
        check(Math.Abs(AnchorSpriteQuad.FromSprite(10f, 10f, float.NaN, float.NaN).Width - 10f) < 0.01f,
            "an unreadable pivot falls back to the sprite's centre instead of producing NaN corners");

        string panel = Source("PanelBehaviour.cs");
        string rect = Between(panel, "private static bool TryScreenRectFromSprite(", "private static bool TryScreenRectWithoutCamera(");
        check(rect.Length > 0, "the sprite-quad measurement exists in the panel (the assertions below describe a block this build has)");
        check(rect.Contains("renderer.size") && rect.Contains("sprite.bounds") && rect.Contains("AnchorSpriteQuad.FromSprite("),
            "it reads the renderer's size, the sprite's own bounds and the sprite's pivot");
        check(!rect.Contains("element.rect") && !rect.Contains("TryCast<RectTransform>()"),
            "and it never reads the element's RectTransform - that rectangle is the container, not what the sprite draws");
        check(rect.Contains("camera.WorldToScreenPoint("),
            "the quad's corners are projected by the same validated camera the renderer-bounds path uses");
        // 1.19.28 re-point: both sprite paths answer the raw window rectangle too. The claim is unchanged, and it is now also
        // scoped exactly as production is - the two sprite paths serve the `Background Fade` element only; the icon row's own
        // rectangle comes from its `RectTransform` and never from a `SpriteRenderer` bounds.
        check(panel.Contains("measured = TryScreenRect(renderer, camera, out rect, out raw) || TryScreenRectFromSprite(renderer, camera, out rect, out raw);"),
            "the renderer's own bounds are still tried first, so a sprite that can describe itself is never re-derived from its quad");
        check(panel.Contains("if (layoutIsGeometry)") && panel.Contains("else measured = TryScreenRect(renderer, camera, out rect, out raw)"),
            "and that sprite path is the `else` of the layout branch, so the icon row's own RectTransform is measured while the row's background keeps its sprite geometry");
    }

    // A change of source is a jump, not drift.
    private static void AChangeOfSourceRestartsTheSettleWindow(Action<bool, string> check)
    {
        var tracker = new AnchorSourceTracker();
        check(tracker.Source == AnchorElementSource.None, "a fresh round has measured nothing yet");
        check(!tracker.Observe(AnchorElementSource.Flashlight),
            "the first sample of a round does not restart anything - there is no window to restart");
        check(!tracker.Observe(AnchorElementSource.Flashlight),
            "two samples from the same element keep the window running");
        check(tracker.Observe(AnchorElementSource.BackgroundFade),
            "a sample from the other element restarts the window, because the two do not share a position");
        check(!tracker.Observe(AnchorElementSource.BackgroundFade),
            "and once the new source is the current one, the window runs again");
        check(tracker.Observe(AnchorElementSource.Flashlight),
            "switching back restarts it as well, in both directions");
        tracker.Restart();
        check(tracker.Source == AnchorElementSource.None && !tracker.Observe(AnchorElementSource.BackgroundFade),
            "a new level / config reload / resolution change forgets the source, so the next sample is a fresh start");

        check(!AnchorSourceLadder.RestartsSettle(AnchorElementSource.None, AnchorElementSource.BackgroundFade)
              && !AnchorSourceLadder.RestartsSettle(AnchorElementSource.BackgroundFade, AnchorElementSource.None)
              && AnchorSourceLadder.RestartsSettle(AnchorElementSource.Flashlight, AnchorElementSource.BackgroundFade),
            "only a change between two real sources restarts the window; a failure in between is not a move");

        string panel = Source("PanelBehaviour.cs");
        check(panel.Contains("if (_anchorSource.Observe(sample.Source))"),
            "the panel asks the tracker on every measured sample, which is the only place a source can change");
        string restart = Between(panel, "if (_anchorSource.Observe(sample.Source))", "bool still =");
        check(restart.Length > 0 && restart.Contains("_anchorSettle = 0f;") && restart.Contains("_anchorLastX = float.NaN;")
              && restart.Contains("_anchorLastY = float.NaN;"),
            "and the restart really drops the settle window and the last pixel, so the next sample is not compared with the other element's");
        check(CountOf(panel, "_anchorSource.Restart();") >= 2,
            "the tracker is forgotten both when the anchor is unlocked and when a level is entered");
        check(panel.Contains("_anchorSampled` keeps running"),
            "the sampling counter is deliberately not reset, because it counts the round's samples and not this source's");
    }

    // One attempt, two element reads, one spent retry - and a bounded budget that a fallback cannot extend.
    private static void OneAttemptTriesBothElementsForOneRetry(Action<bool, string> check)
    {
        string panel = Source("PanelBehaviour.cs");
        string read = Between(panel, "private static AnchorSample ReadAnchor(PlayerGuiLayer layer)", "private readonly struct AnchorElementMeasurement");
        check(read.Length > 0, "ReadAnchor was found in the panel");
        // 1.19.28/1.19.31/1.19.32 re-point: each element is still measured inside the one attempt, now with (a) the element's own
        // RectTransform, (b) that element's own ancestor `Canvas` - the object that decides both its render mode and which camera
        // may project it - and (c) the HUD's own manual canvas, read once for the attempt and asked per element whether it really
        // owns that element. The claim is unchanged: the fallback costs no extra retry.
        check(read.Contains("MeasureElement(sprite, cameras, candidates, mode, iconRect, layoutIsGeometry: true, iconCanvas, cellUi)"),
            "the icon row is measured inside the one attempt");
        check(read.Contains("MeasureElement(background, cameras, candidates, mode, rowElement, layoutIsGeometry: false, rowCanvas, cellUi)"),
            "and the row's background is measured inside the same attempt, with its own canvas, so the fallback costs no extra retry");
        check(!read.Contains("_anchorPolicy."),
            "the read itself never touches the retry budget: `Attempt` owns it, so a fallback cannot buy a second try");
        check(CountOf(panel, "_anchorPolicy.Attempt(now, () => ReadAnchor(layer), out bool gaveUp)") == 1,
            "one attempt per tick, however many elements it reads");

        check(AnchorSamplePolicy.Budget == 12,
            "the round is still twelve measurements, so a HUD that never answers still falls back within one expedition");
        check(AnchorSamplePolicy.DelayAfter(1) < AnchorSamplePolicy.DelayAfter(2)
              && AnchorSamplePolicy.DelayAfter(2) < AnchorSamplePolicy.DelayAfter(4)
              && AnchorSamplePolicy.DelayAfter(20) == AnchorSamplePolicy.MaximumRetryDelay,
            "the retry delay still doubles up to its ceiling, so twelve attempts cost seconds of waiting and not a per-frame poll");

        // Resolution change and configuration reload go through UnlockAnchor; a level change has its own branch. Both have to
        // drop the second source's cache, or a new HUD would be measured with the previous one's rectangle.
        string unlock = Between(panel, "    private void UnlockAnchor()", "    private void Update()");
        check(unlock.Contains("ForgetBackgroundFade();") && unlock.Contains("_loggedBackgroundFade = false;"),
            "a re-anchor drops the cached background and its one-line latch, so a resolution change measures the new layout");
        string entry = Between(panel, "                if (inLevel)\n                {", "            bool visible = inLevel ?");
        check(entry.Contains("ForgetBackgroundFade();") && entry.Contains("_anchorSource.Restart();"),
            "and so does entering a level, whose HUD destroys the cached object");
        check(entry.Contains("_anchorSettle = 0f;") && entry.Contains("_anchorSampled = 0f;")
              && entry.Contains("_anchorLastX = float.NaN;") && entry.Contains("_anchorLastY = float.NaN;"),
            "the sampling state of the previous level is still dropped in the same branch");
    }

    // The cache is a cache: one walk per HUD while the answer is a hit, a bounded retry while it is a miss, and never a scene scan.
    private static void TheSecondSourceIsCachedAndInvalidatedWithTheRound(Action<bool, string> check)
    {
        string panel = Source("PanelBehaviour.cs");
        string lookup = Between(panel, "    private static RectTransform BackgroundFade(PlayerGuiLayer layer)",
            "    private static Renderer BackgroundFadeRenderer(PlayerGuiLayer layer)");
        check(lookup.Length > 0, "the background lookup was found in the panel");
        check(lookup.Contains("if (!_backgroundFadeLookup.Due(owner, Time.frameCount)) return _backgroundFade;")
              && lookup.Contains("_backgroundFadeLookup.Record(owner, _backgroundFade != null, walked);"),
            "the walk is asked about - with the caller's frame counter, so a miss is retried on the lookup's own bounded budget - and answered through the same cache");
        check(!lookup.Contains("_backgroundFadeLookup.Misses") && !panel.Contains("_backgroundFadeMissing"),
            "1.19.26: a miss is no longer remembered for the rest of the level, and no second field was invented to remember it");
        check(!panel.Contains("_backgroundFadeMissing") && lookup.Contains("walked = false;"),
            "and a walk that threw is not remembered as missing: it is recorded as unreadable, so the next attempt walks again");
        check(lookup.Contains("GetComponentsInChildren<RectTransform>(true)"),
            "the walk includes inactive children, which is what the prefab needs (Background Fade is switched off there)");
        // 1.19.28 re-point: the comparison is the production predicate, and the constant it compares against is the *prefab's*
        // name. This line used to assert `AnchorElementNames.BackgroundFade` - the `[DIAG]` token "background-fade" - which is the
        // one-word defect 1.19.28 fixes; the object names are asserted against each other right below.
        check(lookup.Contains("if (!AnchorElementMatch.IsBackgroundFade(candidate.name)) continue;"),
            "the match is one production predicate call, exact and ordinal, never a prefix or a case-insensitive guess");
        check(AnchorElementMatch.IsBackgroundFade(AnchorElementNames.BackgroundFadeObject)
              && !AnchorElementMatch.IsBackgroundFade(AnchorElementNames.BackgroundFade),
            "and it matches the prefab's own name `Background Fade`, not the log token `background-fade`: the recorded `background-fade=missing` every level printed was this constant, not the HUD");
        check(lookup.Contains("layer.Inventory.m_iconDisplay.transform"),
            "and it is scoped to this inventory's own icon display, because three objects in this build carry that name");
        check(!panel.Contains("FindObjectsOfType") && !panel.Contains("Resources.FindObjectsOfTypeAll"),
            "no scene scan and no resource sweep: the anchor may never walk the level per frame");
        check(CountOf(panel, "GetComponentsInChildren<RectTransform>(true)") == 1,
            "exactly one recursive walk exists in the whole panel");

        check(CountOf(panel, "ForgetBackgroundFade();") == 3,
            "the cache is dropped from the unlock, the level entry and the missing-owner branch - nowhere else");
        check(panel.Contains("_backgroundFadeLookup.Forget();") && !panel.Contains("_backgroundFadeMissing"),
            "the drop clears the lookup itself, so the three fields it used to forget cannot drift apart");
    }

    // Log discipline: one line per round, always with a source word, and no per-sample spam.
    private static void TheFallbackIsNamedOncePerRound(Action<bool, string> check)
    {
        string panel = Source("PanelBehaviour.cs");
        check(panel.Contains("if (!_loggedBackgroundFade)") && panel.Contains("_loggedBackgroundFade = true;"),
            "the fallback line is gated by its own latch, so twelve retries cannot print twelve of them");
        check(panel.Contains("_loggedBackgroundFade = false;"),
            "and the latch is cleared exactly where a round starts again");
        check(panel.Contains("triedBackgroundFade={background}") && panel.Contains("triedBackgroundFade={_backgroundFadeLookup.Describe()}"),
            "the failure line and the give-up line both say whether the background was tried, and why not");
        check(panel.Contains("automatic HUD placement waits for proof") && panel.Contains("automatic HUD placement still waits for proof"),
            "neither failed measurement claims the configured position was drawn");
        check(CountOf(panel, "triedBackgroundFade=") == 2,
            "there are exactly two rate-limited lines that mention it - the failure and the give-up");
        check(!panel.Contains("\"Background Fade\""),
            "the object name is not duplicated into the panel: it lives in AnchorElementNames, which the lookup matches");
    }

    // The combination the previous review asked about: a page whose strip settled and which then drops a read.
    //
    // `FailOpenAllowed(everMeasurable, measurableThisFrame, settled, ...)` answers yes on that frame *because it settled*, so
    // the board is placed from the fail-open band instead of being withheld - the screen is never left blank. What it does
    // not do is treat the dropped read as "this page has no geometry", which is what the ever-latch is for. The residual is
    // bounded and is asserted here rather than hidden: `ResultAnchorRecovery` buys exactly one re-measure per page, so a
    // page that blinks repeatedly keeps the band's board until it answers and is still not withdrawn.
    private static void ASettledPageThatDropsAReadIsStillAnswered(Action<bool, string> check)
    {
        check(ResultFinalPolicy.FailOpenAllowed(true, true, true, false, 0),
            "a page whose strip settled is answered on the frame it settles");
        check(ResultFinalPolicy.FailOpenAllowed(true, false, true, false, 0),
            "and it is answered on a later frame that merely failed to read it - the screen is never left blank");
        check(!ResultFinalPolicy.GivenUp(true, true, false, 0) && !ResultFinalPolicy.GivenUp(true, true, false, 10000),
            "that frame is never recorded as a bounded give-up: the page did settle, and the log must not say otherwise");
        check(ResultFinalPolicy.WhyGivenUp(250f, 1244f, ResultFinalPolicy.MaximumWaitFrames, readableThisFrame: true).Contains("stayed readable")
              && ResultFinalPolicy.WhyGivenUp(250f, 1244f, ResultFinalPolicy.MaximumWaitFrames, readableThisFrame: false).Contains("stopped answering"),
            "the give-up line distinguishes a page that never settled from one that went blind after measuring");

        // A page that has never measured is still answered at once, and never waits - the client page's own case.
        check(ResultFinalPolicy.FailOpenAllowed(false, false, false, false, 0)
              && ResultFinalPolicy.FailOpenAllowed(false, false, false, false, 240),
            "a page that never handed over geometry - a client page with no footer - is answered immediately and stays answered");
        check(ResultFinalPolicy.WhyWaiting(false, false, false, float.NaN, float.NaN, 270f, 0).Contains("never handed over"),
            "and the line for it says so, instead of pretending the page was waited for");

        // The recovery this residual depends on is bounded, and that bound is production behaviour, not a comment.
        check(ResultAnchorRecovery.MaximumRecoveries == 1,
            "a fallback board is re-measured exactly once per page, which is the bound on the residual above");
        check(ResultAnchorRecovery.ShouldRetry(0, true, true) && !ResultAnchorRecovery.ShouldRetry(1, true, true),
            "the second look happens once and never becomes a per-frame re-measure");

        // F8 and the blind frame, from the same policy the panel calls.
        check(ResultFinalPolicy.FailOpenAllowed(true, false, false, true, 0),
            "F8 still answers on a blind frame and outranks the latch");

        string display = Source("ResultsDisplay.cs");
        check(display.Contains("ResultSettlementGate.Decision settlement = ResultSettlementGate.Decide(Forced, succeeded, RevealFinished,")
              && display.Contains("_finalEverMeasurable, _finalMeasurable, FinalLayout.Ready, finalWait);"),
            "and the settlement board asks the settlement gate with the ever-latch - not with this frame's read - which asks `ResultFinalPolicy` for the geometry and the native reveal for the page");
        check(display.Contains("ResultAnchorRecovery.ShouldRetry(_fallbackRecoveries, true, true)"),
            "the one re-measure after a fallback placement is wired where a standing board is decided");
        check(display.Contains("if (!BoardStillSafe())") && display.Contains("DeferBoardForInteraction();"),
            "a standing board is still revalidated against the page's own controls on every repaint, which is why a frame that"
            + " cannot see those controls is not kept regardless");
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static string Source(string name)
    {
        string root = SourceRoot();
        if (root == null) throw new InvalidOperationException("The plugin source directory could not be found from " + AppContext.BaseDirectory);
        string path = System.IO.Path.Combine(root, name);
        if (!System.IO.File.Exists(path)) throw new System.IO.FileNotFoundException("Production source is missing", path);
        return System.IO.File.ReadAllText(path);
    }

    private static string SourceRoot()
    {
        var directory = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "GTFOCombatPanel.csproj")))
            directory = directory.Parent;
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
}
