using System;

namespace HawChow.GTFOCombatPanel;

// ---------------------------------------------------------------------------------------------------
// FallbackLayout.cs - the board a result page gets when nothing about that page can be proven
// ---------------------------------------------------------------------------------------------------
//
// The defect this file answers: a host settlement page that was fully drawn, readable and pressable - the game's
// own title, the four prisoners, the artifact columns, the loadout cards and the button at the bottom - was left
// without the settlement board, for the whole page, and F8 could not bring it back. The strict path refuses a page
// whose own anchor it cannot prove (`ResultsDisplay.Placement`: the title gate, the completion edge, the measured
// footer, the interaction scan), and every one of those gates is a proof about the *page*. A page that never hands
// one over therefore never got a board, and the refusal was indistinguishable from a crash to the player.
//
// The rule is now fail-open, and it is written down in one place:
//
//   * A result page that is on the screen always gets a board. When the page's own geometry cannot be proven, the
//     board is placed from the screen's own safe area - the window minus an 8 px margin, minus a top edge that keeps
//     the table below whatever the page did show (and below nothing at all when it showed nothing readable).
//   * The safe area still yields to the page's controls. `ResultBoardBands.TryBelowActions` already carves the
//     measured control rectangles out of the page; the tallest band that can hold the board's own floor is used when
//     one exists, and the strip above the page's measured lower wall is used when it does not.
//   * A fallback board is never taken away again. When the page's controls move under it, it is moved and, if there
//     is no clear room left at all, drawn at `OverlapOpacity` over the page instead of disappearing. That is the one
//     place this mod deliberately accepts a partially covered vanilla element, and it is the trade the fail-open
//     rule makes: a table the player can read over a page they can still see, rather than an empty result screen.
//   * Nothing here is a timeout that promotes a guess to a proof. The strict gates keep their meaning; the fallback
//     is a separate, honestly labelled placement (`ResultFallbackReason`), and it is retried against the real page
//     every time the page is measured, so a page that starts handing its geometry over is placed strictly again.
//
// Everything in this file is free of Unity and of BepInEx, so all of it is asserted headlessly in
// tests/GTFOCombatPanel.Smoke.

// Which band of the fail-open rule produced a placement, so a log line and a regression test can name it.
public enum ResultFallbackReason
{
    None = 0,
    // The band under every control the page's own scan measured: the table steps around the page's actions.
    BelowControls = 1,
    // The strip from the screen's own top edge down to the page's measured lower wall (or the window's foot).
    ScreenSafeArea = 2,
    // No band could hold the board's own floor at all: the whole lower safe area is used and the board is drawn
    // faint over the page rather than not at all.
    OverlapsControls = 3,
    // 页面自己的条带（锚点 → 最高的一堵墙 / 这一页真实的下沿）就装得下板子的自有高度：这是与严格路径同一个
    // `ResultsBounds` 算术，1.19.40 起客机失败页也走它（`ResultFallbackBandPolicy`），所以兜底板与严格板落在
    // 同一条条带上，而不是被一行不可交互的状态文字压成小板。
    PageBand = 4,
}

// One fallback band, in window pixels with y counted from the top of the window.
public readonly struct ResultFallbackPlan
{
    public readonly float Top, Bottom;
    public readonly float Opacity;
    public readonly ResultFallbackReason Reason;
    // 1.19.22: an invented band that had to grow past a measured wall to hold the board's own floor, and which wall
    // it now crosses. `Crosses` carries the few words a live log can afford and is only filled when `Extended` is
    // true: the band is then sized from the board, never the board squeezed into the band.
    public readonly bool Extended;
    public readonly string Crosses;
    // 采纳 `PageBand` 时这一页自己的条带读数（两个边界和各自的来源），好让日志用和严格路径同一套名字说出它。
    public readonly ResultsBand Band;
    public ResultFallbackPlan(float top, float bottom, float opacity, ResultFallbackReason reason)
        : this(top, bottom, opacity, reason, false, string.Empty)
    {
    }

    public ResultFallbackPlan(float top, float bottom, float opacity, ResultFallbackReason reason, bool extended, string crosses)
    {
        Top = top; Bottom = bottom; Opacity = opacity; Reason = reason;
        Extended = extended; Crosses = crosses ?? string.Empty; Band = ResultsBand.None;
    }

    // The page's own band, carried with the plan so one log line can name both edges and the fact each came from.
    public ResultFallbackPlan(float top, float bottom, float opacity, ResultFallbackReason reason, ResultsBand band)
    {
        Top = top; Bottom = bottom; Opacity = opacity; Reason = reason;
        Extended = false; Crosses = string.Empty; Band = band;
    }

    public float Height { get { return Bottom > Top ? Bottom - Top : 0f; } }
    public bool Placed { get { return Height > 0f; } }
    public bool BelowControls { get { return Reason == ResultFallbackReason.BelowControls; } }
    public bool OverlapsControls { get { return Reason == ResultFallbackReason.OverlapsControls; } }
    // The page's own band answered, not an invented one: the plan carries the page's band and its sources.
    public bool PageBand { get { return Reason == ResultFallbackReason.PageBand; } }

    public static ResultFallbackPlan None { get { return default; } }
}

