using System;
using System.Collections.Generic;

namespace HawChow.GTFOCombatPanel;

public readonly struct MineBlastAttribution
{
    public readonly ulong OwnerLookup;
    public readonly bool IsTrackedEquipmentMine;
    public readonly bool SuppressRecentAttackFallback;

    public MineBlastAttribution(ulong ownerLookup, bool trackedEquipmentMine, bool suppressRecentAttackFallback)
    {
        OwnerLookup = ownerLookup;
        IsTrackedEquipmentMine = trackedEquipmentMine;
        SuppressRecentAttackFallback = suppressRecentAttackFallback;
    }

    public static MineBlastAttribution Resolve(bool isMineBlast, ulong trackedEquipmentOwner, ulong coreOwner)
    {
        if (!isMineBlast) return new MineBlastAttribution(0UL, false, false);
        ulong owner = trackedEquipmentOwner != 0UL ? trackedEquipmentOwner : coreOwner;
        return new MineBlastAttribution(owner, trackedEquipmentOwner != 0UL, true);
    }
}

public readonly struct GlueContactCredit
{
    public readonly bool Hit;
    public readonly bool NewEnemy;
    public GlueContactCredit(bool hit, bool newEnemy) { Hit = hit; NewEnemy = newEnemy; }
}

// Per-expedition identity guards for tool events. A mine is registered by its spawned item pointer and,
// when available, its detonation core pointer; those can be distinct IL2CPP objects for the same mine.
// Both tool and carried-in mines retain placer identity; only tool mines contribute tool counts.
public sealed class ToolImpactLedger
{
    private readonly Dictionary<IntPtr, ulong> _deployedMines = new Dictionary<IntPtr, ulong>();
    // The mines that came out of the deployer itself. Every registered mine is attributed, only these are
    // counted as deployments, so a mine carried in from the level still shows its owner's damage.
    private readonly HashSet<IntPtr> _countedMines = new HashSet<IntPtr>();
    private readonly Dictionary<IntPtr, IntPtr> _mineCoreAliases = new Dictionary<IntPtr, IntPtr>();
    private readonly HashSet<IntPtr> _triggeredMines = new HashSet<IntPtr>();
    private readonly HashSet<(IntPtr Turret, uint SearchId)> _sentryHits = new HashSet<(IntPtr, uint)>();
    private readonly HashSet<(ulong Owner, IntPtr Projectile)> _glueHits = new HashSet<(ulong, IntPtr)>();
    // Active provenance survives the first contact so a single shot may glue several distinct enemies.
    private readonly Dictionary<IntPtr, ulong> _glueProjectileOwners = new Dictionary<IntPtr, ulong>();
    private readonly HashSet<(ulong Owner, IntPtr Projectile)> _glueFired = new HashSet<(ulong, IntPtr)>();
    private readonly HashSet<(ulong Owner, IntPtr Enemy)> _gluedEnemies = new HashSet<(ulong, IntPtr)>();
    private readonly Dictionary<IntPtr, ulong> _gluedDoors = new Dictionary<IntPtr, ulong>();
    private readonly HashSet<(IntPtr Door, IntPtr Projectile)> _glueDoorApplications = new HashSet<(IntPtr, IntPtr)>();
    private readonly HashSet<(IntPtr Door, IntPtr Attacker, uint SearchId, int FallbackFrame)> _glueDoorHits = new HashSet<(IntPtr, IntPtr, uint, int)>();

    public int DeployedMineCount => _countedMines.Count;
    public int SentryHitIdentityCount => _sentryHits.Count;
    public int GlueHitIdentityCount => _glueHits.Count;
    public int GluedEnemyCount => _gluedEnemies.Count;
    public int GluedDoorCount => _gluedDoors.Count;
    public int GlueDoorHitIdentityCount => _glueDoorHits.Count;
    public static bool IsEquipmentMine(bool isConsumable, uint equippedGearCategory, uint mineGearCategory)
        => !isConsumable && equippedGearCategory != 0u && mineGearCategory == equippedGearCategory;
    // The same question when the deployer's first-person face may not exist at all. A bot's tool slot
    // exposes no `m_isConsumable`, and a mine that was picked up and placed again keeps the core's
    // identity but not the face - so the category is the remaining evidence. An unreadable category on
    // either side is accepted, because `MineDeployerInstance.OnSpawn` itself already proved this object
    // is a mine; a readable category that disagrees is still refused.
    // Whether this mine is the one the carried tool placed. A picked-up mine is accepted too now: it is
    // attributed to whoever placed it, and the ledger decides separately whether it counts as a deployment.
    public static bool MineBelongsToSlot(uint equippedGearCategory, uint mineGearCategory)
        => mineGearCategory != 0u && (equippedGearCategory == 0u || equippedGearCategory == mineGearCategory);

