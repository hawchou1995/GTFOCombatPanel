using System;

namespace HawChow.GTFOCombatPanel;

// 客机结算页的固定页面几何。
//
// 客机（SNet.IsMaster == false）的结算页上没有「返回大厅」按钮，只有一行「正在等待厅主」的状态文字；
// 1.19.13/1.19.14 让落板去等「完整交互扫描证明本页没有任何 active 控件」，而这条状态文字本身就是一个
// active 控件，证明永远不成立，于是板子被静默永久拒收。这两个结算页的几何在同一分辨率下是固定的，
// 跨分辨率按高度等比对应，所以客机只需要知道锚点画到了哪里，下界照参考分辨率缩放即可，不必问任何控件。
//
// 这个类只做算术：锚点实测底边 + gap 得下界上沿，1080p 参考下界按 screenHeight/1080 等比缩放，
// 结果夹紧到屏内；任何一项不可信（非有限数、屏幕尺寸荒谬、区域装不下最小板高）就明确回答不可信，
// 由调用方保留原版页面。宽度与控件位置都不参与上下界计算。
public static class ResultsGuestPageGeometry
{
    public const float ReferenceWidth = 1920f;
    public const float ReferenceHeight = 1080f;
    public const float ReferenceBottomInset = 8f;

    // 页面固定几何的下界：参考分辨率下的 8px 页脚按高度等比缩放（2560x1440 上是 1429.33）。
    //
    // 1.19.40: 抽成一个入口，因为这一页的下沿不再只属于严格路径——fail-open 路径也用它（`ResultPageLowerBoundPolicy`
    // → `ResultFallbackLayout`/`ResultFallbackBandPolicy`）。两处算式若各写一遍，就会再次出现「同一张页面上
    // 严格 593px、兜底 370px」这类两个说法。
    public static float PageFloor(float screenHeight)
    {
        if (!float.IsFinite(screenHeight) || screenHeight <= 8f) return float.NaN;
        float bottom = screenHeight - ReferenceBottomInset * (screenHeight / ReferenceHeight);
        if (bottom > screenHeight) bottom = screenHeight;
        if (bottom < 0f) bottom = 0f;
        return bottom;
    }

    public static bool TryResolve(float anchorBottom, float screenWidth, float screenHeight,
        float gap, float minimumHeight, out ResultsSafeArea area)
    {
        area = default;
        if (!float.IsFinite(anchorBottom) || !float.IsFinite(screenWidth) || !float.IsFinite(screenHeight)) return false;
        if (!float.IsFinite(gap) || !float.IsFinite(minimumHeight)) return false;
        if (screenWidth <= 8f || screenHeight <= 8f) return false;
        if (gap < 0f || !(minimumHeight > 0f)) return false;
        float scale = screenHeight / ReferenceHeight;
        float bottom = PageFloor(screenHeight);
        if (!float.IsFinite(bottom)) return false;
        float top = anchorBottom + gap * scale;
        if (top < 0f) top = 0f;
        if (top > screenHeight) top = screenHeight;
        if (!(bottom > top)) return false;
        if (bottom - top < minimumHeight) return false;
        area = new ResultsSafeArea(top, bottom);
        return true;
    }
}

