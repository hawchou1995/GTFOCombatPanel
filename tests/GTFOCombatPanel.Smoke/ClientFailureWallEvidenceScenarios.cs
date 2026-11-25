using System;
using System.IO;
using HawChow.GTFOCombatPanel;

// 客机失败页的下界：那行不可点击的提示文字就是墙（用户纠正后的口径）。
//
// 真机证据（同一份 `snap\TOA_Pure_LogOutput_20261115_2157.log`，2560x1440，role=client）：
//
//   run 2（L3926–L3928）  failure native anchor ready: edge=467；failure footerSource=client-fixed-page-geometry:
//                         waitingText=1207 … strip=(488..1186)；failure band: top=488 (source=title-anchor),
//                         bottom=1186 (source=footer-control, waitingText=1207), room=698px
//                         —— 那行字在 1207，条带下沿 1186 = 1207 − clearance(21.333)，板子从来没有压在它上面。
//   run 3（L12649–L12657） fail-open band: top=488, bottom=1429 (source=screen-foot), room=941px；
//                         influence board … 1040x900 at (760,488)；failure board kept where it stands: … buttonTop=1177,
//                         wall=1171, need=900px, have=941px；… boardTop=488->488, boardBottom=1388, intrudes=true,
//                         opacity=0.55
//                         —— 屏幕下界被当成墙，900 高的板压住 1177 的控件，`TryHold` 不能抬一块已经在带顶的板。
//                         （L12649/L12651 里的 `waitingText=unmeasurable` 是 1.19.38–1.19.44 那个策略把量到的读数
//                         **丢掉**之后打印的字段，不是「这一帧量不到那行字」。）
//
// 1.19.45 的口径，也是下面每条断言的口径：
//
//   1. 客机失败页的下界 = `m_returnToLobby_text` 的实际屏幕矩形上沿（run 2 是 1207），和主机页拿「返回大厅」按钮
//      上沿做下界同一性质；真正可按的控件照旧是墙，两者取**更靠上**的那一堵。页面固定几何（1429.33）只做封顶，
//      **永远不是**「量到了墙」。
//   2. 一堵墙都量不到时，客机失败页不由帧数、也不由超时作答：保留原版页 + 既有节流重测。唯一的例外是玩家自己的
//      F8——那一条的日志必须写明墙没被观测到、条带脚下是屏幕自己的几何。页面自己的失败协程终点（
//      `FailureSequenceReadiness`）不是这里的一条证据：它只说页面脚本排完了版，没有说墙在哪。
//   3. 墙到了之后：严格条带先稳定两帧，再给出 run 2 那条 (488.33..1185.67) = 697.33px，第一块板按它拟合；
//      一个更高（更靠上）的可按控件只能把它收紧——run 3 的 1177 收到 (488.33..1155.67)，正是 L12657 的
//      band=(488..1156)。
//   4. 已经站着的板不会被一堵新墙撤掉（`Measure` 不过稳定门），而跑错位置的旧板由 `ResultAnchorRecovery`
//      （每页一次）与 `ResultAnchorDrift`（每页两次）有界重落位。
//
// 这个文件能做到什么、不能做到什么：
//   * 每一格数字都由生产类型算出：`ResultPageLowerBoundPolicy`、`ResultFailureWallPolicy`、`ResultClientFailureStrip`、
//     `ResultFallbackBandPolicy`、`ResultBoardHold`、`ResultAnchorRecovery`、`ResultAnchorDrift`、`ResultsBandFit`；
//   * 这里**没有** Unity：`ResultsDisplay`、IMGUI、原生页面、`Screen`、Harmony 补丁都进不了这个工程，所以没有绘制
//     次数、没有像素回读，也没有「哪一帧真的画了」的结论；
//   * 只存在于 `ResultsDisplay.cs` 里的那半截（谁在什么时候调用这些策略、日志写在哪一行）用源码串检查，并在
//     描述里明说「这不是运行时证明」；
//   * 这些数字来自日志与生产策略，**不是**真人实机回放：没有一帧真的被画到屏幕上。
internal static partial class Program
{
    // 记录页面的真机数字（run 2 / run 3 是同一张客机失败页）。
    private const float ClientWallScreenWidth = 2560f, ClientWallScreenHeight = 1440f;
    private const float ClientWallTitleBottom = 467f;        // L12643 edge=467 / L3923 edge=467
    private const float ClientWallWaitingTextTop = 1207f;    // L3926 waitingText=1207
    private const float ClientWallMessageGap = 16f;          // ResultsDisplay.MessageGap
    private const float ClientWallMinimumStrip = 96f;        // ResultsDisplay.MinimumGuestStripHeight
    private const float ClientWallLateControlTop = 1177f;    // L12656 buttonTop=1177
    private const float ClientWallRedrawnHeight = 900f;      // L12650 板高
    private const float ClientWallRedrawnBottom = 1388f;     // L12656 boardBottom=1388
    private const int ClientWallRows = 30;                   // ImpactBoard.RowCount(true)

