using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// The settlement page's controls, driven against the two recorded host failure pages.
//
// What the recording actually did (scratch\combatpanel-log-forensics.md §3-§6, two independent runs 2781-2797 and
// 405721-405735): the page answered `settlement toggle pending: anchor=no-host-action` for the whole settlement, so
// the toggle - the only pointer that can bring the board back - was never drawn; 45 `board=shown` presses produced
// 46 `result board deferred ... forcedByF8=true` lines and not one drawn frame; and a placed board was re-fitted to a
// 720 px plate in a 651 px band and deferred on the next repaint, one frame of board between two frames of the
// game's own page.
//
// Every assertion below runs the production policy the game runs (`ResultSettlementChrome.cs`). The source checks at
// the end only assert the wiring the headless suite cannot execute - which is this project's existing split, and each
// one says so in its message.
internal static class ResultChromeEntryScenarios
{
    internal static void RunInto(Action<bool, string> check)
    {
        TheHeaderPlanGivesUpDecorationNotControls(check);
        ANoAnchorPageGetsNoControlAndSaysWhy(check);
        AHiddenBoardLeavesTheOneControlWhereItWas(check);
        TheAnchorPageKeepsItsOwnPlace(check);
        AFirstClickOnAFreshControlIsAnswered(check);
        AQuickClickAndAHeldPressAreOneActionEach(check);
        TheFreshWindowIsBounded(check);
        APlateOutsideItsOwnBandIsRefused(check);
        TheDecisionTheRequestAndThePaintingAreSeparateLines(check);
        TheVanillaBlockIsNeverThePageRoot(check);
        ANewSettlementDropsTheTransientOverride(check);
        TheRecordedErrataAreAnswered(check);
    }

    // ---- a scripted page ----------------------------------------------------------------------------------
    //
    // One result page, repainted on demand. Every field is what this repaint's measurement says; there is no
    // second storey to offer the toggle any more (1.19.45), so this harness offers none either.
    private sealed class ScriptedPage
    {
        private readonly ResultChromeCommit _commit = new ResultChromeCommit();
        private readonly ResultChromeSettle _settle = new ResultChromeSettle();
        private readonly ResultChromeProbe _probe = new ResultChromeProbe();
        private readonly List<LayoutRect> _native = new List<LayoutRect>();
        private IntPtr _pointer = new IntPtr(61);

        internal int Width = 1920, Height = 1080;
        internal bool BoardUp = true, AnchorDrawn;
        internal LayoutRect Anchor = new LayoutRect(860f, 990f, 200f, 32f);
        internal ResultChromeAnchor Source = ResultChromeAnchor.HostActionButton;
        internal ResultChromeReveal Reveal = ResultChromeReveal.Arrived;
        internal string AnchorWhy = "go-to-lobby-button";
        internal bool PlateDrawn = true;
        internal LayoutRect Plate = new LayoutRect(575f, 390f, 770f, 500f);

        internal ResultChromeCommit Commit => _commit;
        internal List<LayoutRect> Native => _native;

        internal ResultChromeDecision Next()
        {
            _probe.Reset();
            _probe.Page = _pointer;
            _probe.ScreenWidth = Width;
            _probe.ScreenHeight = Height;
            _probe.BoardUp = BoardUp;
            _probe.AnchorDrawn = AnchorDrawn;
            _probe.Anchor = Anchor;
            _probe.Source = Source;
            _probe.Reveal = Reveal;
            _probe.AnchorWhy = AnchorWhy;
            _probe.Native = _native;
            _probe.PlateDrawn = PlateDrawn;
            _probe.Plate = Plate;
            return ResultChromeLayout.Step(_commit, _settle, ResultChromeMetrics.For(Width, Height), _probe);
        }
    }

    private static bool Same(LayoutRect first, LayoutRect second)
        => Math.Abs(first.X - second.X) < 0.001f && Math.Abs(first.Y - second.Y) < 0.001f
           && Math.Abs(first.Width - second.Width) < 0.001f && Math.Abs(first.Height - second.Height) < 0.001f;

