using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using HawChow.GTFOCombatPanel;

// The reveal that could never finish, the gear name the wire cut in half, and the packet lines that repeated for a
// whole expedition. All three come from the same recorded session:
//
//   A  `BepInEx\LogOutput.log` 17566-17615 (2560x1440, client, run 4): the success board was fitted
//      with the native BlinkIn ledger at `completed=0/6`, the reveal then reached 6/6 and the band moved
//      713..1295 -> 726..1321. The gate that refuses that first placement is `ResultRevealGate`, and the one wait it
//      could never leave was a ledger that had been handed targets but where a target received no routine at all, or a
//      routine really ended without revealing its target: `MayStillComplete` stayed true for the whole visit and only
//      F8 could answer. Both are now native facts about the ledger, asserted here through the production types
//      (`BlinkCompletionLedger`, `ResultRevealGate`, `ResultSettlementGate`, `ResultFinalPolicy`).
//   B  run 4's archived settlement board shows `Long Gun<` in the 副武器 row: the game's own name is
//      `<color=#FF4005>Long Gun</color>` and the wire carries 24 UTF-8 bytes per gear name (`SyncProtocol.NameBytes`),
//      so the cut landed on the opening '<' of the closing tag. The table draws with `richText = false`, so that
//      residue printed itself. `CombatStatsFormatter.VisibleName` is the display-layer answer, and `ImpactBoard` is
//      the one place the four gear rows are built.
//   C  634 of the session's 70,739 lines are this mod's; 243 of those were three shapes that repeat per packet
//      (`squad sync supply kinds` 161, `squad sync row` 49, `squad sync snapshot` 33). `SyncLogPolicy` bounds them per
//      peer and per kind, keeps the first line and every state change, and never drops an anomaly.
//
// Nothing here re-implements a production flow: every assertion calls the types production calls, and the last block
// reads the production sources to show they are wired that way. Unity-side facts (the real page, the real coroutine)
// are static source checks - they cannot be reached from this process, and are not claimed as runtime proof.
internal static class RevealNameAndLogPolicyScenarios
{
    // The recorded success page: four sector icons whose reveal the ledger was handed, and the two captions beside them.
    private static readonly long MainIcon = 0x9A10, SecondIcon = 0x9A20, ThirdIcon = 0x9A30, AllIcon = 0x9A40;
    private const long Caption = 0x9A50, SecondCaption = 0x9A60;
    private const long MainRoutine = 0x7001, SecondRoutine = 0x7002, ThirdRoutine = 0x7003, AllRoutine = 0x7004,
        CaptionRoutine = 0x7005, SecondCaptionRoutine = 0x7006;

    public static void RunInto(Action<bool, string> check)
    {
        SuccessRevealFinishesNormally(check);
        RevealThatCannotFinish(check);
        RevealThatStopsMakingProgress(check);
        LateNativeControlStaysSafe(check);
        PageReentryAndF8(check);
        CompletionBandUsesWhatCouldBeMeasured(check);
        RecordedFailureBandIsCutAtTheButton(check);
        FailureWallGiveUpKeepsItsName(check);
        GearNameDisplay(check);
        PacketLogPolicy(check);
        ProductionWiring(check);
        Console.WriteLine("WIRING: REVEAL_PERMANENT_WAIT + REVEAL_STALL_BOUND + GEAR_NAME_RESIDUE + PACKET_LOG_BOUND (static source checks for the Unity side; the policies above are the production ones)");
    }

    // ---------------------------------------------------------------------------------------------------
    // A1. 实机成功页 0/6 -> 6/6：闸门在原生 reveal 完成前拦，完成后放；没有帧数
    // ---------------------------------------------------------------------------------------------------
    private static void SuccessRevealFinishesNormally(Action<bool, string> check)
    {
        var ledger = new BlinkCompletionLedger();
        RequireAll(ledger);
        check(!ledger.Ready && ledger.Count == 6 && ledger.MayStillComplete,
            "the recorded success page's six targets are all part of the reveal, and none of them has finished on the first frame");

        var readiness = new CompletionReadiness();
        readiness.Bind(new IntPtr(0x51CCE55));
        check(!Reveal(readiness, ledger) && ResultRevealGate.BlocksPlacement(false, ledger.MayStillComplete, forced: false),
            "so the first placement is withheld by the native ledger itself - the frame the old build fitted a board at completed=0/6");

        StartAll(ledger);
        check(!Reveal(readiness, ledger) && ResultRevealGate.BlocksPlacement(false, ledger.MayStillComplete, forced: false),
            "a scheduled routine is not a finished one: the gate stays shut while they run");

        FinishAll(ledger, naturallyRevealed: true);
        check(ledger.Ready && ledger.Completed == 6 && ledger.Aborted == 0 && Reveal(readiness, ledger)
              && !ResultRevealGate.BlocksPlacement(true, ledger.MayStillComplete, forced: false),
            "and the board is released on the frame the game's own routines report completion - completed=6/6, exactly the transition the log recorded");

        // The success page's fail-open path asks the same ledger: with the reveal finished it is answered by geometry.
        ResultSettlementGate.Decision settled = ResultSettlementGate.Decide(forced: false, revealEnforced: true,
            revealFinished: true, revealImpossible: false, revealCount: ledger.Count, revealWaitFrames: 0,
            everMeasurable: true, finalMeasurable: true, finalSettled: true, finalWaitFrames: 0);
        check(settled.MayFailOpen && settled.Why == ResultSettlementGate.Answer.Settled,
            "and the fail-open decision answers on geometry once the reveal is finished, as it always did");
    }

