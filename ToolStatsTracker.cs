using System;
using System.Collections.Generic;
using Agents;
using Enemies;
using Gear;
using Il2CppInterop.Runtime.InteropTypes;
using Player;
using SNetwork;
using LevelGeneration;
namespace HawChow.GTFOCombatPanel;

internal readonly struct SentryScope
{
    internal readonly PlayerAgent PreviousOwner;
    internal readonly SentryGunInstance_Firing_Bullets PreviousSentry;
    internal SentryScope(PlayerAgent owner, SentryGunInstance_Firing_Bullets sentry)
    { PreviousOwner = owner; PreviousSentry = sentry; }
}

internal readonly struct MineBlastScope
{
    internal readonly ulong PreviousOwner;
    internal readonly bool PreviousMineBlast;
    internal readonly bool PreviousEquipmentMineBlast;
    internal MineBlastScope(ulong owner, bool mineBlast, bool equipmentMineBlast)
    { PreviousOwner = owner; PreviousMineBlast = mineBlast; PreviousEquipmentMineBlast = equipmentMineBlast; }
}

internal readonly struct GlueDoorHitSample
{
    internal readonly IntPtr Door, Attacker;
    internal readonly uint SearchId;
    internal readonly int Frame;
    internal readonly ulong Owner;
    internal bool IsValid => Door != IntPtr.Zero && Attacker != IntPtr.Zero && Owner != 0UL;

    internal GlueDoorHitSample(IntPtr door, IntPtr attacker, uint searchId, int frame, ulong owner)
    { Door = door; Attacker = attacker; SearchId = searchId; Frame = frame; Owner = owner; }
}

// Game-facing tool event capture. The score still reads scan first-marks; this class only supplies the
// extra figures printed in the ToolEfficiency cell and the owner evidence for tool-caused friendly fire.
internal static class ToolStatsTracker
{
    private static readonly ToolImpactLedger Ledger = new ToolImpactLedger();
    private static SentryGunInstance_Firing_Bullets _activeSentry;
    private static ulong _activeMineBlastOwner;
    private static bool _activeMineBlast;
    private static bool _activeEquipmentMineBlast;
    private static int _mineDiagnosticBudget = 32;
    private static readonly SentryShotLedger SentryShots = new();
    private static readonly Dictionary<IntPtr, (SentryGunInstance_Firing_Bullets Turret, Il2CppSystem.Action Callback)> SentryEvents = new();

    internal static void SetupSentry(SentryGunInstance_Firing_Bullets sentry)
    {
        if (sentry == null) return;
        if (SentryEvents.Remove(sentry.Pointer, out var old))
            try { old.Turret.remove_OnBulletFired(old.Callback); } catch { }
        SentryShots.Forget(sentry.Pointer); Ledger.BeginSentryWindow(sentry.Pointer);
        AttachSentry(sentry);
    }

    internal static void CreditMineCoreReady(MineDeployerInstance_Detonate_Explosive explosion)
    {
        try { CreditMineInstanceSpawn(explosion?.GetComponent<MineDeployerInstance>()); }
        catch (Exception error) { CombatTracker.LogToolError("mine core setup", error); }
    }

    internal static void AttachSentry(SentryGunInstance_Firing_Bullets sentry)
    {
        try
        {
            if (sentry == null || SentryEvents.ContainsKey(sentry.Pointer)) return;
            Il2CppSystem.Action callback = (Action)(() => CreditSentryEvent(sentry));
            sentry.add_OnBulletFired(callback);
            SentryEvents[sentry.Pointer] = (sentry, callback);
        }
        catch (Exception error) { CombatTracker.LogToolError("sentry event binding", error); }
    }

    internal static SentryScope BeginSentry(SentryGunInstance_Firing_Bullets sentry)
    {
        var previous = new SentryScope(CombatTracker.ToolOwner, _activeSentry);
        AttachSentry(sentry);
        if (sentry != null) Ledger.BeginSentryWindow(sentry.Pointer);
        _activeSentry = sentry;
        CombatTracker.ToolOwner = sentry?.m_core?.Owner;
        return previous;
    }

