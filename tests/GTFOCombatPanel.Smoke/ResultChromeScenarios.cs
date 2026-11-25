using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// The settlement page's own controls, driven through the production types: the toggle that stands left of the
// page's real return control (or of a client page's waiting-status line), the close in the board's reserved
// header, the press discipline both of them share, and the "a new settlement starts with the board up" rule.
//
// Every assertion here runs the same `ResultSettlementChrome.cs` the game runs; the source checks at the end
// only assert the wiring the headless suite cannot reach (which native field is measured, that the header is
// reserved out of the fitted strip, that the same action is what F8 calls, and that nothing suppresses input).
internal static class ResultChromeScenarios
{
    internal static void RunInto(Action<bool, string> check)
    {
        MetricsResolveFromTheWindow(check);
        ToggleStandsLeftOfTheAnchorOnlyWhereThereIsRoom(check);
        CloseStandsInTheReservedHeader(check);
        ThePressDiscipline(check);
        ANewSettlementStartsShown(check);
        TheThemeIsOneGroundOneInkOneAccent(check);
        TheRuntimeIsWiredToThePageItMeasures(check);
    }

    private static void MetricsResolveFromTheWindow(Action<bool, string> check)
    {
        ResultChromeMetrics small = ResultChromeMetrics.For(1280, 720);
        ResultChromeMetrics reference = ResultChromeMetrics.For(1920, 1080);
        ResultChromeMetrics large = ResultChromeMetrics.For(2560, 1440);
        ResultChromeMetrics ultrawide = ResultChromeMetrics.For(3440, 1440);
        check(Math.Abs(reference.Scale - 1f) < 0.001f,
            "1080p is the reference the control numbers are stated at");
        check(Math.Abs(ResultChromeMetrics.HeaderInset - 5f) < 0.001f,
            "the header band keeps a fixed 5 px inset from the plate's own corner: the corner the band is drawn at is the plate's, not a share of it");
        check(small.Scale < reference.Scale && reference.Scale < large.Scale,
            "a smaller window gets smaller controls and a larger one larger controls, from one rule");
        check(small.Scale >= 0.75f - 0.001f && large.Scale <= 1.34f + 0.001f,
            "the rule is clamped at both ends, so a windowed client and a 4K screen are both answered");
        check(Math.Abs(ultrawide.Scale - large.Scale) < 0.001f && ultrawide.HeaderHeight == large.HeaderHeight,
            "an ultrawide window is scaled by its height alone: the extra width is page, not control");
        check(Math.Abs(ResultChromeMetrics.ScaleFor(float.NaN, float.NaN) - ResultChromeMetrics.For(1920, 1080).Scale) < 0.001f,
            "an unreadable window falls back to the reference rather than to a control size of zero");
        check(reference.HeaderHeight > 2f * ResultChromeMetrics.HeaderInset + 20f,
            "the reserved header is the inset, the row it reserves and the inset again (53 px at 1440p), so the band has real page space");
        reference.ToggleBox(reference.ToggleHeight / 0.75f, out float width, out float height);
        check(height >= 20f - 0.001f && height <= 28f + 0.001f && width >= 68f - 0.001f && width <= 92f + 0.001f,
            "the toggle's box is the reference mod's arithmetic: h = clamp(anchorH * 0.75, 20, 28), w = clamp(3.25h, 68, 92)");
        reference.ToggleBox(1f, out float tinyWidth, out float tinyHeight);
        check(tinyHeight >= 20f - 0.001f && tinyWidth >= 60f - 0.001f,
            "a short anchor still gets a control the player can hit (min height, min width)");
        // The label the control carries is four glyphs of its own font plus the padding both sides. A box
        // narrower than that is a box the text is trimmed in - which is what a 720p client page's waiting
        // line produced before the label's own room was asked for.
        check(tinyWidth - ResultChromeTheme.ControlPadX * 2f >= 4f * reference.ToggleFont - 0.001f,
            "and it is wide enough for its own label: four glyphs of the control's font fit beside the padding");
        small.ToggleBox(1f, out float smallTinyWidth, out _);
        check(smallTinyWidth - ResultChromeTheme.ControlPadX * 2f >= 4f * small.ToggleFont - 0.001f
              && smallTinyWidth <= 92f * small.Scale + 0.001f,
            "the same holds at 720p, the resolution where the reference band alone was narrower than the label");
        check(small.ToggleFont > 0f && reference.HeaderHeight > 0f && ultrawide.ToggleGap > 0f,
            "every size the renderer reads is positive at every resolution");
    }

