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

    // Whether the page's own strip has really come back, as opposed to merely having a rectangle somewhere.
    //
    // The second look is a promise that the strict placement can now take what the page offers, so it may only be
    // taken on the same two facts the placement is judged by: a strip this frame could measure at all
    // (`stripOfferable`), and the footer that bounds it - measured (`footerMeasured`), or proven absent by the
    // page's own no-footer proof (`noFooterProven`).
    //
    // 1.19.26: the failure page answered this with "the title rectangle and *a* button rectangle are finite", which
    // a page whose leave button is switched off, re-laid out by another mod or never read by the strip still
    // satisfies. The retry it spent re-placed the board strictly, the next repaint could not measure that footer,
    // `BoardStillSafe` withdrew the board on `no-footer-unproofed`, and the refunded budget armed the second look
    // again - fail-open, second look, withdraw, for ever. A layout rectangle is not a footer, and an unmeasurable
    // footer is not a recovery.
    public static bool Recovered(bool stripOfferable, bool footerMeasured, bool noFooterProven)
        => stripOfferable && (footerMeasured || noFooterProven);

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

// The failure page's upper anchor is always its own mission-failed text. A client whose mission title is inactive
// may use only that title's measured layout after the native ExpeditionFailedSequence has finished; waiting-status
// text is never an upper anchor, because it can sit at the bottom of the page. The active-title path still requires
// full opacity and stable geometry through ResultTitleGate; the inactive-layout path requires native sequence
// completion plus a separately stable measured mission-title edge.
//
// FailureSequenceReadiness binds one native coroutine instance to one page visit. Only that iterator's terminal
// MoveNext(false) observation can mark the sequence finished; a stale page, a different sequence, a timeout, or an
// unreadable title cannot produce readiness.
public sealed class FailureSequenceReadiness
{
    private IntPtr _page, _sequence;
    private bool _finished;

    public IntPtr Page { get { return _page; } }
    public IntPtr Sequence { get { return _sequence; } }
    public bool Finished { get { return _finished; } }

    public void BeginPage(IntPtr page)
    {
        _page = page;
        _sequence = IntPtr.Zero;
        _finished = false;
    }

    public bool BindSequence(IntPtr page, IntPtr sequence)
    {
        if (page == IntPtr.Zero || page != _page || sequence == IntPtr.Zero) return false;
        _sequence = sequence;
        _finished = false;
        return true;
    }

    public bool MarkFinished(IntPtr page, IntPtr sequence)
    {
        if (page == IntPtr.Zero || page != _page || sequence == IntPtr.Zero || sequence != _sequence) return false;
        _finished = true;
        return true;
    }

    public bool IsFinished(IntPtr page) => page != IntPtr.Zero && page == _page && _sequence != IntPtr.Zero && _finished;

    public void Forget() { _page = _sequence = IntPtr.Zero; _finished = false; }
}

public enum FailureTitleAnchorKind { None = 0, MissionFailed = 1, MissionTitleLayoutAfterSequence = 2, MissionTitleLayout = 3 }

// Which native fact supplies the failure page's upper edge.
//
// 1.19.21: the title's laid-out screen rectangle (`ResultsDisplay.TryScreenRect`, a pure projection that asks neither
// alpha, text nor active state) is an anchor source of its own. A title the page has laid out but not finished
// drawing - the reported shape of the real failure page, whose finished text produced `rawTextAlpha=1.00` while every
// earlier frame was refused - contributes its final edge from the very first frame it is laid out. `ResultTitleGate`
// (full opacity over a still edge) is no longer a placement precondition: it remains the *drawn-text route's*
// measurement, and the log line says which of the two routes produced the edge. The old rules are unchanged wherever
// they are not superseded: the active, non-empty mission title is still the first candidate, a client whose title the
// page switched off is still only answered after its native failure sequence has completed, and a page whose title
// hands over no geometry at all still has no upper anchor.
public static class FailureTitleAnchorPolicy
{
    public static FailureTitleAnchorKind Select(bool client, bool nativeLobbyVisible, bool missionPresent,
        bool missionActive, bool missionTextPresent, bool missionGeometryMeasured, bool failureSequenceFinished)
    {
        if (missionPresent && missionActive && missionTextPresent) return FailureTitleAnchorKind.MissionFailed;
        if (client && !nativeLobbyVisible && !missionActive && missionPresent && missionTextPresent
            && missionGeometryMeasured && failureSequenceFinished)
            return FailureTitleAnchorKind.MissionTitleLayoutAfterSequence;
        // The layout route: the title's own rectangle, readable even while its text has not arrived and even while the
        // native sequence is still running. It is the final geometry, so nothing about the draw state may hold it back.
        if (missionPresent && missionGeometryMeasured) return FailureTitleAnchorKind.MissionTitleLayout;
        return FailureTitleAnchorKind.None;
    }
}

