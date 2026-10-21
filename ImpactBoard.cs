using System;
using System.Collections.Generic;
using System.Globalization;

namespace HawChow.GTFOCombatPanel;

// How one cell is tinted. The board decides the tone and the renderer turns it into a colour,
// which is what keeps this file free of Unity and therefore testable without a game running.
public enum ImpactTone { Neutral, PlayerColor, Gold, Silver, Copper }

// One line of the board. The fixed rows describe the squad -- the player's place and name, the
// score, the sentence, the figures that sentence was earned with, the four gear slots and the two
// weapons' figures -- and the metric rows are the ten scored figures in descending weight. The enum
// order is the row order.
public enum ImpactRowKind
{
    PlayerName, Score, Rating, RatingReason, PrimaryWeapon, SecondaryWeapon, MeleeWeapon, ToolWeapon,
    PrimaryDamage, PrimaryHit, PrimaryPellets, SecondaryDamage, SecondaryHit, SecondaryPellets,
    Separator, Metric
}

// The board, resolved: one entry per row and column, holding that cell's fields, plus the tone of
// the cell. Column 0 is the label column; column c >= 1 describes entries[c - 1].
public sealed class ImpactBoardLayout
{
    public PanelColumn[] Columns;
    public string[][][] Text;
    public ImpactTone[][] Tones;
    public ImpactRowKind[] Kinds;

    public int Rows { get { return Text != null ? Text.Length : 0; } }
    public int ColumnCount { get { return Columns != null ? Columns.Length : 0; } }

    public string[] Cell(int row, int column)
    {
        if (Text == null || row < 0 || row >= Text.Length) return Array.Empty<string>();
        string[][] cells = Text[row];
        if (cells == null || column < 0 || column >= cells.Length) return Array.Empty<string>();
        return cells[column] ?? Array.Empty<string>();
    }

    public ImpactTone Tone(int row, int column)
    {
        if (Tones == null || row < 0 || row >= Tones.Length) return ImpactTone.Neutral;
        ImpactTone[] rowTones = Tones[row];
        if (rowTones == null || column < 0 || column >= rowTones.Length) return ImpactTone.Neutral;
        return rowTones[column];
    }

    // One cell of the board as the single string a labelled block would print.
    public string Joined(int row, int column)
        => string.Join(ImpactBoard.ValueSeparator, Cell(row, column));
}

// The success and failure screens' influence board: rows are fields, columns are players, and the
// players are ordered by their composite score. Every figure is derived here from plain numbers,
// so the whole layout can be asserted in the headless suite.
public static class ImpactBoard
{
    // The rows that are not metric rows: the player's place and name, the score, the sentence, the
    // line under it that says what the sentence was earned with, the four gear slots, and the two
    // weapons' three statistic rows each.
    public const int FixedRows = 14;
    public const int SeparatorRows = 1;
    // How the figures of one cell are held apart on the board: one value per cell, its figures split
    // by a slash, so a cell reads as `518 41%` -> `518 / 41%` and lines up on the column's right edge.
    public const string ValueSeparator = " / ";
    private const int MetricHeaderRow = FixedRows;

    public static int MetricRowCount(bool splitKillTiers) { return splitKillTiers ? 15 : 13; }

    public static int RowCount(bool splitKillTiers) { return FixedRows + SeparatorRows + MetricRowCount(splitKillTiers); }

    // Rank is conveyed only by colour; no medal word is appended to a score.
    public static ImpactTone RankTone(int rank)
    {
        if (rank == 1) return ImpactTone.Gold;
        if (rank == 2) return ImpactTone.Silver;
        if (rank == 3) return ImpactTone.Copper;
        return ImpactTone.Neutral;
    }

    // One single-field cell of the impact board; the label column and the plain white columns use
    // this, so the field layout of a cell is decided in exactly one place.
    public static string[] Single(string text) { return new[] { text ?? string.Empty }; }

