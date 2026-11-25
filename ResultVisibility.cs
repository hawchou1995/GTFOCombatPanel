using System;
using System.Collections.Generic;

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

// The one bounded line per page and per reason, and the one-line-per-F8-press exception (1.19.22, corrected in
// 1.19.25).
//
// The ordinary answer is once per page per reason, which is what keeps per-frame repaints from turning a diagnostic
// into a flood. The forced exception is bounded by the press itself, not by the frame: while a forced press still
// owes its answer (`_forcedOutcomePending` is set) the very same reason prints once more - the press must end its
// frame with either a "board drawn" line or a refusal line naming the real missing fact, even when the per-page
// latch has already fired for that reason. The refusal that prints spends the flag, so the *next* forced repaint
// falls back on the ordinary latch and stays silent until the next F8 press re-arms the flag: one line per press,
// never one line per frame (a held F8 no longer floods the log). The latch is not deleted; the frame that owes an
// answer simply prints again for it.
public static class ResultReportPolicy
{
    // `forcedPressPending` is the press's own "not answered yet" flag (ResultsDisplay._forcedOutcomePending).
    // It widens the gate for exactly one refusal - the one that spends it - and never for a later repaint.
    public static bool OncePerPage(HashSet<string> reports, string key, bool forced, bool forcedPressPending)
    {
        if (reports != null && reports.Contains(key) && !(forced && forcedPressPending)) return false;
        if (reports != null) reports.Add(key);
        return true;
    }
}

// 1.19.49: the press answers only for the lines it owes.
//
// `OncePerPage`'s forced exception exists for the one line a press is entitled to: the frame that owes an answer ends with
// either "board drawn" or a refusal naming the real missing fact. That exception is handed to every call site as a pair of
// arguments, so a diagnostic that merely describes the page - the vanilla block that carries a control, the block that is
// the page root - was re-printed on every press as well. The recorded 2026-11-23 settlement page is what that costs: `the
// vanilla block 'artifact summary' holds a control the player can press...` (268 B) was written fourteen times, once for each
// F8 press-and-release of that page, for 3,766 B of a line that had not changed. Nothing about the F8 answer itself is touched
// here, and no other call site changes: this is the strict answer, used where a line describes the page rather than answering
// a press.
public static class ResultReportPolicyStrict
{
    // Once per page per reason, with no exception at all: a press never re-arms it, and a repaint never repeats it.
    public static bool OncePerPage(HashSet<string> reports, string key)
    {
        if (reports != null && reports.Contains(key)) return false;
        if (reports != null) reports.Add(key);
        return true;
    }
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
    // The share of this strip the board may use, decided where the strip is measured: `BandFillCeiling` for a client
    // band that ends above a wall the page really measured (its waiting-status block), `StripFillCeiling` for every
    // other strip, including one whose foot is only the page's own edge. The fit spends it, so a band that may be
    // filled to its foot is not answered with a table two thirds of it and a blank row under the board.
    public readonly float FillCeiling;

