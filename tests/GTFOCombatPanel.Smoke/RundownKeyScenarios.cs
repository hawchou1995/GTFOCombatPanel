using System;
using System.Text.Json;
using HawChow.GTFOCombatPanel;

internal static partial class Program
{
    // A local progressive rundown reaches a mod only as the synthetic key RundownManager.GetLocalRundownKey
    // builds ("Local_<id>"); the readable name lives on the RundownDataBlock the game loaded. These cases pin
    // the rules with an injected resolver, so no game assembly is needed: the loaded name is used when there
    // is one, and the key survives untouched whenever there is not.
    private static void RunRundownKeyScenarios()
    {
        int lookups = 0;
        string resolved = RundownKeyResolver.Resolve("Local_2", id =>
        {
            lookups++;
            return id == 2 ? "Survival Rundown 2.0" : null;
        });
        Check(resolved == "Survival Rundown 2.0" && lookups == 1,
            "a local rundown key resolves to the name of the loaded data block");

        // The name is the block's own text, not this mod's copy of it: padding is trimmed, an interior
        // name a mod renamed itself is written as the game reports it.
        Check(RundownKeyResolver.Resolve("Local_2", _ => "  Survival Rundown 2.0  ") == "Survival Rundown 2.0",
            "the loaded block name is trimmed but never rewritten");
        Check(RundownKeyResolver.Resolve("Local_35", id => "Mod Rundown " + id) == "Mod Rundown 35",
            "the id handed to the resolver is the number the key carries");

        // Falling back means the key, never a guess. A block the game has not loaded, a blank name and a
        // resolver that throws all leave the settlement saying exactly what the game said.
        Check(RundownKeyResolver.Resolve("Local_2", _ => null) == "Local_2", "a missing data block keeps the key");
        Check(RundownKeyResolver.Resolve("Local_2", _ => string.Empty) == "Local_2", "an empty block name keeps the key");
        Check(RundownKeyResolver.Resolve("Local_2", _ => "   ") == "Local_2", "a blank block name keeps the key");
        Check(RundownKeyResolver.Resolve("Local_2", _ => throw new InvalidOperationException("block read failed")) == "Local_2",
            "a resolver that throws keeps the key instead of losing the settlement metadata");

        // Online and every other non-local key never consult the local registry at all: their text is the
        // value players already have in files written by earlier versions.
        int onlineLookups = 0;
        string online = RundownKeyResolver.Resolve("Online_Rundown_Data_A", _ =>
        {
            onlineLookups++;
            return "wrong";
        });
        Check(online == "Online_Rundown_Data_A" && onlineLookups == 0,
            "an online rundown key is written as-is and never reaches the local resolver");
        Check(RundownKeyResolver.Resolve("  Rundown_8_0  ", _ => { onlineLookups++; return "wrong"; }) == "Rundown_8_0"
            && onlineLookups == 0,
            "a non-local rundown key is only trimmed, exactly as before");

        // Only the exact "Local_<uint>" shape is a local id. Anything else keeps its text and is not looked up.
        int shapeLookups = 0;
        Func<uint, string> counter = _ => { shapeLookups++; return "wrong"; };
        Check(RundownKeyResolver.Resolve("Local_", counter) == "Local_"
            && RundownKeyResolver.Resolve("Local_x", counter) == "Local_x"
            && RundownKeyResolver.Resolve("Local_-1", counter) == "Local_-1"
            && RundownKeyResolver.Resolve("Local_2x", counter) == "Local_2x"
            && shapeLookups == 0,
            "an illegal local rundown id keeps the key and is never looked up");
        Check(RundownKeyResolver.Resolve(null, counter) == string.Empty
            && RundownKeyResolver.Resolve("   ", counter) == string.Empty
            && shapeLookups == 0,
            "a blank rundown key stays blank and is never looked up");
        Check(RundownKeyResolver.Resolve("Local_2", null) == "Local_2", "no resolver configured keeps the key");

        Check(RundownKeyResolver.TryReadLocalId("Local_2", out uint two) && two == 2
            && RundownKeyResolver.TryReadLocalId(" Local_35 ", out uint thirtyFive) && thirtyFive == 35
            && !RundownKeyResolver.TryReadLocalId("Online_Rundown_Data_A", out _)
            && !RundownKeyResolver.TryReadLocalId("Local_2x", out _),
            "the local rundown id is read only from the shape the game builds");

        // The rundown is the loaded block name; the visible level name keeps its own prefix + expedition
        // name composition, which this change does not touch.
        ExpeditionMetadata localMetadata = ExpeditionMetadata.Create(resolved, "FX.2", "Chaos");
        Check(localMetadata.Rundown == "Survival Rundown 2.0" && localMetadata.LevelName == "FX.2 Chaos",
            "the resolved rundown name and the composed expedition level name stay separate values");
        ExpeditionMetadata keyOnly = ExpeditionMetadata.Create(RundownKeyResolver.Resolve("Local_2", _ => null), "FX.2", "Chaos");
        Check(keyOnly.Rundown == "Local_2" && keyOnly.LevelName == "FX.2 Chaos",
            "a key kept as a fallback leaves the visible level name untouched");

        var input = new ImpactInput { Name = "Runner", Counters = new ImpactCounters() };
        ImpactReport report = ImpactScoring.Evaluate(new[] { input }, ImpactScoring.DefaultWeights, 1, 1);
        ImpactBoardLayout board = ImpactBoard.Build(report, true, true);
        using (JsonDocument json = JsonDocument.Parse(ResultsLog.Build(true, new DateTime(2026, 10, 24), "1.19.6", board, report, localMetadata)))
        {
            Check(json.RootElement.GetProperty("rundown").GetString() == "Survival Rundown 2.0"
                && json.RootElement.GetProperty("levelName").GetString() == "FX.2 Chaos",
                "the settlement file writes the resolved rundown name beside the unchanged level name");
        }
        using (JsonDocument json = JsonDocument.Parse(ResultsLog.Build(true, new DateTime(2026, 10, 24), "1.19.6", board, report,
            ExpeditionMetadata.Create("Rundown_8_0", "FX6", "Circle"))))
        {
            Check(json.RootElement.GetProperty("rundown").GetString() == "Rundown_8_0"
                && json.RootElement.GetProperty("levelName").GetString() == "FX6 Circle",
                "a non-local rundown keeps the rundown value earlier versions already wrote");
        }
    }
}
