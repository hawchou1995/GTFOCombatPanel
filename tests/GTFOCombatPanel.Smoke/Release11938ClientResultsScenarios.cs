using System;
using HawChow.GTFOCombatPanel;

// 1.19.38: 客机失败页的结算板 = 页面固定几何，不是原生「正在等待厅主」文字。
//
// 缺陷（真机局，2560x1440，`LogOutput.log`）：
//
//   [DIAG] failure footerSource=client-fixed-page-geometry: ... strip=(837..1429) of a 1440 window
//   [DIAG] failure band: top=837 (source=title-anchor), bottom=1207 (source=footer-text, waitingText=1228),
//          room=370px, clearance=21.3px, boardFloor=270px, role=client
//   [DIAG] influence board 3 columns x 30 rows (2 players), 435x360 px at (1062,837) ... glyph 8px
//   [DIAG] result board re-measured: ... the strip ... was (837..1207) and it is (837..1237) now
//
// 客机已经拿到固定页面几何 837..1429，却又被 `ResultsBounds.TryResolve` 用原生等待文字块的上沿
// （1228 减 clearance 21.33 = 1207）二次夹紧成 370px：30 行只能塞进 8px 字号，而且那行字跨帧往下
// 移动 30px 就触发一次「result board re-measured」的重新适配 —— 玩家看到的是「先小、后变、不是一步到位」。
//
// 这一版的口径（本文件逐条驱动生产类型）：
//   1. 客机失败页的条带下界 = 页面固定几何的下界（本机实测 1429），不再被等待文字夹紧；
//   2. 等待文字照旧被测量并写进日志字段 `waitingText=`，但它不是墙，也不是可交互控件（不触发遮挡拒绝）；
//   3. 同一页面几何下首帧即最终尺寸，等待文字移动不改变条带、不改变字号、不改变板宽；
//   4. 主机路径一点没变：标题 / 按钮 / 无自由带 的既有形状仍走 `ResultsBounds`，并回答 1.19.37 的数字。
//
// 这个文件能做到什么、不能做到什么，说清楚：
//   * 下面的条带、字号、板宽、板高都由生产类型算出 —— `ResultsGuestPageGeometry`、`ResultClientFailureBand`、
//     `ResultsBounds`、`ResultsBandFit`、`ResultsFontGrowth`、`ResultBoardHold`、`ResultInteractionStripPolicy`、
//     `ResultAnchorDrift`、`PanelGeometry.Build`；
//   * 没有任何一行在这里跑 Unity：`ResultsDisplay`、`HudTable.Draw`、IMGUI 与原生页面都进不了这个工程，所以
//     这里没有绘制次数、没有像素回读，也没有一张截图的结论；
//   * 凡是只存在于 `ResultsDisplay` / `GuestPageGeometry` 源码里的事实（接线），断言就明说是源码串检查，
//     并在描述里写「这不是运行时证明」。
internal static partial class Program
{
    // 记录页面的真机数字（2560x1440 客户机，一次失败局）。
    private const float ClientScreenWidth = 2560f, ClientScreenHeight = 1440f;
    private const float MessageBottom = 815f;            // 任务失败标题自己的布局底边
    private const float WaitingTextTop = 1228f;          // m_returnToLobby_text 自己的布局上沿
    private const float MessageGap = 16f;                // ResultsDisplay.MessageGap
    private const float MinimumGuestStripHeight = 96f;   // ResultsDisplay.MinimumGuestStripHeight
    private const int Seed = 17;                         // 记录页面上的 FontSizeForSharing
    private const float BoardWidth = 2512f;              // Mathf.Max(320f, Screen.width - 48f)
    private const int Rows = 30;                         // ImpactBoard.RowCount(true)
    // 1.19.37 的主机形状：标题 467、按钮 1151、没有可用的自由带。
    private const float HostTitleEdge = 467f, HostButtonTop = 1151f;

    private static void RunRelease11938ClientResultsScenarios()
    {
        Console.WriteLine("---- 1.19.38: the client failure strip is the fixed page geometry, not the waiting line ----");
        TheClientFailureStripIsTheFixedPage(Check);
        TheFontTheBiggerStripPaysFor(Check);
        TheFirstFrameIsTheFinalSize(Check);
        TheHostPathIsUntouched(Check);
        TheWiringAfter1_19_38(Check);
    }

