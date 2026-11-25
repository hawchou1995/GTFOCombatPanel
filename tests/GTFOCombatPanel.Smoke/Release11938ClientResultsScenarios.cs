using System;
using HawChow.GTFOCombatPanel;

// 1.19.45（用户纠正）：客机失败页的条带下界 = 那行不可点击的「正在等待厅主」提示文字。
//
// 1.19.38 当年把这条规则反过来了：下界改取页面固定几何（本机 1429），等待文字只进日志。理由是那行字会把条带
// 压成 370px（30 行只剩 8px 字号）。真机日志（2560x1440，`LogOutput`）后来给出了相反的证据：
//
//   run 2（L3926–L3928）  waitingText=1207，strip=(488..1186)，room=698px —— 板子从来没有压在那行字上；
//   run 3（L12649–L12657） band=(488..1429) 是按屏幕下界编的，900 高的板压住 1177 的控件，`TryHold` 抬不动，
//                          整页 0.55 不透明度。
//
// 也就是说：那行字不是障碍，它就是墙——和主机页「返回大厅」按钮的上沿同一性质，只是它不可点击。
// 1.19.45 的口径（本文件逐条驱动生产类型）：
//
//   1. 客机失败页的条带下界 = 等待文字自己的上沿减 clearance（本机 1228 → 1206.67），页面固定几何只做封顶；
//   2. 真正可按的控件照旧是墙，两者取更靠上（更安全）的那一堵；
//   3. 同一页面几何下首帧即最终尺寸；那行字按页面动画移动时条带跟着它走（它*就是*墙），且永远不会被压住；
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
        Console.WriteLine("---- 1.19.45: the client failure strip's lower bound is the waiting line itself ----");
        TheClientFailureStripEndsAtTheLine(Check);
        TheFontTheHonestStripPaysFor(Check);
        TheFirstFrameIsTheFinalSize(Check);
        TheHostPathIsUntouched(Check);
        TheWiringAfter1_19_45(Check);
    }

    // ---- 1: 条带下界 = 那行字自己的上沿减 clearance；页面脚只做封顶 -----------------------------------------
    private static void TheClientFailureStripEndsAtTheLine(Action<bool, string> check)
    {
        check(ResultsGuestPageGeometry.TryResolve(MessageBottom, ClientScreenWidth, ClientScreenHeight, MessageGap,
                MinimumGuestStripHeight, out ResultsSafeArea fixedPage),
            "the client page's fixed geometry is resolved from the recorded title edge (messageBottom=815) on a 2560x1440 window");
        check(near(fixedPage.Top, 836.333f, 0.01f) && near(fixedPage.Bottom, 1429.333f, 0.01f),
            $"and it is ({fixedPage.Top:0.##}..{fixedPage.Bottom:0.##}) - the 837..1429 the recorded log printed under `footerSource=client-fixed-page-geometry`");
        check(near(fixedPage.Bottom, 1429.333f, 0.01f)
              && near(ClientScreenHeight - fixedPage.Bottom, 10.667f, 0.01f),
            "its lower edge is 1429.333px, leaving the fixed 10.667px foot of a 2560x1440 window - the 1080p reference foot scaled up, and from this release a cap only");

        check(ResultClientFailureBand.TryResolve(fixedPage, ClientScreenHeight, MinimumGuestStripHeight,
                WaitingTextTop, float.NaN, float.NaN, out ResultClientBandReading clientBand),
            "the client failure band is resolved with the measured waiting line (1228) and no pressable control");
        check(near(clientBand.Top, 836.333f, 0.01f) && near(clientBand.Bottom, 1206.667f, 0.01f)
              && near(clientBand.Height, 370.333f, 0.01f),
            $"so its room is {clientBand.Height:0.##}px ({clientBand.Top:0.##}..{clientBand.Bottom:0.##}): the page's own top down to the line's own top less the clearance - never the 593px the screen's foot offered");
        check(near(ResultsBounds.Clearance(ClientScreenHeight), 21.333f, 0.01f),
            "the clearance is the fixed 21.333px literal for a 2560x1440 window (16 reference units at 1080p, scaled up)");
        check(clientBand.WaitingTextTightened && !clientBand.PressableTightened && clientBand.WallMeasured
              && near(clientBand.WallTop, WaitingTextTop, 0.001f)
              && near(clientBand.WaitingTextTop, WaitingTextTop, 0.001f),
            "no control the player can press was measured on this page, so the waiting line itself is the wall the foot came from and the reading says so");
        check(!clientBand.WaitingTextCovered,
            "and the board's band stops a clearance above that line, so the text the player is reading is never covered");

        // 反例：1.19.38–1.19.44 的规则（下界取屏幕脚）在同一张页面上得到 593px，而板子会压在那行字上。
        bool removed = ResultsBounds.TryResolve(ClientScreenHeight, MessageBottom, ResultBoundSource.TitleAnchor,
            float.NaN, float.NaN, float.NaN, out ResultsBand removedBand);
        check(removed && near(removedBand.Bottom, 1432f, 0.01f) && near(removedBand.Height, 595.667f, 0.05f)
              && removedBand.BottomSource == ResultBoundSource.ScreenFoot,
            $"the rule this release removes, in the same numbers: with no wall at all the shared arithmetic runs to the window's foot ({removedBand.Bottom:0.##}, {removedBand.Height:0.##}px) - and the page's own foot then capped it at the 593px band the run-3 board was drawn into");
        var twelvePxBoard = new LayoutRect(0f, 836.333f, 1160f, 45f * 12f);
        var waitingLine = new LayoutRect(0f, WaitingTextTop, ClientScreenWidth, 40f);
        check(ResultInteractionStripPolicy.OverlapsBoard(waitingLine, twelvePxBoard, ResultBoardHold.ButtonClearance),
            "a board measured into that taller band really does overlap the waiting line, which is why the per-repaint refusal used to take the whole board away - the correction removes the cause instead of the board");

        // 扫描自己也要分得清「那行字」和「可按的控件」：同一个列表里，FooterTop 可以是文字，PressableTop 不能。
        var textOnly = new[] { new ResultActionBounds(new LayoutRect(0f, WaitingTextTop, ClientScreenWidth, 40f), true) };
        check(ResultInteractionStripPolicy.TryResolve(MessageBottom, MessageGap, textOnly, out ResultInteractionStrip textStrip)
              && near(textStrip.FooterTop, WaitingTextTop, 0.01f) && !float.IsFinite(textStrip.PressableTop),
            $"the waiting line is the strip's footer ({textStrip.FooterTop:0.##}) but it is not a pressable control at all, so `PressableTop` stays unmeasured - the band reads the line from its own rectangle, not from the pressable set");
        var textAndButton = new[]
        {
            new ResultActionBounds(new LayoutRect(0f, WaitingTextTop, ClientScreenWidth, 40f), true),
            new ResultActionBounds(new LayoutRect(0f, 1000f, 400f, 60f))
        };
        check(ResultInteractionStripPolicy.TryResolve(MessageBottom, MessageGap, textAndButton, out ResultInteractionStrip mixedStrip)
              && near(mixedStrip.FooterTop, 1000f, 0.01f) && near(mixedStrip.PressableTop, 1000f, 0.01f)
              && mixedStrip.FooterActionCount == 1,
            $"with a real control above the line both figures name the control ({mixedStrip.PressableTop:0.##}) and the line no longer counts as an action - the one place the two walls are told apart");

        // 那条更高的控件：条带收紧到它上方（更安全的一边）。
        check(ResultClientFailureBand.TryResolve(fixedPage, ClientScreenHeight, MinimumGuestStripHeight,
                WaitingTextTop, 1000f, float.NaN, out ResultClientBandReading tightened)
              && tightened.PressableTightened && !tightened.WaitingTextTightened
              && near(tightened.Bottom, 1000f - ResultsBounds.Clearance(ClientScreenHeight), 0.01f),
            $"a pressable control that reaches higher than the line pulls the band above it ({tightened.Bottom:0.##}): the higher wall wins, because it is the safer edge");

        // 一条连最小板高都留不出的条带：拒收（原版页面保留），而不是退回屏幕脚。
        check(!ResultClientFailureBand.TryResolve(fixedPage, ClientScreenHeight, MinimumGuestStripHeight,
                WaitingTextTop, 900f, float.NaN, out ResultClientBandReading tooTight)
              && !tooTight.Fits,
            $"a pressable control at 900 would leave {900f - ResultsBounds.Clearance(ClientScreenHeight) - 836.333f:0.##}px, under the {MinimumGuestStripHeight:0}px client minimum: the band is refused instead of being invented from the page's foot");
    }

    // ---- 2: 30 行在诚实条带里拿到的字号 ---------------------------------------------------------------
    private static void TheFontTheHonestStripPaysFor(Action<bool, string> check)
    {
        int analytic = ResultsBandFit.FontForBand(370.333f, Rows, PanelDensity.Compact, int.MaxValue);
        check(analytic == 8,
            $"the analytic mapping answers {analytic}px for a 370.33px band over 30 rows at the compact density (floor(370.33 / 30 / 1.5)) - the recorded page's own 8px, because the band really is that tall");
        int floor = ResultsBandFit.FloorForBand(370.333f, Rows, PanelDensity.Compact,
            ResultsFontGrowth.ReadabilityFloor(float.NaN), out bool squeezed);
        check(floor == ResultsFontGrowth.MinimumReadablePixels && !squeezed,
            $"a client page measures no pressable button, so the readability floor stays the absolute one ({floor}px) and a 370px band does not squeeze it");
        bool footIsWall = ResultsFontGrowth.FootIsTheWall(1206.667f, WaitingTextTop, MessageGap);
        float ceiling = ResultsFontGrowth.FillCeiling(ResultFooterSource.ClientNoFooterSafeArea, footIsWall);
        check(footIsWall && near(ceiling, 1f, 0.0001f),
            $"the strip's foot is the waiting line - a measured wall - so the band may be filled to its own foot ({ceiling:0.00} of it): nothing under that foot is left for a comfort ceiling to protect");
        int font = ResultsFontGrowth.FitBoardIntoStrip(Seed, floor, int.MaxValue, 370.333f, BoardWidth, Natural(Rows),
            out int usedFloor, ceiling);
        ResultTableSize size = Natural(Rows)(font);
        Console.WriteLine($"CLIENT-STRIP: room=370.33px glyph={font}px board={size.Width:0}x{size.Height:0} (floor {usedFloor}px, ceiling {ceiling:0.00}, footIsTheWall={footIsWall.ToString().ToLowerInvariant()})"
            + $" | recorded 8px 435x360");
        check(font == 8 && usedFloor == ResultsFontGrowth.MinimumReadablePixels && near(size.Height, 45f * font, 0.01f),
            $"and it answers the recorded 8px glyph with a {size.Width:0}x{size.Height:0} table - the same surface the live page drew, now in a band whose foot is a wall the page really draws");
        check(size.Height <= 370.333f + 0.5f,
            $"the board ({size.Height:0}px) is inside the 370.33px band, so nothing is drawn on the waiting line");

        // The board the fit produces, placed by the production placement.
        var strip = new BoardStrip(836.333f, 1206.667f, false, float.NaN, float.NaN, true,
            ResultFooterSource.ClientNoFooterSafeArea, ceiling);
        var waitingLine = new LayoutRect(0f, WaitingTextTop, ClientScreenWidth, 40f);
        check(ResultBoardHold.TryPlace(ClientScreenWidth, ClientScreenHeight, strip, size.Width, size.Height, 0f,
                out LayoutRect placed) && near(placed.Y, 836.333f, 0.01f) && near(placed.Bottom, 836.333f + size.Height, 0.01f),
            $"the table hangs from the band's own top ({placed.Y:0.##}..{placed.Bottom:0.##}), one placement, no step-up later");
        check(!ResultInteractionStripPolicy.OverlapsBoard(waitingLine, placed, ResultBoardHold.ButtonClearance)
              && placed.Bottom <= 1206.667f + 0.5f,
            $"and it stands clear of the waiting line at {WaitingTextTop:0} - no per-repaint refusal is ever provoked by the text the player is reading");
    }

    // ---- 3: 首帧即最终大小；那行字移动时条带跟着它 ------------------------------------------------------
    private static void TheFirstFrameIsTheFinalSize(Action<bool, string> check)
    {
        bool ok0 = ResultClientFailureBand.TryResolve(FixedPage(), ClientScreenHeight, MinimumGuestStripHeight,
            WaitingTextTop, float.NaN, float.NaN, out ResultClientBandReading t0);
        // t+2s: 同一页面几何、同一分辨率，逐帧重复 120 次（2 秒的 60Hz 重绘），每一次都必须逐像素相同。
        bool same = ok0;
        int distinct = 0;
        for (int frame = 0; frame < 120; frame++)
        {
            if (!ResultClientFailureBand.TryResolve(FixedPage(), ClientScreenHeight, MinimumGuestStripHeight,
                    WaitingTextTop, float.NaN, float.NaN, out ResultClientBandReading later)
                || !near(later.Top, t0.Top, 0.0001f) || !near(later.Bottom, t0.Bottom, 0.0001f))
                same = false;
            distinct++;
        }
        check(same && distinct == 120,
            "120 repaints of the same page geometry answer the same strip, to the pixel - there is no second, smaller first frame to grow out of");

        // 等待文字从 1228 移到 1258：那行字就是墙，所以条带跟着它走，而且只走这一次、只收不放。
        bool movedText = ResultClientFailureBand.TryResolve(FixedPage(), ClientScreenHeight, MinimumGuestStripHeight,
            1258f, float.NaN, float.NaN, out ResultClientBandReading at1258);
        check(movedText && near(at1258.Top, t0.Top, 0.0001f)
              && near(at1258.Bottom, 1258f - ResultsBounds.Clearance(ClientScreenHeight), 0.01f)
              && near(at1258.Height - t0.Height, 30f, 0.02f),
            $"the waiting line moving 30px down (1228 -> 1258) moves the band's foot by exactly that 30px ({t0.Height:0.##}px -> {at1258.Height:0.##}px): the band follows the wall the page really draws, and the text is still never covered");

        // 同一个字号、同一个板宽：格子仍然一次到位（条带的差只是一行字的 30px）。
        float ceiling = ResultsFontGrowth.FillCeiling(ResultFooterSource.ClientNoFooterSafeArea, true);
        int floor = ResultsBandFit.FloorForBand(t0.Height, Rows, PanelDensity.Compact,
            ResultsFontGrowth.ReadabilityFloor(float.NaN), out _);
        int font0 = ResultsFontGrowth.FitBoardIntoStrip(Seed, floor, int.MaxValue, t0.Height, BoardWidth, Natural(Rows), out _, ceiling);
        int font1 = ResultsFontGrowth.FitBoardIntoStrip(Seed, floor, int.MaxValue, at1258.Height, BoardWidth, Natural(Rows), out _, ceiling);
        ResultTableSize size0 = Natural(Rows)(font0), size1 = Natural(Rows)(font1);
        check(font0 == font1 && near(size0.Width, size1.Width, 0.001f) && near(size0.Height, size1.Height, 0.001f),
            $"and the grid is the same one: {font0}px, {size0.Width:0}x{size0.Height:0} both samples - a 30px line move does not buy a glyph pixel, so the first frame the anchor settled is the final size");

        // 漂移规则：条带真的动了 30px，`ResultAnchorDrift` 记一次、两次读数之后花掉一次重测预算。
        var drift = new ResultAnchorDrift();
        drift.Record(0, t0.Top, t0.Height);
        check(ResultAnchorDrift.Moved(at1258.Height, t0.Height)
              && !drift.Due(1, at1258.Top, at1258.Height)
              && drift.Due(2, at1258.Top, at1258.Height) && drift.Recoveries == 0,
            "the 30px move is a real move of the wall, so the drift budget sees it: one reading is not the page moving, the second one spends the page's single re-measure instead of re-fitting on every frame of the animation");
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

        // 主机在「按钮量不到」时仍然被等待文字夹紧 —— 客机现在也这么做，两个角色终于同一条规则。
        bool hostNoButton = ResultsBounds.TryResolve(ClientScreenHeight, MessageBottom, ResultBoundSource.TitleAnchor,
            float.NaN, WaitingTextTop, float.NaN, out ResultsBand hostNoButtonBand);
        check(hostNoButton && near(hostNoButtonBand.Bottom, 1206.667f, 0.01f)
              && hostNoButtonBand.BottomSource == ResultBoundSource.FooterText,
            $"a host page whose button could not be measured takes the waiting/footer text as its wall ({hostNoButtonBand.Bottom:0.##}) - and from this release the client page reads the same kind of fact the same way");
    }

    // ---- 5: 接线（源码串检查，不是运行时证明） --------------------------------------------------------
    private static void TheWiringAfter1_19_45(Action<bool, string> check)
    {
        string root = ClientResultsSourceRoot();
        check(root != null, "the client results regression locates the production sources");
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
            check(strip.Contains("ResultClientFailureStrip.Decide(Time.frameCount, ClientWall,")
                  && strip.Contains("float pressableTop = ResultsRegion.HighestTop(namedTop, controls.PressableTop);")
                  && resolve != null
                  && resolve.Contains("ResultClientFailureBand.TryResolve(fixedPage, evidence.ScreenHeight, evidence.MinimumStripHeight,")
                  && resolve.Contains("evidence.SlotTop, evidence.PressableTop, wallTop, out reading)"),
                "the client failure branch takes its band from `ResultClientFailureBand`, handing it both walls - the waiting line's own top and the highest pressable control - plus the highest wall this page has shown (static check, not a runtime proof)");
            check(ClientResultsCount(strip, "ResultsBounds.TryResolve(") == 1,
                "and the waiting line is passed to `ResultsBounds.TryResolve` in exactly one place - the host's `else if` - so no client frame can reach that arithmetic (static check, not a runtime proof)");
            int guestAt = strip.IndexOf("if (GuestClient())", StringComparison.Ordinal);
            int boundsAt = strip.IndexOf("ResultsBounds.TryResolve(", StringComparison.Ordinal);
            int guestReport = strip.IndexOf("ReportBand(\"failure\", new ResultsBand(top, bottom, bandTopSource, bandBottomSource, false),", StringComparison.Ordinal);
            check(guestAt >= 0 && guestReport > guestAt && boundsAt > guestReport,
                "the client's own band is reported before the host's `ResultsBounds` call, so the two branches cannot be taken in the same frame (static check, not a runtime proof)");
            // 1.19.61：客机的页脚报的是「这条条带自己用的那一堵墙」（`clientBand.WallTop`）。1.19.60 报的是门保留的
            // 墙（`ClientWall.Wall`），于是同一帧里条带可以按一个数落位、`BoardStillSafe` 的页脚校验却按另一个数判它
            // （2026-11-25 快照 L4881 条带 1371 / L4883 footerTop=1338），板只能靠 F8 强绘一帧。
            check(strip.Contains("float clientWall = clientBand.WallTop;") && strip.Contains("buttonTop = clientWall;")
                  && strip.Contains("buttonMeasured = float.IsFinite(clientWall);")
                  && strip.Contains("footIsWall = clientBand.WallMeasured;"),
                "on a client the measured footer is the very wall the band's own foot was cut from - the slot's layout top, the slot's visible glyphs or a pressable control, whichever reaches higher - so the measurement and the per-repaint check cannot name two different boundaries (static check, not a runtime proof)");
            check(strip.Contains("float clientSlotTop = slot.Top;")
                  && strip.Contains("ResultClientSlotEvidence slot = ClientSlot(page);"),
                "and the bound comes from the production slot reading - the client's own return slot plus the waiting line's rendered glyphs - so a layout box that sits below the text it draws cannot become the board's lower bound (static check, not a runtime proof)");
            check(strip.Contains("clientBand.WaitingTextTightened") && strip.Contains("ResultBoundSource.FooterText"),
                "and the log line names which of the two walls produced the foot (static check, not a runtime proof)");
        }

        string scan = ClientResultsMethodBody(display, "private static FooterControls ScanFooterControls(");
        check(scan != null, "the interaction scan is found in the production source (static check, not a runtime proof)");
        if (scan != null)
        {
            check(scan.Contains("if (inspectBoard && !client && ResultInteractionStripPolicy.OverlapsBoard(rect, board, ResultBoardHold.ButtonClearance))"),
                "the waiting-status block does not raise the overlap flag on a client, so a board that strays onto it is never withdrawn by that flag (static check, not a runtime proof)");
            check(ClientResultsCount(scan, "if (inspectBoard && ResultInteractionStripPolicy.OverlapsBoard(rect, board, ResultBoardHold.ButtonClearance))") == 1,
                "while every control that can really be pressed keeps the ungated overlap refusal it always had - one occurrence, in `AddControl`, with no role test (static check, not a runtime proof)");
            check(scan.Contains("actions.Add(new ResultActionBounds(rect, protectedPageText: true));"),
                "and the waiting line is still measured and still joins the strip's own facts, so the log can still name it (static check, not a runtime proof)");
        }

        // 1.19.39: the failure page's resolution lives in the shared entry point this branch asks.
        int guestGeometryCalls = ClientResultsCount(display, "ResultsGuestPageGeometry.TryResolve");
        int guestGeometryViaEntryPoint = ClientResultsCount(ClientResultsCode(geometrySource), "ResultsGuestPageGeometry.TryResolve");
        check(guestGeometryCalls == 1 && guestGeometryViaEntryPoint == 1
              && ClientResultsCount(display, "ResultClientFailureStrip.Decide(") == 1
              && ClientResultsCount(display, "ResultClientFailureStrip.Measure(") == 1,
            $"the fixed page geometry is still resolved exactly twice - the client success page in the display and the client failure page inside the entry point it asks (display={guestGeometryCalls}, entryPoint={guestGeometryViaEntryPoint}; static check, not a runtime proof)");
        check(display.Contains("if (!BoardStillSafe()") && display.Contains("DeferBoardForInteraction();"),
            "the per-repaint safety net and the deferral it triggers are still in the draw path (static check, not a runtime proof)");
        check(display.Contains("Drift.Record(Time.frameCount, strip.Top, strip.Bottom - strip.Top);"),
            "the drift sample is still recorded from the strip the board was placed into (static check, not a runtime proof)");

        // 客机成功页：它本来就是固定几何，这里确认它没有同类的二次夹紧（不改行为，只加断言）。
        string success = ClientResultsMethodBody(display, "private static bool SuccessRegion(");
        check(success != null, "the success region is found in the production source (static check, not a runtime proof)");
        if (success != null)
        {
            check(success.Contains("controls = ScanFooterControls(page, completion, MessageGap, null, board, inspectBoard, boardColumn);"),
                "the client success page never nominates the waiting line as protected page text - it passes `null` - so its tightening can only come from a control the player can press (static check, not a runtime proof)");
            check(success.Contains("ResultsGuestPageGeometry.TryResolve(completion, Screen.width, Screen.height,"),
                "and its fixed geometry is resolved from the completion edge with no text-derived second clamp (static check, not a runtime proof)");
        }

        check(geometrySource.Contains("public static class ResultClientFailureBand")
              && geometrySource.Contains("public readonly struct ResultClientBandReading"),
            "the client failure band lives in `GuestPageGeometry.cs` beside the fixed page geometry it refines (static check, not a runtime proof)");
        check(policySource.Contains("public sealed class ResultAnchorDrift"),
            "and the drift budget is still the one page-wide budget (static check, not a runtime proof)");
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private static ResultsSafeArea FixedPage()
        => ResultsGuestPageGeometry.TryResolve(MessageBottom, ClientScreenWidth, ClientScreenHeight, MessageGap,
            MinimumGuestStripHeight, out ResultsSafeArea area)
            ? area
            : default;

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
