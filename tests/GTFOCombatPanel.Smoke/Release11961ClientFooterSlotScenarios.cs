using System;
using System.IO;
using HawChow.GTFOCombatPanel;

// 1.19.61（评审轮修正）：客机失败页页脚的**权威来源**与**稳定/放宽规则**。
//
// 记录下来的页面（2026-11-25，Survival，2560x1440，本机是客机）：
//   L4862 waitingText=1344 / L4870 waitingText=1334 -> band (781..1312) / L4878 band 1371 / L4883 boardBottom=1373,
//   footerTop=1338 -> 每一次 repaint 都 board-reaches-button。
//   像素调查（`survival-prompt-1125\visual-evidence.md` §4/§5）：这一页真正画出来的等待提示在 y 1179..1218，而
//   1.19.60 的板 826..1373 把它整段盖住（该处亮度掉到 0.650）。
//
// 评审否掉了第一次尝试的两处做法，这份回归按评审后的规则重写：
//   * 边界**带来源**；收紧立即，放宽只认「同一来源连续两次可信样本一致」，低可信来源（布局框）永远挤不掉
//     原生槽位或字形。
//   * `Settled` 是「同一来源在两个不同帧上各给出一次可信读数且一致」，不是「保留值这一帧没动」；1344→1334→1344
//     这样的来回不构成首显授权，盲帧切断连续性。
//   * 权威只有两路原生几何：客机上 `m_btnGoToLobby` 自己的已布局矩形（主机页量的是同一个对象），与
//     `m_returnToLobby_text` 真正渲染的字形（mesh 顶点）。布局框、别的可按控件、屏幕下界都不授权首显或 F8。
//   * 一条都量不到就 fail closed：保留原版、按既有节流重测；扩展过墙的兜底带也不许画。
internal static class Release11961Scenarios
{
    // ---- 记录页面的数（2560x1440） ---------------------------------------------------------------------
    private const float Width = 2560f, Height = 1440f;
    private const float TitleBottom = 760f;        // L4861/L4872 edge=760
    private const float MessageGap = 16f;          // ResultsDisplay.MessageGap
    private const float MinimumStrip = 96f;        // ResultsDisplay.MinimumGuestStripHeight
    private const float BoardFloor = 270f;         // L4883 need=270px
    private const float Layout1 = 1344f;           // L4862
    private const float Layout2 = 1334f;           // L4870
    private const float Layout3 = 1392.3333f;      // L4878 band 1371 + clearance
    private const float Layout4 = 1338f;           // L4883 footerTop
    private const float DrawnHintTop = 1179f;      // visual-evidence §4（字形 1179..1218）
    private const float HostSlotTop = 1338f;       // 与 L4883 的 footerTop 同一个槽位读数
    private const float RecordedBandBottom = 1371f;      // L4878/L4881
    private const float RecordedPlateBottom = 1373f;     // L4883
    private const float RecordedPlateHeight = 548f;      // 495 表 + 53 表头

    internal static void RunInto(Action<bool, string> check)
    {
        TheSlotPolicyOnlyTrustsTheSameNativeSlot(check);
        TheHostSlotNeedsCorroboration(check);
        TheGateOnlyRelaxesOnTheSameSourceTwice(check);
        TheRecordedOscillationNeverFirstDisplays(check);
        TheRecordedPageIsBoundedByItsDrawnSlot(check);
        NoAuthorityMeansNoFirstDisplayAndNoForcedBand(check);
        AStandingBoardIsTightenedOnTheFrameTheFooterRises(check);
        TheProductionWiringIsWhatThisFileDrives(check);
    }

    private static float Clearance => ResultsBounds.Clearance(Height);

