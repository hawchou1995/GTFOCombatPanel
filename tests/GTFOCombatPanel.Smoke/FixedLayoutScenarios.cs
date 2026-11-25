using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// 固定模式（`[Results] FixedLayout`）：默认仍是自适应，打开后字号 = 玩家给的实际像素、位置 = 玩家给的
// 「表格底边中点」比例，并且三个数字**一个都不夹**。
//
// 这个文件钉三件事：
//
//   1. 生产算式（`ResultFixedLayout`）本身：默认值、有效边界（1/240 与 0/1 正好有效、差一点就拒绝）、
//      `left = X·W − w/2` / `top = Y·H − h` 两条减法、拒绝理由的措辞，以及「人数变化 / 分辨率变化时
//      旧网格不会被留着用」——底边中点不动，只有顶边和左边缘跟着长。
//   2. 生产可见性决定（`ResultFixedOverlayPolicy`，以及它调的 `ResultInteractionStripPolicy.OverlapsBoard`）：
//      真实 Survival 页面上那块压住返回大厅按钮的固定板**要画出来**（opacity 0.55），X 错开不算冲突，
//      重叠消失恢复 1.0，扫描证据不足时淡画且原版块保留。
//   3. Unity 侧的接线（源码串检查，注释已剥掉）：`Plugin` 那三个键不再绑 `AcceptableValueRange`；
//      `ResultsDisplay` 的每条真拒绝都恢复原版块并丢掉旧网格；绘制帧先用玩家这次配置校验再量表格；
//      淡画/让位由 `_fixedPlan` 决定；控件相交那一条记录相交的是谁。
//
// 边界说明：本工程没有 Unity / IMGUI / 原生页面，`HudTable.BuildGrid`（`Screen`、`GUIStyle`、字体图集）也进不来，
// 所以这里没有「真的画了几帧」「屏幕上真的没压到按钮」「字体真的装得下」这类结论——那些只能实机看。能钉的是
// 算式、可见性决定、拒绝理由与生产代码里那几行接线确实存在；凡是源码串检查的断言，描述里都写明这不是运行时证明。
internal static partial class Program
{
    // 记录页面的窗口（同一份真机日志的 2560x1440）。
    private const float FixedLayoutWindowWidth = 2560f, FixedLayoutWindowHeight = 1440f;

    // Called explicitly by the ordinary smoke entry point, after its command-line utility modes return.

    private static bool _fixedLayoutScenariosRan;

    internal static void RunFixedLayoutScenarios()
    {
        if (_fixedLayoutScenariosRan) return;
        _fixedLayoutScenariosRan = true;
        Console.WriteLine("---- 固定模式 = 玩家自己的像素字号与底边中点；相交淡画而不是整块消失 ----");
        TheConfiguredNumbersAreValidatedNotClamped(Check);
        TheBottomEdgeMidpointIsTheAnchor(Check);
        APlayerCountOrResolutionChangeDoesNotKeepTheOldGrid(Check);
        TheSurvivalFailurePageDrawsInsteadOfVanishing(Check);
        AnXOffsetAnchorIsNotACollision(Check);
        TheConfiguredGlyphIsTheGlyphThatIsDrawn(Check);
        ThinEvidenceDrawsFaintAndKeepsTheVanillaBlocks(Check);
        ANewConfigurationAttemptGetsItsOwnLine(Check);
        EveryRefusalPutsTheVanillaPageBack(Check);
        TheVanillaBlocksFollowTheSamePlanEverywhere(Check);
        TheUnitySideHonoursTheConfiguredNumbers(Check);
        ATopPinnedThirtyRowBoardKeepsItsCloseOnScreen(Check);
        AFixedPlateWithoutRoomForItsHeaderRefuses(Check);
        TheScrollOffsetSurvivesARefreshAndTheWheelNeedsOverflow(Check);
        Console.WriteLine("FIXED-LAYOUT: PASS (production arithmetic and visibility decision plus static Unity wiring; not a live-game pixel test)");
    }

    // ---- 1: 三个数字的校验与措辞：不夹、说清是哪一项 -------------------------------------------------------
    private static void TheConfiguredNumbersAreValidatedNotClamped(Action<bool, string> check)
    {
        check(ResultFixedLayout.DefaultFontPixels == 18f && ResultFixedLayout.DefaultAnchorX == 0.5f
              && ResultFixedLayout.DefaultAnchorY == 0.8f && ResultFixedLayout.MinimumFontPixels == 1f
              && ResultFixedLayout.MaximumFontPixels == 240f,
            "the shipped defaults are the documented ones: 18px glyph, X=0.5, Y=0.8, glyph range 1..240 (IMGUI integer pixels)");

        check(ResultFixedLayout.Valid(1920f, 1080f, 18f, 0.5f, 0.8f, out string why) && why.Length == 0,
            "1920x1080 with an 18px glyph and (0.5,0.8) is the ordinary valid input, and it answers with no reason at all");

        // 边界要准：正好 1 与正好 240、「正好 0 与正好 1」都有效，不是「小于 1 才拒绝」也不是「大于等于 1 就拒绝」。
        check(ResultFixedLayout.Valid(1920f, 1080f, 1f, 0f, 1f, out why),
            "the boundaries themselves are valid: a 1px glyph with anchors of exactly 0 and exactly 1");
        check(ResultFixedLayout.Valid(1920f, 1080f, 5f, 0.5f, 0.9f, out why),
            "and the value the Survival cfg really carried (FixedFontPixels = 5 at Y=0.9) is a valid request, not a refusal: " + why);

        check(!ResultFixedLayout.Valid(1920f, 1080f, 0.99f, 0.5f, 0.8f, out why)
              && why.Contains(ResultFixedLayout.InputInvalid) && why.Contains("between 1 and 240") && why.Contains("nothing is clamped"),
            "0.01px under the floor is refused by name, with the range and with the promise that nothing was clamped: " + why);
        check(!ResultFixedLayout.Valid(1920f, 1080f, 240.01f, 0.5f, 0.8f, out why) && why.Contains(ResultFixedLayout.InputInvalid),
            "and 0.01px over the ceiling is refused the same way instead of being clamped back to 240");
        check(!ResultFixedLayout.Valid(1920f, 1080f, float.NaN, 0.5f, 0.8f, out why) && why.Contains("not-a-number"),
            "a glyph that is not a number says so as a non-number, never as a zero: " + why);
        check(!ResultFixedLayout.Valid(1920f, 1080f, 18f, -0.001f, 0.8f, out why) && why.Contains("anchorX") && why.Contains(ResultFixedLayout.InputInvalid),
            "an anchorX a thousandth to the left of the window is refused, and the line names anchorX");
        check(!ResultFixedLayout.Valid(1920f, 1080f, 18f, 1.001f, 0.8f, out why) && why.Contains("anchorX"),
            "so is an anchorX a thousandth past the right edge");
        check(!ResultFixedLayout.Valid(1920f, 1080f, 18f, 0.5f, -0.5f, out why) && why.Contains("anchorY"),
            "a negative anchorY is refused, and the line names anchorY");
        check(!ResultFixedLayout.Valid(1920f, 1080f, 18f, 0.5f, 1.5f, out why) && why.Contains("anchorY"),
            "so is an anchorY below the window");
        check(!ResultFixedLayout.Valid(8f, 1080f, 18f, 0.5f, 0.8f, out why) && why.Contains("window=") && why.Contains(ResultFixedLayout.InputInvalid),
            "a window 8px wide is refused as a window, not as a glyph: " + why);
        check(!ResultFixedLayout.Valid(float.NaN, 1080f, 18f, 0.5f, 0.8f, out why) && why.Contains("window="),
            "and a window size nobody could read is refused the same way rather than guessed at");

        // 500 正是「绑了范围就静默夹」的那个数字：旧行为会把它变成 240。越界必须被明确拒绝。
        check(!ResultFixedLayout.Valid(1920f, 1080f, 500f, 0.5f, 0.8f, out why) && why.Contains("must be a number between 1 and 240"),
            "a hand-written 500px glyph is refused with its own range, not quietly rewritten to 240px");
        check(!ResultFixedLayout.Valid(1920f, 1080f, 0f, 0.5f, 0.8f, out why) && why.Contains("must be a number between 1 and 240"),
            "and a zero / negative glyph is refused instead of being lifted to the floor");
        check(ResultFixedLayout.RoundFontPixels(5f) == 5 && ResultFixedLayout.RoundFontPixels(2f) == 2,
            "a hand-written small glyph (5px, 2px) stays that many pixels: the fixed mode shows the size the player asked for");

        check(ResultFixedLayout.RoundFontPixels(17.5f) == 18 && ResultFixedLayout.RoundFontPixels(17.4f) == 17
              && ResultFixedLayout.RoundFontPixels(18f) == 18,
            "the drawn glyph is the configured number rounded to whole pixels (17.5 -> 18, 17.4 -> 17, 18 -> 18)");
        check(ResultFixedLayout.RoundFontPixels(float.NaN) == 18,
            "a glyph nobody can read falls back to the documented 18px, never to a zero-pixel table");

        check(!ResultFixedLayout.TryResolve(1920f, 1080f, 0.5f, 0.8f, 500f, 700f, 400f,
                out ResultFixedLayoutPlan refusedPlan, out why)
              && !refusedPlan.Placed && why.Contains(ResultFixedLayout.InputInvalid),
            "so a 500px request never reaches a plan: the refusal comes first and the plan stays empty (nothing to draw, nothing to clamp)");

        check(ResultFixedLayout.FontNotHonoured == "fixed-layout-font-not-honoured"
              && ResultFixedLayout.InputInvalid == "fixed-layout-input-invalid"
              && ResultFixedLayout.GridUnmeasurable == "fixed-layout-grid-unmeasurable"
              && ResultFixedLayout.OffScreen == "fixed-layout-offscreen"
              && ResultFixedLayout.OverlapsControl == "fixed-layout-overlaps-control"
              && ResultFixedLayout.Placed == "fixed-layout-placed"
              && ResultFixedLayout.EvidenceThin == "fixed-layout-evidence-thin",
            "the words the log and this suite share are stable: four refusals, plus the two outcomes a drawn board can report "
            + "(placed / evidence-thin) - a renamed one would make a real decision unreadable from a player's log");
    }

