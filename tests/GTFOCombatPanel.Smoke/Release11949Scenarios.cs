using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// ---------------------------------------------------------------------------------------------------
// Release11949Scenarios.cs - the 2026-11-23 recording, retested (1.19.49).
//
// The recorded run answered its one defect - an equipment column this build could read one row of - with a table drawn at
// the window's LEFT edge: (24,526,459x60) on a 2560x1440 window whose column was read at x=2336..2546. The table is
// right-aligned with that column on every proven rung, so the conservative answer had moved it about 1,850 px away from the
// place it stands in for, and the same absence was written four times in one round (one line per width: 459, 479, 471,
// 482 px). Four contracts are asserted here, each on production code:
//
//   1. every conservative candidate is right-aligned to the window's own margin, on the column's side, whatever the width,
//      the window, the obstacle or the candidate that finally answers - and nothing is claimed about rows nobody read;
//   2. the whole "no proven place" family is Info, latched on a fixed semantic key, written once per round and re-armed by
//      nothing inside that round (not by the width, the coordinates, the retry count, an F8 press or a later poll), while
//      recovery, retry and a read that really threw keep their own lines and their own level;
//   3. the settlement page's press exception widens the gate only for the line that answers the press: the recorded
//      `artifact summary` line was written once per F8 press (fourteen times), and that is what the strict gate ends;
//   4. one version number everywhere - the plugin constant, the Thunderstore manifest, the README and the changelog - and
//      the loader line and the settlement save read it from that one place.
//
// `HudFallbackSpot` and `ResultReportPolicyStrict` are production types compiled straight into this suite. What a headless
// test cannot do is run Unity or press F8, so the panel's own wiring (`PanelBehaviour`, `ResultsDisplay`) is asserted
// against those files' source text - the same split every anchor suite in this project uses.
internal static partial class Program
{
    private const float R11949ScreenWidth = 2560f;
    private const float R11949ScreenHeight = 1440f;

    private static void RunRelease11949Scenarios()
    {
        TheConservativePlaceIsOnTheRightMargin();
        EveryBlockedCandidateKeepsTheRightMargin();
        TheManualModeIsStillTheConfiguredPosition();
        TheAbsenceFamilyIsOneLinePerRound();
        TheFamilyIsInfoAndRealErrorsStayWarnings();
        ThePressOwesOneAnswerAndNotEveryDiagnostic();
        TheDrawnRectangleNamesItsSource();
        OneVersionNumberEverywhere();
    }

    // ---------- 1. the place itself ----------

