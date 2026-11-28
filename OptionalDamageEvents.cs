using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace HawChow.GTFOCombatPanel;

// Adapt EWC's value-type pCWC without making it an assembly reference of the public DLL.
internal sealed class OptionalDamageEvents : IDisposable
{
    private readonly List<(EventInfo Event, Delegate Handler)> _handlers = new();
    internal static OptionalDamageEvents Bind(Type api, Action<string, bool, object[]> callback)
    {
        var result = new OptionalDamageEvents();
        try
        {
            foreach (string kind in new[] { "Explosive", "Shrapnel", "DOT" })
                foreach (bool before in new[] { true, false })
                {
                    string name = (before ? "Pre" : "Post") + kind + "Damage";
                    EventInfo evt = api.GetEvent(name, BindingFlags.Public | BindingFlags.Static)
                        ?? throw new MissingMemberException(api.FullName, name);
                    MethodInfo invoke = evt.EventHandlerType.GetMethod("Invoke");
                    ParameterInfo[] signature = invoke.GetParameters();
                    if (signature.Length != 5 || invoke.ReturnType != typeof(void)) throw new InvalidOperationException(name + " signature mismatch");
                    var args = new ParameterExpression[signature.Length];
                    var boxed = new Expression[signature.Length];
                    for (int i = 0; i < args.Length; i++)
                    { args[i] = Expression.Parameter(signature[i].ParameterType); boxed[i] = Expression.Convert(args[i], typeof(object)); }
                    var body = Expression.Invoke(Expression.Constant(callback), Expression.Constant(kind),
                        Expression.Constant(before), Expression.NewArrayInit(typeof(object), boxed));
                    Delegate handler = Expression.Lambda(evt.EventHandlerType, body, args).Compile();
                    evt.AddEventHandler(null, handler);
                    result._handlers.Add((evt, handler));
                }
            return result;
        }
        catch { result.Dispose(); throw; }
    }
    public void Dispose()
    {
        foreach (var item in _handlers) item.Event.RemoveEventHandler(null, item.Handler);
        _handlers.Clear();
    }
}

internal static class DamageObservation
{
    internal static double Uncredited(double before, double after, double recordedBefore, double recordedAfter)
    {
        if (!double.IsFinite(before) || !double.IsFinite(after) || !double.IsFinite(recordedBefore) || !double.IsFinite(recordedAfter)) return 0;
        return Math.Max(0, Math.Max(0, before - Math.Max(0, after)) - Math.Max(0, recordedAfter - recordedBefore));
    }
}

// What a blow this machine dealt to an enemy limb is worth, for the one case the game leaves unread. On a
// client whose host runs no panel of its own, the host applies the blow and this machine is told the result
// later, so reading the limb's health before and after the call can return the same number twice. The figure
// the weapon carried into the call is then all there is - but it is the pre-armour, pre-precision value, so
// it is an estimate and is capped by the health the limb still had. When the health did move, that loss is
// the applied damage and it wins outright, which is the 1.19.0 reading and stays exactly as it was.
internal static class LocalOutgoingRule
{
    internal static double Amount(float healthBefore, float healthAfter, float dam)
    {
        if (!float.IsFinite(healthBefore) || !float.IsFinite(healthAfter)) return 0;
        double delta = Math.Max(0f, Math.Max(0f, healthBefore) - Math.Max(0f, healthAfter));
        if (delta > 0) return delta;
        if (!float.IsFinite(dam)) return 0;
        // A limb already at zero keeps it that way, and an overkill cannot credit more than the pool it emptied.
        return Math.Max(0, Math.Min(dam, Math.Max(0f, healthBefore)));
    }

    // 1.19.65: true when the two reads this call produced really differ, which is the only case where this event's
    // own loss was measured rather than carried in by the weapon. Only such an event is an attributable reading:
    // it may correct the estimates this machine already made for the same enemy (`EnemyHealthBudget`), and the
    // credit it earns is confirmed and never withdrawn.
    internal static bool Measured(float healthBefore, float healthAfter)
        => float.IsFinite(healthBefore) && float.IsFinite(healthAfter) && healthBefore > 0f && healthAfter < healthBefore;
}