    private static bool Band(float slotTop, float pressableTop, out ResultClientBandReading reading, out ResultsSafeArea fixedPage)
    {
        reading = ResultClientBandReading.None;
        fixedPage = default;
        if (!ResultsGuestPageGeometry.TryResolve(TitleBottom, Width, Height, MessageGap, MinimumStrip, out fixedPage))
            return false;
        return ResultClientFailureBand.TryResolve(fixedPage, Height, MinimumStrip, slotTop, pressableTop, float.NaN, out reading);
    }

    // ---- A: 权威只有同一个原生返回位置的两路几何 ----------------------------------------------------------
    private static void TheSlotPolicyOnlyTrustsTheSameNativeSlot(Action<bool, string> check)
    {
        check(near(ResultClientSlotPolicy.Authority(HostSlotTop, DrawnHintTop, out ResultClientWallSource both), DrawnHintTop, 0.02f)
              && both == ResultClientWallSource.HintGlyph,
            $"the rendered glyph ({DrawnHintTop:0}) is higher than the native return slot ({HostSlotTop:0}), so it is the authority - this is the line the player really reads");
        check(near(ResultClientSlotPolicy.Authority(HostSlotTop, float.NaN, out ResultClientWallSource hostOnly), HostSlotTop, 0.02f)
              && hostOnly == ResultClientWallSource.HostSlot,
            "without glyphs the page's own return slot is the authority: the same native object the host page measures, so a client page is not a second layout rule");
        check(near(ResultClientSlotPolicy.Authority(float.NaN, DrawnHintTop, out ResultClientWallSource glyphOnly), DrawnHintTop, 0.02f)
              && glyphOnly == ResultClientWallSource.HintGlyph,
            "and without the slot rect the rendered glyphs still bound the page");
        check(!float.IsFinite(ResultClientSlotPolicy.Authority(float.NaN, float.NaN, out ResultClientWallSource none))
              && none == ResultClientWallSource.None,
            "two unmeasurable native sources are 'no authority at all' - the caller keeps the vanilla page instead of inventing a bound");
        check(near(ResultClientSlotPolicy.Wall(HostSlotTop, float.NaN, 1200f, out ResultClientWallSource pressed), 1200f, 0.02f)
              && pressed == ResultClientWallSource.Pressable,
            "a really pressable control above the slot still tightens the wall");
        check(near(ResultClientSlotPolicy.Wall(HostSlotTop, DrawnHintTop, 1200f, out ResultClientWallSource below), DrawnHintTop, 0.02f)
              && below == ResultClientWallSource.HintGlyph,
            "while one below the drawn glyphs cannot widen it: the highest reading still wins");
        check(!float.IsFinite(ResultClientSlotPolicy.Wall(float.NaN, float.NaN, 1200f, out _)),
            "but a pressable control alone never becomes the page's bound: pressing something elsewhere cannot authorise this board");
        check(ResultClientSlotPolicy.Trust(ResultClientWallSource.SlotLayout) < ResultClientSlotPolicy.Trust(ResultClientWallSource.HostSlot)
              && ResultClientSlotPolicy.Trust(ResultClientWallSource.HostSlot) < ResultClientSlotPolicy.Trust(ResultClientWallSource.HintGlyph),
            "the trust order is explicit: the layout box is the weakest reading, the native slot stronger, the rendered glyphs strongest");
    }

