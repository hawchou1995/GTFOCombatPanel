using System;
using System.Collections.Generic;
using Agents;
using Enemies;
using Gear;
using Player;
using SNetwork;
using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace HawChow.GTFOCombatPanel;

internal readonly struct DamageSample
{
    internal readonly float HealthBefore;
    internal readonly CombatPlayer Player;
    internal readonly WeaponCombatStats Weapon;
    internal readonly bool Estimated;
    internal readonly DamageKind Cause;
    internal readonly IntPtr SentryPointer;
    internal readonly uint SentrySearchId;
    internal readonly EnemyDeathLedger.Scope DeathScope;
    internal DamageSample(float health, CombatPlayer player, WeaponCombatStats weapon, bool estimated,
        DamageKind cause, IntPtr sentryPointer = default, uint sentrySearchId = 0u, EnemyDeathLedger.Scope deathScope = null)
    {
        HealthBefore = health; Player = player; Weapon = weapon; Estimated = estimated; Cause = cause;
        SentryPointer = sentryPointer; SentrySearchId = sentrySearchId; DeathScope = deathScope;
    }
}
internal sealed class FireCapture
{
    internal FireCapture Parent;
    internal BulletWeapon Weapon;
    internal CombatPlayer Player;
    internal WeaponCombatStats Entry;
    internal ShotSample Sample = new ShotSample();
    internal int ClipBefore;
    internal bool Exact;
    internal bool IsRoot = true;
    // True when this shot belongs to a weapon EWC owns but has never reported for this player's slot, so
    // its pellets are counted here and reconciled with EWC's own totals the moment EWC starts reporting.
    internal bool EwcFallback;
    internal bool ProjectileObserved;
}

