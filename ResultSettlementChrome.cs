using System;
using System.Collections.Generic;

namespace HawChow.GTFOCombatPanel;

// ---------------------------------------------------------------------------------------------------
// ResultSettlementChrome.cs - the settlement board's own controls and its own surface.
//
// Everything here is free of Unity and of BepInEx, so the whole of it is asserted headlessly in
// tests/GTFOCombatPanel.Smoke/ResultChromeScenarios.cs and the same numbers are used to draw the
// offline previews in docs/previews (tests/.../BoardPreview.cs). One set of tokens, one set of
// geometry, one set of decisions - the picture, the test and the game read the same numbers.
//
//   ResultChromeTheme     the surface: a graphite ground, ivory ink, one industrial-yellow accent.
//                         Flat tints only: faces are separated by a step of tone, not by a hairline,
//                         a shadow or a glow.
//   ResultChromeMetrics   every control size, measured from the window instead of assumed. 1080p is
//                         the reference; 720p, 1440p and ultrawide all resolve from the same rule,
//                         and the numbers are the reference mod's own control arithmetic
//                         (h = clamp(anchorH * 0.75, 20, 28), w = clamp(3.25h, 68, 92), gap 4).
//   ResultChromeLayout    where a control may stand. Both controls are placed against something the
//                         page really draws, are rejected against every measured rectangle on that
//                         page, and are simply not produced when no such place exists - a control the
//                         player cannot see never keeps a click box.
//   ResultChromeInputGate the press discipline: a release has to be seen before a press counts, the
//                         press's own frame decides the hit, one physical press is one action, and a
//                         page change, a resolution change or a lost focus cancels everything.
//   ResultSettlementSession
//                         the board is up on a fresh settlement by default, whatever the previous one
//                         was left as. Only this mod's own hide is undone; a switch the player
//                         turned off in the configuration file is never written over.
// ---------------------------------------------------------------------------------------------------

// A colour as the theme states it. Kept as floats rather than a Unity type so the palette, the
// offline preview and the headless assertions are one thing.
public readonly struct ResultInk
{
    public readonly float R, G, B, A;
    public ResultInk(float r, float g, float b, float a = 1f) { R = r; G = g; B = b; A = a; }
    public static ResultInk FromRgb(uint rgb, float alpha = 1f)
        => new ResultInk(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);
    public ResultInk WithAlpha(float alpha) => new ResultInk(R, G, B, alpha);
    // The same ink over a different ground: only the alpha changes, so a quiet line is the same ink
    // stepped down rather than a second colour.
    public ResultInk Quiet(float share) => new ResultInk(R, G, B, Math.Max(0f, Math.Min(1f, share)));
}

// One ground, one ink, one accent. The settlement board is a graphite plate on the page, its labels
// are ivory, and exactly one thing on it is yellow: the figure the player is meant to read first.
public static class ResultChromeTheme
{
    // #212522 - the ground. Deliberately not the in-level plate: this surface is a document, not a
    // HUD, so it is one flat tone rather than a tint over the level.
    public static readonly ResultInk Ground = ResultInk.FromRgb(0x212522);
    // #2B2D2F - the same surface raised one step. It is what a section band and an idle control are
    // filled with, so the eye reads hierarchy without a single dividing line.
    public static readonly ResultInk GroundRaised = ResultInk.FromRgb(0x2B2D2F);
    // #FFFFFF - the ink of a figure that is the point of its row.
    public static readonly ResultInk Ink = ResultInk.FromRgb(0xFFFFFF);
    // #E1E5E1 - ivory: labels, names, the secondary column of every figure.
    public static readonly ResultInk Ivory = ResultInk.FromRgb(0xE1E5E1);
    // #F5DE36 - the one accent. The board's rank figures, the accent rule under a focused control
    // and the pressed state of a control are the only places it appears.
    public static readonly ResultInk Accent = ResultInk.FromRgb(0xF5DE36);
    // #33332B - an idle control's fill, and the raised tone of a section band on the ground.
    public static readonly ResultInk AccentIdle = ResultInk.FromRgb(0x33332B);
    // #6B571C - the same idle fill with the accent in it, which is the whole of a hover state.
    public static readonly ResultInk AccentHover = ResultInk.FromRgb(0x6B571C);
    // #8F7A29 - and one step further for a press, still a flat tint of the same hue.
    public static readonly ResultInk AccentPressed = ResultInk.FromRgb(0x8F7A29);
    // Share of the row band and of a section band. Both are tints of the ground, so they read as tone
    // steps rather than as lines.
    public const float RowBandShare = 0.030f;
    public const float SectionBandShare = 0.085f;
    // The accent rule under a focused control, and the control's own padding.
    public const float FocusRule = 2f;
    public const float ControlPadX = 6f;

    // The close's own surface used to live here (`CloseFace`/`CloseFaceHover`/`CloseFacePressed`,
    // `CloseEdge`, `CloseInk`, `CloseEdgeWidth`). It is gone with the control: 1.19.45 removed the second
    // button from this board entirely - a close that hides the board sitting twelve pixels from a toggle
    // that does the same thing was the one control on the settlement page the player could press by
    // accident. The only visible way to hide or show the board is now the one toggle beside the page's own
    // return control (or, on a client page, beside `m_returnToLobby_text`), and F8 stays what it always was:
    // the keyboard shortcut, answered by the same unified show/hide - a key, never a second button.
    // ---- the board's header ----------------------------------------------------------------------------
    //
    // The header is no longer a bare band with one button on it, and since 1.19.45 it carries no button at all.
    // It carries the board's own name and the key that operates the board, and every rectangle of it is planned
    // from the band the board is drawn on (`PlanHeader`), so the band, its rule, the title and the key hint can
    // never drift apart. The band itself is still one step of tone above the table's ground - no hairline, no
    // shadow, no glow - and the only line on it is the 2 px accent rule across its top edge, the same weight the
    // reference panel uses (conflictmodlist src/TeamVersionsDriver.cs:729 `Rect(0, 0, w, 2)`).
    public const float HeaderRule = 2f;
    // The band's fill share over the table's ground: a step of tone, never a divider.
    public const float HeaderBandAlpha = 0.92f;
    // The header's own inks. Two, not three: the board's name in the full ink, the key hints in the quiet ink.
    public static readonly ResultInk HeaderTitle = Ink;
    public static readonly ResultInk HeaderHint = Ivory;
    // The name of the screen the header belongs to, and the two keys that operate the board on it.
    //
    // 1.19.42: the name has a *short* form as well, because the plan may not draw a box narrower than the ink it puts in
    // it. A 300 px plate left `战斗结算看板` (six full ems at the header's own glyph size) in a 74 px box, and the word ran
    // out of the box and over the close. The plan now asks for the full name, then for this one, and hides the name when
    // neither fits whole (`PlanHeader`); every one of the three is measured from the same `TextEm` the plan budgets with.
    public static string TitleLabel => CombatStrings.Language == "en" ? "COMBAT BOARD" : "战斗结算看板";
    // Four ems against the full name's six: the same board, said short, and still a name rather than a stub.
    public static string ShortTitleLabel => CombatStrings.Language == "en" ? "BOARD" : "结算";
    // The two keys that operate the board, in the header's quiet ink. One short line and never a second row: the header
    // has no room for a row the table would have to give up. Since 1.19.42 it is planned *first* - a player who cannot
    // see how to bring the board back has nothing, while a missing name costs only the label (`PlanHeader`).
    public static string KeyHintLabel => CombatStrings.Language == "en" ? "F8 show/hide  wheel scroll" : "F8 显示/隐藏  滚轮 滚动";
    // 1.19.62: the run's created-enemy figure is the band's third run - the one line of *data* on it. It is planned
    // after the name and only in the room the name did not use, so it can never push the name into its short form, and
    // it is dropped (not the name, not the hint) when that room is too small. Same quiet ink as the key hint: it is an
    // aside about the run, not a headline.
    public const string FigureStyleLabel = "spawn-figure";
    // WCAG 2.1 contrast between two inks, as a ratio from 1 to 21. It exists so that "the close is
    // readable" is a number a test can assert instead of an opinion: the alpha of neither ink is part of
    // it, because the question is what the player's eye sees between the word and the face under it.
    public static float Contrast(ResultInk a, ResultInk b)
    {
        float first = RelativeLuminance(a), second = RelativeLuminance(b);
        float lighter = Math.Max(first, second), darker = Math.Min(first, second);
        return (lighter + 0.05f) / (darker + 0.05f);
    }

    // sRGB relative luminance, the figure the ratio above is built from.
    public static float RelativeLuminance(ResultInk ink)
        => 0.2126f * Channel(ink.R) + 0.7152f * Channel(ink.G) + 0.0722f * Channel(ink.B);

