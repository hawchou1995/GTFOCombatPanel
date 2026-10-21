using System;
using System.IO;
using BepInEx.Configuration;

namespace HawChow.GTFOCombatPanel;

// BepInEx 6 ships no config file watcher, so this plugin watches its own .cfg and only arms a gate.
// The Unity main thread performs the actual reload, which keeps every Unity object touch on the main
// thread. The debounce lives in ReloadGate, and both the .cfg and the scoring file arm the same one,
// so two edits landing together still produce a single reload.
internal static class ConfigWatcher
{
    private static readonly ReloadGate Gate = new ReloadGate();
    private static FileSystemWatcher _watcher;
    private static FileSystemWatcher _scoringWatcher;
    private static string _ownSavedConfig, _scoringAtSave;

    internal static string WatchedPath { get; private set; }
    internal static string WatchedScoringPath { get; private set; }
    internal static bool Watching { get { return _watcher != null; } }

    internal static void Watch(ConfigFile config, string extraFile)
    {
        try
        {
            try { _watcher?.Dispose(); } catch { }
            try { _scoringWatcher?.Dispose(); } catch { }
            _watcher = null; _scoringWatcher = null;
            string path = config != null ? config.ConfigFilePath : null;
            if (string.IsNullOrEmpty(path)) return;
            string directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;
            WatchedPath = path;
            _watcher = new FileSystemWatcher(directory, Path.GetFileName(path));
            _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime;
            FileSystemEventHandler changed = delegate { Gate.Arm(DateTime.UtcNow.Ticks); };
            _watcher.Changed += changed;
            _watcher.Created += changed;
            _watcher.Deleted += changed;
            _watcher.Renamed += delegate { Gate.Arm(DateTime.UtcNow.Ticks); };
            _watcher.EnableRaisingEvents = true;
            // The scoring file lives in its own sub folder beside the DLL, so it cannot be picked up by the
            // watcher above; it is watched here with the same debounce and the same gate, which is what makes
            // an edit to it take effect without a restart.
            string scoringDirectory = string.IsNullOrEmpty(extraFile) ? null : Path.GetDirectoryName(extraFile);
            if (!string.IsNullOrEmpty(scoringDirectory) && Directory.Exists(scoringDirectory))
            {
                WatchedScoringPath = extraFile;
                _scoringWatcher = new FileSystemWatcher(scoringDirectory, Path.GetFileName(extraFile));
                _scoringWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime;
                _scoringWatcher.Changed += changed;
                _scoringWatcher.Created += changed;
                _scoringWatcher.Deleted += changed;
                _scoringWatcher.Renamed += delegate { Gate.Arm(DateTime.UtcNow.Ticks); };
                _scoringWatcher.EnableRaisingEvents = true;
            }
            _scoringAtSave = ReadScoring();
        }
        catch (Exception)
        {
            _watcher = null;
            _scoringWatcher = null;
        }
    }

    // Compare contents, not a time window: a real edit made immediately after F8 must still reload.
    internal static void RecordOwnSave()
    {
        try
        {
            _ownSavedConfig = File.ReadAllText(WatchedPath);
        }
        catch { _ownSavedConfig = null; }
    }

    private static string ReadScoring()
        => !string.IsNullOrEmpty(WatchedScoringPath) && File.Exists(WatchedScoringPath)
            ? File.ReadAllText(WatchedScoringPath) : null;

    internal static bool ConsumeReload()
    {
        if (!Gate.Consume(DateTime.UtcNow.Ticks)) return false;
        // This snapshot is the last scoring content handed to the reload path, not whatever
        // happens to be on disk when F8 saves; a pending scoring edit must not be swallowed.
        try
        {
            string scoring = ReadScoring();
            if (_ownSavedConfig != null && File.ReadAllText(WatchedPath) == _ownSavedConfig && scoring == _scoringAtSave)
                return false;
            _scoringAtSave = scoring;
        }
        catch { /* A deleted or unreadable file is a real change, not a self-save echo. */ }
        _ownSavedConfig = null;
        return true;
    }
}
