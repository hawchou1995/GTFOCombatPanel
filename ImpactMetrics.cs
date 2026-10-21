using System;
using System.Collections.Generic;
using System.Globalization;

namespace HawChow.GTFOCombatPanel;

// The thirteen impact metrics in board order.
public enum ImpactMetric
{
    TotalDamage,
    Stagger,
    Kills,
    Revives,
    WeakspotRate,
    Accuracy,
    SupplyEfficiency,
    ToolEfficiency,
    LoneSurvivor,
    Downs,
    FriendlyFire,
    MeleeKills,
    ScoutKills
}

// Damage origins retained with the last enemy hit for cause-specific kill metrics.
internal enum DamageKind { Unknown, Bullet, Melee, Fire, Push, Explosion }

// Enemy weight class, decided from the enemy's own maximum health so a modded enemy is graded by
// what it actually is instead of by a name list that would go stale.
public enum KillTier { Minor, Elite, Boss }

// One sentence of praise, chosen from the metric the player contributed most to.
public enum ImpactRating
{
    TeamPillar,
    Damage,
    Stagger,
    MinorKills,
    EliteKills,
    BossKills,
    Revives,
    Weakspot,
    Accuracy,
    Supply,
    Scan,
    LoneSurvivor,
    Downs,
    FriendlyFire,
    MeleeKills,
    ScoutKills,
    ZeroScore,
    Unavailable
}

public static class KillClassifier
{
    public const double DefaultMinorMaxHealth = 120;
    public const double DefaultEliteMaxHealth = 1000;

    // < 120 -> minor, 120..999 -> elite, >= 1000 -> boss. Both thresholds are configuration.
    public static KillTier Classify(double healthMax, double minorMaxHealth = DefaultMinorMaxHealth, double eliteMaxHealth = DefaultEliteMaxHealth)
    {
        if (!double.IsFinite(healthMax)) return KillTier.Minor;
        if (healthMax < minorMaxHealth) return KillTier.Minor;
        if (healthMax < eliteMaxHealth) return KillTier.Elite;
        return KillTier.Boss;
    }

    public static void Credit(ImpactCounters counters, KillTier tier)
    {
        if (counters == null) return;
        switch (tier)
        {
            case KillTier.Minor: counters.MinorKills++; break;
            case KillTier.Elite: counters.EliteKills++; break;
            default: counters.BossKills++; break;
        }
    }
    // A kill is credited once, and the state it was landed in travels with it: true = the enemy was still
    // asleep when the lethal blow arrived, false = it was awake, null = the state could not be read. The
    // state is recorded as a subset of the tier total, and an unreadable state gets its own subset, so an
    // unknown state is never displayed as an awake one. The melee and Scout subsets follow the same rule.
    internal static void Credit(ImpactCounters counters, KillTier tier, DamageKind cause, bool isScout, bool? sleeping = null)
    {
        if (counters == null) return;
        Credit(counters, tier);
        counters.CreditKillState(tier, sleeping);
        if (cause == DamageKind.Melee) { counters.CreditMeleeKill(); counters.CreditMeleeKillState(sleeping); }
        if (isScout) { counters.CreditScoutKill(); counters.CreditScoutKillState(sleeping); }
    }
}

// What a hit react on an enemy means for the stagger metric. The tracker maps the game's own
// react type onto this, which keeps the rule itself free of the game and assertable headlessly.
public enum StaggerKind { None, Light, Heavy, Lethal }

// A stagger interrupt is damage that lands on an enemy whose attack is under way and not yet
// finished, and that stops it. A kill is a kill: the react that reports one, and any hit that
// takes the last of the enemy's health, never counts as an interrupt.
public static class StaggerRule
{
    public static bool IsInterrupt(StaggerKind react, float damage, float health, bool attacking, bool needsAttack, bool push = false)
    {
        if (!float.IsFinite(damage) || damage < 0 || damage == 0 && !push) return false;
        if (push && (!attacking || !float.IsFinite(health) || health <= 0)) return false;
        if (react != StaggerKind.Light && react != StaggerKind.Heavy) return false;
        if (float.IsFinite(health) && health > 0 && damage >= health) return false;
        if (needsAttack && !attacking) return false;
        return true;
    }
}
// A friendly-fire incident is another slotted player's live-to-down transition.
public static class FriendlyFireRule
{
    public static bool ShouldCreditDeath(ulong attacker, ulong victim, bool aliveBefore, bool aliveAfter)
        => attacker != 0UL && victim != 0UL && attacker != victim && aliveBefore && !aliveAfter;
}

// Everything the board scores that the damage and accuracy counters do not already hold. Plain
// numbers only, so the whole scoring pass can be exercised without Unity.
public sealed class ImpactCounters
{
    public long MinorKills, EliteKills, BossKills, MeleeKills, ScoutKills;
    public bool MeleeKillsObserved, ScoutKillsObserved;
    public long Stagger;
    public long Revives;
    public long SupplyUses;
    public double SupplyEfficiencySum;
    public long ScanUses, ScanFirstMarks, ScanMarks;
    public bool ScanEstimated;
    public int LoneSurvivor;
    public int Downs, FriendlyFireDeaths;
    public long SentryShots, SentryHits, MineDeployments, MinePickups, MineTriggers, GlueShots, GlueHits, GluedEnemies;
    public double SentryDamage, MineDamage;

    // ---- the state each kill was landed in, and the two facts the supply and down rows report ---------
    // Sleeping kills are the part of a tier total whose enemy was still asleep when the lethal blow
    // arrived; the unknown counts are the part whose state could not be read at all. Both are subsets of
    // the totals above - never extra kills - so every figure on the board still adds up, and a state
    // nobody could read is never displayed as an awake one.
    public long SleepingMinorKills, SleepingEliteKills, SleepingBossKills, SleepingMeleeKills, SleepingScoutKills;
    public long UnknownStateMinorKills, UnknownStateEliteKills, UnknownStateBossKills, UnknownStateMeleeKills, UnknownStateScoutKills;
    // This player's own resupply: every pack they actually consumed, and how many of those were ammo
    // packs. Attribution is the player who benefited, never the one who opened the pack for them.
    public long SelfSupplyUses, SelfAmmoUses;
    // Lifetime actually lost this expedition. Healing does not offset it and a max-health change is not a
    // loss, so this figure only ever grows.
    public double HealthLost;

