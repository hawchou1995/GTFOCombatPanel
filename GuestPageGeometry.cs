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
    //
    // 1.19.45（用户纠正口径）：这个数只是**条带的下限封顶**，永远不是「量到了墙」的证据。它是「这块屏幕有多高」
    // 的事实，不是页面把什么东西画在了哪里；把它当作下界墙正是 run 3 里 band (488..1429)、900 高板压住
    // 1177 控件的那个错（`LogOutput` L12649–L12657）。
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

// 客机失败页的条带：上沿是任务失败标题的实测底边 + gap，下沿是这一页**真的画在下方的那个东西**的上沿减 clearance。
//
// 1.19.45（用户纠正，覆盖 1.19.38 的相反口径）：下界就是 `m_returnToLobby_text` 这行不可点击提示文字的
// 实际屏幕矩形上沿，和主机页拿「返回大厅」按钮上沿做下界是同一个道理——玩家读得到的那行字就是墙。真正可按
// 的控件（重开检查点 / 回大厅，客机上通常永久 inactive）照旧是墙，两者取**更靠上**的那一个，也就是更安全的
// 那一个：宁可板子矮一点，也不压住任何一处页面自己画出来的东西。页面固定几何（`ResultsGuestPageGeometry.PageFloor`，
// 2560x1440 上 1429.33）只做封顶，绝不充当墙；一行都量不到时不作答，由调用方保留原版页面并节流重测。
//
// 记录证据：
//   run 2（`LogOutput` L3926–L3928，2560x1440，title edge=467）  waitingText=1207 → band (488..1186)：那行字就是
//     墙，板子从来没有压到它。
//   run 3（同一份日志 L12649–L12657）  band (488..1429) 是按屏幕下界编出来的，900 高的板压住 1177 的控件，
//     `TryHold` 不能抬一块已经在带顶的板，只能整页 0.55 不透明度。
// 两种口径的差别就是这条规则：墙来自页面上量到的几何，不来自屏幕尺寸。
public readonly struct ResultClientBandReading
{
    // 板子要装进去的房间。
    public readonly ResultsSafeArea Strip;
    // 这一帧量到的等待状态文字上沿（只用于日志与「墙是哪一堵」的命名）。不可测时是 NaN。
    public readonly float WaitingTextTop;
    // 条带下沿真正用到的那堵墙的上沿（等待文字与可按控件里更靠上的那一堵，再与门记忆的最高墙取更靠上者）。
    public readonly float WallTop;
    // 下沿来自那行等待文字自己的上沿。
    public readonly bool WaitingTextTightened;
    // 下沿来自一个真正可按的控件。
    public readonly bool PressableTightened;

    public ResultClientBandReading(ResultsSafeArea strip, float waitingTextTop, float wallTop,
        bool waitingTextTightened, bool pressableTightened)
    {
        Strip = strip; WaitingTextTop = waitingTextTop; WallTop = wallTop;
        WaitingTextTightened = waitingTextTightened; PressableTightened = pressableTightened;
    }

    public float Top { get { return Strip.Top; } }
    public float Bottom { get { return Strip.Bottom; } }
    public float Height { get { return Strip.Height; } }
    public bool Fits { get { return Strip.Fits; } }
    // 下沿真的来自一堵量到的墙（等待文字或可按控件），而不是屏幕自己推出来的下界。
    public bool WallMeasured { get { return float.IsFinite(WallTop); } }
    // 这一版会不会压在那行等待文字上。下沿由「墙减 clearance」算出，而且墙取更靠上的那一堵，
    // 所以只要那行字量到了，答案永远是 false——这个字段留在日志里，是让下一次真机日志能直接读出它。
    public bool WaitingTextCovered
        => float.IsFinite(WaitingTextTop) && Bottom > WaitingTextTop + 0.5f;
    public static ResultClientBandReading None { get { return default; } }
}

