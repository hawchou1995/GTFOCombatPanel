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

// ---------------------------------------------------------------------------------------------------
// When a result page's own anchors are ready to be measured.
// ---------------------------------------------------------------------------------------------------
//
// Both settlement pages animate themselves in, and both used to be measured on the first frame on which
// anything at all could be read: the failure page the moment its title's alpha left zero, the success page
// the moment the game's own BlinkIn routines finished. A strip read on that frame is not the page's strip -
// the title has not landed, the footer has not been drawn, the completion section's own captions are still
// arriving - and the board fitted into it keeps that size for the life of the page, because
// `ResultBoardHold.TryHold` only ever lifts a board and never enlarges one. The board then looks too small
// until something else tears the layout down (F8, a configuration reload, a column count change).
//
// What follows is the rule both pages now share, in four small pieces and no Unity types:
//
//   ResultTitleGate    the failure page's own message: it has to be *fully drawn* - its own alpha, multiplied by
//                      every canvas group above it, has really arrived at opaque - and its measured bottom edge has
//                      to have stopped moving. Geometry that has settled is not evidence that the text has finished
//                      rendering, so a title at half opacity over a rock-steady rectangle is still not an anchor.
//   ResultEdgeGate     one measured edge of the page (a footer button, the completion section's own bottom)
//                      followed across frames. It answers "settled" only for an edge that was really read at the
//                      same value twice; an edge the page has not handed over stays unanswered however long the
//                      page runs, because there is no arrangement of guesses that is better than the vanilla page.
//   ResultRetryCadence how often a page that has not handed an anchor over is measured again: every frame while
//                      the reveal is still running, then at a bounded cadence, so waiting for an anchor cannot cost
//                      a full walk of the page's renderers and buttons 60 times a second for ever.
//   ResultAnchorDrift  the page moved *after* the board was placed: compared against the strip the board was
//                      really measured into, with a threshold and a couple of samples of hysteresis, so one frame
//                      of the page's own animation cannot rebuild the layout and so a board that has settled is
//                      never touched.
// ---------------------------------------------------------------------------------------------------

// The failure page's own title, followed across frames.
//
// The page fades `m_missionFailed_text` in, and it can move as it lands. `alpha` is the title as the player sees it -
// its own text alpha multiplied by every canvas group above it. Two things are required together, and neither one
// substitutes for the other:
//
//   * the text has really been drawn: its alpha has arrived at `OpaqueAlpha`. A title at 10% or 50% over a rectangle
//     that has already stopped moving is still a title the player cannot read, and the board must not appear before
//     the page's own headline is on the screen - that is exactly the report this rule exists for, because a stable
//     rectangle is not evidence that the text has finished rendering. An alpha nobody could read is not evidence of
//     completion either.
//   * its measured bottom edge has held still: this reading and the one before it are the same value within
//     `EdgeTolerance`, `EdgeFrames` readings in a row.
//
// `Edge` is the last bottom edge this title really handed over, and it survives the frames in which the renderer
// cannot be read at all - that is what a later real measurement (the drift test, a re-measure, a diagnostic line)
// starts from, and it is why a title whose renderer blinks out is not turned into "no geometry at all". `EdgeAge`
// says how many frames ago it was read. Neither answers "complete" on its own: `Observe` returns true only on a
// frame where both conditions above hold.
public sealed class ResultTitleGate
{
    // Full opacity: what the page draws once the title's fade is over. There is deliberately no second way to arrive
    // here - geometry that has stopped moving is not text that has finished rendering.
    public const float OpaqueAlpha = 0.99f;
    // At or below this the page is still revealing the title and it is not on the screen at all.
    public const float VisibleAlpha = 0.02f;
    // The measured bottom edge has to hold still: one frame of a title that is still landing is not an anchor.
    public const float EdgeTolerance = 1f;
    public const int EdgeFrames = 2;

    private float _reading = float.NaN, _anchor = float.NaN;
    private int _edgeSteady, _misses, _anchorAge = -1, _opaque;

    // Readings this title has been observed without being settled. Reported, never a bound on anything: a title that
    // never becomes opaque keeps the vanilla page, and no number of missed readings turns that into a placement.
    public int Misses { get { return _misses; } }
    // Consecutive readings at or above `OpaqueAlpha`, for the diagnostic line that says why the board is still not up.
    public int OpaqueFrames { get { return _opaque; } }
    // The last bottom edge this title really handed over; NaN until it has been measured once.
    public float Edge { get { return _anchor; } }
    // Frames since that edge was measured (0 on the frame it was), or -1 when it never was.
    public int EdgeAge { get { return _anchorAge; } }