    private static void TheConservativePlaceIsOnTheRightMargin()
    {
        var recordedRow = new LayoutRect(2336.08f, 294.33f, 210.59f, 110.16f);
        var measured = new[] { recordedRow };
        // Every width the recorded round reported, on the recorded window: one right edge, a left edge that moves with the
        // content - exactly what the proven rung does.
        foreach (float width in new[] { 459f, 479f, 471f, 482f, 300f, 640f })
        {
            Check(HudFallbackSpot.TryResolve(526f, measured, width, 60f, R11949ScreenWidth, R11949ScreenHeight,
                    out LayoutRect spot, out string why, out float covers)
                  && why == "right-row" && near(spot.Right, R11949ScreenWidth - HudFallbackSpot.Margin)
                  && covers == 0f && HudPlacement.OnWindow(spot, R11949ScreenWidth, R11949ScreenHeight),
                "the " + width.ToString("0") + " px table of the recorded round is right-aligned to the window's margin: "
                    + why + " right=" + spot.Right.ToString("0.##"));
        }
        // The old answer is never taken: the recorded frame's own (24,526) is on the far side of the window, and no width
        // puts the table back there.
        Check(HudFallbackSpot.TryResolve(526f, measured, 459f, 60f, R11949ScreenWidth, R11949ScreenHeight,
                out LayoutRect drawn, out _, out _) && drawn.X > R11949ScreenWidth * 0.75f,
            "and the recorded (24,526) left edge is not a candidate any more: left=" + drawn.X.ToString("0"));

        // Narrow, wide and tall windows: the margin is the same inset on the right, and the table is never clamped into a
        // left edge by accident.
        foreach (float[] page in new[]
        {
            new[] { 600f, 720f }, new[] { 1280f, 720f }, new[] { 1920f, 1080f }, new[] { 3440f, 1440f }
        })
        {
            float screenWidth = page[0], screenHeight = page[1];
            float margin = Math.Min(HudFallbackSpot.Margin,
                Math.Min((screenWidth - 459f) * 0.5f, (screenHeight - 120f) * 0.5f));
            if (margin < 0f) margin = 0f;
            Check(HudFallbackSpot.TryResolve(300f, Array.Empty<LayoutRect>(), 459f, 120f, screenWidth, screenHeight,
                    out LayoutRect spot, out string why, out _)
                  && why == "right-row" && near(spot.Right, screenWidth - margin) && spot.X >= 0f
                  && HudPlacement.OnWindow(spot, screenWidth, screenHeight),
                "a " + screenWidth.ToString("0") + "x" + screenHeight.ToString("0") + " window keeps the right margin "
                    + margin.ToString("0.##") + "px: right=" + spot.Right.ToString("0.##"));
        }

        // No geometry at all, a row off the top of the window, and a null obstacle list: all three still right-aligned, and
        // the player's own row is pulled inside the margin rather than off the window.
        Check(HudFallbackSpot.TryResolve(-40f, Array.Empty<LayoutRect>(), 459f, 120f, R11949ScreenWidth, R11949ScreenHeight,
                out LayoutRect top, out string topWhy, out _)
              && topWhy == "right-row" && near(top.Y, HudFallbackSpot.Margin)
              && near(top.Right, R11949ScreenWidth - HudFallbackSpot.Margin),
            "a configured row above the window is pulled inside the top margin, still right-aligned: y=" + top.Y.ToString("0.##"));
        Check(HudFallbackSpot.TryResolve(526f, null, 459f, 120f, R11949ScreenWidth, R11949ScreenHeight,
                out LayoutRect none, out string noneWhy, out _)
              && noneWhy == "right-row" && near(none.Right, R11949ScreenWidth - HudFallbackSpot.Margin),
            "a poll that measured nothing at all draws the right-margin place, never the configured position");
        // The one refusal stays the one refusal: a table that cannot be inside the window at all.
        Check(!HudFallbackSpot.TryResolve(526f, measured, R11949ScreenWidth + 1f, 120f, R11949ScreenWidth, R11949ScreenHeight,
                out _, out _, out _),
            "a table wider than the window is still refused - a property of the table, not of the HUD");
    }

    // ---------- 2. the blocked candidates ----------

    private static void EveryBlockedCandidateKeepsTheRightMargin()
    {
        float right = HudFallbackSpot.Margin;
        // The right-hand strip a measured row occupies, so every candidate below has to be judged against a real obstacle.
        LayoutRect Strip(float top, float height)
            => new LayoutRect(R11949ScreenWidth - 500f, top, 500f, height);

        // Beside the player's row blocked, nothing below: the place steps under the lowest row read, still right-aligned.
        var belowSet = new[] { Strip(500f, 400f) };
        Check(HudFallbackSpot.TryResolve(526f, belowSet, 459f, 120f, R11949ScreenWidth, R11949ScreenHeight,
                out LayoutRect below, out string belowWhy, out float belowCovers)
              && belowWhy == "below-measured" && near(below.Right, R11949ScreenWidth - right) && belowCovers == 0f
              && below.Y >= 908f,
            "a blocked row on the player's own line steps the place below it on the right margin: " + belowWhy);

        // Beside and below blocked: the place goes above what was read, and keeps the right margin.
        var aboveSet = new[] { Strip(500f, 940f) };
        Check(HudFallbackSpot.TryResolve(526f, aboveSet, 459f, 120f, R11949ScreenWidth, R11949ScreenHeight,
                out LayoutRect above, out string aboveWhy, out float aboveCovers)
              && aboveWhy == "above-measured" && near(above.Right, R11949ScreenWidth - right) && aboveCovers == 0f
              && above.Y + 120f <= 500f,
            "with the whole strip below blocked, the place goes above it, still right-aligned: " + aboveWhy);

        // Beside, below and above all blocked, but one scanned row free: the bounded right-edge scan answers, on the margin.
        var scanSet = new[] { Strip(0f, 400f), Strip(600f, 160f), Strip(800f, 600f) };
        Check(HudFallbackSpot.TryResolve(650f, scanSet, 459f, 120f, R11949ScreenWidth, R11949ScreenHeight,
                out LayoutRect scanned, out string scanWhy, out float scanCovers)
              && scanWhy == "right-column" && near(scanned.Right, R11949ScreenWidth - right) && scanCovers == 0f,
            "a page with one free row left on the right edge is answered from that edge, never from the left one: " + scanWhy);

        // Nothing free anywhere: the least-overlapping right candidate stands and its covered area is reported, which is the
        // one honest thing this class can say about a page it could not read.
        var blocked = new[] { new LayoutRect(0f, 0f, R11949ScreenWidth, R11949ScreenHeight) };
        Check(HudFallbackSpot.TryResolve(526f, blocked, 459f, 120f, R11949ScreenWidth, R11949ScreenHeight,
                out LayoutRect least, out string leastWhy, out float leastCovers)
              && leastWhy == "least-overlap" && leastCovers > 0f
              && near(least.Right, R11949ScreenWidth - right)
              && HudPlacement.Area(least, blocked[0]) == leastCovers,
            "an unreadable page still draws the table on the right margin, with the area it covers beside it: "
                + leastCovers.ToString("0"));

        // The reasons are a closed set, and none of them says "left": the caller prints the reason into the log, so a rename
        // here is a rename there.
        string hud = ProductionText("HudAvoidance.cs");
        foreach (string reason in new[] { "right-row", "below-measured", "above-measured", "right-column", "least-overlap", "none" })
            Check(hud.Contains("reason = \"" + reason + "\""), "HudFallbackSpot answers `" + reason + "`");
        Check(!hud.Contains("\"left-row\"") && !hud.Contains("\"left-column\"")
              && !hud.Contains("at the window's LEFT edge - never the configured position"),
            "and there is no left-edge candidate left in the class at all");
        Check(Tally(hud, "reason = \"") == 6,
            "the reason strings are exactly the six the caller can meet, so the log's own vocabulary is closed");
    }