    internal static void EndSentry(SentryScope scope)
    {
        CombatTracker.ToolOwner = scope.PreviousOwner;
        _activeSentry = scope.PreviousSentry;
    }

    internal static bool TryCaptureSentry(ulong owner, out IntPtr turret, out uint searchId)
    {
        turret = IntPtr.Zero;
        searchId = 0u;
        if (_activeSentry == null || _activeSentry.Pointer == IntPtr.Zero
            || CombatTracker.ToolOwner?.Owner?.Lookup != owner) return false;
        turret = _activeSentry.Pointer;
        searchId = _activeSentry.m_damageSearchID;
        return true;
    }

    // The master firing call is the only stream that knows `doDamage`, so it decides whether a round was
    // real and opens the scope the turret's own notification is paired against.
    internal static SentryShotScope BeginSentryShot(SentryGunInstance_Firing_Bullets sentry, bool doDamage)
    {
        try
        {
            if (sentry == null || sentry.Pointer == IntPtr.Zero) return default;
            SNet_Player owner = null;
            bool counts = doDamage && TryTrackSentry(sentry, out owner);
            if (counts) Ledger.BeginSentryWindow(sentry.Pointer);
            long observed = SentryShots.BeginCall(sentry.Pointer, counts, out SentryShotScope scope);
            if (observed > 0) CreditSentryShots(sentry, owner, observed, false);
            return scope;
        }
        catch (Exception error) { CombatTracker.LogToolError("sentry shot scope", error); return default; }
    }

    internal static void EndSentryShot(SentryShotScope scope)
    {
        try { SentryShots.EndCall(scope); }
        catch (Exception error) { CombatTracker.LogToolError("sentry shot scope", error); }
    }

    // The turret's own notification carries no `doDamage`, so the open call scope decides: inside a call
    // that fired without damage the notification describes a non-damaging round and is dropped, inside a
    // damaging call it is that call's own shot, and outside any call it is the only evidence of the round.
    internal static void CreditSentryEvent(SentryGunInstance_Firing_Bullets sentry)
    {
        try
        {
            if (!TryTrackSentry(sentry, out SNet_Player owner)) return;
            long observed = SentryShots.ObserveEvent(sentry.Pointer);
            if (observed > 0) CreditSentryShots(sentry, owner, observed, true);
        }
        catch (Exception error) { CombatTracker.LogToolError("sentry event", error); }
    }

    private static bool TryTrackSentry(SentryGunInstance_Firing_Bullets sentry, out SNet_Player owner)
    {
        owner = null;
        if (sentry == null || sentry.Pointer == IntPtr.Zero || !SNet.IsMaster) return false;
        if (sentry.m_core?.Owner?.Owner is not SNet_Player candidate || !CombatTracker.ShouldTrack(candidate)) return false;
        owner = candidate;
        return true;
    }

    private static void CreditSentryShots(SentryGunInstance_Firing_Bullets sentry, SNet_Player owner, long observed, bool nativeEvent)
    {
        var counters = CombatTracker.ObservePlayer(owner).Impact;
        for (long n = 0; n < observed; n++) counters.CreditSentryShot();
        if (SentryFirstShotReported.Add(sentry.Pointer))
            Plugin.Logger?.LogInfo($"[DIAG] sentry firing counted: turret={sentry.Pointer}, source={(nativeEvent ? "native event without FireBullet scope" : "FireBullet with doDamage")}, searchId={sentry.m_damageSearchID}; each FireBullet call owns its own scope token and its native notification is paired to it, so a stream that drops out no longer swallows the other.");
    }

    // The turret's own counters are what the tool row shows, and until now nothing reported them, so a
    // row reading "shot 29, hit 1" could not be told apart from a dedup that swallowed 28 of them.
    private static readonly HashSet<IntPtr> SentryRepeatReported = new HashSet<IntPtr>();
    private static readonly HashSet<IntPtr> SentryFirstShotReported = new HashSet<IntPtr>();