    private static float Channel(float value)
    {
        float c = float.IsFinite(value) ? Math.Max(0f, Math.Min(1f, value)) : 0f;
        return c <= 0.03928f ? c / 12.92f : (float)Math.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    // The one label the board's toggle carries. It says what the press will do, not what the control is:
    // the same button hides a board that is up and shows one that is not. There is deliberately no second
    // label beside it - see the note above about the close that used to stand in the header. Neither of the
    // two is ever dropped, and the toggle's own box is never narrower than the word in it.
    public static string ToggleLabel(bool boardUp)
        => CombatStrings.Language == "en" ? (boardUp ? "HIDE" : "SHOW") : (boardUp ? "隐藏看板" : "显示看板");
}

// One resolution's worth of control sizes.
//
// The reference is 1080p, and every figure scales linearly from the window's height because that is
// what a control's optical size follows: a 720p window gets 0.75 of it, a 1440p window 1.34, and an
// ultrawide window is scaled by its height alone - its extra width is page, not control. The cap in
// both directions is what keeps a 4K window from drawing a bar across the screen and a windowed
// 720p client from drawing something nobody can hit.
public readonly struct ResultChromeMetrics
{
    public readonly float Scale;
    // The toggle that stands to the left of the page's own return control (or, on a client page, of
    // the waiting-status line the page really draws).
    public readonly float ToggleHeight, ToggleWidth, ToggleMinWidth, ToggleGap, ToggleFont;
    // The board's own header band: its height, its title size, and the row the band reserved for a control.
    // The band carries the board's name and the key hint and no control of this mod's own any more (1.19.45),
    // but it keeps exactly the height it had when a close control stood in that row, so not one table row moved
    // when the control was removed.
    public readonly float HeaderHeight, HeaderFont;
    // The key hints' own size: the one type on the header below the title, and the first thing the header
    // gives up on a narrow band.
    public readonly float HeaderHintFont;
    // The header's own inset from the plate's top and right edges. Fixed pixels, deliberately not scaled: the
    // corner the band is drawn at does not move when the window changes, and the band keeps the same five-pixel
    // distance from the corner the plate really draws at 720p, 1080p, 1440p and on an ultrawide.
    public const float HeaderInset = 5f;

    private ResultChromeMetrics(float scale, float toggleHeight, float toggleWidth, float toggleMinWidth,
        float toggleGap, float toggleFont, float headerHeight, float headerFont)
    {
        Scale = scale;
        ToggleHeight = toggleHeight; ToggleWidth = toggleWidth; ToggleMinWidth = toggleMinWidth;
        ToggleGap = toggleGap; ToggleFont = toggleFont;
        HeaderHeight = headerHeight; HeaderFont = headerFont;
        // The key hint is the smallest type on the board and it is deliberately not a fourth step: it is the
        // header's own title size stepped down, so the two type sizes that do the work here stay two.
        HeaderHintFont = Math.Max(9f, headerFont * 0.82f);
    }

    // The one scaling rule. A window whose height is unreadable is treated as 1080p rather than as a
    // control size of zero, and a portrait or tiny window is answered by the same floor as 720p.
    public static float ScaleFor(float screenWidth, float screenHeight)
    {
        float height = float.IsFinite(screenHeight) && screenHeight > 0f ? screenHeight : 1080f;
        float scale = height / 1080f;
        if (scale < 0.75f) scale = 0.75f;
        if (scale > 1.34f) scale = 1.34f;
        // An ultrawide window keeps the height rule; a window narrower than the 1080p reference is
        // still allowed its height rule down to the floor, so 1280x720 and 1024x768 both land there.
        float width = float.IsFinite(screenWidth) && screenWidth > 0f ? screenWidth : 1920f;
        float widthScale = width / 1920f;
        if (widthScale < 0.75f) widthScale = 0.75f;
        return Math.Min(scale, Math.Max(0.75f, widthScale));
    }

    public static ResultChromeMetrics For(float screenWidth, float screenHeight)
    {
        float scale = ScaleFor(screenWidth, screenHeight);
        // The page's own control arithmetic, scaled: a control is three quarters of the height of the
        // thing it stands beside, never under 20 px and never over 28 px; three and a quarter times its
        // own height wide, never under 68 px and never over 92 px; a 4 px gap to what it stands beside.
        //
        // The header's own row follows that rule and is floored so a 720p window still gets a band worth
        // reading. It is the figure the band's height is built from - 5 + 43 + 5 = 53 px at 1440p, the top strip
        // every recorded settlement board drew - so the band, and every row under it, sits where it always did.
        float headerRow = Math.Max(26f, 32f * scale);
        float header = HeaderInset + headerRow + HeaderInset;
        return new ResultChromeMetrics(scale,
            22f * scale, 78f * scale, 60f * scale, 4f * scale, 13f * scale,
            header, 12f * scale);
    }

    // The widest label the toggle carries, in glyphs, and the room one glyph of the control's own font
    // needs. A control that cannot hold its own label is a control the player reads as an ellipsis, so a
    // box is never narrower than that text plus the padding the label is drawn with.
    private const int ToggleLabelGlyphs = 4;
    private const float GlyphEm = 1.15f;

    // The toggle's own box for an anchor of this height: the reference mod's rule, kept exactly, and
    // then scaled with the window. A very short anchor still gets a control the player can hit - and one
    // wide enough for the label it is about to draw, which the reference band alone is not on a client
    // page whose waiting-status line is a single short line.
    public float ToggleBox(float anchorHeight, out float width, out float height)
    {
        float h = float.IsFinite(anchorHeight) && anchorHeight > 0f ? anchorHeight * 0.75f : ToggleHeight;
        if (h < ToggleHeight) h = ToggleHeight;
        if (h > 28f * Scale) h = 28f * Scale;
        float w = 3.25f * h;
        if (w < 68f * Scale) w = 68f * Scale;
        if (w > 92f * Scale) w = 92f * Scale;
        if (w < ToggleMinWidth) w = ToggleMinWidth;
        // The label's own room, asked last: a box too narrow for its text is widened, never the text trimmed.
        float labelRoom = ToggleLabelGlyphs * GlyphEm * ToggleFont + ResultChromeTheme.ControlPadX * 2f;
        if (w < labelRoom) w = labelRoom;
        width = w; height = h;
        return h;
    }
}

// Where this machine's toggle may stand, and which native fact that place came from.
public enum ResultChromeAnchor { None = 0, HostReturnButton = 1, HostActionButton = 2, ClientWaitingStatus = 3 }

// A control's visual state. Three flat fills, no scale, no animation.
public enum ResultChromeVisual { Idle = 0, Hover = 1, Pressed = 2 }

public static partial class ResultChromeLayout
{
    // A point is inside a rectangle the way every hit test in this project is: half open, so two
    // adjacent rectangles can never both answer for one pixel.
    public static bool Inside(LayoutRect rect, float x, float y)
        => rect.Width > 0f && rect.Height > 0f && x >= rect.X && x < rect.Right && y >= rect.Y && y < rect.Bottom;

    // The plate a board is drawn on: the table's own rectangle with its header reserved above it.
    // Nothing of the table moves: the header is added above what the table measured, and it is that
    // total height the placement has to fit.
    public static LayoutRect BoardOf(LayoutRect grid, ResultChromeMetrics metrics)
        => new LayoutRect(grid.X, grid.Y - metrics.HeaderHeight, grid.Width, grid.Height + metrics.HeaderHeight);

    // 1.19.45: this file used to place a second control - a close button in the board's reserved header
    // (`CloseSeat`/`TryClose`) - together with the two "second storeys" it could fall back to whenever the page
    // offered no anchor of its own (`HeaderSeat` in that header, `DockSeat` at the window's right margin, both
    // judged by `SeatFree`). All of it is gone. There is exactly one visible way to hide or show this board, and
    // it is the toggle that stands immediately left of the control the page really draws: the host's
    // return-to-lobby button (with the checkpoint-restart button kept as the supported fallback), or - when the
    // host's buttons are not on the page - the client's own `m_returnToLobby_text`, whose real drawn text is
    // measured twice and never replaced by a parent's rectangle. F8 stays the keyboard shortcut and is answered
    // by the same unified show/hide: a key, never a second button. A page that offers no anchor at all gets no
    // control this repaint and is asked again on the next one, which is what lets a host control that arrives
    // late earn the place it should have had from the start (the recorded failure page answered
    // `anchor=no-host-action` for its first repaints and `HostActionButton` once the button was really drawn).

    // 1.19.55: the place a control is committed to keeps a *real* gap from everything the page draws, not the bare reference
    // gap. The recorded pages committed the toggle with its right edge exactly on the return button's left edge (r5: toggle
    // 655..746 beside the native 744..1188 - a 2 px overlap on the very frame it was committed; r3: 393..461 beside 447..462),
    // and then the page's own button breathed under it: 32 `toggle paused` / 30 `released` pairs in one session, every one of
    // them taking the button off the screen for three frames. The commit therefore stands `CommitClearance` further aside, so
    // the ordinary breathing of a hover/idle animation never reaches the control at all.
    public static float CommitClearance(ResultChromeMetrics metrics)
    {
        float scale = metrics.Scale > 0f && float.IsFinite(metrics.Scale) ? metrics.Scale : 1f;
        float clearance = 12f * scale;
        return clearance < 8f ? 8f : clearance;
    }

    // The band of the page's own breathing a conflict has to be deeper than. The commit clearance is what normally keeps
    // the two rectangles apart; this is the second half of the same rule, for the frames a page animates further than its
    // clearance (the recorded r3 return button answered at x=447..462 - fifteen pixels of hover scale - under a control
    // committed at 461). Six pixels at 1080p, scaled: a real control standing on the button is hundreds of pixels deep, a
    // page's own animation is a handful.
    public static float ConflictTolerance(ResultChromeMetrics metrics)
    {
        float scale = metrics.Scale > 0f && float.IsFinite(metrics.Scale) ? metrics.Scale : 1f;
        float tolerance = 8f * scale;
        return tolerance < 6f ? 6f : tolerance;
    }

