using System;

namespace HawChow.GTFOCombatPanel;

// ---------------------------------------------------------------------------------------------------
// FixedLayout.cs - 玩家自己指定字号与位置的结算板（自适渲染之外的兜底，默认关闭）
// ---------------------------------------------------------------------------------------------------
//
// 自适应结算板量的是页面：上沿来自任务失败标题 / 完成区，下沿来自玩家要按的按钮或客机上那行提示文字，字号
// 由这条条带的高度反算。这条链在真机上被证明过是对的，但它永远是「页面说了算」：另一个模组换了版式、
// 某个分辨率下条带特别矮、或者玩家长时间读一块自己说不清为什么那么大的表，都不是配置能手改的。
//
// 所以这里给玩家第二把尺子，并把主动权交回去：
//
//   [Results] FixedLayout       = false  默认关闭；关着的时候下面三个键一个字节都不读，自适应行为照旧
//              FixedFontPixels  = 18     字号，单位是**实际屏幕像素**
//              FixedAnchorX     = 0.5    表格**底边中点**的 X：窗口宽度的比例，0 = 左边缘，1 = 右边缘
//              FixedAnchorY     = 0.80   表格**底边中点**的 Y：窗口高度的比例，0 = 顶端，1 = 底端
//
// 左上角是 (0,0)，X 向右增大，Y 向下增大；两个锚点分别是窗口宽度、窗口高度的比例。
//
//   rect.left = FixedAnchorX * 窗口宽度 - 表格宽度 / 2
//   rect.top  = FixedAnchorY * 窗口高度 - 表格高度
//
// 也就是说**底边中点**落在锚点上，表格向上生长，不管是内容变宽、玩家变多还是换了分辨率：底边中点不动。
// 绘制时字号就是这个像素值——不乘 Density、不乘滚轮缩放、不被条带缩小、不套自动模式的居中与偏移。
//
// 拒绝比移动好：下面四种是**不画**（保留原版页面，一行有界诊断说清是哪一项）：
//
//   * 非有限 / 越界的输入（字号不在 `[MinimumFontPixels, MaximumFontPixels]` = 1..240 整数像素、锚点不在
//     [0,1]、窗口尺寸不可信）→ `fixed-layout-input-invalid`，并说清是哪一项；
//   * 那个字号在那一页上量不出表格 → `fixed-layout-grid-unmeasurable`；
//   * 表格量回来的字号不是玩家给的那个像素（`HudTable.BuildGrid` 在某些盒子里会整体缩小）→
//     `fixed-layout-font-not-honoured`：宁可拒绝，也不悄悄画小一号；
//   * 得出的矩形跑出屏幕 → `fixed-layout-offscreen`（**不**夹回屏内、**不**缩字：那会让玩家指定的位置和
//     字号悄悄变样，正是这个模式要避免的事）。
//
// 玩家主动打开这个模式，就不该被「页面上有个按钮」整块吞掉。真实 Survival 日志里，固定板 1026..1296 压住
// pressableTop=1145 的返回大厅按钮，于是 20 次 `fixed-layout-overlaps-control` 拒绘、字号改到 5 又被旧的
// 6px 下限拒成 `fixed-layout-input-invalid`，固定板一次都没画出来。所以**真实相交不再等于不出板**：板照玩家
// 的字号与锚点画，只是按既有的 `ResultFallbackLayout.OverlapOpacity`（0.55）淡画降低遮挡，并按页/配置各记
// 一行（固定矩形、真实字号、锚点、与哪个具名/类型控件的真实矩形相交、opacity）。相交判定是二维矩形相交
// （`ResultInteractionStripPolicy.OverlapsBoard` + `ResultBoardHold.ButtonClearance` 余量）：**X 错开就不算
// 冲突**，重叠消失就恢复 1.0。
//
// 扫描不完整、或有 active 控件却量不到矩形时证据不足：原版块**不隐藏**，固定板仍以 0.55 淡画，日志明确写成
// 「证据不足、不声称无冲突」，免得未知状态把固定板永久挡在页面外。原版块只在这块 overlay 真的替代它（扫描
// 完整、无重叠）时才让位。这块板只画不接管指针：滚轮照旧归它（固定模式不改字号），鼠标点击没有任何新增
// 消费。下一帧、换页、热重载、分辨率变化或 F8 后照旧重新判定。
//
// 这个文件里没有 Unity、没有 BepInEx：算式、校验与日志措辞都可以在无头套件里直接驱动。
public readonly struct ResultFixedLayoutPlan
{
    // 表格真正要画到的矩形，窗口像素，y 从上往下数。
    public readonly LayoutRect Rect;
    // 表格被构建与绘制的字号（实际屏幕像素）。配置给出的小数四舍五入到整数像素，因为 IMGUI 的字号是整数。
    public readonly int FontPixels;
    // 玩家指定的两个比例。
    public readonly float AnchorX, AnchorY;

    public ResultFixedLayoutPlan(LayoutRect rect, int fontPixels, float anchorX, float anchorY)
    {
        Rect = rect; FontPixels = fontPixels; AnchorX = anchorX; AnchorY = anchorY;
    }

    // 底边中点，正好是玩家指定的那一点（日志与断言读它）。
    public float AnchorCenterX { get { return Rect.X + Rect.Width * 0.5f; } }
    public float AnchorBottom { get { return Rect.Y + Rect.Height; } }
    public bool Placed { get { return Rect.Width > 0f && Rect.Height > 0f; } }
    public static ResultFixedLayoutPlan None { get { return default; } }
}