public static class ResultClientFailureBand
{
    // 客机失败页的条带，从已经解析好的固定页面几何出发：
    //
    //   top     固定页面的上沿（锚点底边 + gap，由 `ResultsGuestPageGeometry` 算）
    //   bottom  min(页面上量到的墙 − clearance, 页面固定下沿)。墙 = `waitingTextTop` 与 `pressableTop` 里更靠上的
    //           那一堵，再与门记忆下来的最高墙（`wallTop`）取更靠上者，所以条带只会收紧、不会回摆。
    //   量不到   一条墙都没有（等待文字与可按控件都不可测）：明确回答不可信。**绝不**退回页面固定下沿——
    //           那正是 run 3 的错。
    //
    // `minimumHeight` 是客机最小板高：一条留不出它的条带同样是不可信，而不是硬塞一块小板进去。
    // 任何一项不可信就明确回答不可信，调用方保留原版页面、按既有节流重测。
    public static bool TryResolve(ResultsSafeArea fixedPage, float screenHeight, float minimumHeight,
        float waitingTextTop, float pressableTop, float wallTop, out ResultClientBandReading reading)
    {
        reading = ResultClientBandReading.None;
        if (!fixedPage.Fits) return false;
        if (!float.IsFinite(screenHeight) || screenHeight <= 0f) return false;
        if (!float.IsFinite(minimumHeight) || !(minimumHeight > 0f)) return false;
        float top = fixedPage.Top;
        float cap = fixedPage.Bottom;
        if (!float.IsFinite(top) || !float.IsFinite(cap)) return false;
        if (top < 0f) top = 0f;
        if (top > screenHeight) top = screenHeight;
        if (cap > screenHeight) cap = screenHeight;
        if (!(cap > top)) return false;
        // 这一帧看到的墙，和这一页见过的最高墙，取更靠上的那一堵：一条已经显示过的控件不能因为这一帧又量不到
        // 就被忘掉，而页面自己后来的动画只能把条带收紧。
        float wall = ResultsRegion.HighestTop(waitingTextTop, pressableTop);
        wall = ResultsRegion.HighestTop(wall, wallTop);
        if (!float.IsFinite(wall)) return false;
        float foot = wall - ResultsBounds.Clearance(screenHeight);
        if (foot > cap) foot = cap;
        if (!(foot > top)) return false;
        if (foot - top < minimumHeight) return false;
        bool byWaitingText = float.IsFinite(waitingTextTop) && Math.Abs(wall - waitingTextTop) <= 0.5f;
        bool byPressable = !byWaitingText && float.IsFinite(pressableTop) && Math.Abs(wall - pressableTop) <= 0.5f;
        reading = new ResultClientBandReading(new ResultsSafeArea(top, foot), waitingTextTop, wall, byWaitingText, byPressable);
        return true;
    }
}

