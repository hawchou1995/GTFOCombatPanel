using System;
using System.IO;
using GameData;
using UnityEngine;

namespace HawChow.GTFOCombatPanel;

// Puts the settlement board on disk, one file per result screen. 1.19.23: the archive lives under the game's
// own persistent data path (Application.persistentDataPath - for GTFO, …\AppData\LocalLow\10 Chambers
// Collective\GTFO\CombatPanel-results), not next to this mod's DLL: r2modman and every package manager delete
// and re-extract the plugin folder on install/update, which is what wiped whole archives before. Records found
// in the old plugin-folder location are copied into the new root exactly once (marker-guarded) and nothing at
// the old location is ever deleted; the old folder is read-only to this mod from then on. Only when the
// persistent data path cannot be read or created at all does the archive fall back to the legacy folder, and the
// log says so. New records are written with create-new semantics and a numeric suffix on name collisions, so an
// existing record is never truncated or overwritten; nothing anywhere in this pipeline deletes or caps records.
internal static class ResultsSaver
{
    private static string _archiveFolder, _lastPath, _lastName;
    private static DateTime _writtenAt;
    private static bool _initialized, _importRan, _loggedLocation, _loggedFailure;
    private static readonly SettlementArchive Archive = new SettlementArchive();
    // The visible copy of the current screen's record, next to this mod's DLL (1.19.26), and the three per-screen latches that
    // keep its log to one line per kind however often the record is refreshed.
    private static readonly SettlementMirror Mirror = new SettlementMirror();
    private static bool _mirrorLogged, _mirrorRefreshLogged, _mirrorGoneLogged;

