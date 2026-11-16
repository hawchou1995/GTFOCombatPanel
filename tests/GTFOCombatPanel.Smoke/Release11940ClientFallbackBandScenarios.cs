using System;
using System.IO;
using HawChow.GTFOCombatPanel;

// 客机失败页的条带：下界是那行不可点击的提示文字，两条路径读同一份事实（1.19.45 口径）。
//
// 独立评审记下来的那张 2560x1440 客机失败页上，同一帧的事实被两个分支读成了两条带：
//
//   `ResultsDisplay.FailureStrip` 的客机分支（严格路径）：836.33..1206.67（= 那行字 1228 减 clearance 21.33）
//   `ResultsDisplay.FallbackPlacement`（fail-open）：同一帧、同一份读数，却因为扫描里含那行字的 `FooterTop`
//   被当成「页脚控件」、而 `ResultPageLowerBoundPolicy` 又把那行字丢掉，得到另一条带。
//
// 1.19.38 起严格分支把下界改成了「页面固定几何 1429」，于是同一张页面严格 836..1429（593px）、兜底 836..1207
// （370px）——两个说法。1.19.45（用户纠正）把口径定死在那行字上：它是这一页的墙，固定几何只做封顶。
// 于是两条路径又回到同一个数字，而且这个数字就是玩家真正看到的几何。
//
// 这个文件逐条驱动生产类型：`ResultPageLowerBoundPolicy`、`ResultFallbackBandPolicy`、`ResultsBounds`、
// `ResultFallbackLayout`、`ResultClientFailureStrip`、`ResultsBandFit`、`ResultBoardHold`。
// 没有任何一行在这里跑 Unity；只存在于 `ResultsDisplay` 源码里的接线用源码串检查，并在描述里注明
// 「这不是运行时证明」。
internal static partial class Program
{
    // 记录页面的真机数字（2560x1440 客机，一次失败局）。
    private const float FallbackScreenWidth = 2560f, FallbackScreenHeight = 1440f;
    private const float FallbackMessageBottom = 815f;      // 任务失败标题自己的布局底边
    private const float FallbackWaitingTextTop = 1228f;    // m_returnToLobby_text 自己的布局上沿
    private const float FallbackMessageGap = 16f;          // ResultsDisplay.MessageGap
    private const float FallbackMinimumStrip = 96f;        // ResultsDisplay.MinimumGuestStripHeight
    private const int FallbackRows = 30;                   // ImpactBoard.RowCount(true)
    // 1.19.37/1.19.38 记录的主机形状：标题 467、按钮 1151。
    private const float FallbackHostTitleEdge = 467f, FallbackHostButtonTop = 1151f;

    private static void RunRelease11940ClientFallbackBandScenarios()
    {
        Console.WriteLine("---- 1.19.45: 客机失败页的条带下界 = 那行提示文字（两条路径同一份事实） ----");
        TheClientFallbackIsTheStrictBand(Check);
        ARealControlStillBoundsTheFallback(Check);
        AnIncompleteScanStillRefusesTheBudget(Check);
        ATitleNearTheFootStillAnswers(Check);
        TheHostPageIsUntouched(Check);
        TheBandWiringAfter1_19_45(Check);
    }

