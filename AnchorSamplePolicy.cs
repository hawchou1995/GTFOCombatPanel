using System;

namespace HawChow.GTFOCombatPanel;

// ---------------------------------------------------------------------------------------------------
// AnchorSamplePolicy.cs - why the flashlight anchor may be sampled again, and when it may not.
//
// The in-level table hangs under the native flashlight icon, and that icon's screen rectangle is sampled
// through the HUD's own camera until it stops moving. A sample can fail for a reason that is nobody's
// fault and does not last: `GuiLayerBase.GetWorldCamera()` answers null for a moment while a level is
// still coming up, and the icon has no renderer in the same window. The build this replaces treated the
// first such frame as a verdict - one latch, one `camera=null` line, and the table stood on the
// configured PositionX/PositionY for the rest of the session, with nothing in the log saying the anchor
// had been given up on: the `flashlight anchor locked` line it would have written was simply never
// written again.
//
// What replaces it: a bounded retry. Every failure buys one more attempt after a delay that starts at
// half a second and doubles up to a ceiling, so a startup that lasts a couple of seconds is measured
// again rather than given up on, and the attempt budget is finite, so an anchor that really never appears
// costs a dozen measurements and then one explicit line naming the reason, the attempt count and the
// level it gave up for. A new level, a configuration reload or a resolution change grants a fresh budget
// to an anchor that has not locked yet - a locked anchor is never sampled again at all, which is the
// caller's own flag and the reason a working table never moves for this. A read that *throws* is one more
// failed measurement rather than a free one: `Attempt` runs the whole attempt, so no path can leave the
// budget untouched.
// Nothing here runs per frame: the caller asks `Due` first, and a refused tick costs one comparison.
//
// Free of Unity and BepInEx on purpose, so all of it is asserted headlessly in
// tests/GTFOCombatPanel.Smoke.
// ---------------------------------------------------------------------------------------------------

// What stopped a sample from producing a rectangle. The distinction is diagnostic: "the HUD's camera is
// not there yet" and "the icon has no renderer yet" are different startup states, and a caller that could
// only say "null" cannot tell a page that is still loading from one this build reads wrongly.
public enum AnchorSampleFailure { None = 0, NoCamera = 1, NoIcon = 2, Unmeasurable = 3 }

// What one attempt at the anchor answered: the rectangle, or why there is none.
//
// `Error` carries the message of a read that threw, so the line that reports the failure can say so instead of
// collapsing an exception into the blandest of the three reasons. It is never a verdict about the icon: every
// state behind this struct is one the HUD passes through on its way up.
public readonly struct AnchorSample
{
    public readonly LayoutRect Rect;
    public readonly AnchorSampleFailure Failure;
    public readonly string Error;

    public AnchorSample(LayoutRect rect, AnchorSampleFailure failure, string error = null)
    {
        Rect = rect; Failure = failure; Error = error;
    }

    public static AnchorSample Of(LayoutRect rect) { return new AnchorSample(rect, AnchorSampleFailure.None); }

    public static AnchorSample Missing(AnchorSampleFailure failure) { return new AnchorSample(default, failure); }

    // A read that threw is a rectangle that could not be read - the fact `Unmeasurable` already records - and the
    // exception's own message rides along so the log line can name the read that threw.
    public static AnchorSample Thrown(string error) { return new AnchorSample(default, AnchorSampleFailure.Unmeasurable, error); }

    // Whether this attempt answered a rectangle. The geometry is part of the answer rather than an assumption about
    // it: an attempt that never ran - a round that has already spent its budget - answers no reason at all, and it
    // must not read as a measurement just because it has nothing to complain about.
    public bool Measured { get { return Failure == AnchorSampleFailure.None && Rect.Width > 0f && Rect.Height > 0f; } }
}

// The retry budget behind one anchor measurement round.
//
// A round starts at the first attempt, or again after `Restart` (a new level, a config reload, a
// resolution change). Within a round the schedule is: the first failure retries after `FirstRetryDelay`,
// and each further failure doubles the delay up to `MaximumRetryDelay`. The budget is `Budget` failures;
// the failure that spends the last one answers true from `Failed` and leaves the policy `Abandoned`, at
// which point `Due` answers false for ever until `Restart`. A measurement that succeeds does not end the
// round - the anchor still has to settle - it only returns the cadence to `SettleInterval` and clears the
// failure streak, so a transient miss cannot make the next real sample wait four seconds.
public sealed class AnchorSamplePolicy
{
    // How many failed measurements one round may spend. Twelve failures on the schedule below span 39.5 s of wall
    // time - 0.5 + 1 + 2 and nine more at the 4 s ceiling - which is far longer than a level's HUD takes to come up
    // and short enough that a build which can never measure the icon falls back within one expedition.
    public const int Budget = 12;
    // The cadence while the anchor measures: the same 0.25 s poll the table itself refreshes on, so the
    // settle window (`PanelBehaviour` holds the icon still for a second) is unchanged.
    public const float SettleInterval = 0.25f;
    // The first retry, and the ceiling the doubling walks to. The ceiling is what keeps a long startup
    // from turning into a per-frame poll of the HUD's camera.
    public const float FirstRetryDelay = 0.5f;
    public const float MaximumRetryDelay = 4f;
    // The doubling is bounded before it is clamped, so a caller that keeps failing cannot overflow it.
    private const int MaximumDoublings = 8;

    private float _nextAttempt;
    private bool _attempted;

