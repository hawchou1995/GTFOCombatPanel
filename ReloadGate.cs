using System;

namespace HawChow.GTFOCombatPanel;

// The debounce in front of the configuration reload. The file watchers only arm it; the main thread
// asks whether the file has stopped changing and, if so, reloads once. Both the .cfg and the scoring
// file arm the same gate, so two edits landing together still produce a single reload. Kept free of
// BepInEx and of the file system, so the rule that matters - exactly one reload per settled file - is
// asserted headlessly instead of being taken on trust.
public sealed class ReloadGate
{
    public const int DebounceMilliseconds = 300;

    private long _dueTicks;
    private volatile bool _armed;

    public bool Armed { get { return _armed; } }

    // Called from the watcher's own thread. The deadline is written before the flag, and the flag is
    // volatile, so the main thread that sees the flag also sees the deadline.
    public void Arm(long nowTicks)
    {
        _dueTicks = nowTicks + TimeSpan.TicksPerMillisecond * DebounceMilliseconds;
        _armed = true;
    }

    // True exactly once per settled file: the first call after the debounce has elapsed.
    public bool Consume(long nowTicks)
    {
        if (!_armed) return false;
        if (nowTicks < _dueTicks) return false;
        _armed = false;
        return true;
    }
}