public static class ResultFixedLayout
{
    // 字号的范围，单位是实际屏幕像素。IMGUI 的字号是整数像素，所以下界就是 1：玩家写 5 就是在要 5px，
    // 这个模式把选择权交回玩家，不再拿一个「读不出来」的下限替他拒绝（Survival 的 cfg 里 FixedFontPixels=5
    // 就是明确尝试值，旧的 6px 下限把它整块拒绘了）。上界 240px 在 1080p 上一行都放不下。
    // 真正无法画的情况仍然明确拒绝：非有限、非正数、越界、量不出表格、跑出屏幕；越界不「夹一下就好」——
    // 那会让玩家以为自己设的值生效了。
    public const float MinimumFontPixels = 1f;
    public const float MaximumFontPixels = 240f;
    // 默认值：1080p 下 18px 是表里正文的可读尺寸；底边中点在窗口高度的 80%，也就是屏幕下方五分之一处。
    public const float DefaultFontPixels = 18f;
    public const float DefaultAnchorX = 0.5f;
    public const float DefaultAnchorY = 0.80f;

    // 不画板的四种拒绝，加上「画了但要让位/淡画」的三种状态，日志与回归测试都按这几个名字说话。
    public const string InputInvalid = "fixed-layout-input-invalid";
    public const string GridUnmeasurable = "fixed-layout-grid-unmeasurable";
    public const string FontNotHonoured = "fixed-layout-font-not-honoured";
    public const string OffScreen = "fixed-layout-offscreen";
    public const string OverlapsControl = "fixed-layout-overlaps-control";
    // 画了：这块板干净地替代了原版块（opacity 1.0）。
    public const string Placed = "fixed-layout-placed";
    // 画了，但扫描证据不足（不完整 / 有 active 控件量不到矩形）：淡画、原版块保留、不声称无冲突。
    public const string EvidenceThin = "fixed-layout-evidence-thin";

    // 配置里的字号换算成真正绘制的整数像素。只在输入通过校验后调用。
    public static int RoundFontPixels(float configured)
    {
        if (!float.IsFinite(configured)) return (int)DefaultFontPixels;
        double rounded = Math.Round(configured, MidpointRounding.AwayFromZero);
        if (rounded < MinimumFontPixels) rounded = MinimumFontPixels;
        if (rounded > MaximumFontPixels) rounded = MaximumFontPixels;
        return (int)rounded;
    }

    // 输入校验：窗口、字号、两个锚点。`why` 说清是哪一项，日志直接把它写出去。
    public static bool Valid(float screenWidth, float screenHeight, float fontPixels, float anchorX, float anchorY,
        out string why)
    {
        if (!float.IsFinite(screenWidth) || !float.IsFinite(screenHeight) || screenWidth <= 8f || screenHeight <= 8f)
        {
            why = InputInvalid + ": window=" + Width(screenWidth) + "x" + Width(screenHeight)
                + " (the window size is not a number or is too small to place a table in)";
            return false;
        }
        if (!float.IsFinite(fontPixels) || fontPixels < MinimumFontPixels || fontPixels > MaximumFontPixels)
        {
            why = InputInvalid + ": fontPixels=" + Width(fontPixels) + " (must be a number between "
                + MinimumFontPixels.ToString("0") + " and " + MaximumFontPixels.ToString("0")
                + " actual screen pixels; nothing is clamped and nothing is drawn)";
            return false;
        }
        if (!float.IsFinite(anchorX) || anchorX < 0f || anchorX > 1f)
        {
            why = InputInvalid + ": anchorX=" + Width(anchorX)
                + " (must be a fraction of the window width between 0 and 1; 0.5 is the horizontal centre)";
            return false;
        }
        if (!float.IsFinite(anchorY) || anchorY < 0f || anchorY > 1f)
        {
            why = InputInvalid + ": anchorY=" + Width(anchorY)
                + " (must be a fraction of the window height between 0 and 1; 0.80 is four fifths down the window)";
            return false;
        }
        why = string.Empty;
        return true;
    }

