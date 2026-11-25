using System;
using System.Collections.Generic;

namespace HawChow.GTFOCombatPanel;

// Where the in-level table may stand while it is anchored to the HUD.
//
// 1.19.40 hung the table from the *anchor row*: `y = anchorRow.Bottom + 8`. The recorded expedition shows what that
// is worth on a real HUD. The row's own `PUI_InventoryIconDisplay` layout rectangle measured
// (2336,294) size 211x110 on a 2560x1440 window, so its bottom edge is 404 and the table (397x60) was placed at
// (2150,412)-(2547,472). The equipment column keeps going *below* that row - the weapon, its ammo figures, the tool,
// the resource pack and the consumable are separate rows of the same column - so the table was drawn straight over
// them. A later screenshot shows exactly that: the damage table sits on the vanilla rows under the weapon/tool names.
//
// The anchor row is therefore not the bottom of anything. What the table has to clear is the *band* every drawn row of
// that column covers, and the only honest way to know it is to measure those rows - the same HUD objects, through the
// same camera authorization and the same `ScreenBounds` gate the anchor itself goes through. `EquipmentBand` is that
// union, `HudPlacement` is the ladder the table is placed by, and `EquipmentBandWatch` is what keeps a bottom edge that
// arrives late (the HUD expands a slot when a weapon is selected, a pack is picked up, a throwable is readied) in step
// with the table without making it jitter while a row collapses and expands.
//
// Nothing here knows about `PositionY`, the screen's fractions, a per-row pixel height or a resolution scale. Those were
// the assumptions that produced the overlap; this file only ever sees rectangles the HUD really answered, and a
// placement is refused rather than guessed when it does not see them.

// One native row of the equipment column, as one attempt read it.
//
// `Drawn` is the HUD's own answer about whether the row is on screen (its `IsVisible` flag, or the anchor row itself,
// which was just measured); an object that exists and is switched off must not push the table down. `Measured` says
// whether its rectangle was really read - a row whose `RectTransform` could not be projected is *not* a row at zero,
// and the difference is what `EquipmentBandReading.Complete` reports.
public readonly struct HudObstacle
{
    public readonly string Name;
    public readonly LayoutRect Rect;
    public readonly bool Drawn;
    public readonly bool Measured;
    public readonly bool FullRow;

    public HudObstacle(string name, LayoutRect rect, bool drawn, bool measured = true, bool fullRow = true)
    {
        Name = name; Rect = rect; Drawn = drawn; Measured = measured; FullRow = fullRow;
    }

    public static HudObstacle Unread(string name, bool drawn) => new HudObstacle(name, default, drawn, measured: false);

    public string Describe()
        => Name + "=" + (Measured
            ? "(" + R(Rect.X) + "," + R(Rect.Y) + "," + R(Rect.Width) + "x" + R(Rect.Height) + ")"
            : "unread")
            + (Drawn ? string.Empty : "(hidden)");

    internal static string R(float value) => float.IsFinite(value) ? value.ToString("0.##") : "NaN";
}

// What the equipment column covers on the window, from the rows one attempt measured.
//
// The union is taken over the rows the HUD is *drawing*; a hidden row is skipped, a row that was not measured is
// recorded as a skip (so the caller can tell a complete reading from a partial one), a row that is not a rectangle
// (not a number, a pixel tall) is skipped, and a row that is partly off the window contributes only its on-screen part -
// the table is clamped into the window anyway, so nothing off the edge can ever be drawn over.
//
// This is a reading, not a placement: it says where the column is, and the caller decides what to do with it.
public readonly struct EquipmentBandReading
{
    public readonly bool Measured;
    public readonly bool Complete;
    public readonly LayoutRect Rect;
    public readonly int Used;
    public readonly int Skipped;

    public EquipmentBandReading(bool measured, bool complete, LayoutRect rect, int used, int skipped)
    {
        Measured = measured; Complete = complete; Rect = rect; Used = used; Skipped = skipped;
    }

    public static readonly EquipmentBandReading None = new EquipmentBandReading(false, false, default, 0, 0);

    public string Describe()
        => Measured
            ? "band=(" + HudObstacle.R(Rect.X) + "," + HudObstacle.R(Rect.Y) + "," + HudObstacle.R(Rect.Width) + "x"
                + HudObstacle.R(Rect.Height) + ") rows=" + Used + " skipped=" + Skipped + " complete=" + (Complete ? "yes" : "no")
            : "band=none rows=0 skipped=" + Skipped;
}

public static class EquipmentBand
{
    // The equipment column is a handful of rows: the icon display the anchor is measured from plus the slot rows the
    // inventory lists. Ten is a ceiling that cannot be reached by this HUD and that keeps the reader bounded - it never
    // walks a hierarchy, never scans the scene and never allocates anything sized by what it finds.
    public const int MaxObstacles = 12;