// 客机失败页的条带：两个边界都由固定页面几何决定，量到的「正在等待厅主」文字只进日志。
//
// 1.19.38: 客机已经拿到固定页面几何（`ResultsGuestPageGeometry`，本机实测 2560x1440 上是 837..1429），
// 但 `ResultsBounds.TryResolve` 又以原生等待文字块的上沿（1228 减 clearance = 1207）把它二次夹紧成
// 370px：30 行只能塞进 8px 字号，板子 435x360，而且等待文字跨帧移动（1207→1237）还会触发一次
// 「result board re-measured」的重新适配——这就是玩家看到的「先小、后变、不是一步到位」。
//
// 结论：等待状态文字不是可交互控件，也不是客机条带的下限墙。它照旧被测量、照旧写进日志
// （`waitingText=`），但不参与 `Bottom` 的决定。客机条带的下界就是页面固定几何的下界。
//
// 条带仍可被**真正可按的控件**（重开检查点 / 回大厅按钮）收紧：那不是文字，是玩家要按的东西，
// 收紧后仍装得下客机最小板高才采纳（与旧实现的条件完全一致），并且现有的遮挡拒绝逻辑不变。
// 这条规则在这里做成纯函数，`ResultsDisplay.FailureStrip` 的客机分支只调用它，无头套件可以直接驱动。
public readonly struct ResultClientBandReading
{
    // 板子要装进去的房间。
    public readonly ResultsSafeArea Strip;
    // 这一帧量到的等待状态文字上沿：只进日志，永远不是墙。不可测时是 NaN。
    public readonly float WaitingTextTop;
    // 条带的下沿已经越过了等待文字的上沿：板子会压在那行字上，这是固定页面几何带来的取舍。
    public readonly bool WaitingTextCovered;
    // 真正可按的控件仍然把下沿收上去了。
    public readonly bool PressableTightened;

    public ResultClientBandReading(ResultsSafeArea strip, float waitingTextTop, bool covered, bool tightened)
    {
        Strip = strip; WaitingTextTop = waitingTextTop; WaitingTextCovered = covered; PressableTightened = tightened;
    }

    public float Top { get { return Strip.Top; } }
    public float Bottom { get { return Strip.Bottom; } }
    public float Height { get { return Strip.Height; } }
    public bool Fits { get { return Strip.Fits; } }
    public static ResultClientBandReading None { get { return default; } }
}

public static class ResultClientFailureBand
{
    // 客机失败页的条带，从已经解析好的固定页面几何出发：
    //
    //   top     固定页面的上沿（锚点底边 + gap，由 `ResultsGuestPageGeometry` 算）
    //   bottom  固定页面的下沿（`screenHeight - 8 * scale`）——等待文字不参与
    //   收紧    只有在 `pressableTop` 有限（页面上真的量到一个可按控件）时才发生，且收紧后仍要装得下
    //           `minimumHeight`，与旧实现同样的条件；收紧用的是 `ResultsBounds.Clearance`，也就是主机
    //           那条规则用的同一把尺子，好让两个角色的「墙上方留多少」不会各说各话。
    //
    // `waitingTextTop` 只在这里被读一次，用来回答「这一版会压到那行字吗」，进日志与断言，
    // 不进任何算术。任何一项不可信就明确回答不可信，调用方保留原版页面。
    public static bool TryResolve(ResultsSafeArea fixedPage, float screenHeight, float minimumHeight,
        float waitingTextTop, float pressableTop, out ResultClientBandReading reading)
    {
        reading = ResultClientBandReading.None;
        if (!fixedPage.Fits) return false;
        if (!float.IsFinite(screenHeight) || screenHeight <= 0f) return false;
        if (!float.IsFinite(minimumHeight) || !(minimumHeight > 0f)) return false;
        float top = fixedPage.Top;
        float bottom = fixedPage.Bottom;
        if (!float.IsFinite(top) || !float.IsFinite(bottom)) return false;
        if (top < 0f) top = 0f;
        if (bottom > screenHeight) bottom = screenHeight;
        if (!(bottom > top)) return false;
        bool tightened = false;
        if (float.IsFinite(pressableTop))
        {
            float wall = pressableTop - ResultsBounds.Clearance(screenHeight);
            if (wall < bottom && wall > top && wall - top >= minimumHeight)
            {
                bottom = wall;
                tightened = true;
            }
        }
        if (!(bottom > top)) return false;
        if (bottom - top < minimumHeight) return false;
        // 那行字是一个事实，不是一堵墙：读它只为了日志能说清这一版会不会压上去。
        bool covered = float.IsFinite(waitingTextTop) && bottom > waitingTextTop + 0.5f;
        reading = new ResultClientBandReading(new ResultsSafeArea(top, bottom), waitingTextTop, covered, tightened);
        return true;
    }
}