    // ---- A2: 主机同款槽位读数需要佐证（评审 #3 的残口） --------------------------------------------------
    //
    // 预制件证据（`native-slot-evidence.md` §1.2/§2）：`GoToLobby` 上 TMP 90058(`m_returnToLobby_text`) 与
    // CM_Item 93181(`m_btnGoToLobby`) 共用 RectTransform 86911（550x100）。所以「同一个槽位」可以证明，但**裸矩形本身**
    // 不是几何证据：`TryScreenRect` 在没有可用 renderer 时会退回它，1.19.60 的 1338 就是这么变成权威的。
    private static void TheHostSlotNeedsCorroboration(Action<bool, string> check)
    {
        // 1) 反例：这段文字此刻应该在画、却投不出来，只剩同一个节点上的裸矩形（550x100 布局盒）→ 不授权。
        check(!ResultClientSlotPolicy.HostSlotAuthorised(entityMeasured: false, glyphExpected: true,
                glyphMeasured: false, sameEntity: true),
            "a waiting line that should be drawn right now but could not be projected authorises nothing: the bare 550x100 layout box on the same node is not the geometry the page draws (this is the residual 1338 hole, and it must stay closed)");
        // 2) 正向：真实字形量到了 / 按钮自己的实体几何量到了，都可以（不要求可点击）。
        check(ResultClientSlotPolicy.HostSlotAuthorised(false, true, glyphMeasured: true, sameEntity: false)
              && ResultClientSlotPolicy.HostSlotAuthorised(entityMeasured: true, glyphExpected: true, glyphMeasured: false, sameEntity: false),
            "the two kinds of real evidence do authorise: the waiting line's rendered glyphs, or the button entity's own measured geometry - and neither asks whether the control can be clicked");
        // 3) 该画的字本来就不在：只有「按钮与文字是同一个节点」的实体证明才授权。
        check(ResultClientSlotPolicy.HostSlotAuthorised(false, false, false, sameEntity: true)
              && !ResultClientSlotPolicy.HostSlotAuthorised(false, false, false, sameEntity: false),
            "when the slot text is not being drawn at all (empty, switched off or fully transparent) the control's own slot rectangle counts only with the prefab's same-node proof; on some other node it is just a bare rect again");
        // 4) 与严格路径连起来：不授权 ⇒ 没有槽位权威 ⇒ 首显被拒（保留原版重测）。
        bool authorised = ResultClientSlotPolicy.HostSlotAuthorised(false, true, false, true);
        float authority = ResultClientSlotPolicy.Authority(authorised ? HostSlotTop : float.NaN, float.NaN,
            out ResultClientWallSource source);
        var gate = new ResultClientWallGate();
        var evidence = new ResultClientFailureEvidence(TitleBottom, Width, Height, MessageGap, MinimumStrip,
            authority, float.NaN, true, true, 0, source);
        check(!authorised && !float.IsFinite(authority)
              && ResultClientFailureStrip.Decide(900, gate, evidence, out _, out _) == ResultClientStripState.WallNotSettled
              && ResultClientFailureStrip.Decide(901, gate, evidence, out _, out _) == ResultClientStripState.WallNotSettled,
            "and the consequence is the intended one: no authority means no first display on any number of frames - vanilla is kept and the page is measured again, instead of fitting the 1338 box");
    }