    // ---------------------------------------------------------------------------------------------------
    // A2. 永久等待：没有协程的目标 / 协程真的结束却没展示 / 目标被销毁
    // ---------------------------------------------------------------------------------------------------
    private static void RevealThatCannotFinish(Action<bool, string> check)
    {
        // (a) A holder this build's BlinkIn never hands to SetActiveBlink. Before 1.19.43 it stayed required and
        // pending for the whole visit.
        var unscheduled = new BlinkCompletionLedger();
        RequireAll(unscheduled);
        int skipped = unscheduled.CloseSchedule(new[] { MainIcon, SecondIcon, ThirdIcon, AllIcon, Caption });
        check(skipped == 5 && unscheduled.Unscheduled == 5 && unscheduled.Count == 1 && unscheduled.Required == 6,
            "a BlinkIn call that hands over five targets and schedules none of them leaves them out of the reveal it waits on, while the sixth (this call's own icon) is still pending");
        check(unscheduled.MayStillComplete && !unscheduled.Ready,
            "and the page is still waiting on the one target that really was scheduled - skipping is not a completion");
        StartAll(unscheduled);
        check(unscheduled.Unscheduled == 0 && unscheduled.Count == 6,
            "a routine that arrives later re-admits its target, so a late native control is never ignored");
        FinishAll(unscheduled, naturallyRevealed: true);
        check(unscheduled.Ready && unscheduled.Completed == 6,
            "and the reveal then finishes exactly as the game reports it");

        // (b) Every target this page's reveal was handed ended without scheduling a routine: the fail-open wait is the
        // page's own bounded answer, and it says so rather than claiming a settled reveal.
        var neverObserved = new BlinkCompletionLedger();
        RequireAll(neverObserved);
        neverObserved.CloseSchedule(new[] { MainIcon, SecondIcon, ThirdIcon, AllIcon, Caption, SecondCaption });
        check(neverObserved.Count == 0 && neverObserved.Ready == false && neverObserved.MayStillComplete,
            "a page whose BlinkIn call scheduled nothing at all has an empty reveal, not a finished one");
        ResultSettlementGate.Decision early = ResultSettlementGate.Decide(forced: false, revealEnforced: true,
            revealFinished: false, revealImpossible: neverObserved.RevealImpossible, revealCount: neverObserved.Count,
            revealWaitFrames: 0, everMeasurable: true, finalMeasurable: true, finalSettled: true, finalWaitFrames: 0);
        ResultSettlementGate.Decision spent = ResultSettlementGate.Decide(forced: false, revealEnforced: true,
            revealFinished: false, revealImpossible: neverObserved.RevealImpossible, revealCount: neverObserved.Count,
            revealWaitFrames: ResultFinalPolicy.MaximumWaitFrames, everMeasurable: true, finalMeasurable: true,
            finalSettled: true, finalWaitFrames: 0);
        check(!early.MayFailOpen && early.Why == ResultSettlementGate.Answer.RevealPending
              && spent.MayFailOpen && spent.Why == ResultSettlementGate.Answer.RevealUnobservable && spent.Unobservable,
            "an unobserved reveal is withheld at first and then answered as reveal-unobservable after the one bounded wait this file already has - never as settled");

        // (c) A routine that really ended without revealing its target: the wait ends at once, because nothing else is
        // coming, and the ledger still refuses to call that reveal finished.
        var aborted = new BlinkCompletionLedger();
        RequireAll(aborted);
        StartAll(aborted);
        aborted.Finish(ThirdIcon, ThirdRoutine, naturallyRevealed: false);
        check(aborted.MayStillComplete == false && aborted.RevealImpossible && aborted.Aborted == 1 && !aborted.Ready,
            "a routine that ended without revealing its target makes the wait impossible at once, and the reveal is still not finished");
        check(!ResultRevealGate.BlocksPlacement(false, aborted.MayStillComplete, forced: false),
            "so the success page's own gate releases on the native ledger's answer instead of holding the vanilla screen for the rest of the visit");
        ResultSettlementGate.Decision released = ResultSettlementGate.Decide(forced: false, revealEnforced: true,
            revealFinished: false, revealImpossible: aborted.RevealImpossible, revealCount: aborted.Count,
            revealWaitFrames: 0, everMeasurable: true, finalMeasurable: true, finalSettled: false, finalWaitFrames: 0);
        check(released.MayFailOpen && released.Why == ResultSettlementGate.Answer.RevealImpossible,
            "and the fail-open decision answers it on the first frame, not after sixty refusals or a timeout");

        // (d) The same fact when the target GameObject has already been destroyed and can no longer be named: the
        // routine pointer still identifies the row.
        var destroyed = new BlinkCompletionLedger();
        RequireAll(destroyed);
        StartAll(destroyed);
        check(destroyed.FinishRoutine(AllRoutine, naturallyRevealed: false) && destroyed.Aborted == 1 && destroyed.RevealImpossible,
            "a routine whose target object is gone ends the same row, so a destroyed icon cannot leave the page waiting for a callback that will never come");
        destroyed.Start(AllIcon, AllRoutine + 100);
        check(destroyed.MayStillComplete && destroyed.Aborted == 0,
            "a new routine for the same target re-opens the wait: the answer is about the routines that exist, not a latch");

        // (e) No completion is ever faked for the paths above.
        check(aborted.Completed == 0 && aborted.Count == 6 && aborted.Aborted == 1 && !aborted.Ready,
            "an ended routine is counted as neither completed nor finished: completed stays at 0/6 while the abort is its own figure");
    }