    public static EquipmentBandReading Read(IReadOnlyList<HudObstacle> obstacles, float screenWidth, float screenHeight)
    {
        if (obstacles == null || obstacles.Count == 0) return EquipmentBandReading.None;
        if (!float.IsFinite(screenWidth) || !float.IsFinite(screenHeight) || screenWidth <= 0f || screenHeight <= 0f)
            return EquipmentBandReading.None;
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        int used = 0, skipped = 0;
        bool complete = true;
        int count = Math.Min(obstacles.Count, MaxObstacles);
        for (int index = 0; index < count; index++)
        {
            HudObstacle row = obstacles[index];
            if (!row.Drawn) continue;
            if (!row.Measured)
            {
                // A drawn row nobody could read is the one fact that makes this band incomplete: the column has a bottom
                // edge beyond everything measured here, and a placement "below the band" would be below the wrong edge.
                skipped++;
                complete = false;
                continue;
            }
            // A text rectangle can protect those pixels, but proves nothing about the row's other edges.
            if (!row.FullRow) complete = false;
            LayoutRect rect = row.Rect;
            if (!float.IsFinite(rect.X) || !float.IsFinite(rect.Y) || !float.IsFinite(rect.Width) || !float.IsFinite(rect.Height))
            {
                skipped++; complete = false; continue;
            }
            if (!(rect.Width > 1f) || !(rect.Height > 1f)) { skipped++; continue; }
            float left = Math.Max(0f, rect.X), top = Math.Max(0f, rect.Y);
            float right = Math.Min(screenWidth, rect.Right), bottom = Math.Min(screenHeight, rect.Bottom);
            // A row that is entirely outside the window is not on the screen the table is clamped into, so it cannot be
            // drawn over and is not part of the band.
            if (right <= left || bottom <= top) { skipped++; continue; }
            if (left < x0) x0 = left;
            if (top < y0) y0 = top;
            if (right > x1) x1 = right;
            if (bottom > y1) y1 = bottom;
            used++;
        }
        if (used == 0) return new EquipmentBandReading(false, false, default, 0, skipped);
        return new EquipmentBandReading(true, complete, new LayoutRect(x0, y0, x1 - x0, y1 - y0), used, skipped);
    }

    // The smallest rectangle that covers both. Used for one row that answered more than one of its own elements: the row
    // itself and the native item name it draws are two answers about the same row, and the row is the union of them.
    public static LayoutRect Union(LayoutRect a, LayoutRect b)
    {
        float x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        return new LayoutRect(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
    }
}

// Which rung of the ladder produced a placement. Named in the log line, so a reader can tell "below the column" from
// "squeezed somewhere free because the page left no room".
public enum PanelPlacementKind { None = 0, BelowBand = 1, AboveBand = 2, BesideBand = 3, LeastOverlap = 4 }

public readonly struct PanelPlacement
{
    public readonly LayoutRect Rect;
    public readonly PanelPlacementKind Kind;
    // The area of the table that still lies on the band. Zero for every rung but `LeastOverlap`.
    public readonly float Overlap;

    public PanelPlacement(LayoutRect rect, PanelPlacementKind kind, float overlap)
    {
        Rect = rect; Kind = kind; Overlap = overlap;
    }

    public string Describe()
        => "placement=" + Name(Kind) + " table=(" + HudObstacle.R(Rect.X) + "," + HudObstacle.R(Rect.Y) + ","
            + HudObstacle.R(Rect.Width) + "x" + HudObstacle.R(Rect.Height) + ") overlap=" + HudObstacle.R(Overlap);

    public static string Name(PanelPlacementKind kind)
    {
        switch (kind)
        {
            case PanelPlacementKind.BelowBand: return "below-band";
            case PanelPlacementKind.AboveBand: return "above-band";
            case PanelPlacementKind.BesideBand: return "beside-band";
            case PanelPlacementKind.LeastOverlap: return "least-overlap";
            default: return "none";
        }
    }
}

// The ladder the anchored table is placed by.
//
// The first rung is the one the mod has always meant: right-aligned with the anchor row, one gap below the bottom of
// the equipment column. It is only taken when the *whole* column was measured (`complete`) and when the resulting
// rectangle is both on the window and clear of the band - a partial reading says nothing about the column's bottom, so
// "below the band" would be below the wrong edge.
//
// When the bottom of the window leaves no room, the same gap is used in the other three directions before anything is
// allowed to overlap: above the column, and beside it (right-aligned against the column's left edge, which is a real
// separation in x and therefore needs no vertical room at all). Only when every free rung is off the window is the
// least-overlapping on-window rung used, and it is reported as itself.
//
// 1.19.47: **every** rung of this ladder is a proof about a *fully measured* column, and `Proven` is the one place that
// says so. `below-band` already refused a partial reading (it leans on the column's bottom edge); the recorded expedition
// shows that `above-band` and `beside-band` lean on exactly as much, and that a partial reading cannot pay for it. The log
// line 2769 of that run is the fixture: the reading proved one row - `flashlight=(2336.08,294.33,210.59x110.16)` - and
// stated eleven slot rows `unread`, and the ladder answered `placement=above-band table=(2087.67,226.33,459x60)`. The
// screenshot of that same frame shows the table lying on the native equipment column: the rows nobody could project are
// drawn *above* the one row that was measured, so "above the band" was above the wrong edge, and `overlap=0` only ever
// meant "does not touch the one row that was read". A rung that is not a proof is not a placement: the caller asks
// `Proven` before it draws, and defers instead of falling back to the configured position.
//
// `TryResolve` is deliberately never handed the *anchor row*. The pre-1.19.41 placement was `anchorRow.Bottom + gap`,
// and a function that cannot compute it cannot fall back to it: the only way back to that rectangle is editing the
// caller, not a configuration value or a screen that happens to be tight.
public static class HudPlacement
{
    // The gap this mod has always put between the anchor row and the table. It is kept as it is - the fix is *what* the
    // gap is measured from, not how wide it is.
    public const float Gap = 8f;
    // A rectangle is treated as on the window when it is no further than this outside it: the projections answer in
    // floats, and half a pixel is not a placement.
    private const float EdgeTolerance = 0.5f;

