using System;
using System.Collections.Generic;

namespace HawChow.GTFOCombatPanel;

// How much life a player has actually lost, accumulated from a watermark instead of from a pair of readings
// around one call.
//
// The previous release measured each damage method by reading health before the call and after it, and
// credited the difference. That is exact for the machine that applies the blow and blind for everybody else:
// a host sees a client's health arrive through the replicated health update as often as through a damage
// method, and a figure that moves outside one of the patched methods - a fall through a wrapper, a grab that
// applies its damage from a coroutine, a death handler - was never observed at all. That is the "blood loss
// missing" and the "blood loss too small" a host reports, and no formula on top of a per-call pair fixes it.
//
// A watermark has neither failure. Every observation of a player's health goes through `Observe`: the figure
// is compared with the last one this machine saw for that player, only a decrease is credited, and it is
// credited once no matter how many paths report the same drop. A heal or a revive raises the mark and credits
// nothing.
//
// The mark keeps absolute health points and the maximum they were read against, so a change of the ceiling
// cannot scale an old reading into a figure nobody lost. A ceiling that fell may be the reason a figure is
// lower - the player did not lose life, the maximum under them moved - so that observation only re-bases the
// mark. A ceiling that rose cannot have pressed a figure down, so a drop reported in that same observation is
// real damage and is credited: an infection that raises the maximum must not swallow the bullet that landed
// with it.
//
// The mark belongs to a (damage base, owner) pair, not to a bare native pointer: an object pool that hands the
// same address to another player must not inherit the previous occupant's life, and the first reading of the
// new occupant is a fresh mark rather than a drop against somebody else's figure.
//
// Nothing here touches Unity, SNet or Harmony, so every rule below is asserted headlessly.
internal sealed class HealthLossLedger
{
    private struct Mark
    {
        public double Health, Maximum;
    }

    // A maximum that moved by less than this is the same maximum, so a float round trip in the replication
    // layer does not re-base the mark on every observation.
    private const double MaximumTolerance = 0.01;

    private readonly Dictionary<long, Mark> _mark = new Dictionary<long, Mark>();

    public int Marked { get { return _mark.Count; } }

    // One observation of a player's health and maximum. Returns the life lost since the last observation, in
    // absolute health points, and never a negative figure. The first observation of a player only sets the
    // mark: this machine did not see that player live before it, so it cannot claim to have seen it lost.
    internal double Observe(IntPtr target, ulong owner, double health, double maximum)
    {
        if (target == IntPtr.Zero) return 0;
        if (!double.IsFinite(health) || !double.IsFinite(maximum) || maximum <= 0) return 0;
        long key = Key(target, owner);
        double now = Math.Max(0, Math.Min(health, maximum));       // overkill is not life that was there
        if (!_mark.TryGetValue(key, out Mark previous))
        {
            _mark[key] = new Mark { Health = now, Maximum = maximum };
            return 0;
        }
        _mark[key] = new Mark { Health = now, Maximum = maximum };
        bool ceilingRose = maximum > previous.Maximum + MaximumTolerance;
        bool ceilingMoved = Math.Abs(previous.Maximum - maximum) > MaximumTolerance;
        // The ceiling fell: the lower figure may be the ceiling's doing rather than damage, so nothing is
        // credited and the mark above is the re-base. Any other case is measured as plain blood.
        if (ceilingMoved && !ceilingRose) return 0;
        return now < previous.Health ? previous.Health - now : 0;
    }

    // A seat that changed hands, or a row that was pruned: nothing of the previous occupant survives.
    internal void Forget(IntPtr target, ulong owner)
    {
        if (target != IntPtr.Zero) _mark.Remove(Key(target, owner));
    }

    internal void Clear() => _mark.Clear();

    private static long Key(IntPtr target, ulong owner)
    {
        long pointer = target.ToInt64();
        unchecked { return pointer * 397L ^ (long)owner; }
    }
}
