using System;
using System.Collections.Generic;

namespace HawChow.GTFOCombatPanel;

// ResultFinalLayout.cs - the settlement board's bounds as *final* geometry, decided before anything is drawn.
//
// The defect this answers, from one real success page (2560x1440, client, four players) and the log of
// the run that produced it: the board was placed from the fail-open path after 60 refused frames with
// the completion section's *drawn* edge at 250, then the page's own `CoroutineManager.SetActiveBlink`
// reveal activated more of that section, the edge the strip hangs from walked 250 -> 272 -> 314 -> 366,
// and the board was re-fitted twice and finally withdrawn (`completion-edge-unsettled`,
// `completionTop=526`, `heldStrip=366..1277`). Three separate mistakes are visible in that sequence:
//
//   1. a *timeout* was read as proof that the page had settled. Sixty refused frames say nothing about
//      whether the native reveal has finished; the run's own log shows it had not (`completed=0/3` when
//      the board was placed, `completed=3/3` ten lines later).
//   2. the upper bound was taken from pixels that had already been drawn (or from parts the reveal had
//      activated), so it described the reveal's progress rather than the layout the section ends at.
//      `CompletionBounds.PartBottom` deliberately prefers the drawn renderer; that is right for the
//      *strip a part really covers* and wrong for "where does this section end".
//   3. nothing required the geometry to be *stable* before the first board was shown, so the first thing
//      the player saw was a board laid out against a mid-reveal edge - and then it kept changing.
//
// What 1.19.21 leaves standing:
//
//   `CompletionFinalBounds`  the completion section's deepest edge from the parts' own laid-out slots, and
//                            nothing drawn alongside it: `PartBottom` always answers from the container
//                            rectangle the part was laid out in - the slot is final from the first frame the
//                            page laid it out - and the drawn depth is a diagnostic (`CompletionBounds.Deepest`)
//                            that never enters the band, so neither a slot's activation nor its first drawn
//                            pixel can move the edge. A part contributes when it is present and either
//                            switched on or scheduled by the page's own completion ledger (a slot the reveal
//                            has not switched on yet); a part that is absent from this build, or switched off
//                            and never scheduled for this page (an unused sector icon of a short run),
//                            contributes nothing.
//   `ResultFinalGate`        the stability latch, kept as diagnostics for the drawn-pixel route and for a
//                            page that hands over no full strip: placement itself is satisfied by readable
//                            final layout geometry on the first frame, and the latch cannot hold it back.
//   `ResultFinalPolicy`      when the fail-open path is allowed to answer instead: unchanged. A page that
//                            hands over no final geometry at all keeps the fail-open path it has, and a page
//                            that never stops moving is given up on after a bounded number of frames *and
//                            says so*: a give-up is never printed as "settled".
//   `ResultSettlementGate`   the fail-open decision itself, now that the success page's own native reveal is
//                            asked of it too: a reveal that is still running withholds the fail-open band
//                            exactly as it withholds the strict placement (F8 and a ledger that can no longer
//                            finish stay as escapes), and a reveal that was never observed at all is answered
//                            after the same bounded wait and logged as a reveal-unobservable fail-open.
//   `ResultLateControlPolicy` the per-repaint rule for a board that is already standing: move it only when
//                            it would really reach past its band. A band that slid twelve pixels while the
//                            page animated is the page's own animation, not a control arriving on top -
//                            moving the table for it is what the build this replaces did on every repaint
//                            ("moved to (370,284)", twelve pixels below where it had just been placed).
//
// Nothing here touches what is displayed, the row/column layout, the scoring, the configuration, the
// published 1.19.20 font/row policy (`ResultsFontGrowth`, `ImpactBoard.SplitKillTiersFor`) or the native
// page: it decides *when* a measured strip may be used and *whether* a standing board may move.
// Free of Unity and BepInEx on purpose, so every rule below is asserted headlessly in
// tests/GTFOCombatPanel.Smoke.