    public static bool TryResolve(LayoutRect band, bool complete, float width, float height, float rightEdge, float gap,
        float screenWidth, float screenHeight, out PanelPlacement placement)
    {
        placement = default;
        if (!float.IsFinite(width) || !float.IsFinite(height) || !(width > 0f) || !(height > 0f)) return false;
        if (!float.IsFinite(screenWidth) || !float.IsFinite(screenHeight) || screenWidth <= 0f || screenHeight <= 0f) return false;
        // A table larger than the window is on no rung of this ladder: clamping it would only move a rectangle that still
        // runs off the edge, and the caller's configured position is a better answer than that.
        if (width > screenWidth || height > screenHeight) return false;
        if (!float.IsFinite(gap) || gap < 0f) gap = Gap;
        if (!float.IsFinite(rightEdge)) rightEdge = screenWidth;
        float x = rightEdge - width;

        // 1. Below the column: the one the table is meant to take.
        if (complete)
        {
            var below = new LayoutRect(x, band.Bottom + gap, width, height);
            if (OnWindow(below, screenWidth, screenHeight) && Clear(below, band, gap))
            {
                placement = new PanelPlacement(below, PanelPlacementKind.BelowBand, 0f);
                return true;
            }
        }
        // 2. Above the column, same edge.
        {
            var above = new LayoutRect(x, band.Y - gap - height, width, height);
            if (OnWindow(above, screenWidth, screenHeight) && Clear(above, band, gap))
            {
                placement = new PanelPlacement(above, PanelPlacementKind.AboveBand, 0f);
                return true;
            }
        }
        // 3. Beside the column: right-aligned against its left edge, at its top and at its bottom. Both are clear by
        //    construction (the table's own right edge is one gap left of the column's), so a page with no vertical room
        //    at all still has a proven place.
        float besideX = band.X - gap - width;
        if (TryBeside(besideX, band.Y, width, height, band, gap, screenWidth, screenHeight, out placement)) return true;
        if (TryBeside(besideX, band.Bottom - height, width, height, band, gap, screenWidth, screenHeight, out placement)) return true;
        // 4. Nothing free and on the window. The three rungs are clamped into the window and the least-overlapping one is
        //    taken - the ladder's own answer, printed with the area it still covers, never the pre-1.19.41 rectangle.
        float best = float.MaxValue;
        PanelPlacement chosen = default;
        Consider(new LayoutRect(x, band.Bottom + gap, width, height), band, screenWidth, screenHeight, ref best, ref chosen);
        Consider(new LayoutRect(x, band.Y - gap - height, width, height), band, screenWidth, screenHeight, ref best, ref chosen);
        Consider(new LayoutRect(besideX, band.Y, width, height), band, screenWidth, screenHeight, ref best, ref chosen);
        if (chosen.Kind == PanelPlacementKind.None) return false;
        // A table whose every rung covers it completely is not a placement: the caller keeps the configured position,
        // which is the player's own answer to a HUD this build cannot prove anything about.
        if (chosen.Overlap >= width * height) return false;
        placement = chosen;
        return true;
    }