    // ---- 2: 底边中点就是锚点：两条减法，跑出屏幕就拒绝 -----------------------------------------------
    private static void TheBottomEdgeMidpointIsTheAnchor(Action<bool, string> check)
    {
        ResultFixedLayoutPlan plan;
        string why;
        check(ResultFixedLayout.TryResolve(FixedLayoutWindowWidth, FixedLayoutWindowHeight, 0.5f, 0.8f, 18f, 900f, 600f,
                out plan, out why)
              && near(plan.Rect.X, 830f) && near(plan.Rect.Y, 552f)
              && near(plan.AnchorCenterX, 1280f) && near(plan.AnchorBottom, 1152f) && plan.FontPixels == 18,
            "2560x1440, 18px, (0.5,0.8), a 900x600 table: left = 0.5*2560 - 900/2 = 830, top = 0.8*1440 - 600 = 552, "
            + "and the bottom-edge midpoint is exactly (1280,1152) (got "
            + (plan.Placed ? plan.Rect.X.ToString("0") + "," + plan.Rect.Y.ToString("0") : "no plan") + ")");

        check(ResultFixedLayout.Describe(plan, FixedLayoutWindowWidth, FixedLayoutWindowHeight, "host", "success")
                  .Contains("glyph=18px") && ResultFixedLayout.Describe(plan, FixedLayoutWindowWidth, FixedLayoutWindowHeight, "host", "success")
                  .Contains("anchor=(0.5,0.8)"),
            "the one-per-page line names the glyph it really drew and the two fractions the player gave, so a screenshot and a log can be compared");

        // (0,0) 是**左上角**：底边中点落在那里，整张表在屏幕外 → 拒绝，而不是挪回屏内。
        check(!ResultFixedLayout.TryResolve(FixedLayoutWindowWidth, FixedLayoutWindowHeight, 0f, 0f, 18f, 900f, 600f, out plan, out why)
              && !plan.Placed && why.Contains(ResultFixedLayout.OffScreen),
            "anchor (0,0) is the top-left corner: the bottom-edge midpoint lands there, the table is entirely off-screen, and it is refused rather than moved back inside");
        check(why.Contains("nothing is clamped, rescaled or re-centred"),
            "and the off-screen line says the three things it deliberately did not do: " + why);

        check(ResultFixedLayout.TryResolve(FixedLayoutWindowWidth, FixedLayoutWindowHeight, 0.5f, 1f, 18f, 900f, 600f, out plan, out why)
              && near(plan.AnchorBottom, 1440f),
            "anchor Y=1 puts the bottom edge exactly on the window's bottom edge, and that is allowed (it is the boundary, not past it)");

        check(!ResultFixedLayout.TryResolve(FixedLayoutWindowWidth, FixedLayoutWindowHeight, 1f, 0.8f, 18f, 900f, 600f, out plan, out why)
              && why.Contains(ResultFixedLayout.OffScreen),
            "anchor X=1 puts the bottom-edge MIDPOINT on the right edge, so half the table hangs off the window: refused, not squeezed");

        check(!ResultFixedLayout.TryResolve(FixedLayoutWindowWidth, FixedLayoutWindowHeight, 0.5f, 0.8f, 18f, 3000f, 400f, out plan, out why)
              && why.Contains(ResultFixedLayout.OffScreen),
            "a table wider than the window is refused instead of being rescaled to fit it");

        check(!ResultFixedLayout.TryResolve(FixedLayoutWindowWidth, FixedLayoutWindowHeight, 0.5f, 0.8f, 18f, float.NaN, 600f, out plan, out why)
              && why.Contains(ResultFixedLayout.GridUnmeasurable),
            "a table nobody could measure is refused by its own name: " + why);
        check(!ResultFixedLayout.TryResolve(FixedLayoutWindowWidth, FixedLayoutWindowHeight, 0.5f, 0.8f, 18f, 900f, 0f, out plan, out why)
              && why.Contains(ResultFixedLayout.GridUnmeasurable),
            "and so is a table with no height to draw");

        check(!ResultFixedLayout.TryResolve(1920f, 1080f, 0.5f, 0.8f, 18f, 700f, 900f, out plan, out why)
              && why.Contains(ResultFixedLayout.OffScreen),
            "1080p at Y=0.8 leaves 864px of room, so a 900px-tall table is refused (its top would be at -36)");
    }

    // ---- 3: 人数与分辨率变化：底边中点不动，旧网格不留 ------------------------------
    private static void APlayerCountOrResolutionChangeDoesNotKeepTheOldGrid(Action<bool, string> check)
    {
        ResultFixedLayoutPlan two = ResultFixedLayoutPlan.None, four = ResultFixedLayoutPlan.None;
        string why;
        bool twoOk = ResultFixedLayout.TryResolve(FixedLayoutWindowWidth, FixedLayoutWindowHeight, 0.5f, 0.8f, 18f, 700f, 520f, out two, out why);
        bool fourOk = ResultFixedLayout.TryResolve(FixedLayoutWindowWidth, FixedLayoutWindowHeight, 0.5f, 0.8f, 18f, 940f, 600f, out four, out why);
        bool both = twoOk && fourOk;
        check(both && near(two.AnchorCenterX, four.AnchorCenterX) && near(two.AnchorBottom, four.AnchorBottom),
            "two players and four players on the same page put their bottom-edge midpoint on the very same point ("
            + (both ? two.AnchorCenterX.ToString("0") + "," + two.AnchorBottom.ToString("0") : "no plan") + ")");
        check(both && four.Rect.Width > two.Rect.Width && four.Rect.Height > two.Rect.Height && four.Rect.Y < two.Rect.Y,
            "and the fourth player's table is wider and taller, with the extra height taken from the top: "
            + (both ? two.Rect.Width.ToString("0") + "x" + two.Rect.Height.ToString("0") + " at y=" + two.Rect.Y.ToString("0")
                + " -> " + four.Rect.Width.ToString("0") + "x" + four.Rect.Height.ToString("0") + " at y=" + four.Rect.Y.ToString("0") : "no plan"));
        check(both && !near(two.Rect.Width, four.Rect.Width) && !near(two.Rect.Y, four.Rect.Y),
            "which is the whole point: the old grid's size and its old top are not reused, only the anchor survives the change");

        ResultFixedLayoutPlan fullHd = ResultFixedLayoutPlan.None, uhd = ResultFixedLayoutPlan.None;
        bool fullHdOk = ResultFixedLayout.TryResolve(1920f, 1080f, 0.5f, 0.8f, 18f, 700f, 520f, out fullHd, out why);
        bool uhdOk = ResultFixedLayout.TryResolve(3840f, 2160f, 0.5f, 0.8f, 18f, 700f, 520f, out uhd, out why);
        bool resolutions = fullHdOk && uhdOk;
        check(resolutions && near(fullHd.Rect.Width, uhd.Rect.Width) && near(fullHd.Rect.Height, uhd.Rect.Height),
            "the same 18px table is the same size on 1080p and on 4K: the glyph is real pixels, so nothing is scaled with the window");
        check(resolutions && near(fullHd.AnchorCenterX, 960f) && near(uhd.AnchorCenterX, 1920f)
              && near(fullHd.AnchorBottom, 864f) && near(uhd.AnchorBottom, 1728f),
            "and the anchor is a fraction of each window: (960,864) on 1080p, (1920,1728) on 4K");

        ResultFixedLayoutPlan bigger = ResultFixedLayoutPlan.None;
        check(ResultFixedLayout.TryResolve(FixedLayoutWindowHeight > 0f ? FixedLayoutWindowWidth : 0f, FixedLayoutWindowHeight,
                0.5f, 0.8f, 24f, 1200f, 800f, out bigger, out why) && bigger.FontPixels == 24
              && near(bigger.AnchorBottom, 1152f) && both && bigger.Rect.Y < four.Rect.Y,
            "a 24px table on the same window grows further upward from the same bottom edge (y="
            + (bigger.Placed ? bigger.Rect.Y.ToString("0") : "no plan") + "), it does not re-centre");
    }

