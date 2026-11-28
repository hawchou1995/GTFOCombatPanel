using System;

namespace HawChow.GTFOCombatPanel;

// 1.19.41: what one sample of the native scanner cooldown proved.
//
//   Unavailable - the holder's `m_recharging`/`m_rechargeStartTime` could not be read, so this sample says
//                 nothing at all. It is never a zero, never an activation and never an idle edge.
//   Idle        - the scanner is not recharging and no session was open: nothing happened. A *read* idle is
//                 the one state that proves no cooldown is in flight, so it is also the strongest baseline.
//   Baseline    - the first cooldown this ledger could read while the scanner was already busy (1.19.70). It
//                 is the starting point later starts are compared against and it counts nothing: a cooldown
//                 that was already running when this machine began looking - the level-entry residue - is not
//                 a scan this holder can be credited with.
//   Continuing  - a busy sample with the same or an older start; no new activation.
//   Started     - a session this holder had not shown before, observed from a baseline this ledger had
//                 established: exactly one activation.
//   Completed   - an observed busy session ended; only a counted session increments Completions.
public enum ScannerCooldownSignal
{
    Unavailable = 0,
    Idle = 1,
    Continuing = 2,
    Started = 3,
    Completed = 4,
    Baseline = 5,
}

// The scan-activation ledger of one scanner, on the machine that holds it.
// Why this exists: the old figure counted the rising edge of `Gear.EnemyScanner.m_tagging`, and that flag
// flips many times inside a single sweep (the recorded expedition printed 722 "scans" against a ceiling of
// <=490 that the map itself allows). `m_tagging` is the in-sweep progress flag, so any count derived from it -
// and any count derived from marks, tag requests or the local echo of one's own tags - is a count of windows,
// not of scans.
//
// The native identity of one scan is one cooldown *session*: `m_recharging` true with a
// `m_rechargeStartTime` later than the last one this holder showed. A repeat sample of the same start time
// (the frame poll, the tag-progress bracket, a second reader of the same instance) is the same scan and adds
// nothing. The session's end is the true->false edge of `m_recharging`.
//
// 1.19.70 - what "later than the last one this holder showed" left out. A ledger that starts from
// `_lastStart = NaN` has no start to compare with, so the *first* cooldown it ever saw while busy was counted
// as a scan. A cooldown that was already running when the expedition began looking - a level-entry residue -
// is exactly that first sample. Counting it as a scan would credit an activation even when the player did
// not scan. The activation figure therefore needs a baseline before it may count anything:
//
//   - an active cooldown this ledger has not seen before is a *baseline*, never a scan (1.19.70);
//   - a read idle sample is a baseline too, and it is what makes the first real scan countable - the normal
//     `idle -> new native cooldown` edge is untouched (`Baseline`, then `Started`);
//   - when a start is already known, only a strictly later one counts; when none is known, an
//     observed idle-to-busy transition with a readable start counts;
//   - the same start and an older start never count, and a sample that could not be read is never
//     turned into an idle edge and never into a zero;
//   - a busy sample whose start is unreadable names no session, so when the same busy streak later reads a
//     start, that start is still the baseline it would have been - not a new scan.
//
// What this deliberately does *not* bound: how many scans are counted. Every real activation is counted, for
// the whole expedition, however many times the holder sweeps enemies the squad already has on the tracker -
// nothing here looks at marks, so a first-mark figure can never cap the scan figure.
//
// No duration is baked in here, on purpose: `EquipmentTimers` had to apply `AgentModifier.ScannerRechargeSpeed`
// to `m_rechargeDuration` before a remaining time meant anything, and the start-time identity is what proves
// a new scan either way. This type therefore never subtracts 8, 8.5 or any other number of seconds, and it
// never turns an observed busy/idle edge into a duration.
//
// Plain numbers only: the whole rule is exercised without Unity.
public sealed class ScannerCooldownLedger
{
    private float _lastStart = float.NaN;
    private bool _recharging;
    // The last readable `m_recharging` value: true while the scanner is cooling down from the scan it just did.
    public bool Recharging => _recharging;
    private bool _sessionOpen;
    // The open session was counted as an activation, so its end is a completion. A baselined session closing
    // is not: completions can therefore never outnumber activations, and an uncounted residue never shows up
    // as a scan that finished.
    private bool _sessionCounted;
    private bool _observable;
    // A read idle sample has arrived after the last busy sample this ledger processed: nothing was in flight,
    // so a start observed next began after that observation. Cleared by every readable busy sample.
    private bool _idleEdge;

