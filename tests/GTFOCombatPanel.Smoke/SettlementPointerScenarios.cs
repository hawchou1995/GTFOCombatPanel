using System;
using System.IO;
using HawChow.GTFOCombatPanel;

// 1.19.44: the settlement controls' pointer, its main button and the cursor this mod stands in for - the narrow set of
// decisions the port from `conflictmodlist` is allowed to make, measured without Unity.
//
// Everything below is the production code: `ResultPointerMath`, `ResultPointerSession`, `ResultPressFrame`,
// `ResultPointerHit.SurfaceOf`, `ResultPressOwnership`, `ResultPressDispatch`, `ResultPressAnswer` and the production
// `ResultChromeInputGate`. Only the page's own `CursorWorldPosition`/`MainButtonStatus` reads live in Unity and are
// therefore checked against the source text, the same split the rest of this suite uses.
internal static class SettlementPointerScenarios
{
    private const float ScreenWidth = 2560f;
    private const float ScreenHeight = 1440f;
    // A settlement page's plate and the toggle beside the page's own action, from the recorded pages.
    private static readonly LayoutRect Plate = new LayoutRect(405f, 941f, 1750f, 203f);
    private static readonly LayoutRect Toggle = new LayoutRect(2457f, 920f, 95f, 29f);
    private static readonly LayoutRect Close = new LayoutRect(1686f, 394f, 101f, 43f);

    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        ThePagesOwnCursorIsThePrimarySource(check);
        ANativeClickWorksWithoutTheSystemCursor(check);
        ASourceSwitchMakesNoEdge(check);
        OneFrameIsOneEdge(check);
        ThePressBelongsToTheControlThatTookIt(check);
        TheClaimEndsWithThePress(check);
        TheCursorOnlyCoversWhatWasPainted(check);
        TheWiringIsThePagesOwnAndNothingGlobal(check);
        OneFrameMakesOneDecisionAndOneAction(check);
    }

    // ---------- 1: where the sample comes from ----------

    private static void ThePagesOwnCursorIsThePrimarySource(Action<bool, string> check)
    {
        check(ResultPointerMath.TryFromWorldProjection(1000f, 200f, 5f, ScreenHeight, out float x, out float y, out string note),
            "a projected page cursor is a usable sample");
        check(Math.Abs(x - 1000f) < 0.01f && Math.Abs(y - (ScreenHeight - 200f)) < 0.01f,
            "with exactly one Y flip into window pixels: " + x.ToString("0.##") + "," + y.ToString("0.##") + " " + note);

        check(!ResultPointerMath.TryFromWorldProjection(1000f, 200f, 0f, ScreenHeight, out _, out _, out string behind)
                && behind.Contains("z<=0"),
            "a point at or behind the camera is refused, not mirrored into a hit: " + behind);
        check(!ResultPointerMath.TryFromWorldProjection(float.NaN, 200f, 5f, ScreenHeight, out _, out _, out string nan),
            "a projection that is not a number is refused: " + nan);
        check(!ResultPointerMath.TryFromWorldProjection(1000f, 200f, 5f, 0f, out _, out _, out string noScreen),
            "a window with no height is refused: " + noScreen);

        ResultPointerSample sample = new ResultPointerSample(true, 1000f, 1240f, ResultPointerSource.PageCursor, string.Empty);
        check(sample.Describe().Contains("page-cursor") && sample.Describe().Contains("1000,1240"),
            "the sample names its source in the words the log prints: " + sample.Describe());
        check(!ResultPointerSample.Invalid(ResultPointerSource.PhysicalMouse, "no camera").Valid,
            "a refused sample is never a hit, whatever the note says");
    }

    // ---------- 2: a click with the system cursor hidden ----------

    private static void ANativeClickWorksWithoutTheSystemCursor(Action<bool, string> check)
    {
        // The settlement screen hides the system cursor; the page still knows where its cursor is. One frame: a press
        // inside the toggle, sampled from the page, and the production gate answers it exactly once.
        var gate = new ResultChromeInputGate();
        check(!gate.Fire(true, false, false, false, false, false) && gate.Armed,
            "a quiet frame arms the control without pressing it");
        ResultPressFrame press = ResultPressFrame.Of(true, true, false, ResultPressSource.Page);
        check(press.Pressed && press.Down && !press.Released,
            "the rising edge of the page's own main button is the press");
        ResultPointerSample pointer = new ResultPointerSample(true, Toggle.X + 10f, Toggle.Y + 10f, ResultPointerSource.PageCursor, string.Empty);
        check(ResultPointerHit.Inside(Toggle, pointer.X, pointer.Y), "the page cursor is inside the toggle's own rectangle");
        check(gate.Fire(true, true, true, press.Pressed, press.Down, press.Released),
            "so the control fires although no system cursor exists - the press is the page's, the hit is the page's");
        check(!gate.Fire(true, true, true, false, true, false), "and holding it does not fire twice");
    }

    // ---------- 3: a switch of source is a new session, with no invented edge ----------

    private static void ASourceSwitchMakesNoEdge(Action<bool, string> check)
    {
        var page = new ResultPointerSession((IntPtr)0x1234, ResultPointerSource.PageCursor, true, 2560, 1440);
        var physical = new ResultPointerSession((IntPtr)0x1234, ResultPointerSource.PhysicalMouse, true, 2560, 1440);
        check(!page.Equals(physical), "the same page read from another source is a different pointer session");
        check(!page.Equals(new ResultPointerSession((IntPtr)0x1234, ResultPointerSource.PageCursor, false, 2560, 1440)),
            "losing focus is a different session");
        check(!page.Equals(new ResultPointerSession((IntPtr)0x1234, ResultPointerSource.PageCursor, true, 1920, 1080)),
            "a new window is a different session");
        check(!page.Equals(new ResultPointerSession((IntPtr)0x9999, ResultPointerSource.PageCursor, true, 2560, 1440)),
            "another page is a different session");
        check(page.Equals(new ResultPointerSession((IntPtr)0x1234, ResultPointerSource.PageCursor, true, 2560, 1440)),
            "and the same page, source, focus and window is the same session");

        // First frame of a session: the sample is a state, never a transition. This is the rule that keeps a switch
        // from pressing a control nobody touched.
        ResultPressFrame first = ResultPressFrame.Of(true, true, true, ResultPressSource.Physical);
        check(!first.Pressed && !first.Released && first.Down,
            "a held button on the first frame of a new session produces neither a press nor a release");
        ResultPressFrame next = ResultPressFrame.Of(true, true, false, ResultPressSource.Physical);
        check(next.Pressed, "the frame after it does, because by then the state really did rise");
        ResultPressFrame unavailable = ResultPressFrame.Of(false, false, true, ResultPressSource.None);
        check(!unavailable.Available && !unavailable.Pressed && !unavailable.Released && !unavailable.Down,
            "a source that cannot answer is not 'the button is up' - it makes no edge at all");
    }

    private static void OneFrameIsOneEdge(Action<bool, string> check)
    {
        // The prefix and the panel's own Update can run in either order inside one frame; both ask the same sampler, so
        // the second call in that frame sees the state it already accounted for.
        bool previous = false;
        ResultPressFrame first = ResultPressFrame.Of(true, true, previous, ResultPressSource.Page);
        previous = first.Down;
        ResultPressFrame second = ResultPressFrame.Of(true, true, previous, ResultPressSource.Page);
        check(first.Pressed && !second.Pressed, "two samples of one held press in one frame are one press");
        check(ResultPressFrame.Name(ResultPressSource.Page) == "page-main-button"
                && ResultPressFrame.Name(ResultPressSource.Physical) == "physical-mouse",
            "and the source that answered is named in the log's own words");
    }

    // ---------- 4: the press this mod consumed ----------

    private static void ThePressBelongsToTheControlThatTookIt(Action<bool, string> check)
    {
        // A press inside our toggle is ours; a press outside it is the page's, so no claim is ever made and the page's
        // own dispatch keeps its press (the "bubble" rule of the ported prefix).
        var gate = new ResultChromeInputGate();
        gate.Fire(true, true, false, false, false, false);        // armed
        bool outside = gate.Fire(true, true, false, true, true, false);
        var claim = new ResultPressOwnership();
        check(!outside && !claim.Armed,
            "a press outside the control fires nothing and claims nothing, so the page keeps it");
        // The release frame is observed but is not a press: the page's dispatch must see it, and the gate must not fire
        // from a release.
        check(!gate.Fire(true, true, true, false, false, true) && gate.Armed,
            "the release frame fires nothing and re-arms the control for the next press");
        check(gate.Fire(true, true, true, true, true, false), "the next press on the control fires it");
        var page = (IntPtr)0x7001;
        claim.Arm(page, 10.0);
        check(claim.Armed && claim.Page == page, "the claim carries the page it was made on, by native pointer");
        check(claim.ShouldSuppress(page, true, true, 10.0), "and that press is this mod's until it ends");
        // 1.19.46 (UI review F1): the claim is judged by that identity alone. The layout cache this mod keeps (`_page`) is
        // dropped by the very hide the press performs - `ApplyBoardVisibility(false)` -> repaint -> `ResetPlacement` sets
        // `_page = null` - and a claim judged against it was read as "no page", cleared, and the same press ran on into the
        // page's own dispatch. The ownership has no idea what the layout cache holds, which is the point: a hide is not a
        // release.
        check(claim.ShouldSuppress(page, true, true, 10.4),
            "a repaint that dropped the layout cache in between does not end the claim - the same page still owns the press");
        // The confirmed button: the claim is only ever renewed by a frame that really saw it held.
        claim.Observe(true);
        check(claim.ShouldSuppress(page, true, true, 10.9), "a held press keeps the claim alive inside its bound");
        claim.Observe(false);
        check(!claim.Armed && !claim.ShouldSuppress(page, true, true, 10.96),
            "the first frame that sees the button up drops the claim, so the release reaches the game immediately");
        // A frame whose button state cannot be read is not a confirmation either.
        claim.Arm(page, 11.0);
        claim.Observe(false);
        check(!claim.Armed, "an unreadable held signal is not 'still down': the claim is dropped, never kept on hope");
        // 1.19.46 (UI review F1 close-out): the bound belongs to the press, so a confirmation never renews it. Every held
        // frame used to move the clock, which turned `MaxSeconds` into a sliding window a stuck button never left.
        claim.Arm(page, 20.0);
        for (int frame = 0; frame < 40; frame++) claim.Observe(true);
        check(claim.Armed && claim.ShouldSuppress(page, true, true, 20.0 + ResultPressOwnership.MaxSeconds - 0.01),
            "a press held and confirmed for many frames is still this mod's right up to its own bound");
        check(!claim.ShouldSuppress(page, true, true, 20.0 + ResultPressOwnership.MaxSeconds + 0.01) && !claim.Armed,
            "and it expires one second after it began, however many frames confirmed it");
    }

    private static void TheClaimEndsWithThePress(Action<bool, string> check)
    {
        var page = (IntPtr)0x7001;
        var other = (IntPtr)0x7002;
        var claim = new ResultPressOwnership();
        claim.Arm(page, 5.0);
        check(claim.ShouldSuppress(other, true, true, 5.1) == false && !claim.Armed,
            "another page ends the claim at once and passes the press through - no page this mod did not press loses input");
        claim.Arm(page, 5.0);
        check(!claim.ShouldSuppress(page, false, true, 5.1) && !claim.Armed, "an inactive page ends it");
        claim.Arm(page, 5.0);
        check(!claim.ShouldSuppress(page, true, false, 5.1) && !claim.Armed, "a window that lost focus ends it");
        claim.Arm(page, 5.0);
        check(!claim.ShouldSuppress(page, true, true, 5.0 + ResultPressOwnership.MaxSeconds + 0.01) && !claim.Armed,
            "and a release nobody could read cannot swallow input past the bound of "
                + ResultPressOwnership.MaxSeconds.ToString("0.0") + "s");
        claim.Arm(page, 7.0);
        claim.Clear();
        check(!claim.Armed && claim.Page == IntPtr.Zero && !claim.ShouldSuppress(page, true, true, 7.0),
            "clearing it is final for that press, identity included");
        // A claim with no identity is not a claim at all: it could suppress the press on any page.
        claim.Arm(IntPtr.Zero, 8.0);
        check(!claim.Armed && !claim.ShouldSuppress(page, true, true, 8.0),
            "a claim without a page identity is refused rather than left open to every page");
    }

    // ---------- 5: the cursor covers only what this mod painted ----------

    private static void TheCursorOnlyCoversWhatWasPainted(Action<bool, string> check)
    {
        float insidePlateX = Plate.X + 20f, insidePlateY = Plate.Y + 40f;
        check(ResultPointerHit.SurfaceOf(Plate, true, Toggle, true, Close, true, insidePlateX, insidePlateY) == ResultPointerSurface.Board,
            "a point on the plate the board really drew is the board's surface");
        check(ResultPointerHit.SurfaceOf(Plate, true, Toggle, true, Close, true, Toggle.X + 5f, Toggle.Y + 5f) == ResultPointerSurface.Toggle,
            "the control on top of the plate wins, so the pointer never looks like it is on the plate edge");
        check(ResultPointerHit.SurfaceOf(Plate, true, Toggle, true, Close, true, Close.X + 5f, Close.Y + 5f) == ResultPointerSurface.Close,
            "and the close, when it was drawn, wins over both");
        check(ResultPointerHit.SurfaceOf(Plate, true, Toggle, true, Close, true, 10f, 10f) == ResultPointerSurface.None,
            "a point on page area this mod never painted is no surface: the native cursor stays there");

        // The board hidden: only the mod's own "显示看板" entry is a surface, and nothing else is.
        check(ResultPointerHit.SurfaceOf(Plate, false, Toggle, true, Close, false, insidePlateX, insidePlateY) == ResultPointerSurface.None,
            "a plate that was not painted this frame is not a surface, whatever its cached rectangle says");
        check(ResultPointerHit.SurfaceOf(Plate, false, Toggle, true, Close, false, Toggle.X + 5f, Toggle.Y + 5f) == ResultPointerSurface.Toggle,
            "while the toggle that brings the board back still is - the hidden page keeps exactly one place");
        check(ResultPointerHit.SurfaceOf(Plate, false, Toggle, false, Close, false, Toggle.X + 5f, Toggle.Y + 5f) == ResultPointerSurface.None,
            "and a toggle that was not painted is not a hit area either");
        check(!ResultPointerHit.Inside(Toggle, Toggle.Right, Toggle.Y),
            "the hit test is half open, like every other rectangle in this mod");
    }

    // ---------- 6: the wiring ----------

    private static void TheWiringIsThePagesOwnAndNothingGlobal(Action<bool, string> check)
    {
        string display = Source("ResultsDisplay.cs");
        string pointer = Source("SettlementPointer.cs");
        // The page's own cursor and camera first, the physical mouse only as the labelled fallback.
        int page = display.IndexOf("world = menu.CursorWorldPosition;", StringComparison.Ordinal);
        int project = display.IndexOf("screen = camera.WorldToScreenPoint(world);", StringComparison.Ordinal);
        int fallback = display.IndexOf("private static ResultPointerSample FallBackToPhysical(string why)", StringComparison.Ordinal);
        check(page > 0 && project > page,
            "the sample reads `CM_PageBase.CursorWorldPosition` and projects it with the page's own camera");
        check(fallback > project, "and the physical mouse is a separate, later method - the labelled fallback");
        check(display.Contains("Input.mousePosition") && display.Contains("physical-fallback: "),
            "the fallback still names itself in every sample it produces");
        // The main button: the page's own state first, the physical button only when that cannot be read.
        int pageButton = display.IndexOf("bool down = menu.MainButtonStatus;", StringComparison.Ordinal);
        int physicalButton = display.IndexOf("bool down = Input.GetMouseButton(0);", StringComparison.Ordinal);
        check(pageButton > 0 && physicalButton > pageButton,
            "`MainButtonStatus` is asked before the physical button, and the physical read is the fallback branch");
        check(!display.Contains("Input.GetMouseButtonDown(0)") && !display.Contains("Input.GetMouseButtonUp(0)"),
            "no second, independent sampler: the edges are derived from the one button state the frame really read");
        // Resets that keep a switch from inventing a gesture.
        check(display.Contains("bool newSession = !_pointerSessionValid || !session.Equals(_pointerSession);")
                && display.Contains("if (press.Available) press = ResultPressFrame.Of(true, press.Down, press.Down, press.Source);"),
            "a new session resets the gates and makes no edge on its first frame");
        check(display.Contains("_pointerSessionValid = false;") && display.Contains("_pressFrame = -1;"),
            "and a page change forgets the sample, the previous-down state and this frame's cached press");
        // 1.19.46 (UI review F1): the press claim is deliberately *not* dropped with the layout. `ForgetChrome` is the
        // layout's "this page is no longer what I was measuring" line, and a hide is exactly that - the press that hid the
        // board must not be handed to the page's own dispatch by the same repaint.
        string forget = Between(display, "internal static void ForgetChrome()", "// The controls' input, polled once per frame");
        check(forget.Length > 0 && !forget.Contains("_pressOwned.Clear()"),
            "a layout invalidation (including the hide a press performs) does not drop the press claim");
        string report = Between(display, "private static void ReportPointerState(", "private static string _pointerLogged");
        check(forget.Length > 0 && !forget.Contains("_pointerFailures") && !forget.Contains("_pointerReportGeneration"),
            "layout invalidation cannot reopen the pointer diagnostic budget");
        check(report.Length > 0 && report.Contains("uint generation = CombatSync.Generation;")
                && report.Contains("if (_pointerReportGeneration != generation)")
                && report.Contains("_pointerReportGeneration = generation;") && report.Contains("_pointerFailures = 0;")
                && report.IndexOf("if (_pointerFailures >= PointerReportLimit) return;", StringComparison.Ordinal) >= 0
                && report.IndexOf("if (_pointerFailures >= PointerReportLimit) return;", StringComparison.Ordinal)
                    < report.IndexOf("_pointerFailures++;", StringComparison.Ordinal),
            "only a new expedition reopens the bounded pointer diagnostics");
        string suppressBody = Between(display, "internal static bool ShouldSuppressNativePress(CM_PageBase page)",
            "// What the two controls did this repaint");
        check(suppressBody.Length > 0 && !suppressBody.Contains("_page"),
            "the prefix's judgement never consults the layout cache `_page` - it is judged by the claimed page's identity");
        check(suppressBody.Contains("IntPtr candidate;") && suppressBody.Contains("candidate = page.Pointer;")
                && suppressBody.Contains("_pressOwned.ShouldSuppress(candidate, active, focused, Time.realtimeSinceStartup)"),
            "it reads the candidate page from the dispatch's own instance and hands that pointer to the claim");
        check(suppressBody.Contains("ResultPressDispatch frame = DispatchPressFrame();")
                && suppressBody.Contains("if (!frame.Press.Available || !frame.Press.Down)"),
            "and it makes the frame's own decision - one sample, shared with this mod's poll - before judging, so a release passes through");
        check(display.Contains("if (dispatch.Mine && !overNative) _pressOwned.Arm(pagePointer, Time.realtimeSinceStartup);"),
            "the claim is made at that decision, never over a rectangle the page itself is drawing, and it carries the page");
        // 1.19.46 (UI review F1 close-out): the decision is the *frame's*, so a prefix that runs before this mod's own
        // poll claims the press it is about to be asked about; the action is booked separately, once per frame.
        check(display.Contains("private static ResultPressDispatch DispatchPressFrame()")
                && display.Contains("if (frame == _dispatchFrame) return _dispatchValue;"),
            "one press decision per frame, whoever asks for it - the prefix-first and driver-first orders share it");
        check(suppressBody.Contains("if (candidate != ChromePagePointer()) return false;"),
            "only the resident page's own dispatch is answered (and claimed) by the prefix - another page passes untouched");
        string pollBody = Between(display, "internal static void PollChromeInput()",
            "// The frame's one decision, made by whoever asks first.");
        check(pollBody.Length > 0 && pollBody.Contains("ResultPressDispatch frame = DispatchPressFrame();")
                && pollBody.Contains("if (_pressAnswer.Covers(frame.Frame)) return;")
                && pollBody.Contains("_pressAnswer = _pressAnswer.Take(frame.Frame);"),
            "the action layer reads the same decision and books the frame it answered, so one press is one action");
        check(forget.Length > 0 && forget.Contains("_dispatchFrame = -1;") && !forget.Contains("_pressAnswer"),
            "a layout invalidation (the hide a press performs included) drops the decision but never the answered-frame ledger");
        int decision = display.IndexOf("private static ResultPressDispatch EvaluatePressFrame(int frame)", StringComparison.Ordinal);
        int claimAtDecision = display.IndexOf("if (dispatch.Mine && !overNative) _pressOwned.Arm(pagePointer, Time.realtimeSinceStartup);", StringComparison.Ordinal);
        check(decision > 0 && claimAtDecision > decision,
            "the claim is made inside the evaluation, which is what lets the press be claimed before the page's dispatch acts");
        check(display.Contains("private static IntPtr ChromePagePointer()") && display.Contains("private static bool PointerOverNative()"),
            "both facts the claim needs are read from the resident page and this repaint's native rectangles");
        check(display.Contains("int frame = Time.frameCount;") && display.Contains("if (frame == _pressFrame) return _pressFrameValue;"),
            "one press sample per frame, so the prefix-first and the driver-first order can never make two edges of one press");
        // The cursor: read-only on `Cursor.visible`, and never a second cursor of this mod's own (1.19.59).
        //
        // 1.19.58 and before drew a gold cross whenever the game hid the system cursor, clipped to the plate or the toggle.
        // That was a *second* cursor: the settlement pages already draw the game's own one (`CM_PageBase.m_cursor` is a
        // `CM_Cursor` with `m_trans`/`WorldPos`, updated by the page's own `UpdateCursorPosition`), so the player aimed with
        // one hotspot while the mod hovered and clicked with another. The one point every gesture now shares is the game's
        // own cursor transform, projected through the same camera as the measured controls, with the labelled physical mouse
        // as the fallback when that point cannot be proven.
        check(display.Contains("CM_Cursor cursor = menu.m_cursor;") && display.Contains("transform = cursor.m_trans;")
                && display.Contains("if (NativeCursorWorld(menu, out world)) source = \"native-cursor\";"),
            "the point the player is looking at is the game's own cursor transform (`CM_PageBase.m_cursor` -> `CM_Cursor.m_trans`), asked before the mouse-derived one");
        check(!display.Contains("DrawPointerCursor(") && !display.Contains("GUI.DrawTexture(new Rect(x, y, 4f, 15f)"),
            "and this mod paints no second cursor: the only cursor on the page is the game's own");
        check(display.Contains("if (!wholeWindow)")
                && display.Contains("return FallBackToPhysical(\"the page's camera does not prove this window's raster ("),
            "a camera whose raster cannot be proven lands the hotspot on the labelled physical mouse instead of flipping a guess");
        // The mod never sets the global cursor or its lock state, and it never invents a calibration offset.
        check(!display.Contains("Cursor.visible =") && !display.Contains("Cursor.lockState"),
            "this mod never sets the global cursor or its lock state");
        check(!display.Contains("PointerOffset") && !display.Contains("pointerCalibration"),
            "and no invented calibration offset is applied to the pointer: the only conversion is the one documented flip");
        check(!display.Contains("GUI.Button("),
            "the controls are still painted and hit by this mod's own rectangles - no `GUI.Button` was introduced");
        int patch = display.IndexOf("[HarmonyPatch(typeof(CM_PageBase), \"UpdateButtonPress\", new Type[0])]", StringComparison.Ordinal);
        int suppress = display.IndexOf("internal static bool ShouldSuppressNativePress(CM_PageBase page)", StringComparison.Ordinal);
        check(patch > 0 && suppress > 0 && suppress < patch,
            "the prefix is the page's own `UpdateButtonPress`, answered by the one pure predicate this file already defines");
        check(display.Contains("catch { return true; }"),
            "and a predicate that throws passes the press through - the game keeps it");
        check(pointer.Contains("public const double MaxSeconds = 1.0;"),
            "the claim has the ported bound, so an unreadable release cannot hold input for the run");
    }

    // ---------- 7: one press, one decision, one action ----------

    private static void OneFrameMakesOneDecisionAndOneAction(Action<bool, string> check)
    {
        var page = (IntPtr)0x7001;
        ResultPressFrame down = ResultPressFrame.Of(true, true, false, ResultPressSource.Page);
        var session = new ResultPointerSession(page, ResultPointerSource.PageCursor, true, 2560, 1440);
        // The press that starts on this mod's toggle. The page's own dispatch may ask first, so the *decision* is what
        // claims the press; the poll that reads the same decision a moment later is what answers it - once.
        var mine = new ResultPressDispatch(41, down, session, false, true, Toggle.X + 5f, Toggle.Y + 5f,
            true, true, true, true, false, true, false);
        check(mine.Live && mine.Mine && mine.Toggle && mine.Down && mine.Frame == 41,
            "a press inside a control this repaint drew is this mod's, whichever caller evaluates the frame");
        check(!ResultPressDispatch.None.Live && !ResultPressDispatch.None.Mine,
            "a frame that is not a result-page frame decides nothing, so nothing can be claimed or answered");
        var outside = new ResultPressDispatch(42, down, session, false, true, 10f, 10f, true, true, true, false, false, false, false);
        check(outside.Live && !outside.Mine,
            "a press on page area this mod never painted is the page's: no claim, no suppression, no action");
        var stale = new ResultPressDispatch(43, down, session, false, true, Toggle.X + 5f, Toggle.Y + 5f,
            false, true, true, true, false, false, false);
        check(stale.Live && !stale.Mine,
            "and a control that stopped being drawn cannot answer, however well the pointer fits its cached rectangle");
        var release = new ResultPressDispatch(44, ResultPressFrame.Of(true, false, true, ResultPressSource.Page), session,
            false, true, Toggle.X + 5f, Toggle.Y + 5f, true, true, true, true, false, false, false);
        check(release.Live && release.Up && !release.Down && !release.Mine,
            "the release of the same press is not a press: it passes to the page and ends the claim");

        // The action ledger: the frame the mod answered is remembered, and nothing else is.
        ResultPressAnswer answer = ResultPressAnswer.None;
        check(!answer.Covers(41), "nothing has been answered before the first press");
        answer = answer.Take(41);
        check(answer.Covers(41), "the frame the mod answered is remembered");
        check(!answer.Covers(42) && !answer.Covers(-1),
            "and only that frame: the next press, and a frame that is not one, are not covered");
    }

    // ---------- helpers ----------

    private static string Source(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj")))
            directory = directory.Parent;
        if (directory == null) throw new InvalidOperationException("The plugin source directory could not be found from " + AppContext.BaseDirectory);
        string path = Path.Combine(directory.FullName, name);
        if (!File.Exists(path)) throw new FileNotFoundException("Production source is missing", path);
        return File.ReadAllText(path);
    }

    private static string Between(string text, string from, string to)
    {
        int start = text.IndexOf(from, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        int end = text.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        if (end < 0) end = text.Length;
        return text.Substring(start, end - start);
    }
}
