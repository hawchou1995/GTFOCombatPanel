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
    // 1.19.62: the round's created-enemy figure as the settlement header prints it - one string for the whole
    // board, never one per column. It is a property of the run, not of a player, so it is not a row and not a cell:
    // the table keeps exactly the rows and columns it had, and the header band draws this line through its own
    // measured slot (`ResultChromeLayout.PlanHeader` / `DrawChromeHeader`). Empty only if a caller builds a layout
    // without a report; `Build` always fills it, with the explicit unknown when no figure could be proven.
    public string Figure;

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
    // by the board's single separator, so a cell reads as `518 / 41%` and lines up on the column's right
    // edge. What each figure counts is named in the row label, never inside the cell.
    public const string ValueSeparator = CombatStrings.CellSeparator;
    private const int MetricHeaderRow = FixedRows;

    public static int MetricRowCount(bool splitKillTiers) { return splitKillTiers ? 15 : 13; }

    public static int RowCount(bool splitKillTiers) { return FixedRows + SeparatorRows + MetricRowCount(splitKillTiers); }

    // Whether the settlement board may spend three rows on the kill tiers in the room this page offers.
    //
    // The split board is the richer one and stays the default: three kill rows (minor/elite/boss) rather than one.
    // It costs two extra rows, and the whole point of the row count is the glyph size those rows buy back:
    //
    //   glyph = floor(room / rows / em-per-glyph)
    //
    // so two rows of thirty are 6.7% of the vertical budget. Collapsing them only helps when it moves the glyph size
    // to the next whole pixel; when it does not, it has taken three rows of detail off the page and paid nothing for
    // them. That is the rule here, and it is decided against the *shortest grid this build can draw* - the same
    // `ResultsBandFit.MinimumDrawableHeight` the fit itself works down to - not against the readable floor, because a
    // readable floor is what a short page can never reach and a comparison against it would collapse the board on
    // every such page.
    public static bool SplitKillTiersFor(float room, float emPerGlyph)
    {
        if (!float.IsFinite(room) || room <= 0f) return true;
        if (!float.IsFinite(emPerGlyph) || emPerGlyph <= 0f) return true;
        int splitRows = RowCount(true), collapsedRows = RowCount(false);
        int splitGlyph = GlyphFor(room, splitRows, emPerGlyph);
        int collapsedGlyph = GlyphFor(room, collapsedRows, emPerGlyph);
        // The board only gives up its kill-tier rows when the page is genuinely cramped *and* the rows buy a bigger
        // glyph. "Genuinely cramped" is the split board's own readable size not fitting the room: on a page with room
        // to spare the figures are already comfortable, and shaving two rows to gain one pixel of a table nobody is
        // struggling to read would be this build spending the player's data for no reason. The threshold is the
        // readable glyph (`MinimumReadablePixels`), so every page that was fine before this existed keeps all 30 rows.
        if (GlyphFor(room, splitRows, emPerGlyph) >= ResultsFontGrowth.MinimumReadablePixels) return true;
        return collapsedGlyph <= splitGlyph;
    }

    // The glyph size a room produces for a row count, by the same arithmetic `ResultsBandFit.FontForBand` uses:
    // `floor(room / rows / emPerGlyph)`. It is zero for a room that is not a room, which keeps every caller's
    // comparison against it honest rather than running on a NaN.
    private static int GlyphFor(float room, int rows, float emPerGlyph)
    {
        if (rows < 1 || !float.IsFinite(room) || room <= 0f) return 0;
        if (!float.IsFinite(emPerGlyph) || emPerGlyph <= 0f) return 0;
        float budget = room / rows;
        if (!(budget > 0f)) return 0;
        return (int)Math.Floor(budget / emPerGlyph);
    }

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
            Put(text, tones, (int)ImpactRowKind.PrimaryWeapon, column, Single(SlotWeaponName(input.PrimaryName, input.PrimaryOthers)));
            Put(text, tones, (int)ImpactRowKind.SecondaryWeapon, column, Single(SlotWeaponName(input.SecondaryName, input.SecondaryOthers)));
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

        // 1.19.62: the round's created-enemy figure travels with the board it belongs to, as one line for the whole
        // squad. It changes no row, no column and no cell: `Rows`, `ColumnCount` and every `Joined(...)` answer are
        // exactly what they were before this existed, which is what keeps the fit, the band and every recorded page
        // geometry untouched.
        return new ImpactBoardLayout
        {
            Columns = columnsArray, Text = text, Tones = tones, Kinds = kinds,
            Figure = SettlementSpawns.Figure(report.Spawns)
        };
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
        // A template the player emptied means this row carries no explanation at all - not a dash for a figure
        // the board could not observe, and not a bare estimate marker. Either of those would be this build
        // talking over the player's own edit, which is the one thing a customisable line must not do.
        if (CombatStrings.ReasonOverrideSuppressed(entry.Rating)) return string.Empty;
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
                // the scan row above them is. 1.19.41: the activation count is only known on the machine that
                // holds the scanner, so a row read from confirmations alone reports the marks it proved and
                // never a number of scans.
                return counters.ScanUses > 0 || counters.ScanFirstMarks > 0 || counters.ScanMarks > 0
                    ? (counters.ScanEstimated ? "~" : string.Empty)
                        + (counters.ScanUses > 0
                            ? CombatStrings.ReasonScan(counters.ScanUses, counters.ScanFirstMarks)
                            : CombatStrings.ReasonScanMarks(counters.ScanFirstMarks))
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
            case ImpactRating.AllRound: return CombatStrings.ReasonAllRound;
            default: return CombatStrings.Placeholder;
        }
    }
    private static string MeleeDamageText(ImpactInput input)
    {
        PlayerCombatStats melee = input?.Melee;
        if (melee == null || !melee.EnemyDamageObserved) return CombatStrings.Placeholder;
        return (melee.EnemyDamageEstimated ? "~" : string.Empty) + CombatStatsFormatter.FormatDamage(melee.EnemyDamage);
    }

    // The Scout row's damage: the counters' own Scout total, with the same `~` / `—` discipline as every
    // other figure - an amount nobody could observe is a dash, never a fabricated zero.
    public static string ScoutDamageText(ImpactCounters counters)
    {
        if (counters == null || !counters.ScoutDamageObserved) return CombatStrings.Placeholder;
        if (!double.IsFinite(counters.ScoutDamage) || counters.ScoutDamage < 0) return CombatStrings.Placeholder;
        // The marker travels with the figure: a client behind a host that runs no panel of its own measured its
        // own blow, and printing that as a measurement would be the one lie this board does not tell.
        return (counters.ScoutDamageEstimated ? "~" : string.Empty) + CombatStatsFormatter.FormatDamage(counters.ScoutDamage);
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
                    if (!entry.Input.EnemyDamageObserved && entry.Input.EnemyDamage <= 0) return Single(CombatStrings.Placeholder);
                    // Two fields under `总伤害 / 占比`: the damage, then its share of the squad's own total.
                    // A share nobody could measure is left off rather than printed as a zero.
                    string damage = (entry.Input.EnemyDamageEstimated ? "~" : string.Empty) + CombatStatsFormatter.FormatDamage(entry.Input.EnemyDamage);
                    string share = Share(entry.Input.EnemyDamage, squadDamage, entry.Input.EnemyDamageEstimated);
                    return share == CombatStrings.Placeholder ? Single(damage) : new[] { damage, share };
                }
            case ImpactMetric.Stagger: return Single(Count(counters.Stagger));
            case ImpactMetric.Kills:
                // A weight class prints its total and nothing else: the sleeping value of a kill is not a
                // quality of the class, and the board neither shows nor keeps an awake / unknown split here.
                if (!split) return new[] { MergedKillText(counters, KillTier.Minor), MergedKillText(counters, KillTier.Elite), MergedKillText(counters, KillTier.Boss) };
                return Single(TierKillText(counters, tier));
            case ImpactMetric.Revives: return Single(Count(counters.Revives));
            case ImpactMetric.WeakspotRate: return Single(RateText(entry.Input, true));
            case ImpactMetric.Accuracy: return Single(RateText(entry.Input, false));
            // 1.19.34: the utilization the score was built from, then this player's own pack use split by the pack
            // it was - `补给效率 / 弹药包 / 工具包 / 血包`, all in the one cell this row has always had.
            case ImpactMetric.SupplyEfficiency:
                return Single(counters.SupplyUses > 0 || counters.SelfSupplyUses > 0
                        || counters.SelfToolUses > 0 || counters.SelfMedicalUses > 0
                    ? CombatStrings.SupplyCell(counters.SupplyUses > 0 ? Percent(counters.SupplyEfficiency, 1) : CombatStrings.Placeholder,
                        counters.SelfSupplyUses, counters.SelfAmmoUses, counters.SelfToolUses, counters.SelfMedicalUses,
                        counters.SelfSupplyKindsKnown, counters.SelfMedicalKnown)
                    : CombatStrings.Placeholder);
            case ImpactMetric.ToolEfficiency: return Single(ToolEfficiencyText(entry));
            case ImpactMetric.LoneSurvivor: return Single(expeditionSucceeded ? Count(counters.LoneSurvivor) : CombatStrings.Placeholder);
            // How often, then how much lifetime it cost, once anything was actually lost: `阵亡 / 失血`.
            case ImpactMetric.Downs: return Single(CombatStrings.DownsCell(Count(counters.Downs), counters.HealthLost));
            case ImpactMetric.FriendlyFire:
                {
                    string damage = entry.Input.FriendlyFireObserved
                        ? (entry.Input.FriendlyFireEstimated ? "~" : string.Empty) + CombatStatsFormatter.FormatDamage(entry.Input.FriendlyFireDamage)
                        : CombatStrings.Placeholder;
                    return Single(CombatStrings.FriendlyFireCell(damage, Count(counters.FriendlyFireDeaths)));
                }
            // The two rows that do report a sleeping subset. Both carry the same three fields in the same
            // order - total kills, the part of them landed on a sleeping enemy, and the damage that went
            // with them - and the row label names all three, so the cell is only values.
            case ImpactMetric.MeleeKills:
                {
                    bool observed = counters.MeleeKills > 0 || counters.MeleeKillsObserved;
                    string damage = MeleeDamageText(entry.Input);
                    if (!observed && damage == CombatStrings.Placeholder) return Single(CombatStrings.Placeholder);
                    return new[]
                    {
                        observed ? Count(counters.MeleeKills) : CombatStrings.Placeholder,
                        observed ? Count(ImpactCounters.AsleepPart(counters.MeleeKills, counters.SleepingMeleeKills)) : CombatStrings.Placeholder,
                        damage
                    };
                }
            case ImpactMetric.ScoutKills:
                {
                    bool observed = counters.ScoutKills > 0 || counters.ScoutKillsObserved;
                    string damage = ScoutDamageText(counters);
                    if (!observed && damage == CombatStrings.Placeholder) return Single(CombatStrings.Placeholder);
                    return new[]
                    {
                        observed ? Count(counters.ScoutKills) : CombatStrings.Placeholder,
                        observed ? Count(ImpactCounters.AsleepPart(counters.ScoutKills, counters.SleepingScoutKills)) : CombatStrings.Placeholder,
                        damage
                    };
                }
            default: return Single(CombatStrings.Placeholder);
        }
    }

    // One split tier cell: the total of that weight class. Nothing is appended to it, so the three tier
    // rows stay a plain column of numbers however the kills were landed.
    public static string TierKillText(ImpactCounters counters, KillTier tier)
    {
        if (counters == null) return CombatStrings.Placeholder;
        return Count(counters.KillCount(tier));
    }

    // The merged row is three fields in one cell - one per weight class - and each field names its own class,
    // because the row label cannot: it has to hold all three. The figures are bare totals; the class word is
    // the only thing attached to them, and it carries no state.
    public static string MergedKillText(ImpactCounters counters, KillTier tier)
    {
        if (counters == null) return CombatStrings.Placeholder;
        return Count(counters.KillCount(tier)) + CombatStrings.KillTierLabel(tier);
    }
    // Which detail block a tool's counters describe - decided from the data first, with the tool's name only
    // as a hint.
    //
    // The name a row carries is whatever the game (or a localization mod) gave the equipped GearClass, and it
    // arrives empty, in Chinese, or as the slot's own placeholder often enough that a display keyed on
    // `name.Contains("Sentry")` left a whole column empty: the sentry had really shot, hit and dealt damage,
    // and the cell printed nothing because the row was called `哨戒炮` or nothing at all. The ledger knows what
    // it recorded, so the figures decide - the name only decides between kinds that reported together, or
    // when nothing has been counted yet.
    public enum ToolDetailKind { None = 0, Sentry = 1, Mine = 2, Glue = 3 }

    public static ToolDetailKind ToolDetailOf(ImpactCounters counters, string name)
    {
        bool sentry = counters != null
            && (counters.SentryShots > 0 || counters.SentryHits > 0 || counters.SentryDamage > 0);
        bool mine = counters != null
            && (counters.MineDeployments > 0 || counters.MinePickups > 0 || counters.MineTriggers > 0 || counters.MineDamage > 0);
        bool glue = counters != null
            && (counters.GlueShots > 0 || counters.GlueHits > 0 || counters.GluedEnemies > 0);
        int kinds = (sentry ? 1 : 0) + (mine ? 1 : 0) + (glue ? 1 : 0);
        // Exactly one kind of record means that kind, whatever the row is called: a Chinese or empty tool name
        // is a missing hint, not a licence to hide the figures the ledger really recorded.
        if (kinds == 1) return sentry ? ToolDetailKind.Sentry : mine ? ToolDetailKind.Mine : ToolDetailKind.Glue;
        ToolDetailKind named = NamedDetail(name);
        if (named != ToolDetailKind.None) return named;
        if (kinds == 0) return ToolDetailKind.None;
        // Several kinds in one merged row and no name to choose between them: the order this board has always
        // printed its blocks in.
        return sentry ? ToolDetailKind.Sentry : mine ? ToolDetailKind.Mine : ToolDetailKind.Glue;
    }

    private static ToolDetailKind NamedDetail(string name)
    {
        if (Contains(name, "Sentry") || Contains(name, "哨戒") || Contains(name, "炮台") || Contains(name, "Turret"))
            return ToolDetailKind.Sentry;
        if (Contains(name, "Tripmine") || Contains(name, "Mine") || Contains(name, "绊雷") || Contains(name, "地雷"))
            return ToolDetailKind.Mine;
        if (Contains(name, "Glue") || Contains(name, "C-Foam") || Contains(name, "胶") || Contains(name, "泡沫"))
            return ToolDetailKind.Glue;
        return ToolDetailKind.None;
    }

    // The scan pair remains the score source; the second segment only reports the equipped tool's use.
    public static string ToolEfficiencyText(ImpactEntry entry)
    {
        if (entry == null || entry.Input == null) return CombatStrings.Placeholder;
        ImpactCounters counters = entry.Counters ?? new ImpactCounters();
        string name = entry.Input.ToolName ?? string.Empty;
        // 1.19.64: every row that carries a scanner prints the whole cell - the count, the first marks and the
        // cumulative marks - so nothing a scanner player did drops off the board for being equal to the field
        // beside it or for being zero. What varies between rows is only the *value* of each field: a measured
        // count (the holder's own readable cooldown, including a counted zero, or a positive figure that player's
        // machine reported), the host's grouped estimate in the count slot, the dash for a count nobody could
        // observe beside the marks that were really proved, and the three labelled placeholders when this machine
        // holds no evidence about the row at all. `ScanCell` decides which of those a row gets; the old gate that
        // collapsed an all-zero row to a single `扫描 —` is gone with the omission it encoded.
        string scan = IsScannerTool(name) ? ScanCell(counters, entry) : string.Empty;
        string details;
        switch (ToolDetailOf(counters, name))
        {
            case ToolDetailKind.Sentry:
                details = CombatStrings.ToolSentry(Count(counters.SentryShots), Count(counters.SentryHits), CombatStatsFormatter.FormatDamage(counters.SentryDamage));
                break;
            case ToolDetailKind.Mine:
                details = CombatStrings.ToolMine(Count(Math.Max(0, counters.MineDeployments - counters.MinePickups)), Count(counters.MineTriggers), CombatStatsFormatter.FormatDamage(counters.MineDamage));
                break;
            case ToolDetailKind.Glue:
                details = CombatStrings.ToolGlue(Count(counters.GlueShots), Count(counters.GlueHits), Count(counters.GluedEnemies));
                break;
            default:
                details = string.Empty;
                break;
        }
        if (scan.Length == 0) return details.Length == 0 ? CombatStrings.Placeholder : details;
        // One separator for the whole board: a tool that both scans and holds a placeable would otherwise print
        // two different joints inside one cell. The halves are one cell, so they are joined like every field.
        return details.Length == 0 ? scan : scan + CombatStrings.CellSeparator + details;
    }
    public static bool IsScannerTool(string name)
        => Contains(name, "Bio Tracker") || Contains(name, "Biotracker") || Contains(name, "Enemy Scanner")
            || Contains(name, "Scanner") || Contains(name, "生物追踪") || Contains(name, "追踪仪") || Contains(name, "扫描仪");
    private static bool Contains(string value, string part) => !string.IsNullOrEmpty(value) && value.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
    // The scan cell of a row that carries a scanner, in the four cases this board distinguishes. The cell always
    // holds all three fields; only their values differ.
    //   * a measured activation count this machine trusts - the holder's readable cooldown (including a counted
    //     zero) or a positive count that player's machine reported: the count, the first marks and the cumulative
    //     marks, estimate marker and all, exactly as that row printed it before 1.19.64;
    //   * no measured count, but the host grouped this player's confirmed marks: the batch count fills that same
    //     slot as a plain number (`扫描 2 次` / `scan 2x`), so the row that used to print `扫描 — 次` beside its
    //     marks now prints the figure there. It carries no `~`, no 约, no （估算）/est. and no second clause;
    //   * no measured count and no estimate, but marks this machine really counted: `扫描 — 次` for the count
    //     nobody could observe, then the first and the cumulative marks as the numbers they are - a first-mark
    //     zero included, because a sweep made entirely of repeats is a counted zero and not an absence;
    //   * no evidence at all: the three labelled placeholders, so the row still says which figures are missing.
    //     The estimate exists only because the round ledger saw confirmed marks, so a guess never replaces an
    //     absence.
    private static string ScanCell(ImpactCounters counters, ImpactEntry entry)
        => counters.ScanUses > 0 || counters.ScanUsesKnown
            ? CombatStrings.ToolScan(counters.ScanEstimated, Count(counters.ScanUses),
                Count(counters.ScanFirstMarks), Count(counters.ScanMarks))
            : entry.Input.ScanRoundsEstimated > 0
                ? CombatStrings.ToolScan(false, Count(entry.Input.ScanRoundsEstimated),
                    Count(counters.ScanFirstMarks), Count(counters.ScanMarks))
                : counters.ScanFirstMarks > 0 || counters.ScanMarks > 0
                    ? CombatStrings.ToolScan(counters.ScanEstimated, CombatStrings.Placeholder,
                        Count(counters.ScanFirstMarks), Count(counters.ScanMarks))
                    : CombatStrings.ToolScanUnavailable;
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

    // A gear name is displayed through the one cleaner this build already has (`CombatStatsFormatter.VisibleName`):
    // the game's own names carry markup, and a name that travelled the wire can carry the cut tail of a tag. The
    // string returned here is the string the column is measured with, so what is measured is what is drawn.
    private static string Name(string name)
    {
        string visible = CombatStatsFormatter.VisibleName(name);
        return string.IsNullOrEmpty(visible) ? CombatStrings.Placeholder : visible;
    }
    // 1.19.64: the name a gear slot's row draws. When that slot really held more than one measured weapon the row
    // also says how many of the slot's rows the board cannot draw (`另有 N 把` / `(+N more)`), so the one name on
    // screen is never read as the whole slot and the figures beside it are never read as the slot's whole damage.
    // A slot that held one weapon - every row any recording of this build has shown - prints its name alone, byte
    // for byte, including the placeholder a nameless row still gets.
    public static string SlotWeaponName(string name, int others)
        => Name(name) + (others > 0 ? CombatStrings.SlotOthers(others) : string.Empty);

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
