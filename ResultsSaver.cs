using System;
using System.IO;
using System.Reflection;
using BepInEx;
using GameData;
using UnityEngine;

namespace HawChow.GTFOCombatPanel;

// Puts the settlement board on disk next to this mod's own DLL, one file per result screen. The path is the
// real location the assembly was loaded from, so it lands in the same folder whether the mod was installed by
// hand (BepInEx\plugins\GTFOCombatPanel) or managed by r2modman (…\HawChow-CombatPanel\GTFOCombatPanel);
// only when that cannot be read does it fall back to BepInEx's plugin root.
internal static class ResultsSaver
{
    private static string _directory, _lastPath;
    private static DateTime _writtenAt;
    private static bool _loggedDirectory, _loggedFailure;
    private static readonly SettlementArchive Archive = new SettlementArchive();

    internal static string Directory
    {
        get
        {
            if (_directory != null) return _directory;
            string resolved = null;
            try
            {
                var location = typeof(Plugin).Assembly.Location;
                if (!string.IsNullOrEmpty(location))
                {
                    string folder = Path.GetDirectoryName(location);
                    if (!string.IsNullOrEmpty(folder) && System.IO.Directory.Exists(folder)) resolved = folder;
                }
            }
            catch { }
            if (resolved == null)
            {
                try { resolved = Path.Combine(Paths.PluginPath, PluginInfo.Name); } catch { resolved = null; }
            }
            _directory = resolved;
            return _directory;
        }
    }

    // One file per result screen, keyed by page and this machine's run counter. Later accepted snapshots
    // rewrite that screen's file in place only when the serialized board changes; its original timestamp
    // stays fixed, so polling cannot create duplicate files or needless writes.
    internal static void Save(Component page, bool succeeded, ImpactBoardLayout board, ImpactReport report, uint run)
    {
        try
        {
            if (page == null) return;
            bool newScreen = Archive.ShouldWrite(page.Pointer, run);
            DateTime writtenAt = newScreen ? DateTime.Now : _writtenAt;
            string json = ResultsLog.Build(succeeded, writtenAt, PluginInfo.Version, board, report, CurrentMetadata(), run);
            if (json.Length == 0) return;

            if (newScreen)
            {
                string folder = Directory;
                if (string.IsNullOrEmpty(folder)) { Warn("the mod's own folder could not be resolved, so the settlement file was not written"); return; }
                string archive = Path.Combine(folder, "CombatPanel-results");
                System.IO.Directory.CreateDirectory(archive);
                string name = $"CombatPanel-results-{writtenAt:yyyyMMdd-HHmmss}.json";
                string path = Path.Combine(archive, name);
                File.WriteAllText(path, json, new System.Text.UTF8Encoding(false));
                _lastPath = path;
                _writtenAt = writtenAt;
                Archive.MarkWritten(page.Pointer, run, json);
                if (!_loggedDirectory)
                {
                    _loggedDirectory = true;
                    Plugin.Logger?.LogInfo($"[DIAG] settlement files are written next to this mod's DLL, in {folder}; one file per result screen, refreshed in place as accepted squad rows arrive.");
                }
                Plugin.Logger?.LogInfo($"[DIAG] settlement saved: {path} ({(succeeded ? "success" : "failure")}, {report.Entries.Count} players, run {run}).");
                return;
            }

            if (!Archive.ShouldRefresh(page.Pointer, run, json)) return;
            if (string.IsNullOrEmpty(_lastPath) || !File.Exists(_lastPath))
            {
                Warn("the current settlement file could not be found for an in-place data refresh");
                return;
            }
            File.WriteAllText(_lastPath, json, new System.Text.UTF8Encoding(false));
            Archive.MarkWritten(page.Pointer, run, json);
            Plugin.Logger?.LogInfo($"[DIAG] settlement refreshed: {_lastPath} ({report.Entries.Count} players, run {run}); newly accepted rows changed the board data.");
        }
        catch (Exception error)
        {
            Warn("the settlement file could not be written: " + error.Message);
        }
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
