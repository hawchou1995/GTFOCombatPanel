using System;

namespace HawChow.GTFOCombatPanel;

// ---------------------------------------------------------------------------------------------------
// ResultsScroll.cs - the settlement board is a scrolling table, not a zoomable picture
// ---------------------------------------------------------------------------------------------------
//
// The settlement table has a fixed number of rows: a header row carrying the four player names, then one
// row per metric, then a footer row. On a page that offers less height than those rows need, the old build
// spent the shortage on the glyph size - `FitBoardIntoStrip` walked the size down until the whole table fit
// - which is how a thirty-row board could be drawn at 6 px on a page the player was still expected to read.
// The floor that was supposed to stop it (`ResultsBandFit.FloorForBand`) lowered itself to the band's own
// answer, so a short band was answered with a small table rather than with anything the player could scroll.
//
// This file is the rule that replaces that trade, and it is free of Unity and of BepInEx so the whole of it
// is asserted headlessly in tests/GTFOCombatPanel.Smoke:
//
//   frozen      the first row - the column header that carries the four player names - never moves. It is
//               drawn at the top of the plate on every frame, whatever the scroll offset is.
//   readable    the fit is the answer. `FontFor` hands back the size the page's own strip really holds - the
//               largest one that puts the whole table inside the room the page measured - and never a size
//               below the absolute readable minimum. It does not raise that answer back up to the comfort
//               size the button's height would have liked, because that is exactly how a board that had
//               already been fitted to all thirty rows came back at 16 px and showed twenty-four of them
//               (1.19.45). A table the fit cannot hold - a genuinely short strip, or a player's explicit
//               multiplier - is scrolled, never shrunk.
//   window      `VisibleRows` is how many rows the offered room really holds; the plate the board occupies
//               is exactly that many rows tall, so nothing is drawn below the plate and nothing reaches the
//               button underneath it.
//   reach       `First` moves in whole rows by the wheel, is clamped to `[FrozenRows, MaximumFirst]`, and
//               `MaximumFirst` puts the table's own last row in the plate's last slot: every row is reachable
//               from both ends and the board cannot be scrolled past its own foot.
//   one notch   `ResultScrollLatch` is the same-frame guard: IMGUI can hand the same page several events in
//               one Unity frame, and a gesture that is delivered twice must not move the table twice.

// One arrangement of the table inside its plate: which rows are frozen, how many are visible, and which
// scrollable row is the first one on screen.
public readonly struct ScrollViewport
{
    public readonly int Rows, Frozen, Visible, First;

    public ScrollViewport(int rows, int frozen, int visible, int first)
    {
        Rows = rows < 0 ? 0 : rows;
        Frozen = frozen < 0 ? 0 : frozen > Rows ? Rows : frozen;
        Visible = visible < 1 ? 1 : visible > Rows ? Rows : visible;
        First = ResultsScroll.Clamp(first, Rows, Frozen, Math.Max(1, Visible - Frozen));
    }

    // The rows under the frozen one that the plate can show at once.
    public int ScrollSlots { get { return Math.Max(1, Visible - Frozen); } }
    // The rows that scroll at all.
    public int Scrollable { get { return Math.Max(0, Rows - Frozen); } }
    // Whether anything is off screen.
    public bool Overflows { get { return Scrollable > ScrollSlots; } }
    // The largest first scrollable row: the one that puts the table's last row in the plate's last slot.
    public int MaximumFirst { get { return ResultsScroll.MaximumFirst(Rows, Frozen, ScrollSlots); } }
    // Whether the foot of the table is on screen.
    public bool AtEnd { get { return First >= MaximumFirst; } }
    // Whether the table stands at its own head.
    public bool AtStart { get { return First <= ResultsScroll.FrozenRows; } }
    // The first absolute row the plate draws.
    public int TopRow { get { return Frozen > 0 ? Frozen : First; } }
    // The absolute row a visible slot shows: the frozen row keeps slot 0, the rest scroll.
    public int RowAt(int slot)
    {
        if (slot < 0) return -1;
        if (slot < Frozen) return slot;
        int row = First + (slot - Frozen);
        return row < Rows ? row : -1;
    }

    public ScrollViewport At(int first) { return new ScrollViewport(Rows, Frozen, Visible, first); }
}

public static class ResultsScroll
{
    // The column-header row - one cell per player, the four names the board is read by - is pinned.
    public const int FrozenRows = 1;
    // A plate of one row would be a header with nothing under it; two is the smallest board that still reads
    // as a table, and it is also the floor `VisibleRows` answers for a page that offers almost nothing.
    public const int MinimumVisibleRows = 2;
    // One wheel notch moves the table by one row. A row is the unit the player reads, so a notch that moved
    // half a row would leave two rows half-visible and the gesture would show less than it moved.
    public const int WheelRowsPerNotch = 1;
    // A trackpad can report a fraction of a notch; the direction is what the player meant. The dead zone is
    // what keeps a resting trackpad from scrolling.
    public const float WheelDeadZone = 0.0001f;
    // IMGUI reports one notch *away* from the player - scrolling down, towards the table's foot - as a
    // positive `Event.current.delta.y`. `Direction` reads that convention and it lives here alone.
    public const bool WheelUpIsNegative = true;