// The completion section's final edge, and how much of it is only laid out so far.
public static class CompletionFinalBounds
{
    // The bottom edge of one part as the section will end up: the element's own laid-out container rectangle,
    // always. The drawn pixels (`VisualBottom`) never enter the band - the reveal's drawn edge is a diagnostic
    // (`CompletionBounds.Deepest`) telling how far the reveal has come, and a part that starts drawing must not
    // change where the board hangs from: a padded container can end tens of pixels below its last drawn pixel,
    // and switching the band from the container to the first drawn pixel is exactly the upward jump that
    // re-measured a correctly placed board mid-reveal.
    //
    // A part contributes when it is on this page and either switched on, or scheduled by the page's own
    // completion ledger (`Scheduled`) but not switched on yet - the reveal's normal in-between state. A part
    // that is switched off AND was never scheduled for this page (an unused sector icon of a short run)
    // contributes nothing, and a part that is absent from this build contributes nothing either.
    //
    // `laidOutOnly` is the figure `laidOut` (below) counts: a part whose pixels have not arrived yet, whatever
    // its switch state - the caller reports it, and never waits on it.
    public static float PartBottom(in CompletionPart part, out bool laidOutOnly)
    {
        laidOutOnly = false;
        if (!part.Present || (!part.Active && !part.Scheduled)) return float.NaN;
        if (!float.IsFinite(part.OwnBottom)) return float.NaN;
        laidOutOnly = !float.IsFinite(part.VisualBottom);
        return part.OwnBottom;
    }

    // The deepest final bottom edge among the parts on this page, which of them produced it, and how many
    // contributing parts have not drawn their pixels yet (switched off but scheduled, or at zero alpha).
    //
    // `laidOut` is the figure the caller reports, never waits on: while it is above zero the section still has
    // a part whose pixels have not arrived, and the band the board hangs from is already its final one - a part
    // that is laid out contributes the rectangle it will occupy, so neither its activation nor its first drawn
    // pixel can move the edge. A part this page never scheduled (an unused sector icon of a short run)
    // contributes nothing, so it can neither deepen the band nor withhold the board.
    public static float Bottom(IReadOnlyList<CompletionPart> parts, out int deepest, out int laidOut)
    {
        deepest = -1;
        laidOut = 0;
        float bottom = float.NaN;
        if (parts == null) return bottom;
        for (int index = 0; index < parts.Count; index++)
        {
            float candidate = PartBottom(parts[index], out bool fromLayout);
            if (!float.IsFinite(candidate)) continue;
            if (fromLayout) laidOut++;
            if (float.IsFinite(bottom) && candidate <= bottom) continue;
            bottom = candidate;
            deepest = index;
        }
        return bottom;
    }

    // The laid-out final edge, never above the drawn reading the caller already has: the two together are the
    // lower safety net for a section the layout route could not answer at all. The runtime band is `Bottom` alone;
    // this helper stays for the tests that pin the net.
    public static float Bound(IReadOnlyList<CompletionPart> parts, float drawnBottom, out int deepest, out int laidOut)
    {
        float final = Bottom(parts, out deepest, out laidOut);
        if (!float.IsFinite(final)) return drawnBottom;
        if (!float.IsFinite(drawnBottom)) return final;
        return final > drawnBottom ? final : drawnBottom;
    }

    // The completion section's edge the *board* is placed from.
    //
    // While the page's own native reveal is still running the answer is the laid-out slot alone (`Bottom`): the reveal
    // switches slots on one after another, so anything drawn is a progress report and not the page's layout. Once that
    // reveal has finished, the section's real extent is the deeper of the slot and the pixels it really draws - a
    // padded container can end above the captions its partition icon draws, and a board hung from the container was
    // the panel's own top edge pressing on the native sector icons and their 主要/精英 captions. `Bound` is that
    // deeper-of rule; this is where it is chosen, so the choice is asserted headlessly instead of only in the display.
    //
    // `deepest` names the part that produced the answer, whichever of the two readings won, so the region line stays
    // true about the number it prints. `laidOut` is `Bottom`'s own count of parts whose pixels have not arrived.
    public static float Placed(IReadOnlyList<CompletionPart> parts, bool revealFinished, out int deepest, out int laidOut)
    {
        float layout = Bottom(parts, out deepest, out laidOut);
        if (!revealFinished) return layout;
        float drawn = CompletionBounds.Deepest(parts, out int drawnDeepest);
        if (!float.IsFinite(drawn)) return layout;
        float band = Bound(parts, drawn, out int boundDeepest, out _);
        deepest = !float.IsFinite(layout) || drawn > layout ? drawnDeepest : boundDeepest;
        return band;
    }
}


