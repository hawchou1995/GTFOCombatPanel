#if EWC_PRESENT
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Unity.IL2CPP;
using EWC.API;
using SNetwork;
using EWC.API.Accuracy;
using EWC.CustomWeapon;
using Il2CppInterop.Runtime.InteropTypes;
using Player;
using Gear;

namespace HawChow.GTFOCombatPanel;

// The answer to "how do you measure a weapon the game did not make". ExtraWeaponCustomization owns its
// weapons' firing and already counts their shots and their hits, so this project reads EWC's own
// accuracy API instead of guessing from rays it can never see - the same approach Dinorush's StatDisplay
// takes. What differs from a first attempt at this is *how* EWC's reports are consumed: its cumulative
// per-weapon totals are read, not its per-update deltas, because EWC announces a shot and a hit in two
// separate updates. `EwcReconcile` holds those rules and is asserted headlessly; this file is only the
// part that has to touch EWC's own types.
//
// Two rules keep that honest with the BepInEx-only promise:
//   1. nothing here runs unless the plugin list actually contains EWC (a string check, no assembly touch);
//   2. every member that mentions an EWC type is NoInlining, so the EWC assembly is only resolved when
//      the plugin really is installed.
// When EWC is absent the whole file compiles out (see the EWC_PRESENT define in the csproj) and the mod
// behaves exactly as it did before.
internal static class EwcBridge
{
    internal const string PluginGuid = "Dinorush.ExtraWeaponCustomization";

    internal static readonly bool Present;

    // One weapon row of one player: which of EWC's levels its figures are read from, what this machine
    // has already handed the panel for it, and the hits it has seen for it that EWC is not known to have
    // counted. The row lives per weapon slot, so a hit seen while one weapon was out can never be folded
    // into the next update of another one.
    private struct Row
    {
        public bool Known;
        public bool FullShot;
        public AccuracyCounts Applied;
        public AccuracyCounts Observed;
    }

    private static readonly Dictionary<ulong, Dictionary<int, Row>> Rows = new Dictionary<ulong, Dictionary<int, Row>>();
    private static bool _logged;

    static EwcBridge()
    {
        try
        {
            Present = IL2CPPChainloader.Instance != null && IL2CPPChainloader.Instance.Plugins.ContainsKey(PluginGuid);
            // Arming the bridge resolves EWC's own types, and that resolution happens when the JIT compiles
            // the call, on the caller - not inside the method being armed where its own try could catch it.
            // An EWC that is present but predates the accuracy API therefore fails right here. Outside this
            // try that failure is a broken type initialiser: every later entry point into this class throws
            // too, and the mod goes down with EWC rather than without it. Inside the try it is only an unarmed
            // bridge, and `Present = false` puts the mod back in the state it has when EWC is absent - their
            // weapons measured from this machine's own rays again - which is the behaviour this whole file
            // exists to guarantee.
            if (Present) Subscribe();
        }
        catch (Exception error)
        {
            Present = false;
            Plugin.Logger?.LogWarning("EWC is installed but its bridge could not be armed, so the mod runs exactly as it does without EWC and its weapons are measured from this machine's own rays: " + error.Message);
        }
    }

