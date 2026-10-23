using System;
using System.Collections.Generic;

namespace HawChow.GTFOCombatPanel;

// Result pages outlive the roster and its remote cache. Keep a detached projection for this run,
// not a pointer into either capture ledger. Late packets may enrich it; a missing row cannot erase it.
internal sealed class SettlementRows
{
    private readonly Dictionary<ulong, CombatPlayer> _rows = new();
    private uint _run;
    internal void Clear() { _rows.Clear(); _run = 0; }

    internal List<CombatPlayer> Update(IReadOnlyList<CombatPlayer> incoming, uint run)
    {
        if (_run != run) { _rows.Clear(); _run = run; }
        for (int i = 0; incoming != null && i < incoming.Count; i++)
        {
            CombatPlayer source = incoming[i];
            if (source == null) continue;
            CombatPlayer next = source.Clone();
            if (_rows.TryGetValue(source.Lookup, out CombatPlayer previous)) Retain(next, previous);
            next.ReconcileDamageLowerBound();
            _rows[source.Lookup] = next;
        }
        var result = new List<CombatPlayer>();
        foreach (CombatPlayer row in _rows.Values) result.Add(row.Clone());
        return result;
    }

    private static void Retain(CombatPlayer next, CombatPlayer previous)
    {
        RetainStats(next.Total, previous.Total);
        foreach (WeaponCombatStats old in previous.Weapons)
        {
            WeaponCombatStats weapon = next.Weapon(old.Slot, old.ArchetypeId, old.Name, old.GearCategoryId, old.NameAuthoritative);
            RetainStats(weapon.Counters, old.Counters);
        }
        ImpactCounters a = next.Impact, b = previous.Impact;
        if (a.Kills < b.Kills)
        {
            a.MinorKills = b.MinorKills; a.EliteKills = b.EliteKills; a.BossKills = b.BossKills;
            a.SleepingMinorKills = b.SleepingMinorKills; a.SleepingEliteKills = b.SleepingEliteKills; a.SleepingBossKills = b.SleepingBossKills;
            a.UnknownStateMinorKills = b.UnknownStateMinorKills; a.UnknownStateEliteKills = b.UnknownStateEliteKills; a.UnknownStateBossKills = b.UnknownStateBossKills;
        }
        if (a.MeleeKills < b.MeleeKills)
        { a.MeleeKills = b.MeleeKills; a.SleepingMeleeKills = b.SleepingMeleeKills; a.UnknownStateMeleeKills = b.UnknownStateMeleeKills; }
        if (a.ScoutKills < b.ScoutKills)
        { a.ScoutKills = b.ScoutKills; a.SleepingScoutKills = b.SleepingScoutKills; a.UnknownStateScoutKills = b.UnknownStateScoutKills; }
        a.MeleeKillsObserved |= b.MeleeKillsObserved; a.ScoutKillsObserved |= b.ScoutKillsObserved;
        if (Keep(b.ScoutDamage, b.ScoutDamageObserved, b.ScoutDamageEstimated, a.ScoutDamage, a.ScoutDamageObserved, a.ScoutDamageEstimated))
        { a.ScoutDamage = b.ScoutDamage; a.ScoutDamageObserved = b.ScoutDamageObserved; a.ScoutDamageEstimated = b.ScoutDamageEstimated; }
        a.Stagger = Math.Max(a.Stagger, b.Stagger); a.Revives = Math.Max(a.Revives, b.Revives);
        if (a.SupplyUses < b.SupplyUses) { a.SupplyUses = b.SupplyUses; a.SupplyEfficiencySum = b.SupplyEfficiencySum; }
        if (a.ScanUses < b.ScanUses || a.ScanUses == b.ScanUses && a.ScanMarks < b.ScanMarks)
        { a.ScanUses = b.ScanUses; a.ScanFirstMarks = b.ScanFirstMarks; a.ScanMarks = b.ScanMarks; a.ScanEstimated = b.ScanEstimated; }
        a.SelfSupplyUses = Math.Max(a.SelfSupplyUses, b.SelfSupplyUses); a.SelfAmmoUses = Math.Max(a.SelfAmmoUses, b.SelfAmmoUses);
        a.Downs = Math.Max(a.Downs, b.Downs); a.FriendlyFireDeaths = Math.Max(a.FriendlyFireDeaths, b.FriendlyFireDeaths);
        a.LoneSurvivor = Math.Max(a.LoneSurvivor, b.LoneSurvivor); a.HealthLost = Math.Max(a.HealthLost, b.HealthLost);
        a.SentryShots = Math.Max(a.SentryShots, b.SentryShots); a.SentryHits = Math.Max(a.SentryHits, b.SentryHits);
        a.SentryDamage = Math.Max(a.SentryDamage, b.SentryDamage);
        a.MineDeployments = Math.Max(a.MineDeployments, b.MineDeployments); a.MinePickups = Math.Max(a.MinePickups, b.MinePickups);
        a.MineTriggers = Math.Max(a.MineTriggers, b.MineTriggers); a.MineDamage = Math.Max(a.MineDamage, b.MineDamage);
        a.GlueShots = Math.Max(a.GlueShots, b.GlueShots); a.GlueHits = Math.Max(a.GlueHits, b.GlueHits);
        a.GluedEnemies = Math.Max(a.GluedEnemies, b.GluedEnemies);
    }

    private static bool Keep(double old, bool oldObserved, bool oldEstimated, double value, bool observed, bool estimated)
    {
        int prior = CombatMerge.Exactness(oldObserved, oldEstimated), current = CombatMerge.Exactness(observed, estimated);
        return prior > current || prior == current && old > value;
    }

    private static void RetainStats(PlayerCombatStats next, PlayerCombatStats previous)
    {
        if (Keep(previous.EnemyDamage, previous.EnemyDamageObserved, previous.EnemyDamageEstimated,
            next.EnemyDamage, next.EnemyDamageObserved, next.EnemyDamageEstimated))
            next.RestoreEnemyDamage(previous.EnemyDamage, previous.EnemyDamageObserved, previous.EnemyDamageEstimated);
        if (Keep(previous.FriendlyFireDamage, previous.FriendlyFireObserved, previous.FriendlyFireEstimated,
            next.FriendlyFireDamage, next.FriendlyFireObserved, next.FriendlyFireEstimated))
            next.RestoreFriendlyFireDamage(previous.FriendlyFireDamage, previous.FriendlyFireObserved, previous.FriendlyFireEstimated);
        int old = CombatMerge.Exactness(previous.AccuracyObserved, previous.AccuracyEstimated);
        int current = CombatMerge.Exactness(next.AccuracyObserved, next.AccuracyEstimated);
        if (old > current || old == current && (previous.ShotsFired > next.ShotsFired ||
            previous.ShotsFired == next.ShotsFired && previous.Hits > next.Hits))
            next.RestoreAccuracy(previous.ShotsFired, previous.Hits, previous.WeakspotHits, previous.AccuracyObserved, previous.AccuracyEstimated);
    }
}
