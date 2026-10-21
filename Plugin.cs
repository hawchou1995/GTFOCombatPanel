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
        LanguageConfig = Config.Bind("General", "Language", "zh", "Panel language: zh = Chinese (default), en = English. zh-CN, cn and English are accepted too.");
        DensityConfig = Config.Bind("General", "Density", "compact", "Table compactness: compact (default, smallest footprint), normal, spacious.");
        ShowPanel = Config.Bind("Panel", "ShowPanel", true, "Show the combat table during an expedition.");
        ToggleKey = Config.Bind("Panel", "ToggleKey", KeyCode.F8, "Toggle HUD; None disables the shortcut. The same key hides and shows the settlement board on the result screens, so F8 behaves the same in a level and after one.");
        ShowDamage = Config.Bind("Columns", "ShowDamage", true, "Show the damage / damage-share column.");
        ShowFriendlyFire = Config.Bind("Columns", "ShowFriendlyFire", true, "Show the friendly fire column.");
        ShowAccuracy = Config.Bind("Columns", "ShowAccuracy", true, "Show the hit rate column.");
        ShowWeakspot = Config.Bind("Columns", "ShowWeakspot", true, "Show the weakspot rate column.");
        ShowProjectiles = Config.Bind("Columns", "ShowProjectiles", true, "Show the pellet column (total / hit / weakspot).");
        AnchorToFlashlight = Config.Bind("Layout", "AnchorToFlashlight", true, "Hang the table under the native flashlight icon in the right-hand gear column. The icon's screen rectangle is sampled through the HUD's own camera until it settles, then frozen, so the table follows the icon at any resolution but never inherits the HUD's motion. Set false to use PositionX/PositionY instead.");
        PositionX = Config.Bind("Layout", "PositionX", -0.03f, "Fallback position: a fraction of the window width, or negative to measure from the right edge. Only used when AnchorToFlashlight is off or the icon cannot be measured.");
        PositionY = Config.Bind("Layout", "PositionY", 0.365f, "Table position: a fraction of the window height, or negative to measure from the bottom edge. The default sits just below the flashlight icon in the gear column; the table is clamped so every row stays on screen.");
        OffsetX = Config.Bind("Layout", "OffsetX", 0f, "Pixel offset added to PositionX; positive moves right.");
        OffsetY = Config.Bind("Layout", "OffsetY", 0f, "Pixel offset added to PositionY; positive moves down.");
        FontSize = Config.Bind("Layout", "FontSize", 17f, "Glyph size at 1080p, before the Density factor. 0 hides the columns of text rather than showing unreadable ones.");
        PanelOpacity = Config.Bind("Layout", "PanelOpacity", PanelAppearance.InLevelOpacity,
            new ConfigDescription("How heavy the in-level table's own plate is: 0 = the game shows through untouched, 1 = a solid plate. The plate is dark on purpose so a row of figures survives a bright scene; lower this number if the table reads as a black box over the level. Read on every draw, so an edit here or a hot reload is on screen the next frame. The settlement board has its own approved plate and is not affected by this key.",
                new AcceptableValueRange<float>(0f, 1f)));
        ShowResults = Config.Bind("Results", "ShowResults", true, "Show the settlement board on success/failure screens at all. This is the master switch; the two below choose what it draws.");
        ShowResultOverlay = Config.Bind("Results", "ShowResultOverlay", true, "Draw this mod's board over the result pages. Set false to leave the vanilla result screens exactly as the game drew them, with no board and no legacy table put in their place.");
        ResultsOffsetX = Config.Bind("Results", "OffsetX", 0f, "Pixel offset of the result table from its default place on the page; positive moves right.");
        ResultsOffsetY = Config.Bind("Results", "OffsetY", 0f, "Pixel offset of the result table from its default place on the page; positive moves down.");
        // The influence board, and the thirteen metrics it scores.
        ShowImpactBoard = Config.Bind("Results", "ShowImpactBoard", true, "Draw the influence board (every player scored out of 100, ranked, with a sentence and thirteen metrics) on result screens. Set false and the result page is left exactly as the game drew it: there is no older statistics table to fall back to. The same key that toggles the HUD in a level (ToggleKey, F8 by default) toggles this board on a result screen and writes the new value back to this file.");
        ShowSuccessArtifacts = Config.Bind("Results", "ShowSuccessArtifacts", false, "Show the vanilla red crystal (artifact) block on the success screen: the artifact inventory, its summary line and the checkpoint usage. Off by default - 建议关闭显示以免影响观感 - so this mod's board owns the page; switch it on to see the game's own artifact figures beside the board. Every block is put back exactly where the game drew it the moment this is switched on, the page changes or the results screen is left.");
        ShowSuccessPlayerReports = Config.Bind("Results", "ShowSuccessPlayerReports", false, "Show the four vanilla prisoner report cards on the success screen, with the anchors they hang on. Off by default - 建议关闭显示以免影响观感 - and kept separate from the artifact block because the two are different pieces of the page; restored exactly when switched on.");
        HideVanillaFailBlocks = Config.Bind("Results", "HideVanillaFailBlocks", true, "On the failure screen, move the artifact inventory and its summary line off the page and switch them off, so the board has the room. The result pages animate their content in and switch it back on as they go, so this is re-applied on every frame the page is up. Set false to put the vanilla blocks back without recompiling.");
        SplitKillTiers = Config.Bind("Scoring", "SplitKillTiers", true, "Legacy setting retained for older configurations. Settlement boards always show Minor, Elite and Boss kills on three explicitly labelled rows; scoring is unchanged.");
        KillMinorMaxHealth = Config.Bind("Scoring", "KillMinorMaxHealth", 120f, "An enemy whose maximum health is below this counts as a minor kill. Graded from the enemy's own health, so a modded enemy is graded by what it is.");
        KillEliteMaxHealth = Config.Bind("Scoring", "KillEliteMaxHealth", 1000f, "An enemy whose maximum health is below this counts as an elite kill; at or above it counts as a boss.");
        MinorKillWeight = Config.Bind("Scoring", "MinorKillWeight", (float)ImpactScoring.DefaultMinorKillWeight, "What one minor kill is worth when the kill row is scored. The three tier weights are relative to each other and must be non-decreasing in tier order (minor <= elite <= boss); the published default is the 1 : 2 : 3 ratio. Used only when the scoring file is switched off or unreadable - the scoring file is the editable surface.");
        EliteKillWeight = Config.Bind("Scoring", "EliteKillWeight", (float)ImpactScoring.DefaultEliteKillWeight, "What one elite kill is worth. See MinorKillWeight; must be at least MinorKillWeight and at most BossKillWeight.");
        BossKillWeight = Config.Bind("Scoring", "BossKillWeight", (float)ImpactScoring.DefaultBossKillWeight, "What one boss kill is worth. See MinorKillWeight; must be at least EliteKillWeight.");
        WeightsConfig = Config.Bind("Scoring", "Weights", ImpactScoring.DefaultWeightsText, "Fallback weights of thirteen metrics in board order (total damage, stagger, kills, revives, weakspot rate, hit rate, supply, tool efficiency, lone survivor, downs, friendly fire, melee kills, Scout kills). Used only when scoring.json is off or unreadable; invalid lists fall back to the defaults.");

        // A saved configuration keeps whatever it holds. Published default lists migrate to the current
        // thirteen weights; a custom eleven-item list keeps its values and receives the two new suffixes.
        if (ImpactScoring.IsLegacyDefaultWeights(WeightsConfig.Value))
        {
            WeightsConfig.Value = ImpactScoring.DefaultWeightsText;
            Logger.LogInfo($"[DIAG] scoring weights migrated from a published legacy default to the thirteen-metric defaults ({ImpactScoring.DefaultWeightsText}); melee and Scout kills now score separately.");
        }
        SupportRoleFirepowerBonus = Config.Bind("Scoring", "SupportRoleFirepowerBonus", 1.15f,
            new ConfigDescription("Multiplier applied to a support player's firepower metrics - total damage and stagger only, never aim or kills. 1.0 disables it; the ceiling is 1.3 whatever the file says.",
                new AcceptableValueRange<float>(1f, 1.3f)));
        SupportRoleUtilityBonus = Config.Bind("Scoring", "SupportRoleUtilityBonus", 1.0f,
            new ConfigDescription("Multiplier applied to a support player's utility metrics (tool efficiency, supply, revives, lone survivor). 1.0 by default; the ceiling is 1.3 whatever the file says.",
                new AcceptableValueRange<float>(1f, 1.3f)));
        SupportToolNames = Config.Bind("Scoring", "SupportToolNames", SupportTools.DefaultNames, "Comma separated tool names, matched case insensitively as a substring against the equipped class tool, that mark a player as support: the bio tracker, glue, C-Foam and the lock melter by default.");
        SupportExcludedTools = Config.Bind("Scoring", "SupportExcludedTools", SupportTools.DefaultExcluded, "Comma separated tool names that are never a support kit however they are named: sentries and tripmines by default, because they shoot for their owner rather than being a kit the owner gave their gun up for.");
        SupportOtherTools = Config.Bind("Scoring", "SupportOtherTools", true, "Treat any other non-empty tool as a support kit, so a modded non-damaging tool is included without having to be named. The exclusion list still wins.");
        ScoringFileEnabled = Config.Bind("Scoring", "ScoringFile", true, "Read CombatPanel-config/scoring.json next to this DLL: thirteen weights, enabled metrics, health thresholds, rescue floor, accuracy confidence, role bonuses, the sleeping-kill multiplier and every settlement sentence. ToolEfficiency displays tool-use figures but its score remains scan first-marks. FriendlyFire penalizes friendly damage and lethal incidents. MeleeKills and ScoutKills score their kill counts; melee damage is displayed from the existing GearMelee row. SleepingKillMultiplier (1-10, default 2) rewards a kill landed on an enemy that was still asleep; a kill whose state could not be read is worth an awake one. Titles carries one Title/Description pair per sentence: the three kill classes and total damage each have their own, and a negative sentence (Downs, FriendlyFire) goes to the squad's worst by penalty severity. An entry left exactly as published keeps following the Language setting. Invalid files keep the last good copy and use the .cfg fallback; user files are never overwritten and both sources hot reload on the main thread.");

        Config.SettingChanged += (sender, args) => { if (!_persistingToggle) ApplyConfig(); };
        Config.ConfigReloaded += (sender, args) => ApplyConfig();
        // The scoring file has to be resolved before the watcher is armed, because the watcher watches it too.
        LoadScoringFile();
        ConfigWatcher.Watch(Config, ScoringPath);
        ApplyConfig();
        var harmony = new Harmony(PluginInfo.Guid);
        StaggerNeedsAttack = Config.Bind("Scoring", "StaggerNeedsAttack", true, "Only credit a stagger when the enemy's attack was under way and not yet finished at the moment the damage landed. A kill is never counted as an interrupt, whichever hit react the game reports for it, and neither is a blow that takes the last of the enemy's health. Set false to credit any hit react that reaches the row.");

        harmony.PatchAll();
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

    // The directory this plugin's DLL lives in. The scoring file is resolved from here so it always sits
    // beside the plugin it belongs to, in its own CombatPanel-config sub folder (never a naked file).
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

    // Reads the scoring file. A missing file is created once from the defaults; an invalid one is reported
    // and the last good copy is kept (the file itself is never written over). Called on the main thread.
    internal static void LoadScoringFile()
    {
        ScoringPath = ScoringFiles.Resolve(PluginDirectory());
        if (ScoringFileEnabled != null && !ScoringFileEnabled.Value)
        {
            Scoring = null;
            ApplyKillWeights();
            return;
        }
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
            return;
        }
        ImpactScoring.SetKillWeights(
            MinorKillWeight != null ? MinorKillWeight.Value : (float)ImpactScoring.DefaultMinorKillWeight,
            EliteKillWeight != null ? EliteKillWeight.Value : (float)ImpactScoring.DefaultEliteKillWeight,
            BossKillWeight != null ? BossKillWeight.Value : (float)ImpactScoring.DefaultBossKillWeight);
        // The sleeping multiplier has no .cfg entry of its own: with the file off or unusable the published
        // default stands, which is what a build carrying no file at all scores with.
        ImpactScoring.SetSleepingKillMultiplier(ImpactScoring.DefaultSleepingKillMultiplier);
    }

    // The settlement sentences in force: the scoring file's wording when it is usable, the shipped wording
    // otherwise. An entry the file leaves exactly as published is not pinned, so it keeps following the
    // language setting; a sentence the file really changes is the user's from then on.
    internal static void ApplyTitles()
    {
        if (Scoring != null) Scoring.ApplyTitles();
        else CombatStrings.ResetTitleOverrides();
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
