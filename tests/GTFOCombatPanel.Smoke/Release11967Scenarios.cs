using System;
using System.Linq;

using HawChow.GTFOCombatPanel;

// 1.19.67: the all-round sentence (`全能中坚`) said "综合评分全队第一，无单项领先" - "top of the composite
// score, no single category led" - and its description repeated the same claim. That claim is false on the
// board it is printed beside. In the recorded 2K host failure (`evidence-combatpanel-...`, four players)
// Dauda rescued one teammate while everyone else rescued none: the settlement board prints that rescue as
// the 救援 row's figure, but the scoring floor is two, so the rescue earns no point and no sentence. Dauda
// and Hackett then end on exactly the same score, neither leads a single row outright, and both wear this
// sentence - on a board where Dauda is the printed rescue leader. The line now says only what the sentence
// is: top of the overall score, no category title. That is true for a unique first and for an exact tie.
//
// What this file pins, all of it through the production functions (`ImpactScoring.Evaluate`,
// `ImpactBoard.MetricFields`, `ImpactBoard.ReasonText`, `ScoringSettings`, `CombatStrings`):
//
//   * the recorded board itself - the printed rescue figure, the untouched calculation, the exact tie,
//     the two identical rows and the line under them;
//   * a unique first with no outright row lead reads the same line, so the wording claims no tie either;
//   * two rescues still reach the floor and still earn 妙手回春, unchanged;
//   * the new wording is a default (it follows Language and is never pinned), the wording the previous
//     build published is recognised as an untouched default in the existing detection, a real custom line
//     survives in every language, and a near miss of the old text stays the player's own.
internal static class Release11967Scenarios
{
    // ---- the replaced wording, as this file's own independent sample -----------------------------------
    // These four strings are the test's own copies on purpose, never `CombatStrings.Previous*`: a scenario
    // that compared the detector against the very constants the detector uses would agree with a build
    // whose constants had drifted. The Chinese pair is the wording frozen with the 1.19.67 record
    // (`freeze_20261128_122457/config/scoring.json`, `Reasons.AllRound` and `Titles.AllRound.Description`);
    // the English pair is the wording the previous build published (this file's `Localization.cs` before
    // this delivery, also readable in the installed DLL's decompilation under `scratch\dcs\old`).
    private const string PreviousReasonZh = "综合评分全队第一，无单项领先";
    private const string PreviousReasonEn = "top composite score, no single row led";
    private const string PreviousDescriptionZh = "综合评分全队第一，各项没有单独领先。";
    private const string PreviousDescriptionEn = "First on the composite score without leading a single row.";
    internal static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        string previousLanguage = CombatStrings.Language;
        CombatStrings.ClearReasonOverrides();
        CombatStrings.ResetTitleOverrides();
        ImpactScoring.ResetThresholds();
        try
        {
            Wording(check);
            RecordedHostFailureBoard(check);
            UniqueFirst(check);
            TwoRescuesStillEarnTheOldTitle(check);
            DefaultsAndPublishedWording(check);
        }
        finally
        {
            CombatStrings.ClearReasonOverrides();
            CombatStrings.ResetTitleOverrides();
            ImpactScoring.ResetThresholds();
            CombatStrings.Language = previousLanguage;
        }
    }

    // ---- the shipped pair, in both languages -----------------------------------------------------
    private static void Wording(Action<bool, string> check)
    {
        CombatStrings.Language = "zh";
        check(CombatStrings.ReasonAllRound == "综合分居首，未获单项称号",
            "the all-round line ships as 综合分居首，未获单项称号 (got \"" + CombatStrings.ReasonAllRound + "\")");
        check(!CombatStrings.ReasonAllRound.Contains("无单项领先"),
            "it no longer claims that no single category was led");
        check(CombatStrings.TitleDescription(ImpactRating.AllRound) == "综合得分居首，未获单项称号。",
            "and its description says the same thing (got \"" + CombatStrings.TitleDescription(ImpactRating.AllRound) + "\")");
        check(!CombatStrings.DefaultTitleDescription(ImpactRating.AllRound).Contains("没有单独领先"),
            "the description no longer repeats the claim the board can contradict");
        CombatStrings.Language = "en";
        check(CombatStrings.ReasonAllRound == "top overall; no category title",
            "the English line is top overall; no category title (got \"" + CombatStrings.ReasonAllRound + "\")");
        check(CombatStrings.TitleDescription(ImpactRating.AllRound) == "Top overall composite score; no category title.",
            "and the English description matches it");
        CombatStrings.Language = "zh";

        check(ScoringSettings.DefaultReasons()["AllRound"] == "综合分居首，未获单项称号"
              && ScoringSettings.Defaults().Reasons["AllRound"] == "综合分居首，未获单项称号",
            "the factory template a scoring file is created from carries the new line");
        check(ScoringSettings.DefaultTitles()["AllRound"].Description == "综合得分居首，未获单项称号。"
              && ScoringSettings.Defaults().Titles["AllRound"].Description == "综合得分居首，未获单项称号。",
            "and so does the factory description");
        check(ScoringSettings.Defaults().Titles["AllRound"].Title == "全能中坚",
            "the title word itself is untouched");
    }

    // ---- the recorded 2K host failure ------------------------------------------------------------
    private static void RecordedHostFailureBoard(Action<bool, string> check)
    {
        CombatStrings.Language = "zh";
        ImpactScoring.RescueFloor = ImpactScoring.MinimumRescuesForScoring;
        check(ImpactScoring.RescueFloor == 2, "the scoring floor this record was scored with is two rescues");

        // Dauda rescued one teammate; nobody else rescued anyone. Dauda and Hackett end on exactly the same
        // score, and the identical damage figure is shared, so neither leads the damage row outright either.
        var dauda = new ImpactInput { Name = "Dauda", EnemyDamage = 900 };
        dauda.Counters.CreditRevive();
        var hackett = new ImpactInput { Name = "Hackett", EnemyDamage = 900 };
        var bishop = new ImpactInput { Name = "Bishop", EnemyDamage = 300 };
        var woods = new ImpactInput { Name = "Woods", EnemyDamage = 100 };
        ImpactReport report = ImpactScoring.Evaluate(new[] { dauda, hackett, bishop, woods },
            ImpactScoring.DefaultWeights, 1.0, 1.0);
        ImpactEntry daudaRow = report.Entries.First(entry => entry.Name == "Dauda");
        ImpactEntry hackettRow = report.Entries.First(entry => entry.Name == "Hackett");
        double squadDamage = report.Entries.Sum(entry => entry.Input.EnemyDamage);

        // The rescue is displayed and it does not score: this is the rule the wording may not deny.
        check(daudaRow.Counters.Revives == 1 && hackettRow.Counters.Revives == 0,
            "the record's rescue count is on the row: 1 for Dauda, 0 for the rest");
        check(ImpactBoard.MetricFields(daudaRow, ImpactMetric.Revives, KillTier.Minor, false, true).Single() == "1",
            "the 救援 cell still prints the one rescue the board measured");
        check(CombatStrings.ReasonRevives(1) == "救起队友 1 次",
            "and that figure still has its own explanation line for the row that carries the sentence");
        check(ImpactScoring.RawValue(daudaRow.Input, ImpactMetric.Revives) == 0,
            "one rescue still scores nothing at the floor of two - the calculation is untouched");
        check(ImpactScoring.ScoreText(daudaRow.Score) == ImpactScoring.ScoreText(hackettRow.Score)
              && ImpactScoring.ScoreText(daudaRow.Score) != "0.0",
            "the two first places print one and the same score (" + ImpactScoring.ScoreText(daudaRow.Score) + ")");

        // The exact tie for first: two rows, same rank, same sentence, same line.
        check(daudaRow.Rank == 1 && hackettRow.Rank == 1,
            "an exact tie puts both rows in first place");
        check(daudaRow.Rating == ImpactRating.AllRound && hackettRow.Rating == ImpactRating.AllRound,
            "and both wear the all-round sentence");
        check(daudaRow.RatingMetric == -1 && hackettRow.RatingMetric == -1,
            "which names no metric");
        check(report.Entries.All(entry => entry.Rating != ImpactRating.Revives),
            "no rescue sentence is handed out below the floor");
        check(CombatStrings.Title(daudaRow.Rating) == "全能中坚" && CombatStrings.Title(hackettRow.Rating) == "全能中坚",
            "the two tied rows print the same title word");

        string daudaReason = ImpactBoard.ReasonText(daudaRow, squadDamage);
        string hackettReason = ImpactBoard.ReasonText(hackettRow, squadDamage);
        check(daudaReason == "综合分居首，未获单项称号" && hackettReason == daudaReason,
            "both explanation lines are the new line (got \"" + daudaReason + "\" and \"" + hackettReason + "\")");
        check(daudaReason.Contains("未获单项称号"),
            "and they say the player holds no category title");
        check(!daudaReason.Contains("无单项领先") && !daudaReason.Contains("没有单独领先")
              && !daudaReason.Contains("第一"),
            "the old claim - and any other claim of a lead - is gone from the line the board prints");
        check(CombatStrings.TitleDescription(ImpactRating.AllRound) == "综合得分居首，未获单项称号。"
              && !CombatStrings.TitleDescription(ImpactRating.AllRound).Contains("没有单独领先"),
            "the sentence's own description matches the line beside it");
    }

    // ---- a real unique first: the same wording is right there too --------------------------------
    private static void UniqueFirst(Action<bool, string> check)
    {
        CombatStrings.Language = "zh";
        var steady = new ImpactInput { Name = "Steady", EnemyDamage = 100 };
        steady.Counters.Stagger = 100;
        var heavy = new ImpactInput { Name = "Heavy", EnemyDamage = 120 };
        var stopper = new ImpactInput { Name = "Stopper" };
        stopper.Counters.Stagger = 130;
        var light = new ImpactInput { Name = "Light", EnemyDamage = 10 };
        light.Counters.Stagger = 10;
        ImpactReport report = ImpactScoring.Evaluate(new[] { steady, heavy, stopper, light },
            ImpactScoring.DefaultWeights, 1.0, 1.0);
        ImpactEntry top = report.Entries.First(entry => entry.Name == "Steady");
        double squadDamage = report.Entries.Sum(entry => entry.Input.EnemyDamage);
        check(top.Rank == 1 && report.Entries.Count(entry => entry.Rank == 1) == 1,
            "a first place built out of steady second places is a unique first");
        check(top.Rating == ImpactRating.AllRound && top.RatingMetric == -1,
            "it wears the all-round sentence");
        check(ImpactBoard.ReasonText(top, squadDamage) == "综合分居首，未获单项称号",
            "and reads the same line: the wording claims neither a lead nor a tie");
    }

    // ---- two rescues: the floor is reached and the old sentence is unchanged ----------------------
    private static void TwoRescuesStillEarnTheOldTitle(Action<bool, string> check)
    {
        CombatStrings.Language = "zh";
        var medic = new ImpactInput { Name = "Medic" };
        medic.Counters.CreditRevive();
        medic.Counters.CreditRevive();
        var rifle = new ImpactInput { Name = "Rifle", EnemyDamage = 500 };
        ImpactReport report = ImpactScoring.Evaluate(new[] { medic, rifle }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        ImpactEntry medicRow = report.Entries.First(entry => entry.Name == "Medic");
        double squadDamage = report.Entries.Sum(entry => entry.Input.EnemyDamage);
        check(ImpactScoring.RawValue(medicRow.Input, ImpactMetric.Revives) == 2,
            "two rescues reach the floor of two and still count for the score");
        check(medicRow.Rating == ImpactRating.Revives,
            "so the rescue sentence is handed out as it always was");
        check(CombatStrings.Title(ImpactRating.Revives) == "妙手回春",
            "with its unchanged title word");
        check(ImpactBoard.ReasonText(medicRow, squadDamage) == "救起队友 2 次",
            "and its unchanged explanation line");
    }

    // ---- the new wording is a default; the published one is still recognised as a default --------
    private static void DefaultsAndPublishedWording(Action<bool, string> check)
    {
        CombatStrings.Language = "zh";

        // The text this build publishes is a default in the existing sense: it follows Language.
        ScoringSettings.Defaults().ApplyTitles();
        ScoringSettings.Defaults().ApplyReasons();
        check(!CombatStrings.HasReasonOverride(ImpactRating.AllRound) && !CombatStrings.HasTitleOverride(ImpactRating.AllRound),
            "the shipped all-round text is a default, never pinned as an override");
        CombatStrings.Language = "en";
        check(CombatStrings.ReasonAllRound == "top overall; no category title"
              && CombatStrings.TitleDescription(ImpactRating.AllRound) == "Top overall composite score; no category title.",
            "so an untouched file keeps following the language setting");
        CombatStrings.Language = "zh";

        // A scoring file written by the previous build holds the replaced wording verbatim, entry untouched.
        ScoringSettings previous = FileWithAllRound(PreviousReasonZh, PreviousDescriptionZh);
        check(previous.Reasons["AllRound"] == PreviousReasonZh
              && previous.Titles["AllRound"].Description == PreviousDescriptionZh,
            "the previous build's file round trips with the replaced wording in both halves");
        previous.ApplyTitles();
        previous.ApplyReasons();
        check(!CombatStrings.HasReasonOverride(ImpactRating.AllRound),
            "the replaced explanation is an untouched default under the existing detection, not a custom line (printed \"" + CombatStrings.ReasonAllRound + "\")");
        check(CombatStrings.ReasonAllRound == "综合分居首，未获单项称号"
              && CombatStrings.TitleDescription(ImpactRating.AllRound) == "综合得分居首，未获单项称号。",
            "so such a file prints this build's wording in both halves instead of freezing the old claim (printed \""
            + CombatStrings.ReasonAllRound + "\" / \"" + CombatStrings.TitleDescription(ImpactRating.AllRound) + "\")");
        CombatStrings.Language = "en";
        check(CombatStrings.ReasonAllRound == "top overall; no category title"
              && CombatStrings.TitleDescription(ImpactRating.AllRound) == "Top overall composite score; no category title.",
            "and both follow the language setting, exactly like every other untouched entry");
        CombatStrings.Language = "zh";

        // The old English pair was published in the display layer; a file holding it is a default too.
        ScoringSettings previousEnglishPair = FileWithAllRound(PreviousReasonEn, PreviousDescriptionEn);
        previousEnglishPair.ApplyTitles();
        previousEnglishPair.ApplyReasons();
        check(!CombatStrings.HasReasonOverride(ImpactRating.AllRound)
              && CombatStrings.TitleDescription(ImpactRating.AllRound) == "综合得分居首，未获单项称号。",
            "the old English pair is recognised as an untouched default as well (printed \""
            + CombatStrings.ReasonAllRound + "\" / \"" + CombatStrings.TitleDescription(ImpactRating.AllRound) + "\")");

        // A line the player really wrote is kept, in every language.
        ScoringSettings mine = FileWithAllRound("我打满全场", "全场最稳的一局");
        mine.ApplyTitles();
        mine.ApplyReasons();
        check(CombatStrings.HasReasonOverride(ImpactRating.AllRound) && CombatStrings.ReasonAllRound == "我打满全场",
            "a custom explanation is still the player's own and is pinned (printed \"" + CombatStrings.ReasonAllRound + "\")");
        check(CombatStrings.TitleDescription(ImpactRating.AllRound) == "全场最稳的一局",
            "and a custom description is printed exactly as written (printed \"" + CombatStrings.TitleDescription(ImpactRating.AllRound) + "\")");
        CombatStrings.Language = "en";
        check(CombatStrings.ReasonAllRound == "我打满全场" && CombatStrings.TitleDescription(ImpactRating.AllRound) == "全场最稳的一局",
            "which stays pinned when the language changes");
        CombatStrings.Language = "zh";

        // A near miss of the published text is the player's own wording: only the exact string is a default.
        ScoringSettings almost = FileWithAllRound(PreviousReasonZh + "。", PreviousDescriptionZh);
        almost.ApplyReasons();
        check(CombatStrings.HasReasonOverride(ImpactRating.AllRound)
              && CombatStrings.ReasonAllRound == PreviousReasonZh + "。",
            "a text that only resembles the published one is never mistaken for a default");

        CombatStrings.ClearReasonOverrides();
        CombatStrings.ResetTitleOverrides();
        check(CombatStrings.ReasonAllRound == "综合分居首，未获单项称号",
            "clearing the overrides restores the shipped line for the scenarios that follow");
    }

    // One scoring file with the all-round pair replaced and then written out and read back - exactly the
    // round trip a real file takes, so the assertions above never depend on how the reader formats JSON.
    private static ScoringSettings FileWithAllRound(string reason, string description)
    {
        ScoringSettings edited = ScoringSettings.Parse(ScoringSettings.Defaults().ToJson());
        edited.Reasons["AllRound"] = reason;
        edited.Titles["AllRound"].Description = description;
        edited.Validate();
        return ScoringSettings.Parse(edited.ToJson());
    }
}
