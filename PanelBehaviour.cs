using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime.Attributes;
using Player;
using UnityEngine;

namespace HawChow.GTFOCombatPanel;

// The HUD table, drawn with IMGUI in window pixels.
//
// IMGUI is composited after every camera and every canvas in the frame, and it is positioned
// purely against the window. The table therefore inherits no camera motion and no HUD parallax:
// once its anchor has been resolved it does not move again, and only a configuration change
// relocates it. It also cannot be clipped by a parent canvas, so every column is always on screen.
//
// The anchor is the native flashlight icon: the same element the reference mods hang their text
// beside. Its screen position is sampled through the HUD's own camera - the camera that actually
// renders that canvas - until it stops moving (the HUD animates while entering a level), and is
// then frozen. Sampling once, on the first frame, is what made earlier attempts land hundreds of
// pixels away from the icon.
public sealed class PanelBehaviour : MonoBehaviour
{
    private const int MaxPlayers = 4;
    private const float SampleInterval = 0.25f;

    private static bool _pendingInvalidate = true;
    private bool _visible = true;
    // The F8 toggle, shared with the settlement board so the same key hides and shows this mod's overlay
    // in a level and on a result page alike.
    internal static bool ToggleState = true;
    private float _refreshTime = -1f;
    // The F8 toggle state the last refresh was computed with, so a frame in which the key changed always
    // refreshes. Without it the toggle could wait out the rest of the 0.25 s poll before it was honoured.
    private static bool _refreshedToggle = true;
    private bool _loggedError, _loggedDraw, _loggedAnchorFailure;
    private TableContent _content;
    private PanelGrid _grid;
    private LayoutRect _origin;

    // Anchor sampling state. The anchor is sampled at most until it settles, then never again.
    private bool _anchorLocked;
    private bool _anchorUnavailable;
    private float _anchorSettle;
    private float _anchorSampled;
    private float _anchorLastX = float.NaN, _anchorLastY = float.NaN;
    private int _anchorScreenWidth, _anchorScreenHeight;
    private LayoutRect _anchorRect;

    public PanelBehaviour(IntPtr pointer) : base(pointer) { }

    internal static void InvalidateLayout()
    {
        _pendingInvalidate = true;
        _reanchorRequested = true;
    }

    // A config change or a resolution change is allowed to move the table: the anchor is sampled
    // again and re-frozen. Nothing else ever moves it.
    internal static bool _reanchorRequested;

    private void UnlockAnchor()
    {
        _anchorLocked = false;
        _anchorUnavailable = false;
        _anchorSettle = 0f;
        _anchorSampled = 0f;
        _anchorLastX = float.NaN;
        _anchorLastY = float.NaN;
    }

    private void Update()
    {
        Plugin.PollHotReload();
        try
        {
            var state = GameStateManager.CurrentStateName;
            bool inLevel = state == eGameStateName.InLevel;
            bool visible = inLevel ? Plugin.ShowPanel.Value : Plugin.ShowResultOverlay.Value;
            if (Plugin.ToggleKey.Value != KeyCode.None && Input.GetKeyDown(Plugin.ToggleKey.Value))
            {
                visible = !visible;
                Plugin.PersistToggle(visible);
                Plugin.Logger?.LogInfo($"[DIAG] F8 visibility: state={state}, requested={visible}, ShowPanel={Plugin.ShowPanel.Value}, ShowResultOverlay={Plugin.ShowResultOverlay.Value}, ShowResults={Plugin.ShowResults.Value}, ShowImpactBoard={Plugin.ShowImpactBoard.Value}.");
            }
            // Config is the source of truth, including a manual edit made after an F8 toggle.
            _visible = Plugin.ShowPanel.Value;
            ToggleState = visible;
            CombatTracker.ObserveState(state);
            if (inLevel) CombatTracker.RefreshRoster();
        }
        catch (Exception error) { LogOnce(error); }
    }

    private void OnGUI()
    {
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        try
        {
            if (_reanchorRequested || _anchorScreenWidth != Screen.width || _anchorScreenHeight != Screen.height)
            {
                _reanchorRequested = false;
                UnlockAnchor();
            }
            // The result pages animate their own content in, so the board attaches on the frame the page
            // exists instead of waiting out the poll: that quarter second is when the vanilla blocks
            // would otherwise still be visible. The toggle is watched the same way and for the same reason:
            // F8 has to show or hide this mod's overlay on the very next Repaint, not up to a quarter of a
            // second later, so a frame in which the toggle changed state always refreshes, whatever the poll
            // is in the middle of. That is the whole of the hysteresis on the result screens - the board's
            // own placement and readiness are kept per page, so re-attaching after a toggle never waits for
            // a readiness the page has already earned.
            if (_pendingInvalidate || ToggleState != _refreshedToggle || Time.unscaledTime >= _refreshTime || ResultsDisplay.NeedsAttach())
            {
                _pendingInvalidate = false;
                _refreshedToggle = ToggleState;
                _refreshTime = Time.unscaledTime + SampleInterval;
                Refresh();
            }
            if (_grid != null && _content != null) HudTable.Draw(_grid, _origin.X, _origin.Y, _content);
            ResultsDisplay.Draw();
        }
        catch (Exception error) { LogOnce(error); }
    }