// When the fail-open rule may stand in for the strict one.
//
// Two things answer yes and only two: the player asked for it with F8 (`forced`), or the strict placement has been
// refused on `MissBudget` frames in a row. The budget is a frame count of *refusals*, not a timeout after which an
// unmeasured edge counts as measured - the strict gates are unchanged, and a page that starts proving its own
// geometry is placed strictly again on the next measurement. It is bounded so a page still revealing itself for a
// couple of frames is not drawn twice, and it is short enough that a player never sees a result page without the
// table for more than about a second.
public static class ResultFallbackPolicy
{
    public const int MissBudget = 60;

    public static bool ShouldFallback(int misses, bool forced) => forced || misses >= MissBudget;

    // The same budget, asked the safety question the budget itself cannot answer.
    //
    // `ShouldFallback` says the page has been refused often enough to be worth a board drawn where its own geometry
    // could not be proven. That is a statement about *waiting*, never about what stands under the band: the fail-open
    // band is invented from the window and from the controls the scan did read, so a control the scan could not see
    // (structure incomplete, action parents uncovered) or could not place (an active control whose rectangle was the
    // zero box) is a hole the invented band can land on. Sixty refusals are not evidence that the page has nothing
    // interactive left to protect.
    //
    // `pageReadable` is that evidence, and it comes from the page's own measurement, not from a count:
    // `controls.Complete && controls.ParentCoverageComplete && controls.Unmeasurable == 0`. When it is false, the
    // page keeps the vanilla screen, one line per page says why, and the page is re-measured at `ResultRetryCadence`'s
    // existing bounded cadence - a refusal that never becomes a board, rather than a board that may cover a control
    // nobody could read. The player's own F8 (`forced`) is still answered: that press is the request this path exists
    // for, and it is not a budget deciding anything.
    // The marker the fail-open placement prefixes its refusal with when the page's own scan could not read it, so the
    // single line a page prints can name that fault instead of the generic "no band available".
    public const string UnreadablePage = "page-unreadable";

    // 这一页被扫描看全了吗：结构完整、action 父节点覆盖完整、没有任何 active 却量不到矩形的控件。
    // inactive 控件不进这里——它们被扫描记为 hidden，`Complete` 照旧为真，那才是「页面把控件关掉了」。
    public static bool PageReadable(bool scanComplete, bool parentCoverageComplete, int unmeasurableControls)
        => scanComplete && parentCoverageComplete && unmeasurableControls == 0;

    public static bool MayAnswer(int misses, bool forced, bool pageReadable)
        => forced || (pageReadable && misses >= MissBudget);
}

// The arithmetic of the fallback band, and the placement of a board inside it.
public static class ResultFallbackLayout
{
    // The margin every placement in this mod already keeps from the window's own edges.
    public const float ScreenMargin = 8f;
    // The gap the failure page keeps between its own message and the board, reused as the fallback's own gap so a
    // fallback board stands exactly where a strictly placed one would have.
    public const float AnchorGap = 16f;
    // Where the board starts when the page handed no anchor at all: low enough to clear a title band on every
    // resolution this game runs at, high enough to leave the table the middle of the window.
    public const float TopFraction = 0.14f;
    // The opacity a board is drawn at when nothing on the page is clear enough to stand in. The vanilla page stays
    // readable through it, and the table stays readable over it.
    public const float OverlapOpacity = 0.55f;
    // A band shorter than this is not a band: the board would be a single row of pixels between two elements.
    public const float MinimumRoom = 48f;
    // The clearance the per-repaint overlap check keeps above a control (`ResultBoardHold.ButtonClearance`).
    public const float ControlClearance = 6f;

