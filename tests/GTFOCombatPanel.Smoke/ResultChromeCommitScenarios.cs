using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// The settlement controls' timing: one place per page, committed once.
//
// Everything below drives the production policy - `ResultChromeLayout.Step`, `ResultChromeCommit`,
// `ResultChromeSettle`, `ResultChromeSlot`, `ResultChromeInputGate` - through a scripted sequence of repaints,
// filled exactly the way `ResultsDisplay.DrawChrome` fills its probe. The scenarios are the ones the recorded
// Survival client page produced: an anchor that is missing at first, then measurable while the page is still
// animating, then drifting; a native control arriving on a place that has already been committed; a page
// change, a window change, an F8 hide and a show; and the windows the player runs (720p, 1080p, 1440p,
// ultrawide) plus a narrow one.
//
// `ThePreviousPerRepaintRuleFailsTheseTests` runs the behaviour this release replaces through the same
// primitives and asserts that it *disagrees* with the assertions above - so a regression back to
// "re-derive the rectangle from this frame's anchor" fails here instead of quietly passing.
internal static class ResultChromeCommitScenarios
{
    internal static void RunInto(Action<bool, string> check)
    {
        OnePlaceForTheWholePage(check);
        TheToggleWaitsForTheAnchorToStop(check);
        ALateNativeControlMovesThePlaceOnceThenWithholdsIt(check);
        ANewPageAndANewWindowAreTheOnlyNewLayouts(check);
        HidingTheBoardKeepsTheOnePlace(check);
        ANoAnchorPageStillAnswersWhenTheControlArrives(check);
        TheHeaderBandStaysAboveTheTableAtEveryWindow(check);
        TheOneControlIsReadableAndClear(check);
        APausedControlKeepsNoHotBox(check);
        ThePreviousPerRepaintRuleFailsTheseTests(check);
        TheRuntimeIsWiredToTheCommittedPlace(check);
    }

    // ---- the scripted page -------------------------------------------------------------------------------
    //
    // One settlement page, repainted on demand. Every field is what this repaint's measurement says: the anchor
    // the page drew (or didn't), what it measured, its arrival, the page's own controls, and the board's plate.
    private sealed class Page
    {
        private readonly ResultChromeCommit _commit = new ResultChromeCommit();
        private readonly ResultChromeSettle _settle = new ResultChromeSettle();
        private readonly ResultChromeProbe _probe = new ResultChromeProbe();
        private readonly List<LayoutRect> _native = new List<LayoutRect>();
        private IntPtr _pointer = new IntPtr(41);

        internal int Width = 1920, Height = 1080;
        internal bool Succeeded;
        internal bool BoardUp = true;
        internal bool AnchorDrawn = true;
        internal LayoutRect Anchor = new LayoutRect(860f, 990f, 200f, 32f);
        internal ResultChromeAnchor Source = ResultChromeAnchor.ClientWaitingStatus;
        internal ResultChromeReveal Reveal = ResultChromeReveal.Arrived;
        internal string AnchorWhy = "return-to-lobby-text";
        internal bool PlateDrawn = true;
        // A board that hangs from the upper third of the page with its foot well above the page's own controls -
        // the shape these pages really draw, and the reason the toggle stands beside the foot control rather
        // than under the plate.
        internal LayoutRect Plate = new LayoutRect(575f, 390f, 770f, 500f);

        internal ResultChromeCommit Commit => _commit;
        internal ResultChromeSettle Settle => _settle;
        internal List<LayoutRect> Native => _native;
        internal int Repaints { get; private set; }
        // The engine frame each repaint belongs to. `Next` is one frame; `NextSameFrame` is a second `OnGUI` pass inside
        // the same frame - which is exactly what the native-conflict rule has to be able to tell apart (1.19.48).
        internal int Frame { get; private set; }

        internal void NewPage(int pointer) { _pointer = new IntPtr(pointer); }

        // One repaint, in its own frame. Returns what may be drawn and hit that frame.
        internal ResultChromeDecision Next()
        {
            Frame++;
            return Repaint();
        }

        // The engine ran `OnGUI` again inside the same frame (Layout, Repaint, MouseDown...): same frame number, so the
        // conflict rule counts it once.
        internal ResultChromeDecision NextSameFrame() => Repaint();

        // Frames the chrome was not drawn on at all: the next repaint is not adjacent to the previous observation, which is
        // what the conflict rule has to be able to tell apart (1.19.48).
        internal void SkipFrames(int count) { if (count > 0) Frame += count; }

        private ResultChromeDecision Repaint()
        {
            Repaints++;
            _probe.Reset();
            _probe.Frame = Frame;
            _probe.Page = _pointer;
            _probe.Succeeded = Succeeded;
            _probe.ScreenWidth = Width;
            _probe.ScreenHeight = Height;
            _probe.BoardUp = BoardUp;
            _probe.AnchorDrawn = AnchorDrawn;
            _probe.Anchor = Anchor;
            _probe.Source = Source;
            _probe.Reveal = Reveal;
            _probe.AnchorWhy = AnchorWhy;
            _probe.Native = _native;
            _probe.PlateDrawn = PlateDrawn;
            _probe.Plate = Plate;
            return ResultChromeLayout.Step(_commit, _settle, ResultChromeMetrics.For(Width, Height), _probe);
        }

        // The repaints a page needs before it can place anything: one to observe the anchor and one to place
        // against it. The second is the frame the place is committed on. Nothing here is a frame budget - the
        // second repaint only places because the anchor it measured is the one the first repaint measured.
        internal ResultChromeDecision Establish()
        {
            Next();
            return Next();
        }
    }

