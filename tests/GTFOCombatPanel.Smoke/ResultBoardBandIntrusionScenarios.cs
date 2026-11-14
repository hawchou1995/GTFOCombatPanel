using System;
using HawChow.GTFOCombatPanel;

// Two independent-review findings, asserted against the production code the runtime calls.
//
// 1. A standing fail-open board was only ever asked about its band's *foot*. The band's top edge is the page's own anchor
//    (the completion section, the icon row, the failure title) and it slides down when those native elements do; a board that
//    kept an older, higher top was then drawn over exactly what that edge keeps clear, and no rule noticed. The move decision
//    now asks about both edges and about the rectangle that is on the screen.
// 2. A walk of the icon display that *threw* was cached as "no Background Fade" for the rest of the level. It is now
//    remembered as unreadable and retried on the anchor's own bounded cadence.
//
// Every number below comes from the prefab/measurement the previous rounds recorded, and every rule is the production one:
// `ResultLateControlPolicy`, `ResultFallbackLayout.TryPlaceInBand`, `ResultFallbackOpacity` and `AnchorElementLookup`. Where
// the fact only exists inside `ResultsDisplay`/`PanelBehaviour` (which cannot be compiled without GTFO's assemblies) the
// assertion is made against that file's own source text, and it says so.
internal static class ResultBoardBandIntrusionScenarios
{
    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        ABandTopThatSlidesIntoTheBoardMovesIt(check);
        ABoardInsideBothEdgesIsLeftAlone(check);
        ABandFootThatArrivesUnderTheBoardMovesOrFades(check);
        ASubPixelJitterMovesNothing(check);
        ALateControlThatShrinksTheBandIsSeen(check);
        TheRepaintAsksAboutTheBoardOnTheScreen(check);
        TheFallbackBoardIsNeverTakenAway(check);
        AFailedWalkIsNotRememberedAsAMissingElement(check);
        TheLookupIsAnsweredOncePerOwnerAndRetriedOnlyAfterAFailure(check);
    }

    // The reviewer's case, in the numbers it was given: the band's top moves 100 -> 180, the board stands at 120..500 and
    // the band's foot does not move. The board must follow the top edge - and because the foot stayed at 500, following it
    // makes the board taller than the band, which is the one case the fail-open path answers by fading rather than by
    // sliding up over the page above it.
    private static void ABandTopThatSlidesIntoTheBoardMovesIt(Action<bool, string> check)
    {
        var standing = new LayoutRect(700f, 120f, 1000f, 380f);       // what is on the screen
        check(!ResultLateControlPolicy.MustMove(standing.Y, standing.Bottom, 100f, 500f),
            "a board under a band that already starts above it is not moved for nothing");

        check(ResultLateControlPolicy.MustMove(standing.Y, standing.Bottom, 180f, 500f),
            "the band's top arriving at 180 with the board's top at 120 and its foot still inside the band's foot is a real intrusion");

        bool placed = ResultFallbackLayout.TryPlaceInBand(2560f, 1440f, 180f, 500f, standing.Width, standing.Height, 0f, out LayoutRect moved);
        check(placed && Math.Abs(moved.Y - 180f) < 0.01f,
            $"the board follows the band's own top and not some invented coordinate (top={moved.Y:0.##})");
        check(Math.Abs(moved.Height - standing.Height) < 0.01f,
            "and it keeps the height - i.e. the grid and glyph size - it was fitted with; only its place moves");
        check(Math.Abs(moved.X - (2560f - standing.Width) * 0.5f) < 0.01f && Math.Abs(moved.Width - standing.Width) < 0.01f,
            "the horizontal place and the width are the placement's own (the band's centring, same width); a vertical intrusion changes neither");
        // The band did not grow, so the board that followed its top now reaches past the foot: the fail-open path draws it
        // fainter instead of sliding it back up over the anchor the moved top edge is.
        float opacity = ResultFallbackOpacity.Resolve(1f, moved.Bottom, 180f, 500f);
        check(Math.Abs(opacity - ResultFallbackLayout.OverlapOpacity) < 0.001f,
            $"a board that no longer fits under the moved top is drawn faint ({opacity:0.00}), never slid back over the anchor");
        check(ResultLateControlPolicy.MustMove(moved.Y, moved.Bottom, 180f, 500f),
            "the policy keeps reporting the over-band foot, which is the fade's own input - while the repaint's destination is the place the board already stands, so it is not moved a second time for the same band");
    }

    // A board that sits inside both edges of its band is not moved - the recorded animation case.
    private static void ABoardInsideBothEdgesIsLeftAlone(Action<bool, string> check)
    {
        check(!ResultLateControlPolicy.MustMove(284f, 1184f, 284f, 1252f),
            "the recorded repaint after the board followed the band's top: 284 over 284, foot 1184 inside 1252");
        check(!ResultLateControlPolicy.MustMove(400f, 900f, 100f, 1244f),
            "a board well inside a tall band is left where it is");
        check(!ResultLateControlPolicy.MustMove(284f, 1252f, 284f, 1252f),
            "a board whose foot is exactly the band's foot still fits");
        check(!ResultLateControlPolicy.MustMove(284f, 1252.4f, 284f, 1252f),
            "and four tenths of a pixel past it is the band's own sub-pixel animation, not an intrusion");
    }

    // The band's foot arriving above the board's foot: the control the player has to press moved up into the board. The
    // destination is the band's own top, which is where the board already stands, so the answer is the fade - not a move
    // that would change nothing and not a slide up over the anchor.
    private static void ABandFootThatArrivesUnderTheBoardMovesOrFades(Action<bool, string> check)
    {
        var standing = new LayoutRect(700f, 120f, 1000f, 360f);       // 120..480
        check(ResultLateControlPolicy.MustMove(standing.Y, standing.Bottom, 100f, 420f),
            "a band whose foot arrives at 420 under a board that ends at 480 is a real intrusion");
        check(!ResultLateControlPolicy.MustMove(standing.Y, standing.Bottom, 100f, 480f),
            "a foot that arrives exactly at the board's foot is not");
        check(!ResultLateControlPolicy.MustMove(standing.Y, standing.Bottom, 100f, 479.8f),
            "and neither is half a pixel of it");

        bool placed = ResultFallbackLayout.TryPlaceInBand(2560f, 1440f, 100f, 420f, standing.Width, standing.Height, 0f, out LayoutRect moved);
        check(placed && Math.Abs(moved.Y - 100f) < 0.01f,
            "the destination is still the band's own top, which here is above the board: a foot that arrived does not move the board");
        float opacity = ResultFallbackOpacity.Resolve(1f, 480f, 100f, 420f);
        check(Math.Abs(opacity - ResultFallbackLayout.OverlapOpacity) < 0.001f,
            $"so the board is drawn faint over the control that arrived ({opacity:0.00}) rather than moved somewhere that does not exist");

        // A foot that arrives and leaves a band the board can hold does move it: the whole band slid up.
        bool moved2 = ResultFallbackLayout.TryPlaceInBand(2560f, 1440f, 60f, 420f, standing.Width, standing.Height, 0f, out LayoutRect up);
        check(moved2 && Math.Abs(up.Y - 60f) < 0.01f && Math.Abs(up.Height - standing.Height) < 0.01f,
            "a band that slid up as a whole is followed in the same way, and the grid keeps its size");
    }

    private static void ASubPixelJitterMovesNothing(Action<bool, string> check)
    {
        check(!ResultLateControlPolicy.MustMove(120f, 480f, 120.4f, 600f),
            "four tenths of a pixel of band-top jitter does not move a table the player is reading");
        check(!ResultLateControlPolicy.MustMove(120f, 480f, 120f, 480f),
            "and neither does none at all");
        check(ResultLateControlPolicy.MustMove(120f, 480f, 120.6f, 600f),
            "past the half-pixel tolerance it does move");
        check(!ResultLateControlPolicy.MustMove(120f, 480f, float.NaN, 600f)
              && !ResultLateControlPolicy.MustMove(120f, 480f, 120f, float.NaN),
            "an unmeasurable band edge never moves anything: nothing is known, so nothing is changed");
        check(!ResultLateControlPolicy.MustMove(float.NaN, float.NaN, 120f, 600f),
            "and an unmeasurable board is not moved either");
    }

    // The band comes from the page's own interaction scan. A control that arrives later pushes the band's foot up through
    // that scan, and the same two readings the policy is asked about come out of it - so the late control is seen without a
    // second, parallel scan and without the strict path's `BoardStillSafe`.
    private static void ALateControlThatShrinksTheBandIsSeen(Action<bool, string> check)
    {
        bool early = ResultFallbackLayout.TryResolve(1440f, 300f, 900f, 1000f, 620f, 1240f, 270f, out ResultFallbackPlan before);
        check(early && before.Placed && Math.Abs(before.Top - 620f) < 0.5f && Math.Abs(before.Bottom - 1240f) < 0.5f,
            $"the band the page's own scan produced while its button was still low is used as it is ({before.Top:0}..{before.Bottom:0})");

        var standing = new LayoutRect(700f, before.Top, 1000f, 300f);
        check(!ResultLateControlPolicy.MustMove(standing.Y, standing.Bottom, before.Top, before.Bottom),
            "while that band could hold the board, nothing moved");

        // The control arrives: the scan's own band loses its foot (620..700 is 80 px, less than the board floor), so the plan
        // answers with the strip above the page's actions instead - i.e. the band's foot really moved up, which is the case the
        // old rule could see and the case the fade exists for.
        bool late = ResultFallbackLayout.TryResolve(1440f, 300f, 900f, 1000f, 620f, 700f, 270f, out ResultFallbackPlan after);
        check(late && after.Placed && after.Bottom < before.Bottom,
            $"a band whose foot a control took no longer offers the same strip ({before.Top:0}..{before.Bottom:0} -> {after.Top:0}..{after.Bottom:0})");
        check(ResultLateControlPolicy.MustMove(standing.Y, standing.Bottom, after.Top, after.Bottom),
            "and the frame it arrived, the same board is over the new band's foot - which is what the per-repaint re-check now acts on");

        string display = Source("ResultsDisplay.cs");
        string repaint = Between(display, "private static void RefreshFallbackSafety()", "internal static bool ToggleForcedFallback()");
        check(repaint.Contains("FallbackPlacement(_page, _succeeded, out float top, out float room, out BoardStrip strip, out _)"),
            "the per-repaint re-check reads the band from the same `FallbackPlacement` the placement used, i.e. from the scan");
        check(!repaint.Contains("ScanFooterControls"),
            "and it does not run a second, parallel scan of its own");
    }

    // The wiring: which rectangle is asked about, what a move keeps, and that it happens on the fail-open path itself rather
    // than in the function that path skips.
    private static void TheRepaintAsksAboutTheBoardOnTheScreen(Action<bool, string> check)
    {
        string display = Source("ResultsDisplay.cs");
        string repaint = Between(display, "private static void RefreshFallbackSafety()", "internal static bool ToggleForcedFallback()");
        check(repaint.Length > 0, "the fail-open repaint re-check was found in ResultsDisplay");
        check(repaint.Contains("ResultLateControlPolicy.MustMove(_origin.Y, _origin.Bottom, strip.Top, strip.Bottom)"),
            "it asks about the rectangle that is on the screen and about both edges of the newly measured band");
        check(repaint.Contains("bool intrudes = ") && repaint.Contains("bool moved = horizontal || (intrudes && vertical);"),
            "a vertical move needs a real intrusion and a destination that really differs; a horizontal re-layout keeps its own threshold");
        check(repaint.Contains("_origin = rect; _holdBoard = rect;") && !repaint.Contains("_grid = null") && !repaint.Contains("ResetPlacement"),
            "a move assigns the place only - the grid the board was fitted with, and with it the glyph size, is untouched");
        check(repaint.Contains("beforeTop") && repaint.Contains("boardTop={beforeTop:0}->{_origin.Y:0}"),
            "and the line says which edge moved and by how much, so a moved board is explainable from the log");

        // The path it runs on: the fail-open branch, i.e. exactly the branch that skips `BoardStillSafe`.
        check(display.Contains("if (!_fallbackPlaced)") && display.Contains("RefreshFallbackSafety();"),
            "the fail-open re-check is the branch the strict path's `BoardStillSafe` is *not* used in - the finding's own point");
        check(repaint.Contains("if (_page == null || _grid == null) return;"),
            "a page that has gone away draws nothing new");
        check(!repaint.Contains("GuestClient()") && !repaint.Contains("ResultPageRole.Host"),
            "the rule is the same for host and client pages; the band it follows is the one each page's own scan produced");
        check(display.Contains("internal static bool ToggleForcedFallback()"),
            "F8 still reaches this path through the same placement, so the fixed board is the one F8 puts up as well");
    }

    // The fail-open board is moved and faded, never taken away: a screen with no table is the defect that path exists to
    // remove, and a page whose controls are simply not measurable must still get its board.
    private static void TheFallbackBoardIsNeverTakenAway(Action<bool, string> check)
    {
        string display = Source("ResultsDisplay.cs");
        string repaint = Between(display, "private static void RefreshFallbackSafety()", "internal static bool ToggleForcedFallback()");
        check(!repaint.Contains("ResetPlacement") && !repaint.Contains("_grid = null") && !repaint.Contains("return;   // no band"),
            "the fail-open re-check never withdraws the board: it moves it or fades it");
        check(repaint.Contains("float opacity = ResultFallbackOpacity.Resolve(planned, rect.Bottom, strip.Top, strip.Bottom);"),
            "the fade is derived from the same band the move is, on every repaint");
        check(repaint.Contains("_holdOpacity = opacity;"),
            "and the drawn opacity follows it, so a board that cannot fit is faint rather than invisible");
        check(display.Contains("It is moved out of their way - down to the band's own top when that edge moved into it - and drawn fainter when"),
            "the log line says the same thing in words");
    }

    // A walk that threw is not a miss: it is unreadable, it is retried, and a hit or a real miss is still answered from the
    // cache without walking the hierarchy again.
    private static void AFailedWalkIsNotRememberedAsAMissingElement(Action<bool, string> check)
    {
        var lookup = new AnchorElementLookup();
        object owner = new object();

        check(lookup.Due(owner) && lookup.Outcome == AnchorLookupOutcome.None,
            "nothing has been looked up yet, so the walk is due");
        check(lookup.Describe() == "no(not-looked-for)",
            "and the log says so instead of pretending the element is missing");

        lookup.Record(owner, found: false, walked: false);
        check(lookup.Outcome == AnchorLookupOutcome.Unreadable && lookup.Unreadable,
            "a walk that threw is recorded as unreadable");
        check(lookup.Due(owner),
            "and the same owner is asked again on the next attempt: one bad frame cannot decide the level has no background");
        check(lookup.Describe() == "yes(unreadable)",
            "the log names it as unreadable, which is not the same word as missing");

        lookup.Record(owner, found: true, walked: true);
        check(lookup.Hit && !lookup.Due(owner),
            "the second walk, which succeeded, is a hit - and it is not walked a third time");
        check(lookup.Describe() == "yes(found)", "and the log reports the hit");

        object other = new object();
        check(lookup.Due(other),
            "a new icon display (a level change destroys the old one) earns its own walk");

        lookup.Record(other, found: false, walked: true);
        check(lookup.Missing && lookup.Due(other),
            "a walk that completed and found nothing is a real miss - and 1.19.26 retries it, because a miss taken while the HUD is still coming up is a guess");
        check(lookup.Describe() == "yes(missing,still-looking)",
            "the miss has its own word, distinct from unreadable and from found, and says it is still being looked for");

        lookup.Forget();
        check(lookup.Outcome == AnchorLookupOutcome.None && lookup.Due(other) && lookup.Describe() == "no(not-looked-for)",
            "a re-anchor (config reload, resolution change) forgets everything, so the next attempt walks again");
    }

    // The cache's two budgets, as 1.19.26 defines them: a hit is answered once per owner, a miss is retried a bounded number of
    // times and no more - and an unreadable walk is retried as often as the caller asks, because it never claims anything.
    private static void TheLookupIsAnsweredOncePerOwnerAndRetriedOnlyAfterAFailure(Action<bool, string> check)
    {
        var lookup = new AnchorElementLookup();
        object owner = new object();
        lookup.Record(owner, found: true, walked: true);
        int walks = 0;
        for (int attempt = 0; attempt < 60; attempt++) if (lookup.Due(owner)) walks++;
        check(walks == 0,
            $"a cached hit is answered without touching the hierarchy again, however many times it is asked ({walks} walks in sixty attempts)");

        var missing = new AnchorElementLookup();
        missing.Record(owner, found: false, walked: true);
        int missWalks = 0;
        for (int attempt = 0; attempt < 600; attempt++) if (missing.Due(owner)) missWalks++;
        check(missWalks == AnchorElementLookup.MissWalks,
            $"a real miss is retried on a bounded budget - {missWalks} walk(s) in six hundred attempts, never one per frame and never one for the level");
        check(missing.MissRetired && missing.Describe() == "yes(missing,slow-watch)",
            "and the retired miss says exactly that, so the log can tell a spent budget from a walk that stopped looking");
        // 1.19.26: a spent fast budget is not the end of the lookup. A clocked ask is still granted on the slow tier, once per
        // `WatchFrames` - the cadence that lets a row appearing later in the level be found at all.
        check(missing.Due(owner, 100000) && !missing.Due(owner, 100030) && missing.Due(owner, 100060),
            "after the fast budget a walk is still granted, but only once per WatchFrames - and never twice inside it");

        var spaced = new AnchorElementLookup();
        spaced.Record(owner, found: false, walked: true);
        int spacedWalks = 0;
        for (int frame = 0; frame < 1000; frame++) if (spaced.Due(owner, frame)) spacedWalks++;
        check(spacedWalks > AnchorElementLookup.MissWalks
              && spacedWalks <= 1000 / AnchorElementLookup.RetryFrames + 10,
            $"a caller that asks every frame is spaced out: {spacedWalks} walk(s) in a thousand frames - the fast twelve, then one per {AnchorElementLookup.WatchFrames} frames");
        var restarted = new AnchorElementLookup();
        restarted.Record(owner, found: false, walked: true);
        check(restarted.Due(owner, 500) && !restarted.Due(owner, 520) && restarted.Due(owner, 5),
            "a frame counter that restarted is treated as due, not as a reason to skip for ever");

        var failing = new AnchorElementLookup();
        failing.Record(owner, found: false, walked: false);
        int retries = 0;
        for (int attempt = 0; attempt < 60; attempt++) if (failing.Due(owner)) retries++;
        check(retries == 60,
            "an unreadable lookup is due every time it is asked - the caller's own cadence is what bounds it, not a hidden counter");

        check(AnchorSamplePolicy.Budget == 12 && AnchorSamplePolicy.SettleInterval > 0f,
            "and the caller's cadence is the anchor's: twelve attempts on the documented schedule, never a per-frame loop");

        string panel = Source("PanelBehaviour.cs");
        string lookupBody = Between(panel, "    private static RectTransform BackgroundFade(PlayerGuiLayer layer)",
            "    private static Renderer BackgroundFadeRenderer(PlayerGuiLayer layer)");
        check(lookupBody.Contains("if (!_backgroundFadeLookup.Due(owner, Time.frameCount)) return _backgroundFade;"),
            "the panel asks the cache first, with its own frame counter so a miss is spaced, and returns the cached answer without walking");
        check(lookupBody.Contains("_backgroundFadeLookup.Record(owner, _backgroundFade != null, walked);"),
            "and it records what the walk answered, including that it did not walk at all");
        check(lookupBody.Contains("walked = true;") && lookupBody.Contains("walked = false;"),
            "the flag is only set once the walk itself returned, so a throw cannot be recorded as a completed miss");
        check(lookupBody.Contains("catch (Exception error)") && lookupBody.Contains("_loggedBackgroundFadeLookupError"),
            "a failed walk is named once per round rather than once per attempt");
        check(lookupBody.Contains("if (_backgroundFade == null && _backgroundFadeLookup.Hit) _backgroundFadeLookup.Forget();"),
            "a cached hit whose element Unity has destroyed is forgotten rather than answered for the rest of the level");
        check(CountOf(panel, "GetComponentsInChildren<RectTransform>(true)") == 1
              && !panel.Contains("FindObjectsOfType") && !panel.Contains("Resources.FindObjectsOfTypeAll"),
            "and the single walk per HUD is still the only hierarchy walk in the panel - no periodic scene scan");
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
