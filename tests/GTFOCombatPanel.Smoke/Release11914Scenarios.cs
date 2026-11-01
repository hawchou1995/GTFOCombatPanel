using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// The defect 1.19.14 fixes, driven through the real policies it ships with: a success page that carried one
// active control whose rectangle could not be read was refused for ever.
//
// The live report is a success page where another mod clones the game's own lobby button the moment the page
// opens. The clone's collider only exists once the clone has been laid out, so for a frame or two there is an
// active, pressable control whose bounds are the zero box, and the interaction scan read that as "this scan
// could not finish". The board was then held back on that frame, and on every frame after it - the clone was
// still there on the next repaint, so re-measuring could never clear the condition - which is why the settlement
// board simply stopped appearing.
//
// What is asserted here is the rule that replaced it:
//
//   * an active control whose rectangle nobody can read is a fact about one frame of that control, not about the
//     page. It keeps its place in the count - a player could really press it - and is counted separately as
//     unmeasurable, but it no longer makes the scan incomplete and it no longer keeps the strip from resolving.
//     The per-repaint rescan is what takes it back the moment it has geometry that reaches the board.
//   * a hole in the page's *structure* - the item registry itself unreadable, a parent node that cannot be
//     reached, a type conversion that fails, the client flag nobody can read - is still fatal. Nothing the page
//     draws later repairs any of those, so the vanilla screen stays up.
//   * the answer "this page has no footer at all" is not available while an unmeasurable control is around: it is
//     an interaction nobody could place, so the guest safe area is never granted on the strength of it. That answer
//     is read off the scan's own count of interactions and never off the number of rectangles the strip could
//     place - the measurable subset is a diagnostic of the strip, and reading the answer off it is how a page
//     whose every active control went unmeasured claimed to have no footer at all.
//
// Everything below runs against the real `ResultInteractionTally`, `ResultInteractionScanPolicy`,
// `ResultInteractionStripPolicy` and `ResultNoFooterGate` of this build. The section at the end reads the
// production source instead, and says so.

