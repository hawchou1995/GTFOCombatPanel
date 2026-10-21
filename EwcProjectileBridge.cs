using System;
using System.Reflection;
using Gear;
using Il2CppInterop.Runtime.InteropTypes;
using Player;
using SNetwork;
using UnityEngine;

namespace HawChow.GTFOCombatPanel;

internal static class EwcProjectileBridge
{
    private static readonly ProjectileAccuracyLedger Flights = new ProjectileAccuracyLedger();
    private static OptionalProjectileEvents _binding;
    private static bool _watching, _warned;
    private static volatile bool _retry = true;
    private static int _hitDepth, _hitFrame;
    internal static bool InProjectileHit => _hitDepth > 0 && _hitFrame == Time.frameCount;

    internal static void EnsureInitialized()
    {
        if (!_watching)
        {
            _watching = true;
            AppDomain.CurrentDomain.AssemblyLoad += (_, args) =>
            {
                if (args.LoadedAssembly.GetName().Name == "ExtraWeaponCustomization") _retry = true;
            };
        }
        if (_binding != null || !_retry) return;
        _retry = false;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetName().Name != "ExtraWeaponCustomization") continue;
            try
            {
                Type api = assembly.GetType("EWC.API.ProjectileAPI", true);
                _binding = Bind(api);
                Plugin.Logger?.LogInfo("[DIAG] Optional EWC projectile bridge armed: spawn/hit/destroy; original owner and weapon are frozen at spawn; no compile-time EWC reference.");
            }
            catch (Exception error) { Warn("API could not be bound; projectile accuracy remains unmeasured", error); }
            return;
        }
    }

    internal static OptionalProjectileEvents Bind(Type api)
        => OptionalProjectileEvents.Bind(api, OnSpawn, OnHit, OnDestroy, OnPostHit);

    internal static void ResetExpedition() { Flights.Reset(); _hitDepth = 0; }

    private static void OnPostHit(object projectile, object target)
    {
        if (_hitFrame == Time.frameCount && _hitDepth > 0) _hitDepth--;
    }

    private static void OnSpawn(object projectile)
    {
        try
        {
            if (OptionalProjectileEvents.Read(projectile, "IsManaged") is not true) return;
            if (!OptionalProjectileEvents.Identity(projectile, out IntPtr pointer, out ulong token))
            { Warn("spawn identity is missing; event ignored", null); return; }
            object cwc = OptionalProjectileEvents.Read(OptionalProjectileEvents.Read(projectile, "Settings"), "CWC");
            var agent = OptionalProjectileEvents.Read(OptionalProjectileEvents.Read(cwc, "Owner"), "Player") as PlayerAgent;
            var owner = agent?.Owner;
            if (!CombatTracker.ShouldTrack(owner) || !(owner.IsLocal || (SNet.IsMaster && owner.IsBot))) return;
            object slotValue = OptionalProjectileEvents.Read(OptionalProjectileEvents.Read(cwc, "Weapon"), "InventorySlot");
            if (slotValue == null) { Warn("spawn weapon slot is missing; event ignored", null); return; }
            InventorySlot slot = (InventorySlot)Convert.ToInt32(slotValue);
            if (slot != InventorySlot.GearStandard && slot != InventorySlot.GearSpecial) return;
            // Read the source component, never the weapon currently held when the projectile later lands.
            var component = cwc as Component;
            var item = component?.GetComponent<ItemEquippable>();
            if (item == null) { Warn("spawn source item is missing; event ignored", null); return; }
            var player = CombatTracker.ObservePlayer(owner);
            var weapon = CombatTracker.ResolveWeapon(player, owner, slot, item);
            if (!Flights.Spawn(pointer, token, player, weapon)) return;
            var capture = CombatTracker.ActiveFire;
            if (capture?.Player?.Lookup == player.Lookup && capture.Entry == weapon)
            {
                // EWC can replace the native cast; the same fire scope must not also count ammo/rays.
                capture.ProjectileObserved = true;
                if (capture.Sample.Hits > 0)
                {
                    player.AdjustAccuracy(weapon, 0, 0, 0, true);
                    Warn("mixed native-hit/projectile fire scope: only identified projectiles counted; row marked estimated", null);
                }
            }
            CombatTracker.NoteAttack(player.Lookup, weapon.Slot);
        }
        catch (Exception error) { Warn("spawn could not be read; event ignored", error); }
    }

    private static void OnHit(object projectile, object target)
    {
        if (_hitFrame != Time.frameCount) { _hitFrame = Time.frameCount; _hitDepth = 0; }
        _hitDepth++;
        try
        {
            if (!OptionalProjectileEvents.Identity(projectile, out IntPtr pointer, out ulong token)) return;
            var damageable = target as Il2CppObjectBase;
            var limb = damageable?.TryCast<Dam_EnemyDamageLimb>();
            var enemy = limb?.m_base ?? damageable?.TryCast<Dam_EnemyDamageBase>();
            bool isEnemy = enemy != null && enemy.Owner != null && enemy.TryCast<Dam_StaticEnemyDamage>() == null;
            bool weak = limb != null && limb.m_type == eLimbDamageType.Weakspot;
            Flights.Hit(pointer, token, isEnemy, weak);
        }
        catch (Exception error) { Warn("hit could not be identified; event ignored", error); }
    }

    private static void OnDestroy(object projectile)
    {
        try
        {
            if (OptionalProjectileEvents.Identity(projectile, out IntPtr pointer, out ulong token)) Flights.Destroy(pointer, token);
        }
        catch (Exception error) { Warn("destroy identity could not be read", error); }
    }

    private static void Warn(string message, Exception error)
    {
        if (_warned) return;
        _warned = true;
        Plugin.Logger?.LogWarning("[DIAG] EWC projectile bridge: " + message + (error == null ? string.Empty : ": " + error.Message));
    }
}