    public static bool Accepts(float wheelDelta) => float.IsFinite(wheelDelta) && Math.Abs(wheelDelta) > WheelDeadZone;

    // +1 for a notch towards the table's head, -1 for a notch towards its foot, 0 for a wheel that did not move.
    public static int Direction(float wheelDelta)
    {
        if (!Accepts(wheelDelta)) return 0;
        bool up = WheelUpIsNegative ? wheelDelta < 0f : wheelDelta > 0f;
        return up ? 1 : -1;
    }

    // How many rows the offered room really holds. The plate is this many rows tall and never more, which is
    // what keeps the board off the button underneath it however tall the table itself is.
    //
    // 1.19.59: the answer is `floor(room / rowHeight)` and never a fixed two. 1.19.58 answered two for any room
    // under two rows, and `PlateHeight` then clipped the plate to the room - so a 12px offer was reported as
    // "2 rows" and drawn as one 12px slot with 3px of the second row showing. The count a caller reads here is the
    // count the plate really holds (`PlateHeight` is its own height in whole rows), and a room that cannot hold
    // `MinimumVisibleRows` is refused by `PlateFits` at the placement instead of being drawn as a broken row.
    public static int VisibleRows(float room, float rowHeight)
    {
        if (!float.IsFinite(room) || room <= 0f) return MinimumVisibleRows;
        if (!float.IsFinite(rowHeight) || rowHeight <= 0f) return MinimumVisibleRows;
        int rows = (int)Math.Floor(room / rowHeight + 0.0001f);
        return rows < 1 ? 1 : rows;
    }

    // The plate's own height in pixels: whole rows only, so a row is never cut in half at the plate's foot and
    // the table cannot bleed past the strip it was measured into.
    //
    // 1.19.59: a plate is whole rows or its caller refuses it (`PlateFits`), never a partial row. The room clips
    // the *count*, not the height of a row: `VisibleRows(room, rowHeight) * rowHeight` is therefore this answer,
    // which is what lets `_scrollVisible` describe exactly the rows that are on the page.
    public static float PlateHeight(int visibleRows, float rowHeight, float room)
    {
        if (!float.IsFinite(rowHeight) || rowHeight <= 0f) return 0f;
        int rows = visibleRows < 1 ? 1 : visibleRows;
        if (float.IsFinite(room) && room > 0f)
        {
            int fits = (int)Math.Floor(room / rowHeight + 0.0001f);
            if (fits < 1) fits = 1;
            if (rows > fits) rows = fits;
        }
        return rows * rowHeight;
    }

    // The room the rows themselves may use, once the page's own header band is reserved above them.
    //
    // The header is drawn *above* the rows (`ResultChromeLayout.BoardOf`), so a page that offers `room` pixels can only
    // give `room - header` to the table. Spending the header's height on rows is what put a fixed-mode plate with its
    // close button above the top of the screen: the plate fitted, the board - header included - did not.
    public static float RowRoom(float room, float header)
        => room - (float.IsFinite(header) && header > 0f ? header : 0f);

    // Whether a plate of `minimumRows` rows *plus* its header fits in the offered room at all. A page that cannot hold
    // the header and two readable rows has nothing to draw and must say so, rather than place a plate whose header (and
    // its close button) is off the screen.
    public static bool PlateFits(float room, float rowHeight, float header, int minimumRows)
    {
        if (!float.IsFinite(room) || !float.IsFinite(rowHeight) || rowHeight <= 0f) return false;
        int rows = minimumRows < 1 ? 1 : minimumRows;
        return RowRoom(room, header) + 0.5f >= rows * rowHeight;
    }

    // What a placement reset does with the player's scroll position. The offset belongs to the page the player is
    // reading: a re-measure of that same page - a poll that rebuilt the board, a refresh, a resolution change - keeps
    // what the player scrolled to (clamped to the viewport the new measurement produced), while a different page
    // starts at its own head. Zeroing it on every re-measure snapped the table back to the top for no reason.
    public static int OffsetAfterReset(int first, bool samePage, int rows, int frozen, int slots)
        => samePage ? Clamp(first, rows, frozen, slots) : (frozen < 0 ? 0 : frozen);

    // The largest first scrollable row. `frozen` rows are pinned, `slots` rows scroll on screen, and the last
    // first row is the one that still shows the table's own foot in the last slot.
    public static int MaximumFirst(int rows, int frozen, int slots)
    {
        if (rows < 0) rows = 0;
        if (frozen < 0) frozen = 0;
        if (slots < 1) slots = 1;
        int maximum = rows - slots;
        return maximum < frozen ? frozen : maximum;
    }