// ---------------------------------------------------------------------------------------------------
// 一页的下界语义：严格路径与 fail-open 路径读同一份事实
// ---------------------------------------------------------------------------------------------------
//
// 独立评审的具体缺陷：`ResultsDisplay.FallbackPlacement` 把量到的「正在等待厅主」状态文字上沿（真机 1228）
// 当「页脚文字墙」交给 `ResultsBounds.TryResolve` 和 tier2 的 `ResultFallbackLayout.TryResolve`，还把扫描里
// 含那行字的 `FooterTop` 当成页脚控件。于是 2560x1440 的同一张客机失败页上：严格路径 836..1429（593px），
// fail-open 只有 836..1207（370px，= 1228 减 clearance），30 行只塞进 8px 字号，小板随后重排或长驻。
// `ResultsDisplay.FailureStrip` 的客机分支从 1.19.38 起就写明那行字是事实、不是墙（`ResultClientFailureBand`），
// 下界是固定页面几何的 1429——fail-open 路径没有跟上，这就是两个说法。
//
// 这一层把「哪些读数可以当下界墙、这一页真实的下沿在哪」做成纯函数，两条路径读同一个答案：
//
//   * 客机失败页：状态文字既不是墙、也不是页脚（`FooterTextTop` 为 NaN）；真正可按的控件仍是墙
//     （`PressableTop`，取扫描的 `PressableTop` 与命名按钮里最高的一堵）；页面下沿是固定页面几何
//     （`ResultsGuestPageGeometry.PageFloor`，2560x1440 上 1429.33）。
//   * 其余页面（主机失败页、主机/客机成功页）：三个读数原样返回，页面下沿不设限（NaN = 窗口脚），
//     所以主机路径一个字节都没变。
//
// 「扫描看不全」不在这里放行：inactive 控件被扫描记为 hidden，active 却量不到矩形仍会让
// `ResultFallbackPolicy.PageReadable` 说不（预算路线），F8 照旧由玩家自己授权（`MayAnswer`）。
public readonly struct ResultPageWalls
{
    // 这一页是客机失败页：页面自己量到的「正在等待厅主」状态文字不是墙，也不是页脚。
    public readonly bool GuestFailure;
    // 真正可按控件的最高上沿（`FooterControls.PressableTop` 与命名按钮取最小 y）。NaN = 没有量到可按控件。
    public readonly float PressableTop;
    // 可以当成「页脚文字墙」交出去的读数：主机页 / 客机成功页是等待或页脚文字，客机失败页永远是 NaN。
    public readonly float FooterTextTop;
    // 那堵文字墙的来源，好让日志继续说出「画出来的文字」还是「代替它的控件」。
    public readonly ResultBoundSource FooterTextSource;
    // 这一页真实的下沿：客机失败页的固定页面几何下界；NaN = 这一页一直用到窗口脚。
    public readonly float PageFloor;

    public ResultPageWalls(bool guestFailure, float pressableTop, float footerTextTop,
        ResultBoundSource footerTextSource, float pageFloor)
    {
        GuestFailure = guestFailure; PressableTop = pressableTop;
        FooterTextTop = footerTextTop; FooterTextSource = footerTextSource; PageFloor = pageFloor;
    }

    // 下界不许越过这一页自己的下沿：锚点条带与自由带都按同一个页面几何收口。其余页面上是空操作。
    public float CapBottom(float bottom)
        => float.IsFinite(PageFloor) && float.IsFinite(bottom) && PageFloor < bottom ? PageFloor : bottom;

    public static ResultPageWalls None { get { return default; } }
}

public static class ResultPageLowerBoundPolicy
{
    // `guestFailure` 由页面角色和页种共同决定（客机 + 失败页）。`footerControlTop` 是调用方自己量到的
    // 「可按控件」上沿（`ResultsDisplay.LobbyButtonTop`，它的扫描不把等待文字算进 `FooterTop`），
    // `scanPressableTop` 是交互扫描自己排除掉受保护文字之后的那个读数。两者都只描述可按控件。
    public static ResultPageWalls Resolve(bool guestFailure, float screenHeight, float footerControlTop,
        float scanPressableTop, float footerTextTop, ResultBoundSource footerTextSource)
    {
        if (!guestFailure)
            return new ResultPageWalls(false, footerControlTop, footerTextTop, footerTextSource, float.NaN);
        // 客机失败页：可按控件照旧是墙（这是唯一可能收紧下界的读数），状态文字被拿掉，下沿是本页的固定几何。
        float pressable = ResultsRegion.HighestTop(footerControlTop, scanPressableTop);
        return new ResultPageWalls(true, pressable, float.NaN, ResultBoundSource.None,
            ResultsGuestPageGeometry.PageFloor(screenHeight));
    }
}