    // The top edge a board takes when the page handed no anchor: a fraction of the window, clamped inside the margins.
    public static float SafeTop(float screenHeight)
    {
        if (!float.IsFinite(screenHeight) || screenHeight <= ScreenMargin * 2f) return ScreenMargin;
        float fraction = screenHeight * TopFraction;
        float limit = screenHeight - ScreenMargin - MinimumRoom;
        if (limit < ScreenMargin) limit = ScreenMargin;
        if (!float.IsFinite(fraction) || fraction < ScreenMargin) return ScreenMargin;
        return fraction > limit ? limit : fraction;
    }

    // The band the fallback board may use.
    //
    // `anchorBottom` is the best reading the page could give of its own upper anchor and may be NaN - that is the
    // case this whole file exists for. `footerTop` is the highest measured lower wall (the button the player has to
    // press) and `bandTop`/`bandBottom` the free band the interaction scan carved out of the page; both may be NaN
    // when the scan measured nothing. `boardFloor` is the height the board cannot be drawn under at any readable
    // glyph size (`ResultsFontGrowth.MinimumBoardHeight`), which is what separates a tight band from an impossible
    // one.
    public static bool TryResolve(float screenHeight, float anchorBottom, float footerTop, float bandTop, float bandBottom,
        float boardFloor, out ResultFallbackPlan plan)
        => TryResolve(screenHeight, anchorBottom, footerTop, float.NaN, bandTop, bandBottom, boardFloor, out plan);

    // The same rule with the client's waiting block as a second lower wall.
    //
    // On a page whose leave button is not measurable, that block is the only wall there is; a fallback band that ignores it
    // runs the board straight over the line the player is reading - the one thing this whole file exists to prevent. Both
    // walls are pulled up by the same clearance and the higher one wins, exactly as `ResultsBounds` does for the strict
    // path. Only when neither wall could be measured does the band reach the window's foot.
    public static bool TryResolve(float screenHeight, float anchorBottom, float footerTop, float footerTextTop,
        float bandTop, float bandBottom, float boardFloor, out ResultFallbackPlan plan)
        => TryResolve(screenHeight, anchorBottom, footerTop, footerTextTop, bandTop, bandBottom, boardFloor, float.NaN,
            out plan);

    // The same rule, with the page's own lower edge as the band's foot.
    //
    // `pageFloor` is where the page itself stops drawing (`ResultsGuestPageGeometry.PageFloor`: the 1080p
    // reference foot scaled by height, 1429.33 on a 2560x1440 window). A client failure page reads its lower bound
    // from that geometry and from nothing else (`ResultClientFailureBand`), so the fail-open arithmetic has to end at
    // the same edge; otherwise the same page gets two bands - the page's 593 px under the strict rule and the
    // window's own 1432 px under this one. NaN - every other page - keeps the window's foot and changes nothing.
    public static bool TryResolve(float screenHeight, float anchorBottom, float footerTop, float footerTextTop,
        float bandTop, float bandBottom, float boardFloor, float pageFloor, out ResultFallbackPlan plan)
    {
        plan = ResultFallbackPlan.None;
        if (!float.IsFinite(screenHeight) || screenHeight <= ScreenMargin * 2f) return false;
        float limit = screenHeight - ScreenMargin;
        if (float.IsFinite(pageFloor) && pageFloor > ScreenMargin && pageFloor < limit) limit = pageFloor;
        float floor = float.IsFinite(boardFloor) && boardFloor > 0f ? boardFloor : 0f;

        float top = float.IsFinite(anchorBottom) ? Math.Max(0f, anchorBottom) + AnchorGap : SafeTop(screenHeight);
        if (!float.IsFinite(top)) top = SafeTop(screenHeight);
        if (top < ScreenMargin) top = ScreenMargin;
        // The top is never allowed to eat the whole window: a page whose own anchor sits at the foot of the screen
        // still leaves the board the smallest band this rule is willing to place in.
        float topCeiling = limit - MinimumRoom;
        if (topCeiling < ScreenMargin) topCeiling = Math.Max(0f, screenHeight * 0.5f);
        if (top > topCeiling) top = topCeiling;

        // The highest lower wall this page offered, in window pixels, or the window's foot when it offered none.
        float wall = limit;
        if (float.IsFinite(footerTop)) wall = Math.Min(wall, footerTop - ControlClearance);
        if (float.IsFinite(footerTextTop)) wall = Math.Min(wall, footerTextTop - ControlClearance);
        if (!float.IsFinite(wall)) wall = limit;

        // 1. The free band the page's own scan carved out of its controls, when it can hold the board's own floor.
        if (float.IsFinite(bandTop) && float.IsFinite(bandBottom))
        {
            float bandRoom = Math.Min(bandBottom, limit) - Math.Max(bandTop, 0f);
            if (bandRoom > 0.5f && bandRoom + 0.5f >= floor && bandTop >= top - 0.5f)
            {
                plan = new ResultFallbackPlan(Math.Max(0f, bandTop), Math.Min(bandBottom, limit), 1f,
                    ResultFallbackReason.BelowControls);
                return plan.Placed;
            }
        }

        // 2. The strip between the page's upper edge and the first wall the page really draws - the button the player has
        //    to press, or the waiting line that stands where it would be.
        if (wall > top + 0.5f && wall - top + 0.5f >= floor)
        {
            plan = new ResultFallbackPlan(top, wall, 1f, ResultFallbackReason.ScreenSafeArea);
            return plan.Placed;
        }

        // 3. Nothing left that can hold the board's own floor. The invented band is sized from the board, never the
        //    board squeezed into the band: it reserves the board's floor plus the clearance, so when that reaches past
        //    the highest measured wall the band is *extended* across it and the board is drawn faint over whatever it
        //    now crosses - reported as such (`band=extended, covers=...`), never produced silently. Only a page that
        //    measured no wall at all (and therefore keeps the window's foot) already stops past enough of the window
        //    for the board to stand full-height. 1.19.22: this is what makes a forced F8 end in a drawn board - the
        //    old tier answered a shorter-than-floor band with a band of the same height, and the fit then refused it
        //    for ever.
        float need = floor + ControlClearance;
        float span = top + need;
        float covered = wall > top + 0.5f ? wall : limit;
        bool extended = covered < span;
        string crosses = extended ? WallName(wall, footerTop, footerTextTop, pageFloor) : string.Empty;
        if (extended) covered = span;
        plan = new ResultFallbackPlan(top, covered, OverlapOpacity, ResultFallbackReason.OverlapsControls, extended, crosses);
        return plan.Placed;
    }

