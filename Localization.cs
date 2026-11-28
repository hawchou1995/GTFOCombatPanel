using System;
using System.Globalization;
using System.Collections.Generic;
using System.Text;

namespace HawChow.GTFOCombatPanel;

// Panel text for both supported languages. The default is Chinese; the configured
// value is normalized so common spellings ("zh", "zh-CN", "cn", "en", "English")
// all resolve, and anything unrecognized falls back to Chinese.
public static class CombatStrings
{
    private static string _language = "zh";

    public static string Language
    {
        get { return _language; }
        set { _language = Normalize(value); }
    }

    public static bool English { get { return _language == "en"; } }

    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "zh";
        string candidate = value.Trim().ToLowerInvariant();
        if (candidate.StartsWith("en")) return "en";
        return "zh";
    }

    private static string Pick(string chinese, string english) { return _language == "en" ? english : chinese; }

    // Header for one column, so a hidden column simply never asks for its label.
    public static string Header(PanelColumn column) => string.Join(CombatStatsFormatter.Separator, HeaderFields(column));

    // The header split the same way its values are, so a merged column's labels line up with the
    // numbers underneath them field by field. The pellet label only qualifies "total", which keeps
    // the column two characters narrower than spelling the word out on its own.
    public static string[] HeaderFields(PanelColumn column)
    {
        switch (column)
        {
            case PanelColumn.PlayerName: return new[] { Pick("玩家", "Player") };
            case PanelColumn.DamageAndShare: return new[] { Pick("伤害", "Damage"), Pick("占比", "Share") };
            case PanelColumn.FriendlyFire: return new[] { Pick("友伤", "FF") };
            case PanelColumn.Accuracy: return new[] { Pick("命中率", "Accuracy") };
            case PanelColumn.WeakspotAccuracy: return new[] { Pick("弱点率", "Weakspot") };
            case PanelColumn.Projectiles: return new[] { Pick("总弹丸", "Pellets"), Pick("命中", "Hit"), Pick("弱点", "Weak") };
            default: return new[] { string.Empty };
        }
    }


    public static string Damage { get { return Pick("伤害", "Damage"); } }
    public static string Share { get { return Pick("占比", "Share"); } }
    public static string FriendlyFire { get { return Pick("友伤", "Friendly fire"); } }
    public static string Accuracy { get { return Pick("命中率", "Accuracy"); } }
    public static string Weakspot { get { return Pick("弱点率", "Weakspot"); } }
    public static string Pellets { get { return Pick("弹丸", "Pellets"); } }

    // ---- 1.19.62: the round's created-enemy figure, on the settlement header ----------------------
    // One label for the whole squad, never one per column: the figure answers "how many enemies were created this
    // round", which is not a player's figure. The three forms are the figure itself, the same figure marked as this
    // machine's own observation, and the explicit unknown - never a zero that nobody measured. The wording is the
    // build's existing zh/en pair like every other label here; there is no new setting behind it.
    public static string SpawnFigureLabel { get { return Pick("累计刷怪量：", "Spawned: "); } }
    public static string SpawnFigureLocal { get { return Pick("（本机观测）", " (this machine)"); } }
    public static string SpawnFigureUnknown { get { return Pick("（未知）", " (unknown)"); } }
    public static string TotalHeader { get { return Pick("综合统计", "TOTAL"); } }
    public static string WeaponsHeader { get { return Pick("武器", "WEAPONS"); } }
    public static string SquadHeader { get { return Pick("玩家/综合", "Player/TOTAL"); } }
    // ---- the one cell separator of the settlement board -------------------------------------------
    // Every compound cell of the influence board holds fields of the same kind, and they are held apart
    // by this and nothing else: no brackets, no comma, no run of spaces. What each field counts, and in
    // which unit, is named once - in the row label or in the header above the column - so a cell is only
    // values: `3 / 1 / 123.4`. A figure nobody could observe is the placeholder, an estimate keeps `~`
    // and a percentage keeps `%`.
    public const string CellSeparator = " / ";
    public static string Compound(params string[] fields)
    {
        if (fields == null || fields.Length == 0) return Placeholder;
        var kept = new List<string>(fields.Length);
        for (int index = 0; index < fields.Length; index++)
        {
            if (fields[index] == null) continue;
            kept.Add(fields[index]);
        }
        return kept.Count == 0 ? Placeholder : string.Join(CellSeparator, kept);
    }

    // ---- The influence board ------------------------------------------------------------
    // Rows of the result board, in order: the player's place and name, the score, the sentence, the
    // four gear slots, then each weapon's three figures. A row that pairs or triples numbers says what
    // it counted with the board's own slash separator, and the label column reads in blocks.
    public static string[] ImpactRowLabels()
    {
        return new[]
        {
            Pick("玩家", "Player"),
            Pick("综合评分", "Score"),
            Pick("评价", "Rating"),
            Pick("说明", "Reason"),
            Pick("主武器", "Primary"),
            Pick("副武器", "Secondary"),
            Pick("近战", "Melee"),
            Pick("工具", "Tool"),
            Pick("主武器 · 伤害 / 占比", "Primary · damage / share"),
            Pick("主武器 · 命中 / 弱命", "Primary · hit / weak"),
            Pick("主武器 · 弹丸", "Primary · pellets"),
            Pick("副武器 · 伤害 / 占比", "Secondary · damage / share"),
            Pick("副武器 · 命中 / 弱命", "Secondary · hit / weak"),
            Pick("副武器 · 弹丸", "Secondary · pellets")
        };
    }

    public static string ImpactMetricHeader { get { return Pick("指标", "Metrics"); } }

    // One metric row's label; the order is the display order, which is also ImpactMetric's.
    //
    // The label is where a compound cell says what its fields are: the cell itself holds nothing but the
    // values, so `250.0 / 25.0%` is read under `总伤害 / 占比`, and `3 / 1 / 123.4` under
    // `近战击杀 / 沉睡 / 伤害`. The kill tiers print one bare total each - no state split, no unit word -
    // which is why their labels stay plain.
    public static string MetricLabel(ImpactMetric metric)
    {
        switch (metric)
        {
            case ImpactMetric.TotalDamage: return Pick("总伤害 / 占比", "Total damage / share");
            case ImpactMetric.Stagger: return Pick("硬直阻止", "Stagger");
            case ImpactMetric.Kills: return Pick("击杀", "Kills");
            case ImpactMetric.Revives: return Pick("救援队友", "Revives");
            case ImpactMetric.WeakspotRate: return Pick("弱点命中率", "Weakspot rate");
            case ImpactMetric.Accuracy: return Pick("命中率", "Hit rate");
            // 1.19.34: one row, four fields - the utilization the score is built from and this player's own pack
            // use split into the three packs. Nothing about the order or the number of rows changed.
            case ImpactMetric.SupplyEfficiency: return SupplyRowLabel;
            case ImpactMetric.ToolEfficiency: return Pick("工具效率", "Tool efficiency");
            case ImpactMetric.LoneSurvivor: return Pick("单人存活", "Lone survivor");
            case ImpactMetric.Downs: return Pick("阵亡 / 失血", "Downs / health lost");
            case ImpactMetric.FriendlyFire: return Pick("友伤 / 致倒", "Friendly fire / downs");
            case ImpactMetric.MeleeKills: return Pick("近战击杀 / 沉睡 / 伤害", "Melee kills / sleeping / damage");
            case ImpactMetric.ScoutKills: return Pick("触手击杀 / 沉睡 / 伤害", "Scout kills / sleeping / damage");
            default: return string.Empty;
        }
    }

    // ---- sentences: the user's wording, or the shipping one -------------------------------------
    // A title is the scoring file's when it carries one for that sentence, and the shipping wording
    // otherwise. The two are kept apart on purpose: an entry the user never touched must keep following
    // the language setting instead of freezing the Chinese default into a file nobody edited.
    private static readonly Dictionary<ImpactRating, string> CustomTitles = new Dictionary<ImpactRating, string>();
    private static readonly Dictionary<ImpactRating, string> CustomDescriptions = new Dictionary<ImpactRating, string>();

    // A null or blank text clears that half of the override, which is how the scoring file says "shipped
    // wording" for a field it left alone.
    public static void SetTitleOverride(ImpactRating rating, string title, string description)
    {
        if (!string.IsNullOrWhiteSpace(title)) CustomTitles[rating] = title.Trim(); else CustomTitles.Remove(rating);
        if (!string.IsNullOrWhiteSpace(description)) CustomDescriptions[rating] = description.Trim(); else CustomDescriptions.Remove(rating);
    }

    public static void ResetTitleOverrides()
    {
        CustomTitles.Clear();
        CustomDescriptions.Clear();
    }

    public static bool HasTitleOverride(ImpactRating rating) { return CustomTitles.ContainsKey(rating); }

    public static string Title(ImpactRating rating)
        => CustomTitles.TryGetValue(rating, out string custom) ? custom : TitleText(rating, English);

    // The sentence's own line. The board's explanation row stays the data-driven one, so a description
    // can never contradict the figures printed beside it; this is the wording itself, published so the
    // archive and the log can carry it.
    public static string TitleDescription(ImpactRating rating)
        => CustomDescriptions.TryGetValue(rating, out string custom) ? custom : DescriptionText(rating, English);

    // The shipping wording for a sentence, independent of the language in force: this is what a scoring
    // file entry is compared against, so an untouched entry is not mistaken for a hand-written title.
    public static string DefaultTitle(ImpactRating rating) { return TitleText(rating, false); }
    public static string DefaultTitleDescription(ImpactRating rating) { return DescriptionText(rating, false); }
    // ---- the wording this release replaced ---------------------------------------------------------
    // 1.19.67: the all-round sentence used to say the top led no single row, and its description said the
    // same thing twice over. That assertion is false on any board where the top really does lead a row the
    // run could not score: the rescue floor is two, so a player with one rescue leads the 救援 row printed
    // beside it and still wears this sentence. Both texts now say only what the sentence is - top of the
    // overall score, no category title - which is true for a unique first and for an exact tie alike.
    //
    // A scoring file written by an earlier build carries the then-published wording verbatim, from an entry
    // nobody touched. Those exact strings are recognised as defaults here, so such a file keeps following
    // the Language setting instead of freezing the old claim into a line the player never typed. Nothing
    // else is recognised: any text a player wrote, however close to these, stays that player's own.
    public const string PreviousReasonAllRoundZh = "综合评分全队第一，无单项领先";
    public const string PreviousReasonAllRoundEn = "top composite score, no single row led";
    public const string PreviousDescriptionAllRoundZh = "综合评分全队第一，各项没有单独领先。";
    public const string PreviousDescriptionAllRoundEn = "First on the composite score without leading a single row.";

    // True when this text is the factory wording of an earlier release for that sentence - an untouched
    // entry rather than the player's own line. Only the sentences whose published text this build replaced
    // answer true, and only for the exact string: the comparison is ordinal, after trimming the whitespace
    // the scoring file's own reader trims.
    internal static bool IsPreviousDefaultReason(ImpactRating rating, string text)
    {
        if (rating != ImpactRating.AllRound || text == null) return false;
        string trimmed = text.Trim();
        return string.Equals(trimmed, PreviousReasonAllRoundZh, StringComparison.Ordinal)
            || string.Equals(trimmed, PreviousReasonAllRoundEn, StringComparison.Ordinal);
    }

    internal static bool IsPreviousDefaultDescription(ImpactRating rating, string text)
    {
        if (rating != ImpactRating.AllRound || text == null) return false;
        string trimmed = text.Trim();
        return string.Equals(trimmed, PreviousDescriptionAllRoundZh, StringComparison.Ordinal)
            || string.Equals(trimmed, PreviousDescriptionAllRoundEn, StringComparison.Ordinal);
    }

    private static string TitleText(ImpactRating rating, bool english)
    {
        if (english)
        {
            switch (rating)
            {
                case ImpactRating.TeamPillar: return "Unremarkable";
                case ImpactRating.ZeroScore: return "Battlefield tourist";
                case ImpactRating.Unavailable: return Placeholder;
                case ImpactRating.Damage: return "Firepower core";
                case ImpactRating.Stagger: return "Interrupter";
                case ImpactRating.MinorKills: return "Sweeper";
                case ImpactRating.EliteKills: return "Giant hunter";
                case ImpactRating.BossKills: return "Dragonslayer";
                case ImpactRating.Weakspot: return "Weakspot adept";
                case ImpactRating.Accuracy: return "Dead eye";
                case ImpactRating.Revives: return "Lifesaver";
                case ImpactRating.Supply: return "Quartermaster";
                case ImpactRating.Scan: return "Scout";
                case ImpactRating.LoneSurvivor: return "Last stand";
                case ImpactRating.Downs: return "Frequent faller";
                case ImpactRating.FriendlyFire: return "Friendly-fire hazard";
                case ImpactRating.MeleeKills: return "Melee ace";
                case ImpactRating.ScoutKills: return "Scout hunter";
                case ImpactRating.AllRound: return "All-round anchor";
                default: return "Unremarkable";
            }
        }
        switch (rating)
        {
            case ImpactRating.TeamPillar: return "平平无奇";
            case ImpactRating.ZeroScore: return "战地游客";
            case ImpactRating.Unavailable: return Placeholder;
            case ImpactRating.Damage: return "火力核心";
            case ImpactRating.Stagger: return "寸止大师";
            case ImpactRating.MinorKills: return "清道夫";
            case ImpactRating.EliteKills: return "巨兽猎人";
            case ImpactRating.BossKills: return "屠龙者";
            case ImpactRating.Weakspot: return "弱点专精";
            case ImpactRating.Accuracy: return "弹无虚发";
            case ImpactRating.Revives: return "妙手回春";
            case ImpactRating.Supply: return "后勤大师";
            case ImpactRating.Scan: return "侦察之眼";
            case ImpactRating.LoneSurvivor: return "背水一战";
            case ImpactRating.Downs: return "仰卧起坐";
            case ImpactRating.FriendlyFire: return "敌我不分";
            case ImpactRating.MeleeKills: return "近战强袭";
            case ImpactRating.ScoutKills: return "触手猎手";
            case ImpactRating.AllRound: return "全能中坚";
            default: return "平平无奇";
        }
    }

    // What each sentence means, in one line, in both languages. A penalty sentence says so plainly,
    // because 仰卧起坐 is awarded to the squad's most-downed player, not to the one who avoided going down.
    private static string DescriptionText(ImpactRating rating, bool english)
    {
        switch (rating)
        {
            case ImpactRating.TeamPillar:
                return english ? "No single lead this run; steady work." : "本局没有单项领先，稳定完成本职。";
            case ImpactRating.ZeroScore:
                return english ? "No scoring contribution this run." : "本局没有任何计分贡献。";
            case ImpactRating.Unavailable:
                return english ? "This run's figures could not be read." : "本局数据不可用。";
            case ImpactRating.Damage:
                return english ? "Highest total damage in the squad." : "全队总伤害最高。";
            case ImpactRating.Stagger:
                return english ? "Most enemy attacks interrupted." : "打断敌人攻击次数最多。";
            case ImpactRating.MinorKills:
                return english ? "Most minor kills in the squad." : "小怪击杀数全队最高。";
            case ImpactRating.EliteKills:
                return english ? "Most elite kills in the squad." : "精英击杀数全队最高。";
            case ImpactRating.BossKills:
                return english ? "Most boss kills in the squad." : "BOSS 击杀数全队最高。";
            case ImpactRating.Weakspot:
                return english ? "Highest weakspot share of the pellets that hit." : "弱点命中占全部命中的比例最高。";
            case ImpactRating.Accuracy:
                return english ? "Highest hit rate in the squad." : "命中率全队最高。";
            case ImpactRating.Revives:
                return english ? "Most teammates picked up." : "救起队友次数最多。";
            case ImpactRating.Supply:
                return english ? "Best resupply utilization in the squad." : "补给利用率全队最高。";
            case ImpactRating.Scan:
                return english ? "Most enemies first-marked by scanning." : "扫描首次标记数全队最高。";
            case ImpactRating.LoneSurvivor:
                return english ? "Last one standing more often than anyone." : "独自存活次数全队最多。";
            case ImpactRating.Downs:
                return english ? "Most downs in the squad: a penalty sentence, decided by penalty severity." : "阵亡次数全队最多：负面称号，按惩罚严重度评选。";
            case ImpactRating.FriendlyFire:
                return english ? "Worst friendly fire in the squad: a penalty sentence." : "友伤与友伤致倒最严重：负面称号。";
            case ImpactRating.MeleeKills:
                return english ? "Most melee kills; a sleeping kill counts double." : "近战击杀数全队最高，沉睡击杀加倍计入。";
            case ImpactRating.ScoutKills:
                return english ? "Most Scouts killed in the squad; a sleeping kill counts double." : "触手击杀数全队最高，沉睡击杀加倍计入。";
            case ImpactRating.AllRound:
                return english ? "Top overall composite score; no category title." : "综合得分居首，未获单项称号。";
            default:
                return english ? "No single lead this run; steady work." : "本局没有单项领先，稳定完成本职。";
        }
    }

    // ---- The reason line under the sentence --------------------------------------------------
    // What the sentence was earned with, in the player's own figures. The settlement board prints one
    // of these under every rating, so a title is never a bare word: each line carries the number it
    // came from. They are kept short on purpose - a value column is as wide as its widest cell, and a
    // long line would shrink every other column of the board.
    //
    // A line is the scoring file's when the file carries a template for that sentence, and the shipping
    // wording otherwise: exactly the rule the titles follow, so a template nobody edited keeps following
    // the language setting instead of freezing the Chinese default into a file nobody touched.
    private static readonly Dictionary<ImpactRating, string> CustomReasons = new Dictionary<ImpactRating, string>();

    // A null template clears the override, which is how "follow the language setting" is said. An empty
    // template is a real instruction rather than a missing one: it is how a row is told to print no
    // explanation at all, so it is stored instead of being treated as blank.
    public static void SetReasonOverride(ImpactRating rating, string template)
    {
        if (template == null) CustomReasons.Remove(rating);
        else CustomReasons[rating] = template.Trim();
    }

    public static void ClearReasonOverrides() { CustomReasons.Clear(); }

    public static bool HasReasonOverride(ImpactRating rating) { return CustomReasons.ContainsKey(rating); }

    // The player emptied this sentence's template: the row is meant to carry no explanation at all. The board
    // asks this before it decides on a dash or an estimate marker, because either of those would be this build
    // printing something the player deleted.
    public static bool ReasonOverrideSuppressed(ImpactRating rating)
        => CustomReasons.TryGetValue(rating, out string template) && string.IsNullOrEmpty(template);

    // `{name}` is replaced by the value of that name and by nothing else. A placeholder the caller never
    // supplied is left exactly as the user wrote it rather than being swallowed, so a hand-typed line can
    // never come out with an empty hole where a figure should stand.
    private static string Substitute(string template, params (string Key, string Value)[] values)
    {
        if (string.IsNullOrEmpty(template)) return template ?? string.Empty;
        if (values == null || values.Length == 0) return template;
        var text = new StringBuilder(template.Length + 16);
        int index = 0;
        while (index < template.Length)
        {
            int open = template.IndexOf('{', index);
            int close = open < 0 ? -1 : template.IndexOf('}', open + 1);
            if (open < 0 || close < 0)
            {
                text.Append(template, index, template.Length - index);
                break;
            }
            text.Append(template, index, open - index);
            string name = template.Substring(open + 1, close - open - 1);
            string value = null;
            for (int slot = 0; slot < values.Length; slot++)
            {
                if (!string.Equals(values[slot].Key, name, StringComparison.Ordinal)) continue;
                value = values[slot].Value;
                break;
            }
            if (value != null) text.Append(value);
            else text.Append(template, open, close - open + 1);
            index = close + 1;
        }
        return text.ToString();
    }

    private static bool TryReason(ImpactRating rating, out string template) { return CustomReasons.TryGetValue(rating, out template); }

    public static string ReasonNoScore { get { return TryReason(ImpactRating.ZeroScore, out string template) ? Substitute(template) : Pick("本局没有任何计分贡献", "no scoring contribution this run"); } }
    public static string ReasonUnavailable { get { return TryReason(ImpactRating.Unavailable, out string template) ? Substitute(template) : Placeholder; } }
    public static string ReasonDamage(string damage, string share)
        => TryReason(ImpactRating.Damage, out string template)
            ? Substitute(template, ("damage", damage), ("share", share))
            : Pick("伤害 " + damage + "，占全队 " + share, "damage " + damage + ", " + share + " of the squad");
    public static string ReasonTeam(string damage, long kills)
        => TryReason(ImpactRating.TeamPillar, out string template)
            ? Substitute(template, ("damage", damage), ("count", Count(kills)))
            : Pick("伤害 " + damage + "，击杀 " + Count(kills) + " 只", "damage " + damage + ", " + Count(kills) + " kills");
    public static string ReasonStagger(long count)
        => TryReason(ImpactRating.Stagger, out string template)
            ? Substitute(template, ("count", Count(count)))
            : Pick("打断敌人攻击 " + Count(count) + " 次", "interrupted " + Count(count) + " attacks");
    public static string ReasonKills(KillTier tier, long count)
    {
        // The three classes are three sentences of their own, so a custom template is looked up by the
        // rating the class is scored as - the same mapping the board's own sentence row uses.
        if (TryReason(ReasonRatingOf(tier), out string template)) return Substitute(template, ("count", Count(count)));
        switch (tier)
        {
            case KillTier.Minor: return Pick("击杀小怪 " + Count(count) + " 只", Count(count) + " minor kills");
            case KillTier.Elite: return Pick("击杀精英 " + Count(count) + " 只", Count(count) + " elite kills");
            default: return Pick("击杀 BOSS " + Count(count) + " 只", Count(count) + " boss kills");
        }
    }
    public static string ReasonRevives(long count)
        => TryReason(ImpactRating.Revives, out string template)
            ? Substitute(template, ("count", Count(count)))
            : Pick("救起队友 " + Count(count) + " 次", "picked up " + Count(count) + " teammates");
    public static string ReasonWeakspot(long weakspots, long hits)
        => TryReason(ImpactRating.Weakspot, out string template)
            ? Substitute(template, ("weakspots", Count(weakspots)), ("hits", Count(hits)))
            : Pick("弱点命中 " + Count(weakspots) + "/" + Count(hits) + " 发", Count(weakspots) + "/" + Count(hits) + " weakspot pellets");
    public static string ReasonAccuracy(long hits, long shots)
        => TryReason(ImpactRating.Accuracy, out string template)
            ? Substitute(template, ("hits", Count(hits)), ("shots", Count(shots)))
            : Pick("命中 " + Count(hits) + "/" + Count(shots) + " 发", Count(hits) + "/" + Count(shots) + " pellets on target");
    public static string ReasonSupply(long uses, string efficiency)
        => TryReason(ImpactRating.Supply, out string template)
            ? Substitute(template, ("uses", Count(uses)), ("efficiency", efficiency))
            : Pick("补给 " + Count(uses) + " 次，有效 " + efficiency, Count(uses) + " resupply uses, " + efficiency + " effective");
    public static string ReasonScan(long uses, long firstMarks)
        => TryReason(ImpactRating.Scan, out string template)
            ? Substitute(template, ("uses", Count(uses)), ("firstMarks", Count(firstMarks)))
            : Pick("扫描 " + Count(uses) + " 次，首次标记 " + Count(firstMarks) + " 只", "scanned " + Count(uses) + "x, first-marked " + Count(firstMarks) + " enemies");
    // 1.19.41: the activation count is only readable on the machine that holds the scanner, so a row measured
    // from native execution confirmations alone reports the marks those confirmations proved and no scan count
    // at all. A configurable template keeps its own wording and receives `—` for the count it no longer has.
    public static string ReasonScanMarks(long firstMarks)
        => TryReason(ImpactRating.Scan, out string template)
            ? Substitute(template, ("uses", Placeholder), ("firstMarks", Count(firstMarks)))
            : Pick("首次标记 " + Count(firstMarks) + " 只", "first-marked " + Count(firstMarks) + " enemies");
    public static string ReasonLoneSurvivor(long count)
        => TryReason(ImpactRating.LoneSurvivor, out string template)
            ? Substitute(template, ("count", Count(count)))
            : Pick("独自存活 " + Count(count) + " 次", "last one standing " + Count(count) + "x");
    public static string ReasonDowns(long count)
        => TryReason(ImpactRating.Downs, out string template)
            ? Substitute(template, ("count", Count(count)))
            : Pick("阵亡 " + Count(count) + " 次", "downed " + Count(count) + "x");
    public static string ReasonFriendlyFire(string damage, long deaths)
        => TryReason(ImpactRating.FriendlyFire, out string template)
            ? Substitute(template, ("damage", damage), ("deaths", Count(deaths)))
            : Pick("友伤 " + damage + "，致倒 " + Count(deaths) + " 次", "friendly fire " + damage + ", downed teammates " + Count(deaths) + "x");
    public static string ReasonMeleeKills(long kills, string damage)
        => TryReason(ImpactRating.MeleeKills, out string template)
            ? Substitute(template, ("kills", Count(kills)), ("damage", damage))
            : Pick("近战击杀 " + Count(kills) + " 次，伤害 " + damage, Count(kills) + " melee kills, damage " + damage);
    public static string ReasonScoutKills(long kills)
        => TryReason(ImpactRating.ScoutKills, out string template)
            ? Substitute(template, ("kills", Count(kills)))
            : Pick("触手击杀 " + Count(kills) + " 只", Count(kills) + " Scouts killed");
    // The first place on the overall score: it has no figure of its own to print, so the line says what the
    // sentence is about. 1.19.67: it says that and nothing more - "top overall; no category title". The
    // earlier line asserted the top led no single category, which the board beside it can contradict: a
    // rescue below the scoring floor (the floor is two) is printed as the 救援 row's figure while it earns
    // no point and no sentence, so a 1/0/0/0 player leads that printed row and still wears this one. The
    // sentence is granted on first place in the raw Score - `Rank == 1`, exact ties included - and a score
    // the board prints as 0.0 only decides eligibility: such a row has no first place to stand on, so that
    // arithmetic never moves the rank this sentence is granted on. No wording may promise a unique lead
    // either. Like every other line it is the scoring file's when the file carries one.
    public static string ReasonAllRound { get { return TryReason(ImpactRating.AllRound, out string template) ? Substitute(template) : Pick("综合分居首，未获单项称号", "top overall; no category title"); } }

    // The rating each kill class is scored as, so a custom template for 小怪/精英/BOSS takes effect on
    // the one sentence it names.
    private static ImpactRating ReasonRatingOf(KillTier tier)
    {
        switch (tier)
        {
            case KillTier.Minor: return ImpactRating.MinorKills;
            case KillTier.Elite: return ImpactRating.EliteKills;
            default: return ImpactRating.BossKills;
        }
    }
    // The melee and Scout rows carry the same three figures in the same order - total kills, the part of
    // them landed while the enemy was asleep, and the damage that went with them - so the two rows read
    // alike and the label above each one names all three.
    public static string FriendlyFireCell(string damage, string deaths) => Compound(damage, deaths);
    // The tool detail is the one cell whose meaning cannot live in a shared row label: every player at the
    // table may hold a different tool, so each figure keeps its own one-word tag and the fields are held
    // apart by the board's one separator instead of by a run of spaces.
    //
    // 1.19.64: a scanner row prints all three fields, always. Until this delivery the third clause was dropped
    // whenever the cumulative figure equalled the first-mark one. That was defensible only while both counts came
    // out of the same reveal; since 1.19.42 the cumulative figure is the per-sweep mark count, so a live row that
    // marked ten enemies and revealed ten of them printed `扫描 2 次 / 首次 10 只` and lost the figure the player
    // asked for (the recorded Survival board's Dauda row). Equal counts now print the same three clauses as
    // differing ones. The separator and the estimate marker are untouched, and a counted zero is printed as the
    // zero it is - nothing on this cell is omitted for being equal to its neighbour or for being zero.
    public static string ToolScan(bool estimated, string uses, string marks, string totalMarks)
        => (estimated ? "~" : string.Empty) + Compound(
            Pick("扫描 " + uses + " 次", "scan " + uses + "x"),
            Pick("首次 " + marks + " 只", "first " + marks),
            Pick("累计 " + totalMarks + " 只", "cumulative " + totalMarks));
    // 1.19.64: a scanner row this machine holds no evidence for says which three figures are missing instead of
    // collapsing to a single dash, so every scanner carrier shows its scan count, its first marks and its
    // cumulative marks on the settlement board and in the archive. The placeholder is the labelled one - each
    // value keeps its own clause - and it is never a zero, because nothing measured it.
    public static string ToolScanUnavailable => ToolScan(false, Placeholder, Placeholder, Placeholder);
    public static string ToolSentry(string shots, string hits, string damage)
        => Compound(Pick("射击 " + shots, "shots " + shots), Pick("命中 " + hits, "hits " + hits), Pick("伤害 " + damage, "damage " + damage));
    // 1.19.64: the marker a gear slot's name row carries when the slot really held more than one measured weapon.
    // It says how many of that slot's rows the one-cell-per-slot board cannot draw; it never names one of them, it is
    // never a sum, and a slot that held one weapon prints the name alone exactly as it always did.
    public static string SlotOthers(int others)
        => Pick("（另有 " + Count(others) + " 把）", " (+" + Count(others) + " more)");
    public static string ToolMine(string deployed, string triggered, string damage)
        => Compound(Pick("部署 " + deployed, "deployed " + deployed), Pick("触发 " + triggered, "triggered " + triggered), Pick("伤害 " + damage, "damage " + damage));
    public static string ToolGlue(string shots, string hits, string glued)
        => Compound(Pick("射击 " + shots, "shots " + shots), Pick("命中 " + hits, "hits " + hits), Pick("胶住 " + glued, "stuck " + glued));

    // The supply cell: the utilization the score was built from, then how many packs this player actually
    // consumed, split by the kind of pack it was. `补给效率 / 弹药包 / 工具包 / 血包` names the four fields in that
    // order, and they all live in the one cell the row has always had.
    //
    // 1.19.34: `kindsKnown` is false for a row whose only source was a peer that still speaks the v8 pair - it sent
    // the total and the ammo part and nothing else. Such a row prints `?` in the two kind fields: the split is an
    // absence, so neither a zero nor a guess is printed.
    //
    // 1.19.36: on every player row that carries at least one pack the cell carries four fields, because the label names
    // four things. The one branch that prints a single field is the row whose total, ammo part and both kind parts are
    // all zero; that cell is a placeholder the caller hands in (`ImpactBoard` hands in the placeholder for that row
    // before it ever gets here), so the branch is unreachable in production and is not a fifth shape of the cell. The
    // three kind fields are what replaced the released "all packs" total, so that total is no longer a field of its own
    // fields are what replaced the released "all packs" total, so that total is no longer a field of its own and
    // there is no fifth one. A row whose split cannot be printed therefore prints the ammo part it really sent - a
    // figure that arrived, shown exactly as it arrived - and `?` in the two kind fields. Where the remainder is a
    // *proven* zero (the total is exactly its ammo part, so there is nothing left to be a tool or a medical pack)
    // the two kinds print a counted zero instead. No raw number is changed, and nothing here feeds the score.
    public static string SupplyCell(string efficiency, long totalUses, long ammoUses, long toolUses, long medicalUses, bool kindsKnown, bool medicalKnown = true)
    {
        long total = totalUses > 0 ? totalUses : 0, ammo = ammoUses > 0 ? ammoUses : 0;
        long tool = toolUses > 0 ? toolUses : 0, medical = medicalUses > 0 ? medicalUses : 0;
        if (total == 0 && ammo == 0 && tool == 0 && medical == 0) return efficiency;
        // 1.19.35: a split may only be printed when it describes the total it is printed beside. Two things can go wrong and
        // both used to be printed as a *zero*: a pair that was never split (`kindsKnown` false) and, worse, a pair whose own
        // figures do not add up - a total smaller than its ammo part, or three kinds that do not sum to the total. A zero is
        // a measurement ("no tool pack was consumed"); neither of those is one, so the two kind fields print `?` and the
        // ammo figure that did arrive stays exactly as it is.
        bool describesTheTotal = kindsKnown && ammo + tool + medical == total;
        if (describesTheTotal) return Compound(efficiency, Count(ammo), Count(tool), Count(medical));
        // What the total holds beyond its ammo part: unknown when there is a remainder, and a *proven* zero when the total is
        // exactly its ammo part (there is no remainder left to be a tool or a medical pack). The ammo figure is printed either
        // way: it travelled, it is a measurement, and it is the one kind this build can name without a split.
        long remainder = total - ammo;
        // 1.19.41 fix: a remainder of zero proves only that no pack of the other kinds was *counted* here. The medical
        // figure is a native application this machine either confirmed or did not, so a row whose medkits were counted
        // on somebody else's machine prints `?` there - never the 0 that would claim "this player received no medical
        // pack", which is exactly what the shipment-wide row used to print about every teammate.
        return remainder == 0
            ? Compound(efficiency, Count(ammo), Count(0), medicalKnown ? Count(0) : Unknown)
            : Compound(efficiency, Count(ammo), Unknown, Unknown);
    }
    // The supply row's label, in the two texts the archived settlement files are keyed by.
    public static string SupplyRowLabel { get { return Pick("补给效率 / 弹药包 / 工具包 / 血包", "Supply efficiency / ammo / tool / med"); } }
    // 1.19.60: the released three-field label and the three-field cell beside it are **public API only** now - the
    // archive no longer writes either (`ResultsLog.Build` writes one key per player, the four-field one). They are kept
    // because they are part of this assembly's public surface, and a consumer that reads them - another mod compiled
    // against an older build, a script of the player's own driving the format offline - must not break because the
    // *archive* stopped double-writing. Their own meaning is unchanged: this label's second field is the owner's total
    // number of packs, never the ammo part the four-field cell prints there. Nothing in this mod calls either one.
    public static string SupplyRowLabelLegacy { get { return Pick("补给效率 / 本人 / 弹药", "Supply efficiency / self / ammo"); } }
    // The released three-field form (`补给效率 / 本人 / 弹药`). Kept as public API with the label above; the board uses
    // the four-field overload and the archive uses the board. `ResultsLog` no longer calls it.
    public static string SupplyCell(string efficiency, long selfUses, long ammoUses)
    {
        if (selfUses <= 0 && ammoUses <= 0) return efficiency;
        return Compound(efficiency, Count(selfUses), Count(ammoUses));
    }
    // The down cell: how often, then - once anything was lost - how much lifetime that actually cost.
    // `阵亡 / 失血` names the pair.
    public static string DownsCell(string count, double healthLost)
    {
        if (!double.IsFinite(healthLost) || healthLost <= 0) return count;
        return Compound(count, CombatStatsFormatter.FormatDamage(healthLost));
    }

    private static string Count(long value) { return (value > 0 ? value : 0).ToString(CultureInfo.InvariantCulture); }

    // The mark beside a player whose kit is a support tool, and the word that decorates a podium
    // place on the score row.
    public static string SupportMark { get { return Pick(" 支援", " support"); } }

    public static string RankWord(int rank)
    {
        if (rank == 1) return Pick("金", "gold");
        if (rank == 2) return Pick("银", "silver");
        if (rank == 3) return Pick("铜", "bronze");
        return string.Empty;
    }

    public static string KillTierLabel(KillTier tier)
    {
        switch (tier)
        {
            case KillTier.Minor: return Pick("小怪", "minor");
            case KillTier.Elite: return Pick("精英", "elite");
            default: return Pick("BOSS", "boss");
        }
    }
    public static string KillRowLabel(KillTier tier)
    {
        switch (tier)
        {
            case KillTier.Minor: return Pick("小怪击杀", "Minor kills");
            case KillTier.Elite: return Pick("精英击杀", "Elite kills");
            default: return Pick("BOSS击杀", "Boss kills");
        }
    }

    public static string KillTierMark(KillTier tier) => KillTierLabel(tier);

    public static string Placeholder { get { return "—"; } }
    // A figure that was never reported and that no arithmetic can recover. Distinct from the placeholder: `—`
    // means "this machine observed nothing to print", `?` means "a number of this kind exists and nobody split
    // it into the parts this row prints". It is never a zero, and it is never guessed into one of the kinds.
    public const string Unknown = "?";
}
