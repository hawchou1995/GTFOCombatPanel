using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// 1.19.23: a settlement record and the player's edited scoring configuration are lost only when the player
// deletes them. The defect this round answers: records were written next to the mod's DLL, inside the package
// folder that r2modman deletes and re-extracts on every install/update - the machine showed the results folder's
// CreationTime equal to mods.yml's installedAtTime (2026-11-11 19:46:33 and 2026-11-10 17:12:35), with the
// earliest surviving record minutes later and every record older than the install gone - while the mod's own
// README promised user files are never overwritten.
//
// The rules implemented and driven here (all through the real policies of this build, no Unity):
//
//   1. the archive root is the game's persistent data path; the plugin folder is a fallback only when that
//      path is empty or unusable, and the fallback is said out loud in the log;
//   2. records still sitting in the old plugin-folder location are copied into the new root exactly once,
//      marker-guarded, leaving the source untouched;
//   3. the file name carries the write time to the millisecond, the run, the rundown/level key (sanitised,
//      length-capped) and success/failure, so the same name can never be produced twice;
//   4. new records are written with create-new semantics and a numeric suffix on collision - an existing
//      record is never truncated or overwritten; the session's own record refreshes atomically (temp + move);
//   5. nothing in production sources deletes files or directories; the static scan below proves the absence;
//   6. a settlement page archives whether or not the board is drawn - the visibility gate may only suppress
//      drawing - while the (page, run) latch keeps one record per expedition per page;
//   7. scoring.json is read from the stable root first, the plugin folder's copy is imported once when the
//      root has none (a copy that differs from the shipped default is the player's), and the mod's own
//      default-writing targets the stable root.
internal static class Release11923Scenarios
{
    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        AFolderResolvesToThePersistentPath(check);
        ANameCarriesMillisecondsRunLevelAndResult(check);
        ACollidingWriteGetsASuffixAndNeverTruncates(check);
        TheLegacyImportRunsOnceAndLeavesTheSourceUntouched(check);
        ASettlementPageArchivesWithEveryDisplaySwitchOff(check);
        NothingInProductionCanDeleteARecord(check);
        TheScoringFileSurvivesThePackageManager(check);
        Wiring(check);
    }

    private static string Scratch()
    {
        string directory = Path.Combine(Path.GetTempPath(), "gtfocp-11923-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void TryDelete(string directory)
    {
        try { if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch { }
    }

    // (1) the persistent data path always answers when it is really usable; the plugin folder answers only for
    // an empty path, a missing path or a path a plain file is blocking.
    private static void AFolderResolvesToThePersistentPath(Action<bool, string> check)
    {
        string temp = Scratch();
        try
        {
            string persistent = Path.Combine(temp, "persist");
            string plugin = Path.Combine(temp, "plugin");
            Directory.CreateDirectory(persistent);
            Directory.CreateDirectory(plugin);
            string folder = SettlementArchiveLocation.ResolveArchiveFolder(persistent, plugin, out bool used);
            check(used && folder == Path.Combine(persistent, SettlementArchiveLocation.ArchiveFolderName),
                "the archive resolves under the game's own persistent data path when that path is usable");
            string fromEmpty = SettlementArchiveLocation.ResolveArchiveFolder("", plugin, out bool usedEmpty);
            check(!usedEmpty && fromEmpty == Path.Combine(plugin, SettlementArchiveLocation.ArchiveFolderName),
                "an empty persistent path falls back to the legacy plugin folder");
            string fromNull = SettlementArchiveLocation.ResolveArchiveFolder(null, plugin, out bool usedNull);
            check(!usedNull && fromNull == Path.Combine(plugin, SettlementArchiveLocation.ArchiveFolderName),
                "a missing persistent path falls back the same way");
            string blocker = Path.Combine(temp, "blocker");
            File.WriteAllText(blocker, "not a directory");
            string blocked = SettlementArchiveLocation.ResolveArchiveFolder(blocker, plugin, out bool usedBlocked);
            check(!usedBlocked && blocked == Path.Combine(plugin, SettlementArchiveLocation.ArchiveFolderName),
                "a persistent path blocked by a plain file is unusable and falls back - never silently overwritten");
            check(!SettlementArchiveLocation.UsableDirectory("") && !SettlementArchiveLocation.UsableDirectory(blocker)
                && SettlementArchiveLocation.UsableDirectory(persistent),
                "the usability test is the whole of that decision and reads the same way");
        }
        finally { TryDelete(temp); }
    }

    // (2)/(3) the name grammar: millisecond stamp + run + sanitised rundown/level key + result, round-tripped.
    private static void ANameCarriesMillisecondsRunLevelAndResult(Action<bool, string> check)
    {
        DateTime same = new DateTime(2026, 11, 12, 9, 30, 0, 250, DateTimeKind.Local);
        string level = "C1 它发生了";
        string first = SettlementRecordNamer.BuildFileName(same, 1u, "R7", level, true);
        string second = SettlementRecordNamer.BuildFileName(same, 2u, "R7", level, true);
        check(first != second,
            "two expeditions saved in the same second get different names");
        check(first.Contains("20261112-093000-250") && first.Contains("-r1-") && first.EndsWith("-success.json")
            && first.StartsWith(SettlementRecordNamer.Prefix, StringComparison.Ordinal),
            $"the name carries the millisecond stamp, the run and the result: {first}");
        check(second.Contains("-r2-"),
            "the second run of the same second is told apart by its own run counter");
        DateTime rounded = same.AddTicks(-(same.Ticks % TimeSpan.TicksPerMillisecond));
        check(SettlementRecordNamer.TryParse(first, out DateTime writtenAt, out uint run, out string key, out bool succeeded)
            && writtenAt == rounded && run == 1u && succeeded && key == SettlementRecordNamer.LevelKey("R7", level),
            "the name round-trips the write time, the run, the level key and the result it was built from");
        string failure = SettlementRecordNamer.BuildFileName(same, 4u, "R7", level, false);
        check(SettlementRecordNamer.TryParse(failure, out _, out uint failureRun, out _, out bool failureOk)
            && failureRun == 4u && !failureOk,
            "a failure name round-trips as a failure too");
        // Rendering the settlement JSON uses the same values the name parses back, so a recorded screen can
        // never be renamed into another screen.
        string json = ResultsLog.Build(true, rounded, "1.19.23", ImpactBoard.Build(ImpactScoring.Evaluate(
                new[] { new ImpactInput { Name = "甲", EnemyDamage = 10 } }, ImpactScoring.DefaultWeights, 1, 1), true, true),
            ImpactScoring.Evaluate(new[] { new ImpactInput { Name = "甲", EnemyDamage = 10 } }, ImpactScoring.DefaultWeights, 1, 1),
            ExpeditionMetadata.Create("R7", "C1", level), 1u);
        check(json.Length > 0 && json.Contains("\"result\": \"success\"") && json.Contains("\"run\": 1")
            && json.Contains("\"rundown\": \"R7\"") && json.Contains(level),
            "the same run, rundown and level the name carries are what the record's JSON carries");

        string dirty = SettlementRecordNamer.Sanitize("R6<C>:*?\"\\/|第-一关");
        check(dirty.IndexOfAny(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*', '-' }) < 0,
            $"filesystem-illegal characters and the grammar separator are sanitised out of a level key: {dirty}");
        string capped = SettlementRecordNamer.LevelKey("R7", new string('长', 200));
        check(capped.Length == SettlementRecordNamer.LevelKeyLimit,
            $"a very long level name is length-capped to {SettlementRecordNamer.LevelKeyLimit} characters");
        check(SettlementRecordNamer.LevelKey("", "") == "unknown",
            "a screen with no readable rundown or level still gets a name, never an empty key");
    }

    // (4) create-new writes: the first record stays byte-identical, the colliding write takes a numeric suffix,
    // and the session's own record refreshes atomically without leaving a temp file behind.
    private static void ACollidingWriteGetsASuffixAndNeverTruncates(Action<bool, string> check)
    {
        string temp = Scratch();
        try
        {
            string folder = Path.Combine(temp, "CombatPanel-results");
            DateTime now = new DateTime(2026, 11, 12, 10, 0, 0, 123, DateTimeKind.Local);
            string name = SettlementRecordNamer.BuildFileName(now, 1u, "R7", "C1", true);
            string firstPath = SettlementRecordFiles.WriteCreateNew(folder, name, "first-record-bytes");
            string secondPath = SettlementRecordFiles.WriteCreateNew(folder, name, "second-record-bytes");
            check(firstPath != null && secondPath != null && firstPath != secondPath
                && File.Exists(firstPath) && File.Exists(secondPath),
                "a second write of the same name lands in another file instead of touching the first");
            check(File.ReadAllText(firstPath) == "first-record-bytes",
                "the first record is byte-identical afterwards - create-new never truncates it");
            check(File.ReadAllText(secondPath) == "second-record-bytes",
                "the colliding write's own bytes land in the suffixed file");
            check(secondPath.EndsWith("-2.json") && SettlementRecordFiles.SuffixedPath(firstPath, 2) == secondPath,
                "the suffix is numeric and sits before the extension");
            SettlementRecordFiles.ReplaceAtomically(secondPath, "refreshed-bytes");
            check(File.ReadAllText(secondPath) == "refreshed-bytes" && !File.Exists(secondPath + ".tmp"),
                "the session's own record refreshes atomically (temp + move) and leaves no temp file behind");
            bool missingRefused = false;
            try { SettlementRecordFiles.ReplaceAtomically(Path.Combine(folder, "never-written.json"), "x"); }
            catch (FileNotFoundException) { missingRefused = true; }
            check(missingRefused, "a refresh whose record is gone refuses instead of inventing a new one");
        }
        finally { TryDelete(temp); }
    }

    // (2) the one-time legacy import: copies what is there, leaves the source untouched, runs once across two
    // starts and reports the count it imported.
    private static void TheLegacyImportRunsOnceAndLeavesTheSourceUntouched(Action<bool, string> check)
    {
        string temp = Scratch();
        try
        {
            string stable = Path.Combine(temp, "persist", SettlementArchiveLocation.ArchiveFolderName);
            string legacy = Path.Combine(temp, "plugin", SettlementArchiveLocation.ArchiveFolderName);
            Directory.CreateDirectory(legacy);
            string firstLegacy = Path.Combine(legacy, "CombatPanel-results-20261111-195350.json");
            string secondLegacy = Path.Combine(legacy, "CombatPanel-results-20261111-200000.json");
            File.WriteAllText(firstLegacy, "{\"old\":1}");
            File.WriteAllText(secondLegacy, "{\"old\":2}");
            File.WriteAllText(Path.Combine(legacy, "notes.txt"), "not a record");
            LegacyImportReport imported = SettlementImportPolicy.ImportLegacyRecords(stable, legacy);
            check(imported.Ran && imported.Copied == 2 && imported.Message != null
                && imported.Message.Contains("2 record"),
                "the first start copies the two records that are there and its one log line reports the count");
            check(File.Exists(firstLegacy) && File.Exists(secondLegacy) && File.Exists(Path.Combine(legacy, "notes.txt")),
                "the legacy folder is left exactly as it was - nothing at the source is deleted or renamed");
            check(File.Exists(Path.Combine(stable, "CombatPanel-results-20261111-195350.json"))
                && File.ReadAllText(Path.Combine(stable, "CombatPanel-results-20261111-195350.json")) == "{\"old\":1}",
                "the copied record lands in the stable root with its bytes intact");
            check(!File.Exists(Path.Combine(stable, "notes.txt")),
                "only records are copied - a stray non-record file is not brought along");
            LegacyImportReport again = SettlementImportPolicy.ImportLegacyRecords(stable, legacy);
            check(!again.Ran && again.Copied == 0,
                "a second start imports nothing again: the marker, not process memory, is the guard");
            check(File.Exists(Path.Combine(stable, SettlementImportPolicy.MarkerFileName))
                && Directory.GetFiles(stable, "*.json").Length == 2,
                "the marker lives inside the archive and the two records are there exactly once");
            // A folder that never held records also guards itself once, so no launch keeps scanning it.
            string emptyStable = Path.Combine(temp, "persist2", SettlementArchiveLocation.ArchiveFolderName);
            string emptyLegacy = Path.Combine(temp, "plugin2", SettlementArchiveLocation.ArchiveFolderName);
            LegacyImportReport nothing = SettlementImportPolicy.ImportLegacyRecords(emptyStable, emptyLegacy);
            LegacyImportReport nothingAgain = SettlementImportPolicy.ImportLegacyRecords(emptyStable, emptyLegacy);
            check(nothing.Ran && nothing.Copied == 0 && !nothingAgain.Ran,
                "an import with nothing to copy runs once, reports zero, and is guarded from then on");
        }
        finally { TryDelete(temp); }
    }

    // (6) the archive decision reads the page and the (page, run) latch - never the visibility gate, which may
    // only suppress drawing. The gate itself is driven here with every switch off, and the latch is the exact
    // instance the production saver holds.
    private static void ASettlementPageArchivesWithEveryDisplaySwitchOff(Action<bool, string> check)
    {
        bool shown = ResultVisibility.Shows(false, false, false, false);
        check(!shown,
            "the visibility gate answers hidden when every switch - master, overlay, board, toggle - is off");
        var archive = new SettlementArchive();
        check(archive.ShouldWrite(new IntPtr(7), 3u),
            "the archive latch still says write for a settlement page that exists: the save decision never reads the visibility gate");
        archive.MarkWritten(new IntPtr(7), 3u, "first projection");
        check(!archive.ShouldWrite(new IntPtr(7), 3u),
            "every repaint of that same screen collapses into the one record the latch admits - a hidden screen cannot be written twice by a repaint storm");
        check(archive.ShouldRefresh(new IntPtr(7), 3u, "late rows"),
            "and late data on the still-hidden screen refreshes the same record instead of creating another");
        archive.Forget();
        check(archive.ShouldWrite(new IntPtr(8), 3u),
            "a different page is a different record even at the same run counter");

        string display = Source("ResultsDisplay.cs");
        check(display.Contains("if (!overlay)") && display.Contains("ArchiveScreen(succeeded);"),
            "the production refresh carries the hidden-screen archive call inside the overlay-off branch");
        string hidden = Between(display, "if (!overlay)", "return;");
        check(hidden.Contains("ArchiveScreen(succeeded);") && hidden.Contains("ResetPlacement();"),
            "the archive runs first in the hidden branch, before the placement reset - nothing downstream gates it");
        string screen = Between(display, "private static void ArchiveScreen(bool succeeded)", "private static void ResetPlacement()");
        check(screen.Contains("ResultsSaver.Save(page, succeeded, board, report, CombatSync.Generation);"),
            "the hidden screen saves through the same ResultsSaver path a drawn board uses");
        check(screen.Contains("if (report.Entries.Count == 0)") && screen.Contains("\"archive-empty-roster\"")
              && screen.Length > 0,
            "a page with nothing retained yet still archives nothing, exactly like the drawn path - 1.19.25 adds the one bounded line naming the reason instead of the silent return");
        string drawGate = Between(display, "internal static void Draw()", "// ---- the influence board");
        check(drawGate.Contains("ResultVisibility.Shows(") && !drawGate.Contains("ArchiveScreen("),
            "the Draw switch-off still suppresses drawing and never performs the archive - drawing is not the record's trigger");
    }

    // (5) the static scan: no production source may delete a file or a directory. The known scoring.json temp
    // swap used to delete its target before the move; it now replaces atomically, so the count is zero.
    private static void NothingInProductionCanDeleteARecord(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the 1.19.23 scenarios locate the production sources");
        if (root == null) return;
        var offenders = new List<string>();
        int deletes = 0;
        foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.TopDirectoryOnly))
        {
            string text = File.ReadAllText(file);
            int fileDeletes = CountOf(text, "File.Delete") + CountOf(text, "Directory.Delete");
            deletes += fileDeletes;
            if (fileDeletes > 0) offenders.Add(Path.GetFileName(file));
        }
        check(deletes == 0,
            "no production source has a File.Delete or Directory.Delete call - records survive unless the player deletes them ("
            + string.Join(", ", offenders) + ")");
        string saver = File.ReadAllText(Path.Combine(root, "ResultsSaver.cs"));
        string storage = File.ReadAllText(Path.Combine(root, "SettlementStorage.cs"));
        check(storage.Contains("FileMode.CreateNew"),
            "new records are written with create-new semantics");
        check(!saver.Contains("WriteAllText("),
            "the archive writer never overwrites a record through WriteAllText");
        check(storage.Contains("File.Move(temp, existingPath, true)") || storage.Contains("File.Move(temp, path, true)"),
            "both replaces (the session refresh and the scoring write) are atomic temp+moves, not delete-then-write");
        string scoring = File.ReadAllText(Path.Combine(root, "ScoringSettings.cs"));
        check(!scoring.Contains("File.Delete"),
            "the scoring file's own writer no longer deletes its target either");
    }

    // (7) scoring.jsort precedence: stable root wins; the plugin folder's copy is imported once when the root
    // has none - a copy that differs from the shipped default is the player's; the shipped defaults are written
    // once to the stable root; an unusable persistent path falls back to the plugin folder in the log.
    private static void TheScoringFileSurvivesThePackageManager(Action<bool, string> check)
    {
        string temp = Scratch();
        try
        {
            // (a) a user copy in the stable root wins over anything the plugin folder holds.
            string persistent = Path.Combine(temp, "persist");
            string plugin = Path.Combine(temp, "plugin");
            Directory.CreateDirectory(persistent);
            Directory.CreateDirectory(plugin);
            string stableConfig = Path.Combine(persistent, ScoringFiles.DirectoryName);
            Directory.CreateDirectory(stableConfig);
            string stableFile = Path.Combine(stableConfig, ScoringFiles.FileName);
            File.WriteAllText(stableFile, ScoreWithStagger(3));
            WriteLegacyScoring(plugin, ScoreWithStagger(7));
            ScoringFilePlan plan = ScoringFileLocation.Resolve(persistent, plugin);
            check(plan.Path == stableFile && !plan.ImportedFromLegacy && plan.Message != null
                && plan.Message.Contains("never replaces it"),
                "the player's copy in the stable root wins, and the log line states which file was used");
            ScoringSettings fromStable = ScoringFiles.Load(plan.Path, out bool createdA, out string errorA);
            check(!createdA && errorA == null && fromStable != null && Near(fromStable.Weights["Stagger"], 3),
                "the runtime reads the stable copy, not the plugin folder's");
            check(ReadLegacyScoring(plugin) == ScoreWithStagger(7),
                "and the plugin folder's copy is left untouched by the whole resolution");

            // (b) root has none: the plugin folder's copy is imported once, and one that differs from the
            // shipped default is treated as the player's.
            string persistent2 = Path.Combine(temp, "persist2");
            string plugin2 = Path.Combine(temp, "plugin2");
            Directory.CreateDirectory(persistent2);
            Directory.CreateDirectory(plugin2);
            WriteLegacyScoring(plugin2, ScoreWithStagger(5));
            ScoringFilePlan imported = ScoringFileLocation.Resolve(persistent2, plugin2);
            check(imported.ImportedFromLegacy && imported.Path != null && File.Exists(imported.Path)
                && imported.Path.StartsWith(persistent2.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                "with no stable copy, the plugin folder's scoring.json is imported once into the stable root");
            check(File.ReadAllText(imported.Path) == ScoreWithStagger(5),
                "a legacy copy that differs from the shipped default is kept as the player's - never replaced by the freshly extracted package default");
            ScoringSettings fromImported = ScoringFiles.Load(imported.Path, out bool createdB, out string errorB);
            check(!createdB && errorB == null && fromImported != null && Near(fromImported.Weights["Stagger"], 5),
                "the player's edited weight is what actually scores");
            check(ReadLegacyScoring(plugin2) == ScoreWithStagger(5),
                "the legacy copy itself is not moved or consumed");

            // (c) a plugin-folder copy equal to the shipped default is also the player's file and is imported,
            // not overwritten by the package's own fresh default.
            string persistent3 = Path.Combine(temp, "persist3");
            string plugin3 = Path.Combine(temp, "plugin3");
            Directory.CreateDirectory(persistent3);
            Directory.CreateDirectory(plugin3);
            string shipped = ScoringSettings.Defaults().ToJson();
            WriteLegacyScoring(plugin3, shipped);
            ScoringFilePlan equal = ScoringFileLocation.Resolve(persistent3, plugin3);
            check(equal.ImportedFromLegacy && File.ReadAllText(equal.Path) == shipped,
                "even a legacy copy equal to the shipped default is imported and kept - the player's file, whatever it held");

            // (d) nothing anywhere: the shipped defaults are written once to the stable root, never into the
            // plugin folder that the next package install deletes.
            string persistent4 = Path.Combine(temp, "persist4");
            string plugin4 = Path.Combine(temp, "plugin4");
            Directory.CreateDirectory(persistent4);
            Directory.CreateDirectory(plugin4);
            ScoringFilePlan fresh = ScoringFileLocation.Resolve(persistent4, plugin4);
            check(fresh.Path != null && fresh.Path.StartsWith(persistent4.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                "with no file anywhere, the plan points at the stable root for the default write");
            ScoringSettings defaults = ScoringFiles.Load(fresh.Path, out bool createdD, out string errorD);
            check(createdD && errorD == null && defaults != null && File.Exists(fresh.Path),
                "the shipped defaults are written once, to the stable root");
            check(!Directory.Exists(Path.Combine(plugin4, ScoringFiles.DirectoryName)),
                "the plugin folder stays clean - there is nothing there for an upgrade to replace");

            // (e) an unusable persistent path degrades to the plugin folder with a log line saying so.
            ScoringFilePlan fallen = ScoringFileLocation.Resolve("", plugin2);
            check(!fallen.ImportedFromLegacy && fallen.Path == Path.Combine(plugin2, ScoringFiles.DirectoryName, ScoringFiles.FileName)
                && fallen.Message != null && fallen.Message.Contains("falls back"),
                "an unusable persistent path falls back to the plugin folder, and the plan says so in the log line");
        }
        finally { TryDelete(temp); }
    }

    // ---- wiring, read from the production source -----------------------------------------------

    private static void Wiring(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the 1.19.23 regression locates the production sources");
        if (root == null) return;
        string saver = File.ReadAllText(Path.Combine(root, "ResultsSaver.cs"));
        string storage = File.ReadAllText(Path.Combine(root, "SettlementStorage.cs"));
        string plugin = File.ReadAllText(Path.Combine(root, "Plugin.cs"));
        string info = File.ReadAllText(Path.Combine(root, "PluginInfo.cs"));
        string manifest = File.Exists(Path.Combine(root, "Thunderstore", "manifest.json"))
            ? File.ReadAllText(Path.Combine(root, "Thunderstore", "manifest.json")) : string.Empty;
        check(saver.Contains("Application.persistentDataPath")
            && saver.Contains("SettlementArchiveLocation.ResolveArchiveFolder("),
            "the Unity caller supplies Application.persistentDataPath and hands it to the pure folder policy");
        check(saver.Contains("SettlementImportPolicy.ImportLegacyRecords(") && storage.Contains(MarkerName()),
            "the one-time import is wired to the marker-guarded policy");
        check(saver.Contains("SettlementRecordNamer.BuildFileName(") && saver.Contains("SettlementRecordFiles.WriteCreateNew("),
            "new saves go through the collision-proof namer and the create-new writer");
        check(saver.Contains("SettlementRecordFiles.ReplaceAtomically(_lastPath, json)"),
            "the in-place refresh goes through the atomic replace");
        check(!saver.Contains("File.Delete") && !saver.Contains("Directory.Delete") && !saver.Contains("CombatPanel-results-{writtenAt:yyyyMMdd-HHmmss}"),
            "the old second-resolution name and the delete-prone writers are gone from the saver");
        check(plugin.Contains("ScoringFileLocation.Resolve(persistentDataPath, PluginDirectory())")
            && plugin.Contains("ResultsSaver.Initialize();"),
            "the plugin resolves the scoring file through the stable-location policy and prepares the archive root at load");
        check(info.Contains("Version = \"1.19.38\""),
            "the version carrier says 1.19.38");
        check(manifest.Contains("\"version_number\": \"1.19.38\""),
            "the Thunderstore manifest carries version_number 1.19.38");
        check(!plugin.Contains("ScoringFiles.Resolve(PluginDirectory())"),
            "the old scoring resolution from the plugin folder is gone");
        Console.WriteLine("WIRING: RELEASE_11923_SETTLEMENT_RECORDS_SURVIVE (static source check; not a runtime behaviour test)");
    }

    private static string MarkerName() => SettlementImportPolicy.MarkerFileName.Substring(1);

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