// One enemy instance's remaining health, as this machine can best observe it while it has no authority over
// that instance - and the estimates this machine put on the board from it, so an attributable reading can take
// them back again. 1.19.65.
//
// A blow this machine deals on such a client is not read back from the enemy: the host applies it and only
// tells this machine the result later, so a health read taken across the call can return the same number
// twice - the recorded Survival session has `healthBefore=20.0, healthAfter=20.0, dam=4.3`, and that is a
// bounded sample, never a single blow of 34. The figure the weapon carries in is then the only evidence left,
// and it is pre-armour and theoretical, so five pellets of one shotgun add up to more health than the enemy
// has. This is the balance that confines them: ONE balance per enemy instance - `Dam_EnemyDamageBase`, the
// object every limb of that enemy shares - charged by every estimate this machine makes for it, so two limbs,
// two weapons and five pellets all draw on the same remaining health.
//
// What this balance is NOT:
//   * not the enemy's maximum health - an enemy met with 25 of its 30 left credits 25, never 30;
//   * not one limb's health - a blow on another limb spends the same balance;
//   * not a lifetime sum of the run's credits - subtracting those from a current observation turns a
//     sync-down that already reflects the previous 7 into 11 while 18 is really left.
//
// The rules, in one place:
//   * the first observation of an instance opens the balance at the health that was readable;
//   * an observation BELOW the balance is the sync saying that something this machine did not credit consumed
//     health (another player, a DOT, the host's own bookkeeping). The balance is aligned down to it and nothing
//     is credited, so one event's real loss and an unattributed sync change never become the same number, and
//     the earlier credit is not deducted a second time;
//   * an observation AT or ABOVE the balance raises nothing. A lagging, older or higher snapshot never hands
//     back budget, and on its own it never takes a credit away either: an unattributed reading is no evidence
//     about who dealt what, so it is never booked as this machine's damage;
//   * an ATTRIBUTABLE reading is the one exception, and only downwards. When this event's own health really
//     moved (`before > after`), its `before` is a confirmed statement about the instance, and every estimate
//     this machine still has on the board for that instance which the statement cannot support is withdrawn
//     again - total, weapon row and Scout row alike. That is what keeps "old snapshot 20, estimated 20, then
//     really 20 -> 13" from throwing the 7 away and locking the remaining 13: the 20 is taken back, the 7 is
//     credited, and 13 is what is left. A credit this machine itself measured across a call is never withdrawn
//     (it is confirmed, not estimated), and an earlier 7 is not deducted twice from a reading that already
//     reflects it;
//   * an observation of zero - or a reading that says the instance is gone - closes it: a corpse earns nothing.
internal sealed class EnemyHealthBudget
{
    private const double Epsilon = 1e-6;

    // One estimate this machine put on the board for this enemy, kept only so an attributable reading can take
    // it back: the same three places the credit went - the player's total, the weapon row and the Scout row.
    private sealed class Estimate
    {
        internal CombatPlayer Player;
        internal WeaponCombatStats Weapon;
        internal bool Scout;
        internal double Amount;
    }

    // The instance: the health it was last known to have (`Opened`, only ever lowered), everything this machine
    // has live on the board for it (`Credited`), and the still-withdrawable estimates among that credit.
    private sealed class Row
    {
        internal IntPtr Owner;
        internal double Opened;
        internal double Credited;
        internal bool Closed;
        internal readonly List<Estimate> Estimates = new List<Estimate>();
        internal double Remaining => Math.Max(0, Opened - Credited);
    }

    private readonly Dictionary<IntPtr, Row> _rows = new();

    // The balance this instance still holds. An unknown, spent or closed instance answers zero.
    internal double Remaining(IntPtr enemy)
        => enemy != IntPtr.Zero && _rows.TryGetValue(enemy, out Row row) ? row.Remaining : 0;

    // One blow, charged against the single balance of the instance it landed on. `wanted` is the caller's own
    // figure for this blow - the health the call really moved, or the figure the weapon carried in - and it is
    // only ever lowered here. `attributable` says this event's own health really moved, which is what makes its
    // `before` a confirmed reading: such an event may withdraw estimates the reading does not support, and the
    // credit it earns is confirmed and therefore never withdrawn later. The answer is what may be credited;
    // `withdrawn` is what was taken back off the board first.
    internal double Charge(IntPtr enemy, IntPtr owner, CombatPlayer player, WeaponCombatStats weapon, bool scout,
        float healthBefore, float healthAfter, double wanted, bool attributable, out double withdrawn)
    {
        withdrawn = 0;
        if (enemy == IntPtr.Zero || !float.IsFinite(healthBefore)) return 0;
        Row row = Entry(enemy, owner, healthBefore);
        Align(row, healthBefore);
        if (attributable) withdrawn = WithdrawUnsupported(row, healthBefore);
        double taken = 0;
        if (double.IsFinite(wanted) && wanted > 0 && !row.Closed && row.Remaining > Epsilon)
        {
            taken = Math.Min(wanted, row.Remaining);
            row.Credited += taken;
            if (!attributable && player != null)
                row.Estimates.Add(new Estimate { Player = player, Weapon = weapon, Scout = scout, Amount = taken });
        }
        // The health read after the call is that same event's own outcome: it can only align the balance down, and
        // a loss measured across the call was already charged while the balance sat where the reading now is, so
        // the same event is never deducted twice.
        Align(row, healthAfter);
        return taken;
    }