    // ---- 1: 条带下界 = 固定页面几何下界；等待文字只进日志 ---------------------------------------------------
    private static void TheClientFailureStripIsTheFixedPage(Action<bool, string> check)
    {
        check(ResultsGuestPageGeometry.TryResolve(MessageBottom, ClientScreenWidth, ClientScreenHeight, MessageGap,
                MinimumGuestStripHeight, out ResultsSafeArea fixedPage),
            "the client page's fixed geometry is resolved from the recorded title edge (messageBottom=815) on a 2560x1440 window");
        check(near(fixedPage.Top, 836.333f, 0.01f) && near(fixedPage.Bottom, 1429.333f, 0.01f),
            $"and it is ({fixedPage.Top:0.##}..{fixedPage.Bottom:0.##}) - the 837..1429 the recorded log printed under `footerSource=client-fixed-page-geometry`");
        check(near(fixedPage.Bottom, 1429.333f, 0.01f)
              && near(ClientScreenHeight - fixedPage.Bottom, 10.667f, 0.01f),
            $"its lower edge is 1429.333px, leaving the fixed 10.667px foot of a 2560x1440 window - both pinned as literals (the 1080p reference foot scaled up) instead of re-derived from the same formula the page itself ran");

        check(ResultClientFailureBand.TryResolve(fixedPage, ClientScreenHeight, MinimumGuestStripHeight,
                WaitingTextTop, float.NaN, out ResultClientBandReading clientBand),
            "the client failure band is resolved from that geometry with the measured waiting line (1228) and no pressable control");
        check(near(clientBand.Top, 836.333f, 0.01f) && near(clientBand.Bottom, 1429.333f, 0.01f)
              && near(clientBand.Height, 593f, 0.01f),
            $"so its room is {clientBand.Height:0.##}px, not the 370px the recorded band printed - the waiting line is a fact here, not a wall");
        check(near(ResultsBounds.Clearance(ClientScreenHeight), 21.333f, 0.01f),
            $"the clearance the host path keeps under a pressable control is the fixed 21.333px literal for a 2560x1440 window (16 reference units at 1080p, scaled up) - pinned as a number, neither it nor the foot above is a reading of the waiting line");
        check(clientBand.Height >= 590f,
            $"the acceptance floor stated for this release: the client strip is at least 590px tall ({clientBand.Height:0.##}px), against 370px before");
        check(!clientBand.PressableTightened && near(clientBand.WaitingTextTop, WaitingTextTop, 0.001f),
            "no control the player can press was measured on this page, so nothing tightened the strip, and the waiting line is still carried for the log");
        check(clientBand.WaitingTextCovered,
            "and the board's band really does run past the waiting line - that is the trade the fixed page geometry makes, said out loud instead of silently shrinking the table");

        // 反例：同一帧的事实走旧规则（下界取等待文字上沿减 clearance）会得到 370px。
        bool old = ResultsBounds.TryResolve(ClientScreenHeight, MessageBottom, ResultBoundSource.TitleAnchor,
            float.NaN, WaitingTextTop, float.NaN, out ResultsBand oldBand);
        check(old && near(oldBand.Top, 836.333f, 0.01f) && near(oldBand.Bottom, 1206.667f, 0.01f)
              && near(oldBand.Height, 370.333f, 0.02f) && oldBand.BottomSource == ResultBoundSource.FooterText,
            $"the rule this release removes, in the same numbers: the waiting line at 1228 clamped the recorded strip to {oldBand.Bottom:0.##} ({oldBand.Height:0.##}px, source={ResultsBounds.Name(oldBand.BottomSource)}) - the 370px/8px defect");
        check(oldBand.Height < clientBand.Height - 220f,
            $"{oldBand.Height:0}px and {clientBand.Height:0.##}px are the two rules: the old foot cost {clientBand.Height - oldBand.Height:0.##}px of the page");

        // 扫描自己也要分得清「那行字」和「可按的控件」：同一个列表里，FooterTop 可以是文字，PressableTop 不能。
        var textOnly = new[] { new ResultActionBounds(new LayoutRect(0f, WaitingTextTop, ClientScreenWidth, 40f), true) };
        check(ResultInteractionStripPolicy.TryResolve(MessageBottom, MessageGap, textOnly, out ResultInteractionStrip textStrip)
              && near(textStrip.FooterTop, WaitingTextTop, 0.01f) && !float.IsFinite(textStrip.PressableTop),
            $"the waiting line is still the strip's footer ({textStrip.FooterTop:0.##}) but it is not a pressable control at all, so `PressableTop` stays unmeasured - which is exactly the figure the client band must be tightened by");
        var textAndButton = new[]
        {
            new ResultActionBounds(new LayoutRect(0f, WaitingTextTop, ClientScreenWidth, 40f), true),
            new ResultActionBounds(new LayoutRect(0f, 1000f, 400f, 60f))
        };
        check(ResultInteractionStripPolicy.TryResolve(MessageBottom, MessageGap, textAndButton, out ResultInteractionStrip mixedStrip)
              && near(mixedStrip.FooterTop, 1000f, 0.01f) && near(mixedStrip.PressableTop, 1000f, 0.01f)
              && mixedStrip.FooterActionCount == 1,
            $"with a real control above the line both figures name the control ({mixedStrip.PressableTop:0.##}) and the line no longer counts as an action - the one place the two walls are told apart");
    }

