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

// ---------------------------------------------------------------------------------------------------
// Which side of the squad the settlement page is running on
// ---------------------------------------------------------------------------------------------------
//
// One explicit answer instead of a `!SNet.IsMaster` repeated at every branch. The roles are not symmetric by nature -
// a host's page carries the button that sends the squad home and a client's page carries only a waiting status - but
// every difference between them now names the role it is written for, and each role owns a documented way out of a
// page whose geometry it cannot use:
//
//   client   the page's geometry is fixed and known (`ResultsGuestPageGeometry`), so the strip runs to the foot of
//            the screen from a settled anchor and no interaction scan is consulted for release.
//   host     the page's own controls bound the strip, so the strip is measured geometry - and when that measured
//            strip cannot hold the board's own floor, the measured controls become a gap the table steps around
//            (`ResultBoardBands.TryBelowActions`) instead of the end of the page. Neither role is ever left with
//            nothing but silence, which is what a host page had while a 132 px strip refused a 270 px board.
public enum ResultPageRole { Host = 0, Client = 1 }

public static class ResultPageRolePolicy
{
    // The role this machine runs the page as. An unreadable native flag is a host: the host path is the one that
    // measures the page and refuses, the client path is the one that hands out a fixed safe area.
    public static ResultPageRole Of(bool isMaster) => isMaster ? ResultPageRole.Host : ResultPageRole.Client;
    public static ResultPageRole Unreadable => ResultPageRole.Host;
    public static bool IsClient(ResultPageRole role) => role == ResultPageRole.Client;
    public static string Name(ResultPageRole role) => role == ResultPageRole.Client ? "client" : "host";

    // The "this page has no footer at all" safe area belongs to the client alone: a host's page really does carry the
    // button that sends the squad home, and a host that cannot prove its own footer must not be handed a strip that
    // runs to the foot of the screen. A host's way out is the measured band below its actions, never this proof.
    public static bool UsesNoFooterSafeArea(ResultPageRole role) => role == ResultPageRole.Client;