    private static bool same(LayoutRect first, LayoutRect second)
        => Math.Abs(first.X - second.X) < 0.001f && Math.Abs(first.Y - second.Y) < 0.001f
           && Math.Abs(first.Width - second.Width) < 0.001f && Math.Abs(first.Height - second.Height) < 0.001f;

    // ---- one place, for the whole page -------------------------------------------------------------------
    private static void OnePlaceForTheWholePage(Action<bool, string> check)
    {
        var page = new Page();
        ResultChromeDecision first = page.Next();
        check(!first.ToggleDrawn && first.Pending,
            "the toggle is not drawn by the first repaint that sees the page: one measurement is not a settled anchor, and no provisional rectangle is painted");
        ResultChromeDecision settled = page.Next();
        check(settled.ToggleDrawn && settled.ToggleCommitted,
            "the toggle is drawn - and its place committed - on the first repaint whose anchor has stopped moving");
        LayoutRect place = settled.Toggle;
        check(place.Width > 0f && place.Bottom == page.Anchor.Bottom,
            "the committed place stands on the anchor's own floor rather than centred on it");

        // The page keeps animating for the whole life of the screen: the waiting-status line slides up and left,
        // and the pointer moves over it. None of that is a new page, so none of it may move a control.
        bool everyRepaintKeptIt = true;
        for (int frame = 0; frame < 24; frame++)
        {
            page.Anchor = new LayoutRect(860f - frame * 3f, 990f - frame * 12f, 200f, 32f);
            ResultChromeDecision repaint = page.Next();
            if (!repaint.ToggleDrawn || !same(repaint.Toggle, place)) everyRepaintKeptIt = false;
        }
        check(everyRepaintKeptIt,
            "24 repaints of a page whose anchor moved 72 px: the toggle is drawn at the one place it was committed to, every frame");
        check(page.Commit.Toggle.Placements == 1,
            "the place was placed once for the whole page, not once per repaint (placements=1)");
        check(page.Commit.Contexts == 1,
            "and every one of those repaints was the same commitment context (contexts=1)");

        // A board whose content grew is the same case: a different plate, a moved anchor, and neither may drag
        // the toggle with it.
        page.Plate = new LayoutRect(575f, 390f, 900f, 700f);
        ResultChromeDecision grew = page.Next();
        check(grew.ToggleDrawn && same(grew.Toggle, place),
            "a board that grew and an anchor that moved leave the committed toggle exactly where it is");
    }

    // ---- the page has not settled -------------------------------------------------------------------------
    private static void TheToggleWaitsForTheAnchorToStop(Action<bool, string> check)
    {
        // The board hangs from the top of the page and its foot is well above the waiting line: this is the
        // geometry the client failure page really has (the strip runs from the title down to the waiting text).
        var page = new Page
        {
            AnchorDrawn = false,
            AnchorWhy = "no-waiting-status",
            Plate = new LayoutRect(575f, 180f, 770f, 600f)
        };
        ResultChromeDecision missing = page.Next();
        check(!missing.ToggleDrawn && missing.Pending
              && missing.ToggleWhy != null && missing.ToggleWhy.Contains("no-waiting-status"),
            "a client page that draws no waiting-status line hands the toggle no place at all, and the reason is the page's own word for it (" + missing.ToggleWhy + ")");

        // The line appears mid-animation and is measured three frames running - but it is moving.
        page.AnchorDrawn = true;
        bool drawnWhileMoving = false;
        for (int frame = 0; frame < 3; frame++)
        {
            page.Anchor = new LayoutRect(860f - frame * 20f, 990f - frame * 16f, 200f, 32f);
            if (page.Next().ToggleDrawn) drawnWhileMoving = true;
        }
        check(!drawnWhileMoving,
            "a waiting-status line the page is still sliding in hands over no place: the toggle waits for the anchor to stop rather than following it");

        // It stops. The first place it can be drawn at is the final one.
        page.Anchor = new LayoutRect(800f, 942f, 200f, 32f);
        ResultChromeDecision still = page.Next();
        check(!still.ToggleDrawn,
            "the settled anchor is observed once more before it is trusted - the same rectangle, not a frame count");
        ResultChromeDecision placed = page.Next();
        check(placed.ToggleDrawn && placed.Toggle.Bottom == 942f + 32f,
            "the first place the toggle is drawn at is the settled one, so the entry the player sees is the position it keeps");
        check(page.Settle.Restarts >= 3,
            "the observation restarted for every repaint the anchor was somewhere else (restarts=" + page.Settle.Restarts + "), which is a statement about the geometry and not about a timer");

        // The text arrives at full opacity as part of the same animation: a line still fading in is not settled.
        var fading = new Page { Reveal = ResultChromeReveal.Waiting };
        check(!fading.Establish().ToggleDrawn,
            "a waiting-status line that has not arrived at full opacity is not a line to stand beside, however still it is");
        fading.Reveal = ResultChromeReveal.Arrived;
        check(fading.Establish().ToggleDrawn,
            "and it is placed the moment it has arrived");
    }

