using System.Collections.Generic;
using System.Linq;

namespace HawChow.GTFOCombatPanel;

// One success-page visit, identified by its scheduled native BlinkIn targets and latest routines.
public sealed class BlinkCompletionLedger
{
    private sealed class Entry { public bool Name, Done; public long Routine; }
    private readonly Dictionary<long, Entry> _targets = new Dictionary<long, Entry>();
    private bool _missing;
    public int Names => _targets.Values.Count(e => e.Name);
    public int Artwork => _targets.Values.Count(e => !e.Name);
    public int Completed => _targets.Values.Count(e => e.Done);
    public int Count => _targets.Count;
    public bool NamesReady => !_missing && _targets.Values.Where(e => e.Name).All(e => e.Done);
    public bool ArtworkReady => !_missing && Artwork > 0 && _targets.Values.Where(e => !e.Name).All(e => e.Done);
    public bool Ready => NamesReady && ArtworkReady;

    // Whether waiting for this ledger can still produce a "completed" answer at all, and its two readings.
    //
    // It is a statement about the ledger the page built, never about elapsed time. A target the page handed over with
    // no object at all (`Require(0, ...)`) or a page that scheduled more targets than the ledger can hold leaves the
    // required set incomplete for good: no frame, no alpha and no empty scan turns it into a finished reveal, so a
    // measurement that waited for `Ready` on that page would never answer. `Ready` keeps its meaning; this only says
    // whether waiting is still sensible, which is what the placement gate reads. Nothing here changes what `Require`,
    // `Start`, `Finish` or `Ready` do.
    public bool RevealImpossible => _missing;
    public bool MayStillComplete => !_missing;
    // One scheduled blink target enters the ledger; a name target is a text label, everything else is artwork.
    public void Require(long target, bool name)
    {
        if (target == 0 || _targets.Count >= 64 && !_targets.ContainsKey(target)) { _missing = true; return; }
        if (!_targets.TryGetValue(target, out Entry entry)) _targets[target] = entry = new Entry();
        entry.Name |= name;
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
    }

    public void Finish(long target, long routine, bool naturallyRevealed)
    {
        if (routine == 0 || !_targets.TryGetValue(target, out Entry entry) || entry.Routine != routine) return;
        entry.Done = naturallyRevealed;
    }

    public void Clear() { _targets.Clear(); _missing = false; }
}