    // ---- 4: 每条拒绝都把原版页面放回去（源码串检查）-------------------------------------------------------
    private static void EveryRefusalPutsTheVanillaPageBack(Action<bool, string> check)
    {
        string root = FixedLayoutSourceRoot();
        check(root != null, "the fixed-layout regression locates the production sources from " + AppContext.BaseDirectory);
        if (root == null) return;
        string display = FixedLayoutCode(FixedLayoutSource(root, "ResultsDisplay.cs"));

        string abandon = FixedLayoutMethodBody(display, "private static void AbandonFixedLayout()");
        check(abandon != null && abandon.Contains("RestoreHidden();"),
            "the shared tail of a fixed-mode refusal puts the vanilla blocks back (static source check, not a runtime proof): "
            + "a refusal that left them hidden would leave the page with no board AND no report cards");
        check(abandon != null && abandon.Contains("_grid = null") && abandon.Contains("_content = null"),
            "and it drops the grid, so the next frame cannot draw the previous player count's table at the previous size");

        string draw = FixedLayoutMethodBody(display, "private static void FixedLayoutDraw()");
        check(draw != null && FixedLayoutCount(draw, "AbandonFixedLayout();") >= 3,
            "invalid geometry and vanished pages discard their fixed grid (static wiring)");
        // 这一帧先校验玩家**现在**的配置，再量表格：热重载写进来的非数字/越界不会再被四舍五入成"能画的值"。
        int validated = draw != null ? draw.IndexOf("ResultFixedLayout.Valid(Screen.width, Screen.height, configured", StringComparison.Ordinal) : -1;
        int rounded = draw != null ? draw.IndexOf("ResultFixedLayout.RoundFontPixels(configured)", StringComparison.Ordinal) : -1;
        check(validated >= 0 && rounded > validated,
            "the draw frame validates the configured numbers before it rounds them, so a hot-reloaded bad value is refused by name on that frame");
        check(draw != null && draw.Contains("HudTable.Draw(grid, rect.X, rect.Y, _content, draw.Opacity, _scrollFirst, _scrollVisible, _scrollPlate)"),
            "and it draws its scroll window at the opacity the visibility decision gave it, not at a hard-coded 1.0");
        int tableAt = draw != null ? draw.IndexOf("HudTable.Draw(grid, rect.X, rect.Y, _content, draw.Opacity, _scrollFirst, _scrollVisible, _scrollPlate)", StringComparison.Ordinal) : -1;
        int hideAt = draw != null ? draw.IndexOf("if (draw.HidesVanilla)", tableAt >= 0 ? tableAt : 0, StringComparison.Ordinal) : -1;
        check(draw != null && draw.Contains("if (!draw.HidesVanilla) RestoreHidden();")
              && tableAt >= 0 && hideAt > tableAt,
            "thin evidence puts the game's own blocks back before anything is drawn, and the board takes their place only after this frame's `HudTable.Draw` (1.19.47 review #3: a frame that returns earlier leaves no hole)");
        check(draw != null && draw.Contains("ResultFixedDrawPlan draw = FixedLayoutDecision(rect, out string evidence);"),
            "and the decision is taken again on every drawn frame, so a control that moved under the board changes the opacity rather than the presence of the board");

        string place = FixedLayoutMethodBody(display, "private static bool PlaceFixedLayout(TableContent content");
        check(place != null && FixedLayoutCount(place, "AbandonFixedLayout();") == 4,
            "every refusal that can still happen (invalid input, unmeasurable grid, no vertical room for header + rows, off-screen rect) restores the page and drops the grid: "
            + (place == null ? "no body" : FixedLayoutCount(place, "AbandonFixedLayout();") + " call(s)"));
        check(place != null && place.Contains("TryFixedPlate(grid, anchorY, out int fixedRows, out float plateHeight, out string plateWhy)"),
            "and the plate is measured after the header band is reserved, so a page without room for it refuses instead of drawing one off the screen");
        check(place != null && place.Contains("ResultFixedLayout.Valid(Screen.width, Screen.height, font, anchorX, anchorY, out refusal)"),
            "with the range bindings gone from the config file, the one place the numbers are judged is still this explicit valid call");
        check(place != null && !place.Contains("FixedLayoutStillSafe") && place.Contains("FixedLayoutDecision(plan.Rect, out string evidence)"),
            "and a real intersection is no longer a refusal: placement asks the decision for an opacity instead of a yes/no safety verdict");
        check(place != null && place.Contains("_fixedPlaced = true;") && place.Contains("_placed = true;")
              && place.Contains("_placedSucceeded = succeeded;"),
            "the fixed placement still records its placement and page kind, so the refresh path stays reachable (the 1.19.45 review finding)");
    }

