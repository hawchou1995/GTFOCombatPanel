using System;
using System.Linq;
using System.Text.Json;
using HawChow.GTFOCombatPanel;

internal static partial class Program
{
    private static void RunImpactFeatureScenarios()
    {
        var tools = new ToolImpactLedger();
        IntPtr mineA = new IntPtr(101), mineACore = new IntPtr(1001);
        IntPtr mineB = new IntPtr(102), mineBCore = new IntPtr(1002);
        IntPtr mineC = new IntPtr(103), mineCCore = new IntPtr(1003), turret = new IntPtr(201);
        Check(ToolImpactLedger.IsEquipmentMine(false, 77u, 77u)
            && !ToolImpactLedger.IsEquipmentMine(true, 77u, 77u)
            && !ToolImpactLedger.IsEquipmentMine(false, 77u, 88u)
            && !ToolImpactLedger.IsEquipmentMine(false, 0u, 0u),
            "equipment mine identity requires a non-consumable deployer and matching nonzero gear category");
        // A bot's tool slot exposes no first-person face, so the deployer's consumable flag is unknown
        // there. That shipped as a hard refusal in the field: six mines spawned on the host, every one
        // logged "tool slot is not MineDeployerFirstPerson", and deployment, trigger and damage all read
        // zero. Unknown must fall back to the gear category, which is what actually identifies the tool.
        // A mine matching the carried tool is attributed whether or not it was carried in; a mine the
        // carried tool cannot account for is still refused. The consumable flag no longer decides
        // attribution - it only decides whether the ledger counts a deployment, which is pinned below.
        Check(ToolImpactLedger.MineBelongsToSlot(77u, 77u),
            "a mine matching the carried tool is attributed, carried in or not");
        Check(!ToolImpactLedger.MineBelongsToSlot(77u, 88u),
            "a mine the carried tool cannot account for is still refused");
        Check(ToolImpactLedger.MineBelongsToSlot(0u, 88u) && !ToolImpactLedger.MineBelongsToSlot(77u, 0u),
            "the core's own category is the evidence; an unreadable slot is accepted, an unreadable core is not");
        Check(!ToolImpactLedger.MineBelongsToSlot(0u, 0u),
            "two unreadable zero categories are not evidence for anything");
        // A carried-in mine is attributed but never counted: its blast lands on the placer's row, its
        // deployment and trigger columns stay untouched.
        var pickup = new ToolImpactLedger();
        Check(!pickup.TryDeployMine(new IntPtr(9001), new IntPtr(9001), 7UL, true) && pickup.DeployedMineCount == 0,
            "a carried-in mine is never a deployment");
        Check(pickup.TryGetMineOwner(new IntPtr(9001), out ulong pickupOwner) && pickupOwner == 7UL,
            "a carried-in mine is still attributed to the player who placed it");
        Check(!pickup.TryTriggerMine(new IntPtr(9001), out _),
            "a carried-in mine never moves the trigger column");
        Check(!pickup.IsCountedMine(new IntPtr(9001)),
            "a carried-in mine is never a counted mine, so its blast cannot hang on the placer's gear row");
        Check(pickup.IsCountedMine(new IntPtr(9001)) == false && pickup.DeployedMineCount == 0,
            "neither the count nor the gear row moves for a carried-in mine");
        // A separate mine, so the consumed-item rule is proved on its own instead of leaving mineA
        // registered and then colliding with the equipment case below.
        IntPtr carriedMine = new IntPtr(105), carriedCore = new IntPtr(1005);
        Check(!tools.TryDeployMine(carriedMine, carriedCore, 11UL, true) && tools.DeployedMineCount == 0,
            "a consumable mine pickup never enters equipment deployment stats");
        Check(tools.TryGetMineOwner(carriedCore, out ulong carriedOwner) && carriedOwner == 11UL,
            "a consumable mine pickup still attributes its blast to the player who placed it");
        Check(tools.TryDeployMine(mineA, mineACore, 11UL, false)
            && !tools.TryDeployMine(mineA, mineACore, 11UL, false)
            && tools.DeployedMineCount == 1, "one spawned equipment mine counts once despite separate item/core identities");
        Check(tools.TryGetMineOwner(mineACore, out ulong mineOwner) && mineOwner == 11UL,
            "the detonation core pointer resolves to its equipment mine placer");
        Check(tools.TryRecoverMine(mineACore, out mineOwner) && mineOwner == 11UL
            && !tools.TryGetMineOwner(mineACore, out _) && tools.DeployedMineCount == 0,
            "pickup through either mine identity removes the same deployment once");
        IntPtr delayedMine = new IntPtr(104), delayedCore = new IntPtr(1004);
        Check(tools.TryDeployMine(delayedMine, 44UL, false)
            && !tools.TryDeployMine(delayedMine, delayedCore, 44UL, false)
            && tools.TryGetMineOwner(delayedCore, out ulong delayedOwner) && delayedOwner == 44UL
            && tools.DeployedMineCount == 1,
            "a later core identity is aliased to the already-counted item without double-counting placement");
        Check(tools.TryRecoverMine(delayedCore, out delayedOwner) && tools.DeployedMineCount == 0,
            "the late core alias also resolves recovery");
        Check(!tools.TryRecoverMine(new IntPtr(1999), out _),
            "an unrelated ground pickup cannot subtract an equipment deployment");
        Check(tools.TryDeployMine(mineB, mineBCore, 22UL, false)
            && tools.TryTriggerMine(mineBCore, out mineOwner) && mineOwner == 22UL
            && !tools.TryTriggerMine(mineB, out _) && tools.DeployedMineCount == 1,
            "item and core trigger callbacks deduplicate against one equipment mine");
        Check(!tools.TryRecoverMine(mineBCore, out _), "a triggered mine cannot be counted again as a pickup");
        Check(tools.TryDeployMine(mineC, mineCCore, 33UL, false)
            && tools.TryRecoverMine(mineC, out mineOwner) && mineOwner == 33UL
            && tools.DeployedMineCount == 1, "recovering a second mine preserves the still-deployed triggered mine");
        Check(tools.TryGetMineOwner(mineBCore, out mineOwner) && mineOwner == 22UL,
            "a triggered mine remains attributed until its object is destroyed");
        tools.ForgetMine(mineB);
        Check(!tools.TryGetMineOwner(mineBCore, out _) && tools.DeployedMineCount == 0,
            "destroying the item removes its detonation-core alias too");

        MineBlastAttribution mineBlast = MineBlastAttribution.Resolve(true, 11UL, 22UL);
        Check(mineBlast.OwnerLookup == 11UL && mineBlast.IsTrackedEquipmentMine && mineBlast.SuppressRecentAttackFallback,
            "the registered placer wins while equipment status remains separate");
        MineBlastAttribution placedByCarrier = MineBlastAttribution.Resolve(true, 11UL, 0UL);
        Check(placedByCarrier.OwnerLookup == 11UL && placedByCarrier.IsTrackedEquipmentMine,
            "a tracked mine with no core owner remains attributed to its carrier and deployer");
        MineBlastAttribution consumableBlast = MineBlastAttribution.Resolve(true, 0UL, 44UL);
        Check(consumableBlast.OwnerLookup == 44UL && !consumableBlast.IsTrackedEquipmentMine
            && consumableBlast.SuppressRecentAttackFallback,
            "an untracked consumable blast uses its actual core owner without tool-equipment credit or recent-attacker fallback");
        MineBlastAttribution ordinaryExplosion = MineBlastAttribution.Resolve(false, 0UL, 0UL);
        Check(ordinaryExplosion.OwnerLookup == 0UL && !ordinaryExplosion.SuppressRecentAttackFallback,
            "non-mine explosions retain their existing recent-attack attribution path");

        // A zero ID has meaning only inside the current firing window.
        tools.BeginSentryWindow(turret);
        Check(tools.TrySentryHit(turret, 0u) && !tools.TrySentryHit(turret, 0u)
            && tools.TrySentryHit(turret, 1u) && !tools.TrySentryHit(turret, 1u),
            "zero and real sentry ids both deduplicate repeated contacts within the same shot window");
        IntPtr projectile = new IntPtr(301), secondProjectile = new IntPtr(302), enemyA = new IntPtr(401), enemyB = new IntPtr(402);
        // A shot is registered when the glue gun fires, and a hit is only ever spent out of a registered shot.
        // The two registrations here stand for the two launches `ToolStatsTracker.CreditGlueProjectile` counts.
        tools.BeginGlueProjectile(11UL, projectile);
        tools.BeginGlueProjectile(11UL, secondProjectile);
        Check(tools.TryGlueHit(11UL, projectile) && !tools.TryGlueHit(11UL, projectile)
            && tools.TryGlueHit(11UL, secondProjectile), "one fired glue projectile contributes at most one shot-hit despite repeated enemy contacts");
        IntPtr unfiredProjectile = new IntPtr(303);
        Check(!tools.TryGlueHit(11UL, unfiredProjectile) && tools.GlueHitIdentityCount == 2,
            "a projectile this machine never counted as a shot cannot contribute a glue hit, so hits cannot exceed shots");
        Check(tools.TryGluedEnemy(11UL, enemyA) && !tools.TryGluedEnemy(11UL, enemyA)
            && tools.TryGluedEnemy(22UL, enemyA) && tools.TryGluedEnemy(11UL, enemyB),
            "glued enemies are unique per owning player and distinct enemy");
        IntPtr door = new IntPtr(501), doorProjectile = new IntPtr(502), doorAttacker = new IntPtr(601);
        Check(tools.TryRegisterGlueDoor(door, doorProjectile, 11UL)
            && !tools.TryRegisterGlueDoor(door, doorProjectile, 11UL)
            && tools.TryGetGlueDoorOwner(door, out ulong doorOwner) && doorOwner == 11UL,
            "a foamed door retains the applicator once per projectile");
        Check(tools.TryGlueDoorHit(door, doorAttacker, 77u, 10)
            && !tools.TryGlueDoorHit(door, doorAttacker, 77u, 10)
            && tools.TryGlueDoorHit(door, doorAttacker, 78u, 10),
            "distinct enemy attacks on a foamed door count even when the door survives, while a repeated search callback is deduplicated");
        // An enemy swinging at a foamed door is a melee exchange, not a shot landing: the glue gun fired nothing
        // for that swing, so the swing record grows while the glue-hit column, which can only spend a counted
        // shot, does not move. This is the 435-shot / 516-hit pair the archive showed.
        int glueHitsBeforeDoorSwings = tools.GlueHitIdentityCount;
        Check(tools.TryGlueDoorHit(door, doorAttacker, 79u, 10)
            && tools.GlueDoorHitIdentityCount == 3 && tools.GlueHitIdentityCount == glueHitsBeforeDoorSwings,
            "an enemy swinging at a foamed door grows the door-hit ledger without inventing a glue shot-hit");
        tools.ForgetGlueDoor(door);
        Check(!tools.TryGetGlueDoorOwner(door, out _) && tools.GluedDoorCount == 0 && tools.GlueDoorHitIdentityCount == 0,
            "clearing door foam removes its owner and hit identities");
        tools.Clear();
        Check(tools.DeployedMineCount == 0 && tools.SentryHitIdentityCount == 0
            && tools.GlueHitIdentityCount == 0 && tools.GluedEnemyCount == 0 && tools.GluedDoorCount == 0,
            "tool event identities clear at expedition reset");

        var friendlyFire = new FriendlyFireAttributionLedger();
        Check(!friendlyFire.ObserveDamage(7UL, 7UL, 1.0) && friendlyFire.Count == 0,
            "self-damage is not a friendly-fire attacker record");
        Check(friendlyFire.ObserveDamage(7UL, 8UL, 10.0)
            && friendlyFire.TryTake(8UL, 12.0, 3.0, out ulong attacker)
            && FriendlyFireRule.ShouldCreditDeath(attacker, 8UL, true, false),
            "a teammate hit followed by a live-to-down edge credits the attacker");
        Check(!friendlyFire.TryTake(8UL, 12.0, 3.0, out _), "one teammate down consumes its attribution once");
        friendlyFire.ObserveDamage(7UL, 8UL, 20.0);
        Check(!friendlyFire.TryTake(8UL, 24.0, 3.0, out _), "expired teammate damage is not credited to a later down");
        Check(!FriendlyFireRule.ShouldCreditDeath(7UL, 8UL, true, true)
            && !FriendlyFireRule.ShouldCreditDeath(8UL, 8UL, true, false),
            "a teammate who stayed alive or self-damage does not make a friendly-fire down");

        var metadata = ExpeditionMetadata.Create(" Survival ", "FX6", "Circle");
        Check(metadata.Rundown == "Survival" && metadata.LevelName == "FX6 Circle",
            "rundown key and expedition prefix/name are serialized as separate readable values");
        Check(ExpeditionMetadata.ComposeLevelName("FX6", "FX6 Circle") == "FX6 Circle"
            && ExpeditionMetadata.ComposeLevelName("", "Circle") == "Circle",
            "level name composition does not duplicate an existing prefix or invent a blank one");

        Check(ImpactScoring.DefaultWeights.Length == 13 && ImpactScoring.DefaultWeights.Length == ImpactScoring.Order.Length
            && Math.Abs(ImpactScoring.DefaultWeights.Sum() - 9.68) < 1e-9,
            "thirteen default weights preserve a normalized 0-100 composite scale");
        Check(ImpactScoring.TitleFor(ImpactMetric.FriendlyFire, new ImpactCounters()) == ImpactRating.FriendlyFire
            && CombatStrings.Title(ImpactRating.FriendlyFire).Length == 4,
            "friendly-fire leader title is the four-character 友伤克星");
        Check(CombatStrings.Title(ImpactRating.MeleeKills).Length == 4 && CombatStrings.Title(ImpactRating.ScoutKills).Length == 4,
            "the new melee and Scout awards use four-character Chinese titles");
        var classified = new ImpactCounters();
        KillClassifier.Credit(classified, KillTier.Minor, DamageKind.Melee, false);
        KillClassifier.Credit(classified, KillTier.Boss, DamageKind.Push, true);
        Check(classified.MinorKills == 1 && classified.BossKills == 1 && classified.MeleeKills == 1 && classified.ScoutKills == 1,
            "melee cause and Scout identity credit independently; a push is not a melee kill");
        // A kill whose cause is unknown must never become a melee kill. This is the shape the field
        // reported: 55 melee kills against zero melee damage, because the cause was taken from whatever
        // had touched the enemy last rather than from the blow that ended it.
        var unknownCause = new ImpactCounters();
        KillClassifier.Credit(unknownCause, KillTier.Minor, DamageKind.Unknown, false);
        Check(unknownCause.MinorKills == 1 && unknownCause.MeleeKills == 0,
            "a kill of unknown cause still counts as a kill but is never credited as a melee kill");
        Check(ImpactScoring.RawValue(new ImpactInput { Counters = classified }, ImpactMetric.MeleeKills) == 1
            && ImpactScoring.RawValue(new ImpactInput { Counters = classified }, ImpactMetric.ScoutKills) == 1,
            "the two new score rows read their own cumulative counters");
        var meleeDamage = new PlayerCombatStats(); meleeDamage.AddEnemyDamage(123.4);
        var meleeInput = new ImpactInput { Name = "Melee", Melee = meleeDamage, Counters = new ImpactCounters { MeleeKills = 3 } };
        var meleeWeights = new double[ImpactScoring.Order.Length]; meleeWeights[(int)ImpactMetric.MeleeKills] = 1;
        ImpactReport meleeReport = ImpactScoring.Evaluate(new[] { meleeInput, new ImpactInput { Name = "Other" } }, meleeWeights, 1, 1);
        int meleeLeader = meleeReport.Entries.FindIndex(entry => entry.Name == "Melee");
        Check(meleeReport.Entries[meleeLeader].Score == 100 && meleeReport.Entries[meleeLeader].Rating == ImpactRating.MeleeKills
            && meleeReport.IsGold(meleeLeader, ImpactMetric.MeleeKills),
            "melee-kill normalization remains inside the 100-point composite and identifies its unique leader");
        Check(ImpactBoard.MetricFields(meleeReport.Entries[meleeLeader], ImpactMetric.MeleeKills, KillTier.Minor, false, true)[0] == "3",
            "the settlement melee row is three columns: its kills, the part landed on a sleeping one, and the GearMelee damage");
        var scoutWeights = new double[ImpactScoring.Order.Length]; scoutWeights[(int)ImpactMetric.ScoutKills] = 1;
        var scoutInputs = new[] { new ImpactInput { Name = "Scout", Counters = new ImpactCounters { ScoutKills = 2 } }, new ImpactInput { Name = "Other" } };
        ImpactReport scoutReport = ImpactScoring.Evaluate(scoutInputs, scoutWeights, 1, 1);
        int scoutLeader = scoutReport.Entries.FindIndex(entry => entry.Name == "Scout");
        Check(scoutReport.Entries[scoutLeader].Score == 100 && scoutReport.Entries[scoutLeader].Rating == ImpactRating.ScoutKills
            && ImpactBoard.MetricFields(scoutReport.Entries[scoutLeader], ImpactMetric.ScoutKills, KillTier.Minor, false, true)[0] == "2",
            "Scout kills normalize as a separate count and receive their own settlement row");
        var unobservedKillInput = new ImpactInput { Name = "Unobserved", Counters = new ImpactCounters() };
        var unobservedKillEntry = new ImpactEntry(unobservedKillInput, null, null, 0, 1, ImpactRating.ZeroScore, -1);
        Check(ImpactBoard.MetricFields(unobservedKillEntry, ImpactMetric.MeleeKills, KillTier.Minor, false, true)[0] == "—"
            && ImpactBoard.MetricFields(unobservedKillEntry, ImpactMetric.ScoutKills, KillTier.Minor, false, true)[0] == "—",
            "a guest with no authoritative kill snapshot sees dashes rather than fabricated zeroes");

        var sentryCounters = new ImpactCounters { ScanUses = 4, ScanFirstMarks = 12, SentryShots = 10, SentryHits = 7 };
        sentryCounters.AddSentryDamage(45);
        var sentryEntry = new ImpactEntry(new ImpactInput { ToolName = "Sentry Gun", Counters = sentryCounters }, null, null, 50, 1, ImpactRating.Scan, (int)ImpactMetric.ToolEfficiency);
        string sentryCell = ImpactBoard.ToolEfficiencyText(sentryEntry);
        Check(!sentryCell.Contains("扫描") && !sentryCell.Contains("炮台")
            && sentryCell == "射击 10 / 命中 7 / 伤害 45.0",
            "sentry details show actions and values without repeating the gear name or scanner wording");
        var mineCounters = new ImpactCounters { ScanUses = 3, ScanFirstMarks = 9, MineDeployments = 5, MinePickups = 2, MineTriggers = 3 };
        mineCounters.AddMineDamage(28);
        var mineEntry = new ImpactEntry(new ImpactInput { ToolName = "Mine deployer", Counters = mineCounters }, null, null, 50, 1, ImpactRating.Scan, (int)ImpactMetric.ToolEfficiency);
        string mineCell = ImpactBoard.ToolEfficiencyText(mineEntry);
        Check(!mineCell.Contains("扫描") && !mineCell.Contains("绊雷")
            && mineCell == "部署 3 / 触发 3 / 伤害 28.0",
            "mine details show actions and values without repeating the gear name");
        var glueCounters = new ImpactCounters { ScanUses = 2, ScanFirstMarks = 4, GlueShots = 12, GlueHits = 8, GluedEnemies = 5 };
        var glueEntry = new ImpactEntry(new ImpactInput { ToolName = "Glue Gun", Counters = glueCounters }, null, null, 50, 1, ImpactRating.Scan, (int)ImpactMetric.ToolEfficiency);
        string glueCell = ImpactBoard.ToolEfficiencyText(glueEntry);
        Check(!glueCell.Contains("扫描") && !glueCell.Contains("胶枪")
            && glueCell == "射击 12 / 命中 8 / 胶住 5",
            "glue details show actions and values without repeating the gear name");
        Check(ImpactScoring.RawValue(glueEntry.Input, ImpactMetric.ToolEfficiency) == 4,
            "tool-use details do not replace scan first marks as the ToolEfficiency score source");

        var toolProjectionOwner = new CombatPlayer(0x773UL);
        WeaponCombatStats toolProjectionWeapon = toolProjectionOwner.Weapon(2, 900u, "Bio Tracker", 0, true);
        ImpactInput toolProjectionInput = ImpactInput.From("ToolOwner", null,
            new ImpactCounters { ScanUses = 2, ScanFirstMarks = 7, ScanMarks = 12 }, false, tool: toolProjectionWeapon);
        var toolProjectionEntry = new ImpactEntry(toolProjectionInput, null, null, 50, 1,
            ImpactRating.Scan, (int)ImpactMetric.ToolEfficiency);
        Check(toolProjectionInput.ToolName == "Bio Tracker"
            && ImpactBoard.ToolEfficiencyText(toolProjectionEntry) == "扫描 2 次 / 首次 7 只 / 累计 12 只",
            "the equipped GearClass name carries the detailed scan counts into settlement");
        var scannerCounters = new ImpactCounters { ScanUses = 5, ScanFirstMarks = 10, ScanMarks = 50 };
        var scannerEntry = new ImpactEntry(new ImpactInput { ToolName = "Bio Tracker", Counters = scannerCounters }, null, null, 50, 1, ImpactRating.Scan, (int)ImpactMetric.ToolEfficiency);
        Check(ImpactBoard.ToolEfficiencyText(scannerEntry) == "扫描 5 次 / 首次 10 只 / 累计 50 只"
            && ImpactBoard.IsScannerTool("生物追踪仪") && !ImpactBoard.ToolEfficiencyText(scannerEntry).Contains("，"),
            "scanner details show first and cumulative marks without punctuation unlike other tool cells");
        string previousLanguage = CombatStrings.Language;
        CombatStrings.Language = "en";
        Check(ImpactBoard.ToolEfficiencyText(scannerEntry) == "scan 5x / first 10 / cumulative 50",
            "the English scanner cell keeps the same field order behind the same separator");
        CombatStrings.Language = previousLanguage;
        var ffInput = new ImpactInput { Name = "FF", FriendlyFireObserved = true, FriendlyFireDamage = 100, Counters = new ImpactCounters { FriendlyFireDeaths = 1 } };
        Check(Math.Abs(ImpactScoring.FriendlyFireSeverity(ffInput, 200, 2) - 0.5) < 1e-9,
            "friendly-fire damage and down counts normalize separately then weigh equally");
        Check(Math.Abs(ImpactScoring.FriendlyFireSeverity(ffInput, 200, 0) - 0.5) < 1e-9,
            "a squad with no friendly-fire down component still scores the observed damage component");
        const string legacyDefaultJson = "{\"Weights\":{\"TotalDamage\":1.2,\"Stagger\":1.8,\"Kills\":0.9,\"Revives\":0.35,\"WeakspotRate\":0.55,\"Accuracy\":0.35,\"SupplyEfficiency\":1.0,\"ScanEfficiency\":1.1,\"LoneSurvivor\":0.6,\"Downs\":0.7}}";
        ScoringSettings migratedDefault = ScoringSettings.Parse(legacyDefaultJson);
        Check(migratedDefault.Weights.Count == 13 && migratedDefault.Weights["ToolEfficiency"] == 0.8
            && migratedDefault.Weights["FriendlyFire"] == 0.75 && migratedDefault.Weights["MeleeKills"] == 0.55
            && migratedDefault.Weights["ScoutKills"] == 0.6,
            "an exact published ten-metric JSON default migrates to the current thirteen weights");
        ScoringSettings migratedCustom = ScoringSettings.Parse(legacyDefaultJson.Replace("\"Stagger\":1.8", "\"Stagger\":5.0"));
        Check(migratedCustom.Weights["Stagger"] == 5.0 && migratedCustom.Weights["ToolEfficiency"] == 1.1
            && migratedCustom.Weights["FriendlyFire"] == 0.75 && migratedCustom.Weights["MeleeKills"] == 0.55
            && migratedCustom.Weights["ScoutKills"] == 0.6,
            "a hand-edited legacy weight survives while the renamed field and two suffix metrics are added");
        var toolSyncSource = new CombatPlayer(0x771UL);
        toolSyncSource.Impact.FriendlyFireDeaths = 2;
        toolSyncSource.Impact.ScanUses = 5; toolSyncSource.Impact.ScanFirstMarks = 10; toolSyncSource.Impact.ScanMarks = 50;
        toolSyncSource.Impact.MeleeKills = 4; toolSyncSource.Impact.ScoutKills = 3;
        toolSyncSource.Impact.MeleeKillsObserved = true; toolSyncSource.Impact.ScoutKillsObserved = true;
        toolSyncSource.Impact.SentryShots = 10; toolSyncSource.Impact.SentryHits = 7; toolSyncSource.Impact.SentryDamage = 31.5;
        toolSyncSource.Impact.MineDeployments = 5; toolSyncSource.Impact.MinePickups = 2; toolSyncSource.Impact.MineTriggers = 3; toolSyncSource.Impact.MineDamage = 27.5;
        toolSyncSource.Impact.GlueShots = 12; toolSyncSource.Impact.GlueHits = 8; toolSyncSource.Impact.GluedEnemies = 4;
        WeaponCombatStats syncMelee = toolSyncSource.Weapon(3, 444u, "Hammer");
        syncMelee.Counters.AddEnemyDamage(31.5);
        SyncSnapshot toolSnapshot = SyncProtocol.Capture(toolSyncSource, 0, 1, 2, 3, 0, false);
        Check(toolSnapshot.FriendlyFireDeaths == 2 && toolSnapshot.KillMelee == 4 && toolSnapshot.KillScout == 3
            && toolSnapshot.Has(SyncFlags.MeleeKillsObserved) && toolSnapshot.Has(SyncFlags.ScoutKillsObserved)
            && toolSnapshot.ScanUses == 5 && toolSnapshot.ScanFirstMarks == 10 && toolSnapshot.ScanMarks == 50
            && toolSnapshot.SentryShots == 10 && toolSnapshot.SentryHits == 7
            && toolSnapshot.MineDeployments == 5 && toolSnapshot.MinePickups == 2 && toolSnapshot.MineTriggers == 3
            && toolSnapshot.GlueShots == 12 && toolSnapshot.GlueHits == 8 && toolSnapshot.GluedEnemies == 4,
            "observed scan totals, kill counters and tool counts enter the squad snapshot");
        Check(toolSnapshot.Has(SyncFlags.MeleePresent) && toolSnapshot.Has(SyncFlags.MeleeDamageObserved)
            && toolSnapshot.GetMeleeName() == "Hammer" && Math.Abs(toolSnapshot.MeleeEnemyDamage - 31.5f) < 0.01f,
            "the melee name and observed GearMelee damage travel together");
        byte[] toolWire = SyncProtocol.Encode(toolSnapshot);
        Check(SyncProtocol.Decode(toolWire, 0, toolWire.Length, out SyncSnapshot decodedTools)
            && decodedTools.KillMelee == 4 && decodedTools.KillScout == 3 && decodedTools.ScanMarks == 50
            && Math.Abs(decodedTools.SentryDamage - 31.5f) < 0.01f && Math.Abs(decodedTools.MeleeEnemyDamage - 31.5f) < 0.01f
            && Math.Abs(decodedTools.MineDamage - 27.5f) < 0.01f,
            "kill counters, scan totals and melee damage survive the snapshot wire");
        var toolRestored = new CombatPlayer(0x771UL);
        SyncProtocol.Apply(toolRestored, decodedTools, 0, 1, 2, 3);
        Check(toolRestored.Impact.FriendlyFireDeaths == 2 && toolRestored.Impact.MeleeKills == 4
            && toolRestored.Impact.ScoutKills == 3 && toolRestored.Impact.MeleeKillsObserved && toolRestored.Impact.ScoutKillsObserved
            && toolRestored.Impact.SentryShots == 10 && toolRestored.Impact.MineDeployments == 5
            && toolRestored.Impact.GlueHits == 8 && toolRestored.Impact.ScanMarks == 50
            && Math.Abs(toolRestored.FindWeapon(3, 444u).Counters.EnemyDamage - 31.5) < 0.01,
            "applying a snapshot restores observed scan totals, kill counts and melee damage");
        var emptyToolPeer = new SyncSnapshot { Owner = 0x771UL };
        var mergedTools = SyncProtocol.Combine(toolSnapshot, true, emptyToolPeer, false);
        Check(mergedTools.KillMelee == 4 && mergedTools.KillScout == 3 && mergedTools.SentryShots == 10
            && mergedTools.MineTriggers == 3 && mergedTools.GluedEnemies == 4 && mergedTools.ScanMarks == 50
            && Math.Abs(mergedTools.MeleeEnemyDamage - 31.5f) < 0.01f,
            "an empty peer snapshot cannot erase host kill, scan, melee or tool counters");
        // The glue triple moves as one block on the wire, exactly as the kill tiers do: a machine that fired
        // nothing reports three zeros and may not erase a block that was counted, and a machine that counted
        // anything supplies all three figures at once - so shots from one peer are never paired with hits from
        // another, and "hits never exceed shots" survives a merge.
        var shotOnlyPeer = new SyncSnapshot { Owner = 0x771UL, GlueShots = 7 };
        SyncSnapshot glueTakenWhole = SyncProtocol.Combine(toolSnapshot, true, shotOnlyPeer, false);
        Check(glueTakenWhole.GlueShots == 7 && glueTakenWhole.GlueHits == 0 && glueTakenWhole.GluedEnemies == 0,
            "a peer that counted glue supplies the whole triple, so one machine's shots are never printed with another machine's hits");
        Check(mergedTools.GlueShots == 12 && mergedTools.GlueHits == 8 && mergedTools.GluedEnemies == 4,
            "a peer that counted no glue cannot erase the glue block that was counted");
        // The test is the shot count, and it is the same test in all three places the triple is taken from: a
        // side whose shots are zero has no usable glue sample at all, so a triple that left hits behind without a
        // counted launch - an enemy swinging at a glued door - may neither displace nor be spliced into a block
        // that was measured. That is the shape that printed "shot 435 / hit 516" in the archive.
        var hitOnlyPeer = new SyncSnapshot { Owner = 0x771UL, GlueHits = 6, GluedEnemies = 2 };
        SyncSnapshot glueKeptFromShots = SyncProtocol.Combine(toolSnapshot, true, hitOnlyPeer, false);
        Check(glueKeptFromShots.GlueShots == 12 && glueKeptFromShots.GlueHits == 8 && glueKeptFromShots.GluedEnemies == 4,
            "a peer whose shots are zero supplies no glue sample, so a hits-only triple cannot displace the block that was counted");
        var toolProjection = new CombatPlayer(0x772UL);
        CombatMerge.ApplyRemote(toolProjection, decodedTools, 0, 1, 2, 3, 0x772UL);
        Check(toolProjection.Impact.FriendlyFireDeaths == 2 && toolProjection.Impact.MeleeKills == 4
            && toolProjection.Impact.ScoutKills == 3 && toolProjection.Impact.MinePickups == 2
            && toolProjection.Impact.GlueShots == 12 && toolProjection.Impact.ScanMarks == 50
            && Math.Abs(toolProjection.FindWeapon(3, 444u).Counters.EnemyDamage - 31.5) < 0.01,
            "the client display projection receives scan totals, tool counters, kill counts and melee damage");

        // The display projection takes the triple from the same side the wire merge would, and never from a
        // snapshot about this machine's own row: that copy was built from what this machine broadcast, so it
        // cannot be more precise than the block it was made from.
        var glueDisplay = new CombatPlayer(0x772UL);
        glueDisplay.Impact.GlueShots = 3; glueDisplay.Impact.GlueHits = 1; glueDisplay.Impact.GluedEnemies = 1;
        CombatMerge.ApplyRemote(glueDisplay, new SyncSnapshot { Owner = 0x771UL, GlueShots = 12, GlueHits = 8, GluedEnemies = 4 },
            0, 1, 2, 3, 0x772UL);
        Check(glueDisplay.Impact.GlueShots == 12 && glueDisplay.Impact.GlueHits == 8 && glueDisplay.Impact.GluedEnemies == 4,
            "a remote triple that fired glue supplies all three figures at once, so one machine's shots are never printed with another machine's hits");
        var glueHitOnlyRow = new CombatPlayer(0x772UL);
        glueHitOnlyRow.Impact.GlueShots = 5; glueHitOnlyRow.Impact.GlueHits = 4; glueHitOnlyRow.Impact.GluedEnemies = 2;
        CombatMerge.ApplyRemote(glueHitOnlyRow, hitOnlyPeer, 0, 1, 2, 3, 0x772UL);
        Check(glueHitOnlyRow.Impact.GlueShots == 5 && glueHitOnlyRow.Impact.GlueHits == 4 && glueHitOnlyRow.Impact.GluedEnemies == 2,
            "a remote snapshot that fired no glue cannot rewrite the glue block this row measured");
        var glueOwnRow = new CombatPlayer(0x771UL);
        glueOwnRow.Impact.GlueShots = 2; glueOwnRow.Impact.GlueHits = 2; glueOwnRow.Impact.GluedEnemies = 1;
        CombatMerge.ApplyRemote(glueOwnRow, new SyncSnapshot { Owner = 0x771UL, GlueShots = 9, GlueHits = 7, GluedEnemies = 3 },
            0, 1, 2, 3, 0x771UL);
        Check(glueOwnRow.Impact.GlueShots == 2 && glueOwnRow.Impact.GlueHits == 2 && glueOwnRow.Impact.GluedEnemies == 1,
            "a snapshot about this machine's own row never overwrites the glue triple this machine measured itself");
        var reportInput = new ImpactInput { Name = "Runner", Counters = new ImpactCounters() };
        var report = ImpactScoring.Evaluate(new[] { reportInput }, ImpactScoring.DefaultWeights, 1, 1);
        var board = ImpactBoard.Build(report, true, true);
        using JsonDocument json = JsonDocument.Parse(ResultsLog.Build(true, new DateTime(2026, 10, 18), "1.17.18", board, report, metadata));
        Check(json.RootElement.GetProperty("rundown").GetString() == "Survival"
            && json.RootElement.GetProperty("levelName").GetString() == "FX6 Circle",
            "settlement JSON stores rundown and visible level name as separate fields");
    }
}
