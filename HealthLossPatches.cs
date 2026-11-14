using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace HawChow.GTFOCombatPanel;

// Observe every method of a player's damage base that can move their health, and credit only what was
// actually lost. Nothing is measured by wrapping a single call any more: the ledger keeps a watermark per
// player and is fed by every path that reports a figure, so a host sees a client's life go down whether the
// drop arrived as a damage event or as the replicated health update behind it - which is the whole of
// the "blood loss missing on the host" report - and a drop reported twice is still counted once.
//
// Healing, revives and maximum-health changes are not blood loss, and they are not filtered by name here:
// the ledger credits a decrease against an unchanged ceiling and nothing else, so a method that raises the
// figure needs no exclusion list to stay out of the total. `Setup` is in the list for the opposite reason:
// it is the moment a freshly spawned player's mark is seeded at full health, so the first blow that lands
// on them is measured instead of being swallowed as the first reading of a stranger.
//
// 1.19.33: `Setup` is not the only moment a mark can be missing, and when it is missing the first blow was
// still swallowed - the recorded Test run 3 lost exactly one hit's worth of life (`23.7` reported against a
// 25.0 friendly-fire total made of 20 hits of ~1.25467 HP). `Setup` runs when the damage base is set up,
// which is not the same moment as "this machine may track that owner" (the owner can still be unset), so a
// player's first observed event can be the very damage event that hit them. The prefix below reads the
// game's own health *before* the call and gives the ledger that figure as a mark - the real one, never
// `HealthMax` - and the postfix then measures the drop of that same call against it.
//
// 1.19.35: that pre-call reading was taken on *every* observed method, and a method that *sets* a health
// figure is not a blow. An object pool hands one damage base to the next player, so the seat's first call
// for its new owner can be `Setup`, `OnRevive` or a replicated `ReceiveSetHealth` that writes the spawn
// figure (100 -> 20 in the reported shape); seeding at the out-going occupant's 100 and then measuring the
// write with the postfix credited 80 HP nobody lost - a fabrication, and the one thing this metric may
// never do. The seed therefore has its own target list: `HealthLossTargets.Seedable` keeps it on the calls
// that report a hit (`Receive*Damage`, and `ReceiveTentacleGrabStop`, whose payload is damage as well), and
// the postfix keeps observing every accepted method exactly as it did - so a health write still moves the
// watermark, it simply cannot be credited from a mark this build invented for it.
//
// The two lists are two patch classes because a Harmony prefix cannot be told, at call time, which method
// it was attached to without a runtime-supplied parameter. One class per list means the seed's restriction
// is the class's own target list - the same `HealthLossTargets` rule the headless suite drives - and a
// failure to register the seed class can only cost the first blow its pre-call mark, never the postfix that
// credits everything else.
//
// One detail that must not be forgotten: the postfix is applied to the damage methods too, so on those the
// pair is "seed, then observe" - the 2026-11-13 run's 20 hits of ~1.25467 HP credit 25.0 in full.
[HarmonyPatch]
internal static class HealthLossSeedPatches
{
    // Only the methods that report a hit. A health *write* (`Setup`, `OnRevive`, `ReceiveSetHealth`,
    // `ReceiveAddHealth`, `ReceiveSetDead`, ...) is deliberately absent: its before/after pair is not damage.
    private static IEnumerable<MethodBase> TargetMethods()
        => HealthLossPatches.DamageBaseMethods(HealthLossTargets.Seedable);

    [HarmonyPrefix]
    [HarmonyWrapSafe]
    private static void Prefix(Dam_PlayerDamageBase __instance) => HealthLossPatches.SeedBefore(__instance);
}

[HarmonyPatch]
internal static class HealthLossPatches
{
    private static readonly HealthLossLedger Ledger = new HealthLossLedger();

    internal static void Reset()
    {
        Ledger.Clear();
        _unsettledLoggedAt = 1;
    }