    private static void RunClientFailureWallEvidenceScenarios()
    {
        Console.WriteLine("---- 1.19.45: 客机失败页的下界 = 那行不可点击提示文字自己的上沿 ----");
        TheWaitingLineIsTheWall(Check);
        NoFrameCountAnswersTheClientPage(Check);
        TheRecordedPagePlacesFromTheLine(Check);
        ALateControlOnlyTightens(Check);
        TheStandingWrongBoardIsRefittedNotJustFaded(Check);
        AnUnreadableScanStillBuysNothing(Check);
        TheEvidenceKeepsTheVanillaPage(Check);
        TheHostAndSuccessPagesAreUntouched(Check);
    }

    // ---- 1: 墙的语义：提示文字是墙，屏幕下沿不是 ------------------------------------------------------------
    private static void TheWaitingLineIsTheWall(Action<bool, string> check)
    {
        float clearance = ResultsBounds.Clearance(ClientWallScreenHeight);

        ResultPageWalls line = ResultPageLowerBoundPolicy.Resolve(guestFailure: true, screenHeight: ClientWallScreenHeight,
            footerControlTop: float.NaN, scanPressableTop: float.NaN, footerTextTop: ClientWallWaitingTextTop,
            footerTextSource: ResultBoundSource.None);
        check(near(line.FooterTextTop, ClientWallWaitingTextTop) && line.FooterTextSource == ResultBoundSource.FooterText
              && near(line.PageFloor, 1429.333f, 0.01f) && !float.IsFinite(line.PressableTop),
            "the client failure page hands the waiting-status line over as its footer wall (1207) and keeps its own screen-derived foot (1429.333) beside it; with no pressable control measured, the line is the wall there is");
        check(ResultFailureWallPolicy.WallMeasured(line, float.NaN, float.NaN, float.NaN),
            "and that line IS a wall: on a client the host's clickable button has become a non-clickable hint, and the board's lower bound is that hint's own top edge");

        ResultPageWalls bare = ResultPageLowerBoundPolicy.Resolve(true, ClientWallScreenHeight, float.NaN, float.NaN,
            float.NaN, ResultBoundSource.None);
        check(near(bare.PageFloor, 1429.333f, 0.01f) && !float.IsFinite(bare.FooterTextTop) && !float.IsFinite(bare.PressableTop)
              && !ResultFailureWallPolicy.WallMeasured(bare, float.NaN, float.NaN, float.NaN),
            "the page's own screen-derived foot alone (1429.333) is NOT a wall: nothing about the height of this monitor says where the page draws the thing the board must stay above, and treating it as evidence is what drew the run-3 board over the control at 1177");

        ResultPageWalls above = ResultPageLowerBoundPolicy.Resolve(true, ClientWallScreenHeight, ClientWallLateControlTop,
            ClientWallLateControlTop, ClientWallWaitingTextTop, ResultBoundSource.None);
        check(near(above.PressableTop, ClientWallLateControlTop) && near(above.FooterTextTop, ClientWallWaitingTextTop)
              && ResultFailureWallPolicy.WallMeasured(above, float.NaN, float.NaN, float.NaN),
            "a real pressable control is a wall beside the line, and the band arithmetic takes the higher of the two (1177 is below 1207, so the line still wins on the recorded page)");

        // 主机侧三个读数原样：有按钮、有页脚文字、或什么都没有。
        ResultPageWalls host = ResultPageLowerBoundPolicy.Resolve(false, ClientWallScreenHeight, 1151f, float.NaN,
            ClientWallWaitingTextTop, ResultBoundSource.FooterText);
        ResultPageWalls hostText = ResultPageLowerBoundPolicy.Resolve(false, ClientWallScreenHeight, float.NaN, float.NaN,
            ClientWallWaitingTextTop, ResultBoundSource.FooterText);
        ResultPageWalls hostEmpty = ResultPageLowerBoundPolicy.Resolve(false, ClientWallScreenHeight, float.NaN, float.NaN,
            float.NaN, ResultBoundSource.None);
        check(ResultFailureWallPolicy.WallMeasured(host, float.NaN, float.NaN, float.NaN)
              && ResultFailureWallPolicy.WallMeasured(hostText, float.NaN, float.NaN, ClientWallWaitingTextTop)
              && !ResultFailureWallPolicy.WallMeasured(hostEmpty, float.NaN, float.NaN, float.NaN)
              && !float.IsFinite(host.PageFloor),
            "a host failure page keeps every wall it always had - its button, its footer text, or the scan's footer reading - and has no page floor of its own");

        // 条带下沿就是那行字减 clearance：run 2 的 488..1186。
        bool pageOk = ResultsGuestPageGeometry.TryResolve(ClientWallTitleBottom, ClientWallScreenWidth, ClientWallScreenHeight,
            ClientWallMessageGap, ClientWallMinimumStrip, out ResultsSafeArea fixedPage);
        ResultClientBandReading reading = ResultClientBandReading.None;
        bool bandOk = pageOk && ResultClientFailureBand.TryResolve(fixedPage, ClientWallScreenHeight, ClientWallMinimumStrip,
            ClientWallWaitingTextTop, float.NaN, float.NaN, out reading);
        check(bandOk
              && near(reading.Top, 488.333f, 0.02f) && near(reading.Bottom, ClientWallWaitingTextTop - clearance, 0.02f)
              && near(reading.Height, 697.333f, 0.03f)
              && reading.WaitingTextTightened && !reading.PressableTightened && !reading.WaitingTextCovered,
            $"the band is the page's fixed top down to the line's own top less the clearance ({reading.Top:0.##}..{reading.Bottom:0.##} = {reading.Height:0.##}px): exactly the run-2 line `band: top=488, bottom=1186, room=698px`, and the board never stands on the text");
        check(near(reading.WallTop, ClientWallWaitingTextTop) && reading.WallMeasured,
            "and the reading names the wall it used, so the log can say which edge the foot came from");
    }