    internal static void CreditSentryDamage(CombatPlayer player, IntPtr turret, uint searchId, double damage)
    {
        if (player == null || turret == IntPtr.Zero || !double.IsFinite(damage) || damage <= 0) return;
        bool first = Ledger.TrySentryHit(turret, searchId);
        if (first) player.Impact.CreditSentryHit();
        else if (SentryRepeatReported.Add(turret))
            Plugin.Logger?.LogInfo($"[DIAG] sentry damage deduplicated: turret={turret}, searchId={searchId}, damage={damage:0.0}; an identical (turret, searchId) pair already counted as a hit, so this one adds damage but not a hit. If the game reuses one search id per target rather than per bullet, this is what compresses hits.");
        player.Impact.AddSentryDamage(damage);
    }

    internal static void CreditGlueProjectile(GlueGunProjectile projectile, PlayerAgent source)
    {
        try
        {
            SNet_Player owner = source?.Owner;
            if (projectile == null || !CombatTracker.ShouldTrack(owner) || source.Inventory?.WieldedSlot != InventorySlot.GearClass) return;
            if (!PlayerBackpackManager.TryGetItem(owner, InventorySlot.GearClass, out BackpackItem tool) || tool?.Instance?.TryCast<GlueGun>() == null) return;
            Ledger.BeginGlueProjectile(projectile.Pointer);
            CombatTracker.ObservePlayer(owner).Impact.CreditGlueShot();
            if (!_glueShotReported) { _glueShotReported = true; Plugin.Logger?.LogInfo("[DIAG] glue projectile launch counted from Fire owner parameter; pooled projectile identity reset."); }
        }
        catch (Exception error) { CombatTracker.LogToolError("glue shot", error); }
    }
    private static bool _glueShotReported;

    internal static void CreditGlueContact(Dam_EnemyDamageBase enemy, GlueGunProjectile projectile)
    {
        try
        {
            if (enemy?.Owner == null || projectile?.m_owner?.Owner == null) return;
            SNet_Player owner = projectile.m_owner.Owner;
            if (!CombatTracker.ShouldTrack(owner)) return;
            CombatPlayer player = CombatTracker.ObservePlayer(owner);
            if (Ledger.TryGlueHit(owner.Lookup, projectile.Pointer)) player.Impact.CreditGlueHit();
            if (Ledger.TryGluedEnemy(owner.Lookup, enemy.Owner.Pointer)) player.Impact.CreditGluedEnemy();
        }
        catch (Exception error) { CombatTracker.LogToolError("glue contact", error); }
    }

    internal static void CreditGlueDoorApplication(LG_WeakDoorBladeDamage blade, GlueGunProjectile projectile)
    {
        try
        {
            SNet_Player owner = projectile?.m_owner?.Owner;
            if (blade == null || !CombatTracker.ShouldTrack(owner)) return;
            if (!Ledger.TryRegisterGlueDoor(blade.Pointer, projectile.Pointer, owner.Lookup)) return;
            CombatPlayer player = CombatTracker.ObservePlayer(owner);
            if (Ledger.TryGlueHit(owner.Lookup, projectile.Pointer)) player.Impact.CreditGlueHit();
        }
        catch (Exception error) { CombatTracker.LogToolError("glue door application", error); }
    }

    internal static void ForgetGlueDoor(LG_WeakDoorBladeDamage blade)
    {
        try { Ledger.ForgetGlueDoor(blade?.Pointer ?? IntPtr.Zero); }
        catch (Exception error) { CombatTracker.LogToolError("glue door clear", error); }
    }