    // Which lower edge produced the band's wall, in the few words the extension line prints when the band grows past it.
    private static string WallName(float wall, float footerTop, float footerTextTop, float pageFloor)
    {
        // A page floor the band really used to win the name when it is the edge the band was given: on a client failure
        // page that is the 1429 the strict path uses, and calling it "the window's foot" would be the same kind of
        // half-truth this release is fixing. NaN - every other page - cannot match and keeps the old words.
        if (float.IsFinite(pageFloor) && Math.Abs(wall - pageFloor) <= 0.5f) return "this page's own lower edge";
        // The clearance the wall was pulled up by is part of the comparison, so the name describes the wall the band
        // really stopped at before the extension and never a half-figure.
        if (float.IsFinite(footerTextTop) && Math.Abs(wall - (footerTextTop - ControlClearance)) <= 0.5f)
            return "the waiting-status text";
        if (float.IsFinite(footerTop) && Math.Abs(wall - (footerTop - ControlClearance)) <= 0.5f)
            return "the measured footer control";
        return "the window's foot";
    }

    // The board inside a band. It hangs from the band's *top* edge, always: a board taller than the band keeps the band's
    // top and runs past its lower edge instead of sliding up over the page above it. Sliding up is what put a board on
    // top of a client page's sector-icon row whenever the band came back shorter than the table - it crossed the one
    // bound the band exists to keep. The caller draws such a board at `OverlapOpacity`, and the fit reports the overflow.
    public static bool TryPlaceInBand(float screenWidth, float screenHeight, float top, float bottom, float width,
        float height, float offsetX, out LayoutRect rect)
    {
        rect = default;
        if (!float.IsFinite(screenWidth) || screenWidth <= 0f) return false;
        if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0f || height <= 0f) return false;
        float bandTop = float.IsFinite(top) ? Math.Max(0f, top) : 0f;
        // The band is a *band*: its foot binds exactly as its top does (review #4 - this used to ignore `bottom` entirely and
        // slide a board that did not fit up to `screenHeight - height`, i.e. it guessed the window's bottom edge and drew a
        // board the page had refused, which the next repaint then had to take away again). A board that does not fit inside
        // the band is refused here so the caller can fit a smaller legal grid into the *same* band.
        float bandBottom = float.IsFinite(bottom) ? bottom : float.NaN;
        if (!float.IsFinite(bandBottom) || !(bandBottom > bandTop)) return false;
        if (height > bandBottom - bandTop + 0.5f) return false;
        if (!float.IsFinite(screenHeight) || bandTop + height > screenHeight - 8f) return false;
        float y = bandTop;
        float x = (screenWidth - width) * 0.5f + (float.IsFinite(offsetX) ? offsetX : 0f);
        float widest = screenWidth - width;
        if (!float.IsFinite(widest) || widest < 0f) widest = 0f;
        if (x > widest) x = widest;
        if (x < 0f) x = 0f;
        rect = new LayoutRect(x, y, width, height);
        return true;
    }

    // Why this band was used, in the few words a live log can afford, with the readings that produced it. An extended
    // band says so in the words the next log has to carry: `band=extended, covers=<what it now crosses>` next to
    // `need=` (the grid height the board requires at its floor) and the band the board was really given.
    public static string Describe(ResultFallbackPlan plan, float screenHeight, float boardFloor)
    {
        string reason = plan.Reason == ResultFallbackReason.BelowControls ? "below-controls"
            : plan.Reason == ResultFallbackReason.ScreenSafeArea ? "screen-safe-area"
            : plan.Reason == ResultFallbackReason.OverlapsControls ? "overlaps-controls"
            : plan.Reason == ResultFallbackReason.PageBand ? "page-band"
            : "none";
        string need = (float.IsFinite(boardFloor) ? boardFloor.ToString("0") : "unmeasured") + "px";
        if (plan.Extended)
        {
            return "reason=" + reason
                + ", band=extended, covers=" + (plan.Crosses.Length > 0 ? plan.Crosses : "a measured wall")
                + ", need=" + need
                + ", band=(" + plan.Top.ToString("0") + ".." + plan.Bottom.ToString("0") + ")"
                + ", room=" + plan.Height.ToString("0") + "px"
                + ", opacity=" + plan.Opacity.ToString("0.00")
                + ", boardFloor=" + need
                + ", screen=" + Screen0(screenHeight);
        }
        return "reason=" + reason
            + ", band=(" + plan.Top.ToString("0") + ".." + plan.Bottom.ToString("0") + "), room=" + plan.Height.ToString("0") + "px"
            + ", opacity=" + plan.Opacity.ToString("0.00")
            + ", boardFloor=" + need
            + ", screen=" + Screen0(screenHeight);
    }

    private static string Screen0(float screenHeight)
        => float.IsFinite(screenHeight) ? "height " + screenHeight.ToString("0") : "unmeasured";
}


