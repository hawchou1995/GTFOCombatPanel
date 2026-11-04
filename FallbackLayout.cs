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
}

// One fallback band, in window pixels with y counted from the top of the window.
public readonly struct ResultFallbackPlan
{
    public readonly float Top, Bottom;
    public readonly float Opacity;
    public readonly ResultFallbackReason Reason;

    public ResultFallbackPlan(float top, float bottom, float opacity, ResultFallbackReason reason)
    {
        Top = top; Bottom = bottom; Opacity = opacity; Reason = reason;
    }

    public float Height { get { return Bottom > Top ? Bottom - Top : 0f; } }
    public bool Placed { get { return Height > 0f; } }
    public bool BelowControls { get { return Reason == ResultFallbackReason.BelowControls; } }
    public bool OverlapsControls { get { return Reason == ResultFallbackReason.OverlapsControls; } }

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
    {
        plan = ResultFallbackPlan.None;
        if (!float.IsFinite(screenHeight) || screenHeight <= ScreenMargin * 2f) return false;
        float limit = screenHeight - ScreenMargin;
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

        // 3. Nothing left that can hold the board's own floor. The band still stops at the highest wall the page drew -
        //    the board is drawn faint inside it, never over the waiting line - and only a page that measured no wall at all
        //    falls back to the window's foot.
        float covered = wall > top + 0.5f ? wall : limit;
        plan = new ResultFallbackPlan(top, covered, OverlapOpacity, ResultFallbackReason.OverlapsControls);
        return plan.Placed;
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
        if (!float.IsFinite(screenHeight) || screenHeight <= 0f) return false;
        if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0f || height <= 0f) return false;
        float bandTop = float.IsFinite(top) ? Math.Max(0f, top) : 0f;
        // The band's own top edge, and never above it: the `bottom` the caller passed is the fit's opacity decision, not a
        // placement bound, so it is not read here at all.
        float y = bandTop;
        float highest = screenHeight - height - 1f;
        if (!float.IsFinite(highest) || highest < 0f) highest = 0f;
        if (y > highest) y = highest;
        if (y < 0f) y = 0f;
        float x = (screenWidth - width) * 0.5f + (float.IsFinite(offsetX) ? offsetX : 0f);
        float widest = screenWidth - width;
        if (!float.IsFinite(widest) || widest < 0f) widest = 0f;
        if (x > widest) x = widest;
        if (x < 0f) x = 0f;
        rect = new LayoutRect(x, y, width, height);
        return true;
    }

    // Why this band was used, in the few words a live log can afford, with the readings that produced it.
    public static string Describe(ResultFallbackPlan plan, float screenHeight, float boardFloor)
    {
        string reason = plan.Reason == ResultFallbackReason.BelowControls ? "below-controls"
            : plan.Reason == ResultFallbackReason.ScreenSafeArea ? "screen-safe-area"
            : plan.Reason == ResultFallbackReason.OverlapsControls ? "overlaps-controls"
            : "none";
        return "reason=" + reason
            + ", band=(" + plan.Top.ToString("0") + ".." + plan.Bottom.ToString("0") + "), room=" + plan.Height.ToString("0") + "px"
            + ", opacity=" + plan.Opacity.ToString("0.00")
            + ", boardFloor=" + (float.IsFinite(boardFloor) ? boardFloor.ToString("0") : "unmeasured") + "px"
            + ", screen=" + Screen0(screenHeight);
    }

    private static string Screen0(float screenHeight)
        => float.IsFinite(screenHeight) ? "height " + screenHeight.ToString("0") : "unmeasured";
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