    // The value of one merged column of a weapon's row, in display order: damage and its share,
    // hit rate then weakspot rate, and the pellet triple. A missing weapon degrades to one dash.
    public static string[] WeaponFields(PlayerCombatStats stats, PanelColumn column, double squadDamage)
    {
        if (stats == null) return new[] { CombatStrings.Placeholder };
        switch (column)
        {
            case PanelColumn.DamageAndShare:
                return new[] { CombatStatsFormatter.FormatDamage(stats.EnemyDamage), CombatStatsFormatter.FormatShare(stats, squadDamage) };
            case PanelColumn.Accuracy:
                return new[] { CombatStatsFormatter.AccuracyText(stats) };
            case PanelColumn.WeakspotAccuracy:
                return new[] { CombatStatsFormatter.WeakspotText(stats) };
            case PanelColumn.Projectiles:
                return CombatStatsFormatter.ProjectileFields(stats);
            default:
                return new[] { CombatStrings.Placeholder };
        }
    }

    public static string[] WeaponFields(PlayerCombatStats stats, PanelColumn first, PanelColumn second, double squadDamage)
    {
        string[] head = WeaponFields(stats, first, squadDamage);
        string[] tail = WeaponFields(stats, second, squadDamage);
        var joined = new string[head.Length + tail.Length];
        Array.Copy(head, 0, joined, 0, head.Length);
        Array.Copy(tail, 0, joined, head.Length, tail.Length);
        return joined;
    }

    public static ImpactBoardLayout Build(ImpactReport report, bool splitKillTiers, bool expeditionSucceeded)
    {
        if (report == null) report = ImpactScoring.Evaluate(null, null, 1.0, 1.0);
        List<ImpactEntry> entries = report.Entries;
        int players = entries.Count;
        int columns = players + 1;
        int rows = RowCount(splitKillTiers);

        var columnsArray = new PanelColumn[columns];
        columnsArray[0] = PanelColumn.ImpactLabel;
        for (int column = 1; column < columns; column++) columnsArray[column] = PanelColumn.ImpactValue;

        var text = new string[rows][][];
        var tones = new ImpactTone[rows][];
        var kinds = new ImpactRowKind[rows];
        for (int row = 0; row < rows; row++)
        {
            text[row] = new string[columns][];
            tones[row] = new ImpactTone[columns];
            for (int column = 0; column < columns; column++) { text[row][column] = Single(string.Empty); tones[row][column] = ImpactTone.Neutral; }
        }

        string[] labels = CombatStrings.ImpactRowLabels();
        double squadDamage = 0;
        for (int index = 0; index < players; index++) squadDamage += Math.Max(0, entries[index].Input.EnemyDamage);

        // ---- the descriptive rows ------------------------------------------------------------
        for (int row = 0; row < FixedRows; row++)
        {
            Put(text, tones, row, 0, Single(labels[row]));
            kinds[row] = (ImpactRowKind)row;
        }
        Put(text, tones, MetricHeaderRow, 0, Single(CombatStrings.ImpactMetricHeader));
        kinds[MetricHeaderRow] = ImpactRowKind.Separator;

        for (int index = 0; index < players; index++)
        {
            int column = index + 1;
            ImpactEntry entry = entries[index];
            ImpactInput input = entry.Input;

            Put(text, tones, (int)ImpactRowKind.PlayerName, column, Single("#" + entry.Rank.ToString(CultureInfo.InvariantCulture) + " " + input.Name), ImpactTone.PlayerColor);
            Put(text, tones, (int)ImpactRowKind.Score, column, Single(ImpactScoring.ScoreText(entry.Score)), RankTone(entry.Rank));
            Put(text, tones, (int)ImpactRowKind.Rating, column, Single(CombatStrings.Title(entry.Rating)));
            // The sentence is never left as a bare word: the row under it says what it was earned
            // with, in the player's own figures.
            Put(text, tones, (int)ImpactRowKind.RatingReason, column, Single(ReasonText(entry, squadDamage)));
            Put(text, tones, (int)ImpactRowKind.PrimaryWeapon, column, Single(Name(input.PrimaryName)));
            Put(text, tones, (int)ImpactRowKind.SecondaryWeapon, column, Single(Name(input.SecondaryName)));
            Put(text, tones, (int)ImpactRowKind.MeleeWeapon, column, Single(Name(input.MeleeName)));
            Put(text, tones, (int)ImpactRowKind.ToolWeapon, column, Single(input.SupportRole ? Name(input.ToolName) + CombatStrings.SupportMark : Name(input.ToolName)));

            Put(text, tones, (int)ImpactRowKind.PrimaryDamage, column, WeaponFields(input.Primary, PanelColumn.DamageAndShare, squadDamage));
            Put(text, tones, (int)ImpactRowKind.PrimaryHit, column, WeaponFields(input.Primary, PanelColumn.Accuracy, PanelColumn.WeakspotAccuracy, squadDamage));
            Put(text, tones, (int)ImpactRowKind.PrimaryPellets, column, WeaponFields(input.Primary, PanelColumn.Projectiles, squadDamage));
            Put(text, tones, (int)ImpactRowKind.SecondaryDamage, column, WeaponFields(input.Secondary, PanelColumn.DamageAndShare, squadDamage));
            Put(text, tones, (int)ImpactRowKind.SecondaryHit, column, WeaponFields(input.Secondary, PanelColumn.Accuracy, PanelColumn.WeakspotAccuracy, squadDamage));
            Put(text, tones, (int)ImpactRowKind.SecondaryPellets, column, WeaponFields(input.Secondary, PanelColumn.Projectiles, squadDamage));
        }

        // ---- the metric rows, in descending weight -------------------------------------------
        int metricRow = FixedRows + SeparatorRows;
        for (int index = 0; index < ImpactScoring.Order.Length; index++)
        {
            ImpactMetric metric = ImpactScoring.Order[index];
            if (metric == ImpactMetric.Kills && splitKillTiers)
            {
                metricRow = MetricRow(report, text, tones, kinds, metricRow, entries, metric, KillTier.Minor, true, expeditionSucceeded, squadDamage);
                metricRow = MetricRow(report, text, tones, kinds, metricRow, entries, metric, KillTier.Elite, true, expeditionSucceeded, squadDamage);
                metricRow = MetricRow(report, text, tones, kinds, metricRow, entries, metric, KillTier.Boss, true, expeditionSucceeded, squadDamage);
                metricRow = MetricRow(report, text, tones, kinds, metricRow, entries, ImpactMetric.MeleeKills, KillTier.Minor, false, expeditionSucceeded, squadDamage);
                metricRow = MetricRow(report, text, tones, kinds, metricRow, entries, ImpactMetric.ScoutKills, KillTier.Minor, false, expeditionSucceeded, squadDamage);
                continue;
            }
            if (splitKillTiers && (metric == ImpactMetric.MeleeKills || metric == ImpactMetric.ScoutKills)) continue;
            metricRow = MetricRow(report, text, tones, kinds, metricRow, entries, metric, KillTier.Minor, false, expeditionSucceeded, squadDamage);
        }

        return new ImpactBoardLayout { Columns = columnsArray, Text = text, Tones = tones, Kinds = kinds };
    }