    public long Kills { get { return MinorKills + EliteKills + BossKills; } }
    public long SleepingKills { get { return SleepingMinorKills + SleepingEliteKills + SleepingBossKills; } }
    public long UnknownStateKills { get { return UnknownStateMinorKills + UnknownStateEliteKills + UnknownStateBossKills; } }
    public long AwakeKills { get { return AwakePart(Kills, SleepingKills, UnknownStateKills); } }
    public double WeightedKills => ImpactScoring.WeightedKills(MinorKills, EliteKills, BossKills, SleepingMinorKills, SleepingEliteKills, SleepingBossKills);
    public double SupplyEfficiency { get { return SupplyUses > 0 ? Clamp01(SupplyEfficiencySum / SupplyUses) : 0; } }
    public void RecordSupply(double effective, double intended)
    {
        if (!double.IsFinite(effective) || effective < 0 || !double.IsFinite(intended) || intended <= 0) return;
        SupplyEfficiencySum += Clamp01(effective / intended);
        SupplyUses++;
    }
    public void CreditInterrupt() { if (Stagger < long.MaxValue) Stagger++; }
    public void CreditKill(KillTier tier) { KillClassifier.Credit(this, tier); }
    public void MarkKillMetricsObserved() { MeleeKillsObserved = ScoutKillsObserved = true; }
    public void CreditMeleeKill() { MeleeKillsObserved = true; if (MeleeKills < long.MaxValue) MeleeKills++; }
    public void CreditScoutKill() { ScoutKillsObserved = true; if (ScoutKills < long.MaxValue) ScoutKills++; }
    public void CreditRevive() { if (Revives < long.MaxValue) Revives++; }
    public void CreditDown() { if (Downs < int.MaxValue) Downs++; }
    public void CreditFriendlyFireDeath() { if (FriendlyFireDeaths < int.MaxValue) FriendlyFireDeaths++; }
    public void CreditScan(long firstMarks, bool estimated = false)
    {
        if (ScanUses < long.MaxValue) ScanUses++;
        if (firstMarks > 0)
        {
            ScanFirstMarks = Math.Min(long.MaxValue, ScanFirstMarks + firstMarks);
            ScanMarks = Math.Min(long.MaxValue, ScanMarks + firstMarks);
        }
        if (estimated) ScanEstimated = true;
    }
    public void CreditScanMark() { if (ScanMarks < long.MaxValue) ScanMarks++; }
    public void MarkLoneSurvivor() { if (LoneSurvivor < int.MaxValue) LoneSurvivor++; }
    public void CreditSentryShot() { if (SentryShots < long.MaxValue) SentryShots++; }
    public void CreditSentryHit() { if (SentryHits < long.MaxValue) SentryHits++; }
    public void CreditMineDeployment() { if (MineDeployments < long.MaxValue) MineDeployments++; }
    public void CreditMinePickup() { if (MinePickups < long.MaxValue) MinePickups++; }
    public void CreditMineTrigger() { if (MineTriggers < long.MaxValue) MineTriggers++; }
    public void CreditGlueShot() { if (GlueShots < long.MaxValue) GlueShots++; }
    public void CreditGlueHit() { if (GlueHits < long.MaxValue) GlueHits++; }
    public void CreditGluedEnemy() { if (GluedEnemies < long.MaxValue) GluedEnemies++; }
    public void AddSentryDamage(double amount) { if (double.IsFinite(amount) && amount > 0) SentryDamage = Math.Min(double.MaxValue, SentryDamage + amount); }
    public void AddMineDamage(double amount) { if (double.IsFinite(amount) && amount > 0) MineDamage = Math.Min(double.MaxValue, MineDamage + amount); }

    // ---- kill state, this player's own resupply, and the lifetime they actually lost -----------------
    // One weight class of the kill row, and the two subsets of it that were observed for that class.
    public long KillCount(KillTier tier)
    {
        switch (tier)
        {
            case KillTier.Minor: return MinorKills;
            case KillTier.Elite: return EliteKills;
            default: return BossKills;
        }
    }
    public long SleepingKillCount(KillTier tier)
    {
        switch (tier)
        {
            case KillTier.Minor: return SleepingMinorKills;
            case KillTier.Elite: return SleepingEliteKills;
            default: return SleepingBossKills;
        }
    }
    public long UnknownStateKillCount(KillTier tier)
    {
        switch (tier)
        {
            case KillTier.Minor: return UnknownStateMinorKills;
            case KillTier.Elite: return UnknownStateEliteKills;
            default: return UnknownStateBossKills;
        }
    }
    // Whatever is left of a total is the awake part, and it can never go negative however a peer's
    // figures arrive: both subsets are bounded by the total they belong to.
    public long AwakeKillCount(KillTier tier)
        => AwakePart(KillCount(tier), SleepingKillCount(tier), UnknownStateKillCount(tier));
    public static long AwakePart(long total, long sleeping, long unknown)
    {
        if (total <= 0) return 0;
        long asleep = AsleepPart(total, sleeping);
        long unread = unknown <= 0 ? 0 : Math.Min(total - asleep, unknown);
        return Math.Max(0, total - asleep - unread);
    }
    public static long AsleepPart(long total, long sleeping)
        => total <= 0 || sleeping <= 0 ? 0 : Math.Min(total, sleeping);