    // ---------- 3. the manual mode ----------

    private static void TheManualModeIsStillTheConfiguredPosition()
    {
        string panel = ProductionText("PanelBehaviour.cs");
        string resolve = Segment(panel, "private bool TryResolveOrigin(PanelGrid grid", "private bool TryFallbackSpot(");
        Check(resolve.Length > 0
              && resolve.Contains("bool automatic = Plugin.AnchorToFlashlight.Value;")
              && resolve.Contains("bool configuredSafe = !automatic || (recovered && _bandWatch.Has && _bandWatch.Complete"),
            "`AnchorToFlashlight=false` is still the player's own explicit answer: the automatic gate is short-circuited by it");
        Check(Tally(panel, "TryFallbackSpot(grid, y, out LayoutRect spot") == 1
              && resolve.IndexOf("if (!configuredSafe)", StringComparison.Ordinal) >= 0
              && resolve.IndexOf("if (!configuredSafe)", StringComparison.Ordinal)
                  < resolve.IndexOf("TryFallbackSpot(grid, y, out LayoutRect spot", StringComparison.Ordinal),
            "the conservative place has one call site, inside the refusal branch: the manual mode can never reach it");
        Check(!resolve.Contains("HudFallbackSpot.TryResolve"),
            "and `TryResolveOrigin` never asks the right-margin class itself - the single call site is `TryFallbackSpot`");
    }

    // ---------- 4. one line per round ----------

