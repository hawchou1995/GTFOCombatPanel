using System;
using System.Collections.Generic;

namespace HawChow.GTFOCombatPanel;

// A rectangle in the local units of whatever hierarchy the table lives in. Nothing in the
// mod converts to screen pixels any more: the table is laid out in the same local space as
// the native HUD elements beside it, which is what the reference mods do and why they render
// exactly where they say they do.
public readonly struct LayoutRect
{
    public readonly float X, Y, Width, Height;
    public float Right => X + Width;
    public float Bottom => Y + Height;
    public LayoutRect(float x, float y, float width, float height) { X = x; Y = y; Width = width; Height = height; }
    public bool Overlaps(LayoutRect other, float gap = 0) => X < other.Right + gap && Right > other.X - gap && Y < other.Bottom + gap && Bottom > other.Y - gap;
}

// Which table columns are drawn, in display order. The player name is the row label
// and is therefore always present; every data column is optional.
public static class PanelColumns
{
    public static List<PanelColumn> Compose(bool damage, bool friendlyFire, bool accuracy, bool weakspot, bool projectiles)
    {
        var columns = new List<PanelColumn> { PanelColumn.PlayerName };
        if (damage) columns.Add(PanelColumn.DamageAndShare);
        if (friendlyFire) columns.Add(PanelColumn.FriendlyFire);
        if (accuracy) columns.Add(PanelColumn.Accuracy);
        if (weakspot) columns.Add(PanelColumn.WeakspotAccuracy);
        if (projectiles) columns.Add(PanelColumn.Projectiles);
        return columns;
    }
}

// Table tightness. Compact is the default so the panel occupies as little of the
// screen as it can while staying readable. Row scales the row height, Column scales the
// inner cell padding, Gap scales the space between columns, Font scales the glyph size.
public readonly struct PanelDensity
{
    public readonly string Name;
    public readonly float Row, Column, Gap, Font;
    public PanelDensity(string name, float row, float column, float gap, float font)
    { Name = name; Row = row; Column = column; Gap = gap; Font = font; }
    public static readonly PanelDensity Compact = new PanelDensity("compact", 0.80f, 0.88f, 0.50f, 0.90f);
    public static readonly PanelDensity Normal = new PanelDensity("normal", 1.00f, 1.00f, 1.00f, 1.00f);
    public static readonly PanelDensity Spacious = new PanelDensity("spacious", 1.30f, 1.12f, 1.25f, 1.10f);
    public static string Normalize(string value) => FromName(value).Name;
    public static PanelDensity FromName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Compact;
        string candidate = value.Trim().ToLowerInvariant();
        if (candidate.StartsWith("n")) return Normal;
        if (candidate.StartsWith("s") || candidate.StartsWith("l")) return Spacious;
        return Compact;
    }
}

// A resolved table, expressed in the local units of the hierarchy it is placed in. Every
// number comes from the font the table will actually use, so the grid is scale free: the
// same code works next to the flashlight icon and on a results page, whichever units those
// use.
// One column's field widths. A merged column (damage/share, pellets total/hit/weakspot) is stored
// as one entry per field, so every field can be right-aligned in its own slot
// instead of the whole group being pushed around by the widest row.
public sealed class PanelColumnMetrics
{
    public readonly float[] FieldWidths;
    public PanelColumnMetrics(float[] fieldWidths)
    {
        FieldWidths = fieldWidths == null || fieldWidths.Length == 0 ? new[] { 0f } : fieldWidths;
    }
    public float Total(float separatorWidth)
    {
        float total = 0;
        for (int index = 0; index < FieldWidths.Length; index++) total += FieldWidths[index];
        return total + separatorWidth * Math.Max(0, FieldWidths.Length - 1);
    }
}

public static class PanelAlignmentPolicy
{
    // In a settlement board, the label column is left aligned and every player/data column shares its right edge.
    // The in-level table keeps its established all-right-aligned layout.
    public static bool IsRightAligned(bool settlementTable, bool columnRightAligned)
        => !settlementTable || columnRightAligned;
}

public sealed class PanelGrid
{
    public readonly float[] CellWidths;
    public readonly float[][] FieldWidths;
    public readonly float SeparatorWidth;
    public readonly float Gap, RowHeight, FontPixels;
    public readonly int Rows;
    public readonly PanelDensity Density;
    // Which columns hug their right edge. A board of figures reads down a common right edge; a label
    // or a name reads from the left. A column that was not told otherwise keeps the older behaviour,
    // which is centred in its own box.
    public readonly bool[] RightAligned;

