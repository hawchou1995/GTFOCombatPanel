using System.Collections.Generic;
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

    // 1.19.43：这一页已经为「交给 BlinkIn 却没有拿到协程的目标」打印过一次诊断。
    private static bool _scheduleReported;

    // 这一次 `BlinkIn` 调用登记下来的全部目标。`BlinkIn` 在**调用内部**就排出它的 `SetActiveBlink` 协程，所以它返回时
    // 这次排程的窗口已经关闭：调用里没拿到协程的目标不是「还没轮到」，而是「这份构建的这次调用根本不会展示它」。
    private static readonly List<long> Window = new List<long>(8);

    internal static void BeginPage()
    {
        Ledger.Clear();
        _skipReported = false;
        _scheduleReported = false;
        Window.Clear();
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
        long pointer = target.Pointer.ToInt64();
        Ledger.Require(pointer, name);
        Window.Add(pointer);
    }

    // 一次原生调用开始登记自己的目标。
    internal static void BeginCall() => Window.Clear();

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
            Require(target, name);
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

    // 一次 `BlinkIn` 调用排完它自己的协程之后关窗：调用里没拿到协程的目标不计入这一页等待的 reveal。
    //
    // 1.19.43：此前只有 `Require`，没有任何东西能关闭这个窗口。一个没有对应回调的持有者（例如这份构建里
    // `BlinkIn` 并不交给 `SetActiveBlink` 的 m_skullHolder）因此永远 `Done=false`，`Ready` 永远为假、
    // `MayStillComplete` 永远为真 —— 成功页的首次落板被永久挡住，只有 F8 能出来。现在按**原生事实**关窗：
    // 不是帧数、不是 alpha、不是空扫描。之后若真来了协程，`BlinkCompletionLedger.Start` 会把目标重新计入，
    // 所以迟到的原生控件既不会被忽略，也不会被当成「已经完成」。
    internal static void SchedulesClosed()
    {
        int skipped = Ledger.CloseSchedule(Window);
        Window.Clear();
        if (skipped <= 0 || _scheduleReported) return;
        _scheduleReported = true;
        Plugin.Logger?.LogInfo("[DIAG] completion target not scheduled: " + skipped
            + " target(s) a sector BlinkIn handed over received no SetActiveBlink routine inside that same call (required="
            + Ledger.Required + ", counted=" + Ledger.Count + ", unscheduled=" + Ledger.Unscheduled + "); they are left out of the reveal this "
            + "page waits on, because a routine that was never scheduled has no completion to observe and waiting for it would never answer. "
            + "A routine that arrives later re-admits its own target.");
    }
}

[HarmonyPatch(typeof(CM_ExpeditionSectorIcon), nameof(CM_ExpeditionSectorIcon.BlinkIn))]
internal static class SectorRevealStartedPatch
{
    [HarmonyPrefix, HarmonyWrapSafe]
    private static void Prefix(CM_ExpeditionSectorIcon __instance)
    {
        // Every registration below belongs to this one native call, so the roster starts empty here.
        CompletionTargets.BeginCall();
        // Only icons belonging to this success page enter the required set; inactive optional
        // prefab instances that the game never schedules cannot hold the board forever.
        if (ResultsDisplay.Success != null && __instance.transform.IsChildOf(ResultsDisplay.Success.transform))
            CompletionTargets.Require(__instance);
    }

    // The call is over: whatever it did not hand a routine to is not part of this page's reveal.
    [HarmonyPostfix, HarmonyWrapSafe]
    private static void Postfix()
    {
        CompletionTargets.SchedulesClosed();
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
        // MoveNext also returns false on an aborted/destroyed target. The native loop's t
        // must have reached BlinkDuration and the final state must actually be active.
        bool finished = go != null && __instance.endActive && !__instance.destroyOnEnd && go.activeInHierarchy
            && float.IsFinite(__instance._t_5__2) && __instance._t_5__2 >= CoroutineManager.BlinkDuration;
        if (go != null)
        {
            CompletionTargets.Ledger.Finish(go.Pointer.ToInt64(), __instance.Pointer.ToInt64(), finished);
            return;
        }
        // The target this routine was revealing is already gone, so it can no longer be named by it. The routine
        // pointer still names the row: a routine that ended has ended, and leaving that row "in flight" is how a
        // destroyed icon used to keep the success page waiting for a callback that will never come again.
        CompletionTargets.Ledger.FinishRoutine(__instance.Pointer.ToInt64(), finished);
    }
}