    internal static GlueDoorHitSample CaptureGlueDoorHit(LG_WeakDoorBladeDamage blade, Agent source)
    {
        try
        {
            EnemyAgent attacker = source != null ? source.TryCast<EnemyAgent>() : null;
            if (blade == null || attacker == null || !(blade.GetAttachedGlueVolume() > 0f)) return default;
            IntPtr door = blade.Pointer;
            if (!Ledger.TryGetGlueDoorOwner(door, out ulong owner)) return default;
            return new GlueDoorHitSample(door, attacker.Pointer, blade.TempSearchID, UnityEngine.Time.frameCount, owner);
        }
        catch (Exception error) { CombatTracker.LogToolError("glued door hit capture", error); return default; }
    }

    internal static void CreditGlueDoorHit(GlueDoorHitSample sample)
    {
        try
        {
            if (!sample.IsValid || !Ledger.TryGlueDoorHit(sample.Door, sample.Attacker, sample.SearchId, sample.Frame)) return;
            if (TryPlayer(sample.Owner, out CombatPlayer player)) player.Impact.CreditGlueHit();
        }
        catch (Exception error) { CombatTracker.LogToolError("glued door hit", error); }
    }



    // Keep the game callback when available, and also observe the spawned synced mine after its core is assigned.
    internal static void CreditMineDeployment(MineDeployerFirstPerson deployer, ISyncedItem item, PlayerAgent sourceAgent)
    {
        try
        {
            if (deployer == null || item == null)
            {
                LogMineDiagnostic($"mine spawn callback missing argument: deployer={deployer != null}; item={item != null}");
                return;
            }
            MineDeployerInstance mine = item.TryCast<MineDeployerInstance>();
            if (mine == null) { LogMineDiagnostic("mine spawn callback item is not MineDeployerInstance"); return; }
            MineDeployerInstance_Detonate_Explosive detonation = mine.m_detonationComp?.TryCast<MineDeployerInstance_Detonate_Explosive>();
            iMineDeployerInstanceCore core = detonation?.m_core;
            SNet_Player sourceOwner = sourceAgent?.Owner;
            SNet_Player coreOwner = core?.Owner?.Owner;
            // A carried-in mine is not a deployment, but this callback is the only place a local player's
            // mine reports who placed it - the field log showed it refused here and then refused again by
            // the category check on the other path, so a wild mine never reached its placer at all.
            bool countsAsDeployment = !deployer.m_isConsumable;
            SNet_Player owner = sourceOwner ?? coreOwner;
            if (owner == null)
            {
                LogMineDiagnostic($"mine placement callback has no owner: sourceAgent={(sourceAgent != null)}; item={mine.Pointer}");
                return;
            }
            if (!CombatTracker.ShouldTrack(owner))
            {
                LogMineDiagnostic($"mine placement callback not tracked on this peer: owner={owner.Lookup}; master={SNet.IsMaster}; local={owner.IsLocal}");
                return;
            }
            RegisterMine(mine, core?.Pointer ?? IntPtr.Zero, owner, "spawn callback", sourceOwner, coreOwner, countsAsDeployment);
        }
        catch (Exception error) { CombatTracker.LogToolError("mine deployment callback", error); }
    }