// The mission-failed title's *drawn-text* measurement: full opacity (`OpaqueAlpha`) over an edge read twice
// (`EdgeFrames`).
//
// 1.19.21: this gate is diagnostics and the drawn-pixel route's recorder, no longer a placement precondition. The
// failure page's upper edge is the title's laid-out screen rectangle (see `FailureTitleAnchorPolicy`); whenever that
// layout geometry is readable the board may be placed on it at once, at alpha 0 or mid-fade, and the log line
// distinguishes the route its `titleSource` came from. The gate's own answer still means exactly what it says - the
// *text* has been read at full opacity over a still edge - and it stays in the placement path so a live log can
// always tell which route an edge came from.
public sealed class ResultTitleGate
{
    public const float OpaqueAlpha = 0.99f;
    public const float VisibleAlpha = 0.02f;
    public const float EdgeTolerance = 1f;
    public const int EdgeFrames = 2;

    private float _reading = float.NaN, _anchor = float.NaN;
    private int _edgeSteady, _misses, _anchorAge = -1, _opaque;
    private int _lastFrame = int.MinValue, _syntheticFrame;
    private bool _settled;
    private int _sameFrameInvalidations;
    public int Misses { get { return _misses; } }
    public int OpaqueFrames { get { return _opaque; } }
    public float Edge { get { return _anchor; } }
    public int EdgeAge { get { return _anchorAge; } }
    public int SameFrameInvalidations { get { return _sameFrameInvalidations; } }

    // Compatibility for managed callers that represent one call as one frame. Runtime callers pass Unity's frame id.
    public bool Observe(bool active, float alpha, bool measured, float edge)
        => Observe(_syntheticFrame++, active, alpha, measured, edge);

    public bool Observe(int frame, bool active, float alpha, bool measured, float edge)
    {
        float visible = float.IsFinite(alpha) ? Math.Min(1f, Math.Max(0f, alpha)) : float.NaN;
        bool drawn = active && float.IsFinite(visible) && visible > VisibleAlpha;
        bool opaque = drawn && visible >= OpaqueAlpha;
        bool readable = drawn && measured && float.IsFinite(edge);

        // IMGUI can ask the same page more than once during one Unity frame. Such a repaint is not a second
        // observation; a conflicting reading only invalidates stability and must be confirmed on a later frame.
        if (frame == _lastFrame)
        {
            if (!opaque || !readable || float.IsFinite(_reading) && Math.Abs(edge - _reading) > EdgeTolerance)
            {
                if (_sameFrameInvalidations < int.MaxValue) _sameFrameInvalidations++;
                _reading = float.NaN; _edgeSteady = 0; _opaque = 0; _settled = false;
            }
            return _settled;
        }
        if (_lastFrame != int.MinValue && frame < _lastFrame) Forget();
        _lastFrame = frame;

        if (!active)
        {
            // A title the page has switched off owns no edge: the same page object is raised again for the next
            // expedition, and reusing the rectangle the title occupied on the previous screen would place a board
            // from an anchor nothing is drawn at. The reading and the anchor go together, and `EdgeAge` reports
            // "no anchor" (-1) instead of ageing a value that no longer exists.
            _reading = _anchor = float.NaN; _edgeSteady = 0; _opaque = 0; _settled = false; _anchorAge = -1;
            _misses++;
            return false;
        }
        _opaque = opaque ? _opaque + 1 : 0;
        bool edgeSteady = readable && float.IsFinite(_reading) && Math.Abs(edge - _reading) <= EdgeTolerance;
        _edgeSteady = readable ? (edgeSteady ? _edgeSteady + 1 : 0) : 0;
        if (readable) { _reading = edge; _anchor = edge; _anchorAge = 0; }
        else { _reading = float.NaN; if (_anchorAge >= 0) _anchorAge++; }
        _settled = opaque && readable && _edgeSteady + 1 >= EdgeFrames;
        _misses = _settled ? 0 : _misses + 1;
        return _settled;
    }

