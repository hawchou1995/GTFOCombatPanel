using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using HawChow.GTFOCombatPanel;

internal static partial class Program
{
    public class ReplayProjectile
    {
        public IntPtr Pointer { get; set; } = new IntPtr(101);
        public ushort PlayerIndex { get; set; } = 2;
        public ushort SyncID { get; set; } = 7;
        public ushort ShotIndex { get; set; } = 9;
        public string OwnerField = "source";
        public bool IsManaged { get; set; } = true;
        public ReplaySettings Settings;
    }
    public sealed class ReplaySettings { public ReplaySource CWC; }
    public sealed class ReplaySource : UnityEngine.Component
    {
        public ReplayOwner Owner;
        public ReplayWeapon Weapon;
    }
    public sealed class ReplayOwner { public Player.PlayerAgent Player; }
    public sealed class ReplayWeapon { public Gear.InventorySlot InventorySlot; }
    public sealed class ReplayTarget : Dam_EnemyDamageLimb
    {
        public bool Enemy { set { m_base = new Dam_EnemyDamageBase { Owner = value ? new object() : null }; } }
        public bool Weak { set { m_type = value ? eLimbDamageType.Weakspot : eLimbDamageType.Normal; } }
    }
    private static ReplayProjectile MakeProjectile(CombatPlayer player, WeaponCombatStats weapon)
        => new ReplayProjectile { Settings = new ReplaySettings { CWC = new ReplaySource {
            Owner = new ReplayOwner { Player = new Player.PlayerAgent { Owner = new SNetwork.SNet_Player { Stats = player } } },
            Weapon = new ReplayWeapon { InventorySlot = (Gear.InventorySlot)weapon.Slot },
            SourceItem = new Gear.ItemEquippable { Stats = weapon }
        } } };
    public static class ReplayProjectileApi
    {
        public delegate void ProjectileCallback(ReplayProjectile projectile);
        public delegate void ProjectileHitCallback(ReplayProjectile projectile, ReplayTarget target);
        public static event ProjectileCallback OnProjectileSpawned;
        public static event ProjectileHitCallback OnProjectileHit;
        public static event ProjectileHitCallback OnProjectilePostHit;
        public static event ProjectileCallback OnProjectileDestroyed;
        public static Action DuringDamage;
        public static void Spawn(ReplayProjectile p) => OnProjectileSpawned?.Invoke(p);
        public static void Hit(ReplayProjectile p, ReplayTarget t)
        {
            OnProjectileHit?.Invoke(p, t);
            DuringDamage?.Invoke();
            OnProjectilePostHit?.Invoke(p, t);
        }
        public static void Destroy(ReplayProjectile p) => OnProjectileDestroyed?.Invoke(p);
    }
    public static class IncompleteProjectileApi
    {
        public static event Action<object> OnProjectileSpawned;
        public static void Spawn(object p) => OnProjectileSpawned?.Invoke(p);
    }

    private static void ReplayDelayedProjectile(bool bridgeCompiled)
    {
        var player = new CombatPlayer(333);
        var plasma = player.Weapon(2, 777, "Turbo plasma");
        var switched = player.Weapon(1, 778, "Switched rifle");
        EwcProjectileBridge.ResetExpedition();
        var projectile = MakeProjectile(player, plasma);
        var capture = new FireCapture { Player = player, Entry = plasma };
        CombatTracker.ActiveFire = capture;
        bool fireOpen = true, inProjectileDamage = false;
        using var binding = EwcProjectileBridge.Bind(bridgeCompiled ? typeof(ReplayProjectileApi) : null);
        ReplayProjectileApi.Spawn(projectile);
        fireOpen = false;
        CombatTracker.ActiveFire = new FireCapture { Player = player, Entry = switched };
        if (ProjectileAccuracyLedger.UseNativeCapture(capture.ProjectileObserved)) player.RecordAccuracy(plasma, 1, 0, 0, true);
        ReplayProjectileApi.DuringDamage = () => inProjectileDamage = EwcProjectileBridge.InProjectileHit;
        player.RecordDamage(plasma, 6932.2, false); // Damage cannot manufacture an accuracy contact.
        Check(plasma.Counters.Hits == 0, "damage alone does not fill projectile hits");
        ReplayProjectileApi.Hit(projectile, new ReplayTarget { Enemy = true, Weak = true });
        Console.WriteLine($"EWC_DELAYED_REPLAY: bridgeCompiled={bridgeCompiled}; fireOpen={fireOpen}; fired={plasma.Counters.ShotsFired}; hit={plasma.Counters.Hits}; weak={plasma.Counters.WeakspotHits}");
        Check(!fireOpen && plasma.Counters.ShotsFired == 1 && plasma.Counters.Hits == 1 && plasma.Counters.WeakspotHits == 1,
            "identified enemy projectile after EndFire must count exactly once, independently of damage");
        Check(switched.Counters.ShotsFired == 0 && switched.Counters.Hits == 0, "delayed projectile retains its original weapon after a swap");
        Check(inProjectileDamage && !EwcProjectileBridge.InProjectileHit, "production pre/post events isolate projectile damage from the newly active native fire scope");
        ReplayProjectileApi.Destroy(projectile);
        ReplayProjectileApi.Hit(projectile, new ReplayTarget { Enemy = true, Weak = true });
        Check(plasma.Counters.Hits == 1, "destroyed projectile cannot create further hits through the production bridge");
        ReplayProjectileApi.DuringDamage = null;
        CombatTracker.ActiveFire = null;
        EwcProjectileBridge.ResetExpedition();
    }

