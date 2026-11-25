using System;
using System.Collections.Generic;
using Gear;
using HarmonyLib;
using Player;

namespace HawChow.GTFOCombatPanel;

// 1.19.41: the pack-application bracket.
//
// A medical pack is consumed by one native call - `Gear.ResourcePackFirstPerson.ApplyPack()` (instance) for a
// human, `ResourcePackFirstPerson.ApplyPackBot(PlayerAgent ownerAgent, PlayerAgent receiverAgent,
// ItemEquippable resourceItem)` (static) for a bot - and the health it hands out arrives as
// `PlayerAgent.GiveHealth(PlayerAgent giverAgent, float amountRel)` inside that call. Nothing else names a pack:
// `ReceiveAddHealth` cannot tell a medkit from a syringe, a disinfection pack or another mod's trickle heal, and
// that is what made one expedition report 1319 medical packs. So the bracket is opened on the way in, the gives
// inside it are counted once per receiver, and the bracket is closed on the way out.
//
// Every signature below was read out of the installed profile's own interop
// (`BepInEx/interop/Modules-ASM.dll`): ApplyPack is an instance method
// with no parameters, ApplyPackBot is *static* with three, and GiveHealth/GiveDisinfection are instance methods
// with `(giverAgent, amountRel)`. They are bound statically - no `TypeByName`, no field/parameter reflection - and
// installed explicitly so a missing target is one diagnostic line instead of a failed plugin load. Nothing is
// written to the game: every prefix only reads arguments it is already inside.
internal static class PackApplicationPatches
{
    internal static bool HumanApplyInstalled { get; private set; }
    internal static bool BotApplyInstalled { get; private set; }
    internal static bool HealthGiveInstalled { get; private set; }
    internal static bool DisinfectionInstalled { get; private set; }
    internal static int Resolved => (HumanApplyInstalled ? 1 : 0) + (BotApplyInstalled ? 1 : 0)
        + (HealthGiveInstalled ? 1 : 0) + (DisinfectionInstalled ? 1 : 0);
    internal static string Problem { get; private set; } = "not installed";

    // A pack application without the health give cannot be counted: every bracket would close empty for the wrong
    // reason, so the medical figure stays unknown instead of becoming a counted zero.
    internal static bool MedicalPacksCountable => HumanApplyInstalled && HealthGiveInstalled;

    internal static void Install(Harmony harmony)
    {
        if (harmony == null) { Problem = "no Harmony instance"; return; }
        var problems = new List<string>(4);
        HumanApplyInstalled = Patch(harmony, AccessTools.Method(typeof(ResourcePackFirstPerson), nameof(ResourcePackFirstPerson.ApplyPack)),
            nameof(ApplyPackPrefix), nameof(ApplyFinalizer), "ResourcePackFirstPerson.ApplyPack", problems);
        BotApplyInstalled = Patch(harmony, AccessTools.Method(typeof(ResourcePackFirstPerson), nameof(ResourcePackFirstPerson.ApplyPackBot)),
            nameof(ApplyPackBotPrefix), nameof(ApplyFinalizer), "ResourcePackFirstPerson.ApplyPackBot", problems);
        HealthGiveInstalled = Patch(harmony, AccessTools.Method(typeof(PlayerAgent), nameof(PlayerAgent.GiveHealth)),
            nameof(HealthGivePrefix), null, "PlayerAgent.GiveHealth", problems);
        DisinfectionInstalled = Patch(harmony, AccessTools.Method(typeof(PlayerAgent), nameof(PlayerAgent.GiveDisinfection)),
            nameof(DisinfectionPrefix), null, "PlayerAgent.GiveDisinfection", problems);

        Problem = problems.Count == 0 ? "all four native entry points resolved" : string.Join("; ", problems);
        Plugin.Logger?.LogInfo("[DIAG] pack application contract: resolved=" + Resolved + "/4; humanApply=" + HumanApplyInstalled
            + ", botApply=" + BotApplyInstalled + ", giveHealth=" + HealthGiveInstalled + ", disinfection=" + DisinfectionInstalled
            + "; " + Problem
            + (MedicalPacksCountable ? string.Empty : "; a medical pack count that depends on a missing entry point stays unknown rather than 0"));
    }