    // ---- 1: 同一张客机失败页，兜底与严格落位是同一条条带 ------------------------------------------------
    private static void TheClientFallbackIsTheStrictBand(Action<bool, string> check)
    {
        float floor = FallbackBoardFloor();
        float clearance = ResultsBounds.Clearance(FallbackScreenHeight);
        check(near(floor, 270f, 0.5f),
            $"the board's own floor on this page is {floor:0}px (30 rows at the 6px readable floor), the figure the recorded log printed as boardFloor=270px");

        ResultPageWalls walls = ResultPageLowerBoundPolicy.Resolve(true, FallbackScreenHeight, float.NaN, float.NaN,
            FallbackWaitingTextTop, ResultBoundSource.None);
        check(walls.GuestFailure && near(walls.FooterTextTop, FallbackWaitingTextTop)
              && walls.FooterTextSource == ResultBoundSource.FooterText
              && !float.IsFinite(walls.PressableTop) && near(walls.PageFloor, 1429.333f, 0.01f),
            $"a client failure page hands the waiting line over as its footer wall ({walls.FooterTextTop:0}), reports no pressable control, and keeps its screen-derived foot ({walls.PageFloor:0.###}) as the band's cap");

        // 那行字就是墙：条带的下沿由它自己的上沿减 clearance 得来，页面脚再高也夹不回来。
        check(ResultsBounds.TryResolve(FallbackScreenHeight, FallbackMessageBottom, ResultBoundSource.TitleAnchor,
                float.NaN, FallbackWaitingTextTop, walls.FooterTextSource, float.NaN, out ResultsBand lineBand)
              && near(lineBand.Top, 836.333f, 0.02f) && near(lineBand.Bottom, FallbackWaitingTextTop - clearance, 0.02f)
              && near(lineBand.Height, 370.333f, 0.03f) && lineBand.BottomSource == ResultBoundSource.FooterText,
            $"the shared arithmetic answers the line's own edge ({lineBand.Top:0.###}..{lineBand.Bottom:0.###} = {lineBand.Height:0.##}px, source={ResultsBounds.Name(lineBand.BottomSource)}): the recorded band, and never the 593px the page's screen foot offered instead");

        bool resolved = ResultFallbackBandPolicy.TryResolve(FallbackScreenHeight, FallbackMessageBottom,
            ResultBoundSource.TitleAnchor, walls, float.NaN, float.NaN, float.NaN, floor, out ResultFallbackPlan plan);
        Console.WriteLine($"CLIENT-FAIL-FALLBACK: reason={FallbackReasonName(plan)} band=({plan.Top:0.##}..{plan.Bottom:0.##})"
            + $" room={plan.Height:0.##}px opacity={plan.Opacity:0.00} top={ResultsBounds.Name(plan.Band.TopSource)}"
            + $" bottom={ResultsBounds.Name(plan.Band.BottomSource)}"
            + $" | glyph {ResultsBandFit.FontForBand(plan.Height, FallbackRows, PanelDensity.Compact, 0)}px");
        check(resolved && plan.PageBand && near(plan.Top, 836.333f, 0.01f) && near(plan.Bottom, 1206.667f, 0.01f)
              && near(plan.Height, 370.333f, 0.01f) && near(plan.Opacity, 1f, 0.001f)
              && plan.Band.BottomSource == ResultBoundSource.FooterText,
            $"the fail-open band is the page's own ({plan.Top:0.##}..{plan.Bottom:0.##}) = {plan.Height:0.##}px at full strength, foot={ResultsBounds.Name(plan.Band.BottomSource)}");

        // 严格路径在同一张页面上回答同一个数字：同一份 `ResultClientFailureStrip` 生产入口。
        var gate = new ResultClientWallGate();
        var evidence = new ResultClientFailureEvidence(FallbackMessageBottom, FallbackScreenWidth, FallbackScreenHeight,
            FallbackMessageGap, FallbackMinimumStrip, FallbackWaitingTextTop, float.NaN);
        ResultClientStripState first = ResultClientFailureStrip.Decide(0, gate, evidence,
            out ResultClientBandReading _, out ResultsSafeArea _);
        ResultClientStripState second = ResultClientFailureStrip.Decide(1, gate, evidence,
            out ResultClientBandReading strict, out ResultsSafeArea strictPage);
        check(first == ResultClientStripState.WallNotSettled && second == ResultClientStripState.Placed,
            $"the strict path still asks its two-frame wall question first ({ResultClientFailureStrip.Name(first)} then {ResultClientFailureStrip.Name(second)})");
        check(near(strict.Top, plan.Top, 0.01f) && near(strict.Bottom, plan.Bottom, 0.01f)
              && near(strict.Height, plan.Height, 0.01f),
            $"and it answers ({strict.Top:0.##}..{strict.Bottom:0.##}) = {strict.Height:0.##}px: the strict band and the fail-open band are one band");
        check(near(strictPage.Bottom, walls.PageFloor, 0.01f) && near(strictPage.Top, plan.Top, 0.01f)
              && strict.WaitingTextTightened && !strict.PressableTightened && !strict.WaitingTextCovered,
            $"both come from `ResultsGuestPageGeometry` (page {strictPage.Top:0.##}..{strictPage.Bottom:0.##}, whose foot only caps), and the foot is the line's own top - so the board never stands on the text the player is reading");

        // 字号：条带高度决定字号，而这条带不是屏幕下界编出来的那一条。
        int font = ResultsBandFit.FontForBand(plan.Height, FallbackRows, PanelDensity.Compact, 0);
        ResultTableSize board = FallbackNatural(FallbackRows)(font);
        check(board.Height <= plan.Height + 0.5f,
            $"the {plan.Height:0.##}px band fits a {font}px table ({board.Width:0}x{board.Height:0}), so the board is sized from its own band as it always is");

        // 那行字按页面自己的动画移动：条带跟着它移动，因为它就是墙；而它永远不会被压住。
        bool followsTheLine = true, neverOver = true;
        for (int frame = 0; frame < 120; frame++)
        {
            float text = FallbackWaitingTextTop + (frame % 2 == 0 ? 0f : 30f);
            if (!ResultFallbackBandPolicy.TryResolve(FallbackScreenHeight, FallbackMessageBottom,
                    ResultBoundSource.TitleAnchor,
                    ResultPageLowerBoundPolicy.Resolve(true, FallbackScreenHeight, float.NaN, float.NaN, text,
                        ResultBoundSource.None),
                    float.NaN, float.NaN, float.NaN, floor, out ResultFallbackPlan repaint)
                || !near(repaint.Bottom, text - clearance, 0.02f)) followsTheLine = false;
            else if (repaint.Bottom > text - clearance + 0.02f) neverOver = false;
        }
        check(followsTheLine && neverOver,
            "120 repaints of a page whose waiting line alternates between 1228 and 1258 answer a band whose foot is always that line's own top less the clearance: the band follows the wall the page really draws, and it never crosses it");
    }