    public PanelGrid(float[] cellWidths, float[][] fieldWidths, float separatorWidth, float gap, float rowHeight, float fontPixels, int rows, PanelDensity density, bool[] rightAligned = null)
    {
        CellWidths = cellWidths ?? Array.Empty<float>();
        FieldWidths = fieldWidths ?? Array.Empty<float[]>();
        SeparatorWidth = separatorWidth;
        Gap = gap; RowHeight = rowHeight; FontPixels = fontPixels;
        Rows = Math.Max(1, rows); Density = density;
        RightAligned = rightAligned;
    }

    public int Columns => CellWidths.Length;

    public float Width
    {
        get
        {
            float width = 0;
            for (int i = 0; i < CellWidths.Length; i++) width += CellWidths[i];
            return width + Gap * Math.Max(0, CellWidths.Length - 1);
        }
    }

    public float Height => RowHeight * Rows;

    // Distance from the table's left edge to the left edge of one column.
    public float ColumnOffset(int column)
    {
        if (column <= 0) return 0;
        if (column >= CellWidths.Length) column = CellWidths.Length - 1;
        float x = 0;
        for (int i = 0; i < column; i++) x += CellWidths[i] + Gap;
        return x;
    }

    // One cell box, measured from the table's top left corner. Cells of a column are identical
    // in every row, which is what makes a header line up with the values underneath it.
    public LayoutRect Cell(int row, int column)
        => new LayoutRect(ColumnOffset(column), row * RowHeight, column >= 0 && column < CellWidths.Length ? CellWidths[column] : 0, RowHeight);

    // How many fields one column holds (1 unless its values sit side by side).
    public int FieldCount(int column)
        => FieldWidths == null || column < 0 || column >= FieldWidths.Length ? 1 : Math.Max(1, FieldWidths[column].Length);

    // A row with fewer fields than its column shows them in the right-most slots, so a lone
    // placeholder lands under the last field instead of dragging the group out of line.
    public int SlotFor(int column, int fieldCount, int field)
        => Math.Max(0, FieldCount(column) - fieldCount + field);

    // The box of one field. A field group is centred in its column, so the padding a column carries
    // is split evenly instead of being dumped after the last value, and every field keeps its own
    // fixed slot across rows.
    public LayoutRect FieldRect(int row, int column, int slot)
    {
        if (FieldWidths == null || column < 0 || column >= FieldWidths.Length) return Cell(row, column);
        var fields = FieldWidths[column];
        if (slot < 0) slot = 0;
        if (slot >= fields.Length) slot = fields.Length - 1;
        float x = ColumnOffset(column) + GroupInset(column);
        for (int index = 0; index < slot; index++) x += fields[index] + SeparatorWidth;
        return new LayoutRect(x, row * RowHeight, fields[slot], RowHeight);
    }

    public LayoutRect FieldRectAligned(int row, int column, int fieldCount, int field)
        => FieldRect(row, column, SlotFor(column, fieldCount, field));


    // Space left over in a column once its fields are placed. A right-aligned column keeps all of it on
    // the left, so its figures share one right edge with the column above and below them; any other
    // column keeps the older centred placement.
    private float GroupInset(int column)
    {
        var fields = FieldWidths[column];
        float group = SeparatorWidth * Math.Max(0, fields.Length - 1);
        for (int index = 0; index < fields.Length; index++) group += fields[index];
        float leftover = Math.Max(0f, CellWidths[column] - group);
        return IsRightAligned(column) ? leftover : leftover * 0.5f;
    }

    public bool IsRightAligned(int column)
        => RightAligned != null && column >= 0 && column < RightAligned.Length && RightAligned[column];

    // Largest uniform shrink that makes the table fit a box, so a table that outgrows its host
    // is scaled down instead of being clipped.
    public float FitFactor(float maxWidth, float maxHeight)
    {
        float factor = 1f;
        if (Width > 0 && maxWidth > 0) factor = Math.Min(factor, maxWidth / Width);
        if (Height > 0 && maxHeight > 0) factor = Math.Min(factor, maxHeight / Height);
        return factor < PanelGeometry.MinimumShrink ? PanelGeometry.MinimumShrink : factor > 1f ? 1f : factor;
    }
}