    // True only for a weapon EWC has taken over. The rest of the mod must leave those alone: EWC reports
    // their shots itself, and counting them here as well would count every shot twice.
    internal static bool IsEwcWeapon(BulletWeapon weapon) => Present && HasComponent(weapon);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool HasComponent(BulletWeapon weapon)
    {
        try { return weapon != null && weapon.GetComponent<CustomWeaponComponent>() != null; }
        catch { return false; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Subscribe()
    {
        try
        {
            AccuracyAPI.OnAccuracyUpdate += OnAccuracy;

            // The three streams the accuracy API does not cover, each read exactly the way StatDisplay reads
            // it. Without them a weapon that reports its shots through `FireShotAPI`, its hits through
            // `ProjectileAPI`, or whose damage is DOT / explosive / shrapnel is on the board as 0/0/0 - which
            // is what a 涡轮电浆枪 showed while it was plainly being fired.
            FireShotAPI.PreShotFired += (data, _, _) =>
            {
                try { ObserveShot(data == null ? null : data.owner, data == null || data.shotInfo == null ? null : data.shotInfo.CWC); }
                catch (Exception error) { WarnOnce("an EWC shot could not be read", error); }
            };
            ProjectileAPI.OnProjectileHit += (projectile, damageable) =>
            {
                try { ObserveHit(projectile == null || projectile.Settings == null ? null : projectile.Settings.CWC, damageable); }
                catch (Exception error) { WarnOnce("an EWC projectile hit could not be read", error); }
            };
            // A DOT, explosive or shrapnel blow never travels the game's bullet path, so this event is the
            // only place it is ever seen. The amount is capped by the health left on the limb, the owner and
            // slot come from the event itself, and `CombatTracker.NoteEwcDamage` applies the same
            // "only while the host runs no copy of this mod" guard StatDisplay applies on MasterHasMod.
            DamageAPI.PreLocalDOTDamage += (damage, _, limb, source, cwc) =>
            {
                try
                {
                    if (source == null) return;
                    SNet_Player owner = source.Owner;
                    if (owner == null || !CombatTracker.ShouldTrack(owner)) return;
                    float amount = limb == null || limb.m_base == null ? damage : Math.Min(damage, Math.Max(0f, limb.m_base.Health));
                    CombatTracker.NoteEwcDamage(owner.Lookup, (int)cwc.slot, amount);
                }
                catch (Exception error) { WarnOnce("an EWC DOT damage event could not be read", error); }
            };
            DamageAPI.PreLocalExplosiveDamage += (damage, _, limb, source, cwc) =>
            {
                try
                {
                    if (source == null) return;
                    SNet_Player owner = source.Owner;
                    if (owner == null || !CombatTracker.ShouldTrack(owner)) return;
                    float amount = limb == null || limb.m_base == null ? damage : Math.Min(damage, Math.Max(0f, limb.m_base.Health));
                    CombatTracker.NoteEwcDamage(owner.Lookup, (int)cwc.slot, amount);
                }
                catch (Exception error) { WarnOnce("an EWC explosive damage event could not be read", error); }
            };
            DamageAPI.PreLocalShrapnelDamage += (damage, _, limb, source, cwc) =>
            {
                try
                {
                    if (source == null) return;
                    SNet_Player owner = source.Owner;
                    if (owner == null || !CombatTracker.ShouldTrack(owner)) return;
                    float amount = limb == null || limb.m_base == null ? damage : Math.Min(damage, Math.Max(0f, limb.m_base.Health));
                    CombatTracker.NoteEwcDamage(owner.Lookup, (int)cwc.slot, amount);
                }
                catch (Exception error) { WarnOnce("an EWC shrapnel damage event could not be read", error); }
            };
            Plugin.Logger.LogInfo("[DIAG] EWC found: its weapons are measured by ExtraWeaponCustomization's own accuracy API, plus the three streams that API does not cover - FireShotAPI for the shots of weapons it never reports, ProjectileAPI for their hits, and DamageAPI's local DOT / explosive / shrapnel events for damage that never travels the bullet path. A weapon with none of them counted used to sit at 0/0/0 while it was being fired.");
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning("EWC is installed but its streams could not all be subscribed, so the weapons they carry stay unmeasured: " + error.Message);
        }
    }

    // A new expedition starts from zero everywhere, so the figures held for the last run go with it.
    internal static void ResetExpedition() { Rows.Clear(); }

    // A hit this machine saw for `slot` that EWC is not known to have counted: a record from the game's
    // own damage path for a weapon EWC owns but never reported a hit for. It is held, not written to the
    // panel, and the next EWC update for that weapon folds in only what EWC itself did not count.
    // One more shot observed for a weapon row, for the weapons EWC's accuracy API does not carry.
    internal static void NoteShot(ulong lookup, int slot)
    {
        if (!Present || lookup == 0UL || slot < 0) return;
        try
        {
            Row row = Find(lookup, slot);
            row.Observed = new AccuracyCounts(row.Observed.Fired + 1, row.Observed.Hits, row.Observed.Weak);
            Store(lookup, slot, row);
            Apply(lookup, slot, row, true);
        }
        catch (Exception error) { WarnOnce("an EWC shot could not be held for its weapon row", error); }
    }

    // The owner and slot of something EWC just reported, turned into an observation. Shots land here from
    // `FireShotAPI` and hits from `ProjectileAPI`; both are cumulative observations that the next accuracy
    // update reconciles with a `max`, so a weapon EWC's accuracy API does carry is not counted twice by them.
    private static void ObserveShot(PlayerAgent agent, CustomWeaponComponent cwc)
    {
        int slot = SlotOf(cwc);
        SNet_Player owner = agent == null ? null : agent.Owner;
        if (owner == null || slot < 0 || !CombatTracker.ShouldTrack(owner)) return;
        NoteShot(owner.Lookup, slot);
    }

    private static void ObserveHit(CustomWeaponComponent cwc, IDamageable target)
    {
        int slot = SlotOf(cwc);
        SNet_Player owner = cwc == null || cwc.Owner == null || cwc.Owner.Player == null ? null : cwc.Owner.Player.Owner;
        if (owner == null || slot < 0 || !CombatTracker.ShouldTrack(owner)) return;
        // A projectile announces every impact - wall, floor, ceiling and enemy alike - so what it hit decides
        // whether this was a hit at all. Without that check a projectile weapon read exactly 100% (529 fired
        // / 529 hit and 92 / 92 in one run), and the player's own hit rate was inflated with it.
        if (!CountsAsEnemyHit(target)) return;
        NoteHit(owner.Lookup, slot, false);
    }

    // The rule itself is pure and asserted by the suite; this only reads the il2cpp object it was handed.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool CountsAsEnemyHit(IDamageable damageable)
    {
        try
        {
            var target = damageable as Il2CppObjectBase;
            if (target == null) return false;
            var enemy = target.TryCast<Dam_EnemyDamageBase>();
            bool isEnemy = enemy != null;
            bool isStaticScenery = target.TryCast<Dam_StaticEnemyDamage>() != null;
            bool hasOwner = isEnemy && enemy.Owner != null;
            return EwcReconcile.ProjectileHitCounts(isEnemy, hasOwner, isStaticScenery);
        }
        catch { return false; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int SlotOf(CustomWeaponComponent cwc)
    {
        try { return cwc == null || cwc.Weapon == null ? -1 : (int)cwc.Weapon.InventorySlot; }
        catch { return -1; }
    }

    private static void WarnOnce(string what, Exception error)
    {
        try { Plugin.Logger.LogWarning(what + ": " + error.Message); } catch { }
    }

    internal static void NoteHit(ulong lookup, int slot, bool weakspot)
    {
        if (!Present || lookup == 0UL || slot < 0) return;
        try
        {
            Row row = Find(lookup, slot);
            row.Observed = new AccuracyCounts(row.Observed.Fired,
                row.Observed.Hits + 1, row.Observed.Weak + (weakspot ? 1 : 0));
            Store(lookup, slot, row);
            Apply(lookup, slot, row, false);
        }
        catch (Exception error) { Plugin.Logger.LogWarning("an EWC hit could not be held for its weapon row: " + error.Message); }
    }

    // A weapon EWC has taken over but never reported. This machine can still see its shots reach the game's
    // own `Fire` hook and its damage land as records, so those figures are held here and handed to the panel
    // while EWC stays silent - otherwise such a weapon reads 0/0/0 for a whole run while it is plainly
    // being used. Nothing is counted twice: the handover below is the same `max` reconciliation EWC's own
    // cumulative totals go through, and every pellet already handed over is remembered in `Applied`.
    internal static void NoteObserved(ulong lookup, int slot, int fired, int hits, int weak)
    {
        if (!Present || lookup == 0UL || slot < 0) return;
        if (fired <= 0 && hits <= 0 && weak <= 0) return;
        try
        {
            Row row = Find(lookup, slot);
            row.Observed = new AccuracyCounts(row.Observed.Fired + Math.Max(0, fired),
                row.Observed.Hits + Math.Max(0, hits), row.Observed.Weak + Math.Max(0, weak));
            Store(lookup, slot, row);
            Apply(lookup, slot, row, false);
        }
        catch (Exception error) { WarnOnce("an EWC weapon's own pellets could not be held for its row", error); }
    }

    // True once EWC has published figures for this player's slot. Until it has, EWC is no authority for that
    // row at all, which is exactly the state a weapon it manages but never reports is left in.
    internal static bool HasReports(ulong lookup, int slot)
        => Present && lookup != 0UL && slot >= 0 && Find(lookup, slot).Known;

    private static readonly HashSet<ulong> FallbackReported = new HashSet<ulong>();

    // Hands this machine's own observations to the panel for a row EWC has never reported. A row EWC does
    // report is left to `OnAccuracy`, whose cumulative totals outrank these observations by construction.
    private static void Apply(ulong lookup, int slot, Row row, bool fromShot)
    {
        if (row.Known) return;
        // A shot on its own waits for the second one. EWC announces a shot through `FireShotAPI` just before
        // it publishes the same shot through its accuracy API, so acting on the first one would mark a row
        // that really is EWC's as an estimate for no reason. The observations are cumulative, so waiting for
        // the second shot loses nothing: it carries the first one with it.
        if (fromShot && row.Observed.Fired < 2) return;
        if (slot != (int)InventorySlot.GearStandard && slot != (int)InventorySlot.GearSpecial) return;
        AccuracyCounts handover = EwcReconcile.FallbackHandover(false, row.Applied, row.Observed);
        if (EwcReconcile.IsEmpty(handover)) return;
        if (!SNet.TryGetPlayer(lookup, out SNet_Player owner) || owner == null) return;
        if (!CombatTracker.ShouldTrack(owner)) return;
        row.Applied = EwcReconcile.Resolve(new AccuracyCounts(0, 0, 0), row.Observed, out _);
        Store(lookup, slot, row);
        var player = CombatTracker.ObservePlayer(owner);
        var weapon = CombatTracker.ResolveWeapon(player, owner, (InventorySlot)slot);
        // Marked as an estimate on purpose: these are this machine's own observations, not EWC's totals.
        player.AdjustAccuracy(weapon, handover.Fired, handover.Hits, handover.Weak, true);
        if (FallbackReported.Add(lookup * 16UL + (ulong)slot))
            Plugin.Logger.LogInfo($"[DIAG] EWC weapon filled from this machine's own observation: player={player.Name}, slot={slot}, row now {row.Applied.Fired} fired / {row.Applied.Hits} hit / {row.Applied.Weak} weak, marked as an estimate; EWC has never reported this weapon, and its own cumulative totals take over the moment it does.");
    }

    // The live hook. EWC has already added this update into its cumulative per-weapon totals by the time
    // the callback runs, so the object states the weapon's whole history and the deltas only say which
    // part of it is new.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void OnAccuracy(WeaponAccuracy accuracy, WeaponDelta delta)
    {
        if (accuracy == null) return;
        try
        {
            int slot = (int)accuracy.Slot;
            PlayerAgent agent = accuracy.ParentStats == null ? null : accuracy.ParentStats.Owner;
            SNet_Player owner = agent == null ? null : agent.Owner;

            // Every update is published before anything can return, because an update that carried no
            // shot is exactly the one this build used to drop: it has to be visible in the log whether it
            // is consumed or not, together with all three of EWC's levels.
            LogRaw(owner, slot, accuracy, delta);

            if (owner == null || !CombatTracker.ShouldTrack(owner)) return;
            if (accuracy.Slot != InventorySlot.GearStandard && accuracy.Slot != InventorySlot.GearSpecial) return;

            Row row = Find(owner.Lookup, slot);
            bool fullShot = EwcReconcile.PreferFullShot(accuracy.Shots.Count, accuracy.FullShots.Count, row.Known, row.FullShot);
            StatInfo level = fullShot ? accuracy.FullShots : accuracy.Shots;
            var reported = new AccuracyCounts(level.Count, level.Hits, level.Crits);
            AccuracyCounts target = EwcReconcile.Resolve(reported, row.Observed, out bool filled);
            AccuracyCounts handover = EwcReconcile.Delta(row.Applied, target);

            row.Known = true; row.FullShot = fullShot; row.Applied = target;
            Store(owner.Lookup, slot, row);

            // A repeated update leaves nothing to hand over: EWC's totals are the same as the last time.
            if (EwcReconcile.IsEmpty(handover)) return;

            var player = CombatTracker.ObservePlayer(owner);
            var weapon = CombatTracker.ResolveWeapon(player, owner, accuracy.Slot);
            player.AdjustAccuracy(weapon, handover.Fired, handover.Hits, handover.Weak, filled);

            if (!_logged)
            {
                _logged = true;
                Plugin.Logger.LogInfo($"[DIAG] EWC row measured from EWC's own cumulative totals: player={player.Name}, slot={slot}, level={(fullShot ? "full shot" : "pellet")}, row now {target.Fired} fired / {target.Hits} hit / {target.Weak} weak{(filled ? " (this machine saw hits EWC had not counted and they are marked as an estimate)" : string.Empty)}; the shot and the hit arrive in separate updates and both are consumed.");
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning("an EWC accuracy update could not be applied: " + error.Message);
        }
    }

    // The raw three levels, once per row, and on every update that carries no shot - which is the shape
    // this build used to discard, so it is never discarded without a trace again.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LogRaw(SNet_Player owner, int slot, WeaponAccuracy accuracy, WeaponDelta delta)
    {
        bool noShot = accuracy.Shots.Count == 0 && accuracy.FullShots.Count == 0;
        bool newlySeen = owner != null && !Rows.ContainsKey(owner.Lookup);
        if (!newlySeen && !noShot && delta.Shots.Count != 0) return;
        if (newlySeen)
        {
            Plugin.Logger.LogInfo($"[DIAG] EWC accuracy levels at first sight: player={(owner == null ? 0UL : owner.Lookup)}, slot={slot}, pellets fired={accuracy.Shots.Count} hit={accuracy.Shots.Hits} crit={accuracy.Shots.Crits}, groups={accuracy.Groups.Count}/{accuracy.Groups.Hits}, whole shots fired={accuracy.FullShots.Count} hit={accuracy.FullShots.Hits} crit={accuracy.FullShots.Crits}; this update's delta was pellets={delta.Shots.Count}/{delta.Shots.Hits}/{delta.Shots.Crits} groups={delta.Groups.Count}/{delta.Groups.Hits} whole={delta.FullShots.Count}/{delta.FullShots.Hits}/{delta.FullShots.Crits}.");
            return;
        }
        Plugin.Logger.LogInfo($"[DIAG] EWC update carrying no shot: player={(owner == null ? 0UL : owner.Lookup)}, slot={slot}, delta pellets={delta.Shots.Count}/{delta.Shots.Hits}/{delta.Shots.Crits} whole={delta.FullShots.Count}/{delta.FullShots.Hits}/{delta.FullShots.Crits}; totals are now {accuracy.Shots.Count}/{accuracy.Shots.Hits} pellets and {accuracy.FullShots.Count}/{accuracy.FullShots.Hits} whole shots, which is a hit EWC counted on its own and it is applied as one.");
    }

    private static Row Find(ulong lookup, int slot)
    {
        if (!Rows.TryGetValue(lookup, out Dictionary<int, Row> bySlot)) return default;
        return bySlot.TryGetValue(slot, out Row row) ? row : default;
    }

    private static void Store(ulong lookup, int slot, Row row)
    {
        if (!Rows.TryGetValue(lookup, out Dictionary<int, Row> bySlot))
        {
            bySlot = new Dictionary<int, Row>();
            Rows[lookup] = bySlot;
        }
        bySlot[slot] = row;
    }
}
#endif
