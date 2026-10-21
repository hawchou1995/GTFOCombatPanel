using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace HawChow.GTFOCombatPanel;

// Pure managed model: no Unity, BepInEx or network dependencies, so the whole scoring configuration -
// its defaults, its validation and its file handling - is exercised by the headless smoke suite.
//
// The editable scoring file lives beside this plugin's DLL in CombatPanel-config/scoring.json.
// It carries thirteen metric weights, enabled names, health thresholds, support-tool rules and role bonuses.
// Fresh installs use the same defaults as the headless model; hand-edited files are migrated in memory.
// Existing user files are never rewritten by the plugin.
public sealed class ScoringSettings
{
    public static readonly string[] MetricNames = { "TotalDamage", "Stagger", "Kills", "Revives", "WeakspotRate", "Accuracy", "SupplyEfficiency", "ToolEfficiency", "LoneSurvivor", "Downs", "FriendlyFire", "MeleeKills", "ScoutKills" };

    public string Guidance { get; set; } = "本机评分，不同步队友。Weights 为十三项权重(0-10)，0 不计分；EnabledMetrics 为 null 时全部启用，否则仅启用列出的名称。ToolEfficiency 显示工具统计，但评分继续只使用扫描首次标记数；FriendlyFire 按友伤伤害与友伤致倒次数分别相对归一后等权惩罚。MeleeKills / ScoutKills 分别按近战击杀与 Scout 击杀数计分；近战行同时显示 GearMelee 已采集的累计伤害。MinorHealth / EliteHealth 为小怪/精英血量上限。FirepowerBonus 为辅助火力补偿(1-1.3，仅作用于总伤害与硬直阻止)，UtilityBonus 为辅助效用倍率(1-1.3)。SupportToolNames 为辅助工具名称子串；ExcludedTools 优先排除(哨戒炮/绊雷)；OtherToolsAreSupport 将其余非空工具归为辅助。RescueFloor 为救援计分下限，AccuracyConfidencePellets 为命中率置信样本。MinorKillWeight / EliteKillWeight / BossKillWeight 为三档击杀的相对价值，必须满足 小怪 ≤ 精英 ≤ boss（默认 0.5 / 1 / 1.5）；Weights 里的 Kills 是整行权重，两者相乘。SleepingKillMultiplier 为沉睡击杀相对同类清醒击杀的倍率(默认 2，有效范围 1-10)，只加成已判定为沉睡的击杀，状态未知按清醒计。Titles 为每一句结算称号的 Title / Description：小怪、精英、BOSS 三档与总伤害各有独立称号，负面称号(Downs / FriendlyFire)按惩罚严重度授予全队最严重者；保持默认文本即跟随语言设置，改动后即为自定义。无效文件保留上次有效配置，缺失时自动创建默认文件。";

    public Dictionary<string, double> Weights { get; set; } = DefaultWeights();
    public string[] EnabledMetrics { get; set; }
    public double MinorHealth { get; set; } = 120;
    public double EliteHealth { get; set; } = 1000;
    // What one kill of each tier is worth, relative to each other. Must be non-negative and
    // non-decreasing in tier order (minor <= elite <= boss); the default is the 1 : 2 : 3 ratio.
    public double MinorKillWeight { get; set; } = ImpactScoring.DefaultMinorKillWeight;
    public double EliteKillWeight { get; set; } = ImpactScoring.DefaultEliteKillWeight;
    public double BossKillWeight { get; set; } = ImpactScoring.DefaultBossKillWeight;
    // What a kill landed on an enemy that was still asleep is worth, in awake kills of the same class.
    // Bounded to one and up: the multiplier exists to reward the state a kill was landed in, and a value
    // below one would punish it. The JSON carries it and the plugin applies it to the running scoring.
    public double SleepingKillMultiplier { get; set; } = ImpactScoring.DefaultSleepingKillMultiplier;
    public double FirepowerBonus { get; set; } = 1.15;
    public double UtilityBonus { get; set; } = 1.0;
    public string[] SupportToolNames { get; set; } = Split(SupportTools.DefaultNames);
    public string[] ExcludedTools { get; set; } = Split(SupportTools.DefaultExcluded);
    public bool OtherToolsAreSupport { get; set; } = true;
    public int RescueFloor { get; set; } = ImpactScoring.MinimumRescuesForScoring;
    public int AccuracyConfidencePellets { get; set; } = ImpactScoring.AccuracyConfidencePellets;