    // ---- the header's name and key hint give way in a fixed order ------------------------------------------
    private static void TheHeaderPlanGivesUpDecorationNotControls(Action<bool, string> check)
    {
        ResultChromeMetrics metrics = ResultChromeMetrics.For(1920, 1080);
        LayoutRect plate = new LayoutRect(575f, 390f, 770f, 500f);
        LayoutRect band = ResultChromeLayout.HeaderOf(plate, metrics);
        ResultChromeLayout.ResultChromeHeaderPlan wide = ResultChromeLayout.PlanHeader(band, metrics, default, false);
        check(wide.HasTitle && wide.HasHint && wide.Drawn,
            "a 770 px band carries the board's name and the keys that operate it (" + wide.Describe() + ")");
        check(Math.Abs(wide.Rule.Height - ResultChromeTheme.HeaderRule) < 0.001f
            && Math.Abs(wide.Rule.Width - band.Width) < 0.001f && Math.Abs(wide.Rule.Y - band.Y) < 0.001f,
            "the band's only line is the 2 px accent rule across its top edge, full width");
        check(wide.Title.X >= band.X + ResultChromeMetrics.HeaderInset - 0.001f
              && wide.Title.Right <= band.Right - ResultChromeMetrics.HeaderInset + 0.001f,
            "the name starts at the band's own inset and stays inside it: title=" + wide.Title.X.ToString("0")
            + ".." + wide.Title.Right.ToString("0"));
        check(wide.Title.Height <= band.Height && wide.Hint.Right <= band.Right - ResultChromeMetrics.HeaderInset + 0.001f,
            "both runs of text stay inside the band, inside its own right inset");

        // Narrow: the keys go first and the name keeps whatever form fits; neither control is ever dropped. 1.19.42
        // changed the *order*: the keys keep their line whenever it fits and the name is what gives way - down to its
        // short form - because the keys are how a player brings the board back.
        ResultChromeLayout.ResultChromeHeaderPlan hintOnly = ResultChromeLayout.PlanHeader(
            new LayoutRect(band.X, band.Y, 70f, band.Height), metrics, default, false);
        check(!hintOnly.HasHint && hintOnly.Yield == "hint",
            "a 70 px band loses the key hint first (" + hintOnly.Describe() + ")");
        check(!hintOnly.HasTitle
            || ResultChromeLayout.TextEm(hintOnly.TitleLabel) * metrics.HeaderFont <= hintOnly.Title.Width + 0.001f,
            "and whatever name it keeps is held by its own box: `" + (hintOnly.HasTitle ? hintOnly.TitleLabel : "none")
                + "` in " + (hintOnly.HasTitle ? hintOnly.Title.Width.ToString("0") : "0") + " px");
        ResultChromeLayout.ResultChromeHeaderPlan bare = ResultChromeLayout.PlanHeader(
            new LayoutRect(band.X, band.Y, 40f, band.Height), metrics, default, false);
        check(!bare.HasHint && bare.Drawn,
            "a 40 px band keeps the band, its rule and (if it fits) the short name - never a clipped word ("
                + bare.Describe() + ")");
        check(!bare.HasTitle
            || ResultChromeLayout.TextEm(bare.TitleLabel) * metrics.HeaderFont <= bare.Title.Width + 0.001f,
            "and the short name it keeps is never wider than its box");
        check(bare.Yield == "hint" || bare.Yield == "hint+title", "and the plan names what it gave up: " + bare.Yield);
        check(!ResultChromeLayout.PlanHeader(default, metrics, default, false).Drawn,
            "a band that was never measured yields nothing at all, so nothing is drawn on a page that gave no plate");
    }

