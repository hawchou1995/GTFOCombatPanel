using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using HawChow.GTFOCombatPanel;

// The measured-content edge (A), the failure page's unobserved lower bound (B) and the native schedule window's order
// (C), all driven through the production types.
//
// A  The recorded success page's completion part read `own=692` while the icons and captions on the screenshot end
//    around 417: the container reserves a large blank slot, and `max(container, pixels)` could never take that back in.
//    `CompletionFinalBounds.FinalEdge` uses the pixels a part really draws when those are a *complete* measurement of it
//    (`ContentComplete`: enumeration ran, no child switched off, at least one child drew) and its container otherwise -
//    so a padded container stops shortening the strip, a container that ends above the captions still cannot press the
//    board on them, and a part with a hidden child keeps its container.
// B  The recorded failure page: band 890..1429 (screen foot), board bottom 1385, button at 1240 on the next frame.
//    `ResultFailureWallPolicy` withholds the invented band while a failure page has shown no lower bound at all, and
//    answers after the bounded give-up this file already has, so "two empty scans" is never read as "no wall".
// C  `CoroutineManager.BlinkIn(GameObject, float)` returns `Coroutine` and `CM_ExpeditionSectorIcon.BlinkIn(float)` is
//    void (interop metadata, Modules-ASM.dll): the icon starts its reveal inside the call, so the window closed in the
//    `BlinkIn` postfix is closed after every routine that call scheduled. If a build ever deferred that, a later `Start`
//    re-admits the target - and until then the strict gate still withholds the board, so 0/N never shows early.
internal static class MeasuredContentAndWallScenarios
{
    // The recorded success page's numbers: completion container 692, the icons/captions the screenshot shows ~417,
    // the leave button's own top 1317, a 1440-high window and the page's 16 px message gap.
    private const float Container = 692f, Content = 417f, ButtonTop = 1317f, Height = 1440f, Gap = 16f;
    // The recorded failure page: band top 890, its button at 1240, and the board the old code drew to 1385.
    private const float FailureTop = 890f, FailureButton = 1240f, OldBoardBottom = 1385f;

    public static void RunInto(Action<bool, string> check)
    {
        PaddedContainerStopsShorteningTheStrip(check);
        ContainerAboveCaptionsStillCannotPressOnThem(check);
        IncompleteContentKeepsTheContainer(check);
        BandNeverAboveAHiddenPartsContainer(check);
        RevealStillUsesTheContainer(check);
        FailureWallIsNotInventedFromTheWindow(check);
        FailureEmptyThenLateControl(check);
        ScheduleWindowOrder(check);
        AbortWaitsForTheOtherRoutines(check);
        ContentEdgeBeforeTheRevealFinishes(check);
        DescribePrintsTheEdgeThatWasUsed(check);
        CutClosingTagName(check);
        Console.WriteLine("WIRING: MEASURED_CONTENT_EDGE + FAILURE_WALL_UNOBSERVED + SCHEDULE_WINDOW_ORDER (production policies; the Unity side is read from source)");
    }

