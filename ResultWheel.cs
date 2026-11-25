using System;

namespace HawChow.GTFOCombatPanel;

// ---------------------------------------------------------------------------------------------------
// ResultWheel.cs - the settlement board's wheel, routed the way the player's other panel routes it
// ---------------------------------------------------------------------------------------------------
//
// The board used to read IMGUI's `EventType.ScrollWheel` in `OnGUI`. That made the wheel depend on an event the game's
// own input does not have to deliver (the recorded sessions came from a page whose CellMenu input is sampled, not
// dispatched), it aimed with `Event.current.mousePosition` while every other gesture this mod answers aimed with the
// page's own cursor, and it could only ever move one row per notch.
//
// The panel this player already runs (conflictmodlist 1.1.15, `shared/PanelPointer.cs` `WheelRouter` +
// `src/TeamVersionsDriver.cs` `HandleMouse`) settles the same question from the frame's own samples instead:
//
//   * the page's native `CM_PageBase.ScrollInput` first, the physical `Input.mouseScrollDelta.y` as the fallback;
//   * at most ONE of the two per frame - they are never added, so one notch can never move the table twice;
//   * the native reading is consumed by *change*: a reading that stays non-zero but unchanged has already been used,
//     and the physical wheel gets that frame instead (a held analogue stick cannot scroll for ever);
//   * leaving the list clears only the frame's source (`Suspend`) and keeps the consumed native fingerprint, so coming
//     back with the same still-held reading does not scroll again; only `Reset` forgets it;
//   * one notch is three rows, at most five notches per frame, and a 0.1-per-notch platform is normalised by ten.
//
// `ResultWheelRouter` is that router, with the same constants, the same consumption rule and the same sign convention,
// and it is free of Unity so every rule above is asserted headlessly. It is a self-contained port: this assembly does
// not reference the other mod, does not load it, and does not read its files at run time.
//
// What is *not* the same, and why: the other mod scrolls a continuous list in pixels (its top row may be cut in half),
// while this board is a table with a frozen header row and whole-row slots - `ResultsScroll` owns that. The router
// therefore answers pixels exactly like the reference, and `ResultWheelSteps` turns those pixels into whole rows while
// keeping the sub-row remainder across frames, so a 0.05-notch trackpad tick is not rounded away and two of them are
// one row. Nothing banks input that had no effect: a table held against either end (or one that does not overflow)
// drops the remainder instead of replaying it when the wall opens.
public enum ResultWheelSource
{
    None = 0,
    Native = 1,
    Physical = 2
}

public sealed class ResultWheelRouter
{
    // One notch is three rows - the reference's `RowsPerNotch`, and the figure the recorded pages were read at
    // (3 x 24 px at the 1440 window's 16 px target glyph).
    public const float RowsPerNotch = 3f;
    // At most five notches per frame: one huge reading (a fast flick, a driver that reports the whole gesture at once)
    // must not throw the table to its foot.
    public const float MaxNotches = 5f;

    private float _previous;

    public ResultWheelSource Source { get; private set; }
    public float ObservedNative { get; private set; }
    public float ObservedPhysical { get; private set; }
    // The consumed native fingerprint, exposed so a test (and the one diagnostic line) can say what was remembered.
    public float PreviousNative => _previous;

    // A new page, a new window, the board being hidden: the fingerprint goes with them.
    public void Reset()
    {
        _previous = 0f;
        Source = ResultWheelSource.None;
        ObservedNative = 0f;
        ObservedPhysical = 0f;
    }

    // The pointer left the board (or the board is not drawn this frame): the frame's source is cleared, the consumed
    // native fingerprint is kept. That is the whole difference from `Reset`, and it is what stops a still-held native
    // reading from scrolling a second time when the pointer comes back.
    public void Suspend()
    {
        Source = ResultWheelSource.None;
        ObservedNative = 0f;
        ObservedPhysical = 0f;
    }