// ---------------------------------------------------------------------------------------------------
// Where the fail-open board stands: the page's own band first, the invented ones only after it
// ---------------------------------------------------------------------------------------------------
//
// The defect this file answers, found by an independent review: `ResultsDisplay.FallbackPlacement` handed the
// measured "waiting for the host" line's top edge (1228 on the recorded 2560x1440 client page) to the band arithmetic
// as a lower wall, and handed the interaction scan's `FooterTop` - which includes that same line - as a footer
// control. The page's own rule had already stopped doing that (`ResultClientFailureBand`: the line is a fact, the wall
// is a control the player can press, the foot is the fixed page geometry at 1429), so one page carried two bands:
// 593 px under the strict rule and 370 px under the fail-open one. At 370 px thirty rows fit an 8 px glyph, so the
// fallback board was drawn small, re-fitted when the line animated, or stayed small for the whole page - while the
// strict path would have drawn a 12 px table in the same place.
//
// The rule, in one place, for both paths:
//
//   * `ResultPageWalls` says which readings of a page may bound a board at all (`ResultPageLowerBoundPolicy`): on a
//     client failure page the status text is not one of them and the page's fixed foot is the floor; on every other
//     page nothing changes.
//   * The page's own band is asked first - the same `ResultsBounds` arithmetic the strict path uses, so both paths
//     answer the same numbers for the same page - and it is only accepted when it can hold the board's own floor
//     (`ResultFallbackTier`), which is what keeps a board from being squeezed into a strip nobody could read.
//   * Only then do the invented tiers answer (`ResultFallbackLayout`), with the page's own floor as their foot, so a
//     page that cannot hold the board still gets one instead of nothing.
//
// Nothing here relaxes what the fail-open path may cover: every control the scan measured is still a wall
// (`ResultPageWalls.PressableTop`) and the free band it carved is still offered when the page's own strip is too
// short, so a button the player has to press is stepped around exactly as before. The one reading that stops bounding
// a board is the line the page draws and the player only reads.
public static class ResultFallbackBandPolicy
{
    public static bool TryResolve(float screenHeight, float anchorBottom, ResultBoundSource anchorSource,
        ResultPageWalls walls, float measuredControlTop, float bandTop, float bandBottom, float boardFloor,
        out ResultFallbackPlan plan)
    {
        plan = ResultFallbackPlan.None;
        // 1. The page's own band, with the same two edges and the same clearance the strict path uses.
        if (ResultsBounds.TryResolve(screenHeight, anchorBottom, anchorSource, walls.PressableTop, walls.FooterTextTop,
                walls.FooterTextSource, measuredControlTop, out ResultsBand pageBand) && pageBand.Fits)
        {
            float bottom = walls.CapBottom(pageBand.Bottom);
            float room = bottom - pageBand.Top;
            if (room > 0.5f && ResultFallbackTier.AcceptsPageBand(room, boardFloor))
            {
                pageBand = new ResultsBand(pageBand.Top, bottom, pageBand.TopSource, pageBand.BottomSource, pageBand.Clamped);
                plan = new ResultFallbackPlan(pageBand.Top, pageBand.Bottom, 1f, ResultFallbackReason.PageBand, pageBand);
                return true;
            }
        }
        // 2. The page's band cannot hold the board's own floor (or it could not be resolved at all): the invented
        //    tiers answer, stepping around every control the scan measured and stopping at the page's own foot.
        if (!ResultFallbackLayout.TryResolve(screenHeight, anchorBottom, walls.PressableTop, walls.FooterTextTop,
                bandTop, bandBottom, boardFloor, walls.PageFloor, out ResultFallbackPlan invented)) return false;
        plan = invented;
        return true;
    }
}