    // ---- the recorded `anchor=no-host-action` page gets no control, and says why ---------------------------
    //
    // 1.19.45 removed the header seat and the dock, so the right answer for a page that draws nothing to stand
    // beside is "nothing" - and the refusal has to be legible, which is what `anchor=no-host-action` in the pending
    // line says. A late anchor is answered by the settle rule, which is regressed in
    // `ResultChromeCommitScenarios.ANoAnchorPageStillAnswersWhenTheControlArrives`, not by a rectangle invented here.
    private static void ANoAnchorPageGetsNoControlAndSaysWhy(Action<bool, string> check)
    {
        var page = new ScriptedPage { AnchorDrawn = false, AnchorWhy = "no-host-action" };
        bool nothing = true;
        for (int repaint = 0; repaint < 4; repaint++)
        {
            ResultChromeDecision pending = page.Next();
            if (pending.ToggleDrawn || !pending.Pending || page.Commit.Toggle.Held) nothing = false;
        }
        check(nothing,
            "the recorded page (anchor=no-host-action, restarts=0) draws no control and commits no place on any repaint");
        check(page.Commit.Toggle.Placements == 0,
            "and nothing is placed for it: there is no header seat and no dock to fall back to (placements="
            + page.Commit.Toggle.Placements + ")");

        // The place a page *does* offer is withheld while a native rectangle stands on it, and comes back untouched.
        var anchored = new ScriptedPage { AnchorDrawn = true, AnchorWhy = "go-to-lobby-button" };
        anchored.Next();
        ResultChromeDecision settled = anchored.Next();
        check(settled.ToggleDrawn, "the anchored page places its control beside the page's own button");
        anchored.Native.Add(settled.Toggle);
        // 1.19.48: one repaint of overlap is a flicker and keeps the button where it is; the pause is earned by distinct frames.
        ResultChromeDecision touched = anchored.Next();
        check(touched.ToggleDrawn && touched.ToggleFlicker && !touched.ToggleSuspended,
            "one repaint of a native control on the committed place is a flicker: the control is still drawn and still hit");
        // 1.19.59: three distinct repaints of a real overlap move the place aside (bounded) instead of withholding the button
        // while the page keeps drawing that rectangle.
        anchored.Next();
        ResultChromeDecision moved = anchored.Next();
        check(moved.ToggleDrawn && moved.ToggleMoved && !moved.ToggleSuspended
              && !ResultChromeLayout.Inside(settled.Toggle, moved.Toggle.X + 1f, moved.Toggle.Y + 1f),
            "three distinct repaints of it move the draw and the hit aside, so the pointer keeps a button (moved=" + moved.ToggleMoved + ")");
        LayoutRect aside = moved.Toggle;
        anchored.Native.Clear();
        ResultChromeDecision firstClear = anchored.Next();
        check(firstClear.ToggleDrawn && !firstClear.TogglePaused,
            "the moved control is drawn on the frame the conflict leaves, with no pause in between");
        anchored.Next();
        check(ResultChromeLayout.Inside(anchored.Next().Toggle, aside.X + 1f, aside.Y + 1f),
            "and the place it moved to is the place this page keeps - it is never re-measured away from it");
    }

    // ---- a hidden board leaves the one control where it was -------------------------------------------------
    private static void AHiddenBoardLeavesTheOneControlWhereItWas(Action<bool, string> check)
    {
        var page = new ScriptedPage { AnchorDrawn = true, AnchorWhy = "go-to-lobby-button" };
        page.Next();
        ResultChromeDecision shown = page.Next();
        LayoutRect place = shown.Toggle;
        check(shown.ToggleDrawn && shown.HeaderDrawn,
            "the board is up: its band and the one control are both drawn");
        // The press: the board is hidden. The next repaint has no plate and no band, and the control keeps its place.
        page.BoardUp = false; page.PlateDrawn = false;
        ResultChromeDecision hidden = page.Next();
        check(hidden.ToggleDrawn && Same(hidden.Toggle, place),
            "the control is still drawn at the same place with the board hidden: the screen is never left with nothing but F8");
        check(!hidden.HeaderDrawn,
            "and the band it reserved is gone with the board - the band belongs to the board, the control does not");
        page.BoardUp = true; page.PlateDrawn = true;
        ResultChromeDecision again = page.Next();
        check(again.ToggleDrawn && Same(again.Toggle, place) && again.HeaderDrawn,
            "showing the board again returns the band and leaves the control exactly where it was");
        check(page.Commit.Toggle.Placements == 1,
            "hide, show and hide again cost no re-placement (placements=" + page.Commit.Toggle.Placements + ")");
    }