    // The pixels this frame adds to the table's scroll (positive = towards the table's foot), or 0 for "not this frame".
    public float Sample(float native, float physical, float rowHeight, float maxPixels)
    {
        if (!float.IsFinite(native)) native = 0f;
        if (!float.IsFinite(physical)) physical = 0f;
        ObservedNative = native;
        ObservedPhysical = physical;
        // Consumed by *change*: a native reading that stays non-zero but unchanged is not a second notch.
        bool fresh = native != 0f && native != _previous;
        _previous = native;
        Source = ResultWheelSource.None;
        if (fresh)
        {
            Source = ResultWheelSource.Native;
            return Pixels(native, rowHeight, maxPixels);
        }
        if (physical != 0f)
        {
            Source = ResultWheelSource.Physical;
            return Pixels(physical, rowHeight, maxPixels);
        }
        return 0f;
    }

    // One raw reading, in the reference's own arithmetic: 0.1-per-notch platforms are normalised by ten, the notch
    // count is capped at `MaxNotches`, the pixels are capped at the viewport, and a negative reading (Unity's physical
    // wheel scrolls down as -y) becomes a positive "towards the foot" pixel count.
    public static float Pixels(float raw, float rowHeight, float maxPixels)
    {
        if (!float.IsFinite(raw) || raw == 0f) return 0f;
        if (!float.IsFinite(rowHeight) || rowHeight <= 0f) return 0f;
        float notches = MathF.Abs(raw);
        if (notches < 1f) notches *= 10f;
        notches = MathF.Min(notches, MaxNotches);
        float pixels = MathF.Min(notches * rowHeight * RowsPerNotch, MathF.Max(0f, maxPixels));
        return raw < 0f ? pixels : -pixels;
    }

    // How many rows one raw reading is worth, by the reference's own arithmetic and this table's row pitch. Kept as a
    // pure function so the golden trace can compare the two answers number for number without a router instance.
    public static float Rows(float raw, float rowHeight, float maxPixels)
        => rowHeight > 0f ? Pixels(raw, rowHeight, maxPixels) / rowHeight : 0f;
}

// Pixels in, whole rows out, with the sub-row remainder carried across frames.
//
// The reference keeps a continuous pixel offset, so half a row stays half a row. This board draws whole rows only
// (`ResultsScroll`), so the fraction has to live somewhere or a 0.05-notch trackpad tick is lost every frame it happens.
public sealed class ResultWheelSteps
{
    private float _remainder;
    private float _pitch;

    public float Remainder => _remainder;

    public void Reset() { _remainder = 0f; _pitch = 0f; }

    // The whole rows this frame's pixels buy. `mayMove` is the caller's answer to "is there a table under the pointer
    // with an overflow" - a false answer drops the remainder rather than banking it for a page that is not scrolling.
    //
    // The remainder is a *pixel* fraction, so it is only meaningful at one row pitch: a resolution change re-fits the
    // table and the same pixels mean a different number of rows, so the pitch is part of the accumulator's identity and a
    // new one starts from zero (1.19.59). Nothing else here looks at frames or clocks.
    public int Rows(float pixels, float rowHeight, bool mayMove)
    {
        if (!mayMove || !float.IsFinite(rowHeight) || rowHeight <= 0f) { _remainder = 0f; return 0; }
        if (!float.IsFinite(_pitch) || _pitch != rowHeight) { _pitch = rowHeight; _remainder = 0f; }
        if (!float.IsFinite(pixels)) { _remainder = 0f; return 0; }
        _remainder += pixels;
        float rows = _remainder / rowHeight;
        int whole = (int)(rows >= 0f ? MathF.Floor(rows) : MathF.Ceiling(rows));
        _remainder -= whole * rowHeight;
        // A fraction smaller than a tenth of a pixel is not a fraction; keeping it would only accumulate float noise.
        if (MathF.Abs(_remainder) < 0.1f) _remainder = 0f;
        return whole;
    }
}
