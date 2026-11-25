using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// The six findings of the settlement-controls review, one scenario each, driven through the production policy.
//
// Each scenario is named for its finding and asserts the *key path* - the sequence of frames a player's page really
// goes through - rather than a line of source. The source is read only where the finding is about wiring that no
// headless test can execute (a Unity call, a cache slot, a page walk).
//
//   1. a hidden board must not take the SHOW control with it (the page the controls stand on is not the board)
//   2. a withheld close comes back, and a board that is re-placed a third time still gets its close
//   3. a press / re-measure keeps the place, and a new settlement on the *same* page object does not
//   4. one cached label style per control and per size, with both IMGUI tints pinned and restored
//   5. the page is walked for an anchor only while the toggle is pending
//   6. an unreadable alpha is not an arrival, and is not reported as "this page has no waiting line"
internal static class ResultChromeReviewScenarios
{
    internal static void RunInto(Action<bool, string> check)
    {
        HiddenBoardKeepsTheShowControl(check);
        TheWithheldPlaceComesBackAndIsNeverMoved(check);
        APressKeepsThePlaceAndANewSettlementDoesNot(check);
        OneCachedStylePerControlAndSize(check);
        NoAnchorWalkOnceCommitted(check);
        UnreadableAlphaIsNotArrived(check);
    }

    // ---- the scripted page (same shape the production renderer fills) -------------------------------------
    private sealed class Page
    {
        private readonly ResultChromeCommit _commit = new ResultChromeCommit();
        private readonly ResultChromeSettle _settle = new ResultChromeSettle();
        private readonly ResultChromeProbe _probe = new ResultChromeProbe();
        private readonly List<LayoutRect> _native = new List<LayoutRect>();
        private IntPtr _pointer = new IntPtr(41);

        internal int Width = 1920, Height = 1080;
        internal bool PagePresent = true;
        internal int Session = 1;
        internal bool Succeeded;
        internal bool BoardUp = true;
        internal bool AnchorDrawn = true;
        internal LayoutRect Anchor = new LayoutRect(860f, 990f, 200f, 32f);
        internal ResultChromeAnchor Source = ResultChromeAnchor.ClientWaitingStatus;
        internal ResultChromeReveal Reveal = ResultChromeReveal.Arrived;
        internal string AnchorWhy = "return-to-lobby-text/alpha-arrived";
        internal bool PlateDrawn = true;
        internal LayoutRect Plate = new LayoutRect(575f, 390f, 770f, 500f);

        internal ResultChromeCommit Commit => _commit;
        internal List<LayoutRect> Native => _native;
        internal void NewPage(int pointer) { _pointer = new IntPtr(pointer); }

        internal ResultChromeDecision Next()
        {
            _probe.Reset();
            _probe.Page = PagePresent ? _pointer : IntPtr.Zero;
            _probe.Succeeded = Succeeded;
            _probe.Session = Session;
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

        // The two repaints a page needs: observe the anchor, then place against it.
        internal ResultChromeDecision Establish()
        {
            Next();
            return Next();
        }
    }

    private static bool same(LayoutRect first, LayoutRect second)
        => Math.Abs(first.X - second.X) < 0.001f && Math.Abs(first.Y - second.Y) < 0.001f
           && Math.Abs(first.Width - second.Width) < 0.001f && Math.Abs(first.Height - second.Height) < 0.001f;

