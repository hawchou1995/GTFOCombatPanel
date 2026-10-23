using System;
using System.Collections.Generic;

namespace HawChow.GTFOCombatPanel;

// Main-thread request/confirmation matching. Radar candidates never enter this ledger.
public sealed class ScanTagLedger
{
    private readonly Dictionary<IntPtr, (ulong Owner, double Time, int Identity)> _requests = new();
    public const int Capacity = 4096;
    public const double Lifetime = 10;

    public void Request(IntPtr enemy, ulong owner, double time, int identity = 0)
    {
        if (enemy == IntPtr.Zero || owner == 0 || !double.IsFinite(time)) return;
        Prune(time);
        if (_requests.Count >= Capacity && !_requests.ContainsKey(enemy)) return;
        _requests[enemy] = (owner, time, identity);
    }

    public ulong Confirm(IntPtr enemy, ulong authorityOwner, double time, int identity = 0)
    {
        if (enemy == IntPtr.Zero || !double.IsFinite(time)) return 0;
        Prune(time);
        if (authorityOwner != 0) { _requests.Remove(enemy); return authorityOwner; }
        if (!_requests.TryGetValue(enemy, out var request) || time < request.Time || request.Identity != identity) return 0;
        _requests.Remove(enemy);
        return request.Owner;
    }
    public void Cancel(IntPtr enemy) => _requests.Remove(enemy);

    public void Clear() => _requests.Clear();

    private void Prune(double time)
    {
        if (_requests.Count == 0) return;
        var expired = new List<IntPtr>();
        foreach (var pair in _requests)
            if (time - pair.Value.Time > Lifetime) expired.Add(pair.Key);
        foreach (var key in expired) _requests.Remove(key);
    }
}