    public void Forget()
    {
        _reading = _anchor = float.NaN; _edgeSteady = 0; _misses = 0; _anchorAge = -1; _opaque = 0;
        _lastFrame = int.MinValue; _syntheticFrame = 0; _settled = false;
        _sameFrameInvalidations = 0;
    }
}
// One measured edge of a result page, followed across frames.
// 1.19.21: like every settle latch on these pages this is diagnostics now - the placement is satisfied by readable
// final layout geometry on the first frame it exists, and the `Observe` calls stay in the placement path purely so a
// live log keeps its settle counters. `SettleAttempts` keeps its one real job: telling the caller when to stop
// measuring every frame and fall back to the bounded cadence, never a timeout that turns an unmeasured edge into a
// placement.
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
    public const float Tolerance = 1f;
    public const int SettleFrames = 2;
    public const int SettleAttempts = 120;

    private float _edge = float.NaN;
    private int _steady, _attempts, _misses;
    private int _lastFrame = int.MinValue, _syntheticFrame;
    private bool _settled;

    public float Edge { get { return _edge; } }
    public int Misses { get { return _misses; } }
    public int Attempts { get { return _attempts; } }
    public bool Exhausted { get { return _misses + _attempts >= SettleAttempts; } }

    public bool Observe(bool measured, float edge) => Observe(_syntheticFrame++, measured, edge);

    public bool Observe(int frame, bool measured, float edge)
    {
        bool readable = measured && float.IsFinite(edge);
        if (frame == _lastFrame)
        {
            if (!readable || float.IsFinite(_edge) && Math.Abs(edge - _edge) > Tolerance)
            {
                _steady = 0; _edge = float.NaN; _settled = false;
            }
            return _settled;
        }
        if (_lastFrame != int.MinValue && frame < _lastFrame) Forget();
        _lastFrame = frame;
        if (!readable)
        {
            _steady = 0; _edge = float.NaN; _settled = false;
            if (_misses < int.MaxValue) _misses++;
            return false;
        }
        _misses = 0;
        if (_attempts < int.MaxValue) _attempts++;
        bool steady = float.IsFinite(_edge) && Math.Abs(edge - _edge) <= Tolerance;
        _steady = steady ? _steady + 1 : 0;
        _edge = edge;
        _settled = _steady + 1 >= SettleFrames;
        return _settled;
    }

    public void Forget()
    {
        _edge = float.NaN; _steady = 0; _attempts = 0; _misses = 0;
        _lastFrame = int.MinValue; _syntheticFrame = 0; _settled = false;
    }
}

// A client may use the remainder of a result page only after the page itself confirms that it has no footer
// and a complete scan finds no native or Unity interaction item. Unknown/unreadable controls never qualify.
public sealed class ResultNoFooterGate
{
    public const int RequiredSamples = 3;
    private int _samples, _lastFrame = int.MinValue;

    public int Samples { get { return _samples; } }
    public bool Ready { get { return _samples >= RequiredSamples; } }