    // The line under the sentence: what the title was earned with, in the player's own figures. It is
    // keyed off the sentence itself rather than off the metric behind it, so the explanation can never
    // describe a title the player is not wearing, and a figure the board could not observe degrades
    // to a dash instead of a fabricated zero.
    public static string ReasonText(ImpactEntry entry, double squadDamage)
    {
        if (entry == null || entry.Input == null) return CombatStrings.Placeholder;
        ImpactInput input = entry.Input;
        if (!input.StatisticsAvailable || !input.ScoreComparable) return CombatStrings.ReasonUnavailable;
        ImpactCounters counters = input.Counters ?? new ImpactCounters();
        switch (entry.Rating)
        {
            case ImpactRating.ZeroScore: return CombatStrings.ReasonNoScore;
            case ImpactRating.Unavailable: return CombatStrings.ReasonUnavailable;
            case ImpactRating.Damage: return DamageReason(input, squadDamage);
            case ImpactRating.TeamPillar:
                if (input.EnemyDamage > 0) return DamageReason(input, squadDamage, true, counters.Kills);
                if (counters.MinorKills > 0) return CombatStrings.ReasonKills(KillTier.Minor, counters.MinorKills);
                if (counters.Stagger > 0) return CombatStrings.ReasonStagger(counters.Stagger);
                return CombatStrings.Placeholder;
            case ImpactRating.Stagger: return counters.Stagger > 0 ? CombatStrings.ReasonStagger(counters.Stagger) : CombatStrings.Placeholder;
            case ImpactRating.MinorKills: return counters.MinorKills > 0 ? CombatStrings.ReasonKills(KillTier.Minor, counters.MinorKills) : CombatStrings.Placeholder;
            case ImpactRating.EliteKills: return counters.EliteKills > 0 ? CombatStrings.ReasonKills(KillTier.Elite, counters.EliteKills) : CombatStrings.Placeholder;
            case ImpactRating.BossKills: return counters.BossKills > 0 ? CombatStrings.ReasonKills(KillTier.Boss, counters.BossKills) : CombatStrings.Placeholder;
            case ImpactRating.Revives: return counters.Revives > 0 ? CombatStrings.ReasonRevives(counters.Revives) : CombatStrings.Placeholder;
            case ImpactRating.Weakspot: return input.Hits > 0 ? CombatStrings.ReasonWeakspot(input.WeakspotHits, input.Hits) : CombatStrings.Placeholder;
            case ImpactRating.Accuracy: return input.ShotsFired > 0 ? CombatStrings.ReasonAccuracy(input.Hits, input.ShotsFired) : CombatStrings.Placeholder;
            case ImpactRating.Supply: return counters.SupplyUses > 0 ? CombatStrings.ReasonSupply(counters.SupplyUses, Percent(counters.SupplyEfficiency, 1)) : CombatStrings.Placeholder;
            case ImpactRating.Scan:
                // A bot's sweep reports no owner, so both figures are marked as an estimate, exactly as
                // the scan row above them is.
                return counters.ScanUses > 0
                    ? (counters.ScanEstimated ? "~" : string.Empty) + CombatStrings.ReasonScan(counters.ScanUses, counters.ScanFirstMarks)
                    : CombatStrings.Placeholder;
            case ImpactRating.LoneSurvivor: return counters.LoneSurvivor > 0 ? CombatStrings.ReasonLoneSurvivor(counters.LoneSurvivor) : CombatStrings.Placeholder;
            case ImpactRating.Downs: return counters.Downs > 0 ? CombatStrings.ReasonDowns(counters.Downs) : CombatStrings.Placeholder;
            case ImpactRating.FriendlyFire:
                {
                    string damage = input.FriendlyFireObserved
                        ? (input.FriendlyFireEstimated ? "~" : string.Empty) + CombatStatsFormatter.FormatDamage(input.FriendlyFireDamage)
                        : CombatStrings.Placeholder;
                    return CombatStrings.ReasonFriendlyFire(damage, counters.FriendlyFireDeaths);
                }
            case ImpactRating.MeleeKills: return CombatStrings.ReasonMeleeKills(counters.MeleeKills, MeleeDamageText(input));
            case ImpactRating.ScoutKills: return CombatStrings.ReasonScoutKills(counters.ScoutKills);
            default: return CombatStrings.Placeholder;
        }
    }
    private static string MeleeDamageText(ImpactInput input)
    {
        PlayerCombatStats melee = input?.Melee;
        if (melee == null || !melee.EnemyDamageObserved) return CombatStrings.Placeholder;
        return (melee.EnemyDamageEstimated ? "~" : string.Empty) + CombatStatsFormatter.FormatDamage(melee.EnemyDamage);
    }