    public bool TryDeployMine(IntPtr mine, ulong owner, bool isConsumable)
        => TryDeployMine(mine, IntPtr.Zero, owner, isConsumable);

    public bool TryDeployMine(IntPtr mine, IntPtr detonationCore, ulong owner, bool isConsumable)
    {
        if (mine == IntPtr.Zero || owner == 0UL) return false;
        if (detonationCore != IntPtr.Zero && _mineCoreAliases.TryGetValue(detonationCore, out IntPtr existing)
            && existing != mine && _deployedMines.ContainsKey(existing)) return false;
        if (_deployedMines.TryGetValue(mine, out ulong existingOwner))
        {
            if (existingOwner != owner) return false;
            if (detonationCore != IntPtr.Zero) _mineCoreAliases[detonationCore] = mine;
            return false;
        }
        // A picked-up mine is attributed to the player who placed it, so its blast lands on their row, but
        // it is not one of their tool's deployments: that column measures what the deployer did, and a mine
        // carried in from the level would inflate it.
        _deployedMines.Add(mine, owner);
        if (!isConsumable) _countedMines.Add(mine);
        if (detonationCore != IntPtr.Zero) _mineCoreAliases[detonationCore] = mine;
        return !isConsumable;
    }

    // True only for a mine the deployer itself placed. A carried-in mine is still attributed -
    // `TryGetMineOwner` finds its placer - but its blast is not this player's tool output, so it must not
    // be hung on whatever gear row that player happens to hold.
    public bool IsCountedMine(IntPtr mineOrCore)
    {
        IntPtr mine = ResolveMine(mineOrCore);
        return mine != IntPtr.Zero && _countedMines.Contains(mine);
    }

    public bool TryGetMineOwner(IntPtr mineOrCore, out ulong owner)
    {
        IntPtr mine = ResolveMine(mineOrCore);
        if (mine != IntPtr.Zero && _deployedMines.TryGetValue(mine, out owner)) return true;
        owner = 0UL;
        return false;
    }

    // A recovered, previously deployed equipment mine reduces that owner's effective deployment count.
    // Untracked consumable pickups and repeat callbacks return false and cannot subtract twice.
    public bool TryRecoverMine(IntPtr mineOrCore, out ulong owner)
    {
        IntPtr mine = ResolveMine(mineOrCore);
        if (mine != IntPtr.Zero && !_triggeredMines.Contains(mine) && _deployedMines.TryGetValue(mine, out owner))
        {
            bool equipment = _countedMines.Remove(mine);
            _deployedMines.Remove(mine);
            RemoveCoreAliases(mine);
            return equipment;
        }
        owner = 0UL;
        return false;
    }

    // Triggering a placed mine is counted once but does not alter deployment-minus-recovery.
    public bool TryTriggerMine(IntPtr mineOrCore, out ulong owner)
    {
        IntPtr mine = ResolveMine(mineOrCore);
        // Only a mine the deployer itself placed counts as a trigger; a carried-in mine still attributes
        // its blast, but it never moves this owner's tool figures.
        if (mine != IntPtr.Zero && _deployedMines.TryGetValue(mine, out owner) && _countedMines.Contains(mine))
            return _triggeredMines.Add(mine);
        owner = 0UL;
        return false;
    }

    public void ForgetMine(IntPtr mineOrCore)
    {
        if (mineOrCore == IntPtr.Zero) return;
        IntPtr mine = ResolveMine(mineOrCore);
        if (mine == IntPtr.Zero) return;
        _deployedMines.Remove(mine); _countedMines.Remove(mine);
        _triggeredMines.Remove(mine);
        RemoveCoreAliases(mine);
    }

    private IntPtr ResolveMine(IntPtr mineOrCore)
    {
        if (mineOrCore == IntPtr.Zero) return IntPtr.Zero;
        if (_deployedMines.ContainsKey(mineOrCore)) return mineOrCore;
        return _mineCoreAliases.TryGetValue(mineOrCore, out IntPtr mine) ? mine : IntPtr.Zero;
    }

    private void RemoveCoreAliases(IntPtr mine)
    {
        var remove = new List<IntPtr>();
        foreach (KeyValuePair<IntPtr, IntPtr> pair in _mineCoreAliases)
            if (pair.Value == mine) remove.Add(pair.Key);
        for (int index = 0; index < remove.Count; index++) _mineCoreAliases.Remove(remove[index]);
    }