    private static void TheAbsenceFamilyIsOneLinePerRound()
    {
        string panel = ProductionText("PanelBehaviour.cs");
        // 1.19.50: the vocabulary and the bound live in `HudAbsenceLedger` (asserted runnably by `Release11950Scenarios`); this
        // suite only records that the panel really uses that one ledger and that nothing re-arms it inside a round.
        Check(panel.Contains("private readonly HudAbsenceLedger _absence = new HudAbsenceLedger();")
              && ProductionText("HudAbsenceLedger.cs").Contains("if (!HudAbsence.IsKnown(key)) return false;")
              && ProductionText("HudAbsenceLedger.cs").Contains("if (_claimed.Count >= HudAbsence.Capacity) return false;"),
            "the whole family shares one bounded ledger, keyed by meaning and claimed once");
        Check(Tally(panel, "_absence.Clear();") == 1
              && Segment(panel, "private void ForgetBandRound()", "private void Update()").Contains("_absence.Clear();"),
            "the ledger is emptied by the round and by nothing else - not by a recovery, a proven rung or a poll");
        Check(!panel.Contains("_absenceNotes") && !panel.Contains("ClaimAbsenceNote") && !panel.Contains("AbsenceReports")
              && !panel.Contains("_avoidanceNote") && !panel.Contains("_fallbackSpotNote") && !panel.Contains("_fallbackDeferNote")
              && !panel.Contains("FallbackSpotReports"),
            "the three full-note latches, their per-answer budget and the eight-slot counter are gone");

        // Every key is one of the vocabulary's own names. A key that interpolated the width, the window or the attempt number
        // would put the flood straight back, so that is asserted on the call sites themselves.
        int index = 0, calls = 0;
        while ((index = panel.IndexOf("_absence.Claim(", index, StringComparison.Ordinal)) >= 0)
        {
            int close = panel.IndexOf(')', index);
            string argument = close > index ? panel.Substring(index, close - index) : string.Empty;
            calls++;
            Check(!argument.Contains(".ToString(") && !argument.Contains("Screen.width") && !argument.Contains("grid.Width")
                  && !argument.Contains("_bandRows") && !argument.Contains("_anchorPolicy")
                  && argument.Contains("HudAbsence."),
                "the key is a name from the vocabulary, not a measurement: " + argument.Replace("\n", " ").Trim());
            index += 15;
        }
        Check(calls == 5, "five call sites, five vocabulary answers, no sixth way to become a 'new' absence (" + calls + ")");
        foreach (string key in new[]
        {
            "HudAbsence.AnchorUnmeasured", "HudAbsence.AnchorPartial", "HudAbsence.AnchorNoFreePlace",
            "HudAbsence.Spot(reason)", "complete ? HudAbsence.DeferOverlap : HudAbsence.DeferIncomplete"
        })
            Check(panel.Contains(key), "the family's key `" + key + "` is one of the vocabulary's own names");

        // An F8 press is not a new round: the toggle writes the config back through its own save (which the watcher
        // records as this mod's own write), and a round is only ever restarted by a level change, a configuration reload or a
        // resolution change. So a press cannot re-arm a line the round has already written.
        string plugin = ProductionText("Plugin.cs");
        string persist = Segment(plugin, "internal static void PersistToggle(bool visible)", "// The manual size multiplier in force");
        Check(persist.Length > 0 && persist.Contains("ConfigWatcher.RecordOwnSave();") && !persist.Contains("InvalidateLayout"),
            "the F8 toggle's own save is recorded as this mod's write and never invalidates the layout");
        string hotReload = Segment(plugin, "internal static void PollHotReload()", "internal static string DescribeColumns");
        Check(Tally(plugin, "ConfigWatcher.ConsumeReload()") == 1
              && hotReload.Length > 0 && hotReload.Contains("ConfigWatcher.ConsumeReload()"),
            "and only a real config reload reaches the one place a round is restarted from");
        string resets = Segment(panel, "if (inLevel != _inLevel)", "bool visible = inLevel ?");
        Check(resets.Contains("ForgetBandRound();") && !resets.Contains("_absence.Clear();"),
            "a level change goes through the round reset, which is where the ledger is emptied - not through F8");
    }

    // ---------- 5. the level ----------

    private static void TheFamilyIsInfoAndRealErrorsStayWarnings()
    {
        string panel = ProductionText("PanelBehaviour.cs");
        string family = Segment(panel, "private bool TryAnchoredOrigin(", "private void ReportPlacement(");
        Check(family.Length > 0 && !family.Contains("LogWarning") && Tally(family, "LogInfo") == 3,
            "the three 'no proven place' lines - unmeasured column, partial column, no free rung - are all Info");
        Check(!Segment(panel, "private void ReportFallbackSpot(", "private void DeferFallback(").Contains("LogWarning")
              && !Segment(panel, "private void DeferFallback(", "private bool TryAnchoredOrigin(").Contains("LogWarning"),
            "the conservative place and the one poll that still draws nothing are Info as well");
        Check(panel.Contains("if (string.IsNullOrEmpty(sample.Error)) Plugin.Logger.LogInfo(line);")
              && panel.Contains("else Plugin.Logger.LogWarning(line);"),
            "a measurement that has not arrived is Info, while a read that really threw keeps its Warning and its message");
        Check(panel.Contains("Plugin.Logger.LogInfo($\"[DIAG] the flashlight anchor fast retries ended after"),
            "and the give-up line that names the reason and the waiting is Info for the same reason");
        Check(panel.Contains("Plugin.Logger?.LogInfo($\"[DIAG] the anchor could not be measured from the icon row ("),
            "the row-level fallback to the same row's Background Fade is Info: a measured rectangle was found, just not the icon's");

        // Nothing was lowered wholesale. What is left on the warning channel is what really failed: a read that threw (twice),
        // and the panel's own fatal line on the error channel. The wheel's own refusal line moved with the wheel: it is
        // written once by `ResultsDisplay.ReadNativeScroll` when the page's `ScrollInput` cannot be read (1.19.59), so the
        // panel keeps two warnings and the display keeps that one.
        Check(Tally(panel, "LogWarning") == 2,
            "the panel keeps exactly two warning sites: the throwing anchor read and the throwing Background Fade walk");
        string display = ProductionText("ResultsDisplay.cs");
        Check(display.Contains("private static bool _loggedNativeScroll;")
              && display.Contains("the result page's own scroll reading could not be read, so the physical wheel decides this frame")
              && panel.Contains("the same HUD row's Background Fade could not be looked up - the walk of the icon ")
              && panel.Contains("Plugin.Logger?.LogError(\"Panel failed: \" + error);"),
            "the wheel's own warning is written once, by the display, and the real errors keep their own level and their own text");
    }
    // ---------- 6. the press ----------