public static class PanelGeometry
{
    // Everything below is a multiple of the glyph size, so the table keeps its proportions at
    // any font size instead of needing a resolution dependent scale.
    public const float RowFactor = 1.45f;
    // A glyph is not an em. The face GTFO ships, and every CJK fallback behind it, draws taller than
    // its nominal size, and `HudTable` clips a label to its own row - so a row shorter than the face's
    // line height cuts the top and the bottom off every character. That is exactly what compact did:
    // RowFactor times its 0.80 row scale is 1.16 em, which shipped as the default and clipped the text.
    // 1.50 clears the tallest face in play: Noto Sans SC's hhea metrics are 1.48 em (ascender 1160 plus
    // descender 320 over a 1000 em), and that is the CJK face GTFO's SDF asset actually hands us.
    public const float MinimumRowEm = 1.50f;
    // The whitespace the eye reads between two neighbouring columns is `2*PaddingFactor*Column +
    // GapFactor*Gap` ems of the glyph size. The settlement board is width-bound at four player columns,
    // and every em spent here is an em the glyph size cannot have - which is why the same table read
    // "columns too far apart, text too small". Cell padding carries most of that distance, so it moved
    // the most: 0.625em before 1.8.2, 0.625em at 1.8.2, and 0.436em now (0.20/0.22 at compact).
    public const float GapFactor = 0.22f;
    public const float PaddingFactor = 0.20f;
    public const float MinimumShrink = 0.45f;
    // Space reserved between the fields of a merged column, as a fraction of the glyph size. It is
    // what holds `112 41 10` apart now that no slash is drawn between the values. 1.8.2 left this
    // factor alone deliberately: the user called the pellet triplet's spacing about right, so it
    // is the one distance in the table that must not move.
    public const float FieldGapFactor = 0.60f;

    // Width of one line of text at a given glyph size. The runtime measures the real font
    // asset; this estimate keeps the geometry well defined when no font is available (headless
    // tests, very early startup) and is always taken as a lower bound.
    public static float EstimateTextWidth(string text, float fontSizePixels)
    {
        if (string.IsNullOrEmpty(text) || !(fontSizePixels > 0)) return 0;
        float width = 0;
        for (int index = 0; index < text.Length; index++) width += CharacterWidth(text[index]);
        return width * fontSizePixels;
    }

    // The smallest a column may become, expressed in ems so it follows the font. A floor keeps
    // the table from reflowing every time a counter gains a digit.
    public static float MinimumWidth(PanelColumn column, float fontSizePixels) => MinimumEm(column) * fontSizePixels;

    // Space reserved between two fields of a merged column.
    public static float FieldGap(float fontPixels, PanelDensity density)
        => Math.Max(0f, fontPixels) * FieldGapFactor * density.Gap;

    // Widest cell of one column, never below the column floor.
    public static float ColumnContentWidth(string[] cells, PanelColumn column, float fontSizePixels)
    {
        float width = MinimumWidth(column, fontSizePixels);
        if (cells != null) for (int index = 0; index < cells.Length; index++) width = Math.Max(width, EstimateTextWidth(cells[index], fontSizePixels));
        return width;
    }

    // Resolves the grid from per-column field widths: the column is as wide as its fields plus the
    // separators between them, and each field keeps its own slot so the numbers stay in line.
    public static PanelGrid Build(IReadOnlyList<PanelColumnMetrics> columns, float separatorWidth, float fontPixels, int rows, PanelDensity density, float fontShrink = 1f, bool[] rightAligned = null, float minimumRowEm = MinimumRowEm)
    {
        float shrink = Clamp(fontShrink, MinimumShrink, 1f);
        float font = Math.Max(0, fontPixels) * shrink;
        int count = columns != null ? columns.Count : 0;
        var cellWidths = new float[count];
        var fieldWidths = new float[count][];
        float padding = 2 * font * PaddingFactor * density.Column;
        for (int column = 0; column < count; column++)
        {
            var source = columns[column].FieldWidths;
            var scaled = new float[source.Length];
            for (int index = 0; index < source.Length; index++) scaled[index] = Math.Max(0, source[index]) * shrink;
            fieldWidths[column] = scaled;
            cellWidths[column] = columns[column].Total(separatorWidth) * shrink + padding;
        }
        // The row must be at least as tall as the face's own line, whatever the density scales say.
        float rowHeight = Math.Max(font * RowFactor * density.Row, font * Math.Max(1f, minimumRowEm));
        return new PanelGrid(cellWidths, fieldWidths, separatorWidth * shrink,
            font * GapFactor * density.Gap, rowHeight, font, rows, density, rightAligned);
    }
    // Single-field columns only; kept so the simple case stays simple and testable.
    public static PanelGrid Build(IReadOnlyList<float> contentWidths, float fontPixels, int rows, PanelDensity density, float fontShrink = 1f, float minimumRowEm = MinimumRowEm)
    {
        int count = contentWidths != null ? contentWidths.Count : 0;
        var metrics = new PanelColumnMetrics[count];
        for (int index = 0; index < count; index++) metrics[index] = new PanelColumnMetrics(new[] { contentWidths[index] });
        return Build(metrics, 0f, fontPixels, rows, density, fontShrink, null, minimumRowEm);
    }