    // The damage base's own void methods that `accept` names, which is the one rule the two patch classes share. A name
    // is only ever read here: the list is what decides whether a call is observed at all (`Accepts`) and whether it may
    // plant a pre-call mark (`Seedable`).
    //
    // 1.19.37: *whose* methods, and why that was the whole of the "the host's own blood loss is always 0" report.
    // `Dam_PlayerDamageLocal : Dam_PlayerDamageBase` is the only subclass in the assembly and the one a live player
    // carries, and it declares nineteen methods of its own - `Setup`, `OnRevive`, `OnGodModeUpdated`, `ReceiveSetHealth`,
    // `ReceiveSetDead` and every `Receive*Damage` but `ReceiveAddHealth`, `ReceiveModifyInfection`,
    // `ReceiveTentacleGrabStop`, `OnKilledByTankTentacle` and `SetupPackages` - as virtual *overrides*. A detour on the
    // base's implementation is therefore never entered for the machine's own seat: the call dispatches to the subclass'
    // own native method. The recorded patch receipt said so and nothing read it: it listed `Dam_PlayerDamageBase.*` only,
    // the host's row ended the run at `healthLost=0` while the three bots (whose seats the base's replicated writes do
    // reach) showed 25.0; a local hit and the subsequent down were never observed.
    //
    // The list is now the *DeclaredOnly* methods of both types. The two sets are disjoint by construction (a method is
    // declared on exactly one type), and `HealthLossSources.DeclaredMethods` still holds a `MethodBase` set as the
    // deduplication key, so a method can never be handed to Harmony twice. A profile whose interop carries no
    // `Dam_PlayerDamageLocal` degrades to the base alone and says so once (`HealthLossSources.DeclaringTypes`); it never
    // throws, because the alternative is a patch class that fails to register and a metric that stops moving entirely.
    //
    // The name rule is the same rule for both types: `Accepts`/`Seedable` are asked per name and never per type, so the
    // `ReceiveParasiteDamage` exclusion and the `ReceiveTentacleGrabStop` exception hold on the subclass exactly as on
    // the base. And a subclass override that calls its base implementation is still safe: both postfixes observe the
    // same figure and the watermark credits the drop once (the second `Observe` returns 0).
    internal static IEnumerable<MethodBase> DamageBaseMethods(Func<string, bool> accept)
        => HealthLossSources.DeclaredMethods(DeclaringTypes(), accept);

    // The two declaring types this mod detours, in the order they are read: the base first - the interop type this build
    // names directly - and then the subclass a live player really carries, when this profile's assembly has it.
    internal static IEnumerable<Type> DeclaringTypes()
        => HealthLossSources.DeclaringTypes(typeof(Dam_PlayerDamageBase), LocalDamageType());

    private static IEnumerable<MethodBase> TargetMethods() => DamageBaseMethods(HealthLossTargets.Accepts);

    private static bool _localDamageTypeResolved, _localTypeMissingLogged;
    private static Type _localDamageType;

    // The subclass, resolved by name out of the interop assembly this build was compiled against. Not `typeof` of it:
    // that would make the plugin refuse to *build* against a profile whose interop does not carry it, and the whole
    // point of the degradation is that such a profile still gets the base's observations.
    private static Type LocalDamageType()
    {
        if (_localDamageTypeResolved) return _localDamageType;
        _localDamageTypeResolved = true;
        _localDamageType = ResolveLocalDamageType();
        if (_localDamageType == null && !_localTypeMissingLogged)
        {
            // One bounded line per process, never one per frame: the answer cannot change while this assembly is loaded.
            _localTypeMissingLogged = true;
            try
            {
                Plugin.Logger?.LogWarning("[DIAG] health loss: this profile's interop carries no "
                    + HealthLossSources.LocalTypeName + " (or it is not a " + nameof(Dam_PlayerDamageBase)
                    + "), so the observation is attached to " + HealthLossSources.Describe(DeclaringTypes())
                    + " alone. Everything the base declares is still observed; a player's own overrides - the ones the"
                    + " machine's own seat dispatches to - are not, which is what the recorded host row's healthLost=0 was.");
            }
            catch (Exception) { }
        }
        return _localDamageType;
    }

    private static Type ResolveLocalDamageType()
    {
        Assembly assembly = null;
        try { assembly = typeof(Dam_PlayerDamageBase).Assembly; } catch (Exception) { return null; }
        if (assembly == null) return null;
        // The interop puts these types in the global namespace, so the simple name is the full name; a build that
        // generated them into a namespace is found by the same simple name, once.
        try
        {
            Type direct = assembly.GetType(HealthLossSources.LocalTypeName, false);
            if (direct != null && typeof(Dam_PlayerDamageBase).IsAssignableFrom(direct)) return direct;
        }
        catch (Exception) { }
        try
        {
            foreach (Type candidate in assembly.GetTypes())
            {
                if (candidate == null || candidate.Name != HealthLossSources.LocalTypeName) continue;
                if (typeof(Dam_PlayerDamageBase).IsAssignableFrom(candidate)) return candidate;
            }
        }
        catch (Exception) { }
        return null;
    }