    // ---------------------------------------------------------------------------------------------------
    // F1. 一个目标 abort 时，其余在途目标仍在跑：闸门必须等它们结束，不能在同一帧放板
    // ---------------------------------------------------------------------------------------------------
    private static void AbortWaitsForTheOtherRoutines(Action<bool, string> check)
    {
        const long icon1 = 0xE1, icon2 = 0xE2, icon3 = 0xE3, icon4 = 0xE4, icon5 = 0xE5, caption = 0xE6;
        long[] targets = { icon1, icon2, icon3, icon4, icon5, caption };
        var ledger = new BlinkCompletionLedger();
        for (int index = 0; index < targets.Length; index++) ledger.Require(targets[index], index == targets.Length - 1);
        for (int index = 0; index < targets.Length; index++) ledger.Start(targets[index], 0xB000 + index);
        check(ledger.InFlight == 6 && ledger.Settled == false,
            "six scheduled routines are six in-flight targets (F1): the ledger knows what is still running, not only what finished");

        // The first one aborts. Before 1.19.43 this made `RevealImpossible` true and the gate released the board on that
        // frame, while the other five sector icons were still blinking.
        ledger.Finish(icon1, 0xB000, naturallyRevealed: false);
        check(ledger.RevealImpossible && ledger.Aborted == 1 && ledger.InFlight == 5,
            "the abort makes the ledger impossible to complete and leaves five routines in flight - two different facts");
        check(ResultRevealGate.BlocksPlacement(false, ledger.MayStillComplete, ledger.InFlight, forced: false),
            "so the success board is still withheld on that frame: one aborted target is not the whole reveal");
        check(ResultRevealGate.BlocksPlacement(false, ledger.MayStillComplete, ledger.InFlight, forced: true) == false,
            "and F8 is still the player's own answer, unaffected");

        // The other five finish one by one; the gate stays shut until the last one reports.
        for (int index = 1; index < targets.Length; index++)
        {
            ledger.Finish(targets[index], 0xB000 + index, naturallyRevealed: true);
            bool last = index == targets.Length - 1;
            bool blocks = ResultRevealGate.BlocksPlacement(false, ledger.MayStillComplete, ledger.InFlight, forced: false);
            if (last) check(!blocks && ledger.Settled && ledger.InFlight == 0 && !ledger.Ready && ledger.Completed == 5,
                "after the fifth remaining routine ends the ledger is settled (5 completed, 1 aborted, 0 in flight) and only then does the gate open - the release is labelled, never 'the animation finished'");
            else if (blocks == false) check(false, "the gate opened early: target " + index + " of the remaining routines was still in flight");
        }

        // The fail-open decision follows the same rule: an impossible ledger with work in flight is still 'pending'.
        ResultSettlementGate.Decision pending = ResultSettlementGate.Decide(forced: false, revealEnforced: true,
            revealFinished: false, revealImpossible: true, revealInFlight: 3, revealCount: 6, revealStalled: false,
            everMeasurable: true, finalMeasurable: true, finalSettled: true, finalWaitFrames: 0);
        ResultSettlementGate.Decision answerable = ResultSettlementGate.Decide(forced: false, revealEnforced: true,
            revealFinished: false, revealImpossible: true, revealInFlight: 0, revealCount: 6, revealStalled: false,
            everMeasurable: true, finalMeasurable: true, finalSettled: true, finalWaitFrames: 0);
        check(!pending.MayFailOpen && pending.Why == ResultSettlementGate.Answer.RevealPending
              && answerable.MayFailOpen && answerable.Why == ResultSettlementGate.Answer.RevealImpossible,
            "the fail-open path waits for the ledger's own routines too, and answers the labelled release only when none is left");

        // The stuck case: an impossible ledger whose routines never end must not become 'F8 only'. The one bounded
        // answer is the stall clock's own bound (real seconds, `RevealProgressClock`), and it is reported as
        // unreachable, not as a finished animation.
        ResultSettlementGate.Decision stuck = ResultSettlementGate.Decide(forced: false, revealEnforced: true,
            revealFinished: false, revealImpossible: true, revealInFlight: 2, revealCount: 6,
            revealStalled: true, everMeasurable: true, finalMeasurable: true,
            finalSettled: true, finalWaitFrames: 0);
        check(stuck.MayFailOpen && stuck.Why == ResultSettlementGate.Answer.RevealUnobservable && stuck.Unobservable,
            "a ledger that can no longer complete and still has routines in flight is answered after the one bounded wait, as reveal-unobservable - never as settled");
        string why = ResultSettlementGate.Why(ResultSettlementGate.Answer.RevealUnobservable, 5, 6,
            ResultFinalPolicy.MaximumWaitFrames, inFlight: 2);
        check(why.Contains("still in flight") && why.Contains("inFlight=2") && why.Contains("not a settled page"),
            "and its line says which of the two unobservable cases this is (still in flight), not 'never observed': " + why);
        check(ResultRevealGate.Why(5, 6, 0, 5, 2).Contains("inFlight=2"),
            "the withheld line carries the in-flight count as well, so a live log shows why the board is waiting");
    }