    private static void ToggleStandsLeftOfTheAnchorOnlyWhereThereIsRoom(Action<bool, string> check)
    {
        ResultChromeMetrics metrics = ResultChromeMetrics.For(1920, 1080);
        var anchor = new LayoutRect(860f, 990f, 200f, 32f);
        check(ResultChromeLayout.TryToggle(anchor, null, 1920f, 1080f, metrics, out LayoutRect toggle, out string why),
            "the toggle is produced to the left of a measured anchor (" + why + ")");
        check(toggle.Right <= anchor.X - metrics.ToggleGap + 0.001f,
            "and it is entirely left of the anchor, with the reference gap between them");
        check(Math.Abs(toggle.Bottom - anchor.Bottom) < 0.001f,
            "both stand on the anchor's own floor rather than centred on it or snapped to a screen edge");
        check(!toggle.Overlaps(anchor), "the control can never stand on the control it is anchored to");

        // An obstacle in the way: the search steps one control further left rather than overlapping it.
        var blocked = new LayoutRect(toggle.X - 2f, toggle.Y - 2f, toggle.Width, toggle.Height);
        check(ResultChromeLayout.TryToggle(anchor, new[] { blocked }, 1920f, 1080f, metrics, out LayoutRect stepped, out _)
              && stepped.Right <= blocked.X - metrics.ToggleGap + 0.001f,
            "an obstacle where the toggle would stand moves it one place further left, never on top of it");

        // No room at all: no rectangle, therefore no click box.
        var edge = new LayoutRect(40f, 990f, 200f, 32f);
        check(!ResultChromeLayout.TryToggle(edge, null, 1920f, 1080f, metrics, out LayoutRect none, out string edgeWhy)
              && none.Width == 0f,
            "an anchor hard against the screen's own margin produces no control at all (" + edgeWhy + ")");
        check(!ResultChromeLayout.TryToggle(default, null, 1920f, 1080f, metrics, out _, out string missing),
            "an unmeasurable anchor produces no control: no anchor, no click box (" + missing + ")");

        // Below the screen's own margin.
        var low = new LayoutRect(860f, 1074f, 200f, 32f);
        check(!ResultChromeLayout.TryToggle(low, null, 1920f, 1080f, metrics, out _, out string lowWhy),
            "an anchor whose floor is off the screen produces no control (" + lowWhy + ")");
    }

    private static void CloseStandsInTheReservedHeader(Action<bool, string> check)
    {
        ResultChromeMetrics metrics = ResultChromeMetrics.For(1920, 1080);
        var grid = new LayoutRect(660f, 400f, 600f, 300f);
        LayoutRect board = ResultChromeLayout.BoardOf(grid, metrics);
        check(Math.Abs(board.Height - (grid.Height + metrics.HeaderHeight)) < 0.001f && board.Y == grid.Y - metrics.HeaderHeight,
            "the plate is the table plus the header reserved above it, and the table does not move");
        LayoutRect header = ResultChromeLayout.HeaderOf(board, metrics);
        check(Math.Abs(header.Height - metrics.HeaderHeight) < 0.001f && header.Width == board.Width
              && header.Y == board.Y,
            "the header band is the plate's own top strip, measured from the same rectangle the board is drawn on");
        // 1.19.45: the band carries no control of this mod's own, so what is asserted here is the band's own geometry -
        // not a Close that has been deleted.
        check(header.Bottom <= grid.Y + 0.001f, "and it never reaches the first row of figures");
        check(ResultChromeLayout.Inside(header, header.X + 1f, header.Y + 1f)
              && !ResultChromeLayout.Inside(header, header.Right, header.Y)
              && !ResultChromeLayout.Inside(header, header.X, header.Bottom),
            "hit testing is the same half-open rectangle the band is drawn with");
        check(!header.Overlaps(grid), "and its whole box is above the table's own rectangle");
    }

    private static void ThePressDiscipline(Action<bool, string> check)
    {
        var gate = new ResultChromeInputGate();
        // The click that opened the page must not answer for it: no release has been seen yet.
        check(!gate.Fire(true, true, true, pressed: true, held: true, released: false),
            "a press with no release seen before it does not fire, which keeps the click that opened the page from closing it");
        gate.Fire(true, true, false, pressed: false, held: false, released: true);
        check(gate.Fire(true, true, true, pressed: true, held: true, released: false),
            "the next press that begins on the control, after a release, is the click");
        check(!gate.Fire(true, true, true, pressed: true, held: true, released: false) && gate.Fires == 1,
            "one physical press is one action, however many frames it is held for");
        gate.Fire(true, true, true, pressed: false, held: false, released: true);
        check(!gate.Fire(true, true, false, pressed: true, held: true, released: false) && gate.Fires == 1,
            "a press that begins outside the control and drags in never counts");
        gate.Fire(true, true, false, pressed: false, held: false, released: true);

        // A stale rectangle is not a control: the same press is refused while the control was not drawn this frame.
        check(!gate.Fire(true, rectFresh: false, inside: true, pressed: true, held: true, released: false) && gate.Fires == 1,
            "a rectangle from a frame that did not draw the control answers nothing (no stale hot rectangle)");
        gate.Fire(true, true, false, pressed: false, held: false, released: true);
        check(!gate.Fire(focused: false, true, true, pressed: true, held: true, released: false),
            "a window that does not have focus cannot be clicked through");
        gate.Fire(true, true, false, pressed: false, held: false, released: true);
        gate.Reset();
        check(!gate.Fire(true, true, true, pressed: true, held: true, released: false),
            "a reset (a new page, a new resolution) needs a fresh release before the next click counts");

        check(ResultChromeLayout.Visual(gate, new LayoutRect(0f, 0f, 10f, 10f), true, true, 5f, 5f) == ResultChromeVisual.Hover,
            "the box under the pointer is the hover state");
        check(ResultChromeLayout.Visual(gate, new LayoutRect(0f, 0f, 10f, 10f), false, true, 5f, 5f) == ResultChromeVisual.Idle,
            "and a control that was not drawn this frame cannot look hovered");
        check(ResultChromeLayout.Visual(gate, new LayoutRect(0f, 0f, 10f, 10f), true, true, 50f, 5f) == ResultChromeVisual.Idle,
            "a pointer outside it is idle");
    }

