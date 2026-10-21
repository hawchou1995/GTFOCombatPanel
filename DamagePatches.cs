using System;
using System.Collections.Generic;
using System.Reflection;
using Agents;
using Gear;
using HarmonyLib;

namespace HawChow.GTFOCombatPanel;

internal static class DamageTargets
{
    internal static IEnumerable<MethodBase> Methods(string name)
    {
        yield return AccessTools.DeclaredMethod(typeof(Dam_EnemyDamageBase), name);
        yield return AccessTools.DeclaredMethod(typeof(Dam_PlayerDamageBase), name);
    }
}

[HarmonyPatch]
internal static class BulletDamagePatch
{
    private static IEnumerable<MethodBase> TargetMethods() => DamageTargets.Methods("ReceiveBulletDamage");
    [HarmonyPrefix]
    private static void Prefix(Dam_SyncedDamageBase __instance, pBulletDamageData data, out DamageSample __state)
        => __state = CombatTracker.Capture(__instance, __instance.Health, data.source, DamageKind.Bullet, data.gearCategoryId);
    [HarmonyPostfix]
    private static void Postfix(Dam_SyncedDamageBase __instance, pBulletDamageData data, DamageSample __state)
        => CombatTracker.RecordDamage(__instance, __state, data);
    [HarmonyFinalizer]
    private static Exception Finalizer(Dam_SyncedDamageBase __instance, DamageSample __state, Exception __exception)
    { CombatTracker.CompleteDamage(__instance, __state, __exception); return __exception; }
}

[HarmonyPatch]
internal static class MeleeDamagePatch
{
    private static IEnumerable<MethodBase> TargetMethods() => DamageTargets.Methods("ReceiveMeleeDamage");
    [HarmonyPrefix]
    private static void Prefix(Dam_SyncedDamageBase __instance, pFullDamageData data, out DamageSample __state)
        => __state = CombatTracker.Capture(__instance, __instance.Health, data.source, DamageKind.Melee, data.gearCategoryId);
    [HarmonyPostfix]
    private static void Postfix(Dam_SyncedDamageBase __instance, DamageSample __state) => CombatTracker.RecordDamage(__instance, __state);
    [HarmonyFinalizer]
    private static Exception Finalizer(Dam_SyncedDamageBase __instance, DamageSample __state, Exception __exception)
    { CombatTracker.CompleteDamage(__instance, __state, __exception); return __exception; }
}

[HarmonyPatch]
internal static class FireDamagePatch
{
    private static IEnumerable<MethodBase> TargetMethods() => DamageTargets.Methods("ReceiveFireDamage");
    [HarmonyPrefix]
    private static void Prefix(Dam_SyncedDamageBase __instance, pSmallDamageData data, out DamageSample __state)
        => __state = CombatTracker.Capture(__instance, __instance.Health, data.source, DamageKind.Fire);
    [HarmonyPostfix]
    private static void Postfix(Dam_SyncedDamageBase __instance, DamageSample __state) => CombatTracker.RecordDamage(__instance, __state);
    [HarmonyFinalizer]
    private static Exception Finalizer(Dam_SyncedDamageBase __instance, DamageSample __state, Exception __exception)
    { CombatTracker.CompleteDamage(__instance, __state, __exception); return __exception; }
}

// A push travels its own path in the game: it is sent through PushDamage and received here, never
// through ReceiveMeleeDamage, and what it breaks is the enemy's attack rather than its health. The
// same hook therefore carries both the (empty) damage record and the stagger interrupt.
[HarmonyPatch]
internal static class PushDamagePatch
{
    private static IEnumerable<MethodBase> TargetMethods() => DamageTargets.Methods("ReceivePushDamage");
    [HarmonyPrefix]
    private static void Prefix(Dam_SyncedDamageBase __instance, pFullDamageData data, out DamageSample __state)
    {
        __state = CombatTracker.Capture(__instance, __instance.Health, data.source, DamageKind.Push, data.gearCategoryId);
        // Read before the game processes the push, while the attack it is about to break is still
        // under way: that state is the only evidence a push leaves behind, since it deals no damage.
        CombatTracker.CreditPushInterrupt(__instance.TryCast<Dam_EnemyDamageBase>(), data.source);
    }
    [HarmonyPostfix]
    private static void Postfix(Dam_SyncedDamageBase __instance, DamageSample __state) => CombatTracker.RecordDamage(__instance, __state);
    [HarmonyFinalizer]
    private static Exception Finalizer(Dam_SyncedDamageBase __instance, DamageSample __state, Exception __exception)
    { CombatTracker.CompleteDamage(__instance, __state, __exception); return __exception; }
}

[HarmonyPatch(typeof(GameStateManager), "DoChangeState")]
internal static class ExpeditionStatePatch
{
    [HarmonyPrefix]
    private static void Prefix(eGameStateName nextState) => CombatTracker.ObserveState(nextState);
}

// The same limb, entered from the other side: this machine applying a blow of its own. A machine is not
// sent its own damage back - the host applies every blow and the others are told the result - so on a
// client whose host runs no panel of its own these two hooks are the only place its own blow is ever
// seen. They are what puts a client's own damage back on its own screen, and with it its own share and
// its own kills. `NoteLocalOutgoing` refuses every case where the blow is already accounted for, so
// nothing is read twice here.
[HarmonyPatch]
internal static class LocalBulletDamagePatch
{
    private static IEnumerable<MethodBase> TargetMethods() => ImpactTargets.Methods(typeof(Dam_EnemyDamageLimb), "BulletDamage");

    [HarmonyPrefix]
    private static void Prefix(Dam_EnemyDamageLimb __instance, float dam, Agent sourceAgent, uint gearCategoryId)
        => CombatTracker.NoteLocalOutgoing(__instance, sourceAgent, dam, DamageKind.Bullet, gearCategoryId);
}

// Melee travels its own method and carries the same two parameters that matter, so it gets its own hook.
[HarmonyPatch]
internal static class LocalMeleeDamagePatch
{
    private static IEnumerable<MethodBase> TargetMethods() => ImpactTargets.Methods(typeof(Dam_EnemyDamageLimb), "MeleeDamage");

    [HarmonyPrefix]
    private static void Prefix(Dam_EnemyDamageLimb __instance, float dam, Agent sourceAgent, uint gearCategoryId)
        => CombatTracker.NoteLocalOutgoing(__instance, sourceAgent, dam, DamageKind.Melee, gearCategoryId);
}

// Explosive damage, which this build had no hook for at all until a modded plasma gun showed 0/0/0 while it
// was plainly being fired. It arrives on its own method and its own payload, so a bullet hook never sees it.
// The payload carries no field naming who set it off - that is why `RecordExplosion` attributes the blow
// through the attack cache, the same way StatDisplay attributes its own explosion damage.
[HarmonyPatch]
internal static class ExplosionDamagePatch
{
    private static IEnumerable<MethodBase> TargetMethods() => DamageTargets.Methods("ReceiveExplosionDamage");

    [HarmonyPrefix]
    private static void Prefix(Dam_SyncedDamageBase __instance, out DamageSample __state) => __state = CombatTracker.CaptureExplosion(__instance);

    [HarmonyPostfix]
    private static void Postfix(Dam_SyncedDamageBase __instance, DamageSample __state)
        => CombatTracker.RecordExplosion(__instance, __state.HealthBefore);
    [HarmonyFinalizer]
    private static Exception Finalizer(Dam_SyncedDamageBase __instance, DamageSample __state, Exception __exception)
    { CombatTracker.CompleteDamage(__instance, __state, __exception); return __exception; }
}