    // ---------------------------------------------------------------------------------------------------
    // A2b. F1-STALL：六个目标全部 Start、没有任何 abort、其中一个永不 Finish
    //
    //      独立评审的逐行演算：这样的账本 RevealImpossible=false、InFlight>0，而旧的 RevealWaitFrames 只在
    //      「RevealImpossible && InFlight>0」时计时，所以它永远是 0，两个超时分支都进不去，成功页只剩 F8。
    //      这里断言的是生产类型自己（`RevealProgressClock` + `ResultSettlementGate`），不是测试里复刻的算术。
    // ---------------------------------------------------------------------------------------------------
    private static void RevealThatStopsMakingProgress(Action<bool, string> check)
    {
        var ledger = new BlinkCompletionLedger();
        RequireAll(ledger);
        StartAll(ledger);
        check(ledger.InFlight == 6 && ledger.Aborted == 0 && ledger.Completed == 0
              && !ledger.RevealImpossible && ledger.MayStillComplete && !ledger.Ready,
            "the shape the old frame count could not see: all six targets were scheduled, none aborted, so the ledger can still complete and the reveal is not ready");

        // 界限来自这一页自己的原生时长，秒制：134 FPS 和 60 FPS 得到同一个答案，180 帧不是「动画结束」。
        const float blink = 0.62f;
        float limit = RevealProgressClock.LimitFor(blink);
        check(Near(limit, blink * RevealProgressClock.BlinksPerReveal * RevealProgressClock.StallFactor)
              && Near(RevealProgressClock.LimitFor(0.01f), RevealProgressClock.MinimumStallSeconds)
              && Near(RevealProgressClock.LimitFor(600f), RevealProgressClock.MaximumStallSeconds)
              && Near(RevealProgressClock.LimitFor(float.NaN),
                  RevealProgressClock.AssumeBlinkSeconds * RevealProgressClock.BlinksPerReveal * RevealProgressClock.StallFactor),
            "the stall bound is the page's own blink length times the six blinks it has to play, clamped to [" +
            RevealProgressClock.MinimumStallSeconds.ToString("0") + ", " + RevealProgressClock.MaximumStallSeconds.ToString("0") +
            "] s - and a build that cannot read the blink duration falls back to the documented half second, never to a frame count");

        var clock = new RevealProgressClock();
        float now = 100f;
        check(clock.Observe(now, ledger.Completed, ledger.InFlight, ledger.Count) && !clock.Exhausted(now, limit),
            "the first observation starts the clock, and a reveal that has just been observed is not stalled");
        check(!clock.Exhausted(now + limit * 0.99f, limit) && clock.Exhausted(now + limit, limit),
            "the bound is spent at the limit exactly - " + limit.ToString("0.0") + " s without a single native fact, not one frame earlier");

        // 134 FPS、每一半个界限就有一次原生进展（一个 routine 真的结束）：时钟不断被推后，绝不提前放板。
        long[] targets = { MainIcon, SecondIcon, ThirdIcon, AllIcon, Caption, SecondCaption };
        long[] routines = { MainRoutine, SecondRoutine, ThirdRoutine, AllRoutine, CaptionRoutine, SecondCaptionRoutine };
        float t = now;
        int resets = 0, frames = 0;
        bool releasedWhileMoving = false;
        for (int step = 0; step < 5; step++)
        {
            for (float stop = t + limit * 0.5f; t < stop; t += 1f / 134f)
            {
                clock.Observe(t, ledger.Completed, ledger.InFlight, ledger.Count);
                frames++;
                bool stalledNow = clock.Exhausted(t, limit);
                if (stalledNow || ResultSettlementGate.Decide(false, true, ledger.Ready, ledger.RevealImpossible,
                        ledger.InFlight, ledger.Count, stalledNow, true, true, false, 0).MayFailOpen) releasedWhileMoving = true;
            }
            ledger.Finish(targets[step], routines[step], naturallyRevealed: true);
            if (clock.Observe(t, ledger.Completed, ledger.InFlight, ledger.Count) && Near(clock.StalledSeconds(t), 0f)) resets++;
        }
        check(frames > 500 && resets == 5 && !releasedWhileMoving && ledger.Completed == 5 && ledger.InFlight == 1 && !ledger.Ready,
            "a reveal that really is moving is never released by time: " + frames + " frames at 134 FPS, five native steps over five half bounds, each one restarting the clock (" + resets + " of them), and not one frame answered");

        // 最后一个 routine 永不回来：从它最后一个原生事实起，这一页一点进展都没有。
        float stalledFrom = t;
        bool answeredEarly = false;
        for (; t < stalledFrom + limit * 0.99f; t += 1f / 134f)
        {
            if (clock.Observe(t, ledger.Completed, ledger.InFlight, ledger.Count) || clock.Exhausted(t, limit)) answeredEarly = true;
        }
        check(!answeredEarly, "a reveal that stopped moving is still not answered one frame short of the whole bound");
        t = stalledFrom + limit;
        clock.Observe(t, ledger.Completed, ledger.InFlight, ledger.Count);
        bool stalled = clock.Exhausted(t, limit);
        ResultSettlementGate.Decision held = ResultSettlementGate.Decide(false, true, ledger.Ready, ledger.RevealImpossible,
            ledger.InFlight, ledger.Count, stalled, true, true, false, 0);
        string heldWhy = ResultSettlementGate.Why(held.Why, ledger.Completed, ledger.Count, ledger.InFlight,
            clock.StalledSeconds(t), limit);
        check(stalled && held.MayFailOpen && held.Unobservable && held.RevealHeld && !ledger.Ready
              && held.Why == ResultSettlementGate.Answer.RevealUnobservable,
            "one of six routines that simply never ends is answered after the whole stall bound - as reveal-unobservable, never as a finished reveal (F1-STALL)");
        check(heldWhy.Contains("reveal-unobservable") && heldWhy.Contains("not a settled page")
              && heldWhy.Contains("still running 1 routine") && heldWhy.Contains("no progress for"),
            "and the line says which of the three shapes it was and how long it stood still (" + clock.StalledSeconds(t).ToString("0.0")
            + " s of " + limit.ToString("0.0") + " s), so a stuck page is readable from the log alone");
        check(ResultSettlementGate.Decide(true, true, ledger.Ready, ledger.RevealImpossible, ledger.InFlight, ledger.Count,
                  stalled, true, true, false, 0).Why == ResultSettlementGate.Answer.Forced,
            "F8 answers on the same frame, before the bound is even asked");
        check(ResultRevealGate.BlocksPlacement(ledger.Ready, ledger.MayStillComplete, ledger.InFlight, forced: false),
            "and the strict placement is still withheld: a bounded fail-open is not a placement against a reveal that has not finished");

        // 迟到的 Finish：同一页在下一帧恢复成普通结论，并且是真的 Ready（不是「时间到了」）。
        ledger.Finish(SecondCaption, SecondCaptionRoutine, naturallyRevealed: true);
        t += 1f / 134f;
        clock.Observe(t, ledger.Completed, ledger.InFlight, ledger.Count);
        ResultSettlementGate.Decision resumed = ResultSettlementGate.Decide(false, true, ledger.Ready, ledger.RevealImpossible,
            ledger.InFlight, ledger.Count, clock.Exhausted(t, limit), true, true, true, 0);
        check(ledger.Ready && ledger.Completed == 6 && ledger.InFlight == 0 && resumed.MayFailOpen
              && resumed.Why == ResultSettlementGate.Answer.Settled && !resumed.Unobservable,
            "a routine that finally reports turns the very same page into the ordinary settled answer on the next frame: the give-up was a waiting state, not a verdict");
        check(!ResultRevealGate.BlocksPlacement(ledger.Ready, ledger.MayStillComplete, ledger.InFlight, forced: false),
            "and the strict gate releases on the ledger's own completion, so the page is placed from the pixels the reveal really drew");

        // 换页：上一页的秒数不能穿过下一页。
        clock.Forget();
        check(!clock.Exhausted(t + 900f, limit) && Near(clock.StalledSeconds(t + 900f), 0f),
            "a new result page starts its own clock: nothing of the previous visit can release it");
        check(!ResultSettlementGate.Decide(false, true, false, false, 6, 6, clock.Exhausted(t, limit), true, true, false, 0).MayFailOpen,
            "and the new page's first frame is a reveal-pending frame again, exactly as the first visit's was");
    }

    // ---------------------------------------------------------------------------------------------------
    // B3. F5：未观测墙的预算用完之后，那条兜底成功的日志仍然写着它没有测量到墙
    // ---------------------------------------------------------------------------------------------------
    private static void FailureWallGiveUpKeepsItsName(Action<bool, string> check)
    {
        check(ResultFailureWallPolicy.Unobserved == "wall-not-observed"
              && ResultFailureWallPolicy.Waits(false, ResultFailureWallPolicy.UnobservedBudget - 1, false)
              && !ResultFailureWallPolicy.Waits(false, ResultFailureWallPolicy.UnobservedBudget, false)
              && !ResultFailureWallPolicy.Waits(true, 0, false)
              && ResultFailureWallPolicy.Answer(false, ResultFailureWallPolicy.UnobservedBudget, false),
            "a host failure page with no measured lower bound is withheld for exactly one give-up budget and answered on it");
        string waiting = ResultFailureWallPolicy.AnswerNote(wallMeasured: false, framesWithoutWall: ResultFailureWallPolicy.UnobservedBudget - 1, guestFailure: false);
        string answered = ResultFailureWallPolicy.AnswerNote(wallMeasured: false, framesWithoutWall: ResultFailureWallPolicy.UnobservedBudget, guestFailure: false);
        check(waiting.Length == 0 && answered.Contains("wallUnobserved=true")
              && answered.Contains(ResultFailureWallPolicy.UnobservedBudget + " frames")
              && answered.Contains("bounded give-up"),
            "the note on the band that is answered without a measured wall keeps saying the wall was never observed, and that this is a bounded give-up rather than the page's proven geometry (F5)");
        check(ResultFailureWallPolicy.AnswerNote(wallMeasured: true, framesWithoutWall: 900, guestFailure: false).Length == 0
              && ResultFailureWallPolicy.AnswerNote(wallMeasured: true, framesWithoutWall: 900, guestFailure: true).Length == 0,
            "a page that really measured its wall carries no such note, whatever the frame count and whoever the page belongs to");
    }

