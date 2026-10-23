using System;
using System.Collections.Generic;
using System.Reflection;
using Agents;
using Enemies;
using Gear;
using HarmonyLib;
using Player;
using UnityEngine;

namespace HawChow.GTFOCombatPanel;

// Every game-side source the influence board scores that the damage and accuracy patches do not
// already hold: stagger, kills, revives, the supply a player hands out, and the packs they hand it
// out of. Each hook hands its numbers to CombatTracker, which owns the counters and the guards.
//
// The target of every patch is resolved by name and yielded only when it exists, so a game build
// that no longer carries one of these methods loses that one row instead of failing the plugin.
// The first time a hook fires it says so in the log, which is how a build whose game-side
// assumption turned out to be wrong reports itself.
internal static class ImpactTargets
{
    internal static IEnumerable<MethodBase> Methods(System.Type type, params string[] names)
    {
        foreach (string name in names)
        {
            MethodBase method = AccessTools.Method(type, name);
            if (method != null) yield return method;
        }
    }
}

// Stagger: damage that lands on an enemy at the instant it is attacking or charging an attack is
// what stops the attack. The game reports the hit react, and the enemy reports its own state.
[HarmonyPatch]
internal static class StaggerDamagePatch
{
    private static IEnumerable<MethodBase> TargetMethods() => ImpactTargets.Methods(typeof(Dam_EnemyDamageBase), "ProcessReceivedDamage");

    [HarmonyPrefix]
    private static void Prefix(Dam_EnemyDamageBase __instance, float damage, Agent damageSource, ES_HitreactType hitreact)
    {
        // Read the enemy under the blow before the game resolves it: a blow that kills a Scout leaves no
        // state behind to read afterwards, and this is the one place the enemy itself passes through.
        ScoutWitness.Refresh(__instance);
        CombatTracker.CreditStagger(__instance, damage, damageSource, hitreact);
    }
}

// A kill: the enemy's damage base resolves the death. The damage base also carries the enemy's own
// maximum health, which is what grades the kill, so a modded enemy is graded by what it is.
[HarmonyPatch]
internal static class EnemyDeathPatch
{
    private static IEnumerable<MethodBase> TargetMethods() => ImpactTargets.Methods(typeof(Dam_EnemyDamageBase), "InstantDead");

    [HarmonyPostfix]
    private static void Postfix(Dam_EnemyDamageBase __instance) => CombatTracker.CreditKill(__instance);
}

// The same death seen from the other side. Both hooks feed one de-duplicated ledger, so an enemy
// that trips both paths is still counted once.
[HarmonyPatch]
internal static class EnemyAgentDeathPatch
{
    private static IEnumerable<MethodBase> TargetMethods() => ImpactTargets.Methods(typeof(EnemyAgent), "OnDead");

    [HarmonyPostfix]
    private static void Postfix(EnemyAgent __instance) => CombatTracker.CreditKill(__instance);
}

// The revive interaction callback is local to the initiating machine and does not
// carry remote teammates' successful revives to the host. Capture the game's
// replicated source/target pair, then wait for the downed player's completion.
[HarmonyPatch]
internal static class ReviveStateSyncPatch
{
    private static IEnumerable<MethodBase> TargetMethods() => ImpactTargets.Methods(typeof(Interact_Revive), "SyncReviverState");

    [HarmonyPostfix]
    private static void Postfix(Interact_Revive.pPlayerReviveState __0) => CombatTracker.ObserveReviveState(__0);
}

[HarmonyPatch]
internal static class ReviveCompletionPatch
{
    private static IEnumerable<MethodBase> TargetMethods() => ImpactTargets.Methods(typeof(PLOC_Downed), "OnPlayerRevived");

    [HarmonyPostfix]
    private static void Postfix(PLOC_Downed __instance) => CombatTracker.ConfirmRevive(__instance);
}

[HarmonyPatch(typeof(EnemyAgent), nameof(EnemyAgent.Setup))]
internal static class EnemySpawnLedgerPatch
{
    [HarmonyPostfix]
    private static void Postfix(EnemyAgent __instance)
    {
        CombatTracker.ObserveEnemySpawn(__instance);
        // A pooled enemy is set up again here, and a Scout that screamed in its previous life must not
        // hand that latch to this one.
        ScoutWitness.Spawn(__instance);
    }
}