    // Whether the rung a placement was resolved by is a *proof* for the reading it was resolved from.
    //
    // Only a fully measured column can pay for a rung: `below-band` needs the column's bottom edge, `above-band` and
    // `beside-band` need its top/left edge, and `least-overlap` admits it covers part of the column at all. A partial
    // reading has measured *some* rows and has stated the rest as unread - which is the same thing as saying "there are
    // drawn rows whose rectangles this build does not know", and a rectangle nobody knows can be anywhere, including
    // where the rung just put the table. So `Proven` answers false for every kind of rung on a partial reading, and the
    // caller answers that with a deferral (the vanilla page is left alone for that poll), never with the configured
    // position: that position is inside the column on the recorded pages, and it is exactly the overlap this ladder
    // exists to avoid, arrived at through the fallback instead.
    public static bool Proven(PanelPlacement placement, bool complete)
        => complete && placement.Kind != PanelPlacementKind.None;

    // One "beside the column" rung: right-aligned against the column's left edge, at the requested height, clamped into
    // the window. A null answer means this rung was off the window or did not clear the band, not that it was not tried.
    private static bool TryBeside(float x, float y, float width, float height, LayoutRect band, float gap,
        float screenWidth, float screenHeight, out PanelPlacement placement)
    {
        placement = default;
        var beside = new LayoutRect(x, Clamp(y, 0f, screenHeight - height), width, height);
        if (!OnWindow(beside, screenWidth, screenHeight) || !Clear(beside, band, gap)) return false;
        placement = new PanelPlacement(beside, PanelPlacementKind.BesideBand, 0f);
        return true;
    }

    // The last rung's own arithmetic: clamp the candidate into the window and keep the one that covers the least of the
    // band. Ties keep the earlier rung, so the answer is stable across frames.
    private static void Consider(LayoutRect candidate, LayoutRect band, float screenWidth, float screenHeight,
        ref float best, ref PanelPlacement chosen)
    {
        LayoutRect clamped = ClampInto(candidate, screenWidth, screenHeight);
        float area = Area(clamped, band);
        if (!float.IsFinite(area) || area >= best) return;
        best = area;
        chosen = new PanelPlacement(clamped, PanelPlacementKind.LeastOverlap, area);
    }

    // A rectangle is on the window when every one of its four edges is, within `EdgeTolerance`: a table wider than the
    // window is never "on" it, and a half pixel outside is.

    // Whether a rectangle keeps the gap to the band: it has to be separated on one axis by at least `gap`. Separating on
    // either axis is enough - a table beside the column cannot be drawn over it however tall it is.
    public static bool OnWindow(LayoutRect rect, float screenWidth, float screenHeight)
        => float.IsFinite(rect.X) && float.IsFinite(rect.Y)
            && rect.X >= -EdgeTolerance && rect.Y >= -EdgeTolerance
            && rect.Right <= screenWidth + EdgeTolerance && rect.Bottom <= screenHeight + EdgeTolerance;

    public static bool Clear(LayoutRect rect, LayoutRect band, float gap)
        => rect.Y >= band.Bottom + gap || rect.Bottom <= band.Y - gap
            || rect.X >= band.Right + gap || rect.Right <= band.X - gap;

    // How much of `rect` lies on `band`.
    public static float Area(LayoutRect rect, LayoutRect band)
    {
        float w = Math.Min(rect.Right, band.Right) - Math.Max(rect.X, band.X);
        float h = Math.Min(rect.Bottom, band.Bottom) - Math.Max(rect.Y, band.Y);
        return w > 0f && h > 0f ? w * h : 0f;
    }

    public static LayoutRect ClampInto(LayoutRect rect, float screenWidth, float screenHeight)
    {
        float x = Clamp(rect.X, 0f, Math.Max(0f, screenWidth - rect.Width));
        float y = Clamp(rect.Y, 0f, Math.Max(0f, screenHeight - rect.Height));
        return new LayoutRect(x, y, rect.Width, rect.Height);
    }

    private static float Clamp(float value, float low, float high)
        => !float.IsFinite(value) ? low : (value < low ? low : (value > high ? high : value));
}

// Keeps the avoidance band in step with a column that grows and collapses under the player's hands.
//
// Two requirements pull in opposite directions. A bottom edge that arrives late - a weapon selected (the HUD expands
// that row), a resource pack picked up, a throwable readied - has to be honoured at once, because until it is the table
// is drawn over the very row that just appeared. A column that collapses and re-expands must not drag the table up and
// down with it. The asymmetry answers both:
//
//   * A band that *grows* (its bottom edge moves down, or its left/right edge widens) is committed on the reading that
//     reports it. Moving the table further from the column can never create an overlap, so there is nothing to stabilise.
//     The committed band is the union of the old one and the new one, so growing never loses an edge either.
//   * A band that *shrinks* is only committed once `ShrinkSamples` consecutive readings agree within `Hysteresis`. A row
//     that flickers therefore never moves the table; a row that is really gone moves it back once, after it is gone.
//   * A reading within `Hysteresis` of the committed one is not a change at all. Two pixels is the tolerance the
//     anchor's own settle window uses for the same reason: a projection of the same rectangle answers the same numbers
//     to the pixel, and the HUD's own layout animation is what moves a row by more.
public sealed class EquipmentBandWatch
{
    public const float Hysteresis = 2f;
    public const int ShrinkSamples = 2;