// ---------------------------------------------------------------------------------------------------
// 一页的下界语义：严格路径与 fail-open 路径读同一份事实
// ---------------------------------------------------------------------------------------------------
//
// 独立评审的具体缺陷：`ResultsDisplay.FallbackPlacement` 把量到的「正在等待厅主」状态文字上沿（真机 1207/1228）
// 当「页脚文字墙」交给 `ResultsBounds.TryResolve`，而严格路径从 1.19.38 起把它拿掉、只认页面固定几何，
// 于是 2560x1440 的同一张客机失败页上：严格路径 488..1429（941px），fail-open 488..1186（698px）——
// 同一张页面两个说法。
//
// 1.19.45（用户纠正）：反过来才对。那行不可点击的提示文字**就是**下界，和主机页的按钮上沿同一性质；固定页面几何
// 只是封顶。所以这一层现在把「等待文字 = 这一页的页脚墙」原样交出去，两条路径都从它出发：
//
//   * 客机失败页：`FooterTextTop` 就是那行等待文字自己的上沿（`FooterTextSource` 说出它是画出来的文字还是
//     代替它的控件），`PressableTop` 是真正可按的控件里最高的一堵；页面下沿（`PageFloor`）只用来夹住条带，
//     永远不被当成「量到了墙」。
//   * 其余页面（主机失败页、主机/客机成功页）：三个读数原样返回，页面下沿不设限（NaN = 窗口脚），
//     所以主机路径一个字节都没变。
//
// 「扫描看不全」不在这里放行：inactive 控件被扫描记为 hidden，active 却量不到矩形仍会让
// `ResultFallbackPolicy.PageReadable` 说不（预算路线），F8 照旧由玩家自己授权（`MayAnswer`）。
public readonly struct ResultPageWalls
{
    // 这一页是客机失败页：它的下界是「等待文字 / 可按控件里更靠上的那一堵」，页面下沿只做封顶。
    public readonly bool GuestFailure;
    // 真正可按控件的最高上沿（`FooterControls.PressableTop` 与命名按钮取最小 y）。NaN = 没有量到可按控件。
    public readonly float PressableTop;
    // 可以当成「页脚文字墙」交出去的读数：客机失败页是那行等待文字自己的上沿，主机页 / 客机成功页是等待或页脚文字。
    public readonly float FooterTextTop;
    // 那堵文字墙的来源，好让日志继续说出「画出来的文字」还是「代替它的控件」。
    public readonly ResultBoundSource FooterTextSource;
    // 这一页真实的下沿：客机失败页的固定页面几何下界；NaN = 这一页一直用到窗口脚。只做封顶，不是墙。
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
    // `footerTextTop` 是那一页真正画在下方的文字墙（客机失败页：`m_returnToLobby_text` 的布局上沿）。
    public static ResultPageWalls Resolve(bool guestFailure, float screenHeight, float footerControlTop,
        float scanPressableTop, float footerTextTop, ResultBoundSource footerTextSource)
    {
        if (!guestFailure)
            return new ResultPageWalls(false, footerControlTop, footerTextTop, footerTextSource, float.NaN);
        // 客机失败页：文字墙与可按控件都是墙（`ResultsBounds` 会取更靠上的那一堵），页面下沿只做封顶。
        float pressable = ResultsRegion.HighestTop(footerControlTop, scanPressableTop);
        return new ResultPageWalls(true, pressable, footerTextTop,
            float.IsFinite(footerTextTop) && footerTextSource == ResultBoundSource.None
                ? ResultBoundSource.FooterText : footerTextSource,
            ResultsGuestPageGeometry.PageFloor(screenHeight));
    }
}


// ---------------------------------------------------------------------------------------------------
// 这一页的墙有没有证据：一个诚实的谓词
// ---------------------------------------------------------------------------------------------------
//
// 记录下来的那一页（`LogOutput.snapshot.log` 4426–4441，客机失败页，2560x1440）是这么错的：头两帧扫描完整、
// 但页面上一个可按控件都没有（页面自己的「重开 checkpoint」/「回大厅」还没被点亮），两帧一致就被读成「这一页没有
// 墙」，条带按固定页面几何 381..1429 放了一块 1987x990 的板；下一帧真实控件出现（1175/1181/1189），条带收紧到
// 802px，站着的板真的压在控件上，于是整块板被撤掉、原版页面回来。
//
// 1.19.42（独立评审 F3）：这一段以前收五个入参，其中四个在客机失败页上不携带任何逐帧信息：
//
//   * 「回大厅」按钮在客机上**永久** inactive（角色带来的，不随帧而来），扫描于是把它记成 hidden，
//     `HiddenPageActions` 常 ≥1；
//   * 于是 `hiddenPageActions > 0` 这一条在客机上恒真——裁掉它之后，整个谓词等价于 `!IsFinite(pressableTop)`，
//     这就是「一个恒真入参冒充证据」。
//
// 1.19.45（用户纠正）：这个谓词要问的不是「有没有可按控件」，而是「这一页有没有量到任何一堵墙」。客机失败页上
// 那行不可点击的等待文字就是墙，所以「一个可按控件都没有」不再等于「没有证据」——它是这一页最常见、也最正常的
// 状态。真正没有证据的是「那行字和可按控件都量不到」：那时这一帧既不能说有墙、也不能说没有墙。
public static class ResultClientPageActionPolicy
{
    public static bool Pending(float waitingTextTop, float pressableTop)
        => !float.IsFinite(waitingTextTop) && !float.IsFinite(pressableTop);
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
//   * 两帧**可回答且完整**的读数一致（都量到同一堵墙，相差不超过 `Tolerance`）→ 已稳定，可以落板。
//   * **任何帧数都不能自己授权落板。** `Samples` 只用于诊断和重试节奏，`Settled` 是唯一的放行条件；「等够
//     多少帧就把没量到的墙当成量到了」在这条链上一个字节都没有。一直不稳定的页面由**已有的**有界逃生口
//     回答：`ResultFallbackPolicy.MissBudget`（60 帧拒收）之后的 fail-open 板——它让开每一个量到的控件、
//     跨墙时画淡，并且从不声称这一页被证明过。
//   * 一个 active、但这一帧量不到矩形的控件不是「没有控件」：它让这一帧**不可回答**，既不算一次一致读数，
//     也清不掉已经读到的墙（`ResultInteractionTally` 的规则在这一层一个字都没改）。
//   * 扫描不完整（`controls.Complete` / `ParentCoverageComplete`）同样不算「这一页没有墙」：客机失败页在
//     生产里**没有**外层的完整扫描闸门（`Placement`/`BoardStillSafe` 的 `!GuestClient() && !Complete` 只约束
//     主机），所以这个要求必须落在这条门里，不能靠调用顺序顺带满足。inactive 控件不影响完整性——它们被扫描
//     记为 hidden，`Complete` 照旧为真，这正是「页面把控件关掉了」与「扫描看不见这一页」的区别。
//
// 1.19.45（用户纠正）：门读的墙是「等待文字上沿」与「可按控件上沿」里更靠上的那一堵（`Observe` 只收一个
// 合成后的读数），所以「一个可按控件都没有」的帧不再是不可回答——记录下来的那两帧本来就有那行字。
//
// 文字仍可能随原生动画移动。可信边界收紧立即保留，放宽则需要连续两次可信读数一致；
// 不完整样本不会写入保留边界。晚到互动控件仍由逐帧碰撞保护处理，不声称两帧稳定能预知未来控件。
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
    // 最近可信墙：收紧立即更新，放宽等待稳定；NaN 表示从未得到可信墙。
    public float Wall { get { return _wall; } }