    // ---- a native control arrives on a committed place -----------------------------------------------------
    private static void ALateNativeControlMovesThePlaceOnceThenWithholdsIt(Action<bool, string> check)
    {
        var page = new Page();
        ResultChromeDecision settled = page.Establish();
        LayoutRect place = settled.Toggle;
        page.Native.Add(new LayoutRect(place.X + 4f, place.Y + 2f, place.Width, place.Height));

        // 1.19.48: one repaint of overlap is a flicker, not a conflict. The recorded 2K pages answered a single frame of
        // overlap at a time and every one of those withdrew the button from under the pointer aiming at it (log 5134-5141),
        // and the 1K pages did the same (4554-4566).
        ResultChromeDecision flicker = page.Next();
        check(flicker.ToggleDrawn && same(flicker.Toggle, place) && flicker.ToggleFlicker,
            "a native control that touches the committed place for one repaint does not withhold it: the draw and the hit stay");
        check(!flicker.ToggleSuspended && !flicker.TogglePaused && page.Commit.Toggle.Phase == ResultChromePhase.Committed,
            "and that one repaint is reported as a flicker, never as a pause");
        check(flicker.ToggleConflict != null && flicker.ToggleConflict.StartsWith("overlaps=#0(", StringComparison.Ordinal),
            "and the line names the page rectangle that touched it: " + flicker.ToggleConflict);

        // A second `OnGUI` pass inside the same frame is the same observation, not a second one: the threshold counts
        // frames, so three passes of one frame can never withhold the button.
        ResultChromeDecision sameFrame = page.NextSameFrame();
        check(sameFrame.ToggleDrawn && page.Commit.Toggle.ConflictStreak == 1,
            "a repaint the engine runs again in the same frame does not advance the conflict streak: streak="
                + page.Commit.Toggle.ConflictStreak);

        // Two more distinct frames and the place is *moved aside* rather than withheld (1.19.59). The page's own rectangle
        // really stands on the place it was committed to, so the same search the commit ran is asked again with this
        // repaint's rectangles; the recorded r4 page answered this situation with twelve `paused`/`released` pairs while the
        // pointer was aiming at the button.
        page.Next();
        ResultChromeDecision covered = page.Next();
        check(covered.ToggleMoved && covered.ToggleDrawn && !covered.ToggleSuspended && !covered.TogglePaused,
            "a native control that stands on the committed place for three distinct frames moves the place aside once instead of withholding the button");
        check(!same(covered.Toggle, place) && page.Commit.Toggle.Placements == 2 && page.Commit.Toggle.Revisions == 1,
            "and the button is re-earned beside the page's control - the rectangle it was committed to is not kept and the pointer is never left without one");
        check(page.Commit.Toggle.Phase == ResultChromePhase.Committed,
            "the moved control is live on the frame it moved: it is not paused on the way");
        LayoutRect moved = covered.Toggle;

        // The rectangle that caused the move does not follow the button, and the moved place is the place this page keeps:
        // a place is committed once, so nothing drifts back to the covered rectangle.
        page.Native.Clear();
        page.Next();
        ResultChromeDecision afterMove = page.Next();
        check(afterMove.ToggleDrawn && same(afterMove.Toggle, moved) && page.Commit.Toggle.Placements == 2
              && page.Commit.Toggle.Revisions == 1,
            "the place the button moved to is the place it keeps: no second measurement, no drift back onto the page's control");
        check(!afterMove.ToggleMoved && string.IsNullOrEmpty(afterMove.ToggleConflict),
            "and a clear repaint after the move is neither a new move nor a conflict");

        // The move budget is bounded. `ResultChromeSlot.ConflictMoves` places at most, and a place that is covered again
        // after the budget is spent is withheld exactly as it always was: a button walking around the screen for ever is the
        // same defect from the other side.
        for (int move = 1; move < ResultChromeSlot.ConflictMoves; move++)
        {
            page.Native.Clear();
            page.Native.Add(new LayoutRect(page.Commit.Toggle.Rect.X, page.Commit.Toggle.Rect.Y,
                page.Commit.Toggle.Rect.Width, page.Commit.Toggle.Rect.Height));
            page.Next(); page.Next();
            ResultChromeDecision again = page.Next();
            check(again.ToggleMoved && again.ToggleDrawn,
                "a conflict on the moved place spends the next move of the bounded budget (move " + (move + 1) + ")");
        }
        page.Native.Clear();
        page.Native.Add(new LayoutRect(page.Commit.Toggle.Rect.X, page.Commit.Toggle.Rect.Y,
            page.Commit.Toggle.Rect.Width, page.Commit.Toggle.Rect.Height));
        page.Next(); page.Next();
        ResultChromeDecision spent = page.Next();
        check(!spent.ToggleDrawn && spent.ToggleSuspended && spent.TogglePaused,
            "once the move budget is spent a place that is covered again is withheld - it does not walk around the page for ever");
        check(page.Commit.Toggle.Suspensions == 1,
            "and the withhold is counted once, not once per repaint");

        page.Native.Clear();
        ResultChromeDecision clearing = page.Next();
        check(!clearing.ToggleDrawn && clearing.TogglePaused && !clearing.ToggleResumed,
            "the first clear repaint does not hand the button back yet: the conflict has to be gone for as many frames as it had to stand");

        page.Next();
        ResultChromeDecision released = page.Next();
        check(released.ToggleDrawn && released.ToggleResumed,
            "the conflict leaves and the withheld control comes back at the rectangle it stands at");

        // A control that only comes close (the standing gap, no overlap) is not a conflict: a page whose buttons
        // are laid out neighbourly must not blink the control in and out.
        LayoutRect standing = page.Commit.Toggle.Rect;
        page.Native.Add(new LayoutRect(standing.Right + 1f, standing.Y, 60f, 24f));
        ResultChromeDecision neighbour = page.Next();
        check(neighbour.ToggleDrawn && same(neighbour.Toggle, standing) && string.IsNullOrEmpty(neighbour.ToggleConflict),
            "a native control one pixel away - the gap the toggle keeps to its own anchor - is a neighbour, not a conflict");

        // Two observations an arbitrary time apart are not one continuous conflict: a frame the chrome was not drawn on is a
        // gap, and the run starts again (1.19.48).
        var gapped = new Page();
        ResultChromeDecision gappedPlace = gapped.Establish();
        gapped.Native.Add(new LayoutRect(gappedPlace.Toggle.X, gappedPlace.Toggle.Y, gappedPlace.Toggle.Width,
            gappedPlace.Toggle.Height));
        gapped.Next();
        gapped.SkipFrames(5);
        ResultChromeDecision afterGap = gapped.Next();
        check(afterGap.ToggleDrawn && gapped.Commit.Toggle.ConflictStreak == 1,
            "two overlapping repaints separated by frames nothing was measured on are not a continuous conflict: streak="
                + gapped.Commit.Toggle.ConflictStreak);
    }