    // Observe the synced mine instance when its detonation core is available; the captured run had no first-person spawn receipt.
    internal static void CreditMineInstanceSpawn(MineDeployerInstance mine)
    {
        try
        {
            MineDeployerInstance_Detonate_Explosive detonation = mine?.m_detonationComp?.TryCast<MineDeployerInstance_Detonate_Explosive>();
            iMineDeployerInstanceCore core = detonation?.m_core;
            SNet_Player owner = core?.Owner?.Owner;
            if (mine == null || core == null || owner == null)
            {
                LogMineDiagnostic($"mine instance spawn unresolved: item={(mine == null ? "missing" : mine.Pointer.ToString())}; core={(core == null ? "missing" : core.Pointer.ToString())}; owner={(owner?.Lookup ?? 0UL)}");
                return;
            }
            if (!CombatTracker.ShouldTrack(owner))
            {
                LogMineDiagnostic($"mine instance spawn not tracked on this peer: owner={owner.Lookup}; master={SNet.IsMaster}; local={owner.IsLocal}");
                return;
            }
            if (!PlayerBackpackManager.TryGetItem(owner, InventorySlot.GearClass, out BackpackItem backpack)
                || backpack?.Instance == null)
            {
                LogMineDiagnostic($"mine instance spawn has no readable tool slot: owner={owner.Lookup}; item={mine.Pointer}; core={core.Pointer}");
                return;
            }
            // `MineDeployerInstance.OnSpawn` only ever fires for a mine, and the core names the gear
            // category of the tool that placed it, so the slot does not have to narrow-cast to the
            // first-person face: a bot's slot, and a mine picked up and placed again, both hand back a
            // plain ItemEquippable. Narrowing first rejected every one of them in the field log
            // ("tool slot is not MineDeployerFirstPerson") while those mines plainly detonated.
            ItemEquippable item = backpack.Instance.TryCast<ItemEquippable>();
            if (item == null)
            {
                LogMineDiagnostic($"mine instance spawn tool slot is not an item: owner={owner.Lookup}; item={mine.Pointer}; core={core.Pointer}");
                return;
            }
            uint toolCategory = CombatTracker.GearCategoryOf(item);
            uint mineCategory = core.GearCategoryId;
            // Three cases, and only the first is a deployment:
            //   1. the carried tool IS the deployer (non-consumable) and the core's category is its own
            //      -> counted, because the tool really placed it;
            //   2. a carried-in mine (consumable), or a deployer face this peer cannot read
            //      -> attributed to whoever placed it, never counted, because it did not come out of a tool;
            //   3. the core carries no category at all -> nothing to hang the attribution on, so it is skipped.
            // The field log showed a carried-in mine refused as "toolCategory=12; coreCategory=13": the mine
            // item's category belongs to the mine, never to whatever the player happened to be holding.
            MineDeployerFirstPerson deployer = item.TryCast<MineDeployerFirstPerson>();
            bool consumptionKnown = deployer != null;
            bool consumable = consumptionKnown && deployer.m_isConsumable;
            if (mineCategory == 0u)
            {
                LogMineDiagnostic($"mine instance spawn has no category to attribute: owner={owner.Lookup}; toolCategory={toolCategory}; item={mine.Pointer}; core={core.Pointer}");
                return;
            }
            // `consumptionKnown` must NOT gate the count: a bot exposes no first-person face, so requiring
            // it turned every bot-deployed mine into "carried in" and the deployment column read zero.
            // Only a face that positively says "consumable" rules a mine out; otherwise the category decides.
            bool counts = !consumable && ToolImpactLedger.MineBelongsToSlot(toolCategory, mineCategory);
            if (!counts && !consumable)
                LogMineDiagnostic($"mine instance spawn attributed but not counted: owner={owner.Lookup}; firstPerson={consumptionKnown}; toolCategory={toolCategory}; coreCategory={mineCategory}; item={mine.Pointer}; core={core.Pointer}");
            RegisterMine(mine, core.Pointer, owner, "synced-item OnSpawn", owner, owner, counts);
        }
        catch (Exception error) { CombatTracker.LogToolError("mine instance spawn", error); }
    }

    internal static void ReportMinePlaceAction(MineDeployerFirstPerson deployer)
    {
        try
        {
            if (deployer == null) return;
            SNet_Player owner = deployer.Owner?.Owner;
            LogMineDiagnostic($"MineDeployerFirstPerson.PlaceMine called: owner={(owner?.Lookup ?? 0UL)}; consumable={deployer.m_isConsumable}; toolCategory={CombatTracker.GearCategoryOf(deployer)}");
        }
        catch (Exception error) { CombatTracker.LogToolError("mine placement action", error); }
    }

