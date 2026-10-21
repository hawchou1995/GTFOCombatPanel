using System;
using System.Collections.Generic;

namespace HawChow.GTFOCombatPanel;

// Remembers the most recent player-attributed hit on each teammate until the next down transition.
// The caller supplies the actual alive-state edge and an expiry window; this ledger never infers a down
// from damage magnitude.
public sealed class FriendlyFireAttributionLedger
{
    private readonly struct Hit
    {
        public readonly ulong Attacker;
        public readonly double Time;
        public Hit(ulong attacker, double time) { Attacker = attacker; Time = time; }
    }

    private readonly Dictionary<ulong, Hit> _lastHitByVictim = new Dictionary<ulong, Hit>();

    public int Count => _lastHitByVictim.Count;

    public bool ObserveDamage(ulong attacker, ulong victim, double time)
    {
        if (victim == 0UL || !double.IsFinite(time)) return false;
        if (attacker == 0UL || attacker == victim)
        {
            _lastHitByVictim.Remove(victim);
            return false;
        }
        _lastHitByVictim[victim] = new Hit(attacker, time);
        return true;
    }

    public bool TryTake(ulong victim, double now, double maxAgeSeconds, out ulong attacker)
    {
        attacker = 0UL;
        if (victim == 0UL || !double.IsFinite(now) || !double.IsFinite(maxAgeSeconds) || maxAgeSeconds < 0
            || !_lastHitByVictim.TryGetValue(victim, out Hit hit)) return false;
        _lastHitByVictim.Remove(victim);
        double age = now - hit.Time;
        if (age < 0 || age > maxAgeSeconds) return false;
        attacker = hit.Attacker;
        return attacker != 0UL && attacker != victim;
    }

    public void ForgetVictim(ulong victim)
    {
        if (victim != 0UL) _lastHitByVictim.Remove(victim);
    }

    public void Clear() => _lastHitByVictim.Clear();
}