    // Resolves the archive location once and runs the one-time legacy import, so both facts are in the log from
    // the first launch even before the first settlement screen exists. Called from Plugin.Load; the heavy lifting
    // is the pure SettlementArchiveLocation / SettlementImportPolicy, which the smoke suite drives headlessly.
    internal static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            string folder = Folder(out bool persistentUsed, out string legacyArchive);
            if (string.IsNullOrEmpty(folder))
            {
                Warn("the settlement archive folder could not be resolved, so settlement records cannot be written");
                return;
            }
            LogLocationOnce(folder, persistentUsed);
            ImportLegacyOnce(folder, legacyArchive);
        }
        catch (Exception error)
        {
            Warn("the settlement archive root could not be prepared: " + error.Message);
        }
    }

    // One file per result screen, keyed by page and this session's run counter. Later accepted snapshots
    // refresh that screen's file in place only when the serialized board changes; its original timestamp
    // stays fixed, so polling cannot create duplicate files or needless writes. The run counter starts at 1
    // on every launch, so it is stable only inside a session - the file name carries the write time to the
    // millisecond, the run, the rundown/level key and success/failure, which is what makes the record's
    // identity stable across launches too.
    internal static void Save(Component page, bool succeeded, ImpactBoardLayout board, ImpactReport report, uint run, bool isMaster)
    {
        try
        {
            if (page == null) return;
            bool newScreen = Archive.ShouldWrite(page.Pointer, run);
            DateTime writtenAt = newScreen ? DateTime.Now : _writtenAt;
            ExpeditionMetadata metadata = CurrentMetadata();
            string json = ResultsLog.Build(succeeded, writtenAt, PluginInfo.Version, board, report, metadata, run);
            if (json.Length == 0) return;
            // 1.19.62: the round's created-enemy figure, printed from the same global ledger the page and this record
            // read - one line per expedition, never one per player. It sits beside the scan comparison for the same
            // reason that one does: this is where the settlement's final figures exist. Like it, this is logging only:
            // it never gates, delays or rewrites the record, and a machine that cannot prove a figure still writes
            // exactly the file it always wrote, with `spawns.scope = unknown`.
            string spawns = SettlementSpawns.Line(report, run);
            if (spawns.Length > 0) Plugin.Logger?.LogInfo(spawns);

            // 1.19.62: the host's own scan-batch estimate beside the figure each of those players reported, written
            // here because this is the one point where the settlement board's final rows exist. `ScanRoundComparison`
            // prints at most one line per player, prints it again only when the numbers on it change, and asks the
            // ledger directly - so the line survives the owner's figure arriving, which is the comparison it exists
            // for. `isMaster` keeps a migrated machine from printing the previous host's figure. This loop is logging
            // only: it never gates, delays or rewrites the record below, so a client or a host that grouped nothing
            // writes exactly the file it always wrote.
            foreach (string comparison in ScanRoundComparison.Lines(report, run, isMaster)) Plugin.Logger?.LogInfo(comparison);
            if (newScreen)
            {
                string folder = Folder(out bool persistentUsed, out string legacyArchive);
                if (string.IsNullOrEmpty(folder)) { Warn("the settlement archive folder could not be resolved, so the settlement file was not written"); return; }
                LogLocationOnce(folder, persistentUsed);
                ImportLegacyOnce(folder, legacyArchive);
                System.IO.Directory.CreateDirectory(folder);
                string name = SettlementRecordNamer.BuildFileName(writtenAt, run, metadata.Rundown, metadata.LevelName, succeeded);
                // Create-new plus a numeric suffix on collision: an existing record is never truncated or
                // overwritten, however close together two screens of this session are saved.
                string path = SettlementRecordFiles.WriteCreateNew(folder, name, json);
                if (path == null) { Warn("the settlement file could not be written"); return; }
                _lastPath = path;
                _lastName = name;
                _writtenAt = writtenAt;
                Archive.MarkWritten(page.Pointer, run, json);
                Plugin.Logger?.LogInfo($"[DIAG] settlement saved: {path} ({(succeeded ? "success" : "failure")}, {report.Entries.Count} players, run {run}, {name}).");
                // The visible copy next to this mod's DLL, from the same string, after the record is safely on disk. A new
                // screen starts a fresh copy budget and a fresh set of log latches.
                Mirror.BeginScreen();
                _mirrorLogged = false; _mirrorRefreshLogged = false; _mirrorGoneLogged = false;
                MirrorRecord(folder, name, json, path);
                return;
            }

            if (!Archive.ShouldRefresh(page.Pointer, run, json)) return;
            if (string.IsNullOrEmpty(_lastPath) || !File.Exists(_lastPath))
            {
                Warn("the current settlement file could not be found for an in-place data refresh");
                return;
            }
            // The session's own current record is refreshed atomically (temp + move); a different record is
            // never the target of this write.
            SettlementRecordFiles.ReplaceAtomically(_lastPath, json);
            Archive.MarkWritten(page.Pointer, run, json);
            Plugin.Logger?.LogInfo($"[DIAG] settlement refreshed: {_lastPath} ({report.Entries.Count} players, run {run}); newly accepted rows changed the board data.");
            // The copy follows the record from the same string; a copy that could not be created earlier gets its one
            // bounded retry here, and one the player deleted is left deleted.
            RefreshTheCopy(json, _lastPath);
        }
        catch (Exception error)
        {
            Warn("the settlement file could not be written: " + error.Message);
        }
    }

    // 1.19.26: the visible copy of the record, written next to this mod's DLL. The record above stays the authority - it lives
    // under the game's own persistent data path, which a package install or update never touches - and this copy exists only so
    // the player can find the file in the folder their mod manager shows them. Both are written from the same string, so they
    // cannot drift, and the copy is written only after the record is safely on disk.
    //
    // Every failure here is caught and reported once per screen with the record's own path on the line: a read-only plugin
    // folder may cost the copy, never the record, and never a line per repaint.
    private static void MirrorRecord(string archiveFolder, string requestedName, string json, string recordPath)
    {
        try
        {
            string folder = SettlementArchiveLocation.MirrorArchiveFolder(Plugin.PluginDirectory());
            string copy = Mirror.Create(archiveFolder, folder, requestedName, json, out string reason);
            if (copy != null)
            {
                Plugin.Logger?.LogInfo($"[DIAG] settlement copy: {copy} (the same bytes as the record at {recordPath}; this is the copy next to this mod's DLL, and the record is the one an install or update never touches).");
                return;
            }
            if (_mirrorLogged) return;
            _mirrorLogged = true;
            Plugin.Logger?.LogWarning($"[DIAG] the visible copy of this settlement record was not written ({reason}); the record itself is at {recordPath} and is unaffected.");
        }
        catch (Exception error)
        {
            if (_mirrorLogged) return;
            _mirrorLogged = true;
            Plugin.Logger?.LogWarning($"[DIAG] the visible copy of this settlement record could not be prepared ({error.Message}); the record itself is at {recordPath} and is unaffected.");
        }
    }

    // The record's own data changed on a later pass, so the copy is refreshed from the same string - or, when the copy could
    // not be created the first time, one bounded retry is spent here. A copy the player deleted is reported once and never
    // recreated: it is their folder, not ours.
    private static void RefreshTheCopy(string json, string recordPath)
    {
        try
        {
            string archiveFolder = Folder(out _, out _);
            if (Mirror.CanRetry) { MirrorRecord(archiveFolder, _lastName, json, recordPath); return; }
            if (!Mirror.HasCopy) return;
            if (Mirror.Refresh(json, out bool gone))
            {
                if (_mirrorRefreshLogged) return;
                _mirrorRefreshLogged = true;
                Plugin.Logger?.LogInfo($"[DIAG] settlement copy refreshed: {Mirror.Path} (the same bytes as the record at {recordPath}).");
                return;
            }
            if (!gone || _mirrorGoneLogged) return;
            _mirrorGoneLogged = true;
            Plugin.Logger?.LogInfo($"[DIAG] the settlement copy {Mirror.Path} is gone - it was removed outside this mod, so it is not written again; the record itself is at {recordPath}.");
        }
        catch (Exception error)
        {
            if (_mirrorRefreshLogged) return;
            _mirrorRefreshLogged = true;
            Plugin.Logger?.LogWarning($"[DIAG] the settlement copy could not be refreshed ({error.Message}); the record at {recordPath} is unaffected.");
        }
    }

    // The archive folder, resolved once per process. The persistent data path is read in a try/catch exactly
    // once; the legacy plugin folder is used only when that path is genuinely unusable.
    private static string Folder(out bool persistentUsed, out string legacyArchive)
    {
        if (_archiveFolder == null)
        {
            string persistent = null;
            try { persistent = Application.persistentDataPath; } catch { }
            _archiveFolder = SettlementArchiveLocation.ResolveArchiveFolder(persistent, Plugin.PluginDirectory(), out persistentUsed);
        }
        else persistentUsed = _usedPersistent;
        legacyArchive = SettlementArchiveLocation.LegacyArchiveFolder(Plugin.PluginDirectory());
        return _archiveFolder;
    }
    private static bool _usedPersistent;

    private static void LogLocationOnce(string folder, bool persistentUsed)
    {
        if (_loggedLocation) return;
        _loggedLocation = true;
        _usedPersistent = persistentUsed;
        if (persistentUsed)
            Plugin.Logger?.LogInfo($"[DIAG] settlement records are stored under the game's own persistent data path in {folder}; one file per result screen, refreshed in place as accepted squad rows arrive. A package install or update never touches this folder, and this mod never deletes anything in it.");
        else
            Plugin.Logger?.LogWarning($"[DIAG] the game's persistent data path could not be used on this machine, so settlement records fall back to the old folder next to this mod's DLL in {folder}; that folder is deleted and re-extracted by every package install or update, so records stored there are not safe from the very defect this fallback is trying to avoid.");
    }

    // One scan per process, marker-guarded: the policy's own marker is on disk, so even a second launch of the
    // mod does not copy twice. The count is logged in the policy's own line.
    private static void ImportLegacyOnce(string folder, string legacyArchive)
    {
        if (_importRan) return;
        _importRan = true;
        LegacyImportReport imported = SettlementImportPolicy.ImportLegacyRecords(folder, legacyArchive);
        if (imported.Ran && !string.IsNullOrEmpty(imported.Message)) Plugin.Logger?.LogInfo(imported.Message);
    }

    private static ExpeditionMetadata CurrentMetadata()
    {
        try
        {
            var active = RundownManager.ActiveExpedition;
            var descriptive = active?.Descriptive;
            return ExpeditionMetadata.Create(RundownKey(), descriptive?.Prefix, descriptive?.PublicName);
        }
        catch { return default; }
    }

    // The game exposes a local progressive rundown only as the synthetic key RundownManager.GetLocalRundownKey
    // builds ("Local_<id>"), and its readable name exists only on the RundownDataBlock the game actually
    // loaded. That loaded block is the authority here, so the name is read from the running game instead of
    // from a fixed table: a missing block, a failed read or an empty name keeps the key exactly as it was, and
    // a non-local key never reaches the lookup at all.
    private static string RundownKey()
        => RundownKeyResolver.Resolve(RundownManager.ActiveRundownKey, LocalRundownName);

    private static string LocalRundownName(uint id)
    {
        if (!RundownDataBlock.HasBlock(id)) return null;
        return RundownDataBlock.GetBlockName(id);
    }

    private static void Warn(string message)
    {
        if (_loggedFailure) return;
        _loggedFailure = true;
        Plugin.Logger?.LogWarning("[DIAG] " + message + " (the panel itself is unaffected and keeps drawing).");
    }
}