    private static bool RegisterMine(MineDeployerInstance mine, IntPtr corePointer, SNet_Player owner, string source,
        SNet_Player sourceOwner, SNet_Player coreOwner, bool countsAsDeployment = true)
    {
        // One registration path for both kinds of mine. The ledger keeps every mine's placer so a blast is
        // attributed, and only counts the ones this flag marks, which is what keeps a carried-in mine out
        // of the deployment and trigger columns without losing its damage.
        bool counted = Ledger.TryDeployMine(mine.Pointer, corePointer, owner.Lookup, !countsAsDeployment);
        if (counted) CombatTracker.ObservePlayer(owner).Impact.CreditMineDeployment();
        LogMineDiagnostic($"mine placement: source={source}; counted={counted}; owner={owner.Lookup}; sourceOwner={(sourceOwner?.Lookup ?? 0UL)}; coreOwner={(coreOwner?.Lookup ?? 0UL)}; item={mine.Pointer}; core={(corePointer == IntPtr.Zero ? "missing" : corePointer.ToString())}; carriedIn={!countsAsDeployment}");
        return counted;
    }


    internal static void CreditMinePickup(MineDeployerInstance mine)
    {
        try
        {
            if (mine == null || !Ledger.TryRecoverMine(mine.Pointer, out ulong ownerLookup)) return;
            if (TryPlayer(ownerLookup, out CombatPlayer player)) player.Impact.CreditMinePickup();
        }
        catch (Exception error) { CombatTracker.LogToolError("mine recovery", error); }
    }

    internal static void CreditMineTrigger(MineDeployerInstance mine)
    {
        try
        {
            if (mine != null) CreditMineInstanceSpawn(mine);
            if (mine == null || !Ledger.TryTriggerMine(mine.Pointer, out ulong ownerLookup)) return;
            if (!TryPlayer(ownerLookup, out CombatPlayer player)) return;
            player.Impact.CreditMineTrigger();
            CombatTracker.NoteAttack(ownerLookup, (int)InventorySlot.GearClass);
        }
        catch (Exception error) { CombatTracker.LogToolError("mine trigger", error); }
    }

    internal static MineBlastScope BeginMineBlast(MineDeployerInstance_Detonate_Explosive explosion)
    {
        var scope = new MineBlastScope(_activeMineBlastOwner, _activeMineBlast, _activeEquipmentMineBlast);
        _activeMineBlast = true;
        _activeMineBlastOwner = 0UL;
        _activeEquipmentMineBlast = false;
        try
        {
            var mine = explosion?.GetComponent<MineDeployerInstance>();
            if (mine != null) CreditMineInstanceSpawn(mine);
            iMineDeployerInstanceCore core = explosion?.m_core;
            if (core == null)
            {
                if (mine != null && Ledger.TryGetMineOwner(mine.Pointer, out ulong placer))
                {
                    _activeMineBlastOwner = placer;
                    _activeEquipmentMineBlast = Ledger.IsCountedMine(mine.Pointer);
                    if (Ledger.TryTriggerMine(mine.Pointer, out ulong fallbackTriggerOwner) && TryPlayer(fallbackTriggerOwner, out CombatPlayer placerRow)) placerRow.Impact.CreditMineTrigger();
                }
                else LogMineDiagnostic("mine blast has no core or registered placer; recent-attacker fallback suppressed");
                return scope;
            }
            // Attribution and counting are separate questions here too. `TryGetMineOwner` answers "whose
            // mine is this" for any tracked mine, carried in or not; `IsCountedMine` answers "did this
            // player's tool place it". Only the second may hang the blast on a gear row or a trigger count.
            bool attributedMine = Ledger.TryGetMineOwner(core.Pointer, out ulong trackedOwner);
            if (!attributedMine && mine != null) attributedMine = Ledger.TryGetMineOwner(mine.Pointer, out trackedOwner);
            bool equipmentMine = attributedMine && (Ledger.IsCountedMine(core.Pointer) || (mine != null && Ledger.IsCountedMine(mine.Pointer)));
            ulong coreOwner = core.Owner?.Owner?.Lookup ?? 0UL;
            _activeMineBlastOwner = attributedMine ? trackedOwner : coreOwner;
            _activeEquipmentMineBlast = equipmentMine;
            IntPtr identity = mine != null ? mine.Pointer : core.Pointer;
            if (equipmentMine && Ledger.TryTriggerMine(identity, out ulong triggerOwner)
                && TryPlayer(triggerOwner, out CombatPlayer player)) player.Impact.CreditMineTrigger();
            LogMineDiagnostic($"mine blast owner: core={core.Pointer}; coreOwner={coreOwner}; ledgerOwner={trackedOwner}; resolved={_activeMineBlastOwner}; equipment={equipmentMine}; tracked={attributedMine}");
        }
        catch (Exception error) { CombatTracker.LogToolError("mine blast attribution", error); }
        return scope;
    }

