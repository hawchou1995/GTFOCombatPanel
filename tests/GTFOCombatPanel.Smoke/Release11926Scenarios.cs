using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// 1.19.26: the settlement page's two live faults, driven through the production policies and read back out of the
// production sources.
//
//   (1) F8 on a result page answered itself from `Forced` - an override flag that defaults to false and says nothing
//       about whether a board is on the screen. A page whose fail-open board the mod had already put up by itself
//       therefore answered "not shown", the press that should have hidden the board turned the override on and drew
//       the same board again, and the press after *that* took the page away through a gate the player could not see
//       (the live 1.19.25 failure page: fifteen forced-on, fourteen off). The press now reads the display -
//       `ResultTogglePolicy.BoardUp` over the last repaint's own "a board was drawn" fact - so a board that is up is
//       hidden by the next press and a page with no board is given one.
//
//   (2) After the first fail-open placement, `FailureEdgesReadable` answered the second look from "the title
//       rectangle and *a* button rectangle are finite" - which a failure page whose leave button another mod has
//       switched off still satisfies. The retry it spent re-placed the board strictly, the next repaint could not
//       measure that footer, `BoardStillSafe` withdrew the board on `no-footer-unproofed`, and
//       `DeferBoardForInteraction` refunded the budget, so the same second look was armed again: fail-open, second
//       look, withdraw, for ever. The second look is now asked of the strip the strict placement would take
//       (`FailureStrip`) and of `ResultAnchorRecovery.Recovered`, and the deferral no longer refunds the per-page
//       retry. Nothing was relaxed: the interaction scan, the overlap refusal and the no-footer proof are untouched,
//       no frame budget or timeout was added, and an unmeasurable footer is still never read as safe.
internal static class Release11926Scenarios
{
    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        APressHidesTheBoardThePageIsShowing(check);
        ASecondLookNeedsAMeasuredFooter(check);
        AWithdrawnSecondLookIsNotRefunded(check);
        ADeferralDoesNotRefundTheDriftBudget(check);
        TheScanAndOverlapProtectionSurvive(check);
        ThePressIsWiredToTheDisplay(check);
        ACanvasGroupIsAskedOfTheObjectNotCastFromTheTransform(check);
        TheForcedFrameLineIsWrittenAfterTheDraw(check);
        Wiring(check);
    }

    // The press's whole answer, driven exactly as `ResultsDisplay._boardDrawnFrame` and `ToggleForcedFallback` feed it:
    // a board on the page is hidden, a page with no board is given one, and the override flag is never consulted.
    private static void APressHidesTheBoardThePageIsShowing(Action<bool, string> check)
    {
        // ---- one page, the board already up before the first press (the live fault) ----
        int frame = 100;
        int drawnFrame = frame;                          // the fail-open board was drawn this repaint
        bool showPanel = true, showOverlay = true, showBoard = true, showResults = true;

        check(ResultTogglePolicy.BoardUp(drawnFrame, frame),
            "a board the last repaint really drew is on the page: this is the fact the press reads");
        check(ResultTogglePolicy.Next(ResultTogglePolicy.BoardUp(drawnFrame, frame)) == ResultBoardToggle.Hide,
            "the first press on a page that is already showing its board hides it - not 'force the board on again'");
        check(ResultTogglePolicy.Next(false) == ResultBoardToggle.Show,
            "and the answer the old code gave for that same page - the override flag, false by default - was the opposite one");

        // Press 1: hide. The switch half is the documented one (`TogglePolicy` writes ShowPanel + ShowResultOverlay).
        var hide = ResultTogglePolicy.Next(ResultTogglePolicy.BoardUp(drawnFrame, frame));
        check(hide == ResultBoardToggle.Hide, "press 1 hides");
        ToggleAction hidden = TogglePolicy.Apply(hide == ResultBoardToggle.Show);
        showPanel = hidden.ShowPanel; showOverlay = hidden.ShowResultOverlay;
        check(!showPanel && !showOverlay && hidden.Save,
            "the hide writes both switches the key owns off and asks for the file to be saved, exactly as the in-level press does");
        bool forced = hide == ResultBoardToggle.Show;
        bool draws = OverlayPolicy.Draws(showResults, showOverlay, showBoard, showPanel) || forced;
        check(!draws, "nothing is drawn after the hide: the same gate `Draw` reads answers no");
        drawnFrame = draws ? frame : -1;                 // the repaint of this very frame writes what it really painted
        frame += 1;
        drawnFrame = draws ? frame : -1;

        // Press 2: show. Nothing is on the page, so the press gives the page a board - and the fail-open override goes
        // on first, so no gate of the page can hold it back.
        var show = ResultTogglePolicy.Next(ResultTogglePolicy.BoardUp(drawnFrame, frame));
        check(show == ResultBoardToggle.Show, "press 2 shows: a page with no board is given one");
        ToggleAction shown = TogglePolicy.Apply(show == ResultBoardToggle.Show);
        showPanel = shown.ShowPanel; showOverlay = shown.ShowResultOverlay;
        forced = show == ResultBoardToggle.Show;
        draws = OverlayPolicy.Draws(showResults, showOverlay, showBoard, showPanel) || forced;
        check(draws && showPanel && showOverlay && forced,
            "the show turns both switches back on and puts the fail-open override in front of every gate");
        drawnFrame = draws ? frame : -1;
        // The next frame's repaint: a board that is still up is drawn again, so the fact stays current.
        frame += 1;
        drawnFrame = draws ? frame : -1;

        // Press 3 is a hide again: the two presses alternate, and the page does not have to be pressed twice to obey.
        check(ResultTogglePolicy.Next(ResultTogglePolicy.BoardUp(drawnFrame, frame)) == ResultBoardToggle.Hide,
            "press 3 hides again - the presses alternate one for one");

        // ---- a page that is showing nothing, with every switch on (the default state of a fresh page) ----
        check(!ResultTogglePolicy.BoardUp(-1, 100),
            "a page whose last repaint painted nothing is not showing a board - a board drawn once and withdrawn is gone");
        check(ResultTogglePolicy.Next(false) == ResultBoardToggle.Show,
            "so the first press on a page that is showing nothing shows the board");

        // ---- Update runs between two repaints: the board drawn on the previous frame is still the board on the page ----
        check(ResultTogglePolicy.BoardUp(100, 101) && !ResultTogglePolicy.BoardUp(100, 102),
            "a board drawn during the previous repaint still counts, and one older than that does not");

        // ---- the next run / the next page: nothing was drawn for it yet ----
        int newPageDrawn = -1;                           // `Draw`'s own opening write on the new page
        check(ResultTogglePolicy.Next(ResultTogglePolicy.BoardUp(newPageDrawn, 400)) == ResultBoardToggle.Show,
            "a new result page - the second expedition of the session - starts from 'nothing is showing', so its first press shows");
        check(ResultTogglePolicy.BoardUp(100, 400) == false,
            "and the previous page's draw can never be mistaken for this page's board: the frame number would be stale");
    }

    // The second look is only worth taking when the page offers the strip the strict placement would take. These are
    // exactly the two facts the live failure page disagreed about: a finite button rectangle beside an unmeasurable
    // footer.
    private static void ASecondLookNeedsAMeasuredFooter(Action<bool, string> check)
    {
        check(ResultAnchorRecovery.Recovered(true, true, false),
            "a strip the page measured with the footer that bounds it is a real recovery: the second look may take it");
        check(!ResultAnchorRecovery.Recovered(true, false, false),
            "a strip whose footer the page could not measure is NOT a recovery - this is the live page that used to spend its one retry on it");
        check(!ResultAnchorRecovery.Recovered(false, true, false),
            "a page that offers no strip at all is not a recovery, whatever else is readable");
        check(ResultAnchorRecovery.Recovered(true, false, true),
            "a page's own no-footer proof is still a recovery: a page with nothing to measure is proven, not guessed");
        check(!ResultAnchorRecovery.Recovered(false, false, true),
            "but the proof alone buys no second look without a strip to place the board into");
        check(!ResultAnchorRecovery.Recovered(true, false, false),
            "and a rectangle is not a footer: nothing here accepts geometry as a substitute for the measurement");
    }

    // The livelock, driven through the same calls `Refresh` -> `FallbackRecoveryDue` -> `DeferBoardForInteraction` makes.
    private static void AWithdrawnSecondLookIsNotRefunded(Action<bool, string> check)
    {
        int recoveries = 0;
        bool placedOnFallback = true;                   // the first fail-open placement
        int secondLooks = 0;
        int withdrawn = 0;
        for (int frame = 0; frame < 200; frame++)
        {
            // `FallbackRecoveryDue`: budget first, then the page's own strip.
            if (placedOnFallback && ResultAnchorRecovery.ShouldRetry(recoveries, true, true)
                && ResultAnchorRecovery.Recovered(true, false, false))    // an unmeasurable footer: not a recovery
            {
                recoveries = ResultAnchorRecovery.Record(recoveries);
                placedOnFallback = false;
                secondLooks++;
            }
            if (!placedOnFallback) continue;

            // A fallback board that was re-placed after a recovery: `LatchFallback` refuses to arm a spent retry.
            placedOnFallback = true;
            bool armed = ResultAnchorRecovery.LatchFallback(recoveries, false);
            if (!armed) { placedOnFallback = false; continue; }
        }
        check(secondLooks == 0,
            $"an unmeasurable footer never arms the second look at all: {secondLooks} second look(s) in 200 frames");

        // The same loop with a footer that really becomes measurable: exactly one recovery, then the deferral - which no
        // longer refunds the budget - leaves the page on its fail-open board instead of arming a third round.
        recoveries = 0; placedOnFallback = true; secondLooks = 0; withdrawn = 0;
        for (int frame = 0; frame < 200; frame++)
        {
            if (placedOnFallback && ResultAnchorRecovery.ShouldRetry(recoveries, true, true)
                && ResultAnchorRecovery.Recovered(true, true, false))
            {
                recoveries = ResultAnchorRecovery.Record(recoveries);
                placedOnFallback = false;
                secondLooks++;
                // The strict board went up and `BoardStillSafe` withdrew it on the repaint after: the deferral keeps
                // the spent budget (`_fallbackRecoveries` is no longer reset there) and only forgets the placement.
                withdrawn++;
                placedOnFallback = false;
                // The fail-open path puts a board back on the page; `LatchFallback` sees the spent retry.
                placedOnFallback = ResultAnchorRecovery.LatchFallback(recoveries, false);
                continue;
            }
            if (!placedOnFallback) placedOnFallback = ResultAnchorRecovery.LatchFallback(recoveries, false);
        }
        check(secondLooks == 1 && withdrawn == 1,
            $"a page may spend its one second look once, whatever the deferral does afterwards: {secondLooks} look(s), {withdrawn} withdrawal(s) in 200 frames");
        check(recoveries == ResultAnchorRecovery.MaximumRecoveries,
            "and the budget ends saturated, so no further frame can re-ask the question");
        check(placedOnFallback == false,
            "the page keeps the fail-open board that was re-placed for it - the second look is not what keeps it up");
    }

    // Nothing was traded away for either fix: the interaction scan, the overlap refusal, the no-footer proof and the
    // held-grid bound still decide whether a strict board may stay, and no frame budget was added to the recovery.
    private static void TheScanAndOverlapProtectionSurvive(Action<bool, string> check)
    {
        string display = Source("ResultsDisplay.cs");

        string recovery = Between(display, "private static bool FailureEdgesReadable(", "private static bool AnchorDriftDue(");
        check(recovery.Length > 0, "the 1.19.26 recovery predicate is found in the production source");
        check(!recovery.Contains("VisibleTop("),
            "and it no longer answers itself from a layout rectangle: a finite button rectangle is not a measured footer");
        check(recovery.Contains("FailureStrip(page, out float stripTop, out float stripBottom")
              && recovery.Contains("ResultAnchorRecovery.Recovered(offerable, footerMeasured, noFooterProven)"),
            "it asks the placement's own strip measurement and hands the two facts to the recovery policy");
        check(recovery.Contains("if (!GuestClient() && !controls.Complete) return false;"),
            "a host's incomplete scan is still refused before any recovery may be claimed");
        check(!recovery.Contains("Time.frameCount") && recovery.IndexOf("Elapsed", StringComparison.OrdinalIgnoreCase) < 0,
            "no frame budget, no elapsed-time give-up: the recovery waits for the page's own geometry, exactly as before");

        string stillSafe = Between(display, "private static bool BoardStillSafe()", "private static bool FallbackPlacement(");
        check(stillSafe.Contains("ReportBoardOverlap(\"failure\"")
              && stillSafe.Contains("failureControls.OverlapsBoard")
              && stillSafe.Contains("failureControls.Complete")
              && stillSafe.Contains("ResultBoardHold.HeldGridFits("),
            "the per-repaint interaction scan, its completeness and the overlap/held-grid refusals are untouched");
        // 1.19.37: this assertion used to pin the client proof as the *only* way past the refusal, which made it true of the
        // defect - `FailureNoFooter.Ready` is false for a host by construction, so the recorded host page had no release at
        // all. What it asserts now is the half that must stay: the refusal is still there, still named, still refused for a
        // shape the page's own band does not prove. The second way in is `ResultFailureBandProofPolicy` (Release11937Scenarios).
        check(stillSafe.Contains("\"no-footer-unproofed\"")
              && stillSafe.Contains("if (!GuestClient() && !failureFooterMeasured && failureBandProof != FailureBandProofState.Proven)"),
            "an unmeasurable footer is still refused where the page's own measured band does not prove it: the refusal, its name and its place in the per-repaint check are unchanged");

        string defer = Between(display, "private static void DeferBoardForInteraction()", "// ---- the fail-open placement");
        check(defer.Length > 0 && defer.Contains("_placedOnFallback = false;") && !defer.Contains("_fallbackRecoveries = 0;"),
            "the deferral forgets the placement and keeps the spent retry - refunding it was what armed the next round");
        check(display.Contains("_placedOnFallback = false; _fallbackRecoveries = 0; _boardFloor = float.NaN;"),
            "a different result page still starts the second look from scratch: the per-page reset lives in ForgetPage alone");
    }

    // The press really goes through the display: the drawn-frame fact `Draw` writes, the policy call, the override and
    // the switch write, and the panel that hands the answer to `ToggleState`.
    private static void ThePressIsWiredToTheDisplay(Action<bool, string> check)
    {
        string display = Source("ResultsDisplay.cs");
        string panel = Source("PanelBehaviour.cs");

        check(display.Contains("private static int _boardDrawnFrame = -1;"),
            "the display keeps the frame of the last repaint that really painted the board");
        int opening = display.IndexOf("_boardDrawnFrame = -1;", display.IndexOf("internal static void Draw()", StringComparison.Ordinal), StringComparison.Ordinal);
        int gate = display.IndexOf("ResultVisibility.Shows(", display.IndexOf("internal static void Draw()", StringComparison.Ordinal), StringComparison.Ordinal);
        check(opening > 0 && gate > opening,
            "every early return of the repaint is behind the 'nothing is showing' write, so a refused or hidden frame can never leave a stale board behind");
        check(display.Contains("HudTable.Draw(_grid, _origin.X, _origin.Y, _content, opacity);\n        // A board really went on the page this repaint.")
              && display.Contains("_boardDrawnFrame = Time.frameCount;"),
            "the draw itself is what records the board as on the page");

        string toggle = Between(display, "internal static bool ToggleForcedFallback()", "// The drawn half of the 1.19.22");
        check(toggle.Length > 0, "the F8 handler is found in the production source");
        check(toggle.Contains("ResultTogglePolicy.Next(BoardOnScreen)"),
            "the press asks the display what the page is showing, through the policy this suite drives");
        check(display.Contains("internal static bool BoardOnScreen => ResultTogglePolicy.BoardUp(_boardDrawnFrame, Time.frameCount);"),
            "and that answer is the drawn frame compared by the policy, not the override flag");
        check(!toggle.Contains("Forced = !Forced;"),
            "the override is no longer flipped blind: the press that should hide a board cannot turn it on");
        check(toggle.Contains("Forced = show;") && toggle.Contains("Plugin.PersistToggle(show);"),
            "the show half turns the override on, and both halves write the two switches the key owns to the .cfg");
        check(toggle.Contains("_forcedOutcomePending = show;") && toggle.Contains("return show;"),
            "a show still owes its frame a line, and the panel is handed whether the board is up now");

        check(panel.Contains("bool shown = ResultsDisplay.ToggleForcedFallback();") && panel.Contains("visible = shown;"),
            "the panel takes the display's answer instead of assuming a press means 'board on'");
        check(!panel.Contains("if (forced) visible = true;") && panel.Contains("F8 on a result page"),
            "and the old 'a press always shows' branch is gone while the press still writes its own line");
        check(display.Contains("|| Forced;") && display.Contains("|| Forced))"),
            "the show half still outranks every visibility switch and every gate, exactly as it did before");
    }
    // A `Transform` and a `CanvasGroup` are two components of one GameObject: siblings, not a base class and its subclass. Both
    // alpha walks used to do `node.TryCast<CanvasGroup>()` on the Transform itself (1.19.25 and earlier), which asks the wrong
    // component and can only answer null - so every group above a control read as alpha 1. The visible-control scan therefore
    // counted a control the page had faded out as a wall the board had to step around, and the failure page's title alpha
    // (the anchor the board waits for) read as fully drawn inside a group the page was still fading in. The group has to be
    // asked of the object it belongs to: `GetComponent<CanvasGroup>()`.
    //
    // Everything else about both walks is unchanged and asserted below: the ancestor chain, the bound, the multiplication, a
    // missing group reading as alpha 1, an unreadable control still answering 1 (control) or unreadable (heading), and the
    // same decisions the scan and the title gate already made with those numbers.
    private static void ACanvasGroupIsAskedOfTheObjectNotCastFromTheTransform(Action<bool, string> check)
    {
        string display = Source("ResultsDisplay.cs");
        string control = Between(display, "private static float ControlAlpha(Component element)",
            "private static string DescribeButton(Component element)");
        string heading = Between(display, "private static float HeadingAlpha(TextMeshPro heading)",
            "private static bool LobbyButtonTop(Component page, out float top)");
        check(control.Length > 0 && heading.Length > 0, "both alpha walks are found in the production source");
        string controlCode = Code(control), headingCode = Code(heading), displayCode = Code(display);

        check(controlCode.Contains("Transform node = element.transform;") && headingCode.Contains("Transform node = heading.transform;"),
            "both walks hold a Transform - the component they start from, not a CanvasGroup");
        check(controlCode.Contains("var group = node.GetComponent<CanvasGroup>();")
              && headingCode.Contains("var canvasGroup = node.GetComponent<CanvasGroup>();"),
            "and the group is asked of that object's component: a Transform is not a CanvasGroup, they are siblings on one GameObject");
        check(controlCode.IndexOf("TryCast<CanvasGroup", StringComparison.Ordinal) < 0
              && headingCode.IndexOf("TryCast<CanvasGroup", StringComparison.Ordinal) < 0
              && displayCode.IndexOf("TryCast<CanvasGroup", StringComparison.Ordinal) < 0,
            "no line of code casts a Transform to a CanvasGroup any more - the cast can only answer null, and nothing in a compile says so");

        // The original contract, kept exactly: a group that is not there is alpha 1, the whole chain multiplies, an unreadable
        // group alpha is ignored by the control walk and makes the heading unreadable, and an unreadable object stays protected.
        check(controlCode.Contains("if (group != null && float.IsFinite(group.alpha)) alpha *= Mathf.Clamp01(group.alpha);"),
            "a control with no group above it still composes to its own drawn alpha: the absent group is alpha 1, exactly as before");
        check(controlCode.Contains("while (node != null && guard++ < 32)") && controlCode.Contains("node = node.parent;")
              && headingCode.Contains("while (node != null && guard++ < 32)") && headingCode.Contains("node = node.parent;"),
            "both walks still climb the whole ancestor chain, bounded - so a nested group multiplies in, as it always did");
        check(headingCode.Contains("if (!float.IsFinite(groupAlpha)) return float.NaN;")
              && headingCode.Contains("group *= Mathf.Clamp01(groupAlpha);"),
            "an unreadable group alpha still makes the heading unreadable rather than opaque, and a real one still multiplies");
        check(controlCode.Contains("catch { return 1f; }") && headingCode.Contains("catch { return float.NaN; }"),
            "and an object that throws is still protected the way each caller expected: 1 for a control, unreadable for a heading");

        // The decisions those numbers feed, driven through the real policies: this is the "visible control / alpha safety" half.
        check(!ResultsButtonPolicy.Visible(true, 0f, 10f, 20f, 1920f, 1080f)
              && !ResultsButtonPolicy.Visible(true, 0.01f, 10f, 20f, 1920f, 1080f),
            "a control the page holds at zero alpha is not a wall the board has to clear - which is what the composed group alpha is for");
        check(ResultsButtonPolicy.Visible(true, float.NaN, 10f, 20f, 1920f, 1080f)
              && ResultsButtonPolicy.Visible(true, 1f, 10f, 20f, 1920f, 1080f)
              && !ResultsButtonPolicy.Visible(false, 1f, 10f, 20f, 1920f, 1080f),
            "while an alpha nobody could read keeps the control, a drawn control is a wall, and a switched-off one is not");
        check(!ResultFooterTextPolicy.Counts(true, 0f) && ResultFooterTextPolicy.Counts(true, float.NaN)
              && ResultFooterTextPolicy.Counts(true, 0.5f),
            "the footer-text rule keeps the same convention: faded out is not a wall, unreadable is");
    }

    // 1.19.26: the forced frame's "board drawn" line was written before the board was drawn - once from the fit, and once just
    // ahead of `HudTable.Draw`. Neither could be backed by a pixel: a fit is a placement, and a draw that has not run yet has
    // painted nothing. The answer now comes from the repaint, after the draw, and says what it really is.
    private static void TheForcedFrameLineIsWrittenAfterTheDraw(Action<bool, string> check)
    {
        string display = Source("ResultsDisplay.cs");
        int draw = display.IndexOf("HudTable.Draw(_grid, _origin.X", StringComparison.Ordinal);
        int answer = display.IndexOf("ReportForcedDraw(_origin);", StringComparison.Ordinal);
        check(draw > 0 && answer > draw,
            "the forced frame's line is written after the board's draw call, not before it");
        check(display.IndexOf("ReportForcedDraw(origin);", StringComparison.Ordinal) < 0,
            "and the fit no longer answers it: a board that was fitted is not a board that was drawn, and Draw can still refuse that same frame");
        check(answer > display.IndexOf("_boardDrawnFrame = Time.frameCount;", StringComparison.Ordinal),
            "it follows the fact it reports, in the same order the repaint records it");

        string method = Between(display, "private static void ReportForcedDraw(LayoutRect origin)", "private static bool _placed;");
        string methodCode = Code(method);
        check(methodCode.Contains("board draw issued") || method.Contains("board draw issued"),
            "the line says the draw call was issued on that repaint");
        check(!method.Contains("board drawn") && !method.Contains("the press ended with a board on this frame"),
            "and it no longer claims a board was on the frame or that pixels were painted - there is no pixel readback to back that with");
        check(method.Contains("this line reports that call, not a pixel readback of the page"),
            "the wording names its own limit, so a reader cannot take it for a screenshot");
        check(methodCode.Contains("if (!Forced || !_forcedOutcomePending) return;")
              && methodCode.Contains("_forcedOutcomePending = false;"),
            "one line per press is unchanged: the flag is still spent exactly once, here or at a refusal site");
    }


    // ---- wiring, read from the production source -----------------------------------------------

    private static void Wiring(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the 1.19.26 regression locates the production sources");
        if (root == null) return;
        Console.WriteLine("WIRING: RELEASE_11926_F8_FROM_THE_DISPLAY_AND_SECOND_LOOK_FOOTER (static source check; the press and the recovery are the runtime assertions above)");
    }

    // The re-measure budget is the page's, and only a new page refunds it (independent review F2). `DeferBoardForInteraction`
    // used to call `Drift.Forget()`, which reset `_recoveries` along with the recorded strip: every withdrawal therefore handed
    // the page its two re-measures back, and a page whose edges keep moving could be placed, drift, be withdrawn, refunded and
    // placed again without end. Driven here on the real `ResultAnchorDrift`, over fifty placements of a moving page.
    private static void ADeferralDoesNotRefundTheDriftBudget(Action<bool, string> check)
    {
        const int Rounds = 50;
        var drift = new ResultAnchorDrift();
        int granted = 0;
        for (int round = 0; round < Rounds; round++)
        {
            drift.Record(496f, 688f);                          // the board is placed (or re-placed) into the strip the page shows
            float movedRoom = 688f + (round + 1) * 80f;        // and the footer really moved under it
            // `AnchorDriftDue`: two agreeing samples of a real move, and a budget that has not been spent - one call per repaint,
            // so the two are asked separately (a short-circuit expression would be one repaint, not two).
            bool first = drift.Due(496f, movedRoom);
            bool second = drift.Due(496f, movedRoom);
            if (first || second)
            {
                granted++;
                drift.Spent();
            }
            drift.Clear();                                    // `DeferBoardForInteraction`: the withdrawn board's strip goes
        }
        check(granted == ResultAnchorDrift.MaximumRecoveries,
            $"{Rounds} placements of a page that keeps moving cost {granted} re-measure(s): the page's whole budget of {ResultAnchorDrift.MaximumRecoveries}, because a deferral no longer refunds it");
        check(drift.Exhausted && drift.Recoveries == ResultAnchorDrift.MaximumRecoveries,
            "and the budget ends saturated, so no later frame of that page can re-measure again");

        // What a deferral may still do is forget the strip: it describes a board that has just been withdrawn, and no second
        // look may be granted against it. Forgetting it must not spend a re-measure either.
        var deferred = new ResultAnchorDrift();
        deferred.Record(496f, 688f);
        deferred.Clear();
        check(!deferred.Armed, "a deferral forgets the recorded strip: it described a board that is no longer on the page");
        check(!deferred.Due(496f, 900f) && !deferred.Due(496f, 900f),
            "so the repaints after it are owed nothing: no re-measure is granted against a strip that is gone");
        check(deferred.Recoveries == 0 && !deferred.Exhausted, "and the withdrawal itself spends none of the two re-measures");

        // A different result page is the one thing that resets the budget, and it still does.
        var page = new ResultAnchorDrift();
        int looks = 0;
        for (int attempt = 0; attempt < 20 && !page.Exhausted; attempt++)
        {
            page.Record(496f, 688f);
            bool first = page.Due(496f, 900f);
            bool second = page.Due(496f, 900f);
            if (first || second) { looks++; page.Spent(); }
        }
        check(page.Exhausted && looks == ResultAnchorDrift.MaximumRecoveries,
            $"one page may be re-measured {ResultAnchorDrift.MaximumRecoveries} times and no more: {looks} look(s) before it saturated");
        page.Forget();                                        // `ForgetPage`
        check(!page.Exhausted && page.Recoveries == 0 && !page.Armed,
            "a new result page starts with both re-measures back and no strip: the per-page reset lives in `ForgetPage` alone");
        page.Record(496f, 688f);
        bool fresh = page.Due(496f, 900f);
        check(!fresh && page.Due(496f, 900f),
            "and that page's own move is measured again after two agreeing samples - the ordinary drift protection is untouched");

        string display = Source("ResultsDisplay.cs");
        string defer = Code(Between(display, "private static void DeferBoardForInteraction()", "// ---- the fail-open placement"));
        check(defer.Length > 0, "the deferral is found in the production source");
        check(defer.Contains("Drift.Clear();"),
            "the deferral calls `Clear`, which drops the strip the withdrawn board was measured into and keeps the budget");
        check(!defer.Contains("Drift.Forget();"),
            "and no longer refunds the page's two re-measures (independent review F2)");
        string forgetPage = Code(Between(display, "private static void ForgetPage()", "// The page's final geometry"));
        check(forgetPage.Contains("Drift.Forget();"),
            "a new page still resets them: that reset lives in `ForgetPage` alone");
        string policy = Code(Source("ResultsPagePolicy.cs"));
        string clear = Between(policy, "public void Clear()", "public bool Due(");
        check(clear.Contains("_top = _room = float.NaN;") && clear.Contains("_frames = 0;"),
            "the policy really separates the two: `Clear` drops the recorded strip and the frame it was sampled on");
        check(clear.Length > 0 && !clear.Contains("_recoveries"),
            "and not the budget: `_recoveries` is nowhere in it");

    }
    // ---- helpers --------------------------------------------------------------------------------------------------

    private static string Source(string name)
    {
        string root = SourceRoot();
        if (root == null) throw new InvalidOperationException("The plugin source directory could not be found from " + AppContext.BaseDirectory);
        string path = Path.Combine(root, name);
        if (!File.Exists(path)) throw new FileNotFoundException("Production source is missing", path);
        return File.ReadAllText(path);
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj"))) directory = directory.Parent;
        return directory != null ? directory.FullName : null;
    }

    private static string Between(string text, string from, string to)
    {
        int start = text.IndexOf(from, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        int end = text.IndexOf(to, start, StringComparison.Ordinal);
        return end < 0 ? text.Substring(start) : text.Substring(start, end - start);
    }

    // The text with its comment lines removed: a comment may name a call in order to explain why it is wrong, so the "this code
    // must not do X" checks have to read the code that really runs.
    private static string Code(string text)
    {
        var kept = new List<string>();
        if (text == null) return string.Empty;
        foreach (string line in text.Split('\n'))
            if (!line.TrimStart().StartsWith("//", StringComparison.Ordinal)) kept.Add(line);
        return string.Join("\n", kept);
    }
}