    // The two damage sentences, so a share nobody could measure is a dash rather than a division by a
    // squad total the board never saw.
    private static string DamageReason(ImpactInput input, double squadDamage, bool team = false, long kills = 0)
    {
        // The same figure the board's own damage row prints, so the explanation can never disagree with
        // the row above it; a damage sentence nobody dealt explains itself with a dash instead.
        if (!double.IsFinite(input.EnemyDamage) || input.EnemyDamage <= 0) return CombatStrings.Placeholder;
        string damage = (input.EnemyDamageEstimated ? "~" : string.Empty) + CombatStatsFormatter.FormatDamage(input.EnemyDamage);
        if (team) return CombatStrings.ReasonTeam(damage, kills);
        string share = Share(input.EnemyDamage, squadDamage, input.EnemyDamageEstimated);
        return share == CombatStrings.Placeholder ? CombatStrings.Placeholder : CombatStrings.ReasonDamage(damage, share);
    }

    private static string Share(double damage, double squadDamage, bool estimated)
    {
        if (!double.IsFinite(damage) || damage < 0 || !double.IsFinite(squadDamage) || squadDamage <= 0) return CombatStrings.Placeholder;
        double share = damage / squadDamage * 100;
        if (!double.IsFinite(share) || share < 0) return CombatStrings.Placeholder;
        return (estimated ? "~" : string.Empty) + share.ToString("0.0", CultureInfo.InvariantCulture) + "%";
    }