// What one measurement of a result page ended in.
//
// `PlacedStrict` is the only outcome that may clear the refusal count: it means `Fit` really put a grid on the page, not
// that `Placement` answered "the page offered a strip". A strip can be non-empty and still shorter than the board's own
// floor - a room of 100 px against 30 rows x 3 px x 1.5 em = 135 px is exactly that - and treating that as a success is how
// the board came to be refused for ever on a page whose placement was fine, with F8 unable to help because the fallback
// budget was never consulted.
public enum ResultPlacementOutcome { Deferred = 0, PlacedStrict = 1, PlacedFallback = 2 }

public static class ResultPlacementPolicy
{
    // One more refused measurement, saturated: the count never wraps and never needs a reset to stay meaningful.
    public static int Spend(int misses) => misses < int.MaxValue ? misses + 1 : misses;

    // What this frame's attempt means for the frame itself.
    //
    // `placed` is the fit's own answer. When it is false the frame drew nothing, and the fail-open path takes over on that
    // very frame as soon as F8 asked for it or the refusal budget is spent - never one frame later, and never conditioned
    // on a second measurement.
    public static ResultPlacementOutcome Decide(bool placed, int misses, bool forced)
    {
        if (placed) return ResultPlacementOutcome.PlacedStrict;
        return ResultFallbackPolicy.ShouldFallback(misses, forced)
            ? ResultPlacementOutcome.PlacedFallback
            : ResultPlacementOutcome.Deferred;
    }
}

// Whether the fail-open board may use the page's own band, or the reduced-opacity tier has to answer instead.
//
// A band that cannot hold the board's own floor is not a band: placed in it the board would be taller than the page allows,
// and because `ResultFallbackLayout.TryPlaceInBand` never slides a board up over the page above it, it would either cross
// the footer or sit on the icon row. Such a band is refused here and the caller falls back to the tier that says it stands
// over the page (opacity 0.55) instead.
public static class ResultFallbackTier
{
    public static bool AcceptsPageBand(float bandHeight, float boardFloor)
        => float.IsFinite(bandHeight) && float.IsFinite(boardFloor) && boardFloor > 0f && bandHeight + 0.5f >= boardFloor;
}