    // The state travels with the kill: true = landed while the enemy was asleep, false = awake,
    // null = the state could not be read, which gets its own bucket instead of being called awake.
    public void CreditKillState(KillTier tier, bool? sleeping)
    {
        if (!sleeping.HasValue) { CreditTierState(ref UnknownStateMinorKills, ref UnknownStateEliteKills, ref UnknownStateBossKills, tier); return; }
        if (sleeping.Value) CreditTierState(ref SleepingMinorKills, ref SleepingEliteKills, ref SleepingBossKills, tier);
    }
    public void CreditMeleeKillState(bool? sleeping)
    {
        if (!sleeping.HasValue) { if (UnknownStateMeleeKills < long.MaxValue) UnknownStateMeleeKills++; return; }
        if (sleeping.Value && SleepingMeleeKills < long.MaxValue) SleepingMeleeKills++;
    }
    public void CreditScoutKillState(bool? sleeping)
    {
        if (!sleeping.HasValue) { if (UnknownStateScoutKills < long.MaxValue) UnknownStateScoutKills++; return; }
        if (sleeping.Value && SleepingScoutKills < long.MaxValue) SleepingScoutKills++;
    }
    private static void CreditTierState(ref long minor, ref long elite, ref long boss, KillTier tier)
    {
        switch (tier)
        {
            case KillTier.Minor: if (minor < long.MaxValue) minor++; break;
            case KillTier.Elite: if (elite < long.MaxValue) elite++; break;
            default: if (boss < long.MaxValue) boss++; break;
        }
    }
    // One resupply this player actually consumed; `ammo` marks an ammo pack rather than a tool refill.
    public void CreditSelfSupply(bool ammo)
    {
        if (SelfSupplyUses < long.MaxValue) SelfSupplyUses++;
        if (ammo && SelfAmmoUses < long.MaxValue) SelfAmmoUses++;
    }
    // Lifetime lost. Healing never subtracts from it and a max-health change is not a loss.
    public void AddHealthLost(double amount)
    {
        if (!double.IsFinite(amount) || amount <= 0) return;
        HealthLost = Math.Min(double.MaxValue, HealthLost + amount);
    }
    public void Reset()
    {
        MinorKills = EliteKills = BossKills = MeleeKills = ScoutKills = 0;
        MeleeKillsObserved = ScoutKillsObserved = false;
        Stagger = Revives = SupplyUses = 0;
        SupplyEfficiencySum = 0;
        ScanUses = ScanFirstMarks = ScanMarks = 0;
        ScanEstimated = false;
        LoneSurvivor = Downs = FriendlyFireDeaths = 0;
        SentryShots = SentryHits = MineDeployments = MinePickups = MineTriggers = GlueShots = GlueHits = GluedEnemies = 0;
        SentryDamage = MineDamage = 0;
        SleepingMinorKills = SleepingEliteKills = SleepingBossKills = SleepingMeleeKills = SleepingScoutKills = 0;
        UnknownStateMinorKills = UnknownStateEliteKills = UnknownStateBossKills = UnknownStateMeleeKills = UnknownStateScoutKills = 0;
        SelfSupplyUses = SelfAmmoUses = 0;
        HealthLost = 0;
    }
    public void CopyFrom(ImpactCounters other)
    {
        if (other == null) return;
        MinorKills = other.MinorKills; EliteKills = other.EliteKills; BossKills = other.BossKills; MeleeKills = other.MeleeKills; ScoutKills = other.ScoutKills;
        MeleeKillsObserved = other.MeleeKillsObserved; ScoutKillsObserved = other.ScoutKillsObserved;
        Stagger = other.Stagger; Revives = other.Revives;
        SupplyUses = other.SupplyUses; SupplyEfficiencySum = other.SupplyEfficiencySum;
        ScanUses = other.ScanUses; ScanFirstMarks = other.ScanFirstMarks; ScanMarks = other.ScanMarks; ScanEstimated = other.ScanEstimated;
        LoneSurvivor = other.LoneSurvivor; Downs = other.Downs; FriendlyFireDeaths = other.FriendlyFireDeaths;
        SentryShots = other.SentryShots; SentryHits = other.SentryHits; SentryDamage = other.SentryDamage;
        MineDeployments = other.MineDeployments; MinePickups = other.MinePickups; MineTriggers = other.MineTriggers; MineDamage = other.MineDamage;
        GlueShots = other.GlueShots; GlueHits = other.GlueHits; GluedEnemies = other.GluedEnemies;
        SleepingMinorKills = other.SleepingMinorKills; SleepingEliteKills = other.SleepingEliteKills; SleepingBossKills = other.SleepingBossKills;
        SleepingMeleeKills = other.SleepingMeleeKills; SleepingScoutKills = other.SleepingScoutKills;
        UnknownStateMinorKills = other.UnknownStateMinorKills; UnknownStateEliteKills = other.UnknownStateEliteKills;
        UnknownStateBossKills = other.UnknownStateBossKills; UnknownStateMeleeKills = other.UnknownStateMeleeKills;
        UnknownStateScoutKills = other.UnknownStateScoutKills;
        SelfSupplyUses = other.SelfSupplyUses; SelfAmmoUses = other.SelfAmmoUses; HealthLost = other.HealthLost;
    }
    private static double Clamp01(double value) { return !double.IsFinite(value) || value < 0 ? 0 : value > 1 ? 1 : value; }
}

// One player as the board sees them: the name, the two weapon lines the board prints, the combat
// counters that already exist, and the impact counters this release adds.
public sealed class ImpactInput
{
    public string Name = string.Empty;
    public ulong Lookup;
    public bool SupportRole;
    public bool StatisticsAvailable = true;
    public bool ScoreComparable = true;
    public bool EnemyDamageObserved;
    public bool EnemyDamageEstimated;
    public double EnemyDamage;
    public bool FriendlyFireObserved;
    public bool FriendlyFireEstimated;
    public double FriendlyFireDamage;
    public long ShotsFired, Hits, WeakspotHits;
    public bool AccuracyEstimated;
    public ImpactCounters Counters = new ImpactCounters();
    public PlayerCombatStats Primary, Secondary, Melee;
    public string PrimaryName = string.Empty, SecondaryName = string.Empty, MeleeName = string.Empty, ToolName = string.Empty;
    public static ImpactInput From(string name, PlayerCombatStats combat, ImpactCounters counters, bool support,
        PlayerCombatStats primary = null, PlayerCombatStats secondary = null, PlayerCombatStats melee = null,
        WeaponCombatStats tool = null)
    {
        var input = new ImpactInput
        {
            Name = name ?? string.Empty, SupportRole = support, Counters = counters ?? new ImpactCounters(),
            Primary = primary, Secondary = secondary, Melee = melee,
            ToolName = tool?.Name ?? string.Empty
        };
        if (combat != null)
        {
            input.EnemyDamage = combat.EnemyDamage;
            input.EnemyDamageObserved = combat.EnemyDamageObserved;
            input.EnemyDamageEstimated = combat.EnemyDamageEstimated;
            input.FriendlyFireDamage = combat.FriendlyFireDamage;
            input.FriendlyFireObserved = combat.FriendlyFireObserved;
            input.FriendlyFireEstimated = combat.FriendlyFireEstimated;
            input.ShotsFired = combat.ShotsFired;
            input.Hits = combat.Hits;
            input.WeakspotHits = combat.WeakspotHits;
            input.AccuracyEstimated = combat.AccuracyEstimated;
        }
        return input;
    }
}

// One player's evaluated row: what they scored, where they placed, and which metric earned them
// their sentence.
public sealed class ImpactEntry
{
    public readonly ImpactInput Input;
    public readonly double[] Raw;
    public readonly double[] Normalized;
    public readonly double Score;
    public readonly int Rank;
    public readonly ImpactRating Rating;
    public readonly int RatingMetric;
    public bool ScoreAvailable => Input != null && Input.StatisticsAvailable && Input.ScoreComparable;

    internal ImpactEntry(ImpactInput input, double[] raw, double[] normalized, double score, int rank, ImpactRating rating, int ratingMetric)
    {
        Input = input; Raw = raw; Normalized = normalized; Score = score; Rank = rank; Rating = rating; RatingMetric = ratingMetric;
    }

    public string Name { get { return Input.Name; } }
    public ImpactCounters Counters { get { return Input.Counters; } }
}

public sealed class ImpactReport
{
    // Sorted by score, highest first.
    public readonly List<ImpactEntry> Entries = new List<ImpactEntry>();
    public readonly double[] Weights;
    public readonly double[] MaxRaw;
    public readonly int[] CountAtMax;
    // The same two facts about the lowest value. What leads a reverse metric (deaths) is the
    // fewest, not the most, so the gold star on that row is decided by the minimum.
    public readonly double[] MinRaw;
    public readonly int[] CountAtMin;
    public readonly double FirepowerBonus, UtilityBonus;

    internal ImpactReport(double[] weights, double firepowerBonus, double utilityBonus)
    {
        Weights = weights;
        int metrics = weights != null ? weights.Length : 0;
        MaxRaw = new double[metrics]; CountAtMax = new int[metrics];
        MinRaw = new double[metrics]; CountAtMin = new int[metrics];
        FirepowerBonus = firepowerBonus; UtilityBonus = utilityBonus;
    }