    // ---- 2: 30 行在新条带里拿到的字号 ---------------------------------------------------------------
    private static void TheFontTheBiggerStripPaysFor(Action<bool, string> check)
    {
        int analytic = ResultsBandFit.FontForBand(592f, Rows, PanelDensity.Compact, int.MaxValue);
        check(analytic >= 12,
            $"the analytic mapping answers {analytic}px for a 592px band over 30 rows at the compact density (floor(592 / 30 / 1.5)), which is already past the 12px this release has to reach - the recorded page drew 8px");
        int floor = ResultsBandFit.FloorForBand(593f, Rows, PanelDensity.Compact,
            ResultsFontGrowth.ReadabilityFloor(float.NaN), out bool squeezed);
        check(floor == ResultsFontGrowth.MinimumReadablePixels && !squeezed,
            $"a client page measures no pressable button, so the readability floor stays the absolute one ({floor}px) and a 593px band does not squeeze it");
        float ceiling = ResultsFontGrowth.FillCeiling(ResultFooterSource.ClientNoFooterSafeArea,
            ResultsFontGrowth.FootIsTheWall(1429.333f, WaitingTextTop, MessageGap));
        check(ceiling == ResultsFontGrowth.StripFillCeiling,
            $"the strip's foot is the page's own edge, not a measured wall, so it keeps the comfort ceiling ({ceiling:0.00}) - the board stops short of an edge nobody measured, exactly as `FootIsTheWall` is asserted to answer elsewhere");
        int font = ResultsFontGrowth.FitBoardIntoStrip(Seed, floor, int.MaxValue, 593f, BoardWidth, Natural(Rows),
            out int usedFloor, ceiling);
        ResultTableSize size = Natural(Rows)(font);
        Console.WriteLine($"CLIENT-STRIP: room=593px glyph={font}px board={size.Width:0}x{size.Height:0} (floor {usedFloor}px, ceiling {ceiling:0.00})"
            + $" | recorded 8px 435x360");
        check(font >= 12,
            $"the measured search answers {font}px for the 593px strip, past the 12px floor this release has to reach; the recorded page's 8px glyph is the counter-example");
        check(usedFloor == ResultsFontGrowth.MinimumReadablePixels && near(size.Height, 45f * font, 0.01f),
            $"and it does it at the absolute floor ({usedFloor}px), with the table's own {size.Height:0}px height (30 rows x 1.5em x {font}px) inside the band");
        check(size.Height <= 593f + 0.5f,
            $"the board ({size.Height:0}px) is inside the 593px strip, so nothing is drawn past the page's own foot");
        ResultTableSize atEight = Natural(Rows)(8);
        check(size.Width > atEight.Width && near(size.Width / atEight.Width, (float)font / 8f, 0.02f),
            $"the page's width grows with the glyph exactly as its columns measure: {atEight.Width:0}px at the recorded 8px and {size.Width:0}px at {font}px ({size.Width / atEight.Width:0.###}x, the {font}/8 the fit asked for) - the live page measured 435px wide for the 8px table, and this is the same surface one and a half times the size");

        // The board the fit produces, placed by the production placement.
        var strip = new BoardStrip(836.333f, 1429.333f, false, float.NaN, float.NaN, true,
            ResultFooterSource.ClientNoFooterSafeArea, ceiling);
        check(ResultBoardHold.TryPlace(ClientScreenWidth, ClientScreenHeight, strip, size.Width, size.Height, 0f,
                out LayoutRect placed) && near(placed.Y, 836.333f, 0.01f) && near(placed.Bottom, 836.333f + size.Height, 0.01f),
            $"the table hangs from the band's own top ({placed.Y:0.##}..{placed.Bottom:0.##}), one placement, no step-up later");
        check(placed.Bottom <= 1429.333f && placed.Bottom <= ClientScreenHeight - 8f,
            $"and its foot ({placed.Bottom:0.##}) is inside both the page's own lower edge and the window's foot margin");
        // 它就压在那行字上：这正是 1.19.38 用「不撤板」换来的房间，而不是靠缩小字号回避。
        var waitingLine = new LayoutRect(0f, WaitingTextTop, ClientScreenWidth, 40f);
        check(ResultInteractionStripPolicy.OverlapsBoard(waitingLine, placed, ResultBoardHold.ButtonClearance),
            $"the geometry really does overlap the waiting line ({placed.Y:0.##}..{placed.Bottom:0.##} vs 1228..1268) - so the old per-repaint refusal would have taken the whole board away; the wiring below is what must not ask it on a client");
    }