    // `interactionCount` is the number of *active* interactions the page had switched on - whether or not their
    // rectangle could be read - and `unmeasurableCount` the number of those whose rectangle could not be. Both
    // have to be zero for the footer to count as absent: an interaction nobody could place is still an
    // interaction, and this gate is the only thing that releases the unbounded guest strip, so it may never be
    // released for a page that has anything on it that the scan could not prove empty. The defaults keep the
    // seven-argument call sites (the tests that drive this gate with a plain count) on the same rule.
    public bool Observe(int frame, bool client, bool anchorSettled, bool nativeStateKnown,
        bool nativeFooterVisible, bool interactionScanComplete, int interactionCount, int unmeasurableCount = 0)
    {
        bool absent = client && anchorSettled && nativeStateKnown && !nativeFooterVisible
            && interactionScanComplete && interactionCount == 0 && unmeasurableCount == 0;
        if (!absent)
        {
            Forget();
            _lastFrame = frame;
            return false;
        }
        if (frame == _lastFrame) return Ready;
        if (_lastFrame != int.MinValue && frame < _lastFrame) Forget();
        _lastFrame = frame;
        if (_samples < RequiredSamples) _samples++;
        return Ready;
    }

    public void Forget() { _samples = 0; _lastFrame = int.MinValue; }
}

// ---------------------------------------------------------------------------------------------------
// The band a host's own failure page measured, and when it is proof enough for the board to stand in it
// ---------------------------------------------------------------------------------------------------
//
// The recorded host failure page (`LogOutput.log`, the 2026-11-13 21:57:56 run, 2560x1440, one failed expedition,
// four players downed) had exactly one strict release: the leave button's own rectangle. A few lines after the
// fail-open board had been placed, and one line after `failure region re-measured: both of this page's own edges ...
// are readable now`, the repaint printed
//
//   failure-strip board not drawn: reason=no-footer-unproofed, heldStrip=381..1145 (764px), need=270px,
//   detail=buttonMeasured=false,noFooterReady=False,nativeVisible=True,count=0,complete=True
//
// - the board had just been placed strictly, the page's own two edges were measured (the mission-failed title at
// 381 above, the waiting line at 1164 below: `fail-open band: top=381 (source=title-anchor), bottom=1142
// (source=footer-text, waitingText=1164), room=762px`), all five of the page's controls were switched off
// (`hidden=5`), the complete scan found nothing to press (`count=0, complete=true`), and the native page itself
// reported its footer visible (`nativeVisible=True`, `fail.m_lobbyButtonVisible`). The one fact missing was the
// button's own `activeInHierarchy`: `ResultsDisplay.ButtonActive` answers false for a button the page switched off,
// so `buttonMeasured` is false - and it is the only thing the strict path accepted.
//
// `FailureNoFooter.Ready` cannot stand in for it on a host: its `absent` term is gated on `client`
// (`ResultNoFooterGate.Observe`), and `ResultPageRolePolicy.UsesNoFooterSafeArea` is true for a client alone. So
// before this release a host failure page had exactly one release - this frame's button rectangle - and a board
// was withdrawn on the next frame that could not read it. That withdrawal, one frame after the second look had
// promoted the fail-open board to a strict one, is what left the player ten presses of which four were forced.
//
// The rule stated here: a control the page has switched off cannot be pressed, so the leave button being inactive
// is not evidence that the board stands over something the player needs. What *is* evidence is the page's own two
// measured edges (its title anchor above, the waiting/footer text it measured below) together with a complete scan
// that found nothing switched on. That is a policy rather than a condition inside the display, so the shape the
// recorded page really had can be asserted headlessly.
//
// Nothing is relaxed: an active control whose rectangle reaches the board is still the one fact that defers the
// frame - `BoardStillSafe` asks `failureControls.OverlapsBoard` before it asks this class, and the answer here
// names it too - and a scan that could not see the whole page still refuses, because `complete` is a fact about
// the page's structure that no amount of waiting repairs.