    // ---- 2: 真实可按的控件仍然是墙、仍然被绕开 ------------------------------------------------------------
    private static void ARealControlStillBoundsTheFallback(Action<bool, string> check)
    {
        float floor = FallbackBoardFloor();
        float clearance = ResultsBounds.Clearance(FallbackScreenHeight);
        // 真机探针：扫描量到一个可按控件在 1130（比那行字 1228 更高）。它上方仍留得下板子的自有高度（270px），
        // 所以这条带会被采纳；一个更靠下的控件（例如 1176）什么也不改变。
        ResultPageWalls walls = ResultPageLowerBoundPolicy.Resolve(true, FallbackScreenHeight, float.NaN, 1130f,
            FallbackWaitingTextTop, ResultBoundSource.None);
        check(near(walls.PressableTop, 1130f, 0.01f) && near(walls.FooterTextTop, FallbackWaitingTextTop)
              && near(walls.PageFloor, 1429.333f, 0.01f),
            "a measured pressable control sits beside the waiting line in the readings (1130 vs 1228), and the page's foot is still 1429.333 - as a cap");

        bool resolved = ResultFallbackBandPolicy.TryResolve(FallbackScreenHeight, FallbackMessageBottom,
            ResultBoundSource.TitleAnchor, walls, float.NaN, float.NaN, float.NaN, floor,
            out ResultFallbackPlan plan);
        check(resolved && plan.PageBand && near(plan.Bottom, 1130f - clearance, 0.02f)
              && plan.Band.BottomSource == ResultBoundSource.FooterControl,
            $"the band stops above the higher of the two walls ({plan.Top:0.##}..{plan.Bottom:0.##}, foot={ResultsBounds.Name(plan.Band.BottomSource)}): the safer edge wins, exactly as `ResultsBounds` documents");
        var gate = new ResultClientWallGate();
        var evidence = new ResultClientFailureEvidence(FallbackMessageBottom, FallbackScreenWidth, FallbackScreenHeight,
            FallbackMessageGap, FallbackMinimumStrip, FallbackWaitingTextTop, 1130f);
        ResultClientFailureStrip.Decide(0, gate, evidence, out ResultClientBandReading _, out ResultsSafeArea _);
        ResultClientFailureStrip.Decide(1, gate, evidence, out ResultClientBandReading strict, out ResultsSafeArea _);
        check(strict.PressableTightened && near(strict.Bottom, plan.Bottom, 0.01f),
            $"and the strict page agrees to the pixel ({strict.Bottom:0.###}): the higher wall, whichever of the two it is, is what shortens the band");

        // 板子放进这条带里就碰不到那个控件。
        int font = ResultsBandFit.FontForBand(plan.Height, FallbackRows, PanelDensity.Compact, 0);
        ResultTableSize board = FallbackNatural(FallbackRows)(font);
        check(board.Height <= plan.Height + 0.5f,
            $"the {plan.Height:0.##}px band fits a {font}px table ({board.Width:0}x{board.Height:0})");
        check(ResultFallbackLayout.TryPlaceInBand(FallbackScreenWidth, FallbackScreenHeight, plan.Top, plan.Bottom,
                board.Width, board.Height, 0f, out LayoutRect placed)
              && placed.Bottom <= 1130f - ResultBoardHold.ButtonClearance,
            $"and it stands at {placed.Y:0.##}..{placed.Bottom:0.##}, clear of the control at 1130 by more than the {ResultBoardHold.ButtonClearance:0}px the repaint check keeps");

        // 一条装不下板子自有高度的带：编出来的那一层把控件当墙，并把「跨过了什么」如实写出来。
        bool squeezed = ResultFallbackBandPolicy.TryResolve(FallbackScreenHeight, FallbackMessageBottom,
            ResultBoundSource.TitleAnchor, walls, float.NaN, float.NaN, float.NaN, floor + 200f,
            out ResultFallbackPlan extended);
        check(squeezed && extended.Reason == ResultFallbackReason.OverlapsControls && extended.Extended
              && near(extended.Opacity, ResultFallbackLayout.OverlapOpacity, 0.001f)
              && extended.Crosses.Length > 0,
            $"when even the page's own band cannot hold the floor, the invented band grows past a named wall and says so (`covers={extended.Crosses}`) at {extended.Opacity:0.00} opacity - reported, never silent");
    }

