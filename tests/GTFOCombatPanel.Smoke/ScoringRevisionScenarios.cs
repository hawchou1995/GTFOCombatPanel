using System;
using System.IO;
using System.Linq;
using HawChow.GTFOCombatPanel;

// 1.18.0 scoring revision: the reweighted defaults, the sleeping-kill model, the two new cell figures and
// the configurable settlement sentences. Every assertion here runs the shipping model - no Unity, no
// BepInEx - so the numbers below are the numbers the settlement board scores with.
internal static partial class Program
{
    internal static void RunScoringRevisionScenarios()
    {
        // ---- the published weights: lone survivor leads, stagger is second --------------------------
        Check(Math.Abs(ImpactScoring.DefaultWeights[(int)ImpactMetric.LoneSurvivor] - 1.60) < 1e-9,
            "the default lone-survivor weight is 1.60");
        Check(Math.Abs(ImpactScoring.DefaultWeights[(int)ImpactMetric.Stagger] - 1.40) < 1e-9,
            "the default stagger weight stays 1.40");
        Check(ImpactScoring.DefaultWeights[(int)ImpactMetric.LoneSurvivor] > ImpactScoring.DefaultWeights[(int)ImpactMetric.Stagger],
            "lone survivor is the heaviest default weight");
        int heaviest = 0, second = 0;
        for (int index = 0; index < ImpactScoring.DefaultWeights.Length; index++)
        {
            if (index == (int)ImpactMetric.LoneSurvivor) continue;
            if (ImpactScoring.DefaultWeights[index] > ImpactScoring.DefaultWeights[second]) second = index;
            if (ImpactScoring.DefaultWeights[index] > ImpactScoring.DefaultWeights[heaviest]) heaviest = index;
        }
        Check(heaviest == (int)ImpactMetric.Stagger, "stagger is the second heaviest default weight");
        Check(second == (int)ImpactMetric.Stagger, "and no other row sits between them");
        double[] previous = { 0.95, 1.40, 0.70, 0.28, 0.44, 0.28, 0.78, 0.80, 1.60, 0.55, 0.75, 0.55, 0.60 };
        Check(ImpactScoring.DefaultWeights.SequenceEqual(previous), "every other default weight keeps its previous value");
        Check(ImpactScoring.Describe(ImpactScoring.DefaultWeights) == ImpactScoring.DefaultWeightsText,
            "the published default text is the default list");
        Check(ScoringSettings.Defaults().Weights["LoneSurvivor"] == ImpactScoring.DefaultWeights[(int)ImpactMetric.LoneSurvivor]
            && ScoringSettings.Defaults().Weights["Stagger"] == 1.4,
            "the scoring file's defaults and the code's defaults are the same list");
        Check(ScoringSettings.Defaults().EffectiveWeights().SequenceEqual(ImpactScoring.DefaultWeights),
            "the default scoring file resolves to the same thirteen weights");

        // ---- the previous published list migrates, a hand-edited one never does ----------------------
        Check(ImpactScoring.IsLegacyDefaultWeights(ImpactScoring.PreviousThirteenMetricWeightsText),
            "the previous thirteen-metric default list is recognised for migration");
        Check(!ImpactScoring.IsLegacyDefaultWeights(ImpactScoring.DefaultWeightsText),
            "this release's own list is not mistaken for a legacy one");
        var previousFile = ScoringSettings.Parse(PreviousDefaultJson());
        Check(Math.Abs(previousFile.Weights["LoneSurvivor"] - 1.60) < 1e-9 && Math.Abs(previousFile.Weights["Stagger"] - 1.40) < 1e-9,
            "a file still holding the previous published weights migrates to this release's");
        var handEdited = ScoringSettings.Parse(PreviousDefaultJson().Replace("\"LoneSurvivor\":0.47", "\"LoneSurvivor\":0.33"));
        Check(Math.Abs(handEdited.Weights["LoneSurvivor"] - 0.33) < 1e-9 && Math.Abs(handEdited.Weights["Stagger"] - 1.40) < 1e-9,
            "an explicit custom weight is never silently overwritten");

        // ---- the sleeping-kill model -----------------------------------------------------------------
        Check(Math.Abs(ImpactScoring.DefaultSleepingKillMultiplier - 2.0) < 1e-9 && Math.Abs(ImpactScoring.SleepingKillMultiplier - 2.0) < 1e-9,
            "a sleeping kill is worth two awake ones by default");
        Check(Math.Abs(ImpactScoring.WeightedKills(2, 0, 0) - 1.0) < 1e-9, "two awake minor kills are worth one minor-kill point");
        Check(Math.Abs(new ImpactCounters { MinorKills = 2, SleepingMinorKills = 2 }.WeightedKills - 1.0) < 1e-9,
            "two sleeping minor kills are worth exactly what two awake ones are: a weight class scores its total and nothing else");
        Check(Math.Abs(ImpactScoring.WeightedKills(1, 1, 1) - 3.0) < 1e-9,
            "the sleeping share never reaches the weight of a tier");
        Check(Math.Abs(new ImpactCounters { MinorKills = 1, SleepingMinorKills = 5 }.WeightedKills - 0.5) < 1e-9,
            "a sleeping subset that outran its total cannot change a class total either");
        var awakeAndUnknown = new ImpactCounters { MinorKills = 4, UnknownStateMinorKills = 4 };
        Check(Math.Abs(awakeAndUnknown.WeightedKills - 2.0) < 1e-9,
            "kills of unreadable state are worth an awake kill, never a sleeping one");
        // The three fields are separate on purpose: the tier rows score their total, and only the melee and
        // Scout rows carry a multiplier of their own, so a stealth-heavy squad cannot inflate the weight
        // classes and a melee player does not silently inherit the Scout's tuning.
        ImpactScoring.SetSleepingKillMultiplier(3.0);
        Check(Math.Abs(new ImpactCounters { MinorKills = 2, SleepingMinorKills = 2 }.WeightedKills - 1.0) < 1e-9,
            "the legacy multiplier no longer reaches any tier row, which is the whole point of the split");
        Check(Math.Abs(ImpactScoring.SleepingWeighted(2, 2) - 6.0) < 1e-9,
            "the legacy multiplier still drives the generic sleeping-weighted count the two rows are built on");
        ImpactScoring.SetSleepingKillMultipliers(3.0, 4.0);
        Check(Math.Abs(ImpactScoring.RawValue(new ImpactInput { Counters = new ImpactCounters { MeleeKills = 2, SleepingMeleeKills = 2 } }, ImpactMetric.MeleeKills) - 6.0) < 1e-9
            && Math.Abs(ImpactScoring.RawValue(new ImpactInput { Counters = new ImpactCounters { ScoutKills = 2, SleepingScoutKills = 2 } }, ImpactMetric.ScoutKills) - 8.0) < 1e-9,
            "the melee and Scout rows carry a multiplier each, so one can be tuned without the other");
        ImpactScoring.SetSleepingKillMultipliers(0.5, 50);
        Check(Math.Abs(ImpactScoring.SleepingMeleeKillMultiplier - ImpactScoring.DefaultSleepingKillMultiplier) < 1e-9
            && Math.Abs(ImpactScoring.SleepingScoutKillMultiplier - ImpactScoring.MaxSleepingKillMultiplier) < 1e-9,
            "each of the two is bounded like the legacy field: below one falls back to the default, above the ceiling is clamped");
        ImpactScoring.SetSleepingKillMultiplier(0.5);
        Check(Math.Abs(ImpactScoring.SleepingKillMultiplier - ImpactScoring.DefaultSleepingKillMultiplier) < 1e-9,
            "a multiplier below one falls back to the published default instead of punishing a stealth kill");
        ImpactScoring.SetSleepingKillMultiplier(50);
        Check(Math.Abs(ImpactScoring.SleepingKillMultiplier - ImpactScoring.MaxSleepingKillMultiplier) < 1e-9,
            "the multiplier is bounded by its documented ceiling");
        ImpactScoring.ResetThresholds();
        Check(Math.Abs(ImpactScoring.SleepingKillMultiplier - 2.0) < 1e-9
            && Math.Abs(ImpactScoring.SleepingMeleeKillMultiplier - 2.0) < 1e-9
            && Math.Abs(ImpactScoring.SleepingScoutKillMultiplier - 2.0) < 1e-9,
            "ResetThresholds restores all three sleeping multipliers");

        // The JSON carries it, and its bounds are enforced like every other figure.
        Check(Math.Abs(ScoringSettings.Defaults().SleepingKillMultiplier - 2.0) < 1e-9, "the scoring file's default multiplier is two");
        Check(Math.Abs(ScoringSettings.Defaults().SleepingMeleeKillMultiplier - 2.0) < 1e-9
            && Math.Abs(ScoringSettings.Defaults().SleepingScoutKillMultiplier - 2.0) < 1e-9,
            "a fresh file carries the melee and Scout multipliers too, each defaulting to two");
        ScoringSettings splitFile = ScoringSettings.Parse("{\"SleepingKillMultiplier\": 3, \"SleepingScoutKillMultiplier\": 5}");
        Check(Math.Abs(splitFile.SleepingMeleeKillMultiplier - 3.0) < 1e-9 && Math.Abs(splitFile.SleepingScoutKillMultiplier - 5.0) < 1e-9,
            "the older field still feeds the melee row when the newer one is absent, and the newer one wins where it is written");
        Check(Math.Abs(ScoringSettings.Parse("{\"SleepingScoutKillMultiplier\": 4}").SleepingMeleeKillMultiplier - 2.0) < 1e-9,
            "a file that names neither falls back to the published default rather than to zero");
        Check(Math.Abs(ScoringSettings.Parse("{\"SleepingKillMultiplier\": 3}").SleepingKillMultiplier - 3.0) < 1e-9,
            "the multiplier round trips through the scoring file");
        Check(Math.Abs(ScoringSettings.Parse(ScoringSettings.Defaults().ToJson()).SleepingKillMultiplier - 2.0) < 1e-9,
            "a written scoring file reads its multiplier back");
        foreach (string badMultiplier in new[] { "{\"SleepingKillMultiplier\": 0.5}", "{\"SleepingKillMultiplier\": 99}", "{\"SleepingKillMultiplier\": \"x\"}" })
        {
            bool rejected = false;
            try { ScoringSettings.Parse(badMultiplier); } catch (Exception) { rejected = true; }
            Check(rejected, "an out-of-range sleeping multiplier is rejected: " + badMultiplier);
        }

        // ---- a kill carries the state it was landed in ------------------------------------------------
        var states = new ImpactCounters();
        KillClassifier.Credit(states, KillTier.Minor, DamageKind.Bullet, false, true);
        Check(states.MinorKills == 1 && states.SleepingMinorKills == 1 && states.UnknownStateMinorKills == 0,
            "a kill landed on a sleeping enemy records the sleeping subset");
        KillClassifier.Credit(states, KillTier.Minor, DamageKind.Bullet, false, false);
        Check(states.MinorKills == 2 && states.SleepingMinorKills == 1 && states.UnknownStateMinorKills == 0,
            "an awake kill is not folded into the sleeping subset");
        KillClassifier.Credit(states, KillTier.Minor, DamageKind.Unknown, false);
        Check(states.MinorKills == 3 && states.UnknownStateMinorKills == 1 && states.SleepingMinorKills == 1,
            "a kill of unreadable state is recorded as unknown, never as a sleeping one");
        var meleeStates = new ImpactCounters();
        KillClassifier.Credit(meleeStates, KillTier.Elite, DamageKind.Melee, false, true);
        Check(meleeStates.EliteKills == 1 && meleeStates.SleepingEliteKills == 1 && meleeStates.MeleeKills == 1 && meleeStates.SleepingMeleeKills == 1,
            "a sleeping melee kill credits the elite tier, the melee subset and its sleeping share");
        var scoutStates = new ImpactCounters();
        KillClassifier.Credit(scoutStates, KillTier.Boss, DamageKind.Push, true, null);
        Check(scoutStates.BossKills == 1 && scoutStates.UnknownStateBossKills == 1 && scoutStates.ScoutKills == 1 && scoutStates.UnknownStateScoutKills == 1,
            "an unreadable Scout kill is recorded as unknown on both the tier and the Scout subset");
        var legacyCredit = new ImpactCounters();
        KillClassifier.Credit(legacyCredit, KillTier.Minor);
        KillClassifier.Credit(legacyCredit, KillTier.Minor, DamageKind.Melee, false);
        Check(legacyCredit.MinorKills == 2 && legacyCredit.MeleeKills == 1 && legacyCredit.UnknownStateMinorKills == 1,
            "the three- and four-argument credits still work, and their unreadable state lands in the unknown subset");
        Check(states.AwakeKills == 1 && states.UnknownStateKills == 1 && states.SleepingKills == 1,
            "the awake figure is the total minus both subsets");
        var overrun = new ImpactCounters { MinorKills = 3, SleepingMinorKills = 2, UnknownStateMinorKills = 5 };
        Check(overrun.AwakeKillCount(KillTier.Minor) == 0 && overrun.SleepingKillCount(KillTier.Minor) == 2,
            "the two subsets are bounded by the total they belong to, so the awake figure can never go negative");
        Check(ImpactCounters.AwakePart(0, 2, 2) == 0 && ImpactCounters.AwakePart(-4, 1, 1) == 0,
            "an empty or negative total has no awake part");

        // Melee and Scout rows score their sleeping share exactly like the tier rows.
        var meleeScoring = new ImpactCounters { MeleeKills = 2, SleepingMeleeKills = 1 };
        var scoutScoring = new ImpactCounters { ScoutKills = 2, SleepingScoutKills = 2 };
        Check(Math.Abs(ImpactScoring.RawValue(new ImpactInput { Counters = meleeScoring }, ImpactMetric.MeleeKills) - 3.0) < 1e-9,
            "the melee row is worth the sleeping share at double value");
        Check(Math.Abs(ImpactScoring.RawValue(new ImpactInput { Counters = scoutScoring }, ImpactMetric.ScoutKills) - 4.0) < 1e-9,
            "the Scout row is worth the sleeping share at double value");

        // ---- Reset and CopyFrom cover every new field ------------------------------------------------
        var filled = new ImpactCounters
        {
            SleepingMinorKills = 1, SleepingEliteKills = 2, SleepingBossKills = 3, SleepingMeleeKills = 4, SleepingScoutKills = 5,
            UnknownStateMinorKills = 6, UnknownStateEliteKills = 7, UnknownStateBossKills = 8, UnknownStateMeleeKills = 9, UnknownStateScoutKills = 10,
            SelfSupplyUses = 11, SelfAmmoUses = 12, HealthLost = 345.5
        };
        var copy = new ImpactCounters();
        copy.CopyFrom(filled);
        Check(copy.SleepingMinorKills == 1 && copy.SleepingEliteKills == 2 && copy.SleepingBossKills == 3
            && copy.SleepingMeleeKills == 4 && copy.SleepingScoutKills == 5,
            "CopyFrom carries every sleeping subset");
        Check(copy.UnknownStateMinorKills == 6 && copy.UnknownStateEliteKills == 7 && copy.UnknownStateBossKills == 8
            && copy.UnknownStateMeleeKills == 9 && copy.UnknownStateScoutKills == 10,
            "CopyFrom carries every unknown-state subset");
        Check(copy.SelfSupplyUses == 11 && copy.SelfAmmoUses == 12 && Math.Abs(copy.HealthLost - 345.5) < 1e-9,
            "CopyFrom carries the resupply counts and the lifetime lost");
        copy.Reset();
        Check(copy.SleepingKills == 0 && copy.UnknownStateKills == 0 && copy.SelfSupplyUses == 0 && copy.SelfAmmoUses == 0 && copy.HealthLost == 0,
            "Reset clears every new field with the expedition");

        // Own resupply and lifetime lost are recorded the way the collection layer needs them.
        var ownSupply = new ImpactCounters();
        ownSupply.CreditSelfSupply(true); ownSupply.CreditSelfSupply(false);
        Check(ownSupply.SelfSupplyUses == 2 && ownSupply.SelfAmmoUses == 1, "own resupply counts every pack and the ammo packs among them");
        var lost = new ImpactCounters();
        lost.AddHealthLost(120); lost.AddHealthLost(double.NaN); lost.AddHealthLost(-5); lost.AddHealthLost(80);
        Check(Math.Abs(lost.HealthLost - 200) < 1e-9, "lifetime lost accumulates only real losses");

        // ---- the cells that carry a figure beyond their count -------------------------------------------
        // The three weight classes print their total and nothing else: the sleeping share of a kill is not a
        // quality of the class, so it neither scores there nor appears there. The melee and Scout rows are
        // the two that do report a sleeping subset, and each reports it as its own column.
        var splitCounters = new ImpactCounters { MinorKills = 3, SleepingMinorKills = 1, UnknownStateMinorKills = 1 };
        var splitEntry = new ImpactEntry(new ImpactInput { Name = "Split", Counters = splitCounters }, null, null, 40, 1, ImpactRating.MinorKills, (int)ImpactMetric.Kills);
        Check(ImpactBoard.MetricFields(splitEntry, ImpactMetric.Kills, KillTier.Minor, true, true)[0] == "3",
            "a weight class prints its total only, so a sleeping or unreadable share never reaches that cell");
        Check(ImpactBoard.MetricFields(splitEntry, ImpactMetric.Kills, KillTier.Elite, true, true)[0] == "0",
            "a tier with nothing in it keeps its plain zero");
        string[] mergedFields = ImpactBoard.MetricFields(splitEntry, ImpactMetric.Kills, KillTier.Minor, false, true);
        Check(mergedFields.Length == 3 && mergedFields[0] == "3小怪" && mergedFields[1] == "0精英" && mergedFields[2] == "0BOSS",
            "the legacy merged row carries the same bare totals, each with the class word its label cannot hold");
        var scoutRow = new ImpactCounters { ScoutKills = 2, SleepingScoutKills = 1 };
        scoutRow.AddScoutDamage(120.5);
        var scoutEntry = new ImpactEntry(new ImpactInput { Name = "Scout", Counters = scoutRow }, null, null, 40, 1, ImpactRating.ScoutKills, (int)ImpactMetric.ScoutKills);
        string[] scoutFields = ImpactBoard.MetricFields(scoutEntry, ImpactMetric.ScoutKills, KillTier.Minor, true, true);
        Check(scoutFields.Length == 3 && scoutFields[0] == "2" && scoutFields[1] == "1"
            && scoutFields[2] == CombatStatsFormatter.FormatDamage(120.5),
            "the Scout row is three columns in one cell: its total kills, the part landed on a sleeping one, and the damage that went into them");
        var meleeRow = new ImpactCounters { MeleeKills = 4, MeleeKillsObserved = true };
        var meleeEntry = new ImpactEntry(new ImpactInput { Name = "Melee", Counters = meleeRow }, null, null, 40, 1, ImpactRating.MeleeKills, (int)ImpactMetric.MeleeKills);
        string[] meleeFields = ImpactBoard.MetricFields(meleeEntry, ImpactMetric.MeleeKills, KillTier.Minor, true, true);
        Check(meleeFields.Length == 3 && meleeFields[0] == "4" && meleeFields[1] == "0",
            "the melee row reports its sleeping column even when it is empty, so the two rows line up column for column");
        var supplyCounters = new ImpactCounters();
        supplyCounters.RecordSupply(30, 100); supplyCounters.CreditSelfSupply(true);
        var supplyEntry = new ImpactEntry(new ImpactInput { Name = "Supplier", Counters = supplyCounters }, null, null, 40, 1, ImpactRating.Supply, (int)ImpactMetric.SupplyEfficiency);
        Check(ImpactBoard.MetricFields(supplyEntry, ImpactMetric.SupplyEfficiency, KillTier.Minor, false, true)[0] == "30.0% / 1 / 1",
            "the supply cell shows the utilization and this player's own pack use");
        var efficiencyOnly = new ImpactCounters();
        efficiencyOnly.RecordSupply(30, 100);
        var efficiencyEntry = new ImpactEntry(new ImpactInput { Name = "NoOwnUse", Counters = efficiencyOnly }, null, null, 40, 1, ImpactRating.Supply, (int)ImpactMetric.SupplyEfficiency);
        Check(ImpactBoard.MetricFields(efficiencyEntry, ImpactMetric.SupplyEfficiency, KillTier.Minor, false, true)[0] == "30.0%",
            "a row whose own pack use was never observed keeps its plain utilization instead of a fabricated zero");
        Check(ImpactBoard.MetricFields(efficiencyEntry, ImpactMetric.SupplyEfficiency, KillTier.Minor, false, true)[0]
            != ImpactBoard.MetricFields(supplyEntry, ImpactMetric.SupplyEfficiency, KillTier.Minor, false, true)[0],
            "an observed own-pack count really does change the cell");
        var downCounters = new ImpactCounters();
        downCounters.CreditDown(); downCounters.AddHealthLost(150);
        var downEntry = new ImpactEntry(new ImpactInput { Name = "Downed", Counters = downCounters }, null, null, 40, 1, ImpactRating.Downs, (int)ImpactMetric.Downs);
        Check(ImpactBoard.MetricFields(downEntry, ImpactMetric.Downs, KillTier.Minor, false, true)[0] == "1 / 150.0",
            "the down cell shows the count and the lifetime it cost");
        var plainDownEntry = new ImpactEntry(new ImpactInput { Name = "Undamaged", Counters = new ImpactCounters() }, null, null, 40, 1, ImpactRating.Downs, (int)ImpactMetric.Downs);
        Check(ImpactBoard.MetricFields(plainDownEntry, ImpactMetric.Downs, KillTier.Minor, false, true)[0] == "0",
            "nothing lost means the down cell keeps its bare count");
        var noRowsAdded = ImpactBoard.Build(ImpactScoring.Evaluate(new[] { splitEntry.Input, supplyEntry.Input }, ImpactScoring.DefaultWeights, 1.0, 1.0), true, true);
        Check(noRowsAdded.Rows == 30 && ImpactBoard.FixedRows == 14, "the new figures arrive inside existing cells; no row was added");
        string previousLanguage = CombatStrings.Language;
        CombatStrings.Language = "en";
        Check(CombatStrings.SupplyCell("30.0%", 1, 1) == "30.0% / 1 / 1"
            && CombatStrings.DownsCell("1", 150) == "1 / 150.0",
            "the cell formats follow the language setting");
        CombatStrings.Language = previousLanguage;

        // ---- sentences: only a real, unique lead earns one -------------------------------------------
        var leader = new ImpactInput { Name = "Leader", EnemyDamage = 400 };
        var runnerUp = new ImpactInput { Name = "RunnerUp", EnemyDamage = 100 };
        var leadReport = ImpactScoring.Evaluate(new[] { leader, runnerUp }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        Check(leadReport.Entries[0].Rating == ImpactRating.Damage && leadReport.Entries[0].RatingMetric == (int)ImpactMetric.TotalDamage,
            "the unique damage leader wears the firepower sentence");
        Check(leadReport.Entries[1].Rating == ImpactRating.TeamPillar && leadReport.Entries[1].RatingMetric == -1,
            "a player who leads nothing reads 平平无奇 instead of borrowing a sentence");
        Check(CombatStrings.Title(leadReport.Entries[1].Rating) == "平平无奇", "the neutral sentence is 平平无奇");
        var tieLeft = new ImpactInput { Name = "TieLeft", EnemyDamage = 40 };
        var tieRight = new ImpactInput { Name = "TieRight", EnemyDamage = 40 };
        var tieReport = ImpactScoring.Evaluate(new[] { tieLeft, tieRight }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        Check(tieReport.Entries.All(entry => entry.Rating == ImpactRating.TeamPillar && entry.RatingMetric == -1),
            "a stable tie awards nothing: nobody leads the other");
        Check(tieReport.Entries.All(entry => entry.Rating != ImpactRating.ZeroScore), "a tied positive score is still not a zero-score row");
        var zeroSquadRevision = ImpactScoring.Evaluate(new[]
        {
            new ImpactInput { Name = "Z1" }, new ImpactInput { Name = "Z2" }, new ImpactInput { Name = "Z3" }, new ImpactInput { Name = "Z4" }
        }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        Check(zeroSquadRevision.Entries.All(entry => entry.Rating == ImpactRating.TeamPillar), "zero data has no outstanding top and uses the repeatable neutral title");
        Check(zeroSquadRevision.Entries.Select(entry => entry.Rating).Distinct().Count() == 1, "and it is the only repeated sentence there");

        // ---- a penalty row is decided by the penalty, not by the points kept for avoiding it --------
        var clean = new ImpactInput { Name = "Clean", EnemyDamage = 300 };
        var careless = new ImpactInput { Name = "Careless", EnemyDamage = 50 };
        careless.Counters.CreditDown(); careless.Counters.CreditDown(); careless.Counters.CreditDown();
        var penaltyReport = ImpactScoring.Evaluate(new[] { clean, careless }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        ImpactEntry carelessEntry = penaltyReport.Entries.First(entry => entry.Name == "Careless");
        ImpactEntry cleanEntry = penaltyReport.Entries.First(entry => entry.Name == "Clean");
        Check(carelessEntry.Rating == ImpactRating.Downs && carelessEntry.RatingMetric == (int)ImpactMetric.Downs,
            "the squad's most-downed player wears the down sentence");
        Check(cleanEntry.Rating == ImpactRating.Damage,
            "the player who never went down is never handed the down sentence for avoiding it");
        var hazard = new ImpactInput { Name = "Hazard", EnemyDamage = 60, FriendlyFireObserved = true, FriendlyFireDamage = 200 };
        hazard.Counters.FriendlyFireDeaths = 2;
        var hazardMate = new ImpactInput { Name = "HazardMate", EnemyDamage = 200 };
        var hazardReport = ImpactScoring.Evaluate(new[] { hazard, hazardMate }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        Check(hazardReport.Entries.First(entry => entry.Name == "Hazard").Rating == ImpactRating.FriendlyFire,
            "the worst friendly-fire player wears the friendly-fire sentence");
        Check(hazardReport.Entries.First(entry => entry.Name == "Hazard").RatingMetric == (int)ImpactMetric.FriendlyFire,
            "the friendly-fire sentence names the row it came from");
        Check(hazardReport.Entries.First(entry => entry.Name == "HazardMate").Rating == ImpactRating.Damage,
            "a teammate with no friendly fire is never called 敌我不分");
        var noFriendlyFire = ImpactScoring.Evaluate(new[] { new ImpactInput { Name = "A1", EnemyDamage = 10 }, new ImpactInput { Name = "A2", EnemyDamage = 5 } }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        Check(noFriendlyFire.Entries.All(entry => entry.Rating != ImpactRating.FriendlyFire),
            "a squad that took no friendly fire has no friendly-fire hazard");
        // A player whose only mark on the run is a penalty has no score to show, so the zero-score sentence
        // keeps precedence: the penalty sentence is never invented on top of a displayed 0.0.
        var penaltyOnly = new ImpactInput { Name = "PenaltyOnly", FriendlyFireObserved = true, FriendlyFireDamage = 90 };
        var penaltyMate = new ImpactInput { Name = "PenaltyMate", EnemyDamage = 300 };
        var penaltyOnlyReport = ImpactScoring.Evaluate(new[] { penaltyOnly, penaltyMate }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        Check(penaltyOnlyReport.Entries.First(entry => entry.Name == "PenaltyOnly").Rating == ImpactRating.FriendlyFire,
            "a zero-score player with a unique penalty top earns that penalty title rather than bypassing the top rule");

        // ---- every kill class is its own sentence, and the three shares add back up ------------------
        var minorKing = new ImpactInput { Name = "MinorKing", Counters = new ImpactCounters { MinorKills = 4, SleepingMinorKills = 2 } };
        var bossKing = new ImpactInput { Name = "BossKing", Counters = new ImpactCounters { BossKills = 1 } };
        var tierReport = ImpactScoring.Evaluate(new[] { minorKing, bossKing }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        ImpactEntry minorEntry = tierReport.Entries.First(entry => entry.Name == "MinorKing");
        ImpactEntry bossEntry = tierReport.Entries.First(entry => entry.Name == "BossKing");
        Check(minorEntry.Rating == ImpactRating.MinorKills && minorEntry.RatingMetric == (int)ImpactMetric.Kills,
            "the unique minor-kill leader wears the minor sentence, sourced from the kill row");
        Check(bossEntry.Rating == ImpactRating.BossKills && bossEntry.RatingMetric == (int)ImpactMetric.Kills,
            "the unique boss-kill leader wears the boss sentence at the same time");
        Check(minorEntry.Rating != bossEntry.Rating, "the three kill classes do not share one merged sentence");
        ImpactCounters minorCounters = minorEntry.Counters;
        double minorValue = ImpactScoring.WeightedKillValue(minorCounters, KillTier.Minor);
        double eliteValue = ImpactScoring.WeightedKillValue(minorCounters, KillTier.Elite);
        double bossValue = ImpactScoring.WeightedKillValue(minorCounters, KillTier.Boss);
        Check(Math.Abs(minorValue + eliteValue + bossValue - minorCounters.WeightedKills) < 1e-9,
            "the three class values add back up to the kill row exactly, so no kill is scored twice");
        Check(Math.Abs(minorValue - 2.0) < 1e-9 && eliteValue == 0 && bossValue == 0,
            "the class value is the tier weight applied to the class total, and a sleeping kill is worth one kill here too");
        var totalDamageLeader = new ImpactInput { Name = "Boomer", EnemyDamage = 900, Counters = new ImpactCounters { MinorKills = 1 } };
        var tierAndDamage = ImpactScoring.Evaluate(new[] { totalDamageLeader, minorKing, bossKing }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        Check(tierAndDamage.Entries.First(entry => entry.Name == "Boomer").Rating == ImpactRating.Damage,
            "the damage sentence is independent of the three kill sentences");
        Check(tierAndDamage.Entries.First(entry => entry.Name == "MinorKing").Rating == ImpactRating.MinorKills
            && tierAndDamage.Entries.First(entry => entry.Name == "BossKing").Rating == ImpactRating.BossKills,
            "the three kill sentences survive a squad that also leads on damage");
        Check(tierAndDamage.Entries.Select(entry => entry.Rating).Distinct().Count() == 3, "three real leads are three different sentences");

        // The sentence walk is by what the figure actually carried for that player, so a player who leads
        // two rows wears the heavier one, and a sentence is never handed to somebody who did not earn it.
        var bothLeads = new ImpactInput { Name = "BothLeads", EnemyDamage = 500, Counters = new ImpactCounters { Revives = 3 } };
        var quiet = new ImpactInput { Name = "Quiet", EnemyDamage = 20 };
        var orderedReport = ImpactScoring.Evaluate(new[] { bothLeads, quiet }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        ImpactEntry bothLeadsEntry = orderedReport.Entries.First(entry => entry.Name == "BothLeads");
        Check(bothLeadsEntry.Rating == ImpactRating.Damage && bothLeadsEntry.RatingMetric == (int)ImpactMetric.TotalDamage,
            "the heavier of two real leads is the sentence the player wears");
        Check(orderedReport.Entries.First(entry => entry.Name == "Quiet").Rating == ImpactRating.TeamPillar,
            "the revive sentence is not passed to a player who never revived anybody");
        Check(orderedReport.Entries.Where(entry => entry.Rating != ImpactRating.TeamPillar).Select(entry => entry.Rating).Distinct().Count()
            == orderedReport.Entries.Count(entry => entry.Rating != ImpactRating.TeamPillar),
            "no two players ever wear the same earned sentence");
        var fullSquad = new System.Collections.Generic.List<ImpactInput>();
        for (int index = 0; index < 4; index++)
        {
            var member = new ImpactInput { Name = "S" + index, EnemyDamage = 800 - index * 150, ShotsFired = 40, Hits = 36 - index * 6, WeakspotHits = 12 - index * 2 };
            member.Counters.CreditRevive(); member.Counters.CreditRevive(); member.Counters.CreditKill(KillTier.Minor);
            fullSquad.Add(member);
        }
        var fullReport = ImpactScoring.Evaluate(fullSquad, ImpactScoring.DefaultWeights, 1.0, 1.0);
        var earnedSentences = fullReport.Entries.Where(entry => entry.Rating != ImpactRating.TeamPillar && entry.Rating != ImpactRating.ZeroScore)
            .Select(entry => entry.Rating).ToArray();
        Check(earnedSentences.Length == earnedSentences.Distinct().Count(),
            "an earned sentence is never worn by two players at once");
        Check(fullReport.Entries.All(entry => entry.RatingMetric < 0 || entry.RatingMetric < ImpactScoring.Order.Length),
            "every sentence names a metric the board actually has, or none at all");

        // ---- the sentences themselves are configurable ------------------------------------------------
        CombatStrings.ResetTitleOverrides();
        string shippedDamage = CombatStrings.Title(ImpactRating.Damage);
        string shippedDamageLine = CombatStrings.TitleDescription(ImpactRating.Damage);
        Check(shippedDamage == "火力核心" && !string.IsNullOrEmpty(shippedDamageLine), "the shipped damage sentence and its line are defined");
        Check(CombatStrings.DefaultTitle(ImpactRating.Damage) == shippedDamage
            && ScoringSettings.Defaults().Titles["Damage"].Title == shippedDamage,
            "the shipped wording, the scoring file's default entry and the display layer are the same text");
        Check(ScoringSettings.Defaults().Titles["Damage"].Description == CombatStrings.DefaultTitleDescription(ImpactRating.Damage),
            "the default description is the display layer's own line too");
        Check(ScoringSettings.Defaults().Titles.Count == ScoringSettings.TitleNames.Length
            && ScoringSettings.TitleNames.All(name => ScoringSettings.Defaults().Titles.ContainsKey(name)),
            "every settlement sentence has a configurable entry");
        Check(ScoringSettings.Defaults().Titles["MinorKills"].Title != ScoringSettings.Defaults().Titles["EliteKills"].Title
            && ScoringSettings.Defaults().Titles["EliteKills"].Title != ScoringSettings.Defaults().Titles["BossKills"].Title,
            "the three kill classes ship with three different sentences");
        string customJson = ScoringSettings.Defaults().ToJson()
            .Replace("\"Title\": \"火力核心\"", "\"Title\": \"重炮手\"")
            .Replace("\"Title\": \"清道夫\"", "\"Title\": \"清扫专家\"");
        ScoringSettings customTitles = ScoringSettings.Parse(customJson);
        Check(customTitles.Titles["Damage"].Title == "重炮手" && customTitles.Titles["MinorKills"].Title == "清扫专家",
            "a custom sentence is read back out of the file");
        Check(customTitles.Titles["Damage"].Description == CombatStrings.DefaultTitleDescription(ImpactRating.Damage),
            "the description the file left alone stays as shipped");
        Check(customTitles.Fingerprint() != ScoringSettings.Defaults().Fingerprint(), "a renamed sentence changes the scoring fingerprint");
        customTitles.ApplyTitles();
        Check(CombatStrings.Title(ImpactRating.Damage) == "重炮手" && CombatStrings.Title(ImpactRating.MinorKills) == "清扫专家",
            "the configured sentence is the one the board prints");
        Check(CombatStrings.Title(ImpactRating.Stagger) == "寸止大师", "a sentence the file left alone is untouched");
        Check(CombatStrings.TitleDescription(ImpactRating.Damage) == shippedDamageLine, "its explanation line is still the shipped one");
        CombatStrings.Language = "en";
        Check(CombatStrings.Title(ImpactRating.Damage) == "重炮手" && CombatStrings.Title(ImpactRating.Stagger) == "Interrupter",
            "a configured sentence stays configured, and an untouched one still follows the language");
        CombatStrings.Language = previousLanguage;
        customTitles.Titles["Damage"].Title = "爆破手";
        customTitles.ApplyTitles();
        Check(CombatStrings.Title(ImpactRating.Damage) == "爆破手", "re-applying a scoring file replaces the previous wording");
        CombatStrings.ResetTitleOverrides();
        Check(CombatStrings.Title(ImpactRating.Damage) == shippedDamage, "clearing the overrides restores the shipped sentence");

        // A file that names an unknown sentence, repeats one, or carries unusable text is refused whole,
        // which is what leaves the last good settings - and their sentences - in place.
        string unknownKey = ScoringSettings.Defaults().ToJson().Replace("\"Damage\": {", "\"Nope\": {");
        string emptyTitle = ScoringSettings.Defaults().ToJson().Replace("\"Title\": \"火力核心\"", "\"Title\": \"   \"");
        string twoLineTitle = ScoringSettings.Defaults().ToJson().Replace("\"Title\": \"火力核心\"", "\"Title\": \"火\\n力\"");
        string repeatedKey = ScoringSettings.Defaults().ToJson().Replace("\"Titles\": {", "\"Titles\": {\n    \"damage\": {\n      \"Title\": \"重复\",\n      \"Description\": \"重复\"\n    },");
        string tooLongTitle = ScoringSettings.Defaults().ToJson().Replace("\"Title\": \"火力核心\"", "\"Title\": \"" + new string('火', ScoringSettings.MaxTitleLength + 1) + "\"");
        foreach (var broken in new[] { unknownKey, emptyTitle, twoLineTitle, repeatedKey, tooLongTitle })
        {
            bool rejected = false;
            try { ScoringSettings.Parse(broken); } catch (FormatException) { rejected = true; }
            Check(rejected, "an unusable sentence entry rejects the whole file so the last good settings stand");
        }
        Check(ScoringSettings.Parse("{}").Titles.Count == ScoringSettings.TitleNames.Length,
            "a sparse file fills every sentence from the shipped wording");
        string sparseTitles = "{\"Titles\": {\"Damage\": {\"Title\": \"重炮手\", \"Description\": \"全队伤害最高。\"}}}";
        Check(ScoringSettings.Parse(sparseTitles).Titles["Damage"].Title == "重炮手"
            && ScoringSettings.Parse(sparseTitles).Titles["Stagger"].Title == "寸止大师",
            "a single custom sentence is honoured while the rest keep their shipped text");
        string lowerCaseKey = "{\"Titles\": {\"damage\": {\"Title\": \"重炮手\", \"Description\": \"全队伤害最高。\"}}}";
        Check(ScoringSettings.Parse(lowerCaseKey).Titles["Damage"].Title == "重炮手",
            "a sentence key is matched without case and stored under its canonical spelling");

        // ---- the scoring file on disk still round trips with the new fields ---------------------------
        CombatStrings.ResetTitleOverrides();
        ImpactScoring.ResetThresholds();
        string revisionDir = Path.Combine(Path.GetTempPath(), "gtfocp-revision-" + Guid.NewGuid().ToString("N"));
        string revisionPath = ScoringFiles.Resolve(revisionDir);
        ScoringSettings created = ScoringFiles.Load(revisionPath, out bool wasCreated, out string createError);
        Check(wasCreated && createError == null && created != null, "a fresh install still writes one default scoring file");
        Check(File.ReadAllText(revisionPath).Contains("\"SleepingKillMultiplier\": 2"), "the created file carries the sleeping multiplier");
        Check(File.ReadAllText(revisionPath).Contains("\"LoneSurvivor\": 1.6"), "the created file carries the reweighted lone survivor");
        Check(File.ReadAllText(revisionPath).Contains("\"Titles\""), "the created file carries the configurable sentences");
        ScoringSettings reopenedRevision = ScoringFiles.Load(revisionPath, out bool createdAgain, out string reopenError);
        Check(!createdAgain && reopenError == null && Math.Abs(reopenedRevision.SleepingKillMultiplier - 2.0) < 1e-9
            && reopenedRevision.Titles["BossKills"].Title == "屠龙者",
            "the file this build creates is read back with its multiplier and sentences intact");
        Check(ScoringFiles.Matches(revisionPath, reopenedRevision), "the file this build writes is byte for byte the file it would write again");
        Directory.Delete(revisionDir, true);

        CombatStrings.ResetTitleOverrides();
        ImpactScoring.ResetThresholds();
        Check(Math.Abs(ImpactScoring.SleepingKillMultiplier - 2.0) < 1e-9, "the scenarios leave the scoring statics as they found them");
    }

    // The scoring file the previous release published, so the migration path is asserted against real text
    // rather than against a list the test wrote for itself.
    private static string PreviousDefaultJson()
        => "{\"Weights\":{\"TotalDamage\":0.95,\"Stagger\":1.4,\"Kills\":0.7,\"Revives\":0.28,\"WeakspotRate\":0.44,"
         + "\"Accuracy\":0.28,\"SupplyEfficiency\":0.78,\"ToolEfficiency\":0.8,\"LoneSurvivor\":0.47,\"Downs\":0.55,"
         + "\"FriendlyFire\":0.75,\"MeleeKills\":0.55,\"ScoutKills\":0.6}}";
}
