using System;

namespace HawChow.GTFOCombatPanel;

// ---------------------------------------------------------------------------------------------------
// ResultsBounds.cs - the settlement board's agreed band: an icon row above, a footer below
// ---------------------------------------------------------------------------------------------------
//
// The defect this file answers, as a player saw it on a client's success page (2560x1440, photographed):
// the board started around 575/1080 down a page whose sector-icon row ends around 250/1080 - a band of empty
// page above it and a table squeezed into the lower half - and its lower edge ran straight through the
// native "waiting for the host" line and the "send the prisoners back to the lobby" line, because a client
// page has no pressable footer and the old rule simply ran the board to the foot of the window.
//
// The rule, agreed and not to be loosened again:
//
//   upper  the bottom edge of the "sectors cleared" icon row, *including its label*, plus a fixed clearance.
//          Measured when it is measurable; the last trusted value when it is not; and only when neither
//          exists does the band start from a fraction of the window. The board hangs from this edge - it is
//          top-aligned, never centred - so a taller band makes a taller table, not a table further down.
//   lower  the top edge of the footer this machine can actually see, minus the same clearance: the host's
//          "leave expedition" button, or - on a client, where that button is the host's decision - the
//          waiting-status text block, which is a world-space TextMeshPro object and is therefore measured
//          through the menu camera exactly like the icons (`ResultsDisplay.TryScreenRect`).
//   band   `[upper, lower]`. The table is fitted into it: its glyph size and row height come from the band
//          height (`ResultsBandFit`), the row count is fixed, and a taller band gives a taller, more
//          readable table. Nothing is ever drawn outside it.
//
// Everything here is free of Unity and of BepInEx, so all of it is asserted headlessly in
// tests/GTFOCombatPanel.Smoke.

// Which measured fact produced a bound, so a log line and a regression test can name it.
public enum ResultBoundSource
{
    None = 0,
    // The bottom of the sector-icon row (its label included): the success page's upper bound.
    IconRow = 1,
    // The same edge, from the last frame that could measure it: a page still revealing itself keeps the band
    // it had instead of falling back to a fraction of the window.
    LastTrustedIconRow = 2,
    // A page's own title edge - the failure page's mission-failed message - as its upper bound. 1.19.47 no longer
    // takes the failure page's band from it (a caption is not completion information, `ResultAutoBoardContract`); it
    // survives for the callers that still hand it over, and it is still an accepted page-measured top.
    TitleAnchor = 3,
    // Nothing measurable at all: a fraction of the window, used only when both of the above are absent.
    ScreenSafeArea = 4,
    // The control the player has to press (the host's "leave expedition" button).
    FooterControl = 5,
    // The client's waiting-status text block, which stands where that button would be.
    FooterText = 6,
    // The same wall read from the leave-expedition control's own laid-out rectangle, because this build draws no
    // TextMeshPro of its own for the waiting line. Named apart so a log line can tell "the line the player reads"
    // from "where that line would be".
    FooterTextControl = 9,
    // The failure page's own upper edge: the window's safe top margin, the one bound the newest user contract allows
    // there. The native "mission failed" title is a caption rather than completion information, so it no longer sets
    // the band's top and no longer spends the table's glyph size - see `ResultAutoBoardContract`.
    ScreenTopMargin = 10,
    // Any other interaction the page's own scan measured: it may shorten the band, never lengthen it.
    MeasuredControl = 7,
    // Nothing measurable below: the window's own foot margin.
    ScreenFoot = 8,
}