    [HideFromIl2Cpp]
    private void Refresh()
    {
        _grid = null; _content = null;
        var state = GameStateManager.CurrentStateName;
        ResultsDisplay.Refresh(state);
        if (state != eGameStateName.InLevel || !_visible || !Plugin.ShowPanel.Value) return;
        var layer = GuiManager.Current?.m_playerLayer;
        if (layer == null || !layer.IsVisible() || layer.Inventory?.m_iconDisplay == null) return;

        if (Plugin.AnchorToFlashlight.Value && !_anchorLocked && !_anchorUnavailable) SampleAnchor(layer);

        var columns = Columns();
        var players = CombatSync.DisplayRows().OrderBy(p => p.DisplaySlot).Take(MaxPlayers).ToList();
        _content = BuildTable(columns, players);
        _grid = HudTable.BuildGrid(_content, FontSize(), Plugin.Density, Screen.width, Screen.height);
        if (_grid == null) { _content = null; return; }
        if (!TryResolveOrigin(_grid, out var origin)) { _grid = null; _content = null; return; }   // the anchor is still settling; draw nothing rather than jump
        _origin = origin;
        if (!_loggedDraw)
        {
            _loggedDraw = true;
            Plugin.Logger.LogInfo($"[DIAG] table {_content.ColumnCount} columns x {_content.Rows} rows, {_grid.Width:0}x{_grid.Height:0} px at ({origin.X:0},{origin.Y:0}) of a {Screen.width}x{Screen.height} window, font {_grid.FontPixels:0}px, density={_grid.Density.Name}, anchor={(_anchorLocked ? "flashlight icon" : "config PositionX/PositionY")}, columns={string.Join("+", columns)}; drawn by IMGUI in screen space, so nothing in the game can move it.");
        }
    }

    // One field list per cell: a merged column's values are split so each one gets its own
    // right-aligned slot, and the header is split the same way so labels line up with them.
    [HideFromIl2Cpp]
    private static TableContent BuildTable(IReadOnlyList<PanelColumn> columns, IReadOnlyList<CombatPlayer> players)
    {
        int rows = players.Count + 1;
        var text = new string[rows][][];
        var colors = new Color[rows, columns.Count];
        text[0] = new string[columns.Count][];
        for (int column = 0; column < columns.Count; column++)
        {
            text[0][column] = CombatStrings.HeaderFields(columns[column]);
            colors[0, column] = Color.white;
        }
        double? squad = CombatTracker.SquadEnemyDamage;
        for (int row = 0; row < players.Count; row++)
        {
            text[row + 1] = new string[columns.Count][];
            for (int column = 0; column < columns.Count; column++)
            {
                text[row + 1][column] = column == 0
                    ? new[] { CombatStatsFormatter.FullPlayerName(players[row].Name) }
                    : CombatStatsFormatter.Fields(players[row].Total, columns[column], squad);
                colors[row + 1, column] = column == 0 ? CombatTracker.PlayerColor(players[row].Lookup) : Color.white;
            }
        }
        return new TableContent { Columns = columns.ToArray(), Text = text, Colors = colors };
    }