// ---------------------------------------------------------------------------------------------------
// 这一页的墙有没有证据：一个诚实的谓词
// ---------------------------------------------------------------------------------------------------
//
// 记录下来的那一页（`LogOutput.snapshot.log` 4426–4441，客机失败页，2560x1440）是这么错的：头两帧扫描完整、
// 但页面上一个可按控件都没有（页面自己的「重开检查点」/「回大厅」还没被点亮），两帧一致就被读成「这一页没有
// 墙」，条带按固定页面几何 381..1429 放了一块 1987x990 的板；下一帧真实控件出现（1175/1181/1189），条带收紧到
// 802px，站着的板真的压在控件上，于是整块板被撤掉、原版页面回来。
//
// 这一条补上的就是这个事实：**这一页还没有量到任何可按控件**，所以这一帧不是「页面没有墙」，而是「墙还没出现」，
// 不能落板。一旦量到可按控件，这一条就不再说话：那堵墙是真的，由它自己回答。
//
// 1.19.42（独立评审 F3）：这一段以前收五个入参，其中四个在客机失败页上不携带任何逐帧信息：
//
//   * 「回大厅」按钮在客机上**永久** inactive（角色带来的，不随帧而来），扫描于是把它记成 hidden，
//     `HiddenPageActions` 常 ≥1；
//   * 于是 `hiddenPageActions > 0` 这一条在客机上恒真——裁掉它之后，整个谓词等价于 `!IsFinite(pressableTop)`，
//     这就是「一个恒真入参冒充证据」；
//   * `restartPresent && !restartActive` / `lobbyPresent && !lobbyActive` 两条也几乎与同一件事等价，而它们唯一的
//     差别（两个按钮都 active、却量不到任何可按控件）给出的是**错的**答案：那一帧同样没有证据说墙存在。
//
// 所以这条策略只问它真正能回答的那一个问题，并且**没有证据时不声称就绪**：
//
//   * 量到一个真的可按控件 → 墙的问题由那堵墙回答，不 pending；
//   * 量不到 → 这一页没有任何证据，不能当成「这一页没有墙」，pending。
//
// 它不再假装从本页动作的 active 状态里读出逐帧信息；那些读数（hidden / hiddenPageActions / pressable）只进调用方
// 按页打印的诊断行。诚实边界：这**不是**「更早知道」——它比旧谓词更保守（多了一类 pending），也没有退回
// 「两帧空扫描即 settled」；一直等不到控制时由既有的 60 次拒收预算回答（`ResultFallbackPolicy.MayAnswer`），
// 日志会把它叫成 fail-open 而不是 settled。
public static class ResultClientPageActionPolicy
{
    public static bool Pending(float pressableTop) => !float.IsFinite(pressableTop);
}
// ---------------------------------------------------------------------------------------------------
// 客机失败页的下界墙：先稳定，再落板
// ---------------------------------------------------------------------------------------------------
//
// 玩家报告的是「结算界面没有第一时间按最终状态定下大小和位置」。记录下来的两次客机失败页
// （`LogOutput.log`，2560x1440，run 4 与 run 5）逐行都是同一个形状：
//
//   failure band: top=381 (title-anchor), bottom=1429 (screen-foot), room=1049px
//   influence board ... 1160x990 px at (700,381) ... glyph 22px filling 94% of the 1049px strip
//   failure board kept where it stands: ... buttonTop=1176, wall=1170, need=990px, have=1049px
//   failure board deferred: an interaction the scan can see now reaches the placed board (count=1,... room=798)
//   result board deferred: ... vanilla is restored and the strip is measured again
//
// 也就是：第一帧扫描还没量到任何可按控件，客机条带整段用固定页面几何（381..1429），一块 990px 高的板被
// 画上去；下一帧扫描量到可按控件 1176，条带收紧 251px（381..1179，room 798），站着的板真的压在那个控件上，
// 于是整块板被撤掉，原版页面回来，这一页再也没有出现过。玩家看到的就是「先画一块大的，再消失」。
//
// 根因不是遮挡判断错了——990px 的板确实压在 1176 的控件上——而是**第一帧就把板落在一张还没量完的条带上**。
// 这个门把那一步补上：本页的下界墙读到和上一帧一样了，才允许落板。
//
//   * 两帧**可回答且完整**的读数一致（都不可测且没有 active-but-unmeasurable 控件，或都可测且相差不超过
//     `Tolerance`）→ 已稳定，可以落板。
//   * **任何帧数都不能自己授权落板。** `Samples` 只用于诊断和重试节奏，`Settled` 是唯一的放行条件；「等够
//     多少帧就把没量到的墙当成量到了」在这条链上一个字节都没有。一直不稳定的页面由**已有的**有界逃生口
//     回答：`ResultFallbackPolicy.MissBudget`（60 帧拒收）之后的 fail-open 板——它让开每一个量到的控件、
//     跨墙时画淡，并且从不声称这一页被证明过。
//   * 一个 active、但这一帧量不到矩形的控件不是「没有控件」：它让这一帧**不可回答**，既不算一次一致读数，
//     也清不掉已经读到的墙（`ResultInteractionTally` 的规则在这一层一个字都没改）。这种页面无论等 30、
//     120 还是 500 帧，这条门都不会交出条带。
//   * 扫描不完整（`controls.Complete` / `ParentCoverageComplete`）同样不算「这一页没有墙」：客机失败页在
//     生产里**没有**外层的完整扫描闸门（`Placement`/`BoardStillSafe` 的 `!GuestClient() && !Complete` 只约束
//     主机），所以这个要求必须落在这条门里，不能靠调用顺序顺带满足。inactive 控件不影响完整性——它们被扫描
//     记为 hidden，`Complete` 照旧为真，这正是「页面把控件关掉了」与「扫描看不见这一页」的区别。
//
// 诚实边界（不声称首显永远最终）：真机没有任何「控件都到齐了」的原生信号可用——`FailureSequenceReadiness`
// 只在失败协程终点 `MoveNext(false)` 之后才成立，而记录下来的那一页在序列还没播完时控件就已经亮了。所以
// 「连续两次完整扫描都是空集合」是这条链上能拿到的最强真实就绪证据；若控件在第三帧才亮，站着的板会由既有
// 的扫描遮挡拒绝被撤下一次，下一帧就按收紧后的几何重新落定（`Wall` 是这一页见过的最高一堵，不会来回摆）。
public sealed class ResultClientWallGate
{
    // 两帧读数之间的容差：页面自己的动画会让一个控件的上沿挪一两个像素，那不是一堵新墙。
    public const float Tolerance = 1f;

