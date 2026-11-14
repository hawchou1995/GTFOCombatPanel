using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// 1.19.26: every settlement record now has a visible copy next to the mod's own DLL.
//
// The record itself stays where 1.19.23 put it - under the game's own persistent data path, which no package manager touches -
// and the copy exists only because that path sits three folders deep inside AppData, where a player looking for "the file"
// cannot find it. What is asserted here is the whole contract of that copy, against real temp directories and the real
// production types (`SettlementMirror`, `SettlementRecordFiles`, `SettlementArchiveLocation`, `SettlementImportPolicy`):
//
//   * exactly one record and one copy per result screen, from the same string, byte for byte;
//   * an existing file is never adopted and never overwritten - not even a byte-identical older one with the very same name;
//   * only the copy this screen really created is ever refreshed afterwards;
//   * a failing copy cannot break the record, is reported once per screen with the record's own path, and is bounded;
//   * a copy the player deleted is not recreated;
//   * the `Results` folder is not the legacy import source, is never scanned and is never back-filled;
//   * the package whitelist cannot ever pick any of it up.
//
// The file-level state machine driven below is the production type itself; `ResultsSaver` is asserted to call it in that order,
// with the record first and the copy second, from the same string.
internal static class Release11926ResultsMirrorScenarios
{
    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        ARecordIsCopiedByteForByte(check);
        EveryScreenGetsItsOwnCopy(check);
        AnExistingFileIsNeverAdoptedOrOverwritten(check);
        ARefreshUpdatesBothFiles(check);
        AnUnwritableFolderCostsTheCopyNotTheRecord(check);
        AFailedCopyIsRetriedOnALaterRefreshButBounded(check);
        ADeletedCopyIsNotRecreated(check);
        TheSameFolderIsNotCopiedIntoItself(check);
        TheCopyFolderIsNotAnImportSource(check);
        ThePackageWhitelistCarriesNoRecords(check);
        Wiring(check);
    }

    // A record and its copy, from one string, byte for byte - and the copy lives in `Results` next to the DLL.
    private static void ARecordIsCopiedByteForByte(Action<bool, string> check)
    {
        string root = Scratch();
        try
        {
            string archive = Path.Combine(root, "persistent", SettlementArchiveLocation.ArchiveFolderName);
            string plugin = Path.Combine(root, "plugin");
            Directory.CreateDirectory(plugin);
            string mirror = SettlementArchiveLocation.MirrorArchiveFolder(plugin);
            check(mirror == Path.Combine(plugin, "Results") && SettlementArchiveLocation.MirrorFolderName == "Results",
                "the copy lives in a `Results` folder next to the mod's DLL, and that name is its own - not the record folder's");

            string json = "{\"Run\":1,\"Result\":\"failure\",\"Players\":[]}";
            string name = SettlementRecordNamer.BuildFileName(new DateTime(2026, 1, 12, 10, 10, 10, 123), 1, "R8", "The Admin", false);
            string record = SettlementRecordFiles.WriteCreateNew(archive, name, json);
            check(record != null && File.Exists(record), "the record is written first, under the persistent data path");

            var copy = new SettlementMirror();
            copy.BeginScreen();
            string copied = copy.Create(archive, mirror, name, json, out string reason);
            check(copied != null && File.Exists(copied) && reason == string.Empty,
                $"the copy is written next to the DLL under the same requested name: {copied} ({reason})");
            check(Path.GetFullPath(copied).StartsWith(Path.GetFullPath(mirror), StringComparison.OrdinalIgnoreCase),
                "and it is inside the `Results` folder, not somewhere beside it");
            check(File.ReadAllText(record) == File.ReadAllText(copied),
                "the two files hold exactly the same bytes - they are written from one string, so they cannot drift");
            check(copy.HasCopy && copy.Path == copied && !copy.CanRetry,
                "the copy that was really created is the one this screen remembers, and no further attempt is owed");
        }
        finally { TryDelete(root); }
    }

    // Two expeditions in one session, one failed and one successful screen: each gets its own record and its own copy, and a
    // second screen never reuses or overwrites the first one's files.
    private static void EveryScreenGetsItsOwnCopy(Action<bool, string> check)
    {
        string root = Scratch();
        try
        {
            string archive = Path.Combine(root, "persistent", SettlementArchiveLocation.ArchiveFolderName);
            string plugin = Path.Combine(root, "plugin");
            Directory.CreateDirectory(plugin);
            string mirror = SettlementArchiveLocation.MirrorArchiveFolder(plugin);
            var state = new SettlementMirror();
            var paths = new List<string>();
            var runs = new[] { 1u, 2u };
            for (int index = 0; index < runs.Length; index++)
            {
                bool succeeded = index == 1;                       // one failure, then one success
                string json = "{\"Run\":" + runs[index] + ",\"Result\":\"" + (succeeded ? "success" : "failure") + "\"}";
                string name = SettlementRecordNamer.BuildFileName(new DateTime(2026, 1, 12, 10, 20 + index, 0, 0), runs[index], "R8", "The Admin", succeeded);
                SettlementRecordFiles.WriteCreateNew(archive, name, json);
                state.BeginScreen();                              // a new result screen
                string copied = state.Create(archive, mirror, name, json, out string reason);
                check(copied != null, $"the {(succeeded ? "success" : "failure")} screen of run {runs[index]} gets its copy ({reason})");
                paths.Add(copied);
                check(File.ReadAllText(Path.Combine(archive, name)) == File.ReadAllText(copied),
                    $"run {runs[index]}'s copy holds the record's own bytes");
            }
            check(paths[0] != paths[1] && File.Exists(paths[0]) && File.Exists(paths[1]),
                "two screens left two copies, and the second one did not replace the first");
            check(Directory.GetFiles(mirror, "*.json").Length == 2,
                "exactly one copy per screen, no more");
        }
        finally { TryDelete(root); }
    }

    // The one the user asked for in so many words: a file already sitting at the target name is never claimed, whatever it
    // holds - including a byte-identical older copy of the same record.
    private static void AnExistingFileIsNeverAdoptedOrOverwritten(Action<bool, string> check)
    {
        string root = Scratch();
        try
        {
            string archive = Path.Combine(root, "persistent", SettlementArchiveLocation.ArchiveFolderName);
            string plugin = Path.Combine(root, "plugin");
            string mirror = SettlementArchiveLocation.MirrorArchiveFolder(plugin);
            Directory.CreateDirectory(mirror);
            string json = "{\"Run\":1,\"Result\":\"failure\"}";
            string name = "CombatPanel-results-20260112-101010-123-r1-R8-success.json";
            string occupied = Path.Combine(mirror, name);
            File.WriteAllText(occupied, json);                   // same name, and the same bytes on purpose
            DateTime stamp = new DateTime(2025, 5, 5, 5, 5, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(occupied, stamp);

            var state = new SettlementMirror();
            state.BeginScreen();
            string copied = state.Create(archive, mirror, name, json, out string reason);
            check(copied != null && copied != occupied, $"an existing file is never adopted: the copy was written beside it, at {copied} ({reason})");
            check(Path.GetFileName(copied) == Path.GetFileNameWithoutExtension(name) + "-2.json",
                "and it takes the next numeric suffix, exactly as every other create-new write in this build does");
            check(File.ReadAllText(occupied) == json && File.GetLastWriteTimeUtc(occupied) == stamp,
                "the file that was already there keeps its content and its timestamp - it is not ours to refresh");
            check(File.ReadAllText(copied) == json, "while the copy this screen created holds the record's bytes");
        }
        finally { TryDelete(root); }
    }

    // A later accepted snapshot changes the record: both files follow it, from the same string, and only the copy this screen
    // created is touched.
    private static void ARefreshUpdatesBothFiles(Action<bool, string> check)
    {
        string root = Scratch();
        try
        {
            string archive = Path.Combine(root, "persistent", SettlementArchiveLocation.ArchiveFolderName);
            string plugin = Path.Combine(root, "plugin");
            string mirror = SettlementArchiveLocation.MirrorArchiveFolder(plugin);
            string json = "{\"Run\":2,\"Rows\":1}";
            string name = "CombatPanel-results-20260112-110000-000-r2-R8-failure.json";
            string record = SettlementRecordFiles.WriteCreateNew(archive, name, json);
            var state = new SettlementMirror();
            state.BeginScreen();
            string copied = state.Create(archive, mirror, name, json, out string reason);
            check(copied != null, "the screen's copy starts out with the record (" + reason + ")");

            string later = "{\"Run\":2,\"Rows\":3}";
            SettlementRecordFiles.ReplaceAtomically(record, later);      // what ResultsSaver does to the record
            check(state.Refresh(later, out bool gone) && !gone,
                "the copy is refreshed in place, not recreated");
            check(state.Path == copied && File.Exists(copied),
                "the refreshed file is the one this screen created - the same path, still there");
            check(File.ReadAllText(record) == File.ReadAllText(copied) && File.ReadAllText(copied) == later,
                "and the record and its copy still hold the same, newest bytes");
            check(Directory.GetFiles(mirror, "*.json").Length == 1,
                "an in-place refresh adds no file: one screen is still one copy");
        }
        finally { TryDelete(root); }
    }

    // A plugin folder that cannot be written (a read-only install, here a file sitting where the folder should be) costs the
    // copy and nothing else: the record is already on disk, the reason names it, and nothing throws out of Save.
    private static void AnUnwritableFolderCostsTheCopyNotTheRecord(Action<bool, string> check)
    {
        string root = Scratch();
        try
        {
            string archive = Path.Combine(root, "persistent", SettlementArchiveLocation.ArchiveFolderName);
            string plugin = Path.Combine(root, "plugin");
            Directory.CreateDirectory(plugin);
            File.WriteAllText(Path.Combine(plugin, SettlementArchiveLocation.MirrorFolderName), "not a folder");
            string mirror = SettlementArchiveLocation.MirrorArchiveFolder(plugin);
            string json = "{\"Run\":3,\"Result\":\"failure\"}";
            string name = "CombatPanel-results-20260112-120000-000-r3-R8-failure.json";
            string record = SettlementRecordFiles.WriteCreateNew(archive, name, json);
            check(record != null && File.Exists(record), "the record is written before the copy is even attempted");

            var state = new SettlementMirror();
            state.BeginScreen();
            string copied = null;
            string reason = null;
            bool threw = false;
            try { copied = state.Create(archive, mirror, name, json, out reason); }
            catch (Exception error) { threw = true; reason = error.Message; }
            check(!threw, "a copy that cannot be written never throws out of the caller: " + reason);
            check(copied == null && !string.IsNullOrEmpty(reason),
                "it answers the reason instead, so `ResultsSaver` can print one line with the record's own path: " + reason);
            check(File.ReadAllText(record) == json,
                "and the record on disk is complete and untouched by the failure");
            check(state.CanRetry && !state.HasCopy,
                "the screen still has its bounded retries, but no copy is claimed");
        }
        finally { TryDelete(root); }
    }

    // A copy that failed is retried on a later legitimate refresh - a bounded number of times, never per repaint.
    private static void AFailedCopyIsRetriedOnALaterRefreshButBounded(Action<bool, string> check)
    {
        string root = Scratch();
        try
        {
            string archive = Path.Combine(root, "persistent", SettlementArchiveLocation.ArchiveFolderName);
            string plugin = Path.Combine(root, "plugin");
            Directory.CreateDirectory(plugin);
            string blocker = Path.Combine(plugin, SettlementArchiveLocation.MirrorFolderName);
            File.WriteAllText(blocker, "not a folder");
            string mirror = SettlementArchiveLocation.MirrorArchiveFolder(plugin);
            string json = "{\"Run\":4,\"Rows\":1}";
            string name = "CombatPanel-results-20260112-130000-000-r4-R8-failure.json";
            SettlementRecordFiles.WriteCreateNew(archive, name, json);

            var state = new SettlementMirror();
            state.BeginScreen();
            int attempts = 0;
            while (state.CanRetry) { state.Create(archive, mirror, name, json, out _); attempts++; }
            check(attempts == SettlementMirror.MaximumAttempts,
                $"a folder that cannot be written is attempted {attempts} time(s) and then left alone - not once per repaint");
            check(!state.CanRetry && !state.HasCopy, "after the budget the screen stops asking");

            File.Delete(blocker);                                   // the folder problem goes away
            state.BeginScreen();                                   // a later screen may try again ...
            string copied = state.Create(archive, mirror, name, json, out string reason);
            check(copied != null, "a new screen starts a fresh budget, so a temporary problem does not cost every later screen: " + reason);

            var later = new SettlementMirror();
            later.BeginScreen();
            check(later.Create(archive, mirror, name, "{\"Run\":4,\"Rows\":2}", out reason) != null
                  && later.CanRetry == false,
                "and a copy that was created straight away owes no retry");
        }
        finally { TryDelete(root); }
    }

    // The player deletes the copy. It is not put back: that folder is theirs, and a file they removed was their decision.
    private static void ADeletedCopyIsNotRecreated(Action<bool, string> check)
    {
        string root = Scratch();
        try
        {
            string archive = Path.Combine(root, "persistent", SettlementArchiveLocation.ArchiveFolderName);
            string plugin = Path.Combine(root, "plugin");
            string mirror = SettlementArchiveLocation.MirrorArchiveFolder(plugin);
            string json = "{\"Run\":5,\"Rows\":1}";
            string name = "CombatPanel-results-20260112-140000-000-r5-R8-success.json";
            string record = SettlementRecordFiles.WriteCreateNew(archive, name, json);
            var state = new SettlementMirror();
            state.BeginScreen();
            string copied = state.Create(archive, mirror, name, json, out _);
            check(copied != null, "the copy exists to begin with");

            File.Delete(copied);                                    // the player removes it
            string later = "{\"Run\":5,\"Rows\":2}";
            SettlementRecordFiles.ReplaceAtomically(record, later);
            check(!state.Refresh(later, out bool gone) && gone,
                "the refresh reports the copy as gone instead of writing a new file there");
            check(!File.Exists(copied) && Directory.GetFiles(mirror, "*.json").Length == 0,
                "and nothing was put back: a deleted copy is the player's decision");
            check(!state.HasCopy && !state.CanRetry,
                "the screen neither refreshes nor retries it again");
            check(File.ReadAllText(record) == later,
                "the record itself refreshed normally, exactly as if the copy had never existed");
        }
        finally { TryDelete(root); }
    }

    // The record's folder and the copy's folder are the same directory (the fallback layout on a machine whose persistent path
    // is unusable): a second file with the same bytes would be a duplicate of the record, so nothing is written.
    private static void TheSameFolderIsNotCopiedIntoItself(Action<bool, string> check)
    {
        string root = Scratch();
        try
        {
            string folder = Path.Combine(root, "one", "CombatPanel-results");
            Directory.CreateDirectory(folder);
            string json = "{\"Run\":6}";
            string name = "CombatPanel-results-20260112-150000-000-r6-R8-failure.json";
            SettlementRecordFiles.WriteCreateNew(folder, name, json);

            var state = new SettlementMirror();
            state.BeginScreen();
            string copied = state.Create(folder, folder, name, json, out string reason);
            check(copied == null && reason.Contains("same directory"),
                "the same folder is recognised and no second file is written: " + reason);
            check(Directory.GetFiles(folder, "*.json").Length == 1,
                "the folder still holds exactly the record");
            check(!state.CanRetry && !state.HasCopy, "and no retry is owed for a copy that was never wanted");

            check(SettlementArchiveLocation.SameFolder(folder, folder + Path.DirectorySeparatorChar)
                  && SettlementArchiveLocation.SameFolder(folder.ToUpperInvariant(), folder),
                "the comparison ignores a trailing separator and the case of the path");
            check(!SettlementArchiveLocation.SameFolder(folder, Path.Combine(root, "other")),
                "and two genuinely different folders are not confused");
        }
        finally { TryDelete(root); }
    }

    // `Results` is not an import source: the one-time legacy import only ever reads `CombatPanel-results`, so a copy folder
    // full of records is never read back into the archive (which is what would happen if the copies lived in the legacy folder).
    private static void TheCopyFolderIsNotAnImportSource(Action<bool, string> check)
    {
        string root = Scratch();
        try
        {
            string archive = Path.Combine(root, "persistent", SettlementArchiveLocation.ArchiveFolderName);
            string plugin = Path.Combine(root, "plugin");
            Directory.CreateDirectory(plugin);
            string mirror = SettlementArchiveLocation.MirrorArchiveFolder(plugin);
            Directory.CreateDirectory(mirror);
            string recordJson = "{\"Run\":1}";
            string name = "CombatPanel-results-20260112-100000-000-r1-R8-failure.json";
            SettlementRecordFiles.WriteCreateNew(archive, name, recordJson);
            var state = new SettlementMirror();
            state.BeginScreen();
            string copied = state.Create(archive, mirror, name, recordJson, out _);
            check(copied != null, "a copy exists in `Results`");

            // Nothing in the legacy folder: an import must copy nothing, and the archive must not gain a second file.
            var report = SettlementImportPolicy.ImportLegacyRecords(archive, SettlementArchiveLocation.LegacyArchiveFolder(plugin));
            check(report.Ran && report.Copied == 0,
                $"the copy in `Results` is not imported: the legacy folder is the only source ({report.Copied} copied)");
            check(Directory.GetFiles(archive, "*.json").Length == 1,
                "so the archive still holds exactly one record - the copy did not come back in as a second one");
            check(Directory.GetFiles(mirror, "*.json").Length == 1,
                "and the copy folder was not touched by the import either (no rename, no move)");

            // And a record that really is in the legacy folder is still imported exactly as it always was. A *fresh* archive is
            // used on purpose: the one-time marker now sits in the first archive, so a second import there would (correctly)
            // refuse to run at all and prove nothing about where the file came from.
            string legacy = SettlementArchiveLocation.LegacyArchiveFolder(plugin);
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "old-record.json"), "{\"Run\":0}");
            string freshArchive = Path.Combine(root, "second-persistent", SettlementArchiveLocation.ArchiveFolderName);
            var second = SettlementImportPolicy.ImportLegacyRecords(freshArchive, legacy);
            check(second.Ran && second.Copied == 1 && File.Exists(Path.Combine(freshArchive, "old-record.json")),
                "while a record that really is in the legacy folder is still imported once, as it always was");
            check(Directory.GetFiles(mirror, "*.json").Length == 1,
                "and the copy folder is still exactly as it was left - never read, never moved");
        }
        finally { TryDelete(root); }
    }

    // The package is a whitelist of six files. No `Results` folder, no record and no personal data may ever travel in it.
    private static void ThePackageWhitelistCarriesNoRecords(Action<bool, string> check)
    {
        string script = Source("scripts/Build-Package.ps1");
        check(script.Length > 0, "the packaging script is found");
        int list = script.IndexOf("$expected = @(", StringComparison.Ordinal);
        int end = script.IndexOf(")\r\n$check", StringComparison.Ordinal);
        if (end < 0) end = script.IndexOf(")\n$check", StringComparison.Ordinal);
        string whitelist = list < 0 || end < 0 ? string.Empty : script.Substring(list, end - list);
        check(whitelist.Length > 0, "the exact expected file list is found in it");
        foreach (string entry in new[] { "manifest.json", "README.md", "CHANGELOG.md", "icon.png" })
            check(whitelist.Contains("'" + entry + "'"), $"the whitelist still carries {entry}");
        check(whitelist.Contains("scoring.json") && whitelist.Contains("GTFOCombatPanel.dll"),
            "and the only two files from this build: its DLL and the default scoring file");
        check(whitelist.IndexOf("Results", StringComparison.Ordinal) < 0
              && whitelist.IndexOf("CombatPanel-results", StringComparison.Ordinal) < 0,
            "the whitelist contains no `Results` folder and no record folder, so a settlement record can never be packaged");
        check(!script.Contains("Copy-Item") && !script.Contains("Get-ChildItem"),
            "and the script copies nothing by directory walk: every entry is added by name");
    }

    // ---- wiring, read from the production source -----------------------------------------------

    private static void Wiring(Action<bool, string> check)
    {
        string saver = Source("ResultsSaver.cs");
        check(saver.Contains("private static readonly SettlementMirror Mirror = new SettlementMirror();"),
            "the saver owns one copy state machine, so one screen's copy cannot leak into the next");
        int record = saver.IndexOf("string path = SettlementRecordFiles.WriteCreateNew(folder, name, json);", StringComparison.Ordinal);
        int copy = saver.IndexOf("MirrorRecord(folder, name, json, path);", StringComparison.Ordinal);
        check(record > 0 && copy > record,
            "the record is written first and the copy after it, so a copy can never cost the record");
        check(saver.Contains("Mirror.BeginScreen();")
              && saver.Contains("_mirrorLogged = false; _mirrorRefreshLogged = false; _mirrorGoneLogged = false;"),
            "a new screen starts a fresh copy budget and fresh log latches - one line per kind, never one per repaint");
        check(saver.Contains("RefreshTheCopy(json, _lastPath);"),
            "a refresh of the same screen refreshes its copy from the same string");
        check(saver.Contains("Mirror.Create(archiveFolder, folder, requestedName, json, out string reason)")
              && saver.Contains("Mirror.Refresh(json, out bool gone)"),
            "and both halves go through the production type the suite above drives");

        string policy = Source("SettlementStorage.cs");
        check(policy.Contains("public const string MirrorFolderName = \"Results\";")
              && policy.Contains("SettlementArchiveLocation.SameFolder(archiveFolder, mirrorFolder)"),
            "`Results` is its own folder name, and the same-folder case is recognised before anything is written");
        check(policy.Contains("internal const int MaximumAttempts = 3;")
              && policy.Contains("internal bool CanRetry => !_gone && string.IsNullOrEmpty(_path) && _attempts < MaximumAttempts;"),
            "the copy budget is bounded per screen, and a copy that is gone is never retried");
        check(policy.Contains("if (!File.Exists(_path)) { _gone = true; gone = true; return false; }"),
            "a copy the player deleted is reported, not recreated");

        // The words a player reads, and the words the package ships.
        string data = Source("DATA-AVAILABILITY.md");
        string readme = Source("README.md");
        string packageReadme = Source("Thunderstore/README.md");
        check(data.Contains("Application.persistentDataPath") && data.Contains("Results\\"),
            "the data note names the API that really resolves the record's folder - not only a hard-coded LocalLow path - and names the copy folder beside the DLL");
        check(readme.Contains("Results\\") && packageReadme.Contains("Results\\"),
            "both READMEs tell the player where the copy is: next to this mod's DLL");
        check(packageReadme.IndexOf("## 更新日志", StringComparison.Ordinal) < 0
              && packageReadme.IndexOf("CHANGELOG 摘要", StringComparison.Ordinal) < 0
              && packageReadme.IndexOf("## Changelog", StringComparison.Ordinal) < 0,
            "and the packaged README carries no changelog section: the changelog stays in CHANGELOG.md");
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

    private static string Scratch()
    {
        string directory = Path.Combine(Path.GetTempPath(), "gtfocp-mirror-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void TryDelete(string directory)
    {
        try { if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch { }
    }
}