// Whether a failure page's lower bound has been observed at all.
//
// The recorded failure page (`LogOutput.log` 31794-31809, 2560x1440, client) is the reason this exists: a band of
// 890..1429 - the screen's own foot - was answered while the page's own button had not been measured yet, a 1385-tall
// board was drawn into it, and the button then appeared at 1240. Two complete scans that found no pressable control are
// not evidence that the page has no wall (`TIP-20261114-8bd8fe92`: a later control can still arrive); they are evidence
// that the wall has not been seen. This policy says so for *any* failure page, not only the client one - the client
// branch already had its own voice (`ResultClientPageActionPolicy`), which is what left the host path able to invent a
// band from the window.
//
// 1.19.44 (the TOA Pure client failure page, `LogOutput` 12642-12676): this policy was **dead on the one page it was
// written for**. `ResultsDisplay.FallbackPlacement` decided "a wall was measured" from
// `float.IsFinite(walls.PageFloor)` as well - and `ResultPageLowerBoundPolicy` gives every client failure page a
// `PageFloor` (the 1080p reference foot scaled by height, 1429.33 on 2560x1440). A reading that is *always* finite
// therefore made `wallMeasured` always true, the refusal above never happened, and after `MissBudget` (60) refusals the
// fail-open band was invented from the screen: band (488..1429), a 900-tall board, a real control at 1171 that arrived a
// frame later and `ResultBoardHold.TryHold` refusing to lift a board that was already at the band's top - so the board
// spent the rest of the page 217 px over the footer at 0.55 opacity.
//
// 1.19.45 (user correction, and it settles what this policy may treat as a wall):
//
//   * `WallMeasured` is the one definition of "this page really bounded its board", and the page's own screen-derived
//     foot is **not** one of the readings it accepts. A client failure page's foot is a fact about the screen, not a
//     measurement of anything the page draws or the player presses.
//   * The client failure page's "waiting for the host" line **is** a wall, and it is the ordinary one: it is the thing
//     the page really draws under the board, exactly as the host's "leave expedition" button is under the host's board
//     (`ResultPageLowerBoundPolicy` hands it over as `FooterTextTop`). A page whose wall is that line therefore needs no
//     other evidence, and the first board is fitted above it.
//   * What no frame count and no timeout may do is turn "no wall seen **at all**" into "this page has no wall". On a
//     client failure page that is the only honest answer: waiting longer says nothing about what stands under the band,
//     so the vanilla page is kept and the page is re-measured at the existing bounded cadence (`ResultRetryCadence`)
//     instead of a board being invented from the screen. F8 is still the player's own answer (`Forced`), and a guest
//     page it answers is logged as a wall that was never observed, with the band's foot named as the screen's own
//     geometry.
//   * The native failure sequence (`FailureSequenceReadiness`) is deliberately **not** an answer here. It says the
//     page's own script finished laying itself out; it does not say where the wall is, and a band invented from the
//     screen foot on the strength of it is the very reading this release removes.
public static class ResultFailureWallPolicy
{
    public const string Unobserved = "wall-not-observed";
    public const int UnobservedBudget = ResultFinalPolicy.MaximumWaitFrames;

    // Which readings of *this* page really bound a board below it.
    //
    // `walls` already carries the page's own semantics (`ResultPageLowerBoundPolicy`): on a client failure page
    // `FooterTextTop` is the waiting-status line's own laid-out top (a wall, and the ordinary one) and `PressableTop` is
    // the highest control the player can really press, while `PageFloor` is only the band's cap. `namedControlTop` is
    // the caller's own named-button reading (`ResultsDisplay.LobbyButtonTop`), `scanPressableTop` the scan's pressable
    // read and `scanFooterTextTop` the scan's footer read - the last one only counts on a page where that footer is a
    // wall the walls struct does not already carry (a host failure page, a success page). One definition, used by the
    // production placement and driven directly by the regression suite.
    public static bool WallMeasured(in ResultPageWalls walls, float namedControlTop, float scanPressableTop,
        float scanFooterTextTop)
        => float.IsFinite(walls.PressableTop) || float.IsFinite(walls.FooterTextTop)
            || float.IsFinite(namedControlTop) || float.IsFinite(scanPressableTop)
            || (!walls.GuestFailure && float.IsFinite(scanFooterTextTop));

    // Withhold the invented band while the page has shown no lower bound at all.
    //
    // A host failure page keeps the bounded give-up it has always had (`UnobservedBudget` frames, and the answer says it
    // was a give-up). A client failure page is answered by a measured wall - never by a frame count and never by a
    // timeout: waiting longer is not evidence about what stands under the band. Only the player's own F8 request
    // (`Forced`, handled by the caller) may answer such a page without one.
    public static bool Waits(bool wallMeasured, int framesWithoutWall, bool guestFailure)
        => !wallMeasured && (guestFailure || framesWithoutWall < UnobservedBudget);

    // The same question as `Waits`, answered the other way round, for the callers that ask it positively.
    public static bool Answer(bool wallMeasured, int framesWithoutWall, bool guestFailure)
        => !Waits(wallMeasured, framesWithoutWall, guestFailure);