    private static void ThePressOwesOneAnswerAndNotEveryDiagnostic()
    {
        // The recorded page: fourteen F8 presses, one 268 B line about the artifact-summary block that carries a control.
        // The old gate widened for every call site on a press, so it printed fourteen times; the strict gate prints once.
        var oldGate = new HashSet<string>();
        var strictGate = new HashSet<string>();
        int oldLines = 0, strictLines = 0, answerLines = 0;
        var answerGate = new HashSet<string>();
        for (int press = 0; press < 14; press++)
        {
            // Each press re-arms the one answer it is owed, and nothing else.
            bool pending = true;
            if (ResultReportPolicy.OncePerPage(oldGate, "vanilla-block-action-artifact summary", true, pending)) oldLines++;
            if (ResultReportPolicyStrict.OncePerPage(strictGate, "vanilla-block-action-artifact summary")) strictLines++;
            if (ResultReportPolicy.OncePerPage(answerGate, "board-refusal-of-this-press", true, pending)) answerLines++;
        }
        Check(oldLines == 14 && strictLines == 1,
            "the recorded fourteen repetitions of one unchanged line become one: old=" + oldLines + " new=" + strictLines);
        Check(answerLines == 14,
            "while the line a press is really owed still prints once per press: " + answerLines);
        Check(ResultReportPolicyStrict.OncePerPage(null, "no-set") && ResultReportPolicyStrict.OncePerPage(null, "no-set"),
            "the strict gate is null-safe, exactly as the ordinary one is");
        Check(ResultReportPolicyStrict.OncePerPage(strictGate, "another-page-reason"),
            "and it is per reason inside the page, so a different fact is still reported");

        string display = ProductionText("ResultsDisplay.cs");
        string visibility = ProductionText("ResultVisibility.cs");
        Check(visibility.Contains("public static class ResultReportPolicyStrict")
              && visibility.Contains("if (reports != null && reports.Contains(key)) return false;"),
            "the strict gate is the production type this replay just ran");
        Check(display.Contains("ResultReportPolicyStrict.OncePerPage(HeadingReports, \"vanilla-block-shared-\" + what)")
              && display.Contains("ResultReportPolicyStrict.OncePerPage(HeadingReports, \"vanilla-block-action-\" + what)"),
            "both vanilla-block descriptions go through it: neither answers a press, so neither may re-print on one");
        Check(Tally(display, "ResultReportPolicy.OncePerPage(HeadingReports") >= 13,
            "and every other call site keeps the press's own exception: the F8 answer itself is untouched");
        Check(ProductionText("PanelBehaviour.cs").Contains("bool shown = ResultsDisplay.ToggleForcedFallback();"),
            "the F8 visibility answer is still read from the board the page was really showing");
    }

    // ---------- 7. what the drawn line names ----------