    // ---- B: 同源两次一致才放宽；盲帧切断连续性 -----------------------------------------------------------
    private static void TheGateOnlyRelaxesOnTheSameSourceTwice(Action<bool, string> check)
    {
        var gate = new ResultClientWallGate();
        // 1) 收紧立即：字形 1179 在保留的槽位 1300 之上。
        check(!gate.Observe(100, true, true, 1300f, 0, ResultClientWallSource.HostSlot) && near(gate.Wall, 1300f, 0.02f)
              && gate.WallSource == ResultClientWallSource.HostSlot,
            "the first trustworthy reading of the native slot is retained, and one sample is not an agreement");
        check(!gate.Observe(101, true, true, 1179f, 0, ResultClientWallSource.HintGlyph) && near(gate.Wall, 1179f, 0.02f)
              && gate.WallSource == ResultClientWallSource.HintGlyph,
            "a higher source tightens immediately, even on its first sample - the safer edge needs no second opinion");
        // 2) 低可信来源连续两次一致也不许把字形挤下去。
        check(!gate.Observe(102, true, true, Layout4, 0, ResultClientWallSource.SlotLayout) && near(gate.Wall, 1179f, 0.02f),
            "the 1338 layout box cannot replace the glyph: a lower-trust reading is not evidence about the drawn line");
        check(!gate.Observe(103, true, true, Layout3, 0, ResultClientWallSource.SlotLayout) && near(gate.Wall, 1179f, 0.02f)
              && gate.WallSource == ResultClientWallSource.HintGlyph,
            "two agreeing layout samples still cannot (they are a different source, and a weaker one) - this is the exact step that produced the recorded 1371/1373");
        // 3) 同源两次一致 → 放宽。
        var relax = new ResultClientWallGate();
        relax.Observe(200, true, true, 1300f, 0, ResultClientWallSource.HostSlot);
        check(!relax.Observe(201, true, true, 1340f, 0, ResultClientWallSource.HostSlot) && near(relax.Wall, 1300f, 0.02f),
            "a single lower sample of the same source does not relax the retained bound");
        check(relax.Observe(202, true, true, 1340f, 0, ResultClientWallSource.HostSlot) && near(relax.Wall, 1340f, 0.02f)
              && relax.WallSource == ResultClientWallSource.HostSlot,
            "and the same source agreeing twice does relax it: the page really drew its slot lower, and the board may follow");
        // 4) 盲帧切断连续性。
        var blind = new ResultClientWallGate();
        check(!blind.Observe(300, true, true, 1179f, 0, ResultClientWallSource.HintGlyph),
            "one glyph sample is not an agreement");
        check(!blind.Observe(301, true, true, float.NaN, 0, ResultClientWallSource.HintGlyph) && !blind.Settled,
            "a blind frame is not an agreement either, and it breaks the streak");
        check(!blind.Observe(302, true, true, 1179f, 0, ResultClientWallSource.HintGlyph) && !blind.Settled,
            "the same value after a blind frame is a new first sample, so it still does not settle");
        check(blind.Observe(303, true, true, 1179f, 0, ResultClientWallSource.HintGlyph) && blind.Settled
              && near(blind.Wall, 1179f, 0.02f),
            "two consecutive complete samples of the same source, on different frames, do");
        // 5) 不同来源之间同样不构成一致性。
        var mixed = new ResultClientWallGate();
        check(!mixed.Observe(400, true, true, 1179f, 0, ResultClientWallSource.HintGlyph)
              && !mixed.Observe(401, true, true, 1179f, 0, ResultClientWallSource.HostSlot)
              && !mixed.Settled,
            "the same number from two different sources is not 'the same reading twice'; the source has to agree too");
    }

    // ---- C: 1344 → 1334 → 1344 不得首显 ----------------------------------------------------------------
    private static void TheRecordedOscillationNeverFirstDisplays(Action<bool, string> check)
    {
        var gate = new ResultClientWallGate();
        float[] readings = { Layout1, Layout2, Layout1 };
        int placed = 0;
        // 下标驱动，**不用 `Array.IndexOf(readings, reading)`**：重复读数会让两帧拿到同一个帧号，门把第二次当成
        // 「同一帧的又一次 repaint」直接返回上一帧答案（`frame == _lastFrame`），于是「帧回退」在这里是假阳性。
        // 三个读数必须是三个不同帧，而且绝不调用 `Forget()`（否则计数被重置，测的就不是连续性了）。
        for (int index = 0; index < readings.Length; index++)
        {
            var evidence = new ResultClientFailureEvidence(TitleBottom, Width, Height, MessageGap, MinimumStrip,
                readings[index], float.NaN, true, true, 0, ResultClientWallSource.SlotLayout);
            if (ResultClientFailureStrip.Decide(500 + index, gate, evidence, out _, out _)
                == ResultClientStripState.Placed) placed++;
        }
        check(placed == 0 && gate.Samples == readings.Length,
            $"the recorded 1344 -> 1334 -> 1344 oscillation authorises nothing ({placed} placements on {gate.Samples} distinct frames): each frame is a different reading from the same source, and the settle question is not 'did the retained value move'");
        check(!gate.Settled,
            "and the gate says so plainly: no two consecutive samples agreed");
        var steady = new ResultClientWallGate();
        var same = new ResultClientFailureEvidence(TitleBottom, Width, Height, MessageGap, MinimumStrip, Layout1, float.NaN,
            true, true, 0, ResultClientWallSource.SlotLayout);
        check(ResultClientFailureStrip.Decide(600, steady, same, out _, out _) == ResultClientStripState.WallNotSettled
              && ResultClientFailureStrip.Decide(601, steady, same, out _, out _) == ResultClientStripState.Placed,
            "while a page whose own slot reads the same value on two complete frames does place - the settle rule is a real two-sample question, not a permanent refusal");
    }

