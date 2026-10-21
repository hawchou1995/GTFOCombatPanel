using System;
using System.Collections.Generic;

namespace HawChow.GTFOCombatPanel;

// A receive callback can notify death before its postfix sees the final health.
// Keep that notification inside the damage scope until actual health loss is known.
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
        var scope = new Scope { Enemy = enemy, Attacker = attacker, Cause = cause, Sleeping = sleeping, Before = before, Parent = parent };
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
    internal void Spawn(IntPtr enemy) { _active.Remove(enemy); _credited.Remove(enemy); }
    internal void Clear() { _active.Clear(); _credited.Clear(); }
}