    internal static void EndMineBlast(MineBlastScope scope)
    {
        _activeMineBlastOwner = scope.PreviousOwner;
        _activeMineBlast = scope.PreviousMineBlast;
        _activeEquipmentMineBlast = scope.PreviousEquipmentMineBlast;
    }
    internal static ulong ActiveMineBlastOwner => _activeMineBlastOwner;
    internal static bool IsMineBlastActive => _activeMineBlast;
    internal static bool IsEquipmentMineBlast => _activeEquipmentMineBlast;
    internal static void ForgetMine(MineDeployerInstance mine) { if (mine != null) Ledger.ForgetMine(mine.Pointer); }
    internal static void CreditMineDamage(ulong ownerLookup, double damage)
    {
        if (ownerLookup != 0UL && damage > 0 && TryPlayer(ownerLookup, out CombatPlayer player)) player.Impact.AddMineDamage(damage);
    }

    private static readonly FriendlyFireAttributionLedger FriendlyFire = new FriendlyFireAttributionLedger();
    private const double FriendlyFireAttributionSeconds = 3.0;

    internal static void ObserveFriendlyDamage(ulong attacker, ulong victim)
        => FriendlyFire.ObserveDamage(attacker, victim, UnityEngine.Time.time);

    internal static void ClearFriendlyDamage(ulong victim) => FriendlyFire.ForgetVictim(victim);

    internal static bool TryCreditFriendlyFireDown(ulong victim, bool aliveBefore, bool aliveAfter, out ulong attacker)
    {
        if (!FriendlyFire.TryTake(victim, UnityEngine.Time.time, FriendlyFireAttributionSeconds, out attacker)) return false;
        return FriendlyFireRule.ShouldCreditDeath(attacker, victim, aliveBefore, aliveAfter);
    }

    internal static void Reset()
    {
        Ledger.Clear();
        foreach (var entry in SentryEvents.Values)
            try { entry.Turret.remove_OnBulletFired(entry.Callback); } catch { }
        SentryEvents.Clear(); SentryShots.Clear(); SentryFirstShotReported.Clear(); SentryRepeatReported.Clear();
        _glueShotReported = false;
        _activeSentry = null;
        _activeMineBlastOwner = 0UL;
        _activeMineBlast = false;
        _activeEquipmentMineBlast = false;
        _mineDiagnosticBudget = 32;
        FriendlyFire.Clear();
        CombatTracker.ToolOwner = null;
    }

    private static void LogMineDiagnostic(string message)
    {
        if (_mineDiagnosticBudget <= 0) return;
        _mineDiagnosticBudget--;
        Plugin.Logger?.LogInfo("[DIAG] " + message);
    }

    private static bool TryPlayer(ulong lookup, out CombatPlayer player)
    {
        if (CombatTracker.Store.TryGet(lookup, out player) && player != null) return true;
        if (SNet.TryGetPlayer(lookup, out SNet_Player owner) && CombatTracker.ShouldTrack(owner))
        {
            player = CombatTracker.ObservePlayer(owner);
            return player != null;
        }
        player = null;
        return false;
    }
}