    // ---- D: 记录下来的页面由它自己画出来的槽位定界 --------------------------------------------------------
    private static void TheRecordedPageIsBoundedByItsDrawnSlot(Action<bool, string> check)
    {
        // 1) 旧读数的红色数字：布局框 1392.33 当墙时条带 1371、板底 1373、判据 1334。
        check(Band(Layout3, float.NaN, out ResultClientBandReading layoutBand, out _)
              && near(layoutBand.Bottom, RecordedBandBottom, 0.02f),
            $"the layout reading reproduces the recorded band foot {RecordedBandBottom:0} - this is the transcription of L4878/L4881");
        var layoutStrip = new BoardStrip(layoutBand.Top, layoutBand.Bottom, true, Layout3);
        check(ResultBoardHold.TryPlace(Width, Height, layoutStrip, 370f, RecordedPlateHeight, 0f, out LayoutRect redPlate, 2f, bottomAligned: true)
              && near(redPlate.Bottom, RecordedPlateBottom, 0.02f),
            $"and it puts a 370x548 plate at y {redPlate.Y:0}..{redPlate.Bottom:0} - the pixel survey measured the drawn board at 826..1373");
        check(redPlate.Bottom > Layout4 - ResultBoardHold.ButtonClearance + 2f,
            $"BEFORE THE FIX (red): boardBottom={redPlate.Bottom:0} against the {Layout4 - ResultBoardHold.ButtonClearance + 2f:0} line - exactly L4883's board-reaches-button");
        // 2) 绿色：字形 1179 是权威。
        check(Band(DrawnHintTop, float.NaN, out ResultClientBandReading slotBand, out _)
              && near(slotBand.Bottom, DrawnHintTop - Clearance, 0.02f),
            $"AFTER THE FIX (green): the drawn glyph is the bound, so the band foot is {slotBand.Bottom:0.##} - the board can no longer be fitted over the line the player reads (1179..1218)");
        var slotStrip = new BoardStrip(slotBand.Top, slotBand.Bottom, true, DrawnHintTop);
        check(!ResultBoardHold.TryPlace(Width, Height, slotStrip, 370f, RecordedPlateHeight, 0f, out _, 2f, bottomAligned: true),
            "the recorded 548px plate does not fit a band that stops above the glyphs, so it is re-sized instead of drawn outside its band");
        check(ResultBoardHold.TryPlace(Width, Height, slotStrip, 370f, 360f, 0f, out LayoutRect greenPlate, 2f, bottomAligned: true)
              && greenPlate.Bottom <= DrawnHintTop - ResultBoardHold.ButtonClearance + 2f
              && near(DrawnHintTop - greenPlate.Bottom, 19.33f, 0.05f),
            $"the band between the title and the drawn line ({slotBand.Top:0.##}..{slotBand.Bottom:0.##}) holds a 360px plate at {greenPlate.Y:0}..{greenPlate.Bottom:0}, {DrawnHintTop - greenPlate.Bottom:0.##}px clear of the glyphs");
        // 3) 记录页面的真实序列：字形稳定 → 第 2 帧落板；之后布局框的两种排版都不能放宽。
        var gate = new ResultClientWallGate();
        var evidence = new ResultClientFailureEvidence(TitleBottom, Width, Height, MessageGap, MinimumStrip,
            DrawnHintTop, float.NaN, true, true, 0, ResultClientWallSource.HintGlyph);
        check(ResultClientFailureStrip.Decide(700, gate, evidence, out _, out _) == ResultClientStripState.WallNotSettled
              && ResultClientFailureStrip.Decide(701, gate, evidence, out ResultClientBandReading placedBand, out _) == ResultClientStripState.Placed
              && near(placedBand.Bottom, DrawnHintTop - Clearance, 0.02f),
            "the recorded page is placed on its second glyph reading, at the glyph bound (the same two-frame question as everywhere else)");
        var loose = new ResultClientFailureEvidence(TitleBottom, Width, Height, MessageGap, MinimumStrip,
            Layout3, float.NaN, true, true, 0, ResultClientWallSource.SlotLayout);
        check(ResultClientFailureStrip.Measure(702, gate, loose, out ResultClientBandReading afterLoose, out _) == ResultClientStripState.Placed
              && near(afterLoose.Bottom, DrawnHintTop - Clearance, 0.02f) && near(gate.Wall, DrawnHintTop, 0.02f),
            "and a later frame that reads the 1392.33 layout box cannot widen it: the standing board keeps the drawn bound");
    }

