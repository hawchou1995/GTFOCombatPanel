using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace HawChow.GTFOCombatPanel;

// What the renderer needs: one entry per row and column, holding that cell's fields, plus the
// colour of the cell. Field-level content is what lets a merged column right align every value
// in its own slot instead of pushing the whole group around.
internal sealed class TableContent
{
    internal PanelColumn[] Columns;
    internal string[][][] Text;
    internal Color[,] Colors;
    // How the cells of a multi-value group are printed: when set, the values of one cell are drawn
    // as one right-aligned string held apart by this separator instead of as separate slots.
    internal string FieldSeparator;
    // Rows that carry a hairline above them, so the board reads in sections instead of as one block.
    internal bool[] Dividers;
    internal ImpactRowKind[] Kinds;

    internal int Rows => Text != null ? Text.Length : 0;
    internal int ColumnCount => Columns != null ? Columns.Length : 0;
    internal string[] Cell(int row, int column)
    {
        if (Text == null || row < 0 || row >= Text.Length) return Array.Empty<string>();
        var rowCells = Text[row];
        if (rowCells == null || column < 0 || column >= rowCells.Length) return Array.Empty<string>();
        return rowCells[column] ?? Array.Empty<string>();
    }
}

// Screen-space table rendering.
//
// The table is drawn with IMGUI, in window pixels. IMGUI is composited after every camera and
// every canvas, so the table inherits no view and no HUD motion at all: its position changes only
// when the configuration does.
internal static class HudTable
{
    private static GUIStyle _style;
    private static int _styleFontSize = -1;
    private static Font _font;
    private static Texture2D _backdrop;
    private static bool _loggedFont;

    internal static GUIStyle Style(int fontSize)
    {
        if (fontSize < 1) fontSize = 1;
        if (_style != null && _styleFontSize == fontSize) return _style;
        _styleFontSize = fontSize;
        var style = new GUIStyle(GUI.skin.label);
        style.richText = false;
        style.wordWrap = false;
        style.clipping = TextClipping.Clip;
        style.alignment = TextAnchor.UpperRight;
        style.fontSize = fontSize;
        style.padding = new RectOffset(0, 4, 0, 0);
        Font font = FontFor(fontSize);
        if (font != null) style.font = font;
        _style = style;
        return _style;
    }

    // How tall one row has to be for this face, measured rather than assumed.
    //
    // The face is a single cached object (`FontFor` hands the same one back for every size), so its
    // `lineHeight` is fixed and dividing it by whatever size was asked for produced a ratio that moved
    // with the caller - which is why the board appeared to rescale itself on an F8 redraw. `font.fontSize`
    // is the size that line height actually belongs to, so the ratio is the face constant it was always
    // meant to be, and it is measured once rather than per size.
    internal static float RowEm(int fontSize)
    {
        if (_rowEm > 1f) return _rowEm;
        try
        {
            Font font = FontFor(fontSize < 1 ? 1 : fontSize);
            if (font != null)
            {
                int faceSize = font.fontSize;
                float ratio = faceSize > 0 ? font.lineHeight / (float)faceSize : 0f;
                // Guard against a face that reports nothing or something wild; the bound is only a bound.
                if (ratio >= 1.02f && ratio <= 2.5f) _rowEm = ratio;
            }
        }
        catch { }
        return _rowEm;
    }
    private static float _rowEm = PanelGeometry.MinimumRowEm;
    // The in-level HUD and result board share this renderer, so both borrow the active GTFO TMP face.
    // A TMP atlas itself cannot be assigned to GUIStyle; use its source Font, then the TMP face family.
    internal static Font FontFor(int fontSize)
    {
        if (_font != null && _font.Pointer != IntPtr.Zero) return _font;
        string nativeError = null;
        try
        {
            TMP_FontAsset asset = FindNativeCjkFontAsset();
            if (asset != null)
            {
                Font source = asset.sourceFontFile;
                if (source != null && source.Pointer != IntPtr.Zero)
                {
                    _font = source;
                    LogFont($"'{_font.name}' reused from GTFO TMP asset '{asset.name}'");
                    return _font;
                }

                string family = asset.faceInfo.familyName;
                if (!string.IsNullOrWhiteSpace(family))
                {
                    _font = CreateDynamicFont(new[] { family }, fontSize);
                    if (IsUsableFont(_font))
                    {
                        LogFont($"'{_font.name}' created from GTFO TMP face '{family}' (asset '{asset.name}')");
                        return _font;
                    }
                }
                nativeError = "the selected GTFO TMP asset exposed neither a usable source font nor a system face";
            }
            else nativeError = "TMP_Settings has no loaded font asset with direct CJK glyphs";
        }
        catch (Exception error) { nativeError = error.GetType().Name + ": " + error.Message; }

        try
        {
            _font = CreateDynamicFont(new[]
            {
                "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "SimSun", "Noto Sans CJK SC", "Arial"
            }, fontSize);
            if (IsUsableFont(_font))
            {
                LogFont($"'{_font.name}' loaded from the operating system as a fallback; GTFO TMP font unavailable ({nativeError})");
                return _font;
            }
        }
        catch (Exception error) { nativeError = (nativeError ?? string.Empty) + "; OS fallback: " + error.Message; }

        if (!IsUsableFont(_font)) _font = null;
        if (!_loggedFont)
        {
            _loggedFont = true;
            Plugin.Logger?.LogWarning("[DIAG] no usable GTFO TMP or CJK system font was available; the table keeps GUI.skin.label: " + nativeError);
        }
        return _font;
    }