    // The toggle: immediately left of the control the page really draws, on that control's own floor,
    // with the reference gap and the commit clearance. A place that would cross the screen's own margin, or any
    // rectangle this page really draws (the anchor itself, the page's other actions, the waiting-status line,
    // this mod's own board), is rejected; the search then steps one control further left, and then one more,
    // because a page whose return button has a neighbour should get its toggle beside it rather than a
    // control on top of something. Nothing found means no control at all: no place, no click box.
    public static bool TryToggle(LayoutRect anchor, IReadOnlyList<LayoutRect> obstacles, float screenWidth,
        float screenHeight, ResultChromeMetrics metrics, out LayoutRect rect, out string why, float clearance = 0f)
    {
        rect = default; why = string.Empty;
        if (!(anchor.Width > 0f) || !(anchor.Height > 0f) || !float.IsFinite(anchor.X) || !float.IsFinite(anchor.Y))
        {
            why = "anchor-unmeasurable";
            return false;
        }
        metrics.ToggleBox(anchor.Height, out float width, out float height);
        if (!(width > 0f) || !(height > 0f) || !float.IsFinite(screenWidth) || !float.IsFinite(screenHeight))
        {
            why = "unmeasurable";
            return false;
        }
        // The control's floor is the anchor's floor: it stands beside the page's control, not centred
        // on it and not snapped to a screen edge.
        float y = anchor.Bottom - height;
        if (y < 8f) y = Math.Max(8f, anchor.Y);
        if (y + height > screenHeight - 8f) { why = "off-screen-bottom"; return false; }
        // The gap the place is committed with: the reference gap plus the commit clearance. A clearance that is not a number
        // is no clearance, never an infinite one.
        if (!float.IsFinite(clearance) || clearance < 0f) clearance = 0f;
        float gap = metrics.ToggleGap + clearance;
        float step = width + gap;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            float x = anchor.X - gap - width - step * attempt;
            if (x < 8f) { why = attempt == 0 ? "no-room-left" : "no-gap-left"; break; }
            var candidate = new LayoutRect(x, y, width, height);
            bool blocked = false;
            if (obstacles != null)
            {
                for (int index = 0; index < obstacles.Count; index++)
                {
                    LayoutRect other = obstacles[index];
                    if (!(other.Width > 0f) || !(other.Height > 0f)) continue;
                    // A control never stands on the pixel of something the page draws: the gap is the
                    // same one it keeps to its anchor, in both axes.
                    if (candidate.Overlaps(other, gap)) { blocked = true; break; }
                }
            }
            if (blocked) { why = "obstacle"; continue; }
            rect = candidate;
            why = attempt == 0 ? "beside-anchor" : "stepped-aside";
            return true;
        }
        if (why.Length == 0) why = "no-room";
        return false;
    }

    // The fill and ink of one control in one state. Flat tints only: the press is a third step of the
    // same hue, never a scale and never a glow.
    public static void Visuals(ResultChromeVisual state, out ResultInk fill, out ResultInk ink)
    {
        switch (state)
        {
            case ResultChromeVisual.Pressed:
                fill = ResultChromeTheme.AccentPressed; ink = ResultChromeTheme.Accent; return;
            case ResultChromeVisual.Hover:
                fill = ResultChromeTheme.AccentHover; ink = ResultChromeTheme.Accent; return;
            default:
                fill = ResultChromeTheme.AccentIdle; ink = ResultChromeTheme.Ivory; return;
        }
    }

    // The state a control is in this frame. A press that began on this control is the pressed state;
    // the box under the pointer is the hover. Nothing is remembered between frames, which is why a
    // control that is no longer drawn cannot look hovered.
    public static ResultChromeVisual Visual(ResultChromeInputGate gate, LayoutRect rect, bool rectFresh,
        bool pointerValid, float pointerX, float pointerY)
    {
        if (!rectFresh || !pointerValid) return ResultChromeVisual.Idle;
        if (!Inside(rect, pointerX, pointerY)) return ResultChromeVisual.Idle;
        return gate != null && gate.PressInside ? ResultChromeVisual.Pressed : ResultChromeVisual.Hover;
    }

    // The header band the close stands in: the plate's own top strip, whose height is reserved out of the
    // strip the table was fitted into. Drawing the band from the same rectangle the close is measured
    // against is what guarantees the button always has a face under it.
    public static LayoutRect HeaderOf(LayoutRect plate, ResultChromeMetrics metrics)
        => new LayoutRect(plate.X, plate.Y, plate.Width, metrics.HeaderHeight);

    // ---------------------------------------------------------------------------------------------------
    // The board's header: one band, one rule, one name, two controls
    // ---------------------------------------------------------------------------------------------------
    //
    // Every rectangle the header draws is planned here, from the band the close was committed to, and the
    // renderer draws exactly this plan. The point is not decoration: the header was the one piece of the board
    // with nothing on it but a button, so a player could not read what the plate was, which keys operate it, or
    // where the second control is. The plan answers all three from the same numbers the hit tests use.
    //
    // 1.19.42, two recorded findings on that plan:
    //
    //   1. The band is the *only* budget. `right` used to be the toggle's own x wherever that toggle stood, and on a
    //      page whose board is 1064..1497 the toggle stands in the page's dock at 2309 - so the key hint was planned at
    //      2309, 800 px outside the plate it belongs to, and the name was given 1240 px of room it could never use. A
    //      control now spends the header's room only when it really stands inside the band, and the hard upper bound is
    //      the leftmost of those controls and the band's own right inset.
    //   2. A planned box has to hold the ink the renderer draws in it. `战斗结算看板` is six full ems: on a 300 px plate
    //      the plan handed it a 74 px box and the word ran out of the box, over the close. The name is now measured
    //      against the room it has - full label, then `ShortTitleLabel`, then nothing - so a box is never narrower than
    //      the word inside it.
    //
    // The order the plan gives things up is therefore: the key hint keeps its line first (it is what tells the player the
    // board can be brought back) and the name drops to its short form and then away. The band carries no control of this
    // mod's own since 1.19.45, so there is nothing else for it to spend its room on. The hint stays a single line inside
    // the band and never a second row: a row would have to be taken out of the table's own measured strip.
    //
    // 1.19.62 adds the third run and gives it the lowest priority of all three: the run's created-enemy figure is
    // planned *after* the name, only in the room the name did not use, and is dropped alone when that room is too
    // small. A figure can therefore never shorten the name or displace the key hint.
    public struct ResultChromeHeaderPlan
    {
        public LayoutRect Band, Rule, Title, Hint, Figure, Toggle;
        public bool HasTitle, HasHint, HasFigure, HasToggle;
        // The name the renderer must draw in `Title`: the full label, or the short one when the full one does not fit.
        // Empty whenever `HasTitle` is false, so a caller can never draw a label the plan did not measure.
        public string TitleLabel;
        // 1.19.62: the run's created-enemy figure, drawn in `Figure` when the band had room for it beside the name.
        // The plan answers the exact string it measured, so a caller never draws a figure that was not fitted, and an
        // empty label means exactly "this band has no room for it" - the name and the key hint keep their own room.
        public string FigureLabel;
        // What the band gave up this repaint: "none" (everything the band can carry is on it), "short-title" (the name
        // is the short form), "title" (no room for a name), "hint" (no room for the keys either), "hint+title", or
        // "no-band" - and `no-band` now means exactly one thing: the page drew no band to plan on. It used to be the
        // initial value of every plan, so a header that fitted everything reported the name of the failure it did not have.
        public string Yield;

        public bool Drawn => Band.Width > 0f && Band.Height > 0f;

        public string Describe()
            => "band=" + RectText(Band) + ", rule=" + RectText(Rule) + ", title=" + RectText(Title) + ", hint=" + RectText(Hint)
               + ", figure=" + RectText(Figure) + ", yielded=" + (string.IsNullOrEmpty(Yield) ? "none" : Yield);

        private static string RectText(LayoutRect rect)
            => rect.Width > 0f && rect.Height > 0f
                ? "(" + rect.X.ToString("0") + "," + rect.Y.ToString("0") + "," + rect.Width.ToString("0") + "x" + rect.Height.ToString("0") + ")"
                : "none";
    }

    // The average advance of one character of a label, in ems, for the room a label needs. A CJK glyph is a
    // full em and a latin one is not; spaces are narrower still. This measures the *text*, not a font: the two
    // labels the header carries are the one zh and the one en line in `ResultChromeTheme`, and both are short.
    // A label that does not fit is dropped whole, never clipped mid-word.
    public static float TextEm(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        float em = 0f;
        for (int index = 0; index < text.Length; index++)
        {
            char glyph = text[index];
            if (glyph == ' ') em += 0.30f;
            else if (glyph >= 0x2E80) em += 1f;
            else em += 0.56f;
        }
        return em;
    }

    // The smallest box this plan will draw a name in, in ems of the header's own font. It is a floor on the *box*, and
    // since 1.19.42 the box is also never narrower than the label's own ink (`TitleFits`): the old rule compared this
    // floor against the room and then drew the whole label into it, which is how `战斗结算看板` ended up in a 74 px box.
    public const float TitleMinGlyphs = 4f;

    // Whether a control really stands on this band.
    //
    // The dock next to the page's own return button is a control of the *page*: on a 433 px plate it can stand at 2309,
    // and the plan used its x as the header's right edge - the key hint landed at 2309, outside the board, and the name
    // was budgeted 800 px of plate that does not exist. Only a control whose rectangle is inside the band spends the
    // band's room. Half a pixel of slack, because a control seated on the band's own inset is a control on the band.
    public static bool OnBand(LayoutRect band, LayoutRect slot)
        => ResultChromeSlot.Usable(slot)
            && slot.X >= band.X - 0.5f && slot.Right <= band.Right + 0.5f
            && slot.Y < band.Bottom && slot.Bottom > band.Y;