    private static void ANewSettlementStartsShown(Action<bool, string> check)
    {
        var session = new ResultSettlementSession();
        IntPtr first = new IntPtr(11), second = new IntPtr(12);
        check(session.Starts(first) && !session.HiddenByPress,
            "a settlement page starts with the board up, which is the default every new settlement gets");
        session.MarkHidden();
        check(session.HiddenByPress && !session.Starts(first),
            "a hide on this page is this mod's own and stays this page's");
        // The next page starts a new session; the mod gives back the switches it turned off, once.
        bool started = session.Starts(second);
        bool owed = session.OweRestore(second);
        check(started && owed,
            "the next settlement page is shown again: the mod gives back the switches it turned off");
        check(!session.OweRestore(second), "and it gives them back once, so a hide on the new page stands");
        session.MarkShown();
        check(!session.OweRestore(second) && !session.HiddenByPress,
            "a page that was never hidden by a press owes nothing, so a switch the player set in the file is untouched");
        session.Forget();
        check(!session.HiddenByPress && session.Page == IntPtr.Zero, "leaving the results screens forgets the session");
    }

    private static void TheThemeIsOneGroundOneInkOneAccent(Action<bool, string> check)
    {
        check(ResultChromeTheme.Ground.R < 0.2f && ResultChromeTheme.Ground.G < 0.2f && ResultChromeTheme.Ground.B < 0.2f,
            "the surface is graphite: one restrained dark ground");
        check(ResultChromeTheme.Ivory.R > 0.8f && Math.Abs(ResultChromeTheme.Ivory.R - ResultChromeTheme.Ivory.G) < 0.1f,
            "labels are ivory, not pure white");
        check(ResultChromeTheme.Accent.R > 0.9f && ResultChromeTheme.Accent.G > 0.8f && ResultChromeTheme.Accent.B < 0.3f,
            "the one accent is industrial yellow");
        ResultChromeLayout.Visuals(ResultChromeVisual.Idle, out ResultInk idle, out ResultInk idleInk);
        ResultChromeLayout.Visuals(ResultChromeVisual.Hover, out ResultInk hover, out ResultInk hoverInk);
        ResultChromeLayout.Visuals(ResultChromeVisual.Pressed, out ResultInk pressed, out ResultInk pressedInk);
        check(hover.G > idle.G && pressed.G > hover.G,
            "hover and press are tone steps of the same fill, not a scale or an animation");
        check(idleInk.B > 0.7f && hoverInk.B < 0.3f,
            "an idle control is labelled in ivory and a live one in the accent");
        check(ResultChromeTheme.RowBandShare > 0f && ResultChromeTheme.RowBandShare < ResultChromeTheme.SectionBandShare,
            "a section band reads stronger than the row banding it sits above");
        check(ResultChromeTheme.ToggleLabel(true) != ResultChromeTheme.ToggleLabel(false),
            "the control says what the press will do: the same button hides a board that is up and shows one that is not");
    }

