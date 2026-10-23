using System;
using System.Linq;
using HawChow.GTFOCombatPanel;

internal static partial class Program
{
    private static void SettlementHandoffScenarios()
    {
        var cache = new SettlementRows();
        var live = new CombatPlayer(88) { Name = "scanner", DisplaySlot = 1 };
        var gun = live.Weapon(1, 100, "rifle");
        live.RecordDamage(gun, 120, false);
        live.RecordAccuracy(gun, 10, 8, 4);
        live.Impact.ScanUses = 3; live.Impact.ScanFirstMarks = 8; live.Impact.ScanMarks = 10;
        live.Impact.RecordSupply(10, 20);
        cache.Update(new[] { live }, 1);
        var saved = cache.Update(Array.Empty<CombatPlayer>(), 1).Single();
        Check(saved.Total.EnemyDamage == 120 && saved.Impact.ScanMarks == 10 && saved.Impact.SupplyUses == 1,
            "settlement keeps damage, scan detail and supply when live roster/cache disappears");
        var empty = new CombatPlayer(88);
        saved = cache.Update(new[] { empty }, 1).Single();
        Check(saved.Total.EnemyDamage == 120 && saved.Weapons.Single().Counters.Hits == 8,
            "empty result row cannot erase live total or per-weapon measurements");
        Check(empty.Total.EnemyDamage == 0 && live.Total.EnemyDamage == 120,
            "settlement handoff never writes capture ledgers");
        saved.Total.RestoreEnemyDamage(0, false, false);
        Check(cache.Update(null, 1).Single().Total.EnemyDamage == 120, "settlement callers receive detached clones");
        var late = live.Clone(); late.RecordDamage(late.Weapons.Single(), 30, false);
        late.Impact.ScanMarks = 14; late.Impact.ScanFirstMarks = 9;
        Check(cache.Update(new[] { late }, 1).Single().Total.EnemyDamage == 150, "late result snapshot enriches settlement");
        saved = cache.Update(new[] { live }, 1).Single();
        Check(saved.Total.EnemyDamage == 150 && saved.Impact.ScanMarks == 14, "older same-quality cumulative snapshot never rolls settlement backwards");
        Check(cache.Update(null, 2).Count == 0, "new expedition cannot inherit the previous settlement");
        var estimate = new CombatPlayer(99); estimate.Total.AddEnemyDamage(200, true);
        cache.Update(new[] { estimate }, 2);
        var exact = new CombatPlayer(99); exact.Total.AddEnemyDamage(100);
        Check(cache.Update(new[] { exact }, 2).Single().Total.EnemyDamage == 100,
            "exact result measurement may correct a larger estimate rather than taking arbitrary maximum");
        cache.Clear(); Check(cache.Update(null, 2).Count == 0, "explicit expedition reset clears settlement handoff");
        var partial = new CombatPlayer(100);
        partial.Weapon(1, 1, "primary").Counters.AddEnemyDamage(2743.8);
        partial.Weapon(2, 2, "special").Counters.AddEnemyDamage(11437.7);
        partial.ReconcileDamageLowerBound();
        Check(Math.Abs(partial.Total.EnemyDamage - 14181.5) < 0.001 && partial.Total.EnemyDamageEstimated,
            "known weapon damage supplies an explicitly estimated lower bound instead of a contradictory zero total");
        partial.ReconcileDamageLowerBound();
        Check(Math.Abs(partial.Total.EnemyDamage - 14181.5) < 0.001, "lower bound reconciliation is not additive");
        partial.Total.RestoreEnemyDamage(20000, true, false); partial.ReconcileDamageLowerBound();
        Check(partial.Total.EnemyDamage == 20000 && !partial.Total.EnemyDamageEstimated, "complete authoritative total above weapon subtotal is preserved");
        var unavailable = ImpactScoring.Evaluate(new[] { new ImpactInput { Name = "unknown" } }, ImpactScoring.DefaultWeights, 1, 1);
        Check(ImpactBoard.MetricFields(unavailable.Entries[0], ImpactMetric.TotalDamage, KillTier.Minor, false, true)[0] == "—",
            "unobserved settlement damage is unavailable, not a fabricated zero");
    }
}