    // True when this entry is the squad's outright leader in one metric, so its cell earns the
    // gold star. A tie earns nothing, and so does a metric nobody scored in.
    public bool IsGold(int entry, ImpactMetric metric)
    {
        int index = (int)metric;
        if (entry < 0 || entry >= Entries.Count || index < 0 || index >= MaxRaw.Length) return false;
        if (!MetricActive(metric)) return false;
        if (metric == ImpactMetric.Accuracy)
            return IsGoldBy(entry, row => ImpactScoring.DisplayedAccuracy(row.Input));
        double value = Entries[entry].Raw[index];
        if (ImpactScoring.IsReverse(metric))
            return CountAtMin[index] == 1 && value <= MinRaw[index] + 1e-9;
        return value > 0 && value >= MaxRaw[index] - 1e-9 && CountAtMax[index] == 1;
    }

    // Gold for a figure that is not one of the ten scored metrics, such as one of the three kill
    // tiers when the board splits the kill row: the squad's outright highest count wins it, and a
    // tie wins nothing.
    public bool IsGoldBy(int entry, Func<ImpactEntry, double> value)
    {
        if (value == null || entry < 0 || entry >= Entries.Count) return false;
        double own = value(Entries[entry]);
        if (!(own > 0)) return false;
        double best = 0; int count = 0;
        for (int other = 0; other < Entries.Count; other++)
        {
            double candidate = Sanitize(value(Entries[other]));
            best = Math.Max(best, candidate);
        }
        for (int other = 0; other < Entries.Count; other++) if (Sanitize(value(Entries[other])) >= best) count++;
        return count == 1 && own >= best - 1e-9;
    }

    private static double Sanitize(double value) => !double.IsFinite(value) || value < 0 ? 0 : value;

    public bool MetricActive(ImpactMetric metric)
    {
        int index = (int)metric;
        return index >= 0 && index < MaxRaw.Length && MaxRaw[index] > 0;
    }

}

public static class ImpactScoring
{
    public static readonly ImpactMetric[] Order = (ImpactMetric[])Enum.GetValues(typeof(ImpactMetric));
    // Team help leads, but the interrupt is the rarest and hardest contribution the game asks for, so
    // stagger outweighs a raw damage column: a single stopped chomp or a broken scream can decide the
    // run, while damage alone usually only accelerates it. Rescues are deliberately modest: picking a
    // teammate up matters, but on its own it must not outrank sustained team play, and the floor below
    // still keeps a lone rescue from scoring at all. The two rate metrics stay low on purpose - a rate
    // read from a handful of pellets is not evidence.
    // Thirteen metrics keep a balanced 9.68 total; all count metrics normalize against the squad leader.
    // Lone survivor now leads the table at 1.60 and stagger stays second at 1.40: being the last one
    // standing is the rarest state a run can put a player in and the one contribution nobody can share,
    // so it outranks even the interrupt. Every other weight keeps the relative order it had.
    public static readonly double[] DefaultWeights = { 0.95, 1.40, 0.70, 0.28, 0.44, 0.28, 0.78, 0.80, 1.60, 0.55, 0.75, 0.55, 0.60 };
    public const string DefaultWeightsText = "0.95,1.4,0.7,0.28,0.44,0.28,0.78,0.8,1.6,0.55,0.75,0.55,0.6";
    // The thirteen-metric list the previous release published, recognised so it migrates instead of being
    // mistaken for a hand-edited list.
    public const string PreviousThirteenMetricWeightsText = "0.95,1.4,0.7,0.28,0.44,0.28,0.78,0.8,0.47,0.55,0.75,0.55,0.6";
    public const string PreviousElevenMetricWeightsText = "1.08,1.62,0.81,0.32,0.5,0.32,0.9,0.99,0.54,0.63,0.84";
    public const string PreviousDefaultWeightsText = "1.2,1.8,0.9,0.35,0.55,0.35,1.0,1.1,0.6,0.7";
    public const string LegacyDefaultWeightsText = "1.1,1.2,0.9,1.0,0.55,0.35,1.3,1.1,0.6,0.7";
    public const int AccuracyConfidencePellets = 20;
    public const int MinimumRescuesForScoring = 2;

    // The two thresholds the scoring file may move. They are settable so the JSON can carry them, and the
    // constants above are their defaults, so a build with no file at all scores exactly as it always did.
    // They are only ever written on the Unity main thread (config load/reload), so a plain static is safe.
    public static int RescueFloor { get; set; } = MinimumRescuesForScoring;
    public static int AccuracyConfidence { get; set; } = AccuracyConfidencePellets;

    // What one kill of each tier is worth when the kill row is scored. The published default is the
    // 1 : 2 : 3 ratio this build carried in code before 1.16.0 - an elite is worth two minor kills and a
    // boss three - now moved out of the code and into the scoring file. The order is part of the
    // meaning: a boss must never be worth less than an elite, and an elite never less than a minor.
    public const double DefaultMinorKillWeight = 0.5;
    public const double DefaultEliteKillWeight = 1.0;
    public const double DefaultBossKillWeight = 1.5;

    // Settable so the JSON can carry them; the constants above are the defaults, so a build with no file
    // at all scores exactly as it always did. Written only on the Unity main thread (config load/reload),
    // so plain statics are safe - the same rule `RescueFloor` and `AccuracyConfidence` already follow.
    public static double MinorKillWeight { get; set; } = DefaultMinorKillWeight;
    public static double EliteKillWeight { get; set; } = DefaultEliteKillWeight;
    public static double BossKillWeight { get; set; } = DefaultBossKillWeight;

    public static void SetKillWeights(double minor, double elite, double boss)
    {
        MinorKillWeight = double.IsFinite(minor) && minor >= 0 ? minor : DefaultMinorKillWeight;
        EliteKillWeight = double.IsFinite(elite) && elite >= 0 ? elite : DefaultEliteKillWeight;
        BossKillWeight = double.IsFinite(boss) && boss >= 0 ? boss : DefaultBossKillWeight;
    }

    // The sleeping-kill multiplier: a kill landed on an enemy that was still asleep when the lethal blow
    // arrived is worth this many awake kills of the same class. Two by default and carried by the scoring
    // file; a value below one would make a stealth kill worth less than a loud one, so it is bounded from
    // one up, and anything unreadable falls back to the published default rather than to a guess.
    public const double DefaultSleepingKillMultiplier = 2.0;
    public const double MaxSleepingKillMultiplier = 10.0;
    public static double SleepingKillMultiplier { get; set; } = DefaultSleepingKillMultiplier;

    public static void SetSleepingKillMultiplier(double multiplier)
        => SleepingKillMultiplier = double.IsFinite(multiplier) && multiplier >= 1.0
            ? Math.Min(multiplier, MaxSleepingKillMultiplier)
            : DefaultSleepingKillMultiplier;

    // The weighted kill count the kill row scores with, in tier order. Only the sleeping part of a count
    // is worth the multiplier: a kill whose pre-lethal state could not be read is worth exactly an awake
    // one, because an unreadable state is never assumed to have been a stealth kill.
    public static double WeightedKills(long minor, long elite, long boss)
        => WeightedKills(minor, elite, boss, 0, 0, 0);