    // ---- 2: 没有墙时，任何帧数/超时都不放行；只有 F8 --------------------------------------------------------
    private static void NoFrameCountAnswersTheClientPage(Action<bool, string> check)
    {
        int[] frames = { 0, 1, 59, 60, 61, 120, 179, 180, 181, 1000, 100000 };
        bool answered = false, waited = true;
        foreach (int frame in frames)
        {
            answered |= ResultFailureWallPolicy.Answer(wallMeasured: false, framesWithoutWall: frame, guestFailure: true);
            waited &= ResultFailureWallPolicy.Waits(wallMeasured: false, framesWithoutWall: frame, guestFailure: true);
        }
        check(!answered && waited,
            "no frame count answers a client failure page whose wall was never measured - 0, 60, 180 and 100 000 frames all still wait (the run-3 page was answered at 60 because the screen foot was counted as a wall, not because 60 frames proved anything)");
        check(!ResultFailureWallPolicy.Waits(wallMeasured: true, framesWithoutWall: 0, guestFailure: true)
              && ResultFailureWallPolicy.Answer(wallMeasured: true, framesWithoutWall: 0, guestFailure: true),
            "and a measured wall answers immediately: the waiting line's own rectangle needs no budget and no stability proof beyond the page's own two-frame reading");

        // 主机失败页：按帧的有界放弃一个字节都没变。
        check(ResultFailureWallPolicy.Waits(false, ResultFailureWallPolicy.UnobservedBudget - 1, false)
              && !ResultFailureWallPolicy.Waits(false, ResultFailureWallPolicy.UnobservedBudget, false)
              && ResultFailureWallPolicy.Answer(false, ResultFailureWallPolicy.UnobservedBudget, false),
            "a host failure page keeps its bounded give-up at " + ResultFailureWallPolicy.UnobservedBudget
            + " frames - the client's rule never shortens or lengthens it");

        // 日志注脚：两种答案两种说法，谁都不许冒充「这一页被证明过」，而且不再把原生协程终点当证据。
        string byF8 = ResultFailureWallPolicy.AnswerNote(false, 0, true);
        string hostGiveUp = ResultFailureWallPolicy.AnswerNote(false, ResultFailureWallPolicy.UnobservedBudget, false);
        check(byF8.Contains("wallUnobserved=true") && byF8.Contains("F8")
              && byF8.Contains("screen-derived geometry") && byF8.Contains("no frame count or timeout"),
            $"the line that answers a wall-less client page says it was the player's own F8, that the band's foot is the page's own screen-derived geometry, and that no frame count or timeout may do it ({byF8})");
        check(!byF8.Contains("native") && !hostGiveUp.Contains("native"),
            "and neither note claims the page's native failure coroutine as evidence: it says the page's own script finished laying itself out, which is not a statement about where the wall is");
        check(hostGiveUp.Contains("bounded give-up") && ResultFailureWallPolicy.AnswerNote(true, 0, true).Length == 0,
            "the host page keeps the note it always printed, and a page that really measured its wall carries no note at all");
    }

