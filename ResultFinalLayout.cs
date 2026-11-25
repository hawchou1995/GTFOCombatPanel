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
    // Whether this part's *visible content* is a complete measurement of it: every child renderer and every caption
    // under it was enumerated (no exception), nothing under it is switched off or faded out (so nothing deeper can
    // still appear), and at least one child really answered with a rectangle. Only then may the band use the pixels
    // the part draws instead of its container rectangle - the reading that lets a padded container that reserves
    // 275 px of blank space stop pushing the board down the page.
    public static bool ContentComplete(in CompletionPart part)
    {
        if (!part.Present || !part.Active) return false;
        if (!part.Measured || part.Hidden > 0 || part.Unmeasured > 0) return false;
        if (part.Drawn <= 0) return false;
        if (float.IsFinite(part.Alpha) && part.Alpha <= CompletionBounds.VisibleAlpha) return false;
        return float.IsFinite(part.VisualBottom);
    }

    // The bottom edge the board may hang from for one part: the pixels it really draws when those are a *complete*
    // measurement of it, and its laid-out container rectangle otherwise. The second answer is never tightened: a part
    // with a hidden child, an unreadable alpha, a failed enumeration or no drawn rectangle keeps the container, so a
    // caption that has not arrived yet can never be covered by a band that was derived from its absence.
    public static float FinalEdge(in CompletionPart part, out bool fromContent)
    {
        fromContent = false;
        if (!part.Present || (!part.Active && !part.Scheduled)) return float.NaN;
        if (ContentComplete(part))
        {
            fromContent = true;
            return part.VisualBottom;
        }
        return float.IsFinite(part.OwnBottom) ? part.OwnBottom : float.NaN;
    }

    // The bottom edge of one part as the section will end up while the reveal is still running: the element's own
    // laid-out container rectangle, always. The drawn pixels (`VisualBottom`) never enter *this* reading - the reveal
    // switches parts on one after another, so a drawn edge taken mid-reveal is a progress report and not the page's
    // layout, and switching the band from the container to the first drawn pixel is exactly the upward jump that
    // re-measured a correctly placed board mid-reveal. `FinalEdge` below is the reading that may use the pixels, and
    // only for a part whose content is completely measured (`ContentComplete`).
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
    // switches slots on one after another, so anything drawn is a progress report and not the page's layout.
    //
    // Once that reveal has finished, every part contributes `FinalEdge` - the pixels it really draws when those are a
    // *complete* measurement of it (`ContentComplete`), its container rectangle otherwise - and the band is the deepest
    // of those. Both directions matter on the recorded page:
    //
    //   * a container can end *above* the captions its icon draws (313 vs 352), and a band hung from the container
    //     pressed the panel's own top edge on the native sector icons and their 主要/精英 captions;
    //   * a container can also end *far below* everything it draws - the recorded success page's icon container read
    //     692 while the icons and captions on the screenshot end around 417, some 275 px of blank slot - and a band
    //     hung from that reading shortened the strip for no visible reason, which is the small board the player
    //     reported. `max(container, pixels)` could never answer that case; `FinalEdge` can, and only for a part whose
    //     content is complete: every child enumerated, nothing under it switched off or faded out, at least one child
    //     drawn. A part with a hidden child (a caption the reveal has not shown yet, an unused icon of a short run)
    //     keeps its container, so nothing that can still appear is covered.
    //
    // `deepest` names the part that produced the answer, whichever reading won, so the region line stays true about the
    // number it prints. `laidOut` is `Bottom`'s own count of parts whose pixels have not arrived.
    public static float Placed(IReadOnlyList<CompletionPart> parts, bool revealFinished, out int deepest, out int laidOut)
        => Placed(parts, revealFinished, contentSettled: false, out deepest, out laidOut);

    // F3 (independent review): a page whose ledger will never answer - every target excluded, or one aborted - kept the
    // *container* edge for the whole visit, so the extra 275 px of blank slot never came back on exactly the pages the
    // ledger cannot finish. The content reading may be used before the reveal finishes when the ledger itself has
    // proved that no reveal is coming (`contentSettled`, decided by the caller from the ledger *and* the page's own
    // two-agreeing-readings latch), because at that point the pixels a part draws are not a progress report: nothing is
    // going to change them. Next to it, every part that is scheduled-but-not-switched-on still contributes its laid-out
    // slot, so the answer can never be above a slot that may still appear - the one thing a content reading must not do.
    public static float Placed(IReadOnlyList<CompletionPart> parts, bool revealFinished, bool contentSettled,
        out int deepest, out int laidOut)
    {
        float layout = Bottom(parts, out deepest, out laidOut);
        if (parts == null) return layout;
        if (!revealFinished && !contentSettled) return layout;
        float band = float.NaN;
        int winner = -1;
        for (int index = 0; index < parts.Count; index++)
        {
            float edge = FinalEdge(parts[index], out bool fromContent);
            if (!float.IsFinite(edge)) continue;
            if (float.IsFinite(band) && edge <= band) continue;
            band = edge;
            winner = index;
        }
        if (!float.IsFinite(band)) return layout;
        deepest = winner;
        return band;
    }

    // 1.19.52 (F1, the recorded 1080p success page): the floor the band may never rise above.
    //
    // `Placed` answers "where does the section end", and it answers with a *container* for every part whose child set
    // is not a complete measurement (`ContentComplete`), because a caption that has not been switched on yet may still
    // appear inside that container. That is the right answer for an edge and the wrong answer for a floor: the
    // recorded page read
    //
    //   sectorHeader  own=233 visual=233 hidden=0  drawn=5 complete=true
    //   sectorIconMain own=198 visual=333 hidden=14 drawn=6 complete=false
    //
    // so the band hung from 233 while the part that had really drawn six children reached 333 - the board's top edge
    // (249) and its whole plate (238..424) sat on icon captions the page had already painted, which is the
    // 主要/精英 block the player measured down to ~334. A container is not the only thing that can be wrong in that
    // direction: it can also end far below everything it draws, and taking `max` against every container would bring
    // back the 275 px blank slot this build already removed. The floor is therefore built from drawn pixels alone, and
    // only for a part that is on the page, switched on, measured, drew at least one child and has a visible alpha:
    // what such a part draws is on the screen now, whatever else it may still add.
    //
    // It is a floor and never the section's edge: the caller takes `max(Placed, DrawnFloor)`, so nothing here claims
    // the section is complete, and a NaN, an inactive part, a part that drew nothing or a hidden one contributes
    // nothing at all.
    public static float DrawnFloor(IReadOnlyList<CompletionPart> parts)
    {
        float floor = float.NaN;
        if (parts == null) return floor;
        for (int index = 0; index < parts.Count; index++)
        {
            CompletionPart part = parts[index];
            if (!part.Present || !part.Active || !part.Measured) continue;
            if (part.Drawn <= 0) continue;
            if (!float.IsFinite(part.VisualBottom)) continue;
            if (float.IsFinite(part.Alpha) && part.Alpha <= CompletionBounds.VisibleAlpha) continue;
            if (float.IsFinite(floor) && part.VisualBottom <= floor) continue;
            floor = part.VisualBottom;
        }
        return floor;
    }

    // The per-part reading the page prints once, so a live log alone can say which part produced the band and why:
    // its container, the pixels it draws, whether those were a complete measurement, how many children were switched
    // off, and the edge that was used. `[DIAG] success completion part: ...`
    //
    // F4 (independent review): the `source` word must describe the edge `Placed` really used on this frame, so the same
    // `revealFinished`/`contentSettled` pair is passed in. A reveal still running therefore prints `container` and the
    // container's number, not a content figure the placement never read.
    public static string Describe(in CompletionPart part, string name, bool revealFinished, bool contentSettled = false)
    {
        bool usesContent = revealFinished || contentSettled;
        float edge = usesContent ? FinalEdge(part, out _) : PartBottom(part, out _);
        return name
            + ": present=" + part.Present.ToString().ToLowerInvariant()
            + ", active=" + part.Active.ToString().ToLowerInvariant()
            + ", scheduled=" + part.Scheduled.ToString().ToLowerInvariant()
            + ", alpha=" + (float.IsFinite(part.Alpha) ? part.Alpha.ToString("0.00") : "unreadable")
            + ", own=" + Edge(part.OwnBottom)
            + ", visual=" + Edge(part.VisualBottom)
            + ", measured=" + part.Measured.ToString().ToLowerInvariant()
            + ", hidden=" + part.Hidden
            + ", unmeasured=" + part.Unmeasured
            + ", drawn=" + part.Drawn
            + ", complete=" + ContentComplete(part).ToString().ToLowerInvariant()
            + ", edge=" + Edge(edge)
            + ", source=" + (usesContent ? (ContentComplete(part) ? "content" : "container(incomplete)")
                : "container(reveal-running)");
    }
    private static string Edge(float value) => float.IsFinite(value) ? value.ToString("0") : "unmeasured";
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
//   * a ledger that can no longer finish answers immediately (`RevealImpossible`), exactly as every earlier build did.
//     That is now three facts about the ledger, never about time: a target the page handed over with no object, a
//     target count past the ledger's budget, and a native routine that really ended without revealing its target
//     (`1.19.43`). A routine that ended is not a routine that is still coming, and waiting for it was the one
//     permanent wait this gate still had: the success page stayed on its vanilla screen for the whole visit and only
//     F8 could release it. A target whose BlinkIn call handed it over but never scheduled a routine for it is not
//     counted as pending at all (`CompletionTargets.SchedulesClosed`), so a holder this build never blinks cannot
//     keep the gate shut either;
//   * F8 is the player's own answer and never waits (`Forced`).
//
// A page whose reveal never finishes at all is answered by the paths that already exist for it: the strict fit spends
// its bounded refusal budget (`ResultFallbackPolicy`) and the fail-open board - which steps around every control the
// page measured - stands in, logged as fail-open rather than as a proven page.
public static class ResultRevealGate
{
    public static bool BlocksPlacement(bool revealFinished, bool mayStillComplete, bool forced)
        => BlocksPlacement(revealFinished, mayStillComplete, inFlight: 0, forced);