    // ---- 1. the hidden board keeps the control that brings it back ----------------------------------------
    private static void HiddenBoardKeepsTheShowControl(Action<bool, string> check)
    {
        var page = new Page();
        page.Establish();
        LayoutRect place = page.Commit.Toggle.Rect;
        check(page.Commit.Toggle.Placements == 1 && page.Commit.Toggle.Held,
            "the toggle is placed once while the board is up (the baseline this finding starts from)");

        // A repaint that hands the policy no page at all - the shape a hidden board produced while the renderer
        // sourced the page from `_page`, the board's own layout cache, and handed `IntPtr.Zero` on.
        page.PagePresent = false;
        ResultChromeDecision blind = page.Next();
        check(!blind.NewContext,
            "a repaint that cannot see the page is NOT a new page: it must not start a new layout");
        check(page.Commit.Toggle.Held && page.Commit.Toggle.Placements == 1 && same(page.Commit.Toggle.Rect, place),
            "and it must not spend the place the player is aiming at (the old `Starts` treated an absent page as a new page and forgot it)");
        check(page.Commit.Contexts == 1,
            "the context count is unchanged: an unreadable page is not a page change");

        // The board is hidden and the page is readable again: this is the SHOW state, and the control has to be
        // there at the place it was committed to, or the player has no way back to the board.
        page.PagePresent = true;
        page.BoardUp = false;
        page.PlateDrawn = false;
        ResultChromeDecision show = page.Next();
        check(show.ToggleDrawn && same(show.Toggle, place),
            "a hidden board still draws its toggle at the committed place - the SHOW control survives the board it belongs to");
        check(!show.HeaderDrawn,
            "and the band, which belongs to the board, is not drawn - there is no second control to keep a place for");

        // A genuinely different page object is still a new layout.
        page.NewPage(42);
        check(page.Next().NewContext && !page.Commit.Toggle.Held,
            "a different page object is a new layout, and the place earned on the old one is dropped");

        // The wiring this finding is actually about: which page the renderer asks.
        string root = SourceRoot();
        if (root == null) { check(false, "the renderer's source is locatable from this test's output folder"); return; }
        string display = Code(File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs")));
        check(display.Contains("Component page = ChromePage();"),
            "the controls are drawn against `ChromePage()`, not against the board's layout page");
        check(display.Contains("if (_page != null && PageActive(_page)) return _page;")
              && display.Contains("if (state == eGameStateName.ExpeditionSuccess) return Success != null && PageActive(Success) ? Success : null;")
              && display.Contains("if (state == eGameStateName.ExpeditionFail) return Fail != null && PageActive(Fail) ? Fail : null;"),
            "which answers with the live layout page when there is one, and otherwise with the resident page of the result state the game is in");
    }

    // ---- 2. the withheld place comes back, and a re-placed board never moves it ----------------------------
    private static void TheWithheldPlaceComesBackAndIsNeverMoved(Action<bool, string> check)
    {
        var page = new Page();
        ResultChromeDecision settled = page.Establish();
        LayoutRect place = settled.Toggle;
        check(settled.ToggleDrawn && place.Width > 0f, "the toggle is placed beside the page's own control");

        // (a) A native control arrives on it, then leaves.
        //
        // 1.19.59: three distinct repaints of a real overlap move the place aside once (`ResultChromeSlot.ConflictMoves`)
        // instead of withholding the button while the page keeps drawing that rectangle - the recorded r4 page answered this
        // situation with twelve `paused`/`released` pairs under a pointer that was aiming at the button.
        page.Native.Add(new LayoutRect(place.X, place.Y, place.Width, place.Height));
        page.Next(); page.Next();
        ResultChromeDecision blocked = page.Next();
        check(blocked.ToggleMoved && blocked.ToggleDrawn && !same(blocked.Toggle, place),
            "a native control on the committed place for three repaints moves it aside once, so the pointer is never left without a button");
        LayoutRect movedPlace = blocked.Toggle;
        page.Native.Clear();
        page.Next(); page.Next();
        ResultChromeDecision released = page.Next();
        check(released.ToggleDrawn && same(released.Toggle, movedPlace) && page.Commit.Toggle.Revisions == 1,
            "and the place it moved to is the place this page keeps: one revision, never a drift back onto the page's own control");

        // (b) The board is re-placed four times. The toggle stands beside the page's own control, so none of that may
        // move it - and there is no second control whose header could follow the plate.
        LayoutRect plate = page.Plate;
        bool unmoved = true;
        for (int step = 1; step <= 4; step++)
        {
            page.Plate = new LayoutRect(plate.X, plate.Y + step * 40f, plate.Width, plate.Height);
            ResultChromeDecision moved = page.Next();
            if (!moved.ToggleDrawn || !same(moved.Toggle, movedPlace)) unmoved = false;
        }
        check(unmoved,
            "four successive re-placements of the board leave the toggle on the same rectangle: nothing about a re-measure moves it");
        check(page.Commit.Toggle.Revisions == 1 && page.Commit.Toggle.Placements == 2,
            "and not one of them is a revision or a new placement (revisions=" + page.Commit.Toggle.Revisions
            + ", placements=" + page.Commit.Toggle.Placements + ")");

        // (c) A conflict while the plate is unreadable must not leave a stale rectangle behind.
        page.PlateDrawn = false;
        page.Native.Add(new LayoutRect(10f, 10f, 50f, 50f));
        ResultChromeDecision unreadable = page.Next();
        check(!unreadable.HeaderDrawn && page.Commit.Toggle.Held,
            "a plate that cannot be read draws no band at all, and the toggle keeps the place it earned");
        page.PlateDrawn = true;
        page.Native.Clear();
        check(page.Next().ToggleDrawn,
            "and the moment the plate is readable and the conflict is gone the control is back");
    }

    // ---- 3. a press / re-measure keeps the place; a new settlement does not ---------------------------------
    private static void APressKeepsThePlaceAndANewSettlementDoesNot(Action<bool, string> check)
    {
        var page = new Page();
        ResultChromeDecision settled = page.Establish();
        LayoutRect place = settled.Toggle;

        // What F8 (and its config write, and the hot reload that may follow it) does: `Invalidate` -> `Relayout`.
        // The board is dropped and re-measured; the toggle beside the page's own control is not touched by that.
        page.Commit.Relayout();
        page.PlateDrawn = false;
        ResultChromeDecision relaid = page.Next();
        check(page.Commit.Relayouts == 1 && page.Commit.Contexts == 1,
            "a re-measure is recorded as a re-measure (`Relayouts=1`), not as a new page");
        check(page.Commit.Toggle.Held && page.Commit.Toggle.Placements == 1 && same(page.Commit.Toggle.Rect, place),
            "the place survives the re-measure an F8 press / a config reload / a close causes (it used to be forgotten with the board)");
        check(relaid.ToggleDrawn && same(relaid.Toggle, place),
            "and the toggle is still drawn at that place on the frame the board is gone");

        // The hide/show half of a press draws the same rectangle on both frames.
        page.BoardUp = false; page.PlateDrawn = false;
        check(page.Next().ToggleDrawn, "the SHOW state of the same press draws it as well");
        page.BoardUp = true; page.PlateDrawn = true;
        ResultChromeDecision shown = page.Next();
        check(shown.ToggleDrawn && same(shown.Toggle, place) && page.Commit.Toggle.Placements == 1,
            "hide and show around one press: same rectangle, one placement");

        // A new settlement on the *same* page object (the game reuses both result pages) is a new layout.
        page.Session++;
        ResultChromeDecision nextRun = page.Next();
        check(nextRun.NewContext && !page.Commit.Toggle.Held,
            "a new settlement on the same page object starts a new layout - the toggle's place is not inherited from the previous expedition");
        check(!page.Commit.Toggle.Held && page.Commit.Toggle.Placements == 0,
            "and the new settlement's own place has to be earned again - nothing is inherited from the previous expedition");
        ResultChromeDecision again = page.Establish();
        check(again.ToggleDrawn && page.Commit.Contexts == 2,
            "and the new settlement earns its own place, one context later");
    }

    // ---- 4. one cached style per control, both tints pinned and restored -----------------------------------
    private static void OneCachedStylePerControlAndSize(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the renderer's source is locatable from this test's output folder");
        if (root == null) return;
        string display = Code(File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs")));

        check(display.Contains("private static GUIStyle _toggleStyle;")
              && display.Contains("private static int _toggleStyleSize = -1;")
              && !display.Contains("_closeStyle"),
            "there is one cached label style for the one control, and no second slot (1.19.45)");
        check(display.Contains("private static GUIStyle ChromeStyle(int size)"),
            "the factory is asked for a size only: there is no second surface for it to keep a slot for");
        check(display.Contains("_toggleStyle = style; _toggleStyleSize = size;"),
            "the one slot is only ever replaced by a size change: no `new GUIStyle` on a repaint that did not change sizes");

        // Both IMGUI tints are pinned before the word is drawn and put back afterwards: whatever another mod (or
        // the in-level HUD) left in `GUI.contentColor` cannot colour the settlement controls.
        int control = display.IndexOf("private static void DrawChromeControl(", StringComparison.Ordinal);
        int end = display.IndexOf("private static GUIStyle ChromeStyle(", control, StringComparison.Ordinal);
        check(control > 0 && end > control, "the control renderer is one method");
        string body = control >= 0 && end > control ? display.Substring(control, end - control) : string.Empty;
        int save = body.IndexOf("Color previousContent = GUI.contentColor;", StringComparison.Ordinal);
        int pin = body.IndexOf("GUI.contentColor = Color.white;", StringComparison.Ordinal);
        int label = body.IndexOf("GUI.Label(", StringComparison.Ordinal);
        int restore = body.IndexOf("GUI.contentColor = previousContent;", StringComparison.Ordinal);
        check(save > 0 && pin > save && label > pin && restore > label,
            "`GUI.contentColor` is saved, pinned to white before the label, and restored after it - the word is the colour it states");
        check(body.Contains("GUI.color = previous;") && body.Contains("GUI.color = Color.white;"),
            "`GUI.color` is pinned the same way and restored with it");
    }

    // ---- 5. the page is walked for an anchor only while the toggle is pending ------------------------------
    private static void NoAnchorWalkOnceCommitted(Action<bool, string> check)
    {
        var page = new Page();
        check(page.Commit.NeedsAnchor,
            "a page with no committed place needs its anchor: `NeedsAnchor` is true while the toggle is pending");
        page.Establish();
        check(!page.Commit.NeedsAnchor,
            "and once the place is committed it does not - the renderer's page walk is closed from that repaint on");

        // The committed place does not depend on the anchor being measurable again.
        page.AnchorDrawn = false;
        page.AnchorWhy = "waiting-status-unmeasured";
        ResultChromeDecision blind = page.Next();
        check(blind.ToggleDrawn && page.Commit.Toggle.Placements == 1,
            "an anchor the page no longer draws does not move or remove a committed toggle: the place is not re-derived");
        check(!blind.NewContext, "and it is not a new layout either");

        string root = SourceRoot();
        if (root == null) { check(false, "the renderer's source is locatable from this test's output folder"); return; }
        string display = Code(File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs")));
        int gate = display.IndexOf("if (ChromePlaces.NeedsAnchor)", StringComparison.Ordinal);
        int walk = display.IndexOf("TryChromeAnchor(page, succeeded", StringComparison.Ordinal);
        check(gate > 0 && walk > gate,
            "the page is asked for an anchor inside the `NeedsAnchor` gate, so a committed place costs no walk of the page's label tree");
        check(display.Contains("ChromeFrame.AnchorWhy = \"committed\";"),
            "and the frame that skips the walk says so, instead of claiming the page drew nothing");
    }

    // ---- 6. an unreadable alpha is not an arrival ----------------------------------------------------------
    private static void UnreadableAlphaIsNotArrived(Action<bool, string> check)
    {
        // A text that measured but whose alpha could not be read is *not arrived*, and never becomes a place on its
        // own: however many ordinary frames measure the same rectangle, nothing is drawn and no click box is kept.
        var page = new Page { Reveal = ResultChromeReveal.Unreadable, AnchorWhy = "return-to-lobby-text/alpha-unreadable" };
        bool drawnWhileUnreadable = false, hotspotWhileUnreadable = false, reasonNamedTheRead = true;
        for (int repaint = 0; repaint < 8; repaint++)
        {
            ResultChromeDecision frame = page.Next();
            drawnWhileUnreadable |= frame.ToggleDrawn;
            hotspotWhileUnreadable |= frame.Toggle.Width > 0f || page.Commit.Toggle.Held;
            if (frame.ToggleWhy == null || !frame.ToggleWhy.Contains("alpha-unreadable")) reasonNamedTheRead = false;
        }
        check(!drawnWhileUnreadable && !page.Commit.Toggle.Held,
            "eight identical repaints with an unreadable alpha place nothing: `Unreadable` is not an arrival, so it never earns a place from the geometry alone");
        check(!hotspotWhileUnreadable,
            "and no rectangle is kept for it: a control nobody could read is not a hidden hot area");
        check(reasonNamedTheRead,
            "every one of those frames says the alpha could not be read, so the pending line names the read and not a missing line");
        check(!page.Next().ToggleWhy.Contains("no-waiting"),
            "it is never reported as a page with no waiting line - one failed read must not decide that");

        // Recovery is a *new* arrival: the failed frames left no history, so the first `Arrived` frame is only the
        // first sample and the second identical one is what may place.
        page.Reveal = ResultChromeReveal.Arrived;
        page.AnchorWhy = "return-to-lobby-text/alpha-arrived";
        check(!page.Next().ToggleDrawn,
            "the first frame whose alpha reads full is only its first sample: the unreadable frames left nothing to inherit");
        ResultChromeDecision arrivedTwice = page.Next();
        check(arrivedTwice.ToggleDrawn && page.Commit.Toggle.Placements == 1,
            "and the place is earned by two fresh arrived samples, one placement");
        check(page.Commit.Toggle.Why != null && !page.Commit.Toggle.Why.Contains("alpha-unreadable"),
            "the committed place's own reason is the arrival, not the earlier failed read");

        // A text that is readable and still fading is a different answer again, and it never places either.
        var fading = new Page { Reveal = ResultChromeReveal.Waiting, AnchorWhy = "return-to-lobby-text/alpha-fading" };
        bool drawnWhileFading = false;
        for (int repaint = 0; repaint < 4; repaint++) drawnWhileFading |= fading.Next().ToggleDrawn;
        check(!drawnWhileFading,
            "a readable alpha below full is a line still fading in: four identical repaints place nothing");
        ResultChromeDecision fadingFrame = fading.Next();
        check(fadingFrame.ToggleWhy != null && fadingFrame.ToggleWhy.Contains("fading"),
            "and the pending reason says fading, not unreadable and not absent (" + fadingFrame.ToggleWhy + ")");

        // Waiting frames leave no stable history either: the arrival that follows still has to be its own sample.
        fading.Reveal = ResultChromeReveal.Arrived;
        fading.AnchorWhy = "return-to-lobby-text/alpha-arrived";
        check(!fading.Next().ToggleDrawn,
            "the first frame a fading line reads as arrived is only its first sample - the fading frames left nothing to inherit");
        check(fading.Next().ToggleDrawn, "and the second identical one places it");

        // The page's own word for a failed measurement is carried, not replaced by a generic "no anchor".
        var unmeasured = new Page { AnchorDrawn = false, AnchorWhy = "waiting-status-unmeasured" };
        ResultChromeDecision absent = unmeasured.Next();
        check(absent.ToggleWhy != null && absent.ToggleWhy.Contains("waiting-status-unmeasured"),
            "a field that exists and could not be measured is reported with the page's own word");
        var noField = new Page { AnchorDrawn = false, AnchorWhy = "no-waiting-status-field" };
        check(noField.Next().ToggleWhy.Contains("no-waiting-status-field"),
            "and a page with no such field is reported as a different fact");

        // The wiring the headless run cannot execute: the alpha read maps NaN to Unreadable, and a text that is
        // switched off is not an anchor at all.
        string root = SourceRoot();
        if (root == null) { check(false, "the renderer's source is locatable from this test's output folder"); return; }
        string display = Code(File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs")));
        check(display.Contains("if (!float.IsFinite(alpha)) return ResultChromeReveal.Unreadable;"),
            "a not-a-number alpha is mapped to Unreadable, never to Arrived");
        check(display.Contains("if (!text.gameObject.activeInHierarchy) { why = \"not-drawn\"; return false; }"),
            "a text object that is switched off is not an anchor, however measurable its rectangle is");
        check(display.Contains("? \"no-waiting-status-field\" : \"waiting-status-\""),
            "the client failure page distinguishes a missing field from a failed measurement in the one word it prints");
        // No guessed second read either: the only other alpha helper in this mod (`ControlAlpha`) reports 1 for an
        // alpha it could not read, so the strict read is the only one allowed to answer "arrived".
        int revealStart = display.IndexOf("private static ResultChromeReveal ChromeReveal(", StringComparison.Ordinal);
        int revealEnd = display.IndexOf("private static bool TryChromeTextRect(", revealStart, StringComparison.Ordinal);
        string revealBody = revealStart > 0 && revealEnd > revealStart ? display.Substring(revealStart, revealEnd - revealStart) : string.Empty;
        check(revealBody.Length > 0 && !revealBody.Contains("ControlAlpha") && !revealBody.Contains("?? 1f"),
            "the alpha read is the strict one (`HeadingAlpha`, NaN when unknown): nothing in the reveal path falls back to a helper that reports 1 for an unreadable alpha");
    }

    private static string Code(string text)
    {
        if (text == null) return string.Empty;
        var kept = new List<string>(text.Length / 40 + 8);
        foreach (string line in text.Split('\n'))
            if (!line.TrimStart().StartsWith("//", StringComparison.Ordinal)) kept.Add(line);
        return string.Join("\n", kept);
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj")))
            directory = directory.Parent;
        return directory != null ? directory.FullName : null;
    }
}