    private int _samples, _lastFrame = int.MinValue;
    private float _reading = float.NaN, _wall = float.NaN;
    private bool _hasReading, _answerable, _settled;

    // 本页观察到过的帧数。诊断与重试节奏用，**不是**放行条件。
    public int Samples { get { return _samples; } }
    // 最近一次读数和上一帧一致，而且两帧都可回答、都完整：这是唯一的放行答案。
    public bool Settled { get { return _settled; } }
    // 本页见过的最高那堵墙（y 最小）；NaN 表示这一页从没量到过可按控件。
    public float Wall { get { return _wall; } }

    // `scanComplete`/`parentCoverageComplete` 是扫描自己的结构性结论；`unmeasuredControls` 是这一帧 active
    // 但量不到矩形的控件数；`pageActionsPending` 是「这一页还没量到任何可按控件，因此墙的问题没有证据」（见
    // `ResultClientPageActionPolicy`：它只读 `pressableTop`）。三个事实合起来回答「这一帧的墙问题被回答了吗」。
    //
    // 最后一项只属于**首次落位**：`Measure` 不读它，所以一堵「页面还没有证据」的帧绝不会把已经站住的板
    // 撤下来——那条规则是玩家看到的撤板，这里一个字节都不碰。
    public bool Observe(int frame, bool scanComplete, bool parentCoverageComplete, bool measured, float wall,
        int unmeasuredControls = 0, bool pageActionsPending = false)
    {
        // IMGUI 一帧里可能 repaint 多次。同一帧的第二次观察不是新读数，答案就是上一次的答案。
        if (frame == _lastFrame) return _settled;
        if (_lastFrame != int.MinValue && frame < _lastFrame) Forget();
        _lastFrame = frame;
        if (_samples < int.MaxValue) _samples++;
        bool readable = measured && float.IsFinite(wall);
        // 一个 active 但量不到矩形的控件：墙的问题没有被回答，不能当成「这一帧没有墙」。
        // 这一页还没量到任何可按控件（`pageActionsPending`）：同样不是「这一页没有墙」，而是「这一页的墙还没出现」。
        bool answerable = scanComplete && parentCoverageComplete && !pageActionsPending && (readable || unmeasuredControls == 0);
        if (readable) _wall = ResultsRegion.HighestTop(wall, _wall);
        bool agree = _hasReading && _answerable && answerable
            && (readable
                ? float.IsFinite(_reading) && Math.Abs(wall - _reading) <= Tolerance
                : !float.IsFinite(_reading));
        _reading = readable ? wall : float.NaN;
        _hasReading = true;
        _answerable = answerable;
        _settled = agree;
        return _settled;
    }