    private static int MetricRow(ImpactReport report, string[][][] text, ImpactTone[][] tones, ImpactRowKind[] kinds, int row,
        List<ImpactEntry> entries, ImpactMetric metric, KillTier tier, bool split, bool expeditionSucceeded, double squadDamage)
    {
        if (row < 0 || row >= text.Length) return Math.Max(0, row);
        kinds[row] = ImpactRowKind.Metric;
        string label = CombatStrings.MetricLabel(metric);
        if (split) label = CombatStrings.KillRowLabel(tier);
        Put(text, tones, row, 0, Single(label));

        for (int index = 0; index < entries.Count; index++)
        {
            ImpactEntry entry = entries[index];
            Put(text, tones, row, index + 1, MetricFields(entry, metric, tier, split, expeditionSucceeded, squadDamage),
                IsMetricGold(report, entry, index, metric, tier, split, expeditionSucceeded));
        }
        return row + 1;
    }

    public static string[] MetricFields(ImpactEntry entry, ImpactMetric metric, KillTier tier, bool split, bool expeditionSucceeded, double squadDamage = 0)
    {
        if (entry == null) return Single(CombatStrings.Placeholder);
        ImpactCounters counters = entry.Counters ?? new ImpactCounters();
        switch (metric)
        {
            case ImpactMetric.TotalDamage:
                {
                    string damage = (entry.Input.EnemyDamageEstimated ? "~" : string.Empty) + CombatStatsFormatter.FormatDamage(entry.Input.EnemyDamage);
                    string share = Share(entry.Input.EnemyDamage, squadDamage, entry.Input.EnemyDamageEstimated);
                    return Single(share == CombatStrings.Placeholder ? damage : damage + " (" + share + ")");
                }
            case ImpactMetric.Stagger: return Single(Count(counters.Stagger));
            case ImpactMetric.Kills:
                if (!split) return new[] { MergedKillText(counters, KillTier.Minor), MergedKillText(counters, KillTier.Elite), MergedKillText(counters, KillTier.Boss) };
                return Single(TierKillText(counters, tier));
            case ImpactMetric.Revives: return Single(Count(counters.Revives));
            case ImpactMetric.WeakspotRate: return Single(RateText(entry.Input, true));
            case ImpactMetric.Accuracy: return Single(RateText(entry.Input, false));
            // The utilization the score was built from, and this player's own pack use beside it.
            case ImpactMetric.SupplyEfficiency:
                return Single(counters.SupplyUses > 0 || counters.SelfSupplyUses > 0
                    ? CombatStrings.SupplyCell(counters.SupplyUses > 0 ? Percent(counters.SupplyEfficiency, 1) : CombatStrings.Placeholder, counters.SelfSupplyUses, counters.SelfAmmoUses)
                    : CombatStrings.Placeholder);
            case ImpactMetric.ToolEfficiency: return Single(ToolEfficiencyText(entry));
            case ImpactMetric.LoneSurvivor: return Single(expeditionSucceeded ? Count(counters.LoneSurvivor) : CombatStrings.Placeholder);
            // How often, then how much lifetime it cost, once anything was actually lost.
            case ImpactMetric.Downs: return Single(CombatStrings.DownsCell(Count(counters.Downs), counters.HealthLost));
            case ImpactMetric.FriendlyFire:
                {
                    string damage = entry.Input.FriendlyFireObserved
                        ? (entry.Input.FriendlyFireEstimated ? "~" : string.Empty) + CombatStatsFormatter.FormatDamage(entry.Input.FriendlyFireDamage)
                        : CombatStrings.Placeholder;
                    return Single(CombatStrings.FriendlyFireCell(damage, Count(counters.FriendlyFireDeaths)));
                }
            case ImpactMetric.MeleeKills:
                {
                    string kills = counters.MeleeKills > 0 || counters.MeleeKillsObserved
                        ? Count(counters.MeleeKills) + KillStateText(counters.MeleeKills, counters.SleepingMeleeKills, counters.UnknownStateMeleeKills)
                        : CombatStrings.Placeholder;
                    string damage = MeleeDamageText(entry.Input);
                    if (kills == CombatStrings.Placeholder && damage == CombatStrings.Placeholder) return Single(CombatStrings.Placeholder);
                    return Single(CombatStrings.MeleeKillsCell(kills, damage));
                }
            case ImpactMetric.ScoutKills:
                return Single(counters.ScoutKills > 0 || counters.ScoutKillsObserved
                    ? Count(counters.ScoutKills) + KillStateText(counters.ScoutKills, counters.SleepingScoutKills, counters.UnknownStateScoutKills)
                    : CombatStrings.Placeholder);
            default: return Single(CombatStrings.Placeholder);
        }
    }

