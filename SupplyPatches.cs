using System;
using HarmonyLib;
using Player;

namespace HawChow.GTFOCombatPanel;

[HarmonyPatch(typeof(PlayerAgent), nameof(PlayerAgent.GiveAmmoRel))]
internal static class SupplyAmmoSenderPatch
{
    [HarmonyPrefix]
    private static void Prefix(PlayerAgent __instance, PlayerAgent giverAgent, out SupplyTracker.AmmoScope __state)
        => __state = SupplyTracker.BeginAmmo(__instance, giverAgent);

    [HarmonyFinalizer]
    private static Exception Finalizer(SupplyTracker.AmmoScope __state, Exception __exception)
    {
        SupplyTracker.EndAmmo(__state, __exception);
        return __exception;
    }
}

[HarmonyPatch(typeof(PlayerBackpackManager), nameof(PlayerBackpackManager.ReceiveAmmoGive))]
internal static class SupplyAmmoReceiverPatch
{
    [HarmonyPrefix]
    private static void Prefix(pAmmoGive data, out SupplyTracker.AmmoScope __state)
        => __state = SupplyTracker.BeginAmmoPacket(data);

    [HarmonyFinalizer]
    private static Exception Finalizer(SupplyTracker.AmmoScope __state, Exception __exception)
    {
        SupplyTracker.EndAmmo(__state, __exception);
        return __exception;
    }
}

[HarmonyPatch(typeof(PlayerAmmoStorage), nameof(PlayerAmmoStorage.PickupAmmo))]
internal static class SupplyNativeAmmoPatch
{
    [HarmonyPrefix, HarmonyPriority(Priority.Last)]
    private static void Prefix(PlayerAmmoStorage __instance, AmmoType ammoType, float ammoAmount, out SupplyTracker.AmmoChange __state)
        => __state = SupplyTracker.CaptureAmmo(__instance, ammoType, ammoAmount);

    [HarmonyPostfix, HarmonyPriority(Priority.First)]
    private static void Postfix(SupplyTracker.AmmoChange __state) => SupplyTracker.CompleteAmmo(__state);
}

[HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveAddHealth))]
internal static class SupplyMedicalReceiverPatch
{
    [HarmonyPrefix, HarmonyPriority(Priority.Last)]
    private static void Prefix(Dam_PlayerDamageBase __instance, pAddHealthData data, out SupplyTracker.HealthChange __state)
        => __state = SupplyTracker.CaptureHealth(__instance, data);

    [HarmonyPostfix, HarmonyPriority(Priority.First)]
    private static void Postfix(Dam_PlayerDamageBase __instance, SupplyTracker.HealthChange __state)
        => SupplyTracker.CompleteHealth(__instance, __state);
}
