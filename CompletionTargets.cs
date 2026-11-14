using CellMenu;
using HarmonyLib;
using Il2CppSystem.Collections;
using UnityEngine;

namespace HawChow.GTFOCombatPanel;

// Native BlinkIn uses SetActiveBlink, not an alpha tween. Observe its actual completion.
internal static class CompletionTargets
{
    internal static readonly BlinkCompletionLedger Ledger = new BlinkCompletionLedger();

    // 1.19.42（独立评审 F2）：这一页已经为「某个可选持有者没有出现」打印过一次诊断。
    private static bool _skipReported;

    internal static void BeginPage()
    {
        Ledger.Clear();
        _skipReported = false;
    }

    // 一个目标只在这份构建真的把它交给这一页时才进入必需集合。
    //
    // 1.19.42（独立评审 F2）：`Require(icon.m_bgHolder)` / `Require(icon.m_skullHolder)` 过去对 null 直接调
    // `Ledger.Require(0, ...)`，而账本把 0 记成 `_missing`——于是这一页的整张门（`ResultRevealGate`、
    // `ResultSettlementGate`、`__missing` 派生的一切）被一个「这份构建里没有这个持有者」的事实静默关掉：reveal 门
    // 再也不会拦住任何东西。缺一个可选持有者不是「这一页的 reveal 不可能完成」，它只是「这个目标不属于这一页」，
    // 所以跳过它；只有账本自己的预算（>64 个目标）或明确不可恢复的情形才置 `_missing`。
    internal static void Require(GameObject target, bool name)
    {
        if (target == null) return;
        Ledger.Require(target.Pointer.ToInt64(), name);
    }

    internal static void Require(CM_ExpeditionSectorIcon icon)
    {
        if (icon == null) return;
        RequireOptional(icon, icon.m_bgHolder, false, "m_bgHolder");
        RequireOptional(icon, icon.m_skullHolder, false, "m_skullHolder");
        if (icon.m_titleVisible) RequireOptional(icon, icon.m_title != null ? icon.m_title.gameObject : null, true, "m_title");
    }

    // 跳过是可选的、可记录的，但从不致命：哪个图标、哪个字段，每页打印一次。
    private static void RequireOptional(CM_ExpeditionSectorIcon icon, GameObject target, bool name, string field)
    {
        if (target != null)
        {
            Ledger.Require(target.Pointer.ToInt64(), name);
            return;
        }
        if (_skipReported) return;
        _skipReported = true;
        Plugin.Logger?.LogWarning("[DIAG] completion target skipped: field=" + field
            + ", icon=" + icon.Pointer.ToInt64() + ", name=" + name.ToString().ToLowerInvariant()
            + ", pageTargets=" + Ledger.Count
            + "; this build's sector icon hands over no such holder, so this target is not part of the page's reveal. It is skipped rather than "
            + "marking the ledger impossible: 'this build has no such object' is not 'this page's reveal can never finish'.");
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