// One band, in window pixels with y counted from the top of the window.
public readonly struct ResultsBand
{
    public readonly float Top, Bottom;
    public readonly ResultBoundSource TopSource, BottomSource;
    // The band the page offered was shorter than `ResultsBounds.MinimumBand`, so its upper edge was slid up to keep
    // the lower one: the footer is the promise that may not be broken, and the board is drawn smaller instead.
    public readonly bool Clamped;

    public ResultsBand(float top, float bottom, ResultBoundSource topSource, ResultBoundSource bottomSource, bool clamped)
    {
        Top = top; Bottom = bottom; TopSource = topSource; BottomSource = bottomSource; Clamped = clamped;
    }

    public float Height { get { return Bottom > Top ? Bottom - Top : 0f; } }
    public bool Fits { get { return Height > 0f; } }
    public static ResultsBand None { get { return default; } }
}

public static class ResultsBounds
{
    // The reference the design clearance is written in, and the design clearance itself: a 16 px gap at 1080p.
    public const float ReferenceHeight = 1080f;
    public const float ClearanceDesign = 16f;
    // The margin every placement in this mod keeps from the window's own edges.
    public const float ScreenMargin = 8f;
    // A band shorter than this is not a band: the upper edge is slid up so the lower one still holds.
    public const float MinimumBand = 48f;
    // Where a band starts when the page handed no anchor at all: below the title block on every resolution this game
    // runs at, and high enough that the table can still grow downwards into the page.
    public const float UnmeasuredTopFraction = 0.10f;

    // The clearance, in the pixels of the window the board is drawn in: a design value that scales with the height,
    // so a 4K page keeps the same look as a 1080p one.
    public static float Clearance(float screenHeight)
        => float.IsFinite(screenHeight) && screenHeight > ReferenceHeight * 0.01f
            ? ClearanceDesign * (screenHeight / ReferenceHeight)
            : ClearanceDesign;

    // The band the board may use.
    //
    // `upperAnchor` is the resolved top edge (already the icon row's bottom plus nothing, or the failure page's title
    // edge) and `upperSource` says which fact produced it; the clearance is added here so every caller gets the same
    // one. `footerControlTop`, `footerTextTop` and `measuredControlTop` are the three lower walls the page can offer,
    // any of which may be NaN: the highest of them wins, because a band that stops at the waiting line always also
    // stops above the button underneath it.
    // The six-argument form keeps the original naming: a caller that has only the figure gets `FooterText`.
    public static bool TryResolve(float screenHeight, float upperAnchor, ResultBoundSource upperSource,
        float footerControlTop, float footerTextTop, float measuredControlTop, out ResultsBand band)
        => TryResolve(screenHeight, upperAnchor, upperSource, footerControlTop, footerTextTop,
            ResultBoundSource.FooterText, measuredControlTop, out band);