    public static double WeightedKills(long minor, long elite, long boss, long sleepingMinor, long sleepingElite, long sleepingBoss)
        => TierValue(MinorKillWeight, minor, sleepingMinor)
         + TierValue(EliteKillWeight, elite, sleepingElite)
         + TierValue(BossKillWeight, boss, sleepingBoss);

    // One weight class of one player's kill row, sleeping share included. The three of them add up to
    // ImpactCounters.WeightedKills exactly, which is what keeps a per-tier title share from double counting.
    public static double WeightedKillValue(ImpactCounters counters, KillTier tier)
    {
        if (counters == null) return 0;
        switch (tier)
        {
            case KillTier.Minor: return TierValue(MinorKillWeight, counters.MinorKills, counters.SleepingMinorKills);
            case KillTier.Elite: return TierValue(EliteKillWeight, counters.EliteKills, counters.SleepingEliteKills);
            default: return TierValue(BossKillWeight, counters.BossKills, counters.SleepingBossKills);
        }
    }

    // A plain count with its sleeping subset lifted: the melee and Scout rows score this way.
    public static double SleepingWeighted(long total, long sleeping) => TierValue(1.0, total, sleeping);

    private static double TierValue(double weight, long total, long sleeping)
    {
        if (total <= 0 || !double.IsFinite(weight) || weight <= 0) return 0;
        // A sleeping subset that outran its own total (a bad hand edit or a stale peer figure) is bounded
        // by the total, so no weight can ever be created out of nothing.
        long asleep = sleeping <= 0 ? 0 : Math.Min(total, sleeping);
        double multiplier = SleepingKillMultiplier;
        if (!double.IsFinite(multiplier) || multiplier < 1.0) multiplier = DefaultSleepingKillMultiplier;
        return weight * (total + (multiplier - 1.0) * asleep);
    }

    public static void ResetThresholds()
    {
        RescueFloor = MinimumRescuesForScoring;
        AccuracyConfidence = AccuracyConfidencePellets;
        SetKillWeights(DefaultMinorKillWeight, DefaultEliteKillWeight, DefaultBossKillWeight);
        SetSleepingKillMultiplier(DefaultSleepingKillMultiplier);
    }

    // Every published default migrates; any hand-edited list remains intact. The thirteen-metric list of
    // the previous release is included because this release reweighted lone survivor (0.47 -> 1.60), so a
    // file still holding that exact published list is a default rather than a user's decision.
    public static bool IsLegacyDefaultWeights(string text)
    {
        string candidate = text == null ? null : text.Trim();
        return string.Equals(candidate, LegacyDefaultWeightsText, StringComparison.Ordinal)
            || string.Equals(candidate, PreviousDefaultWeightsText, StringComparison.Ordinal)
            || string.Equals(candidate, PreviousElevenMetricWeightsText, StringComparison.Ordinal)
            || string.Equals(candidate, PreviousThirteenMetricWeightsText, StringComparison.Ordinal);
    }