    // The name, measured against the room it really has. Answers the label to draw - the full name, then the short one -
    // and never a label whose own ink is wider than `room`; a name that fits in neither form is not drawn at all rather
    // than drawn clipped or spilling over the controls. The width answered is the ink, not the room: a centered or
    // left-aligned box wider than its word is a box whose edge the eye reads as a line.
    // The name, measured against the room it really has. Answers the label to draw - the full name, then the short one -
    // and never a label whose own ink is wider than `room`; a name that fits in neither form is not drawn at all rather
    // than drawn clipped or spilling over the controls. The width answered is the ink, not the room: a box wider than its
    // word is an edge the eye reads as a line.
    //
    // The floor (`TitleMinGlyphs`) guards the *full* name against being reduced to a stub. The short name is a complete
    // name of its own - two glyphs - so it only has to fit its own ink, which is what makes a narrow plate show `结算`
    // rather than nothing.
    public static bool TitleFits(float room, ResultChromeMetrics metrics, out string label, out float ink)
    {
        label = null;
        ink = 0f;
        if (!(room > 0f) || !(metrics.HeaderFont > 0f)) return false;
        float full = TextEm(ResultChromeTheme.TitleLabel) * metrics.HeaderFont;
        if (full > 0f && full <= room && room >= TitleMinGlyphs * metrics.HeaderFont)
        {
            label = ResultChromeTheme.TitleLabel; ink = full; return true;
        }
        float shortInk = TextEm(ResultChromeTheme.ShortTitleLabel) * metrics.HeaderFont;
        if (shortInk > 0f && shortInk <= room) { label = ResultChromeTheme.ShortTitleLabel; ink = shortInk; return true; }
        return false;
    }
    // One line of the header's text, measured against the room it really has: the label's own ink, never more.
    public static bool TextFits(string text, float fontPixels, float room, out float ink)
    {
        ink = 0f;
        if (string.IsNullOrEmpty(text) || !(fontPixels > 0f) || !(room > 0f)) return false;
        float measured = TextEm(text) * fontPixels;
        if (!(measured > 0f) || measured > room) return false;
        ink = measured;
        return true;
    }

    // The band and everything on it. `toggle` is the rectangle the step committed this repaint (or the one it is about to
    // commit), so the band can never describe a different button from the one drawn; it is described here only when it
    // really stands on the band - the page's own dock never does, and since 1.19.45 no control of this mod's own lives on
    // the band either.
    //
    // 1.19.62: `figure` is the run's created-enemy figure as the board's own model answers it (`ImpactBoardLayout.Figure`).
    // It gets the last word on the band: planned after the name, only in the room the name left, and dropped alone when
    // that room cannot hold it. A default of null is the pre-1.19.62 plan exactly - every existing caller and every
    // recorded header assertion keeps the behaviour it had.
    public static ResultChromeHeaderPlan PlanHeader(LayoutRect band, ResultChromeMetrics metrics,
        LayoutRect toggle, bool hasToggle, string figure = null)
    {
        // `no-band` is the answer to one question only - did this page draw a band? - and a plan that fits everything
        // says `none`. It used to be the initial value of every plan, so the normal header reported the failure's name.
        var plan = new ResultChromeHeaderPlan { Yield = "no-band" };
        if (!ResultChromeSlot.Usable(band) || !(metrics.HeaderHeight > 0f)) return plan;
        plan.Band = band;
        plan.Yield = "none";
        float inset = ResultChromeMetrics.HeaderInset;
        // The 2 px accent rule across the top edge: the only line on the board, and it never eats the band it
        // marks. A band too short to hold the rule and a glyph under it is not a band this header draws on.
        if (band.Height > ResultChromeTheme.HeaderRule + inset)
            plan.Rule = new LayoutRect(band.X, band.Y, band.Width, ResultChromeTheme.HeaderRule);
        // Only a control that stands on this band is drawn on it and only such a control spends its room. A control of
        // the page's own dock is neither: drawing it here would draw a second button somewhere it is not, and budgeting
        // against it is what put the key hint 800 px outside the plate.
        if (hasToggle && OnBand(band, toggle)) { plan.Toggle = toggle; plan.HasToggle = true; }
        // The hard upper bound: the band's own right inset, pulled in by the toggle when that really stands on it.
        float right = band.Right - inset;
        if (plan.HasToggle) right = Math.Min(right, plan.Toggle.X - metrics.ToggleGap);
        float left = band.X + inset;
        float width = right - left;
        float height = Math.Max(1f, band.Height - inset * 2f);
        if (!(width > 0f)) { plan.Yield = "hint+title"; return plan; }
        // The key hint is planned first and keeps its room whenever its own ink fits: it is the one line that tells the
        // player the board can be brought back, and a name that loses its place costs the player nothing.
        bool hintDropped = true;
        float rest = width;
        if (TextFits(ResultChromeTheme.KeyHintLabel, metrics.HeaderHintFont, width, out float hintInk))
        {
            plan.Hint = new LayoutRect(right - hintInk, band.Y + inset, hintInk, height);
            plan.HasHint = plan.Hint.Width > 0f && plan.Hint.Height > 0f;
            if (plan.HasHint) { hintDropped = false; rest = width - hintInk - ResultChromeTheme.ControlPadX; }
        }
        // Then the name, in the room the hint left, in the widest form that really fits that room.
        bool full = false, shortForm = false;
        if (TitleFits(rest, metrics, out string label, out float ink))
        {
            plan.Title = new LayoutRect(left, band.Y + inset, ink, height);
            plan.HasTitle = plan.Title.Width > 0f;
            plan.TitleLabel = plan.HasTitle ? label : null;
            full = plan.HasTitle && label == ResultChromeTheme.TitleLabel;
            shortForm = plan.HasTitle && !full;
        }
        plan.Yield = !plan.HasTitle
            ? (hintDropped ? "hint+title" : "title")
            : hintDropped ? "hint" : shortForm ? "short-title" : "none";
        // 1.19.62: the run's created-enemy figure, in the room the name left. It is measured with the band's own
        // `TextFits`, placed with the same inset and height as the name, and it is the only run that is ever dropped
        // for want of room: a figure that does not fit leaves the name and the key hint exactly as they were.
        //
        // The room it may use already excludes the key hint. `right` is the band's own edge (pulled in by a control that
        // stands on it) and the hint was planned at `right - hintInk` - so the figure's room must stop at the hint, minus
        // one control pad, exactly as the name's room does (`rest`). Measuring it against `right` would hand it the
        // hint's own rectangle on a narrow band; the assertion in the suite pins `Figure.Right <= Hint.X` at every width.
        if (!string.IsNullOrEmpty(figure) && plan.HasTitle)
        {
            float figureLeft = left + plan.Title.Width + ResultChromeTheme.ControlPadX;
            float figureRight = plan.HasHint ? plan.Hint.X - ResultChromeTheme.ControlPadX : right;
            float figureRoom = figureRight - figureLeft;
            if (figureRoom > 0f && TextFits(figure, metrics.HeaderHintFont, figureRoom, out float figureInk))
            {
                plan.Figure = new LayoutRect(figureLeft, band.Y + inset, figureInk, height);
                plan.HasFigure = plan.Figure.Width > 0f && plan.Figure.Height > 0f;
                plan.FigureLabel = plan.HasFigure ? figure : null;
            }
        }
        return plan;
    }

    // The two storeys this file used to offer a page that anchored nowhere are gone (1.19.45): `HeaderSeat` put the
    // toggle in the board's own header and `DockSeat` docked it at the window's right margin, and both are exactly the
    // "some other visible place" the one-control rule forbids - the toggle must stand beside the page's own control or
    // not be drawn at all. `SeatFree`, which judged those two seats against every rectangle the page draws, went with
    // them. What survives is the band invariant below, and it is asked of the plate that is really on the screen.

    // The band invariant, asked of the plate that is really on the screen: a board is never drawn taller than
    // the strip it was measured into, nor off the window. The placement is supposed to guarantee this; this is
    // the check that makes "supposed to" a fact a repaint can enforce. A board that fails it is deferred - the
    // vanilla page kept - rather than drawn outside its band.
    public static bool PlateInsideBand(LayoutRect plate, float bandTop, float bandBottom, float screenHeight,
        float slack = 0f)
    {
        if (!ResultChromeSlot.Usable(plate)) return false;
        if (!float.IsFinite(slack) || slack < 0f) slack = 0f;
        if (float.IsFinite(screenHeight) && screenHeight > 0f && plate.Bottom > screenHeight - 8f + slack) return false;
        if (float.IsFinite(bandTop) && plate.Y < bandTop - slack) return false;
        if (float.IsFinite(bandBottom) && plate.Bottom > bandBottom + slack) return false;
        return true;
    }

