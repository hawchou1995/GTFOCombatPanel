using System;
using HawChow.GTFOCombatPanel;

// The settlement header's own plan (`ResultChromeLayout.PlanHeader`), after two recorded findings on it.
//
// Both findings are replayed on the numbers the runs printed, through the production methods - `PlanHeader`, `TitleFits`,
// `TextFits`, `OnBand` and `ResultChromeTheme` - and never through a re-implementation. Where a test needs the *old*
// answer to show what was wrong, it computes that answer with the pre-fix arithmetic inside the test and says so in the
// assertion's own words; the production code carries only the fixed rule.
//
//   1. The board was 1064..1497 and the key hint was drawn at 2309, because the plan used the *page dock's* toggle x as
//      the header's right edge. Nothing outside the band may budget the band.
//   2. On a narrow plate the name was given a 74 px box while `战斗结算看板` (six full ems at the header's 16 px face on a
//      1440p window) needs 96: the word ran out of its box, over the close. A box is now never narrower than the ink it is
//      given, the name has a short form, and a name that fits in neither form is not drawn at all.
internal static class ResultHeaderPlanScenarios
{
    // The recorded page: a 433 px plate at x=1064 on a 2560x1440 window, with the page's own dock toggle at x=2309.
    private const float ScreenWidth = 2560f;
    private const float ScreenHeight = 1440f;
    private const float BoardX = 1064f;
    private const float BoardWidth = 433f;
    private const float DockToggleX = 2309f;

    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        string previousLanguage = CombatStrings.Language;
        try
        {
            CombatStrings.Language = "zh";
            ThePageDockNeverBudgetsTheHeader(check);
            ABoxIsNeverNarrowerThanItsWords(check);
            TheKeyHintIsServedBeforeTheName(check);
            AHeaderThatFitsEverythingSaysSo(check);
            TheControlsNeverGoAndNeverLeaveTheBand(check);
            CombatStrings.Language = "en";
            TheSameRulesHoldInEnglish(check);
        }
        finally
        {
            CombatStrings.Language = previousLanguage;
        }
    }

    private static ResultChromeMetrics Metrics => ResultChromeMetrics.For(ScreenWidth, ScreenHeight);

    private static LayoutRect Band(float x, float width)
        => new LayoutRect(x, 400f, width, Metrics.HeaderHeight);

    private static LayoutRect Plate(float x, float width)
        => new LayoutRect(x, 400f, width, 600f);

    // The close this board used to carry is gone (1.19.45). The scenarios below still reproduce the *recorded*
    // pre-fix budget arithmetic from that button's own numbers at 1080p - a 76 x 32 box inset 5 px from the plate's
    // top-right corner - so those numbers are stated here as history, never as a rule the product still runs.
    private const float RecordedCloseInset = 5f;
    // The pre-change close, re-stated as *history*: the deleted `CloseSeat` was `board.Right - CloseInset - CloseWidth`
    // with `CloseWidth = max(64, 76*scale)` and `CloseHeight = max(26, 32*scale)`, i.e. the same metric-faithful numbers
    // the old `ResultChromeMetrics.For` produced. Keeping the historical INPUT exact is what lets the assertions above
    // compare the pre-1.19.42 budget against the plan; no production code reads this.
    private static LayoutRect RecordedClose(LayoutRect board, ResultChromeMetrics metrics)
    {
        float width = Math.Max(64f, 76f * metrics.Scale);
        float height = Math.Max(26f, 32f * metrics.Scale);
        return new LayoutRect(board.Right - RecordedCloseInset - width, board.Y + RecordedCloseInset, width, height);
    }

    // ---- finding 1: the page's own dock is not the header's budget --------------------------------------

    private static void ThePageDockNeverBudgetsTheHeader(Action<bool, string> check)
    {
        ResultChromeMetrics metrics = Metrics;
        LayoutRect band = Band(BoardX, BoardWidth);
        LayoutRect close = RecordedClose(Plate(BoardX, BoardWidth), metrics);
        // The page's own dock, far to the right of the plate and nowhere near the header.
        var dock = new LayoutRect(DockToggleX, 1230f, 104f, 31f);

        ResultChromeLayout.ResultChromeHeaderPlan plan = ResultChromeLayout.PlanHeader(band, metrics, dock, true);

        // The old rule, computed here to show what it answered: `right` was the toggle's x wherever it stood.
        float hintInk = ResultChromeLayout.TextEm(ResultChromeTheme.KeyHintLabel) * metrics.HeaderHintFont;
        float oldHintX = (dock.X - metrics.ToggleGap) - hintInk;
        check(oldHintX > band.Right,
            "the pre-1.19.42 rule put the key hint at " + oldHintX.ToString("0") + ", outside a band that ends at "
                + band.Right.ToString("0") + " - the recorded defect, reproduced from its own arithmetic");
        check(!plan.HasToggle,
            "a control that does not stand on the band is not a control of the header (" + plan.Describe() + ")");
        check(plan.HasHint && plan.HasTitle && plan.Drawn,
            "the band still carries the keys and the name it has room for: " + plan.Describe());
        check(plan.Hint.Right <= band.Right - ResultChromeMetrics.HeaderInset + 0.001f,
            "the key hint ends inside the band: hint right=" + plan.Hint.Right.ToString("0") + " <= "
                + (band.Right - ResultChromeMetrics.HeaderInset).ToString("0"));
        check(!plan.HasTitle || plan.Title.Right <= band.Right - ResultChromeMetrics.HeaderInset + 0.001f,
            "and so does the name: title right=" + plan.Title.Right.ToString("0"));
        check(!plan.HasTitle || plan.Title.Right <= plan.Hint.X - ResultChromeTheme.ControlPadX + 0.001f,
            "and the name stops left of the key hint the band really carries: hint=" + plan.Hint.X.ToString("0"));
        check(plan.Yield == "none",
            "a header that fits everything says it gave up nothing, not the name of a failure it does not have: "
                + plan.Yield);
        check(!ResultChromeLayout.OnBand(band, dock) && ResultChromeLayout.OnBand(band, close),
            "`OnBand` is the rule that tells the page's own dock from a rectangle really standing on the band");
        // The numbers a preview reader needs, written whether the assertions pass or not: the dock rule's hint x, and the
        // two rectangles the plan really answered.
        Console.WriteLine("HEADER-DOCK: old hint x=" + oldHintX.ToString("0.#") + " (old right="
            + (dock.X - metrics.ToggleGap).ToString("0.#") + ") band=" + band.X.ToString("0") + ".." + band.Right.ToString("0")
            + " | new hint=" + plan.Hint.X.ToString("0.#") + ".." + plan.Hint.Right.ToString("0.#")
            + " title=" + plan.Title.X.ToString("0.#") + ".." + plan.Title.Right.ToString("0.#")
            + " label=" + plan.TitleLabel + " yield=" + plan.Yield);
    }

    // ---- finding 2: the box holds the ink --------------------------------------------------------------

    private static void ABoxIsNeverNarrowerThanItsWords(Action<bool, string> check)
    {
        ResultChromeMetrics metrics = Metrics;
        // The recorded narrow plate (a ~300 px board), where the name's box measured 74 px.
        LayoutRect band = Band(BoardX, 300f);
        LayoutRect close = RecordedClose(Plate(BoardX, 300f), metrics);
        ResultChromeLayout.ResultChromeHeaderPlan plan = ResultChromeLayout.PlanHeader(band, metrics, default, false);
        float fullInk = ResultChromeLayout.TextEm(ResultChromeTheme.TitleLabel) * metrics.HeaderFont;
        float hintInk = ResultChromeLayout.TextEm(ResultChromeTheme.KeyHintLabel) * metrics.HeaderHintFont;
        // The old rule, computed here: the title's box was `width - hint - pad`, and the whole label went into it.
        float oldWidth = (close.X - ResultChromeTheme.ControlPadX) - (band.X + ResultChromeMetrics.HeaderInset);
        float oldBox = oldWidth - hintInk - ResultChromeTheme.ControlPadX;
        // The before/after numbers for the recorded narrow plate.
        Console.WriteLine("HEADER-NARROW: plate=" + (band.Width).ToString("0") + "px old box=" + oldBox.ToString("0.#")
            + " old ink=" + fullInk.ToString("0.#") + " (overflow " + (fullInk - oldBox).ToString("0.#") + "px)"
            + " | new label=" + (plan.HasTitle ? plan.TitleLabel : "none")
            + " box=" + (plan.HasTitle ? plan.Title.Width.ToString("0.#") : "0")
            + " ink=" + (plan.HasTitle ? (ResultChromeLayout.TextEm(plan.TitleLabel) * metrics.HeaderFont).ToString("0.#") : "0")
            + " hint=" + (plan.HasHint ? plan.Hint.X.ToString("0.#") + ".." + plan.Hint.Right.ToString("0.#") : "none")
            + " close.x=" + close.X.ToString("0.#") + " yield=" + plan.Yield);
        check(oldBox > 0f && fullInk > oldBox,
            "the pre-1.19.42 rule drew a " + fullInk.ToString("0") + " px name into a " + oldBox.ToString("0")
                + " px box on this plate - the recorded overflow, reproduced from its own arithmetic");
        check(plan.HasTitle, "the band still shows the board's name, in the form that fits (" + plan.Describe() + ")");
        float ink = ResultChromeLayout.TextEm(plan.TitleLabel) * metrics.HeaderFont;
        check(!string.IsNullOrEmpty(plan.TitleLabel) && ink <= plan.Title.Width + 0.001f,
            "the box is never narrower than the ink in it: `" + plan.TitleLabel + "` needs " + ink.ToString("0")
                + " px and has " + plan.Title.Width.ToString("0") + " px");
        check(plan.TitleLabel == ResultChromeTheme.TitleLabel && plan.Yield == "none",
            "with no control spending the band's room the full name fits here: `" + plan.TitleLabel
                + "` in " + plan.Title.Width.ToString("0") + " px (yield=" + plan.Yield + ")");
        check(plan.Title.Right <= band.Right - ResultChromeMetrics.HeaderInset + 0.001f,
            "and it stops inside the band's own right inset: title right=" + plan.Title.Right.ToString("0"));
        check(!plan.HasHint || plan.Title.Right <= plan.Hint.X - ResultChromeTheme.ControlPadX + 0.001f,
            "nor does it run under the key hint: title right=" + plan.Title.Right.ToString("0") + ", hint="
                + (plan.HasHint ? plan.Hint.X.ToString("0") : "none"));

        // The recorded plate itself: at 340 px the pre-fix rule reproduces the recorded 74 px box exactly, and the name it
        // drew was 96 px wide. This is the one pair of numbers a reader of the report can check against the run.
        LayoutRect recordedBand = Band(BoardX, 340f);
        LayoutRect recordedClose = RecordedClose(Plate(BoardX, 340f), metrics);
        float recordedWidth = (recordedClose.X - ResultChromeTheme.ControlPadX) - (recordedBand.X + ResultChromeMetrics.HeaderInset);
        float recordedBox = recordedWidth - hintInk - ResultChromeTheme.ControlPadX;
        ResultChromeLayout.ResultChromeHeaderPlan recorded = ResultChromeLayout.PlanHeader(recordedBand, metrics, default, false);
        check(recordedBox > 0f && fullInk > recordedBox,
            "on the recorded 340 px plate the pre-fix rule's own box (" + recordedBox.ToString("0.#")
                + " px) was narrower than the " + fullInk.ToString("0")
                + " px name it drew into it - the recorded overflow, reproduced from the pre-change input");
        check(recorded.HasTitle && !string.IsNullOrEmpty(recorded.TitleLabel)
            && ResultChromeLayout.TextEm(recorded.TitleLabel) * metrics.HeaderFont <= recorded.Title.Width + 0.001f,
            "and the fixed plan draws a name that fits its box there: `" + (recorded.HasTitle ? recorded.TitleLabel : "none")
                + "` in " + (recorded.HasTitle ? recorded.Title.Width.ToString("0.#") : "0") + " px, yield=" + recorded.Yield);
        Console.WriteLine("HEADER-RECORDED: plate=340px old box=" + recordedBox.ToString("0.#") + " old ink=" + fullInk.ToString("0.#")
            + " | new label=" + (recorded.HasTitle ? recorded.TitleLabel : "none") + " box="
            + (recorded.HasTitle ? recorded.Title.Width.ToString("0.#") : "0") + " yield=" + recorded.Yield);
        // A plate too narrow for the keys keeps whatever name it can hold, and never clips one.
        LayoutRect narrow = Band(BoardX, 150f);
        ResultChromeLayout.ResultChromeHeaderPlan narrowPlan = ResultChromeLayout.PlanHeader(narrow, metrics, default, false);
        check(!narrowPlan.HasHint && narrowPlan.Yield == "hint",
            "a band too narrow for the keys gives them up and says so: " + narrowPlan.Describe());
        check(!narrowPlan.HasTitle
            || ResultChromeLayout.TextEm(narrowPlan.TitleLabel) * metrics.HeaderFont <= narrowPlan.Title.Width + 0.001f,
            "and whatever name it keeps is held by its own box");
    }

    // ---- the order: the keys first, then the name ------------------------------------------------------

    private static void TheKeyHintIsServedBeforeTheName(Action<bool, string> check)
    {
        ResultChromeMetrics metrics = Metrics;
        float hintInk = ResultChromeLayout.TextEm(ResultChromeTheme.KeyHintLabel) * metrics.HeaderHintFont;
        float shortInk = ResultChromeLayout.TextEm(ResultChromeTheme.ShortTitleLabel) * metrics.HeaderFont;
        float floor = ResultChromeLayout.TitleMinGlyphs * metrics.HeaderFont;
        // A band that holds the keys and a box between the short name's floor and the full name's ink.
        float width = hintInk + ResultChromeTheme.ControlPadX + (floor + 8f) + ResultChromeMetrics.HeaderInset * 2f;
        LayoutRect band = Band(BoardX, width);
        ResultChromeLayout.ResultChromeHeaderPlan plan = ResultChromeLayout.PlanHeader(band, metrics, default, false);
        check(plan.HasHint,
            "the keys keep their line whenever it fits at all - they are how the board comes back (" + plan.Describe() + ")");
        check(plan.Hint.Width >= hintInk - 0.001f, "and they are drawn at their own measured width");
        check(plan.HasTitle && plan.TitleLabel == ResultChromeTheme.ShortTitleLabel,
            "the name is what gives way, down to its short form: `" + (plan.HasTitle ? plan.TitleLabel : "none") + "`");
        check(plan.Title.Width >= shortInk - 0.001f && plan.Title.Width < ResultChromeLayout.TextEm(ResultChromeTheme.TitleLabel) * metrics.HeaderFont,
            "in a box the short name fits and the full one would not: " + plan.Title.Width.ToString("0") + " px");
        check(plan.Yield == "short-title", "and the plan says which form it used: " + plan.Yield);
        check(plan.Hint.X > band.X && plan.Hint.Right <= band.Right, "the keys stay on the band");

        // The old rule gave the hint up first; this one keeps it and drops the name instead.
        check(plan.HasHint && plan.Title.Right <= plan.Hint.X - ResultChromeTheme.ControlPadX + 0.001f,
            "the keys and the name never share a pixel: title right=" + plan.Title.Right.ToString("0") + ", hint="
                + plan.Hint.X.ToString("0"));
    }

    // ---- the misdiagnosis: a good header is not a missing band ------------------------------------------

    private static void AHeaderThatFitsEverythingSaysSo(Action<bool, string> check)
    {
        ResultChromeMetrics metrics = Metrics;
        LayoutRect band = Band(BoardX, BoardWidth);
        ResultChromeLayout.ResultChromeHeaderPlan fitted = ResultChromeLayout.PlanHeader(band, metrics, default, false);
        check(fitted.Yield == "none",
            "a header that gave up nothing reports `none`; it used to report `no-band`, the name of a failure it never had ("
                + fitted.Describe() + ")");
        ResultChromeLayout.ResultChromeHeaderPlan missing = ResultChromeLayout.PlanHeader(default, metrics, default, false);
        check(missing.Yield == "no-band" && !missing.Drawn,
            "`no-band` now means exactly what it says: the page drew no band to plan on");
        check(ResultChromeLayout.PlanHeader(band, metrics, default, false).Yield.Length > 0,
            "and every plan answers with a verdict, never with a blank");
    }

    // ---- the controls: never dropped, never covered ----------------------------------------------------

    private static void TheControlsNeverGoAndNeverLeaveTheBand(Action<bool, string> check)
    {
        ResultChromeMetrics metrics = Metrics;
        LayoutRect band = Band(BoardX, BoardWidth);
        // 1.19.45: the band carries no control of this mod's own. What is left to prove is that the plan draws nothing
        // outside the band, and that a toggle standing in the page's own dock is never budgeted as if it stood on it.
        LayoutRect dock = new LayoutRect(DockToggleX, band.Y, 104f, 31f);
        ResultChromeLayout.ResultChromeHeaderPlan plan = ResultChromeLayout.PlanHeader(band, metrics, dock, true);
        check(!plan.HasToggle && plan.Drawn,
            "a toggle that does not stand on the band is not planned on it (" + plan.Describe() + ")");
        float bound = band.Right - ResultChromeMetrics.HeaderInset;
        check(!plan.HasTitle || plan.Title.Right <= bound + 0.001f,
            "no text passes the band's own right inset (" + plan.Describe() + ", bound=" + bound.ToString("0") + ")");
        check(!plan.HasHint || plan.Hint.Right <= bound + 0.001f,
            "and the same bound holds the key hint (" + plan.Describe() + ", bound=" + bound.ToString("0") + ")");
        // A toggle the step really commits on the band does spend its room.
        var onBand = new LayoutRect(band.Right - ResultChromeMetrics.HeaderInset - 104f, band.Y + 5f, 104f, 31f);
        ResultChromeLayout.ResultChromeHeaderPlan seated = ResultChromeLayout.PlanHeader(band, metrics, onBand, true);
        check(seated.HasToggle && seated.Toggle.Right <= band.Right + 0.001f,
            "a toggle that really stands on the band keeps its place there (" + seated.Describe() + ")");
        check(!seated.HasTitle || seated.Title.Right <= seated.Toggle.X - metrics.ToggleGap + 0.001f,
            "and no text is drawn on top of it: title right=" + (seated.HasTitle ? seated.Title.Right.ToString("0") : "none"));
        // Even a band far too short for text still draws its band.
        LayoutRect thin = new LayoutRect(BoardX, 400f, BoardWidth, Math.Max(12f, band.Height * 0.6f));
        ResultChromeLayout.ResultChromeHeaderPlan tight = ResultChromeLayout.PlanHeader(thin, metrics, default, false);
        check(tight.Drawn, "a band with no room for text still draws its own band: " + tight.Describe());
    }

    // ---- the same rules in English ---------------------------------------------------------------------

    private static void TheSameRulesHoldInEnglish(Action<bool, string> check)
    {
        ResultChromeMetrics metrics = Metrics;
        check(ResultChromeTheme.TitleLabel == "COMBAT BOARD" && ResultChromeTheme.ShortTitleLabel == "BOARD",
            "the English header has the same two forms: `" + ResultChromeTheme.TitleLabel + "` / `"
                + ResultChromeTheme.ShortTitleLabel + "`");
        LayoutRect band = Band(BoardX, 300f);
        LayoutRect close = RecordedClose(Plate(BoardX, 300f), metrics);
        ResultChromeLayout.ResultChromeHeaderPlan plan = ResultChromeLayout.PlanHeader(band, metrics, default, false);
        check(plan.HasHint && plan.Hint.Right <= band.Right - ResultChromeMetrics.HeaderInset + 0.001f,
            "the English keys stay on the band and inside its own right inset: " + plan.Describe());
        check(!plan.HasTitle
            || ResultChromeLayout.TextEm(plan.TitleLabel) * metrics.HeaderFont <= plan.Title.Width + 0.001f,
            "and an English name is held by the box it is drawn in: `" + (plan.HasTitle ? plan.TitleLabel : "none") + "` in "
                + (plan.HasTitle ? plan.Title.Width.ToString("0") : "0") + " px");
        check(plan.Yield == "none" || plan.Yield == "short-title" || plan.Yield == "title" || plan.Yield == "hint"
            || plan.Yield == "hint+title",
            "the English plan answers with the same vocabulary: " + plan.Yield);
    }
}
