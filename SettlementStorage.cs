using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace HawChow.GTFOCombatPanel;

// The pure managed half of settlement record storage: where the archive lives, what its files are called,
// how a new record is written without ever truncating one that exists, how the legacy plugin-folder records
// are imported exactly once, and where the editable scoring file is read from. No Unity, no BepInEx, no
// game assemblies - every rule here is asserted by the headless smoke suite and only the thin callers in
// ResultsSaver / Plugin carry Unity values (Application.persistentDataPath, the plugin folder) into it.
//
// The one promise everything below exists to keep: a settlement record or an edited scoring configuration is
// lost only when the player deletes it. Nothing in this file deletes, prunes, rotates or caps anything.

// Where the settlement archive lives. The root is the game's own persistent data path; the plugin folder is
// only a fallback for a machine on which that path cannot be used at all, because r2modman and every package
// manager delete and re-extract the plugin folder on install/update.
public static class SettlementArchiveLocation
{
    public const string ArchiveFolderName = "CombatPanel-results";

    // The archive folder, and whether the persistent path (rather than the legacy plugin folder) answered.
    public static string ResolveArchiveFolder(string persistentDataPath, string pluginFolder, out bool persistentUsed)
    {
        string root = ResolveRoot(persistentDataPath, pluginFolder, out persistentUsed);
        return root == null ? null : Path.Combine(root, ArchiveFolderName);
    }

    // The pre-1.19.23 location, next to the mod's DLL. Kept only as the one-time import source: TheArchive re-imports it
    // whenever the rundown changes, so nothing may write settlement records there.
    public static string LegacyArchiveFolder(string pluginFolder)
        => string.IsNullOrEmpty(pluginFolder) ? null : Path.Combine(pluginFolder, ArchiveFolderName);

    // The visible copy of every record, next to the mod's own DLL (1.19.26). Deliberately *not* `ArchiveFolderName`: that
    // folder is the one-time import source above, and writing our own copies into it would feed them back into the archive.
    // This folder is never scanned, never imported from and never back-filled.
    public const string MirrorFolderName = "Results";

    public static string MirrorArchiveFolder(string pluginFolder)
        => string.IsNullOrEmpty(pluginFolder) ? null : Path.Combine(pluginFolder, MirrorFolderName);

    // Whether two paths really name the same directory. Trailing separators are ignored, so a caller that has one and a
    // resolver that has not still agree; anything that cannot be made into a full path is "not the same folder", which keeps
    // a broken location from silently suppressing the copy.
    public static bool SameFolder(string first, string second)
    {
        if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(second)) return false;
        try
        {
            char[] trim = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
            return string.Equals(Path.GetFullPath(first).TrimEnd(trim), Path.GetFullPath(second).TrimEnd(trim),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // A path is usable as the archive root when it either already is a directory or can be created as one.
    // Anything else - an empty value, a file sitting where the directory should be, a denied create - falls
    // back to the legacy folder, and the caller says so in the log.
    public static bool UsableDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            if (Directory.Exists(path)) return true;
            Directory.CreateDirectory(path);
            return Directory.Exists(path);
        }
        catch { return false; }
    }

    private static string ResolveRoot(string persistentDataPath, string pluginFolder, out bool persistentUsed)
    {
        persistentUsed = true;
        if (UsableDirectory(persistentDataPath)) return Path.GetFullPath(persistentDataPath.Trim());
        persistentUsed = false;
        return string.IsNullOrEmpty(pluginFolder) ? null : pluginFolder;
    }
}

// The one-time import from the legacy plugin folder. The marker lives inside the archive folder itself, so the
// decision of whether the import has run is on disk rather than in process memory: two launches of the mod never
// copy twice, and a player who empties the archive folder (marker included) while the legacy folder still holds
// records gets them imported again on the next launch. Nothing at the source is ever deleted, moved or renamed.
public static class SettlementImportPolicy
{
    public const string MarkerFileName = ".legacy-import-completed";