    // Whether a control that has already been committed now stands on something the page itself draws. This
    // is the only thing allowed to take a committed place away - and it takes it away by pausing the control,
    // never by moving it: a native control that really arrived on top of the button is a conflict of two real
    // rectangles, so the test is a real overlap and not the standing gap.
    // 1.19.55: a conflict is a *real* overlap, so it is measured with a depth, not with a touch. The page's own controls
    // breathe - the recorded return button answered at x=745, 744, 741, 743 across one settlement - and a rule that treats a
    // one-pixel touch as "the page's control covers this place" is what paused and released the button 32/30 times in a single
    // session. `tolerance` is that breathing band: an overlap has to be deeper than it on both axes to count. The caller
    // passes the same figure the commit clearance was built from, so a place that was committed with a real gap answers
    // "no conflict" for the whole of the page's own animation.
    public static bool Conflicts(LayoutRect control, IReadOnlyList<LayoutRect> native, float tolerance = 0f)
    {
        if (!(control.Width > 0f) || !(control.Height > 0f) || native == null) return false;
        if (!float.IsFinite(tolerance) || tolerance < 0f) tolerance = 0f;
        for (int index = 0; index < native.Count; index++)
        {
            LayoutRect other = native[index];
            if (!(other.Width > 0f) || !(other.Height > 0f)) continue;
            if (!control.Overlaps(other)) continue;
            // How far the two rectangles really stand inside one another, per axis. Both axes have to be deeper than the
            // tolerance: a button that slid a few pixels sideways shares a sliver of the control's edge and nothing more.
            float depthX = Math.Min(control.Right, other.Right) - Math.Max(control.X, other.X);
            float depthY = Math.Min(control.Bottom, other.Bottom) - Math.Max(control.Y, other.Y);
            if (depthX > tolerance && depthY > tolerance) return true;
        }
        return false;
    }

    // Whether the plate a control was committed to is gone or has moved past the tolerance. Half a pixel,
    // the same tolerance the placement and the band checks use: a plate that slid by a fraction of a pixel -
    // the page's own animation - is the same plate.
    public static bool PlateMoved(LayoutRect committed, LayoutRect live, float tolerance = 0.5f)
    {
        if (!float.IsFinite(tolerance) || tolerance < 0f) tolerance = 0.5f;
        if (!(live.Width > 0f) || !(live.Height > 0f)) return true;
        return Math.Abs(live.X - committed.X) > tolerance || Math.Abs(live.Y - committed.Y) > tolerance
            || Math.Abs(live.Width - committed.Width) > tolerance || Math.Abs(live.Height - committed.Height) > tolerance;
    }

}

// How long a drawn control stays answerable, and what "this frame's rectangle" means.
//
// The rectangle a control is hit through is only ever written by a repaint that really drew it (`DrawChrome`
// drops it at the top and writes one back only for a control it painted). What this adds is the *age* rule: the
// rectangle answers for a bounded number of frames after that repaint, not for exactly one.
//
// Why bounded and not exactly one: Unity does not promise a repaint every frame - a paused window, a frame the
// editor swallowed, a resolution change the game spends on its own layout - and under a one-frame rule a click
// that lands in such a gap is refused with no log and no visible reason. The recorded host pages show the other
// half of the same rule: 45 "shown" presses and not one control press, because the control was never drawn at
// all - a page whose anchor had not arrived. That is answered by the settle rule earning the place, never by a
// rectangle painted somewhere no native control stands.
//
// Why bounded and not "as long as the page lives": a rectangle whose control stopped being drawn is exactly the
// stale hot rectangle this project keeps refusing - a click on the empty page must never take the board away.
// Six frames at 60 Hz is 100 ms, which is a missed repaint and not a hidden control; and the caller passes the
// *current* window too, so a resolution change drops the rectangle regardless of age.
public static class ResultChromeInputPolicy
{
    public const int FreshFrames = 6;

    public static bool Fresh(int drawnFrame, int frame, int window = FreshFrames)
    {
        if (drawnFrame < 0 || frame < drawnFrame) return false;
        int limit = window > 0 ? window : 1;
        return frame - drawnFrame <= limit;
    }
}

// The press discipline of one control.
//
// A click is a press that began on the control and ended on it. The action is taken on the press
// itself - so the page answers the player immediately - but only for a press this gate saw begin on
// the control while the control was really on the screen this frame, and only once per physical
// press. A release has to have been seen - or the button has to have been seen *up* on a frame the
// control was live - which is what keeps the click that opened the result page from closing it; a press
// that starts outside and drags in never counts; and a page change, a resolution change or a lost focus
// throws all of it away rather than leaving a click box behind.
//
// Why "seen up" and not only "seen a release": the release that raised the result page happens before the
// control exists, so nothing ever fires a release edge into a gate that has just been created. Waiting for
// one made the *first* ordinary click on a freshly drawn control dead - the player had to click twice -
// which is one of the two live findings this class answers. A frame with the button up is the same fact as
// a release edge and it is the honest one: nothing is being held, so the next press may be answered.
//
// There is no global input handling anywhere in this: the gate is handed this frame's pointer and
// button state by the caller and answers for the one rectangle it was given.
public sealed class ResultChromeInputGate
{
    private bool _armed;       // the button has been seen up (or released) since this control became live
    private bool _pressInside; // the physical press being tracked began on this control

    // Whether the control is being held down by a press that began on it: the whole of its pressed
    // look, and the reason a press that has already been answered still draws as pressed while the
    // player keeps the button down.
    public bool PressInside => _down && _pressInside;
    private bool _down;
    public int Fires { get; private set; }
    // What the last frame's input decided, in the words the click log uses: "click", "click-quick",
    // "not-armed", "outside", "stale", "not-drawn", "focus-lost", "release", "idle-armed". Never a claim
    // that a board appeared - this is the decision, and `settlement board ... pressed` is the request.
    public string LastWhy { get; private set; } = "idle";
    public bool Armed => _armed;

    public void Reset()
    {
        _armed = false;
        _pressInside = false;
        _down = false;
        LastWhy = "reset";
    }

    // One frame of input for one control. `pressed` is the down edge, `released` the up edge, `held`
    // the button's state after this frame's edges, `inside` this frame's hit, and `rectFresh` whether
    // the rectangle came from a repaint that really drew the control.
    public bool Fire(bool focused, bool rectFresh, bool inside, bool pressed, bool held, bool released)
    {
        if (!focused)
        {
            // A window that lost focus cancels the gesture and needs a fresh release - or a frame with the
            // button up - before it counts again: a drag out of the window and back in must not fire, and a
            // press held across an alt-tab must not answer for whatever the pointer is over when it returns.
            Reset();
            LastWhy = "focus-lost";
            return false;
        }
        bool fire = false;
        if (pressed)
        {
            // A press that arrives in the same frame as its release - a click shorter than one frame, which a
            // low frame rate really delivers - is still one press: it began and ended on the control.
            bool quick = released && !held;
            _pressInside = rectFresh && inside && _armed;
            _down = !quick;
            if (_pressInside)
            {
                fire = true; Fires++; _armed = false;
                LastWhy = quick ? "click-quick" : "click";
            }
            else LastWhy = !rectFresh ? "stale" : !inside ? "outside" : "not-armed";
            if (quick) { _pressInside = false; _armed = true; }
        }
        else _down = held;
        if (released && !held)
        {
            // The physical press is over: the control is live again for the next one, whether the
            // release happened on it or not.
            _armed = true;
            _pressInside = false;
            _down = false;
            if (!pressed) LastWhy = "release";
        }
        else if (!pressed && !held)
        {
            // Nothing is being held: the same fact a release edge carries, and the only one a control that
            // appeared after the last release will ever see.
            _armed = true;
            if (!pressed) LastWhy = "idle-armed";
        }
        if (!rectFresh)
        {
            _pressInside = false; _down = false;
            if (!pressed) LastWhy = "not-drawn";
        }
        return fire;
    }
}

// Whether the board is up on a fresh settlement.
//
// The player's own configuration is the truth for a page that has not been played yet: a switch left
// off in the file stays off, and nothing here writes over it. What this undone is the mod's own hide -
// a press on this page's toggle or close, or the key - so the next settlement starts with the board
// up instead of inheriting a decision made about the previous screen.
public sealed class ResultSettlementSession
{
    private IntPtr _page;
    private bool _hiddenByPress;

    public IntPtr Page => _page;
    public bool HiddenByPress => _hiddenByPress;

    // Called when this page attaches. A page change is this session's start; the hide flag is *not*
    // cleared here, because the fact that this mod was the one that hid the board is exactly what the
    // next page has to be told about (`OweRestore`).
    public bool Starts(IntPtr page)
    {
        bool started = page != _page;
        _page = page;
        return started;
    }

    public void MarkHidden() { _hiddenByPress = true; }
    public void MarkShown() { _hiddenByPress = false; }

    // Asked when a page starts: whether this mod owes the switches a restore because it was the one
    // that turned the board off. The answer is given once - it spends the fact - so a settlement is
    // restored once and a player who hides the *new* page is not answered by the old page's hold.
    public bool OweRestore(IntPtr page)
    {
        if (!_hiddenByPress) return false;
        _hiddenByPress = false;
        _page = page;
        return true;
    }

    public void Forget() { _page = IntPtr.Zero; _hiddenByPress = false; }
}

// ---------------------------------------------------------------------------------------------------
// One place, committed once
// ---------------------------------------------------------------------------------------------------
//
// The page animates its own content in. The return button, the waiting-status line and the board's own plate
// are all somewhere else in the frame a control first measures them, so a control that re-derived its
// rectangle on every repaint *followed* the page: the toggle slid across the screen after the line it stands
// beside, and the close stepped down the board as the plate settled. A player aiming at either of them was
// aiming at a frame that had already gone. This is the fix, and it is deliberately a rule about time rather
// than about geometry: the first place that is committed is the place the control keeps.
//
//   Pending    the page has not settled its own geometry yet. Nothing is drawn and nothing is clickable -
//              no provisional rectangle is ever painted and no hit box is kept for one. F8 is untouched.
//   Committed  the place the control was drawn at, and the only place it is drawn at. A repaint, a key, a
//              pointer, a new figure on the board and a hide/show of the board all leave it alone.
//   Suspended  a native control of the page has arrived on top of the committed place. The control is
//              withheld - no draw, no hit - for as long as that lasts, and comes back at the same rectangle.
//              It is never moved out of the way.
//
// A new page, a new page flavour or a new window is the only thing that forgets a place (`Starts`).
public enum ResultChromePhase { Pending = 0, Committed = 1, Suspended = 2 }