    // `inFlight` is `BlinkCompletionLedger.InFlight`: the native routines that are still running right now.
    //
    // F1 (independent review): one aborted target makes `mayStillComplete` false, and the three-argument rule above
    // then released the board *on that frame* while five of the recorded page's six sector icons were still blinking -
    // "this ledger will never read `Ready`" is not "the page has stopped animating". A ledger that can no longer
    // complete but still has routines in flight therefore keeps the gate shut until they end, and the release that
    // follows is the explicitly labelled one (`WhyReleased`: placed from the laid-out slots, never from pixels a reveal
    // that ended did not draw). Nothing about that release claims the animation completed.
    public static bool BlocksPlacement(bool revealFinished, bool mayStillComplete, int inFlight, bool forced)
        => !forced && !revealFinished && (mayStillComplete || inFlight > 0);

    // The one line a withheld frame prints: the ledger's own counts, and what releases it.
    public static string Why(int completed, int count, int names, int artwork, int inFlight = 0)
        => "the page's own sector BlinkIn reveal has not finished (completed=" + completed + "/" + count
            + ", names=" + names + ", artwork=" + artwork + ", inFlight=" + inFlight + "), so the completion section is still being switched on; "
            + "the board is not placed against a section that is still arriving, and the native ledger itself releases "
            + "it - not a frame count, an alpha threshold or an empty scan. A ledger that can no longer finish is "
            + "answered once its own routines have ended, and F8 never waits";
    // The one line the release prints when the ledger could no longer finish: a native routine really ended without
    // revealing its target, or a target this page handed over never got a routine at all. Neither is a completion, and
    // neither is a timeout: the section is placed from the slots the page laid out, and the drawn pixels stay out of
    // the band because the reveal never finished.
    public static string WhyReleased(int completed, int count, int aborted, int unscheduled, int inFlight = 0)
        => "the page's own sector BlinkIn reveal can no longer complete (completed=" + completed + "/" + count
            + ", aborted=" + aborted + ", unscheduled=" + unscheduled + ", inFlight=" + inFlight + "), so the completion section is placed from the "
            + "slots the page laid out and never from pixels a reveal that ended did not draw; this is not a settled "
            + "reveal and not a timeout - a routine that ended is not a routine that is still coming, and F8 is untouched";
}