    // ---- 3: 首帧即最终大小 ------------------------------------------------------------------------
    private static void TheFirstFrameIsTheFinalSize(Action<bool, string> check)
    {
        bool ok0 = ResultClientFailureBand.TryResolve(FixedPage(), ClientScreenHeight, MinimumGuestStripHeight,
            WaitingTextTop, float.NaN, out ResultClientBandReading t0);
        // t+2s: 同一页面几何、同一分辨率，逐帧重复 120 次（2 秒的 60Hz 重绘），每一次都必须逐像素相同。
        bool same = ok0;
        int distinct = 0;
        for (int frame = 0; frame < 120; frame++)
        {
            if (!ResultClientFailureBand.TryResolve(FixedPage(), ClientScreenHeight, MinimumGuestStripHeight,
                    WaitingTextTop, float.NaN, out ResultClientBandReading later)
                || !near(later.Top, t0.Top, 0.0001f) || !near(later.Bottom, t0.Bottom, 0.0001f))
                same = false;
            distinct++;
        }
        check(same && distinct == 120,
            "120 repaints of the same page geometry answer the same strip, to the pixel - there is no second, smaller first frame to grow out of");

        // 等待文字从 1228 移到 1258：旧实现正是在这一步重排（1207 -> 1237）。
        bool movedText = ResultClientFailureBand.TryResolve(FixedPage(), ClientScreenHeight, MinimumGuestStripHeight,
            1258f, float.NaN, out ResultClientBandReading at1258);
        check(movedText && near(at1258.Top, t0.Top, 0.0001f) && near(at1258.Bottom, t0.Bottom, 0.0001f)
              && near(at1258.Height, t0.Height, 0.0001f),
            $"the waiting line moving 30px down (1228 -> 1258) leaves the strip exactly as it was ({at1258.Height:0.##}px) - this is the frame the recorded page re-fitted on");
        check(oldStrip(WaitingTextTop).Height < oldStrip(1258f).Height - 20f,
            $"the old rule is what moved: the same two samples ({oldStrip(WaitingTextTop).Height:0.##}px and {oldStrip(1258f).Height:0.##}px) are {oldStrip(1258f).Height - oldStrip(WaitingTextTop).Height:0.##}px apart, which is the `result board re-measured` in the log");

        // 同一个字号、同一个板宽：格子本身一次到位。
        float ceiling = ResultsFontGrowth.FillCeiling(ResultFooterSource.ClientNoFooterSafeArea, false);
        int floor = ResultsBandFit.FloorForBand(t0.Height, Rows, PanelDensity.Compact,
            ResultsFontGrowth.ReadabilityFloor(float.NaN), out _);
        int font0 = ResultsFontGrowth.FitBoardIntoStrip(Seed, floor, int.MaxValue, t0.Height, BoardWidth, Natural(Rows), out _, ceiling);
        int font1 = ResultsFontGrowth.FitBoardIntoStrip(Seed, floor, int.MaxValue, at1258.Height, BoardWidth, Natural(Rows), out _, ceiling);
        ResultTableSize size0 = Natural(Rows)(font0), size1 = Natural(Rows)(font1);
        check(font0 == font1 && near(size0.Width, size1.Width, 0.001f) && near(size0.Height, size1.Height, 0.001f),
            $"and the grid is the same one: {font0}px, {size0.Width:0}x{size0.Height:0} both samples - the first frame the anchor settled is the final size");

        // 漂移规则本身：新条带对那 30px 一动不动，旧条带会花掉一次重排预算。
        var drift = new ResultAnchorDrift();
        drift.Record(0, t0.Top, t0.Height);
        check(!ResultAnchorDrift.Moved(at1258.Height, t0.Height) && !drift.Due(1, at1258.Top, at1258.Height)
              && !drift.Due(2, at1258.Top, at1258.Height) && drift.Recoveries == 0,
            "`ResultAnchorDrift` sees no move at all on the new strip for the same 30px of waiting-line animation - and spends none of the page's re-measure budget");
        var oldDrift = new ResultAnchorDrift();
        oldDrift.Record(0, oldStrip(WaitingTextTop).Top, oldStrip(WaitingTextTop).Height);
        check(!oldDrift.Due(1, oldStrip(1278f).Top, oldStrip(1278f).Height)
              && oldDrift.Due(2, oldStrip(1278f).Top, oldStrip(1278f).Height) && oldDrift.Recoveries == 0,
            "while the old foot records the move for the same page - the first sample is one reading, the second spends the page's re-measure - which is the growth re-fit `result board re-measured` this release removes from the client page");
    }

