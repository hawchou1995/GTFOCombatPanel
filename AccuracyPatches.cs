using System;
using System.Collections.Generic;
using System.Reflection;
using Agents;
using Gear;
using HarmonyLib;
using UnityEngine;

namespace HawChow.GTFOCombatPanel;

[HarmonyPatch]
internal static class WeaponFirePatch
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (Type type in new[] { typeof(BulletWeapon), typeof(Shotgun), typeof(BulletWeaponSynced), typeof(ShotgunSynced) })
            yield return AccessTools.DeclaredMethod(type, "Fire", new[] { typeof(bool) });
    }
    [HarmonyPrefix]
    private static void Prefix(BulletWeapon __instance, out FireCapture __state) => __state = CombatTracker.BeginFire(__instance);
    [HarmonyPostfix]
    private static void Postfix(FireCapture __state) => CombatTracker.EndFire(__state, true);
    [HarmonyFinalizer]
    private static void Finalizer(Exception __exception, FireCapture __state)
    {
        if (__exception != null) CombatTracker.EndFire(__state, false);
    }
}

[HarmonyPatch]
internal static class ProjectileRayPatch
{
    internal static MethodBase TargetMethod() => AccessTools.Method(typeof(Weapon), "CastWeaponRay",
        new[] { typeof(Transform), typeof(Weapon.WeaponHitData).MakeByRefType(), typeof(Vector3), typeof(int) });
    [HarmonyPostfix]
    private static void Postfix(Weapon.WeaponHitData weaponRayData) => CombatTracker.OnWeaponRay(weaponRayData);
}

[HarmonyPatch(typeof(Dam_EnemyDamageLimb), "BulletDamage")]
internal static class EnemyPelletContactPatch
{
    [HarmonyPostfix]
    private static void Postfix(Dam_EnemyDamageLimb __instance, Agent sourceAgent) => CombatTracker.OnEnemyLimbHit(__instance, sourceAgent);
}

[HarmonyPatch(typeof(PlayerInventorySynced), "GetSync")]
internal static class RemoteShotsPatch
{
    [HarmonyPrefix]
    private static void Prefix(PlayerInventorySynced __instance) => CombatTracker.OnRemoteSync(__instance);
}

[HarmonyPatch(typeof(SentryGunInstance_Firing_Bullets), "UpdateFireMaster")]
internal static class SentryDamageSourcePatch
{
    [HarmonyPrefix]
    private static void Prefix(SentryGunInstance_Firing_Bullets __instance, out SentryScope __state)
        => __state = ToolStatsTracker.BeginSentry(__instance);
    [HarmonyFinalizer]
    private static void Finalizer(SentryScope __state) => ToolStatsTracker.EndSentry(__state);
}