    // ---- a new page, a new window --------------------------------------------------------------------------
    private static void ANewPageAndANewWindowAreTheOnlyNewLayouts(Action<bool, string> check)
    {
        var page = new Page();
        page.Establish();
        int contexts = page.Commit.Contexts;
        for (int repaint = 0; repaint < 3; repaint++) page.Next();
        check(page.Commit.Contexts == contexts && page.Commit.Toggle.Placements == 1,
            "ordinary repaints add no commitment context and no placement");

        page.NewPage(42);
        ResultChromeDecision nextPage = page.Next();
        check(nextPage.NewContext && !nextPage.ToggleDrawn && nextPage.Pending,
            "a new page object starts a new layout: the place is dropped and nothing is drawn until the new page's anchor settles");
        check(!page.Commit.Toggle.Held && page.Commit.Toggle.Placements == 0,
            "the toggle's place is dropped the moment the page changes: it is not carried over and not drawn while the new page animates - and there is no second control whose plate could outlive the page");
        ResultChromeDecision reSettled = page.Establish();
        check(reSettled.ToggleDrawn && page.Commit.Contexts == contexts + 1,
            "the new page earns its own place, once");

        // The same page at a new resolution: another layout, once.
        page.Width = 2560; page.Height = 1440;
        ResultChromeDecision resized = page.Next();
        check(resized.NewContext && !resized.ToggleDrawn && page.Commit.Toggle.Placements == 0,
            "a window of a different size is a new layout, and the old place is not carried into it");
        ResultChromeDecision resizedSettled = page.Establish();
        check(resizedSettled.ToggleDrawn && !same(resizedSettled.Toggle, reSettled.Toggle),
            "the toggle is placed once for the new window, at the new window's own size");

        // A page flavour change (failure -> success) is a new layout too: they are different pages.
        page.Succeeded = true;
        check(page.Next().NewContext,
            "the success page is a different page flavour and starts its own layout");

        // An unreadable window is no measurement, not a new resolution.
        int before = page.Commit.Contexts;
        page.Width = 0; page.Height = 0;
        check(!page.Next().NewContext && page.Commit.Contexts == before,
            "a window that reports no size is no measurement at all, so it cannot throw a place away");
    }

    // ---- F8 ----------------------------------------------------------------------------------------------
    // ---- F8 ----------------------------------------------------------------------------------------------
    private static void HidingTheBoardKeepsTheOnePlace(Action<bool, string> check)
    {
        var page = new Page();
        ResultChromeDecision settled = page.Establish();
        LayoutRect toggle = settled.Toggle;
        LayoutRect plate = settled.Plate;
        check(settled.ToggleDrawn && !settled.Pending && toggle.Width > 0f,
            "the toggle is placed beside the page's own control while the board is up");

        page.BoardUp = false;
        ResultChromeDecision hidden = page.Next();
        // The decision reports the band it planned and the plate it hung from; a hidden board plans neither, so both
        // rectangles come back empty. (`PlateDrawn` is the *probe*'s fact, not the decision's - the decision only ever
        // carries the rectangles it decided on.)
        check(!hidden.HeaderDrawn && hidden.Plate.Width <= 0f && hidden.Header.Width <= 0f,
            "with the board hidden neither the band it reserved nor a plate is reported this repaint");
        check(hidden.ToggleDrawn && same(hidden.Toggle, toggle) && page.Commit.Toggle.Placements == 1,
            "the toggle stays on the screen, at the identical rectangle, while the board is hidden - it is the way back to it");

        page.BoardUp = true;
        ResultChromeDecision shown = page.Next();
        check(shown.HeaderDrawn
              && same(shown.Header, ResultChromeLayout.HeaderOf(plate, ResultChromeMetrics.For(page.Width, page.Height))),
            "showing the board again draws its header band from the same plate");

        // The plate itself goes away for a few repaints (the board is being re-measured) and comes back unmoved.
        page.PlateDrawn = false;
        bool bandWithheld = true;
        for (int repaint = 0; repaint < 3; repaint++)
        {
            if (page.Next().HeaderDrawn) bandWithheld = false;
        }
        check(bandWithheld && same(page.Commit.Toggle.Rect, toggle),
            "a plate that cannot be read for a few repaints draws no band, and still does not move the toggle");
        page.PlateDrawn = true;
        check(page.Next().HeaderDrawn,
            "and the band is drawn again the moment the plate is readable");

        // A board that really is re-placed does not move the toggle: its place belongs to the page's own control.
        page.Plate = new LayoutRect(plate.X, plate.Y + 40f, plate.Width, plate.Height);
        ResultChromeDecision moved = page.Next();
        check(moved.ToggleDrawn && same(moved.Toggle, toggle),
            "a board re-placed 40 px lower leaves the toggle exactly where it was: nothing about a re-measure moves it");
        check(page.Commit.Toggle.Placements == 1,
            "and it is still one placement for the whole page - a re-measure is not a place");
    }