    public int Attempts { get; private set; }
    public int Failures { get; private set; }
    public bool Abandoned { get; private set; }
    public AnchorSampleFailure LastFailure { get; private set; }
    // How long this round has spent waiting between attempts. Printed with the give-up line so the reason
    // and the cost are stated together.
    public float Waited { get; private set; }

    // The delay owed after `failures` consecutive failed measurements. Zero failures means the anchor is
    // measuring and the ordinary settle cadence applies.
    public static float DelayAfter(int failures)
    {
        if (failures <= 0) return SettleInterval;
        int doublings = failures - 1;
        if (doublings > MaximumDoublings) doublings = MaximumDoublings;
        float delay = FirstRetryDelay * (float)Math.Pow(2d, doublings);
        if (!float.IsFinite(delay)) return MaximumRetryDelay;
        return delay < MaximumRetryDelay ? delay : MaximumRetryDelay;
    }

    // Whether this tick is allowed to attempt a measurement at all. A round that is waiting a retry
    // delay, and a round that has spent its budget, both answer false - and a clock that cannot be read
    // is not an attempt either, so a broken timer cannot turn this into a busy loop.
    public bool Due(float now)
    {
        if (Abandoned) return false;
        if (!_attempted) return true;
        if (!float.IsFinite(now)) return false;
        return now >= _nextAttempt;
    }

    // One attempt starts, before the HUD is read. It spends one of `Budget` attempts; what it does *not* do is
    // record a failure - that is `Failed`'s job - which is why the whole attempt is run through `Attempt` below
    // rather than spread over a caller that can be interrupted between the two by an exception.
    public void Began(float now)
    {
        if (Abandoned) return;
        Attempts++;
        _attempted = true;
        if (float.IsFinite(now) && float.IsFinite(_nextAttempt) && now > _nextAttempt) _nextAttempt = now;
    }

    // One attempt as a whole: the attempt is begun, the sample is taken, and the verdict is recorded - with the
    // budget spent on *every* path, including a sample that throws.
    //
    // The sample is a delegate because the only read that can really throw is the HUD read in `PanelBehaviour`,
    // and that read used to sit outside this class: `Began` was called there, an exception out of the projection
    // left `Failed` uncalled, and the retry delay therefore never advanced - so a HUD that throws was measured
    // again on every settle tick for the rest of the level, which is exactly the busy loop this class exists to
    // prevent. Run through here, a throw is one more failed measurement: it spends one of `Budget`, it earns the
    // retry delay of the streak it belongs to, and it is reported as the unreadable rectangle it really was -
    // with the exception's own message, never as a silent give-up. `gaveUp` answers true only on the failure
    // that spent the last one, so the caller writes its one give-up line exactly once.
    public AnchorSample Attempt(float now, Func<AnchorSample> sample, out bool gaveUp)
    {
        gaveUp = false;
        if (Abandoned) return AnchorSample.Missing(AnchorSampleFailure.None);
        Began(now);
        AnchorSample answer;
        try { answer = sample != null ? sample() : AnchorSample.Missing(AnchorSampleFailure.Unmeasurable); }
        catch (Exception error) { answer = AnchorSample.Thrown(error != null ? error.Message : null); }
        if (answer.Measured)
        {
            Measured(now);
            return answer;
        }
        AnchorSampleFailure reason = answer.Failure == AnchorSampleFailure.None ? AnchorSampleFailure.Unmeasurable : answer.Failure;
        gaveUp = Failed(now, reason);
        return new AnchorSample(answer.Rect, reason, answer.Error);
    }

    // The sample produced a rectangle. The failure streak - and with it the retry delay - is over; the
    // attempt count is not, because it describes the round and is what a give-up line reports.
    public void Measured(float now)
    {
        Failures = 0;
        LastFailure = AnchorSampleFailure.None;
        _nextAttempt = float.IsFinite(now) ? now + SettleInterval : float.PositiveInfinity;
    }

    // The sample produced nothing. Answers true when this failure was the last one the budget allowed.
    public bool Failed(float now, AnchorSampleFailure reason)
    {
        if (Abandoned) return true;
        Failures++;
        LastFailure = reason == AnchorSampleFailure.None ? AnchorSampleFailure.Unmeasurable : reason;
        float delay = DelayAfter(Failures);
        Waited += delay;
        _nextAttempt = float.IsFinite(now) ? now + delay : float.PositiveInfinity;
        if (Failures < Budget) return false;
        Abandoned = true;
        return true;
    }

    // A fresh round: a new level, a configuration reload, a resolution change. A locked anchor is the
    // caller's own flag and is not touched here - this only forgets the verdict about a failed one.
    public void Restart()
    {
        Attempts = 0;
        Failures = 0;
        Abandoned = false;
        LastFailure = AnchorSampleFailure.None;
        Waited = 0f;
        _nextAttempt = 0f;
        _attempted = false;
    }

    // What the last failure was, in the words a log line uses. Nothing here claims the anchor does not
    // exist: every one of these is a state the HUD passes through.
    public static string Describe(AnchorSampleFailure reason)
    {
        switch (reason)
        {
            case AnchorSampleFailure.NoCamera: return "the HUD's own camera answered null";
            case AnchorSampleFailure.NoIcon: return "the flashlight icon had no renderer";
            case AnchorSampleFailure.Unmeasurable: return "the icon's bounds projected off screen";
            default: return "no attempt was made";
        }
    }
}
