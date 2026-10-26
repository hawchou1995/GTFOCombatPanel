using System;

namespace HawChow.GTFOCombatPanel;

// ---------------------------------------------------------------------------------------------------
// ResultsPagePolicy.cs - the two settlement pages' own preferences and the one second look a board that
// was measured without its button is allowed.
//
// Everything here is free of Unity and of BepInEx so the whole of it is asserted headlessly in
// tests/GTFOCombatPanel.Smoke. Both pieces exist because of what a real page does:
//
//   ResultsZoom            the mouse wheel scales the settlement board, one preference per result page, so
//                          the success board and the failure board remember their own size instead of
//                          sharing one. The scale can only ever *reduce* the size the page's own strip
//                          produced: that size is already the largest board that clears the button, so a
//                          scale above 1 is clamped back onto it rather than allowed to cover the footer.
//   ResultAnchorRecovery   a board laid out on the historical fraction of the window because the lobby
//                          button could not be measured at all may be re-measured exactly once, the moment
//                          that button's own rectangle becomes readable. A board that is stable is never
//                          moved by it, and the second look is a per-page budget of one, not a per-frame
//                          re-measure.
// ---------------------------------------------------------------------------------------------------

// Which of the two settlement pages a preference belongs to. The page itself decides it: the same switch
// the board uses to pick the page it stands in for.
public enum ResultPageKind { Success = 0, Failure = 1 }

// The wheel-controlled glyph scale of the settlement board.
//
// The board's size on a result page is a measurement, not a preference: `ResultsFontGrowth` measures the
// table at a candidate glyph size and answers with the largest one that fills the strip between the page's
// completion section and the lobby button. What the wheel changes is the size that measurement is *drawn*
// at, so a player who reads better on a smaller board can have one, and the value is remembered separately
// per page - the success strip and the failure strip are different heights, so the same scale means two
// different tables.
public static class ResultsZoom
{
    // Where the two preferences live in the plugin's own .cfg. One readable key each, in the same section
    // as the rest of the result-page settings.
    public const string Section = "Results";
    public const string SuccessKey = "SuccessFontScale";
    public const string FailureKey = "FailureFontScale";

    // 1.0 is "the size the page's own strip allows", which is what the board has always been drawn at.
    public const float Default = 1f;
    // The range a player may step through. The floor keeps a board a table rather than a rumour; the ceiling
    // is where a bigger board would stop being one - and it is clamped again by the strip, so it can never
    // be the reason a button is covered.
    public const float Minimum = 0.5f;
    public const float Maximum = 2f;
    // One wheel notch, so both pages store the same discrete set of values and a file edit and a scroll
    // cannot leave a value the next notch then jumps over.
    public const float Step = 0.1f;
    // A wheel delta is "a notch happened" or it is nothing: a trackpad can report a fraction of one, and the
    // direction is what the player meant. The dead zone is what keeps a resting trackpad from rescaling.
    public const float WheelDeadZone = 0.0001f;
    // IMGUI reports one notch *away* from the player - scrolling down - as a positive `Event.current.delta.y`,
    // and a notch towards them as a negative one. That convention is what `Direction` reads, and it is the one
    // place it lives: a build whose wheel reads the other way only has to flip this constant.
    public const bool WheelUpIsNegative = true;

    public static ResultPageKind Page(bool succeeded) => succeeded ? ResultPageKind.Success : ResultPageKind.Failure;

    public static string KeyFor(ResultPageKind page) => page == ResultPageKind.Success ? SuccessKey : FailureKey;

    public static bool Accepts(float wheelDelta) => float.IsFinite(wheelDelta) && Math.Abs(wheelDelta) > WheelDeadZone;

    // +1 for a notch up (the board grows), -1 for a notch down, 0 for a wheel that did not move.
    public static int Direction(float wheelDelta)
    {
        if (!Accepts(wheelDelta)) return 0;
        bool up = WheelUpIsNegative ? wheelDelta < 0f : wheelDelta > 0f;
        return up ? 1 : -1;
    }

    // A stored value that is out of range, not a number at all (an entry nobody could read), or exactly on a
    // bound is brought back inside the range the keys describe.
    public static float Clamp(float scale)
    {
        if (!float.IsFinite(scale)) return Default;
        if (scale < Minimum) return Minimum;
        if (scale > Maximum) return Maximum;
        return scale;
    }

    // The value quantised onto the step grid, so every stored scale is one the wheel can produce again.
    public static float Snap(float scale)
    {
        double steps = Math.Round((Clamp(scale) - Minimum) / (double)Step, MidpointRounding.AwayFromZero);
        double snapped = Minimum + steps * (double)Step;
        return Clamp((float)Math.Round(snapped, 3));
    }