    // `wallTop` 是这一帧合成后的下界墙（等待文字与可按控件里更靠上的那一堵，NaN = 一条都没有）；
    // `scanComplete`/`parentCoverageComplete` 是扫描自己的结构性结论；`unmeasuredControls` 是这一帧 active
    // 但量不到矩形的控件数。三个事实合起来回答「这一帧的墙问题被回答了吗」。
    //
    // `Measure`（已经站着的板）用同一份喂法，但它不过 `Settled`：一堵「还没读到两次」的墙不能把板撤掉。
    public bool Observe(int frame, bool scanComplete, bool parentCoverageComplete, float wallTop,
        int unmeasuredControls = 0)
    {
        // IMGUI 一帧里可能 repaint 多次。同一帧的第二次观察不是新读数，答案就是上一次的答案。
        if (frame == _lastFrame) return _settled;
        if (_lastFrame != int.MinValue && frame < _lastFrame) Forget();
        _lastFrame = frame;
        if (_samples < int.MaxValue) _samples++;
        bool readable = float.IsFinite(wallTop);
        // 一条墙都没量到：不是「这一页没有墙」，而是「这一页的墙还没出现」，这一帧不回答任何问题。
        // 一个 active 但量不到矩形的控件同样如此（它可能就站在墙上方，没人知道）。
        bool answerable = scanComplete && parentCoverageComplete && readable && unmeasuredControls == 0;
        bool agree = _hasReading && _answerable && answerable
            && readable && float.IsFinite(_reading) && Math.Abs(wallTop - _reading) <= Tolerance;
        // Blind/incomplete samples cannot poison the retained edge. Tighten immediately for safety;
        // relax only after two trustworthy readings agree, so an animated early edge is not permanent.
        if (answerable && (!float.IsFinite(_wall) || wallTop < _wall || agree)) _wall = wallTop;
        _reading = readable ? wallTop : float.NaN;
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

// 一帧的客机失败页证据：固定页面几何的四项输入、两个下界读数，和扫描自己的三个结论。
public readonly struct ResultClientFailureEvidence
{
    public readonly float AnchorBottom, ScreenWidth, ScreenHeight;
    public readonly float Gap, MinimumStripHeight;
    // 等待状态文字的上沿：客机失败页的下界墙之一（用户口径，1.19.45）。不可测时是 NaN。
    public readonly float WaitingTextTop;
    // 这一帧真正可按的控件里最高的上沿（`FooterControls.PressableTop` 与命名按钮取最小 y）。
    public readonly float PressableTop;
    // 扫描自己的结构性结论：这一页被看见了吗。inactive 控件不影响这两项。
    public readonly bool ScanComplete, ParentCoverageComplete;
    // 这一帧 active 但量不到矩形的控件数：墙的问题没有被回答。
    public readonly int UnmeasurableControls;

    public ResultClientFailureEvidence(float anchorBottom, float screenWidth, float screenHeight, float gap,
        float minimumStripHeight, float waitingTextTop, float pressableTop,
        bool scanComplete = true, bool parentCoverageComplete = true, int unmeasurableControls = 0)
    {
        AnchorBottom = anchorBottom; ScreenWidth = screenWidth; ScreenHeight = screenHeight;
        Gap = gap; MinimumStripHeight = minimumStripHeight;
        WaitingTextTop = waitingTextTop; PressableTop = pressableTop;
        ScanComplete = scanComplete; ParentCoverageComplete = parentCoverageComplete;
        UnmeasurableControls = unmeasurableControls;
    }

    // 这一帧合成后的下界墙：两个读数里更靠上的那一堵；一条都没有是 NaN。
    public float WallTop { get { return ResultsRegion.HighestTop(WaitingTextTop, PressableTop); } }
    // 这一帧连一堵墙都没量到（`ResultClientPageActionPolicy`）：它既不是「有墙」也不是「没有墙」。
    public bool WallPending { get { return ResultClientPageActionPolicy.Pending(WaitingTextTop, PressableTop); } }
}

// 客机失败页条带的一次判定。
public enum ResultClientStripState
{
    // 条带可用，而且用的墙已经稳定：这是落板的那一帧。
    Placed = 0,
    // 这一帧的墙问题没有被回答：读数变了、页面上量不到任何墙，或有 active 控件量不到矩形。再等一帧——没有
    // 任何上限被当作放行；等不到就交给既有的 fail-open 逃生口。
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

    // 落板这一问：扫描看得见这一页，这一页量到了墙，而且墙已经稳定。
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
        if (!wall.Observe(frame, true, true, evidence.WallTop, evidence.UnmeasurableControls))
            return ResultClientStripState.WallNotSettled;
        return Resolve(wall, evidence, fixedPage, out reading);
    }

    // 已经站在页面上的板：**不再过稳定门**——一堵「还没读到两次」的墙不能把站着的板撤掉（那正是玩家看到的
    // 撤板）。其余算术完全相同，门照旧被喂一帧，好让本页「最高的一堵墙」跟上这一帧的读数。
    public static ResultClientStripState Measure(int frame, ResultClientWallGate wall, in ResultClientFailureEvidence evidence,
        out ResultClientBandReading reading, out ResultsSafeArea fixedPage)
    {
        reading = ResultClientBandReading.None;
        if (!FixedPage(evidence, out fixedPage)) return ResultClientStripState.GeometryUnusable;
        if (wall != null)
            wall.Observe(frame, evidence.ScanComplete, evidence.ParentCoverageComplete,
                evidence.WallTop, evidence.UnmeasurableControls);
        return Resolve(wall, evidence, fixedPage, out reading);
    }

    private static bool FixedPage(in ResultClientFailureEvidence evidence, out ResultsSafeArea fixedPage)
        => ResultsGuestPageGeometry.TryResolve(evidence.AnchorBottom, evidence.ScreenWidth, evidence.ScreenHeight,
            evidence.Gap, evidence.MinimumStripHeight, out fixedPage);

    private static ResultClientStripState Resolve(ResultClientWallGate wall, in ResultClientFailureEvidence evidence,
        ResultsSafeArea fixedPage, out ResultClientBandReading reading)
    {
        // 当前可测的文字/控件和门保留的可信墙共同约束条带；未稳定的放宽不能抢先扩板。
        float wallTop = wall != null ? wall.Wall : evidence.WallTop;
        if (!ResultClientFailureBand.TryResolve(fixedPage, evidence.ScreenHeight, evidence.MinimumStripHeight,
                evidence.WaitingTextTop, evidence.PressableTop, wallTop, out reading))
            return ResultClientStripState.BandUnusable;
        return ResultClientStripState.Placed;
    }
}
