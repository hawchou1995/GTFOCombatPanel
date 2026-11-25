using System;

namespace HawChow.GTFOCombatPanel;

// 1.19.41: what one sample of the native scanner cooldown proved.
//
//   Unavailable - the holder's `m_recharging`/`m_rechargeStartTime` could not be read, so this sample says
//                 nothing at all. It is never a zero and never an activation.
//   Idle        - the scanner is not recharging and no session was open: nothing happened.
//   Continuing  - the same recharge session as before (`m_rechargeStartTime` did not move).
//   Started     - a session this holder had not shown before: exactly one activation.
//   Completed   - the session that was open ended (`m_recharging` went true->false).
public enum ScannerCooldownSignal
{
    Unavailable = 0,
    Idle = 1,
    Continuing = 2,
    Started = 3,
    Completed = 4,
}

// The scan-activation ledger of one scanner, on the machine that holds it.
//
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
// No duration is baked in here, on purpose: `EquipmentTimers` had to apply `AgentModifier.ScannerRechargeSpeed`
// to `m_rechargeDuration` before a remaining time meant anything, and the start-time identity is what proves
// a new scan either way. This type therefore never subtracts 8, 8.5 or any other number of seconds.
//
// Plain numbers only: the whole rule is exercised without Unity.
public sealed class ScannerCooldownLedger
{
    private float _lastStart = float.NaN;
    private bool _recharging;
    // The last readable `m_recharging` value: true while the scanner is cooling down from the scan it just did.
    public bool Recharging => _recharging;
    private bool _sessionOpen;
    private bool _observable;

    // A readable cooldown sample has arrived at some point. Without one the activation count is *unknown*,
    // not zero: a row whose holder this machine cannot observe must print "scans unknown".
    public bool Observable => _observable;

    // Independent cooldown starts: the only figure that may be called a number of scans.
    public long Activations { get; private set; }
    // Sessions that ended (true->false of `m_recharging`, or superseded by a later start).
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
        _observable = false;
        Activations = Completions = Repeats = Unusable = 0;
    }

    // One sample of the holder's own scanner. `rechargingKnown` is whether the field could be read at all;
    // `startKnown` is the same for `m_rechargeStartTime`.
    public ScannerCooldownSignal Observe(bool rechargingKnown, bool recharging, bool startKnown, float startTime)
    {
        if (!rechargingKnown)
        {
            Unusable++;
            return ScannerCooldownSignal.Unavailable;
        }
        _observable = true;
        if (!recharging)
        {
            _recharging = false;
            if (!_sessionOpen) return ScannerCooldownSignal.Idle;
            _sessionOpen = false;
            Completions++;
            return ScannerCooldownSignal.Completed;
        }
        // A true flag without a readable start time names no session: it cannot be told from the one already
        // counted, so it is not counted again. The scanner is observably busy (a session is open) but the
        // activation figure stays what identity proved.
        _observable = true;
        if (!startKnown || !float.IsFinite(startTime))
        {
            _recharging = true;
            _sessionOpen = true;
            Unusable++;
            return ScannerCooldownSignal.Unavailable;
        }
        _recharging = true;
        if (!float.IsNaN(_lastStart) && !(startTime > _lastStart))
        {
            // Same session - or a stale sample from a reader that fell behind. Neither is a new scan.
            _sessionOpen = true;
            Repeats++;
            return ScannerCooldownSignal.Continuing;
        }
        if (_sessionOpen && !float.IsNaN(_lastStart)) Completions++;
        _lastStart = startTime;
        _sessionOpen = true;
        Activations++;
        return ScannerCooldownSignal.Started;
    }

    // A fresh expedition: the next session is new again, and nothing carries over.
    public void ResetExpedition() => Clear();
}