    // ---- 4: 主机路径逐像素不变 ----------------------------------------------------------------------
    private static void TheHostPathIsUntouched(Action<bool, string> check)
    {
        // 主机：同一次 `ResultsBounds` 调用，下界是按钮 / 量到的控件里最高的那堵墙。
        bool host = ResultsBounds.TryResolve(ClientScreenHeight, HostTitleEdge, ResultBoundSource.TitleAnchor,
            HostButtonTop, WaitingTextTop, WaitingTextTop, out ResultsBand hostBand);
        check(host && near(hostBand.Top, 488.333f, 0.01f) && near(hostBand.Bottom, 1129.667f, 0.01f)
              && near(hostBand.Height, 641.333f, 0.02f) && hostBand.BottomSource == ResultBoundSource.FooterControl,
            $"a host page with the 1.19.37 shape (title 467, button 1151, waiting text 1228) still answers ({hostBand.Top:0.##}..{hostBand.Bottom:0.##}), foot={ResultsBounds.Name(hostBand.BottomSource)} - unchanged, because nothing in this release touches `ResultsBounds`");
        check(near(hostBand.Bottom, HostButtonTop - ResultsBounds.Clearance(ClientScreenHeight), 0.01f),
            $"its foot is still the button minus the scaled clearance ({HostButtonTop} - {ResultsBounds.Clearance(ClientScreenHeight):0.##}), never the waiting line");
        float hostRoom = Math.Min(hostBand.Bottom, ClientScreenHeight - 8f) - hostBand.Top;
        int hostFont = ResultsFontGrowth.FitBoardIntoStrip(Seed,
            ResultsBandFit.FloorForBand(hostRoom, Rows, PanelDensity.Compact, ResultsBandFit.FontForBand(hostRoom, Rows, PanelDensity.Compact, 0), out _),
            int.MaxValue, hostRoom, BoardWidth, Natural(Rows),
            out _, ResultsFontGrowth.FillCeiling(ResultFooterSource.NamedControl, true));
        ResultTableSize hostSize = Natural(Rows)(hostFont);
        Console.WriteLine($"HOST-STRIP: room={hostRoom:0.###}px glyph={hostFont}px board={hostSize.Width:0}x{hostSize.Height:0} ceiling={ResultsFontGrowth.FillCeiling(ResultFooterSource.NamedControl, true):0.00}");
        check(hostFont == 13 && near(hostSize.Height, 585f, 0.01f) && near(hostSize.Width, 166f, 1f),
            $"and the host board it produces ({hostSize.Width:0}x{hostSize.Height:0} at {hostFont}px) is the comfort-ceiling table the host path drew in 1.19.37 for the same facts - this line exists so an edit that reuses the client rule on the host fails here");

        // 主机在「按钮量不到」时仍然被等待文字夹紧 —— 这正是客机不再需要的那一次夹紧。
        bool hostNoButton = ResultsBounds.TryResolve(ClientScreenHeight, MessageBottom, ResultBoundSource.TitleAnchor,
            float.NaN, WaitingTextTop, float.NaN, out ResultsBand hostNoButtonBand);
        check(hostNoButton && near(hostNoButtonBand.Bottom, 1206.667f, 0.01f)
              && hostNoButtonBand.BottomSource == ResultBoundSource.FooterText,
            $"a host page whose button could not be measured still takes the waiting/footer text as its wall ({hostNoButtonBand.Bottom:0.##}) - the client's rule is the client's alone");
        check(ResultClientFailureBand.TryResolve(FixedPage(), ClientScreenHeight, MinimumGuestStripHeight,
                WaitingTextTop, HostButtonTop, out ResultClientBandReading tightened)
              && tightened.PressableTightened && near(tightened.Bottom, HostButtonTop - ResultsBounds.Clearance(ClientScreenHeight), 0.01f),
            $"and a client page that does hand over a pressable control still has its strip pulled above it ({tightened.Bottom:0.##}) - the control, not the text, is the wall, and that foot is a measured wall so its band earns the whole-band ceiling ({ResultsFontGrowth.FillCeiling(ResultFooterSource.ClientNoFooterSafeArea, tightened.PressableTightened):0.00})");
        check(ResultFontGrowthEdgeKeepsTheMinimum(),
            $"a pressable control that would leave less than the {MinimumGuestStripHeight:0}px client minimum does not tighten the strip at all (the unchanged condition), so a page with no room for both keeps the fixed geometry and the overlap refusal above answers it");
    }

