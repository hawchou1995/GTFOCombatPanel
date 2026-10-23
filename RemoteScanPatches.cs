using System;
using Enemies;
using HarmonyLib;
using SNetwork;

namespace HawChow.GTFOCombatPanel;

// LastSender alone is stale outside packet dispatch. A thread-local receive scope is required;
// nothing here consumes a packet or changes its data, and forwarding clients never guess an owner.
[HarmonyPatch(typeof(SNet_Replication), "RecieveBytes")]
internal static class ScanReceiveScopePatch
{
    [ThreadStatic] internal static int Depth;
    [HarmonyPrefix]
    private static void Prefix(out int __state) { __state = Depth; Depth++; }
    [HarmonyFinalizer]
    private static Exception Finalizer(int __state, Exception __exception) { Depth = __state; return __exception; }
}

[HarmonyPatch(typeof(EnemyAgent), "SyncPlaceNavMarkerTag")]
internal static class RemoteScanTagPatch
{
    [HarmonyPostfix]
    private static void Postfix(EnemyAgent __instance)
    {
        try
        {
            if (ScanReceiveScopePatch.Depth == 0 || !SNet.IsMaster || __instance == null || !__instance.IsTagged) return;
            if (!SNet.Replication.TryGetLastSender(out SNet_Player sender) || sender == null || sender.IsLocal || sender.IsBot) return;
            CombatTracker.CreditRemoteScanTag(__instance, sender);
        }
        catch (Exception error) { CombatTracker.LogToolError("remote scan timer", error); }
    }
}