    // One wheel notch: up enlarges by exactly one step, down reduces by exactly one step, and a wheel that
    // stopped at a bound keeps the bound rather than wrapping round it.
    public static float Next(float current, float wheelDelta)
    {
        int direction = Direction(wheelDelta);
        if (direction == 0) return Snap(current);
        return Snap(Clamp(current) + direction * Step);
    }

    // The glyph size the board is really drawn at.
    //
    // `fittedPixels` is what the page's strip produced: the largest size that fills the strip inside the fill band
    // `ResultsFontGrowth` keeps free above the button. The wheel scales that, and the result is then measured
    // against the box the page really offers - `stripHeight` from the completion section to the button, and
    // `boardWidth` the width of the page. Asking for more than the band keeps free is what makes a notch up
    // visible at all (the band leaves 4-8% of the strip unused), and a size that would reach the button or leave
    // the width is stepped down until it does not: the wheel can choose a bigger board, never a board over the
    // control the player has to press. Downwards the answer is the scaled size itself, and never below the absolute
    // readable minimum, because a board nobody can read is not a zoom level.
    //
    // A caller that passes no measurement - or a strip and width it could not read - gets the conservative answer:
    // the size the strip produced, scaled but never enlarged, which is what this answered before a page offered its
    // own box.
    public static int ScaleSize(int fittedPixels, float scale) => ScaleSize(fittedPixels, scale, 0f, 0f, null);

    // Hard bound on the walk down, so a measurement that is not monotone cannot spin here.
    public const int MaximumScaleSteps = 512;

    public static int ScaleSize(int fittedPixels, float scale, float stripHeight, float boardWidth,
        Func<int, ResultTableSize> measure)
    {
        int fitted = fittedPixels < 1 ? 1 : fittedPixels;
        // The absolute readable minimum is the floor of everything this returns, including the size a caller handed
        // in: a board nobody can read is not a size, and a scale that could only answer with one would make the grid
        // vanish rather than draw a smaller table.
        if (fitted < ResultsFontGrowth.MinimumReadablePixels) fitted = ResultsFontGrowth.MinimumReadablePixels;
        int wanted = (int)Math.Round(fitted * (double)Clamp(scale), MidpointRounding.AwayFromZero);
        if (wanted < ResultsFontGrowth.MinimumReadablePixels) wanted = ResultsFontGrowth.MinimumReadablePixels;
        bool boxed = measure != null && float.IsFinite(stripHeight) && stripHeight > 0f
            && float.IsFinite(boardWidth) && boardWidth > 0f;
        if (!boxed) return wanted > fitted ? fitted : wanted;
        // The strip's own size fits the strip by definition, so this walk can never end below it either: it starts
        // at the scaled size and stops at the first size the page can really draw.
        int working = wanted, guard = 0;
        while (working > ResultsFontGrowth.MinimumReadablePixels && guard++ < MaximumScaleSteps)
        {
            ResultTableSize size;
            try { size = measure(working); }
            catch { return wanted > fitted ? fitted : wanted; }
            // A measurement that reports nothing is no evidence that a larger board fits, so the strip's own size
            // stands rather than a size nobody could measure.
            if (!size.Usable) return fitted;
            if (size.Height <= stripHeight + 0.5f && size.Width <= boardWidth + ResultsFontGrowth.WidthSlack) break;
            working--;
        }
        return working;
    }
}

// A board that had to be laid out without the lobby button's geometry.
//
// `ResultsRegion.Resolve` falls back to the historical fraction of the window when the button cannot be
// measured, and the settlement board is then fixed: it keeps the size it was measured at for the life of the
// page, because re-measuring a settled board is exactly what used to make it jump. The one exception is a
// board whose strip was never measured at all - it was placed against a guess - and which is still waiting
// when the button's own rectangle becomes readable a moment later. That board gets one second look, so a
// first measurement taken before the page had finished revealing its footer cannot fix a table at 6px for
// the rest of the expedition. A board that is stable is never moved by it, and the budget is one per page.
public static class ResultAnchorRecovery
{
    public const int MaximumRecoveries = 1;

    // Whether the board standing now should be re-measured: it has to have been placed on the fallback, the
    // button's geometry has to be readable now, and this page must not have spent its one retry already.
    public static bool ShouldRetry(int recoveries, bool placedOnFallback, bool anchorReadable)
        => placedOnFallback && anchorReadable && recoveries >= 0 && recoveries < MaximumRecoveries;

    // The budget after a retry, saturated: asking twice can never buy a third attempt.
    public static int Record(int recoveries)
        => recoveries < 0 ? 0 : recoveries >= MaximumRecoveries ? recoveries : recoveries + 1;

    // Whether a placement made just now is one the second look applies to. A strip that was measured - even
    // one measured through the button's own layout rectangle - is not a guess and is never re-measured; and a
    // page that already spent its retry does not get another, however it was placed afterwards.
    public static bool LatchFallback(int recoveries, bool stripMeasured) => !stripMeasured && recoveries == 0;
}