    // One split tier cell: the count the board has always printed, and - only once a state other than
    // "awake" was observed - the asleep / awake / unknown split of that same count, so nothing new is
    // added to the row and a fully awake tier still reads as a bare number.
    public static string TierKillText(ImpactCounters counters, KillTier tier)
    {
        if (counters == null) return CombatStrings.Placeholder;
        return Count(counters.KillCount(tier)) + KillStateText(counters.KillCount(tier),
            counters.SleepingKillCount(tier), counters.UnknownStateKillCount(tier));
    }

    // The legacy merged cell keeps its weight-class word and carries the same split after it.
    public static string MergedKillText(ImpactCounters counters, KillTier tier)
    {
        if (counters == null) return CombatStrings.Placeholder;
        return Count(counters.KillCount(tier)) + CombatStrings.KillTierMark(tier) + KillStateText(counters.KillCount(tier),
            counters.SleepingKillCount(tier), counters.UnknownStateKillCount(tier));
    }

    private static string KillStateText(long total, long sleeping, long unknown)
    {
        long asleep = ImpactCounters.AsleepPart(total, sleeping);
        long awake = ImpactCounters.AwakePart(total, sleeping, unknown);
        return CombatStrings.KillStateSuffix(asleep, awake, Math.Max(0, total - asleep - awake));
    }
    // The scan pair remains the score source; the second segment only reports the equipped tool's use.
    public static string ToolEfficiencyText(ImpactEntry entry)
    {
        if (entry == null || entry.Input == null) return CombatStrings.Placeholder;
        ImpactCounters counters = entry.Counters ?? new ImpactCounters();
        string name = entry.Input.ToolName ?? string.Empty;
        string scan = IsScannerTool(name)
            ? counters.ScanUses > 0
                ? CombatStrings.ToolScan(counters.ScanEstimated, Count(counters.ScanUses), Count(counters.ScanFirstMarks), Count(counters.ScanMarks))
                : CombatStrings.ToolScanUnavailable
            : string.Empty;
        string details = string.Empty;
        if (Contains(name, "Sentry") || Contains(name, "哨戒"))
            details = CombatStrings.ToolSentry(Count(counters.SentryShots), Count(counters.SentryHits), CombatStatsFormatter.FormatDamage(counters.SentryDamage));
        else if (Contains(name, "Tripmine") || Contains(name, "Mine") || Contains(name, "绊雷") || Contains(name, "地雷"))
            details = CombatStrings.ToolMine(Count(Math.Max(0, counters.MineDeployments - counters.MinePickups)), Count(counters.MineTriggers), CombatStatsFormatter.FormatDamage(counters.MineDamage));
        else if (Contains(name, "Glue") || Contains(name, "C-Foam") || Contains(name, "胶") || Contains(name, "泡沫"))
            details = CombatStrings.ToolGlue(Count(counters.GlueShots), Count(counters.GlueHits), Count(counters.GluedEnemies));
        if (scan.Length == 0) return details.Length == 0 ? CombatStrings.Placeholder : details;
        return details.Length == 0 ? scan : scan + " | " + details;
    }
    public static bool IsScannerTool(string name)
        => Contains(name, "Bio Tracker") || Contains(name, "Biotracker") || Contains(name, "Enemy Scanner")
            || Contains(name, "Scanner") || Contains(name, "生物追踪") || Contains(name, "追踪仪") || Contains(name, "扫描仪");
    private static bool Contains(string value, string part) => !string.IsNullOrEmpty(value) && value.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
    // The gold star goes to the squad's outright leader in the metric, and to nobody when there is a tie or no data.
    public static ImpactTone IsMetricGold(ImpactReport report, ImpactEntry entry, int index, ImpactMetric metric,
        KillTier tier, bool split, bool expeditionSucceeded)
    {
        if (report == null || entry == null) return ImpactTone.Neutral;
        if (metric == ImpactMetric.LoneSurvivor && !expeditionSucceeded) return ImpactTone.Neutral;
        if (metric == ImpactMetric.Kills && split)
        {
            if (tier == KillTier.Minor) return report.IsGoldBy(index, e => e.Counters != null ? e.Counters.MinorKills : 0) ? ImpactTone.Gold : ImpactTone.Neutral;
            if (tier == KillTier.Elite) return report.IsGoldBy(index, e => e.Counters != null ? e.Counters.EliteKills : 0) ? ImpactTone.Gold : ImpactTone.Neutral;
            return report.IsGoldBy(index, e => e.Counters != null ? e.Counters.BossKills : 0) ? ImpactTone.Gold : ImpactTone.Neutral;
        }
        // The rescue floor controls score only; a unique raw-count leader still gets the row tint.
        if (metric == ImpactMetric.Revives)
            return report.IsGoldBy(index, e => e.Counters != null ? e.Counters.Revives : 0)
                ? ImpactTone.Gold : ImpactTone.Neutral;

        return report.IsGold(index, metric) ? ImpactTone.Gold : ImpactTone.Neutral;
    }