// One frame of evidence about a host's failure page: what the interaction scan found, what the native page says
// about its own footer, whether the leave button's rectangle was read this frame, and which of the page's own
// measurements produced the two edges of the strip the board would stand in.
public readonly struct FailureBandEvidence
{
    public readonly ResultPageRole Role;
    public readonly bool ScanComplete, ParentCoverageComplete;
    public readonly int ActiveControls, Unmeasurable;
    public readonly bool NativeFooterVisible, ButtonMeasured, OverlapsControls;
    public readonly ResultBoundSource BandTopSource, BandBottomSource;

    public FailureBandEvidence(ResultPageRole role, bool scanComplete, bool parentCoverageComplete,
        int activeControls, int unmeasurable, bool nativeFooterVisible, bool buttonMeasured, bool overlapsControls,
        ResultBoundSource bandTopSource, ResultBoundSource bandBottomSource)
    {
        Role = role; ScanComplete = scanComplete; ParentCoverageComplete = parentCoverageComplete;
        ActiveControls = activeControls; Unmeasurable = unmeasurable;
        NativeFooterVisible = nativeFooterVisible; ButtonMeasured = buttonMeasured; OverlapsControls = overlapsControls;
        BandTopSource = bandTopSource; BandBottomSource = bandBottomSource;
    }
}

// What one frame's evidence proves about the strip a host failure page offers.
public enum FailureBandProofState
{
    // The page's own two edges bound the strip, the scan was complete and found nothing switched on, and the page
    // itself says its footer is there: a board may stand in it, and the button it could not read is not a reason
    // to refuse or to withdraw one.
    Proven = 0,
    // A client's strip is its fixed page geometry; this proof is the host's way out and never the client's.
    ClientOwned = 1,
    // An active control's rectangle really reaches the board: defer, unchanged, whatever else was measured.
    OverlapsControls = 2,
    // The scan could not see the whole page, so it cannot claim the page has nothing on it.
    ScanIncomplete = 3,
    // The scan saw something switched on, or something it could not place: both may take the pointer.
    ActiveControls = 4,
    // The page reports no footer of its own, so there is nothing to say the strip ends where the player is not.
    NativeFooterAbsent = 5,
    // The strip's edges are not the page's own measurements (its title anchor and a text/wall it laid out): a
    // band that starts from a fraction of the window or ends at the window's foot proves nothing about the page.
    BandNotPageMeasured = 6,
}

public static class ResultFailureBandProofPolicy
{
    // Which fact produced the answer, in the few words a live log can afford.
    public static string Name(FailureBandProofState state)
    {
        switch (state)
        {
            case FailureBandProofState.Proven: return "page-band-proven";
            case FailureBandProofState.ClientOwned: return "client-owned-strip";
            case FailureBandProofState.OverlapsControls: return "overlaps-controls";
            case FailureBandProofState.ScanIncomplete: return "scan-incomplete";
            case FailureBandProofState.ActiveControls: return "active-controls";
            case FailureBandProofState.NativeFooterAbsent: return "native-footer-absent";
            case FailureBandProofState.BandNotPageMeasured: return "band-not-page-measured";
            default: return "unknown";
        }
    }

    // The upper edge has to be the failure page's own mission-failed title, which `FailureStrip` reads from the
    // title's laid-out rectangle whether or not the page has drawn it yet.
    public static bool PageMeasuredTop(ResultBoundSource source)
        => source == ResultBoundSource.TitleAnchor;

    // The lower edge has to be a wall or a block the page itself laid out: the waiting-status text the player reads,
    // the leave-expedition control's own rectangle standing in for it, or another control the scan measured. The
    // window's own foot is not one of them - a board that ends there has nothing under it that was read.
    public static bool PageMeasuredBottom(ResultBoundSource source)
        => source == ResultBoundSource.FooterText || source == ResultBoundSource.FooterTextControl
            || source == ResultBoundSource.FooterControl || source == ResultBoundSource.MeasuredControl;