// One control's place, over its whole life on one page.
public sealed class ResultChromeSlot
{
    // How many times a committed place has followed the plate it belongs to. The close lives in the board's
    // reserved header, so a board that is really re-placed into another plate takes its header with it. There is
    // no budget on that: a page that re-measures three times is a page whose close has to be in the third header,
    // and a button that stops coming back is a button the player has lost. The count is reported, never spent.
    // (A plate that is *unusable* still withholds the button - see `Revise` returning false - because a button
    // drawn in a header that is not there is exactly the rectangle that must not exist.)

    private ResultChromePhase _phase = ResultChromePhase.Pending;
    private LayoutRect _rect, _plate;
    private string _why = string.Empty, _pause = string.Empty;
    private int _placements, _revisions, _suspensions;
    // 1.19.48: the native-conflict observation. `_conflictFrame` is the last frame this slot was *asked* about, so a
    // repaint the engine runs more than once in one frame cannot count twice; `_conflictStreak` is how many distinct
    // repaints in a row answered the same way, and `_conflictAnswer` which way that was.
    private int _conflictFrame = int.MinValue, _conflictStreak;
    private bool _conflictAnswer;
    // 1.19.59: how many times a standing conflict has moved this place aside (`MoveAside`). Bounded by `ConflictMoves`.
    private int _conflictMoves;

    public ResultChromePhase Phase => _phase;
    public LayoutRect Rect => _rect;
    // The plate the place belongs to (the close's header owner). Default for a control that has none.
    public LayoutRect Plate => _plate;
    public string Why => _why;
    public string Pause => _pause;
    // How many times this slot was given a rectangle. One is the normal life of a control on a page.
    public int Placements => _placements;
    public int Revisions => _revisions;
    public int Suspensions => _suspensions;
    // Whether a place has been earned at all: a committed or a paused control still owns its rectangle.
    public bool Held => _phase != ResultChromePhase.Pending;
    // Whether this frame may draw and answer for the rectangle: committed, and a rectangle that is really one.
    public bool Live => _phase == ResultChromePhase.Committed && Usable(_rect);

    public static bool Usable(LayoutRect rect)
        => rect.Width > 0f && rect.Height > 0f && float.IsFinite(rect.X) && float.IsFinite(rect.Y)
           && float.IsFinite(rect.Width) && float.IsFinite(rect.Height);

    // The one write a pending place may make. A committed place is never re-derived, which is what keeps the
    // control from following the page's animation; a place that cannot be drawn is not committed at all.
    public bool Commit(LayoutRect rect, string why) => Commit(rect, default, why);

    public bool Commit(LayoutRect rect, LayoutRect plate, string why)
    {
        if (_phase != ResultChromePhase.Pending || !Usable(rect)) return false;
        _rect = rect; _plate = plate; _why = why ?? string.Empty; _pause = string.Empty;
        _phase = ResultChromePhase.Committed; _placements++;
        return true;
    }

    // The plate the place belongs to was replaced by another one. Counted, never a repaint.
    public bool Revise(LayoutRect rect, LayoutRect plate, string why)
    {
        if (_phase == ResultChromePhase.Pending) return Commit(rect, plate, why);
        if (!Usable(rect) || !(plate.Width > 0f) || !(plate.Height > 0f)) return false;
        _rect = rect; _plate = plate; _why = why ?? string.Empty; _pause = string.Empty;
        _phase = ResultChromePhase.Committed; _revisions++; _placements++;
        return true;
    }

    // A real native rectangle has stood on the committed one for `ConflictFrames` distinct repaints: the control is
    // withheld, not moved. One repaint of overlap is a flicker and never reaches this method (1.19.48).
    public bool Suspend(string why)
    {
        if (_phase != ResultChromePhase.Committed) return false;
        _phase = ResultChromePhase.Suspended;
        _pause = why ?? string.Empty;
        _suspensions++;
        return true;
    }

    // The conflict has left: the same rectangle comes back.
    public bool Resume()
    {
        if (_phase != ResultChromePhase.Suspended) return false;
        _phase = ResultChromePhase.Committed;
        _pause = string.Empty;
        return true;
    }
    // 1.19.59: a native rectangle that really stands on the place is answered by moving the place *once*, not by withholding
    // the control for as long as the page draws that rectangle.
    //
    // The recorded r4 page committed the toggle at (913,1323,121x37) beside the return button and a second native rectangle
    // (587x107, x 1013..1600) overlapped it by 21 x 37 px: deep enough for `Conflicts` to call it a conflict, and - because
    // the page's own button breathes under the pointer - it came and went twelve times (`suspensions=1..12`), each pair
    // taking the button away from the pointer that was aiming at it. A place that a measured rectangle really covers is a
    // place that was measured before that rectangle was known: the honest answer is the same search the commit ran, asked
    // again with the rectangles this repaint really sees, and the button keeps existing throughout. `ConflictMoves` bounds
    // it, so a page that keeps growing rectangles under it ends in the withhold it always had instead of a button that walks
    // around the screen.
    public const int ConflictMoves = 2;
    public bool MayMoveAside => _conflictMoves < ConflictMoves;

    public bool MoveAside(LayoutRect rect, string why)
    {
        if (_conflictMoves >= ConflictMoves) return false;
        if (!Usable(rect)) return false;
        _rect = rect; _why = why ?? string.Empty; _pause = string.Empty;
        _phase = ResultChromePhase.Committed; _revisions++; _placements++; _conflictMoves++;
        // The conflict was answered by this move, so the observation starts again from nothing: the streak describes the
        // place that no longer exists.
        _conflictAnswer = false; _conflictStreak = 0;
        return true;
    }

    // How many *distinct* repaints a native rectangle has to stand on the committed place before the control is withheld.
    //
    // One repaint is not a conflict. The recorded 2K run lost the button to four one-frame overlaps inside eight frames: the
    // log carries four `paused`/`released` pairs (`settlement toggle paused` at 5134/5136/5138/5140) while the pointer sat at
    // (949,1239) inside the committed (882,1214,121x37) - and `toggleRect=none` on the frames between, because the pause had
    // already taken the rectangle away. Every one of those pairs took the button out from under a player aiming at it, and
    // the 1K pages did the same (4554-4566). A native control that really stands on the place stands there for as long as the
    // page draws it - hundreds of repaints - so this threshold costs a real conflict nothing and costs a flicker everything.
    public const int ConflictFrames = 3;

    // One repaint's answer about the page's own rectangles, counted once per *frame*.
    //
    // `frame` is `Time.frameCount`. That is the whole point of the parameter: `OnGUI` runs several times in one frame
    // (Layout, Repaint, MouseDown...), so a rule that counted calls would withhold the control on three events of a single
    // frame - which is the same single-frame pause this replaces, wearing a bigger number. The same frame asked again is the
    // same observation and changes nothing. A caller that does not name a frame (`frame < 0`, which is what the headless
    // replays leave it at) is taken as one repaint per call, exactly as before.
    public void ObserveConflict(bool overlap, int frame)
    {
        if (frame >= 0)
        {
            if (frame == _conflictFrame) return;
            // Only *consecutive* frames count. A frame the chrome was not drawn on (`frame - _conflictFrame > 1`) is a gap:
            // nothing was observed in between, so what stood on the place may have left and come back - the run starts again
            // instead of treating two samples an arbitrary time apart as one continuous conflict.
            bool adjacent = _conflictFrame >= 0 && frame == _conflictFrame + 1;
            _conflictFrame = frame;
            if (!adjacent) _conflictStreak = 0;
        }
        if (_conflictStreak > 0 && _conflictAnswer == overlap) _conflictStreak++;
        else { _conflictAnswer = overlap; _conflictStreak = 1; }
    }

    // Whether a native rectangle has stood on the place for `ConflictFrames` distinct repaints in a row.
    public bool ConflictStanding => _conflictAnswer && _conflictStreak >= ConflictFrames;
    // Whether the place has been clear for as many: the control comes back only once the conflict has really left, so a
    // one-frame gap inside a flicker does not hand the player a button that is taken away again on the next repaint.
    public bool ConflictCleared => !_conflictAnswer && _conflictStreak >= ConflictFrames;
    // The overlap that did not survive the threshold. Nothing is withheld for it - it is reported, so a page that really is
    // flickering can be read out of the log instead of guessed at.
    public bool ConflictFlicker => _conflictAnswer && _conflictStreak < ConflictFrames;
    public int ConflictStreak => _conflictStreak;

    public void Forget()
    {
        _phase = ResultChromePhase.Pending;
        _rect = default; _plate = default;
        _why = _pause = string.Empty;
        _placements = _revisions = _suspensions = 0;
        // 1.19.48: the conflict streak is this place's observation, so a new page/flavour/window starts it from nothing.
        _conflictFrame = int.MinValue; _conflictStreak = 0; _conflictAnswer = false;
        _conflictMoves = 0;
    }
}