    // ---- the settlement sentences ---------------------------------------------------------------
    // One entry per sentence the board can print. `Title` is the word the board shows and `Description`
    // is what that sentence means; both are free text. An entry left exactly as shipped keeps following
    // the language setting rather than freezing the Chinese wording into a file nobody edited.
    public const int MaxTitleLength = 16;
    public const int MaxTitleDescriptionLength = 96;
    public static readonly string[] TitleNames =
    {
        "TeamPillar", "ZeroScore", "Damage", "Stagger", "MinorKills", "EliteKills", "BossKills", "Revives",
        "Weakspot", "Accuracy", "Supply", "Scan", "LoneSurvivor", "Downs", "FriendlyFire", "MeleeKills", "ScoutKills"
    };
    public Dictionary<string, ScoringTitle> Titles { get; set; } = DefaultTitles();

    // The shipped wording, taken from the display layer so the two can never drift apart.
    public static Dictionary<string, ScoringTitle> DefaultTitles()
    {
        var titles = new Dictionary<string, ScoringTitle>();
        foreach (string name in TitleNames)
        {
            ImpactRating rating = RatingOf(name);
            titles[name] = new ScoringTitle
            {
                Title = CombatStrings.DefaultTitle(rating),
                Description = CombatStrings.DefaultTitleDescription(rating)
            };
        }
        return titles;
    }

    public static Dictionary<string, double> DefaultWeights()
    {
        var weights = new Dictionary<string, double>();
        for (int index = 0; index < MetricNames.Length; index++) weights[MetricNames[index]] = ImpactScoring.DefaultWeights[index];
        return weights;
    }

    // The defaults of this release, used when no file exists and as the fallback for a sparse file.
    public static ScoringSettings Defaults() => new ScoringSettings();


    private static string[] Split(string commaSeparated)
        => string.IsNullOrWhiteSpace(commaSeparated)
            ? Array.Empty<string>()
            : commaSeparated.Split(',').Select(part => part.Trim()).Where(part => part.Length > 0).ToArray();