    // ---- 5: Unity 侧接线：不绑范围、不读缩放/偏移、表格是配置的像素、成功页也交等待文字 --------------------
    private static void TheUnitySideHonoursTheConfiguredNumbers(Action<bool, string> check)
    {
        string root = FixedLayoutSourceRoot();
        if (root == null) { check(false, "the fixed-layout regression needs the production sources"); return; }
        string display = FixedLayoutCode(FixedLayoutSource(root, "ResultsDisplay.cs"));
        string plugin = FixedLayoutCode(FixedLayoutSource(root, "Plugin.cs"));
        string panel = FixedLayoutCode(FixedLayoutSource(root, "PanelBehaviour.cs"));
        string readme = FixedLayoutSource(root, "README.md");

        // A1: 三个键都不绑范围。绑了就是「说明写不夹、实际静默夹」的那个已证缺陷。
        int first = plugin.IndexOf("FixedFontPixelsConfig = Config.Bind(", StringComparison.Ordinal);
        int end = first >= 0 ? plugin.IndexOf("SuccessFontScale = Config.Bind(", first, StringComparison.Ordinal) : -1;
        string keys = first >= 0 && end > first ? plugin.Substring(first, end - first) : string.Empty;
        check(keys.Contains("FixedFontPixels") && keys.Contains("FixedAnchorX") && keys.Contains("FixedAnchorY"),
            "the three fixed-layout keys are still bound in [Results] (static source check)");
        check(!keys.Contains("AcceptableValueRange"),
            "and none of the three binds AcceptableValueRange: that layer silently rewrote a hand-written 500 to 240 and a 2 to 6 "
            + "while the description promised the value was taken as written");
        check(keys.Contains("不夹"),
            "the descriptions still promise the numbers are not clamped, which is now literally true (the refusal is fixed-layout-input-invalid)");
        check(plugin.Contains("FixedLayoutConfig = Config.Bind(\"Results\", \"FixedLayout\", false,"),
            "FixedLayout itself is still a plain bool that defaults to false, so the adaptive chain stays the shipped behaviour");

        // A2: 固定模式不读自动缩放/偏移，也不等页面的条带。
        check(display.Contains("if (!fixedLayout) strict = Placement(page, succeeded"),
            "the adaptive placement is not even called in fixed mode: the page's band, its walls and its settle gates do not decide anything here");
        int placeStart = display.IndexOf("private static bool PlaceFixedLayout(TableContent content", StringComparison.Ordinal);
        check(placeStart >= 0 && !display.Substring(placeStart, 1200).Contains("OffsetX") && !display.Substring(placeStart, 1200).Contains("OffsetY"),
            "and the fixed placement never reads [Results] OffsetX/OffsetY: the anchor is the player's own position, not an offset from the page's");
        check(placeStart >= 0 && !display.Substring(placeStart, 1200).Contains("FontScale")
              && !display.Substring(placeStart, 1200).Contains("ResultsZoom") && !display.Substring(placeStart, 1200).Contains("FitBoard"),
            "nor the two wheel scales and the strip fit: the glyph is the configured pixel, so there is no multiplier and no shrink to apply");

        // A3: 表格在配置的字号下重量，并且验回来的字形就是那个像素。
        string grid = FixedLayoutMethodBody(display, "private static PanelGrid FixedGrid(TableContent content");
        check(grid != null && grid.Contains("float.PositiveInfinity, float.PositiveInfinity, pixels"),
            "the fixed grid is measured in an infinite box, so PanelGrid.FitFactor cannot shrink it: the table is exactly its own size at that glyph");
        check(grid != null && grid.Contains("grid.FontPixels") && grid.Contains("ResultFixedLayout.FontNotHonoured"),
            "and the drawn glyph is checked against the configured one - a build that ever shrinks it refuses by name instead of drawing a smaller table");
        check(display.Contains("if (!_fixedPlaced && (board.Rows != _grid.Rows || board.ColumnCount != _grid.Columns)) return false;")
              && display.Contains("if (_fixedPlaced) return FixedLayoutRefresh(content, board, report, succeeded);"),
            "a refreshed snapshot in fixed mode re-measures and re-anchors (players, columns, wider content) instead of being refused for changing its row count");

        string draw = FixedLayoutMethodBody(display, "private static void FixedLayoutDraw()");
        check(draw != null && draw.Contains("ResultFixedLayout.TryResolve(Screen.width, Screen.height, anchorX, anchorY, configured,"),
            "the drawn frame recomputes the rectangle from the live window and the live anchors, so a resolution change or a hot-reloaded anchor is correct on that frame");
        check(draw != null && draw.Contains("Math.Abs(grid.FontPixels - pixels)"),
            "and it re-measures the table only when the configured glyph really changed, so a hot-reloaded FixedFontPixels takes effect the same frame");

        // A4: 两个页面的等待文字都按受保护文字交给扫描；相交的那一条记录「相交的是谁」。
        string safe = FixedLayoutMethodBody(display, "private static ResultFixedDrawPlan FixedLayoutDecision(LayoutRect rect, out string evidence)");
        check(safe != null && safe.Contains("Component waiting = FixedFooterText(_page);")
              && safe.Contains("ScanFooterControls(_page, rect.Y, MessageGap, waiting, rect, inspectBoard: true)"),
            "the fixed visibility decision hands the page's own footer text to the scan instead of scanning without it (static source check)");
        check(safe != null && safe.Contains("ResultFixedOverlayPolicy.Decide(")
              && safe.Contains("controls.Complete && controls.ParentCoverageComplete && controls.Unmeasurable == 0")
              && safe.Contains("scanError="),
            "and it asks the production policy with the scan's own proof, and treats a scan that threw as thin evidence rather than as a refusal");
        string footer = FixedLayoutMethodBody(display, "private static Component FixedFooterText(Component page)");
        check(footer != null && footer.Contains("m_returnToLobby_text"),
            "on the failure page that text is m_returnToLobby_text, the very object the adaptive chain has always measured");
        check(footer != null && footer.Contains("m_btnLeaveExpedition") && footer.Contains("GetComponentsInChildren<TextMeshPro>(true)")
              && footer.Contains("ResultFooterTextPolicy.Counts"),
            "on the success page it is the TextMeshPro the page is really drawing inside m_btnLeaveExpedition's subtree - "
            + "the same object FooterTextTop measures, and only while it is switched on and visible");
        string scan = FixedLayoutMethodBody(display, "private static FooterControls ScanFooterControls(Component page, float anchorBottom, float gap,");
        check(scan != null && scan.Contains("protectedPageText"),
            "the scan accepts measured protected text separately; its TextMeshPro identity is not the parent button identity");
        check(scan != null && scan.Contains("inspectBoard && ResultInteractionStripPolicy.OverlapsBoard(rect, board, ResultBoardHold.ButtonClearance)"),
            "and the one overlap test every real control keeps is still the 2D one-liner it always was (X and Y, with the clearance), not a Y-only strip test");
        check(scan != null && scan.Contains("blocker = Blocker(control, rect, pressable: true);")
              && scan.Contains("blocker = Blocker(protectedPageText, rect, pressable: false);"),
            "the intersecting control and the intersecting page text are both recorded by object name and component type, which is what the log line reads");
        check(display.Contains("public readonly ResultFixedBlocker Blocker;") && display.Contains("private static ResultFixedBlocker Blocker(Component control, LayoutRect rect, bool pressable)"),
            "the scan carries that evidence out in its result instead of only a bool, so the caller can name the control");
        check(scan != null && scan.Contains("if (protectedPageText != null)") && !scan.Contains("protectedPointer"),
            "footer text is measured independently without a dead label-versus-button pointer comparison");
        check(scan != null && scan.Contains("actions.Add(new ResultActionBounds(rect, protectedPageText: true));"),
            "protected text bounds are recorded separately; actual parent buttons retain interaction collision checks");

        // A4b: 这块板不接管鼠标：没有任何点击消费，F8 那一行带上淡淡与原版块的结果。
        //
        // 1.19.55：`Event.current` 现在只在一个地方被读——每页/每窗口一次的 `settlement space` 观测。它读的是引擎交给本
        // 次 repaint 的鼠标位置，**不读** `type`、不读按键、不调用 `Use()`，所以一块画出来的板照样不消费任何点击：命中仍然
        // 只来自 `ResultPointerHit.Inside` 对"本次 repaint 真的画过的矩形"的比较。
        string displayCode = FixedLayoutCode(display);
        string spaceObservation = FixedLayoutMethodBody(display,
            "private static void ReportPointerSpace(Component page, ResultChromeMetrics metrics)");
        int eventReads = FixedLayoutCount(displayCode, "Event.current");
        int eventReadsInObservation = spaceObservation != null
            ? FixedLayoutCount(FixedLayoutCode(spaceObservation), "Event.current") : 0;
        check(eventReads > 0 && eventReads == eventReadsInObservation
              && !displayCode.Contains("Event.current.type") && !displayCode.Contains("Event.current.Use(")
              && !displayCode.Contains("EventType.Mouse"),
            "1.19.55: the settlement display reads an IMGUI event in exactly one place - the bounded `settlement space` observation, its mouse position only - and never its type, never a button and never `Use()`, so a drawn board consumes no mouse click");
        check(!panel.Contains("EventType.MouseDown") && !panel.Contains("EventType.MouseUp"),
            "and the overlay window consumes no mouse button either - only the wheel, which fixed mode keeps for itself");
        check(display.Contains("opacity={_fixedPlan.Opacity:0.##}") && display.Contains("overlay={_fixedPlan.Reason}")
              && display.Contains("vanillaBlocks={(_fixedPlan.HidesVanilla ? \"put away\" : \"kept\")}"),
            "the F8 answer for a fixed board carries the opacity, the reason and whether the vanilla blocks were put away");
        string heldBack = FixedLayoutMethodBody(display, "private static void ReportHeldBack(string where, string reason, string detail, string signature = null)");
        check(heldBack != null && heldBack.Contains("if (!string.IsNullOrEmpty(signature)) key += \"|\" + signature;"),
            "the bounded refusal line takes the configuration attempt as part of its key, so a new glyph or anchor is a new line instead of silence");
        check(display.Contains("\"fixed-layout|\"") && display.Contains("ResultFixedLayout.Attempt("),
            "and the drawn-board line is keyed the same way (fixed rect, glyph, anchor, intersecting control)");

        // A5（新版契约）：滚轮只滚动，不写任何配置；固定模式的板和自适应一样走这条滚动路径。
        // 1.19.59: the wheel is settled on the frame (`ResultsDisplay.PollWheel()`) from the page's own `ScrollInput` first and
        // the physical wheel as the fallback - the routing the player's other panel uses - and it no longer consumes an IMGUI
        // event: the page keeps its own reading, and this mod only moves its own table.
        check(panel.Contains("ResultsDisplay.PollWheel();") && !panel.Contains("ResultsScrollEvent"),
            "the frame driver settles the wheel, and `PanelBehaviour` no longer reads an IMGUI scroll event");
        check(!panel.Contains("PersistResultFontScale") && !display.Contains("PersistResultFontScale")
              && !display.Contains("FixedFontPixels ="),
            "the wheel writes nothing: a scroll never touches [Results] FixedFontPixels, the anchors, or the manual multipliers");
        check(display.Contains("FixedPlateRows(") && display.Contains("ResultsScroll.PlateHeight("),
            "the fixed layout resolves a plate of whole rows inside the window, so its close button and frozen header stay on screen");

        // A6: README 有四个键的示例与两条边界说明。
        check(readme.Contains("FixedLayout = false") && readme.Contains("FixedFontPixels = 18")
              && readme.Contains("FixedAnchorX = 0.5") && readme.Contains("FixedAnchorY = 0.8"),
            "the README's [Results] block carries the four keys with the documented defaults");
        check(readme.Contains("左上角是 (0,0)") && readme.Contains("底边中点"),
            "and says the coordinates are window fractions measured from the top-left corner, with the bottom-edge midpoint on the anchor");
        check(readme.Contains("`1`–`240`") && readme.Contains("actualglyph=5px"),
            "and the documented glyph range is 1..240 with 5px named as a value that is drawn, not refused");
        check(readme.Contains("0.55") && readme.Contains("真实二维矩形相交") && readme.Contains("证据不足"),
            "and it still documents the drawn-but-faint rule: a real 2D intersection draws the board at 0.55, thin evidence keeps the vanilla blocks");
        check(readme.Contains("不会被夹") && readme.Contains("fixed-layout-offscreen"),
            "and that the numbers are not clamped: a table that runs off the screen is refused by name");
        check(readme.Contains("不保证任意字体都装得下"),
            "and it does not promise that every font fits: the columns are measured from the real font, so a wider face can push the table off-screen");
    }