    // ---- the page that anchors nowhere, and the control that arrives late ---------------------------------
    //
    // The recorded host failure page answered `anchor=no-host-action` for its first repaints and
    // `anchor=HostActionButton` once the return button was really drawn (log 2910 -> 3363). There is no fallback
    // seat and no dock any more (1.19.45), so this is the whole of the late-arrival contract: nothing is drawn
    // while the page offers nothing, and the moment the page's own control is drawn and has stopped moving, the
    // toggle earns its place beside it - and a press on it is the click.
    private static void ANoAnchorPageStillAnswersWhenTheControlArrives(Action<bool, string> check)
    {
        var page = new Page { AnchorDrawn = false, AnchorWhy = "no-host-action" };
        bool nothingWhileNoAnchor = true;
        for (int repaint = 0; repaint < 5; repaint++)
        {
            ResultChromeDecision pending = page.Next();
            if (pending.ToggleDrawn || !pending.Pending || page.Commit.Toggle.Held) nothingWhileNoAnchor = false;
        }
        check(nothingWhileNoAnchor,
            "a page that draws no anchor gets no control at all: nothing is drawn, no place is committed, no click box exists, and F8 is untouched");

        // The host's own return button is drawn now: one repaint observes it, the next one places against the very
        // same rectangle - the settle rule earns the place, and no header seat or dock may take it over.
        page.AnchorDrawn = true;
        page.Source = ResultChromeAnchor.HostActionButton;
        page.AnchorWhy = "go-to-lobby-button";
        page.Anchor = new LayoutRect(860f, 990f, 200f, 32f);
        ResultChromeDecision observed = page.Next();
        check(!observed.ToggleDrawn,
            "the first repaint that can see the button is only an observation: one measurement is not a settled anchor");
        ResultChromeDecision arrived = page.Next();
        check(arrived.ToggleDrawn && arrived.ToggleCommitted,
            "the second repaint that sees the same rectangle commits the place - the late button is answered, not ignored");
        check(arrived.Toggle.Right <= page.Anchor.X - 1f && Math.Abs(arrived.Toggle.Bottom - page.Anchor.Bottom) < 0.001f,
            "and it stands immediately left of the page's own control, on that control's own floor");

        var gate = new ResultChromeInputGate();
        gate.Fire(true, false, false, pressed: false, held: false, released: true);
        float x = arrived.Toggle.X + arrived.Toggle.Width * 0.5f, y = arrived.Toggle.Y + arrived.Toggle.Height * 0.5f;
        check(gate.Fire(true, arrived.ToggleDrawn, ResultChromeLayout.Inside(arrived.Toggle, x, y),
                pressed: true, held: true, released: false) && gate.Fires == 1,
            "and a press on it is the click: the control that arrived late is really clickable at the place it earned");
    }

    // ---- the band, at every window the player runs ----------------------------------------------------------
    private static void TheHeaderBandStaysAboveTheTableAtEveryWindow(Action<bool, string> check)
    {
        int[][] windows =
        {
            new[] { 1280, 720 }, new[] { 1920, 1080 }, new[] { 2560, 1440 },
            new[] { 3440, 1440 }, new[] { 1024, 768 }
        };
        foreach (int[] window in windows)
        {
            int width = window[0], height = window[1];
            ResultChromeMetrics metrics = ResultChromeMetrics.For(width, height);
            var page = new Page
            {
                Width = width,
                Height = height,
                // The page's own foot control, in its own window: the toggle stands beside this at every size.
                Anchor = new LayoutRect(width * 0.42f, height * 0.86f, width * 0.12f, height * 0.028f),
                Plate = new LayoutRect(width * 0.30f, height * 0.27f, width * 0.40f, height * 0.42f)
            };
            ResultChromeDecision settled = page.Establish();
            LayoutRect header = ResultChromeLayout.HeaderOf(settled.Plate, metrics);
            LayoutRect table = new LayoutRect(settled.Plate.X, settled.Plate.Y + metrics.HeaderHeight,
                settled.Plate.Width, Math.Max(0f, settled.Plate.Height - metrics.HeaderHeight));

            check(settled.HeaderDrawn && same(settled.Header, header),
                "the band comes from the plate's own top strip at " + width + "x" + height);
            check(Math.Abs(header.Height - metrics.HeaderHeight) < 0.001f && header.Width == settled.Plate.Width,
                "and it is exactly the reserved header height across the plate's whole width at " + width + "x" + height);
            check(header.Bottom <= table.Y + 0.001f && !header.Overlaps(table),
                "it never stands on the first row of figures: the reserved header is real page space at " + width + "x" + height);
            check(header.X >= 8f && header.Right <= width - 8f && header.Y >= 8f && header.Bottom <= height - 8f,
                "the whole band is on the screen at " + width + "x" + height);
            // 1.19.45: the band carries no control of this mod's own. The one control stands beside the page's own,
            // which `OnePlaceForTheWholePage` and the late-arrival scenario assert.
            Console.WriteLine("CHROME-BAND: screen=" + width + "x" + height
                + " header=" + metrics.HeaderHeight.ToString("0") + "px band=(" + header.X.ToString("0") + "," + header.Y.ToString("0")
                + " " + header.Width.ToString("0") + "x" + header.Height.ToString("0") + ")"
                + " placements=" + page.Commit.Toggle.Placements);
        }
    }

