using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using HawChow.GTFOCombatPanel;

internal static partial class Program
{
    private static int _assertions;
    private static void Main(string[] args)
    {
        if (args != null && args.Length == 2 && args[0] == "--ewc-replay-dll")
        {
            ReplayEwcDll(args[1]);
            return;
        }
        // Packaging helper: writes the scoring file this build would create on a fresh install, so the
        // archive's copy is byte-identical to the runtime default instead of a hand-maintained duplicate.
        // The normal test run passes no arguments and takes no notice of this.
        if (args != null && args.Length >= 2 && args[0] == "--emit-default-scoring")
        {
            Directory.CreateDirectory(args[1]);
            File.WriteAllText(Path.Combine(args[1], ScoringFiles.FileName), ScoringSettings.Defaults().ToJson());
            Console.WriteLine("DEFAULT-SCORING-WRITTEN " + Path.Combine(args[1], ScoringFiles.FileName));
            return;
        }
        SettlementHandoffScenarios();
        RunScoutSleepScenarios();
        RunEwcDamageScenarios();
        RunSquadTransportFramingScenarios();
        RunReviewFixScenarios();
        CaptureRevisionScenarios();
        RunScoringRevisionScenarios();
        RunSyncRevisionScenarios();
        var shot = new ShotSample();
        shot.Contact(true);
        Check(shot.Hits == 0, "contacts outside a firing window do not manufacture hits");
        shot.BeginPellet(); shot.Contact(false); shot.Contact(true); shot.Contact(true);
        Check(shot.Fired == 1 && shot.Hits == 1 && shot.WeakspotHits == 1, "penetration contacts count once and upgrade weakspot once");
        RunSquadGateScenarios();
        var shotgun = new ShotSample();
        for (int i = 0; i < 8; i++) { shotgun.BeginPellet(); if (i < 4) { shotgun.Contact(i < 2); shotgun.Contact(false); } }
        Check(shotgun.Fired == 8 && shotgun.Hits == 4 && shotgun.WeakspotHits == 2, "shotgun pellets, misses, penetration and weakspots are independently counted");
        var secondWeapon = new ShotSample(); secondWeapon.BeginPellet(); secondWeapon.Contact(false);
        Check(secondWeapon.Hits == 1, "a new weapon/firing window cannot inherit previous pellet dedup state");
        var player = new CombatPlayer(11);
        var primary = player.Weapon(1, 100, "Primary");
        var special = player.Weapon(2, 200, "Shotgun");
        player.RecordAccuracy(primary, shot.Fired, shot.Hits, shot.WeakspotHits);
        player.RecordAccuracy(special, shotgun.Fired, shotgun.Hits, shotgun.WeakspotHits);
        player.RecordDamage(primary, 30, false); player.RecordDamage(special, 50, false); player.RecordDamage(special, 7, true);
        Check(player.Total.ShotsFired == 9 && player.Total.Hits == 5 && player.Total.WeakspotHits == 3, "TOTAL accuracy equals the sum of weapon counts");
        Check(player.Total.EnemyDamage == 80 && player.Total.FriendlyFireDamage == 7, "TOTAL damage and friendly fire equal weapon totals");
        Check(primary.Counters.EnemyDamage == 30 && primary.Counters.FriendlyFireDamage == 0, "switching weapons does not contaminate previous weapon data");
        Check(special.Counters.EnemyDamage == 50 && special.Counters.FriendlyFireDamage == 7, "friendly fire is attached to the corresponding weapon");

        // Attribution follows the weapon that actually fired, not the one being held when the damage
        // record lands. Every bullet and melee record carries the firing weapon's gear category, which
        // is the only handle a modded weapon whose archetype id the game never exposes still has.
        var carrier = new CombatPlayer(77);
        var rifle = carrier.Weapon(1, 501, "Assault Rifle", 7001);
        var modded = carrier.Weapon(1, 0, "EWC Carbine", 7002);
        var sidearm = carrier.Weapon(2, 502, "HEL Revolver", 7003);
        Check(rifle.GearCategoryId == 7001 && sidearm.GearCategoryId == 7003, "a weapon remembers the gear category its damage records will carry");
        Check(!rifle.EwcManaged && modded.EwcManaged, "only a weapon with no archetype id of its own is matched by its category");
        Check(!sidearm.EwcManaged, "a vanilla weapon is not flagged as category-matched");
        Check(ReferenceEquals(carrier.FindWeaponByGearCategory(7001), rifle), "a gear category resolves to the one weapon that carries it");
        Check(carrier.FindWeaponByGearCategory(0) == null, "an absent gear category resolves to nothing rather than to a guess");
        Check(carrier.FindWeaponByGearCategory(9999) == null, "an unknown gear category resolves to nothing");
        Check(ReferenceEquals(CombatPlayer.MatchDamage(carrier, sidearm, 7001), rifle), "a hit is credited to the weapon whose category the record names, not to what is held now");
        Check(ReferenceEquals(CombatPlayer.MatchDamage(carrier, sidearm, 0), sidearm), "a record with no category falls back to the held weapon");
        Check(ReferenceEquals(CombatPlayer.MatchDamage(carrier, sidearm, 9999), sidearm), "a record naming an unknown category falls back to the held weapon");
        Check(ReferenceEquals(CombatPlayer.MatchDamage(carrier, null, 7002), modded), "a modded weapon is found by its category even when nothing is held to fall back on");
        Check(CombatPlayer.MatchDamage(null, sidearm, 7001) == sidearm, "no player means no category match");
        // Two weapons sharing one category are ambiguous, so the record falls back rather than guessing.
        var twin = carrier.Weapon(2, 503, "EWC Revolver", 7003);
        Check(carrier.FindWeaponByGearCategory(7003) == null, "two weapons sharing a category resolve to nothing");
        Check(ReferenceEquals(CombatPlayer.MatchDamage(carrier, sidearm, 7003), rifle) == false, "an ambiguous category never steals a hit for an unrelated weapon");
        Check(ReferenceEquals(CombatPlayer.MatchDamage(carrier, sidearm, 7003), sidearm), "an ambiguous category leaves the hit on the held weapon");
        carrier.RecordDamage(modded, 25, false);
        Check(modded.Counters.EnemyDamage == 25 && rifle.Counters.EnemyDamage == 0, "damage lands on the modded weapon's own row");
        carrier.RecordDamage(modded, 4, true);
        Check(modded.Counters.FriendlyFireDamage == 4 && rifle.Counters.FriendlyFireDamage == 0, "friendly fire lands on the modded weapon's own row");
        Check(carrier.Total.EnemyDamage == 25 && carrier.Total.FriendlyFireDamage == 4, "the player TOTAL still sums both weapons");
        // Damage and share share one column, one space apart and free of any slash.
        Check(CombatStatsFormatter.Value(player.Total, PanelColumn.DamageAndShare, 80) == "80.0 100.0%", "damage and share are one column, space separated");
        Check(CombatStatsFormatter.Value(primary.Counters, PanelColumn.DamageAndShare, 80) == "30.0 37.5%", "damage share excludes friendly fire");
        Check(CombatStatsFormatter.Value(special.Counters, PanelColumn.DamageAndShare, 80) == "50.0 62.5%", "individual contributions sum to total damage");
        Check(CombatStatsFormatter.Value(player.Total, PanelColumn.DamageAndShare, 0) == "80.0 —", "zero team total leaves the share placeholder but keeps damage");
        Check(CombatStatsFormatter.Value(player.Total, PanelColumn.DamageAndShare) == "80.0 —", "unknown team total is not fabricated");
        Check(CombatStatsFormatter.Value(new PlayerCombatStats(), PanelColumn.DamageAndShare, 80) == "— —", "damage nobody observed is a placeholder, never a fabricated zero");
        var observedZero = new PlayerCombatStats(); observedZero.AddEnemyDamage(0);
        Check(CombatStatsFormatter.Value(observedZero, PanelColumn.DamageAndShare, 80) == "0.0 0.0%", "an observed zero with known team damage is zero percent");
        Check(CombatStatsFormatter.Value(player.Total, PanelColumn.FriendlyFire) == "7.0", "friendly fire keeps its own column");
        Check(CombatStatsFormatter.Value(special.Counters, PanelColumn.Accuracy) == "50.0%", "accuracy denominator is fired pellets");
        Check(CombatStatsFormatter.Value(player.Total, PanelColumn.Accuracy) == "55.6%", "TOTAL accuracy uses total fired pellets");
        Check(CombatStatsFormatter.Value(special.Counters, PanelColumn.WeakspotAccuracy) == "50.0%", "weakspot rate denominator is hit pellets");
        // The three projectile counters share one column in total/hit/weakspot order.
        Check(CombatStatsFormatter.Value(special.Counters, PanelColumn.Projectiles) == "8 4 2", "three projectile counters are one space-separated column");
        Check(CombatStatsFormatter.Value(player.Total, PanelColumn.Projectiles) == "9 5 3", "merged projectile column sums both weapons");
        Check(CombatStatsFormatter.ProjectilesText(special.Counters).Split(" ").Length == 3, "projectile column exposes exactly three space-separated values");
        Check(!CombatStatsFormatter.Value(player.Total, PanelColumn.Projectiles).Contains("/") && !CombatStrings.Header(PanelColumn.Projectiles).Contains("/") && !CombatStatsFormatter.Summary(player.Total, 80, true).Contains("/"), "no slash survives anywhere in the panel or result text");
        var unknown = new PlayerCombatStats();
        Check(CombatStatsFormatter.Value(unknown, PanelColumn.Projectiles) == "—", "unobserved projectile data is not fabricated as zero");
        unknown.AddAccuracy(0, 0, 0);
        Check(CombatStatsFormatter.Value(unknown, PanelColumn.Projectiles) == "0 0 0", "observed no-shots state has real zero counts");
        Check(CombatStatsFormatter.Value(unknown, PanelColumn.Accuracy) == "—", "no fired pellets yields no NaN percentage");
        unknown.AddAccuracy(10, 3, 1, true);
        Check(CombatStatsFormatter.Value(unknown, PanelColumn.Accuracy) == "~30.0%", "network estimates are visibly marked");
        Check(CombatStatsFormatter.Value(unknown, PanelColumn.Projectiles) == "~10 3 1", "estimated counters carry a marker in the merged column");
        unknown.AddEnemyDamage(double.NaN); unknown.AddFriendlyFireDamage(double.PositiveInfinity);
        Check(unknown.EnemyDamage == 0 && unknown.FriendlyFireDamage == 0, "nonfinite damage is ignored");
        Check(CombatStatsFormatter.FormatDamage(1234.56) == "1,234.6", "damage format uses one decimal");
        Check(CombatStatsFormatter.FormatDamage(double.NaN) == "0.0", "NaN damage never appears in UI");
        Check(CombatStatsFormatter.FormatPercent(0, 0) == "—", "zero denominator never displays infinity");
        Check(CombatStatsFormatter.TruncatePlayerName("abcdefghi") == "abcdef", "Latin names limited to six elements");
        Check(CombatStatsFormatter.TruncatePlayerName("名字玩家测试完成") == "名字玩家测试", "CJK names limited to six characters");
        Check(CombatStatsFormatter.TruncatePlayerName("A😀BCDEF") == "A😀BCDE", "surrogate pairs preserved");
        Check(CombatStatsFormatter.TruncatePlayerName("a\nb\tc") == "abc", "names cannot create new table rows");
        Check(CombatStatsFormatter.TruncatePlayerName("") == "", "empty names are safe");
        Check(CombatStatsFormatter.TruncatePlayerName("<#fa0>\"<#0ff>A1pha<#f00>\"") == "\"A1pha", "the six element cut of a markup name is the visible text, not the literal tag");
        Check(CombatStatsFormatter.TruncatePlayerName("<color=red>Alpha</color>") == "Alpha", "paired colour tags are stripped");
        Check(CombatStatsFormatter.TruncatePlayerName("ab<br>cd") == "abcd", "a line break tag cannot split a table row");
        Check(CombatStatsFormatter.TruncatePlayerName("</color>") == "", "a name that is only markup leaves nothing to print");
        Check(CombatStatsFormatter.TruncatePlayerName("1 < 2") == "1 < 2", "a stray angle bracket stays literal");
        Check(CombatStatsFormatter.TruncatePlayerName("<unknown>abcdef") == "<unkno", "an unrecognised tag is left alone");

        // ---- squad sync: what the host sends, what a client ends up believing --------------------
        var host = new CombatPlayer(0xABCUL);
        host.Name = "Host"; host.DisplaySlot = 0; host.StatisticsAvailable = true; host.SupportRole = true;
        host.Total.AddAccuracy(100, 60, 20);
        host.Total.AddEnemyDamage(1234.5);
        host.Total.AddFriendlyFireDamage(9.5);
        host.Impact.MinorKills = 7; host.Impact.EliteKills = 3; host.Impact.BossKills = 1;
        host.Impact.Stagger = 4; host.Impact.Revives = 2; host.Impact.Downs = 1;
        host.Impact.SupplyUses = 3; host.Impact.SupplyEfficiencySum = 2.25;
        host.Impact.ScanUses = 2; host.Impact.ScanFirstMarks = 5; host.Impact.ScanMarks = 9;
        host.Weapon(0, 111u, "Burst Rifle", 500u).Counters.AddAccuracy(80, 50, 18);
        host.Weapon(0, 111u, "Burst Rifle", 500u).Counters.AddEnemyDamage(1000.5);
        host.Weapon(1, 222u, "Deagle", 600u).Counters.AddAccuracy(20, 10, 2);
        host.Weapon(2, 333u, "Bio Tracker", 0u);

        SyncSnapshot snap = SyncProtocol.Capture(host, 0, 1, 2, 3, 4321.0, true);
        Check(snap.Owner == 0xABCUL, "a snapshot names the player it describes");
        Check(snap.Has(SyncFlags.SquadDamageKnown) && Math.Abs(snap.SquadEnemyDamage - 4321f) < 0.01f, "the host's squad total travels with the snapshot");
        Check(snap.Has(SyncFlags.EnemyDamageObserved) && Math.Abs(snap.EnemyDamage - 1234.5f) < 0.01f, "enemy damage travels");
        Check(snap.Has(SyncFlags.FriendlyFireObserved) && Math.Abs(snap.FriendlyFire - 9.5f) < 0.01f, "friendly fire travels");
        Check(snap.Shots == 100 && snap.Hits == 60 && snap.WeakHits == 20, "the pellet counters travel");
        Check(snap.Has(SyncFlags.PrimaryPresent) && snap.PrimaryArchetypeId == 111u && snap.GetPrimaryName() == "Burst Rifle", "the primary row travels with its name and archetype");
        Check(snap.Has(SyncFlags.SpecialPresent) && snap.GetSpecialName() == "Deagle", "the secondary row travels");
        Check(snap.Has(SyncFlags.ClassPresent) && snap.GetClassName() == "Bio Tracker", "the tool row's name travels");
        Check(!snap.Has(SyncFlags.MeleePresent), "a slot the host never filled is not claimed");
        Check(snap.KillMinor == 7 && snap.KillElite == 3 && snap.KillBoss == 1 && snap.Stagger == 4 && snap.Revives == 2 && snap.Downs == 1, "the impact counters travel");
        Check(snap.SupplyUses == 3 && snap.ScanUses == 2 && snap.ScanFirstMarks == 5 && snap.ScanMarks == 9, "supply, first scan marks and cumulative scan marks travel");

        var mirror = new CombatPlayer(0xABCUL);
        SyncProtocol.Apply(mirror, snap, 0, 1, 2, 3);
        Check(mirror.Total.ShotsFired == 100 && mirror.Total.Hits == 60 && mirror.Total.WeakspotHits == 20, "applying a snapshot restores the pellet counters");
        Check(Math.Abs(mirror.Total.EnemyDamage - 1234.5) < 0.01 && Math.Abs(mirror.Total.FriendlyFireDamage - 9.5) < 0.01, "applying a snapshot restores both damage figures");
        Check(mirror.Total.AccuracyObserved && !mirror.Total.AccuracyEstimated, "applying a snapshot keeps the observed flag and drops the estimate mark");
        Check(mirror.StatisticsAvailable && mirror.SupportRole, "applying a snapshot restores the display flags");
        Check(mirror.Impact.Kills == 11 && mirror.Impact.Stagger == 4 && mirror.Impact.Revives == 2 && mirror.Impact.Downs == 1, "applying a snapshot restores the impact counters");
        Check(Math.Abs(mirror.Impact.SupplyEfficiency - 0.75) < 0.001, "the supply average survives the round trip");
        Check(mirror.FindWeapon(0, 111u)?.Name == "Burst Rifle", "the weapon rows land on the slot the host named");
        Check(mirror.FindWeapon(2, 333u)?.Name == "Bio Tracker", "the tool row lands too");
        SyncProtocol.Apply(mirror, snap, 0, 1, 2, 3);
        Check(mirror.Total.ShotsFired == 100 && Math.Abs(mirror.Total.EnemyDamage - 1234.5) < 0.01, "re-applying the same snapshot does not double count");

        Check(!SyncProtocol.Accepts(0xABCUL, snap), "a snapshot about the local player is refused, its own measurement wins");
        Check(SyncProtocol.Accepts(0xDEFUL, snap), "a snapshot about another player is accepted");
        Check(!SyncProtocol.Accepts(0xDEFUL, new SyncSnapshot()), "a snapshot that names nobody is refused");

        // A player nothing was observed for must leave a dash, not a fabricated zero.
        var silent = new CombatPlayer(0x111UL);
        SyncSnapshot sparse = SyncProtocol.Capture(silent, 0, 1, 2, 3, 0, false);
        Check(!sparse.Has(SyncFlags.SquadDamageKnown), "a client's own snapshot claims no squad total");
        Check(!sparse.Has(SyncFlags.EnemyDamageObserved) && !sparse.Has(SyncFlags.AccuracyObserved), "an unobserved player carries no observed flags");
        var stale = new CombatPlayer(0x111UL);
        stale.Total.AddAccuracy(50, 50, 50);
        stale.Total.AddEnemyDamage(999);
        SyncProtocol.Apply(stale, sparse, 0, 1, 2, 3);
        Check(!stale.Total.AccuracyObserved && !stale.Total.EnemyDamageObserved, "applying an unobserved snapshot clears what this machine thought it saw");
        Check(stale.Total.EnemyDamage == 0 && stale.Total.ShotsFired == 0, "an unobserved snapshot leaves no fabricated number behind");

        var words = new uint[SyncSnapshot.NameWords];
        SyncProtocol.PackName("削短型霰彈槍", words);
        Check(SyncProtocol.UnpackName(words) == "削短型霰彈槍", "a CJK weapon name survives the packing");
        SyncProtocol.PackName("ABCDEFGHIJKLMNOPQRSTUVWXYZ", words);
        Check(SyncProtocol.UnpackName(words) == "ABCDEFGHIJKLMNOPQRSTUVWX", "a name longer than the packet is cut at the byte limit");
        SyncProtocol.PackName(new string('a', 21) + "😀", words);
        Check(SyncProtocol.UnpackName(words) == new string('a', 21), "a code point that would straddle the limit is dropped whole, never half printed");
        SyncProtocol.PackName("", words);
        Check(SyncProtocol.UnpackName(words) == "", "an empty name packs to nothing");

        SyncSnapshot round = SyncProtocol.Capture(host, 0, 1, 2, 3, 4321.0, true);
        var landed = new CombatPlayer(0xABCUL);
        SyncProtocol.Apply(landed, round, 0, 1, 2, 3);
        SyncSnapshot echo = SyncProtocol.Capture(landed, 0, 1, 2, 3, 4321.0, true);
        Check(echo.Shots == round.Shots && echo.Hits == round.Hits && echo.EnemyDamage == round.EnemyDamage
            && echo.KillMinor == round.KillMinor && echo.SupplyUses == round.SupplyUses
            && echo.GetPrimaryName() == round.GetPrimaryName() && echo.GetClassName() == round.GetClassName(),
            "capture, apply and capture again reproduces the same snapshot");

        // The wire itself: a snapshot is copied byte for byte over the game's own byte channel, so what
        // one build writes is what the other reads back, and a packet too short to hold one is refused
        // rather than read as a half-filled row.
        byte[] wire = SyncProtocol.Encode(round);
        Check(SyncProtocol.SnapshotSize == 360 && wire.Length == 360, "the v8 snapshot carries the Scout damage beside every earlier figure, with native alignment");
        Check(SyncProtocol.Decode(wire, 0, wire.Length, out SyncSnapshot readBack), "a snapshot that was written is read back");
        Check(readBack.Owner == round.Owner && readBack.EnemyDamage == round.EnemyDamage && readBack.KillBoss == round.KillBoss
            && readBack.GetPrimaryName() == round.GetPrimaryName() && readBack.GetClassName() == round.GetClassName(),
            "every figure survives the wire, gear names included");
        Check(!SyncProtocol.Decode(wire, 0, 12, out _), "a packet too short to hold a snapshot is refused");
        Check(!SyncProtocol.Decode(wire, 8, wire.Length - 8, out _), "a packet that does not carry a whole snapshot is refused");
        Check(!SyncProtocol.Decode(null, 0, 0, out _), "a missing packet is refused rather than read as an empty snapshot");

        // The share a client prints when the host runs no mod of its own: it is built on the health the
        // client saw the enemies lose, so it goes out marked as an estimate and never as a measurement.
        var localRow = new CombatPlayer(0xDEFUL);
        localRow.Total.AddEnemyDamage(250);
        Check(CombatStatsFormatter.FormatShare(localRow.Total, 1000) == "25.0%", "a measured squad total prints an unmarked share");
        localRow.Total.SquadDamageEstimated = true;
        Check(CombatStatsFormatter.FormatShare(localRow.Total, 1000) == "~25.0%", "a share built on a health-loss estimate is marked as one");
        localRow.Total.SquadDamageEstimated = false;
        Check(CombatStatsFormatter.FormatShare(localRow.Total, 0) == "—", "an all-zero squad total still prints a dash");
        foreach (PanelColumn column in CombatStatsFormatter.Columns)
        {
            Check(CombatStatsFormatter.IsRightAligned(column), column + " is right aligned");
            Check(CombatStatsFormatter.TextRoleFor(column) == (column == PanelColumn.PlayerName ? PanelTextRole.PlayerName : PanelTextRole.Neutral), column + " color isolation");
            Check(!string.IsNullOrEmpty(CombatStrings.Header(column)), column + " has a Chinese header");
        }
        Check(CombatStatsFormatter.Columns.Length == 6, "table has name, damage/share, friendly fire, accuracy, weakspot and projectiles");
        Check(CombatStatsFormatter.Columns[0] == PanelColumn.PlayerName, "the first column is the player name");
        Check(CombatStatsFormatter.Columns[1] == PanelColumn.DamageAndShare, "the damage column carries the share");
        Check(CombatStatsFormatter.Columns[5] == PanelColumn.Projectiles, "the three pellet counters are one column");
        Check(CombatStatsFormatter.Summary(special.Counters).Contains("弹丸 8 4 2"), "weapon result block merges the three counts with spaces");
        Check(CombatStatsFormatter.Summary(player.Total, 80, true).Contains("伤害 80.0 占比 100.0%"), "result TOTAL block merges damage and share on one line");
        Check(CombatStatsFormatter.Summary(player.Total).Contains("友伤 7.0"), "friendly fire keeps its own result line");
        // Language selection: default Chinese, English on request, tolerant of spelling.
        Check(CombatStrings.Language == "zh", "Chinese is the default language");
        Check(CombatStrings.Normalize("EN") == "en", "language matching is case-insensitive");
        Check(CombatStrings.Normalize("English") == "en", "spelled-out English is accepted");
        Check(CombatStrings.Normalize("zh-CN") == "zh", "regional Chinese is accepted");
        Check(CombatStrings.Normalize("chinese") == "zh", "anything unrecognized falls back to Chinese");
        Check(CombatStrings.Normalize("") == "zh", "empty language falls back to Chinese");
        Check(CombatStrings.Header(PanelColumn.DamageAndShare) == "伤害 占比", "Chinese damage header shows the merged pair");
        Check(CombatStrings.Header(PanelColumn.Projectiles) == "总弹丸 命中 弱点", "Chinese pellet header shows the merged triple");
        CombatStrings.Language = "en";
        Check(CombatStrings.Language == "en", "language switches to English");
        Check(CombatStrings.Header(PanelColumn.PlayerName) == "Player", "English column headers are used");
        Check(CombatStrings.Header(PanelColumn.Projectiles) == "Pellets Hit Weak" && !CombatStrings.Header(PanelColumn.Projectiles).Contains("/"), "English pellet header shows the merged triple without a slash");
        foreach (PanelColumn column in CombatStatsFormatter.Columns) Check(!string.IsNullOrEmpty(CombatStrings.Header(column)), column + " has an English header");
        Check(CombatStrings.TotalHeader == "TOTAL" && CombatStrings.WeaponsHeader == "WEAPONS", "result headings follow the language");
        Check(CombatStatsFormatter.Summary(player.Total, 80, true).Contains("Damage 80.0 Share 100.0%"), "result block follows the language");
        Check(CombatStatsFormatter.Summary(player.Total).Contains("Pellets 9 5 3"), "English result block keeps the space-separated triple");
        CombatStrings.Language = "zh";
        Check(CombatStrings.Header(PanelColumn.PlayerName) == "玩家", "language switches back to Chinese");
        Check(CombatStrings.TotalHeader == "综合统计", "Chinese result heading is restored");
        // Column plan: the name is the row label and is always drawn, the rest is optional.
        Check(PanelColumns.Compose(true, true, true, true, true).Count == 6, "all optional columns produce a full table");
        Check(PanelColumns.Compose(true, true, true, true, true)[0] == PanelColumn.PlayerName, "the name column is always first");
        Check(PanelColumns.Compose(false, false, false, false, false).Count == 1, "disabling every data column still leaves the player name");
        var onlyFriendly = PanelColumns.Compose(false, true, false, false, false);
        Check(onlyFriendly.Count == 2 && onlyFriendly[1] == PanelColumn.FriendlyFire, "only the enabled data column is planned");
        Check(PanelColumns.Compose(true, false, true, false, true).Count == 4, "an arbitrary column subset is planned");
        Check(PanelColumns.Compose(true, false, true, false, true).SequenceEqual(new[] { PanelColumn.PlayerName, PanelColumn.DamageAndShare, PanelColumn.Accuracy, PanelColumn.Projectiles }), "planned columns keep display order");
        // Density: compact is the default and occupies strictly less area.
        Check(PanelDensity.Normalize(null) == "compact", "compact is the default density");
        Check(PanelDensity.Normalize("") == "compact", "an empty density falls back to compact");
        Check(PanelDensity.Normalize("garbage") == "compact", "an unknown density falls back to compact");
        Check(PanelDensity.Normalize("Normal") == "normal", "density names are matched case-insensitively");
        Check(PanelDensity.Normalize("loose") == "spacious", "spacious tolerates an alias");
        Check(PanelDensity.FromName(null).Name == "compact", "the default density struct is compact");
        // Width estimation: the layout still resolves when no font asset can be queried
        // (headless tests, very early startup). Digits are narrower than CJK, and width
        // follows the font size.
        Check(PanelGeometry.EstimateTextWidth(null, 20) == 0 && PanelGeometry.EstimateTextWidth("", 20) == 0, "no text has no width");
        Check(PanelGeometry.EstimateTextWidth("1234", 20) > PanelGeometry.EstimateTextWidth("12", 20), "width grows with the number of digits");
        Check(PanelGeometry.EstimateTextWidth("玩家", 20) > PanelGeometry.EstimateTextWidth("12", 20), "CJK glyphs are wider than digits");
        Check(near(PanelGeometry.EstimateTextWidth("abcd", 40), PanelGeometry.EstimateTextWidth("abcd", 20) * 2), "width is proportional to the font size");
        Check(PanelGeometry.MinimumWidth(PanelColumn.PlayerName, 20) > PanelGeometry.MinimumWidth(PanelColumn.FriendlyFire, 20), "the name column keeps a wider floor than friendly fire");
        Check(near(PanelGeometry.ColumnContentWidth(new[] { "0.0", "1,234.5" }, PanelColumn.FriendlyFire, 20), PanelGeometry.EstimateTextWidth("1,234.5", 20)), "a column is as wide as its widest cell");
        Check(PanelGeometry.ColumnContentWidth(new[] { "1" }, PanelColumn.PlayerName, 20) == PanelGeometry.MinimumWidth(PanelColumn.PlayerName, 20), "a short value never shrinks a column below its floor");
        // The grid is expressed in the local units of its host hierarchy and derived from the
        // glyph size, so it is scale free: no resolution and no screen size enter the arithmetic.
        float[] content = { 92f, 148f, 62f, 72f, 72f, 142f };
        float font = 30f;
        PanelGrid normal = PanelGeometry.Build(content, font, 5, PanelDensity.Normal);
        PanelGrid again = PanelGeometry.Build(content, font, 5, PanelDensity.Normal);
        Check(normal.Width == again.Width && normal.Height == again.Height && normal.FontPixels == again.FontPixels, "identical inputs produce identical geometry");
        Check(normal.Columns == 6 && normal.Rows == 5, "the grid keeps its column and row counts");
        Check(near(normal.FontPixels, font), "normal density keeps the glyph size it was given");
        Check(near(normal.Height, normal.RowHeight * 5), "table height is rows times the row height");
        // RowFactor is the design proportion; the minimum row em is the clipping guard that wins when the
        // density scales would put the row below the face's own line. Normal density now lands on the guard.
        Check(near(normal.RowHeight, font * Math.Max(PanelGeometry.RowFactor * PanelDensity.Normal.Row, PanelGeometry.MinimumRowEm)),
            "row height follows the glyph size, whichever of the design proportion and the clipping guard is taller");
        Check(near(normal.Gap, font * PanelGeometry.GapFactor), "column gap is a multiple of the glyph size");
        Check(near(normal.CellWidths[0], content[0] + 2 * font * PanelGeometry.PaddingFactor), "a cell is its content plus padding");
        Check(near(normal.Width, normal.CellWidths.Sum() + normal.Gap * 5), "table width is the cells plus the gaps");
        Check(near(normal.ColumnOffset(0), 0), "the first column starts at the left edge");
        Check(near(normal.ColumnOffset(1), normal.CellWidths[0] + normal.Gap), "columns advance by cell width plus gap");
        Check(near(normal.ColumnOffset(5) + normal.CellWidths[5], normal.Width), "the last column ends at the right edge");
        Check(PanelGeometry.Build(content, font, 1, PanelDensity.Normal).RowHeight == normal.RowHeight, "row height does not depend on how many players are present");
        Check(near(PanelGeometry.Build(content, font, 0, PanelDensity.Normal).Height, normal.Height / 5), "a header only table still has one row");
        // Every row of a column shares one box, which is exactly what makes a header line up
        // with the numbers under it; no two cells of the table may overlap.
        for (int column = 0; column < normal.Columns; column++)
        {
            Check(near(normal.Cell(0, column).X, normal.Cell(4, column).X), "every row of a column starts at the same x");
            Check(near(normal.Cell(0, column).Right, normal.Cell(4, column).Right), "every row of a column shares one right edge");
            Check(near(normal.Cell(2, column).X, normal.ColumnOffset(column)), "cells are measured from the table's top left");
            Check(near(normal.Cell(2, column).Y, 2 * normal.RowHeight), "cells are stacked one row height apart");
        }
        var tableCells = new List<LayoutRect>();
        for (int row = 0; row < normal.Rows; row++) for (int column = 0; column < normal.Columns; column++) tableCells.Add(normal.Cell(row, column));
        bool overlappingCells = false;
        for (int a = 0; a < tableCells.Count; a++) for (int b = a + 1; b < tableCells.Count; b++) if (tableCells[a].Overlaps(tableCells[b])) overlappingCells = true;
        Check(!overlappingCells, "no two cells of the table overlap");
        Check(tableCells.All(rect => rect.Y >= -0.001f && rect.Bottom <= normal.Height + 0.001f), "every cell stays inside the table's height");
        Check(tableCells.All(rect => rect.X >= -0.001f && rect.Right <= normal.Width + 0.001f), "every cell stays inside the table's width");
        PanelGrid compact = PanelGeometry.Build(content, font, 5, PanelDensity.Compact);
        // Both densities now sit on the same floor, so compact is still the tighter table through its
        // cell padding, its column gap and its glyph size - the row is the one thing it may not shrink
        // below the face's own line any more.
        Check(compact.RowHeight <= normal.RowHeight, "compact is never taller than normal");
        Check(compact.Width < normal.Width && compact.CellWidths[0] < normal.CellWidths[0], "compact tables are narrower than normal tables");
        Check(compact.Gap < normal.Gap, "compact columns are closer together");
        Check(near(compact.FontPixels, normal.FontPixels), "density reshapes the table; the glyph size stays what the caller measured with");
        PanelGrid spacious = PanelGeometry.Build(content, font, 5, PanelDensity.Spacious);
        Check(spacious.RowHeight > normal.RowHeight && spacious.Width > normal.Width, "spacious tables are larger than normal tables");
        // The table scales with the glyph size it is given, which is how one code path serves the
        // HUD and the results page even though their local units differ.
        float[] doubleContent = content.Select(width => width * 2).ToArray();
        Check(near(PanelGeometry.Build(doubleContent, font * 2, 5, PanelDensity.Normal).Width, normal.Width * 2), "the table scales with the glyph size it is given");
        // A table that outgrows its host is scaled down instead of being clipped. 1.8.2 narrowed the
        // table, so the overflow is stated against the table itself rather than against a frozen
        // pixel budget that the tighter spacing no longer exceeds.
        PanelGrid wide = PanelGeometry.Build(doubleContent, font * 2, 5, PanelDensity.Normal);
        float budget = wide.Width - 200f;
        float fit = wide.FitFactor(budget, 100000);
        Check(fit < 1f && near(wide.Width * fit, budget), "an oversized table is scaled to the width it was given");
        Check(wide.FitFactor(100000, wide.Height) == 1f, "a table that already fits is not scaled");
        Check(wide.FitFactor(1, 1) >= PanelGeometry.MinimumShrink, "the shrink factor is bounded so text stays readable");
        PanelGrid shrunk = PanelGeometry.Build(doubleContent, font * 2, 5, PanelDensity.Normal, fit);
        Check(shrunk.Width <= budget + 0.001f && shrunk.Height < wide.Height, "rebuilding with the fit factor actually fits");
        Check(shrunk.FontPixels < wide.FontPixels, "fitting the table also shrinks the font");
        Check(near(PanelGeometry.Build(content, font, 5, PanelDensity.Normal, 0.01f).FontPixels, PanelGeometry.Build(content, font, 5, PanelDensity.Normal, 0.45f).FontPixels), "the shrink factor is clamped when rebuilt");
        // A merged column is split into fields so every value can be right-aligned in its own slot
        // instead of the whole group being pushed around by the longest row.
        Check(CombatStatsFormatter.Fields(player.Total, PanelColumn.DamageAndShare, 80).Length == 2, "damage and share are two fields");
        Check(CombatStatsFormatter.Fields(player.Total, PanelColumn.DamageAndShare, 80)[1] == "100.0%", "the share is the second field");
        Check(CombatStatsFormatter.Fields(player.Total, PanelColumn.Projectiles).Length == 3, "the pellet column is three fields");
        Check(CombatStatsFormatter.Fields(player.Total, PanelColumn.Projectiles)[0] == "9", "the first pellet field is the total");
        Check(CombatStatsFormatter.Fields(new PlayerCombatStats(), PanelColumn.Projectiles).Length == 1, "unobserved pellets collapse to a single placeholder field");
        Check(string.Join(" ", CombatStatsFormatter.Fields(player.Total, PanelColumn.Projectiles)) == CombatStatsFormatter.ProjectilesText(player.Total), "the joined fields are exactly the column text");
        var friendlyFireOnly = new PlayerCombatStats();
        friendlyFireOnly.AddFriendlyFireDamage(39.9);
        string friendlyFireCell = CombatStatsFormatter.Fields(friendlyFireOnly, PanelColumn.FriendlyFire)[0];
        Check(friendlyFireCell == "39.9" && !friendlyFireCell.Contains("/"), "the in-level friendly-fire column shows damage only, never down counts");
        Check(CombatStrings.HeaderFields(PanelColumn.Projectiles).Length == 3, "the pellet header is split like its values");
        Check(CombatStrings.HeaderFields(PanelColumn.Projectiles)[0] == "总弹丸", "the pellet word qualifies the total instead of standing alone");
        Check(CombatStrings.HeaderFields(PanelColumn.DamageAndShare).Length == 2, "the damage header is split too");
        Check(string.Join(" ", CombatStrings.HeaderFields(PanelColumn.DamageAndShare)) == CombatStrings.Header(PanelColumn.DamageAndShare), "the joined header fields are the column header");
        // Field slots: every field keeps one right edge in every row, and a row with fewer fields
        // lands in the right-most slot.
        var columnMetrics = new List<PanelColumnMetrics>
        {
            new PanelColumnMetrics(new[] { 40f }),
            new PanelColumnMetrics(new[] { 60f, 80f }),
            new PanelColumnMetrics(new[] { 20f })
        };
        PanelGrid fields = PanelGeometry.Build(columnMetrics, 10f, font, 3, PanelDensity.Normal);
        float cellPadding = 2 * font * PanelGeometry.PaddingFactor * PanelDensity.Normal.Column;
        float columnGap = font * PanelGeometry.GapFactor * PanelDensity.Normal.Gap;
        Check(fields.Columns == 3 && fields.FieldCount(1) == 2 && fields.FieldCount(0) == 1, "the grid reports how many fields each column holds");
        Check(near(fields.Width, (40f + cellPadding) + (60f + 80f + 10f + cellPadding) + (20f + cellPadding) + columnGap * 2), "a merged column is as wide as its fields plus the gaps between them");
        Check(near(fields.FieldRect(0, 1, 0).Right, fields.FieldRect(2, 1, 0).Right), "the first field keeps one right edge in every row");
        Check(near(fields.FieldRect(0, 1, 1).Right, fields.FieldRect(2, 1, 1).Right), "the second field keeps one right edge in every row");
        Check(near(fields.FieldRect(0, 1, 0).X - fields.Cell(0, 1).X, fields.Cell(0, 1).Right - fields.FieldRect(0, 1, 1).Right), "the field group is centred, so the column's padding is shared evenly");
        Check(near(fields.SeparatorWidth, 10f), "the grid lays fields out with the gap it was handed");
        Check(near(PanelGeometry.FieldGap(font, PanelDensity.Normal), font * PanelGeometry.FieldGapFactor) && PanelGeometry.FieldGap(font, PanelDensity.Compact) < PanelGeometry.FieldGap(font, PanelDensity.Normal), "the gap between two fields is a fraction of the glyph size and follows the density");
        Check(PanelGeometry.FieldGap(font, PanelDensity.Normal) < PanelGeometry.EstimateTextWidth(" / ", font), "the field gap is narrower than the slash separator it replaced");
        Check(fields.SlotFor(1, 1, 0) == 1, "a row with fewer fields uses the right-most slot");
        Check(near(fields.FieldRectAligned(0, 1, 1, 0).X, fields.FieldRect(0, 1, 1).X), "an aligned field rect maps a short row to the last slots");
        var store = new CombatStatsStore(); store.GetOrCreate(11); store.GetOrCreate(12);
        Check(store.Players.Count() == 2, "identity is lookup rather than truncated name or reused player slot");
        store.Reset(); Check(!store.Players.Any(), "new expedition clears every player and weapon");
        var life = new ExpeditionLifecycle();
        Check(life.ShouldReset("Generating"), "reset at new generation");
        Check(!life.ShouldReset("Generating"), "repeated generation refresh does not reset twice");
        Check(!life.ShouldReset("InLevel"), "entering generated level keeps startup data");
        Check(!life.ShouldReset("ExpeditionSuccess"), "success retains statistics");
        Check(!life.ShouldReset("ExpeditionFail"), "failure retains statistics");
        Check(!life.ShouldReset("CaptureRecall") && !life.ShouldReset("InLevel"), "checkpoint recall does not silently erase counters");
        life.ShouldReset("Lobby"); Check(life.ShouldReset("Generating"), "next expedition resets previous results");
        var late = new ExpeditionLifecycle(); Check(late.ShouldReset("InLevel") && !late.ShouldReset("InLevel"), "late startup initializes once");
        // The result screens list the combined statistic per player and the individual ones
        // per weapon, in one plan the renderer simply follows.
        var plan = ResultLayout.Plan("玩家/综合", new[] { "A", "B" }, new List<IReadOnlyList<string>> { new[] { "primary", "special" }, new[] { "knife" } });
        Check(plan.Count == 7, "the result plan is a header, a TOTAL row and one row per weapon, with one blank row between players");
        Check(plan[0].Kind == ResultRowKind.Header && plan[0].Label == "玩家/综合", "the result table starts with its header");
        Check(plan[1].Kind == ResultRowKind.Total && plan[1].Squad == 0 && plan[1].Weapon == -1, "a player's first row carries their combined statistic");
        Check(plan[2].Kind == ResultRowKind.Weapon && plan[2].Weapon == 0 && plan[3].Weapon == 1, "weapon rows follow their TOTAL row in order");
        Check(plan[4].Kind == ResultRowKind.Spacer && plan[4].Label == string.Empty, "players are separated by a blank row");
        Check(plan[5].Kind == ResultRowKind.Total && plan[5].Squad == 1, "the second player keeps their own TOTAL row");
        Check(plan[6].Kind == ResultRowKind.Weapon && plan[6].Squad == 1 && plan[6].Weapon == 0, "each weapon row points at its own player and weapon");
        Check(plan.Count(row => row.Kind == ResultRowKind.Spacer) == 1, "there is no blank row at the edges of the table");
        Check(ResultLayout.Plan("h", Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>()).Count == 1, "no players leaves a header only table");
        Check(ResultLayout.Plan("h", new[] { "A" }, null).Count == 2, "a missing weapon list is tolerated");
        Check(ResultLayout.Plan("h", new[] { "A" }, new List<IReadOnlyList<string>> { Array.Empty<string>() }).Count == 2, "a player without weapons still gets a TOTAL row");
        Check(ResultLayout.Plan(null, null, null).Count == 1, "a missing squad list is tolerated");
        Check(ResultLayout.Plan("h", new[] { "A" }, new List<IReadOnlyList<string>> { new[] { "w" } })[2].Label == "w", "a weapon row carries the weapon name");
        Check(ResultLayout.Plan("h", new[] { "A", "B" }, new List<IReadOnlyList<string>> { new[] { "w" }, new[] { "x" } }).Count == 6, "each player adds one TOTAL row plus their weapons");
        Check(ResultLayout.WeaponLabel("SBS III") == "· SBS III", "weapon rows are marked as sub entries");
        Check(ResultLayout.WeaponLabel(null) == "· ", "a missing weapon name is safe");
        // ---------------------------------------------------------------------------------------
        // 1.8.2 spacing contract. The in-game feedback was that the columns sit too far apart, with
        // one exception: the three pellet counters were called "about right". These assertions pin
        // both halves of that feedback to the geometry the renderer really resolves, so a later
        // tweak to a padding, a gap or a column floor cannot quietly undo either of them.
        // ---------------------------------------------------------------------------------------
        const float spacingFont = 20f;
        var spacingColumns = PanelColumns.Compose(true, true, true, true, true);
        // 1.8.1: padding 0.45, column gap 0.55, and the four floors this round lowers.
        const float legacyPaddingFactor = 0.45f;
        const float legacyGapFactor = 0.55f;
        float[] spacingFloors = { 5.4f, 6.0f, 2.65f, 3.05f, 3.05f, 6.8f };
        float[] legacyFloors = { 5.4f, 7.0f, 3.4f, 4.6f, 4.6f, 6.8f };
        // Whitespace between two neighbouring columns is the padding of both cells plus the gap
        // between them, in ems of the glyph size. 1.8.2 brings it down to well under 0.68em.
        Check(2 * PanelGeometry.PaddingFactor * PanelDensity.Compact.Column + PanelGeometry.GapFactor * PanelDensity.Compact.Gap <= 0.68f,
            "the 1.8.2 column whitespace baseline is at most 0.68em");
        Check(near(2 * legacyPaddingFactor * PanelDensity.Compact.Column + legacyGapFactor * PanelDensity.Compact.Gap, 1.067f),
            "the 1.8.1 baseline this round improves on really was 1.067em");
        // The floors of the four columns whose header is the widest thing they ever hold.
        Check(PanelGeometry.MinimumWidth(PanelColumn.DamageAndShare, 20) / 2 / 20 <= 3.1f, "the damage/share floor is at most 3.1em per field");
        Check(PanelGeometry.MinimumWidth(PanelColumn.FriendlyFire, 20) / 20 <= 2.7f, "the friendly fire floor is at most 2.7em");
        Check(PanelGeometry.MinimumWidth(PanelColumn.Accuracy, 20) / 20 <= 3.1f, "the accuracy floor is at most 3.1em");
        Check(PanelGeometry.MinimumWidth(PanelColumn.WeakspotAccuracy, 20) / 20 <= 3.1f, "the weakspot floor is at most 3.1em");
        // And the pellet column, whose spacing is explicitly the one thing not to touch.
        Check(PanelGeometry.MinimumWidth(PanelColumn.Projectiles, 20) / 20 == 6.8f, "the pellet column floor is exactly its 1.8.1 value");
        Check(PanelGeometry.FieldGap(20, PanelDensity.Compact) == 20 * PanelGeometry.FieldGapFactor * PanelDensity.Compact.Gap, "the pellet field gap is still the glyph fraction the density scales");
        Check(PanelGeometry.FieldGapFactor == 0.60f, "the field gap factor itself is untouched");
        // A representative table: the real column set with the real formatted strings, at the HUD's
        // font size and density, carrying the header row the renderer draws as row zero.
        var spacingStats = new List<PlayerCombatStats>();
        double[] spacingDamage = { 812.4, 604.0, 233.5, 90.0 };
        double[] spacingFire = { 0.0, 14.5, 0.0, 3.0 };
        long[][] spacingShots = { new long[] { 40, 32, 9 }, new long[] { 60, 33, 5 }, new long[] { 40, 20, 2 }, new long[] { 20, 10, 1 } };
        string[] spacingNames = { "Haw Chow", "Kai", "Vex", "Nine" };
        double spacingSquad = spacingDamage.Sum();
        for (int index = 0; index < spacingDamage.Length; index++)
        {
            var entry = new PlayerCombatStats();
            entry.AddEnemyDamage(spacingDamage[index]);
            entry.AddFriendlyFireDamage(spacingFire[index]);
            entry.AddAccuracy(spacingShots[index][0], spacingShots[index][1], spacingShots[index][2]);
            spacingStats.Add(entry);
        }
        var spacingRows = new List<string[][]> { new string[spacingColumns.Count][] };
        for (int column = 0; column < spacingColumns.Count; column++) spacingRows[0][column] = CombatStrings.HeaderFields(spacingColumns[column]);
        for (int index = 0; index < spacingStats.Count; index++)
        {
            var row = new string[spacingColumns.Count][];
            for (int column = 0; column < spacingColumns.Count; column++)
                row[column] = column == 0
                    ? new[] { CombatStatsFormatter.TruncatePlayerName(spacingNames[index]) }
                    : CombatStatsFormatter.Fields(spacingStats[index], spacingColumns[column], spacingSquad);
            spacingRows.Add(row);
        }
        PanelGrid spaced = Shape(spacingColumns, spacingRows, spacingFont, PanelDensity.Compact, PanelGeometry.PaddingFactor, PanelGeometry.GapFactor, PanelGeometry.FieldGapFactor, spacingFloors);
        PanelGrid spaced181 = Shape(spacingColumns, spacingRows, spacingFont, PanelDensity.Compact, legacyPaddingFactor, legacyGapFactor, 0.60f, legacyFloors);
        Check(spaced.Columns == 6 && spaced.Rows == spacingRows.Count, "the spacing contract is judged on the real six-column table with its header row");
        Check(spaced.Width < spaced181.Width, "the 1.8.2 table is narrower than the same table laid out with the 1.8.1 spacing");
        // The five gaps the screenshot called out. Each is the distance from one text block to the
        // next, measured against the widest text the following field can hold.
        var tightened = new (string Name, int Left, int LeftSlot, int Right, int RightSlot)[]
        {
            ("伤害->占比", 1, 0, 1, 1),
            ("占比->友伤", 1, 1, 2, 0),
            ("友伤->命中率", 2, 0, 3, 0),
            ("命中率->弱点率", 3, 0, 4, 0),
            ("弱点率->总弹丸", 4, 0, 5, 0)
        };
        var spacingEvidence = new List<string>();
        foreach (var pair in tightened)
        {
            float before = NetWhitespace(spaced181, spacingRows, pair.Left, pair.LeftSlot, pair.Right, pair.RightSlot, spacingFont);
            float after = NetWhitespace(spaced, spacingRows, pair.Left, pair.LeftSlot, pair.Right, pair.RightSlot, spacingFont);
            spacingEvidence.Add(pair.Name + " " + (before / spacingFont).ToString("0.###") + "em->" + (after / spacingFont).ToString("0.###") + "em");
            Check(after < before * 0.75f, pair.Name + " is at least a quarter narrower than in 1.8.1 (" + before.ToString("0.###") + "px -> " + after.ToString("0.###") + "px)");
        }
        // The three pellet counters are the one distance that had to stay exactly where it was.
        for (int slot = 0; slot < 2; slot++)
        {
            float before = NetWhitespace(spaced181, spacingRows, 5, slot, 5, slot + 1, spacingFont);
            float after = NetWhitespace(spaced, spacingRows, 5, slot, 5, slot + 1, spacingFont);
            spacingEvidence.Add("弹丸" + (slot + 1) + "->" + (slot + 2) + " " + (before / spacingFont).ToString("0.###") + "em->" + (after / spacingFont).ToString("0.###") + "em");
            Check(Math.Abs(after - before) < 1e-4f, "pellet fields " + slot + " and " + (slot + 1) + " keep exactly their 1.8.1 spacing");
        }
        Console.WriteLine("SPACING: column whitespace "
            + (2 * legacyPaddingFactor * PanelDensity.Compact.Column + legacyGapFactor * PanelDensity.Compact.Gap).ToString("0.###") + "em@1.8.1 -> "
            + (2 * PanelGeometry.PaddingFactor * PanelDensity.Compact.Column + PanelGeometry.GapFactor * PanelDensity.Compact.Gap).ToString("0.###") + "em@1.8.2 | "
            + string.Join(" | ", spacingEvidence));

        // ---- the influence board ------------------------------------------------------------
        // Kill grading is decided from the enemy's own maximum health, so a modded enemy is graded
        // by what it is rather than by a name list that would go stale.
        CombatStrings.Language = "zh";
        Check(KillClassifier.Classify(119) == KillTier.Minor, "119 health is a minor kill");
        Check(KillClassifier.Classify(120) == KillTier.Elite, "120 health is an elite kill");
        Check(KillClassifier.Classify(999) == KillTier.Elite, "999 health is an elite kill");
        Check(KillClassifier.Classify(1000) == KillTier.Boss, "1000 health is a boss kill");
        Check(KillClassifier.Classify(5000) == KillTier.Boss, "a modded giant is graded by its own health");
        Check(KillClassifier.Classify(80, 50, 100) == KillTier.Elite, "the two thresholds are configuration");
        Check(KillClassifier.Classify(double.NaN) == KillTier.Minor, "unreadable health never lands outside the three tiers");
        var weightClasses = new ImpactCounters();
        weightClasses.CreditKill(KillTier.Minor); weightClasses.CreditKill(KillTier.Minor);
        weightClasses.CreditKill(KillTier.Elite); weightClasses.CreditKill(KillTier.Boss);
        Check(weightClasses.MinorKills == 2 && weightClasses.EliteKills == 1 && weightClasses.BossKills == 1, "each kill credits its own weight class");
        Check(weightClasses.Kills == 4, "the kill total is the three classes together");
        Check(Math.Abs(weightClasses.WeightedKills - 3.5) < 1e-9, "a boss is worth three minors and an elite two");

        // Every credit guards its own arithmetic, so a bad read shows a dash rather than a wrong score.
        var credited = new ImpactCounters();
        credited.CreditInterrupt(); credited.CreditInterrupt();
        Check(credited.Stagger == 2, "stagger is actual attack interruptions, not accumulated damage");
        credited.CreditRevive(); credited.CreditRevive();
        Check(credited.Revives == 2, "revives are counted");
        credited.CreditDown();
        Check(credited.Downs == 1, "a down is counted against the player who went down");
        credited.CreditScan(3); credited.CreditScan(0);
        Check(credited.ScanUses == 2 && credited.ScanFirstMarks == 3 && credited.ScanMarks == 3, "a sweep that revealed nothing new still counts as a sweep and its first marks");
        credited.RecordSupply(30, 100); credited.RecordSupply(double.NaN, 100);
        Check(credited.SupplyUses == 1 && Math.Abs(credited.SupplyEfficiencySum - 0.3) < 1e-9, "supply only records complete finite use samples");
        credited.RecordSupply(30, 0); credited.RecordSupply(30, double.NaN);
        Check(credited.SupplyUses == 1, "a use with unreadable intended amount is never fabricated");
        Check(Math.Abs(credited.SupplyEfficiency - 0.3) < 1e-9, "supply efficiency averages per-use utilization");
        credited.MarkLoneSurvivor();
        Check(credited.LoneSurvivor == 1, "the last one standing is recorded once");
        Check(new ImpactCounters().SupplyEfficiency == 0, "a squad that never supplied anybody divides by nothing");

        // A failed expedition hides the last-one-standing row, so its points must be gone too: the
        // displayed board and the score have to agree about a metric the player cannot see.
        var loneInput = new ImpactInput { Name = "孤军", EnemyDamage = 100 };
        loneInput.Counters.MarkLoneSurvivor();
        var mateInput = new ImpactInput { Name = "同行", EnemyDamage = 10 };
        var withLone = ImpactScoring.Evaluate(new[] { loneInput, mateInput }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        var withoutLone = ImpactScoring.Evaluate(new[] { loneInput, mateInput }, ImpactScoring.DefaultWeights, 1.0, 1.0, false);
        int loneMetric = (int)ImpactMetric.LoneSurvivor;
        ImpactEntry loneShown = withLone.Entries.First(entry => entry.Name == "孤军");
        ImpactEntry loneHidden = withoutLone.Entries.First(entry => entry.Name == "孤军");
        Check(withLone.MaxRaw[loneMetric] == 1 && loneShown.Raw[loneMetric] == 1, "a completed expedition counts the last one standing");
        Check(withoutLone.MaxRaw[loneMetric] == 0 && loneHidden.Raw[loneMetric] == 0, "a failed expedition scores no last-one-standing credit");
        Check(loneShown.Score > loneHidden.Score, "hiding the row also removes its points, so the board and the score agree");
        Check(withLone.Entries.Count == 2 && withoutLone.Entries.Count == 2, "the rest of the board is unaffected either way");

        // The weight list, the metric order and the role bonuses.
        Check(ImpactScoring.ParseWeights(ImpactScoring.DefaultWeightsText).Length == 13, "the published thirteen-weight list parses");
        Check(ImpactScoring.ParseWeights("1,2,3").SequenceEqual(ImpactScoring.DefaultWeights), "a short weight list falls back to the defaults");
        Check(ImpactScoring.ParseWeights("1,2,3,4,5,6,7,8,9,-1").SequenceEqual(ImpactScoring.DefaultWeights), "a negative weight falls back to the defaults");
        Check(ImpactScoring.ParseWeights("1,2,3,4,5,6,7,8,9,nope").SequenceEqual(ImpactScoring.DefaultWeights), "an unparseable weight falls back to the defaults");
        Check(ImpactScoring.ParseWeights(null).SequenceEqual(ImpactScoring.DefaultWeights), "a missing weight list falls back to the defaults");
        Check(ImpactScoring.ParseWeights("").SequenceEqual(ImpactScoring.DefaultWeights), "an empty weight list falls back to the defaults");
        Check(ImpactScoring.Describe(ImpactScoring.DefaultWeights) == ImpactScoring.DefaultWeightsText, "the weight list round trips through its own text");
        Check(ImpactScoring.Order.Length == 13 && ImpactScoring.Order[0] == ImpactMetric.TotalDamage
            && ImpactScoring.Order[10] == ImpactMetric.FriendlyFire && ImpactScoring.Order[11] == ImpactMetric.MeleeKills
            && ImpactScoring.Order[12] == ImpactMetric.ScoutKills, "thirteen metrics keep board order with friendly fire followed by melee and Scout kills");
        Check(ImpactScoring.IsReverse(ImpactMetric.Downs) && ImpactScoring.IsReverse(ImpactMetric.FriendlyFire) && !ImpactScoring.IsReverse(ImpactMetric.Kills), "downs and friendly-fire severity are reverse metrics");
        Check(ImpactScoring.UsesFirepower(ImpactMetric.TotalDamage) && ImpactScoring.UsesFirepower(ImpactMetric.Stagger) && !ImpactScoring.UsesFirepower(ImpactMetric.SupplyEfficiency), "the firepower half of the kit is exactly the fighting metrics");
        Check(!ImpactScoring.UsesFirepower(ImpactMetric.Kills) && !ImpactScoring.UsesFirepower(ImpactMetric.Accuracy) && !ImpactScoring.UsesFirepower(ImpactMetric.WeakspotRate), "the firepower bonus only lifts damage and stagger, never aim or kills");
        Check(ImpactScoring.UsesUtility(ImpactMetric.SupplyEfficiency) && !ImpactScoring.UsesUtility(ImpactMetric.Accuracy), "the utility half of the kit is exactly the support metrics");
        Check(Math.Abs(ImpactScoring.RoleFactor(ImpactMetric.TotalDamage, false, 1.25, 1.25) - 1.0) < 1e-9, "a player who is not support gets no bonus");
        Check(Math.Abs(ImpactScoring.RoleFactor(ImpactMetric.TotalDamage, true, 1.15, 1.15) - 1.15) < 1e-9, "a support player's damage is lifted by the modest default");
        Check(Math.Abs(ImpactScoring.RoleFactor(ImpactMetric.Stagger, true, 1.15, 1.15) - 1.15) < 1e-9, "a support player's stagger is lifted by the same bonus");
        Check(Math.Abs(ImpactScoring.RoleFactor(ImpactMetric.SupplyEfficiency, true, 1.15, 1.25) - 1.25) < 1e-9, "a support player's utility metrics are lifted by their own multiplier");
        Check(Math.Abs(ImpactScoring.RoleFactor(ImpactMetric.Kills, true, 1.15, 1.25) - 1.0) < 1e-9, "the firepower bonus never touches the kill row");
        Check(Math.Abs(ImpactScoring.RoleFactor(ImpactMetric.WeakspotRate, true, 1.15, 1.25) - 1.0) < 1e-9, "the firepower bonus never touches a rate row");
        Check(Math.Abs(ImpactScoring.RoleFactor(ImpactMetric.TotalDamage, true, 5.0, 1.0) - ImpactScoring.MaxRoleBonus) < 1e-9, "a hand-edited firepower bonus is clamped to the ceiling");
        Check(Math.Abs(ImpactScoring.RoleFactor(ImpactMetric.ToolEfficiency, true, 1.0, 5.0) - ImpactScoring.MaxRoleBonus) < 1e-9, "a hand-edited utility bonus is clamped to the same ceiling");
        Check(Math.Abs(ImpactScoring.RoleFactor(ImpactMetric.TotalDamage, true, 0.2, 0.2) - 1.0) < 1e-9, "a bonus below one never punishes a support player");
        Check(Math.Abs(ImpactScoring.MaxRoleBonus - 1.3) < 1e-9, "the role-bonus ceiling is the documented 1.3");
        Check(SupportTools.IsSupport(new[] { "生物追踪仪" }, SupportTools.DefaultNames), "a Chinese tool name matches");
        Check(SupportTools.IsSupport(new[] { "bio tracker mk2" }, SupportTools.DefaultNames), "a modded tool whose name contains a known one counts as support");
        Check(SupportTools.IsSupport(new[] { "Glue Gun" }, SupportTools.DefaultNames), "the glue gun is a support kit");
        Check(SupportTools.IsSupport(new[] { "C-Foam Launcher" }, SupportTools.DefaultNames), "the C-Foam launcher is a support kit");
        Check(!SupportTools.IsSupport(new[] { "HEL Revolver" }, SupportTools.DefaultNames), "a weapon is not a support tool");
        Check(!SupportTools.IsSupport(null, SupportTools.DefaultNames), "no gear means no support role");
        Check(!SupportTools.IsSupport(new[] { "Sentry Gun" }, SupportTools.DefaultNames, SupportTools.DefaultExcluded, true), "a sentry is never a support kit, even for the include-the-rest rule");
        Check(!SupportTools.IsSupport(new[] { "Tripmine" }, SupportTools.DefaultNames, SupportTools.DefaultExcluded, true), "a tripmine is never a support kit either");
        Check(SupportTools.IsSupport(new[] { "Modded Freeze Ray" }, SupportTools.DefaultNames, SupportTools.DefaultExcluded, true), "an unrecognised modded tool counts as support when the include-the-rest rule is on");
        Check(!SupportTools.IsSupport(new[] { "Modded Freeze Ray" }, SupportTools.DefaultNames, SupportTools.DefaultExcluded, false), "the include-the-rest rule is off by default for callers that do not opt in");
        Check(SupportTools.IsSupport(new[] { "Modded Sentry MK9" }, SupportTools.DefaultNames, SupportTools.DefaultExcluded, true) == false, "an excluded name wins over the include-the-rest rule");
        Check(SupportTools.Parse("a, b ,,c").Length == 3, "the configured tool list drops its empty entries");

        // A two-player squad: one all-round leader and one support player who did the reviving.
        var alphaPrimary = new PlayerCombatStats();
        alphaPrimary.AddEnemyDamage(100);
        alphaPrimary.AddAccuracy(10, 8, 2);
        var alpha = new ImpactInput { Name = "Alpha", EnemyDamage = 100, ShotsFired = 10, Hits = 8, WeakspotHits = 2, Primary = alphaPrimary };
        var beta = new ImpactInput { Name = "Beta", EnemyDamage = 50, ShotsFired = 10, Hits = 4, WeakspotHits = 0 };
        alpha.Counters.CreditKill(KillTier.Boss);
        beta.Counters.CreditKill(KillTier.Minor);
        beta.Counters.CreditRevive(); beta.Counters.CreditRevive();
        var report = ImpactScoring.Evaluate(new[] { alpha, beta }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        Check(report.Entries.Count == 2, "every player gets a row");
        Check(report.Entries[0].Name == "Alpha" && report.Entries[0].Rank == 1, "the squad is sorted by score, best first");
        Check(report.Entries[0].Score > report.Entries[1].Score, "the better all-round player scores higher");
        Check(report.Entries[0].Score <= 100 && report.Entries[1].Score >= 0, "a score is clamped into 0..100");
        Check(!double.IsNaN(report.Entries[0].Score) && !double.IsInfinity(report.Entries[0].Score), "no NaN score reaches the board");
        Check(report.IsGold(0, ImpactMetric.TotalDamage) && !report.IsGold(1, ImpactMetric.TotalDamage), "the damage leader earns the gold star");
        Check(report.IsGold(1, ImpactMetric.Revives) && !report.IsGold(0, ImpactMetric.Revives), "the reviver earns the gold star for revives");
        Check(!report.IsGold(0, ImpactMetric.Downs) && !report.IsGold(1, ImpactMetric.Downs), "a metric nobody scored is never gold");
        Check(report.MetricActive(ImpactMetric.TotalDamage) && !report.MetricActive(ImpactMetric.Downs), "an empty metric is inactive");
        Check(report.MaxRaw[(int)ImpactMetric.TotalDamage] == 100, "a metric's leader is its maximum");
        Check(report.MinRaw[(int)ImpactMetric.TotalDamage] == 50, "a metric's floor is its minimum");
        Check(report.CountAtMax[(int)ImpactMetric.TotalDamage] == 1, "an outright leader is alone at the top");
        Check(report.Entries[0].Rating == ImpactRating.Damage, "the sentence goes to the metric the player contributed most to");
        Check(report.Entries[0].RatingMetric == (int)ImpactMetric.TotalDamage, "the sentence names the metric that earned it");
        Check(report.Entries[1].Rating == ImpactRating.Revives, "the second player's sentence names their own strongest metric");

        // 1.13.0 recalibration. One rescue is not a contribution, and a rate read from three pellets
        // is not evidence: the revive row stays at zero below its floor and hands out no sentence.
        var oneRescue = new ImpactInput { Name = "One" };
        oneRescue.Counters.CreditRevive();
        Check(ImpactScoring.RawValue(oneRescue, ImpactMetric.Revives) == 0, "a single rescue scores nothing");
        Check(!ImpactScoring.HasValue(oneRescue, ImpactMetric.Revives), "a single rescue hands out no revive sentence");
        var rescueTintInput = new ImpactInput { Name = "RescueTint" };
        rescueTintInput.Counters.CreditRevive();
        var rescueNoCountInput = new ImpactInput { Name = "NoRescue" };
        var rescueTintReport = ImpactScoring.Evaluate(new[] { rescueTintInput, rescueNoCountInput }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        int rescueTintLeader = rescueTintReport.Entries.FindIndex(entry => entry.Name == "RescueTint");
        Check(!rescueTintReport.IsGold(rescueTintLeader, ImpactMetric.Revives), "one rescue remains below the scoring floor");
        Check(ImpactBoard.IsMetricGold(rescueTintReport, rescueTintReport.Entries[rescueTintLeader], rescueTintLeader, ImpactMetric.Revives, KillTier.Minor, false, true) == ImpactTone.Gold,
            "the unique raw rescue leader is highlighted even below the scoring floor");
        var rescueTintBoard = ImpactBoard.Build(rescueTintReport, false, true);
        int rescueTintMetricRow = -1;
        for (int rescueToneRow = 0; rescueToneRow < rescueTintBoard.Rows; rescueToneRow++)
            if (rescueTintBoard.Kinds[rescueToneRow] == ImpactRowKind.Metric && rescueTintBoard.Joined(rescueToneRow, 0) == CombatStrings.MetricLabel(ImpactMetric.Revives)) rescueTintMetricRow = rescueToneRow;
        Check(rescueTintMetricRow >= 0 && rescueTintBoard.Tone(rescueTintMetricRow, rescueTintLeader + 1) == ImpactTone.Gold,
            "the rendered rescue-count cell carries the leader tint");
        var rescueTieA = new ImpactInput { Name = "RescueTieA" }; rescueTieA.Counters.CreditRevive();
        var rescueTieB = new ImpactInput { Name = "RescueTieB" }; rescueTieB.Counters.CreditRevive();
        var rescueTieReport = ImpactScoring.Evaluate(new[] { rescueTieA, rescueTieB }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        bool rescueTieHasTint = false;
        for (int rescueTieIndex = 0; rescueTieIndex < rescueTieReport.Entries.Count; rescueTieIndex++)
            rescueTieHasTint |= ImpactBoard.IsMetricGold(rescueTieReport, rescueTieReport.Entries[rescueTieIndex], rescueTieIndex, ImpactMetric.Revives, KillTier.Minor, false, true) == ImpactTone.Gold;
        Check(!rescueTieHasTint, "a tie for the maximum rescue count stays unaccented");
        var rescueTieHighA = new ImpactInput { Name = "TieHighA" }; rescueTieHighA.Counters.CreditRevive(); rescueTieHighA.Counters.CreditRevive();
        var rescueTieHighB = new ImpactInput { Name = "TieHighB" }; rescueTieHighB.Counters.CreditRevive(); rescueTieHighB.Counters.CreditRevive();
        var rescueTieHighReport = ImpactScoring.Evaluate(new[] { rescueTieHighA, rescueTieHighB }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        bool rescueTieHighHasTint = false;
        for (int rescueTieHighIndex = 0; rescueTieHighIndex < rescueTieHighReport.Entries.Count; rescueTieHighIndex++)
            rescueTieHighHasTint |= ImpactBoard.IsMetricGold(rescueTieHighReport, rescueTieHighReport.Entries[rescueTieHighIndex], rescueTieHighIndex, ImpactMetric.Revives, KillTier.Minor, false, true) == ImpactTone.Gold;
        Check(!rescueTieHighHasTint, "a tie at the maximum rescue count leaves all tied cells neutral");

        var twoRescues = new ImpactInput { Name = "Two" };
        twoRescues.Counters.CreditRevive(); twoRescues.Counters.CreditRevive();
        Check(ImpactScoring.RawValue(twoRescues, ImpactMetric.Revives) == 2, "rescues at the floor score again");
        var lonely = ImpactScoring.Evaluate(new[] { oneRescue, twoRescues }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        Check(lonely.Entries[0].Name == "Two", "one rescue alone cannot top the board");
        Check(lonely.Entries.First(row => row.Name == "Two").Rating == ImpactRating.Revives, "the player who really rescued twice wears the revive sentence");
        Check(lonely.Entries.First(row => row.Name == "One").Rating != ImpactRating.Revives, "the single rescuer is never called 妙手回春");

        // Accuracy is scaled by how much of the confidence sample was actually fired, and at one fixed
        // sample the figure still never moves backwards as the rate rises.
        double previousAccuracy = -1;
        foreach (double bandRate in new[] { 0.5, 0.6, 0.7, 0.8, 0.9 })
        {
            var band = new ImpactInput { Name = "Band", ShotsFired = 100, Hits = (int)Math.Round(bandRate * 100) };
            double value = ImpactScoring.AccuracyValue(band);
            Check(value > previousAccuracy, "a higher hit rate at one sample never scores lower (" + (bandRate * 100).ToString("0") + "%)");
            previousAccuracy = value;
        }
        var tiny = new ImpactInput { Name = "Tiny", ShotsFired = 3, Hits = 3 };
        var solid = new ImpactInput { Name = "Solid", ShotsFired = 100, Hits = 80 };
        Check(Math.Abs(ImpactScoring.AccuracyValue(tiny) - 0.15) < 1e-9, "a three-pellet perfect rate carries a fifth of its weight");
        Check(ImpactScoring.AccuracyValue(tiny) < ImpactScoring.AccuracyValue(solid), "three pellets at 100% cannot outweigh 80% over a hundred");
        Check(ImpactScoring.RawValue(tiny, ImpactMetric.Accuracy) == ImpactScoring.AccuracyValue(tiny), "the accuracy metric scores the confidence-scaled figure");
        var visibleAccuracyLeader = new ImpactInput { Name = "VisibleAccuracyLeader", ShotsFired = 5, Hits = 2 };
        var confidenceAccuracyLeader = new ImpactInput { Name = "ConfidenceAccuracyLeader", ShotsFired = 64, Hits = 8 };
        var accuracyOnlyWeights = new double[ImpactScoring.Order.Length];
        accuracyOnlyWeights[(int)ImpactMetric.Accuracy] = 1;
        ImpactReport accuracyLeadership = ImpactScoring.Evaluate(new[] { visibleAccuracyLeader, confidenceAccuracyLeader }, accuracyOnlyWeights, 1, 1);
        int visibleAccuracyIndex = accuracyLeadership.Entries.FindIndex(entry => entry.Name == "VisibleAccuracyLeader");
        int confidenceAccuracyIndex = accuracyLeadership.Entries.FindIndex(entry => entry.Name == "ConfidenceAccuracyLeader");
        Check(ImpactScoring.AccuracyValue(confidenceAccuracyLeader) > ImpactScoring.AccuracyValue(visibleAccuracyLeader)
            && ImpactScoring.DisplayedAccuracy(confidenceAccuracyLeader) < ImpactScoring.DisplayedAccuracy(visibleAccuracyLeader),
            "the sample-confidence score can differ from the raw hit rate printed on the board");
        Check(accuracyLeadership.IsGold(visibleAccuracyIndex, ImpactMetric.Accuracy)
            && !accuracyLeadership.IsGold(confidenceAccuracyIndex, ImpactMetric.Accuracy),
            "accuracy gold follows the displayed raw rate, not its confidence-scaled score");
        Check(accuracyLeadership.Entries[visibleAccuracyIndex].Rating == ImpactRating.Accuracy
            && accuracyLeadership.Entries[confidenceAccuracyIndex].Rating != ImpactRating.Accuracy,
            "the Deadeye title cannot go to a player who trails the displayed hit rate");
        var accuracyLeadershipBoard = ImpactBoard.Build(accuracyLeadership, false, true);
        int accuracyMetricRow = ImpactBoard.FixedRows + ImpactBoard.SeparatorRows + (int)ImpactMetric.Accuracy;
        Check(accuracyLeadershipBoard.Tone(accuracyMetricRow, visibleAccuracyIndex + 1) == ImpactTone.Gold
            && accuracyLeadershipBoard.Tone(accuracyMetricRow, confidenceAccuracyIndex + 1) == ImpactTone.Neutral,
            "rendered accuracy highlights match the displayed-rate leader");

        // The previous published weight list is migrated; a hand-edited list is not.
        Check(ImpactScoring.IsLegacyDefaultWeights(ImpactScoring.LegacyDefaultWeightsText), "the previous published weights are recognised for migration");
        Check(ImpactScoring.IsLegacyDefaultWeights(" " + ImpactScoring.LegacyDefaultWeightsText + " "), "surrounding whitespace does not defeat the migration");
        Check(!ImpactScoring.IsLegacyDefaultWeights(ImpactScoring.DefaultWeightsText), "this release's own weights are already current");
        Check(!ImpactScoring.IsLegacyDefaultWeights("2,2,2,2,2,2,2,2,2,2"), "a hand-edited weight list is left alone");
        Check(!ImpactScoring.IsLegacyDefaultWeights(null), "a missing weight list is not mistaken for the legacy default");

        // Ties, a squad of one, and an empty squad.
        var tieA = new ImpactInput { Name = "A", EnemyDamage = 40 };
        var tieB = new ImpactInput { Name = "B", EnemyDamage = 40 };
        var tied = ImpactScoring.Evaluate(new[] { tieA, tieB }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        Check(!tied.IsGold(0, ImpactMetric.TotalDamage) && !tied.IsGold(1, ImpactMetric.TotalDamage), "a tie for the lead earns nobody a gold star");
        Check(Math.Abs(tied.Entries[0].Score - tied.Entries[1].Score) < 1e-9, "an even squad scores evenly");
        Check(tied.CountAtMax[(int)ImpactMetric.TotalDamage] == 2, "a tie is recorded as two players at the top");
        Check(tied.Entries[0].Score > 0 && tied.Entries[0].Rating == ImpactRating.TeamPillar, "equal positive contributions have no unique top and earn neutral titles");
        Check(tied.Entries.All(e => e.Rating == ImpactRating.TeamPillar), "neutral titles may repeat for tied players");
        var alone = ImpactScoring.Evaluate(new[] { new ImpactInput { Name = "Solo", EnemyDamage = 10 } }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        Check(alone.Entries.Count == 1 && alone.Entries[0].Score >= 0 && alone.Entries[0].Score <= 100, "a squad of one scores without dividing by zero");
        Check(ImpactScoring.Evaluate(null, null, 1.0, 1.0).Entries.Count == 0, "an empty squad yields an empty board");
        var star = new ImpactInput { Name = "Star", EnemyDamage = 500 };
        var rest = new ImpactInput { Name = "Rest", EnemyDamage = 5 };
        var rated = ImpactScoring.Evaluate(new[] { star, rest }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        Check(rated.Entries[0].Rating == ImpactRating.Damage, "the overwhelming damage lead earns the firepower sentence");
        Check(rated.Entries[0].Rating == ImpactRating.Damage, "the overwhelming damage lead earns the firepower sentence");
        Check(rated.Entries[1].Rating == ImpactRating.TeamPillar, "a player who leads nothing receives the neutral title");

        // Four players who all contribute: every place ends up with a sentence of its own, and the
        // top of the board is evaluated first.
        var four = new List<ImpactInput>();
        for (int index = 0; index < 4; index++)
        {
            var member = new ImpactInput { Name = "P" + index, EnemyDamage = 1000 - index * 100, ShotsFired = 40, Hits = 40 - index * 5, WeakspotHits = 20 - index };
            member.Counters.CreditRevive();
            member.Counters.CreditKill(KillTier.Minor);
            four.Add(member);
        }
        var squad = ImpactScoring.Evaluate(four, ImpactScoring.DefaultWeights, 1.0, 1.0);
        Check(squad.Entries.Count == 4, "a full squad is scored");
        Check(squad.Entries.Where(e => e.Rating != ImpactRating.TeamPillar).Select(e => e.Rating).Distinct().Count() == squad.Entries.Count(e => e.Rating != ImpactRating.TeamPillar), "earned titles are distinct; neutral titles may repeat");
        Check(squad.Entries[0].Rating == ImpactRating.Damage, "the best placed player takes the sentence they earned before anyone else is evaluated");
        Check(squad.Entries.All(entry => !string.IsNullOrEmpty(CombatStrings.Title(entry.Rating))), "every sentence is one a player can read");

        // A stagger is an interrupt and only an interrupt: it needs an attack already under way and
        // not yet finished, and a kill is a kill rather than an interrupt.
        Check(StaggerRule.IsInterrupt(StaggerKind.Light, 10f, 100f, true, true), "a light hit react on an enemy mid-attack is an interrupt");
        Check(StaggerRule.IsInterrupt(StaggerKind.Heavy, 10f, 100f, true, true), "a heavy hit react on an enemy mid-attack is an interrupt");
        Check(!StaggerRule.IsInterrupt(StaggerKind.Lethal, 10f, 100f, true, true), "the react that reports a death is never an interrupt");
        Check(!StaggerRule.IsInterrupt(StaggerKind.None, 10f, 100f, true, true), "a hit that does not move the enemy is not an interrupt");
        Check(!StaggerRule.IsInterrupt(StaggerKind.Light, 10f, 100f, false, true), "a hit react on an enemy that is not attacking is not an interrupt");
        Check(StaggerRule.IsInterrupt(StaggerKind.Light, 10f, 100f, false, false), "with the attack check switched off any hit react of the right kind counts");
        Check(!StaggerRule.IsInterrupt(StaggerKind.Light, 100f, 100f, true, true), "a blow that takes the last of the enemy's health is a kill, not an interrupt");
        Check(!StaggerRule.IsInterrupt(StaggerKind.Light, 120f, 100f, true, true), "an overkill is a kill, not an interrupt");
        Check(StaggerRule.IsInterrupt(StaggerKind.Light, 99f, 100f, true, true), "a blow that leaves the enemy standing is still an interrupt");
        Check(!StaggerRule.IsInterrupt(StaggerKind.Light, 0f, 100f, true, true) && !StaggerRule.IsInterrupt(StaggerKind.Light, float.NaN, 100f, true, true), "a blow that dealt no damage is never an interrupt");
        Check(StaggerRule.IsInterrupt(StaggerKind.Heavy, 10f, 0f, true, true), "a health total that could not be read does not by itself reject the interrupt");

        // A melee push deals no damage, so it is allowed in on the attack state instead: it counts
        // only while the attack it broke was under way on an enemy that was still standing, and it
        // is still refused for a react that reports a death.
        Check(StaggerRule.IsInterrupt(StaggerKind.Heavy, 0f, 100f, true, true, true), "a melee push that broke an attack under way is an interrupt even with no damage");
        Check(StaggerRule.IsInterrupt(StaggerKind.Light, 0f, 100f, true, true, true), "a light react from a push counts the same way");
        Check(!StaggerRule.IsInterrupt(StaggerKind.Heavy, 0f, 100f, false, true, true), "a push against an enemy that was not attacking interrupted nothing");
        Check(!StaggerRule.IsInterrupt(StaggerKind.Heavy, 0f, 0f, true, true, true), "a push on an enemy with no health left counts nothing");
        Check(!StaggerRule.IsInterrupt(StaggerKind.Lethal, 0f, 100f, true, true, true), "the react that reports a death is never an interrupt, push or not");
        Check(!StaggerRule.IsInterrupt(StaggerKind.None, 0f, 100f, true, true, true), "a push that does not move the enemy is not an interrupt");

        // The board itself: rows are fields, columns are players, and the label column is its own thing.
        var board = ImpactBoard.Build(report, false, true);
        Check(board.Rows == 28, "the board is fourteen fixed rows, a separator and thirteen metrics");
        Check(ImpactBoard.RowCount(false) == 28 && ImpactBoard.RowCount(true) == 30, "splitting the kill row adds the two extra weight classes");
        Check(board.ColumnCount == 3, "one label column plus one column per player");
        Check(board.Columns[0] == PanelColumn.ImpactLabel && board.Columns[1] == PanelColumn.ImpactValue, "the board gives its columns their own identities");
        Check(board.Kinds[0] == ImpactRowKind.PlayerName && board.Kinds[1] == ImpactRowKind.Score && board.Kinds[2] == ImpactRowKind.Rating, "the three rows over the gear are the name, the score and the sentence");
        Check(board.Kinds[13] == ImpactRowKind.SecondaryPellets && board.Kinds[14] == ImpactRowKind.Separator, "the gear block ends at the separator");
        Check(board.Kinds[15] == ImpactRowKind.Metric && board.Kinds[25] == ImpactRowKind.Metric, "every metric row is a metric row");
        Check(board.Joined(0, 0) == CombatStrings.ImpactRowLabels()[0], "the first cell is the field label");
        Check(board.Joined(14, 0) == CombatStrings.ImpactMetricHeader, "the separator row names what follows");
        Check(board.Joined(15, 0) == CombatStrings.MetricLabel(ImpactMetric.TotalDamage), "the first metric row is labelled");
        Check(board.Joined(0, 1) == "#1 Alpha", "the first row carries the place and the name");
        int nameRows = 0;
        for (int row = 0; row < board.Rows; row++) foreach (string field in board.Cell(row, 1)) if (field != null && field.Contains("Alpha")) nameRows++;
        Check(nameRows == 1, "the name is written exactly once instead of on a row of its own");
        Check(board.Joined(1, 1).StartsWith(report.Entries[0].Score.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)), "the score sits on the row below the name");
        Check(board.Joined(2, 1) == CombatStrings.Title(report.Entries[0].Rating), "the sentence sits on the row below that");
        Check(board.Tone(0, 1) == ImpactTone.PlayerColor && board.Tone(0, 0) == ImpactTone.Neutral, "the name is in the player's colour and the labels stay neutral");
        Check(board.Tone(1, 1) == ImpactTone.Gold && board.Tone(1, 2) == ImpactTone.Silver, "the score row is coloured by place");
        Check(board.Tone(2, 1) == ImpactTone.Neutral, "the sentence row stays plain white");
        Check(board.Kinds[3] == ImpactRowKind.RatingReason && board.Kinds[2] == ImpactRowKind.Rating, "the explanation sits directly under the sentence");
        Check(board.Joined(3, 0) == CombatStrings.ImpactRowLabels()[3] && !string.IsNullOrEmpty(CombatStrings.ImpactRowLabels()[3]), "the explanation row is labelled");
        Check(board.Joined(3, 1) == ImpactBoard.ReasonText(report.Entries[0], 150.0), "the explanation prints the figures the sentence was earned with");
        Check(board.Cell(3, 1).Length == 1, "the explanation is one sentence, not a pair of numbers");
        Check(board.Joined(3, 1).Any(char.IsDigit), "the explanation carries real numbers");
        Check(board.Tone(3, 1) == ImpactTone.Neutral, "the explanation row stays plain white");
        // Every sentence the board can hand out comes with the figures behind it, and a player who scored
        // nothing is told so in words rather than being left with a blank row.
        var reasonScanCounters = new ImpactCounters(); reasonScanCounters.CreditScan(4); reasonScanCounters.CreditScan(2);
        var reasonScan = new ImpactEntry(new ImpactInput { Name = "Scan", Counters = reasonScanCounters }, null, null, 50, 1, ImpactRating.Scan, (int)ImpactMetric.ToolEfficiency);
        Check(ImpactBoard.ReasonText(reasonScan, 0) == "扫描 2 次，首次标记 6 只", "the scan explanation counts the sweeps and the first marks");
        var reasonHurt = new ImpactEntry(new ImpactInput { Name = "Hurt", EnemyDamage = 100, EnemyDamageObserved = true }, null, null, 60, 1, ImpactRating.Damage, (int)ImpactMetric.TotalDamage);
        Check(ImpactBoard.ReasonText(reasonHurt, 150) == "伤害 100.0，占全队 66.7%", "the damage explanation carries the damage and its share");
        var reasonBlind = new ImpactEntry(new ImpactInput { Name = "Blind" }, null, null, 20, 1, ImpactRating.Damage, (int)ImpactMetric.TotalDamage);
        Check(ImpactBoard.ReasonText(reasonBlind, 150) == "—", "a damage sentence nobody could measure explains itself with a dash");
        var reasonStagger = new ImpactEntry(new ImpactInput { Name = "Stop" }, null, null, 40, 1, ImpactRating.Stagger, (int)ImpactMetric.Stagger);
        reasonStagger.Input.Counters.Stagger = 3;
        Check(ImpactBoard.ReasonText(reasonStagger, 0) == "打断敌人攻击 3 次", "the stagger explanation counts the attacks that were stopped");
        var reasonKill = new ImpactEntry(new ImpactInput { Name = "Kill" }, null, null, 40, 1, ImpactRating.MinorKills, (int)ImpactMetric.Kills);
        reasonKill.Input.Counters.CreditKill(KillTier.Minor); reasonKill.Input.Counters.CreditKill(KillTier.Minor);
        Check(ImpactBoard.ReasonText(reasonKill, 0) == "击杀小怪 2 只", "the kill explanation names the weight class it counts");
        var reasonSupply = new ImpactEntry(new ImpactInput { Name = "Supp" }, null, null, 40, 1, ImpactRating.Supply, (int)ImpactMetric.SupplyEfficiency);
        reasonSupply.Input.Counters.RecordSupply(30, 100);
        Check(ImpactBoard.ReasonText(reasonSupply, 0) == "补给 1 次，有效 30.0%", "the supply explanation counts the uses and how effective they were");
        var reasonDown = new ImpactEntry(new ImpactInput { Name = "Down" }, null, null, 10, 1, ImpactRating.Downs, (int)ImpactMetric.Downs);
        reasonDown.Input.Counters.CreditDown();
        Check(ImpactBoard.ReasonText(reasonDown, 0) == "阵亡 1 次", "the death explanation counts the deaths");
        var reasonZero = new ImpactEntry(new ImpactInput { Name = "Zero" }, null, null, 0, 4, ImpactRating.ZeroScore, -1);
        Check(ImpactBoard.ReasonText(reasonZero, 0) == CombatStrings.ReasonNoScore, "a zero-score player is told there was nothing to score");
        Check(ImpactBoard.ReasonText(null, 0) == "—", "a missing row explains itself with a dash");
        Check(board.Cell(1, 1)[0] == report.Entries[0].Score.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), "the score row prints the score");
        Check(board.Cell(1, 1).Length == 1, "the score row uses colour only, with no medal word");
        Check(ImpactBoard.RankTone(1) == ImpactTone.Gold && ImpactBoard.RankTone(2) == ImpactTone.Silver && ImpactBoard.RankTone(3) == ImpactTone.Copper && ImpactBoard.RankTone(4) == ImpactTone.Neutral, "only the podium is coloured");
        Check(board.Joined(4, 1) == "—" && board.Joined(7, 1) == "—", "a slot with no gear prints a dash");

        // The merged statistic rows of a weapon, and the damage share they carry.
        Check(board.Joined(8, 1) == "100.0 / 66.7%", "the primary's damage row carries its share of the squad's damage");
        Check(board.Joined(9, 1) == "80.0% / 25.0%", "the primary's hit and weakspot rates sit side by side");
        Check(board.Joined(10, 1) == "10 / 8 / 2", "the primary's pellet triple sits side by side");
        var armedInput = new ImpactInput { Name = "Armed", Primary = new PlayerCombatStats() };
        var armedBoard = ImpactBoard.Build(ImpactScoring.Evaluate(new[] { armedInput }, ImpactScoring.DefaultWeights, 1.0, 1.0), false, true);
        Check(armedBoard.Joined(8, 1) == "0.0 / —", "a weapon with no damage prints a real zero and a dash for the share");
        Check(armedBoard.Joined(10, 1) == "—", "a weapon that never fired shows one dash for its pellet triple");
        Check(ImpactBoard.RateText(new ImpactInput { Hits = 8, ShotsFired = 10, WeakspotHits = 2 }, true) == "25.0%", "the weakspot rate is weakspot hits over hits");
        Check(ImpactBoard.RateText(new ImpactInput { Hits = 8, ShotsFired = 10 }, false) == "80.0%", "the hit rate is hits over fired pellets");
        Check(ImpactBoard.RateText(new ImpactInput(), false) == "—", "an untouched weapon shows a dash, not a fabricated zero");
        Check(ImpactBoard.RateText(new ImpactInput { Hits = 4, ShotsFired = 8, AccuracyEstimated = true }, false) == "~50.0%", "an estimated rate keeps its marker");

        // Supply, scan, lone survivor and deaths, and the split kill rows.
        var supplier = new ImpactInput { Name = "Supp", ToolName = "Bio Tracker" };
        supplier.Counters.RecordSupply(30, 100);
        supplier.Counters.CreditScan(4);
        var supplyBoard = ImpactBoard.Build(ImpactScoring.Evaluate(new[] { supplier }, ImpactScoring.DefaultWeights, 1.0, 1.0), false, true);
        Check(supplyBoard.Joined(21, 1) == "30.0%", "one measured supply use displays its utilization");
        Check(supplyBoard.Joined(22, 1) == "扫描 1 次 / 首次 4 只 / 累计 4 只", "the scanner cell shows attempts, first marks and cumulative marks, one field per clause");
        Check(supplyBoard.Tone(21, 1) == ImpactTone.Gold, "the only supplier leads the supply row");
        var dry = new ImpactInput { Name = "Dry", ToolName = "Bio Tracker" };
        var dryBoard = ImpactBoard.Build(ImpactScoring.Evaluate(new[] { dry }, ImpactScoring.DefaultWeights, 1.0, 1.0), false, true);
        Check(dryBoard.Joined(21, 1) == "—" && dryBoard.Joined(22, 1) == "扫描 —", "unperformed supply and unobserved scanning remain explicit for an equipped scanner");
        Check(dryBoard.Joined(23, 1) == "0", "a squad that never split up shows a real zero for lone survivor");
        Check(dryBoard.Joined(24, 1) == "0", "nobody died, so the deaths row is a real zero");
        var failed = ImpactBoard.Build(report, false, false);
        Check(failed.Joined(23, 1) == "—", "the lone-survivor row shows a dash when the expedition failed");
        Check(ImpactBoard.Build(report, false, true).Joined(23, 1) == "0", "the lone-survivor row shows its real count on a success");
        Check(failed.Joined(21, 1) == board.Joined(21, 1), "a failure changes nothing but the lone-survivor row");
        var splitBoard = ImpactBoard.Build(report, true, true);
        Check(splitBoard.Rows == 30, "the split board has the two extra kill rows");
        Check(splitBoard.Joined(17, 0) == "小怪击杀" && splitBoard.Joined(18, 0) == "精英击杀" && splitBoard.Joined(19, 0) == "BOSS击杀", "split kill rows use complete understandable labels");
        Check(splitBoard.Cell(19, 1)[0] == "1" && splitBoard.Tone(19, 1) == ImpactTone.Gold, "the boss row is led by the only player who killed one");
        Check(splitBoard.Tone(19, 2) == ImpactTone.Neutral, "a weight class nobody reached is never gold");
        Check(splitBoard.Tone(17, 1) == ImpactTone.Neutral && splitBoard.Tone(17, 2) == ImpactTone.Gold, "a tier with a single leader is gold for that leader alone"); Check(splitBoard.Joined(20, 0) == CombatStrings.MetricLabel(ImpactMetric.MeleeKills) && splitBoard.Joined(21, 0) == CombatStrings.MetricLabel(ImpactMetric.ScoutKills) && splitBoard.Joined(22, 0) == CombatStrings.MetricLabel(ImpactMetric.Revives), "settlement melee and tentacle rows follow the three kill tiers");
        Check(board.Joined(17, 1) == "0小怪 / 0精英 / 1BOSS", "even a legacy merged model uses complete category names");

        // A support player is marked, and the labels exist in both languages.
        var support = new ImpactInput { Name = "Sup", SupportRole = true, ToolName = "Bio Tracker" };
        var supportBoard = ImpactBoard.Build(ImpactScoring.Evaluate(new[] { support }, ImpactScoring.DefaultWeights, 1.0, 1.0), false, true);
        Check(supportBoard.Joined(7, 1).EndsWith(CombatStrings.SupportMark), "a support player's tool row is marked");
        Check(CombatStrings.ImpactRowLabels().Length == ImpactBoard.FixedRows, "there is exactly one label per fixed row");
        Check(CombatStrings.ImpactRowLabels().All(label => !string.IsNullOrEmpty(label)), "no fixed row is unlabelled");
        foreach (ImpactMetric metric in ImpactScoring.Order) Check(!string.IsNullOrEmpty(CombatStrings.MetricLabel(metric)), metric + " has a metric label");
        foreach (ImpactRating rating in (ImpactRating[])Enum.GetValues(typeof(ImpactRating))) Check(!string.IsNullOrEmpty(CombatStrings.Title(rating)), rating + " has a sentence");
        Check(CombatStrings.RankWord(1) == "金" && CombatStrings.RankWord(2) == "银" && CombatStrings.RankWord(3) == "铜", "the podium words are Chinese by default");
        Check(CombatStrings.RankWord(4) == string.Empty, "everyone off the podium carries no place word");
        Check(CombatStrings.KillTierLabel(KillTier.Boss) == "BOSS", "the boss weight class has its own label");
        Check(CombatStrings.Placeholder == "—", "the placeholder is a single dash");
        CombatStrings.Language = "en";
        Check(CombatStrings.ImpactRowLabels()[0] == "Player" && CombatStrings.ImpactRowLabels()[1] == "Score" && CombatStrings.ImpactRowLabels()[2] == "Rating" && CombatStrings.MetricLabel(ImpactMetric.Downs) == "Downs / health lost", "the board's labels follow the language setting, and a label that names two figures names both");
        Check(CombatStrings.Title(ImpactRating.Damage) == "Firepower core" && CombatStrings.RankWord(1) == "gold", "the sentences follow the language setting too");
        Check(CombatStrings.KillTierLabel(KillTier.Minor) == "minor", "the weight classes follow the language setting");
        Check(ImpactBoard.Build(report, false, true).Cell(1, 1)[0] == board.Cell(1, 1)[0], "the score itself does not depend on the language");
        CombatStrings.Language = "zh";
        Check(ImpactBoard.Build(null, false, true).Rows == 28, "a missing report yields an empty thirteen-metric board rather than a crash");
        Check(ResultsPlacement.Top(180f, -500f) == 196f, "negative saved offsets cannot move the board above the failure heading");
        Check(ResultsPlacement.Top(180f, 20f) == 216f, "positive result offsets preserve heading clearance");
        Check(ResultsPlacement.Top(180f, float.NaN) == 196f, "invalid offsets do not poison placement");
        Check(ResultsPlacement.TryFit(1920f, 1080f, 196f, 1100f, 700f, -9999f, out var placed) && placed.X == 0 && placed.Y == 196f, "horizontal clamp leaves the board below the heading");
        Check(!ResultsPlacement.TryFit(1280f, 720f, 400f, 1100f, 500f, 0, out _), "insufficient vertical space never pushes a board over its heading");
        Check(!ResultsPlacement.TryFit(1280f, 720f, 200f, 1400f, 400f, 0, out _), "over-wide boards must shrink before placement");
        Check(!ResultsPlacement.TryFit(1280f, 720f, float.NaN, 100f, 100f, 0, out _), "invalid placement inputs are rejected");

        // The success board lives in the strip between the page's completion section and the button that
        // sends the squad home, and the table scales its glyphs and rows into that strip.
        var strip = ResultsRegion.Resolve(true, 520f, true, 1180f, 1440f, 16f);
        Check(near(strip.Top, 536f) && near(strip.Bottom, 1164f) && near(strip.Height, 628f), "a measured completion section and lobby button define the strip between them");
        var noButton = ResultsRegion.Resolve(true, 520f, false, 0f, 1440f, 16f);
        Check(near(noButton.Top, 536f) && near(noButton.Bottom, 1440f * ResultsRegion.FallbackBottomFraction)
            && noButton.Bottom < 1440f - 8f,
            "an unreadable lobby button stops the board at the fraction of the window that clears every footer measured on a real page, instead of running it to the foot of the screen and over the button it could not read");
        var noCompletion = ResultsRegion.Resolve(false, 0f, true, 1180f, 1440f, 16f);
        Check(near(noCompletion.Top, 1440f * ResultsRegion.FallbackTopFraction) && near(noCompletion.Bottom, 1164f), "an unreadable completion section falls back to the historical fraction");
        Check(!ResultsRegion.Resolve(true, 1200f, true, 1100f, 1440f, 16f).Fits, "a button above the completion section yields no region rather than an overlap");
        Check(!ResultsRegion.Resolve(true, 520f, true, 1180f, 0f, 16f).Fits, "a zero-height window yields no region");
        var nonFiniteRegion = ResultsRegion.Resolve(true, float.NaN, true, float.NaN, 1440f, 16f);
        Check(near(nonFiniteRegion.Top, 230.4f) && near(nonFiniteRegion.Bottom, 1440f * ResultsRegion.FallbackBottomFraction), "a non-finite edge counts as unmeasured instead of poisoning the region");
        Check(near(ResultsRegion.LowestBottom(600f, 400f), 600f) && near(ResultsRegion.LowestBottom(400f, 600f), 600f), "the completion section is cleared by its deepest edge");
        // The 720-high window in the screenshot this rule came from: its own footer sits at 0.83 of the
        // window, and the board used to be allowed down to 0.994 of it, which is exactly the overlap seen.
        var screenshot1 = ResultsRegion.Resolve(false, 0f, false, float.NaN, 720f, 8f);
        Check(screenshot1.Fits && screenshot1.Bottom <= 720f * 0.83f,
            "a 720-high page whose button could not be read keeps the board clear of a footer at 0.83 of the window");
        Check(near(ResultsRegion.HighestTop(200f, 900f), 200f) && near(ResultsRegion.HighestTop(900f, 200f), 200f), "the footer is cleared by its highest edge");
        Check(!float.IsFinite(ResultsRegion.LowestBottom(float.NaN, float.NaN)) && !float.IsFinite(ResultsRegion.HighestTop(float.NaN, float.NaN)), "an unreadable page element contributes no edge");

        // One twenty-eight row board, three window sizes: each gets a region, and the glyphs shrink into
        // the room instead of rows being dropped, so the third and fourth players stay on screen.
        var stripMetrics = new[] { new PanelColumnMetrics(new[] { 130f }), new PanelColumnMetrics(new[] { 280f }) };
        var stripFonts = new List<float>();
        foreach (var layout in new[] { new[] { 1920f, 1440f }, new[] { 2560f, 1440f }, new[] { 1280f, 720f } })
        {
            float windowWidth = layout[0], windowHeight = layout[1];
            var area = ResultsRegion.Resolve(true, windowHeight * 0.20f, true, windowHeight * 0.82f, windowHeight, 16f);
            Check(area.Fits && area.Top > 0f && area.Bottom < windowHeight, "a " + windowWidth + "x" + windowHeight + " layout leaves the success board a region inside the window");
            var fullBoard = PanelGeometry.Build(stripMetrics, 16f, 24, 28, PanelDensity.Normal, 1f, null);
            float shrink = fullBoard.FitFactor(windowWidth * 0.90f, area.Height);
            var fitted = PanelGeometry.Build(stripMetrics, 16f, 24, 28, PanelDensity.Normal, shrink, null);
            int fitFont = 16, fitRetries = 0;
            while (fitted.Height > area.Height && fitFont > 6 && fitRetries++ < 40)
            {
                fitFont--;
                fitted = PanelGeometry.Build(stripMetrics, 16f, fitFont, 28, PanelDensity.Normal, 1f, null);
            }
            Check(fitted.Height <= area.Height + 1e-3f, "the production grid fallback lowers the font size until all twenty-eight rows fit the strip");
            Check(fitted.Width <= windowWidth * 0.90f + 1e-3f, "a " + windowWidth + "x" + windowHeight + " board fits the width it was given");
            Check(fitted.FontPixels <= fullBoard.FontPixels + 1e-3f, "a " + windowWidth + "x" + windowHeight + " board never enlarges its glyphs to fit");
            stripFonts.Add(fitted.FontPixels);
        }
        Check(stripFonts[2] < stripFonts[0], "the 720p board draws smaller glyphs than the 1440p one");
        Check(stripFonts[1] >= stripFonts[0], "a wider window never leaves the glyphs smaller");

        // Four players, four kits, four sets of figures: after the board sorts them the third and fourth
        // columns still carry their own name, their own gear and their own numbers.
        var kits = new List<ImpactInput>();
        string[] kitNames = { "名字玩家甲", "Rook", "Zoë", "玩家丁" };
        for (int index = 0; index < 4; index++)
        {
            var kitPrimary = new PlayerCombatStats();
            kitPrimary.AddEnemyDamage(100 + index * 10); kitPrimary.AddAccuracy(40, 30 - index, 5 - index);
            var kitSecondary = new PlayerCombatStats();
            kitSecondary.AddEnemyDamage(50 + index); kitSecondary.AddFriendlyFireDamage(index);
            var kit = new ImpactInput
            {
                Name = kitNames[index], EnemyDamage = 100 + index * 10, ShotsFired = 40,
                Hits = 30 - index, WeakspotHits = 5 - index, Primary = kitPrimary, Secondary = kitSecondary
            };
            kit.PrimaryName = "主武器-" + index; kit.SecondaryName = "副武器-" + index;
            kit.MeleeName = "近战-" + index; kit.ToolName = "工具-" + index;
            kits.Add(kit);
        }
        var kitReport = ImpactScoring.Evaluate(kits, ImpactScoring.DefaultWeights, 1.0, 1.0);
        var kitBoard = ImpactBoard.Build(kitReport, true, true);
        Check(kitBoard.ColumnCount == 5 && kitBoard.Rows == 30, "a four-player split board is five columns and thirty rows");
        Check(kitBoard.Cell(8, 0)[0] == CombatStrings.ImpactRowLabels()[8], "the label column still labels every row");
        for (int index = 0; index < kitReport.Entries.Count; index++)
        {
            ImpactEntry entry = kitReport.Entries[index];
            int column = index + 1;
            Check(kitBoard.Cell(0, column)[0] == "#" + entry.Rank + " " + entry.Input.Name, "place " + (index + 1) + " carries its own player's name");
            Check(kitBoard.Cell(4, column)[0] == entry.Input.PrimaryName, "the primary weapon row follows the same player as the name");
            Check(kitBoard.Cell(5, column)[0] == entry.Input.SecondaryName, "the secondary weapon row follows the same player");
            Check(kitBoard.Cell(6, column)[0] == entry.Input.MeleeName, "the melee row follows the same player");
            Check(kitBoard.Cell(7, column)[0].StartsWith(entry.Input.ToolName), "the tool row follows the same player");
            Check(kitBoard.Cell(8, column)[0] == CombatStatsFormatter.FormatDamage(entry.Input.Primary.EnemyDamage), "the primary damage row carries that player's own figure");
            Check(kitBoard.Cell(10, column)[0] == "40", "the primary pellet count is that player's own");
            Check(kitBoard.Cell(11, column)[0] == CombatStatsFormatter.FormatDamage(entry.Input.Secondary.EnemyDamage), "the secondary damage row carries that player's own figure");
            Check(kitBoard.Tone(0, column) == ImpactTone.PlayerColor, "each name column keeps its own player colour");
        }
        Check(kitReport.Entries.Select(entry => entry.Name).Distinct().Count() == 4, "no player is merged into another's column");
        Check(kitReport.Entries.Zip(kitReport.Entries.Skip(1), (first, second) => first.Score >= second.Score).All(ordered => ordered), "the columns are ordered by score, best first");
        Check(kitReport.Entries[0].Name != kitNames[0], "the board's column order is the score order, not the input order");

        // Names are shown whole now; only the markup the game itself would interpret comes off.
        Check(CombatStatsFormatter.FullPlayerName("<#fa0>\"<#0ff>A1pha<#f00>\"") == "\"A1pha\"", "a full name keeps every visible character and drops only the markup");
        Check(CombatStatsFormatter.FullPlayerName("名字玩家测试完成六七八") == "名字玩家测试完成六七八", "a full name is no longer cut to six elements");
        Check(CombatStatsFormatter.FullPlayerName("a\nb\tc") == "abc", "a full name still cannot start a new table row");
        Check(CombatStatsFormatter.FullPlayerName("1 < 2") == "1 < 2", "a stray angle bracket in a full name stays literal");
        Check(CombatStatsFormatter.FullPlayerName("") == "", "an empty name is still safe");

        Check(CombatStrings.MetricLabel(ImpactMetric.ToolEfficiency) == "工具效率"
            || CombatStrings.MetricLabel(ImpactMetric.ToolEfficiency) == "Tool efficiency",
            "the scan metric is labelled tool efficiency");

        var scanInput = new ImpactInput { Name = "Scan", ToolName = "Bio Tracker" };
        scanInput.Counters.ScanUses = 2; scanInput.Counters.ScanFirstMarks = 5; scanInput.Counters.ScanMarks = 9;
        var scanReport = ImpactScoring.Evaluate(new[] { scanInput }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        ImpactEntry scanEntry = scanReport.Entries[0];
        Check(ImpactScoring.RawValue(scanEntry.Input, ImpactMetric.ToolEfficiency) == 5,
            "ToolEfficiency score still uses first scan marks");
        Check(ImpactBoard.MetricFields(scanEntry, ImpactMetric.ToolEfficiency, KillTier.Minor, false, true)[0] == "扫描 2 次 / 首次 5 只 / 累计 9 只",
            "an equipped scanner shows attempts then first marks in detail");
        scanEntry.Counters.ScanEstimated = true;
        Check(ImpactBoard.MetricFields(scanEntry, ImpactMetric.ToolEfficiency, KillTier.Minor, false, true)[0] == "~扫描 2 次 / 首次 5 只 / 累计 9 只",
            "estimated scan counters retain the marker with the unified format");
        scanEntry.Counters.ScanUses = 0; scanEntry.Counters.ScanFirstMarks = 0; scanEntry.Counters.ScanMarks = 0;
        Check(ImpactBoard.MetricFields(scanEntry, ImpactMetric.ToolEfficiency, KillTier.Minor, false, true)[0] == "扫描 —",
            "an equipped but unobserved scanner keeps an explicit placeholder");
        // The 总伤害 metric row carries the figure it is named for and its share of the squad total in
        // one cell, and prints the bare figure when no host measured a total to divide by.
        var shareEntry = new ImpactEntry(new ImpactInput { Name = "Share", EnemyDamage = 250 }, null, null, 50, 1, ImpactRating.Damage, (int)ImpactMetric.TotalDamage);
        string[] shareFields = ImpactBoard.MetricFields(shareEntry, ImpactMetric.TotalDamage, KillTier.Minor, false, true, squadDamage: 1000);
        Check(shareFields.Length == 2 && shareFields[0] == "250.0" && shareFields[1] == "25.0%",
            "the total-damage metric row carries its percentage share of the squad total as its second field, one figure per field");
        Check(ImpactBoard.MetricFields(shareEntry, ImpactMetric.TotalDamage, KillTier.Minor, false, true)[0] == "250.0",
            "a squad total nobody measured leaves the total-damage row as the bare figure");

        // A hit whose ray this build never saw still has to be scored, and the rate that follows has to say
        // that the pellet count was a guess. This is the EWC-weapon path: fired was counted, hits were not.
        var blind = new ShotSample { Blind = true };
        blind.BeginPellet();          // what the firing window does on the first contact of a ray-less shot
        blind.Contact(true);
        Check(blind.Fired == 1 && blind.Hits == 1 && blind.WeakspotHits == 1, "a window that opened a pellet on a ray-less shot still scores its contact");
        Check(blind.Blind, "the window remembers that the pellet count was an estimate");
        var rayed = new ShotSample();
        rayed.BeginPellet(); rayed.BeginPellet(); rayed.Contact(false); rayed.Contact(true);
        Check(rayed.Fired == 2 && rayed.Hits == 1 && rayed.WeakspotHits == 1, "two rays with two contacts on one pellet stay one hit, one of them a weakspot");
        var blindStats = new PlayerCombatStats();
        blindStats.AddAccuracy(37, 22, 5, true);
        Check(blindStats.AccuracyEstimated && CombatStatsFormatter.AccuracyText(blindStats).StartsWith("~"),
            "a blind pellet count yields a rate that is marked as an estimate rather than presented as measured");

        // not see is the placeholder, and no cell anywhere turns into NaN, Infinity or an invented zero.
        var unseen = new CombatPlayer(901);
        var unseenWeapon = unseen.Weapon(1, 601, "Assault Rifle");
        Check(CombatStatsFormatter.Value(unseenWeapon.Counters, PanelColumn.DamageAndShare, 0) == "— —", "a weapon nobody observed damage for reports a placeholder, not a zero");
        Check(CombatStatsFormatter.Value(unseenWeapon.Counters, PanelColumn.FriendlyFire) == "—", "friendly fire nobody observed is a placeholder");
        Check(CombatStatsFormatter.Value(unseenWeapon.Counters, PanelColumn.Accuracy) == "—", "an accuracy nobody observed is a placeholder");
        Check(CombatStatsFormatter.Value(unseenWeapon.Counters, PanelColumn.WeakspotAccuracy) == "—", "a weakspot rate nobody observed is a placeholder");
        Check(CombatStatsFormatter.Value(unseenWeapon.Counters, PanelColumn.Projectiles) == "—", "pellet counts nobody observed are one placeholder");
        string[] unseenText =
        {
            CombatStatsFormatter.Value(unseenWeapon.Counters, PanelColumn.DamageAndShare, 0),
            CombatStatsFormatter.Value(unseenWeapon.Counters, PanelColumn.FriendlyFire),
            CombatStatsFormatter.Value(unseenWeapon.Counters, PanelColumn.Accuracy),
            CombatStatsFormatter.Value(unseenWeapon.Counters, PanelColumn.WeakspotAccuracy),
            CombatStatsFormatter.Value(unseenWeapon.Counters, PanelColumn.Projectiles),
            CombatStatsFormatter.Summary(unseen.Total, null, true),
            CombatStatsFormatter.Summary(unseen.Total, 0, true)
        };
        foreach (string text in unseenText)
            Check(!text.Contains("NaN") && !text.Contains("Infinity") && !text.Contains("∞"), "no table or summary text fabricates NaN or Infinity");
        Check(CombatStatsFormatter.FormatShare(new PlayerCombatStats(), null) == "—", "an unknown team total leaves the share a placeholder, never a zero percent");
        Check(CombatStatsFormatter.FormatPercent(0, 0) == "—", "a zero denominator never prints infinity");
        Check(CombatStatsFormatter.FormatDamage(double.NaN) == "0.0" && CombatStatsFormatter.FormatDamage(double.PositiveInfinity) == "0.0", "a non-finite damage figure is never printed as NaN or infinity");
        var unseenInput = new ImpactInput { Name = "Blind" };
        var unseenReport = ImpactScoring.Evaluate(new[] { unseenInput }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        var unseenBoard = ImpactBoard.Build(unseenReport, true, true);
        for (int row = 0; row < unseenBoard.Rows; row++)
        {
            for (int column = 0; column < unseenBoard.ColumnCount; column++)
            {
                string cell = unseenBoard.Joined(row, column);
                Check(!cell.Contains("NaN") && !cell.Contains("Infinity") && !cell.Contains("∞"), "board cell " + row + "," + column + " is never NaN or Infinity");
            }
        }
        Check(unseenBoard.Joined(8, 1) == "—", "a weapon nobody fired prints one dash for its damage and share");
        Check(unseenBoard.Joined(10, 1) == "—", "a weapon nobody fired prints one dash for its pellet triple");
        var unseenDry = ImpactBoard.Build(unseenReport, false, true);
        Check(unseenDry.Joined(21, 1) == "—" && unseenDry.Joined(22, 1) == "—", "supply and unobserved non-scanner tool stay explicit without scanner wording");
        Check(unseenDry.Joined(23, 1) == "0", "a squad that never split up still shows a real zero");
        var aligned = PanelGeometry.Build(new[] { new PanelColumnMetrics(new[] { 150f }), new PanelColumnMetrics(new[] { 200f }) }, 0f, 20f, 26, PanelDensity.Normal, 1f, new[] { false, true });
        Check(near(aligned.FieldRect(0, 1, 0).Right, aligned.Cell(0, 1).Right), "settlement numeric cells hug their column right edge");
        Check(near(aligned.FieldRect(0, 1, 0).Right, aligned.FieldRect(25, 1, 0).Right), "settlement right edges remain identical over all rows");
        Check(!PanelAlignmentPolicy.IsRightAligned(true, aligned.IsRightAligned(0))
            && PanelAlignmentPolicy.IsRightAligned(true, aligned.IsRightAligned(1))
            && PanelAlignmentPolicy.IsRightAligned(false, false),
            "settlement labels stay left, every player value stays right, and the in-level layout remains unchanged");
        Check(!aligned.IsRightAligned(-1) && !aligned.IsRightAligned(99), "out-of-range columns are not right aligned");
        var estimatedScan = new ImpactInput { Name = "Scout", ToolName = "Bio Tracker" };
        estimatedScan.Counters.CreditScan(3, true);
        var estimatedBoard = ImpactBoard.Build(ImpactScoring.Evaluate(new[] { estimatedScan }, ImpactScoring.DefaultWeights, 1, 1), false, true);
        Check(estimatedBoard.Joined(22, 1) == "~扫描 1 次 / 首次 3 只 / 累计 3 只", "estimated scan counters show cumulative marks and keep the estimate marker");
        estimatedScan.Counters.Reset();
        Check(!estimatedScan.Counters.ScanEstimated && estimatedScan.Counters.ScanUses == 0 && estimatedScan.Counters.ScanFirstMarks == 0 && estimatedScan.Counters.ScanMarks == 0, "new expeditions clear both scan counts and estimate flags");
        Check(board.Cell(-1, 1).Length == 0 && board.Cell(0, 999).Length == 0 && board.Tone(999, 999) == ImpactTone.Neutral, "board bounds are safe");
        var scanState = new ScanSession();
        var scans = new ImpactCounters();
        Check(scanState.MarkEnemy(11, scans) && scans.ScanFirstMarks == 1 && scans.ScanUses == 1 && scans.ScanMarks == 1, "a fresh session's first reveal is a first mark and stands as its own sweep");
        Check(scanState.ObserveTagging(true, scans) && scans.ScanUses == 2 && scans.ScanMarks == 1, "a rising tag flag starts exactly one attempt without changing previous cumulative marks");
        Check(!scanState.ObserveTagging(true, scans) && scans.ScanUses == 2, "prefix postfix and frame polling share one attempt");
        Check(!scanState.MarkEnemy(11, scans) && scans.ScanFirstMarks == 1 && scans.ScanMarks == 2, "a repeat target counts once in a new sweep but not as a first mark");
        Check(!scanState.MarkEnemy(11, scans) && scans.ScanMarks == 2, "repeated samples of one target in the same sweep are deduplicated");
        Check(scanState.MarkEnemy(12, scans) && scans.ScanFirstMarks == 2 && scans.ScanUses == 2 && scans.ScanMarks == 3, "a second target increments cumulative marks only once within the sweep");
        scanState.ObserveTagging(false, scans);
        Check(scanState.MarkEnemy(13, scans) && scans.ScanUses == 3 && scans.ScanMarks == 4, "a reveal outside a tag still counts the sweep it evidences");
        Check(scanState.ObserveTagging(true, scans) && !scanState.MarkEnemy(11, scans) && !scanState.MarkEnemy(13, scans) && scans.ScanFirstMarks == 3 && scans.ScanUses == 4 && scans.ScanMarks == 6, "later sweeps count repeated targets cumulatively without recounting first marks");
        var botState = new ScanSession();
        var botScans = new ImpactCounters();
        Check(botState.BeginBotTag(10f, 2f, botScans) && botScans.ScanEstimated, "bot tagging is explicitly estimated");
        Check(!botState.BeginBotTag(10.1f, 2f, botScans) && !botState.BeginBotTag(11.9f, 2f, botScans) && botScans.ScanUses == 1, "continuous bot callbacks within tag duration share one denominator");
        Check(botState.MarkEnemy(20, botScans, true) && !botState.MarkEnemy(20, botScans, true) && botScans.ScanMarks == 1, "bot enemy identities are deduplicated within a sweep");
        Check(botState.BeginBotTag(12f, 2f, botScans) && botScans.ScanUses == 2 && !botState.MarkEnemy(20, botScans, true) && botScans.ScanMarks == 2, "a later bot sweep counts the same enemy cumulatively but only once");
        Check(!botState.BeginBotTag(float.NaN, 2f, botScans) && !botState.MarkEnemy(0, botScans, true), "invalid bot times and zero identities are discarded");
        var newScanState = new ScanSession();
        newScanState.ObserveTagging(true, new ImpactCounters());
        Check(newScanState.MarkEnemy(11, new ImpactCounters()), "a fresh expedition ledger has no previous enemy identities");

        // Each scanner carries its own reveal ledger: an enemy the squad's other bio tracker already
        // revealed is still a first mark for this tracker, which is what lets two human trackers both
        // count. A repeat by the same scanner is still a repeat.
        var firstScanner = new ScanSession();
        var firstCounters = new ImpactCounters();
        var secondScanner = new ScanSession();
        var secondCounters = new ImpactCounters();
        firstScanner.ObserveTagging(true, firstCounters);
        Check(firstScanner.MarkEnemy(41, firstCounters) && firstCounters.ScanFirstMarks == 1 && firstCounters.ScanUses >= 1, "the sweep that reveals an enemy first takes the credit");
        Check(!firstScanner.MarkEnemy(41, firstCounters) && firstCounters.ScanFirstMarks == 1, "a second sweep of the same enemy by the same scanner is a repeat, not a mark");
        secondScanner.ObserveTagging(true, secondCounters);
        Check(secondScanner.MarkEnemy(41, secondCounters) && secondCounters.ScanFirstMarks == 1 && secondCounters.ScanUses >= 1, "an enemy the squad's other tracker revealed is still a first mark for this scanner");
        Check(secondScanner.Observe(41, secondCounters) == ScanMark.Repeat, "the same scanner sweeping the same enemy again is a repeat");
        Check(secondScanner.MarkEnemy(42, secondCounters) && secondCounters.ScanFirstMarks == 2, "an enemy nobody had revealed is a first mark");
        Check(secondScanner.Observe(43, secondCounters) == ScanMark.First, "the tracker can tell a repeat from a first mark");
        secondScanner.ObserveTagging(false, secondCounters);
        Check(secondScanner.Observe(44, secondCounters) == ScanMark.First, "a reveal outside a tag is its own proof of a sweep, not a rejection");
        var sharedReveals = new ScanReveals();
        sharedReveals.Reveal(41); sharedReveals.Reveal(42); sharedReveals.Reveal(43);
        Check(sharedReveals.Count == 3, "the expedition diagnostic ledger remembers exactly the enemies the squad revealed");
        sharedReveals.Clear();
        Check(sharedReveals.Count == 0 && new ScanSession(sharedReveals).Observe(41, new ImpactCounters(), true) == ScanMark.First, "a cleared ledger is a fresh expedition");
        if (args.Length == 2 && args[0] == "--preview") BoardPreview.Write(args[1], BoardPreview.Sample());
        Check(ResultsHeading.Resolve(true, true, 1f, false, 0, 1440, 0, out var fallbackTop, out var fallbackAlpha) && near(fallbackTop, 505.6f) && fallbackAlpha == 1f, "a visible title with unavailable measurement still produces a visible board");
        Check(ResultsHeading.Resolve(true, true, 1f, true, 450f, 1440, 0, out var measuredTop, out _) && measuredTop == 466f, "a readable title uses its real lower edge");
        Check(!ResultsHeading.Resolve(true, true, 0f, false, 0, 1440, 0, out _, out _), "a known transparent title still delays the board");
        Check(!ResultsHeading.Resolve(true, false, 1f, false, 0, 1440, 0, out _, out _), "an inactive title delays the board");
        Check(ResultsHeading.Resolve(false, false, 0f, false, 0, 1440, 0, out _, out var missingAlpha) && missingAlpha == 1f, "an unavailable title component degrades instead of permanently hiding statistics");
        Check(ResultsHeading.Resolve(true, true, float.NaN, false, 0, 1440, 0, out _, out var unknownAlpha) && unknownAlpha == 1f, "unreadable alpha does not fabricate invisibility");
        Check(ResultsHeading.Resolve(true, true, 1f, true, float.NaN, 1440, 0, out var invalidTop, out _) && near(invalidTop, fallbackTop), "invalid measured geometry uses the same fallback");
        Check(ResultsHeading.Resolve(true, true, 1f, false, 0, 1440, -500, out var negativeTop, out _) && near(negativeTop, fallbackTop), "negative offsets cannot lift the fallback over the captured title");
        Check(!ResultsHeading.Resolve(true, true, 1f, false, 0, 0, 0, out _, out _), "an invalid viewport rejects placement");
        Check(ResultsHeading.Resolve(true, true, 0.5f, false, 0, 1440, 0, out _, out var fadingAlpha) && fadingAlpha == 0.5f, "the fallback board still follows known heading fade");
        var fullSupply = new ImpactCounters();
        fullSupply.RecordSupply(0.2, 0.2); fullSupply.RecordSupply(0.35, 0.35);
        Check(fullSupply.SupplyUses == 2 && fullSupply.SupplyEfficiency == 1, "two non-overflowing ammo uses average to one, not two percent");
        var fullSupplier = new ImpactInput { Name = "Supply", Counters = fullSupply };
        Check(ImpactBoard.Build(ImpactScoring.Evaluate(new[] { fullSupplier }, null, 1, 1), true, false).Joined(25, 1) == "100.0%", "full utilization displays 100 percent on the split settlement board after the kill rows");
        var equalUses = new ImpactCounters(); equalUses.RecordSupply(100, 100); equalUses.RecordSupply(0, 1);
        Check(equalUses.SupplyUses == 2 && equalUses.SupplyEfficiency == 0.5, "different-sized uses have equal weight and complete overflow counts as zero");
        equalUses.RecordSupply(-1, 1); equalUses.RecordSupply(1, double.PositiveInfinity);
        Check(equalUses.SupplyUses == 2, "invalid supply samples cannot change the average");
        var mixedUses = new ImpactCounters(); mixedUses.RecordSupply(0.4, 0.4); mixedUses.RecordSupply(10, 20); mixedUses.RecordSupply(0.24, 0.3);
        Check(Math.Abs(mixedUses.SupplyEfficiency - (1 + 0.5 + 0.8) / 3) < 1e-9, "ammo health and tool samples are averaged only after each is dimensionless");
        var overSupply = new ImpactCounters(); overSupply.RecordSupply(30, 20);
        Check(overSupply.SupplyEfficiency == 1, "supply utilization is clamped to one");
        equalUses.Reset(); Check(equalUses.SupplyUses == 0 && equalUses.SupplyEfficiencySum == 0, "new expeditions clear supply averages");
        var zeroSquad = ImpactScoring.Evaluate(new[] { new ImpactInput { Name = "Z1" }, new ImpactInput { Name = "Z2" }, new ImpactInput { Name = "Z3" }, new ImpactInput { Name = "Z4" } }, null, 1, 1);
        Check(zeroSquad.Entries.All(e => e.Rating == ImpactRating.TeamPillar && e.RatingMetric == -1), "all-zero data cannot manufacture an outstanding top");
        Check(zeroSquad.Entries.Select(e => e.Rating).Distinct().Count() == 1, "the zero-score title bypasses uniqueness");
        Check(CombatStrings.Title(ImpactRating.ZeroScore) == "战地游客", "the zero-score title is the approved Chinese wording");
        Check(ImpactScoring.IsDisplayedZero(0) && ImpactScoring.IsDisplayedZero(0.04) && !ImpactScoring.IsDisplayedZero(0.06) && !ImpactScoring.IsDisplayedZero(double.NaN), "zero-score classification follows the exact displayed one-decimal score");
        var leaderAndZeros = ImpactScoring.Evaluate(new[] { new ImpactInput { Name = "Leader", EnemyDamage = 100 }, new ImpactInput { Name = "Zero1" }, new ImpactInput { Name = "Zero2" } }, null, 1, 1);
        Check(leaderAndZeros.Entries[0].Rating != ImpactRating.TeamPillar && leaderAndZeros.Entries.Skip(1).All(e => e.Rating == ImpactRating.TeamPillar), "only a genuine leader earns an outstanding title");
        var zeroBoard = ImpactBoard.Build(zeroSquad, true, false);
        Check(zeroBoard.Joined(1, 1) == "0.0" && zeroBoard.Joined(2, 1) == "平平无奇" && zeroBoard.Joined(2, 4) == "平平无奇", "the board renders neutral titles beside zero scores with no top data");
        Check(ResultsAppearance.BackgroundOpacity == 0.5f && ResultsAppearance.RuleOpacity < 0.22f && ResultsAppearance.RowBandOpacity < 0.035f, "settlement background and rules use the approved lighter transparency");
        // The in-level plate: the number the player reads as "the box my numbers sit on". It was a constant
        // (0.48) and read as a black box over the level, so it is a setting now, with a lighter default.
        Check(PanelAppearance.InLevelOpacity < 0.48f && PanelAppearance.InLevelOpacity > 0f,
            "the in-level plate defaults lighter than the 0.48 that read as a black box");
        Check(PanelAppearance.Plate(0.9f, false) == 0.9f && PanelAppearance.Plate(0f, false) == 0f,
            "the in-level plate follows the setting");
        Check(PanelAppearance.Plate(-1f, false) == 0f && PanelAppearance.Plate(2.5f, false) == 1f,
            "a value outside the slider's range is clamped rather than drawn");
        Check(PanelAppearance.Plate(float.NaN, false) == PanelAppearance.InLevelOpacity,
            "an unreadable entry falls back to the published plate instead of leaving the table on nothing");
        Check(PanelAppearance.Plate(0.05f, false) == 0.05f && PanelAppearance.Plate(0.05f, true) == ResultsAppearance.BackgroundOpacity,
            "the settlement board keeps its own approved plate whatever the in-level key says");
        Check(near(PanelAppearance.Frame(0.48f), 0.22f, 0.001f),
            "the frame keeps the ratio it had before the plate became a setting");
        Check(PanelAppearance.Frame(0.28f) < PanelAppearance.Frame(0.48f) && PanelAppearance.Frame(-1f) == 0f,
            "a lighter plate gets a proportionally lighter frame, and an out-of-range plate never inverts it");
        CombatStrings.Language = "en";
        Check(CombatStrings.Title(ImpactRating.ZeroScore) == "Battlefield tourist" && CombatStrings.KillRowLabel(KillTier.Boss) == "Boss kills", "new rating and full kill labels are localized");
        CombatStrings.Language = "zh";
        var nativeSupply = new SupplyUse(); nativeSupply.AddChange(20, 40, 20); nativeSupply.AddChange(30, 50, 20);
        var nativeCounters = new ImpactCounters();
        Check(nativeSupply.Record(nativeCounters) && nativeCounters.SupplyUses == 1 && nativeCounters.SupplyEfficiency == 1, "primary and secondary native changes form one non-overflowing pack use");
        Check(!nativeSupply.Record(nativeCounters) && nativeCounters.SupplyUses == 1, "one supply sample cannot be committed twice");
        var halfSupply = new SupplyUse(); halfSupply.AddChange(80, 100, 20); halfSupply.AddChange(100, 100, 20);
        var halfCounters = new ImpactCounters();
        Check(halfSupply.Record(halfCounters) && halfCounters.SupplyEfficiency == 0.5, "overflow in one weapon reduces utilization of the same ammo pack");
        var emptySupply = new SupplyUse(); Check(!emptySupply.Record(new ImpactCounters()), "a use without native measurements never fabricates a result");
        var brokenSupply = new SupplyUse(); brokenSupply.AddChange(20, 40, 20); brokenSupply.AddChange(double.NaN, 1, 20);
        var brokenCounters = new ImpactCounters(); Check(!brokenSupply.Record(brokenCounters) && brokenCounters.SupplyUses == 0, "one missing channel invalidates the whole use rather than overstating efficiency");
        var negativeSupply = new SupplyUse(); negativeSupply.AddChange(40, 20, 20);
        Check(!negativeSupply.Record(new ImpactCounters()), "unexplained negative storage changes are rejected");
        // ---- field-wise merge: how one row is assembled out of two machines' figures -----------------
        // The host applied every blow, so its damage is the damage; a client's own rate is exact, so it
        // survives a host figure that is only an estimate; and an absent host field never erases a
        // measurement the receiving machine made itself. That last rule is the reported symptom: an
        // unobserved snapshot used to be applied verbatim and blanked the row.
        Check(CombatMerge.HostSuppliesDamage(true) && !CombatMerge.HostSuppliesDamage(false), "only an observed host damage figure displaces the local one");
        Check(CombatMerge.MergeAmount(500, true, 800, true) == 800, "an observed host figure wins over the local one");
        Check(CombatMerge.MergeAmount(500, true, 0, false) == 500, "an unobserved host field leaves the local figure alone");
        Check(CombatMerge.MergeCount(3, 0) == 3 && CombatMerge.MergeCount(3, 7) == 7, "an observed host count wins, an absent one does not erase");
        Check(!CombatMerge.HostSuppliesAccuracy(true, false, true, true), "a locally exact rate is never replaced by the host's estimate");
        Check(CombatMerge.HostSuppliesAccuracy(true, true, true, false), "a locally estimated rate is replaced by the host's exact one");
        Check(!CombatMerge.HostSuppliesAccuracy(false, false, false, false), "neither side observed means nothing is applied");

        // The host side: it applied every blow, so its own damage stands, while the client's own exact rate
        // is the one figure the host cannot compute for itself.
        var clientRowOnHost = new CombatPlayer(0x66UL);
        clientRowOnHost.Total.AddEnemyDamage(700);
        clientRowOnHost.Total.AddAccuracy(40, 0, 0, true);
        clientRowOnHost.Impact.ScanUses = 2; clientRowOnHost.Impact.ScanFirstMarks = 3; clientRowOnHost.Impact.ScanMarks = 9;
        var clientOwn = new CombatPlayer(0x66UL);
        clientOwn.Total.AddAccuracy(42, 30, 8);
        SyncSnapshot exactSnap = SyncProtocol.Capture(clientOwn, 0, 1, 2, 3, 0, false);
        Check(exactSnap.Has(SyncFlags.AccuracyObserved) && !exactSnap.Has(SyncFlags.AccuracyEstimated), "a client's own snapshot carries an exact rate");
        Check(!exactSnap.Has(SyncFlags.EnemyDamageObserved), "a client's own snapshot never fabricates the damage it cannot know");
        // Apply through a projection: the measured row is never written, so re-applying the same
        // snapshot cannot stack a second copy on it.
        var hostProjection = CombatMerge.Project(clientRowOnHost);
        CombatMerge.ApplyRemote(hostProjection, exactSnap, 0, 1, 2, 3, 0x99UL);
        Check(Math.Abs(hostProjection.Total.EnemyDamage - 700) < 0.01, "an unobserved client damage row never erases the host's own measurement");
        Check(hostProjection.Total.ShotsFired == 42 && hostProjection.Total.Hits == 30 && hostProjection.Total.WeakspotHits == 8, "the client's exact rate replaces the host's estimated one");
        Check(!hostProjection.Total.AccuracyEstimated, "the exact rate is no longer marked as an estimate");
        Check(hostProjection.Impact.ScanFirstMarks == 3 && hostProjection.Impact.ScanMarks == 9, "a client's first and cumulative scan counts survive a snapshot that carries none");
        CombatMerge.ApplyRemote(hostProjection, exactSnap, 0, 1, 2, 3, 0x99UL);
        Check(hostProjection.Total.ShotsFired == 42 && Math.Abs(hostProjection.Total.EnemyDamage - 700) < 0.01
            && hostProjection.Impact.ScanFirstMarks == 3 && hostProjection.Impact.ScanMarks == 9,
            "applying the same snapshot twice never accumulates a cumulative echo");
        // A locally exact rate is this machine's own count and a peer can only ever estimate it, so it
        // survives the projection whatever the snapshot says.
        var exactLocal = new CombatPlayer(0xA1UL);
        exactLocal.Total.AddAccuracy(50, 40, 10);
        var estimateFromPeer = new CombatPlayer(0xA1UL);
        estimateFromPeer.Total.AddAccuracy(60, 45, 12, true);
        var exactProjection = CombatMerge.Project(exactLocal);
        CombatMerge.ApplyRemote(exactProjection, SyncProtocol.Capture(estimateFromPeer, 0, 1, 2, 3, 0, false), 0, 1, 2, 3, 0xA1UL);
        Check(exactProjection.Total.ShotsFired == 50 && exactProjection.Total.Hits == 40 && !exactProjection.Total.AccuracyEstimated, "a locally exact rate survives a peer's estimated one when the row is projected");

        // The client side: a local estimate is displaced by the host's measured figure, and a host snapshot
        // that carries nothing leaves the local row exactly as it was.
        var peerOnClient = new CombatPlayer(0x77UL);
        peerOnClient.Total.AddEnemyDamage(300, true);
        SyncSnapshot hostRow = SyncProtocol.Capture(host, 0, 1, 2, 3, 4321.0, true);
        var clientProjection = CombatMerge.Project(peerOnClient);
        CombatMerge.ApplyRemote(clientProjection, hostRow, 0, 1, 2, 3, 0x77UL);
        Check(Math.Abs(clientProjection.Total.EnemyDamage - 1234.5) < 0.01, "the host's observed damage replaces a client's local estimate");
        Check(clientProjection.Total.ShotsFired == 100 && clientProjection.Total.Hits == 60, "the host's row fills a client's empty rate");
        CombatMerge.ApplyRemote(clientProjection, hostRow, 0, 1, 2, 3, 0x77UL);
        Check(Math.Abs(clientProjection.Total.EnemyDamage - 1234.5) < 0.01, "re-applying the same host row does not add its damage twice");
        var untouched = new CombatPlayer(0x88UL);
        untouched.Total.AddEnemyDamage(50);
        SyncSnapshot emptyRow = SyncProtocol.Capture(new CombatPlayer(0x88UL), 0, 1, 2, 3, 0, false);
        var untouchedProjection = CombatMerge.Project(untouched);
        CombatMerge.ApplyRemote(untouchedProjection, emptyRow, 0, 1, 2, 3, 0x88UL);
        Check(Math.Abs(untouchedProjection.Total.EnemyDamage - 50) < 0.01 && untouchedProjection.Total.EnemyDamageObserved, "an all-unobserved snapshot leaves a local observation standing");

        // ---- the scoring file: defaults, validation, last-good, atomicity ----------------------------
        ScoringSettings scoringDefaults = ScoringSettings.Defaults();
        Check(scoringDefaults.Weights.Count == 13 && Math.Abs(scoringDefaults.Weights["Stagger"] - 1.4) < 1e-9 && Math.Abs(scoringDefaults.Weights["Revives"] - 0.28) < 1e-9,
            "the scoring file's defaults are the recalibrated weights");
        Check(scoringDefaults.EffectiveWeights().SequenceEqual(ImpactScoring.DefaultWeights), "the scoring file's defaults and the code's defaults are the same list");
        Check(Math.Abs(scoringDefaults.FirepowerBonus - 1.15) < 1e-9 && Math.Abs(scoringDefaults.UtilityBonus - 1.0) < 1e-9, "the scoring file's default role bonuses are the modest ones");
        Check(scoringDefaults.EnabledMetrics == null, "every metric is enabled when the list is absent");
        ScoringSettings onlyStagger = ScoringSettings.Parse(scoringDefaults.ToJson());
        onlyStagger.EnabledMetrics = new[] { "Stagger" };
        double[] onlyWeights = onlyStagger.EffectiveWeights();
        Check(Math.Abs(onlyWeights[(int)ImpactMetric.Stagger] - 1.4) < 1e-9 && onlyWeights[(int)ImpactMetric.TotalDamage] == 0, "a disabled metric is worth zero rather than removed");
        Check(Math.Abs(ScoringSettings.Parse(scoringDefaults.ToJson()).Weights["Kills"] - 0.7) < 1e-9, "the scoring file round trips through JSON");
        Check(ScoringSettings.Parse("{}").Weights["Stagger"] == 1.4, "a sparse scoring file fills its missing fields from the defaults");
        Check(ScoringSettings.Defaults().IsSupport("Glue Gun") && ScoringSettings.Defaults().IsSupport("Modded Freezer"), "a modded non-damaging tool counts as support by default");
        Check(!ScoringSettings.Defaults().IsSupport("Sentry Turret") && !ScoringSettings.Defaults().IsSupport("Tripmine"), "a sentry and a tripmine are excluded from support");
        foreach (string broken in new[] { "not json", "{\"FirepowerBonus\":9}", "{\"Weights\":{\"TotalDamage\":1}}", "{\"RescueFloor\":-1}", "{\"EnabledMetrics\":[\"Nope\"]}" })
        {
            bool rejected = false;
            try { ScoringSettings.Parse(broken); } catch (Exception) { rejected = true; }
            Check(rejected, "an invalid scoring file is rejected rather than half-applied");
        }
        ImpactScoring.RescueFloor = 1;
        var singleRescue = new ImpactCounters(); singleRescue.CreditRevive();
        Check(ImpactScoring.RawValue(new ImpactInput { Counters = singleRescue }, ImpactMetric.Revives) == 1, "the rescue floor is configurable and a single rescue scores at floor one");
        ImpactScoring.ResetThresholds();
        Check(ImpactScoring.RawValue(new ImpactInput { Counters = singleRescue }, ImpactMetric.Revives) == 0, "the rescue floor returns to two");
        ImpactScoring.AccuracyConfidence = 4;
        var shortSample = new ImpactInput { ShotsFired = 2, Hits = 2 };
        Check(Math.Abs(ImpactScoring.AccuracyValue(shortSample) - 0.5) < 1e-9, "the accuracy confidence sample is configurable");
        ImpactScoring.ResetThresholds();
        Check(Math.Abs(ImpactScoring.AccuracyValue(shortSample) - 0.1) < 1e-9, "the accuracy confidence sample returns to twenty");

        string scoringDir = Path.Combine(Path.GetTempPath(), "gtfocp-smoke-" + Guid.NewGuid().ToString("N"));
        string scoringPath = ScoringFiles.Resolve(scoringDir);
        Check(scoringPath.EndsWith(Path.Combine(ScoringFiles.DirectoryName, ScoringFiles.FileName)), "the scoring file lives in its own sub folder beside the plugin");
        ScoringSettings createdSettings = ScoringFiles.Load(scoringPath, out bool created, out string createError);
        Check(created && createError == null && createdSettings != null && File.Exists(scoringPath), "a missing scoring file is created once from the defaults");
        Check(!File.Exists(scoringPath + ".tmp"), "the atomic write leaves no temporary file behind");
        ScoringSettings reopened = ScoringFiles.Load(scoringPath, out bool createdAgain, out string reopenError);
        Check(!createdAgain && reopenError == null && reopened != null, "an existing scoring file is read, never recreated");
        string userEdit = ScoringSettings.Defaults().ToJson().Replace("\"Stagger\": 1.4", "\"Stagger\": 5");
        File.WriteAllText(scoringPath, userEdit);
        ScoringSettings fromUser = ScoringFiles.Load(scoringPath, out _, out string userError);
        Check(userError == null && fromUser != null && Math.Abs(fromUser.Weights["Stagger"] - 5) < 1e-9, "a user's edited weight is read back");
        string invalidText = "{ this is not json";
        File.WriteAllText(scoringPath, invalidText);
        ScoringSettings lastGood = ScoringFiles.Load(scoringPath, out _, out string invalidError);
        Check(lastGood == null && !string.IsNullOrEmpty(invalidError), "an invalid scoring file reports an error instead of yielding half settings");
        Check(File.ReadAllText(scoringPath) == invalidText, "an invalid scoring file is never overwritten behind the user's back");
        Directory.Delete(scoringDir, true);
        // ---- the wire: framing, the declared length, staleness, and the bounded hello -----------------
        // The receive prefix is handed the packet's declared size and a backing array that may be larger;
        // every bound has to come from the size, or a packet gets read past its own end.
        Check(SquadPackets.HeaderBytes == 16 && SquadPackets.HelloBytes == 24 && SquadPackets.SnapshotBytes == SquadPackets.HeaderBytes + SyncProtocol.SnapshotSize,
            "the envelope is key, magic, kind, protocol and the run counter, then the body");
        Check(SquadPackets.Key == 0xFFFE && SquadPackets.Key > 65533, "the packet key is outside the range the game's own replicators use");
        Check(SyncProtocol.Version == 8, "the protocol is v8 for the Scout damage that travels beside every earlier figure");
        byte[] helloBytes = SquadPackets.Hello(0x1234UL, 7u, SyncProtocol.Version);
        SquadPacket helloPacket = SquadPackets.Parse(helloBytes, helloBytes.Length);
        Check(helloPacket.Kind == SquadPacketKind.Hello && helloPacket.Sender == 0x1234UL && helloPacket.Generation == 7u && helloPacket.IsOurs,
            "a hello decodes to the sender and the run it was sent for");
        var padded = new byte[helloBytes.Length + 64];
        Array.Copy(helloBytes, padded, helloBytes.Length);
        for (int index = helloBytes.Length; index < padded.Length; index++) padded[index] = 0xAB;
        SquadPacket paddedPacket = SquadPackets.Parse(padded, helloBytes.Length);
        Check(paddedPacket.Kind == SquadPacketKind.Hello && paddedPacket.Sender == 0x1234UL, "the declared length bounds the read, not the backing array");
        Check(SquadPackets.Parse(padded, helloBytes.Length - 4).Kind == SquadPacketKind.Malformed, "a packet that stops inside the header is ours, malformed, and consumed");
        Check(SquadPackets.Parse(helloBytes, helloBytes.Length + 8).Kind == SquadPacketKind.Foreign, "a packet that declares more bytes than it carries is left to the game");
        Check(SquadPackets.Parse(null, 0).Kind == SquadPacketKind.Foreign, "a null packet is not ours");
        byte[] foreignKey = (byte[])helloBytes.Clone(); foreignKey[0] = 0x01;
        Check(SquadPackets.Parse(foreignKey, foreignKey.Length).Kind == SquadPacketKind.Foreign, "another key is not ours and is passed to the game");
        byte[] foreignMagic = (byte[])helloBytes.Clone(); foreignMagic[2] = (byte)'X';
        Check(SquadPackets.Parse(foreignMagic, foreignMagic.Length).Kind == SquadPacketKind.Foreign, "another mod's magic is not ours either");
        byte[] olderVersion = SquadPackets.Hello(1UL, 1u, SyncProtocol.Version - 1);
        SquadPacket stalePacket = SquadPackets.Parse(olderVersion, olderVersion.Length);
        Check(stalePacket.Kind == SquadPacketKind.VersionMismatch && stalePacket.IsOurs, "a build speaking an older protocol is recognised as ours and dropped, never handed to the game");
        byte[] snapshotWire = SquadPackets.Snapshot(hostRow, 12u, SyncProtocol.Version);
        SquadPacket decodedWire = SquadPackets.Parse(snapshotWire, snapshotWire.Length);
        Check(decodedWire.Kind == SquadPacketKind.Snapshot && decodedWire.Snapshot.Owner == 0xABCUL && Math.Abs(decodedWire.Snapshot.EnemyDamage - 1234.5f) < 0.01f,
            "a snapshot decodes to the row the sender sent");
        Check(decodedWire.Generation == 12u, "a snapshot carries the run it belongs to");
        Check(SquadPackets.Parse(snapshotWire, snapshotWire.Length - 1).Kind == SquadPacketKind.Malformed, "a truncated snapshot is consumed rather than half-read");

        // The hello: sent at once, retried on a cadence until the peer is heard from, re-sent after a
        // silence, and bounded so a peer that does not run this mod is never spammed.
        var handshake = new HandshakePolicy();
        Check(handshake.ShouldGreet(5UL, 0f), "the first hello goes out at once");
        Check(!handshake.ShouldGreet(5UL, 0f), "a second hello in the same instant is not sent");
        Check(handshake.ShouldGreet(5UL, HandshakePolicy.RetrySeconds), "the hello is retried on the cadence");
        // The quick cadence is a burst, not a budget. A fixed number of attempts wrote a peer off for the
        // whole run - and the ordinary case for a peer whose machine is still on the loading screen during
        // those ten seconds is exactly the failure it produced: two machines that exchange nothing, which
        // looks from either screen like a build that cannot share at all.
        var keepAlive = new HandshakePolicy();
        var helloAt = new List<float>();
        for (float at = 0f; at <= 60f; at += 0.25f) if (keepAlive.ShouldGreet(9UL, at)) helloAt.Add(at);
        // The burst covers the first ten seconds - five retries at the quick cadence, the sixth greeting
        // landing at the ten-second mark - and every greeting after that is on the keep-alive cadence, so a
        // peer whose machine was still loading, or whose greetings were dropped, is still found later in the
        // same run instead of being written off until the next lobby.
        Check(helloAt.Count == 11 && near(helloAt[5], 10f) && near(helloAt[6], 20f) && near(helloAt[10], 60f),
            "a peer that never answers is greeted over the first ten seconds and then on the keep-alive cadence, and is never written off for the run");
        Check(near(helloAt[7] - helloAt[6], HandshakePolicy.KeepAliveSeconds)
            && HandshakePolicy.KeepAliveSeconds > HandshakePolicy.RetrySeconds,
            "and the cadence it settles on is the slow one, so a peer that does not run this mod is still not spammed");
        // Which rule gates the relay: a peer that once proved it runs this mod keeps being sent rows even
        // after its own uplink has been quiet for longer than the silence window. It can still receive, and
        // stopping the relay there was a second way this build could go silent for a whole run.
        var relay = new HandshakePolicy();
        relay.ShouldGreet(4UL, 0f);
        relay.Heard(4UL, 100f);
        relay.ShouldGreet(4UL, 100f + HandshakePolicy.SilenceSeconds);
        Check(relay.EverAnswered(4UL) && !relay.IsAnswered(4UL),
            "a peer that answered and then went quiet is greeted afresh but is still one that runs this mod, so rows keep going to it");
        relay.Reset();
        Check(!relay.EverAnswered(4UL), "and a lobby or host transition forgets that it ever answered");
        var answered = new HandshakePolicy();
        Check(answered.ShouldGreet(3UL, 0f), "a fresh peer is greeted");
        answered.MarkAnswered(3UL);
        answered.Heard(3UL, 100f);
        Check(answered.IsAnswered(3UL) && !answered.ShouldGreet(3UL, 100f), "a peer that answered is not greeted again while it is talking");
        Check(answered.ShouldGreet(3UL, 100f + HandshakePolicy.SilenceSeconds), "a peer that answered and then went quiet is greeted afresh, which is how a restart is picked up again");
        answered.Reset();
        Check(!answered.IsAnswered(3UL) && answered.ShouldGreet(3UL, 200f), "a lobby or host transition makes every peer fresh again");
        var answers = new HandshakePolicy();
        Check(answers.ShouldAnswer(7UL, 0f), "the first hello of a peer is answered");
        Check(!answers.ShouldAnswer(7UL, 0f), "two answers in the same instant would be a greeting loop");
        Check(answers.ShouldAnswer(7UL, HandshakePolicy.AnswerInterval), "a repeated hello is answered again, so one lost answer no longer ends the exchange");
        answers.Forget(7UL);
        Check(!answers.IsAnswered(7UL) && answers.GenerationKnown(7UL) == false, "a peer that left the squad is forgotten whole");

        // The run counter: the first packet adopts a sender's run, a newer one supersedes it, and a straggler
        // from the run before is refused - but only until the sender insists, which is the case the old
        // The run counter: the first packet adopts a sender's run, a newer one supersedes it, and a straggler
        // from the run before is refused - but only until the sender insists, which is the case the old build
        // had no answer for at all: the test log ends on exactly that refusal and nothing followed it.
        var runs = new HandshakePolicy();
        Check(runs.AcceptsFrom(11UL, 4u, 0f), "the first packet from a sender is accepted and adopts its run");
        Check(runs.AcceptsFrom(11UL, 4u, 1f), "another packet from the same run is accepted");
        Check(runs.AcceptsFrom(11UL, 5u, 2f), "a newer run supersedes the old one");
        bool healed = true;
        Check(!runs.AcceptsFrom(11UL, 4u, 3f), "a straggler from the previous run is refused");
        Check(!runs.AcceptsFrom(11UL, 4u, 4f), "and refused again while it stays a straggler");
        Check(runs.AcceptsFrom(11UL, 4u, 5f, out healed) && healed,
            "a sender that keeps insisting on an older run is believed on the third packet: it restarted, and its counter really did begin again");
        Check(runs.GenerationOf(11UL) == 4u, "and the run it insists on is the one the policy now holds");
        Check(runs.AcceptsFrom(11UL, 6u, 6f), "a newer run is still adopted at once");
        // The same recovery by time rather than by count: a straggler that keeps arriving past the grace
        // window is believed even if few of its packets made it through, which is what a lossy link does.
        var restarted = new HandshakePolicy();
        restarted.AcceptsFrom(12UL, 9u, 0f);
        Check(!restarted.AcceptsFrom(12UL, 1u, 0.5f), "a straggler from a restarted peer is refused while it is still fresh");
        Check(restarted.DescribeStale(12UL).Contains("generation 1") && restarted.DescribeStale(12UL).Contains("saw 9"),
            "the refusal reason carries both run counters for the diagnostic line");
        bool restartedHealed = false;
        Check(restarted.AcceptsFrom(12UL, 1u, HandshakePolicy.StaleGraceSeconds + 1f, out restartedHealed) && restartedHealed,
            "and is adopted once refusing it has gone on past the grace window, so a slow stream of stale packets still recovers");
        Check(!runs.AcceptsFrom(0UL, 5u, 7f), "a packet that names no sender is refused");
        runs.Reset();
        Check(runs.PeerCount == 0 && runs.GenerationKnown(11UL) == false, "resetting the policy forgets every run it had seen");
        // ---- the one safe strip both result screens use ------------------------------------------------
        // The failure board and the success board are placed by the same rule: below the page's own message
        // and above the button the player has to press. Neither may ever be covered.
        Check(ResultsPlacement.TryFitBetween(1920, 1080, 300, 900, 800, 500, 0, out LayoutRect stripRect) && near(stripRect.Y, 300f) && near(stripRect.Bottom, 800f),
            "a table that fits the strip between the message and the buttons is placed at its top");
        Check(!ResultsPlacement.TryFitBetween(1920, 1080, 300, 900, 800, 601, 0, out _), "a table taller than the strip is refused rather than drawn over a button");
        Check(!ResultsPlacement.TryFitBetween(1920, 1080, 900, 300, 800, 100, 0, out _), "an inverted strip has no room at all");
        Check(ResultsPlacement.TryFitBetween(1920, 1080, 300, 900, 800, 100, -500, out LayoutRect clamped) && clamped.X >= 0f && clamped.Right <= 1920f,
            "an offset that would push the strip table off screen is clamped");
        Check(!ResultsPlacement.TryFitBetween(1920, 1080, float.NaN, 900, 800, 100, 0, out _), "a strip with no measured top is refused rather than guessed");
        Check(!ResultsPlacement.TryFitBetween(1920, 1080, 300, 900, 2000, 100, 0, out _), "a table wider than the window is refused");
        Check(!ResultsPlacement.TryFitBetween(1920, 1080, 300, 900, 0, 100, 0, out _), "a table with no size is refused");

        // The board that was dropped in game for half a pixel: the strip between the page's message and its
        // buttons measured 518 px, the table came back 518.5 px tall and the draw path refused it. The draw
        // path now allows two pixels of slack, because a button's own renderer bounds move that much while it
        // is hovered, and the board is still placed against the top of the strip.
        Check(!ResultsPlacement.TryFitBetween(1920f, 1440f, 483f, 1001f, 832f, 518.5f, 0f, out _, 0f),
            "a board half a pixel taller than the strip is still refused when the caller allows no slack");
        Check(ResultsPlacement.TryFitBetween(1920f, 1440f, 483f, 1001f, 832f, 518.5f, 0f, out LayoutRect slacked, 2f) && near(slacked.Y, 483f),
            "the same board is placed against the top of the strip once the draw path allows its two pixels of slack");
        Check(ResultsPlacement.TryFitBetween(1920f, 1440f, 483f, 1001f, 832f, 518f, 0f, out LayoutRect exactBoard, 0f) && near(exactBoard.Y, 483f),
            "a board exactly as tall as the strip is placed with no slack at all");
        // The fit factor is a ratio, so the rebuild it drives has to come back inside the height it was
        // measured against: a grid a fraction of a pixel over the strip is what the draw path used to drop.
        var fitMetrics = new[]
        {
            new PanelColumnMetrics(new[] { 96f }),
            new PanelColumnMetrics(new[] { 120f, 34f, 34f }),
            new PanelColumnMetrics(new[] { 120f, 34f, 34f }),
            new PanelColumnMetrics(new[] { 120f, 34f, 34f }),
            new PanelColumnMetrics(new[] { 132f })
        };
        var fitSource = PanelGeometry.Build(fitMetrics, 16f, 22, 27, PanelDensity.Normal, 1f);
        foreach (float stripBudget in new[] { fitSource.Height, 518f, 518.5f, 519f, 620f })
        {
            float factor = fitSource.FitFactor(832f, stripBudget);
            Check(factor >= PanelGeometry.MinimumShrink,
                "a " + stripBudget + " px strip is above the shrink floor, so the rebuild it asks for really honours it");
            var rebuilt = PanelGeometry.Build(fitMetrics, 16f, 22, 27, PanelDensity.Normal, factor);
            Check(rebuilt.Height <= stripBudget + 1e-4f,
                "a board rebuilt at its own fit factor is never taller than the " + stripBudget + " px it was measured against");
            Check(rebuilt.Width <= 832f + 1e-3f, "and it still fits the width the board is drawn into at " + stripBudget + " px");
        }

        var failStrip = ResultsRegion.Resolve(true, 400f, true, 880f, 1080f, 16f);
        Check(failStrip.Fits && near(failStrip.Top, 416f) && near(failStrip.Bottom, 864f), "the failure message and the lobby/restart buttons bound the same strip the success page uses");
        Check(!ResultsRegion.Resolve(true, 900f, true, 700f, 1080f, 16f).Fits, "a failure page whose buttons sit above its message yields no strip and the board is not drawn");
        Check(near(ResultsRegion.Resolve(false, float.NaN, true, 880f, 1080f, 16f).Top, 1080f * ResultsRegion.FallbackTopFraction),
            "an unmeasurable failure message still leaves the button band clear through the shared fallback");

        // ---- the overlay gate, the F8 persistence and the shared result strip -----------------------
        // F8 flips both switches and asks for a save: the in-level table and the settlement board are one
        // overlay, and the change outlives the session.
        var toggledOn = TogglePolicy.Apply(true);
        Check(toggledOn.ShowPanel && toggledOn.ShowResultOverlay && toggledOn.Save, "the toggle turns both the HUD and the result overlay on and writes the file back");
        var toggledOff = TogglePolicy.Apply(false);
        Check(!toggledOff.ShowPanel && !toggledOff.ShowResultOverlay && toggledOff.Save, "the toggle turns both off and still writes the file back");
        // Three switches and the toggle all have to agree before a pixel is drawn.
        Check(OverlayPolicy.Draws(true, true, true, true), "the board draws only when the master switch, the overlay, the influence board and the toggle all agree");
        Check(!OverlayPolicy.Draws(false, true, true, true), "the master switch alone can hold the board back");
        Check(!OverlayPolicy.Draws(true, false, true, true), "the result-overlay switch alone can hold the board back");
        Check(!OverlayPolicy.Draws(true, true, false, true), "the influence-board switch alone can hold the board back");
        Check(!OverlayPolicy.Draws(true, true, true, false), "the F8 toggle alone can hold the board back");
        // The two vanilla-block switches are positive and off by default, so the board owns the page.
        Check(OverlayPolicy.HidesArtifacts(false) && OverlayPolicy.HidesReports(false), "the artifact block and the report cards are put away while their switches are off");
        Check(!OverlayPolicy.HidesArtifacts(true) && !OverlayPolicy.HidesReports(true), "switching either block on brings it back");

        // The line the failure page's message must end on is the one the success completion section ended
        // on, remembered per window size and refused outright when it would cover a button.
        FailureAlignment.Forget();
        Check(!FailureAlignment.AnchorKnown(1440f), "a line nobody measured is not claimed");
        FailureAlignment.RememberCompletion(600f, 1440f);
        Check(FailureAlignment.AnchorKnown(1440f), "the completion line is remembered at the window it was measured in");
        Check(!FailureAlignment.AnchorKnown(1080f), "the remembered line is not reused at another window size");
        Check(FailureAlignment.Resolve(false, float.NaN, 1440f, 0f, 16f, true, 1300f, out float alignedBottom, out ResultsSafeArea alignedStrip)
            && near(alignedBottom, 600f) && near(alignedStrip.Top, 616f) && near(alignedStrip.Bottom, 1284f),
            "the failure message is moved onto the remembered completion line and the board starts the same gap under it");
        Check(!FailureAlignment.Resolve(false, float.NaN, 1440f, 0f, 16f, true, 620f, out _, out ResultsSafeArea tightStrip) && !tightStrip.Fits,
            "a button right under the line yields no strip and the board is not drawn rather than covering it");
        var lobbyButton = new LayoutRect(0f, alignedStrip.Bottom + 16f, 1920f, 100f);
        Check(ResultsPlacement.TryFitBetween(1920f, 1440f, alignedStrip.Top, alignedStrip.Bottom, 800f, 500f, 0f, out LayoutRect placedBoard)
            && !placedBoard.Overlaps(lobbyButton),
            "the failure board placed in the shared strip clears the lobby button");
        FailureAlignment.Forget();

        // ---- the settlement board's own visibility policy ------------------------------------------
        // Readiness of the success page's completion section. The board is not allowed over the page until the
        // sector name and the sector icon have both really been drawn, and the answer, once earned, is kept for
        // the rest of that page.
        var readiness = new CompletionReadiness();
        var nothingDrawn = new CompletionEvidence(false, false, false, false, false);
        var nameOnly = new CompletionEvidence(true, true, false, false, false);
        var iconOnly = new CompletionEvidence(false, false, true, true, true);
        var noArtwork = new CompletionEvidence(true, true, true, true, false);
        var nameFading = new CompletionEvidence(true, false, true, true, true);
        var iconFading = new CompletionEvidence(true, true, true, false, true);
        var drawnTogether = new CompletionEvidence(true, true, true, true, true);
        Check(!readiness.Ready && readiness.Page == IntPtr.Zero, "a readiness latch that was never bound to a page is not ready");
        Check(readiness.Bind(new IntPtr(77)) && !readiness.Bind(new IntPtr(77)), "binding a page once starts the wait, and binding the same page again changes nothing");
        Check(!readiness.Observe(nothingDrawn) && !readiness.Observe(nameOnly) && !readiness.Observe(iconOnly)
            && !readiness.Observe(noArtwork) && !readiness.Observe(nameFading) && !readiness.Observe(iconFading),
            "nothing drawn, only the name, only the icon, an icon that exposed no artwork, or either half still fading in all hold the board back");
        for (int frame = 0; frame < 100000; frame++) readiness.Observe(iconFading);
        Check(!readiness.Ready, "a completion section that never finishes drawing is never allowed through, however long the page is up");
        Check(readiness.Observe(drawnTogether), "the board is allowed through once the name and the icon are both drawn");
        Check(readiness.Observe(iconFading) && readiness.Observe(nothingDrawn) && readiness.Ready,
            "an earned readiness survives the page's own later animation: a pulsing icon or a caption caught mid-fade cannot take the board away");
        Check(readiness.Bind(new IntPtr(78)) && !readiness.Ready, "a different result page starts the wait over again");
        Check(readiness.Observe(drawnTogether) && readiness.Ready && !readiness.Bind(new IntPtr(78)), "the new page earns its own answer and keeps it");
        readiness.Forget();
        Check(!readiness.Ready && readiness.Page == IntPtr.Zero, "forgetting a page drops the readiness with it");
        // The render alpha each half has to reach before it counts as drawn. A caption is fully opaque when it
        // has arrived; the icon's own ceiling is set by the game per case and is never 1, so the floor sits just
        // under the lowest ceiling the game uses.
        Check(!CompletionDrawing.Text(0.5f) && !CompletionDrawing.Text(float.NaN), "a caption still fading in, or one nobody could read, is not a drawn caption");
        Check(CompletionDrawing.Text(1f), "a fully opaque caption is drawn");
        Check(!CompletionDrawing.Icon(0.2f) && !CompletionDrawing.Icon(float.NaN) && CompletionDrawing.Icon(0.5f) && CompletionDrawing.Icon(0.8f),
            "an icon at half its own reveal is not drawn, and one the game finished revealing is");
        Check(!CompletionDrawing.Icon(0.5f, 0.8f) && CompletionDrawing.Icon(0.8f, 0.8f), "a 0.8-alpha skull is not complete at the 0.5-alpha background ceiling");
        Check(!CompletionDrawing.Icon(0.8f, float.NaN) && !CompletionDrawing.Icon(1f, 0f), "unknown or invalid sprite targets cannot release the board");
        Check(!CompletionDrawing.AllSectors(true, 2, 1, 2), "the second sector name must be rendered, not merely the cleared-sector header");
        Check(!CompletionDrawing.AllSectors(true, 2, 2, 1), "all completed sector icons must be rendered");
        Check(!CompletionDrawing.AllSectors(true, 0, 0, 0), "an empty completion section never releases the board");
        Check(CompletionDrawing.AllSectors(true, 2, 2, 2), "the full completion section releases together");
        // The switches and the key, read where the board is drawn: the answer for the next frame needs nothing
        // but the flags themselves, which is what makes F8 take effect on the next Repaint.
        Check(ResultVisibility.Shows(true, true, true, true), "the board draws only when the master switch, the overlay, the influence board and the toggle all agree");
        Check(!ResultVisibility.Shows(true, true, true, false), "the F8 toggle alone hides the board, on the same frame it is read");
        Check(!ResultVisibility.Shows(false, true, true, true) && !ResultVisibility.Shows(true, false, true, true) && !ResultVisibility.Shows(true, true, false, true),
            "any one of the three configuration switches holds the board back on its own");
        Check(ResultVisibility.SamePage(new IntPtr(42), new IntPtr(42)) && !ResultVisibility.SamePage(new IntPtr(42), new IntPtr(43)) && !ResultVisibility.SamePage(new IntPtr(42), IntPtr.Zero),
            "a placement and a readiness belong to the page they were earned on and to no other");

        // ---- the failure board is held where it was measured -----------------------------------------
        // In game the strip measured 483..1001 when the board was placed and the table was 517 px tall; a frame
        // later the lobby button's own bounds had moved, the strip measured 483..997.6, and two pixels short of
        // the board was enough for the draw path to refuse it - which is why the board blinked out and only came
        // back while the player happened to hover the page. A board that is already up keeps its place now.
        var heldFailureBoard = new LayoutRect(855f, 483f, 850f, 517f);
        var shorterStrip = new BoardStrip(483f, 997.6f, true, 1013.6f);
        Check(!ResultBoardHold.TryPlace(1920f, 1440f, shorterStrip, 850f, 517f, 0f, out _),
            "a first placement still has to fit the strip the page itself measured");
        Check(ResultBoardHold.TryHold(shorterStrip, heldFailureBoard, 1440f, out LayoutRect heldInPlace)
            && near(heldInPlace.Y, heldFailureBoard.Y) && near(heldInPlace.Bottom, heldFailureBoard.Bottom),
            "a board that is already up keeps the place it was measured into when the strip comes back shorter");
        Check(!heldInPlace.Overlaps(new LayoutRect(0f, shorterStrip.ButtonTop, 1920f, 120f))
            && heldInPlace.Bottom <= shorterStrip.ButtonTop - ResultBoardHold.ButtonClearance + 1e-3f,
            "and it still clears the lobby button while it does");
        var risenButton = new LayoutRect(0f, 995f, 1920f, 120f);
        Check(ResultBoardHold.TryHold(new BoardStrip(483f, 979f, true, risenButton.Y), heldFailureBoard, 1440f, out LayoutRect heldLifted)
            && heldLifted.Bottom <= risenButton.Y - ResultBoardHold.ButtonClearance + 1e-3f && !heldLifted.Overlaps(risenButton),
            "a lobby button that really did move onto the board lifts the board instead of being covered");
        Check(ResultBoardHold.TryHold(new BoardStrip(483f, 1000f, false, float.NaN), heldFailureBoard, 1440f, out LayoutRect heldUnmeasured)
            && near(heldUnmeasured.Y, heldFailureBoard.Y),
            "a page whose button could not be measured leaves the held board exactly where it was");
        Check(ResultBoardHold.TryPlace(1920f, 1440f, new BoardStrip(483f, 1001f, true, 1016f), 850f, 517f, 0f, out LayoutRect fresh, 2f)
            && near(fresh.Y, 483f),
            "the strip the board was first measured against still places it, with the draw path's two pixels of slack");
        Check(ScoringFiles.DirectoryName == "CombatPanel-config" && ScoringFiles.FileName == "scoring.json",
            "the scoring file keeps its own folder and name beside the plugin");
        // Lone-survivor is the sole exception to the previous firepower ordering.
        Check(ImpactScoring.DefaultWeights[(int)ImpactMetric.LoneSurvivor] > ImpactScoring.DefaultWeights[(int)ImpactMetric.Stagger], "lone survivor now outweighs stagger");
        foreach (ImpactMetric firepowerMetric in ImpactScoring.Order)
        {
            if (!ImpactScoring.UsesFirepower(firepowerMetric)) continue;
            foreach (ImpactMetric otherMetric in ImpactScoring.Order)
            {
                if (ImpactScoring.UsesFirepower(otherMetric) || otherMetric == ImpactMetric.LoneSurvivor) continue;
                Check(ImpactScoring.DefaultWeights[(int)firepowerMetric] > ImpactScoring.DefaultWeights[(int)otherMetric],
                    "firepower metric " + firepowerMetric + " outweighs " + otherMetric);
            }
        }
        Check(ImpactScoring.DefaultWeights[(int)ImpactMetric.Stagger] > ImpactScoring.DefaultWeights[(int)ImpactMetric.Revives], "stagger outweighs revives");
        Check(Math.Abs(ImpactScoring.DefaultWeights[(int)ImpactMetric.TotalDamage] - 0.95) < 1e-9, "total damage weight is rebalanced to 0.95 in the thirteen-metric table");
        Check(Math.Abs(ImpactScoring.MaxRoleBonus - 1.3) < 1e-9, "the role-bonus ceiling is still the only ceiling and stays at 1.3");
        Check(Math.Abs(ImpactScoring.RoleFactor(ImpactMetric.TotalDamage, true, ScoringSettings.Defaults().FirepowerBonus, ScoringSettings.Defaults().UtilityBonus) - 1.15) < 1e-9,
            "a default-config support player's total damage factor is exactly 1.15");
        Check(Math.Abs(ImpactScoring.RoleFactor(ImpactMetric.TotalDamage, false, ScoringSettings.Defaults().FirepowerBonus, ScoringSettings.Defaults().UtilityBonus) - 1.0) < 1e-9,
            "a default-config non-support player's total damage factor is exactly 1.0");
        Check(ScoringSettings.Defaults().Weights["TotalDamage"] == ImpactScoring.DefaultWeights[0], "the scoring file's total damage default follows the code default");
        Check(ScoringSettings.Defaults().Weights["Stagger"] == 1.4, "the scoring file's stagger default follows the thirteen-metric weights");
        long ticksPerMs = TimeSpan.TicksPerMillisecond;
        Check(ReloadGate.DebounceMilliseconds == 300, "the hot-reload debounce is the documented 300 ms");
        var gate = new ReloadGate();
        long t0 = DateTime.UtcNow.Ticks;
        Check(!gate.Consume(t0), "a hot-reload gate that was never armed never reloads");
        gate.Arm(t0);
        Check(!gate.Consume(t0), "the gate waits out the debounce instead of reloading at once");
        Check(!gate.Consume(t0 + 299 * ticksPerMs), "a file still settling inside the debounce window does not reload");
        Check(gate.Consume(t0 + 300 * ticksPerMs), "exactly the first poll after the debounce reloads");
        Check(!gate.Consume(t0 + 300 * ticksPerMs), "the settled file is consumed once and never reloaded again");
        long t1 = t0 + TimeSpan.TicksPerSecond;
        gate.Arm(t1);
        Check(gate.Consume(t1 + 300 * ticksPerMs), "a later edit re-arms the same gate and reloads again");
        long t2 = t0 + 10 * TimeSpan.TicksPerSecond;
        gate.Arm(t2);
        gate.Arm(t2 + 100 * ticksPerMs);
        Check(gate.Consume(t2 + 400 * ticksPerMs), "two edits landing together still produce one reload");
        Check(!gate.Consume(t2 + 400 * ticksPerMs), "the doubled edit leaves nothing to reload a second time");
        // ---- the configurable kill-tier weights: defaults, ordering, JSON and the fallback file ----------
        Check(ImpactScoring.DefaultMinorKillWeight == 0.5 && ImpactScoring.DefaultEliteKillWeight == 1.0 && ImpactScoring.DefaultBossKillWeight == 1.5
            && ImpactScoring.DefaultMinorKillWeight < ImpactScoring.DefaultEliteKillWeight && ImpactScoring.DefaultEliteKillWeight < ImpactScoring.DefaultBossKillWeight,
            "the default kill-tier weights are the ordered 1 : 2 : 3 ratio the old code hard-coded");
        Check(Math.Abs(ImpactScoring.WeightedKills(4, 2, 1) - 5.5) < 1e-9,
            "a boss outweighs an elite and an elite outweighs a minor in one weighted kill count");
        ImpactScoring.SetKillWeights(1, 3, 9);
        Check(Math.Abs(ImpactScoring.WeightedKills(0, 0, 1) - 9) < 1e-9, "the kill-tier weights are configurable");
        ImpactScoring.ResetThresholds();
        Check(Math.Abs(ImpactScoring.WeightedKills(0, 0, 1) - 1.5) < 1e-9, "ResetThresholds restores the default kill-tier weights");
        var tierCounters = new ImpactCounters { MinorKills = 3, EliteKills = 1, BossKills = 0 };
        Check(Math.Abs(tierCounters.WeightedKills - 2.5) < 1e-9, "an ImpactCounters with three minor and one elite kill scores 2.5 under the defaults");
        Check(Math.Abs(ScoringSettings.Defaults().MinorKillWeight - 0.5) < 1e-9 && Math.Abs(ScoringSettings.Defaults().EliteKillWeight - 1.0) < 1e-9 && Math.Abs(ScoringSettings.Defaults().BossKillWeight - 1.5) < 1e-9,
            "the scoring file's kill-tier defaults are 0.5 / 1.0 / 1.5");
        Check(Math.Abs(ScoringSettings.Parse(ScoringSettings.Defaults().ToJson()).BossKillWeight - 1.5) < 1e-9, "the kill-tier weights round trip through JSON");
        Check(ScoringSettings.Parse("{}").MinorKillWeight == 0.5 && ScoringSettings.Parse("{}").BossKillWeight == 1.5, "a sparse scoring file fills the kill-tier weights from the defaults");
        ScoringSettings orderedTiers = ScoringSettings.Parse("{\"MinorKillWeight\":1,\"EliteKillWeight\":2,\"BossKillWeight\":3}");
        Check(orderedTiers.MinorKillWeight == 1 && orderedTiers.EliteKillWeight == 2 && orderedTiers.BossKillWeight == 3, "an ordered kill-tier triple is read back");
        bool outOfOrderRejected = false;
        try { ScoringSettings.Parse("{\"MinorKillWeight\":1,\"EliteKillWeight\":0.5,\"BossKillWeight\":3}"); } catch (FormatException) { outOfOrderRejected = true; }
        Check(outOfOrderRejected, "an out-of-order kill-tier triple is rejected");
        string tierDir = Path.Combine(Path.GetTempPath(), "gtfocp-tiers-" + Guid.NewGuid().ToString("N"));
        string tierPath = ScoringFiles.Resolve(tierDir);
        ScoringFiles.Load(tierPath, out _, out _);
        Check(File.ReadAllText(tierPath).Contains("\"BossKillWeight\": 1.5"), "a created scoring file writes the default boss kill weight");
        Directory.Delete(tierDir, true);
        var nativeBlink = new BlinkCompletionLedger();
        Check(!nativeBlink.Ready, "no scheduled sector blink cannot release the board");
        nativeBlink.Require(11, false); nativeBlink.Require(12, false); nativeBlink.Require(13, true);
        nativeBlink.Start(11, 101); nativeBlink.Start(12, 102); nativeBlink.Start(13, 103);
        Check(nativeBlink.Count == 3 && nativeBlink.Names == 1 && nativeBlink.Artwork == 2 && !nativeBlink.Ready, "the actual background, skull and optional title are required, not unused prefabs");
        nativeBlink.Finish(11, 101, true); nativeBlink.Finish(12, 102, true);
        Check(!nativeBlink.Ready && !nativeBlink.NamesReady && nativeBlink.ArtworkReady, "a title whose native BlinkIn is still running holds the board back");
        nativeBlink.Finish(13, 103, false);
        Check(!nativeBlink.Ready, "aborted or BlinkOut completion does not count as revealed");
        nativeBlink.Finish(13, 103, true);
        Check(nativeBlink.Ready && nativeBlink.Completed == 3, "native completion releases the board without a TMP alpha or camera culling test");
        Check(!ResultVisibility.Shows(true, true, true, false) && nativeBlink.Ready, "F8 hides without discarding completed native reveal state");
        Check(ResultVisibility.Shows(true, true, true, true) && nativeBlink.Ready, "F8 shows the already-ready board immediately");
        nativeBlink.Start(13, 104); nativeBlink.Finish(13, 103, true);
        Check(!nativeBlink.Ready, "stale completion cannot release a restarted native animation");
        nativeBlink.Finish(13, 104, true);
        Check(nativeBlink.Ready, "the replacement animation releases only on its own completion");
        nativeBlink.Require(99, false); nativeBlink.Finish(99, 0, true);
        Check(!nativeBlink.Ready, "a required node with no native routine remains unavailable");
        nativeBlink.Clear(); nativeBlink.Require(21, false); nativeBlink.Start(21, 201); nativeBlink.Finish(21, 201, true);
        Check(nativeBlink.Ready && nativeBlink.Names == 0, "a title explicitly disabled by the game does not become a phantom prerequisite");
        nativeBlink.Require(0, true);
        Check(!nativeBlink.Ready, "a missing required object is not mistaken for completion");
        nativeBlink.Clear();
        Check(!nativeBlink.Ready && nativeBlink.Count == 0, "new page visits clear native reveal state even when the page object is reused");
        // ---- the squad exchange in the three topologies, driven in memory (SquadScenarios.cs) -----------
        RunSquadScenarios();
        RunTransportScenarios();
        RunReviveAttributionScenarios();
        RunImpactFeatureScenarios();
        RunEwcProjectileScenarios();
        RunRowHeightScenarios();
        // ---- the settlement board's size: a search, not a guess --------------------------------
        ResultsGrowthScenarios.RunInto(Check);
        // ---- the explanation line under each title: a template a player may edit --------------
        ReasonTemplateScenarios.RunInto(Check);
        // ---- EWC accuracy: its cumulative totals, never its deltas --------------------------------
        // EWC announces a shot when the weapon fires and a hit when the pellet lands, in two separate
        // updates; the second one carries hits with a count of zero. Reading each update as one complete
        // shot and skipping the ones that fired nothing is exactly how an EWC weapon ended up showing
        // shots fired and never a hit, so that traffic is asserted here field by field.
        var ewcShotOnly = new AccuracyCounts(1, 0, 0);
        var ewcHitOnly = new AccuracyCounts(0, 1, 0);
        Check(EwcReconcile.Delta(default, ewcShotOnly).Fired == 1 && EwcReconcile.Delta(default, ewcShotOnly).Hits == 0,
            "the shot EWC announces when a weapon fires is handed to the row");
        Check(EwcReconcile.Delta(default, ewcHitOnly).Hits == 1 && EwcReconcile.Delta(default, ewcHitOnly).Fired == 0,
            "the hit EWC announces in its own update carries no shot at all and is handed to the row anyway");
        Check(EwcReconcile.IsEmpty(EwcReconcile.Delta(ewcShotOnly, ewcShotOnly)),
            "an update that repeats figures already on the row hands over nothing");
        // The level a weapon's figures come from is decided from its first update and then held, so pellets
        // and hits are never taken from two different counters.
        Check(!EwcReconcile.PreferFullShot(8, 1, false, false) && EwcReconcile.PreferFullShot(1, 8, false, false),
            "a weapon reads the widest level that carries a count");
        Check(!EwcReconcile.PreferFullShot(1, 8, true, false) && EwcReconcile.PreferFullShot(8, 1, true, true),
            "and once chosen it stays chosen, whatever a later update's numbers look like");
        // A hit EWC counted is never counted twice, and a hit EWC did not count still reaches the row.
        Check(EwcReconcile.Resolve(new AccuracyCounts(2, 2, 0), new AccuracyCounts(0, 2, 0), out bool deduped).Hits == 2 && !deduped,
            "a hit EWC counted and this machine also saw is counted once");
        Check(EwcReconcile.Resolve(new AccuracyCounts(2, 0, 0), new AccuracyCounts(0, 1, 0), out bool filled).Hits == 1 && filled,
            "a hit this machine saw and EWC never counted still reaches the row, marked as an estimate");
        Check(EwcReconcile.Resolve(new AccuracyCounts(1, 0, 0), new AccuracyCounts(0, 5, 0), out _).Hits == 1,
            "the fill can never push hits past the shots EWC says were fired");
        // A weapon EWC's accuracy API never reports at all, counted entirely from the streams that do carry it
        // (`FireShotAPI` for the shot, `ProjectileAPI` for the hit): this is the case a 涡轮电浆枪 showed as
        // 0/0/0 while it was being fired, and it has to be able to report both figures.
        AccuracyCounts streamedRow = EwcReconcile.Resolve(new AccuracyCounts(0, 0, 0), new AccuracyCounts(3, 2, 1), out bool streamed);
        Check(streamedRow.Fired == 3 && streamedRow.Hits == 2 && streamedRow.Weak == 1 && streamed,
            "a weapon whose shots and hits only ever arrive from EWC's other streams still reports them, and the row says it is no longer purely EWC's own count");
        AccuracyCounts doubledRow = EwcReconcile.Resolve(new AccuracyCounts(3, 3, 0), new AccuracyCounts(3, 3, 0), out bool doubled);
        Check(doubledRow.Fired == 3 && doubledRow.Hits == 3 && !doubled,
            "and a weapon EWC's accuracy API does report is not counted twice by the same streams");
        Check(EwcReconcile.Resolve(new AccuracyCounts(1, 1, 1), new AccuracyCounts(0, 0, 0), out _).Weak == 1,
            "a weakspot EWC counted arrives with the hit");
        Check(EwcReconcile.Resolve(new AccuracyCounts(2, 2, 2), new AccuracyCounts(0, 3, 3), out _).Weak == 2,
            "a weakspot can never outnumber the hits on the row");
        // A cancelled shot arrives as a negative count and is a correction, not noise.
        Check(EwcReconcile.Delta(new AccuracyCounts(3, 0, 0), new AccuracyCounts(2, 0, 0)).Fired == -1,
            "the shot EWC retracts travels as a negative count");
        var ewcRow = new CombatPlayer(91);
        var ewcWeapon = ewcRow.Weapon(1, 0, "沙鹰");
        ewcRow.AdjustAccuracy(ewcWeapon, 1, 0, 0);
        ewcRow.AdjustAccuracy(ewcWeapon, 0, 1, 1);
        Check(ewcRow.Total.ShotsFired == 1 && ewcRow.Total.Hits == 1 && ewcRow.Total.WeakspotHits == 1
            && CombatStatsFormatter.AccuracyText(ewcRow.Total) == "100.0%",
            "the two updates EWC sends for one pellet add up to a fired pellet that hit");
        ewcRow.AdjustAccuracy(ewcWeapon, -1, 0, 0);
        Check(ewcRow.Total.ShotsFired == 0 && CombatStatsFormatter.AccuracyText(ewcRow.Total) == "—",
            "the shot EWC takes back leaves the row with no fired pellet, so it reads as a dash rather than as a rate over a shot that never happened");
        // The counter contract itself: the additive API refuses a hit that carries no shot, which is why
        // this traffic could never be handed to it, and the delta API is the one that accepts it.
        bool additiveRefused = false;
        try { ewcRow.Total.AddAccuracy(0, 1, 1); } catch (ArgumentOutOfRangeException) { additiveRefused = true; }
        Check(additiveRefused, "an update carrying a hit and no shot is refused by the additive counter, as it always was");
        // ---- one archive per result screen, not one per session (SettlementArchive) -------------------
        // The game keeps its result pages alive for the whole session, so the second expedition raises the
        // same page pointer again, and the pointer alone refused that screen as a duplicate.
        var settlementArchive = new SettlementArchive();
        Check(settlementArchive.ShouldWrite(new IntPtr(11), 1u), "the first result screen of a session is archived");
        settlementArchive.MarkWritten(new IntPtr(11), 1u, "initial board");
        Check(settlementArchive.ShouldRefresh(new IntPtr(11), 1u, "late host damage"),
            "late synchronized values mark the existing result screen for an in-place archive refresh");
        settlementArchive.MarkWritten(new IntPtr(11), 1u, "late host damage");
        Check(!settlementArchive.ShouldRefresh(new IntPtr(11), 1u, "late host damage"),
            "an unchanged refreshed result does not rewrite its archive again");
        Check(!settlementArchive.ShouldWrite(new IntPtr(11), 1u),
            "the same result screen is never archived twice, however often its board is re-measured");
        Check(settlementArchive.ShouldWrite(new IntPtr(11), 2u),
            "a second expedition on the same page is archived, which is what the page pointer alone refused");
        settlementArchive.MarkWritten(new IntPtr(11), 2u);
        Check(settlementArchive.ShouldWrite(new IntPtr(12), 2u),
            "a different result page is archived inside one run counter");
        settlementArchive.Forget();
        Check(settlementArchive.ShouldWrite(new IntPtr(11), 2u),
            "a forgotten archive accepts the screen it last refused");
        // ---- a weapon EWC manages but never reports is filled from this machine's own observation ----
        // A row EWC reports is left to EWC. A silent row is moved by what this machine saw, and a pellet
        // already handed over is never handed over twice - that is what keeps the fallback from becoming a
        // second source of numbers.
        Check(EwcReconcile.FallbackHandover(true, new AccuracyCounts(0, 0, 0), new AccuracyCounts(5, 4, 1)).Fired == 0,
            "a row EWC reports is never filled by the fallback: its own cumulative totals are the authority");
        AccuracyCounts firstHandover = EwcReconcile.FallbackHandover(false, new AccuracyCounts(0, 0, 0), new AccuracyCounts(5, 4, 1));
        Check(firstHandover.Fired == 5 && firstHandover.Hits == 4 && firstHandover.Weak == 1,
            "a weapon EWC never reported is handed this machine's own figures instead of staying at 0/0/0");
        Check(EwcReconcile.IsEmpty(EwcReconcile.FallbackHandover(false, new AccuracyCounts(5, 4, 1), new AccuracyCounts(5, 4, 1))),
            "the same observations handed over twice hand over nothing the second time");
        AccuracyCounts grownHandover = EwcReconcile.FallbackHandover(false, new AccuracyCounts(5, 4, 1), new AccuracyCounts(7, 4, 1));
        Check(grownHandover.Fired == 2 && grownHandover.Hits == 0,
            "only the pellets that are new travel on the second handover");
        AccuracyCounts boundedHandover = EwcReconcile.FallbackHandover(false, new AccuracyCounts(0, 0, 0), new AccuracyCounts(1, 9, 9));
        Check(boundedHandover.Fired == 1 && boundedHandover.Hits == 1 && boundedHandover.Weak == 1,
            "a hit seen without its shot cannot outnumber the pellets, so a rate built on it stays a rate");
        // ---- a weapon row's name belongs to the weapon that named it ------------------------------------
        // One archive wrote `HEL左轮手枪` and the board drawn a moment later wrote `手枪` for the same row with
        // the same figures, because a passive probe of a bot's backpack renamed the row after the run ended.
        var named = new CombatPlayer(77);
        var probeNamed = named.Weapon(1, 0, "GearStandard");
        Check(!probeNamed.NameAuthoritative && probeNamed.Name == "GearStandard",
            "a row opened before any weapon could be read carries a placeholder and is not locked");
        named.Weapon(1, 0, "手枪", 0, true);
        Check(probeNamed.Name == "手枪" && probeNamed.NameAuthoritative,
            "the first real name a row gets replaces the placeholder and locks it");
        named.Weapon(1, 0, "HEL左轮手枪");
        Check(probeNamed.Name == "手枪",
            "a later passive probe never renames a locked row, which is the drift an archive and a board disagreed by");
        named.Weapon(1, 0, "HEL左轮手枪", 0, true);
        Check(probeNamed.Name == "HEL左轮手枪",
            "a weapon that really is in the slot still renames the row, so a swap is not frozen out");
        var projection = named.Clone();
        Check(projection.Weapon(1, 0, "手枪").NameAuthoritative
            && projection.Weapon(1, 0, "手枪").Name == "HEL左轮手枪",
            "a display projection carries the locked name and its lock, so it cannot drift either");
        // ---- a projectile impact is only a hit when it landed on an enemy -------------------------------
        // The field run that motivated this showed `~529 / 529` and `~92 / 92` - a hit for every projectile
        // fired, because an impact on a wall was counted as one.
        Check(EwcReconcile.ProjectileHitCounts(true, true, false),
            "a projectile that lands on an enemy with a damage base behind it is a hit");
        Check(!EwcReconcile.ProjectileHitCounts(false, false, false),
            "a projectile that lands on a wall is not a hit, which is what made every projectile weapon read 100%");
        Check(!EwcReconcile.ProjectileHitCounts(true, false, false),
            "a damage base with no enemy behind it is scenery, not a hit");
        Check(!EwcReconcile.ProjectileHitCounts(true, true, true),
            "a static scenery damage base is never a hit, even when it reports an owner");
        Console.WriteLine($"PASS: {_assertions} combat-panel assertions");
    }
    // The same slot arithmetic HudTable.BuildGrid performs at runtime, with every geometry constant
    // left open so the 1.8.2 table and the 1.8.1 snapshot it is compared against are built from one
    // body of content instead of from two hand-written expectations.
    private static PanelGrid Shape(IReadOnlyList<PanelColumn> columns, IReadOnlyList<string[][]> rows, float font, PanelDensity density, float paddingFactor, float gapFactor, float fieldGapFactor, float[] floorsEm)
    {
        var metrics = new List<PanelColumnMetrics>();
        for (int column = 0; column < columns.Count; column++)
        {
            int fields = 1;
            for (int row = 0; row < rows.Count; row++) fields = Math.Max(fields, rows[row][column].Length);
            var widths = new float[fields];
            for (int slot = 0; slot < fields; slot++) widths[slot] = floorsEm[column] / fields * font;
            for (int row = 0; row < rows.Count; row++)
            {
                string[] cell = rows[row][column];
                for (int field = 0; field < cell.Length; field++)
                {
                    int slot = fields - cell.Length + field;
                    if (slot < 0 || slot >= fields) continue;
                    widths[slot] = Math.Max(widths[slot], PanelGeometry.EstimateTextWidth(cell[field], font));
                }
            }
            metrics.Add(new PanelColumnMetrics(widths));
        }
        float separator = font * fieldGapFactor * density.Gap;
        float padding = 2 * font * paddingFactor * density.Column;
        return new PanelGrid(
            metrics.Select(metric => metric.Total(separator) + padding).ToArray(),
            metrics.Select(metric => metric.FieldWidths).ToArray(),
            separator, font * gapFactor * density.Gap, font * PanelGeometry.RowFactor * density.Row, font, rows.Count, density);
    }

    // Widest text one slot can hold; it is what the slot does not use up in front of its own text.
    private static float WidestText(IReadOnlyList<string[][]> rows, int column, int slot, int slots, float font)
    {
        float widest = 0;
        for (int row = 0; row < rows.Count; row++)
        {
            string[] cell = rows[row][column];
            int index = slot - (slots - cell.Length);
            if (index < 0 || index >= cell.Length) continue;
            widest = Math.Max(widest, PanelGeometry.EstimateTextWidth(cell[index], font));
        }
        return widest;
    }

    // Net whitespace the eye reads between two neighbouring texts. Every field is right aligned, so
    // the left block ends exactly at its slot's right edge and the whole distance past the gap is
    // the space the right block leaves in front of the widest text that slot can hold.
    private static float NetWhitespace(PanelGrid grid, IReadOnlyList<string[][]> rows, int leftColumn, int leftSlot, int rightColumn, int rightSlot, float font)
        => grid.FieldRect(0, rightColumn, rightSlot).Right
            - WidestText(rows, rightColumn, rightSlot, grid.FieldCount(rightColumn), font)
            - grid.FieldRect(0, leftColumn, leftSlot).Right;

    private static bool near(float a, float b) => near(a, b, 0.001f);
    private static bool near(float a, float b, float tolerance) => Math.Abs(a - b) <= tolerance;
    private static void Check(bool condition, string description)
    { _assertions++; if (!condition) throw new InvalidOperationException("FAIL: " + description); }
}
