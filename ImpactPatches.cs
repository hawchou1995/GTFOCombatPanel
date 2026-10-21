using System.Collections.Generic;
using System.Reflection;
using Agents;
using Enemies;
using Gear;
using HarmonyLib;
using Player;

namespace HawChow.GTFOCombatPanel;

// Every game-side source the influence board scores that the damage and accuracy patches do not
// already hold: stagger, kills, revives, the supply a player hands out, and the packs they hand it
// out of. Each hook hands its numbers to CombatTracker, which owns the counters and the guards.
//
// The target of every patch is resolved by name and yielded only when it exists, so a game build
// that no longer carries one of these methods loses that one row instead of failing the plugin.
// The first time a hook fires it says so in the log, which is how a build whose game-side
// assumption turned out to be wrong reports itself.
internal static class ImpactTargets
{
    internal static IEnumerable<MethodBase> Methods(System.Type type, string name)
    {
        MethodBase method = AccessTools.Method(type, name);
        if (method != null) yield return method;
    }
}

// Stagger: damage that lands on an enemy at the instant it is attacking or charging an attack is
// what stops the attack. The game reports the hit react, and the enemy reports its own state.
[HarmonyPatch]
internal static class StaggerDamagePatch
{
    private static IEnumerable<MethodBase> TargetMethods() => ImpactTargets.Methods(typeof(Dam_EnemyDamageBase), "ProcessReceivedDamage");

    [HarmonyPrefix]
    private static void Prefix(Dam_EnemyDamageBase __instance, float damage, Agent damageSource, ES_HitreactType hitreact)
        => CombatTracker.CreditStagger(__instance, damage, damageSource, hitreact);
}

// A kill: the enemy's damage base resolves the death. The damage base also carries the enemy's own
// maximum health, which is what grades the kill, so a modded enemy is graded by what it is.
[HarmonyPatch]
internal static class EnemyDeathPatch
{
    private static IEnumerable<MethodBase> TargetMethods() => ImpactTargets.Methods(typeof(Dam_EnemyDamageBase), "InstantDead");

    [HarmonyPostfix]
    private static void Postfix(Dam_EnemyDamageBase __instance) => CombatTracker.CreditKill(__instance);
}

// The same death seen from the other side. Both hooks feed one de-duplicated ledger, so an enemy
// that trips both paths is still counted once.
[HarmonyPatch]
internal static class EnemyAgentDeathPatch
{
    private static IEnumerable<MethodBase> TargetMethods() => ImpactTargets.Methods(typeof(EnemyAgent), "OnDead");

    [HarmonyPostfix]
    private static void Postfix(EnemyAgent __instance) => CombatTracker.CreditKill(__instance);
}

// The revive interaction callback is local to the initiating machine and does not
// carry remote teammates' successful revives to the host. Capture the game's
// replicated source/target pair, then wait for the downed player's completion.
[HarmonyPatch]
internal static class ReviveStateSyncPatch
{
    private static IEnumerable<MethodBase> TargetMethods() => ImpactTargets.Methods(typeof(Interact_Revive), "SyncReviverState");

    [HarmonyPostfix]
    private static void Postfix(Interact_Revive.pPlayerReviveState __0) => CombatTracker.ObserveReviveState(__0);
}

[HarmonyPatch]
internal static class ReviveCompletionPatch
{
    private static IEnumerable<MethodBase> TargetMethods() => ImpactTargets.Methods(typeof(PLOC_Downed), "OnPlayerRevived");

    [HarmonyPostfix]
    private static void Postfix(PLOC_Downed __instance) => CombatTracker.ConfirmRevive(__instance);
}

[HarmonyPatch(typeof(EnemyAgent), nameof(EnemyAgent.Setup))]
internal static class EnemySpawnLedgerPatch
{
    [HarmonyPostfix]
    private static void Postfix(EnemyAgent __instance) => CombatTracker.ObserveEnemySpawn(__instance);
}
