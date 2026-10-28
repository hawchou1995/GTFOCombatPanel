using System;

namespace HawChow.GTFOCombatPanel;

// ---------------------------------------------------------------------------------------------------
// ResultVisibility.cs - when the settlement board may be up.
//
// Everything here is free of Unity and of BepInEx so the whole of it is asserted headlessly in
// tests/GTFOCombatPanel.Smoke. Each piece exists because of a fault that was seen in game:
//
//   ResultVisibility.Shows   the switch gate F8 writes into, read where the board is drawn and holding
//                            no state of its own, so the next Repaint after the key already agrees with
//                            it and nothing between the key and the pixels can hold it back.
//   CompletionReadiness      the success page's readiness latch. The board waits until the page's
//                            completion section - the sector name and the sector icon - has really been
//                            drawn, and then keeps that answer for the rest of the page. There is no
//                            elapsed-time bound and no "the section stopped changing" escape anywhere in
//                            it: those let a half-drawn page through, and a readiness that was already
//                            earned must not be given up again because the page's animation moved on.
//   ResultBoardHold          where the board stands. The first placement has to fit the strip the page
//                            measured; a board that is already up keeps the place it was measured at,
//                            because the pages animate their own buttons and a button's own bounds move a
//                            pixel or two while it is hovered. The lobby button stays a wall throughout:
//                            a held board that really would reach it is lifted off it, never dropped.
//   ResultBoardLatch         whether the board may stand in for the game's own result blocks. Those blocks are
//                            only ever put away for a board that was really fitted, has the grid and content it
//                            will draw, and is therefore about to be drawn; a frame whose placement was refused
//                            hides nothing, leaves the page exactly as the game drew it and is asked again.
// ---------------------------------------------------------------------------------------------------

// A placement, a measured strip and an earned readiness all belong to one result page. A different page
// is a different screen, so nothing measured on the previous one is reused on it: a failure placement can
// never be carried onto a success page and a readiness earned on one expedition is never spent on the
// next. A page that is not named at all - the results screens have been left - is a change as well.
public static class ResultVisibility
{
    // The three switches and the F8 toggle. The runtime reads this on the frame the board is drawn rather
    // than on the frame it was placed, which is the whole of the F8 path on a result screen: the answer
    // for the next frame needs nothing but the four flags, so a toggle takes effect on the next Repaint
    // whatever the rest of the board is in the middle of.
    public static bool Shows(bool masterSwitch, bool overlaySwitch, bool boardSwitch, bool toggled)
        => masterSwitch && overlaySwitch && boardSwitch && toggled;

    public static bool SamePage(IntPtr cached, IntPtr current)
        => current != IntPtr.Zero && cached == current;

    public static bool PageChanged(IntPtr cached, IntPtr current) => cached != current;
}

// The one latch that decides whether the board stands in for the game's own result blocks.
//
// `Fit` may refuse a board outright - the strip the page measured came back shorter than the table - and it used to
// answer its caller with silence: the refresh path then recorded the placement anyway, `ApplyVanillaBlocks` put the
// game's own blocks away for a board `Draw` would never paint (it returns the moment the grid or the content is
// missing), and the settlement page was left with neither. A refusal is a refusal here: the frame hides nothing, the
// page stays exactly as the game drew it, and it is asked again on the next one.
public static class ResultBoardLatch
{
    // A placement counts only when the fit confirmed it and the grid and the content it will draw are the ones it
    // fitted. A frame that fitted nothing has nothing to draw and therefore nothing to stand in for.
    public static bool Placed(bool fitConfirmed, bool gridReady, bool contentReady)
        => fitConfirmed && gridReady && contentReady;

    // The game's own blocks make way only for a board that is really there.
    public static bool HidesVanilla(bool placed) => placed;

    // Whether the page still has to be attached. A board that was never really placed is retried on the next frame
    // instead of being recorded as done, which is the half of the rule that makes a transient refusal cost one frame
    // rather than the rest of the page.
    public static bool NeedsAttach(bool placed, bool succeededMatches) => !placed || !succeededMatches;
}

