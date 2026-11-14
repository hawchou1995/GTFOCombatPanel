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
// not there yet" and "no camera can be shown to draw this icon" are different answers, and a caller that could
// only say "null" cannot tell a page that is still loading from one this build reads wrongly. `NoProjection` is
// the second kind: the whole chain was read - the element's own layer, whether it carries a `RectTransform` on a
// screen-space canvas, the canvas' render mode, and every candidate camera's culling mask - and none of it can put
// the element on the window where it is. 1.19.27 narrows it to what it now really means: it is only reached when
// the canvas-geometry answer is unavailable too (no `RectTransform`, or a canvas that is not screen-space), because
// an element inside a screen-space canvas no longer needs any camera to prove where it was drawn.
public enum AnchorSampleFailure { None = 0, NoCamera = 1, NoIcon = 2, Unmeasurable = 3, NoProjection = 4 }

// What one attempt at the anchor answered: the rectangle and which native element it came from, or why there is none.
//
// `Error` carries the message of a read that threw, so the line that reports the failure can say so instead of
// collapsing an exception into the blandest of the three reasons. It is never a verdict about the icon: every
// state behind this struct is one the HUD passes through on its way up.
//
// `Source` is the element the rectangle was measured from - the exact flashlight icon, or the same row's `Background
// Fade` when the icon could not be measured at all. A sample that answered no rectangle names no source: a failure is
// not a statement about either element.
public readonly struct AnchorSample
{
    public readonly LayoutRect Rect;
    public readonly AnchorSampleFailure Failure;
    public readonly string Error;
    public readonly AnchorElementSource Source;

    public AnchorSample(LayoutRect rect, AnchorSampleFailure failure, string error = null, AnchorElementSource source = AnchorElementSource.None)
    {
        Rect = rect; Failure = failure; Error = error; Source = source;
    }

    public static AnchorSample Of(LayoutRect rect, AnchorElementSource source = AnchorElementSource.None)
    {
        return new AnchorSample(rect, AnchorSampleFailure.None, null, source);
    }

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
//
// The round also owns the *log* cadence, because that is the same question: a line is owed once per attempt and
// once per attempt only. `DueToReport` answers true for the attempt that has not been written yet and false for
// every tick that was not one, so the log follows the schedule above instead of the frame rate, and the attempt
// that spends the budget writes no failure line at all - `Abandoned` has already refused it, and the give-up line
// the caller writes in its place is the last word on the round.
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

    // 1.19.26: after the budget is spent the round does not go silent for the rest of the level - it *watches*. One attempt
    // every `WatchInterval` seconds, with no budget behind it and no failure line of its own, so a HUD that grows the row's
    // `Background Fade` (or answers the camera) a minute later is still measured; a watch that measures anything ends the
    // give-up and restarts the round. Two seconds is 120 frames at 60 fps - eight times rarer than the slowest retry - and
    // costs one comparison per poll when it is not due, so nothing here is a poll of the HUD.
    public const float WatchInterval = 2f;
    // How many watch attempts this policy has made. Diagnostics only: printed beside the give-up line and asserted by the
    // timeline suite, which is what shows the round watching rather than waiting for ever. Deliberately *not* reset by
    // `Restart`, so a round that was revived by a watch can still say how many looks the level needed.
    public int Watches { get; private set; }
    // The next watch, while the budget is spent. Only read by `WatchDue`.
    private float _nextWatch;
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
    // How many of this round's attempts have already been written to the log. One line per attempt is the only cadence
    // that is both honest and bounded: the attempt number such a line prints is this round's own count, and the number
    // of lines one round can cost is the budget - never the number of frames the round was polled on.
    public int Lines { get; private set; }
    // 1.19.22: the reason the last failure line of this round accounted for, whether it was printed or suppressed.
    private AnchorSampleFailure _lastPrintedFailure;

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

    // This is the one clock in front of `SampleAnchor`. A round that is measuring answers on its settle/retry schedule; a
    // round whose budget is spent answers on the watch below - never "no" for ever, and never "yes" on every poll.
    public bool Due(float now)
    {
        if (Abandoned) return WatchDue(now);
        if (!_attempted) return true;
        if (!float.IsFinite(now)) return false;
        return now >= _nextAttempt;
    }

    // Whether the low-rate watch is owed an attempt: only for a round whose budget is spent, and only once per
    // `WatchInterval`. A HUD that can never be measured therefore costs one comparison per poll and one attempt every two
    // seconds - not a poll of the camera, and not the silence that made a late `Background Fade` unreachable.
    public bool WatchDue(float now)
    {
        if (!Abandoned) return false;
        if (!float.IsFinite(now)) return false;
        if (now < _nextWatch) return false;
        // The grant is recorded here, exactly as the lookup records its own walks, so asking twice inside one interval answers
        // false the second time: the watch is a cadence, not a poll.
        _nextWatch = now + WatchInterval;
        return true;
    }

    // Whether the attempt that has just run still owes the log a line. True exactly once per attempt of an open round:
    // a line that has been written cannot be written again for the same attempt, and a tick the throttle refused was
    // not an attempt at all, so it is owed nothing. A round that has been abandoned answers false for ever - the
    // failure that spent the last retry is the one the give-up line speaks for.
    public bool DueToReport
    {
        get { return !Abandoned && Attempts > Lines; }
    }

    // 1.19.22: whether the attempt's failure line would say something new. The first failure of a round always
    // prints; a later one prints only when its reason differs from the last accounted one - twelve identical
    // warnings about an answer that cannot change are the log flood this removes. The one-line-per-attempt
    // mechanics (`DueToReport` / `Reported`) are untouched: a suppressed line is still spent through `Reported`,
    // and the give-up line still speaks for the attempt that spent the budget.
    public bool FreshWarning
    {
        get { return !Abandoned && Attempts > 0 && LastFailure != _lastPrintedFailure; }
    }

    // The line for the attempt that has just run has been written. Idempotent, so a caller that logs the same attempt
    // twice - a give-up line after its own failure line, say - cannot spend the next attempt's line - and a no-op once
    // the round is abandoned, so nothing can re-arm a spent round into writing its failure line after the give-up: the
    // failed attempt that spent the budget is the one the give-up line speaks for.
    public void Reported()
    {
        if (Abandoned) return;
        if (Attempts > Lines) Lines = Attempts;
        // The reason is accounted for whether the caller printed the line or suppressed it: `FreshWarning` asks
        // about what the last line said, and a suppressed repeat says nothing new.
        _lastPrintedFailure = LastFailure;
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
    //
    // 1.19.26: on a round whose budget is spent this runs a *watch* instead. The watch spends nothing, prints nothing
    // (the give-up line already spoke for this level) and increments `Watches`; a watch that measures anything ends the
    // give-up and restarts the round, which is what lets a `Background Fade` that appears a minute later be used without
    // F8, a level change or a manual re-anchor. `gaveUp` stays false for a watch, so a watcher can never write a second
    // give-up line for the same level.
    public AnchorSample Attempt(float now, Func<AnchorSample> sample, out bool gaveUp)
    {
        gaveUp = false;
        bool watch = Abandoned;
        if (watch) Started(now);
        else Began(now);
        AnchorSample answer;
        try { answer = sample != null ? sample() : AnchorSample.Missing(AnchorSampleFailure.Unmeasurable); }
        catch (Exception error) { answer = AnchorSample.Thrown(error != null ? error.Message : null); }
        if (answer.Measured)
        {
            if (watch) Restart();
            Measured(now);
            return answer;
        }
        if (watch)
        {
            Watches++;
            return answer;
        }
        AnchorSampleFailure reason = answer.Failure == AnchorSampleFailure.None ? AnchorSampleFailure.Unmeasurable : answer.Failure;
        gaveUp = Failed(now, reason);
        return new AnchorSample(answer.Rect, reason, answer.Error, answer.Source);
    }

    // One watch begins: the next one is owed one `WatchInterval` later, whatever this one answers. A clock that cannot be
    // read is not a watch either, so a broken timer cannot turn the watch into a busy loop.
    private void Started(float now)
    {
        _nextWatch = float.IsFinite(now) ? now + WatchInterval : float.PositiveInfinity;
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
        // The first watch is owed one full interval after the give-up: the attempts that just ran are not owed a second look
        // on the next poll, and the give-up line is not immediately contradicted by another attempt.
        _nextWatch = float.IsFinite(now) ? now + WatchInterval : float.PositiveInfinity;
        return true;
    }

    // A fresh round: a new level, a configuration reload, a resolution change, or a watch that measured something. A locked
    // anchor is the caller's own flag and is not touched here - this only forgets the verdict about a failed one.
    public void Restart()
    {
        Attempts = 0;
        Failures = 0;
        Abandoned = false;
        LastFailure = AnchorSampleFailure.None;
        Waited = 0f;
        // The new round owes its first line again, exactly as it owed its first attempt: this is what the one-shot
        // `_loggedAnchorFailure` flag used to do from outside, and it is reset here so the report can never be
        // forgotten or restarted on its own.
        Lines = 0;
        _lastPrintedFailure = AnchorSampleFailure.None;
        _nextAttempt = 0f;
        _attempted = false;
        // `Watches` deliberately survives: it is the level's own count of looks, and a revived round still reports it.
        _nextWatch = 0f;
    }

    // What the last failure was, in the words a log line uses. Nothing here claims the anchor does not
    // exist: every one of these is a state the HUD passes through.
    //
    // 1.19.27: `NoProjection` no longer means "there is no camera that could draw this icon" on its own - a
    // camera is not required any more for an element that carries a RectTransform inside a screen-space
    // canvas. It is reached only when neither answer exists: no camera can be shown to draw the element *and*
    // the canvas cannot supply its rectangle either. The words say both halves, so a reader of the give-up line
    // cannot conclude that the element was unmeasurable for lack of a camera alone.
    public static string Describe(AnchorSampleFailure reason)
    {
        switch (reason)
        {
            case AnchorSampleFailure.NoCamera: return "the HUD's own camera answered null";
            case AnchorSampleFailure.NoProjection: return "no camera that draws the icon could be shown to exist, and the element carries no RectTransform on a screen-space canvas from which its own rectangle could be computed either (its layer, its RectTransform, the canvas render mode and every candidate camera's culling mask were all read)";
            case AnchorSampleFailure.NoIcon: return "the flashlight icon had no renderer";
            // 1.19.29: the second half used to say the rectangle "was not a usable rectangle", which is only one of the two
            // ways that answer fails. On the recorded level nothing was projected at all - there was no camera to project the
            // element's corners with - so the words now name that, and the attempt's own authorization (`Describe(choice,
            // measured)` in `AnchorCameraChoice`) is printed right beside this reason on every failure line.
            case AnchorSampleFailure.Unmeasurable: return "the icon's bounds projected off screen (or could not be read), and the rectangle its own canvas laid out could not be projected into a usable window rectangle either - no camera to project it with, a projection that answered no usable rectangle, or a rectangle the window does not cover";
            default: return "no attempt was made";
        }
    }
}

// Which native element the current measurement round's rectangle comes from, and whether a change has to restart the caller's
// settle window.
//
// Kept here, beside the samples it describes, so the one consequence a change of source has can be asserted headlessly: the
// flashlight icon and the row's `Background Fade` do not share a position, so a sample that changes source is a jump and not
// drift. Measuring the jump as movement and then locking on the far side of it would freeze the table on a rectangle read
// while the HUD was still coming up.
public sealed class AnchorSourceTracker
{
    public AnchorElementSource Source { get; private set; }

    // Answers true when the caller's settle window has to start again - i.e. the source changed between two measured samples.
    // The first sample of a round is not a change: there is no window to restart yet.
    public bool Observe(AnchorElementSource source)
    {
        bool restarts = AnchorSourceLadder.RestartsSettle(Source, source);
        Source = source;
        return restarts;
    }

    // A new round (a new level, a configuration reload, a resolution change): nothing has been measured from either element.
    public void Restart() { Source = AnchorElementSource.None; }
}

// What one walk of the icon display's children answered.
//
// `Unreadable` is the case this type exists for: a walk that threw says nothing about whether the element is there. Caching
// such a walk as `Missing` is what turned one transient exception into "this level has no Background Fade" for the rest of
// the expedition - the lookup then answered the (empty) cache on every later attempt and never tried again.
public enum AnchorLookupOutcome
{
    None = 0,
    Found = 1,
    Missing = 2,
    Unreadable = 3
}

// The cache behind one recursive lookup: one walk per owner while the answer is a hit, and a *spaced* retry while it is a miss -
// never a walk per frame, and never a miss that stands for the rest of the level.
//
// The owner is the object the walk started from (`m_iconDisplay.transform`), so a level change - which destroys that object -
// is a new owner and earns a new walk. `Due` is the only thing the caller has to ask; `Record` is the only thing it has to
// answer. Free of Unity on purpose, so the three cases are asserted headlessly.
//
// 1.19.26: a `Missing` answer used to be cached for the life of the owner. That is right for a HUD that really does not carry
// the element and wrong for one that is still being built: the row's own `Background Fade` is instantiated together with the
// icon display, but this lookup can be asked on the first sampling attempt of a level, before that row exists. The recorded
// 1.19.25 level did exactly that - `background-fade=missing` on attempt 1 - and because the miss was cached, every later
// attempt answered `yes(missing)` without touching the hierarchy, so the element that appeared a moment later could never be
// found for the whole expedition.
//
// A miss is therefore retried in two tiers, and both are rate-bounded rather than count-bounded:
//
//   fast   the first `MissWalks` walks are granted on the anchor's own attempts, `RetryFrames` frames apart - the twelve
//          attempts of the round, exactly as before.
//   watch  after that a walk is still granted, but only one per `WatchFrames`: the slow phase the anchor's own low-rate watch
//          needs, because a count-bounded miss and the round's bounded budget ran out together and the element that appears
//          later was then unreachable without F8, a level change or a manual re-anchor.
//
// A caller with no clock (the single-argument `Due`) gets the fast tier only: it can never ask "is a walk due" without a way to
// space them, so it is not handed an unbounded grant.
public sealed class AnchorElementLookup
{
    // Walks a real miss may cost at the anchor's own cadence. Twelve is the round's attempt budget (`AnchorSamplePolicy.Budget`),
    // so the fast phase costs exactly the twelve subtree walks the caller already takes - not one per repaint.
    public const int MissWalks = 12;
    // The least number of frames between two fast walks: half a second at sixty frames, which is the anchor's own first retry
    // delay, so a caller that asks every frame still cannot turn this into a per-frame scan.
    public const int RetryFrames = 30;
    // The least number of frames between two walks once the fast budget is spent: one second at sixty frames, which is half the
    // anchor watch's own interval - so the watch never finds the lookup "not due", and the hierarchy is walked at most once a
    // second for the rest of the level. That is the price of a row that can appear at any time; it is not a poll of the scene.
    public const int WatchFrames = 60;

    private object _owner;
    // Walks granted for the current miss streak. Counted where they are *granted* (`Due`), not where they are recorded, so a
    // caller that forgets to record cannot turn the budget back into a per-frame scan.
    private int _walks;
    private int _lastWalkFrame = int.MinValue;

    public AnchorLookupOutcome Outcome { get; private set; }
    public bool Hit { get { return Outcome == AnchorLookupOutcome.Found; } }
    public bool Missing { get { return Outcome == AnchorLookupOutcome.Missing; } }
    public bool Unreadable { get { return Outcome == AnchorLookupOutcome.Unreadable; } }
    // How many walks this miss streak has been granted, and whether the fast tier (the round's own attempts) is spent. A miss
    // that is retired from the fast tier is *not* silent: the slow tier below keeps looking at `WatchFrames`.
    public int Walks { get { return _walks; } }
    public bool MissRetired { get { return Outcome == AnchorLookupOutcome.Missing && _walks >= MissWalks; } }

    // Whether the walk has to run again for this owner: a different owner, nothing looked up yet, a walk that threw, a miss
    // that still has fast walks left, or - after the fast tier - the slow tier's own one-per-`WatchFrames` look. A hit is
    // answered from the cache without touching the hierarchy again, and a caller with no clock is never granted the slow tier.
    //
    // `frame` is the caller's frame counter (`Time.frameCount`); `int.MinValue` means "no clock", which is how a caller that
    // has none asks. A granted walk is remembered here, so `Record` does not have to carry the clock at all. A frame counter
    // that went backwards (a reload, a new scene's counter) counts as due rather than as a reason to skip for ever.
    public bool Due(object owner) { return Due(owner, int.MinValue); }

    public bool Due(object owner, int frame)
    {
        if (!ReferenceEquals(owner, _owner)) return Started(owner, frame, newOwner: true);
        switch (Outcome)
        {
            case AnchorLookupOutcome.Found: return false;
            case AnchorLookupOutcome.Missing:
                // No clock: only the fast tier can be bounded, so that is all a caller without one is granted.
                if (frame == int.MinValue) return _walks < MissWalks ? Started(owner, frame, newOwner: false) : false;
                if (_lastWalkFrame != int.MinValue)
                {
                    if (frame < _lastWalkFrame) return Started(owner, frame, newOwner: false);
                    int spacing = _walks < MissWalks ? RetryFrames : WatchFrames;
                    if (frame - _lastWalkFrame < spacing) return false;
                }
                return Started(owner, frame, newOwner: false);
            default:
                // `None` (nothing looked up yet) and `Unreadable` (a walk that threw says nothing about the element): both are
                // retried on the caller's own cadence, and neither is bounded here because neither claims anything.
                return Started(owner, frame, newOwner: false);
        }
    }

    // One walk is being granted: it is counted here, and the owner it is granted for is bound here, so a caller that asks
    // twice for the same new owner does not keep restarting the counters.
    private bool Started(object owner, int frame, bool newOwner)
    {
        if (newOwner)
        {
            _walks = 0;
            _lastWalkFrame = int.MinValue;
        }
        _owner = owner;
        if (_walks < int.MaxValue) _walks++;
        if (frame != int.MinValue) _lastWalkFrame = frame;
        return true;
    }

    // The result of one walk. `walked` is false when the walk itself threw: that is remembered as unreadable - never as
    // missing - so the next attempt tries again, and the miss budget is left where it was.
    public void Record(object owner, bool found, bool walked)
    {
        if (!ReferenceEquals(owner, _owner))
        {
            _owner = owner;
            _walks = 0;
        }
        _owner = owner;
        if (!walked)
        {
            Outcome = AnchorLookupOutcome.Unreadable;
            return;
        }
        if (found)
        {
            Outcome = AnchorLookupOutcome.Found;
            _walks = 0;
            return;
        }
        Outcome = AnchorLookupOutcome.Missing;
    }

    // A new owner, a level change, a configuration reload, a resolution change, or a hit whose element Unity has since
    // destroyed: nothing is known any more, and the miss budget starts over with it.
    public void Forget()
    {
        _owner = null;
        Outcome = AnchorLookupOutcome.None;
        _walks = 0;
        _lastWalkFrame = int.MinValue;
    }

    // The state in the words the panel's log lines print. A miss that is still being retried says so, so a log reader can tell
    // "we looked again twelve times" from "we looked once and gave up" - the two the old wording could not be told apart.
    public string Describe()
    {
        switch (Outcome)
        {
            case AnchorLookupOutcome.Found: return "yes(found)";
            case AnchorLookupOutcome.Missing: return MissRetired ? "yes(missing,slow-watch)" : "yes(missing,still-looking)";
            case AnchorLookupOutcome.Unreadable: return "yes(unreadable)";
            default: return "no(not-looked-for)";
        }
    }
}