    // ---- readability ---------------------------------------------------------------------------------------
    private static void TheOneControlIsReadableAndClear(Action<bool, string> check)
    {
        ResultChromeLayout.Visuals(ResultChromeVisual.Idle, out ResultInk idleFill, out ResultInk idleInk);
        check(ResultChromeTheme.Contrast(idleInk, idleFill) >= 4.5f,
            "an idle toggle's word clears 4.5:1 against its own fill ("
            + ResultChromeTheme.Contrast(idleInk, idleFill).ToString("0.0") + ":1), so the control that gives the board back is legible");

        ResultChromeMetrics small = ResultChromeMetrics.For(1280f, 720f);
        ResultChromeMetrics reference = ResultChromeMetrics.For(1920f, 1080f);
        ResultChromeMetrics large = ResultChromeMetrics.For(2560f, 1440f);
        check(small.ToggleFont > 0f && reference.ToggleFont > 0f && large.ToggleFont > 0f,
            "the control carries its own glyph size at every window ("
            + small.ToggleFont.ToString("0.#") + "/" + reference.ToggleFont.ToString("0.#") + "/"
            + large.ToggleFont.ToString("0.#") + " px) instead of the table header's step");

        // Both labels the one control can carry fit the box the metrics state.
        string previous = CombatStrings.Language;
        try
        {
            CombatStrings.Language = "zh";
            reference.ToggleBox(reference.ToggleHeight / 0.75f, out float zhWidth, out float zhHeight);
            check(zhWidth - ResultChromeTheme.ControlPadX * 2f >= 4f * 1.15f * reference.ToggleFont,
                "the Chinese label 隐藏看板/显示看板 fits the box: " + zhWidth.ToString("0") + "x" + zhHeight.ToString("0") + " px");
            CombatStrings.Language = "en";
            reference.ToggleBox(reference.ToggleHeight / 0.75f, out float enWidth, out _);
            check(enWidth - ResultChromeTheme.ControlPadX * 2f >= 5f * 0.66f * reference.ToggleFont,
                "the English label HIDE/SHOW fits the same box: " + enWidth.ToString("0") + " px");
        }
        finally { CombatStrings.Language = previous; }
    }

    // ---- the hit box ----------------------------------------------------------------------------------------
    private static void APausedControlKeepsNoHotBox(Action<bool, string> check)
    {
        var page = new Page();
        ResultChromeDecision settled = page.Establish();
        LayoutRect place = settled.Toggle;
        float insideX = place.X + place.Width * 0.5f, insideY = place.Y + place.Height * 0.5f;

        // The click that opened the page is a press this control has never seen a release for: it must not
        // answer for the button that page just drew.
        var gate = new ResultChromeInputGate();
        check(!gate.Fire(true, settled.ToggleDrawn, ResultChromeLayout.Inside(settled.Toggle, insideX, insideY),
                pressed: true, held: true, released: false) && gate.Fires == 0,
            "the click that opened the page does not answer for the toggle: no release was seen before that press");
        gate.Fire(true, false, false, pressed: false, held: false, released: true);

        // 1.19.59: a native rectangle that really stands on the place moves the control aside (bounded), and a press where it
        // used to be answers nothing - the hot rectangle travels with the control that is really drawn.
        page.Native.Add(new LayoutRect(place.X, place.Y, place.Width, place.Height));
        page.Next(); page.Next();
        ResultChromeDecision moved = page.Next();
        check(moved.ToggleDrawn && !same(moved.Toggle, place),
            "a native rectangle that stands on the committed place moves the control aside instead of withholding it (1.19.59)");
        check(moved.ToggleDrawn && !ResultChromeLayout.Inside(moved.Toggle, insideX, insideY)
              && !gate.Fire(true, moved.ToggleDrawn, ResultChromeLayout.Inside(moved.Toggle, insideX, insideY),
                    pressed: true, held: true, released: false) && gate.Fires == 0,
            "and a press at the rectangle it used to occupy answers nothing: the hot box is the rectangle the control is really drawn at, nowhere else");
        gate.Fire(true, false, false, pressed: false, held: false, released: true);

        // Spend the rest of the bounded move budget, then drive a place that is covered again: it is withheld exactly as it
        // always was, and a withheld control keeps no hot box.
        for (int move = 0; move < ResultChromeSlot.ConflictMoves && page.Commit.Toggle.MayMoveAside; move++)
        {
            page.Native.Clear();
            page.Native.Add(new LayoutRect(page.Commit.Toggle.Rect.X, page.Commit.Toggle.Rect.Y,
                page.Commit.Toggle.Rect.Width, page.Commit.Toggle.Rect.Height));
            page.Next(); page.Next(); page.Next();
        }
        page.Native.Clear();
        page.Native.Add(new LayoutRect(page.Commit.Toggle.Rect.X, page.Commit.Toggle.Rect.Y,
            page.Commit.Toggle.Rect.Width, page.Commit.Toggle.Rect.Height));
        page.Next(); page.Next();
        ResultChromeDecision paused = page.Next();
        check(!paused.ToggleDrawn && paused.ToggleSuspended,
            "the control is withheld while a native rectangle stands on it, once the move budget is spent");
        check(!gate.Fire(true, paused.ToggleDrawn, true, pressed: true, held: true, released: false) && gate.Fires == 0,
            "and a press inside where it used to be answers nothing: the click belongs to the page under it, and no hot rectangle is kept for a control that is not drawn");
        gate.Fire(true, false, false, pressed: false, held: false, released: true);

        page.Native.Clear();
        page.Next(); page.Next();
        ResultChromeDecision released = page.Next();
        check(released.ToggleDrawn && gate.Fire(true, released.ToggleDrawn,
                ResultChromeLayout.Inside(released.Toggle, released.Toggle.X + 1f, released.Toggle.Y + 1f),
                pressed: true, held: true, released: false) && gate.Fires == 1,
            "the moment it is back, the same press on it is the click again");
    }

