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

    public static bool TryResolve(float anchorBottom, float screenWidth, float screenHeight,
        float gap, float minimumHeight, out ResultsSafeArea area)
    {
        area = default;
        if (!float.IsFinite(anchorBottom) || !float.IsFinite(screenWidth) || !float.IsFinite(screenHeight)) return false;
        if (!float.IsFinite(gap) || !float.IsFinite(minimumHeight)) return false;
        if (screenWidth <= 8f || screenHeight <= 8f) return false;
        if (gap < 0f || !(minimumHeight > 0f)) return false;
        float scale = screenHeight / ReferenceHeight;
        float bottom = screenHeight - ReferenceBottomInset * scale;
        if (bottom > screenHeight) bottom = screenHeight;
        if (bottom < 0f) bottom = 0f;
        float top = anchorBottom + gap * scale;
        if (top < 0f) top = 0f;
        if (top > screenHeight) top = screenHeight;
        if (!(bottom > top)) return false;
        if (bottom - top < minimumHeight) return false;
        area = new ResultsSafeArea(top, bottom);
        return true;
    }
}
