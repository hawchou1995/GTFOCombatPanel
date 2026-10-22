using System;

using System.Linq;

using HawChow.GTFOCombatPanel;

// The settlement board prints three parts per row: the player, a sentence (`评价`: "火力核心") and the line
// that says what that sentence was earned with (`说明`: "伤害 24,962.0，占全队 38.8%"). The sentences became
// configurable through `scoring.json`'s `Titles`; this pins the same treatment for the explanation lines, one
// `Reasons` template per sentence, under the same rule: a template left exactly as shipped keeps following
// the language setting, and only a template the user really changed is pinned down.
//
// Everything asserted here runs the shipping model - `ScoringSettings`, `CombatStrings`, no Unity, no
// BepInEx - so the numbers and the wording below are the ones the board prints. `Run()` counts its own
// failures; `RunInto(Check)` feeds the same assertions to the smoke suite's own checker.
internal static class ReasonTemplateScenarios
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

    // The smoke suite's shape: one boolean per property, with the number that makes it true in the text.
    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        string previousLanguage = CombatStrings.Language;
        CombatStrings.ClearReasonOverrides();
        CombatStrings.ResetTitleOverrides();

        // ---- one factory template per sentence, and the table that documents them ---------------------
        //
        // `ReasonTokens` is the one place that says which placeholders a sentence may use: the validator
        // reads it, the file's own Guidance quotes it, and the display layer fills exactly those. Its key set
        // is asserted against `TitleNames` first, because everything else stands on that.
        check(ScoringSettings.ReasonTokens.Count == ScoringSettings.TitleNames.Length
              && ScoringSettings.TitleNames.All(name => ScoringSettings.ReasonTokens.ContainsKey(name)),
            $"ReasonTokens documents exactly the {ScoringSettings.TitleNames.Length} settlement sentences");
        check(ScoringSettings.DefaultReasons().Count == ScoringSettings.TitleNames.Length
              && ScoringSettings.TitleNames.All(name => ScoringSettings.Defaults().Reasons.ContainsKey(name)),
            "every sentence ships with one explanation template and the default file carries them all");
        check(ScoringSettings.TitleNames.All(name => ScoringSettings.Defaults().Reasons[name] == ScoringSettings.DefaultReasons()[name]),
            "the default settings' templates are the factory templates, not a second list that could drift");
        check(ScoringSettings.ReasonTokens["Damage"].SequenceEqual(new[] { "damage", "share" }),
            "the damage line documents {damage} and {share}");
        check(ScoringSettings.ReasonTokens["Stagger"].SequenceEqual(new[] { "count" }),
            "the stagger line documents {count} only");
        check(ScoringSettings.ReasonTokens["Weakspot"].SequenceEqual(new[] { "weakspots", "hits" })
              && ScoringSettings.ReasonTokens["MeleeKills"].SequenceEqual(new[] { "kills", "damage" }),
            "the two-figure lines document both of their figures");
        check(ScoringSettings.ReasonTokens["ZeroScore"].Length == 0,
            "the zero-score line takes no placeholder at all");
        check(ScoringSettings.TitleNames.All(name => ScoringSettings.ReasonTokens[name]
                  .All(token => ScoringSettings.DefaultReasons()[name].Contains("{" + token + "}"))),
            "every documented placeholder is really used by that sentence's factory template");
        check(ScoringSettings.Defaults().ToJson().Contains("\"Reasons\""),
            "the file this build writes carries the explanation templates");

        // ---- an untouched file follows the language setting --------------------------------------------
        //
        // The factory template renders the shipped Chinese line character for character, and it is not an
        // override: that is what lets a language change move it. The English wording is unchanged by this
        // release, which is why the template itself is never what an English build prints.
        ScoringSettings.Defaults().ApplyReasons();
        bool nothingPinned = ScoringSettings.TitleNames.All(name => !CombatStrings.HasReasonOverride(RatingOf(name)));
        check(nothingPinned, "an untouched template is not pinned, so every line keeps following the language setting");
        check(CombatStrings.ReasonDamage("24,962.0", "38.8%") == "伤害 24,962.0，占全队 38.8%",
            "the factory damage template renders exactly the shipped Chinese line");
        check(CombatStrings.ReasonScan(4, 12) == "扫描 4 次，首次标记 12 只",
            "the factory scan template renders both of its figures");
        check(CombatStrings.ReasonMeleeKills(7, "1,204.5") == "近战击杀 7 次，伤害 1,204.5",
            "the factory melee template renders its two figures in the order the row prints them");
        check(CombatStrings.ReasonKills(KillTier.Elite, 3) == "击杀精英 3 只",
            "the elite class keeps the sentence of its own class");
        check(CombatStrings.ReasonNoScore == "本局没有任何计分贡献", "the zero-score line ships as before");
        CombatStrings.Language = "en";
        check(CombatStrings.ReasonDamage("24,962.0", "38.8%") == "damage 24,962.0, 38.8% of the squad",
            "an untouched template follows a language change instead of freezing the Chinese text");
        check(CombatStrings.ReasonKills(KillTier.Minor, 5) == "5 minor kills",
            "and so does a kill class the file never mentioned");
        CombatStrings.Language = previousLanguage;

        // ---- one template changed: it is the user's from then on, in every language --------------------
        //
        // "我打了 {damage}" names one placeholder and drops the other. The share is therefore neither filled
        // in nor printed as a raw `{share}`: a placeholder the template does not carry is simply not part of
        // the line, and one the caller cannot supply is left exactly as the user wrote it.
        string customJson = ScoringSettings.Defaults().ToJson().Replace("伤害 {damage}，占全队 {share}", "我打了 {damage}");
        check(customJson.Contains("我打了 {damage}"), "the edited file really carries the new template");
        ScoringSettings custom = ScoringSettings.Parse(customJson);
        check(custom.Reasons["Damage"] == "我打了 {damage}", "a custom template is read back out of the file");
        check(custom.Fingerprint() != ScoringSettings.Defaults().Fingerprint(),
            "a rewritten explanation changes the scoring fingerprint, so the board is rebuilt for it");
        custom.ApplyReasons();
        check(CombatStrings.HasReasonOverride(ImpactRating.Damage), "a changed template is pinned as an override");
        check(!CombatStrings.HasReasonOverride(ImpactRating.Stagger), "a template the file left alone is still not pinned");
        string rendered = CombatStrings.ReasonDamage("24,962.0", "38.8%");
        check(rendered == "我打了 24,962.0", $"the custom template renders with the figure it names (got \"{rendered}\")");
        check(!rendered.Contains("{share}") && !rendered.Contains("38.8%"),
            "the share the user removed is neither printed as a placeholder nor filled in");
        CombatStrings.Language = "en";
        check(CombatStrings.ReasonDamage("24,962.0", "38.8%") == "我打了 24,962.0" && CombatStrings.ReasonScan(4, 12) == "scanned 4x, first-marked 12 enemies",
            "a pinned template stays pinned in another language, and an untouched one follows it");
        CombatStrings.Language = previousLanguage;

        // ---- the refusals: a template the board could not print rejects the whole file ------------------
        //
        // Each of these is a file a user could really write, and each one leaves the last good settings - and
        // their wording - in force instead of half-applying a template nobody can render.
        string unknownPlaceholder = ScoringSettings.Defaults().ToJson().Replace("伤害 {damage}，占全队 {share}", "伤害 {damage} {nope}");
        string placeholderOfAnotherRow = ScoringSettings.Defaults().ToJson().Replace("打断敌人攻击 {count} 次", "打断敌人攻击 {damage} 次");
        string unclosedPlaceholder = ScoringSettings.Defaults().ToJson().Replace("伤害 {damage}，占全队 {share}", "伤害 {damage");
        string tooLong = ScoringSettings.Defaults().ToJson().Replace("伤害 {damage}，占全队 {share}", new string('火', ScoringSettings.MaxReasonLength + 1));
        string repeatedKey = ScoringSettings.Defaults().ToJson().Replace("\"Reasons\": {", "\"Reasons\": {\n    \"damage\": \"重复\",");
        string unknownKey = ScoringSettings.Defaults().ToJson().Replace("\"Reasons\": {", "\"Reasons\": {\n    \"Nope\": \"未知\",");
        var refused = new (string What, string Broken)[]
        {
            ("an unknown placeholder", unknownPlaceholder),
            ("a placeholder another row owns", placeholderOfAnotherRow),
            ("a placeholder that is never closed", unclosedPlaceholder),
            ("a template longer than " + ScoringSettings.MaxReasonLength + " characters", tooLong),
            ("the same sentence twice", repeatedKey),
            ("a sentence that does not exist", unknownKey)
        };
        for (int index = 0; index < refused.Length; index++)
        {
            bool rejected = false;
            try { ScoringSettings.Parse(refused[index].Broken); }
            catch (FormatException) { rejected = true; }
            check(rejected, $"{refused[index].What} rejects the whole file, so the last good wording stands");
        }

        // ---- an empty template: the row prints no explanation at all ----------------------------------
        //
        // Empty is a value, not a mistake: it is how a user says "this row needs no line under it". It is a
        // change from the shipped template, so it is pinned, and the row it names prints nothing while every
        // other row still reads as shipped.
        string silent = ScoringSettings.Defaults().ToJson().Replace("伤害 {damage}，占全队 {share}", "");
        ScoringSettings quiet = ScoringSettings.Parse(silent);
        check(quiet.Reasons["Damage"] == string.Empty,
            "an empty template is read as the empty template rather than treated as a missing entry");
        quiet.ApplyReasons();
        check(CombatStrings.HasReasonOverride(ImpactRating.Damage), "an empty template is a change, so it is pinned");
        check(CombatStrings.ReasonDamage("24,962.0", "38.8%") == string.Empty,
            "an empty template prints nothing at all for the row it names");
        check(CombatStrings.ReasonStagger(3) == "打断敌人攻击 3 次",
            "and a row the file did not touch still reads as shipped");

        // ---- the board's own reading of an emptied template ---------------------------------------------
        // The line under the title is drawn by the board, which used to decide on a dash or a bare estimate
        // marker before it ever looked at the template. It now asks this first, so an emptied row prints
        // nothing instead of a `~` or a `—` the player deleted.
        check(CombatStrings.ReasonOverrideSuppressed(ImpactRating.Damage),
            "the board can ask whether a row's explanation was emptied by the player");
        check(!CombatStrings.ReasonOverrideSuppressed(ImpactRating.Scan),
            "and a row the file left alone is never suppressed");

        // ---- a sparse file and a null template ---------------------------------------------------------
        check(ScoringSettings.Parse("{}").Reasons.Count == ScoringSettings.TitleNames.Length,
            "a sparse file fills every explanation from the factory template");
        string sparseReason = "{\"Reasons\": {\"damage\": \"我打了 {damage}\"}}";
        ScoringSettings sparse = ScoringSettings.Parse(sparseReason);
        check(sparse.Reasons["Damage"] == "我打了 {damage}"
              && sparse.Reasons["Stagger"] == ScoringSettings.DefaultReasons()["Stagger"],
            "one custom template is honoured while the rest keep their shipped text, under the canonical key");
        check(ScoringSettings.Parse("{\"Reasons\": {\"Damage\": null}}").Reasons["Damage"] == string.Empty,
            "a null template reads as the empty one rather than as a broken file");

        // ---- the scenarios leave the display layer as they found it ------------------------------------
        CombatStrings.ClearReasonOverrides();
        CombatStrings.ResetTitleOverrides();
        CombatStrings.Language = previousLanguage;
        check(CombatStrings.ReasonDamage("24,962.0", "38.8%") == "伤害 24,962.0，占全队 38.8%",
            "clearing the overrides restores the shipped line, so a later scenario sees what this one did");
    }

    // The rating a sentence name stands for: the same one-to-one mapping the scoring file's own keys use.
    private static ImpactRating RatingOf(string name) => (ImpactRating)Enum.Parse(typeof(ImpactRating), name);
}