    // 换页、离开结算页：这一页的墙和样本都不属于下一张页面。
    public void Forget()
    {
        _samples = 0; _lastFrame = int.MinValue;
        _reading = float.NaN; _wall = float.NaN;
        _hasReading = false; _answerable = false; _settled = false;
    }
}

// 一帧的客机失败页证据：固定页面几何的四项输入、两个只进日志的读数，和扫描自己的三个结论。
public readonly struct ResultClientFailureEvidence
{
    public readonly float AnchorBottom, ScreenWidth, ScreenHeight;
    public readonly float Gap, MinimumStripHeight;
    // 等待状态文字的上沿：只进日志，永远不是墙（`ResultClientFailureBand`）。
    public readonly float WaitingTextTop;
    // 这一帧真正可按的控件里最高的上沿（`FooterControls.PressableTop` 与命名按钮取最小 y）。
    public readonly float PressableTop;
    // 扫描自己的结构性结论：这一页被看见了吗。inactive 控件不影响这两项。
    public readonly bool ScanComplete, ParentCoverageComplete;
    // 这一帧 active 但量不到矩形的控件数：墙的问题没有被回答。
    public readonly int UnmeasurableControls;
    // 这一页还没有量到任何可按控件（`ResultClientPageActionPolicy.Pending`，只读 `pressableTop`）：这一帧因此不是
    // 「这一页没有墙」，而是「这一页的墙还没出现」，不能落板。本页动作的 active 状态（「回大厅」按钮在客机上永久
    // inactive、`HiddenPageActions` 常 ≥1）不携带逐帧信息，不再参与这个判断，只进日志。它只作用于首次落位，
    // `ResultClientFailureStrip.Measure` 不读它。
    public readonly bool PageActionsPending;

    public ResultClientFailureEvidence(float anchorBottom, float screenWidth, float screenHeight, float gap,
        float minimumStripHeight, float waitingTextTop, float pressableTop,
        bool scanComplete = true, bool parentCoverageComplete = true, int unmeasurableControls = 0,
        bool pageActionsPending = false)
    {
        AnchorBottom = anchorBottom; ScreenWidth = screenWidth; ScreenHeight = screenHeight;
        Gap = gap; MinimumStripHeight = minimumStripHeight;
        WaitingTextTop = waitingTextTop; PressableTop = pressableTop;
        ScanComplete = scanComplete; ParentCoverageComplete = parentCoverageComplete;
        UnmeasurableControls = unmeasurableControls;
        PageActionsPending = pageActionsPending;
    }
}

// 客机失败页条带的一次判定。
public enum ResultClientStripState
{
    // 条带可用，而且用的墙已经稳定：这是落板的那一帧。
    Placed = 0,
    // 这一帧的墙问题没有被回答：读法变了、页面还在亮控件，或有 active 控件量不到矩形。再等一帧——没有任何
    // 上限被当作放行；等不到就交给既有的 fail-open 逃生口。
    WallNotSettled = 1,
    // 页面固定几何这一帧不可用（锚点、屏幕尺寸、间距、最小板高任何一项不可信）。
    GeometryUnusable = 2,
    // 稳定后的墙把条带压得装不下最小板高。
    BandUnusable = 3,
    // 扫描看不见这一页（结构不完整 / 父节点覆盖不全）：它不能断言「这一页没有墙」。这与「页面把控件关掉了」
    // 不是一回事——inactive 控件被记为 hidden，扫描照旧完整。
    ScanIncomplete = 4,
}