    // ---- 6: 真实 Survival 失败页：固定板不再被「页面上有个按钮」整块吞掉 ---------------------------------------
    //
    // 数字取自 2026-11-15 23:50 那次 Survival 运行（2560x1440、FixedAnchorY=0.9，cfg 后来改成 FixedFontPixels=5）：
    // 固定板底边 = 0.9 × 1440 = 1296，页面真正可按的那颗返回大厅按钮上沿 = 1145。旧判定把这当成「遮住按钮」→
    // 20 次拒绘；现在同一次尝试必须画出来，只是淡到 0.55。这个用例驱动的是生产算式（`TryResolve`）、生产碰撞
    // （`ResultInteractionStripPolicy.OverlapsBoard`）和生产决定（`ResultFixedOverlayPolicy.Decide`），不是另写一份 if。
    private static void TheSurvivalFailurePageDrawsInsteadOfVanishing(Action<bool, string> check)
    {
        ResultFixedLayoutPlan plan;
        string why;
        // 表格尺寸取自同一次运行的量级（5px 字形、四列的固定看板），这里只是为了驱动算式：断言从它算出的矩形出发。
        bool resolved = ResultFixedLayout.TryResolve(FixedLayoutWindowWidth, FixedLayoutWindowHeight, 0.5f, 0.9f, 5f,
            240f, 120f, out plan, out why);
        check(resolved && plan.FontPixels == 5 && near(plan.AnchorBottom, 1296f) && near(plan.AnchorCenterX, 1280f),
            "Survival 1440p at Y=0.9 with the player's 5px glyph resolves: bottom-edge midpoint (1280,1296), glyph 5px, "
            + "nothing clamped (" + (resolved ? plan.FontPixels + "px at " + plan.Rect.X.ToString("0") + "," + plan.Rect.Y.ToString("0") : why) + ")");

        LayoutRect lobby = new LayoutRect(1120f, 1145f, 320f, 44f);  // the recorded pressableTop = 1145
        bool overlaps = ResultInteractionStripPolicy.OverlapsBoard(lobby, plan.Rect, ResultBoardHold.ButtonClearance);
        ResultFixedBlocker blocker = new ResultFixedBlocker(lobby, "returnToLobby", "CM_Item", true);
        ResultFixedDrawPlan draw = ResultFixedOverlayPolicy.Decide(true, overlaps, blocker,
            "complete=true,unmeasurableControls=0", out why);
        check(overlaps && draw.Draws && near(draw.Opacity, 0.55f) && draw.Reason == ResultFixedLayout.OverlapsControl,
            "that page really intersects (board 1176..1296 over the button at 1145) and the answer is a drawn board at "
            + "opacity 0.55, never a refusal: " + draw.Reason + "; " + why);
        check(!draw.HidesVanilla,
            "and a complete scan that REALLY intersects still keeps the vanilla blocks: the faint board sits over them, so the "
            + "page stays readable and the button stays pressable (HidesVanilla = Draws && Proven && !Overlap)");
        string line = draw.Describe(plan, FixedLayoutWindowWidth, FixedLayoutWindowHeight, "host", "failure");
        check(line.Contains("rect=") && line.Contains("glyph=5px") && line.Contains("anchor=(0.5,0.9)")
              && line.Contains("CM_Item") && line.Contains("returnToLobby") && line.Contains("opacity=0.55")
              && line.Contains("1176") && line.Contains("1296"),
            "the one line that page gets names the fixed rectangle, the real 5px glyph, the anchor, the control it "
            + "intersects by type and name, and the opacity it was drawn at");
        check(line.Contains("vanilla blocks are kept"),
            "and it says out loud that the vanilla blocks were kept under the faint board: " + line);
    }

    // ---- 7: 碰撞是真实二维矩形，X 错开就不算冲突 ------------------------------------------------
    private static void AnXOffsetAnchorIsNotACollision(Action<bool, string> check)
    {
        ResultFixedLayoutPlan plan;
        string why;
        bool resolved = ResultFixedLayout.TryResolve(FixedLayoutWindowWidth, FixedLayoutWindowHeight, 0.2f, 0.9f, 5f,
            240f, 120f, out plan, out why);
        LayoutRect lobby = new LayoutRect(1120f, 1145f, 320f, 44f);
        bool overlaps = ResultInteractionStripPolicy.OverlapsBoard(lobby, plan.Rect, ResultBoardHold.ButtonClearance);
        ResultFixedDrawPlan draw = ResultFixedOverlayPolicy.Decide(true, overlaps, ResultFixedBlocker.None,
            "complete=true", out why);
        check(resolved && near(plan.Rect.X, 392f) && !overlaps && near(draw.Opacity, 1f)
              && draw.Reason == ResultFixedLayout.Placed && draw.HidesVanilla,
            "the same board at X=0.2 sits at 392..632 and does not touch the button at 1120..1440: the test is a 2D "
            + "rectangle intersection, an X offset really is no conflict, and the board is drawn at opacity "
            + draw.Opacity.ToString("0.##"));

        // 余量是竖直方向的那 6px：控件只要离板底不到 clearance 就算相交，离得更远就不算（板取水平居中的那份矩形）。
        ResultFixedLayoutPlan centred;
        ResultFixedLayout.TryResolve(FixedLayoutWindowWidth, FixedLayoutWindowHeight, 0.5f, 0.9f, 5f, 240f, 120f,
            out centred, out why);
        LayoutRect justBelow = new LayoutRect(1160f, 1300f, 240f, 30f);
        LayoutRect furtherBelow = new LayoutRect(1160f, 1310f, 240f, 30f);
        check(ResultInteractionStripPolicy.OverlapsBoard(justBelow, centred.Rect, ResultBoardHold.ButtonClearance),
            "a control 4px under the board's bottom edge is inside the documented clearance and counts as an intersection");
        check(!ResultInteractionStripPolicy.OverlapsBoard(furtherBelow, centred.Rect, ResultBoardHold.ButtonClearance),
            "a control 14px under it is not, so the clearance is the documented 6px and not an unbounded strip");
    }