    // ---- 3: 扫描看不全 / F8 合同照旧 ----------------------------------------------------------------------
    private static void AnIncompleteScanStillRefusesTheBudget(Action<bool, string> check)
    {
        check(ResultFallbackPolicy.PageReadable(true, true, 0)
              && !ResultFallbackPolicy.PageReadable(false, true, 0)
              && !ResultFallbackPolicy.PageReadable(true, false, 0)
              && !ResultFallbackPolicy.PageReadable(true, true, 1),
            "readability is still the scan's own three facts - complete, parent coverage complete, no active control left without a rectangle");
        check(!ResultFallbackPolicy.MayAnswer(ResultFallbackPolicy.MissBudget, false, false)
              && !ResultFallbackPolicy.MayAnswer(ResultFallbackPolicy.MissBudget * 10, false, false),
            "sixty refusals - or six hundred - buy no board on a page the scan could not read: the band arithmetic never sees that page at all");
        check(ResultFallbackPolicy.MayAnswer(0, true, false) && ResultFallbackPolicy.MayAnswer(ResultFallbackPolicy.MissBudget, false, true),
            "F8 is still the player's own authorization on such a page, and a page the scan read whole still gets the budget");

        // 一个 active 却量不到矩形的控件：墙的问题没有被回答，严格路径不交条带。
        var gate = new ResultClientWallGate();
        var blocked = new ResultClientFailureEvidence(FallbackMessageBottom, FallbackScreenWidth, FallbackScreenHeight,
            FallbackMessageGap, FallbackMinimumStrip, FallbackWaitingTextTop, float.NaN,
            scanComplete: true, parentCoverageComplete: true, unmeasurableControls: 1);
        check(ResultClientFailureStrip.Decide(0, gate, blocked, out _, out _) == ResultClientStripState.WallNotSettled
              && ResultClientFailureStrip.Decide(1, gate, blocked, out _, out _) == ResultClientStripState.WallNotSettled,
            "an active control whose rectangle could not be read keeps the strict page on `no wall answered`, however many frames it lasts");

        // 一页连那行字都量不到：条带不交，也不会退回屏幕下界。
        var lineUnmeasurable = new ResultClientFailureEvidence(FallbackMessageBottom, FallbackScreenWidth, FallbackScreenHeight,
            FallbackMessageGap, FallbackMinimumStrip, float.NaN, float.NaN);
        check(ResultClientFailureStrip.Decide(0, new ResultClientWallGate(), lineUnmeasurable, out _, out _) == ResultClientStripState.WallNotSettled
              && ResultClientFailureStrip.Decide(1, new ResultClientWallGate(), lineUnmeasurable, out _, out _) == ResultClientStripState.WallNotSettled,
            "a frame that measures neither the waiting line nor a control has no band to give: the gate refuses it and the vanilla page stays, instead of the band being invented from the screen's foot");
        ResultPageWalls noWall = ResultPageLowerBoundPolicy.Resolve(true, FallbackScreenHeight, float.NaN, float.NaN,
            float.NaN, ResultBoundSource.None);
        check(!ResultFailureWallPolicy.WallMeasured(noWall, float.NaN, float.NaN, float.NaN)
              && ResultFailureWallPolicy.Waits(false, 0, true)
              && ResultFailureWallPolicy.Waits(false, ResultFailureWallPolicy.UnobservedBudget * 10, true),
            "and the fail-open gate agrees with it: a client failure page with no wall is not answered by frames, only by the player's own F8 (asserted in the client-wall regression)");
    }