// What the one place belongs to: one page, one page flavour, one settlement, one window.
public sealed class ResultChromeCommit
{
    private readonly ResultChromeSlot _toggle = new ResultChromeSlot();
    private IntPtr _page;
    private int _width, _height, _session;
    private bool _succeeded, _started;
    private int _contexts, _relayouts;

    public ResultChromeSlot Toggle => _toggle;
    public IntPtr Page => _page;
    public bool Started => _started;
    // How many distinct pages/settlements/windows this commitment has been started for. The one honest measure
    // of "this layout belongs to a page": a repaint must never add to it.
    public int Contexts => _contexts;
    // How many times the board asked to be re-measured while this layout stood. A re-measure is not a place.
    public int Relayouts => _relayouts;
    // Whether the toggle still needs the page asked for an anchor. Once its place is committed the page is not
    // measured for it again - only the late-conflict check reads the page's own rectangles.
    public bool NeedsAnchor => _toggle.Phase == ResultChromePhase.Pending;

    // True only when this call really started a new commitment: a different page object, a different page
    // flavour, a different settlement on the same page object, or a window of a different size.
    //
    // A *missing* page is not a new page. The caller cannot read the result page for one repaint (the board was
    // hidden, the layout cache was dropped, the page object went inactive) and hands over `IntPtr.Zero`; the
    // places that were earned on that page are still the places the player is aiming at, so they are kept and
    // only their drawing is suspended for that frame. An unreadable window is the same kind of fact.
    public bool Starts(IntPtr page, bool succeeded, int width, int height, int session)
    {
        bool readable = width > 0 && height > 0;
        // No page and no window are not new pages: neither is a measurement of anything, so neither may spend a
        // place. The caller draws nothing for that repaint and asks again on the next one.
        if (!readable || page == IntPtr.Zero) return false;
        if (_started && page == _page && succeeded == _succeeded && width == _width && height == _height
            && session == _session)
            return false;
        _page = page; _succeeded = succeeded; _width = width; _height = height; _session = session;
        _started = true; _contexts++;
        _toggle.Forget();
        return true;
    }

    // The board's own layout was dropped (a configuration reload, an F8 press, a re-measure). The toggle stands
    // beside the page's own control, so nothing about a re-measure moves it: a re-layout keeps its place.
    public void Relayout() { _relayouts++; }

    public void Forget()
    {
        _toggle.Forget();
        _page = IntPtr.Zero; _width = _height = 0; _succeeded = false; _started = false; _session = 0;
    }
}

// How far the native thing a control stands beside has come in.
//
// Three answers, because three different facts arrive at this one question and treating them as one is what the
// recorded client page showed:
//
//   Arrived     an anchor that is proven by activation (a button the page has switched on) has no reveal at all,
//               and a text whose own alpha has reached full is a line that has finished fading in. Both are
//               arrived - and only the second one has ever read an alpha.
//   Waiting     a text whose alpha is readable and below full is still fading in. Nothing is placed against it.
//   Unreadable  a text that measured, but whose alpha could not be read at all (a destroyed or half-initialised
//               TMP, a component that throws). A not-a-number is *not* "arrived": it is a measurement that failed,
//               and a failed read never becomes a place - not on its own, and not from repeating the rectangle it
//               happened to measure. The settle rule then keeps no history at all, so the control stays pending
//               (drawn nowhere, clickable nowhere) until a real read reports `Arrived` twice in a row. It is also
//               not a verdict of "this page has no waiting line": the pending line names the failed read as such,
//               and the page is measured again on the next repaint.
public enum ResultChromeReveal { Arrived = 0, Waiting = 1, Unreadable = 2 }

// Whether the thing a control stands beside has stopped moving.
//
// This is not a frame count and it is not a timeout: the anchor is handed over every repaint, and it is only
// trustworthy once the *same* rectangle has been measured twice - which is a statement about the page's
// geometry, not about how long the mod has been waiting. One repaint in which the anchor is anywhere else (a
// page still sliding in, a board re-fitting under it) starts the observation again, and a page whose anchor
// never stands still hands over no place at all rather than a place that is about to be wrong.
public sealed class ResultChromeSettle
{
    public const float TolerancePixels = 1.5f;
    public const float Arrived = 0.999f;

    private ResultChromeAnchor _source = ResultChromeAnchor.None;
    private LayoutRect _last;
    private bool _have;
    private int _samples, _restarts;

    public bool Have => _have;
    public int Samples => _samples;
    public int Restarts => _restarts;

    public string Describe()
        => _have ? "moving(restarts=" + _restarts + ",samples=" + _samples + ",last=" + _source + ")" : "unobserved";

    // `reveal` is what the page said about the thing it drew, and only `Arrived` may earn a place.
    //
    // A line that is still fading in and a line whose alpha could not be read are both *not arrived*: the
    // observation is thrown away (so whatever stability was built up before cannot be inherited), nothing is
    // placed, nothing is drawn and no click box is kept. There is deliberately no second guess here. The only
    // other alpha read in this mod (`ControlAlpha`) answers 1 for an alpha it could not read, so calling it would
    // turn "unknown" into "fully arrived" - the one substitution this rule exists to refuse. When the alpha comes
    // back, the place has to be earned again from two fresh `Arrived` samples.
    public ResultChromeReveal LastReveal { get; private set; } = ResultChromeReveal.Arrived;

    public bool Observe(LayoutRect anchor, ResultChromeAnchor source, ResultChromeReveal reveal)
    {
        if (!ResultChromeSlot.Usable(anchor) || source == ResultChromeAnchor.None)
        {
            Forget();
            return false;
        }
        if (reveal != ResultChromeReveal.Arrived)
        {
            // Fading, or an alpha nobody could read: not this frame, not that rectangle, and no history kept.
            Forget();
            LastReveal = reveal;
            return false;
        }
        LastReveal = reveal;
        if (_have && _source == source && Near(anchor, _last))
        {
            _samples++;
            _last = anchor;
            return true;
        }
        if (_have) _restarts++;
        _last = anchor; _source = source; _have = true; _samples = 1;
        return false;
    }

    private static bool Near(LayoutRect first, LayoutRect second)
        => Math.Abs(first.X - second.X) <= TolerancePixels && Math.Abs(first.Y - second.Y) <= TolerancePixels
           && Math.Abs(first.Width - second.Width) <= TolerancePixels && Math.Abs(first.Height - second.Height) <= TolerancePixels;

    public void Forget()
    {
        _have = false; _samples = 0; _source = ResultChromeAnchor.None; _last = default;
    }
}

// One repaint's measurement of the page, as the caller really measured it. Nothing here is derived by the
// policy and nothing is guessed: an anchor the page did not draw says so, and a plate that could not be read
// this frame says so too. The caller keeps one instance and refills it, so a repaint allocates nothing.
public sealed class ResultChromeProbe
{
    public IntPtr Page;
    public bool Succeeded;
    // Which settlement the page is showing. The game keeps both result pages alive for the whole session and
    // raises them again for every expedition, so the page object can be the *same* one two settlements running:
    // the session identity is what makes the second settlement a new layout.
    public int Session;
    // The engine frame this repaint belongs to (`Time.frameCount`). It exists for one rule: `OnGUI` runs more than once per
    // frame (Layout, Repaint, MouseDown...), so a "this lasted N frames" test may not count calls. Left at -1 by a caller
    // that does not name a frame - which is what the headless replays do - every call is taken as its own observation.
    public int Frame = -1;
    public int ScreenWidth = 1920, ScreenHeight = 1080;
    // Whether a board is up this repaint: the close belongs to the board, the toggle does not.
    public bool BoardUp = true;
    // Whether the page drew something to stand beside this repaint, and what it was.
    public bool AnchorDrawn;
    public LayoutRect Anchor;
    public ResultChromeAnchor Source = ResultChromeAnchor.None;
    // How far that thing has come in: `Arrived` for an activation-proven control, and for a text the alpha the
    // page reported - including `Unreadable`, which is a failed read and never an arrival.
    public ResultChromeReveal Reveal = ResultChromeReveal.Arrived;
    public string AnchorWhy = "no-anchor";
    // Every rectangle the page draws of its own (its own controls and its waiting-status line).
    public IReadOnlyList<LayoutRect> Native;
    // The board's plate, header included, when it could be measured this repaint.
    public bool PlateDrawn;
    public LayoutRect Plate;

    public void Reset()
    {
        Page = IntPtr.Zero; Succeeded = false; Session = 0; ScreenWidth = 1920; ScreenHeight = 1080;
        Frame = -1;
        BoardUp = true; AnchorDrawn = false; Anchor = default; Source = ResultChromeAnchor.None;
        Reveal = ResultChromeReveal.Arrived; AnchorWhy = "no-anchor"; Native = null;
        PlateDrawn = false; Plate = default;
    }
}

// What one repaint decided. The caller draws exactly this and nothing else, and keeps a click box only for
// the one rectangle that is really on the screen this frame.
public struct ResultChromeDecision
{
    public bool NewContext;      // this repaint started a new commitment (new page, flavour or window)
    public bool Pending;         // no trustworthy place for the toggle yet: nothing is drawn for it
    public bool ToggleDrawn; public LayoutRect Toggle;
    public bool HeaderDrawn; public LayoutRect Header; public LayoutRect Plate;
    public bool ToggleCommitted;
    public bool ToggleSuspended, ToggleResumed;
    // 1.19.48: the pause is no longer decided by a single repaint, so the decision says three things about it instead of
    // one. `TogglePaused` is the state (the control is withheld this repaint), `ToggleConflict` names which of the page's
    // own rectangles stands on it and `ToggleFlicker` marks an overlap that did not survive `ResultChromeSlot.ConflictFrames`
    // - the button was kept, and the line says so.
    public bool TogglePaused;
    public bool ToggleFlicker;
    // 1.19.59: a standing native conflict moved the place instead of withholding it. The line names the rectangle it moved
    // away from, so a reader can tell "the page's control arrived on the toggle" from "the toggle moved out of its way".
    public bool ToggleMoved;
    public string ToggleConflict;
    public string ToggleWhy;
}