    // Fraction of an em that one glyph occupies. Digits and punctuation are the common case in
    // this panel, CJK players and headers supply the wide case.
    private static float CharacterWidth(char glyph)
    {
        if (IsWide(glyph)) return 1f;
        if (glyph >= '0' && glyph <= '9') return 0.58f;
        if (glyph >= 'A' && glyph <= 'Z') return 0.66f;
        if (glyph >= 'a' && glyph <= 'z') return 0.55f;
        switch (glyph)
        {
            case ' ': return 0.30f;
            case '.': case ',': case ':': case ';': case '\'': case '|': return 0.28f;
            case '/': case '\\': case '(': case ')': case '[': case ']': return 0.34f;
            case '%': return 0.66f;
            case '~': case '-': case '+': case '=': case '<': case '>': return 0.42f;
            case '#': return 0.60f;
            default: return 0.56f;
        }
    }

    private static bool IsWide(char glyph)
        => glyph >= 0x1100 && (glyph <= 0x115F || glyph == 0x2010 || glyph == 0x2013 || glyph == 0x2014 || glyph == 0x2018
            || glyph == 0x2019 || glyph == 0x201C || glyph == 0x201D || glyph == 0x2026 || glyph == 0x203C || glyph == 0x2049
            || glyph == 0x2190 || glyph == 0x2191 || glyph == 0x2192 || glyph == 0x2193 || glyph == 0x2460 || glyph == 0x2469
            || (glyph >= 0x2E80 && glyph <= 0x303E) || (glyph >= 0x3041 && glyph <= 0x33FF) || (glyph >= 0x3400 && glyph <= 0x4DBF)
            || (glyph >= 0x4E00 && glyph <= 0x9FFF) || (glyph >= 0xA000 && glyph <= 0xA4CF) || (glyph >= 0xAC00 && glyph <= 0xD7A3)
            || (glyph >= 0xF900 && glyph <= 0xFAFF) || (glyph >= 0xFE30 && glyph <= 0xFE6F) || (glyph >= 0xFF00 && glyph <= 0xFF60)
            || (glyph >= 0xFFE0 && glyph <= 0xFFE6));

    // Column floors in ems. They cover the widest value each column can normally print, so the
    // table keeps its shape while a match is running. A floor is only a minimum: a column still
    // grows to whatever its widest cell measures, so lowering one can never truncate a value.
    // 1.8.2 lowered the four floors whose header is the widest thing their column ever holds
    // (friendly fire `友伤`, accuracy `命中率`, weakspot `弱点率`, and the damage/share pair), which
    // is what removes the dead space a short header used to leave inside its own slot. The pellet
    // column's floor is untouched: its three fields must keep the spacing the user approved.
    private static float MinimumEm(PanelColumn column)
    {
        switch (column)
        {
            case PanelColumn.PlayerName: return 5.4f;
            case PanelColumn.DamageAndShare: return 6.0f;
            case PanelColumn.FriendlyFire: return 2.65f;
            case PanelColumn.Accuracy: return 3.05f;
            case PanelColumn.WeakspotAccuracy: return 3.05f;
            case PanelColumn.Projectiles: return 6.8f;
            default: return 4.0f;
        }
    }

    private static float Clamp(float value, float low, float high) => value < low ? low : value > high ? high : value;
}

