using System;
using System.Collections.Generic;

namespace HawChow.GTFOCombatPanel;

// The component pointer is a pool slot, not a projectile ID. Each spawn owns a fresh sample.
internal sealed class ProjectileAccuracyLedger
{
    private sealed class Flight
    {
        internal ulong Token;
        internal CombatPlayer Player;
        internal WeaponCombatStats Weapon;
        internal readonly ShotSample Sample = new ShotSample();
    }

    private readonly Dictionary<IntPtr, Flight> _flights = new Dictionary<IntPtr, Flight>();
    internal int Count => _flights.Count;
    internal const int Capacity = 16384;

    internal bool Spawn(IntPtr poolSlot, ulong token, CombatPlayer player, WeaponCombatStats weapon)
    {
        if (poolSlot == IntPtr.Zero || player == null || weapon == null) return false;
        if (_flights.TryGetValue(poolSlot, out Flight previous) && previous.Token == token) return false;
        if (!_flights.ContainsKey(poolSlot) && _flights.Count >= Capacity) return false;
        var flight = new Flight { Token = token, Player = player, Weapon = weapon };
        flight.Sample.BeginPellet();
        _flights[poolSlot] = flight;
        player.RecordAccuracy(weapon, 1, 0, 0);
        return true;
    }

    internal bool Hit(IntPtr poolSlot, ulong token, bool enemy, bool weakspot)
    {
        if (!enemy || !_flights.TryGetValue(poolSlot, out Flight flight) || flight.Token != token) return false;
        int hits = flight.Sample.Hits, weak = flight.Sample.WeakspotHits;
        flight.Sample.Contact(weakspot);
        int hitDelta = flight.Sample.Hits - hits, weakDelta = flight.Sample.WeakspotHits - weak;
        if (hitDelta == 0 && weakDelta == 0) return false;
        flight.Player.AdjustAccuracy(flight.Weapon, 0, hitDelta, weakDelta);
        return true;
    }

    internal void Destroy(IntPtr poolSlot, ulong token)
    {
        if (_flights.TryGetValue(poolSlot, out Flight flight) && flight.Token == token) _flights.Remove(poolSlot);
    }

    internal void Reset() => _flights.Clear();
    internal static bool UseNativeCapture(bool projectileObserved) => !projectileObserved;
}