// ---- Scouts -----------------------------------------------------------------------------------
// A Scout has no hibernation state to read. It is unaware while it stands in `ScoutDetection`,
// sweeping with its tendrils, and awake from the moment it enters `ScoutScream`: the scream is what
// wakes the room, and it is the only thing that ends a Scout's sleep. Nothing below changes a game
// state - every witness is read from the object the hook already carries and handed to
// ScoutSleepPolicy, which the death ledger asks when a blow opens.
internal static class ScoutWitness
{
    private static bool _wired, _logged;

    // The frame is the generation a capture belongs to: one spawn runs several of these hooks and
    // their order inside a frame is not this mod's to choose, so a spawn's own reset must not discard
    // evidence that frame already produced.
    private static void Ready()
    {
        ScoutSleepPolicy.Now = Time.frameCount;
        if (_wired) return;
        _wired = true;
        if (Plugin.Logger != null) ScoutSleepPolicy.Note = Plugin.Logger.LogInfo;
    }

    // The death ledger keys a blow by the enemy's damage base pointer, so that is the identity a
    // capture is filed under.
    internal static IntPtr Enemy(EnemyAgent agent)
    {
        try { return agent?.Damage?.Pointer ?? IntPtr.Zero; }
        catch { return IntPtr.Zero; }
    }

    // A pooled enemy is set up again for its next life, and the previous occupant's scream latch
    // belongs to that life rather than to this one. Called after the ledger's own spawn reset, so the
    // two agree on which pointer now belongs to whom.
    internal static void Spawn(EnemyAgent agent)
    {
        try
        {
            Ready();
            IntPtr enemy = Enemy(agent);
            if (enemy == IntPtr.Zero) return;
            ScoutSleepPolicy.Spawn(enemy);
            if (IsScout(agent)) ScoutSleepPolicy.Scout(enemy);
        }
        catch (Exception error) { Log("the enemy's spawn state", error); }
    }

    // `ScoutDetection` being set up again is that Scout's own fresh start: unaware again, its tendrils
    // at whatever length the next sweep begins with.
    internal static void Fresh(ES_ScoutDetection state)
    {
        try
        {
            Ready();
            IntPtr enemy = Enemy(Owner(state));
            if (enemy == IntPtr.Zero) return;
            ScoutSleepPolicy.Spawn(enemy);
            ScoutSleepPolicy.Scout(enemy);
        }
        catch (Exception error) { Log("the Scout's detection setup", error); }
    }

    // Entering the detection state is the Scout standing there unaware. After a scream the game sends it
    // back here to keep looking, so this is a capture and never a reset.
    internal static void Dormant(ES_ScoutDetection state)
    {
        try
        {
            Ready();
            IntPtr enemy = Enemy(Owner(state));
            if (enemy != IntPtr.Zero) ScoutSleepPolicy.Scout(enemy);
        }
        catch (Exception error) { Log("the Scout's detection state", error); }
    }

    // A tendril has registered a player. The Scout is alert, its tendril is out and may be pulled back
    // in - none of that is a scream, so it is still asleep.
    internal static void Alerted(ES_ScoutDetection state)
    {
        try
        {
            Ready();
            IntPtr enemy = Enemy(Owner(state));
            if (enemy != IntPtr.Zero) ScoutSleepPolicy.Alerted(enemy);
        }
        catch (Exception error) { Log("the Scout's detection result", error); }
    }

    // DoStartScream is the actual scream, not ActivateState/Enter's charge-up.
    // Tendril motion and the charge-up remain sleeping until this witness.
    internal static void Screamed(ES_ScoutScream state)
    {
        try
        {
            Ready();
            EnemyAgent agent = Owner(state);
            IntPtr enemy = Enemy(agent);
            if (enemy != IntPtr.Zero) ScoutSleepPolicy.Screamed(enemy, Tendrils(agent));
        }
        catch (Exception error) { Log("the Scout's scream state", error); }
    }