    // ---- E: 没有权威 = 不首显、F8 也不给带；扩展过墙的兜底带也拒收 ------------------------------------------
    private static void NoAuthorityMeansNoFirstDisplayAndNoForcedBand(Action<bool, string> check)
    {
        var gate = new ResultClientWallGate();
        // 只有布局框与一个可按控件：两者都不授权。
        var noSlot = new ResultClientFailureEvidence(TitleBottom, Width, Height, MessageGap, MinimumStrip,
            float.NaN, 1200f, true, true, 0, ResultClientWallSource.None);
        check(ResultClientFailureStrip.Decide(800, gate, noSlot, out _, out _) == ResultClientStripState.WallNotSettled
              && !float.IsFinite(gate.Wall),
            "a page with only a layout box and a pressable control has no authority: the strict path refuses the first display instead of fitting the 1338 box");
        // 兜底/强绘：同一条判断。
        ResultPageWalls none = ResultPageLowerBoundPolicy.Resolve(guestFailure: true, screenHeight: Height,
            footerControlTop: float.NaN, scanPressableTop: 1200f, footerTextTop: float.NaN, footerTextSource: ResultBoundSource.None);
        check(!float.IsFinite(none.FooterTextTop),
            "the fail-open page walls carry no slot bound, so the forced path has nothing to fit against (the pressable control is not a footer)");
        check(ResultFailureWallPolicy.WallMeasured(none, 1200f, 1200f, float.NaN),
            "the shared wall policy does count a pressable control as a wall - that is the host rule, and it is exactly why the client branch asks its slot alone instead of that policy");
        // 有槽位时兜底带落在字形上，且不是扩展带。
        ResultPageWalls slot = ResultPageLowerBoundPolicy.Resolve(true, Height, float.NaN, float.NaN, DrawnHintTop,
            ResultBoundSource.None);
        check(ResultFallbackBandPolicy.TryResolve(Height, TitleBottom, ResultBoundSource.TitleAnchor, slot, float.NaN,
                float.NaN, float.NaN, BoardFloor, out ResultFallbackPlan band)
              && near(band.Bottom, DrawnHintTop - Clearance, 0.02f) && !band.Extended,
            $"with the slot measured the fail-open band ends at {band.Bottom:0.##} ({DrawnHintTop:0} - {Clearance:0.##}) and is not an extension past the wall");
        // 标题量不到时也不直通页底：band 仍被槽位收住。
        check(ResultFallbackBandPolicy.TryResolve(Height, float.NaN, ResultBoundSource.ScreenSafeArea, slot, float.NaN,
                float.NaN, float.NaN, BoardFloor, out ResultFallbackPlan titleless)
              && titleless.Bottom <= DrawnHintTop - Clearance + 0.02f && !titleless.Extended,
            $"a forced frame whose title is unmeasurable still stops at the slot ({titleless.Top:0}..{titleless.Bottom:0}), never at the window's foot");
        // 板的最小高度装不下时，兜底带会扩展过墙 —— 生产必须拒收这种带。
        check(ResultFallbackBandPolicy.TryResolve(Height, TitleBottom, ResultBoundSource.TitleAnchor, slot, float.NaN,
                float.NaN, float.NaN, 900f, out ResultFallbackPlan oversized)
              && oversized.Extended,
            "a board floor that cannot fit above the wall produces the extension the production path must refuse (`no-slot-band`)");
    }

