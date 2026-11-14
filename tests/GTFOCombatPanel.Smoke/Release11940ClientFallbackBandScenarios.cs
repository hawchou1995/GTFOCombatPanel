using System;
using System.IO;
using HawChow.GTFOCombatPanel;

// 1.19.40：客机失败页上的 fail-open 条带 = 页面自己的固定几何，与严格落位读同一份事实。
//
// 独立评审的具体缺陷（本文件逐条驱动生产类型）：
//
//   `ResultsDisplay.FallbackPlacement` 把量到的「正在等待厅主」状态文字上沿（真机 1228）当「页脚文字墙」
//   交给 `ResultsBounds.TryResolve`，又把含那行字的 `controls.FooterTop` 一起交出去，而 tier2 的
//   `ResultFallbackLayout.TryResolve` 也收到同一个 1228。于是 2560x1440 的同一张客机失败页上：
//
//     严格路径（`ResultClientFailureStrip` → `ResultClientFailureBand`）：836.33..1429.33 = 593px
//     fail-open：836.33..1206.67（1228 − 21.33）= 370px → 30 行只塞进 8px 字号
//
//   370/30/1.5 = 8，正是真机日志里那块 435x360 的小板；条带一换（那行字跨帧下移 30px）就再来一次重排，
//   或者干脆长驻一块小板。严格分支从 1.19.38 起就写明那行字是事实、不是墙，下界是固定页面几何——兜底没有跟上。
//
// 这一版的口径，也是下面每条断言的口径：
//
//   1. `ResultPageLowerBoundPolicy`：客机失败页上状态文字不是墙、也不是页脚；真正可按的控件仍是墙；
//      页面下沿是固定页面几何（1429.333）。其余页面三个读数原样（主机路径一个字节没变）。
//   2. `ResultFallbackBandPolicy`（`FallbackPlacement` 唯一调用点）：先问页面自己的条带（与严格路径同一个
//      `ResultsBounds` 算术），装不下板子的自有高度才退到编出来的带；两层都用同一个页面下沿收口。
//   3. 真实可按控件仍然收紧下界、仍然被绕开；扫描看不全时预算路线照旧拒收、F8 照旧由玩家授权。
//
// 这个文件能做到什么、不能做到什么：
//   * 所有条带、字号、边界来源都由生产类型算出——`ResultPageLowerBoundPolicy`、`ResultFallbackBandPolicy`、
//     `ResultsBounds`、`ResultFallbackLayout`、`ResultClientFailureStrip`、`ResultsBandFit`、`ResultBoardHold`；
//   * 没有任何一行在这里跑 Unity：`ResultsDisplay`、IMGUI、原生页面都进不了这个工程，所以这里没有绘制次数、
//     没有像素回读，也没有一张截图的结论；
//   * 只存在于 `ResultsDisplay` 源码里的事实（接线）明说是源码串检查，并在描述里写「这不是运行时证明」。
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
        Console.WriteLine("---- 1.19.40: the client failure page's fail-open band is the page's own geometry ----");
        TheClientFallbackIsTheStrictBand(Check);
        ARealControlStillBoundsTheFallback(Check);
        AnIncompleteScanStillRefusesTheBudget(Check);
        ATitleNearTheFootStillGetsAnHonestBand(Check);
        TheHostPageIsUntouched(Check);
        TheWiringAfter1_19_40(Check);
    }

    // ---- 1: 评审实例：同一张客机失败页，兜底与严格落位是同一条条带 -------------------------------------------
    private static void TheClientFallbackIsTheStrictBand(Action<bool, string> check)
    {
        float floor = FallbackBoardFloor();
        check(near(floor, 270f, 0.5f),
            $"the board's own floor on this page is {floor:0}px (30 rows at the 6px readable floor), the figure the recorded log printed as boardFloor=270px");

        ResultPageWalls walls = ResultPageLowerBoundPolicy.Resolve(true, FallbackScreenHeight, float.NaN, float.NaN,
            FallbackWaitingTextTop, ResultBoundSource.None);
        check(walls.GuestFailure && !float.IsFinite(walls.FooterTextTop) && !float.IsFinite(walls.PressableTop)
              && near(walls.PageFloor, 1429.333f, 0.01f),
            $"a client failure page hands over three facts and no waiting-line wall: no pressable control measured, footerText=not-a-wall, pageFloor={walls.PageFloor:0.###}");

        // 那行字挪到哪里都不改变这一页的下界：同一个读数喂两个不同的上沿，答案逐字节相同。
        ResultPageWalls moved = ResultPageLowerBoundPolicy.Resolve(true, FallbackScreenHeight, float.NaN, float.NaN,
            FallbackWaitingTextTop + 30f, ResultBoundSource.None);
        check(near(moved.PageFloor, walls.PageFloor, 0.0001f) && !float.IsFinite(moved.FooterTextTop)
              && !float.IsFinite(moved.PressableTop),
            "the waiting line moving 30px down (1228 -> 1258) changes nothing the fail-open band can read - the recorded page re-fitted on exactly that move");

        // 对照：同一帧的事实走 1.19.39 及更早的规则（下界取那行字减 clearance）会得到 370px。
        bool old = ResultsBounds.TryResolve(FallbackScreenHeight, FallbackMessageBottom, ResultBoundSource.TitleAnchor,
            float.NaN, FallbackWaitingTextTop, float.NaN, out ResultsBand oldBand);
        check(old && near(oldBand.Bottom, 1206.667f, 0.02f) && near(oldBand.Height, 370.333f, 0.02f)
              && oldBand.BottomSource == ResultBoundSource.FooterText,
            $"the rule this release removes, in the same numbers: the line at 1228 clamped the band to {oldBand.Bottom:0.###} ({oldBand.Height:0.##}px, source={ResultsBounds.Name(oldBand.BottomSource)}) - the 370px band the log printed");

        bool resolved = ResultFallbackBandPolicy.TryResolve(FallbackScreenHeight, FallbackMessageBottom,
            ResultBoundSource.TitleAnchor, walls, float.NaN, float.NaN, float.NaN, floor, out ResultFallbackPlan plan);
        Console.WriteLine($"CLIENT-FAIL-FALLBACK: reason={FallbackReasonName(plan)} band=({plan.Top:0.##}..{plan.Bottom:0.##})"
            + $" room={plan.Height:0.##}px opacity={plan.Opacity:0.00} top={ResultsBounds.Name(plan.Band.TopSource)}"
            + $" bottom={ResultsBounds.Name(plan.Band.BottomSource)} | old band={oldBand.Height:0.##}px"
            + $" | glyph {ResultsBandFit.FontForBand(oldBand.Height, FallbackRows, PanelDensity.Compact, 0)}px ->"
            + $" {ResultsBandFit.FontForBand(plan.Height, FallbackRows, PanelDensity.Compact, 0)}px");
        check(resolved && plan.PageBand && near(plan.Top, 836.333f, 0.01f) && near(plan.Bottom, 1429.333f, 0.01f)
              && near(plan.Height, 593f, 0.01f) && near(plan.Opacity, 1f, 0.001f),
            $"the fail-open band is now the page's own: ({plan.Top:0.##}..{plan.Bottom:0.##}) = {plan.Height:0.##}px at full strength - not the 370px the waiting line used to leave");
        check(plan.Band.TopSource == ResultBoundSource.TitleAnchor
              && plan.Band.BottomSource == ResultBoundSource.ScreenFoot && !plan.Band.Clamped,
            $"and both edges say what they are: top={ResultsBounds.Name(plan.Band.TopSource)}, bottom={ResultsBounds.Name(plan.Band.BottomSource)} (the page's own foot, with nothing measured below it), clamped={plan.Band.Clamped.ToString().ToLowerInvariant()}");

        // 严格路径在同一张页面上回答什么：同一份 `ResultClientFailureStrip` 生产入口。
        var gate = new ResultClientWallGate();
        var evidence = new ResultClientFailureEvidence(FallbackMessageBottom, FallbackScreenWidth, FallbackScreenHeight,
            FallbackMessageGap, FallbackMinimumStrip, FallbackWaitingTextTop, float.NaN);
        ResultClientStripState first = ResultClientFailureStrip.Decide(0, gate, evidence,
            out ResultClientBandReading _, out ResultsSafeArea _);
        ResultClientStripState second = ResultClientFailureStrip.Decide(1, gate, evidence,
            out ResultClientBandReading strict, out ResultsSafeArea strictPage);
        check(first == ResultClientStripState.WallNotSettled && second == ResultClientStripState.Placed,
            $"the strict path still asks its two-frame wall question first ({ResultClientFailureStrip.Name(first)} then {ResultClientFailureStrip.Name(second)}), unchanged");
        check(near(strict.Top, plan.Top, 0.01f) && near(strict.Bottom, plan.Bottom, 0.01f)
              && near(strict.Height, plan.Height, 0.01f),
            $"and it answers ({strict.Top:0.##}..{strict.Bottom:0.##}) = {strict.Height:0.##}px: the strict band and the fail-open band are one band now, which is the whole point of this fix");
        check(near(strictPage.Bottom, walls.PageFloor, 0.01f) && near(strictPage.Top, plan.Top, 0.01f)
              && !strict.PressableTightened && strict.WaitingTextCovered,
            $"both come from `ResultsGuestPageGeometry` (page {strictPage.Top:0.##}..{strictPage.Bottom:0.##}), and both really do run past the waiting line ({strict.WaitingTextTop:0}) - that is the trade the page's own geometry makes, said out loud");

        // 字号：370px 只有 8px（记录页的 8px/435x360），593px 回到 12px 以上。
        int oldFont = ResultsBandFit.FontForBand(oldBand.Height, FallbackRows, PanelDensity.Compact, 0);
        int newFont = ResultsBandFit.FontForBand(plan.Height, FallbackRows, PanelDensity.Compact, 0);
        check(oldFont == 8 && newFont >= 12,
            $"the band is what the glyph is made of: {oldFont}px at 370px (the recorded small board) against {newFont}px at {plan.Height:0}px - the same 30 rows, one page");

        // 逐次重绘：兜底板每一帧都按同一条带重新定位，所以那行字动多少次都不会把它挪走。
        bool stable = true;
        for (int frame = 0; frame < 120; frame++)
        {
            float text = FallbackWaitingTextTop + (frame % 2 == 0 ? 0f : 30f);
            if (!ResultFallbackBandPolicy.TryResolve(FallbackScreenHeight, FallbackMessageBottom,
                    ResultBoundSource.TitleAnchor,
                    ResultPageLowerBoundPolicy.Resolve(true, FallbackScreenHeight, float.NaN, float.NaN, text,
                        ResultBoundSource.None),
                    float.NaN, float.NaN, float.NaN, floor, out ResultFallbackPlan repaint)
                || !near(repaint.Top, plan.Top, 0.0001f) || !near(repaint.Bottom, plan.Bottom, 0.0001f))
                stable = false;
        }
        check(stable,
            "120 repaints of a page whose waiting line alternates between 1228 and 1258 answer the same band to the pixel, so `RefreshFallbackSafety` finds nothing to move and spends no drift budget");
    }

    // ---- 2: 真实可按的控件仍然是墙、仍然被绕开 ------------------------------------------------------------
    private static void ARealControlStillBoundsTheFallback(Action<bool, string> check)
    {
        float floor = FallbackBoardFloor();
        // 真机探针：扫描量到一个可按控件在 1176；墙 = 1176 - 21.333 = 1154.667，与严格路径同一把尺子。
        ResultPageWalls walls = ResultPageLowerBoundPolicy.Resolve(true, FallbackScreenHeight, float.NaN, 1176f,
            FallbackWaitingTextTop, ResultBoundSource.None);
        check(near(walls.PressableTop, 1176f, 0.01f) && !float.IsFinite(walls.FooterTextTop)
              && near(walls.PageFloor, 1429.333f, 0.01f),
            "a measured pressable control becomes the wall (1176); the waiting line stays out of it, and the page's foot is still 1429.333");

        bool resolved = ResultFallbackBandPolicy.TryResolve(FallbackScreenHeight, FallbackMessageBottom,
            ResultBoundSource.TitleAnchor, walls, float.NaN, float.NaN, float.NaN, floor,
            out ResultFallbackPlan plan);
        check(resolved && plan.PageBand && near(plan.Bottom, 1154.667f, 0.02f) && near(plan.Height, 318.333f, 0.02f)
              && plan.Band.BottomSource == ResultBoundSource.FooterControl,
            $"the band stops above it ({plan.Top:0.##}..{plan.Bottom:0.##}, foot={ResultsBounds.Name(plan.Band.BottomSource)}), which is exactly what `ResultClientFailureBand` does with the same control");
        var gate = new ResultClientWallGate();
        var evidence = new ResultClientFailureEvidence(FallbackMessageBottom, FallbackScreenWidth, FallbackScreenHeight,
            FallbackMessageGap, FallbackMinimumStrip, FallbackWaitingTextTop, 1176f);
        ResultClientFailureStrip.Decide(0, gate, evidence, out ResultClientBandReading _, out ResultsSafeArea _);
        ResultClientFailureStrip.Decide(1, gate, evidence, out ResultClientBandReading strict, out ResultsSafeArea _);
        check(strict.PressableTightened && near(strict.Bottom, plan.Bottom, 0.01f),
            $"and the strict page agrees to the pixel ({strict.Bottom:0.###}): the control, never the text, is what shortens it");

        // 板子放进这条带里就碰不到那个控件：字号由条带自己的高度决定，与 `Fit` 同一套映射。
        int font = ResultsBandFit.FontForBand(plan.Height, FallbackRows, PanelDensity.Compact, 0);
        ResultTableSize board = FallbackNatural(FallbackRows)(font);
        check(board.Height <= plan.Height + 0.5f,
            $"the {plan.Height:0.##}px band fits a {font}px table ({board.Width:0}x{board.Height:0}), so the board is sized from its own band as it always is");
        check(ResultFallbackLayout.TryPlaceInBand(FallbackScreenWidth, FallbackScreenHeight, plan.Top, plan.Bottom,
                board.Width, board.Height, 0f, out LayoutRect placed)
              && placed.Bottom <= 1176f - ResultBoardHold.ButtonClearance,
            $"and it stands at {placed.Y:0.##}..{placed.Bottom:0.##}, clear of the control at 1176 by more than the {ResultBoardHold.ButtonClearance:0}px the repaint check keeps");

        // 一条装不下板子自有高度的带：编出来的那一层也把控件当墙，并把「跨过了什么」如实写出来。
        bool squeezed = ResultFallbackBandPolicy.TryResolve(FallbackScreenHeight, FallbackMessageBottom,
            ResultBoundSource.TitleAnchor, walls, float.NaN, float.NaN, float.NaN, floor + 200f,
            out ResultFallbackPlan extended);
        check(squeezed && extended.Reason == ResultFallbackReason.OverlapsControls && extended.Extended
              && near(extended.Opacity, ResultFallbackLayout.OverlapOpacity, 0.001f)
              && extended.Crosses.Contains("footer control"),
            $"when even the page's own band cannot hold the floor, the invented band grows past the named wall and says so (`covers={extended.Crosses}`) at {extended.Opacity:0.00} opacity - the control is still what it is measured against");
        check(!extended.Crosses.Contains("waiting"),
            "and the waiting-status text is never the wall the extension is named after");
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
            "sixty refusals - or six hundred - buy no board on a page the scan could not read: the new band arithmetic never sees that page at all");
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

        // 墙的读数本身不因「扫描不完整」而放宽：下界语义只描述读数，不描述扫描的完整性。
        ResultPageWalls incomplete = ResultPageLowerBoundPolicy.Resolve(true, FallbackScreenHeight, float.NaN, float.NaN,
            FallbackWaitingTextTop, ResultBoundSource.None);
        check(!float.IsFinite(incomplete.PressableTop) && near(incomplete.PageFloor, 1429.333f, 0.01f),
            "the wall semantics answer with the readings they were handed - they never assume 'no controls' from an unread page, which is why the readability gate has to be asked before them (asserted in the wiring below)");
    }

    // ---- 4: 标题贴着页面下沿：装不下连最小板高都不够，也要给出诚实的带 --------------------------------
    private static void ATitleNearTheFootStillGetsAnHonestBand(Action<bool, string> check)
    {
        const float titleNearFoot = 1300f;
        ResultPageWalls walls = ResultPageLowerBoundPolicy.Resolve(true, FallbackScreenHeight, float.NaN, float.NaN,
            FallbackWaitingTextTop, ResultBoundSource.None);
        float floor = FallbackBoardFloor();
        // 页面自己的条带这一帧只有 1321.33..1429.33 = 108px（下沿由 `CapBottom` 收到页面自己的 1429.333），
        // 装不下 270px 的自有高度。
        check(ResultsBounds.TryResolve(FallbackScreenHeight, titleNearFoot, ResultBoundSource.TitleAnchor,
                float.NaN, float.NaN, float.NaN, out ResultsBand pageBand)
              && near(pageBand.Top, titleNearFoot + ResultsBounds.Clearance(FallbackScreenHeight), 0.02f)
              && near(pageBand.Bottom, FallbackScreenHeight - 8f, 0.02f)
              && near(walls.CapBottom(pageBand.Bottom), walls.PageFloor, 0.01f)
              && near(walls.CapBottom(pageBand.Bottom) - pageBand.Top, 108f, 0.05f)
              && !ResultFallbackTier.AcceptsPageBand(walls.CapBottom(pageBand.Bottom) - pageBand.Top, floor),
            $"a title at {titleNearFoot:0} leaves the page band {walls.CapBottom(pageBand.Bottom) - pageBand.Top:0.##}px ({pageBand.Top:0.##}..{walls.CapBottom(pageBand.Bottom):0.##}, cut at the page's own foot rather than the window's), under the {floor:0}px floor - refused, exactly as before");
        bool resolved = ResultFallbackBandPolicy.TryResolve(FallbackScreenHeight, titleNearFoot,
            ResultBoundSource.TitleAnchor, walls, float.NaN, float.NaN, float.NaN, floor, out ResultFallbackPlan plan);
        Console.WriteLine($"CLIENT-FAIL-FALLBACK-NEAR-TITLE: reason={FallbackReasonName(plan)} band=({plan.Top:0.##}..{plan.Bottom:0.##})"
            + $" room={plan.Height:0.##}px opacity={plan.Opacity:0.00} extended={plan.Extended.ToString().ToLowerInvariant()} covers={plan.Crosses}");
        check(resolved && plan.Reason == ResultFallbackReason.OverlapsControls && plan.Extended
              && near(plan.Top, titleNearFoot + ResultFallbackLayout.AnchorGap, 0.02f)
              && near(plan.Height, floor + ResultFallbackLayout.ControlClearance, 0.02f)
              && near(plan.Opacity, ResultFallbackLayout.OverlapOpacity, 0.001f),
            $"so the invented band answers instead of nothing: top {plan.Top:0.##} (the title plus its gap), {plan.Height:0.##}px = the board's own floor plus the clearance, drawn at {plan.Opacity:0.00}");
        check(plan.Crosses.Contains("page") || plan.Crosses.Contains("lower edge"),
            $"and it names the page's own lower edge as what it crosses (`{plan.Crosses}`), never the waiting line the player is reading");
        check(plan.Bottom > walls.PageFloor && plan.Bottom <= FallbackScreenHeight + 400f,
            $"the band is sized from the board and runs to {plan.Bottom:0.##}, past the page's foot at {walls.PageFloor:0.##} - reported, never silent");
        // 同一页没有量到标题：页面自己的条带仍按严格路径那条「未量到上沿」的比例作答，下沿还是页面自己的脚。
        bool noTitle = ResultFallbackBandPolicy.TryResolve(FallbackScreenHeight, float.NaN,
            ResultBoundSource.ScreenSafeArea, walls, float.NaN, float.NaN, float.NaN, floor, out ResultFallbackPlan blind);
        check(noTitle && blind.PageBand
              && near(blind.Top, FallbackScreenHeight * ResultsBounds.UnmeasuredTopFraction, 0.02f)
              && near(blind.Bottom, walls.PageFloor, 0.01f),
            $"a client failure page whose title could not be measured still gets the page's own band from the screen-fraction edge the strict path uses ({blind.Top:0.##}..{blind.Bottom:0.##}) - not the screen-safe-area tier, and not a silent refusal");
        // 而编出来的那一层在同样条件下仍从 `SafeTop` 起步：那是这一页连条带都装不下时的最后一招。
        bool blindTier = ResultFallbackBandPolicy.TryResolve(FallbackScreenHeight, float.NaN,
            ResultBoundSource.ScreenSafeArea, walls, float.NaN, float.NaN, float.NaN, floor + 2000f, out ResultFallbackPlan safeArea);
        check(blindTier && !safeArea.PageBand && near(safeArea.Top, ResultFallbackLayout.SafeTop(FallbackScreenHeight), 0.02f),
            $"and when the band cannot hold a board of that size the invented tier starts at the screen's own 14% edge ({safeArea.Top:0.##}), the last resort this path has always had");
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

        // 主机按钮量不到时，等待文字照旧是墙 - 客机唯一被拿掉的那次夹紧。
        ResultPageWalls hostNoButton = ResultPageLowerBoundPolicy.Resolve(false, FallbackScreenHeight, float.NaN,
            float.NaN, FallbackWaitingTextTop, ResultBoundSource.FooterText);
        bool textWall = ResultFallbackBandPolicy.TryResolve(FallbackScreenHeight, FallbackMessageBottom,
            ResultBoundSource.TitleAnchor, hostNoButton, float.NaN, float.NaN, float.NaN, floor,
            out ResultFallbackPlan hostPlan);
        check(textWall && near(hostPlan.Bottom, 1206.667f, 0.02f)
              && hostPlan.Band.BottomSource == ResultBoundSource.FooterText,
            $"a host page whose button could not be measured still takes the waiting/footer text as its wall ({hostPlan.Bottom:0.##}) - and that is the clamp the client page no longer runs");

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
    private static void TheWiringAfter1_19_40(Action<bool, string> check)
    {
        string root = FallbackSourceRoot();
        check(root != null, "the 1.19.40 regression locates the production sources");
        if (root == null) return;
        string display = FallbackCode(FallbackSource(root, "ResultsDisplay.cs"));
        string geometry = FallbackSource(root, "GuestPageGeometry.cs");
        string fallback = FallbackSource(root, "FallbackLayout.cs");
        string body = FallbackMethodBody(display, "private static bool FallbackPlacement(Component page");
        check(body != null, "the fail-open placement is found in the production source (static check, not a runtime proof)");
        if (body != null)
        {
            check(body.Contains("ResultPageLowerBoundPolicy.Resolve(GuestClient() && !succeeded, Screen.height,")
                  && body.Contains("float measuredWall = walls.GuestFailure ? float.NaN : controls.FooterTop;"),
                "the fail-open placement asks the shared page-lower-bound policy, and only a page that is not a client failure keeps the scan's `FooterTop` (which includes the waiting line) as a measured wall (static check, not a runtime proof)");
            check(body.Contains("ResultFallbackBandPolicy.TryResolve(Screen.height, anchorBottom, anchorSource, walls, measuredWall,"),
                "and it hands the band decision to the one policy this suite drives, instead of calling the two tiers itself (static check, not a runtime proof)");
            check(!body.Contains("footerText, footerTextSource, controls.FooterTop")
                  && !body.Contains("ResultFallbackLayout.TryResolve(Screen.height, anchorBottom, controls.FooterTop")
                  && !body.Contains("float.IsFinite(pageButtonTop) ? pageButtonTop : float.NaN"),
                "no frame of any page can pass the waiting text into the band arithmetic any more - the three old argument lists are gone from that method (static check, not a runtime proof)");
            check(body.Contains("protectedText = waiting;"),
                "while the line is still measured and still handed to the scan, so the log can still name it (static check, not a runtime proof)");
            int readable = body.IndexOf("if (!ResultFallbackPolicy.MayAnswer(_strictMisses, Forced, pageReadable))", StringComparison.Ordinal);
            int bandCall = body.IndexOf("ResultFallbackBandPolicy.TryResolve(", StringComparison.Ordinal);
            check(readable >= 0 && bandCall > readable,
                "and the readability gate - the one thing sixty refusals cannot buy - is still asked before any band is resolved (static check, not a runtime proof)");
        }
        check(geometry.Contains("public static float PageFloor(float screenHeight)")
              && geometry.Contains("float bottom = PageFloor(screenHeight);")
              && geometry.Contains("public static class ResultPageLowerBoundPolicy"),
            "the page's fixed foot has one formula (`ResultsGuestPageGeometry.PageFloor`), used by the strict geometry and read by the fail-open policy (static check, not a runtime proof)");
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