    // ---- 4: 标题贴着页面下沿：装不下也给出诚实的带，并说清它跨过了谁 --------------------------------
    private static void ATitleNearTheFootStillAnswers(Action<bool, string> check)
    {
        const float titleNearFoot = 1300f;
        ResultPageWalls walls = ResultPageLowerBoundPolicy.Resolve(true, FallbackScreenHeight, float.NaN, float.NaN,
            FallbackWaitingTextTop, ResultBoundSource.None);
        float floor = FallbackBoardFloor();
        // 标题在 1300，墙在 1228：页面自己的条带只剩 48px（`ResultsBounds` 会把它夹到最小带宽），装不下 270px。
        check(ResultsBounds.TryResolve(FallbackScreenHeight, titleNearFoot, ResultBoundSource.TitleAnchor,
                float.NaN, FallbackWaitingTextTop, walls.FooterTextSource, float.NaN, out ResultsBand pageBand)
              && near(pageBand.Bottom, FallbackWaitingTextTop - ResultsBounds.Clearance(FallbackScreenHeight), 0.02f)
              && pageBand.Bottom - pageBand.Top < floor && pageBand.Clamped,
            $"a title at {titleNearFoot:0} leaves the page band {pageBand.Height:0.##}px below it (clamped={pageBand.Clamped.ToString().ToLowerInvariant()}), under the {floor:0}px floor - refused by the page-band tier");
        check(!ResultFallbackTier.AcceptsPageBand(pageBand.Height, floor),
            "and `ResultFallbackTier` refuses that band rather than squeezing a board into it");

        bool resolved = ResultFallbackBandPolicy.TryResolve(FallbackScreenHeight, titleNearFoot,
            ResultBoundSource.TitleAnchor, walls, float.NaN, float.NaN, float.NaN, floor, out ResultFallbackPlan plan);
        Console.WriteLine($"CLIENT-FAIL-FALLBACK-NEAR-TITLE: reason={FallbackReasonName(plan)} band=({plan.Top:0.##}..{plan.Bottom:0.##})"
            + $" room={plan.Height:0.##}px opacity={plan.Opacity:0.00} extended={plan.Extended.ToString().ToLowerInvariant()} covers={plan.Crosses}");
        check(resolved && plan.Reason == ResultFallbackReason.OverlapsControls && plan.Extended
              && near(plan.Opacity, ResultFallbackLayout.OverlapOpacity, 0.001f)
              && plan.Crosses.Contains("waiting-status text"),
            $"so the invented band answers and names what it now crosses (`covers={plan.Crosses}`) at {plan.Opacity:0.00} opacity: the one place this path deliberately accepts a faint board over a vanilla element, and it is never silent about it");

        // 同一页连标题都量不到：条带按严格路径那条「未量到上沿」的比例作答，下沿仍然是那行字。
        bool noTitle = ResultFallbackBandPolicy.TryResolve(FallbackScreenHeight, float.NaN,
            ResultBoundSource.ScreenSafeArea, walls, float.NaN, float.NaN, float.NaN, floor, out ResultFallbackPlan blind);
        check(noTitle && blind.PageBand
              && near(blind.Top, FallbackScreenHeight * ResultsBounds.UnmeasuredTopFraction, 0.02f)
              && near(blind.Bottom, FallbackWaitingTextTop - ResultsBounds.Clearance(FallbackScreenHeight), 0.02f),
            $"a client failure page whose title could not be measured still gets the page's own band from the screen-fraction edge ({blind.Top:0.##}..{blind.Bottom:0.##}), still bounded by the line");
    }