    // 固定模式下表格的矩形：底边中点落在 (anchorX * 宽, anchorY * 高) 上，字号就是给定的那个像素值。
    //
    // `gridWidth`/`gridHeight` 是表格在**那个字号**下自己的尺寸（`HudTable.BuildGrid` 用无穷大的盒子量出来的），
    // 所以这里既不会缩字，也不会重新居中：算式只做减法。
    public static bool TryResolve(float screenWidth, float screenHeight, float anchorX, float anchorY,
        float fontPixels, float gridWidth, float gridHeight, out ResultFixedLayoutPlan plan, out string why)
    {
        plan = ResultFixedLayoutPlan.None;
        if (!Valid(screenWidth, screenHeight, fontPixels, anchorX, anchorY, out why)) return false;
        if (!float.IsFinite(gridWidth) || !float.IsFinite(gridHeight) || !(gridWidth > 0f) || !(gridHeight > 0f))
        {
            why = GridUnmeasurable + ": grid=" + Width(gridWidth) + "x" + Width(gridHeight)
                + " at the configured " + RoundFontPixels(fontPixels) + "px glyph (the table cannot be measured at that size)";
            return false;
        }
        int pixels = RoundFontPixels(fontPixels);
        float left = anchorX * screenWidth - gridWidth * 0.5f;
        float top = anchorY * screenHeight - gridHeight;
        // 跑出屏幕就是跑出屏幕：不夹、不缩、不挪，留给调用方保留原版并写一行诊断。
        if (left < -0.5f || top < -0.5f || left + gridWidth > screenWidth + 0.5f || top + gridHeight > screenHeight + 0.5f)
        {
            why = OffScreen + ": rect=(" + left.ToString("0.#") + "," + top.ToString("0.#") + ","
                + gridWidth.ToString("0") + "x" + gridHeight.ToString("0") + ") of a "
                + screenWidth.ToString("0") + "x" + screenHeight.ToString("0") + " window at anchor=("
                + anchorX.ToString("0.###") + "," + anchorY.ToString("0.###") + ") glyph=" + pixels
                + "px (nothing is clamped, rescaled or re-centred: lower the glyph, move the anchor, or turn FixedLayout off)";
            return false;
        }
        plan = new ResultFixedLayoutPlan(new LayoutRect(left, top, gridWidth, gridHeight), pixels, anchorX, anchorY);
        why = string.Empty;
        return true;
    }

    // 一页一次的那行日志：矩形、字号、锚点、窗口，和它在屏幕上真正占的那一段。
    public static string Describe(ResultFixedLayoutPlan plan, float screenWidth, float screenHeight, string role,
        string page)
        => "fixed layout board: rect=(" + plan.Rect.X.ToString("0.#") + "," + plan.Rect.Y.ToString("0.#")
            + "," + plan.Rect.Width.ToString("0") + "x" + plan.Rect.Height.ToString("0") + ") of a "
            + screenWidth.ToString("0") + "x" + screenHeight.ToString("0") + " window, glyph=" + plan.FontPixels
            + "px (the configured size itself: no density scale, no wheel scale, no strip shrink), anchor=("
            + plan.AnchorX.ToString("0.###") + "," + plan.AnchorY.ToString("0.###")
            + ") => the bottom-edge midpoint sits at (" + plan.AnchorCenterX.ToString("0.#") + ","
            + plan.AnchorBottom.ToString("0.#") + "), role=" + role + ", page=" + page
            + "; measured from the window and the player's own numbers only, never from the page's adaptive band.";