    // The same rule, with the source that produced the waiting block's top edge: a text the page really draws
    // (`FooterText`) and a control rectangle standing in for it (`FooterTextControl`) are both walls, and the log says
    // which one it was.
    public static bool TryResolve(float screenHeight, float upperAnchor, ResultBoundSource upperSource,
        float footerControlTop, float footerTextTop, ResultBoundSource footerTextSource, float measuredControlTop,
        out ResultsBand band)
    {
        band = ResultsBand.None;
        if (!float.IsFinite(screenHeight) || screenHeight <= ScreenMargin * 2f) return false;
        float clearance = Clearance(screenHeight);
        float limit = screenHeight - ScreenMargin;
        if (!(limit > ScreenMargin)) return false;

        ResultBoundSource topSource = upperSource == ResultBoundSource.None ? ResultBoundSource.ScreenSafeArea : upperSource;
        float top;
        if (float.IsFinite(upperAnchor) && upperAnchor < screenHeight)
            top = Math.Max(0f, upperAnchor) + clearance;
        else
        {
            // Neither this frame nor the last one could measure an upper edge: the band starts from the window's own
            // fraction, which is the one case the rule allows.
            top = screenHeight * UnmeasuredTopFraction;
            topSource = ResultBoundSource.ScreenSafeArea;
        }
        if (!float.IsFinite(top)) { top = ScreenMargin; topSource = ResultBoundSource.ScreenSafeArea; }
        if (top < 0f) top = 0f;
        if (top > limit) top = limit;

        // The highest of the measured lower walls, each pulled up by the same clearance.
        float bottom = limit;
        ResultBoundSource bottomSource = ResultBoundSource.ScreenFoot;
        if (float.IsFinite(measuredControlTop))
        {
            float wall = measuredControlTop - clearance;
            if (wall < bottom) { bottom = wall; bottomSource = ResultBoundSource.MeasuredControl; }
        }
        if (float.IsFinite(footerControlTop))
        {
            float wall = footerControlTop - clearance;
            if (wall < bottom) { bottom = wall; bottomSource = ResultBoundSource.FooterControl; }
        }
        if (float.IsFinite(footerTextTop))
        {
            float wall = footerTextTop - clearance;
            if (wall < bottom) { bottom = wall; bottomSource = footerTextSource == ResultBoundSource.None ? ResultBoundSource.FooterText : footerTextSource; }
        }
        if (!float.IsFinite(bottom)) { bottom = limit; bottomSource = ResultBoundSource.ScreenFoot; }
        if (bottom > limit) bottom = limit;
        if (bottom < ScreenMargin) bottom = ScreenMargin;

        bool clamped = false;
        if (bottom - top < MinimumBand)
        {
            // The page left less room than a band is worth. The footer is the promise that holds: the upper edge moves
            // up until the band is worth drawing in, and the board is drawn small inside it rather than over the text
            // the player is reading.
            top = Math.Max(ScreenMargin, bottom - MinimumBand);
            clamped = true;
        }
        band = new ResultsBand(top, bottom, topSource, bottomSource, clamped);
        return band.Height > 0f;
    }

    // Which of the two edges a log line is describing, in the few words a live log can afford.
    public static string Name(ResultBoundSource source)
    {
        switch (source)
        {
            case ResultBoundSource.IconRow: return "icon-row";
            case ResultBoundSource.LastTrustedIconRow: return "icon-row-last-trusted";
            case ResultBoundSource.TitleAnchor: return "title-anchor";
            case ResultBoundSource.ScreenSafeArea: return "screen-safe-area";
            case ResultBoundSource.FooterControl: return "footer-control";
            case ResultBoundSource.FooterText: return "footer-text";
            case ResultBoundSource.FooterTextControl: return "footer-text-control";
            // The failure page's own upper edge: the window's safe top margin, which is the one upper bound the newest
            // user contract allows there. The mission-failed title is a caption rather than completion information, so
            // it no longer sets the band's top or spends the table's glyph size (`ResultAutoBoardContract`).
            case ResultBoundSource.ScreenTopMargin: return "screen-top-margin";
            case ResultBoundSource.MeasuredControl: return "measured-control";
            case ResultBoundSource.ScreenFoot: return "screen-foot";
            default: return "none";
        }
    }
}

// How a band height becomes a glyph size and a row height.
//
// The settlement table has a fixed row count (the label rows plus one per player plus the kill tiers), so the band
// is spent on the rows and not on adding rows:
//
//   rowBudget = bandHeight / rows                        the height one row may take
//   rowHeight = max(font * RowFactor * density.Row, font)  what one row really takes (squeezed: one em minimum)
//   font      = floor(rowBudget / max(RowFactor * density.Row, 1))
//
// At the compact density `RowFactor * density.Row` is 1.45 * 0.80 = 1.16, so a 588 px band over 30 rows allows a
// 16 px glyph and a real 200 px band still allows a 5 px one over 30 rows (30 x 5.8 = 174 px) - which is below the
// 6 px the player would like, and exactly why the readability floor is a preference and the band is a promise.
// The measured search in `ResultsFontGrowth.FitBoardIntoStrip` then refines this analytic answer against the real
// text widths; this mapping is what keeps that search inside the band in the first place, and it is the number the
// one log line reports when the band forced a smaller glyph than readability alone would have chosen.
public static class ResultsBandFit
{
    // A glyph smaller than this is not a glyph: below it the band is refused by its caller instead.
    public const int AbsoluteMinimumPixels = 3;