    // The whole answer, in the order the facts are asked. Overlap first, because an active control that reaches the
    // board is a deferral whatever else is true; structure next, because a scan that could not see the page cannot
    // claim anything about it; then the two ways a strip is released - the button the page did hand over this frame,
    // or the page's own two measured edges.
    public static FailureBandProofState Decide(in FailureBandEvidence evidence)
    {
        if (evidence.Role != ResultPageRole.Host) return FailureBandProofState.ClientOwned;
        if (evidence.OverlapsControls) return FailureBandProofState.OverlapsControls;
        if (!evidence.ScanComplete || !evidence.ParentCoverageComplete) return FailureBandProofState.ScanIncomplete;
        if (evidence.ActiveControls != 0 || evidence.Unmeasurable != 0) return FailureBandProofState.ActiveControls;
        if (evidence.ButtonMeasured) return FailureBandProofState.Proven;
        if (!evidence.NativeFooterVisible) return FailureBandProofState.NativeFooterAbsent;
        if (!PageMeasuredTop(evidence.BandTopSource) || !PageMeasuredBottom(evidence.BandBottomSource))
            return FailureBandProofState.BandNotPageMeasured;
        return FailureBandProofState.Proven;
    }

    // Whether a board may stand in this frame's strip and stay there, which is the one question both the placement
    // and the per-repaint check ask.
    public static bool Proven(in FailureBandEvidence evidence) => Decide(evidence) == FailureBandProofState.Proven;

    // The same answer for the failure page's own strip measurement, so a caller cannot pass the fields in the wrong
    // order: `band` is the `ResultsBand` the strip was built from, whose sources name the facts that produced it.
    public static FailureBandProofState Decide(ResultPageRole role, bool scanComplete, bool parentCoverageComplete,
        int activeControls, int unmeasurable, bool nativeFooterVisible, bool buttonMeasured, bool overlapsControls,
        ResultsBand band)
        => Decide(new FailureBandEvidence(role, scanComplete, parentCoverageComplete, activeControls, unmeasurable,
            nativeFooterVisible, buttonMeasured, overlapsControls, band.TopSource, band.BottomSource));

    // The same answer, as the one yes/no the display needs.
    public static bool Proven(ResultPageRole role, bool scanComplete, bool parentCoverageComplete,
        int activeControls, int unmeasurable, bool nativeFooterVisible, bool buttonMeasured, bool overlapsControls,
        ResultsBand band)
        => Proven(new FailureBandEvidence(role, scanComplete, parentCoverageComplete, activeControls, unmeasurable,
            nativeFooterVisible, buttonMeasured, overlapsControls, band.TopSource, band.BottomSource));

    // The strip's two provenances in one string, for the one line a refusal prints: a band that was not proven has
    // to say which edge it could not measure, or the next reader is back to guessing.
    public static string Describe(FailureBandProofState state, ResultsBand band)
        => "bandProof=" + Name(state)
           + ",bandTop=" + ResultsBounds.Name(band.TopSource) + ",bandBottom=" + ResultsBounds.Name(band.BottomSource);
}

// A success page can prove the host's strip from its own icon row and measured footer, even when the
// lobby button is not active/readable on this frame. The native-visible flag is recorded as evidence,
// but it cannot replace either measured edge; unlike the client no-footer route, a host does not need
// to claim that the page has no footer when the page has measured the band it offers.
public readonly struct SuccessBandEvidence
{
    public readonly ResultPageRole Role;
    public readonly bool ScanComplete, ParentCoverageComplete;
    public readonly int ActiveControls, Unmeasurable;
    public readonly bool NativeFooterVisible, ButtonMeasured, OverlapsControls;
    public readonly ResultBoundSource BandTopSource, BandBottomSource;

    public SuccessBandEvidence(ResultPageRole role, bool scanComplete, bool parentCoverageComplete,
        int activeControls, int unmeasurable, bool nativeFooterVisible, bool buttonMeasured, bool overlapsControls,
        ResultBoundSource bandTopSource, ResultBoundSource bandBottomSource)
    {
        Role = role; ScanComplete = scanComplete; ParentCoverageComplete = parentCoverageComplete;
        ActiveControls = activeControls; Unmeasurable = unmeasurable;
        NativeFooterVisible = nativeFooterVisible; ButtonMeasured = buttonMeasured; OverlapsControls = overlapsControls;
        BandTopSource = bandTopSource; BandBottomSource = bandBottomSource;
    }
}

public enum SuccessBandProofState
{
    Proven = 0,
    ClientOwned = 1,
    OverlapsControls = 2,
    ScanIncomplete = 3,
    ActiveControls = 4,
    BandNotPageMeasured = 5,
}

