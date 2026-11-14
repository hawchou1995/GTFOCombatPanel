using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// What the three diagnostics fixes of 1.19.16 change, driven through the real policy this build ships with.
//
// The three defects, as the read-only diagnosis confirmed them:
//
//   the level clock   `CombatSync` kept `_levelSince` and the two once-per-level latches in three fields of its
//                     own, restarted them only on the `InLevel` branch and cleared them on a line that branch had
//                     already returned past, so a level entered after a lobby and a generation inherited the
//                     previous level's clock. The field log shows it: `3351` InLevel, two peers greeted at
//                     `3363`-`3373`, `5563` NoLobby --> InLobby, `5819` state=Generating, `5979` the new level's
//                     first frame - where the build warned "1416s in the level with 3 human peer(s) and not one
//                     packet heard", and then accepted those peers' hellos six lines later.
//   the anchor        `PanelBehaviour.SampleAnchor` turned the first unreadable sample - `camera=null` on the
//                     first frame of a level, before the HUD's camera exists - into a permanent verdict, so no
//                     `flashlight anchor locked` line was ever written again and the table stood on the
//                     configured position for the whole session.
//   the wording       the `ShowImpactBoard` description claimed F8 writes that key back, which `TogglePolicy`
//                     never did and never should: F8 stores `ShowPanel` and `ShowResultOverlay`.
//
// Everything below runs against the real classes of this build. The last section reads the production sources
// instead, and says so.
internal static class Release11916bScenarios
{
    public static int Run()
    {
        int failures = 0;
        RunInto((condition, description) =>
        {
            if (condition) return;
            failures++;
            Console.WriteLine("FAIL: " + description);
        });
        return failures;
    }

    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));

        TheLevelClockIsOneLevelLong(check);
        ARealSilenceStillWarnsOncePerLevel(check);
        ThePerLevelLatchesAreSpentAgainOnEveryLevel(check);
        ATransientAnchorFailureIsRetriedAndLocks(check);
        TheRetryScheduleIsBoundedAndNeverPerFrame(check);
        TheAnchorLinesFollowTheScheduleAndStopAtTheGiveUp(check);
        TheGiveUpEndsTheRoundAndSaysWhy(check);
        ASampleThatThrowsStillSpendsTheBudget(check);
        TheDiagnosticTextTellsTheTruth(check);
    }

    // ------------------------------------------------------------- the level clock is one level long
    //
    // The exact sequence of the field log, with its own numbers: a level, two peers heard in it, a lobby, a
    // generation, and the next level. 1416 s separate the two `InLevel` frames in the report, which is the age
    // the old build quoted for a level that had just begun.
    private const float FirstLevelFrame = 100f;
    private const float SecondLevelFrame = 1516f;

    private static void TheLevelClockIsOneLevelLong(Action<bool, string> check)
    {
        var clock = new LevelDiagnosticClock();
        check(!clock.Running && clock.Phase == LevelPhase.Outside && !clock.OutboundOwed && !clock.NoInboundReported,
            "a clock that no level has touched is outside, stopped, and owes no line");

        check(clock.Enter(LevelPhase.InLevel, FirstLevelFrame), "the first InLevel of a session begins a level");
        check(clock.InLevel && clock.Running, "the level clock runs for as long as the level is up");
        // `3363`-`3373`: two peers greeted, so the exchange is not blind and the alarm has nothing to say.
        check(!clock.NoInboundOwed(FirstLevelFrame + 10f, 3, 2),
            "a level that has already handled two peers' packets is never called silent");

        // `5563` NoLobby --> InLobby, then `5819` state=Generating: two non-level states in a row.
        check(!clock.Enter(LevelPhase.Outside, FirstLevelFrame + 30f) && !clock.Running,
            "leaving the level for a lobby stops the clock, which the old build never did");
        check(!clock.Enter(LevelPhase.Outside, FirstLevelFrame + 31f),
            "a second non-level state - the generation after the lobby - is not a new level");

        // `5979`: the next level's first frame.
        check(clock.Enter(LevelPhase.InLevel, SecondLevelFrame),
            "the next InLevel begins its own level, 1416 s after the first one began");
        check(Math.Abs(clock.Seconds(SecondLevelFrame + 0.1f) - 0.1f) < 0.001f,
            "the new level's clock starts at its own first frame, not at the previous level's");
        check(!clock.NoInboundOwed(SecondLevelFrame + 0.1f, 3, 0),
            "the new level's first frame is never reported as a 1416 s silence - this is the false warning the field log quoted");
        check(!clock.Enter(LevelPhase.InLevel, SecondLevelFrame + 4f),
            "a repeated InLevel frame does not restart the clock");
        check(Math.Abs(clock.Seconds(SecondLevelFrame + 4f) - 4f) < 0.001f,
            "the clock keeps counting while the same level is up");
        check(Math.Abs(clock.Seconds(float.NaN)) < 0.001f && Math.Abs(clock.Seconds(SecondLevelFrame - 1f)) < 0.001f,
            "an unreadable or backwards clock reads as zero rather than as a negative age");
    }

    // The other half of the fix: the alarm that is real must still be raised, once per level.
    private static void ARealSilenceStillWarnsOncePerLevel(Action<bool, string> check)
    {
        var clock = new LevelDiagnosticClock();
        clock.Enter(LevelPhase.InLevel, 0f);
        check(!clock.NoInboundOwed(19.9f, 3, 0), "nineteen seconds of silence is not a warning yet");
        check(clock.NoInboundOwed(20f, 3, 0),
            "twenty seconds into a level with three human peers and nothing heard is still the warning this diagnostic exists for");
        check(clock.NoInboundOwed(20.5f, 3, 0) == false, "and it is written once per level, not once per second");

        var heard = new LevelDiagnosticClock();
        heard.Enter(LevelPhase.InLevel, 0f);
        check(!heard.NoInboundOwed(30f, 3, 1), "one packet from one peer keeps the alarm silent");

        var alone = new LevelDiagnosticClock();
        alone.Enter(LevelPhase.InLevel, 0f);
        check(!alone.NoInboundOwed(30f, 0, 0), "a solo level has nobody to hear from and is never called silent");

        var results = new LevelDiagnosticClock();
        results.Enter(LevelPhase.InLevel, 0f);
        results.Enter(LevelPhase.Result, 5f);
        check(!results.NoInboundOwed(600f, 3, 0),
            "a result page quotes the level that produced it and never invents a silence of its own");
        results.Enter(LevelPhase.Outside, 610f);
        results.Enter(LevelPhase.InLevel, 620f);
        check(results.NoInboundOwed(641f, 3, 0),
            "the level after that page earns its own warning, which is exactly what the old unreachable reset lost");

        var unreadable = new LevelDiagnosticClock();
        unreadable.Enter(LevelPhase.InLevel, float.NaN);
        check(!unreadable.NoInboundOwed(float.NaN, 3, 0), "a clock that cannot be read cannot produce a warning");
        // Runtime: the frame after that one hands over a readable clock - `Time.unscaledTime` is up, the level is not.
        // Reading the missing start as 0 (what this build used to do) made the level look a whole session old on that
        // frame, which is the false alarm the per-level clock exists to remove.
        check(!unreadable.NoInboundOwed(5000f, 3, 0),
            "a level whose first frame had no readable clock is not called silent when the clock comes back: 5000s of the session is not 5000s of the level");
        check(Math.Abs(unreadable.Seconds(5000f)) < 0.001f,
            "and its age is not measured from a zero it never read");
        check(unreadable.InLevel && !unreadable.Running,
            "the level is up and no clock is running, because the clock it would run from was never readable");
        check(!unreadable.Enter(LevelPhase.InLevel, 5000f),
            "the first readable frame of that level starts its clock without being taken for a new level");
        check(unreadable.Running && Math.Abs(unreadable.Seconds(5001f) - 1f) < 0.001f,
            "so the clock starts from the frame it could be read, and the age it reports is that level's");
        check(!unreadable.NoInboundOwed(5019f, 3, 0) && unreadable.NoInboundOwed(5020f, 3, 0),
            "and twenty seconds after that readable start the real warning is still raised, so the fix silences no true alarm");
    }

    // ------------------------------------------------------------- the two latches are per level, not per session
    private static void ThePerLevelLatchesAreSpentAgainOnEveryLevel(Action<bool, string> check)
    {
        var clock = new LevelDiagnosticClock();
        check(!clock.OutboundOwed, "no level, no outbound line");

        clock.Enter(LevelPhase.InLevel, 0f);
        check(clock.OutboundOwed, "the first row of a level may be reported");
        clock.OutboundWritten();
        check(!clock.OutboundOwed, "and the report is spent after it is written");
        check(clock.NoInboundOwed(21f, 2, 0), "the first level of the session warns when it is really silent");

        clock.Enter(LevelPhase.Outside, 300f);
        clock.Enter(LevelPhase.Outside, 301f);
        clock.Enter(LevelPhase.InLevel, 400f);
        check(clock.OutboundOwed,
            "the second level of the same session reports its own first row - the old latch printed that line once per session");
        clock.OutboundWritten();
        check(clock.NoInboundOwed(421f, 2, 0), "and the second level earns its own warning as well");
        check(!clock.NoInboundOwed(430f, 2, 0), "each level warns once and only once");

        clock.Enter(LevelPhase.Result, 440f);
        check(!clock.OutboundOwed, "a result page following a level that already reported adds no second line");

        var unfinished = new LevelDiagnosticClock();
        unfinished.Enter(LevelPhase.InLevel, 0f);
        unfinished.Enter(LevelPhase.Result, 10f);
        check(unfinished.OutboundOwed,
            "a level whose exchange never put a row out can still report the first one on its own result page, where the old latch could print it");

        var fresh = new LevelDiagnosticClock();
        fresh.Enter(LevelPhase.InLevel, 0f);
        fresh.NoInboundOwed(30f, 2, 0);
        fresh.OutboundWritten();
        fresh.Forget();
        check(!fresh.Running && fresh.Phase == LevelPhase.Outside && !fresh.OutboundOwed && !fresh.NoInboundReported,
            "a new expedition forgets the clock and both latches");
        check(fresh.Enter(LevelPhase.InLevel, 0f) && fresh.OutboundOwed,
            "and the level after that reset is a fresh level with both lines owed again");
    }

    // ------------------------------------------------------------- the anchor: a transient miss is retried
    private static void ATransientAnchorFailureIsRetriedAndLocks(Action<bool, string> check)
    {
        // The first frame of the level: `camera=null`, exactly what the field log shows at `3358`.
        var policy = new AnchorSamplePolicy();
        check(policy.Due(0f), "the first frame of a level is always allowed to measure the anchor");
        policy.Began(0f);
        check(!policy.Failed(0f, AnchorSampleFailure.NoCamera),
            "a camera that is not there yet spends one attempt, not the whole measurement");
        check(policy.Failures == 1 && policy.Attempts == 1 && !policy.Abandoned,
            "the failure is counted and the round is still open");
        check(!policy.Due(0.25f), "the retry is throttled: half a second later it has not fired yet either");
        check(policy.Due(0.5f), "and half a second after the failure it is due");

        // The HUD's camera exists one attempt later: the round measures, the streak ends, and the ordinary
        // settle cadence (`PanelBehaviour` holds the icon still for a second) takes over.
        policy.Began(0.5f);
        policy.Measured(0.5f);
        check(policy.Failures == 0 && policy.LastFailure == AnchorSampleFailure.None && !policy.Abandoned,
            "a successful measurement clears the failure streak and keeps the round open");
        check(!policy.Due(0.7f) && policy.Due(0.75f),
            "after a measurement the table is sampled on the settle cadence again, not on a retry delay");

        // A later miss is a first miss again: it must not inherit the four-second delay of a long streak.
        policy.Began(0.75f);
        check(!policy.Failed(0.75f, AnchorSampleFailure.NoIcon) && !policy.Due(1.0f) && policy.Due(1.25f),
            "a miss after a good sample retries at the first delay, not at the ceiling");

        check(policy.Attempts == 3, "the round counts the attempts it really made");
        policy.Restart();
        check(!policy.Abandoned && policy.Attempts == 0 && policy.Failures == 0 && policy.Due(0f),
            "a new level or a config reload grants a fresh round");
    }

    // The retry is finite, throttled, and never a per-frame poll.
    private static void TheRetryScheduleIsBoundedAndNeverPerFrame(Action<bool, string> check)
    {
        check(Math.Abs(AnchorSamplePolicy.DelayAfter(0) - AnchorSamplePolicy.SettleInterval) < 0.001f,
            "a measuring anchor is sampled on the settle interval");
        check(Math.Abs(AnchorSamplePolicy.DelayAfter(1) - 0.5f) < 0.001f
              && Math.Abs(AnchorSamplePolicy.DelayAfter(2) - 1f) < 0.001f
              && Math.Abs(AnchorSamplePolicy.DelayAfter(3) - 2f) < 0.001f
              && Math.Abs(AnchorSamplePolicy.DelayAfter(4) - 4f) < 0.001f,
            "the retry delay doubles from half a second: 0.5, 1, 2, 4");
        bool monotone = true, bounded = true;
        for (int failures = 1; failures <= 64; failures++)
        {
            float delay = AnchorSamplePolicy.DelayAfter(failures);
            if (delay < AnchorSamplePolicy.DelayAfter(failures - 1) - 0.001f) monotone = false;
            if (!float.IsFinite(delay) || delay > AnchorSamplePolicy.MaximumRetryDelay + 0.001f) bounded = false;
        }
        check(monotone, "the schedule never gets shorter as failures accumulate");
        check(bounded, "and it never leaves its own ceiling, whatever the failure count is");

        // Two seconds of frames at 60 Hz, every one of which fails: a per-frame sampler would attempt 121
        // times. This one attempts three - one immediately, then half a second, then a second later.
        var policy = new AnchorSamplePolicy();
        int frames = 0;
        for (int frame = 0; frame <= 120; frame++)
        {
            float now = frame / 60f;
            frames++;
            if (!policy.Due(now)) continue;
            policy.Began(now);
            policy.Failed(now, AnchorSampleFailure.NoCamera);
        }
        check(policy.Attempts == 3,
            $"two seconds of failing frames at 60 Hz cost {policy.Attempts} measurements, not one per frame");
        check(policy.Attempts * 20 < frames,
            "the anchor is not polled per frame, which is the property the old single-sample latch was protecting");

        var noClock = new AnchorSamplePolicy();
        check(noClock.Due(float.NaN), "an unreadable clock still allows the first attempt");
        noClock.Began(float.NaN);
        check(!noClock.Due(float.NaN) && !noClock.Due(float.PositiveInfinity),
            "and after it, an unreadable clock is never due, so a broken timer cannot become a busy loop");
    }

    // ------------------------------------------------------- a sample that throws is still a spent measurement
    //
    // Runtime, against the production `AnchorSamplePolicy.Attempt`: the panel's own read used to sit *between*
    // `Began` and `Failed`, so an exception out of the HUD read left the failure uncounted, the retry delay never
    // advanced, and the icon was re-read on every settle tick for the rest of the level. These assertions drive the
    // method the panel now runs its attempt through with a body that really throws.
    private static void ASampleThatThrowsStillSpendsTheBudget(Action<bool, string> check)
    {
        var policy = new AnchorSamplePolicy();
        int reads = 0;
        AnchorSample answer = policy.Attempt(0f,
            () => { reads++; throw new InvalidOperationException("renderer was destroyed while the level loaded"); },
            out bool gaveUp);
        check(reads == 1 && !answer.Measured && !gaveUp,
            "a read that throws is one attempt, not a give-up");
        check(policy.Attempts == 1 && policy.Failures == 1,
            "and it spends the same failure any other unreadable rectangle spends, which is the whole point");
        check(policy.LastFailure == AnchorSampleFailure.Unmeasurable && answer.Failure == AnchorSampleFailure.Unmeasurable,
            "it is reported with a reason the log line already knows how to describe, not swallowed");
        check(answer.Error != null && answer.Error.Contains("renderer was destroyed"),
            "and the exception's own message rides along, so the line can name the read that threw");
        check(!policy.Due(0.25f) && policy.Due(0.5f),
            "the throw is throttled exactly like any other failure: half a second later it is due, not on the next tick");

        // A HUD that throws on every read must still run out of budget. This is the retry that used to be unbounded.
        var always = new AnchorSamplePolicy();
        float now = 0f;
        int thrown = 0;
        for (int frame = 0; frame < 4000 && !always.Abandoned; frame++)
        {
            now += 0.25f;
            if (!always.Due(now)) continue;
            always.Attempt(now, () => { thrown++; throw new TimeoutException("the icon's bounds could not be projected"); }, out _);
        }
        check(always.Abandoned && always.Attempts == AnchorSamplePolicy.Budget,
            $"a read that throws on every tick spends the {AnchorSamplePolicy.Budget}-attempt budget instead of polling for ever: attempts={always.Attempts}");
        check(thrown == AnchorSamplePolicy.Budget,
            $"and each of those attempts ran the read exactly once: reads={thrown}");
        check(always.Waited > 39f && always.Waited < 40f,
            $"the waiting the give-up line reports is the schedule the budget comment promises (0.5 + 1 + 2 + 4x9 = 39.5 s), not a fraction of it: waited={always.Waited:0.##}s");
        check(always.Abandoned && always.Due(now + 1000f)
              && !always.Due(now + 1000f + AnchorSamplePolicy.WatchInterval * 0.5f),
            $"an abandoned round stays abandoned after a throw as well, and answers only on its watch interval - never on the settle cadence again (watches={always.Watches})");

        // The wrapper adds nothing to the good path: a sample that answers hands its rectangle straight back.
        var good = new AnchorSamplePolicy();
        AnchorSample measured = good.Attempt(0f, () => AnchorSample.Of(new LayoutRect(10f, 20f, 30f, 40f)), out bool spent);
        check(measured.Measured && Math.Abs(measured.Rect.Y - 20f) < 0.001f && !spent,
            "a sample that answers hands its rectangle back through the same call");
        check(good.Failures == 0 && good.Due(0.25f) && !good.Due(0.2f),
            "and the good path keeps the settle cadence, with no failure to its name");
        // 1.19.26: a spent budget is not a poll, and it is not silence either. The attempt runs - that is the watch, and it is
        // how an element that appears after the twelve samples is ever found - but it spends nothing and answers no give-up.
        var stopped = new AnchorSamplePolicy();
        for (int failure = 0; failure < AnchorSamplePolicy.Budget; failure++) stopped.Failed(0f, AnchorSampleFailure.NoCamera);
        bool ran = false;
        AnchorSample late = stopped.Attempt(0f, () => { ran = true; return AnchorSample.Missing(AnchorSampleFailure.NoIcon); }, out bool spentLate);
        check(ran && !spentLate && !late.Measured,
            "a watch really reads the HUD, and a watch that finds nothing answers no give-up: the level is abandoned exactly once");
        check(stopped.Abandoned && stopped.Watches == 1 && stopped.Attempts == 0 && stopped.Failures == AnchorSamplePolicy.Budget,
            "and the watch spends nothing at all - no attempt, no failure - so a spent budget costs no poll and no second give-up line");
    }

    private static void TheGiveUpEndsTheRoundAndSaysWhy(Action<bool, string> check)
    {
        var policy = new AnchorSamplePolicy();
        float now = 0f;
        int abandonAt = -1;
        for (int attempt = 0; attempt < 64 && abandonAt < 0; attempt++)
        {
            if (!policy.Due(now)) { now += 10f; continue; }
            policy.Began(now);
            if (policy.Failed(now, AnchorSampleFailure.Unmeasurable)) abandonAt = policy.Attempts;
            now += 10f;
        }
        check(abandonAt == AnchorSamplePolicy.Budget,
            $"the round gives up on the {AnchorSamplePolicy.Budget}th failed measurement, and the failure that spends the budget says so");
        check(policy.Abandoned && policy.Failures == AnchorSamplePolicy.Budget,
            "the policy is abandoned once the budget is spent");
        check(policy.LastFailure == AnchorSampleFailure.Unmeasurable,
            "the reason the give-up line prints is the last real one, not a placeholder");
        // 1.19.26: an abandoned round is no longer silent for the level - it waits out one WatchInterval and then watches. The
        // settle-cadence poll is still refused, which is what this block was written for.
        check(policy.Attempts == AnchorSamplePolicy.Budget,
            "the attempt count the give-up line reports is the number of measurements really taken");
        check(policy.Due(now + 1000f),
            "an abandoned round is still looked at once every WatchInterval, instead of going silent for the rest of the level");
        check(!policy.WatchDue(now + 1000f + AnchorSamplePolicy.WatchInterval * 0.5f),
            "but never twice inside one interval: the watch is a cadence, not a poll");
        check(policy.Waited > 0f && policy.Waited < AnchorSamplePolicy.MaximumRetryDelay * (AnchorSamplePolicy.Budget + 1),
            "the wait the give-up line reports is the sum of the delays, inside its own bound");

        int failures = policy.Failures;
        check(policy.Failed(now, AnchorSampleFailure.NoCamera) && policy.Failures == failures,
            "a failure reported after the give-up changes nothing: the verdict is already written");

        policy.Restart();
        check(!policy.Abandoned && policy.Attempts == 0 && policy.Waited == 0f && policy.Due(0f),
            "a new level measures again from a whole budget, which is what makes the give-up a per-level verdict");

        check(AnchorSamplePolicy.Describe(AnchorSampleFailure.NoCamera).Contains("camera")
              && AnchorSamplePolicy.Describe(AnchorSampleFailure.NoIcon).Contains("icon")
              && AnchorSamplePolicy.Describe(AnchorSampleFailure.Unmeasurable).Contains("off screen")
              && AnchorSamplePolicy.Describe(AnchorSampleFailure.None).Length > 0,
            "each failure the give-up line can name is described in words a reader can act on");
        check(AnchorSamplePolicy.Describe(AnchorSampleFailure.NoCamera) != AnchorSamplePolicy.Describe(AnchorSampleFailure.NoIcon),
            "and the three reasons are not collapsed into one sentence");
    }

    // ------------------------------------------------------------- the flood: a line per attempt, not per repaint
    //
    // The recorded expedition holds 625 of these lines, four to a second, and every one of them reads "attempt 1 of
    // 12 ... 11 retry(s) left". The round underneath the line was being rebuilt on every repaint, so the figure the
    // line printed never advanced and the retry delay never gated it: the budget could not be spent and the give-up
    // line was unreachable. The lines are owed by the round itself now - `DueToReport` answers true for an attempt
    // that has not been written yet and false for a tick that was not one - which is what puts them on the schedule
    // (0.5 s, 1 s, 2 s, then the 4 s ceiling) instead of on the frame rate. Everything below drives the real policy
    // through a model of `SampleAnchor`, and the last paragraph reads the production source and says so.
    private const float AnchorFrame = 1f / 60f;

    private static void TheAnchorLinesFollowTheScheduleAndStopAtTheGiveUp(Action<bool, string> check)
    {
        // Ten seconds of a silent HUD at the frame rate the panel is really polled on. A per-frame sampler would
        // attempt 600 times; this one attempts five - one immediately, then half a second, one, two and four
        // seconds later - and writes exactly one line for each.
        int attempts = RunSilentHud(600, out List<int> written, out List<int> left, out bool abandoned);
        check(attempts == 5,
            $"ten seconds of failing frames at 60 Hz cost {attempts} measurements, not one per frame");
        check(!abandoned && written.Count == 1,
            $"1.19.22: a HUD whose answer cannot change writes one failure line - the first, with its reason: {written.Count} line(s) for five attempts, never five identical warnings");
        check(string.Join(",", written) == "1",
            $"the one line printed is the round's own first attempt: {string.Join(",", written)}");
        bool advancing = true, countingDown = true;
        for (int index = 1; index < written.Count; index++)
        {
            if (written[index] <= written[index - 1]) advancing = false;
            if (left[index] >= left[index - 1]) countingDown = false;
        }
        check(advancing,
            "no line repeats an attempt number - the 625-line expedition printed \"attempt 1 of 12\" every single time");
        check(countingDown && left.Count > 0 && left[0] == AnchorSamplePolicy.Budget - 1,
            $"and the retries left in that first line count down from {AnchorSamplePolicy.Budget - 1}: {string.Join(",", left)}");

        // The whole budget, at the same frame rate: the schedule spends it and the give-up closes the round.
        int frames = 60 * 50;   // fifty seconds, past the 39.5 s the twelve attempts really span
        int full = RunSilentHud(frames, out List<int> fullWritten, out _, out bool fullAbandoned);
        check(full == AnchorSamplePolicy.Budget && fullAbandoned,
            $"fifty seconds of a HUD that answers nothing spends the whole {AnchorSamplePolicy.Budget}-attempt budget and gives up (attempts={full})");
        check(fullWritten.Count == 2,
            $"1.19.22: the log costs exactly two lines - the first failure with its reason (attempt 1) and the give-up - {fullWritten.Count} line(s) in fifty seconds of frames, never twelve identical warnings");
        check(fullWritten.Count > 0 && fullWritten[fullWritten.Count - 1] == AnchorSamplePolicy.Budget,
            "the last line is the give-up line, and it speaks for the attempt that spent the budget");
        bool fullAdvancing = true;
        for (int index = 1; index < fullWritten.Count; index++) if (fullWritten[index] <= fullWritten[index - 1]) fullAdvancing = false;
        check(fullAdvancing, $"every line of the whole round advances the attempt: {string.Join(",", fullWritten)}");

        // Nothing at all once the round is abandoned - three times as many frames, the same log.
        int after = RunSilentHud(frames * 3, out List<int> afterWritten, out _, out bool afterAbandoned);
        check(after == AnchorSamplePolicy.Budget && afterAbandoned && afterWritten.Count == fullWritten.Count
              && string.Join(",", afterWritten) == string.Join(",", fullWritten),
            $"an abandoned round prints nothing more however long the level runs: {afterWritten.Count} line(s) after 150 s, the same as after 50 s (attempts={after})");

        // The policy itself refuses the line, so the caller cannot print one by asking again.
        var spent = new AnchorSamplePolicy();
        for (int attempt = 0; attempt < AnchorSamplePolicy.Budget; attempt++)
            spent.Attempt(attempt, () => AnchorSample.Missing(AnchorSampleFailure.NoCamera), out _);
        check(spent.Abandoned && !spent.DueToReport,
            "a round that has spent its budget owes no line for the attempt that abandoned it either");
        spent.Reported();
        check(!spent.DueToReport && spent.Lines == 0,
            "and writing a line cannot re-arm an abandoned round: the report is spent nowhere after the give-up");

        // A successful read resets what the schedule counts, not the round's identity.
        var recovered = new AnchorSamplePolicy();
        float now = 0f;
        int lines = 0;
        for (int frame = 0; frame < 240; frame++)     // four seconds: attempts 1..4, one line each
        {
            now = frame * AnchorFrame;
            if (!recovered.Due(now)) continue;
            if (!recovered.Attempt(now, () => AnchorSample.Missing(AnchorSampleFailure.NoCamera), out _).Measured && recovered.DueToReport)
            {
                if (recovered.FreshWarning) lines++;   // 1.19.22: the first failure prints, its four identical repeats do not
                recovered.Reported();
            }
        }
        check(recovered.Attempts == 4 && lines == 1,
            $"four failing attempts in four seconds, one line - the first reads the reason, the identical repeats are suppressed (attempts={recovered.Attempts}, lines={lines})");
        AnchorSample good = recovered.Attempt(now, () => AnchorSample.Of(new LayoutRect(1848f, 526f, 40f, 40f)), out bool gaveUpLate);
        check(good.Measured && !gaveUpLate && recovered.Failures == 0,
            "the HUD's camera comes up and the sample measures: the failure streak is over");
        check(recovered.Attempts == 5 && recovered.Lines == 4 && recovered.DueToReport,
            "the round keeps counting its attempts, and the measurement it just took owes no failure line");
        recovered.Attempt(now + 0.25f, () => AnchorSample.Missing(AnchorSampleFailure.NoCamera), out _);
        check(recovered.Attempts == 6 && recovered.Failures == 1,
            "the next miss is a first miss again, not the sixth failure of a streak");
        check(Math.Abs(AnchorSamplePolicy.DelayAfter(recovered.Failures) - AnchorSamplePolicy.FirstRetryDelay) < 0.001f,
            "so it retries in half a second, which is the sequence a successful read resets");
        check(Math.Max(0, AnchorSamplePolicy.Budget - recovered.Failures) == AnchorSamplePolicy.Budget - 1,
            "and the retries left the next line prints count down from eleven again, not from the eighth a long streak would leave");
        recovered.Reported();
        check(!recovered.DueToReport && recovered.Attempts == 6,
            "that line is spent exactly once, so the same attempt is never printed twice");
        recovered.Restart();
        check(recovered.Attempts == 0 && recovered.Lines == 0 && recovered.Failures == 0 && !recovered.Abandoned,
            "a new level - or a resolution change, or a config reload - forgets the count and the lines together");
        recovered.Attempt(500f, () => AnchorSample.Missing(AnchorSampleFailure.NoCamera), out _);
        check(recovered.Attempts == 1 && recovered.DueToReport,
            "which is what makes the new round's first line say \"attempt 1\" again, exactly as the latch this replaces did");

        // The production wiring: the panel asks the round and keeps no latch of its own for a re-anchor to clear.
        string root = SourceRoot();
        check(root != null, "the anchor flood scenario locates the production sources");
        if (root == null) return;
        string panel = File.ReadAllText(Path.Combine(root, "PanelBehaviour.cs"));
        check(!panel.Contains("_loggedAnchorFailure"),
            "no one-shot failure latch is left in the panel - that latch cleared on every re-anchor, which is what printed the same first attempt four times a second");
        check(panel.Contains("if (_anchorPolicy.DueToReport)"),
            "the failure line is gated by the round's own report test, so a tick that was not an attempt prints nothing");
        check(CountOf(panel, "_anchorPolicy.Reported();") == 2,
            "both lines the panel writes - the failure and the give-up - spend the report, so neither can be printed twice");
        check(CountOf(panel, "_anchorPolicy.Restart();") >= 2,
            "and both a re-anchor and a new level restart the round, which is what resets the attempt number and the lines with it");
    }

    // `PanelBehaviour.Refresh` -> `SampleAnchor`, frame for frame: the sampling guard, the policy's throttle, the whole
    // attempt through the policy, and the two lines it writes through `DueToReport`. Nothing here is a second opinion
    // about the schedule; it is the model `AnchorAndFinalGeometryScenarios` already uses for the same gate.
    private static int RunSilentHud(int frames, out List<int> written, out List<int> left, out bool abandoned)
    {
        var policy = new AnchorSamplePolicy();
        written = new List<int>();
        left = new List<int>();
        bool unavailable = false;
        float now = 0f;
        for (int frame = 0; frame < frames; frame++)
        {
            // The frame's own timestamp, multiplied rather than accumulated, so a retry boundary that falls between two
            // frames lands where the schedule says and not where float drift puts it.
            now = frame * AnchorFrame;
            if (unavailable) continue;                  // a given-up anchor is not sampled at all
            if (!policy.Due(now)) continue;             // the retry schedule, never the frame rate
            AnchorSample sample = policy.Attempt(now, () => AnchorSample.Missing(AnchorSampleFailure.NoCamera), out bool gaveUp);
            if (sample.Measured) continue;
            if (policy.DueToReport)
            {
                if (policy.FreshWarning)   // 1.19.22: an identical failure prints nothing new, so only the first is written
                {
                    written.Add(policy.Attempts);
                    left.Add(Math.Max(0, AnchorSamplePolicy.Budget - policy.Failures));
                }
                policy.Reported();
            }
            if (!gaveUp) continue;
            unavailable = true;
            written.Add(policy.Attempts);               // the give-up line, which speaks for that attempt
            policy.Reported();
        }
        abandoned = policy.Abandoned;
        return policy.Attempts;
    }

    // ------------------------------------------------------------- the wording, read from the production source
    //
    // Static source checks only: they say the runtime calls these policies and that the two sentences the
    // diagnosis called untrue can no longer be printed. They are not runtime proof.
    private static void TheDiagnosticTextTellsTheTruth(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the 1.19.16 diagnostics regression locates the production sources");
        if (root == null) return;

        string sync = File.ReadAllText(Path.Combine(root, "CombatSync.cs"));
        string panel = File.ReadAllText(Path.Combine(root, "PanelBehaviour.cs"));
        string plugin = File.ReadAllText(Path.Combine(root, "Plugin.cs"));
        string overlay = File.ReadAllText(Path.Combine(root, "OverlayPolicy.cs"));

        // The level clock is the one thing the runtime decides this on.
        check(sync.Contains("private static readonly LevelDiagnosticClock LevelClock = new LevelDiagnosticClock();"),
            "the level clock and its two latches are one object, not three loose fields");
        check(sync.Contains("if (LevelClock.Enter(phase, now))"),
            "every tick hands its phase to the clock before anything else is decided");
        check(sync.Contains("LevelPhase phase = PhaseOf(state);") && CountOf(sync, "LevelClock.Enter(") == 1,
            "the phase is mapped once per tick, and the clock is entered exactly there");
        check(CountOf(sync, "Heard.Clear();") >= 3,
            "the per-level evidence is dropped on the level transition as well as by the lobby and the expedition reset");
        int enterAt = sync.IndexOf("if (LevelClock.Enter(phase, now))", StringComparison.Ordinal);
        int heardAt = sync.IndexOf("Heard.Clear();", enterAt < 0 ? 0 : enterAt, StringComparison.Ordinal);
        check(enterAt >= 0 && heardAt > enterAt,
            "and it is dropped inside that transition, so the clock and the evidence cannot describe different levels");
        check(!sync.Contains("if (state == eGameStateName.InLevel && _levelSince < 0f)")
              && !sync.Contains("if (state != eGameStateName.InLevel) _levelSince = -1f;"),
            "the two lines whose only executed half was the one inside the level are gone");
        check(!sync.Contains("_levelSince") && !sync.Contains("_loggedNoInbound") && !sync.Contains("_loggedOutboundRows"),
            "no per-session field is left for the level clock to be read from");

        // The alarm reads the per-level figure and says what it is.
        check(sync.Contains("if (LevelClock.NoInboundOwed(now, Exchange.HumanPeers, Heard.Count))"),
            "the inbound warning is asked of the level clock, with the per-level evidence");
        check(sync.Contains("if (rows > 0 && LevelClock.OutboundOwed)"),
            "the outbound line is asked of the level clock too, so it is spent again on every level");
        check(sync.Contains("squad sync inbound: {LevelClock.Seconds(now):0}s since this level began"),
            "the warning counts this level, and says so");
        check(!sync.Contains("in the level with") && !sync.Contains("not one packet heard from any of them"),
            "the sentence that described a per-session figure as \"in the level\" can no longer be printed");
        check(!sync.Contains("a zero means nothing arrived at all"),
            "the parenthetical that read receivedButUnusable=0 as \"nothing arrived\" is gone");
        check(sync.Contains("it counts nothing else, so a zero there says no packet was undecodable, never that none arrived"),
            "and the figure now says what it counts and what it does not");

        // The anchor: a bounded retry, a lock line, and one give-up line that names reason and count.
        check(panel.Contains("private readonly AnchorSamplePolicy _anchorPolicy = new AnchorSamplePolicy();"),
            "the anchor's retry budget is the policy class, not a latch of its own");
        check(panel.Contains("if (!_anchorPolicy.Due(now)) return;")
              && panel.Contains("SampleAnchor(layer, Time.unscaledTime)"),
            "the sample is gated by the policy's own due test, so a spent budget costs one comparison per refresh");
        check(panel.Contains("flashlight anchor locked after"),
            "a sample that does succeed still writes the lock line the field log was missing");
        check(panel.Contains("was given up on for this level after {_anchorPolicy.Attempts} attempt(s) and {_anchorPolicy.Waited:0.#}s of waiting"),
            "giving up is a line of its own: it names the attempt count and the wait");
        check(panel.Contains("the measurement is retried in {AnchorSamplePolicy.DelayAfter(_anchorPolicy.Failures):0.##}s")
              && panel.Contains("{Math.Max(0, AnchorSamplePolicy.Budget - _anchorPolicy.Failures)} retry(s) left"),
            "the first failure reports the retry delay and the attempts left, so the schedule is legible in a log");
        check(!panel.Contains("the flashlight icon could not be measured (camera="),
            "the once-per-session verdict line that read a transient null as a permanent absence is gone");
        check(panel.Contains("_anchorFallback") && CountOf(panel, "_anchorPolicy.Restart();") >= 2,
            "the configured position is drawn while the retries run, and both a re-anchor and a new level restart them");
        check(CountOf(panel, "!_anchorUnavailable && !_anchorFallback") == 1
              && CountOf(panel, "still settling: draw nothing rather than jump") == 1,
            "the settle window still draws nothing rather than jump, and only the failure path falls back");

        // The config description may only say what the runtime does.
        check(!plugin.Contains("and writes the new value back to this file"),
            "the ShowImpactBoard description no longer claims F8 writes that key");
        check(plugin.Contains("从不写这个键：F8 保存的是 ShowPanel 和 ShowResultOverlay"),
            "and it says what F8 really stores instead - the corrected wording is now Chinese");
        check(overlay.Contains("public static ToggleAction Apply(bool visible) => new ToggleAction(visible, visible, true);"),
            "the write set the description now matches is still the two overlay switches - this release changed no key of it");
        ToggleAction off = TogglePolicy.Apply(false);
        check(!off.ShowPanel && !off.ShowResultOverlay && off.Save,
            "F8 still flips exactly ShowPanel and ShowResultOverlay and saves them, as the corrected text says");

        check(panel.Contains("AnchorSample sample = _anchorPolicy.Attempt(now, () => ReadAnchor(layer), out bool gaveUp);"),
            "the panel runs the whole attempt through the policy, so the read that can throw is inside the budget");
        check(panel.Contains("private static AnchorSample ReadAnchor(PlayerGuiLayer layer)") && !panel.Contains("_anchorPolicy.Began(now);"),
            "and the read is one body of its own: no `Began` is left outside a matching verdict");

        Console.WriteLine("DIAGNOSTICS: LEVEL_CLOCK_PER_LEVEL + ANCHOR_BOUNDED_RETRY (static source checks; not a runtime behaviour test)");
    }

    private static int CountOf(string source, string needle)
    {
        int count = 0;
        int at = -1;
        while ((at = source.IndexOf(needle, at + 1, StringComparison.Ordinal)) >= 0) count++;
        return count;
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj"))) directory = directory.Parent;
        return directory != null ? directory.FullName : null;
    }
}
