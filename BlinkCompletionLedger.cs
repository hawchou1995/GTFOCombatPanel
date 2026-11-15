using System.Collections.Generic;
using System.Linq;

namespace HawChow.GTFOCombatPanel;

// One success-page visit, identified by its scheduled native BlinkIn targets and latest routines.
public sealed class BlinkCompletionLedger
{
    private sealed class Entry { public bool Name, Done, Scheduled, Ended, Excluded; public long Routine; }
    private readonly Dictionary<long, Entry> _targets = new Dictionary<long, Entry>();
    private bool _missing;

    // Only the targets the page's own native reveal really took over are counted: a target whose BlinkIn
    // schedule window has closed without the game ever handing it a routine (see `CloseSchedule`) is not
    // part of this page's reveal, and counting it would keep `Ready` false for good.
    private static bool Counted(Entry entry) => !entry.Excluded;
    private bool All(bool name) => _targets.Values.Where(e => e.Name == name && Counted(e)).All(e => e.Done);

    public int Names => _targets.Values.Count(e => e.Name && Counted(e));
    public int Artwork => _targets.Values.Count(e => !e.Name && Counted(e));
    public int Completed => _targets.Values.Count(e => e.Done && Counted(e));
    public int Count => _targets.Values.Count(Counted);

    // Every target this page handed over, counted or not: the figure the diagnostics quote next to `Count`
    // so a skipped slot is visible rather than silently dropped.
    public int Required => _targets.Count;
    // Targets whose native schedule window closed without a routine: this build (or this page's own icon
    // instance) hands over no such reveal, so waiting for one would be a wait for a callback that never comes.
    public int Unscheduled => _targets.Values.Count(e => e.Excluded);
    // Targets whose native routine really ended without revealing them, and that no later routine took over.
    // A routine that ended is not a routine that is still coming: this page's reveal can no longer complete.
    public int Aborted => _targets.Values.Count(e => e.Ended && !e.Done);
    // A page with no counted name target is vacuously name-ready, exactly as it was before: only the artwork
    // half needs at least one target, because that is the half the reveal proves.
    public bool NamesReady => !RevealImpossible && All(true);
    public bool ArtworkReady => !RevealImpossible && Artwork > 0 && All(false);
    public bool Ready => NamesReady && ArtworkReady;

    // Whether waiting for this ledger can still produce a "completed" answer at all, and its two readings.
    //
    // It is a statement about the ledger the page built, never about elapsed time. A target the page handed over with
    // no object at all (`Require(0, ...)`) or a page that scheduled more targets than the ledger can hold leaves the
    // required set incomplete for good, and so does a native routine that ended without revealing its target
    // (`Aborted`): no frame, no alpha and no empty scan turns any of those into a finished reveal, so a measurement
    // that waited for `Ready` on that page would never answer. `Ready` keeps its meaning - `Aborted` makes it false,
    // never true.
    //
    // `RevealImpossible` is therefore **not** "the page has stopped animating": it only says this ledger will never
    // read `Ready`. The page's own routines may still be running - on the recorded success page five of six sector
    // icons were still blinking - so the placement gate reads `InFlight`/`Settled` beside it and waits for those
    // (F1). Nothing here changes what `Require`, `Start`, `Finish` or `Ready` do.
    public bool RevealImpossible => _missing || Aborted > 0;
    public bool MayStillComplete => !RevealImpossible;
    // Targets whose native routine is still running: scheduled, and neither finished nor ended. A target that ended
    // without revealing is *not* in flight - it is over - and a target that never received a routine is not in flight
    // either (`Excluded`). This is the figure that tells "the reveal can no longer complete" apart from "the reveal has
    // stopped", which is what the success page's first placement actually needs.
    public int InFlight => _targets.Values.Count(e => Counted(e) && e.Scheduled && !e.Done && !e.Ended);
    public bool Settled => InFlight == 0;
    // One scheduled blink target enters the ledger; a name target is a text label, everything else is artwork.
    public void Require(long target, bool name)
    {
        if (target == 0 || _targets.Count >= 64 && !_targets.ContainsKey(target)) { _missing = true; return; }
        if (!_targets.TryGetValue(target, out Entry entry)) _targets[target] = entry = new Entry();
        entry.Name |= name;
    }

    // The schedule window of one native `CM_ExpeditionSectorIcon.BlinkIn` closed: every target that call handed over
    // and that never received a `SetActiveBlink` routine is not part of this page's reveal.
    //
    // `BlinkIn` schedules its coroutines inside the call itself, so the window is closed when it returns. A target
    // that never got a routine inside it will never be revealed by it - and, if a later call does schedule it,
    // `Start` re-admits it, because a late native routine is evidence that the target is part of the reveal after all.
    // The count is returned so the caller can say once, per page, which target was skipped.
    public int CloseSchedule(IEnumerable<long> required)
    {
        if (required == null) return 0;
        int skipped = 0;
        foreach (long target in required)
        {
            if (!_targets.TryGetValue(target, out Entry entry) || entry.Scheduled || entry.Excluded) continue;
            entry.Excluded = true;
            skipped++;
        }
        return skipped;
    }

    // Whether this page really scheduled a Blink reveal for the target. The answer drives the completion band's
    // rule for a part that is switched off right now: a slot the page scheduled (its reveal has not switched it
    // on yet) must contribute its laid-out rectangle, while a slot the page never scheduled - an unused sector
    // icon of a short run - contributes nothing. A ledger that stopped recording because it ran into its target
    // budget cannot prove an absence, so it answers the conservative direction: treat the target as scheduled
    // rather than let a band hang from a slot the page is about to draw under.
    public bool IsScheduled(long target)
    {
        if (target == 0) return false;
        if (_missing) return true;
        return _targets.ContainsKey(target);
    }

    public void Start(long target, long routine)
    {
        if (routine == 0 || !_targets.TryGetValue(target, out Entry entry)) return;
        entry.Routine = routine;
        entry.Done = false;
        entry.Ended = false;
        entry.Scheduled = true;
        // A routine that really runs re-admits a target whose window was closed too early: the reveal is observed.
        entry.Excluded = false;
    }

    public void Finish(long target, long routine, bool naturallyRevealed)
    {
        if (routine == 0 || !_targets.TryGetValue(target, out Entry entry) || entry.Routine != routine) return;
        entry.Done = naturallyRevealed;
        // A routine that ended without revealing its target is over: nothing else will arrive for it unless a later
        // `Start` hands it a new one, so the page's reveal can no longer complete. It is never recorded as done.
        entry.Ended = !naturallyRevealed;
    }

    // The same answer for a routine whose target GameObject is already gone: the coroutine still names the routine,
    // and a destroyed target must not leave the ledger waiting for a routine that has already ended.
    public bool FinishRoutine(long routine, bool naturallyRevealed)
    {
        if (routine == 0) return false;
        foreach (Entry entry in _targets.Values)
        {
            if (entry.Routine != routine) continue;
            entry.Done = naturallyRevealed;
            entry.Ended = !naturallyRevealed;
            return true;
        }
        return false;
    }

    public void Clear()
    {
        _targets.Clear();
        _missing = false;
    }
}