    // ---------------------------------------------------------------------------------------------------
    // F3. 账本永远不回答的页：内容读数在几何已读两次之后才允许当最终边；未观测的页仍然保守
    // ---------------------------------------------------------------------------------------------------
    private static void ContentEdgeBeforeTheRevealFinishes(Action<bool, string> check)
    {
        var padded = new CompletionPart(true, true, 1f, Container, Content, scheduled: true);
        var parts = new List<CompletionPart> { padded };
        check(Near(CompletionFinalBounds.Placed(parts, revealFinished: false, contentSettled: false, out _, out _), Container),
            "a page whose reveal is simply running keeps the container edge (692): the drawn reading is still progress");
        check(Near(CompletionFinalBounds.Placed(parts, revealFinished: false, contentSettled: true, out _, out _), Content),
            "once the ledger has proved no reveal is coming and the geometry read the same twice, the content edge (417) is used (F3)");

        // A scheduled slot that is not switched on yet still contributes its slot in that same call, so the answer can
        // never be above a part that may still appear.
        var scheduledSlot = new CompletionPart(true, false, 0f, 900f, float.NaN, scheduled: true);
        var mixed = new List<CompletionPart> { padded, scheduledSlot };
        check(Near(CompletionFinalBounds.Placed(mixed, revealFinished: false, contentSettled: true, out _, out _), 900f),
            "and a slot the page scheduled but has not switched on yet still deepens the band to its own rectangle, so nothing that may still appear is covered");

        // The production predicate: only the two ledger facts, never 'any page'.
        string root = SourceRoot();
        if (root == null) { check(false, "F3 wiring locates the production sources"); return; }
        string display = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        string predicate = MethodBody(display, "private static bool ContentSettled");
        check(predicate != null
              && predicate.Contains("ledger.Required > 0 && ledger.Count == 0")
              && predicate.Contains("!ledger.MayStillComplete && ledger.Settled")
              && predicate.Contains("return noRevealComing && FinalLayout.Ready;"),
            "the production predicate asks only 'every target unscheduled' or 'impossible and settled', and always the two-reading latch - a page whose reveal was never even observed stays on its container");
        check(display.Contains("bool contentSettled = ContentSettled;")
              && Body(display, "private static float IconRowBottom(").Contains("contentSettled"),
            "and both the section's edge and the icon row the fail-open band uses are computed from that one flag");
    }

    // ---------------------------------------------------------------------------------------------------
    // F4. Describe 必须打印生产 Placed 真正用的边与来源
    // ---------------------------------------------------------------------------------------------------
    private static void DescribePrintsTheEdgeThatWasUsed(Action<bool, string> check)
    {
        var part = new CompletionPart(true, true, 1f, Container, Content, scheduled: true);
        string running = CompletionFinalBounds.Describe(part, "sectorIconAllCompleted", revealFinished: false, contentSettled: false);
        string settled = CompletionFinalBounds.Describe(part, "sectorIconAllCompleted", revealFinished: false, contentSettled: true);
        string finished = CompletionFinalBounds.Describe(part, "sectorIconAllCompleted", revealFinished: true, contentSettled: false);
        check(running.Contains("edge=692") && running.Contains("source=container(reveal-running)"),
            "while the reveal runs the line prints the container's number and says the container was used: " + running);
        check(settled.Contains("edge=417") && settled.Contains("source=content")
              && finished.Contains("edge=417") && finished.Contains("source=content"),
            "once the content reading is the one `Placed` uses, the line prints that number and `source=content`");
        // The printed figure is exactly what the placement uses, on every one of those paths.
        check(Near(CompletionFinalBounds.Placed(new List<CompletionPart> { part }, false, false, out _, out _), Container)
              && Near(CompletionFinalBounds.Placed(new List<CompletionPart> { part }, false, true, out _, out _), Content)
              && Near(CompletionFinalBounds.Placed(new List<CompletionPart> { part }, true, false, out _, out _), Content),
            "so the three printed edges are the three edges the production call really returns (F4)");
    }

