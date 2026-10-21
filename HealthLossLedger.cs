using System;
using System.Collections.Generic;

namespace HawChow.GTFOCombatPanel;

internal sealed class HealthLossLedger
{
    internal sealed class Scope
    {
        internal IntPtr Target;
        internal double Before, Maximum;
        internal Scope Parent;
        internal bool Closed;
    }
    private readonly Dictionary<IntPtr, Scope> _active = new();
    internal Scope Begin(IntPtr target, double health, double maximum)
    {
        if (target == IntPtr.Zero || !double.IsFinite(health) || !double.IsFinite(maximum) || health < 0 || maximum <= 0) return null;
        _active.TryGetValue(target, out Scope parent);
        var scope = new Scope { Target = target, Before = health, Maximum = maximum, Parent = parent };
        _active[target] = scope;
        return scope;
    }
    internal double Complete(Scope scope, double health, double maximum, bool success)
    {
        if (scope == null || scope.Closed) return 0;
        scope.Closed = true;
        if (_active.TryGetValue(scope.Target, out Scope current) && ReferenceEquals(current, scope))
        {
            if (scope.Parent == null) _active.Remove(scope.Target);
            else _active[scope.Target] = scope.Parent;
        }
        if (scope.Parent != null || !success || !double.IsFinite(health) || !double.IsFinite(maximum) ||
            Math.Abs(scope.Maximum - maximum) > 0.0001) return 0;
        return Math.Max(0, Math.Min(scope.Before, scope.Maximum) - Math.Max(0, health));
    }
    internal void Clear() => _active.Clear();
}