    // ---- 3: 记录下来的那一页：第一块板就按那行字拟合 --------------------------------------------------------
    private static void TheRecordedPagePlacesFromTheLine(Action<bool, string> check)
    {
        float floor = FallbackBoardFloor();
        var evidence = new ResultClientFailureEvidence(ClientWallTitleBottom, ClientWallScreenWidth, ClientWallScreenHeight,
            ClientWallMessageGap, ClientWallMinimumStrip, ClientWallWaitingTextTop, float.NaN);
        var gate = new ResultClientWallGate();
        ResultClientStripState first = ResultClientFailureStrip.Decide(12643, gate, evidence, out _, out _);
        ResultClientStripState second = ResultClientFailureStrip.Decide(12644, gate, evidence,
            out ResultClientBandReading strict, out ResultsSafeArea page);
        check(first == ResultClientStripState.WallNotSettled && second == ResultClientStripState.Placed,
            $"the page's own two-frame wall question is what releases it ({ResultClientFailureStrip.Name(first)} then {ResultClientFailureStrip.Name(second)}): one reading is not an agreement, and no sample count authorises anything");
        check(near(strict.Top, 488.333f, 0.02f) && near(strict.Bottom, 1185.667f, 0.02f) && near(strict.Height, 697.333f, 0.02f)
              && near(page.Bottom, 1429.333f, 0.01f) && strict.WaitingTextTightened,
            $"and it answers the run-2 band ({strict.Top:0.##}..{strict.Bottom:0.##}) = {strict.Height:0.##}px: the fixed page geometry (foot {page.Bottom:0.##}) caps it, the waiting line at {ClientWallWaitingTextTop} bounds it");

        ResultPageWalls walls = ResultPageLowerBoundPolicy.Resolve(true, ClientWallScreenHeight, float.NaN, float.NaN,
            ClientWallWaitingTextTop, ResultBoundSource.None);
        bool resolved = ResultFallbackBandPolicy.TryResolve(ClientWallScreenHeight, ClientWallTitleBottom,
            ResultBoundSource.TitleAnchor, walls, float.NaN, float.NaN, float.NaN, floor, out ResultFallbackPlan plan);
        check(resolved && plan.PageBand && near(plan.Top, 488.333f, 0.02f) && near(plan.Bottom, 1185.667f, 0.02f)
              && near(plan.Opacity, 1f, 0.001f) && plan.Band.BottomSource == ResultBoundSource.FooterText,
            $"the fail-open path reads the same fact and answers the same numbers ({plan.Top:0.##}..{plan.Bottom:0.##}) at full strength, foot={ResultsBounds.Name(plan.Band.BottomSource)} - one page, one band");
        check(near(plan.Bottom, strict.Bottom, 0.02f) && near(plan.Top, strict.Top, 0.02f),
            "the strict path and the fail-open path agree to the pixel on the same frame");

        int font = ResultsBandFit.FontForBand(plan.Height, ClientWallRows, PanelDensity.Compact, 0);
        ResultTableSize board = FallbackNatural(ClientWallRows)(font);
        check(board.Height <= plan.Height + 0.5f && board.Height < ClientWallRedrawnHeight,
            $"the first board fitted into that band is {board.Width:0}x{board.Height:0} at a {font}px glyph - shorter than the 900 the run-3 log printed, because the band it is measured into is 697px and not the 941px the screen's foot offered");
        check(ResultBoardHold.TryPlace(ClientWallScreenWidth, ClientWallScreenHeight,
                new BoardStrip(plan.Top, plan.Bottom, true, ClientWallWaitingTextTop, float.NaN), board.Width, board.Height,
                0f, out LayoutRect origin) && near(origin.Y, 488.333f, 0.02f)
              && origin.Bottom <= ClientWallWaitingTextTop - ResultBoardHold.ButtonClearance + 0.5f,
            $"and it stands at ({origin.X:0.##},{origin.Y:0.##})..{origin.Bottom:0.##}, clear of the waiting line by more than the {ResultBoardHold.ButtonClearance:0}px clearance the repaint check keeps");
        check(!ResultBoardHold.TryPlace(ClientWallScreenWidth, ClientWallScreenHeight,
                new BoardStrip(plan.Top, plan.Bottom, true, ClientWallWaitingTextTop, float.NaN), 1040f, ClientWallRedrawnHeight,
                0f, out _),
            "the run-3 rectangle itself (1040x900) cannot be placed into that band at all: it is refused, and the vanilla page is kept instead of the 217px overlap the log recorded");

        Console.WriteLine($"CLIENT-FAIL-LINE: band=({plan.Top:0.##}..{plan.Bottom:0.##}) room={plan.Height:0.##}px"
            + $" line={ClientWallWaitingTextTop:0} pageFloor={walls.PageFloor:0.###}"
            + $" board={board.Width:0}x{board.Height:0} glyph={font}px"
            + $" against run-3 board=1040x{ClientWallRedrawnHeight:0} bottom={ClientWallRedrawnBottom:0} (band 488..1429)");
    }