    // ---- 8: 玩家写 5 就是 5px：生产网格不夹字、不塌行 --------------------------------------------------
    private static void TheConfiguredGlyphIsTheGlyphThatIsDrawn(Action<bool, string> check)
    {
        string why;
        check(ResultFixedLayout.Valid(FixedLayoutWindowWidth, FixedLayoutWindowHeight, 5f, 0.5f, 0.9f, out why)
              && ResultFixedLayout.RoundFontPixels(5f) == 5 && ResultFixedLayout.RoundFontPixels(1f) == 1,
            "5px (and the 1px floor) are valid configured glyphs and stay exactly those pixels instead of being lifted "
            + "to a 6px readability floor that refuses the whole board");
        check(!ResultFixedLayout.Valid(FixedLayoutWindowWidth, FixedLayoutWindowHeight, 0f, 0.5f, 0.9f, out why)
              && why.Contains(ResultFixedLayout.InputInvalid) && why.Contains("between 1 and 240"),
            "a non-positive glyph is still refused by name: " + why);

        // `HudTable.BuildGrid` 真正调的就是 `PanelGeometry.Build`：5px 进来、5px 出去，行高与宽度都还成立。
        var metrics = new[] { new PanelColumnMetrics(new[] { 60f, 30f }), new PanelColumnMetrics(new[] { 30f }) };
        PanelGrid grid = PanelGeometry.Build(metrics, PanelGeometry.FieldGap(5, PanelDensity.Normal), 5f, 12,
            PanelDensity.Normal);
        check(grid != null && near(grid.FontPixels, 5f) && grid.Height > 0f && grid.Width > 0f,
            "the production grid builder keeps a 5px glyph at 5px and still produces a drawable table (glyph="
            + (grid != null ? grid.FontPixels.ToString("0.##") + ", w=" + grid.Width.ToString("0.##")
                + ", h=" + grid.Height.ToString("0.##") : "null") + ")");
        check(grid != null && grid.FitFactor(float.PositiveInfinity, float.PositiveInfinity) == 1f,
            "and in the infinite box the fixed mode measures in, FitFactor is exactly 1: no whole-table shrink");
    }

    // ---- 9: 扫描证据不足时淡画但保留原版块（不声称无冲突）---------------------------------------------
    private static void ThinEvidenceDrawsFaintAndKeepsTheVanillaBlocks(Action<bool, string> check)
    {
        string why;
        ResultFixedDrawPlan thin = ResultFixedOverlayPolicy.Decide(false, false, ResultFixedBlocker.None,
            "complete=false,scanReason=registry-unreadable", out why);
        check(thin.Draws && near(thin.Opacity, 0.55f) && !thin.HidesVanilla
              && thin.Reason == ResultFixedLayout.EvidenceThin,
            "a scan that cannot see the whole page still draws the board, faint, and keeps the vanilla blocks: " + why);
        string line = thin.Describe(new ResultFixedLayoutPlan(new LayoutRect(1160f, 1176f, 240f, 120f), 5, 0.5f, 0.9f),
            FixedLayoutWindowWidth, FixedLayoutWindowHeight, "host", "failure");
        check(line.Contains("scan=incomplete") && line.Contains("does not claim there is no conflict"),
            "and the line says the evidence is thin and that it does not claim the page is clear");

        LayoutRect lobby = new LayoutRect(1120f, 1145f, 320f, 44f);
        ResultFixedDrawPlan unprovenOverlap = ResultFixedOverlayPolicy.Decide(false, true,
            new ResultFixedBlocker(lobby, "returnToLobby", "CM_Item", true), "complete=false", out why);
        check(unprovenOverlap.Draws && near(unprovenOverlap.Opacity, 0.55f) && !unprovenOverlap.HidesVanilla
              && unprovenOverlap.Reason == ResultFixedLayout.OverlapsControl,
            "thin evidence plus a real intersection is both: faint, and the vanilla blocks stay");

        ResultFixedDrawPlan recovered = ResultFixedOverlayPolicy.Decide(true, false, ResultFixedBlocker.None,
            "complete=true", out why);
        check(recovered.Draws && near(recovered.Opacity, 1f) && recovered.HidesVanilla
              && recovered.Reason == ResultFixedLayout.Placed,
            "and once the control is gone and the scan is complete the board is back to full opacity and replaces the "
            + "vanilla blocks again");
    }

    // ---- 10: 日志限频按「这次配置尝试」：改过的参数必须能再说一遍 -----------------------------------------
    private static void ANewConfigurationAttemptGetsItsOwnLine(Action<bool, string> check)
    {
        var reports = new HashSet<string>();
        string Prefix = "held-back-prepare-fixed-layout-refused|";
        string five = Prefix + ResultFixedLayout.Attempt(5f, 0.5f, 0.9f, 2560f, 1440f, 0f, 0f);
        string twentyFour = Prefix + ResultFixedLayout.Attempt(24f, 0.5f, 0.9f, 2560f, 1440f, 0f, 0f);
        check(ResultReportPolicy.OncePerPage(reports, five, false, false),
            "the first refusal of a configuration attempt prints");
        check(!ResultReportPolicy.OncePerPage(reports, five, false, false),
            "the same attempt on the next repaint stays silent, so a per-frame repaint cannot turn it into a flood");
        check(ResultReportPolicy.OncePerPage(reports, twentyFour, false, false),
            "a new glyph is a new attempt and gets its own line: the old per-page key swallowed every later change");
        check(ResultReportPolicy.OncePerPage(reports, five, true, true),
            "and an F8 press still gets its one answer for the attempt it is on");
        check(!ResultReportPolicy.OncePerPage(reports, five, true, false),
            "without the press flag it is back on the ordinary once-per-attempt latch");
        check(ResultReportPolicy.OncePerPage(reports,
                Prefix + ResultFixedLayout.Attempt(5f, 0.5f, 0.9f, 1920f, 1080f, 0f, 0f), false, false),
            "a resolution change is a new attempt too");

        // 板自己的尺寸在键里（人数变化 → 新行），但相交控件的像素不在：1145 -> 1151 的抖动不该刷屏。
        ResultFixedDrawPlan draw = ResultFixedOverlayPolicy.Decide(true, true,
            new ResultFixedBlocker(new LayoutRect(1120f, 1145f, 320f, 44f), "returnToLobby", "CM_Item", true), "", out _);
        ResultFixedDrawPlan jittered = ResultFixedOverlayPolicy.Decide(true, true,
            new ResultFixedBlocker(new LayoutRect(1120f, 1151f, 320f, 44f), "returnToLobby", "CM_Item", true), "", out _);
        check(draw.Signature == jittered.Signature,
            "the intersecting control's own pixels drifting (1145 -> 1151 in the recorded log) does not mint a new line: "
            + "the key carries identity, not jitter");
        check(draw.Signature.Contains("returnToLobby") && draw.Signature.Contains("opacity=0.55"),
            "and the key names the control and the opacity, so a different blocker is a different line");
    }

