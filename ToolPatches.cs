using System;
using Agents;
using Enemies;
using Gear;
using HarmonyLib;
using LevelGeneration;
using Player;

namespace HawChow.GTFOCombatPanel;

// The master firing path is authoritative. FireBullet is called once per sentry round and is the only
// stream that knows `doDamage`, so its prefix opens the scope token the turret's own OnBulletFired
// notification is paired against, while the surrounding UpdateFireMaster scope lets the damage patches
// bind the hit and measured damage to it. The finalizer closes that scope even when the round throws, so
// a failed call can never leave a scope open that would swallow the later notifications of its turret.
[HarmonyPatch(typeof(SentryGunInstance_Firing_Bullets), "FireBullet")]
internal static class SentryShotPatch
{
    [HarmonyPrefix]
    private static void Prefix(SentryGunInstance_Firing_Bullets __instance, bool doDamage, out SentryShotScope __state)
        => __state = ToolStatsTracker.BeginSentryShot(__instance, doDamage);

    [HarmonyFinalizer]
    private static Exception Finalizer(Exception __exception, SentryShotScope __state)
    {
        ToolStatsTracker.EndSentryShot(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(GlueGunProjectile), "Fire")]
internal static class GlueShotPatch
{
    [HarmonyPostfix]
    private static void Postfix(GlueGunProjectile __instance, PlayerAgent owner) => ToolStatsTracker.CreditGlueProjectile(__instance, owner);
}

// SyncDestroy is the native projectile end-of-life callback; erase only this projectile's provenance and per-shot identities.
[HarmonyPatch(typeof(GlueGunProjectile), "SyncDestroy")]
internal static class GlueProjectileDestroyPatch
{
    [HarmonyPostfix]
    private static void Postfix(GlueGunProjectile __instance) => ToolStatsTracker.ForgetGlueProjectile(__instance);
}

// This enemy-only callback ignores walls and counts one successful projectile only once.
// Multiple enemies can still be counted individually by the glued-enemy metric.
[HarmonyPatch(typeof(Dam_EnemyDamageBase), "AddToTotalGlueVolume")]
internal static class GlueEnemyContactPatch
{
    [HarmonyPostfix]
    private static void Postfix(Dam_EnemyDamageBase __instance, GlueGunProjectile proj)
        => ToolStatsTracker.CreditGlueContact(__instance, proj);

}

// Door glue and its later impact share the actual damage-limb identity.
[HarmonyPatch(typeof(LG_WeakDoorBladeDamage), "AddGlue")]
internal static class GlueDoorApplicationPatch
{
    [HarmonyPostfix]
    private static void Postfix(LG_WeakDoorBladeDamage __instance, GlueGunProjectile proj)
        => ToolStatsTracker.CreditGlueDoorApplication(__instance, proj);
}

[HarmonyPatch(typeof(LG_WeakDoorBladeDamage), "ClearGlue")]
internal static class GlueDoorClearPatch
{
    [HarmonyPostfix]
    private static void Postfix(LG_WeakDoorBladeDamage __instance)
        => ToolStatsTracker.ForgetGlueDoor(__instance);
}

// Capture glue before the hit is resolved so a breakthrough cannot erase the evidence.

[HarmonyPatch(typeof(LG_WeakDoorBladeDamage), "MeleeDamage")]
internal static class GluedDoorMeleeHitPatch
{
    [HarmonyPrefix]
    private static void Prefix(LG_WeakDoorBladeDamage __instance, Agent sourceAgent, out GlueDoorHitSample __state)
        => __state = ToolStatsTracker.CaptureGlueDoorHit(__instance, sourceAgent);

    [HarmonyPostfix]
    private static void Postfix(GlueDoorHitSample __state)
        => ToolStatsTracker.CreditGlueDoorHit(__state);
}

// Keep the placement callback when it fires; the synced item OnSpawn patch covers the core-owned spawn path too.
[HarmonyPatch(typeof(MineDeployerFirstPerson), "OnStickyMineSpawned")]
internal static class MineEquipmentDeploymentPatch
{
    [HarmonyPostfix]
    private static void Postfix(MineDeployerFirstPerson __instance, ISyncedItem item, PlayerAgent sourceAgent)
        => ToolStatsTracker.CreditMineDeployment(__instance, item, sourceAgent);
}

[HarmonyPatch(typeof(MineDeployerFirstPerson), "PlaceMine")]
internal static class MinePlaceActionPatch
{
    [HarmonyPostfix]
    private static void Postfix(MineDeployerFirstPerson __instance)
        => ToolStatsTracker.ReportMinePlaceAction(__instance);
}

[HarmonyPatch(typeof(MineDeployerInstance), "OnSpawn")]
internal static class MineInstanceSpawnPatch
{
    [HarmonyPostfix]
    private static void Postfix(MineDeployerInstance __instance)
        => ToolStatsTracker.CreditMineInstanceSpawn(__instance);
}

[HarmonyPatch(typeof(MineDeployerInstance), "SyncedTrigger")]
internal static class MineTriggerPatch
{
    [HarmonyPostfix]
    private static void Postfix(MineDeployerInstance __instance)
        => ToolStatsTracker.CreditMineTrigger(__instance);
}

// PickUp and SyncedPickup may both run for one returned mine. The ledger removes that deployed instance
// on the first callback, so only equipment mines tracked at spawn reduce effective deployments.
[HarmonyPatch(typeof(MineDeployerInstance), "PickUp")]
internal static class MinePickupPatch
{
    [HarmonyPrefix]
    private static void Prefix(MineDeployerInstance __instance)
        => ToolStatsTracker.CreditMinePickup(__instance);
}

[HarmonyPatch(typeof(MineDeployerInstance), "SyncedPickup")]
internal static class MineSyncedPickupPatch
{
    [HarmonyPrefix]
    private static void Prefix(MineDeployerInstance __instance)
        => ToolStatsTracker.CreditMinePickup(__instance);
}

[HarmonyPatch(typeof(MineDeployerInstance), "OnDestroy")]
internal static class MineDestroyedPatch
{
    [HarmonyPostfix]
    private static void Postfix(MineDeployerInstance __instance)
        => ToolStatsTracker.ForgetMine(__instance);
}

// DoExplode is the exact synchronous damage window for this mine, and m_core is the spawned mine's
// authority object. The tracker only opens the window when that instance came from non-consumable equipment.
[HarmonyPatch(typeof(MineDeployerInstance_Detonate_Explosive), "DoExplode")]
internal static class MineExplosionAttributionPatch
{
    [HarmonyPrefix]
    private static void Prefix(MineDeployerInstance_Detonate_Explosive __instance, out MineBlastScope __state)
        => __state = ToolStatsTracker.BeginMineBlast(__instance);

    [HarmonyFinalizer]
    private static Exception Finalizer(Exception __exception, MineBlastScope __state)
    {
        ToolStatsTracker.EndMineBlast(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(SentryGunInstance_Firing_Bullets), "Setup")]
internal static class SentryFiringEventPatch
{
    [HarmonyPostfix]
    private static void Postfix(SentryGunInstance_Firing_Bullets __instance) => ToolStatsTracker.SetupSentry(__instance);
}

[HarmonyPatch(typeof(MineDeployerInstance_Detonate_Explosive), "Setup")]
internal static class MineCoreSetupPatch
{
    [HarmonyPostfix]
    private static void Postfix(MineDeployerInstance_Detonate_Explosive __instance)
        => ToolStatsTracker.CreditMineCoreReady(__instance);
}
