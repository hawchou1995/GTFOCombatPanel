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
        // A method the game does not have is skipped, never yielded as null: Harmony's patch processor throws
        // on a null target, Plugin.PatchAll does not guard the call, and a throw there costs every patch class
        // that comes after it - which is how one renamed game method silently empties the whole panel.
        MethodBase enemy = AccessTools.DeclaredMethod(typeof(Dam_EnemyDamageBase), name);
        if (enemy != null) yield return enemy;
        MethodBase player = AccessTools.DeclaredMethod(typeof(Dam_PlayerDamageBase), name);
        if (player != null) yield return player;
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

    // What the blow is worth is read as the health the enemy actually lost whenever that loss is
    // readable. The `dam` argument is the figure the weapon carries before the game applies the armour of
    // the limb that was struck and the precision multiplier of the hit, so a shot that landed on a
    // weakspot was recorded as though every hit were the same - which is exactly why a precise primary
    // could read lower than the same rifle fired into a body. The health before the call and after it is
    // the applied damage, the same measurement the receive path above takes from its own packet.
    //
    // `dam` is handed over anyway, for the one case the two reads cannot differ: a client whose host
    // applies the blow is told the result later, so its own call can return the health unchanged, and the
    // weapon's carried figure is then all there is. `NoteLocalOutgoing` reads it only on that path, capped
    // by the health the limb had left, and still marks the row as an estimate.
    //
    // A finalizer rather than a postfix, so the figure is read exactly once on the path that returns, and a
    // fault in this mod never replaces what the game was going to do. A call that threw is not measured at
    // all: its health readings say nothing about a blow that never landed, so the carried figure would be
    // damage invented by a game-side exception.
    [HarmonyPrefix]
    private static void Prefix(Dam_EnemyDamageLimb __instance, out float __state)
        => __state = CombatTracker.LimbHealth(__instance);

    [HarmonyFinalizer]
    private static Exception Finalizer(Dam_EnemyDamageLimb __instance, Agent sourceAgent, uint gearCategoryId,
        float dam, float __state, Exception __exception)
    {
        if (__exception == null)
            CombatTracker.NoteLocalOutgoing(__instance, sourceAgent, __state, DamageKind.Bullet, gearCategoryId, dam);
        return __exception;
    }
}

// Melee travels its own method and carries the same parameters that matter, so it gets its own hook.
// The amount is read the same way: a backstab, a weakspot blow and a sleeper multiplier all live between
// the argument and the health the enemy lost, so the argument is never the figure printed - it is only
// the fallback for a call that moved no health here.
[HarmonyPatch]
internal static class LocalMeleeDamagePatch
{
    private static IEnumerable<MethodBase> TargetMethods() => ImpactTargets.Methods(typeof(Dam_EnemyDamageLimb), "MeleeDamage");

    [HarmonyPrefix]
    private static void Prefix(Dam_EnemyDamageLimb __instance, out float __state)
        => __state = CombatTracker.LimbHealth(__instance);

    // Same guard as the bullet hook: a game call that threw measured nothing and is left unrecorded.
    [HarmonyFinalizer]
    private static Exception Finalizer(Dam_EnemyDamageLimb __instance, Agent sourceAgent, uint gearCategoryId,
        float dam, float __state, Exception __exception)
    {
        if (__exception == null)
            CombatTracker.NoteLocalOutgoing(__instance, sourceAgent, __state, DamageKind.Melee, gearCategoryId, dam);
        return __exception;
    }
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