    // 一次配置尝试的签名：日志限频拿它当键，所以「同一次尝试的同一句话」只写一遍，而玩家改过的字号、锚点、
    // 窗口、或者表格换了尺寸（人数变化、分辨率变化）都是新的一次尝试，必须重新说一遍——旧的固定模式限频
    // 只按 `fixed-layout-refused` 一个键记，于是第一次失败就把后面改过的参数一起噤掉了。
    // 它刻意不含相交控件的矩形像素（那个值会在一两帧内抖动），只含板自己的矩形。
    public static string Attempt(float fontPixels, float anchorX, float anchorY, float screenWidth,
        float screenHeight, float rectWidth, float rectHeight)
        => "window=" + Width(screenWidth) + "x" + Width(screenHeight) + ",glyph=" + Width(fontPixels)
            + ",anchor=(" + Width(anchorX) + "," + Width(anchorY) + "),rect="
            + (float.IsFinite(rectWidth) && rectWidth > 0f && float.IsFinite(rectHeight) && rectHeight > 0f
                ? rectWidth.ToString("0") + "x" + rectHeight.ToString("0")
                : "none");

    // A configured number in the few words a log can afford, without pretending a non-number is zero.
    private static string Width(float value) => float.IsFinite(value) ? value.ToString("0.###") : "not-a-number";
}

// ---------------------------------------------------------------------------------------------------
// 固定板与页面真实控件的相交证据，以及「画 / 淡画 / 原版块让不让位」这一个决定
// ---------------------------------------------------------------------------------------------------

// 一个真的和固定板（含 `ResultBoardHold.ButtonClearance` 余量）相交的控件：哪个对象、什么组件类型、它自己的
// 真实屏幕矩形，以及它是不是玩家真的能按的。日志按这几个字段说清「压到了谁」，回归测试也读它。
public readonly struct ResultFixedBlocker
{
    public readonly LayoutRect Rect;
    public readonly string Name, Type;
    public readonly bool Pressable;

    public ResultFixedBlocker(LayoutRect rect, string name, string type, bool pressable)
    {
        Rect = rect; Name = name ?? string.Empty; Type = type ?? string.Empty; Pressable = pressable;
    }

    public bool Any => Rect.Width > 0f && Rect.Height > 0f && float.IsFinite(Rect.X) && float.IsFinite(Rect.Y);
    public static ResultFixedBlocker None => default;

    // 日志里的那一段：类型、对象名、它自己的真实矩形。
    public string Describe()
        => (Pressable ? "pressable " : "page text ") + (Type.Length > 0 ? Type : "control")
            + " '" + (Name.Length > 0 ? Name : "(unnamed)") + "' rect=(" + Rect.X.ToString("0") + ","
            + Rect.Y.ToString("0") + "," + Rect.Width.ToString("0") + "x" + Rect.Height.ToString("0") + ")";

    // 限频键里用的短名：只留身份，不留会在一两帧内抖动的像素。
    public string Signature
        => (Type.Length > 0 ? Type : "control") + ":" + (Name.Length > 0 ? Name : "(unnamed)")
            + (Pressable ? ":pressable" : ":text");
}