// The reveal in the words a log line uses.
public static class ResultChromeRevealName
{
    public static string Of(ResultChromeReveal reveal)
        => reveal == ResultChromeReveal.Waiting ? "fading"
            : reveal == ResultChromeReveal.Unreadable ? "alpha-unreadable"
            : "arrived";
}

// Which of the page's own rectangles stands on one of this mod's, in the words the pause line uses (1.19.48).
//
// The recorded 2K frame that started this carried `toggleRect=none` and `control=none` beside a pointer that was inside the
// committed rectangle, so the log could not say *what* had taken the place away - and a reader had to guess between a native
// control, a page that moved and a bug. Every paused line now names the rectangle it found, by index into the page's own
// list, with that rectangle's pixels: what was measured, not a theory about it.
public static class ResultChromeConflict
{
    public static string Describe(LayoutRect control, IReadOnlyList<LayoutRect> native)
    {
        if (native == null) return "overlaps=none";
        var text = new System.Text.StringBuilder(96);
        for (int index = 0; index < native.Count; index++)
        {
            LayoutRect other = native[index];
            if (!(other.Width > 0f) || !(other.Height > 0f)) continue;
            if (!float.IsFinite(other.X) || !float.IsFinite(other.Y)) continue;
            if (!control.Overlaps(other)) continue;
            if (text.Length > 0) text.Append(';');
            text.Append('#').Append(index).Append('(').Append(other.X.ToString("0")).Append(',').Append(other.Y.ToString("0"))
                .Append(',').Append(other.Width.ToString("0")).Append('x').Append(other.Height.ToString("0")).Append(')');
        }
        return text.Length == 0 ? "overlaps=none" : "overlaps=" + text;
    }
}

// The step itself: one repaint in, what may be drawn and hit out.
public static partial class ResultChromeLayout
{
    // One repaint of the settlement controls.
    //
    // This is the whole policy the renderer runs, and it is deliberately free of Unity so the timing rules
    // - the page still moving, a native control arriving late, a new page or window, a hide and a show - are
    // asserted headlessly. `commitment` carries the one place, `settle` the anchor observation, and `probe`
    // this frame's measurement of the page; the answer is the rectangle that may be drawn and hit *this* frame,
    // which is empty for the toggle while the page has not settled, empty while the anchor has not arrived twice
    // in a row, and empty while a native rectangle stands on it. There is no second control and no fallback seat.
    public static ResultChromeDecision Step(ResultChromeCommit commitment, ResultChromeSettle settle,
        ResultChromeMetrics metrics, ResultChromeProbe probe)
    {
        var decision = new ResultChromeDecision();
        if (commitment == null || probe == null) return decision;
        decision.NewContext = commitment.Starts(probe.Page, probe.Succeeded, probe.ScreenWidth, probe.ScreenHeight, probe.Session);
        if (decision.NewContext && settle != null) settle.Forget();
        float width = probe.ScreenWidth, height = probe.ScreenHeight;

        // ---- the toggle: one anchor, one place, one time --------------------------------------------
        ResultChromeSlot toggle = commitment.Toggle;
        if (toggle.Phase == ResultChromePhase.Pending)
        {
            // A place beside the page's own control is earned, never guessed: an anchor that is fading in, still
            // moving, or whose alpha could not be read leaves the toggle pending for this repaint, and there is no
            // second storey to take that place over. The page is asked again on the next repaint, which is what lets
            // a host button that only arrives late be answered at all.
            if (!probe.AnchorDrawn)
            {
                if (settle != null) settle.Forget();
                decision.ToggleWhy = "anchor=" + (probe.AnchorWhy ?? "no-anchor");
            }
            else if (settle == null || !settle.Observe(probe.Anchor, probe.Source, probe.Reveal))
            {
                // Still on its way: nothing is drawn for it this repaint, and no click box exists.
                decision.ToggleWhy = "anchor=" + (probe.AnchorWhy ?? "no-anchor")
                    + ", reveal=" + ResultChromeRevealName.Of(probe.Reveal)
                    + ", settle=" + (settle != null ? settle.Describe() : "unobserved");
            }
            else
            {
                // Every rectangle this repaint really measured: the page's own controls (the anchor itself is
                // one of them), and this mod's plate, so a control never hides under the board it belongs to.
                var obstacles = new List<LayoutRect>((probe.Native != null ? probe.Native.Count : 0) + 2);
                if (probe.Native != null) obstacles.AddRange(probe.Native);
                obstacles.Add(probe.Anchor);
                if (probe.PlateDrawn) obstacles.Add(probe.Plate);
                float clearance = CommitClearance(metrics);
                if (TryToggle(probe.Anchor, obstacles, width, height, metrics, out LayoutRect place, out string placeWhy, clearance))
                    decision.ToggleCommitted = toggle.Commit(place, (probe.AnchorWhy ?? "anchor") + "/" + placeWhy);
                else
                    decision.ToggleWhy = "anchor=" + (probe.AnchorWhy ?? "no-anchor") + ", place=" + placeWhy;
            }
            // A page that draws no anchor this repaint gets no control this repaint: there is deliberately no second
            // storey to fall back to. The recorded host failure page answered `anchor=no-host-action` for its first
            // repaints and `HostActionButton` once the return button was really drawn, and the settle rule earns that
            // place from two fresh arrived samples on the repaint the button appears - which is exactly the late
            // arrival this has to keep answering. The page is measured again on the next repaint.
        }
        if (toggle.Held)
        {
            // The only thing that may take a committed place away: a native rectangle that really stands on top of it. The
            // *only* (1.19.48) is a repaint: it has to answer the same way for `ResultChromeSlot.ConflictFrames` distinct
            // frames before the control is withheld, because the recorded 2K pages answered a single frame of overlap at a
            // time and every one of those withdrew the button from under the pointer (log 5134-5141). One flicker therefore
            // costs nothing, and a conflict that survives the threshold still only withholds - the draw does not move.
            // 1.19.55: the tolerance is the page's own breathing, and `Conflicts` only answers "yes" for an overlap deeper
            // than it. A committed place with the commit clearance in front of it therefore reports no conflict at all for
            // the ordinary hover/idle animation, and the pause stays what it says it is: two real rectangles on top of each
            // other.
            bool overlap = Conflicts(toggle.Rect, probe.Native, ConflictTolerance(metrics));
            toggle.ObserveConflict(overlap, probe.Frame);
            string conflict = overlap ? ResultChromeConflict.Describe(toggle.Rect, probe.Native) : null;
            if (toggle.ConflictStanding)
            {
                // 1.19.59: the conflict is answered by moving the place once (bounded by `ConflictMoves`), not by withholding
                // the control for as long as the page draws that rectangle. The search is the commit's own - the same anchor,
                // the same obstacles, the same clearance - asked with the rectangles this repaint really measured, so a place
                // a late native control covers is re-earned instead of blinked away twelve times.
                var obstacles = new List<LayoutRect>((probe.Native != null ? probe.Native.Count : 0) + 2);
                if (probe.Native != null) obstacles.AddRange(probe.Native);
                obstacles.Add(probe.Anchor);
                if (probe.PlateDrawn) obstacles.Add(probe.Plate);
                bool aside = toggle.MayMoveAside
                    && TryToggle(probe.Anchor, obstacles, width, height, metrics, out LayoutRect next, out string moveWhy, CommitClearance(metrics))
                    && (Math.Abs(next.X - toggle.Rect.X) > 1f || Math.Abs(next.Y - toggle.Rect.Y) > 1f)
                    && toggle.MoveAside(next, (probe.AnchorWhy ?? "anchor") + "/" + moveWhy + "/moved-aside");
                decision.ToggleMoved = aside;
                if (aside) decision.ToggleConflict = "moved-aside from " + conflict;
                else
                    decision.ToggleSuspended = toggle.Suspend("native-conflict " + conflict
                        + " frames=" + toggle.ConflictStreak);
            }
            else if (toggle.ConflictCleared)
                decision.ToggleResumed = toggle.Resume();
            decision.TogglePaused = toggle.Phase == ResultChromePhase.Suspended;
            decision.ToggleFlicker = toggle.ConflictFlicker;
            if (!decision.ToggleMoved) decision.ToggleConflict = conflict;
            if (toggle.Live)
            {
                decision.ToggleDrawn = true;
                decision.Toggle = toggle.Rect;
                decision.ToggleWhy = toggle.Why;
            }
        }
        else decision.Pending = true;

        // ---- the board's own header band ----------------------------------------------------------------
        // The band is the space the board reserved above its table: the board's name and the key that operates it,
        // planned from the plate this repaint really measured. It carries no control of this mod's own (1.19.45),
        // so there is nothing here to place, draw, hover, hit or withhold - only the band itself.
        if (probe.BoardUp && probe.PlateDrawn)
        {
            decision.Plate = probe.Plate;
            decision.Header = HeaderOf(decision.Plate, metrics);
            decision.HeaderDrawn = decision.Header.Width > 0f && decision.Header.Height > 0f;
        }
        return decision;
    }
}