    // ---- F: 站立板当帧收紧（不变） ----------------------------------------------------------------------
    private static void AStandingBoardIsTightenedOnTheFrameTheFooterRises(Action<bool, string> check)
    {
        var held = new ResultsSafeArea(TitleBottom + MessageGap * (Height / 1080f), RecordedBandBottom);
        check(ResultClientStandingBand.TryTighten(held, DrawnHintTop - Clearance, RecordedPlateHeight, 2f,
                out ResultsSafeArea tightened, out bool standsWhole)
              && near(tightened.Bottom, DrawnHintTop - Clearance, 0.02f) && !standsWhole,
            $"the recorded 548px plate cannot keep its height above the tightened foot ({tightened.Bottom:0.##}), so that frame asks for a re-fit instead of drawing over the slot");
        check(ResultClientStandingBand.TryTighten(held, DrawnHintTop - Clearance, 360f, 2f, out ResultsSafeArea moved, out bool standsShort)
              && standsShort && near(moved.Bottom, DrawnHintTop - Clearance, 0.02f),
            "a plate that does fit is only moved up: the same repaint keeps the board on the page");
        var tightHeld = new ResultsSafeArea(held.Top, DrawnHintTop - Clearance);
        check(ResultClientStandingBand.TryTighten(tightHeld, RecordedBandBottom, 360f, 2f, out ResultsSafeArea unchanged, out _)
              && near(unchanged.Bottom, DrawnHintTop - Clearance, 0.02f),
            $"a later frame that reads a lower state ({RecordedBandBottom:0}) cannot widen the standing band again");
    }