    private static void TheDrawnRectangleNamesItsSource()
    {
        string panel = ProductionText("PanelBehaviour.cs");
        // The draw line is the one that prints `anchor=`; it is found by its own new text so the `_loggedDraw` declaration
        // earlier in the file cannot answer for it.
        string draw = Segment(panel, "string source = _fallbackSpotDrawn", "// One field list per cell");
        Check(draw.Length > 0
              && draw.Contains("string source = _fallbackSpotDrawn ? \"conservative place (no proven rung)\"")
              && draw.Contains(": (_anchorLocked ? \"flashlight icon\" : \"config PositionX/PositionY\");")
              && draw.Contains("anchor={source}"),
            "`anchor=` names the source of the drawn rectangle: the recorded 1.19.48 line said `flashlight icon` on a table that "
                + "had been drawn at the conservative place");
        Check(!panel.Contains("anchor={(_anchorLocked"),
            "a locked anchor is no longer enough to call a placement anchored");
        Check(panel.Contains("if (!TryResolveOrigin(_grid, out var origin)) { _grid = null; _content = null; _origin = default; return; }"),
            "and a refused poll still clears the grid, the content and the drawn origin together");
    }

    // ---------- 8. one version number ----------

    private static void OneVersionNumberEverywhere()
    {
        string info = ProductionText("PluginInfo.cs");
        int at = info.IndexOf("internal const string Version = \"", StringComparison.Ordinal);
        Check(at >= 0, "the plugin's version constant is where the rest of this reads it from");
        if (at < 0) return;
        int start = at + "internal const string Version = \"".Length;
        int end = info.IndexOf('"', start);
        string version = info.Substring(start, end - start);
        Check(version.Length > 0 && char.IsDigit(version[0]),
            "the version is a real number: " + version);
        Check(version != "1.19.40" && version != "1.19.48",
            "and it is not one of the two numbers this round had to tell apart (" + version + ")");

        // The one source: the assembly and file versions are built from it, the loader line prints it, and the settlement
        // save writes it.
        Check(info.Contains("[assembly: System.Reflection.AssemblyVersion(HawChow.GTFOCombatPanel.PluginInfo.Version + \".0\")]")
              && info.Contains("[assembly: System.Reflection.AssemblyFileVersion(HawChow.GTFOCombatPanel.PluginInfo.Version + \".0\")]"),
            "the assembly version and the file version are both derived from the one constant");
        Check(ProductionText("Plugin.cs").Contains("[BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]")
              && ProductionText("Plugin.cs").Contains("Logger.LogInfo($\"{PluginInfo.Name} {PluginInfo.Version} loaded;"),
            "so the loader line and the BepInEx registration carry it");
        Check(ProductionText("ResultsSaver.cs").Contains("ResultsLog.Build(succeeded, writtenAt, PluginInfo.Version, board, report, metadata,"),
            "and so does the settlement save's own `version` field");

        // The package's manifest, the README a reader lands on, and the changelog this delivery adds an entry to.
        string manifest = ProductionText(Path.Combine("Thunderstore", "manifest.json"));
        Check(manifest.Contains("\"version_number\": \"" + version + "\""),
            "the Thunderstore manifest is the same number (" + version + "), which is also what Verify.ps1 compares");
        string readme = ProductionText("README.md");
        Check(readme.Contains("源码版本为 **" + version + "**"),
            "the README's current-state line says the same number");
        string changelog = ProductionText(Path.Combine("Thunderstore", "CHANGELOG.md"));
        Check(changelog.Contains("## " + version),
            "and the changelog has a section for it");
        Check(changelog.IndexOf("## " + version, StringComparison.Ordinal)
              < changelog.IndexOf("## 1.19.40", StringComparison.Ordinal) || !changelog.Contains("## 1.19.40"),
            "the new entry is the first one, and the older entries are left exactly as they were");
        string readmeRules = Segment(readme, "## 构建 / 验证", "## 验证边界");
        Check(readmeRules.Contains("**版本号纪律**") && readmeRules.Contains("同一个号不得分发两份不同的修复代码"),
            "and the project's own build section states the rule: bump the patch digit before any delivery, never reuse a number");
    }

    // ---------- helpers ----------

    // The production sources, read from the project root this test assembly was built inside.
    private static string ProductionText(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj")))
            directory = directory.Parent;
        if (directory == null) return string.Empty;
        string path = Path.Combine(directory.FullName, name);
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    // The text between two markers, or empty when either is missing - a missing marker is a failed assertion in the caller,
    // never an exception here.
    private static string Segment(string text, string from, string to)
    {
        int start = text.IndexOf(from, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        int end = text.IndexOf(to, start, StringComparison.Ordinal);
        return end < 0 ? text.Substring(start) : text.Substring(start, end - start);
    }

    private static int Tally(string text, string needle)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0) { count++; index += needle.Length; }
        return count;
    }
}