    public static LegacyImportReport ImportLegacyRecords(string archiveFolder, string legacyArchiveFolder)
    {
        if (string.IsNullOrEmpty(archiveFolder))
            return new LegacyImportReport { Copied = 0, Ran = false, Message = null };
        try
        {
            Directory.CreateDirectory(archiveFolder);
            string marker = Path.Combine(archiveFolder, MarkerFileName);
            if (File.Exists(marker))
                return new LegacyImportReport { Copied = 0, Ran = false, Message = null };
            int copied = 0;
            try
            {
                if (!string.IsNullOrEmpty(legacyArchiveFolder) && Directory.Exists(legacyArchiveFolder)
                    && !SameFolder(archiveFolder, legacyArchiveFolder))
                {
                    foreach (string file in Directory.GetFiles(legacyArchiveFolder, "*.json"))
                    {
                        try
                        {
                            SettlementRecordFiles.CopyCreateNew(file, Path.Combine(archiveFolder, Path.GetFileName(file)));
                            copied++;
                        }
                        catch { /* One unreadable record never blocks the rest of the import. */ }
                    }
                }
                try
                {
                    File.WriteAllText(marker, "The legacy plugin-folder records were imported once into this archive. " +
                        "The old folder is read-only to this mod from now on; nothing here is ever deleted automatically." + Environment.NewLine);
                }
                catch { /* The marker could not be written; a later launch will simply scan and import again. */ }
            }
            catch { }
            return new LegacyImportReport
            {
                Copied = copied,
                Ran = true,
                Message = "[DIAG] legacy settlement records: " + copied + " record(s) were copied once from "
                    + (legacyArchiveFolder ?? "(no legacy folder)") + " into " + archiveFolder
                    + "; the old folder was left untouched and is read-only to this mod from here on; settlement records are never deleted by this mod."
            };
        }
        catch { return new LegacyImportReport { Copied = 0, Ran = false, Message = null }; }
    }

    private static bool SameFolder(string first, string second)
    {
        try { return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}

public sealed class LegacyImportReport
{
    public int Copied;
    // True when a scan really ran this call (the marker was absent). The marker, not process memory, is what
    // makes the import happen once: this flag just tells the caller whether there is a line to log.
    public bool Ran;
    // The one log line carrying the imported count.
    public string Message;
}

// The record's file name. It carries the write time to the millisecond, the session's run counter, the
// rundown/level key (sanitised for the file system and length-capped) and success/failure, so two screens that
// happen in the same second still get different names, and so can never be produced twice. The format round-trips
// through TryParse, which is what the tests and the collision suffix both rely on.
public static class SettlementRecordNamer
{
    public const string Prefix = "CombatPanel-results-";
    public const int LevelKeyLimit = 40;
    private const string IllegalFileNameCharacters = "<>:\"/\\|?*";

    public static string BuildFileName(DateTime writtenAt, uint run, string rundown, string levelName, bool succeeded)
    {
        DateTime stamp = TruncateToMilliseconds(writtenAt);
        return Prefix
            + stamp.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
            + "-r" + run.ToString(CultureInfo.InvariantCulture)
            + "-" + LevelKey(rundown, levelName)
            + "-" + (succeeded ? "success" : "failure")
            + ".json";
    }

    // The part of the name that identifies the level: runt down key and visible name, sanitised so the name is
    // legal on every file system this mod runs on and parseable again (no '-' survives, so the grammar above is
    // unambiguous), then capped so a long custom name can never push a path past a real limit.
    public static string LevelKey(string rundown, string levelName)
    {
        string key = Sanitize(string.IsNullOrEmpty(rundown) ? levelName : rundown + "_" + levelName);
        if (key.Length > LevelKeyLimit) key = key.Substring(0, LevelKeyLimit).TrimEnd('_', ' ');
        return key.Length == 0 ? "unknown" : key;
    }

    public static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var text = new StringBuilder(value.Length);
        bool pendingSpace = false;
        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c)) { if (!pendingSpace) text.Append(' '); pendingSpace = true; continue; }
            pendingSpace = false;
            if (c < '\u0020' || c == '-' || IllegalFileNameCharacters.IndexOf(c) >= 0) { text.Append('_'); continue; }
            text.Append(c);
        }
        return text.ToString().Trim().TrimEnd('.');
    }

    public static bool TryParse(string fileName, out DateTime writtenAt, out uint run, out string levelKey, out bool succeeded)
    {
        writtenAt = default; run = 0u; levelKey = null; succeeded = false;
        if (string.IsNullOrEmpty(fileName)) return false;
        string stem = fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? fileName.Substring(0, fileName.Length - ".json".Length) : fileName;
        if (!stem.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        string[] parts = stem.Substring(Prefix.Length).Split('-');
        // yyyyMMdd, HHmmss, fff, r<n>, <level key>, success|failure
        if (parts.Length != 6) return false;
        if (parts[1].Length != 6 || parts[2].Length != 3) return false;
        if (!DateTime.TryParseExact(parts[0] + parts[1] + parts[2], "yyyyMMddHHmmssfff",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out writtenAt)) return false;
        if (!parts[3].StartsWith("r", StringComparison.Ordinal)
            || !uint.TryParse(parts[3].Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out run)) return false;
        levelKey = parts[4];
        if (parts[5] == "success") succeeded = true;
        else if (parts[5] != "failure") return false;
        return true;
    }

    private static DateTime TruncateToMilliseconds(DateTime when)
        => when.AddTicks(-(when.Ticks % TimeSpan.TicksPerMillisecond));
}