    // ---- G: 生产接线（静态检查，不是运行时证明） ---------------------------------------------------------
    private static void TheProductionWiringIsWhatThisFileDrives(Action<bool, string> check)
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "ResultsDisplay.cs"))) root = root == null ? null : Directory.GetParent(root)?.FullName;
        check(root != null, "the 1.19.61 regression locates the production sources");
        if (root == null) return;
        string display = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        string geometry = File.ReadAllText(Path.Combine(root, "GuestPageGeometry.cs"));

        check(geometry.Contains("bool relaxed = !tightened && sameSource && Trust(source) >= Trust(_wallSource);")
              && geometry.Contains("_pendingFrame[slot] != int.MinValue")
              && geometry.Contains("ClearPending();"),
            "the wall gate keeps a per-source sample, relaxes only on same-source agreement and clears every streak on a blind frame (static check)");
        check(geometry.Contains("if (!evidence.SlotMeasured) return ResultClientStripState.WallNotSettled;"),
            "the strict entry point refuses a first display without slot authority (static check)");
        check(geometry.Contains("public static class ResultClientSlotPolicy")
              && geometry.Contains("public static float Authority(float hostSlotTop, float hintGlyphTop, out ResultClientWallSource source)"),
            "the slot authority is a production policy, i.e. the same code these assertions compile against (static check)");

        check(display.Contains("ResultClientSlotEvidence slot = ClientSlot(page);")
              && display.Contains("float clientSlotTop = slot.Top;")
              && display.Contains("MessageGap, MinimumGuestStripHeight, clientSlotTop, pressableTop,"),
            "the client branch takes its bound from the production slot reading (static check)");
        check(display.Contains("hostSlot = HostSlotTop(fail, slotText, titleBottom, glyphExpected, float.IsFinite(glyph));")
              && display.Contains("glyph = HintGlyphTop(slotText, titleBottom);"),
            "and the slot reading is exactly the native return slot plus the waiting line's own rendered glyphs (static check)");
        check(display.Contains("if (fail == null || fail.m_btnGoToLobby == null) return float.NaN;")
              && display.Contains("if (!TryScreenRect(fail.m_btnGoToLobby, out LayoutRect rect)) return float.NaN;"),
            "the host's own return button is what a client measures too - not a container and not another text (static check)");
        int slotAt = display.IndexOf("private static float HostSlotTop(", StringComparison.Ordinal);
        int glyphAt = display.IndexOf("private static float HintGlyphTop(", StringComparison.Ordinal);
        string slotBody = slotAt >= 0 && glyphAt > slotAt ? display.Substring(slotAt, glyphAt - slotAt) : string.Empty;
        check(slotBody.Length > 0 && !slotBody.Contains("ButtonActive"),
            "and the slot reader itself never asks whether the button is clickable: on a client it is permanently unclickable, which is not the same as absent (static check)");
        check(slotBody.Contains("!ResultClientSlotPolicy.HostSlotAuthorised(entity, glyphExpected, glyphMeasured, sameEntity)")
              && slotBody.Contains("bool entity = EntityBoundsMeasured(fail.m_btnGoToLobby);")
              && slotBody.Contains("bool sameEntity = SameSlotEntity(fail.m_btnGoToLobby, slotText);"),
            "and the button's rectangle is only read once the policy has authorised it: real entity geometry, measured glyphs, or (only when nothing is being drawn) the prefab's same-node proof (static check)");
        check(display.Contains("bool glyphExpected = SlotTextExpected(slotText);")
              && display.Contains("hostSlot = HostSlotTop(fail, slotText, titleBottom, glyphExpected, float.IsFinite(glyph));"),
            "the slot measurement hands the policy both facts it needs: whether this page's waiting line should be drawing, and whether its glyphs were really measured (static check)");
        check(display.Contains("TMP_TextInfo info = text.textInfo;")
              && display.Contains("Vector3[] vertices = info.meshInfo[material].vertices;")
              && display.Contains("int index = character.vertexIndex + corner;"),
            "the glyph reading is the text's rendered vertex mesh, not a layout proxy (static check)");
        int measureAt = display.IndexOf("private static ResultClientSlotEvidence MeasureClientSlot(", StringComparison.Ordinal);
        int evidenceAt = display.IndexOf("private readonly struct ResultClientSlotEvidence", StringComparison.Ordinal);
        string measureBody = measureAt >= 0 && evidenceAt > measureAt ? display.Substring(measureAt, evidenceAt - measureAt) : string.Empty;
        check(measureBody.Length > 0 && !measureBody.Contains("GetComponentsInChildren"),
            "the slot reading walks no subtree: only the two named native objects are measured, so unrelated or hidden text cannot become this page's bound (static check)");
        check(display.Contains("if (frame == _slotFrame && pointer == _slotPage) return _slot;"),
            "the slot is measured once per frame and page, so one repaint cannot re-measure it four times (static check)");
        check(display.Contains("why = ResultFailureWallPolicy.Unobserved + \": no-slot-evidence;"),
            "the fail-open/F8 path refuses a client failure page with no slot evidence instead of inventing a band (static check)");
        check(display.Contains("if (walls.GuestFailure && band.Extended)"),
            "and a fail-open band that would cross the wall to reach the window's foot is refused (static check)");
        check(display.Contains("buttonMeasured ? buttonTop : float.NaN, waitingTextTop, controls.FooterTop, out ResultsBand pageBand)"),
            "the host failure page still resolves its band from its own button and waiting text, unchanged (static check)");
    }

    private static bool near(float a, float b, float tolerance) => Math.Abs(a - b) <= tolerance;
}
