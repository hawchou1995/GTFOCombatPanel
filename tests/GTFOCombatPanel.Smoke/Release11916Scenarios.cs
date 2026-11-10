using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// What 1.19.16 changes, driven through the real policy this build ships with.
//
// The defect: a host's success page has the button that sends the squad home, so the board's strip was bounded by that
// button's upper edge. A real host run measured `completion=519`, `lobbyButton=683` and `boardTop=535` of a 1440-high
// window - a 132 px strip - while the settlement board cannot be drawn under 270 px at any glyph size (thirty rows of
// the smallest readable glyph, 9 px each). `ResultsDisplay.Fit` therefore refused every frame, printed one
// `[DIAG] result board cannot fit` line and drew nothing, while the archive JSON was still written by
// `ResultsSaver.Save` before the fit ran, so the log looked like the page had been handled. A client never hit this:
// its strip is fixed page geometry (`ResultsGuestPageGeometry`) that runs to the foot of the screen and never asks a
// button anything. The same page had more than 700 px of empty space below a 683-px button.
//
// What replaced it: the measured controls of a host page are a gap the table steps around, not the end of the page.
// `ResultBoardBands.TryBelowActions` carves every control the interaction scan measured out of the page (widened by the
// page's own gap) and answers with the tallest free band left; `ResultPageRolePolicy.UsesBandBelowActions` decides that
// the host may use it, and only when the strip above the controls is shorter than the board's own floor and the band
// can hold it. `ResultsDisplay.SuccessRegion` then hands that band to the placement as the strip, records
// `ResultFooterSource.BelowActions`, and every per-repaint check validates the board against the band instead of the
// button's clearance line - so the button's own rectangle is still never covered, and `ResultBoardHold.TryPlace` still
// refuses a board taller than the band.
//
// Everything below runs against the real policy classes of this build with the real logged numbers. The section at the
// end reads the production sources instead, and says so.

internal static class Release11916Scenarios
{
    // The host run these figures come from: a 2560x1440 window, the success page, the completion section ending at 519,
    // the lobby button's measured upper edge at 683, the page gap of 16 px, the configured vertical offset of 3 px and a
    // board that measured 553x270 at the smallest readable glyph.
    private const float ScreenWidth = 2560f;
    private const float ScreenHeight = 1440f;
    private const float CompletionBottom = 519f;
    private const float LobbyButtonTop = 683f;
    private const float PageGap = 16f;
    private const float BoardOffsetY = 3f;
    private const float BoardFloorFromLog = 270f;
    private const int RowsFromLog = 30;

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