    public BoardStrip(float top, float bottom, bool buttonMeasured, float buttonTop, float buttonHeight = float.NaN,
        bool anchorMeasured = true, ResultFooterSource footerSource = ResultFooterSource.None,
        float fillCeiling = ResultsFontGrowth.StripFillCeiling)
    {
        Top = top; Bottom = bottom; ButtonMeasured = buttonMeasured; ButtonTop = buttonTop; ButtonHeight = buttonHeight;
        AnchorMeasured = anchorMeasured; FooterSource = footerSource;
        FillCeiling = float.IsFinite(fillCeiling) && fillCeiling > 0f
            ? (fillCeiling > ResultsFontGrowth.BandFillCeiling ? ResultsFontGrowth.BandFillCeiling : fillCeiling)
            : ResultsFontGrowth.StripFillCeiling;
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
    // does - does not take it away. The button is still a wall: a board that really would reach it is lifted
    // onto the clearance line, never moved down.
    //
    // Both edges bind. `topBound` is the board's own band top - `_holdStrip.Top`, now passed for the strict
    // board too, so a late button reading can no longer lift a strict board to the top of the window over the
    // failure title. A board whose own height cannot sit between the band's top and the button's clearance
    // line cannot be held inside its band by lifting it, so `TryHold` refuses (false) and the caller reports
    // the real numbers instead of drawing the board outside its band.
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
        // The same half-pixel tolerance the placement and the fade checks use: a board that would end within
        // half a pixel of the wall is still held on the line.
        if (lifted + placed.Height > wall + 0.5f) return false;
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

    // 1.19.60 (independent review F1): may this lower wall stand in for a *final* edge? Only a control the page really
    // measured may: the named control the player can press, or the drawn band of the page's own interactions. `None` is a
    // strip that ran to the foot of the window, the client's fixed page geometry is a statement about the screen rather
    // than about what the page drew, and a fail-open screen safe area is the fallback's own invention - none of the three
    // is evidence that this page's lower edge has stopped moving, so none of them may be called final.
    public static bool MeasuredLowerWall(ResultFooterSource source)
        => source == ResultFooterSource.NamedControl || source == ResultFooterSource.InteractiveBand;

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

// ---------------------------------------------------------------------------------------------------
// 1.19.55: the board may enter before the page has proven its final boundary
// ---------------------------------------------------------------------------------------------------
//
// The recorded 2026-11-23 session never showed the success board on its own: every page needed F8 or a second look, because
// the only way past the reveal gate was a *proven* boundary and a host success page whose return button the page never
// made visible can never prove one. The player's own contract is narrower than that gate: as soon as the page is live and
// this machine holds real settlement data, a readable table may stand - drawn from the page's own measured completion edge,
// *non-interactive* (no wheel, no press) and without touching the game's own blocks - and it may be replaced by the mature
// board the moment the page's own boundary is proven. Nothing here claims the page settled: `Ready`/`Proven` stay the
// strict path's own words, and no timeout, frame count or empty scan is turned into one.
//
//   room     the strip the page itself measured this frame, from its completion section down to its measured foot. It is
//            the same `ResultsSafeArea` the strict path uses, so the early board can never stand outside it.
//   floor    the height a table of this row count really draws at (`ResultsFontGrowth.DrawableBoardHeight`). A strip that
//            cannot hold it is not a place for any board, early or mature.
//   overlap  an active control the scan can see reaching the placed plate. That refusal stays absolute: the early board
//            never covers something the player can press.
public static class ResultEarlyBoardPolicy
{
    // The one answer this policy gives. `localData` is this machine's own retained roster (the board has something true to
    // print), `completionMeasured` is the page's own upper edge read this frame, `scanComplete` is the whole interaction
    // scan having seen the page, and `overlapsControl` is the refusal that no mode may pass.
    //
    // 1.19.60 (the recorded 2026-11-24 client success page): `hostProven` and `boundsFinal` are the pair that answers
    // the question this policy used to leave out - *is the strip the early board is fitted into the one the mature board
    // will be fitted into?* On a client page it was not. The page's completion section is still revealing itself when
    // the early board enters, so the strip was measured from a moving edge: the recording placed the table into a
    // 626 px strip at 12 px and, once the reveal had drawn the captions its layout does not include, re-fitted it into
    // a 534 px strip at 10 px, 123 px lower - the "jump and the type got smaller" the player reported. The two edges
    // of that page only stop moving when the page's own reveal has answered, so a page without the host's privilege
    // now requires `boundsFinal`. Nothing here counts frames, spends a budget or caps the glyph: the strip is the
    // page's final one, so the first board and the mature board are the same table at the same size, and the only thing
    // the mature placement can still do is grow it.
    //
    // `hostProven` is a *positive* reading of the host flag, not `!client`. 1.19.60 (independent review F1): the role
    // this page runs as is answered by `ResultPageRolePolicy.Unreadable` when the native flag throws, and that value is
    // deliberately the host's *measuring* path - a page whose role nobody could read must measure and refuse rather than
    // hand out a client's fixed safe area. It must not inherit the host's early-display privilege as well: an unreadable
    // role gets the stricter answer below, exactly as a client does, and keeps its board until its own bounds are final.
    // A proven host keeps the 1.19.55 behaviour unchanged (its strip already hangs from the button it can read), and a
    // page that never proves its own edges still gets its board the moment the ledger answers - the route is the same,
    // only the measurement it is handed is a final one.
    public static bool Allows(bool localData, bool completionMeasured, bool scanComplete, bool overlapsControl,
        float room, float floor, bool hostProven, bool boundsFinal)
    {
        if (!localData || !completionMeasured || !scanComplete || overlapsControl) return false;
        if (!float.IsFinite(room) || !float.IsFinite(floor) || !(floor > 0f)) return false;
        if (room + 0.5f < floor) return false;
        return hostProven || boundsFinal;
    }

    // An early board is a *picture* of the settlement, not a control surface: a press belongs to whatever the game drew
    // under the pointer. The wheel is a different question and it is asked separately (`_scrollValid`): a picture whose
    // table overflows is still a table the player has to be able to read to its last row.
    public static bool Interactive(bool early) => !early;
    // The game's own blocks make way for a board that is really on the page - early or mature.
    //
    // 1.19.59: this used to answer `!early && placed`, i.e. an early board put *nothing* away, and the recorded success
    // page never matured (`nativeFooterVisible=false` for the whole page), so the red artifact block and the four
    // prisoner cards stayed on the screen under a table that was already drawn - the defect the player reported as
    // "1.19.40 的视觉替换没了". The two questions are now separated instead of being traded against each other:
    //   * `HidesVanilla` says the page's own blocks make way, for any board that is painted;
    //   * the *kind* of making way is the caller's (`ApplyVanillaBlocks(..., visualOnly: early)`), and an early board
    //     may only switch the block's drawing off - never move it and never deactivate it - so every control the page
    //     hung inside it keeps answering, and the page's own wheel and presses stay the page's.
    public static bool HidesVanilla(bool early, bool placed) => placed;

    // 1.19.57 (review of 1.19.56 D): a standing early board is *kept* while the page has not handed over what the mature
    // board still needs. `Refresh` used to answer every re-measure of the same page with `ResetPlacement()` and
    // `RestoreHidden()`, which threw the grid that was on the screen away before the page was read again - so a page that
    // never proved its own footer lost and regained the table on the retry cadence, and `_scrollVisible`/`_scrollPlate`
    // described a board that was no longer on the page for that frame. Keeping it is not a claim about the page: the
    // measurement still runs every retry, and the moment it really places the mature board the same call overwrites the
    // early one exactly once (`_earlyBoard` is written by that same measurement).
    //
    // `samePage` is the pointer the standing board was placed for against the page this repaint really holds, so a new
    // settlement on the same reused page object is still a new screen and starts from nothing.
    public static bool KeepsStanding(bool early, bool placed, bool samePage, bool flavourMatches, bool pageLive,
        bool gridReady, bool contentReady)
        => early && placed && samePage && flavourMatches && pageLive && gridReady && contentReady;
}

// ---------------------------------------------------------------------------------------------------
// 1.19.55: which part of a vanilla block may be put away
// ---------------------------------------------------------------------------------------------------
//
// `CM_Item` is not by itself a control the player can press: the result pages hang unregistered `CM_Item` components inside
// their presentational blocks (an artifact summary entry, a decorative row), and treating every one of them as an action is
// what left `artifact summary` sitting on the page although the player had asked for it to be hidden (recorded lines
// 7442/13753/20050/24656). The question this policy answers is the one the hide path really means: does this subtree hold an
// action *the page can dispatch to* - `registered` in `CM_PageBase.m_allItems` - or one that can take the pointer this moment
// (`activeInHierarchy`)? Everything else is a decoration belonging to the block, and a block whose only `CM_Item`s are
// decorations may be put away exactly as it always was.
//
// A mixed subtree is not one question but two: the branches that carry a live action stay, and the branches that carry none
// are put away, so the player's own setting is honoured without taking a button off the page. `Nothing` is the honest
// answer for a block whose whole visual body carries an action: it is left exactly where the game put it.
public enum VanillaHidePlan
{
    Nothing = 0,        // the block itself is still on the page, unchanged
    WholeBlock = 1,     // no live action anywhere in it: the block is put away as before (moved off, switched off)
    SafeBranches = 2,   // a mixed tree: only the branches with no live action are put away
    // 1.19.59: the parts this build may not move - a branch that really carries a `Button`, a subtree it cannot read, a
    // block the page hangs one of its own controls inside - but whose drawing must still make way, because the player
    // asked for this block to be replaced and the board is already painted over it. Nothing is moved, nothing is
    // deactivated and no press is taken away: the block's CanvasGroup is switched to zero alpha, which is the one
    // suppression that leaves every GameObject, every collider and every event handler exactly where the page put them.
    VisualOnly = 3
}

public static class ResultVanillaHidePolicy
{
    // One `CM_Item` seen inside the block.
    public static bool IsLiveAction(bool activeInHierarchy, bool registered) => activeInHierarchy || registered;

    // The plan for one block, from the counts the hide path measured: how many `CM_Item`s live in it, how many of those are
    // live actions, and how many of its own child branches really contain a live action.
    public static VanillaHidePlan Plan(int controls, int liveActions, int branchesWithAction)
    {
        if (controls <= 0) return VanillaHidePlan.WholeBlock;
        if (liveActions <= 0) return VanillaHidePlan.WholeBlock;
        return branchesWithAction > 0 ? VanillaHidePlan.SafeBranches : VanillaHidePlan.Nothing;
    }
}

// ---------------------------------------------------------------------------------------------------
// 1.19.56 (A): one classifier for both halves of the same question
// ---------------------------------------------------------------------------------------------------
//
// The scan asks "is this a wall the board must never cover" and the hide asks "may this be put away". 1.19.55 answered
// those two questions with two different pieces of code, and that is how a decoration could be *too dangerous to hide*
// (so the player's switch was ignored and the red-crystal prompt stayed) while still being counted as a wall (so the
// board was never drawn, or was driven into a false band). Both now ask the same three-way answer, from facts the page
// itself provides:
//
//   Action   a `UnityEngine.UI.Button` anywhere in the subtree, or a `CM_Item` the page actually dispatches to
//            (`CM_PageBase.m_allItems`). Never hidden, always a wall.
//   Visual   a Renderer/CanvasRenderer - the block's own text, plate or icon - with no Button below it. Inside one of the
//            page's *known* decoration blocks it is a decoration: hideable, and not a wall. Outside them a `CM_Item` that
//            is switched on is still treated as a wall (the 1.19.54 fail-safe): this release does not decide by itself
//            that a control the page registered elsewhere is decoration.
//   Unknown  an entry this build could not describe. It is a wall and it is never hidden - the fail-safe answer both
//            paths already gave.
//
// "Active" alone is deliberately not an action: the recorded page hung *active* decorative `CM_Item`s in its artifact
// block, and calling every active one a button is exactly what made the block impossible to hide. Registration is the
// page's own statement; a Button is the player's own control. Nothing here looks at text, at a coordinate or at a
// language.
public readonly struct VanillaNodeFacts
{
    public readonly bool InsideDecorationBlock;
    public readonly bool ContainerRoot;
    public readonly bool Registered;
    public readonly bool Active;
    public readonly bool HasButton;
    public readonly bool VisualOnly;

    public VanillaNodeFacts(bool insideDecorationBlock, bool containerRoot, bool registered, bool active,
        bool hasButton, bool visualOnly)
    {
        InsideDecorationBlock = insideDecorationBlock; ContainerRoot = containerRoot;
        Registered = registered; Active = active; HasButton = hasButton; VisualOnly = visualOnly;
    }
}

public enum VanillaNodeKind
{
    Action = 0,   // a control the player can press: protected, always a wall
    Visual = 1,   // the page's own drawing: hideable, never a wall when it lives in a decoration block
    Unknown = 2   // this build cannot describe it: a wall, and never hidden
}

public static class ResultVanillaClassify
{
    // 1.19.59: `Registered` no longer outranks the block a node lives in. The recorded pages hang *registered*
    // `CM_Item`s inside their own presentational blocks - the artifact summary's `i` and its `1.0` line are the ones the
    // player named - and `CM_PageBase.m_allItems` is a list of everything the page can draw a hover for, not a list of
    // playable buttons: the page's real controls (the lobby/restart buttons) are its own fields (`m_btnGoToLobby`,
    // `m_btnRestartCheckpoint`, `m_btnLeaveExpedition`) and they live *outside* these blocks. Treating the internal
    // registration as "a control the player can press" is what left the red-crystal block on the page (recorded
    // `artifact summary holds a control ... only the 2 branches were moved`) and, worse, what made those decorations walls
    // in the interaction scan - so the board was compressed around them and the hover scan deferred the board for them.
    //
    // The order is therefore: a `Button` anywhere in the subtree is a control (always, inside or out); the block's *own*
    // node, when the page really registered it as an item, is a control too (the page dispatches to the block itself, so
    // putting it away takes that dispatch with it); outside one of the page's own named decoration blocks nothing changed
    // from 1.19.54/1.19.57 (a registered or switched-on `CM_Item` is a wall); and *inside* one of them the block's own
    // drawing - its renderer, and a registered entry nested under it - is the block's picture.
    //
    // 1.19.59 second pass: "nested" is the operative word. Not every registered `CM_Item` is decoration - the block's own
    // root and every container root keep the old answer - so this never turns a page control into wallpaper. What it does
    // turn into wallpaper is the recorded case: a registered entry *under* a declared presentational block, which the page
    // keeps a hover entry for and nothing else. Anything this build cannot describe is still `Unknown` (a wall, never
    // hidden), and a `Button` is never decoration at any depth.
    public static VanillaNodeKind Of(VanillaNodeFacts facts)
    {
        // A `UnityEngine.UI.Button` anywhere in the subtree is the player's own control: never hidden, always a wall.
        if (facts.HasButton) return VanillaNodeKind.Action;
        // The block's own node (or any container root): registered means the page dispatches to it as an item, and a
        // registered container is a control whatever it looks like. Unregistered, it is the plate the block is drawn on.
        if (facts.ContainerRoot)
            return facts.Registered ? VanillaNodeKind.Action : VanillaNodeKind.Visual;
        if (!facts.InsideDecorationBlock)
        {
            // Outside a known decoration block nothing changed from 1.19.54: the page dispatches to it, or it is switched
            // on, and it is a wall; an entry nobody could read stays one too.
            if (facts.Registered) return VanillaNodeKind.Action;
            return facts.Active ? VanillaNodeKind.Action : VanillaNodeKind.Unknown;
        }
        // Nested inside one of the page's known presentational blocks: a node that really renders something, and a `CM_Item`
        // the page merely keeps a hover entry for, are the block's own drawing. A bare transform or an entry whose facts
        // could not be read stays `Unknown` - never hidden, still a wall - so an unreadable node can only ever leave the
        // block where the game put it.
        if (facts.VisualOnly || facts.Registered) return VanillaNodeKind.Visual;
        return VanillaNodeKind.Unknown;
    }

    public static bool IsWall(VanillaNodeKind kind) => kind != VanillaNodeKind.Visual;

    public static bool Hideable(VanillaNodeKind kind) => kind == VanillaNodeKind.Visual;

    // The plan for one whole block, from the kinds its own tree answered - the production decision, shared with the scan.
    //
    // `rootKind` is the *block's own* answer (its own `Button`, its own registration, its own renderer), never the
    // subtree answer: a mixed block whose root is a plate with a control in one of its branches is exactly the case
    // this policy exists for, and reading the subtree's `Button` into the root is what turned every mixed block into
    // "leave the whole thing alone".
    // A root this build cannot describe is left where the game put it. It is never *WholeBlock*: putting a whole block
    // away on the strength of an answer nobody could read is the one direction this policy must not take, and 1.19.56
    // took it whenever such a block happened to hold no `CM_Item` the scan could read.
    //
    // 1.19.59: the last answer is no longer `Nothing`. A block the player asked to replace, whose body carries a control in
    // one of its branches, used to stay on the page *whole* - so the red crystal and the prisoner cards were still drawn
    // under a board that was already painted over them (`artifact summary holds a control the player can press, so it is
    // left exactly where the page put it`). The page's own drawing now makes way in the one way that cannot take a control
    // away: `VisualOnly` switches the block's alpha and renderers off and leaves every GameObject, collider and handler
    // exactly where the game put it, and `SafeBranches` still moves the branches that carry no action at all.
    //
    // `hasButton` is the second pass of the same release, and it is a *safety* gate rather than a convenience: a block whose
    // body really contains a `UnityEngine.UI.Button` must never be answered with `VisualOnly`, because zeroing a parent's
    // alpha disables that button's drawing while leaving it pressable - an invisible control, which is worse than a visible
    // decoration. Such a block gives up its safe branches and otherwise stays exactly where the game put it.
    public static VanillaHidePlan PlanForBlock(VanillaNodeKind rootKind, int actionNodes, int visualNodes, int branchesWithAction,
        bool hasButton = false)
    {
        if (rootKind == VanillaNodeKind.Unknown) return VanillaHidePlan.Nothing;
        if (rootKind == VanillaNodeKind.Action) return VanillaHidePlan.Nothing;
        // No action anywhere in the tree: the whole block is the page's own drawing and is put away as it always was.
        if (actionNodes <= 0) return VanillaHidePlan.WholeBlock;
        // A mixed block gives up the parts that are *proven* to be the page's own drawing, branch by branch.
        if (branchesWithAction > 0 && visualNodes > 0) return VanillaHidePlan.SafeBranches;
        // Nothing can be moved. A block that really carries a `Button` is left visible (an invisible-but-pressable control
        // is not an improvement); one whose only "actions" are the page's own hover registrations gives up its drawing.
        return hasButton ? VanillaHidePlan.Nothing : VanillaHidePlan.VisualOnly;
    }
}

// ---------------------------------------------------------------------------------------------------
// 1.19.57: which blocks of the page are decoration *for this player's configuration*
// ---------------------------------------------------------------------------------------------------
//
// The classifier in 1.19.56 asked the static `_page`/`_succeeded` cache which blocks existed, which is the wrong
// question twice over: that cache is cleared by `ResetPlacement` on the frames the scan and the hide run through it, so
// a live page could answer "no decoration block at all"; and it ignored the player's switches, so a block the player
// had explicitly asked to keep (`ShowSuccessArtifacts`, `ShowSuccessPlayerReports`, `HideVanillaFailBlocks` off) was
// still treated as decoration - its `CM_Item`s were exempted from the scan as "drawing" while nothing was going to
// hide them. The answer below is about one block of one page and the three switches, so the hide and the scan can be
// asked with the page the caller really holds, at whatever moment the caller asks.
public enum VanillaBlockKind
{
    ArtifactInventory = 0,  // `m_ArtifactInventoryDisplay`
    ArtifactSummary = 1,    // `m_artifactInfo_text`
    CheckpointUsage = 2,    // `m_checkpointUsage` (success page only)
    PlayerReport = 3,       // `m_playerReports[]`
    PlayerReportAnchor = 4  // `m_playerReportAligns[]`
}

public static class ResultVanillaBlockPolicy
{
    // Whether this mod is really about to put this block away on this page. Only such a block is a decoration block:
    // the one the player asked to hide. A block the player asked to keep is left outside the classifier, keeps the
    // 1.19.54 fail-safe (its active `CM_Item`s are walls), and the board steps around it - the player gets what the
    // switch promised, and nothing the player wants to read is ever reported as "decoration to be ignored".
    public static bool IsDecoration(bool succeeded, bool hideArtifacts, bool hideReports, bool hideFailBlocks,
        VanillaBlockKind block)
    {
        if (succeeded)
        {
            switch (block)
            {
                case VanillaBlockKind.ArtifactInventory:
                case VanillaBlockKind.ArtifactSummary:
                case VanillaBlockKind.CheckpointUsage:
                    return hideArtifacts;
                case VanillaBlockKind.PlayerReport:
                case VanillaBlockKind.PlayerReportAnchor:
                    return hideReports;
                default:
                    return false;
            }
        }
        // The failure page has no report cards and no checkpoint line: its artifact block is the whole question.
        switch (block)
        {
            case VanillaBlockKind.ArtifactInventory:
            case VanillaBlockKind.ArtifactSummary:
                return hideFailBlocks;
            default:
                return false;
        }
    }
}

// ---------------------------------------------------------------------------------------------------
// 1.19.57: the one identity rule the hide path and the scan ask
// ---------------------------------------------------------------------------------------------------
//
// Two objects are the same node when they are anchored on the same native object. A native field can name a component
// (`m_artifactInfo_text` is a text, `m_ArtifactInventoryDisplay` a display) while an ancestor walk arrives with that
// object's `Transform`, and those are different native objects: the component pointer, the transform pointer and the
// GameObject pointer of one node are three distinct values, so comparing a component's pointer with a transform's can
// never answer true - which is exactly what 1.19.56 did, and why no node was ever "inside" a decoration block.
//
// The comparison therefore uses only the two anchors every wrapper of one node shares: the `Transform` and the
// `GameObject`. It is a pure function so a test can hand it the distinct ids a real page's three wrappers have.
public static class ResultNodeIdentity
{
    public static bool Same(IntPtr blockTransform, IntPtr blockObject, IntPtr nodeTransform, IntPtr nodeObject)
    {
        if (nodeTransform == IntPtr.Zero) return false;
        if (blockTransform != IntPtr.Zero && blockTransform == nodeTransform) return true;
        return blockObject != IntPtr.Zero && nodeObject != IntPtr.Zero && blockObject == nodeObject;
    }
}