    // A damage search id of zero is not an identity. The turret reports exactly that for the whole run
    // (`searchId=0` in the field log), so keying on it collapsed a whole turret's hits into one - 33 shots,
    // 1 hit, 1215 damage. Zero means "no id available", and in that case each recorded damage event is the
    // hit; a real id still dedupes repeated callbacks for the same shot.
    public void BeginSentryWindow(IntPtr turret) => _sentryHits.RemoveWhere(hit => hit.Turret == turret);

    public bool TrySentryHit(IntPtr turret, uint damageSearchId)
        => turret != IntPtr.Zero && _sentryHits.Add((turret, damageSearchId));

    // A Fire accepted from a tracked owner's equipped GlueGun opens provenance for this projectile pointer.
    // Re-firing a pooled object starts a new lifetime and cannot inherit its former owner's identities.
    public void BeginGlueProjectile(ulong owner, IntPtr projectile)
    {
        if (owner == 0UL || projectile == IntPtr.Zero) return;
        ForgetGlueProjectile(projectile);
        _glueProjectileOwners[projectile] = owner;
        _glueFired.Add((owner, projectile));
    }

    public bool HasGlueProjectile(ulong owner, IntPtr projectile)
        => owner != 0UL && projectile != IntPtr.Zero
            && _glueProjectileOwners.TryGetValue(projectile, out ulong firedBy) && firedBy == owner;

    // The shot-hit is spent once, but provenance remains until projectile cleanup so the same spray can glue
    // multiple enemies. Enemy identities are still deduplicated once per owner for the expedition.
    public GlueContactCredit RecordGlueContact(ulong owner, IntPtr projectile, IntPtr enemy)
    {
        if (enemy == IntPtr.Zero || !HasGlueProjectile(owner, projectile)) return default;
        bool hit = TryGlueHit(owner, projectile);
        bool newEnemy = _gluedEnemies.Add((owner, enemy));
        return new GlueContactCredit(hit, newEnemy);
    }

    public bool TryGlueHit(ulong owner, IntPtr projectile)
        => HasGlueProjectile(owner, projectile)
            && _glueFired.Remove((owner, projectile)) && _glueHits.Add((owner, projectile));

    // Called on native SyncDestroy and before a pooled pointer is registered for another Fire.
    public void ForgetGlueProjectile(IntPtr projectile)
    {
        if (projectile == IntPtr.Zero) return;
        _glueProjectileOwners.Remove(projectile);
        _glueHits.RemoveWhere(hit => hit.Projectile == projectile);
        _glueFired.RemoveWhere(hit => hit.Projectile == projectile);
        _glueDoorApplications.RemoveWhere(hit => hit.Projectile == projectile);
    }

    public bool TryRegisterGlueDoor(IntPtr door, IntPtr projectile, ulong owner)
    {
        if (door == IntPtr.Zero || !HasGlueProjectile(owner, projectile)
            || !_glueDoorApplications.Add((door, projectile))) return false;
        _gluedDoors[door] = owner;
        return true;
    }

    public bool TryGetGlueDoorOwner(IntPtr door, out ulong owner)
    {
        if (door != IntPtr.Zero && _gluedDoors.TryGetValue(door, out owner)) return true;
        owner = 0UL;
        return false;
    }

    public bool TryGlueDoorHit(IntPtr door, IntPtr attacker, uint searchId, int frame)
        => door != IntPtr.Zero && attacker != IntPtr.Zero
            && _glueDoorHits.Add((door, attacker, searchId, searchId == 0u ? frame : 0));

    public void ForgetGlueDoor(IntPtr door)
    {
        if (door == IntPtr.Zero) return;
        _gluedDoors.Remove(door);
        _glueDoorApplications.RemoveWhere(identity => identity.Door == door);
        _glueDoorHits.RemoveWhere(identity => identity.Door == door);
    }

    public void Clear()
    {
        _deployedMines.Clear();
        _countedMines.Clear();
        _mineCoreAliases.Clear();
        _triggeredMines.Clear();
        _sentryHits.Clear();
        _glueHits.Clear();
        _glueProjectileOwners.Clear();
        _glueFired.Clear();
        _gluedEnemies.Clear();
        _gluedDoors.Clear();
        _glueDoorApplications.Clear();
        _glueDoorHits.Clear();
    }

    // EnemyAgent.Setup marks a new occupant when the game reuses a native enemy pointer.
    public void ForgetGluedEnemy(IntPtr enemy)
    {
        if (enemy != IntPtr.Zero) _gluedEnemies.RemoveWhere(identity => identity.Enemy == enemy);
    }
}