    // ---------------------------------------------------------------------------------------------------
    // A3. 迟到控件与已站住的板：闸门只管首次落板，安全策略仍会移动
    // ---------------------------------------------------------------------------------------------------
    private static void LateNativeControlStaysSafe(Action<bool, string> check)
    {
        // The recorded repaint: the band moved from 713..1295 to 726..1321 after the reveal finished.
        check(ResultLateControlPolicy.MustMove(713f, 1100f, 726f, 1321f),
            "a band whose top slid down past a standing board's top still forces the move: the reveal's arrival is not the page moving away");
        check(!ResultLateControlPolicy.MustMove(726f, 1300f, 726.4f, 1321f),
            "a band that moved by less than half a pixel does not move a table the player is reading");
        // Past the gate the drawn pixels are the section's real extent, so a caption deeper than its container is kept.
        var parts = new List<CompletionPart>
        {
            new CompletionPart(true, true, 1f, 313f, 352f),   // container 313, captions drawn to 352 (the recorded page)
            new CompletionPart(true, true, 1f, 292f, 292f),
            CompletionPart.Missing
        };
        float beforeReveal = CompletionFinalBounds.Placed(parts, revealFinished: false, out _, out _);
        float afterReveal = CompletionFinalBounds.Placed(parts, revealFinished: true, out int deepest, out _);
        check(Near(beforeReveal, 313f), "while the reveal runs the section's edge is the laid-out slot alone (313)");
        check(Near(afterReveal, 352f) && deepest == 0,
            "and once it has finished the deeper of the slot and the pixels it really draws is used (352), named by the part that produced it");
        check(afterReveal >= beforeReveal,
            "the finished reading is never *above* the laid-out one, so a padded container can never be traded for less room than the section occupies");
        // A container padded below everything it draws: the drawn reading is deeper than the slot, so the band follows
        // the content and never presses on it.
        var padded = new List<CompletionPart> { new CompletionPart(true, true, 1f, 300f, 340f) };
        check(Near(CompletionFinalBounds.Placed(padded, revealFinished: true, out _, out _), 340f),
            "a container that ends above the captions its icon draws keeps the captions clear");
        // And a part that draws nothing yet contributes its slot, never a zero.
        var undrawn = new List<CompletionPart> { new CompletionPart(true, false, 0f, 366f, float.NaN, scheduled: true) };
        check(Near(CompletionFinalBounds.Placed(undrawn, revealFinished: false, out _, out _), 366f),
            "a slot the page scheduled but has not switched on yet contributes the rectangle it will occupy");
        // A part this page never scheduled (an unused sector icon of a short run) contributes nothing at all.
        var unused = new List<CompletionPart> { new CompletionPart(true, false, 0f, 900f, float.NaN, scheduled: false) };
        check(float.IsNaN(CompletionFinalBounds.Placed(unused, revealFinished: false, out _, out _)),
            "and a slot this page never scheduled cannot push the board down");
    }

    // ---------------------------------------------------------------------------------------------------
    // A4. 下一页 / F8 / 重新进入
    // ---------------------------------------------------------------------------------------------------
    private static void PageReentryAndF8(Action<bool, string> check)
    {
        var ledger = new BlinkCompletionLedger();
        RequireAll(ledger);
        StartAll(ledger);
        ledger.Finish(ThirdIcon, ThirdRoutine, naturallyRevealed: false);
        check(ledger.RevealImpossible, "the first visit ended with a reveal that cannot finish");
        ledger.Clear();
        check(ledger.Count == 0 && ledger.Required == 0 && ledger.Aborted == 0 && ledger.Unscheduled == 0
              && ledger.MayStillComplete && !ledger.Ready,
            "entering the next result page clears every one of those facts: the new page is 'not observed yet', never 'impossible'");
        check(!ResultRevealGate.BlocksPlacement(false, ledger.MayStillComplete, forced: true),
            "F8 is the player's own answer and is never withheld by the reveal, on any page");
        ResultSettlementGate.Decision forced = ResultSettlementGate.Decide(forced: true, revealEnforced: true,
            revealFinished: false, revealImpossible: false, revealCount: 0, revealWaitFrames: 0,
            everMeasurable: false, finalMeasurable: false, finalSettled: false, finalWaitFrames: 0);
        check(forced.MayFailOpen && forced.Why == ResultSettlementGate.Answer.Forced,
            "and the settlement gate answers F8 first, before it asks anything about the ledger or the geometry");
        // A page whose ledger was handed nothing this visit is the one case the bounded wait may answer.
        var empty = new BlinkCompletionLedger();
        check(empty.Count == 0 && empty.MayStillComplete && !empty.RevealImpossible,
            "an empty ledger is not an impossible one");
    }

    // ---------------------------------------------------------------------------------------------------
    // B1. 成功页的可用条带：只对可测的真实内容收紧，容器空白不硬编码
    // ---------------------------------------------------------------------------------------------------
    private static void CompletionBandUsesWhatCouldBeMeasured(Action<bool, string> check)
    {
        // The recorded page: completion edge 692 (icon row), band 713..1295, 12px glyph, 1095x540 board.
        ResultsSafeArea strip = ResultsRegion.Resolve(completionMeasured: true, completionBottom: 692f,
            buttonMeasured: true, buttonTop: 1317f, screenHeight: 1440f, gap: 16f);
        check(strip.Fits && Near(strip.Top, 708f) && Near(strip.Bottom, 1301f),
            "the page's own gap below the completion edge and above the button it measured is the strip, with no screen fraction involved");
        float room = strip.Bottom - strip.Top;
        check(room > 540f,
            "and that strip is deeper than the 540px board the recorded run drew, so fitting it is a font/row question, not a reason to widen anything");
        // A page whose completion section could not be measured at all is never placed from the fraction of the window
        // that `Resolve` falls back to: the caller's own guard refuses it (`ResultsUpperBound.Decide`), which is what
        // keeps an unmeasured section from becoming a placed board.
        check(ResultsUpperBound.Decide(completionMeasured: false, stripFits: true) != ResultsUpperBound.Decision.Placed,
            "an unmeasurable completion section is deferred by the caller's own rule, so the fraction of the window `Resolve` falls back to never becomes a placed board");
    }

    // ---------------------------------------------------------------------------------------------------
    // B2. 实机失败页：band=(890..1429) 与 buttonTop=1240，旧板 bottom=1385
    // ---------------------------------------------------------------------------------------------------
    private static void RecordedFailureBandIsCutAtTheButton(Action<bool, string> check)
    {
        ResultsSafeArea wall = ResultsRegion.Resolve(completionMeasured: true, completionBottom: 890f,
            buttonMeasured: true, buttonTop: 1240f, screenHeight: 1440f, gap: 16f);
        check(wall.Fits && Near(wall.Bottom, 1224f) && Near(wall.Top, 906f),
            "the failure page's strip stops at the button it measured (1240 - 16), not at the screen foot the old band used (1429)");
        var board = new LayoutRect(24f, 906f, 1000f, 1385f - 906f);
        var button = new LayoutRect(900f, 1240f, 400f, 60f);
        check(ResultInteractionStripPolicy.OverlapsBoard(button, board, 0f),
            "the 1385-tall board the recorded run drew really did reach the button, which is why it had to be taken away");
        check(ResultLateControlPolicy.MustMove(906f, 1385f, 906f, 1224f),
            "a standing board whose foot is past the band's foot is moved by the late-control rule");
        // Two empty scans are not a proof that no control will arrive: the third frame's control still defers.
        check(ResultLateControlPolicy.MustMove(906f, 1250f, 906f, 1224f),
            "and a control that arrives later still moves it, so an empty scan is never read as 'this page has no wall'");
        check(!ResultLateControlPolicy.MustMove(906f, 1200f, 906f, 1224f),
            "a board that stays above the measured wall is left exactly where it is");
    }