    // 1.19.33: the reading *before* the call, and the one thing that makes a first blow measurable.
    //
    // `Ledger.Seed` fills a mark only when this machine has none for that (damage base, owner) pair - so on every
    // ordinary call this is one dictionary lookup and nothing else - and it never credits anything. What it buys is the
    // case in the recorded run: the first event this machine sees for a player is the damage event that hits them, and
    // without a pre-call mark that blow is the "first reading" the ledger may not claim. The figure is the game's own
    // `Health`, so a player who came into the level hurt, joined mid-run or was revived at part health is seeded where
    // they really were - `HealthMax` is never substituted, which would credit life nobody lost.
    //
    // Two nested calls: the outer prefix seeds, the inner prefix finds the mark and does nothing, the inner postfix
    // credits the drop and the outer postfix finds it already counted. A call that kills is credited against the life
    // that was really there. The seed is not gated on the level state (the mark is state, not a credit); the credit
    // below still is.
    //
    // 1.19.35: reached only from `HealthLossSeedPatches`, whose target list is `HealthLossTargets.Seedable` - the
    // methods that report a hit. Nothing here has to know its own method name, because a call that gets here is one of
    // those by construction.
    internal static void SeedBefore(Dam_PlayerDamageBase instance)
    {
        try
        {
            if (instance == null) return;
            var owner = instance.Owner != null ? instance.Owner.Owner : null;
            if (!CombatTracker.ShouldTrack(owner)) return;
            ulong lookup = 0UL;
            try { lookup = owner != null ? owner.Lookup : 0UL; } catch (Exception) { }
            Ledger.Seed(instance.Pointer, lookup, instance.Health, instance.HealthMax);
        }
        catch (Exception error) { CombatTracker.LogToolError("health loss seed", error); }
    }

    [HarmonyPostfix]
    [HarmonyWrapSafe]
    private static void Postfix(Dam_PlayerDamageBase __instance)
    {
        try
        {
            if (__instance == null) return;
            var owner = __instance.Owner != null ? __instance.Owner.Owner : null;
            if (!CombatTracker.ShouldTrack(owner)) return;
            ulong lookup = 0UL;
            try { lookup = owner != null ? owner.Lookup : 0UL; } catch (Exception) { }
            // The mark is seeded by whatever report arrives first, in the level or out of it. `Setup` runs when a
            // player is spawned and the lobby health sync runs before anyone has been hit; both are the moment a
            // full-health reading can be taken, and without it the first blow of the run would be swallowed as
            // "the first reading of a stranger". Only the credit is limited to a level.
            double loss = Ledger.Observe(__instance.Pointer, lookup, __instance.Health, __instance.HealthMax);
            // 1.19.35 (review fix): reported *before* the credit gate, because the seats it counts are exactly the ones that
            // credit nothing. See `NoteUnsettledSeeds`.
            NoteUnsettledSeeds();
            if (!(loss > 0)) return;
            if (GameStateManager.CurrentStateName != eGameStateName.InLevel) return;
            var counters = CombatTracker.ObservePlayer(owner).Impact;
            counters.HealthLost = Math.Min(double.MaxValue, counters.HealthLost + loss);
        }
        catch (Exception error) { CombatTracker.LogToolError("health loss observation", error); }
    }

    // 1.19.35 (review fix): the one loss this ledger can never credit is the blow whose own postfix did not run - the call
    // threw, or the seat was torn down with the player - because the next observation is the revive or the heal that wrote the
    // figure back up, and a re-base credits nothing: `死亡 -> 复活` used to lose that blow for good and say nothing about it.
    // Its size is not knowable from here (no measurement of the drop survives), so nothing is credited and nothing is invented;
    // the seat is counted by `HealthLossLedger.UnsettledSeeds` and reported at 1, 2, 4, 8... seats, which keeps the line count
    // logarithmic however long the run is. No such line means no such seat was seen - never that `healthLost` is complete.
    private static int _unsettledLoggedAt = 1;
    private static void NoteUnsettledSeeds()
    {
        int unsettled = Ledger.UnsettledSeeds;
        if (unsettled < _unsettledLoggedAt) return;
        _unsettledLoggedAt = unsettled <= int.MaxValue / 2 ? unsettled * 2 : int.MaxValue;
        Plugin.Logger?.LogInfo("[DIAG] blood loss: " + unsettled
            + " seat(s) were seeded before a call and re-based upwards by the next observation, so the blow in between is not in healthLost and is not guessed at.");
    }
}