    private bool _has;
    private LayoutRect _committed;
    private bool _complete;
    private LayoutRect _candidate;
    private int _agree;

    public bool Has => _has;
    public LayoutRect Committed => _committed;
    public bool Complete => _complete;

    public void Restart()
    {
        _has = false;
        _committed = default;
        _complete = false;
        _candidate = default;
        _agree = 0;
    }

    // One reading. Answers whether the committed *geometry* changed, and never deletes a measured band for a reading that
    // could not be measured: a HUD that is a moment late is not evidence that the column shrank.
    //
    // 1.19.45: the geometry and the *completeness* are two different facts and this method now treats them as such.
    //
    //   * The geometry is what the hysteresis is for: a row that flickers open and shut must not drag the table up and
    //     down, so a shrink waits for `ShrinkSamples` agreeing readings and a reading nobody could measure changes
    //     nothing at all (the committed rectangle is kept - "unreadable this poll" is not "the column is not there").
    //   * The completeness is never tolerated: it is the promise that the band's *bottom edge* is proven, which is the
    //     one thing the `below-band` rung leans on. A reading that carries a drawn row nobody could project - or that
    //     could not be measured at all - has just stopped proving that edge, so `Complete` drops on that very reading,
    //     whether or not the geometry moves. It comes back on the first reading that proves the whole column again.
    //
    // The recorded failure this replaces: a complete band plus one newly unreadable row whose projected rectangle happened
    // to stay inside the two-pixel tolerance. This branch used to only ever *raise* `_complete`, so the table was placed
    // "below the band" - under a bottom edge the same reading had just stopped proving - while the row it could not read
    // sat somewhere below that edge.
    public bool Observe(EquipmentBandReading reading)
    {
        if (!reading.Measured)
        {
            // Nothing was measured this poll. The committed rectangle stays (see above), but the bottom edge is not
            // proven by a poll that measured nothing, so `below-band` is refused until a reading proves it again.
            _complete = false;
            return false;
        }
        LayoutRect measured = reading.Rect;
        if (!_has)
        {
            _has = true;
            _committed = measured;
            _complete = reading.Complete;
            _candidate = default;
            _agree = 0;
            return true;
        }
        if (Grew(measured, _committed))
        {
            _committed = Union(_committed, measured);
            _complete = _complete && reading.Complete;
            _candidate = default;
            _agree = 0;
            return true;
        }
        if (!Shrank(measured, _committed))
        {
            // Within the hysteresis: the same band, told with the same numbers. The geometry is therefore not a change -
            // and the completeness is not part of that tolerance. This reading is the whole truth about the committed
            // rectangle: a complete one proves its bottom edge (as it always could), an incomplete one stops proving it on
            // this very frame. One assignment for both directions, so the branch can no longer be a one-way ratchet -
            // which is exactly the defect that placed a table "below the band" while the row it could not read sat below
            // that edge.
            _complete = reading.Complete;
            _candidate = default;
            _agree = 0;
            return false;
        }
        // A collapse needs agreement before it moves the table, so the committed geometry above is untouched - but the
        // column this poll read is already the one that is there, and if it is not fully proven the band is not either.
        // Waiting for the second agreeing reading is a rule about the *rectangle*, never about the completeness.
        _complete = _complete && reading.Complete;
        if (_agree > 0 && Within(_candidate, measured))
        {
            _agree++;
        }
        else
        {
            _candidate = measured;
            _agree = 1;
        }
        if (_agree < ShrinkSamples) return false;
        _committed = measured;
        _complete = reading.Complete;
        _candidate = default;
        _agree = 0;
        return true;
    }

    private static bool Grew(LayoutRect measured, LayoutRect committed)
        => measured.Bottom > committed.Bottom + Hysteresis
            || measured.X < committed.X - Hysteresis
            || measured.Right > committed.Right + Hysteresis;

    private static bool Shrank(LayoutRect measured, LayoutRect committed)
        => measured.Bottom < committed.Bottom - Hysteresis
            || measured.X > committed.X + Hysteresis
            || measured.Right < committed.Right - Hysteresis;