    // ---- 4: 一个更靠上的可按控件只收紧，不撤板 --------------------------------------------------------------
    private static void ALateControlOnlyTightens(Action<bool, string> check)
    {
        float clearance = ResultsBounds.Clearance(ClientWallScreenHeight);
        var gate = new ResultClientWallGate();
        var lineOnly = new ResultClientFailureEvidence(ClientWallTitleBottom, ClientWallScreenWidth, ClientWallScreenHeight,
            ClientWallMessageGap, ClientWallMinimumStrip, ClientWallWaitingTextTop, float.NaN);
        ResultClientFailureStrip.Decide(10, gate, lineOnly, out _, out _);
        ResultClientFailureStrip.Decide(11, gate, lineOnly, out ResultClientBandReading placedBand, out _);
        var late = new ResultClientFailureEvidence(ClientWallTitleBottom, ClientWallScreenWidth, ClientWallScreenHeight,
            ClientWallMessageGap, ClientWallMinimumStrip, ClientWallWaitingTextTop, ClientWallLateControlTop);
        check(ResultClientFailureStrip.Measure(12, gate, late, out ResultClientBandReading tightened, out _)
              == ResultClientStripState.Placed
              && near(tightened.Bottom, ClientWallLateControlTop - clearance, 0.02f)
              && near(tightened.Bottom, 1155.667f, 0.02f) && tightened.PressableTightened
              && near(placedBand.Bottom, 1185.667f, 0.02f),
            $"a control that arrives later and reaches higher than the line tightens the band from {placedBand.Bottom:0.##} to {tightened.Bottom:0.##} through `Measure` - the (488..1156) the run-3 log printed a frame after it had already drawn the 900-tall board - and the standing board is never taken away");

        // 反过来：那行字更高（run 2/run 3 的 1207 对 1177 就是这一种）。
        check(near(tightened.WallTop, ClientWallLateControlTop) && tightened.WallTop < ClientWallWaitingTextTop,
            "the band remembers the highest wall this page ever showed, so a later frame that stops drawing the control cannot swing the band back down");

        // 页面自己的固定下沿永远不会成为墙，即使它是唯一「合理」的数字。
        ResultPageWalls floorOnly = ResultPageLowerBoundPolicy.Resolve(true, ClientWallScreenHeight, float.NaN, float.NaN,
            float.NaN, ResultBoundSource.None);
        check(!ResultFailureWallPolicy.Answer(ResultFailureWallPolicy.WallMeasured(floorOnly, float.NaN, float.NaN, float.NaN),
                0, guestFailure: true),
            "and a page that only has its screen-derived foot is not answered at all - the run-3 band (488..1429) has no honest source left");
    }