    // ---- 11: 原版块的唯一开关：固定模式下「让位」只由 `_fixedPlan` 决定（M1 策略 + M2 接线）------------------
    //
    // M1：`HidesVanilla` 少了 `!Overlap`，于是"扫描完整但真实相交"（正是用户当前的 5px / 0.5 / 0.9）会把原版块挪走，
    //     与 README 的"完整 **且** 无重叠才让位"相反。
    // M2：`Refresh` 在 `Prepare` 之后按 `_placed` 就把原版块隐藏，绕过 `_fixedPlan`——淡画帧每帧隐藏/恢复，页面在 `Draw`
    //     之前失活时还会留下空洞页面。
    // 这里既钉策略三态，也钉"预绘制、自适应绘制、固定绘制"三条入口都不绕过计划（源码串检查，不是运行时证明）。
    private static void TheVanillaBlocksFollowTheSamePlanEverywhere(Action<bool, string> check)
    {
        string root = FixedLayoutSourceRoot();
        if (root == null) { check(false, "the fixed-layout regression needs the production sources"); return; }
        string display = FixedLayoutCode(FixedLayoutSource(root, "ResultsDisplay.cs"));

        string why;
        ResultFixedDrawPlan overlap = ResultFixedOverlayPolicy.Decide(true, true,
            new ResultFixedBlocker(new LayoutRect(1120f, 1145f, 320f, 44f), "returnToLobby", "CM_Item", true), "", out why);
        ResultFixedDrawPlan thin = ResultFixedOverlayPolicy.Decide(false, false, ResultFixedBlocker.None, "", out why);
        ResultFixedDrawPlan clean = ResultFixedOverlayPolicy.Decide(true, false, ResultFixedBlocker.None, "", out why);
        check(overlap.Draws && near(overlap.Opacity, 0.55f) && !overlap.HidesVanilla,
            "M1: complete scan + real intersection -> drawn at 0.55 with the vanilla blocks KEPT (HidesVanilla = Draws && Proven && !Overlap)");
        check(thin.Draws && near(thin.Opacity, 0.55f) && !thin.HidesVanilla,
            "unknown scan -> drawn at 0.55 with the vanilla blocks kept");
        check(clean.Draws && near(clean.Opacity, 1f) && clean.HidesVanilla,
            "complete scan + no intersection -> 1.0 and the board may take the vanilla blocks' place");
        check(overlap.Describe(new ResultFixedLayoutPlan(new LayoutRect(1160f, 1176f, 240f, 120f), 5, 0.5f, 0.9f),
                FixedLayoutWindowWidth, FixedLayoutWindowHeight, "host", "failure").Contains("vanilla blocks are kept"),
            "and the diagnostic for the intersecting frame says the blocks were kept, not put away");

        // 证据不足优先于"相交且证明完整"的措辞（review 的日志残项）：`Decide(false,true,…)` 这一帧板照画、0.55、原版块保留，
        // 但这一行不能出现 `scan=complete`（它与同行的 readback complete=false 自相矛盾）。
        ResultFixedDrawPlan thinOverlap = ResultFixedOverlayPolicy.Decide(false, true,
            new ResultFixedBlocker(new LayoutRect(1120f, 1145f, 320f, 44f), "returnToLobby", "CM_Item", true),
            "complete=false,parentCoverage=true,unmeasurableControls=0,scanReason=registry-unreadable", out why);
        check(thinOverlap.Draws && thinOverlap.Overlap && !thinOverlap.Proven
              && near(thinOverlap.Opacity, 0.55f) && !thinOverlap.HidesVanilla,
            "thin evidence + real intersection still draws at 0.55 with the vanilla blocks kept: the wording fix changes no decision");
        string thinOverlapLine = thinOverlap.Describe(
            new ResultFixedLayoutPlan(new LayoutRect(1160f, 1176f, 240f, 120f), 5, 0.5f, 0.9f),
            FixedLayoutWindowWidth, FixedLayoutWindowHeight, "host", "failure");
        check(thinOverlapLine.Contains("scan=incomplete") && !thinOverlapLine.Contains("scan=complete"),
            "and its line says scan=incomplete, never scan=complete, when the scan was not proven: " + thinOverlapLine);
        check(thinOverlapLine.Contains("readback=(complete=false") && thinOverlapLine.Contains("does not claim there is no conflict"),
            "so the sentence agrees with the readback it carries on the same line");
        check(thinOverlapLine.Contains("CM_Item") && thinOverlapLine.Contains("returnToLobby"),
            "and the intersecting control is still named on that faint frame");
        check(overlap.Describe(new ResultFixedLayoutPlan(new LayoutRect(1160f, 1176f, 240f, 120f), 5, 0.5f, 0.9f),
                FixedLayoutWindowWidth, FixedLayoutWindowHeight, "host", "failure").Contains("scan=complete"),
            "while a PROVEN intersecting frame still says scan=complete (the two states keep their own wording)");

        string refresh = FixedLayoutMethodBody(display, "internal static void Refresh(eGameStateName state)");
        check(refresh != null && FixedLayoutCount(refresh, "ApplyVanillaBlocks(") == 0,
            "M2 -> 1.19.47 (review #3): `Refresh` no longer puts a single block away - the pre-draw hiding entry that could move a block to 6000 on a frame that then refused to draw is gone");
        string fixedDraw = FixedLayoutMethodBody(display, "private static void FixedLayoutDraw()");
        check(fixedDraw != null && fixedDraw.Contains("if (!draw.HidesVanilla) RestoreHidden();")
              && fixedDraw.IndexOf("if (draw.HidesVanilla)", StringComparison.Ordinal)
                 > fixedDraw.IndexOf("HudTable.Draw(grid", StringComparison.Ordinal),
            "and the fixed board's plan is asked where it is honoured: thin evidence puts the game's own blocks back before anything is drawn, and a proven board takes their place only after this frame's `HudTable.Draw`");
        // 1.19.55: the same guard, plus the early-board contract the adaptive path now also asks. An early board (drawn
        // while the page's own reveal is still running) stands over the page as the game drew it and puts nothing away.
        check(display.Contains("ResultEarlyBoardPolicy.HidesVanilla(_earlyBoard, true)")
              && display.Contains("&& (!FixedLayoutActive() || _fixedPlan.HidesVanilla))")
              && display.Contains("if (vanillaPage != null) ApplyVanillaBlocks(vanillaPage, vanillaSucceeded);"),
            "M2b -> 1.19.55: the adaptive draw path is still guarded by the fixed layout's plan - with FixedLayout on it cannot hide the blocks behind a plan that says keep them - and it now also asks the early-board contract, so a board drawn before the page proved its own boundary puts none of the page's blocks away; the page it hands over is the live one (`LivePage`, 1.19.57)");
        check(FixedLayoutCount(display, "ApplyVanillaBlocks(") == 3,
            "the file has one declaration and exactly two call sites (" + FixedLayoutCount(display, "ApplyVanillaBlocks(") + " total): "
            + "the adaptive draw and the fixed draw, both after their own `HudTable.Draw` - no third way to hide the page");
    }

    // ---- 读生产源码：路径、剥注释、取方法体、数出现次数 ------------------------------------------------
    private static string FixedLayoutSource(string root, string name)
    {
        string path = Path.Combine(root, name);
        if (!File.Exists(path)) throw new FileNotFoundException("Production source is missing", path);
        return File.ReadAllText(path);
    }