    private static bool Within(LayoutRect a, LayoutRect b)
        => Math.Abs(a.X - b.X) <= Hysteresis && Math.Abs(a.Y - b.Y) <= Hysteresis
            && Math.Abs(a.Width - b.Width) <= Hysteresis && Math.Abs(a.Height - b.Height) <= Hysteresis;

    private static LayoutRect Union(LayoutRect a, LayoutRect b)
    {
        float x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        return new LayoutRect(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
    }
}

// Whether the configured (manual) position may still be drawn once the equipment column has been measured.
//
// Three silences that look alike and are not: "this HUD was never measured", "this HUD was measured and there is no free
// place beside it", and "this HUD was measured *partly*". The first has no better answer than the player's own
// `PositionX/PositionY` - that is exactly what the manual mode uses, and that path is untouched. The second does: the
// configured position lands inside the right-hand column on the recorded pages, so drawing it there is the same overlap
// the anchor placement was moved out of, arrived at through the fallback instead of through the anchor. The third is the
// same answer for a different reason: a column this build could only partly project has drawn rows whose rectangles are
// unknown, so "the configured position does not touch the band" is a statement about the rows that *were* read and
// nothing at all about the ones that were not. Such a frame is *deferred* - the table is not drawn, the vanilla page is
// left exactly as the game drew it, and the next poll asks again - with one line per distinct rectangle, so a deferred
// table is never silent about why.
//
// The deferral is bounded by nothing but the HUD: the column is re-read on the panel's own 0.25 s poll while the anchor
// stays locked, so the first reading that proves the whole column brings the table back by itself.
//
// This is the decision only; the caller owns the deferral and its log line.
public static class HudFallback
{
    // The reading-level answer: given a measured column, does the configured rectangle cover any of it?
    //
    // 1.19.47: this form says nothing about *completeness*, so it answers only the question it is given - the overlap
    // against the rectangle that was measured. The panel uses the completeness-aware form below on every anchored poll;
    // this one stays for the reading-level replays that compare a rectangle with a band.
    public static bool Clear(LayoutRect fallback, bool columnMeasured, LayoutRect band, out float overlap)
        => Clear(fallback, columnMeasured, columnMeasured, band, out overlap);

    // The production answer. A column that was measured but not *completely* proves nothing about the configured
    // position, so it is refused before the area is even asked: `overlap` stays 0, and the caller's line says the reason
    // is the completeness rather than an overlap it cannot see.
    public static bool Clear(LayoutRect fallback, bool columnMeasured, bool columnComplete, LayoutRect band, out float overlap)
    {
        overlap = 0f;
        if (!columnMeasured) return true;                 // no measurement: the configured position is all anybody has
        if (!columnComplete) return false;                // a partial column leaves rows nobody can place against
        if (!float.IsFinite(fallback.Width) || !float.IsFinite(fallback.Height)) return true;
        overlap = HudPlacement.Area(fallback, band);
        return !(overlap > 0f);
    }
}

// The conservative landing spot a page with no *proof* still gets (1.19.48), at the window's RIGHT edge (1.19.49).
//
// The ladder above answers only with proofs and that does not change: a rung measured from an edge a partial reading never
// proved is exactly the overlap 1.19.47 removed, and nothing here makes one cheaper. What 1.19.48 changed is the answer
// to "no proof". It used to be "draw nothing", and the recorded pages show what that costs: the anchor row measured
// (2336,294,211x110) on a 2560x1440 window while every slot row came back `unread`, so the band was partial and a 459x120
// table was refused on every poll for the rest of the level (log 2776/2777); and after a 1920x1080 -> 2560x1440 change,
// where the HUD's own canvas texture stayed 1080, the anchor round spent its twelve attempts and the table was refused for
// the whole level as well. Both frames drew nothing at the one moment the player was looking at the board, and the data is
// the reason this mod exists.
//
// So a refused poll is answered with a place that claims nothing instead:
//
//   * inside the window with `Margin` to spare - a margin is not a proof, it is the promise that the table is really on the
//     screen and not hanging over an edge;
//   * clear of every rectangle this poll *did* measure, one row at a time and never their union (the union of a column with
//     a hole in it covers the hole as well), plus the band this round committed when that band belongs to this window;
//   * at the window's RIGHT edge (1.19.49) - never the configured position, and on the side the equipment column itself is
//     drawn on. This is the half the first review got wrong in the other direction: 1.19.48 answered the LEFT edge, and the
//     recorded 2026-11-23 frame is what that cost. The column was read at x=2336..2546 on a 2560 px window and the table
//     was drawn at (24,526,459x60) - the opposite side of the screen, about 1,850 px from the column it stands in for. A
//     proven rung is right-aligned with that column (`HudPlacement` resolves it from `_anchorRect.Right`), and the widths
//     the recorded round reported (459/479/471/482 px, one per content change) all move the *left* edge while this one
//     stays where it is: the right margin is the only edge a conservative place can keep and still be where the table
//     belongs;
//   * never a claim about the anchor: this reads no `_anchorRect`, no camera and no attempt budget, so it can neither prove
//     nor disprove anything. The caller's line says `conservative=`, names the candidate it used and, for the last
//     candidate, the area it still covers. A right-edge candidate may lie where the rows this build could not project are
//     drawn - that is the reading's own missing fact, never something measured here - so nothing in this class calls the
//     place safe, and a candidate that reaches a measured row is only ever reported with that area beside it.
//
// `TryResolve` answers false only for a table that cannot be inside the window at all (wider or taller than it). That is a
// property of the table and not of the HUD, and it is the one case in which the caller still draws nothing.
public static class HudFallbackSpot
{
    // The inset the conservative place keeps from the window's edges.
    public const float Margin = 24f;
    // The same gap the ladder keeps from the column, measured from a row this poll really read.
    public const float Gap = HudPlacement.Gap;
    // How many rows the right-edge scan tries before it takes the least-overlapping candidate: bounded by construction, and
    // nothing here allocates.
    public const int ScanRows = 16;