    // ---------------------------------------------------------------------------------------------------
    // C. 富文本残片：24 字节截断后的 `Long Gun<`
    // ---------------------------------------------------------------------------------------------------
    private static void GearNameDisplay(Action<bool, string> check)
    {
        check(CombatStatsFormatter.VisibleName("<color=#FF4005>Long Gun</color>") == "Long Gun",
            "the game's own complete markup is stripped, exactly as the player name cleaner already did");
        check(CombatStatsFormatter.VisibleName("<color=#FF4005>Long Gun<") == "Long Gun",
            "the archived run4 residue - a cut that left the opening '<' of the closing tag - is dropped from the display");
        check(CombatStatsFormatter.VisibleName("<color=#FF4005>Long Gun") == "Long Gun",
            "a cut that dropped the whole closing tag leaves the visible name, never a literal tag");
        check(CombatStatsFormatter.VisibleName("<color=#FF40") == string.Empty,
            "a cut inside the opening tag leaves nothing to show, which the board renders as its placeholder");
        check(CombatStatsFormatter.VisibleName("Long Gun") == "Long Gun",
            "an ordinary weapon name is untouched");
        check(CombatStatsFormatter.VisibleName("收束器霰弹枪") == "收束器霰弹枪",
            "and a Chinese weapon name is untouched");
        check(CombatStatsFormatter.VisibleName("1 < 2") == "1 < 2" && CombatStatsFormatter.VisibleName("A<B") == "A<B"
              && CombatStatsFormatter.VisibleName("A < B") == "A < B",
            "a literal comparer inside a name stays literal: an isolated '<' followed by anything that is not tag-shaped is not a fragment");
        // A bare trailing '<' is only removed with proof: the name is at the wire's own capacity (24 UTF-8 bytes), which
        // is what a cut *is*. A shorter name keeps its '<' even at the end.
        check(CombatStatsFormatter.VisibleName("x <") == "x <" && CombatStatsFormatter.VisibleName("123456789012345678901 <") == "123456789012345678901 <",
            "a short name that ends in a bare '<' keeps it: nothing about it proves the wire cut anything");
        check(Encoding.UTF8.GetByteCount("<color=#FF4005>Long Gun<") == SyncSnapshot.NameCapacity
              && CombatStatsFormatter.NameCapacityBytes == SyncSnapshot.NameCapacity,
            "the recorded name is exactly NameCapacity (" + SyncSnapshot.NameCapacity + ") bytes, which is the proof that its bare '<' is the dropped closing tag, and the formatter's copy of that number matches the wire's");
        check(CombatStatsFormatter.VisibleName("AB<co") == "AB" && CombatStatsFormatter.VisibleName("A<c") == "A<c",
            "a two-letter fragment that really is a tag prefix ('<co') is removed even in a short name, while a single letter ('<c') is kept like any other literal");
        check(CombatStatsFormatter.VisibleName("HEL自动手枪<") == "HEL自动手枪<",
            "a 16-byte Chinese name that ends in '<' is not at the field's capacity, so its '<' is kept: only a proved cut is removed");
        check(CombatStatsFormatter.VisibleName("<color=#FF4005>收束器霰弹枪<") == "收束器霰弹枪",
            "and a Chinese name that really was cut (the same markup prefix, past the capacity) loses the residue and keeps its visible text");
        // N1: a literal `</` is not markup. Without a proved cut (the field's own capacity) or a complete opening tag in
        // the same name, nothing says the '/' was ever a closing tag, so it stays exactly as the player typed it.
        check(CombatStatsFormatter.VisibleName("A</") == "A</" && CombatStatsFormatter.VisibleName("Gun</c") == "Gun</c"
              && CombatStatsFormatter.VisibleName("大锤</c") == "大锤</c",
            "a short name that merely contains '</' keeps it - Latin, mixed and Chinese: the '/' alone proves nothing (N1)");
        check(CombatStatsFormatter.VisibleName("a<b") == "a<b" && CombatStatsFormatter.VisibleName("x <") == "x <"
              && CombatStatsFormatter.VisibleName("A < B") == "A < B",
            "and the literal comparers the review named stay literal, whatever their case (N1)");
        check(CombatStatsFormatter.VisibleName("<color=#FF4005>大锤</c") == "大锤",
            "while the same '</c' inside a name that really carries the game's opening tag is the cut closing tag, and its visible text is kept (N1)");

        // The one production entry point: the settlement board's four gear rows.
        var input = new ImpactInput { Name = "A1pha", SecondaryName = "<color=#FF4005>Long Gun<" };
        input.PrimaryName = "<color=#FF4005>Long Gun</color>";
        input.MeleeName = "大锤";
        input.ToolName = "生物追踪仪";
        ImpactReport report = ImpactScoring.Evaluate(new[] { input }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        ImpactBoardLayout board = ImpactBoard.Build(report, true, true);
        string secondary = board.Cell((int)ImpactRowKind.SecondaryWeapon, 1)[0];
        string primary = board.Cell((int)ImpactRowKind.PrimaryWeapon, 1)[0];
        check(secondary == "Long Gun" && !secondary.Contains("<"),
            "the settlement board's 副武器 row shows the visible name, not the tag residue");
        check(primary == "Long Gun", "and the 主武器 row is cleaned the same way");
        check(board.Cell((int)ImpactRowKind.MeleeWeapon, 1)[0] == "大锤" && board.Cell((int)ImpactRowKind.ToolWeapon, 1)[0] == "生物追踪仪",
            "the other two gear rows keep their Chinese names");
        check(board.Cell((int)ImpactRowKind.SecondaryWeapon, 1)[0] == CombatStatsFormatter.VisibleName(input.SecondaryName),
            "the string the column is measured with is the string it draws: both are the one visible name");
        check(!board.Cell((int)ImpactRowKind.SecondaryWeapon, 1)[0].Contains("#FF4005"),
            "and no fragment of the cut tag reaches the table at all");

        // N1, end to end on the wire's own field: the recorded melee name is exactly 24 UTF-8 bytes, so the game's own
        // packer hands the peer a name cut at '</c'. Nothing here pastes the residue in - `SyncProtocol.PackName`,
        // `SyncProtocol.UnpackName` and `ImpactBoard` are the production calls the runtime makes.
        var words = new uint[SyncSnapshot.NameWords];
        SyncProtocol.PackName("<color=#FF4005>大锤</c", words);
        string overWire = SyncProtocol.UnpackName(words);
        var cut = new ImpactInput { Name = "A1pha", PrimaryName = overWire, MeleeName = overWire };
        ImpactReport cutReport = ImpactScoring.Evaluate(new[] { cut }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        ImpactBoardLayout cutBoard = ImpactBoard.Build(cutReport, true, true);
        check(overWire == "<color=#FF4005>大锤</c" && Encoding.UTF8.GetByteCount(overWire) == SyncSnapshot.NameCapacity,
            "the packer fills the whole " + SyncSnapshot.NameCapacity + "-byte name field with the cut markup and the unpacker hands it back unchanged");
        check(cutBoard.Cell((int)ImpactRowKind.MeleeWeapon, 1)[0] == "大锤"
              && cutBoard.Cell((int)ImpactRowKind.PrimaryWeapon, 1)[0] == "大锤",
            "and the board's own cells show 大锤 for the packed-and-unpacked name, never the residue (N1)");
    }

    // ---------------------------------------------------------------------------------------------------
    // D. 每包/每帧刷的日志：按对局/对象/状态有界
    // ---------------------------------------------------------------------------------------------------
    private static void PacketLogPolicy(Action<bool, string> check)
    {
        // The recorded run: 161 supply-kind lines from four peers, one state, one expedition.
        var policy = new SyncLogPolicy();
        var peers = new ulong[] { 0xEA1E, 0xEA2E, 0xEA3E, 0xEA4E };
        int written = 0;
        for (int packet = 0; packet < 161; packet++)
        {
            if (policy.Write("supply kinds", peers[packet % peers.Length], "owner:7", "True;the split is cached", anomaly: false)) written++;
        }
        check(written == 4 && policy.Suppressed("supply kinds") == 157,
            "161 identical supply-kind packets from four peers become four lines - one per peer - and 157 counted repeats");
        check(policy.Write("supply kinds", peers[0], "False;the peer was not greeted", anomaly: true),
            "a refusal is still written on the packet that produces it");
        check(!policy.Write("supply kinds", peers[0], "False;the peer was not greeted", anomaly: true),
            "and the same refusal from the same peer is counted rather than repeated per packet");
        // N3: the owner is the line's *object key*, never part of the state. Four different decimal owners behind one
        // peer with the identical reason are four first lines: digits in an identity must not be folded by the
        // normalisation that collapses a generation or a packet count in a reason.
        var owners = new SyncLogPolicy();
        ulong sender = peers[0];
        var ownerIds = new ulong[] { 10000000000000031UL, 10000000000000032UL, 10000000000000033UL, 10000000000000034UL };
        int firstLines = 0;
        foreach (ulong owner in ownerIds)
            if (owners.Write("row", sender, "owner:" + owner, "True;the split is cached", anomaly: false)) firstLines++;
        check(firstLines == 4 && owners.Written("row") == 4,
            "four different decimal owners behind one peer, with the identical reason, are four first lines (N3): the identity is the object, not a part of the state the policy normalises");
        check(SyncLogPolicy.Normalize("owner:" + ownerIds[0]) == SyncLogPolicy.Normalize("owner:" + ownerIds[1]),
            "the normalisation would have folded those two owners into the same '#' run, which is exactly why the owner travels as its own field");
        int repeats = 0;
        for (int packet = 0; packet < 100; packet++)
            foreach (ulong owner in ownerIds)
                if (owners.Write("row", sender, "owner:" + owner, "True;the split is cached", anomaly: false)) repeats++;
        check(repeats == 0 && owners.Suppressed("row") == 400,
            "a hundred repeats of each of the four owners are counted and not printed (400 of them): the same peer can neither fold four players into one line nor grow a line per packet");
        check(owners.Write("row", sender, "owner:" + ownerIds[1], "False;the peer was not greeted", anomaly: true),
            "a refusal is still written on the packet that produces it, including for an object whose accepted state is already known");
        check(!owners.Write("row", sender, "owner:" + ownerIds[1], "False;the peer was not greeted", anomaly: true),
            "and the same refusal for the same object is counted rather than repeated per packet");
        string ownerSummary = owners.Summary();
        check(ownerSummary != null && ownerSummary.Contains("row written=5") && ownerSummary.Contains("suppressed=401")
              && ownerSummary.Contains("anomalies=1"),
            "the counts are printed as one line when the expedition ends (" + ownerSummary + ")");
        owners.Reset();
        check(owners.Written("row") == 0 && owners.Suppressed("row") == 0 && owners.Anomalies("row") == 0
              && owners.Summary() == null && owners.Write("row", sender, "owner:" + ownerIds[0], "True;the split is cached", anomaly: false),
            "a new lobby starts its own counts and its own remembered states, so the previous run's silence can never answer for it");

        // The recorded run's own summary: five lines for the whole pack, the rest counted.
        string summary = policy.Summary();
        check(summary != null && summary.Contains("supply kinds written=5") && summary.Contains("suppressed=158")
              && summary.Contains("anomalies=1"),
            "the counts are printed as one line when the expedition ends (" + summary + ")");
        policy.Reset();
        check(policy.Suppressed("supply kinds") == 0 && policy.Written("supply kinds") == 0 && policy.Summary() == null,
            "and the next expedition starts its own counts, so a page/run reset does not hide a later anomaly");

        // N2: a line the caller could not write is not counted as written. `Reserve` + `Cancel` is exactly that path.
        var tickets = new SyncLogPolicy();
        SyncLogPolicy.Ticket first = tickets.Reserve("row", 0x11UL, "True;owner=1", anomaly: false);
        check(first.Valid && tickets.Written("row") == 0,
            "reserving a line does not count it yet - the policy waits for the caller's own answer (N2)");
        tickets.Cancel(first);
        check(tickets.Written("row") == 0 && tickets.Suppressed("row") == 1,
            "a reserved line the logger refused is counted as suppressed, never as written");
        SyncLogPolicy.Ticket second = tickets.Reserve("row", 0x11UL, "True;owner=1", anomaly: false);
        check(second.Valid, "and the same state can be reserved again, because a cancelled line was never recorded");
        tickets.Commit(second);
        check(tickets.Written("row") == 1 && tickets.Suppressed("row") == 1,
            "committing it is what makes the summary say written=1 suppressed=1 - the figure now matches the log");

        // A different kind of packet is bounded on its own, and one expedition cannot exceed the per-kind budget.
        var budget = new SyncLogPolicy();
        for (int index = 0; index < 80; index++)
            budget.Write("row", (ulong)index, "owner:" + index, "True", anomaly: false);
        check(budget.Written("row") == SyncLogPolicy.PerKindBudget && budget.Suppressed("row") == 80 - SyncLogPolicy.PerKindBudget,
            "eighty distinct peers on one kind of packet stop at the per-kind budget (" + SyncLogPolicy.PerKindBudget + " lines) and count the rest");

        // An anomaly after the budget is spent still gets its line: a fault must not disappear because ordinary
        // packets used the budget up.
        var anomalyLast = new SyncLogPolicy();
        for (int index = 0; index < 60; index++) anomalyLast.Write("snapshot", (ulong)index, "owner:" + index, "True", anomaly: false);
        check(anomalyLast.Write("snapshot", 0x99UL, "owner:9", "False;generation # behind", anomaly: true),
            "an anomaly from a peer that arrives after the per-kind budget is spent is still written");
        check(anomalyLast.Written("snapshot") == SyncLogPolicy.PerKindBudget + 1 && anomalyLast.Suppressed("snapshot") > 0,
            "while the ordinary repeats, including the ones past the budget, stop exactly at the budget and are counted");

        // N5: a refusal whose text carries a changing generation is the *same* statement. Five hundred packets, five
        // hundred generations, one line - and the state set stays bounded.
        var generations = new SyncLogPolicy();
        int shown = 0;
        for (int generation = 1; generation <= 500; generation++)
            if (generations.Write("snapshot", 0x77UL, "False;the peer generation " + generation + " is behind", anomaly: true)) shown++;
        check(shown == 1 && generations.Suppressed("snapshot") == 499,
            "500 refusals that differ only by the generation they quote collapse to one line and 499 counted repeats (N5)");
        check(SyncLogPolicy.Normalize("False;the peer generation 4212 is behind")
              == SyncLogPolicy.Normalize("False;the peer generation 7 is behind"),
            "the normalisation that makes that true keeps the words and collapses only the digits");

        // A peer that keeps inventing objects cannot grow one entry without limit: the remembered states are bounded and
        // the per-peer budget is spent exactly - sixty distinct owners are twelve lines, never one line per packet.
        var manyOwners = new SyncLogPolicy();
        for (int owner = 0; owner < 60; owner++) manyOwners.Write("row", 0x55UL, "owner:" + owner, "True;cached", anomaly: false);
        check(manyOwners.Written("row") == SyncLogPolicy.PerPeerKindBudget,
            "sixty distinct owners from one peer stop at exactly that peer's budget (" + manyOwners.Written("row") + " lines)");
        check(manyOwners.Suppressed("row") == 60 - SyncLogPolicy.PerPeerKindBudget,
            "and every one of the others is counted, so the summary still accounts for all sixty packets");

        // N5: a peer that rotates through more states than the policy remembers must not be able to rewrite itself once
        // per packet. Two hundred anomaly packets over forty rotating states stay inside the anomaly budget.
        var rotating = new SyncLogPolicy();
        int rotated = 0;
        for (int packet = 0; packet < 200; packet++)
            if (rotating.Write("snapshot", 0x66UL, "owner:" + (packet % 40), "False;state " + (packet % 40), anomaly: true)) rotated++;
        check(rotated == SyncLogPolicy.AnomalyPerPeerBudget && rotating.Anomalies("snapshot") == SyncLogPolicy.AnomalyPerPeerBudget,
            "a peer rotating through forty distinct anomaly states is written " + rotated + " times - its anomaly budget - and never once per packet, even though that budget is smaller than the state set (N5)");
        check(rotating.Suppressed("snapshot") == 200 - SyncLogPolicy.AnomalyPerPeerBudget,
            "and the other " + rotating.Suppressed("snapshot") + " packets are counted, so a jittering state can neither hide the fault nor flood the log");

        // N4: an exception that repeats per packet prints first and on every doubling, and counts the rest - never
        // swallowed, never per packet.
        var errors = new SyncLogPolicy();
        int printed = 0, lastOccurrences = 0;
        for (int packet = 0; packet < 100; packet++)
            if (errors.Error("row", "Object reference not set (packet " + packet + ")", out lastOccurrences)) printed++;
        check(printed == 7 && lastOccurrences == 100,
            "100 identical failures print 7 lines (1,2,4,8,16,32,64) and the last carries the true total of 100 (N4): printed=" + printed);
        check(errors.Errors("row") == 100 && errors.Summary() != null && errors.Summary().Contains("errors=100"),
            "and the packet summary hands the same total over, so nothing is hidden");

        // N5: the exception buckets are bounded too. Twelve different non-numeric messages do not become twelve keys:
        // the first `MaxErrorKeysPerKind` keep their own bucket and the rest count under one visible "(other failure
        // text)" bucket, so a page that fails in twelve different ways cannot print - or remember - a line per packet.
        var mixed = new SyncLogPolicy();
        int mixedPrinted = 0, worst = 0;
        for (int message = 0; message < 12; message++)
        {
            for (int repeat = 0; repeat < 10; repeat++)
            {
                if (mixed.Error("supply kinds", "the cached split could not be applied: case " + (char)('A' + message), out int occurrences)) mixedPrinted++;
                if (occurrences > worst) worst = occurrences;
            }
        }
        check(mixed.ErrorBuckets("supply kinds") == SyncLogPolicy.MaxErrorKeysPerKind,
            "twelve different failure texts keep exactly " + mixed.ErrorBuckets("supply kinds") + " buckets - the bound, not one key per text (N5)");
        check(mixed.Errors("supply kinds") == 120 && worst == 40,
            "and the four texts past the bound are counted together under the one visible '(other failure text)' bucket: 120 occurrences in all, the last of them the bucket's own 40th (" + worst + ")");
        check(mixedPrinted == 38,
            "the bucketed lines still print on the first occurrence and on every doubling (" + mixedPrinted + " lines for 120 failures: each of the eight own buckets at 1,2,4,8 and the shared bucket at 1,2,4,8,16,32), so nothing is hidden - and nothing is printed per packet");
        mixed.Reset();
        check(mixed.Errors("supply kinds") == 0 && mixed.ErrorBuckets("supply kinds") == 0 && mixed.Summary() == null,
            "a new expedition starts with no remembered failure text at all");
    }

    // ---------------------------------------------------------------------------------------------------
    // E. 生产接线（Unity 侧只能静态检查，本进程到不了真页面/真协程）
    // ---------------------------------------------------------------------------------------------------
    private static void ProductionWiring(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the regression locates the production sources");
        if (root == null) return;
        string display = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        string targets = File.ReadAllText(Path.Combine(root, "CompletionTargets.cs"));
        string ledger = File.ReadAllText(Path.Combine(root, "BlinkCompletionLedger.cs"));
        string final = File.ReadAllText(Path.Combine(root, "ResultFinalLayout.cs"));
        string sync = File.ReadAllText(Path.Combine(root, "CombatSync.cs"));
        string impact = File.ReadAllText(Path.Combine(root, "ImpactBoard.cs"));

        // A1: the BlinkIn patch closes the schedule window it opened, in the same native call.
        check(targets.Contains("CompletionTargets.BeginCall();") && targets.Contains("CompletionTargets.SchedulesClosed();")
              && targets.Contains("[HarmonyPostfix, HarmonyWrapSafe]") && targets.Contains("Ledger.CloseSchedule(Window)")
              && targets.Contains("FinishRoutine(__instance.Pointer.ToInt64(), finished)"),
            "the BlinkIn call opens and closes one schedule window, and a routine whose target is gone is finished by its routine pointer");

        // A2: the release is logged once per page, as a state, never per frame, and only once the ledger is settled (F1).
        int blocks = display.IndexOf("if (ResultRevealGate.BlocksPlacement(revealDone, CompletionTargets.Ledger.MayStillComplete,", StringComparison.Ordinal);
        int release = display.IndexOf("HeadingReports.Add(\"success-reveal-released\")", StringComparison.Ordinal);
        check(blocks >= 0 && release > blocks && display.Contains("ResultRevealGate.WhyReleased(")
              && display.Contains("CompletionTargets.Ledger.RevealImpossible && CompletionTargets.Ledger.Settled"),
            "the success page still asks the reveal gate first, and the released case has its own once-per-page line, printed only once nothing of the ledger's own is still running (F1)");
        check(display.Contains("unscheduled={ledger.Unscheduled}, aborted={ledger.Aborted}"),
            "and the reveal diagnostic prints what the ledger was handed, what it counted and what ended without revealing, so a stuck page is readable from the log alone");
        int gateStart = final.IndexOf("public static class ResultRevealGate", StringComparison.Ordinal);
        int gateClose = gateStart >= 0 ? final.IndexOf("\n}", gateStart, StringComparison.Ordinal) : -1;
        string gateBody = gateClose > gateStart ? final.Substring(gateStart, gateClose - gateStart) : string.Empty;
        check(gateBody.Length > 0 && !gateBody.Contains("MaximumWaitFrames") && !gateBody.Contains("MissBudget"),
            "the strict reveal gate's own body still carries no frame count and no refusal budget: the only bounded wait stays where it was");

        // B: the settlement board's gear names all go through the one cleaner.
        check(impact.Contains("CombatStatsFormatter.VisibleName(name)") && impact.Contains("private static string Name(string name)"),
            "the board's gear rows are displayed through the cleaner, not through the raw name");
        check(!impact.Contains("string.IsNullOrEmpty(name) ? CombatStrings.Placeholder : name"),
            "and the raw-name display is gone");

        // C: the packet lines are bounded through the policy, and the counts are flushed at both resets.
        check(sync.Contains("PacketLine(\"row\", sender, ObjectKey(snapshot.Owner),")
              && sync.Contains("PacketLine(\"supply kinds\", sender, ObjectKey(kinds.Owner),")
              && sync.Contains("SyncLogPolicy.Ticket ticket = Packets.Reserve(kind, peer, objectKey, state, anomaly);")
              && sync.Contains("private static void PacketLine(string kind, ulong peer, string objectKey, string state, bool anomaly, string text)")
              && sync.Contains("private static string ObjectKey(ulong owner) => \"owner:\" + owner.ToString(CultureInfo.InvariantCulture);")
              && sync.Contains("if (Line(text)) Packets.Commit(ticket);")
              && sync.Contains("else Packets.Cancel(ticket);")
              && sync.Contains("Packets.Error(\"row\", error.Message, out int occurrences)")
              && sync.Contains("Packets.Error(\"supply kinds\", error.Message, out int occurrences)")
              && sync.Contains("FlushPacketSummary(\"expedition reset\");") && sync.Contains("FlushPacketSummary(\"new lobby\");")
              && sync.Contains("Packets.Summary()") && sync.Contains("Packets.Reset();"),
            "the two per-packet lines are reserved, committed only when the logger really wrote them (N2), the two catch blocks go through the bounded error line (N4), and the counted repeats are flushed at the expedition and lobby resets");
        check(!sync.Contains("\"owner=\" + snapshot.Owner") && !sync.Contains("\"owner=\" + kinds.Owner")
              && sync.Contains("decision.Accepted + \";\" + decision.Reason, !decision.Accepted,"),
            "the owner is no longer folded into the state the policy normalises: it is passed as the line's own object key, so the same peer forwarding four different decimal owners keeps four first lines (N3)");

        // D: F1-STALL - the stall clock is fed with the ledger's own facts every time it is asked, and its bound comes
        // from the page's own blink duration. No frame count and no refusal budget is in it.
        string stalled = Body(display, "private static bool RevealStalled(out float stalledSeconds, out float limitSeconds)");
        check(stalled.Length > 0
              && stalled.Contains("RevealClock.Observe(now, ledger.Completed, ledger.InFlight, ledger.Count);")
              && stalled.Contains("RevealProgressClock.LimitFor(BlinkSeconds())")
              && !stalled.Contains("MaximumWaitFrames") && !stalled.Contains("MissBudget"),
            "the stall clock is fed with completed/in-flight/scheduled on every frame it is asked, and its bound is the page's own blink duration - no frame count and no refusal budget appears in it (F1-STALL)");
        check(display.Contains("bool revealStalled = RevealStalled(out float stallSeconds, out float stallLimit);")
              && display.Contains("CompletionTargets.Ledger.InFlight, CompletionTargets.Ledger.Count,")
              && display.Contains("revealStalled, _finalEverMeasurable"),
            "and `Prepare` hands that answer, together with the in-flight count, to the one settlement decision");
        check(display.Contains("private static float BlinkSeconds()")
              && Body(display, "private static float BlinkSeconds()").Contains("CoroutineManager.BlinkDuration")
              && Body(display, "private static void ForgetPage()").Contains("RevealClock.Forget();"),
            "the bound is read from the game's own `CoroutineManager.BlinkDuration`, and a new result page forgets the clock with everything else");

        // E: F5 - the line that answers an unproven band keeps saying the wall was never observed.
        string fallback = Body(display, "private static bool FallbackPlacement(Component page, bool succeeded, out float top, out float room,");
        // 1.19.45: “墙量到了吗”只剩一个定义（`ResultFailureWallPolicy.WallMeasured`），它接受那一页真正画出来的墙
        // （客机失败页的等待文字、主机页的按钮/页脚文字），但**不接受**页面自己从屏幕推出来的下沿；那条兜底日志的
        // 注脚也由同一份证据决定（F8 / 主机的有界放弃）。页面自己的失败协程终点不再是这里的一条证据。
        check(fallback.Length > 0 && fallback.Contains("wallMeasured = ResultFailureWallPolicy.WallMeasured(walls, pageButtonTop, controls.PressableTop,")
              && !fallback.Contains("FailureSequence.IsFinished(page.Pointer)")
              && fallback.Contains("wallWait = FailureWallWaitFrames(wallMeasured);")
              && fallback.Contains("ResultFailureWallPolicy.AnswerNote(wallMeasured, wallWait, walls.GuestFailure)"),
            "the fail-open band's own line carries the wall-not-observed note through the policy, and the wall it was answered from is the one definition that never counts the page's screen-derived foot (F5)");
    }

    // ---------------------------------------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------------------------------------
    // One method's or type's body by brace pairing: what belongs to it, never what it means.
    private static string Body(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        int open = source.IndexOf('{', start);
        if (open < 0) return string.Empty;
        int depth = 0;
        for (int index = open; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0) return source.Substring(open, index - open + 1);
            }
        }
        return string.Empty;
    }