internal static class CombatTracker
{
    internal static readonly CombatStatsStore Store = new CombatStatsStore();
    private static readonly ExpeditionLifecycle Lifecycle = new ExpeditionLifecycle();
    private static readonly SettlementRows ResultRows = new SettlementRows();
    private static readonly DamageCreditLedger EnemyCredits = new DamageCreditLedger();
    // 1.19.65: the one balance per enemy instance that every estimate this machine makes for that enemy is spent
    // from while this machine is a client with no authority over it. Its rules - and what it is deliberately not -
    // are in `OptionalDamageEvents.cs`, beside the local-outgoing rule it serves.
    private static readonly EnemyHealthBudget Budget = new EnemyHealthBudget();
    internal static double DamageCredit(Dam_EnemyDamageBase enemy, ulong owner) => EnemyCredits.Read(enemy.Pointer, owner);
    private static bool _inResults;
    internal static List<CombatPlayer> SettlementView()
        => ResultRows.Update(CombatSync.DisplayRows(), CombatSync.Generation);
    internal static FireCapture ActiveFire;
    internal static PlayerAgent ToolOwner;
    private static readonly Dictionary<ulong, ColorRecord> Colors = new Dictionary<ulong, ColorRecord>();
    private static readonly Dictionary<ulong, int> PendingRemoteShots = new Dictionary<ulong, int>();
    private static readonly ReviveAttributionLedger ReviveLedger = new ReviveAttributionLedger();
    internal readonly struct ColorRecord
    {
        internal readonly UnityEngine.Color Color;
        internal ColorRecord(UnityEngine.Color color) { Color = color; }
    }
    internal static UnityEngine.Color PlayerColor(ulong lookup) => Colors.TryGetValue(lookup, out ColorRecord entry) ? entry.Color : UnityEngine.Color.white;
    // The squad total as the board divides by. The host adds up the rows it measured itself. A client
    // holds one of the four columns, so it prints the total a host that runs this mod told it and a
    // dash when no such host has spoken - never a share of a total this machine guessed at from the
    // health the enemies happened to lose on their way past it.
    internal static double? SquadEnemyDamage
    {
        get
        {
            if (!SNet.IsMaster) return CombatSync.SquadTotal;
            double total = 0;
            foreach (var player in CombatSync.DisplayRows()) total += Math.Max(0, player.Total.EnemyDamage);
            return total;
        }
    }
    internal static void ObserveState(eGameStateName state)
    {
        bool results = state == eGameStateName.ExpeditionSuccess || state == eGameStateName.ExpeditionFail;
        if (results && !_inResults)
        {
            if (GameStateManager.CurrentStateName == eGameStateName.InLevel) ScanTagPipeline.Drain();
            try { ResultRows.Update(CombatSync.DisplayRows(), CombatSync.Generation); }
            catch (Exception error) { LogImpact("settlement handoff", error); }
        }
        _inResults = results;
        CombatSync.Tick(state, UnityEngine.Time.realtimeSinceStartup);
        EwcProjectileBridge.EnsureInitialized();
        EwcDamageBridge.EnsureInitialized();
        if (!Lifecycle.ShouldReset(state.ToString()))
        {
            // Every frame the level is up, the signals no event reports are compared with their own
            // previous value: who is still standing, who is the last one standing, and what the bio
            // tracker has revealed.
            if (state == eGameStateName.InLevel) Tick();
            return;
        }
        EwcDamageBridge.Reset();
        Store.Reset(); ToolStatsTracker.Reset(); Colors.Clear(); PendingRemoteShots.Clear(); ActiveFire = null;
        ResultRows.Clear(); EnemyCredits.Clear(); Budget.Clear();
        LastAttacker.Clear(); LastDamageKind.Clear(); Deaths.Clear(); PendingEnemySpawns.Clear(); Alive.Clear(); ScanLedger.Clear(); ScanSquad.Clear();
        // 1.19.62: the round's created-enemy figure begins on this very edge, and this call is the ONLY thing that empties
        // it - `Generating -> InLevel` is the same round and does not, and a settlement screen only reads it. `state` is
        // which edge raised the reset: `Generating` means this machine was waiting for the level, so every enemy that
        // level creates belongs to its count (a preset set up before the first in-level frame included); any other state
        // here is a machine that walked into a level already running, and it may only report its own observation.
        Spawns.Begin(SNet.IsMaster, state == eGameStateName.Generating);
        // The settlement line's own latch goes with the round, so the next run's figure is written once.
        SettlementSpawns.Reset();
        ScanTagPipeline.Reset();
        // 1.19.62: the host's scan-round estimate belongs to one expedition, like every other ledger here. The
        // comparison latch goes with it, so the next run's first settlement line is always written.
        ScanRoundEstimates.Reset(); ScanRoundComparison.Reset();
        ReviveLedger.Clear();
        InterruptedFrame.Clear();
        SupplyTracker.Reset(); HealthLossPatches.Reset();
        EwcProjectileBridge.ResetExpedition();
#if EWC_PRESENT
        EwcBridge.ResetExpedition();
#endif
        CombatSync.ResetExpedition();
        _loneSurvivorAwarded = false;
        _loggedScan = _loggedBotScan = _loggedBotScanUnresolved = _loggedScanRepeat = _loggedBotAction = false;
        _loggedBotScanUse = _loggedBotScanStart = false;
        _loggedReviveState = _loggedReviveUnmatched = false;
        Plugin.Logger.LogInfo("[DIAG] New expedition: counters reset; success/failure screens retain the final counters.");
    }
    internal static bool ShouldTrack(SNet_Player player) => player != null
        && ReviveAttributionLedger.CanTrackOwner(player.IsInSlot, SNet.IsMaster, player.IsLocal);
    internal static CombatPlayer ObservePlayer(SNet_Player owner)
    {
        var player = Store.GetOrCreate(owner.Lookup);
        if (SNet.IsMaster) player.Impact.MarkKillMetricsObserved();
        player.DisplaySlot = owner.PlayerSlotIndex(); player.Name = CombatStatsFormatter.FullPlayerName(owner.GetName());
        Colors[owner.Lookup] = new ColorRecord(owner.PlayerColor);
        return player;
    }
    internal static void RefreshRoster()
    {
        if (SNet.Current == null || SNet.Slots == null || SNet.Slots.SlottedPlayers == null) return;
        var owners = SNet.Slots.SlottedPlayers;
        var presentLookups = new HashSet<ulong>();
        foreach (var owner in owners)
            if (owner != null && owner.Lookup != 0UL) presentLookups.Add(owner.Lookup);

        // An empty slot list can be a transient level transition. Never use that frame to erase the run.
        if (presentLookups.Count == 0) return;
        int removed = Store.PruneExcept(presentLookups);
        if (removed > 0)
            Plugin.Logger?.LogInfo($"[DIAG] roster rows pruned: removed={removed}; active={presentLookups.Count}; departed lookups no longer occupy a slot.");

        foreach (var owner in owners)
        {
            if (owner == null) continue;
            // A row is shown when this machine measures it itself, or when the host has sent figures for
            // it. The second case is the whole point of the exchange: on a client the other three rows
            // exist only because the host described them.
            bool synced = CombatSync.HasSnapshot(owner.Lookup);
            if (!ShouldTrack(owner) && !synced) continue;
            var player = ObservePlayer(owner);
            if (synced) continue;      // every gear row and the support role arrive with the host's snapshot
            if (!player.Total.AccuracyObserved) player.Total.AddAccuracy(0, 0, 0, !owner.IsLocal && !owner.IsBot);
            foreach (var slot in GearSlots)
            {
                var weapon = ResolveWeapon(player, owner, slot);
                if ((slot == InventorySlot.GearStandard || slot == InventorySlot.GearSpecial) && !weapon.Counters.AccuracyObserved)
                    weapon.Counters.AddAccuracy(0, 0, 0, !owner.IsLocal && !owner.IsBot);
            }
            var tool = ResolveWeapon(player, owner, InventorySlot.GearClass);
            player.SupportRole = Plugin.IsSupportTool(tool.Name);
        }
    }
    internal static readonly InventorySlot[] GearSlots = { InventorySlot.GearStandard, InventorySlot.GearSpecial, InventorySlot.GearClass, InventorySlot.GearMelee };
    internal static WeaponCombatStats ResolveWeapon(CombatPlayer player, SNet_Player owner, InventorySlot slot, ItemEquippable hint = null, uint gearCategoryId = 0)
    {
        ItemEquippable item = hint;
        if (item == null && PlayerBackpackManager.TryGetItem(owner, slot, out BackpackItem bp) && bp?.Instance != null)
            item = bp.Instance.TryCast<ItemEquippable>();
        string name = item == null ? slot.ToString() : item.ArchetypeName;
        if (string.IsNullOrEmpty(name) && item != null) name = item.PublicName;
        return player.Weapon((int)slot, item?.ArchetypeID ?? 0, name,
            gearCategoryId != 0 ? gearCategoryId : GearCategoryOf(item), hint != null || item != null);
    }
    // The gear category the game stamps onto every damage record this weapon produces, read from the
    // equipped item. It is the one handle that survives a modded weapon, whose own archetype id the
    // game may not expose at all, so this is what attributes a hit to the weapon that actually fired.
    internal static uint GearCategoryOf(ItemEquippable item)
    {
        try
        {
            var category = item?.GearCategoryData;
            return category == null ? 0 : category.persistentID;
        }
        catch { return 0; }
    }
    // One line per weapon class the first time it is seen at a firing window, and one line when that weapon
    // is EWC's (which is why this build deliberately leaves it alone). It exists to answer a question that
    // cannot be answered from a screenshot: a weapon whose shots never appear on the board either never
    // reaches a hooked `Fire` - so its firing path is not one of the four this build hooks, and no amount of
    // counting elsewhere will find it - or it does reach one and the figures are missing for another reason.
    private static void ReportFireClass(BulletWeapon weapon, bool ewcOwned)
    {
        try
        {
            if (weapon == null) return;
            string name = weapon.GetType().Name;
            string key = name + (ewcOwned ? "|ewc" : string.Empty);
            if (!FireClassesSeen.Add(key)) return;
            Plugin.Logger.LogInfo($"[DIAG] firing window: class={name}, ewcOwned={ewcOwned.ToString().ToLowerInvariant()}, weapon={(string.IsNullOrEmpty(weapon.ArchetypeName) ? weapon.PublicName : weapon.ArchetypeName)}{(ewcOwned ? "; EWC reports this one itself, so the panel takes its figures from EWC's streams" : "; this build counts it here, from the rays the shot casts")}.");
        }
        catch (Exception error) { LogImpact("fire class report", error); }
    }


    private static readonly HashSet<ulong> EwcBlindReported = new HashSet<ulong>();

    // One line when a weapon EWC owns fired and no ray reached this build. The row is left exactly as it
    // was: this is the case where an honest gap beats an invented figure, and the line says which it was.
    private static void ReportEwcFallbackBlind(FireCapture capture)
    {
        try
        {
            if (capture?.Player == null || capture.Entry == null) return;
            if (!EwcBlindReported.Add(capture.Player.Lookup * 16UL + (ulong)capture.Entry.Slot)) return;
            Plugin.Logger.LogInfo($"[DIAG] EWC weapon fired without a ray this build can see: player={capture.Player.Name}, slot={capture.Entry.Slot}, weapon={capture.Entry.Name}; EWC has never reported this weapon and the shot cast no ray here, so nothing is counted rather than inventing a pellet count that would read as a 0% hit rate. The shot is still recorded as the attack under way for the explosion that may follow it.");
        }
        catch (Exception error) { LogImpact("EWC blind shot", error); }
    }
    internal static FireCapture BeginFire(BulletWeapon weapon)
    {
        if (weapon?.Owner?.Owner == null || !ShouldTrack(weapon.Owner.Owner)) return null;
        bool ewcFallback = false;
#if EWC_PRESENT
        // A weapon ExtraWeaponCustomization has taken over is normally counted by EWC alone: it reports its
        // own weapons' shots, and counting them here as well would count every shot twice. That deferral is
        // conditional now, because EWC does not report every weapon it manages - a row it never publishes
        // for could only ever read 0/0/0 while the weapon was plainly being fired. Until EWC has reported
        // this player's slot, the shot is counted here from the rays this build can see; once EWC does
        // report, `EwcReconcile` folds the two together with a `max` and nothing is counted twice.
        if (EwcBridge.IsEwcWeapon(weapon))
        {
            ReportFireClass(weapon, true);
            if (EwcBridge.HasReports(weapon.Owner.Owner.Lookup, (int)weapon.ItemDataBlock.inventorySlot)) return null;
            ewcFallback = true;
        }
        else ReportFireClass(weapon, false);
#else
        ReportFireClass(weapon, false);
#endif
        if (ActiveFire != null && ActiveFire.Weapon.Pointer == weapon.Pointer) return new FireCapture { IsRoot = false };
        var owner = weapon.Owner.Owner;
        var player = ObservePlayer(owner);
        InventorySlot slot = weapon.ItemDataBlock.inventorySlot;
        var capture = new FireCapture
        {
            Parent = ActiveFire, Weapon = weapon, Player = player,
            Entry = ResolveWeapon(player, owner, slot, weapon), ClipBefore = weapon.m_clip,
            Exact = owner.IsLocal || (SNet.IsMaster && owner.IsBot), EwcFallback = ewcFallback
        };
        ActiveFire = capture; return capture;
    }
    internal static void EndFire(FireCapture capture, bool succeeded)
    {
        if (capture == null || !capture.IsRoot) return;
        ActiveFire = capture.Parent;
        if (!succeeded || !capture.Exact) return;
        if (!ProjectileAccuracyLedger.UseNativeCapture(capture.ProjectileObserved))
        {
            NoteAttack(capture.Player.Lookup, capture.Entry.Slot);
            return;
        }
        int rays = capture.Sample.Fired;
        int fired = rays;
        bool blind = false;
        // Fire may have no cast (empty clip); only use the verified pellet count when ammunition was consumed.
        if (fired == 0 && capture.Weapon.m_clip < capture.ClipBefore)
        {
            // A weapon EWC owns is never guessed at. Its own ray casts may not reach this build at all, and
            // inventing the pellets one shot carried would put a fabricated "0% accuracy" on a row that is
            // only here because EWC never reported it. The shot is still registered as the attack under way
            // so a blast that follows it can be attributed; no figure is invented for the row itself.
            if (capture.EwcFallback)
            {
                NoteAttack(capture.Player.Lookup, capture.Entry.Slot);
                ReportEwcFallbackBlind(capture);
                return;
            }
            bool shotgun = capture.Weapon.TryCast<Shotgun>() != null || capture.Weapon.TryCast<ShotgunSynced>() != null;
            fired = shotgun ? Math.Max(1, capture.Weapon.ArchetypeData.ShotgunBulletCount) : 1;
            // No ray reached this build, so how many pellets the shot carried is unknown. The count is
            // the best estimate available and the row is marked with a tilde rather than presented as measured.
            blind = true;
        }
        if (fired == 0) return;
        // A contact can only ever be one of the pellets that were fired, whichever way the count was reached.
        int hits = Math.Min(capture.Sample.Hits, fired);
        int weak = Math.Min(capture.Sample.WeakspotHits, hits);
        bool first = capture.Entry.Counters.ShotsFired == 0;
        if (capture.EwcFallback)
        {
#if EWC_PRESENT
            // Handed to the bridge rather than straight onto the row, so EWC's own cumulative totals
            // reconcile with these pellets by `max` the moment it starts reporting this weapon.
            EwcBridge.NoteObserved(capture.Player.Lookup, capture.Entry.Slot, fired, hits, weak);
#endif
        }
        else capture.Player.RecordAccuracy(capture.Entry, fired, hits, weak, blind);
        // Every shot counted here is the attack under way, so an explosion that follows it has something to
        // be attributed to: the blast payload names nobody, and without this the blow is dropped entirely -
        // which is exactly what happened to a 19.3-point explosion in the field.
        NoteAttack(capture.Player.Lookup, capture.Entry.Slot);
        if (first) Plugin.Logger.LogInfo($"[DIAG] Fire captured: slot={capture.Entry.Slot}, weapon={capture.Entry.Name}, class={capture.Weapon.GetType().Name}, fired={fired}, rays={rays}, hit={hits}, weak={weak}, ammoUsed={capture.ClipBefore - capture.Weapon.m_clip}{(blind ? ", no ray was seen so the row is an estimate" : string.Empty)}");
    }
    internal static void OnWeaponRay(Weapon.WeaponHitData data)
    {
        if (ActiveFire == null || !ActiveFire.Exact || ActiveFire.ProjectileObserved || ToolOwner != null || data == null) return;
        // The ray states the gear category of the weapon that cast it, which is how a modded weapon
        // with no archetype id of its own is pinned to the entry that actually fired.
        if (data.gearCategoryId != 0 && ActiveFire.Entry != null && ActiveFire.Entry.GearCategoryId == 0)
            ActiveFire.Entry.GearCategoryId = data.gearCategoryId;
        // Same rule as StatDisplay: initial pellet casts have full range; penetration casts have reduced range.
        if (data.maxRayDist >= 100f) ActiveFire.Sample.BeginPellet();
    }
    internal static void OnEnemyLimbHit(Dam_EnemyDamageLimb limb, Agent source)
    {
        if (ActiveFire == null || !ActiveFire.Exact || ActiveFire.ProjectileObserved || EwcProjectileBridge.InProjectileHit || ToolOwner != null || limb == null || source == null) return;
        var player = source.TryCast<PlayerAgent>();
        if (player?.Owner?.Lookup != ActiveFire.Player.Lookup) return;
        // A weapon whose rays this build never sees still has to score its hits. This window is the
        // proof that the shot happened, so its first contact opens the pellet that the ray which fired
        // it never got to open; later contacts fold into it exactly as they do for a seen pellet.
        if (ActiveFire.Sample.Fired == 0) ActiveFire.Sample.BeginPellet();
        ActiveFire.Sample.Contact(limb.m_type == eLimbDamageType.Weakspot);
    }
    internal static void OnRemoteSync(PlayerInventorySynced inventory)
    {
        var agent = inventory?.Owner; var owner = agent?.Owner;
        if (owner == null || !SNet.IsMaster || owner.IsLocal || owner.IsBot) return;
        int shots = Math.Max(0, agent.Sync.FireCountSync);
        PendingRemoteShots.TryGetValue(owner.Lookup, out int pending);
        shots += pending;
        if (shots == 0) return;
        InventorySlot slot = inventory.WieldedSlot;
        if (slot != InventorySlot.GearStandard && slot != InventorySlot.GearSpecial)
        { PendingRemoteShots[owner.Lookup] = shots; return; }
        var gun = inventory.WieldedItem?.TryCast<BulletWeapon>();
        if (gun == null) { PendingRemoteShots[owner.Lookup] = shots; return; }
        PendingRemoteShots.Remove(owner.Lookup);
        int pellets = gun.TryCast<ShotgunSynced>() != null ? Math.Max(1, gun.ArchetypeData.ShotgunBulletCount) : 1;
        var player = ObservePlayer(owner);
        player.RecordAccuracy(ResolveWeapon(player, owner, slot, gun), (long)shots * pellets, 0, 0, true);
        NoteAttack(owner.Lookup, (int)slot);
    }
    // Both entry points below are called straight out of a Harmony body, so a fault of ours must not become a
    // fault of the game's: the work is done in `CaptureCore` and `RecordDamageCore`, and each pair answers an
    // exception by dropping the record and keeping the game's own call intact.
    internal static DamageSample Capture(Dam_SyncedDamageBase target, float health, pAgent source, DamageKind kind, uint gearCategoryId = 0)
    {
        try { return CaptureCore(target, health, source, kind, gearCategoryId); }
        catch (Exception error) { LogImpact("damage capture", error); return default; }
    }
    internal static DamageSample CaptureEwcDamage(Dam_EnemyDamageBase target, PlayerAgent source, int sourceSlot, DamageKind kind)
    {
        if (GameStateManager.CurrentStateName != eGameStateName.InLevel || target?.Owner == null || !ShouldTrack(source?.Owner)) return default;
        var player = ObservePlayer(source.Owner);
        InventorySlot slot = (InventorySlot)sourceSlot;
        WeaponCombatStats weapon = slot == InventorySlot.GearStandard || slot == InventorySlot.GearSpecial || slot == InventorySlot.GearClass || slot == InventorySlot.GearMelee
            ? ResolveWeapon(player, source.Owner, slot) : null;
        RememberAttacker(target, source.Owner.Lookup, kind);
        return new DamageSample(target.Health, player, weapon, false, kind,
            deathScope: Deaths.Begin(target.Pointer, source.Owner.Lookup, kind, ReadSleeping(target), target.Health));
    }
    internal static void CompleteEwcDamage(Dam_EnemyDamageBase target, DamageSample sample, double recordedBefore)
    {
        if (target == null || sample.Player == null) return;
        bool client = !SNet.IsMaster && !CombatSync.HostRunsMod;
        // 1.19.65 (M2): an enemy whose damage base was not readable at its own `Setup` left a pending spawn behind.
        // This is the first client-side damage entrance that can see that base, so the spawn is consumed before any
        // ledger keyed by the pointer is read - a reused pointer never inherits the previous occupant's spent
        // balance, its death scope or its native credit. `Remove` is the gate, so this is idempotent.
        if (client) ConsumePendingSpawn(target);
        float healthAfter = target.Health;
        double residual = DamageObservation.Uncredited(sample.HealthBefore, healthAfter, recordedBefore, DamageCredit(target, sample.Player.Lookup));
        bool measured = LocalOutgoingRule.Measured(sample.HealthBefore, healthAfter);
        bool scout = target.Owner != null && target.Owner.IsScout;
        // 1.19.65: on a client with no authority over this enemy that residual is still this machine's own estimate,
        // so it is spent from the same one balance the native fallback uses - a DOT, an explosion and a shot landing
        // on one enemy can then never together credit more health than that enemy had to lose. An event whose own
        // health really moved is also the only kind of reading allowed to correct an older estimate. The host's
        // branch is untouched: there the residual is a measurement, and `DamageCreditLedger` keeps its own job of not
        // counting one event twice.
        double amount = residual;
        double withdrawn = 0;
        if (client)
            amount = Budget.Charge(target.Pointer, OwnerPointer(target), sample.Player, sample.Weapon, scout,
                sample.HealthBefore, healthAfter, residual, measured, out withdrawn);
        if (withdrawn > 0 && !_loggedWithdrawal)
        {
            _loggedWithdrawal = true;
            Plugin.Logger.LogInfo($"[DIAG] EWC damage estimate withdrawn: {withdrawn:0.0} for player={sample.Player.Name} on {sample.Weapon?.Name ?? "unknown weapon"}, because this enemy really read {sample.HealthBefore:0.0} -> {healthAfter:0.0}; the residual this event is worth ({amount:0.0}) and the {Budget.Remaining(target.Pointer):0.0} left are measured, not the older estimate.");
        }
        if (amount > 0)
        {
            sample.Player.RecordDamage(sample.Weapon, amount, false);
            EnemyCredits.Add(target.Pointer, sample.Player.Lookup, amount);
            if (scout) sample.Player.Impact.AddScoutDamage(amount, false);
        }
        CompleteDamage(target, sample, null);
    }

    private static DamageSample CaptureCore(Dam_SyncedDamageBase target, float health, pAgent source, DamageKind kind, uint gearCategoryId = 0)
    {
        Agent baseAgent = null;
        if (source.TryGet(out Agent resolved)) baseAgent = resolved;
        PlayerAgent agent = baseAgent == null ? null : baseAgent.TryCast<PlayerAgent>();
        SNet_Player owner = agent?.Owner;
        PlayerAgent toolOwner = ToolOwner;
        if (toolOwner?.Owner != null && (owner == null || toolOwner.Owner.Lookup == owner.Lookup))
        {
            agent = toolOwner;
            owner = toolOwner.Owner;
        }
        if (!ShouldTrack(owner)) return default;
        var player = ObservePlayer(owner);
        var enemy = target == null ? null : target.TryCast<Dam_EnemyDamageBase>();
        if (enemy != null) RememberAttacker(enemy, owner.Lookup, kind);
        bool sentry = ToolStatsTracker.TryCaptureSentry(owner.Lookup, out IntPtr sentryPointer, out uint sentrySearchId);
        bool remote = !sentry && !owner.IsLocal && !owner.IsBot;
        InventorySlot slot = kind == DamageKind.Melee || kind == DamageKind.Push
            ? InventorySlot.GearMelee
            : (agent.Inventory?.WieldedSlot ?? InventorySlot.None);
        if (toolOwner?.Owner?.Lookup == owner.Lookup) slot = InventorySlot.GearClass;
        WeaponCombatStats entry = ActiveFire?.Player?.Lookup == owner.Lookup && slot != InventorySlot.GearClass
            ? ActiveFire.Entry : ResolveWeapon(player, owner, slot);
        // The record names the gear category of the weapon that produced it, and that beats the slot the
        // player is holding now: a penetrating or delayed projectile can land after the swap.
        return new DamageSample(health, player, CombatPlayer.MatchDamage(player, entry, gearCategoryId), remote,
            kind, sentryPointer, sentrySearchId,
            enemy?.Owner != null ? Deaths.Begin(enemy.Pointer, owner.Lookup, kind, ReadSleeping(enemy), health) : null);
    }
    internal static void RecordDamage(Dam_SyncedDamageBase target, DamageSample sample, pBulletDamageData? bullet = null)
    {
        try { RecordDamageCore(target, sample, bullet); }
        catch (Exception error) { LogImpact("damage record", error); }
    }

    private static void RecordDamageCore(Dam_SyncedDamageBase target, DamageSample sample, pBulletDamageData? bullet = null)
    {
        // The total is recorded even when no gear could be named for the blow: a melee push, a modded tool
        // with no archetype, or a record that arrived after the weapon was swapped all still dealt damage,
        // and dropping the row entirely was how a player's total could end up lower than what they hit.
        // Only the per-weapon row needs a weapon; the fallback attribution is the player's own total, which
        // is defensible because the record named nobody else and the weapon row is left untouched.
        if (target == null) return;
        float loss = Math.Max(0, sample.HealthBefore - Math.Max(0, target.Health));
        var enemy = target.TryCast<Dam_EnemyDamageBase>();
        var victim = target.TryCast<Dam_PlayerDamageBase>();
        ulong victimLookup = victim?.Owner?.Owner?.Lookup ?? 0UL;
        if (sample.Player == null)
        {
            if (victim != null && loss > 0) ToolStatsTracker.ClearFriendlyDamage(victimLookup);
            return;
        }
        if (victim != null && victimLookup == sample.Player.Lookup)
        {
            if (loss > 0) ToolStatsTracker.ClearFriendlyDamage(victimLookup);
            return;
        }
        if (enemy == null && victim == null) return;

        // Scenery shares the damage base with enemies, but is never a hit or an impact target.
        if (enemy != null && IsScenery(target)) return;

        bool IsScenery(Dam_SyncedDamageBase receiver)
        {
            if (receiver.TryCast<Dam_StaticEnemyDamage>() != null) return true;
            var enemyBase = receiver.TryCast<Dam_EnemyDamageBase>();
            return enemyBase != null && enemyBase.Owner == null;
        }

        if (victim != null && loss > 0)
            ToolStatsTracker.ObserveFriendlyDamage(sample.Player.Lookup, victimLookup);
        if (enemy != null && sample.SentryPointer != IntPtr.Zero)
            ToolStatsTracker.CreditSentryDamage(sample.Player, sample.SentryPointer, sample.SentrySearchId, loss);
#if EWC_PRESENT
        // A weapon EWC has taken over never opens a firing window here, so this record is also the only
        // place this machine sees that the pellet it carried landed. It is held by the bridge rather than
        // written onto the row: the bridge fills in only the hits EWC did not count itself, which is what
        // lets a hit EWC never published reach the row without a hit EWC did publish being counted twice.
        // The weakspot is read from the limb the record names - the same guard the estimate below uses,
        // because a packet that names no limb must not index past the array.
        if (bullet != null && enemy != null && loss > 0f)
        {
            int limbId = bullet.Value.limbID;
            bool weak = enemy.DamageLimbs != null && limbId >= 0 && limbId < enemy.DamageLimbs.Length
                && enemy.DamageLimbs[limbId] != null && enemy.DamageLimbs[limbId].m_type == eLimbDamageType.Weakspot;
            EwcBridge.NoteHit(sample.Player.Lookup, sample.Weapon == null ? -1 : sample.Weapon.Slot, weak);
        }
#endif
        if (float.IsFinite(loss) && loss > 0)
        {
            sample.Player.RecordDamage(sample.Weapon, loss, victim != null);
            if (enemy != null) EnemyCredits.Add(enemy.Pointer, sample.Player.Lookup, loss);
            // The Scout row reports the damage that went into Scouts as its third field, so a blow
            // measured here is the same blow that row carries. Only a Scout's own health pool feeds it;
            // every other enemy leaves the figure alone, which is why a squad that killed no Scout shows
            // a dash rather than a zero.
            if (enemy != null && enemy.Owner != null && enemy.Owner.IsScout)
                sample.Player.Impact.AddScoutDamage(loss, sample.Estimated);
        }
        CompleteDamage(target, sample, null);
        // The weakspot estimate only ever fires for a weapon row, so it needs a weapon; and the limb id is
        // read defensively, because a packet that names no limb (or a negative id) must not index past the
        // array - that guard was missing and is exactly the kind of read that throws in a real match.
        if (enemy != null && bullet.HasValue && sample.Estimated && sample.Weapon != null && loss > 0)
        {
            int id = bullet.Value.limbID;
            bool isToolRow = sample.Weapon.Slot == (int)InventorySlot.GearClass;
            bool weak = enemy.DamageLimbs != null && id >= 0 && id < enemy.DamageLimbs.Length
                && enemy.DamageLimbs[id] != null && enemy.DamageLimbs[id].m_type == eLimbDamageType.Weakspot;
            // Vanilla packets have no unique pellet ID, so a remote-human damage contact is one estimated
            // hit and never a fabricated miss. A tool row deals no pellets and is left alone.
            if (!isToolRow) sample.Player.RecordAccuracy(sample.Weapon, 0, 1, weak ? 1 : 0, true);
        }
    }

    // What this path covers, and what it deliberately does not: a bullet and a melee blow enter the enemy
    // through the two limb methods hooked above. An explosion, a burn and a modded projectile enter through
    // methods of their own, and this build does not claim to measure those on such a client - it leaves them
    // out rather than crediting them to whichever weapon happened to be held. On a host that runs this mod
    // every one of them is measured where the game applied it, which is the arrangement this fallback defers to.
    //
    // The health an enemy limb's owner carries, read before the game applies a blow. Kept in one place so
    // the two limb hooks and the record path cannot disagree about which number is the enemy's own.
    internal static float LimbHealth(Dam_EnemyDamageLimb limb)
    {
        try { return limb == null || limb.m_base == null ? float.NaN : limb.m_base.Health; }
        catch (Exception) { return float.NaN; }
    }

    // The enemy agent behind one damage instance, used only to tell one occupant of a reused pointer from the
    // next. A read that is unavailable is zero and therefore never matches an occupant.
    private static IntPtr OwnerPointer(Dam_EnemyDamageBase enemy)
    {
        try { return enemy == null || enemy.Owner == null ? IntPtr.Zero : enemy.Owner.Pointer; }
        catch (Exception) { return IntPtr.Zero; }
    }

    // A blow this machine dealt that the game will not hand back to it. The host applies every blow and
    // every other machine is told the result, but a machine is not sent its own damage back: that is why a
    // client that has received no usable host data for the run used to show no damage of its own at all, and
    // with it no share and no kill of its own.
    //
    // The amount is the health the enemy lost across the call whenever that loss is readable - the damage
    // actually applied. The argument the weapon carried in (`dam`) is pre-armour and pre-precision: the
    // game multiplies it by the struck limb's resistance and by the hit's precision multiplier inside the
    // call, so a precise shot into a weakspot was being recorded as a plain body shot. Subtracting the
    // health afterwards is the damage that was applied - the same measurement the receive path takes
    // from its packet - and it is what makes the fallback agree with a host that runs this mod.
    //
    // A client whose host applies the blow is never told the result inside the same call, so the two reads can
    // come back as one number and that loss is zero. `dam` is then the only figure there is; it is read as the
    // estimate it is, and a host that does run this mod replaces it with the blow it applied, which outranks an
    // estimate by construction.
    //
    // 1.19.65: that estimate is spent from the ONE remaining-health balance this enemy instance has on this
    // machine (`EnemyHealthBudget`), not from the health of the limb that was struck and not from anything the
    // enemy's maximum health would allow. Two limbs, two weapons and the five pellets of one shotgun all draw
    // on the same balance, so the 34 a shotgun's pellets carry cannot be credited to an enemy whose readable
    // health was 20, and an enemy met with 25 of its 30 left credits 25. A health this machine reads lower than
    // that balance is a sync change it did not cause: it only lowers the balance and is never credited, while a
    // lagging higher snapshot never hands spent budget back.
    //
    // Nobody counts a blow here twice: the host never enters this path (it reads every blow through the
    // record path) and neither does a client whose host runs this mod (the host's record of that blow
    // reaches it). Only a client whose host runs nothing falls back to measuring its own blows locally,
    // which is the same split Dinorush's StatDisplay makes on MasterHasMod.
    internal static void NoteLocalOutgoing(Dam_EnemyDamageLimb limb, Agent source, float healthBefore,
        DamageKind kind, uint gearCategoryId = 0, float dam = 0f)
    {
        try
        {
            if (SNet.IsMaster || CombatSync.HostRunsMod) return;
            if (limb == null || limb.m_base == null || !float.IsFinite(healthBefore)) return;
            var agent = source == null ? null : source.TryCast<PlayerAgent>();
            SNet_Player owner = agent == null ? null : agent.Owner;
            if (owner == null || !owner.IsLocal || !ShouldTrack(owner)) return;
            // 1.19.65 (M2): an enemy whose damage base was still unreadable at its own `Setup` left a pending spawn
            // behind. This is the first client-side place that sees that base, so the spawn is consumed here, before
            // any ledger keyed by the pointer is read - otherwise a later enemy reusing the same pointer would
            // inherit the previous occupant's spent balance and its death scope.
            ConsumePendingSpawn(limb.m_base);
            float healthAfter = Math.Max(0f, limb.m_base.Health);
            var player = ObservePlayer(owner);
            InventorySlot slot = kind == DamageKind.Melee
                ? InventorySlot.GearMelee
                : (agent.Inventory == null ? InventorySlot.GearStandard : agent.Inventory.WieldedSlot);
            var entry = ResolveWeapon(player, owner, slot);
            var weapon = CombatPlayer.MatchDamage(player, entry, gearCategoryId);
            bool scout = limb.m_base.Owner != null && limb.m_base.Owner.IsScout;
            bool measured = LocalOutgoingRule.Measured(healthBefore, healthAfter);
            // 1.19.65: the figure is either the health this call really moved or, when it moved none because the host
            // applied the blow, the figure the weapon carried in - and either one is spent from the ONE balance this
            // enemy instance has left (`EnemyHealthBudget`), shared by its limbs and every weapon. A reading that
            // really moved is also a checkpoint: estimates this machine cannot support any more are taken back off the
            // board first, total / weapon row / Scout row alike, so a stale 20 that was estimated in full no longer
            // swallows the 7 that really happened.
            double amount = Budget.Charge(limb.m_base.Pointer, OwnerPointer(limb.m_base), player, weapon, scout,
                healthBefore, healthAfter, LocalOutgoingRule.Amount(healthBefore, healthAfter, dam), measured,
                out double withdrawn);
            if (withdrawn > 0 && !_loggedWithdrawal)
            {
                _loggedWithdrawal = true;
                // Two different things can lower a figure and only one of them is correction: a sync this machine did
                // not cause only lowers the balance and is never booked, while an estimate the enemy's own confirmed
                // reading cannot support is taken back. The log says which one this was.
                Plugin.Logger.LogInfo($"[DIAG] local damage estimate withdrawn: {withdrawn:0.0} on {entry?.Name ?? "unknown weapon"} for player={player.Name}, because this enemy really read {healthBefore:0.0} -> {healthAfter:0.0} and that reading cannot support it; this blow is worth {amount:0.0} and {Budget.Remaining(limb.m_base.Pointer):0.0} is what the enemy has left. Only estimates this machine made itself are taken back - a credit it measured across a call is confirmed and stays, and a health change it did not cause is never booked here in either direction.");
            }
            if (!double.IsFinite(amount) || amount <= 0) return;
            RememberAttacker(limb.m_base, owner.Lookup, kind);
            player.RecordDamage(weapon, amount, false, true);
            // The Scout row reports the damage that went into Scouts, so the same figure is put there too when the
            // enemy under this limb is one. It is an estimate for the same reason the row above is: this machine
            // measured its own blow, and a host that runs the panel overrules it.
            if (scout) player.Impact.AddScoutDamage(amount, true);
            if (!_loggedOutgoing)
            {
                _loggedOutgoing = true;
                // The claim is the one this machine can actually make: `HostRunsMod` is `SNet.IsMaster || Exchange.HostAnswered`,
                // so what was observed is that no usable host data arrived for this run - never that the host runs no panel.
                // A host that runs the panel but was never reached looks identical here, and the log must not tell the
                // two apart on this machine's behalf.
                Plugin.Logger.LogInfo($"[DIAG] local damage fallback: this machine is a client that has received no usable host data for this run (the host has not answered on this build's protocol, so it is not the authority this machine is reading), so a blow it deals itself has to be measured here - {amount:0.0} damage on {entry?.Name ?? "unknown weapon"}, from healthBefore={healthBefore:0.0}, healthAfter={healthAfter:0.0}, dam={dam:0.0}, out of the one balance this enemy instance has left ({Budget.Remaining(limb.m_base.Pointer):0.0} after this blow) - because the game never sends a machine its own damage back. The health the enemy lost across the call is used whenever it moved; when it did not, the figure the weapon carried in is used instead. Either way the figure is capped by that enemy's own remaining health, shared by all its limbs and weapons and never by its maximum health: a lower health this machine later reads is a sync change it did not cause, it only lowers the balance and is never credited here, while a lagging higher snapshot never hands spent budget back. A reading that really moved is the one thing that can also take an estimate back - see the withdrawal line. The figure is marked as an estimate, and a host that does answer this build's protocol replaces it with the blow it applied.");
            }
        }
        catch (Exception error) { LogImpact("local outgoing damage", error); }
    }

    // ---- explosions ---------------------------------------------------------------------------
    // An explosion payload names nobody: `pExplosionDamageData` has no source field (the compiler is the one
    // that told this build so), which is why StatDisplay attributes these blows through a cache filled by the
    // attack that was under way when they landed. The same cache is kept here, filled by every shot this
    // machine sees - EWC's own shot stream included - and consumed by the next explosion inside a short
    // window. With nothing to attribute it to, the blow is left out rather than credited to a guess.
    private const float ExplosionAttributionSeconds = 3f;
    private static ulong _attackLookup;
    private static int _attackSlot = -1;
    private static float _attackTime = float.NegativeInfinity;
    private static bool _loggedExplosion, _loggedExplosionUnattributed;

    // The attack that just happened, remembered so the explosion it may set off can be attributed to it.
    internal static void NoteAttack(ulong lookup, int slot)
    {
        if (lookup == 0UL || slot < 0) return;
        _attackLookup = lookup; _attackSlot = slot; _attackTime = Time.time;
    }

    internal static DamageSample CaptureExplosion(Dam_SyncedDamageBase target)
    {
        float before = target == null ? float.NaN : target.Health;
        var enemy = target?.TryCast<Dam_EnemyDamageBase>();
        ulong lookup = ToolStatsTracker.IsMineBlastActive ? ToolStatsTracker.ActiveMineBlastOwner : _attackLookup;
        bool known = ToolStatsTracker.IsMineBlastActive || Time.time - _attackTime <= ExplosionAttributionSeconds;
        EnemyDeathLedger.Scope scope = null;
        if (known && enemy?.Owner != null && Store.TryGet(lookup, out _) && SNet.IsMaster)
        {
            RememberAttacker(enemy, lookup, DamageKind.Explosion);
            scope = Deaths.Begin(enemy.Pointer, lookup, DamageKind.Explosion, ReadSleeping(enemy), before);
        }
        return new DamageSample(before, null, null, false, DamageKind.Explosion, deathScope: scope);
    }

    internal static void RecordExplosion(Dam_SyncedDamageBase target, float healthBefore)
    {
        try
        {
            if (target == null) return;
            // The blow is the health that actually left the limb, measured before and after the game applied
            // it - the same measurement the bullet path takes from its own record.
            float loss = Math.Max(0f, healthBefore - Math.Max(0f, target.Health));
            if (!float.IsFinite(loss) || loss <= 0f) return;
            if (target.TryCast<Dam_StaticEnemyDamage>() != null) return;
            var enemy = target.TryCast<Dam_EnemyDamageBase>();
            if (enemy != null && enemy.Owner == null) return;
            if (enemy == null && target.TryCast<Dam_PlayerDamageBase>() == null) return;

            bool mineBlast = ToolStatsTracker.IsMineBlastActive;
            ulong mineOwner = ToolStatsTracker.ActiveMineBlastOwner;
            if (mineBlast && mineOwner == 0UL)
            {
                if (!_loggedExplosionUnattributed)
                {
                    _loggedExplosionUnattributed = true;
                    Plugin.Logger.LogInfo("[DIAG] mine explosive damage had no core owner or tracked equipment identity; it was left unassigned and the recent-attacker fallback was suppressed.");
                }
                return;
            }
            ulong attackerLookup = mineBlast ? mineOwner : _attackLookup;
            int attackSlot = mineBlast ? (int)InventorySlot.GearClass : _attackSlot;
            bool attributed = mineBlast || (attackSlot >= 0 && Time.time - _attackTime <= ExplosionAttributionSeconds);
            if (!attributed)
            {
                if (!_loggedExplosionUnattributed)
                {
                    _loggedExplosionUnattributed = true;
                    Plugin.Logger.LogInfo($"[DIAG] explosive damage of {loss:0.0} arrived with no attack in the last {ExplosionAttributionSeconds:0}s to attribute it to, so it is left out rather than credited to the wrong player; the game's explosion payload names no owner, and a weapon whose blast follows its own shot is the case this is for.");
                }
                return;
            }
            if (!Store.TryGet(attackerLookup, out CombatPlayer player) || player == null) return;
            if (!SNet.TryGetPlayer(attackerLookup, out SNet_Player attacker) || attacker == null) return;
            var victim = target.TryCast<Dam_PlayerDamageBase>();
            ulong victimLookup = victim?.Owner?.Owner?.Lookup ?? 0UL;
            if (victim != null) ToolStatsTracker.ObserveFriendlyDamage(attackerLookup, victimLookup);
            var weapon = mineBlast && !ToolStatsTracker.IsEquipmentMineBlast
                ? null
                : ResolveWeapon(player, attacker, (InventorySlot)attackSlot);
            RememberAttacker(enemy, attackerLookup, DamageKind.Explosion);
            player.RecordDamage(weapon, loss, victim != null);
            if (enemy != null) EnemyCredits.Add(enemy.Pointer, player.Lookup, loss);
            // An explosion that went into a Scout is the same blow the Scout row reports, so it feeds that
            // figure too - the blast of a mine and the shot of a rifle are the same kind of evidence.
            if (enemy != null && enemy.Owner != null && enemy.Owner.IsScout)
                player.Impact.AddScoutDamage(loss);
            if (enemy != null && mineBlast && ToolStatsTracker.IsEquipmentMineBlast) ToolStatsTracker.CreditMineDamage(attackerLookup, loss);
            if (!_loggedExplosion)
            {
                _loggedExplosion = true;
                Plugin.Logger.LogInfo($"[DIAG] explosive damage measured: {loss:0.0} credited to {player.Name} on {weapon?.Name}, source={(mineBlast ? "mine-core-owner" : "recent attack")}, equipmentMine={ToolStatsTracker.IsEquipmentMineBlast}.");
            }
        }
        catch (Exception error) { LogImpact("explosion", error); }
    }

    // ---- EWC's other streams ----------------------------------------------------------------
    // The accuracy API carries a shot and a hit for most weapons EWC manages, but not for all of them: a
    // projectile weapon's shots come from `FireShotAPI.PreShotFired` and its hits from
    // `ProjectileAPI.OnProjectileHit`, and a DOT / explosive / shrapnel weapon's damage comes from
    // `DamageAPI.PreLocal*Damage`. StatDisplay reads all of them, and a weapon with none of them counted is
    // on the row as `0/0/0` - which is exactly what a 涡轮电浆枪 showed while it was being used.
    internal static void NoteEwcShot(ulong lookup, int slot)
    {
        NoteAttack(lookup, slot);
#if EWC_PRESENT
        EwcBridge.NoteShot(lookup, slot);
#endif
    }

    internal static void NoteEwcDamage(ulong lookup, int slot, float amount)
    {
        try
        {
            // The same split StatDisplay makes on `MasterHasMod`: while a host that runs this mod is
            // measuring everybody, the local machine must not count its own blows as well, or every blow
            // would be on the board twice. With no such host, this is the only place an EWC weapon's
            // explosive damage is ever seen.
            if (CombatSync.HostRunsMod) return;
            if (lookup == 0UL || !(amount > 0f) || !float.IsFinite(amount)) return;
            if (!Store.TryGet(lookup, out CombatPlayer player) || player == null) return;
            if (!SNet.TryGetPlayer(lookup, out SNet_Player ewcOwner) || ewcOwner == null) return;
            var weapon = ResolveWeapon(player, ewcOwner, (InventorySlot)slot);
            player.RecordDamage(weapon, amount, false, true);
            if (!_loggedEwcDamage)
            {
                _loggedEwcDamage = true;
                Plugin.Logger.LogInfo($"[DIAG] EWC damage stream credited: {amount:0.0} on {weapon?.Name} for a weapon whose damage does not travel the game's bullet path; it is marked as an estimate and a host that runs this mod outranks it.");
            }
        }
        catch (Exception error) { LogImpact("EWC damage stream", error); }
    }

    // ---- The influence board's ledger --------------------------------------------------------
    // The last player to damage each enemy, so a death can be credited to whoever caused it. The
    // key is the enemy's native damage base pointer, which is what both death hooks can reach.

    private static readonly Dictionary<IntPtr, ulong> LastAttacker = new Dictionary<IntPtr, ulong>();
    private static readonly Dictionary<IntPtr, DamageKind> LastDamageKind = new Dictionary<IntPtr, DamageKind>();
    private static readonly EnemyDeathLedger Deaths = new EnemyDeathLedger();
    private static readonly PendingSpawns PendingEnemySpawns = new PendingSpawns();
    // The frame each enemy last had an attack broken in, so the push and the damage it comes with
    // reported inside one frame still count as the one interrupt they are.
    private static readonly Dictionary<IntPtr, int> InterruptedFrame = new Dictionary<IntPtr, int>();
    private static readonly Dictionary<ulong, bool> Alive = new Dictionary<ulong, bool>();
    private static readonly Dictionary<ulong, ScanProbe> ScanLedger = new Dictionary<ulong, ScanProbe>();
    // 1.19.62: the expedition's one reveal namespace, and therefore the squad's one answer to "who put this enemy
    // on the bio tracker first". Every probe below is handed its session from this factory, so humans and bots share
    // the same revealed set: one enemy instance is a first mark for exactly one sweep in the squad. What is per
    // player stays per player - the sweep window behind `ScanMarks`, the cooldown session behind `ScanUses` and the
    // bot-callback throttle - so exclusive first marks never touch a cumulative figure or a scan count.
    private static readonly SquadScanLedger ScanSquad = new SquadScanLedger();
    // 1.19.62: the round's created-enemy figure. One ledger for the whole squad, fed by each enemy's own successful
    // `EnemyAgent.Setup` edge (natural and command spawns alike), never by a hit or a death, and never per player.
    // See `SquadSpawns.cs` for the rules a machine has to satisfy before it may call that number the squad's.
    private static readonly SquadSpawnLedger Spawns = new SquadSpawnLedger();
    // The one figure the success and the failure settlement read: the same global ledger, resolved against this
    // machine's role, so neither page ever shows a copy per player and neither invents a number.
    internal static SpawnTotal SpawnTotal => Spawns.Total(SNet.IsMaster);
    // The bot whose scan action is currently on the stack: its call to the static BotTag arrives
    // inside that window, which is what makes the attribution exact rather than a guess. Since 1.19.42 the
    // scope also carries the action's own scan start, so the one scan this window belongs to is named: the
    // same action instance really scanning twice publishes two different starts.
    [ThreadStatic] private static BotScanScope _botScan;
    private static int NearbyBotCandidates;
    private static float NearbyBotDistance = float.PositiveInfinity;
    private static bool _loneSurvivorAwarded;
    private static bool _loggedStagger, _loggedKill, _loggedReviveState, _loggedReviveUnmatched, _loggedScan, _loggedScanRepeat, _loggedDown, _loggedLone, _loggedAttackState, _loggedBotScan, _loggedBotScanUnresolved, _loggedBotAction, _loggedBotScanUse, _loggedBotScanStart, _loggedOutgoing, _loggedEwcDamage, _loggedWithdrawal;
    // Which weapon classes this build has already reported a firing window for. One line per class is what
    // turns "this weapon's shots are missing" into a decision: either the class reaches a hooked `Fire` and
    // the window is here (so the figures are missing for another reason), or it never appears - which means
    // its firing path is not one of the four this build hooks and the weapon has to be found another way.
    private static readonly HashSet<string> FireClassesSeen = new HashSet<string>();

    // A stagger interrupt is damage that lands while the enemy's attack is under way and not yet
    // finished, and that stops it. The enemy's own state machine says an attack was under way; the
    // hit react says the hit broke it. A kill is never an interrupt: the react that reports one is
    // rejected, and so is any blow that takes the last of the enemy's health, because a corpse that
    // stopped moving was not interrupted. A melee push deals no damage at all, so it is judged on
    // the attack state alone: that is what the push flag is for, and it is the only thing a push
    // can be judged on.
    internal static void CreditStagger(Dam_EnemyDamageBase enemy, float damage, Agent source, ES_HitreactType hitreact, bool push = false)
    {
        try
        {
            if (enemy == null || source == null) return;
            if (!push && !(damage > 0)) return;
            var agent = source.TryCast<PlayerAgent>();
            if (!ShouldTrack(agent?.Owner)) return;
            bool needsAttack = Plugin.StaggerNeedsAttack == null || Plugin.StaggerNeedsAttack.Value;
            bool attacking = IsAttackUnderWay(enemy);
            float health = ReadHealth(enemy);
            // A push lands no damage, so the attack it broke is the only evidence there is.
            if (push && !attacking) return;
            if (!StaggerRule.IsInterrupt(KindOf(hitreact), damage, health, attacking, needsAttack, push)) return;
            // A push and the damage reported alongside it can both arrive inside one frame, and one
            // broken attack is one interrupt, so the same enemy is only counted once per frame.
            int frame = FrameNumber();
            if (frame >= 0 && enemy.Pointer != IntPtr.Zero)
            {
                if (InterruptedFrame.TryGetValue(enemy.Pointer, out int last) && last == frame) return;
                InterruptedFrame[enemy.Pointer] = frame;
            }
            var player = ObservePlayer(agent.Owner);
            player.Impact.CreditInterrupt();
            if (!_loggedStagger)
            {
                _loggedStagger = true;
                Plugin.Logger.LogInfo($"[DIAG] stagger credited: {player.Name} interrupted an attack that was already under way with {damage:0.0} damage (hitreact={hitreact}, push={push}, enemy health {health:0.0}).");
            }
        }
        catch (Exception error) { LogImpact("stagger", error); }
    }

    // The frame the interrupt landed in. Unity is absent from the headless suite, so a frame
    // number that cannot be read simply switches the de-duplication off.
    private static int FrameNumber()
    {
        try { return Time.frameCount; } catch { return -1; }
    }
    // The melee push. The game sends it down its own path, so it never reaches the damage hook, and
    // it deals no damage at all: what it breaks is the attack. The enemy's attack state is read here,
    // before the push is processed, and that state is what decides the interrupt.
    internal static void CreditPushInterrupt(Dam_EnemyDamageBase enemy, pAgent source)
    {
        try
        {
            if (enemy == null || !source.TryGet(out Agent baseAgent)) return;
            // The game applies a light or heavy react of its own inside the push it is about to
            // process; either way the attack is what broke, so the react handed to the rule is the
            // one a push produces.
            CreditStagger(enemy, 0f, baseAgent, ES_HitreactType.Light, true);
        }
        catch (Exception error) { LogImpact("push interrupt", error); }
    }


    // The game's own react type, mapped onto the plain rule the headless suite asserts. ToDeath and
    // InstantRagdollDeath are the enemy dying, not the enemy being interrupted.
    private static StaggerKind KindOf(ES_HitreactType hitreact)
    {
        if (hitreact == ES_HitreactType.Light) return StaggerKind.Light;
        if (hitreact == ES_HitreactType.Heavy) return StaggerKind.Heavy;
        if (hitreact == ES_HitreactType.ToDeath || hitreact == ES_HitreactType.InstantRagdollDeath) return StaggerKind.Lethal;
        return StaggerKind.None;
    }

    // How much health the enemy had *before* this blow: the hook runs as the damage is processed, so
    // a blow that takes it to zero is the kill that ends the fight rather than an interrupt.
    internal static float ReadHealth(Dam_EnemyDamageBase enemy)
    {
        try
        {
            float health = enemy.Health;
            return float.IsFinite(health) ? health : 0f;
        }
        catch { return 0f; }
    }

    private static void RememberAttacker(Dam_EnemyDamageBase enemy, ulong lookup, DamageKind cause)
    {
        if (enemy == null || enemy.Pointer == IntPtr.Zero || lookup == 0UL) return;
        ResolvePendingEnemySpawn(enemy);
        LastAttacker[enemy.Pointer] = lookup;
        if (cause != DamageKind.Unknown) LastDamageKind[enemy.Pointer] = cause;
    }

    private static bool IsAttackUnderWay(Dam_EnemyDamageBase enemy)
    {
        try
        {
            var locomotion = enemy.Owner != null ? enemy.Owner.Locomotion : null;
            if (locomotion == null)
            {
                if (!_loggedAttackState)
                {
                    _loggedAttackState = true;
                    Plugin.Logger.LogInfo("[DIAG] the enemy's locomotion could not be read, so no hit react is credited as a stagger; the row stays empty rather than counting hits that interrupted nothing.");
                }
                return false;
            }
            return locomotion.IsAttacking() || locomotion.IsChargingAttack();
        }
        catch (Exception error) { LogImpact("attack state", error); return false; }
    }

    internal static void CreditKill(Dam_EnemyDamageBase enemy)
    {
        try
        {
            if (enemy == null) return;
            EnemyAgent owner = enemy.Owner;
            ResolvePendingEnemySpawn(enemy);
            CreditKill(enemy.Pointer, enemy.HealthMax, owner != null && owner.IsScout);
        }
        catch (Exception error) { LogImpact("kill", error); }
    }

    internal static void CreditKill(EnemyAgent agent)
    {
        try
        {
            if (agent == null) return;
            Dam_EnemyDamageBase damage = agent.Damage;
            if (damage != null) { ResolvePendingEnemySpawn(damage); CreditKill(damage.Pointer, damage.HealthMax, agent.IsScout); }
            else CreditKill(agent.Pointer, 0, agent.IsScout);
        }
        catch (Exception error) { LogImpact("kill", error); }
    }

    // One enemy dies once. Both death hooks land here, and the pointer ledger is what keeps the
    // second one from counting the same corpse twice.
    private static void CreditKill(IntPtr enemy, float healthMax, bool isScout)
    {
        if (enemy == IntPtr.Zero || !SNet.IsMaster) return;
        if (Deaths.IsReceiving(enemy)) return; // The receive postfix owns this death, with its pre-hit state.
        if (!LastAttacker.TryGetValue(enemy, out ulong attacker) || !Deaths.TryClaimUnscoped(enemy)) return;
        AwardKill(enemy, attacker, healthMax, isScout, DamageKind.Unknown, null);
    }

    private static void AwardKill(IntPtr enemy, ulong attacker, float healthMax, bool isScout, DamageKind cause, bool? sleeping)
    {
        LastAttacker.Remove(enemy); LastDamageKind.Remove(enemy);
        if (!Store.TryGet(attacker, out CombatPlayer player)) return;
        double minor = Plugin.KillMinorMaxHealth != null ? Plugin.KillMinorMaxHealth.Value : KillClassifier.DefaultMinorMaxHealth;
        double elite = Plugin.KillEliteMaxHealth != null ? Plugin.KillEliteMaxHealth.Value : KillClassifier.DefaultEliteMaxHealth;
        KillTier tier = KillClassifier.Classify(healthMax, minor, elite);
        KillClassifier.Credit(player.Impact, tier, cause, isScout, sleeping);
        if (!_loggedKill)
        {
            _loggedKill = true;
            Plugin.Logger.LogInfo($"[DIAG] kill credited: {player.Name} took down a {tier} enemy (cause={cause}, scout={isScout}, healthMax={healthMax:0}, thresholds {minor:0}/{elite:0}).");
        }
    }


    internal static void CompleteDamage(Dam_SyncedDamageBase target, DamageSample sample, Exception error)
    {
        var scope = sample.DeathScope;
        if (scope == null || scope.Closed) return;
        try
        {
            var enemy = target?.TryCast<Dam_EnemyDamageBase>();
            if (Deaths.Complete(scope, target == null ? float.NaN : target.Health, error == null) && enemy != null && SNet.IsMaster)
                AwardKill(scope.Enemy, scope.Attacker, enemy.HealthMax, enemy.Owner != null && enemy.Owner.IsScout, scope.Cause, scope.Sleeping);
        }
        catch (Exception captureError) { Deaths.Complete(scope, float.NaN, false); LogImpact("death scope", captureError); }
    }

    internal static void ObserveEnemySpawn(EnemyAgent agent)
    {
        try
        {
            if (agent == null) return;
            // 1.19.62 (review of the previous round): the spawn reset the death accounting depends on comes FIRST and has
            // its own guard, exactly as it always had. `EnemyAgent.Setup` is also where a death is prepared
            // (`Deaths.Spawn` / `EnemyCredits.Spawn` clear the previous occupant of this pointer), so anything added
            // here must never be able to skip it: an interop read that throws would otherwise cost an enemy its fresh
            // death ledger entry for the rest of the run.
            PendingEnemySpawns.Add(agent.Pointer);
            if (agent.Damage != null) ResolvePendingEnemySpawn(agent.Damage);
        }
        catch (Exception error) { LogImpact("enemy spawn", error); }
        // 1.19.62: the round's created-enemy figure, taken at the enemy's own successful `Setup` edge - the one
        // lifecycle every created enemy passes, natural and command spawn alike, whatever happens to it later. A preset
        // enemy the level sets up before its first in-level frame counts exactly like a wave spawned twenty minutes
        // later: what decides whether the figure may speak for the squad is the round edge this machine saw, not the
        // state at this instant. It is deliberately not derived from a hit, a death, a scan or survival, and it is
        // isolated from the block above: the identity read is an interop call, an unreadable one counts nothing
        // (identity 0), and a failure here can never touch the pending-spawn set the death path reads.
        try
        {
            int identity = 0;
            try { identity = agent.GetInstanceID(); }
            catch (Exception error) { LogImpact("enemy spawn identity", error); }
            Spawns.Observe(SNet.IsMaster, identity);
        }
        catch (Exception error) { LogImpact("enemy spawn figure", error); }
    }

    private static void ResolvePendingEnemySpawn(Dam_EnemyDamageBase damage)
    {
        if (damage?.Owner == null || !PendingEnemySpawns.Take(damage.Owner.Pointer)) return;
        ResetDamageInstance(damage.Pointer);
    }

    // 1.19.65 (M2): an enemy whose damage base (or its owner) was still unreadable at its own `Setup` leaves its
    // spawn pending for ever, so the first place that *can* see that base has to consume it - otherwise a later
    // enemy that reuses the same damage or owner pointer inherits the previous occupant's spent balance, its death
    // scope and its native credit. Only the client-side damage entrances call this; `Remove` is the idempotent
    // gate, so the second caller of the same spawn finds nothing pending and does nothing at all.
    private static void ConsumePendingSpawn(Dam_EnemyDamageBase damage)
    {
        if (damage?.Owner == null || !PendingEnemySpawns.Take(damage.Owner.Pointer)) return;
        ResetDamageInstance(damage.Pointer);
    }

    // Everything one damage pointer holds for one occupant. Cleared only on that pointer's own spawn edge.
    private static void ResetDamageInstance(IntPtr pointer)
    {
        Deaths.Spawn(pointer); EnemyCredits.Spawn(pointer); Budget.Spawn(pointer); LastAttacker.Remove(pointer); LastDamageKind.Remove(pointer); InterruptedFrame.Remove(pointer);
    }

    private static bool? ReadSleeping(Dam_EnemyDamageBase enemy)
    {
        try
        {
            ScoutWitness.Refresh(enemy);
            var locomotion = enemy?.Owner?.Locomotion;
            if (locomotion == null) return null;
            var state = locomotion.CurrentStateEnum;
            if (state == ES_StateEnum.None || state == ES_StateEnum.Dead || state == ES_StateEnum.DeadFlyer || state == ES_StateEnum.DeadSquidBoss) return null;
            return state == ES_StateEnum.Hibernate;
        }
        catch { return null; }
    }
    internal static void ObserveReviveState(Interact_Revive.pPlayerReviveState state)
    {
        try
        {
            if (!state.TargetPlayer.TryGet(out PlayerAgent target) || target == null || target.Owner == null
                || !state.SourcePlayer.TryGet(out PlayerAgent source) || source == null || source.Owner == null)
            {
                if (!_loggedReviveState)
                {
                    _loggedReviveState = true;
                    Plugin.Logger.LogWarning("[DIAG] revive source/target state could not resolve to slotted PlayerAgents; rescue attribution remains unknown.");
                }
                return;
            }

            SNet_Player targetOwner = target.Owner;
            SNet_Player sourceOwner = source.Owner;
            if (!targetOwner.IsInSlot || !sourceOwner.IsInSlot) return;

            double now = UnityEngine.Time.realtimeSinceStartup;
            if (!ReviveLedger.Observe(targetOwner.Lookup, sourceOwner.Lookup, state.State, now)) return;
            if (ShouldTrack(sourceOwner))
                Plugin.Logger.LogInfo($"[DIAG] revive state observed: target={targetOwner.GetName()}; reviver={sourceOwner.GetName()}; active={state.State};");
        }
        catch (Exception error) { LogImpact("revive-state", error); }
    }

    internal static void ConfirmRevive(PLOC_Downed downed)
    {
        try
        {
            SNet_Player target = downed?.m_owner?.Owner;
            if (target == null || !target.IsInSlot) return;
            if (!ReviveLedger.TryTakeSuccessfulRevive(target.Lookup, UnityEngine.Time.realtimeSinceStartup, out ulong reviverLookup))
            {
                if (!_loggedReviveUnmatched)
                {
                    _loggedReviveUnmatched = true;
                    // Both endpoints may be remote: source ownership decides credit only after the replicated pair
                    // and successful completion match. Missing pairs never name a guessed reviver.
                    bool hostRow = SNet.IsMaster;
                    string unmatched = $"[DIAG] successful revive completion observed for target={target.GetName()}, but no matching replicated reviver state was available; not assigning the credit to a guessed player. "
                        + $"role={(hostRow ? "host" : "client")}; reviverStateRecordedLocally=false; rowOwner={(hostRow ? "this-machine" : "host-snapshot")}; "
                        + "reviverReadableLocally=false: Interact_Revive keeps no source field and pSetHealthData carries health only; "
                        + $"creditPath={(hostRow ? "reviver-state" : "host snapshot Revives")}.";
                    if (hostRow) Plugin.Logger.LogWarning(unmatched); else Plugin.Logger.LogInfo(unmatched);
                }
                return;
            }

            if (!SNet.TryGetPlayer(reviverLookup, out SNet_Player reviver)) return;
            // A remote candidate can replace a local attempt without giving this client ownership of its row.
            if (!ShouldTrack(reviver)) return;
            CombatPlayer player = ObservePlayer(reviver);
            player.Impact.CreditRevive();
            Plugin.Logger.LogInfo($"[DIAG] revive credited: reviver={player.Name}; target={target.GetName()}; total={player.Impact.Revives}; source=reviver-state+PLOC_Downed.OnPlayerRevived.");
        }
        catch (Exception error) { LogImpact("revive-complete", error); }
    }

    // Everything the board scores that no event reports: who is still standing, who is the last one
    // standing, and what the bio tracker has revealed. Polled from the per-frame state observer, so
    // no patch is spent on a signal that only ever needs comparing with its own previous value.
    internal static void Tick()
    {
        try
        {
            // 1.19.62: the created-enemy figure's role is sampled on every in-level frame - before anything else in the
            // frame can refuse - so a host that was demoted and promoted again inside one round cannot pass a start/end
            // comparison and publish a complete total it did not have. The figure itself begins at the expedition edge
            // (`Spawns.Begin` in `ObserveState`), never here: `Generating -> InLevel` is the same round and clears nothing.
            Spawns.NoteRole(SNet.IsMaster);
            var agents = PlayerManager.PlayerAgentsInLevel;
            if (agents == null) return;
            int alive = 0;
            CombatPlayer lastStanding = null;
            foreach (var agent in agents)
            {
                if (agent == null) continue;
                var owner = agent.Owner;
                if (!ShouldTrack(owner)) continue;
                var player = ObservePlayer(owner);
                bool standing;
                try { standing = agent.m_alive; } catch { continue; }
                if (Alive.TryGetValue(player.Lookup, out bool previous) && previous && !standing)
                {
                    player.Impact.CreditDown();
                    if (ToolStatsTracker.TryCreditFriendlyFireDown(player.Lookup, previous, standing, out ulong attackerLookup)
                        && Store.TryGet(attackerLookup, out CombatPlayer attacker))
                        attacker.Impact.CreditFriendlyFireDeath();
                    if (!_loggedDown)
                    {
                        _loggedDown = true;
                        Plugin.Logger.LogInfo($"[DIAG] down credited: {player.Name} went down (m_alive true -> false).");
                    }
                }
                Alive[player.Lookup] = standing;
                if (standing) { alive++; lastStanding = player; }
            }
            if (SNet.IsMaster && alive == 1 && !_loneSurvivorAwarded && Store.Players != null)
            {
                int tracked = 0;
                foreach (var player in Store.Players) tracked++;
                if (tracked >= 2 && lastStanding != null)
                {
                    _loneSurvivorAwarded = true;
                    lastStanding.Impact.MarkLoneSurvivor();
                    if (!_loggedLone)
                    {
                        _loggedLone = true;
                        Plugin.Logger.LogInfo($"[DIAG] lone survivor credited: {lastStanding.Name} was the last one standing.");
                    }
                }
            }
            ObserveScans();
        }
        catch (Exception error) { LogImpact("tick", error); }
    }

    // ---- The scan ledger ---------------------------------------------------------------------
    private sealed class ScanProbe
    {
        public bool Announced, RemoteAnnounced;
        // 1.19.62: one line per player and expedition when this host's estimate first appears - never one per
        // mark or per enemy.
        public bool EstimateAnnounced;
        // Confirmation diagnostics are per player and per expedition.
        public readonly ScanSession Session;
        // 1.19.62: the session is handed in, never built here, because which reveal namespace it belongs to is the
        // whole question: the tracker passes the expedition's one shared session factory (`SquadScanLedger`), while a
        // single-scanner test builds `new ScanSession()` and keeps its own namespace.
        public ScanProbe(ScanSession session) { Session = session; }
    }

    private static ScanProbe Ledger(SNet_Player owner)
    {
        if (!ScanLedger.TryGetValue(owner.Lookup, out ScanProbe probe))
        {
            // 1.19.62: every probe takes its session from the expedition's one factory, so the revealed set - and
            // therefore the first mark - is the squad's, humans and bots together. This is the whole fix for the
            // recorded archive that read 78 first marks off a round of 21 created enemies: this call used to build a
            // fresh reveal set per player, which made every tracker "first" in a namespace of its own.
            //
            // A session that is only this scanner's is still what a single-scanner test builds (`new ScanSession()`),
            // and it keeps exactly the semantics such a session always had.
            probe = new ScanProbe(ScanSquad.For(owner.Lookup)); ScanLedger[owner.Lookup] = probe;
        }
        return probe;
    }

    private static void ObserveScans()
    {
        ScanTagPipeline.Drain();
        if (SNet.Current == null || SNet.Slots?.SlottedPlayers == null) return;
        foreach (var owner in SNet.Slots.SlottedPlayers)
        {
            if (!ShouldTrack(owner) || owner.IsBot) continue; // Bots use only BotTag, never both paths.
            EnemyScanner scanner = ResolveScanner(owner);
            if (scanner != null) ObserveHumanScan(scanner);
            else Ledger(owner).Session.ObserveTagging(false, null);
        }
    }

    // The scanner instance names the local human while native code submits each tag request.
    internal sealed class HumanScanCapture
    {
        internal EnemyScanner Scanner;
        internal ulong Lookup;
        internal HumanScanCapture Previous;
    }
    [ThreadStatic] private static HumanScanCapture _humanScan;
    internal static ulong ActiveHumanScanLookup => _humanScan?.Lookup ?? 0UL;

    internal static HumanScanCapture BeginHumanScan(EnemyScanner scanner)
    {
        try
        {
            var owner = scanner?.Owner?.Owner;
            if (!ShouldTrack(owner) || owner.IsBot || !owner.IsLocal
                || GameStateManager.CurrentStateName != eGameStateName.InLevel) return null;
            ObserveHumanScan(scanner);
            // The native tag-progress call is one attempt, and it brackets a confirmed mark even when the
            // scanner's own `m_tagging` flag reads false while the confirmation is drained on the main
            // thread. Opening the window here is what lets the cumulative count differ from the unique
            // first-mark count instead of being pinned to it. The owner resolved above is reused; the
            // capture, the lookup and the previous bracket are left exactly as they were.
            Ledger(owner).Session.ObserveTagging(scanner.m_tagging, null, true);
            var capture = new HumanScanCapture { Scanner = scanner, Lookup = owner.Lookup, Previous = _humanScan };
            _humanScan = capture;
            return capture;
        }
        catch (Exception error) { LogImpact("scan begin", error); return null; }
    }
    internal static void EndHumanScan(HumanScanCapture capture, Exception error)
    {
        if (capture == null) return;
        try { if (error == null) ObserveHumanScan(capture.Scanner); }
        catch (Exception captureError) { LogImpact("scan end", captureError); }
        finally { if (ReferenceEquals(_humanScan, capture)) _humanScan = capture.Previous; }
    }

    // Invoked only from the main-thread confirmation drain; no remote scanner object is required. `receivedAt` is
    // the monotonic stamp this confirmation was *captured* with (`ScanTagPipeline`'s `Event.Time`), never the time
    // this drain got round to it: a backlogged drain must neither spread one sweep's marks apart nor make the gap
    // between two real sweeps look shorter than it was.
    internal static void CreditExecutedScanTag(EnemyAgent enemy, ulong lookup, bool estimated, double receivedAt)
    {
        if (GameStateManager.CurrentStateName != eGameStateName.InLevel || enemy == null || !enemy.IsTagged) return;
        var players = SNet.Slots?.SlottedPlayers;
        if (players == null) return;
        foreach (var owner in players)
        {
            if (!ShouldTrack(owner) || owner.IsBot || owner.Lookup != lookup) continue;
            var player = ObservePlayer(owner);
            ScanProbe probe = Ledger(owner);
            long before = player.Impact.ScanMarks;
            // One confirmation, one window, whoever the player is. A guest's row on the host has no scanner
            // instance to read a tag flag or a bracket from (`BeginHumanScan` runs for the local human only),
            // so without this its window stayed open for the whole expedition and the cumulative figure was
            // pinned to the first-mark figure - two columns printing the same number off one event source.
            probe.Session.BeginConfirmWindow();
            ScanMark mark = probe.Session.ObserveConfirmed(enemy.GetInstanceID(), player.Impact, true);
            // A remote request identifies its player, but not the scanner's sweep/empty-attempt edges.
            estimated |= !owner.IsLocal;
            player.Impact.ScanEstimated |= estimated;
            // 1.19.62: the host's own estimate of scan batches, kept beside the measured figures and never inside
            // them. `CreditConfirmed` owns the whole gate: only a confirmed mark of *another* machine's human feeds it,
            // a `First` and a `Repeat` both open the next batch (a sweep of enemies the squad already knows still cost
            // a scan), and a mark the session rejected names no enemy, so it feeds nothing.
            if (ScanRoundEstimates.CreditConfirmed(SNet.IsMaster, owner.IsLocal, owner.IsBot, lookup, mark, receivedAt)
                && ScanRoundEstimates.TryRounds(lookup, out long rounds) && !probe.EstimateAnnounced)
            {
                probe.EstimateAnnounced = true;
                Plugin.Logger?.LogInfo($"[DIAG] scan rounds estimated: {player.Name}; estimate={rounds}; measured={(player.Impact.ScanUsesKnown ? player.Impact.ScanUses.ToString() : "—")}; one round is one short cluster of that player's confirmed marks, never the native activation count.");
            }
            if (player.Impact.ScanMarks > before && !probe.RemoteAnnounced)
            {
                probe.RemoteAnnounced = true;
                Plugin.Logger?.LogInfo($"[DIAG] scan execution confirmed: owner={lookup}; first={player.Impact.ScanFirstMarks}; marks={player.Impact.ScanMarks}; estimated={estimated}; source=native tag request/execution.");
            }
            return;
        }
    }

    // 1.19.41: the scanner's own native cooldown, read off the instance the holder has equipped. `null` for a
    // `m_recharging` and `m_rechargeStartTime` are interop *properties* on `Gear.EnemyScanner` (verified in the
    // Survival profile's own `Modules-ASM.dll`: `get_m_recharging`, `get_m_rechargeStartTime`), which is why they are
    // read directly here and never looked up as fields - a field lookup silently returns null on a property, and an
    // always-unreadable cooldown would have left every scan count "unknown" for the rest of the expedition. Each
    // read is guarded on its own: a transient interop failure is an unusable sample, not an idle edge.
    internal static void ObserveCooldown(EnemyScanner scanner, ScanSession session, ImpactCounters counters)
    {
        if (scanner == null || session == null) return;
        bool rechargingKnown = false, recharging = false, startKnown = false;
        float start = float.NaN;
        try { recharging = scanner.m_recharging; rechargingKnown = true; }
        catch (Exception error) { LogImpact("scanner recharging", error); }
        if (rechargingKnown)
        {
            try { start = scanner.m_rechargeStartTime; startKnown = float.IsFinite(start); }
            catch (Exception error) { LogImpact("scanner recharge start", error); }
        }
        session.ObserveRecharge(rechargingKnown, recharging, startKnown, start, counters);
    }

    internal static void ObserveHumanScan(EnemyScanner scanner)
    {
        try
        {
            if (GameStateManager.CurrentStateName != eGameStateName.InLevel || scanner == null) return;
            SNet_Player owner = scanner.Owner?.Owner;
            if (!ShouldTrack(owner) || owner.IsBot) return;
            ScanProbe probe = Ledger(owner);
            var player = ObservePlayer(owner);
            // The tag flag opens the sweep window the cumulative marks belong to; it counts no activation.
            bool started = probe.Session.ObserveTagging(scanner.m_tagging, player.Impact);
            // The activation is the native cooldown session: `m_recharging` true with a `m_rechargeStartTime`
            // later than the last one this holder showed. Repeated samples of one session add nothing, and a
            // scanner whose cooldown this build cannot read leaves the figure unknown rather than at 0.
            ObserveCooldown(scanner, probe.Session, player.Impact);
            // Taggable entries are candidates, not evidence of successful marks.
            // ToolSyncManager request/execution hooks supply completed marks independently of this poll.
            if (!probe.Announced && (scanner.m_tagging || ListCount(scanner.m_enemiesDetected) > 0))
            {
                probe.Announced = true;
                Plugin.Logger.LogInfo($"[DIAG] scan probe: {player.Name} tagging={scanner.m_tagging}, detected={ListCount(scanner.m_enemiesDetected)}, taggable={ListCount(scanner.m_taggableEnemies)}; the cooldown session is the activation source, tag windows are not.");
            }
            if (!_loggedScan && probe.Session.CooldownObservable)
            {
                _loggedScan = true;
                Plugin.Logger.LogInfo($"[DIAG] scan cooldown observed: {player.Name}; activations={probe.Session.CooldownActivations}, completions={probe.Session.CooldownCompletions}, repeats={probe.Session.CooldownRepeats}, unusable={probe.Session.CooldownUnusable}; first marks={player.Impact.ScanFirstMarks}.");
            }
            else if (started) { /* the tag window only; no activation is credited for it */ }
        }
        catch (Exception error) { LogImpact("human scan", error); }
    }

    // The tagging bot's own scan action brackets its call to the static BotTag, so the owner published here is
    // exactly the bot that swept, and the scan start published beside it is the one scan that window belongs to.
    // The scope is popped when the action returns - including when it throws - so nothing reads it afterwards.
    internal sealed class BotScanScope
    {
        internal SNet_Player Owner;
        internal bool StartKnown;
        internal float Start;
        internal BotScanScope Previous;
    }

    internal static BotScanScope BeginBotScanAction(PlayerBotActionUseEnemyScanner action)
    {
        var scope = new BotScanScope { Previous = _botScan };
        _botScan = scope;
        try
        {
            scope.Owner = action != null && action.m_agent != null ? action.m_agent.Owner : null;
            // The bot's own scan start names the sweep this call belongs to; no duration is read for it. A build
            // that does not expose the member leaves `StartKnown` false, and that bot's activation figure then
            // stays unknown - exactly the figure it had before this hook existed.
            if (ShouldTrack(scope.Owner) && scope.Owner.IsBot) scope.StartKnown = ReadBotScanStart(action, out scope.Start);
            if (ShouldTrack(scope.Owner) && !_loggedBotAction)
            {
                _loggedBotAction = true;
                Plugin.Logger.LogInfo("[DIAG] bot scan action observed: the tagging bot is read from its own scan action instance, so bot scans no longer depend on a proximity guess.");
            }
            return scope;
        }
        catch (Exception error) { scope.Owner = null; scope.StartKnown = false; LogImpact("bot scan action", error); return scope; }
    }

    internal static void EndBotScanAction(BotScanScope scope)
    {
        try
        {
            if (scope?.Owner != null && ScanLedger.TryGetValue(scope.Owner.Lookup, out ScanProbe probe)) probe.Session.EndBotTag();
        }
        catch (Exception error) { LogImpact("bot scan end", error); }
        // The previous scope comes back, so a nested or re-entrant update cannot leave a stale owner or a stale
        // scan start behind for the next BotTag to be credited to.
        finally { if (scope != null && ReferenceEquals(_botScan, scope)) _botScan = scope.Previous; }
    }

    // The action's own scan start. The slot is pinned by the disassembly: `StartScanning` (RVA 0x17DB430) writes
    // `[action+0x70]` from `Time.time` as the scan begins (0x17DB490, right after 0x17DB484 zeroes `[action+0x74]`
    // and just before 0x17DB495 sets the scan state), and `UpdateStateScan` (RVA 0x17DBA90) subtracts that same
    // slot from `Time.time` for its elapsed test (0x17DBB20/0x17DBB4D), so one scan carries one value. The
    // member's name is not in this build's verified member list, so it is looked up by name instead of being
    // guessed at: a build that does not expose it returns false and counts nothing.
    private const string BotScanStartMember = "m_scanStartTime";
    private static bool ReadBotScanStart(object action, out float start)
    {
        start = float.NaN;
        if (action == null) return false;
        try
        {
            if (OptionalProjectileEvents.Read(action, BotScanStartMember) is float value && float.IsFinite(value))
            {
                start = value;
                return true;
            }
        }
        catch (Exception error) { LogImpact("bot scan start", error); }
        return false;
    }

    // Static bot tagging carries no owner, but it is called from inside the tagging bot's own scan
    // action, and that action is what names the bot. Only where it cannot be read does the proximity
    // rule remain: a single tracker-carrying bot near the supplied centre.
    internal static void CreditBotScan(Vector3 centre, Il2CppSystem.Collections.Generic.List<EnemyAgent> found)
    {
        try
        {
            if (!SNet.IsMaster || GameStateManager.CurrentStateName != eGameStateName.InLevel || found == null) return;
            BotScanScope scope = _botScan;
            bool fromAction = ShouldTrack(scope?.Owner) && scope.Owner.IsBot;
            SNet_Player owner = fromAction ? scope.Owner : BotOwnerNear(centre);
            if (owner == null)
            {
                if (!_loggedBotScanUnresolved)
                {
                    _loggedBotScanUnresolved = true;
                    string closest = float.IsFinite(NearbyBotDistance) ? $"{NearbyBotDistance:0.0} m" : "no readable distance";
                    Plugin.Logger.LogInfo($"[DIAG] bot tag observed but its owner could not be resolved: the scan action owner was unreadable and the proximity fallback matched {NearbyBotCandidates} tracker-carrying bot(s) within a metre (closest {closest}); the scan stays unavailable rather than being assigned to a human.");
                }
                return;
            }

            // 1.19.62 (review of the previous round): a human tag the game already executed is credited BEFORE this bot's
            // tag snapshot is looked at, so the human keeps the squad's first reveal and the bot sees a repeat. The old
            // guard asked a request ledger that only a *client* fills (`ScanTagPipeline.Request` runs only when
            // `!SNet.IsMaster`), while a bot's tag only ever runs on the host - so it could never fire. This is the queue
            // that really holds "executed, not yet credited": `ScanTagPipeline.Events`. Draining it here applies exactly
            // the same validation the frame drain applies (generation, resolved target, tagged state, owner, native
            // exception), and it runs on this thread, which is the main thread `BotTag` was called on.
            //
            // Order is the whole rule: a human tag that executed earlier takes the first mark; a bot that really tagged
            // first keeps it, and the human's later execution is the repeat. Neither order changes any count - the same
            // marks are credited either way, only the owner of the squad's single first reveal follows the real order.
            ScanTagPipeline.Drain();
            ScanProbe probe = Ledger(owner);
            var player = ObservePlayer(owner);
            bool started = probe.Session.BeginBotTag(Time.time, EnemyScanner.s_tagDuration, player.Impact);
            LogScanRepeats(player, MarkEnemies(found, probe.Session, player.Impact, true));
            // 1.19.42: the scan itself, counted once per real scan and only from evidence this machine can show.
            // `fromAction` is what makes the owner exact, and it is also the only case that publishes a scan
            // start, so the proximity fallback keeps its marks and gains no use. `BotTag` returning normally is
            // the success this callback *is*: a native tag that throws never reaches a postfix. Nothing here
            // reads `s_tagDuration` - an estimated window is not a scan.
            float scanStart = fromAction ? scope.Start : float.NaN;
            bool startKnown = fromAction && scope.StartKnown;
            BotScanSignal scan = probe.Session.ObserveBotScan(startKnown, scanStart, player.Impact);
            if (scan == BotScanSignal.Started && !_loggedBotScanUse)
            {
                _loggedBotScanUse = true;
                Plugin.Logger.LogInfo($"[DIAG] bot scan counted: {player.Name}; uses={player.Impact.ScanUses}; source=the bot action's own scan start {scanStart:0.###}s, one per scan.");
            }
            else if (fromAction && !startKnown && !_loggedBotScanStart)
            {
                _loggedBotScanStart = true;
                Plugin.Logger.LogInfo($"[DIAG] bot tag returned with no readable scan start: {player.Name}; its marks are kept and no use is counted, so the figure stays unknown instead of being estimated from a window.");
            }
            if (started && !_loggedBotScan)
            {
                _loggedBotScan = true;
                Plugin.Logger.LogInfo($"[DIAG] bot tag credited: {player.Name}; uses={player.Impact.ScanUses}, first marks={player.Impact.ScanFirstMarks}; the owner came from the bot's own scan action.");
            }
        }
        catch (Exception error) { LogImpact("bot scan", error); }
    }

    // The fallback attribution, used only when the scan action could not name the bot: the nearest
    // tracker-carrying bot, and only if it is within one metre and nobody else is. What it saw is
    // kept for the diagnostic, so a build where this never resolves says why instead of going quiet.
    private static SNet_Player BotOwnerNear(Vector3 centre)
    {
        NearbyBotCandidates = 0; NearbyBotDistance = float.PositiveInfinity;
        try
        {
            var agents = PlayerManager.PlayerAgentsInLevel;
            if (agents == null) return null;
            SNet_Player candidate = null;
            foreach (var agent in agents)
            {
                if (agent == null) continue;
                var owner = agent.Owner;
                if (!ShouldTrack(owner) || !owner.IsBot || ResolveScanner(owner) == null) continue;
                float distance = Vector3.Distance(agent.transform.position, centre);
                if (distance < NearbyBotDistance) NearbyBotDistance = distance;
                if (distance > 1f) continue;
                NearbyBotCandidates++;
                if (candidate != null) return null;
                candidate = owner;
            }
            return candidate;
        }
        catch (Exception error) { LogImpact("bot proximity", error); return null; }
    }


    private static EnemyScanner ResolveScanner(SNet_Player owner)
    {
        if (!PlayerBackpackManager.TryGetItem(owner, InventorySlot.GearClass, out BackpackItem backpack) || backpack?.Instance == null) return null;
        return backpack.Instance.TryCast<EnemyScanner>();
    }

    private static int ListCount(Il2CppSystem.Collections.Generic.List<EnemyAgent> enemies) => enemies == null ? 0 : enemies.Count;

    // Sweeps the list a BotTag call handed back and reports how many of those enemies the squad had already
    // revealed. 1.19.62: the reveal namespace is the squad's, shared with every human tracker and every other
    // bot, so an enemy that is already on the bio tracker is a repeat for this sweep too - it is not a first
    // mark for the bot simply because the bot had not swept it before.
    //
    // The one honest limit left in this path: `foundEnemies` is a snapshot of enemies that are tagged, and it
    // cannot say whether *this* sweep tagged them or an earlier one did. That is why a bot's marks stay an
    // estimate (`ScanEstimated`, printed with `~`) and why nothing here is allowed to outrank a confirmation the
    // native `ToolSyncManager.DoTagEnemy` path actually executed: a human's first mark is credited from that
    // execution (`CreditExecutedScanTag`), never from a tag-state snapshot like this one.
    //
    // 1.19.62 (review of the previous round): "nothing here outranks a confirmation" is enforced by ORDER, where the two
    // can actually meet: `CreditBotScan` drains the executed-and-not-yet-credited confirmations before this sweep runs,
    // so a human tag the game really executed already holds the squad's first reveal and this snapshot sees the repeat.
    // A bot that really tagged first keeps it, and the human's later execution is the repeat - the same marks either way.
    // (The guard this replaced asked a *request* ledger that only a client fills, while a bot's tag only runs on the
    // host, so it could never fire; it is gone, not left behind.)
    private static int MarkEnemies(Il2CppSystem.Collections.Generic.List<EnemyAgent> enemies, ScanSession session, ImpactCounters counters, bool bot)
    {
        if (enemies == null) return 0;
        int repeats = 0;
        for (int index = 0; index < enemies.Count; index++)
        {
            EnemyAgent enemy = enemies[index];
            if (enemy == null) continue;
            int identity = enemy.GetInstanceID();
            // `IsTagged` is the confirmation this sweep can offer; the session decides the reveal against the one
            // squad namespace, so a repeat costs nothing and takes nothing away from whoever revealed it first. A human
            // tag that executed earlier has already been credited by the drain above, which is what makes this bot's
            // sweep see a repeat instead of stealing the first reveal.
            ScanMark mark = session.ObserveConfirmed(identity, counters, enemy.IsTagged, bot);
            if (mark == ScanMark.Repeat) repeats++;
        }
        return repeats;
    }

    private static void LogScanRepeats(CombatPlayer player, int repeats)
    {
        if (repeats <= 0 || _loggedScanRepeat) return;
        _loggedScanRepeat = true;
        Plugin.Logger.LogInfo($"[DIAG] scan repeats suppressed: {repeats} of {player.Name}'s swept enemies were already revealed, so they are not first marks for anyone, including the sweeper who revealed them first.");
    }

    internal static void LogToolError(string what, Exception error) => LogImpact("tool " + what, error);
    private static void LogImpact(string what, Exception error)
    {
        Plugin.Logger?.LogWarning("[DIAG] " + what + " hook could not read its input and was skipped: " + error.Message);
    }
}