    // ---- a page that does offer an anchor keeps the place it always had -----------------------------------
    private static void TheAnchorPageKeepsItsOwnPlace(Action<bool, string> check)
    {
        var page = new ScriptedPage { AnchorDrawn = true, AnchorWhy = "go-to-lobby-button" };
        page.Next();
        ResultChromeDecision settled = page.Next();
        check(settled.ToggleDrawn && settled.ToggleCommitted,
            "a page with an anchor still places the toggle against it on the second settled repaint");
        check(Math.Abs(settled.Toggle.Bottom - page.Anchor.Bottom) < 0.001f
              && settled.Toggle.Right <= page.Anchor.X - 4f + 0.001f,
            "and the place is beside the page's own control: that is the only place this mod ever draws a control");
    }

    // ---- the press discipline the recorded pages needed ---------------------------------------------------
    private static void AFirstClickOnAFreshControlIsAnswered(Action<bool, string> check)
    {
        // A control committed while the button is up, then one ordinary press. Nothing ever fired a release edge
        // into this gate, and the old rule (a release must have been seen) refused the click.
        var gate = new ResultChromeInputGate();
        gate.Fire(true, true, false, pressed: false, held: false, released: false);
        check(gate.Armed && gate.LastWhy == "idle-armed",
            "a frame with the button up arms a control that is live: the same fact a release edge carries (why=" + gate.LastWhy + ")");
        check(gate.Fire(true, true, true, pressed: true, held: true, released: false) && gate.Fires == 1,
            "the first ordinary click on a freshly drawn control is answered (it used to be swallowed and the player had to click twice)");
        gate.Fire(true, true, true, pressed: false, held: true, released: false);
        check(!gate.Fire(true, true, true, pressed: false, held: true, released: false) && gate.Fires == 1,
            "a press held down does not repeat: one physical press is one action");
        // The click that opened the page is still refused: its press arrived while the control was not there.
        var opened = new ResultChromeInputGate();
        check(!opened.Fire(true, true, true, pressed: true, held: true, released: false) && opened.LastWhy == "not-armed",
            "a press with no release and no up frame before it still does not fire, which keeps the click that opened the page from closing it");
    }

    private static void AQuickClickAndAHeldPressAreOneActionEach(Action<bool, string> check)
    {
        var gate = new ResultChromeInputGate();
        gate.Fire(true, true, false, pressed: false, held: false, released: false);
        // One Unity frame can deliver both edges - a click shorter than a frame at a low frame rate.
        check(gate.Fire(true, true, true, pressed: true, held: false, released: true) && gate.Fires == 1,
            "a click whose down and up arrive in the same frame is still one click (why=" + gate.LastWhy + ")");
        gate.Fire(true, true, true, pressed: false, held: true, released: false);
        check(gate.Fires == 1,
            "and the frames after it cannot answer the same press a second time (fires=" + gate.Fires + ")");
        var stale = new ResultChromeInputGate();
        stale.Fire(true, true, false, pressed: false, held: false, released: false);
        check(!stale.Fire(true, rectFresh: false, inside: true, pressed: true, held: true, released: false) && stale.LastWhy == "stale",
            "a rectangle from a repaint that did not draw the control answers nothing (why=" + stale.LastWhy + ")");
        var lost = new ResultChromeInputGate();
        lost.Fire(true, true, false, pressed: false, held: false, released: false);
        check(!lost.Fire(focused: false, true, true, pressed: true, held: true, released: false) && !lost.Armed,
            "a window that lost focus cancels the gesture and needs a fresh up frame before it counts again");
    }