// -----------------------------------------------------------------------------------------------------------
// 1.19.60 (independent review F1): what this page's ledger has actually told us, in four states
// -----------------------------------------------------------------------------------------------------------
//
// The 1.19.60 delivery asked "may anything on this page still move the reveal?" with
//
//     revealDone || (!MayStillComplete && InFlight == 0) || (Count == 0 && FinalLayout.Ready)
//
// and the third term could not tell two completely different pages apart:
//
//   * `Required == 0` - this page has handed the ledger **nothing yet**. The native `BlinkIn` runs inside `OnEnable`
//     and the ledger is filled from in there, so the first frames of a live page legitimately read 0/0 while its six
//     targets are still to come. Answering "settled" there let a client's early board stand on a page whose reveal had
//     not started - the recorded defect, one frame earlier.
//   * `Required > 0 && Count == 0` - the page handed targets over and every one of them was proved *unscheduled*.
//     Nothing on this page can animate, so that really is its final geometry (`ContentSettled` has trusted it since
//     1.19.41).
//
// The four states below are the whole answer, and every branch that asks it reads it from this one place:
//
//   NotStarted    `Required == 0` and the page has not been watched long enough to say nothing is coming;
//   Running       something of the ledger's own still has to happen (a routine in flight, or a target that can still
//                 finish);
//   Finished      every counted target is done, or every handed-over target was proved unscheduled, and nothing of the
//                 ledger's own is still running;
//   Unobservable  `Required == 0` right across the bounded observation budget: this page's native callback never came.
//
// `Unobservable` is a bounded *retry* answer and never a claim: it has its own name in the log, it still demands the
// page's geometry latch (`ResultFinalGate.Ready`) before an edge is called final, and it never pretends a reveal
// completed. A page that registers its targets a moment later simply leaves this state on the frame they arrive,
// because the budget only counts observations in which `Required == 0` - it is "looked and saw nothing", not elapsed
// time.
public enum RevealObservation
{
    NotStarted = 0,
    Running = 1,
    Finished = 2,
    Unobservable = 3
}