// 客机失败页的完整判定，也是唯一入口：固定页面几何 → 扫描完整性 → 下界墙稳定门 → 客机条带。
//
// 生产（`ResultsDisplay.FailureStrip` 的客机分支）和回归测试都走这里。测试因此不必、也不能手工复刻三者的
// 调用顺序去凑一个绿色结果：它驱动的是页面真正会跑的那一段。
public static class ResultClientFailureStrip
{
    public static string Name(ResultClientStripState state)
    {
        switch (state)
        {
            case ResultClientStripState.Placed: return "client-strip-placed";
            case ResultClientStripState.WallNotSettled: return "client-wall-not-settled";
            case ResultClientStripState.GeometryUnusable: return "client-geometry-unusable";
            case ResultClientStripState.BandUnusable: return "client-band-unusable";
            case ResultClientStripState.ScanIncomplete: return "client-scan-incomplete";
            default: return "none";
        }
    }

    // 落板这一问：扫描看得见这一页，这一页量到了可按控件（`PageActionsPending` 为假），而且墙已经稳定。
    //
    // 1.19.42（独立评审 F5）：`Measure` 对缺失的门是「没有一致性可查」，`Decide` 里那道门**就是答案本身**，
    // 所以缺门时先拒绝（`WallNotSettled`，这一帧没有可以落板的墙），而不是对 null 解引用抛出去。
    public static ResultClientStripState Decide(int frame, ResultClientWallGate wall, in ResultClientFailureEvidence evidence,
        out ResultClientBandReading reading, out ResultsSafeArea fixedPage)
    {
        reading = ResultClientBandReading.None;
        if (!FixedPage(evidence, out fixedPage)) return ResultClientStripState.GeometryUnusable;
        if (!evidence.ScanComplete || !evidence.ParentCoverageComplete) return ResultClientStripState.ScanIncomplete;
        if (wall == null) return ResultClientStripState.WallNotSettled;
        if (!wall.Observe(frame, true, true, float.IsFinite(evidence.PressableTop), evidence.PressableTop,
                evidence.UnmeasurableControls, evidence.PageActionsPending))
            return ResultClientStripState.WallNotSettled;
        return Resolve(wall, evidence, fixedPage, out reading);
    }

    // 已经站在页面上的板：**不再过稳定门**——一堵「还没读到两次」的墙不能把站着的板撤掉（那正是玩家看到的
    // 撤板）。其余算术完全相同，门照旧被喂一帧，好让本页「最高的一堵墙」跟上这一帧的读数。
    //
    // 1.19.41：这一条**不读** `evidence.PageActionsPending`。那个事实只属于首次落位——「这一页还没量到可按控件」，
    // 从来不是撤掉一块已经站住的板的理由；把它接到这里，就是把玩家看到的撤板重新做一遍。
    public static ResultClientStripState Measure(int frame, ResultClientWallGate wall, in ResultClientFailureEvidence evidence,
        out ResultClientBandReading reading, out ResultsSafeArea fixedPage)
    {
        reading = ResultClientBandReading.None;
        if (!FixedPage(evidence, out fixedPage)) return ResultClientStripState.GeometryUnusable;
        if (wall != null)
            wall.Observe(frame, evidence.ScanComplete, evidence.ParentCoverageComplete,
                float.IsFinite(evidence.PressableTop), evidence.PressableTop, evidence.UnmeasurableControls,
                pageActionsPending: false);
        return Resolve(wall, evidence, fixedPage, out reading);
    }

    private static bool FixedPage(in ResultClientFailureEvidence evidence, out ResultsSafeArea fixedPage)
        => ResultsGuestPageGeometry.TryResolve(evidence.AnchorBottom, evidence.ScreenWidth, evidence.ScreenHeight,
            evidence.Gap, evidence.MinimumStripHeight, out fixedPage);

    private static ResultClientStripState Resolve(ResultClientWallGate wall, in ResultClientFailureEvidence evidence,
        ResultsSafeArea fixedPage, out ResultClientBandReading reading)
    {
        // 落板用的墙永远是「本页量到过的最高那一堵」：它只会比这一帧的读数更保守，绝不会把板放到一个这一页
        // 显示过的控件下面去；一帧都没量到就是 NaN，由固定页面几何作答。
        float wallTop = wall != null ? wall.Wall : evidence.PressableTop;
        if (!ResultClientFailureBand.TryResolve(fixedPage, evidence.ScreenHeight, evidence.MinimumStripHeight,
                evidence.WaitingTextTop, wallTop, out reading))
            return ResultClientStripState.BandUnusable;
        return ResultClientStripState.Placed;
    }
}