    private static void ReplayEwcDll(string dll)
    {
        using var stream = File.OpenRead(dll);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        bool bridgeCompiled = false;
        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            if (metadata.GetString(type.Namespace) == "HawChow.GTFOCombatPanel"
                && metadata.GetString(type.Name) == "EwcProjectileBridge") bridgeCompiled = true;
        }
        // This is a headless replay selected by the DLL's real compiled route, not execution inside GTFO.
        ReplayDelayedProjectile(bridgeCompiled);
        Console.WriteLine("EWC_DLL_ROUTE_REPLAY: PASS");
    }

    private static void RunEwcProjectileScenarios()
    {
        ReplayDelayedProjectile(true);
        var player = new CombatPlayer(77);
        var weapon = player.Weapon(2, 200, "EWC projectile");
        var ledger = new ProjectileAccuracyLedger();
        IntPtr pointer = new IntPtr(444);
        Check(!ledger.Hit(pointer, 0, true, true), "orphan hit cannot invent a projectile");
        Check(!ledger.Spawn(IntPtr.Zero, 0, player, weapon), "missing component identity is rejected");
        Check(!ledger.Spawn(pointer, 0, null, weapon), "missing owner is rejected");
        Check(!ledger.Spawn(pointer, 0, player, null), "missing weapon is rejected");
        Check(ledger.Spawn(pointer, 0, player, weapon), "zero EWC SyncID/ShotIndex is a valid new lifecycle");
        Check(!ledger.Spawn(pointer, 0, player, weapon), "duplicate spawn is idempotent");
        Check(!ledger.Hit(pointer, 0, false, true) && weapon.Counters.Hits == 0, "wall or scenery cannot count as an enemy or weakspot");
        Check(ledger.Hit(pointer, 0, true, false), "first body contact counts one hit");
        Check(!ledger.Hit(pointer, 0, true, false), "duplicate callback does not count twice");
        Check(ledger.Hit(pointer, 0, true, true), "same projectile penetrating another target can upgrade its weakspot once");
        Check(!ledger.Hit(pointer, 0, true, true), "multiple weak targets still represent one fired projectile");
        Check(weapon.Counters.ShotsFired == 1 && weapon.Counters.Hits == 1 && weapon.Counters.WeakspotHits == 1,
            "multi-target and repeated callbacks preserve weak <= hit <= fired");
        ledger.Destroy(pointer, 1);
        Check(ledger.Count == 1, "stale destroy token cannot retire the current flight");
        ledger.Destroy(pointer, 0);
        Check(!ledger.Hit(pointer, 0, true, true), "post-destroy callback is ignored");
        Check(ledger.Spawn(pointer, 0, player, weapon), "pool slot and zero IDs can be reused after destruction");
        Check(ledger.Hit(pointer, 0, true, false) && weapon.Counters.Hits == 2, "pool reuse gets a fresh pellet gate");
        Check(ledger.Spawn(pointer, 2, player, weapon), "new IDs replace a missed-destroy pool lifecycle");
        Check(!ledger.Hit(pointer, 0, true, true), "stale previous identity cannot hit the replacement flight");
        ledger.Reset();
        Check(ledger.Count == 0 && !ledger.Hit(pointer, 2, true, true), "expedition reset releases all identities and attribution");
        var ray = new ShotSample(); ray.BeginPellet(); ray.Contact(true);
        if (ProjectileAccuracyLedger.UseNativeCapture(false)) player.RecordAccuracy(weapon, ray.Fired, ray.Hits, ray.WeakspotHits);
        Check(weapon.Counters.ShotsFired == 4 && weapon.Counters.Hits == 3, "ordinary ray keeps its existing single native accounting");
        Check(!ProjectileAccuracyLedger.UseNativeCapture(true), "projectile-owned capture cannot also hand over ammo/rays");
        Check(OptionalProjectileEvents.Bind(null, _ => { }, (_, _) => { }, _ => { }) == null, "EWC absence is a no-op");
        int callbacks = 0;
        try { OptionalProjectileEvents.Bind(typeof(IncompleteProjectileApi), _ => callbacks++, (_, _) => { }, _ => { }); }
        catch (MissingMemberException) { }
        IncompleteProjectileApi.Spawn(new object());
        Check(callbacks == 0, "incompatible API rolls back a partially subscribed stream");
        using (var binding = OptionalProjectileEvents.Bind(typeof(ReplayProjectileApi), _ => callbacks++, (_, _) => { }, _ => { }))
            ReplayProjectileApi.Spawn(new ReplayProjectile());
        ReplayProjectileApi.Spawn(new ReplayProjectile());
        Check(callbacks == 1, "runtime callback signature binds and disposal unsubscribes");
        var p = new ReplayProjectile();
        Check(OptionalProjectileEvents.Identity(p, out IntPtr actual, out ulong token) && actual == p.Pointer
            && token == ((2UL << 32) | (7UL << 16) | 9UL), "identity reads public EWC-shaped properties without an assembly reference");
        Check((string)OptionalProjectileEvents.Read(p, "OwnerField") == "source", "runtime reader handles public CWC-style fields");
        Check(!OptionalProjectileEvents.Identity(new object(), out _, out _), "unknown projectile schema is rejected, not guessed");
    }
}