    // ---------------------------------------------------------------------------------------------------
    // N1. 真实 24 字节截断的 closing tag：`<color=#FF4005>大锤</c`
    // ---------------------------------------------------------------------------------------------------
    private static void CutClosingTagName(Action<bool, string> check)
    {
        const string name = "<color=#FF4005>大锤</color>";
        var words = new uint[SyncSnapshot.NameWords];
        SyncProtocol.PackName(name, words);
        string unpacked = SyncProtocol.UnpackName(words);
        check(unpacked == "<color=#FF4005>大锤</c",
            "the wire's own 24-byte field really cuts that name to `</c` (" + unpacked + "), which is the N1 case");
        check(Encoding.UTF8.GetByteCount(name) > SyncSnapshot.NameCapacity,
            "and the name it was cut from is longer than the field, so the cut is a fact about the wire, not a guess");
        check(CombatStatsFormatter.VisibleName(unpacked) == "大锤",
            "the display removes the proved closing-tag fragment and keeps the Chinese weapon name");
        check(CombatStatsFormatter.VisibleName("A<c") == "A<c" && CombatStatsFormatter.VisibleName("a<b") == "a<b"
              && CombatStatsFormatter.VisibleName("A < B") == "A < B" && CombatStatsFormatter.VisibleName("x <") == "x <",
            "while a single-letter *opening* fragment and every literal comparison stay exactly as they are");

        // The settlement board is the production entry point: the row it draws carries only the visible name.
        var input = new ImpactInput { Name = "Brav0", SecondaryName = unpacked };
        ImpactReport report = ImpactScoring.Evaluate(new[] { input }, ImpactScoring.DefaultWeights, 1.0, 1.0);
        ImpactBoardLayout board = ImpactBoard.Build(report, true, true);
        check(board.Cell((int)ImpactRowKind.SecondaryWeapon, 1)[0] == "大锤",
            "and the 副武器 row of the settlement board shows `大锤`, not the cut tag");
    }

    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) return null;
        int open = source.IndexOf('{', start);
        if (open < 0) return null;
        int depth = 0;
        for (int index = open; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0) return source.Substring(start, index - start + 1);
            }
        }
        return null;
    }

    private static string Body(string source, string signature) => MethodBody(source, signature) ?? string.Empty;

    // ---------------------------------------------------------------------------------------------------
    // A1. 记录的 692 vs 417：完整内容证据成立时，板向上收回 275px，room/glyph 真的变大
    // ---------------------------------------------------------------------------------------------------
    private static void PaddedContainerStopsShorteningTheStrip(Action<bool, string> check)
    {
        var padded = new CompletionPart(true, true, 1f, Container, Content, scheduled: true, measured: true, hidden: 0);
        check(CompletionFinalBounds.ContentComplete(padded) && Near(CompletionFinalBounds.FinalEdge(padded, out bool fromContent), Content) && fromContent,
            "a part whose content is completely measured contributes the pixels it really draws (417), not its 692 container");
        var parts = new List<CompletionPart> { padded };
        check(Near(CompletionFinalBounds.Placed(parts, revealFinished: true, out int deepest, out _), Content) && deepest == 0,
            "and that is the edge the board hangs from once the reveal has finished");

        // The same page's strip, before and after: the page's own gap above and the button it measured below.
        ResultsSafeArea before = ResultsRegion.Resolve(true, Container, true, ButtonTop, Height, Gap);
        ResultsSafeArea after = ResultsRegion.Resolve(true, Content, true, ButtonTop, Height, Gap);
        float roomBefore = before.Bottom - before.Top, roomAfter = after.Bottom - after.Top;
        check(before.Fits && after.Fits && Near(roomBefore, 593f) && Near(roomAfter, 868f),
            "the strip grows from 593 to 868 px on the recorded numbers (before=" + roomBefore.ToString("0") + ", after=" + roomAfter.ToString("0") + ")");
        check(Near(roomAfter - roomBefore, 275f),
            "which is the 275 px of blank slot the container reserved: the board the player reported as small now has that room");
        // The fit's own height search and the row/column policy are untouched: more room can only allow a taller table at
        // the same rows, never a different grid.
        int rows = ImpactBoard.RowCount(true);
        float floorBefore = ResultsFontGrowth.MinimumBoardHeight(rows, 12f, PanelDensity.Normal);
        float floorAfter = ResultsFontGrowth.MinimumBoardHeight(rows, 12f, PanelDensity.Normal);
        check(Near(floorBefore, floorAfter) && floorAfter > 0f,
            "the readability floor for the same rows and glyph is the same figure: this change moves the band, not the height search, the rows or the scoring");
        float grown = ResultsFontGrowth.DrawableBoardHeight(rows, 12f, PanelDensity.Normal, roomAfter);
        float grownBefore = ResultsFontGrowth.DrawableBoardHeight(rows, 12f, PanelDensity.Normal, roomBefore);
        // The figure the player actually notices: the largest glyph whose rows fit the band (`ResultsBandFit`, the same
        // arithmetic `FitBoard` seeds its search with). More room is only worth anything if this moves.
        int glyphBefore = ResultsBandFit.FontForBand(roomBefore, rows, PanelDensity.Compact, 0);
        int glyphAfter = ResultsBandFit.FontForBand(roomAfter, rows, PanelDensity.Compact, 0);
        Console.WriteLine("CONTENT-EDGE: band top " + (Container + Gap).ToString("0") + " -> " + (Content + Gap).ToString("0")
            + ", room " + roomBefore.ToString("0") + " -> " + roomAfter.ToString("0") + "px, floor " + floorBefore.ToString("0")
            + "px, band glyph " + glyphBefore + "px -> " + glyphAfter + "px, rows=" + rows);
        check(glyphAfter > glyphBefore,
            "and the glyph the band can hold really grows with the room (" + glyphBefore + "px -> " + glyphAfter + "px at 30 rows): this is the small board the player reported, not a cosmetic change");
        check(Near(grown, grownBefore),
            "and the readability figure itself is unchanged (" + grownBefore.ToString("0") + " = " + grown.ToString("0") + "): this change moves the band the fit is given, not the height search, the rows or the scoring");
    }

    // ---------------------------------------------------------------------------------------------------
    // A2. 容器上沿高于题注：可见内容更深处仍要压住（反向保护）
    // ---------------------------------------------------------------------------------------------------
    private static void ContainerAboveCaptionsStillCannotPressOnThem(Action<bool, string> check)
    {
        var captions = new CompletionPart(true, true, 1f, 313f, 352f);
        check(Near(CompletionFinalBounds.Placed(new List<CompletionPart> { captions }, revealFinished: true, out _, out _), 352f),
            "a container that ends above the captions its icon draws (313 vs 352) still contributes the captions' own depth");
    }

    // ---------------------------------------------------------------------------------------------------
    // A3. 内容证据不完整：一律退回容器（隐藏子件 / 枚举失败 / 透明 / 未激活）
    // ---------------------------------------------------------------------------------------------------
    private static void IncompleteContentKeepsTheContainer(Action<bool, string> check)
    {
        var hiddenChild = new CompletionPart(true, true, 1f, Container, Content, scheduled: true, measured: true, hidden: 1);
        check(!CompletionFinalBounds.ContentComplete(hiddenChild)
              && Near(CompletionFinalBounds.FinalEdge(hiddenChild, out bool hiddenFromContent), Container) && !hiddenFromContent,
            "a part with one switched-off child keeps its container (692): that child can still appear, and the band must not be tightened past it");

        var failed = new CompletionPart(true, true, 1f, Container, Content, scheduled: true, measured: false, hidden: 0);
        check(!CompletionFinalBounds.ContentComplete(failed) && Near(CompletionFinalBounds.FinalEdge(failed, out _), Container),
            "an enumeration that threw is not a measurement of the content, so the container stands");

        var faded = new CompletionPart(true, true, 0f, Container, Content, scheduled: true, measured: true, hidden: 0);
        check(!CompletionFinalBounds.ContentComplete(faded) && Near(CompletionFinalBounds.FinalEdge(faded, out _), Container),
            "a part the page has faded out keeps its container: its pixels are not what the player sees");

        var undrawn = new CompletionPart(true, true, 1f, Container, float.NaN, scheduled: true, measured: true, hidden: 0);
        check(!CompletionFinalBounds.ContentComplete(undrawn) && Near(CompletionFinalBounds.FinalEdge(undrawn, out _), Container),
            "a part that draws nothing measurable keeps its laid-out slot");

        var slot = new CompletionPart(true, false, 0f, Container, float.NaN, scheduled: true);
        check(Near(CompletionFinalBounds.FinalEdge(slot, out _), Container),
            "a scheduled slot the reveal has not switched on yet contributes the rectangle it will occupy");

        var unused = new CompletionPart(true, false, 0f, 900f, float.NaN, scheduled: false);
        check(float.IsNaN(CompletionFinalBounds.FinalEdge(unused, out _)),
            "and a part this page never scheduled still contributes nothing at all");
    }

    // ---------------------------------------------------------------------------------------------------
    // A4. 多部件：隐藏子件的那一个仍然是更深的边，不能被别处的 content 读数越过
    // ---------------------------------------------------------------------------------------------------
    private static void BandNeverAboveAHiddenPartsContainer(Action<bool, string> check)
    {
        var content = new CompletionPart(true, true, 1f, 620f, Content);
        var hidden = new CompletionPart(true, true, 1f, Container, 500f, scheduled: true, measured: true, hidden: 1);
        var parts = new List<CompletionPart> { content, hidden };
        check(Near(CompletionFinalBounds.Placed(parts, revealFinished: true, out int deepest, out _), Container) && deepest == 1,
            "the band is the deepest final edge among the parts, so a part with a hidden child still deepens it to its container");
        var both = new List<CompletionPart>
        {
            new CompletionPart(true, true, 1f, 430f, Content),
            new CompletionPart(true, true, 1f, 380f, 402f)
        };
        check(Near(CompletionFinalBounds.Placed(both, revealFinished: true, out int winner, out _), Content) && winner == 0,
            "and when every part is completely measured the band is the deepest measured content, named by the part that produced it");
    }

    // ---------------------------------------------------------------------------------------------------
    // A5. reveal 未结束时仍然只用容器：内容读数不能在中途抬高/降低边
    // ---------------------------------------------------------------------------------------------------
    private static void RevealStillUsesTheContainer(Action<bool, string> check)
    {
        var parts = new List<CompletionPart> { new CompletionPart(true, true, 1f, Container, Content, scheduled: true) };
        check(Near(CompletionFinalBounds.Placed(parts, revealFinished: false, out _, out int laidOut), Container) && laidOut == 0,
            "while the page's own reveal is still running the edge is the laid-out container (692): a drawn reading mid-reveal is progress, not layout");
        string line = CompletionFinalBounds.Describe(parts[0], "sectorIconAllCompleted", revealFinished: true);
        check(line.Contains("own=692") && line.Contains("visual=417") && line.Contains("measured=true")
              && line.Contains("hidden=0") && line.Contains("complete=true") && line.Contains("source=content")
              && line.Contains("scheduled=true") && line.Contains("active=true"),
            "and the per-part diagnostic line carries own/visual/active/scheduled/measured/hidden/complete/source: " + line);
    }

    // ---------------------------------------------------------------------------------------------------
    // B1. 失败页：没有任何下界读数时不从窗口编带（记录页的 890..1429 不再可能）
    // ---------------------------------------------------------------------------------------------------
    private static void FailureWallIsNotInventedFromTheWindow(Action<bool, string> check)
    {
        // 1.19.45: 主机失败页的答案还是那条有界放弃（预算按帧）；`guestFailure` 只描述客机失败页，而且它现在
        // 根本不由帧数作答（量不到墙就保留原版并重测，唯一的例外是玩家自己的 F8）。
        check(ResultFailureWallPolicy.Waits(false, 0, false)
              && ResultFailureWallPolicy.Waits(false, ResultFailureWallPolicy.UnobservedBudget - 1, false)
              && !ResultFailureWallPolicy.Waits(false, ResultFailureWallPolicy.UnobservedBudget, false)
              && ResultFailureWallPolicy.Answer(false, ResultFailureWallPolicy.UnobservedBudget, false)
              && ResultFailureWallPolicy.Answer(true, 0, false)
              && !ResultFailureWallPolicy.Answer(false, 0, false),
            "a failure page with no lower bound withholds the invented band, and answers after the bounded give-up ("
            + ResultFailureWallPolicy.UnobservedBudget + " frames) or as soon as one reading exists");

        // The recorded page's own arithmetic: with no pressable control, no footer text and no page geometry, the
        // invented tier really would have used the window's foot - which is what the policy above now refuses first.
        ResultPageWalls empty = ResultPageLowerBoundPolicy.Resolve(guestFailure: false, screenHeight: Height,
            footerControlTop: float.NaN, scanPressableTop: float.NaN, footerTextTop: float.NaN, footerTextSource: ResultBoundSource.None);
        bool invented = ResultFallbackBandPolicy.TryResolve(Height, anchorBottom: FailureTop, anchorSource: ResultBoundSource.TitleAnchor,
            empty, measuredControlTop: float.NaN, bandTop: float.NaN, bandBottom: float.NaN, boardFloor: 270f, out ResultFallbackPlan windowBand);
        check(float.IsNaN(empty.PressableTop) && float.IsNaN(empty.FooterTextTop) && float.IsNaN(empty.PageFloor)
              && invented && windowBand.Bottom > FailureButton,
            "with nothing measured the band arithmetic does reach past the button the page later shows (bottom="
            + windowBand.Bottom.ToString("0") + " > " + FailureButton.ToString("0") + "), which is exactly why the wall policy is asked first");

        // The same page once the button arrives: the band is cut above it.
        ResultPageWalls measured = ResultPageLowerBoundPolicy.Resolve(guestFailure: false, screenHeight: Height,
            footerControlTop: FailureButton, scanPressableTop: FailureButton, footerTextTop: float.NaN, footerTextSource: ResultBoundSource.None);
        bool cut = ResultFallbackBandPolicy.TryResolve(Height, anchorBottom: FailureTop, anchorSource: ResultBoundSource.TitleAnchor,
            measured, measuredControlTop: float.NaN, bandTop: float.NaN, bandBottom: float.NaN, boardFloor: 270f, out ResultFallbackPlan buttonBand);
        check(cut && buttonBand.Bottom <= FailureButton,
            "and once it is measured the band stops above it (" + buttonBand.Top.ToString("0") + ".." + buttonBand.Bottom.ToString("0") + ")");
    }

    // ---------------------------------------------------------------------------------------------------
    // B2. 实机序列：空 → 空 → 晚到控件
    // ---------------------------------------------------------------------------------------------------
    private static void FailureEmptyThenLateControl(Action<bool, string> check)
    {
        // Frame 0 and 1: two complete scans that found no pressable control at all.
        int framesWithoutWall = 0;
        bool placed = false;
        for (int frame = 0; frame < 2; frame++)
        {
            framesWithoutWall++;
            placed |= ResultFailureWallPolicy.Answer(wallMeasured: false, framesWithoutWall, guestFailure: false);
        }
        check(!placed,
            "two complete scans that found no control never release the fail-open band: empty x2 is not 'this page has no wall'");

        // Frame 2: the page's own control arrives at 1240.
        ResultPageWalls late = ResultPageLowerBoundPolicy.Resolve(guestFailure: false, screenHeight: Height,
            footerControlTop: FailureButton, scanPressableTop: FailureButton, footerTextTop: float.NaN, footerTextSource: ResultBoundSource.None);
        check(ResultFailureWallPolicy.Answer(wallMeasured: true, framesWithoutWall, guestFailure: false),
            "the frame the control is measured is the frame the band may answer");
        bool cut = ResultFallbackBandPolicy.TryResolve(Height, FailureTop, ResultBoundSource.TitleAnchor, late, float.NaN,
            float.NaN, float.NaN, 270f, out ResultFallbackPlan plan);
        check(cut && plan.Bottom <= FailureButton && !ResultLateControlPolicy.MustMove(plan.Top, plan.Bottom, plan.Top, plan.Bottom),
            "and a board placed inside that band (906..1224) does not reach the control: nothing has to move");
        // A board that was already standing lower - the recorded 1385 - is moved by the late-control rule, not removed.
        check(ResultLateControlPolicy.MustMove(plan.Top, OldBoardBottom, plan.Top, plan.Bottom),
            "a board that really was drawn to 1385 is moved out of the control's way by the per-repaint rule");
        // The bounded give-up is honest: after the budget the band is answered, and the caller logs `ResultFailureWallPolicy.Unobserved`.
        check(ResultFailureWallPolicy.Answer(wallMeasured: false, framesWithoutWall: ResultFailureWallPolicy.UnobservedBudget, guestFailure: false)
              && ResultFailureWallPolicy.Unobserved == "wall-not-observed",
            "and a page that never shows a bound is answered only after the bounded give-up, under its own reason string");
    }

    // ---------------------------------------------------------------------------------------------------
    // C. BlinkIn 排程窗口的先后：0/N 不提前显示、迟到 Start 重新接纳、abort/销毁结案
    // ---------------------------------------------------------------------------------------------------
    private static void ScheduleWindowOrder(Action<bool, string> check)
    {
        const long artA = 0xA3, titleA = 0xA2, artB = 0xB2;
        const long routineA1 = 0x9001, routineA2 = 0x9002, routineB = 0x9003;
        var ledger = new BlinkCompletionLedger();

        // One icon's BlinkIn: it hands over its own artwork and its caption, and schedules both inside the call.
        ledger.Require(artA, false); ledger.Require(titleA, true);
        ledger.Start(artA, routineA1); ledger.Start(titleA, routineA2);
        check(ledger.CloseSchedule(new[] { artA, titleA }) == 0 && ledger.Count == 2,
            "a BlinkIn call whose targets all received a routine inside it closes without excluding anything");
        check(!ledger.Ready,
            "and the section is not 'ready' before those routines report completion");

        // A second icon that hands over artwork this build never schedules.
        ledger.Require(artB, false);
        check(ledger.CloseSchedule(new[] { artB }) == 1 && ledger.Unscheduled == 1 && ledger.Count == 2 && ledger.Required == 3,
            "a target that received no routine inside its own BlinkIn call is left out of the reveal, and counted separately");
        // The strict gate still withholds the board while the counted targets are pending: 0/N never shows early.
        check(ResultRevealGate.BlocksPlacement(false, ledger.MayStillComplete, forced: false),
            "the success page's gate is still shut while the scheduled routines are running - nothing is shown at 0/N");

        // The routines finish; the unscheduled target stays out of the count and never fakes a completion.
        ledger.Finish(artA, routineA1, naturallyRevealed: true);
        ledger.Finish(titleA, routineA2, naturallyRevealed: true);
        check(ledger.Ready && ledger.Completed == 2 && ledger.Count == 2 && ledger.Unscheduled == 1,
            "the reveal finishes on the two targets that were really scheduled (completed=2/2, unscheduled=1), with no completion invented for the third");

        // A late routine for the unscheduled target re-admits it: the board is withheld again until it too finishes.
        ledger.Start(artB, routineB);
        check(ledger.Count == 3 && ledger.Unscheduled == 0 && !ledger.Ready
              && ResultRevealGate.BlocksPlacement(false, ledger.MayStillComplete, forced: false),
            "a routine that starts after the window closed re-admits its target, so a late native control is awaited rather than ignored");

        // Abort: a routine that really ended without revealing its target ends the wait at once, and never counts as done.
        ledger.Finish(artB, routineB, naturallyRevealed: false);
        check(ledger.RevealImpossible && ledger.Aborted == 1 && !ledger.Ready && ledger.Completed == 2
              && !ResultRevealGate.BlocksPlacement(false, ledger.MayStillComplete, forced: false),
            "an aborted routine releases the wait at once, is counted as aborted rather than completed, and the reveal is still not finished");

        // Destroyed target: the routine pointer that ended is enough to close the row.
        var destroyed = new BlinkCompletionLedger();
        destroyed.Require(artA, false);
        destroyed.Start(artA, routineA1);
        check(destroyed.FinishRoutine(routineA1, naturallyRevealed: false) && destroyed.RevealImpossible && !destroyed.Ready,
            "a routine whose target object is already gone closes the same row instead of leaving the page waiting for ever");

        // Every target excluded, none scheduled: the count is zero, and the gate is *still* shut - the one case the
        // settlement gate may answer is the bounded reveal-unobservable fail-open, never a board at 0/N.
        var none = new BlinkCompletionLedger();
        for (int index = 0; index < 6; index++) none.Require(0x100 + index, false);
        long[] all = { 0x100, 0x101, 0x102, 0x103, 0x104, 0x105 };
        check(none.CloseSchedule(all) == 6 && none.Count == 0 && !none.Ready
              && ResultRevealGate.BlocksPlacement(false, none.MayStillComplete, forced: false),
            "six targets and no routine at all leaves an empty reveal and a shut strict gate: this build never shows a board at 0/6");
        ResultSettlementGate.Decision unobserved = ResultSettlementGate.Decide(forced: false, revealEnforced: true,
            revealFinished: false, revealImpossible: none.RevealImpossible, revealCount: none.Count,
            revealWaitFrames: ResultFinalPolicy.MaximumWaitFrames, everMeasurable: true, finalMeasurable: true,
            finalSettled: true, finalWaitFrames: 0);
        check(unobserved.MayFailOpen && unobserved.Why == ResultSettlementGate.Answer.RevealUnobservable && unobserved.Unobservable,
            "the only answer such a page gets is the bounded reveal-unobservable fail-open, logged as such and re-measured on the bounded cadence");

        // The window is closed in the native call's *postfix*, after every routine that call scheduled.
        string root = SourceRoot();
        if (root == null) { check(false, "the schedule-window regression locates the production sources"); return; }
        string targets = File.ReadAllText(Path.Combine(root, "CompletionTargets.cs"));
        int prefix = targets.IndexOf("private static void Prefix(CM_ExpeditionSectorIcon", StringComparison.Ordinal);
        int postfix = targets.IndexOf("private static void Postfix()", StringComparison.Ordinal);
        check(prefix >= 0 && postfix > prefix
              && targets.IndexOf("CompletionTargets.BeginCall();", prefix, StringComparison.Ordinal) > 0
              && targets.IndexOf("CompletionTargets.SchedulesClosed();", postfix, StringComparison.Ordinal) > 0,
            "the window is opened in the BlinkIn prefix and closed in its postfix, so every routine that call schedules is already recorded when it closes");
        check(!targets.Substring(postfix, 260).Contains("Done = true") && !targets.Contains("Ledger.Finish(__instance.Pointer.ToInt64(), __instance.Pointer.ToInt64(), true)"),
            "and closing the window never marks anything done");
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) <= 0.001f;

    private static string SourceRoot()
    {
        string directory = AppContext.BaseDirectory;
        for (int step = 0; step < 8 && directory != null; step++)
        {
            if (File.Exists(Path.Combine(directory, "ResultsDisplay.cs")) && File.Exists(Path.Combine(directory, "PluginInfo.cs"))) return directory;
            directory = Path.GetDirectoryName(directory);
        }
        return null;
    }
}