    private static void TheFreshWindowIsBounded(Action<bool, string> check)
    {
        check(ResultChromeInputPolicy.Fresh(10, 10) && ResultChromeInputPolicy.Fresh(10, 11),
            "a control drawn on this repaint or the one before answers: `Update` runs between two repaints on the same frame window");
        check(ResultChromeInputPolicy.Fresh(10, 10 + ResultChromeInputPolicy.FreshFrames),
            "the window is bounded, and it covers a skipped repaint (" + ResultChromeInputPolicy.FreshFrames + " frames, ~100 ms at 60 Hz)");
        check(!ResultChromeInputPolicy.Fresh(10, 11 + ResultChromeInputPolicy.FreshFrames),
            "past the bound the rectangle is stale and answers nothing - no hot box outlives the control it described");
        check(!ResultChromeInputPolicy.Fresh(-1, 42) && !ResultChromeInputPolicy.Fresh(7, 6),
            "a frame that drew nothing, and a clock that went backwards, are both answered 'not fresh'");
    }

    // ---- the plate/brand invariant the recorded re-fit broke ----------------------------------------------
    private static void APlateOutsideItsOwnBandIsRefused(Action<bool, string> check)
    {
        // The recorded re-fit, verbatim: `result board re-fitted: ... 433x720 px at (1063,541) ... band=(488..1139)`.
        // A 720 px plate hung from 541 ends at 1261 - 122 px past the band it claims to stand in.
        check(!ResultChromeLayout.PlateInsideBand(new LayoutRect(1063f, 541f, 433f, 720f), 488f, 1139f, 1400f),
            "the recorded 433x720 plate at (1063,541) in a (488..1139) band is refused: a board is never drawn outside the band it was measured into");
        check(ResultChromeLayout.PlateInsideBand(new LayoutRect(1115f, 541f, 330f, 540f), 488f, 1139f, 1400f),
            "the placement that came before it - 330x540 at (1115,541), foot 1081 - is inside the same band and passes");
        check(!ResultChromeLayout.PlateInsideBand(new LayoutRect(1063f, 1000f, 433f, 500f), 488f, float.NaN, 1400f),
            "an unmeasurable band foot still refuses a plate that runs off the bottom of the window");
        check(!ResultChromeLayout.PlateInsideBand(new LayoutRect(1063f, 400f, 433f, 200f), 488f, 1139f, 1400f),
            "a plate that starts above its band's top is refused too (the failure title's own layout is the top edge)");
        check(!ResultChromeLayout.PlateInsideBand(default, 488f, 1139f, 1400f),
            "and a plate that was never measured is not a plate");
    }

    // ---- the three lines a press produces are three different lines ---------------------------------------
    private static void TheDecisionTheRequestAndThePaintingAreSeparateLines(Action<bool, string> check)
    {
        string root = SourceRoot();
        if (root == null) { check(false, "the settlement-entry wiring check locates the production sources"); return; }
        string display = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        string policy = File.ReadAllText(Path.Combine(root, "ResultSettlementChrome.cs"));

        check(display.Contains("settlement board click:") && display.Contains("decision={decision}"),
            "the bounded click decision has its own line (control, pointer, frameAge, armed outcome) and says it is not a claim that a board was painted");
        check(display.Contains("settlement board {(show ? \"show\" : \"hide\")} requested:"),
            "the toggle request has its own line, so a press is answered even when nothing is painted");
        check(display.Contains("[DIAG] F8 forced frame: board draw issued") && display.Contains("ReportForcedDraw(_origin);"),
            "and the presentation is still reported only after `HudTable.Draw` really ran");
        check(display.Contains("bool fresh = ResultChromeInputPolicy.Fresh(_chromeDrawnFrame, frame) && sameWindow;"),
            "the input path asks the bounded freshness policy - not `Time.frameCount - _chromeDrawnFrame <= 1`, which closed the only frame a click could be answered in");
        check(!display.Contains("OfferChromeSeat(")
              && !policy.Contains("public static bool HeaderSeat(")
              && !policy.Contains("public static bool DockSeat(")
              && !policy.Contains("public static LayoutRect CloseSeat("),
            "there is no fallback storey left anywhere in the production sources: no header seat and no dock for the toggle to stand in (1.19.45)");
        check(!display.Contains("toggle.Commit(")
              && policy.Contains("decision.ToggleCommitted = toggle.Commit(place, (probe.AnchorWhy ?? \"anchor\") + \"/\" + placeWhy);"),
            "the place is committed by the policy alone, and only from the anchor the page really drew: the renderer never writes a place");
    }