    // Aligns the instance's balance with one health this machine has just read, and answers what is left. This is
    // the unattributed path: it never credits and never withdraws - a sync change is not this machine's damage.
    internal double Observe(IntPtr enemy, IntPtr owner, float health)
    {
        if (enemy == IntPtr.Zero || !float.IsFinite(health)) return Remaining(enemy);
        Row row = Entry(enemy, owner, health);
        Align(row, health);
        return row.Remaining;
    }

    // One new occupant of this pointer: the enemy's own spawn edge clears what the last one spent. The board keeps
    // what it was already told - an old occupant's credit is not this new enemy's to take back.
    internal void Spawn(IntPtr enemy) { if (enemy != IntPtr.Zero) _rows.Remove(enemy); }
    internal void Clear() => _rows.Clear();

    // The instance behind this pointer, opened at the first readable health, and replaced when a different agent
    // appears behind a reused pointer. A new occupant starts from nothing; the previous one's estimates go with
    // it, unwithdrawn, because another enemy cannot prove anything about them.
    private Row Entry(IntPtr enemy, IntPtr owner, float health)
    {
        Row row = _rows.TryGetValue(enemy, out Row found) ? found : null;
        bool reused = row != null && owner != IntPtr.Zero && row.Owner != IntPtr.Zero && row.Owner != owner;
        if (row == null || reused)
        {
            row = new Row { Owner = owner, Opened = Math.Max(0, health), Closed = health <= 0 };
            _rows[enemy] = row;
        }
        else if (owner != IntPtr.Zero) row.Owner = owner;
        return row;
    }

    // A lower reading lowers the ceiling by the same amount, so the balance always equals what the enemy was last
    // seen to have. A reading at or above it changes nothing: the balance never rises.
    private static void Align(Row row, float health)
    {
        if (row == null || !float.IsFinite(health)) return;
        if (health <= 0) { row.Opened = row.Credited; row.Closed = true; return; }
        if (row.Closed) return;
        if (health < row.Remaining) row.Opened = health + row.Credited;
    }

    // What this machine still claims for the instance that a confirmed reading of `health` does not support. The
    // reading can only ever prove that LESS was taken than was estimated; it can never prove more, so this only
    // runs on an attributable event and only over the estimates (a measured credit is confirmed and stays).
    private static double WithdrawUnsupported(Row row, float health)
    {
        if (row == null || row.Closed || row.Estimates.Count == 0) return 0;
        double supported = Math.Max(0, row.Opened - health);
        double excess = row.Credited - supported;
        if (!(excess > Epsilon)) return 0;
        double withdrawn = 0;
        for (int index = row.Estimates.Count - 1; index >= 0 && withdrawn < excess - Epsilon; index--)
        {
            Estimate estimate = row.Estimates[index];
            double take = Math.Min(estimate.Amount, excess - withdrawn);
            if (!(take > Epsilon)) continue;
            estimate.Amount -= take;
            withdrawn += take;
            estimate.Player?.Total.WithdrawEnemyDamage(take);
            estimate.Weapon?.Counters.WithdrawEnemyDamage(take);
            if (estimate.Scout) estimate.Player?.Impact.WithdrawScoutDamage(take);
            if (estimate.Amount <= Epsilon) row.Estimates.RemoveAt(index);
        }
        row.Credited = Math.Max(0, row.Credited - withdrawn);
        return withdrawn;
    }
}
internal sealed class DamageCreditLedger
{
    private readonly Dictionary<IntPtr, Dictionary<ulong, double>> _credit = new();
    internal double Read(IntPtr enemy, ulong owner)
        => _credit.TryGetValue(enemy, out var row) && row.TryGetValue(owner, out double amount) ? amount : 0;
    internal void Add(IntPtr enemy, ulong owner, double amount)
    {
        if (enemy == IntPtr.Zero || owner == 0 || !double.IsFinite(amount) || amount <= 0) return;
        if (!_credit.TryGetValue(enemy, out var row)) _credit[enemy] = row = new Dictionary<ulong, double>();
        row[owner] = Math.Min(double.MaxValue, Read(enemy, owner) + amount);
    }
    internal void Spawn(IntPtr enemy) => _credit.Remove(enemy);
    internal void Clear() => _credit.Clear();
}