    // ---- the behaviour this release replaces ----------------------------------------------------------------
    private static void ThePreviousPerRepaintRuleFailsTheseTests(Action<bool, string> check)
    {
        // The old rule, run through the same production primitives: ask the page what it draws *this* repaint and
        // place the toggle against that answer, every repaint.
        ResultChromeMetrics metrics = ResultChromeMetrics.For(1920f, 1080f);
        LayoutRect firstAnchor = new LayoutRect(860f, 990f, 200f, 32f);
        LayoutRect driftedAnchor = new LayoutRect(800f, 942f, 200f, 32f);
        bool firstDrawn = ResultChromeLayout.TryToggle(firstAnchor, null, 1920f, 1080f, metrics, out LayoutRect firstPlace, out _);
        bool driftedDrawn = ResultChromeLayout.TryToggle(driftedAnchor, null, 1920f, 1080f, metrics, out LayoutRect driftedPlace, out _);
        check(firstDrawn && driftedDrawn && !same(firstPlace, driftedPlace)
              && Math.Abs(firstPlace.Y - driftedPlace.Y) > 12f,
            "the per-repaint rule the mod used to run moved the toggle 48 px with the page's animation - which is exactly what the committed place refuses to do");

        // The old rule also stepped *aside* for a late control rather than pausing: the two places are different
        // rectangles, so the assertions above cannot pass on the old behaviour either.
        var blocker = new LayoutRect(firstPlace.X - 1f, firstPlace.Y - 1f, firstPlace.Width + 2f, firstPlace.Height + 2f);
        check(ResultChromeLayout.TryToggle(firstAnchor, new[] { blocker }, 1920f, 1080f, metrics, out LayoutRect stepped, out string steppedWhy)
              && !same(stepped, firstPlace) && stepped.X < firstPlace.X,
            "and it stepped one place to the left (" + steppedWhy + ") where this release pauses at the same place instead");

        // The committed rule, on the same measurements: the toggle does not move at all when the page's own control
        // drifts, and a plate that moves by less than half a pixel spends nothing.
        var page = new Page();
        ResultChromeDecision settled = page.Establish();
        LayoutRect committed = settled.Toggle;
        page.Anchor = driftedAnchor;
        // The page's own plate really moves by two pixels - a re-placement of the board, not a wobble - and the toggle
        // still may not follow it: its place is the page's own control, not the board. (Restored input: the two-pixel
        // move the old close-era demo used, at this page's own plate height.)
        LayoutRect nudged = new LayoutRect(575f, 392f, 770f, 500f);
        page.Plate = nudged;
        ResultChromeDecision later = page.Next();
        check(later.ToggleDrawn && same(later.Toggle, committed),
            "that same drifted anchor leaves this release's toggle on the committed rectangle");
        page.Plate = new LayoutRect(575f, 392.4f, 770f, 500f);
        ResultChromeDecision wobble = page.Next();
        check(wobble.ToggleDrawn && same(wobble.Toggle, committed),
            "and a plate that wobbles by less than half a pixel moves nothing at all");
        check(page.Commit.Toggle.Placements == 1,
            "one placement for the whole page: the place is earned once and never re-derived");
    }