    private static string FixedLayoutSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj")))
            directory = directory.Parent;
        return directory != null ? directory.FullName : null;
    }

    // A file's source with every comment removed, so a wiring assertion cannot be satisfied by the prose around the code.
    private static string FixedLayoutCode(string source)
    {
        var text = new System.Text.StringBuilder(source.Length);
        bool line = false, block = false;
        for (int index = 0; index < source.Length; index++)
        {
            char current = source[index];
            char next = index + 1 < source.Length ? source[index + 1] : '\0';
            if (line)
            {
                if (current == '\n') { line = false; text.Append(current); }
                continue;
            }
            if (block)
            {
                if (current == '*' && next == '/') { block = false; index++; }
                continue;
            }
            if (current == '/' && next == '/') { line = true; index++; continue; }
            if (current == '/' && next == '*') { block = true; index++; continue; }
            text.Append(current);
        }
        return text.ToString();
    }

    // ---- review #4：贴顶固定板（30 行）的表头与关闭按钮必须在屏幕内 --------------------------------
    //
    // 生产算式：底边中点 = anchorY × 窗口高；板高 = 整行 × 行高；**表头画在板之上**（`ResultChromeLayout.BoardOf`），
    // 所以"放得下"的条件是"表头 + 行"一起放得下。行高用 1080p / 18px 下生产公式给出的 24px
    // （`nov21-scroll\logs\window-numbers.txt`：rowHeight = max(font×1.45×0.80, font×1.5)），表头取
    // `ResultChromeMetrics` 自己算出的值 —— 没有一个是猜的。
    private static void ATopPinnedThirtyRowBoardKeepsItsCloseOnScreen(Action<bool, string> check)
    {
        const float width = 1920f, height = 1080f, rowHeight = 24f, margin = 8f;
        ResultChromeMetrics metrics = ResultChromeMetrics.For(width, height);
        float header = metrics.HeaderHeight;
        const int rows = 30;
        // 表头顶在屏幕顶端 margin 上的那个 anchor：room = margin + header + rows × rowHeight。
        float anchorY = (margin + header + rows * rowHeight) / height;
        check(anchorY > 0f && anchorY < 1f, "the anchor that pins a 30-row board to the top is inside the window: anchorY=" + anchorY.ToString("0.####"));

        float room = anchorY * height - margin;
        check(ResultsScroll.PlateFits(room, rowHeight, header, ResultsScroll.MinimumVisibleRows),
            "the plate fits once the header is reserved: room=" + room.ToString("0") + "px, header=" + header.ToString("0") + "px");

        int visible = ResultsScroll.VisibleRows(ResultsScroll.RowRoom(room, header), rowHeight);
        check(visible == rows, "thirty rows fit under the header at this anchor: visible=" + visible);
        var viewport = new ScrollViewport(rows, ResultsScroll.FrozenRows, visible, ResultsScroll.FrozenRows);
        check(!viewport.Overflows && viewport.RowAt(viewport.Visible - 1) == rows - 1,
            "with all thirty rows visible the last row is on the page and nothing is off screen");

        // 更大的字号（行高 36px）时只有部分行放得下：滚到末端时最后一行必须落在最后一个可见槽里。
        const float bigRow = 36f;
        int bigVisible = ResultsScroll.VisibleRows(ResultsScroll.RowRoom(room, header), bigRow);
        var scrolled = new ScrollViewport(rows, ResultsScroll.FrozenRows, bigVisible, ResultsScroll.FrozenRows);
        check(scrolled.Overflows, "a taller row means the table really scrolls: visible=" + bigVisible);
        var atEnd = new ScrollViewport(rows, ResultsScroll.FrozenRows, bigVisible, scrolled.MaximumFirst);
        check(atEnd.AtEnd && atEnd.RowAt(atEnd.Visible - 1) == rows - 1,
            "and the last row is reachable: the wheel puts it in the plate's last slot (first=" + atEnd.First + ")");

        // 板本身的条带不变式：板顶就在屏幕 margin 上，整块板（表头 + 表格）四面都在屏内且真的点得到。
        float gridTop = anchorY * height - visible * rowHeight;
        LayoutRect grid = new LayoutRect(600f, gridTop, 700f, visible * rowHeight);
        LayoutRect board = ResultChromeLayout.BoardOf(grid, metrics);
        check(Math.Abs(board.Y - margin) < 0.6f,
            "the board's own top edge sits at the screen margin: board.Y=" + board.Y.ToString("0.##"));
        // 1.19.45：没有第二个 Close 需要摆位了。留下来的不变式是整块板 —— 表头 + 表格 —— 必须落在这次量到的条带里，
        // 而且它问的就是滚轮和绘制用的那同一个矩形。
        check(ResultChromeLayout.PlateInsideBand(board, margin, height, height),
            "the board - header included - is inside the band it was placed into: ("
            + board.X.ToString("0") + "," + board.Y.ToString("0") + "," + board.Width.ToString("0") + "x" + board.Height.ToString("0") + ")");
        check(ResultChromeLayout.Inside(board, board.X + board.Width * 0.5f, board.Y + board.Height * 0.5f),
            "and a point in the middle of that plate is on the board's own surface");
        check(!ResultChromeLayout.Inside(board, board.X + board.Width * 0.5f, board.Y - 2f),
            "while a point above the board is not a hit");
    }

    // ---- review #4：放不下"表头 + 两行"时不许声称可见/可点 -----------------------------------------
    private static void AFixedPlateWithoutRoomForItsHeaderRefuses(Action<bool, string> check)
    {
        const float width = 1920f, height = 1080f, rowHeight = 24f, margin = 8f;
        ResultChromeMetrics metrics = ResultChromeMetrics.For(width, height);
        float header = metrics.HeaderHeight;

        // 锚点设得很靠上：剩下的高度连表头加两行都装不下。
        float anchorY = (margin + header + rowHeight * 1.5f) / height;
        float room = anchorY * height - margin;
        check(!ResultsScroll.PlateFits(room, rowHeight, header, ResultsScroll.MinimumVisibleRows),
            "a room that cannot hold the header and two rows reports no plate: room=" + room.ToString("0") + "px, header=" + header.ToString("0") + "px");
        check(ResultFixedLayout.VerticalRoom.Length > 0 && ResultFixedLayout.VerticalRoom != ResultFixedLayout.OverlapsControl,
            "and it refuses with a reason of its own, distinct from the overlap state: " + ResultFixedLayout.VerticalRoom);

        // 防御：板顶被算成负值时，条带不变式必须拒绝它（旧实现只看左右）。
        LayoutRect aboveScreen = new LayoutRect(600f, -header, 700f, 400f);
        check(!ResultChromeLayout.PlateInsideBand(aboveScreen, margin, height, height),
            "a plate whose header is above the screen is refused by the band invariant");
        // 同一块板往下挪一屏距（表头进屏）就恢复合法：拒绝的是"看不见"，不是位置本身。
        LayoutRect onScreen = new LayoutRect(600f, margin, 700f, 400f);
        check(ResultChromeLayout.PlateInsideBand(onScreen, margin, height, height),
            "the same plate one header lower is legal again");
    }

    // ---- review #4：刷新不得把同一页的滚动位置甩回顶部；滚轮只动真的能滚的表 ---------------------------
    private static void TheScrollOffsetSurvivesARefreshAndTheWheelNeedsOverflow(Action<bool, string> check)
    {
        // 同一页的再量一次（轮询重建、刷新、换分辨率）保留位置；换页回到表头。
        check(ResultsScroll.OffsetAfterReset(7, samePage: true, rows: 30, frozen: ResultsScroll.FrozenRows, slots: 20) == 7,
            "a re-measure of the same page keeps the row the player scrolled to");
        check(ResultsScroll.OffsetAfterReset(29, samePage: true, rows: 30, frozen: ResultsScroll.FrozenRows, slots: 20)
              == ResultsScroll.MaximumFirst(30, ResultsScroll.FrozenRows, 20),
            "and a smaller plate clamps it to the new foot instead of leaving it past the table");
        check(ResultsScroll.OffsetAfterReset(7, samePage: false, rows: 30, frozen: ResultsScroll.FrozenRows, slots: 20)
              == ResultsScroll.FrozenRows,
            "a different page starts at its own head, not where the last one was left");

        // 滚轮：表真的超出视口才动。
        check(!new ScrollViewport(30, ResultsScroll.FrozenRows, 30, ResultsScroll.FrozenRows).Overflows,
            "a table whose rows all fit has nothing to scroll");
        check(new ScrollViewport(30, ResultsScroll.FrozenRows, 20, ResultsScroll.FrozenRows).Overflows,
            "a table taller than its plate does scroll");

        string root = FixedLayoutSourceRoot();
        check(root != null, "the scroll regression locates the production sources");
        if (root == null) return;
        string display = FixedLayoutCode(FixedLayoutSource(root, "ResultsDisplay.cs"));
        string scroll = FixedLayoutCode(FixedLayoutSource(root, "ResultsScroll.cs"));
        // 1.19.59: the handler is `PollWheel` now (the frame's own sample), and the "refuse a table that does not overflow"
        // rule moved into `WheelApplies` rather than sitting inside the scroll maths.
        check(display.Contains("if (!viewport.Overflows) return false;"),
            "the wheel handler refuses a table that does not overflow (static source check, not a runtime proof): a board that cannot scroll must not swallow the page's wheel");
        // 1.19.56 (review 7/8/9): the page is captured *before* the cache is cleared. The old comparison ran after
        // `_page` had already been set to null, so it could never be true and every re-measure of one page looked like
        // a new page - the player's scroll position was dropped and the table jumped back to its head.
        check(display.Contains("Component previousPage = _page;") && display.Contains("ResultsScroll.OffsetAfterReset(_scrollFirst, sameScrollPage,")
              && display.Contains("bool sameScrollPage = _scrollOwner != null && ReferenceEquals(_scrollOwner, previousPage);"),
            "the placement reset keeps the offset for the same page object and drops it for a new one, comparing against the page captured before the cache was cleared");
        check(display.Contains("_scrollOwner = null;"),
            "and leaving or replacing the page clears the owner, so the next page starts at its head");
        check(scroll.Contains("public static int OffsetAfterReset(int first, bool samePage, int rows, int frozen, int slots)"),
            "the rule itself lives in the Unity-free scroll policy, which is what this assertion runs");
    }

    private static string FixedLayoutMethodBody(string source, string signature)
    {
        if (source == null) return null;
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) return null;
        int open = source.IndexOf('{', start);
        if (open < 0) return null;
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
        return null;
    }

    private static int FixedLayoutCount(string source, string needle)
    {
        if (source == null || string.IsNullOrEmpty(needle)) return 0;
        int count = 0, at = -1;
        while ((at = source.IndexOf(needle, at + 1, StringComparison.Ordinal)) >= 0) count++;
        return count;
    }
}
