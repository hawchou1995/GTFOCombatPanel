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
        public bool ProvenEmpty => ResultInteractionScanPolicy.ProvenEmpty(Complete, ParentCoverageComplete, Tally);
        public FooterControls(float footerTop, float boardTop, int count, int scannedCount, bool complete,
            bool parentCoverageComplete, bool overlapsBoard, int hidden = 0, int hiddenPageActions = 0, string reason = "",
            int unmeasurable = 0, ResultBoardBand belowActions = default)
        {
            FooterTop = footerTop; BoardTop = boardTop; Count = count; ScannedCount = scannedCount;
            Complete = complete; ParentCoverageComplete = parentCoverageComplete; OverlapsBoard = overlapsBoard;
            Hidden = hidden; HiddenPageActions = hiddenPageActions; Reason = reason ?? string.Empty;
            Unmeasurable = unmeasurable;
            Tally = new ResultInteractionTally(count, hidden, hiddenPageActions, unmeasurable);
            BelowActions = belowActions;
        }
    }

    private static readonly ResultNoFooterGate SuccessNoFooter = new ResultNoFooterGate();
    private static readonly ResultNoFooterGate FailureNoFooter = new ResultNoFooterGate();

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
                + ",successPage=" + (Success != null) + ",failurePage=" + (Fail != null));
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
    // answers "settled" only for an edge it really read at the same value twice; a button the page never hands over
    // stays unanswered, and the vanilla page is kept rather than a board drawn from a fraction of the window. The
    // failure page's own title is followed by `Title` (full opacity *and* a still edge) and the success page's
    // completion section by `Completion`.
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
    private static readonly CompletionReadiness Readiness = new CompletionReadiness();
    // The height below which no strip can hold this page's board, whatever glyph size is tried: the settlement board's
    // own row count at the smallest readable glyph the table may be drawn at. It is what tells a strip that is merely
    // tight from one that can never hold the board, and it is page independent, so the page can be judged against it
    // before a single size has been searched for. Set for every page visit in `Prepare` and dropped with the page.
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
    internal static bool Forced;
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
        _holdStrip = default;
        Drift.Forget();
        _placedOnFallback = false; _fallbackRecoveries = 0; _boardFloor = float.NaN;
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
    private static float IconRowBottom(Component page)
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
            return CompletionBounds.Deepest(parts, out _);
        }
        catch { return float.NaN; }
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
            ReportHeldBack("draw", _page == null || !PageActive(_page) ? "no-live-page" : "no-placement",
                "grid=" + (_grid != null ? _grid.Width.ToString("0") + "x" + _grid.Height.ToString("0") : "none")
                + ",content=" + (_content != null) + ",boardFloor=" + _boardFloor.ToString("0"));
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
                // A fail-open board's upper edge is the band's top and is as binding as the footer underneath it: the lift
                // below may never carry it above that edge. A board that then still reaches the button is drawn fainter
                // instead - the same trade the fit makes when the board is taller than its band.
                float topBound = _fallbackPlaced ? _holdStrip.Top : float.NaN;
                if (!ResultBoardHold.TryHold(new BoardStrip(_holdBoard.Y, _holdBoard.Bottom, buttonMeasured, buttonTop),
                        _holdBoard, Screen.height, out LayoutRect held, topBound))
                {
                    ReportHeldBack("failure-hold", "held-board-unreadable",
                        "buttonMeasured=" + buttonMeasured + ",buttonTop=" + (float.IsFinite(buttonTop) ? buttonTop.ToString("0") : "unmeasurable"));
                    return;
                }
                if (_fallbackPlaced && buttonMeasured && float.IsFinite(buttonTop)
                    && held.Bottom > buttonTop - ResultBoardHold.ButtonClearance + 0.5f)
                    _holdOpacity = Math.Min(_holdOpacity, ResultFallbackLayout.OverlapOpacity);
                opacity = _holdOpacity;
            }
            else
            {
                // A first placement still has to fit the strip the page measured: below the page's own message
                // and above the buttons the player has to press. A board that cannot fit is not drawn rather
                // than drawn over a button.
                if (!FailureStrip(_page, out float top, out float bottom, out opacity, out bool buttonMeasured, out float buttonTop, out float buttonHeight, out _, out _, out _, out _)) return;
                if (!ResultBoardHold.TryPlace(Screen.width, Screen.height, new BoardStrip(top, bottom, buttonMeasured, buttonTop, buttonHeight), _grid.Width, _grid.Height,
                    Plugin.ResultsOffsetX.Value, out LayoutRect fitted, BoardSlack))
                {
                    if (HeadingReports.Add("fail-no-room"))
                        Plugin.Logger?.LogInfo($"[DIAG] failure board held back: strip=({top:0}..{bottom:0}) cannot hold a {_grid.Width:0}x{_grid.Height:0} board, so the vanilla screen is left alone rather than a button covered.");
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
        if (_page == null) return;
        // The success board keeps the position it was measured at. Re-measuring it on every frame made the
        // table resize while the page was still animating, and a table that has settled must not move: it is
        // placed once, and placed again only when the results screen itself changes.
        HudTable.Draw(_grid, _origin.X, _origin.Y, _content, opacity);
        // The rectangle that is really on the screen now, which is what the wheel is aimed at. Written here, on the
        // draw, so it describes the board the player is looking at rather than the cache a rebuild keeps replacing.
        _hitRect = new LayoutRect(_origin.X, _origin.Y, _grid.Width, _grid.Height);
        _hitSucceeded = _succeeded;
        _hitValid = true;

        if (!_succeeded && HeadingReports.Add("failure-board-placed"))
            Plugin.Logger?.LogInfo($"[DIAG] failure board placed: board=({_origin.X:0},{_origin.Y:0},{_grid.Width:0}x{_grid.Height:0}), failureTitleEdge={(float.IsFinite(Title.Edge) ? Title.Edge.ToString("0") : float.IsFinite(FailureLayoutTitle.Edge) ? FailureLayoutTitle.Edge.ToString("0") : "unseen")}, titleSource=mission-title-only; this page's native title/layout and interaction lower wall were measured before drawing.");
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
        if (!HeadingReports.Add("held-back-" + where + "-" + reason)) return;
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
            + $"screen={Screen.width}x{Screen.height}, detail={detail}; the vanilla result page is left exactly as the game drew it and the page is measured again.");
    }

    // Before drawing cached settlement geometry, revalidate the page's measured anchor and every native/Unity action.
    // If a late control overlaps the board or cannot be read, restore vanilla and attach again from the new strip.
    private static bool BoardStillSafe()
    {
        if (_page == null || _grid == null || _content == null) return false;
        var board = new LayoutRect(_origin.X, _origin.Y, _grid.Width, _grid.Height);
        if (_succeeded)
        {
            if (!CompletionReady(_page)
                || !SuccessRegion(_page, out ResultsSafeArea successArea, out bool successButtonMeasured, out float successButtonTop,
                    out float successButtonHeight, out FooterControls successControls, out bool successNativeFooterVisible,
                    out ResultFooterSource successFooterSource, board, true)) return false;
            // 客机页固定存在等待状态文字，任何交互扫描都不可能证明本页为空，所以放行只看锚点渲染完成
            // + 固定页面几何；「完整交互扫描」与 no-footer 证明只约束主机。
            if (!GuestClient() && !successControls.Complete)
            {
                ReportIncompleteScan("success", successControls, successArea.Top, successArea.Height, successFooterSource);
                return false;
            }
            if (!Completion.Observe(Time.frameCount, true, successArea.Top))
            {
                ReportHeldBack("success-edge", "completion-edge-unsettled",
                    "completionTop=" + successArea.Top.ToString("0") + ",completionSettled=false");
                return false;
            }
            if (successControls.OverlapsBoard)
            {
                ReportBoardOverlap("success", successControls, successArea.Top, successArea.Height, successFooterSource);
                return false;
            }
            // The band under the page's measured actions is a placement the fit itself accepted: its own two edges are
            // the whole contract, and no button sits under the board to keep clear of. Every other placement is still
            // bounded by the button the player has to press.
            bool belowActions = successFooterSource == ResultFooterSource.BelowActions;
            float safeTop = Math.Max(0f, successArea.Top) + OffsetY();
            float bottom = Math.Min(successArea.Bottom, Screen.height - 8f);
            if (!float.IsFinite(safeTop) || !float.IsFinite(bottom) || !(bottom >= safeTop))
            {
                ReportHeldBack("success-strip", "strip-unusable",
                    "safeTop=" + safeTop.ToString("0") + ",stripBottom=" + bottom.ToString("0") + ",source=" + successFooterSource);
                return false;
            }
            if (board.Y < safeTop - BoardSlack || board.Bottom > bottom + BoardSlack)
            {
                ReportHeldBack("success-strip", "board-outside-strip",
                    "boardY=" + board.Y.ToString("0") + ",boardBottom=" + board.Bottom.ToString("0")
                    + ",safeTop=" + safeTop.ToString("0") + ",stripBottom=" + bottom.ToString("0") + ",source=" + successFooterSource);
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
            if (!GuestClient() && !successButtonMeasured && (!SuccessNoFooter.Ready || !successControls.ProvenEmpty || successNativeFooterVisible))
            {
                ReportHeldBack("success-strip", "no-footer-unproofed",
                    "buttonMeasured=false,noFooterReady=" + SuccessNoFooter.Ready
                    + ",nativeVisible=" + successNativeFooterVisible + ",count=" + successControls.Count + ",complete=" + successControls.Complete);
                return false;
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
            out float failureFooterTop, out float failureFooterHeight, out bool failureTitleSettled, out FooterControls failureControls,
            out bool failureNativeFooterVisible, out ResultFooterSource failureFooterSource, board, true)) return false;
        // 客机：同上——完整扫描证明只约束主机，客机放行只看锚点渲染完成 + 固定页面几何。
        if (!GuestClient() && !failureControls.Complete)
        {
            ReportIncompleteScan("failure", failureControls, failureTop, Math.Min(failureBottom, Screen.height - 8f) - failureTop, failureFooterSource);
            return false;
        }
        if (!failureTitleSettled)
        {
            ReportHeldBack("failure-strip", "title-edge-unsettled", "titleEdge=" + (float.IsFinite(Title.Edge) ? Title.Edge.ToString("0") : "unmeasurable"));
            return false;
        }
        if (failureControls.OverlapsBoard)
        {
            ReportBoardOverlap("failure", failureControls, failureTop, Math.Min(failureBottom, Screen.height - 8f) - failureTop, failureFooterSource);
            return false;
        }
        float failureBottom2 = Math.Min(failureBottom, Screen.height - 8f);
        if (board.Y < failureTop - BoardSlack || board.Bottom > failureBottom2 + BoardSlack)
        {
            ReportHeldBack("failure-strip", "board-outside-strip",
                "boardY=" + board.Y.ToString("0") + ",boardBottom=" + board.Bottom.ToString("0")
                + ",stripTop=" + failureTop.ToString("0") + ",stripBottom=" + failureBottom2.ToString("0") + ",source=" + failureFooterSource);
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
        if (!GuestClient() && !failureFooterMeasured && (!FailureNoFooter.Ready || !failureControls.ProvenEmpty || failureNativeFooterVisible))
        {
            ReportHeldBack("failure-strip", "no-footer-unproofed",
                "buttonMeasured=false,noFooterReady=" + FailureNoFooter.Ready
                + ",nativeVisible=" + failureNativeFooterVisible + ",count=" + failureControls.Count + ",complete=" + failureControls.Complete);
            return false;
        }
        var failureStrip = new BoardStrip(failureTop, failureBottom, failureFooterMeasured, failureFooterTop, failureFooterHeight,
            failureTitleSettled, failureFooterSource);
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
        _placedOnFallback = false; _fallbackRecoveries = 0;
        Drift.Forget(); Retry.Forget(); ClearHit();
        if (HeadingReports.Add("board-interaction-changed"))
            Plugin.Logger?.LogInfo("[DIAG] result board deferred: the page exposed an unreadable or overlapping native/UI control after placement; vanilla is restored and the strip is measured again before the overlay may return.");
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
                if (waiting != null && waiting.gameObject.activeInHierarchy && !string.IsNullOrWhiteSpace(waiting.text))
                {
                    protectedText = waiting;
                    if (TryScreenRect(waiting, out LayoutRect waitingRect)) footerText = waitingRect.Y;
                }
            }
            else
            {
                // The success page: the icon row is the upper bound and the client's waiting block the lower one, and
                // the last trusted icon row stands in for a frame in which the reveal has not drawn it yet.
                float iconRow = IconRowBottom(page);
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
        // 1. The agreed band, with the same two edges the strict path uses: the fail-open board may be drawn where the
        //    page's own geometry could not be proven, but it may not stand where the page draws its icon row or the
        //    footer the player is reading. This is the case the last release got wrong: the client's band ran under the
        //    waiting line because the band was the screen's own safe area, not this page's.
        LobbyButtonTop(page, out float pageButtonTop);
        if (ResultsBounds.TryResolve(Screen.height, anchorBottom, anchorSource,
                float.IsFinite(pageButtonTop) ? pageButtonTop : float.NaN,
                footerText, footerTextSource, controls.FooterTop, out ResultsBand band)
            && band.Fits
            // A band that cannot hold the board's own floor is not a band: the board would be taller than the page allows,
            // and since it hangs from the band's top edge it would run past the footer rather than slide up over the icon
            // row. Such a band is refused here and the reduced-opacity tier below answers instead, saying so.
            && ResultFallbackTier.AcceptsPageBand(band.Height, FallbackFloor()))
        {
            top = band.Top; room = band.Height;
            _fallbackOpacity = 1f;
            strip = new BoardStrip(band.Top, band.Bottom, false, float.NaN, float.NaN, float.IsFinite(anchorBottom),
                ResultFooterSource.ScreenSafeArea);
            ReportBand("fail-open", band, anchorBottom, footerText, FallbackFloor());
            why = "band top=" + band.Top.ToString("0") + " (" + ResultsBounds.Name(band.TopSource) + ")"
                + ", bottom=" + band.Bottom.ToString("0") + " (" + ResultsBounds.Name(band.BottomSource) + ")"
                + ", room=" + band.Height.ToString("0") + "px, clamped=" + band.Clamped.ToString().ToLowerInvariant()
                + ", boardFloor=" + FallbackFloor().ToString("0")
                + ", anchorBottom=" + (float.IsFinite(anchorBottom) ? anchorBottom.ToString("0") : "unmeasurable")
                + ", footerText=" + (float.IsFinite(footerText) ? footerText.ToString("0") : "unmeasurable")
                + " (" + ResultsBounds.Name(footerTextSource) + ")"
                + ", footerTop=" + (float.IsFinite(controls.FooterTop) ? controls.FooterTop.ToString("0") : "unmeasurable")
                + ", controls=" + controls.Count + ", hidden=" + controls.Hidden + ", unmeasurable=" + controls.Unmeasurable
                + ", scanComplete=" + controls.Complete.ToString().ToLowerInvariant();
            return room > 0f;
        }
        // 2. The page offered no band that can hold the board, or offered none at all: the window's own safe area answers,
        //    and it steps around every control the scan did read **and** around the waiting block. That second wall is what
        //    this tier used to miss: on a client page whose leave button is not measurable it is the only wall there is, and
        //    a band that ignored it ran the board straight over the line the player is reading.
        if (!ResultFallbackLayout.TryResolve(Screen.height, anchorBottom, controls.FooterTop, footerText,
            controls.BelowActions.Top, controls.BelowActions.Bottom, FallbackFloor(), out ResultFallbackPlan plan))
        {
            why = "no-safe-area: anchorBottom=" + (float.IsFinite(anchorBottom) ? anchorBottom.ToString("0") : "unmeasurable")
                + ",footerTop=" + (float.IsFinite(controls.FooterTop) ? controls.FooterTop.ToString("0") : "unmeasurable")
                + ",footerText=" + (float.IsFinite(footerText) ? footerText.ToString("0") : "unmeasurable")
                + " (" + ResultsBounds.Name(footerTextSource) + ")"
                + ",screen=" + Screen.height + ",boardFloor=" + FallbackFloor().ToString("0");
            return false;
        }
        top = plan.Top; room = plan.Height;
        _fallbackOpacity = plan.Opacity;
        strip = new BoardStrip(plan.Top, plan.Bottom, false, float.NaN, float.NaN, float.IsFinite(anchorBottom),
            plan.BelowControls ? ResultFooterSource.BelowActions : ResultFooterSource.ScreenSafeArea);
        why = ResultFallbackLayout.Describe(plan, Screen.height, FallbackFloor())
            + ", anchorBottom=" + (float.IsFinite(anchorBottom) ? anchorBottom.ToString("0") : "unmeasurable")
            + ", footerText=" + (float.IsFinite(footerText) ? footerText.ToString("0") : "unmeasurable")
            + " (" + ResultsBounds.Name(footerTextSource) + ")"
            + ", footerTop=" + (float.IsFinite(controls.FooterTop) ? controls.FooterTop.ToString("0") : "unmeasurable")
            + ", controls=" + controls.Count + ", hidden=" + controls.Hidden + ", unmeasurable=" + controls.Unmeasurable
            + ", scanComplete=" + controls.Complete.ToString().ToLowerInvariant();
        return room > 0f;
    }
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
        // A dozen pixels of movement is the page's own animation, not a control arriving on top of the board.
        bool moved = Math.Abs(rect.Y - _origin.Y) > ResultAnchorDrift.MinimumPixels || Math.Abs(rect.X - _origin.X) > ResultAnchorDrift.MinimumPixels;
        if (moved)
        {
            // The board keeps the grid it was fitted with and only its place moves; the strip recorded with the placement is
            // left alone, so the one place that really records a placement (`Fit`) stays the only one that does.
            _origin = rect; _holdBoard = rect;
        }
        if (!moved && !opacityChanged) return;
        if (HeadingReports.Add(moved ? "fallback-moved" : "fallback-fade"))
            Plugin.Logger?.LogInfo($"[DIAG] the fail-open settlement board {(moved ? $"moved to ({rect.X:0},{rect.Y:0})" : "stayed where it was")} "
                + $"because the page's own controls changed under it; band=({strip.Top:0}..{strip.Bottom:0}), boardBottom={rect.Bottom:0}, "
                + $"opacity={_fallbackOpacity:0.00}{(overBand ? " (over its band, drawn faint)" : string.Empty)}, role={ResultPageRolePolicy.Name(Role())}. "
                + "It is moved out of their way and drawn fainter when nothing clear is left, and it is never taken away. The next measurement "
                + "that proves the page's own anchors places it strictly again.");
    }

    // F8 on a result page: the override itself.
    //
    // It depends on nothing that has to be ready first - not the title gate, not the completion edge, not the footer
    // settle, not the interaction proof, not even the three visibility switches - and it drops whatever placement was
    // refused before it, so the very next repaint measures and draws from the fail-open path. Pressing it again hands
    // the page back to the ordinary judgement.
    internal static bool ToggleForcedFallback()
    {
        Forced = !Forced;
        Invalidate();
        _strictMisses = 0;
        _fallbackPlaced = false;
        _fallbackOpacity = 1f;
        return Forced;
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

    // Whether the board standing now should be measured again instead of merely refreshed.
    //
    // Two things answer yes, and both are budgeted per page:
    //
    //   the second look   the board was laid out against a guess - the lobby button handed no geometry, or the
    //                     anchor above it had not settled - and the page's own edges are readable now. That is
    //                     the difference between a first measurement taken before the footer had revealed and a
    //                     table that stays small for the rest of the expedition, and it is what
    //                     `ResultAnchorRecovery` budgets: one retry per page, never a per-frame re-measure.
    //                     `1.19.8` only ever took it on the success page; both pages take it now, because the
    //                     failure page's title and footer animate in exactly the same way.
    //   the page moved    the strip the board was really measured into no longer describes this page: another
    //                     mod re-laid the footer out, the resolution changed, the completion section's optional
    //                     parts appeared after the native Blink finished. `ResultAnchorDrift` compares the
    //                     recorded strip against a fresh measurement, with a threshold and two samples of
    //                     hysteresis, and spends at most two re-measures per page.
    //
    // A board whose strip was measured and whose page has not moved is touched by neither, which is what keeps a
    // settled table from jumping.
    private static bool FallbackRecoveryDue(bool succeeded, Component page)
    {
        if (page == null || !_placed) return false;
        if (_placedOnFallback && AnchorReadable(page, succeeded)
            && ResultAnchorRecovery.ShouldRetry(_fallbackRecoveries, true, true))
        {
            _fallbackRecoveries = ResultAnchorRecovery.Record(_fallbackRecoveries);
            _placedOnFallback = false;
            if (HeadingReports.Add(succeeded ? "success-region-recovered" : "failure-region-recovered"))
                Plugin.Logger?.LogInfo($"[DIAG] {(succeeded ? "success" : "failure")} region re-measured: both of this page's own edges - the anchor the board hangs from and the button it has to clear - are readable now, and the board standing here was laid out before one of them was, so this page is measured again once - and only once - and the board is re-placed against the real strip instead of the guess.");
            RearmSettle();
            return true;
        }
        return AnchorDriftDue(page, succeeded);
    }

    // Whether the page's own two edges can both be measured now: the anchor the board hangs from and the button
    // it may not reach. That is the whole of "the second look is worth taking" - re-measuring before the footer
    // hands over its geometry would only reproduce the guess that was placed in the first place.
    private static bool AnchorReadable(Component page, bool succeeded)
        => succeeded ? SuccessButtonReadable(page) : FailureEdgesReadable(page);

    // The failure page's own two edges: the native title, anywhere on the frame, and one of the two footer
    // buttons the strip is bounded by. Alpha is deliberately not part of it - the reveal is exactly the case the
    // second look exists for - and neither is whether the title has finished settling: the strip only has to be
    // readable.
    private static bool FailureEdgesReadable(Component page)
    {
        try
        {
            var fail = page != null ? page.TryCast<CM_PageExpeditionFail>() : null;
            if (fail == null) return false;
            var heading = fail.m_missionFailed_text;
            if (heading == null || !heading.gameObject.activeInHierarchy) return false;
            LayoutRect title = default;
            if (!TryScreenRect(heading, out title) || !float.IsFinite(title.Bottom)) return false;
            return float.IsFinite(VisibleTop(fail.m_btnRestartCheckpoint))
                || float.IsFinite(VisibleTop(fail.m_btnGoToLobby));
        }
        catch { return false; }
    }

    // The page moved away from the strip the board was measured into. The sample is the same measurement the
    // placement uses, taken without any of its gates and without touching a counter, a hold or a log line of the
    // placement itself: a frame that cannot read the page's own edges is not evidence that they moved.
    private static bool AnchorDriftDue(Component page, bool succeeded)
    {
        if (!Drift.Armed) return false;
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
                if (!SuccessRegion(page, out ResultsSafeArea successArea, out bool successButtonMeasured, out _, out _, out FooterControls successControls, out bool successNativeFooterVisible, out _)) return false;
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
                out bool failureTitleSettled, out FooterControls failureControls, out bool failureNativeFooterVisible, out _)
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
        // The board's own floor, measured before the page is asked for a strip: this is the one figure that tells a
        // strip that is merely tight from one the table can never be drawn into, and the page's own decision about
        // where the board may stand is made against it (see the host branch of `SuccessRegion`). The settlement board
        // is always drawn with its three kill tiers split, so the row count is the split board's and does not depend
        // on how many players are in the squad.
        _boardFloor = ResultsFontGrowth.MinimumBoardHeight(ImpactBoard.RowCount(true),
            HudTable.RowEm(ResultsFontGrowth.MinimumReadablePixels), Plugin.Density);
        // The strict placement first, exactly as it has always been: the page's own settled anchor, its measured
        // footer, its complete interaction scan. **But a placement is only a placement when the fit really puts a board
        // on the page.** `Placement` answers "the page offered a strip", and a strip can be shorter than the board's own
        // floor (30 rows x 3 px x 1.5 em = 135 px) while still being non-empty; a page answering `true` there used to
        // clear the refusal count and then have `Fit` refuse in the very same frame - for ever, with F8 unable to help,
        // because `ShouldFallback` was never evaluated. The count is therefore spent by what really happened: `TryFit`
        // below, never by `Placement` on its own.
        bool strict = Placement(page, succeeded, out _, out float room, out BoardStrip strip);
        ImpactReport report = BuildReport(succeeded);
        if (report.Entries.Count == 0)
        {
            if (HeadingReports.Add("empty-roster")) Plugin.Logger?.LogWarning("[DIAG] result board waiting: no retained player counters.");
            return;
        }
        ImpactBoardLayout board = ImpactBoard.Build(report, true, succeeded); // Settlement kill tiers are always explicit.
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
        if (ResultPlacementPolicy.Decide(false, _strictMisses, Forced) == ResultPlacementOutcome.Deferred)
        {
            // Say why the frame was empty, once per page and per reason: a strip that is merely short and a grid the
            // fit refused are two different faults and used to look identical.
            ReportHeldBack("prepare", strict ? "fit-refused" : "strip-refused", refusal);
            return;
        }
        if (!FallbackPlacement(page, succeeded, out _, out room, out strip, out string fallbackWhy))
        {
            // The fail-open placement itself found no band this frame. That is a miss of its own: the cadence and the log
            // below need it, and the floor ladder must not keep asking the same impossible question.
            _fallbackMisses = ResultPlacementPolicy.Spend(_fallbackMisses);
            ReportHeldBack("prepare", "fallback-unavailable", fallbackWhy + ", misses=" + _fallbackMisses + ",floor=" + FallbackFloor().ToString("0") + "px");
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
        PanelGrid grid = FitBoard(content, room, boardWidth, succeeded, strip.ButtonHeight);
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
    private static PanelGrid FitBoard(TableContent content, float room, float boardWidth, bool succeeded, float buttonHeight = float.NaN)
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
            size => NaturalSize(content, size, bandFloor), out int floor);
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
            PanelGrid resized = FitBoard(content, room, boardWidth, succeeded, strip.ButtonHeight);
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
        // A fail-open board is never refused: it hangs from the band's own top edge (never above it) and, when it is
        // taller than the band, it is drawn at `OverlapOpacity` instead of being dropped or slid up over the page above
        // it. This is the one placement in the mod that accepts a board over its own band, and it is reachable only from
        // `_fallbackPlaced`.
        if (!placedStrictly && _fallbackPlaced)
            placedStrictly = ResultFallbackLayout.TryPlaceInBand(Screen.width, Screen.height, strip.Top, strip.Bottom,
                grid.Width, grid.Height, Plugin.ResultsOffsetX.Value, out origin);
        if (placedStrictly)
        {
            _origin = origin;
            _content = content; _grid = grid;
            if (_fallbackPlaced && float.IsFinite(strip.Bottom) && origin.Bottom > strip.Bottom + 0.5f
                && !(strip.Bottom <= strip.Top))
            {
                // The board is taller than the band the page allowed. It keeps the band's top edge - the icon row stays
                // clear - and it is drawn fainter so the footer underneath stays readable. Said once, with the numbers.
                _fallbackOpacity = Math.Min(_fallbackOpacity, ResultFallbackLayout.OverlapOpacity);
                if (HeadingReports.Add("fallback-over-band"))
                    Plugin.Logger?.LogInfo($"[DIAG] fail-open board taller than its band: overBand=({strip.Top:0}..{strip.Bottom:0}) is "
                        + $"{(strip.Bottom - strip.Top):0}px and the grid is {grid.Height:0}px, so the board keeps the band's top edge "
                        + $"({origin.Y:0}) and is drawn at opacity {_fallbackOpacity:0.00} instead of sliding up over the page above it. "
                        + $"role={ResultPageRolePolicy.Name(Role())}, screen={Screen.width}x{Screen.height}.");
            }
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
                Plugin.Logger.LogInfo($"[DIAG] influence board {board.ColumnCount} columns x {board.Rows} rows ({report.Entries.Count} players), {grid.Width:0}x{grid.Height:0} px at ({x:0},{y:0}) of a {Screen.width}x{Screen.height} window, {(succeeded ? "success" : "failure")} screen, anchorSource={(succeeded ? "completion-section" : "failure-strip")}, blankAbove={blankAbove:0}px, blankBelow={blankBelow:0}px, glyph {grid.FontPixels:0}px filling {grid.Height * 100f / stripHeight:0}% of the {stripHeight:0}px strip between the completion section and the lobby button ({grid.Width * 100f / Math.Max(1f, Screen.width - 48f):0}% of the {Screen.width - 48}x width it was fitted to); drawn by IMGUI, no vanilla report card was modified.");
            }
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
        if (HeadingReports.Add("fit-failed"))
            Plugin.Logger?.LogWarning($"[DIAG] result board cannot fit: role={ResultPageRolePolicy.Name(Role())}, page={(succeeded ? "success" : "failure")}, "
                + $"top={strip.Top:0}, bottom={strip.Bottom:0}, strip={(strip.Bottom - strip.Top):0}px, grid={grid.Width:0}x{grid.Height:0}, "
                + $"need={grid.Height:0}px at the {grid.FontPixels:0}px glyph it was built with (floor={_boardFloor:0}px for {grid.Rows} rows), "
                + $"have={((strip.Bottom - strip.Top)):0}px, footerSource={strip.FooterSource}, buttonTop={(float.IsFinite(strip.ButtonTop) ? strip.ButtonTop.ToString("0") : "unmeasurable")}, "
                + $"screen={Screen.width}x{Screen.height}; nothing is drawn over the page - the vanilla result screen is kept and the strip is measured again.");
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
                out ResultFooterSource failureFooterSource)) return false;
            // 客机：完整扫描证明只约束主机；客机放行只看锚点渲染完成 + 固定页面几何。
            if (!GuestClient() && !failureControls.Complete)
            {
                ReportIncompleteScan("failure", failureControls, top, Math.Min(failureBottom, Screen.height - 8f) - top, failureFooterSource);
                return false;
            }
            if (GuestClient())
            {
                // 客机：锚点（任务失败标题）渲染完成即落板；条带已由 FailureStrip 的客机分支算成固定几何。
                ButtonSettled(false, float.NaN);
            }
            else if (failureButtonMeasured)
            {
                if (!ButtonSettled(true, failureButtonTop))
                {
                    if (Footer.Exhausted && HeadingReports.Add("failure-footer-unsettled"))
                        Plugin.Logger?.LogInfo($"[DIAG] failure board held back: {Footer.Misses} readings without a stable measured interaction edge; the vanilla failure page is retained and no window-fraction fallback is used.");
                    return false;
                }
            }
            else
            {
                // 1.19.14 原样：没有实测按钮时先清掉按钮 settle 状态，否则 Footer 的稳定计数会跨未测帧残留，
                // A→B→A 的输入会让 ButtonSettled 在单帧读数上就放行，且 Footer.Exhausted 永不成立、诊断日志不打印。
                ButtonSettled(false, float.NaN);
                bool failureNoFooterReady = FailureNoFooter.Observe(Time.frameCount,
                    ResultPageRolePolicy.UsesNoFooterSafeArea(Role()), failureTitleSettled,
                    page != null, failureNativeFooterVisible, failureControls.Complete, failureControls.Count, failureControls.Unmeasurable);
                if (!failureNoFooterReady)
                {
                    // The role decides what this line is allowed to say; see the matching line on the success page.
                    if (Footer.Exhausted && HeadingReports.Add("failure-footer-unsettled"))
                        Plugin.Logger?.LogInfo($"[DIAG] failure board held back: role={ResultPageRolePolicy.Name(Role())}, no stable native footer edge was measured; "
                            + $"noFooterProof={(ResultPageRolePolicy.UsesNoFooterSafeArea(Role()) ? "available" : "not-this-role")} (ready={FailureNoFooter.Ready}, samples={FailureNoFooter.Samples}), "
                            + $"count={failureControls.Count},unmeasurable={failureControls.Unmeasurable},complete={failureControls.Complete},nativeVisible={failureNativeFooterVisible},footerTop={(float.IsFinite(failureControls.FooterTop) ? failureControls.FooterTop.ToString("0") : "unmeasurable")}, "
                            + $"need={_boardFloor:0}px; vanilla is kept and placement is retried.");
                    return false;
                }
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
                    Plugin.Logger?.LogInfo($"[DIAG] failure footerSource=client-no-native-footer-safe-area: native lobbyVisible=false, complete interaction scan empty for {FailureNoFooter.Samples} distinct frames, title settled; safe strip=({top:0}..{failureBottom:0}).");
            }
            // 客机没有原生页脚按钮，条带不得标成「按钮已实测」，否则 ResultBoardHold 会按按钮高度算可读性下限。
            strip = new BoardStrip(top, failureBottom, GuestClient() ? false : failureButtonMeasured, failureButtonTop, failureButtonHeight, failureTitleSettled, failureFooterSource);
            room = Math.Min(failureBottom, Screen.height - 8f) - top;
            return room > 0f;
        }

        if (!CompletionReady(page))
        {
            // The completion section is the success page's one honest anchor. A page that never finishes drawing it is
            // refused here - and the fail-open path above then places the board from the screen's own safe area, which is
            // why this refusal is a log line and not an empty screen.
            ReportHeldBack("success-anchor", "completion-not-ready",
                "completion=" + _completionState + ",boardFloor=" + _boardFloor.ToString("0"));
            return false;
        }
        bool completionMeasured = SuccessRegion(page, out ResultsSafeArea area, out bool measuredButton,
            out float measuredTop, out float measuredHeight, out FooterControls successControls,
            out bool successNativeFooterVisible, out ResultFooterSource footerSource);
        if (!completionMeasured)
        {
            // The page has no completion section this build could read: nothing proves where a board would hang from, so
            // the vanilla page is kept and the line says which read failed rather than leaving the silence.
            ReportHeldBack("success-anchor", "completion-unmeasurable",
                "completionEdge=" + (float.IsFinite(Completion.Edge) ? Completion.Edge.ToString("0") : "unmeasured") + ",boardFloor=" + _boardFloor.ToString("0"));
            return false;
        }
        bool completionSettled = Completion.Observe(Time.frameCount, true, area.Top);
        // 客机：同上——完整扫描证明只约束主机；客机放行只看锚点渲染完成 + 固定页面几何。
        if (!GuestClient() && !successControls.Complete)
        {
            ReportIncompleteScan("success", successControls, area.Top, area.Height, footerSource);
            return false;
        }
        if (!completionSettled)
        {
            ReportHeldBack("success-edge", "completion-edge-unsettled", "completionTop=" + area.Top.ToString("0"));
            return false;
        }
        if (GuestClient())
        {
            // 客机：条带已由 SuccessRegion 的客机分支按固定几何算好；完成区上界稳定即落板。
            ButtonSettled(false, float.NaN);
        }
        else if (measuredButton)
        {
            if (!ButtonSettled(true, measuredTop))
            {
                if (Footer.Exhausted && HeadingReports.Add("success-footer-unsettled"))
                    Plugin.Logger?.LogInfo($"[DIAG] success board held back: {Footer.Misses} readings without a stable measured interaction edge; vanilla is retained and no guessed footer bound is used.");
                return false;
            }
        }
        else
        {
            // 1.19.14 原样：没有实测按钮时先清掉按钮 settle 状态，否则 Footer 的稳定计数会跨未测帧残留，
            // A→B→A 的输入会让 ButtonSettled 在单帧读数上就放行，且 Footer.Exhausted 永不成立、诊断日志不打印。
            ButtonSettled(false, float.NaN);
            bool successNoFooterReady = SuccessNoFooter.Observe(Time.frameCount,
                ResultPageRolePolicy.UsesNoFooterSafeArea(Role()), completionSettled,
                page != null, successNativeFooterVisible, successControls.Complete, successControls.Count, successControls.Unmeasurable);
            if (!successNoFooterReady)
            {
                // The role decides what this line is allowed to say. Only a client can hold the "this page has no footer"
                // proof, so a host that cannot measure its own button is held back by the *measured* footer it could not
                // read - it is never told about a client proof it can never have, which is what this line used to print
                // for the host that was reporting the bug.
                if (Footer.Exhausted && HeadingReports.Add("success-footer-unsettled"))
                    Plugin.Logger?.LogInfo($"[DIAG] success board held back: role={ResultPageRolePolicy.Name(Role())}, no stable native footer edge was measured; "
                        + $"noFooterProof={(ResultPageRolePolicy.UsesNoFooterSafeArea(Role()) ? "available" : "not-this-role")} (ready={SuccessNoFooter.Ready}, samples={SuccessNoFooter.Samples}), "
                        + $"count={successControls.Count},unmeasurable={successControls.Unmeasurable},complete={successControls.Complete},nativeVisible={successNativeFooterVisible},footerTop={(float.IsFinite(successControls.FooterTop) ? successControls.FooterTop.ToString("0") : "unmeasurable")}, "
                        + $"need={_boardFloor:0}px; vanilla is kept and the page is measured again at the bounded cadence.");
                return false;
            }
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
                Plugin.Logger?.LogInfo($"[DIAG] success footerSource=client-no-native-footer-safe-area: native lobbyVisible=false, complete interaction scan empty for {SuccessNoFooter.Samples} distinct frames, Blink and completion edge settled; safe strip=({area.Top:0}..{area.Bottom:0}).");
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
        strip = new BoardStrip(top, top + room, GuestClient() || belowActions ? false : measuredButton, measuredTop, measuredHeight, completionSettled, footerSource);
        return room > 0f;
    }
    // The footer of a result page animates in, so the button's own top edge is only trusted once it has stopped moving -
    // and a button the page has not drawn yet is not a licence to use a fraction of the window instead. `Footer` answers
    // true only for an edge it really read at the same value twice; a button that never arrives leaves the vanilla page
    // in place, and the page is retried at the bounded cadence `Retry` keeps.
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
        string state = $"completed={ledger.Completed}/{ledger.Count}, names={ledger.Names}, artwork={ledger.Artwork}, ready={ready}";
        if (state != _completionState)
        {
            _completionState = state;
            Plugin.Logger?.LogInfo("[DIAG] success native reveal: " + state);
        }
        if (ready) Plugin.Logger?.LogInfo("[DIAG] success board released after the game's sector BlinkIn routines completed; no timeout or alpha threshold bypass.");
        return ready;
    }

    // The success page's completion section and its lobby button, reduced to the strip between them. Both
    // are read from the page itself: the sector box, its header and the four cleared-sector icons make up
    // the completion section, and the deepest edge the page actually drew is the one the board must clear.
    // The answer is whether that *section* could be measured at all - not whether the strip happens to fit -
    // because the caller decides placement from it through `ResultsUpperBound`, and a section nobody could read
    // must never be turned into a fraction of the window.
    private static bool SuccessRegion(Component page, out ResultsSafeArea area, out bool buttonMeasured, out float buttonTop, out float buttonHeight, out FooterControls controls, out bool nativeFooterVisible, out ResultFooterSource footerSource, LayoutRect board = default, bool inspectBoard = false)
    {
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
        string completionSource = "none";
        // The lobby button's rectangle and the edge it produces are read inside the guarded block, and both
        // are needed after it, so they are declared here and only filled there.
        LayoutRect lobbyRect = default;
        ResultsRegion.LobbyEdgeReading wall = default;
        try
        {
            // The completion section is measured as the pixels it really draws: the sector box, its header and
            // the four cleared-sector icons, each read for its own visible renderers with the padded container
            // rectangle only as a fallback. A part the page switched off - the second and third icons of a squad
            // that cleared one sector - and a part still at zero alpha contribute nothing, so the strip starts
            // under the last thing on the screen instead of under a hidden prefab's old geometry. See
            // `CompletionBounds` for the rule itself.
            var parts = new List<CompletionPart>(CompletionParts)
            {
                Read(success.m_sectorsWhiteBox),
                Read(success.m_sectorHeader),
                Read(success.m_sectorIconMain),
                Read(success.m_sectorIconSecond),
                Read(success.m_sectorIconThird),
                Read(success.m_sectorIconAllCompleted)
            };
            completion = CompletionBounds.Deepest(parts, out int deepest);
            completionSource = deepest >= 0 && deepest < CompletionNames.Length ? CompletionNames[deepest] : "none";
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
            if (ResultPageRolePolicy.UsesBandBelowActions(Role(), anchoredHeight, _boardFloor, controls.BelowActions.Height))
            {
                area = new ResultsSafeArea(controls.BelowActions.Top, controls.BelowActions.Bottom);
                footerSource = ResultFooterSource.BelowActions;
                if (HeadingReports.Add("success-band-below-actions"))
                    Plugin.Logger?.LogInfo($"[DIAG] success strip cannot hold the board's own floor: role={ResultPageRolePolicy.Name(Role())}, "
                        + $"strip=({anchoredTop:0}..{anchoredBottom:0}) is {anchoredHeight:0}px but the {ImpactBoard.RowCount(true)}-row board cannot be drawn under {_boardFloor:0}px at any readable glyph size on a {Screen.height} window; "
                        + $"the page's measured actions are stepped around instead of ending the strip - {DescribeLobbyWall(wall)}, lobbyEdge={DescribeLobbyEdge(wall)} - "
                        + $"and the free band below them is offered to the fit: boardBand=({controls.BelowActions.Top:0}..{controls.BelowActions.Bottom:0}) is {controls.BelowActions.Height:0}px, footerSource=below-actions-band. "
                        + "Every control the scan measured is outside that band, so no interaction is covered.");
            }
            else if (area.Fits && float.IsFinite(_boardFloor) && _boardFloor > 0f && anchoredHeight + 0.5f < _boardFloor)
            {
                // The honest refusal: the strip cannot hold the board and the page leaves no band that could. The numbers
                // are the whole point of the line - a strip of 132 px and a band of 40 px are two different faults.
                if (HeadingReports.Add("success-band-unavailable"))
                    Plugin.Logger?.LogWarning($"[DIAG] success board deferred: role={ResultPageRolePolicy.Name(Role())}, need={_boardFloor:0}px for the {ImpactBoard.RowCount(true)}-row board, "
                        + $"have={anchoredHeight:0}px in the strip ({anchoredTop:0}..{anchoredBottom:0}) above the page's own actions"
                        + (controls.BelowActions.Fits
                            ? $", and the free band below them ({controls.BelowActions.Top:0}..{controls.BelowActions.Bottom:0}) is only {controls.BelowActions.Height:0}px"
                            : ", and the page's measured actions leave no free band below them")
                        + $", {DescribeLobbyWall(wall)}, lobbyEdge={DescribeLobbyEdge(wall)}, screen={Screen.width}x{Screen.height}; the vanilla result screen is kept rather than drawn over any control.");
            }
        }
        // ---- the agreed band: icon row above, the footer this machine can see below ---------------------------------
        //
        // The strip the two branches above produced is *a* band, not *the* band: the success branch hangs it from the
        // deepest part of the whole completion section (the white plate behind the icons on a client page) and the
        // guest branch runs it to the foot of the window. Both are replaced here by the one rule this mod agreed on
        // with its players:
        //
        //   top    the sector-icon row's own bottom edge (label included) plus a clearance scaled from 16 px at 1080p
        //   bottom the highest footer this machine can measure minus the same clearance: the host's leave button, or
        //          the client's waiting-status text block, whichever the page really draws
        //
        // The board hangs from the top edge, so a taller band is a taller table and never a table further down; the
        // measured free band under the page's actions (1.19.16) is left exactly as it was measured, because its own two
        // edges are the whole contract.
        float iconRow = IconRowBottom(success);
        if (float.IsFinite(iconRow)) _iconRowBottom = iconRow;
        float waitingText = FooterTextTop(success, out ResultBoundSource waitingTextSource);
        float bandAnchor = float.IsFinite(iconRow) ? iconRow : _iconRowBottom;
        ResultBoundSource bandAnchorSource = float.IsFinite(iconRow) ? ResultBoundSource.IconRow
            : float.IsFinite(_iconRowBottom) ? ResultBoundSource.LastTrustedIconRow
            : ResultBoundSource.ScreenSafeArea;
        if (ResultsBounds.TryResolve(Screen.height, bandAnchor, bandAnchorSource,
                buttonMeasured ? buttonTop : float.NaN, waitingText, waitingTextSource, controls.FooterTop, out ResultsBand band))
        {
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
            Plugin.Logger?.LogInfo($"[DIAG] success region: anchorSource={completionSource}, completion={(completionMeasured ? completion.ToString("0") : "unmeasurable")} (deepest visible part of the completion section, gap={MessageGap:0}px), "
                + $"{DescribeLobbyWall(wall)}, lobbyEdge={DescribeLobbyEdge(wall)}, "
                + (area.Fits ? $"boardTop={area.Top:0}, room={area.Height:0}" : "boardTop=unmeasurable, room=unmeasurable")
                + $" of a {Screen.height} window, "
                + $"boardBand={DescribeBand(footerSource)}, boardFloor={(_boardFloor):0}, role={ResultPageRolePolicy.Name(Role())}; "
                + "the strip is bounded by this page's own measured geometry - the completion section above and, below, the lobby button's upper edge or the free band under the page's measured actions - "
                + "never by a hidden, transparent or off-page control, and it is measured from drawn geometry rather than inferred from the draw. All four players scale into whichever band is used, "
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
    private const int CompletionParts = 6;
    private static readonly string[] CompletionNames =
        { "sectorsWhiteBox", "sectorHeader", "sectorIconMain", "sectorIconSecond", "sectorIconThird", "sectorIconAllCompleted" };

    // One element of the completion section as the player can see it: whether the page has it at all, whether it
    // is switched on this frame, how visible it is, the deepest renderer that is really drawing under it - the
    // captions included - and its own container rectangle as the fallback for an element that draws nothing
    // measurable. The pixels, not the padded layout, are what `CompletionBounds` builds the strip from.
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
        float alpha = ControlAlpha(element);
        float own = TryScreenRect(element, out LayoutRect rect) ? rect.Bottom : float.NaN;
        float visual = float.NaN;
        try
        {
            var renderers = element.GetComponentsInChildren<Renderer>(true);
            if (renderers != null)
                foreach (Renderer child in renderers)
                {
                    if (child == null || !child.enabled) continue;
                    if (!child.gameObject.activeInHierarchy) continue;
                    if (TryScreenRect(child, out LayoutRect nested)) visual = ResultsRegion.LowestBottom(nested.Bottom, visual);
                }
        }
        catch { }
        return new CompletionPart(true, active, alpha, own, visual);
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
                var group = node.TryCast<CanvasGroup>();
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

    // The failure strip's upper edge is the mission-failed title, never the client waiting-status line. The
    // active-title route requires full opacity plus a stable measured edge; an inactive client may use only the
    // same mission title's layout after its native failure coroutine has completed and that layout settles.
    // The waiting-status text is measured separately as a protected lower wall, and the complete interaction scan
    // still guards the board against every page/tree/source-parent control before each draw.
    private static bool FailureStrip(Component page, out float top, out float bottom, out float opacity, out bool buttonMeasured, out float buttonTop, out float buttonHeight, out bool anchorSettled, out FooterControls controls, out bool nativeFooterVisible, out ResultFooterSource footerSource, LayoutRect overlapBoard = default, bool inspectBoard = false)
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
                : anchorKind == FailureTitleAnchorKind.MissionTitleLayoutAfterSequence ? "mission-title-layout-after-sequence" : "none";
            bool active = anchorKind == FailureTitleAnchorKind.MissionFailed && missionActive;
            float alpha = active ? HeadingAlpha(heading) : float.NaN;
            float rawTextAlpha = float.NaN;
            try { if (heading != null) rawTextAlpha = heading.alpha; } catch { }
            LayoutRect rect = missionRect;
            opacity = active && float.IsFinite(alpha) ? Mathf.Clamp01(alpha) : 1f;
            bool measured = anchorKind == FailureTitleAnchorKind.MissionFailed
                ? active && missionGeometryMeasured
                : anchorKind == FailureTitleAnchorKind.MissionTitleLayoutAfterSequence && missionGeometryMeasured;
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
            if (anchorKind == FailureTitleAnchorKind.MissionFailed)
            {
                FailureLayoutTitle.Forget();
                anchorSettled = Title.Observe(Time.frameCount, active, alpha, measured, measured ? rect.Bottom : float.NaN);
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
            if (!anchorSettled)
            {
                if ((Title.Misses >= ResultRetryCadence.EveryFrameFrames
                    || FailureLayoutTitle.Misses >= ResultRetryCadence.EveryFrameFrames)
                    && HeadingReports.Add("failure-title-unsettled"))
                    Plugin.Logger?.LogInfo($"[DIAG] failure board held back: titleSource={titleSource}; pageActive={(page.gameObject.activeInHierarchy).ToString().ToLowerInvariant()}; missionTitlePresent={missionPresent.ToString().ToLowerInvariant()},missionTitleActive={missionActive.ToString().ToLowerInvariant()},missionTextPresent={missionTextPresent.ToString().ToLowerInvariant()}; waitingStatusPresent={(guestStatus != null).ToString().ToLowerInvariant()},waitingStatusActive={(guestStatus != null && guestStatus.gameObject.activeInHierarchy).ToString().ToLowerInvariant()},nativeLobbyVisible={nativeFooterVisible.ToString().ToLowerInvariant()},failureSequenceFinished={sequenceFinished.ToString().ToLowerInvariant()}; anchorActive={active.ToString().ToLowerInvariant()},alpha={(float.IsFinite(alpha) ? alpha.ToString("0.00") : "unreadable")},rawTextAlpha={(float.IsFinite(rawTextAlpha) ? rawTextAlpha.ToString("0.00") : "unreadable")},geometryMeasured={measured.ToString().ToLowerInvariant()},renderer={hasRenderer.ToString().ToLowerInvariant()}/{rendererEnabled.ToString().ToLowerInvariant()},rectTransform={hasRectTransform.ToString().ToLowerInvariant()},menuCamera={hasMenuCamera.ToString().ToLowerInvariant()},mainCamera={hasMainCamera.ToString().ToLowerInvariant()},opaqueReadings={Title.OpaqueFrames},sameFrameInvalidations={Title.SameFrameInvalidations}; native mission-title completion/geometry proof is not ready, so vanilla is retained.");
                return false;
            }
            bool titleKnown = float.IsFinite(titleBottom);
            if (!titleKnown)
            {
                ReportHeldBack("failure-title", "title-edge-unknown", "titleSource=" + titleSource + ",anchorSettled=" + anchorSettled);
                return false;
            }
            if (HeadingReports.Add("failure-native-anchor-ready"))
                Plugin.Logger?.LogInfo($"[DIAG] failure native anchor ready: titleSource={titleSource}; activeTitle={active.ToString().ToLowerInvariant()},alpha={(float.IsFinite(alpha) ? alpha.ToString("0.00") : "unreadable")},geometryMeasured={measured.ToString().ToLowerInvariant()},failureSequenceFinished={sequenceFinished.ToString().ToLowerInvariant()},edge={titleBottom:0},opaqueReadings={Title.OpaqueFrames},sameFrameInvalidations={Title.SameFrameInvalidations}; waiting-status remains a lower protection only.");

            bool protectWaitingStatus = false;
            try { protectWaitingStatus = guestStatus != null && guestStatus.gameObject.activeInHierarchy
                && !string.IsNullOrWhiteSpace(guestStatus.text); } catch { }
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

            if (GuestClient())
            {
                // 客机：本页没有「返回大厅」按钮，只有一行等待厅主的状态文字——而它本身就会被扫描当成一个
                // 受保护的下边界控件，所以 buttonMeasured 在客机上几乎恒为真，绝不能拿它当分支条件。
                // 上界 = 锚点（任务失败标题）实测底边 + 玩家配置的纵向偏移，再按 1080p 等比缩放补一个 gap；
                // 下界 = 参考分辨率等比缩放的固定页面下界。锚点就绪即落板，不数帧、不问页脚。
                float configuredOffset = Plugin.ResultsOffsetY.Value;
                if (!float.IsFinite(configuredOffset) || configuredOffset < 0f) configuredOffset = 0f;
                float guestAnchor = titleBottom + configuredOffset;
                if (!ResultsGuestPageGeometry.TryResolve(guestAnchor, Screen.width, Screen.height, MessageGap,
                    MinimumGuestStripHeight, out ResultsSafeArea guestArea))
                {
                    if (HeadingReports.Add("failure-guest-geometry-unusable"))
                        Plugin.Logger?.LogInfo($"[DIAG] failure board held back: the client page's fixed geometry is unusable this frame (anchorBottom={(float.IsFinite(titleBottom) ? titleBottom.ToString("0") : "unmeasurable")}, screen={Screen.width}x{Screen.height}); vanilla is retained and the page is measured again.");
                    return false;
                }
                top = guestArea.Top;
                bottom = guestArea.Bottom;
                footerSource = ResultFooterSource.ClientNoFooterSafeArea;
                // 量得到的原生控件只做事后下界收紧：把下界收到它自己上方，收紧后仍装得下最小板高才采纳。
                if (float.IsFinite(controls.FooterTop))
                {
                    float tightened = Math.Min(bottom, controls.FooterTop - MessageGap);
                    if (tightened > top && tightened - top >= MinimumGuestStripHeight) bottom = tightened;
                }
                if (HeadingReports.Add("failure-client-fixed-geometry"))
                    Plugin.Logger?.LogInfo($"[DIAG] failure footerSource=client-fixed-page-geometry: native lobbyVisible={nativeFooterVisible.ToString().ToLowerInvariant()}, anchor=settled-mission-title, nativeFooterMeasured={buttonMeasured.ToString().ToLowerInvariant()} (logged only), strip=({top:0}..{bottom:0}) of a {Screen.height} window; no interaction scan and no footer button is consulted for release.");
            }
            else
            {
                // 主机：与 1.19.14 完全一致。
                top = Math.Max(ResultsPlacement.Top(titleBottom, Plugin.ResultsOffsetY.Value, MessageGap), controls.BoardTop);
                if (buttonMeasured) bottom = Math.Min(buttonTop - MessageGap, Screen.height - 8f);
                else bottom = Screen.height - 8f;
            }
            // ---- the agreed band, on the failure page too ----------------------------------------------------
            //
            // Upper: the page's own mission-failed title edge. Lower: the button the host presses, or - on a client,
            // which has no button at all - the waiting-status text block, measured as the world-space text it is. The
            // existing figures are kept as *extra* walls (never loosened): the band may only pull the strip tighter or
            // start it higher, so a configured vertical offset and the 1.19.13 guest geometry both still hold.
            float waitingTextTop = float.NaN;
            try
            {
                if (guestStatus != null && guestStatus.gameObject.activeInHierarchy
                    && TryScreenRect(guestStatus, out LayoutRect waitingRect)) waitingTextTop = waitingRect.Y;
            }
            catch { }
            if (ResultsBounds.TryResolve(Screen.height, titleBottom, ResultBoundSource.TitleAnchor,
                    buttonMeasured ? buttonTop : float.NaN, waitingTextTop, controls.FooterTop, out ResultsBand pageBand))
            {
                ReportBand("failure", pageBand, float.NaN, waitingTextTop, _boardFloor);
                if (pageBand.Fits && footerSource != ResultFooterSource.BelowActions)
                {
                    if (pageBand.Top > top) top = pageBand.Top;
                    if (pageBand.Bottom < bottom) bottom = pageBand.Bottom;
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
                Plugin.Logger?.LogInfo($"[DIAG] failure strip: anchorSource={titleSource},messageBottom={titleBottom:0},titleAlpha={(float.IsFinite(alpha) ? alpha.ToString("0.00") : "unreadable")},opaqueReadings={Title.OpaqueFrames},sequenceFinished={sequenceFinished.ToString().ToLowerInvariant()},messageEdge={(measured ? "measured-this-frame" : "stable-mission-title-layout")},boardTop=({candidateBoard.Y:0}..{candidateBoard.Bottom:0}),blankAbove={blankAbove:0}px,blankBelow={blankBelow:0}px,footerTop={(float.IsFinite(buttonTop) ? buttonTop.ToString("0") : "unmeasurable")},waitingStatusProtected={protectWaitingStatus.ToString().ToLowerInvariant()},scanComplete={controls.Complete.ToString().ToLowerInvariant()},parentCoverage={controls.ParentCoverageComplete.ToString().ToLowerInvariant()},unmeasurable={controls.Unmeasurable},boardOverlapsControl={overlapsLobby.ToString().ToLowerInvariant()},screen={Screen.width}x{Screen.height}.");
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
                var canvasGroup = node.TryCast<CanvasGroup>();
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
            float anchor = float.IsFinite(Title.Edge) ? Title.Edge : FailureLayoutTitle.Edge;
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
                    if (inspectBoard && ResultInteractionStripPolicy.OverlapsBoard(rect, board, ResultBoardHold.ButtonClearance))
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
            tally.Hidden, tally.HiddenPageActions, reason, tally.Unmeasurable, belowActions);
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
            Camera camera = CM_PageBase.Camera;
            Canvas canvas = component.GetComponentInParent<Canvas>();
            bool overlay = canvas != null && canvas.renderMode == RenderMode.ScreenSpaceOverlay;
            if (canvas != null && !overlay && canvas.worldCamera != null) camera = canvas.worldCamera;
            if (!overlay && camera == null)
            {
                // The page's own camera is not always assigned on the frame the board attaches - the menu
                // camera comes up with the page - and the completion section's icons are world-space objects
                // with no canvas above them, so this used to be read as "unmeasurable" and the board fell back
                // to a fraction of the window, straight over them. A missing camera is looked for instead.
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