    // ---- 5: 已经站着的错板：先淡、再真的重落位（有界） ------------------------------------------------------
    private static void TheStandingWrongBoardIsRefittedNotJustFaded(Action<bool, string> check)
    {
        // run 3 站着的矩形：760,488,1040x900，带顶 488.33，板底 1388。
        var standing = new LayoutRect(760f, 488f, 1040f, ClientWallRedrawnHeight);
        check(!ResultBoardHold.TryHold(new BoardStrip(488f, ClientWallRedrawnHeight, true, ClientWallLateControlTop),
                standing, ClientWallScreenHeight, out LayoutRect lifted, 488.333f),
            "the production hold rule really refuses to lift that board (TryHold=false: it is already at its band's top, so lifting it would put it over the mission-failed title) - exactly the L12656 line, and the reason the run-3 page could only fade it");
        check(ResultLateControlPolicy.MustMove(standing.Y, standing.Bottom, 488.333f, 1155.667f),
            $"and the per-repaint rule sees the intrusion (board {standing.Y:0}..{standing.Bottom:0} against band 488.33..1155.67), so the very next repaint does act instead of pretending nothing happened");

        // 第二眼：兜底落位没量过条带 → 闩上；客机自己的探针两半都为真 → 真的重测。
        bool latched = ResultAnchorRecovery.LatchFallback(recoveries: 0, stripMeasured: false);
        check(latched && ResultAnchorRecovery.ShouldRetry(recoveries: 0, placedOnFallback: true, anchorReadable: true),
            "a fail-open placement whose strip was never measured latches the second look (`LatchFallback`), and `ShouldRetry` answers true while this page still has its one retry");
        check(ResultAnchorRecovery.Recovered(stripOfferable: true, footerMeasured: true, noFooterProven: true),
            "and the client's own recovery probe answers both halves: `FailureEdgesReadable` passes `footerMeasured`/`noFooterProven` from the page's fixed geometry, which is the only footer a client page has");
        int spent = ResultAnchorRecovery.Record(0);
        check(spent == 1 && !ResultAnchorRecovery.ShouldRetry(spent, placedOnFallback: true, anchorReadable: true),
            "one re-measure per page, and no second one however long the page stays up");

        // 第二条有界通道：页面自己的边真的动了（带 488.33..1155.67 对记录下来的 488..1429）。
        var drift = new ResultAnchorDrift();
        drift.Record(0, 488f, 941f);
        bool oneSample = drift.Due(1, 488.333f, 667.333f);
        bool twoSamples = drift.Due(2, 488.333f, 667.333f);
        check(!oneSample && twoSamples,
            "`ResultAnchorDrift` needs the strip to read differently on two frames in a row before it spends a re-measure - a single frame of the page's own animation is not the page moving");
        drift.Spent(); drift.Spent();
        check(drift.Exhausted && !drift.Due(3, 488.333f, 100f),
            $"and its budget is {ResultAnchorDrift.MaximumRecoveries} per page, so a page whose edges keep moving cannot cost a re-measure for ever");

        // 重落位之后：板底在墙上方，而不是 1388。
        float floor = FallbackBoardFloor();
        ResultPageWalls walls = ResultPageLowerBoundPolicy.Resolve(true, ClientWallScreenHeight, ClientWallLateControlTop,
            ClientWallLateControlTop, ClientWallWaitingTextTop, ResultBoundSource.None);
        bool refitResolved = ResultFallbackBandPolicy.TryResolve(ClientWallScreenHeight, ClientWallTitleBottom,
            ResultBoundSource.TitleAnchor, walls, float.NaN, float.NaN, float.NaN, floor, out ResultFallbackPlan refit);
        int refitFont = refitResolved ? ResultsBandFit.FontForBand(refit.Height, ClientWallRows, PanelDensity.Compact, 0) : 0;
        ResultTableSize refitBoard = refitResolved ? FallbackNatural(ClientWallRows)(refitFont) : ResultTableSize.None;
        LayoutRect replaced = default;
        bool replacedOk = refitResolved && ResultBoardHold.TryPlace(ClientWallScreenWidth, ClientWallScreenHeight,
            new BoardStrip(refit.Top, refit.Bottom, true, ClientWallLateControlTop, float.NaN),
            refitBoard.Width, refitBoard.Height, 0f, out replaced);
        check(replacedOk && replaced.Bottom <= ClientWallLateControlTop - ResultBoardHold.ButtonClearance + 0.5f,
            $"the board the second look really places ends at {replaced.Bottom:0.##}, above the control at {ClientWallLateControlTop} - not the {ClientWallRedrawnBottom:0} the faded rectangle kept for the rest of the page");

        // 跨页重置：墙的记忆、重落位闩都不属于下一张页面。
        var page = new ResultClientWallGate();
        var evidence = new ResultClientFailureEvidence(ClientWallTitleBottom, ClientWallScreenWidth, ClientWallScreenHeight,
            ClientWallMessageGap, ClientWallMinimumStrip, ClientWallWaitingTextTop, float.NaN);
        ResultClientFailureStrip.Decide(10, page, evidence, out _, out _);
        ResultClientFailureStrip.Decide(11, page, evidence, out _, out _);
        bool settledBefore = page.Settled;
        page.Forget();
        check(settledBefore && page.Samples == 0 && !page.Settled && !float.IsFinite(page.Wall),
            "a new page starts with no wall, no samples and nothing settled: the strip a previous expedition answered cannot place a board on the next one");
    }