    // ---- the vanilla blocks only make way for a board that is painted ------------------------------------
    private static void TheVanillaBlockIsNeverThePageRoot(Action<bool, string> check)
    {
        string root = SourceRoot();
        if (root == null) { check(false, "the vanilla-block guard check locates the production sources"); return; }
        string display = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        // 1.19.57: `Hide` is handed the live page and the flavour, so the guard order below is measured on the method
        // that really runs - the signature changed, the order it protects did not.
        int hide = display.IndexOf("private static void Hide(Component block, string what, Component page, bool succeeded, bool visualOnly = false)", StringComparison.Ordinal);
        int shared = display.IndexOf("private static bool SharedOrRoot(Component block)", StringComparison.Ordinal);
        check(hide >= 0 && shared >= 0 && shared < hide,
            "the page-root guard is defined and used by `Hide` (source check: this is the Unity side)");
        int guard = display.IndexOf("if (SharedOrRoot(block))", hide, StringComparison.Ordinal);
        int firstTouch = display.IndexOf("Hidden.HoldMove(", hide, StringComparison.Ordinal);
        check(guard > hide && firstTouch > guard,
            "and it is asked before anything is recorded or moved: the result page itself and any ancestor of it are never switched off");
        check(display.Contains("private static bool BoardSettled(LayoutRect plate)"),
            "the one-repaint settle exists: a board is painted only after the place it was measured into survived a second repaint");
        int draw = display.IndexOf("internal static void Draw()", StringComparison.Ordinal);
        int band = display.IndexOf("ResultChromeLayout.PlateInsideBand(_holdBoard, _holdStrip.Top, _holdStrip.Bottom, Screen.height, BoardSlack)", draw, StringComparison.Ordinal);
        int settle = display.IndexOf("if (!painted && !BoardSettled(_holdBoard))", draw, StringComparison.Ordinal);
        // 1.19.55: the same statement, now also asking the early-board contract. The needle follows the call it guards,
        // so the ordering claim below still measures the one place the page's blocks may be put away - and the early
        // board (drawn before the page proved its own boundary) can never put anything away from here at all.
        int vanilla = display.IndexOf("ApplyVanillaBlocks(vanillaPage, vanillaSucceeded, _earlyBoard);", draw, StringComparison.Ordinal);
        int earlyGuard = display.IndexOf("ResultEarlyBoardPolicy.HidesVanilla(_earlyBoard, true)", draw, StringComparison.Ordinal);
        int visualOnlyFrame = display.IndexOf("ApplyVanillaBlocks(Component page, bool succeeded, bool visualOnly = false)", StringComparison.Ordinal);
        check(band > draw && settle > band && vanilla > settle && earlyGuard > settle && earlyGuard < vanilla && visualOnlyFrame > 0,
            "and both are asked before the vanilla blocks are put away, so the game's own page is never hidden for a board that is refused a frame later - and an early board's frame passes `visualOnly`, the one half of hiding that moves and deactivates nothing (1.19.59)");
    }