// Failure results never overlap their vanilla heading, even with an old negative offset.
public static class ResultsPlacement
{
    public static float Top(float headingBottom, float offsetY, float gap = 16f)
        => Math.Max(0f, headingBottom + gap) + Math.Max(0f, float.IsFinite(offsetY) ? offsetY : 0f);

    public static bool TryFit(float screenWidth, float screenHeight, float top, float width, float height, float offsetX, out LayoutRect rect)
    {
        rect = default;
        if (!float.IsFinite(top) || !float.IsFinite(width) || !float.IsFinite(height) ||
            top < 0 || width <= 0 || height <= 0 || width > screenWidth || top + height > screenHeight - 8f) return false;
        float x = (screenWidth - width) * 0.5f + (float.IsFinite(offsetX) ? offsetX : 0f);
        x = Math.Max(0f, Math.Min(x, screenWidth - width));
        rect = new LayoutRect(x, top, width, height);
        return true;
    }

    // Fit a table into the strip between two measured edges of a result page: the one safe strip both
    // result screens use. A table that cannot fit is refused rather than pushed over the button the player
    // has to press. `slack` is the tolerance the caller allows on the strip's bottom edge: the pages
    // animate their own buttons in and a button's renderer bounds move by a pixel or two while it is
    // hovered, so a board measured against one frame can come back a fraction of a pixel too tall for the
    // next. It is a caller decision, and it defaults to nothing, so a placement that has not measured a
    // moving edge keeps the exact rule it had.
    public static bool TryFitBetween(float screenWidth, float screenHeight, float stripTop, float stripBottom,
        float width, float height, float offsetX, out LayoutRect rect, float slack = 0f)
    {
        rect = default;
        if (!float.IsFinite(screenWidth) || !float.IsFinite(screenHeight) || !float.IsFinite(width) || !float.IsFinite(height)) return false;
        if (width <= 0f || height <= 0f || width > screenWidth || screenHeight <= 0f) return false;
        if (!float.IsFinite(stripTop) || !float.IsFinite(stripBottom)) return false;
        if (!float.IsFinite(slack) || slack < 0f) slack = 0f;
        float top = Math.Max(0f, stripTop);
        float bottom = Math.Min(stripBottom, screenHeight - 8f) + slack;
        if (!(bottom > top) || top + height > bottom) return false;
        float x = (screenWidth - width) * 0.5f + (float.IsFinite(offsetX) ? offsetX : 0f);
        rect = new LayoutRect(x, top, width, height);
        return true;
    }
}

// The strip of the success page the settlement board may use: below the expedition's completion
// section and above the button that sends the squad home. Both edges belong to the page, and the board
// is never allowed to cover either of them, so an edge that cannot be measured degrades to a fraction
// that leaves the completion section and the button alone rather than being guessed at.
public readonly struct ResultsSafeArea
{
    public readonly float Top, Bottom;
    public ResultsSafeArea(float top, float bottom) { Top = top; Bottom = bottom; }
    public float Height { get { return Bottom > Top ? Bottom - Top : 0f; } }
    public bool Fits { get { return Height > 0f; } }
}

public static class ResultsRegion
{
    // Used when the completion section cannot be measured: the fraction the success board used before
    // the region was measured, which sits below the page's heading band in the captured layouts.
    public const float FallbackTopFraction = 0.16f;
    // Where the strip stops when the page's buttons could not be read at all. It is deliberately a fraction
    // of the window rather than "the bottom minus a margin": a button this build failed to measure is
    // exactly the case where the board was drawn straight over it. The lowest footer edge measured on real
    // pages so far is 0.705 of the window (a 1440-high host fail page), so 0.66 clears every one of them -
    // including the 720-high window in the screenshot, whose own footer sits at 0.83.
    public const float FallbackBottomFraction = 0.66f;

    public static ResultsSafeArea Resolve(bool completionMeasured, float completionBottom,
        bool buttonMeasured, float buttonTop, float screenHeight, float gap)
    {
        if (!float.IsFinite(screenHeight) || screenHeight <= 0f) return default;
        if (!float.IsFinite(gap) || gap < 0f) gap = 0f;
        float top = completionMeasured && float.IsFinite(completionBottom)
            ? completionBottom + gap
            : screenHeight * FallbackTopFraction;
        float bottom = buttonMeasured && float.IsFinite(buttonTop)
            ? buttonTop - gap
            : screenHeight * FallbackBottomFraction;
        // No room between the two elements is no region at all. Pulling the top down onto the completion
        // section, or the bottom up onto the button, is precisely the overlap this exists to prevent.
        if (!(bottom > top)) return default;
        top = Math.Max(0f, top);
        bottom = Math.Min(screenHeight, bottom);
        return bottom > top ? new ResultsSafeArea(top, bottom) : default;
    }