// 固定模式一帧的决定：画不画、淡不淡、原版块让不让位、为什么。
public readonly struct ResultFixedDrawPlan
{
    // 板照玩家给的字号与锚点画出去（不能画的情况已经在更早的算式里拒绝掉了，所以走到这里就是画）。
    public readonly bool Draws;
    // 有一个真实的控件矩形和板相交（二维，含 clearance）。
    public readonly bool Overlap;
    // 这一帧的扫描证明它看完了整页，而且没有量不到矩形的 active 控件。
    public readonly bool Proven;
    public readonly float Opacity;
    public readonly string Reason;
    public readonly ResultFixedBlocker Blocker;
    // 这一帧扫描自己的读数（完整/父级覆盖/量不到个数/原因/pressableTop/footerTop/相交个数），日志原样带出去，
    // 「证据不足」才不是一句空话。
    public readonly string Evidence;

    public ResultFixedDrawPlan(bool draws, bool overlap, bool proven, float opacity, string reason,
        ResultFixedBlocker blocker, string evidence = null)
    {
        Draws = draws; Overlap = overlap; Proven = proven; Opacity = opacity;
        Reason = reason ?? string.Empty; Blocker = blocker; Evidence = evidence ?? string.Empty;
    }

    // 原版块只在这块 overlay 真的替代它的时候让位：**证明看完 + 没有真实相交**。三者缺一都是"板在场、原版块也在"：
    // 相交时板是淡画在控件上的（原版块留着，页面才既看得见也按得动），证据不足时板也是淡画的（原版块留着，页面不会
    // 因为一次没看全的扫描变成「半张空页 + 一块看不清的表」）。`Overlap` 必须在这里再挡一次：扫描可以既完整又相交
    // （用户当前的 5px / 0.5 / 0.9 就是这一种），那一帧不能把原版块挪走。
    public bool HidesVanilla => Draws && Proven && !Overlap;

    public static ResultFixedDrawPlan None => default;

    // 限频键：同一次配置尝试、同一个相交对象、同一个证明状态只写一行；控件矩形抖动不产生新行。
    public string Signature
        => "reason=" + Reason + ",proven=" + (Proven ? "true" : "false")
            + ",opacity=" + Opacity.ToString("0.##") + ",blocker=" + (Blocker.Any ? Blocker.Signature : "none");

    // 一行有界诊断：固定矩形、真实字号、锚点、窗口、与谁的矩形相交、opacity 和这一帧到底证明了什么。
    public string Describe(ResultFixedLayoutPlan plan, float screenWidth, float screenHeight, string role, string page)
        => ResultFixedLayout.Describe(plan, screenWidth, screenHeight, role, page)
            + (Overlap
                ? " The board really intersects " + Blocker.Describe()
                  + " (2D rectangle intersection with a " + ResultBoardHold.ButtonClearance.ToString("0")
                  + "px clearance), so the player's own glyph and anchor are drawn anyway at opacity "
                  + Opacity.ToString("0.##") + " instead of being refused: the control underneath still owns its"
                  + " pointer, this board consumes no mouse click, and the wheel stays with it."
                : " No measured control intersects it.")
            + " opacity=" + Opacity.ToString("0.##") + ",reason=" + Reason
            + (Evidence.Length > 0 ? ",readback=(" + Evidence + ")" : string.Empty)
            // 证据不足优先于"相交且证明完整"：两者同时成立时（`Overlap=true && Proven=false`）这一行不能说 `scan=complete`，
            // 否则与同行的 `readback=(complete=false,…)` 自相矛盾。判定只改措辞顺序，`Draws`/`Overlap`/`Opacity`/`HidesVanilla`
            // 一个字都没动（没有新门禁、没有迟滞）。
            + (!Proven
                ? ",scan=incomplete: the evidence is not enough to prove the page is clear, so the vanilla blocks are"
                  + " kept and the board is drawn faint - this line does not claim there is no conflict."
                : Overlap
                    ? ",scan=complete but the board really covers a control, so the vanilla blocks are kept under the"
                      + " faint board: the page stays readable and the control stays pressable."
                    : ",scan=complete: the vanilla blocks this board replaces are put away on the frames it is drawn.");
}

// 固定模式的可见性决定。
//
// 玩家打开这个模式，主动权在他手里：真实相交、扫描不完整、有 active 控件量不到矩形，都不再等于「不出板」——
// 那是把整块板从页面上抹掉，而玩家改了字号、挪了锚点、按了 F8 也只会得到另一行拒绝。现在这三种情况都画，只是
// 淡到 0.55（既有的 `ResultFallbackLayout.OverlapOpacity`）并把证据不足写清楚；原版块只在「扫描完整 **且** 没有真实
// 相交」时才让位——相交那一帧板是淡画在控件上的，原版块留着。
public static class ResultFixedOverlayPolicy
{
    // 和兜底板同一档：遮着页面时降到这个不透明度，重叠消失就回到 1.0。
    public const float OverlapOpacity = ResultFallbackLayout.OverlapOpacity;

    // `proven` 是这一帧扫描的证明状态（完整 + 父级覆盖完整 + 没有量不到矩形的 active 控件），`overlap` 是二维
    // 矩形相交的答案（含 clearance）。两个答案彼此独立，而且**都降不透明度**：相交要降低遮挡，证据不足是保守淡画
    // （原版块同时保留）。只有在「证明看完 + 没有相交」时才是 1.0。`evidence` 是这一帧扫描自己的读数，`why` 是给
    // 日志的那一句（理由名 + 读数）。
    public static ResultFixedDrawPlan Decide(bool proven, bool overlap, ResultFixedBlocker blocker, string evidence,
        out string why)
    {
        if (blocker.Any) overlap = true;
        string reason = overlap
            ? ResultFixedLayout.OverlapsControl
            : proven ? ResultFixedLayout.Placed : ResultFixedLayout.EvidenceThin;
        bool faint = overlap || !proven;
        why = reason + ": " + (evidence ?? string.Empty);
        return new ResultFixedDrawPlan(true, overlap, proven, faint ? OverlapOpacity : 1f, reason,
            overlap ? blocker : ResultFixedBlocker.None, evidence);
    }
}