    private static void RequireAll(BlinkCompletionLedger ledger)
    {
        ledger.Require(MainIcon, false);
        ledger.Require(SecondIcon, false);
        ledger.Require(ThirdIcon, false);
        ledger.Require(AllIcon, false);
        ledger.Require(Caption, true);
        ledger.Require(SecondCaption, true);
    }

    private static void StartAll(BlinkCompletionLedger ledger)
    {
        ledger.Start(MainIcon, MainRoutine);
        ledger.Start(SecondIcon, SecondRoutine);
        ledger.Start(ThirdIcon, ThirdRoutine);
        ledger.Start(AllIcon, AllRoutine);
        ledger.Start(Caption, CaptionRoutine);
        ledger.Start(SecondCaption, SecondCaptionRoutine);
    }

    private static void FinishAll(BlinkCompletionLedger ledger, bool naturallyRevealed)
    {
        ledger.Finish(MainIcon, MainRoutine, naturallyRevealed);
        ledger.Finish(SecondIcon, SecondRoutine, naturallyRevealed);
        ledger.Finish(ThirdIcon, ThirdRoutine, naturallyRevealed);
        ledger.Finish(AllIcon, AllRoutine, naturallyRevealed);
        ledger.Finish(Caption, CaptionRoutine, naturallyRevealed);
        ledger.Finish(SecondCaption, SecondCaptionRoutine, naturallyRevealed);
    }

    // Exactly as `ResultsDisplay.CompletionReady` feeds its latch.
    private static bool Reveal(CompletionReadiness readiness, BlinkCompletionLedger ledger)
    {
        if (readiness.Ready) return true;
        return readiness.Observe(new CompletionEvidence(ledger.Artwork > 0, ledger.NamesReady,
            ledger.Artwork > 0, ledger.ArtworkReady, ledger.Artwork > 0));
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) <= 0.001f;

    private static string SourceRoot()
    {
        string directory = AppContext.BaseDirectory;
        for (int step = 0; step < 8 && directory != null; step++)
        {
            if (File.Exists(Path.Combine(directory, "ResultsDisplay.cs")) && File.Exists(Path.Combine(directory, "PluginInfo.cs"))) return directory;
            directory = Path.GetDirectoryName(directory);
        }
        return null;
    }
}