// The two writes a record ever receives. A new record is written with create-new semantics: if the requested
// name exists the write gets a numeric suffix, and the existing file is never truncated or replaced. The in-place
// refresh of the session's own current record lands in a temp file first and is moved over in one step, so a
// crash mid-write can never leave a half file. No delete call exists anywhere in this type.
public static class SettlementRecordFiles
{
    public const int MaximumSuffixedCopies = 1024;

    // Returns the path that was really written, which may carry a numeric suffix.
    public static string WriteCreateNew(string folder, string requestedName, string content)
    {
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(requestedName)) return null;
        Directory.CreateDirectory(folder);
        string basePath = Path.Combine(folder, requestedName);
        string candidate = basePath;
        for (int suffix = 2; suffix <= MaximumSuffixedCopies; suffix++)
        {
            try
            {
                using (var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    writer.Write(content ?? string.Empty);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // An existing record is never overwritten: the next write takes the next free suffix.
                candidate = SuffixedPath(basePath, suffix);
            }
        }
        throw new IOException("No free file name was left for a settlement record; existing records were not touched.");
    }

    // Copies a legacy record into the archive without ever overwriting a file that is already there.
    public static void CopyCreateNew(string sourceFile, string targetPath)
    {
        string candidate = targetPath;
        int suffix = 2;
        while (File.Exists(candidate))
        {
            if (suffix > MaximumSuffixedCopies) throw new IOException("No free file name was left to import a legacy record; the existing records were not touched.");
            candidate = SuffixedPath(targetPath, suffix++);
        }
        File.Copy(sourceFile, candidate);
    }

    // The session's own current record may be refreshed in place. The refresh is an atomic replace of that one
    // file (temp + move), never a delete-and-recreate, and never a different record being overwritten.
    public static void ReplaceAtomically(string existingPath, string content)
    {
        if (string.IsNullOrEmpty(existingPath) || !File.Exists(existingPath))
            throw new FileNotFoundException("The current settlement record is gone.", existingPath);
        string temp = existingPath + ".tmp";
        File.WriteAllText(temp, content ?? string.Empty, new UTF8Encoding(false));
        File.Move(temp, existingPath, true);
    }

    // "name.json" -> "name-2.json" and so on; the suffix sits before the extension.
    public static string SuffixedPath(string basePath, int suffix)
    {
        string extension = Path.GetExtension(basePath);
        string stem = extension.Length == 0 ? basePath : basePath.Substring(0, basePath.Length - extension.Length);
        return stem + "-" + suffix.ToString(CultureInfo.InvariantCulture) + extension;
    }
}