    // ---- a new settlement generation drops the transient override, and writes no configuration -----------
    private static void ANewSettlementDropsTheTransientOverride(Action<bool, string> check)
    {
        string root = SourceRoot();
        if (root == null) { check(false, "the generation check locates the production sources"); return; }
        string display = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        int clear = display.IndexOf("private static void ClearTransientForced()", StringComparison.Ordinal);
        int success = display.IndexOf("internal static void BeginSuccessPage", clear, StringComparison.Ordinal);
        int failure = display.IndexOf("internal static void BeginFailurePage", StringComparison.Ordinal);
        string body = clear >= 0 && success > clear ? display.Substring(clear, success - clear) : string.Empty;
        check(clear >= 0 && success > clear && failure > success,
            "the transient-override clear sits before both page visits it belongs to (source check: the Unity side)");
        check(body.Contains("Forced = false;") && body.Contains("_forcedOutcomePending = false;"),
            "a new generation drops both the fail-open override and the pending 'this press owes a line' flag: an F8 press cannot leak into the next settlement");
        check(body.Length > 0 && !body.Contains("PersistToggle"),
            "and it writes no configuration: the player's own visibility switches keep exactly the semantics they had");
        int inSuccess = display.IndexOf("ClearTransientForced();", success, StringComparison.Ordinal);
        int inFailure = display.IndexOf("ClearTransientForced();", failure, StringComparison.Ordinal);
        check(inSuccess > success && inSuccess < failure && inFailure > failure,
            "both visits call it, so neither the success nor the failure page inherits the previous settlement's override");
    }

    // ---- the attachment's own numbers, as the policy now answers them -------------------------------------
    private static void TheRecordedErrataAreAnswered(Action<bool, string> check)
    {
        // 2781/405721: the board placed from the fail-open safe area was 330x540 at glyph 12 px - a board drawn from a
        // guessed band. Nothing in this change makes a guess a placement: the band invariant refuses a plate outside
        // its band, and the settle keeps the vanilla page until a plate stands twice.
        check(ResultChromeLayout.PlateInsideBand(new LayoutRect(1115f, 541f, 330f, 540f), 488f, 1139f, 1400f),
            "the fail-open plate itself passes the band check - the refusal is about the plate that ran past the band, not about fail-open boards");
        // 2757/405722: `settlement toggle pending: anchor=no-host-action, restarts=0` for a whole settlement. Since
        // 1.19.45 that line is the whole and correct answer for such a page: there is no seat to commit, and inventing
        // one is exactly what the one-control rule forbids. The player keeps F8, and the control arrives the moment
        // the page's own return button is really drawn.
        ResultChromeMetrics metrics = ResultChromeMetrics.For(1400f, 900f);
        LayoutRect plate = new LayoutRect(484f, 488f, 433f, 540f);
        LayoutRect band = ResultChromeLayout.HeaderOf(plate, metrics);
        check(ResultChromeLayout.PlanHeader(band, metrics, default, false).Drawn && band.Y >= 488f,
            "the no-host-action page's own plate still yields its band: the band names the board and never needed an anchor");
        // 2785/405724: `toggle=none (anchor=None, ), close=(1339,493,101x43)` - one control placed while the other was
        // withheld. There is one control now, and it is placed only at the page's own anchor, so that line cannot recur.
        LayoutRect table = new LayoutRect(plate.X, plate.Y + metrics.HeaderHeight, plate.Width,
            Math.Max(0f, plate.Height - metrics.HeaderHeight));
        check(!band.Overlaps(table),
            "and the band is real page space above the table, holding no button of this mod's own");
    }

    private static string SourceRoot()
    {
        string directory = AppContext.BaseDirectory;
        for (int step = 0; step < 8 && directory != null; step++)
        {
            string candidate = Path.Combine(directory, "ResultsDisplay.cs");
            if (File.Exists(candidate)) return directory;
            directory = Path.GetDirectoryName(directory);
        }
        return null;
    }
}
