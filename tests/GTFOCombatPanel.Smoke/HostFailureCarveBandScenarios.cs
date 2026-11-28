using System;
using System.IO;
using HawChow.GTFOCombatPanel;

internal static class HostFailureCarveBandScenarios
{
    internal static void RunInto(Action<bool, string> check)
    {
        const float width = 2560f, height = 1440f, titleBottom = 1001f, buttonTop = 1141f;
        var walls = ResultPageLowerBoundPolicy.Resolve(false, height, buttonTop, float.NaN, 1143f, ResultBoundSource.FooterText);
        // Reproduce the old redraw from the recorded host failure (1.19.64, LogOutput 6739–6743).
        check(ResultFallbackBandPolicy.TryResolve(height, titleBottom, ResultBoundSource.TitleAnchor,
            walls, float.NaN, 1189f, 1432f, 200f, out ResultFallbackPlan oldBand)
            && oldBand.BelowControls && oldBand.Top == 1189f, "the generic fallback reproduces the old carved strip");
        var recordedPlate = new LayoutRect(571f, 1189f, 1419f, 221f);
        var earlierFallback = new LayoutRect(571f, 1026f, 1419f, 269f);
        check(!ResultHostFailureBand.Allows(recordedPlate, buttonTop, height), "the recorded plate below the measured action top cannot draw");
        check(!ResultHostFailureBand.Allows(earlierFallback, buttonTop, height), "the earlier faint fallback cannot draw through the action either");
        check(!ResultHostFailureBand.TryResolve(height, titleBottom, ResultBoundSource.TitleAnchor, walls,
            float.NaN, 1143f, out _), "a text/scan reading cannot stand in for an unmeasured native button, even on forced fallback");
        check(!ResultHostFailureBand.TryResolve(height, float.NaN, ResultBoundSource.TitleAnchor, walls,
            buttonTop, float.NaN, out _), "no frame budget invents a missing title edge");
        bool resolved = ResultHostFailureBand.TryResolve(height, titleBottom, ResultBoundSource.TitleAnchor,
            walls, buttonTop, float.NaN, out ResultFallbackPlan safe);
        check(resolved && safe.PageBand && !safe.Extended && safe.Opacity == 1f
            && Math.Abs(safe.Bottom - (buttonTop - ResultsBounds.Clearance(height))) < 0.01f,
            "host failure fits only above the measured button, even when the strip is below the nominal board floor");
        check(!ResultFallbackLayout.TryPlaceInBand(width, height, safe.Top, safe.Bottom, 1419f,
            221f, 0f, out _), "short room cannot fit the recorded 53px header plus 168px plate");
        bool placed = ResultFallbackLayout.TryPlaceInBand(width, height, safe.Top, safe.Bottom, 1419f,
            53f + 2f * 18f, 0f, out LayoutRect compact);
        check(placed && ResultHostFailureBand.Allows(compact, buttonTop, height), "a short but readable scrolling plate can draw safely");
        check(placed && !ResultHostFailureBand.Allows(compact, compact.Bottom + 3f, height),
            "a late button that tightens the wall vetoes the held plate");
        check(placed && !ResultHostFailureBand.Allows(compact, float.NaN, height), "lost native measurements do not keep the old board drawing");
        check(ResultHostFailureBand.TryResolve(height, titleBottom, ResultBoundSource.TitleAnchor,
            walls, buttonTop, 1100f, out ResultFallbackPlan earlierControl) && earlierControl.Bottom < safe.Bottom,
            "a higher measured control still tightens the fallback strip");

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "ResultsDisplay.cs"))) root = root.Parent;
        check(root != null, "the smoke suite can inspect the display call sites");
        if (root == null) return;
        string display = File.ReadAllText(Path.Combine(root.FullName, "ResultsDisplay.cs"));
        int resolve = display.IndexOf("ResultHostFailureBand.TryResolve(", StringComparison.Ordinal);
        int budget = display.IndexOf("ResultFallbackPolicy.MayAnswer(_strictMisses, Forced, pageReadable)", StringComparison.Ordinal);
        int verify = display.IndexOf("if (!_holdValid || !ResultHostFailureBand.Allows(_holdBoard, actionTop, Screen.height))", StringComparison.Ordinal);
        int fallbackRefresh = display.IndexOf("RefreshFallbackSafety();", StringComparison.Ordinal);
        int draw = display.IndexOf("HudTable.Draw(_grid, _origin.X, _origin.Y, _content, opacity", StringComparison.Ordinal);
        check(budget >= 0 && resolve > budget && display.Contains("!succeeded && !guestFailure;"),
            "normal and forced host-failure fallback must choose the native strip");
        check(fallbackRefresh >= 0 && verify > fallbackRefresh && draw > verify,
            "the actual clipped plate is checked after fallback reflow and before repaint");
        int refresh = display.IndexOf("private static void RefreshFallbackSafety()", StringComparison.Ordinal);
        int invalidate = display.IndexOf("if (hostFailure) _holdValid = false;", StringComparison.Ordinal);
        int remeasure = display.IndexOf("if (!FallbackPlacement(_page, _succeeded", StringComparison.Ordinal);
        int validate = display.IndexOf("_holdValid = !hostFailure || ResultChromeLayout.PlateInsideBand(_holdBoard, strip.Top, strip.Bottom", StringComparison.Ordinal);
        check(refresh >= 0 && invalidate > refresh && remeasure > invalidate && validate > remeasure,
            "an unavailable current strip cannot keep painting a previously valid host plate");
    }
}