    public static float RowBudget(float bandHeight, int rows)
    {
        if (!float.IsFinite(bandHeight) || bandHeight <= 0f) return 0f;
        if (rows < 1) rows = 1;
        return bandHeight / rows;
    }

    // The height one row really takes at a glyph size. `HudTable.BuildGrid` builds every settlement row with the
    // face's own line height (`HudTable.RowEm`, never below `PanelGeometry.MinimumRowEm` = 1.5 em), and the density
    // scales it; the larger of the two terms is what the row occupies.
    public static float RowHeight(float fontPixels, PanelDensity density, float rowEm = PanelGeometry.MinimumRowEm)
    {
        if (!float.IsFinite(fontPixels) || fontPixels <= 0f) return 0f;
        float rowScale = float.IsFinite(density.Row) && density.Row > 0f ? density.Row : PanelDensity.Compact.Row;
        float em = float.IsFinite(rowEm) && rowEm > 1f ? rowEm : PanelGeometry.MinimumRowEm;
        return Math.Max(fontPixels * PanelGeometry.RowFactor * rowScale, fontPixels * em);
    }

    // The number of pixels of row one glyph pixel buys, which is what turns a band into a glyph size.
    public static float EmPerGlyphPixel(PanelDensity density, float rowEm = PanelGeometry.MinimumRowEm)
    {
        float rowScale = float.IsFinite(density.Row) && density.Row > 0f ? density.Row : PanelDensity.Compact.Row;
        float em = float.IsFinite(rowEm) && rowEm > 1f ? rowEm : PanelGeometry.MinimumRowEm;
        return Math.Max(PanelGeometry.RowFactor * rowScale, em);
    }

    // The largest glyph size whose rows fit the band, before any text is measured.
    public static int FontForBand(float bandHeight, int rows, PanelDensity density, int ceiling,
        float rowEm = PanelGeometry.MinimumRowEm)
    {
        float budget = RowBudget(bandHeight, rows);
        if (!(budget > 0f)) return AbsoluteMinimumPixels;
        int font = (int)Math.Floor(budget / EmPerGlyphPixel(density, rowEm));
        if (font < AbsoluteMinimumPixels) font = AbsoluteMinimumPixels;
        if (ceiling > 0 && font > ceiling) font = ceiling;
        return font;
    }

    // The floor a grid may be built with in this band: the readable floor when the band can hold it, and the band's
    // own answer when it cannot (`squeezed`). Handing this to `HudTable.BuildGrid` is what stops a short band from
    // being answered with a table that is taller than the page allows: the glyph and the row shrink together.
    public static int FloorForBand(float bandHeight, int rows, PanelDensity density, int readableFloor,
        out bool squeezed, float rowEm = PanelGeometry.MinimumRowEm)
    {
        int requested = readableFloor < AbsoluteMinimumPixels ? AbsoluteMinimumPixels : readableFloor;
        int analytic = FontForBand(bandHeight, rows, density, 0, rowEm);
        squeezed = analytic < requested;
        int floor = squeezed ? analytic : requested;
        if (floor < AbsoluteMinimumPixels) floor = AbsoluteMinimumPixels;
        return floor;
    }

    // The shortest grid this build can draw for `rows` rows: every row at the absolute minimum glyph, with the row's own line
    // as its only floor. It is the bottom of the fail-open floor ladder - a band shorter than this cannot hold a board at all,
    // and the caller says so once instead of looping on the same impossible measurement for ever.
    public static float MinimumDrawableHeight(int rows, PanelDensity density)
        => Math.Max(1, rows) * RowHeight(AbsoluteMinimumPixels, density);
}