    // Samples the flashlight icon's screen rectangle. The HUD animates while a level loads, so a
    // single sample can be far off; the sample is repeated until it stops moving, and the first
    // sample that holds still is the one that is frozen. If this cannot be measured at all, the
    // configured PositionX/PositionY is used instead and the failure is reported once.
    [HideFromIl2Cpp]
    private void SampleAnchor(PlayerGuiLayer layer)
    {
        _anchorSampled += SampleInterval;
        Camera camera = null;
        try { camera = layer.GuiLayerBase != null ? layer.GuiLayerBase.GetWorldCamera() : null; }
        catch { camera = null; }
        SpriteRenderer sprite = null;
        try
        {
            var icon = layer.Inventory != null && layer.Inventory.m_iconDisplay != null ? layer.Inventory.m_iconDisplay.FlashLightIcon : null;
            if (icon != null) sprite = icon.Enabled != null ? icon.Enabled : icon.Disabled;
        }
        catch { sprite = null; }

        if (camera == null || sprite == null || !TryScreenRect(sprite, camera, out var rect))
        {
            if (!_loggedAnchorFailure)
            {
                _loggedAnchorFailure = true;
                Plugin.Logger.LogWarning($"[DIAG] the flashlight icon could not be measured (camera={(camera == null ? "null" : "ok")}, icon={(sprite == null ? "null" : "ok")}); the table uses PositionX/PositionY instead.");
            }
            _anchorUnavailable = true;
            return;
        }

        bool still = !float.IsNaN(_anchorLastX) && Mathf.Abs(rect.X - _anchorLastX) < 2f && Mathf.Abs(rect.Y - _anchorLastY) < 2f;
        _anchorSettle = still ? _anchorSettle + SampleInterval : 0f;
        _anchorLastX = rect.X; _anchorLastY = rect.Y;
        _anchorRect = rect;
        if (_anchorSettle >= 1f || _anchorSampled >= 6f)
        {
            _anchorLocked = true;
            _anchorScreenWidth = Screen.width; _anchorScreenHeight = Screen.height;
            Plugin.Logger.LogInfo($"[DIAG] flashlight anchor locked after {_anchorSampled:0.00}s at ({rect.X:0},{rect.Y:0}) size {rect.Width:0}x{rect.Height:0} screen px, sampled through the HUD's own camera; the table is positioned relative to this and then held.");
        }
    }

    [HideFromIl2Cpp]
    private static bool TryScreenRect(Renderer renderer, Camera camera, out LayoutRect rect)
    {
        rect = default;
        Bounds bounds = renderer.bounds;
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 point = bounds.center + Vector3.Scale(bounds.extents,
                new Vector3((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f));
            Vector3 screen = camera.WorldToScreenPoint(point);
            if (screen.z < 0f || !float.IsFinite(screen.x) || !float.IsFinite(screen.y)) return false;
            float y = Screen.height - screen.y;
            x0 = Mathf.Min(x0, screen.x); x1 = Mathf.Max(x1, screen.x);
            y0 = Mathf.Min(y0, y); y1 = Mathf.Max(y1, y);
        }
        rect = new LayoutRect(x0, y0, x1 - x0, y1 - y0);
        return rect.Width > 0.5f && rect.Height > 0.5f;
    }

    [HideFromIl2Cpp]
    private static List<PanelColumn> Columns()
        => Plugin.VisibleColumns.Count > 0 ? Plugin.VisibleColumns : PanelColumns.Compose(true, true, true, true, true);

    // With the flashlight anchor the table hangs below the icon, right-aligned with it, so it sits
    // in the right-hand gear column wherever that column happens to be. A negative PositionX/Y
    // measures from the right/bottom edge, which is the fallback when the icon cannot be measured.
    // Either way the whole table is clamped inside the window.
    [HideFromIl2Cpp]
    private bool TryResolveOrigin(PanelGrid grid, out LayoutRect origin)
    {
        origin = default;
        float x, y;
        if (Plugin.AnchorToFlashlight.Value && !_anchorUnavailable)
        {
            if (!_anchorLocked) return false;   // still settling: draw nothing rather than jump
            x = _anchorRect.Right - grid.Width + Plugin.OffsetX.Value;
            y = _anchorRect.Bottom + 8f + Plugin.OffsetY.Value;
        }
        else
        {
            float fractionX = Plugin.PositionX.Value;
            float fractionY = Plugin.PositionY.Value;
            x = (fractionX < 0f ? Screen.width - grid.Width + fractionX * Screen.width : fractionX * Screen.width) + Plugin.OffsetX.Value;
            y = (fractionY < 0f ? Screen.height - grid.Height + fractionY * Screen.height : fractionY * Screen.height) + Plugin.OffsetY.Value;
        }
        x = Mathf.Clamp(x, 0f, Mathf.Max(0f, Screen.width - grid.Width));
        y = Mathf.Clamp(y, 0f, Mathf.Max(0f, Screen.height - grid.Height));
        origin = new LayoutRect(x, y, grid.Width, grid.Height);
        return true;
    }

    // The glyph size is a reference size at 1080p, scaled by the window height and the density,
    // so the table looks the same on any resolution.
    private static int FontSize()
    {
        float scale = Mathf.Clamp(Screen.height / 1080f, 0.6f, 2.5f);
        return Mathf.Max(6, Mathf.RoundToInt(Plugin.FontSize.Value * Plugin.Density.Font * scale));
    }

    internal static int FontSizeForSharing => FontSize();

    [HideFromIl2Cpp]
    private void LogOnce(Exception error)
    {
        if (_loggedError) return;
        _loggedError = true;
        Plugin.Logger?.LogError("Panel failed: " + error);
    }
}