    // The lowest bottom edge among the measured completion-section elements: the board has to clear
    // whichever of them the page actually drew, so the deepest one is the one that matters.
    public static float LowestBottom(float candidate, float best)
        => float.IsFinite(candidate) && (!float.IsFinite(best) || candidate > best) ? candidate : best;

    // The highest top edge among the measured footer elements, by the same reasoning.
    public static float HighestTop(float candidate, float best)
        => float.IsFinite(candidate) && (!float.IsFinite(best) || candidate < best) ? candidate : best;
}

// Whether a control of a result page is really on the screen, and therefore whether the board has to keep
// out of its way. Three kinds of object live in these pages without being part of them: a control the page
// switched off for this state (the host's own lobby button on a client's fail page), a control whose canvas
// group still holds it at zero alpha (the reveal the page animates with), and a control laid out past the
// edge of the window by a stale anchor. None of them is a button the player can press, so none of them may
// cost the board the height between the heading and the button that is really there.
public static class ResultsButtonPolicy
{
    // Alpha at or below this reads as "not drawn": the pages reveal their footers from 0, and a control the
    // player cannot see is not one they can press.
    public const float VisibleAlpha = 0.02f;

    public static bool Visible(bool activeInHierarchy, float alpha, float top, float bottom, float screenWidth, float screenHeight)
    {
        if (!activeInHierarchy) return false;
        if (!float.IsFinite(top) || !float.IsFinite(bottom) || !(bottom > top)) return false;
        // An alpha nobody could read is not evidence of transparency, so the control is kept.
        if (float.IsFinite(alpha) && alpha <= VisibleAlpha) return false;
        if (!float.IsFinite(screenHeight) || screenHeight <= 0f) return false;
        // Off the page: a control above the top edge or below the bottom edge belongs to no frame the
        // player is looking at, whatever anchor left it there.
        if (top >= screenHeight || bottom <= 0f) return false;
        return true;
    }

    // The highest top edge among the controls that are really there: the wall the board stays above.
    public static float HighestTop(bool activeInHierarchy, float alpha, float top, float bottom,
        float screenWidth, float screenHeight, float best)
        => Visible(activeInHierarchy, alpha, top, bottom, screenWidth, screenHeight)
            && (!float.IsFinite(best) || top < best) ? top : best;
}

// Native title visibility is independent of whether its screen-space rectangle can be read.
public static class ResultsHeading
{

    // The fraction of the window the vanilla failure composition puts the bottom of its own message at,
    // used only when nothing at all could be measured.
    public const float FallbackBottomFraction = 0.34f;
    public static bool Resolve(bool present, bool active, float alpha, bool measured, float bottom,
        float screenHeight, float offsetY, out float top, out float opacity)
    {
        top = 0f; opacity = 0f;
        if (!float.IsFinite(screenHeight) || screenHeight <= 0f) return false;
        if (present && (!active || float.IsFinite(alpha) && alpha <= 0.001f)) return false;
        opacity = present && float.IsFinite(alpha) ? Math.Min(1f, Math.Max(0f, alpha)) : 1f;
        bool sane = measured && float.IsFinite(bottom) && bottom >= 0f && bottom < screenHeight - 8f;
        // Explicit fallback for the vanilla failure composition; logged by the runtime, not claimed
        // to be a measured title edge. It lies below the title in the supplied 2560x1440 capture.
        top = ResultsPlacement.Top(sane ? bottom : screenHeight * FallbackBottomFraction, offsetY);
        return true;
    }
}

public static class ResultsAppearance
{
    public const float BackgroundOpacity = 0.50f;
    public const float RowBandOpacity = 0.020f;
    public const float RuleOpacity = 0.12f;
}