    // Accept this version's 13 weights and legacy 10/11/12-item prefixes, filling only missing suffixes.
    public static double[] ParseWeights(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            string[] parts = text.Split(',');
            if (parts.Length >= Order.Length - 3 && parts.Length <= Order.Length)
            {
                var parsed = (double[])DefaultWeights.Clone();
                bool ok = true;
                for (int index = 0; index < parts.Length; index++)
                {
                    if (!double.TryParse(parts[index].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[index])
                        || !double.IsFinite(parsed[index]) || parsed[index] < 0)
                    { ok = false; break; }
                }
                if (ok) return parsed;
            }
        }
        return (double[])DefaultWeights.Clone();
    }

    // "0.0##" rather than "0.###": a whole weight keeps its ".0" so the text this prints is exactly
    // the text the default is written as, and a saved config reloads to the same numbers.
    public static string Describe(double[] weights)
        => string.Join(",", Array.ConvertAll(weights ?? DefaultWeights, value => value.ToString("0.0##", CultureInfo.InvariantCulture)));

    // The support role's firepower compensation. The user asked for it to lift the two damage figures a
    // support player is measurably denied by the kit they carry - the damage they deal and the attacks
    // they interrupt - and nothing else: a hit rate or a kill count is not evidence of firepower, so
    // those two rows are deliberately left alone. A support player pays for the team with their gun, not
    // with their aim, which is what this compensates.
    public static bool UsesFirepower(ImpactMetric metric)
        => metric == ImpactMetric.TotalDamage || metric == ImpactMetric.Stagger;

    public static bool UsesUtility(ImpactMetric metric)
        => metric == ImpactMetric.ToolEfficiency || metric == ImpactMetric.SupplyEfficiency
            || metric == ImpactMetric.Revives || metric == ImpactMetric.LoneSurvivor;

    // The role bonus is a nudge, never a free win: both multipliers are clamped to this ceiling no
    // matter what the configuration file holds, so a hand-edited 5.0 in the .cfg cannot turn a support
    // player's row into a fabricated lead.
    public const double MaxRoleBonus = 1.3;


    // Metrics that are already a ratio and therefore are not renormalised against the squad leader.
    // Scan left this group in 1.13.0: the row counts first marks, so it is scaled against the squad
    // leader like the other counts instead of being read as a 0..1 rate.
    public static bool IsRate(ImpactMetric metric)
        => metric == ImpactMetric.WeakspotRate || metric == ImpactMetric.Accuracy
            || metric == ImpactMetric.SupplyEfficiency;

    public static bool IsReverse(ImpactMetric metric) => metric == ImpactMetric.Downs || metric == ImpactMetric.FriendlyFire;

    public static double RoleFactor(ImpactMetric metric, bool support, double firepowerBonus, double utilityBonus)
    {
        if (!support) return 1.0;
        if (UsesFirepower(metric)) return ClampBonus(firepowerBonus);
        if (UsesUtility(metric)) return ClampBonus(utilityBonus);
        return 1.0;
    }

    // 1.0 is the floor and MaxRoleBonus the ceiling: a bonus below one would punish a support player for
    // carrying the kit, and one above the ceiling is not a nudge any more.
    private static double ClampBonus(double bonus)
    {
        double value = Safe(bonus, 1.0);
        return value < 1.0 ? 1.0 : value > MaxRoleBonus ? MaxRoleBonus : value;
    }
    // A rate read from a handful of pellets is not evidence yet, so accuracy is scaled by how much of
    // the confidence sample was actually fired: 100% over three pellets must not outweigh a real team
    // contribution. At a fixed sample the figure still never moves backwards as accuracy rises.
    public static double AccuracyValue(ImpactInput player)
    {
        if (player == null || player.ShotsFired <= 0) return 0;
        double confidence = Math.Min(1.0, (double)player.ShotsFired / Math.Max(1, AccuracyConfidence));
        return Clamp01(DisplayedAccuracy(player) * confidence);
    }

    public static double DisplayedAccuracy(ImpactInput player)
    {
        if (player == null || player.ShotsFired <= 0) return 0;
        return Clamp01((double)player.Hits / player.ShotsFired);
    }

    public static double RawValue(ImpactInput player, ImpactMetric metric, double maxFriendlyFireDamage = 0, long maxFriendlyFireDeaths = 0)
    {
        if (player == null) return 0;
        ImpactCounters counters = player.Counters ?? new ImpactCounters();
        switch (metric)
        {
            case ImpactMetric.TotalDamage: return Safe(player.EnemyDamage, 0);
            case ImpactMetric.Stagger: return Safe(counters.Stagger, 0);
            case ImpactMetric.Kills: return Safe(counters.WeightedKills, 0);
            case ImpactMetric.Revives: return counters.Revives >= RescueFloor ? counters.Revives : 0;
            case ImpactMetric.WeakspotRate: return player.Hits > 0 ? Clamp01((double)player.WeakspotHits / player.Hits) : 0;
            case ImpactMetric.Accuracy: return AccuracyValue(player);
            case ImpactMetric.SupplyEfficiency: return counters.SupplyEfficiency;
            case ImpactMetric.ToolEfficiency: return Safe(counters.ScanFirstMarks, 0);
            case ImpactMetric.LoneSurvivor: return counters.LoneSurvivor > 0 ? counters.LoneSurvivor : 0;
            case ImpactMetric.Downs: return counters.Downs > 0 ? counters.Downs : 0;
            case ImpactMetric.FriendlyFire: return FriendlyFireSeverity(player, maxFriendlyFireDamage, maxFriendlyFireDeaths);
            // A weapon kill's sleeping share is worth the sleeping multiplier, exactly like the tier rows.
            case ImpactMetric.MeleeKills: return SleepingWeighted(counters.MeleeKills, counters.SleepingMeleeKills);
            case ImpactMetric.ScoutKills: return SleepingWeighted(counters.ScoutKills, counters.SleepingScoutKills);
            default: return 0;
        }
    }

    // Damage and lethal incidents use separate squad-relative scales, then contribute equally.
    public static double FriendlyFireSeverity(ImpactInput player, double maxDamage, long maxDeaths)
    {
        if (player == null) return 0;
        double sum = 0;
        int components = 0;
        if (double.IsFinite(maxDamage) && maxDamage > 0)
        {
            double damage = player.FriendlyFireObserved ? Safe(player.FriendlyFireDamage, 0) : 0;
            sum += Clamp01(damage / maxDamage);
            components++;
        }
        long deaths = player.Counters != null ? player.Counters.FriendlyFireDeaths : 0;
        if (maxDeaths > 0)
        {
            sum += Clamp01((double)deaths / maxDeaths);
            components++;
        }
        return components > 0 ? sum / components : 0;
    }
    // True when the player's own counter for a metric is actually defined, so an untouched metric
    // prints a dash instead of a fabricated zero, and a metric below its scoring floor never hands
    // out its sentence.
    public static bool HasValue(ImpactInput player, ImpactMetric metric)
    {
        if (player == null) return false;
        ImpactCounters counters = player.Counters ?? new ImpactCounters();
        switch (metric)
        {
            case ImpactMetric.Revives: return counters.Revives >= RescueFloor;
            case ImpactMetric.WeakspotRate: return player.Hits > 0;
            case ImpactMetric.Accuracy: return player.ShotsFired > 0;
            case ImpactMetric.SupplyEfficiency: return counters.SupplyUses > 0;
            case ImpactMetric.ToolEfficiency: return counters.ScanUses > 0;
            case ImpactMetric.MeleeKills: return counters.MeleeKills > 0;
            case ImpactMetric.ScoutKills: return counters.ScoutKills > 0;
            default: return true;
        }
    }

    public static ImpactReport Evaluate(IReadOnlyList<ImpactInput> squad, double[] weights, double firepowerBonus, double utilityBonus, bool loneSurvivorCounts = true)
    {
        double[] effective = Normalize(weights);
        if (squad == null) squad = new List<ImpactInput>();
        var report = new ImpactReport(effective, Safe(firepowerBonus, 1.0), Safe(utilityBonus, 1.0));

        int players = squad.Count;
        int metrics = Order.Length;
        double maxFriendlyFireDamage = 0;
        long maxFriendlyFireDeaths = 0;
        for (int player = 0; player < players; player++)
        {
            ImpactInput input = squad[player];
            if (input == null) continue;
            if (input.FriendlyFireObserved) maxFriendlyFireDamage = Math.Max(maxFriendlyFireDamage, Safe(input.FriendlyFireDamage, 0));
            maxFriendlyFireDeaths = Math.Max(maxFriendlyFireDeaths, input.Counters == null ? 0 : input.Counters.FriendlyFireDeaths);
        }
        var raw = new double[players][];
        for (int player = 0; player < players; player++)
        {
            raw[player] = new double[metrics];
            for (int metric = 0; metric < metrics; metric++)
                raw[player][metric] = RawValue(squad[player], Order[metric], maxFriendlyFireDamage, maxFriendlyFireDeaths);
        }

        // A failed expedition neither awards nor shows the last-one-standing metric, so it must not
        // score either: a figure the board hides must never move the total behind the player's back.
        if (!loneSurvivorCounts)
        {
            int lone = (int)ImpactMetric.LoneSurvivor;
            for (int player = 0; player < players; player++) raw[player][lone] = 0;
        }

        for (int metric = 0; metric < metrics; metric++)
        {
            double max = 0, min = double.MaxValue;
            for (int player = 0; player < players; player++)
            {
                double value = raw[player][metric];
                max = Math.Max(max, value);
                min = Math.Min(min, value);
            }
            if (players == 0 || !double.IsFinite(min)) min = 0;
            report.MaxRaw[metric] = max;
            report.MinRaw[metric] = min;
            int atMax = 0, atMin = 0;
            for (int player = 0; player < players; player++)
            {
                if (raw[player][metric] >= max) atMax++;
                if (raw[player][metric] <= min) atMin++;
            }
            report.CountAtMax[metric] = max > 0 ? atMax : 0;
            report.CountAtMin[metric] = max > 0 ? atMin : 0;
        }

        var entries = new List<ImpactEntry>();
        for (int player = 0; player < players; player++)
        {
            var normalized = new double[metrics];
            double weighted = 0, total = 0;
            for (int metric = 0; metric < metrics; metric++)
            {
                double norm = Normalize(Order[metric], raw[player][metric], report.MaxRaw[metric]);
                normalized[metric] = norm;
                double factor = effective[metric] * RoleFactor(Order[metric], squad[player].SupportRole, report.FirepowerBonus, report.UtilityBonus);
                weighted += factor * norm;
                total += factor;
            }
            double score = total > 0 ? 100.0 * weighted / total : 0;
            if (!double.IsFinite(score)) score = 0;
            entries.Add(new ImpactEntry(squad[player], raw[player], normalized, Clamp(score, 0, 100), 0, ImpactRating.TeamPillar, -1));
        }

        entries.Sort((left, right) =>
        {
            int byScore = right.Score.CompareTo(left.Score);
            if (byScore != 0) return byScore;
            return string.CompareOrdinal(left.Name, right.Name);
        });

        var ranked = new List<ImpactEntry>();
        for (int index = 0; index < entries.Count; index++)
        {
            int rank = 1;
            for (int other = 0; other < entries.Count; other++) if (entries[other].Score > entries[index].Score) rank++;
            ranked.Add(new ImpactEntry(entries[index].Input, entries[index].Raw, entries[index].Normalized, entries[index].Score,
                rank, ImpactRating.TeamPillar, -1));
        }
        report.Entries.AddRange(ranked);

        AssignRatings(report);
        return report;
    }

    // A sentence is earned, never dealt out: the board walks the score order from the top, and every
    // player takes the single figure they lead the squad in outright and that carried the most of
    // their own score. Only a real lead counts - an even split is not a lead, and a figure nobody
    // registered is not evidence - and a penalty row is judged by the severity of the penalty the
    // player carried, never by the points they kept for avoiding it. A sentence somebody above them
    // already wears is passed over for their own next best; a player who leads nothing at all reads
    // 平平无奇 instead of borrowing a sentence they never earned.
    public static string ScoreText(double score) => score.ToString("0.0", CultureInfo.InvariantCulture);
    public static bool IsDisplayedZero(double score) => double.IsFinite(score) && score >= 0 && ScoreText(score) == "0.0";

    private struct TitleCandidate
    {
        public ImpactRating Rating;
        public int Metric;
        public double Magnitude;
        public int Sequence;
    }

    private static void AssignRatings(ImpactReport report)
    {
        int players = report.Entries.Count;
        var ratings = new ImpactRating[players];
        var metrics = new int[players];
        var used = new HashSet<ImpactRating>();
        for (int index = 0; index < players; index++)
        {
            ImpactEntry row = report.Entries[index];
            ratings[index] = ImpactRating.TeamPillar;
            metrics[index] = -1;
            if (!row.ScoreAvailable) continue;
            foreach (TitleCandidate candidate in TitleCandidates(report, row, index))
            {
                // The higher placed player already wears it: pass to this player's next best lead.
                if (!used.Add(candidate.Rating)) continue;
                ratings[index] = candidate.Rating;
                metrics[index] = candidate.Metric;
                break;
            }
        }
        for (int index = 0; index < players; index++)
        {
            ImpactEntry row = report.Entries[index];
            report.Entries[index] = new ImpactEntry(row.Input, row.Raw, row.Normalized, row.Score, row.Rank,
                ratings[index], metrics[index]);
        }
    }

    // Every sentence one player could wear, best first: the figures they lead the squad in outright,
    // ordered by how much of their own score that figure actually carried.
    private static List<TitleCandidate> TitleCandidates(ImpactReport report, ImpactEntry row, int entry)
    {
        var candidates = new List<TitleCandidate>();
        int sequence = 0;
        for (int metric = 0; metric < Order.Length; metric++)
        {
            ImpactMetric kind = Order[metric];
            if (!HasValue(row.Input, kind) || report.Weights[metric] <= 0 || !report.MetricActive(kind)) continue;
            if (kind == ImpactMetric.Kills)
            {
                // The three weight classes are three sentences of their own. Each one is worth the share
                // of the kill row its own weighted count contributed, so the three add back up to that
                // row exactly and no kill is ever scored twice.
                AddKillTierCandidate(report, row, entry, candidates, metric, KillTier.Minor, sequence);
                AddKillTierCandidate(report, row, entry, candidates, metric, KillTier.Elite, sequence + 1);
                AddKillTierCandidate(report, row, entry, candidates, metric, KillTier.Boss, sequence + 2);
                sequence += 3;
                continue;
            }
            if (IsReverse(kind))
            {
                // A penalty row's lead fact is the heaviest penalty in the squad; what it earned its
                // wearer is how severe that penalty was, not the points they kept by avoiding it.
                if (!WorstInSquad(report, entry, metric)) { sequence++; continue; }
                candidates.Add(new TitleCandidate
                {
                    Rating = TitleFor(kind, row.Input.Counters), Metric = metric,
                    Magnitude = PenaltyContribution(report, row, metric), Sequence = sequence++
                });
                continue;
            }
            if (!LeadsSquad(report, entry, metric)) { sequence++; continue; }
            candidates.Add(new TitleCandidate
            {
                Rating = TitleFor(kind, row.Input.Counters), Metric = metric,
                Magnitude = Math.Abs(Contribution(report, row, metric)), Sequence = sequence++
            });
        }
        candidates.Sort((left, right) =>
        {
            int byMagnitude = right.Magnitude.CompareTo(left.Magnitude);
            if (byMagnitude != 0) return byMagnitude;
            return left.Sequence.CompareTo(right.Sequence);
        });
        return candidates;
    }

    // One weight class of the kill row as a candidate of its own. The lead is decided on the raw count
    // the board prints for that class; the value it carries is that class's share of the row's score.
    private static void AddKillTierCandidate(ImpactReport report, ImpactEntry row, int entry, List<TitleCandidate> candidates,
        int metric, KillTier tier, int sequence)
    {
        ImpactCounters counters = row.Input.Counters;
        if (counters == null || counters.KillCount(tier) <= 0) return;
        if (!report.IsGoldBy(entry, other => other.Counters == null ? 0 : other.Counters.KillCount(tier))) return;
        double rowValue = WeightedKillValue(counters, KillTier.Minor)
            + WeightedKillValue(counters, KillTier.Elite)
            + WeightedKillValue(counters, KillTier.Boss);
        double share = rowValue > 0 ? Clamp01(WeightedKillValue(counters, tier) / rowValue) : 0;
        double magnitude = Contribution(report, row, metric) * share;
        candidates.Add(new TitleCandidate
        {
            Rating = TitleForTier(tier), Metric = metric,
            Magnitude = double.IsFinite(magnitude) ? magnitude : 0, Sequence = sequence
        });
    }

    // True when this entry is the squad's outright best at one metric, which is what makes the
    // sentence about that metric rather than about whatever is left over.
    // A reverse metric's unique minimum is its leader; ordinary metrics use the unique maximum.
    private static bool LeadsSquad(ImpactReport report, int entry, int metric)
    {
        if (entry < 0 || entry >= report.Entries.Count || metric < 0 || metric >= Order.Length) return false;
        if (Order[metric] == ImpactMetric.Accuracy) return report.IsGold(entry, ImpactMetric.Accuracy);
        double value = report.Entries[entry].Raw[metric];
        if (IsReverse(Order[metric]))
            return report.CountAtMin[metric] == 1 && value <= report.MinRaw[metric] + 1e-9;
        return value > 0 && report.CountAtMax[metric] == 1 && value >= report.MaxRaw[metric] - 1e-9;
    }

    // The squad's outright worst at a reverse metric, and only when there is something to be worst at:
    // nobody can be the friendly-fire hazard of a squad that took no friendly fire.
    private static bool WorstInSquad(ImpactReport report, int entry, int metric)
    {
        if (entry < 0 || entry >= report.Entries.Count || metric < 0 || metric >= Order.Length) return false;
        double worst = report.Entries[entry].Raw[metric];
        return worst > 0 && report.CountAtMax[metric] == 1 && worst >= report.MaxRaw[metric] - 1e-9;
    }

    // What one of the player's own metrics carried for their score, as the board computed it.
    private static double Contribution(ImpactReport report, ImpactEntry row, int metric)
    {
        double factor = report.Weights[metric]
            * RoleFactor(Order[metric], row.Input.SupportRole, report.FirepowerBonus, report.UtilityBonus);
        double contribution = factor * row.Normalized[metric];
        return double.IsFinite(contribution) ? contribution : 0;
    }

    // How heavy a penalty a player carries on a reverse row: the weight in force there times how much
    // of the squad's worst penalty it is. The player who carries the whole of it is worth the full
    // weight, which is what makes a penalty row comparable with a positive one.
    private static double PenaltyContribution(ImpactReport report, ImpactEntry row, int metric)
    {
        double factor = report.Weights[metric]
            * RoleFactor(Order[metric], row.Input.SupportRole, report.FirepowerBonus, report.UtilityBonus);
        double max = report.MaxRaw[metric];
        double severity = max > 0 ? Clamp01(row.Raw[metric] / max) : 0;
        double magnitude = factor * severity;
        return double.IsFinite(magnitude) ? magnitude : 0;
    }

    // A merged kill row borrows the title of whichever weight class the player is best at, so the
    // three class titles stay reachable even when the rows are merged.
    public static ImpactRating TitleFor(ImpactMetric metric, ImpactCounters counters)
    {
        switch (metric)
        {
            case ImpactMetric.TotalDamage: return ImpactRating.Damage;
            case ImpactMetric.Stagger: return ImpactRating.Stagger;
            case ImpactMetric.Kills:
                if (counters == null) return ImpactRating.MinorKills;
                if (counters.BossKills > 0 && counters.BossKills >= counters.EliteKills) return ImpactRating.BossKills;
                if (counters.EliteKills > 0) return ImpactRating.EliteKills;
                return ImpactRating.MinorKills;
            case ImpactMetric.Revives: return ImpactRating.Revives;
            case ImpactMetric.WeakspotRate: return ImpactRating.Weakspot;
            case ImpactMetric.Accuracy: return ImpactRating.Accuracy;
            case ImpactMetric.SupplyEfficiency: return ImpactRating.Supply;
            case ImpactMetric.ToolEfficiency: return ImpactRating.Scan;
            case ImpactMetric.LoneSurvivor: return ImpactRating.LoneSurvivor;
            case ImpactMetric.FriendlyFire: return ImpactRating.FriendlyFire;
            case ImpactMetric.MeleeKills: return ImpactRating.MeleeKills;
            case ImpactMetric.ScoutKills: return ImpactRating.ScoutKills;
            default: return ImpactRating.Downs;
        }
    }

    // One weight class of the kill row, its own sentence. Three classes, three sentences, so a squad can
    // read who cleared the small ones, who broke the elites and who took the boss without the three
    // sharing a single merged word.
    public static ImpactRating TitleForTier(KillTier tier)
    {
        switch (tier)
        {
            case KillTier.Minor: return ImpactRating.MinorKills;
            case KillTier.Elite: return ImpactRating.EliteKills;
            default: return ImpactRating.BossKills;
        }
    }

    // One metric against the squad leader. Rate metrics keep their own 0..1 value; everything else
    // is scaled by the leader, except deaths which invert so fewer is better. A metric nobody
    // scored counts for nothing, which is also what keeps a squad of one from dividing by zero.
    public static double Normalize(ImpactMetric metric, double raw, double max)
    {
        double value = Safe(raw, 0);
        if (IsRate(metric)) return Clamp01(value);
        if (!(max > 0)) return 0;
        double normalized = value / max;
        if (IsReverse(metric)) normalized = 1 - normalized;
        return Clamp01(normalized);
    }

    private static double[] Normalize(double[] weights)
    {
        if (weights == null || weights.Length != Order.Length) return (double[])DefaultWeights.Clone();
        var copy = new double[Order.Length];
        for (int index = 0; index < copy.Length; index++) copy[index] = Safe(weights[index], 0);
        return copy;
    }

    private static double Safe(double value, double fallback) { return double.IsFinite(value) ? value : fallback; }
    private static double Clamp(double value, double low, double high) { return value < low ? low : value > high ? high : value; }
    public static double Clamp01(double value) { return Clamp(Safe(value, 0), 0, 1); }
}

