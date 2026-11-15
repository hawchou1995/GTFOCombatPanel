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
        public bool ProvenEmpty => ResultInteractionScanPolicy.ProvenEmpty(Complete, ParentCoverageComplete, Tally);
        public FooterControls(float footerTop, float boardTop, int count, int scannedCount, bool complete,
            bool parentCoverageComplete, bool overlapsBoard, int hidden = 0, int hiddenPageActions = 0, string reason = "",
            int unmeasurable = 0, ResultBoardBand belowActions = default, float pressableTop = float.NaN)
        {
            FooterTop = footerTop; BoardTop = boardTop; Count = count; ScannedCount = scannedCount;
            Complete = complete; ParentCoverageComplete = parentCoverageComplete; OverlapsBoard = overlapsBoard;
            Hidden = hidden; HiddenPageActions = hiddenPageActions; Reason = reason ?? string.Empty;
            Unmeasurable = unmeasurable;
            Tally = new ResultInteractionTally(count, hidden, hiddenPageActions, unmeasurable);
            BelowActions = belowActions;
            PressableTop = pressableTop;
        }
    }

    private static readonly ResultNoFooterGate SuccessNoFooter = new ResultNoFooterGate();
    private static readonly ResultNoFooterGate FailureNoFooter = new ResultNoFooterGate();
    // 客机失败页的下界墙稳定门：一页一份。`ForgetPage` 是唯一的重置点，所以换页、离开结算页都会从零开始
    // 观察新页面的墙，而这一页上一次的读数（`Wall` 是这一页见过的最高一堵）绝不会跟到下一张页面上去。
    private static readonly ResultClientWallGate ClientWall = new ResultClientWallGate();

    // The vanilla blocks this mod has moved, with the exact position and active state each had before it
    // touched them. Restoration is the inverse of hiding, one block at a time: the moment a flag is
    // switched off, the page changes or the results screen is left, every block goes back to exactly
    // where it was and exactly the state it was in. The previous release only ever moved them off the
    // page, so switching the flag off left them off the page for good.
    private sealed class HiddenBlock
    {
        public Transform Target;
        public GameObject Host;
        public Vector3 Position;
        public bool Active;
    }
    private static readonly Dictionary<IntPtr, HiddenBlock> Hidden = new Dictionary<IntPtr, HiddenBlock>();
    // What was hidden the last time the page was touched, so a flag that changed can be noticed and the
    // blocks it covered restored before the new set is applied.
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
        // moved since (another mod's footer, a resolution change, a section that finished drawing later). Both
        // are budgeted per page, so a settled board is never rebuilt for a frame of the page's own animation.
        if (_placed && _placedSucceeded == succeeded && _grid != null && _content != null &&
            _page != null && _page.gameObject.activeInHierarchy)
        {
            if (!FallbackRecoveryDue(succeeded, _page) && RefreshBoardData(_page, succeeded)) return;
        }
        // A page whose own anchors have not arrived yet is measured every frame while its reveal runs, then at a
        // bounded cadence: measuring a page walks its renderers and every button it drew, and a page that never hands
        // an anchor over would pay for that on every frame for the rest of the expedition while the vanilla page is on
        // the screen anyway. A skipped frame draws nothing and hides nothing - the caches are already empty, so the
        // game's own page stays exactly as it drew it.
        if (!Retry.Due(Time.frameCount)) return;

        Component previous = _page;
        ResetPlacement();
        if (previous != null) RestoreHidden();
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
        // A different page is a different screen: the placement, the strip it was measured in and the
        // readiness of the previous one are dropped here and nowhere else. A configuration change, the F8
        // write it triggers and the hot reload that follows it all leave them standing.
        if (_holdPage != page.Pointer) ForgetPage();
        _holdPage = page.Pointer;
        _page = page; _succeeded = succeeded;
        Readiness.Bind(page.Pointer);
        try
        {
            Prepare(page, succeeded);
            if (ResultBoardLatch.HidesVanilla(_placed)) ApplyVanillaBlocks(page, succeeded);
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
            ResultsSaver.Save(page, succeeded, board, report, CombatSync.Generation);
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
        _grid = null; _content = null; _page = null; _succeeded = false; _placed = false; _placedSucceeded = false;
    }

    // The rectangle the wheel is judged against, dropped when the board it describes is no longer on the page: a
    // different result screen, the results screens being left, or the overlay being switched off. Deliberately not
    // part of `ResetPlacement`: the rebuild a wheel notch itself causes goes through there, and the gesture that
    // caused it must still find the board it was aimed at.
    private static void ClearHit()
    {
        _hitRect = default; _hitValid = false; _hitSucceeded = false; _hitFrame = -1;
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
        _hitSucceeded = false;
    }

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
    // the top of the success placement - so the section is measured as final on the very frame the ledger completes
    // and the two never disagree about which reading the board was placed from.
    private static bool RevealFinished { get { return Readiness.Ready; } }
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
        // A different screen has a different footer, title and completion section: every settle state, the retry
        // cadence and the recorded strip are dropped here and nowhere else.
        Footer.Forget(); Title.Forget(); FailureLayoutTitle.Forget(); Completion.Forget();
        FailureSequence.Forget(); Retry.Forget();
        SuccessNoFooter.Forget(); FailureNoFooter.Forget();
        // 客机的下界墙属于这一张页面：它的样本、它读到过的最高一堵墙都留在这一页上。
        ClientWall.Forget();
        _holdStrip = default;
        Drift.Forget();
        _placedOnFallback = false; _fallbackRecoveries = 0; _boardFloor = float.NaN;
        // The page's final geometry is a property of the page: the latch, whether the last frame could read it, how many
        // of the section's parts were still waiting to be drawn and the frame the wait started on all go with it.
        FinalLayout.Forget(); _finalMeasurable = false; _finalEverMeasurable = false; _finalUndrawn = 0; _finalFirstFrame = -1;
        // The reveal's stall clock belongs to the page too: the next result screen starts from zero progress.
        RevealClock.Forget();
        // The per-part completion diagnostic is a statement about this page's own parts: its signature and its line
        // budget go with the page, so the next screen prints its own reading.
        _completionPartSignature = string.Empty; _completionPartLines = 0;
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
        // A new result screen reports its own board: the layout line belongs to the screen, not to the
        // session. A page pointer is reused across a session's runs, so a once-per-session latch left every
        // result screen after the first without its own line and its own archive entry in the log.
        _loggedDraw = false;
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
    internal static void Invalidate() { ResetPlacement(); Retry.Forget(); }

    internal static void Draw()
    {
        // Every repaint starts from "no board to aim at": the rectangle below is only ever re-earned by a frame that
        // really draws one. Nothing is carried over from the last frame, so a frame whose board was refused - no grid
        // yet, a strip that would not hold it - leaves the wheel to the game instead of consuming it over a rectangle
        // that is not on the screen. The same-frame gesture lock survives this; see `InvalidateHitForRepaint`.
        InvalidateHitForRepaint();
        // Nothing on this page is showing a board until this repaint really draws one: this is the fact F8's next
        // press is answered from, so it is written here - before every early return below - and only the draw at
        // the end of this method puts a frame number back into it.
        _boardDrawnFrame = -1;
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
            if (_page != null) { RestoreHidden(); ResetPlacement(); }
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
            ReportHeldBack("draw", _page == null || !PageActive(_page) ? "no-live-page" : "no-placement",
                "grid=" + (_grid != null ? _grid.Width.ToString("0") + "x" + _grid.Height.ToString("0") : "none")
                + ",content=" + (_content != null) + ",boardFloor=" + _boardFloor.ToString("0")
                + ",pagePresent=" + (_page != null) + ",pageActive=" + PageActive(_page));
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
                if (!ResultBoardHold.TryPlace(Screen.width, Screen.height, new BoardStrip(top, bottom, buttonMeasured, buttonTop, buttonHeight), _grid.Width, _grid.Height,
                    Plugin.ResultsOffsetX.Value, out LayoutRect fitted, BoardSlack))
                {
                    if (ResultReportPolicy.OncePerPage(HeadingReports, "fail-no-room", Forced, _forcedOutcomePending))
                        Plugin.Logger?.LogInfo($"[DIAG] failure board held back: strip=({top:0}..{bottom:0}), have={(bottom - top):0}px cannot hold a {_grid.Width:0}x{_grid.Height:0} (need={_grid.Height:0}px) board, so the vanilla screen is left alone rather than a button covered.{(Forced ? " forcedByF8=true - this press is answered by a refusal line, never silence." : string.Empty)}");
                    // The forced press spends its answer here, exactly as ReportHeldBack does: once this refusal has
                    // printed, the next frame cannot print a second, false "board drawn" answer for the same press.
                    if (Forced) _forcedOutcomePending = false;
                    return;
                }
                _origin = fitted;
            }
        }
        // The page switches the blocks the board replaces back on as its own animation plays, so they are
        // put away again on every frame the board is up, not once per poll. This happens after every gate
        // that can still hold the board back, so a frame that draws nothing also hides nothing: the vanilla
        // blocks only ever make way for a board that is really about to be drawn. It is guarded on its own
        // so a page that has already gone away can never take the board down with it.
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
            // A fail-open board is checked against the page on every repaint too, and the answer is a move or a fainter
            // board - never a blank screen. A page whose controls moved under it keeps its table.
            RefreshFallbackSafety();
            opacity = _fallbackOpacity;
        }
        try { ApplyVanillaBlocks(_page, _succeeded); }
        catch (Exception error)
        {
            _page = null;
            if (!_loggedMeasure)
            {
                _loggedMeasure = true;
                Plugin.Logger?.LogWarning("[DIAG] the result page went away while its blocks were being put back, so the board stops touching it: " + error.Message);
            }
        }
        // A page that went away while its blocks were being put back gets nothing drawn on it: the board belongs to a live
        // result screen, and the next repaint attaches to whatever screen is up then.
        if (_page == null) return;
        // The success board keeps the position it was measured at. Re-measuring it on every frame made the
        // table resize while the page was still animating, and a table that has settled must not move: it is
        // placed once, and placed again only when the results screen itself changes.
        HudTable.Draw(_grid, _origin.X, _origin.Y, _content, opacity);
        // A board really went on the page this repaint. That is the whole of "the page is showing a board", and it is
        // what a press of F8 will hide from now on.
        _boardDrawnFrame = Time.frameCount;
        // 1.19.26: the forced press is answered *after* the draw, never before it. The line says what this build did - it
        // issued the board's draw calls on this frame - and not what the page ended up looking like: there is no pixel
        // readback anywhere in this pipeline, and a "board drawn" line printed ahead of the draw was a claim about pixels
        // nobody had painted yet.
        ReportForcedDraw(_origin);
        // The rectangle that is really on the screen now, which is what the wheel is aimed at. Written here, on the
        // draw, so it describes the board the player is looking at rather than the cache a rebuild keeps replacing.
        _hitRect = new LayoutRect(_origin.X, _origin.Y, _grid.Width, _grid.Height);
        _hitSucceeded = _succeeded;
        _hitValid = true;

        if (!_succeeded && HeadingReports.Add("failure-board-placed"))
            Plugin.Logger?.LogInfo($"[DIAG] failure board placed: board=({_origin.X:0},{_origin.Y:0},{_grid.Width:0}x{_grid.Height:0}), failureTitleEdge={(float.IsFinite(Title.Edge) ? Title.Edge.ToString("0") : float.IsFinite(FailureLayoutTitle.Edge) ? FailureLayoutTitle.Edge.ToString("0") : "unseen")}, band=({_holdStrip.Top:0}..{_holdStrip.Bottom:0}), bandSource={_holdStrip.FooterSource}; this page's native title layout and interaction lower wall were measured before drawing.");
    }

    // 客机结算页没有「返回大厅」按钮，只有一行等待状态文字；本模组的落板不依赖任何原生控件。
    //
    // The role is read once per decision and named in every branch that depends on it, so a host is never answered
    // with a client's rule (and never told about a proof it can never have). An unreadable native flag is a host: that
    // is the path that measures the page and refuses, not the one that hands out a fixed safe area.
    private static ResultPageRole Role()
    {
        try { return ResultPageRolePolicy.Of(SNet.IsMaster); } catch { return ResultPageRolePolicy.Unreadable; }
    }

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
    private static void ReportHeldBack(string where, string reason, string detail)
    {
        if (!ResultReportPolicy.OncePerPage(HeadingReports, "held-back-" + where + "-" + reason, Forced, _forcedOutcomePending)) return;
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
        var board = new LayoutRect(_origin.X, _origin.Y, _grid.Width, _grid.Height);
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
            if (!GuestClient() && !successButtonMeasured)
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
            if (!ResultBoardHold.HeldGridFits(_holdStrip, current, board, _grid.Height, Screen.height, BoardSlack))
            {
                ReportHeldBack("success-strip", "grid-would-reach-edge",
                    "boardBottom=" + (board.Y + _grid.Height).ToString("0") + ",heldStripBottom="
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
        if (!ResultBoardHold.HeldGridFits(_holdStrip, failureStrip, board, _grid.Height, Screen.height, BoardSlack))
        {
            ReportHeldBack("failure-strip", "grid-would-reach-edge",
                "boardBottom=" + (board.Y + _grid.Height).ToString("0") + ",heldStripBottom="
                + (float.IsFinite(_holdStrip.Bottom) ? _holdStrip.Bottom.ToString("0") : "unmeasured"));
            return false;
        }
        return true;
    }

    private static void DeferBoardForInteraction()
    {
        if (_page != null) RestoreHidden();
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
            Plugin.Logger?.LogInfo("[DIAG] result board deferred: the page exposed an unreadable or overlapping native/UI control after placement; vanilla is restored and the strip is measured again before the overlay may return."
                + (Forced ? " forcedByF8=true - this press is answered by this deferral line, never silence." : string.Empty));
            if (Forced) _forcedOutcomePending = false;
        }
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
                // 1.19.21: the waiting block's laid-out rectangle is the wall - active state and text content are
                // not asked of it, exactly as the strict path measures it now.
                if (waiting != null && TryScreenRect(waiting, out LayoutRect waitingRect))
                {
                    protectedText = waiting;
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
        try { controls = ScanFooterControls(page, guessTop, MessageGap, protectedText); }
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
        ResultPageWalls walls = ResultPageLowerBoundPolicy.Resolve(GuestClient() && !succeeded, Screen.height,
            pageButtonTop, controls.PressableTop, footerText, footerTextSource);
        // The failure page's own lower bound: any of the readings that really bound a board there. When none of them
        // exists the page has shown no wall yet - not "no wall" - and the invented band is withheld (see
        // `ResultFailureWallPolicy`) instead of being hung from the window's foot, which is how the recorded page drew a
        // 1385-tall board where its button (1240) later appeared.
        //
        // F5: when that budget is finally spent and a band *is* answered without any measured lower bound, the line has
        // to keep saying so (`wallUnobserved=true` below) - a successful fallback is not proof that the page has a wall.
        bool wallUnproven = false;
        int wallWait = 0;
        if (!succeeded)
        {
            bool wallMeasured = float.IsFinite(walls.PressableTop) || float.IsFinite(walls.FooterTextTop)
                || float.IsFinite(walls.PageFloor) || FailureWallObserved(pageButtonTop, controls.PressableTop, controls.FooterTop);
            wallWait = FailureWallWaitFrames(wallMeasured);
            wallUnproven = !wallMeasured;
            if (ResultFailureWallPolicy.Waits(wallMeasured, wallWait))
            {
                why = ResultFailureWallPolicy.Unobserved + ": pressable=" + WallText(walls.PressableTop)
                    + ",footerText=" + WallText(walls.FooterTextTop) + ",pageFloor=" + WallText(walls.PageFloor)
                    + ",button=" + WallText(pageButtonTop) + ",controls=" + controls.Count + ",hidden=" + controls.Hidden
                    + ",waited=" + wallWait + " of " + ResultFailureWallPolicy.UnobservedBudget + " frames"
                    + "; two complete scans that found no pressable control are not a page with no wall, so the fail-open band is not invented from the window";
                return false;
            }
        }
        // 客机失败页上那行字不进墙，也不进「量到的控件」这一路：`controls.FooterTop` 含它，`PressableTop` 不含。
        float measuredWall = walls.GuestFailure ? float.NaN : controls.FooterTop;
        if (!ResultFallbackBandPolicy.TryResolve(Screen.height, anchorBottom, anchorSource, walls, measuredWall,
                controls.BelowActions.Top, controls.BelowActions.Bottom, FallbackFloor(), out ResultFallbackPlan band))
        {
            why = "no-safe-area: anchorBottom=" + (float.IsFinite(anchorBottom) ? anchorBottom.ToString("0") : "unmeasurable")
                + ",footerTop=" + WallText(walls.PressableTop)
                + ",footerText=" + WallText(walls.FooterTextTop) + " (" + (walls.GuestFailure ? "not-a-wall" : ResultsBounds.Name(walls.FooterTextSource)) + ")"
                + ",screen=" + Screen.height + ",boardFloor=" + FallbackFloor().ToString("0");
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
            + ", footerText=" + WallText(walls.FooterTextTop) + " (" + (walls.GuestFailure ? "not-a-wall" : ResultsBounds.Name(walls.FooterTextSource)) + ")"
            + ", footerTop=" + WallText(walls.PressableTop)
            + ", pageFloor=" + WallText(walls.PageFloor)
            + ", controls=" + controls.Count + ", hidden=" + controls.Hidden + ", unmeasurable=" + controls.Unmeasurable
            + ", scanComplete=" + controls.Complete.ToString().ToLowerInvariant()
            + ResultFailureWallPolicy.AnswerNote(!wallUnproven, wallWait);
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
        if (_page == null || _grid == null) return;
        if (!FallbackPlacement(_page, _succeeded, out float top, out float room, out BoardStrip strip, out _)) return;
        if (room <= 0f) return;
        if (!ResultFallbackLayout.TryPlaceInBand(Screen.width, Screen.height, top, top + room, _grid.Width, _grid.Height,
            Plugin.ResultsOffsetX.Value, out LayoutRect rect)) return;
        // The opacity is re-derived from the same measurement the place is, every repaint - a board that fitted when it was
        // placed but is over its band now must go faint, and one that has been moved back inside a band must come back to
        // full strength. The plan's own opacity is the floor of the answer: a band that was only ever the "standing over the
        // page" tier stays faint whatever the board does inside it.
        float planned = _fallbackOpacity;
        bool overBand = float.IsFinite(strip.Bottom) && rect.Bottom > strip.Bottom + 0.5f && strip.Bottom > strip.Top;
        float opacity = ResultFallbackOpacity.Resolve(planned, rect.Bottom, strip.Top, strip.Bottom);
        bool opacityChanged = Math.Abs(opacity - _fallbackOpacity) > 0.001f;
        _fallbackOpacity = opacity;
        _holdOpacity = opacity;
        // A standing board moves when its band really intruded on the board that is on the screen - in either direction.
        // Two edges bind: the band's foot is the control the player has to press, and the band's top is the page's own anchor
        // (the completion section, the icon row, the failure title), which slides *down* when those native elements do. A
        // board that kept an older, higher top after that was drawn over the very thing that edge keeps clear - and the rule
        // this replaces could not see it, because it asked only whether the board's own bottom passed the band's foot, and
        // that foot does not move when the top does.
        //
        // The question is asked of the rectangle that is on the screen (`_origin`), never of the destination `TryPlaceInBand`
        // just computed: the destination is inside the new band by construction, so asking about it can only answer the fit's
        // own question and never "the band moved into the board".
        //
        // Two thresholds, both already in use: an intrusion has to exceed half a pixel, and the destination has to differ by
        // the drift minimum, so a band that jitters by a fraction of a pixel does not move a table the player is reading. A
        // band whose foot arrived above the board's foot has no destination to move to - the destination is the band's own
        // top, where the board already stands - and is answered by the fade below instead of by a move that changes nothing.
        float beforeTop = _origin.Y;
        bool horizontal = Math.Abs(rect.X - _origin.X) > ResultAnchorDrift.MinimumPixels;
        bool vertical = Math.Abs(rect.Y - _origin.Y) > ResultAnchorDrift.MinimumPixels;
        bool intrudes = ResultLateControlPolicy.MustMove(_origin.Y, _origin.Bottom, strip.Top, strip.Bottom);
        bool moved = horizontal || (intrudes && vertical);
        if (moved)
        {
            // The board keeps the grid it was fitted with and only its place moves; the strip recorded with the placement is
            // left alone, so the one place that really records a placement (`Fit`) stays the only one that does.
            _origin = rect; _holdBoard = rect;
        }
        if (!moved && !opacityChanged) return;
        if (HeadingReports.Add(moved ? "fallback-moved" : "fallback-fade"))
            Plugin.Logger?.LogInfo($"[DIAG] the fail-open settlement board {(moved ? $"moved to ({rect.X:0},{rect.Y:0})" : "stayed where it was")} "
                + $"because the page's own controls changed under it; band=({strip.Top:0}..{strip.Bottom:0}), "
                + $"boardTop={beforeTop:0}->{_origin.Y:0}, boardBottom={_origin.Bottom:0}, intrudes={intrudes.ToString().ToLowerInvariant()}, "
                + $"opacity={_fallbackOpacity:0.00}{(overBand ? " (over its band, drawn faint)" : string.Empty)}, role={ResultPageRolePolicy.Name(Role())}. "
                + "It is moved out of their way - down to the band's own top when that edge moved into it - and drawn fainter when "
                + "nothing clear is left, and it is never taken away. The next measurement that proves the page's own anchors places "
                + "it strictly again.");
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
        ResultBoardToggle toggle = ResultTogglePolicy.Next(BoardOnScreen);
        bool show = toggle == ResultBoardToggle.Show;
        Forced = show;
        // The switch half of the documented F8 contract: the same two switches, the same saved file, on both screens.
        // `Plugin.PersistToggle` batches them and only saves when something really moved.
        Plugin.PersistToggle(show);
        Invalidate();
        _strictMisses = 0;
        _fallbackPlaced = false;
        _fallbackOpacity = 1f;
        // A press that shows the board owes its frame a line: the flag is spent by `ReportForcedDraw` when the frame ends
        // with a board, or by the forced-gated refusal reports when it ends without one. A hide is answered by the empty
        // page itself, and a pending flag left over from an earlier show must not outlive it.
        _forcedOutcomePending = show;
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
        Plugin.Logger?.LogInfo($"[DIAG] F8 forced frame: board draw issued, {_grid.Width:0}x{_grid.Height:0}px at ({origin.X:0},{origin.Y:0}) of a {Screen.width}x{Screen.height} window, band=({_holdStrip.Top:0}..{_holdStrip.Bottom:0}), bandSource={_holdStrip.FooterSource}, forcedByF8=true; the press is answered on the repaint that issued the board's draw calls (this line reports that call, not a pixel readback of the page).");
    }
    private static bool _placed;             // a placement has been computed for the screen now up
    private static bool _placedSucceeded;    // and it was computed for this kind of results screen
    internal static bool NeedsAttach()
    {
        if (!Plugin.ShowResults.Value) return false;
        var state = GameStateManager.CurrentStateName;
        if (state != eGameStateName.ExpeditionSuccess && state != eGameStateName.ExpeditionFail) return false;
        bool succeeded = state == eGameStateName.ExpeditionSuccess;
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

    // Where the wheel scales the settlement board.
    //
    // The rectangle is the one the board was drawn at during the most recent repaint, and it is dropped again at the
    // start of the next one: a frame that could not draw the board leaves nothing to aim at, so a pointer resting
    // where the board used to be cannot keep scaling something nobody can see. `frame` is the GUI frame the notch
    // arrived in, and the gesture lock is asked first: the notch that changed the scale saves the configuration,
    // that save drops the layout, and the rectangle is gone by the time the rest of the same gesture arrives - which
    // must still be consumed here rather than reaching the page underneath. Beyond that lock, the first notch of a
    // sequence has to be inside the rectangle that is really on screen, and nothing outside it is ever claimed.
    internal static bool ZoomAt(Vector2 point, bool succeeded, int frame)
    {
        // The same four switches the board itself is gated by - with one exception that has to agree with the draw: a
        // player who pressed F8 on this page (`Forced`) has the board on screen whatever the switches say, so the wheel
        // over it must keep working. A wheel that worked only while the config agreed would be a second, quieter version
        // of the bug that left a forced board unscalable.
        if (!Forced)
        {
            if (!PanelBehaviour.ToggleState) return false;
            if (Plugin.ShowResults != null && !Plugin.ShowResults.Value) return false;
            if (Plugin.ShowResultOverlay != null && !Plugin.ShowResultOverlay.Value) return false;
            if (Plugin.ShowImpactBoard != null && !Plugin.ShowImpactBoard.Value) return false;
        }
        // The rest of a gesture that already landed on this page's board during this very frame. Asked before the
        // rectangle, because the rectangle is exactly what the rebuild the first notch started has taken away.
        if (_hitFrame == frame) return true;
        if (!_hitValid || _hitSucceeded != succeeded) return false;
        if (!float.IsFinite(point.x) || !float.IsFinite(point.y)) return false;
        if (!float.IsFinite(_hitRect.X) || !float.IsFinite(_hitRect.Y) || !(_hitRect.Width > 0f) || !(_hitRect.Height > 0f)) return false;
        if (point.x < _hitRect.X || point.x > _hitRect.X + _hitRect.Width) return false;
        if (point.y < _hitRect.Y || point.y > _hitRect.Y + _hitRect.Height) return false;
        _hitFrame = frame;
        return true;
    }

    // ---- the influence board -------------------------------------------------------------------

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
        bool strict = Placement(page, succeeded, out _, out float room, out BoardStrip strip);
        // The row count the board is built with, decided once per frame from the room the page measured. On every
        // page that can hold the split board this is the 30 rows it has always been; on a settlement page whose band
        // `SplitKillTiersFor` keeps the split board for an unmeasured room, for a room too short for either board and
        // for a room where the two rows buy no glyph pixel at all, so a page this rule cannot help keeps exactly the
        // content it had.
        float rowEm = HudTable.RowEm(ResultsFontGrowth.MinimumReadablePixels);
        bool splitTiers = ImpactBoard.SplitKillTiersFor(strict ? room : float.NaN,
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
        ResultsSaver.Save(page, succeeded, board, report, CombatSync.Generation);
        var content = new TableContent
        {
            Columns = board.Columns,
            Text = board.Text,
            Colors = Colors(board, report),
            FieldSeparator = ImpactBoard.ValueSeparator,
            Dividers = Dividers(board),
            Kinds = board.Kinds
        };
        // The strict attempt is a strict attempt: whatever the previous frame ended in, this one asks the fit to place
        // inside the strip the page itself measured.
        _fallbackPlaced = false;
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
                + ", forcedByF8=" + Forced.ToString().ToLowerInvariant()
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
        PanelGrid grid = FitBoard(content, room, boardWidth, succeeded, strip.ButtonHeight, strip.FillCeiling);
        if (grid == null)
        {
            refusal = "reason=grid-unmeasurable,rows=" + content.Rows + ",columns=" + content.ColumnCount + "," + refusal;
            return false;
        }
        if (!ResultBoardLatch.Placed(Fit(strip, grid, content, board, report, succeeded), _grid != null, _content != null))
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
    private static PanelGrid FitBoard(TableContent content, float room, float boardWidth, bool succeeded, float buttonHeight = float.NaN,
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
        int bandFloor = ResultsBandFit.FloorForBand(room, content.Rows, Plugin.Density, requested, out bool squeezed);
        if (squeezed && HeadingReports.Add("band-squeezed"))
            Plugin.Logger?.LogInfo($"[DIAG] settlement board squeezed by its band: room={room:0}px for {content.Rows} rows allows a {bandFloor}px glyph, "
                + $"below the {requested}px this page would have liked; the glyph and the row shrink together so nothing is drawn outside "
                + $"[top, bottom], and the page is not left without a table. role={ResultPageRolePolicy.Name(Role())}.");
        int fitted = ResultsFontGrowth.FitBoardIntoStrip(seed, bandFloor, int.MaxValue, room, boardWidth,
            size => NaturalSize(content, size, bandFloor), out int floor, fillCeiling);
        // The player's own wheel preference scales the size the page's own strip produced, inside the box this
        // page really offers: a bigger board is allowed the room the fit's own comfort band leaves free, and a size
        // that would reach the lobby button or leave the width is stepped back until it does not. The two result
        // pages keep their own scale, so the success board and the failure board cannot resize each other.
        int fontSize = ResultsZoom.ScaleSize(fitted, Plugin.ResultFontScale(succeeded), room, boardWidth,
            size => NaturalSize(content, size, bandFloor));
        // The floor the grid is built with may not be above the size it is drawn at, or a zoomed-out board
        // would be rebuilt back up into the room the player just asked it to leave - and it may never be above
        // the band's own answer either, which is what keeps a short band from being answered with a taller table.
        floor = Mathf.Max(bandFloor, Mathf.Min(floor, fontSize));
        if (squeezed) floor = Mathf.Min(floor, Mathf.Max(ResultsBandFit.AbsoluteMinimumPixels, fontSize));
        PanelGrid grid = BoardGrid(content, fontSize, floor, room, boardWidth);
        if (!InStrip(grid, room, boardWidth) && floor > bandFloor)
        {
            floor = bandFloor;
            grid = BoardGrid(content, fontSize, floor, room, boardWidth);
        }
        // 1.19.21 (the hard band invariant): whatever the wheel preference asked for, the grid handed to the
        // placement is stepped back until it fits the band the page really measured - the board is never placed
        // taller than its band and merely faded, which is what the recorded failure page got (boardBottom=1388,
        // opacity 0.55). A band too short for the absolute minimum glyph keeps the smallest grid and is refused by
        // the placement below, which is the honest `squeezed`/`fit-failed` answer with the vanilla page kept.
        int clampTries = 0;
        while (!InStrip(grid, room, boardWidth)
            && fontSize > Math.Max(bandFloor, ResultsBandFit.AbsoluteMinimumPixels) && clampTries++ < 64)
        {
            fontSize--;
            floor = bandFloor;
            grid = BoardGrid(content, fontSize, floor, room, boardWidth);
        }
        // The floor the grid on screen was really built with, so a later refresh re-measures with that one
        // instead of asking for a floor this strip already proved it cannot honour.
        _fontFloor = floor;
        return grid;
    }

    // The grid the board is drawn with. The page's width is a bound the table may not exceed, never a width
    // the table is stretched to: `HudTable.BuildGrid` measures the cells and gives every value column the
    // same width, and nothing afterwards widens the grid to fill the page it is drawn on.
    private static PanelGrid BoardGrid(TableContent content, int fontSize, int floor, float room, float boardWidth)
        => HudTable.BuildGrid(content, fontSize, Plugin.Density, boardWidth, room, floor);

    // A board the placement will accept: no taller than the strip between the completion section and the
    // lobby button, and no wider than the page. The half pixel is the rounding a measured width or height
    // can leave behind; a grid a fraction over is still a board the result page refuses to draw.
    private static bool InStrip(PanelGrid grid, float room, float boardWidth)
        => grid != null && grid.Height <= room + 0.5f && grid.Width <= boardWidth + 0.5f;

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
        ResultsSaver.Save(page, succeeded, board, report, CombatSync.Generation);
        if (board.Rows != _grid.Rows || board.ColumnCount != _grid.Columns) return false;
        var content = new TableContent
        {
            Columns = board.Columns,
            Text = board.Text,
            Colors = Colors(board, report),
            FieldSeparator = ImpactBoard.ValueSeparator,
            Dividers = Dividers(board),
            Kinds = board.Kinds
        };
        float boardWidth = Mathf.Max(320f, Screen.width - 48f);
        PanelGrid measured = HudTable.BuildGrid(content, Mathf.Max(1, Mathf.RoundToInt(_grid.FontPixels)), Plugin.Density, boardWidth, _grid.Height, _fontFloor);
        if (measured != null && !GridCovers(_grid, measured)
            && Placement(page, succeeded, out _, out float room, out BoardStrip strip) && room > 0f)
        {
            // The strip may have grown since the board arrived, so the same fit the first placement used is run
            // again rather than reusing the glyph size it happened to land on: a refreshed snapshot re-measures.
            PanelGrid resized = FitBoard(content, room, boardWidth, succeeded, strip.ButtonHeight, strip.FillCeiling);
            if (resized != null)
            {
                bool fitted = Fit(strip, resized, content, board, report, succeeded);
                // A rejected larger grid keeps the existing grid on screen; its cells and geometry must stay paired.
                if (!fitted || !ReferenceEquals(_grid, resized)) return true;
            }
        }
        _content = content;
        return true;
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
            input.PrimaryName = Name(primary);
            input.SecondaryName = Name(secondary);
            input.MeleeName = Name(melee);
            inputs.Add(input);
        }
        double firepower = Plugin.SupportRoleFirepowerBonus != null ? Plugin.SupportRoleFirepowerBonus.Value : 1.0;
        double utility = Plugin.SupportRoleUtilityBonus != null ? Plugin.SupportRoleUtilityBonus.Value : 1.0;
        // A failed expedition neither shows nor scores the last-one-standing metric, so the two halves
        // are decided together here rather than by hiding a figure the score still counted.
        return ImpactScoring.Evaluate(inputs, Plugin.ScoringWeights(), firepower, utility, countLoneSurvivor);
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
    private static bool Fit(BoardStrip strip, PanelGrid grid, TableContent content, ImpactBoardLayout board, ImpactReport report, bool succeeded)
    {
        // The board is placed inside the strip its own measurement produced, not merely inside the window: a
        // board taller than that strip used to be accepted here and then refused at draw time, which is how a
        // whole table was dropped for a fraction of a pixel of overflow.
        bool placedStrictly = ResultBoardHold.TryPlace(Screen.width, Screen.height, strip, grid.Width, grid.Height,
            Plugin.ResultsOffsetX.Value, out LayoutRect origin, BoardSlack, bottomAligned: false);
        // 1.19.21 (the hard band invariant): a board is never placed taller than its band and merely faded. The grid
        // handed in here is already fitted to the strip's own room (`FitBoard` steps the glyph down until it fits), so
        // the strip's top-aligned placement is the whole answer for both the strict and the fail-open board; the band
        // the fail-open path was given is the band the fit slid the grid into. `ResultFallbackLayout.TryPlaceInBand`
        // stands in only for the fraction-of-a-pixel case the strip's own probe refused, and only when the grid really
        // does not exceed the band - a taller-than-band board is refused and reported, never drawn faint.
        if (!placedStrictly && _fallbackPlaced && grid.Height <= strip.Bottom - strip.Top + 0.5f)
            placedStrictly = ResultFallbackLayout.TryPlaceInBand(Screen.width, Screen.height, strip.Top, strip.Bottom,
                grid.Width, grid.Height, Plugin.ResultsOffsetX.Value, out origin);
        if (placedStrictly)
        {
            _origin = origin;
            _content = content; _grid = grid;
            // The place the board was measured into, kept for as long as this page is up: the F8 write, the hot
            // reload that follows it and the page's own button animation all come back through here, and none of
            // them may cost the board the strip it was already given.
            _holdValid = true; _holdBoard = origin; _holdOpacity = _fallbackPlaced ? _fallbackOpacity : 0f;
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
                Plugin.Logger.LogInfo($"[DIAG] influence board {board.ColumnCount} columns x {board.Rows} rows ({report.Entries.Count} players), {grid.Width:0}x{grid.Height:0} px at ({x:0},{y:0}) of a {Screen.width}x{Screen.height} window, {(succeeded ? "success" : "failure")} screen, anchorSource={(succeeded ? "completion-section" : "failure-strip")}, blankAbove={blankAbove:0}px, blankBelow={blankBelow:0}px, band=({strip.Top:0}..{strip.Bottom:0}), bandSource={strip.FooterSource}, glyph {grid.FontPixels:0}px filling {grid.Height * 100f / stripHeight:0}% of the {stripHeight:0}px strip between the completion section and the lobby button ({grid.Width * 100f / Math.Max(1f, Screen.width - 48f):0}% of the {Screen.width - 48}x width it was fitted to); drawn by IMGUI, no vanilla report card was modified.");
            }
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
            if (!ResultBoardHold.HeldGridFits(_holdStrip, strip, _holdBoard, grid.Height, Screen.height, BoardSlack))
            {
                if (HeadingReports.Add("board-growth-refused"))
                    Plugin.Logger?.LogInfo($"[DIAG] result board kept at its measured size: a refreshed {grid.Width:0}x{grid.Height:0} board would cross the wall the held grid has to stay above (wall={(float.IsFinite(strip.ButtonTop) ? strip.ButtonTop.ToString("0") : "unmeasurable")}, source={DescribeBand(strip.FooterSource)}) or run past the strip it was measured into ({_holdStrip.Top:0}..{_holdStrip.Bottom:0}), so the grid already on the page is kept and the next snapshot asks again.");
                return true;
            }
            _origin = _holdBoard;
            _content = content; _grid = grid;
            if (HeadingReports.Add("board-held"))
                Plugin.Logger?.LogInfo($"[DIAG] result board kept: the strip measured {strip.Top:0}..{strip.Bottom:0} cannot hold the {grid.Width:0}x{grid.Height:0} board that is already up, so it keeps the place it was measured into at ({_holdBoard.X:0},{_holdBoard.Y:0}), and only the lobby button is read from here on.");
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
        if (ResultRevealGate.BlocksPlacement(revealDone, CompletionTargets.Ledger.MayStillComplete,
                CompletionTargets.Ledger.InFlight, Forced))
        {
            if (HeadingReports.Add("success-reveal-pending"))
                Plugin.Logger?.LogInfo("[DIAG] success board held back: "
                    + ResultRevealGate.Why(CompletionTargets.Ledger.Completed, CompletionTargets.Ledger.Count,
                        CompletionTargets.Ledger.Names, CompletionTargets.Ledger.Artwork, CompletionTargets.Ledger.InFlight)
                    + ", completion=" + WallText(Completion.Edge)
                    + ", boardTop=" + (area.Fits ? area.Top.ToString("0") : "unmeasurable")
                    + ", role=" + ResultPageRolePolicy.Name(Role())
                    + ", forcedByF8=" + Forced.ToString().ToLowerInvariant()
                    + "; vanilla is retained, and the section is measured again on the frame the ledger completes.");
            return false;
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
                + ", forcedByF8=" + Forced.ToString().ToLowerInvariant()
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
            if (!successNoFooterReady && GuestClient())
            {
                // Guest no-footer readiness and fixed geometry are unchanged; a client never uses the host band proof.
                if (Footer.Exhausted && HeadingReports.Add("success-footer-unsettled"))
                    Plugin.Logger?.LogInfo($"[DIAG] success board held back: role={ResultPageRolePolicy.Name(Role())}, no stable native footer edge was measured; "
                        + $"noFooterProof=available (ready={SuccessNoFooter.Ready}, samples={SuccessNoFooter.Samples}), "
                        + $"count={successControls.Count},unmeasurable={successControls.Unmeasurable},complete={successControls.Complete},nativeVisible={successNativeFooterVisible},footerTop={(float.IsFinite(successControls.FooterTop) ? successControls.FooterTop.ToString("0") : "unmeasurable")} "
                        + $"need={_boardFloor:0}px; vanilla is kept and the page is measured again at the bounded cadence.");
                return false;
            }
            if (!successNoFooterReady)
            {
                SuccessBandProofState successBandProof = ResultSuccessBandProofPolicy.Decide(Role(),
                    successControls.Complete, successControls.ParentCoverageComplete, successControls.Count,
                    successControls.Unmeasurable, successNativeFooterVisible, measuredButton,
                    successControls.OverlapsBoard, successBand);
                if (successBandProof != SuccessBandProofState.Proven)
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

    internal static void BeginSuccessPage(CM_PageExpeditionSuccess page)
    {
        RestoreHidden();
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
        ResetPlacement();
        ForgetPage();
        HeadingReports.Clear();
        Fail = page;
        FailureSequence.BeginPage(page != null ? page.Pointer : IntPtr.Zero);
        _holdPage = page != null ? page.Pointer : IntPtr.Zero;
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
        if (ready) Plugin.Logger?.LogInfo("[DIAG] success board released after the game's sector BlinkIn routines completed; no timeout or alpha threshold bypass.");
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
            controls = ScanFooterControls(page, completion, MessageGap, null, board, inspectBoard);
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
        if (float.IsFinite(iconRow)) _iconRowBottom = iconRow;
        float waitingText = FooterTextTop(success, out ResultBoundSource waitingTextSource);
        float bandAnchor = float.IsFinite(iconRow) ? iconRow : _iconRowBottom;
        ResultBoundSource bandAnchorSource = float.IsFinite(iconRow) ? ResultBoundSource.IconRow
            : float.IsFinite(_iconRowBottom) ? ResultBoundSource.LastTrustedIconRow
            : ResultBoundSource.ScreenSafeArea;
        if (ResultsBounds.TryResolve(Screen.height, bandAnchor, bandAnchorSource,
                buttonMeasured ? buttonTop : float.NaN, waitingText, waitingTextSource, controls.FooterTop, out ResultsBand band))
        {
            pageBand = band;
            ReportBand("success", band, iconRow, waitingText, _boardFloor);
            if (band.Fits && footerSource != ResultFooterSource.BelowActions)
            {
                // The band is the whole answer when the page handed nothing else, and it tightens the page's own strip
                // otherwise: never above the icon row, never below the footer the player is reading.
                float bandBottom = area.Fits ? Math.Min(band.Bottom, area.Bottom) : band.Bottom;
                if (!(bandBottom > band.Top)) bandBottom = band.Bottom;
                area = new ResultsSafeArea(band.Top, bandBottom);
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
        if (HeadingReports.Add("success-region"))
            Plugin.Logger?.LogInfo($"[DIAG] success region: anchorSource={completionSource}, completion={(completionMeasured ? completion.ToString("0") : "unmeasurable")} (deepest part of the completion section's final layout - the pixels a part draws, or its own laid-out rectangle while it has not drawn yet (laidOutParts={_finalUndrawn}), drawnEdge={(float.IsFinite(drawnEdge) ? drawnEdge.ToString("0") : "none")}, gap={MessageGap:0}px), "
                + $"{DescribeLobbyWall(wall)}, lobbyEdge={DescribeLobbyEdge(wall)}, "
                + (area.Fits ? $"boardTop={area.Top:0}, room={area.Height:0}" : "boardTop=unmeasurable, room=unmeasurable")
                + $" of a {Screen.height} window, "
                + $"boardBand={DescribeBand(_holdValid ? _holdStrip.FooterSource : footerSource)}, boardFloor={(_boardFloor):0}, role={ResultPageRolePolicy.Name(Role())}; "
                + "the strip is bounded by this page's own final-layout geometry - the completion section's laid-out parts above and, below, the lobby button's upper edge or the free band under the page's measured actions - "
                + "never by a hidden, transparent or off-page control, and never by how much of the section has drawn. All four players scale into whichever band is used, "
                + "and the fit that decides whether the table is drawn into it reports its own line when it is not.");
        ReportDowngradedControls("success", controls, area.Top, area.Height, footerSource);
        // What the caller may act on is whether the *section* was measured: the strip's own fit is asked separately
        // through `ResultsUpperBound.Decide`, so a section that could not be read and a strip that cannot hold the
        // table stay two different answers instead of one false.
        return completionMeasured;
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
        // 1.19.37: what each of the strip's two edges was really produced by, so the page's own band can be named as
        // the proof it is (`ResultFailureBandProofPolicy`). The upper edge is always this page's mission-failed
        // title's own layout rectangle - `anchorSettled` above refuses the page without it - and the lower edge is
        // the window's foot until something below the board was measured.
        ResultBoundSource bandTopSource = ResultBoundSource.TitleAnchor, bandBottomSource = ResultBoundSource.ScreenFoot;
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

            // 等待状态文字块：照旧测量一次，写进 `waitingText=` 日志字段。
            // 1.19.21: the waiting block's own layout is read as soon as the object exists - active state and text
            // content are not asked of it, and `TryScreenRect` asks neither. 1.19.38: on a client it is a *fact*
            // about the page and not a wall - the band below never takes its foot from this figure.
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
                // 上界 = 锚点（任务失败标题）实测底边 + 玩家配置的纵向偏移，再按 1080p 等比缩放补一个 gap；
                // 下界 = 参考分辨率等比缩放的固定页面下界。
                //
                // 1.19.38：下界不再被原生等待文字二次夹紧。那行字只作为 `waitingText=` 被量一次，不是墙；
                // 真正可按的控件（重开检查点 / 回大厅按钮）仍然是墙，收紧条件与旧实现一致（见
                // `ResultClientFailureBand`）。
                //
                // 1.19.39：墙先稳定，再落板。记录下来的客机失败页第一帧还没有任何可按控件，条带整段是固定页面
                // 几何（381..1429，1049px），一块 1160x990 的板被画上去；下一帧扫描量到可按控件 1176，条带收紧
                // 到 381..1179（798px），站着的板真的压在那个控件上，于是整块板被撤掉——玩家看到的就是「先画
                // 一块大的，再消失」。这一帧的判定现在由 `ResultClientFailureStrip` 完整给出（固定几何 → 墙稳定门
                // → 条带），落板用的墙永远是本页量到过的最高那一堵：第一帧就把最终几何定下来，之后既不重排也
                // 不撤板。落板的另一个前提由 `ResultClientFailureStrip` 自己带：扫描必须真的看见这一页
                // （`controls.Complete` + `ParentCoverageComplete`）——客机在生产里没有外层的完整扫描闸门，
                // 而 inactive 控件被扫描记为 hidden，不会让扫描变成「不完整」。
                // 已经站在页面上的板（`inspectBoard`）不过稳定门：一堵还没读到两次的墙不能把板撤掉。
                float configuredOffset = Plugin.ResultsOffsetY.Value;
                if (!float.IsFinite(configuredOffset) || configuredOffset < 0f) configuredOffset = 0f;
                float guestAnchor = titleBottom + configuredOffset;
                // 只有真的可按的控件才是收紧用的墙：`controls.PressableTop` 已经排除了扫描量到的等待文字，
                // 命名按钮（重开检查点 / 回大厅）仍然按自己的布局矩形加进来。
                float pressableTop = ResultsRegion.HighestTop(namedTop, controls.PressableTop);
                // 1.19.42（独立评审 F3）：这一页量到可按控件了吗？这是这条门**唯一**读的逐帧事实
                // （`ResultClientPageActionPolicy` 现在只收 `pressableTop`）：量到就由那堵墙自己回答，量不到就是「这一页的墙
                // 还没出现」而不是「这一页没有墙」。本页动作的 active 状态曾经是入参，但在客机上「回大厅」永久 inactive、
                // `HiddenPageActions` 常 ≥1，那一条恒真、整体等价于 `!IsFinite(pressableTop)`——所以它只进下面那一行日志。
                // 记录下来的那一页头两帧就是这个形状：两次一致的「空扫描」让条带按 381..1429 定了一块 990px 的板，下一帧
                // 控件出现就被撤掉。这一项只作用于首次落位（`Measure` 不读它）。
                bool pageActionsPending = ResultClientPageActionPolicy.Pending(pressableTop);
                if (pageActionsPending && ResultFallbackPolicy.ShouldFallback(_strictMisses, Forced)
                    && HeadingReports.Add("failure-client-page-actions-pending-to-fail-open"))
                    Plugin.Logger?.LogWarning($"[DIAG] failure board: this client page has stayed pending until the fail-open budget "
                        + $"({ResultFallbackPolicy.MissBudget} refusals, misses={_strictMisses}, forcedByF8={Forced}); hidden={controls.Hidden},"
                        + $"hiddenPageActions={controls.HiddenPageActions},pressable={(float.IsFinite(pressableTop) ? pressableTop.ToString("0") : "unmeasurable")},"
                        + $"scanComplete={controls.Complete.ToString().ToLowerInvariant()},parentCoverage={controls.ParentCoverageComplete.ToString().ToLowerInvariant()},"
                        + $"unmeasurable={controls.Unmeasurable}; no frame of this page measured a pressable control, so nothing here proves the wall - "
                        + $"the answer is the fail-open board (every measured control stepped around), never a settled page.");
                var clientEvidence = new ResultClientFailureEvidence(guestAnchor, Screen.width, Screen.height,
                    MessageGap, MinimumGuestStripHeight, waitingTextTop, pressableTop,
                    controls.Complete, controls.ParentCoverageComplete, controls.Unmeasurable, pageActionsPending);
                ResultClientBandReading clientBand;
                ResultsSafeArea guestArea;
                ResultClientStripState clientState = inspectBoard
                    ? ResultClientFailureStrip.Measure(Time.frameCount, ClientWall, clientEvidence, out clientBand, out guestArea)
                    : ResultClientFailureStrip.Decide(Time.frameCount, ClientWall, clientEvidence, out clientBand, out guestArea);
                if (clientState == ResultClientStripState.WallNotSettled)
                {
                    // 不是超时，也不是有界放行：这一帧的墙问题没有被回答（读法变了 / 页面还在亮控件 / 有 active
                    // 控件量不到矩形），所以这一帧不是可以落板的那一帧。放行的唯一条件是「同一堵墙、两帧、都可
                    // 回答」；等不到就交给既有的 60 帧拒收预算与 fail-open 板，那一条从不声称这一页被证明过。
                    // 这一行走 `HeadingReports`，每页一次：一次诊断，不刷屏。
                    if (HeadingReports.Add("failure-client-wall-not-settled"))
                        Plugin.Logger?.LogInfo($"[DIAG] failure board held back: this client page's lower wall has not been answered the same way on two complete scans yet (wallSoFar={(float.IsFinite(ClientWall.Wall) ? ClientWall.Wall.ToString("0") : "unmeasurable")}, thisFrame={(float.IsFinite(pressableTop) ? pressableTop.ToString("0") : "unmeasurable")}, samples={ClientWall.Samples} seen, state={ResultClientFailureStrip.Name(clientState)}, scanComplete={controls.Complete.ToString().ToLowerInvariant()}, parentCoverage={controls.ParentCoverageComplete.ToString().ToLowerInvariant()}, hidden={controls.Hidden},hiddenPageActions={controls.HiddenPageActions},unmeasurable={controls.Unmeasurable},pageActionsPending={pageActionsPending.ToString().ToLowerInvariant()}); no frame count authorises this placement, so the board is not fitted against a wall the page is still revealing and is never withdrawn a frame later. pageActionsPending=true means this page has not measured a single pressable control yet, so this frame is not a page without a wall - hidden/hiddenPageActions are printed for the record only (the go-to-lobby button is permanently inactive on a client). If it never settles the existing refusal budget answers with the fail-open board, which steps around every measured control instead of claiming the page was proven.");
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
                    if (HeadingReports.Add("failure-guest-band-unusable"))
                        Plugin.Logger?.LogInfo($"[DIAG] failure board held back: the client page's fixed band cannot hold the minimum board this frame (fixedPage=({guestArea.Top:0}..{guestArea.Bottom:0}), waitingText={(float.IsFinite(waitingTextTop) ? waitingTextTop.ToString("0") : "unmeasurable")}, pressableTop={(float.IsFinite(pressableTop) ? pressableTop.ToString("0") : "unmeasurable")}, wall={(float.IsFinite(ClientWall.Wall) ? ClientWall.Wall.ToString("0") : "unmeasurable")}) on a {Screen.height} window; vanilla is retained and the page is measured again.");
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
                bandBottomSource = clientBand.PressableTightened ? ResultBoundSource.FooterControl : ResultBoundSource.ScreenFoot;
                // 下界来自一条真的可按控件时，那一版条带就是它自己的承诺（与主机同一条 ceiling 规则）；来自页面
                // 固定几何时脚下什么都没有量过，于是保留舒适带宽 —— 这正是 `ResultsFontGrowth.FootIsTheWall` 的回答。
                footIsWall = clientBand.PressableTightened;
                // 客机的「页脚」只可能是真正可按的控件：等待状态文字不算页脚。量不到就是没有页脚可量，
                // 而不是「页脚在 1228」——后者正是把条带压回 370px 的那次读数。这里报的是本页见过的最高那堵墙，
                // 也就是条带自己用的那一堵：条带与它的下限永远说同一个数。
                float clientWall = ClientWall.Wall;
                buttonTop = clientWall;
                buttonMeasured = float.IsFinite(clientWall);
                buttonHeight = restartInStrip && Math.Abs(namedTop - restartTop) <= 0.5f ? restartRect.Height
                    : goInStrip && Math.Abs(namedTop - goTop) <= 0.5f ? goRect.Height : float.NaN;
                if (HeadingReports.Add("failure-client-fixed-geometry"))
                    Plugin.Logger?.LogInfo($"[DIAG] failure footerSource=client-fixed-page-geometry: native lobbyVisible={nativeFooterVisible.ToString().ToLowerInvariant()}, anchor=settled-mission-title, nativeFooterMeasured={buttonMeasured.ToString().ToLowerInvariant()} (logged only), waitingText={(float.IsFinite(waitingTextTop) ? waitingTextTop.ToString("0") : "unmeasurable")},waitingTextCovered={clientBand.WaitingTextCovered.ToString().ToLowerInvariant()}, wallSettled={ClientWall.Settled.ToString().ToLowerInvariant()},wallSamples={ClientWall.Samples}, hidden={controls.Hidden},hiddenPageActions={controls.HiddenPageActions},pageActionsPending={pageActionsPending.ToString().ToLowerInvariant()}, strip=({top:0}..{bottom:0}) of a {Screen.height} window; the waiting-status line is measured and logged only and never bounds this strip, the interaction scan is consulted only for controls the player can press, and no footer button is required for release.");
            }
            else
            {
                // 主机：与 1.19.14 完全一致。
                top = Math.Max(ResultsPlacement.Top(titleBottom, Plugin.ResultsOffsetY.Value, MessageGap), controls.BoardTop);
                if (buttonMeasured)
                {
                    bottom = Math.Min(buttonTop - MessageGap, Screen.height - 8f);
                    bandBottomSource = ResultBoundSource.FooterControl;
                }
                else bottom = Screen.height - 8f;
            }
            // ---- the agreed band, on the failure page too ----------------------------------------------------
            //
            // Upper: the page's own mission-failed title layout edge. Lower: the button the host presses.
            //
            // 1.19.38: 客机的条带不再经过 `ResultsBounds`。客机的下界就是页面固定几何的下界（本机实测 1429），
            // 原生「正在等待厅主」文字块（实测 1228）不是墙：它照旧被测量、照旧写进 `waitingText=` 字段，
            // 但不参与下界的决定。旧版这里把 waitingTextTop 传给 `ResultsBounds.TryResolve`，于是 1429 被二次
            // 夹紧成 1207（370px，30 行只够 8px 字号），等待文字跨帧下移 30px 还会再触发一次「result board
            // re-measured」。主机这一行都没有变：它的下界仍然取「按钮 / 量到的控件 / 屏幕脚」里最高的那堵墙。
            if (GuestClient())
            {
                // 客机：条带的两个边界已经由上面那个固定几何分支决定，这里只把这一页的 band 报一次。
                ReportBand("failure", new ResultsBand(top, bottom, bandTopSource, bandBottomSource, false),
                    float.NaN, waitingTextTop, _boardFloor);
            }
            else if (ResultsBounds.TryResolve(Screen.height, titleBottom, ResultBoundSource.TitleAnchor,
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
            float blankAbove = Math.Max(0f, candidateBoard.Y - titleBottom);
            float blankBelow = Math.Max(0f, (float.IsFinite(buttonTop) ? buttonTop : candidateBoard.Bottom) - candidateBoard.Bottom);
            if (HeadingReports.Add("failure-strip"))
                Plugin.Logger?.LogInfo($"[DIAG] failure strip: anchorSource={titleSource},messageBottom={titleBottom:0},edgeFrom={(drawnRoute ? "drawn-text" : "layout")},titleAlpha={(float.IsFinite(alpha) ? alpha.ToString("0.00") : "unreadable")},opaqueReadings={Title.OpaqueFrames},sequenceFinished={sequenceFinished.ToString().ToLowerInvariant()},boardTop=({candidateBoard.Y:0}..{candidateBoard.Bottom:0}),blankAbove={blankAbove:0}px,blankBelow={blankBelow:0}px,footerTop={(float.IsFinite(buttonTop) ? buttonTop.ToString("0") : "unmeasurable")},waitingStatusProtected={protectWaitingStatus.ToString().ToLowerInvariant()},scanComplete={controls.Complete.ToString().ToLowerInvariant()},parentCoverage={controls.ParentCoverageComplete.ToString().ToLowerInvariant()},unmeasurable={controls.Unmeasurable},boardOverlapsControl={overlapsLobby.ToString().ToLowerInvariant()},screen={Screen.width}x{Screen.height}.");
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
        Component protectedPageText = null, LayoutRect board = default, bool inspectBoard = false)
    {
        if (page == null) return new FooterControls(float.NaN, float.NaN, 0, 0, false, false, false, 0, 0, "page-missing");
        var actions = new List<ResultActionBounds>();
        var seen = new HashSet<IntPtr>();
        var parentSeen = new HashSet<IntPtr>();
        // Every control the scan sees is added to this tally, and the three answers it can give are kept apart on
        // purpose: one for the control the page switched on, one for the control it switched off, and one for the
        // control that is on but whose rectangle this frame could not be read.
        var tally = new ResultInteractionTally(0, 0, 0, 0);
        string reason = string.Empty;
        bool complete = true, overlaps = false, client = false, parentCoverageComplete = true;
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
            if (inspectBoard && ResultInteractionStripPolicy.OverlapsBoard(rect, board, ResultBoardHold.ButtonClearance))
                overlaps = true;
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
                    // 1.19.38: 客机上这一行等待状态文字不是可交互控件，也不再是条带的下界——固定页面几何才是。
                    // 它因此不能触发 `OverlapsBoard` 的遮挡拒绝：那条拒绝会走 `DeferBoardForInteraction`，
                    // 把板子整体撤下来，于是客机页面上完全没有板子。主机上它仍然是页脚文字，遮挡拒绝保持原样。
                    // 真正可按的控件走上面的 `AddControl`，那一路没有任何角色判断。
                    if (inspectBoard && !client && ResultInteractionStripPolicy.OverlapsBoard(rect, board, ResultBoardHold.ButtonClearance))
                        overlaps = true;
                }
            }
            catch { complete = false; if (reason.Length == 0) reason = "protected-text-unreadable"; }
        }

        // A control that is on but had no rectangle is a real interaction the collision set could not place, which
        // is a verdict about this one frame rather than about the page. The scan therefore no longer refuses the
        // page for it: the strip is resolved from the rectangles that could be read, and `BoardStillSafe` asks
        // again on the next repaint.
        if (!ResultInteractionStripPolicy.TryResolve(anchorBottom, gap, actions, out ResultInteractionStrip strip))
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
        return new FooterControls(strip.FooterTop, strip.BoardTop, tally.Active, tally.Hidden + tally.Active, complete, parentCoverageComplete, overlaps,
            tally.Hidden, tally.HiddenPageActions, reason, tally.Unmeasurable, belowActions, strip.PressableTop);
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
                if (!float.IsFinite(screen.x) || !float.IsFinite(screen.y)) return false;
                xs.Add(screen.x);
                ys.Add(Screen.height - screen.y);
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
                if (!float.IsFinite(screen.x) || !float.IsFinite(screen.y)) return false;
                xs.Add(screen.x);
                ys.Add(Screen.height - screen.y);
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
    private static void ApplyVanillaBlocks(Component page, bool succeeded)
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

        if (succeeded)
        {
            var success = page.TryCast<CM_PageExpeditionSuccess>();
            if (success == null) return;
            if (successBlocks)
            {
                Hide(success.m_ArtifactInventoryDisplay, "artifact inventory");
                Hide(success.m_artifactInfo_text, "artifact summary");
                Hide(success.m_checkpointUsage, "checkpoint usage");
            }
            if (prisonerReports)
            {
                var reports = success.m_playerReports;
                int count = reports == null ? 0 : reports.Length;
                for (int index = 0; index < count; index++) Hide(reports[index], "prisoner report " + index);
                var anchors = success.m_playerReportAligns;
                int anchors_ = anchors == null ? 0 : anchors.Length;
                for (int index = 0; index < anchors_; index++) Hide(anchors[index], "prisoner report anchor " + index);
            }
        }
        else
        {
            var fail = page.TryCast<CM_PageExpeditionFail>();
            if (fail == null) return;
            Hide(fail.m_ArtifactInventoryDisplay, "artifact inventory");
            Hide(fail.m_artifactInfo_text, "artifact summary");
        }
    }

    // How far off the page a hidden block is put: past any resolution, still a valid position.
    private const float AwayY = 6000f;

    // Everything this mod moved goes back, exactly. An entry is written the first time a block is touched
    // and never rewritten, so the recorded position is the page's own and not this mod's own displacement.
    private static void RestoreHidden()
    {
        foreach (KeyValuePair<IntPtr, HiddenBlock> entry in Hidden)
        {
            HiddenBlock block = entry.Value;
            try
            {
                if (block.Target != null) block.Target.localPosition = block.Position;
                if (block.Host != null) block.Host.SetActive(block.Active);
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("[DIAG] a vanilla block could not be put back where it was: " + error.Message);
            }
        }
        Hidden.Clear();
        _appliedSuccessBlocks = _appliedPrisonerReports = _appliedFailBlocks = _appliedAny = false;
    }

    private static void Hide(Component block, string what)
    {
        if (block == null) return;
        try
        {
            var target = block.transform;
            var host = block.gameObject;
            IntPtr key = block.Pointer;
            if (!Hidden.TryGetValue(key, out HiddenBlock record))
            {
                // First touch: this is the page's own position and state, recorded before anything moves.
                record = new HiddenBlock
                {
                    Target = target, Host = host,
                    Position = target != null ? target.localPosition : default,
                    Active = host != null && host.activeSelf
                };
                Hidden[key] = record;
            }
            if (target != null) target.localPosition = new Vector3(record.Position.x, AwayY, record.Position.z);
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
