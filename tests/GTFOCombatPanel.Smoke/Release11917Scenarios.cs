using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// What 1.19.17 changes: a result page that is on the screen always gets its board.
//
// The defect, as it really happened: a host failure page was fully drawn - the game's own title at alpha 1.00, the
// four prisoners, the artifact columns, the loadout cards and the button at the bottom all visible and pressable -
// and the settlement board never appeared, for the whole page. The mod's log line for that frame says why it refused
// and it is worth reading literally:
//
//   [DIAG] failure board held back: titleSource=none; pageActive=true; missionTitlePresent=true,missionTitleActive=false,
//   ...; anchorActive=false,alpha=unreadable,rawTextAlpha=1.00,geometryMeasured=false ...; native mission-title
//   completion/geometry proof is not ready, so vanilla is retained.
//
// Every gate in `ResultsDisplay.Placement` is a proof about the *page*, and that page never handed one over. F8 could
// not help either: it cleared the placement and the same gates refused it again a frame later, which is why the log
// shows a press ("F8 visibility: ... requested=True") with no board and no further line at all.
//
// What replaced it: `ResultFallbackPolicy` (a bounded budget of refusals, or F8) decides when the strict path is
// stood in for, and `ResultFallbackLayout` places the board from the window's own safe area - the band the page's own
// controls leave free when there is one, the strip above the measured lower wall when there is not, and the whole
// lower safe area at a reduced opacity when nothing clear is left. It is never refused, and a board already up is
// moved and made fainter rather than taken away.
//
// The first sections drive the real policy of this build with the real numbers from that page. The last section reads
// the production source instead, and says so.