    private static TMP_FontAsset FindNativeCjkFontAsset()
    {
        var candidates = new List<TMP_FontAsset>();
        var seen = new HashSet<IntPtr>();
        TMP_FontAsset defaultAsset = TMP_Settings.defaultFontAsset;
        if (defaultAsset == null) defaultAsset = TMP_Settings.GetFontAsset();
        AddFontAsset(candidates, seen, defaultAsset);
        if (defaultAsset != null) AddFontAssets(candidates, seen, defaultAsset.fallbackFontAssetTable);
        AddFontAssets(candidates, seen, TMP_Settings.fallbackFontAssets);

        for (int index = 0; index < candidates.Count && index < 128; index++)
        {
            TMP_FontAsset candidate = candidates[index];
            if (candidate == null) continue;
            try
            {
                if (candidate.HasCharacter('任', false, false) && candidate.HasCharacter('务', false, false))
                    return candidate;
            }
            catch { }
            AddFontAssets(candidates, seen, candidate.fallbackFontAssetTable);
        }
        return null;
    }

    private static void AddFontAssets(List<TMP_FontAsset> target, HashSet<IntPtr> seen,
        Il2CppSystem.Collections.Generic.List<TMP_FontAsset> source)
    {
        if (source == null) return;
        foreach (TMP_FontAsset asset in source) AddFontAsset(target, seen, asset);
    }

    private static void AddFontAsset(List<TMP_FontAsset> target, HashSet<IntPtr> seen, TMP_FontAsset asset)
    {
        if (asset == null || asset.Pointer == IntPtr.Zero || target.Count >= 128 || !seen.Add(asset.Pointer)) return;
        target.Add(asset);
    }

    private static Font CreateDynamicFont(string[] familyNames, int fontSize)
    {
        var font = new Font();
        var names = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(familyNames);
        Font.Internal_CreateDynamicFont(font, names, Math.Max(8, fontSize));
        return font;
    }

    private static bool IsUsableFont(Font font) => font != null && font.Pointer != IntPtr.Zero;

    private static void LogFont(string message)
    {
        if (_loggedFont) return;
        _loggedFont = true;
        Plugin.Logger?.LogInfo("[DIAG] table font: " + message + "; shared by the in-level HUD and settlement board.");
    }