    public static int Clamp(int first, int rows, int frozen, int slots)
    {
        int minimum = frozen < 0 ? 0 : frozen;
        int maximum = MaximumFirst(rows, frozen, slots);
        if (!float.IsFinite(first)) return minimum;
        if (first < minimum) return minimum;
        if (first > maximum) return maximum;
        return first;
    }

    // One wheel notch: the table moves by whole rows, and a wheel held against either end keeps the end rather
    // than wrapping round it.
    public static int Next(int first, int rows, int frozen, int slots, float wheelDelta)
    {
        int settled = Clamp(first, rows, frozen, slots);
        int direction = Direction(wheelDelta);
        if (direction == 0) return settled;
        // A notch towards the head moves the first visible row back towards the frozen row; a notch towards the
        // foot moves it forward. Both are clamped, so neither can leave the table.
        return Clamp(settled - direction * WheelRowsPerNotch, rows, frozen, slots);
    }

    // The same step, for a router that answers whole rows at once (1.19.59): `step` positive walks towards the table's
    // foot, and the answer is clamped exactly like `Next`, so a frame that asks for five rows at the foot moves what is
    // left and no more. It exists so the wheel can move up to five notches in one frame without looping a one-row step.
    public static int ByRows(int first, int rows, int frozen, int slots, int step)
    {
        int minimum = frozen < 0 ? 0 : frozen;
        int settled = Clamp(first, rows, frozen, slots);
        if (step == 0) return settled;
        long moved = (long)settled + step;
        if (moved < minimum) return minimum;
        if (moved > int.MaxValue) moved = int.MaxValue;
        return Clamp((int)moved, rows, frozen, slots);
    }

    // The glyph size the board is really drawn at.
    //
    // `fitted` is the largest size the page's own strip really holds: it is the answer of the band walk in
    // `ResultsDisplay.FitBoard`, and it is the only figure in this mod that has measured the table against the
    // room the page offered. `manualScale` is the player's own multiplier from `[Results] SuccessFontScale` /
    // `FailureFontScale` - a preference, applied to the fitted size, and clamped so that it can never be the
    // thing that made the table unreadable. It is *not* a wheel value any more: the wheel scrolls, and this
    // figure is only ever read from the file.
    //
    // 1.19.45: this used to take `readable` - the floor derived from the height of the button under the strip -
    // as a floor it had to raise the answer to. On the recorded failure page that floor was 16 px while the
    // strip's own arithmetic allowed 13 px for all thirty rows, so the fitted board was handed back at 16 px:
    // rows 24/30 on screen, and the six metrics a player actually reads (hit rate, supplies, tool efficiency,
    // solo survival, deaths by blood loss, friendly-fire downs) behind a wheel nothing on the page mentioned.
    // The fit is the bound that measured something, so it is the answer; the comfort size is only reached when
    // the strip can hold it, and that is `FitBoard`'s business, not this one's.
    public static int FontFor(int fitted, float manualScale)
    {
        int natural = fitted < ResultsFontGrowth.MinimumReadablePixels
            ? ResultsFontGrowth.MinimumReadablePixels : fitted;
        float scale = float.IsFinite(manualScale) && manualScale > 0f ? manualScale : 1f;
        int scaled = (int)Math.Round(natural * (double)scale, MidpointRounding.AwayFromZero);
        if (scaled < ResultsFontGrowth.MinimumReadablePixels) scaled = ResultsFontGrowth.MinimumReadablePixels;
        return scaled;
    }

    // The whole arrangement in one call, from the room a page offered and the row height the grid was built at.
    public static ScrollViewport Viewport(int rows, int frozen, float room, float rowHeight, int first)
        => new ScrollViewport(rows, frozen, VisibleRows(room, rowHeight), first);
}

// One notch per frame.
//
// IMGUI can deliver the same wheel gesture more than once during a single Unity frame - the repaint that
// follows a layout change is the usual way - and a scroll that answered every copy would move the table by
// two rows for one notch of the player's wheel. The latch is claimed before the offset moves and released only
// when a frame id larger than the claimed one arrives, so a repeat is consumed here (it is still this board's
// gesture and must not reach the page underneath) without moving the table a second time.
public sealed class ResultScrollLatch
{
    private int _frame = int.MinValue;

    public int Frame { get { return _frame; } }

    // Whether this frame may move the table. A frame id that went backwards is a new page (or a restarted
    // counter), so it is treated as a fresh frame rather than as a repeat of the last one.
    public bool Claim(int frame)
    {
        if (frame <= _frame && _frame != int.MinValue) return false;
        _frame = frame;
        return true;
    }

    // Whether this very frame already moved the table, which is the question the consumer asks before it takes
    // the event: the answer is "yes, this is the same gesture" rather than "no, move it again".
    public bool SameFrame(int frame) => _frame != int.MinValue && frame == _frame;

    public void Forget() { _frame = int.MinValue; }
}