    // ---- 5: 主机页面逐像素不变 ----------------------------------------------------------------------------
    private static void TheHostPageIsUntouched(Action<bool, string> check)
    {
        float floor = FallbackBoardFloor();
        ResultPageWalls host = ResultPageLowerBoundPolicy.Resolve(false, FallbackScreenHeight, FallbackHostButtonTop,
            float.NaN, FallbackWaitingTextTop, ResultBoundSource.FooterText);
        check(!host.GuestFailure && near(host.PressableTop, FallbackHostButtonTop, 0.01f)
              && near(host.FooterTextTop, FallbackWaitingTextTop, 0.01f) && !float.IsFinite(host.PageFloor),
            "a host page keeps every reading it always had: the button, the waiting/footer text, and no page floor of its own");
        check(near(host.CapBottom(1432f), 1432f, 0.0001f),
            "the page-floor cap is a no-op on such a page, so no bound it produces moves");

        bool resolved = ResultFallbackBandPolicy.TryResolve(FallbackScreenHeight, FallbackHostTitleEdge,
            ResultBoundSource.TitleAnchor, host, float.NaN, float.NaN, float.NaN, floor, out ResultFallbackPlan plan);
        check(resolved && plan.PageBand && near(plan.Top, 488.333f, 0.02f) && near(plan.Bottom, 1129.667f, 0.02f)
              && near(plan.Height, 641.333f, 0.02f) && plan.Band.BottomSource == ResultBoundSource.FooterControl,
            $"the 1.19.37 host shape (title 467, button 1151, waiting text 1228) still answers ({plan.Top:0.##}..{plan.Bottom:0.##}), foot={ResultsBounds.Name(plan.Band.BottomSource)} - the client rule is the client's alone");
        check(near(plan.Bottom, FallbackHostButtonTop - ResultsBounds.Clearance(FallbackScreenHeight), 0.02f),
            $"its foot is the button minus the scaled clearance ({FallbackHostButtonTop} - {ResultsBounds.Clearance(FallbackScreenHeight):0.##}), never the waiting line");

        // 主机按钮量不到时，等待文字照旧是墙。
        ResultPageWalls hostNoButton = ResultPageLowerBoundPolicy.Resolve(false, FallbackScreenHeight, float.NaN,
            float.NaN, FallbackWaitingTextTop, ResultBoundSource.FooterText);
        bool textWall = ResultFallbackBandPolicy.TryResolve(FallbackScreenHeight, FallbackMessageBottom,
            ResultBoundSource.TitleAnchor, hostNoButton, float.NaN, float.NaN, float.NaN, floor,
            out ResultFallbackPlan hostPlan);
        check(textWall && near(hostPlan.Bottom, 1206.667f, 0.02f)
              && hostPlan.Band.BottomSource == ResultBoundSource.FooterText,
            $"a host page whose button could not be measured still takes the waiting/footer text as its wall ({hostPlan.Bottom:0.##})");

        // 没变的那两层：7 参 `ResultFallbackLayout.TryResolve` 与带 NaN 页面下沿的 8 参版本逐字节相同。
        bool seven = ResultFallbackLayout.TryResolve(FallbackScreenHeight, FallbackMessageBottom, FallbackHostButtonTop,
            float.NaN, float.NaN, float.NaN, floor, out ResultFallbackPlan oldPlan);
        bool eight = ResultFallbackLayout.TryResolve(FallbackScreenHeight, FallbackMessageBottom, FallbackHostButtonTop,
            float.NaN, float.NaN, float.NaN, floor, float.NaN, out ResultFallbackPlan samePlan);
        check(seven && eight && near(oldPlan.Top, samePlan.Top, 0.0001f) && near(oldPlan.Bottom, samePlan.Bottom, 0.0001f)
              && oldPlan.Reason == samePlan.Reason,
            "the 1.19.17-1.19.22 callers of `ResultFallbackLayout.TryResolve` keep the answer they always had: an unusable page floor (NaN) changes nothing");
        check(ResultFallbackLayout.Describe(plan, FallbackScreenHeight, floor).Contains("reason=page-band"),
            "and a page-band plan names itself `page-band` in the one log line, beside the tiers that already had names");
    }