public static class ResultRevealStatePolicy
{
    // How many live observations of `Required == 0` a page gets before its native callback is called unobservable. The
    // success page registers its targets from inside `BlinkIn`, which the page calls as it comes up, so a live page
    // that is still at zero after this many *observations* is not "about to start". The number is a poll count, not a
    // duration, because the panel's poll cadence is the only thing that decides how often this question is asked.
    public const int UnobservableObservations = 40;

    // The state this frame's ledger answers. Every argument is a reading of the ledger itself (`BlinkCompletionLedger`)
    // except `observationsWithoutTargets`, which the caller counts for the `Unobservable` bound and resets per page.
    public static RevealObservation Observe(bool revealFinished, int required, int count, int completed, int inFlight,
        bool mayStillComplete, bool settled, int observationsWithoutTargets)
    {
        if (revealFinished) return RevealObservation.Finished;
        if (required <= 0)
            return observationsWithoutTargets >= UnobservableObservations
                ? RevealObservation.Unobservable
                : RevealObservation.NotStarted;
        // The ledger can no longer finish and nothing of its own is running: the release path 1.19.43 named.
        if (!mayStillComplete && inFlight <= 0) return RevealObservation.Finished;
        // Every handed-over target was proved unscheduled. `InFlight` still has the last word, because a routine can
        // start late and would then be a target this answer has not seen yet.
        if (count <= 0) return inFlight > 0 ? RevealObservation.Running : RevealObservation.Finished;
        // Targets exist and none of them has finished: this is the recorded `0/6`, and the frame a page registers its six
        // targets on. Nothing but the ledger's own completion count may call that finished.
        if (inFlight > 0 || !settled) return RevealObservation.Running;
        return completed >= count ? RevealObservation.Finished : RevealObservation.Running;
    }

    // The reveal has answered: it finished, or it proved nothing of its own is still running. Only this state may be
    // called final on the ledger's word alone.
    public static bool Answered(RevealObservation state) => state == RevealObservation.Finished;

    // `Unobservable` is the bounded retry: the caller must still prove the page's geometry (its own two-agreeing-readings
    // latch), so this answers "there is nothing more to wait for", never "the edges are final".
    public static bool Bounded(RevealObservation state)
        => state == RevealObservation.Finished || state == RevealObservation.Unobservable;

    public static string Name(RevealObservation state)
    {
        switch (state)
        {
            case RevealObservation.Finished: return "finished";
            case RevealObservation.Running: return "running";
            case RevealObservation.Unobservable: return "unobservable";
            default: return "not-started";
        }
    }
}