internal static class Release11914Scenarios
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

        AnUnmeasurableControlNoLongerCondemnsTheScan(check);
        AnUnreadableRegistryStillCondemnsTheScan(check);
        UnmeasurableControlsKeepThePageFromClaimingNoFooter(check);
        HiddenAndUnmeasurableControlsAreCountedApart(check);
        TheScanTakesAnUnmeasurableControlBackOnceItCanBeMeasured(check);
        Wiring(check);
    }

    // ------------------------------------------------------- one clone, one measurable control
    //
    // The page the report came from, reduced to its interactions: the game's own lobby button with a real
    // rectangle, and the clone beside it whose bounds are the zero box. The scan has to finish, the strip has to
    // be built from the one rectangle it could read, and the clone has to be visible in the counts.
    private static void AnUnmeasurableControlNoLongerCondemnsTheScan(Action<bool, string> check)
    {
        var tally = new ResultInteractionTally(0, 0, 0, 0);
        tally = tally.ActiveControl();                                    // the lobby button
        var actions = new List<ResultActionBounds>
        {
            new ResultActionBounds(new LayoutRect(120f, 1180f, 400f, 70f))
        };
        tally = tally.ActiveControl();                                    // the clone
        tally = tally.UnmeasurableControl();                              // ... whose rectangle is the zero box

        check(tally.Active == 2 && tally.Unmeasurable == 1 && tally.Hidden == 0,
            $"the clone is an interaction and an unmeasurable one at the same time: active={tally.Active}, unmeasurable={tally.Unmeasurable}");

        check(ResultInteractionScanPolicy.ProvenEmpty(true, true, tally) == false,
            "a page with an interaction on it - measurable or not - never proves it has no footer");

        // The strip is resolved from the rectangles that could be read. The zero box contributes nothing, which is
        // exactly the answer the scan must give: the board is placed against the button that really is there.
        check(ResultInteractionStripPolicy.TryResolve(500f, 16f, actions, out ResultInteractionStrip strip)
            && Math.Abs(strip.BoardTop - 516f) < 0.01f && Math.Abs(strip.FooterTop - 1180f) < 0.01f
            && strip.FooterActionCount == 1,
            $"the strip is built from the one readable control: boardTop={strip.BoardTop:0}, footerTop={strip.FooterTop:0}");

        check(ResultInteractionScanPolicy.DowngradeIsWhy(true, true, tally),
            "a scan that is complete while carrying an unmeasurable control is exactly the case the one-off line exists for");

        // The same page with no unmeasurable control says nothing, so the line cannot become per-frame noise.
        var clean = new ResultInteractionTally(2, 0, 0, 0);
        check(!ResultInteractionScanPolicy.DowngradeIsWhy(true, true, clean),
            "a scan in which every control could be read has nothing to say about unmeasurable controls");
        check(!ResultInteractionScanPolicy.DowngradeIsWhy(false, true, new ResultInteractionTally(1, 0, 0, 1)),
            "a scan refused for a structural reason does not claim the unmeasurable control was what let it through");
    }

    // ------------------------------------------------------- a hole in the page's structure stays fatal
    //
    // `ResultInteractionScanPolicy.ProvenEmpty` is the only answer that ever releases the unbounded safe area, so
    // it has to keep refusing everything a scan could not see. The registry the report's page exposes
    // (`m_allItems`) is read through the same policy: a scan that never ran to the end of it has not seen the page.
    private static void AnUnreadableRegistryStillCondemnsTheScan(Action<bool, string> check)
    {
        // No controls were found and there is nothing unmeasurable, but the registry itself was unreadable.
        check(!ResultInteractionScanPolicy.ProvenEmpty(false, true, new ResultInteractionTally(0, 0, 0, 0)),
            "an unreadable item registry keeps the page from proving it has no footer, however empty the scan looked");
        check(!ResultInteractionScanPolicy.DowngradeIsWhy(false, true, new ResultInteractionTally(0, 0, 0, 0)),
            "and the downgrade line is never used to explain a structural refusal");

        // The same for the coverage edge of the named action parent, whichever flag reports it.
        check(!ResultInteractionScanPolicy.ProvenEmpty(true, false, new ResultInteractionTally(0, 0, 0, 0)),
            "a parent node that could not be reached keeps the page from proving it has no footer");

        // The gate the runtime asks before it releases the safe area is unchanged by this release: a complete scan
        // with no interaction is the only thing that lets it through, and "complete" is still false for every
        // structural hole.
        var gate = new ResultNoFooterGate();
        bool neverReady = false;
        for (int frame = 1; frame <= ResultNoFooterGate.RequiredSamples + 5; frame++)
            neverReady |= gate.Observe(frame, true, true, true, false, false, 0);
        check(!neverReady && !gate.Ready,
            "an incomplete scan never reaches the guest safe area, however many frames it is sampled");

        var ready = new ResultNoFooterGate();
        bool eventuallyReady = false;
        for (int frame = 1; frame <= ResultNoFooterGate.RequiredSamples; frame++)
            eventuallyReady = ready.Observe(frame, true, true, true, false, true, 0);
        check(eventuallyReady,
            "a scan that really finished with nothing on the page still releases the safe area, so the refusal above is about the hole and not about the gate");
    }

    // ------------------------------------------------------- "no footer" is not available for free
    //
    // The page whose every control is unmeasurable is the worst case for the safe area: nothing could be placed,
    // and it is tempting to read that as "nothing is there". It is not - the interactions exist, so the safe area
    // stays refused, and the count is what keeps it refused.
    private static void UnmeasurableControlsKeepThePageFromClaimingNoFooter(Action<bool, string> check)
    {
        // Exactly the tally production builds for that page: the scan counts two active controls, the strip can
        // place neither rectangle, so the counts it hands `FooterControls` are active = 2 and unmeasurable = 2 -
        // not the zero the strip measured. The zero-measured shape is asserted below, because that is the one
        // this release shipped and the one that let the board through.
        var allUnmeasurable = new ResultInteractionTally(2, 0, 0, 2);
        check(ResultInteractionScanPolicy.ProvenEmpty(true, true, allUnmeasurable) == false,
            "two controls whose rectangles nobody could read are still two interactions, so the page has not been proved empty");

        check(ResultInteractionStripPolicy.TryResolve(500f, 16f, new List<ResultActionBounds>(), out ResultInteractionStrip emptyStrip)
            && Math.Abs(emptyStrip.BoardTop - 516f) < 0.01f && !float.IsFinite(emptyStrip.FooterTop),
            "with no readable rectangle the strip is the whole area under the anchor, which is why the count and not the strip has to decide");

        // The gate is therefore never told "complete and empty", and the safe area stays refused for as long as the
        // controls are there.
        var gate = new ResultNoFooterGate();
        bool everReleased = false;
        var tally = allUnmeasurable;
        for (int frame = 1; frame <= ResultNoFooterGate.RequiredSamples + 5; frame++)
        {
            bool provenEmpty = ResultInteractionScanPolicy.ProvenEmpty(true, true, tally);
            everReleased |= gate.Observe(frame, true, true, true, false, true, provenEmpty ? 0 : tally.Active);
        }
        check(!everReleased && !gate.Ready,
            "the unbounded guest safe area is never granted while an interaction on the page could not be measured");

        // The page whose only interaction was the one that could not be measured is the same answer.
        var oneBlind = new ResultInteractionTally(1, 0, 0, 1);
        check(!ResultInteractionScanPolicy.ProvenEmpty(true, true, oneBlind),
            "one unmeasurable interaction is enough to keep the page from claiming it has no footer");

        // The shape the shipped wiring really produced: an active count of zero, because the strip had no rectangle
        // to place, carrying the interactions it merely could not measure. Reading the answer off the measured
        // subset turned "every active control went unmeasured" into "the page is empty" - and the guest safe area
        // then handed the board the whole screen down to the footer that was still there. Both counts decide now.
        check(!ResultInteractionScanPolicy.ProvenEmpty(true, true, new ResultInteractionTally(0, 0, 0, 1)),
            "an active count of zero carrying one unmeasurable interaction is not an empty page: a strip that measured nothing is not a page that has nothing");
        check(!ResultInteractionScanPolicy.ProvenEmpty(true, true, new ResultInteractionTally(0, 0, 0, 2)),
            "and neither is the same page with both of its interactions unmeasured");

        // The gate refuses that pair as well, so a caller that reports the unmeasurable count separately cannot
        // release the safe area by reporting an active count of zero.
        var separate = new ResultNoFooterGate();
        bool separatelyReleased = false;
        for (int frame = 1; frame <= ResultNoFooterGate.RequiredSamples + 2; frame++)
            separatelyReleased |= separate.Observe(frame, true, true, true, false, true, 0, 1);
        check(!separatelyReleased && !separate.Ready,
            "the guest safe-area gate keeps its full sample budget refused while any interaction on the page could not be measured, however the caller splits the counts");

        // A page that really has nothing on it still does claim it, so the rule did not simply become "never".
        check(ResultInteractionScanPolicy.ProvenEmpty(true, true, new ResultInteractionTally(0, 0, 0, 0)),
            "a scan that finished and found nothing still proves the page empty");
        var empty = new ResultNoFooterGate();
        bool emptyReleased = false;
        for (int frame = 1; frame <= ResultNoFooterGate.RequiredSamples; frame++)
            emptyReleased |= empty.Observe(frame, true, true, true, false, true, 0, 0);
        check(emptyReleased,
            "and the same gate still releases the safe area for that page on its third distinct frame, so the refusal above is about the interactions and not about the counts being read at all");
    }

    // ------------------------------------------------------- two kinds of control that cannot be pressed
    private static void HiddenAndUnmeasurableControlsAreCountedApart(Action<bool, string> check)
    {
        var tally = new ResultInteractionTally(0, 0, 0, 0);
        tally = tally.HiddenControl(false);          // the game switched a page action off
        tally = tally.HiddenControl(true);           // ... one of the page's own result actions
        tally = tally.ActiveControl();               // something the player can press
        tally = tally.UnmeasurableControl();         // ... with no rectangle this frame

        check(tally.Hidden == 2 && tally.HiddenPageActions == 1,
            $"the controls the game switched off keep their own count and their own page-action count: hidden={tally.Hidden}, pageActions={tally.HiddenPageActions}");
        check(tally.Unmeasurable == 1,
            $"an active control nobody could measure is not merged into the hidden count: unmeasurable={tally.Unmeasurable}");
        check(tally.Active == 1,
            $"only the control the page switched on is counted as an interaction: active={tally.Active}");
        check(tally.Hidden + tally.Unmeasurable + tally.Active == 4,
            "every control the scan visited lands in exactly one of the three counts, so none of them can swallow another");

        // A scan that visits only hidden controls has nothing to measure and nothing to protect.
        var onlyHidden = new ResultInteractionTally(0, 3, 1, 0);
        check(ResultInteractionScanPolicy.ProvenEmpty(true, true, onlyHidden),
            "a page whose only controls are switched off can still prove it has no footer, which is the 1.19.13 rule this release leaves alone");
        check(!ResultInteractionScanPolicy.DowngradeIsWhy(true, true, onlyHidden),
            "and a hidden control is not an unmeasurable one, so it never produces the downgrade line");
    }

    // ------------------------------------------------------- the safety net underneath the downgrade
    //
    // The downgrade only ever says "press on with the rectangles that could be read". The moment the control has a
    // rectangle, the same strip arithmetic and the same collision test the runtime runs decide whether it reaches
    // the board, so a clone that lands over the table still takes the vanilla page back.
    private static void TheScanTakesAnUnmeasurableControlBackOnceItCanBeMeasured(Action<bool, string> check)
    {
        // The board the scan placed on the page from the first case.
        var board = new LayoutRect(120f, 560f, 900f, 400f);
        LayoutRect cloneOnTheBoard = new LayoutRect(300f, 700f, 300f, 60f);
        LayoutRect cloneBelowTheBoard = new LayoutRect(300f, 1180f, 300f, 60f);

        check(ResultInteractionStripPolicy.OverlapsBoard(cloneOnTheBoard, board, ResultBoardHold.ButtonClearance),
            "the clone whose rectangle has arrived and reaches the placed board is an overlap, so `BoardStillSafe` restores vanilla on that frame");
        check(!ResultInteractionStripPolicy.OverlapsBoard(cloneBelowTheBoard, board, ResultBoardHold.ButtonClearance),
            "a clone that reads cleanly and sits below the board is nothing to worry about, so the board stays up");

        // And once it has a rectangle it is an ordinary lower wall for the strip as well.
        var actions = new List<ResultActionBounds> { new ResultActionBounds(cloneOnTheBoard) };
        check(ResultInteractionStripPolicy.TryResolve(500f, 16f, actions, out ResultInteractionStrip strip)
            && Math.Abs(strip.FooterTop - 700f) < 0.01f && strip.FooterActionCount == 1,
            $"a control that could not be measured a moment ago is the measured lower wall the moment it can be: footerTop={strip.FooterTop:0}");

        // The count is unchanged by that transition: the control was an interaction before it was measurable and it
        // is one afterwards, so no frame in between can make the page look emptier than it is.
        var before = new ResultInteractionTally(1, 0, 0, 1);
        var after = new ResultInteractionTally(1, 0, 0, 0);
        check(before.Active == after.Active && !ResultInteractionScanPolicy.ProvenEmpty(true, true, before)
              && !ResultInteractionScanPolicy.ProvenEmpty(true, true, after),
            "the control is an interaction on both sides of the moment its geometry becomes readable");
    }

    // ------------------------------------------------------- the wiring, read from the production source
    //
    // A static source check only: it says the runtime really builds the tally described above and really draws the
    // one-off line. It is not a runtime behaviour test and does not pretend to be one.
    private static void Wiring(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the 1.19.14 regression locates the production sources");
        if (root == null) return;
        string results = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        string layout = File.ReadAllText(Path.Combine(root, "LayoutPolicy.cs"));

        check(layout.Contains("public readonly struct ResultInteractionTally")
              && layout.Contains("public static class ResultInteractionScanPolicy")
              && layout.Contains("tally.Active == 0 && tally.Unmeasurable == 0"),
            "the scan tally and the two decisions read off it live in the Unity-free policy file this suite drives");

        int addControl = results.IndexOf("void AddControl(Component control)", StringComparison.Ordinal);
        int unmeasurable = results.IndexOf("tally = tally.UnmeasurableControl();", addControl, StringComparison.Ordinal);
        int noVerdict = results.IndexOf("complete = false;", unmeasurable, StringComparison.Ordinal);
        check(addControl >= 0 && unmeasurable > addControl,
            "an active control whose rectangle cannot be read is counted as unmeasurable");
        check(!results.Contains("active-control-unmeasurable"),
            "no code path makes an unmeasurable control a reason the scan could not finish");
        // The line the assertion above used to leave dangling: the unmeasurable branch returns before any
        // structural refusal, so a control whose rectangle could not be read never writes `complete = false` and
        // never names a reason. That is the whole downgrade, and it is read from the source rather than assumed.
        int unmeasurableBranchEnd = results.IndexOf("return;", unmeasurable, StringComparison.Ordinal);
        check(unmeasurableBranchEnd > unmeasurable && noVerdict > unmeasurableBranchEnd
              && !results.Substring(unmeasurable, unmeasurableBranchEnd - unmeasurable).Contains("complete = false"),
            "the unmeasurable-control branch returns before the next structural refusal, so that control does not make the scan incomplete");

        // The structural refusals are still there, one by one: this release narrowed the geometry case only.
        foreach (string structural in new[]
        {
            "reason = \"client-state-unreadable\"",
            "reason = \"pointer-unreadable\"",
            "reason = \"active-state-unreadable\"",
            "reason = \"action-parent-missing\"",
            "reason = \"action-parent-unreadable\"",
            "reason = \"action-parent-scan-failed\"",
            "reason = \"page-type-unreadable\"",
            "reason = \"registry-unreadable\"",
            "reason = \"registry-entry-unreadable\"",
            "reason = \"failure-page-type-unreadable\"",
            "reason = \"page-scan-failed\"",
            "reason = \"strip-unresolved\""
        })
            check(results.Contains(structural), "the structural refusal " + structural + " is still in place");

        check(results.Contains("ResultInteractionScanPolicy.ProvenEmpty(Complete, ParentCoverageComplete, Tally)")
              && results.Contains("public readonly ResultInteractionTally Tally;"),
            "the no-footer answer is decided by the counted interactions rather than by the measurable ones");

        // BLOCK-1: the count handed to `FooterControls` - and from there into `Tally`, `ProvenEmpty`,
        // `BoardStillSafe` and `ResultNoFooterGate.Observe` - is the scan's own active count, measurable or not.
        // `strip.FooterActionCount` counts the rectangles the strip could place, which is a diagnostic of the strip
        // and nothing else; handing that to the no-footer answer is what let a page whose every active control went
        // unmeasured claim to have no footer and take the whole screen down to the footer still drawn on it.
        check(results.Contains("new FooterControls(strip.FooterTop, strip.BoardTop, tally.Active,")
              && !results.Contains("strip.FooterActionCount, tally.Active"),
            "the scan hands `FooterControls` the interactions it found rather than the subset its strip could measure");
        check(results.Contains("successControls.Complete, successControls.Count, successControls.Unmeasurable)")
              && results.Contains("failureControls.Complete, failureControls.Count, failureControls.Unmeasurable)"),
            "both guest safe-area gates are told the active count and the unmeasurable count, so neither can be released with an active count of zero while a control went unmeasured");
        check(results.Contains("tally.Active, tally.Hidden + tally.Active,"),
            "the diagnostic `scanned=` count is every control the scan visited, switched on or off, so the log line no longer reports the active count as the visited one");
        int protectedText = results.IndexOf("if (protectedPageText != null)", StringComparison.Ordinal);
        int protectedUnmeasurable = protectedText >= 0
            ? results.IndexOf("else if (!TryScreenRect(protectedPageText, out LayoutRect rect))", protectedText, StringComparison.Ordinal) : -1;
        int protectedCounted = protectedUnmeasurable >= 0
            ? results.IndexOf("tally = tally.ActiveControl();", protectedUnmeasurable, StringComparison.Ordinal) : -1;
        int protectedMeasured = protectedUnmeasurable >= 0
            ? results.IndexOf("protectedPageText: true", protectedUnmeasurable, StringComparison.Ordinal) : -1;
        check(protectedText >= 0 && protectedUnmeasurable > protectedText && protectedCounted > protectedUnmeasurable
              && protectedMeasured > protectedCounted,
            "the protected waiting-status line whose rectangle cannot be read counts as an interaction, so a page carrying it can never prove it has no footer and the board can never be given the strip it sits in");

        check(results.Contains("ReportDowngradedControls(\"success\"")
              && results.Contains("ReportDowngradedControls(\"failure\"")
              && results.Contains("unmeasurable={controls.Unmeasurable}")
              && results.Contains("if (!HeadingReports.Add(\"control-downgraded-\" + where)) return;"),
            "the held-back line names the unmeasurable count and the downgrade is reported once per page visit");

        int stillSafe = results.IndexOf("if (!BoardStillSafe())", StringComparison.Ordinal);
        int defer = results.IndexOf("DeferBoardForInteraction();", stillSafe, StringComparison.Ordinal);
        int scan = results.IndexOf("controls = ScanFooterControls(page, completion, MessageGap, null, board, inspectBoard);", StringComparison.Ordinal);
        check(stillSafe >= 0 && defer > stillSafe && scan >= 0,
            "the per-repaint rescan that asks for the collision set again is untouched, which is what takes an unmeasurable control back");
        Console.WriteLine("WIRING: RESULT_UNMEASURABLE_CONTROL (static source check; not a runtime behaviour test)");
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj"))) directory = directory.Parent;
        return directory != null ? directory.FullName : null;
    }
}