    // F5: the note that rides on a line which answers a band without a measured lower bound. The give-up is bounded, and
    // a successful fallback is not proof that the page has a wall: the line has to keep saying which one it was, so a
    // reader of the log can tell an answered page from a page whose geometry was proven. Empty whenever the wall was seen.
    //
    // On a client failure page the only answer without a measured wall is the player's own F8 request, so the note says
    // exactly that and names the band's foot for what it is.
    public static string AnswerNote(bool wallMeasured, int framesWithoutWall, bool guestFailure)
    {
        if (wallMeasured) return string.Empty;
        if (guestFailure)
            return ", wallUnobserved=true (answered by the player's own F8 request while this page has measured no wall at all - neither the waiting-status line's own rectangle nor a pressable control: the band's foot is the page's own screen-derived geometry, never a measured wall, and no frame count or timeout may answer this page)";
        return framesWithoutWall < UnobservedBudget
            ? string.Empty
            : ", wallUnobserved=true (answered after " + framesWithoutWall + " frames without a measured lower bound: a bounded give-up, not the page's proven geometry)";
    }
}

// Whether one text of a result page's footer subtree is the waiting block the board must stay above.
//
// The block is only a wall while the page is really drawing it: a TextMeshPro that is switched off, or one at zero alpha
// inside the same subtree, is a layout leftover rather than something the player reads - counting it would lift the board
// above a line that is not on the screen and shorten the band for nothing. The failure page has always been measured this
// way (`m_returnToLobby_text` only while active and carrying text); this is the same rule for the success page's subtree.
public static class ResultFooterTextPolicy
{
    public const float VisibleAlpha = 0.02f;

    // An alpha nobody could read is not evidence that the text is invisible. `ControlAlpha` treats an unreadable alpha the
    // same way - it protects the control instead of dropping it - and the failure page has always counted its waiting line
    // on the object's own state rather than on a number. So: switched off means "not a wall", a readable alpha at or below
    // `VisibleAlpha` means "not a wall", and an unreadable one keeps the wall exactly where it was.
    public static bool Counts(bool activeInHierarchy, float alpha)
        => activeInHierarchy && (!float.IsFinite(alpha) || alpha > VisibleAlpha);
}
// The fail-open board's opacity, re-derived on every repaint.
//
// A board that fitted its band when it was placed can be over that band a moment later - the page's own controls move, the
// band shortens - and a board that has been moved back inside a band must come back to full strength. The plan's own
// opacity is the floor of the answer: a band that was only ever the "standing over the page" tier stays faint whatever the
// board does inside it, because the page underneath it is still the page the player is reading.
public static class ResultFallbackOpacity
{
    public static float Resolve(float plannedOpacity, float boardBottom, float bandTop, float bandBottom)
    {
        float planned = float.IsFinite(plannedOpacity) && plannedOpacity > 0f ? Math.Min(1f, plannedOpacity) : 1f;
        bool overBand = float.IsFinite(bandBottom) && float.IsFinite(bandTop) && bandBottom > bandTop && boardBottom > bandBottom + 0.5f;
        return overBand ? Math.Min(planned, ResultFallbackLayout.OverlapOpacity) : planned;
    }
}

// The fail-open floor ladder.
//
// The measured floor is what the page would need for a readable board. A page that cannot offer that much used to be answered
// with "no band can hold the board" for ever; each fail-open attempt that ends without a board now lowers the scale by one
// step, so the next frame accepts a shorter band and builds a smaller table. It stops at the smallest grid this build can
// draw - a band shorter than that cannot hold a board at all, and the caller says so once instead of looping.
public static class ResultFallbackLadder
{
    public const float StepScale = 0.7f;
    public const float MinimumScale = 0.05f;

    public static float Next(float scale)
    {
        float current = float.IsFinite(scale) && scale > 0f ? scale : 1f;
        float next = current * StepScale;
        return next < MinimumScale ? MinimumScale : next;
    }

    public static float Floor(float measuredFloor, float smallestDrawable, float scale)
    {
        float smallest = float.IsFinite(smallestDrawable) && smallestDrawable > 0f ? smallestDrawable : 1f;
        float measured = float.IsFinite(measuredFloor) && measuredFloor > 0f ? measuredFloor : smallest;
        float current = float.IsFinite(scale) && scale > 0f ? Math.Min(1f, scale) : 1f;
        float scaled = measured * current;
        return scaled < smallest ? smallest : scaled;
    }
}