    // ---- 6: 扫描看不全 / 量不到：预算路线照旧买不到板 ------------------------------------------------------
    private static void AnUnreadableScanStillBuysNothing(Action<bool, string> check)
    {
        check(!ResultFallbackPolicy.PageReadable(false, true, 0) && !ResultFallbackPolicy.PageReadable(true, false, 0)
              && !ResultFallbackPolicy.PageReadable(true, true, 1) && ResultFallbackPolicy.PageReadable(true, true, 0),
            "readability is still the scan's own three facts, and none of this release's changes touch it");
        check(!ResultFallbackPolicy.MayAnswer(ResultFallbackPolicy.MissBudget, false, false)
              && !ResultFallbackPolicy.MayAnswer(ResultFallbackPolicy.MissBudget * 10, false, false),
            "sixty or six hundred refusals buy no board on a page the scan could not read: the wall policy is never even reached");
        check(ResultFallbackPolicy.MayAnswer(0, true, false) && ResultFallbackPolicy.MayAnswer(ResultFallbackPolicy.MissBudget, false, true),
            "F8 is still the player's own authorization on such a page, and a page the scan read whole still gets the budget");

        // 一个 active 却量不到矩形的控件：墙的问题没有被回答，严格路径不交条带。
        var gate = new ResultClientWallGate();
        var blocked = new ResultClientFailureEvidence(ClientWallTitleBottom, ClientWallScreenWidth, ClientWallScreenHeight,
            ClientWallMessageGap, ClientWallMinimumStrip, ClientWallWaitingTextTop, float.NaN,
            scanComplete: true, parentCoverageComplete: true, unmeasurableControls: 1);
        check(ResultClientFailureStrip.Decide(0, gate, blocked, out _, out _) == ResultClientStripState.WallNotSettled
              && ResultClientFailureStrip.Decide(1, gate, blocked, out _, out _) == ResultClientStripState.WallNotSettled,
            "an active control whose rectangle could not be read keeps the strict page on `no wall answered`, however many frames it lasts: it may stand anywhere, including above the line");
        var incomplete = new ResultClientFailureEvidence(ClientWallTitleBottom, ClientWallScreenWidth, ClientWallScreenHeight,
            ClientWallMessageGap, ClientWallMinimumStrip, ClientWallWaitingTextTop, float.NaN,
            scanComplete: false, parentCoverageComplete: true, unmeasurableControls: 0);
        check(ResultClientFailureStrip.Decide(0, new ResultClientWallGate(), incomplete, out _, out _) == ResultClientStripState.ScanIncomplete,
            "and a scan that could not see the page is told apart from a page with no wall: `client-scan-incomplete`, not `client-wall-not-settled`");

        // 一条装不下最小板高的带：仍然拒收（原版页面保留）。
        check(!ResultFallbackTier.AcceptsPageBand(100f, 270f) && ResultFallbackTier.AcceptsPageBand(697.333f, 270f),
            "a band shorter than the board's own floor is refused (`ResultFallbackTier`), which is the `fit-failed` path that keeps the vanilla screen - the corrected 697px band passes it with room to spare");
    }