    // ---- 6: 接线（源码串检查，不是运行时证明） ------------------------------------------------------------
    private static void TheBandWiringAfter1_19_45(Action<bool, string> check)
    {
        string root = FallbackSourceRoot();
        check(root != null, "the client-band regression locates the production sources");
        if (root == null) return;
        string display = FallbackCode(FallbackSource(root, "ResultsDisplay.cs"));
        string geometry = FallbackSource(root, "GuestPageGeometry.cs");
        string fallback = FallbackSource(root, "FallbackLayout.cs");
        string body = FallbackMethodBody(display, "private static bool FallbackPlacement(Component page");
        check(body != null, "the fail-open placement is found in the production source (static check, not a runtime proof)");
        if (body != null)
        {
            check(body.Contains("bool guestFailure = GuestClient() && !succeeded;")
                  && body.Contains("ResultPageLowerBoundPolicy.Resolve(guestFailure, Screen.height,")
                  && body.Contains("float measuredWall = walls.GuestFailure ? float.NaN : controls.FooterTop;"),
                "the fail-open placement asks the shared page-lower-bound policy for the page it is looking at, and only a page that is not a client failure keeps the scan's `FooterTop` as a second wall (the client's own reading already arrived as `FooterTextTop`) (static check, not a runtime proof)");
            check(body.Contains("ResultFallbackBandPolicy.TryResolve(Screen.height, anchorBottom, anchorSource, walls, measuredWall,"),
                "and it hands the band decision to the one policy this suite drives, instead of calling the two tiers itself (static check, not a runtime proof)");
            check(body.Contains("pageFloor=") && body.Contains("(this page's own screen-derived foot: the band's cap, never a wall)"),
                "while the diagnostic names the page's own foot for what it is - a cap, never a wall (static check, not a runtime proof)");
            check(body.Contains("protectedText = waiting;"),
                "and the line is still measured and still handed to the scan, so the log can still name it (static check, not a runtime proof)");
            int readable = body.IndexOf("if (!ResultFallbackPolicy.MayAnswer(_strictMisses, Forced, pageReadable))", StringComparison.Ordinal);
            int bandCall = body.IndexOf("ResultFallbackBandPolicy.TryResolve(", StringComparison.Ordinal);
            check(readable >= 0 && bandCall > readable,
                "and the readability gate - the one thing sixty refusals cannot buy - is still asked before any band is resolved (static check, not a runtime proof)");
        }
        check(geometry.Contains("public static float PageFloor(float screenHeight)")
              && geometry.Contains("public static class ResultPageLowerBoundPolicy")
              && geometry.Contains("return new ResultPageWalls(true, pressable, footerTextTop,"),
            "the page's fixed foot has one formula and one cap-only use, and the shared policy hands the waiting line over as the client page's footer wall (static check, not a runtime proof)");
        check(fallback.Contains("public static class ResultFallbackBandPolicy")
              && fallback.Contains("ResultFallbackTier.AcceptsPageBand(room, boardFloor)")
              && fallback.Contains("float pageFloor, out ResultFallbackPlan plan)")
              && fallback.Contains("PageBand = 4,"),
            "and the page-band-first decision, its floor gate and the page-floor parameter all live in `FallbackLayout.cs` beside the arithmetic they use (static check, not a runtime proof)");
        check(display.Contains("if (band.PageBand) ReportBand(\"fail-open\", band.Band,"),
            "the fail-open placement reports the page's own band through the strict path's own report helper when it adopts it (static check, not a runtime proof)");
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    // `ResultsDisplay`'s `_boardFloor` on a page the strict path refused: the readable floor for the same rows.
    private static float FallbackBoardFloor()
        => ResultsFontGrowth.MinimumBoardHeight(FallbackRows, PanelGeometry.MinimumRowEm, PanelDensity.Compact);

    private static string FallbackReasonName(ResultFallbackPlan plan)
        => plan.Reason == ResultFallbackReason.PageBand ? "page-band"
            : plan.Reason == ResultFallbackReason.BelowControls ? "below-controls"
            : plan.Reason == ResultFallbackReason.ScreenSafeArea ? "screen-safe-area"
            : plan.Reason == ResultFallbackReason.OverlapsControls ? "overlaps-controls"
            : "none";

    // `ResultsDisplay.NaturalSize`, headless: the settlement columns through `PanelGeometry.Build` (1.5 em rows).
    private static Func<int, ResultTableSize> FallbackNatural(int rows)
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

    private static string FallbackSource(string root, string name)
    {
        string path = Path.Combine(root, name);
        if (!File.Exists(path)) throw new FileNotFoundException("Production source is missing", path);
        return File.ReadAllText(path);
    }

    private static string FallbackSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj")))
            directory = directory.Parent;
        return directory != null ? directory.FullName : null;
    }

    // Comments are stripped before any wiring is read, so a paragraph of prose cannot feed an assertion.
    private static string FallbackCode(string source)
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

    private static string FallbackMethodBody(string source, string signature)
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
}