// The stability latch for a page whose geometry is still moving.
//
// One reading is not a measurement of a page that is still revealing itself: the completion section of a
// real success page moved by 276 px between two readings a handful of frames apart. The gate therefore
// closes on the first reading and only opens once two consecutive readings agree to within
// `TolerancePixels` - both edges, not just the top, because the strip is what the board is fitted into.
// 1.19.21: the *placement* no longer waits for this latch - readable final layout geometry answers it on
// the first frame - and `Observe` stays in the runtime as diagnostics plus the fail-open policy's
// note-taking (`ResultFinalPolicy`), which still refuses to answer for readable-but-moving geometry with
// the screen's own band.
public sealed class ResultFinalGate
{
    // How far two readings may differ and still count as the same edge. The page's own reveal moves by tens
    // of pixels; a hover animation on a button moves by one or two.
    public const float TolerancePixels = 1f;
    // How many agreeing readings the gate needs. Two: one reading can be taken in the middle of a frame the
    // page is still laying out, and a page that has stopped moving produces the second one immediately.
    public const int RequiredSamples = 2;

    private float _top = float.NaN, _bottom = float.NaN;
    private int _samples;

    // Consecutive readings that really agreed on both edges.
    public int Samples { get { return _samples; } }
    public bool Ready { get { return _samples >= RequiredSamples; } }
    public float Top { get { return _top; } }
    public float Bottom { get { return _bottom; } }

    // One frame's reading. Answers whether the gate is open now.
    //
    // A reading that is not finite cannot settle anything: it is treated as "the page did not answer", which
    // resets the agreement count rather than counting as a match - the alternative would be a page that
    // becomes "stable" by failing to be measured.
    public bool Observe(float top, float bottom)
    {
        if (!float.IsFinite(top) || !float.IsFinite(bottom) || !(bottom > top))
        {
            Forget();
            return false;
        }
        bool same = _samples > 0
            && Math.Abs(top - _top) <= TolerancePixels
            && Math.Abs(bottom - _bottom) <= TolerancePixels;
        _samples = same ? _samples + 1 : 1;
        _top = top; _bottom = bottom;
        return Ready;
    }

    // A different page, or a re-measure that is a first measurement again.
    public void Forget() { _samples = 0; _top = float.NaN; _bottom = float.NaN; }
}

// When the fail-open path may answer instead of the strict one.
public static class ResultFinalPolicy
{
    // How long a page whose final geometry is readable but never stops moving may keep the board away. This
    // is a give-up, not a definition of stability: a page that has not settled after this many frames is
    // placed from the screen's own band (the path that already existed) and the log line says the wait was
    // abandoned rather than that the page was stable. Three seconds at 60 frames per second.
    public const int MaximumWaitFrames = 180;

    // `finalMeasurable`  this frame's page handed over the geometry its strip is built from at all;
    // `finalSettled`     and that geometry has been read the same way twice (`ResultFinalGate.Ready`);
    // `forced`           F8, which never waits for anything;
    // `waitedFrames`     frames spent waiting on readable-but-moving geometry.
    //
    // The one combination that is refused is the interesting one: geometry that *is* readable but has not
    // settled yet. That is precisely the window in which the old build drew a board against a mid-reveal
    // edge and then re-fitted it three times; waiting a frame or two is cheaper than showing a board that
    // moves. A page that hands over no geometry at all is not waited for - it has nothing to wait for, and
    // the fail-open band is the only thing that can answer it - and a page that never settles is answered
    // after the bounded wait above.
    public static bool FailOpenAllowed(bool everMeasurable, bool finalMeasurable, bool finalSettled, bool forced, int waitedFrames)
        => forced || !everMeasurable || finalSettled || GivenUp(everMeasurable, finalSettled, forced, waitedFrames);

