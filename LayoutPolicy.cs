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

    // Column widths for one settlement board, from the width every column measured on its own.
    //
    // The row label is a column of its own: it keeps the width its content asked for, and never less than its
    // floor, because a label that changed width would push its text in from the plate's own left edge, and a
    // label that shared the figure width would move the figures with it. Every value column holds the same
    // kind of figure, so they are all given one width - the widest of their own measurements. That one width
    // is what makes `#1`..`#N` line up down the board, and it is a measurement of the figures rather than a
    // share of the page, so a wide page leaves the board at its own size instead of stretching it across.
    public static float[] SettlementColumnWidths(IReadOnlyList<PanelColumn> columns, IReadOnlyList<float> measuredWidths, float fontSizePixels)
    {
        int count = columns != null ? columns.Count : 0;
        var widths = new float[count];
        float labelWidth = 0f, valueWidth = 0f;
        for (int column = 0; column < count; column++)
        {
            float measured = measuredWidths != null && column < measuredWidths.Count ? measuredWidths[column] : 0f;
            if (columns[column] == PanelColumn.ImpactLabel) labelWidth = Math.Max(labelWidth, measured);
            else valueWidth = Math.Max(valueWidth, Math.Max(measured, MinimumWidth(columns[column], fontSizePixels)));
        }
        for (int column = 0; column < count; column++)
            widths[column] = columns[column] == PanelColumn.ImpactLabel
                ? Math.Max(labelWidth, MinimumWidth(PanelColumn.ImpactLabel, fontSizePixels))
                : valueWidth;
        return widths;
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
    // The lower bound of a result board's strip: the top edge of the button that sends the squad home,
    // and nothing else.
    //
    // `ButtonBand` answers for every button the page has drawn, which is what keeps a page this build has
    // never seen from being covered - but a result page also carries controls that are switched on, drawn
    // from the page's own animation and yet far above the button the player presses. Taking the highest of
    // them cost the board some 180px of strip on a real success page, which is why the board sat small with
    // a band of empty page above the footer. When the lobby button itself can be measured it is therefore
    // the only wall; the band is the fallback for a page whose button could not be read at all.
    //
    // A measured edge that is not a screen coordinate (not a number, zero, negative, past the bottom of the
    // window) is not evidence of anything and falls back to the band, so no NaN ever travels on from here.
    public static float LobbyEdge(bool lobbyMeasured, float lobbyTop, float bandTop, float screenHeight)
        => lobbyMeasured && float.IsFinite(lobbyTop) && lobbyTop > 0f && lobbyTop <= screenHeight
            ? lobbyTop
            : bandTop;

    // Which control a result board's lower bound came from, once the bound has been resolved.
    //
    // `Pressable` is the control the player can really press: the page drew it, switched it on and left it on
    // the frame. `Layout` is that same control's own laid-out rectangle while the page is still revealing its
    // footer from zero alpha - the button is where it will be pressed, it simply is not drawn yet, which is
    // why `ResultsButtonPolicy.Visible` quite rightly refuses it. `Band` is the drawn buttons of a page whose
    // own control could not be read by name, and `None` is a page that handed nothing measurable at all.
    public enum LobbyEdgeSource { None = 0, Pressable = 1, Layout = 2, Band = 3 }

    public readonly struct LobbyEdgeReading
    {
        public readonly float Top;
        public readonly LobbyEdgeSource Source;
        public LobbyEdgeReading(float top, LobbyEdgeSource source) { Top = top; Source = source; }
        // A wall the board has to stay above exists.
        public bool Measured { get { return Source != LobbyEdgeSource.None && float.IsFinite(Top); } }
        // That wall is a control the player can press right now.
        public bool Pressable { get { return Source == LobbyEdgeSource.Pressable; } }
        // That wall is the button's own geometry - drawn, or merely laid out - rather than the fallback band of
        // everything the page drew. This is the case a board may be measured against without waiting for the
        // page's reveal to finish, and the one whose height the readability floor is taken from.
        public bool FromButton { get { return Source == LobbyEdgeSource.Pressable || Source == LobbyEdgeSource.Layout; } }
    }

    // The lower bound of the strip, with the button's *pressability* and its *geometry* kept apart.
    //
    // The success page reveals its footer from zero alpha. During those frames the lobby button is already laid
    // out exactly where the player will press it, while the visibility test says "not a bound", and treating the
    // two answers as one is what cost the board 240px of a 1080-high page: the strip fell back to the historical
    // fraction of the window even though the button's own top edge was known, and a 30-row table could only be
    // drawn at 6px. Measuring a bound is therefore asked for in this order:
    //
    //   1. the control as the player can press it (the drawn rectangle), which is the tightest true edge;
    //   2. the same control's laid-out rectangle, which is the same edge before the reveal finished;
    //   3. the band of every button the page has drawn, for a page whose own control cannot be read by name;
    //   4. nothing, which leaves the caller on its own historical fallback - never permission to cover a button.
    //
    // `active` is the one gate in front of the first two: a control the page has switched off is not part of this
    // frame at all, and a rectangle it left behind is a stale guess rather than an edge - a page that re-lays its
    // footer out can keep the old rect on a deactivated object, and letting that win would bound the board against
    // a button nobody can press. Alpha is deliberately *not* part of the gate: a control that is laid out and still
    // fading in is exactly the case this exists for.
    //
    // Every answer is a coordinate on the page, so an edge that is not a number, sits at or above zero, or lies
    // past the bottom of the window is discarded rather than trusted.
    public static LobbyEdgeReading ResolveLobbyEdge(bool active, bool pressable, float pressableTop, bool layoutRead,
        float layoutTop, float bandTop, float screenHeight)
    {
        if (!float.IsFinite(screenHeight) || screenHeight <= 0f) return new LobbyEdgeReading(float.NaN, LobbyEdgeSource.None);
        if (active)
        {
            if (pressable && OnPage(pressableTop, screenHeight)) return new LobbyEdgeReading(pressableTop, LobbyEdgeSource.Pressable);
            if (layoutRead && OnPage(layoutTop, screenHeight)) return new LobbyEdgeReading(layoutTop, LobbyEdgeSource.Layout);
        }
        if (OnPage(bandTop, screenHeight)) return new LobbyEdgeReading(bandTop, LobbyEdgeSource.Band);
        return new LobbyEdgeReading(float.NaN, LobbyEdgeSource.None);
    }

    private static bool OnPage(float top, float screenHeight)
        => float.IsFinite(top) && top > 0f && top <= screenHeight;
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

// ---------------------------------------------------------------------------------------------------
// How large the settlement board's glyph should be.
//
// The board used to be laid out at the sharing size and then multiplied by one guess, which is why it sat
// small in the middle of a 2560x1440 page with most of the strip above the lobby button unused. Fitting it
// is a measurement problem rather than a guess: the table is measured at a candidate glyph size, the strip
// between the completion section and the button and the width the page offers say whether that size uses
// the room it was given, and the largest size that does is the one that is drawn. Nothing here holds a
// Unity type, so the whole of it is asserted headlessly.
// ---------------------------------------------------------------------------------------------------

// What one measurement of the table at one glyph size produced. A table with no extent is not a table,
// so a failed or empty measurement is never evidence that a larger glyph fits.
public readonly struct ResultTableSize
{
    public readonly float Width, Height;
    public ResultTableSize(float width, float height) { Width = width; Height = height; }
    public static ResultTableSize None { get { return new ResultTableSize(0f, 0f); } }
    public bool Usable => float.IsFinite(Width) && float.IsFinite(Height) && Width > 0f && Height > 0f;
}

public static class ResultsFontGrowth
{
    // The readability ceiling, as a multiple of the glyph size the in-level table shares. Past this the
    // board stops reading as a table and starts reading as a poster, so the climb ends here whatever the
    // strip would still accept. It is applied by this method itself, not only by its caller.
    public const float MaximumSeedMultiple = 3.5f;
    // The band the board is meant to fill: below the floor it still leaves the strip looking empty, and
    // above the ceiling it would be pressed against the button the player has to press.
    public const float StripFillFloor = 0.92f;
    public const float StripFillCeiling = 0.96f;
    // The width counts as used once the table is this close to the width the page offers.
    public const float WidthFillRatio = 0.98f;
    // A measured width is text plus padding, so a table can come back a fraction of a pixel over the box
    // it was asked for. This is the tolerance that keeps such a table usable instead of refusing it.
    public const float WidthSlack = 0.5f;
    // Hard bound on how many times the caller's measure is invoked. Bisection needs about six probes for a
    // 3.5x climb; the bound only exists so a caller whose measurement is not monotone cannot spin here.
    public const int MaximumMeasurements = 64;

    // The glyph size to draw the settlement board at.
    //
    // `seedPixels` is the size the in-level table shares and the size the board has always been drawn at;
    // `minimumPixels` is the smallest size the caller will accept as readable; `maximumPixels` is the
    // caller's own ceiling, which is additionally clamped to `MaximumSeedMultiple` times the seed.
    // `stripHeight` is the height between the page's completion section and the lobby button, `boardWidth`
    // the width the page offers, and `measure` returns the table's natural extents at a candidate size.
    //
    // The answer is the largest size that is both readable and inside the band: its table is at most
    // `StripFillCeiling` of the strip tall and at most `boardWidth` wide, and the climb stops early the
    // moment the table is already inside the band or has used the width. A seed that already fills the
    // strip is returned unchanged, so asking twice for the same strip gives the same size twice.
    public static int FitResultFontSize(int seedPixels, int minimumPixels, int maximumPixels, float stripHeight,
        float boardWidth, Func<int, ResultTableSize> measure)
    {
        int seed = seedPixels < 1 ? 1 : seedPixels;
        // A page whose strip or width could not be measured has nothing to fit to, and a caller with no
        // measurement at all has no evidence: every one of those keeps the size the board already uses.
        if (measure == null) return seed;
        if (!float.IsFinite(stripHeight) || stripHeight <= 0f) return seed;
        if (!float.IsFinite(boardWidth) || boardWidth <= 0f) return seed;
        int ceiling = Ceiling(seed, maximumPixels);
        if (ceiling <= seed) return seed;

        ResultTableSize first = Measured(measure, seed);
        if (!first.Usable) return seed;
        // A board that already overruns its box cannot be fixed by making it bigger; that is the caller's
        // problem to place, and growing here only makes it worse.
        if (!Fits(first, stripHeight, boardWidth)) return seed;
        if (Filled(first, stripHeight, boardWidth)) return seed;

        // The readable floor is the first size above the seed that may be tried. A floor that does not fit
        // is not a reason to overrun the strip, so the seed is kept instead.
        int low = seed;
        if (minimumPixels > seed)
        {
            int readable = Math.Min(minimumPixels, ceiling);
            ResultTableSize probe = Measured(measure, readable);
            if (!probe.Usable || !Fits(probe, stripHeight, boardWidth)) return seed;
            low = readable;
            if (Filled(probe, stripHeight, boardWidth)) return readable;
        }
        if (low >= ceiling) return low;

        ResultTableSize top = Measured(measure, ceiling);
        if (top.Usable && Fits(top, stripHeight, boardWidth)) return ceiling;

        // `low` is known to fit and `high` is known not to, so the largest size that fits lies between
        // them. Bisection, not a multiplication: every step is a real measurement of the table.
        int high = ceiling;
        int guard = 0;
        while (high - low > 1 && guard++ < MaximumMeasurements)
        {
            int middle = low + (high - low) / 2;
            ResultTableSize probe = Measured(measure, middle);
            if (!probe.Usable || !Fits(probe, stripHeight, boardWidth)) { high = middle; continue; }
            low = middle;
            // The strip is used once the table is inside its band, which is where the climb was heading.
            if (Filled(probe, stripHeight, boardWidth)) break;
        }
        return low;
    }

    // The smallest glyph size `FitIntoStrip` will ever answer with. Below this a table is no longer a table a
    // player can read, and shrinking further would only trade text for room nobody can use.
    public const int MinimumReadablePixels = 6;

    // The share of the button's own height that the settlement board's glyphs have to keep. The board is
    // fitted into the strip between the page's completion section and the lobby button, and a strip that
    // is short is what used to drag the glyph size down to the absolute minimum - a table the player has
    // to press a button under, drawn at 6 to 12px. Deriving the floor from the control the player presses
    // instead keeps the figures as large as the room in front of that control really allows.
    public const float ButtonHeightShare = 0.15f;
    // A sanity ceiling for that floor: the button's measured height is a hint, never a poster licence.
    public const int MaximumReadabilityFloor = 32;

    // The smallest glyph size the settlement board may be drawn at, given the measured height of the
    // button it has to clear. An unmeasurable, zero or negative height keeps the old absolute minimum, so
    // a page that hands nothing measurable behaves exactly as it did before this existed.
    public static int ReadabilityFloor(float buttonHeight)
    {
        if (!float.IsFinite(buttonHeight) || buttonHeight <= 0f) return MinimumReadablePixels;
        int floor = (int)Math.Round(buttonHeight * ButtonHeightShare);
        if (floor < MinimumReadablePixels) floor = MinimumReadablePixels;
        if (floor > MaximumReadabilityFloor) floor = MaximumReadabilityFloor;
        return floor;
    }

    // The glyph size to draw the settlement board at, when the seed itself is the wrong size.
    //
    // `FitResultFontSize` above is a climb: a board that already overruns its strip is returned unchanged,
    // because for the in-level table an overrun is the caller's to place. On a result page that is exactly
    // the case that hurts - the sharing size can be taller than the strip between the completion section and
    // the lobby button, the climb refuses to look downwards, and `HudTable.BuildGrid` then squeezes the
    // table into the box it was given, which is how a board ended up drawn at ~9px with a band of empty page
    // above the footer. This one looks in both directions and answers with the largest size that fits:
    // downwards when the seed overruns, upwards when the strip has room to spare, and never past the
    // `MaximumSeedMultiple` readability cap or the caller's own `maximumPixels`.
    //
    // `floorPixels` is the readability floor and is never taken below `MinimumReadablePixels`; it may be
    // above the answer only when nothing between it and the ceiling fits at all, in which case the seed is
    // returned and the caller's own shrink decides - the same degradation this had before. `measure`
    // answers with the table's natural extents at a candidate size; a measure that is missing, throws or
    // reports no extent, and a strip or width that could not be measured, all keep the seed.
    public static int FitIntoStrip(int seedPixels, int floorPixels, int maximumPixels, float stripHeight,
        float boardWidth, Func<int, ResultTableSize> measure)
    {
        int seed = seedPixels < 1 ? 1 : seedPixels;
        if (measure == null) return seed;
        if (!float.IsFinite(stripHeight) || stripHeight <= 0f) return seed;
        if (!float.IsFinite(boardWidth) || boardWidth <= 0f) return seed;
        int ceiling = Ceiling(seed, maximumPixels);
        int floor = floorPixels < MinimumReadablePixels ? MinimumReadablePixels : floorPixels;
        if (floor > ceiling) floor = ceiling;

        ResultTableSize seedSize = Measured(measure, seed);
        if (!seedSize.Usable) return seed;
        if (ceiling > seed)
        {
            ResultTableSize top = Measured(measure, ceiling);
            if (top.Usable && Fits(top, stripHeight, boardWidth)) return ceiling;
        }
        else if (Fits(seedSize, stripHeight, boardWidth)) return seed;

        // The ceiling has been refused (or is the seed itself), so the answer - if there is one - lies
        // between the readable floor and that refused size. A floor that does not fit either means the strip
        // cannot hold the table at any size worth drawing, and the seed is kept.
        int low = floor;
        int high = ceiling;
        ResultTableSize smallest = low == seed ? seedSize : Measured(measure, low);
        if (!smallest.Usable || !Fits(smallest, stripHeight, boardWidth)) return seed;
        int guard = 0;
        while (high - low > 1 && guard++ < MaximumMeasurements)
        {
            int middle = low + (high - low) / 2;
            ResultTableSize probe = Measured(measure, middle);
            if (!probe.Usable || !Fits(probe, stripHeight, boardWidth)) { high = middle; continue; }
            low = middle;
        }
        return low;
    }

    // The glyph size the settlement board is drawn at, when the readability floor is a preference rather
    // than a licence to draw nothing.
    //
    // `FitIntoStrip` above answers with the seed when nothing between the floor and the ceiling fits, and
    // `HudTable.BuildGrid` then honours that same floor and refuses to shrink below it: the grid it hands
    // back is not merely taller than the strip, it is refused by the placement and the board is never drawn
    // at all. The floor is what the board would like its figures to be, not what it may cost: a strip that
    // cannot hold the table at the readable size is drawn at the absolute minimum instead, which is exactly
    // the degradation this had before the floor existed. `usedFloor` reports which of the two produced the
    // answer, and the caller has to build its grid with that one - a grid built with the floor that was
    // refused is the overrun again.
    //
    // A measurement that reports nothing is not evidence that anything overruns, so the first pass stands as
    // it is; a floor already at the absolute minimum leaves nothing lower to fall back to.
    public static int FitBoardIntoStrip(int seedPixels, int floorPixels, int maximumPixels, float stripHeight,
        float boardWidth, Func<int, ResultTableSize> measure, out int usedFloor)
    {
        int floor = floorPixels < MinimumReadablePixels ? MinimumReadablePixels : floorPixels;
        usedFloor = floor;
        int size = FitIntoStrip(seedPixels, floor, maximumPixels, stripHeight, boardWidth, measure);
        if (measure == null) return size;
        ResultTableSize table = Measured(measure, size);
        if (!table.Usable || Inside(table, stripHeight, boardWidth)) return size;
        if (floor <= MinimumReadablePixels) return size;
        usedFloor = MinimumReadablePixels;
        return FitIntoStrip(seedPixels, MinimumReadablePixels, maximumPixels, stripHeight, boardWidth, measure);
    }

    // The table the answer produces really lands in the box it was measured against. The height is the figure
    // that decides whether the board is drawn at all, and the slack is the half pixel a ratio-based rebuild
    // can leave behind - the same tolerance the fit above already allows.
    private static bool Inside(ResultTableSize size, float stripHeight, float boardWidth)
        => size.Height <= stripHeight + 0.5f && size.Width <= boardWidth + WidthSlack;

    // The table is inside the box it was given: tall enough to be the point of the page, short enough to
    // stay clear of the button, and never wider than the width the page offers.
    private static bool Fits(ResultTableSize size, float stripHeight, float boardWidth)
        => size.Height <= stripHeight * StripFillCeiling + 0.5f && size.Width <= boardWidth + WidthSlack;

    // The strip is already being used: either the table has reached the band's floor, or it has used the
    // width, in which case growing further would only cost height for nothing.
    private static bool Filled(ResultTableSize size, float stripHeight, float boardWidth)
        => size.Height >= stripHeight * StripFillFloor || size.Width >= boardWidth * WidthFillRatio;

    private static ResultTableSize Measured(Func<int, ResultTableSize> measure, int fontSize)
    {
        try { return measure(fontSize); }
        catch { return ResultTableSize.None; }
    }

    // The climb's hard ceiling: the caller's own limit and the readability multiple of the seed, whichever
    // is smaller, and never below the seed itself.
    private static int Ceiling(int seed, int maximumPixels)
    {
        double scaled = Math.Floor(seed * (double)MaximumSeedMultiple);
        int readable = scaled >= int.MaxValue ? int.MaxValue : (int)scaled;
        int requested = maximumPixels < 1 ? readable : maximumPixels;
        int ceiling = Math.Min(readable, requested);
        return ceiling < seed ? seed : ceiling;
    }
}