    private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    // Never returns null for a file that parsed: a settings object that violates the bounds is a parse
    // failure, and the caller keeps the last good one instead of half-applying a broken file.
    public static ScoringSettings Parse(string json)
    {
        var settings = JsonSerializer.Deserialize<ScoringSettings>(json, Options);
        if (settings == null) throw new FormatException("Scoring settings must be an object.");
        if (settings.Weights != null)
        {
            if (settings.Weights.TryGetValue("ScanEfficiency", out double oldScanWeight))
            {
                settings.Weights.Remove("ScanEfficiency");
                if (!settings.Weights.ContainsKey("ToolEfficiency")) settings.Weights["ToolEfficiency"] = oldScanWeight;
            }
            string[] legacyTenNames = MetricNames.Take(10).ToArray();
            if (!settings.Weights.ContainsKey("FriendlyFire") && legacyTenNames.All(name => settings.Weights.ContainsKey(name)))
            {
                string priorDefaults = string.Join(",", legacyTenNames.Select(name => settings.Weights[name].ToString("0.0##", CultureInfo.InvariantCulture)));
                if (ImpactScoring.IsLegacyDefaultWeights(priorDefaults)) settings.Weights = DefaultWeights();
                else settings.Weights["FriendlyFire"] = ImpactScoring.DefaultWeights[(int)ImpactMetric.FriendlyFire];
            }
            string[] legacyElevenNames = MetricNames.Take(11).ToArray();
            if (legacyElevenNames.All(name => settings.Weights.ContainsKey(name)))
            {
                string previousEleven = string.Join(",", legacyElevenNames.Select(name => settings.Weights[name].ToString("0.0##", CultureInfo.InvariantCulture)));
                if (string.Equals(previousEleven, ImpactScoring.PreviousElevenMetricWeightsText, StringComparison.Ordinal))
                    settings.Weights = DefaultWeights();
            }
            // The thirteen-metric list of the previous release is recognised the same way: this release
            // reweighted lone survivor, so a file still holding that exact published list is a default and
            // migrates, while any hand-edited value in it keeps what the user typed.
            string[] currentNames = MetricNames;
            if (currentNames.All(name => settings.Weights.ContainsKey(name)))
            {
                string previousThirteen = string.Join(",", currentNames.Select(name => settings.Weights[name].ToString("0.0##", CultureInfo.InvariantCulture)));
                if (string.Equals(previousThirteen, ImpactScoring.PreviousThirteenMetricWeightsText, StringComparison.Ordinal))
                    settings.Weights = DefaultWeights();
            }
            if (!settings.Weights.ContainsKey("MeleeKills")) settings.Weights["MeleeKills"] = ImpactScoring.DefaultWeights[(int)ImpactMetric.MeleeKills];
            if (!settings.Weights.ContainsKey("ScoutKills")) settings.Weights["ScoutKills"] = ImpactScoring.DefaultWeights[(int)ImpactMetric.ScoutKills];
        }
        if (settings.EnabledMetrics != null)
        {
            for (int index = 0; index < settings.EnabledMetrics.Length; index++)
                if (settings.EnabledMetrics[index] == "ScanEfficiency") settings.EnabledMetrics[index] = "ToolEfficiency";
            settings.EnabledMetrics = settings.EnabledMetrics.Distinct().ToArray();
        }
        // Sentence wording is canonicalised before it is validated, so a key typed with the wrong case is
        // read, stored and rewritten under the one spelling the board looks up.
        settings.NormalizeTitles();
        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        if (Weights == null || Weights.Count != MetricNames.Length || MetricNames.Any(name => !Weights.ContainsKey(name)))
            throw new FormatException("Weights must contain exactly thirteen named metrics.");
        foreach (string name in MetricNames) Bound(Weights[name], 0, 10, "Weights." + name);
        Bound(MinorHealth, 1, 10000000, "MinorHealth");
        Bound(EliteHealth, MinorHealth + 1, 100000000, "EliteHealth");
        Bound(MinorKillWeight, 0, 100, "MinorKillWeight");
        Bound(EliteKillWeight, 0, 100, "EliteKillWeight");
        Bound(BossKillWeight, 0, 100, "BossKillWeight");
        if (!(MinorKillWeight <= EliteKillWeight && EliteKillWeight <= BossKillWeight))
            throw new FormatException("Kill weights must be non-decreasing (MinorKillWeight <= EliteKillWeight <= BossKillWeight).");
        // A sleeping kill may be worth several awake ones, never less: the multiplier is the value of a
        // stealth kill, so one is its floor and the documented maximum its ceiling.
        Bound(SleepingKillMultiplier, 1, ImpactScoring.MaxSleepingKillMultiplier, "SleepingKillMultiplier");
        Bound(FirepowerBonus, 1, ImpactScoring.MaxRoleBonus, "FirepowerBonus");
        Bound(UtilityBonus, 1, ImpactScoring.MaxRoleBonus, "UtilityBonus");
        if (RescueFloor < 0 || RescueFloor > 100) throw new FormatException("RescueFloor must be between 0 and 100.");
        if (AccuracyConfidencePellets < 1 || AccuracyConfidencePellets > 100000) throw new FormatException("AccuracyConfidencePellets must be between 1 and 100000.");
        if (EnabledMetrics != null && (EnabledMetrics.Any(name => name == null || !MetricNames.Contains(name)) || EnabledMetrics.Distinct().Count() != EnabledMetrics.Length))
            throw new FormatException("EnabledMetrics contains unknown or repeated names.");
        ValidateNames(SupportToolNames, "SupportToolNames");
        ValidateNames(ExcludedTools, "ExcludedTools");
        ValidateTitles();
    }

    // One entry per sentence the board can print, all present, each a single line of usable text: a title
    // is as wide as the column it lands in, and a line break inside it would break the row it sits on.
    private void ValidateTitles()
    {
        if (Titles == null || Titles.Count != TitleNames.Length)
            throw new FormatException("Titles must carry exactly one entry per settlement sentence.");
        foreach (string name in TitleNames)
        {
            if (!Titles.ContainsKey(name)) throw new FormatException("Titles is missing " + name + ".");
            ScoringTitle entry = Titles[name];
            if (entry == null) throw new FormatException("Titles." + name + " must be an object with Title and Description.");
            RequireLine(entry.Title, MaxTitleLength, "Titles." + name + ".Title");
            RequireLine(entry.Description, MaxTitleDescriptionLength, "Titles." + name + ".Description");
        }
    }

