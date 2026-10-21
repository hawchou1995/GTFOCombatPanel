using System;

namespace HawChow.GTFOCombatPanel;

// The two stable text values stored with a settlement: the rundown key and the visible expedition name.
// `Prefix` is separate in the GTFO data model, so composing it here keeps custom levels such as
// `FX6 Circle` intact without tying the JSON formatter to Unity or game assemblies.
public readonly struct ExpeditionMetadata
{
    public readonly string Rundown;
    public readonly string LevelName;

    private ExpeditionMetadata(string rundown, string levelName)
    {
        Rundown = Clean(rundown);
        LevelName = ComposeLevelName(string.Empty, levelName);
    }

    public static ExpeditionMetadata Create(string rundown, string prefix, string publicName)
        => new ExpeditionMetadata(rundown, ComposeLevelName(prefix, publicName));

    public static string ComposeLevelName(string prefix, string publicName)
    {
        string head = Clean(prefix);
        string name = Clean(publicName);
        if (head.Length == 0) return name;
        if (name.Length == 0) return head;
        if (name.Equals(head, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(head + " ", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(head + ":", StringComparison.OrdinalIgnoreCase)) return name;
        return head + " " + name;
    }

    private static string Clean(string value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
