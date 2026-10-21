using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace HawChow.GTFOCombatPanel;

// Observe only damage methods. SetHealth, max-health changes, healing and revives are not damage.
[HarmonyPatch]
internal static class HealthLossPatches
{
    private static readonly HealthLossLedger Ledger = new();
    internal static void Reset() => Ledger.Clear();
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var method in typeof(Dam_PlayerDamageBase).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            if ((method.Name.StartsWith("Receive", StringComparison.Ordinal) && method.Name.EndsWith("Damage", StringComparison.Ordinal)) ||
                method.Name == "OnApplyDamageOverTimeModifier") yield return method;
    }
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(Dam_PlayerDamageBase __instance, out HealthLossLedger.Scope __state)
    {
        __state = null;
        try
        {
            if (GameStateManager.CurrentStateName == eGameStateName.InLevel && CombatTracker.ShouldTrack(__instance?.Owner?.Owner))
                __state = Ledger.Begin(__instance.Pointer, __instance.Health, __instance.HealthMax);
        }
        catch (Exception error) { CombatTracker.LogToolError("health loss before", error); }
    }
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception Finalizer(Dam_PlayerDamageBase __instance, HealthLossLedger.Scope __state, Exception __exception)
    {
        if (__state == null) return __exception;
        try
        {
            double loss = Ledger.Complete(__state, __instance.Health, __instance.HealthMax, __exception == null);
            if (loss > 0 && CombatTracker.ShouldTrack(__instance.Owner?.Owner))
            {
                var counters = CombatTracker.ObservePlayer(__instance.Owner.Owner).Impact;
                counters.HealthLost = Math.Min(double.MaxValue, counters.HealthLost + loss);
            }
        }
        catch (Exception error) { Ledger.Complete(__state, double.NaN, double.NaN, false); CombatTracker.LogToolError("health loss after", error); }
        return __exception;
    }
}
