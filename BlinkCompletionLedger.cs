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

    public void Require(long target, bool name)
    {
        if (target == 0 || _targets.Count >= 64 && !_targets.ContainsKey(target)) { _missing = true; return; }
        if (!_targets.TryGetValue(target, out Entry entry)) _targets[target] = entry = new Entry();
        entry.Name |= name;
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
