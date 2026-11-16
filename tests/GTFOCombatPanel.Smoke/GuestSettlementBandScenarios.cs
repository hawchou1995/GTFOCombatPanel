using System;
using HawChow.GTFOCombatPanel;

// The client settlement page's band, in the numbers a live 2560x1440 failure page was recorded with, driven through the
// production rules the runtime calls.
//
// The recorded page (1.19.25, role=client, 30 rows, 1 player, compact):
//   title layout edge 868, design clearance 16 * 1440/1080 = 21.33, waiting-status line at 1227
//   fixed client page geometry   strip 889.33 .. 1429.33  (room 540)
//   the band the page really offered  889.33 .. 1205.67    (room 316.33, foot = the waiting line minus the clearance)
//   the board drawn into it  165..173 x 270 at y=889, glyph 6px, blankBelow 46px - i.e. 85% of the band
//   the waiting line then animated 30/31/32 px lower, the page was re-measured, and the board was fitted again
//
// What this file does and does not cover, said plainly because the difference matters:
//   * every figure below comes out of a production type the runtime calls - `ResultsBounds`, `ResultsGuestPageGeometry`,
//     `ResultsBandFit`, `ResultsFontGrowth`, `ResultBoardHold`, `ResultInteractionStripPolicy`, `ResultAnchorDrift`,
//     `ResultTogglePolicy` and `PanelGeometry.Build` (the row/column arithmetic `HudTable.BuildGrid` delegates to);
//   * nothing here runs Unity. `HudTable.Draw`, IMGUI and the native page are not reachable from this project, so no
//     assertion below counts real draw calls or reads a pixel back - a claim about what the screen looked like would have
//     to come from a live page, and this file never makes one;
//   * where a fact only exists inside `ResultsDisplay`/`ResultVisibility`/`LayoutPolicy` source (the wiring), the
//     assertion is made against that file's source text and its description says so.
internal static class GuestSettlementBandScenarios
{
    // `ResultsDisplay.MessageGap` and its client floor; asserted against the source in `TheDesignNumbersAreTheOnesDriven`.
    private const float MessageGap = 16f;
    private const float MinimumGuestStripHeight = 96f;
    // The in-level table's glyph size, which is the seed the settlement fit climbs from (`FontSizeForSharing`): the
    // recorded page's configuration carried FontSize=17.
    private const int Seed = 17;
    // The board is fitted inside the width the page offers: `Mathf.Max(320f, Screen.width - 48f)` at 2560.
    private const float BoardWidth = 2512f;
    private const float ScreenWidth = 2560f, ScreenHeight = 1440f;

    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        TheRecordedClientBandIsFilledToItsFoot(check);
        TheWaitingLineMovingDownDoesNotResizeTheBoard(check);
        TheHostBandKeepsTheComfortCeiling(check);
        TheCeilingFollowsTheFootThePageMeasured(check);
        AShortBandIsAnsweredTheSameWayAsBefore(check);
        AnImpossibleBandIsRefusedRatherThanFilled(check);
        TheBoardNeverReachesTheWaitingLineOrTheButton(check);
        TheSuccessPageIsFilledByTheSameRule(check);
        F8AnswersFromWhatThePageShows(check);
        GuestSettlementBandWallScenarios.RunInto(check);
        ABoardIsFittedIntoTheStripItWasMeasuredAgainst(check);
        TheDesignNumbersAreTheOnesDriven(check);
    }

    // The defect, in the recorded numbers: a 316px client band, 30 rows, the same table, two ceilings.
    private static void TheRecordedClientBandIsFilledToItsFoot(Action<bool, string> check)
    {
        float clearance = ResultsBounds.Clearance(ScreenHeight);
        check(near(clearance, 21.333f, 0.01f),
            $"the design clearance scales with the window: 16px at 1080p is {clearance:0.##}px at 1440");

        bool resolved = ResultsBounds.TryResolve(ScreenHeight, 868f, ResultBoundSource.TitleAnchor, float.NaN, 1227f,
            float.NaN, out ResultsBand band);
        check(resolved && near(band.Top, 889.333f, 0.01f) && near(band.Bottom, 1205.667f, 0.01f),
            $"the recorded band is the title edge plus the clearance down to the waiting line minus the clearance ({band.Top:0.##}..{band.Bottom:0.##})");
        check(band.BottomSource == ResultBoundSource.FooterText && !band.Clamped && near(band.Height, 316.333f, 0.01f),
            $"and its foot is the waiting-status line itself, not the page edge ({ResultsBounds.Name(band.BottomSource)}, room {band.Height:0.##}px)");

        bool guest = ResultsGuestPageGeometry.TryResolve(868f, ScreenWidth, ScreenHeight, MessageGap,
            MinimumGuestStripHeight, out ResultsSafeArea fixedPage);
        check(guest && near(fixedPage.Top, 889.333f, 0.01f) && near(fixedPage.Bottom, 1429.333f, 0.01f),
            $"the client's fixed page geometry offers the same top and the page bottom ({fixedPage.Top:0.##}..{fixedPage.Bottom:0.##}) - the 540px strip the log printed as 889..1429");

        int rows = ImpactBoard.RowCount(true);
        check(rows == 30, $"the recorded page's board is the split 30-row board ({rows} rows)");
        float em = ResultsBandFit.EmPerGlyphPixel(PanelDensity.Compact, PanelGeometry.MinimumRowEm);
        check(near(em, 1.5f, 0.001f), $"one glyph pixel buys {em:0.##}px of row at the compact density");

        int floor = ResultsBandFit.FloorForBand(band.Height, rows, PanelDensity.Compact,
            ResultsFontGrowth.ReadabilityFloor(float.NaN), out bool squeezed);
        check(floor == ResultsFontGrowth.MinimumReadablePixels && !squeezed,
            $"a client page measures no pressable button, so the readability floor is the absolute one ({floor}px) and the 316px band is not squeezing it");

        // The ceiling that shipped: 96% of the band, which refuses the 7px table by a single pixel of comfort.
        int shippedFont = ResultsFontGrowth.FitBoardIntoStrip(Seed, floor, int.MaxValue, band.Height, BoardWidth,
            Natural(rows), out int shippedFloor);
        float shippedHeight = Natural(rows)(shippedFont).Height;
        check(shippedFont == 6 && near(shippedHeight, 270f, 0.01f) && shippedFloor == floor,
            $"the comfort ceiling answers the recorded {shippedFont}px glyph and the {shippedHeight:0}px board the live page drew");
        check(near(band.Height - shippedHeight, 46.33f, 0.5f),
            $"and leaves {band.Height - shippedHeight:0.##}px of the band unused - the log's blankBelow=46px, i.e. four whole rows of the table it could have drawn");

        // The ceiling the client band earns: its foot is a wall the page measured, so the whole band may be used.
        float ceiling = ResultsFontGrowth.FillCeiling(ResultFooterSource.ClientNoFooterSafeArea, footIsMeasuredWall: true);
        check(ceiling == ResultsFontGrowth.BandFillCeiling,
            $"a client band whose foot is the waiting-status wall gets the whole band ({ceiling:0.00})");
        int grownFont = ResultsFontGrowth.FitBoardIntoStrip(Seed, floor, int.MaxValue, band.Height, BoardWidth,
            Natural(rows), out int grownFloor, ceiling);
        ResultTableSize grown = Natural(rows)(grownFont);
        check(grownFont == 7 && near(grown.Height, 315f, 0.01f) && grownFloor == floor,
            $"so the same band is answered with the {grownFont}px glyph and the {grown.Height:0}px board instead of a one-step-smaller table");
        check(grown.Height <= band.Height + 0.5f,
            $"and that board is still inside the band ({grown.Height:0}px <= {band.Height:0.##}px), so nothing is drawn over the waiting line");
        check(band.Height - grown.Height < 2f,
            $"the band is used to within {band.Height - grown.Height:0.##}px - less than one row ({grown.Height / rows:0.##}px), so the board no longer leaves a blank band under it");

        // The board the grown fit produces, placed by the production placement inside the strip it was measured against.
        var strip = new BoardStrip(band.Top, band.Bottom, false, float.NaN, float.NaN, true,
            ResultFooterSource.ClientNoFooterSafeArea, ceiling);
        bool placed = ResultBoardHold.TryPlace(ScreenWidth, ScreenHeight, strip, grown.Width, grown.Height, 0f,
            out LayoutRect origin);
        check(placed && near(origin.Y, band.Top, 0.01f) && near(origin.Bottom, band.Top + 315f, 0.01f),
            $"the board hangs from the band's own top ({origin.Y:0.##}..{origin.Bottom:0.##}) and keeps the size the fit chose");
        check(origin.Bottom < 1227f - 0.5f,
            $"its foot stays {(1227f - origin.Bottom):0.##}px above the waiting-status line at 1227, so the line is never drawn over");
        check(origin.Bottom <= strip.Bottom + 0.01f,
            $"and inside the band's own foot ({origin.Bottom:0.##} <= {strip.Bottom:0.##})");

        // The shipped fit placed the smaller board at the same top with 46px of page under it: the two boards a player saw
        // as "first a small one, then a bigger one" are the same rule answering the same band with two different ceilings.
        ResultTableSize shipped = Natural(rows)(shippedFont);
        bool shippedPlaced = ResultBoardHold.TryPlace(ScreenWidth, ScreenHeight, strip, shipped.Width, shipped.Height, 0f,
            out LayoutRect shippedOrigin);
        check(shippedPlaced && near(shippedOrigin.Y, origin.Y, 0.01f) && shippedOrigin.Bottom < origin.Bottom,
            "the smaller board of the recorded page stood at the same top edge - the size and the left edge were what moved, which is what the player reported");
    }

    // The waiting-status line animating 30/31/32px lower is the recorded re-measure. With the band filled to its foot the
    // re-measure answers the same board, so the page is never rebuilt at a second size.
    //
    // This covers the fit and the placement - `ResultsBandFit`, `ResultsFontGrowth`, `ResultBoardHold` and
    // `ResultAnchorDrift`, each driven in this file - which is where a re-measure would change a rectangle. It does not
    // issue a repaint: whether `ResultsDisplay.Draw` painted once or twice on a live page is not something this project can
    // measure, and nothing below claims it.
    private static void TheWaitingLineMovingDownDoesNotResizeTheBoard(Action<bool, string> check)
    {
        string[] waiting = { "1227", "1257", "1258", "1259" };
        float[] lines = { 1227f, 1257f, 1258f, 1259f };
        float ceiling = ResultsFontGrowth.FillCeiling(ResultFooterSource.ClientNoFooterSafeArea, footIsMeasuredWall: true);
        int rows = ImpactBoard.RowCount(true);
        int fonts = 0;
        float firstTop = float.NaN, firstHeight = float.NaN, firstWidth = float.NaN;

        var drift = new ResultAnchorDrift();
        for (int index = 0; index < lines.Length; index++)
        {
            ResultsBounds.TryResolve(ScreenHeight, 868f, ResultBoundSource.TitleAnchor, float.NaN, lines[index], float.NaN,
                out ResultsBand band);
            int floor = ResultsBandFit.FloorForBand(band.Height, rows, PanelDensity.Compact,
                ResultsFontGrowth.ReadabilityFloor(float.NaN), out _);
            int font = ResultsFontGrowth.FitBoardIntoStrip(Seed, floor, int.MaxValue, band.Height, BoardWidth,
                Natural(rows), out _, ceiling);
            ResultTableSize size = Natural(rows)(font);
            var strip = new BoardStrip(band.Top, band.Bottom, false, float.NaN, float.NaN, true,
                ResultFooterSource.ClientNoFooterSafeArea, ceiling);
            bool placed = ResultBoardHold.TryPlace(ScreenWidth, ScreenHeight, strip, size.Width, size.Height, 0f,
                out LayoutRect origin);
            if (index == 0)
            {
                drift.Record(889.333f, band.Height);
                firstTop = origin.Y; firstHeight = origin.Bottom - origin.Y; firstWidth = size.Width;
                fonts = font;
                check(placed && font == 7,
                    $"the first frame the waiting line is read at {waiting[0]} answers a {font}px glyph");
                continue;
            }
            check(placed && near(origin.Y, firstTop, 0.01f) && near(origin.Bottom - origin.Y, firstHeight, 0.01f)
                  && near(size.Width, firstWidth, 0.01f) && font == fonts,
                $"the waiting line moving to {waiting[index]} (band room {band.Height:0.##}px) leaves the board exactly as it was: same top, same height, same width");
            // The re-measure the page really asks for is still asked for - and it is answered with the same grid.
            bool due = drift.Due(index, 889.333f, band.Height);
            if (index == 1)
            {
                check(!due,
                    "the first moved frame is one sample, not yet a drift: the page's own animation is not spent a re-measure on");
                check(drift.Due(index + 1, 889.333f, band.Height),
                    "the second sample in a row spends one re-measure, which is the `result board re-measured` the log printed");
                drift.Spent();
                check(drift.Recoveries == 1 && !drift.Exhausted,
                    $"and it is budgeted: {drift.Recoveries} of {ResultAnchorDrift.MaximumRecoveries} re-measures spent on this page");
            }
        }

        // A page that really changed size still asks for its second look - two samples, one spend - and the budget still
        // bounds it, so the fit can never become a per-frame rebuild: 40 consecutive samples of a page that keeps moving
        // buy exactly the two re-measures `ResultAnchorDrift.MaximumRecoveries` allows.
        var budget = new ResultAnchorDrift();
        budget.Record(0, 889.333f, 316.333f);
        int granted = 0;
        for (int sample = 1; sample <= 40; sample++)
        {
            if (!budget.Due(sample, 889.333f, 316.333f + sample * 20f)) continue;
            granted++;
            budget.Spent();
        }
        check(granted == ResultAnchorDrift.MaximumRecoveries && budget.Exhausted,
            $"{granted} of 40 samples of a page that keeps moving buy a re-measure, which is the page's whole budget - a standing board can never be rebuilt every frame");
    }

    // A host page's band is bounded by the button the player has to press, and none of this reaches it.
    private static void TheHostBandKeepsTheComfortCeiling(Action<bool, string> check)
    {
        foreach (ResultFooterSource source in new[]
                 {
                     ResultFooterSource.NamedControl, ResultFooterSource.InteractiveBand,
                     ResultFooterSource.BelowActions, ResultFooterSource.ScreenSafeArea, ResultFooterSource.None
                 })
        {
            check(ResultsFontGrowth.FillCeiling(source, footIsMeasuredWall: true) == ResultsFontGrowth.StripFillCeiling
                  && ResultsFontGrowth.FillCeiling(source, footIsMeasuredWall: false) == ResultsFontGrowth.StripFillCeiling,
                $"a {source} strip keeps the historical comfort ceiling whatever its foot measured - filling it is how a control the player has to press gets covered");
        }
        // The same band height on a host page answers the size it answered before this rule existed.
        int rows = ImpactBoard.RowCount(true);
        int floor = ResultsBandFit.FloorForBand(316.333f, rows, PanelDensity.Compact,
            ResultsFontGrowth.ReadabilityFloor(float.NaN), out _);
        int hostFont = ResultsFontGrowth.FitBoardIntoStrip(Seed, floor, int.MaxValue, 316.333f, BoardWidth,
            Natural(rows), out _, ResultsFontGrowth.FillCeiling(ResultFooterSource.NamedControl, true));
        check(hostFont == 6 && near(Natural(rows)(hostFont).Height, 270f, 0.01f),
            $"a 316px host band still draws the {hostFont}px / 270px table it drew before, so the host placement does not regress");
        // A strip nobody named a ceiling for is the comfort ceiling, so every existing BoardStrip caller is unchanged.
        check(new BoardStrip(0f, 100f, true, 90f).FillCeiling == ResultsFontGrowth.StripFillCeiling,
            "a strip built without a ceiling - every caller that is not the two result-page placements - keeps the comfort ceiling");
        check(new BoardStrip(0f, 100f, true, 90f, float.NaN, true, ResultFooterSource.ClientNoFooterSafeArea, 2f)
                .FillCeiling == ResultsFontGrowth.BandFillCeiling,
            "and a ceiling is clamped to the band itself: no caller can ask for more than the whole strip");
        check(new BoardStrip(0f, 100f, true, 90f, float.NaN, true, ResultFooterSource.ClientNoFooterSafeArea,
                float.PositiveInfinity).FillCeiling == ResultsFontGrowth.StripFillCeiling,
            "a ceiling nobody could measure is not a licence either - it falls back to the comfort one");
    }

    // Which wall the band's foot belongs to decides the ceiling, and a client band that still reaches the page edge is not
    // a measured wall.
    private static void TheCeilingFollowsTheFootThePageMeasured(Action<bool, string> check)
    {
        check(ResultsFontGrowth.FootIsTheWall(1205.667f, 1227f, MessageGap),
            "the recorded band's foot is a design clearance or more above the waiting line, so that line is the wall its foot came from");
        check(!ResultsFontGrowth.FootIsTheWall(1429.333f, 1227f, MessageGap),
            "a band that still runs to the page's own bottom is not above that line at all, so its foot is no measured wall");
        check(!ResultsFontGrowth.FootIsTheWall(float.NaN, 1227f, MessageGap)
              && !ResultsFontGrowth.FootIsTheWall(1205.667f, float.NaN, MessageGap)
              && !ResultsFontGrowth.FootIsTheWall(1205.667f, 1227f, float.NaN),
            "an unread band or wall is never a wall: nothing known means nothing filled");
        check(!ResultsFontGrowth.FootIsTheWall(1430f, 1440f, MessageGap),
            "a waiting line at the very foot of the window protects nothing above it, and the band's foot is not its wall");

        float ceilingWhenUnmeasured = ResultsFontGrowth.FillCeiling(ResultFooterSource.ClientNoFooterSafeArea,
            ResultsFontGrowth.FootIsTheWall(1429.333f, 1227f, MessageGap));
        check(ceilingWhenUnmeasured == ResultsFontGrowth.StripFillCeiling,
            "so a client band whose foot is the page edge keeps the comfort ceiling: the board stops short of an edge nobody measured");
        // And the consequence, in the numbers: that band is the 540px one, and it is not filled with a 540px table.
        int rows = ImpactBoard.RowCount(true);
        int floor = ResultsBandFit.FloorForBand(540f, rows, PanelDensity.Compact,
            ResultsFontGrowth.ReadabilityFloor(float.NaN), out _);
        int font = ResultsFontGrowth.FitBoardIntoStrip(Seed, floor, int.MaxValue, 540f, BoardWidth,
            Natural(rows), out _, ceilingWhenUnmeasured);
        float height = Natural(rows)(font).Height;
        check(height <= 540f * ResultsFontGrowth.StripFillCeiling + 0.5f && height < 540f,
            $"the 540px fixed strip is answered with a {height:0}px board, not one that would reach the waiting line at 1227");
        check(near(height, 495f, 0.01f),
            $"which is the {font}px / {height:0}px table this build drew before the client band was given its own ceiling");
    }

    // A band too short for the readable glyph is answered by the band, and the new ceiling changes none of that.
    private static void AShortBandIsAnsweredTheSameWayAsBefore(Action<bool, string> check)
    {
        int rows = ImpactBoard.RowCount(true);
        int squeezedFloor = ResultsBandFit.FloorForBand(200f, rows, PanelDensity.Compact,
            ResultsFontGrowth.ReadabilityFloor(float.NaN), out bool squeezed);
        check(squeezed && squeezedFloor == 4,
            $"a 200px band over 30 rows allows a {squeezedFloor}px glyph, so the readability floor is lowered to the band's own answer");
        // The fit refuses no size the band can hold - but its floor is the *readable* one, and a 200px band cannot hold
        // even that (30 rows at 6px are 270px). Both ceilings therefore answer the same thing, which is the whole point:
        // a short band is not made worse by the new ceiling, and `FitBoard`'s own step-down (asserted in the wiring
        // section) is what draws the squeezed table this band allows.
        int withBand = ResultsFontGrowth.FitBoardIntoStrip(Seed, squeezedFloor, int.MaxValue, 200f, BoardWidth,
            Natural(rows), out _, ResultsFontGrowth.BandFillCeiling);
        int withComfort = ResultsFontGrowth.FitBoardIntoStrip(Seed, squeezedFloor, int.MaxValue, 200f, BoardWidth,
            Natural(rows), out _, ResultsFontGrowth.StripFillCeiling);
        check(withBand == withComfort,
            $"the short band is answered identically by both ceilings ({withBand}px) - neither may draw a table the band cannot hold");
        check(Natural(rows)(withBand).Height > 200f,
            $"and that answer is the seed, because the readable floor itself does not fit {200f:0}px ({Natural(rows)(withBand).Height:0}px) - which is the `squeezed` trade, not an overrun");
        // A band that can hold the readable floor is answered by that floor, again identically.
        int roomyFloor = ResultsBandFit.FloorForBand(300f, rows, PanelDensity.Compact,
            ResultsFontGrowth.ReadabilityFloor(float.NaN), out bool roomySqueezed);
        int roomyBand = ResultsFontGrowth.FitBoardIntoStrip(Seed, roomyFloor, int.MaxValue, 300f, BoardWidth,
            Natural(rows), out _, ResultsFontGrowth.BandFillCeiling);
        int roomyComfort = ResultsFontGrowth.FitBoardIntoStrip(Seed, roomyFloor, int.MaxValue, 300f, BoardWidth,
            Natural(rows), out _, ResultsFontGrowth.StripFillCeiling);
        check(!roomySqueezed && roomyBand == roomyComfort && roomyBand == ResultsFontGrowth.MinimumReadablePixels,
            $"a 300px band holds the readable floor, so both ceilings answer {roomyBand}px");
        check(Natural(rows)(roomyBand).Height <= 300f + 0.5f,
            $"and that table ({Natural(rows)(roomyBand).Height:0}px) is inside the band");
    }

    // A band shorter than the shortest grid this build can draw is refused, never filled: the vanilla page stays.
    private static void AnImpossibleBandIsRefusedRatherThanFilled(Action<bool, string> check)
    {
        int rows = ImpactBoard.RowCount(true);
        int floor = ResultsBandFit.FloorForBand(MinimumGuestStripHeight, rows, PanelDensity.Compact,
            ResultsFontGrowth.ReadabilityFloor(float.NaN), out bool squeezed);
        check(squeezed && floor == ResultsBandFit.AbsoluteMinimumPixels,
            $"the {MinimumGuestStripHeight:0}px client floor allows only the {floor}px absolute minimum glyph");
        int font = ResultsFontGrowth.FitBoardIntoStrip(Seed, floor, int.MaxValue, MinimumGuestStripHeight, BoardWidth,
            Natural(rows), out int usedFloor, ResultsFontGrowth.BandFillCeiling);
        ResultTableSize size = Natural(rows)(font);
        var strip = new BoardStrip(600f, 600f + MinimumGuestStripHeight, false, float.NaN, float.NaN, true,
            ResultFooterSource.ClientNoFooterSafeArea, ResultsFontGrowth.BandFillCeiling);
        check(usedFloor == ResultsFontGrowth.MinimumReadablePixels && size.Height > MinimumGuestStripHeight,
            $"the fit answers the shortest readable grid ({size.Height:0}px at a {usedFloor}px floor), which is already taller than the {MinimumGuestStripHeight:0}px band");
        check(!ResultBoardHold.TryPlace(ScreenWidth, ScreenHeight, strip, size.Width, size.Height, 0f, out _),
            $"so the placement refuses it and the game's own page is kept - a band this short is never filled by drawing outside it");
    }

    // The board the client band produces never reaches the waiting-status line, and a host board never reaches its button.
    private static void TheBoardNeverReachesTheWaitingLineOrTheButton(Action<bool, string> check)
    {
        float clearance = ResultsBounds.Clearance(ScreenHeight);
        int rows = ImpactBoard.RowCount(true);
        int floor = ResultsBandFit.FloorForBand(316.333f, rows, PanelDensity.Compact,
            ResultsFontGrowth.ReadabilityFloor(float.NaN), out _);
        int font = ResultsFontGrowth.FitBoardIntoStrip(Seed, floor, int.MaxValue, 316.333f, BoardWidth,
            Natural(rows), out _, ResultsFontGrowth.BandFillCeiling);
        ResultTableSize size = Natural(rows)(font);
        var client = new BoardStrip(889.333f, 1205.667f, false, float.NaN, float.NaN, true,
            ResultFooterSource.ClientNoFooterSafeArea, ResultsFontGrowth.BandFillCeiling);
        check(ResultBoardHold.TryPlace(ScreenWidth, ScreenHeight, client, size.Width, size.Height, 0f, out LayoutRect placed),
            "the client board is placed inside the band it was fitted into");
        check(placed.Bottom <= 1205.667f + 0.01f && placed.Bottom <= 1227f - clearance + 0.01f,
            $"its lower edge is inside the band's foot, which is itself the waiting line minus the clearance ({placed.Bottom:0.##} <= {1227f - clearance:0.##})");
        check(placed.Bottom < 1227f - 20f,
            $"so the waiting-status line keeps {1227f - placed.Bottom:0.#}px of clear page above it - more than the design clearance");
        check(placed.X >= 0f && placed.Right <= ScreenWidth,
            $"and the board is inside the window horizontally ({placed.X:0.#}..{placed.Right:0.#} of {ScreenWidth})");

        // The host's wall is a control, and a board that would reach it is refused rather than drawn over it.
        var host = new BoardStrip(488f, 1126f, true, 1126f, 120f, true, ResultFooterSource.NamedControl);
        check(ResultBoardHold.TryPlace(ScreenWidth, ScreenHeight, host, 519f, 585f, 0f, out LayoutRect hostPlaced)
              && hostPlaced.Bottom <= 1126f + 0.01f,
            "the host board the Test profile recorded (519x585 in a 488..1126 band) still stands above the button with the comfort ceiling");
        var tight = new BoardStrip(488f, 900f, true, 900f, 120f, true, ResultFooterSource.NamedControl);
        check(!ResultBoardHold.TryPlace(ScreenWidth, ScreenHeight, tight, 519f, 585f, 0f, out _),
            "a band that cannot hold the board is refused - the board is never drawn faint over the button instead");
    }

    // The success page is measured by its own anchors, and the same rule decides its ceiling - so a client success band
    // that ends above the waiting line is filled to its foot, and its board still stays above the line.
    private static void TheSuccessPageIsFilledByTheSameRule(Action<bool, string> check)
    {
        check(ResultsGuestPageGeometry.TryResolve(500f, ScreenWidth, ScreenHeight, MessageGap, MinimumGuestStripHeight,
                out ResultsSafeArea fixedPage),
            "the client completion section at 500 leaves the fixed page geometry usable");
        float tightened = Math.Min(fixedPage.Bottom, 1000f - MessageGap);
        var area = new ResultsSafeArea(fixedPage.Top, tightened);
        check(area.Fits && near(area.Bottom, 984f, 0.01f),
            $"the waiting line at 1000 pulls the area's foot up to {area.Bottom:0.##}");

        bool footIsWall = ResultsFontGrowth.FootIsTheWall(area.Bottom, 1000f, MessageGap);
        check(footIsWall, "and that foot is the wall the waiting line is, so this page's band is its own promise too");
        float ceiling = ResultsFontGrowth.FillCeiling(ResultFooterSource.ClientNoFooterSafeArea, footIsWall);
        check(ceiling == ResultsFontGrowth.BandFillCeiling,
            $"so the client success band is filled to its foot as well ({ceiling:0.00})");

        int rows = ImpactBoard.RowCount(true);
        int floor = ResultsBandFit.FloorForBand(area.Height, rows, PanelDensity.Compact,
            ResultsFontGrowth.ReadabilityFloor(float.NaN), out bool squeezed);
        int font = ResultsFontGrowth.FitBoardIntoStrip(Seed, floor, int.MaxValue, area.Height, BoardWidth,
            Natural(rows), out _, ceiling);
        ResultTableSize size = Natural(rows)(font);
        check(!squeezed && size.Height <= area.Height + 0.5f,
            $"the board it produces is inside the area ({size.Height:0}px <= {area.Height:0.##}px) without lowering the floor");
        var strip = new BoardStrip(area.Top, area.Bottom, false, float.NaN, float.NaN, true,
            ResultFooterSource.ClientNoFooterSafeArea, ceiling);
        check(ResultBoardHold.TryPlace(ScreenWidth, ScreenHeight, strip, size.Width, size.Height, 0f, out LayoutRect placed)
              && placed.Y >= area.Top - 0.01f && placed.Bottom <= 1000f - 0.5f,
            $"its foot stops {(1000f - placed.Bottom):0.#}px above the waiting line ({placed.Y:0.##}..{placed.Bottom:0.##})");

        check(ResultsFontGrowth.FillCeiling(ResultFooterSource.NamedControl,
                ResultsFontGrowth.FootIsTheWall(area.Bottom, 1000f, MessageGap)) == ResultsFontGrowth.StripFillCeiling,
            "and a host success page, whose area is bounded by its own measured button, still keeps the comfort ceiling");
    }

    // F8 on a result page: the press is answered from what the page is showing, so a second press hides what the first
    // one drew instead of drawing the same board again. What is asserted here is the policy (`ResultTogglePolicy`) and the
    // wiring in `ResultsDisplay`'s own source; no repaint is issued by this file, so nothing below claims a measured draw
    // count on a live page - `HudTable.Draw` needs Unity and is out of this project's reach.
    private static void F8AnswersFromWhatThePageShows(Action<bool, string> check)
    {
        check(ResultTogglePolicy.BoardUp(100, 100) && ResultTogglePolicy.BoardUp(100, 101),
            "a board drawn on this repaint (or the one before it) is a board the page is showing");
        check(!ResultTogglePolicy.BoardUp(100, 102) && !ResultTogglePolicy.BoardUp(-1, 100),
            "and a repaint that painted nothing - every refused, hidden or held-back frame writes -1 - is not");
        check(ResultTogglePolicy.Next(true) == ResultBoardToggle.Hide && ResultTogglePolicy.Next(false) == ResultBoardToggle.Show,
            "the press hides a board that is up and gives one to a page that is showing none");
        bool showing = true;
        for (int press = 0; press < 4; press++)
        {
            ResultBoardToggle answer = ResultTogglePolicy.Next(showing);
            check(answer == (showing ? ResultBoardToggle.Hide : ResultBoardToggle.Show),
                $"press {press + 1} answers the state the page is in, so two presses always end where they started");
            showing = answer == ResultBoardToggle.Show;
        }

        string display = Code(Source("ResultsDisplay.cs"));
        check(display.Contains("internal static bool BoardOnScreen => ResultTogglePolicy.BoardUp(_boardDrawnFrame, Time.frameCount);"),
            "the display's own answer to \"is the board on the page\" is the last repaint's drawn frame");
        check(display.Contains("ResultBoardToggle toggle = ResultTogglePolicy.Next(BoardOnScreen);"),
            "and F8 asks that answer - not the `Forced` override, which is false for a board the fail-open path put up itself");
        check(display.Contains("_boardDrawnFrame = -1;") && display.Contains("_boardDrawnFrame = Time.frameCount;"),
            "the drawn frame is written before every early return of a repaint and set again only by the draw at its end");
        // 1.19.45: two draw paths record a drawn board now - the adaptive one at the end of `Draw`, and the fixed-mode one
        // in `FixedLayoutDraw` (which returns before the adaptive tail). Both write the same fact, so F8's answer stays
        // one fact; the count is pinned so a third, accidental writer still fails here.
        check(CountOf(display, "_boardDrawnFrame = Time.frameCount;") == 2,
            "exactly two places record a drawn board, each right after its own table draw call: the adaptive repaint and the fixed-mode one");
    }

    // The fit spends the ceiling of the strip it was given, and the failure/success placements build that strip from the
    // wall they measured. Asserted against the sources of the files that cannot be compiled without GTFO's assemblies.
    private static void ABoardIsFittedIntoTheStripItWasMeasuredAgainst(Action<bool, string> check)
    {
        string display = Code(Source("ResultsDisplay.cs"));
        check(display.Contains("PanelGrid grid = FitBoard(content, room, boardWidth, succeeded, strip.ButtonHeight, strip.FillCeiling);"),
            "the strict fit spends the strip's own ceiling instead of a constant");
        check(display.Contains("PanelGrid resized = FitBoard(content, room, boardWidth, succeeded, strip.ButtonHeight, strip.FillCeiling);"),
            "and so does the re-fit a refreshed snapshot runs, so a refresh cannot ask for a different size than the placement did");
        check(display.Contains("size => NaturalSize(content, size, bandFloor), out int floor, fillCeiling);"),
            "the ceiling travels into `ResultsFontGrowth.FitBoardIntoStrip` as its own argument - the call the interrupted write left unwired");
        check(display.Contains("float fillCeiling = ResultsFontGrowth.StripFillCeiling)"),
            "and a caller that names no ceiling keeps the historical one");

        check(display.Contains("footIsWall = pageBand.BottomSource != ResultBoundSource.ScreenFoot;"),
            "the failure strip records whether the foot `ResultsBounds` gave it came from a measured wall or from the page's own edge");
        check(display.Contains("ResultsFontGrowth.FillCeiling(failureFooterSource, failureFootIsWall)"),
            "and the failure placement builds its strip from that fact and from its source");
        check(display.Contains("bool successFootIsWall = ResultsFontGrowth.FootIsTheWall(area.Bottom, successControls.FooterTop, MessageGap);"),
            "the success placement asks the same question about its own area and its own measured footer");
        check(display.Contains("ResultsFontGrowth.FillCeiling(footerSource, successFootIsWall)"),
            "and builds its strip from the answer");

        string visibility = Code(Source("ResultVisibility.cs"));
        check(visibility.Contains("public readonly float FillCeiling;")
              && visibility.Contains("float fillCeiling = ResultsFontGrowth.StripFillCeiling)"),
            "a strip carries its ceiling, and a strip nobody said anything about carries the comfort one");
        string policy = Code(Source("LayoutPolicy.cs"));
        check(policy.Contains("public const float BandFillCeiling = 1f;")
              && policy.Contains("public static float FillCeiling(ResultFooterSource source, bool footIsMeasuredWall)")
              && policy.Contains("public static bool FootIsTheWall(float bandBottom, float wallTop, float gap)"),
            "the policy layer owns the ceiling and the wall question, so both are asserted above rather than copied into the renderer");
        check(display.Contains("if (!_loggedDraw)") && display.Contains("_loggedDraw = false;"),
            "the board's geometry line is printed once per page (the latch is dropped only when the page is), which is why the recorded page logged one board and one re-measure");
    }

    // The design figures this file drives are the ones the two placements use: a test that invented them would keep
    // passing for a build that changed them.
    private static void TheDesignNumbersAreTheOnesDriven(Action<bool, string> check)
    {
        string display = Code(Source("ResultsDisplay.cs"));
        check(display.Contains("private const float MessageGap = 16f;"),
            "the design gap the band keeps above a measured wall is still 16px at 1080p");
        check(display.Contains("private const float MinimumGuestStripHeight = 96f;"),
            "and the client page's minimum strip is still 96px");
        string bounds = Code(Source("ResultsBounds.cs"));
        check(bounds.Contains("public const float ClearanceDesign = 16f;")
              && bounds.Contains("public const float ReferenceHeight = 1080f;"),
            "so the clearance this file scales to 1440 is the one `ResultsBounds` applies");
        check(ImpactBoard.RowCount(true) == 30 && ImpactBoard.RowCount(false) == 28,
            "and the recorded 30-row board is still the split board's row count, not a number this file chose");
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    // `ResultsDisplay.NaturalSize`, headless: `HudTable.BuildGrid`'s settlement rule measured against no box at all, so
    // the extents are the table's own. The row height is `PanelGeometry.Build`'s (the face's 1.5em floor at the compact
    // density), which is the row the renderer builds.
    private static Func<int, ResultTableSize> Natural(int rows)
        => size =>
        {
            var columns = new[] { PanelColumn.ImpactLabel, PanelColumn.ImpactValue };
            var measured = new[]
            {
                PanelGeometry.EstimateTextWidth("TOTAL", size),
                PanelGeometry.EstimateTextWidth("#1 1,234.5 42.1%", size)
            };
            float[] widths = PanelGeometry.SettlementColumnWidths(columns, measured, size);
            var metrics = new[]
            {
                new PanelColumnMetrics(new[] { widths[0] }),
                new PanelColumnMetrics(new[] { widths[1] })
            };
            PanelGrid grid = PanelGeometry.Build(metrics, PanelGeometry.FieldGap(size, PanelDensity.Compact), size, rows,
                PanelDensity.Compact, 1f, new[] { false, true });
            return grid == null ? ResultTableSize.None : new ResultTableSize(grid.Width, grid.Height);
        };

    private static bool near(float a, float b) => near(a, b, 0.001f);
    private static bool near(float a, float b, float tolerance) => Math.Abs(a - b) <= tolerance;

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

    // A file's source with every comment removed, so a wiring assertion cannot be satisfied by the prose around the code.
    private static string Code(string source)
    {
        var text = new System.Text.StringBuilder(source.Length);
        bool line = false, block = false;
        for (int index = 0; index < source.Length; index++)
        {
            char current = source[index];
            char next = index + 1 < source.Length ? source[index + 1] : '\0';
            if (line)
            {
                if (current == '\n') { line = false; text.Append(current); }
                continue;
            }
            if (block)
            {
                if (current == '*' && next == '/') { block = false; index++; }
                continue;
            }
            if (current == '/' && next == '/') { line = true; index++; continue; }
            if (current == '/' && next == '*') { block = true; index++; continue; }
            text.Append(current);
        }
        return text.ToString();
    }

    private static int CountOf(string source, string needle)
    {
        int count = 0, at = -1;
        while ((at = source.IndexOf(needle, at + 1, StringComparison.Ordinal)) >= 0) count++;
        return count;
    }
}
