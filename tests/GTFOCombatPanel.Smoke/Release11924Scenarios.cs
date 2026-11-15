using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HawChow.GTFOCombatPanel;

// 1.19.24: the whole .cfg surface speaks Chinese. Every Config.Bind description in `Plugin.cs` (40) and
// `CombatSync.cs` (2) is read back out of the production sources - not out of a duplicate list - and held
// to the properties a player actually sees in the `.cfg`: one physical line, at least one CJK glyph (so no
// untranslated English sentence can survive), never byte-identical to any of the former English sentences,
// and free of any routing through `CombatStrings`, which is bound before the language setting is applied
// and would therefore always answer Chinese. The two success-page switches keep their 「建议关闭显示以免影响观感」
// hint, and `scoring.json`'s `Guidance` teaches its own edits without a schema change.
internal static class Release11924Scenarios
{
    public static int Run()
    {
        int failures = 0;
        RunInto((condition, description) =>
        {
            if (condition) return;
            failures++;
            Console.WriteLine("FAIL: " + description);
        });
        return failures;
    }

    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        EveryConfigDescriptionIsChinese(check);
        TheScoringGuidanceTeachesHowToEdit(check);
        Wiring(check);
    }

    // The exact former English sentences, hard-coded here so a hand-rolled revert is caught verbatim even
    // if it somehow carried a CJK character past the glyph check above. The two multi-fragment squad
    // descriptions are their joined form, which is what the extraction below produces.
    private static readonly string[] FormerEnglishSentences =
    {
        "Panel language: zh = Chinese (default), en = English. zh-CN, cn and English are accepted too.",
        "Show the combat table during an expedition.",
        "Toggle HUD; None disables the shortcut. The same key hides and shows the settlement board on the result screens, so F8 behaves the same in a level and after one.",
        "Show the damage / damage-share column.",
        "Hang the table under the native flashlight icon in the right-hand gear column. The icon's screen rectangle is sampled through the HUD's own camera until it settles, then frozen, so the table follows the icon at any resolution but never inherits the HUD's motion. Set false to use PositionX/PositionY instead.",
        "Glyph scale of the settlement board on the SUCCESS screen, changed with the mouse wheel over the board and remembered on its own. 1.0 is the size the page's own strip allows - the strip between the completion section and the button that sends the squad home. Below 1 draws a smaller board; above 1 is clamped back onto the strip's own size, so the wheel can never make the board cover that button. One notch is one step of 0.1.",
        "Draw the influence board (every player scored out of 100, ranked, with a sentence and thirteen metrics) on result screens. Set false and the result page is left exactly as the game drew it: there is no older statistics table to fall back to. The same key that toggles the HUD in a level (ToggleKey, F8 by default) also hides and shows this board on a result screen, but it never writes this key: F8 stores ShowPanel and ShowResultOverlay, and this value stays exactly as it is set here.",
        "Show the vanilla red crystal (artifact) block on the success screen: the artifact inventory, its summary line and the checkpoint usage. Off by default - 建议关闭显示以免影响观感 - so this mod's board owns the page; switch it on to see the game's own artifact figures beside the board. Every block is put back exactly where the game drew it the moment this is switched on, the page changes or the results screen is left.",
        "Show the four vanilla prisoner report cards on the success screen, with the anchors they hang on. Off by default - 建议关闭显示以免影响观感 - and kept separate from the artifact block because the two are different pieces of the page; restored exactly when switched on.",
        "On the failure screen, move the artifact inventory and its summary line off the page and switch them off, so the board has the room. The result pages animate their content in and switch it back on as they go, so this is re-applied on every frame the page is up. Set false to put the vanilla blocks back without recompiling.",
        "Legacy setting retained for older configurations. Settlement boards always show Minor, Elite and Boss kills on three explicitly labelled rows; scoring is unchanged.",
        "An enemy whose maximum health is below this counts as a minor kill. Graded from the enemy's own health, so a modded enemy is graded by what it is.",
        "Fallback weights of thirteen metrics in board order (total damage, stagger, kills, revives, weakspot rate, hit rate, supply, tool efficiency, lone survivor, downs, friendly fire, melee kills, Scout kills). Used only when scoring.json is off or unreadable; invalid lists fall back to the defaults.",
        "Multiplier applied to a support player's firepower metrics - total damage and stagger only, never aim or kills. 1.0 disables it; the ceiling is 1.3 whatever the file says.",
        "Comma separated tool names, matched case insensitively as a substring against the equipped class tool, that mark a player as support: the bio tracker, glue, C-Foam and the lock melter by default.",
        "Read CombatPanel-config/scoring.json from the game's own persistent data path (the plugin folder's copy is imported once if that is the only one): thirteen weights, enabled metrics, health thresholds, rescue floor, accuracy confidence, role bonuses, the sleeping-kill multiplier and every settlement sentence. ToolEfficiency displays tool-use figures but its score remains scan first-marks. FriendlyFire penalizes friendly damage and lethal incidents. MeleeKills and ScoutKills score their kill counts; melee damage is displayed from the existing GearMelee row. SleepingKillMultiplier (1-10, default 2) rewards a kill landed on an enemy that was still asleep; a kill whose state could not be read is worth an awake one. Titles carries one Title/Description pair per sentence: the three kill classes and total damage each have their own, and a negative sentence (Downs, FriendlyFire) goes to the squad's worst by penalty severity. An entry left exactly as published keeps following the Language setting. Invalid files keep the last good copy and use the .cfg fallback; user files are never overwritten - a package update never replaces the stable copy - and both sources hot reload on the main thread.",
        "Only credit a stagger when the enemy's attack was under way and not yet finished at the moment the damage landed. A kill is never counted as an interrupt, whichever hit react the game reports for it, and neither is a blow that takes the last of the enemy's health. Set false to credit any hit react that reaches the row.",
        "Share this machine's rows with every player in the squad by default. The packets ride GTFO-API's own network event, which every other mod on that transport already uses in lobbies that hold unmodded players: a machine that runs no mod has no handler for it and drops the bytes, exactly as it drops every other mod packet it receives. Set this to false to share only with players named in AllowlistedPeers, or with those that have themselves sent this machine a packet of this build's protocol - that pair works, but two machines that both wait for the other to speak first share nothing, which is what this build repairs.",
        "SteamID64 lookups of players known to run CombatPanel, separated by commas. Only consulted when ShareWithEveryPlayer is false: then nothing is sent to a player who is not named here until that player has itself sent this machine a packet of this mod's protocol, and a greeting counts as one. The number is the one the game's own lobby associates with the player; a value that is not a plain decimal number is ignored rather than guessed at.",
    };

    // The 42 sites, one per Config.Bind description. `Token` is the source spelling that uniquely precedes
    // the description: the quoted key name, or the named constant for the two wheel preferences. The two
    // `OffsetX`/`OffsetY` keys exist in both [Layout] and [Results], so `Occurrence` picks the right one.
    private sealed class ConfigSite
    {
        public string File;
        public string Key;
        public string Token;
        public int Occurrence;
    }

    private static readonly ConfigSite[] Sites =
    {
        Site("Plugin.cs", "Language", "\"Language\"", 1),
        Site("Plugin.cs", "Density", "\"Density\"", 1),
        Site("Plugin.cs", "ShowPanel", "\"ShowPanel\"", 1),
        Site("Plugin.cs", "ToggleKey", "\"ToggleKey\"", 1),
        Site("Plugin.cs", "ShowDamage", "\"ShowDamage\"", 1),
        Site("Plugin.cs", "ShowFriendlyFire", "\"ShowFriendlyFire\"", 1),
        Site("Plugin.cs", "ShowAccuracy", "\"ShowAccuracy\"", 1),
        Site("Plugin.cs", "ShowWeakspot", "\"ShowWeakspot\"", 1),
        Site("Plugin.cs", "ShowProjectiles", "\"ShowProjectiles\"", 1),
        Site("Plugin.cs", "AnchorToFlashlight", "\"AnchorToFlashlight\"", 1),
        Site("Plugin.cs", "PositionX", "\"PositionX\"", 1),
        Site("Plugin.cs", "PositionY", "\"PositionY\"", 1),
        Site("Plugin.cs", "Layout.OffsetX", "\"OffsetX\"", 1),
        Site("Plugin.cs", "Layout.OffsetY", "\"OffsetY\"", 1),
        Site("Plugin.cs", "FontSize", "\"FontSize\"", 1),
        Site("Plugin.cs", "PanelOpacity", "\"PanelOpacity\"", 1),
        Site("Plugin.cs", "ShowResults", "\"ShowResults\"", 1),
        Site("Plugin.cs", "ShowResultOverlay", "\"ShowResultOverlay\"", 1),
        Site("Plugin.cs", "Results.OffsetX", "\"OffsetX\"", 2),
        Site("Plugin.cs", "Results.OffsetY", "\"OffsetY\"", 2),
        Site("Plugin.cs", "SuccessFontScale", "ResultsZoom.SuccessKey", 1),
        Site("Plugin.cs", "FailureFontScale", "ResultsZoom.FailureKey", 1),
        Site("Plugin.cs", "ShowImpactBoard", "\"ShowImpactBoard\"", 1),
        Site("Plugin.cs", "ShowSuccessArtifacts", "\"ShowSuccessArtifacts\"", 1),
        Site("Plugin.cs", "ShowSuccessPlayerReports", "\"ShowSuccessPlayerReports\"", 1),
        Site("Plugin.cs", "HideVanillaFailBlocks", "\"HideVanillaFailBlocks\"", 1),
        Site("Plugin.cs", "SplitKillTiers", "\"SplitKillTiers\"", 1),
        Site("Plugin.cs", "KillMinorMaxHealth", "\"KillMinorMaxHealth\"", 1),
        Site("Plugin.cs", "KillEliteMaxHealth", "\"KillEliteMaxHealth\"", 1),
        Site("Plugin.cs", "MinorKillWeight", "\"MinorKillWeight\"", 1),
        Site("Plugin.cs", "EliteKillWeight", "\"EliteKillWeight\"", 1),
        Site("Plugin.cs", "BossKillWeight", "\"BossKillWeight\"", 1),
        Site("Plugin.cs", "Weights", "\"Weights\"", 1),
        Site("Plugin.cs", "SupportRoleFirepowerBonus", "\"SupportRoleFirepowerBonus\"", 1),
        Site("Plugin.cs", "SupportRoleUtilityBonus", "\"SupportRoleUtilityBonus\"", 1),
        Site("Plugin.cs", "SupportToolNames", "\"SupportToolNames\"", 1),
        Site("Plugin.cs", "SupportExcludedTools", "\"SupportExcludedTools\"", 1),
        Site("Plugin.cs", "SupportOtherTools", "\"SupportOtherTools\"", 1),
        Site("Plugin.cs", "ScoringFile", "\"ScoringFile\"", 1),
        Site("Plugin.cs", "StaggerNeedsAttack", "\"StaggerNeedsAttack\"", 1),
        Site("CombatSync.cs", "ShareWithEveryPlayer", "\"ShareWithEveryPlayer\"", 1),
        Site("CombatSync.cs", "AllowlistedPeers", "\"AllowlistedPeers\"", 1),
    };

    private static ConfigSite Site(string file, string key, string token, int occurrence)
        => new ConfigSite { File = file, Key = key, Token = token, Occurrence = occurrence };

    private static void EveryConfigDescriptionIsChinese(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the 1.19.24 regression locates the production sources");
        check(Sites.Length == 42,
            "the inventory is exactly the 42 player-facing config descriptions: 40 in Plugin.cs and 2 in CombatSync.cs");
        if (root == null) return;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ConfigSite site in Sites)
        {
            string source = File.ReadAllText(Path.Combine(root, site.File));
            string window;
            string description = ReadDescription(source, site.Token, site.Occurrence, out window);
            check(description != null && description.Length > 0,
                site.File + ":" + site.Key + " yields its description from the production source");
            if (description == null) continue;
            check(!seen.Contains(site.Key),
                "each inventory site maps to one description - " + site.Key + " is not doubled");
            seen.Add(site.Key);
            check(description.Any(c => c >= '\u4E00' && c <= '\u9FFF'),
                site.Key + " description is Chinese: it carries CJK text, so an untranslated English sentence is impossible");
            check(!description.Contains('\n') && !description.Contains('\r'),
                site.Key + " description is one physical line, ready for the ## comment BepInEx writes into the .cfg");
            check(!window.Contains("CombatStrings."),
                site.Key + " description is a plain literal - no binding through CombatStrings, which would always answer Chinese and betray the Language setting");
            check(!FormerEnglishSentences.Any(sentence => string.Equals(sentence, description, StringComparison.Ordinal)),
                site.Key + " description is not byte-identical to a formerly shipped English sentence");
        }
        check(seen.Count == Sites.Length,
            "the 42 distinct description sites were all extracted, so the .cfg surface is fully covered");

        Dictionary<string, string> extracted = Sites.ToDictionary(site => site.Key,
            site => ReadDescription(File.ReadAllText(Path.Combine(SourceRoot(), site.File)), site.Token, site.Occurrence, out _),
            StringComparer.Ordinal);
        const string Hint = "建议关闭显示以免影响观感";
        string artifactsHint, reportsHint;
        check(extracted.TryGetValue("ShowSuccessArtifacts", out artifactsHint) && artifactsHint != null && artifactsHint.Contains(Hint),
            "the ShowSuccessArtifacts description keeps the 「建议关闭显示以免影响观感」 hint");
        check(extracted.TryGetValue("ShowSuccessPlayerReports", out reportsHint) && reportsHint != null && reportsHint.Contains(Hint),
            "the ShowSuccessPlayerReports description keeps the 「建议关闭显示以免影响观感」 hint");
    }

    // The scoring file's own `Guidance` now teaches how to edit titles and reasons, with the same string
    // surface as before: no new JSON field, so a sparse file still parses and inherits the shipped guide.
    private static void TheScoringGuidanceTeachesHowToEdit(Action<bool, string> check)
    {
        string guidance = ScoringSettings.Defaults().Guidance;
        check(!string.IsNullOrEmpty(guidance) && guidance.Contains("【自定义称号与说明】"),
            "the scoring guidance carries the how-to paragraph on customising titles and reasons");
        check(guidance.Contains("Titles.MinorKills.Title") && guidance.Contains("Titles.MinorKills.Description")
              && guidance.Contains("≤16 字符") && guidance.Contains("≤96 字符"),
            "it names the Titles.Title / Titles.Description fields and their length limits");
        check(guidance.Contains("{count}") && guidance.Contains("占位符") && guidance.Contains("空串表示该行不显示说明"),
            "it states the Reasons placeholder rule: only the documented tokens per entry are allowed, and the empty template hides the row");
        check(guidance.Contains("18 个条目名") && guidance.Contains("都不可改名"),
            "it names the eighteen entries and says their keys must not be renamed, nor the top-level keys");
        check(guidance.Contains("保持与出厂完全一致的文本即继续跟随 Language 语言设置"),
            "it states that a title left exactly as shipped keeps following the Language setting");
        check(string.Equals(ScoringSettings.Parse("{ }").Guidance, guidance, StringComparison.Ordinal),
            "a sparse scoring file still parses and inherits the shipped guidance - the guide is the existing string, not a new JSON field");

        // The shipped `defaults/scoring.json` is regenerated from this model by the packaging helper; the
        // repository copy must stay byte-identical to what a fresh install would write.
        string root = SourceRoot();
        check(root != null, "the 1.19.24 scoring guidance locates the production sources");
        if (root == null) return;
        string onDisk = File.ReadAllText(Path.Combine(root, "defaults", "scoring.json")).Replace("\r\n", "\n");
        string generated = ScoringSettings.Defaults().ToJson().Replace("\r\n", "\n");
        check(onDisk == generated,
            "defaults/scoring.json is byte-identical to this build's runtime default, so the shipped guidance matches the code");
    }

    // ---- wiring, read from the production source -----------------------------------------------

    private static void Wiring(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the 1.19.24 regression locates the production sources");
        if (root == null) return;
        string info = File.ReadAllText(Path.Combine(root, "PluginInfo.cs"));
        string manifest = File.Exists(Path.Combine(root, "Thunderstore", "manifest.json"))
            ? File.ReadAllText(Path.Combine(root, "Thunderstore", "manifest.json")) : string.Empty;
        check(info.Contains("Version = \"1.19.39\""),
            "the version carrier says 1.19.39");
        check(manifest.Contains("\"version_number\": \"1.19.39\""),
            "the Thunderstore manifest carries version_number 1.19.39");
        Console.WriteLine("WIRING: RELEASE_11924_CHINESE_CONFIG_SURFACE (static source check; not a runtime behaviour test)");
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    // Reads the `Ocurrence`-th occurrence of `token`, then returns everything literals this bind carries
    // from there to the end of its statement - i.e. the description (the `.cfg` comment), plus, for the
    // three keys whose default is itself a string literal, that default; the per-site checks tolerate the
    // join. The raw statement window is returned for the no-localization check.
    private static string ReadDescription(string source, string token, int occurrence, out string window)
    {
        window = null;
        if (string.IsNullOrEmpty(source)) return null;
        int index = -1;
        for (int step = 0; step < occurrence; step++)
        {
            index = source.IndexOf(token, index + 1, StringComparison.Ordinal);
            if (index < 0) return null;
        }
        int from = index + token.Length;
        int cursor = from;
        int length = source.Length;
        var text = new System.Text.StringBuilder();
        while (cursor < length)
        {
            char c = source[cursor];
            if (c == '"')
            {
                var literal = new System.Text.StringBuilder();
                int end = cursor + 1;
                bool closed = false;
                while (end < length)
                {
                    char d = source[end];
                    if (d == '\\' && end + 1 < length) { literal.Append(source[end + 1]); end += 2; continue; }
                    if (d == '"') { closed = true; end++; break; }
                    literal.Append(d);
                    end++;
                }
                if (!closed) return null;
                text.Append(literal.ToString());
                cursor = end;
                continue;
            }
            if (c == ';') break;
            if (c == '/' && cursor + 1 < length && source[cursor + 1] == '/')
            {
                while (cursor < length && source[cursor] != '\n') cursor++;
                continue;
            }
            if (c == '/' && cursor + 1 < length && source[cursor + 1] == '*')
            {
                cursor += 2;
                while (cursor + 1 < length && !(source[cursor] == '*' && source[cursor + 1] == '/')) cursor++;
                cursor += 2;
                continue;
            }
            cursor++;
        }
        window = source.Substring(from, Math.Min(cursor, length) - from);
        return text.ToString();
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj"))) directory = directory.Parent;
        return directory != null ? directory.FullName : null;
    }
}