    // Every blow that lands re-reads the enemy it landed on: a Scout is confirmed there, and a witness
    // left behind by the previous occupant of a pooled pointer is dropped once the enemy under it is
    // plainly not a Scout - `IsScout` is the same figure the kill itself is graded by.
    internal static void Refresh(Dam_EnemyDamageBase damage)
    {
        try
        {
            Ready();
            IntPtr enemy = damage?.Pointer ?? IntPtr.Zero;
            EnemyAgent agent = damage?.Owner;
            if (enemy == IntPtr.Zero || agent == null) return;
            if (IsScout(agent))
            {
                ScoutSleepPolicy.Scout(enemy);
                var scream = agent.Locomotion?.ScoutScream;
                if (agent.Locomotion?.CurrentStateEnum == ES_StateEnum.ScoutScream && scream != null
                    && (scream.m_state == ES_ScoutScream.ScoutScreamState.Scream
                        || scream.m_state == ES_ScoutScream.ScoutScreamState.Response
                        || scream.m_state == ES_ScoutScream.ScoutScreamState.Done))
                    ScoutSleepPolicy.Screamed(enemy);
            }
            else ScoutSleepPolicy.Forget(enemy);
        }
        catch (Exception error) { Log("the damaged enemy", error); }
    }

    private static EnemyAgent Owner(ES_Base state)
    {
        if (state == null) return null;
        EnemyAgent agent = state.m_enemyAgent;
        if (agent != null) return agent;
        EnemyLocomotion locomotion = state.m_locomotion;
        return locomotion == null ? null : locomotion.m_agent;
    }

    // A Scout carries the two Scout states on its locomotion and nothing else does, so their presence
    // identifies one even before the game's own flag is readable.
    private static bool IsScout(EnemyAgent agent)
    {
        if (agent == null) return false;
        try
        {
            return agent.IsScout;
        }
        catch { return false; }
    }

    // Read-only and best effort: which tendrils the Scout had out at the instant it woke. That says
    // nothing about the sleeping rule, and it is here to show that a tendril never was the witness.
    private static string Tendrils(EnemyAgent agent)
    {
        try
        {
            EnemyLocomotion locomotion = agent?.Locomotion;
            ScoutAntennaDetection antennas = locomotion?.ScoutDetection?.m_antennaDetection;
            if (antennas == null) return "tendrils unreadable";
            Il2CppSystem.Collections.Generic.List<ScoutAntenna> active = antennas.m_activeTendrils;
            int extended = 0, total = 0;
            if (active != null)
            {
                for (int i = 0; i < active.Count; i++)
                {
                    ScoutAntenna tendril = active[i];
                    if (tendril == null) continue;
                    total++;
                    ScoutAntenna.eTendrilState phase = tendril.State;
                    if (phase == ScoutAntenna.eTendrilState.MovingOut || phase == ScoutAntenna.eTendrilState.WaitingOut) extended++;
                }
            }
            return $"tendrils {extended}/{total} out, wants={(antennas.m_wantsToHaveTendrils ? "out" : "in")}, locomotion={locomotion.CurrentStateEnum}";
        }
        catch { return "tendrils unreadable"; }
    }

    private static void Log(string what, Exception error)
    {
        if (_logged) return;
        _logged = true;
        Plugin.Logger?.LogWarning($"[DIAG] the Scout sleeping capture could not read {what}, so that witness is missing: {error.Message}");
    }
}

// The Scout's own state objects are the only witness of whether it has screamed yet.
[HarmonyPatch]
internal static class ScoutDetectionSetupPatch
{
    private static IEnumerable<MethodBase> TargetMethods() => ImpactTargets.Methods(typeof(ES_ScoutDetection), "Setup");

    [HarmonyPostfix]
    private static void Postfix(ES_ScoutDetection __instance) => ScoutWitness.Fresh(__instance);
}

[HarmonyPatch]
internal static class ScoutDetectionEnterPatch
{
    private static IEnumerable<MethodBase> TargetMethods() => ImpactTargets.Methods(typeof(ES_ScoutDetection), "Enter", "SyncEnter");

    [HarmonyPostfix]
    private static void Postfix(ES_ScoutDetection __instance) => ScoutWitness.Dormant(__instance);
}

[HarmonyPatch]
internal static class ScoutDetectionAlertPatch
{
    private static IEnumerable<MethodBase> TargetMethods() => ImpactTargets.Methods(typeof(ES_ScoutDetection), "OnDetectFoundPlayer");

    [HarmonyPostfix]
    private static void Postfix(ES_ScoutDetection __instance) => ScoutWitness.Alerted(__instance);
}

// Observe the actual start, not the earlier activation or charge-up state.
[HarmonyPatch]
internal static class ScoutScreamPatch
{
    private static IEnumerable<MethodBase> TargetMethods() => ImpactTargets.Methods(typeof(ES_ScoutScream), "DoStartScream");

    [HarmonyPostfix]
    private static void Postfix(ES_ScoutScream __instance) => ScoutWitness.Screamed(__instance);
}
