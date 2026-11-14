using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// 1.19.26, the closed loop the previous round left open: the anchor's *outer* budget and the lookup's *inner* budget both ran out
// at the twelfth attempt, and `_anchorUnavailable` then kept `SampleAnchor` from being entered at all - so a row's
// `Background Fade` that appeared after the first twelve samples could never be found without F8, a level change or a manual
// re-anchor.
//
// This file drives the two real state machines (`AnchorSamplePolicy`, `AnchorElementLookup`) in the order the panel really
// calls them - `Refresh` every `SampleInterval`, `SampleAnchor`'s `Due`, `Attempt`, `BackgroundFade`'s `Due/Record`, the
// measure rule, the settle window and the lock - over a frame timeline, and asks one question: a row that appears at 60 s,
// long after the twelve attempts are spent, must still be measured and locked onto, with the hierarchy walk bounded to a low
// rate.
internal static class Release11926AnchorWatchScenarios
{
    private const float SampleInterval = 0.25f;   // PanelBehaviour.SampleInterval: Refresh, and one attempt at most
    private const int FramesPerSecond = 60;

    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        ALateRowIsFoundAfterTheBudgetIsSpent(check);
        ARevivedAnchorDoesNotInheritTheOldSettleWindow(check);
        ARowThatNeverAppearsCostsABoundedNumberOfWalks(check);
        ARowWithoutAProvenCameraIsNeverMeasured(check);
        TheWatchIsSpacedAndTheGateIsOne(check);
    }

    // The recorded level's starting condition: attempt 1 of 12 finds nothing. The row then appears at 60 s - well past the
    // 12 attempts, which are spent by 35.5 s - and must still be measured and locked onto, with no F8, no level change and no
    // manual re-anchor.
    private static void ALateRowIsFoundAfterTheBudgetIsSpent(Action<bool, string> check)
    {
        var hud = new Hud { Present = t => t >= 60f };
        List<string> log = hud.Run(seconds: 90f);

        // The timeline the panel really produces, printed before the assertions so a red run can be read too.
        foreach (string line in log) Console.WriteLine("      ANCHOR-TIMELINE: " + line);

        check(hud.Policy.Attempts > 0 || hud.Locked,
            "the round did attempt measurements");
        int spentBy = hud.FirstGiveUpAttempts;
        check(spentBy == AnchorSamplePolicy.Budget,
            $"the outer budget really was spent first: {spentBy} attempt(s) of {AnchorSamplePolicy.Budget} before the row existed");

        check(hud.Locked,
            "the row that appeared at 60 s is measured and locked onto: the table hangs under it again, with no F8, no level change and no manual re-anchor");
        check(hud.LockedAt >= 60f && hud.LockedAt <= 60f + 8f,
            $"and it locks within a few samples of the row appearing (locked at {hud.LockedAt:0.00}s, row appeared at 60.00s)");
        check(hud.Source == AnchorElementSource.BackgroundFade,
            "the rectangle is reported as the row's Background Fade, the element that was really measured");
        check(!hud.Unavailable,
            "and the give-up state is lifted when the watch finds it: the panel is not left claiming the anchor is unavailable");
        check(hud.WalkFrames.Count > AnchorElementLookup.MissWalks,
            $"the lookup walked again after its fast budget was spent ({hud.WalkFrames.Count} walk(s) in total, {AnchorElementLookup.MissWalks} of them fast)");
    }

    // Independent review F1, driven on the panel's own sequence (the two real state machines, the measure rule, the settle
    // window and the lock, in the order `Refresh`/`SampleAnchor` call them): the row is there for the first second - four
    // samples, so the abandoned round's still window had already run 0.75 s of its 1 s - then goes away long enough for the
    // round to spend all twelve attempts and give up, then comes back at exactly the pixels it left. The watch's first sample
    // must not lock on that stale stillness: a revived anchor owes the same full second of fresh, consecutive stillness that
    // a first-ever sample does.
    private static void ARevivedAnchorDoesNotInheritTheOldSettleWindow(Action<bool, string> check)
    {
        var hud = new Hud { Present = t => t < 1f || t >= 60f };
        List<string> log = hud.Run(seconds: 90f);
        foreach (string line in log) Console.WriteLine("      REVIVAL-TIMELINE: " + line);

        bool gaveUp = false;
        foreach (string line in log) if (line.Contains("gave up")) gaveUp = true;
        check(gaveUp && !hud.Unavailable,
            "the round really spent its attempts and gave up first, and the watch lifted that give-up when it found the row again");
        check(hud.RevivedAt > 60f,
            $"and it found it on the watch, long after the give-up (revived at {hud.RevivedAt:0.00}s)");
        check(hud.SettleBeforeRevival >= 0.7f && hud.SettleBeforeRevival < 1f,
            $"the abandoned round left just under a second of stillness standing ({hud.SettleBeforeRevival:0.00}s of the 1 s window) - the stale evidence the review is about");
        check(Math.Abs(hud.StaleX - hud.Rect.X) < 0.01f && Math.Abs(hud.StaleY - hud.Rect.Y) < 0.01f,
            $"the row came back at the very pixels the abandoned round had last measured ({hud.StaleX:0},{hud.StaleY:0} against {hud.Rect.X:0},{hud.Rect.Y:0}), so that comparison would have read as \"still\" and added the last 0.25 s");
        check(!hud.LockedOnRevival,
            "the revived round's first sample does not lock: the dead rectangle's stillness is not evidence about this one (independent review F1)");
        check(hud.Locked, "and the round still locks, once the row has proven it is really there");
        check(hud.LockedAt - hud.RevivedAt >= 1f && hud.LockedAt - hud.RevivedAt <= 2f,
            $"one full second of fresh, consecutive stillness after the revival (locked {hud.LockedAt - hud.RevivedAt:0.00}s after it, at {hud.LockedAt:0.00}s) - not the abandoned round's head start");
        check(hud.MeasuredAfterRevival >= (int)Math.Round(1f / SampleInterval) + 1,
            $"{hud.MeasuredAfterRevival} sample(s) were needed: the revival one plus a whole settle window's worth at the 0.25 s cadence");
        check(hud.Source == AnchorElementSource.BackgroundFade,
            "and it locks on the row's Background Fade, the element that was really measured");

    }
    // The same run with a row that never appears: the table stays on the configured position, and the price is a bounded number
    // of walks at a low rate - never one per frame, never an unbounded high-frequency scan.
    private static void ARowThatNeverAppearsCostsABoundedNumberOfWalks(Action<bool, string> check)
    {
        var hud = new Hud { Present = _ => false };
        hud.Run(seconds: 240f);

        check(!hud.Locked && hud.Fallback,
            "a HUD that never carries the row never locks, and the table is drawn from the configured position throughout");
        int allowance = AnchorElementLookup.MissWalks + (int)(240f / hud.WatchIntervalSeconds) + 4;
        check(hud.WalkFrames.Count <= allowance,
            $"240 s of a HUD that never answers costs {hud.WalkFrames.Count} walks, inside the bounded allowance of {allowance}");
        // Every walk after the fast budget is at least `WatchFrames` away from the one before it, and no two walks anywhere are
        // closer than the fast spacing: the hierarchy is never walked per frame, in either phase.
        int closest = int.MaxValue;
        for (int index = 1; index < hud.WalkFrames.Count; index++) closest = Math.Min(closest, hud.WalkFrames[index] - hud.WalkFrames[index - 1]);
        check(hud.WalkFrames.Count < 2 || closest >= AnchorElementLookup.RetryFrames,
            $"no two walks are ever closer than the fast spacing of {AnchorElementLookup.RetryFrames} frames (closest {closest})");
        int slowest = int.MaxValue;
        for (int index = AnchorElementLookup.MissWalks + 1; index < hud.WalkFrames.Count; index++)
            slowest = Math.Min(slowest, hud.WalkFrames[index] - hud.WalkFrames[index - 1]);
        check(hud.WalkFrames.Count <= AnchorElementLookup.MissWalks + 1 || slowest >= AnchorElementLookup.WatchFrames,
            $"and once the fast budget is spent they are at least {AnchorElementLookup.WatchFrames} frames apart - the slow tier (closest {slowest})");
        check(hud.Policy.Watches > 0,
            $"after the budget the round really watches instead of going silent ({hud.Policy.Watches} watch attempt(s))");
    }

    // The element is there from the first frame but nothing can prove a camera draws it: that is never a measurement, and it is
    // never reported as the row's Background Fade either.
    private static void ARowWithoutAProvenCameraIsNeverMeasured(Action<bool, string> check)
    {
        var hud = new Hud { Present = _ => true, Camera = false };
        hud.Run(seconds: 120f);

        check(!hud.Locked && hud.Measured == 0,
            $"an element no camera can be shown to draw is never measured and never locked onto ({hud.Measured} measurement(s))");
        check(hud.Source == AnchorElementSource.None,
            "and no source is claimed for it: a refused projection is not a rectangle from the row");
        check(hud.Fallback,
            "the table stays on the configured PositionX/PositionY, exactly as the give-up line says");
    }

    // The gate is one, and the panel really drives both clocks: no `_anchorUnavailable` shortcut in front of `SampleAnchor`, and
    // the lookup handed the frame counter that spaces it.
    private static void TheWatchIsSpacedAndTheGateIsOne(Action<bool, string> check)
    {
        string panel = Source("PanelBehaviour.cs");
        check(panel.Contains("if (Plugin.AnchorToFlashlight.Value && !_anchorLocked) SampleAnchor(layer, Time.unscaledTime);"),
            "a spent budget no longer closes the only door to `SampleAnchor`: the policy's own cadence decides, in both states");
        check(!panel.Contains("if (Plugin.AnchorToFlashlight.Value && !_anchorLocked && !_anchorUnavailable) SampleAnchor"),
            "the old `!_anchorUnavailable` gate that made the give-up permanent is gone");
        string revived = Code(Between(panel, "if (_anchorUnavailable)", "LayoutRect rect = sample.Rect;"));
        check(revived.Length > 0, "the revival block is found in the production source, ahead of the stability comparison");
        check(revived.Contains("_anchorUnavailable = false;") && revived.Contains("_anchorSampled = 0f;"),
            "the revived round starts its settle window again: the abandoned round's sampling time is not counted against it");
        check(revived.Contains("_anchorSettle = 0f;") && revived.Contains("_anchorLastX = float.NaN;") && revived.Contains("_anchorLastY = float.NaN;"),
            "and the stability comparison starts again with it: the abandoned round's 0.75 s of stillness is not inherited by the first sample after the watch finds the row (independent review F1)");
        check(revived.Contains("_anchorSource.Restart();"),
            "the source tracker is reset too, so that first sample cannot be read as \"the same element as before\"");
        check(!revived.Contains("_anchorRect =") && !revived.Contains("_anchorLocked"),
            "the locked rectangle and the lock itself are deliberately left alone: a locked anchor is never sampled, so this branch can never run with one");

        string policy = Source("AnchorSamplePolicy.cs");
        check(policy.Contains("public const float WatchInterval") && policy.Contains("public bool WatchDue(float now)"),
            "the round owns the low-rate watch, so one clock decides when the HUD is read");
        check(policy.Contains("if (Abandoned) return WatchDue(now);"),
            "and `Due` answers with it once the budget is spent");
        check(Source("AnchorSamplePolicy.cs").Contains("public const int WatchFrames"),
            "the lookup owns the slow spacing its own walks are granted at");
    }

    // ---- the panel's own sequence, frame by frame ------------------------------------------------------------------

    // Mirrors `PanelBehaviour.Refresh`/`SampleAnchor`/`ReadAnchor`/`BackgroundFade` and the lock, over the two real state
    // machines. Every line of glue mirrored here is also pinned by a source assertion above, so the driver cannot drift away
    // from the panel silently.
    private sealed class Hud
    {
        public readonly AnchorSamplePolicy Policy = new AnchorSamplePolicy();
        public readonly AnchorElementLookup Lookup = new AnchorElementLookup();
        public readonly object Owner = new object();
        public readonly List<int> WalkFrames = new List<int>();
        public readonly LayoutRect Rect = new LayoutRect(1000f, 200f, 32f, 32f);

        // When this HUD carries the row's `Background Fade` at all: a window rather than a single time, because the independent
        // review's F1 is exactly the case of an element that is there, then gone long enough for the round to give up, then back
        // at the same place.
        public Func<float, bool> Present = _ => false;
        public bool Camera = true;
        public bool Fallback, Locked, Unavailable;
        public float Settle, Sampled;
        public float LastX = float.NaN, LastY = float.NaN;
        public float LockedAt = -1f;
        public int Measured;
        public int FirstGiveUpAttempts;
        // When the revived round took its first sample, and how many samples it needed before the lock: the two figures the F1
        // regression is about (a revived anchor may not lock on that first sample).
        public float RevivedAt = -1f;
        public int MeasuredAfterRevival;
        // The abandoned round's own evidence, captured as the revival sample arrives: the still window that had already run out
        // its 0.75 s, and the pixels it was compared against. The review's premise is that these are exactly the ones the row
        // comes back at - so this run asserts both.
        public float SettleBeforeRevival = -1f;
        public float StaleX = float.NaN, StaleY = float.NaN;
        public bool LockedOnRevival;
        public AnchorElementSource Source = AnchorElementSource.None;
        public AnchorElementSource LastSource = AnchorElementSource.None;

        public double WatchIntervalSeconds => AnchorSamplePolicy.WatchInterval;

        public List<string> Run(float seconds)
        {
            var log = new List<string>();
            for (int frame = 0; frame <= (int)(seconds * FramesPerSecond); frame++)
            {
                // `Refresh` is polled every `SampleInterval`: the in-level table's own 0.25 s cadence.
                if (frame % (int)(SampleInterval * FramesPerSecond) != 0) continue;
                if (Locked) continue;
                float now = frame / (float)FramesPerSecond;
                if (!Policy.Due(now)) continue;

                Sampled += SampleInterval;                       // _anchorSampled += SampleInterval
                AnchorSample sample = Policy.Attempt(now, () => Read(frame, now), out bool gaveUp);
                if (gaveUp && FirstGiveUpAttempts == 0) FirstGiveUpAttempts = Policy.Attempts;
                if (!sample.Measured)
                {
                    Fallback = true;                             // _anchorFallback = true
                    if (gaveUp)
                    {
                        Unavailable = true;                      // _anchorUnavailable = true
                        log.Add($"{now:0.00}s  gave up after {Policy.Attempts} attempt(s); watchers={Policy.Watches}");
                    }
                    continue;
                }
                Measured++;
                Source = sample.Source;
                if (Unavailable)
                {
                    Unavailable = false;
                    Sampled = 0f;                            // _anchorSampled = 0f
                    // What the abandoned round left standing, captured before the reset below so the F1 run can assert it was
                    // really there: a 0.75 s still window and the pixel pair it was measured against.
                    SettleBeforeRevival = Settle; StaleX = LastX; StaleY = LastY;
                    // The stability comparison is reset with it: this is the whole of review F1. `Settle`/`LastX`/`LastY` are the
                    // abandoned round's own evidence, and `LastSource = None` mirrors `_anchorSource.Restart()`.
                    Settle = 0f; LastX = float.NaN; LastY = float.NaN; LastSource = AnchorElementSource.None;
                    if (RevivedAt < 0f) RevivedAt = now;
                    log.Add($"{now:0.00}s  the watch found it: measuring {Source} again (attempt {Policy.Attempts} of {AnchorSamplePolicy.Budget}), walks={WalkFrames.Count}");
                }
                if (LastSource != sample.Source)
                {
                    Settle = 0f; LastX = float.NaN; LastY = float.NaN;   // _anchorSource.Observe(sample.Source)
                    LastSource = sample.Source;
                }
                bool still = !float.IsNaN(LastX) && Math.Abs(sample.Rect.X - LastX) < 2f && Math.Abs(sample.Rect.Y - LastY) < 2f;
                Settle = still ? Settle + SampleInterval : 0f;
                LastX = sample.Rect.X; LastY = sample.Rect.Y;
                if (RevivedAt >= 0f) MeasuredAfterRevival++;
                log.Add($"{now:0.00}s  measured {Source} at ({sample.Rect.X:0},{sample.Rect.Y:0}) still={still} settle={Settle:0.00}s sampled={Sampled:0.00}s");
                if (Settle >= 1f || Sampled >= 6f)
                {
                    Locked = true; Fallback = false; LockedAt = now;
                    log.Add($"{now:0.00}s  LOCKED after {Policy.Attempts} attempt(s) of this round, {WalkFrames.Count} hierarchy walk(s) in the whole level");
                }
                // Whether the revived round's *first* sample already locked: the one thing review F1 says it may not do.
                if (MeasuredAfterRevival == 1) LockedOnRevival = Locked;
            }
            return log;
        }

        // `ReadAnchor` -> `BackgroundFadeRenderer` -> `BackgroundFade`, then `MeasureElement`: the row can only be measured when
        // it exists *and* a camera that really draws its layer was validated.
        private AnchorSample Read(int frame, float now)
        {
            bool present = Present != null && Present(now);
            // `BackgroundFade`: `if (_backgroundFade == null && _backgroundFadeLookup.Hit) _backgroundFadeLookup.Forget();` - an
            // element Unity has since destroyed drops the lookup, which is what lets the row be looked for again. Without this
            // the row, once found, would stay "found" for ever and the review's F1 could never be reached.
            if (!present && Lookup.Hit) Lookup.Forget();
            if (!Lookup.Due(Owner, frame)) return Decision();
            WalkFrames.Add(frame);                          // the one GetComponentsInChildren of the display
            Lookup.Record(Owner, present, walked: true);
            return Decision();
        }

        private AnchorSample Decision()
        {
            if (!Lookup.Hit) return AnchorSample.Missing(AnchorSampleFailure.NoProjection);
            if (!Camera) return AnchorSample.Missing(AnchorSampleFailure.NoProjection);
            return AnchorSample.Of(Rect, AnchorElementSource.BackgroundFade);
        }
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private static string Between(string text, string from, string to)
    {
        int start = text.IndexOf(from, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        int end = text.IndexOf(to, start, StringComparison.Ordinal);
        return end < 0 ? text.Substring(start) : text.Substring(start, end - start);
    }

    // The text with its comment lines removed: a comment may name a call in order to explain why it is wrong, so a "this code
    // must do X" check has to read the code that really runs, not the prose beside it.
    private static string Code(string text)
    {
        var kept = new List<string>();
        if (text == null) return string.Empty;
        foreach (string line in text.Split('\n'))
            if (!line.TrimStart().StartsWith("//", StringComparison.Ordinal)) kept.Add(line);
        return string.Join("\n", kept);
    }

    private static string Source(string name)
    {
        string root = SourceRoot();
        if (root == null) throw new InvalidOperationException("The plugin source directory could not be found from " + AppContext.BaseDirectory);
        string path = Path.Combine(root, name);
        if (!File.Exists(path)) throw new FileNotFoundException("Production source is missing", path);
        return File.ReadAllText(path);
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj"))) directory = directory.Parent;
        return directory != null ? directory.FullName : null;
    }
}