public static class ResultSuccessBandProofPolicy
{
    public static string Name(SuccessBandProofState state)
    {
        switch (state)
        {
            case SuccessBandProofState.Proven: return "page-band-proven";
            case SuccessBandProofState.ClientOwned: return "client-owned-strip";
            case SuccessBandProofState.OverlapsControls: return "overlaps-controls";
            case SuccessBandProofState.ScanIncomplete: return "scan-incomplete";
            case SuccessBandProofState.ActiveControls: return "active-controls";
            case SuccessBandProofState.BandNotPageMeasured: return "band-not-page-measured";
            default: return "unknown";
        }
    }

    public static bool PageMeasuredTop(ResultBoundSource source)
        => source == ResultBoundSource.IconRow || source == ResultBoundSource.LastTrustedIconRow;

    public static bool PageMeasuredBottom(ResultBoundSource source)
        => source == ResultBoundSource.FooterText || source == ResultBoundSource.FooterTextControl
            || source == ResultBoundSource.FooterControl || source == ResultBoundSource.MeasuredControl;

    // Overlap is the first answer: even perfect page geometry cannot release a control under the board.
    // The old measured-button path remains valid after the same scan/role gates; only an unmeasured
    // button needs the success page's own icon-row-to-footer band as its alternative proof.
    public static SuccessBandProofState Decide(in SuccessBandEvidence evidence)
    {
        if (evidence.OverlapsControls) return SuccessBandProofState.OverlapsControls;
        if (evidence.Role != ResultPageRole.Host) return SuccessBandProofState.ClientOwned;
        if (!evidence.ScanComplete || !evidence.ParentCoverageComplete) return SuccessBandProofState.ScanIncomplete;
        if (evidence.ActiveControls != 0 || evidence.Unmeasurable != 0) return SuccessBandProofState.ActiveControls;
        if (evidence.ButtonMeasured) return SuccessBandProofState.Proven;
        if (!PageMeasuredTop(evidence.BandTopSource) || !PageMeasuredBottom(evidence.BandBottomSource))
            return SuccessBandProofState.BandNotPageMeasured;
        return SuccessBandProofState.Proven;
    }

    public static SuccessBandProofState Decide(ResultPageRole role, bool scanComplete, bool parentCoverageComplete,
        int activeControls, int unmeasurable, bool nativeFooterVisible, bool buttonMeasured, bool overlapsControls,
        ResultsBand band)
        => Decide(new SuccessBandEvidence(role, scanComplete, parentCoverageComplete, activeControls, unmeasurable,
            nativeFooterVisible, buttonMeasured, overlapsControls, band.TopSource, band.BottomSource));

    public static bool Proven(in SuccessBandEvidence evidence) => Decide(evidence) == SuccessBandProofState.Proven;

    public static string Describe(SuccessBandProofState state, ResultsBand band)
        => "bandProof=" + Name(state)
           + ",bandTop=" + ResultsBounds.Name(band.TopSource) + ",bandBottom=" + ResultsBounds.Name(band.BottomSource);
}


// An explicitly sourced viewport strip for a client page whose native footer is absent. A measured protected
// lower wall (for example, the native host-wait status text) shortens the strip; it is never an upper anchor.
// Placement still requires ResultNoFooterGate.Ready from page state and a complete control/tree scan.
public static class ResultNoFooterSafeArea
{
    public static bool TryResolve(float measuredTop, float screenHeight, out ResultsSafeArea area,
        float lowerWallTop = float.NaN, float gap = 0f)
    {
        area = default;
        if (!float.IsFinite(measuredTop) || !float.IsFinite(screenHeight) || screenHeight <= 8f) return false;
        if (!float.IsNaN(lowerWallTop) && (!float.IsFinite(lowerWallTop) || !float.IsFinite(gap) || gap < 0f)) return false;
        float top = Math.Max(0f, measuredTop);
        float bottom = screenHeight - 8f;
        if (float.IsFinite(lowerWallTop)) bottom = Math.Min(bottom, lowerWallTop - gap);
        if (!(bottom > top)) return false;
        area = new ResultsSafeArea(top, bottom);
        return true;
    }
}