// How heavy the in-level table's own plate is. This is the one number the player sees as "the box my
// numbers sit on": the plate is deliberately darker than the game so a row of figures survives a bright
// scene, and how much of that scene shows through it is a matter of taste - so it is a setting rather than
// a constant. The settlement board keeps its own approved plate and is never affected by this key.
public static class PanelAppearance
{
    // The published default. 0.48 was read as "a black box" over the level, so the plate starts lighter
    // while staying dark enough to hold the numbers apart from what is behind it.
    public const float InLevelOpacity = 0.28f;
    // How strong the plate's hairline frame is relative to the plate itself. It is the ratio the two figures
    // already had (0.22 / 0.48), kept so that a lighter plate also gets a proportionally lighter frame
    // instead of turning into an outline drawn around nothing.
    public const float FrameRatio = 0.46f;

    // The settlement board's own plate, taken from `ResultsAppearance` rather than from the setting.
    public static float SettlementPlate => ResultsAppearance.BackgroundOpacity;

    // The plate's alpha for one draw. A number outside 0..1 - or a value that is not a number at all, which
    // is what an unreadable entry yields - falls back to the published default rather than to a transparent
    // plate, so a typo in the file can never leave the table drawn on nothing.
    public static float Plate(float configured, bool settlement)
    {
        if (settlement) return SettlementPlate;
        if (!float.IsFinite(configured)) return InLevelOpacity;
        return Math.Min(1f, Math.Max(0f, configured));
    }

    // The frame the plate is outlined with, scaled by the plate so the two always look like one object.
    public static float Frame(float plate)
    {
        float value = float.IsFinite(plate) ? Math.Min(1f, Math.Max(0f, plate)) : InLevelOpacity;
        return value * FrameRatio;
    }
}

// Where the failure page's own message has to end and where the board under it begins, so the two
// result screens carry their board in one and the same strip. The success page's completion section is
// measured while it is up and remembered here - the failure page has no such section, so its own
// message is moved until its bottom edge is that same line, and the board then starts the same gap
// under it: identical arithmetic to the success side, which is what "the same place" means. The
// remembered line is only ever reused at the window size it was measured at, and a page whose line was
// never seen keeps the message exactly where the game put it.
public static class FailureAlignment
{
    private static float _completionBottom = float.NaN;
    private static float _completionScreenHeight = float.NaN;

    public static bool AnchorKnown(float screenHeight)
        => float.IsFinite(_completionBottom) && float.IsFinite(_completionScreenHeight)
           && float.IsFinite(screenHeight) && screenHeight > 0f
           && Math.Abs(_completionScreenHeight - screenHeight) <= 1f;

    public static float Anchor { get { return _completionBottom; } }

    // Called by the success path with every completion-section measurement it makes.
    public static void RememberCompletion(float bottom, float screenHeight)
    {
        if (!float.IsFinite(bottom) || bottom < 0f || !float.IsFinite(screenHeight) || screenHeight <= 0f) return;
        _completionBottom = bottom;
        _completionScreenHeight = screenHeight;
    }

    public static void Forget() { _completionBottom = float.NaN; _completionScreenHeight = float.NaN; }

    // The line the failure page's message must end on, and the strip its board may use - produced by the
    // very same rule the success page uses, so the two boards stand in the same place. The message is
    // never pushed off the top of the page and never pushed down onto the buttons: a line that would do
    // either is refused and the message stays where the game put it.
    public static bool Resolve(bool messageMeasured, float messageBottom, float screenHeight, float offsetY, float gap,
        bool buttonMeasured, float buttonTop, out float alignedBottom, out ResultsSafeArea strip)
    {
        alignedBottom = float.NaN; strip = default;
        if (!float.IsFinite(screenHeight) || screenHeight <= 0f) return false;
        if (!float.IsFinite(gap) || gap < 0f) gap = 0f;
        bool anchored = AnchorKnown(screenHeight) && _completionBottom < screenHeight - 8f && _completionBottom >= screenHeight * 0.05f;
        if (anchored) alignedBottom = _completionBottom;
        else if (messageMeasured && float.IsFinite(messageBottom) && messageBottom >= 0f && messageBottom < screenHeight - 8f) alignedBottom = messageBottom;
        else alignedBottom = screenHeight * ResultsHeading.FallbackBottomFraction;
        strip = ResultsRegion.Resolve(true, alignedBottom, buttonMeasured, buttonTop, screenHeight, gap);
        if (!strip.Fits) return false;
        float top = ResultsPlacement.Top(alignedBottom, offsetY, gap);
        strip = new ResultsSafeArea(top, strip.Bottom);
        return strip.Fits;
    }
}
