using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace HawChow.GTFOCombatPanel;

[BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
public sealed class Plugin : BasePlugin
{
    internal static Plugin Instance;
    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> ShowPanel, ShowResults, AnchorToFlashlight;
    internal static ConfigEntry<float> PositionX, PositionY, OffsetX, OffsetY, FontSize, PanelOpacity, ResultsOffsetX, ResultsOffsetY;
    internal static ConfigEntry<bool> ShowDamage, ShowFriendlyFire, ShowAccuracy, ShowWeakspot, ShowProjectiles;
    internal static ConfigEntry<KeyCode> ToggleKey;
    internal static ConfigEntry<string> LanguageConfig, DensityConfig;
    internal static ConfigEntry<bool> ShowImpactBoard, SplitKillTiers, StaggerNeedsAttack, HideVanillaFailBlocks;
    internal static ConfigEntry<float> KillMinorMaxHealth, KillEliteMaxHealth, SupportRoleFirepowerBonus, SupportRoleUtilityBonus, MinorKillWeight, EliteKillWeight, BossKillWeight;
    // The settlement board's own wheel-controlled glyph scale, one preference per result page: the two pages
    // have strips of different heights, so the same scale means two different tables.
    internal static ConfigEntry<float> SuccessFontScale, FailureFontScale;
    // （B）固定模式：玩家自己指定字号与「表格底边中点」的位置，作为自适渲染之外的兜底尺子，默认关闭。
    // 关着的时候三个数值一个字节都不读，自适应行为（成功页/失败页各自的条带与滚轮缩放）完全照旧。
    internal static ConfigEntry<bool> FixedLayoutConfig;
    internal static ConfigEntry<float> FixedFontPixelsConfig, FixedAnchorXConfig, FixedAnchorYConfig;
    internal static ConfigEntry<string> WeightsConfig, SupportToolNames;
    // 1.16.0 additions: the result overlay's own switch, the two positive vanilla-block switches (off by
    // default, so the board owns the page), the two support-tool rules, and the user-editable scoring file.
    internal static ConfigEntry<bool> ShowResultOverlay, ShowSuccessArtifacts, ShowSuccessPlayerReports, SupportOtherTools, ScoringFileEnabled;
    internal static ConfigEntry<string> SupportExcludedTools;
    internal static readonly List<PanelColumn> VisibleColumns = new List<PanelColumn>();

    // The last good scoring file. Null means the file was never usable and the .cfg values stand, which is
    // exactly what a build with no JSON does.
    internal static ScoringSettings Scoring { get; private set; }
    internal static string ScoringPath { get; private set; }
    private static bool _persistingToggle;

    public override void Load()
    {
        Instance = this;
        Logger = Log;
        LanguageConfig = Config.Bind("General", "Language", "zh", "面板语言：zh = 中文（默认），en = 英文。zh-CN、cn 与 English 等拼写同样接受。");
        DensityConfig = Config.Bind("General", "Density", "compact", "表格紧凑度：compact（默认，占地最小）、normal、spacious。");
        ShowPanel = Config.Bind("Panel", "ShowPanel", true, "远征期间显示战斗表格。");
        ToggleKey = Config.Bind("Panel", "ToggleKey", KeyCode.F8, "切换 HUD 显隐；None 关闭该快捷键。同一个键在结算页隐藏与显示结算板，因此 F8 在关卡内与结算后的行为一致。");
        ShowDamage = Config.Bind("Columns", "ShowDamage", true, "显示伤害 / 伤害占比列。");
        ShowFriendlyFire = Config.Bind("Columns", "ShowFriendlyFire", true, "显示友伤列。");
        ShowAccuracy = Config.Bind("Columns", "ShowAccuracy", true, "显示命中率列。");
        ShowWeakspot = Config.Bind("Columns", "ShowWeakspot", true, "显示弱点率列。");
        ShowProjectiles = Config.Bind("Columns", "ShowProjectiles", true, "显示弹丸列（总弹丸 / 命中 / 弱点）。");
        AnchorToFlashlight = Config.Bind("Layout", "AnchorToFlashlight", true, "把表格挂在右侧装备栏原生手电筒图标下方。图标的屏幕矩形用 HUD 自己的相机采样，直至稳定后冻结，因此表格在任何分辨率下都跟随图标、但从不继承 HUD 的运动。设 false 改用 PositionX/PositionY。");
        PositionX = Config.Bind("Layout", "PositionX", -0.03f, "回退位置：窗口宽度的分数，负数表示从右边缘起算。仅在 AnchorToFlashlight 关闭或图标无法测量时使用。");
        PositionY = Config.Bind("Layout", "PositionY", 0.365f, "表格位置：窗口高度的分数，负数表示从下边缘起算。默认值正好位于装备栏手电筒图标下方；表格会被夹住，保证每一行都显示在屏幕上。");
        OffsetX = Config.Bind("Layout", "OffsetX", 0f, "加在 PositionX 上的像素偏移；正数向右移。");
        OffsetY = Config.Bind("Layout", "OffsetY", 0f, "加在 PositionY 上的像素偏移；正数向下移。");
        FontSize = Config.Bind("Layout", "FontSize", 17f, "1080p 基准字号（乘 Density 系数之前）。0 表示隐藏文字列，而不是显示无法辨认的小字。");
        PanelOpacity = Config.Bind("Layout", "PanelOpacity", PanelAppearance.InLevelOpacity,
            new ConfigDescription("局内表格自身底板的浓淡：0 = 完全透出游戏画面，1 = 实心底板。底板做成深色是有意的，好让一排数字在明亮场景下也看得清；若表格看起来像压在关卡上的黑块，就把这个数调低。每次绘制都重新读取，所以改这里或一次热重载下一帧就会上屏。结算板有自己的底板取值，不受这个键影响。",
                new AcceptableValueRange<float>(0f, 1f)));
        ShowResults = Config.Bind("Results", "ShowResults", true, "成功/失败结算页上是否显示结算板。这是总开关；下面两个键决定它画什么。");
        ShowResultOverlay = Config.Bind("Results", "ShowResultOverlay", true, "把本模组的板子画在结算页之上。设 false 则结算页保持游戏原样：不画板，也不放回旧统计表。");
        ResultsOffsetX = Config.Bind("Results", "OffsetX", 0f, "结算表相对页面默认位置的像素偏移；正数向右移。");
        ResultsOffsetY = Config.Bind("Results", "OffsetY", 0f, "旧版垂直偏移，保留已有配置；默认统一条带不使用此键。手动位置请用 FixedLayout / FixedAnchorY。");
        // 固定模式：把「板子多大、放在哪里」的主动权交给玩家。自适应那条链仍然是默认，这一组只是兜底/覆盖。
        FixedLayoutConfig = Config.Bind("Results", "FixedLayout", false,
            "固定模式（兜底，默认关闭）：打开后结算板不再按页面自适应，而由下面三个键指定的字号与「表格底边中点」决定大小和位置，成功页与失败页共用这一组值。"
            + "关着时这三个键一个都不读，自适应与两页各自的滚轮缩放照旧。固定模式不等待页面的下界墙（那行等待文字/按钮），"
            + "但每一帧仍会做真实的二维控件相交检查：表格压在一个真可按的控件上时**照样画**（降到 0.55 不透明度降低遮挡，"
            + "日志写清相交的是哪个控件与 opacity，X 错开就不算冲突）；只有画不出来的输入（非数字、越界、量不出表格、"
            + "跑出屏幕）才不画板、不改你的数字、保留原版页面并写一行 fixed-layout-* 诊断；扫描证据不足时淡画但保留原版块。"
            + "固定模式下滚轮不会改字号。例：FixedLayout = true");
        // 三个数值键都不绑 `AcceptableValueRange<float>`：那层范围会在读配置时把 500 静默夹成 240、把 2 静默夹成 6，
        // 而玩家看到的是「我设的值生效了」。校验只在生产里做（`ResultFixedLayout.Valid`）：越界就拒绝整块板、
        // 保留原版页面并写一行 `fixed-layout-input-invalid`，说清是哪一项、有效范围是多少、以及没有被夹过。
        FixedFontPixelsConfig = Config.Bind("Results", "FixedFontPixels", ResultFixedLayout.DefaultFontPixels,
            "固定模式的字号，单位是**实际屏幕像素**（1080p 下 18 左右是正文可读尺寸，1440p 可以用 24）。"
            + "绘制时就是这个像素值：不乘 Density、不乘滚轮缩放、不按页面条带缩小。有效范围 "
            + ResultFixedLayout.MinimumFontPixels.ToString("0") + "-" + ResultFixedLayout.MaximumFontPixels.ToString("0")
            + "（IMGUI 的整数像素）：写 5 就是 5px，不会为了「可读」抬到更大的字号、也不会因此整块拒绘；"
            + "写成非数字、0 或负数、或越界时不夹、不缩，整块保留原版页面并写诊断（日志里 fixed-layout-input-invalid）。"
            + "小数会四舍五入到整数像素，因为 IMGUI 的字号是整数（日志 actualglyph= 就是真正画出来的那个值）。"
            + "例：FixedFontPixels = 18");
        FixedAnchorXConfig = Config.Bind("Results", "FixedAnchorX", ResultFixedLayout.DefaultAnchorX,
            "固定模式下**表格底边中点**的横向位置：窗口宽度的比例。左上角是 (0,0)，X 向右增大；"
            + "0 = 窗口左边缘，1 = 右边缘，0.5 = 水平居中（默认）。表格按这个点居中：左边缘 = FixedAnchorX × 窗口宽度 − 表格宽度 ÷ 2。"
            + "所以表格变宽、玩家变多时，底边中点仍然钉在这条竖线上。允许 0-1；越界或非数字时不夹，保留原版并写诊断。"
            + "例：FixedAnchorX = 0.5");
        FixedAnchorYConfig = Config.Bind("Results", "FixedAnchorY", ResultFixedLayout.DefaultAnchorY,
            "固定模式下**表格底边中点**的纵向位置：窗口高度的比例。左上角是 (0,0)，Y 向下增大；"
            + "0 = 窗口顶端，1 = 底端，0.80 = 屏幕下方五分之一处（默认）。表格向上生长、底边落在这条横线上："
            + "上边缘 = FixedAnchorY × 窗口高度 − 表格高度。所以字号或行数变化时，底边中点不动，只有顶边跟着长。"
            + "允许 0-1；越界或非数字时不夹，保留原版并写诊断。例：FixedAnchorY = 0.80");
        // The board's two manual size preferences. Readable keys, the same section as the rest of the result-page
        // settings, and a range a hand edit has to stay inside. They are **manual**: the wheel no longer writes them.
        // It scrolls the table; a size a player chose in the file is applied to the fitted glyph and clamped to the
        // readable floor, so a preference can never make the table unreadable - it scrolls instead.
        SuccessFontScale = Config.Bind(ResultsZoom.Section, ResultsZoom.SuccessKey, ResultsZoom.Default,
            new ConfigDescription("旧版成功页字号倍率，保留已有配置；统一结算布局不使用此键。手动字号请用 FixedLayout / FixedFontPixels。",
                new AcceptableValueRange<float>(ResultsZoom.Minimum, ResultsZoom.Maximum)));
        FailureFontScale = Config.Bind(ResultsZoom.Section, ResultsZoom.FailureKey, ResultsZoom.Default,
            new ConfigDescription("旧版失败页字号倍率，保留已有配置；统一结算布局不使用此键。手动字号请用 FixedLayout / FixedFontPixels。",
                new AcceptableValueRange<float>(ResultsZoom.Minimum, ResultsZoom.Maximum)));
        // The influence board, and the thirteen metrics it scores.
        ShowImpactBoard = Config.Bind("Results", "ShowImpactBoard", true, "在结算页画影响力看板（每位玩家按 100 分制打分并排名，配一句称号与十三项指标）。设 false 则结算页保持游戏原样：没有旧统计表可回落。在关卡内切换 HUD 的同一个键（ToggleKey，默认 F8）在结算页也用于隐藏与显示这块板，但它从不写这个键：F8 保存的是 ShowPanel 和 ShowResultOverlay，此值始终保持这里设置的样子。");
        ShowSuccessArtifacts = Config.Bind("Results", "ShowSuccessArtifacts", false, "显示成功页原版红晶（神器）块：神器清单、总结行与检查点消耗。默认关闭 - 建议关闭显示以免影响观感 - 好让本模组的板子独占页面；打开后可在板子旁边看到游戏自己的神器数字。打开、换页或离开结算页的瞬间，每一块都会精确放回游戏原来绘制的位置。");
        ShowSuccessPlayerReports = Config.Bind("Results", "ShowSuccessPlayerReports", false, "显示成功页四张原版囚犯报告卡及它们悬挂的锚点。默认关闭 - 建议关闭显示以免影响观感 -，并与神器块分开控制，因为两者是页面上不同的部件；打开时精确还原。");
        HideVanillaFailBlocks = Config.Bind("Results", "HideVanillaFailBlocks", true, "失败页上把神器清单与总结行移出页面并关闭，为板子腾出空间。结算页会一边播动画一边把内容再打开，因此页面在的每一帧都会重新应用这个设置；设 false 则无需重新编译即可恢复原版块。");
        SplitKillTiers = Config.Bind("Scoring", "SplitKillTiers", true, "为旧配置保留的遗留设置。结算板始终把小怪、精英与 BOSS 击杀显示在三行带明确标签的行上；评分不变。");
        KillMinorMaxHealth = Config.Bind("Scoring", "KillMinorMaxHealth", 120f, "生命上限低于此值的敌人按小怪击杀计。按敌人自己的生命上限分级，模改敌人也按其真实属性分级。");
        KillEliteMaxHealth = Config.Bind("Scoring", "KillEliteMaxHealth", 1000f, "生命上限低于此值的敌人按精英击杀计；达到或超过则按 BOSS 计。");
        MinorKillWeight = Config.Bind("Scoring", "MinorKillWeight", (float)ImpactScoring.DefaultMinorKillWeight, "击杀行计分时一个小怪击杀的价值。三档权重彼此相对，且必须按档位顺序非递减（小怪 <= 精英 <= BOSS）；发布默认是 1 : 2 : 3 比例。仅在评分文件关闭或不可读时使用——评分文件才是可编辑的界面。");
        EliteKillWeight = Config.Bind("Scoring", "EliteKillWeight", (float)ImpactScoring.DefaultEliteKillWeight, "一个精英击杀的价值。见 MinorKillWeight；必须不小于 MinorKillWeight 且不大于 BossKillWeight。");
        BossKillWeight = Config.Bind("Scoring", "BossKillWeight", (float)ImpactScoring.DefaultBossKillWeight, "一个 BOSS 击杀的价值。见 MinorKillWeight；必须不小于 EliteKillWeight。");
        WeightsConfig = Config.Bind("Scoring", "Weights", ImpactScoring.DefaultWeightsText, "十三项指标的备用权重，按看板顺序（总伤害、硬直、击杀、救援、弱点率、命中率、补给、工具效率、独自存活、倒地、友伤、近战击杀、触手击杀）。仅在 scoring.json 关闭或不可读时使用；非法列表回退默认值。");

        // A saved configuration keeps whatever it holds. Published default lists migrate to the current
        // thirteen weights; a custom eleven-item list keeps its values and receives the two new suffixes.
        if (ImpactScoring.IsLegacyDefaultWeights(WeightsConfig.Value))
        {
            WeightsConfig.Value = ImpactScoring.DefaultWeightsText;
            Logger.LogInfo($"[DIAG] scoring weights migrated from a published legacy default to the thirteen-metric defaults ({ImpactScoring.DefaultWeightsText}); melee and Scout kills now score separately.");
        }
        SupportRoleFirepowerBonus = Config.Bind("Scoring", "SupportRoleFirepowerBonus", 1.15f,
            new ConfigDescription("辅助玩家火力指标上的乘数——只作用于总伤害与硬直，绝不作用于瞄准或击杀。1.0 表示关闭；无论文件里写多少，上限都是 1.3。",
                new AcceptableValueRange<float>(1f, 1.3f)));
        SupportRoleUtilityBonus = Config.Bind("Scoring", "SupportRoleUtilityBonus", 1.0f,
            new ConfigDescription("辅助玩家效用指标上的乘数（工具效率、补给、救援、独自存活）。默认 1.0；无论文件里写多少，上限都是 1.3。",
                new AcceptableValueRange<float>(1f, 1.3f)));
        SupportToolNames = Config.Bind("Scoring", "SupportToolNames", SupportTools.DefaultNames, "逗号分隔的工具名，不区分大小写、按子串匹配装备的职业工具，命中即把该玩家记为辅助：默认是生物追踪仪、胶枪、C-Foam 与开锁器。");
        SupportExcludedTools = Config.Bind("Scoring", "SupportExcludedTools", SupportTools.DefaultExcluded, "逗号分隔的工具名，无论如何命名都永不算辅助套件：默认是哨戒炮与绊雷，因为它们是替主人开枪的，而不是主人为套件让出自己那把枪换来的。");
        SupportOtherTools = Config.Bind("Scoring", "SupportOtherTools", true, "把任何其它非空工具都当作辅助套件，模改的无伤害工具无需点名即可纳入。排除名单仍然优先。");
        ScoringFileEnabled = Config.Bind("Scoring", "ScoringFile", true, "从游戏自身持久数据目录读取 CombatPanel-config/scoring.json（仅当那里没有时才会一次性导入插件目录里的副本）：十三项权重、启用指标、血量阈值、救援下限、命中率置信、角色加成、睡眠击杀倍率与每句结算称号。ToolEfficiency 显示工具使用数据，但评分仍然是扫描首次标记数。FriendlyFire 惩罚友伤伤害与致倒事故。MeleeKills 与 ScoutKills 按其击杀数计分；近战伤害仍显示在既有 GearMelee 行。SleepingKillMultiplier（1-10，默认 2）奖励打在仍沉睡敌人身上的击杀；状态读不出的击杀按清醒计。Titles 为每句称号携带一对 Title/Description：三档击杀与总伤害各有独立称号，负面称号（Downs、FriendlyFire）按惩罚严重度授予全队最严重者。与出厂完全一致的条目继续跟随 Language 设置。无效文件保留上一次有效副本并回退 .cfg 值；用户文件从不被覆盖——包更新也绝不替换稳定副本——两个来源都在主线程热重载。");

        Config.SettingChanged += (sender, args) => { if (!_persistingToggle) ApplyConfig(); };
        Config.ConfigReloaded += (sender, args) => ApplyConfig();
        // The scoring file has to be resolved before the watcher is armed, because the watcher watches it too.
        LoadScoringFile();
        ResultsSaver.Initialize();
        ConfigWatcher.Watch(Config, ScoringPath);
        ApplyConfig();
        StaggerNeedsAttack = Config.Bind("Scoring", "StaggerNeedsAttack", true, "仅当伤害落下的瞬间敌人的攻击正在进行且尚未结束时才计一次硬直。击杀绝不算作打断——无论游戏为它报告哪种受击反应——夺走敌人最后一滴血的一击也不算。设 false 则计入任何到达该行的受击反应。");
        var harmony = new Harmony(PluginInfo.Guid);

        harmony.PatchAll();
        // 1.19.41: the medical pack bracket is resolved by name, so an interop drift reports an unavailable
        // metric instead of silently measuring something else. Installed after `PatchAll` so its own diagnostic
        // line is the last word on what this build actually hooked.
        PackApplicationPatches.Install(harmony);
        EwcProjectileBridge.EnsureInitialized();
        AddComponent<PanelBehaviour>();
        CombatSync.Register();
        foreach (MethodBase method in harmony.GetPatchedMethods())
        {
            var info = Harmony.GetPatchInfo(method);
            Logger.LogInfo($"[DIAG] patch {method.DeclaringType.Name}.{method.Name}: pre={info.Prefixes.Count},post={info.Postfixes.Count},final={info.Finalizers.Count}");
        }
        Logger.LogInfo($"{PluginInfo.Name} {PluginInfo.Version} loaded; language={CombatStrings.Language}; density={Density.Name}; columns={DescribeColumns()}; panelOpacity={DescribePanelOpacity()}; the table is drawn by IMGUI in window pixels, so view motion and HUD parallax cannot move it; config hot reload watching {ConfigWatcher.WatchedPath} and {ScoringPath}.");
    }

    internal static PanelDensity Density => PanelDensity.FromName(DensityConfig != null ? DensityConfig.Value : null);

    // The plate's value as the log and the config comments describe it: the number actually in force for this
    // frame, so a line in the log is enough to tell whether an edit took. It is the same call the draw makes.
    internal static string DescribePanelOpacity()
        => PanelAppearance.Plate(PanelOpacity != null ? PanelOpacity.Value : float.NaN, false)
            .ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    // The directory this plugin's DLL lives in. It is the source of the one-time scoring/record imports and the
    // fallback location when the game's persistent data path is unusable; the stable copies live under the
    // persistent data path, in their own CombatPanel sub folders (never a naked file).
    internal static string PluginDirectory()
    {
        try
        {
            string location = typeof(Plugin).Assembly.Location;
            if (!string.IsNullOrEmpty(location)) return Path.GetDirectoryName(location);
        }
        catch { }
        try { return Paths.PluginPath; } catch { return null; }
    }

    // Reads the scoring file, from the stable location the pure ScoringFileLocation policy resolves: the
    // player's copy in the game's persistent data path wins; the plugin folder's copy is imported once when
    // that root has none; the shipped defaults are written once to the stable root when nothing exists
    // anywhere. A package install or update therefore never replaces an edited scoring file. An invalid one
    // is reported and the last good copy is kept (the file itself is never written over). Called on the main
    // thread; the policy's own line states which file was used.
    internal static void LoadScoringFile()
    {
        string persistentDataPath = null;
        try { persistentDataPath = Application.persistentDataPath; } catch { }
        ScoringFilePlan plan = ScoringFileLocation.Resolve(persistentDataPath, PluginDirectory());
        ScoringPath = plan.Path;
        if (ScoringFileEnabled != null && !ScoringFileEnabled.Value)
        {
            Scoring = null;
            ApplyKillWeights();
            return;
        }
        if (!string.IsNullOrEmpty(plan.Message)) Logger?.LogInfo(plan.Message);
        ScoringSettings loaded = null;
        string error = null;
        bool created = false;
        try { loaded = ScoringFiles.Load(ScoringPath, out created, out error); }
        catch (Exception exception) { error = exception.Message; }
        if (loaded == null)
        {
            if (!string.IsNullOrEmpty(error)) Logger?.LogWarning("[DIAG] the scoring file could not be used, so the last good settings (or the .cfg weights) stand and the file was not written over: " + error);
            return;
        }
        Scoring = loaded;
        ImpactScoring.RescueFloor = loaded.RescueFloor;
        ImpactScoring.AccuracyConfidence = loaded.AccuracyConfidencePellets;
        ImpactScoring.SetFriendlyFireGrace(loaded.FriendlyFireGrace);
        ApplyKillWeights();
        if (created) Logger?.LogInfo($"[DIAG] scoring file created once from this release's defaults at {ScoringPath}; every weight, threshold and support rule can now be edited there without recompiling.");
        if (!string.IsNullOrEmpty(error)) Logger?.LogWarning("[DIAG] the scoring file was written but could not be reported cleanly: " + error);
    }

    // The weights the settlement board scores with: the scoring file when it is usable, the .cfg list
    // otherwise, and the published defaults when neither parses.
    internal static double[] ScoringWeights()
        => Scoring != null ? Scoring.EffectiveWeights() : ImpactScoring.ParseWeights(WeightsConfig != null ? WeightsConfig.Value : null);

    // The three kill-tier values in force: the scoring file's when it is usable, the .cfg entries
    // otherwise, and the published defaults when neither is available.
    internal static void ApplyKillWeights()
    {
        if (Scoring != null)
        {
            ImpactScoring.SetKillWeights(Scoring.MinorKillWeight, Scoring.EliteKillWeight, Scoring.BossKillWeight);
            ImpactScoring.SetSleepingKillMultiplier(Scoring.SleepingKillMultiplier);
            ImpactScoring.SetSleepingKillMultipliers(Scoring.SleepingMeleeKillMultiplier, Scoring.SleepingScoutKillMultiplier);
            return;
        }
        ImpactScoring.SetKillWeights(
            MinorKillWeight != null ? MinorKillWeight.Value : (float)ImpactScoring.DefaultMinorKillWeight,
            EliteKillWeight != null ? EliteKillWeight.Value : (float)ImpactScoring.DefaultEliteKillWeight,
            BossKillWeight != null ? BossKillWeight.Value : (float)ImpactScoring.DefaultBossKillWeight);
        // The sleeping multiplier has no .cfg entry of its own: with the file off or unusable the published
        // default stands, which is what a build carrying no file at all scores with.
        ImpactScoring.SetSleepingKillMultiplier(ImpactScoring.DefaultSleepingKillMultiplier);
        ImpactScoring.SetSleepingKillMultipliers(ImpactScoring.DefaultSleepingKillMultiplier, ImpactScoring.DefaultSleepingKillMultiplier);
        // The friendly-fire grace has no .cfg entry either, so the fallback is the published default - the
        // same rule the sleeping multipliers follow, and the same figure a build with no file scores with.
        ImpactScoring.SetFriendlyFireGrace(ImpactScoring.DefaultFriendlyFireGrace);
    }

    // The settlement sentences in force: the scoring file's wording when it is usable, the shipped wording
    // otherwise. An entry the file leaves exactly as published is not pinned, so it keeps following the
    // language setting; a sentence the file really changes is the user's from then on. The explanation line
    // under each sentence follows the same rule, so both halves are installed from this one place and a
    // language change or a scoring-file edit can never leave one half behind.
    internal static void ApplyTitles()
    {
        if (Scoring != null)
        {
            Scoring.ApplyTitles();
            Scoring.ApplyReasons();
            return;
        }
        CombatStrings.ResetTitleOverrides();
        CombatStrings.ClearReasonOverrides();
    }

    // Whether an equipped tool marks its owner as support: the scoring file's rules when it is usable,
    // the .cfg lists otherwise.
    internal static bool IsSupportTool(string tool)
    {
        if (Scoring != null) return Scoring.IsSupport(tool);
        return SupportTools.IsSupport(new[] { tool },
            SupportToolNames != null ? SupportToolNames.Value : SupportTools.DefaultNames,
            SupportExcludedTools != null ? SupportExcludedTools.Value : SupportTools.DefaultExcluded,
            SupportOtherTools == null || SupportOtherTools.Value);
    }

    // The artifact block and the prisoner report cards are shown only when their switch is on: the board
    // is meant to own the page, so the default leaves the vanilla figures away and one switch each brings
    // them back without recompiling.
    internal static bool HideVanillaArtifacts => OverlayPolicy.HidesArtifacts(ShowSuccessArtifacts == null || ShowSuccessArtifacts.Value);
    internal static bool HideVanillaReports => OverlayPolicy.HidesReports(ShowSuccessPlayerReports == null || ShowSuccessPlayerReports.Value);

    // F8 is the one control that has to survive a restart: what it flipped goes back to the .cfg and the
    // file is saved, so both the in-level table and the settlement board come back the way they were left.
    // The decision of what to write is the pure `TogglePolicy`, asserted headlessly; this only carries it
    // out.
    internal static void PersistToggle(bool visible)
    {
        ConfigFile file = Instance?.Config;
        bool autoSave = file != null && file.SaveOnConfigSet;
        try
        {
            // Visibility is not a geometry change. Batch the two flags without firing a full
            // rebuild or doing two automatic disk writes on the input frame.
            _persistingToggle = true;
            if (file != null) file.SaveOnConfigSet = false;
            ToggleAction action = TogglePolicy.Apply(visible);
            if (ShowPanel != null && ShowPanel.Value != action.ShowPanel) ShowPanel.Value = action.ShowPanel;
            if (ShowResultOverlay != null && ShowResultOverlay.Value != action.ShowResultOverlay) ShowResultOverlay.Value = action.ShowResultOverlay;
            if (action.Save && file != null)
            {
                file.Save();
                ConfigWatcher.RecordOwnSave();
            }
        }
        catch (Exception error)
        {
            Logger?.LogWarning("the F8 toggle could not be written back to the config file: " + error.Message);
        }
        finally
        {
            if (file != null) file.SaveOnConfigSet = autoSave;
            _persistingToggle = false;
        }
    }

    // The manual size multiplier in force for one result page. Read on every rebuild and never cached, so a hand edit
    // of the file - or the hot reload that follows it - is on screen the next frame, and a value that could not be read
    // at all falls back to the published 1.0 rather than to something nobody asked for.
    //
    // This is a preference and nothing else: the wheel does not write it (the wheel scrolls the table), and the fit
    // applies it on top of the readable floor (`ResultsScroll.FontFor`), so a multiplier can never be the reason a
    // table became unreadable - the rows that no longer fit are scrolled to instead.
    internal static float ResultFontScale(bool succeeded)
        => ResultsZoom.Clamp(succeeded ? SuccessFontScale != null ? SuccessFontScale.Value : ResultsZoom.Default
                                       : FailureFontScale != null ? FailureFontScale.Value : ResultsZoom.Default);

    // Rebuilt on every config change. The player name is the row label and is always
    // present; every data column is optional.
    internal static void ApplyConfig()
    {
        CombatStrings.Language = LanguageConfig != null ? LanguageConfig.Value : "zh";
        var columns = PanelColumns.Compose(
            ShowDamage == null || ShowDamage.Value,
            ShowFriendlyFire == null || ShowFriendlyFire.Value,
            ShowAccuracy == null || ShowAccuracy.Value,
            ShowWeakspot == null || ShowWeakspot.Value,
            ShowProjectiles == null || ShowProjectiles.Value);
        VisibleColumns.Clear();
        VisibleColumns.AddRange(columns);
        PanelBehaviour.InvalidateLayout();
        // A configuration change may have moved the scoring, the thresholds or the vanilla-block flags, so
        // the settlement board drops its cached layout. An ordinary counter update does not come through
        // here, so the board keeps the position and size it was measured with while the figures change.
        ResultsDisplay.Invalidate();
        // A .cfg edit of the three kill-tier weights takes effect on the next hot reload even when the
        // scoring file is off; when it is on, ApplyKillWeights reads the file's values instead. The
        // settlement sentences are re-installed here too, because a language change and a scoring-file
        // edit both arrive through this one call.
        ApplyKillWeights();
        ApplyTitles();
    }

    private static string DescribeColumns() => string.Join("+", VisibleColumns);

    // Called from the main thread. The watchers only arm a flag; the reload itself, and every Unity object
    // touch, stays on the main thread.
    internal static void PollHotReload()
    {
        if (!ConfigWatcher.ConsumeReload()) return;
        try
        {
            ConfigFile file = Instance != null ? Instance.Config : null;
            if (file != null) file.Reload();
            LoadScoringFile();
            ApplyConfig();
            Logger?.LogInfo($"[HOTRELOAD] config reloaded; language={CombatStrings.Language}; density={Density.Name}; columns={DescribeColumns()}; panelOpacity={DescribePanelOpacity()}; scoring weights={(Scoring != null ? "scoring.json" : "cfg fallback")}; the table is rebuilt and repositioned from the new settings.");
        }
        catch (Exception error)
        {
            Logger?.LogWarning("Config hot reload failed: " + error.Message);
        }
    }
}
