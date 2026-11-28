using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// What 1.19.18 changes: the settlement board's band - an icon row above, the footer this machine can see below.
//
// The defect, as a player photographed it on a client's success page of a 2560x1440 window: the board started around
// 575/1080 down a page whose sector-icon row ends around 250/1080, so a band of empty page sat above it and the table
// was squeezed into the lower half; and its lower edge ran through the native "waiting for the host" line and the
// "send the prisoners back to the lobby" line, because a client page has no pressable footer and the old rule simply
// ran the board to the foot of the window.
//
// What replaced it: `ResultsBounds` resolves one band for both roles - the icon row's own bottom edge (its label
// included) plus a clearance scaled from 16 px at 1080p above, and the highest footer the page really draws minus the
// same clearance below (the host's leave button, or the client's waiting block, measured as the world-space
// TextMeshPro it is). `ResultsBandFit` spends that band on the rows: the row count is fixed, the glyph and the row
// grow with the band, and a band too short for the readable minimum lowers the glyph instead of being crossed.
//
// The numbers below are the ones the log and the screenshot really produced: 2560x1440, an icon row ending at 333, a
// waiting block at 1373 and a host lobby button at 683. The last section reads the production source instead of
// driving it, and says so.

internal static class Release11918Scenarios
{
    // The real page: 2560x1440, the sector-icon row's own bottom edge (the screenshot's 250/1080 scaled to 1440).
    private const float ScreenWidth = 2560f;
    private const float ScreenHeight = 1440f;
    private const float IconRowBottom = 333f;
    private const float WaitingTextTop = 1373f;
    private const float HostLobbyButtonTop = 683f;