    // Whether the answer above is the bounded give-up rather than a settled page.
    //
    // It deliberately does not ask whether *this* frame was readable. A page that measured its strip and then went blind
    // for good is exactly the page this bound exists for: it has to be answered - with a line that says the wait was
    // abandoned - rather than leaving the screen empty for the rest of the expedition.
    public static bool GivenUp(bool everMeasurable, bool finalSettled, bool forced, int waitedFrames)
        => !forced && everMeasurable && !finalSettled && waitedFrames >= MaximumWaitFrames;

    // The line a waiting frame prints, once per page: what is missing, and the reason it is not a timeout.
    public static string WhyWaiting(bool everMeasurable, bool finalMeasurable, bool finalSettled, float top, float bottom, float boardFloor, int waitedFrames)
    {
        if (!everMeasurable)
            return "this page has never handed over its final geometry, so the fail-open band answers instead";
        if (!finalMeasurable)
            return "this page measured its final geometry earlier and could not this frame (last top="
                + Fix(top) + ", bottom=" + Fix(bottom) + ", waited=" + waitedFrames + " of " + MaximumWaitFrames
                + " frames); a dropped read is not taken as a page with no geometry, so the board is not placed against the screen";
        if (!finalSettled)
            return "the page's final geometry is readable but has not read the same twice yet (top="
                + Fix(top) + ", bottom=" + Fix(bottom) + ", waited=" + waitedFrames + " of "
                + MaximumWaitFrames + " frames), so the board is not shown at an edge that is still moving";
        return "the final geometry is settled (top=" + Fix(top) + ", bottom=" + Fix(bottom)
            + ", boardFloor=" + Fix(boardFloor) + "px)";
    }

    // The line the bounded give-up prints, once per page.
    public static string WhyGivenUp(float top, float bottom, int waitedFrames, bool readableThisFrame = true)
        => (readableThisFrame
                ? "the page's final geometry stayed readable but never read the same twice within "
                : "the page's final geometry stopped answering after it had been measured, and did not come back within ")
            + MaximumWaitFrames + " frames (last top=" + Fix(top) + ", bottom=" + Fix(bottom)
            + ", waited=" + waitedFrames + "), so the board is placed from the screen's own band instead - "
            + "this is a bounded give-up, not a settled page";

    private static string Fix(float value) => float.IsFinite(value) ? value.ToString("0") : "unmeasurable";
}

// Whether the success page's own native reveal still withholds the first placement.
//
// The native BlinkIn ledger (`BlinkCompletionLedger`, fed by `CompletionTargets`) is the only real "this page has
// finished showing itself" signal these screens offer, and the recorded success page is why it is asked *before* the
// board is placed: the panel laid the board out while the ledger read `completed=0/6` and re-measured it at `6/6`, so
// the first thing the player saw stood against a section that was still arriving. This gate refuses that placement.
//
// Three things keep it from ever becoming "the board never appears":
//
//   * it is a statement about the ledger this page built, never about elapsed frames: nothing here counts frames, and
//     there is no `ready || dead || forced` short circuit anywhere above it;
//   * a ledger that can no longer finish - a target the page handed over with no object, a target count past the
//     ledger's budget - answers immediately (`RevealImpossible`), exactly as every earlier build did;
//   * F8 is the player's own answer and never waits (`Forced`).
//
// A page whose reveal never finishes at all is answered by the paths that already exist for it: the strict fit spends
// its bounded refusal budget (`ResultFallbackPolicy`) and the fail-open board - which steps around every control the
// page measured - stands in, logged as fail-open rather than as a proven page.
public static class ResultRevealGate
{
    public static bool BlocksPlacement(bool revealFinished, bool mayStillComplete, bool forced)
        => !forced && !revealFinished && mayStillComplete;

    // The one line a withheld frame prints: the ledger's own counts, and what releases it.
    public static string Why(int completed, int count, int names, int artwork)
        => "the page's own sector BlinkIn reveal has not finished (completed=" + completed + "/" + count
            + ", names=" + names + ", artwork=" + artwork + "), so the completion section is still being switched on; "
            + "the board is not placed against a section that is still arriving, and the native ledger itself releases "
            + "it - not a frame count, an alpha threshold or an empty scan. A ledger that can no longer finish is "
            + "answered at once, and F8 never waits";
}