// One frame of evidence about the success page's completion section, taken from the page's own objects:
// the sector name block and the sector icon. `Present` means the object is on the active page; `Drawn`
// means it is really being rendered - the name carries a text whose own render alpha has arrived, and the
// icon's own sprites have finished the reveal the game fades them in with. An object that exists but has
// not been drawn is not evidence of anything, and no amount of elapsed time turns one into the other.
public readonly struct CompletionEvidence
{
    public readonly bool NamePresent;
    public readonly bool NameDrawn;
    public readonly bool IconPresent;
    public readonly bool IconDrawn;
    // Whether the icon exposed any artwork to read at all. A build that hands the icon no sprite renderers has
    // given no evidence that it has been drawn, and "no evidence" is not "drawn": the board waits rather than
    // guessing, which is the whole point of the latch below.
    public readonly bool IconReadable;

    public CompletionEvidence(bool namePresent, bool nameDrawn, bool iconPresent, bool iconDrawn, bool iconReadable)
    {
        NamePresent = namePresent; NameDrawn = nameDrawn;
        IconPresent = iconPresent; IconDrawn = iconDrawn; IconReadable = iconReadable;
    }

    // Both halves, or nothing. A page whose name is up but whose icon is still arriving is not ready, and
    // neither is the other way round.
    public bool Complete => NamePresent && NameDrawn && IconPresent && IconDrawn && IconReadable;
}

// The one per-page readiness latch of the success board. It is fed the page's own evidence every frame the
// board is not yet up, latches the first frame that is complete, and then answers "ready" for the rest of
// the page's life. The page keeps animating after the board has arrived - its captions and its icons
// pulse - and a frame of that must not take the answer away.
public sealed class CompletionReadiness
{
    private IntPtr _page;
    private bool _ready;

    public IntPtr Page { get { return _page; } }
    public bool Ready { get { return _ready; } }

    // Binds the latch to a page. Returns true when that was a different page, which is the only thing that
    // starts the wait over. A page that is already the latched one keeps whatever it has earned.
    public bool Bind(IntPtr page)
    {
        if (page == _page) return false;
        _page = page;
        _ready = false;
        return true;
    }

    public bool Observe(CompletionEvidence evidence)
    {
        if (!_ready && evidence.Complete) _ready = true;
        return _ready;
    }

    public void Forget()
    {
        _page = IntPtr.Zero;
        _ready = false;
    }
}

// Compare against the target alpha the game supplied for this renderer, not a global floor:
// 0.5 is complete for a 0.5 target but still fading toward a 0.8 target.
public static class CompletionDrawing
{
    public const float TextAlpha = 0.99f;

    public static bool Text(float renderedAlpha) => float.IsFinite(renderedAlpha) && renderedAlpha >= TextAlpha;
    public static bool Icon(float renderedAlpha, float targetAlpha = 0.5f)
        => float.IsFinite(renderedAlpha) && float.IsFinite(targetAlpha) && targetAlpha > 0f
            && renderedAlpha >= targetAlpha * 0.99f;

    public static bool AllSectors(bool headerDrawn, int expected, int namesDrawn, int iconsDrawn)
        => headerDrawn && expected > 0 && namesDrawn == expected && iconsDrawn == expected;

}

// The strip of a result page the board may use, together with the button it may never reach. `ButtonTop`
// is the lobby button's own measured top edge and is only meaningful when `ButtonMeasured` is set: a page
// whose button could not be measured has nothing to protect and nothing to guess at.
//
// `ButtonHeight` is that same button's own measured height, and it is the one figure that says how large a
// control the player is being asked to press. The settlement board derives its readability floor from it,
// so a page that offers no measurable height keeps the absolute minimum and behaves exactly as before.
//
// `AnchorMeasured` is whether the strip's *upper* edge - the failure title's own bottom, the success
// completion section's - was measured in its settled state, rather than taken from a remembered line or a
// frame in which the page was still landing. A strip whose button or whose anchor was a guess is the one case
// that may be measured a second time; see `ResultAnchorRecovery` and `ResultAnchorDrift`.
public readonly struct BoardStrip
{
    public readonly float Top, Bottom;
    public readonly bool ButtonMeasured;
    public readonly float ButtonTop;
    public readonly float ButtonHeight;
    public readonly bool AnchorMeasured;

    public BoardStrip(float top, float bottom, bool buttonMeasured, float buttonTop, float buttonHeight = float.NaN,
        bool anchorMeasured = true)
    {
        Top = top; Bottom = bottom; ButtonMeasured = buttonMeasured; ButtonTop = buttonTop; ButtonHeight = buttonHeight;
        AnchorMeasured = anchorMeasured;
    }
}

// Placing the board and then holding it in place.
public static class ResultBoardHold
{
    // Clearance the board keeps above the lobby button, independent of the gap the page's own strip
    // leaves: that gap is comfort, the button is the one thing the player has to be able to press.
    public const float ButtonClearance = 6f;

