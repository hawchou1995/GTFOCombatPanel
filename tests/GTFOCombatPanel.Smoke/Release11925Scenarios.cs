using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// 1.19.25: the review round that closes 1.19.22-1.19.24. Four new behaviours are pinned here, all through the
// real policies of this build (the ResultReportPolicy gate and the scoring-file resolution) or read back out of
// the production sources:
//
//   (1) a forced refusal prints once per F8 press, never once per frame - `ResultReportPolicy.OncePerPage` now
//       takes the press's own pending flag, and the refusal that spends the press is the only one that overrides
//       the per-page latch (the player's per-frame log-flood complaint);
//   (2) one press is never answered twice - the two refusal sites that print their own line (fail-no-room and
//       empty-roster) clear the pending flag exactly as ReportHeldBack does, so the next drawn frame cannot print
//       a second, false "board drawn" line for a press that was already refused;
//   (3) a hidden result page with no retained counters says so once (`archive-empty-roster`) instead of silently
//       failing the "a page that exists archives its screen" promise, and still writes nothing;
//   (4) when the one-time scoring import throws, the shipped defaults are NOT written to the stable root - the
//       player's edited legacy scoring.json stays the resolved source and the import is retried on the next launch.
internal static class Release11925Scenarios
{
    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        AForcedRefusalPrintsOncePerPress(check);
        ARefusalSiteSpendsThePress(check);
        AHiddenEmptyPageSaysSoOnce(check);
        AFailedScoringImportKeepsTheLegacyFile(check);
        Wiring(check);
    }

    // The 1.19.25 `ResultReportPolicy` contract, driven exactly as the production flag moves: the press sets the
    // pending flag, the refusal that prints spends it, and the ordinary latch takes over from the next repaint on -
    // one line per press, so an F8 key held over a refused page can never turn a diagnostic into a per-frame flood.
    private static void AForcedRefusalPrintsOncePerPress(Action<bool, string> check)
    {
        var reports = new HashSet<string>();
        bool pending = false;                          // ResultsDisplay._forcedOutcomePending
        check(ResultReportPolicy.OncePerPage(reports, "held-back-draw-no-placement", false, pending),
            "an ordinary frame whose reason has not fired yet prints");
        check(!ResultReportPolicy.OncePerPage(reports, "held-back-draw-no-placement", false, pending),
            "and the per-page latch silences every ordinary repaint of the same reason");
        pending = true;                                // an F8 press arrived and its answer is owed
        check(ResultReportPolicy.OncePerPage(reports, "held-back-draw-no-placement", true, pending),
            "the forced frame that still owes the press prints its refusal even though the latch already fired for that reason");
        pending = false;                               // the refusal line spent the flag
        check(!ResultReportPolicy.OncePerPage(reports, "held-back-draw-no-placement", true, pending),
            "the repaint right after that press was answered is silent again: one line per press, never one line per frame");
        check(!ResultReportPolicy.OncePerPage(reports, "held-back-draw-no-placement", true, pending),
            "and it stays silent on every further repaint while F8 stays down - the whole of the 1.19.25 flood fix");
        pending = true;                                // F8 was pressed again
        check(ResultReportPolicy.OncePerPage(reports, "held-back-draw-no-placement", true, pending),
            "a new press re-arms the exception and gets its own refusal line - a press is never answered with silence");
    }

    // A press answered by a refusal must not be answered again. ReportHeldBack clears the pending flag after it
    // prints; the two refusal sites that print their own line must do the same, or the next drawn frame would print
    // a second, false "board drawn" line for a press that was already refused. This reads the production source.
    private static void ARefusalSiteSpendsThePress(Action<bool, string> check)
    {
        string display = Source("ResultsDisplay.cs");
        string failNoRoom = Between(display, "if (ResultReportPolicy.OncePerPage(HeadingReports, \"fail-no-room\"", "return;");
        check(failNoRoom.Length > 0 && failNoRoom.Contains("_forcedOutcomePending = false;"),
            "the fail-no-room refusal clears the pending flag after printing, so the press it answered can never be answered twice");
        string emptyRoster = Between(display, "if (ResultReportPolicy.OncePerPage(HeadingReports, \"empty-roster\"", "return;");
        check(emptyRoster.Length > 0 && emptyRoster.Contains("_forcedOutcomePending = false;"),
            "the empty-roster refusal clears the pending flag after printing, exactly as ReportHeldBack does");
        int spendSites = CountOf(display, "if (Forced) _forcedOutcomePending = false;");
        check(spendSites == 4,
            "exactly the four refusal paths spend a forced press - ReportHeldBack, fail-no-room, empty-roster and the forced-aware deferral - each at its own site (found " + spendSites + ")");
        // 1.19.26 re-point: the drawn half is now answered from the repaint that really issues the draw (`ReportForcedDraw(_origin)`
        // after `HudTable.Draw`), so this checks the call that exists rather than the one the old fit-time emission used.
        check(display.Contains("if (!Forced || !_forcedOutcomePending) return;") && display.Contains("ReportForcedDraw(_origin);"),
            "the drawn half still only answers a press that was never answered, so no frame can print two answers for one press");
    }

    // A hidden result page that has nothing to archive must say so once, naming the real reason, and must still
    // write nothing. The visible path's `empty-roster` line already does this; the hidden path's ArchiveScreen was
    // silently returning. Read from the production source because ArchiveScreen touches Unity objects headlessly.
    private static void AHiddenEmptyPageSaysSoOnce(Action<bool, string> check)
    {
        string display = Source("ResultsDisplay.cs");
        string archive = Between(display, "private static void ArchiveScreen", "private static bool _loggedArchiveError;");
        check(archive.Contains("if (report.Entries.Count == 0)") && archive.Contains("\"archive-empty-roster\""),
            "the hidden path's empty report is gated by the same once-per-page policy as the visible empty-roster");
        check(archive.Contains("[DIAG] settlement archive held back: no retained player counters"),
            "and it names the real reason out loud - a hidden page with no retained counters says so once instead of archiving nothing silently");
        int emptyIndex = archive.IndexOf("report.Entries.Count == 0", StringComparison.Ordinal);
        int saveIndex = archive.IndexOf("ResultsSaver.Save(", StringComparison.Ordinal);
        check(emptyIndex >= 0 && saveIndex > emptyIndex,
            "an empty report is still never written: the archive write stays after the empty-report return, exactly as 1.19.23 shipped it");
        check(archive.Contains("ImpactBoard.Build(report, true, succeeded)"),
            "the archive semantics are untouched: a non-empty hidden report still builds the same split board and saves it");
    }

    // The failed-import path of `ScoringFileLocation.Resolve`: the copy throws, the shipped defaults must NOT be
    // written to the stable root (they would shadow the player's edited legacy file for ever, because the stable
    // root is read first from then on), the legacy file stays the resolved source, and the next launch retries.
    private static void AFailedScoringImportKeepsTheLegacyFile(Action<bool, string> check)
    {
        string temp = Scratch();
        try
        {
            string persistent = Path.Combine(temp, "persist");
            string plugin = Path.Combine(temp, "plugin");
            Directory.CreateDirectory(persistent);
            Directory.CreateDirectory(plugin);
            WriteLegacyScoring(plugin, ScoreWithStagger(7));
            // The import throws: a *file* sits where the stable config directory must be created, so
            // Directory.CreateDirectory on that folder fails and CopyCreateNew is never reached.
            string blocker = Path.Combine(persistent, ScoringFiles.DirectoryName);
            File.WriteAllText(blocker, "a file blocking the config directory");
            string stable = Path.Combine(blocker, ScoringFiles.FileName);

            ScoringFilePlan plan = ScoringFileLocation.Resolve(persistent, plugin);
            check(plan.Path == Path.Combine(plugin, ScoringFiles.DirectoryName, ScoringFiles.FileName),
                "a failed import keeps the legacy scoring.json as the resolved source - the player's edited file, not the shipped defaults");
            check(!plan.ImportedFromLegacy,
                "and the plan does not claim the file was imported");
            check(plan.Message != null && plan.Message.Contains("still the file in use")
                && plan.Message.Contains("no defaults are written") && plan.Message.Contains("retried on the next launch"),
                "the one bounded line says the player's file is still in use and the import is retried on the next launch: " + plan.Message);
            check(!File.Exists(stable),
                "the shipped defaults are NOT written to the stable root - nothing is allowed to shadow the player's edit");
            ScoringSettings loaded = ScoringFiles.Load(plan.Path, out bool created, out string error);
            check(!created && error == null && loaded != null && Near(loaded.Weights["Stagger"], 7),
                "the file that actually scores is the legacy one with the player's edit (Stagger=7), not the defaults");
            check(ReadLegacyScoring(plugin) == ScoreWithStagger(7),
                "and the legacy file itself is untouched by the whole resolution");

            // The "retried on the next launch" half: once the obstacle is gone the same call succeeds and imports.
            File.Delete(blocker);
            ScoringFilePlan retried = ScoringFileLocation.Resolve(persistent, plugin);
            check(retried.ImportedFromLegacy
                && retried.Path == Path.Combine(persistent, ScoringFiles.DirectoryName, ScoringFiles.FileName)
                && File.Exists(retried.Path) && File.ReadAllText(retried.Path) == ScoreWithStagger(7),
                "the next launch retries the same import and succeeds: the player's file moves to the stable root with the edit intact");
        }
        finally { TryDelete(temp); }
    }

    // ---- wiring, read from the production source -----------------------------------------------

    private static void Wiring(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the 1.19.25 regression locates the production sources");
        if (root == null) return;
        string info = File.ReadAllText(Path.Combine(root, "PluginInfo.cs"));
        string manifest = File.Exists(Path.Combine(root, "Thunderstore", "manifest.json"))
            ? File.ReadAllText(Path.Combine(root, "Thunderstore", "manifest.json")) : string.Empty;
        check(info.Contains("Version = \"1.19.38\""),
            "the version carrier says 1.19.38");
        check(manifest.Contains("\"version_number\": \"1.19.38\""),
            "the Thunderstore manifest carries version_number 1.19.38");
        // The shipped file set carries nothing that names this machine: no local drive paths, no package-manager
        // install path, no profile name. Static scan of the same six files and tokens the release checklist greps.
        string shipped = string.Empty;
        foreach (string relative in new[]
        {
            "Thunderstore/CHANGELOG.md", "Thunderstore/README.md", "Thunderstore/manifest.json",
            "README.md", "DATA-AVAILABILITY.md", "defaults/scoring.json"
        })
        {
            string path = Path.Combine(root, relative);
            if (File.Exists(path)) shipped += File.ReadAllText(path) + "\n";
        }
        check(shipped.Length > 0
              && shipped.IndexOf("E:\\", StringComparison.OrdinalIgnoreCase) < 0
              && shipped.IndexOf("C:\\", StringComparison.OrdinalIgnoreCase) < 0
              && shipped.IndexOf("R2ModMan", StringComparison.OrdinalIgnoreCase) < 0
              && shipped.IndexOf("profiles\\", StringComparison.OrdinalIgnoreCase) < 0,
            "no shipped file leaks a local path, the package-manager install path or a profile name (the packaged CHANGELOG is verifiably private)");
        Console.WriteLine("WIRING: RELEASE_11925_PRESS_ONCE_AND_SCORING_IMPORT (static source check; the press-once and import-failure policies are the runtime assertions above)");
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private static string Source(string name)
    {
        string root = SourceRoot();
        if (root == null) throw new InvalidOperationException("The plugin source directory could not be found from " + AppContext.BaseDirectory);
        string path = Path.Combine(root, name);
        if (!File.Exists(path)) throw new FileNotFoundException("Production source is missing", path);
        return File.ReadAllText(path);
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj"))) directory = directory.Parent;
        return directory != null ? directory.FullName : null;
    }

    private static string Between(string text, string from, string to)
    {
        int start = text.IndexOf(from, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        int end = text.IndexOf(to, start, StringComparison.Ordinal);
        return end < 0 ? text.Substring(start) : text.Substring(start, end - start);
    }

    private static int CountOf(string text, string token) => text.Split(new[] { token }, StringSplitOptions.None).Length - 1;

    private static bool Near(double value, double expected) => Math.Abs(value - expected) < 1e-9;

    private static string Scratch()
    {
        string directory = Path.Combine(Path.GetTempPath(), "gtfocp-11925-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void TryDelete(string directory)
    {
        try { if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch { }
    }

    private static string ScoreWithStagger(double stagger)
        => ScoringSettings.Defaults().ToJson().Replace("\"Stagger\": 1.4", "\"Stagger\": " + stagger.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture));

    private static void WriteLegacyScoring(string pluginFolder, string json)
    {
        string directory = Path.Combine(pluginFolder, ScoringFiles.DirectoryName);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ScoringFiles.FileName), json);
    }

    private static string ReadLegacyScoring(string pluginFolder)
    {
        string path = Path.Combine(pluginFolder, ScoringFiles.DirectoryName, ScoringFiles.FileName);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }
}