// Whether the *fail-open* path may answer, now that the success page's native reveal is asked of it too.
//
// The defect this answers is the independent review's first finding, on the same recorded page the gate above is written
// for. `ResultRevealGate` had been wired into the *strict* placement only: the fail-open decision asked
// `ResultFinalPolicy` - the final-geometry latch - and nothing else, and that latch had already been fed from
// `SuccessRegion` on the same frame with the completion section's *container* reading (313 on that page), which is the
// section's edge only once the reveal has switched it on. So sixty refused frames were answered by the fail-open band at
// 313, the reveal then delivered the caption edge at 352, the standing board was re-measured 39 px lower and the log wrote
// "region re-measured": "drawn first, moved after", in the one place left that could still do it. A budget that gives up
// on *geometry* cannot answer a question about the page's own *reveal*.
//
// The rule, in one place:
//
//   * a success page whose reveal is still running does not fail open, exactly as it does not place strictly;
//   * the two escapes `ResultRevealGate` already has are kept unchanged: F8 (`forced`) never waits, and a ledger that can
//     no longer finish (`revealImpossible` - a target past the ledger's budget) answers at once;
//   * a page whose reveal was **never observed at all** (`revealCount == 0`: the game ran no sector BlinkIn this visit, so
//     no frame can ever complete that ledger) is still answered - after `ResultFinalPolicy.MaximumWaitFrames`, the give-up
//     budget this file already has, and as `RevealUnobservable`. That is the only place a frame count may release this
//     gate, it introduces no new number, and the caller logs it as a reveal-unobservable fail-open rather than as a
//     settled page. The fail-open refusal budget (`ResultFallbackPolicy.MissBudget`, 60) never releases it;
//   * once the reveal has finished, the fail-open path's own rule (`ResultFinalPolicy`) is untouched.
//
// A failure page never feeds the completion ledger - the blink-in reveal is the success page's - so the caller passes
// `revealEnforced: false` there and that page behaves exactly as it did.
public static class ResultSettlementGate
{
    public enum Answer
    {
        // F8: the player's own answer, waits for nothing.
        Forced = 0,
        // The ledger can no longer complete: there is nothing left to wait for.
        RevealImpossible = 1,
        // The page's reveal has been observed and has not finished: not this frame.
        RevealPending = 2,
        // The reveal was never observed at all and the bounded wait is spent: fail open, and say which this is.
        RevealUnobservable = 3,
        // The reveal has finished and the page handed over no final geometry: nothing to wait for there either.
        NoFinalGeometry = 4,
        // The reveal has finished and the final geometry settled: the ordinary answer.
        Settled = 5,
        // The reveal has finished and the final geometry never settled: the bounded give-up, said as such.
        GivenUp = 6,
        // The reveal has finished and the final geometry is readable but still moving: not yet.
        FinalPending = 7,
    }

    // The answer, and which of the two questions produced it.
    public readonly struct Decision
    {
        public readonly bool MayFailOpen;
        public readonly Answer Why;
        public Decision(bool mayFailOpen, Answer why) { MayFailOpen = mayFailOpen; Why = why; }
        // A fail-open that the page's reveal, not its geometry, had to be released for.
        public bool RevealHeld { get { return Why == Answer.RevealPending || Why == Answer.RevealUnobservable; } }
        // Drawable, and never to be reported as a settled page.
        public bool Unobservable { get { return Why == Answer.RevealUnobservable; } }
    }

