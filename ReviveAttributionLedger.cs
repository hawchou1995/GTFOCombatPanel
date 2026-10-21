using System;
using System.Collections.Generic;

namespace HawChow.GTFOCombatPanel;

// Correlates the game's replicated reviver/target pair with the downed player's
// successful recovery callback. Interaction state alone never increments a rescue.
internal sealed class ReviveAttributionLedger
{
    internal const double ActiveTimeoutSeconds = 20.0;
    internal const double ReleaseGraceSeconds = 3.0;

    private struct Candidate
    {
        public ulong Reviver;
        public double ExpiresAt;
    }

    private readonly Dictionary<ulong, Candidate> _byTarget = new Dictionary<ulong, Candidate>();

    internal int Count => _byTarget.Count;

    internal static bool CanTrackOwner(bool isInSlot, bool isMaster, bool isLocal)
        => isInSlot && (isMaster || isLocal);

    internal bool Observe(ulong target, ulong reviver, bool active, double now)
    {
        Prune(now);
        if (target == 0 || reviver == 0 || target == reviver || !double.IsFinite(now)) return false;

        if (active)
        {
            _byTarget[target] = new Candidate { Reviver = reviver, ExpiresAt = now + ActiveTimeoutSeconds };
            return true;
        }

        if (!_byTarget.TryGetValue(target, out Candidate existing) || existing.Reviver != reviver) return false;
        existing.ExpiresAt = now + ReleaseGraceSeconds;
        _byTarget[target] = existing;
        return true;
    }

    internal bool TryTakeSuccessfulRevive(ulong target, double now, out ulong reviver)
    {
        reviver = 0;
        if (target == 0 || !double.IsFinite(now)) return false;
        if (!_byTarget.TryGetValue(target, out Candidate existing)) return false;
        _byTarget.Remove(target);
        if (now > existing.ExpiresAt) return false;
        reviver = existing.Reviver;
        return reviver != 0 && reviver != target;
    }

    internal void Clear() => _byTarget.Clear();

    private void Prune(double now)
    {
        if (!double.IsFinite(now)) return;
        var expired = new List<ulong>();
        foreach (KeyValuePair<ulong, Candidate> entry in _byTarget)
            if (now > entry.Value.ExpiresAt) expired.Add(entry.Key);
        for (int i = 0; i < expired.Count; i++) _byTarget.Remove(expired[i]);
    }
}
