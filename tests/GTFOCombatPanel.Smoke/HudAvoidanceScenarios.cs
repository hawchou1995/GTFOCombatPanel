using System;
using System.Collections.Generic;
using HawChow.GTFOCombatPanel;

// The in-level table's place beside the native equipment column (1.19.41).
//
// What this replay is made of. The recorded expedition's own numbers are the fixture: a 2560x1440 window, an anchor row
// (the icon row's `PUI_InventoryIconDisplay` layout rectangle) of (2336,294) size 211x110, and a 397x60 table drawn at
// (2150,412)-(2547,472). The screenshot that goes with it shows the table lying across the vanilla rows - the weapon and
// its ammo figures, the tool, the resource pack - which is the *relative* shape this replay encodes: the column
// continues below the anchor row, row after row, and the pre-1.19.41 rectangle sits inside that run. The row rectangles
// below the anchor row are the screenshot's shape (rows stacked under the anchor row, each about half its height) laid
// out on the log's own scale; nothing here invents a screen fraction or a per-row constant, and every number the
// production code answers with is compared against the four rectangles it was handed.
//
// The placement itself is *not* re-implemented here: `EquipmentBand.Read` and `HudPlacement.TryResolve` are the
// production decisions, and `EquipmentBandWatch` is the production hysteresis. What a headless test cannot do is run
// Unity, so the row reader in `PanelBehaviour` (which objects it asks, which camera it projects them with, which gate it
// passes them through) is asserted against that file's own source text - the same split the anchor suites use.
internal static class HudAvoidanceScenarios
{
    // The recorded window.
    private const float ScreenWidth = 2560f;
    private const float ScreenHeight = 1440f;
    // The recorded anchor row: the icon row's own layout rectangle, projected to window pixels.
    private const float AnchorX = 2336.08f;
    private const float AnchorY = 294.33f;
    private const float AnchorWidth = 210.59f;
    private const float AnchorHeight = 110.16f;
    // The recorded table, and the gap this mod has always used.
    private const float TableWidth = 397f;
    private const float TableHeight = 60f;

    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        TheRecordedPlacementIsTheRecordedOverlap(check);
        TheSecondRecordedRoundIsTheSameOverlap(check);
        TheConfiguredFallbackIsNeverTheKnownOverlap(check);
        TheMeasuredColumnPlacesTheTableUnderItsLastRow(check);
        TheBandIsOnlyWhatTheHudReallyDraws(check);
        APartialColumnNeverPlacesBelowItself(check);
        ATightWindowFindsAFreePlaceInsteadOfOverlapping(check);
        AnImpossibleWindowKeepsTheConfiguredPosition(check);
        MorePlayersMakeTheTableTallerAndItStillClearsTheColumn(check);
        ALateBottomEdgeIsHonouredAndACollapseDoesNotJitter(check);
        // 1.19.45: the two confirmed defects of this round - an unreadable row stops proving the bottom edge on the very
        // reading that carries it, and a level change drops the whole column round (camera included).
        ACompleteBandLosesItsProofTheMomentARowCannotBeRead(check);
        ALevelChangeDropsTheColumnRound(check);
        TheMeasurementChainIsTheHudsOwnInventory(check);
        TheBoundedReadNeverScansTheSceneOrGrowsWithTheColumn(check);
        TheConfiguredPositionIsUntouched(check);
        // 1.19.47: the recorded retest - a half-read column issued an `above-band` proof and drew the table on the native
        // equipment column, the configured fallback is not a way round that, the drawn rectangle is re-avoided after the
        // offset and the clamp, a row is measured through its own native name, and a compositor whose texture is not the
        // window keeps its safe refusal instead of being scaled to fit.
        APartialReadingIssuesNoRungAtAll(check);
        TheDrawnRectangleIsReAvoidedAfterTheOffset(check);
        TheRowIsMeasuredThroughItsOwnNativeName(check);
        AnUnscaledCompositorKeepsItsSafeRefusal(check);
        ARefusedRoundClearsTheDrawnOrigin(check);
        // 1.19.48: the refusals of the 2026-11-23 retest - a column this build could only read one row of, and a HUD that
        // was never measured at all after the resolution change - are answered with a conservative place instead of with a
        // blank table, while the completeness rule and the ladder keep their own meaning.
        AConservativeSpotRestoresTheTable(check);
    }

    // ---------- the recorded frame, replayed ----------

    private static LayoutRect Anchor() => new LayoutRect(AnchorX, AnchorY, AnchorWidth, AnchorHeight);

    // The equipment column the screenshot shows: the anchor row, then the rows stacked under it. Heights are the
    // screenshot's relative shape - each row a little shorter than the one above it, the whole run about 2.6 times the
    // anchor row's own height - laid out on the log's pixel scale.
    private static HudObstacle[] Column()
    {
        float top = AnchorY + AnchorHeight;                 // 404.49, the anchor row's own bottom edge
        return new[]
        {
            new HudObstacle("iconRow", Anchor(), drawn: true),
            new HudObstacle("slot:GearStandard", new LayoutRect(AnchorX, top, AnchorWidth, 52f), drawn: true),
            new HudObstacle("slot:GearSpecial", new LayoutRect(AnchorX, top + 52f, AnchorWidth, 48f), drawn: true),
            new HudObstacle("slot:GearClass", new LayoutRect(AnchorX, top + 100f, AnchorWidth, 44f), drawn: true),
            new HudObstacle("slot:ResourcePack", new LayoutRect(AnchorX, top + 144f, AnchorWidth, 36f), drawn: true)
        };
    }

    private static LayoutRect PanelsOldRect()
        => new LayoutRect(Anchor().Right - TableWidth, Anchor().Bottom + 8f, TableWidth, TableHeight);

    // The defect, as the numbers the recorded log printed: the rectangle 1.19.40 drew sat *inside* the equipment column.
    // Its own anchor row was measured correctly - the table cleared that row by the gap - and the overlap is with the
    // rows below it, which is what "the anchor row is not the column's bottom edge" means.
    private static void TheRecordedPlacementIsTheRecordedOverlap(Action<bool, string> check)
    {
        HudObstacle[] column = Column();
        EquipmentBandReading band = EquipmentBand.Read(column, ScreenWidth, ScreenHeight);
        check(band.Measured && band.Complete, "the recorded column reads as a complete band: " + band.Describe());
        check(Near(band.Rect.Y, AnchorY) && Near(band.Rect.Bottom, 584.49f),
            "the band runs from the anchor row's top to the last row's bottom (" + band.Rect.Y.ToString("0.##") + ".." + band.Rect.Bottom.ToString("0.##") + ")");
        check(Near(band.Rect.X, AnchorX) && Near(band.Rect.Right, AnchorX + AnchorWidth),
            "and keeps the column's own horizontal extent");

        LayoutRect old = PanelsOldRect();
        check(Near(old.Y, Anchor().Bottom + 8f),
            "1.19.40's rectangle was the anchor row's bottom edge plus the gap: y=" + old.Y.ToString("0.##"));
        check(Near(HudPlacement.Area(old, Anchor()), 0f),
            "it did clear the anchor row itself by the gap, which is why the old placement looked measured");
        float overlap = HudPlacement.Area(old, band.Rect);
        check(overlap > 12000f,
            "and it lay on the column below that row: " + overlap.ToString("0.##") + " px² of the table on the vanilla rows");
        int covered = 0;
        foreach (HudObstacle row in column)
        {
            if (row.Name == "iconRow") continue;
            if (HudPlacement.Area(old, row.Rect) > 0f) covered++;
        }
        check(covered >= 2,
            "the old rectangle really lay on " + covered + " of the rows the column stacks under the anchor row");
    }

    private static void TheMeasuredColumnPlacesTheTableUnderItsLastRow(Action<bool, string> check)
    {
        HudObstacle[] column = Column();
        EquipmentBandReading band = EquipmentBand.Read(column, ScreenWidth, ScreenHeight);
        PanelPlacement placement;
        bool resolved = HudPlacement.TryResolve(band.Rect, band.Complete, TableWidth, TableHeight, Anchor().Right,
            HudPlacement.Gap, ScreenWidth, ScreenHeight, out placement);
        check(resolved, "the measured column answers a placement");
        check(placement.Kind == PanelPlacementKind.BelowBand,
            "and it is the rung the table is meant to take: " + PanelPlacement.Name(placement.Kind));
        check(Near(HudPlacement.Gap, 8f), "the gap is the one this mod has always used (8 px), not a new constant");
        check(Near(placement.Rect.Y, band.Rect.Bottom + 8f),
            "the table's top edge is one gap below the column's bottom edge: " + placement.Rect.Y.ToString("0.##"));
        check(Near(placement.Overlap, 0f), "and the placement covers none of the column");
        check(HudPlacement.OnWindow(placement.Rect, ScreenWidth, ScreenHeight), "it is on the window");
        check(Near(placement.Rect.Right, Anchor().Right), "it is still right-aligned with the anchor row's right edge");
        foreach (HudObstacle row in column)
            check(HudPlacement.Area(placement.Rect, row.Rect) == 0f, "no part of the table lies on " + row.Name);
        // The movement itself, in the recorded frame: 412 -> 592, i.e. the row that was never measured.
        check(placement.Rect.Y > PanelsOldRect().Y + 150f,
            "the table moved down past the rows it used to cover (" + PanelsOldRect().Y.ToString("0.##") + " -> "
                + placement.Rect.Y.ToString("0.##") + ")");
    }

    // ---------- what the band is, and what it is not ----------

    private static void TheBandIsOnlyWhatTheHudReallyDraws(Action<bool, string> check)
    {
        // A hidden row is not on the screen, so it cannot be drawn over and must not push the table down.
        var hidden = new[]
        {
            new HudObstacle("iconRow", new LayoutRect(2336f, 294f, 210f, 110f), drawn: true),
            new HudObstacle("slot:Consumable", new LayoutRect(2336f, 404f, 210f, 300f), drawn: false)
        };
        EquipmentBandReading hiddenBand = EquipmentBand.Read(hidden, ScreenWidth, ScreenHeight);
        check(hiddenBand.Measured && Near(hiddenBand.Rect.Bottom, 404f),
            "a row the HUD reports as not visible does not extend the band (" + hiddenBand.Rect.Bottom.ToString("0.##") + ")");
        check(hiddenBand.Complete, "and it does not make the reading incomplete either - a hidden row is an answer");

        // A row that exists but is not a rectangle is not a row at zero. The band is still measured (the rows that did
        // answer), and it is reported as *incomplete*.
        var unread = new[]
        {
            new HudObstacle("slot:GearStandard", new LayoutRect(2336f, 294f, 210f, 110f), drawn: true),
            HudObstacle.Unread("slot:ResourcePack", drawn: true)
        };
        EquipmentBandReading unreadBand = EquipmentBand.Read(unread, ScreenWidth, ScreenHeight);
        check(unreadBand.Measured && !unreadBand.Complete && unreadBand.Skipped == 1,
            "a drawn row nobody could read leaves the band measured but incomplete: " + unreadBand.Describe());

        // Not a number, one pixel tall, entirely off the window: none of them is a rectangle the table can be drawn over, so
        // not one of them may extend the band. The first is a row that could not be read and leaves the reading incomplete;
        // the other two are rectangles that are simply not places anything can be drawn on.
        var invalid = new[]
        {
            new HudObstacle("nan", new LayoutRect(float.NaN, 100f, 10f, 10f), drawn: true),
            new HudObstacle("real", new LayoutRect(2336f, 294f, 210f, 110f), drawn: true)
        };
        EquipmentBandReading invalidBand = EquipmentBand.Read(invalid, ScreenWidth, ScreenHeight);
        check(invalidBand.Measured && Near(invalidBand.Rect.Bottom, 404f) && !invalidBand.Complete,
            "a row whose rectangle is not a number is an unread row, not a row at zero: " + invalidBand.Describe());
        var unusable = new[]
        {
            new HudObstacle("flat", new LayoutRect(2336f, 500f, 210f, 1f), drawn: true),
            new HudObstacle("offscreen", new LayoutRect(2336f, 1500f, 210f, 60f), drawn: true),
            new HudObstacle("real", new LayoutRect(2336f, 294f, 210f, 110f), drawn: true)
        };
        EquipmentBandReading unusableBand = EquipmentBand.Read(unusable, ScreenWidth, ScreenHeight);
        check(unusableBand.Measured && Near(unusableBand.Rect.Bottom, 404f) && unusableBand.Complete,
            "a pixel-tall row and one past the window's edge are rectangles nothing can be drawn on, not unread rows: "
                + unusableBand.Describe());

        // A row half off the window contributes only its on-screen part: the table is clamped into the window, so nothing
        // past the edge can be covered.
        var straddling = new[]
        {
            new HudObstacle("row", new LayoutRect(2400f, 300f, 400f, 100f), drawn: true)
        };
        EquipmentBandReading clamped = EquipmentBand.Read(straddling, ScreenWidth, ScreenHeight);
        check(clamped.Measured && Near(clamped.Rect.Right, ScreenWidth),
            "a row that runs past the right edge is clipped to the window (" + clamped.Rect.Right.ToString("0.##") + ")");

        // Nothing at all: no band, and no claim about one.
        check(!EquipmentBand.Read(null, ScreenWidth, ScreenHeight).Measured, "no rows is no band");
        check(!EquipmentBand.Read(new HudObstacle[0], ScreenWidth, ScreenHeight).Measured, "an empty list is no band");
        check(!EquipmentBand.Read(Column(), float.NaN, ScreenHeight).Measured, "an unreadable window is no band");
        var allHidden = new[] { new HudObstacle("row", new LayoutRect(2336f, 294f, 210f, 110f), drawn: false) };
        check(!EquipmentBand.Read(allHidden, ScreenWidth, ScreenHeight).Measured, "a column with nothing drawn on it is no band");
    }

    private static void APartialColumnNeverPlacesBelowItself(Action<bool, string> check)
    {
        string panel = Source("PanelBehaviour.cs");
        // One row of the column could not be projected, so no edge of the column is an edge this reading proved: not its
        // bottom (`below-band`), not its top (`above-band`) and not its left (`beside-band`).
        // The trap this round has to avoid: a band that is *only* the anchor row answers 1.19.40's rectangle exactly, and
        // - as the recorded expedition shows - the rung *above* that band answers another rectangle on the native column.
        // That is why the panel states the inventory's unnamed rows as unread instead of dropping them (asserted below),
        // and why 1.19.47 makes every rung a proof about a *complete* reading (`HudPlacement.Proven`).
        EquipmentBandReading anchorOnly = EquipmentBand.Read(new[] { new HudObstacle("iconRow", Anchor(), drawn: true) },
            ScreenWidth, ScreenHeight);
        PanelPlacement wouldBeOld;
        check(HudPlacement.TryResolve(anchorOnly.Rect, anchorOnly.Complete, TableWidth, TableHeight, Anchor().Right,
                HudPlacement.Gap, ScreenWidth, ScreenHeight, out wouldBeOld),
            "a band of only the anchor row answers a placement");
        check(Near(wouldBeOld.Rect.Y, PanelsOldRect().Y) && Near(wouldBeOld.Rect.X, PanelsOldRect().X),
            "and that placement is exactly the pre-1.19.41 rectangle - which is why a column this build could not name a"
                + " row of is reported as partial, never as one row deep");
        check(HudPlacement.Proven(wouldBeOld, anchorOnly.Complete),
            "which is a proof here only because that page really has one row: the anchor row is the whole column");
        check(panel.Contains("int known = 0, named = 0;") && panel.Contains("for (int index = named; index < known; index++)")
            && panel.Contains("_obstacles.Add(HudObstacle.Unread(\"slot:unnamed\""),
            "the reader states the inventory's unnamed rows as unread, so the band can never silently shrink to the anchor row");
        HudObstacle[] column =
        {
            new HudObstacle("iconRow", Anchor(), drawn: true),
            HudObstacle.Unread("slot:ResourcePack", drawn: true)
        };
        EquipmentBandReading band = EquipmentBand.Read(column, ScreenWidth, ScreenHeight);
        check(band.Measured && !band.Complete, "a column with an unreadable drawn row is a partial reading: " + band.Describe());
        PanelPlacement placement;
        check(HudPlacement.TryResolve(band.Rect, band.Complete, TableWidth, TableHeight, Anchor().Right, HudPlacement.Gap,
                ScreenWidth, ScreenHeight, out placement),
            "the ladder still answers a rung for it - the ladder is arithmetic, and this is the rung it would take");
        check(placement.Kind != PanelPlacementKind.BelowBand,
            "never the one that needs the column's bottom edge: " + PanelPlacement.Name(placement.Kind));
        check(Near(placement.Overlap, 0f), "and it clears everything that *was* measured");
        check(!HudPlacement.Proven(placement, band.Complete),
            "but it is not a proof on a partial reading: " + PanelPlacement.Name(placement.Kind)
                + " is measured from an edge this reading did not prove");
        check(HudPlacement.Proven(placement, true),
            "while the same rung is a proof once the whole column is measured - the rule is the completeness, not the rung");
        check(panel.Contains("if (!resolved || !HudPlacement.Proven(placement, _bandWatch.Complete))"),
            "and the panel asks that rule before it draws anything at all");
    }

    // ---------- screens with no room ----------

    private static void ATightWindowFindsAFreePlaceInsteadOfOverlapping(Action<bool, string> check)
    {
        // A column that runs from near the top edge to the bottom of the window: nothing below it, no room above it.
        var column = new[] { new HudObstacle("column", new LayoutRect(2336f, 40f, 210f, 1360f), drawn: true) };
        EquipmentBandReading band = EquipmentBand.Read(column, ScreenWidth, ScreenHeight);
        PanelPlacement placement;
        check(HudPlacement.TryResolve(band.Rect, band.Complete, TableWidth, TableHeight, Anchor().Right, HudPlacement.Gap,
            ScreenWidth, ScreenHeight, out placement), "a tight window still answers a placement");
        check(placement.Kind == PanelPlacementKind.BesideBand,
            "it is the rung beside the column, which is a real separation in x: " + PanelPlacement.Name(placement.Kind));
        check(Near(placement.Overlap, 0f), "and it covers nothing");
        check(Near(placement.Rect.Right, band.Rect.X - HudPlacement.Gap),
            "the table's right edge is one gap left of the column's left edge");
        check(HudPlacement.OnWindow(placement.Rect, ScreenWidth, ScreenHeight), "it is on the window");

        // The same column, but the window is short enough that even the beside rung has to be clamped: the answer is
        // still on the window and still the least-covering rung, reported as itself.
        var shortWindow = new[] { new HudObstacle("column", new LayoutRect(2300f, 0f, 260f, 700f), drawn: true) };
        EquipmentBandReading shortBand = EquipmentBand.Read(shortWindow, 1280f, 720f);
        PanelPlacement tight;
        check(HudPlacement.TryResolve(shortBand.Rect, shortBand.Complete, 397f, 60f, 2560f, HudPlacement.Gap, 1280f, 720f, out tight),
            "a 1280x720 window answers a placement too");
        check(HudPlacement.OnWindow(tight.Rect, 1280f, 720f), "which is clamped inside it");
        check(tight.Overlap == 0f, "and does not touch the column: " + tight.Describe());
    }

    private static void AnImpossibleWindowKeepsTheConfiguredPosition(Action<bool, string> check)
    {
        // A column that covers the whole window: every rung of the ladder covers the whole table, so nothing here can
        // prove a place. The answer is "no placement" - the panel then keeps the player's own PositionX/PositionY, which
        // is an answer a player gave, rather than the anchor row's bottom edge, which is the known overlap.
        var whole = new[] { new HudObstacle("column", new LayoutRect(0f, 0f, ScreenWidth, ScreenHeight), drawn: true) };
        EquipmentBandReading band = EquipmentBand.Read(whole, ScreenWidth, ScreenHeight);
        PanelPlacement placement;
        check(!HudPlacement.TryResolve(band.Rect, band.Complete, TableWidth, TableHeight, Anchor().Right, HudPlacement.Gap,
                ScreenWidth, ScreenHeight, out placement),
            "a column that covers the window answers no placement at all");
        check(placement.Kind == PanelPlacementKind.None, "and names none");
        // A table larger than the window is the same answer: it cannot be on the window anywhere.
        check(!HudPlacement.TryResolve(band.Rect, true, ScreenWidth + 10f, TableHeight, Anchor().Right, HudPlacement.Gap,
            ScreenWidth, ScreenHeight, out placement), "a table wider than the window is not placed either");
        check(!HudPlacement.TryResolve(band.Rect, true, float.NaN, TableHeight, Anchor().Right, HudPlacement.Gap,
            ScreenWidth, ScreenHeight, out placement), "and neither is a table with no size");
    }

    private static void MorePlayersMakeTheTableTallerAndItStillClearsTheColumn(Action<bool, string> check)
    {
        // The real table grid, at the recorded glyph size and density: one player is two rows (header and figure), four
        // players are five. The placement is the same decision for both, so the table's own height is the only thing that
        // changes - which is exactly what a screen-fraction placement could not follow.
        PanelGrid solo = PanelGeometry.Build(new[] { 380f }, 20f, 2, PanelDensity.Compact);
        PanelGrid squad = PanelGeometry.Build(new[] { 380f }, 20f, 5, PanelDensity.Compact);
        check(Near(solo.Height, 60f), "the recorded one-player table is 60 px tall (" + solo.Height.ToString("0.##") + ")");
        check(squad.Height > solo.Height, "and four players make it taller (" + squad.Height.ToString("0.##") + ")");

        HudObstacle[] column = Column();
        EquipmentBandReading band = EquipmentBand.Read(column, ScreenWidth, ScreenHeight);
        PanelPlacement small, large;
        check(HudPlacement.TryResolve(band.Rect, band.Complete, solo.Width, solo.Height, Anchor().Right, HudPlacement.Gap,
            ScreenWidth, ScreenHeight, out small), "one player is placed");
        check(HudPlacement.TryResolve(band.Rect, band.Complete, squad.Width, squad.Height, Anchor().Right, HudPlacement.Gap,
            ScreenWidth, ScreenHeight, out large), "four players are placed");
        check(small.Kind == PanelPlacementKind.BelowBand && large.Kind == PanelPlacementKind.BelowBand,
            "both below the column");
        check(Near(small.Rect.Y, large.Rect.Y),
            "both start one gap under the same measured bottom edge, whatever their height (" + small.Rect.Y.ToString("0.##")
                + " vs " + large.Rect.Y.ToString("0.##") + ")");
        check(Near(large.Overlap, 0f), "and the taller one still covers none of the column");
    }

    // ---------- a bottom edge that arrives late ----------

    private static void ALateBottomEdgeIsHonouredAndACollapseDoesNotJitter(Action<bool, string> check)
    {
        var watch = new EquipmentBandWatch();
        check(!watch.Has, "a fresh watch has no band");
        LayoutRect first = new LayoutRect(AnchorX, AnchorY, AnchorWidth, AnchorHeight);
        // A reading that could not be measured is never a verdict about the column.
        check(!watch.Observe(EquipmentBandReading.None), "an unmeasured reading changes nothing");
        check(!watch.Has, "and does not create a band");
        check(watch.Observe(new EquipmentBandReading(true, true, first, 1, 0)), "the first measured reading is committed");
        check(watch.Has && Near(watch.Committed.Bottom, Anchor().Bottom), "at the column's own bottom edge");

        // The HUD grows a row (a weapon selected, a pack picked up): the new bottom edge is honoured on the reading that
        // reports it, because a table further down cannot overlap anything.
        LayoutRect grown = new LayoutRect(AnchorX, AnchorY, AnchorWidth, AnchorHeight + 181.5f);
        check(watch.Observe(new EquipmentBandReading(true, true, grown, 5, 0)), "a grown column is committed at once");
        check(Near(watch.Committed.Bottom, grown.Bottom), "and the watch now reports its real bottom edge");

        // The row collapses. One reading is not enough: that is the flicker this exists to stop.
        check(!watch.Observe(new EquipmentBandReading(true, true, first, 1, 0)), "one small reading moves nothing");
        check(Near(watch.Committed.Bottom, grown.Bottom), "the table stays where it was while the row could come back");
        // It comes back: the grow is committed again and the collapse is forgotten, so an oscillating row can never drag
        // the table up into the overlap it just left.
        check(watch.Observe(new EquipmentBandReading(true, true, grown, 5, 0)) == false,
            "the same grown band is not a change");
        check(!watch.Observe(new EquipmentBandReading(true, true, first, 1, 0)), "and the collapse still needs agreement");
        check(Near(watch.Committed.Bottom, grown.Bottom), "the table is still below the grown column");
        // Twice in a row is a collapse: the column really is that small now, so the table may come back.
        check(watch.Observe(new EquipmentBandReading(true, true, first, 1, 0)), "a second agreeing reading commits the collapse");
        check(Near(watch.Committed.Bottom, first.Bottom), "and the table may move back up to it");

        // A reading within the hysteresis is not a change at all; the anchor's own settle window uses the same 2 px.
        check(!watch.Observe(new EquipmentBandReading(true, true, new LayoutRect(AnchorX, AnchorY, AnchorWidth, AnchorHeight + 1f), 1, 0)),
            "a one-pixel difference is not a move");
        check(Near(EquipmentBandWatch.Hysteresis, 2f), "the tolerance is the anchor's own two pixels");
        check(EquipmentBandWatch.ShrinkSamples == 2, "and a collapse needs two agreeing readings");

        // Restart forgets the band, so a new level or a resolution change does not inherit it.
        watch.Restart();
        check(!watch.Has && Near(watch.Committed.Width, 0f), "a restart forgets the band");

        // The whole sequence, on the production placement: the same 397x60 table, moved once when the row arrived.
        var replay = new EquipmentBandWatch();
        replay.Observe(EquipmentBand.Read(new[]
        {
            new HudObstacle("iconRow", Anchor(), drawn: true)
        }, ScreenWidth, ScreenHeight));
        PanelPlacement before, after;
        HudPlacement.TryResolve(replay.Committed, replay.Complete, TableWidth, TableHeight, Anchor().Right, HudPlacement.Gap,
            ScreenWidth, ScreenHeight, out before);
        replay.Observe(EquipmentBand.Read(Column(), ScreenWidth, ScreenHeight));
        HudPlacement.TryResolve(replay.Committed, replay.Complete, TableWidth, TableHeight, Anchor().Right, HudPlacement.Gap,
            ScreenWidth, ScreenHeight, out after);
        check(Near(before.Rect.Y, Anchor().Bottom + 8f), "before the rows arrived the table stood under the anchor row");
        check(Near(after.Rect.Y, 592.49f), "and the arriving rows moved it to the column's new bottom edge: "
            + after.Rect.Y.ToString("0.##"));
        check(HudPlacement.Area(after.Rect, replay.Committed) == 0f, "where it covers nothing");
    }

    // ---------- 1.19.45: the completeness is not part of the hysteresis ----------

    private static void ACompleteBandLosesItsProofTheMomentARowCannotBeRead(Action<bool, string> check)
    {
        EquipmentBandReading complete = EquipmentBand.Read(Column(), ScreenWidth, ScreenHeight);
        var watch = new EquipmentBandWatch();
        check(watch.Observe(complete) && watch.Complete, "the recorded column is committed as a complete band: " + complete.Describe());
        PanelPlacement below;
        check(HudPlacement.TryResolve(watch.Committed, watch.Complete, TableWidth, TableHeight, Anchor().Right,
                HudPlacement.Gap, ScreenWidth, ScreenHeight, out below) && below.Kind == PanelPlacementKind.BelowBand,
            "and a complete band answers the rung that hangs the table under its bottom edge: " + below.Describe());

        // The confirmed defect: one *drawn* row this build cannot project. Its rectangle is nowhere, so the union does not
        // move - and the branch that answered "the same band" used to leave `Complete` exactly as it was.
        var rows = new List<HudObstacle>(Column());
        rows.Add(HudObstacle.Unread("slot:Tool", drawn: true));
        EquipmentBandReading partial = EquipmentBand.Read(rows.ToArray(), ScreenWidth, ScreenHeight);
        check(partial.Measured && !partial.Complete && Near(partial.Rect.Bottom, complete.Rect.Bottom),
            "the unreadable row leaves the measured rectangle exactly where it was: " + partial.Describe());
        check(!watch.Observe(partial), "so the watch answers 'no move', because the geometry is not what changed");
        check(!watch.Complete,
            "but the band stops proving its bottom edge on that very reading: bottom="
                + watch.Committed.Bottom.ToString("0.##"));
        PanelPlacement after;
        check(HudPlacement.TryResolve(watch.Committed, watch.Complete, TableWidth, TableHeight, Anchor().Right,
                HudPlacement.Gap, ScreenWidth, ScreenHeight, out after) && after.Kind != PanelPlacementKind.BelowBand,
            "and the rung the ladder would take is no longer the one under the column: " + after.Describe());
        check(!HudPlacement.Proven(after, watch.Complete),
            "1.19.47: which is not a proof either - the row nobody could project is drawn wherever it is drawn, and the "
                + "recorded expedition put exactly that rung over the native column (log 2769: placement=above-band "
                + "table=(2087.67,226.33,459x60) rows=1 skipped=11 complete=no). A partial reading therefore places nothing "
                + "at all, and the vanilla HUD is left alone for that poll");
        check(!watch.Observe(complete) && watch.Complete,
            "the next complete reading proves it again, without moving anything (the comparison is inside the two-pixel tolerance)");
        check(HudPlacement.TryResolve(watch.Committed, watch.Complete, TableWidth, TableHeight, Anchor().Right,
                HudPlacement.Gap, ScreenWidth, ScreenHeight, out below) && below.Kind == PanelPlacementKind.BelowBand,
            "so the rung under the column is answered again: " + below.Describe());
        check(HudPlacement.Proven(below, watch.Complete) && HudPlacement.Area(below.Rect, watch.Committed) == 0f,
            "and it is proven again, on the column's own measured bottom edge: " + below.Describe());

        // A collapse candidate that is not complete: the geometry waits for its second agreeing reading (unchanged), the
        // completeness does not wait for anything.
        LayoutRect anchorOnly = Anchor();
        EquipmentBandReading shrunken = EquipmentBand.Read(new[]
        {
            new HudObstacle("flashlight", anchorOnly, drawn: true),
            HudObstacle.Unread("slot:Tool", drawn: true)
        }, ScreenWidth, ScreenHeight);
        check(shrunken.Measured && !shrunken.Complete
                && shrunken.Rect.Bottom < watch.Committed.Bottom - EquipmentBandWatch.Hysteresis,
            "a much smaller column with one unreadable row is a collapse candidate that is not complete: " + shrunken.Describe());
        check(!watch.Observe(shrunken), "a collapse still waits for its second agreeing reading");
        check(!watch.Complete, "but the incompleteness is immediate - it is not part of that agreement window");
        check(Near(watch.Committed.Bottom, complete.Rect.Bottom), "and the committed geometry is kept while it waits");
        check(watch.Observe(shrunken), "the second agreeing reading commits the smaller column");
        check(!watch.Complete, "which is still an unproven bottom edge: bottom=" + watch.Committed.Bottom.ToString("0.##"));
        check(!watch.Observe(EquipmentBand.Read(new[] { new HudObstacle("flashlight", anchorOnly, drawn: true) },
                    ScreenWidth, ScreenHeight)) && watch.Complete,
            "the first complete reading of that same rectangle proves it, again without moving anything");

        // An unreadable poll: the geometry is not deleted, the proof is.
        check(!watch.Observe(EquipmentBandReading.None), "a poll that measured nothing is not a verdict about the column");
        check(watch.Has && Near(watch.Committed.Bottom, anchorOnly.Bottom),
            "the committed rectangle is kept, so nothing deletes the geometry");
        check(!watch.Complete, "but the bottom edge is not proven by a poll that measured nothing");
    }

    // ---------- 1.19.45: a level change drops the column round, camera included ----------

    private static void ALevelChangeDropsTheColumnRound(Action<bool, string> check)
    {
        string panel = Source("PanelBehaviour.cs");
        string reset = Between(panel, "private void ForgetBandRound()", "private void Update()");
        check(reset.Length > 0, "the column round has one reset of its own");
        check(reset.Contains("_bandWatch.Restart();") && reset.Contains("_bandCamera = null;")
                && reset.Contains("_bandCameraProved = false;") && reset.Contains("_nextBandRead = 0f;")
                && reset.Contains("_bandRows = \"band=not-read\";") && reset.Contains("_placementNote = null;")
                && reset.Contains("_obstacles.Clear();") && reset.Contains("_absence.Clear();"),
            "and it drops the committed band, the proved camera, the read clock and every diagnostic line together");
        string unlock = Between(panel, "private void UnlockAnchor()", "private void ForgetBandRound()");
        check(Count(unlock, "ForgetBandRound();") == 1,
            "the resolution / configuration round calls it - which is the reset that already existed");
        string level = Between(panel, "if (inLevel != _inLevel)", "bool visible = inLevel ?");
        check(Count(level, "ForgetBandRound();") == 1,
            "and so does the level change, whose previous level destroyed both the HUD and the camera that projected it");
        check(!level.Contains("_anchorLocked = false;"),
            "and it does that without unlocking the anchor: the locked-anchor design of that branch is untouched");
        // A camera no round proved - or one whose object is gone - is never projected through, so no stale rectangle is
        // re-issued from it.
        check(panel.Contains("bool projectable = _bandCameraProved && BandCameraAlive(_bandCamera);"),
            "the row reader only projects through a camera this round proved and that still exists");
        check(Count(panel, "if (projectable) read = TryScreenRectFromLayout(anchorRow") == 1
                && Count(panel, "if (projectable && TryRowRect(item, camera, out var slotRect, out var evidence))") == 1,
            "and both projection sites - the anchor row and every slot row - are behind that gate");
        // 1.19.45: `isActiveAndEnabled` alone was the wrong test for exactly one kind of camera. The recorded HUD prints its
        // manual canvas' camera as `enabled=no, activeInHierarchy=yes` (log 2827) - that camera composites this element on
        // demand, so `enabled` names who calls `Render`, not whether the pixels land in the window. The decision is now a
        // pure rule (`BandCameraLiveness.Alive`), so its six facts are asserted as behaviour rather than read back as text.
        check(BandCameraLiveness.Alive(proved: true, manualCanvasProved: false, enabledAndActive: true, activeInHierarchy: true, readable: true),
            "an ordinary enabled, active camera is alive - unchanged from before this round");
        check(!BandCameraLiveness.Alive(false, false, false, true, true),
            "an ordinary DISABLED camera that no round proved is dead: nothing general is let through");
        check(!BandCameraLiveness.Alive(true, false, false, true, true),
            "and neither is a disabled one this round proved by an ordinary camera projection: only the manual-canvas proof relaxes `enabled`");
        check(BandCameraLiveness.Alive(proved: true, manualCanvasProved: true, enabledAndActive: false, activeInHierarchy: true, readable: true),
            "the manual canvas' own camera stays alive while disabled, because it is still active in the hierarchy (log 2827)");
        check(!BandCameraLiveness.Alive(true, true, false, false, true),
            "but not once its object is no longer active: the relaxation is hierarchy-liveness, not a licence for a dead HUD");
        check(!BandCameraLiveness.Alive(true, true, false, true, readable: false),
            "and an unreadable wrapper - the destroyed-object case that throws on first access - is never alive");
        check(!BandCameraLiveness.Alive(true, true, true, true, readable: false),
            "even an apparently enabled one: a fact that could not be read cannot be an answer");

        // The wiring, from the production sources: the rule is the one `BandCameraAlive` really runs, the `null` overlay
        // answer keeps its own branch, the proof is a real proof type, and the round reset drops both halves of it.
        check(Count(panel, "BandCameraLiveness.Alive(") == 2,
            "`BandCameraAlive` asks the shared rule on both of its paths - the readable one and the throwing one");
        check(panel.Contains("return BandCameraLiveness.Alive(_bandCameraProved, _bandCameraManualCanvas,"),
            "and it hands that rule this round's two proof facts, instead of answering out of its own copy of the rule");
        check(panel.Contains("if (camera == null) return true;"),
            "while a null camera stays the proved screen-space-overlay answer, decided before the rule is asked");
        check(panel.Contains("_bandCameraManualCanvas = measuredAnything")
              && panel.Contains("&& decided.Choice.Projection == AnchorProjection.Rect")
              && panel.Contains("&& decided.Choice.Source == AnchorCameraSource.None;"),
            "and what it relies on is a real proof type - the manual canvas' own `Rect` authorization with no source lens - not any disabled camera");
        check(reset.Contains("_bandCameraManualCanvas = false;") && reset.Contains("_bandCameraReproofs = 0;"),
            "the round reset clears the proof together with the camera AND the attempt budget, so a previous round's authorization can never make this level's disabled camera look alive");
        check(panel.Contains("_bandCameraProved = measuredAnything;"),
            "while the anchor read is what proves it, at the same moment it hands the camera over");
    }

    // ---------- the measurement chain ----------

    private static void TheMeasurementChainIsTheHudsOwnInventory(Action<bool, string> check)
    {
        string panel = Source("PanelBehaviour.cs");
        // The chain is the one the anchor is read from - not a second, parallel reading of the HUD.
        check(panel.Contains("layer.Inventory.m_iconDisplay"),
            "the anchor row is still the one `m_playerLayer -> Inventory -> m_iconDisplay` answers");
        check(panel.Contains("display.m_rectTrans != null ? display.m_rectTrans"),
            "and it is the display's own layout rectangle that is measured, as the anchor always was");
        check(panel.Contains("var order = inventory != null ? inventory.m_slotGUIOrder : null;"),
            "the column's rows are the slots the inventory itself lists in its own GUI order");
        check(panel.Contains("var slots = inventory != null ? inventory.m_inventorySlots : null;"),
            "each one is the row object the inventory holds for that slot");
        check(panel.Contains("if (!slots.TryGetValue(slot, out item) || item == null) continue;"),
            "a slot with no row is skipped rather than guessed at");
        // 1.19.43: an `IsVisible` that throws is a row this build cannot ask about, and the answer is "drawn" - a row
        // nobody could read must be able to keep the band open. Answering "not drawn" there passed a partial reading
        // off as a complete one, which is the same mistake at the other end of this chain.
        check(panel.Contains("try { drawn = item.IsVisible; } catch { drawn = true; }"),
            "the row's own visibility flag is what decides whether it is on the screen, and a flag that cannot be read is kept as drawn rather than as hidden");
        check(panel.Contains("if (projectable && TryRowRect(item, camera, out var slotRect, out var evidence))")
                && panel.Contains("private static bool TryRowRect(PUI_InventoryItem item, Camera camera, out LayoutRect rect, out string evidence)"),
            "every row goes through the same projection the anchor uses, which is the same `ScreenBounds` gate - behind the"
                + " 1.19.45 gate that refuses to project through a camera no round proved or one that no longer exists");
        check(panel.Contains("RectTransform own = item.m_rectTrans != null ? item.m_rectTrans : item.transform.TryCast<RectTransform>();"),
            "the row's own rectangle is still the first answer this chain asks for");
        check(panel.Contains("try { selected = item.m_selected_archetypeName; } catch { selected = null; }")
                && panel.Contains("try { slim = item.m_slim_archetypeName; } catch { slim = null; }")
                && panel.Contains("if (Drawn(selected)) return RectOf(selected);"),
            "1.19.47: and the native item name this row draws is the second - the two `TextMeshPro` members the sibling HUD"
                + " reader anchors its own labels with on this same build, chosen by the same active-in-hierarchy rule");
        check(panel.Contains("rect = EquipmentBand.Union(row, name);"),
            "a row that answered both is the union of the two rectangles, so neither answer can shrink the band");
        check(panel.Contains("evidence = \"(row-rect)\";") && panel.Contains("evidence = \"(name-text)\";"),
            "and the line says which element each row was measured from, so a recorded frame can be checked against the HUD");
        check(panel.Contains("_bandCamera = measuredAnything ? decided.Camera : null;"),
            "the camera is the one this attempt proved for the anchor, not a second lookup");
        check(panel.Contains("Camera camera = _bandCamera;"),
            "and every later re-read of the column uses that same camera");
        check(panel.Contains("private static Camera _bandCamera;"), "which is carried across the round");
        check(panel.Contains("return EquipmentBand.Read(_obstacles, Screen.width, Screen.height);"),
            "the window the band is measured in is the window the table is drawn in");
        check(panel.Contains("else _obstacles.Add(HudObstacle.Unread(AnchorElementNames.Flashlight, true));"),
            "an anchor row that could not be projected is recorded as unread, never as a row at zero");
        check(panel.Contains("else if (drawn) _obstacles.Add(HudObstacle.Unread(name, true));"),
            "and so is a slot row the HUD is drawing");

        // The placement is the production decision, fed the production band.
        check(panel.Contains("bool resolved = HudPlacement.TryResolve(_bandWatch.Committed, _bandWatch.Complete, grid.Width, grid.Height, _anchorRect.Right,"),
            "the table is placed by `HudPlacement` from the committed band and the table's own measured size");
        check(panel.Contains("HudPlacement.Gap, Screen.width, Screen.height, out placement)"),
            "with the mod's own gap and the real window");
        check(panel.Contains("x = placement.Rect.X + Plugin.OffsetX.Value;")
            && panel.Contains("y = placement.Rect.Y + Plugin.OffsetY.Value;"),
            "the two offset settings still move it, unchanged");

        // The known overlap is structurally unreachable: nothing in the panel adds the anchor row's bottom edge to the
        // gap any more, and the placement function is never handed the anchor row.
        check(!panel.Contains("_anchorRect.Bottom + 8f"),
            "1.19.40's `anchor row bottom + 8` is gone from the panel");
        check(panel.Contains("if (!_bandWatch.Has)"), "a HUD with no measured column takes the configured-position branch");
        check(Between(panel, "private bool TryAnchoredOrigin(", "private void ReportPlacement(")
                .Contains("return false;"),
            "which is answered as 'no anchored place' rather than as the old rectangle");
    }

    private static void TheBoundedReadNeverScansTheSceneOrGrowsWithTheColumn(Action<bool, string> check)
    {
        string panel = Source("PanelBehaviour.cs");
        string reader = Between(panel, "private EquipmentBandReading ReadEquipmentBand(PlayerGuiLayer layer)",
            "private string Rows(EquipmentBandReading reading)");
        check(reader.Length > 0, "the row reader is where the test found it");
        check(!reader.Contains("GetComponentsInChildren") && !reader.Contains("FindObjectsOfType")
            && !reader.Contains("Object.Find") && !reader.Contains("transform.parent"),
            "it walks no hierarchy and scans no scene: the rows are looked up by slot, one object each");
        check(reader.Contains("int ceiling = EquipmentBand.MaxObstacles - _obstacles.Count;"),
            "and the loop is bounded by the buffer's own ceiling, not by what the HUD happens to answer");
        check(panel.Contains("new List<HudObstacle>(EquipmentBand.MaxObstacles)"),
            "the buffer is allocated once, at its ceiling, and reused");
        check(panel.Contains("_obstacles.Clear();"), "every read starts by clearing that one buffer");
        check(panel.Contains("private const float BandInterval = 0.25f;")
            && panel.Contains("if (!float.IsFinite(now) || now < _nextBandRead) return;")
            && panel.Contains("_nextBandRead = now + BandInterval;"),
            "and the read happens on the panel's own 0.25 s poll - never per frame");
        check(panel.Contains("if (Plugin.AnchorToFlashlight.Value && _anchorLocked) WatchEquipmentBand(layer, Time.unscaledTime);"),
            "it only runs once the anchor is locked, because before that there is no proven camera to project rows with");
        check(panel.Contains("_bandWatch.Restart();") && panel.Contains("_bandCamera = null;"),
            "and a new round (level change, config reload, resolution change) forgets both");
        // The diagnostic is one line per real move, printing the rows it was built from.
        check(panel.Contains("if (note == _placementNote) return;"),
            "the placement is reported once per change, not once per poll");
        check(panel.Contains("_bandRows = Rows(reading);") && panel.Contains("text.Append(_obstacles[index].Describe());"),
            "and the line prints every row's own rectangle, so a recorded frame can be checked against the HUD");
        check(panel.Contains("placement=below-band") || panel.Contains("placement.Describe()"),
            "the line names the rung the table was placed by");
    }

    private static void TheConfiguredPositionIsUntouched(Action<bool, string> check)
    {
        string panel = Source("PanelBehaviour.cs");
        check(panel.Contains("float fractionX = Plugin.PositionX.Value;")
            && panel.Contains("float fractionY = Plugin.PositionY.Value;"),
            "the configured PositionX/PositionY path is byte-for-byte the arithmetic it always was");
        check(panel.Contains("x = (fractionX < 0f ? Screen.width - grid.Width + fractionX * Screen.width : fractionX * Screen.width) + Plugin.OffsetX.Value;"),
            "including the negative-means-from-the-right-edge rule");
        check(panel.Contains("y = (fractionY < 0f ? Screen.height - grid.Height + fractionY * Screen.height : fractionY * Screen.height) + Plugin.OffsetY.Value;"),
            "and the bottom-edge variant");
        check(panel.Contains("bool automatic = Plugin.AnchorToFlashlight.Value;") && panel.Contains("bool anchored = automatic && _anchorLocked;"),
            "the anchor switch still decides whether the column is consulted at all");
        check(panel.Contains("if (!anchored && automatic && !_anchorUnavailable && !_anchorFallback)")
            && panel.Contains("return false;   // still settling: draw nothing rather than jump"),
            "and an anchor that is still settling still draws nothing rather than jumping");
        check(panel.Contains("x = Mathf.Clamp(x, 0f, Mathf.Max(0f, Screen.width - grid.Width));")
            && panel.Contains("y = Mathf.Clamp(y, 0f, Mathf.Max(0f, Screen.height - grid.Height));"),
            "the whole table is still clamped inside the window whichever branch produced it");
        // The settings themselves are not touched: this round adds no configuration entry.
        string plugin = Source("Plugin.cs");
        int configEntries = Count(plugin, "Config.Bind(");
        check(!plugin.Contains("AnchorToEquipmentColumn") && !plugin.Contains("EquipmentColumnGap"),
            "no new configuration key was added for this: the columns are measured, not configured");
        check(configEntries > 0, "and the existing settings are still bound (" + configEntries + " entries)");
    }

    // ---------- the second recorded round ----------

    // The next recorded expedition (Legacy run, LogOutput.log lines 2737/2738): the same document, one level later.
    //
    // What the log really printed, and what is therefore evidence:
    //   anchor row   (2336.08, 197.33) size 210.59 x 110.16   -> its bottom edge is 307.49
    //   table        397 x 60 at (2150, 315) on 2560x1440     -> 315.49, printed as 315 by the log's own "0" format
    //   the rule     y = the anchor row's bottom edge + 8
    // What the log did *not* print is the equipment column below that row, so the rows under it are **simulated** here:
    // the same relative shape as the earlier round (rows stacked under the anchor row, 52/48/44/36 px) dropped onto this
    // round's own anchor, plus the throwable row. The new y this test derives is a replay of the *rule* on this round's
    // measured anchor - it is not a measurement of this round, and nothing here claims 584.49 or 180 px was seen in it.
    private static void TheSecondRecordedRoundIsTheSameOverlap(Action<bool, string> check)
    {
        const float anchorX = 2336.08f, anchorY = 197.33f, anchorWidth = 210.59f, anchorHeight = 110.16f;
        var anchorRow = new LayoutRect(anchorX, anchorY, anchorWidth, anchorHeight);
        check(Near(anchorRow.Bottom, 307.49f),
            "the second round's anchor row ends at 307.49 (" + anchorRow.Bottom.ToString("0.##") + ")");

        // The logged rectangle, exactly as the log printed it.
        var logged = new LayoutRect(2150f, 315f, 397f, 60f);
        check(Math.Abs(logged.Y - (anchorRow.Bottom + 8f)) <= 1f,
            "the logged table's y is the anchor row's bottom edge plus the gap, rounded down: " + logged.Y.ToString("0")
                + " vs " + (anchorRow.Bottom + 8f).ToString("0.##"));

        // Simulated rows: the *shape* of the column, not a measurement of this round.
        float top = anchorRow.Bottom;
        var column = new[]
        {
            new HudObstacle("iconRow", anchorRow, drawn: true),
            new HudObstacle("slot:GearStandard(sim)", new LayoutRect(anchorX, top, anchorWidth, 52f), drawn: true),
            new HudObstacle("slot:GearSpecial(sim)", new LayoutRect(anchorX, top + 52f, anchorWidth, 48f), drawn: true),
            new HudObstacle("slot:GearClass(sim)", new LayoutRect(anchorX, top + 100f, anchorWidth, 44f), drawn: true),
            new HudObstacle("slot:ResourcePack(sim)", new LayoutRect(anchorX, top + 144f, anchorWidth, 36f), drawn: true),
            new HudObstacle("slot:Consumable(sim)", new LayoutRect(anchorX, top + 180f, anchorWidth, 36f), drawn: true)
        };
        EquipmentBandReading band = EquipmentBand.Read(column, ScreenWidth, ScreenHeight);
        check(band.Measured && band.Complete, "the simulated column reads as a complete band: " + band.Describe());

        float overlap = HudPlacement.Area(logged, band.Rect);
        check(overlap > 0f,
            "the logged rectangle lies on this round's column: " + overlap.ToString("0") + " px2 ("
                + (overlap / (logged.Width * logged.Height) * 100f).ToString("0") + "% of the table)");
        check(HudPlacement.Area(logged, anchorRow) == 0f,
            "and it clears the anchor row itself, which is why the old placement looked right in the log");

        PanelPlacement placement;
        check(HudPlacement.TryResolve(band.Rect, band.Complete, TableWidth, TableHeight, anchorRow.Right,
            HudPlacement.Gap, ScreenWidth, ScreenHeight, out placement),
            "the rule answers a placement on this round's window too");
        check(placement.Kind == PanelPlacementKind.BelowBand && Near(placement.Overlap, 0f),
            "it is the rung below the column and it covers nothing: " + placement.Describe());
        check(Near(placement.Rect.Y, band.Rect.Bottom + HudPlacement.Gap),
            "one gap under the column's simulated bottom edge: y=" + placement.Rect.Y.ToString("0.##")
                + " (replay of the rule, not a measurement of this round)");
        // The numbers for a preview reader: the recorded rectangle, the simulated band and the placement the rule answers.
        Console.WriteLine("HUD-ROUND2: logged table=(" + logged.X.ToString("0") + "," + logged.Y.ToString("0") + ","
            + logged.Width.ToString("0") + "x" + logged.Height.ToString("0") + ") band=(" + band.Rect.X.ToString("0") + ","
            + band.Rect.Y.ToString("0") + "," + band.Rect.Width.ToString("0") + "x" + band.Rect.Height.ToString("0")
            + ") [simulated rows] new table=(" + placement.Rect.X.ToString("0") + "," + placement.Rect.Y.ToString("0") + ","
            + placement.Rect.Width.ToString("0") + "x" + placement.Rect.Height.ToString("0") + ") placement="
            + PanelPlacement.Name(placement.Kind) + " overlap=" + placement.Overlap.ToString("0"));
        check(HudPlacement.OnWindow(placement.Rect, ScreenWidth, ScreenHeight), "and the table is on the window");
        foreach (HudObstacle row in column)
            check(HudPlacement.Area(placement.Rect, row.Rect) == 0f, "no part of it lies on " + row.Name);
        check(placement.Rect.Y > logged.Y,
            "which is below where it was logged (" + logged.Y.ToString("0") + " -> " + placement.Rect.Y.ToString("0") + ")");
    }
    // ---------- the configured position, once the column is known ----------

    // "No evidence" and "confirmed conflict" are different answers and may not share one fallback. `HudFallback.Clear` is
    // the production decision; the panel's own configured-position arithmetic is replayed verbatim above it.
    private static void TheConfiguredFallbackIsNeverTheKnownOverlap(Action<bool, string> check)
    {
        EquipmentBandReading band = EquipmentBand.Read(Column(), ScreenWidth, ScreenHeight);
        check(band.Measured && band.Complete, "the recorded column is measured for this replay");
        // `PositionX=-0.02` / `PositionY=0.365` on the recorded window, for a 397x60 table: a negative fraction measures
        // from the right edge, which is the arithmetic the panel keeps byte-for-byte.
        float fractionX = -0.02f, fractionY = 0.365f;
        float width = TableWidth, height = TableHeight;
        var configured = new LayoutRect(ScreenWidth - width + fractionX * ScreenWidth, fractionY * ScreenHeight, width, height);
        // The recorded column reaches that position: on this page the fallback would be drawn over the column, which is
        // exactly the silent overlap this rule removes.
        float recordedOverlapPresent = HudPlacement.Area(configured, band.Rect);
        check(recordedOverlapPresent > 0f,
            "the recorded column reaches the configured position: " + recordedOverlapPresent.ToString("0") + " px2");
        check(HudFallback.Clear(configured, true, band.Rect, out float recordedOverlap) == false && recordedOverlap > 0f,
            "so it is a confirmed conflict there and the fallback is deferred, not drawn over the column");
        // A deeper column is the same answer, and it is the one the panel meets when the HUD grows a row.
        var deep = new[]
        {
            new HudObstacle("iconRow", Anchor(), drawn: true),
            new HudObstacle("column", new LayoutRect(AnchorX, AnchorY, AnchorWidth, 400f), drawn: true)
        };
        EquipmentBandReading deepBand = EquipmentBand.Read(deep, ScreenWidth, ScreenHeight);
        check(HudFallback.Clear(configured, true, deepBand.Rect, out float confirmed) == false && confirmed > 0f,
            "a deeper measured column under it defers the same position: " + confirmed.ToString("0") + " px2");
        // A column that no longer reaches it accepts the same position again: the deferral comes back by itself.
        EquipmentBandReading shortBand = EquipmentBand.Read(new[] { new HudObstacle("iconRow", Anchor(), drawn: true) },
            ScreenWidth, ScreenHeight);
        check(HudFallback.Clear(configured, true, shortBand.Rect, out float clearOverlap) && clearOverlap == 0f,
            "and the same position is accepted again once the column no longer reaches it");
        // No measurement at all is the third answer: the configured position is all anybody has.
        check(HudFallback.Clear(configured, false, deepBand.Rect, out float unmeasuredOverlap),
            "a HUD that was never measured takes the configured position, exactly as the manual mode does");
        check(unmeasuredOverlap == 0f, "and no conflict is claimed about a column nobody measured");
        // A position that simply is not on the column is never a conflict.
        var clear = new LayoutRect(200f, 200f, width, height);
        check(HudFallback.Clear(clear, true, deepBand.Rect, out _),
            "a configured position that does not reach the column is drawn, measured column or not");
        // 1.19.47: a column that was measured only *in part* refuses the same position for a different reason - the rows
        // nobody could project are drawn somewhere this reading cannot place anything against - and that refusal is the one
        // the panel asks for on every anchored poll.
        var partialRows = new[]
        {
            new HudObstacle("flashlight", Anchor(), drawn: true),
            HudObstacle.Unread("slot:GearMelee", drawn: true)
        };
        EquipmentBandReading partialBand = EquipmentBand.Read(partialRows, ScreenWidth, ScreenHeight);
        check(partialBand.Measured && !partialBand.Complete,
            "the recorded 1-in-12 reading is measured but not proven: " + partialBand.Describe());
        check(!HudFallback.Clear(configured, partialBand.Measured, partialBand.Complete, partialBand.Rect, out float partialOverlap)
                && partialOverlap == 0f,
            "so the configured position is refused, without claiming an overlap this reading cannot see");
        check(HudPlacement.Area(configured, partialBand.Rect) == 0f,
            "and that is the point: the configured rectangle misses the one row that *was* measured ("
                + HudPlacement.Area(configured, partialBand.Rect).ToString("0") + " px2) while the rows nobody could "
                + "project are drawn around it");
        check(HudFallback.Clear(configured, partialBand.Measured, true, partialBand.Rect, out float completedOverlap)
                && completedOverlap == 0f,
            "the very same position is accepted the moment the reading is complete again: the deferral is bounded by the "
                + "measurement, not by a timer");

        // The panel is wired to that rule, and only on the anchored path.
        string panel = Source("PanelBehaviour.cs");
        check(panel.Contains("bool configuredSafe = !automatic || (recovered && _bandWatch.Has && _bandWatch.Complete")
                && panel.Contains("bool proven = anchored && recovered && TryAnchoredOrigin(grid, out x, out y);"),
            "the configured position is only an automatic answer for a *complete* reading that clears it, and only after the recovery gate");
        // 1.19.48 (review): the recorded 2K configured rectangle misses the row that was read but not the column the unread rows are
        // drawn in, so a partial reading - or none at all - may not use it. That is asserted on the pixels by
        // `AConservativeSpotRestoresTheTable` below; this is the wiring that makes it so.
        check(panel.Contains("&& HudFallback.Clear(new LayoutRect(x, y, grid.Width, grid.Height), _bandWatch.Has,")
                && panel.Contains("_bandWatch.Complete, _bandWatch.Committed, out fallbackOverlap));"),
            "the completeness-aware band test is still the one the configured position has to pass");
        // 1.19.48: "no measured band" is no longer an unconditional refusal. That gate is what blanked the table for a whole
        // level after the recorded 1920x1080 -> 2560x1440 change, where the HUD's texture stayed 1080 and the column was
        // never measured at all.
        check(!panel.Contains("if (automatic && !_bandWatch.Has) return false;")
                && panel.Contains("else if (!anchored && automatic && !_anchorUnavailable && !_anchorFallback)"),
            "a poll without a measured band is answered with a place, while a round that is still settling keeps its silence");
        check(panel.Contains("if (!TryFallbackSpot(grid, y, out LayoutRect spot, out string spotWhy, out float spotOverlap))")
                && panel.Contains("ReportFallbackSpot(spot, spotWhy, spotOverlap, fallbackOverlap);")
                && panel.Contains("origin = spot;"),
            "a refused configured position is answered with the conservative place and drawn: the refusal is about proofs, not about painting");
        check(panel.Contains("DeferFallback(new LayoutRect(x, y, grid.Width, grid.Height), fallbackOverlap, _bandWatch.Complete);"),
            "and the one answer that still draws nothing - a table larger than the window - keeps its own line");
        check(panel.Contains("if (!_absence.Claim(complete ? HudAbsence.DeferOverlap : HudAbsence.DeferIncomplete)) return;")
                && panel.Contains("fallbackCovers=") && panel.Contains("fallbackComplete="),
            "the deferral is reported once per round under the vocabulary's own key, and its line states the completeness beside the area");
        check(panel.Contains("if (!_absence.Claim(HudAbsence.Spot(reason))) return;")
                && panel.Contains("rung, no proof and no anchor"),
            "the conservative place is reported once per candidate per round, and its own line says it claims no rung");
        // 1.19.49: the family is deduped by *meaning*. The recorded 1.19.48 round wrote four lines for one partial column
        // because the table's width (459/479/471/482 px) was part of the note; here the key is the candidate that answered and
        // nothing else, the ledger is emptied only with the round, and the three full-note latches are gone. 1.19.50 moves the
        // vocabulary and the bound into `HudAbsenceLedger`, where `Release11950Scenarios` asserts that all ten reasons are
        // reportable and an eleventh is not.
        check(panel.Contains("private readonly HudAbsenceLedger _absence = new HudAbsenceLedger();")
                && Between(panel, "private void ForgetBandRound()", "private void Update()").Contains("_absence.Clear();")
                && !panel.Contains("_absenceNotes") && !panel.Contains("ClaimAbsenceNote")
                && !panel.Contains("_fallbackSpotNote") && !panel.Contains("_fallbackDeferNote") && !panel.Contains("_avoidanceNote"),
            "every absence line of the family is latched by the round's own ledger, claimed once per round, and re-armed by nothing "
                + "inside that round - not by the width, the spot, the window, the retry count or a second F8 press");
        // 1.19.49: the whole family is Info. The automatic mode waiting for a proof is a state of this HUD, and the warning
        // channel is left for what really went wrong - the one exception is a read that threw, which stays a Warning.
        string anchorFamily = Between(panel, "private bool TryAnchoredOrigin(", "private void ReportPlacement(");
        check(anchorFamily.Length > 0 && !anchorFamily.Contains("LogWarning") && Count(anchorFamily, "LogInfo") == 3,
            "the three 'no proven place' lines are Info, not Warning");
        check(!Between(panel, "private void ReportFallbackSpot(", "private void DeferFallback(").Contains("LogWarning")
                && !Between(panel, "private void DeferFallback(", "private bool TryAnchoredOrigin(").Contains("LogWarning"),
            "and so are the conservative place and the one poll that still draws nothing");
        string failureLine = Between(panel, "if (_anchorPolicy.FreshWarning)", "// The configured position is drawn while the retries continue");
        check(failureLine.Contains("if (string.IsNullOrEmpty(sample.Error)) Plugin.Logger.LogInfo(line);")
                && failureLine.Contains("else Plugin.Logger.LogWarning(line);"),
            "a measurement that has not arrived is Info, while a read that really threw keeps its Warning and its message");
        check(panel.Contains("Plugin.Logger.LogInfo($\"[DIAG] the flashlight anchor fast retries ended after"),
            "and the give-up line is Info for the same reason");
        // 1.19.48 (review): the proof is sticky once a conservative place was drawn, so a page that flips complete/partial at the
        // 0.25 s poll cannot move the table (or write a line) at every turn - and cannot stay fallen back for ever either.
        check(panel.Contains("bool recovered = !_fallbackSpotDrawn || _bandCompleteStreak >= StableCompleteSamples;")
                && panel.Contains("private const int StableCompleteSamples = 2;")
                && panel.Contains("if (_bandWatch.Has && _bandWatch.Complete) _bandCompleteStreak++;")
                && panel.Contains("else _bandCompleteStreak = 0;"),
            "the recovery needs consecutive complete readings, and a partial or unreadable poll breaks the run");
        check(panel.Contains("bool automatic = Plugin.AnchorToFlashlight.Value;")
                && panel.Contains("bool anchored = automatic && _anchorLocked;")
                && panel.Contains("bool configuredSafe = !automatic ||"),
            "manual placement bypasses the automatic geometry gate");
        check(Count(panel, "if (automatic && !HudFallback.Clear(") == 0
                && Count(panel, "HudFallback.Clear(new LayoutRect(x, y, grid.Width, grid.Height), _bandWatch.Has,") == 1
                && Count(panel, "DeferFallback(") == 2,
            "the automatic test has exactly one call site and the last-resort deferral exactly one: `AnchorToFlashlight=false` keeps the " +
                "configured position with nothing added");
    }

    // ---------- 1.19.47: the recorded retest ----------

    // The expedition of 2026-11-22 logged the defect in one line (log 2769, repeated at 2777):
    //
    //   placement=above-band table=(2087.67,226.33,459x60) overlap=0 ... anchorRow=(2336,294,211x110)
    //   band=(2336.08,294.33,210.59x110.16) rows=1 skipped=11 complete=no
    //   rows:flashlight=(2336.08,294.33,210.59x110.16),slot:GearMelee=unread,slot:GearStandard=unread,...
    //
    // One row was measured and eleven were stated unread, and the ladder answered a rung whose separation is measured
    // from an edge this reading never proved. The screenshot of that frame shows the table lying on the native equipment
    // column, above the flashlight row: the rows the reader could not project are drawn *there*. `overlap=0` only ever
    // said the rectangle misses the one row that was read.
    private static void APartialReadingIssuesNoRungAtAll(Action<bool, string> check)
    {
        var anchorRow = new LayoutRect(2336f, 294f, 211f, 110f);
        var rows = new List<HudObstacle>
        {
            new HudObstacle("flashlight", new LayoutRect(2336.08f, 294.33f, 210.59f, 110.16f), drawn: true),
            HudObstacle.Unread("slot:GearMelee", drawn: true),
            HudObstacle.Unread("slot:GearStandard", drawn: true),
            HudObstacle.Unread("slot:GearSpecial", drawn: true),
            HudObstacle.Unread("slot:GearClass", drawn: true),
            HudObstacle.Unread("slot:ResourcePack", drawn: true),
            HudObstacle.Unread("slot:Consumable", drawn: true),
            HudObstacle.Unread("slot:unnamed0", drawn: true),
            HudObstacle.Unread("slot:unnamed1", drawn: true),
            HudObstacle.Unread("slot:unnamed2", drawn: true),
            HudObstacle.Unread("slot:unnamed3", drawn: true),
            HudObstacle.Unread("slot:unnamed4", drawn: true)
        };
        EquipmentBandReading band = EquipmentBand.Read(rows, ScreenWidth, ScreenHeight);
        check(band.Measured && !band.Complete && band.Used == 1 && band.Skipped == 11,
            "the recorded reading is replayed exactly: one row measured, eleven stated unread - " + band.Describe());
        check(Near(band.Rect.Bottom, 404.49f) && Near(band.Rect.Right, 2546.67f),
            "and its band is the flashlight row's own rectangle, nothing more: " + band.Describe());

        // The production ladder, on those numbers: this is the line the log printed, produced again.
        PanelPlacement placement;
        bool resolved = HudPlacement.TryResolve(band.Rect, band.Complete, 459f, 60f, anchorRow.Right,
            HudPlacement.Gap, ScreenWidth, ScreenHeight, out placement);
        Console.WriteLine("HUD-RETEST: ladder=" + (resolved ? PanelPlacement.Name(placement.Kind) : "none")
            + " at (" + placement.Rect.X.ToString("0.##") + "," + placement.Rect.Y.ToString("0.##") + ")"
            + " band=" + band.Describe() + " proven=" + HudPlacement.Proven(placement, band.Complete));
        check(resolved && placement.Kind == PanelPlacementKind.AboveBand,
            "the ladder still answers the rung the log names: " + PanelPlacement.Name(placement.Kind));
        check(Near(placement.Rect.X, 2087.67f, 0.6f) && Near(placement.Rect.Y, 226.33f, 0.6f),
            "at the rectangle the log printed, (2087.67,226.33,459x60): (" + placement.Rect.X.ToString("0.##") + ","
                + placement.Rect.Y.ToString("0.##") + ")");
        check(HudPlacement.Area(placement.Rect, band.Rect) == 0f,
            "it misses the one row that was read, which is all `overlap=0` ever said");
        check(placement.Rect.Right > band.Rect.X && placement.Rect.Right <= band.Rect.Right + 0.5f,
            "while it is right-aligned with the column, so every native row drawn above the flashlight row is under it: "
                + "table right=" + placement.Rect.Right.ToString("0.##") + " column left=" + band.Rect.X.ToString("0.##"));

        // The rule that stops it.
        check(!HudPlacement.Proven(placement, band.Complete),
            "1.19.47: and that rung is not a proof - an edge this reading did not prove cannot separate the table from "
                + "rows it did not read");
        check(HudPlacement.Proven(placement, true),
            "the same rung is a proof for a reading that proves the whole column, so the ladder itself is unchanged");
        string panel = Source("PanelBehaviour.cs");
        // 1.19.49: the partial answer is one semantic key in the round's absence ledger, so the same partial column costs one
        // line however many times it is polled - and 1.19.49 puts the place it draws at on the right margin.
        check(panel.Contains("if (_absence.Claim(HudAbsence.AnchorPartial))"),
            "the panel therefore answers a partial reading with its own deferral, and claims its line once per round");
        check(panel.Contains("so 'below', 'above' and ")
                && panel.Contains("'beside' the band would each be measured from an edge it has not proved"),
            "naming all three rungs, because all three lean on an edge of the same column");
    }

    // The offset settings are the player's, but the rectangle they produce is what is painted: 1.19.47 re-checks the
    // clamped, offset rectangle against the same band before anything is drawn, so `OffsetY` cannot walk the table back
    // onto the column the rung just cleared - and the line prints the rectangle that is really drawn.
    private static void TheDrawnRectangleIsReAvoidedAfterTheOffset(Action<bool, string> check)
    {
        EquipmentBandReading band = EquipmentBand.Read(Column(), ScreenWidth, ScreenHeight);
        check(band.Measured && band.Complete, "the recorded column is measured for this replay");
        PanelPlacement placement;
        check(HudPlacement.TryResolve(band.Rect, band.Complete, TableWidth, TableHeight, Anchor().Right, HudPlacement.Gap,
                ScreenWidth, ScreenHeight, out placement) && placement.Kind == PanelPlacementKind.BelowBand,
            "the proven rung is the one under the column: " + placement.Describe());
        // A player offset that lifts the table by 100 px puts it straight back on the column.
        var pushed = HudPlacement.ClampInto(
            new LayoutRect(placement.Rect.X, placement.Rect.Y - 100f, TableWidth, TableHeight), ScreenWidth, ScreenHeight);
        check(HudPlacement.OnWindow(pushed, ScreenWidth, ScreenHeight),
            "the offset rectangle is still on the window, so nothing but the band check can refuse it");
        check(HudPlacement.Area(pushed, band.Rect) > 0f,
            "and it covers the column again: " + HudPlacement.Area(pushed, band.Rect).ToString("0") + " px2");
        check(HudPlacement.Area(placement.Rect, band.Rect) == 0f,
            "so the proven rung is what is drawn instead - the offset is dropped, not the avoidance");
        // A clamp at the window's edge is the same question: the clamped rectangle is what is checked, never the rung alone.
        var atEdge = HudPlacement.ClampInto(new LayoutRect(ScreenWidth + 500f, 700f, TableWidth, TableHeight),
            ScreenWidth, ScreenHeight);
        check(HudPlacement.OnWindow(atEdge, ScreenWidth, ScreenHeight) && Near(atEdge.Right, ScreenWidth),
            "a rectangle pushed past the right edge is clamped into the window: right=" + atEdge.Right.ToString("0.##"));

        string panel = Source("PanelBehaviour.cs");
        check(panel.Contains("LayoutRect final = HudPlacement.ClampInto(new LayoutRect(x, y, grid.Width, grid.Height), Screen.width, Screen.height);")
                && panel.Contains("bool offsetKept = HudPlacement.OnWindow(final, Screen.width, Screen.height)")
                && panel.Contains("&& HudPlacement.Area(final, _bandWatch.Committed) <= 0f;")
                && panel.Contains("if (!offsetKept) final = HudPlacement.ClampInto(placement.Rect, Screen.width, Screen.height);"),
            "the panel asks the same question of the rectangle it is about to draw, offset and clamp included");
        check(panel.Contains("x = final.X;") && panel.Contains("y = final.Y;"),
            "and hands that rectangle to the paint, not the rung");
        check(panel.Contains("final=({final.X:0.##},{final.Y:0.##},{final.Width:0.##}x{final.Height:0.##})")
                && panel.Contains("offsetKept="),
            "the placement line prints the true final rectangle and whether the offset survived the re-check");
    }

    // A row of this HUD's column answers through its own native item name text. `PUI_InventoryItem` carries no rectangle
    // of its own on the recorded build (`rows=1 skipped=11`), and the two `TextMeshPro`s it alternates between do - the same
    // two members the sibling HUD reader anchors its labels with on this same game build.
    private static void TheRowIsMeasuredThroughItsOwnNativeName(Action<bool, string> check)
    {
        string panel = Source("PanelBehaviour.cs");
        string rowRect = Between(panel, "private static bool TryRowRect(PUI_InventoryItem item, Camera camera, out LayoutRect rect, out string evidence)",
            "private static RectTransform NativeItemName(PUI_InventoryItem item)");
        check(rowRect.Length > 0, "the per-row measurement is where the test found it");
        check(rowRect.Contains("RectTransform own = item.m_rectTrans != null ? item.m_rectTrans : item.transform.TryCast<RectTransform>();"),
            "the row's own rectangle is asked first, exactly as before");
        check(rowRect.Contains("RectTransform label = NativeItemName(item);"),
            "and the native item name it draws is asked second");
        check(rowRect.Contains("rect = EquipmentBand.Union(row, name);"),
            "a row that answered both is the union of the two, so the second answer never shrinks the first");
        string name = Between(panel, "private static RectTransform NativeItemName(PUI_InventoryItem item)",
            "private static bool Drawn(TextMeshPro text)");
        check(name.Contains("item.m_selected_archetypeName") && name.Contains("item.m_slim_archetypeName"),
            "the two members are the ones the sibling HUD reader uses on this build - no member is invented here");
        check(name.Contains("if (Drawn(selected)) return RectOf(selected);")
                && name.Contains("if (Drawn(slim)) return RectOf(slim);"),
            "and the choice between them is the one fact this build can answer - which of the two is on the screen");
        string drawn = Between(panel, "private static bool Drawn(TextMeshPro text)", "private static RectTransform RectOf(TextMeshPro text)");
        check(drawn.Contains("text.gameObject.activeInHierarchy") && drawn.Contains("catch { return false; }"),
            "a text that is switched off - or one whose wrapper is gone - is not evidence about where the row is");

        var rows = new List<HudObstacle>
        {
            new HudObstacle("slot:GearStandard(name-text)", new LayoutRect(2336f, 404f, 210f, 52f), drawn: true, fullRow: false),
            new HudObstacle("slot:ResourcePack(name-text)", new LayoutRect(2336f, 456f, 210f, 36f), drawn: true, fullRow: false)
        };
        EquipmentBandReading named = EquipmentBand.Read(rows, ScreenWidth, ScreenHeight);
        check(named.Measured && !named.Complete && Near(named.Rect.Bottom, 492f),
            "name-only geometry is kept but does not prove the full row: " + named.Describe());
        HudPlacement.TryResolve(named.Rect, named.Complete, TableWidth, TableHeight,
            named.Rect.Right, HudPlacement.Gap, ScreenWidth, ScreenHeight, out PanelPlacement placement);
        check(!HudPlacement.Proven(placement, named.Complete), "partial row evidence does not authorize auto placement");
        rows[0] = new HudObstacle("slot:GearStandard(row-rect)", rows[0].Rect, true);
        rows[1] = new HudObstacle("slot:ResourcePack(row-rect)", rows[1].Rect, true);
        EquipmentBandReading full = EquipmentBand.Read(rows, ScreenWidth, ScreenHeight);
        check(full.Complete && HudPlacement.TryResolve(full.Rect, full.Complete, TableWidth, TableHeight,
            full.Rect.Right, HudPlacement.Gap, ScreenWidth, ScreenHeight, out placement)
            && HudPlacement.Proven(placement, full.Complete), "a full subsequent sample restores auto placement");
        // The union itself: the row covers both answers, never only the first.
        LayoutRect cover = EquipmentBand.Union(new LayoutRect(10f, 10f, 20f, 20f), new LayoutRect(0f, 40f, 60f, 10f));
        check(Near(cover.X, 0f) && Near(cover.Y, 10f) && Near(cover.Right, 60f) && Near(cover.Bottom, 50f),
            "`EquipmentBand.Union` is the smallest rectangle covering both answers");
    }

    // The second expedition of the recorded run (log 3755/3756): a 1920x1080 window and a compositor whose texture and
    // canvas are 2560x1440. The authorization refuses, names the missing fact, and the table keeps the safe fallback.
    // 1.19.47 changes nothing here: a matching aspect is not a matching pixel grid, and guessing a conversion is exactly
    // what the refusal exists to prevent - so the *recovery* is what this asserts, not a scaled projection.
    private static void AnUnscaledCompositorKeepsItsSafeRefusal(Action<bool, string> check)
    {
        var recorded = new AnchorManualCanvasFacts(
            canvasPresent: true, cameraPresent: true, elementInsideCanvas: true, elementLayer: 5,
            cameraDrawsLayer: true, cameraMask: 0x00000020,
            cameraPixelWidth: 2560, cameraPixelHeight: 1440,
            canvasPixelWidth: 2560f, canvasPixelHeight: 1440f,
            windowWidth: 1920, windowHeight: 1080,
            viewportX: 0f, viewportY: 0f, viewportWidth: 1f, viewportHeight: 1f);
        check(!recorded.Verified, "the recorded 1920 window against a 2560 texture is not a verified chain");
        check(!recorded.RenderTargetMatchesWindow,
            "the fact that refuses it is the texture size, not the canvas' aspect: target=" + recorded.CameraPixelWidth
                + "x" + recorded.CameraPixelHeight + " window=" + recorded.WindowWidth + "x" + recorded.WindowHeight);
        string refusal = AnchorManualCanvasPolicy.Refusal(recorded);
        check(refusal.Contains("no conversion from that texture's pixels has been verified"),
            "and the refusal says exactly that: " + refusal);
        check(AnchorManualCanvasPolicy.Describe(recorded).Contains("targetMatchesWindow=no"),
            "the line carries the two grids side by side, so the next run can be compared rather than guessed at");

        // The recovery half: the same HUD, once the window really is the texture's size, is authorized again. That is the
        // only conversion this build accepts - the identity one.
        var matched = new AnchorManualCanvasFacts(
            canvasPresent: true, cameraPresent: true, elementInsideCanvas: true, elementLayer: 5,
            cameraDrawsLayer: true, cameraMask: 0x00000020,
            cameraPixelWidth: 1920, cameraPixelHeight: 1080,
            canvasPixelWidth: 1920f, canvasPixelHeight: 1080f,
            windowWidth: 1920, windowHeight: 1080,
            viewportX: 0f, viewportY: 0f, viewportWidth: 1f, viewportHeight: 1f);
        check(matched.Verified && AnchorManualCanvasPolicy.Refusal(matched) == "none",
            "a compositor whose pixels are the window's is the proof this ladder asks for, and it is granted again");
        check(AnchorManualCanvasPolicy.Describe(matched).Contains("origin=manual-canvas"),
            "so a run at that window recovers the anchored placement without any new rule");
        // No ratio-based shortcut was added anywhere: the refusal is decided by the two pixel sizes, and nothing in the
        // ladder turns one into the other.
        string ladder = Source("AnchorCameraLadder.cs");
        check(!ladder.Contains("CameraPixelWidth * ") && !ladder.Contains("WindowWidth / CanvasPixelWidth"),
            "and nothing in the ladder scales a texture pixel into a window pixel: there is no ratio to guess with");
    }

    // A round that cannot place the table may not leave the last rectangle it drew in the field: the paint is stopped by
    // `_grid`, and the drawn origin is cleared on the same poll so nothing else can read a stale rectangle.
    private static void ARefusedRoundClearsTheDrawnOrigin(Action<bool, string> check)
    {
        string panel = Source("PanelBehaviour.cs");
        check(panel.Contains("if (!TryResolveOrigin(_grid, out var origin)) { _grid = null; _content = null; _origin = default; return; }"),
            "the refused poll clears the grid, the content and the drawn origin together");
        check(panel.Contains("if (_grid != null && _content != null) HudTable.Draw(_grid, _origin.X, _origin.Y, _content);"),
            "and the paint still refuses a null grid, so neither lock can be bypassed on its own");
        // The deferral itself leaves the vanilla page alone: nothing in it asks for a grid, a content or an origin.
        string defer = Between(panel, "private void DeferFallback(", "private bool TryAnchoredOrigin(");
        check(defer.Length > 0 && !defer.Contains("_origin =") && !defer.Contains("HudTable.Draw"),
            "while the deferral line itself never paints or places anything");
    }

    // ---------- 1.19.48: the conservative place, on the window's right margin since 1.19.49 ----------
    //
    // The recorded refusals of 2026-11-23: at 2560x1440 the anchor row measured while eleven slot rows came back `unread`
    // (log 2776/2777), so a 459x120 table was refused on every poll. The review of the first fix is the second half of that
    // reading: the configured rectangle (2024,526,459x120) misses the row that *was* read by 122 px in y, but it overlaps the
    // column's own x range (2336..2546) from 2336 to 2483 - and the eleven rows nobody could project are drawn there. "It
    // misses the row I read" is not "it misses the column", so a partial reading (or none at all: the recorded texture that
    // stayed 1080) may not draw the configured position. 1.19.48 answered that with the window's LEFT edge and the recorded
    // frame drew the table at (24,526) - the far side of a 2560 px window from the column it stands in for - so 1.19.49 puts
    // every candidate on the RIGHT margin instead, the edge a proven rung keeps. Asserted on pixels, both ways.
    private static void AConservativeSpotRestoresTheTable(Action<bool, string> check)
    {
        var anchorRow = new LayoutRect(2336.08f, 294.33f, 210.59f, 110.16f);
        var measured = new[] { anchorRow };
        // The configured rectangle the recorded page really drew its refusal line about.
        var configured = new LayoutRect(2024f, 526f, 459f, 120f);
        check(HudPlacement.Area(configured, anchorRow) == 0f,
            "the recorded configured rectangle misses the one row that was measured in y: "
                + HudPlacement.Area(configured, anchorRow).ToString("0") + " px2");
        check(configured.X < anchorRow.Right && configured.Right > anchorRow.X,
            "but it sits on that column's own x range, where the rows this build could not project are drawn: configured x="
                + configured.X.ToString("0") + ".." + configured.Right.ToString("0") + " column x="
                + anchorRow.X.ToString("0") + ".." + anchorRow.Right.ToString("0"));

        LayoutRect spot;
        string why;
        float covers;
        bool resolved = HudFallbackSpot.TryResolve(configured.Y, measured, configured.Width, configured.Height,
            ScreenWidth, ScreenHeight, out spot, out why, out covers);
        Console.WriteLine("HUD-FALLBACK: reason=" + why + " spot=(" + spot.X.ToString("0.##") + "," + spot.Y.ToString("0.##")
            + "," + spot.Width.ToString("0") + "x" + spot.Height.ToString("0") + ") covers=" + covers.ToString("0"));
        // 1.19.49: the place keeps the player's own row and is right-aligned to the window's own margin - on the equipment
        // column's side of the screen, which is the edge a proven rung keeps. 1.19.48 answered the left edge, and the recorded
        // frame drew the table at (24,526): about 1,850 px from the column it stands in for.
        check(resolved && why == "right-row" && Near(spot.Right, ScreenWidth - HudFallbackSpot.Margin)
                && Near(spot.Y, configured.Y) && covers == 0f,
            "the 2560 page keeps the player's own row and right-aligns the table to the window's margin: " + why + " at ("
                + spot.X.ToString("0.##") + "," + spot.Y.ToString("0.##") + "), right=" + spot.Right.ToString("0.##"));
        check(HudPlacement.OnWindow(spot, ScreenWidth, ScreenHeight), "and it stays inside the window");
        check(HudPlacement.Area(spot, anchorRow) == 0f, "clear of the one row this reading really measured");
        check(spot.X > ScreenWidth / 2f,
            "and on the column's own side of the screen, never the far side the recorded 1.19.48 frame used: candidate left="
                + spot.X.ToString("0") + " window middle=" + (ScreenWidth / 2f).ToString("0"));

        // All four widths the recorded round reported for the same column (459, 479, 471 and 482 px). Right-aligned, every one
        // of them keeps the same edge and only the left edge moves - which is exactly what the proven rung does, and why the
        // width is not a reason to re-report the place either.
        foreach (float width in new[] { 459f, 479f, 471f, 482f })
        {
            check(HudFallbackSpot.TryResolve(configured.Y, measured, width, 60f, ScreenWidth, ScreenHeight,
                    out spot, out why, out covers) && why == "right-row"
                    && Near(spot.Right, ScreenWidth - HudFallbackSpot.Margin) && covers == 0f,
                "the " + width.ToString("0") + " px answer of the recorded round keeps the same right edge: "
                    + spot.Right.ToString("0.##"));
        }

        // A narrow window keeps the same margin rather than degenerating to a left-edge clamp.
        check(HudFallbackSpot.TryResolve(300f, Array.Empty<LayoutRect>(), 459f, 120f, 600f, 720f, out spot, out why, out _)
                && why == "right-row" && Near(spot.Right, 600f - HudFallbackSpot.Margin) && spot.X >= 0f,
            "a 600 px window right-aligns the same 459 px table with the same margin: right=" + spot.Right.ToString("0.##"));

        // The 1920 page of the same run: configured (1500,394,362x45), column at x>=1752.
        var smallColumn = new LayoutRect(1752.06f, 220.75f, 157.94f, 82.62f);
        var smallConfigured = new LayoutRect(1500f, 394f, 362f, 45f);
        check(smallConfigured.Right > smallColumn.X,
            "the 1920 configured rectangle reaches into the column's x range as well: right=" + smallConfigured.Right.ToString("0")
                + " column left=" + smallColumn.X.ToString("0"));
        check(HudFallbackSpot.TryResolve(smallConfigured.Y, new[] { smallColumn }, 362f, 45f, 1920f, 1080f,
                out spot, out why, out covers)
                && why == "right-row" && Near(spot.Right, 1920f - HudFallbackSpot.Margin) && covers == 0f,
            "so the 1920 page gets the same right-margin place, clear of the column's own row: " + why);
        check(spot.X >= 1920f / 2f, "and it is on the right half of the window, the equipment column's side");

        // One measured row and nothing else - the recorded 1-in-12 reading - with the table's own row lying on it: the place
        // steps below that row, still on the right margin, and never onto the one row that was read.
        check(HudFallbackSpot.TryResolve(300f, measured, configured.Width, configured.Height, ScreenWidth, ScreenHeight,
                out spot, out why, out covers) && why == "below-measured"
                && Near(spot.Right, ScreenWidth - HudFallbackSpot.Margin) && covers == 0f && spot.Y >= anchorRow.Bottom,
            "with one row measured and the table's row on it, the place steps below that row on the right margin: " + why);

        // A measured block that reaches the player's own row on this side of the window: the place steps below it, still
        // right-aligned.
        var covering = new[] { new LayoutRect(ScreenWidth - 900f, 500f, 900f, 400f) };
        check(HudFallbackSpot.TryResolve(configured.Y, covering, configured.Width, configured.Height, ScreenWidth, ScreenHeight,
                out spot, out why, out covers) && why == "below-measured"
                && Near(spot.Right, ScreenWidth - HudFallbackSpot.Margin) && covers == 0f && spot.Y > 900f,
            "a measured row the player's own height really covers puts the table below it, still right-aligned: " + why);
        // The whole page blocked: the least-overlapping right-aligned candidate stands, and it says so with the area.
        var everything = new[] { new LayoutRect(0f, 0f, ScreenWidth, ScreenHeight) };
        check(HudFallbackSpot.TryResolve(configured.Y, everything, configured.Width, configured.Height, ScreenWidth, ScreenHeight,
                out spot, out why, out covers) && why == "least-overlap" && covers > 0f
                && Near(spot.Right, ScreenWidth - HudFallbackSpot.Margin),
            "an unreadable page still draws the table, at the right margin, with the area it covers reported: " + covers.ToString("0"));

        // No geometry at all - the recorded texture that stayed 1080 - and a configured row off the window: the same place,
        // pulled inside the margin. The configured position is never the automatic answer here.
        check(HudFallbackSpot.TryResolve(-40f, Array.Empty<LayoutRect>(), 459f, 120f, ScreenWidth, ScreenHeight,
                out spot, out why, out covers) && why == "right-row" && Near(spot.Y, HudFallbackSpot.Margin),
            "with nothing measured the right margin is still the answer, and the row is pulled inside the margin: " + spot.Y.ToString("0.##"));
        check(HudFallbackSpot.TryResolve(526f, null, 459f, 120f, ScreenWidth, ScreenHeight, out spot, out why, out _)
                && why == "right-row" && Near(spot.Right, ScreenWidth - HudFallbackSpot.Margin),
            "a poll that measured nothing at all never draws the configured position in the automatic mode: it draws the right-margin place");

        // The one refusal left: a table that cannot be inside the window at all.
        check(!HudFallbackSpot.TryResolve(526f, measured, ScreenWidth + 1f, 120f, ScreenWidth, ScreenHeight, out _, out _, out _),
            "a table wider than the window is the only input this refuses: a property of the table, not of the HUD");
        check(!HudFallbackSpot.TryResolve(526f, measured, float.NaN, 120f, ScreenWidth, ScreenHeight, out _, out _, out _)
                && !HudFallbackSpot.TryResolve(526f, measured, 459f, 120f, 0f, 0f, out _, out _, out _),
            "and a table or a window whose size cannot be read is refused rather than guessed at");

        // The recovery this must not disturb: a reading that proves the whole column still places the table by the ladder.
        EquipmentBandReading complete = EquipmentBand.Read(Column(), ScreenWidth, ScreenHeight);
        PanelPlacement placement = default;
        check(complete.Measured && complete.Complete
                && HudPlacement.TryResolve(complete.Rect, complete.Complete, 459f, 120f, complete.Rect.Right, HudPlacement.Gap,
                    ScreenWidth, ScreenHeight, out placement)
                && HudPlacement.Proven(placement, complete.Complete),
            "a complete reading still places the table by the proven rung: " + PanelPlacement.Name(placement.Kind));
        string panel = Source("PanelBehaviour.cs");
        string resolve = Between(panel, "private bool TryResolveOrigin(PanelGrid grid", "private bool TryFallbackSpot(");
        check(resolve.Length > 0
                && resolve.IndexOf("bool proven = anchored && recovered && TryAnchoredOrigin(", StringComparison.Ordinal) >= 0
                && resolve.IndexOf("bool proven = anchored && recovered && TryAnchoredOrigin(", StringComparison.Ordinal)
                    < resolve.IndexOf("HudFallback.Clear(", StringComparison.Ordinal)
                && !resolve.Contains("_fallbackObstacles.Add"),
            "and the panel asks the proven rung before it ever asks for a conservative place");
        check(resolve.Contains("bool configuredSafe = !automatic || (recovered && _bandWatch.Has && _bandWatch.Complete")
                && resolve.Contains("_fallbackSpotDrawn = true;"),
            "the configured position needs a complete reading and the recovery gate, and the spot that is really drawn is recorded");
        string spotScope = Between(panel, "private bool TryFallbackSpot(", "private void ReportFallbackSpot(");
        check(spotScope.Length > 0 && !spotScope.Contains("_anchorRect") && !spotScope.Contains("_anchorPolicy")
                && spotScope.Contains("_bandWatch.Committed") && !spotScope.Contains("_bandWatch.Complete"),
            "the conservative place reads no anchor rectangle and no attempt budget; the committed band is only an obstacle "
                + "there, never a completeness claim");
        check(spotScope.Contains("_anchorScreenWidth == Screen.width && _anchorScreenHeight == Screen.height"),
            "and that retained band is only used while it belongs to the window that is on the screen now");
        check(panel.Contains("if (state != eGameStateName.InLevel || !_visible || !Plugin.ShowPanel.Value) return;"),
            "F8 still gates the draw exactly as before: `ShowPanel=True` now finds a place to draw at instead of a refusal");
        check(panel.Contains("HudFallbackSpot.TryResolve(preferredY, _fallbackObstacles, grid.Width, grid.Height, Screen.width,"),
            "and the conservative place is measured from the current window's own pixels, so a resize re-places it on the next poll");
    }

    // ---------- helpers ----------

    private static int Count(string text, string needle)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0) { count++; index += needle.Length; }
        return count;
    }

    private static bool Near(float a, float b) => Near(a, b, 0.01f);

    private static bool Near(float a, float b, float tolerance) => Math.Abs(a - b) <= tolerance;

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
}