    // One frame of evidence. `active` is whether the page has the title switched on at all; `measured` and `edge`
    // are this frame's rectangle of it.
    //
    // Two different edges live here and they are not the same thing: `_reading` is the previous frame's reading,
    // which is what "has this edge stopped moving" is asked against and which a frame without a reading clears;
    // `_anchor` is the last reading that really existed, which survives those frames so a re-measure still starts
    // from this page's own geometry.
    public bool Observe(bool active, float alpha, bool measured, float edge)
    {
        if (!active)
        {
            _reading = float.NaN; _edgeSteady = 0; _opaque = 0;
            if (_anchorAge >= 0) _anchorAge++;
            _misses++;
            return false;
        }
        float visible = float.IsFinite(alpha) ? Math.Min(1f, Math.Max(0f, alpha)) : float.NaN;
        bool drawn = float.IsFinite(visible) && visible > VisibleAlpha;
        bool opaque = float.IsFinite(visible) && visible >= OpaqueAlpha;
        _opaque = opaque ? _opaque + 1 : 0;
        bool readable = drawn && measured && float.IsFinite(edge);
        bool edgeSteady = readable && float.IsFinite(_reading) && Math.Abs(edge - _reading) <= EdgeTolerance;
        _edgeSteady = readable ? (edgeSteady ? _edgeSteady + 1 : 0) : 0;
        if (readable) { _reading = edge; _anchor = edge; _anchorAge = 0; }
        else { _reading = float.NaN; if (_anchorAge >= 0) _anchorAge++; }
        bool settled = opaque && readable && _edgeSteady + 1 >= EdgeFrames;
        _misses = settled ? 0 : _misses + 1;
        return settled;
    }

    public void Forget() { _reading = _anchor = float.NaN; _edgeSteady = 0; _misses = 0; _anchorAge = -1; _opaque = 0; }
}

// One measured edge of a result page, followed across frames.
//
// It answers one question and only one: has this edge been read at the same value twice in a row? An edge the page has
// not handed over yet - a footer button that has not been drawn, a completion section whose pixels cannot be read -
// stays unanswered however long the page runs. `SettleAttempts` exists for the caller to know when to stop measuring
// the page on every frame and fall back to a bounded retry cadence (`ResultRetryCadence`); it is emphatically *not* a
// timeout after which an unmeasured edge counts as measured. Turning "nobody handed me a button" into "ready" is
// precisely how a board was drawn from the 0.66-of-the-window fraction and locked in at a size nobody could read, and
// a page whose own geometry is unavailable is better served by the game's own screen than by this mod's guess.
public sealed class ResultEdgeGate
{
    // An edge is settled once its own value has been read `SettleFrames` times in a row within `Tolerance`.
    public const float Tolerance = 1f;
    public const int SettleFrames = 2;
    // Readings after which the caller should stop measuring every frame and retry at a bounded cadence. A report, not
    // a licence: `Exhausted` never makes `Observe` answer true.
    public const int SettleAttempts = 120;

    private float _edge = float.NaN;
    private int _steady, _attempts, _misses;

    // The last real reading of this edge; NaN until it has been read once. Cleared by a frame without a reading, because
    // a stale edge is not evidence that the edge is still where it was.
    public float Edge { get { return _edge; } }
    // Readings in which this edge was not handed over at all.
    public int Misses { get { return _misses; } }
    // Readings in which it was handed over.
    public int Attempts { get { return _attempts; } }
    // Enough readings have gone by that the caller should retry at a bounded cadence instead of every frame.
    public bool Exhausted { get { return _misses + _attempts >= SettleAttempts; } }

    // One frame of evidence: true only for an edge that was really read at the same value twice in a row.
    public bool Observe(bool measured, float edge)
    {
        if (!measured || !float.IsFinite(edge))
        {
            _steady = 0; _edge = float.NaN;
            if (_misses < int.MaxValue) _misses++;
            return false;
        }
        _misses = 0;
        if (_attempts < int.MaxValue) _attempts++;
        bool steady = float.IsFinite(_edge) && Math.Abs(edge - _edge) <= Tolerance;
        _steady = steady ? _steady + 1 : 0;
        _edge = edge;
        return _steady + 1 >= SettleFrames;
    }

    public void Forget() { _edge = float.NaN; _steady = 0; _attempts = 0; _misses = 0; }
}