    // Width the font really needs for one line, in pixels. IMGUI measures the same atlas it will
    // draw with, so a measured slot is a slot that cannot truncate.
    internal static float Measure(string text, int fontSize, bool includeBold = false)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        GUIStyle style = Style(fontSize);
        if (style == null) return 0;
        try
        {
            FontStyle previous = style.fontStyle;
            try
            {
                style.fontStyle = FontStyle.Normal;
                float normal = style.CalcSize(new GUIContent(text)).x;
                style.fontStyle = FontStyle.Bold;
                float width = includeBold ? Mathf.Max(normal, style.CalcSize(new GUIContent(text)).x) : normal;
                return float.IsFinite(width) && width > 0 ? width : 0;
            }
            finally { style.fontStyle = previous; }
        }
        catch { return 0; }
    }

    // Column widths for a whole table: one slot per field, each as wide as the widest value that
    // lands in it. Because every field keeps its own slot, the numbers of a merged column line up
    // vertically across rows. Fields are held apart by a gap that is a fraction of the glyph, not
    // by a drawn separator: that is what keeps a merged column narrow. The whole table is scaled
    // down if it would outgrow the box it was given.
    internal static PanelGrid BuildGrid(TableContent content, int fontSize, PanelDensity density, float maxWidth, float maxHeight)
    {
        if (content == null || content.ColumnCount == 0 || content.Rows == 0) return null;
        float separatorWidth = PanelGeometry.FieldGap(fontSize, density);
        var metrics = new PanelColumnMetrics[content.ColumnCount];
        for (int column = 0; column < content.ColumnCount; column++)
        {
            // Settlement cells share one measured right edge; HUD fields keep their own slots.
            if (content.FieldSeparator != null)
            {
                float width = PanelGeometry.MinimumWidth(content.Columns[column], fontSize);
                for (int row = 0; row < content.Rows; row++)
                {
                    string joined = string.Join(content.FieldSeparator, content.Cell(row, column));
                    width = Mathf.Max(width, Mathf.Max(PanelGeometry.EstimateTextWidth(joined, fontSize), Measure(joined, fontSize, true)));
                }
                metrics[column] = new PanelColumnMetrics(new[] { width });
                continue;
            }
            int maxFields = 1;
            for (int row = 0; row < content.Rows; row++) maxFields = Mathf.Max(maxFields, content.Cell(row, column).Length);
            float floor = PanelGeometry.MinimumWidth(content.Columns[column], fontSize) / maxFields;
            var widths = new float[maxFields];
            for (int field = 0; field < maxFields; field++) widths[field] = floor;
            for (int row = 0; row < content.Rows; row++)
            {
                string[] fields = content.Cell(row, column);
                for (int field = 0; field < fields.Length; field++)
                {
                    int slot = maxFields - fields.Length + field;
                    if (slot < 0 || slot >= maxFields) continue;
                    float width = Mathf.Max(PanelGeometry.EstimateTextWidth(fields[field], fontSize), Measure(fields[field], fontSize));
                    widths[slot] = Mathf.Max(widths[slot], width);
                }
            }
            metrics[column] = new PanelColumnMetrics(widths);
        }
        // A column of figures is right-aligned so it shares one right edge down the board; a label or a
        // player's name reads from the left instead.
        bool[] rightAligned = null;
        if (content.Kinds != null)
        {
            rightAligned = new bool[content.ColumnCount];
            for (int column = 0; column < content.ColumnCount; column++)
                rightAligned[column] = content.Columns[column] != PanelColumn.ImpactLabel;
        }
        // One measurement for the whole build: the row asks for the face's own line height at the size
        // being tried, so fitting the table to a box shrinks the row with the glyph instead of pinning it.
        float rowEm = RowEm(fontSize);
        PanelGrid grid = PanelGeometry.Build(metrics, separatorWidth, fontSize, content.Rows, density, 1f, rightAligned, rowEm);
        float fit = grid.FitFactor(maxWidth, maxHeight);
        if (fit < 0.999f) grid = PanelGeometry.Build(metrics, separatorWidth, fontSize, content.Rows, density, fit, rightAligned, rowEm);
        // The fit factor is a ratio, and the rebuild rounds the glyph size, so a grid can come back a
        // fraction of a pixel taller than the box it was asked to fit - which is exactly how a board that
        // fitted in principle got refused by a `>` comparison at draw time. Shrink the glyph size until
        // the grid really fits, or until it is too small to be worth drawing.
        if (maxHeight > 0f && float.IsFinite(maxHeight))
        {
            int guard = 0;
            int working = fontSize;
            while (grid.Height > maxHeight && working > 6 && guard++ < 40)
            {
                working--;
                grid = PanelGeometry.Build(metrics, separatorWidth, working, content.Rows, density, 1f, rightAligned, RowEm(working));
            }
        }
        return grid;
    }

    internal static void Draw(PanelGrid grid, float originX, float originY, TableContent content, float opacity = 1f)
    {
        if (grid == null || grid.Columns == 0 || content == null) return;
        int rows = Math.Min(grid.Rows, content.Rows);
        GUIStyle style = Style(Mathf.Max(1, Mathf.RoundToInt(grid.FontPixels)));
        if (style == null) return;

        Texture2D backdrop = Backdrop();
        if (backdrop != null)
        {
            Color previous = GUI.color;
            // A dark plate so the numbers stay readable over bright geometry, a hairline border so the
            // board reads as one surface, banding for scanning across a row, and a hairline above each
            // section. Everything here is a flat tint of the same white texture: no atlas, no state.
            // How heavy the in-level plate is, is the player's setting (`[Layout] PanelOpacity`); the
            // settlement board keeps its own approved value. Both are read here, on every draw, so an edit
            // to the file - or a hot reload - is on screen the next frame with no rebuild.
            float plate = PanelAppearance.Plate(Plugin.PanelOpacity != null ? Plugin.PanelOpacity.Value : float.NaN, content.Kinds != null);
            GUI.color = content.Kinds != null ? new Color(0.035f, 0.045f, 0.055f, plate * opacity) : new Color(0.04f, 0.05f, 0.06f, plate * opacity);
            GUI.DrawTexture(new Rect(originX, originY, grid.Width, grid.Height), backdrop);
            for (int row = 1; content.Kinds != null && row < rows; row += 2)
            {
                GUI.color = new Color(1f, 1f, 1f, ResultsAppearance.RowBandOpacity * opacity);
                GUI.DrawTexture(new Rect(originX, originY + row * grid.RowHeight, grid.Width, grid.RowHeight), backdrop);
            }
            for (int row = 0; row < rows; row++)
            {
                if (content.Dividers == null || row >= content.Dividers.Length || !content.Dividers[row]) continue;
                GUI.color = new Color(1f, 1f, 1f, ResultsAppearance.RuleOpacity * opacity);
                GUI.DrawTexture(new Rect(originX, originY + row * grid.RowHeight, grid.Width, 1f), backdrop);
            }
            GUI.color = new Color(1f, 1f, 1f, (content.Kinds != null ? ResultsAppearance.RuleOpacity : PanelAppearance.Frame(plate)) * opacity);
            GUI.DrawTexture(new Rect(originX, originY, grid.Width, 1f), backdrop);
            GUI.DrawTexture(new Rect(originX, originY + grid.Height - 1f, grid.Width, 1f), backdrop);
            GUI.DrawTexture(new Rect(originX, originY, 1f, grid.Height), backdrop);
            GUI.DrawTexture(new Rect(originX + grid.Width - 1f, originY, 1f, grid.Height), backdrop);
            GUI.color = previous;
        }

        for (int row = 0; row < rows; row++)
        {
            // Every value column follows the same right edge; only settlement row labels stay left-aligned.
            for (int column = 0; column < grid.Columns; column++)
            {
                string[] fields = content.Cell(row, column);
                Color color = content.Colors != null ? content.Colors[row, column] : Color.white;
                color.a *= opacity;
                style.normal.textColor = color;
                style.alignment = PanelAlignmentPolicy.IsRightAligned(content.Kinds != null, grid.IsRightAligned(column))
                    ? TextAnchor.UpperRight : TextAnchor.UpperLeft;
                style.fontStyle = content.Kinds != null && row < content.Kinds.Length &&
                    (content.Kinds[row] == ImpactRowKind.Score || content.Kinds[row] == ImpactRowKind.PlayerName || content.Kinds[row] == ImpactRowKind.Separator)
                    ? FontStyle.Bold : FontStyle.Normal;
                if (content.FieldSeparator != null)
                {
                    LayoutRect slot = grid.FieldRect(row, column, 0);
                    GUI.Label(new Rect(originX + slot.X, originY + slot.Y, slot.Width, slot.Height),
                        string.Join(content.FieldSeparator, fields), style);
                    continue;
                }
                for (int field = 0; field < fields.Length; field++)
                {
                    string text = fields[field];
                    if (string.IsNullOrEmpty(text)) continue;
                    LayoutRect slot = grid.FieldRectAligned(row, column, fields.Length, field);
                    GUI.Label(new Rect(originX + slot.X, originY + slot.Y, slot.Width, slot.Height), text, style);
                }
            }
        }
    }

    private static Texture2D Backdrop()
    {
        if (_backdrop != null && _backdrop.Pointer != IntPtr.Zero) return _backdrop;
        try { _backdrop = Texture2D.whiteTexture; } catch { _backdrop = null; }
        return _backdrop;
    }
}