        TheBoardFloorIsTheLoggedTwoHundredAndSeventyPixels(check);
        TheLoggedHostStripCannotHoldTheBoard(check);
        TheButtonBecomesAGapInsteadOfTheEndOfThePage(check);
        TheBandIsAPlacementEveryRuleStillAppliesTo(check);
        APageThatLeavesNoBandIsRefusedWithItsNumbers(check);
        TheClientPageIsUnchangedAndNeverNeedsTheBand(check);
        TheClearanceLineIsNamedForWhatItMeasured(check);
        TheRoomIsTheBoundAndTheCliffIsGone(check);
        TheKillTierRowsAreOnlySpentWhenTheyBuyAGlyph(check);
        Wiring(check);
    }

    // ------------------------------------------------------- the board's own floor
    //
    // Thirty rows at the smallest readable glyph: `MinimumBoardHeight` reproduces the 270 px the refused fit logged, which
    // is what makes it something a page can be judged against before a single size is searched for.
    private static void TheBoardFloorIsTheLoggedTwoHundredAndSeventyPixels(Action<bool, string> check)
    {
        check(ImpactBoard.RowCount(true) == RowsFromLog,
            $"the settlement board is {RowsFromLog} rows with its three kill tiers split (row count={ImpactBoard.RowCount(true)})");

        float floor = ResultsFontGrowth.MinimumBoardHeight(RowsFromLog, 1.5f, PanelDensity.Compact);
        check(Math.Abs(floor - BoardFloorFromLog) < 0.01f,
            $"the floor a strip has to clear is exactly the {BoardFloorFromLog:0}px the refused host fit logged: floor={floor:0.##}");
        check(Math.Abs(ResultsFontGrowth.MinimumBoardHeight(RowsFromLog, 1.5f, PanelDensity.Normal) - BoardFloorFromLog) < 0.01f,
            "the face's own line height wins over the density scale, so every density has the same floor");
        check(Math.Abs(ResultsFontGrowth.MinimumBoardHeight(28, 1.5f, PanelDensity.Compact) - 252f) < 0.01f,
            "a board without the split kill tiers needs two rows less");
        check(ResultsFontGrowth.MinimumBoardHeight(0, 1.5f, PanelDensity.Compact) > 0f
              && ResultsFontGrowth.MinimumBoardHeight(1, float.NaN, PanelDensity.Compact) > 0f,
            "a zero row count and a face that reports no line height both still yield a usable floor");
    }

    // ------------------------------------------------------- the strip that refused the board
    //
    // The exact page the log came from. The old build's strip is reproduced first, then refused - which is the bug, not a
    // hypothesis: `Fit` runs `TryPlace` with these numbers and answers false, so no board was ever drawn.
    private static void TheLoggedHostStripCannotHoldTheBoard(Action<bool, string> check)
    {
        ResultsSafeArea anchored = ResultsRegion.Resolve(true, CompletionBottom, true, LobbyButtonTop, ScreenHeight, PageGap);
        check(anchored.Fits && Math.Abs(anchored.Top - 535f) < 0.01f && Math.Abs(anchored.Bottom - 667f) < 0.01f
              && Math.Abs(anchored.Height - 132f) < 0.01f,
            $"the host page's own measured edges leave the 132 px strip the log reported: ({anchored.Top:0.##}..{anchored.Bottom:0.##}) height={anchored.Height:0.##}");

        // Placement puts the configured offset on the strip's top edge, which is the 538..670 the log printed.
        float top = Math.Max(0f, anchored.Top) + BoardOffsetY;
        var strip = new BoardStrip(top, top + anchored.Height, true, LobbyButtonTop, float.NaN, true, ResultFooterSource.InteractiveBand);
        check(Math.Abs(strip.Top - 538f) < 0.01f && Math.Abs(strip.Bottom - 670f) < 0.01f,
            $"the placement's strip is the 538..670 the log printed: ({strip.Top:0.##}..{strip.Bottom:0.##})");

        check(!ResultBoardHold.TryPlace(ScreenWidth, ScreenHeight, strip, 553f, BoardFloorFromLog, 0f, out _),
            "the 132 px strip cannot hold the 553x270 board the host page measured - this is the frame that drew nothing");

        // And the reason it can never be squeezed in: the floor is the floor.
        check(ResultsFontGrowth.MinimumBoardHeight(RowsFromLog, 1.5f, PanelDensity.Compact) > strip.Bottom - strip.Top,
            "no glyph size can fit that board into that strip, because the board's own floor is taller than the strip");
    }

    // ------------------------------------------------------- the same page, with the button stepped around
    private static void TheButtonBecomesAGapInsteadOfTheEndOfThePage(Action<bool, string> check)
    {
        var lobbyButton = new LayoutRect(1080f, LobbyButtonTop, 800f, 44f);
        var controls = new List<ResultActionBounds> { new ResultActionBounds(lobbyButton) };

        check(ResultBoardBands.TryBelowActions(535f, ScreenHeight, controls, PageGap, out ResultBoardBand band),
            "a page whose only measured control is the lobby button still leaves a free band below it");
        check(band.Kind == ResultBoardBandKind.BelowActions,
            "that band is the one under the page's measured actions, not the strip the board was already refused");
        check(Math.Abs(band.Top - (LobbyButtonTop + 44f + PageGap)) < 0.01f,
            $"the band starts below the button's own rectangle plus the page gap: top={band.Top:0.##}");
        check(Math.Abs(band.Bottom - (ScreenHeight - ResultBoardBands.ScreenMargin)) < 0.01f,
            $"the band ends at the window's own margin: bottom={band.Bottom:0.##}");
        check(Math.Abs(band.Height - 689f) < 0.01f,
            $"the band is the 689 px of page below the button that the old build never used: height={band.Height:0.##}");

        check(ResultPageRolePolicy.UsesBandBelowActions(ResultPageRole.Host, 132f, BoardFloorFromLog, band.Height),
            "a host whose strip is shorter than the board's floor takes the band - this is the case that used to draw nothing");
        check(!ResultPageRolePolicy.UsesBandBelowActions(ResultPageRole.Host, 400f, BoardFloorFromLog, band.Height),
            "a host whose strip already holds the board keeps the strip it measured, so no page that worked before moves");
        check(!ResultPageRolePolicy.UsesBandBelowActions(ResultPageRole.Host, 132f, BoardFloorFromLog, 40f),
            "a host whose band is also too short is refused: the role alone never releases a placement");
        check(!ResultPageRolePolicy.UsesBandBelowActions(ResultPageRole.Client, 132f, BoardFloorFromLog, band.Height),
            "a client keeps its fixed page geometry and never takes this branch");
        check(!ResultPageRolePolicy.UsesBandBelowActions(ResultPageRole.Host, float.NaN, BoardFloorFromLog, band.Height)
              && !ResultPageRolePolicy.UsesBandBelowActions(ResultPageRole.Host, 132f, float.NaN, band.Height),
            "an unmeasured strip or floor never releases the band");

        var bandStrip = new BoardStrip(band.Top, band.Bottom, false, float.NaN, float.NaN, true, ResultFooterSource.BelowActions);
        check(ResultBoardHold.TryPlace(ScreenWidth, ScreenHeight, bandStrip, 553f, BoardFloorFromLog, 0f, out LayoutRect placed),
            "the board is placed in that band instead of being refused");
        check(Math.Abs(placed.Y - band.Top) < 0.01f && placed.Bottom <= band.Bottom + 0.01f,
            $"it hangs from the band's own top edge and stays inside it: y={placed.Y:0.##} bottom={placed.Bottom:0.##}");
        check(!ResultInteractionStripPolicy.OverlapsBoard(lobbyButton, placed, ResultBoardHold.ButtonClearance),
            "the placed board does not reach the lobby button's rectangle or its clearance band");
        check(placed.Bottom <= ScreenHeight - 8f,
            "and it stays inside the window's own margin, exactly as every other placement does");
    }

    // ------------------------------------------------------- every rule still applies inside the band
    private static void TheBandIsAPlacementEveryRuleStillAppliesTo(Action<bool, string> check)
    {
        var controls = new List<ResultActionBounds> { new ResultActionBounds(new LayoutRect(1080f, LobbyButtonTop, 800f, 44f)) };
        ResultBoardBands.TryBelowActions(535f, ScreenHeight, controls, PageGap, out ResultBoardBand band);
        var bandStrip = new BoardStrip(band.Top, band.Bottom, false, float.NaN, float.NaN, true, ResultFooterSource.BelowActions);

        // A band is a bound, not a licence: a board taller than the band is still refused rather than pushed over the
        // window's margin.
        check(ResultBoardHold.TryPlace(ScreenWidth, ScreenHeight, bandStrip, 553f, 600f, 0f, out LayoutRect tall)
              && tall.Bottom <= band.Bottom + 0.01f,
            "a board that grows into the band is placed as long as it really fits");
        // The real fit passes `slack = BoardSlack` - `ResultsDisplay.cs` declares `internal const float BoardSlack = 2f;`
        // and `Fit` hands it to this call - and `TryPlace` adds that tolerance to the strip's own lower edge before it
        // refuses. The two shapes answer differently, so both are asserted here: at slack=0 a board one pixel taller
        // than the band is refused, and at the slack production really passes the same board is *placed*, one pixel
        // into the window's 8 px margin. The assertion is therefore no stronger than the real call - it says what that
        // call does, not what the stricter one would do.
        check(!ResultBoardHold.TryPlace(ScreenWidth, ScreenHeight, bandStrip, 553f, band.Height + 1f, 0f, out _),
            "with no slack a board one pixel taller than the band is refused instead of running past the window's margin");
        check(ResultBoardHold.TryPlace(ScreenWidth, ScreenHeight, bandStrip, 553f, band.Height + 1f, 0f, out LayoutRect tolerated, ProductionSlack)
              && tolerated.Bottom <= band.Bottom + ProductionSlack + 0.01f
              && tolerated.Bottom <= ScreenHeight - 8f + ProductionSlack + 0.01f,
            $"the production call (slack={ProductionSlack:0}) accepts that same board inside its own tolerance: bottom={tolerated.Bottom:0.##}, band bottom={band.Bottom:0.##}");
        check(!ResultBoardHold.TryPlace(ScreenWidth, ScreenHeight, bandStrip, 553f, band.Height + ProductionSlack + 1f, 0f, out _, ProductionSlack),
            "and a board taller than the band plus that tolerance is still refused, so the tolerance is not a licence");
        // A control that was not there when the band was measured (another mod's clone, a footer that is re-laid out) is
        // still a control: the per-repaint scan reads it against the board and takes the vanilla page back. The band is
        // measured geometry, not a promise that the page cannot change.
        check(ResultInteractionStripPolicy.OverlapsBoard(new LayoutRect(tall.X, band.Top + 40f, 400f, 40f), tall, ResultBoardHold.ButtonClearance),
            "a control that appears inside the band is still read as an overlap with the board standing in it");

        // The per-repaint revalidation `BoardStillSafe` runs: the held strip is the band, so a board inside the band is
        // safe and one that would leave it is not.
        check(ResultBoardHold.TryPlace(ScreenWidth, ScreenHeight, bandStrip, 553f, BoardFloorFromLog, 0f, out LayoutRect inside)
              && ResultBoardHold.HeldGridFits(bandStrip, bandStrip, inside, BoardFloorFromLog, ScreenHeight, BoardFixSlack),
            "a board standing in the band passes the same per-repaint check every other placement passes");
        check(!ResultBoardHold.HeldGridFits(bandStrip, bandStrip,
                new LayoutRect(0f, band.Bottom - 10f, 553f, BoardFloorFromLog), BoardFloorFromLog, ScreenHeight, BoardFixSlack),
            "a board that would leave the band at the bottom is taken back, exactly as a board that reached the button is outside it");

        // A second control that leaves a band below itself is measured too: the band is the page's geometry, not the
        // single button the log happened to print.
        var twoControls = new List<ResultActionBounds>
        {
            new ResultActionBounds(new LayoutRect(1080f, LobbyButtonTop, 800f, 44f)),
            new ResultActionBounds(new LayoutRect(0f, 900f, 400f, 60f))
        };
        check(ResultBoardBands.TryBelowActions(535f, ScreenHeight, twoControls, PageGap, out ResultBoardBand belowBoth)
              && Math.Abs(belowBoth.Top - (900f + 60f + PageGap)) < 0.01f,
            $"a page with a second control below the button offers the band under the lower of the two: top={belowBoth.Top:0.##}");

        // The strip above the controls is never a band candidate: that one is what the caller already has.
        var highControl = new List<ResultActionBounds> { new ResultActionBounds(new LayoutRect(0f, 700f, 400f, 40f)) };
        check(ResultBoardBands.TryBelowActions(535f, ScreenHeight, highControl, PageGap, out ResultBoardBand underHigh)
              && underHigh.Top > 535f,
            "the band always starts below a measured control, never at the anchor the strip already hangs from");
    }

    private static void APageThatLeavesNoBandIsRefusedWithItsNumbers(Action<bool, string> check)
    {
        // A control that takes the whole page below the completion section: there is nowhere to stand, and the answer is
        // an explicit refusal rather than a board drawn over it.
        var wall = new List<ResultActionBounds> { new ResultActionBounds(new LayoutRect(0f, LobbyButtonTop, ScreenWidth, ScreenHeight - LobbyButtonTop - 8f)) };
        check(!ResultBoardBands.TryBelowActions(535f, ScreenHeight, wall, PageGap, out ResultBoardBand none),
            "a page whose measured control reaches the window's margin leaves no band at all");

        // A control that leaves a band too short for the board: the band exists, and it is still refused by the floor.
        var tight = new List<ResultActionBounds> { new ResultActionBounds(new LayoutRect(0f, 1300f, 400f, 40f)) };
        check(ResultBoardBands.TryBelowActions(535f, ScreenHeight, tight, PageGap, out ResultBoardBand shortBand) && shortBand.Height < BoardFloorFromLog,
            $"a band shorter than the board's floor is still measured and still reported: height={shortBand.Height:0.##}");
        check(!ResultPageRolePolicy.UsesBandBelowActions(ResultPageRole.Host, 132f, BoardFloorFromLog, shortBand.Height),
            "and the role policy refuses it, so the vanilla page is kept rather than a board drawn over the control");

        check(!ResultBoardBands.TryBelowActions(535f, ScreenHeight, null, PageGap, out _),
            "a page with no measured control offers no band: there is no evidence of where the free space is");
        check(!ResultBoardBands.TryBelowActions(float.NaN, ScreenHeight, new List<ResultActionBounds> { new ResultActionBounds(new LayoutRect(0f, 900f, 40f, 40f)) }, PageGap, out _),
            "an unreadable anchor offers no band");
        check(!ResultBoardBands.TryBelowActions(535f, 0f, new List<ResultActionBounds> { new ResultActionBounds(new LayoutRect(0f, 900f, 40f, 40f)) }, PageGap, out _),
            "an impossible window offers no band");
        check(!ResultBoardBands.TryBelowActions(535f, ScreenHeight,
                new List<ResultActionBounds> { new ResultActionBounds(new LayoutRect(0f, 900f, 0f, 40f)) }, PageGap, out _),
            "a control with no width is not a control and does not manufacture a band");
    }

    // ------------------------------------------------------- the client page, unchanged
    private static void TheClientPageIsUnchangedAndNeverNeedsTheBand(Action<bool, string> check)
    {
        // The same moment of the same run, as a client: the fixed page geometry already runs to the foot of the screen,
        // so the board fits without any band and nothing about the client path moved.
        bool resolved = ResultsGuestPageGeometry.TryResolve(CompletionBottom, ScreenWidth, ScreenHeight, PageGap,
            MinimumGuestStripHeightUsedByTheMod, out ResultsSafeArea clientArea);
        check(resolved, "the client's fixed page geometry still resolves the same strip it did in 1.19.15");
        // The client's strip is the page geometry scaled by screenHeight/1080: the anchor plus the scaled gap, down to the
        // scaled footer inset. At 1440 that is 519 + 21.33 = 540.33 and 1440 - 10.67 = 1429.33, exactly what 1.19.15
        // resolved - this release does not touch it.
        check(resolved && Math.Abs(clientArea.Top - (CompletionBottom + PageGap * (ScreenHeight / 1080f))) < 0.01f
              && Math.Abs(clientArea.Bottom - (ScreenHeight - 8f * (ScreenHeight / 1080f))) < 0.01f,
            $"the client's strip is its anchor plus the scaled page gap, down to the scaled footer inset: ({(resolved ? clientArea.Top : float.NaN):0.##}..{(resolved ? clientArea.Bottom : float.NaN):0.##})");
        var clientStrip = new BoardStrip(clientArea.Top, clientArea.Bottom, false, float.NaN, float.NaN, true,
            ResultFooterSource.ClientNoFooterSafeArea);
        check(ResultBoardHold.TryPlace(ScreenWidth, ScreenHeight, clientStrip, 553f, BoardFloorFromLog, 0f, out LayoutRect clientBoard),
            "the client board places in that strip exactly as before, with no band and no button asked for anything");
        check(clientBoard.Y >= clientArea.Top && clientBoard.Bottom <= clientArea.Bottom + 0.01f,
            "the client placement is unchanged: it hangs from the anchor and stays inside the fixed strip");

        // A client whose fixed strip had come out short does not fall back to the host's rule: its geometry is proven by
        // the geometry itself, so the refusal stays explicit.
        check(!ResultPageRolePolicy.UsesBandBelowActions(ResultPageRole.Client, 40f, BoardFloorFromLog, 689f),
            "a client never takes the host's band, so its behaviour cannot change behind this release");
        check(ResultPageRolePolicy.IsClient(ResultPageRolePolicy.Of(false)) && !ResultPageRolePolicy.IsClient(ResultPageRolePolicy.Of(true)),
            "the role is read from the native host flag, and an unreadable flag is a host");
        check(ResultPageRolePolicy.Unreadable == ResultPageRole.Host,
            "an unreadable native flag resolves to the host role, which is the path that measures and refuses");
        check(ResultPageRolePolicy.UsesNoFooterSafeArea(ResultPageRole.Client) && !ResultPageRolePolicy.UsesNoFooterSafeArea(ResultPageRole.Host),
            "the no-footer safe area is the client's rule, as it always was, and it is now named as one");
    }

    // ------------------------------------------------------- the wall's own field names, driven at runtime
    //
    // The figure the per-repaint refusal prints is the figure the page was placed against, and it is a *button* only
    // when that button was really read: `SuccessRegion` marks the reading `Measured` for the drawn band as well, so
    // "measured" alone cannot license the button's name. These run the production helper itself rather than reading its
    // source - the band case is the one a real host run printed (real button 683, band 743).
    // ------------------------------------------------------- the room is the bound, and the cliff is gone
    //
    // The host run this release came from, in the numbers the log printed: the success page measured a strip of 269 px
    // on one frame and 286 px on another, and the two frames ended in opposite answers - 269 px drew no board at all
    // (`success board deferred: need=270px ... have=240px`) while 286 px drew a 6 px table. Nothing about the page
    // changed between them except seventeen pixels, and the figure that decided it was a *readable* preference
    // (30 rows x 6 px x 1.5 em = 270 px) rather than the room the table can really be drawn in.
    //
    // `DrawableBoardHeight` answers the second question. These assertions pin its answers: the readable floor is
    // returned untouched when the room can hold it, the shortest drawable grid is returned when it cannot, and an
    // unmeasured room falls back to the readable floor so a page whose geometry could not be read behaves exactly as
    // it did before this existed.
    // ------------------------------------------------------- B: the kill-tier rows are a trade, not a default
    //
    // The split board is three kill rows rather than one, and those two extra rows only mean anything if they buy a
    // bigger glyph: `glyph = floor(room / rows / emPerGlyph)`. On a page with room to spare they buy nothing worth
    // having, so the board must not shed them. On a genuinely cramped page they can be worth a whole pixel of readable
    // text, and that is the only case in which this build collapses them.
    private static void TheKillTierRowsAreOnlySpentWhenTheyBuyAGlyph(Action<bool, string> check)
    {
        float emPerGlyph = ResultsBandFit.EmPerGlyphPixel(PanelDensity.Compact, 1.5f);
        check(Math.Abs(emPerGlyph - 1.5f) < 0.01f,
            $"one glyph pixel costs 1.5 row pixels at compact density: emPerGlyph={emPerGlyph:0.##}");
        check(ImpactBoard.RowCount(true) == 30 && ImpactBoard.RowCount(false) == 28,
            $"the split board is 30 rows and the collapsed one 28: {ImpactBoard.RowCount(true)}/{ImpactBoard.RowCount(false)}");

        // Every page that has room for a readable table keeps all 30 rows - the behaviour this build inherits.
        foreach (float room in new[] { 270f, 286f, 400f, 588f, 800f, 1080f })
            check(ImpactBoard.SplitKillTiersFor(room, emPerGlyph),
                $"a {room:0}px page has room to spare and keeps its three kill tiers");

        // A page too short for either board keeps them too: the rule only spends them when they buy something.
        foreach (float room in new[] { 100f, 135f, 150f, 180f, 200f })
            check(ImpactBoard.SplitKillTiersFor(room, emPerGlyph),
                $"a {room:0}px page gains nothing from two fewer rows and keeps its three kill tiers");

        // The cramped band the log printed: 269 px gives a 5 px glyph at 30 rows and a 6 px one at 28, so the rows are
        // spent - that is the page this rule is for.
        check(!ImpactBoard.SplitKillTiersFor(269f, emPerGlyph),
            "a 269px page buys a 5px -> 6px glyph with the two rows, so the collapsed board is chosen");

        // An unmeasured room is not a licence to change the board's content.
        check(ImpactBoard.SplitKillTiersFor(float.NaN, emPerGlyph)
              && ImpactBoard.SplitKillTiersFor(0f, emPerGlyph)
              && ImpactBoard.SplitKillTiersFor(-10f, emPerGlyph),
            "an unmeasured, zero or negative room keeps the richer board");
        check(ImpactBoard.SplitKillTiersFor(269f, float.NaN) && ImpactBoard.SplitKillTiersFor(269f, 0f),
            "an unmeasured glyph cost keeps the richer board");

        // The two boards hold the same fields; only the kill tiers are merged.
        check(ImpactBoard.MetricRowCount(true) - ImpactBoard.MetricRowCount(false) == 2,
            "collapsing the tiers costs exactly two metric rows");
    }

    private static void TheRoomIsTheBoundAndTheCliffIsGone(Action<bool, string> check)
    {
        float readable = ResultsFontGrowth.MinimumBoardHeight(RowsFromLog, 1.5f, PanelDensity.Compact);
        check(Math.Abs(readable - BoardFloorFromLog) < 0.01f,
            $"the readable floor is still the {BoardFloorFromLog:0}px it always was: floor={readable:0.##}");

        // The shortest grid this build can draw: 30 rows at the 3 px absolute minimum, 1.5 em per row = 135 px.
        float shortest = ResultsBandFit.MinimumDrawableHeight(RowsFromLog, PanelDensity.Compact);
        check(Math.Abs(shortest - 135f) < 0.01f,
            $"the shortest grid this build can draw for {RowsFromLog} rows is 135px: shortest={shortest:0.##}");

        // The two strips the log printed, and the answer each of them now gets.
        float tight = ResultsFontGrowth.DrawableBoardHeight(RowsFromLog, 1.5f, PanelDensity.Compact, 269f);
        float roomy = ResultsFontGrowth.DrawableBoardHeight(RowsFromLog, 1.5f, PanelDensity.Compact, 286f);
        check(Math.Abs(tight - shortest) < 0.01f,
            $"a 286px strip is judged by the same room rule, so the two frames no longer disagree: floor={roomy:0.##}");
        check(tight <= 269f + 0.5f && roomy <= 286f + 0.5f,
            $"neither floor is taller than the strip that produced it: tight={tight:0.##} of 269, roomy={roomy:0.##} of 286");

        check(Math.Abs(roomy - readable) < 0.01f,
            $"a 286px strip can hold the readable table, so it keeps the 270px floor: floor={roomy:0.##}");
        // The two answers now differ by the real reason - 286 px can hold a readable table and 269 px cannot - instead
        // of by seventeen pixels straddling a preference. Both are placements; neither is a blank page.
        check(tight < roomy,
            $"the two frames no longer straddle a cliff: 269px asks for {tight:0.##}px and 286px asks for {roomy:0.##}px");
        check(tight <= 269f + 0.5f && roomy <= 286f + 0.5f,
            $"neither floor is taller than the strip that produced it: tight={tight:0.##} of 269, roomy={roomy:0.##} of 286");

        // The property the bug was about: a 269 px strip is no longer refused. The board that fits it is the shortest
        // grid, and the production placement accepts it in the strip the page measured.
        var strip = new BoardStrip(999f, 1268f, false, float.NaN, float.NaN, true, ResultFooterSource.ScreenSafeArea);
        check(Math.Abs((strip.Bottom - strip.Top) - 269f) < 0.01f,
            $"the strip is the 269px one the log printed: ({strip.Top:0}..{strip.Bottom:0})");
        check(ResultBoardHold.TryPlace(ScreenWidth, ScreenHeight, strip, 529f, shortest, 0f, out _),
            "the 269px strip that drew nothing now holds the shortest grid the board can be built at");

        // A room that can hold the readable table keeps it: the fix lowers the judgement figure, never the target.
        float large = ResultsFontGrowth.DrawableBoardHeight(RowsFromLog, 1.5f, PanelDensity.Compact, 588f);
        check(Math.Abs(large - readable) < 0.01f,
            $"a 588px strip is still judged against the 270px readable floor: floor={large:0.##}");

        // No measurement is not a licence to lower the bar.
        check(Math.Abs(ResultsFontGrowth.DrawableBoardHeight(RowsFromLog, 1.5f, PanelDensity.Compact, float.NaN) - readable) < 0.01f
              && Math.Abs(ResultsFontGrowth.DrawableBoardHeight(RowsFromLog, 1.5f, PanelDensity.Compact, 0f) - readable) < 0.01f
              && Math.Abs(ResultsFontGrowth.DrawableBoardHeight(RowsFromLog, 1.5f, PanelDensity.Compact, -40f) - readable) < 0.01f,
            "an unmeasured, zero or negative room keeps the readable floor, so an unreadable page behaves as it did before");

        // A band shorter than the shortest grid is still refused - by the real figure, not by a preference.
        check(Math.Abs(ResultsFontGrowth.DrawableBoardHeight(RowsFromLog, 1.5f, PanelDensity.Compact, 100f) - shortest) < 0.01f,
            "a 100px strip is judged against the shortest grid, so a refusal is about the room and not about a glyph preference");
        var cramped = new BoardStrip(400f, 500f, false, float.NaN, float.NaN, true, ResultFooterSource.ScreenSafeArea);
        check(!ResultBoardHold.TryPlace(ScreenWidth, ScreenHeight, cramped, 529f, shortest, 0f, out _),
            "a 100px strip still cannot hold a 135px grid: the floor was lowered to the room, not removed");
    }

    // The figure the per-repaint refusal prints is the figure the page was placed against, and it is a *button* only
    // when that button was really read: `SuccessRegion` marks the reading `Measured` for the drawn band as well, so
    // "measured" alone cannot license the button's name. These run the production helper itself rather than reading its
    // source - the band case is the one a real host run printed (real button 683, band 743).
    private static void TheClearanceLineIsNamedForWhatItMeasured(Action<bool, string> check)
    {
        // The band of a page whose own button could not be read by name: measured, and still not a button.
        string band = ResultFooterProvenance.LobbyWall(ResultFooterSource.InteractiveBand, 743f, true);
        check(!band.Contains("lobbyButton=") && band.Contains("lobbyEdgeValue=743") && band.Contains("source=band"),
            $"a wall that came from the drawn band never wears the button's name: {band}");
        check(band == "lobbyEdgeValue=743,source=band",
            $"the band case is exactly the figure and its source: {band}");

        // The same page, as the reading the strip was resolved from.
        var drawn = new ResultsRegion.LobbyEdgeReading(743f, ResultsRegion.LobbyEdgeSource.Band);
        check(ResultFooterProvenance.Of(drawn) == ResultFooterSource.InteractiveBand
              && ResultFooterProvenance.LobbyWall(drawn) == "lobbyEdgeValue=743,source=band",
            $"a band reading is recorded as a band and printed as one: {ResultFooterProvenance.LobbyWall(drawn)}");


        // ...and the button itself, read while the player can press it and read from its own layout while the page is
        // still revealing it. Both are that control's own upper edge, so both are named as it - with the real figure.
        var pressable = new ResultsRegion.LobbyEdgeReading(683f, ResultsRegion.LobbyEdgeSource.Pressable);
        var laidOut = new ResultsRegion.LobbyEdgeReading(683f, ResultsRegion.LobbyEdgeSource.Layout);
        check(ResultFooterProvenance.Of(pressable) == ResultFooterSource.NamedControl
              && ResultFooterProvenance.LobbyWall(pressable) == "lobbyButton=683",
            $"a pressable button is printed as the button it is: {ResultFooterProvenance.LobbyWall(pressable)}");
        check(ResultFooterProvenance.Of(laidOut) == ResultFooterSource.NamedControl
              && ResultFooterProvenance.LobbyWall(laidOut) == "lobbyButton=683",
            $"the button's own layout is printed as that same button: {ResultFooterProvenance.LobbyWall(laidOut)}");

        // A page that measured no wall at all says so, and names the source it did not get.
        var none = new ResultsRegion.LobbyEdgeReading(float.NaN, ResultsRegion.LobbyEdgeSource.None);
        check(ResultFooterProvenance.Of(none) == ResultFooterSource.None
              && ResultFooterProvenance.LobbyWall(none) == "lobbyEdgeValue=unmeasurable,source=none",
            $"a page with no measurable wall names no source and no figure: {ResultFooterProvenance.LobbyWall(none)}");
    }

    private const float BoardFixSlack = 2f;
    // The tolerance the real fit passes (`ResultsDisplay.BoardSlack`, which this suite cannot link): a board may hang
    // at most this far past the strip it was measured into, and never past the window's own margin plus it.
    private const float ProductionSlack = 2f;
    private const float MinimumGuestStripHeightUsedByTheMod = 96f;

    // ------------------------------------------------------- the wiring, read from the production source
    //
    // Static source checks only: they say the real page calls these policies, that the host branch is the one carrying
    // them, and that the two lines the bug report quoted can no longer be printed. They are not runtime proof.
    private static void Wiring(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the 1.19.16 regression locates the production sources");
        if (root == null) return;

        string layout = File.ReadAllText(Path.Combine(root, "LayoutPolicy.cs"));
        string visibility = File.ReadAllText(Path.Combine(root, "ResultVisibility.cs"));
        string results = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));

        check(layout.Contains("public static class ResultBoardBands") && layout.Contains("public static bool TryBelowActions(")
              && layout.Contains("public enum ResultBoardBandKind"),
            "the band arithmetic is one policy of its own: ResultBoardBands.TryBelowActions");
        check(layout.Contains("public static float MinimumBoardHeight("),
            "the board's own floor is a policy figure, not a number written into the page code");

        check(visibility.Contains("public enum ResultPageRole") && visibility.Contains("public static class ResultPageRolePolicy")
              && visibility.Contains("public static bool UsesBandBelowActions(") && visibility.Contains("public static bool UsesNoFooterSafeArea("),
            "the two roles are one explicit policy, with a documented way out for each of them");
        check(visibility.Contains("BelowActions = 4"),
            "a placement made in the band says so in its own footer source, so a log line and a re-measure can tell it apart");

        // The scan measures the band, the page decides to use it, and the placement records it.
        check(CountOf(results, "ResultBoardBands.TryBelowActions(") == 1,
            $"the band is measured exactly once, in the scan that holds the rectangles: calls={CountOf(results, "ResultBoardBands.TryBelowActions(")}");
        // 1.19.20: the host branch asks the policy with the floor resolved *from the strip this page measured*
        // (`stripFloor`), because judging a 269 px strip against the 270 px readable floor is the cliff that drew no
        // board next to a 286 px strip that drew a 6 px one. The figure passed in must be the room-resolved one, and it
        // must still come from a policy call rather than being written as a number.
        check(results.Contains("ResultPageRolePolicy.UsesBandBelowActions(Role(), anchoredHeight, bandQuestion, controls.BelowActions.Height)"),
            "the success page's host branch asks the role policy before it moves the board into the band");
        check(results.Contains("float stripFloor = area.Fits")
              && results.Contains("ResultsFontGrowth.DrawableBoardHeight(stripRows, stripRowEm, Plugin.Density, anchoredHeight)"),
            "the figure the band policy is asked with is resolved from the strip the page measured, not from the readable floor");
        // C: the escape hatch has to be answerable. The band under the page's actions on the run this came from measured
        // 85-88 px, and the question asked of it used to be the 270 px readable floor - a comparison no page could ever
        // win, which is why the branch never fired there. It is asked with the room figure now.
        // The policy fires exactly when the strip cannot hold the board *and* the band under the page's actions can:
        // 100 px of strip against a 135 px drawable grid, with a 200 px band beneath the controls, is that case.
        check(ResultPageRolePolicy.UsesBandBelowActions(ResultPageRole.Host, 100f, 135f, 200f),
            "a 100 px strip that cannot hold the board steps into a 200 px band when 135 px is the drawable floor");
        // A strip that already holds the board needs no escape hatch: the board simply stands in it.
        check(!ResultPageRolePolicy.UsesBandBelowActions(ResultPageRole.Host, 269f, 135f, 200f),
            "a 269 px strip that already holds the board is used as it is, without stepping around the page's actions");
        // The run this came from measured an 85-88 px band against a 135 px drawable grid, so on that page the answer
        // stays no - honestly. What C fixes is that the question is answerable at all: the old form compared 88 px
        // against the 270 px readable floor, which no band on that page could ever clear.
        check(!ResultPageRolePolicy.UsesBandBelowActions(ResultPageRole.Host, 100f, 135f, 88f),
            "an 88 px band is refused against a 135 px drawable grid instead of against a 270 px preference");
        check(!ResultPageRolePolicy.UsesBandBelowActions(ResultPageRole.Host, 100f, 135f, 134f),
            "a band still shorter than the drawable floor is not offered: the floor was lowered to the room, not removed");
        check(!ResultPageRolePolicy.UsesBandBelowActions(ResultPageRole.Client, 100f, 135f, 1000f),
            "a client page never uses the host's band escape hatch, whatever band it has");
        check(results.Contains("footerSource = ResultFooterSource.BelowActions;"),
            "the band placement records its own footer source");
        check(GuardedFromGuestBranch(results, "ResultPageRolePolicy.UsesBandBelowActions(Role(), anchoredHeight"),
            "the band branch sits in the host side of the page's role chain, so a client page can never reach it");

        // The silent refusals the bug report listed now say something, once per page.
        check(!results.Contains("if (_grid == null || _content == null || _page == null || !_page.gameObject.activeInHierarchy) return;"),
            "the draw's silent early return now reports why there is nothing to draw");
        check(!results.Contains("if (!Completion.Observe(Time.frameCount, true, successArea.Top)) return false;"),
            "the success page's unsettled completion edge is reported instead of only refusing");
        check(!results.Contains("if (board.Y < safeTop - BoardSlack || board.Bottom > bottom + BoardSlack) return false;")
              && !results.Contains("if (successButtonMeasured && board.Bottom > successButtonTop - ResultBoardHold.ButtonClearance + BoardSlack) return false;"),
            "the per-repaint checks report the numbers that refused the frame");
        check(CountOf(results, "ReportHeldBack(") >= 8,
            $"every silent refusal path reports once per page: calls={CountOf(results, "ReportHeldBack(")}");
        check(results.Contains("reason={reason}, role={ResultPageRolePolicy.Name(Role())}")
              && results.Contains("need={_boardFloor:0}px"),
            "those lines carry reason, role, the height needed and the strip that was measured");

        // The two sentences the bug report quoted as untrue.
        check(!results.Contains("drawn, or laid out and still revealing"),
            "the success region line no longer claims a board is drawn or revealing when only a strip was measured");
        check(!results.Contains("the guest safe-area proof is incomplete"),
            "a host is no longer told about a client proof it can never have; the line is role-aware");

        // One role read, and every branch that depends on it goes through it.
        check(CountOf(results, "SNet.IsMaster") == 2,
            $"the native host flag is read in exactly two places - the role helper and the scan's own coverage edge: reads={CountOf(results, "SNet.IsMaster")}");
        check(results.Contains("private static ResultPageRole Role()") && results.Contains("private static bool GuestClient() => ResultPageRolePolicy.IsClient(Role());"),
            "both the role and the guest test come from the one policy");

        // The offset that used to be sanitised for a client only.
        check(!results.Contains("Mathf.Max(0f, Plugin.ResultsOffsetY.Value)")
              && !results.Contains("float guestOffsetY = Plugin.ResultsOffsetY.Value;"),
            "a configured offset of NaN is sanitised for both roles, so a host page cannot become a NaN strip");

        check(results.Contains("private static string DescribeBand(ResultFooterSource source)"),
            "the success region line names the band it may use, so a log alone says where the board could stand");
        check(results.Contains("strip.FooterSource != ResultFooterSource.BelowActions"),
            "a board standing in the band is not treated as a guess by the one-shot re-measure");

        // The lines this release added may only state what was measured, field by field. These six read the source, so
        // they say the wording is in place - the runtime behaviour behind it is asserted through the policies above.
        check(!results.Contains("the board is laid out in the free band below them")
              && results.Contains("and the free band below them is offered to the fit"),
            "the band line offers the band to the fit instead of claiming the board is already laid out in it");
        check(results.Contains("boardBand=(") && !results.Contains(": band=("),
            "the band field is now `boardBand=`, so it no longer collides with the `lobbyEdge=band` source it is printed beside");
        // The button's name belongs to the control and to nothing else, and it is spelled out in exactly one place: the
        // branch that really read that control. Any other line printing it - a call site, a band line, a refusal - is the
        // same false field name this release removed, so the check reads every production source with its comments
        // stripped and fails on a second occurrence, whatever constructed it.
        check(CountOf(results, "DescribeLobbyWall(wall)") == 3
              && results.Contains("private static string DescribeLobbyWall(ResultsRegion.LobbyEdgeReading wall) => ResultFooterProvenance.LobbyWall(wall);"),
            $"all three success-region lines describe the wall through the one helper: calls={CountOf(results, "DescribeLobbyWall(wall)")}");
        check(results.Contains("ResultFooterProvenance.LobbyWall(successFooterSource, successButtonTop, successButtonMeasured)"),
            "the per-repaint refusal prints its wall through that same helper, so a band top can no longer be printed as the button's");
        var lobbyButtonLines = new List<string>();
        foreach (string sourceFile in Directory.GetFiles(root, "*.cs"))
            foreach (string sourceLine in File.ReadAllLines(sourceFile))
            {
                int comment = sourceLine.IndexOf("//", StringComparison.Ordinal);
                string code = comment >= 0 ? sourceLine.Substring(0, comment) : sourceLine;
                if (code.Contains("lobbyButton=")) lobbyButtonLines.Add(Path.GetFileName(sourceFile) + ":" + code.Trim());
            }
        check(lobbyButtonLines.Count == 1 && lobbyButtonLines[0].Contains("ResultFooterSource.NamedControl"),
            $"the button's field name is printed nowhere but the branch that read the button: {string.Join(" | ", lobbyButtonLines)}");
        check(results.Contains("heldStrip=none, have=unmeasurable, top=unmeasurable, bottom=unmeasurable,"),
            "a page that never gave the board a strip prints no zero box for it - not `heldStrip=none` beside `top=0, bottom=0`");
        check(results.Contains("boardTop=unmeasurable, room=unmeasurable") && results.Contains("private static string DescribeStrip("),
            "and an unresolved strip says the same in the success region line and in the three scan lines");
        check(!results.Contains("the board is drawn as usual"),
            "no line printed before the fit claims the board was drawn: the downgrade line says the page is not deferred, not that the table landed");

        Console.WriteLine("WIRING: RESULT_BOARD_BAND_BELOW_ACTIONS (static source checks; not a runtime behaviour test)");
    }

    private static int CountOf(string source, string needle)
    {
        int count = 0;
        int at = -1;
        while ((at = source.IndexOf(needle, at + 1, StringComparison.Ordinal)) >= 0) count++;
        return count;
    }

    // The call sits in the `else` side of the nearest role chain above it: the `if (GuestClient())` branch that starts
    // that chain has already closed by the time the call is reached. Brace counting only.
    private static bool GuardedFromGuestBranch(string source, string needle)
    {
        int at = source.IndexOf(needle, StringComparison.Ordinal);
        if (at < 0) return false;
        int gate = source.LastIndexOf("if (GuestClient())", at, StringComparison.Ordinal);
        if (gate < 0) return false;
        int open = source.IndexOf('{', gate);
        if (open < 0 || open > at) return false;
        int depth = 0;
        for (int index = open; index < at; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0) return true;
            }
        }
        return false;
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj"))) directory = directory.Parent;
        return directory != null ? directory.FullName : null;
    }
}
