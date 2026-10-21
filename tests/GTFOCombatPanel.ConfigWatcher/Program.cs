using BepInEx.Configuration;
using HawChow.GTFOCombatPanel;

string scratch = Environment.GetEnvironmentVariable("PI_SCRATCH_DIR") ?? Path.GetTempPath();
string root = Path.Combine(scratch, "combatpanel-watcher-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string configPath = Path.Combine(root, "panel.cfg"), scoring = Path.Combine(root, "scoring.json");
File.WriteAllText(configPath, "[Panel]\nShowPanel = true\n");
File.WriteAllText(scoring, "{\"weight\":1}");
var config = new ConfigFile(configPath, false);
ConfigWatcher.Watch(config, scoring);
int assertions = 0;
void Check(bool value, string label)
{
    if (!value) throw new Exception("FAIL: " + label);
    assertions++;
    Console.WriteLine("PASS: " + label);
}
bool Settle()
{
    // Let the real OS watcher deliver its notifications, then let the production debounce expire.
    Thread.Sleep(750);
    return ConfigWatcher.ConsumeReload();
}
Check(ConfigWatcher.Watching, "production watcher armed with real BepInEx ConfigFile");
File.WriteAllText(configPath, "[Panel]\nShowPanel = false\n");
ConfigWatcher.RecordOwnSave();
Check(!Settle(), "F8 self-save does not trigger layout reload");
File.WriteAllText(configPath, "[Panel]\nShowPanel = true\n");
Check(Settle(), "manual cfg edit immediately after own save reloads");
Check(!ConfigWatcher.ConsumeReload(), "one settled cfg edit reloads once");
File.WriteAllText(configPath, "[Panel]\nShowPanel = false\n");
ConfigWatcher.RecordOwnSave();
Check(!Settle(), "subsequent self-save remains suppressed");
File.WriteAllText(scoring, "{\"weight\":2}");
ConfigWatcher.RecordOwnSave();
Check(Settle(), "pending JSON edit survives a concurrent F8 save");
ConfigWatcher.RecordOwnSave();
File.WriteAllText(configPath, "[Panel]\nShowPanel = false\n");
Check(!Settle(), "duplicate own-save notifications stay suppressed");
File.WriteAllText(scoring, "{\"weight\":3}");
Check(Settle(), "external JSON edit reloads normally");
File.Delete(configPath);
Check(Settle(), "deleted cfg is not mistaken for a self-save");
Console.WriteLine($"PASS: {assertions} config watcher assertions; fixture retained at {root}");
