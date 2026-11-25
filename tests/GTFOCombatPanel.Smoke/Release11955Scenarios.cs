using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// ---------------------------------------------------------------------------------------------------
// Release11955Scenarios.cs - the five things 1.19.55 answers, each one from a recorded fact
// ---------------------------------------------------------------------------------------------------
//
// The evidence is the 2026-11-23 four-run session on the Test profile (4 runs: 2560x1440 / 1280x960 /
// 1920x1080 success, 1920x1080 failure). Everything below is either a production *policy* driven here as
// a pure function, or a source-level assertion about the one call site that uses it. Nothing here claims
// to have been observed in the game: the Unity reads (`Camera.pixelRect`, `Cursor.position`,
// `m_allItems`, `activeInHierarchy`) cannot be executed headless, and the round that wrote this could
// not start the game at all.
//
//   A  a vanilla block is put away per switch and per branch, and the way back is written down
//   B  the committed place keeps a real gap, and only an overlap deeper than the page's own breathing pauses it
//   C  the pointer, the measured rectangles and the drawn board are one grid, or the sample falls back
//   D  the success board enters early, non-interactively, with no key - and nothing else moves
//   E  the two per-page loops behind "it only appears after F8" and the 2,873/895 log lines are gone
internal static class Release11955Scenarios
{
    private const float Width = 1920f;
    private const float Height = 1080f;

    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        TheEarlyBoardIsAPictureNotAControlSurface(check);
        TheEarlyBoardOnlySwitchesTheDrawingOff(check);
        TheCommittedPlaceKeepsARealGapAndOnlyDeepOverlapsPause(check);
        ThePointerTheRectanglesAndTheBoardAreOneGrid(check);
        ThePageIdentityIsTheAttachedPageNotThePlacedOne(check);
        TheDuplicateLinesAreBoundedPerPage(check);
        AVanillaBlockIsTakenApartBranchByBranch(check);
        TheWayBackIsWrittenDown(check);
        TheClientFailureWallAndTheFixedContractAreUntouched(check);
        TheVersionCarrierIsThisFix(check);
    }

    // ---------- D: the early board -------------------------------------------------------------

    private static void TheEarlyBoardIsAPictureNotAControlSurface(Action<bool, string> check)
    {
        // 1.19.60: `Allows` gained the pair the recorded client page needed - `hostProven` and `boundsFinal` - so a page
        // without the host's privilege can only be fitted into the strip the mature board will use. Every assertion below
        // keeps its own meaning: they are asked as a *proven host* page whose bounds are final, which is the path 1.19.55
        // described and the one the player asked to keep. What the pair itself answers - including that an unreadable role
        // gets the stricter answer - is asserted on its own in `Release11960Scenarios`.
        // The strip holds the table at the height this build really draws it at: a board may stand there.
        check(ResultEarlyBoardPolicy.Allows(true, true, true, false, 500f, 420f, true, true),
            "a live page that holds this machine's own rows, whose completion section was measured, whose scan finished, "
                + "that nothing reaches, and whose strip holds the table's own floor, allows the early board");
        // Its floor is the table's own drawable height, not a fraction of the window: 420 px of table does not fit 400 px.
        check(!ResultEarlyBoardPolicy.Allows(true, true, true, false, 400f, 420f, true, true),
            "a strip shorter than the table's own floor is not a place for any board, early or mature");
        check(ResultEarlyBoardPolicy.Allows(true, true, true, false, 420f, 420f, true, true),
            "the comparison is inclusive, so a strip exactly as tall as the table counts as fitting");
        // The refusals, one by one: each is a fact the recorded pages really answered.
        check(!ResultEarlyBoardPolicy.Allows(false, true, true, false, 900f, 300f, true, true),
            "no rows of this machine's own - nothing true to print - is no early board");
        check(!ResultEarlyBoardPolicy.Allows(true, false, true, false, 900f, 300f, true, true),
            "a completion section this frame could not measure is no anchor for a table that must not move");
        check(!ResultEarlyBoardPolicy.Allows(true, true, false, false, 900f, 300f, true, true),
            "an unfinished interaction scan is no early board: the one thing it may never do is cover a control");
        check(!ResultEarlyBoardPolicy.Allows(true, true, true, true, 900f, 300f, true, true),
            "a scan that sees an active control reaching the plate refuses the early board absolutely");
        check(!ResultEarlyBoardPolicy.Allows(true, true, true, false, float.NaN, 300f, true, true)
              && !ResultEarlyBoardPolicy.Allows(true, true, true, false, 900f, float.NaN, true, true)
              && !ResultEarlyBoardPolicy.Allows(true, true, true, false, 900f, 0f, true, true),
            "an unreadable room, an unreadable floor and a zero floor are all refusals rather than a guessed placement");

        // A picture, not a target: no wheel notch and no press belongs to the board while it is early.
        check(!ResultEarlyBoardPolicy.Interactive(true) && ResultEarlyBoardPolicy.Interactive(false),
            "an early board is not a control surface, and the mature one is");

        string display = ProductionText("ResultsDisplay.cs");
        string placement = MethodBody(display, "private static bool Placement(Component page, bool succeeded,");
        check(placement.Contains("bool earlyAllowed = ResultEarlyBoardPolicy.Allows(earlyReport != null && earlyReport.Entries.Count > 0,")
              && placement.Contains("completionMeasured, successControls.Complete, successControls.OverlapsBoard, area.Height, _boardFloor,")
              && placement.Contains("HostProven(), clientBoundsFinal);"),
            "the production gate asks exactly those facts - this machine's rows, the measured section, the complete "
                + "scan, the overlap refusal, the strip against the table's own floor, and (1.19.60) whether a client "
                + "page's strip is the final one");
        check(placement.Contains("early = true;"),
            "and the answer is the early board, not a refusal and not a wait for the button");
        check(display.Contains("_hitValid = ResultEarlyBoardPolicy.Interactive(_earlyBoard);")
              && display.Contains("_scrollValid = true;"),
            "the draw path opens the press surface from the same policy - an early board has no click box - while the wheel is opened by its own flag, because a picture whose table overflows still has to be readable to its last row (1.19.59)");
        check(Tally(display, "_earlyBoard = early;") == 1,
            "the early/mature switch is written once, in the frame's own measurement - a second writer would be a second answer");
        // The mature band proof and the drawn floor are the *mature* board's gates, not reasons to withdraw the early one.
        check(display.Contains("if (!GuestClient() && !successButtonMeasured && !_earlyBoard)")
              && display.Contains("if (successBandProof != SuccessBandProofState.Proven && !early)"),
            "the footer/band proof is what the early board is waiting for, never a reason to take it away");
        // The early entry is bounded per page, and it says what it is not.
        string earlyLine = Segment(display, "HeadingReports.Add(\"success-early-entry\")", "success-reveal-released");
        check(earlyLine.Contains("not a claim that the page settled") && earlyLine.Contains("none of the game's own blocks is put away"),
            "the line says the page has not settled and that no vanilla block was touched - the two claims this build may not make");
        check(display.Contains("HeadingReports.Add(\"success-early-entry\")"),
            "and it is bounded by the per-page report set");
    }
    private static void TheEarlyBoardOnlySwitchesTheDrawingOff(Action<bool, string> check)
    {
        // 1.19.59: an early board does stand in for the page's own blocks - the recorded success page never matured
        // (`nativeFooterVisible=false` for the whole page), so `!early && placed` left the red-crystal block and the four
        // prisoner cards drawn under a table that was already on the screen. What it may *not* do is move or deactivate
        // anything: its presses and wheel still belong to the page.
        check(ResultEarlyBoardPolicy.HidesVanilla(true, true),
            "an early board that is really painted stands in for the page's own blocks (their drawing makes way)");
        check(!ResultEarlyBoardPolicy.HidesVanilla(true, false) && !ResultEarlyBoardPolicy.HidesVanilla(false, false),
            "and never before it is placed: a board that was refused a frame later has put nothing away");
        string display = ProductionText("ResultsDisplay.cs");
        check(Tally(display, "ApplyVanillaBlocks(vanillaPage, vanillaSucceeded, _earlyBoard);") == 1
              && Tally(display, "ApplyVanillaBlocks(vanillaPage, vanillaSucceeded);") == 1,
            "there are exactly two call sites of the hide (the adaptive draw, which passes the early flag, and the fixed draw, which is always a mature placement)");
        check(display.Contains("private static void ApplyVanillaBlocks(Component page, bool succeeded, bool visualOnly = false)")
              && display.Contains("if (!visualOnly && plan == VanillaHidePlan.WholeBlock)"),
            "and the early frame's flag reaches `Hide`, where it allows the drawing half only - never a move and never a switch-off");
        check(display.Contains("if (ResultEarlyBoardPolicy.HidesVanilla(_earlyBoard, true)"),
            "the adaptive call asks the early-board contract first");
    }

    // ---------- B: the committed place ---------------------------------------------------------

    // The recorded r5 page committed the toggle at (655,922,91x28) beside the native (744,868,444x81): the toggle's
    // right edge 746 against the button's left edge 744 is a two-pixel touch on the very frame it was committed, and
    // r3's button breathed between 447 and 462 under a control committed at 461. That is what paused and released the
    // button 32/30 times in one session.
    private static void TheCommittedPlaceKeepsARealGapAndOnlyDeepOverlapsPause(Action<bool, string> check)
    {
        ResultChromeMetrics metrics = ResultChromeMetrics.For(Width, Height);
        var place = new LayoutRect(655f, 922f, 91f, 28f);
        var touch = new List<LayoutRect> { new LayoutRect(744f, 868f, 444f, 81f) };

        check(ResultChromeLayout.Conflicts(place, touch),
            "with no tolerance the recorded two-pixel touch is a conflict - which is the 1.19.54 answer this release changes");
        float tolerance = ResultChromeLayout.ConflictTolerance(metrics);
        check(tolerance >= 6f, "the tolerance is at least the six pixels the smallest scaled window may use: " + tolerance.ToString("0.0"));
        check(!ResultChromeLayout.Conflicts(place, touch, tolerance),
            "with the page's own breathing as the tolerance, a two-pixel touch is not a conflict: the button stays");

        // A control that really stands on the place is hundreds of pixels deep, or at least tens.
        var covering = new List<LayoutRect> { new LayoutRect(700f, 900f, 200f, 60f) };
        check(ResultChromeLayout.Conflicts(place, covering, tolerance),
            "a native control that really stands on the committed place still pauses it");
        // Deeper on one axis only is still not "on top of it": a button that slid sideways shares an edge, not the place.
        var sliver = new List<LayoutRect> { new LayoutRect(place.Right - 3f, place.Y - 200f, 400f, 400f) };
        check(!ResultChromeLayout.Conflicts(place, sliver, tolerance),
            "an overlap that is deep on one axis and three pixels on the other is an edge, not a control standing on it");
        check(ResultChromeLayout.Conflicts(place, sliver),
            "and with no tolerance that same rectangle is what the old rule answered - the two answers differ exactly here");

        // The commit itself stands the clearance further aside, so the ordinary breathing never reaches the place at all.
        float clearance = ResultChromeLayout.CommitClearance(metrics);
        check(clearance >= 8f && clearance >= 12f * metrics.Scale - 0.001f,
            "the commit clearance is twelve pixels at the window's own scale, never under eight: " + clearance.ToString("0.0"));
        check(Math.Abs(ResultChromeLayout.CommitClearance(default(ResultChromeMetrics)) - 12f) < 0.001f,
            "a metrics value with no scale is answered with the 1080p figure rather than with NaN or infinity");
        check(Math.Abs(ResultChromeLayout.ConflictTolerance(default(ResultChromeMetrics)) - 8f) < 0.001f,
            "the same for the tolerance");

        var anchor = new LayoutRect(744f, 868f, 444f, 81f);
        var obstacles = new List<LayoutRect> { anchor };
        check(ResultChromeLayout.TryToggle(anchor, obstacles, Width, Height, metrics, out LayoutRect without, out _),
            "the toggle is still placed beside the page's own control");
        check(ResultChromeLayout.TryToggle(anchor, obstacles, Width, Height, metrics, out LayoutRect with, out _, clearance),
            "and it is placed with the commit clearance");
        check(Near(without.Right, anchor.X - metrics.ToggleGap),
            "without a clearance the place keeps the reference gap alone (" + without.Right.ToString("0.0") + " vs "
                + (anchor.X - metrics.ToggleGap).ToString("0.0") + ")");
        check(Near(with.Right, anchor.X - metrics.ToggleGap - clearance),
            "with it, the place stands the clearance further aside (" + with.Right.ToString("0.0") + "), so the page's own "
                + "breathing never reaches it");
        check(ResultChromeLayout.TryToggle(anchor, obstacles, Width, Height, metrics, out LayoutRect nan, out _, float.NaN)
              && Near(nan.Right, without.Right),
            "a clearance that is not a number is no clearance, never an infinite one");

        string policy = ProductionText("ResultSettlementChrome.cs");
        check(policy.Contains("bool overlap = Conflicts(toggle.Rect, probe.Native, ConflictTolerance(metrics));"),
            "the repaint asks the conflict with the same tolerance, from the committed rectangle");
        check(policy.Contains("float clearance = CommitClearance(metrics);")
              && policy.Contains("if (TryToggle(probe.Anchor, obstacles, width, height, metrics, out LayoutRect place, out string placeWhy, clearance))"),
            "and the commit is the one place that uses the clearance");
        check(Tally(policy, ".Revise(") == 0,
            "no call site re-places or revises a committed control: the place is never moved after it is committed");
    }

    // ---------- C: one grid --------------------------------------------------------------------

    private static void ThePointerTheRectanglesAndTheBoardAreOneGrid(Action<bool, string> check)
    {
        // The identity case: a camera that really renders this window. Nothing is converted and an existing resolution
        // cannot move by one pixel.
        var whole = new ResultPointerViewport(2560f, 1440f, 0f, 0f, 2560f, 1440f, 2560f, 1440f);
        check(whole.Whole && whole.Usable, "a camera whose raster is the window's own grid is recognised as the proven case");
        check(ResultPointerSpace.TryWindow(whole, 1280f, 720f, 1f, out float wx, out float wy, out string note)
              && Near(wx, 1280f) && Near(wy, 720f) && note == "direct",
            "and the conversion answers the same point 1.19.54's flip did (" + wx.ToString("0") + "," + wy.ToString("0") + ")");

        // 1.19.56 (C): `WorldToScreenPoint` already answers in window pixels - `pixelRect` offset included - so a
        // letterboxed camera must NOT be converted again. The recorded 1280x960 page (a 4:3 window, a camera authored for
        // 16:9 letterboxing to (0,120,1280,720)) therefore answers exactly what 1.19.54 answered, and says the mapping is
        // unproven rather than claiming a conversion took place.
        var letterbox = new ResultPointerViewport(1280f, 720f, 0f, 120f, 1280f, 720f, 1280f, 960f);
        check(!letterbox.Whole && letterbox.Usable, "a letterboxed camera is usable but is not the proven case");
        check(ResultPointerSpace.TryWindow(letterbox, 640f, 100f, 1f, out float lx, out float ly, out bool proven, out string space)
              && Near(lx, 640f) && Near(ly, 860f) && !proven && space == "unproven",
            "the projection is flipped and nothing else: (640,100) -> (" + lx.ToString("0") + "," + ly.ToString("0")
                + "), space=" + space + " - a conversion here would be a double conversion");
        check(Near(960f - 100f, ly),
            "which is the 1.19.54 answer (" + (960f - 100f).ToString("0") + "), kept because it is the measured one");
        check(letterbox.Describe().Contains("space=unproven") && whole.Describe().Contains("space=direct"),
            "and the description says how much of the mapping is proven for this repaint");

        // The refusals: only what makes a number meaningless is refused, never "this rectangle cannot be measured".
        var unknown = default(ResultPointerViewport);
        check(!unknown.Usable, "a camera that renders into a texture is not the proven case");
        check(!ResultPointerSpace.TryWindow(unknown, 10f, 10f, 1f, out _, out _, out string unknownNote) && unknownNote.Length > 0,
            "and with no window height to flip against the sample is refused with a note: " + unknownNote);
        check(!ResultPointerSpace.TryWindow(whole, 10f, 10f, 0f, out _, out _, out string behind) && behind.Contains("z<=0"),
            "a point at or behind the camera has no window position: " + behind);
        check(!ResultPointerSpace.TryWindow(whole, float.NaN, 10f, 1f, out _, out _, out _),
            "a projection that is not a number is refused");
        check(ResultPointerSpace.TryWindow(new ResultPointerViewport(float.NaN, float.NaN, 0f, 0f, Width,
                  Height, Width, Height), 100f, 200f, 1f, out float ux, out float uy, out bool unproven, out _)
              && Near(ux, 100f) && Near(uy, Height - 200f) && !unproven,
            "an unproven camera is still measured - refusing here is what would make the whole page unmeasurable");

        string display = ProductionText("ResultsDisplay.cs");
        // 1.19.61：第四个调用点是 `HintGlyphTop`——等待行真正渲染的顶点也走同一个换算，所以「一个换算」这条不变，
        // 只是又多了一个必须与指针、矩形、`textBounds` 完全一致的量。
        check(Tally(display, "ResultPointerSpace.TryWindow(") == 4,
            "exactly four call sites ask the conversion: the page pointer, `TryScreenRect`, `TryTextRect` and the slot glyph mesh ("
                + Tally(display, "ResultPointerSpace.TryWindow(") + ")");
        string sample = MethodBody(display, "private static ResultPointerSample SamplePagePointer(");
        check(sample.Contains("ResultPointerViewport viewport = PointerViewport(camera, out bool wholeWindow);")
              && sample.Contains("if (!ResultPointerSpace.TryWindow(viewport, screen.x, screen.y, screen.z, out float gx, out float gy, out string note))"),
            "the page cursor is converted through the same call every rectangle uses");
        string screenRect = MethodBody(display, "private static bool TryScreenRect(");
        string textRect = MethodBody(display, "private static bool TryTextRect(");
        check(screenRect.Contains("ResultPointerSpace.TryWindow(viewport, screen.x, screen.y, 1f,")
              && textRect.Contains("ResultPointerSpace.TryWindow(viewport, screen.x, screen.y, 1f,"),
            "both rectangle readers use the same conversion the pointer does - a control measured on another grid is "
                + "exactly how a table ends up 'not overlapping' the thing it covers");
        string viewportReader = MethodBody(display, "private static ResultPointerViewport PointerViewport(Camera camera, out bool wholeWindow)");
        check(viewportReader.Contains("if (camera.targetTexture != null)")
              && viewportReader.Contains("Rect viewport = camera.pixelRect;")
              && !viewportReader.Contains("return default;"),
            "the raster comes from the camera's own `pixelRect`; an RT camera is *unproven*, not unmeasurable (1.19.56)");
        // The one bounded observation that can locate a real mismatch, once per page and window.
        check(display.Contains("private static string _spaceReported = string.Empty;")
              && display.Contains("if (key == _spaceReported) return;"),
            "the `settlement space` line is bounded by page and window, so one page prints it once");
        check(display.Contains("[DIAG] settlement space: window=")
              && display.Contains("\",space=\"") && display.Contains(",gui=") && display.Contains(",physical=") && display.Contains(",toggle="),
            "and it puts the window, the proven/unproven space, the engine's own GUI mouse, the sampled pointer and the "
                + "committed toggle side by side - a measurement, not a claim about which of them drifted");
        check(Tally(display, "ReportPointerSpace(page, metrics);") == 1,
            "it is called once per repaint, from the chrome step that already measured this frame");
        // The press itself is unchanged: one sample, one claim, one fire, vanilla wins.
        check(display.Contains("bool overNative = PointerOverNative();")
              && display.Contains("press.Available && fresh && _chromeToggleValid && !overNative"),
            "a press on a place the page's own control covers is the page's, from the same sample: this release does not "
                + "move the pointer under the player");
    }

    // ---------- E: the two loops and the two log families ---------------------------------------

    private static void ThePageIdentityIsTheAttachedPageNotThePlacedOne(Action<bool, string> check)
    {
        string display = ProductionText("ResultsDisplay.cs");
        check(display.Contains("if (_attachedPage != page.Pointer)"),
            "`Refresh` answers 'is this a new page?' from the page this mod last attached to");
        check(!display.Contains("if (_holdPage != page.Pointer)"),
            "and no longer from `_holdPage`, which only a successful placement ever writes - that is what made every "
                + "refused repaint look like a new page, run `ForgetPage` and re-measure the screen from nothing");
        check(Tally(display, "_attachedPage = page.Pointer;") == 2,
            "the attachment is recorded on the page it attaches to, and restored after `ForgetPage` drops it");
        string forget = MethodBody(display, "private static void ForgetPage()");
        check(forget.Contains("_attachedPage = IntPtr.Zero;") && forget.Contains("_widthProbe = float.NaN; _columnProbes = 0;"),
            "the attachment and the two-phase column belong to the page and are dropped with it, and nowhere else");
        check(forget.Contains("_completionPartSignature = string.Empty; _completionPartLines = 0;"),
            "the per-part reading's signature and its line budget are reset with the page - the reset has to stay, "
                + "or the previous page's budget holds the next page's reading back");
        string reset = MethodBody(display, "private static void ResetPlacement()");
        check(!reset.Contains("_widthProbe = float.NaN") && !reset.Contains("_columnProbes = 0"),
            "`ResetPlacement` runs on every repaint and no longer wipes the measured table width: the two-phase column "
                + "measurement can reach its second phase, which is why the first placement of every success page was refused");
        // The recorded page: 640 / 1,102 / 1,132 completion-part lines and 1 / 443 / 451 release lines per page.
        check(display.Contains("if (ready && HeadingReports.Add(\"success-board-released\"))"),
            "the release line is bounded per page instead of printed on every repaint that already answered 'ready'");
        string parts = MethodBody(display, "private static void ReportCompletionParts(");
        check(parts.Contains("if (signature == _completionPartSignature) return;") && parts.Contains("_completionPartLines >= CompletionPartReportLines"),
            "the per-part reading keeps both of its own bounds: the signature and the per-page line budget");
        // A repaint may not spend the fail-open budget or forget the page: that was `strictRefusals=1 of 60`. The only
        // `ForgetPage()` left in `Refresh` is the one inside the new-page branch.
        string refresh = MethodBody(display, "internal static void Refresh(eGameStateName state)");
        int attachBranch = refresh.IndexOf("if (_attachedPage != page.Pointer)", StringComparison.Ordinal);
        int forgetAt = refresh.IndexOf("ForgetPage();", StringComparison.Ordinal);
        int secondForget = refresh.IndexOf("ForgetPage();", forgetAt + 1, StringComparison.Ordinal);
        check(attachBranch > 0 && forgetAt > 0 && secondForget > attachBranch,
            "`Refresh` has exactly two places that forget a page, and the second one is inside the new-page branch: a "
                + "refused repaint keeps the reveal latch, the settle gates, the retry cadence and the strict-miss budget");
        check(refresh.Contains("if (!results)") && Tally(refresh, "ForgetPage();") == 2,
            "the first is leaving the result screens altogether, which is a page change too - no third, unconditional one");
        check(!refresh.Contains("_strictMisses = 0"),
            "and a repaint no longer spends the fail-open budget: the 60-frame ladder can be walked to its end");
    }

    private static void TheDuplicateLinesAreBoundedPerPage(Action<bool, string> check)
    {
        string display = ProductionText("ResultsDisplay.cs");
        // Every line whose text is the same on every page is written through the per-page set or with a latch.
        int sampled = Tally(display, "settlement refreshed");
        check(sampled == 0 || display.Contains("HeadingReports.Add(\"settlement-refreshed\")"),
            "the `settlement refreshed` line (112 in one session) is bounded by the per-page set or gone entirely: " + sampled);
        string space = MethodBody(display, "private static void ReportPointerSpace(Component page, ResultChromeMetrics metrics)");
        check(space.Contains("if (key == _spaceReported) return;"),
            "the new observation is latched by page and window, so it cannot become the next 895-line family");
        check(Tally(space, "LogInfo(") == 1,
            "and it is one `LogInfo` call in one guarded place, not a loop over the page's objects");
    }

    // ---------- A: the vanilla blocks ----------------------------------------------------------

    private static void AVanillaBlockIsTakenApartBranchByBranch(Action<bool, string> check)
    {
        // A live action is one the page can dispatch to, or one that can take the pointer this moment. An unregistered,
        // switched-off `CM_Item` is a decoration hanging inside a presentational block - the recorded `artifact summary`.
        check(!ResultVanillaHidePolicy.IsLiveAction(false, false),
            "an unregistered, switched-off `CM_Item` is not an action: that is what left the artifact summary on the page");
        check(ResultVanillaHidePolicy.IsLiveAction(true, false) && ResultVanillaHidePolicy.IsLiveAction(false, true),
            "either a registered entry or one that is active in the hierarchy is");

        check(ResultVanillaHidePolicy.Plan(0, 0, 0) == VanillaHidePlan.WholeBlock,
            "a block with no `CM_Item` in it is put away as it always was");
        check(ResultVanillaHidePolicy.Plan(4, 0, 0) == VanillaHidePlan.WholeBlock,
            "and so is a block whose only `CM_Item`s are decorations");
        check(ResultVanillaHidePolicy.Plan(1, 1, 0) == VanillaHidePlan.Nothing,
            "a block whose whole body carries a live action is left exactly where the game put it (the 1.19.43 answer)");
        check(ResultVanillaHidePolicy.Plan(3, 1, 1) == VanillaHidePlan.SafeBranches,
            "a mixed block: the branches with no action may be put away");
        check(ResultVanillaHidePolicy.Plan(3, 2, 2) == VanillaHidePlan.SafeBranches,
            "however many of them carry an action - what matters is that some branch does not");

        string display = ProductionText("ResultsDisplay.cs");
        // 1.19.57: 1.19.56 moved the per-entry question into the one classifier both halves ask, so the assertion names
        // the current guard instead of the removed `LiveAction(items[index])` loop. The property under test is unchanged:
        // an entry is an action only when the page can dispatch to it or it can take the pointer now.
        string carries = MethodBody(display, "private static bool CarriesAction(Component block, Component page, bool succeeded)");
        check(carries.Contains("KindOf(block, page, succeeded) == VanillaNodeKind.Action")
              && !carries.Contains("return items != null && items.Length > 0;"),
            "`CarriesAction` asks the shared classifier instead of treating the mere presence of a `CM_Item` as an action");
        string live = MethodBody(display, "private static bool LiveAction(CM_Item item, Component page)");
        check(live.Contains("bool active = item.gameObject.activeInHierarchy;")
              && live.Contains("return ResultVanillaHidePolicy.IsLiveAction(active, RegisteredInPage(item, page));"),
            "and a live action is read from the object's own active state and the page's own registry");
        check(MethodBody(display, "private static bool RegisteredInPage(CM_Item item, Component page)").Contains("menu.m_allItems"),
            "the registry is the page's `m_allItems`, the same one the interaction scan and `UpdateButtonPress` read");

        string branches = MethodBody(display, "private static bool HideSafeBranches(Component block, string what, Component page, bool succeeded)");
        check(branches.Contains("if (children <= 1) return false;"),
            "a block whose body is one branch has no safe part to take away, so it stays");
        int record = branches.IndexOf("Vector3 at = branch.localPosition;", StringComparison.Ordinal);
        int move = branches.IndexOf("branch.localPosition = new Vector3(record.X, AwayY, record.Z);", StringComparison.Ordinal);
        check(record > 0 && move > record,
            "the branch's own position and active state are recorded before anything moves");
        check(branches.Contains("if (SharedOrRoot(node)) continue;") && branches.Contains("if (CarriesAction(node, page, succeeded)) continue;"),
            "the page root, any ancestor of it and any branch carrying an action are never touched");
        check(branches.Contains("if (hidden == 0) return false;"),
            "and a block where nothing could be taken away is reported as untouched, so the caller leaves it whole");
        string hide = MethodBody(display, "private static void Hide(Component block, string what, Component page, bool succeeded, bool visualOnly = false)");
        check(hide.Contains("VanillaHidePlan plan = PlanOfBlock(block, page, succeeded);")
              && hide.Contains("if (!visualOnly && plan == VanillaHidePlan.SafeBranches && HideSafeBranches(block, what, page, succeeded)) return;")
              && hide.Contains("SuppressVisual(block, what);"),
            "`Hide` answers a mixed block branch by branch and, when nothing may be moved, switches the drawing off instead of leaving the player's own setting unanswered (1.19.59)");
        check(hide.Contains("if (plan == VanillaHidePlan.Nothing)") && !hide.Contains("VanillaHidePlan.Nothing) { HideSafeBranches"),
            "and an entry this build cannot describe is still left exactly where the page put it");
        // The switches are per page and per block kind, as the recorded config asked (`HideVanillaFailBlocks = true`).
        string apply = MethodBody(display, "private static void ApplyVanillaBlocks(Component page, bool succeeded, bool visualOnly = false)");
        check(apply.Contains("bool successBlocks = succeeded && Plugin.HideVanillaArtifacts;")
              && apply.Contains("bool prisonerReports = succeeded && Plugin.HideVanillaReports;")
              && apply.Contains("bool failBlocks = !succeeded && Plugin.HideVanillaFailBlocks != null && Plugin.HideVanillaFailBlocks.Value;"),
            "each page reads its own switch: the success page's artifact block, its prisoner reports and the failure page's "
                + "artifact block are three separate questions");
        check(apply.Contains("if (_appliedAny && (successBlocks != _appliedSuccessBlocks || prisonerReports != _appliedPrisonerReports || failBlocks != _appliedFailBlocks))")
              && apply.Contains("RestoreHidden();"),
            "and a switch that changed puts the page's own blocks back before the new set is applied");
    }

    private static void TheWayBackIsWrittenDown(Action<bool, string> check)
    {
        string display = ProductionText("ResultsDisplay.cs");
        string restore = MethodBody(display, "private static void RestoreHidden()");
        string putBack = MethodBody(display, "private static bool PutBack(ResultVanillaHold block)");
        check(restore.Contains("if (PutBack(block)) restored++;")
              && putBack.Contains("if (block.TryRestorePosition(out float x, out float y, out float z))")
              && putBack.Contains("if (block.TryRestoreActive(out bool active))"),
            "the restore puts back the recorded position and the recorded active state - both halves, for whole blocks and "
                + "for the branches taken apart in this release - and asks the record which half it may put back at all "
                + "(1.19.59 review: a drawing-only hold has no position and no active state to put back)");
        check(restore.IndexOf("Hidden.Clear();", StringComparison.Ordinal) > restore.IndexOf("if (PutBack(block)) restored++;", StringComparison.Ordinal),
            "the record is only dropped after it has been used");
        check(restore.Contains("if (restored == 0 && refused == 0) return;"),
            "and a page with nothing hidden prints nothing - the line cannot be produced by a repaint");
        check(restore.Contains("restored=") && restore.Contains("refused="),
            "the line carries how many entries were put back and whether any refused: the recorded session had one "
                + "'moved off the page' line and no line at all for the way back");
        check(Tally(display, "RestoreHidden()") >= 7,
            "the restore is asked from every way out - the F8 hide and the switch, leaving the page, a new page, the fixed "
                + "draw's thin evidence and the config's own change (" + Tally(display, "RestoreHidden()") + " sites)");
        check(display.Contains("if (_page != null) { RestoreHidden(); ResetPlacement(); }"),
            "F8 off and the visibility switch put the page's own blocks back in the same statement that drops the placement");
        check(display.Contains("internal static void BeginFailurePage(CM_PageExpeditionFail page)") && display.Contains("RestoreHidden();"),
            "a new failure page starts by restoring the previous one - the asymmetry 1.19.47 review #2 fixed stays fixed");
    }

    // ---------- what this release may not touch ------------------------------------------------

    private static void TheClientFailureWallAndTheFixedContractAreUntouched(Action<bool, string> check)
    {
        string geomancy = ProductionText("GuestPageGeometry.cs");
        check(geomancy.Contains("public sealed class ResultClientWallGate")
              && geomancy.Contains("public static class ResultClientFailureStrip")
              && geomancy.Contains("public static float PageFloor(float screenHeight)"),
            "the client's lower wall, its two-sample gate and the page floor are all still where they were");
        string display = ProductionText("ResultsDisplay.cs");
        check(display.Contains("ResultClientFailureStrip.Measure(Time.frameCount, ClientWall, clientEvidence, out clientBand, out guestArea)")
              && display.Contains("ResultClientFailureStrip.Decide(Time.frameCount, ClientWall, clientEvidence, out clientBand, out guestArea)"),
            "the client failure path still measures an on-board strip and decides an off-board one, unchanged");
        check(display.Contains("if (GuestClient() && !succeeded && !Forced && !_fallbackPlaced && !ClientWall.Settled)"),
            "and it still waits for the wall to settle on two complete scans before a client failure board is placed");
        check(display.Contains("if (draw.HidesVanilla)") && display.Contains("ApplyVanillaBlocks(vanillaPage, vanillaSucceeded);"),
            "the fixed layout keeps its own plan and its own hide, decided where it always was");
        // The mature placement still needs the page's own boundary, and 30 rows still fit a 540 px band.
        check(display.Contains("if (successBandProof != SuccessBandProofState.Proven && !early)"),
            "an unproven band still refuses the mature board: an early board is what this release adds, not a permission");
    }

    // ---------- the version -------------------------------------------------------------------

    private static void TheVersionCarrierIsThisFix(Action<bool, string> check)
    {
        string info = ProductionText("PluginInfo.cs");
        const string carrier = "internal const string Version = \"";
        int at = info.IndexOf(carrier, StringComparison.Ordinal);
        string version = at < 0 ? string.Empty
            : info.Substring(at + carrier.Length, info.IndexOf('"', at + carrier.Length) - at - carrier.Length);
        // 1.19.61: this assertion is the release's own version carrier, so it moves with the patch digit - the four
        // files below are the ones the delivery number has to reach (source, manifest, changelog section, README's
        // current state). Its meaning is unchanged: one number, carried everywhere, never an old number with
        // different code behind it.
        check(version == "1.19.61", "this release's carrier is its own unused patch number: " + version);
        check(ProductionText(Path.Combine("Thunderstore", "manifest.json")).Contains("\"version_number\": \"1.19.61\""),
            "the manifest carries it");
        check(ProductionText(Path.Combine("Thunderstore", "CHANGELOG.md")).Contains("## 1.19.61"),
            "the changelog has this build's own section");
        check(ProductionText("README.md").Contains("源码版本为 **1.19.61**"),
            "and the README's current state names it");
    }

    // ---------- helpers -----------------------------------------------------------------------

    private static bool Near(float left, float right) => Math.Abs(left - right) <= 0.51f;

    private static int Tally(string text, string needle)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(needle)) return 0;
        int count = 0, at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
        return count;
    }

    private static string Segment(string source, string from, string to)
    {
        int at = source.IndexOf(from, StringComparison.Ordinal);
        if (at < 0) return string.Empty;
        int end = source.IndexOf(to, at, StringComparison.Ordinal);
        return end > at ? source.Substring(at, end - at) : source.Substring(at);
    }

    private static string MethodBody(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        if (at < 0) return string.Empty;
        int open = source.IndexOf('{', at);
        if (open < 0) return string.Empty;
        int depth = 0;
        for (int index = open; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0) return source.Substring(open, index - open + 1);
            }
        }
        return string.Empty;
    }

    private static string ProductionText(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj")))
            directory = directory.Parent;
        if (directory == null) throw new InvalidOperationException("the production source root was not found");
        return File.ReadAllText(Path.Combine(directory.FullName, name));
    }
}