    private static void Put(string[][][] text, ImpactTone[][] tones, int row, int column, string[] fields)
        => Put(text, tones, row, column, fields, ImpactTone.Neutral);

    private static void Put(string[][][] text, ImpactTone[][] tones, int row, int column, string[] fields, ImpactTone tone)
    {
        if (text == null || row < 0 || row >= text.Length) return;
        string[][] cells = text[row];
        if (cells == null || column < 0 || column >= cells.Length) return;
        cells[column] = fields == null || fields.Length == 0 ? Single(string.Empty) : fields;
        if (tones != null && tones[row] != null && column >= 0 && column < tones[row].Length) tones[row][column] = tone;
    }

    private static string Name(string name) => string.IsNullOrEmpty(name) ? CombatStrings.Placeholder : name;

    private static string Count(long value) => value > 0 ? value.ToString(CultureInfo.InvariantCulture) : "0";

    private static string Percent(double numerator, double denominator)
    {
        if (!(denominator > 0) || !double.IsFinite(numerator) || numerator < 0) return CombatStrings.Placeholder;
        return (numerator * 100 / denominator).ToString("0.0", CultureInfo.InvariantCulture) + "%";
    }

    // Hit rate and weakspot rate as the board prints them. Both read the impact input's own
    // counters, so the ratio shown here is the same ratio the score was computed from, and an
    // unobserved rate degrades to a single dash instead of a fabricated zero.
    public static string RateText(ImpactInput input, bool weakspot)
    {
        if (input == null) return CombatStrings.Placeholder;
        long numerator = weakspot ? input.WeakspotHits : input.Hits;
        long denominator = weakspot ? input.Hits : input.ShotsFired;
        if (denominator <= 0 || numerator < 0) return CombatStrings.Placeholder;
        string mark = input.AccuracyEstimated ? "~" : string.Empty;
        return mark + Percent(numerator, denominator);
    }
}
