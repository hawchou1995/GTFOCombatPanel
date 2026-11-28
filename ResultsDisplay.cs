using System;
using System.Collections.Generic;
using System.Linq;
using CellMenu;
using HarmonyLib;
using Player;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

using SNetwork;
namespace HawChow.GTFOCombatPanel;

// The success and failure screens. The influence board is drawn by IMGUI in window pixels on top of
// the page: nothing is written into a vanilla text object and nothing inside a report card is
// repositioned, so the two can never fight, and because the board is in screen space it keeps every
// column on screen instead of being clipped by whatever page happens to be up.
//
// The vanilla blocks the board replaces are switched off in place, one at a time and each in its own
// guard, and only when the config says so: a field that is null on this game build is skipped and
// the board simply draws over it.
internal static class ResultsDisplay
{
    internal static CM_PageExpeditionSuccess Success;
    internal static CM_PageExpeditionFail Fail;

    private static TableContent _content;
    private static PanelGrid _grid;
    private static LayoutRect _origin;
    // Where the board stood the last time it was really drawn, which page kind it was drawn for, and the frame of
    // the wheel sequence that last matched it. The wheel is judged against this rather than against `_grid`: the
    // notch that changes the scale also saves the configuration, that save drops the layout cache so the board can
    // be rebuilt at the new size, and the rest of the same gesture still has to land on the board it was aimed at.
    // It is dropped the moment the page changes or the board is explicitly hidden, so nothing keeps answering for a
    // board that is no longer up.
    private static LayoutRect _hitRect;
    private static bool _hitSucceeded;
    private static bool _hitValid;
    // 1.19.59: whether the wheel may move the table this repaint drew. It is written by the same line that writes
    // `_hitRect` and is deliberately *wider* than `_hitValid`: an early board is a picture and takes no press (its own
    // button stays the page's), but the table under it still has to be readable to its last row. The recorded success page
    // never matured, so `_hitValid` was false for the whole page and the wheel could not reach rows 27..30 of a board whose
    // own log line said "rows shown 30 of 30" was the aim.
    private static bool _scrollValid;
    private static int _hitFrame = -1;

    // The glyph floor the grid on screen was fitted with, so a refresh that has to re-measure it asks for
    // the same floor the placement used instead of quietly falling back to the absolute minimum.
    private static int _fontFloor = ResultsFontGrowth.MinimumReadablePixels;
    // The page the board is standing in for, kept so the vanilla blocks it replaces can be put away
    // again on every frame: the result pages animate their content in and switch it back on as they
    // go, which is what makes a one-off SetActive(false) blink back in.
    private static Component _page;
    private static bool _succeeded;
    private static bool _loggedError, _loggedDraw, _loggedHidden, _loggedMeasure;
    private static readonly HashSet<string> HeadingReports = new HashSet<string>();

    private readonly struct FooterControls
    {
        public readonly float FooterTop, BoardTop;
        // `Count` is the number of interactions the scan found on the page this frame: everything the game left
        // switched on, whether or not its rectangle could be read. `Hidden` is the number the game switched off, of
        // which `HiddenPageActions` are this page's own result actions (the client case, where the game hides the
        // lobby button and another mod may clone a replacement). `Unmeasurable` is the number of active controls
        // whose rectangle was the zero box this frame, counted separately from `Hidden` because the two need
        // different answers: a hidden control cannot take the pointer, an unmeasurable one very well can.
        // `Count` is the only count the two decisions that matter read - `ProvenEmpty` and `ResultNoFooterGate` -
        // so it is filled from the scan's own tally and never from the number of rectangles its strip could place
        // (`strip.FooterActionCount`, which describes the strip alone). `ScannedCount` is everything the scan
        // visited, switched on or off, so a log line can tell "nothing there" from "something there that cannot
        // take input".
        public readonly int Count, ScannedCount, Hidden, HiddenPageActions, Unmeasurable;
        public readonly bool Complete, ParentCoverageComplete, OverlapsBoard;
        // Why a scan that is not `Complete` could not finish, in the few words a live log can afford: a coverage
        // edge (page registry, tree, named action parent, client state) was unavailable, so the scan cannot claim
        // it saw the page. A control the page switched off is *not* one of these - it cannot take the pointer, so
        // it neither forms a lower wall nor makes the scan incomplete - and neither is an active control whose
        // rectangle could not be read this frame: that one is counted in `Unmeasurable` and takes part in the
        // collision set as soon as its geometry is readable.
        public readonly string Reason;
        // Nothing the player can touch was found, on a scan that could see everything it had to see. Hidden
        // controls do not stand in the way of this answer; a control that comes back on is caught by the
        // per-repaint rescan (`BoardStillSafe`) instead. An unmeasurable control does stand in the way, because
        // it is a real interaction the scan could not place, which is why the count - not the measured set -
        // decides this answer. The invariant behind it is the whole point of this struct: while any active
        // control is on the page, measurable or not, nothing may claim the page has no footer - not this answer,
        // not `ResultNoFooterGate`, and not the full-height guest safe area either of them releases.
        public readonly ResultInteractionTally Tally;
        // The free band under every interaction this scan measured, in window pixels, or a band of no kind when the
        // page leaves none. It is measured here because this is the one place that holds the rectangles the scan
        // read: a page whose strip is shorter than the board's own floor can then be answered with the space its own
        // controls leave free, instead of with the button's upper edge pretending to be the end of the page.
        public readonly ResultBoardBand BelowActions;
        // The highest edge among the controls this frame can really be pressed (see `ResultInteractionStrip.PressableTop`):
        // the client failure page's strip is tightened against this figure, because its waiting-status line is neither a
        // control nor a wall. On every other page it is the same number `FooterTop` carries unless the text sits higher.
        public readonly float PressableTop;
        // 1.19.51：扫描量到、但横向站在板子自己那一栏之外的控件个数（见 `LayoutColumn`）。它们既没有把条带起点
        // 往下推，也没有成为条带的下界：板子永远画不到它们身上，而真正和板相交的控件由每帧的 `OverlapsBoard`
        // 继续拦（拒绝落板），那条保护一个有字的都没动。栏宽读不出来时这个数是 0，也就是这一条根本不参与。
        public readonly int BeyondColumn;
        // 1.19.51：这一段条带的下界是「谁」给出来的（类型、对象名、它自己的矩形）。记录的 1080p 成功页上这一格
        // 完全不存在：日志只说条带是 190 px，没有任何一行说 455 上站着什么，于是根因只能靠截图去猜。
        public readonly string Wall;
        // 固定模式专用：第一个和固定板真实相交的控件（类型、对象名、它自己的矩形），以及相交的控件个数。
        // 只有 `inspectBoard` 的调用会填它；自适应那条链读都不读，所以这里多存两个值不改变它的任何决定。
        public readonly ResultFixedBlocker Blocker;
        public readonly int Blockers;
        public bool ProvenEmpty => ResultInteractionScanPolicy.ProvenEmpty(Complete, ParentCoverageComplete, Tally);
        public FooterControls(float footerTop, float boardTop, int count, int scannedCount, bool complete,
            bool parentCoverageComplete, bool overlapsBoard, int hidden = 0, int hiddenPageActions = 0, string reason = "",
            int unmeasurable = 0, ResultBoardBand belowActions = default, float pressableTop = float.NaN,
            ResultFixedBlocker blocker = default, int blockers = 0, int beyondColumn = 0, string wall = "")
        {
            FooterTop = footerTop; BoardTop = boardTop; Count = count; ScannedCount = scannedCount;
            Complete = complete; ParentCoverageComplete = parentCoverageComplete; OverlapsBoard = overlapsBoard;
            Hidden = hidden; HiddenPageActions = hiddenPageActions; Reason = reason ?? string.Empty;
            Unmeasurable = unmeasurable;
            BeyondColumn = beyondColumn;
            Wall = wall ?? string.Empty;
            Tally = new ResultInteractionTally(count, hidden, hiddenPageActions, unmeasurable);
            BelowActions = belowActions;
            PressableTop = pressableTop;
            Blocker = blocker; Blockers = blockers;
        }
    }

    private static readonly ResultNoFooterGate SuccessNoFooter = new ResultNoFooterGate();
    private static readonly ResultNoFooterGate FailureNoFooter = new ResultNoFooterGate();
    // 客机失败页的下界墙稳定门：一页一份。`ForgetPage` 是唯一的重置点，所以换页、离开结算页都会从零开始
    // 观察新页面的墙，而这一页上一次的读数（`Wall` 是这一页见过的最高一堵）绝不会跟到下一张页面上去。
    private static readonly ResultClientWallGate ClientWall = new ResultClientWallGate();

    // 客机失败页页脚槽位的一帧读数（`ClientSlot`）：同一帧里落位、逐帧校验、兜底与二次测量都会问它，所以每帧
    // 只量一次。评审 #5：这既去掉了 1.19.61 第一次尝试里的整树扫描，也去掉了同一帧的重复测量。
    private static int _slotFrame = -1;
    private static IntPtr _slotPage;
    private static ResultClientSlotEvidence _slot;

    // The vanilla blocks this mod has touched, with the exact position, active state and drawing state each had before it
    // touched them. Restoration is the inverse of taking, one block at a time: the moment a flag is switched off, the page
    // changes or the results screen is left, every block goes back to exactly where it was and exactly the state it was
    // in.
    //
    // 1.19.59: an entry has two independent halves and either may be empty:
    //   * the *move* - `localPosition` off the page plus `SetActive(false)` - for a block the classifier proved carries no
    //     control of the player's (`WholeBlock`), or for a branch of one that carries none (`SafeBranches`);
    //   * the *drawing* - the block's own `CanvasGroup` at zero alpha and its own renderers switched off - for everything
    //     else, including the early board's own frame and any block that really carries a `Button`. Nothing is moved,
    //     deactivated or unregistered by that half, so a press the page hung inside the block still answers; only the
    //     pixels go away.
    //
    // 1.19.59 (independent review): the record itself is a Unity-free ledger (`ResultVanillaHold`), so the two questions
    // this file used to answer with one object are answered separately and can be driven headlessly:
    //   * H1 - each half is taken at its own first *write*, never at the first touch of any kind, and the ledger's
    //     `HoldMove` is idempotent, so a block whose drawing was switched off first still records the position the page
    //     drew it at when it is finally moved. The old record answered the move of such a block with `Position =
    //     default`, i.e. F8 put it back at (0,0,0) for the rest of the run.
    //   * M3 - nothing is put back that was not taken: a drawing-only record has no active state at all (the old one
    //     hardcoded `Active = true` and switched on blocks the page had left switched off), and this mod never adds a
    //     `CanvasGroup` to the game's object - only a group the block already has is used, and only its alpha.
    //   * H2 - no raycast flag is recorded or written: `interactable`/`blocksRaycasts` are absent from the ledger, and the
    //     drawing half owns alpha and renderer enablement alone.
    private static readonly ResultVanillaHoldLedger Hidden = new ResultVanillaHoldLedger();
    // The last repaint that really drew a board and put the page's own blocks away. A repaint that has to defer the board
    // (`DeferBoardForInteraction`) does *not* give the blocks back: the recorded pages restored them on the deferred frame
    // and hid them again on the next one, i.e. the prisoner cards blinked at the poll cadence. They come back on real
    // evidence - a placement the page refused twice in a row (`NoteVanillaRefusal`), a page that went away, F8 off, a page
    // change, leaving the screens - and, only as a last resort for a page that gives no evidence at all, after
    // `VanillaHideGrace` seconds in which nothing was drawn.
    private static float _vanillaHiddenAt = float.NegativeInfinity;
    private const float VanillaHideGrace = 3f;
    // Consecutive refused placements this hide has outlived, and the frame the last one was counted on. A refusal is
    // evidence about the page ("this board cannot be placed here"), not a frame budget: two of them while nothing is drawn
    // is the point at which the game's own blocks are given back.
    private static int _vanillaRefusals, _vanillaRefusalFrame = -1;
    private static bool _appliedSuccessBlocks, _appliedPrisonerReports, _appliedFailBlocks, _appliedAny;
    // Clearance between the failure screen's own message and the board that stands under it.
    private const float MessageGap = 16f;
    // 客机固定几何下允许尝试落板的最小可用条带高度。装不下就明确不落板（原版页面保留），
    // 而不是把板子压成一条看不见的缝；真正的字号适配仍由 FitBoard 决定。
    private const float MinimumGuestStripHeight = 96f;
    // Tolerance for the page's own elements drifting a pixel or two between the measurement and the draw,
    // well inside the 16 px gap the strip leaves above the buttons.
    internal const float BoardSlack = 2f;

    internal static void Refresh(eGameStateName state)
    {
        bool succeeded = state == eGameStateName.ExpeditionSuccess;
        bool results = succeeded || state == eGameStateName.ExpeditionFail;
        // Leaving the results screens puts every vanilla block this mod moved back exactly where it was and forgets
        // everything that belonged to the page: the next run's page must never inherit this run's hiding, its
        // placement or its readiness. This branch comes before the overlay gate on purpose - a board that had put the
        // page's own blocks away must not leave them away in the lobby just because the switches are still on, and
        // F8's `Forced` is a result-page override only: it is not read outside one.
        if (!results)
        {
            RestoreHidden();
            ResetPlacement();
            ForgetPage();
            return;
        }
        bool overlay = ResultVisibility.Shows(
            Plugin.ShowResults == null || Plugin.ShowResults.Value,
            Plugin.ShowResultOverlay == null || Plugin.ShowResultOverlay.Value,
            Plugin.ShowImpactBoard == null || Plugin.ShowImpactBoard.Value,
            PanelBehaviour.ToggleState)
            // F8 on a result page is the fail-open override and outranks every visibility switch: a player who asked
            // for the board must get it on the next repaint even if a config file, a hot reload or a stale toggle says
            // otherwise. In a lobby or in a level `Forced` is not read by anything.
            || Forced;
        if (!overlay)
        {
            // The archive deliberately does not share the visibility gate: the switches and F8 may suppress
            // *drawing* the board, never recording the settlement. A page that exists archives its screen even
            // with the board hidden; the same (page, run) latch keeps one record per expedition per page.
            ArchiveScreen(succeeded);
            // Nothing is measured for a board-less entry any more: 1.19.45 removed the second storey that used to
            // stand the toggle on this page's own title edge while no board was placed. A page with no board has the
            // key and nothing else, which is the contract: F8 is the shortcut and it is answered by the same unified
            // show/hide the toggle uses.
            if (_page != null) RestoreHidden();
            ResetPlacement();
            ClearHit();
            // Turning the board off and on again is a fresh attempt: the settle state is kept (a page that has already
            // been measured stays measured) but the retry cadence starts over, so the board comes back on the next
            // frame rather than up to half a second later.
            Retry.Forget();
            return;
        }
        // The placed board keeps its position and size while current cells fit its measured grid.
        // Polls refresh the text and archive; geometry is remeasured only when updated data outgrows it, or when
        // `FallbackRecoveryDue` says the strip this board was measured into no longer describes this page: a
        // placement that had to fall back before one of the page's own edges was readable, or a page that has
        VanillaHideExpired();
        if (_resolutionWidth != 0 && _succeeded == succeeded && PageActive(_page) && !FixedLayoutActive())
        {
            PrepareResolutionBoard(_page, succeeded);
            return;
        }
        // moved since (another mod's footer, a resolution change, a section that finished drawing later). Both
        // are budgeted per page, so a settled board is never rebuilt for a frame of the page's own animation.
        if (_placed && !_earlyBoard && _placedSucceeded == succeeded && _grid != null && _content != null &&
            _page != null && _page.gameObject.activeInHierarchy)
        {
            // 1.19.56 (review 6/12): the early board must not short-circuit this poll for ever - that is what left a page
            // stuck on a non-interactive picture with no way to mature. While an early board stands, the re-measure runs
            // on the retry cadence and the early board keeps drawing meanwhile (`_holdValid` is deliberately not cleared
            // by `ResetPlacement`), so the promotion is measured, bounded and non-destructive.
            if (_fixedPlaced && FixedLayoutActive() && RefreshBoardData(_page, succeeded)) return;
        }
        // A page whose own anchors have not arrived yet is measured every frame while its reveal runs, then at a
        // bounded cadence: measuring a page walks its renderers and every button it drew, and a page that never hands
        // an anchor over would pay for that on every frame for the rest of the expedition while the vanilla page is on
        // the screen anyway. A skipped frame draws nothing and hides nothing - the caches are already empty, so the
        // game's own page stays exactly as it drew it.
        if (!Retry.Due(Time.frameCount)) return;

        // 1.19.57 (independent review of 1.19.56 D): a standing early board is *kept*, not thrown away to re-measure the same
        // page. This path used to answer every retry with `ResetPlacement()` + `RestoreHidden()` before the page was read
        // again, which cleared the grid, its place and the plate the wheel and the draw read: a page that never hands over the
        // facts the mature board needs lost and regained the table on the retry cadence, and any frame whose re-measure
        // refused left the player looking at the game's own page again. The measurement still runs at the same cadence - that
        // is what promotes it - and the mature board replaces the early one in the single fit that really places it.
        bool keepStanding = EarlyBoardStanding(succeeded, out IntPtr standingPage);
        Component previous = _page;
        if (!keepStanding)
        {
            ResetPlacement();
            if (previous != null) RestoreHidden();
        }
        Component page = null;
        if (succeeded && Success != null && Success.gameObject.activeInHierarchy) page = Success;
        else if (!succeeded && Fail != null && Fail.gameObject.activeInHierarchy) page = Fail;
        if (page == null)
        {
            // The result state is up but neither page object is live: the screen has already been taken down, or the
            // page that carries it has not been raised yet. Nothing can be measured or drawn, and saying so once per
            // page is the difference between "this mod is idle" and "this mod is broken".
            ReportHeldBack("refresh", "no-live-page", "state=" + (succeeded ? "success" : "failure")
                + ",successPage=" + (Success != null) + ",failurePage=" + (Fail != null)
                // 1.19.38: the same two facts the draw line carries. This branch is only reached when no page
                // object was live, so it says both out loud instead of leaving a reader to infer it from the reason.
                + ",pagePresent=False,pageActive=False");
            return;
        }
        // 1.19.57: a board that entered early may only be kept on the page it was measured for. A later settlement reuses
        // the same page object, and the socket that says so is the pointer the placement was recorded under.
        if (keepStanding && page.Pointer != standingPage)
        {
            keepStanding = false;
            ResetPlacement();
            if (previous != null) RestoreHidden();
        }
        // A different page is a different screen: the placement, the strip it was measured in and the
        // readiness of the previous one are dropped here and nowhere else. A configuration change, the F8
        // write it triggers and the hot reload that follows it all leave them standing.
        //
        // 1.19.55: "a different page" is the page this mod last *attached* to (`_attachedPage`), not the page a board was
        // placed on (`_holdPage`, written only by a placement that really happened). Asking the second question meant that
        // a page whose board was still being refused - which is every page until its own boundary is proven - was treated as a
        // new page on every repaint: `ForgetPage` dropped the reveal latch, the settle gates, the retry cadence, the strict-miss
        // budget and the per-page line budgets, the page was measured from nothing again, and the fail-open ladder could never
        // be spent (`strictRefusals=1 of 60` on every recorded page). That is the loop the recorded log calls 895 `released`
        // lines, 2,873 `completion parts` lines and a board that only ever appeared after F8.
        if (_attachedPage != page.Pointer)
        {
            _attachedPage = page.Pointer;
            // A new settlement starts with the board up. This undo is for this mod's own hide only - the press on
            // this page's toggle, its close, or the key wrote the two switches off - so the next expedition is not
            // asked to live with a decision made about the last one. A switch the player turned off in the
            // configuration file is not what this restores, and it is never written over by anything else here.
            bool startOfSettlement = page.Pointer != _holdPage;
            bool owed = startOfSettlement && Session.OweRestore(page.Pointer);
            ForgetPage();
            // `ForgetPage` drops the attachment with the page, so it is restored for the page this repaint is attaching to.
            _attachedPage = page.Pointer;
            _earlyBoard = false;
            if (owed)
            {
                Plugin.PersistToggle(true);
                Forced = false;
                if (HeadingReports.Add("settlement-shown-again"))
                {
                    Plugin.Logger?.LogInfo("[DIAG] a new settlement page starts with the board up: the previous screen's hide was this mod's own "
                        + "(a press on the settlement toggle, its close or the toggle key), so the two switches it wrote off are given back here and "
                        + "nothing the player set in the configuration file is touched.");
                }
            }
        }
        _page = page; _succeeded = succeeded;
        Readiness.Bind(page.Pointer);
        if (keepStanding && ResultReportPolicyStrict.OncePerPage(HeadingReports, "early-board-kept"))
            Plugin.Logger?.LogInfo("[DIAG] the early board is kept while this page has not handed over the facts the mature board"
                + " needs: the grid, its place and the scroll window measured for this same page are left exactly as they are, and"
                + " nothing is reset or put back. The page is still measured on this retry's cadence and the mature board replaces"
                + " this one in the single fit that really places it (1.19.57).");
        try
        {
            // 1.19.59: the page's own blocks are given back only when this mod has really stopped standing in for them - not
            // on the deferred frame of a board that is about to come back. A repaint is the right place for the question: it
            // runs once per attached page, and the answer needs no new observation.
            VanillaHideExpired();
            if (FixedLayoutActive()) Prepare(page, succeeded);
            else PrepareResolutionBoard(page, succeeded);
            // 原版块只在这一帧**真的画出表格之后**才让位（`Draw` 里，`HudTable.Draw` 之后同一帧）。这里不再预隐藏：
            // 预隐藏是 review #3 找到的那条旁路——`_placed` 为真、`Prepare` 也量到了板，但 `Draw` 在 1793 行之前
            // 还有七道门（可见性、无页面、无 placement、固定模式、`BoardStillSafe`、band 不变式、settle），任何一道
            // 退出去就把页面留在"原版块被挪走、板没画"的空洞里。固定模式本来就只听 `_fixedPlan`，它自己的绘制路径
            // 同样在画完之后才让位（`FixedLayoutDraw`）。
        }
        catch (Exception error)
        {
            if (!_loggedError) { _loggedError = true; Plugin.Logger?.LogError("Results display failed: " + error); }
        }
    }

    // A settlement page is archived whether or not the board is drawn - the visibility switches and F8 only
    // suppress drawing. This is the hidden half of that contract: the same projection a drawn board saves (the
    // split board over the report the settlement view really holds) and the same one-record-per-(page, run)
    // latch underneath, so a repaint storm cannot write duplicates, and a screen that later becomes visible
    // refreshes the same file in place. It is called on every frame the screen exists; the latch is the budget.
    private static void ArchiveScreen(bool succeeded)
    {
        try
        {
            Component page = null;
            if (succeeded && Success != null && Success.gameObject.activeInHierarchy) page = Success;
            else if (!succeeded && Fail != null && Fail.gameObject.activeInHierarchy) page = Fail;
            if (page == null) return;
            ImpactReport report = BuildReport(succeeded);
            if (report.Entries.Count == 0)
            {
                // The same reason the visible path names as `empty-roster`, said once here too: a hidden result
                // page with no retained counters has nothing to project, so "a page that exists archives its
                // screen" is honestly answered - the empty report is still not written, only the reason is named.
                if (ResultReportPolicy.OncePerPage(HeadingReports, "archive-empty-roster", Forced, _forcedOutcomePending))
                    Plugin.Logger?.LogWarning("[DIAG] settlement archive held back: no retained player counters for this hidden screen, so nothing is recorded.");
                return;
            }
            ImpactBoardLayout board = ImpactBoard.Build(report, true, succeeded);
            ResultsSaver.Save(page, succeeded, board, report, CombatSync.Generation, HostProven());
        }
        catch (Exception error)
        {
            if (!_loggedArchiveError) { _loggedArchiveError = true; Plugin.Logger?.LogError("[DIAG] the settlement archive could not be written for a hidden result page: " + error); }
        }
    }
    private static bool _loggedArchiveError;

    // The caches the settlement board holds: the measured grid, its position, and the page it belongs to.
    // Cleared whenever the page changes, the overlay is switched off, or a configuration change asks for a
    // remeasurement. An ordinary counter update never reaches here, so a board that has settled keeps the
    // size and position it was measured with while the figures under it change. Deliberately does not touch
    // the per-page hold below: a re-placement attempt for the same page is exactly what has to keep it.
    private static void ResetPlacement()
    {
        // 1.19.56 (review 7/8/9): what this cache is being replaced *from* is captured before anything is cleared. The
        // page and the row count are what tell a re-measure of the same page apart from a new one - without them the
        // scroll owner could never match (the page was already null), every re-measure looked like a new page and the
        // player's scroll position was dropped with it.
        Component previousPage = _page;
        int previousRows = _grid != null ? _grid.Rows : 0;
        _resolutionWidth = _resolutionHeight = 0;
        _resolutionRetryAt = 0f;
        _grid = null; _content = null; _page = null; _succeeded = false; _placed = false; _placedSucceeded = false;
        // 1.19.52: the measured table width and the probe budget belong to the page as well - a new page must not
        // inherit the previous table's column, and its own two-phase measurement starts from its own first frame.
        // 1.19.55: `_widthProbe` and `_columnProbes` are *not* reset here any more. This method runs on every repaint, and
        // wiping the measured table width here is what made `Fit`'s two-phase column measurement impossible to complete: phase
        // one recorded the width and refused the frame, and the next frame's reset had already thrown it away, so the first
        // placement of every success page was refused for ever (the "board before column, column before board" loop). They
        // are dropped with the page instead (`ForgetPage`), which is the only thing that can invalidate them.
        _drawnFloorCompletion = float.NaN;
        // 固定模式的落位也是这个缓存的一部分：换了页面、改了配置或关了开关，下一帧重新按玩家的数字判定。
        _fixedPlaced = false;
        _fixedPlan = ResultFixedDrawPlan.None;
        // The scroll position belongs to the page, not to the build: a new page starts at its own head, and the
        // gesture lock is released with it so the first notch of the new page is never mistaken for a repeat. A
        // re-measure of the *same* page keeps the offset the player scrolled to - a poll, a refresh or a resolution
        // change must not snap the table back to its head - and it is clamped to the viewport that measurement
        // produced, so a smaller plate cannot leave the offset past the table's foot.
        // 1.19.56: the same page keeps the player's scroll position, and a different page starts at its head. The page
        // and the row count were captured before this cache was cleared (`previousPage`/`previousRows`), which is the
        // only way `sameScrollPage` could ever be true - it compared against `_page` after `_page` had been set to null.
        bool sameScrollPage = _scrollOwner != null && ReferenceEquals(_scrollOwner, previousPage);
        _scrollFirst = ResultsScroll.OffsetAfterReset(_scrollFirst, sameScrollPage, previousRows,
            ResultsScroll.FrozenRows, Math.Max(1, _scrollVisible - ResultsScroll.FrozenRows));
        if (!sameScrollPage) { _scrollVisible = 0; _scrollPlate = 0f; }
        _scrollOwner = previousPage;
        _scrollLatch.Forget();
    }

    // Whether a board that entered early against this same page is still standing, and the page it was measured for.
    //
    // This is the reading of the page's own objects; the rule itself is `ResultEarlyBoardPolicy.KeepsStanding`, so a test
    // can assert the same answer the game asks for. `_holdPage` is the page the last placement was recorded under, which is
    // the only pointer that can tell a re-measure of this page from the next settlement reusing the same page object.
    private static bool EarlyBoardStanding(bool succeeded, out IntPtr pagePointer)
    {
        pagePointer = _holdPage;
        bool live = false;
        try { live = _page != null && _page.gameObject.activeInHierarchy; } catch { live = false; }
        IntPtr current = _page != null ? _page.Pointer : IntPtr.Zero;
        return ResultEarlyBoardPolicy.KeepsStanding(_earlyBoard, _placed,
            ResultVisibility.SamePage(_holdPage, current), _placedSucceeded == succeeded, live,
            _grid != null, _content != null);
    }

    // The rectangle the wheel is judged against, dropped when the board it describes is no longer on the page: a
    // different result screen, the results screens being left, or the overlay being switched off. Deliberately not
    // part of `ResetPlacement`: the rebuild a wheel notch itself causes goes through there, and the gesture that
    // caused it must still find the board it was aimed at.
    private static void ClearHit()
    {
        _hitRect = default; _hitValid = false; _scrollValid = false; _hitSucceeded = false; _hitFrame = -1;
    }

    // The rectangle is re-earned on every repaint: it describes a board that is on the screen now, not one that was on
    // the screen at some earlier frame. A frame in which the board could not be drawn at all - no grid yet, a
    // placement the strip refused - leaves nothing for the wheel to aim at, so the stale rectangle is dropped before
    // any of `Draw`'s early returns can skip past it.
    //
    // The gesture lock is deliberately *not* dropped: `_hitFrame` is the frame a notch of the wheel already matched
    // the board in, and the rebuild that notch started is exactly what invalidates the layout a moment later. A
    // later notch of that same frame must still be consumed, so only the rectangle and its validity are cleared
    // here - the full reset, frame included, stays with `ClearHit` for F8 and for leaving or replacing the page.
    private static void InvalidateHitForRepaint()
    {
        _hitRect = default;
        _hitValid = false;
        _scrollValid = false;
        _hitSucceeded = false;
    }

    // ---- the settlement table's scroll position ---------------------------------------------------------
    //
    // The board is a table of a fixed row count drawn into a plate only as tall as the room its page offers.
    // `_scrollFirst` is the first scrollable row on screen, `_scrollVisible` how many rows the plate holds and
    // `_scrollPlate` the plate's height in pixels; all three are decided by the fit from the strip this frame
    // really measured, so the wheel can only ever move a table that is on the page. The frozen row (the column
    // header carrying the four player names) is never part of the offset: it is drawn from the table's own head
    // on every frame.
    //
    // `_scrollLatch` is the same-frame guard. IMGUI runs a layout pass and then a repaint, so one notch can be
    // delivered twice in one Unity frame; the first copy moves the table, the second is consumed here without
    // moving it again - which is what keeps one notch from costing two rows.
    private static int _scrollFirst = ResultsScroll.FrozenRows;
    private static int _scrollVisible;
    private static float _scrollPlate;
    private static readonly ResultScrollLatch _scrollLatch = new ResultScrollLatch();
    // 1.19.59: the wheel's own two pieces - the reference's router (native first, physical fallback, consumed by change,
    // 3 rows per notch, at most 5 notches per frame) and the whole-row step that carries its sub-row remainder.
    private static readonly ResultWheelRouter _wheel = new ResultWheelRouter();
    private static readonly ResultWheelSteps _wheelSteps = new ResultWheelSteps();
    // The page whose scroll position `_scrollFirst` belongs to. A re-measure of *this* page keeps the offset; a page
    // that replaced it (or the results screens being left, which clears this in `ForgetPage`) starts at its head.
    private static Component _scrollOwner;

    // Everything the board caches for one result page: the place it was measured into, the strip that place
    // came from and the readiness its completion section has already earned. None of it is dropped by
    // `ResetPlacement`, and that is the whole point of it: F8 writes the configuration file, that write comes
    // back through `Plugin.ApplyConfig` as a second invalidation a moment later, and the board must neither
    // wait again for a readiness it has already been given nor lose the strip it was measured against and be
    // refused by what is left of it. It is dropped when the page changes, when a different kind of result
    // screen comes up and when the results screens are left - and by nothing else.
    private static IntPtr _holdPage;
    private static bool _holdValid;
    private static LayoutRect _holdBoard;
    private static float _holdOpacity;
    // 1.19.55: the page this mod last *attached* to, which is not the page a board was placed on. `_holdPage` is written only
    // by a placement that really happened, and `Refresh` used it to answer "is this a new page?" - so every repaint of a page
    // whose board was still being refused looked like a new page, ran `ForgetPage()` (dropping the reveal latch, the settle
    // gates, the retry cadence, the strict-miss budget and the per-page line budgets) and measured the page again from
    // nothing. That is the loop behind `895 released` / `2873 completion parts` / `strictRefusals=1 of 60` and behind the board
    // that only ever appeared after F8: the fail-open budget could never be spent because it was reset every frame. This
    // field is the honest identity for that question, and it is cleared with the page itself (`ForgetPage`).
    private static IntPtr _attachedPage;
    // 1.19.55: the board standing now entered before the page had proven its own boundary (`ResultEarlyBoardPolicy`): it is
    // drawn, it is not a control surface, and it puts none of the game's own blocks away. The mature placement clears it.
    private static bool _earlyBoard;
    // The page animates its footer in. A button measured mid-animation sits higher than the one the player ends up
    // pressing, and the board built against that edge is permanently too short - `TryHold` never enlarges a board, by
    // design, so a first measurement taken too early is never corrected. `Footer` follows that edge across frames and
    // answers "settled" only for an edge it really read at the same value twice. 1.19.21: the settle latch is
    // diagnostics for the drawn-pixel route - a readable laid-out edge (button or title rectangle) satisfies the
    // placement on its own first frame - and a page that hands over no geometry at all keeps the vanilla page until
    // the bounded fail-open path answers. The failure page's own title is followed by `Title` (full opacity *and* a
    // still edge) and the success page's completion section by `Completion`, for the log's drawn-pixel route.
    private static readonly ResultEdgeGate Footer = new ResultEdgeGate();
    private static readonly ResultTitleGate Title = new ResultTitleGate();
    // Used only for an inactive mission-title layout whose native failure coroutine has completed.
    private static readonly ResultEdgeGate FailureLayoutTitle = new ResultEdgeGate();
    private static readonly FailureSequenceReadiness FailureSequence = new FailureSequenceReadiness();
    private static readonly ResultEdgeGate Completion = new ResultEdgeGate();
    // A page whose own anchors have not arrived is measured every frame while its reveal runs, then at a bounded
    // cadence: waiting for an anchor must not cost a full walk of the page's renderers and buttons on every frame for
    // the rest of the expedition, and it must not buy an approximate board either.
    private static readonly ResultRetryCadence Retry = new ResultRetryCadence();
    // The board standing now was laid out against a guess - the lobby button handed it no geometry - and the one second
    // look that case is allowed: a page whose own edges become readable a moment later is re-measured once, not every
    // frame. See `ResultAnchorRecovery`.
    private static bool _placedOnFallback;
    private static int _fallbackRecoveries;
    // The strip the board standing now was really measured into, and the comparison that notices the page has
    // moved away from it. Both live as long as the page does: a re-measure is a bounded answer to a real move,
    // never a per-frame rebuild, and it is what makes an F8 toggle unnecessary.
    private static BoardStrip _holdStrip;
    private static readonly ResultAnchorDrift Drift = new ResultAnchorDrift();
    // 1.19.44：一次真的重测（第二眼 / 页面移动）重新落板之后，把新矩形记一次。`_loggedDraw` 与
    // `failure board placed` 都是每页一次，所以 run 2 那次 L3928 的重落位在日志里没有任何尺寸可查
    // （独立报告 §7.2）。这里按页一次，且只由 `ResultAnchorRecovery`（1 次）和 `ResultAnchorDrift`（2 次）
    // 这两个有界触发器置位，所以一页最多一行，不刷屏。
    private static bool _relayoutPending;
    private static string _relayoutReason = string.Empty;
    // ---- 1.19.52: the board's own column, measured before it is drawn -------------------------------------------
    //
    // F2: the band's lower wall may only be set by an interaction the board could really cover, which needs the board's
    // *width* - and on the first placement of a page no board has been fitted yet, so nothing on the page has measured
    // it. 1.19.51 fell back to the page's footer control (not the board) and then, when that was unreadable, to
    // `LayoutColumn.None` - the old eight-row band. The fit therefore records the width it really measured for this
    // page's content (`_widthProbe`) and refuses to commit that first frame at all: the vanilla page stays up for one
    // repaint, the next frame's band is measured against the table's own column, and nothing is ever drawn in between
    // and re-aligned under the player (F4). The budget is bounded per page, so a table whose width keeps changing can
    // never loop here.
    private static float _widthProbe = float.NaN;
    private static int _columnProbes;
    private const int MaximumColumnProbes = 2;
    // F1: the deepest pixel the completion section has really drawn on this frame (`CompletionFinalBounds.DrawnFloor`).
    // The strip may hang no higher than this; the per-repaint safety check reads it to tell a board that stands above
    // content the page has painted since it was placed.
    private static float _drawnFloorCompletion = float.NaN;
    // ---- the page's final geometry ---------------------------------------------------------------------------
    //
    // The strip a settlement board is measured into has to be the one the page *ends at*, not the one it is passing
    // through: a recorded success page activated its completion section over several frames (the native
    // `SetActiveBlink` reveal), the edge the strip hangs from walked 250 -> 272 -> 314 -> 366, and the board - placed
    // in the meantime from the fail-open path - was re-fitted twice and finally withdrawn. `FinalLayout` is the latch
    // that decides "this strip has read the same twice, so a board may be fitted to it once"; `ResultFinalPolicy`
    // decides when the fail-open path may answer at all. See `ResultFinalLayout.cs`.
    private static readonly ResultFinalGate FinalLayout = new ResultFinalGate();
    // Whether this frame's page handed over the geometry its strip is built from at all, how many of the completion
    // section's parts are laid out but not drawn yet, and the first frame on which that geometry was readable without
    // having settled - the bounded give-up in `ResultFinalPolicy` is measured from it.
    private static bool _finalMeasurable;
    // Whether this page has *ever* handed over that geometry. One blind frame is not evidence of a page with no
    // geometry: a page that measured its strip a moment ago and drops one reading (a canvas being rebuilt, a frame in
    // which a control could not be read) must not be answered with the fail-open band, because the board that lands
    // there is laid out against the screen rather than the page and is re-fitted the moment the page answers again -
    // which is the size flicker this work exists to remove. The latch only ever turns on for this page, so a page that
    // really never measures anything (a client page with no footer to read, a failure page whose own strips cannot be
    // resolved) keeps the immediate fail-open answer it has always had.
    private static bool _finalEverMeasurable;
    private static int _finalUndrawn;
    private static int _finalFirstFrame = -1;
    // 1.19.42（独立评审 F1）：这一页已经等了多久「一个连目标都没观察到的 reveal」。只有当完成账本一个目标都没被交给
    // 过（`CompletionTargets.Ledger.Count == 0`，这次访问游戏根本没排 BlinkIn）时它才走，用尽
    // `ResultFinalPolicy.MaximumWaitFrames` 之后 fail-open 并把这一页记成 reveal-unobservable、而不是 settled。
    // 被观察到的 reveal 永远不由帧数放行——那正是这一轮要消灭的「先画再变」。
    // 1.19.43 F1-STALL: 无进展停滞改用真实秒（`RevealProgressClock`），任何新的原生事实都会重置它。
    private static readonly RevealProgressClock RevealClock = new RevealProgressClock();
    // 1.19.60（独立评审 F1）：这一页「看了但账本还是 0 个目标」的次数。它只喂 `ResultRevealStatePolicy` 的
    // `Unobservable` 边界（有界、带名字、且仍要求几何闩），每页重置；一个稍后才登记目标的页面在登记的那一帧就离开
    // 这个状态，因为只有 `Required == 0` 的观察才计数。
    private static int _revealObservations;
    // The per-part completion reading is printed as one line per part, at most `CompletionPartReportLines` times per
    // page, and only when the reading it would describe really changed: a live log has to say which part produced the
    // band and from which reading, without printing anything per frame.
    internal const int CompletionPartReportLines = 3;
    private static string _completionPartSignature = string.Empty;
    private static int _completionPartLines;
    // The frame a failure page was first seen with no lower bound at all (`ResultFailureWallPolicy`); -1 while it has
    // one. It belongs to the page, resets with it, and is only read to decide whether the bounded give-up has been
    // reached - never to turn "not measured" into "measured".
    private static int _failureWallFirstFrame = -1;
    // 1.19.21: whether a laid-out part counts as part of the section's final edge is decided by the part's own
    // state - present and laid out - and never by how far the native reveal has drawn. The switch the previous
    // builds kept here (`_finalIncludeLaidOut`) is gone with it: a part the reveal has not reached still knows
    // where it will sit, and a part a finished page does not use is switched off and contributes nothing.
    private static readonly CompletionReadiness Readiness = new CompletionReadiness();
    // Whether this page's native reveal has finished, read where a caller only needs the *answer* rather than the
    // feeding of the latch: the completion section's own edge and the icon row the band hangs from
    // (`CompletionFinalBounds.Placed`), and the fail-open band. `CompletionReady` is what feeds it, once per frame, at
    // the top of the success placement - so the section is measured as final on the very frame the ledger completes,
    // and the two never disagree about which reading the board was placed from.
    private static bool RevealFinished { get { return Readiness.Ready; } }
    // 1.19.60 (independent review F1): the host's early-display privilege needs a host that was really read. `Role()`
    // answers `Unreadable` when the native flag throws, which is deliberately the host's *measuring* path - it measures
    // and refuses instead of handing out a client's fixed safe area. A page whose role nobody could read may not inherit
    // the privilege on top of that, so the one place that grants it asks `HostProven()` (defined beside `Role()`, from the
    // same single read) instead of `!GuestClient()`.
    // The height the table wants, and the height the page is judged against. Kept apart on purpose, because they
    // answer different questions and conflating them is what made a 269 px strip draw nothing while a 286 px strip
    // drew a 6 px table.
    //
    // `_readableFloor` is 30 rows at the smallest *readable* glyph (270 px at compact): what the fit aims at, and what
    // a page has to clear to get a table the player can read.
    //
    // `_boardFloor` is the height this table can actually be drawn at in the room the page measured
    // (`ResultsFontGrowth.DrawableBoardHeight`). It starts at the readable floor and is refreshed from the strip each
    // frame measures, so a page that offers less than the readable floor is still held to a figure about the room it
    // really gave instead of a preference it can never meet. Both are set for every page visit in `Prepare` and
    // dropped with the page.
    private static float _readableFloor = float.NaN;
    private static float _boardFloor = float.NaN;
    // ---- fail-open: a result page that is on the screen always shows its board --------------------------------
    //
    // Everything above is a proof about the page, and a page that never hands one over used to keep the vanilla
    // screen and nothing else - for the whole page, with F8 unable to help, because F8 only cleared the placement
    // and the same gates refused it again a frame later. A real host failure page did exactly that: the game's own
    // page was fully drawn (`rawTextAlpha=1.00`) while this mod read `missionTitleActive=false, geometryMeasured=false`
    // and held the board back for the life of the screen.
    //
    // `_strictMisses` counts the measurements on which the strict placement was refused. Once the bounded budget in
    // `ResultFallbackPolicy` is spent the board is placed from the screen's own safe area (`ResultFallbackLayout`),
    // labelled as such in the log, and re-checked against the page's controls on every repaint. `Forced` is the F8
    // override: it skips the budget and the three visibility switches, and it never waits for a gate.
    private static int _strictMisses;
    private static bool _fallbackPlaced;
    private static float _fallbackOpacity = 1f;
    // How many frames in a row the *fail-open* path itself could not put a board on the page, and how far its own floor has
    // been lowered in answer. A fail-open fit that keeps refusing used to be a silent, permanent empty screen: the refusal
    // count had already saturated, so every frame re-decided "use the fail-open band" and every frame failed the same way.
    // The count gives that path a cadence of its own, and the scale walks the floor down to the smallest grid this build can
    // draw, which is the one thing that turns "no band can hold the board" into "a tiny band holds a tiny board".
    private static int _fallbackMisses;
    private static float _fallbackFloorScale = 1f;
    // ---- 固定模式（1.19.45，[Results] FixedLayout）-----------------------------------------------------------
    //
    // 玩家自己指定字号与「表格底边中点」时的落板：位置和字号都来自窗口与玩家自己的数字（`ResultFixedLayout`），
    // 不来自页面自适应条带的任何一条边。它因此不会被客机失败页的下界墙等待卡住，输入不可信或跑出屏幕时明确
    // 不画板（**不**隐藏原版块、不改玩家指定的任何数字）；真实相交与扫描证据不足不撤板，按
    // `ResultFixedOverlayPolicy` 淡画并把证据写进那行诊断。
    private static bool _fixedPlaced;
    // 这一帧固定板的可见性决定（相交对象、证明状态、不透明度、理由）：`Draw` 用它决定淡淡和原版块让不让位。
    private static ResultFixedDrawPlan _fixedPlan;
    // The override the F8 *show* press turns on: a board the player asked for may not be held back by any gate,
    // switch or stale toggle. It says nothing about whether a board is on the page - it defaults to false on a page
    // whose fail-open board is already drawn - so it is never what F8's next answer is read from.
    internal static bool Forced;
    // The frame of the last repaint that really painted the board, or -1 when that repaint painted nothing. Every
    // early return in `Draw` writes the -1 at the top, so this is the display's own answer to "is the board on the
    // page right now", and it is what `ToggleForcedFallback` asks before it decides. 1.19.26: the press used to read
    // `Forced` instead, which is false for a board the fail-open path had already put up, and the press that should
    // have hidden the board therefore turned the override on and drew the same board again.
    private static int _boardDrawnFrame = -1;
    // The 1.19.22 forced-frame contract: an F8 press that turns the override on must end its frame with a line - a
    // "board drawn" line (ReportForcedDraw) or a refusal line (the press-pending exception in ReportHeldBack and the
    // other forced-gated reports). The flag is set on the press and spent by whichever of the two the frame ends
    // with, so a press can never read as silence in the log. 1.19.25: refusal sites spend it too, which makes the
    // exception one line per press - a held F8 whose page stays refused prints nothing more until the next press
    // re-arms the flag, never one line per repaint.
    private static bool _forcedOutcomePending;
    // ---- the agreed band: an icon row above, the footer this machine can see below ------------------------------
    //
    // The last icon-row bottom this page measured. A page that is still revealing its sector box hands no icon row for
    // a frame or two, and the band must not fall back to a fraction of the window for that: the last trusted edge is
    // the edge the page really has, and it is dropped with the page (`ForgetPage`). See `ResultsBounds`.
    private static float _iconRowBottom = float.NaN;

    private static void ForgetPage()
    {
        _holdPage = IntPtr.Zero;
        _holdValid = false;
        _holdBoard = default;
        _holdOpacity = 0f;
        // 1.19.41 review #4: the scroll offset belongs to the page it was made on, so leaving or replacing the page
        // drops the owner as well as the offset - the next page starts at its own head.
        _scrollOwner = null;
        // 1.19.59: the wheel's consumed native fingerprint and its sub-row remainder belong to the page the pointer was
        // over, so a new screen never inherits them (the reference's `Reset`, not its `Suspend`). Leaving the results
        // screens and F8 off go through here as well, which is what makes a hidden board hand the page its own wheel back.
        ResetWheel();
        // A different screen has a different footer, title and completion section: every settle state, the retry
        // cadence and the recorded strip are dropped here and nowhere else.
        Footer.Forget(); Title.Forget(); FailureLayoutTitle.Forget(); Completion.Forget();
        FailureSequence.Forget(); Retry.Forget();
        SuccessNoFooter.Forget(); FailureNoFooter.Forget();
        // 客机的下界墙属于这一张页面：它的样本、它读到过的最高一堵墙都留在这一页上。
        ClientWall.Forget();
        // 1.19.61：槽位读数与墙一样属于这一张页面（同一帧只量一次的那份缓存也在其中）。
        _slotFrame = -1; _slotPage = IntPtr.Zero; _slot = default;
        _holdStrip = default;
        Drift.Forget();
        _placedOnFallback = false; _fallbackRecoveries = 0; _boardFloor = float.NaN;
        // The page's final geometry is a property of the page: the latch, whether the last frame could read it, how many
        // of the section's parts were still waiting to be drawn and the frame the wait started on all go with it.
        FinalLayout.Forget(); _finalMeasurable = false; _finalEverMeasurable = false; _finalUndrawn = 0; _finalFirstFrame = -1;
        // 1.19.60 (review F1): how often this page was watched with an empty ledger is the page's own count too, so a new
        // settlement starts from `NotStarted` instead of inheriting the previous screen's observation budget.
        _revealObservations = 0;
        // The reveal's stall clock belongs to the page too: the next result screen starts from zero progress.
        RevealClock.Forget();
        // The per-part completion diagnostic is a statement about this page's own parts: its signature and its line
        // budget go with the page, so the next screen prints its own reading. 1.19.55: the reset itself has to stay -
        // without it the budget of the previous page held the next page's reading back.
        _completionPartSignature = string.Empty; _completionPartLines = 0;
        // 1.19.55: the measured table width and its probe budget are the page's own too. `ResetPlacement` runs on every
        // repaint (a wheel notch, a hide/show) and used to wipe them there, so the two-phase column measurement (`Fit`:
        // "record the width, refuse this frame, resolve the band against it next frame") could never reach its second
        // phase: the width was gone by the time the next frame asked, the fit refused again, and the *first* placement of
        // every success page was impossible - that is the "the board must be drawn before the column can be known, the
        // column must be known before the board may be drawn" loop. They are dropped here instead, where the page itself
        // is dropped.
        _widthProbe = float.NaN; _columnProbes = 0;
        // The page this mod last attached to, and whether the board standing on it entered early: both are statements
        // about this page and go with it.
        _attachedPage = IntPtr.Zero;
        _earlyBoard = false;
        // 1.19.55: the pointer-space observation belongs to the page it described, so the next screen prints its own.
        _spaceReported = string.Empty;
        // The failure page's own wall clock goes with the page as well: the next result screen waits through its own.
        _failureWallFirstFrame = -1;
        // A different screen is a different fail-open decision: the refusal count and the fallback placement belong to
        // the page they were measured on. `Forced` deliberately survives it - it is the player's answer, given with F8,
        // and it lasts until the player takes it back.
        _strictMisses = 0; _fallbackPlaced = false; _fallbackOpacity = 1f;
        // The fail-open path's own ladder belongs to the page as well: a new screen starts at the measured floor again.
        _fallbackMisses = 0; _fallbackFloorScale = 1f;
        // The icon row this page last measured goes with the page: the next result screen has its own.
        _iconRowBottom = float.NaN;
        // A different screen has a different board, and the wheel is only ever aimed at the one on the page now.
        ClearHit();
        Readiness.Forget();
        _iconRowBottom = float.NaN;
        // session. A page pointer is reused across a session's runs, so a once-per-session latch left every
        // result screen after the first without its own line and its own archive entry in the log.
        _loggedDraw = false;
        // 1.19.44：重落位那一行也是这一页的：下一个结果屏有自己的第一块板。
        _relayoutPending = false; _relayoutReason = string.Empty;
        // 固定模式的落位与这一页一起结束。
        _fixedPlaced = false;
        _fixedPlan = ResultFixedDrawPlan.None;
        // The page's controls belong to the page: their rectangles, the presses in flight and the frame that drew
        // them are dropped with it, so a click on the next screen can never be answered by the last one's box.
        ForgetChrome();
    }

    // The success page's "sectors cleared" row: its own label and the four icon slots, and *nothing else*. The white
    // box behind them is a plate, not the row - it draws far below the icons, and taking it as the row's bottom is
    // exactly what pushed a real client board some 400 px down the page with a band of empty page above it.
    //
    // The row is measured as its final edge, not as the pixels it has drawn so far: the native reveal switches these
    // slots on one after another, so the drawn bottom of this row walks down the page while the page is coming up, and a
    // band anchored to it moved with it (the recorded success page's icon row read 250 and then 292). A slot that is on
    // the page and laid out but not drawn yet still knows where it will sit, so the band hangs from where the row ends.
    // 1.19.41: `revealFinished` carries the same answer the completion section was measured with. While the reveal runs,
    // the row's edge is its laid-out slots and nothing drawn (`CompletionFinalBounds.Bottom`); once it has finished the
    // row ends where the deeper of the slot and the pixels really drawn under it ends (`Placed`), which is the caption
    // row the panel's top used to press on. The two callers pass the same latch, so the section's edge and the band
    // cannot disagree about which reveal state they describe.
    private static float IconRowBottom(Component page, bool revealFinished, bool contentSettled)
    {
        try
        {
            var success = page != null ? page.TryCast<CM_PageExpeditionSuccess>() : null;
            if (success == null) return float.NaN;
            var parts = new List<CompletionPart>
            {
                Read(success.m_sectorHeader),
                Read(success.m_sectorIconMain),
                Read(success.m_sectorIconSecond),
                Read(success.m_sectorIconThird),
                Read(success.m_sectorIconAllCompleted)
            };
            // The row's edge is its own final extent, never the reveal's progress: while the reveal runs it is the
            // parts' laid-out slots and nothing drawn (the recording's band walked 250 -> 292 only because the drawn
            // edge was mixed in there), and once it has finished it is the deeper of the slot and the pixels really
            // drawn under it - the caption row (`CompletionFinalBounds.Placed`).
            return CompletionFinalBounds.Placed(parts, revealFinished, contentSettled, out _, out _);
        }
        catch { return float.NaN; }
    }

    // The per-part reading of the success page's completion section, as one log line.
    //
    // It is printed when the reading changes (a part switching on, the reveal finishing, the band moving) and at most
    // `CompletionPartReportLines` times per page, so a live log carries the evidence without a per-frame line: which
    // parts are present, switched on, scheduled, drawn, whether their content was completely enumerated, how many of
    // their children are switched off, and which reading the band was taken from (`source=content` vs `container`).
    // That is the line a future run needs to settle the 692-vs-417 question this build answers conservatively today.
    private static void ReportCompletionParts(List<CompletionPart> parts, float completion, int deepest, bool contentSettled)
    {
        if (parts == null || parts.Count == 0) return;
        string signature = (float.IsFinite(completion) ? completion.ToString("0") : "unmeasured")
            + "#" + deepest + "#" + RevealFinished + "#" + _finalUndrawn;
        if (signature == _completionPartSignature) return;
        _completionPartSignature = signature;
        if (_completionPartLines >= CompletionPartReportLines) return;
        _completionPartLines++;
        var text = new System.Text.StringBuilder(256);
        for (int index = 0; index < parts.Count; index++)
        {
            string name = index < CompletionNames.Length ? CompletionNames[index] : "part" + index;
            if (text.Length > 0) text.Append(" | ");
            text.Append(CompletionFinalBounds.Describe(parts[index], name, RevealFinished, contentSettled));
        }
        Plugin.Logger?.LogInfo("[DIAG] success completion parts (" + _completionPartLines + " of at most "
            + CompletionPartReportLines + " this page): " + text
            + " ; band=" + (float.IsFinite(completion) ? completion.ToString("0") : "unmeasured")
            + ", deepest=" + (deepest >= 0 && deepest < CompletionNames.Length ? CompletionNames[deepest] : "none")
            + ", laidOut=" + _finalUndrawn + ", revealFinished=" + RevealFinished.ToString().ToLowerInvariant()
            + "; source=content means every child of that part was enumerated, nothing under it is switched off and at "
            + "least one child really drew, so the band uses the pixels it draws instead of a container rectangle that "
            + "may reserve blank space.");
    }

    // The waiting-status block a client's success page draws where the host's button would be.
    //
    // The page has no field for it, so it is measured where it lives: inside the leave-expedition control's own
    // subtree, as the world-space TextMeshPro objects it is made of, projected through the menu camera by
    // `TryScreenRect` - the same measurement the sector icons get. The button's own label is in that subtree too and
    // sits lower, so the highest top this finds is the waiting line, which is the wall the board must stay above.
    // The source word a waiting block's reading carries: `FooterText` when the page really draws a TextMeshPro of its
    // own, `FooterTextControl` when only the leave-expedition control's laid-out rectangle could be read. The two are
    // kept apart in `[DIAG] band` because they mean different things on a real page: one is the waiting line the player
    // reads, the other is only where that line would be.
    private static float FooterTextTop(Component page, out ResultBoundSource source)
    {
        source = ResultBoundSource.None;
        try
        {
            var success = page != null ? page.TryCast<CM_PageExpeditionSuccess>() : null;
            if (success == null || success.m_btnLeaveExpedition == null) return float.NaN;
            float top = float.NaN;
            foreach (TextMeshPro label in success.m_btnLeaveExpedition.GetComponentsInChildren<TextMeshPro>(true))
            {
                if (label == null) continue;
                // The same rule the failure page's `m_returnToLobby_text` has always been measured by: the text is a wall
                // only while the page is drawing it. A switched-off or zero-alpha leftover inside this subtree would
                // otherwise lift the board above a line nobody can see and shorten the band for nothing.
                bool active;
                try { active = label.gameObject.activeInHierarchy; } catch { continue; }
                if (!ResultFooterTextPolicy.Counts(active, HeadingAlpha(label))) continue;
                if (!TryScreenRect(label, out LayoutRect rect)) continue;
                top = ResultsRegion.HighestTop(rect.Y, top);
            }
            if (float.IsFinite(top))
            {
                source = ResultBoundSource.FooterText;
                return top;
            }
            // A build whose waiting line is not a TextMeshPro of its own still lays the control's rectangle out where
            // that line stands, so the control's own top edge is the block's bound - and it says so.
            if (TryScreenRect(success.m_btnLeaveExpedition, out LayoutRect control))
            {
                source = ResultBoundSource.FooterTextControl;
                return control.Y;
            }
            return float.NaN;
        }
        catch { return float.NaN; }
    }
    // One line per page for the band the board is really fitted into: the two edges, which fact produced each, and
    // the room the fit was given. A board that starts too low and a board that starts too high look identical in a
    // screenshot, and this line is what tells them apart without a second run.
    private static void ReportBand(string where, ResultsBand band, float iconRow, float footerText, float floor)
    {
        if (!HeadingReports.Add("band-" + where)) return;
        Plugin.Logger?.LogInfo($"[DIAG] {where} band: top={(band.Fits ? band.Top.ToString("0") : "unmeasurable")} (source={ResultsBounds.Name(band.TopSource)}, iconRow={(float.IsFinite(iconRow) ? iconRow.ToString("0") : "unmeasurable")}), "
            + $"bottom={(band.Fits ? band.Bottom.ToString("0") : "unmeasurable")} (source={ResultsBounds.Name(band.BottomSource)}, waitingText={(float.IsFinite(footerText) ? footerText.ToString("0") : "unmeasurable")}), "
            + $"room={(band.Fits ? band.Height.ToString("0") : "unmeasurable")}px, clearance={ResultsBounds.Clearance(Screen.height):0.#}px, clamped={band.Clamped.ToString().ToLowerInvariant()}, "
            + $"boardFloor={floor:0}px, screen={Screen.width}x{Screen.height}, role={ResultPageRolePolicy.Name(Role())}; the board hangs from the top edge and is fitted into the room below it.");
    }

    // A configuration change (weights, thresholds, columns, the vanilla-block flags) may move the board or
    // the blocks it stands in for, so the caches are dropped and the next poll remeasures. The retry cadence is
    // dropped too: a change the player just made is measured on the next frame, not on the next cadence tick.
    // Called on the main thread from Plugin.ApplyConfig.
    // This is the *board's* cache and nothing else. `ChromePlaces` deliberately does not go with it: a
    // configuration reload (or the config write an F8 press makes) re-measures the board, and a re-measured board
    // is not a new page - the toggle stands beside the page's own control and nothing here follows the re-measured
    // header. The places are forgotten by `ForgetChrome` alone: leaving the results screens, a
    // genuinely different page/settlement, or a repaint that failed.
    internal static void Invalidate()
    {
        ResetPlacement(); Retry.Forget();
        ChromePlaces.Relayout();
    }

    // -------------------------------------------------------------------------------------------------
    // The settlement board's own controls.
    //
    // Two controls hang off what the page really draws rather than off a guess:
    //
    //   toggle  immediately left of the control the page puts at its foot - the host's own return
    //           button, or, on a client page, the waiting-status line that page actually draws (a client
    //           settlement page has no return button at all: the game leaves it permanently inactive).
    //           It is drawn whether the board is up or not, because it is the pointer that can give the
    //           board back after it has been hidden.
    //   close   in the board's own reserved header, top right. The header is taken out of the strip the
    //           board was measured into before the table is fitted (`ChromeHeader()`), so no figure is
    //           ever drawn under the button and the board never grows past its band to make room.
    //
    // It is drawn and hit from the same struct, it is simply absent when the page hands over nothing to
    // anchor it (no anchor means no control, never a rectangle over an imagined one), and the one action it
    // shares with F8 is `ToggleForcedFallback`: one visible-state answer, so the key and the control can
    // never disagree about what a press does. Nothing here suppresses input:
    // the controls are placed where the page's own controls are not, and the pointer is read - never
    // taken - from the game.
    private static readonly ResultSettlementSession Session = new ResultSettlementSession();
    private static readonly ResultChromeInputGate ToggleGate = new ResultChromeInputGate();
    // The one place, committed once per page, page flavour and window (`ResultChromeLayout.Step`), and the
    // observation that decides whether the thing the toggle stands beside has stopped moving. The rectangle
    // below is *this repaint's* answer - drawn and hit only when the step put it on the screen - so a repaint,
    // a key or a paused control can never leave a click box behind while the place itself survives.
    private static readonly ResultChromeCommit ChromePlaces = new ResultChromeCommit();
    private static readonly ResultChromeSettle ChromeAnchorSettle = new ResultChromeSettle();
    private static readonly ResultChromeProbe ChromeFrame = new ResultChromeProbe();
    private static readonly List<LayoutRect> ChromeNative = new List<LayoutRect>(8);
    // This repaint's rectangle. `_chromeToggleRect` is only ever filled by a repaint that really draws the control;
    // the *place* lives in `ChromePlaces`, which is what survives the frame.
    private static LayoutRect _chromeToggleRect, _chromeBoardRect;
    private static bool _chromeToggleValid;
    private static int _chromeDrawnFrame = -1;
    private static int _chromeScreenWidth, _chromeScreenHeight;
    // What this repaint booked and `FinishChromeDraw` paints: the band only when the table really drew, the toggle on
    // every path. `_chromeToggleHides` is the word the toggle carried this repaint and the action its press takes - a
    // button never does the opposite of its own label (review #1).
    //
    // 1.19.43: the toggle is booked here and made clickable by the paint, never by the booking. `_chromeToggleValid` was
    // written from the decision, i.e. one step before `DrawChromeControl`, so a repaint that decided on the control and
    // then failed - or returned - before painting it left a hot rectangle with no button under it.
    //
    // 1.19.45: the close and the two fallback seats this block used to hold went with the second control. Every field
    // left here belongs to one button, and nothing in this file can paint or hit a rectangle the step did not give it.
    private static LayoutRect _chromeHeaderRect;
    private static bool _chromeHeaderPending, _chromeTogglePending, _chromeToggleHides;
    private static ResultChromeVisual _chromeToggleVisual;
    private static ResultChromeAnchor _chromeAnchor;
    private static string _chromeWhy = string.Empty;

    // ---- 1.19.44: the settlement page's own pointer, its own main button, and the cursor this mod draws ----
    //
    // Ported from the panel the user already runs (`conflictmodlist/src/TeamVersionsDriver.cs` 443-513/517-569/584-616,
    // `shared/PanelPointer.cs` 54-144). The rules are the same and the globals are still absent: this mod never touches
    // `Cursor.visible`, never locks, never captures and never asks the game for an input mode. It reads the page's cursor
    // and the page's own main button, and it only *stands in for* the cursor inside a rectangle it painted this repaint.
    private static ResultPointerSample _pointer = ResultPointerSample.None;
    // 1.19.59 (independent review L5): the frame this sample was taken on. Every entry that answers from the pointer -
    // the repaint, the page's own press dispatch and the wheel - asks `EnsurePointer` first, so a frame has exactly one
    // sample and all three read the same point. The defect this closes: `PollWheel` runs in the press dispatch *before*
    // `DispatchPressFrame`, and it used to read whatever `_pointer` the previous repaint had left behind.
    private static readonly ResultPointerFrameCache PointerFrame = new ResultPointerFrameCache();
    // The session the two fields above belong to, and the press bookkeeping that belongs to that session only.
    private static ResultPointerSession _pointerSession = ResultPointerSession.None;
    private static bool _pointerSessionValid;
    private static ResultPressSource _pressSource = ResultPressSource.None;
    private static bool _pressDown;
    // The one sample this frame, whoever asked for it first: the native dispatch's prefix and this mod's own `Update` may
    // run in either order, and a second derivation of the same physical press would be a second edge.
    private static int _pressFrame = -1;
    private static ResultPressFrame _pressFrameValue = ResultPressFrame.Unavailable;
    // The press this mod consumed, kept until it really ends (`ResultPressOwnership`). Its identity is the page it began
    // on, captured at the press - never this mod's layout cache, which the hide that press performs drops in the same
    // frame (`ResetPlacement` sets `_page = null`).
    private static ResultPressOwnership _pressOwned;
    // 1.19.46 (UI review F1 close-out): the frame's one *decision*, shared by the page's own dispatch and this mod's
    // poll, and the action layer's own memory of the frame it has already answered. The decision cache is dropped with
    // the layout (`ForgetChrome`); the answer ledger deliberately is not - a hide is not the end of the press that
    // caused it.
    private static int _dispatchFrame = -1;
    private static ResultPressDispatch _dispatchValue = ResultPressDispatch.None;
    private static ResultPressAnswer _pressAnswer = ResultPressAnswer.None;
    // Bounded diagnostic: which source answered, once per change, and why the cursor is not drawn when it is not.
    private static string _pointerNote = string.Empty;
    private static uint _pointerReportGeneration;
    private static int _pointerFailures;
    private const int PointerReportLimit = 6;
    // One cached label style *per control*, each one rebuilt only when that control's glyph size changes. The two
    // controls carry different sizes (13 px toggle, a scaled close), so a single slot rebuilt one of them on every
    // repaint - two `new GUIStyle` allocations a frame for nothing.
    private static GUIStyle _toggleStyle;
    private static int _toggleStyleSize = -1;
    // The header's two runs of text get their own slots for the same reason the two controls do: the name (left
    // aligned) and the key hint (right aligned) are different glyph sizes in two different alignments, and a
    // single slot would rebuild one of them on every repaint.
    private static GUIStyle _titleStyle, _hintStyle;
    private static int _titleStyleSize = -1, _hintStyleSize = -1;
    // One line per kind of failure, not per frame: a page whose controls cannot be measured says so once. The
    // event lines of the two controls are rate-limited per *page* (`ReportChrome`), not by these flags.
    private static bool _loggedChromeDraw, _loggedChromeInput;

    // The height the board's header takes out of its own strip, from the one metrics rule the controls,
    // the tests and the offline preview all read.
    private static float ChromeHeader()
    {
        try { return ResultChromeMetrics.For(Screen.width, Screen.height).HeaderHeight; }
        catch { return ResultChromeMetrics.For(1920f, 1080f).HeaderHeight; }
    }

    private static ResultChromeMetrics ChromeMetrics()
    {
        try { return ResultChromeMetrics.For(Screen.width, Screen.height); }
        catch { return ResultChromeMetrics.For(1920f, 1080f); }
    }

    private static bool ResultsPage()
    {
        var state = GameStateManager.CurrentStateName;
        return state == eGameStateName.ExpeditionSuccess || state == eGameStateName.ExpeditionFail;
    }

    // The pointer, in the same window space every measured rectangle of this mod lives in.
    //
    // 1.19.44 (ported from the panel the user already runs): the *page's own cursor* first - `CM_PageBase.CursorWorldPosition`
    // projected through the camera this build already proves for that page, one Y flip - and the physical mouse only as a
    // labelled fallback for a frame in which the page cannot answer. The order matters: the page's cursor is the point the
    // game itself hit-tests its own menu items with, so a control of this mod answers on a frame where the system cursor is
    // hidden (which is the settlement screen's normal state) exactly as a native one does.
    //
    // `Cursor.visible` is read, never written: while the game is showing a hardware cursor there is nothing to stand in
    // for, and the sample says so (the cursor this mod draws is then suppressed rather than fighting the system one).
    private static bool ChromePointer(out float x, out float y)
    {
        x = float.NaN; y = float.NaN;
        EnsurePointer();
        if (!_pointer.Valid) return false;
        x = _pointer.X; y = _pointer.Y;
        return x >= 0f && y >= 0f && x <= Screen.width && y <= Screen.height;
    }

    // The frame's one pointer sample (L5). The first entry to ask in a frame takes it and every other entry in that same
    // frame reads it: the repaint that draws the table, the press dispatch that answers the page's own button, and the
    // wheel - which is settled from the frame driver and again from the page's press dispatch, i.e. *before*
    // `DispatchPressFrame`. A frame that changes its mind about where the pointer is, half way through, is not a thing:
    // the page's own cursor is written once per frame by the game, so one read of it is the frame's answer. A new frame
    // always samples again - the point of the cache is that two entries agree, not that a value is kept.
    //
    // A read that throws leaves the previous sample in place and is not remembered as this frame's answer: the cache is
    // only claimed by a sample that really happened.
    private static void EnsurePointer()
    {
        int frame;
        try { frame = Time.frameCount; } catch { frame = -1; }
        if (frame < 0 || !PointerFrame.Claim(frame)) return;
        try { _pointer = SamplePagePointer(); }
        catch { PointerFrame.Forget(); }
    }

    // The one page-cursor sample of the frame: world point from the page, the page's own camera, one conversion.
    //
    // Every refusal names itself (`ResultPointerSample.Note`) and falls through to the physical mouse, which is the only
    // source left when the page cannot answer - an unreadable cursor, a camera that is not there, a projection that is
    // not a number or a point behind the camera. A refusal never keeps the previous frame's coordinates.
    private static ResultPointerSample SamplePagePointer()
    {
        Component page = ChromePage();
        if (page == null) return ResultPointerSample.Invalid(ResultPointerSource.PageCursor, "no live result page");
        CM_PageBase menu = null;
        try { menu = page.TryCast<CM_PageBase>(); } catch { menu = null; }
        if (menu == null) return ResultPointerSample.Invalid(ResultPointerSource.PageCursor, "the page is not a CM_PageBase");
        Camera camera = PointerCamera(page);
        if (camera == null) return FallBackToPhysical("no camera proved for this page");
        // 1.19.59: the sampled point is the one the player is looking at. The page carries the game's own cursor object
        // (`CM_PageBase.m_cursor` -> `CM_Cursor.m_trans`/`WorldPos`/`IsLocal`/`IsVisible`, written by the page's own
        // `UpdateCursorPosition`), so that transform is asked first and the mouse-derived `CursorWorldPosition` is the
        // fallback. Both are projected through the same camera and the same one conversion, which is what makes the drawn
        // board, the hovered control, the click and the wheel answer for one point.
        Vector3 world;
        string source;
        if (NativeCursorWorld(menu, out world)) source = "native-cursor";
        else
        {
            source = "page-cursor";
            try { world = menu.CursorWorldPosition; }
            catch (Exception error) { return FallBackToPhysical("CursorWorldPosition threw: " + error.Message); }
        }
        Vector3 screen;
        try { screen = camera.WorldToScreenPoint(world); } catch (Exception error) { return FallBackToPhysical("the projection threw: " + error.Message); }
        ResultPointerViewport viewport = PointerViewport(camera, out bool wholeWindow);
        // 1.19.59: a camera whose raster this build cannot prove is not used to move the player's hotspot. 1.19.55/1.19.56
        // still flipped that unproven point and only labelled it, so on a page whose camera is not the window's own (a
        // render-texture camera, another mod's viewport) the hover, the press and the wheel were all aimed at a point the
        // player was not pointing at - and "space=direct" could not prove otherwise, because it only judges this camera
        // against itself. The physical mouse is where the hotspot really is; the label says so and nothing is guessed.
        if (!ResultPointerSpace.TryWindow(viewport, screen.x, screen.y, screen.z, out float gx, out float gy, out string note))
            return FallBackToPhysical(note);
        if (!wholeWindow)
            return FallBackToPhysical("the page's camera does not prove this window's raster (" + viewport.Describe() + ")");
        _pointerNote = source;
        return new ResultPointerSample(true, gx, gy, ResultPointerSource.PageCursor, string.Empty);
    }

    // The world point of the game's own cursor, when the page exposes one this build may trust.
    //
    // `CM_Cursor` is the sprite the player sees on these menu pages (`m_cursorSprite`, `m_trans`); it is marked
    // `IsLocal`/`IsVisible`, and both are read rather than assumed - a cursor that is a remote player's, or one the page has
    // hidden, is not this machine's hotspot. A transform that is not a number, or one at the world origin (the value an
    // uninitialised wrapper answers), is refused so the fallback stays in charge.
    private static bool NativeCursorWorld(CM_PageBase menu, out Vector3 world)
    {
        world = default;
        try
        {
            CM_Cursor cursor = menu.m_cursor;
            if (cursor == null) return false;
            try { if (!cursor.IsVisible) return false; } catch { }
            try { if (!cursor.IsLocal) return false; } catch { }
            Transform transform = null;
            try { transform = cursor.m_trans; } catch { transform = null; }
            if (transform != null)
            {
                Vector3 point = transform.position;
                if (float.IsFinite(point.x) && float.IsFinite(point.y) && float.IsFinite(point.z) && point.sqrMagnitude > 0.0001f)
                {
                    world = point;
                    return true;
                }
            }
            Vector3 direct = cursor.WorldPos;
            if (float.IsFinite(direct.x) && float.IsFinite(direct.y) && float.IsFinite(direct.z) && direct.sqrMagnitude > 0.0001f)
            {
                world = direct;
                return true;
            }
        }
        catch { }
        return false;
    }

    // The raster facts of a page camera, from the camera's own fields, for `ResultPointerSpace`.
    //
    // 1.19.56 (review 2/3/10): the projection is already a window point (`WorldToScreenPoint` includes the camera's
    // `pixelRect` offset), so this never converts anything - it only decides whether the mapping can be *proven* and what
    // the `settlement space` line may therefore claim. `wholeWindow` is false when a camera renders into a texture or its
    // metadata could not be read; the point is then still flipped with `Screen.height` (the 1.19.54 behaviour) and every
    // line says `unproven`, so a reader is never told a mapping was validated when it was only carried over. The window's
    // own size is always carried, because a rectangle this mod cannot prove must still be measurable - refusing here is
    // what would make the whole page unmeasurable.
    private static ResultPointerViewport PointerViewport(Camera camera, out bool wholeWindow)
    {
        wholeWindow = false;
        try
        {
            // An overlay canvas (`RenderMode.ScreenSpaceOverlay`) hands back a null camera, and
            // `RectTransformUtility.WorldToScreenPoint(null, ...)` is then *documented* to answer in window pixels: the
            // raster is this window's own grid and the mapping is proven direct.
            if (camera == null)
            {
                wholeWindow = true;
                return new ResultPointerViewport(Screen.width, Screen.height, 0f, 0f, Screen.width, Screen.height,
                    Screen.width, Screen.height);
            }
            // A camera that renders into a texture answers in that texture's pixels and where the game puts it on the
            // window is not read here. Unproven - and still answerable, with the window's own height.
            if (camera.targetTexture != null)
                return new ResultPointerViewport(float.NaN, float.NaN, 0f, 0f, Screen.width, Screen.height,
                    Screen.width, Screen.height);
            Rect viewport = camera.pixelRect;
            var facts = new ResultPointerViewport(camera.pixelWidth, camera.pixelHeight,
                viewport.x, viewport.y, viewport.width, viewport.height, Screen.width, Screen.height);
            wholeWindow = facts.Whole;
            return facts;
        }
        catch
        {
            return new ResultPointerViewport(float.NaN, float.NaN, 0f, 0f, Screen.width, Screen.height,
                Screen.width, Screen.height);
        }
    }
    // own canvas camera is asked first (`PageCamera`, the same one `TryScreenRect` projects every measured rectangle
    // with), then the page's static `CM_PageBase.Camera`, then `Camera.main` - and nothing is remembered across a page:
    // a camera from the previous settlement is not evidence about this one.
    //
    // 1.19.46 (UI review note): the blueprint's live finding is transplanted here - the camera that really renders a
    // `CM_PageBase` menu page is `page.m_guiLayer.m_menuCamera.m_camera` (`MainMenuGuiLayer.CameraController.m_camera`,
    // both confirmed in this profile's `Modules-ASM` interop), and `CM_PageBase.Camera` answered null in that reference
    // case. The menu camera is therefore asked *first*, and the chain below it stays as the fallbacks. A camera that
    // cannot be shown to be live is never used: the sample then falls to the physical mouse, which is labelled, instead
    // of projecting the page cursor through the wrong camera.
    private static Camera PointerCamera(Component page)
    {
        try
        {
            var menu = page != null ? page.TryCast<CM_PageBase>() : null;
            Camera drawn = menu != null && menu.m_guiLayer != null && menu.m_guiLayer.m_menuCamera != null
                ? menu.m_guiLayer.m_menuCamera.m_camera
                : null;
            if (Usable(drawn)) return drawn;
        }
        catch { }
        try
        {
            Camera canvas = PageCamera(page, out bool overlay);
            if (Usable(canvas)) return canvas;
        }
        catch { }
        try { if (Usable(CM_PageBase.Camera)) return CM_PageBase.Camera; } catch { }
        try { if (Usable(Camera.main)) return Camera.main; } catch { }
        return null;
    }

    private static bool Usable(Camera camera)
    {
        try { return camera != null && camera.isActiveAndEnabled; } catch { return false; }
    }

    // The physical mouse, kept only as the labelled fallback. Same conversion as always; the note says which refusal sent
    // the frame here, so a log can tell "the page cannot answer on this build" from "nobody moved the mouse".
    private static ResultPointerSample FallBackToPhysical(string why)
    {
        _pointerNote = "physical-fallback: " + why;
        try
        {
            Vector3 pointer = Input.mousePosition;
            if (!float.IsFinite(pointer.x) || !float.IsFinite(pointer.y))
                return ResultPointerSample.Invalid(ResultPointerSource.PhysicalMouse, _pointerNote);
            float x = pointer.x, y = Screen.height - pointer.y;
            if (x < 0f || y < 0f || x > Screen.width || y > Screen.height)
                return ResultPointerSample.Invalid(ResultPointerSource.PhysicalMouse, _pointerNote + "; outside the window");
            return new ResultPointerSample(true, x, y, ResultPointerSource.PhysicalMouse, why);
        }
        catch (Exception error) { return ResultPointerSample.Invalid(ResultPointerSource.PhysicalMouse, _pointerNote + ": " + error.Message); }
    }

    // The page's own main button state, and the one place a press edge is made.
    //
    // `CM_PageBase.MainButtonStatus` is the state the game's own `UpdateButtonPress` dispatches on, so it is asked first;
    // `Input.GetMouseButton(0)` is the labelled fallback for a build whose page cannot answer. Edges are derived from this
    // one sample against the previous frame's *down* state - never from `GetMouseButtonDown/Up`, which would be a second,
    // independent sampler and could disagree with it inside one frame.
    private static ResultPressFrame SamplePress()
    {
        // 1.19.46: one sample per frame, whoever asks. The native dispatch (`CM_PageBase.UpdateButtonPress`, through the
        // prefix) and this mod's own `Update` may run in either order inside one frame; a second call returns the frame
        // that was already derived, so neither order can produce a second edge out of the same physical press.
        int frame = Time.frameCount;
        if (frame == _pressFrame) return _pressFrameValue;
        ResultPressFrame sampled = SamplePressUncached();
        _pressFrame = frame;
        _pressFrameValue = sampled;
        return sampled;
    }

    private static ResultPressFrame SamplePressUncached()
    {
        Component page = ChromePage();
        CM_PageBase menu = null;
        try { menu = page != null ? page.TryCast<CM_PageBase>() : null; } catch { menu = null; }
        if (menu != null)
        {
            try
            {
                bool down = menu.MainButtonStatus;
                ResultPressFrame frame = ResultPressFrame.Of(true, down, _pressDown, ResultPressSource.Page);
                _pressDown = down; _pressSource = ResultPressSource.Page;
                return frame;
            }
            catch { /* the physical fallback below is the answer, and it says so */ }
        }
        try
        {
            bool down = Input.GetMouseButton(0);
            ResultPressFrame frame = ResultPressFrame.Of(true, down, _pressDown, ResultPressSource.Physical);
            _pressDown = down; _pressSource = ResultPressSource.Physical;
            return frame;
        }
        catch
        {
            _pressSource = ResultPressSource.None;
            return ResultPressFrame.Unavailable;
        }
    }

    // The resident result page's native pointer, read from the page the controls stand on (`ChromePage`, which survives
    // `_page` being dropped by a hide) - the identity a press claim is made against.
    private static IntPtr ChromePagePointer()
    {
        try { Component page = ChromePage(); return page != null ? page.Pointer : IntPtr.Zero; }
        catch { return IntPtr.Zero; }
    }

    // Whether the sampled pointer is over a rectangle the page itself is drawing. A press that began on one of the
    // page's own controls is never claimed by this mod, so a known native protection zone keeps its press whatever else
    // is true about the frame.
    private static bool PointerOverNative()
    {
        if (!_pointer.Valid) return false;
        for (int index = 0; index < ChromeNative.Count; index++)
            if (ResultPointerHit.Inside(ChromeNative[index], _pointer.X, _pointer.Y)) return true;
        return false;
    }

    // Every rectangle the page itself is drawing this repaint: the control at the foot of the page and, on a
    // client failure page, the waiting-status line. Only what the page is really drawing reaches this list - a
    // control the game has switched off is not a rectangle on the screen, and treating it as one is what would
    // let a hidden button pause the toggle that stands beside the visible one (`Conflicts`).
    //
    // `TryChromeTextRect` is the same measurement the anchor uses, so the obstacle and the thing the control
    // stands beside can never be two different rectangles for the same text.
    private static void ChromeNativeControls(List<LayoutRect> into, Component page, bool succeeded)
    {
        into.Clear();
        try
        {
            if (succeeded)
            {
                var success = page != null ? page.TryCast<CM_PageExpeditionSuccess>() : null;
                if (success != null && ButtonActive(success.m_btnLeaveExpedition)
                    && TryScreenRect(success.m_btnLeaveExpedition, out LayoutRect leave)) into.Add(leave);
                return;
            }
            var fail = page != null ? page.TryCast<CM_PageExpeditionFail>() : null;
            if (fail == null) return;
            if (ButtonActive(fail.m_btnRestartCheckpoint)
                && TryScreenRect(fail.m_btnRestartCheckpoint, out LayoutRect restart)) into.Add(restart);
            if (ButtonActive(fail.m_btnGoToLobby)
                && TryScreenRect(fail.m_btnGoToLobby, out LayoutRect goToLobby)) into.Add(goToLobby);
            if (fail.m_returnToLobby_text != null && ButtonActive(fail.m_returnToLobby_text)
                && TryChromeTextRect(fail.m_returnToLobby_text, out LayoutRect waiting)) into.Add(waiting);
        }
        catch { }
    }

    // The one anchor this page really offers, and which native fact it came from.
    //
    // A host's page carries the button that sends the squad home, so the toggle stands beside it. A
    // client's page does not - the game leaves `m_btnGoToLobby` permanently inactive and the success
    // page has no waiting-status field at all - so the anchor there is the line the page is really
    // drawing: on the failure page `m_returnToLobby_text`, and on the success page the topmost label in
    // `m_btnLeaveExpedition`'s own subtree that is active, carries text and has arrived at full
    // opacity. No field is invented for it and no coordinate is guessed: a page that draws no such line
    // and no button hands over no anchor, and the toggle is simply not there.
    //
    // `reveal` is how far that native fact has come in, and it is deliberately three answers rather than a
    // number: a button the page has switched on has no reveal at all (`Arrived`), a text still fading in is
    // `Waiting`, and a text whose own alpha could not be read is `Unreadable` - a failed read, which is neither
    // an arrival nor a verdict that the page has no waiting line.
    //
    // `m_returnToLobby_text` is measured by `TryChromeTextRect`, which asks the text's own content twice - its
    // laid-out text bounds and then the glyph mesh it really draws - and never falls back to a parent's
    // rectangle. A decoration container on this page is the size of the screen, and a screen-sized rectangle
    // standing in for "the waiting line" is the one measurement that must never be handed to a control.
    private static bool TryChromeAnchor(Component page, bool succeeded, out LayoutRect anchor,
        out ResultChromeAnchor source, out ResultChromeReveal reveal, out string why)
    {
        anchor = default; source = ResultChromeAnchor.None; reveal = ResultChromeReveal.Arrived; why = "no-page";
        if (page == null) return false;
        try
        {
            if (succeeded)
            {
                var success = page.TryCast<CM_PageExpeditionSuccess>();
                if (success == null) { why = "no-success-page"; return false; }
                if (ButtonActive(success.m_btnLeaveExpedition) && TryScreenRect(success.m_btnLeaveExpedition, out anchor))
                {
                    source = ResultChromeAnchor.HostReturnButton;
                    why = "leave-expedition-button";
                    reveal = ResultChromeReveal.Arrived;   // proven by activation: this anchor has no reveal
                    return true;
                }
                if (success.m_btnLeaveExpedition == null) { why = "no-leave-button"; return false; }
                try
                {
                    foreach (TextMeshPro label in success.m_btnLeaveExpedition.GetComponentsInChildren<TextMeshPro>(true))
                    {
                        if (label == null || !label.gameObject.activeInHierarchy) continue;
                        string text = label.text;
                        if (string.IsNullOrWhiteSpace(text)) continue;
                        if (!(HeadingAlpha(label) >= 0.99f)) continue;
                        if (!TryChromeTextRect(label, out LayoutRect rect)) continue;
                        if (anchor.Width <= 0f || rect.Y < anchor.Y) anchor = rect;
                    }
                }
                catch { why = "label-walk-failed"; return false; }
                if (anchor.Width > 0f && anchor.Height > 0f)
                {
                    // The label this anchor came from is already proven to be drawing at full opacity.
                    reveal = ResultChromeReveal.Arrived;
                    source = ResultChromeAnchor.ClientWaitingStatus;
                    why = "success-footer-label";
                    return true;
                }
                why = "no-drawn-footer-label";
                return false;
            }
            var fail = page.TryCast<CM_PageExpeditionFail>();
            if (fail == null) { why = "no-failure-page"; return false; }
            if (ResultPageRolePolicy.IsClient(Role()))
            {
                if (ChromeTextAnchor(fail.m_returnToLobby_text, out anchor, out reveal, out string textWhy))
                {
                    source = ResultChromeAnchor.ClientWaitingStatus;
                    why = "return-to-lobby-text/" + textWhy;
                    return true;
                }
                // "The page has no such field" and "the field is there and could not be measured this repaint"
                // are different answers, and only the first one is a page with nothing to stand beside.
                why = fail.m_returnToLobby_text == null ? "no-waiting-status-field" : "waiting-status-" + textWhy;
                return false;
            }
            LayoutRect restartRect = default;
            LayoutRect lobbyRect = default;
            bool restart = ButtonActive(fail.m_btnRestartCheckpoint) && TryScreenRect(fail.m_btnRestartCheckpoint, out restartRect);
            bool lobby = ButtonActive(fail.m_btnGoToLobby) && TryScreenRect(fail.m_btnGoToLobby, out lobbyRect);
            if (!restart && !lobby)
            {
                if (ChromeTextAnchor(fail.m_returnToLobby_text, out anchor, out reveal, out string textWhy))
                {
                    source = ResultChromeAnchor.ClientWaitingStatus;
                    why = "return-to-lobby-text-host-fallback/" + textWhy;
                    return true;
                }
                why = "no-host-action";
                return false;
            }
            // 开关贴在哪颗按钮左边：**优先「返回大厅」**，而不是位置更高的「重启检查点」。玩家在失败页真正要按的是
            // 回大厅；把开关挂在更高的那颗键上，会让它落在两者之间那块空处，看起来谁也不属于。两颗键都仍然参与
            // 避让（`TryToggle` 的 obstacles 里两颗都在），所以原版的避让没有被取消，只是排队的顺序换了。
            anchor = lobby ? lobbyRect : restartRect;
            source = ResultChromeAnchor.HostActionButton;
            why = lobby ? "go-to-lobby-button" : "restart-checkpoint-button";
            reveal = ResultChromeReveal.Arrived;   // proven by activation
            return true;
        }
        catch (Exception error)
        {
            why = "anchored-read-failed: " + error.GetType().Name;
            return false;
        }
    }

    // A native text as an anchor: drawn, measurable, and with its reveal read out of the text itself.
    //
    // The three refusals are named, because the recorded client page's only line was "no-waiting-status" and
    // that word covered "the field is null", "the object is switched off" and "the text measured nothing" - three
    // different facts, only the first of which means the page has nothing to stand beside.
    private static bool ChromeTextAnchor(TMP_Text text, out LayoutRect rect, out ResultChromeReveal reveal, out string why)
    {
        rect = default; reveal = ResultChromeReveal.Arrived; why = "no-field";
        if (text == null) return false;
        try
        {
            if (!text.gameObject.activeInHierarchy) { why = "not-drawn"; return false; }
            if (!TryChromeTextRect(text, out rect)) { why = "unmeasured"; return false; }
            reveal = ChromeReveal(text);
            why = reveal == ResultChromeReveal.Arrived ? "alpha-arrived"
                : reveal == ResultChromeReveal.Waiting ? "alpha-fading" : "alpha-unreadable";
            return true;
        }
        catch (Exception error) { why = "read-failed-" + error.GetType().Name; return false; }
    }

    // How far a text has come in. A not-a-number alpha is `Unreadable`, never `Arrived`: a TMP whose alpha cannot
    // be read is a measurement that failed, and the settle rule then has to earn the place from the geometry
    // alone. It is also never reported as "this page has no waiting line" - one failed read must not decide that.
    private static ResultChromeReveal ChromeReveal(TMP_Text text)
    {
        // A text that cannot even be cast to the component the alpha is read from is a failed read, not an
        // arrival - the same answer as a not-a-number alpha.
        var heading = text != null ? text.TryCast<TextMeshPro>() : null;
        if (heading == null) return ResultChromeReveal.Unreadable;
        float alpha = HeadingAlpha(heading);
        if (!float.IsFinite(alpha)) return ResultChromeReveal.Unreadable;
        return alpha >= 0.999f ? ResultChromeReveal.Arrived : ResultChromeReveal.Waiting;
    }

    // The rectangle a native text really draws, by two independent routes and never by a parent's rectangle.
    //
    // `TryTextRect` is the text's own laid-out extent; on a world-space `TextMeshPro` it answers whenever the
    // text has been laid out, and it is the route that knows nothing about renderers. The second route is the
    // glyph mesh the same text draws: `TryScreenRect` measures a `Renderer`'s own bounds, which for a TMP text
    // is the generated quads - the pixels the player is reading - and it refuses a rectangle that covers the
    // whole window. Both routes measure *this text*, so a page's decoration container (which on this page is
    // the size of the screen) can never be mistaken for the waiting-status line.
    //
    // The two routes are asked in that order because the first is exact when it answers and the second costs a
    // projection; either one answering is a real measurement of the text, and both answering "no" means the
    // text is not on the screen this frame - which is the only thing that makes a control go pending rather
    // than get a place it did not earn.
    private static bool TryChromeTextRect(TMP_Text text, out LayoutRect rect)
    {
        rect = default;
        if (text == null) return false;
        if (TryTextRect(text, out rect)) return true;
        try
        {
            var component = text.TryCast<Component>();
            if (component == null) return false;
            // Exactly the text's own renderer. `TryScreenRect` asks the component it is given for one, so this
            // is a check that a glyph mesh exists rather than a walk up the hierarchy.
            if (component.GetComponent<Renderer>() == null) return false;
            return TryScreenRect(component, out rect);
        }
        catch { rect = default; return false; }
    }

    // The plate the board is drawn on, header included: the header is the band this mod plans above the table.
    private static LayoutRect ChromeBoardRect()
    {
        if (_fixedPlaced && _origin.Width > 0f) return ResultChromeLayout.BoardOf(_origin, ChromeMetrics());
        if (_holdValid && _holdBoard.Width > 0f) return _holdBoard;
        if (_grid != null && _origin.Width > 0f) return ResultChromeLayout.BoardOf(_origin, ChromeMetrics());
        return default;
    }

    // The page the two controls stand on.
    //
    // Deliberately not the board's layout page: `_page` is the cache the board's own placement lives in, and it is
            // dropped the moment the board is hidden (F8 or the toggle), by a configuration reload and by every
    // re-measure. Sourcing the controls from it is exactly why the SHOW button used to disappear together with
    // the board it was meant to bring back. The result context and its resident page object are the truth here:
    // the game keeps both result pages alive for the session and raises the one for the state it is in.
    private static Component ChromePage()
    {
        if (_page != null && PageActive(_page)) return _page;
        var state = GameStateManager.CurrentStateName;
        if (state == eGameStateName.ExpeditionSuccess) return Success != null && PageActive(Success) ? Success : null;
        if (state == eGameStateName.ExpeditionFail) return Fail != null && PageActive(Fail) ? Fail : null;
        return null;
    }

    // 1.19.55: the one bounded observation that can locate a real pointer/board mismatch, written once per page *and* window.
    //
    // The recorded 1280x960 page pressed twice at (353,935) and (347,925) while the committed toggle stood at (393,916,68x21) -
    // forty-odd pixels apart, and nothing in that log could say which of the two the drawn board agreed with, or whether the
    // pointer, the measured controls and the drawn board were even on one grid. This line puts the spaces beside each other in
    // one place: the window, the page camera's own raster and viewport (the grid every measured rectangle and the pointer are
    // converted through, `ResultPointerSpace`), the IMGUI mouse position the engine handed this repaint (`Event.current`), the
    // sampled page pointer with the source it came from, the physical mouse, and the toggle this repaint committed. It is a
    // measurement, not a claim: `grid=same` means the camera really renders the window and no conversion took place.
    private static string _spaceReported = string.Empty;

    private static void ReportPointerSpace(Component page, ResultChromeMetrics metrics)
    {
        try
        {
            string key = (page != null ? page.Pointer.ToString("X") : "none") + "/" + Screen.width + "x" + Screen.height;
            if (key == _spaceReported) return;
            _spaceReported = key;
            Camera camera = PointerCamera(page);
            ResultPointerViewport viewport = PointerViewport(camera, out bool whole);
            float guiX = float.NaN, guiY = float.NaN;
            try { if (Event.current != null) { guiX = Event.current.mousePosition.x; guiY = Event.current.mousePosition.y; } }
            catch { }
            float physicalX = float.NaN, physicalY = float.NaN;
            try { Vector3 mouse = Input.mousePosition; physicalX = mouse.x; physicalY = Screen.height - mouse.y; }
            catch { }
            Plugin.Logger?.LogInfo("[DIAG] settlement space: window=" + Screen.width + "x" + Screen.height
                + "," + viewport.Describe()
                + ",space=" + (whole ? "direct" : "unproven")
                + ",gui=" + Vector2Text(guiX, guiY)
                + ",physical=" + Vector2Text(physicalX, physicalY)
                + "," + _pointer.Describe()
                + ",toggle=" + ChromeRect(_chromeToggleRect) + (_chromeToggleValid ? string.Empty : "(not drawn)")
                + ",toggleFont=" + metrics.ToggleFont.ToString("0") + "px"
                + ",role=" + ResultPageRolePolicy.Name(Role())
                + "; one line per page and window - the camera's raster, the engine's own GUI mouse position, the sampled page"
                + " pointer and the rectangle that was committed, so a page whose clicks land beside the drawn control says"
                + " which of the four drifted instead of leaving it to be guessed (1.19.55).");
        }
        catch { }
    }

    private static string Vector2Text(float x, float y)
        => float.IsFinite(x) && float.IsFinite(y)
            ? "(" + x.ToString("0") + "," + y.ToString("0") + ")"
            : "unreadable";

    // One repaint of the two controls. Called from the top of `Draw`, before every gate and every early
    // return, because the toggle belongs to the result page and not to the board: it stays on the screen
    // while the board is hidden, which is the only way back to it.
    //
    // Every frame starts from "no control drawn": the rectangle is dropped, and `_chromeDrawnFrame` is only
    // written by a frame that really drew it. That is what keeps the input path from ever answering for a
    // rectangle that belongs to a page, a window or a visibility the player is no longer looking at. What
    // survives the frame is the *place* (`ChromePlaces`), which is the whole point: a new figure on the board,
    // a key, a pointer and a hide/show of the board must not move the control.
    private static void DrawChrome(bool boardUp)
    {
        _chromeToggleValid = false;
        _chromeDrawnFrame = -1;
        try
        {
            if (!ResultsPage()) { ForgetChrome(); return; }
            // The page the controls stand on - the resident result page itself, not the board's layout cache.
            // `_page` is dropped the moment the board is hidden (F8, the toggle), by a configuration
            // reload and by every re-measure, so sourcing the controls from it is what made the SHOW button
            // disappear with the board it was meant to bring back.
            Component page = ChromePage();
            // No page this repaint: draw nothing and keep every place already committed. A repaint that cannot
            // see the page is not a page change (the places are forgotten by `ForgetChrome` alone).
            if (page == null) return;
            bool succeeded = GameStateManager.CurrentStateName == eGameStateName.ExpeditionSuccess;
            _chromeScreenWidth = Screen.width; _chromeScreenHeight = Screen.height;
            ResultChromeMetrics metrics = ChromeMetrics();
            bool pointerValid = ChromePointer(out float pointerX, out float pointerY);

            // The commitment is told what page/settlement/window this repaint belongs to *before* the page is
            // asked for anything: a new layout is a new layout, and a re-measure of the same one is not. `Step`
            // makes the same call, so this only prepares the state the probe gate below reads.
            bool newContext = ChromePlaces.Starts(page.Pointer, succeeded, Screen.width, Screen.height, _settlementGeneration);
            if (newContext) ChromeAnchorSettle.Forget();

            // This repaint's measurement of the page, filled in exactly as the page answered: nothing is
            // derived here and nothing is guessed. An anchor the page did not draw says so, and a plate it
            // derived here and nothing is guessed. An anchor the page did not draw says so, and a plate it could not be read
            // from says so, which is what keeps a control off the screen in that frame.
            ChromeFrame.Reset();
            ChromeFrame.Page = page.Pointer;
            ChromeFrame.Succeeded = succeeded;
            ChromeFrame.Session = _settlementGeneration;
            // 1.19.48: the frame this repaint belongs to. `OnGUI` runs several times per frame and the native-conflict rule
            // counts *frames*, never calls, so the number is handed in here - the one place that knows it.
            ChromeFrame.Frame = Time.frameCount;
            ChromeFrame.ScreenWidth = Screen.width;
            ChromeFrame.ScreenHeight = Screen.height;
            ChromeFrame.BoardUp = boardUp;
            ChromeNative.Clear();
            ChromeNativeControls(ChromeNative, page, succeeded);
            ChromeFrame.Native = ChromeNative;
            LayoutRect board = ChromeBoardRect();
            _chromeBoardRect = board;
            ChromeFrame.PlateDrawn = board.Width > 0f && board.Height > 0f;
            ChromeFrame.Plate = board;
            // There is no second storey to offer the toggle any more (1.19.45): the place it stands in is the one
            // beside the control the page really draws, or nothing at all.
            if (ChromePlaces.NeedsAnchor)
            {
                // Only while the toggle is still pending is the page walked for an anchor. Once the place is
                // committed the page's own tree is not searched again - the settle observation would have
                // nothing to add and the walk is the expensive half of this method.
                if (TryChromeAnchor(page, succeeded, out LayoutRect anchor, out ResultChromeAnchor source,
                        out ResultChromeReveal reveal, out string why))
                {
                    ChromeFrame.AnchorDrawn = true;
                    ChromeFrame.Anchor = anchor;
                    ChromeFrame.Source = source;
                    ChromeFrame.Reveal = reveal;
                    ChromeFrame.AnchorWhy = why;
                    _chromeAnchor = source;
                }
                else
                {
                    ChromeFrame.AnchorDrawn = false;
                    ChromeFrame.AnchorWhy = why;
                    _chromeAnchor = ResultChromeAnchor.None;
                }
            }
            else
            {
                ChromeFrame.AnchorDrawn = false;
                ChromeFrame.AnchorWhy = "committed";
            }
            _chromeWhy = ChromeFrame.AnchorWhy;

            // The one decision: what stands where this frame, and whether this is the frame that committed it.
            ResultChromeDecision decision = ResultChromeLayout.Step(ChromePlaces, ChromeAnchorSettle, metrics, ChromeFrame);
            if (newContext || decision.NewContext)
            {
                // A new page, a new settlement, a new page flavour or a new window - the only thing that drops a
                // press in flight.
                ToggleGate.Reset();
            }
            // The decision is *booked* here and painted by `FinishChromeDraw`, which `Draw` runs in a `finally`:
            // the band belongs to a board that is really on the screen this repaint, so a frame that deferred the
            // board still leaves no header floating over the game's page, while the toggle is booked on every path,
            // board or no board - it is the only pointer back to a hidden board.
            // (`_chromeToggleHides` is the label this repaint draws: HIDE when the board is up, SHOW when it is not.
            // The press reads exactly that, so a button can never do the opposite of the word on it.)
            //
            // 1.19.43: booked only. `FinishChromeDraw` paints it and opens the hit rectangle afterwards, so the
            // rectangle the input path tests is always one that was really painted.
            _chromeToggleRect = decision.ToggleDrawn ? decision.Toggle : default;
            _chromeTogglePending = decision.ToggleDrawn;
            _chromeToggleValid = false;
            _chromeToggleHides = boardUp;
            _chromeToggleVisual = ResultChromeLayout.Visual(ToggleGate, decision.Toggle, true, pointerValid, pointerX, pointerY);
            _chromeHeaderRect = decision.HeaderDrawn ? decision.Header : default;
            _chromeHeaderPending = decision.HeaderDrawn;
            ReportChrome(decision, metrics);
            // 1.19.55: one bounded observation of the four spaces this repaint measured in, once per page and window.
            ReportPointerSpace(page, metrics);
        }
        catch (Exception error)
        {
            ForgetChrome();
            if (_loggedChromeDraw) return;
            _loggedChromeDraw = true;
            Plugin.Logger?.LogWarning("[DIAG] the settlement board's controls could not be drawn this frame and are not clickable: " + error.Message);
        }
    }

    // Everything the settlement page's own surface paints, in the one order that makes each of it true.
    //
    //   the band    painted only when this repaint's table really drew (`_boardDrawnFrame == Time.frameCount`, written
    //               by the one `HudTable.Draw` in `Draw`). The recorded pages painted the band ahead of every gate, so
    //               a frame that deferred the board still left a header floating over the game's page.
    //   the toggle  painted on *every* path, board or no board: it is the only pointer back to a hidden board.
    //
    // The close that used to be painted between them is gone with the second control (1.19.45): this page's surface
    // has one button on it, and no button belongs to the band.
    //
    // Called from `Draw`'s `finally`, so every early return and every exception lands here with the right answer.
    private static void FinishChromeDraw()
    {
        try
        {
            bool boardDrawn = _boardDrawnFrame == Time.frameCount;
            if (boardDrawn && _chromeHeaderPending && _chromeHeaderRect.Width > 0f) DrawChromeHeader(_chromeHeaderRect);
            ResultChromeMetrics metrics = ChromeMetrics();
            if (_chromeTogglePending && _chromeToggleRect.Width > 0f && _chromeToggleRect.Height > 0f)
            {
                DrawChromeControl(_chromeToggleRect, metrics.ToggleFont, ResultChromeTheme.ToggleLabel(_chromeToggleHides),
                    _chromeToggleVisual, 1f);
                _chromeToggleValid = true;
            }
            else
            {
                // Nothing was painted, so nothing is clickable: no ghost hit rectangle on the empty place a control
                // this repaint decided on but did not draw would have left behind.
                _chromeToggleValid = false;
                _chromeToggleRect = default;
            }
            if (_chromeToggleValid) _chromeDrawnFrame = Time.frameCount;
        }
        catch (Exception error)
        {
            ForgetChrome();
            if (_loggedChromeDraw) return;
            _loggedChromeDraw = true;
            Plugin.Logger?.LogWarning("[DIAG] the settlement board's controls could not be drawn this frame and are not clickable: " + error.Message);
        }
    }

    // There is no second cursor.
    //
    // 1.19.59: this file used to paint its own gold cross (`DrawPointerCursor`) whenever `!Cursor.visible`, clipped to the
    // plate or the toggle. It was a *second* cursor: the settlement pages already draw the game's own one, and the interop
    // this profile ships says so plainly - `CM_PageBase.m_cursor` is a `CM_Cursor` with its own `m_trans`/`WorldPos`/
    // `LocalPos` and `m_cursorSprite`, updated by the page's own `UpdateCursorPosition`. Two cursors drawn from two different
    // points is exactly the "游戏鼠标与看板光标错位" the player reported, and no amount of converting the mod's own copy can
    // fix a hotspot the player is not looking at. What the player aims with is the game's cursor: `SamplePagePointer` reads
    // *its* world transform when the page exposes one, and falls back to the labelled physical mouse - the one place the
    // hotspot is certainly true - instead of painting a third answer on top of both.

    // Whether the game's own press dispatch must skip this press.
    //
    // The question is answered from one decision and one claim, and from nothing else:
    //
    //   * the decision (`ResultPressDispatch`) is the *frame's* - the same one this mod's own poll makes, made here when
    //     the page's dispatch runs first. That is the 1.19.46 (UI review F1) close-out: a press that starts on this
    //     mod's toggle used to find no claim yet, pass the press through, and then be answered by the mod a moment
    //     later in the same frame - one click, two things. The prefix is therefore allowed to *make* the decision, and
    //     the decision claims the press at the moment it is made (`EvaluatePressFrame`), not when the action is issued.
    //   * the claim (`ResultPressOwnership`) carries the *page it was made on*, by native pointer. 1.19.46 (UI review
    //     F1) had already replaced the layout cache `_page` with that identity, because the hide the press performs drops
    //     `_page` in the same frame (`ApplyBoardVisibility(false)` -> repaint -> `ResetPlacement` -> `_page = null`).
    //
    // Everything else - another page, an inactive page, no focus, a released or expired press, no claim at all - answers
    // false and drops the claim, so the game keeps every press that is not provably ours and no other page loses input.
    internal static bool ShouldSuppressNativePress(CM_PageBase page)
    {
        try
        {
            if (page == null || !ResultsPage()) return false;
            IntPtr candidate;
            bool active;
            try
            {
                candidate = page.Pointer;
                active = page.gameObject != null && page.gameObject.activeInHierarchy;
            }
            catch { _pressOwned.Clear(); return false; }
            if (!active) { _pressOwned.Clear(); return false; }
            bool focused;
            try { focused = Application.isFocused; } catch { focused = true; }
            if (!focused) { _pressOwned.Clear(); return false; }
            // Only the page this mod is resident on may be answered - and claimed - here: the poll below reads *its*
            // pointer and *its* two rectangles. A dispatch for any other page passes through untouched; it cannot end a
            // claim that was made on ours.
            if (candidate != ChromePagePointer()) return false;
            // 1.19.59: the page's own press dispatch is the second place a frame's wheel can be settled from - the frame
            // driver is the other - and the router's frame latch keeps the two to one settlement, exactly like the
            // reference's `HandleMouse` being called from both `Update` and `SuppressLobbyPress`. It only reads.
            PollWheel();
            ResultPressDispatch frame = DispatchPressFrame();
            if (!frame.Live) return false;
            // A release, or a button state that cannot be read, is not a press this mod owns: it passes through, and the
            // claim ends with it.
            if (!frame.Press.Available || !frame.Press.Down) { _pressOwned.Clear(); return false; }
            return _pressOwned.ShouldSuppress(candidate, active, focused, Time.realtimeSinceStartup);
        }
        catch { _pressOwned.Clear(); return false; }
    }

    // What the two controls did this repaint, one line per event and never one per frame.
    //
    // Rate limiting is per page, not per process: `ResultReportPolicy.OncePerPage` writes into
    // `HeadingReports`, which is cleared when a result page attaches. The old flags
    // (`_loggedChromeToggle`/`_loggedChromeClose`) were process-wide, and the recorded Survival run proves what
    // that costs - three settlements, one line, and the second and third unable to say whether their page drew
    // a waiting-status line at all. The committed line carries the numbers a reader needs without a screenshot:
    // the control's rectangle, the header band with its two type sizes, and how many times the place was placed.
    private static void ReportChrome(ResultChromeDecision decision, ResultChromeMetrics metrics)
    {
        if (decision.ToggleCommitted)
        {
            if (ResultReportPolicy.OncePerPage(HeadingReports, "chrome-committed", Forced, _forcedOutcomePending))
            {
                Plugin.Logger?.LogInfo($"[DIAG] settlement controls committed: toggle={ChromeRect(ChromePlaces.Toggle.Rect)} "
                    + $"(anchor={_chromeAnchor}, {ChromePlaces.Toggle.Why}), "
                    + $"header={(decision.HeaderDrawn ? ChromeRect(decision.Header) : "unmeasurable")} inside the board's own plate "
                    + $"(titleFont={metrics.HeaderFont:0}px, hintFont={metrics.HeaderHintFont:0}px), "
                    + $"placements=toggle{ChromePlaces.Toggle.Placements}, "
                    + $"role={ResultPageRolePolicy.Name(Role())}, screen={Screen.width}x{Screen.height}; "
                    + "a place is committed once per page, page flavour and window - a repaint, a key, a pointer or a new figure on the board never moves it, "
                    + "and a native control that later arrives on top of it only pauses the hit until it leaves. There is exactly one control on this page: the toggle at the page's own anchor.");
            }
        }
        if (decision.Pending && ResultReportPolicy.OncePerPage(HeadingReports, "chrome-toggle-pending", Forced, _forcedOutcomePending))
        {
            string why = string.IsNullOrEmpty(decision.ToggleWhy) ? _chromeWhy : decision.ToggleWhy;
            Plugin.Logger?.LogInfo($"[DIAG] settlement toggle pending: {why}, restarts={ChromeAnchorSettle.Restarts}, role={ResultPageRolePolicy.Name(Role())}, "
                + $"screen={Screen.width}x{Screen.height}; the page has not settled the control this toggle stands beside - it has not arrived "
                + $"(reveal={ResultChromeRevealName.Of(ChromeFrame.Reveal)}) or it is still moving - so nothing is drawn and no click box exists "
                + "for it: no provisional rectangle is ever painted, an alpha that could not be read is never treated as arrived (the settle "
                + "observation is dropped, so the place has to be earned again from two fresh arrived samples), F8 is untouched, and the page "
                + "is observed again next repaint.");
        }
        if (decision.ToggleSuspended)
        {
            if (ResultReportPolicy.OncePerPage(HeadingReports, "chrome-toggle-paused-" + ChromePlaces.Toggle.Suspensions, Forced, _forcedOutcomePending))
            {
                // 1.19.48: the line names the rectangle that paused it (index `#n` into the page's own list, with its pixels)
                // and the number of distinct frames it stood there, so the next recorded frame can be read without guessing.
                Plugin.Logger?.LogInfo($"[DIAG] settlement toggle paused: the page's own control now covers {ChromeRect(ChromePlaces.Toggle.Rect)} "
                    + $"({ChromePlaces.Toggle.Pause}, suspensions={ChromePlaces.Toggle.Suspensions}); the hit and the draw are withheld and the same "
                    + "rectangle comes back when it leaves - the control is not moved and no second place is measured for it. The pause needs the "
                    + "overlap to stand for " + ResultChromeSlot.ConflictFrames + " distinct frames, so a single-frame overlap never pauses it; a press "
                    + "that lands on the page's own control is answered by the page on the first frame, paused or not.");
            }
        }
        else if (decision.ToggleMoved)
        {
            // 1.19.59: a real native rectangle that stood on the place moved it instead of withholding it. One line per page
            // is enough - the place is committed once per page, flavour and window, and this says where it went.
            if (ResultReportPolicy.OncePerPage(HeadingReports, "chrome-toggle-moved", Forced, _forcedOutcomePending))
            {
                Plugin.Logger?.LogInfo($"[DIAG] settlement toggle moved aside: a real native rectangle covered the place it was committed to "
                    + $"({decision.ToggleConflict ?? "overlaps=none"}), so the same search the commit ran was asked again with this repaint's own rectangles and the control now stands at "
                    + $"{ChromeRect(ChromePlaces.Toggle.Rect)} (placements={ChromePlaces.Toggle.Placements}, revisions={ChromePlaces.Toggle.Revisions}, "
                    + $"moves={ResultChromeSlot.ConflictMoves} at most per page). The button is never taken away from the pointer: it is re-earned beside the page's control.");
            }
        }
        else if (decision.ToggleResumed)
        {
            if (ResultReportPolicy.OncePerPage(HeadingReports, "chrome-toggle-resumed-" + ChromePlaces.Toggle.Suspensions, Forced, _forcedOutcomePending))
            {
                Plugin.Logger?.LogInfo($"[DIAG] settlement toggle released: the conflict left and the control is back at {ChromeRect(ChromePlaces.Toggle.Rect)}, "
                    + $"the same place it was committed to (placements={ChromePlaces.Toggle.Placements}).");
            }
        }
        // A flicker - an overlap that did not survive `ResultChromeSlot.ConflictFrames` - costs one line per page instead of
        // one withdrawal per frame: it is the recorded defect, and the line is what says the button was kept.
        if (decision.ToggleFlicker && ResultReportPolicy.OncePerPage(HeadingReports, "chrome-toggle-flicker", Forced, _forcedOutcomePending))
        {
            Plugin.Logger?.LogInfo($"[DIAG] settlement toggle kept: a native rectangle touched {ChromeRect(ChromePlaces.Toggle.Rect)} "
                + $"({decision.ToggleConflict ?? "overlaps=none"}, streak={ChromePlaces.Toggle.ConflictStreak} of "
                + ResultChromeSlot.ConflictFrames + " frames); one repaint of overlap is a flicker, not a conflict, so the draw and the hit stay "
                + "exactly where they are and the button is not taken away from the pointer that is aiming at it.");
        }
    }
    // 1.19.45: `OfferChromeSeat`, `EntryAnchorLine` and `MeasureEntryBand` lived here - the two fallback storeys that
    // stood the toggle in the board's own header (beside a close that no longer exists) and, when there was no board
    // to stand in at all, docked it at the window's right margin on the page's own title edge. They are gone: the
    // toggle stands beside the control the page really draws or it is not drawn, and a page with no board has F8.
    // Nothing in this file invents a place for it that the page did not draw.

    // The board's own header band: the raised tone one step above the table's ground, with the band's plan drawn on
    // it - the 2 px accent rule across its top edge, the board's own name and the key that operates it
    // (`ResultChromeLayout.PlanHeader`). It is drawn from the rectangle the same decision gave the band, and the plan
    // is told the toggle's own rectangle so a control that really stands on the band keeps its room.
    //
    // What this replaced: a bare `GroundRaised @0.92` band with one button on it and nothing else - the recorded pages
    // had a plate a player could not name and no key hint anywhere on it. Every part is planned, and the plan gives the
    // key hint up before the name, so a narrow plate loses decoration and never the line that says how to get the
    // board back. The button itself is not here: the only visible way to show or hide the board is the toggle at the
    // page's own anchor (1.19.45), and the band draws only what the plan measured.
    private static void DrawChromeHeader(LayoutRect header)
    {
        Texture2D white = Texture2D.whiteTexture;
        if (white == null) return;
        if (!(header.Width > 0f) || !(header.Height > 0f)) return;
        ResultChromeMetrics metrics = ChromeMetrics();
        // 1.19.62: the run's created-enemy figure is the third run on this band. It comes from the table's own content
        // (`_content.Figure`, filled by `ImpactBoard.Build`), is measured by the plan with this band's own font and
        // insets, and is drawn by the same `DrawChromeLabel` call the name and the key hint use. A null figure - the
        // plan's default - is the pre-1.19.62 band exactly.
        ResultChromeLayout.ResultChromeHeaderPlan plan = ResultChromeLayout.PlanHeader(header, metrics,
            _chromeToggleRect, _chromeToggleValid, _content != null ? _content.Figure : null);
        Color previous = GUI.color;
        Color previousContent = GUI.contentColor;
        try
        {
            GUI.color = ChromeColor(ResultChromeTheme.GroundRaised, ResultChromeTheme.HeaderBandAlpha);
            GUI.DrawTexture(new Rect(header.X, header.Y, header.Width, header.Height), white);
            // One line on the whole board, and it is the accent's: 2 px, full width, flat. No gradient, no glow.
            if (plan.Rule.Width > 0f && plan.Rule.Height > 0f)
            {
                GUI.color = ChromeColor(ResultChromeTheme.Accent, 1f);
                GUI.DrawTexture(new Rect(plan.Rule.X, plan.Rule.Y, plan.Rule.Width, plan.Rule.Height), white);
            }
            GUI.contentColor = Color.white;
            // 1.19.42: the plan names the label it measured, and that label is the only one drawn. Drawing `TitleLabel`
            // here would draw a word wider than the box the plan just measured for it - the 74 px box on a 300 px plate,
            // with `战斗结算看板` six ems wide inside it, spilling over the close.
            if (plan.HasTitle) DrawChromeLabel(plan.Title, metrics.HeaderFont, plan.TitleLabel, ResultChromeTheme.HeaderTitle, false);
            if (plan.HasHint) DrawChromeLabel(plan.Hint, metrics.HeaderHintFont, ResultChromeTheme.KeyHintLabel, ResultChromeTheme.HeaderHint, true);
            // 1.19.62: the run's created-enemy figure, in the room the plan measured for it after the name. Same call,
            // same face, same band - the one difference is the string, which is the board model's own figure.
            if (plan.HasFigure) DrawChromeLabel(plan.Figure, metrics.HeaderHintFont, plan.FigureLabel, ResultChromeTheme.HeaderHint, false);
        }
        finally
        {
            GUI.color = previous;
            GUI.contentColor = previousContent;
        }
        if (HeadingReports.Add("chrome-header-plan"))
            Plugin.Logger?.LogInfo($"[DIAG] settlement header: {plan.Describe()}, titleFont={metrics.HeaderFont:0}px, hintFont={metrics.HeaderHintFont:0}px, screen={Screen.width}x{Screen.height}, titleLabel={(plan.HasTitle ? plan.TitleLabel : "none")}; the band is a step of tone with one 2 px accent rule, only a control that stands on the band spends its room, the key hint keeps its line first, and the name drops to its short form and then away. The band carries no button of its own since 1.19.45.");
    }

    // One run of header text, drawn in its own copy of the table's face: the name left aligned, the key hint
    // right aligned, both with `GUI.color` and `GUI.contentColor` pinned to white by the caller so the stated ink
    // is the pixel (the 1.19.41 finding below is the same one). No ellipsis: the plan only produces a rectangle
    // for a line that fits, so `clipping` here is a guard and never a truncation.
    private static void DrawChromeLabel(LayoutRect rect, float fontPixels, string label, ResultInk ink, bool rightAligned)
    {
        if (!(rect.Width > 0f) || !(rect.Height > 0f) || string.IsNullOrEmpty(label)) return;
        int size = Mathf.Max(8, Mathf.RoundToInt(fontPixels));
        GUIStyle style = ChromeLabelStyle(size, rightAligned);
        if (style == null) return;
        style.normal.textColor = ChromeColor(ink, 1f);
        GUI.Label(new Rect(rect.X, rect.Y, rect.Width, rect.Height), label, style);
    }

    // One control: a flat fill, a label in the native
    // TMP face the board already draws with, and the accent rule under a control the player is pressing.
    // No scale, no animation, no glow.
    //
    // The label is drawn with `GUI.color` back at white and with its own copy of the table's style. Both are
    // 1.19.41 findings and both were real, which is why a control could be unreadable on a live client page:
    //
    //   GUI.color  IMGUI multiplies it into everything it draws, text included, and it was still holding the
    //              *fill* when the label was drawn - so an idle toggle's ivory word was multiplied by #33332B
    //              (a fifth of the brightness it is stated at). A face and a label of the same tone is a control
    //              no resolution makes readable.
    //   the style  `HudTable.Style` hands back one cached instance shared with the board's own text, so the
    //              `alignment`/`normal.textColor` writes were a mutation of the table's style rather than of
    //              this control's. `ChromeStyle` copies it once per glyph size instead; the table keeps the
    //              instance it owns and this control keeps its own.
    private static void DrawChromeControl(LayoutRect rect, float fontPixels, string label, ResultChromeVisual state,
        float opacity)
    {
        Texture2D white = Texture2D.whiteTexture;
        if (white == null) return;
        ResultChromeLayout.Visuals(state, out ResultInk fill, out ResultInk ink);
        Color previous = GUI.color;
        Color previousContent = GUI.contentColor;
        try
        {
            GUI.color = ChromeColor(fill, opacity);
            GUI.DrawTexture(new Rect(rect.X, rect.Y, rect.Width, rect.Height), white);
            if (state == ResultChromeVisual.Pressed)
            {
                GUI.color = ChromeColor(ResultChromeTheme.Accent, opacity);
                GUI.DrawTexture(new Rect(rect.X, rect.Bottom - ResultChromeTheme.FocusRule, rect.Width, ResultChromeTheme.FocusRule), white);
            }
            int size = Mathf.Max(8, Mathf.RoundToInt(fontPixels));
            GUIStyle style = ChromeStyle(size);
            if (style == null || string.IsNullOrEmpty(label)) return;
            // Both IMGUI tints are pinned to white before the word is drawn: `GUI.color` is what used to multiply
            // the label by the fill it stands on, and `GUI.contentColor` is the one anything outside this renderer
            // may have left behind - a tint that would make "the stated ink" a promise rather than the pixel. Both
            // are put back in the `finally`, so nothing this control does can colour the board, the in-level HUD or
            // the next control.
            GUI.color = Color.white;
            GUI.contentColor = Color.white;
            style.normal.textColor = ChromeColor(ink, opacity);
            GUI.Label(new Rect(rect.X + ResultChromeTheme.ControlPadX, rect.Y,
                Mathf.Max(1f, rect.Width - ResultChromeTheme.ControlPadX * 2f), rect.Height), label, style);
        }
        finally
        {
            GUI.color = previous;
            GUI.contentColor = previousContent;
        }
        }
    // The chrome's own label style: a copy of the table's face, never the cached instance itself, so the alignment,
    // padding and colour set above belong to that control alone. The one control keeps its own slot, so its 13 px
    // style is not rebuilt on every repaint.
    private static GUIStyle ChromeStyle(int size)
    {
        if (_toggleStyle != null && _toggleStyleSize == size) return _toggleStyle;
        GUIStyle face = HudTable.Style(size);
        if (face == null) return null;
        var style = new GUIStyle(face);
        style.alignment = TextAnchor.MiddleCenter;
        style.padding = new RectOffset(0, 0, 0, 0);
        style.clipping = TextClipping.Clip;
        style.wordWrap = false;
        style.richText = false;
        _toggleStyle = style; _toggleStyleSize = size;
        return style;
    }

    // The header's own label styles: one copy of the table's face per alignment and glyph size, never the cached
    // instance `HudTable.Style` hands back, so the header's left/right alignment can never re-align the table's
    // own text (the same 1.19.41 mutation the controls' styles were split for).
    private static GUIStyle ChromeLabelStyle(int size, bool rightAligned)
    {
        if (rightAligned)
        {
            if (_hintStyle != null && _hintStyleSize == size) return _hintStyle;
        }
        else if (_titleStyle != null && _titleStyleSize == size) return _titleStyle;
        GUIStyle face = HudTable.Style(size);
        if (face == null) return null;
        var style = new GUIStyle(face);
        style.alignment = rightAligned ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft;
        style.padding = new RectOffset(0, 0, 0, 0);
        style.clipping = TextClipping.Clip;
        style.wordWrap = false;
        style.richText = false;
        if (rightAligned) { _hintStyle = style; _hintStyleSize = size; }
        else { _titleStyle = style; _titleStyleSize = size; }
        return style;
    }

    // The rows a plate could not show, said on the plate's own foot: the existing bottom line plus one arrow that
    // points where the rest of the table is.
    //
    // It reads the same `ScrollViewport` the wheel and the plate are built from, so it can never claim an overflow the
    // plate does not have - and a board whose every row is already on the screen draws nothing here at all. The fixed
    // layout keeps its own drawing contract untouched: this is the adaptive board's own foot.
    private static void DrawOverflowHint()
    {
        if (FixedLayoutActive()) return;
        if (_grid == null || !(_scrollPlate > 0f) || _scrollVisible < 1) return;
        ScrollViewport view = ResultsScroll.Viewport(_grid.Rows, ResultsScroll.FrozenRows, _scrollPlate,
            _grid.RowHeight, _scrollFirst);
        if (!view.Overflows) return;
        bool more = !view.AtEnd;      // rows below the plate's foot
        bool above = !view.AtStart;   // rows above the plate's head
        if (!more && !above) return;
        Texture2D white = Texture2D.whiteTexture;
        if (white == null) return;
        ResultChromeMetrics metrics = ChromeMetrics();
        int size = Mathf.Max(9, Mathf.RoundToInt(metrics.HeaderHintFont));
        GUIStyle style = ChromeLabelStyle(size, true);
        if (style == null) return;
        Color previous = GUI.color;
        Color previousContent = GUI.contentColor;
        try
        {
            // The plate's own bottom line: 2 px of the one accent, the same weight as the header's rule.
            GUI.color = ChromeColor(ResultChromeTheme.Accent, 1f);
            GUI.DrawTexture(new Rect(_origin.X, _origin.Y + _scrollPlate - 2f, _grid.Width, 2f), white);
            GUI.contentColor = Color.white;
            // One arrow in the header's own hint ink, at the edge the rest of the table lies beyond.
            style.normal.textColor = ChromeColor(ResultChromeTheme.HeaderHint, 1f);
            float labelY = more ? _origin.Y + _scrollPlate - size - 4f : _origin.Y + 2f;
            GUI.Label(new Rect(_origin.X + _grid.Width - 18f, labelY, 16f, size + 2f), more ? "▼" : "▲", style);
        }
        finally
        {
            GUI.color = previous;
            GUI.contentColor = previousContent;
        }
    }

    private static Color ChromeColor(ResultInk ink, float opacity)
        => new Color(ink.R, ink.G, ink.B, ink.A * (float.IsFinite(opacity) ? Mathf.Clamp01(opacity) : 1f));

    private static string ChromeHex(ResultInk ink)
        => "#" + Mathf.RoundToInt(Mathf.Clamp01(ink.R) * 255f).ToString("X2")
             + Mathf.RoundToInt(Mathf.Clamp01(ink.G) * 255f).ToString("X2")
             + Mathf.RoundToInt(Mathf.Clamp01(ink.B) * 255f).ToString("X2");

    private static string ChromeRect(LayoutRect rect)
        => rect.Width > 0f && rect.Height > 0f
            ? "(" + rect.X.ToString("0") + "," + rect.Y.ToString("0") + "," + rect.Width.ToString("0") + "x" + rect.Height.ToString("0") + ")"
            : "none";

    // Everything that belongs to one page's control: the committed place, the anchor observation and the presses in
    // flight. Called when the results screens are left and when a repaint failed - never for a repaint, a key or a new
    // figure, because none of those is a new page.
    internal static void ForgetChrome()
    {
        _chromeToggleValid = false;
        _chromeTogglePending = false;
        _chromeDrawnFrame = -1;
        // The press probe is per page too: the first few left edges of the *next* result page are worth reporting.
        _chromePressProbes = 0;
        _chromeAnchor = ResultChromeAnchor.None;
        _chromeWhy = string.Empty;
        _chromeBoardRect = default;
        ChromePlaces.Forget();
        ChromeAnchorSettle.Forget();
        ChromeFrame.Reset();
        ToggleGate.Reset();
        // 1.19.44: the pointer belongs to the page it was sampled on. A new page starts with no sample and no previous-down
        // state, and the press is re-derived from the new frame rather than inherited from the old one.
        //
        // 1.19.46 (UI review F1): the *press claim* is deliberately NOT dropped here. This method is the layout's "this
        // page is no longer what I was measuring" line, and a hide is exactly that - `ApplyBoardVisibility(false)` leads to
        // a repaint whose `ResetPlacement` drops `_page`. Dropping the claim with the layout is what let the very press
        // that hid the board run on into the page's own dispatch. The claim is a press-lifetime fact and keeps its own
        // ...and the frame's pointer sample goes with it: a page with no sample left must sample again on the next ask,
        // even if that ask lands in the same frame (L5).
        PointerFrame.Forget();
        // ends: a release, a page that is not the claimed one, an inactive page, lost focus, or `MaxSeconds`.
        _pointer = ResultPointerSample.None;
        _pointerSession = ResultPointerSession.None;
        _pointerSessionValid = false;
        _pressSource = ResultPressSource.None;
        _pressDown = false;
        _pressFrame = -1;
        _pressFrameValue = ResultPressFrame.Unavailable;
        // 1.19.46 (UI review F1 close-out): this frame's *decision* goes with the layout it was made against, so the
        // next evaluation reads the new rectangles and the new page instead of reusing one measured before the hide.
        // The action ledger (the frame already answered) is deliberately NOT dropped: it is what keeps the press that
        // caused this invalidation from being answered twice, and a hide is not the end of the press that performed it.
        _dispatchFrame = -1;
        _dispatchValue = ResultPressDispatch.None;
        _pointerNote = string.Empty;
        _pointerLogged = string.Empty;
    }

    // The controls' input, polled once per frame from the panel's own `Update`.
    //
    // The pointer and the three button facts come from the one source the game's own menus and the reference mod
    // use: `Input.mousePosition` and `Input.GetMouseButton*`, converted once into the window space every measured
    // rectangle lives in (`ChromePointer`; `TryScreenRect` does the same conversion, so hit and draw are one space).
    // Nothing is captured, wrapped or suppressed, and the page underneath keeps every click that is not on one of
    // these two rectangles.
    //
    // A press counts when the last repaint that drew the control is recent (`ResultChromeInputPolicy.Fresh`, six
    // frames) *and* the window has not changed since. That is the whole of the "no stale hot rectangle" rule, with
    // the one correction the recorded runs needed: "recent" is a bound, not exactly one frame. A repaint that does
    // not happen - a paused window, a frame the renderer skipped - used to close the only window in which a click
    // could be answered, and the refusal went into the log nowhere.
    //
    // The decision, the request and the presentation are three different lines: this method logs the bounded click
    // decision (`settlement board click`), `PressChromeControl` logs the request (`settlement board ... pressed`),
    // and the repaint that follows logs what was really painted (`F8 forced frame`) or the fact that refused it.
    internal static void PollChromeInput()
    {
        try
        {
            ResultPressDispatch frame = DispatchPressFrame();
            if (!frame.Live) { ForgetChrome(); return; }
            ReportPointerState(frame.Session, frame.Press, frame.NewSession);
            // The close slots of these two report-only helpers are passed as "not drawn": this mod paints one control
            // on the settlement page (1.19.45), so neither line can ever describe a second rectangle.
            ReportChromeClick(frame.ToggleInside, false, frame.Down, frame.Held, frame.Up, frame.Fresh,
                frame.Focused, frame.SameWindow, frame.Toggle, false, frame.PointerValid, frame.PointerX, frame.PointerY);
            ReportChromePressProbe(frame.ToggleInside, false, frame.Down, frame.Fresh, frame.Focused,
                frame.SameWindow, frame.PointerValid, frame.PointerX, frame.PointerY);
            if (!frame.Mine) return;
            // 1.19.46 (UI review F1 close-out): the press is this mod's - and the page's own dispatch may have asked the
            // same question first, this very frame. The action is therefore booked here, once per frame, and never by
            // the decision itself: `_pressAnswer` is the action layer's own dedupe, and it is deliberately not dropped
            // by `ForgetChrome` - the hide this press performs must not let the same click be answered a second time.
            if (_pressAnswer.Covers(frame.Frame)) return;
            _pressAnswer = _pressAnswer.Take(frame.Frame);
            PressChromeControl("toggle");
        }
        catch (Exception error)
        {
            ForgetChrome();
            if (_loggedChromeInput) return;
            _loggedChromeInput = true;
            Plugin.Logger?.LogWarning("[DIAG] the settlement board's controls could not read the pointer and are not clickable this frame: " + error.Message);
        }
    }

    // The frame's one decision, made by whoever asks first.
    //
    // 1.19.46 (UI review F1 close-out): two callers ask inside one frame - this mod's `Update` (`PollChromeInput`) and the
    // page's own press dispatch (`SettlementPressPatch`, through `ShouldSuppressNativePress`) - and they may run in
    // either order. Both get the same answer, and both get it from the *same* evaluation: one press sample, one pointer
    // sample, one pair of gate edges. That is what makes the prefix-first order safe, which is the order the recorded run
    // used and the one the previous round could not answer.
    private static ResultPressDispatch DispatchPressFrame()
    {
        int frame = Time.frameCount;
        if (frame == _dispatchFrame) return _dispatchValue;
        ResultPressDispatch dispatch = EvaluatePressFrame(frame);
        _dispatchFrame = frame;
        _dispatchValue = dispatch;
        return dispatch;
    }

    // The evaluation itself. It may *claim* the press (`_pressOwned.Arm`) and it never answers it: the action belongs to
    // `PollChromeInput` alone, so a frame in which the page's dispatch asked first still produces exactly one action -
    // the one this mod's own poll issues when it reads this same decision a moment later.
    private static ResultPressDispatch EvaluatePressFrame(int frame)
    {
        if (!ResultsPage()) return ResultPressDispatch.None;
        bool sameWindow = _chromeScreenWidth == Screen.width && _chromeScreenHeight == Screen.height;
        bool fresh = ResultChromeInputPolicy.Fresh(_chromeDrawnFrame, frame) && sameWindow;
        bool focused = true;
        try { focused = Application.isFocused; } catch { focused = true; }
        bool pointerValid = ChromePointer(out float pointerX, out float pointerY);
        // 1.19.44: one *pointer session* per (page, source, focus, window). A change of any of them is a different
        // pointer, so the gates and the previous-down state are reset instead of carrying the old gesture across -
        // that is what keeps a source switch (page cursor <-> labelled physical fallback) from inventing a down or
        // an up edge nobody performed.
        IntPtr pagePointer = ChromePagePointer();
        var session = new ResultPointerSession(pagePointer, _pointer.Source, focused, Screen.width, Screen.height);
        ResultPressFrame press = SamplePress();
        bool newSession = !_pointerSessionValid || !session.Equals(_pointerSession);
        if (newSession)
        {
            _pointerSession = session; _pointerSessionValid = true;
            ToggleGate.Reset();
            _pressOwned.Clear();
            // The first frame of a session has no edges at all: this sample describes a pointer the previous frame
            // never saw, so whatever it reads now is a state, not a transition.
            if (press.Available) press = ResultPressFrame.Of(true, press.Down, press.Down, press.Source);
        }
        // 1.19.46: the claim is confirmed only by a frame that really read the button *down*, and a confirmation never
        // renews it: its bound is the press's own (`ResultPressOwnership.MaxSeconds`), not the last frame that saw it.
        else _pressOwned.Observe(press.Available && press.Down);
        bool down = press.Pressed, held = press.Down, up = press.Released;
        bool toggleInside = pointerValid && press.Available && _chromeToggleValid
            && ResultChromeLayout.Inside(_chromeToggleRect, pointerX, pointerY);
        // 1.19.48: the *action* asks the same question the claim already asked. `PointerOverNative` is one source (the
        // pointer this frame sampled and the rectangles the last repaint measured), and a press that lands on one of the
        // page's own controls is the page's press - immediately, on the first frame that control is there, and whether or
        // not the pause above has had time to withhold the button. Without this the toggle could still act on a click that
        // began on a native control whenever the conflict had not yet survived its threshold: the flicker fix and the input
        // rule have to be the same rule, or the button takes the press the page was about to answer.
        bool overNative = PointerOverNative();
        bool toggle = ToggleGate.Fire(focused, press.Available && fresh && _chromeToggleValid && !overNative, toggleInside, down, held, up);
        // `closeInside`/`close` are handed over as false: the second control is gone (1.19.45), and the dispatch's own
        // `Mine` therefore answers for the toggle alone.
        var dispatch = new ResultPressDispatch(frame, press, session, newSession, pointerValid, pointerX, pointerY,
            fresh, focused, sameWindow, toggleInside, false, toggle, false);
        // The claim is made *at the decision*, not at the action: the press that starts on this mod's control may be asked
        // about by the page's own dispatch before this mod's poll has run, and only a claim made now can stop that
        // dispatch from acting on the same click. It carries the resident page's own pointer (which survives `_page`
        // being dropped by the hide this very press performs) and it is never made over a rectangle the page itself is
        // drawing: a press that began on one of the page's own controls is the page's, whatever else is true.
        if (dispatch.Mine && !overNative) _pressOwned.Arm(pagePointer, Time.realtimeSinceStartup);
        return dispatch;
    }

    // Where the sample came from, once per change and once per refusal, bounded - so a live log can say whether the page
    // answered, which fallback ran and why. Nothing here claims a hit: that is the click line's business.
    private static void ReportPointerState(ResultPointerSession session, ResultPressFrame press, bool newSession)
    {
        // Layout, focus and cursor-source changes can recur every frame on one result page.
        uint generation = CombatSync.Generation;
        if (_pointerReportGeneration != generation)
        {
            _pointerReportGeneration = generation;
            _pointerFailures = 0;
        }
        if (_pointerFailures >= PointerReportLimit) return;
        string note = _pointer.Describe() + ", press=" + ResultPressFrame.Name(press.Available ? press.Source : _pressSource)
            + (string.IsNullOrEmpty(_pointerNote) ? string.Empty : ", sampled=" + _pointerNote)
            + (press.Available ? (press.Down ? " (down)" : " (up)") : " (unreadable)");
        bool refusal = !_pointer.Valid || !press.Available;
        bool changed = newSession || note != _pointerLogged;
        _pointerLogged = note;
        if (!changed || (!refusal && !newSession)) return;
        _pointerFailures++;
        Plugin.Logger?.LogInfo("[DIAG] settlement pointer: " + note + ", " + session.Describe()
            + "; the page's own cursor and the page's own main button are asked first, the physical mouse is only the"
            + " labelled fallback, and this line is written when the answer changes or is refused ("
            + _pointerFailures + "/" + PointerReportLimit + " per expedition).");
    }

    private static string _pointerLogged = string.Empty;

    // The bounded click decision, one line per gesture edge and never one per frame.
    //
    // It is written *before* the request, and it says only what this frame's input decided: the rectangle the
    // pointer was tested against, whether that rectangle was recent enough to answer, whether the gate was armed,
    // and the gate's own word for the outcome. It never claims the board appeared - that is the repaint's line.
    // A click that is nowhere near either control is the page's own and is not logged at all.
    private static void ReportChromeClick(bool toggleInside, bool closeInside, bool down, bool held, bool up,
        bool fresh, bool focused, bool sameWindow, bool toggle, bool close, bool pointerValid, float pointerX, float pointerY)
    {
        if (!down && !up) return;
        bool inside = toggleInside || closeInside;
        bool fired = toggle || close;
        if (!inside && !fired) return;
        string control = toggleInside ? "toggle" : "none";
        string decision = fired || inside ? ToggleGate.LastWhy : "page";
        string pointer = pointerValid ? "(" + pointerX.ToString("0") + "," + pointerY.ToString("0") + ")" : "unreadable";
        Plugin.Logger?.LogInfo($"[DIAG] settlement board click: control={control}, pointer={pointer}, down={down}, held={held}, up={up}, "
            + $"frameAge={(fresh ? (Time.frameCount - _chromeDrawnFrame).ToString() : "stale")}, windowSame={sameWindow}, focused={focused}, "
            + $"toggleRect={ChromeRect(_chromeToggleRect)}{(_chromeToggleValid ? string.Empty : "(not drawn)")}, "
            + $"decision={decision}{(fired ? " - the request is issued next, and the repaint answers it" : string.Empty)}; "
            + "this is the bounded click decision, never a claim that a board was painted, and there is one control on this page.");
    }

    // How many left-button edges of one result page are written down at all, and how many have been. The recorded
    // failure page drew this board's control and its log carried not one `settlement board click` line, and that alone
    // cannot tell "the pointer was never read" from "the rectangle was not live on that frame" from "the player pressed
    // somewhere else". The first few presses of a page are therefore written once each, inside and outside the rectangle
    // alike, with the three facts that separate those cases; after them the probe is silent and only a real gesture on
    // the control (or on one that was not drawn) still logs. Bounded per page by construction (`ForgetChrome` resets
    // the count), so a repaint can never turn it into a flood.
    private const int PressProbeReports = 4;
    private static int _chromePressProbes;

    private static void ReportChromePressProbe(bool toggleInside, bool closeInside, bool down, bool fresh, bool focused,
        bool sameWindow, bool pointerValid, float pointerX, float pointerY)
    {
        if (!down || toggleInside || closeInside) return;
        if (_chromePressProbes >= PressProbeReports) return;
        _chromePressProbes++;
        string pointer = pointerValid ? "(" + pointerX.ToString("0") + "," + pointerY.ToString("0") + ")" : "unreadable";
        Plugin.Logger?.LogInfo($"[DIAG] settlement board press probe {_chromePressProbes}/{PressProbeReports}: pointer={pointer}, "
            + $"toggleRect={ChromeRect(_chromeToggleRect)}{(_chromeToggleValid ? string.Empty : "(not drawn)")}, "
            + $"frameAge={(fresh ? (Time.frameCount - _chromeDrawnFrame).ToString() : "stale")}, windowSame={sameWindow}, focused={focused}; "
            + "the left button came down outside this board's one control, so the click is the page's and nothing here "
            + $"has taken it - the line says only whether the pointer and that rectangle were live on that frame, and it is "
            + $"written for the first {PressProbeReports} presses of a result page and never again.");
    }

    // One press on the board's toggle, answered from the intent of the label it was drawn with - never from the last
    // painted frame and never from another button's intent (review #1).
    //
    //   toggle "隐藏看板"  hide. It was drawn while the board was up, so that is what it does.
    //   toggle "显示看板"  show. It was drawn while the board was down - which is also the page that has no board, and
    //                    the page whose entry the mod owes the player.
    //
    // There is no second button to consider any more (1.19.45): the close is gone and F8 reaches this same answer
    // through the same `ApplyBoardVisibility`, so the key and the control can never disagree.
    private static void PressChromeControl(string control)
    {
        bool show = !_chromeToggleHides;
        bool shown = ApplyBoardVisibility(show);
        ToggleGate.Reset();
        _chromeToggleValid = false;
        _chromeDrawnFrame = -1;
        Plugin.Logger?.LogInfo($"[DIAG] settlement board toggle pressed: board={(shown ? "shown" : "hidden")}, "
            + $"label={(_chromeToggleHides ? "hide" : "show")}; role={ResultPageRolePolicy.Name(Role())}, "
            + $"anchor={_chromeAnchor}, screen={Screen.width}x{Screen.height}; the press takes the intent of the label it was drawn with "
            + "(a 显示看板 shows, a 隐藏看板 hides), F8 answers through the same switch, and the vanilla result page is restored on the frame the board is hidden.");
    }

    internal static void Draw()
    {
        // Nothing is booked until this repaint books it: a frame that throws before `DrawChrome` must not paint the
        // previous frame's band or toggle. `FinishChromeDraw` runs in the `finally`, so the page's own surface is
        // answered exactly once per repaint, on every path including the refused ones (review #1).
        _chromeToggleValid = false;
        // 1.19.43: the booking goes with the toggle, so a repaint that never reaches `DrawChrome` leaves nothing booked
        // and nothing clickable.
        _chromeHeaderPending = false; _chromeTogglePending = false;
        _chromeDrawnFrame = -1;
        try { DrawBoard(); }
        finally { FinishChromeDraw(); }
    }

    private static void DrawBoard()
    {
        // Every repaint starts from "no board to aim at": the rectangle below is only ever re-earned by a frame that
        // really draws one. Nothing is carried over from the last frame, so a frame whose board was refused - no grid
        // yet, a strip that would not hold it - leaves the wheel to the game instead of consuming it over a rectangle
        // that is not on the screen. The same-frame gesture lock survives this; see `InvalidateHitForRepaint`.
        InvalidateHitForRepaint();
        // Nothing on this page is showing a board until this repaint really draws one: this is the fact the
        // press is answered from, so it is written here - before every early return below - and only the draw at
        // the end of this method puts a frame number back into it.
        //
        // The answer is kept before it is cleared, because the one control below needs it: it is drawn on this page
        // whether or not a board is up, and there is nothing else on the page for this mod to draw.
        bool boardUp = BoardOnScreen;
        _boardDrawnFrame = -1;
        // The settlement page's own controls are drawn before every gate and every early return: the toggle has to
        // be there on a page whose board is hidden (it is the way back), and a control that is not produced this
        // frame leaves no hit box behind for the next click.
        DrawChrome(boardUp);
        // F8 hides this mod's overlay on a result page exactly as it hides the table in a level, and the
        // influence-board switch holds it back on its own. The vanilla blocks go back on their own page, so
        // the screen is never left half-hidden.
        //
        // `Forced` is the one exception and it is the point of the fail-open rule: a player who pressed F8 on this page
        // asked for the board, so no switch and no stale toggle may hold it back here. Everything downstream - the
        // placement, the strip, the fit - is the same code, and the fail-open path is what answers it when the page's
        // own anchors never became provable.
        if (!(ResultVisibility.Shows(
                Plugin.ShowResults == null || Plugin.ShowResults.Value,
                true,
                Plugin.ShowImpactBoard == null || Plugin.ShowImpactBoard.Value,
                PanelBehaviour.ToggleState) || Forced))
        {
            // The one hide: the game's own page back, the board's placement dropped, and the next show starts from a
            // budget the page has not spent (review #5: `_strictMisses` used to survive the hide, so the next show began
            // its wait already 60 frames in).
            if (_page != null) { RestoreHidden(); ResetPlacement(); }
            _strictMisses = 0; _fallbackMisses = 0; _fallbackFloorScale = 1f;
            _fallbackPlaced = false; _fallbackOpacity = 1f; _forcedOutcomePending = false;
            // A hidden board is not a wheel target: F8 off means the next notch belongs to the game again.
            ClearHit();
            return;
        }

        // Nothing is touched until the board is really about to be drawn: the vanilla blocks belong to the
        // page and are only put away while a board stands in for them. Hiding them for a page whose board
        // never appears leaves the screen half-empty with nothing drawn over the gap.
        if (_grid == null || _content == null || _page == null || !_page.gameObject.activeInHierarchy)
        {
            // Nothing was placed for this page (or the page has gone away), so there is nothing to draw and nothing
            // to touch. Said once per page rather than silently: a screen that shows the vanilla page because no
            // placement was ever made, and a screen that shows it because the fit refused, look identical otherwise.
            // 1.19.38: `pagePresent=` and `pageActive=` are printed here because `no-live-page` and `no-placement`
            // were the only two things a reader could see, and the recorded client run's five lines of this shape
            // (`reason=no-live-page, role=client`) are a page object that was not *active* in that frame - not a
            // geometry refusal and not a missing placement. One field each, no change to what is drawn.
            // 1.19.59: a frame in which there is no board *and no page to draw one on* is not a transient deferral - the
            // page object is gone, so nothing will ever be drawn for it again. The blocks this mod put away belong to that
            // page and go back with it (the same rule as leaving the screens), instead of waiting for a refusal count that
            // can no longer arrive. A page that is merely still being measured reports `no-placement` and keeps the hide,
            // which is the transient case `NoteVanillaRefusal` bounds with the page's own refusals.
            if (_page == null && Hidden.Count > 0) RestoreHidden();
            ReportHeldBack("draw", _page == null || !PageActive(_page) ? "no-live-page" : "no-placement",
                "grid=" + (_grid != null ? _grid.Width.ToString("0") + "x" + _grid.Height.ToString("0") : "none")
                + ",content=" + (_content != null) + ",boardFloor=" + _boardFloor.ToString("0")
                + ",pagePresent=" + (_page != null) + ",pageActive=" + PageActive(_page));
            return;
        }

        if (_resolutionWidth != 0)
        {
            if (_resolutionWidth == Screen.width && _resolutionHeight == Screen.height) PaintResolutionBoard();
            return;
        }
        // 固定模式：位置和字号来自玩家的配置，不来自页面条带，所以这里既不重排也不缩放，只重新验证控件安全。
        if (_fixedPlaced)
        {
            FixedLayoutDraw();
            return;
        }
        float opacity = 1f;
        if (!_succeeded && _content.Kinds != null)
        {
            if (_holdValid)
            {
                // The board is up and it stays up. Its strip was measured when it arrived, and re-measuring the
                // page's own message and buttons on every frame is exactly what used to take it away again:
                // those elements animate, a button's own bounds move by a pixel or two while it is hovered, and
                // a strip a fraction of a pixel shorter than the board refused it - which is why the board only
                // came back while the player happened to hover the page. The one edge that still has to hold is
                // the button the player has to press, so that is the only measurement made here, and the board
                // keeps its place unless it would really reach it.
                // A fallback board keeps the opacity its own band earned: a board drawn over the page because nothing
                // clear was left is faint, one standing in a free band is not.
                _holdOpacity = _fallbackPlaced ? _fallbackOpacity : 1f; // Once shown, only the explicit visibility switches can hide this page's board.
                opacity = _holdOpacity;
                bool buttonMeasured = LobbyButtonTop(_page, out float buttonTop);
                // Every standing board is bounded by its own band on both edges, strict or fail-open: the lift
                // below may never carry it above the band's top - the failure title's own layout. 1.19.22: a board
                // that cannot be held inside both edges is no longer deferred - `TryHold` refusing meant the button
                // reading moved up, the placement was thrown away, and the next frame re-placed and refused again,
                // which is exactly the loop that made F8 look dead on this page. The board keeps the rectangle it
                // already has and is drawn through the same opacity path the draw already computes: faint when it now
                // crosses the button's clearance wall or its band's foot, so a standing board never disappears because
                // a control moved. (A fail-open board is re-fitted/faded by `RefreshFallbackSafety` right after; a
                // strict board is still judged by `BoardStillSafe`, whose strict refusal contracts are untouched.)
                float topBound = _holdStrip.Bottom > _holdStrip.Top ? _holdStrip.Top : float.NaN;
                float bandFoot = _holdStrip.Bottom > _holdStrip.Top ? _holdStrip.Bottom : float.NaN;
                if (!ResultBoardHold.TryHold(new BoardStrip(_holdBoard.Y, _holdBoard.Bottom, buttonMeasured, buttonTop),
                        _holdBoard, Screen.height, out LayoutRect held, topBound))
                {
                    float wall = buttonMeasured && float.IsFinite(buttonTop)
                        ? Math.Min(buttonTop - ResultBoardHold.ButtonClearance, Screen.height - 8f) : float.NaN;
                    bool crossesWall = float.IsFinite(wall) && _holdBoard.Bottom > wall + 0.5f;
                    bool crossesBand = float.IsFinite(bandFoot) && _holdBoard.Bottom > bandFoot + 0.5f;
                    if (crossesWall || crossesBand)
                        _holdOpacity = Math.Min(_fallbackPlaced ? _fallbackOpacity : 1f, ResultFallbackLayout.OverlapOpacity);
                    if (ResultReportPolicy.OncePerPage(HeadingReports, "failure-hold-kept", Forced, _forcedOutcomePending))
                        Plugin.Logger?.LogInfo($"[DIAG] failure board kept where it stands: TryHold refused to lift it into its band, so the board keeps the rectangle it already has and draws {(crossesWall || crossesBand ? "faint" : "as it stands")} instead of disappearing - boardY={_holdBoard.Y:0},boardBottom={_holdBoard.Bottom:0},bandTop={(float.IsFinite(topBound) ? topBound.ToString("0") : "unmeasurable")},bandBottom={(float.IsFinite(bandFoot) ? bandFoot.ToString("0") : "unmeasurable")},buttonMeasured={buttonMeasured},buttonTop={(float.IsFinite(buttonTop) ? buttonTop.ToString("0") : "unmeasurable")},wall={(float.IsFinite(wall) ? wall.ToString("0") : "unmeasurable")},need={_holdBoard.Height:0}px,have={(float.IsFinite(bandFoot) && float.IsFinite(topBound) ? (bandFoot - topBound).ToString("0") : "unmeasurable")}px{(Forced ? ", forcedByF8=true" : string.Empty)}; nothing here takes a standing board away from a control that moved.");
                    opacity = _holdOpacity;
                }
                else if (_fallbackPlaced && buttonMeasured && float.IsFinite(buttonTop)
                    && held.Bottom > buttonTop - ResultBoardHold.ButtonClearance + 0.5f)
                    _holdOpacity = Math.Min(_holdOpacity, ResultFallbackLayout.OverlapOpacity);
                opacity = _holdOpacity;
            }
            else
            {
                // A first placement still has to fit the strip the page measured: below the page's own message
                // and above the buttons the player has to press. A board that cannot fit is not drawn rather
                // than drawn over a button.
                if (!FailureStrip(_page, out float top, out float bottom, out opacity, out bool buttonMeasured, out float buttonTop, out float buttonHeight, out _, out _, out _, out _, out _, out _)) return;
                // The board's header is part of the board: the total height the strip has to hold is the table
                // plus the header the close stands in, and the table's own origin is that much lower.
                float header = ChromeHeader();
                // The plate is the rows this strip really holds, not the whole table: a board taller than the page
                // is scrolled inside its plate, so the button underneath it is never covered and nothing is drawn
                // outside the band.
                float plateHeight = PlateFor(_grid, bottom - top, header, out int scrollVisible, out int scrollFirst);
                float total = plateHeight + header;
                // The plate stops where this page's own wall left its band: a host failure strip is already the action
                // button's top minus this page's clearance (`ResultsBounds.TryResolve`), so it is never extended outward
                // the way an animated footer's strip is - the gate that judges it (`ResultHostFailureBand.Allows`) is
                // half a pixel wide and the plate now stops on the band's own foot (`ResultBoardHold.PlacementSlack`).
                if (!ResultBoardHold.TryPlace(Screen.width, Screen.height, new BoardStrip(top, bottom, buttonMeasured, buttonTop, buttonHeight), _grid.Width, total,
                    Plugin.ResultsOffsetX.Value, out LayoutRect fitted, ResultBoardHold.PlacementSlack(!_succeeded && !GuestClient(), BoardSlack), bottomAligned: true))
                {
                    if (ResultReportPolicy.OncePerPage(HeadingReports, "fail-no-room", Forced, _forcedOutcomePending))
                        Plugin.Logger?.LogInfo($"[DIAG] failure board held back: strip=({top:0}..{bottom:0}), have={(bottom - top):0}px cannot hold a {_grid.Width:0}x{_grid.Height:0} (need={total:0}px with the {header:0}px header) board, so the vanilla screen is left alone rather than a button covered.{(Forced ? " forcedByF8=true - this press is answered by a refusal line, never silence." : string.Empty)}");
                    // The forced press spends its answer here, exactly as ReportHeldBack does: once this refusal has
                    // printed, the next frame cannot print a second, false "board drawn" answer for the same press.
                    if (Forced) _forcedOutcomePending = false;
                    return;
                }
                _origin = new LayoutRect(fitted.X, fitted.Y + header, fitted.Width, plateHeight);
                _scrollVisible = scrollVisible; _scrollFirst = scrollFirst; _scrollPlate = plateHeight;
                _holdValid = true;
                // The hold belongs to this page - the fact `Refresh`'s new-page block and `Fit`'s "the board is
                // already up on this page" branch both read. Recorded where the place is, never pre-set.
                _holdPage = _page != null ? _page.Pointer : IntPtr.Zero;
                _holdBoard = fitted;
            }
        }
        // The page switches the blocks the board replaces back on as its own animation plays, so they are
        // put away again on every frame the board is up, not once per poll. This happens after every gate
        // that can still hold the board back, so a frame that draws nothing also hides nothing: the vanilla
        // blocks only ever make way for a board that is really about to be drawn. It is guarded on its own
        // so a page that has already gone away can never take the board down with it.
        //
        // 1.19.46 additions, both answers to the recorded failure pages:
        //
        //   the settle     a board is painted only once the place it was measured into has been validated by
        //                  `BoardStillSafe` on two consecutive repaints. The recorded page placed a board, hid the
        //                  vanilla blocks and drew it, and the *next* repaint deferred it (`count=1,hidden=4`) and
        //                  restored those blocks - one frame of board between two frames of the game's own page.
        //                  Waiting one repaint costs 16 ms and removes the flash entirely: the frame that would be
        //                  deferred never painted anything, so the page was never half-hidden.
        //   the band       the plate that is about to be painted is checked against the band it was measured into
        //                  (`ResultChromeLayout.PlateInsideBand`). The recorded re-fit claimed a 720 px plate in a
        //                  651 px band; that is a board drawn outside its own contract, and it is deferred now with
        //                  the two real numbers in the line rather than painted and withdrawn a frame later.
        if (!_fallbackPlaced)
        {
            if (!BoardStillSafe())
            {
                DeferBoardForInteraction();
                return;
            }
        }
        else
        {
            // Recheck the fallback plate on every repaint; a host failure may not reuse an unproven strip.
            RefreshFallbackSafety();
            opacity = _fallbackOpacity;
        }
        if (!_succeeded && !GuestClient())
        {
            float actionTop = HostFailureButtonTop(_page);
            if (!_holdValid || !ResultHostFailureBand.Allows(_holdBoard, actionTop, Screen.height))
            {
                ReportHeldBack("draw", !_holdValid ? "host-failure-strip-unproven" : "host-failure-button-wall",
                    $"plateBottom={_holdBoard.Bottom:0}, actionTop={WallText(actionTop)}");
                DeferBoardForInteraction();
                RestoreHidden();
                return;
            }
        }

        // The whole board, header included, inside the band it was measured into - asked of the rectangle that is
        // really on the screen, every repaint, before anything is painted or any vanilla block is touched.
        if (!ResultChromeLayout.PlateInsideBand(_holdBoard, _holdStrip.Top, _holdStrip.Bottom, Screen.height, BoardSlack))
        {
            if (ResultReportPolicy.OncePerPage(HeadingReports, "board-outside-band", Forced, _forcedOutcomePending))
                Plugin.Logger?.LogInfo($"[DIAG] result board deferred: the plate about to be painted does not sit inside the band it was measured into - "
                    + $"plate=({_holdBoard.X:0},{_holdBoard.Y:0},{_holdBoard.Width:0}x{_holdBoard.Height:0}), plateBottom={_holdBoard.Bottom:0}, "
                    + $"band=({(float.IsFinite(_holdStrip.Top) ? _holdStrip.Top.ToString("0") : "unmeasurable")}..{(float.IsFinite(_holdStrip.Bottom) ? _holdStrip.Bottom.ToString("0") : "unmeasurable")}), "
                    + $"bandSource={_holdStrip.FooterSource}, screen={Screen.width}x{Screen.height}, role={ResultPageRolePolicy.Name(Role())}"
                    + (Forced ? ", forcedByF8=true - this press is answered by this deferral line, never silence." : string.Empty)
                    + "; the vanilla result page is kept exactly as the game drew it and the band is measured again.");
            DeferBoardForInteraction();
            return;
        }
        // A board that has not been painted yet waits one more repaint; a board that is already painted keeps being
        // painted - this gate only ever delays the first paint of a placement, it never takes a board away.
        bool painted = _boardDrawnFrame >= 0 && Time.frameCount - _boardDrawnFrame <= 2;
        if (!painted && !BoardSettled(_holdBoard))
        {
            if (Forced && _forcedOutcomePending && HeadingReports.Add("board-settle-frame"))
                Plugin.Logger?.LogInfo("[DIAG] result board settling: the placed board is validated on this repaint and is painted on the next one "
                    + "if the band still stands, so the vanilla blocks are not put away for a board that could be deferred a frame later; "
                    + $"plate=({_holdBoard.X:0},{_holdBoard.Y:0},{_holdBoard.Width:0}x{_holdBoard.Height:0}), band=({_holdStrip.Top:0}..{_holdStrip.Bottom:0}); "
                    + "forcedByF8=true - this press is answered by this line and by the paint that follows it.");
            return;
        }
        // The success board keeps the position it was measured at. Re-measuring it on every frame made the
        // table resize while the page was still animating, and a table that has settled must not move: it is
        // placed once, and placed again only when the results screen itself changes.
        HudTable.Draw(_grid, _origin.X, _origin.Y, _content, opacity, _scrollFirst, _scrollVisible, _scrollPlate);
        // ---- the overflow is visible, not silent --------------------------------------------------------
        //
        // A board the fit could not hold - a genuinely short strip, or a player's own multiplier - is scrolled, and
        // until this line existed nothing on the page said so: the recorded failure page drew thirty rows at 16 px and
        // showed twenty-four of them without a word (log 3357/3358). The hint is drawn only on a screen that really
        // overflows, from the same viewport the wheel and the plate are built from; a board that fits draws nothing.
        DrawOverflowHint();
        // A board really went on the page this repaint. That is the whole of "the page is showing a board", and it is
        // what a press of F8 will hide from now on.
        _boardDrawnFrame = Time.frameCount;
        // 原版块只在这帧的表格**真的画出来之后**才让位，而且只在这一处（固定模式在它自己的绘制之后，同一道理）：
        // 任何一道门退出去都不碰原版页，所以"被挪走、板没画"的空洞不存在（review #3）。
        // The game's own blocks make way *here*, on the frame the table above really drew, and nowhere else.
        try
        {
            // 1.19.59: a board that is painted *does* stand in for the page's own blocks - an early one only at the drawing
            // (it may not move or deactivate anything, because its presses and wheel are still the page's), and a mature one
            // outright. 1.19.55 put nothing away for an early board, and the recorded success page never matured, so the red
            // crystal and the four prisoner cards stayed on the screen under a table that was already drawn.
            if (ResultEarlyBoardPolicy.HidesVanilla(_earlyBoard, true)
                && (!FixedLayoutActive() || _fixedPlan.HidesVanilla))
            {
                // 1.19.57: the page is asked of the page objects, not of the cache this repaint may already have cleared.
                Component vanillaPage = LivePage(out bool vanillaSucceeded);
                if (vanillaPage != null) ApplyVanillaBlocks(vanillaPage, vanillaSucceeded, _earlyBoard);
            }
        }
        catch (Exception error)
        {
            // The page went away while its own blocks were being put away: the table is already on the screen, so nothing is
            // taken back - the board simply stops touching this page and the next repaint attaches to whatever screen is up.
            _page = null;
            if (!_loggedMeasure)
            {
                _loggedMeasure = true;
                Plugin.Logger?.LogWarning("[DIAG] the result page went away while its blocks were being put away, so the board stops touching it: " + error.Message);
            }
        }
        // 1.19.26: the forced press is answered *after* the draw, never before it. The line says what this build did - it
        // issued the board's draw calls on this frame - and not what the page ended up looking like: there is no pixel
        // readback anywhere in this pipeline, and a "board drawn" line printed ahead of the draw was a claim about pixels
        // nobody had painted yet.
        ReportForcedDraw(_origin);
        // The rectangle that is really on the screen now, which is what the wheel is aimed at. Written here, on the
        // draw, so it describes the board the player is looking at rather than the cache a rebuild keeps replacing.
        // It is the plate the player sees, header included, measured from the placement this very repaint drew:
        // `_chromeBoardRect` was taken before this frame's placement (the controls are drawn ahead of every gate),
        // so reading it here would aim the wheel one zoom behind the plate that is on the screen - the notch that
        // grew the board would fall outside the rectangle it grew from and cut one wheel gesture in half.
        _hitRect = ResultChromeLayout.BoardOf(new LayoutRect(_origin.X, _origin.Y, _grid.Width, _scrollPlate > 0f ? _scrollPlate : _grid.Height), ChromeMetrics());
        _hitSucceeded = _succeeded;
        // 1.19.55: an early board is a picture, not a target. The wheel stays the page's and a press belongs to whatever the
        // game drew under the pointer, so the rectangle is measured (the diagnostics and the next frame's column use it) but
        // it is not opened as a hit surface until the page's own boundary is proven and the mature board stands here.
        _hitValid = ResultEarlyBoardPolicy.Interactive(_earlyBoard);
        // The wheel is a separate question from the press (1.19.59): a board that is really painted may be scrolled, early
        // or not, because a table the player cannot reach the last row of is the same defect at 30 rows as at 8.
        _scrollValid = true;

        if (!_succeeded && HeadingReports.Add("failure-board-placed"))
            Plugin.Logger?.LogInfo($"[DIAG] failure board placed: board=({_origin.X:0},{_origin.Y:0},{_grid.Width:0}x{_grid.Height:0}), failureTitleEdge={(float.IsFinite(Title.Edge) ? Title.Edge.ToString("0") : float.IsFinite(FailureLayoutTitle.Edge) ? FailureLayoutTitle.Edge.ToString("0") : "unseen")}, band=({_holdStrip.Top:0}..{_holdStrip.Bottom:0}), bandSource={_holdStrip.FooterSource}; this page's native title layout and interaction lower wall were measured before drawing.");
    }

    // 客机结算页没有「返回大厅」按钮，只有一行等待状态文字；本模组的落板不依赖任何原生控件。
    //
    // The role is read once per decision and named in every branch that depends on it, so a host is never answered
    // with a client's rule (and never told about a proof it can never have). An unreadable native flag is a host: that
    // is the path that measures the page and refuses, not the one that hands out a fixed safe area.
    // 1.19.60 (review F1): the same read also records whether it *answered*. `Unreadable` is deliberately the host's
    // measuring path, so the role alone cannot tell a proven host from a page whose flag threw - and the host's
    // early-display privilege needs the positive answer. Recorded here, from this one read, so the native flag is still
    // read in exactly two places in this file (this helper and the scan's own coverage edge).
    private static bool _roleReadable = true;

    private static ResultPageRole Role()
    {
        try { _roleReadable = true; return ResultPageRolePolicy.Of(SNet.IsMaster); }
        catch { _roleReadable = false; return ResultPageRolePolicy.Unreadable; }
    }

    // A proven host, in the few words the early-board privilege needs.
    private static bool HostProven() => _roleReadable && Role() == ResultPageRole.Host;

    private static bool GuestClient() => ResultPageRolePolicy.IsClient(Role());

    private static bool PageActive(Component page)
    {
        if (page == null) return false;
        try { return page.gameObject.activeInHierarchy; } catch { return false; }
    }

    // The vertical offset the player configured for the settlement board, sanitised once for both roles. A value that is
    // not a number - or below zero, which would move the board over the section it hangs from - reads as no offset;
    // `Mathf.Max(0f, NaN)` is NaN, and a NaN strip is a board that silently never appears, which is the one thing this
    // path may not do.
    private static float OffsetY()
    {
        float offset = Plugin.ResultsOffsetY.Value;
        return float.IsFinite(offset) && offset > 0f ? offset : 0f;
    }

    // One bounded line per page for a frame in which the board was held back for a reason no other line reports: the
    // real numbers, the role, and the reason in the few words a live log can afford. Once per page and per reason -
    // a per-frame repaint must not turn a diagnostic into a flood - and never a player's identity.
    //
    // `signature` (optional) is the fixed layout's own "which configuration attempt is this" key: the same refusal on
    // the next repaint stays silent, while a coercion-free retry with a new glyph, a new anchor, a new window or a new
    // board size is a new attempt and gets its own line. Without it, one `fixed-layout-refused` line per page would
    // swallow every later parameter change - which is exactly what made a live refusal look like silence.
    // One refused placement, counted while this mod is standing in for the page's own blocks.
    //
    // The deferred case is *transient*: a board was drawn a moment ago and a re-place is in flight, so the blocks stay
    // away and the player sees no flicker. Two refusals in a row is a different statement - the page measured a strip and
    // the fit refused it twice - so the vanilla screen is the honest one and the game's blocks come back. Counting
    // refusals rather than frames is the point: a hover that defers the board for one frame can never trip this, and a
    // page that really cannot hold a board trips it within one retry round (`ResultWheel`'s "no input is banked" is the
    // same principle applied to the wheel).
    private static void NoteVanillaRefusal()
    {
        if (Hidden.Count == 0) return;
        int frame = Time.frameCount;
        if (frame == _vanillaRefusalFrame) return;
        _vanillaRefusalFrame = frame;
        _vanillaRefusals++;
        if (_vanillaRefusals < 2) return;
        RestoreHidden();
        if (ResultReportPolicyStrict.OncePerPage(HeadingReports, "vanilla-blocks-refused"))
            Plugin.Logger?.LogInfo("[DIAG] the page's own blocks were given back: this board has been hidden while the page refused two placements in a row, so no board is standing in for them - "
                + "the vanilla screen is the honest one, and the next placement that really lands puts them away again (1.19.59).");
    }

    private static void ReportHeldBack(string where, string reason, string detail, string signature = null)
    {
        // 1.19.59: a refusal is the page's own evidence that no board is standing in for it. Counted before the per-page
        // report latch (which exists to keep the log quiet, not to decide anything), once per frame.
        NoteVanillaRefusal();
        string key = "held-back-" + where + "-" + reason;
        if (!string.IsNullOrEmpty(signature)) key += "|" + signature;
        if (!ResultReportPolicy.OncePerPage(HeadingReports, key, Forced, _forcedOutcomePending)) return;
        // A board that was never placed has no strip to report, and printing "0..0" for it would be the same kind of
        // half-truth this helper exists to remove. The figures are named for what they are: the strip this page last
        // gave the board, the height the board cannot be drawn under, and the figures that produced the refusal -
        // and a page that never gave the board a strip says so instead of printing the zero box the strip's own
        // fields hold when they were never written (`heldStrip=none` next to `top=0, bottom=0` was that same false
        // reading, one line removed).
        bool held = _holdStrip.Bottom > _holdStrip.Top;
        string strip = held
            ? "heldStrip=" + _holdStrip.Top.ToString("0") + ".." + _holdStrip.Bottom.ToString("0")
              + " (" + (_holdStrip.Bottom - _holdStrip.Top).ToString("0") + "px), have=" + (_holdStrip.Bottom - _holdStrip.Top).ToString("0") + "px,"
              + " top=" + _holdStrip.Top.ToString("0") + ", bottom=" + _holdStrip.Bottom.ToString("0") + ","
            : "heldStrip=none, have=unmeasurable, top=unmeasurable, bottom=unmeasurable,";
        Plugin.Logger?.LogInfo($"[DIAG] {where} board not drawn: reason={reason}, role={ResultPageRolePolicy.Name(Role())}, "
            + $"{strip} need={_boardFloor:0}px, "
            + $"screen={Screen.width}x{Screen.height}, detail={detail}{(Forced ? ", forcedByF8=true: the F8 press above will only be answered by a refusal line on the forced frame it owes, never silence" : string.Empty)}; the vanilla result page is left exactly as the game drew it and the page is measured again.");
        // A forced press whose frame ends here has its line - the press is accounted for, whether or not the
        // per-page latch has already fired for this reason.
        if (Forced) _forcedOutcomePending = false;
    }

    // Before drawing cached settlement geometry, revalidate every native/Unity action and the band the board was
    // placed from. If a late control overlaps the board or cannot be read, restore vanilla and attach again from the
    // new strip. 1.19.21: the native reveal, the settle frames and the strip the section has drawn *to* are no longer
    // re-judged here - a board placed from the final layout keeps its band while the reveal draws, and the only
    // fresh measurements that can refuse a frame are the pages' controls and the band `_holdStrip` the board really
    // stands in.
    private static bool BoardStillSafe()
    {
        if (_page == null || _grid == null || _content == null) return false;
        // 1.19.43: the rectangle judged here is the one the board really occupies - the plate the table is drawn into,
        // with the header the close stands in above it. It is *never* `_grid.Height`, the table's unscrolled height: a
        // page whose band is shorter than the whole table gets a plate of whole rows and scrolls the rest
        // (`PlateFor`/`ResultsScroll`), so `_grid.Height` is a number nothing paints. Judging by it refused a board that
        // stood inside its band on `board-outside-strip`, and the scan's own overlap test then reported the same
        // over-tall rectangle reaching the page's waiting line - the recorded failure page's "1750x900 px at (405,994)"
        // over a 232px strip, whose real plate was 149px at (405,994) under its 53px header, i.e. inside 941..1173.
        // `ResultChromeLayout.BoardOf` is the one rule that says where a plate and its header are; the hit rectangle
        // below and `ChromeBoardRect` already read the board through it.
        float plateHeight = _scrollPlate > 0f ? _scrollPlate : _origin.Height;
        if (!(plateHeight > 0f)) plateHeight = _origin.Height;
        var board = ResultChromeLayout.BoardOf(new LayoutRect(_origin.X, _origin.Y, _grid.Width, plateHeight), ChromeMetrics());
        if (_succeeded)
        {
            // The reveal is still watched (its ledger drives [DIAG] and the drift stand-down), never waited on.
            CompletionReady(_page);
            if (!SuccessRegion(_page, out ResultsSafeArea successArea, out bool successButtonMeasured, out float successButtonTop,
                    out float successButtonHeight, out FooterControls successControls, out bool successNativeFooterVisible,
                    out ResultFooterSource successFooterSource, out ResultsBand successBand, board, true)) return false;
            if (successControls.OverlapsBoard)
            {
                ReportBoardOverlap("success", successControls, successArea.Top, successArea.Height, successFooterSource);
                return false;
            }
            // 客机页固定存在等待状态文字，任何交互扫描都不可能证明本页为空，所以放行只看锚点布局可读
            // + 固定页面几何；主机的 band-proof 则要求完整扫描与完整 parent coverage。
            if (!GuestClient() && (!successControls.Complete || !successControls.ParentCoverageComplete))
            {
                ReportIncompleteScan("success", successControls, successArea.Top, successArea.Height, successFooterSource);
                return false;
            }
            // The completion settle gate keeps counting for the log; a placed board is not withdrawn over it any more.
            Completion.Observe(Time.frameCount, true, successArea.Top);
            // 1.19.52/1.19.53 (F1): the same floor the placement hangs from, asked again about the board that is
            // *already* up - and asked about the rectangle that is really on the page. `board` is
            // `ResultChromeLayout.BoardOf(origin, metrics)`, i.e. the plate including the header the board reserves, so
            // `board.Y` is the board's own top edge. The strip's top is not that edge: a bottom-aligned board stands
            // lower than its band's top, and judging the band instead of the plate would refuse a board that is clear of
            // everything. The clearance is the same `MessageGap` the placement leaves.
            // 1.19.55: an early board is not withdrawn by the live drawn floor. It was placed from the section's laid-out
            // slot while the reveal was still drawing (`CompletionFinalBounds.Placed`, revealFinished=false), so the floor
            // walks down under it as the page animates - and taking the board away for that is exactly the "drawn first,
            // moved after" defect this check exists to prevent on the mature path. The mature placement (which needs the
            // page's own boundary) replaces the early one and is judged by this line from then on.
            if (!_earlyBoard && float.IsFinite(_drawnFloorCompletion) && board.Y < _drawnFloorCompletion + MessageGap - 0.5f)
            {
                ReportHeldBack("success-strip", "board-above-drawn-section",
                    "plateY=" + board.Y.ToString("0") + ",drawnFloor=" + _drawnFloorCompletion.ToString("0")
                    + ",gap=" + MessageGap.ToString("0") + ",bandTop=" + _holdStrip.Top.ToString("0")
                    + ",boardBottom=" + board.Bottom.ToString("0"));
                return false;
            }
            // The band under the page's measured actions is a placement the fit itself accepted: its own two edges are
            // the whole contract, and no button sits under the board to keep clear of. Every other placement is still
            // bounded by the button the player has to press.
            bool belowActions = successFooterSource == ResultFooterSource.BelowActions;
            // The band a standing board is judged against is the band it was placed from (`_holdStrip`), not the pixels
            // the section has drawn to this frame: those move while the reveal runs, and re-judging by them is what
            // withdrew the placed board on the recorded page. A fresh measurement is only a *wall* (the button), never
            // a re-anchor of the band.
            float safeTop = _holdStrip.Bottom > _holdStrip.Top
                ? Math.Max(0f, _holdStrip.Top) : Math.Max(0f, successArea.Top) + OffsetY();
            float bottom = _holdStrip.Bottom > _holdStrip.Top
                ? Math.Min(_holdStrip.Bottom, Screen.height - 8f) : Math.Min(successArea.Bottom, Screen.height - 8f);
            if (!float.IsFinite(safeTop) || !float.IsFinite(bottom) || !(bottom >= safeTop))
            {
                ReportHeldBack("success-strip", "strip-unusable",
                    "safeTop=" + safeTop.ToString("0") + ",stripBottom=" + bottom.ToString("0") + ",source=" + (_holdValid ? _holdStrip.FooterSource : successFooterSource));
                return false;
            }
            if (board.Y < safeTop - BoardSlack || board.Bottom > bottom + BoardSlack)
            {
                ReportHeldBack("success-strip", "board-outside-strip",
                    "boardY=" + board.Y.ToString("0") + ",boardBottom=" + board.Bottom.ToString("0")
                    + ",bandTop=" + safeTop.ToString("0") + ",bandBottom=" + bottom.ToString("0") + ",source=" + (_holdValid ? _holdStrip.FooterSource : successFooterSource));
                return false;
            }
            if (!belowActions && successButtonMeasured
                && board.Bottom > successButtonTop - ResultBoardHold.ButtonClearance + BoardSlack)
            {
                ReportHeldBack("success-strip", "board-reaches-button",
                    "boardBottom=" + board.Bottom.ToString("0") + "," + ResultFooterProvenance.LobbyWall(successFooterSource, successButtonTop, successButtonMeasured)
                    + ",clearance=" + ResultBoardHold.ButtonClearance.ToString("0"));
                return false;
            }
            // 客机：没有本页可用的 no-footer 证明（等待状态文字本身就是 active 控件），此门槛只属于主机。
            // 1.19.55: the page's band proof is what an early board is *waiting for*, never a reason to take it away: the
            // early placement already required a complete scan, no overlap and a strip that holds the board's own floor
            // (`ResultEarlyBoardPolicy`). The mature board is the one that must prove the footer.
            if (!GuestClient() && !successButtonMeasured && !_earlyBoard)
            {
                bool successScanEmpty = successControls.ProvenEmpty;
                SuccessBandProofState successBandProof = ResultSuccessBandProofPolicy.Decide(Role(),
                    successControls.Complete, successControls.ParentCoverageComplete, successControls.Count,
                    successControls.Unmeasurable, successNativeFooterVisible, successButtonMeasured,
                    successControls.OverlapsBoard, successBand);
                if (!successScanEmpty || successBandProof != SuccessBandProofState.Proven)
                {
                    ReportHeldBack("success-strip", "no-footer-unproofed",
                        "buttonMeasured=" + successButtonMeasured + ",noFooterReady=" + SuccessNoFooter.Ready
                        + ",nativeVisible=" + successNativeFooterVisible + ",active=" + successControls.Count
                        + ",unmeasurable=" + successControls.Unmeasurable + ",scanComplete=" + successControls.Complete
                        + ",parentCoverageComplete=" + successControls.ParentCoverageComplete + ","
                        + ResultSuccessBandProofPolicy.Describe(successBandProof, successBand));
                    return false;
                }
                else successFooterSource = ResultFooterSource.InteractiveBand;
            }
            float room = bottom - safeTop;
            var current = new BoardStrip(safeTop, safeTop + room, belowActions ? false : successButtonMeasured, successButtonTop, successButtonHeight,
                true, successFooterSource);
            // The drawn rectangle, not the table: a grid taller than its plate is scrolled inside it, so the plate is
            // the only height that can ever reach the strip's foot or the button's clearance line.
            if (!ResultBoardHold.HeldGridFits(_holdStrip, current, board, board.Height, Screen.height, BoardSlack))
            {
                ReportHeldBack("success-strip", "grid-would-reach-edge",
                    "boardBottom=" + board.Bottom.ToString("0") + ",visibleHeight=" + board.Height.ToString("0")
                    + ",fullGrid=" + _grid.Height.ToString("0") + ",heldStripBottom="
                    + (float.IsFinite(_holdStrip.Bottom) ? _holdStrip.Bottom.ToString("0") : "unmeasured")
                    + ",source=" + successFooterSource);
                return false;
            }
            return true;
        }

        if (!FailureStrip(_page, out float failureTop, out float failureBottom, out _, out bool failureFooterMeasured,
            out float failureFooterTop, out float failureFooterHeight, out _, out FooterControls failureControls,
            out bool failureNativeFooterVisible, out ResultFooterSource failureFooterSource, out _,
            out ResultsBand failureBand, board, true)) return false;
        // 客机：同上——完整扫描证明只约束主机，客机放行只看锚点布局可读 + 固定页面几何。
        if (!GuestClient() && !failureControls.Complete)
        {
            ReportIncompleteScan("failure", failureControls, failureTop, Math.Min(failureBottom, Screen.height - 8f) - failureTop, failureFooterSource);
            return false;
        }
        if (failureControls.OverlapsBoard)
        {
            ReportBoardOverlap("failure", failureControls, failureTop, Math.Min(failureBottom, Screen.height - 8f) - failureTop, failureFooterSource);
            return false;
        }
        // The same 1.19.21 rule as the success page: the band a standing failure board is judged against is the one it
        // was placed from; the title's layout and the reveal may keep animating without moving the board away from it.
        float stripTop = _holdStrip.Bottom > _holdStrip.Top ? _holdStrip.Top : failureTop;
        float failureBottom2 = _holdStrip.Bottom > _holdStrip.Top
            ? Math.Min(_holdStrip.Bottom, Screen.height - 8f) : Math.Min(failureBottom, Screen.height - 8f);
        // 1.19.61（客机）：页脚是一个边界，而且当帧就收紧。已经站在页面上的板，判据用的条带是「落位时的条带」与
        // 「这一帧同一套算术给出的下沿」（`failureBottom`，同一张客机条带）里**更靠上**的那个；原来那块板整块还站
        // 得进去就整块上移（同一帧，板不消失），站不进去就交给下一次 fit 缩板——不再用「落位时的条带」去比「这一帧
        // 的页脚」、然后每一个 repaint 都把板撤掉（2026-11-25 快照 L4881 落位 1371 / L4883 页脚 1338 的死循环）。
        if (GuestClient() && ResultClientStandingBand.TryTighten(new ResultsSafeArea(stripTop, failureBottom2),
                failureBottom, board.Height, BoardSlack, out ResultsSafeArea tightenedBand, out bool boardHoldsHeight))
        {
            if (tightenedBand.Bottom < failureBottom2 - 0.5f)
            {
                failureBottom2 = tightenedBand.Bottom;
                if (boardHoldsHeight && MoveStandingFailureBoard(tightenedBand, failureFooterTop, failureFooterSource))
                    return true;
            }
        }
        if (board.Y < stripTop - BoardSlack || board.Bottom > failureBottom2 + BoardSlack)
        {

            ReportHeldBack("failure-strip", "board-outside-strip",
                "boardY=" + board.Y.ToString("0") + ",boardBottom=" + board.Bottom.ToString("0")
                + ",bandTop=" + stripTop.ToString("0") + ",bandBottom=" + failureBottom2.ToString("0") + ",source=" + (_holdValid ? _holdStrip.FooterSource : failureFooterSource));
            return false;
        }
        if (failureFooterMeasured && board.Bottom > failureFooterTop - ResultBoardHold.ButtonClearance + BoardSlack)
        {
            ReportHeldBack("failure-strip", "board-reaches-button",
                "boardBottom=" + board.Bottom.ToString("0")
                + ",footerTop=" + (float.IsFinite(failureFooterTop) ? failureFooterTop.ToString("0") : "unmeasurable")
                + ",clearance=" + ResultBoardHold.ButtonClearance.ToString("0"));
            return false;
        }
        // 客机：量到的控件只用于事后重叠保护，不构成放行前提。
        //
        // 1.19.37: a host page has a second, *page-measured* way past this refusal, and it is the shape the recorded
        // failure page really had - the title anchor above, the waiting/footer text the page laid out below, a
        // complete scan that found nothing switched on, and the native page's own "my footer is visible" flag. The
        // leave button being switched off (`activeInHierarchy=false`, so `ButtonActive` refuses to measure it) is not
        // evidence that the board stands over something the player can press, and `FailureNoFooter.Ready` is
        // client-only, so it can never stand in for a host. Nothing above is loosened: the incomplete-scan refusal
        // and the overlap refusal are asked first and unchanged, so a control that is really active and reaches the
        // board still defers this frame. The policy is `ResultFailureBandProofPolicy`; see it for the recorded line
        // this replaces (`reason=no-footer-unproofed, ... count=0,complete=True,buttonMeasured=false`).
        FailureBandProofState failureBandProof = ResultFailureBandProofPolicy.Decide(Role(), failureControls.Complete,
            failureControls.ParentCoverageComplete, failureControls.Count, failureControls.Unmeasurable,
            failureNativeFooterVisible, failureFooterMeasured, failureControls.OverlapsBoard, failureBand);
        if (!GuestClient() && !failureFooterMeasured && failureBandProof != FailureBandProofState.Proven)
        {
            ReportHeldBack("failure-strip", "no-footer-unproofed",
                "buttonMeasured=false,noFooterReady=" + FailureNoFooter.Ready
                + ",nativeVisible=" + failureNativeFooterVisible + ",count=" + failureControls.Count + ",complete=" + failureControls.Complete
                + "," + ResultFailureBandProofPolicy.Describe(failureBandProof, failureBand));
            return false;
        }
        var failureStrip = new BoardStrip(stripTop, failureBottom2, failureFooterMeasured, failureFooterTop, failureFooterHeight,
            true, failureFooterSource);
        // Same rule as the success page: the plate is what is drawn, the table scrolls inside it.
        if (!ResultBoardHold.HeldGridFits(_holdStrip, failureStrip, board, board.Height, Screen.height, BoardSlack))
        {
            ReportHeldBack("failure-strip", "grid-would-reach-edge",
                "boardBottom=" + board.Bottom.ToString("0") + ",visibleHeight=" + board.Height.ToString("0")
                + ",fullGrid=" + _grid.Height.ToString("0") + ",heldStripBottom="
                + (float.IsFinite(_holdStrip.Bottom) ? _holdStrip.Bottom.ToString("0") : "unmeasured"));
            return false;
        }
        return true;
    }

    // 把这一页已经画出来的板整块搬进收紧后的条带。
    //
    // 高度不变、宽度不变，底边按首次落位那套算术（`ResultBoardHold.TryPlace` 的 `bottomAligned` + `BoardSlack`）贴到
    // 条带的新下沿，`_origin` 跟着移动同样的像素——所以表体、滚轮窗口和点击矩形描述的还是同一块板，只是它现在停在
    // 页面真正画出来的那行字上面。整块放不下（页脚抬得比板还高）就回答 false，由调用方按既有路径重新 fit 一块更小的
    // 板：那一条会写自己的日志，这里不发明一个「勉强画出来」的矩形。
    private static bool MoveStandingFailureBoard(ResultsSafeArea band, float footerTop, ResultFooterSource source)
    {
        if (!band.Fits || !_holdValid) return false;
        if (!(_holdBoard.Width > 0f) || !(_holdBoard.Height > 0f)) return false;
        var strip = new BoardStrip(band.Top, band.Bottom, float.IsFinite(footerTop), footerTop, float.NaN,
            _holdStrip.AnchorMeasured, source, _holdStrip.FillCeiling);
        if (!ResultBoardHold.TryPlace(Screen.width, Screen.height, strip, _holdBoard.Width, _holdBoard.Height,
                Plugin.ResultsOffsetX.Value, out LayoutRect plate, BoardSlack, bottomAligned: true))
            return false;
        if (!ResultChromeLayout.PlateInsideBand(plate, band.Top, band.Bottom, Screen.height, BoardSlack)) return false;
        float delta = plate.Y - _holdBoard.Y;
        float previousFoot = _holdStrip.Bottom;
        if (Math.Abs(delta) < 0.5f) return true;
        _holdBoard = new LayoutRect(plate.X, plate.Y, _holdBoard.Width, _holdBoard.Height);
        _origin = new LayoutRect(_origin.X, _origin.Y + delta, _origin.Width, _origin.Height);
        _holdStrip = strip;
        if (HeadingReports.Add("failure-foot-rose"))
            Plugin.Logger?.LogInfo($"[DIAG] failure board tightened on the frame the footer rose: the band this page now offers ends at {band.Bottom:0} (the band the plate was fitted into ended at {(float.IsFinite(previousFoot) ? previousFoot.ToString("0") : "unmeasured")}, footerTop={(float.IsFinite(footerTop) ? footerTop.ToString("0") : "unmeasurable")}), so the same plate (bottom={_holdBoard.Bottom:0}, {_holdBoard.Width:0}x{_holdBoard.Height:0}) moved up {Math.Abs(delta):0}px on this repaint instead of being withdrawn for a re-fit - the boundary the fit and the per-repaint check read is the same one, and it only ever tightens. role={ResultPageRolePolicy.Name(Role())}.");
        return true;
    }

    // Whether the placement on the screen has survived one full repaint of the per-repaint safety check.
    //
    // The chain is keyed on the plate itself and on consecutive frames, never on a counter another path has to reset:
    // a re-fit, a fallback move, a new window or a frame in which nothing was drawn all start it over, so a page that
    // keeps moving can never accumulate two samples by accident. Two consecutive safe, *unchanged* plates is the
    // smallest evidence that a board stands where the page's own controls leave it - the evidence the recorded
    // failure page was missing when it drew a board for one frame and restored the vanilla blocks on the next.
    private static int _safeFrames, _safeFrame = -1;
    private static LayoutRect _safePlate;

    private static bool BoardSettled(LayoutRect plate)
    {
        int frame = Time.frameCount;
        bool same = frame == _safeFrame + 1
            && Math.Abs(plate.X - _safePlate.X) <= ResultChromeSettle.TolerancePixels
            && Math.Abs(plate.Y - _safePlate.Y) <= ResultChromeSettle.TolerancePixels
            && Math.Abs(plate.Width - _safePlate.Width) <= ResultChromeSettle.TolerancePixels
            && Math.Abs(plate.Height - _safePlate.Height) <= ResultChromeSettle.TolerancePixels;
        _safeFrames = same ? _safeFrames + 1 : 1;
        _safeFrame = frame;
        _safePlate = plate;
        return _safeFrames >= 2;
    }

    private static void DeferBoardForInteraction()
    {
        // 1.19.59: the page's own blocks are *not* given back here any more.
        //
        // This is the recorded flicker: the hover/scan refuses one frame (`count=2,hidden=1,scanned=3`), the deferred
        // frame called `RestoreHidden()`, the next repaint placed the board again and hid the same blocks - at the poll
        // cadence, i.e. the prisoner cards and the artifact block came and went while the pointer was simply moving over
        // the page. The blocks belong to the page visit, not to one frame's placement: they are given back by
        // `VanillaHideExpired` (no board for `VanillaHideGrace` seconds), by F8 hiding the board, by the switches, by a
        // page change and by leaving the screens - every one of those is a real end of this mod's stand-in.
        ResetPlacement();
        _holdValid = false; _holdBoard = default; _holdStrip = default; _holdOpacity = 0f;
        // `_placedOnFallback` is dropped because the placement it described is gone; `_fallbackRecoveries` is not.
        // The second look is a budget of one per page and this deferral is the answer to having spent it: 1.19.26,
        // refunding it here armed the very retry whose strict board this function is withdrawing - fail-open board,
        // second look, `no-footer-unproofed` withdrawal, refund, second look, for ever. A page that has spent its one
        // retry waits for `ResultAnchorDrift`'s own budget instead of re-asking a question it already answered.
        _placedOnFallback = false;
        // `Drift.Forget()` used to sit here, and it reset the page's whole drift budget as well as the recorded strip - so every
        // deferral refunded the two re-measures `ResultAnchorDrift` allows per page, and a page whose edges keep moving could be
        // re-measured for ever (independent review F2). `Clear` drops exactly what a deferral invalidates - the strip the board
        // that has just been withdrawn was measured into - and keeps the budget, which only a new page resets (`ForgetPage`).
        Drift.Clear(); Retry.Forget(); ClearHit();
        // Forced-aware on purpose: a forced press can reach this return through `BoardStillSafe`'s unlined refusals
        // (a strip read that failed with no ReportHeldBack of its own), and that frame must end with a line too. The
        // deferral itself is the answer, and spending the flag keeps it to one line per press instead of per frame.
        if (ResultReportPolicy.OncePerPage(HeadingReports, "board-interaction-changed", Forced, _forcedOutcomePending))
        {
            Plugin.Logger?.LogInfo("[DIAG] result board deferred: the page exposed an unreadable or overlapping native/UI control after placement; the strip is measured again before the overlay may return, and the page's own blocks stay where this mod put them until a board really stands here again (no frame-by-frame restore - that is what made them blink)."
                + (Forced ? " forcedByF8=true - this press is answered by this deferral line, never silence." : string.Empty));
            if (Forced) _forcedOutcomePending = false;
        }
    }

    // ---- 固定模式（1.19.45，[Results] FixedLayout）：玩家自己指定字号与表格底边中点 ---------------------------
    //
    // 三个配置值每一帧现读，所以文件一改、热重载一到，下一帧就生效；`FixedLayout` 关着时这里一个数值都不读，
    // 自适应（成功页/失败页各自的条带与滚轮缩放）完全照旧。
    internal static bool FixedLayoutActive()
        => Plugin.FixedLayoutConfig != null && Plugin.FixedLayoutConfig.Value;

    private static float FixedFontPixels()
        => Plugin.FixedFontPixelsConfig != null ? Plugin.FixedFontPixelsConfig.Value : ResultFixedLayout.DefaultFontPixels;

    private static float FixedAnchorX()
        => Plugin.FixedAnchorXConfig != null ? Plugin.FixedAnchorXConfig.Value : ResultFixedLayout.DefaultAnchorX;

    private static float FixedAnchorY()
        => Plugin.FixedAnchorYConfig != null ? Plugin.FixedAnchorYConfig.Value : ResultFixedLayout.DefaultAnchorY;

    // 固定模式的一次落位：字号 = 玩家指定的像素值（一个字节都不缩），矩形 = 底边中点落在玩家指定的那一点。
    //
    // 表格是用无穷大的盒子量出来的，所以量出来的尺寸就是画出来的尺寸：这里没有 `FitBoard` 的条带反算、没有
    // `ResultsZoom` 的乘算、也没有 `ResultsOffsetX/Y` 的偏移。只有**画不出来**的输入（非有限、越界、量不出表格、
    // 跑出屏幕）才拒绝，拒绝时不写缓存、不动玩家的数字；真实相交与扫描证据不足都照旧落位——板照玩家的字号与
    // 锚点画，让不让原版块让位、用多大不透明度由 `ResultFixedOverlayPolicy` 决定。
    private static bool PlaceFixedLayout(TableContent content, ImpactBoardLayout board, ImpactReport report, bool succeeded,
        out string refusal)
    {
        _fixedPlaced = false;
        _fixedPlan = ResultFixedDrawPlan.None;
        float font = FixedFontPixels();
        float anchorX = FixedAnchorX();
        float anchorY = FixedAnchorY();
        if (!ResultFixedLayout.Valid(Screen.width, Screen.height, font, anchorX, anchorY, out refusal))
        { AbandonFixedLayout(); return false; }
        int pixels = ResultFixedLayout.RoundFontPixels(font);
        PanelGrid grid = FixedGrid(content, pixels, out refusal);
        if (grid == null) { AbandonFixedLayout(); return false; }
        // 固定模式的板是「窗口真正放得下的那一段」：玩家给的锚点仍然决定底边中点，字号仍然决定行高，一屏放不下
        // 的行由滚轮滚动。顶边因此被夹进窗口——关闭按钮和冻结的表头行一定在屏幕上、一定点得到，而不是「布局算得
        // 出、实际画在屏幕外」。玩家设的 FixedFontPixels 与锚点一个数字都不改。
        if (!TryFixedPlate(grid, anchorY, out int fixedRows, out float plateHeight, out string plateWhy))
        { refusal = plateWhy; AbandonFixedLayout(); return false; }
        if (!ResultFixedLayout.TryResolve(Screen.width, Screen.height, anchorX, anchorY, font,
                grid.Width, plateHeight, out ResultFixedLayoutPlan plan, out refusal))
        { AbandonFixedLayout(); return false; }
        _origin = plan.Rect;
        _grid = grid; _content = content;
        _scrollVisible = Mathf.Min(fixedRows, grid.Rows);
        _scrollFirst = ResultsScroll.Clamp(_scrollFirst, grid.Rows, ResultsScroll.FrozenRows,
            Math.Max(1, _scrollVisible - ResultsScroll.FrozenRows));
        _scrollPlate = plateHeight;
        _fixedPlaced = true;
        _placed = true;
        _placedSucceeded = succeeded;
        // 固定模式的板与自适应/兜底的记录互不相干：那些条带描述的是另一条链的几何。
        _holdValid = false; _holdBoard = default; _holdStrip = default; _holdOpacity = 0f;
        _fallbackPlaced = false;
        // 控件相交与扫描证明在这一帧就判一次：板照画，但这块 overlay 让不让原版块让位、用多大不透明度由它决定。
        _fixedPlan = FixedLayoutDecision(plan.Rect, out string evidence);
        _fixedPlan = FullOpacity(_fixedPlan);
        ReportFixedPlacement(plan, _fixedPlan, evidence,
            " rows=" + board.Rows + ",columns=" + board.ColumnCount + ",players=" + report.Entries.Count
            + ",density=" + Plugin.Density.Name);
        return true;
    }

    // 表格在玩家指定的那个字号下自己的尺寸。
    //
    // 盒子是无穷大，所以 `PanelGrid.FitFactor` 恒为 1、`HudTable.BuildGrid` 不会再整体缩一次字——这一点**要验**，
    // 不能假设：量回来的 glyph 不是要的那个像素，就明确拒绝（`fixed-layout-font-not-honoured`），而不是悄悄画小一号。
    // 尺寸本身每调用一次就重量一次，所以人数变化、内容变宽或字号被热重载改掉之后，下一张表就是新的尺寸。
    private static PanelGrid FixedGrid(TableContent content, int pixels, out string why)
    {
        why = string.Empty;
        PanelGrid grid = HudTable.BuildGrid(content, pixels, Plugin.Density,
            float.PositiveInfinity, float.PositiveInfinity, pixels);
        if (grid == null)
        {
            why = ResultFixedLayout.GridUnmeasurable + ": the table could not be measured at a " + pixels
                + "px glyph (no rows or no columns to measure)";
            return null;
        }
        if (Math.Abs(grid.FontPixels - pixels) > 0.5f)
        {
            why = ResultFixedLayout.FontNotHonoured + ": the grid came back at " + grid.FontPixels.ToString("0.#")
                + "px for the configured " + pixels + "px glyph (nothing here shrinks the table, so this build refuses"
                + " rather than draw a size the player did not ask for)";
            return null;
        }
        return grid;
    }

    // 固定模式下一屏放得下的行数：底边中点以上的窗口高度 ÷ 行高。这就是「表头固定在顶部、其余行滚动」的那块
    // 视口；它只由玩家自己的锚点、字号和窗口决定，不看页面条带，也不改任何配置。
    private static int FixedPlateRows(PanelGrid grid, float anchorY)
    {
        if (grid == null) return ResultsScroll.MinimumVisibleRows;
        // 1.19.41 review #4: the page's own header band is drawn *above* these rows, so the room the rows may use is
        // what is left after the header is reserved. Spending it on rows is what let a top-pinned plate put the header
        // and its reserved band above the top of the screen while still calling the placement legal.
        float room = ResultsScroll.RowRoom(anchorY * Screen.height - ResultsBounds.ScreenMargin, ChromeHeader());
        return ResultsScroll.VisibleRows(room, grid.RowHeight);
    }

    // The plate a fixed-mode attempt may use, or the reason there is none.
    //
    // `anchorY` is the player's bottom-edge midpoint and the glyph is the player's size; neither is touched here. What
    // this decides is only how much of the window is left for the table once the screen margin *and the page's own
    // header band* are reserved: a page that cannot hold that header plus the two readable rows refuses instead of
    // drawing a plate whose header and close button would hang off the top of the screen.
    private static bool TryFixedPlate(PanelGrid grid, float anchorY, out int rows, out float plateHeight, out string why)
    {
        rows = ResultsScroll.MinimumVisibleRows;
        plateHeight = 0f;
        why = string.Empty;
        float room = Mathf.Max(1f, anchorY * Screen.height - ResultsBounds.ScreenMargin);
        float header = ChromeHeader();
        float rowHeight = grid != null ? grid.RowHeight : 0f;
        if (!ResultsScroll.PlateFits(room, rowHeight, header, ResultsScroll.MinimumVisibleRows))
        {
            why = ResultFixedLayout.VerticalRoom + ": room=" + room.ToString("0") + "px at anchorY="
                + anchorY.ToString("0.###") + " of a " + Screen.height + "px window cannot hold the "
                + header.ToString("0") + "px header plus " + ResultsScroll.MinimumVisibleRows + " rows at "
                + (float.IsFinite(rowHeight) && rowHeight > 0f ? rowHeight.ToString("0.#") : "unmeasurable")
                + "px (nothing is clamped, shrunk or moved: lower the glyph, move the anchor, or turn FixedLayout off)";
            return false;
        }
        rows = FixedPlateRows(grid, anchorY);
        plateHeight = Mathf.Min(grid.Height, ResultsScroll.PlateHeight(rows, grid.RowHeight, ResultsScroll.RowRoom(room, header)));
        return true;
    }

    // 固定模式拒绝之后的共同收尾：把这一帧之前被挪走的原版块按记录放回去，**并且丢掉旧网格**。
    //
    // 丢掉旧网格是这个模式自己的契约：人数变了、字号变了、窗口变了，还拿上一帧的尺寸继续落位，画出来的就是一张
    // 过期的表；`Draw` 也因为 `_grid` 为空而不会落到自适应那条链上去，页面就停在本模组没碰过的原版样子上。
    private static void AbandonFixedLayout()
    {
        _fixedPlaced = false;
        _fixedPlan = ResultFixedDrawPlan.None;
        _grid = null; _content = null;
        _widthProbe = float.NaN; _columnProbes = 0;
        _placed = false;
        Retry.Forget();
        if (_page != null) RestoreHidden();
    }

    // 固定模式的控件相交与扫描证明，每一帧都问一次。两个答案彼此独立，而且**都不再是「不画」**：
    //
    //   * `overlap`：二维矩形相交（`ResultInteractionStripPolicy.OverlapsBoard`，含
    //     `ResultBoardHold.ButtonClearance` 的余量）。X 错开就不相交——判定从来不只看 Y 或整条页脚带。
    //   * `proven`：这一帧的扫描看完了整页（`Complete` + `ParentCoverageComplete`）且没有「active 却量不到
    //     矩形」的控件。
    //
    // 真实相交 → 淡到 0.55 照样画（原版块让位）；证据不足 → 淡到 0.55 画但原版块**保留**，日志写明「证据不足、
    // 不声称无冲突」；两者都没有 → 不透明度 1.0 并让位。位置和字号一个字都不改：拒绝挪位置、拒绝缩字正是这个
    // 模式存在的理由。
    //
    // 页面自己那段「等待状态文字」按扫描的「受保护文字」交出去（`FixedFooterText`）：它不是可交互控件，成功页的
    // 文字来自按钮子树，但那不代表该按钮不可点击——真正的按钮仍然经过 `AddControl` 那条没有任何角色判断的路。
    private static ResultFixedDrawPlan FixedLayoutDecision(LayoutRect rect, out string evidence)
    {
        evidence = string.Empty;
        try
        {
            Component waiting = FixedFooterText(_page);
            FooterControls controls = ScanFooterControls(_page, rect.Y, MessageGap, waiting, rect, inspectBoard: true);
            bool proven = controls.Complete && controls.ParentCoverageComplete && controls.Unmeasurable == 0;
            evidence = "complete=" + controls.Complete.ToString().ToLowerInvariant()
                + ",parentCoverage=" + controls.ParentCoverageComplete.ToString().ToLowerInvariant()
                + ",unmeasurableControls=" + controls.Unmeasurable
                + ",scanReason=" + (controls.Reason.Length > 0 ? controls.Reason : "none")
                + ",pressableTop=" + (float.IsFinite(controls.PressableTop) ? controls.PressableTop.ToString("0") : "unmeasurable")
                + ",footerTop=" + (float.IsFinite(controls.FooterTop) ? controls.FooterTop.ToString("0") : "unmeasurable")
                + ",intersectingControls=" + controls.Blockers;
            return ResultFixedOverlayPolicy.Decide(proven, controls.OverlapsBoard, controls.Blocker, evidence,
                out _);
        }
        catch (Exception error)
        {
            // 扫描本身炸了：这也是「证据不足」，不是「这一页有问题」。板照画、原版块保留、日志说清楚。
            evidence = "scanError=" + error.Message;
            return ResultFixedOverlayPolicy.Decide(false, false, ResultFixedBlocker.None, evidence, out _);
        }
    }

    // 一次配置尝试的签名，日志限频键的一半：窗口 / 字号 / 锚点 / 板自己的矩形，全是玩家这次给的值。
    private static ResultFixedDrawPlan FullOpacity(ResultFixedDrawPlan plan)
        => new ResultFixedDrawPlan(plan.Draws, plan.Overlap, plan.Proven, 1f, plan.Reason, plan.Blocker, plan.Evidence);

    private static string FixedSignature(float screenWidth, float screenHeight, float font, float anchorX, float anchorY,
        float rectWidth, float rectHeight)
        => ResultFixedLayout.Attempt(font, anchorX, anchorY, screenWidth, screenHeight, rectWidth, rectHeight);

    // 固定模式那行「画了」的诊断：板自己的矩形、真实字号、锚点、窗口、与谁的矩形相交、opacity，证据不足时
    // 说清证据不足。限频按「这一页 × 这次配置尝试 × 这个相交对象 × 这个证明状态」做键：改字号、改锚点、换分辨率、
    // 人数/列数变化、相交对象变了都是新的一次尝试，会重新写一遍；同一帧里重复的同一句话只写一次。
    private static void ReportFixedPlacement(ResultFixedLayoutPlan plan, ResultFixedDrawPlan draw, string evidence,
        string extras)
    {
        string key = "fixed-layout|"
            + FixedSignature(Screen.width, Screen.height, FixedFontPixels(), FixedAnchorX(), FixedAnchorY(),
                plan.Rect.Width, plan.Rect.Height)
            + "|" + draw.Signature;
        if (!HeadingReports.Add(key)) return;
        Plugin.Logger?.LogInfo("[DIAG] " + draw.Describe(plan, Screen.width, Screen.height,
            ResultPageRolePolicy.Name(Role()), _succeeded ? "success" : "failure")
            + " fixedrect=(" + plan.Rect.X.ToString("0") + "," + plan.Rect.Y.ToString("0") + ","
            + plan.Rect.Width.ToString("0") + "x" + plan.Rect.Height.ToString("0") + "),actualglyph="
            + plan.FontPixels + "px,anchor=(" + plan.AnchorX.ToString("0.###") + "," + plan.AnchorY.ToString("0.###")
            + "),window=" + Screen.width + "x" + Screen.height + ",opacity=" + draw.Opacity.ToString("0.##")
            + ",evidence=" + evidence + (extras ?? string.Empty)
            + "; the configured glyph and the bottom-edge midpoint are the ones drawn, nothing was clamped, shrunk or"
            + " moved, and the vanilla blocks only make way on a frame this board really replaces them.");
    }

    // 这一页「不是可按控件」的那段页脚文字，按扫描的规则交出去。
    //
    // 失败页：`m_returnToLobby_text`——自适应那条链一直用的就是它，固定模式读同一个对象，语义不另开一份。
    // 成功页：`CM_PageExpeditionSuccess` 没有等待文字的字段（反射只有 `m_btnLeaveExpedition` / `m_lobbyButtonVisible`），
    // 那行字是 `m_btnLeaveExpedition` 子树里的 TextMeshPro，`FooterTextTop` 量的也正是它——取法照抄：只挑**页面真的在画**
    // 的（`ResultFooterTextPolicy.Counts`：开着且 alpha 可见），多个候选取最靠上的那个。主机页上那行等待文字没在画，
    // 于是这一次交出去的是按钮自己的标签；它在按钮自己的矩形里，而按钮本身就是可按键，所以「受保护文字」这个身份
    // 在这里不会放宽任何决定（遮挡检查对按钮自己照旧生效），也不会让主机页多出一次假拒绝。
    // 交出去之后，扫描把这个对象记成「受保护文字」：算进 `FooterTop`、不算进 `PressableTop`，也不参与遮挡拒绝。
    private static Component FixedFooterText(Component page)
    {
        try
        {
            var fail = page != null ? page.TryCast<CM_PageExpeditionFail>() : null;
            if (fail != null) return fail.m_returnToLobby_text;
            var success = page != null ? page.TryCast<CM_PageExpeditionSuccess>() : null;
            if (success == null || success.m_btnLeaveExpedition == null) return null;
            Component highest = null;
            float top = float.NaN;
            foreach (TextMeshPro label in success.m_btnLeaveExpedition.GetComponentsInChildren<TextMeshPro>(true))
            {
                if (label == null) continue;
                bool active;
                try { active = label.gameObject.activeInHierarchy; } catch { continue; }
                if (!ResultFooterTextPolicy.Counts(active, HeadingAlpha(label))) continue;
                if (!TryScreenRect(label, out LayoutRect labelRect)) continue;
                if (float.IsFinite(top) && labelRect.Y >= top) continue;
                top = labelRect.Y;
                highest = label;
            }
            return highest;
        }
        catch { return null; }
    }

    // 固定模式真正画出去的那一帧。
    //
    // 字号、两个锚点和窗口都是**当场**读的：热重载改了锚点、换了分辨率，这一帧就按新值画，不必等到下一次
    // `Prepare`；字号变化时重新测量。画不出来的输入丢掉网格并明确说出原因（不再先四舍五入成"能画的值"）；
    // 控件相交、扫描证据不足都不撤板，而是按 `ResultFixedOverlayPolicy` 淡画——玩家要求的字号与位置就是画出来
    // 的那个，日志里每行都带着它与这次配置尝试。
    private static void FixedLayoutDraw()
    {
        float configured = FixedFontPixels();
        float anchorX = FixedAnchorX();
        float anchorY = FixedAnchorY();
        string attempt = FixedSignature(Screen.width, Screen.height, configured, anchorX, anchorY, 0f, 0f);
        // 配置本身也在这一帧校验：热重载写进来的非数字 / 非正数 / 越界在这里同样明确拒绝，而不是先被
        // `RoundFontPixels` 抬进范围再"合法"地画出来。
        if (!ResultFixedLayout.Valid(Screen.width, Screen.height, configured, anchorX, anchorY, out string why))
        {
            AbandonFixedLayout();
            ReportHeldBack("fixed", "fixed-layout-input-invalid", why, attempt);
            return;
        }
        int pixels = ResultFixedLayout.RoundFontPixels(configured);
        PanelGrid grid = _grid;
        string reglyph = string.Empty;
        if (grid == null || Math.Abs(grid.FontPixels - pixels) > 0.5f) grid = FixedGrid(_content, pixels, out reglyph);
        if (grid == null)
        {
            AbandonFixedLayout();
            ReportHeldBack("fixed", "fixed-layout-grid", reglyph, attempt);
            return;
        }
        // 和落位那一帧同一条算式：视口是窗口放得下的整行，顶边夹在窗口里，底边中点就是玩家给的锚点。
        if (!TryFixedPlate(grid, anchorY, out int fixedRows, out float plateHeight, out string plateWhy))
        {
            AbandonFixedLayout();
            ReportHeldBack("fixed", "fixed-layout-vertical-room", plateWhy, attempt);
            return;
        }
        if (!ResultFixedLayout.TryResolve(Screen.width, Screen.height, anchorX, anchorY, configured,
                grid.Width, plateHeight, out ResultFixedLayoutPlan plan, out why))
        {
            AbandonFixedLayout();
            ReportHeldBack("fixed", "fixed-layout-offscreen",
                why, FixedSignature(Screen.width, Screen.height, configured, anchorX, anchorY, grid.Width, plateHeight));
            return;
        }
        _scrollVisible = Mathf.Min(fixedRows, grid.Rows);
        _scrollFirst = ResultsScroll.Clamp(_scrollFirst, grid.Rows, ResultsScroll.FrozenRows,
            Math.Max(1, _scrollVisible - ResultsScroll.FrozenRows));
        _scrollPlate = plateHeight;
        LayoutRect rect = plan.Rect;
        ResultFixedDrawPlan draw = FixedLayoutDecision(rect, out string evidence);
        draw = FullOpacity(draw);
        // 证据不足的这一帧把之前挪走的放回去，页面保持原版、板淡画在上面。**让位**放到下面 `HudTable.Draw` 之后：
        // 一帧里的任何早退都不能把原版块留在 6000 以外（review #3）。
        try
        {
            if (!draw.HidesVanilla) RestoreHidden();
        }
        catch (Exception error)
        {
            // 页面在放回原版块的过程中消失了：先按记录把这页上被挪走的东西放回原处，再停止触碰它。
            if (!_loggedMeasure)
            {
                _loggedMeasure = true;
                Plugin.Logger?.LogWarning("[DIAG] the result page went away while its blocks were being put back, so the fixed board stops touching it: " + error.Message);
            }
            _page = null;
            AbandonFixedLayout();
            return;
        }
        if (_page == null) { AbandonFixedLayout(); return; }
        _grid = grid; _origin = rect; _fixedPlan = draw;
        HudTable.Draw(grid, rect.X, rect.Y, _content, draw.Opacity, _scrollFirst, _scrollVisible, _scrollPlate);
        _boardDrawnFrame = Time.frameCount;
        ReportFixedPlacement(plan, draw, evidence, string.Empty);
        ReportForcedDraw(rect);
        _hitRect = rect;
        _hitSucceeded = _succeeded;
        _hitValid = true;
        // The wheel follows the plate that was really painted here too (1.19.59), so the fixed board is scrolled by the same
        // rectangle the press and the hover are judged against.
        _scrollValid = true;
        // 原版块让位：只在这帧 overlay 真的画出来、且扫描证据完整（`draw.HidesVanilla`）之后。
        if (draw.HidesVanilla)
        {
            try
            {
                Component vanillaPage = LivePage(out bool vanillaSucceeded);
                if (vanillaPage != null) ApplyVanillaBlocks(vanillaPage, vanillaSucceeded);
            }
            catch (Exception error)
            {
                if (!_loggedMeasure)
                {
                    _loggedMeasure = true;
                    Plugin.Logger?.LogWarning("[DIAG] the fixed board is on the screen but the page's own blocks could not be put away: " + error.Message);
                }
            }
        }
    }

    // 固定模式下数据刷新（人数变化、内容变宽）重新量表格，但底边中点不动：矩形按同一个锚点重算。
    private static bool FixedLayoutRefresh(TableContent content, ImpactBoardLayout board, ImpactReport report, bool succeeded)
    {
        if (!PlaceFixedLayout(content, board, report, succeeded, out string refusal))
        {
            // 这次配置尝试是新的一次，所以要重新说一遍：同一句话在同一页、同一组数字上仍然只写一行。
            ReportHeldBack("prepare", "fixed-layout-refused", refusal,
                FixedSignature(Screen.width, Screen.height, FixedFontPixels(), FixedAnchorX(), FixedAnchorY(), 0f, 0f));
            return false;
        }
        return true;
    }

    // ---- the fail-open placement ------------------------------------------------------------------------------

    // Where the board stands when the page could not be proven, and why.
    //
    // Nothing in here reads a gate: no title alpha, no completion edge, no footer settle, no interaction proof. It
    // reads whatever of the page *can* be read - the mission title's rectangle even while it is switched off, the
    // rectangles the interaction scan did manage to measure, the waiting-status line as a lower wall - and hands the
    // answer to `ResultFallbackLayout`, which falls back to the window's own safe area when that is nothing at all.
    // The floor the fail-open path is judged against, after its own failures have walked it down.
    //
    // The measured `_boardFloor` is what the page would need for a readable board; a page that cannot offer that much used to
    // be answered with "no band can hold the board" for ever. Every fail-open attempt that ends without a board lowers this
    // scale, so the next frame accepts a shorter band and builds a smaller grid - down to the smallest grid this build can
    // draw (`ResultsBandFit.MinimumDrawableHeight`), which is where it stops. A strict board standing resets it: the page has
    // proved it can hold the real one.
    private static float FallbackFloor()
        => ResultFallbackLadder.Floor(_boardFloor,
            ResultsBandFit.MinimumDrawableHeight(ImpactBoard.RowCount(true), Plugin.Density), _fallbackFloorScale);
    private static bool FallbackPlacement(Component page, bool succeeded, out float top, out float room,
        out BoardStrip strip, out string why)
    {
        top = 0f; room = 0f; strip = default; why = "no-page";
        if (page == null) return false;
        float anchorBottom = float.NaN;
        ResultBoundSource anchorSource = ResultBoundSource.ScreenSafeArea;
        float footerText = float.NaN;
        if (GuestClient() && !succeeded && !Forced && !_fallbackPlaced && !ClientWall.Settled)
        {
            why = ResultFailureWallPolicy.Unobserved + ": client-wall-not-settled; the first adaptive board waits for two trustworthy lower-bound readings, even after the refusal budget";
            return false;
        }
        ResultBoundSource footerTextSource = ResultBoundSource.None;
        Component protectedText = null;
        try
        {
            var fail = page.TryCast<CM_PageExpeditionFail>();
            if (fail != null)
            {
                TextMeshPro title = fail.m_missionFailed_text;
                if (title != null && TryScreenRect(title, out LayoutRect titleRect))
                {
                    anchorBottom = titleRect.Bottom;
                    anchorSource = ResultBoundSource.TitleAnchor;
                }
                TextMeshPro waiting = fail.m_returnToLobby_text;
                // 1.19.21: the waiting block is the protected text of this page, so the scan behind this path cannot
                // mistake it for a control the board may cover. Its *layout* rectangle is no longer the wall (review #3):
                // on the recorded page it sits 155px below the line the player reads.
                protectedText = waiting;
                // 1.19.61（评审 #3）：兜底路径（也是 F8 强绘走的那条）只认严格路径同一个槽位权威：客机上
                // `m_btnGoToLobby` 自己的已布局几何与 `m_returnToLobby_text` 真正渲染的字形，取更靠上的那一堵。
                // 布局框、别的可按控件、页面下界、玩家按键都不授权这条带；没有权威时下面直接拒收（保留原版 + 重测）。
                if (GuestClient() && !succeeded)
                {
                    ResultClientSlotEvidence slot = ClientSlot(page);
                    footerText = slot.Top;
                }
                else if (waiting != null && TryScreenRect(waiting, out LayoutRect waitingRect))
                {
                    // 主机失败页：与 1.19.60 一个字节都不变。
                    footerText = waitingRect.Y;
                }
            }
            else
            {
                // The success page: the icon row is the upper bound and the client's waiting block the lower one, and
                // the last trusted icon row stands in for a frame in which the reveal has not drawn it yet.
                // The fail-open band reads the same latch the placement does, so a page whose reveal finished gets the
                // same section edge from both paths (`CompletionFinalBounds.Placed`).
                float iconRow = IconRowBottom(page, RevealFinished, ContentSettled);
                if (float.IsFinite(iconRow))
                {
                    _iconRowBottom = iconRow;
                    anchorBottom = iconRow;
                    anchorSource = ResultBoundSource.IconRow;
                }
                else if (float.IsFinite(_iconRowBottom))
                {
                    anchorBottom = _iconRowBottom;
                    anchorSource = ResultBoundSource.LastTrustedIconRow;
                }
                footerText = FooterTextTop(page, out footerTextSource);
            }
        }
        catch (Exception error) { why = "page-unreadable: " + error.Message; }
        // The scan is the same one the strict path uses, run with the best top edge this page could give. It is allowed
        // to answer incomplete here - the rectangles it did read are exactly what the fail-open board has to step
        // around - and it is not asked to prove anything.
        float guessTop = float.IsFinite(anchorBottom)
            ? anchorBottom + ResultsBounds.Clearance(Screen.height)
            : ResultFallbackLayout.SafeTop(Screen.height);
        FooterControls controls = default;
        // 1.19.52 (F2/F6): the same column the strict path reads, for the same reason - this fallback runs the same
        // scan, and a wall it invents from an interaction the board could never cover is the eight-row plate again,
        // one budget later. The failure page keeps `LayoutColumn.None`, byte for byte: its strip has no board column
        // to prove, and its two pages' arithmetic must not move.
        LayoutColumn fallbackColumn = succeeded ? BoardColumn() : LayoutColumn.None;
        try { controls = ScanFooterControls(page, guessTop, MessageGap, protectedText, inspectBoard: false, column: fallbackColumn); }
        catch (Exception error) { if (why == "no-page") why = "scan-failed: " + error.Message; }
        // 1a. 这一页被人看见了吗？这条兜底自己的扫描就是问这个的地方，所以它的回答也在这里被读——不是拒收次数、也
        //     不是调用顺序。一个扫描看不见的控件（结构不完整 / action 父节点没覆盖）或量不到矩形的 active 控件，正是
        //     下面那条「从窗口编出来的带」能压上去的洞；这种页面保留原版，一行日志（`Prepare` 按每页一次打印），
        //     重试走既有节流。F8 仍然被回答（`MayAnswer` 放 `Forced` 过去），因为那是玩家的请求，不是预算在决定。
        bool pageReadable = ResultFallbackPolicy.PageReadable(controls.Complete, controls.ParentCoverageComplete,
            controls.Unmeasurable);
        if (!ResultFallbackPolicy.MayAnswer(_strictMisses, Forced, pageReadable))
        {
            why = ResultFallbackPolicy.UnreadablePage + ": complete=" + controls.Complete.ToString().ToLowerInvariant()
                + ",parentCoverage=" + controls.ParentCoverageComplete.ToString().ToLowerInvariant()
                + ",unmeasurable=" + controls.Unmeasurable + ",controls=" + controls.Count
                + ",misses=" + _strictMisses + " of " + ResultFallbackPolicy.MissBudget
                + ",forcedByF8=" + Forced.ToString().ToLowerInvariant()
                + "; the panel band would be invented from the window, and a control this scan could not read may stand"
                + " exactly there - so the refusal budget does not buy a board and the vanilla page is kept";
            return false;
        }
        // 1. 这一页的下界语义，和严格路径读同一份事实（`ResultPageLowerBoundPolicy`）。
        //
        //    独立评审的具体缺陷：客机失败页上量到的「正在等待厅主」状态文字被当成下方文字墙（`footerText`），
        //    交互扫描的 `FooterTop` 也含那行字，于是 2560x1440 上这条兜底条带只有 836..1207（370px），而严格
        //    路径是 836..1429（593px）：30 行只塞进 8px 字号，小板随后重排或长驻。严格分支从 1.19.38 起就写明
        //    那行字是事实、不是墙，下界是固定页面几何（`ResultClientFailureBand`）；这条兜底没有跟上。
        //    现在：客机失败页上状态文字不再参与下界，真正可按的控件照旧是墙（`PressableTop`），页脚下沿是页面
        //    自己的固定几何；主机失败页与两个成功页三个读数原样，所以主机路径一个字节都没变。
        LobbyButtonTop(page, out float pageButtonTop);
        // 客机失败页 = 客机 + 失败页。它的下界语义由 `ResultPageLowerBoundPolicy` 给出，下面的 `walls` 就是那一份。
        bool guestFailure = GuestClient() && !succeeded;
        ResultPageWalls walls = ResultPageLowerBoundPolicy.Resolve(guestFailure, Screen.height,
            pageButtonTop, controls.PressableTop, footerText, footerTextSource);
        // The failure page's own lower bound: the readings that really bound a board there, and only those.
        //
        // 1.19.44（TOA Pure 客机失败页，`LogOutput` 12642-12676）：这里以前把 `walls.PageFloor` 也算成「量到了墙」。
        // 客机失败页的 `PageFloor` 永远是有限数（`ResultsGuestPageGeometry.PageFloor`，2560x1440 上是 1429.33），
        // 于是 `wallMeasured` 永远为真、`ResultFailureWallPolicy` 在这张页面上一次都没生效过：60 次严格拒收之后
        // fail-open 直接按屏幕下界编出 band (488..1429)、一块 900 高的板，晚一帧出现的真实控件在 1171，
        // `ResultBoardHold.TryHold` 拒绝抬起一块已经在带顶的板 → 整页 0.55 不透明度压住页脚 217px。
        //
        // 1.19.45（用户纠正）：客机失败页的下界就是那行不可点击的「正在等待厅主」提示文字自己的上沿
        // （`ResultPageLowerBoundPolicy` 把它作为 `FooterTextTop` 交出来），和主机页的按钮上沿同一性质；真正可按的
        // 控件照旧是墙，`ResultsBounds` 取更靠上的那一堵。页面自己从屏幕推出来的下沿只做条带的封顶，**不是**墙：
        // 「这块屏幕有多高」不是页面把什么画在了哪里。一条墙都量不到时，客机失败页不由帧数、也不由超时作答
        // （见 `ResultFailureWallPolicy.Waits`），保留原版页、按既有节流重测；F8 仍然由玩家自己授权。
        bool wallMeasured = false;
        int wallWait = 0;
        if (!succeeded)
        {
            if (walls.GuestFailure)
            {
                // 1.19.61（评审 #3）：客机失败页只有「同一个原生返回位置」的几何可以授权这条带——不是布局框、不是别的
                // 可按控件、不是页面自己从屏幕推出来的下界，也不是玩家按下的 F8。没有它这一帧就保留原版并按既有节流
                // 重测（`why` 说出这是「没有槽位证据」，不是超时）。
                wallMeasured = float.IsFinite(walls.FooterTextTop);
                wallWait = FailureWallWaitFrames(wallMeasured);
                if (!wallMeasured)
                {
                    why = ResultFailureWallPolicy.Unobserved + ": no-slot-evidence; hostSlot/hintGlyph unmeasurable, "
                        + "pressable=" + WallText(walls.PressableTop)
                        + ", pageFloor=" + WallText(walls.PageFloor) + " (cap only, never a wall)"
                        + ",wallPending=" + ResultClientPageActionPolicy.Pending(float.NaN, float.NaN).ToString().ToLowerInvariant() + ", forcedByF8=" + Forced.ToString().ToLowerInvariant()
                        + ", controls=" + controls.Count + ", hidden=" + controls.Hidden
                        + "; a forced press authorises a board, never an invented bound, so the vanilla page is kept and "
                        + "measured again at the existing cadence until this page's own return slot or waiting glyphs read.";
                    return false;
                }
            }
            else
            {
                // 扫描自己的 `FooterTop` 只在「页脚文字真是墙、而 `walls` 还没带着它」的页面上算数。
                wallMeasured = ResultFailureWallPolicy.WallMeasured(walls, pageButtonTop, controls.PressableTop,
                    controls.FooterTop);
                wallWait = FailureWallWaitFrames(wallMeasured);
                if (ResultFailureWallPolicy.Waits(wallMeasured, wallWait, walls.GuestFailure))
                {
                    why = ResultFailureWallPolicy.Unobserved + ": pressable=" + WallText(walls.PressableTop)
                        + ",footerText=" + WallText(walls.FooterTextTop)
                        + " (" + ResultsBounds.Name(walls.FooterTextSource) + ")"
                        + ",pageFloor=" + WallText(walls.PageFloor)
                        + " (this page's own screen-derived foot: the band's cap, never a wall)"
                        + ",button=" + WallText(pageButtonTop)
                        + ",scanFooter=" + WallText(controls.FooterTop)
                        + ",controls=" + controls.Count + ",hidden=" + controls.Hidden
                        + ",waited=" + wallWait + " of " + ResultFailureWallPolicy.UnobservedBudget + " frames"
                        + "; this page has shown no lower bound at all, and two complete scans that found nothing are not a page with no wall"
                        + ", so the fail-open band is not invented from the window";
                    return false;
                }
            }
        }
        // 客机失败页上那行字已经作为 `walls.FooterTextTop` 进了条带算术（它就是这一页的槽位边界），所以扫描自己的
        // `FooterTop`（含那行字，也含可按控件）只在不是客机失败页时作为「量到的控件」那一路补一次。
        float measuredWall = walls.GuestFailure ? float.NaN : controls.FooterTop;
        // A scan can measure the text inside the host button without measuring its visual extent.
        // This page uses only the strip above a measured native action, never the carved/extended band below it.
        bool hostFailure = !succeeded && !guestFailure;
        ResultFallbackPlan band;
        bool resolvedBand = hostFailure
            ? ResultHostFailureBand.TryResolve(Screen.height, anchorBottom, anchorSource, walls,
                pageReadable ? HostFailureButtonTop(page) : float.NaN, measuredWall, out band)
            : ResultFallbackBandPolicy.TryResolve(Screen.height, anchorBottom, anchorSource, walls, measuredWall,
                controls.BelowActions.Top, controls.BelowActions.Bottom, FallbackFloor(), out band);
        if (!resolvedBand)
        {
            why = "no-safe-area: anchorBottom=" + (float.IsFinite(anchorBottom) ? anchorBottom.ToString("0") : "unmeasurable")
                + ",footerTop=" + WallText(walls.PressableTop)
                + ",footerText=" + WallText(walls.FooterTextTop) + " (" + (walls.GuestFailure ? "the native return slot, a wall" : ResultsBounds.Name(walls.FooterTextSource)) + ")"
                + ",screen=" + Screen.height + ",boardFloor=" + FallbackFloor().ToString("0");
            return false;
        }
        // 1.19.61（评审 #3）：客机失败页上，一条必须跨过那堵墙才能装下板「扩展带」同样不算证据——那是 run 3 的
        // 0.55 全页淡画，也是强绘能走到的屏幕底托底。这里明确拒收，保留原版。
        if (walls.GuestFailure && band.Extended)
        {
            why = "no-slot-band: the board floor does not fit above this page's slot wall (top=" + WallText(band.Top)
                + ", wall=" + WallText(walls.FooterTextTop) + ", room=" + band.Height.ToString("0") + "px), and a band that "
                + "crosses the wall to reach the window's foot is not a placement this page offers - vanilla page kept, "
                + "measured again (forcedByF8=" + Forced.ToString().ToLowerInvariant() + " authorises a board, never an overlap).";
            return false;
        }
        top = band.Top; room = band.Height;
        _fallbackOpacity = band.Opacity;
        strip = new BoardStrip(band.Top, band.Bottom, false, float.NaN, float.NaN, float.IsFinite(anchorBottom),
            band.BelowControls ? ResultFooterSource.BelowActions : ResultFooterSource.ScreenSafeArea);
        // 采纳页面自己那条条带时，日志用与严格路径同一套边界名字（`ResultsBounds.Name`），而不是自造一句。
        if (band.PageBand) ReportBand("fail-open", band.Band, anchorBottom, walls.FooterTextTop, FallbackFloor());
        why = (band.PageBand
                ? "band top=" + band.Top.ToString("0") + " (" + ResultsBounds.Name(band.Band.TopSource) + ")"
                    + ", bottom=" + band.Bottom.ToString("0") + " (" + ResultsBounds.Name(band.Band.BottomSource) + ")"
                    + ", room=" + band.Height.ToString("0") + "px, clamped=" + band.Band.Clamped.ToString().ToLowerInvariant()
                    + ", boardFloor=" + FallbackFloor().ToString("0")
                : ResultFallbackLayout.Describe(band, Screen.height, FallbackFloor()))
            + ", anchorBottom=" + (float.IsFinite(anchorBottom) ? anchorBottom.ToString("0") : "unmeasurable")
            + ", footerText=" + WallText(walls.FooterTextTop) + " (" + (walls.GuestFailure ? "the waiting-status line, a wall" : ResultsBounds.Name(walls.FooterTextSource)) + ")"
            + ", footerTop=" + WallText(walls.PressableTop)
            + ", pageFloor=" + WallText(walls.PageFloor) + " (cap only)"
            + ", controls=" + controls.Count + ", hidden=" + controls.Hidden + ", unmeasurable=" + controls.Unmeasurable
            + (hostFailure ? ", host-action-wall=required" : string.Empty)
            + ResultFailureWallPolicy.AnswerNote(wallMeasured, wallWait, walls.GuestFailure);
        return room > 0f;
    }

    // A wall reading in the few words a live log can afford, without claiming an unmeasured edge.
    private static string WallText(float edge)
        => float.IsFinite(edge) ? edge.ToString("0") : "unmeasurable";

    // The per-repaint re-check of a fail-open board.
    //
    // The strict board is revalidated by `BoardStillSafe`, which answers "safe" or "take it away". A fail-open board
    // answers the same question differently, on purpose: it is moved out of the way of a control that arrived under
    // it, and when no clear band is left it is drawn fainter. It is never taken away - a screen with no table is the
    // defect this whole path exists to remove.
    private static void RefreshFallbackSafety()
    {
        bool hostFailure = !_succeeded && !GuestClient();
        if (hostFailure) _holdValid = false;
        if (_page == null || _grid == null || _content == null) return;
        if (!FallbackPlacement(_page, _succeeded, out float top, out float room, out BoardStrip strip, out _)) return;
        if (room <= 0f) return;
        // The band holds the whole board: the table plus the header the close stands in, exactly as the fit that placed it
        // measured. review #4: the place and the strip that produced it are one measurement and are committed together, the
        // band's *foot* is read (it used to be ignored), and a board that no longer fits is answered with a smaller legal
        // grid fitted inside the same band - never with "place it at the top and let it run to the bottom of the window".
        float header = ChromeHeader();
        PanelGrid grid = _grid;
        float plateHeight = PlateFor(grid, room, header, out int scrollVisible, out int scrollFirst);
        float total = plateHeight + header;
        bool refitted = false;
        if (!FitsBand(total, strip))
        {
            if (!TrySmallerGrid(room, header, out grid, out plateHeight, out scrollVisible, out scrollFirst, out total))
            {
                FadeInPlace("no-grid-fits", strip, room, total);
                return;
            }
            refitted = true;
        }
        if (!ResultFallbackLayout.TryPlaceInBand(Screen.width, Screen.height, strip.Top, strip.Bottom, grid.Width, total,
                Plugin.ResultsOffsetX.Value, out LayoutRect plate))
        {
            // The band has a foot this build cannot place inside even at the smallest grid it can draw: the board keeps the
            // rectangle it already has and goes faint. It is never pushed to the screen's bottom edge and then "corrected".
            FadeInPlace("band-refused", strip, room, total);
            return;
        }
        LayoutRect rect = new LayoutRect(plate.X, plate.Y + header, grid.Width, plateHeight);
        float planned = _fallbackOpacity;
        float opacity = ResultFallbackOpacity.Resolve(planned, rect.Bottom, strip.Top, strip.Bottom);
        bool opacityChanged = Math.Abs(opacity - _fallbackOpacity) > 0.001f;
        float beforeTop = _origin.Y;
        bool horizontal = Math.Abs(rect.X - _origin.X) > ResultAnchorDrift.MinimumPixels;
        bool vertical = Math.Abs(rect.Y - _origin.Y) > ResultAnchorDrift.MinimumPixels;
        bool sizeChanged = Math.Abs(rect.Height - _origin.Height) > ResultAnchorDrift.MinimumPixels
            || Math.Abs(grid.Width - _grid.Width) > 0.5f;
        // The question is asked of the rectangle that is on the screen (`_origin`), never of the destination just computed, and
        // it is asked of both of the band's edges: a band whose top slid down into the board is as much an intrusion as a band
        // whose foot rose into it.
        bool intrudes = ResultLateControlPolicy.MustMove(_origin.Y, _origin.Bottom, strip.Top, strip.Bottom)
            || (hostFailure && !ResultChromeLayout.PlateInsideBand(_holdBoard, strip.Top, strip.Bottom, Screen.height, 0.5f));
        bool moved = refitted || sizeChanged || horizontal || (intrudes && vertical);
        if (moved)
        {
            // One assignment block: the place, the grid it was fitted at, the plate the close stands in and the strip that
            // produced all three. A repaint can never see "a new place against the old band" - review #4's repeated withdrawal.
            _origin = rect;
            _holdBoard = plate;
            _holdStrip = strip;
            _grid = grid;
            _scrollVisible = scrollVisible; _scrollFirst = scrollFirst; _scrollPlate = plateHeight;
        }
        _holdValid = !hostFailure || ResultChromeLayout.PlateInsideBand(_holdBoard, strip.Top, strip.Bottom, Screen.height, 0.5f);
        _holdPage = _page != null ? _page.Pointer : IntPtr.Zero;
        _fallbackOpacity = opacity;
        _holdOpacity = opacity;
        if (!refitted && !moved && !opacityChanged) return;
        if (HeadingReports.Add(refitted ? "fallback-refit" : moved ? "fallback-moved" : "fallback-fade"))
            Plugin.Logger?.LogInfo($"[DIAG] the fail-open settlement board {(refitted ? "re-fitted" : moved ? "moved" : "faded")} "
                + $"inside the band this repaint measured; band=({strip.Top:0}..{strip.Bottom:0}), source={strip.FooterSource}, "
                + $"boardTop={beforeTop:0}->{_origin.Y:0}, plate={plate.Width:0}x{plate.Height:0} at ({plate.X:0},{plate.Y:0}), "
                + $"plateBottom={plate.Bottom:0}, grid={grid.Width:0}x{grid.Height:0} glyph={grid.FontPixels:0}px, "
                + $"room={room:0}px need={total:0}px, opacity={opacity:0.00}, refitted={refitted.ToString().ToLowerInvariant()}, "
                + $"role={ResultPageRolePolicy.Name(Role())}. The place and the strip are committed together, the band's foot is read, "
                + "and a board that no longer fits is fitted smaller inside the same band instead of being drawn past it.");
    }

    // Whether the whole board - table plus the header the close stands in - is inside the band this repaint measured.
    private static bool FitsBand(float total, BoardStrip strip)
        => float.IsFinite(total) && total > 0f && strip.Bottom > strip.Top && total <= strip.Bottom - strip.Top + 0.5f;

    // A legal, smaller grid inside the same band. This is the fit the review asks for by name: when the board that is up no
    // longer fits the band the page measures, a smaller *real* table is fitted into that band (header included) instead of
    // the standing rectangle being pushed down the window and corrected a frame later. A page that cannot hold even the
    // smallest drawable grid gets no new grid at all, and the caller keeps the board it has.
    private static bool TrySmallerGrid(float room, float header, out PanelGrid grid, out float plateHeight,
        out int scrollVisible, out int scrollFirst, out float total)
    {
        grid = null; plateHeight = 0f; total = 0f; scrollVisible = 0; scrollFirst = ResultsScroll.FrozenRows;
        float boardWidth = Math.Max(1f, Math.Min(_grid != null ? _grid.Width : Screen.width - 48f, Screen.width - 48f));
        // 1.19.54：这是唯一一个把**整条带高**交给 `FitBoard` 的调用点（两个严格调用点传的是已扣标题的
        // `tableRoom`）。成功页按输入契约补扣一次标题，失败页原样传整条带——与 1.19.50 的实参逐字一致。
        float body = room;
        if (_succeeded && body - header > 0f) body -= header;
        PanelGrid smaller = FitBoard(_content, body, boardWidth, _succeeded);
        if (smaller == null) return false;
        float plate = PlateFor(smaller, room, header, out int visible, out int first);
        float sum = plate + header;
        if (!(sum > 0f) || sum > room + 0.5f) return false;
        grid = smaller; plateHeight = plate; scrollVisible = visible; scrollFirst = first; total = sum;
        return true;
    }

    // The board keeps the rectangle it already has and is drawn fainter: the one answer that is never "no board at all".
    private static void FadeInPlace(string why, BoardStrip strip, float room, float total)
    {
        _fallbackOpacity = Math.Min(_fallbackOpacity, ResultFallbackLayout.OverlapOpacity);
        _holdOpacity = _fallbackOpacity;
        if (!HeadingReports.Add("fallback-fade-" + why)) return;
        Plugin.Logger?.LogInfo($"[DIAG] the fail-open settlement board keeps the place it has and goes fainter: {why}, "
            + $"band=({strip.Top:0}..{strip.Bottom:0}) room={room:0}px need={total:0}px, board=({_holdBoard.X:0},{_holdBoard.Y:0},"
            + $"{_holdBoard.Width:0}x{_holdBoard.Height:0}), opacity={_fallbackOpacity:0.00}, role={ResultPageRolePolicy.Name(Role())}; "
            + "the band this repaint measured cannot hold a board at any grid this build can draw, and a board is never taken away for it.");
    }

    // Whether the settlement board is on the page right now: the last repaint's own "a board was drawn" fact.
    internal static bool BoardOnScreen => ResultTogglePolicy.BoardUp(_boardDrawnFrame, Time.frameCount);

    // F8 on a result page: hide the board the page is showing, or give a page that is showing none one.
    //
    // The answer is taken from the display (`BoardOnScreen`), never from the override flag: `Forced` is false on a page
    // whose fail-open board the mod already put up by itself, so reading it made the press that should have hidden the
    // board turn the override *on* and draw the same board again - a live 1.19.25 failure page, where fifteen presses
    // did nothing and the fourteen that followed took the page away. The two halves then mean what the player sees:
    //
    //   hide   the two switches the key owns go off and are written back to the .cfg, exactly as the in-level press does
    //          (`Plugin.PersistToggle`, which never writes `ShowImpactBoard`), and the override goes off with them, so the
    //          next repaint restores the vanilla page and draws nothing.
    //   show   the override goes on first, so no gate, switch or stale toggle of this page can hold the board back: it is
    //          placed from the fail-open path on the very next repaint however little the page has proved, and the two
    //          switches go back on with it.
    //
    // Nothing here waits for a gate to be ready, and nothing about the page's own measurement decides whether the press
    // hid or showed: that decision belongs to the display alone.
    internal static bool ToggleForcedFallback()
    {
        // The answer comes from the request, not from the last painted frame. `RequestedShown` reads the player's own
        // switches and this page's own hide; `Forced` is a show request a previous press already left behind and no
        // repaint has answered yet. A page asked to show is asked to hide, and a page asked to hide is asked to show -
        // so three quick presses are show/hide/show whatever the frame rate and whatever the geometry did (review #1).
        bool requestedShown = ResultTogglePolicy.RequestedShown(SwitchesWantBoard(), Session.HiddenByPress, Forced);
        bool show = ResultTogglePolicy.Press(requestedShown) == ResultBoardToggle.Show;
        ApplyBoardVisibility(show);
        Plugin.Logger?.LogInfo($"[DIAG] settlement board {(show ? "show" : "hide")} requested: requestedShown={requestedShown}, "
            + $"painted={BoardOnScreen}, forced={Forced}, hiddenByPress={Session.HiddenByPress}, switchesShown={SwitchesWantBoard()}, "
            + $"role={ResultPageRolePolicy.Name(Role())}, screen={Screen.width}x{Screen.height}; "
            + "one line per press - the request flips with each press and is not read back from the last repaint, the request is not the "
            + "presentation, and a hide restores the game's own page on the frame it is answered.");
        return show;
    }

    // The four visibility switches as the player left them. `Shows` is the one rule the overlay gate and the refresh path both
    // read, so the key can never disagree with them about what "the player asked for the board" means.
    private static bool SwitchesWantBoard()
        => ResultVisibility.Shows(
            Plugin.ShowResults == null || Plugin.ShowResults.Value,
            Plugin.ShowResultOverlay == null || Plugin.ShowResultOverlay.Value,
            Plugin.ShowImpactBoard == null || Plugin.ShowImpactBoard.Value,
            PanelBehaviour.ToggleState);

    // The one visible-state action F8 and both settlement buttons share. `show` is the *request*; the repaint that follows
    // answers it (`F8 forced frame`) or names the fact that refused it. A show deliberately keeps everything this page has
    // already measured - the placement, the strip it was fitted into and the fail-open budget - because a request that waits
    // for a valid measurement must not restart that measurement on every press (review #1/#5). A hide does drop the board's
    // own placement: the page is going back to the game on this very frame.
    private static bool ApplyBoardVisibility(bool show)
    {
        Forced = show;
        if (show) Session.MarkShown(); else Session.MarkHidden();
        // The switch half of the documented contract: the same two switches, the same saved file, on both screens. It never
        // writes `ShowImpactBoard`, so a player's own switch is still theirs.
        Plugin.PersistToggle(show);
        if (show)
        {
            // The press owes its frame an answer: spent by `ReportForcedDraw` when it ends with a board, or by the
            // forced-gated refusal lines when it ends without one.
            _forcedOutcomePending = true;
        }
        else
        {
            Invalidate();
            // A hide is not a spent budget: the next show starts from a page that has refused nothing yet (review #5 -
            // `_strictMisses` used to survive the hide, so the next show began 60 frames into a budget it never spent).
            _strictMisses = 0; _fallbackMisses = 0; _fallbackFloorScale = 1f;
            _fallbackPlaced = false; _fallbackOpacity = 1f;
            _forcedOutcomePending = false;
        }
        ClearHit();
        return show;
    }

    // The drawn half of the 1.19.22 forced-frame contract, printed once per F8 press - and 1.19.26 prints it *after* the draw,
    // never before it. `Draw` calls this only once `HudTable.Draw` has really run, so the line describes a call this build made
    // rather than a picture it hopes for; the figures it prints (the board's rectangle and the band it was fitted into) are the
    // ones that draw used. Nothing here claims a pixel was read back - no pixel readback exists in this pipeline, and the line
    // says so in as many words. The one-line-per-press mechanics are unchanged: the flag is spent here and by the refusal sites,
    // so a press can be answered neither twice nor with silence. 1.19.25: a press whose frame printed a refusal can never print
    // this second line on a later frame.
    private static void ReportForcedDraw(LayoutRect origin)
    {
        if (!Forced || !_forcedOutcomePending) return;
        _forcedOutcomePending = false;
        if (_fixedPlaced)
        {
            Plugin.Logger?.LogInfo($"[DIAG] F8 forced frame: fixed board draw issued, {_grid.Width:0}x{_grid.Height:0}px at ({origin.X:0},{origin.Y:0}), glyph={_grid.FontPixels:0}px, anchor=({FixedAnchorX():0.###},{FixedAnchorY():0.###}), opacity={_fixedPlan.Opacity:0.##}, overlay={_fixedPlan.Reason}, vanillaBlocks={(_fixedPlan.HidesVanilla ? "put away" : "kept")}, forcedByF8=true; configured bottom-edge midpoint, no adaptive band, nothing clamped or shrunk; draw-call receipt, not pixel readback.");
            return;
        }
        Plugin.Logger?.LogInfo($"[DIAG] F8 forced frame: board draw issued, {_grid.Width:0}x{_grid.Height:0}px at ({origin.X:0},{origin.Y:0}) of a {Screen.width}x{Screen.height} window, band=({_holdStrip.Top:0}..{_holdStrip.Bottom:0}), bandSource={_holdStrip.FooterSource}, forcedByF8=true; the press is answered on the repaint that issued the board's draw calls (this line reports that call, not a pixel readback of the page).");
    }
    private static bool _placed;             // a placement has been computed for the screen now up
    private static int _resolutionWidth, _resolutionHeight;
    private static float _resolutionRetryAt;
    private static bool _placedSucceeded;    // and it was computed for this kind of results screen
    internal static bool NeedsAttach()
    {
        if (!Plugin.ShowResults.Value) return false;
        if (!FixedLayoutActive() && Time.unscaledTime < _resolutionRetryAt) return false;
        var state = GameStateManager.CurrentStateName;
        if (state != eGameStateName.ExpeditionSuccess && state != eGameStateName.ExpeditionFail) return false;
        bool succeeded = state == eGameStateName.ExpeditionSuccess;
        if (_resolutionWidth != 0 && (_resolutionWidth != Screen.width || _resolutionHeight != Screen.height)) return true;
        // A placement belongs to the screen it was measured on. The success page measures its strip once, so a
        // failure placement carried onto a success page would leave the board on the failure coordinates -
        // straight over the completion icons. A different kind of results screen always re-attaches.
        return _page == null || _grid == null || ResultBoardLatch.NeedsAttach(_placed, _placedSucceeded == succeeded);
    }

    // Whether a fallback placement should spend its one second look instead of merely being refreshed.
    //
    // The recovery-readability probe is page-specific: success checks that the leave button is active and one of its
    // rectangles can be read; failure checks that `FailureStrip` can offer a strip with its current footer measured or
    // proven absent. This is deliberately not the placement's measured-button result and is not, by itself, proof that
    // both edges of a band were measured. The retry is still bounded to one per page, never one per frame.
    //
    // The other trigger is the page moving after placement: `ResultAnchorDrift` compares the strip the board used
    // against a fresh measurement, with a threshold and two samples of hysteresis, and spends at most two re-measures.
    // A board whose measured strip still describes the page is touched by neither trigger, which keeps it from jumping.
    private static bool FallbackRecoveryDue(bool succeeded, Component page)
    {
        if (page == null || !_placed) return false;
        if (_fixedPlaced) return false;
        // The budget is asked first: a page that has already spent its one retry must not pay for the measurement the
        // second look would have needed. 1.19.26 note: `AnchorReadable` is now the placement's own strip measurement,
        // so a recovery can no longer be claimed on a fact the strict placement would refuse one frame later.
        if (_placedOnFallback && ResultAnchorRecovery.ShouldRetry(_fallbackRecoveries, true, true)
            && AnchorReadable(page, succeeded))
        {
            _fallbackRecoveries = ResultAnchorRecovery.Record(_fallbackRecoveries);
            _placedOnFallback = false;
            if (HeadingReports.Add(succeeded ? "success-region-recovered" : "failure-region-recovered"))
            {
                string probe = succeeded
                    ? "the success-page leave button is active and its rectangle is readable"
                    : "the failure-page strip is readable and its footer is measured or proven absent";
                Plugin.Logger?.LogInfo($"[DIAG] {(succeeded ? "success" : "failure")} region re-measured: the current anchor-readability probe succeeded ({probe}); this is a recovery-readability check, not the placement's measured-button result or a claim that both band edges were measured. The board standing here was laid out before this probe succeeded, so the page is measured again once - and only once - and the board is re-placed against its current strip instead of the earlier guess.");
            }
            RearmSettle();
            _relayoutPending = true; _relayoutReason = "second-look";
            return true;
        }
        return AnchorDriftDue(page, succeeded);
    }

    // The recovery probe used by the second-look path above, not the strict placement proof. The success check only
    // verifies an active leave button and a trial-readable rectangle; the failure check asks `FailureStrip` for its
    // current strip and footer condition. Neither log claims that both edges of the band were read by this probe.
    private static bool AnchorReadable(Component page, bool succeeded)
        => succeeded ? SuccessButtonReadable(page) : FailureEdgesReadable(page);

    // The failure page's own strip, as the second look now asks for it: the same measurement the strict placement
    // takes, and the same two facts that placement is judged by - a strip this frame could measure at all, and a
    // footer that really bounds it.
    //
    // 1.19.26: this used to answer "the title rectangle and *a* button rectangle are finite", which a page whose leave
    // button another mod has switched off still satisfies - the button keeps its layout rectangle while the strip can
    // no longer use it. The retry that fact spent re-placed the board strictly, the next repaint could not measure the
    // same footer, `BoardStillSafe` withdrew the board on `no-footer-unproofed`, and the refunded budget armed the
    // second look again: fail-open, second look, withdraw, for ever. What the second look promises is that the strict
    // placement will now *hold*, so it is asked of the strip `FailureStrip` builds and of `ResultAnchorRecovery`.
    //
    // Alpha is still deliberately not part of it - the reveal is exactly the case the second look exists for - and
    // neither is the title's settle state: the strip only has to be offerable.
    private static bool FailureEdgesReadable(Component page)
    {
        try
        {
            if (page == null || page.TryCast<CM_PageExpeditionFail>() == null) return false;
            // Exactly the call the placement makes, with the placement's own arguments: the strip it answers with is the
            // one a strict placement would be fitted into this frame, which is the only thing the second look may spend
            // its one retry on.
            if (!FailureStrip(page, out float stripTop, out float stripBottom, out _, out bool buttonMeasured,
                out _, out _, out _, out FooterControls controls, out bool nativeFooterVisible,
                out ResultFooterSource footerSource, out _, out _)) return false;
            // The host's scan has to be able to see the page before its strip counts: the placement refuses an
            // incomplete one, so a recovery claimed on it would only be refused again.
            if (!GuestClient() && !controls.Complete) return false;
            bool offerable = float.IsFinite(stripTop) && float.IsFinite(stripBottom) && stripBottom > stripTop
                && footerSource != ResultFooterSource.None;
            // A host that could not measure the footer may only be recovered by the page's own proof that it has none to
            // measure; the client's strip is its fixed page geometry and is measured geometry by definition.
            bool footerMeasured = GuestClient() || buttonMeasured;
            bool noFooterProven = GuestClient()
                || (FailureNoFooter.Ready && controls.ProvenEmpty && !nativeFooterVisible);
            return ResultAnchorRecovery.Recovered(offerable, footerMeasured, noFooterProven);
        }
        catch { return false; }
    }

    // The page moved away from the strip the board was measured into. The sample is the same measurement the
    // placement uses, taken without any of its gates and without touching a counter, a hold or a log line of the
    // placement itself: a frame that cannot read the page's own edges is not evidence that they moved.
    private static bool AnchorDriftDue(Component page, bool succeeded)
    {
        if (!Drift.Armed) return false;
        // A success page whose own reveal is still running is not a page that moved. `SetActiveBlink` walks the completion
        // section over several frames, so its edges really do change - and spending a re-measure on that is how one
        // recorded page ended up with three different board sizes and then no board at all. Whether the reveal has finished
        // is native state (`CompletionReady`), never an elapsed time; the strip is compared again once it has, and the
        // layout-inclusive edge `SuccessRegion` builds from means the comparison is made against the final geometry.
        if (succeeded && !CompletionReady(page))
        {
            if (HeadingReports.Add("success-drift-during-reveal"))
                Plugin.Logger?.LogInfo("[DIAG] result board kept while the success page's own reveal is still running: the completion section is still being activated, so a changed edge is the native animation and not the page moving away from the board. The strip is compared again as soon as the native BlinkIn routines report completion, and no re-measure is spent on the animation.");
            return false;
        }
        if (!AnchorSample(page, succeeded, out float top, out float bottom)) return false;
        if (!Drift.Due(Time.frameCount, top, bottom - top)) return false;
        Drift.Spent();
        if (HeadingReports.Add(succeeded ? "success-region-moved" : "failure-region-moved"))
            Plugin.Logger?.LogInfo($"[DIAG] result board re-measured: the {(succeeded ? "success" : "failure")} page's own edges have moved since this board was placed - the strip it was measured into was ({_holdStrip.Top:0}..{_holdStrip.Bottom:0}) and it is ({top:0}..{bottom:0}) now - so the board is fitted again against what the page really offers, once, instead of staying at the size of a page that is no longer there. {Drift.Recoveries}/{ResultAnchorDrift.MaximumRecoveries} re-measures of this page have been spent; no F8 toggle is needed for this.");
        RearmSettle();
        _relayoutPending = true; _relayoutReason = succeeded ? "success-page-moved" : "page-moved";
        return true;
    }

    // The strip this page offers now, measured for the drift test only: no gates, no counters, no log lines, and
    // no effect on what is drawn this frame.
    private static bool AnchorSample(Component page, bool succeeded, out float top, out float bottom)
    {
        top = float.NaN; bottom = float.NaN;
        try
        {
            if (page == null) return false;
            if (succeeded)
            {
                // Exactly the edges `Placement` places from, so the comparison is against the same measurement.
                if (!SuccessRegion(page, out ResultsSafeArea successArea, out bool successButtonMeasured, out _, out _, out FooterControls successControls, out bool successNativeFooterVisible, out _, out _)) return false;
                if (!successButtonMeasured)
                {
                    if (GuestClient())
                    {
                        // 客机条带已由 SuccessRegion 的客机分支按固定页面几何算好（锚点 = 完成区实测上界），
                        // 这里直接比对同一组边界，不再二次缩放、也不问 no-footer。
                    }
                    else if (!SuccessNoFooter.Ready || !successControls.ProvenEmpty || successNativeFooterVisible
                        || !ResultNoFooterSafeArea.TryResolve(successArea.Top, Screen.height, out successArea,
                            successControls.FooterTop, MessageGap)) return false;
                }
                if (!successArea.Fits) return false;
                top = Math.Max(0f, successArea.Top) + OffsetY();
                bottom = Math.Min(successArea.Bottom, Screen.height - 8f);
                return float.IsFinite(top) && float.IsFinite(bottom) && bottom > top;
            }
            if (!FailureStrip(page, out top, out bottom, out _, out bool failureButtonMeasured, out _, out _,
                out bool failureTitleSettled, out FooterControls failureControls, out bool failureNativeFooterVisible, out _, out _, out _)
                || !failureTitleSettled || (!GuestClient() && !failureControls.Complete)) return false;
            if (GuestClient())
            {
                // 客机条带已由 FailureStrip 的客机分支算成固定几何，与 Placement 完全同源，直接比对。
            }
            else if (!failureButtonMeasured && (!FailureNoFooter.Ready || !failureControls.ProvenEmpty || failureNativeFooterVisible)) return false;
            bottom = Math.Min(bottom, Screen.height - 8f);
            return float.IsFinite(top) && float.IsFinite(bottom) && bottom > top;
        }
        catch { return false; }
    }

    // A re-measure is a first measurement: the settle state of the page's own edges and the retry cadence are re-armed
    // so the second look is exactly as strict as the first one was, and it happens on the next frame rather than on the
    // next cadence tick. Left alone, a state spent while the page was still arriving would let the second placement
    // through on a single frame's reading - the weaker gate the F8 toggle has always had.
    private static void RearmSettle()
    {
        Footer.Forget();
        Title.Forget();
        Completion.Forget();
        Retry.Forget();
        SuccessNoFooter.Forget(); FailureNoFooter.Forget();
    }

    // Whether the success page's lobby button now hands over its own geometry, by either of the two questions
    // `SuccessRegion` asks: the rectangle of the control the player can press, or the rectangle the page has laid
    // out while the reveal is still running. Alpha and active state are deliberately not part of this - the point
    // of the second look is that the geometry arrived while the visibility test still says no.
    private static bool SuccessButtonReadable(Component page)
    {
        try
        {
            var success = page != null ? page.TryCast<CM_PageExpeditionSuccess>() : null;
            if (success == null || !ButtonActive(success.m_btnLeaveExpedition)) return false;
            LayoutRect rect = default;
            if (TryScreenRect(success.m_btnLeaveExpedition, out rect)) return true;
            return VisibleRect(success.m_btnLeaveExpedition, out rect);
        }
        catch { return false; }
    }

    // Whether a page control belongs to this frame at all: switched on, in the active hierarchy. Alpha is
    // deliberately not part of it - the result pages reveal their footers from zero, and a control nobody can see
    // yet is still exactly where the player will press it - and neither is anything about geometry, which is what
    // `TryScreenRect` answers.
    private static bool ButtonActive(Component element)
    {
        if (element == null) return false;
        try { return element.gameObject.activeInHierarchy; }
        catch { return false; }
    }

    // The settlement board's wheel, sampled once per frame from the game's own readings.
    //
    // 1.19.59 replaced the IMGUI read (`EventType.ScrollWheel` in `OnGUI`) with the routing the player's other panel
    // already uses (conflictmodlist 1.1.15 `WheelRouter` + `HandleMouse`), ported in `ResultWheel.cs`: the page's native
    // `CM_PageBase.ScrollInput` first, `Input.mouseScrollDelta.y` as the labelled fallback, one of the two per frame and
    // never both, the native reading consumed by *change* (a held non-zero reading is not a second notch), three rows per
    // notch, at most five notches per frame, 0.1-per-notch platforms normalised by ten.
    //
    // Two callers ask for it - the frame driver (`PanelBehaviour.Update`) and the page's own press dispatch
    // (`SettlementPressPatch`, which runs `CM_PageBase.UpdateButtonPress`) - and `ResultScrollLatch.Claim` is what keeps
    // them to one settlement per frame, exactly like the reference's `wheelFrame`. Nothing here is written to the
    // configuration: a scroll position is not a setting.
    internal static void PollWheel()
    {
        int frame = Time.frameCount;
        // The second caller inside the same frame does not sample again: one notch is one settlement.
        if (!_scrollLatch.Claim(frame)) return;
        // 1.19.59 (independent review L5): this entry may run *before* the repaint that takes the frame's pointer sample -
        // the page's own press dispatch calls `PollWheel` and only then `DispatchPressFrame` - so the sample is taken here
        // when nobody has taken it yet. `WheelApplies` below judges the pointer against the plate the board was last drawn
        // at, and answering that from the previous frame's `_pointer` is what let the wheel and the press disagree on a
        // frame where the cursor entered or left the board. One sample per frame, whoever asks first.
        EnsurePointer();
        if (!WheelApplies(out bool succeeded, out ScrollViewport viewport, out float pitch, out float maxPixels))
        {
            // Not this frame's table (pointer left the board, the board is hidden, the page is not a settlement, nothing
            // overflows, the window lost focus). The frame's source is cleared and the consumed native fingerprint is
            // kept - the reference's `Suspend` - so a still-held native reading does not scroll when the pointer returns.
            _wheel.Suspend();
            _wheelSteps.Reset();
            return;
        }
        Component page = ChromePage();
        if (page == null) { _wheel.Suspend(); _wheelSteps.Reset(); return; }
        float native = ReadNativeScroll(page);
        float physical = ReadPhysicalWheel();
        float pixels = _wheel.Sample(native, physical, pitch, maxPixels);
        int rows = _wheelSteps.Rows(pixels, pitch, true);
        if (rows == 0) return;
        int before = _scrollFirst;
        int after = ResultsScroll.ByRows(before, _grid.Rows, ResultsScroll.FrozenRows, viewport.ScrollSlots, rows);
        if (after == before)
        {
            // The table is already against that end: the input had no effect, so the remainder it left is dropped rather
            // than banked and replayed when the other end opens (the reference clamps the same way, in pixels).
            _wheelSteps.Reset();
            return;
        }
        _scrollFirst = after;
        // A frame that moved fewer rows than it asked for (the table's foot) also gives up the rest of its remainder.
        if (after - before != rows) _wheelSteps.Reset();
        if (HeadingReports.Add("board-scrolled"))
            Plugin.Logger?.LogInfo($"[DIAG] settlement board scrolled: rows {after}..{after + viewport.ScrollSlots} of {_grid.Rows} are on the page "
                + $"({ResultsScroll.FrozenRows} frozen header row with the player names), plate {_scrollPlate:0}px at the {_grid.FontPixels:0}px glyph the page was read at; "
                + $"source={_wheel.Source} (native={native:0.###}, physical={physical:0.###}, {pixels:0.#}px -> {rows} row(s), {ResultWheelRouter.RowsPerNotch:0} rows per notch, "
                + $"at most {ResultWheelRouter.MaxNotches:0} notches per frame), pointer=({_pointer.X:0},{_pointer.Y:0}) inside ({_hitRect.X:0},{_hitRect.Y:0},{_hitRect.Width:0}x{_hitRect.Height:0}); "
                + "the wheel scrolls the table and never changes its size, and nothing here writes the config file.");
    }

    // Whether this frame's wheel may move the table at all: the same gates the press and the hover answer, asked once.
    //
    // Every refusal answers "not this frame's table": the pointer outside the plate the board was really drawn at, a
    // board that was not painted, a page that is not a settlement, the overlay switched off (F8 off), a window without
    // focus, and a table that fits its own plate. None of them withholds input from the page: the wheel is only ever
    // *read* here (the game keeps its own `ScrollInput`), so a frame this mod does not use is a frame the page still has.
    private static bool WheelApplies(out bool succeeded, out ScrollViewport viewport, out float pitch, out float maxPixels)
    {
        succeeded = false; viewport = default; pitch = 0f; maxPixels = 0f;
        if (!InputFocused()) return false;
        eGameStateName state;
        try { state = GameStateManager.CurrentStateName; } catch { return false; }
        succeeded = state == eGameStateName.ExpeditionSuccess;
        if (!succeeded && state != eGameStateName.ExpeditionFail) return false;
        if (!WheelShown()) return false;
        if (!_scrollValid || _hitSucceeded != succeeded) return false;
        if (_grid == null || _content == null) return false;
        if (!_pointer.Valid) return false;
        if (!ResultChromeLayout.Inside(_hitRect, _pointer.X, _pointer.Y)) return false;
        viewport = new ScrollViewport(_grid.Rows, ResultsScroll.FrozenRows, _scrollVisible, _scrollFirst);
        if (!viewport.Overflows) return false;
        pitch = _grid.RowHeight;
        if (!(pitch > 0f) || !float.IsFinite(pitch)) return false;
        maxPixels = Mathf.Max(pitch, _scrollPlate);
        return true;
    }

    // The four switches the board itself is gated by, with the one exception that has to agree with the draw: a player who
    // pressed F8 on this page (`Forced`) has the board on screen whatever the switches say, so the wheel over it works.
    private static bool WheelShown()
    {
        if (Forced) return true;
        if (!PanelBehaviour.ToggleState) return false;
        if (Plugin.ShowResults != null && !Plugin.ShowResults.Value) return false;
        if (Plugin.ShowResultOverlay != null && !Plugin.ShowResultOverlay.Value) return false;
        if (Plugin.ShowImpactBoard != null && !Plugin.ShowImpactBoard.Value) return false;
        return true;
    }

    // Whether this window has the input focus. Read only, and a build that cannot answer is treated as focused (the rest
    // of the gates still apply), which is the same answer the reference's `Focused()` gives.
    private static bool InputFocused()
    {
        try { return Application.isFocused; } catch { return true; }
    }

    // The page's own wheel reading (`CM_PageBase.ScrollInput`): the primary source. A build that cannot read it answers
    // zero, which is exactly "the physical wheel decides this frame" - never a second read of the same notch.
    private static float ReadNativeScroll(Component page)
    {
        try
        {
            var menu = page != null ? page.TryCast<CM_PageBase>() : null;
            if (menu == null) return 0f;
            float value = menu.ScrollInput;
            return float.IsFinite(value) ? value : 0f;
        }
        catch (Exception error)
        {
            if (!_loggedNativeScroll) { _loggedNativeScroll = true; Plugin.Logger?.LogWarning("[DIAG] the result page's own scroll reading could not be read, so the physical wheel decides this frame: " + error.Message); }
            return 0f;
        }
    }

    private static bool _loggedNativeScroll;

    // The one physical wheel read in this assembly: Unity's `mouseScrollDelta.y`, read and never captured (no cursor
    // hiding, no lock, no event consumption, no global input state touched).
    private static float ReadPhysicalWheel()
    {
        try
        {
            Vector2 delta = Input.mouseScrollDelta;
            return float.IsFinite(delta.y) ? delta.y : 0f;
        }
        catch { return 0f; }
    }

    // A new page, a new window, a board that stopped being drawn: the router forgets the consumed fingerprint with it.
    private static void ResetWheel()
    {
        _wheel.Reset();
        _wheelSteps.Reset();
        _scrollLatch.Forget();
    }

    // ---- the influence board -------------------------------------------------------------------

    // ---- the influence board -------------------------------------------------------------------

    private static void PrepareResolutionBoard(Component page, bool succeeded)
    {
        ImpactReport report = BuildReport(succeeded);
        if (report.Entries.Count == 0) { ResolutionRefused("empty-roster"); return; }
        ImpactBoardLayout board = ImpactBoard.Build(report, splitKillTiers: true, succeeded);
        ResultsSaver.Save(page, succeeded, board, report, CombatSync.Generation, HostProven());
        int width = Screen.width, height = Screen.height;
        if (!ResultAutoBoardContract.TryResolutionStrip(width, height, out BoardStrip strip))
        { ResolutionRefused("invalid-window"); return; }
        var content = new TableContent
        {
            Columns = board.Columns, Text = board.Text, Colors = Colors(board, report),
            FieldSeparator = ImpactBoard.ValueSeparator, Dividers = Dividers(board), Kinds = board.Kinds, Figure = board.Figure
        };
        int pixels = ResultAutoBoardContract.TargetGlyphPixels(height);
        PanelGrid grid = ResultAutoBoardContract.ConstrainWidth(BoardGrid(content, pixels, pixels, float.PositiveInfinity),
            width - 2f * ResultsBounds.ScreenMargin);
        float header = ChromeHeader();
        if (grid == null || !ResultsScroll.PlateFits(strip.Bottom - strip.Top, grid.RowHeight, header, ResultsScroll.MinimumVisibleRows))
        { ResolutionRefused("viewport-too-small"); return; }
        float plateHeight = PlateFor(grid, strip.Bottom - strip.Top, header, out int visible, out int first);
        float edge = ResultsBounds.ScreenMargin;
        float maxX = Math.Max(edge, width - edge - grid.Width);
        float x = Math.Clamp((width - grid.Width) * 0.5f + Plugin.ResultsOffsetX.Value, edge, maxX);
        if (!ResultBoardHold.TryPlace(width, height, strip, grid.Width, plateHeight + header,
                x - (width - grid.Width) * 0.5f, out LayoutRect plate, bottomAligned: true))
        { ResolutionRefused("plate-outside-window"); return; }

        _grid = grid; _content = content;
        _origin = new LayoutRect(plate.X, plate.Y + header, plate.Width, plateHeight);
        _scrollPlate = plateHeight; _scrollVisible = visible; _scrollFirst = first;
        _holdBoard = plate; _holdStrip = strip; _holdValid = true; _holdPage = page.Pointer;
        if (_resolutionWidth != width || _resolutionHeight != height) _loggedDraw = false;
        _resolutionWidth = width; _resolutionHeight = height;
        _resolutionRetryAt = 0f;
        _placed = true;
        _placedSucceeded = succeeded;
    }

    private static void ResolutionRefused(string reason)
    {
        // Keep a standing board on a transient empty snapshot, but never paint it in a different window.
        _resolutionRetryAt = Time.unscaledTime + 0.25f;
        if (_resolutionWidth != Screen.width || _resolutionHeight != Screen.height) RestoreHidden();
        ReportHeldBack("prepare", "resolution-" + reason, $"screen={Screen.width}x{Screen.height}");
    }

    private static void PaintResolutionBoard()
    {
        HudTable.Draw(_grid, _origin.X, _origin.Y, _content, 1f, _scrollFirst, _scrollVisible, _scrollPlate);
        DrawOverflowHint();
        _boardDrawnFrame = Time.frameCount;
        _hitRect = _holdBoard; _hitSucceeded = _succeeded; _hitValid = true; _scrollValid = true;
        try { ApplyVanillaBlocks(_page, _succeeded); }
        catch (Exception error)
        {
            if (HeadingReports.Add("resolution-board-vanilla"))
                Plugin.Logger?.LogWarning("[DIAG] could not update the settlement page's optional blocks: " + error.Message);
        }
        ReportForcedDraw(_origin);
        if (!_loggedDraw)
        {
            _loggedDraw = true;
            Plugin.Logger?.LogInfo($"[DIAG] resolution settlement board: screen={_resolutionWidth}x{_resolutionHeight}, "
                + $"band=({_holdStrip.Top:0}..{_holdStrip.Bottom:0}), glyph={_grid.FontPixels:0}px, "
                + $"rows shown {_scrollVisible}/{_grid.Rows}, role={ResultPageRolePolicy.Name(Role())}, success={_succeeded}; draw calls issued.");
        }
    }

    private static void Prepare(Component page, bool succeeded)
    {
        // Two floors, two questions, deliberately kept apart.
        //
        // `_readableFloor` is what the table wants: its own row count at the smallest *readable* glyph, 270 px at
        // compact for the split board. It is the figure the fit aims at, and a page that can hold it gets a table the
        // player can read.
        //
        // `_boardFloor` is what the page is *judged* against, and it is the height this table can actually be drawn at
        // in the room the page measured (`ResultsFontGrowth.DrawableBoardHeight`). Judging the page by the readable
        // floor is what turned a 269 px strip into no board at all while a 286 px strip drew a 6 px one: the same
        // page, seventeen pixels apart, and nothing in between. The room is only known after `Placement`, so the
        // judgement figure is refreshed from the strip this frame measured, and stays on the readable floor for every
        // frame whose geometry could not be read - exactly the behaviour a page with no measurement had before.
        //
        // Both are computed for the split board and re-derived below once `Placement` has answered, because the row
        // count itself depends on the room (see `splitTiers`).
        _readableFloor = ResultsFontGrowth.MinimumBoardHeight(ImpactBoard.RowCount(true),
            HudTable.RowEm(ResultsFontGrowth.MinimumReadablePixels), Plugin.Density);
        _boardFloor = _readableFloor;
        // The strict placement first, exactly as it has always been: the page's own settled anchor, its measured
        // footer, its complete interaction scan. **But a placement is only a placement when the fit really puts a board
        // on the page.** `Placement` answers "the page offered a strip", and a strip can be shorter than the board's own
        // floor (30 rows x 3 px x 1.5 em = 135 px) while still being non-empty; a page answering `true` there used to
        // clear the refusal count and then have `Fit` refuse in the very same frame - for ever, with F8 unable to help,
        // because `ShouldFallback` was never evaluated. The count is therefore spent by what really happened: `TryFit`
        // below, never by `Placement` on its own.
        //
        // 1.19.45（固定模式，[Results] FixedLayout）：玩家自己指定字号与「表格底边中点」时，这一整条自适应链（页面
        // 条带、下界墙、稳定门、fail-open）都不参与——它读的是窗口和玩家自己的数字。`Placement` 因此根本不被调用：
        // 固定模式不会被客机失败页的下界墙等待卡住。控件安全检查没有取消，它在 `PlaceFixedLayout` 里单独做，
        // 而且是每一帧都做。
        bool fixedLayout = FixedLayoutActive();
        bool strict = false;
        float room = 0f;
        BoardStrip strip = default;
        if (!fixedLayout) strict = Placement(page, succeeded, out _, out room, out strip);
        // The row count the board is built with, decided once per frame from the room the page measured. On every
        // page that can hold the split board this is the 30 rows it has always been; on a settlement page whose band
        // `SplitKillTiersFor` keeps the split board for an unmeasured room, for a room too short for either board and
        // for a room where the two rows buy no glyph pixel at all, so a page this rule cannot help keeps exactly the
        // content it had.
        float rowEm = HudTable.RowEm(ResultsFontGrowth.MinimumReadablePixels);
        // 固定模式没有「条带给的房间」这一说：行数按完整看板固定，好让同一组配置在任何窗口、任何分辨率上都是同一张表。
        bool splitTiers = fixedLayout || ImpactBoard.SplitKillTiersFor(strict ? room : float.NaN,
            ResultsBandFit.EmPerGlyphPixel(Plugin.Density, rowEm));
        int rows = ImpactBoard.RowCount(splitTiers);
        // The pair is re-derived for the row count this frame really uses, so every figure quoted below - the fit's
        // target, the page's judgement, the fail-open ladder and the log lines - describes the same table.
        _readableFloor = ResultsFontGrowth.MinimumBoardHeight(rows, rowEm, Plugin.Density);
        _boardFloor = strict && float.IsFinite(room) && room > 0f
            ? ResultsFontGrowth.DrawableBoardHeight(rows, rowEm, Plugin.Density, room)
            : _readableFloor;
        ImpactReport report = BuildReport(succeeded);
        if (report.Entries.Count == 0)
        {
            if (ResultReportPolicy.OncePerPage(HeadingReports, "empty-roster", Forced, _forcedOutcomePending))
                Plugin.Logger?.LogWarning("[DIAG] result board waiting: no retained player counters." + (Forced ? " forcedByF8=true - this press is answered by this refusal line, never silence." : string.Empty));
            // Spent like every other refusal line: this frame answered the press, so no later frame prints a second,
            // false "board drawn" line for it, and the held F8 stays on the per-page latch until the next press.
            if (Forced) _forcedOutcomePending = false;
            return;
        }
        // The settlement board's kill tiers are explicit whenever the room allows them (`splitTiers`), which is the
        // only case in which this build ever collapsed them.
        ImpactBoardLayout board = ImpactBoard.Build(report, splitTiers, succeeded);
        // The initial accepted projection is archived; later snapshots refresh the same file in place.
                ResultsSaver.Save(page, succeeded, board, report, CombatSync.Generation, HostProven());
        var content = new TableContent
        {
            Columns = board.Columns,
            Text = board.Text,
            Colors = Colors(board, report),
            FieldSeparator = ImpactBoard.ValueSeparator,
            Dividers = Dividers(board),
            Kinds = board.Kinds,
            // 1.19.62: the round's created-enemy figure travels with the table it belongs to, so the header band draws
            // the same string the board model produced - one line for the whole squad, never one per column.
            Figure = board.Figure
        };
        // The strict attempt is a strict attempt: whatever the previous frame ended in, this one asks the fit to place
        // inside the strip the page itself measured.
        _fallbackPlaced = false;
        if (fixedLayout)
        {
            // 固定模式：字号就是玩家指定的像素值，位置就是玩家指定的底边中点。只有画不出来的输入才拒绝，并在
            // 那一行里说清是哪一项（字号、锚点、窗口、算出的矩形、原因）；既不改玩家的数字，也不隐藏原版块。
            // 真实相交与证据不足不拒绝：板照画，淡淡与原版块由 `ResultFixedOverlayPolicy` 决定。
            if (!PlaceFixedLayout(content, board, report, succeeded, out string fixedRefusal))
                ReportHeldBack("prepare", "fixed-layout-refused", fixedRefusal,
                    FixedSignature(Screen.width, Screen.height, FixedFontPixels(), FixedAnchorX(), FixedAnchorY(), 0f, 0f));
            return;
        }
        string refusal = "reason=placement-refused,room=unmeasurable,source=" + strip.FooterSource;
        bool placed = strict && TryFit(content, board, report, strip, room, succeeded, out refusal);
        if (ResultPlacementPolicy.Decide(placed, _strictMisses, Forced) == ResultPlacementOutcome.PlacedStrict)
        {
            // A board really stands: only now is the refusal count cleared, the fail-open ladder reset (the page has proved
            // it can hold the real board) and the fail-open flag dropped.
            _strictMisses = 0;
            _fallbackMisses = 0;
            _fallbackFloorScale = 1f;
            _fallbackOpacity = 1f;
            FinishPlacement(succeeded, strip);
            return;
        }
        // could not draw into, and the count is spent here - after the fit, never before it. The same frame may already
        // be the frame the fail-open path takes over (F8, or a budget that this refusal completed), so it is decided
        // once, here, and never a frame later.
        _strictMisses = ResultPlacementPolicy.Spend(_strictMisses);
        ResultPlacementOutcome refused = ResultPlacementPolicy.Decide(false, _strictMisses, Forced);
        if (refused == ResultPlacementOutcome.Deferred)
        {
            // Say why the frame was empty, once per page and per reason: a strip that is merely short and a grid the
            // fit refused are two different faults and used to look identical.
            ReportHeldBack("prepare", strict ? "fit-refused" : "strip-refused", refusal);
            return;
        }
        // May the fail-open path answer this frame?
        //
        // Two questions, asked in one place (`ResultSettlementGate`), because the recorded success page answered the
        // second one alone and drew a board the page then moved:
        //
        //   1. the page's own native reveal. `ResultRevealGate` was wired into the strict placement only, so the fail-open
        //      band could answer after sixty refused frames while the completion ledger still read 0/6 - and the band it
        //      used was the completion section's *container* edge (313 on the recorded page), which only becomes the
        //      section's edge once the reveal has switched it on. The reveal then delivered the caption edge at 352, the
        //      standing board was re-measured 39 px lower and the log wrote "region re-measured": the first thing the
        //      player saw was a board that then changed. So the reveal gate decides this too, with the escapes it already
        //      has: F8 (`Forced`) never waits, and a ledger that can no longer complete answers at once. A page whose
        //      reveal was *never observed at all* is released by the bounded wait this file already has
        //      (`ResultFinalPolicy.MaximumWaitFrames`) and the line says it was a reveal-unobservable fail-open, never
        //      that the page settled.
        //   2. the page's final geometry, unchanged (`ResultFinalPolicy`): a page that never handed it over has nothing to
        //      wait for, one that settled is answered because it settled, one that merely dropped a read is not treated
        //      as a page with no geometry, and one that never stops moving - or goes blind for good after measuring - is
        //      given up on after the bound *and says so*, never as "settled".
        //
        // Sixty refused frames are not evidence that a page has finished drawing its reveal, which is why the fail-open
        // budget is not what answers a success page whose reveal is still running.
        int finalWait = FinalWaitFrames();
        bool revealStalled = RevealStalled(out float stallSeconds, out float stallLimit);
        ResultSettlementGate.Decision settlement = ResultSettlementGate.Decide(Forced, succeeded, RevealFinished,
            CompletionTargets.Ledger.RevealImpossible, CompletionTargets.Ledger.InFlight, CompletionTargets.Ledger.Count,
            revealStalled, _finalEverMeasurable, _finalMeasurable, FinalLayout.Ready, finalWait);
        if (!settlement.MayFailOpen)
        {
            if (settlement.Why == ResultSettlementGate.Answer.RevealPending)
                ReportHeldBack("prepare", "reveal-pending",
                    ResultSettlementGate.Why(settlement.Why, CompletionTargets.Ledger.Completed, CompletionTargets.Ledger.Count,
                        CompletionTargets.Ledger.InFlight, stallSeconds, stallLimit)
                    + ",role=" + ResultPageRolePolicy.Name(Role()));
            else
                ReportHeldBack("prepare", "final-geometry-pending",
                    ResultFinalPolicy.WhyWaiting(_finalEverMeasurable, _finalMeasurable, FinalLayout.Ready, FinalLayout.Top, FinalLayout.Bottom, _boardFloor, finalWait)
                    + ",role=" + ResultPageRolePolicy.Name(Role()));
            return;
        }
        if (settlement.Unobservable && HeadingReports.Add("fail-open-reveal-unobservable"))
            Plugin.Logger?.LogWarning("[DIAG] settlement board: "
                + ResultSettlementGate.Why(settlement.Why, CompletionTargets.Ledger.Completed, CompletionTargets.Ledger.Count,
                    CompletionTargets.Ledger.InFlight, stallSeconds, stallLimit)
                + ", role=" + ResultPageRolePolicy.Name(Role())
                + ",wallPending=" + ResultClientPageActionPolicy.Pending(float.NaN, float.NaN).ToString().ToLowerInvariant() + ", forcedByF8=" + Forced.ToString().ToLowerInvariant()
                + "; this is the only claim made about this page: it is fail-open because the reveal made no progress for the whole "
                + "stall bound (never observed, aborted with routines still running, or scheduled and simply never finishing), "
                + "not because anything settled, "
                + "and the board is measured again at the bounded cadence while every control the page measures is still stepped around.");
        if (settlement.Why == ResultSettlementGate.Answer.GivenUp && HeadingReports.Add("final-geometry-given-up"))
            Plugin.Logger?.LogWarning("[DIAG] settlement board: " + ResultFinalPolicy.WhyGivenUp(FinalLayout.Top, FinalLayout.Bottom, finalWait, _finalMeasurable)
                + "; the page is measured again at the bounded cadence and every control it measures is still stepped around.");
        if (!FallbackPlacement(page, succeeded, out _, out room, out strip, out string fallbackWhy))
        {
            // The fail-open placement found no band this frame - either because the page's own scan could not read it
            // (the one refusal this escape may not answer, see `ResultFallbackPolicy.PageReadable`) or because even the
            // window's own safe area could not hold the board. Two different faults, two different lines, both once per
            // page; the floor ladder must not keep asking the same impossible question.
            _fallbackMisses = ResultPlacementPolicy.Spend(_fallbackMisses);
            bool pageReadable = !fallbackWhy.StartsWith(ResultFallbackPolicy.UnreadablePage, StringComparison.Ordinal);
            // A failure page whose own lower bound has not been observed yet is a third, separate fault: the scan was
            // complete and the band was still not invented from the window (`ResultFailureWallPolicy`).
            bool wallUnobserved = fallbackWhy.StartsWith(ResultFailureWallPolicy.Unobserved, StringComparison.Ordinal);
            ReportHeldBack("prepare", wallUnobserved ? "fail-open-wall-unobserved"
                    : pageReadable ? "fallback-unavailable" : "fail-open-page-unreadable",
                fallbackWhy + ", misses=" + _fallbackMisses + ",floor=" + FallbackFloor().ToString("0") + "px");
            return;
        }
        // review #4: a host failure page may stand a board on the window's own foot, but not on nothing at all. `Top` of the
        // fail-open band is the page's anchor (its title layout, its waiting line), and `AnchorMeasured` says whether that
        // anchor was really read this frame. Sixty refused frames are a budget for "this page offers no measured lower
        // bound", never a licence to invent the whole band from the window and call it the board's first appearance - which
        // is the 330x540 @ ScreenSafeArea placement the attachment shows one frame before the page's own 488..1139 band
        // arrived. F8 (`Forced`) is the player's own answer and still overrides it.
        if (!Forced && !succeeded && !GuestClient() && !strip.AnchorMeasured)
        {
            _fallbackMisses = ResultPlacementPolicy.Spend(_fallbackMisses);
            ReportHeldBack("prepare", "fail-open-unanchored",
                "band=(" + strip.Top.ToString("0") + ".." + strip.Bottom.ToString("0") + ") source=" + strip.FooterSource
                + ", anchorMeasured=False, misses=" + _fallbackMisses + ", floor=" + FallbackFloor().ToString("0") + "px");
            return;
        }
        _fallbackPlaced = true;
        if (!TryFit(content, board, report, strip, room, succeeded, out refusal))
        {
            _fallbackPlaced = false;
            // The fail-open path could not draw either. Said once per page and per reason - never per frame - and the floor
            // ladder steps down so the next measurement accepts a shorter band and builds a smaller table; it stops at the
            // shortest grid this build can draw, at which point the page really has no room and the line says so.
            _fallbackMisses = ResultPlacementPolicy.Spend(_fallbackMisses);
            float before = FallbackFloor();
            _fallbackFloorScale = ResultFallbackLadder.Next(_fallbackFloorScale);
            bool exhausted = FallbackFloor() <= before + 0.01f || before <= ResultsBandFit.MinimumDrawableHeight(ImpactBoard.RowCount(true), Plugin.Density) + 0.01f;
            ReportHeldBack("prepare", exhausted ? "fallback-floor-exhausted" : "fallback-fit-refused",
                refusal + ", " + fallbackWhy + ", misses=" + _fallbackMisses + ",floor=" + before.ToString("0")
                + "px,next=" + FallbackFloor().ToString("0") + "px,opacity=" + _fallbackOpacity.ToString("0.00")
                + (exhausted ? ", the page has no room for even the shortest board this build can draw" : ""));
            return;
        }
        if (HeadingReports.Add("fallback-" + (succeeded ? "success" : "failure")))
            Plugin.Logger?.LogInfo($"[DIAG] {(succeeded ? "success" : "failure")} board placed from the fail-open safe area: "
                + $"{fallbackWhy}, strictRefusals={_strictMisses} of {ResultFallbackPolicy.MissBudget} frames before the budget is spent, "
                + $"forcedByF8={Forced}, role={ResultPageRolePolicy.Name(Role())}; the page's own anchors could not be proven, so the screen's "
                + "own geometry is used and the page's controls are stepped around instead of the board being dropped.");
        // 1.19.55: the fail-open band is the mature board's own escape (it measures the page, steps around its controls and
        // fades rather than vanishing), so a placement made here is not the early, non-interactive one. Saying so is what lets
        // this frame's earlier `Placement` answer stand for nothing: the board that stands is this one.
        _earlyBoard = false;
        FinishPlacement(succeeded, strip);
    }

    // How many frames this page has been waiting for its final geometry to settle.
    //
    // The clock starts on the first frame of a page that has measured its strip at least once, runs while the latch is
    // closed, and is dropped the moment the page settles. A frame in which a page that *did* measure goes blind keeps
    // the clock running instead of resetting it: "the page stopped answering" is a reason to wait for the bound, not a
    // reason to place a board against the screen. A page that never measured anything never starts the clock at all, so
    // the client and failure paths that have always answered immediately still do.
    //
    // It is a count of *frames spent waiting*, never a substitute for the stability the gate itself proves: a page
    // answered by the give-up is logged as given up, not as settled.
    private static int FinalWaitFrames()
    {
        if (!_finalEverMeasurable || FinalLayout.Ready)
        {
            _finalFirstFrame = -1;
            return 0;
        }
        if (_finalFirstFrame < 0) _finalFirstFrame = Time.frameCount;
        return Math.Max(0, Time.frameCount - _finalFirstFrame);
    }

    // How many frames this failure page has shown no lower bound at all.
    //
    // `ResultFailureWallPolicy` withholds the invented band while this is zero-ish, and answers after
    // `UnobservedBudget` - the same bounded give-up this file already uses (`ResultFinalPolicy.MaximumWaitFrames`), and
    // the answer is logged as a wall that was never observed rather than as a settled page. A page that does hand over a
    // bound clears the clock, so nothing is ever released early on account of a reading that has since arrived.
    private static int FailureWallWaitFrames(bool wallMeasured)
    {
        if (wallMeasured)
        {
            _failureWallFirstFrame = -1;
            return 0;
        }
        if (_failureWallFirstFrame < 0) _failureWallFirstFrame = Time.frameCount;
        return Math.Max(0, Time.frameCount - _failureWallFirstFrame);
    }

    // The page's own lower bound, from the readings each path actually has. One definition, so the strict path that
    // *starts* the clock (F5) and the fail-open path that reads it cannot disagree about what "a wall was seen" means.
    // A reading that is not finite is not a wall; a control the page has switched off is not one either (the scan
    // records those as hidden, and `PressableTop`/`FooterTop` only carry controls that really answered).
    private static bool FailureWallObserved(float namedButton, float scanPressable, float scanFooter)
        => float.IsFinite(namedButton) || float.IsFinite(scanPressable) || float.IsFinite(scanFooter);

    // How long this page's reveal has made no progress, in real seconds (F1-STALL), and whether that is the whole bound.
    //
    // Every call feeds the clock with the ledger's own facts - completed, in flight, scheduled - so *any* native movement
    // (a routine starting, a routine finishing, a target entering the ledger) restarts it: a reveal that is really
    // running is never released by time. The bound comes from the page's own blink duration (`CoroutineManager.
    // BlinkDuration`), never from a frame count, so the answer is the same at 60 and at 134 FPS.
    private static bool RevealStalled(out float stalledSeconds, out float limitSeconds)
    {
        var ledger = CompletionTargets.Ledger;
        float now = Time.realtimeSinceStartup;
        RevealClock.Observe(now, ledger.Completed, ledger.InFlight, ledger.Count);
        limitSeconds = RevealProgressClock.LimitFor(BlinkSeconds());
        stalledSeconds = RevealClock.StalledSeconds(now);
        // A reveal the ledger has finished is not stalled; a page whose clock was never fed is not stalled either.
        return !RevealFinished && RevealClock.Exhausted(now, limitSeconds);
    }

    // One blink's native length, from the game itself; a build that cannot answer it falls back to the clock's own
    // documented assumption, so the bound never depends on a number this file invents.
    private static float BlinkSeconds()
    {
        try { return CoroutineManager.BlinkDuration; } catch { return float.NaN; }
    }

    // F3: whether the measured content may already stand in for the section's final edge, before the native reveal
    // finishes.
    //
    // Only when the ledger has itself proved that no reveal is coming: every target it was handed was proven
    // unscheduled (so nothing will ever blink on this page), or the ledger can no longer read `Ready` and nothing of its
    // own is still running. The page's own geometry latch must also have read the same twice (`FinalLayout.Ready`), so
    // a frame taken while the page is still laying itself out cannot be used. A page whose reveal was never even
    // observed (`Required == 0`) is deliberately **not** covered: nothing proves an animation will not start later, so
    // the container stands and the per-part line says `source=container(reveal-running)`.
    private static bool ContentSettled
    {
        get
        {
            var ledger = CompletionTargets.Ledger;
            bool noRevealComing = (ledger.Required > 0 && ledger.Count == 0)
                || (!ledger.MayStillComplete && ledger.Settled);
            return noRevealComing && FinalLayout.Ready;
        }
    }

    // One frame's answer about the page's own final geometry: whether it could be read now, and whether this page has
    // ever been able to read it. The second fact is the latch - it is set and never cleared for a page, and `ForgetPage`
    // is the only thing that drops it.
    private static void NoteFinalGeometry(bool measurable)
    {
        _finalMeasurable = measurable;
        if (measurable) _finalEverMeasurable = true;
    }

    // One attempt with one strip: the grid this room allows, and the fit that really puts it on the page.
    //
    // Answers whether a board stands now - which is the only thing the refusal count may be cleared by - and says why
    // when it does not: an empty room, a grid nobody could measure, or a fit that refused the grid it was given.
    private static bool TryFit(TableContent content, ImpactBoardLayout board, ImpactReport report, BoardStrip strip,
        float room, bool succeeded, out string refusal)
    {
        refusal = "room=" + (float.IsFinite(room) ? room.ToString("0") + "px" : "unmeasurable")
            + ",boardFloor=" + (float.IsFinite(_boardFloor) ? _boardFloor.ToString("0") : "unmeasured")
            + ",source=" + strip.FooterSource;
        if (!(room > 0f)) { refusal = "reason=strip-empty," + refusal; return false; }
        float boardWidth = Mathf.Max(320f, Screen.width - 48f);
        // The header the close stands in is reserved out of the strip before the table is fitted, so the board -
        // table plus header - is still exactly as tall as the band the page measured. A strip so short that the
        // header would leave no room at all keeps the old room; the placement below then refuses it on its own
        // numbers rather than drawing a board taller than its band.
        float header = ChromeHeader();
        float tableRoom = room - header;
        if (!(tableRoom > 0f)) tableRoom = room;
        PanelGrid grid = FitBoard(content, tableRoom, boardWidth, succeeded, strip.ButtonHeight, strip.FillCeiling);
        if (grid == null)
        {
            refusal = "reason=grid-unmeasurable,rows=" + content.Rows + ",columns=" + content.ColumnCount + "," + refusal;
            return false;
        }
        // 1.19.59: a band that cannot hold the board's own header *and* two whole rows has nothing to draw. 1.19.58 placed
        // a board into it anyway (`PlateFor` reported two rows, `PlateHeight` clipped the plate to the room), so a 48px band
        // drew a 36px header and one 12px slot with three pixels of the second row showing - the "标题 + 碎行" the player
        // reported. A refused band leaves the vanilla page exactly as the game drew it (and the fail-open path still has its
        // own band), which is the honest answer: no board is better than a board nobody can read.
        if (!ResultsScroll.PlateFits(room, grid.RowHeight, header, ResultsScroll.MinimumVisibleRows))
        {
            refusal = "reason=plate-below-" + ResultsScroll.MinimumVisibleRows + "-rows,rowHeight=" + grid.RowHeight.ToString("0.##")
                + ",header=" + header.ToString("0") + "," + refusal;
            return false;
        }
        if (!ResultBoardLatch.Placed(Fit(strip, grid, content, board, report, succeeded, header), _grid != null, _content != null))
        {
            refusal = "reason=fit-refused,grid=" + grid.Width.ToString("0") + "x" + grid.Height.ToString("0")
                + ",strip=(" + strip.Top.ToString("0") + ".." + strip.Bottom.ToString("0") + ")," + refusal;
            return false;
        }
        return true;
    }

    // What a placed board records about itself, whether it stands inside the page's own strip or in the fail-open band.
    private static void FinishPlacement(bool succeeded, BoardStrip strip)
    {
        _placed = true; _placedSucceeded = succeeded;
        // A strip whose three facts were all measured is not a guess and the board is never re-measured away from it:
        // the anchor it hangs from in its settled state, the button's own top edge, and that button's height - which is
        // the one figure the readability floor is taken from and which only exists when the bound came from a named
        // control rather than from the drawn band of everything the page put at its bottom.
        // A board standing in the band below the page's actions is not a guess either: that band is measured geometry,
        // and the second look exists for a strip that was laid out before one of the page's own edges could be read.
        _placedOnFallback = strip.FooterSource != ResultFooterSource.ClientNoFooterSafeArea
            && strip.FooterSource != ResultFooterSource.BelowActions
            && ResultAnchorRecovery.LatchFallback(_fallbackRecoveries,
                strip.ButtonMeasured && strip.AnchorMeasured && float.IsFinite(strip.ButtonHeight));
    }

    // The glyph size the settlement board is drawn at, and the grid that size produces.
    //
    // The strip between the page's completion section and the lobby button is usually far taller than the table
    // needs, and it is occasionally shorter than the sharing size wants, so the size is searched rather than
    // guessed: `ResultsFontGrowth.FitIntoStrip` measures the table at a candidate size and answers with the
    // largest one that fits - down as well as up. Measuring and then squeezing the table into the box, which
    // is what a climb that refuses to look downwards does, is how a board ended up drawn at ~9px with the top
    // of its strip left empty.
    //
    // `NaturalSize` is the measurement and the box is what is being chosen here, so the table is measured
    // with no box at all. What comes out of it is drawn at exactly the width its own cells measured: the row
    // label keeps its own width and every value column is given the widest of them (HudTable's settlement
    // rule), so a wider page leaves the board its own size instead of stretching it across the page. Neither
    // the fit nor that rule can push the board over the icons, the title or the button: the fit's own
    // ceiling stays below the strip and inside the width the page offers.
    // `succeeded` picks which of the two result pages' wheel preference the board is drawn at.
    // `fillCeiling` is the share of the room the board may use, and it comes from the strip the board is being fitted
    // into (`BoardStrip.FillCeiling`): the historical comfort band above a control the player has to press, or - on a
    // client page whose band already stops a clearance above its waiting-status block - the whole band, because
    // nothing under that foot is left for the ceiling to protect.
    // 1.19.54（输入契约）：这一个参数是**正文高度**——即“板子可以给表格的那一段”，已经扣掉板子自己在上方保留的标题
    // （`ChromeHeader()`）。它以前在两个严格调用点（`TryFit` 与重排）就是这么传的（`tableRoom = room - header`），
    // 1.19.53 又在函数内部无条件再扣一次，于是成功页与失败页都被**双重扣减**；只有 fail-open 的重拟合传的是整条带高。
    // 现在的契约是“恰好扣一次，由调用方负责”，函数内部**不再扣**：
    //   * `TryFit`（两页共用）与重排路径：`tableRoom = room - header`（1.19.50 原样）；
    //   * fail-open 的重拟合（`RefreshFallbackBoard`）：成功页补扣标题，失败页原样传整条带（1.19.50 原样）。
    // 失败页因此与 1.19.50 逐字等价：同一个 `tableRoom`、同一条 `FloorForBand`/`FitBoardIntoStrip` 调用。
    private static PanelGrid FitBoard(TableContent content, float bodyRoom, float boardWidth, bool succeeded, float buttonHeight = float.NaN,
        float fillCeiling = ResultsFontGrowth.StripFillCeiling)
    {
        int seed = Mathf.Max(1, PanelBehaviour.FontSizeForSharing);
        // The readability floor is taken from the button the player has to press. A strip that is short
        // leaves the table no room to grow, but it may not shrink it to a size nobody can read while that
        // control is right underneath: the floor travels into the same build the search measures with, so
        // the glyph size and the row height the board is drawn at honour it. A page that hands no measurable
        // button height keeps the absolute minimum, and every figure here is what it was before.
        int requested = ResultsFontGrowth.ReadabilityFloor(buttonHeight);
        // ---- band height -> row height / glyph size ---------------------------------------------------------
        //
        // The band is the promise; the glyph size is what gets spent on it. `ResultsBandFit` turns the room the page
        // gave into a row budget (`room / rows`) and then into a glyph size:
        //
        //   rowHeight = max(font * RowFactor * density.Row, font * 1.5em)     what HudTable really builds a row at
        //   font      = floor(bandHeight / rows / thatEmPerGlyphPixel)
        //
        // At the compact density that is 19.6 px of row for the 588 px band of a 1440 window over 30 rows, i.e. a
        // 13 px glyph - the figure the real page logged as `glyph 12px`/`13px`. A 200 px band, which is what a page
        // squeezed between a tall icon block and an early footer can really offer, still yields a 4 px glyph over
        // 30 rows (30 x 6 = 180 px). That is below the 6 px the player would like, and that is the trade: the band
        // may not be crossed, and the page may not be left without a table, so the readability floor is lowered to
        // the band's own answer and the row shrinks with it. The measured search below then refines that answer
        // against the real text widths, and the grid is drawn at whatever it produced.
        // `bodyRoom` is the height the *table* may use - the caller has already taken the header out - so nothing is reserved here: the
        // search's answer is the one `PlateFor`/`TryPlace` accept, because both of them hold `plate + header` against the
        // whole band and the header has been taken out exactly once, by the caller.
        int bandFloor = ResultsBandFit.FloorForBand(bodyRoom, content.Rows, Plugin.Density, requested, out bool squeezed);
        if (squeezed && HeadingReports.Add("band-squeezed"))
            Plugin.Logger?.LogInfo($"[DIAG] settlement board wider than its band would like: body={bodyRoom:0}px for {content.Rows} rows would allow a {bandFloor}px glyph, "
                + $"below the {requested}px this page would have liked; the table keeps the readable glyph and the rows that no longer fit are "
                + $"reached with the wheel instead of being squeezed into a size nobody can read. role={ResultPageRolePolicy.Name(Role())}.");
        // A band that cannot hold the comfort glyph has already been answered by `FloorForBand` with its own
        // figure (`squeezed`), and that figure is the largest one this page can hold. Fitting the table against
        // the historical 96% comfort ceiling on top of it asks for less room than the band allows, so the search
        // refuses its own floor and hands back the seed - which is the recorded 16 px over thirty rows on a
        // 646 px strip whose own arithmetic said 13 px. A squeezed band is therefore fitted against itself;
        // every band that can hold the comfort size keeps the ceiling it always had.
        float fittedCeiling = squeezed ? ResultsFontGrowth.BandFillCeiling : fillCeiling;
        int fitted = ResultsFontGrowth.FitBoardIntoStrip(seed, bandFloor, int.MaxValue, bodyRoom, boardWidth,
            size => NaturalSize(content, size, bandFloor), out int floor, fittedCeiling);
        // ---- 自动模式的目标字号：按窗口高度等比，而不是「带能撑多大就多大」 ----------------------------------
        //
        // 1.19.47（最新用户合同）：自动字号以基准画面按分辨率等比例设目标——1080 高是 12px、1440 高是 16px，同一条
        // 1/90 的窗口高度——而不是让每一条带把自己的字号撑到带顶。撑到带顶的字号在一条高带上会变成一块巨板，而同一个
        // roster 换一个分辨率又会得到另一个大小。目标只封顶，不改变行数：带比目标矮时 `ResultsBandFit` 早就把可读下限
        // 降到了带自己的答案（整行 fit，行不裁），玩家自己的缩放仍然在 `ResultsScroll.FontFor` 里叠在目标之上，溢出
        // 照旧由滚轮到达。窄宽仍然优先：下面的宽度循环在封顶之后仍然会把放不下的宽度一步一步减下去。
        int autoTarget = ResultAutoBoardContract.TargetGlyphPixels(Screen.height);
        if (autoTarget > 0 && fitted > autoTarget)
        {
            if (HeadingReports.Add("auto-target"))
                Plugin.Logger?.LogInfo($"[DIAG] settlement board at the automatic mode's target: this {bodyRoom:0}px of table height would have held a {fitted}px glyph, "
                    + $"and {autoTarget}px is what a {Screen.height}px window aims at ({ResultAutoBoardContract.BaselineGlyphPixels}px at "
                    + $"{ResultAutoBoardContract.BaselineScreenHeight:0}px, scaled and rounded); the board keeps its {content.Rows} rows, "
                    + $"hangs from the wall below instead of filling the band, and role={ResultPageRolePolicy.Name(Role())} keeps its own walls.");
            fitted = autoTarget;
        }
        // ---- the fit is the size the board is drawn at -----------------------------------------------------
        //
        // This is the whole of the change from the zoomable board: `ResultsZoom.ScaleSize` walked the size down
        // until the *whole* table fitted the strip, and `ResultsBandFit.FloorForBand` lowered the readable floor
        // to the band's own answer on the way, so a short page was answered with a six-pixel table and the scroll
        // the player needed could never happen. `ResultsScroll.FontFor` answers the size this fit measured, with
        // the player's own `FontScale` on top of it, and the height bound is no longer handed to the grid at all:
        // a table taller than its plate - a genuinely short strip, or a board the player asked to be larger - is
        // a table the wheel scrolls, and the plate that clips it is measured from the rows the strip really holds.
        int fontSize = ResultsScroll.FontFor(fitted, Plugin.ResultFontScale(succeeded));
        // The floor the grid is built with follows the size it is drawn at. It may never be above the band's own
        // answer, because the width still has to hold: the board has a vertical scroll and no horizontal one, so
        // a table too wide for the page is the one case that still spends glyph size.
        floor = Mathf.Max(ResultsBandFit.AbsoluteMinimumPixels, Mathf.Min(bandFloor, fontSize));
        PanelGrid grid = BoardGrid(content, fontSize, floor, boardWidth);
        int widthTries = 0;
        while (grid != null && grid.Width > boardWidth + 0.5f
            && fontSize > Mathf.Max(floor, ResultsBandFit.AbsoluteMinimumPixels) && widthTries++ < 64)
        {
            fontSize--;
            grid = BoardGrid(content, fontSize, floor, boardWidth);
        }
        // The floor the grid on screen was really built with, so a later refresh re-measures with that one
        // instead of asking for a floor this strip already proved it cannot honour.
        _fontFloor = floor;
        return grid;
    }

    // The grid the board is drawn with. The page's width is a bound the table may not exceed, never a width
    // the table is stretched to: `HudTable.BuildGrid` measures the cells and gives every value column the
    // same width, and nothing afterwards widens the grid to fill the page it is drawn on. The height is
    // deliberately unbounded: the table is built at its full row count and the plate clips it, which is what
    // lets the rows below the plate be scrolled to instead of being squeezed into a smaller glyph.
    private static PanelGrid BoardGrid(TableContent content, int fontSize, int floor, float boardWidth)
        => HudTable.BuildGrid(content, fontSize, Plugin.Density, boardWidth, float.PositiveInfinity, floor);

    // The plate this board really occupies inside the room a page offered: whole rows, the frozen header row
    // included, and never the whole table when the table is taller than the room. `visible` is how many rows the
    // plate holds and `first` the first scrollable row on screen; both are clamped to the table, so a page that
    // grew, shrank or changed resolution between frames cannot leave the offset past the table's own foot.
    private static float PlateFor(PanelGrid grid, float room, float header, out int visible, out int first)
    {
        visible = 0; first = ResultsScroll.FrozenRows;
        if (grid == null) return 0f;
        float tableRoom = room - header;
        if (!(tableRoom > 0f)) tableRoom = room;
        ScrollViewport view = ResultsScroll.Viewport(grid.Rows, ResultsScroll.FrozenRows, tableRoom,
            grid.RowHeight, _scrollFirst);
        visible = view.Visible;
        first = view.First;
        float plate = ResultsScroll.PlateHeight(view.Visible, grid.RowHeight, tableRoom);
        return Mathf.Min(grid.Height, plate);
    }


    // One candidate plate for a standing board, re-placed against this frame's strip and judged against both bands.
    //
    // `room` is the room the plate is measured into (the larger of this frame's strip and the one the board stands in, or
    // this frame's alone when the first attempt does not fit), `strip` is what the page offers *now*. The candidate has to:
    //   * be placeable by the same bottom-aligned search the first placement used, at the current grid's width;
    //   * sit inside this frame's band (`ResultChromeLayout.PlateInsideBand`, the screen's own foot included);
    //   * clear the wall the held board has to stay above and the strip it was measured into (`HeldGridFits`).
    // A plate that fails any of them is not committed, which is what stops the "larger of the two rooms" rule from ever
    // drawing through a footer that really rose.
    private static bool TryFitHeld(PanelGrid grid, float room, float header, BoardStrip strip,
        out float plate, out int visible, out int first, out LayoutRect rect)
    {
        plate = 0f; visible = 0; first = ResultsScroll.FrozenRows; rect = _holdBoard;
        if (grid == null) return false;
        if (!float.IsFinite(room) || !(room > 0f)) return false;
        plate = PlateFor(grid, room, header, out visible, out first);
        if (!(plate > 0f) || !float.IsFinite(plate)) return false;
        float total = plate + header;
        if (!ResultBoardHold.TryPlace(Screen.width, Screen.height, strip, grid.Width, total,
                Plugin.ResultsOffsetX.Value, out LayoutRect candidate, ResultBoardHold.PlacementSlack(!_succeeded && !GuestClient(), BoardSlack), bottomAligned: true,
                // 1.19.68: a host success strip whose foot is only the window's edge caps its own 2 px tolerance (`TryPlace`),
                // so a refreshed plate is judged against the same edge the first placement stopped on - otherwise the growth
                // candidate would stand 2 px past the edge `HeldGridFits` below judges it with and never be committed.
                screenEdgeCeiling: _succeeded && !GuestClient()))
            return false;
        if (!ResultChromeLayout.PlateInsideBand(candidate, strip.Top, strip.Bottom, Screen.height, BoardSlack))
            return false;
        if (!ResultBoardHold.HeldGridFits(_holdStrip, strip, candidate, total, Screen.height, BoardSlack))
            return false;
        rect = candidate;
        return true;
    }

    // The table's own extents at one glyph size, measured without a box to fit into: the box is what is being
    // chosen here, so measuring inside one would only report that box back instead of the table's real size.
    private static ResultTableSize NaturalSize(TableContent content, int fontSize, int minimumPixels = ResultsFontGrowth.MinimumReadablePixels)
    {
        PanelGrid measured = HudTable.BuildGrid(content, fontSize, Plugin.Density, float.PositiveInfinity, float.PositiveInfinity, minimumPixels);
        if (measured == null) return ResultTableSize.None;
        return new ResultTableSize(measured.Width, measured.Height);
    }

    // Result snapshots continue arriving after the page opens. Refresh the displayed board and its existing
    // archive entry from the same projection, while keeping its placement unless the current grid cannot fit.
    private static bool RefreshBoardData(Component page, bool succeeded)
    {
        if (page == null || _grid == null || _content == null) return false;
        ImpactReport report = BuildReport(succeeded);
        if (report.Entries.Count == 0) return true;
        ImpactBoardLayout board = ImpactBoard.Build(report, true, succeeded);
            ResultsSaver.Save(page, succeeded, board, report, CombatSync.Generation, HostProven());
        // 固定模式：人数或内容变了就是同一张表的新尺寸，底边中点不动，所以这里不按自适应那套「行/列变了就放弃」处理。
        if (!_fixedPlaced && (board.Rows != _grid.Rows || board.ColumnCount != _grid.Columns)) return false;
        var content = new TableContent
        {
            Columns = board.Columns,
            Text = board.Text,
            Colors = Colors(board, report),
            FieldSeparator = ImpactBoard.ValueSeparator,
            Dividers = Dividers(board),
            Kinds = board.Kinds,
            // 1.19.62: the same one line for the whole squad, from the same board model. It is carried, never measured
            // as a cell: rows, columns and the fixed-layout row/column guard above are exactly what they were.
            Figure = board.Figure
        };
        // 固定模式：展开/收起的行数只由完整看板决定（上面 `ImpactBoard.Build(report, true, ...)`），表格在自己指定的
        // 字号下重量一次，矩形按同一个锚点重算——底边中点因此不随人数变化而移动。
        if (_fixedPlaced) return FixedLayoutRefresh(content, board, report, succeeded);
        float boardWidth = Mathf.Max(320f, Screen.width - 48f);
        // Measured UNBOUNDED at the size the board is drawn at: a bounded build can come back with a smaller glyph, and a
        // shrunken grid is not evidence that the grid on the page still covers this snapshot.
        PanelGrid measured = HudTable.BuildGrid(content, Mathf.Max(1, Mathf.RoundToInt(_grid.FontPixels)), Plugin.Density,
            boardWidth, float.PositiveInfinity, _fontFloor);
        // Nothing can be measured at the font the board is drawn at, so this snapshot cannot be adopted: the pair already
        // on the page is kept exactly as it is and this frame is still answered ("handled") - the caller has no reason to
        // throw a good board away because one poll could not measure its own text.
        if (measured == null) return true;
        // The standing grid really covers it - same rows and columns, no field wider than the one it already holds, no
        // larger glyph. Only then may the text be swapped under the geometry that is already on the page, because the
        // geometry is not changing.
        if (GridCovers(_grid, measured))
        {
            _content = content;
            return true;
        }
        // The strip is not measurable this repaint (the page is still animating, or it went away between the two reads).
        // Again: adopt nothing, keep the board that is already on the page, and answer "handled" so the caller does not
        // reset it for a frame that measured no strip.
        if (!Placement(page, succeeded, out _, out float room, out BoardStrip strip) || !(room > 0f)) return true;
        // The strip may have grown since the board arrived, so the same fit the first placement used is run again rather
        // than reusing the glyph size it happened to land on: a refreshed snapshot re-measures. It reserves the same header
        // the first placement reserved, out of the same room and into the same `Fit`, so a refreshed board cannot grow its
        // plate above the strip's top edge.
        float header = ChromeHeader();
        float tableRoom = room - header;
        if (!(tableRoom > 0f)) tableRoom = room;
        PanelGrid resized = FitBoard(content, tableRoom, boardWidth, succeeded, strip.ButtonHeight, strip.FillCeiling);
        // Nothing is written here on purpose. `Fit` commits `_grid`/`_content`/`_origin` together and only on a frame it
        // really placed the board, and its own `true` is the "handled" answer even when it refuses a growth and keeps the
        // board already on the page (`ResultBoardHold.HeldGridFits`). A grid that could not be measured is not handled at
        // all, so the caller takes the ordinary re-measure path with the old pair untouched.
        return resized != null && Fit(strip, resized, content, board, report, succeeded, header);
    }

    private static bool GridCovers(PanelGrid current, PanelGrid required)
    {
        if (current == null || required == null || current.Rows != required.Rows || current.Columns != required.Columns
            || required.FontPixels < current.FontPixels - 0.5f || current.FieldWidths == null || required.FieldWidths == null)
            return false;
        for (int column = 0; column < current.Columns; column++)
        {
            float[] available = current.FieldWidths[column];
            float[] needed = required.FieldWidths[column];
            if (available == null || needed == null || available.Length != needed.Length) return false;
            for (int field = 0; field < available.Length; field++)
                if (needed[field] > available[field] + 0.5f) return false;
        }
        return true;
    }
    // Every player as the board sees them: the damage and accuracy counters that already existed, the
    // impact counters this release adds, and the four gear names.
    private static ImpactReport BuildReport(bool countLoneSurvivor)
    {
        var inputs = new List<ImpactInput>();
        foreach (CombatPlayer player in CombatTracker.SettlementView().OrderBy(entry => entry.DisplaySlot))
        {
            WeaponCombatStats primary = WeaponOf(player, InventorySlot.GearStandard);
            WeaponCombatStats secondary = WeaponOf(player, InventorySlot.GearSpecial);
            WeaponCombatStats melee = WeaponOf(player, InventorySlot.GearMelee);
            WeaponCombatStats tool = WeaponOf(player, InventorySlot.GearClass);
            var input = ImpactInput.From(player.Name, player.Total, player.Impact, player.SupportRole,
                primary != null ? primary.Counters : null,
                secondary != null ? secondary.Counters : null,
                melee != null ? melee.Counters : null,
                tool);
            input.Lookup = player.Lookup;
            // 1.19.62 (unreleased): the host's scan estimate for this row, read from the host-local ledger. `Display`
            // answers 0 whenever the row has a measured activation figure of its own - the holder's readable
            // cooldown, including a counted zero, or a positive count that player's machine reported - so the
            // measured figure always wins, even when it is smaller than the estimate this host accumulated. It also
            // answers 0 to a machine that is not a readable host (`HostProven`, the one role read this file owns),
            // so a row keeps its dash after a host migration instead of showing the previous host's figure.
            input.ScanRoundsEstimated = ScanRoundEstimates.Display(player.Impact, player.Lookup, HostProven());
            input.PrimaryName = Name(primary);
            input.SecondaryName = Name(secondary);
            input.MeleeName = Name(melee);
            // 1.19.64: a gun slot that really held more than one measured weapon is written down here instead of
            // being folded into the one name and one set of figures those cells can draw.
            input.AttachSlotWeapons(player.Weapons, primary, secondary,
                (int)InventorySlot.GearStandard, (int)InventorySlot.GearSpecial);
            inputs.Add(input);
        }
        double firepower = Plugin.SupportRoleFirepowerBonus != null ? Plugin.SupportRoleFirepowerBonus.Value : 1.0;
        double utility = Plugin.SupportRoleUtilityBonus != null ? Plugin.SupportRoleUtilityBonus.Value : 1.0;
        // A failed expedition neither shows nor scores the last-one-standing metric, so the two halves
        // are decided together here rather than by hiding a figure the score still counted.
        ImpactReport report = ImpactScoring.Evaluate(inputs, Plugin.ScoringWeights(), firepower, utility, countLoneSurvivor);
        // 1.19.62: the round's created-enemy figure, read once from the one squad ledger and carried on the report the
        // settlement board and the archive share. It is the same read for the success page and the failure page -
        // `BuildReport` is the only report this file builds - so neither page can show a figure of its own, and no
        // column, row or cell of the board changes because of it.
        report.Spawns = CombatTracker.SpawnTotal;
        return report;
    }

    // The row of one slot the settlement board reads. One slot can hold more than one row - the placeholder the
    // game opened it with and the real weapon or tool that was later named - so the most specific real row is
    // taken rather than whichever the dictionary yields first. `WeaponRows` owns that rule and is asserted
    // headlessly; reading "the first row of this slot" is how the tool column showed an unnamed placeholder for
    // part of the squad.
    private static WeaponCombatStats WeaponOf(CombatPlayer player, InventorySlot slot)
        => player == null ? null : WeaponRows.For(player.Weapons, (int)slot);

    private static string Name(WeaponCombatStats weapon)
        => weapon == null || string.IsNullOrEmpty(weapon.Name) ? string.Empty : weapon.Name;

    // (1.19.64: the two gun slots' extra rows are projected by `ImpactInput.AttachSlotWeapons`, which the headless
    // regression drives directly; this file only names the two slots the board draws.)
    // The tone the board decided is turned into a colour here, which is the only place in the mod that
    // knows what gold or copper look like.
    private static Color[,] Colors(ImpactBoardLayout board, ImpactReport report)
    {
        int rows = board.Rows, columns = board.ColumnCount;
        var colors = new Color[rows, columns];
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++) colors[row, column] = ToneColor(board.Tone(row, column), column, report);
        }
        return colors;
    }

    private static Color ToneColor(ImpactTone tone, int column, ImpactReport report)
    {
        switch (tone)
        {
            case ImpactTone.PlayerColor:
                {
                    int index = column - 1;
                    if (report != null && index >= 0 && index < report.Entries.Count) return CombatTracker.PlayerColor(report.Entries[index].Input.Lookup);
                    return Color.white;
                }
            case ImpactTone.Gold: return new Color(1f, 0.84f, 0f);
            case ImpactTone.Silver: return new Color(0.75f, 0.75f, 0.75f);
            case ImpactTone.Copper: return new Color(0.8f, 0.5f, 0.2f);
            default: return Color.white;
        }
    }
    // The board is placed inside the strip its own measurement produced, and against the strip's *upper* edge:
    // the table hangs from the completion section (or from the failure page's own message) and grows downwards,
    // so the band of empty page it can leave is the one just above the lobby button rather than a gap between
    // the section the player is reading and the table that belongs under it. The button stays the strict lower
    // bound - `TryPlace` refuses a board that would reach it - so nothing here may ever overlap it either.
    // Answers whether a board really went onto the page. A refused fit leaves the caches untouched and answers
    // false, which is what keeps `Refresh` from hiding the game's own blocks for a board nobody will draw.
    private static bool Fit(BoardStrip strip, PanelGrid grid, TableContent content, ImpactBoardLayout board, ImpactReport report, bool succeeded,
        float header = 0f)
    {
        // The board is placed inside the strip its own measurement produced, not merely inside the window: a
        // board taller than that strip used to be accepted here and then refused at draw time, which is how a
        // whole table was dropped for a fraction of a pixel of overflow. The header the close stands in is part
        // of that board: the placement holds the table plus the header, and the table's own origin is one header
        // lower, so the close never stands on a figure.
        if (!(header > 0f) || !float.IsFinite(header)) header = 0f;
        // 1.19.52 (F2/F4): the two-phase column measurement.
        //
        // The band this frame was measured into only ends where the table's own column allows: a wall that stands in
        // the board's way ends it, one that stands beside the board does not. The board's width is what decides which
        // of the two a control is, and on the first placement of a page nothing has measured it yet. Rather than
        // committing a board the next frame would re-align (or, as 1.19.51 did, falling back to the page's footer and
        // drawing an eight-row plate), the fit records the width it really measured for this page's content and
        // refuses this frame: the vanilla page stays exactly as the game drew it, the next repaint resolves the band
        // against the table's own column, and one extra frame is the whole cost. The budget is bounded per page, and
        // a page whose table keeps changing width spends it and then proceeds with what it has.
        LayoutColumn measuredColumn = BoardColumn();
        if (succeeded && !measuredColumn.Known && grid != null && grid.Width > 0f && _columnProbes < MaximumColumnProbes)
        {
            _columnProbes++;
            _widthProbe = grid.Width;
            if (HeadingReports.Add("success-column-probe"))
                Plugin.Logger?.LogInfo($"[DIAG] result board measured its own column: {grid.Width:0}x{grid.Height:0} px at the "
                    + $"{grid.FontPixels:0}px glyph this page's table really needs, so the band can be resolved against the "
                    + $"column the board will occupy instead of the page's footer control (probe {_columnProbes}/{MaximumColumnProbes}). "
                    + "Nothing is committed and nothing is painted on this frame - the vanilla page is kept, and the next "
                    + "repaint measures the strip against this width.");
            return false;
        }
        float plateHeight = PlateFor(grid, strip.Bottom - strip.Top, header, out int scrollVisible, out int scrollFirst);
        float total = plateHeight + header;
        // 1.19.68: the fit is the one placement a host success page gets - strict, early, F8 and the fail-open band alike
        // all come back through here (`TryFit` -> `Fit`). A strip whose foot is only the window's own edge therefore stops
        // on that edge instead of taking the 2 px tolerance past it: the tolerance exists for an animating page element and
        // there is none under a screen-foot strip, while the per-repaint checks that judge this very plate
        // (`BoardStillSafe` -> `HeldGridFits`) take `Screen.height - 8` as a hard limit. The recorded 2560x1440 host success
        // page is that shape: `band=(846..1432)`/`bandSource=InteractiveBand`, plate placed to `boardBottom=1434` and then
        // `grid-would-reach-edge` on every repaint (log 7224-7225). Client pages keep their own arithmetic - this cap belongs
        // to the host success band only, and a strip that ends on a measured button or a measured page element is unchanged.
        bool placedStrictly = ResultBoardHold.TryPlace(Screen.width, Screen.height, strip, grid.Width, total,
            Plugin.ResultsOffsetX.Value, out LayoutRect plate, ResultBoardHold.PlacementSlack(!succeeded && !GuestClient(), BoardSlack), bottomAligned: true,
            screenEdgeCeiling: succeeded && !GuestClient());
        // 1.19.21 (the hard band invariant): a board is never placed taller than its band and merely faded. The grid
        // handed in here is already fitted to the strip's own room (`FitBoard` steps the glyph down until it fits), so
        // the strip's top-aligned placement is the whole answer for both the strict and the fail-open board; the band
        // the fail-open path was given is the band the fit slid the grid into. `ResultFallbackLayout.TryPlaceInBand`
        // stands in only for the fraction-of-a-pixel case the strip's own probe refused, and only when the grid really
        // does not exceed the band - a taller-than-band board is refused and reported, never drawn faint.
        if (!placedStrictly && _fallbackPlaced && total <= strip.Bottom - strip.Top + 0.5f)
            placedStrictly = ResultFallbackLayout.TryPlaceInBand(Screen.width, Screen.height, strip.Top, strip.Bottom,
                grid.Width, total, Plugin.ResultsOffsetX.Value, out plate);
        if (placedStrictly)
        {
            // The table's own origin sits under the reserved header; the plate is the whole board.
            LayoutRect origin = new LayoutRect(plate.X, plate.Y + header, grid.Width, plateHeight);
            _origin = origin;
            // The window this placement really drew: the wheel moves these rows, and the plate they are drawn into
            // is what the next frame's hit rectangle is measured from.
            _scrollVisible = scrollVisible; _scrollFirst = scrollFirst; _scrollPlate = plateHeight;
            _content = content; _grid = grid;
            // The place the board was measured into, kept for as long as this page is up: the F8 write, the hot
            // reload that follows it and the page's own button animation all come back through here, and none of
            // them may cost the board the strip it was already given.
            _holdValid = true; _holdBoard = plate; _holdOpacity = _fallbackPlaced ? _fallbackOpacity : 0f;
            // The page this place belongs to, recorded with the place itself (review #2: the failure page used to get
            // this pre-set on `OnEnable`, which is not where a hold is made).
            _holdPage = _page != null ? _page.Pointer : IntPtr.Zero;
            // The strip itself is kept next to the place, because a page that later offers a different one is a
            // page this board no longer fits: `ResultAnchorDrift` compares the strip's upper edge and the room it
            // gave against what the page measures from now on, and asks for one bounded re-measure when they have
            // really moved.
            _holdStrip = strip;
            Drift.Record(Time.frameCount, strip.Top, strip.Bottom - strip.Top);
            float x = origin.X, y = origin.Y;
            if (!_loggedDraw)
            {
                _loggedDraw = true;
                float stripHeight = Math.Max(1f, strip.Bottom - strip.Top);
                // The two blank bands the placement really leaves, in pixels: above the table inside the measured
                // strip, and between the table and the lobby button. They are what a "there is a large empty
                // band" report has to be answered with, and neither of them carries anything about a player.
                float blankAbove = Math.Max(0f, origin.Y - Math.Max(0f, strip.Top));
                float blankBelow = Math.Max(0f, strip.Bottom - origin.Bottom);
                // The placed frame prints the band the board was really fitted into - its own two edges and the
                // source word - so a log alone settles where a board stands and which measurement put it there.
                Plugin.Logger.LogInfo($"[DIAG] influence board {board.ColumnCount} columns x {board.Rows} rows ({report.Entries.Count} players), {grid.Width:0}x{grid.Height:0} px at ({x:0},{y:0}) of a {Screen.width}x{Screen.height} window, {(succeeded ? "success" : "failure")} screen, anchorSource={(succeeded ? "completion-section" : "failure-strip")}, blankAbove={blankAbove:0}px, blankBelow={blankBelow:0}px, band=({strip.Top:0}..{strip.Bottom:0}), bandSource={strip.FooterSource}, glyph {grid.FontPixels:0}px filling {grid.Height * 100f / stripHeight:0}% of the {stripHeight:0}px strip between the completion section and the lobby button ({grid.Width * 100f / Math.Max(1f, Screen.width - 48f):0}% of the {Screen.width - 48}x width it was fitted to); rows shown {Math.Min(scrollVisible, grid.Rows):0} of {grid.Rows} in a {plateHeight:0}px plate under the {header:0}px header (first row {scrollFirst:0}, {grid.RowHeight:0.##}px row pitch - the plate is the height that clips the table, so this is the count a player really sees); drawn by IMGUI, no vanilla report card was modified.");
            }
            else if (_relayoutPending && HeadingReports.Add("board-relaid"))
                Plugin.Logger?.LogInfo($"[DIAG] result board re-fitted: the page was measured again ({_relayoutReason}) and the board now stands at {grid.Width:0}x{grid.Height:0} px at ({x:0},{y:0}) of a {Screen.width}x{Screen.height} window, band=({strip.Top:0}..{strip.Bottom:0}), bandSource={strip.FooterSource}, glyph {grid.FontPixels:0}px, role={ResultPageRolePolicy.Name(Role())}; the placement before it was laid out before this page's own wall could be read, so this line is the size and place the player really ends up with (one line per page - the re-measure itself stays inside `ResultAnchorRecovery` and `ResultAnchorDrift`).");
            _relayoutPending = false;
            // 1.19.26: the drawn half of the 1.19.22 forced-frame contract is *not* answered from here. This is the fit, and a
            // fit says a board was measured and placed - not that one pixel of it was issued; `Draw` can still refuse the very
            // frame that fitted it (`BoardStillSafe`), and a "board drawn" line printed at fit time was an answer about pixels
            // nobody had painted. The answer comes from `ResultsDisplay.Draw`, after its `HudTable.Draw` call. The refusal half
            // stays where it always was (ReportHeldBack, fail-no-room, empty-roster, the deferral line), so an F8 press still
            // ends its frame with exactly one line.
            return true;
        }
        // The strip came back shorter than the board. The result pages animate their own buttons and a button's
        // own bounds move by a pixel or two while it is hovered, so a board measured against one frame can be
        // refused by the next - which is exactly how the failure board disappeared and only came back while the
        // player happened to hover the page. A board that is already up on this page keeps the place it was
        // measured into.
        //
        // What it does not do is commit a refreshed grid that cannot clear the measured strip and current controls.
        // Failure Draw can lift a held board; BoardStillSafe applies the same no-overlap check on every repaint for
        // both result pages, and a refused growth keeps the grid/content pair already on screen.
        if (_holdValid && _page != null && ResultVisibility.SamePage(_holdPage, _page.Pointer))
        {
            // 1.19.46 (UI review F2): what a refreshed grid may occupy is the *plate* it would be drawn into, measured from
            // this strip with the same `PlateFor` the fit uses - never `grid.Height`, the table's unscrolled height, which
            // nothing paints. Judging by the unscrolled table refused every refresh of a board taller than its strip; worse,
            // it left `_scrollVisible`/`_scrollPlate` describing the grid that *was* on the page while `_grid` was already
            // the new one, i.e. stale content drawn at the old plate. Plate, visible rows and first row are therefore
            // computed once, judged together with the header, and committed together.
            // 1.19.59: the room the refreshed plate is measured into is the *larger* of this frame's strip and the strip the
            // standing board was placed into. 1.19.58 used this frame's strip alone, and `HeldGridFits` only ever checks the
            // bottom edge - so every repaint whose measurement came back a little shorter wrote a shorter plate, and a page
            // whose footer kept breathing ratcheted the table down one row at a time. That is the recorded "布局越来越短".
            //
            // The larger room never means "draw over the wall". The plate it produces is re-placed against *this* frame's
            // strip with the same bottom-aligned placement the first one used, and committed only when it really sits inside
            // both bands and clears the wall the held board has to stay above (`TryFitHeld`). When it does not - the footer
            // really rose, another control arrived - the smaller room this frame offers is tried, which is the "re-adapt to a
            // wall that tightened" case; only a frame in which neither fits keeps the plate already on the page, and
            // `BoardStillSafe` then defers it. A tightened wall can therefore never be walked through.
            float headerNow = ChromeHeader();
            float heldRoom = _holdStrip.Bottom > _holdStrip.Top ? _holdStrip.Bottom - _holdStrip.Top : 0f;
            float frameRoom = strip.Bottom - strip.Top;
            bool standing = TryFitHeld(grid, Math.Max(heldRoom, frameRoom), headerNow, strip,
                    out float keptPlate, out int keptVisible, out int keptFirst, out LayoutRect keptRect)
                || TryFitHeld(grid, frameRoom, headerNow, strip,
                    out keptPlate, out keptVisible, out keptFirst, out keptRect);
            if (!standing)
            {
                if (HeadingReports.Add("board-growth-refused"))
                    Plugin.Logger?.LogInfo($"[DIAG] result board kept at its measured size: a refreshed {grid.Width:0}x{grid.Height:0} board cannot be placed inside this frame's strip "
                        + $"({strip.Top:0}..{strip.Bottom:0}, source={DescribeBand(strip.FooterSource)}, wall={(float.IsFinite(strip.ButtonTop) ? strip.ButtonTop.ToString("0") : "unmeasurable")}) "
                        + $"nor inside the strip it was measured into ({_holdStrip.Top:0}..{_holdStrip.Top + heldRoom:0}), so the grid already on the page is kept and the next snapshot asks again.");
                _relayoutPending = false;
                return true;
            }
            _content = content; _grid = grid;
            // The plate the judge just measured is the plate the next repaint draws: the new grid's own visible rows and
            // first row travel with it, so nothing is left describing the previous table.
            _scrollVisible = keptVisible; _scrollFirst = keptFirst;
            _scrollPlate = keptPlate;
            _relayoutPending = false;
            // The board stands where the placement put it: the table's own origin is that plate's top plus the header it
            // reserved, and the rectangle is the one the per-repaint scan judges.
            _holdBoard = keptRect;
            _origin = new LayoutRect(keptRect.X, keptRect.Y + headerNow, _grid.Width, keptPlate > 0f ? keptPlate : _grid.Height);
            if (HeadingReports.Add("board-kept"))
                Plugin.Logger?.LogInfo($"[DIAG] result board kept: the strip measured {strip.Top:0}..{strip.Bottom:0} cannot hold the {grid.Width:0}x{grid.Height:0} board whole, so the refreshed rows are drawn into a "
                    + $"{keptPlate:0}px plate at ({keptRect.X:0},{keptRect.Y:0}) of this page's own strip (rows {keptFirst}..{keptFirst + keptVisible - 1}, {keptVisible} whole rows), "
                    + "and only the lobby button is read from here on - the plate is never grown through a wall the page has drawn.");
            return true;
        }
        // The one line a refused fit must never be silent about, and must never dress up: the real strip, the real height
        // the board needs, the role and the page it was measured on. `need` is the board's own floor - the height it
        // cannot be drawn under at any glyph size - so the two figures say whether the strip was tight or impossible.
        // The refusal belongs to the strict/page band (a board taller than the page's own band is refused, never drawn
        // outside it); the invented fail-open band is sized from the board in `ResultFallbackLayout.TryResolve`, so a
        // forced F8 never reaches this line for room.
        if (ResultReportPolicy.OncePerPage(HeadingReports, "fit-failed", Forced, _forcedOutcomePending))
            Plugin.Logger?.LogWarning($"[DIAG] result board cannot fit: role={ResultPageRolePolicy.Name(Role())}, page={(succeeded ? "success" : "failure")}, "
                + $"top={strip.Top:0}, bottom={strip.Bottom:0}, strip={(strip.Bottom - strip.Top):0}px, grid={grid.Width:0}x{grid.Height:0}, "
                + $"need={grid.Height:0}px at the {grid.FontPixels:0}px glyph it was built with (floor={_boardFloor:0}px for {grid.Rows} rows), "
                + $"have={((strip.Bottom - strip.Top)):0}px, footerSource={strip.FooterSource}, buttonTop={(float.IsFinite(strip.ButtonTop) ? strip.ButtonTop.ToString("0") : "unmeasurable")}, "
                + $"screen={Screen.width}x{Screen.height}{(Forced ? ", forcedByF8=true - this press is answered by a refusal line, never silence" : string.Empty)}; nothing is drawn over the page - the vanilla result screen is kept and the strip is measured again.");
        return false;
    }

    // Where the board's top edge goes and how much room it has, together with the strip that produced it and
    // the lobby button that bounds it, so that place can be held afterwards. On the failure screen the board
    // before that is seen on its own, jumps as the message fades in, and keeps the shorter strip it was measured
    // into for the whole page. On the success screen the board measures the strip between the expedition's
    // completion section and the button that sends the squad home, and the table scales its font and row spacing
    // to that strip, so all four players fit without covering either element. Neither page places anything before
    // both of its own edges - the anchor above and the button below - have been measured in their settled state.
    private static bool Placement(Component page, bool succeeded, out float top, out float room, out BoardStrip strip)
    {
        top = 0f; room = 0f; strip = default;
        if (!succeeded)
        {
            if (!FailureStrip(page, out top, out float failureBottom, out _, out bool failureButtonMeasured, out float failureButtonTop,
                out float failureButtonHeight, out bool failureTitleSettled, out FooterControls failureControls, out bool failureNativeFooterVisible,
                out ResultFooterSource failureFooterSource, out bool failureFootIsWall, out ResultsBand failureBand)) return false;
            // F5 (independent review): the failure page's own wall clock starts on the first frame this page is *looked
            // at*, not after the fail-open path has spent its sixty refusals - otherwise a page that never shows a lower
            // bound waited 60 + 180 frames before the labelled give-up. The same predicate the fail-open path uses
            // decides it here, and a page that hands over any bound clears the clock on that very frame.
            FailureWallWaitFrames(FailureWallObserved(failureButtonMeasured ? failureButtonTop : float.NaN,
                failureControls.PressableTop, failureControls.FooterTop));
            // 客机：完整扫描证明只约束主机；客机放行只看锚点布局可读 + 固定页面几何。
            if (!GuestClient() && !failureControls.Complete)
            {
                ReportIncompleteScan("failure", failureControls, top, Math.Min(failureBottom, Screen.height - 8f) - top, failureFooterSource);
                return false;
            }
            if (GuestClient())
            {
                // 客机：锚点（任务失败标题）布局读得出来即落板；条带已由 FailureStrip 的客机分支算成固定几何。
                ButtonSettled(false, float.NaN);
            }
            else if (failureButtonMeasured)
            {
                // 1.19.21: the leave/restart buttons' laid-out rect is enough on the frame it is read; `Footer`'s settle
                // counters keep running for the log, but settle frames no longer hold the placement back.
                ButtonSettled(true, failureButtonTop);
            }
            else
            {
                // 1.19.14 原样：没有实测按钮时先清掉按钮 settle 状态，否则 Footer 的稳定计数会跨未测帧残留，
                // A→B→A 的输入会让 ButtonSettled 在单帧读数上就放行，且 Footer.Exhausted 永不成立、诊断日志不打印。
                ButtonSettled(false, float.NaN);
                bool failureNoFooterReady = FailureNoFooter.Observe(Time.frameCount,
                    ResultPageRolePolicy.UsesNoFooterSafeArea(Role()), failureTitleSettled,
                    page != null, failureNativeFooterVisible, failureControls.Complete, failureControls.Count, failureControls.Unmeasurable);
                // 1.19.37: the host's own way in, and the reason the recorded page needed four forced F8 presses. The
                // client's no-footer proof (`failureNoFooterReady`) is true for a client alone, so on a host this branch
                // used to answer false however complete its scan was, and an inactive leave button - the recorded page
                // had all five of its controls switched off - then refused a strip the page itself had measured. The
                // policy asks the page's own facts instead: a complete scan, nothing switched on, the page reporting its
                // footer visible, and a strip whose two edges come from the page's own title anchor and its measured
                // waiting/footer text. An active control that reaches the board still refuses the frame, above.
                FailureBandProofState failureBandProof = ResultFailureBandProofPolicy.Decide(Role(),
                    failureControls.Complete, failureControls.ParentCoverageComplete, failureControls.Count,
                    failureControls.Unmeasurable, failureNativeFooterVisible, failureButtonMeasured,
                    failureControls.OverlapsBoard, failureBand);
                bool failureBandProven = failureBandProof == FailureBandProofState.Proven;
                if (!failureNoFooterReady && !failureBandProven)
                {
                    // The role decides what this line is allowed to say; see the matching line on the success page.
                    if (HeadingReports.Add("failure-band-proof-state"))
                        Plugin.Logger?.LogInfo($"[DIAG] failure band proof refused: nativeFooterVisible={failureNativeFooterVisible},buttonMeasured={failureButtonMeasured},active={failureControls.Count},unmeasurable={failureControls.Unmeasurable},scanComplete={failureControls.Complete},parentCoverageComplete={failureControls.ParentCoverageComplete},{ResultFailureBandProofPolicy.Describe(failureBandProof, failureBand)}; vanilla is kept and placement is retried.");
                    if (Footer.Exhausted && HeadingReports.Add("failure-footer-unsettled"))
                        Plugin.Logger?.LogInfo($"[DIAG] failure board held back: role={ResultPageRolePolicy.Name(Role())}, no stable native footer edge was measured; "
                            + $"noFooterProof={(ResultPageRolePolicy.UsesNoFooterSafeArea(Role()) ? "available" : "not-this-role")} (ready={FailureNoFooter.Ready}, samples={FailureNoFooter.Samples}), "
                            + $"count={failureControls.Count},unmeasurable={failureControls.Unmeasurable},complete={failureControls.Complete},nativeVisible={failureNativeFooterVisible},footerTop={(float.IsFinite(failureControls.FooterTop) ? failureControls.FooterTop.ToString("0") : "unmeasurable")} "
                            + $"{ResultFailureBandProofPolicy.Describe(failureBandProof, failureBand)}, "
                            + $"need={_boardFloor:0}px; vanilla is kept and placement is retried.");
                    return false;
                }
                if (failureNoFooterReady)
                {
                    if (!ResultNoFooterSafeArea.TryResolve(top, Screen.height, out ResultsSafeArea safeArea,
                        failureControls.FooterTop, MessageGap))
                    {
                        // The client page's only bound here is the page's own measured lower wall; a wall that leaves no
                        // room at all is refused and the page is measured again - and the fail-open path places the board
                        // from the screen's own safe area meanwhile, so the refusal is never an empty screen.
                        ReportHeldBack("failure-client-safe-area", "safe-area-unusable",
                            "anchorTop=" + top.ToString("0") + ",footerTop="
                            + (float.IsFinite(failureControls.FooterTop) ? failureControls.FooterTop.ToString("0") : "unmeasurable")
                            + ",gap=" + MessageGap.ToString("0"));
                        return false;
                    }
                    top = safeArea.Top; failureBottom = safeArea.Bottom;
                    failureFooterSource = ResultFooterSource.ClientNoFooterSafeArea;
                    if (HeadingReports.Add("failure-client-safe-area"))
                        Plugin.Logger?.LogInfo($"[DIAG] failure footerSource=client-no-native-footer-safe-area: native lobbyVisible=false, complete interaction scan empty for {FailureNoFooter.Samples} distinct frames, title layout edge read; safe strip=({top:0}..{failureBottom:0}).");
                }
                else if (HeadingReports.Add("failure-page-band-released"))
                {
                    // The strip `FailureStrip` measured is kept as it is: its two edges are this page's own title anchor
                    // and the text/wall it laid out below the board, which is the whole of the proof.
                    Plugin.Logger?.LogInfo($"[DIAG] failure board released by the page's own band: role={ResultPageRolePolicy.Name(Role())}, "
                        + $"{ResultFailureBandProofPolicy.Describe(failureBandProof, failureBand)}, strip=({top:0}..{failureBottom:0}) of a {Screen.height} window, "
                        + $"count={failureControls.Count},hidden={failureControls.Hidden},unmeasurable={failureControls.Unmeasurable},complete={failureControls.Complete},nativeVisible={failureNativeFooterVisible}; "
                        + "the leave button was not measurable this frame, and a control the page has switched off is not evidence that the board stands over something the player can press - "
                        + "the page's own measured band is. An active control that reaches the board still defers the frame.");
                }
            }
            // 客机没有原生页脚按钮，条带不得标成「按钮已实测」，否则 ResultBoardHold 会按按钮高度算可读性下限。
            // 客机没有原生页脚按钮，条带不得标成「按钮已实测」，否则 ResultBoardHold 会按按钮高度算可读性下限。
            // The fill ceiling comes with the strip: a client band that ends above the wall `ResultsBounds` named (the
            // waiting-status block) may be filled to its own foot, and every other strip keeps the comfort ceiling -
            // including a client band that still runs to the page's own edge, which is the one case where filling it
            // would be a claim about a wall nobody measured.
            strip = new BoardStrip(top, failureBottom, GuestClient() ? false : failureButtonMeasured, failureButtonTop, failureButtonHeight, failureTitleSettled, failureFooterSource,
                ResultsFontGrowth.FillCeiling(failureFooterSource, failureFootIsWall));
            room = Math.Min(failureBottom, Screen.height - 8f) - top;
            // The failure page's strip is layout geometry - the title's own rectangle and the buttons' - so it answers
            // `ResultFinalPolicy` as soon as it has been read at all; the latch is fed here once per measurement for
            // its own record keeping. A failure page that hands over no strip at all (a branch above) leaves
            // `_finalMeasurable` false, so the fail-open band answers it exactly as before.
            NoteFinalGeometry(room > 0f && float.IsFinite(top) && float.IsFinite(failureBottom));
            _finalUndrawn = 0;
            if (_finalMeasurable) FinalLayout.Observe(strip.Top, strip.Bottom);
            return room > 0f;
        }

        // The section is measured, and the native reveal is asked, and both answers are used - for different
        // questions now.
        //
        // 1.19.21 measured the section first and asked the reveal afterwards, because the strip was the section's
        // final *layout* and the reveal only ever decided whether a board could be shown. 1.19.41 keeps the layout
        // reading but two things changed around it: the section's edge is now the deeper of that layout and the pixels
        // really drawn under it once the reveal has finished (`CompletionFinalBounds.Placed`, so the panel's top no
        // longer presses on the native sector icons and their 主要/精英 captions), and the first placement waits for
        // the reveal to finish (`ResultRevealGate`) instead of being torn down and re-measured after it. Both of those
        // need the reveal's own answer *before* the section is measured, so it is asked here: the frame the ledger
        // completes is the frame the section is measured as final.
        bool revealDone = CompletionReady(page);
        bool completionMeasured = SuccessRegion(page, out ResultsSafeArea area, out bool measuredButton,
            out float measuredTop, out float measuredHeight, out FooterControls successControls,
            out bool successNativeFooterVisible, out ResultFooterSource footerSource, out ResultsBand successBand);
        // A page that hands over no usable strip has nothing to settle, so the fail-open band answers it as it always did;
        // a page that does hand one over feeds the latch, which now only *records* the drawn-pixel route and the
        // fail-open policy's readings.
        NoteFinalGeometry(completionMeasured && area.Fits);
        if (area.Fits) FinalLayout.Observe(area.Top, area.Bottom);
        if (!completionMeasured)
        {
            // The page has no completion section this build could read: nothing proves where a board would hang from, so
            // the vanilla page is kept and the line says which read failed rather than leaving the silence.
            ReportHeldBack("success-anchor", "completion-unmeasurable",
                "completionEdge=" + (float.IsFinite(Completion.Edge) ? Completion.Edge.ToString("0") : "unmeasured") + ",boardFloor=" + _boardFloor.ToString("0"));
            return false;
        }
        // 1.19.41: the section is only *final* once the page has finished switching it on.
        //
        // The ledger was already wired and already logged, but the first placement threw its answer away - the recorded
        // success page reads `completed=0/6` on the frame the board was fitted and `6/6` a few lines later, and the
        // strip was re-measured once the reveal finished. The gate below refuses that first placement while the reveal
        // is unfinished *and* the ledger can still finish. It is not a frame count, not an alpha threshold and not an
        // empty scan: a ledger that can no longer finish (a target handed over with no object, a target count past its
        // budget) answers at once, and F8 is the player's own answer and never waits (`ResultRevealGate`). The
        // per-repaint safety checks (`BoardStillSafe`, `HeldGridFits`) deliberately do not ask this question: a board
        // that is already standing is never taken away because the page's reveal moved on.
        // 1.19.55 (user contract D): the board may enter *before* the page has proven its final boundary.
        //
        // The gate below is unchanged for the mature board: while the completion ledger can still finish, the page's own
        // reveal has not said where the section ends, and a table hung from a half-revealed row is a table that moves. What
        // is new is the way out that does not require F8, a frame count or an empty scan. When this machine holds real
        // settlement data, the page's completion section has been *laid out* (its slot edge, which is final from the frame
        // the page laid it out), the interaction scan finished, nothing it can see reaches the strip, and that strip holds the
        // table's own readable floor, the board enters as an **early, non-interactive** board (`ResultEarlyBoardPolicy`):
        // drawn, no wheel, no press, and not one of the game's own blocks put away. The mature placement below then takes
        // over on the frame the page's own edges are proven, with no key and no pointer involved.
        bool early = false;
        // 1.19.60 (review F1): is the strip this frame measured a *final* strip? One production predicate answers it,
        // `ResultRevealStatePolicy`, from the page's own ledger - never from a frame count, a budget or an empty scan:
        //   * `Finished` - the reveal answered: it finished, or it proved that nothing of its own is still running, or
        //     every target it was handed was proved unscheduled. Only this state is final on the ledger's word alone;
        //   * `Unobservable` - the page has been watched through the whole bounded observation budget and has handed the
        //     ledger nothing at all. This is the bounded retry F1 asked for: it still has to prove the page's own
        //     geometry (`FinalLayout.Ready`, the same two-agreeing-readings latch the fail-open path already uses), and it
        //     has its own name in the log so nobody can read it as "the reveal finished";
        //   * `NotStarted` / `Running` - the live page whose targets have not all arrived. `Required == 0` on the first
        //     frames of a page is exactly this state and never "settled": the native `BlinkIn` fills the ledger, so a page
        //     that is about to register six targets looks the same as one that never will - until the budget says which.
        // The lower wall must also be a control the page really measured (`ResultFooterProvenance.MeasuredLowerWall`):
        // `None` is a strip that ran to the foot of the window, and the client's fixed page geometry is a statement about
        // the screen rather than about what the page drew, so neither may stand in for a final edge.
        int revealRequired = CompletionTargets.Ledger.Required;
        if (revealRequired <= 0 && _revealObservations < ResultRevealStatePolicy.UnobservableObservations)
            _revealObservations++;
        RevealObservation revealState = ResultRevealStatePolicy.Observe(revealDone, revealRequired,
            CompletionTargets.Ledger.Count, CompletionTargets.Ledger.Completed, CompletionTargets.Ledger.InFlight,
            CompletionTargets.Ledger.MayStillComplete,
            CompletionTargets.Ledger.Settled, _revealObservations);
        bool revealSettled = ResultRevealStatePolicy.Answered(revealState)
            || (revealState == RevealObservation.Unobservable && FinalLayout.Ready);
        bool clientBoundsFinal = revealSettled && ResultFooterProvenance.MeasuredLowerWall(footerSource);
        if (revealState != RevealObservation.Finished && HeadingReports.Add("success-reveal-state"))
            Plugin.Logger?.LogInfo("[DIAG] success reveal state: " + ResultRevealStatePolicy.Name(revealState)
                + " (required=" + revealRequired + ",counted=" + CompletionTargets.Ledger.Count
                + ",completed=" + CompletionTargets.Ledger.Completed + ",inFlight=" + CompletionTargets.Ledger.InFlight
                + ",unscheduled=" + CompletionTargets.Ledger.Unscheduled
                + ",observationsWithoutTargets=" + _revealObservations + " of "
                + ResultRevealStatePolicy.UnobservableObservations
                + ",pageGeometryReady=" + FinalLayout.Ready.ToString().ToLowerInvariant()
                + ",revealFinished=" + RevealFinished.ToString().ToLowerInvariant()
                + ",role=" + ResultPageRolePolicy.Name(Role()) + ",lowerWall=" + footerSource
                + "); this names what the page's own ledger has answered so far - `unobservable` is the bounded retry for "
                + "a page whose native callback never came, and it still requires the page's geometry to read the same twice.");
        if (ResultRevealGate.BlocksPlacement(revealDone, CompletionTargets.Ledger.MayStillComplete,
                CompletionTargets.Ledger.InFlight, Forced))
        {
            ImpactReport earlyReport = BuildReport(succeeded);
            bool earlyAllowed = ResultEarlyBoardPolicy.Allows(earlyReport != null && earlyReport.Entries.Count > 0,
                completionMeasured, successControls.Complete, successControls.OverlapsBoard, area.Height, _boardFloor,
                HostProven(), clientBoundsFinal);
            if (!earlyAllowed)
            {
                if (HeadingReports.Add("success-reveal-pending"))
                    Plugin.Logger?.LogInfo("[DIAG] success board held back: "
                        + ResultRevealGate.Why(CompletionTargets.Ledger.Completed, CompletionTargets.Ledger.Count,
                            CompletionTargets.Ledger.Names, CompletionTargets.Ledger.Artwork, CompletionTargets.Ledger.InFlight)
                        + ", completion=" + WallText(Completion.Edge)
                        + ", boardTop=" + (area.Fits ? area.Top.ToString("0") : "unmeasurable")
                        + ", role=" + ResultPageRolePolicy.Name(Role())
                        + ", early=" + (earlyReport != null ? earlyReport.Entries.Count.ToString() : "0") + "rows,"
                        + "room=" + (float.IsFinite(area.Height) ? area.Height.ToString("0") : "unmeasurable")
                        + ", floor=" + (_boardFloor.ToString("0"))
                        + ",wallPending=" + ResultClientPageActionPolicy.Pending(float.NaN, float.NaN).ToString().ToLowerInvariant() + ", forcedByF8=" + Forced.ToString().ToLowerInvariant()
                        + "; vanilla is retained, and the section is measured again on the frame the ledger completes.");
                return false;
            }
            early = true;
            if (HeadingReports.Add("success-early-entry"))
                Plugin.Logger?.LogInfo("[DIAG] success board entered early (noninteractive): the page is live and this machine"
                    + " holds its own settlement rows, so the table is drawn from the completion section's laid-out edge while"
                    + " the page's native reveal is still running (completed=" + CompletionTargets.Ledger.Completed + "/"
                    + CompletionTargets.Ledger.Count + ", revealFinished=" + RevealFinished.ToString().ToLowerInvariant() + "),"
                    + " strip=(" + area.Top.ToString("0") + ".." + area.Bottom.ToString("0") + ") role="
                    + ResultPageRolePolicy.Name(Role()) + ", floor=" + _boardFloor.ToString("0")
                    + "px; this is not a claim that the page settled - Ready/Proven stay the strict path's own words - the wheel"
                    + " stays the page's, a press belongs to whatever the game drew, and none of the game's own blocks is put away."
                    + " The mature board replaces it on the frame its own boundary is proven.");
        }

        // The ledger answered "no further reveal can arrive" rather than "finished" (`RevealImpossible`: a routine that
        // really ended without revealing its target, a target with no object, or a target count past the budget).
        // Nothing is claimed about the page having settled: the section is placed from the slots the page laid out
        // (`CompletionFinalBounds.Placed` with `revealFinished: false`), the drawn pixels stay out of the band, and the
        // line says exactly that - once per page, because this is a state, not a frame. F1: this line can only print
        // once nothing of the ledger's own is still running, because the gate above waits for that.
        if (!revealDone && CompletionTargets.Ledger.RevealImpossible && CompletionTargets.Ledger.Settled
            && HeadingReports.Add("success-reveal-released"))
            Plugin.Logger?.LogWarning("[DIAG] success board released while the page's own reveal can no longer complete: "
                + ResultRevealGate.WhyReleased(CompletionTargets.Ledger.Completed, CompletionTargets.Ledger.Count,
                    CompletionTargets.Ledger.Aborted, CompletionTargets.Ledger.Unscheduled, CompletionTargets.Ledger.InFlight)
                + ", completion=" + WallText(Completion.Edge)
                + ", boardTop=" + (area.Fits ? area.Top.ToString("0") : "unmeasurable")
                + ", role=" + ResultPageRolePolicy.Name(Role())
                + ",wallPending=" + ResultClientPageActionPolicy.Pending(float.NaN, float.NaN).ToString().ToLowerInvariant() + ", forcedByF8=" + Forced.ToString().ToLowerInvariant()
                + "; vanilla was retained for the frames the reveal was still legible, and the board is placed from the layout edge alone.");
        // The completion settle gate is fed for the drawn-pixel route's diagnostics and counters only: the layout edge
        // above has already satisfied the placement for this frame, and the gate cannot hold it back any more.
        Completion.Observe(Time.frameCount, true, area.Top);
        if (successControls.OverlapsBoard)
        {
            ReportBoardOverlap("success", successControls, area.Top, area.Height, footerSource);
            return false;
        }
        if (!GuestClient() && (!successControls.Complete || !successControls.ParentCoverageComplete))
        {
            ReportIncompleteScan("success", successControls, area.Top, area.Height, footerSource);
            return false;
        }
        if (GuestClient())
        {
            // 客机：条带已由 SuccessRegion 的客机分支按固定几何算好；完成区布局读得出来即落板。
            ButtonSettled(false, float.NaN);
        }
        else if (measuredButton)
        {
            // 1.19.21: the button's laid-out or pressable edge is enough on the frame it is read; `Footer`'s settle
            // counters keep running for the log, but settle frames no longer hold the placement back.
            ButtonSettled(true, measuredTop);
        }
        else
        {
            // 1.19.14 原样：没有实测按钮时先清掉按钮 settle 状态，否则 Footer 的稳定计数会跨未测帧残留，
            // A→B→A 的输入会让 ButtonSettled 在单帧读数上就放行，且 Footer.Exhausted 永不成立、诊断日志不打印。
            ButtonSettled(false, float.NaN);
            bool successNoFooterReady = SuccessNoFooter.Observe(Time.frameCount,
                ResultPageRolePolicy.UsesNoFooterSafeArea(Role()), completionMeasured,
                page != null, successNativeFooterVisible, successControls.Complete, successControls.Count, successControls.Unmeasurable);
            // 1.19.60: a client whose own strip is already final is not held back by the no-footer proof. That proof
            // answers "may this page be read as having no footer at all", and the recorded client page never proved it -
            // which is why the board used to enter on the moving strip instead. Once the ledger has answered and the
            // lower wall is a control the page really measured, the strip is the one the mature board would be fitted
            // into, so the page takes the same early route below and the player gets one table at one size. While the
            // geometry can still move, nothing changes: vanilla is kept and the page is measured again.
            if (!successNoFooterReady && GuestClient() && !clientBoundsFinal)
            {
                // Guest no-footer readiness and fixed geometry are unchanged; a client never uses the host band proof.
                if (Footer.Exhausted && HeadingReports.Add("success-footer-unsettled"))
                    Plugin.Logger?.LogInfo($"[DIAG] success board held back: role={ResultPageRolePolicy.Name(Role())}, no stable native footer edge was measured; "
                        + $"noFooterProof=available (ready={SuccessNoFooter.Ready}, samples={SuccessNoFooter.Samples}), "
                        + $"count={successControls.Count},unmeasurable={successControls.Unmeasurable},complete={successControls.Complete},nativeVisible={successNativeFooterVisible},footerTop={(float.IsFinite(successControls.FooterTop) ? successControls.FooterTop.ToString("0") : "unmeasurable")} "
                        + $"need={_boardFloor:0}px; boundsFinal=false (revealFinished={RevealFinished.ToString().ToLowerInvariant()},mayStillComplete={CompletionTargets.Ledger.MayStillComplete.ToString().ToLowerInvariant()},inFlight={CompletionTargets.Ledger.InFlight},footerSource={footerSource}); vanilla is kept and the page is measured again at the bounded cadence.");
                return false;
            }
            if (!successNoFooterReady)
            {
                SuccessBandProofState successBandProof = ResultSuccessBandProofPolicy.Decide(Role(),
                    successControls.Complete, successControls.ParentCoverageComplete, successControls.Count,
                    successControls.Unmeasurable, successNativeFooterVisible, measuredButton,
                    successControls.OverlapsBoard, successBand);
                // 1.19.55: an unproven band is what the early board is waiting for, not a reason to withdraw it. The early
                // placement already required a complete scan, no overlap and a strip that holds the table's own floor; the
                // mature board - the one that may put the page's blocks away and take the wheel - is the one that must prove
                // the footer.
                // 1.19.56 (review 6/12): an unprovable footer is exactly what the early board exists for. A success page
                // whose return button never becomes visible - or which has no footer at all - used to refuse every board
                // for ever, so seeing all the data required F8. The early board is allowed here on the same measured
                // facts as in the reveal branch, and it is *replaced* (never repeated) on the frame the footer/band is
                // proven, because the mature placement sets `_earlyBoard = false` from this same measurement.
                if (successBandProof != SuccessBandProofState.Proven && !early)
                {
                    ImpactReport footerless = BuildReport(succeeded);
                    if (ResultEarlyBoardPolicy.Allows(footerless != null && footerless.Entries.Count > 0,
                            completionMeasured, successControls.Complete, successControls.OverlapsBoard, area.Height, _boardFloor,
                            HostProven(), clientBoundsFinal))
                    {
                        early = true;
                        if (HeadingReports.Add("success-early-entry-nofooter"))
                            Plugin.Logger?.LogInfo("[DIAG] success board entered early (noninteractive) because this page has no"
                                + " provable footer: revealFinished=" + RevealFinished.ToString().ToLowerInvariant()
                                + ", completed=" + CompletionTargets.Ledger.Completed + "/" + CompletionTargets.Ledger.Count
                                + ", nativeFooterVisible=" + successNativeFooterVisible.ToString().ToLowerInvariant()
                                + ", buttonMeasured=" + measuredButton.ToString().ToLowerInvariant()
                                + ", strip=(" + area.Top.ToString("0") + ".." + area.Bottom.ToString("0") + ")"
                                + ", room=" + area.Height.ToString("0") + "px, floor=" + _boardFloor.ToString("0")
                                + "px, role=" + ResultPageRolePolicy.Name(Role())
                                + "; every row this machine holds is drawn, the wheel and every press stay the page's, and no"
                                + " vanilla block is put away. The mature board replaces it the frame the page proves its own"
                                + " footer - this is not a claim that the page settled (1.19.56).");
                    }
                }
                if (successBandProof != SuccessBandProofState.Proven && !early)
                {
                    if (HeadingReports.Add("success-band-proof-state"))
                        Plugin.Logger?.LogInfo($"[DIAG] success band proof refused: nativeFooterVisible={successNativeFooterVisible},buttonMeasured={measuredButton},active={successControls.Count},unmeasurable={successControls.Unmeasurable},scanComplete={successControls.Complete},parentCoverageComplete={successControls.ParentCoverageComplete},{ResultSuccessBandProofPolicy.Describe(successBandProof, successBand)}; vanilla is kept and placement is retried.");
                    if (Footer.Exhausted && HeadingReports.Add("success-footer-unsettled"))
                        Plugin.Logger?.LogInfo($"[DIAG] success board held back: role={ResultPageRolePolicy.Name(Role())}, no stable native footer edge was measured; "
                            + $"noFooterProof=not-this-role (ready={SuccessNoFooter.Ready}, samples={SuccessNoFooter.Samples}), "
                            + $"count={successControls.Count},unmeasurable={successControls.Unmeasurable},complete={successControls.Complete},nativeVisible={successNativeFooterVisible},footerTop={(float.IsFinite(successControls.FooterTop) ? successControls.FooterTop.ToString("0") : "unmeasurable")} "
                            + $"need={_boardFloor:0}px; vanilla is kept and the page is measured again at the bounded cadence.");
                    return false;
                }
                // This is the page-measured band route, not a named button or a screen-safe-area fallback.
                footerSource = ResultFooterSource.InteractiveBand;
                if (HeadingReports.Add("success-page-band-released"))
                    Plugin.Logger?.LogInfo($"[DIAG] success board released by the page's measured band: {ResultSuccessBandProofPolicy.Describe(successBandProof, successBand)}, active={successControls.Count},unmeasurable={successControls.Unmeasurable},scanComplete={successControls.Complete},parentCoverageComplete={successControls.ParentCoverageComplete},nativeFooterVisible={successNativeFooterVisible}; footerSource=band.");
            }
            else
            {
                if (!ResultNoFooterSafeArea.TryResolve(area.Top, Screen.height, out area,
                    successControls.FooterTop, MessageGap))
                {
                    // Same rule as the failure page's client branch: an unusable safe area is refused, said so once, and the
                    // fail-open path places the board from the screen's own safe area instead of leaving the page blank.
                    ReportHeldBack("success-client-safe-area", "safe-area-unusable",
                        "completionTop=" + area.Top.ToString("0") + ",footerTop="
                        + (float.IsFinite(successControls.FooterTop) ? successControls.FooterTop.ToString("0") : "unmeasurable")
                        + ",gap=" + MessageGap.ToString("0"));
                    return false;
                }
                footerSource = ResultFooterSource.ClientNoFooterSafeArea;
                if (HeadingReports.Add("success-client-safe-area"))
                    Plugin.Logger?.LogInfo($"[DIAG] success footerSource=client-no-native-footer-safe-area: native lobbyVisible=false, complete interaction scan empty for {SuccessNoFooter.Samples} distinct frames, completion layout edge read; safe strip=({area.Top:0}..{area.Bottom:0}).");
            }
        }
        if (ResultsUpperBound.Decide(completionMeasured, area.Fits) != ResultsUpperBound.Decision.Placed)
        {
            if (HeadingReports.Add("success-strip-unusable"))
                Plugin.Logger?.LogWarning($"[DIAG] success board deferred: the measured completion section leaves no usable strip ({area.Top:0}..{area.Bottom:0} of {Screen.height}); vanilla is retained.");
            return false;
        }
        // 玩家把纵向偏移手改成 NaN / 负值时按 0 处理：`Mathf.Max(0f, NaN)` 会得到 NaN，条带与 room 全变 NaN，
        // 结算页会静默不出板且不打任何日志。这一条对主机与客机同样成立（`OffsetY`）。
        top = Math.Max(0f, area.Top) + OffsetY();
        room = Math.Min(area.Bottom, Screen.height - 8f) - top;
        if (GuestClient() && room < MinimumGuestStripHeight)
        {
            if (HeadingReports.Add("success-guest-strip-too-short"))
                Plugin.Logger?.LogInfo($"[DIAG] success board held back: the client page's fixed strip is shorter than the minimum board height after the configured offset ({room:0}px < {MinimumGuestStripHeight:0}px) on a {Screen.height} window; vanilla is retained.");
            return false;
        }
        // 客机没有原生页脚按钮，条带不得标成「按钮已实测」，否则 ResultBoardHold 会按按钮高度算可读性下限。
        // The band below the page's actions has no button under it either, for the same reason: the board's readability
        // floor there is the absolute one, not a share of a control that is not underneath it.
        bool belowActions = footerSource == ResultFooterSource.BelowActions;
        // The same fill ceiling on this page, from the same fact: the strip may be filled to its own foot only when that
        // foot is the wall the page measured under it - on a client success page the waiting-status block the scan
        // found, on a host page the button the player presses (which never reaches this branch, since only the client
        // source is allowed the full ceiling). A strip that ends at the page's own edge keeps the comfort ceiling.
        bool successFootIsWall = ResultsFontGrowth.FootIsTheWall(area.Bottom, successControls.FooterTop, MessageGap);
        strip = new BoardStrip(top, top + room, GuestClient() || belowActions ? false : measuredButton, measuredTop, measuredHeight, completionMeasured, footerSource,
            ResultsFontGrowth.FillCeiling(footerSource, successFootIsWall));
        // 1.19.55: which mode this frame's strip was measured for. A mature placement clears the flag, so the board that
        // stands after the page's own boundary is proven is the ordinary one - interactive, and the one that puts the page's
        // blocks away. The switch is decided by this frame's measurement alone: no pointer, no key and no frame count takes
        // part in it, which is what keeps the early-to-mature change from following the mouse.
        _earlyBoard = early;
        return room > 0f;
    }
    // 1.19.21: `Footer` is fed every frame the placement runs - its settle counters and `Exhausted` state keep the
    // log meaningful for the drawn-pixel route - but its answer no longer gates the placement: a button the page has
    // laid out or drawn is a wall on the first frame it is read, and a button the page never hands over leaves the
    // vanilla page in place with the page retried at the bounded cadence `Retry` keeps.
    private static bool ButtonSettled(bool measured, float top) => Footer.Observe(Time.frameCount, measured, top);

    // One bounded line per page visit for a scan that could not finish for a structural reason: the client flag,
    // a control's own pointer, a control's active state, the named action's parent node, the page's item registry
    // or a registry entry's type, the page's own type, or the strip arithmetic. Those are the cases in which the
    // page could not be read, and geometry and counts are all a log line needs to say which one it was. A control
    // its rectangle could not be read for is *not* one of them and is reported by `ReportDowngradedControls`
    // instead, because that control does not stop the board from being drawn. Never a player's identity.
    private static void ReportIncompleteScan(string where, FooterControls controls, float top, float room, ResultFooterSource source)
    {
        if (!HeadingReports.Add("scan-incomplete-" + where)) return;
        Plugin.Logger?.LogInfo($"[DIAG] {where} board held back: the interaction scan could not finish (scanComplete={controls.Complete.ToString().ToLowerInvariant()},parentCoverage={controls.ParentCoverageComplete.ToString().ToLowerInvariant()},reason={(string.IsNullOrEmpty(controls.Reason) ? "none" : controls.Reason)},count={controls.Count},hidden={controls.Hidden},hiddenPageActions={controls.HiddenPageActions},unmeasurable={controls.Unmeasurable},scanned={controls.ScannedCount},footerSource={source},{DescribeStrip(top, room)}); vanilla is retained and the page is measured again.");
    }

    // One bounded line per page visit for the page that used to be refused for ever.
    //
    // An active control whose rectangle could not be read is not a hole in the scan, so the scan no longer says
    // the page cannot be measured because of one - but a silent change of verdict is exactly what makes a live log
    // useless, so the first scan that is allowed through *only* because of such a control says so once. The line
    // names the count and nothing else: the control's own identity belongs to whatever mod created it, and the
    // per-repaint rescan is what takes over, which is what the sentence at the end of the line promises.
    private static void ReportDowngradedControls(string where, FooterControls controls, float top, float room, ResultFooterSource source)
    {
        if (!ResultInteractionScanPolicy.DowngradeIsWhy(controls.Complete, controls.ParentCoverageComplete, controls.Tally)) return;
        if (!HeadingReports.Add("control-downgraded-" + where)) return;
        Plugin.Logger?.LogInfo($"[DIAG] {where} board allowed past an unmeasurable interaction: count={controls.Count},unmeasurable={controls.Unmeasurable},hidden={controls.Hidden},scanned={controls.ScannedCount},footerSource={source},{DescribeStrip(top, room)}; the control is active but its geometry could not be read, so its collision was ignored and this page is not deferred on account of it. Whether the table is drawn is the fit's own answer - it reports its line when it refuses - and the per-repaint scan re-reads the control and takes the vanilla page back the moment it has a rectangle that reaches the board.");
    }

    private static void ReportBoardOverlap(string where, FooterControls controls, float top, float room, ResultFooterSource source)
    {
        if (!HeadingReports.Add("board-overlap-" + where)) return;
        Plugin.Logger?.LogInfo($"[DIAG] {where} board deferred: an interaction the scan can see now reaches the placed board (count={controls.Count},hidden={controls.Hidden},scanned={controls.ScannedCount},footerSource={source},{DescribeStrip(top, room)}); vanilla is restored and the board is measured again before it may return.");
    }

    private static string _completionState;
    // Which settlement the result page is showing. The game keeps both result pages alive for the whole session
    // and raises them again for every expedition, so a page object can be the *same* one two settlements running:
    // this counter is what tells the settlement controls that the second one is a new layout. It is bumped where a
    // visit really begins (the page's own OnEnable), never by a repaint, a key or a configuration reload.
    private static int _settlementGeneration;

    // A new generation of the result screen. The fail-open override (`Forced`) belongs to *that settlement's own
    // press*, so it is dropped here rather than leaking into the next screen: a player who pressed F8 on one
    // settlement must not silently get the next one forced on, and a pending "this press owes a line" flag from a
    // previous screen must never be spent by a repaint of the new one. Nothing here writes the player's visibility
    // switches - `Plugin.PersistToggle` is not called, and the "a new settlement starts shown" rule still gives back
    // only what this mod itself turned off, where it always did (`Refresh` -> `Session.OweRestore`).
    private static void ClearTransientForced()
    {
        Forced = false;
        _forcedOutcomePending = false;
        _boardDrawnFrame = -1;
        _safeFrames = 0; _safeFrame = -1; _safePlate = default;
    }

    internal static void BeginSuccessPage(CM_PageExpeditionSuccess page)
    {
        FailureSequence.BeginPage(page != null ? page.Pointer : IntPtr.Zero);
        // `_holdPage` is deliberately *not* pre-set here. It is the page a real placement was recorded for
        // (`Fit` / the failure page's first placement), and `Refresh`'s "a new settlement is a new page" block reads
        // the difference between it and the page now up to decide whether this mod owes the game's own switches a
        // restore (`Session.OweRestore`). Pre-setting it made the failure page look like "the same page", so the
        // restore that the success page gets never ran on it - the recorded asymmetry of review #2.
        _settlementGeneration++;
        ClearTransientForced();
        ResetPlacement();
        ForgetPage();
        HeadingReports.Clear();
        _completionState = null;
        Success = page;
        CompletionTargets.BeginPage();
    }

    // A new visit to the failure page is a new screen: its title starts fading in from zero again, its footer is
    // revealed again, and none of the settle budgets the previous visit spent may be inherited. The game keeps
    // both result pages alive for the whole session and raises them for every expedition, so without this the
    // second expedition's failure page would be measured with a budget already used up - the same "already
    // waited once, so place on whatever is readable now" shortcut that F8 used to inherit.
    internal static void BeginFailurePage(CM_PageExpeditionFail page)
    {
        RestoreHidden();
        _settlementGeneration++;
        ResetPlacement();
        ClearTransientForced();
        ForgetPage();
        HeadingReports.Clear();
        Fail = page;
        FailureSequence.BeginPage(page != null ? page.Pointer : IntPtr.Zero);
        // `_holdPage` is deliberately *not* pre-set here (1.19.47 review #2). It is the page a real placement was recorded
        // for - `Fit` and the failure page's first placement write it - and `Refresh`'s "a new settlement is a new page" block
        // reads the difference between it and the page now up to decide whether this mod owes the game's own switches a
        // restore (`Session.OweRestore`). Pre-setting it here made the failure page look like "the same page", so the restore
        // the success page gets never ran on it.
    }

    internal static void FailureSequenceStarted(CM_PageExpeditionFail page, Il2CppSystem.Collections.IEnumerator sequence)
    {
        if (page == null || sequence == null) return;
        FailureSequence.BindSequence(page.Pointer, sequence.Pointer);
    }

    internal static void FailureSequenceFinished(CM_PageExpeditionFail page, IntPtr sequence)
    {
        if (page == null) return;
        FailureSequence.MarkFinished(page.Pointer, sequence);
    }


    // Blink completion is native state, not an inferred alpha or a camera culling flag.
    private static bool CompletionReady(Component page)
    {
        if (page == null) return false;
        Readiness.Bind(page.Pointer);
        if (Readiness.Ready) return true;
        var ledger = CompletionTargets.Ledger;
        var evidence = new CompletionEvidence(ledger.Artwork > 0, ledger.NamesReady,
            ledger.Artwork > 0, ledger.ArtworkReady, ledger.Artwork > 0);
        bool ready = Readiness.Observe(evidence);
        // `required` and `unscheduled` are the ledger's own account of what it was handed versus what the page's native
        // reveal really took over, and `aborted` counts routines that ended without revealing: without them a page whose
        // reveal can no longer finish looks exactly like one that is merely slow.
        string state = $"completed={ledger.Completed}/{ledger.Count}, names={ledger.Names}, artwork={ledger.Artwork}, required={ledger.Required}, unscheduled={ledger.Unscheduled}, aborted={ledger.Aborted}, ready={ready}";
        if (state != _completionState)
        {
            _completionState = state;
            Plugin.Logger?.LogInfo("[DIAG] success native reveal: " + state);
        }
        // 1.19.55: one line per page, not one per repaint. This sentence was written on every frame the latch already
        // answered "ready" - 895 lines in one recorded session - because `Readiness.Forget()` ran on every repaint (see
        // `Refresh`: `_holdPage` was only ever written by a *successful* placement, so a page that was still being refused
        // looked like a new page for ever). The latch below is the same fact stated once; the root cause is fixed in
        // `Refresh`/`ForgetPage`, and this line can no longer be produced by a repaint at all.
        if (ready && HeadingReports.Add("success-board-released"))
            Plugin.Logger?.LogInfo("[DIAG] success board released after the game's sector BlinkIn routines completed; no timeout or alpha threshold bypass.");
        return ready;
    }

    // The success page's completion section and its lobby button, reduced to the strip between them. Both
    // are read from the page itself: the sector header and the four cleared-sector icons make up the
    // completion section (the white plate behind them is not a part - it draws far below the row), and the
    // deepest laid-out edge of those parts is the one the board must clear.
    // The answer is whether that *section* could be measured at all - not whether the strip happens to fit -
    // because the caller decides placement from it through `ResultsUpperBound`, and a section nobody could read
    // must never be turned into a fraction of the window.
    private static bool SuccessRegion(Component page, out ResultsSafeArea area, out bool buttonMeasured, out float buttonTop, out float buttonHeight, out FooterControls controls, out bool nativeFooterVisible, out ResultFooterSource footerSource, out ResultsBand pageBand, LayoutRect board = default, bool inspectBoard = false)
    {
        pageBand = ResultsBand.None;
        area = default;
        buttonMeasured = false;
        buttonTop = float.NaN;
        buttonHeight = float.NaN;
        controls = default;
        nativeFooterVisible = false;
        footerSource = ResultFooterSource.None;
        var success = page != null ? page.TryCast<CM_PageExpeditionSuccess>() : null;
        if (success == null) return false;
        float completion = float.NaN, button = float.NaN;
        // The section's final-layout edge and, separately, the drawn edge (diagnostics) - the region line below
        // prints both, so a log alone shows how far the reveal has drawn versus where the strip hangs from.
        float drawnEdge = float.NaN;
        string completionSource = "none";
        // The lobby button's rectangle and the edge it produces are read inside the guarded block, and both
        // are needed after it, so they are declared here and only filled there.
        LayoutRect lobbyRect = default;
        ResultsRegion.LobbyEdgeReading wall = default;
        // 1.19.51: the column the board stands in (see `LayoutColumn`) - filled inside the guarded block from
        // this page's own readings, and read by the scan and by the line at the end of this method.
        LayoutColumn boardColumn = LayoutColumn.None;
        try
        {
            // 1.19.21: whether a laid-out part counts is the completion ledger's answer, not a draw state - the part
            // is present on this page and switched on, or scheduled for it but not switched on yet - and the drawn
            // pixels are never mixed into the band: the native reveal activates parts over several frames, and every
            // mixing of the drawn edge into the strip is what made the recorded page's band walk while its layout
            // was fixed all along. The white plate is not a part; see the `CompletionParts` comment below.
            var parts = new List<CompletionPart>(CompletionParts)
            {
                Read(success.m_sectorHeader),
                Read(success.m_sectorIconMain),
                Read(success.m_sectorIconSecond),
                Read(success.m_sectorIconThird),
                Read(success.m_sectorIconAllCompleted)
            };
            // ---- the final edge -------------------------------------------------------------------------------
            //
            // The figure the drawn rule produces is what the section *draws*; it is not where the section ends. One
            // recorded success page activated its parts over several frames (`SetActiveBlink`), so the drawn edge
            // walked 250 -> 272 -> 314 -> 366 while the page was up, and a board fitted to the first of those was
            // fitted to a page that no longer existed. `CompletionFinalBounds` answers with the section's final edge
            // instead: a part that is on the page and switched on - or scheduled by the completion ledger but not
            // switched on yet - contributes its laid-out slot (`OwnBottom`), which is final from the first frame the
            // page laid it out, and `_finalUndrawn` counts the parts whose pixels have not arrived. A part the page
            // never scheduled (an unused sector icon of a short run, whose BlinkIn never ran) contributes nothing,
            // and the drawn depth stays out of the band entirely: however far the reveal has come, the strip does
            // not move - which is the whole of the defect this answers.
            // 1.19.41: the section's edge is the parts' laid-out slots while the page's own reveal is still running, and
            // once it has finished every part contributes the pixels it really draws when those are a *complete*
            // measurement of it, its container rectangle otherwise (`CompletionFinalBounds.Placed` -> `FinalEdge`).
            // That answers both directions: a container that ends above the captions its icon draws no longer presses
            // the board on them, and a container that reserves a large blank slot below everything it draws no longer
            // shortens the strip for a board the player reported as too small. Before the reveal finishes the old
            // reading stands unchanged.
            // F3: `ContentSettled` decides whether the content reading may already be the band on a page whose ledger
            // cannot answer (every target unscheduled, or an aborted one and nothing still running) *and* whose geometry
            // has read the same twice. Otherwise the container stands, exactly as before.
            bool contentSettled = ContentSettled;
            completion = CompletionFinalBounds.Placed(parts, RevealFinished, contentSettled, out int finalDeepest, out _finalUndrawn);
            // 1.19.52 (F1): the strip may hang from the laid-out section edge, but it may never hang above content this
            // page has already painted. `Placed` answers with a *container* for any part whose child set is not a
            // complete measurement - the recorded page's icon part read own=198, visual=333, hidden=14, drawn=6 - and
            // the band then hung from 233 while the captions reached 333, so the board's top edge covered them. The
            // drawn floor is a floor only: it never claims the section is complete, it never re-uses a container that
            // reserves blank space, and a NaN/inactive/undrawn part contributes nothing.
            float drawnFloor = CompletionFinalBounds.DrawnFloor(parts);
            _drawnFloorCompletion = drawnFloor;
            if (float.IsFinite(drawnFloor) && (!float.IsFinite(completion) || drawnFloor > completion + 0.5f))
            {
                completion = drawnFloor;
                completionSource += ", drawn floor";
            }
            ReportCompletionParts(parts, completion, finalDeepest, contentSettled);
            drawnEdge = CompletionBounds.Deepest(parts, out int drawnDeepest); // diagnostics only
            int named = float.IsFinite(completion) ? finalDeepest : drawnDeepest;
            completionSource = named >= 0 && named < CompletionNames.Length ? CompletionNames[named] : "none";
            if (named == finalDeepest && named != drawnDeepest && float.IsFinite(completion))
                completionSource += ", final edge";
            // The scan retains every active interaction for collision rejection, but its footer edge is filtered
            // against this page's measured completion boundary. Upper-page controls cannot shrink the strip.
            bool buttonActive = ButtonActive(success.m_btnLeaveExpedition);
            bool pressRead = VisibleRect(success.m_btnLeaveExpedition, out LayoutRect pressed);
            bool layoutRead = TryScreenRect(success.m_btnLeaveExpedition, out lobbyRect);
            // 1.19.52 (F2/F6): the column this page's board really stands in, from what this page can measure. The
            // footer button is *not* the board - a 440 px button under a 330 px table is not evidence for the table's
            // width - so the reading is the board's own placed rectangle, else the width the fit really measured for
            // this page (`_widthProbe`), and nothing else. A page that has neither keeps the column unknown, which
            // fills every rectangle and keeps the band this build had before the rule existed.
            boardColumn = BoardColumn();
            controls = ScanFooterControls(page, completion, MessageGap, null, board, inspectBoard, boardColumn);
            bool pressInStrip = pressRead && float.IsFinite(controls.BoardTop)
                && pressed.Y >= controls.BoardTop - 0.5f && pressed.Bottom > controls.BoardTop;
            bool layoutInStrip = layoutRead && float.IsFinite(controls.BoardTop)
                && lobbyRect.Y >= controls.BoardTop - 0.5f && lobbyRect.Bottom > controls.BoardTop;
            wall = ResultsRegion.ResolveLobbyEdge(buttonActive && (pressInStrip || layoutInStrip),
                pressInStrip, pressInStrip ? pressed.Y : float.NaN,
                layoutInStrip, layoutInStrip ? lobbyRect.Y : float.NaN, controls.FooterTop, Screen.height);
            if (float.IsFinite(controls.FooterTop) && (!wall.Measured || controls.FooterTop < wall.Top))
                wall = new ResultsRegion.LobbyEdgeReading(controls.FooterTop, ResultsRegion.LobbyEdgeSource.Band);
            nativeFooterVisible = success.m_lobbyButtonVisible;
            if (pressInStrip) lobbyRect = pressed;
            button = wall.Top;
        }
        catch (Exception error)
        {
            if (HeadingReports.Add("success-region-error"))
                Plugin.Logger?.LogWarning("[DIAG] success region could not be read this frame, so the board is deferred rather than placed against a guess: " + error.Message);
            return false;
        }
        bool completionMeasured = float.IsFinite(completion);

        buttonMeasured = wall.Measured;
        footerSource = ResultFooterProvenance.Of(wall);
        buttonTop = buttonMeasured ? button : float.NaN;
        // The height of that same button, and only of that button: when the bound came from the drawn band
        // there is no single control to measure, and the board keeps its absolute readability floor. The
        // laid-out rectangle is that control's own height as well - the reveal it is waiting for is alpha, not
        // geometry - so the floor describes the button the player is being asked to press either way.
        buttonHeight = wall.FromButton && lobbyRect.Height > 0f ? lobbyRect.Height : float.NaN;
        if (GuestClient())
        {
            // 客机页固定存在等待状态文字，任何交互扫描都不可能证明本页为空，所以放行只看锚点（完成区）渲染完成
            // + 固定页面几何。量到的控件只做事后下界收紧，且不得把区域压到最小板高以下，永远不构成放行前提。
            area = completionMeasured && ResultsGuestPageGeometry.TryResolve(completion, Screen.width, Screen.height,
                MessageGap, MinimumGuestStripHeight, out ResultsSafeArea guestArea)
                ? guestArea : default;
            if (area.Fits && float.IsFinite(controls.FooterTop) && controls.FooterTop - MessageGap > area.Top)
            {
                float tightened = Math.Min(area.Bottom, controls.FooterTop - MessageGap);
                if (tightened > area.Top && tightened - area.Top >= MinimumGuestStripHeight)
                    area = new ResultsSafeArea(area.Top, tightened);
            }
        }
        else
        {
            area = buttonMeasured
                ? ResultsRegion.Resolve(completionMeasured, completion, true, buttonTop, Screen.height, MessageGap)
                : completionMeasured
                    ? new ResultsSafeArea(Math.Max(0f, completion) + MessageGap, Screen.height - 8f)
                    : default;
            // Every interaction the scan found stays in the per-draw collision set, but only a control that really
            // reaches this page's own measured completion boundary may move the strip: the strip starts at that
            // boundary plus the message gap, so a control above the completion section can never pull it down.
            if (area.Fits && float.IsFinite(controls.BoardTop))
                area = new ResultsSafeArea(Math.Max(area.Top, controls.BoardTop), area.Bottom);
            // 1.19.16 - the strip above the button is the *preferred* band, not the only one the page offers.
            //
            // The button's own upper edge is a lower bound for that band; it is not the end of the page. A real host
            // success page put its completion section at 519 of 1440 and its lobby button at 683, which is a 132 px
            // strip - and the settlement board cannot be drawn under 270 px at any readable glyph size, so the board
            // was never drawn at all even though more than 700 px of the page below that button were empty. When the
            // strip cannot hold the board's own floor, the measured controls therefore become a gap the table steps
            // around and the board stands in the free band below them. Every control the scan measured is outside that
            // band by construction (`ResultBoardBands.TryBelowActions`), so nothing a player can press is covered, and
            // the blank row at the button stays blank.
            float anchoredHeight = area.Height;
            float anchoredTop = area.Top, anchoredBottom = area.Bottom;
            // ---- D: the page is judged by the room it measured, not by a printable preference ---------------------
            //
            // `_boardFloor` arrives here as the readable floor, because this method runs inside `Placement` and
            // `Prepare` only learns `room` after that returns. Comparing a measured 269 px strip against a 270 px
            // preference is the cliff this build is fixing: 269 px drew nothing at all while 286 px drew a 6 px table,
            // although the table can be drawn far shorter than either. The strip this page just measured is the bound
            // that really holds, so it is resolved here - at this frame's own row count - and written back to
            // `_boardFloor` for the rest of the frame, so the fit, the fail-open ladder and the log lines all quote one
            // figure about the room.
            float stripRowEm = HudTable.RowEm(ResultsFontGrowth.MinimumReadablePixels);
            int stripRows = ImpactBoard.RowCount(
                ImpactBoard.SplitKillTiersFor(anchoredHeight,
                    ResultsBandFit.EmPerGlyphPixel(Plugin.Density, stripRowEm)));
            float stripFloor = area.Fits
                ? ResultsFontGrowth.DrawableBoardHeight(stripRows, stripRowEm, Plugin.Density, anchoredHeight)
                : _boardFloor;
            bool roomKnown = stripFloor > 0f && float.IsFinite(stripFloor);
            // C: the band-below-actions question is asked with the figure the room produced, which is the only figure
            // that can answer it. Asking with the readable floor made the escape hatch unreachable on every page whose
            // free band was shorter than a readable table - which, on the run this came from, was every page: the band
            // under the controls measured 85-88 px against a 270 px question.
            float bandQuestion = roomKnown ? stripFloor : _boardFloor;
            if (roomKnown && stripFloor < _boardFloor) _boardFloor = stripFloor;
            if (ResultPageRolePolicy.UsesBandBelowActions(Role(), anchoredHeight, bandQuestion, controls.BelowActions.Height))
            {
                area = new ResultsSafeArea(controls.BelowActions.Top, controls.BelowActions.Bottom);
                footerSource = ResultFooterSource.BelowActions;
                if (HeadingReports.Add("success-band-below-actions"))
                    Plugin.Logger?.LogInfo($"[DIAG] success strip cannot hold the board's own floor: role={ResultPageRolePolicy.Name(Role())}, "
                        + $"strip=({anchoredTop:0}..{anchoredBottom:0}) is {anchoredHeight:0}px but the {stripRows}-row board cannot be drawn under {_boardFloor:0}px at its shortest drawable grid on a {Screen.height} window; "
                        + $"the page's measured actions are stepped around instead of ending the strip - {DescribeLobbyWall(wall)}, lobbyEdge={DescribeLobbyEdge(wall)} - "
                        + $"and the free band below them is offered to the fit: boardBand=({controls.BelowActions.Top:0}..{controls.BelowActions.Bottom:0}) is {controls.BelowActions.Height:0}px, footerSource=below-actions-band. "
                        + "Every control the scan measured is outside that band, so no interaction is covered.");
            }
            else if (area.Fits && float.IsFinite(_boardFloor) && _boardFloor > 0f && anchoredHeight + 0.5f < _boardFloor)
            {
                // The honest refusal: the strip cannot hold the board *even at the shortest grid this build can draw*
                // and the page leaves no band that could. The numbers are the whole point of the line - a strip of
                // 132 px and a band of 40 px are two different faults - and `need` here is the room figure, not the
                // readable one, so the line can no longer say "270 px needed" about a 269 px strip that could have
                // held a 135 px table.
                if (HeadingReports.Add("success-band-unavailable"))
                    Plugin.Logger?.LogWarning($"[DIAG] success board deferred: role={ResultPageRolePolicy.Name(Role())}, need={_boardFloor:0}px for the {ImpactBoard.RowCount(true)}-row board at its shortest drawable grid (readable floor {_readableFloor:0}px), "
                        + $"have={anchoredHeight:0}px in the strip ({anchoredTop:0}..{anchoredBottom:0}) above the page's own actions"
                        + (controls.BelowActions.Fits
                            ? $", and the free band below them ({controls.BelowActions.Top:0}..{controls.BelowActions.Bottom:0}) is only {controls.BelowActions.Height:0}px"
                            : ", and the page's measured actions leave no free band below them")
                        + $", {DescribeLobbyWall(wall)}, lobbyEdge={DescribeLobbyEdge(wall)}, screen={Screen.width}x{Screen.height}; the vanilla result screen is kept rather than drawn over any control.");
            }
        }
        // ---- the agreed band: icon row above, the footer this machine can see below ---------------------------------
        //
        // The strip the two branches above produced is *a* band, not *the* band: the success branch hangs it from
        // the completion section's laid-out edge - the label and the four icon slots, never the white plate - and
        // the guest branch runs it to the foot of the window. Both are replaced here by the one rule this mod
        // agreed on with its players:
        //
        //   top    the sector-icon row's own bottom edge (label included) plus a clearance scaled from 16 px at 1080p
        //   bottom the highest footer this machine can measure minus the same clearance: the host's leave button, or
        //          the client's waiting-status text block, whichever the page really draws
        //
        // The board hangs from the top edge, so a taller band is a taller table and never a table further down; the
        // measured free band under the page's actions (1.19.16) is left exactly as it was measured, because its own two
        // edges are the whole contract.
        float iconRow = IconRowBottom(success, RevealFinished, ContentSettled);
        bool iconRowCurrent = float.IsFinite(iconRow);
        if (iconRowCurrent) _iconRowBottom = iconRow;
        float waitingText = FooterTextTop(success, out ResultBoundSource waitingTextSource);
        // 1.19.53: the band hangs from the same combined anchor the per-repaint safety check protects - `max(icon row,
        // drawn floor)` when this frame really measured the row, and never a last-trusted value passed off as a current
        // one. Without this the region line printed 349 while the band was resolved from 249, and the board was then
        // refused on every repaint for standing above the captions the very same frame had painted.
        float bandAnchor;
        ResultBoundSource bandAnchorSource;
        if (ResultSuccessBandAnchor.Resolve(iconRowCurrent, iconRow, _drawnFloorCompletion, out float anchoredRow, out bool anchorFromDrawn))
        {
            bandAnchor = anchoredRow;
            bandAnchorSource = ResultBoundSource.IconRow;
            if (anchorFromDrawn) completionSource += ", band hangs from the drawn floor";
        }
        else
        {
            bandAnchor = _iconRowBottom;
            bandAnchorSource = float.IsFinite(_iconRowBottom) ? ResultBoundSource.LastTrustedIconRow
                : ResultBoundSource.ScreenSafeArea;
        }
        if (ResultsBounds.TryResolve(Screen.height, bandAnchor, bandAnchorSource,
                buttonMeasured ? buttonTop : float.NaN, waitingText, waitingTextSource, controls.FooterTop, out ResultsBand band))
        {
            pageBand = band;
            ReportBand("success", band, bandAnchor, waitingText, _boardFloor);
            if (band.Fits && footerSource != ResultFooterSource.BelowActions)
            {
                // The band is the whole answer when the page handed nothing else, and it tightens the page's own strip
                // otherwise: never above the icon row, never below the footer the player is reading.
                float bandBottom = area.Fits ? Math.Min(band.Bottom, area.Bottom) : band.Bottom;
                if (!(bandBottom > band.Top)) bandBottom = band.Bottom;
                area = new ResultsSafeArea(band.Top, bandBottom);
            }
        }
        // ---- F5: the band below the page's actions, when the strip above cannot show the whole table --------------
        //
        // 1.19.52. A control that really stands in the board's column may not be covered, but it is also not the end of
        // the page: the recorded page put an interaction at y=455 (which one, the 1.19.50 log does not say) and the
        // strip above it had to hold the whole table. When it cannot, and the free band the scan measured *below*
        // every interaction shows strictly more of the same table at the same glyph, the board belongs there - not
        // squeezed into the short strip with the player left to find a wheel, and not forced into a band that cannot
        // hold it either (`ResultSuccessBandChoice` compares rows, never forces a height). Both bands come from the
        // page's own measured geometry, the footer wall still bounds the lower one, and a page whose bands are both
        // too short keeps its rows on the wheel exactly as before. Host only and success only: the client strip is its
        // fixed geometry and the failure page's band is not this rule's business.
        if (Role() == ResultPageRole.Host && footerSource != ResultFooterSource.BelowActions
            && area.Fits && controls.BelowActions.Fits && controls.BelowActions.Height > area.Height + 0.5f)
        {
            float targetGlyph = ResultAutoBoardContract.TargetGlyphPixels(Screen.height);
            float targetRow = ResultsBandFit.RowHeight(targetGlyph, Plugin.Density);
            int wholeRows = ImpactBoard.RowCount(true);
            float upperTop = area.Top, upperBottom = area.Bottom, upperHeight = area.Height;
            if (ResultSuccessBandChoice.PrefersBelow(upperHeight, controls.BelowActions.Height, wholeRows,
                    ChromeHeader(), targetRow, out int rowsAbove, out int rowsBelow))
            {
                area = new ResultsSafeArea(controls.BelowActions.Top, controls.BelowActions.Bottom);
                footerSource = ResultFooterSource.BelowActions;
                if (HeadingReports.Add("success-band-below-for-whole-table"))
                    Plugin.Logger?.LogInfo($"[DIAG] success strip cannot show the whole table: the strip above the page's "
                        + $"actions ({upperTop:0}..{upperBottom:0} is {upperHeight:0}px, {rowsAbove} of {wholeRows} rows at the "
                        + $"{targetGlyph:0}px glyph this window aims at) is shorter than the free band below them "
                        + $"({controls.BelowActions.Top:0}..{controls.BelowActions.Bottom:0} is {controls.BelowActions.Height:0}px, "
                        + $"{rowsBelow} of {wholeRows} rows), so the board stands there instead - the same table at the same "
                        + $"glyph, more of it on the page, and every control the scan measured stays outside that band. "
                        + $"role={ResultPageRolePolicy.Name(Role())}, screen={Screen.width}x{Screen.height}.");
            }
        }
        if (!controls.ProvenEmpty || nativeFooterVisible) SuccessNoFooter.Forget();
        // The line names which element produced the upper bound, which band the board may use and the figures behind
        // both, because a board that starts too low is indistinguishable from a board that starts too high without it.
        //
        // It says what was *measured* and nothing about what will be drawn: whether the table lands in this strip is
        // the fit's answer, and the fit reports its own line - with the real numbers - when it refuses. The sentence
        // this line used to end with claimed the strip was either drawn already or laid out while still revealing, which
        // was read as a board being on the page; on the host page it was printed for a strip the board was then never
        // drawn into.
        //
        // The same rule applies field by field. `boardBand=` names the band the board may use (`band=` collided with
        // `lobbyEdge=band`, which is a *source*, not a band). `lobbyButton=` is printed only when the page's own button
        // was really read: a page whose bound came from the drawn band (`lobbyEdge=band`) has no measured button, and
        // the band's own top edge is printed as `lobbyEdgeValue=<px>,source=<band|none>` instead - on the run this
        // release came from the real button was at 683 while the band started at 743, so calling 743 `lobbyButton` was
        // a true field name over a false number. `ResultFooterProvenance.LobbyWall` is the only place that spells out
        // either name, so the per-repaint refusal in `BoardStillSafe` prints that same figure under that same field. A
        // page that handed over no strip at all says `boardTop=unmeasurable`, not a zero box.
        string wallControl = controls.Wall != null && controls.Wall.Length > 0 ? controls.Wall : "none";
        if (HeadingReports.Add("success-region"))
            Plugin.Logger?.LogInfo($"[DIAG] success region: anchorSource={completionSource}, completion={(completionMeasured ? completion.ToString("0") : "unmeasurable")} (deepest part of the completion section's final layout - the pixels a part draws, or its own laid-out rectangle while it has not drawn yet (laidOutParts={_finalUndrawn}), drawnEdge={(float.IsFinite(drawnEdge) ? drawnEdge.ToString("0") : "none")}, gap={MessageGap:0}px), "
                + $"{DescribeLobbyWall(wall)}, lobbyEdge={DescribeLobbyEdge(wall)}, "
                + (area.Fits ? $"boardTop={area.Top:0}, room={area.Height:0}" : "boardTop=unmeasurable, room=unmeasurable")
                + $" of a {Screen.height} window, "
                + $"boardBand={DescribeBand(_holdValid ? _holdStrip.FooterSource : footerSource)}, boardFloor={(_boardFloor):0}, "
                + (float.IsFinite(_drawnFloorCompletion) ? $"drawnFloor={_drawnFloorCompletion:0}, " : "drawnFloor=none, ")
                + (boardColumn.Known ? $"boardColumn=({boardColumn.Left:0}..{boardColumn.Right:0}), " : "boardColumn=unmeasurable, ")
                + $"besideColumn={controls.BeyondColumn}, wallControl={wallControl}, role={ResultPageRolePolicy.Name(Role())}; "
                + "the strip is bounded by this page's own final-layout geometry - the completion section's laid-out parts above and, below, the lobby button's upper edge or the free band under the page's measured actions - "
                + "never by a hidden, transparent or off-page control, and never by how much of the section has drawn. All four players scale into whichever band is used, "
                + "and the fit that decides whether the table is drawn into it reports its own line when it is not.");
        ReportDowngradedControls("success", controls, area.Top, area.Height, footerSource);
        // What the caller may act on is whether the *section* was measured: the strip's own fit is asked separately
        // through `ResultsUpperBound.Decide`, so a section that could not be read and a strip that cannot hold the
        // table stay two different answers instead of one false.
        return completionMeasured;
    }

    // 1.19.52 (F2/F6): the horizontal column this page's settlement board occupies, from what this page can really
    // measure.
    //
    // 1.19.51 read it from the board's placed rectangle and, before the first placement, from the page's centred
    // footer control. Both halves were wrong in a way the tests could not see: a footer button is not the board (a
    // 440 px button under a 330 px table describes the button, not the table, and a wider table could not be proven at
    // all), and a first placement with no width fell back to `LayoutColumn.None` - the old band and the eight-row plate
    // this round exists to remove.
    //
    // The width is now the table's own, in this order and nothing else:
    //   1. the rectangle a board really stands in on this page (`_holdBoard`, the same rect `OverlapsBoard` judges);
    //   2. the width the fit really measured for this page's content (`_widthProbe`, taken on the frame the fit ran
    //      without a column - the bounded first phase of the two-phase measurement);
    //   3. nothing: `LayoutColumn.None`, which fills every rectangle and answers the band this build had before the
    //      rule existed.
    // A column is never widened by the footer, never clamped to `Screen.width - 48` behind the placement's back, and
    // never derived from the window alone.
    private static LayoutColumn BoardColumn()
    {
        if (_holdValid && _holdBoard.Width > 0f && _holdPage != IntPtr.Zero && _page != null
            && ResultVisibility.SamePage(_holdPage, _page.Pointer))
            return new LayoutColumn(_holdBoard.X, _holdBoard.Right);
        if (float.IsFinite(_widthProbe) && _widthProbe > 0f && Screen.width > 0)
        {
            // 1.19.53 (F6): the width the fit measured is the width this column uses - no implicit `min(width,
            // screen - 48)`, which described a box the placement never used. The rectangle is the one `TryPlace`
            // computes for it (centred plus the player's offset, clamped to the window the same way `ResultsPlacement`
            // clamps), so the column and the board can only agree.
            float width = _widthProbe;
            float offset = Plugin.ResultsOffsetX.Value;
            if (!float.IsFinite(offset)) offset = 0f;
            float x = (Screen.width - width) * 0.5f + offset;
            x = Math.Max(0f, Math.Min(x, Screen.width - width));
            return new LayoutColumn(x, x + width);
        }
        return LayoutColumn.None;
    }

    // Which measurement produced the strip's lower bound, spelled out once so a log line is enough to tell the
    // three cases apart: the button as the player can press it, the button's own layout while its reveal is
    // still running, and the drawn band of a page whose button could not be read by name.
    private static string DescribeLobbyEdge(ResultsRegion.LobbyEdgeReading wall)
    {
        switch (wall.Source)
        {
            case ResultsRegion.LobbyEdgeSource.Pressable: return "pressable";
            case ResultsRegion.LobbyEdgeSource.Layout: return "layout";
            case ResultsRegion.LobbyEdgeSource.Band: return "band";
            default: return "unmeasurable";
        }
    }

    // The button's own upper edge, or the honest statement of the wall this page really gave - in one place, so the
    // three `SuccessRegion` lines that call it and the per-repaint refusal in `BoardStillSafe` cannot drift apart again.
    //
    // The field names and the argument for them live in `ResultFooterProvenance.LobbyWall` (`ResultVisibility.cs`):
    // `lobbyButton=` means that control was read, and a band top is printed as `lobbyEdgeValue=` with its source. This
    // wrapper only turns the resolved reading into the pair the shared helper takes.
    private static string DescribeLobbyWall(ResultsRegion.LobbyEdgeReading wall) => ResultFooterProvenance.LobbyWall(wall);

    // The strip a line is about and the room it gave, printed only when the page really resolved one.
    //
    // `ResultsSafeArea.Fits` is `Height > 0`, and the zero box an unresolved one holds is not a strip at 0 with no
    // room - it is no strip at all. Printing `top=0, room=0` for it is the same half-truth as printing `0..0` for a
    // held-back board: a reader has no way to tell "the page gave 0 px" from "the page gave nothing".
    private static string DescribeStrip(float top, float room)
    {
        if (!(room > 0f) || !float.IsFinite(top) || !float.IsFinite(room)) return "top=unmeasurable, room=unmeasurable";
        return "top=" + top.ToString("0") + ", room=" + room.ToString("0");
    }

    // Which band the board may use, spelled out so one log line is enough to tell the two apart: the strip between the
    // page's anchor and the controls it measured, and the free band below those controls when that strip cannot hold the
    // board at all.
    private static string DescribeBand(ResultFooterSource source)
    {
        switch (source)
        {
            case ResultFooterSource.BelowActions: return "below-actions-band";
            case ResultFooterSource.ClientNoFooterSafeArea: return "client-fixed-page-geometry";
            case ResultFooterSource.NamedControl: return "anchor-to-named-button";
            case ResultFooterSource.InteractiveBand: return "anchor-to-measured-band";
            default: return "unresolved";
        }
    }

    // The completion section's parts, in the order `CompletionNames` spells them out for the diagnostic line.
    // The white plate (`m_sectorsWhiteBox`) is deliberately not one of them: it is a plate behind the row, it draws
    // far below the icons, and counting it is what pushed a real client board some 450 px down its page (1.19.18) -
    // `IconRowBottom` already excludes it, and the band's own part list excludes it too, so the two agree.
    private const int CompletionParts = 5;
    private static readonly string[] CompletionNames =
        { "sectorHeader", "sectorIconMain", "sectorIconSecond", "sectorIconThird", "sectorIconAllCompleted" };

    // is switched on this frame, whether the page's completion ledger scheduled its reveal, how visible it is, the
    // deepest renderer that is really drawing under it - the captions included - and its own container rectangle.
    // The *band* hangs from the container rectangle alone (`CompletionFinalBounds`); the drawn pixels are what the
    // diagnostic `CompletionBounds` reads, and the scheduled flag is what lets a laid-out-but-not-yet-switched-on
    // slot contribute its slot while the unused sector icon of a short run contributes nothing - `activeInHierarchy`
    // alone cannot tell the reveal's in-between state from a slot this page never schedules.
    //
    // `GetComponentsInChildren(..., true)` returns the renderers of deactivated children as well, and their own
    // `enabled` flag stays true while their object is switched off: that is exactly how the optional sector icons
    // of a squad that cleared one sector kept a bottom edge on the page and pushed the board down by their height.
    // They are skipped here, by object state rather than by name, so a build that adds a fifth icon needs no change.
    private static CompletionPart Read(Component element)
    {
        if (element == null) return CompletionPart.Missing;
        bool active;
        try { active = element.gameObject.activeInHierarchy; } catch { return CompletionPart.Missing; }
        // The completion ledger, not the switch state, is what says whether this page really scheduled the part's
        // reveal (`SectorRevealStartedPatch` requires every BlinkIn target that belongs to this success page; its
        // comment spells out the inactive-optional-prefab-instance case).
        bool scheduled = false;
        try { scheduled = CompletionTargets.Ledger.IsScheduled(element.gameObject.Pointer.ToInt64()); } catch { }
        float alpha = ControlAlpha(element);
        float own = TryScreenRect(element, out LayoutRect rect) ? rect.Bottom : float.NaN;
        float visual = float.NaN;
        // Completeness of the content reading (F2). Three counts, and the part is only "complete" when all three agree
        // with the pixels on the screen:
        //   * `drawn`      visible candidates whose rectangle really answered;
        //   * `unmeasured` visible candidates that could not be projected this frame - the part must keep its container
        //                  until they can, or the band would be taken from a shallower reading than the screen shows;
        //   * `hidden`     children that draw nothing (switched off, disabled, faded): not candidates, but they still
        //                  keep the container, because one of them may be switched on later.
        bool measured = false;
        int hidden = 0, unmeasured = 0, drawn = 0;
        try
        {
            var renderers = element.GetComponentsInChildren<Renderer>(true);
            if (renderers != null)
                foreach (Renderer child in renderers)
                {
                    CompletionCandidateKind kind = CompletionCandidatePolicy.Classify(child != null,
                        child != null && child.gameObject.activeInHierarchy, child != null && child.enabled,
                        child != null ? ControlAlpha(child) : float.NaN);
                    if (kind == CompletionCandidateKind.Missing) continue;
                    if (kind == CompletionCandidateKind.Hidden) { hidden++; continue; }
                    if (TryScreenRect(child, out LayoutRect nested)) { visual = ResultsRegion.LowestBottom(nested.Bottom, visual); drawn++; }
                    else unmeasured++;
                }
            // A visible `Graphic` - an `Image`, a `RawImage`, a `TextMeshProUGUI` - carries a `CanvasRenderer`, which is
            // not a `Renderer` at all: the route above cannot see it, and skipping it is how a part could report
            // `complete` while a picture or a caption under it was never measured. Each visible graphic is measured in
            // its own right, and one that cannot be projected makes the part incomplete rather than invisible.
            var graphics = element.GetComponentsInChildren<Graphic>(true);
            if (graphics != null)
                foreach (Graphic graphic in graphics)
                {
                    CompletionCandidateKind kind = CompletionCandidatePolicy.Classify(graphic != null,
                        graphic != null && graphic.gameObject.activeInHierarchy, graphic != null && graphic.enabled,
                        graphic != null && float.IsFinite(graphic.color.a) ? graphic.color.a : float.NaN);
                    if (kind == CompletionCandidateKind.Missing) continue;
                    if (kind == CompletionCandidateKind.Hidden) { hidden++; continue; }
                    // A `TextMeshProUGUI` has no renderer bounds but does have a laid-out text extent; the text route
                    // below reads it, and this route only needs to know the graphic is accounted for.
                    if (graphic is TMP_Text) { drawn++; continue; }
                    if (TryScreenRect(graphic, out LayoutRect picture)) { visual = ResultsRegion.LowestBottom(picture.Bottom, visual); drawn++; }
                    else unmeasured++;
                }
            // TextMeshPro is measured in its own right, because the renderer route misses half of it: a world-space text
            // carries a MeshRenderer (and its bounds are the generated glyph quads, so that route already answers for
            // it), while a `TextMeshProUGUI` carries a `CanvasRenderer` instead - and a CanvasRenderer has no bounds at
            // all. `TryTextRect` reads the text's own laid-out extent for both flavours, and the deepest one joins the
            // same figure. Only text that is switched on is read: a caption inside a slot the page has not revealed yet
            // is not part of what the player sees - and while one is switched off the part is not *completely* measured,
            // so the band cannot be tightened past it.
            var texts = element.GetComponentsInChildren<TMP_Text>(true);
            if (texts != null)
                foreach (TMP_Text text in texts)
                {
                    CompletionCandidateKind kind = CompletionCandidatePolicy.Classify(text != null,
                        text != null && text.gameObject.activeInHierarchy, text != null && text.enabled,
                        text != null ? ControlAlpha(text) : float.NaN);
                    if (kind == CompletionCandidateKind.Missing) continue;
                    if (kind == CompletionCandidateKind.Hidden) { hidden++; continue; }
                    if (TryTextRect(text, out LayoutRect caption)) { visual = ResultsRegion.LowestBottom(caption.Bottom, visual); drawn++; }
                    else unmeasured++;
                }
            measured = true;
        }
        catch { measured = false; }
        return new CompletionPart(true, active, alpha, own, visual, scheduled, measured, hidden, unmeasured, drawn);
    }
    private static CompletionPart Read(GameObject host) => host == null ? CompletionPart.Missing : Read(host.transform);
    private static float Top(Component element) => TryScreenRect(element, out LayoutRect rect) ? rect.Y : float.NaN;

    // The whole screen-space rectangle of the part of a page control the player can actually see, or false
    // when the control is not on this page at all. Three things are asked of it, and all three have cost
    // the board height before: the control has to be switched on, it has to be drawn (its canvas group and
    // its own graphics are not faded out), and it has to lie on the frame the player is looking at. The
    // rectangle spans the highest and lowest renderer the control draws - the real edges of the button, not
    // the container's padded rect - so the strip can run from the heading down to the button the player
    // presses, and that button's own height says how large the control being pressed really is.
    private static bool VisibleRect(Component element, out LayoutRect rect)
    {
        rect = default;
        if (element == null) return false;
        bool active;
        try { active = element.gameObject.activeInHierarchy; } catch { return false; }
        if (!active) return false;
        float top = float.NaN, bottom = float.NaN;
        try
        {
            var renderers = element.GetComponentsInChildren<Renderer>(true);
            if (renderers != null)
                foreach (Renderer child in renderers)
                {
                    if (child == null || !child.enabled) continue;
                    if (!TryScreenRect(child, out LayoutRect nested)) continue;
                    top = float.IsFinite(top) ? Math.Min(top, nested.Y) : nested.Y;
                    bottom = float.IsFinite(bottom) ? Math.Max(bottom, nested.Bottom) : nested.Bottom;
                }
        }
        catch { }
        // A control that draws nothing measurable is read from its own rect instead.
        if (!float.IsFinite(top) && TryScreenRect(element, out LayoutRect own)) { top = own.Y; bottom = own.Bottom; }
        if (!float.IsFinite(top) || !float.IsFinite(bottom)) return false;
        if (!ResultsButtonPolicy.Visible(active, ControlAlpha(element), top, bottom, Screen.width, Screen.height))
            return false;
        float x = 0f, width = Screen.width;
        if (TryScreenRect(element, out LayoutRect bounds) && float.IsFinite(bounds.X) && float.IsFinite(bounds.Width) && bounds.Width > 0f)
        {
            x = bounds.X; width = bounds.Width;
        }
        rect = new LayoutRect(x, top, width, Math.Max(0f, bottom - top));
        return true;
    }

    // The visible top edge alone: the edge the strip's lower bound is built from, and the only thing most
    // callers need. It is the same measurement, so the two can never disagree.
    private static float VisibleTop(Component element) => VisibleRect(element, out LayoutRect rect) ? rect.Y : float.NaN;

    // How visible a control is, as the product of every canvas group above it and the strongest colour
    // alpha among its own graphics. An alpha that cannot be read is reported as 1 so a control nobody could
    // measure is protected rather than dropped.
    private static float ControlAlpha(Component element)
    {
        if (element == null) return 1f;
        float alpha = 1f;
        try
        {
            Transform node = element.transform;
            int guard = 0;
            while (node != null && guard++ < 32)
            {
                // A `Transform` is one component of a GameObject and a `CanvasGroup` is another: they are siblings, so the group
                // has to be asked of the object with `GetComponent<CanvasGroup>()`. `transform.TryCast<CanvasGroup>()` (1.19.25
                // and earlier) asks the wrong component and can only answer null - every faded group then read as alpha 1, so a
                // control the page had faded out still counted as a visible wall and the board stepped around nothing (and a
                // title inside a faded group counted as drawn). The `TryCast<RectTransform>()` this build uses elsewhere is a
                // genuine downcast of the very component held here, which is why that one works and this one could not.
                var group = node.GetComponent<CanvasGroup>();
                if (group != null && float.IsFinite(group.alpha)) alpha *= Mathf.Clamp01(group.alpha);
                node = node.parent;
            }
            float drawn = float.NaN;
            foreach (Graphic graphic in element.GetComponentsInChildren<Graphic>(true))
            {
                if (graphic == null || !float.IsFinite(graphic.color.a)) continue;
                drawn = float.IsFinite(drawn) ? Math.Max(drawn, graphic.color.a) : graphic.color.a;
            }
            if (float.IsFinite(drawn)) alpha *= Mathf.Clamp01(drawn);
            return Mathf.Clamp01(alpha);
        }
        catch { return 1f; }
    }

    // The rect of a button component can include layout padding above the control a player actually sees,
    // and a footer group can own several controls. Neither is the wall the board must clear, so the raw
    // numbers behind the measured edge are reported once - a strip that stopped short of the button is
    // otherwise indistinguishable from a page that really had no room.
    private static string DescribeButton(Component element)
    {
        if (element == null) return "missing";
        var text = new System.Text.StringBuilder();
        text.Append(TryScreenRect(element, out LayoutRect own) ? $"container={own.Y:0}..{own.Bottom:0}" : "container=unreadable");
        try
        {
            var renderers = element.GetComponentsInChildren<Renderer>(true);
            if (renderers != null)
            {
                float top = float.NaN, bottom = float.NaN;
                int count = 0;
                foreach (Renderer child in renderers)
                {
                    if (child == null || !child.enabled) continue;
                    if (!TryScreenRect(child, out LayoutRect nested)) continue;
                    count++;
                    top = float.IsFinite(top) ? Math.Min(top, nested.Y) : nested.Y;
                    bottom = float.IsFinite(bottom) ? Math.Max(bottom, nested.Bottom) : nested.Bottom;
                }
                text.Append(count == 0 ? "; visuals=none" : $"; visuals={top:0}..{bottom:0} of {count}");
            }
        }
        catch (Exception error) { text.Append("; visuals failed: " + error.GetType().Name); }
        return text.ToString();
    }
    private static float Top(GameObject host) => host == null ? float.NaN : Top(host.transform);

    // Rows that carry a hairline above them: the metric block's own header and the start of each group,
    // so the board reads in sections instead of as one wall of rows.
    private static bool[] Dividers(ImpactBoardLayout board)
    {
        var dividers = new bool[board.Rows];
        for (int row = 0; row < board.Rows; row++) dividers[row] = board.Kinds[row] == ImpactRowKind.Separator;
        Mark(dividers, (int)ImpactRowKind.PrimaryWeapon);
        Mark(dividers, (int)ImpactRowKind.PrimaryDamage);
        Mark(dividers, (int)ImpactRowKind.SecondaryDamage);
        return dividers;
    }

    private static void Mark(bool[] dividers, int row)
    {
        if (row > 0 && row < dividers.Length) dividers[row] = true;
    }

    // The failure strip's upper edge is the mission-failed title, never the client waiting-status line. 1.19.21: the
    // edge is the title's *laid-out* screen rectangle - `TryScreenRect`, a pure projection asking neither alpha, text
    // nor active state - on the first frame it is laid out. `ResultTitleGate`'s full-opacity-over-a-still-edge answer
    // stays in the path as the drawn-text route's measurement, and `titleSource` plus `messageEdge=` in the [DIAG]
    // lines say which route produced the edge; the route itself no longer decides whether the board may be placed.
    // The waiting-status text is measured separately as a lower wall, and the complete interaction scan
    // still guards the board against every page/tree/source-parent control before each draw.
    // `footIsWall` answers whether the foot this strip really ends at is a wall the page measured - the client's
    // waiting-status block, or a control - and not the page's own edge. `ResultsBounds` already decided which wall
    // produced that foot, so the answer is read from it rather than inferred from the band's height: only a band that
    // stops a design clearance above a measured wall leaves nothing under it that filling it could cover.
    // `band` (1.19.37) is the strip this page really offered, with the source of each of its two edges, so the caller
    // can hand the shape the page had to `ResultFailureBandProofPolicy` instead of re-deriving it from the figures.
    private static bool FailureStrip(Component page, out float top, out float bottom, out float opacity, out bool buttonMeasured, out float buttonTop, out float buttonHeight, out bool anchorSettled, out FooterControls controls, out bool nativeFooterVisible, out ResultFooterSource footerSource, out bool footIsWall, out ResultsBand band, LayoutRect overlapBoard = default, bool inspectBoard = false)
    {
        top = 0f;
        bottom = Screen.height - 8f;
        opacity = 1f;
        buttonMeasured = false;
        buttonTop = float.NaN;
        buttonHeight = float.NaN;
        anchorSettled = false;
        controls = default;
        nativeFooterVisible = false;
        footerSource = ResultFooterSource.None;
        footIsWall = false;
        band = ResultsBand.None;
        // 1.19.47: the upper edge of this page's band is the window's own safe top margin, on a host and on a client
        // alike - `ResultAutoBoardContract.TryUpperAnchor` answers it together with the source, and both branches below
        // assign that source to `bandTopSource`, so the recorded provenance can never drift from the edge it names. The
        // lower edge is still the wall this role really has: the leave/restart button, the waiting-status line, or a
        // pressable control above either.
        ResultBoundSource bandTopSource = ResultBoundSource.ScreenTopMargin, bandBottomSource = ResultBoundSource.ScreenFoot;
        try
        {
            var fail = page != null ? page.TryCast<CM_PageExpeditionFail>() : null;
            if (fail == null || page == null) return false;
            TextMeshPro missionTitle = fail.m_missionFailed_text;
            TextMeshPro guestStatus = fail.m_returnToLobby_text;
            bool missionPresent = missionTitle != null;
            bool missionActive = missionPresent && missionTitle.gameObject.activeInHierarchy;
            bool missionTextPresent = false;
            try { missionTextPresent = missionPresent && !string.IsNullOrWhiteSpace(missionTitle.text); } catch { }
            nativeFooterVisible = fail.m_lobbyButtonVisible;
            // The role, named: the failure page's own anchor policy and its no-footer proof are client-only, and the
            // answer comes from the one place that reads the native flag.
            bool client = ResultPageRolePolicy.IsClient(Role());
            LayoutRect missionRect = default;
            bool missionGeometryMeasured = missionPresent && TryScreenRect(missionTitle, out missionRect);
            bool sequenceFinished = FailureSequence.IsFinished(page.Pointer);
            FailureTitleAnchorKind anchorKind = FailureTitleAnchorPolicy.Select(client, nativeFooterVisible,
                missionPresent, missionActive, missionTextPresent, missionGeometryMeasured, sequenceFinished);
            TextMeshPro heading = missionTitle;
            string titleSource = anchorKind == FailureTitleAnchorKind.MissionFailed ? "mission-failed-text"
                : anchorKind == FailureTitleAnchorKind.MissionTitleLayoutAfterSequence ? "mission-title-layout-after-sequence"
                : anchorKind == FailureTitleAnchorKind.MissionTitleLayout ? "mission-title-layout" : "none";
            bool active = anchorKind == FailureTitleAnchorKind.MissionFailed && missionActive;
            float alpha = active ? HeadingAlpha(heading) : float.NaN;
            float rawTextAlpha = float.NaN;
            try { if (heading != null) rawTextAlpha = heading.alpha; } catch { }
            LayoutRect rect = missionRect;
            // 1.19.21: the board is placed from the title's layout and drawn at full opacity; the title's own fade is
            // no longer copied onto the board - a layout-anchored placement at alpha 0 would otherwise be invisible.
            opacity = 1f;
            bool measured = anchorKind == FailureTitleAnchorKind.MissionFailed
                ? active && missionGeometryMeasured
                : anchorKind != FailureTitleAnchorKind.None && missionGeometryMeasured;
            bool hasRenderer = false, rendererEnabled = false, hasRectTransform = false, hasMenuCamera = false, hasMainCamera = false;
            try
            {
                Renderer titleRenderer = heading != null ? heading.GetComponent<Renderer>() : null;
                hasRenderer = titleRenderer != null;
                rendererEnabled = titleRenderer != null && titleRenderer.enabled;
                hasRectTransform = heading != null && heading.transform.TryCast<RectTransform>() != null;
                hasMenuCamera = CM_PageBase.Camera != null;
                hasMainCamera = Camera.main != null;
            }
            catch { }

            float titleBottom = float.NaN;
            bool drawnRoute = false;
            if (anchorKind == FailureTitleAnchorKind.MissionFailed)
            {
                FailureLayoutTitle.Forget();
                drawnRoute = anchorSettled = Title.Observe(Time.frameCount, active, alpha, measured, measured ? rect.Bottom : float.NaN);
                if (anchorSettled) titleBottom = Title.Edge;
            }
            else if (anchorKind == FailureTitleAnchorKind.MissionTitleLayoutAfterSequence)
            {
                Title.Observe(Time.frameCount, false, float.NaN, false, float.NaN);
                anchorSettled = FailureLayoutTitle.Observe(Time.frameCount, measured, measured ? rect.Bottom : float.NaN);
                if (anchorSettled) titleBottom = FailureLayoutTitle.Edge;
            }
            else
            {
                Title.Observe(Time.frameCount, false, float.NaN, false, float.NaN);
                FailureLayoutTitle.Forget();
            }
            // 1.19.21: the title's laid-out rectangle is an anchor on its own. Whenever the layout is readable the
            // edge is that rectangle - on the first frame, at alpha 0, before the native sequence or the settle frames
            // have done anything. The gates above keep their counters for the drawn-text route; they no longer hold
            // the placement back.
            if (anchorKind != FailureTitleAnchorKind.None && missionGeometryMeasured)
            {
                titleBottom = missionRect.Bottom;
                anchorSettled = true;
            }
            if (!anchorSettled)
            {
                if ((Title.Misses >= ResultRetryCadence.EveryFrameFrames
                    || FailureLayoutTitle.Misses >= ResultRetryCadence.EveryFrameFrames)
                    && HeadingReports.Add("failure-title-unsettled"))
                    Plugin.Logger?.LogInfo($"[DIAG] failure board held back: titleSource={titleSource}; pageActive={(page.gameObject.activeInHierarchy).ToString().ToLowerInvariant()}; missionTitlePresent={missionPresent.ToString().ToLowerInvariant()},missionTitleActive={missionActive.ToString().ToLowerInvariant()},missionTextPresent={missionTextPresent.ToString().ToLowerInvariant()}; waitingStatusPresent={(guestStatus != null).ToString().ToLowerInvariant()},waitingStatusActive={(guestStatus != null && guestStatus.gameObject.activeInHierarchy).ToString().ToLowerInvariant()},nativeLobbyVisible={nativeFooterVisible.ToString().ToLowerInvariant()},failureSequenceFinished={sequenceFinished.ToString().ToLowerInvariant()}; anchorActive={active.ToString().ToLowerInvariant()},alpha={(float.IsFinite(alpha) ? alpha.ToString("0.00") : "unreadable")},rawTextAlpha={(float.IsFinite(rawTextAlpha) ? rawTextAlpha.ToString("0.00") : "unreadable")},geometryMeasured={measured.ToString().ToLowerInvariant()},renderer={hasRenderer.ToString().ToLowerInvariant()}/{rendererEnabled.ToString().ToLowerInvariant()},rectTransform={hasRectTransform.ToString().ToLowerInvariant()},menuCamera={hasMenuCamera.ToString().ToLowerInvariant()},mainCamera={hasMainCamera.ToString().ToLowerInvariant()},opaqueReadings={Title.OpaqueFrames},sameFrameInvalidations={Title.SameFrameInvalidations}; the failure page's title handed over neither drawn text nor measurable layout geometry this frame, so vanilla is retained.");
                return false;
            }
            bool titleKnown = float.IsFinite(titleBottom);
            if (!titleKnown)
            {
                ReportHeldBack("failure-title", "title-edge-unknown", "titleSource=" + titleSource + ",anchorSettled=" + anchorSettled);
                return false;
            }
            if (HeadingReports.Add("failure-native-anchor-ready"))
                Plugin.Logger?.LogInfo($"[DIAG] failure native anchor ready: titleSource={titleSource}; edgeFrom={(drawnRoute ? "drawn-text" : "layout")}; activeTitle={active.ToString().ToLowerInvariant()},alpha={(float.IsFinite(alpha) ? alpha.ToString("0.00") : "unreadable")},geometryMeasured={measured.ToString().ToLowerInvariant()},failureSequenceFinished={sequenceFinished.ToString().ToLowerInvariant()},edge={titleBottom:0},opaqueReadings={Title.OpaqueFrames},sameFrameInvalidations={Title.SameFrameInvalidations}; waiting-status remains a lower protection only.");

            // 1.19.21: the waiting-status block's own layout protects the strip as soon as the object exists - no
            // active-state and no non-empty-text test stands in front of the lower wall any more. `TryScreenRect` asks
            // neither, and a wall that hands nothing over is counted by the scan (an unmeasurable interaction) instead
            // of silently vanishing.
            bool protectWaitingStatus = guestStatus != null;
            controls = ScanFooterControls(page, titleBottom, MessageGap,
                protectWaitingStatus ? guestStatus : null, overlapBoard, inspectBoard);
            if (!controls.ProvenEmpty || nativeFooterVisible) FailureNoFooter.Forget();

            LayoutRect goRect = default, restartRect = default;
            bool restartRead = ButtonActive(fail.m_btnRestartCheckpoint) && TryScreenRect(fail.m_btnRestartCheckpoint, out restartRect);
            bool goRead = ButtonActive(fail.m_btnGoToLobby) && TryScreenRect(fail.m_btnGoToLobby, out goRect);
            bool restartInStrip = restartRead && restartRect.Y >= controls.BoardTop - 0.5f && restartRect.Bottom > controls.BoardTop;
            bool goInStrip = goRead && goRect.Y >= controls.BoardTop - 0.5f && goRect.Bottom > controls.BoardTop;
            float restartTop = restartInStrip ? restartRect.Y : float.NaN;
            float goTop = goInStrip ? goRect.Y : float.NaN;
            float namedTop = ResultsRegion.HighestTop(restartTop, goTop);
            buttonTop = ResultsRegion.HighestTop(namedTop, controls.FooterTop);
            buttonMeasured = float.IsFinite(buttonTop);
            buttonHeight = restartInStrip && Math.Abs(buttonTop - restartTop) <= 0.5f ? restartRect.Height
                : goInStrip && Math.Abs(buttonTop - goTop) <= 0.5f ? goRect.Height : float.NaN;
            footerSource = !buttonMeasured ? ResultFooterSource.None
                : float.IsFinite(namedTop) && (!float.IsFinite(controls.FooterTop) || namedTop <= controls.FooterTop + 0.5f)
                    ? ResultFooterSource.NamedControl : ResultFooterSource.InteractiveBand;

            // 等待状态文字块：测量一次，既写进 `waitingText=` 日志字段，也是客机失败页的下界墙。
            // 1.19.21: the waiting block's own layout is read as soon as the object exists - active state and text
            // content are not asked of it, and `TryScreenRect` asks neither. A block the page has not laid out yet is
            // simply not measured this frame, and the strip is not invented without it (1.19.45).
            float waitingTextTop = float.NaN;
            try
            {
                if (guestStatus != null && TryScreenRect(guestStatus, out LayoutRect waitingRect)) waitingTextTop = waitingRect.Y;
            }
            catch { }

            if (GuestClient())
            {
                // 客机：本页没有「返回大厅」按钮，只有一行等待厅主的状态文字——而它自己会被扫描当成一个
                // 受保护的下边界控件，所以旧的 buttonMeasured 在客机上几乎恒为真，绝不能拿它当分支条件。
                // 上界 = 窗口自己的上沿安全留边 + 玩家配置的纵向偏移（1.19.47：不再是任务失败标题的实测底边），
                // 之后再由 `ResultsGuestPageGeometry` 按 1080p 等比补一个 gap。
                //
                // 1.19.45（用户纠正，覆盖 1.19.38 的相反口径）：下界就是那行不可点击提示文字自己的上沿，和主机
                // 页拿按钮上沿做下界是同一个道理——「他的下界不过是从主机的可点击按钮变成了客机的不可点击提示
                // 文字」。真正可按的控件（重开检查点 / 回大厅）照旧是墙，两者取更靠上的那一堵。页面固定几何只做
                // 封顶，绝不冒充墙：run 3 里 band (488..1429) 就是这么编出来的，900 高的板压住 1177 的控件，
                // `TryHold` 不能抬一块已经在带顶的板，只能整页 0.55。
                //
                // 1.19.39：墙先稳定，再落板。记录下来的客机失败页第一帧还没有任何可按控件，条带整段是屏幕下界
                // （381..1429，1049px），一块 1160x990 的板被画上去；下一帧扫描量到控件 1176，条带收紧到
                // 381..1179（798px），站着的板真的压在控件上，于是整块板被撤掉——玩家看到的就是「先画一块大的，
                // 再消失」。这一帧的判定现在由 `ResultClientFailureStrip` 完整给出（固定几何 → 墙稳定门 → 条带），
                // 落板用的墙永远是本页量到过的最高那一堵：第一帧就把最终几何定下来，之后既不重排也不撤板。
                // 落板的另一个前提由 `ResultClientFailureStrip` 自己带：扫描必须真的看见这一页
                // （`controls.Complete` + `ParentCoverageComplete`）——客机在生产里没有外层的完整扫描闸门，
                // 而 inactive 控件被扫描记为 hidden，不会让扫描变成「不完整」。
                // 已经站在页面上的板（`inspectBoard`）不过稳定门：一堵还没读到两次的墙不能把板撤掉。
                float configuredOffset = Plugin.ResultsOffsetY.Value;
                if (!float.IsFinite(configuredOffset) || configuredOffset < 0f) configuredOffset = 0f;
                // 1.19.60：客机的上界与主机页同一条规则——本页自己的屏幕上沿安全留边，与「活着的任务失败标题」实测底边
                // 取更靠下的那一个，再由本页**真的量到**的、比它更深的交互控件收紧。1.19.47 把客机页的标题从上界里拿掉，
                // 代价是有证据的：同一张 1440p 客机页上严格路径的带顶退到窗口留边（10.67→+gap=32），fail-open 路径却仍从
                // 标题底边（467）走 `ResultsBounds`——同一页两个说法，相差约 456px，而严格路径的板是**底对齐墙、向上生长**
                // 的，于是板顶落在 380~425，压住玩家正在读的标题下半段 40~90px。
                //
                // 1.19.60（独立评审 F2）：控件那一路必须交**未经间距的原始边**。扫描是带着 `MessageGap` 跑的
                // （扫描就是上面那次、带着 `MessageGap` 跑的那一次），所以 `controls.BoardTop` 已经是
                // `anchorBottom + gap`，或者在真有控件抬过它时是 `control.Bottom + gap`——把整条 `BoardTop` 当成
                // 「量到的控件」交给 `TryFailureAnchor`，会让 `ResultClientFailureStrip` 在它之上再加一次
                // `MessageGap * scale`，1440p 上于是变成 467+16+21.33 = 504.33，比兜底的 488.33 多了一个间距；而且没有
                // 任何控件的时候也会被标成 `MeasuredControl`，那是假造。减去同一个 `MessageGap` 得到的就是这条基准本身
                // （没有控件时正是 `titleBottom`，有控件时是那个控件的下沿），间距因此只由条带自己加**一次**。
                float rawControlTop = float.IsFinite(controls.BoardTop) ? controls.BoardTop - MessageGap : float.NaN;
                if (!ResultAutoBoardContract.TryFailureAnchor(Screen.height, titleBottom, rawControlTop,
                        out float guestAnchor, out bandTopSource))
                    guestAnchor = Math.Max(ResultsBounds.ScreenMargin, ResultAutoBoardContract.TopMargin(Screen.height));
                guestAnchor += configuredOffset;
                // 墙的两路读数：真正可按的控件（扫描的 `PressableTop` 已排除受保护文字，命名按钮按自己的布局矩形
                // 加进来）只用来收紧；**权威**只有「同一个原生返回位置」的几何——客机上 `m_btnGoToLobby` 自己的
                // 已布局矩形（主机页量的就是这同一个对象）与 `m_returnToLobby_text` 真正渲染出来的字形，
                // 两者取更靠上的那一堵（`ResultClientSlotPolicy`）。
                float pressableTop = ResultsRegion.HighestTop(namedTop, controls.PressableTop);
                ResultClientSlotEvidence slot = ClientSlot(page);
                float clientSlotTop = slot.Top;
                // 1.19.61（评审 #3）：没有槽位权威时这一页没有可以落板的边界。布局框（1.19.60 的 1338）、别的可按控件、
                // 页面自己从屏幕推出来的下界都不授权首显，F8 也一样；这一页保留原版并按既有节流重测。
                bool wallPending = ResultClientPageActionPolicy.Pending(slot.HostSlotTop, slot.HintGlyphTop);
                if (wallPending && ResultFallbackPolicy.ShouldFallback(_strictMisses, Forced)
                    && HeadingReports.Add("failure-client-wall-pending-to-fail-open"))
                    Plugin.Logger?.LogWarning($"[DIAG] failure board: this client page has shown no slot evidence until the fail-open budget "
                        + $"({ResultFallbackPolicy.MissBudget} refusals, misses={_strictMisses}, forcedByF8={Forced}); hidden={controls.Hidden},"
                        + $"hiddenPageActions={controls.HiddenPageActions},hostSlot={(float.IsFinite(slot.HostSlotTop) ? slot.HostSlotTop.ToString("0") : "unmeasurable")},"
                        + $"hintGlyph={(float.IsFinite(slot.HintGlyphTop) ? slot.HintGlyphTop.ToString("0") : "unmeasurable")},"
                        + $"pressable={(float.IsFinite(pressableTop) ? pressableTop.ToString("0") : "unmeasurable")},"
                        + $"scanComplete={controls.Complete.ToString().ToLowerInvariant()},parentCoverage={controls.ParentCoverageComplete.ToString().ToLowerInvariant()},"
                        + $"unmeasurable={controls.Unmeasurable}; neither the native return slot on this page nor the waiting line's rendered glyphs could be read, so this page has no lower bound at all - "
                        + $"the fail-open path's own gate decides from here (`ResultFailureWallPolicy.WallMeasured`): a client failure page without a readable slot is kept vanilla and measured again, the player's F8 authorises a board only once this page has a bound, and no frame count or timeout may do it.");
                var clientEvidence = new ResultClientFailureEvidence(guestAnchor, Screen.width, Screen.height,
                    MessageGap, MinimumGuestStripHeight, clientSlotTop, pressableTop,
                    controls.Complete, controls.ParentCoverageComplete, controls.Unmeasurable, slot.Source);
                ResultClientBandReading clientBand;
                ResultsSafeArea guestArea;
                ResultClientStripState clientState = inspectBoard
                    ? ResultClientFailureStrip.Measure(Time.frameCount, ClientWall, clientEvidence, out clientBand, out guestArea)
                    : ResultClientFailureStrip.Decide(Time.frameCount, ClientWall, clientEvidence, out clientBand, out guestArea);
                if (clientState == ResultClientStripState.WallNotSettled)
                {
                    // 不是超时，也不是有界放行：这一帧的墙问题没有被回答（读数变了 / 一堵墙都没量到 / 有 active
                    // 控件量不到矩形），所以这一帧不是可以落板的那一帧。放行的唯一条件是「同一堵墙、两帧、都可
                    // 回答」；等不到就交给既有的 60 帧拒收预算与 fail-open 板，那一条从不声称这一页被证明过。
                    // 这一行走 `HeadingReports`，每页一次：一次诊断，不刷屏。
                    if (HeadingReports.Add("failure-client-wall-not-settled"))
                        Plugin.Logger?.LogInfo($"[DIAG] failure board held back: this client page's lower wall has not read the same value on two complete scans yet (wallSoFar={(float.IsFinite(ClientWall.Wall) ? ClientWall.Wall.ToString("0") : "unmeasurable")},wallSource={ResultClientSlotPolicy.Name(ClientWall.WallSource)},thisFrame={(float.IsFinite(clientEvidence.WallTop) ? clientEvidence.WallTop.ToString("0") : "unmeasurable")},hostSlot={(float.IsFinite(slot.HostSlotTop) ? slot.HostSlotTop.ToString("0") : "unmeasurable")},hintGlyph={(float.IsFinite(slot.HintGlyphTop) ? slot.HintGlyphTop.ToString("0") : "unmeasurable")},slot={(float.IsFinite(clientSlotTop) ? clientSlotTop.ToString("0") : "unmeasurable")},slotSource={ResultClientSlotPolicy.Name(slot.Source)},wall={(float.IsFinite(clientEvidence.WallTop) ? clientEvidence.WallTop.ToString("0") : "unmeasurable")},{ResultFooterProvenance.LobbyWall(ResultFooterSource.ClientNoFooterSafeArea, clientEvidence.WallTop, float.IsFinite(clientEvidence.WallTop))},pressable={(float.IsFinite(pressableTop) ? pressableTop.ToString("0") : "unmeasurable")},samples={ClientWall.Samples} seen,state={ResultClientFailureStrip.Name(clientState)},scanComplete={controls.Complete.ToString().ToLowerInvariant()},parentCoverage={controls.ParentCoverageComplete.ToString().ToLowerInvariant()},hidden={controls.Hidden},hiddenPageActions={controls.HiddenPageActions},unmeasurable={controls.Unmeasurable},wallPending={wallPending.ToString().ToLowerInvariant()}); no frame count authorises this placement, so the board is not fitted against a wall the page is still revealing and is never withdrawn a frame later. wallPending=true means neither this page's own return slot nor the waiting line's rendered glyphs were measurable, so this is not a page without a wall - hidden/hiddenPageActions are printed for the record only (the go-to-lobby control is permanently inactive on a client, and that is not read as 'absent'). If it never settles the existing refusal budget answers with the fail-open board, which steps around every measured control instead of claiming the page was proven.");
                    return false;
                }
                if (clientState == ResultClientStripState.GeometryUnusable)
                {
                    if (HeadingReports.Add("failure-guest-geometry-unusable"))
                        Plugin.Logger?.LogInfo($"[DIAG] failure board held back: the client page's fixed geometry is unusable this frame (anchorBottom={(float.IsFinite(titleBottom) ? titleBottom.ToString("0") : "unmeasurable")}, screen={Screen.width}x{Screen.height}); vanilla is retained and the page is measured again.");
                    return false;
                }
                if (clientState == ResultClientStripState.BandUnusable)
                {
                    // 量到了墙，但那堵墙上方留不出客机最小板高：这是「保留原版 + 重测」，不是把板压在墙上面。
                    if (HeadingReports.Add("failure-guest-band-unusable"))
                        Plugin.Logger?.LogInfo($"[DIAG] failure board held back: the client page's band cannot hold the minimum board above the wall this frame (fixedPage=({guestArea.Top:0}..{guestArea.Bottom:0}),hostSlot={(float.IsFinite(slot.HostSlotTop) ? slot.HostSlotTop.ToString("0") : "unmeasurable")},hintGlyph={(float.IsFinite(slot.HintGlyphTop) ? slot.HintGlyphTop.ToString("0") : "unmeasurable")},slot={(float.IsFinite(clientSlotTop) ? clientSlotTop.ToString("0") : "unmeasurable")},slotSource={ResultClientSlotPolicy.Name(slot.Source)},wall={(float.IsFinite(ClientWall.Wall) ? ClientWall.Wall.ToString("0") : "unmeasurable")},wallSource={ResultClientSlotPolicy.Name(ClientWall.WallSource)},{ResultFooterProvenance.LobbyWall(ResultFooterSource.ClientNoFooterSafeArea, clientSlotTop, float.IsFinite(clientSlotTop))},pressableTop={(float.IsFinite(pressableTop) ? pressableTop.ToString("0") : "unmeasurable")},minimum={MinimumGuestStripHeight:0}px) on a {Screen.height} window; vanilla is retained and the page is measured again.");
                    return false;
                }
                // 扫描看不见这一页：它不能断言「这一页没有墙」，也不是「页面把控件关掉了」。客机失败页在生产里
                // 没有外层的完整扫描闸门（那一条只约束主机），所以这里必须自己说清楚，并沿用主机同一条日志。
                if (clientState == ResultClientStripState.ScanIncomplete)
                {
                    ReportIncompleteScan("failure-client", controls, guestArea.Top, guestArea.Height,
                        ResultFooterSource.ClientNoFooterSafeArea);
                    return false;
                }
                top = clientBand.Top;
                bottom = clientBand.Bottom;
                footerSource = ResultFooterSource.ClientNoFooterSafeArea;
                // 下界来自哪一堵墙，日志就说哪一堵：等待文字是 `footer-text`，可按控件是 `footer-control`。
                bandBottomSource = clientBand.PressableTightened ? ResultBoundSource.FooterControl
                    : clientBand.WaitingTextTightened ? ResultBoundSource.FooterText
                    : ResultBoundSource.ScreenFoot;
                // 下界来自一堵量到的墙（等待文字或可按控件）时，这条条带就是它自己的承诺（与主机同一条 ceiling
                // 规则，`ResultsFontGrowth.FootIsTheWall` 的回答）；一堵墙都没有时它根本不会走到这里。
                footIsWall = clientBand.WallMeasured;
                // 客机的「页脚」是这一页真正画出来的墙：槽位的布局上沿、槽位里真的画出来的字形、或更靠上的可按控件。
                // 1.19.61：这里报的是**条带自己用的那一堵**（`clientBand.WallTop`）——1.19.60 报的是门保留的墙
                // （`ClientWall.Wall`），于是条带可能按一个数落位、`BoardStillSafe` 的页脚校验却按另一个数判它，
                // 同一张页面两个边界（2026-11-25 快照 L4881 条带下沿 1371 / L4883 footerTop=1338）。一个边界
                // 同时喂测量与绘制，这一条才是「板站得住」的前提。
                float clientWall = clientBand.WallTop;
                buttonTop = clientWall;
                buttonMeasured = float.IsFinite(clientWall);
                buttonHeight = restartInStrip && Math.Abs(namedTop - restartTop) <= 0.5f ? restartRect.Height
                    : goInStrip && Math.Abs(namedTop - goTop) <= 0.5f ? goRect.Height : float.NaN;
                if (HeadingReports.Add("failure-client-fixed-geometry"))
                    Plugin.Logger?.LogInfo($"[DIAG] failure footerSource=client-fixed-page-geometry: native lobbyVisible={nativeFooterVisible.ToString().ToLowerInvariant()}, anchor=settled-mission-title, nativeFooterMeasured={buttonMeasured.ToString().ToLowerInvariant()}, hostSlot={(float.IsFinite(slot.HostSlotTop) ? slot.HostSlotTop.ToString("0") : "unmeasurable")},hintGlyph={(float.IsFinite(slot.HintGlyphTop) ? slot.HintGlyphTop.ToString("0") : "unmeasurable")},slot={(float.IsFinite(clientSlotTop) ? clientSlotTop.ToString("0") : "unmeasurable")},slotSource={ResultClientSlotPolicy.Name(slot.Source)},wall={(float.IsFinite(clientWall) ? clientWall.ToString("0") : "unmeasurable")},slotCovered={clientBand.WaitingTextCovered.ToString().ToLowerInvariant()}, slotTightened={clientBand.WaitingTextTightened.ToString().ToLowerInvariant()}, pressableTightened={clientBand.PressableTightened.ToString().ToLowerInvariant()}, wallSettled={ClientWall.Settled.ToString().ToLowerInvariant()},wallSource={ResultClientSlotPolicy.Name(ClientWall.WallSource)},wallSamples={ClientWall.Samples}, hidden={controls.Hidden},hiddenPageActions={controls.HiddenPageActions},wallPending={wallPending.ToString().ToLowerInvariant()}, strip=({top:0}..{bottom:0}) of a {Screen.height} window; this page's lower bound is the same native return slot the host page measures (its own `m_btnGoToLobby` layout geometry, unclickable here but present) or the waiting line's actually rendered glyphs when they reach higher - never the layout box, never another pressable control and never the page's own screen-derived foot, which only caps the band. An empty slot reading keeps the vanilla page and is measured again.");
            }
            else
            {
                // 主机：上界同时受本页自己的屏幕上沿留边与「活着的任务失败标题」约束，下界是本页真正画出来的那堵墙
                // （返回大厅 / 重开检查点按钮）。
                //
                // 1.19.55（用户实测，r5 1920x1080）：1.19.47 把标题从这条带的上界里完全拿掉，理由是「标题是说明文字、
                // 会把表压到下半页」。代价是那一次失败页的带顶退回 `screen-top-margin`=24，板子先落在 (795,315)、
                // 再被二次量测抬到 (825,110) —— 两次都压在标题上（标题下沿 350，日志 `failure native anchor ready
                // ... edge=350`），玩家看到的正是「板子压着任务标题」。标题是这一页上唯一读得出的原生文字，它的底边
                // 是**保护**而不是装饰：带顶永远不低于「标题底边 + MessageGap」，除非标题本身量不到（那时仍是本页
                // 自己的屏幕留边，不发明一个数）。带被压短时由 `ResultsBandFit` 收字号、由滚轮到达余下的行；真的
                // 装不下一块可读的表时这条路径照旧保留原版页面，不把板子挪到标题上面去。
                ResultAutoBoardContract.TryUpperAnchor(false, Screen.height, float.NaN, out float screenAnchor, out bandTopSource);
                // 1.19.56 (review 7): one title gap, applied once. `ResultsBounds.TryResolve` below adds this page's own
                // clearance to whatever upper anchor it is handed, so adding `MessageGap` here as well put the title at
                // `350 + 16 + clearance`; the F8/fail-open path resolves its band through the same `ResultsBounds` from
                // the same `top`, so it repeated the same double gap. The anchor is now the deeper of the page's own
                // screen margin and the live mission-failed title's bottom edge, and the gap comes from the one rule that
                // owns it. The measured-control override keeps working on the same anchor, and the client branch above is
                // untouched - a client's waiting-status line is still its own stable lower wall.
                float titleAnchor = float.IsFinite(titleBottom) && titleBottom > screenAnchor ? titleBottom : screenAnchor;
                top = Math.Max(0f, titleAnchor) + OffsetY();
                if (float.IsFinite(titleBottom) && titleBottom >= screenAnchor) bandTopSource = ResultBoundSource.TitleAnchor;
                if (controls.BoardTop > top + 0.5f)
                {
                    top = controls.BoardTop;
                    bandTopSource = ResultBoundSource.MeasuredControl;
                }
                if (buttonMeasured)
                {
                    bottom = Math.Min(buttonTop - MessageGap, Screen.height - 8f);
                    bandBottomSource = ResultBoundSource.FooterControl;
                }
                else bottom = Screen.height - 8f;
            }
            // ---- the agreed band, on the failure page too ----------------------------------------------------
            //
            // Upper: the window's own safe top margin on this page (1.19.47 - the mission-failed title is a caption and
            // no longer bounds the table). Lower: the wall the player's role really has.
            //
            // 1.19.45（用户纠正）：客机失败页的下界由上面那个客机分支给出，来自「等待文字自己的上沿」或更靠上的可按
            // 控件，页面固定几何只做封顶——和主机页「按钮上沿减 clearance」是同一条规则的两张面孔。这里因此只把这一页
            // 的 band 报一次（`ReportBand` 的 `waitingText=` 字段就是那堵墙的读数）。主机页的下界与它完全相同：仍然取
            // 「按钮 / 量到的控件 / 屏幕脚」里最高的那堵墙，只有上界换成了本页自己的屏幕上沿。
            if (GuestClient())
            {
                // 客机：条带的两个边界已经由上面那个客机分支决定，这里只把这一页的 band 报一次；`waitingText=`
                // 报的是这条带用的那条槽位边界（`buttonTop` 在这里就是 `clientBand.WallTop`），不是那个比字形低
                // 155px 的布局框。
                ReportBand("failure", new ResultsBand(top, bottom, bandTopSource, bandBottomSource, false),
                    float.NaN, buttonTop, _boardFloor);
            }
            // 1.19.47（最新用户合同）：这条带的上界已经是本页自己的屏幕上沿安全留边，所以这里交给 `ResultsBounds`
            // 的就只剩它自己的 clearance 与那一堵真实下界。标题的布局仍然是扫描的切分依据（`ScanFooterControls` 的
            // anchorBottom），但它不再参与这条带的任何一个边界。
            else if (ResultsBounds.TryResolve(Screen.height, top, bandTopSource,
                    buttonMeasured ? buttonTop : float.NaN, waitingTextTop, controls.FooterTop, out ResultsBand pageBand))
            {
                ReportBand("failure", pageBand, float.NaN, waitingTextTop, _boardFloor);
                if (pageBand.Fits && footerSource != ResultFooterSource.BelowActions)
                {
                    if (pageBand.Top > top)
                    {
                        top = pageBand.Top;
                        bandTopSource = pageBand.TopSource;
                    }
                    if (pageBand.Bottom < bottom)
                    {
                        // The foot this strip really ends at came from the wall the band names, not from the page's own
                        // edge - so nothing under the band's foot is left unread, and the board may be fitted to the whole
                        // of it. A band that still ends at the page edge answered `ScreenFoot` and keeps the comfort
                        // ceiling: the board then stops short of an edge nobody measured.
                        bottom = pageBand.Bottom;
                        bandBottomSource = pageBand.BottomSource;
                        footIsWall = pageBand.BottomSource != ResultBoundSource.ScreenFoot;
                    }
                }
            }
            if (!float.IsFinite(top) || !float.IsFinite(bottom) || !(bottom > top))
            {
                ReportHeldBack("failure-strip-measured", "strip-arithmetic-unnumbered",
                    "stripTop=" + (float.IsFinite(top) ? top.ToString("0") : "unmeasurable")
                    + ",stripBottom=" + (float.IsFinite(bottom) ? bottom.ToString("0") : "unmeasurable")
                    + ",anchorBottom=" + titleBottom.ToString("0") + ",footerSource=" + footerSource);
                return false;
            }

            LayoutRect candidateBoard = new LayoutRect(0f, top, Screen.width, Math.Max(0f, bottom - top));
            bool overlapsLobby = controls.OverlapsBoard;
            float stripTopFromWindow = candidateBoard.Y;
            float blankBelow = Math.Max(0f, (float.IsFinite(buttonTop) ? buttonTop : candidateBoard.Bottom) - candidateBoard.Bottom);
            if (HeadingReports.Add("failure-strip"))
                Plugin.Logger?.LogInfo($"[DIAG] failure strip: titleSource={titleSource},messageBottom={titleBottom:0},edgeFrom={(drawnRoute ? "drawn-text" : "layout")},titleAlpha={(float.IsFinite(alpha) ? alpha.ToString("0.00") : "unreadable")},opaqueReadings={Title.OpaqueFrames},sequenceFinished={sequenceFinished.ToString().ToLowerInvariant()},boardTop=({candidateBoard.Y:0}..{candidateBoard.Bottom:0}),bandTopSource={bandTopSource},stripTopFromWindow={stripTopFromWindow:0}px,blankBelow={blankBelow:0}px,footerTop={(float.IsFinite(buttonTop) ? buttonTop.ToString("0") : "unmeasurable")},waitingStatusProtected={protectWaitingStatus.ToString().ToLowerInvariant()},scanComplete={controls.Complete.ToString().ToLowerInvariant()},parentCoverage={controls.ParentCoverageComplete.ToString().ToLowerInvariant()},unmeasurable={controls.Unmeasurable},boardOverlapsControl={overlapsLobby.ToString().ToLowerInvariant()},screen={Screen.width}x{Screen.height}.");
            // The strip the caller is handed, with the fact that produced each of its edges: this is the shape
            // `ResultFailureBandProofPolicy` is asked about, and it is read here because here is the only place that
            // knows which of the page's own measurements the two figures came from.
            band = new ResultsBand(top, bottom, bandTopSource, bandBottomSource, false);
            ReportDowngradedControls("failure", controls, top, Math.Max(0f, bottom - top), footerSource);
            return true;
        }
        catch (Exception error)
        {
            if (HeadingReports.Add("title-read-error")) Plugin.Logger?.LogWarning("[DIAG] failure strip measurement failed; keeping the vanilla results page and waiting to retry with stable anchors: " + error.Message);
            buttonMeasured = false;
            buttonTop = float.NaN;
            buttonHeight = float.NaN;
            return false;
        }
    }

    // Read the selected native TextMeshPro anchor as the player sees it: its own fade multiplied by ancestor
    // CanvasGroups. Unknown alpha is not evidence of full opacity; return NaN so ResultTitleGate keeps vanilla
    // until both a real render alpha and measurable screen geometry are available.
    private static float HeadingAlpha(TextMeshPro heading)
    {
        if (heading == null) return float.NaN;
        float text, group = 1f;
        try
        {
            text = heading.alpha;
            if (!float.IsFinite(text)) return float.NaN;
            text = Mathf.Clamp01(text);
        }
        catch { return float.NaN; }
        try
        {
            Transform node = heading.transform;
            int guard = 0;
            while (node != null && guard++ < 32)
            {
                // The same sibling-component rule as `ControlAlpha`: the group is asked of the object, never cast from the
                // transform. With the cast this walk read no group at all, so a title the page had faded out still answered
                // "drawn at full alpha" to the failure title gate - the one alpha the board's upper anchor waits for.
                var canvasGroup = node.GetComponent<CanvasGroup>();
                if (canvasGroup != null)
                {
                    float groupAlpha = canvasGroup.alpha;
                    if (!float.IsFinite(groupAlpha)) return float.NaN;
                    group *= Mathf.Clamp01(groupAlpha);
                }
                node = node.parent;
            }
        }
        catch { return float.NaN; }
        return Mathf.Clamp01(text * group);
    }


    // Revalidate the failure board's current lower wall for the held-board lift. The trusted title edge is
    // whichever measured mission-title path authorized placement; the waiting-status is never used as an anchor.
    private static float HostFailureButtonTop(Component page)
    {
        try
        {
            var fail = page != null ? page.TryCast<CM_PageExpeditionFail>() : null;
            return fail != null
                ? ResultsRegion.HighestTop(VisibleTop(fail.m_btnRestartCheckpoint), VisibleTop(fail.m_btnGoToLobby))
                : float.NaN;
        }
        catch { return float.NaN; }
    }

    private static bool LobbyButtonTop(Component page, out float top)
    {
        top = float.NaN;
        try
        {
            // A layout-anchored placement may leave the drawn-text gates still counting: the held strip's own top is
            // the band the board stands under either way, and the anchor the scan is keyed to is that band's foot.
            float anchor = float.IsFinite(_holdStrip.Top)
                ? _holdStrip.Top - ResultsBounds.Clearance(Screen.height)
                : float.IsFinite(Title.Edge) ? Title.Edge : FailureLayoutTitle.Edge;
            if (!float.IsFinite(anchor)) return false;
            var fail = page != null ? page.TryCast<CM_PageExpeditionFail>() : null;
            float namedTop = fail != null
                ? ResultsRegion.HighestTop(VisibleTop(fail.m_btnRestartCheckpoint), VisibleTop(fail.m_btnGoToLobby))
                : float.NaN;
            FooterControls controls = ScanFooterControls(page, anchor, MessageGap, null);
            if (float.IsFinite(namedTop) && namedTop < controls.BoardTop - 0.5f) namedTop = float.NaN;
            float protectedTop = ResultsRegion.HighestTop(namedTop, controls.FooterTop);
            top = ResultsRegion.LobbyEdge(float.IsFinite(protectedTop), protectedTop, float.NaN, Screen.height);
            return controls.Complete && float.IsFinite(top);
        }
        catch { return false; }
    }


    // The candidate wall is based on a trusted page title/completion edge, while every discovered action remains
    // in the per-draw collision set. In addition to page registrations/descendants, scan only the local parents of
    // the page's own result actions: sibling clones (including unregistered CM_Item controls)
    // live there without requiring a whole-scene scan.
    private static FooterControls ScanFooterControls(Component page, float anchorBottom, float gap,
        Component protectedPageText = null, LayoutRect board = default, bool inspectBoard = false,
        LayoutColumn column = default)
    {
        if (page == null) return new FooterControls(float.NaN, float.NaN, 0, 0, false, false, false, 0, 0, "page-missing");
        var actions = new List<ResultActionBounds>();
        // 1.19.51: the owner of each entry in `actions`, same order, so the line can name the interaction that ended
        // the band (`ResultFixedBlocker.Describe`). Only the strip's lower wall is looked up here.
        var owners = new List<Component>();
        var seen = new HashSet<IntPtr>();
        var parentSeen = new HashSet<IntPtr>();
        // Every control the scan sees is added to this tally, and the three answers it can give are kept apart on
        // purpose: one for the control the page switched on, one for the control it switched off, and one for the
        // control that is on but whose rectangle this frame could not be read.
        var tally = new ResultInteractionTally(0, 0, 0, 0);
        string reason = string.Empty;
        bool complete = true, overlaps = false, client = false, parentCoverageComplete = true;
        // 固定模式的那一块：哪些控件真的和板相交。自适应链不看这两个值（它只用 `overlaps`），所以这里多记一条
        // 身份不会改变它的任何决定；记的是**第一个**相交控件（日志用它说清"压到了谁"）和相交的个数。
        ResultFixedBlocker blocker = ResultFixedBlocker.None;
        int blockers = 0;
        // 1.19.57: which result page this scan is looking at, read from the live page the caller handed in - the
        // classifier must not depend on the `_page`/`_succeeded` cache, which `ResetPlacement` clears on the same frame.
        bool succeeded = page.TryCast<CM_PageExpeditionSuccess>() != null;
        // The client flag decides which result action the page is expected to have hidden, so an unreadable one is
        // a coverage edge of the scan itself and keeps the vanilla page up rather than being guessed at.
        try { client = ResultPageRolePolicy.IsClient(ResultPageRolePolicy.Of(SNet.IsMaster)); } catch { complete = false; reason = "client-state-unreadable"; }

        // Two different questions, deliberately kept apart:
        //   * can this control take the pointer right now - only an *active* control can, so a control the game
        //     switched off is counted as hidden and then ignored (it is not a lower wall and it is not evidence
        //     that the page has something to protect). A page that hides half its own cell menu, a hidden panel
        //     prefab or another mod's clone parked with `SetActive(false)` therefore no longer condemns the
        //     board to never appear. When one of them comes back on, the per-repaint rescan in `BoardStillSafe`
        //     reads it as a real interaction and restores the vanilla page on that frame if it reaches the board.
        //   * could this scan see everything it had to see - that is the only thing `complete` answers, and the
        //     cases that keep it false are the ones about the page's *structure*: an unreadable item registry, a
        //     parent node that cannot be reached, a type conversion that fails, a client flag nobody could read.
        //     None of those is repaired by waiting, so a scan that hits one must leave the board alone.
        //
        // TextMeshPro footer bounds are measured separately below. A label and its parent
        // button have different native identities; protecting text must never exempt that button.

        void AddControl(Component control)
        {
            if (control == null) return;
            IntPtr pointer;
            try { pointer = control.Pointer; }
            catch { pointer = IntPtr.Zero; complete = false; if (reason.Length == 0) reason = "pointer-unreadable"; }
            if (pointer != IntPtr.Zero && !seen.Add(pointer)) return;
            bool active;
            try { active = control.gameObject.activeInHierarchy; }
            catch
            {
                // The scan cannot tell whether this control can take the pointer at all, which is again a fact
                // about the page rather than about one rectangle, so the count grows and the page is refused.
                tally = tally.ActiveControl();
                complete = false; if (reason.Length == 0) reason = "active-state-unreadable"; return;
            }
            if (!active)
            {
                tally = tally.HiddenControl(client && IsKnownClientHiddenLobbyAction(page, control));
                return;
            }
            // 1.19.56 (A): the same classifier the hide asks decides whether this entry can be a wall at all. A
            // decoration inside one of this page's known presentational blocks is drawn by the page, never pressed by
            // the player: it must not refuse the board (the recorded "the board never appears" / false-wall point), and
            // because the hide asks the same question it can still be put away. Anything this build cannot describe is a
            // wall and is counted exactly as before.
            try { if (ResultVanillaClassify.Hideable(KindOf(control, page, succeeded))) return; } catch { }
            tally = tally.ActiveControl();
            if (!TryScreenRect(control, out LayoutRect rect))
            {
                // Active but with a rectangle nobody could read: this is one frame of one control, not a hole in
                // the scan. Another mod clones the lobby button and its collider exists only once the clone has
                // been laid out, so for a frame or two there is an active, pressable control whose rectangle is
                // the zero box; treating that as an unfinished scan kept the board off a page that was in every
                // other respect safe, and re-measuring could never clear it because the same clone was there on
                // the next repaint too. The control keeps its place in `Count`, so the page cannot claim to have
                // no footer, and `BoardStillSafe` re-runs this scan every repaint: once the rectangle reads, the
                // control joins the collision set and restores the vanilla page if it reaches the board.
                tally = tally.UnmeasurableControl();
                return;
            }
            actions.Add(new ResultActionBounds(rect));
            owners.Add(control);
            if (inspectBoard && ResultInteractionStripPolicy.OverlapsBoard(rect, board, ResultBoardHold.ButtonClearance))
            {
                overlaps = true;
                blockers++;
                if (!blocker.Any) blocker = Blocker(control, rect, pressable: true);
            }
        }

        void ScanSiblingParent(Component source, bool required)
        {
            if (source == null)
            {
                // The page's own result action - the button whose parent this scan exists to cover - is not there
                // at all. Nothing below can visit the sibling clones that would live beside it, so the scan cannot
                // claim the page is covered and must say so rather than pass an unvisited subtree off as empty.
                // The failure page's checkpoint-restart button is optional, so its absence alone changes nothing.
                if (!required) return;
                parentCoverageComplete = false;
                complete = false;
                if (reason.Length == 0) reason = "action-source-missing";
                return;
            }
            try
            {
                Transform parent = source.transform.parent;
                if (parent == null) { parentCoverageComplete = false; complete = false; if (reason.Length == 0) reason = "action-parent-missing"; return; }
                IntPtr pointer = parent.Pointer;
                if (pointer == IntPtr.Zero) { parentCoverageComplete = false; complete = false; if (reason.Length == 0) reason = "action-parent-unreadable"; return; }
                if (!parentSeen.Add(pointer)) return;
                foreach (CM_Item item in parent.GetComponentsInChildren<CM_Item>(true)) AddControl(item);
                foreach (Button button in parent.GetComponentsInChildren<Button>(true))
                    if (button != null) AddControl(button.TryCast<Component>());
            }
            catch { parentCoverageComplete = false; complete = false; if (reason.Length == 0) reason = "action-parent-scan-failed"; }
        }

        try
        {
            var menuPage = page.TryCast<CM_PageBase>();
            if (menuPage == null) { complete = false; if (reason.Length == 0) reason = "page-type-unreadable"; }
            else
            {
                var registered = menuPage.m_allItems;
                if (registered == null) { complete = false; if (reason.Length == 0) reason = "registry-unreadable"; }
                else
                {
                    for (int index = 0; index < registered.Length; index++)
                    {
                        var item = registered[index];
                        if (item == null) continue;
                        CM_Item native = item.TryCast<CM_Item>();
                        if (native == null)
                        {
                            // A registered interaction that is not a CM_Item is a hole in the page's structure as
                            // this scan understands it, so it is counted and the scan stays incomplete.
                            tally = tally.ActiveControl();
                            complete = false; if (reason.Length == 0) reason = "registry-entry-unreadable"; continue;
                        }
                        AddControl(native);
                    }
                }
            }
            foreach (CM_Item native in page.GetComponentsInChildren<CM_Item>(true)) AddControl(native);
            foreach (Button button in page.GetComponentsInChildren<Button>(true))
                if (button != null) AddControl(button.TryCast<Component>());

            var success = page.TryCast<CM_PageExpeditionSuccess>();
            if (success != null) ScanSiblingParent(success.m_btnLeaveExpedition, true);
            else
            {
                var fail = page.TryCast<CM_PageExpeditionFail>();
                if (fail == null) { complete = false; parentCoverageComplete = false; if (reason.Length == 0) reason = "failure-page-type-unreadable"; }
                else
                {
                    ScanSiblingParent(fail.m_btnGoToLobby, true);
                    ScanSiblingParent(fail.m_btnRestartCheckpoint, false);
                }
            }
        }
        catch { complete = false; parentCoverageComplete = false; if (reason.Length == 0) reason = "page-scan-failed"; }

        // The waiting-status line is a lower wall, never an upper anchor. A frame in which the page has switched
        // it off is a frame without that wall rather than an unreadable page: it cannot take the pointer either,
        // and the next repaint re-reads it through the same scan.
        //
        // It is not one of the page's interactions, so the rectangle it *does* hand over only becomes a lower wall
        // and never moves `Count`. A line whose geometry nobody can read is the one case that has to move it: a
        // wall that is there but could not be measured this frame leaves the safe area without any lower bound at
        // all, and the board is then free to stand on the very text that must stay readable. It is therefore
        // counted as an interaction that is on the page and was not measured - two readings of the same element,
        // exactly like an active control with an unreadable rectangle - which keeps `ProvenEmpty` false and keeps
        // `ResultNoFooterGate` off its zero here. `Unmeasurable` is deliberately left alone: it is the count the
        // one-off downgrade line reads, and that line's sentence is "this page is not deferred on account of it",
        // which is not true of an element that stops the guest safe area outright. A line whose own *state* cannot
        // be read is still the structural case and keeps the scan incomplete, because there is no telling whether it
        // is on the screen at all.
        // Footer text is measured separately from any real button that contains it.
        if (protectedPageText != null)
        {
            try
            {
                bool protectedActive = protectedPageText.gameObject.activeInHierarchy;
                if (!protectedActive) { }
                else if (!TryScreenRect(protectedPageText, out LayoutRect rect))
                {
                    tally = tally.ActiveControl();
                }
                else
                {
                    actions.Add(new ResultActionBounds(rect, protectedPageText: true));
                    owners.Add(protectedPageText);
                    // 1.19.38: 客机上这一行等待状态文字不是可交互控件，也不再是条带的下界——固定页面几何才是。
                    // 它因此不能触发 `OverlapsBoard` 的遮挡拒绝：那条拒绝会走 `DeferBoardForInteraction`，
                    // 把板子整体撤下来，于是客机页面上完全没有板子。主机上它仍然是页脚文字，遮挡拒绝保持原样。
                    // 真正可按的控件走上面的 `AddControl`，那一路没有任何角色判断。
                    if (inspectBoard && !client && ResultInteractionStripPolicy.OverlapsBoard(rect, board, ResultBoardHold.ButtonClearance))
                    {
                        overlaps = true;
                        blockers++;
                        if (!blocker.Any) blocker = Blocker(protectedPageText, rect, pressable: false);
                    }
                }
            }
            catch { complete = false; if (reason.Length == 0) reason = "protected-text-unreadable"; }
        }

        // A control that is on but had no rectangle is a real interaction the collision set could not place, which
        // is a verdict about this one frame rather than about the page. The scan therefore no longer refuses the
        // page for it: the strip is resolved from the rectangles that could be read, and `BoardStillSafe` asks
        // again on the next repaint.
        if (!ResultInteractionStripPolicy.TryResolve(anchorBottom, gap, actions, out ResultInteractionStrip strip, column))
        { complete = false; strip = default; if (reason.Length == 0) reason = "strip-unresolved"; }
        if (!parentCoverageComplete) complete = false;
        // A scan that refused the page for a structural reason and also saw an unmeasurable control states the
        // structural reason only; the log line reports both counts under their own names either way.
        if (!complete && reason.Length == 0) reason = "structural-coverage-unreadable";
        // `Count` is the number of interactions the page had switched on - the ones whose rectangle could be read
        // and the ones it could not - and never the number of rectangles the strip managed to place: the latter is
        // `strip.FooterActionCount`, a diagnostic of the strip alone. `Count` is what `Tally`, `ProvenEmpty`,
        // `BoardStillSafe` and `ResultNoFooterGate.Observe` all read, so taking it from the strip is exactly how a
        // page whose every active control was unmeasurable came to claim it had no footer and hand the guest safe
        // page whose every active control was unmeasurable came to claim it had no footer and hand the guest safe
        // area the whole screen. `ScannedCount` is everything the scan visited, switched on or off.
        //
        // the board: `ResultBoardBands.TryBelowActions` carves the measured rectangles out of the page and answers with
        // the tallest band left. It is measured from the strip's own top, so a control that pushed the strip down can
        // never leave a band above itself. Nothing is decided from it here - the page's own role decides whether it may
        // be used - and a scan that found no control gets no band, which is the same answer as "the strip is the page".
        float bandAnchor = float.IsFinite(strip.BoardTop) ? strip.BoardTop : anchorBottom + gap;
        ResultBoardBands.TryBelowActions(bandAnchor, Screen.height, actions, gap, out ResultBoardBand belowActions);
        // 1.19.51: which interaction ended this band, named with its own type, object name and rectangle. The recorded
        // success page could not answer that question at all - it printed a 190 px strip and nothing else - and the
        // answer is what tells a reader whether the wall is the page's own footer (a control that really spans the
        // board's column) or something standing beside it. Bounded: the first control whose own top *is* the wall.
        string wallIdentity = string.Empty;
        if (float.IsFinite(strip.FooterTop))
        {
            for (int index = 0; index < actions.Count && index < owners.Count; index++)
            {
                ResultActionBounds action = actions[index];
                if (Math.Abs(action.Bounds.Y - strip.FooterTop) > 0.5f) continue;
                wallIdentity = Blocker(owners[index], action.Bounds, pressable: !action.IsProtectedPageText).Describe();
                break;
            }
        }
        return new FooterControls(strip.FooterTop, strip.BoardTop, tally.Active, tally.Hidden + tally.Active, complete, parentCoverageComplete, overlaps,
            tally.Hidden, tally.HiddenPageActions, reason, tally.Unmeasurable, belowActions, strip.PressableTop,
            blocker, blockers, strip.BeyondColumn, wallIdentity);
    }
    // 一个相交控件在日志里要有的身份：它是什么组件、对象叫什么名字、它自己的真实矩形。名字读不出来不算结构故障
    // ——它只是这一行的措辞少一段，决定本身不受影响。
    private static ResultFixedBlocker Blocker(Component control, LayoutRect rect, bool pressable)
    {
        string name = "(unnamed)", type = "control";
        try
        {
            if (control != null && control.gameObject != null) name = control.gameObject.name;
        }
        catch { name = "(unreadable)"; }
        try { type = control != null ? control.GetType().Name : "control"; }
        catch { type = "(unreadable)"; }
        return new ResultFixedBlocker(rect, name, type, pressable);
    }


    private static bool IsKnownClientHiddenLobbyAction(Component page, Component control)
    {
        try
        {
            var success = page.TryCast<CM_PageExpeditionSuccess>();
            if (success != null && success.m_btnLeaveExpedition != null
                && success.m_btnLeaveExpedition.Pointer == control.Pointer) return true;
            var fail = page.TryCast<CM_PageExpeditionFail>();
            return fail != null && fail.m_btnGoToLobby != null && fail.m_btnGoToLobby.Pointer == control.Pointer;
        }
        catch { return false; }
    }


    // The failure title is never moved and the success-page completion edge is never reused for a failure. Placement
    // starts below the native mission-title geometry only after its full-opacity gate settles, or after the native
    // failure sequence completes and the same mission-title layout is independently measured and stable. A page
    // that supplies neither proof keeps the vanilla screen; no status-text or window-fraction upper fallback exists.

    // A component's screen rectangle: the pixels it really occupies on the frame the player is looking at.
    //
    // A Renderer is measured from its own world bounds - the eight corners of `renderer.bounds` projected through
    // the camera - and that is the first thing asked for, because the parts of these pages that matter most are not
    // UI at all: the cleared-sector icons and their captions are world-space renderers, and a build whose icon host
    // carries no RectTransform of its own was read as "unmeasurable" while its pixels were right there on the
    // screen. A RectTransform is only the fallback, and for a real UI element it is the whole measurement: an
    // overlay canvas is measured with no camera at all, which is what RectTransformUtility expects, and a canvas
    // drawn by a camera is measured through that camera. Either way the figure is the rectangle the player sees;
    // `ScreenBounds` then applies the one set of sanity rules to the projected corners - a corner behind the camera
    // or not a number, a rectangle a pixel tall, off the frame, or the whole window is discarded rather than
    // trusted - so a renderer measured from its bounds and an element measured from its rect are judged alike.
    private static bool TryScreenRect(Component component, out LayoutRect rect)
    {
        rect = default;
        if (component == null) return false;
        try
        {
            Camera camera = PageCamera(component, out _);
            Renderer renderer = component.GetComponent<Renderer>();
            // A world position cannot be turned into a screen position without a camera, so a renderer with no
            // camera to project it is measured from its RectTransform instead - never from a guessed direction.
            bool worldBounds = renderer != null && camera != null && renderer.bounds.size.sqrMagnitude > 0.0001f;
            RectTransform element = component.transform.TryCast<RectTransform>();
            if (!worldBounds && element == null) return false;
            Bounds bounds = worldBounds ? renderer.bounds : default;
            Rect local = worldBounds ? default : element.rect;
            int corners = worldBounds ? 8 : 4;
            var xs = new List<float>(corners);
            var ys = new List<float>(corners);
            for (int corner = 0; corner < corners; corner++)
            {
                Vector3 world = worldBounds
                    ? bounds.center + Vector3.Scale(bounds.extents, new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1))
                    : element.TransformPoint(new Vector3((corner & 1) == 0 ? local.xMin : local.xMax, (corner & 2) == 0 ? local.yMin : local.yMax, 0f));
                // Behind the camera: there is no screen position for this corner, and half a box is not a box.
                if (camera != null && camera.WorldToScreenPoint(world).z <= 0f) return false;
                Vector2 screen = RectTransformUtility.WorldToScreenPoint(camera, world);
                // 1.19.55: `WorldToScreenPoint` answers in the camera's own raster, and this rectangle has to be in the window
                // pixels the board is drawn and hit in. One conversion, the same one the pointer asks for
                // (`ResultPointerSpace`), so a camera that does not render the whole window - the letterboxed 1280x960 case the
                // session could not explain - can never move a control without moving the pointer with it.
                ResultPointerViewport viewport = PointerViewport(camera, out _);
                if (!ResultPointerSpace.TryWindow(viewport, screen.x, screen.y, 1f, out float windowX, out float windowY, out _))
                    return false;
                xs.Add(windowX);
                ys.Add(windowY);
            }
            return ScreenBounds.TryFromCorners(xs, ys, Screen.width, Screen.height, out rect);
        }
        catch (Exception error)
        {
            if (!_loggedMeasure)
            {
                _loggedMeasure = true;
                Plugin.Logger?.LogWarning("[DIAG] failure heading screen projection failed; the rectangle is unmeasurable this frame, so nothing is placed from it: " + error.Message);
            }
            return false;
        }
    }

    // The camera an element of this page is drawn through, and whether it is an overlay canvas that needs none.
    //
    // One ladder, because two different measurements are projected through it - a renderer's own world bounds
    // (`TryScreenRect`) and a TextMeshPro text's laid-out extent (`TryTextRect`) - and the two must not disagree about
    // which camera the page is being looked through. The page's own camera is not always assigned on the frame the board
    // attaches (the menu camera comes up with the page), and the completion section's icons are world-space objects with
    // no canvas above them, so a missing camera is looked for instead of read as "unmeasurable".
    private static Camera PageCamera(Component component, out bool overlay)
    {
        Camera camera = CM_PageBase.Camera;
        Canvas canvas = component.GetComponentInParent<Canvas>();
        overlay = canvas != null && canvas.renderMode == RenderMode.ScreenSpaceOverlay;
        if (canvas != null && !overlay && canvas.worldCamera != null) camera = canvas.worldCamera;
        if (!overlay && camera == null)
        {
            camera = Camera.main;
            if (camera == null)
            {
                var cameras = Camera.allCameras;
                if (cameras != null)
                    foreach (Camera candidate in cameras)
                    {
                        if (candidate == null || !candidate.isActiveAndEnabled) continue;
                        camera = candidate; break;
                    }
            }
        }
        if (overlay) camera = null;
        return camera;
    }

    // 客机失败页的页脚槽位：只认「同一个原生返回位置」的两路几何，各自是自己的对象、自己的相机换算。
    //
    // 评审 #3/#4 否掉了 1.19.61 的第一次尝试（父容器子树扫描 + `textBounds` 代理）：那既会把无关/隐藏文本算进来，
    // 也仍然不是这一行字真正画出来的顶点。现在的权威只有两路，都来自页面自己：
    //
    //   * `m_btnGoToLobby` 的已布局矩形——主机页量的就是这同一个原生对象（`ButtonActive` + `TryScreenRect`），
    //     客机上它永久不可点，但它存在、也占着那个槽位，所以这里**不读 active**，只要求有效布局证据；
    //   * `m_returnToLobby_text` 自己的字形 mesh 顶点（`textInfo.meshInfo[..].vertices` 中可见字符的四个角），
    //     不是容器、不是别的文本的代理。
    //
    // 两者取更靠上的那一堵；都量不到时**没有权威**——调用方保留原版并按既有节流重测，F8 也不会拿到一条从布局框、
    // 别的可按控件或屏幕下界编出来的带。
    private static ResultClientSlotEvidence ClientSlot(Component page)
    {
        int frame = Time.frameCount;
        IntPtr pointer = page != null ? page.Pointer : IntPtr.Zero;
        if (frame == _slotFrame && pointer == _slotPage) return _slot;
        _slotFrame = frame; _slotPage = pointer;
        _slot = MeasureClientSlot(page);
        return _slot;
    }

    private static ResultClientSlotEvidence MeasureClientSlot(Component page)
    {
        float hostSlot = float.NaN, glyph = float.NaN, titleBottom = float.NaN;
        try
        {
            var fail = page != null ? page.TryCast<CM_PageExpeditionFail>() : null;
            if (fail != null)
            {
                TextMeshPro title = fail.m_missionFailed_text;
                if (title != null && TryScreenRect(title, out LayoutRect titleRect)) titleBottom = titleRect.Bottom;
                TextMeshPro slotText = fail.m_returnToLobby_text;
                bool glyphExpected = SlotTextExpected(slotText);
                glyph = HintGlyphTop(slotText, titleBottom);
                hostSlot = HostSlotTop(fail, slotText, titleBottom, glyphExpected, float.IsFinite(glyph));
            }
        }
        catch { }
        float top = ResultClientSlotPolicy.Authority(hostSlot, glyph, out ResultClientWallSource source);
        return new ResultClientSlotEvidence(hostSlot, glyph, top, source);
    }

    // 槽位那行字此刻**应该正在画**：存在、可见（`ResultFooterTextPolicy.Counts` 的规则）、文本非空。
    // 只用来判断「该画的字没量到」这个状态——不用来区分主客，也不问能不能点。
    private static bool SlotTextExpected(TextMeshPro text)
    {
        if (text == null) return false;
        try
        {
            bool active = text.gameObject.activeInHierarchy;
            if (!ResultFooterTextPolicy.Counts(active, HeadingAlpha(text))) return false;
            return !string.IsNullOrWhiteSpace(text.text);
        }
        catch { return false; }
    }

    // 客机上「返回大厅」按钮的槽位读数。
    //
    // 评审 #3 的残口：`TryScreenRect` 在没有可用 renderer 时会退回裸 RectTransform，于是「550x100 的布局盒」也能当权威
    // （1.19.60 的 1338 就是这么来的）。现在只有三种读数算数（判据在 `ResultClientSlotPolicy.HostSlotAuthorised`）：
    //   * 按钮自己的**实体几何**（renderer 包围盒路线，与能不能点无关）；
    //   * 槽位那行字自己的**字形**（`HintGlyphTop` 已经量到）；
    //   * 该画的字本来就不在时，**同一个节点**的布局矩形（预制件里 TMP 90058 与 CM_Item 93181 共用 RectTransform 86911）。
    // 该画的字此刻应该在画却没量到 → 一律不授权：保留已证的边界（`Measure` 仍用门保留的墙）或原版重测，F8 同样不给。
    private static float HostSlotTop(CM_PageExpeditionFail fail, TextMeshPro slotText, float titleBottom,
        bool glyphExpected, bool glyphMeasured)
    {
        if (fail == null || fail.m_btnGoToLobby == null) return float.NaN;
        try
        {
            bool entity = EntityBoundsMeasured(fail.m_btnGoToLobby);
            bool sameEntity = SameSlotEntity(fail.m_btnGoToLobby, slotText);
            if (!ResultClientSlotPolicy.HostSlotAuthorised(entity, glyphExpected, glyphMeasured, sameEntity))
                return float.NaN;
            if (!TryScreenRect(fail.m_btnGoToLobby, out LayoutRect rect)) return float.NaN;
            if (!(rect.Height > 0.5f) || !(rect.Width > 0.5f)) return float.NaN;
            if (rect.Y < ResultsBounds.ScreenMargin) return float.NaN;
            if (rect.Bottom > Screen.height - ResultsBounds.ScreenMargin) return float.NaN;
            if (float.IsFinite(titleBottom) && rect.Y < titleBottom) return float.NaN;
            return rect.Y;
        }
        catch { return float.NaN; }
    }

    // `TryScreenRect` 的路线选择逐字复制：有 renderer、有相机、包围盒非退化 → 它量的是实体几何，而不是布局矩形。
    private static bool EntityBoundsMeasured(Component entity)
    {
        if (entity == null) return false;
        try
        {
            Renderer renderer = entity.GetComponent<Renderer>();
            return renderer != null && PageCamera(entity, out _) != null
                && renderer.bounds.size.sqrMagnitude > 0.0001f;
        }
        catch { return false; }
    }

    // 同一个节点：预制件里 `m_returnToLobby_text`(TMP 90058) 与 `m_btnGoToLobby`(CM_Item 93181) 在同一个 GameObject 上、
    // 共用同一个 RectTransform 86911（`native-slot-evidence.md` §1.2/§2）。这才是「同一个槽位」的可验证证明。
    private static bool SameSlotEntity(Component button, Component text)
    {
        if (button == null || text == null) return false;
        try
        {
            Transform left = button.transform, right = text.transform;
            if (left == null || right == null) return false;
            if (left.Pointer == right.Pointer) return true;
            return left.gameObject.Pointer == right.gameObject.Pointer;
        }
        catch { return false; }
    }

    // `m_returnToLobby_text` 真正画出来的字形的上沿：逐个可见字符的四个顶点，经同一个相机阶梯投到窗口像素。
    // 这是评审要求的「渲染顶点/实际 mesh」，不是 `textBounds` 也不是容器代理。
    private static float HintGlyphTop(TextMeshPro text, float titleBottom)
    {
        if (text == null) return float.NaN;
        try
        {
            TMP_TextInfo info = text.textInfo;
            if (info == null || info.characterCount <= 0 || info.meshInfo == null) return float.NaN;
            Transform transform = text.transform;
            if (transform == null) return float.NaN;
            Camera camera = PageCamera(text, out _);
            ResultPointerViewport viewport = PointerViewport(camera, out _);
            var xs = new List<float>(8);
            var ys = new List<float>(8);
            for (int i = 0; i < info.characterCount; i++)
            {
                TMP_CharacterInfo character = info.characterInfo[i];
                if (!character.isVisible) continue;
                int material = character.materialReferenceIndex;
                if (material < 0 || material >= info.meshInfo.Length) continue;
                Vector3[] vertices = info.meshInfo[material].vertices;
                if (vertices == null) continue;
                for (int corner = 0; corner < 4; corner++)
                {
                    int index = character.vertexIndex + corner;
                    if (index < 0 || index >= vertices.Length) continue;
                    Vector3 world = transform.TransformPoint(vertices[index]);
                    if (camera != null && camera.WorldToScreenPoint(world).z <= 0f) return float.NaN;
                    Vector2 screen = RectTransformUtility.WorldToScreenPoint(camera, world);
                    if (!ResultPointerSpace.TryWindow(viewport, screen.x, screen.y, 1f, out float windowX, out float windowY, out _))
                        return float.NaN;
                    xs.Add(windowX);
                    ys.Add(windowY);
                }
            }
            if (xs.Count == 0) return float.NaN;
            if (!ScreenBounds.TryFromCorners(xs, ys, Screen.width, Screen.height, out LayoutRect rect)) return float.NaN;
            if (float.IsFinite(titleBottom) && rect.Y < titleBottom) return float.NaN;
            return rect.Y;
        }
        catch { return float.NaN; }
    }

    // 一帧的槽位读数（同帧只量一次：`FailureStrip` 一次 repaint 里会被落位、逐帧校验与兜底各问一次）。
    private readonly struct ResultClientSlotEvidence
    {
        public readonly float HostSlotTop, HintGlyphTop, Top;
        public readonly ResultClientWallSource Source;
        public ResultClientSlotEvidence(float hostSlotTop, float hintGlyphTop, float top, ResultClientWallSource source)
        {
            HostSlotTop = hostSlotTop; HintGlyphTop = hintGlyphTop; Top = top; Source = source;
        }
        public bool Fits { get { return float.IsFinite(Top); } }
    }

    // The rectangle a TextMeshPro text really draws, in window pixels.
    //
    // `textBounds` is the text's own laid-out extent in its local space, and it is the same figure for the world-space
    // `TextMeshPro` and for the UI `TextMeshProUGUI` - which is the half the renderer route cannot see: the first
    // carries a MeshRenderer whose bounds are the generated glyph quads, the second carries a CanvasRenderer, and a
    // CanvasRenderer has no bounds at all. The eight corners are projected through the page's camera with the same
    // ladder and the same sanity rules (`ScreenBounds.TryFromCorners`) every other measurement here goes through: a text
    // that projects to the whole window, off the frame, or to half a box is not a measurement of anything.
    private static bool TryTextRect(TMP_Text text, out LayoutRect rect)
    {
        rect = default;
        if (text == null) return false;
        try
        {
            Bounds local = text.textBounds;
            if (!(local.size.sqrMagnitude > 0.0001f)) return false;
            Transform transform = text.transform;
            if (transform == null) return false;
            Camera camera = PageCamera(text, out _);
            var xs = new List<float>(8);
            var ys = new List<float>(8);
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 world = transform.TransformPoint(local.center + Vector3.Scale(local.extents,
                    new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1)));
                // Behind the camera: there is no screen position for this corner, and half a box is not a box.
                if (camera != null && camera.WorldToScreenPoint(world).z <= 0f) return false;
                Vector2 screen = RectTransformUtility.WorldToScreenPoint(camera, world);
                // 1.19.55: the same one conversion the pointer and `TryScreenRect` use - a text block measured on a different
                // grid from the board it is protected against is exactly how a control ends up "not overlapping" the table
                // that covers it.
                ResultPointerViewport viewport = PointerViewport(camera, out _);
                if (!ResultPointerSpace.TryWindow(viewport, screen.x, screen.y, 1f, out float windowX, out float windowY, out _))
                    return false;
                xs.Add(windowX);
                ys.Add(windowY);
            }
            return ScreenBounds.TryFromCorners(xs, ys, Screen.width, Screen.height, out rect);
        }
        catch { return false; }
    }

    // ---- the vanilla blocks the board replaces ---------------------------------------------------

    // The blocks the board stands in for, put away again on every frame the page is up.
    //
    // Switching a block off is not enough on its own: the result pages animate their content in over
    // several seconds and switch it back on as they go, which is why a block that is only deactivated
    // blinks back in. The blocks are therefore moved right out of the page, which survives whatever the
    // page does to them, and deactivated too in case a layout group is what drives their position. One
    // block at a time, each in its own guard, and only when the config asks for it: a field that is null
    // on this game build is skipped and the board simply draws over it.
    //
    // The artifact blocks and the four prisoner report cards are two separate switches, and both are off
    // by default (建议关闭显示以免影响观感), so this mod's board owns the page. Whenever a switch is
    // turned back on, the page changes or the results screen is left, every block this mod moved is put
    // back at the exact position and in the exact active state it had before - which is what the previous
    // release could not do, because it only ever moved them off the page.
    // `visualOnly` is the early board's own frame (1.19.59): the page's blocks make way for a board that is painted, but
    // a picture on the page may not move or switch anything off - it may only stop drawing what it replaces. The mature
    // board takes the same blocks the rest of the way on the frame it really places.
    private static void ApplyVanillaBlocks(Component page, bool succeeded, bool visualOnly = false)
    {
        if (page == null) return;
        // Both switches are positive and off by default: the block is put away unless the player asked for
        // it back.
        bool successBlocks = succeeded && Plugin.HideVanillaArtifacts;
        bool prisonerReports = succeeded && Plugin.HideVanillaReports;
        bool failBlocks = !succeeded && Plugin.HideVanillaFailBlocks != null && Plugin.HideVanillaFailBlocks.Value;
        // A switch that changed is a set of blocks that has to be put back before the new set is applied.
        if (_appliedAny && (successBlocks != _appliedSuccessBlocks || prisonerReports != _appliedPrisonerReports || failBlocks != _appliedFailBlocks))
            RestoreHidden();
        _appliedSuccessBlocks = successBlocks; _appliedPrisonerReports = prisonerReports; _appliedFailBlocks = failBlocks;
        _appliedAny = successBlocks || prisonerReports || failBlocks;
        if (!_appliedAny) return;
        _vanillaHiddenAt = Time.realtimeSinceStartup;
        // A board really drew on this frame, so the refusals that came before it are answered: the next refusal starts the
        // count again from zero (this is what keeps a page that alternates draw/refuse from ever tripping the give-back).
        _vanillaRefusals = 0; _vanillaRefusalFrame = -1;

        if (succeeded)
        {
            var success = page.TryCast<CM_PageExpeditionSuccess>();
            if (success == null) return;
            if (successBlocks)
            {
                Hide(success.m_ArtifactInventoryDisplay, "artifact inventory", page, succeeded, visualOnly);
                Hide(success.m_artifactInfo_text, "artifact summary", page, succeeded, visualOnly);
                Hide(success.m_checkpointUsage, "checkpoint usage", page, succeeded, visualOnly);
            }
            if (prisonerReports)
            {
                var reports = success.m_playerReports;
                int count = reports == null ? 0 : reports.Length;
                for (int index = 0; index < count; index++) Hide(reports[index], "prisoner report " + index, page, succeeded, visualOnly);
                var anchors = success.m_playerReportAligns;
                int anchors_ = anchors == null ? 0 : anchors.Length;
                for (int index = 0; index < anchors_; index++) Hide(anchors[index], "prisoner report anchor " + index, page, succeeded, visualOnly);
            }
        }
        else
        {
            var fail = page.TryCast<CM_PageExpeditionFail>();
            if (fail == null) return;
            Hide(fail.m_ArtifactInventoryDisplay, "artifact inventory", page, succeeded, visualOnly);
            Hide(fail.m_artifactInfo_text, "artifact summary", page, succeeded, visualOnly);
        }
    }

    // The page's own blocks are given back when this mod has really stopped standing in for them: no board has been drawn
    // for `VanillaHideGrace` seconds while a result page is still up (a page that never places, another mod's control that
    // keeps deferring it, a fit the page refuses). Called once per attached repaint; it reads a clock, never a frame count,
    // so the answer is the same at 30 and at 240 FPS.
    private static void VanillaHideExpired()
    {
        if (Hidden.Count == 0) return;
        if (Time.realtimeSinceStartup - _vanillaHiddenAt <= VanillaHideGrace) return;
        RestoreHidden();
        if (ResultReportPolicyStrict.OncePerPage(HeadingReports, "vanilla-blocks-given-back"))
            Plugin.Logger?.LogInfo("[DIAG] the page's own blocks were given back because this board stopped standing in for them: no board has been drawn for "
                + VanillaHideGrace.ToString("0.0") + "s on this page, so the vanilla screen is the honest one - the next board that really places puts them away again (1.19.59).");
    }

    // How far off the page a hidden block is put: past any resolution, still a valid position.
    private const float AwayY = 6000f;

    // Everything this mod moved goes back, exactly. An entry is written the first time a block is touched
    // and never rewritten, so the recorded position is the page's own and not this mod's own displacement.
    //
    // 1.19.55 (A): the way back is written down. The recorded session had one `moved off the page` line and no line at
    // all for the restore, so "the blocks come back when hiding is switched off, when F8 hides the board, when the page
    // changes and when the results screen is left" could not be checked from a log. The line carries how many entries
    // were put back and whether any of them refused, and it is written only when something really was put back - the
    // frames that ask a page with nothing hidden print nothing at all.
    private static void RestoreHidden()
    {
        int restored = 0, refused = 0;
        foreach (ResultVanillaHold block in Hidden.Holds)
        {
            try
            {
                if (PutBack(block)) restored++;
            }
            catch (Exception error)
            {
                refused++;
                Plugin.Logger?.LogWarning("[DIAG] a vanilla block could not be put back where it was: " + error.Message);
            }
        }
        Hidden.Clear();
        _vanillaHiddenAt = float.NegativeInfinity;
        _vanillaRefusals = 0; _vanillaRefusalFrame = -1;
        if (restored == 0 && refused == 0) return;
        if (ResultReportPolicyStrict.OncePerPage(HeadingReports, "vanilla-blocks-restored"))
            Plugin.Logger?.LogInfo("[DIAG] the page's own blocks were put back where the game drew them: restored="
                + restored + ",refused=" + refused + ",pageActive=" + PageActive(_page)
                + "; every block this mod touched - moved whole, moved branch by branch when the block carried an action,"
                + " or only switched off at the drawing when it carried one this build may not move - stands at the"
                + " position, in the active state and at the alpha that were recorded before the first write, and this same"
                + " record answers the F8 hide, the switch being turned back on, the page changing, the results screen being"
                + " left, and a board that stopped standing in for the page (1.19.59).");
    }

    // One record, put back - and *only* the state this mod really took (M3). The answers come from the ledger, not from
    // this file: `TryRestorePosition` and `TryRestoreActive` are true only for a record whose move half was really taken,
    // so a block whose drawing alone was switched off can never be switched on by the way back, and the position written
    // is the one the page had before the first move (H1) instead of whatever `localPosition` this mod last left behind.
    // Nothing about raycasts appears here either: alpha and renderer enablement are the whole of the drawing half, so a
    // press the page hung inside a hidden block answers exactly as it did before (H2).
    private static bool PutBack(ResultVanillaHold block)
    {
        bool any = false;
        if (block.TryRestorePosition(out float x, out float y, out float z))
        {
            var target = block.Target as Transform;
            if (target != null) { target.localPosition = new Vector3(x, y, z); any = true; }
        }
        if (block.TryRestoreActive(out bool active))
        {
            var host = block.Host as GameObject;
            if (host != null && host.activeSelf != active) { host.SetActive(active); any = true; }
        }
        if (!block.VisualHeld) return any;
        if (block.TryRestoreGroup(out float alpha) && block.Group is CanvasGroup group)
        {
            group.alpha = alpha;
            any = true;
        }
        for (int index = 0; index < block.Renderers.Length; index++)
        {
            if (!(block.Renderers[index] is Renderer renderer) || renderer == null) continue;
            if (renderer.enabled == block.RendererEnabled[index]) continue;
            renderer.enabled = block.RendererEnabled[index];
            any = true;
        }
        for (int index = 0; index < block.Canvas.Length; index++)
        {
            if (!(block.Canvas[index] is CanvasRenderer canvas) || canvas == null) continue;
            try { canvas.SetAlpha(block.CanvasAlpha[index]); any = true; } catch { }
        }
        return any;
    }
    // Whether a block is the result page itself, or something the page hangs from. Compared by native pointer first
    // (the interop wrappers are not reference-equal across calls) and then by ancestry; a read that throws is
    // treated as "shared" because the safe answer for a block this mod cannot place is to leave the page alone.
    private static bool SharedOrRoot(Component block)
    {
        if (block == null) return true;
        try
        {
            if (_page != null && block.Pointer == _page.Pointer) return true;
            Transform host = block.transform;
            Transform page = _page != null ? _page.transform : null;
            if (host == null || page == null) return false;
            // True when the page hangs under this transform, i.e. the block is a shared ancestor of the screen.
            return page.IsChildOf(host);
        }
        catch { return true; }
    }

    // Whether this block's own subtree holds a control the player can press. `CM_Item` is the result pages' own action
    // type - the interaction scan reads exactly that type out of the page's registrations (`m_allItems`) and out of
    // each control's parent - so a "content" block that carries one is not content: switching it off takes the page's
    // own button with it, and a settlement page whose button does nothing is what that looks like from the player's
    // side. A read that throws answers true: a subtree this build cannot describe is left alone, the same way
    // `SharedOrRoot` treats an unreadable block.
    //
    // 1.19.55: `CM_Item` alone is not that question. The result pages hang *unregistered, switched-off* `CM_Item`
    // components inside their presentational blocks, and answering "yes" for one of those left `artifact summary` on the
    // page although the player had asked for it to be hidden (the recorded 7442/13753/20050/24656). A live action is one the
    // page can dispatch to (registered in `m_allItems`) or one that can take the pointer right now (active in the
    // hierarchy); everything else belongs to the block and may be put away with it (`ResultVanillaHidePolicy`).
    // 1.19.56 (A): the one classifier both halves of the question ask. A decoration inside one of this page's known
    // presentational blocks is *not* a wall for the scan and *is* hideable for the hide - because both call `KindOf`. The
    // node facts come from the page's own fields and components only: registration (`m_allItems`), active state, a
    // `UnityEngine.UI.Button` in the subtree, and a Renderer/CanvasRenderer for the block's own drawing.
    //
    // 1.19.57 (independent review of 1.19.56 A): the page is handed in, never read from the `_page`/`_succeeded` cache.
    // `ResetPlacement` clears that cache on the same frame the scan and the hide run, and the two are asked at different
    // moments of it, so asking the cache meant a live page could be described as "this page has no decoration block".
    private static VanillaNodeKind KindOf(Component node, Component page, bool succeeded)
    {
        if (node == null) return VanillaNodeKind.Unknown;
        try
        {
            bool inside = InsideDecorationBlock(node, page, succeeded, out bool root);
            bool active = node.gameObject.activeInHierarchy;
            bool button = HasButton(node);
            bool visual = HasVisual(node);
            var facts = new VanillaNodeFacts(inside, root, false, active, button, visual);
            // Registration is only meaningful for a `CM_Item`, and it is the page's own word.
            var item = node.TryCast<CM_Item>();
            if (item != null)
                facts = new VanillaNodeFacts(inside, root, RegisteredInPage(item, page), active, button, visual);
            return ResultVanillaClassify.Of(facts);
        }
        catch { return VanillaNodeKind.Unknown; }
    }

    // The block's *own* answer, which is what the plan is decided from. A `Button` in one of its branches belongs to that
    // branch, not to the whole block: reading the subtree's `Button` into the root is what turned every mixed block into
    // `Nothing` in 1.19.56, i.e. it made the player's switch do nothing on the one page the switch exists for.
    private static VanillaNodeKind RootKindOf(Component block, Component page, bool succeeded)
    {
        if (block == null) return VanillaNodeKind.Unknown;
        try
        {
            bool inside = InsideDecorationBlock(block, page, succeeded, out bool root);
            bool active = block.gameObject.activeInHierarchy;
            bool button = OwnButton(block);
            bool visual = HasVisual(block);
            return ResultVanillaClassify.Of(new VanillaNodeFacts(inside, root, RegisteredInPage(block, page), active, button, visual));
        }
        catch { return VanillaNodeKind.Unknown; }
    }

    private static bool OwnButton(Component node)
    {
        try { return node.GetComponent<Button>() != null; }
        catch { return true; }
    }

    private static bool HasButton(Component node)
    {
        try
        {
            Button[] buttons = node.GetComponentsInChildren<Button>(true);
            return buttons != null && buttons.Length > 0;
        }
        catch { return true; }
    }

    private static bool HasVisual(Component node)
    {
        try { return node.GetComponent<Renderer>() != null || node.GetComponent<CanvasRenderer>() != null; }
        catch { return false; }
    }

    // Whether this node lives inside one of the result pages' own presentational blocks, and whether it *is* that block.
    // The blocks are named by the native fields the hide path already used - never by text, never by a coordinate - and
    // only the ones the player's own switches ask this mod to put away count (`ResultVanillaBlockPolicy`).
    private static bool InsideDecorationBlock(Component node, Component page, bool succeeded, out bool containerRoot)
    {
        containerRoot = false;
        try
        {
            Transform transform = node != null ? node.transform : null;
            if (transform == null) return false;
            for (int step = 0; step < 8 && transform != null; step++)
            {
                if (IsDecorationBlock(transform, page, succeeded))
                {
                    if (step == 0) containerRoot = true;
                    return true;
                }
                transform = transform.parent;
            }
            return false;
        }
        catch { return false; }
    }

    // Whether this node is one of the blocks this mod is really about to put away on the page handed in. 1.19.56 walked
    // the ancestors and compared each ancestor's *component* pointer with the page's fields directly, which can never be
    // equal: `m_artifactInfo_text` is a text component and `m_ArtifactInventoryDisplay` a display component, while the
    // walk arrives with the node's `Transform` - three different native objects on the same GameObject. `SameNode` aligns
    // both sides onto the node they belong to, and the switches decide whether that node is a decoration at all.
    private static bool IsDecorationBlock(Transform node, Component page, bool succeeded)
    {
        if (node == null || page == null) return false;
        bool hideArtifacts = Plugin.HideVanillaArtifacts;
        bool hideReports = Plugin.HideVanillaReports;
        bool hideFailBlocks = Plugin.HideVanillaFailBlocks != null && Plugin.HideVanillaFailBlocks.Value;
        try
        {
            if (succeeded)
            {
                var success = page.TryCast<CM_PageExpeditionSuccess>();
                if (success == null) return false;
                if (ResultVanillaBlockPolicy.IsDecoration(true, hideArtifacts, hideReports, hideFailBlocks, VanillaBlockKind.ArtifactInventory)
                    && SameNode(success.m_ArtifactInventoryDisplay, node)) return true;
                if (ResultVanillaBlockPolicy.IsDecoration(true, hideArtifacts, hideReports, hideFailBlocks, VanillaBlockKind.ArtifactSummary)
                    && SameNode(success.m_artifactInfo_text, node)) return true;
                if (ResultVanillaBlockPolicy.IsDecoration(true, hideArtifacts, hideReports, hideFailBlocks, VanillaBlockKind.CheckpointUsage)
                    && SameNode(success.m_checkpointUsage, node)) return true;
                if (ResultVanillaBlockPolicy.IsDecoration(true, hideArtifacts, hideReports, hideFailBlocks, VanillaBlockKind.PlayerReport))
                {
                    var reports = success.m_playerReports;
                    if (reports != null)
                        for (int index = 0; index < reports.Length; index++)
                            if (SameNode(reports[index], node)) return true;
                }
                if (ResultVanillaBlockPolicy.IsDecoration(true, hideArtifacts, hideReports, hideFailBlocks, VanillaBlockKind.PlayerReportAnchor))
                {
                    var anchors = success.m_playerReportAligns;
                    if (anchors != null)
                        for (int index = 0; index < anchors.Length; index++)
                            if (SameNode(anchors[index], node)) return true;
                }
                return false;
            }
            var fail = page.TryCast<CM_PageExpeditionFail>();
            if (fail == null) return false;
            if (ResultVanillaBlockPolicy.IsDecoration(false, hideArtifacts, hideReports, hideFailBlocks, VanillaBlockKind.ArtifactInventory)
                && SameNode(fail.m_ArtifactInventoryDisplay, node)) return true;
            return ResultVanillaBlockPolicy.IsDecoration(false, hideArtifacts, hideReports, hideFailBlocks, VanillaBlockKind.ArtifactSummary)
                && SameNode(fail.m_artifactInfo_text, node);
        }
        catch { return false; }
    }

    // Two objects are the same node when they are anchored on the same native object. A field can name a component and a
    // walk arrives with that object's `Transform`, so the `Transform` is read first and the `GameObject` second - the two
    // anchors every wrapper of one node shares - and the comparison itself is `ResultNodeIdentity.Same`, the same rule a
    // test can ask with the distinct ids a page's three wrappers really have (1.19.57).
    private static bool SameNode(Component block, Transform node)
    {
        if (block == null || node == null) return false;
        IntPtr blockTransform = IntPtr.Zero, blockObject = IntPtr.Zero;
        try { Transform own = block.transform; if (own != null) blockTransform = own.Pointer; } catch { }
        try { GameObject mine = block.gameObject; if (mine != null) blockObject = mine.Pointer; } catch { }
        IntPtr nodeTransform = IntPtr.Zero, nodeObject = IntPtr.Zero;
        try { nodeTransform = node.Pointer; } catch { }
        try { GameObject other = node.gameObject; if (other != null) nodeObject = other.Pointer; } catch { }
        return ResultNodeIdentity.Same(blockTransform, blockObject, nodeTransform, nodeObject);
    }

    // The plan for one block, from the classifier - this is the production decision, not a policy that is only
    // exercised by a test.
    private static VanillaHidePlan PlanOfBlock(Component block, Component page, bool succeeded)
    {
        if (block == null) return VanillaHidePlan.Nothing;
        try
        {
            VanillaNodeKind root = RootKindOf(block, page, succeeded);
            CM_Item[] items = block.GetComponentsInChildren<CM_Item>(true);
            int count = items == null ? 0 : items.Length;
            int actions = 0, visuals = 0, branchesWithAction = 0;
            for (int index = 0; index < count; index++)
            {
                if (items[index] == null) continue;
                // An entry nobody can describe counts as an action: it is not a proven part of the page's own drawing, so
                // it is never one of the parts a mixed block may put away (1.19.57).
                if (KindOf(items[index], page, succeeded) == VanillaNodeKind.Visual) visuals++;
                else actions++;
            }
            Transform root_ = block.transform;
            int children = root_ != null ? root_.childCount : 0;
            for (int index = 0; index < children; index++)
            {
                Transform branch = root_.GetChild(index);
                Component node = branch != null ? branch.TryCast<Component>() : null;
                if (node == null) continue;
                if (HasButton(node) || LiveActionIn(node, page)) branchesWithAction++;
            }
            // 1.19.59 second pass: the subtree's own `Button` is handed in, so a block that really carries one can never be
            // answered with the drawing-only plan (that would leave an invisible-but-pressable control where the page drew it).
            return ResultVanillaClassify.PlanForBlock(root, actions, visuals, branchesWithAction, HasButton(block));
        }
        catch { return VanillaHidePlan.Nothing; }
    }

    private static bool LiveActionIn(Component node, Component page)
    {
        try
        {
            CM_Item[] items = node.GetComponentsInChildren<CM_Item>(true);
            if (items == null) return false;
            for (int index = 0; index < items.Length; index++)
                if (items[index] != null && RegisteredInPage(items[index], page)) return true;
            return false;
        }
        catch { return true; }
    }

    // Whether this subtree carries one of the page's own actions, asked of the same classifier the scan uses. A read that
    // throws answers true: a subtree this build cannot describe is never moved.
    private static bool CarriesAction(Component block, Component page, bool succeeded)
    {
        try { return KindOf(block, page, succeeded) == VanillaNodeKind.Action; }
        catch { return true; }
    }

    // Whether the page itself dispatches to this entry: `CM_PageBase.m_allItems` is the registry the interaction scan and
    // `UpdateButtonPress` both read.
    //
    // 1.19.57: a registry this build cannot read is *not* "not registered". 1.19.56 answered false for a null registry
    // while its own doc comment promised the opposite, which turned every entry of a page whose registry could not be read
    // into an unregistered decoration - the one answer that lets the hide path move a control the player needs.
    // "Unreadable" is answered true (registered), so an entry the page might dispatch to is protected, and the scan's own
    // `complete` flag keeps the board off a page whose registry it could not read.
    private static bool RegisteredInPage(CM_Item item, Component page)
    {
        try
        {
            if (item == null) return true;
            var menu = page != null ? page.TryCast<CM_PageBase>() : null;
            var registered = menu != null ? menu.m_allItems : null;
            if (registered == null) return true;
            IntPtr pointer = item.Pointer;
            for (int index = 0; index < registered.Length; index++)
            {
                var entry = registered[index];
                if (entry == null) continue;
                CM_Item native = entry.TryCast<CM_Item>();
                if (native != null && native.Pointer == pointer) return true;
            }
            return false;
        }
        catch { return true; }
    }

    private static bool RegisteredInPage(Component node, Component page)
    {
        var item = node != null ? node.TryCast<CM_Item>() : null;
        return item != null && RegisteredInPage(item, page);
    }

    // One `CM_Item` seen inside a block: `true` when the page can really press it.
    private static bool LiveAction(CM_Item item, Component page)
    {
        try
        {
            if (item == null) return true;
            bool active = item.gameObject.activeInHierarchy;
            return ResultVanillaHidePolicy.IsLiveAction(active, RegisteredInPage(item, page));
        }
        catch { return true; }
    }

    // A branch that really draws something of the page's own. Only such a branch may be put away: one that draws nothing
    // is not worth touching, and one this build cannot describe is left exactly where the game put it (1.19.57).
    private static bool ProvenVisual(Component node, Component page, bool succeeded)
    {
        try
        {
            if (KindOf(node, page, succeeded) == VanillaNodeKind.Visual) return true;
            Renderer[] renderers = node.GetComponentsInChildren<Renderer>(true);
            if (renderers != null && renderers.Length > 0) return true;
            CanvasRenderer[] canvas = node.GetComponentsInChildren<CanvasRenderer>(true);
            return canvas != null && canvas.Length > 0;
        }
        catch { return false; }
    }

    // A mixed subtree: content that happens to carry a decoration `CM_Item` somewhere. The branch(es) that carry a live
    // action are left untouched and the branch(es) that carry none are put away by the same discipline the whole block used
    // to take - moved off the page, switched off, the page's own position and active state recorded before anything moved, so
    // `RestoreHidden` puts them back exactly. Answers false when there is nothing that can be put away safely: a block whose
    // whole body is one branch, an unreadable child list, or a branch list in which every branch carries an action. The caller
    // then leaves the block exactly where the game put it.
    //
    // 1.19.57: a branch is only ever put away when three things are proven about it - it is inside this page's own declared
    // block (so an icon another mod parked beside it is never mistaken for the page's own drawing), no part of it is an
    // action the page can dispatch to or that can take the pointer now, and it really renders something. The block itself
    // and every ancestor of the page are refused before any of that (`SharedOrRoot`), so a move can never take a late
    // button's carrier off the page.
    // 1.19.58: `CarriesAction` answers the classifier, which asks "is *this node* an action"; a branch whose own node is a
    // plate with a renderer while a *descendant* is a registered `CM_Item` therefore came back as a proven visual and was
    // moved. `LiveActionIn(node, page)` is the second, independent question - does anything below this branch sit in the
    // page's registry - and it answers true when the registry or the subtree cannot be read, so an unreadable read can only
    // ever leave a branch where the game put it.
    private static bool HideSafeBranches(Component block, string what, Component page, bool succeeded)
    {
        int hidden = 0;
        try
        {
            Transform root = block != null ? block.transform : null;
            if (root == null) return false;
            int children = root.childCount;
            // One branch is the block itself: there is no "safe part" to take away that is not the whole of it.
            if (children <= 1) return false;
            for (int index = 0; index < children; index++)
            {
                Transform branch = root.GetChild(index);
                Component node = branch != null ? branch.TryCast<Component>() : null;
                if (node == null) continue;
                if (SharedOrRoot(node)) continue;
                if (CarriesAction(node, page, succeeded)) continue;
                if (LiveActionIn(node, page)) continue;
                if (!InsideDecorationBlock(node, page, succeeded, out _)) continue;
                if (!ProvenVisual(node, page, succeeded)) continue;
                var host = node.gameObject;
                IntPtr key = node.Pointer;
                // The branch's own position and active state, read at its first *move* and never before: a branch whose
                // drawing was switched off elsewhere has no move recorded yet, and `HoldMove` answers with the position
                // and state the page had the first time it was asked - every later ask reads the same page state back
                // (H1), so the write below is idempotent and the way back cannot land on a position this mod invented.
                Vector3 at = branch.localPosition;
                ResultVanillaHold record = Hidden.HoldMove(key, branch, host, at.x, at.y, at.z, host != null && host.activeSelf);
                branch.localPosition = new Vector3(record.X, AwayY, record.Z);
                if (host != null && host.activeSelf) host.SetActive(false);
                hidden++;
            }
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("[DIAG] the vanilla block '" + what + "' could not be put away branch by branch: " + error.Message);
        }
        if (hidden == 0) return false;
        if (ResultReportPolicyStrict.OncePerPage(HeadingReports, "vanilla-block-branches-" + what))
            Plugin.Logger?.LogInfo("[DIAG] the vanilla block '" + what + "' holds a control the player can press, so only the "
                + hidden + " branch(es) of it that carry none of the page's own actions were moved off the page and switched off: "
                + "the branch that carries the action is untouched, its own position and state were recorded before anything moved, "
                + "and every branch comes back the moment hiding is switched off, the page changes or the results screen is left (1.19.55).");
        return true;
    }

    // The page this frame really shows, asked of the page objects themselves: the draw path runs after the repaint that may
    // have cleared `_page`, and a block the player asked to hide must not depend on that cache (1.19.57).
    private static Component LivePage(out bool succeeded)
    {
        succeeded = _succeeded;
        if (_page != null) return _page;
        try
        {
            if (Success != null && Success.gameObject.activeInHierarchy) { succeeded = true; return Success; }
            if (Fail != null && Fail.gameObject.activeInHierarchy) { succeeded = false; return Fail; }
        }
        catch { }
        return null;
    }

    private static void Hide(Component block, string what, Component page, bool succeeded, bool visualOnly = false)
    {
        if (block == null) return;
        // 1.19.59 (independent review L4): a block this mod has already taken is not measured again. Everything below -
        // `PlanOfBlock`, the `GetComponentsInChildren<CM_Item>` walk and the branch-by-branch pass - costs a full walk of
        // the block, and this method runs from two entry points on every repaint, so a page with eight blocks walked its
        // own tree sixteen times a frame to learn the same answer. The ledger holds everything needed to keep a block
        // away; the one question that still has to be asked every frame is whether a control of the player's has appeared
        // inside it since. `HoldStillApplies` asks exactly that, re-applies the writes it already owns from the record
        // alone, and answers false - falling through to the classifier below - for any block it has not taken, so a
        // native control that appears later is still seen on the first frame it exists. There is no permanent skip list:
        // the record is what is skipped, and only while it is still true.
        if (HoldStillApplies(block, what)) return;
        // A board only ever puts away a leaf it stands in front of. The result page itself - or anything the page
        // hangs from - is never switched off, because that is the whole screen: the recorded "the vanilla settlement
        // page disappeared and came back" is one frame of exactly that failure mode, and no board is allowed to
        // produce it.
        if (SharedOrRoot(block))
        {
            if (ResultReportPolicyStrict.OncePerPage(HeadingReports, "vanilla-block-shared-" + what))
                Plugin.Logger?.LogWarning("[DIAG] the vanilla block '" + what + "' is the result page itself or something the page hangs from, so it is left switched on: "
                    + "the board only puts away a block it stands in front of, and the page root belongs to the game.");
            return;
        }
        // 1.19.43: nor is a block that carries a control the player has to press. The hide list is made of content -
        // the artifact summary, the checkpoint line, the prisoner report cards - but on a page whose layout hangs one of
        // the page's own actions inside one of those blocks, hiding it hides the action, and the mod's board is the
        // reason a button stopped answering.
        //
        // 1.19.55: a *mixed* subtree is answered branch by branch rather than left whole.
        // 1.19.56/1.19.57: the plan is the production decision, asked with the live page; an unreadable answer is
        // `Nothing` - never a whole block put away on the strength of an answer nobody could read.
        // 1.19.59: the plan's last two answers are both "the drawing goes away":
        //   * `SafeBranches` moves the branches that carry no action, and
        //   * `VisualOnly` - and a `SafeBranches` that could not move anything - switches the block's own drawing off
        //     without moving, deactivating or unregistering a single object (`SuppressVisual`).
        // The recorded defect this closes: `artifact summary holds a control the player can press, so it is left exactly
        // where the page put it` (r2 5845, r3 10703, r4 15978) left the red-crystal block and its `[i]`/`1.0` line drawn
        // under a board that was already on the page.
        //
        // `visualOnly` is the early board's own frame: it may only switch the drawing off, never move or deactivate
        // anything, because an early board is a picture and the page's own presses and wheel still belong to the page.
        VanillaHidePlan plan = PlanOfBlock(block, page, succeeded);
        if (plan == VanillaHidePlan.Nothing)
        {
            if (ResultReportPolicyStrict.OncePerPage(HeadingReports, "vanilla-block-action-" + what))
                Plugin.Logger?.LogInfo("[DIAG] the vanilla block '" + what + "' is the page's own control or an entry this build cannot describe, so it is left exactly where the page put it: "
                    + "nothing here may take a control off the page. The board does not depend on the space it would have freed.");
            return;
        }
        if (!visualOnly && plan == VanillaHidePlan.WholeBlock)
        {
            MoveOffPage(block, what);
            return;
        }
        if (!visualOnly && plan == VanillaHidePlan.SafeBranches && HideSafeBranches(block, what, page, succeeded)) return;
        // 1.19.59 second pass: the drawing-only half is never applied over a real `Button`. A block whose body carries one, and
        // whose branch could not be moved away from it, stays exactly where the game put it - a visible decoration is a worse
        // picture than no board, an *invisible but pressable* control is a worse control.
        if (HasButton(block))
        {
            if (ResultReportPolicyStrict.OncePerPage(HeadingReports, "vanilla-block-button-kept-" + what))
                Plugin.Logger?.LogInfo("[DIAG] the vanilla block '" + what + "' carries a control of the player's that cannot be moved away from it, so the block is left visible exactly where the page put it: "
                    + "switching its drawing off would leave that control invisible but still pressable, which is worse than the decoration it replaces (1.19.59).");
            return;
        }
        SuppressVisual(block, what);
    }

    // Whether this frame's `Hide` has nothing left to decide: the block is already held, and holding it is still the right
    // answer. The per-frame cost is the point of the method (L4), so it reads the record and nothing else - except for the
    // one question that must survive every deduplication: has a control of the player's appeared inside a block this mod
    // already put away? A moved block is switched off and off the page, so a `Button` that appears in it can never be
    // pressed and the player would lose it silently; an invisible-but-pressable one is no better. Either way the block is
    // given back exactly where the page put it and this frame's classifier is allowed to answer again.
    //
    // What is deliberately *not* here: the classifier (`PlanOfBlock`), the `CM_Item` walk, and any list that would skip a
    // block for the rest of the page's life. A block this mod has not taken, or one whose record no longer describes what
    // is on the page, falls through to `Hide`'s ordinary path - so newly appearing native controls and a structural change
    // are still seen, and the only thing that is skipped is work whose answer cannot have changed.
    private static bool HoldStillApplies(Component block, string what)
    {
        if (!Hidden.TryGetValue(block.Pointer, out ResultVanillaHold record)) return false;
        try
        {
            if (HasButton(block))
            {
                GiveBack(block, record, what);
                return false;
            }
            if (record.MoveHeld)
            {
                // Cheap verification of the two writes this record owns: the block is still off the page and still
                // switched off, and it is put back there only if the game moved or re-enabled it behind this mod.
                var target = record.Target as Transform;
                if (target != null && target.localPosition.y != AwayY) target.localPosition = new Vector3(record.X, AwayY, record.Z);
                var host = record.Host as GameObject;
                if (host != null && host.activeSelf) host.SetActive(false);
                return true;
            }
            if (record.VisualHeld)
            {
                ReapplyDrawing(record);
                return true;
            }
        }
        catch (Exception error)
        {
            // A record this frame cannot keep is not a record that may keep a block away: give it back and let the
            // classifier answer again, which is the same order of preference every other read here follows.
            Plugin.Logger?.LogWarning("[DIAG] the vanilla block '" + what + "' could not be kept away this frame: " + error.Message);
            GiveBack(block, record, what);
        }
        return false;
    }

    // One block given back in place, from the record alone: the drawing half's alpha and renderer states, then the move
    // half's position and active state - and only the halves that were really taken. Used when the block turns out to hold
    // a control of the player's after all (H2), which is the one thing a deduplicated hide may not go on ignoring.
    private static void GiveBack(Component block, ResultVanillaHold record, string what)
    {
        try
        {
            PutBack(record);
            Hidden.Drop(record.Key);
            if (ResultReportPolicyStrict.OncePerPage(HeadingReports, "vanilla-block-given-back-" + what))
                Plugin.Logger?.LogInfo("[DIAG] the vanilla block '" + what + "' carries a control the player can press after all, so it is given back exactly where the page put it: "
                    + "its recorded position, active state, alpha and renderers are put back from the record alone, the block stays visible and pressable where the game drew it, and the board steps around it (1.19.59).");
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("[DIAG] the vanilla block '" + what + "' could not be given back: " + error.Message);
        }
    }

    // The drawing half, re-asserted from the record: one group write, one loop over the renderers that were really
    // switched off, one over the canvas renderers whose alpha was really taken. No search of the block, no classifier, and
    // never a raycast flag (H2) - the drawing half owns the pixels and nothing else.
    private static void ReapplyDrawing(ResultVanillaHold record)
    {
        if (record.Group is CanvasGroup group)
        {
            if (group.alpha != 0f) group.alpha = 0f;
        }
        for (int index = 0; index < record.Renderers.Length; index++)
        {
            if (!(record.Renderers[index] is Renderer renderer) || renderer == null) continue;
            try { if (renderer.enabled) renderer.enabled = false; } catch { }
        }
        for (int index = 0; index < record.Canvas.Length; index++)
        {
            if (!(record.Canvas[index] is CanvasRenderer canvas) || canvas == null) continue;
            try { canvas.SetAlpha(0f); } catch { }
        }
    }

    // A block put away outright: moved past any resolution and switched off, with the position and active state the page
    // had recorded before anything moved. Only a block the classifier proved carries no control of the player's is ever
    // taken this way (`VanillaHidePlan.WholeBlock`).
    //
    // 1.19.59 (independent review H1): the capture belongs to the *move*, not to the first touch of any kind. A block whose
    // drawing was switched off first (`SuppressVisual`) used to hand this method a record with `Target = null` and
    // `Position = (0,0,0)`, which the move then reused - so F8, a page change or leaving the screens put the block back at
    // the origin instead of the place the page drew it, for the rest of the run. `HoldMove` answers with the page's own
    // position and state the first time it is asked and with that same answer every time after, so this write is
    // idempotent and the way back always lands on a position this mod really read off the page.
    private static void MoveOffPage(Component block, string what)
    {
        try
        {
            var target = block.transform;
            var host = block.gameObject;
            IntPtr key = block.Pointer;
            // Nothing can be moved and nothing can be put back without the transform: no record is written for it.
            if (target == null) return;
            Vector3 at = target.localPosition;
            ResultVanillaHold record = Hidden.HoldMove(key, target, host, at.x, at.y, at.z, host != null && host.activeSelf);
            target.localPosition = new Vector3(record.X, AwayY, record.Z);
            if (host != null && host.activeSelf) host.SetActive(false);
            if (!_loggedHidden)
            {
                _loggedHidden = true;
                Plugin.Logger.LogInfo("[DIAG] the vanilla " + what + " block was moved off the page and switched off so the board has the room; its exact original position and active state are remembered and put back the moment hiding is switched off, the page changes or the results screen is left. The mod never touches the text inside it.");
            }
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("[DIAG] the vanilla " + what + " block could not be hidden: " + error.Message);
        }
    }

    // The drawing half of hiding: the block stops being painted and nothing else about it changes.
    //
    // This is the answer to a block this build may not *move* - one whose own body carries a `Button`, one the page hangs
    // an action inside, one whose answer could not be read past the root. Switching it off (`SetActive(false)`), moving it
    // or unregistering it would take that control off the page; zero alpha takes only the pixels. Three routes, because a
    // GTFO result block is drawn three ways and none of them covers the others:
    //   * the block's *own* `CanvasGroup` at zero alpha for everything on a UI canvas (and it keeps the children's own
    //     alpha untouched),
    //   * `Renderer.enabled` for a world-space `TextMeshPro`/sprite - the artifact summary and the crystal are world text,
    //     not canvas graphics, so a CanvasGroup alone would leave them on the screen,
    //   * `CanvasRenderer.SetAlpha(0)` for a `Graphic` with no CanvasGroup above it.
    // Every one of them is recorded before it is written and put back by `RestoreHidden`, and none of them changes a
    // collider, an event handler, a `CM_Item` registration or a `Button`, so a press the page hung inside the block still
    // answers (it simply answers in the place a hidden control occupies).
    //
    // 1.19.59 (independent review, H2/M3):
    //   * H2 - `CanvasGroup.interactable`/`blocksRaycasts` are not read, not recorded and not written. A hidden block answers
    //     its presses exactly as the one the page drew, so a mixed subtree that really holds an action keeps its hit - and
    //     `Hide` never reaches this method at all for a block whose body carries a `Button`.
    //   * M3 - no `CanvasGroup` is ever added. A component added to the game's own object would be a permanent change
    //     nothing here may undo (a destroy that lands a frame late races the next frame's re-add), and the three routes
    //     above switch the drawing off without it: only a group the block already had is used, and only its alpha. A block
    //     with no group is drawn by its renderers and its canvas renderers, which are taken directly.
    //   * Only the state this mod really changed is recorded: a renderer that was already switched off, and a canvas
    //     renderer whose alpha was already zero, are left alone and are not part of the record.
    private static void SuppressVisual(Component block, string what)
    {
        try
        {
            IntPtr key = block.Pointer;
            if (!Hidden.TryGetValue(key, out ResultVanillaHold record) || !record.VisualHeld)
            {
                CanvasGroup group = null;
                try { group = block.GetComponent<CanvasGroup>(); } catch { group = null; }
                Renderer[] renderers = block.GetComponentsInChildren<Renderer>(true);
                var drawn = new List<Renderer>();
                if (renderers != null)
                {
                    for (int index = 0; index < renderers.Length; index++)
                    {
                        Renderer renderer = renderers[index];
                        if (renderer == null) continue;
                        bool on;
                        try { on = renderer.enabled; } catch { on = false; }
                        if (on) drawn.Add(renderer);
                    }
                }
                CanvasRenderer[] canvas = block.GetComponentsInChildren<CanvasRenderer>(true);
                var painted = new List<CanvasRenderer>();
                var alphas = new List<float>();
                if (canvas != null)
                {
                    for (int index = 0; index < canvas.Length; index++)
                    {
                        CanvasRenderer renderer = canvas[index];
                        if (renderer == null) continue;
                        float alpha;
                        try { alpha = renderer.GetAlpha(); } catch { alpha = 1f; }
                        if (alpha == 0f) continue;
                        painted.Add(renderer); alphas.Add(alpha);
                    }
                }
                var enabled = new bool[drawn.Count];
                for (int index = 0; index < enabled.Length; index++) enabled[index] = true;
                record = Hidden.HoldVisual(key, group, group != null ? group.alpha : 1f,
                    drawn.ToArray(), enabled, painted.ToArray(), alphas.ToArray());
            }
            ReapplyDrawing(record);
            if (ResultReportPolicyStrict.OncePerPage(HeadingReports, "vanilla-block-drawing-" + what))
                Plugin.Logger?.LogInfo("[DIAG] the vanilla block '" + what + "' was switched off at the drawing only: it is still on the page, still registered and still pressable where the game put it - "
                    + "only its pixels are gone (alpha 0, its own renderers off) - because moving or deactivating it would take a control the page hung inside it with it. "
                    + "The exact alpha and renderer states are recorded and put back the moment hiding is switched off, the page changes or the results screen is left; no CanvasGroup was added to the game's object and no raycast flag was touched (1.19.59).");
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("[DIAG] the vanilla " + what + " block could not have its drawing switched off: " + error.Message);
        }
    }
}

[HarmonyPatch(typeof(CM_PageExpeditionSuccess), "OnEnable")]
internal static class SuccessResultsPatch
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(CM_PageExpeditionSuccess __instance) => ResultsDisplay.BeginSuccessPage(__instance);

    [HarmonyPostfix]
    private static void Postfix(CM_PageExpeditionSuccess __instance) => ResultsDisplay.Success = __instance;
}

[HarmonyPatch(typeof(CM_PageExpeditionFail), "OnEnable")]
internal static class FailureResultsPatch
{
    // The prefix only drops what belonged to the previous visit to this page: the page object is reused for
    // every failed expedition in a session, so its title starts fading in from zero again and none of the settle
    // budgets the last visit spent - or the smaller board it was measured into - may be inherited by this one.
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(CM_PageExpeditionFail __instance) => ResultsDisplay.BeginFailurePage(__instance);

    [HarmonyPostfix]
    private static void Postfix(CM_PageExpeditionFail __instance) => ResultsDisplay.Fail = __instance;
}

// Bind readiness to the native failure coroutine returned for this page visit. Only its own terminal MoveNext(false)
// is evidence that the native failure/title sequence completed; a timeout or waiting-status string is not.
[HarmonyPatch(typeof(CM_PageExpeditionFail), "ExpeditionFailedSequence")]
internal static class FailureSequenceStartedPatch
{
    [HarmonyPostfix, HarmonyWrapSafe]
    private static void Postfix(CM_PageExpeditionFail __instance, Il2CppSystem.Collections.IEnumerator __result)
        => ResultsDisplay.FailureSequenceStarted(__instance, __result);
}

[HarmonyPatch(typeof(CM_PageExpeditionFail._ExpeditionFailedSequence_d__18), "MoveNext")]
internal static class FailureSequenceFinishedPatch
{
    [HarmonyPostfix, HarmonyWrapSafe]
    private static void Postfix(CM_PageExpeditionFail._ExpeditionFailedSequence_d__18 __instance, bool __result)
    {
        if (__result) return;
        ResultsDisplay.FailureSequenceFinished(__instance.__4__this, __instance.Pointer);
    }
}

// 1.19.44: the game's own press dispatch, skipped only while this mod owns the press.
//
// `CM_PageBase.UpdateButtonPress` is what turns the page's main button into presses for the page's own menu items. A
// click this mod answered on its toggle or close has already been spent on that control, and letting the same press run
// through the page's dispatch is exactly how one click would press two things. The prefix keeps no state of its own: it
// asks `ResultsDisplay` the one question, and that question is answered from the *frame's* decision - the same decision
// this mod's own `Update` makes, made here when the page's dispatch runs first (1.19.46, UI review F1 close-out), so the
// press that starts on one of this mod's controls is claimed before the page can act on it. Every answer that is not
// provably ours is `true`, so the game keeps every press that is not this mod's: another page, an inactive page, a window
// without focus, a released press, and a press older than the bounded fallback all pass.
//
// It is registered by the same `PatchAll()` as every other patch in this assembly, and it is safe on builds that do not
// have the dispatch point at all: Harmony simply never calls it there.
[HarmonyPatch(typeof(CM_PageBase), "UpdateButtonPress", new Type[0])]
internal static class SettlementPressPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(CM_PageBase __instance)
    {
        try { return !ResultsDisplay.ShouldSuppressNativePress(__instance); }
        catch { return true; }
    }
}