public sealed class ResultRetryCadence
{
    public const int EveryFrameFrames = 120;
    public const int RetryEveryFrames = 30;

    private int _waits, _lastSampleFrame = int.MinValue, _lastObservedFrame = int.MinValue;
    private bool _lastDecision;

    public int Waits { get { return _waits; } }
    public bool Throttled { get { return _waits >= EveryFrameFrames; } }

    public bool Due(int frame)
    {
        if (frame == _lastObservedFrame) return _lastDecision;
        if (_lastObservedFrame != int.MinValue && frame < _lastObservedFrame)
        {
            _waits = 0;
            _lastSampleFrame = int.MinValue;
        }
        _lastObservedFrame = frame;
        if (_waits < EveryFrameFrames)
        {
            _waits++;
            _lastSampleFrame = frame;
            return _lastDecision = true;
        }
        if (_lastSampleFrame != int.MinValue && frame >= _lastSampleFrame
            && frame - _lastSampleFrame < RetryEveryFrames)
            return _lastDecision = false;
        if (_waits < int.MaxValue) _waits++;
        _lastSampleFrame = frame;
        return _lastDecision = true;
    }

    public void Forget()
    {
        _waits = 0; _lastSampleFrame = int.MinValue; _lastObservedFrame = int.MinValue; _lastDecision = false;
    }
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
    public const float MinimumPixels = 12f;
    public const float Share = 0.08f;
    public const int Frames = 2;
    public const int MaximumRecoveries = 2;

    private float _top = float.NaN, _room = float.NaN;
    private int _frames, _recoveries, _lastFrame = int.MinValue, _syntheticFrame;

    public bool Armed { get { return float.IsFinite(_top) && float.IsFinite(_room); } }
    public int Recoveries { get { return _recoveries; } }
    public bool Exhausted { get { return _recoveries >= MaximumRecoveries; } }

    public void Record(float top, float room) => Record(_syntheticFrame++, top, room);

    public void Record(int frame, float top, float room)
    {
        if (!float.IsFinite(top) || !float.IsFinite(room) || !(room > 0f)) return;
        _top = top; _room = room; _frames = 0; _lastFrame = frame;
    }

    public void Forget()
    {
        _top = _room = float.NaN; _frames = 0; _recoveries = 0;
        _lastFrame = int.MinValue; _syntheticFrame = 0;
    }

    // The board this drift was recorded against is gone - a deferral threw the placement away - so the recorded strip has to go
    // with it: it describes a board that is no longer on the page, and re-measuring against it would compare a fresh placement
    // with a stale one. What must *not* go is the budget (independent review F2): `MaximumRecoveries` is the only thing that stops
    // a page whose edges keep moving from costing a re-measure for ever, and deferrals are per-repaint events, so refunding it
    // there would have made the bound meaningless - place, drift, defer, refund, place again, without end. `Forget` is therefore
    // kept for a new page (and for a frame counter that restarted); a deferral calls `Clear`.
    public void Clear()
    {
        _top = _room = float.NaN;
        _frames = 0;
        _lastFrame = int.MinValue;
    }

    public bool Due(float top, float room) => Due(_syntheticFrame++, top, room);

    public bool Due(int frame, float top, float room)
    {
        if (frame == _lastFrame) return false;
        if (_lastFrame != int.MinValue && frame < _lastFrame) Forget();
        _lastFrame = frame;
        if (!Armed) return false;
        if (!Moved(top, _top) && !Moved(room, _room)) { _frames = 0; return false; }
        if (_frames < Frames) _frames++;
        return _frames >= Frames && !Exhausted;
    }

    public void Spent()
    {
        if (_recoveries < MaximumRecoveries) _recoveries++;
        _frames = 0;
    }

    public static bool Moved(float now, float recorded)
        => float.IsFinite(now) && float.IsFinite(recorded)
           && Math.Abs(now - recorded) > Math.Max(MinimumPixels, Math.Abs(recorded) * Share);
}