    // ---- the wiring the headless suite cannot run ----------------------------------------------------------
    private static void TheRuntimeIsWiredToTheCommittedPlace(Action<bool, string> check)
    {
        string root = SourceRoot();
        if (root == null) { check(false, "the settlement-controls wiring check locates the production sources"); return; }
        string display = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        string policy = File.ReadAllText(Path.Combine(root, "ResultSettlementChrome.cs"));

        // The policy is the only thing that decides, and the renderer calls it once per repaint with this frame's
        // measurements - not once per candidate it happens to find.
        check(display.Contains("ResultChromeDecision decision = ResultChromeLayout.Step(ChromePlaces, ChromeAnchorSettle, metrics, ChromeFrame);"),
            "the renderer hands one repaint's measurements to the production step and draws exactly what it answers");
        check(Count(display, "ResultChromeLayout.Step(") == 1,
            "the step is called once per repaint, not once per control or once per candidate place");
        check(!display.Contains("TryToggle(") && !display.Contains("TryClose("),
            "the renderer no longer derives a place itself: both are committed by the policy, where the timing rules live");
        check(Count(display, "TryChromeAnchor(") == 2,
            "the page is asked for its anchor in exactly one place (one declaration, one call), so a committed place cannot be re-derived behind the policy's back");
        check(policy.Contains("if (toggle.Phase == ResultChromePhase.Pending)") && policy.Contains("if (toggle.Held)"),
            "the policy only looks for a place while the slot is pending, and only uses the committed one afterwards");
        // 1.19.55 (B): the same three states, and the conflict is now measured with the page's own breathing as its
        // tolerance - `Conflicts` answers "yes" only for an overlap deeper than `ConflictTolerance`, which is the figure
        // the commit clearance was built from. The call still happens once per repaint, from the committed rectangle,
        // and the pause still only withholds: nothing in this path moves the control.
        check(policy.Contains("public bool Suspend(string why)") && policy.Contains("public bool Resume()")
              && policy.Contains("bool overlap = Conflicts(toggle.Rect, probe.Native, ConflictTolerance(metrics));")
              && policy.Contains("toggle.ObserveConflict(overlap, probe.Frame);")
              && policy.Contains("if (toggle.ConflictStanding)"),
            "a late native control pauses the committed place and releases the same rectangle; nothing moves it - and the "
                + "overlap it needs is deeper than the page's own breathing (1.19.55)");
        // 1.19.48: the pause is earned by *distinct frames*, never by calls - `OnGUI` runs several times per frame - and the
        // threshold is a named constant rather than a literal, so the recorded one-frame flicker cannot withdraw the button.
        check(policy.Contains("public const int ConflictFrames = 3;")
              && policy.Contains("if (frame == _conflictFrame) return;")
              && policy.Contains("bool adjacent = _conflictFrame >= 0 && frame == _conflictFrame + 1;")
              && policy.Contains("if (!adjacent) _conflictStreak = 0;")
              && policy.Contains("public bool ConflictStanding => _conflictAnswer && _conflictStreak >= ConflictFrames;")
              && policy.Contains("public bool ConflictCleared => !_conflictAnswer && _conflictStreak >= ConflictFrames;"),
            "the conflict has to stand for a named number of distinct frames before the control is withheld, and the same "
                + "number of clear frames before it comes back");
        check(display.Contains("ChromeFrame.Frame = Time.frameCount;"),
            "the renderer tells the policy which engine frame this repaint belongs to, so a repeated OnGUI pass counts once");
        check(display.Contains("bool overNative = PointerOverNative();")
              && display.Contains("press.Available && fresh && _chromeToggleValid && !overNative")
              && display.Contains("if (dispatch.Mine && !overNative)"),
            "the action and the claim ask the same native question from the same sample, so a press on a page control is the page's "
                + "on the first frame it exists");

        // A hide, a key and a re-measure are not a new page: only the page facts start a new layout.
        check(policy.Contains("if (_started && page == _page && succeeded == _succeeded && width == _width && height == _height")
              && policy.Contains("&& session == _session)")
              && policy.Contains("if (!readable || page == IntPtr.Zero) return false;"),
            "a new layout is started only by a new page, a new settlement on the same page object, a new page flavour or a new window - and a repaint that cannot see the page is none of those");
        check(Count(display, "ChromePlaces.Forget()") == 1 && display.Contains("ChromePlaces.Relayout();"),
            "the places are forgotten by the results screens alone: a configuration reload or an F8 press re-measures the board (`Relayout`) and keeps them");
        check(display.Contains("if (ChromePlaces.NeedsAnchor)") && display.Contains("ChromeFrame.AnchorWhy = \"committed\";"),
            "the page is walked for an anchor only while the toggle is pending; a committed place costs no page scan");

        // The readability work, read from the source: its own style copy, its own inks, and a label that is not
        // tinted by the face it stands on.
        int control = display.IndexOf("private static void DrawChromeControl(", StringComparison.Ordinal);
        int controlEnd = display.IndexOf("private static GUIStyle ChromeStyle(", control, StringComparison.Ordinal);
        check(control > 0 && controlEnd > control,
            "the control renderer is the one place the one control is painted from");
        string body = control >= 0 && controlEnd > control ? display.Substring(control, controlEnd - control) : string.Empty;
        int white = body.IndexOf("GUI.color = Color.white;", StringComparison.Ordinal);
        int ink = body.IndexOf("style.normal.textColor", StringComparison.Ordinal);
        int label = body.IndexOf("GUI.Label(", StringComparison.Ordinal);
        check(white > 0 && ink > white && label > ink,
            "the label is drawn with GUI.color at white and its own stated ink - the fill no longer tints the word (the unreadable-close bug)");
        check(body.Contains("ResultChromeLayout.Visuals(state, out ResultInk fill, out ResultInk ink)")
              && !body.Contains("Close"),
            "the control is painted from its own fill and ink, and the renderer carries no second surface at all (1.19.45)");
        check(display.Contains("var style = new GUIStyle(face);") && display.Contains("style.padding = new RectOffset(0, 0, 0, 0);"),
            "the chrome's label style is a copy of the table's, so its alignment, padding and colour never touch the board's own style");
        check(display.Contains("metrics.ToggleFont, ResultChromeTheme.ToggleLabel(_chromeToggleHides)"),
            "the toggle is drawn at the toggle's own glyph size and with the word it is about to act on");
        check(display.Contains("GUI.color = previous;"),
            "and every GUI.color this renderer sets is put back");

        // The diagnostics: per page, not per process, and they carry the numbers.
        check(!display.Contains("_loggedChromeToggle =") && !display.Contains("_loggedChromeClose ="),
            "no process-wide 'already logged' flag holds this control's line back any more - the recorded three-settlement run printed one line for all three pages");
        check(display.Contains("ResultReportPolicy.OncePerPage(HeadingReports, \"chrome-committed\"")
              && display.Contains("ResultReportPolicy.OncePerPage(HeadingReports, \"chrome-toggle-pending\"")
              && display.Contains("placements=toggle"),
            "the committed line is rate-limited per page and carries the control's rectangle, the band and its placements");

        // The toggle is measured against the board's plate, and the band above it is reserved page space.
        check(display.Contains("private static LayoutRect ChromeBoardRect()")
              && display.Contains("ResultChromeLayout.BoardOf(_origin, ChromeMetrics())"),
            "the plate the pointer and the header agree on is the board's own rectangle, header included");
        check(display.Contains("_chromeHeaderRect = decision.HeaderDrawn ? decision.Header : default;")
              && display.Contains("if (boardDrawn && _chromeHeaderPending && _chromeHeaderRect.Width > 0f) DrawChromeHeader(_chromeHeaderRect);"),
            "the band is booked from the same decision the toggle came from, and painted only after this repaint's table drew - so the band and the toggle can never drift apart, and no band is left over a page whose board was refused (1.19.47 review #1)");
    }

    private static int Count(string text, string needle)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0) { count++; index += needle.Length; }
        return count;
    }

    private static string SourceRoot()
    {
        string directory = AppContext.BaseDirectory;
        for (int step = 0; step < 8 && directory != null; step++)
        {
            string candidate = Path.Combine(directory, "ResultsDisplay.cs");
            if (File.Exists(candidate)) return directory;
            directory = Path.GetDirectoryName(directory);
        }
        return null;
    }
}