    private static void RequireLine(string text, int maxLength, string field)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException(field + " must not be empty.");
        string trimmed = text.Trim();
        if (trimmed.Length > maxLength) throw new FormatException(field + " must be at most " + maxLength + " characters.");
        if (trimmed.IndexOfAny(new[] { '\r', '\n', '\t' }) >= 0) throw new FormatException(field + " must be a single line of text.");
    }

    private static void Bound(double value, double min, double max, string name)
    {
        if (!double.IsFinite(value) || value < min || value > max) throw new FormatException(name + " outside valid bounds.");
    }

    private static void ValidateNames(string[] names, string field)
    {
        if (names == null || names.Length > 128 || names.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Contains(',')))
            throw new FormatException(field + " must contain nonempty tool-name substrings (no commas).");
    }

    // The thirteen weights in board order, with disabled metrics at 0 rather than removed.
    // EnabledMetrics == null means every metric is on.
    public double[] EffectiveWeights()
        => MetricNames.Select(name => EnabledMetrics == null || EnabledMetrics.Contains(name) ? Weights[name] : 0d).ToArray();

    public bool IsSupport(string tool)
    {
        if (string.IsNullOrWhiteSpace(tool) || ExcludedTools.Any(name => tool.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)) return false;
        return OtherToolsAreSupport || SupportToolNames.Any(name => tool.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    // ---- sentence handling ----------------------------------------------------------------------
    private static ImpactRating RatingOf(string name) => (ImpactRating)Enum.Parse(typeof(ImpactRating), name, false);

    private static string CanonicalTitleName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string trimmed = name.Trim();
        foreach (string candidate in TitleNames)
            if (string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase)) return candidate;
        return null;
    }

    // Keys are matched without case, so a hand-written file is not rejected over a capital letter, but the
    // canonical spelling is what stays in memory and what the file is rewritten as. A missing sentence is
    // filled from the shipped defaults; an unknown or repeated one makes the whole file invalid, which is
    // how the last good settings keep the board they were scored with.
    private void NormalizeTitles()
    {
        if (Titles == null || Titles.Count == 0)
        {
            Titles = DefaultTitles();
            return;
        }
        var normalized = new Dictionary<string, ScoringTitle>();
        foreach (KeyValuePair<string, ScoringTitle> pair in Titles)
        {
            string canonical = CanonicalTitleName(pair.Key);
            if (canonical == null) throw new FormatException("Titles contains an unknown sentence: " + pair.Key + ".");
            if (normalized.ContainsKey(canonical)) throw new FormatException("Titles repeats " + canonical + ".");
            if (pair.Value == null) throw new FormatException("Titles." + canonical + " must be an object with Title and Description.");
            normalized[canonical] = new ScoringTitle
            {
                Title = pair.Value.Title == null ? string.Empty : pair.Value.Title.Trim(),
                Description = pair.Value.Description == null ? string.Empty : pair.Value.Description.Trim()
            };
        }
        Dictionary<string, ScoringTitle> shipped = DefaultTitles();
        var ordered = new Dictionary<string, ScoringTitle>();
        foreach (string name in TitleNames)
            ordered[name] = normalized.TryGetValue(name, out ScoringTitle entry) ? entry : shipped[name];
        Titles = ordered;
    }

    // Installs this file's wording into the display layer. A half that still reads exactly as shipped is
    // left to the language setting, so only the text the user really changed is pinned down.
    public void ApplyTitles()
    {
        foreach (string name in TitleNames)
        {
            ImpactRating rating = RatingOf(name);
            ScoringTitle entry = Titles != null && Titles.TryGetValue(name, out ScoringTitle found) ? found : null;
            string title = entry != null && !IsShippedText(entry.Title, CombatStrings.DefaultTitle(rating)) ? entry.Title : null;
            string description = entry != null && !IsShippedText(entry.Description, CombatStrings.DefaultTitleDescription(rating)) ? entry.Description : null;
            CombatStrings.SetTitleOverride(rating, title, description);
        }
    }

    private static bool IsShippedText(string text, string shipped)
        => string.IsNullOrWhiteSpace(text) || string.Equals(text.Trim(), shipped, StringComparison.Ordinal);

    // What "this file changed" means: the settings that affect a score, in a stable order. The revision is
    // what the runtime compares to decide whether a result screen has to be rebuilt; a mere data update
    // leaves it alone.
    public string Fingerprint()
    {
        var text = new StringBuilder();
        foreach (string name in MetricNames) text.Append(name).Append('=').Append(Weights[name].ToString("R", CultureInfo.InvariantCulture)).Append(';');
        text.Append("en=").Append(EnabledMetrics == null ? "*" : string.Join("|", EnabledMetrics)).Append(';');
        text.Append("hp=").Append(MinorHealth.ToString("R", CultureInfo.InvariantCulture)).Append('/').Append(EliteHealth.ToString("R", CultureInfo.InvariantCulture)).Append(';');
        text.Append("kill=").Append(MinorKillWeight.ToString("R", CultureInfo.InvariantCulture)).Append('/').Append(EliteKillWeight.ToString("R", CultureInfo.InvariantCulture)).Append('/').Append(BossKillWeight.ToString("R", CultureInfo.InvariantCulture)).Append(';');
        text.Append("roll=").Append(FirepowerBonus.ToString("R", CultureInfo.InvariantCulture)).Append('/').Append(UtilityBonus.ToString("R", CultureInfo.InvariantCulture)).Append(';');
        text.Append("sup=").Append(string.Join("|", SupportToolNames)).Append('/').Append(string.Join("|", ExcludedTools)).Append('/').Append(OtherToolsAreSupport).Append(';');
        // The sentences and the sleeping multiplier belong here too: a title the user renamed is exactly the
        // kind of change that has to rebuild the settlement board.
        text.Append("sleep=").Append(SleepingKillMultiplier.ToString("R", CultureInfo.InvariantCulture)).Append(';');
        text.Append("titles=");
        foreach (string name in TitleNames)
        {
            ScoringTitle entry = Titles != null && Titles.TryGetValue(name, out ScoringTitle found) ? found : null;
            text.Append(name).Append('=').Append(entry != null ? entry.Title : string.Empty)
                .Append('/').Append(entry != null ? entry.Description : string.Empty).Append(';');
        }
        text.Append("floor=").Append(RescueFloor).Append('/').Append(AccuracyConfidencePellets);
        return text.ToString();
    }
}