// ---------------------------------------------------------------------------------------------------
// The settlement board's automatic mode: the newest user contract, in one place
// ---------------------------------------------------------------------------------------------------
//
// The two pages do not answer the same question about their upper edge, and pretending they did is what made a
// failure page push its table down the screen:
//
//   failure  the native "mission failed" title is a caption, not completion information, so it is *not* the band's
//            upper bound any more. The band starts at the window's own safe top margin and ends at the highest wall
//            the page really draws below it - the host's leave-expedition button, the client's waiting-status line,
//            or a pressable control above either. A title laid out lower can no longer push the table down the page
//            nor spend its glyph size through `ResultsBandFit`.
//   success  the page's own completion section - the level name, the sector box, the cleared-sector icons and their
//            captions - is the information the player came for, so its measured lower edge stays the board's upper
//            bound. A page that cannot prove that edge keeps the vanilla screen: nothing here substitutes a fraction
//            of the window for it, and no proportionality rule is ever paid for with the section.
//
// What both pages do share is the rest of the rule, and it is written once, here:
//
//   * the board is bottom-aligned to the wall (`ResultBoardHold.TryPlace(..., bottomAligned: true)`): it grows
//     upward from the one edge that may not move, instead of hanging from a top edge the page can slide around.
//   * the glyph is aimed at a resolution-scaled target rather than at "as large as the band allows": 12 px at a
//     1080-high window and 16 px at 1440, the same 1/90 of the window height in both. That is what keeps one page
//     with a tall band from turning into a poster while another keeps a readable table, and what makes the same
//     roster land in the same normalized size and place at 1920x1080 and 2560x1440 (whole pixels are the only
//     quantisation allowed).
//   * the target is a target and never a licence to drop content: the row count is fixed, a band that cannot hold
//     the whole table at the target shrinks the glyph to the largest size that really fits whole rows, and only a
//     band too short even for the readable floor - or the player's own explicit zoom - leaves rows to the wheel
//     (`ResultsScroll`). Nothing here crops a row or changes what the table says.
//
// Everything here is free of Unity and of BepInEx, so the whole of it is asserted headlessly in
// tests/GTFOCombatPanel.Smoke (`ResultsPageScenarios.AutoModeContract`).
public static class ResultAutoBoardContract
{
    // The reference frame the target is written in, and the target itself: 12 px at 1080, 16 px at 1440.
    public const float BaselineScreenHeight = 1080f;
    public const int BaselineGlyphPixels = 12;
    // A sanity ceiling: a pathological window height may not ask for a glyph that is no longer a table.
    public const int MaximumGlyphPixels = 48;
    // The air the failure page keeps from the window's own top edge, in the pixels of a 1080-high window.
    public const float TopMarginDesign = 8f;

    // The glyph size this window's automatic mode aims at: 12 px at 1080p and 16 px at 1440p, rounded to whole
    // pixels because a glyph is drawn at whole pixels, and clamped to the absolute minimum at the small end.
    public static int TargetGlyphPixels(float screenHeight)
    {
        if (!float.IsFinite(screenHeight) || screenHeight <= BaselineScreenHeight * 0.01f) return BaselineGlyphPixels;
        int pixels = (int)Math.Round(BaselineGlyphPixels * ((double)screenHeight / BaselineScreenHeight));
        if (pixels < ResultsBandFit.AbsoluteMinimumPixels) pixels = ResultsBandFit.AbsoluteMinimumPixels;
        if (pixels > MaximumGlyphPixels) pixels = MaximumGlyphPixels;
        return pixels;
    }

    // The failure page's own top margin: the same eight design pixels a 1080p window gets, scaled with the height,
    // and never below the margin every other placement in this mod keeps from the window's edge.
    public static float TopMargin(float screenHeight)
    {
        if (!float.IsFinite(screenHeight) || screenHeight <= BaselineScreenHeight * 0.01f) return TopMarginDesign;
        float margin = TopMarginDesign * (screenHeight / BaselineScreenHeight);
        return margin < ResultsBounds.ScreenMargin ? ResultsBounds.ScreenMargin : margin;
    }