// How long the success page's own reveal may make **no progress at all** before the fail-open path is allowed to answer.
//
// F1-STALL (independent review): a ledger where every target was scheduled, none aborted, and one routine simply never
// ends leaves `RevealImpossible == false` with `InFlight > 0`. The frame-based wait this replaces only counted the
// "aborted but still running" shape, so that page waited for F8 for ever. This clock answers the real question - "has
// this reveal moved at all recently?" - in **real seconds**, never in frames, so it behaves the same at 60 and 134 FPS:
//
//   * any new native fact is progress and restarts the clock: a target entering the ledger, a routine starting, a
//     routine finishing (`Completed`/`InFlight`/counts change);
//   * the bound is derived from the page's own native length, not a hard number of frames: `BlinkDuration` is what one
//     blink takes, the recorded page has six of them, and the limit is twice that whole sequence, clamped to
//     [`MinimumStallSeconds`, `MaximumStallSeconds`];
//   * exhausting it never says the reveal finished. The only answer it releases is the explicitly labelled
//     `RevealUnobservable` fail-open, which keeps stepping around every measured control and measuring again.
//
// Free of Unity: the caller passes `now` (production passes `Time.realtimeSinceStartup`) and the blink duration, so the
// whole rule is asserted headlessly instead of re-implemented in a test.
public sealed class RevealProgressClock
{
    // The recorded success page shows six sector targets; six blinks are one whole reveal.
    public const float BlinksPerReveal = 6f;
    // Twice that sequence is the stall bound: a page that has moved nothing for two whole native reveals has stopped.
    public const float StallFactor = 2f;
    // A blink that cannot be read is about half a second on this build; the floor keeps a very short native duration from
    // turning the bound into a single frame.
    public const float AssumeBlinkSeconds = 0.5f;
    public const float MinimumStallSeconds = 3f;
    public const float MaximumStallSeconds = 30f;

    public static float LimitFor(float blinkDurationSeconds)
    {
        float blink = float.IsFinite(blinkDurationSeconds) && blinkDurationSeconds > 0f ? blinkDurationSeconds : AssumeBlinkSeconds;
        float limit = blink * BlinksPerReveal * StallFactor;
        if (!(limit > MinimumStallSeconds)) limit = MinimumStallSeconds;
        if (limit > MaximumStallSeconds) limit = MaximumStallSeconds;
        return limit;
    }

    private float _lastProgress = float.NaN;
    private int _completed = -1, _inFlight = -1, _scheduled = -1;

    // One observation of the ledger. Answers whether this observation was progress (a new native fact).
    public bool Observe(float now, int completed, int inFlight, int scheduled)
    {
        bool progress = !float.IsFinite(_lastProgress)
            || completed != _completed || inFlight != _inFlight || scheduled != _scheduled;
        if (progress) _lastProgress = now;
        _completed = completed; _inFlight = inFlight; _scheduled = scheduled;
        return progress;
    }

    // A different page: nothing of the previous reveal's clock survives it.
    public void Forget() { _lastProgress = float.NaN; _completed = _inFlight = _scheduled = -1; }

    public float StalledSeconds(float now)
        => float.IsFinite(_lastProgress) && float.IsFinite(now) ? Math.Max(0f, now - _lastProgress) : 0f;

    // Whether the reveal has made no progress for the whole bound. A clock that was never observed is not exhausted:
    // "not watched yet" is not "stalled".
    public bool Exhausted(float now, float limit)
    {
        if (!float.IsFinite(_lastProgress) || !float.IsFinite(now)) return false;
        float bound = limit > 0f ? limit : MinimumStallSeconds;
        return StalledSeconds(now) >= bound;
    }
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
    // `revealImpossible` the ledger can never read `Ready` (`BlinkCompletionLedger.RevealImpossible`);
    // `revealInFlight`   and the routines that are still running right now: an impossible ledger is only *answered*
    //                    once this is zero (F1), never on the frame one of its targets aborted;
    // `revealCount`      targets the ledger was handed this visit; zero means the reveal was never observed;
    // `revealWaitFrames` frames spent waiting on a reveal that cannot answer by itself (never observed, or impossible
    //                    with routines still in flight - the two cases no callback can end);
    // the rest are `ResultFinalPolicy`'s own facts, unchanged.
    public static Decision Decide(bool forced, bool revealEnforced, bool revealFinished, bool revealImpossible,
        int revealCount, int revealWaitFrames, bool everMeasurable, bool finalMeasurable, bool finalSettled,
        int finalWaitFrames)
        => Decide(forced, revealEnforced, revealFinished, revealImpossible, revealInFlight: 0, revealCount,
            revealStalled: revealWaitFrames >= ResultFinalPolicy.MaximumWaitFrames,
            everMeasurable, finalMeasurable, finalSettled, finalWaitFrames);

