using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace HawChow.GTFOCombatPanel;

// Observe every method of a player's damage base that can move their health, and credit only what was
// actually lost. Nothing is measured by wrapping a single call any more: the ledger keeps a watermark per
// player and is fed by every path that reports a figure, so a host sees a client's life go down whether
// the drop arrived as a damage event or as the replicated health update behind it - which is the whole of
// the "blood loss missing on the host" report - and a drop reported twice is still counted once.
//
// Healing, revives and maximum-health changes are not blood loss, and they are not filtered by name here:
// the ledger credits a decrease against an unchanged ceiling and nothing else, so a method that raises the
// figure needs no exclusion list to stay out of the total. `Setup` is in the list for the opposite reason:
// it is the moment a freshly spawned player's mark is seeded at full health, so the first blow that lands
// on them is measured instead of being swallowed as the first reading of a stranger.
[HarmonyPatch]
internal static class HealthLossPatches
{
    private static readonly HealthLossLedger Ledger = new HealthLossLedger();

    internal static void Reset()
    {
        Ledger.Clear();
    }

    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (MethodInfo method in typeof(Dam_PlayerDamageBase).GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (method.IsAbstract || method.ContainsGenericParameters) continue;
            if (!ReturnsVoid(method)) continue;
            string name = method.Name;
            if (name.StartsWith("Receive", StringComparison.Ordinal)
                || name == "OnApplyDamageOverTimeModifier"
                || name == "OnRevive"
                || name == "OnKilledByTankTentacle"
                || name == "Setup")
                yield return method;
        }
    }

    private static bool ReturnsVoid(MethodInfo method)
    {
        try { return method.ReturnType == typeof(void); }
        catch (Exception) { return false; }
    }

    [HarmonyPostfix]
    [HarmonyWrapSafe]
    private static void Postfix(Dam_PlayerDamageBase __instance)
    {
        try
        {
            if (__instance == null) return;
            var owner = __instance.Owner != null ? __instance.Owner.Owner : null;
            if (!CombatTracker.ShouldTrack(owner)) return;
            ulong lookup = 0UL;
            try { lookup = owner != null ? owner.Lookup : 0UL; } catch (Exception) { }
            // The mark is seeded by whatever report arrives first, in the level or out of it. `Setup` runs when a
            // player is spawned and the lobby health sync runs before anyone has been hit; both are the moment a
            // full-health reading can be taken, and without it the first blow of the run would be swallowed as
            // "the first reading of a stranger". Only the credit is limited to a level.
            double loss = Ledger.Observe(__instance.Pointer, lookup, __instance.Health, __instance.HealthMax);
            if (!(loss > 0)) return;
            if (GameStateManager.CurrentStateName != eGameStateName.InLevel) return;
            var counters = CombatTracker.ObservePlayer(owner).Impact;
            counters.HealthLost = Math.Min(double.MaxValue, counters.HealthLost + loss);
        }
        catch (Exception error) { CombatTracker.LogToolError("health loss observation", error); }
    }
}
