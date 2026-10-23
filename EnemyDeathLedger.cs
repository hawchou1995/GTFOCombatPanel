using System;
using System.Collections.Generic;

namespace HawChow.GTFOCombatPanel;

// A receive callback can notify death before its postfix sees the final health.
// Keep that notification inside the damage scope until actual health loss is known.
//
// The sleeping reading the caller brings is the game's own answer - `ES_StateEnum.Hibernate` - which
// covers every enemy but the Scout. ScoutSleepPolicy holds this machine's capture of the Scout's own
// state and overrides that reading only for an enemy it captured, so the two rules stay separate: an
// enemy nobody captured keeps exactly the answer the game gave.
internal sealed class EnemyDeathLedger
{
    internal sealed class Scope
    {
        internal IntPtr Enemy;
        internal ulong Attacker;
        internal DamageKind Cause;
        internal bool? Sleeping;
        internal float Before;
        internal Scope Parent;
        internal bool Closed;
    }
    private readonly Dictionary<IntPtr, Scope> _active = new();
    private readonly HashSet<IntPtr> _credited = new();
    internal Scope Begin(IntPtr enemy, ulong attacker, DamageKind cause, bool? sleeping, float before)
    {
        if (enemy == IntPtr.Zero || attacker == 0 || !float.IsFinite(before) || before <= 0) return null;
        _active.TryGetValue(enemy, out Scope parent);
        var scope = new Scope { Enemy = enemy, Attacker = attacker, Cause = cause, Sleeping = ScoutSleepPolicy.Resolve(enemy, sleeping), Before = before, Parent = parent };
        _active[enemy] = scope;
        return scope;
    }
    internal bool IsReceiving(IntPtr enemy) => _active.ContainsKey(enemy);
    internal bool Complete(Scope scope, float after, bool succeeded)
    {
        if (scope == null || scope.Closed) return false;
        scope.Closed = true;
        if (_active.TryGetValue(scope.Enemy, out Scope current) && ReferenceEquals(current, scope))
        {
            if (scope.Parent == null) _active.Remove(scope.Enemy);
            else _active[scope.Enemy] = scope.Parent;
        }
        return succeeded && float.IsFinite(after) && after <= 0.0001f && after < scope.Before && _credited.Add(scope.Enemy);
    }
    internal bool TryClaimUnscoped(IntPtr enemy) => enemy != IntPtr.Zero && !IsReceiving(enemy) && _credited.Add(enemy);
    // A spawn hands this pointer to a new enemy, so the Scout capture of the previous occupant goes too.
    internal void Spawn(IntPtr enemy) { _active.Remove(enemy); _credited.Remove(enemy); ScoutSleepPolicy.Spawn(enemy); }
    // One expedition's captures mean nothing in the next.
    internal void Clear() { _active.Clear(); _credited.Clear(); ScoutSleepPolicy.Clear(); }
}