    // ---- 7: 证据拿不到时：保留原版页 + 有界重测 + 一行清楚的日志（源码串检查，不是运行时证明） ---------------
    private static void TheEvidenceKeepsTheVanillaPage(Action<bool, string> check)
    {
        string root = FallbackSourceRoot();
        check(root != null, "the client-wall regression locates the production sources");
        if (root == null) return;
        string display = FallbackCode(FallbackSource(root, "ResultsDisplay.cs"));
        string body = FallbackMethodBody(display, "private static bool FallbackPlacement(Component page");
        check(body != null && body.Length > 0, "the fail-open placement is found in the production source (static check, not a runtime proof)");
        if (body == null || body.Length == 0) return;

        check(body.Contains("ResultFailureWallPolicy.WallMeasured(walls, pageButtonTop, controls.PressableTop,")
              && !body.Contains("float.IsFinite(walls.PageFloor)")
              && !body.Contains("FailureWallObserved(pageButtonTop, controls.PressableTop, controls.FooterTop)"),
            "the place that decides whether a failure page has a wall asks the one policy and no longer reads the page's screen-derived floor or the old three-reading helper (static check, not a runtime proof)");
        check(!body.Contains("FailureSequence.IsFinished(page.Pointer)")
              && body.Contains("ResultFailureWallPolicy.Waits(wallMeasured, wallWait, walls.GuestFailure)"),
            "and the native failure sequence is not evidence here any more: it answers where the page's script has got to, never where the wall is (static check, not a runtime proof)");
        // 1.19.61（评审 #3）：客机失败页的墙只由槽位权威给出，F8 不再豁免这一条——它授权的是「画板」，不是「编一条边界」。
        // 主机失败页仍然走 `ResultFailureWallPolicy`（`Waits` 与 `WallMeasured` 都在上面那几条断言里）。
        check(body.Contains("wallMeasured = float.IsFinite(walls.FooterTextTop);")
              && body.Contains("if (!wallMeasured)")
              && !body.Contains("bool waiving = walls.GuestFailure && Forced;"),
            "F8 keeps its meaning for the player but not the exemption that used to skip the wall question on the page this release changes: a client failure page's bound is its own slot geometry, and the host page keeps the wait it always had (static check, not a runtime proof)");
        check(body.Contains(",wallPending=") && !body.Contains("nativeSequenceFinished")
              && body.Contains("measured again at the existing cadence") && body.Contains("vanilla page is kept"),
            "the one line a page prints says which readings were missing, that the wall question is still pending, and that the vanilla page is kept and measured again - a permanent no-board is never reported as success (static check, not a runtime proof)");
        check(body.Contains("ResultFailureWallPolicy.AnswerNote(wallMeasured, wallWait, walls.GuestFailure)"),
            "and the band that is answered without a wall carries the note that names its evidence (F5, unchanged in spirit)");
        check(body.IndexOf("if (!succeeded)", StringComparison.Ordinal) >= 0
              && body.IndexOf("ResultFailureWallPolicy.Waits(", StringComparison.Ordinal) > body.IndexOf("if (!succeeded)", StringComparison.Ordinal)
              && body.IndexOf("ResultFailureWallPolicy.Waits(", StringComparison.Ordinal) < body.IndexOf("ResultFallbackBandPolicy.TryResolve(", StringComparison.Ordinal),
            "the wall gate sits inside the failure branch and before any band is resolved: no success page and no host page reaches it (static check, not a runtime proof)");

        string forget = FallbackMethodBody(display, "private static void ForgetPage()");
        check(forget.Contains("_failureWallFirstFrame = -1;") && forget.Contains("_relayoutPending = false;")
              && forget.Contains("_strictMisses = 0;") && forget.Contains("_loggedDraw = false;"),
            "a new result page drops the wall clock, the re-placement latch and the once-per-page line with everything else (static check, not a runtime proof)");

        string fit = FallbackMethodBody(display, "private static bool Fit(BoardStrip strip");
        check(fit.Contains("else if (_relayoutPending && HeadingReports.Add(\"board-relaid\"))")
              && fit.Contains("result board re-fitted:")
              && fit.Contains("_relayoutPending = false;"),
            "a re-measure that really re-places the board prints its new rectangle and size once - the gap the recorded run 2 left when its re-placement printed no figure at all (static check, not a runtime proof)");
        check(display.Contains("_relayoutPending = true; _relayoutReason = \"second-look\";")
              && display.Contains("_relayoutPending = true; _relayoutReason = succeeded ? \"success-page-moved\" : \"page-moved\";"),
            "and only the two bounded re-measure triggers arm that line, so it can never become a per-frame log (static check, not a runtime proof)");

        // 客机分支：等待文字被读进证据，而且是这一页的下界之一。
        string strip = FallbackMethodBody(display, "private static bool FailureStrip(Component page");
        check(strip.Contains("new ResultClientFailureEvidence(guestAnchor, Screen.width, Screen.height,")
              && strip.Contains("MessageGap, MinimumGuestStripHeight, clientSlotTop, pressableTop,")
              && strip.Contains("bool wallPending = ResultClientPageActionPolicy.Pending(slot.HostSlotTop, slot.HintGlyphTop);")
              && strip.Contains("float clientSlotTop = slot.Top;")
              && strip.Contains("clientBand.WaitingTextTightened")
              && strip.Contains("footIsWall = clientBand.WallMeasured;"),
            "the client failure branch hands the merged slot wall (the field's layout top, the slot's visible glyphs and the highest pressable control) to the one entry point, reports which of them produced the foot, and treats a measured wall as the band's own promise (static check, not a runtime proof)");
        check(!strip.Contains("never bounds this strip") && !strip.Contains("not-a-wall"),
            "and no diagnostic still claims the waiting line does not bound the strip - the sentence this correction removes (static check, not a runtime proof)");
    }

    // ---- 8: 主机与成功页一个字节都没变 ----------------------------------------------------------------------
    private static void TheHostAndSuccessPagesAreUntouched(Action<bool, string> check)
    {
        check(ResultFailureWallPolicy.AnswerNote(false, 0, false).Length == 0
              && ResultFailureWallPolicy.AnswerNote(true, 0, false).Length == 0
              && ResultFailureWallPolicy.AnswerNote(false, ResultFailureWallPolicy.UnobservedBudget, false).Contains("bounded give-up"),
            "the note a success page's fail-open band prints is exactly what it printed before: nothing at zero frames, and the host's bounded-give-up wording after its budget");
        ResultPageWalls hostEmpty = ResultPageLowerBoundPolicy.Resolve(false, ClientWallScreenHeight, float.NaN, float.NaN,
            float.NaN, ResultBoundSource.None);
        check(!ResultFailureWallPolicy.WallMeasured(hostEmpty, float.NaN, float.NaN, float.NaN)
              && ResultFailureWallPolicy.Waits(false, 0, false)
              && !ResultFailureWallPolicy.Waits(false, ResultFailureWallPolicy.UnobservedBudget, false),
            "a host failure page with nothing measured keeps the frame budget it has always had - this release changes what bounds a client page, it does not take the host's rule away");
        check(!double.IsNaN(FallbackBoardFloor()) && near(FallbackBoardFloor(), 270f, 0.5f),
            $"and the board floor the fail-open ladder is judged by is still {FallbackBoardFloor():0}px (the figure the run-3 log printed as boardFloor=270px)");
    }
}
