using System.Collections.Generic;

namespace HawChow.GTFOCombatPanel;

public enum ResultRowKind { Header, Total, Weapon, Spacer }

// One line of the result table. Squad and Weapon index back into the squad list the plan was
// built from, so the caller can find the statistics a row describes.
public readonly struct ResultRow
{
    public readonly ResultRowKind Kind;
    public readonly string Label;
    public readonly int Squad;
    public readonly int Weapon;
    public ResultRow(ResultRowKind kind, string label, int squad, int weapon)
    { Kind = kind; Label = label ?? string.Empty; Squad = squad; Weapon = weapon; }
}

// Row plan for the success and failure screens: a header, then every player's own TOTAL row
// followed by one row per weapon, with a blank row between players so the squads stay
// readable. Both the combined and the per weapon numbers are therefore always on screen.
public static class ResultLayout
{
    public static List<ResultRow> Plan(string header, IReadOnlyList<string> names, IReadOnlyList<IReadOnlyList<string>> weapons)
    {
        var rows = new List<ResultRow> { new ResultRow(ResultRowKind.Header, header, -1, -1) };
        if (names == null) return rows;
        for (int squad = 0; squad < names.Count; squad++)
        {
            if (squad > 0) rows.Add(new ResultRow(ResultRowKind.Spacer, string.Empty, -1, -1));
            rows.Add(new ResultRow(ResultRowKind.Total, names[squad], squad, -1));
            if (weapons == null || squad >= weapons.Count || weapons[squad] == null) continue;
            IReadOnlyList<string> list = weapons[squad];
            for (int weapon = 0; weapon < list.Count; weapon++) rows.Add(new ResultRow(ResultRowKind.Weapon, list[weapon], squad, weapon));
        }
        return rows;
    }

    // Weapon rows are marked as sub entries of the player above them. Column one is right
    // aligned like every other column, so the mark is what shows the nesting.
    public static string WeaponLabel(string name) => "· " + (name ?? string.Empty);
}
