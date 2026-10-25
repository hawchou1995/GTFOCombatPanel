using System;
using System.Globalization;

namespace HawChow.GTFOCombatPanel;

// The rundown text a settlement carries. The game does not name a local progressive rundown in the key it
// exposes to mods: RundownManager.GetLocalRundownKey(uint) builds the synthetic `Local_<id>`, and the
// readable name exists only on the RundownDataBlock the game actually loaded. This helper recognises that
// shape and asks the caller's resolver for the loaded block name; every other key - and every local key
// whose name cannot be read - is written exactly as the game gave it. Nothing here knows Unity or the game
// assemblies, so the rules stay testable without the game.
public static class RundownKeyResolver
{
    private const string LocalPrefix = "Local_";

    // `lookup` returns the loaded data block's name for a local rundown id, or null/empty when the game has
    // no such block. A resolver that throws is treated the same way: the key survives and no name is
    // invented, so a missing block can never be written as a rundown that was not loaded.
    public static string Resolve(string key, Func<uint, string> lookup)
    {
        string trimmed = Trim(key);
        if (lookup == null || trimmed.Length == 0) return trimmed;
        if (!TryReadLocalId(trimmed, out uint id)) return trimmed;
        try
        {
            string name = Trim(lookup(id));
            return name.Length == 0 ? trimmed : name;
        }
        catch { return trimmed; }
    }

    // True only for the exact shape RundownManager.GetLocalRundownKey(uint) produces. `Local_`, `Local_x`,
    // `Local_-1` and `Local_2x` are not local ids, so they are never looked up and keep their own text.
    public static bool TryReadLocalId(string key, out uint id)
    {
        id = 0;
        string trimmed = Trim(key);
        if (!trimmed.StartsWith(LocalPrefix, StringComparison.Ordinal)) return false;
        string digits = trimmed.Substring(LocalPrefix.Length);
        return digits.Length != 0
            && uint.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    private static string Trim(string value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