    // ---- wiring the headless suite cannot run, read from the production sources --------------------------
    private static void TheRuntimeIsWiredToThePageItMeasures(Action<bool, string> check)
    {
        string root = SourceRoot();
        if (root == null) { check(false, "the chrome wiring check locates the production sources"); return; }
        string display = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        string panel = File.ReadAllText(Path.Combine(root, "PanelBehaviour.cs"));
        string hud = File.ReadAllText(Path.Combine(root, "HudTable.cs"));

        // The toggle's anchor is what the page really draws, by role, and a page with nothing gives nothing.
        check(display.Contains("ResultChromeAnchor.HostReturnButton") && display.Contains("success.m_btnLeaveExpedition"),
            "a host success page anchors its toggle on the leave-expedition button it can really press");
        check(display.Contains("fail.m_btnRestartCheckpoint") && display.Contains("fail.m_btnGoToLobby")
              && display.Contains("ResultChromeAnchor.HostActionButton"),
            "a host failure page anchors it on the higher of its own two action buttons");
        check(display.Contains("ResultPageRolePolicy.IsClient(Role())") && display.Contains("fail.m_returnToLobby_text")
              && display.Contains("ResultChromeAnchor.ClientWaitingStatus"),
            "a client failure page anchors it on the waiting-status line it really draws (the client has no return button)");
        check(display.Contains("success-footer-label") && display.Contains("GetComponentsInChildren<TextMeshPro>(true)")
              && display.Contains("HeadingAlpha(label) >= 0.99f"),
            "a client success page anchors it on the footer label the page is really drawing, not on an invented field");
        check(display.Contains("why = \"no-drawn-footer-label\""),
            "a page that draws no such line hands over no anchor, and the toggle is then not produced");

        // Drawing and hits: one repaint, one set of rectangles, and the toggle is drawn before every gate.
        int drawStart = display.IndexOf("internal static void Draw()", StringComparison.Ordinal);
        int chromeCall = display.IndexOf("DrawChrome(boardUp);", drawStart, StringComparison.Ordinal);
        int gate = display.IndexOf("ResultVisibility.Shows(", drawStart, StringComparison.Ordinal);
        check(chromeCall > drawStart && gate > chromeCall,
            "the controls are drawn before the visibility gate, so the toggle is there while the board is hidden");
        check(display.Contains("_chromeDrawnFrame = -1;")
              && display.Contains("if (_chromeToggleValid) _chromeDrawnFrame = Time.frameCount;"),
            "the 'drawn this frame' fact is cleared every repaint and only a frame that drew the one control writes it");
        check(display.Contains("press.Available && fresh && _chromeToggleValid"),
            "a press only counts for the rectangle this repaint really drew - the gate is armed with that fact, not with the decision");
        check(display.Contains("ResultChromeDecision decision = ResultChromeLayout.Step(ChromePlaces, ChromeAnchorSettle, metrics, ChromeFrame);")
              && display.Contains("if (newContext || decision.NewContext)")
              && display.Contains("ToggleGate.Reset();"),
            "a page change and a resolution change are the layout's own context change, and they drop the press in flight with it");
        check(display.Contains("try { focused = Application.isFocused; }") ,
            "a window that is not focused cannot answer for a click");

        // One action for the key and the two controls - but each control's own intent, never the last painted frame.
        check(display.Contains("private static void PressChromeControl(string control)")
              && display.Contains("bool show = !_chromeToggleHides;")
              && display.Contains("bool shown = ApplyBoardVisibility(show);"),
            "the one control takes the intent of the label it was drawn with - a 显示看板 shows, a 隐藏看板 hides - through the one visible-state action F8 shares (1.19.45 removed the second button, so there is no second intent to reconcile)");
        // Two controls, one action: the close is the same press, never a second kind of hide.
        check(display.Contains("if (show) Session.MarkShown(); else Session.MarkHidden();")
              && display.Contains("Session.OweRestore(page.Pointer)"),
            "any press that hides the board is recorded as the mod's own, and a new settlement gives the switches back");
        check(display.Contains("if (_page != null) { RestoreHidden(); ResetPlacement(); }"),
            "the hide path restores the game's own page on the frame the board goes away, with nothing held back");

        // The header is reserved out of the fitted strip, not added on top of the band.
        check(display.Contains("float header = ChromeHeader();") && display.Contains("float tableRoom = room - header;")
              && display.Contains("Fit(strip, grid, content, board, report, succeeded, header)"),
            "the close's header is taken out of the strip before the table is fitted, and placed as part of the board");
        check(hud.Contains("private static void SettlementSurface(") && hud.Contains("ResultChromeTheme.Ground")
              && hud.Contains("ResultChromeTheme.Accent") && hud.Contains("ResultChromeTheme.SectionBandShare"),
            "the settlement surface is drawn from the chrome tokens: graphite ground, raised section bands, one accent");
        check(hud.Contains("SettlementSurface(backdrop, grid, rows, content, originX, originY, plate * opacity);")
              && hud.Contains("The in-level HUD is untouched"),
            "and the in-level HUD keeps its own plate, banding, rules and frame unchanged");

        // No global input handling.
        check(!display.Contains("Input.GetKey") && !panel.Contains("BlockInput") && !panel.Contains("Cursor.lockState"),
            "nothing suppresses or captures input: the page underneath keeps every click that is not on a control");
        check(panel.Contains("ResultsDisplay.PollChromeInput();"),
            "the panel polls the two controls on the frame, next to the key it already reads");
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