    // The margin a table of this size really keeps on this window: `Margin`, capped at half of the free width and half of the
    // free height, so a table that only just fits keeps none and a narrow window keeps what it can. This is the only
    // calculation of it - `TryResolve` places with it and `PanelBehaviour.ReportFallbackSpot` prints it (1.19.50). The
    // recorded round printed the constant 24 while a 1400 px table on a 1440 px window kept 20, so the line described a
    // margin the table never had, and the two numbers could drift apart with nothing to catch it.
    public static float EffectiveMargin(float width, float height, float screenWidth, float screenHeight)
    {
        if (!float.IsFinite(width) || !float.IsFinite(height) || !float.IsFinite(screenWidth) || !float.IsFinite(screenHeight))
            return 0f;
        float margin = Math.Min(Margin, Math.Min((screenWidth - width) * 0.5f, (screenHeight - height) * 0.5f));
        return margin > 0f ? margin : 0f;
    }
    // The place, given only the row the player's own position would have used. Every candidate is right-aligned to the
    // window's own right margin (1.19.49): the side the equipment column is drawn on and the edge a proven rung keeps
    // (`_anchorRect.Right`). There is deliberately no left-hand candidate, so a conservative place can never put the table
    // on the far side of the screen from the column it is standing in for.
    public static bool TryResolve(float preferredY, IReadOnlyList<LayoutRect> measured, float width, float height,
        float screenWidth, float screenHeight, out LayoutRect rect, out string reason, out float overlap)
    {
        rect = default;
        reason = "none";
        overlap = 0f;
        if (!float.IsFinite(width) || !float.IsFinite(height) || !(width > 0f) || !(height > 0f)) return false;
        if (!float.IsFinite(screenWidth) || !float.IsFinite(screenHeight) || screenWidth <= 0f || screenHeight <= 0f) return false;
        // A table bigger than the window is on no place at all: clamping it only moves a rectangle that still runs off the
        // edge, and that is the caller's one remaining reason to draw nothing.
        if (width > screenWidth || height > screenHeight) return false;
        // The margin is only spent where the window can afford it, and it is the *one* calculation of what this page really
        // keeps: `EffectiveMargin` is what the caller's line prints too (1.19.50), so the log can never state a margin the
        // table did not get.
        float margin = EffectiveMargin(width, height, screenWidth, screenHeight);
        // The left coordinate of a right-aligned candidate: the one edge that does not move when the table's width grows,
        // exactly as a proven rung keeps it. Never negative, because `margin` is capped at half the free width above.
        float left = Math.Max(0f, screenWidth - width - margin), top = margin;
        float maxY = Math.Max(top, screenHeight - height - margin);

        // 1. The player's own row, at the window's right edge: the table keeps the height the player chose, and the x is the
        //    edge a proven rung keeps.
        var besideRow = new LayoutRect(left, Clamp(preferredY, top, maxY), width, height);
        if (ClearOf(besideRow, measured)) { rect = besideRow; reason = "right-row"; return true; }
        float bottom = BottomOf(measured), measuredTop = TopOf(measured);
        // 2. Below everything measured, still right-aligned - the ladder's own "below the band", measured from the lowest
        //    rectangle this poll really read instead of from an edge it did not.
        if (float.IsFinite(bottom))
        {
            var below = new LayoutRect(left, Clamp(bottom + Gap, top, maxY), width, height);
            if (ClearOf(below, measured)) { rect = below; reason = "below-measured"; return true; }
        }
        // 3. Above everything measured: a page whose foot is busy still has its upper half free, which is where the table
        //    used to hang before 1.19.41.
        if (float.IsFinite(measuredTop))
        {
            var above = new LayoutRect(left, Clamp(measuredTop - Gap - height, top, maxY), width, height);
            if (ClearOf(above, measured)) { rect = above; reason = "above-measured"; return true; }
        }
        // 4. A bounded scan down the right edge, one table height at a time: that strip is the column's own side of the
        //    window, so it is tried to its end before anything is allowed to touch a measured row.
        float step = height + Gap;
        for (int scan = 0; scan < ScanRows; scan++)
        {
            float y = top + scan * step;
            if (y > maxY) break;
            var candidate = new LayoutRect(left, y, width, height);
            if (ClearOf(candidate, measured)) { rect = candidate; reason = "right-column"; return true; }
        }
        // 5. Nothing on the right edge is free either. The candidate there that covers the least of what was measured stands,
        //    reported with the area it still covers - never as a rung, never as a proof and never as "safe". A right-aligned
        //    candidate can lie where the unread rows are drawn, which is why the caller's line prints that area: the reading
        //    never claimed those rows, and these candidates never pretend the place is clear of them.
        float best = float.MaxValue;
        LayoutRect chosen = default;
        Consider(besideRow, measured, ref best, ref chosen);
        if (float.IsFinite(bottom)) Consider(new LayoutRect(left, Clamp(bottom + Gap, top, maxY), width, height), measured, ref best, ref chosen);
        if (float.IsFinite(measuredTop))
            Consider(new LayoutRect(left, Clamp(measuredTop - Gap - height, top, maxY), width, height), measured, ref best, ref chosen);
        for (int scan = 0; scan < ScanRows; scan++)
        {
            float y = top + scan * step;
            if (y > maxY) break;
            Consider(new LayoutRect(left, y, width, height), measured, ref best, ref chosen);
        }
        if (best == float.MaxValue) return false;
        rect = chosen;
        overlap = best;
        reason = "least-overlap";
        return true;
    }

