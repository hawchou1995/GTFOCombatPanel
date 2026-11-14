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
        // 1.19.34: the pack split moves as one block, like the glue triple. A side that never split its use (an older peer, a
        // row rebuilt from a v8 snapshot) must not be maxed field by field into a block that no longer adds up to its own
        // total, so one side's block is taken whole and the other is left alone. The larger total wins, because a split can
        // only describe packs it counted; when the side holding that pair has no split to go with it, the block is marked
        // unknown again rather than keeping kinds that describe fewer packs than the row prints.
        if (b.SelfSupplyKindsKnown && (b.SelfSupplyUses > a.SelfSupplyUses
            || !a.SelfSupplyKindsKnown && b.SelfSupplyUses == a.SelfSupplyUses))
        {
            a.SelfSupplyUses = b.SelfSupplyUses; a.SelfAmmoUses = b.SelfAmmoUses;
            a.SelfToolUses = b.SelfToolUses; a.SelfMedicalUses = b.SelfMedicalUses;
            a.SelfSupplyKindsKnown = true;
        }
        else if (a.SelfSupplyKindsKnown && b.SelfSupplyUses > a.SelfSupplyUses)
        {
            a.SelfSupplyUses = b.SelfSupplyUses; a.SelfAmmoUses = b.SelfAmmoUses;
            a.SelfToolUses = 0; a.SelfMedicalUses = 0; a.SelfSupplyKindsKnown = false;
        }
        else if (!a.SelfSupplyKindsKnown)
        {
            // 1.19.35 (review fix): an unsplit pair is one measurement, so one side is taken whole. Maxing the two fields apart
            // produced a pair no machine ever reported - (total 3, ammo 0) out of one side's (3, 3) and the other's (2, 0) -
            // and could hand on a pair whose ammo part is larger than its own total. The larger total wins, because a pair can
            // only describe the packs it counted; an equal total leaves the pair this run already settled alone, which is the
            // rule the glue triple uses below. An ammo part larger than its own total is impossible, so it is cut to the
            // total: the total is the figure the row prints and the one the split has to describe.
            if (b.SelfSupplyUses > a.SelfSupplyUses)
            {
                a.SelfSupplyUses = b.SelfSupplyUses;
                a.SelfAmmoUses = b.SelfAmmoUses;
            }
            if (a.SelfAmmoUses > a.SelfSupplyUses) a.SelfAmmoUses = a.SelfSupplyUses;
            a.SelfToolUses = 0; a.SelfMedicalUses = 0;
        }
        a.Downs = Math.Max(a.Downs, b.Downs); a.FriendlyFireDeaths = Math.Max(a.FriendlyFireDeaths, b.FriendlyFireDeaths);
        a.LoneSurvivor = Math.Max(a.LoneSurvivor, b.LoneSurvivor); a.HealthLost = Math.Max(a.HealthLost, b.HealthLost);
        a.SentryShots = Math.Max(a.SentryShots, b.SentryShots); a.SentryHits = Math.Max(a.SentryHits, b.SentryHits);
        a.SentryDamage = Math.Max(a.SentryDamage, b.SentryDamage);
        a.MineDeployments = Math.Max(a.MineDeployments, b.MineDeployments); a.MinePickups = Math.Max(a.MinePickups, b.MinePickups);
        a.MineTriggers = Math.Max(a.MineTriggers, b.MineTriggers); a.MineDamage = Math.Max(a.MineDamage, b.MineDamage);
        // The glue triple moves as one block and its test is the shot count, the same test `CombatMerge.ApplyRemote`
        // and `SyncProtocol.Combine` use: a row that fired no glue has no usable sample, so a hit-only triple
        // - an enemy swinging at a glued door - can neither be spliced into the block nor replace it. Of two
        // blocks that did fire, the larger count wins and an equal one leaves what is already settled alone,
        // which is what keeps a late, smaller snapshot from shrinking a figure this run already reported.
        if (b.GlueShots > 0 && b.GlueShots > a.GlueShots)
        { a.GlueShots = b.GlueShots; a.GlueHits = b.GlueHits; a.GluedEnemies = b.GluedEnemies; }
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
