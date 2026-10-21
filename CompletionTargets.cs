using CellMenu;
using HarmonyLib;
using Il2CppSystem.Collections;
using UnityEngine;

namespace HawChow.GTFOCombatPanel;

// Native BlinkIn uses SetActiveBlink, not an alpha tween. Observe its actual completion.
internal static class CompletionTargets
{
    internal static readonly BlinkCompletionLedger Ledger = new BlinkCompletionLedger();
    internal static void BeginPage() => Ledger.Clear();

    internal static void Require(GameObject target, bool name)
        => Ledger.Require(target != null ? target.Pointer.ToInt64() : 0, name);

    internal static void Require(CM_ExpeditionSectorIcon icon)
    {
        Require(icon.m_bgHolder, false);
        Require(icon.m_skullHolder, false);
        if (icon.m_titleVisible) Require(icon.m_title != null ? icon.m_title.gameObject : null, true);
    }
}

[HarmonyPatch(typeof(CM_ExpeditionSectorIcon), nameof(CM_ExpeditionSectorIcon.BlinkIn))]
internal static class SectorRevealStartedPatch
{
    [HarmonyPrefix, HarmonyWrapSafe]
    private static void Prefix(CM_ExpeditionSectorIcon __instance)
    {
        // Only icons belonging to this success page enter the required set; inactive optional
        // prefab instances that the game never schedules cannot hold the board forever.
        if (ResultsDisplay.Success != null && __instance.transform.IsChildOf(ResultsDisplay.Success.transform))
            CompletionTargets.Require(__instance);
    }
}

[HarmonyPatch(typeof(CoroutineManager), nameof(CoroutineManager.SetActiveBlink))]
internal static class SectorBlinkRoutinePatch
{
    [HarmonyPostfix, HarmonyWrapSafe]
    private static void Postfix(GameObject go, IEnumerator __result)
    {
        if (go != null && __result != null)
            CompletionTargets.Ledger.Start(go.Pointer.ToInt64(), __result.Pointer.ToInt64());
    }
}

[HarmonyPatch(typeof(CoroutineManager._SetActiveBlink_d__35), nameof(CoroutineManager._SetActiveBlink_d__35.MoveNext))]
internal static class SectorBlinkFinishedPatch
{
    [HarmonyPostfix, HarmonyWrapSafe]
    private static void Postfix(CoroutineManager._SetActiveBlink_d__35 __instance, bool __result)
    {
        if (__result) return;
        GameObject go = __instance.go;
        if (go == null) return;
        // MoveNext also returns false on an aborted/destroyed target. The native loop's t
        // must have reached BlinkDuration and the final state must actually be active.
        bool finished = __instance.endActive && !__instance.destroyOnEnd && go.activeInHierarchy
            && float.IsFinite(__instance._t_5__2) && __instance._t_5__2 >= CoroutineManager.BlinkDuration;
        CompletionTargets.Ledger.Finish(go.Pointer.ToInt64(), __instance.Pointer.ToInt64(), finished);
    }
}