// One settlement sentence as the scoring file carries it: the word the board prints and the line that
// explains what that word means. Both are the user's text; neither is ever inferred from data.
public sealed class ScoringTitle
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

// The user-editable scoring file on disk. Every failure mode is spelled out so the runtime can act on
// it: a missing file is created once and never again, an unreadable or invalid file leaves the last good
// settings in place and is never written over, and the write itself is atomic (temp file + replace) so a
// crash mid-write cannot leave a half-file behind.
public static class ScoringFiles
{
    public const string DirectoryName = "CombatPanel-config";
    public const string FileName = "scoring.json";

    // The path is derived from the DLL's own directory, so the config always travels with the plugin it
    // belongs to instead of landing wherever the process happens to start.
    public static string Resolve(string pluginDirectory)
        => string.IsNullOrEmpty(pluginDirectory) ? null : Path.Combine(pluginDirectory, DirectoryName, FileName);

    public static ScoringSettings Defaults() => new ScoringSettings();

    // created: a default file was written because none existed.
    // error:   the file existed but could not be used; the caller must keep its last good settings.
    public static ScoringSettings Load(string path, out bool created, out string error)
    {
        created = false; error = null;
        if (string.IsNullOrEmpty(path)) { error = "no scoring file path"; return null; }
        try
        {
            if (!File.Exists(path))
            {
                var fresh = Defaults();
                try
                {
                    Write(path, fresh);
                    created = true;
                }
                catch (Exception writeError)
                {
                    // The defaults still score; only the on-disk copy failed, so the mod carries on.
                    error = "default file could not be written: " + writeError.Message;
                }
                return fresh;
            }
            string json = File.ReadAllText(path);
            return ScoringSettings.Parse(json);
        }
        catch (Exception readError)
        {
            error = readError.Message;
            return null;
        }
    }

    // Atomic: the text lands in a sibling temp file first and is moved over the target in one step, so a
    // reader (or the user's editor) never sees a partial file.
    public static void Write(string path, ScoringSettings settings)
    {
        if (string.IsNullOrEmpty(path) || settings == null) return;
        string directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temp = path + ".tmp";
        File.WriteAllText(temp, settings.ToJson());
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
    }

    // Whether the bytes on disk are the ones this build would have written for these settings. Used to
    // decide whether a user edit is pending without ever rewriting the user's file behind their back.
    public static bool Matches(string path, ScoringSettings settings)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || settings == null || !File.Exists(path)) return false;
            return string.Equals(File.ReadAllText(path).Trim(), settings.ToJson().Trim(), StringComparison.Ordinal);
        }
        catch { return false; }
    }
}