    // `revealStalled` is `RevealProgressClock.Exhausted` for this page: the reveal has made **no progress at all** for
    // the whole bound, measured in real seconds from the page's own native blink duration.
    //
    // F1-STALL (independent review): the answer is the same for every shape of "this reveal will not answer by itself" -
    // never observed, aborted with routines still running, and (the shape the frame count used to miss) all six targets
    // scheduled, none aborted, one routine that simply never ends. In every one of them the only release is the
    // explicitly labelled `RevealUnobservable`; a reveal that is still moving is never released by time, because every
    // new native fact restarts the clock.
    public static Decision Decide(bool forced, bool revealEnforced, bool revealFinished, bool revealImpossible,
        int revealInFlight, int revealCount, bool revealStalled, bool everMeasurable, bool finalMeasurable,
        bool finalSettled, int finalWaitFrames)
    {
        if (forced) return new Decision(true, Answer.Forced);
        if (revealEnforced)
        {
            // An impossible ledger is answered only when nothing of its own is still animating. Until then the page is
            // still arriving, and this is the same statement `ResultRevealGate` makes for the strict placement.
            if (revealImpossible && revealInFlight <= 0) return new Decision(true, Answer.RevealImpossible);
            if (!revealFinished)
            {
                if (revealStalled) return new Decision(true, Answer.RevealUnobservable);
                return new Decision(false, Answer.RevealPending);
            }
        }
        if (!ResultFinalPolicy.FailOpenAllowed(everMeasurable, finalMeasurable, finalSettled, false, finalWaitFrames))
            return new Decision(false, Answer.FinalPending);
        if (!everMeasurable) return new Decision(true, Answer.NoFinalGeometry);
        return new Decision(true, finalSettled ? Answer.Settled : Answer.GivenUp);
    }

    // The one line an unobservable fail-open prints, in real seconds: which of the three shapes it was, and how long the
    // reveal has stood still. It never says the animation finished.
    public static string Why(Answer answer, int completed, int count, int inFlight, float stalledSeconds, float limitSeconds)
    {
        string stalled = "no progress for " + (float.IsFinite(stalledSeconds) ? stalledSeconds.ToString("0.0") : "unmeasured")
            + "s of " + (float.IsFinite(limitSeconds) ? limitSeconds.ToString("0.0") : "unmeasured") + "s";
        if (answer != Answer.RevealUnobservable)
            return "the page's own sector BlinkIn reveal has not finished (completed=" + completed + "/" + count
                + ", " + stalled + "), so the fail-open band may not answer either: the refusal budget answers the page's "
                + "geometry, never its reveal, and a board drawn at a container edge the reveal has not reached is the board "
                + "that is re-measured 39 px lower a moment later";
        string shape = count <= 0
            ? "was never observed on this visit"
            : revealShapeInFlight(inFlight)
                ? "is still running " + inFlight + " routine(s) that have not reported"
                : "can no longer complete";
        return "the page's own sector BlinkIn reveal " + shape + " (completed=" + completed + "/" + count
            + ", inFlight=" + inFlight + ", " + stalled + "), so the board is drawn from the fail-open band - a "
            + "reveal-unobservable fail-open, not a settled page and not a finished animation: every control the page "
            + "measures is still stepped around and the page is measured again";
    }
    private static bool revealShapeInFlight(int inFlight) => inFlight > 0;

    // The line the older callers ask for, still in frames: kept for the tests that pin the frame-shaped wording.
    public static string Why(Answer answer, int completed, int count, int waitFrames, int inFlight = 0)
    {
        if (answer != Answer.RevealUnobservable)
            return "the page's own sector BlinkIn reveal has not finished (completed=" + completed + "/" + count
                + ", waited=" + waitFrames + " of " + ResultFinalPolicy.MaximumWaitFrames + " frames), so the fail-open band may not "
                + "answer either: the refusal budget answers the page's geometry, never its reveal, and a board drawn at a container "
                + "edge the reveal has not reached is the board that is re-measured 39 px lower a moment later";
        return count <= 0
            ? "the page's own sector BlinkIn reveal was never observed on this visit (completed=" + completed + "/" + count
              + ", waited=" + waitFrames + " of " + ResultFinalPolicy.MaximumWaitFrames + " frames), so the board is drawn from the "
              + "fail-open band - a reveal-unobservable fail-open, not a settled page: nothing in this window proves the completion "
              + "section stopped changing"
            : "the page's own sector BlinkIn reveal can no longer complete and its routines are still in flight (completed="
              + completed + "/" + count + ", inFlight=" + inFlight + ", waited=" + waitFrames + " of "
              + ResultFinalPolicy.MaximumWaitFrames + " frames), so the board is drawn from the fail-open band - a "
              + "reveal-unobservable fail-open, not a settled page and not a finished animation";
    }
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
