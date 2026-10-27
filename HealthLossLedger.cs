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

// Which of a player's damage-base methods this mod observes for blood loss.
//
// Everything that reports a figure is observed, because the watermark ledger only credits a real decrease and
// therefore needs no exclusion list to stay correct. There is exactly one exception, and it is a crash
// mitigation rather than a change of policy: `ReceiveParasiteDamage` is left unpatched so the game's own
// method carries no detour of this mod at all. Two sessions on one machine - one host, one client, the same
// rundown layout - ended in the same `Stack overflow; Repeat 1526 times; DMD<ReceiveParasiteDamage> <->
// IL2CPP runtime invoke` signature, and this mod's only detour anywhere near that call is the postfix below,
// which observes every `Receive*` of `Dam_PlayerDamageBase`. The link is a correlation and not a proof: no
// callback frame of this mod appears in those stacks. Leaving that single method alone is the smallest change
// that removes the correlation. The game's damage is untouched and every other `Receive*` still reports, but the
// cost is real and it is not merely a delay: the ledger only credits a decrease it *observes*, so a drop caused by
// a parasite tick that no other observed event reports is not sampled at all. Whether the next observation recovers
// it depends on what that observation is - a later decrease (any other `Receive*`, `Setup`, `OnRevive`,
// `OnApplyDamageOverTimeModifier`, `OnKilledByTankTentacle`) credits the accumulated difference, but a heal, a revive
// or a maximum-health change lands first often enough, and its watermark re-bases upward, after which the parasite
// damage is gone from `HealthLost` for good. The metric is therefore allowed to under-count for that one damage kind;
// no line anywhere may promise that the next observation recovers it.
//
// Nothing else may be added here: a re-entry guard was considered and rejected, because the reported stack
// never returns to this postfix - the loop lives inside the IL2CPP bridge, where no postfix gets a frame.
public static class HealthLossTargets
{
    // The one method this mod deliberately does not detour.
    public const string ParasiteDamage = "ReceiveParasiteDamage";

    // Every `Receive*` of the damage base is observed, except that one, and the named events below are
    // observed for the reasons the ledger documents (a heal, a revive, a tentacle kill and the spawn seed).
    public static bool Accepts(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name == ParasiteDamage) return false;
        if (name.StartsWith("Receive", StringComparison.Ordinal)) return true;
        return name == "OnApplyDamageOverTimeModifier" || name == "OnRevive"
            || name == "OnKilledByTankTentacle" || name == "Setup";
    }
}