// The visible copy of one settlement record, written next to the mod's own DLL (1.19.26).
//
// Why a copy at all: the record itself lives under the game's persistent data path, which no package manager touches, and that
// is what keeps it safe across installs and updates - but it also sits three folders deep inside AppData, where a player who
// wants to send the file to someone cannot find it. The copy lives in `<plugin folder>\Results\`, the folder the mod manager
// shows them, and holds exactly the same bytes.
//
// What it is *not*: the legacy `CombatPanel-results` folder next to the DLL. That one is the one-time import source of
// `SettlementImportPolicy` - and TheArchive re-imports it whenever the rundown changes - so writing records there would feed
// our own copies back into the archive. `Results` is never scanned, never imported from and never back-filled.
//
// The rules this type keeps, every one of them asserted against a real temp directory:
//   * one copy per result screen (page + run), created with the record and refreshed with it, always from the same string;
//   * an existing file is never adopted and never overwritten, whatever it holds - even a byte-identical older file with the
//     very same name. A copy is only ever written create-new, and a collision takes the next numeric suffix, so the path that
//     was really created is the only file this object ever refreshes afterwards;
//   * a copy the player deleted is not recreated: it is reported once and then left alone;
//   * failures are bounded: at most `MaximumAttempts` creations per screen, so a read-only install costs three attempts and
//     silence, not one per repaint;
//   * nothing here deletes, moves, rotates or caps anything, and no junction or symlink is ever followed.
internal sealed class SettlementMirror
{
    internal const int MaximumAttempts = 3;

    private string _path;
    private int _attempts;
    private bool _gone;

    // The copy this screen created, or null.
    internal string Path => _path;
    // Whether a copy exists that later refreshes may rewrite.
    internal bool HasCopy => !_gone && !string.IsNullOrEmpty(_path);
    // Whether another creation attempt is still allowed for this screen: never for a copy that is gone, and never past the
    // bounded budget - which is what keeps a read-only folder from being retried on every repaint.
    internal bool CanRetry => !_gone && string.IsNullOrEmpty(_path) && _attempts < MaximumAttempts;

    // A new result screen (page + run): the previous screen's copy is no longer this object's business. The attempt budget
    // belongs to the screen, so a new screen starts it again.
    internal void BeginScreen()
    {
        _path = null;
        _attempts = 0;
        _gone = false;
    }

    // Writes the copy of a record this session has just written. `json` must be the very string the record was written with,
    // so the two files cannot drift. Answers the path that was really created, or null with `reason` set for the one line the
    // caller may print. Never throws: a copy may not be able to break the record.
    internal string Create(string archiveFolder, string mirrorFolder, string requestedName, string json, out string reason)
    {
        reason = string.Empty;
        if (SettlementArchiveLocation.SameFolder(archiveFolder, mirrorFolder))
        {
            // The record already lives in this very folder: that is the fallback layout on a machine whose persistent path is
            // unusable. A second file with the same bytes would be a duplicate of the record, not a copy of it.
            _gone = true;
            reason = "the record folder and the copy folder are the same directory, so no second file was written";
            return null;
        }
        if (_attempts >= MaximumAttempts)
        {
            reason = "the " + MaximumAttempts + " attempts this screen is allowed have been spent";
            return null;
        }
        // Every failure below spends one of the bounded attempts - including "the folder or the name could not be read", so a
        // broken location is not re-asked on every later refresh for the rest of the level.
        _attempts++;
        if (string.IsNullOrEmpty(mirrorFolder))
        {
            reason = "this mod's own folder could not be read, so the copy has no place to go";
            return null;
        }
        if (string.IsNullOrEmpty(requestedName))
        {
            reason = "the record has no file name to copy";
            return null;
        }
        try
        {
            // Create-new with a numeric suffix on collision: an existing file - the player's own, or one of ours from an
            // earlier launch - is never truncated, renamed or claimed as this screen's copy.
            string path = SettlementRecordFiles.WriteCreateNew(mirrorFolder, requestedName, json);
            if (string.IsNullOrEmpty(path))
            {
                reason = "the copy could not be written";
                return null;
            }
            _path = path;
            _gone = false;
            return path;
        }
        catch (Exception error)
        {
            reason = "the copy could not be written: " + error.Message;
            return null;
        }
    }

    // Rewrites the copy this screen created, from the same string the record was just refreshed with. Answers false when there
    // is nothing to refresh; `gone` says whether the copy is missing (removed outside this mod) rather than simply absent.
    internal bool Refresh(string json, out bool gone)
    {
        gone = false;
        if (string.IsNullOrEmpty(_path)) return false;
        if (_gone) { gone = true; return false; }
        try
        {
            if (!File.Exists(_path)) { _gone = true; gone = true; return false; }
            SettlementRecordFiles.ReplaceAtomically(_path, json);
            return true;
        }
        catch (FileNotFoundException) { _gone = true; gone = true; return false; }
        catch { return false; }
    }
}