    // Whether the rectangle touches none of the rectangles this poll really measured. A row that is not a rectangle (not a
    // number, a pixel tall) is refused by the same `Usable` test the band itself uses, so it neither blocks a place nor
    // pretends to be one.
    private static bool ClearOf(LayoutRect rect, IReadOnlyList<LayoutRect> measured)
    {
        if (measured == null) return true;
        for (int index = 0; index < measured.Count; index++)
        {
            LayoutRect other = measured[index];
            if (!Usable(other)) continue;
            if (HudPlacement.Area(rect, other) > 0f) return false;
        }
        return true;
    }

    private static float Coverage(LayoutRect rect, IReadOnlyList<LayoutRect> measured)
    {
        if (measured == null) return 0f;
        float total = 0f;
        for (int index = 0; index < measured.Count; index++)
        {
            LayoutRect other = measured[index];
            if (!Usable(other)) continue;
            total += HudPlacement.Area(rect, other);
        }
        return total;
    }

    // Tie-safe by construction: `>=` keeps the earlier candidate, so the same page answers the same rectangle on every poll
    // and a repaint can never make the table flicker between two equally good places.
    private static void Consider(LayoutRect candidate, IReadOnlyList<LayoutRect> measured, ref float best, ref LayoutRect chosen)
    {
        float area = Coverage(candidate, measured);
        if (!float.IsFinite(area) || area >= best) return;
        best = area;
        chosen = candidate;
    }

    private static float TopOf(IReadOnlyList<LayoutRect> measured)
    {
        float top = float.NaN;
        if (measured == null) return top;
        for (int index = 0; index < measured.Count; index++)
        {
            LayoutRect row = measured[index];
            if (!Usable(row)) continue;
            if (!float.IsFinite(top) || row.Y < top) top = row.Y;
        }
        return top;
    }

    private static float BottomOf(IReadOnlyList<LayoutRect> measured)
    {
        float bottom = float.NaN;
        if (measured == null) return bottom;
        for (int index = 0; index < measured.Count; index++)
        {
            LayoutRect row = measured[index];
            if (!Usable(row)) continue;
            if (!float.IsFinite(bottom) || row.Bottom > bottom) bottom = row.Bottom;
        }
        return bottom;
    }

    private static bool Usable(LayoutRect rect)
        => float.IsFinite(rect.X) && float.IsFinite(rect.Y) && float.IsFinite(rect.Width) && float.IsFinite(rect.Height)
           && rect.Width > 1f && rect.Height > 1f;

    private static float Clamp(float value, float low, float high)
        => !float.IsFinite(value) ? low : (value < low ? low : (value > high ? high : value));
}