// How often a settlement page that has not handed an anchor over yet is measured again.
//
// Measuring a page walks every renderer of its completion section and every button it drew, so a page whose title never
// becomes opaque, or whose footer button is never drawn, must not pay for that walk on every frame for the rest of the
// expedition - and it must not be given an approximate board either. The page is therefore measured on every frame
// while its reveal is still running (that is where a real page settles), and after `EveryFrameFrames` readings it is
// re-measured every `RetryEveryFrames` frames until a real anchor appears. Nothing here answers "ready": it answers
// "look again", and `ResultTitleGate` / `ResultEdgeGate` answer whether what was found is real.
public sealed class ResultRetryCadence
{
    // Readings taken on every frame: the page's own reveal, plus a wide margin for a slow one.
    public const int EveryFrameFrames = 120;
    // After that, one reading every this many frames (~0.5s at 60 fps).
    public const int RetryEveryFrames = 30;

    private int _waits, _lastFrame = int.MinValue;

    // Readings asked for so far on this page.
    public int Waits { get { return _waits; } }
    // Whether the every-frame window is spent and the page is on the bounded cadence.
    public bool Throttled { get { return _waits >= EveryFrameFrames; } }

    // Whether the page should be measured on this frame. `frame` is the engine's own frame counter, which only has to
    // be monotone; a counter that restarts (a reload) is treated as "due" rather than as a reason to skip for ever.
    public bool Due(int frame)
    {
        _waits++;
        if (_waits <= EveryFrameFrames) { _lastFrame = frame; return true; }
        if (_lastFrame != int.MinValue && frame >= _lastFrame && frame - _lastFrame < RetryEveryFrames) return false;
        _lastFrame = frame;
        return true;
    }

    public void Forget() { _waits = 0; _lastFrame = int.MinValue; }
}

// The page moved after the board was placed.
//
// The board keeps the strip it was measured into - that latch is what stops a settled table from jumping - but
// the page underneath it can still change: a resolution change, another mod re-laying the footer out, a
// completion section whose optional parts appear a moment after the native Blink finished, or a first
// placement that had to fall back because one edge was not readable yet. None of those should need F8: the two
// figures the board was really fitted to - the strip's upper edge and the room it gave - are compared against
// what the page measures now, and a difference past the threshold for two samples in a row spends one bounded
// re-measure. The threshold is both absolute and relative, so a two-pixel wobble of the page's own animation is
// not a move, and every re-measure is budgeted, so this can never become a per-frame rebuild.
public sealed class ResultAnchorDrift
{
    // A real move: more than this many pixels, and more than this share of the figure that moved. The share is
    // what keeps a couple of pixels of the page's own animation from reading as a new layout, while a footer
    // that lands a tenth of the strip lower - the case that fixes a small board - does read as one.
    public const float MinimumPixels = 12f;
    public const float Share = 0.08f;
    // Two samples in a row, so one frame of the page's own animation cannot rebuild anything.
    public const int Frames = 2;
    // How many times one page may be re-measured because it moved. A page that keeps moving is left alone
    // after this rather than rebuilt for ever.
    public const int MaximumRecoveries = 2;

    private float _top = float.NaN, _room = float.NaN;
    private int _frames, _recoveries;

    // A board is only compared against a strip that was really recorded.
    public bool Armed { get { return float.IsFinite(_top) && float.IsFinite(_room); } }
    public int Recoveries { get { return _recoveries; } }
    public bool Exhausted { get { return _recoveries >= MaximumRecoveries; } }

    // The strip the board standing now was measured into: its upper edge and the room it gave. Called from the one
    // place that really places a board, so the comparison is always against a real measurement.
    public void Record(float top, float room)
    {
        if (!float.IsFinite(top) || !float.IsFinite(room) || !(room > 0f)) return;
        _top = top; _room = room; _frames = 0;
    }

    public void Forget() { _top = _room = float.NaN; _frames = 0; _recoveries = 0; }

    // Whether the page has really moved away from the strip the board was measured into. Does not spend the
    // budget: the caller spends it with `Spent` once it has started the re-measure, so a sample that could not be
    // acted on costs nothing.
    public bool Due(float top, float room)
    {
        if (!Armed) return false;
        if (!Moved(top, _top) && !Moved(room, _room)) { _frames = 0; return false; }
        if (++_frames < Frames) return false;
        return !Exhausted;
    }

    public void Spent()
    {
        if (_recoveries < MaximumRecoveries) _recoveries++;
        _frames = 0;
    }

    // Whether an edge has moved by more than this rule allows.
    public static bool Moved(float now, float recorded)
        => float.IsFinite(now) && float.IsFinite(recorded)
           && Math.Abs(now - recorded) > Math.Max(MinimumPixels, Math.Abs(recorded) * Share);
}