    // The upper edge of one result page's band, and the fact that produced it.
    //
    // A success page may only answer with the completion section's own measured lower edge: an unmeasurable one is
    // `false` here, which is what keeps the vanilla page up instead of drawing the table over the level name and the
    // sector icons. A failure page answers with the window's safe top margin whatever its title did - its caller
    // keeps that title for the interaction scan's split and for its own gates, but never as a bound on the table.
    public static bool TryUpperAnchor(bool successPage, float screenHeight, float completionBottom,
        out float anchor, out ResultBoundSource source)
    {
        anchor = float.NaN;
        source = ResultBoundSource.None;
        if (!float.IsFinite(screenHeight) || screenHeight <= ResultsBounds.ScreenMargin * 2f) return false;
        if (successPage)
        {
            // A completion section that cannot be placed on this page is not an edge: the caller defers.
            if (!float.IsFinite(completionBottom) || completionBottom < 0f
                || completionBottom >= screenHeight - ResultsBounds.ScreenMargin) return false;
            anchor = completionBottom;
            source = ResultBoundSource.IconRow;
            return true;
        }
        anchor = TopMargin(screenHeight);
        source = ResultBoundSource.ScreenTopMargin;
        return true;
    }

    // 1.19.60: the failure page's upper edge, asked once for a client page as well.
    //
    // The recorded 1.19.59 client failure page answered this question twice and differently: the strict path started
    // its band at the window's own safe margin (10.67 at 1440) while the fail-open path started at the live
    // mission-failed title's bottom (467) plus the same clearance the strict path applies - ~456 px apart on one
    // page, with the strict board bottom-aligned to its wall and therefore covering the lower 40-90 px of the title
    // the player is reading. The title is protection, not decoration (that has been this contract's own word for it
    // since 1.19.55), and it is literally the same page object on both roles, so it bounds both.
    //
    // `titleBottom` is the page's live mission-failed title edge; `float.NaN` means this frame could not read it, and
    // the window margin then stands alone - the one case the contract allows, never an invented title. `measuredControlTop`
    // is the highest interaction the page's own scan saw reaching past the anchor (`FooterControls.BoardTop`): a
    // control standing above the title is a wall too, and it may only tighten the band downwards. No clearance is
    // added here - every caller adds `ResultsBounds.Clearance` exactly once, from the rule that owns it.
    public static bool TryFailureAnchor(float screenHeight, float titleBottom, float measuredControlTop,
        out float anchor, out ResultBoundSource source)
    {
        anchor = float.NaN;
        source = ResultBoundSource.None;
        if (!float.IsFinite(screenHeight) || screenHeight <= ResultsBounds.ScreenMargin * 2f) return false;
        float limit = screenHeight - ResultsBounds.ScreenMargin;
        if (!(limit > ResultsBounds.ScreenMargin)) return false;
        anchor = TopMargin(screenHeight);
        source = ResultBoundSource.ScreenTopMargin;
        if (float.IsFinite(titleBottom) && titleBottom > anchor && titleBottom < limit)
        {
            anchor = titleBottom;
            source = ResultBoundSource.TitleAnchor;
        }
        // 1.19.60 (review F2): `measuredControlTop` is the *raw* edge of the deepest interaction that reaches past the
        // anchor - never a value that already had this page's clearance applied to it. The caller subtracts the gap the
        // scan was run with (`FooterControls.BoardTop - MessageGap`), because the strip adds the clearance itself, once.
        // A control within half a pixel of the anchor adds nothing, so a value that is the anchor plus a gap can never be
        // dressed up as a measured control.
        if (float.IsFinite(measuredControlTop) && measuredControlTop > anchor + 0.5f && measuredControlTop < limit)
        {
            anchor = measuredControlTop;
            source = ResultBoundSource.MeasuredControl;
        }
        return true;
    }

}