    private static bool Patch(Harmony harmony, System.Reflection.MethodBase target, string prefix, string finalizer, string label, List<string> problems)
    {
        if (target == null) { problems.Add(label + " is not in this build"); return false; }
        try
        {
            System.Reflection.MethodInfo prefixMethod = AccessTools.Method(typeof(PackApplicationPatches), prefix);
            System.Reflection.MethodInfo finalizerMethod = finalizer == null ? null : AccessTools.Method(typeof(PackApplicationPatches), finalizer);
            // NAMED ARGUMENTS, DELIBERATELY. `Harmony.Patch`'s positions are (original, prefix, postfix,
            // transpiler, finalizer[, ilmanipulator]): the third positional argument is the *postfix* and the
            // fourth is the *transpiler*. This call used to read `Patch(target, prefix, null, finalizer)`, which
            // handed a `(PackBracket __state, Exception __exception)` finalizer to the transpiler slot. HarmonyX
            // then failed the patch inside `ApplyTranspilers` with
            // `System.Reflection.TargetParameterCountException: Parameter count mismatch` - the recorded Survival
            // run's two `[Error : HarmonyX] Failed to patch ... ResourcePackFirstPerson::ApplyPack` lines and its
            // `resolved=2/4; humanApply=False, botApply=False`. Named arguments make the slot the compiler's
            // problem instead of a positional guess a future edit can silently re-break.
            harmony.Patch(target,
                prefix: prefixMethod == null ? null : new HarmonyMethod(prefixMethod),
                finalizer: finalizerMethod == null ? null : new HarmonyMethod(finalizerMethod));
            return true;
        }
        catch (Exception error) { problems.Add(label + " could not be patched: " + error.Message); return false; }
    }

    // ---- the patches ------------------------------------------------------------------------------------

    private static void ApplyPackPrefix(ResourcePackFirstPerson __instance, ref PackBracket __state)
    {
        __state = null;
        try { __state = SupplyTracker.BeginApply(__instance != null ? __instance.Owner : null, false); }
        catch (Exception error) { SupplyTracker.NoteDiagnostic("pack-apply-prefix", error); }
    }

    // Static entry point: the owner is the first argument, and the bot's own receiver is the second.
    private static void ApplyPackBotPrefix(PlayerAgent ownerAgent, PlayerAgent receiverAgent, ref PackBracket __state)
    {
        __state = null;
        try { __state = SupplyTracker.BeginApply(ownerAgent, true); }
        catch (Exception error) { SupplyTracker.NoteDiagnostic("pack-apply-bot-prefix", error); }
    }

    private static Exception ApplyFinalizer(PackBracket __state, Exception __exception)
    {
        try { SupplyTracker.EndApply(__state, __exception); }
        catch (Exception error) { SupplyTracker.NoteDiagnostic("pack-apply-finalizer", error); }
        return __exception;
    }

    // The receiver is the agent the call landed on, the giver is `giverAgent`, and the second parameter is the
    // relative amount - exactly the interop signature the profile ships.
    private static void HealthGivePrefix(PlayerAgent __instance, PlayerAgent giverAgent, float amountRel)
    {
        try { SupplyTracker.NoteHealthGive(SupplyTracker.Applications.Current, __instance, giverAgent, amountRel); }
        catch (Exception error) { SupplyTracker.NoteDiagnostic("pack-give-prefix", error); }
    }

    // Disinfection shares the medical slot and its denominator and is not a health pack: it is recorded as an
    // excluded give, so the exclusion is a number in the ledger rather than a comment beside the patch.
    private static void DisinfectionPrefix(PlayerAgent __instance, float amountRel)
    {
        try { SupplyTracker.NoteExcludedGive(__instance, amountRel); }
        catch (Exception error) { SupplyTracker.NoteDiagnostic("pack-excluded-prefix", error); }
    }
}