    // `forced`           F8, the player's own press;
    // `revealEnforced`   this page's placement waits on the completion ledger at all (the success page's reveal);
    // `revealFinished`   `ResultsDisplay.CompletionReady` - the native BlinkIn ledger really completed;
    // `revealImpossible` the ledger can no longer complete (`BlinkCompletionLedger.RevealImpossible`);
    // `revealCount`      targets the ledger was handed this visit; zero means the reveal was never observed;
    // `revealWaitFrames` frames spent waiting on a reveal that has not been observed (never on one that has);
    // the rest are `ResultFinalPolicy`'s own facts, unchanged.
    public static Decision Decide(bool forced, bool revealEnforced, bool revealFinished, bool revealImpossible,
        int revealCount, int revealWaitFrames, bool everMeasurable, bool finalMeasurable, bool finalSettled,
        int finalWaitFrames)
    {
        if (forced) return new Decision(true, Answer.Forced);
        if (revealEnforced)
        {
            if (revealImpossible) return new Decision(true, Answer.RevealImpossible);
            if (!revealFinished)
            {
                if (revealCount <= 0 && revealWaitFrames >= ResultFinalPolicy.MaximumWaitFrames)
                    return new Decision(true, Answer.RevealUnobservable);
                return new Decision(false, Answer.RevealPending);
            }
        }
        if (!ResultFinalPolicy.FailOpenAllowed(everMeasurable, finalMeasurable, finalSettled, false, finalWaitFrames))
            return new Decision(false, Answer.FinalPending);
        if (!everMeasurable) return new Decision(true, Answer.NoFinalGeometry);
        return new Decision(true, finalSettled ? Answer.Settled : Answer.GivenUp);
    }

    // The one line a withheld or unobservable fail-open frame prints: which answer it was, in the ledger's own counts.
    public static string Why(Answer answer, int completed, int count, int waitFrames)
        => answer == Answer.RevealUnobservable
            ? "the page's own sector BlinkIn reveal was never observed on this visit (completed=" + completed + "/" + count
              + ", waited=" + waitFrames + " of " + ResultFinalPolicy.MaximumWaitFrames + " frames), so the board is drawn from the "
              + "fail-open band - a reveal-unobservable fail-open, not a settled page: nothing in this window proves the completion "
              + "section stopped changing"
            : "the page's own sector BlinkIn reveal has not finished (completed=" + completed + "/" + count
              + ", waited=" + waitFrames + " of " + ResultFinalPolicy.MaximumWaitFrames + " frames), so the fail-open band may not "
              + "answer either: the refusal budget answers the page's geometry, never its reveal, and a board drawn at a container "
              + "edge the reveal has not reached is the board that is re-measured 39 px lower a moment later";
}

// The rules for a board that is already standing on a page that keeps drawing.
public static class ResultLateControlPolicy
{
    // Whether a standing fail-open board has to move this frame.
    //
    // Two edges are binding, not one.
    //
    // The band's *lower* edge is the control the player has to press: a board whose bottom reaches past it would be drawn
    // over that control. The band's *upper* edge is the page's own anchor - the completion section, the icon row, the
    // mission-failed title - and it moves *down* when those native elements move down. A board that keeps an older, higher
    // top after that is drawn over the very thing the band's top exists to keep clear, and a rule that only asks about the
    // board's bottom cannot see it: the board is still inside the band's foot while its top sits above the band's top. So
    // both edges are asked about, and the caller passes the rectangle that is *on the screen* rather than the one it just
    // computed - asking about the destination is what made the old rule blind to the top edge moving.
    //
    // The tolerance is the same half pixel the placement and the safety checks already use: a band that slid by a fraction
    // of a pixel - the page's own animation - must not move a table the player is reading.
    //
    // A band whose foot arrived above the board's foot is not moved into a longer life by this rule; the destination is the
    // band's own top, which is where the board already stands, and the caller draws such a board faint instead
    // (`ResultFallbackOpacity`). Moving is for the case in which a move really helps; fading is for the case in which it
    // cannot.
    public static bool MustMove(float boardTop, float boardBottom, float bandTop, float bandBottom, float tolerance = 0.5f)
    {
        if (!float.IsFinite(boardTop) || !float.IsFinite(boardBottom)) return false;
        if (!float.IsFinite(tolerance) || tolerance < 0f) tolerance = 0.5f;
        // The band's own anchor slid down into the board: the board would be drawn over what that edge keeps clear.
        if (float.IsFinite(bandTop) && boardTop < bandTop - tolerance) return true;
        // A band that does not exist is not a band to leave.
        if (!float.IsFinite(bandBottom) || !(bandBottom > 0f)) return false;
        // The board reaches past the band's foot, i.e. over the control that foot is.
        return boardBottom > bandBottom + tolerance;
    }
}
