using System.Collections.Generic;
using System.Reflection;
using Enemies;
using Gear;
using HarmonyLib;
using Player;
using UnityEngine;

namespace HawChow.GTFOCombatPanel;

// Observe both sides of tag-progress updates, which may mutate or clear the taggable list.
// The shared edge ledger deduplicates these samples and the normal frame poll.
[HarmonyPatch]
internal static class HumanScanProgressPatch
{
    private static IEnumerable<MethodBase> TargetMethods() => ImpactTargets.Methods(typeof(EnemyScanner), "UpdateTagProgress");

    [HarmonyPrefix]
    private static void Prefix(EnemyScanner __instance, out CombatTracker.HumanScanCapture __state)
        => __state = CombatTracker.BeginHumanScan(__instance);

    [HarmonyFinalizer]
    private static System.Exception Finalizer(CombatTracker.HumanScanCapture __state, System.Exception __exception)
    { CombatTracker.EndHumanScan(__state, __exception); return __exception; }
}

// BotGetScanResult is a query, not a marking attempt. Only BotTag credits bot scans.
[HarmonyPatch]
internal static class BotTagPatch
{
    private static IEnumerable<MethodBase> TargetMethods() => ImpactTargets.Methods(typeof(EnemyScanner), "BotTag");

    [HarmonyPostfix]
    private static void Postfix(Vector3 centerPosition, Il2CppSystem.Collections.Generic.List<EnemyAgent> foundEnemies)
        => CombatTracker.CreditBotScan(centerPosition, foundEnemies);
}

// BotTag is static, so the callback itself can never say which bot tagged. Its one caller is the
// bot's own "use the enemy scanner" action, an instance whose m_agent is exactly that bot: the
// action brackets the callback, so the owner published here is taken while it runs and cleared when
// it returns. That is the attribution the proximity rule was guessing at.
[HarmonyPatch]
internal static class BotScanActionPatch
{
    private static IEnumerable<MethodBase> TargetMethods() => ImpactTargets.Methods(typeof(PlayerBotActionUseEnemyScanner), "UpdateStateScan");

    [HarmonyPrefix]
    private static void Prefix(PlayerBotActionUseEnemyScanner __instance) => CombatTracker.BeginBotScanAction(__instance);

    [HarmonyFinalizer]
    private static System.Exception Finalizer(System.Exception __exception)
    { CombatTracker.EndBotScanAction(); return __exception; }
}