    private static bool ResultFontGrowthEdgeKeepsTheMinimum()
    {
        // 可按控件在 900：900 - 21.33 = 878.67，去掉 836.33 只剩 42.33px < 96px，于是不收紧。
        if (!ResultClientFailureBand.TryResolve(FixedPage(), ClientScreenHeight, MinimumGuestStripHeight,
                WaitingTextTop, 900f, out ResultClientBandReading reading)) return false;
        return !reading.PressableTightened && near(reading.Bottom, 1429.333f, 0.01f);
    }

    // ---- 5: 接线（源码串检查，不是运行时证明） --------------------------------------------------------
    private static void TheWiringAfter1_19_38(Action<bool, string> check)
    {
        string root = ClientResultsSourceRoot();
        check(root != null, "the 1.19.38 regression locates the production sources");
        if (root == null) return;
        string displaySource = ClientResultsSource(root, "ResultsDisplay.cs");
        string display = ClientResultsCode(displaySource);
        string geometrySource = ClientResultsSource(root, "GuestPageGeometry.cs");
        string policySource = ClientResultsSource(root, "ResultsPagePolicy.cs");
        string resolve = ClientResultsMethodBody(ClientResultsCode(geometrySource), "private static ResultClientStripState Resolve(");

        string strip = ClientResultsMethodBody(display, "private static bool FailureStrip(Component page, out float top");
        check(strip != null, "the failure strip measurement is found in the production source (static check, not a runtime proof)");
        if (strip != null)
        {
            // 1.19.39: the branch asks the shared entry point, so the band call and its two inputs now live there; the
            // check follows them instead of pinning the call to this file, which is what keeps the order from drifting.
            check(strip.Contains("ResultClientFailureStrip.Decide(Time.frameCount, ClientWall,")
                  && strip.Contains("float pressableTop = ResultsRegion.HighestTop(namedTop, controls.PressableTop);")
                  && resolve != null
                  && resolve.Contains("ResultClientFailureBand.TryResolve(fixedPage, evidence.ScreenHeight, evidence.MinimumStripHeight,")
                  && resolve.Contains("evidence.WaitingTextTop, wallTop, out reading)"),
                "the client failure branch takes its band from `ResultClientFailureBand`, with the measured waiting line passed as a fact and the pressable controls - never the text - as the wall (static check, not a runtime proof)");
            check(ClientResultsCount(strip, "ResultsBounds.TryResolve(") == 1,
                "and the waiting line is passed to `ResultsBounds.TryResolve` in exactly one place - the host's `else if` - so no client frame can reach that clamp (static check, not a runtime proof)");
            int guestAt = strip.IndexOf("if (GuestClient())", StringComparison.Ordinal);
            int boundsAt = strip.IndexOf("ResultsBounds.TryResolve(", StringComparison.Ordinal);
            int guestReport = strip.IndexOf("ReportBand(\"failure\", new ResultsBand(top, bottom, bandTopSource, bandBottomSource, false),", StringComparison.Ordinal);
            check(guestAt >= 0 && guestReport > guestAt && boundsAt > guestReport,
                "the client's own band is reported before the host's `ResultsBounds` call, so the two branches cannot be taken in the same frame (static check, not a runtime proof)");
            check(strip.Contains("float clientWall = ClientWall.Wall;") && strip.Contains("buttonTop = clientWall;")
                  && strip.Contains("buttonMeasured = float.IsFinite(clientWall);"),
                "on a client only controls the player can press become the measured footer - the highest wall this page has really shown, never the waiting text (static check, not a runtime proof)");
        }

        string scan = ClientResultsMethodBody(display, "private static FooterControls ScanFooterControls(");
        check(scan != null, "the interaction scan is found in the production source (static check, not a runtime proof)");
        if (scan != null)
        {
            check(scan.Contains("if (inspectBoard && !client && ResultInteractionStripPolicy.OverlapsBoard(rect, board, ResultBoardHold.ButtonClearance))"),
                "the waiting-status block no longer raises the overlap flag on a client, so it can never reach `DeferBoardForInteraction` and take the whole board away (static check, not a runtime proof)");
            check(ClientResultsCount(scan, "if (inspectBoard && ResultInteractionStripPolicy.OverlapsBoard(rect, board, ResultBoardHold.ButtonClearance))") == 1,
                "while every control that can really be pressed keeps the ungated overlap refusal it always had - one occurrence, in `AddControl`, with no role test (static check, not a runtime proof)");
            check(scan.Contains("actions.Add(new ResultActionBounds(rect, protectedPageText: true));"),
                "and the waiting line is still measured and still joins the strip's own facts, so the log can still name it (static check, not a runtime proof)");
        }

        // 1.19.39: the failure page's resolution moved into the shared entry point this branch now asks, so the
        // count is "one direct call for the success page, one inside the entry point for the failure page".
        int guestGeometryCalls = ClientResultsCount(display, "ResultsGuestPageGeometry.TryResolve");
        int guestGeometryViaEntryPoint = ClientResultsCount(ClientResultsCode(geometrySource), "ResultsGuestPageGeometry.TryResolve");
        check(guestGeometryCalls == 1 && guestGeometryViaEntryPoint == 1
              && ClientResultsCount(display, "ResultClientFailureStrip.Decide(") == 1
              && ClientResultsCount(display, "ResultClientFailureStrip.Measure(") == 1,
            $"the fixed page geometry is still resolved exactly twice - the client success page in the display and the client failure page inside the entry point it asks (display={guestGeometryCalls}, entryPoint={guestGeometryViaEntryPoint}; static check, not a runtime proof)");
        check(display.Contains("if (!BoardStillSafe())") && display.Contains("DeferBoardForInteraction();"),
            "the per-repaint safety net and the deferral it triggers are still in the draw path (static check, not a runtime proof)");
        check(display.Contains("Drift.Record(Time.frameCount, strip.Top, strip.Bottom - strip.Top);"),
            "the drift sample is still recorded from the strip the board was placed into (static check, not a runtime proof)");

        // 客机成功页：它本来就是固定几何，这里确认它没有同类的二次夹紧（不改行为，只加断言）。
        string success = ClientResultsMethodBody(display, "private static bool SuccessRegion(");
        check(success != null, "the success region is found in the production source (static check, not a runtime proof)");
        if (success != null)
        {
            check(success.Contains("controls = ScanFooterControls(page, completion, MessageGap, null, board, inspectBoard);"),
                "the client success page never nominates the waiting line as protected page text - it passes `null` - so its tightening can only come from a control the player can press (static check, not a runtime proof)");
            check(success.Contains("if (area.Fits && float.IsFinite(controls.FooterTop) && controls.FooterTop - MessageGap > area.Top)")
                  && success.Contains("ResultsGuestPageGeometry.TryResolve(completion, Screen.width, Screen.height,"),
                "and its fixed geometry is resolved from the completion edge with no text-derived second clamp (static check, not a runtime proof)");
        }

        check(geometrySource.Contains("public static class ResultClientFailureBand")
              && geometrySource.Contains("public readonly struct ResultClientBandReading"),
            "the client failure band lives in `GuestPageGeometry.cs` beside the fixed page geometry it refines (static check, not a runtime proof)");
        check(policySource.Contains("public sealed class ResultAnchorDrift"),
            "and the drift budget the client page no longer spends is still the one page-wide budget (static check, not a runtime proof)");
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private static ResultsSafeArea FixedPage()
        => ResultsGuestPageGeometry.TryResolve(MessageBottom, ClientScreenWidth, ClientScreenHeight, MessageGap,
            MinimumGuestStripHeight, out ResultsSafeArea area)
            ? area
            : default;

    // 旧规则（客机被等待文字夹紧）在同一张页面上的答案，作为对照写进断言。
    private static ResultsBand oldStrip(float waitingTextTop)
        => ResultsBounds.TryResolve(ClientScreenHeight, MessageBottom, ResultBoundSource.TitleAnchor, float.NaN,
            waitingTextTop, float.NaN, out ResultsBand band) ? band : ResultsBand.None;

    // `ResultsDisplay.NaturalSize`, headless: 结算列按 `PanelGeometry.Build` 自己的规则量出来（行高 = 1.5em）。
    private static Func<int, ResultTableSize> Natural(int rows)
        => size =>
        {
            var columns = new[] { PanelColumn.ImpactLabel, PanelColumn.ImpactValue };
            var measured = new[]
            {
                PanelGeometry.EstimateTextWidth("TOTAL", size),
                PanelGeometry.EstimateTextWidth("#1 1,234.5 42.1%", size)
            };
            float[] widths = PanelGeometry.SettlementColumnWidths(columns, measured, size);
            var metrics = new[]
            {
                new PanelColumnMetrics(new[] { widths[0] }),
                new PanelColumnMetrics(new[] { widths[1] })
            };
            PanelGrid grid = PanelGeometry.Build(metrics, PanelGeometry.FieldGap(size, PanelDensity.Compact), size, rows,
                PanelDensity.Compact, 1f, new[] { false, true });
            return grid == null ? ResultTableSize.None : new ResultTableSize(grid.Width, grid.Height);
        };

    // `near(a, b, tolerance)` is Program's own helper (Program.cs), shared by every scenario file.
    private static string ClientResultsSource(string root, string name)
    {
        string path = System.IO.Path.Combine(root, name);
        if (!System.IO.File.Exists(path)) throw new System.IO.FileNotFoundException("Production source is missing", path);
        return System.IO.File.ReadAllText(path);
    }

    private static string ClientResultsSourceRoot()
    {
        var directory = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "GTFOCombatPanel.csproj")))
            directory = directory.Parent;
        return directory != null ? directory.FullName : null;
    }

    // 注释被剥掉之后再读接线，所以一段散文不能把断言喂饱。
    private static string ClientResultsCode(string source)
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

    private static string ClientResultsMethodBody(string source, string signature)
    {
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

    private static int ClientResultsCount(string source, string needle)
    {
        int count = 0, at = -1;
        while ((at = source.IndexOf(needle, at + 1, StringComparison.Ordinal)) >= 0) count++;
        return count;
    }
}
