using System;
using System.Collections.Generic;

namespace HawChow.GTFOCombatPanel;

// What one sweep did about one enemy: Rejected when the radar was only updating, First when this
// sweep revealed the enemy for the squad, Repeat when somebody had already revealed it.
public enum ScanMark { Rejected, First, Repeat }

// One reveal namespace per expedition, shared by every scanner in the squad. The first sweep that
// puts an enemy on the bio tracker takes the credit; an enemy that is already revealed scores
// nothing for anybody afterwards, whoever sweeps it again and however many times they do.
public sealed class ScanReveals
{
    private readonly HashSet<int> _revealed = new HashSet<int>();

    public int Count { get { return _revealed.Count; } }

    // True only for the sweep that reveals this enemy first. Zero is never an enemy identity.
    public bool Reveal(int identity) { return identity != 0 && _revealed.Add(identity); }

    public void Clear() { _revealed.Clear(); }
}

// One scanner: its own tag-attempt edges, its own bot callback throttle, and the expedition's
// shared reveal ledger. Passive radar updates do not count as attempts. Bot static callbacks have
// no owner: attribution and timing remain estimates.
//
// The two mark figures answer two different questions and no longer share a gate:
//   - first marks  (`ScanFirstMarks`) are expedition-unique reveals: the sweep that first put an enemy
//     on the squad's bio tracker takes the credit, and nobody is credited for it again.
//   - cumulative marks (`ScanMarks`) are per-enemy marks inside the sweep window now under way, counted
//     again by every later sweep that marks the same enemy. One enemy contributes at most once to one
//     window, and a window is opened by the tag flag's rising edge, by a bot callback burst, by the
//     caller's own `bracket` (the native tag progress call) and by every native execution confirmation,
//     so a build - or a player - whose tag flag never reads true still counts cumulative marks instead of
//     mirroring the first marks.
public sealed class ScanSession
{
    private readonly ScanReveals _reveals;
    private readonly HashSet<int> _sweepMarks = new HashSet<int>();
    private bool _tagging;
    // Whether the sweep now under way has already contributed its one denominator, so a reveal that was
    // never credited by the tag flag can still credit the sweep it evidences exactly once.
    private bool _countedThisSweep;
    private float _lastBotTag = float.NegativeInfinity;

    // A session on its own is its own ledger, which is what a single scanner's tests want. The
    // tracker hands every squad member's session the same expedition ledger instead.
    public ScanSession() : this(new ScanReveals()) { }

    public ScanSession(ScanReveals reveals) { _reveals = reveals ?? new ScanReveals(); }

    // The rising edge starts one sweep. A build whose flag is never readable still gets its first
    // sweep counted below, from the reveals themselves. `bracket` is the caller's own attempt boundary -
    // the native tag progress call - and is a fresh sweep window on its own, even while the flag reads
    // false, because that is how a human scanner's confirmed tags really arrive.
    public bool ObserveTagging(bool active, ImpactCounters counters, bool bracket = false)
    {
        bool started = active && !_tagging;
        if (started || bracket) _sweepMarks.Clear();
        _tagging = active;
        if (!active) { _countedThisSweep = false; return false; }
        if (started && counters != null) { counters.CreditScan(0); _countedThisSweep = true; }
        return started;
    }

    // Each enemy contributes once to the sweep window now under way, while the first-mark score still
    // counts expedition-unique reveals. The cumulative mark therefore no longer depends on the tag flag
    // being readable: the window is what bounds it, never the reveal.
    public ScanMark Observe(int identity, ImpactCounters counters, bool bot = false)
    {
        if (counters == null || identity == 0) return ScanMark.Rejected;
        bool first = _reveals.Reveal(identity);
        if (_sweepMarks.Add(identity)) counters.CreditScanMark();
        if (!first) return ScanMark.Repeat;
        if (!_countedThisSweep) { counters.CreditScan(0); _countedThisSweep = true; }
        counters.ScanFirstMarks++;
        return ScanMark.First;
    }

    public ScanMark ObserveConfirmed(int identity, ImpactCounters counters, bool confirmed, bool bot = false)
        => confirmed ? Observe(identity, counters, bot) : ScanMark.Rejected;

    // One native execution confirmation, one sweep window. A guest's scanner is never readable here - only
    // the local human's `BeginHumanScan` runs, and the host has no scanner instance for a guest's row at all
    // - so on the host that row kept a single window for the whole expedition and its cumulative figure could
    // never leave the first-mark figure. Opening the window at every confirmation is what makes the
    // confirmation itself the boundary, whoever the player is. Only the sweep window is cleared: the
    // denominator flags are left exactly as they were, so a confirmation still cannot invent an attempt.
    public void BeginConfirmWindow() => _sweepMarks.Clear();

    public void EndBotTag() => _tagging = false;

    public bool MarkEnemy(int identity, ImpactCounters counters, bool bot = false)
        => Observe(identity, counters, bot) == ScanMark.First;


    public bool BeginBotTag(float time, float duration, ImpactCounters counters)
    {
        if (counters == null || !float.IsFinite(time)) return false;
        // A callback burst within the game's tag duration is one estimated attempt.
        float interval = float.IsFinite(duration) && duration > 0 ? Math.Max(0.25f, duration) : 1f;
        if (time - _lastBotTag < interval) { _tagging = true; return false; }
        _lastBotTag = time;
        _sweepMarks.Clear();
        _tagging = true;
        counters.CreditScan(0, true);
        _countedThisSweep = true;
        return true;
    }
}