    // The first placement: the board fits inside the strip between the two measured edges, with the same
    // tolerance the draw path allows for a button whose bounds moved between the measurement and the
    // draw. A board that cannot fit is refused here rather than pushed over a button.
    //
    // `bottomAligned` puts the board's lower edge on the strip's lower edge instead of its upper edge on
    // the strip's top. The strip ends where the button begins, so a table shorter than the room it was
    // given lands in the gap the player is really looking at - right above the button - rather than leaving
    // an empty band there and clinging to the completion section above.
    public static bool TryPlace(float screenWidth, float screenHeight, BoardStrip strip, float width, float height,
        float offsetX, out LayoutRect rect, float slack = 0f, bool bottomAligned = false)
    {
        rect = default;
        if (!float.IsFinite(screenWidth) || !float.IsFinite(screenHeight) || screenHeight <= 0f) return false;
        if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0f || height <= 0f) return false;
        if (!float.IsFinite(strip.Top) || !float.IsFinite(strip.Bottom)) return false;
        if (!float.IsFinite(slack) || slack < 0f) slack = 0f;
        float top = Math.Max(0f, strip.Top);
        float bottom = Math.Min(strip.Bottom, screenHeight - 8f) + slack;
        if (!(bottom > top) || top + height > bottom) return false;
        float x = (screenWidth - width) * 0.5f + (float.IsFinite(offsetX) ? offsetX : 0f);
        rect = bottomAligned
            ? new LayoutRect(x, Math.Max(top, bottom - height), width, height)
            : new LayoutRect(x, top, width, height);
        return true;
    }

    // A board that is already up. It keeps the rectangle it was measured into; a strip that comes back a
    // little shorter than the one it was measured against - which is what the pages' own button animation
    // does - does not take it away, and the board is never moved down. The button is still a wall: a board
    // that really would reach it is lifted onto the clearance line, and only if even that would leave the
    // page is it left where it is, because there is nothing else to do with it that keeps both promises.
    public static bool TryHold(BoardStrip strip, LayoutRect placed, float screenHeight, out LayoutRect rect)
    {
        rect = placed;
        if (!(placed.Width > 0f) || !(placed.Height > 0f)) return false;
        if (!float.IsFinite(screenHeight) || screenHeight <= 0f) return false;
        if (!strip.ButtonMeasured || !float.IsFinite(strip.ButtonTop)) return true;
        float wall = Math.Min(strip.ButtonTop - ButtonClearance, screenHeight - 8f);
        if (!float.IsFinite(wall)) return true;
        if (placed.Bottom <= wall) return true;
        rect = new LayoutRect(placed.X, Math.Max(0f, wall - placed.Height), placed.Width, placed.Height);
        return true;
    }

    // Whether a grid that arrived *after* the board was placed may be drawn in the rectangle the board keeps.
    //
    // A settlement snapshot keeps arriving while the page is up, and updated data can ask for a bigger table. A board
    // that is already up keeps the rectangle it was measured into - that is what stops a settled board from jumping -
    // and `Fit` replaces the grid it draws there. On the failure page `Draw` then lifts that board off the button every
    // frame (`TryHold`), so a taller grid is still held to the control the player presses. The success page has no such
    // per-frame wall: it places once and draws that placement, so a grid that grew after the fact would be drawn over
    // the button nobody re-checks. This is that missing check, and it is deliberately the same rule the first placement
    // uses: the grid's lower edge may not run past the strip the board was really measured into (`measured` - not this
    // frame's strip, which is exactly the one that refused it) and may not reach the button's clearance line whenever
    // this frame knows where that button is. A grid that does not clear either keeps the grid that is already up.
    public static bool HeldGridFits(BoardStrip measured, BoardStrip current, LayoutRect held, float height,
        float screenHeight, float slack = 0f)
    {
        if (!float.IsFinite(held.Y) || !float.IsFinite(height) || !(height > 0f)) return false;
        if (!float.IsFinite(screenHeight) || screenHeight <= 0f) return false;
        if (!float.IsFinite(slack) || slack < 0f) slack = 0f;
        float bottom = held.Y + height;
        float limit = screenHeight - 8f;
        // The strip the board was measured into, with the same tolerance the first placement allows: that strip is the
        // contract the board's place was accepted under.
        if (float.IsFinite(measured.Bottom) && measured.Bottom > 0f) limit = Math.Min(limit, measured.Bottom + slack);
        // ...and the control the player has to press, whenever this frame can see it.
        if (current.ButtonMeasured && float.IsFinite(current.ButtonTop))
            limit = Math.Min(limit, current.ButtonTop - ButtonClearance + slack);
        return bottom <= limit;
    }
}