// The support kits whose owner is credited with the firepower and utility bonuses. Matched by
// substring against the equipped tool's name, so a modded tracker is picked up as soon as its name
// contains one of these and the whole list can be edited in the config file.
public static class SupportTools
{
    // The kits that are carried for the team rather than for damage. Matched case-insensitively as a
    // substring, so a modded tracker named "Bio Tracker Mk2" is caught by "Bio Tracker" and a localised
    // name is caught by its own entry. Glue, C-Foam and the tracker are the three the user named; the
    // lock melter is the same kind of tool (it spends itself on a door, never on an enemy).
    public const string DefaultNames = "Bio Tracker,Enemy Scanner,生物追踪仪,Glue Gun,Glue Grenade,C-Foam,胶枪,粘胶,泡沫,Lock Melter,开锁器";
    // Tools that are deliberately NOT support: a sentry or a tripmine shoots for its owner, so it is
    // firepower the owner deployed rather than a kit they gave up their gun for. The exclusion always
    // wins, however the tool is named.
    public const string DefaultExcluded = "Sentry,哨戒炮,哨戒,Tripmine,Mine,绊雷,地雷";

    public static string[] Parse(string names)
    {
        if (string.IsNullOrWhiteSpace(names)) return new string[0];
        string[] parts = names.Split(',');
        var result = new List<string>();
        foreach (string part in parts)
        {
            string trimmed = part.Trim();
            if (trimmed.Length > 0) result.Add(trimmed);
        }
        return result.ToArray();
    }

    // Kept for the callers that only want the named-inclusion test.
    public static bool IsSupport(IEnumerable<string> toolNames, string configured)
        => IsSupport(toolNames, configured, null, false);

    // The full policy. `othersAsSupport` is what makes a modded tool that is not on any list count as a
    // support kit: the user asked for non-damaging modded tools to be included, and a mod's tool cannot be
    // known by name in advance. The exclusion list is checked first and always wins, so a modded sentry or
    // tripmine is never dragged in by that rule.
    public static bool IsSupport(IEnumerable<string> toolNames, string included, string excluded, bool othersAsSupport)
    {
        if (toolNames == null) return false;
        string[] wanted = Parse(included);
        string[] refused = Parse(excluded);
        foreach (string name in toolNames)
        {
            if (string.IsNullOrEmpty(name)) continue;
            bool blocked = false;
            foreach (string candidate in refused)
                if (name.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0) { blocked = true; break; }
            if (blocked) continue;
            foreach (string candidate in wanted)
                if (name.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (othersAsSupport) return true;
        }
        return false;
    }
}