    public static int Run()
    {
        int failures = 0;
        RunInto((condition, description) =>
        {
            // The same polarity as `Program.Check`: an assertion says what must be true.
            if (condition) return;
            failures++;
            Console.WriteLine("FAIL: " + description);
        });
        return failures;
    }

    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));

        TheClearanceIsADesignValueThatScales(check);
        AClientPageIsBoundedByItsIconRowAndItsWaitingBlock(check);
        AHostPageIsBoundedByItsOwnButton(check);
        AnIconRowAloneStillStartsTheBoardAtTheTop(check);
        AnUnmeasurableIconRowKeepsTheLastTrustedEdge(check);
        ATightBandShrinksInsteadOfCrossing(check);
        TheFitDecidesWhetherTheFrameSucceeded(check);
        TheFailOpenBoardObeysTheSameBand(check);
        Wiring(check);
    }

    // A 16 px design gap at 1080p, scaled with the window: the same look at 720p, 1080p, 1440p and 4K.
    private static void TheClearanceIsADesignValueThatScales(Action<bool, string> check)
    {
        check(near(ResultsBounds.Clearance(1080f), 16f),
            $"the clearance is 16 px at the 1080p reference: {ResultsBounds.Clearance(1080f):0.##}");
        check(near(ResultsBounds.Clearance(1440f), 16f * 1440f / 1080f),
            $"and scales with the window height: {ResultsBounds.Clearance(1440f):0.##} at 1440");
        check(near(ResultsBounds.Clearance(2160f), 32f),
            $"32 px at 4K: {ResultsBounds.Clearance(2160f):0.##}");
        check(near(ResultsBounds.Clearance(float.NaN), 16f) && near(ResultsBounds.Clearance(0f), 16f),
            "a screen nobody could read keeps the design value rather than a zero gap");
    }

    // The page from the screenshot: the icon row above, the waiting block below, and the board between them.
    private static void AClientPageIsBoundedByItsIconRowAndItsWaitingBlock(Action<bool, string> check)
    {
        bool resolved = ResultsBounds.TryResolve(ScreenHeight, IconRowBottom, ResultBoundSource.IconRow,
            float.NaN, WaitingTextTop, float.NaN, out ResultsBand band);
        float clearance = ResultsBounds.Clearance(ScreenHeight);
        check(resolved && band.Fits, "an icon row and a waiting block measure a band");
        check(resolved && band.TopSource == ResultBoundSource.IconRow,
            $"the upper edge names the icon row: {ResultsBounds.Name(band.TopSource)}");
        check(resolved && band.BottomSource == ResultBoundSource.FooterText,
            $"the lower edge names the waiting block: {ResultsBounds.Name(band.BottomSource)}");
        check(resolved && near(band.Top, IconRowBottom + clearance, 2f),
            $"the board starts at the icon row's own bottom plus the clearance: top={band.Top:0.#} expected={IconRowBottom + clearance:0.#}");
        check(resolved && near(band.Bottom, WaitingTextTop - clearance, 2f),
            $"and stops above the waiting block by the same clearance: bottom={band.Bottom:0.#} expected={WaitingTextTop - clearance:0.#}");
        check(resolved && band.Height > 900f,
            $"which is the whole lower page rather than the 400 px the old rule used: room={band.Height:0}px");

        // The board hangs from the top edge - never centred - so a taller band is a taller table.
        bool placed = ResultFallbackLayout.TryPlaceInBand(ScreenWidth, ScreenHeight, band.Top, band.Bottom, 1800f, 700f, 0f,
            out LayoutRect rect);
        check(placed && near(rect.Y, band.Top, 2f),
            $"the board's top edge is the band's top edge: y={rect.Y:0.#}");
        check(placed && rect.Bottom <= band.Bottom + 0.5f,
            $"and its lower edge stays inside the band: boardBottom={rect.Bottom:0.#} bandBottom={band.Bottom:0.#}");
        check(placed && rect.Bottom < WaitingTextTop,
            $"so it cannot cover the waiting block: boardBottom={rect.Bottom:0.#} waitingText={WaitingTextTop:0}");

        // A page that carries both walls - a host's button below a waiting line that is still on screen - is bounded by
        // the higher of the two, which is the waiting line: the board never stands between them.
        bool both = ResultsBounds.TryResolve(ScreenHeight, IconRowBottom, ResultBoundSource.IconRow,
            WaitingTextTop + 40f, WaitingTextTop, float.NaN, out ResultsBand tighter);
        check(both && tighter.BottomSource == ResultBoundSource.FooterText,
            $"the higher wall wins: {(both ? ResultsBounds.Name(tighter.BottomSource) : "none")}");
        check(both && near(tighter.Bottom, WaitingTextTop - clearance, 2f),
            $"and the band stops at the waiting line, not at the button under it: bottom={tighter.Bottom:0.#}");
    }

    // The host's own page: the button the player has to press is the lower edge.
    private static void AHostPageIsBoundedByItsOwnButton(Action<bool, string> check)
    {
        bool resolved = ResultsBounds.TryResolve(ScreenHeight, IconRowBottom, ResultBoundSource.IconRow,
            HostLobbyButtonTop, float.NaN, float.NaN, out ResultsBand band);
        float clearance = ResultsBounds.Clearance(ScreenHeight);
        check(resolved && band.BottomSource == ResultBoundSource.FooterControl,
            $"the host band's lower edge names the control: {(resolved ? ResultsBounds.Name(band.BottomSource) : "none")}");
        check(resolved && near(band.Bottom, HostLobbyButtonTop - clearance, 2f),
            $"bottom = button top - clearance: {band.Bottom:0.#} expected={HostLobbyButtonTop - clearance:0.#}");
        check(resolved && near(band.Top, IconRowBottom + clearance, 2f),
            $"and it still starts at the icon row: top={band.Top:0.#}");

        // A control the scan measured somewhere else may shorten the band, never lengthen it.
        bool withControl = ResultsBounds.TryResolve(ScreenHeight, IconRowBottom, ResultBoundSource.IconRow,
            float.NaN, float.NaN, 900f, out ResultsBand controlled);
        check(withControl && controlled.BottomSource == ResultBoundSource.MeasuredControl
              && near(controlled.Bottom, 900f - clearance, 2f),
            $"a measured control is a wall of its own: {controlled.Bottom:0.#}");
    }

    // Nothing measurable below the icon row: the band still starts at the icon row and runs to the window's foot,
    // and it is the *top* that decides where the board begins - not the middle of the page.
    private static void AnIconRowAloneStillStartsTheBoardAtTheTop(Action<bool, string> check)
    {
        bool resolved = ResultsBounds.TryResolve(ScreenHeight, IconRowBottom, ResultBoundSource.IconRow,
            float.NaN, float.NaN, float.NaN, out ResultsBand band);
        check(resolved && band.TopSource == ResultBoundSource.IconRow && band.BottomSource == ResultBoundSource.ScreenFoot,
            $"an unmeasurable footer keeps the window's foot margin: {(resolved ? ResultsBounds.Name(band.BottomSource) : "none")}");
        check(resolved && near(band.Bottom, ScreenHeight - ResultsBounds.ScreenMargin),
            $"at 1440 - 8 = 1432: {band.Bottom:0.#}");
        check(resolved && near(band.Top, IconRowBottom + ResultsBounds.Clearance(ScreenHeight), 2f),
            $"and the board still hangs from the icon row: top={band.Top:0.#}");
    }

    // The page was still revealing its icons: the last trusted edge stands in, and the label above the icons is never
    // covered either way.
    private static void AnUnmeasurableIconRowKeepsTheLastTrustedEdge(Action<bool, string> check)
    {
        // The page was still revealing its icons: the caller hands over the edge it measured on an earlier frame, and the
        // band keeps standing there instead of dropping to a fraction of the window.
        bool resolved = ResultsBounds.TryResolve(ScreenHeight, IconRowBottom, ResultBoundSource.LastTrustedIconRow,
            float.NaN, WaitingTextTop, float.NaN, out ResultsBand band);
        check(resolved && band.TopSource == ResultBoundSource.LastTrustedIconRow,
            $"a frame without a freshly measured icon row keeps the last trusted one: {(resolved ? ResultsBounds.Name(band.TopSource) : "none")}");
        check(resolved && near(band.Top, IconRowBottom + ResultsBounds.Clearance(ScreenHeight), 2f),
            $"and the board still hangs from that edge: top={band.Top:0.#}");

        bool nothing = ResultsBounds.TryResolve(ScreenHeight, float.NaN, ResultBoundSource.ScreenSafeArea,
            float.NaN, float.NaN, float.NaN, out ResultsBand bare);
        check(nothing && bare.TopSource == ResultBoundSource.ScreenSafeArea
              && near(bare.Top, 0.10f * ScreenHeight, 0.5f),
            $"nothing measurable at all is the one case that takes the window's own fraction: top={bare.Top:0.#}");
        check(nothing && bare.Bottom > bare.Top, "and even that is a real band, never an empty one");
    }

    // The band is the promise: a short one lowers the glyph and the row together, and the table still fits inside it.
    private static void ATightBandShrinksInsteadOfCrossing(Action<bool, string> check)
    {
        const int rows = 30;
        int readable = ResultsFontGrowth.MinimumReadablePixels;
        int floor = ResultsBandFit.FloorForBand(200f, rows, PanelDensity.Compact, readable, out bool squeezed);
        check(squeezed, $"a 200 px band cannot hold the {readable} px readable table of {rows} rows");
        check(floor < readable && floor >= ResultsBandFit.AbsoluteMinimumPixels,
            $"so the band's own answer is the floor: {floor}px");
        float rowHeight = ResultsBandFit.RowHeight(floor, PanelDensity.Compact);
        check(rowHeight * rows <= 200f + 0.5f,
            $"and {rows} rows of {rowHeight:0.#} px fit 200 px: {rowHeight * rows:0.#}");

        // The same mapping on the real page: the 588 px strip of the host log and the full client band.
        int generous = ResultsBandFit.FontForBand(588f, rows, PanelDensity.Compact, 0);
        check(generous >= 12 && generous <= 15,
            $"a 588 px band over 30 rows allows a 12-15 px glyph, the figure the real page logged: {generous}px");
        check(ResultsBandFit.RowHeight(generous, PanelDensity.Compact) * rows <= 588f + 0.5f,
            "and those rows fit the 588 px band");

        // A band so short that the page collapsed it: the lower edge still holds and the upper edge moved up to it.
        bool clamped = ResultsBounds.TryResolve(ScreenHeight, IconRowBottom, ResultBoundSource.IconRow,
            360f, float.NaN, float.NaN, out ResultsBand band);
        check(clamped && band.Clamped,
            "a band shorter than the minimum is marked clamped instead of being drawn into");
        check(clamped && band.Height >= ResultsBounds.MinimumBand - 0.5f,
            $"and it keeps the minimum room worth drawing in: {band.Height:0.#}px");
        check(clamped && near(band.Bottom, 360f - ResultsBounds.Clearance(ScreenHeight), 2f),
            $"with the footer's edge exactly where the page put it: bottom={band.Bottom:0.#}");
    }

    // The fail-open path of 1.19.17 obeys the same two edges: this is the regression that matters most, because the
    // band the client got last release was the window's own safe area and ran straight under the waiting text.

    // ---- review fixes: what a frame means, and the band's two hard edges -------------------------------------
    //
    // Required change 1. `Placement` only answers "the page offered a strip", and a strip of 100 px is non-empty while
    // the 30-row board cannot be drawn under about 135 px at the smallest glyph this build allows. Clearing the refusal
    // count on that answer meant: placement fine, fit refused, count zero, for ever - and F8 unable to help, because the
    // fallback budget was never consulted. The count is now spent by the fit's own answer, and the very same frame may
    // already be the frame the fail-open band answers on.
    private static void TheFitDecidesWhetherTheFrameSucceeded(Action<bool, string> check)
    {
        float floor = ResultsFontGrowth.MinimumBoardHeight(30, 1.5f, PanelDensity.Compact);
        // The board's own floor at the smallest glyph, and the smallest grid this build can draw: 30 rows x 3 px x 1.5 em.
        check(floor >= 135f - 0.5f,
            $"the 30-row board cannot be drawn under about 135 px at any glyph this build allows: floor={floor:0}");

        check(ResultPlacementPolicy.Decide(placed: true, misses: 0, forced: false) == ResultPlacementOutcome.PlacedStrict,
            "only a fit that really placed a board is a success");
        check(ResultPlacementPolicy.Decide(false, 0, false) == ResultPlacementOutcome.Deferred,
            "a frame the fit refused is a refusal, not a success");
        check(ResultPlacementPolicy.Decide(false, ResultFallbackPolicy.MissBudget - 1, false) == ResultPlacementOutcome.Deferred,
            "one refusal short of the budget still waits");
        check(ResultPlacementPolicy.Decide(false, ResultFallbackPolicy.MissBudget, false) == ResultPlacementOutcome.PlacedFallback,
            "the refusal that spends the budget puts the fail-open board on that very frame");
        check(ResultPlacementPolicy.Decide(false, 0, true) == ResultPlacementOutcome.PlacedFallback,
            "F8 puts it there on the frame it is pressed, with no refusal behind it at all");
        check(ResultPlacementPolicy.Decide(false, 100000, true) == ResultPlacementOutcome.PlacedFallback,
            "and it answers the same however long the page has been refusing");
        check(ResultPlacementPolicy.Spend(int.MaxValue) == int.MaxValue,
            "the refusal count saturates instead of wrapping");

        // Required change 2. A band that cannot hold the board's own floor is refused, and the reduced-opacity tier
        // answers instead; a board taller than its band keeps the band's top edge and never climbs over the icon row.
        check(!ResultFallbackTier.AcceptsPageBand(48f, floor)
              && !ResultFallbackTier.AcceptsPageBand(135f, floor)
              && !ResultFallbackTier.AcceptsPageBand(floor - 0.6f, floor),
            "a band shorter than the board's own floor is not a band");
        check(ResultFallbackTier.AcceptsPageBand(floor, floor) && ResultFallbackTier.AcceptsPageBand(997f, floor),
            "a band that can hold it is");
        check(!ResultFallbackTier.AcceptsPageBand(float.NaN, floor) && !ResultFallbackTier.AcceptsPageBand(300f, float.NaN),
            "and an unmeasurable one is not a band either");

        foreach (float bandHeight in new[] { 48f, 135f, 269f, 270f })
        {
            bool placed = ResultFallbackLayout.TryPlaceInBand(2560f, 1440f, 400f, 400f + bandHeight, 1200f, 270f, 0f,
                out LayoutRect rect);
            // 1.19.47 (review #4): the band's foot binds exactly as its top does. A band that cannot hold the board refuses
            // the placement (the caller then fits a smaller legal grid into the same band); a band that can hold it keeps the
            // board between its two edges and never lets it climb above the top.
            bool holds = bandHeight + 0.5f >= 270f;
            check(placed == holds,
                $"a {bandHeight:0} px band {(holds ? "holds" : "refuses")} a 270 px board - the band's own foot is a bound, not a guess (placed={placed})");
            check(!placed || (rect.Y >= 400f - 0.5f && rect.Bottom <= 400f + bandHeight + 0.5f),
                $"and a placed board stays between the band's two edges: y={(placed ? rect.Y : float.NaN):0.##}, bottom={(placed ? rect.Bottom : float.NaN):0.##}");
        }

        // Required change 3 (the waiting block's reading). The failure page has always counted its waiting text only
        // while the page draws it; the success page's subtree now does the same, and an alpha nobody could read keeps the
        // wall instead of dropping it - the same convention `ControlAlpha` uses for a control.
        check(!ResultFooterTextPolicy.Counts(activeInHierarchy: false, alpha: 1f),
            "a switched-off text is not a wall");
        check(!ResultFooterTextPolicy.Counts(true, 0f) && !ResultFooterTextPolicy.Counts(true, 0.01f),
            "a readable alpha at or below the visible threshold is not a wall");
        check(ResultFooterTextPolicy.Counts(true, float.NaN),
            "an unreadable alpha keeps the wall instead of making it vanish for a frame");
        check(ResultFooterTextPolicy.Counts(true, 1f) && ResultFooterTextPolicy.Counts(true, 0.5f),
            "the waiting line the page is really drawing is");
        bool fromText = ResultsBounds.TryResolve(1440f, 333f, ResultBoundSource.IconRow, float.NaN, 1373f,
            ResultBoundSource.FooterText, float.NaN, out ResultsBand textBand);
        bool fromControl = ResultsBounds.TryResolve(1440f, 333f, ResultBoundSource.IconRow, float.NaN, 1373f,
            ResultBoundSource.FooterTextControl, float.NaN, out ResultsBand controlBand);
        check(fromText && textBand.BottomSource == ResultBoundSource.FooterText
              && ResultsBounds.Name(textBand.BottomSource) == "footer-text",
            $"a text the page draws is named as such: {(fromText ? ResultsBounds.Name(textBand.BottomSource) : "none")}");
        check(fromControl && controlBand.BottomSource == ResultBoundSource.FooterTextControl
              && ResultsBounds.Name(controlBand.BottomSource) == "footer-text-control",
            $"and a control rectangle standing in for it says that instead: {(fromControl ? ResultsBounds.Name(controlBand.BottomSource) : "none")}");
        check(fromText && fromControl && near(textBand.Bottom, controlBand.Bottom),
            "both readings bound the band at the same edge");


        // ---- closure round: the ways a page could still end up covered, or blank -------------------------------
        //
        // (a) The fail-open tier that stands in for "no band can hold the board" used to read only the button as a wall.
        //     On a client page whose button is not measurable that left the band running to the window's foot, straight
        //     over the waiting line - and because the board was then shorter than that huge band, the over-band fade
        //     never fired either. Both walls are read now and the higher one wins.
        bool textOnly = ResultFallbackLayout.TryResolve(1440f, 333f, float.NaN, 1373f, float.NaN, float.NaN,
            ResultsFontGrowth.MinimumBoardHeight(30, 1.5f, PanelDensity.Compact), out ResultFallbackPlan textPlan);
        check(textOnly && near(textPlan.Bottom, 1373f - ResultFallbackLayout.ControlClearance, 1f),
            $"with no button but a waiting line, the fail-open band stops above that line: bottom={textPlan.Bottom:0.#}");
        check(textOnly && textPlan.Bottom < 1373f,
            $"so the board it places cannot cover it: bottom={textPlan.Bottom:0.#} < waitingText=1373");
        bool bothWalls = ResultFallbackLayout.TryResolve(1440f, 333f, 1330f, 1373f, float.NaN, float.NaN, 270f,
            out ResultFallbackPlan bothPlan);
        check(bothWalls && near(bothPlan.Bottom, 1330f - ResultFallbackLayout.ControlClearance, 1f),
            $"and when both walls are readable the higher one wins: bottom={bothPlan.Bottom:0.#}");
        bool noWalls = ResultFallbackLayout.TryResolve(1440f, 333f, float.NaN, float.NaN, float.NaN, float.NaN, 270f,
            out ResultFallbackPlan barePlan);
        check(noWalls && barePlan.Bottom > 1373f,
            $"only a page that measured no wall at all reaches the window's foot: {barePlan.Bottom:0}");

        // (b) The fail-open board's opacity is re-derived from the same measurement its place is, every repaint.
        check(near(ResultFallbackOpacity.Resolve(1f, 900f, 400f, 1000f), 1f),
            "a board inside its band is drawn at the opacity its band earned");
        check(near(ResultFallbackOpacity.Resolve(1f, 1001f, 400f, 1000f), ResultFallbackLayout.OverlapOpacity),
            "a board over its band is drawn faint");
        check(near(ResultFallbackOpacity.Resolve(ResultFallbackLayout.OverlapOpacity, 900f, 400f, 1000f),
                ResultFallbackLayout.OverlapOpacity),
            "and a band that was only ever the overlap tier stays faint even when the board fits it");
        check(near(ResultFallbackOpacity.Resolve(1f, 900f, float.NaN, float.NaN), 1f),
            "an unmeasurable band leaves the opacity alone rather than inventing a fade");

        // (c) A fail-open board may not be lifted above its band's top edge by the per-repaint button check - and 1.19.21's
        // code-review fix goes one step further: both edges bind, and a board whose height cannot sit between the band's
        // top and the button's clearance line is refused (with the real numbers) instead of being drawn over either edge.
        var held = new LayoutRect(100f, 500f, 800f, 200f);
        bool kept = ResultBoardHold.TryHold(new BoardStrip(500f, 700f, true, 640f), held, 1440f, out LayoutRect lifted);
        check(kept && lifted.Bottom <= 640f - ResultBoardHold.ButtonClearance + 0.5f,
            $"a board that would reach the button is lifted clear of it: bottom={lifted.Bottom:0.#}");
        check(kept && lifted.Y < 500f,
            $"which does move it up from where it was: y={lifted.Y:0.#}");
        bool bounded = ResultBoardHold.TryHold(new BoardStrip(500f, 700f, true, 640f), held, 1440f,
            out LayoutRect clamped, 500f);
        check(!bounded,
            $"1.19.22 re-point: TryHold's own answer is unchanged - a board whose height cannot fit between its band's top (500) and the button's clearance wall ({640f - ResultBoardHold.ButtonClearance:0}) is refused rather than climbing over the band or reaching over the button - and the caller now keeps the standing rectangle, draws it faint and reports instead of deferring it away (the no-defer branch is asserted by the 1.19.22 suite)");

        // (d) The fail-open floor ladder: a page that cannot hold the measured floor is answered with a smaller board on
        //     the next frame, and the ladder stops at the shortest grid this build can draw.
        float smallest = ResultsBandFit.MinimumDrawableHeight(30, PanelDensity.Compact);
        check(smallest > 0f && smallest < floor,
            $"the shortest drawable grid is a real figure below the measured floor: {smallest:0} < {floor:0}");
        float ladder = ResultFallbackLadder.Next(1f);
        float firstStep = ResultFallbackLadder.Floor(floor, smallest, ladder);
        check(firstStep < floor && firstStep >= smallest,
            $"one failed fail-open fit lowers the floor it asks for: {firstStep:0} of {floor:0}");
        for (int step = 0; step < 40; step++) ladder = ResultFallbackLadder.Next(ladder);
        check(near(ResultFallbackLadder.Floor(floor, smallest, ladder), smallest, 0.5f),
            $"and the ladder bottoms at the shortest drawable grid, never below it: {ResultFallbackLadder.Floor(floor, smallest, ladder):0}");
        check(ResultFallbackTier.AcceptsPageBand(smallest, ResultFallbackLadder.Floor(floor, smallest, ladder)),
            "so the band that was refused at the measured floor is accepted once the ladder has stepped down");
        // The wheel over a forced board: F8 outranks the switches for the draw, so it has to for the wheel too.
        // 1.19.59: the wheel's gates moved into `WheelShown`/`WheelApplies` (the same four switches, the F8 override first),
        // and the scroll itself is settled by `PollWheel` from the game's own readings rather than from an IMGUI event.
        string root = SourceRoot();
        if (root == null) { check(false, "the 1.19.18 regression locates the production sources"); return; }
        string results = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        string wheelShown = MethodBody(results, "private static bool WheelShown()");
        check(wheelShown != null && wheelShown.Contains("if (Forced) return true;"),
            "the wheel reads the F8 override before the three visibility switches");
        check(wheelShown != null && wheelShown.Contains("PanelBehaviour.ToggleState") && wheelShown.Contains("ShowImpactBoard"),
            "and keeps every switch it used to read inside that guard");
        check(MethodBody(results, "internal static void PollWheel()") != null
              && results.Contains("if (!WheelApplies(out bool succeeded, out ScrollViewport viewport, out float pitch, out float maxPixels))"),
            "and the scroll itself is settled by the frame's own sample, not by an IMGUI event");
        // The refusal count belongs to the fit, not to the placement.
        check(results.Contains("bool placed = strict && TryFit("),
            "Prepare decides the frame from TryFit's answer");
        check(results.Contains("ResultPlacementPolicy.Decide(placed, _strictMisses, Forced) == ResultPlacementOutcome.PlacedStrict"),
            "and only that answer clears the refusal count");
        check(!results.Contains("if (Placement(page, succeeded, out _"),
            "the placement's own answer no longer clears anything by itself");
        // The shared fallback still uses the floor gate; host failure can only use its measured action strip.
        string bandPolicy = File.ReadAllText(Path.Combine(root, "FallbackLayout.cs"));
        string fallbackPlacement = MethodBody(results, "private static bool FallbackPlacement(Component page");
        check(bandPolicy.Contains("ResultFallbackTier.AcceptsPageBand(room, boardFloor)")
              && fallbackPlacement != null && fallbackPlacement.Contains(": ResultFallbackBandPolicy.TryResolve(")
              && fallbackPlacement.Contains("controls.BelowActions.Top, controls.BelowActions.Bottom, FallbackFloor(), out band)"),
            "the shared fail-open policy still reads and enforces the board's own floor");
        string fitBody = MethodBody(results, "private static bool Fit(BoardStrip strip");
        check(fitBody != null && !fitBody.Contains("ResultFallbackLayout.OverlapOpacity")
              && results.Contains("total <= strip.Bottom - strip.Top + 0.5f"),
            "1.19.22 re-point: a board taller than the *page's own* band is refused and reported with need/have, never placed and merely faded - the fit's fallback placement is reached only for a board that really fits that band; the invented fail-open band is instead sized from the board, which the 1.19.22 suite drives");
        // 1.19.47 (review #4): the one place that keeps a board and fades it is the per-repaint fail-open re-check, and it
        // commits the place together with the strip it was just measured against - never "a new place against the old band".
        string recheck = MethodBody(results, "private static void RefreshFallbackSafety()");
        check(recheck != null && recheck.Contains("FadeInPlace(") && recheck.Contains("_holdStrip = strip;")
              && recheck.Contains("if (!FitsBand(total, strip))"),
            "and the fail-open re-check is where a board keeps its place and goes faint, with the place and the strip it was re-measured into committed in one block (review #4)");
        string fallback = File.ReadAllText(Path.Combine(root, "FallbackLayout.cs"));
        check(fallback.Contains("band=extended"),
            "and the invented band reports itself as extended when it grows past the measured wall");
        Console.WriteLine("REVIEW: RESULT_PLACEMENT_AND_BAND_EDGES (runtime policy + static source checks)");
    }
    private static void TheFailOpenBoardObeysTheSameBand(Action<bool, string> check)
    {
        // F8 asks for the fail-open board on the frame it is pressed; the band it is placed in is the page's, not the
        // window's.
        check(ResultFallbackPolicy.ShouldFallback(0, true),
            "F8 still reaches the fail-open board with no refusal behind it");
        bool resolved = ResultsBounds.TryResolve(ScreenHeight, IconRowBottom, ResultBoundSource.IconRow,
            float.NaN, WaitingTextTop, float.NaN, out ResultsBand band);
        check(resolved && band.Fits,
            "the band a forced board is placed in is the same one the strict path uses");

        // The old fail-open tier on the same page, for the comparison the release note makes: the client's screen
        // safe area runs to 1432 and would cover the waiting block at 1373; the band stops at 1351.7 instead.
        bool old = ResultFallbackLayout.TryResolve(ScreenHeight, float.NaN, float.NaN, float.NaN, float.NaN,
            ResultsFontGrowth.MinimumBoardHeight(30, 1.5f, PanelDensity.Compact), out ResultFallbackPlan plan);
        check(old && plan.Bottom > WaitingTextTop,
            $"the 1.19.17 client fallback really did run under the waiting block: {plan.Bottom:0} > {WaitingTextTop:0}");
        check(resolved && band.Bottom < WaitingTextTop,
            $"the band stops above it: {band.Bottom:0.#} < {WaitingTextTop:0}");

        string root = SourceRoot();
        if (root == null) { check(false, "the 1.19.18 regression locates the production sources"); return; }
        string results = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        string bounds = File.ReadAllText(Path.Combine(root, "ResultsBounds.cs"));

        check(bounds.Contains("public static bool TryResolve(float screenHeight, float upperAnchor, ResultBoundSource upperSource")
              && bounds.Contains("public static class ResultsBandFit"),
            "the band policy and the band-to-glyph mapping are one file, driven by this suite");
        // The fail-open placement resolves the band itself, through the policy that keeps the page's band first and the
        // invented tiers after it. 1.19.40 re-point: that order now lives in `ResultFallbackBandPolicy` beside the tier
        // arithmetic, so the check reads both files instead of one line of the display.
        int fallback = results.IndexOf("private static bool FallbackPlacement(Component page", StringComparison.Ordinal);
        int bandCall = results.IndexOf("ResultFallbackBandPolicy.TryResolve(Screen.height, anchorBottom, anchorSource", StringComparison.Ordinal);
        string bandFile = File.ReadAllText(Path.Combine(root, "FallbackLayout.cs"));
        int pageBand = bandFile.IndexOf("ResultsBounds.TryResolve(screenHeight, anchorBottom, anchorSource", StringComparison.Ordinal);
        int screenTier = bandFile.IndexOf("ResultFallbackLayout.TryResolve(screenHeight, anchorBottom, walls.PressableTop", StringComparison.Ordinal);
        check(fallback >= 0 && bandCall > fallback && pageBand > 0 && screenTier > pageBand,
            "the fail-open path asks the page's own band first and the window's safe area only after it");
        // The board is hung from the band's top edge, not centred in it.
        check(results.Contains("top = band.Top; room = band.Height;"),
            "the fail-open board starts at the band's upper edge");
        // The strict paths are bounded by the same policy.
        check(results.Contains("ResultsBounds.TryResolve(Screen.height, bandAnchor, bandAnchorSource"),
            "the success page's strip is replaced by the band");
        // 1.19.47（最新用户合同）：失败页的条带仍然由 `ResultsBounds` 收紧，但收紧的依据换成了窗口自己的上沿安全留边
        // ——原生「任务失败」标题只是说明文字，不再卡位置也不再卡字号（`ResultAutoBoardContract`）。标题仍然留给扫描的
        // 切分（`ScanFooterControls` 的 anchorBottom）和它自己的闸门，所以那条路径一个字都没动。
        check(results.Contains("ResultsBounds.TryResolve(Screen.height, top, bandTopSource,")
              && !results.Contains("ResultsBounds.TryResolve(Screen.height, titleBottom, ResultBoundSource.TitleAnchor"),
            "the failure page's strip is still tightened by `ResultsBounds`, now from the window's own safe top margin and never from the mission-failed title");
        // The icon row is the row and nothing else: the plate behind it is not an anchor.
        check(!IconRowReadsThePlate(results),
            "the upper anchor reads the icon row only - the white plate behind the icons is not the row's bottom edge");
        // Band height decides the glyph size - and the band the *table* gets is the strip minus the header the board
        // reserves above it (1.19.53): fitting against the whole strip answered glyphs whose board the placement then
        // refused, and the rows one whole-pixel step lower would have shown were lost.
        // 1.19.54（输入契约）：`FitBoard` 只收**正文高度**（调用方恰好扣一次标题），函数内部不再扣；两个严格调用点
        // 传的仍是 1.19.50 的 `tableRoom = room - header`，fail-open 的重拟合在成功页补扣一次、失败页原样传整条带。
        check(results.Contains("ResultsBandFit.FloorForBand(bodyRoom, content.Rows, Plugin.Density, requested, out bool squeezed)")
              && results.Contains("private static PanelGrid FitBoard(TableContent content, float bodyRoom,"),
            "the fit takes its floor from the body height the caller hands it, and nothing is reserved inside");
        Console.WriteLine("WIRING: RESULT_BAND (static source check; not a runtime behaviour test)");
    }

    private static void Wiring(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the 1.19.18 regression locates the production sources");
        if (root == null) return;
        string results = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        // Every early exit that used to leave the page blank keeps its line; the band adds one of its own per page.
        check(results.Contains("private static void ReportBand(string where, ResultsBand band"),
            "the band reports itself once per page, with both edges and the fact that produced each");
        // 1.19.41: the fail-open band passes the same reveal latch the strict placement does, so both paths measure the
        // row from the same reading (`CompletionFinalBounds.Placed`); 1.19.43 adds the F3 settled flag to the same call.
        check(results.Contains("IconRowBottom(page, RevealFinished, ContentSettled)") && results.Contains("FooterTextTop(page, out"),
            "the fail-open placement measures the icon row and the waiting block through the same two helpers, and reads the row against the same reveal state and settled flag the strict placement used");
        // The waiting block is a world-space TextMeshPro, measured through the menu camera like the icons.
        check(results.Contains("GetComponentsInChildren<TextMeshPro>(true)"),
            "the client's waiting block is measured as the TextMeshPro it is");
    }

    // `IconRowBottom` must read the label and the four icon slots and nothing else. The white plate
    // (`m_sectorsWhiteBox`) draws far below the icons; taking it as the row's bottom is what put a real client board
    // some 400 px down its page.
    private static bool IconRowReadsThePlate(string source)
    {
        // 1.19.41: the signature gained the reveal flag (`Placed` chooses between the laid-out slot and the deeper of
        // that slot and the drawn captions), so the anchor names the method rather than one argument list. What it
        // pins is unchanged: the plate behind the row is not one of the parts.
        int start = source.IndexOf("private static float IconRowBottom(", StringComparison.Ordinal);
        if (start < 0) return true;
        int end = source.IndexOf("private static float FooterTextTop(", start, StringComparison.Ordinal);
        if (end < 0) end = Math.Min(source.Length, start + 900);
        return source.Substring(start, end - start).Contains("m_sectorsWhiteBox");
    }

    // One method's body, by brace pairing, for the source-level assertions: it says which lines belong to a method, never
    // what they mean.
    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) return null;
        int open = source.IndexOf('{', start);
        if (open < 0) return null;
        int depth = 0;
        for (int index = open; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}' && --depth == 0) return source.Substring(open, index - open + 1);
        }
        return null;
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj"))) directory = directory.Parent;
        return directory != null ? directory.FullName : null;
    }

    private static bool near(float a, float b) => near(a, b, 0.01f);
    private static bool near(float a, float b, float tolerance) => Math.Abs(a - b) <= tolerance;
}