// Where the editable scoring file is read from. Precedence, in order: the player's copy in the stable root wins;
// when the root has none, the plugin folder's copy is imported once and from then on *is* the player's copy (a
// legacy file that differs from the shipped default is exactly the case this protects - it is kept, never
// overwritten by the freshly extracted package default); when an import throws, the legacy file stays the source
// in use and the import is retried on the next launch; and only when nothing exists anywhere does this build write
// its shipped defaults to the stable root, so a package upgrade can never replace them either.
public static class ScoringFileLocation
{
    public static ScoringFilePlan Resolve(string persistentDataPath, string pluginFolder)
    {
        string root = ResolveRoot(persistentDataPath, pluginFolder, out bool persistentUsed);
        string stable = root == null ? null : Path.Combine(root, ScoringFiles.DirectoryName, ScoringFiles.FileName);
        string legacy = string.IsNullOrEmpty(pluginFolder)
            ? null : Path.Combine(pluginFolder, ScoringFiles.DirectoryName, ScoringFiles.FileName);
        if (!persistentUsed)
            return new ScoringFilePlan
            {
                Path = stable,
                ImportedFromLegacy = false,
                Message = "[DIAG] scoring file: the persistent data path is unavailable on this machine, so the file falls back to the plugin folder: " + stable + "."
            };
        if (stable != null && File.Exists(stable))
            return new ScoringFilePlan
            {
                Path = stable,
                ImportedFromLegacy = false,
                Message = "[DIAG] scoring file: " + stable + " (this is the player's own copy in the game's persistent-data location; a package install or update never replaces it)."
            };
        if (legacy != null && !SameFile(stable, legacy) && File.Exists(legacy))
        {
            // A failed import must not fall through to the "write the shipped defaults to the stable root" plan
            // below: the stable root is read first from then on, and a defaults file written out of a failed import
            // would shadow the player's edited legacy file for ever. On failure the legacy file stays the source in
            // use - the next launch still finds no stable copy and retries the import - and the one bounded line
            // says so.
            try
            {
                string directory = Path.GetDirectoryName(stable);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                SettlementRecordFiles.CopyCreateNew(legacy, stable);
                return new ScoringFilePlan
                {
                    Path = stable,
                    ImportedFromLegacy = true,
                    Message = "[DIAG] scoring file: the plugin folder carried " + legacy + " and it was imported once into " + stable
                        + "; it is kept as the player's copy from now on - whatever it contained, the freshly extracted package default never replaces it."
                };
            }
            catch (Exception error)
            {
                return new ScoringFilePlan
                {
                    Path = legacy,
                    ImportedFromLegacy = false,
                    Message = "[DIAG] scoring file: the legacy copy at " + legacy + " could not be imported (" + error.Message
                        + ") and was left untouched; it is still the file in use - no defaults are written, and the import is retried on the next launch."
                };
            }
        }
        return new ScoringFilePlan
        {
            Path = stable,
            ImportedFromLegacy = false,
            Message = "[DIAG] scoring file: no scoring file existed anywhere, so this build's shipped defaults are written once to the game's persistent-data location: " + stable
                + " - a package install or update never replaces an existing file there."
        };
    }

    private static string ResolveRoot(string persistentDataPath, string pluginFolder, out bool persistentUsed)
    {
        persistentUsed = true;
        if (SettlementArchiveLocation.UsableDirectory(persistentDataPath))
            return Path.GetFullPath(persistentDataPath.Trim());
        persistentUsed = false;
        return string.IsNullOrEmpty(pluginFolder) ? null : pluginFolder;
    }

    private static bool SameFile(string first, string second)
    {
        try { return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}

public sealed class ScoringFilePlan
{
    // The file the runtime reads: the stable copy in every ordinary case. When a one-time import from the legacy
    // plugin folder throws, this is the legacy file itself - the player's edits keep applying and the next launch
    // retries the import instead of letting default writes shadow them.
    public string Path;
    public bool ImportedFromLegacy;
    // The one log line stating plainly which file was used and why.
    public string Message;
}