    // A readable cooldown sample has arrived at some point. Without one the activation count is *unknown*,
    // not zero: a row whose holder this machine cannot observe must print "scans unknown".
    public bool Observable => _observable;

    // Independent cooldown starts: the only figure that may be called a number of scans.
    public long Activations { get; private set; }
    // Sessions that ended (true->false of `m_recharging`, or superseded by a later start) and were counted.
    public long Completions { get; private set; }
    // Samples that repeated the session already counted - a second reader, the frame poll, a re-entrant call.
    public long Repeats { get; private set; }
    // Samples whose cooldown state could not be read. They count nothing and never reset a session.
    public long Unusable { get; private set; }

    public bool SessionOpen => _sessionOpen;
    public float LastStart => _lastStart;

    public void Clear()
    {
        _lastStart = float.NaN;
        _recharging = false;
        _sessionOpen = false;
        _sessionCounted = false;
        _observable = false;
        _idleEdge = false;
        Activations = Completions = Repeats = Unusable = 0;
    }

    // One sample of the holder's own scanner. `rechargingKnown` is whether the field could be read at all;
    // `startKnown` is the same for `m_rechargeStartTime`.
    public ScannerCooldownSignal Observe(bool rechargingKnown, bool recharging, bool startKnown, float startTime)
    {
        if (!rechargingKnown)
        {
            // No read, no claim: this sample is not an idle edge, does not end a session and is not a zero.
            Unusable++;
            return ScannerCooldownSignal.Unavailable;
        }
        _observable = true;
        if (!recharging)
        {
            _recharging = false;
            // Read idle: the scanner provably has no cooldown in flight, which is a baseline for whatever
            // starts next. This is the only place an idle edge may be produced.
            _idleEdge = true;
            if (!_sessionOpen) return ScannerCooldownSignal.Idle;
            _sessionOpen = false;
            bool counted = _sessionCounted;
            _sessionCounted = false;
            if (counted) Completions++;
            return ScannerCooldownSignal.Completed;
        }
        // A true flag without a readable start time names no session: it cannot be told from the one already
        // counted, so it is not counted again. The scanner is observably busy (a session is open) but the
        // activation figure stays what identity proved. It leaves the idle edge as it found it - a sample that
        // could not be read proves nothing either way.
        _recharging = true;
        if (!startKnown || !float.IsFinite(startTime))
        {
            _sessionOpen = true;
            Unusable++;
            return ScannerCooldownSignal.Unavailable;
        }
        bool idleEdge = _idleEdge;
        _idleEdge = false;
        bool haveStart = !float.IsNaN(_lastStart);
        if (haveStart && startTime > _lastStart) return Count(startTime);
        if (haveStart && startTime == _lastStart)
        {
            // The session this ledger already adopted or counted, sampled again. Never a new scan.
            _sessionOpen = true;
            Repeats++;
            return ScannerCooldownSignal.Continuing;
        }
        if (haveStart)
        {
            // An idle edge does not make an older timestamp new. Keep the baseline unchanged.
            _sessionOpen = true;
            Repeats++;
            return ScannerCooldownSignal.Continuing;
        }
        if (idleEdge)
        {
            // Idle observed first, then this start: nothing was in flight before it, so it is this holder's own
            // first scan and it may be counted whatever its time base says.
            return Count(startTime);
        }
        // The first cooldown this ledger ever saw, already in progress. That is the level-entry residue: it is
        // the baseline later starts are measured against, and it is not a scan.
        _lastStart = startTime;
        _sessionOpen = true;
        _sessionCounted = false;
        return ScannerCooldownSignal.Baseline;
    }

    private ScannerCooldownSignal Count(float startTime)
    {
        if (_sessionOpen && _sessionCounted) Completions++;
        _lastStart = startTime;
        _sessionOpen = true;
        _sessionCounted = true;
        Activations++;
        return ScannerCooldownSignal.Started;
    }

    // A fresh expedition: the next session is new again, and nothing carries over.
    public void ResetExpedition() => Clear();
}