internal static class Release11917Scenarios
{
    public static int Run()
    {
        int failures = 0;
        RunInto((condition, description) =>
        {
            if (condition) return;
            failures++;
            Console.WriteLine("FAIL: " + description);
        });
        return failures;
    }

    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));

        AHostSuccessPageWithNoMeasurableFooterStillPlaces(check);
        AHostFailurePageDrawnButUnprovableStillPlaces(check);
        InactiveControlsAreCountedAsHiddenNotAsARefusal(check);
        AnExhaustedSettleBudgetOnlySlowsTheRetries(check);
        F8NeedsNoGateToHaveBeenReady(check);
        AFallbackBoardYieldsToThePageInsteadOfVanishing(check);
        GuestPagesAndThe11913PoliciesDoNotRegress(check);
        GlueOwnershipStillNeedsItsOwnRegisteredFire(check);
        Wiring(check);
    }

    // The success page of a host whose footer never hands over a rectangle, whose completion edge never settles and
    // whose title (there is none on this page) is not a thing: nothing about the page can be proven, and the board
    // is placed from the window's own safe area anyway. 2560x1440 is the real window from the log.
    private static void AHostSuccessPageWithNoMeasurableFooterStillPlaces(Action<bool, string> check)
    {
        check(!ResultFallbackPolicy.ShouldFallback(0, false),
            "an unrefused page is not drawn from the fallback: the strict geometry is still what a provable page gets");
        check(!ResultFallbackPolicy.ShouldFallback(ResultFallbackPolicy.MissBudget - 1, false),
            "one frame short of the budget is still the strict path, so a page still revealing itself is not drawn twice");

        bool resolved = ResultFallbackLayout.TryResolve(1440f, float.NaN, float.NaN, float.NaN, float.NaN,
            BoardFloor(), out ResultFallbackPlan plan);
        check(resolved && plan.Placed, "a 1440-high page with nothing measurable still gets a band");
        check(resolved && plan.Reason == ResultFallbackReason.ScreenSafeArea,
            $"with no control and no anchor the band is the window's own safe area: reason={(resolved ? plan.Reason.ToString() : "none")}");
        check(resolved && near(plan.Top, ResultFallbackLayout.SafeTop(1440f)),
            $"the upper edge is the screen's own fraction when the page handed no anchor: top={(resolved ? plan.Top : float.NaN):0.##}");
        check(resolved && near(plan.Bottom, 1432f) && near(plan.Height, 1432f - plan.Top),
            $"the lower edge keeps the window's own 8 px margin: bottom={(resolved ? plan.Bottom : float.NaN):0.##} room={(resolved ? plan.Height : float.NaN):0.##}");
        check(resolved && near(plan.Opacity, 1f),
            "a band nothing else stands in is drawn at full opacity");

        // The board fitted into that band is placed inside the window, never off it.
        bool placed = ResultFallbackLayout.TryPlaceInBand(2560f, 1440f, plan.Top, plan.Bottom, 1036f, 540f, 0f,
            out LayoutRect rect);
        check(placed && near(rect.Y, plan.Top) && near(rect.Bottom, plan.Top + 540f),
            $"the board hangs from the top of the band it was given: y={(placed ? rect.Y : float.NaN):0.##}");
        check(placed && rect.X >= 0f && rect.Right <= 2560f && rect.Y >= 0f && rect.Bottom <= 1432f,
            "the placed board is inside the window on all four sides");
    }

    // The real host failure page: the title's own rectangle reads (bottom 467), the lobby button reads (1151), and the
    // interaction scan is not complete. That page used to be refused for its whole life.
    private static void AHostFailurePageDrawnButUnprovableStillPlaces(Action<bool, string> check)
    {
        bool resolved = ResultFallbackLayout.TryResolve(1440f, 467f, 1151f, float.NaN, float.NaN, BoardFloor(),
            out ResultFallbackPlan plan);
        check(resolved && plan.Reason == ResultFallbackReason.ScreenSafeArea,
            $"a measured title and a measured button give the safe-area band: reason={(resolved ? plan.Reason.ToString() : "none")}");
        check(resolved && near(plan.Top, 467f + 16f),
            $"the upper edge is the title's own bottom plus the page gap: top={(resolved ? plan.Top : float.NaN):0.##}");
        check(resolved && near(plan.Bottom, 1151f - ResultFallbackLayout.ControlClearance),
            $"the lower edge keeps the board off the button: bottom={(resolved ? plan.Bottom : float.NaN):0.##}");
        check(resolved && plan.Height > BoardFloor(),
            $"the band holds the board's own 270 px floor: room={(resolved ? plan.Height : float.NaN):0.##}");

        // The band under the page's controls wins whenever the page's own scan measured one and it can hold the floor:
        // a board standing under every action covers nothing at all.
        bool band = ResultFallbackLayout.TryResolve(1440f, 519f, 683f, 743f, 1432f, BoardFloor(), out ResultFallbackPlan under);
        check(band && under.Reason == ResultFallbackReason.BelowControls,
            $"a measured free band under the page's controls is the first choice: reason={(band ? under.Reason.ToString() : "none")}");
        check(band && near(under.Top, 743f) && near(under.Bottom, 1432f),
            $"that band is used as measured: band=({(band ? under.Top : float.NaN):0}..{(band ? under.Bottom : float.NaN):0})");
        check(band && near(under.Opacity, 1f),
            "a board standing in a free band is drawn at full opacity");
    }

    // The 1.19.13 behaviour, unchanged by this release: a control the page switched off can never be pressed, so it is
    // counted as hidden and never as a refusal - and an active control whose rectangle could not be read still keeps
    // the "this page has no footer" answer off the table.
    private static void InactiveControlsAreCountedAsHiddenNotAsARefusal(Action<bool, string> check)
    {
        var allInactive = new ResultInteractionTally(0, 4, 2, 0);
        check(ResultInteractionScanPolicy.ProvenEmpty(true, true, allInactive),
            "a complete scan that found every control switched off proves the page empty: hidden controls are not a refusal");
        check(!ResultInteractionScanPolicy.ProvenEmpty(false, true, allInactive),
            "an incomplete scan proves nothing, however many of its controls were hidden");
        check(!ResultInteractionScanPolicy.ProvenEmpty(true, true, new ResultInteractionTally(1, 4, 2, 0)),
            "one active control keeps the page from being called footerless");
        check(!ResultInteractionScanPolicy.ProvenEmpty(true, true, new ResultInteractionTally(1, 4, 2, 1)),
            "an active control whose rectangle could not be read is still an interaction");
        check(ResultInteractionScanPolicy.DowngradeIsWhy(true, true, new ResultInteractionTally(1, 0, 0, 1)),
            "the one-off downgraded-control line is still written for an otherwise complete scan with an unreadable control");
    }

    // `ResultEdgeGate.SettleAttempts` is a budget for the *cadence*, never a timeout that turns an unmeasured edge into
    // a measured one - and, since this release, never a reason for the page to keep its board off either.
    private static void AnExhaustedSettleBudgetOnlySlowsTheRetries(Action<bool, string> check)
    {
        var gate = new ResultEdgeGate();
        for (int frame = 0; frame < ResultEdgeGate.SettleAttempts + 40; frame++) gate.Observe(frame, false, float.NaN);
        check(gate.Exhausted, "a gate nobody ever hands an edge to reports its budget spent");
        check(ResultFallbackPolicy.ShouldFallback(ResultFallbackPolicy.MissBudget, false),
            "once the strict path has been refused enough times the fail-open path answers, so an exhausted budget is not 'never again'");
        bool resolved = ResultFallbackLayout.TryResolve(1440f, float.NaN, float.NaN, float.NaN, float.NaN, BoardFloor(),
            out ResultFallbackPlan plan);
        check(resolved && plan.Placed,
            "and the board it places is a real one, on the same exhausted page");

        // The retry cadence itself never stops: it slows down, and it keeps answering.
        var retry = new ResultRetryCadence();
        for (int frame = 0; frame < 400; frame++) retry.Due(frame);
        check(retry.Throttled, "the cadence throttles after its first stretch of frames");
        check(retry.Due(1000) && !retry.Due(1010) && retry.Due(1030) && retry.Due(2000) && retry.Due(5000),
            "the throttled cadence keeps measuring the page for the rest of the expedition: every 30 frames, for ever");
    }

    // F8 on a result page is the override itself. `ShouldFallback(0, forced: true)` is the policy half of it: no frame
    // of refusal is needed, so nothing about the page has to be ready first.
    private static void F8NeedsNoGateToHaveBeenReady(Action<bool, string> check)
    {
        check(ResultFallbackPolicy.ShouldFallback(0, true),
            "F8 asks for the fail-open board on the frame it is pressed, with no refusal behind it");
        check(ResultFallbackPolicy.ShouldFallback(1, true) && ResultFallbackPolicy.ShouldFallback(100000, true),
            "and it answers the same whatever the page's own refusal count is: held back, never placed, or given up on");

        string root = SourceRoot();
        if (root == null) { check(false, "the 1.19.17 regression locates the production sources"); return; }
        string results = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        string panel = File.ReadAllText(Path.Combine(root, "PanelBehaviour.cs"));

        check(results.Contains("internal static bool ToggleForcedFallback()"),
            "the F8 override is one explicit policy call and not a side effect of clearing a placement");
        check(panel.Contains("ResultsDisplay.ToggleForcedFallback()"),
            "PanelBehaviour really calls it on the toggle key");
        check(panel.Contains("F8 on a result page"),
            "and a press on a result page writes a line of its own, whether or not it produced a draw");

        // The key block has to sit before the result-page branches so a page with no board yet is still reached: the
        // press is read in `Update`, which runs whatever the settlement page is doing.
        int keyHandle = panel.IndexOf("Input.GetKeyDown(Plugin.ToggleKey.Value)", StringComparison.Ordinal);
        int resultBranch = panel.IndexOf("bool resultPage = state == eGameStateName.ExpeditionSuccess", StringComparison.Ordinal);
        check(keyHandle >= 0 && resultBranch > keyHandle,
            "the key is read unconditionally and the result-page branch hangs off that same read");
        check(!panel.Contains("if (resultPage && ResultsDisplay") && !panel.Contains("ResultsDisplay.NeedsAttach() &&"),
            "the F8 branch is not conditional on a board being attached or a gate being ready");
    }

    // The safety half of the rule: a fallback board yields to the page - it moves, it shrinks into the band the fit
    // was given, it is drawn fainter - and a board taller than its band is still a board.
    private static void AFallbackBoardYieldsToThePageInsteadOfVanishing(Action<bool, string> check)
    {
        // The page's controls leave only a 50 px band and the strip above the wall is 159 px: neither can hold the
        // 270 px floor, so the board takes the whole lower safe area and is drawn faint. It is not dropped.
        bool resolved = ResultFallbackLayout.TryResolve(1440f, 519f, 700f, 710f, 760f, BoardFloor(),
            out ResultFallbackPlan plan);
        check(resolved && plan.Reason == ResultFallbackReason.OverlapsControls,
            $"a page whose bands are all shorter than the board's floor still answers with a placement: reason={(resolved ? plan.Reason.ToString() : "none")}");
        check(resolved && near(plan.Opacity, ResultFallbackLayout.OverlapOpacity),
            $"and that placement says it stands over the page, in the opacity it is drawn at: opacity={(resolved ? plan.Opacity : float.NaN):0.00}");
        check(resolved && plan.Placed && plan.Height > 0f,
            "the answer is a band, never an empty one");

        // A grid taller than the band it was fitted into keeps the band's *top* edge and runs past its lower one. It is
        // never slid up over the page above it: that is what put a real client board on top of the sector-icon row. The
        // opacity of such a board is the reduced one (`ResultFallbackLayout.OverlapOpacity`, applied by the fit).
        bool tall = ResultFallbackLayout.TryPlaceInBand(2560f, 1440f, 800f, 900f, 1200f, 540f, 0f, out LayoutRect rect);
        check(tall, "a board taller than its fallback band is still placed");
        check(tall && rect.Y >= 800f - 0.5f,
            $"it keeps the band's upper edge and never climbs above it: y={(tall ? rect.Y : float.NaN):0.##} bandTop=800");
        check(tall && near(rect.Y, 800f),
            $"which is the band's own top edge: y={(tall ? rect.Y : float.NaN):0.##}");
        check(tall && rect.Bottom <= 1440f,
            "and it still never leaves the window");

        // A page whose controls move under the board is answered with a move, not with a blank screen: the per-repaint
        // re-check exists to hand over a new band, and the failure of a strict gate is not consulted at all.
        string root = SourceRoot();
        if (root == null) { check(false, "the 1.19.17 regression locates the production sources"); return; }
        string results = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        check(results.Contains("RefreshFallbackSafety();"),
            "the fail-open board is re-checked against the page on every repaint");
        check(results.Contains("if (!_fallbackPlaced)") && results.Contains("if (!BoardStillSafe())"),
            "the strict per-repaint rejection still exists, and is skipped only for a fail-open board");
        check(!results.Contains("if (!Placement(page, succeeded, out _, out float room, out BoardStrip strip)) return;"),
            "the strict placement is no longer the only answer to a page: its refusal is counted, not obeyed as 'never draw'");
    }

    // 1.19.13's rules this release did not touch, asserted again so a later change cannot lose them quietly.
    private static void GuestPagesAndThe11913PoliciesDoNotRegress(Action<bool, string> check)
    {
        check(ResultsGuestPageGeometry.TryResolve(380f, 1920f, 1080f, 16f, 96f, out ResultsSafeArea failure),
            "the client failure page's fixed geometry still resolves from its measured title");
        check(near(failure.Top, 396f) && near(failure.Bottom, 1072f),
            $"at the same pixels as 1.19.15: ({failure.Top:0.##}..{failure.Bottom:0.##})");
        check(ResultsGuestPageGeometry.TryResolve(520f, 1920f, 1080f, 16f, 96f, out ResultsSafeArea success),
            "the client success page's fixed geometry still resolves from its settled completion edge");
        check(near(success.Top, 536f), $"at the same upper edge as 1.19.15: {success.Top:0.##}");
        check(!ResultsGuestPageGeometry.TryResolve(float.NaN, 1920f, 1080f, 16f, 96f, out _),
            "an unmeasurable client anchor still places nothing rather than guessing");

        check(ResultPageRolePolicy.UsesNoFooterSafeArea(ResultPageRole.Client)
              && !ResultPageRolePolicy.UsesNoFooterSafeArea(ResultPageRole.Host),
            "the no-footer safe area is still the client's proof alone");
        check(ResultPageRolePolicy.UsesBandBelowActions(ResultPageRole.Host, 132f, 270f, 689f),
            "a host strip too short for the board's floor still steps into the measured band below its actions");
        check(!ResultPageRolePolicy.UsesBandBelowActions(ResultPageRole.Client, 132f, 270f, 689f),
            "and the client page never takes it");

        var controls = new List<ResultActionBounds>
        {
            new ResultActionBounds(new LayoutRect(700f, 400f, 400f, 60f))
        };
        bool strip = ResultInteractionStripPolicy.TryResolve(300f, 16f, controls, out ResultInteractionStrip resolved);
        check(strip && near(resolved.BoardTop, 316f) && near(resolved.FooterTop, 400f),
            $"the 1.19.13 strip policy is unchanged: boardTop={(strip ? resolved.BoardTop : float.NaN):0} footerTop={(strip ? resolved.FooterTop : float.NaN):0}");
        // The clearance band is part of the rule: a board whose lower edge reaches the control's own top edge is inside
        // the 6 px the per-repaint check keeps free, so "clear" means clear by that much.
        var board = new LayoutRect(700f, 306f, 400f, 84f);
        check(!ResultInteractionStripPolicy.OverlapsBoard(controls[0].Bounds, board, ResultBoardHold.ButtonClearance),
            $"a board that stops clear of a control does not overlap it: boardBottom={board.Bottom:0} controlTop={controls[0].Bounds.Y:0}");
        var touching = new LayoutRect(700f, 316f, 400f, 84f);
        check(ResultInteractionStripPolicy.OverlapsBoard(controls[0].Bounds, touching, ResultBoardHold.ButtonClearance),
            "a board whose lower edge reaches the control's own top edge is inside that clearance and does overlap it");

        // The floor the fallback is judged against is the same 270 px the real 1440-high page reported.
        check(near(BoardFloor(), 270f, 1f),
            $"the board's own floor is still the 30-row table at the smallest readable glyph: floor={BoardFloor():0.##}px");
    }

    // 1.19.13's glue attribution: a glue hit counts only when this projectile was registered as a Fire from the owner
    // that is being credited. Nothing in this release touched it, so it is asserted once more.
    private static void GlueOwnershipStillNeedsItsOwnRegisteredFire(Action<bool, string> check)
    {
        var ledger = new ToolImpactLedger();
        IntPtr projectile = new IntPtr(0x1200), enemy = new IntPtr(0x3400), other = new IntPtr(0x5600);
        check(!ledger.TryGlueHit(7UL, projectile),
            "a glue hit with no registered Fire is not counted");
        ledger.BeginGlueProjectile(7UL, projectile);
        check(ledger.HasGlueProjectile(7UL, projectile), "the owner that fired the projectile owns it");
        check(!ledger.HasGlueProjectile(9UL, projectile), "another player does not");
        check(ledger.TryGlueHit(9UL, projectile) == false,
            "and cannot be credited with the shot it never fired");
        check(ledger.TryGlueHit(7UL, projectile), "the owner's own shot is counted once");
        check(!ledger.TryGlueHit(7UL, projectile), "and only once: the shot is spent by the first contact");
        GlueContactCredit stranger = ledger.RecordGlueContact(9UL, projectile, enemy);
        check(!stranger.Hit && !stranger.NewEnemy,
            "a contact from an owner that did not fire the projectile credits nothing at all");
        ledger.BeginGlueProjectile(7UL, other);
        GlueContactCredit first = ledger.RecordGlueContact(7UL, other, enemy);
        GlueContactCredit again = ledger.RecordGlueContact(7UL, other, enemy);
        check(first.Hit && first.NewEnemy, "one spray glues the first enemy it touches");
        check(!again.Hit && !again.NewEnemy, "the same enemy is not glued twice by the same spray");
        check(ledger.GluedEnemyCount == 1, $"and the glued-enemy count says one: {ledger.GluedEnemyCount}");
    }

    // A static source check only: it says the fail-open path is really wired into the page, that F8 is the key that
    // reaches it, and that the file the policy lives in declares what this suite drives. It is not a runtime
    // behaviour test and does not pretend to be one.
    private static void Wiring(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the 1.19.17 regression locates the production sources");
        if (root == null) return;
        string fallbackPath = Path.Combine(root, "FallbackLayout.cs");
        check(File.Exists(fallbackPath), "the fail-open band lives in its own source file");
        if (!File.Exists(fallbackPath)) return;
        string fallback = File.ReadAllText(fallbackPath);
        check(fallback.Contains("public static class ResultFallbackPolicy")
              && fallback.Contains("public static class ResultFallbackLayout")
              && fallback.Contains("public readonly struct ResultFallbackPlan"),
            "that file really declares the policy this suite drives");
        check(fallback.Contains("public static bool ShouldFallback(int misses, bool forced) => forced || misses >= MissBudget;")
              && fallback.Contains("MissBudget"),
            "the budget and the F8 override are the only two ways in, exactly as the tests above assume");

        string results = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        check(results.Contains("ResultPlacementPolicy.Decide(false, _strictMisses, Forced)"),
            "Prepare consults the policy on the frame the fit refused - after the fit, never on the placement's own answer");
        check(results.Contains("FallbackPlacement(page, succeeded"),
            "and the fail-open placement is a real call in the same method");
        check(results.Contains("ResultFallbackLayout.TryPlaceInBand(Screen.width, Screen.height, strip.Top, strip.Bottom"),
            "the fit places a fallback board that is a fraction over its band instead of refusing it");
        check(results.Contains("|| Forced;") && results.Contains("ResultFooterSource.ScreenSafeArea"),
            "the F8 override outranks the visibility switches and the fail-open band names its own source");

        // Every early return that used to leave the page blank now has a line of its own. These are the four that had
        // none: the two client safe-area resolutions, the unready completion section and the page that is not there.
        check(results.Contains("ReportHeldBack(\"success-anchor\", \"completion-not-ready\""),
            "an unready completion section is reported instead of being a silent refusal");
        check(results.Contains("ReportHeldBack(\"failure-client-safe-area\", \"safe-area-unusable\"")
              && results.Contains("ReportHeldBack(\"success-client-safe-area\", \"safe-area-unusable\""),
            "both client safe-area refusals say why they refused");
        check(results.Contains("ReportHeldBack(\"refresh\", \"no-live-page\""),
            "a result state whose page object is not live is reported too");

        Console.WriteLine("WIRING: RESULT_FAIL_OPEN (static source check; not a runtime behaviour test)");
    }

    // The height the table cannot be drawn under at any readable glyph size, measured by this build for the 30-row
    // The height the table cannot be drawn under at any readable glyph size, measured by this build for the 30-row
    // settlement board at the compact density - the figure the real 1440-high page reported as `need=270px`. The test
    // project has no Unity and therefore no `HudTable.RowEm`; 1.5 em is the same row height `HudTable` reports for the
    // smallest readable glyph, and the assertion below fixes the 270 px the log and the 1.19.16 suite already use.
    private static float BoardFloor() => ResultsFontGrowth.MinimumBoardHeight(30, 1.5f, PanelDensity.Compact);

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj"))) directory = directory.Parent;
        return directory != null ? directory.FullName : null;
    }

    private static bool near(float a, float b) => near(a, b, 0.01f);
    private static bool near(float a, float b, float tolerance) => Math.Abs(a - b) <= tolerance;
}
