using System;
using System.Collections.Generic;
using HawChow.GTFOCombatPanel;

internal static partial class Program
{
    public struct DamageSourcePacket { public int slot; }
    public static class ReplayDamageApi
    {
        public delegate void Callback(float damage, object enemy, object limb, object source, DamageSourcePacket cwc);
        public static event Callback PreExplosiveDamage, PostExplosiveDamage, PreShrapnelDamage, PostShrapnelDamage, PreDOTDamage, PostDOTDamage;
        public static void Fire()
        {
            var p = new DamageSourcePacket { slot = 2 };
            PreExplosiveDamage?.Invoke(42, null, null, null, p); PostExplosiveDamage?.Invoke(42, null, null, null, p);
            PreShrapnelDamage?.Invoke(42, null, null, null, p); PostShrapnelDamage?.Invoke(42, null, null, null, p);
            PreDOTDamage?.Invoke(42, null, null, null, p); PostDOTDamage?.Invoke(42, null, null, null, p);
        }
    }
    private static void RunEwcDamageScenarios()
    {
        var events = new List<string>();
        using (OptionalDamageEvents.Bind(typeof(ReplayDamageApi), (kind, pre, args) =>
        {
            Check((float)args[0] == 42 && ((DamageSourcePacket)args[4]).slot == 2, "EWC callback boxes pCWC slot without strong assembly reference");
            events.Add(kind + ":" + pre);
        })) ReplayDamageApi.Fire();
        Check(events.Count == 6 && events[0] == "Explosive:True" && events[5] == "DOT:False", "all three EWC damage kinds receive paired pre/post callbacks");
        ReplayDamageApi.Fire(); Check(events.Count == 6, "EWC damage binding disposes all six handlers");
        Check(DamageObservation.Uncredited(100, 58, 10, 10) == 42, "direct EWC health write is captured");
        Check(DamageObservation.Uncredited(100, 58, 10, 52) == 0, "native receive and EWC event never double credit the same loss");
        Check(DamageObservation.Uncredited(100, 58, 10, 30) == 22, "only the not-yet-credited residual is added");
        Check(DamageObservation.Uncredited(20, -100, 0, 0) == 20, "overkill damage is bounded by the actual health pool");
        Check(DamageObservation.Uncredited(20, 40, 0, 0) == 0 && DamageObservation.Uncredited(double.NaN, 0, 0, 0) == 0, "heals and invalid observations add no damage");
        var credit = new DamageCreditLedger();
        IntPtr outer = new IntPtr(1), chain = new IntPtr(2);
        credit.Add(chain, 7, 500);
        Check(DamageObservation.Uncredited(100, 58, 0, credit.Read(outer, 7)) == 42,
            "a nested chain explosion on another enemy cannot erase the outer target's damage");
        credit.Add(outer, 8, 30);
        Check(credit.Read(outer, 7) == 0, "another source's damage does not settle this source's observation");
        credit.Add(outer, 7, 42);
        Check(DamageObservation.Uncredited(100, 58, 0, credit.Read(outer, 7)) == 0, "same target and source native credit deduplicates the event");
        credit.Spawn(outer); Check(credit.Read(outer, 7) == 0 && credit.Read(chain, 7) == 500, "pooled enemy clears only its own damage credit");
        credit.Clear(); Check(credit.Read(chain, 7) == 0, "new expedition clears all reconciliation credit");
    }
}
