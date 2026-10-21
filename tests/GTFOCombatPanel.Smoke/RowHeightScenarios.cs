using System;
using HawChow.GTFOCombatPanel;

// A row shorter than the face's own line height gets its glyphs clipped top and bottom, and the
// default density shipped in exactly that state: compact scaled RowFactor (1.45) by 0.80 into
// 1.16 em while the label clipped to its row. These pins keep every density above the floor.
internal static partial class Program
{
    private static void RunRowHeightScenarios()
    {
        var densities = new[] { PanelDensity.Compact, PanelDensity.Normal, PanelDensity.Spacious };
        foreach (PanelDensity density in densities)
        {
            foreach (int fontSize in new[] { 6, 11, 15, 17, 24, 41, 68 })
            {
                PanelGrid grid = PanelGeometry.Build(new[] { 10f }, fontSize, 4, density);
                float em = grid.RowHeight / grid.FontPixels;
                Check(em >= PanelGeometry.MinimumRowEm - 0.0001f,
                    $"{density.Name} at {fontSize}px keeps a row at least as tall as the face's line ({em:0.###} em)");
            }
        }
        // The published defect, stated as a number so it cannot come back quietly.
        PanelGrid compact = PanelGeometry.Build(new[] { 10f }, 15, 22, PanelDensity.Compact);
        float legacyEm = PanelGeometry.RowFactor * PanelDensity.Compact.Row;
        Check(legacyEm < 1.2f, "compact's pre-fix row was 1.16 em of its glyph size, which is why text was clipped");
        Check(compact.RowHeight > 15 * legacyEm,
            "compact rows are taller than the clipped composite they used to be");
        // The floor sits above RowFactor, so normal is nudged up to it; spacious keeps its own proportion.
        PanelGrid normal = PanelGeometry.Build(new[] { 10f }, 17, 22, PanelDensity.Normal);
        Check(Math.Abs(normal.RowHeight - 17 * PanelGeometry.MinimumRowEm) < 0.001f,
            "normal density lands on the floor, the tallest face's line, and no longer accepts a clipped row");
        PanelGrid spacious = PanelGeometry.Build(new[] { 10f }, 17, 22, PanelDensity.Spacious);
        Check(spacious.RowHeight > normal.RowHeight,
            "spacious stays visibly roomier than normal");
        // A row still has to hold its own frame: the floor is a multiplier of the glyph size, so shrinking
        // the table for a narrow box shrinks the row with it instead of pinning a pixel height.
        PanelGrid shrunk = PanelGeometry.Build(new[] { 10f }, 20, 22, PanelDensity.Compact, 0.5f);
        Check(shrunk.RowHeight <= compact.RowHeight && shrunk.FontPixels < 15,
            "the floor follows the glyph size, so a shrunk table keeps proportionally shorter rows");
        Check(shrunk.RowHeight / shrunk.FontPixels >= PanelGeometry.MinimumRowEm - 0.0001f,
            "a shrunk table still keeps every row above the floor");
    }
}