    // A host may step around its measured controls when the strip they leave cannot hold the board's own floor. The
    // answer is about the two measured figures, never about the role alone - a page whose band is also too short is
    // still refused - and it is deliberately the host's branch: a client's strip is its fixed geometry and is proven
    // by that geometry, so a client page never needs a second rule.
    public static bool UsesBandBelowActions(ResultPageRole role, float stripHeight, float floorHeight, float bandHeight)
        => role == ResultPageRole.Host
           && float.IsFinite(stripHeight) && float.IsFinite(floorHeight) && float.IsFinite(bandHeight)
           && floorHeight > 0f && stripHeight + 0.5f < floorHeight && bandHeight + 0.5f >= floorHeight;
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

// The lower bound's provenance is retained in the placement so an explicitly proven client safe area cannot be
// mistaken for an unmeasured-button guess during later recovery or diagnostics.
//
// `BelowActions` is the bound that is *not* a lower bound at all: the board stands in the free band under every
// measured interaction because the strip above them is shorter than the board's own floor. It is recorded here so a
// held board, a re-measure and a log line can all tell it apart from a strip that is merely bounded by a button.
// `ScreenSafeArea` is the fail-open source: the board stands in the window's own safe area because the page never
// proved its own geometry (`FallbackLayout.cs`). It is a named source of its own so a log line, a held board and a
// regression test can tell a fall-open placement from one the page measured.
public enum ResultFooterSource { None = 0, NamedControl = 1, InteractiveBand = 2, ClientNoFooterSafeArea = 3, BelowActions = 4, ScreenSafeArea = 5 }

// The strip of a result page the board may use, together with the footer bound and its measured provenance.
public readonly struct BoardStrip
{
    public readonly float Top, Bottom;
    public readonly bool ButtonMeasured;
    public readonly float ButtonTop;
    public readonly float ButtonHeight;
    public readonly bool AnchorMeasured;
    public readonly ResultFooterSource FooterSource;

    public BoardStrip(float top, float bottom, bool buttonMeasured, float buttonTop, float buttonHeight = float.NaN,
        bool anchorMeasured = true, ResultFooterSource footerSource = ResultFooterSource.None)
    {
        Top = top; Bottom = bottom; ButtonMeasured = buttonMeasured; ButtonTop = buttonTop; ButtonHeight = buttonHeight;
        AnchorMeasured = anchorMeasured; FooterSource = footerSource;
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
    //
    // `topBound` is the fall-open board's other wall: its upper edge is the band's own top, and lifting must never carry it
    // above that - a board that cannot clear the footer without crossing its band is left on the band's edge and drawn
    // fainter by its caller, which is the trade the fail-open rule makes. It is NaN for a strict board, whose upper edge is
    // the page's own anchor and never moves.
    public static bool TryHold(BoardStrip strip, LayoutRect placed, float screenHeight, out LayoutRect rect,
        float topBound = float.NaN)
    {
        rect = placed;
        if (!(placed.Width > 0f) || !(placed.Height > 0f)) return false;
        if (!float.IsFinite(screenHeight) || screenHeight <= 0f) return false;
        if (!strip.ButtonMeasured || !float.IsFinite(strip.ButtonTop)) return true;
        float wall = Math.Min(strip.ButtonTop - ButtonClearance, screenHeight - 8f);
        if (!float.IsFinite(wall)) return true;
        if (placed.Bottom <= wall) return true;
        float lifted = Math.Max(0f, wall - placed.Height);
        if (float.IsFinite(topBound)) lifted = Math.Max(lifted, Math.Max(0f, topBound));
        rect = new LayoutRect(placed.X, lifted, placed.Width, placed.Height);
        return true;
    }

    // Whether a grid that arrived *after* the board was placed may be drawn in the rectangle the board keeps.
    //
    // Settlement snapshots can ask for a larger table. The board keeps the rectangle it was fitted into so ordinary
    // updates do not jump; a refreshed grid must still clear that strip and the current measured action wall.
    // Both the growth-fit path and ResultsDisplay.BoardStillSafe use this contract. A late/unreadable control is
    // handled by deferring the overlay before drawing, never by covering its old bounds.
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

// ---------------------------------------------------------------------------------------------------
// Which wall a result page's lower bound came from, said in the field names a log line may print
// ---------------------------------------------------------------------------------------------------
//
// One helper, because the same wall is described by four lines: the success page's own region line and the two band
// lines inside `SuccessRegion`, plus the per-repaint refusal in `BoardStillSafe`. When each of them spelled the field
// out itself they drifted, and the drift was a lie about the page: `lobbyButton=` names a control, so it may only be
// printed when that control was really read (`NamedControl` - as the player can press it, or from its own layout while
// the page still reveals it). A page whose bound came from the drawn band of everything it drew
// (`InteractiveBand`, the `LobbyEdgeSource.Band` case) has no measured button at all, and its band top is printed as
// `lobbyEdgeValue=` next to the source it came from instead. On the run 1.19.16 comes from the real button sat at 683
// and the band at 743, so calling 743 `lobbyButton` was a true field name over a false number - and the per-repaint
// refusal made the same claim from a line of its own.
//
// The mapping from a resolved reading to a `ResultFooterSource` lives here too, so the page's own `footerSource` and
// every line that describes the same wall cannot disagree about what was measured.
public static class ResultFooterProvenance
{
    // Which footer source a resolved wall came from: the control itself, the drawn band, or nothing measurable.
    public static ResultFooterSource Of(ResultsRegion.LobbyEdgeReading wall)
        => wall.FromButton ? ResultFooterSource.NamedControl
            : wall.Measured ? ResultFooterSource.InteractiveBand
            : ResultFooterSource.None;

    public static string LobbyWall(ResultsRegion.LobbyEdgeReading wall) => LobbyWall(Of(wall), wall.Top, wall.Measured);

    // The button's own upper edge when there is a button to name, and otherwise the wall the strip was really built
    // from together with its source - never that wall under the button's name.
    public static string LobbyWall(ResultFooterSource source, float top, bool measured)
    {
        if (source == ResultFooterSource.NamedControl) return "lobbyButton=" + top.ToString("0");
        return "lobbyEdgeValue=" + (measured && float.IsFinite(top) ? top.ToString("0") : "unmeasurable")
            + ",source=" + WallSource(source);
    }

    // The source word beside `lobbyEdgeValue=`: a wall is either the drawn band of the page's own controls or nothing
    // at all. The two remaining sources describe a *strip*, not a wall, and are named as such if one ever reaches here.
    private static string WallSource(ResultFooterSource source)
    {
        switch (source)
        {
            case ResultFooterSource.InteractiveBand: return "band";
            case ResultFooterSource.BelowActions: return "below-actions-band";
            case ResultFooterSource.ClientNoFooterSafeArea: return "client-fixed-page-geometry";
            case ResultFooterSource.ScreenSafeArea: return "fail-open-screen-safe-area";
            default: return "none";
        }
    }
}
