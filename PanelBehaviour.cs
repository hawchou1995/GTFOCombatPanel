using System;
using System.Collections.Generic;
using System.Linq;
using CellMenu;
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
// The anchor is the equipment row's own layout rectangle: `GuiManager.Current -> m_playerLayer -> Inventory -> m_iconDisplay`,
// the same row the reference mods hang their text beside. Its screen position is sampled until it stops moving (the HUD
// animates while entering a level) and is then frozen. What is measured is the row's declared `RectTransform`
// (`m_iconDisplay.m_rectTrans`, 210.59 x 110.16 with its pivot on the right edge) - NOT the 65 px `SpriteRenderer` the
// `SpriteStatusIcon` value carries (32.5 world units at 2 pixels per unit), which this build used to read as "the icon" and
// which is not the rectangle the HUD lays the row out in. There are exactly two ways a sample can be answered, and the log
// says which one answered: a camera that can be *shown* to draw the element (its culling mask contains the element's layer, it
// is alive, and it is not a RenderTexture camera), or the element's own `RectTransform` projected by the camera its *own* canvas
// is composited by - and with no camera at all when that canvas is a genuine screen-space overlay canvas, which the element-canvas
// policy decides from that canvas itself (1.19.31). `GuiLayerBase.m_layerCanvas` and `GetWorldCamera()` are null for this HUD's
// whole life, so neither of them can name that camera; the CellUI canvas' `m_UICamera` is only one *candidate*, and the recorded
// level shows it is not always alive. No third way, and never a guessed coordinate: every answer goes through the same
// `ScreenBounds` sanity gate and the same settle window, a canvas whose camera is unusable is refused rather than substituted with
// another one, and sampling once - on the first frame - is what made earlier attempts land hundreds of pixels away.
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
    private bool _loggedError, _loggedDraw;
    private TableContent _content;
    private PanelGrid _grid;
    private LayoutRect _origin;

    // Anchor sampling state. The anchor is sampled until it settles and is then frozen. A sample that could not
    // be read at all is not a verdict any more: the HUD's camera answers null for a moment while a level is
    // coming up, so a failed sample buys a retry on the bounded schedule in `AnchorSamplePolicy` instead of
    // locking the table onto the configured position for the rest of the session.
    private bool _anchorLocked;
    private bool _anchorUnavailable;
    // A sample has already failed in this measurement round, so the configured PositionX/PositionY is drawn
    // while the retries continue: the table is never blank for the length of a retry schedule, and an anchor
    // that does arrive a moment later moves it once instead of never being used at all.
    private bool _anchorFallback;
    private float _anchorSettle;
    private float _anchorSampled;
    private float _anchorLastX = float.NaN, _anchorLastY = float.NaN;
    private int _anchorScreenWidth, _anchorScreenHeight;
    // How the icon's camera was resolved, in the words the failure line prints. Set by every read, so a give-up
    // line says which dead end it was instead of the old, uninformative "camera=null" - and so the camera
    // diagnostic below can be printed exactly once rather than on every attempt of the round.
    // A property of the one panel instance this mod creates, and static for the same reason `ReadAnchor` is: the HUD
    // read is a delegate handed to `AnchorSamplePolicy.Attempt`, and the note it leaves behind is what the failure and
    // lock lines print. Both are reset by `UnlockAnchor`, so the report belongs to a measurement round.
    private static string _anchorCameraNote = string.Empty;
    private static bool _loggedAnchorCamera;
    // The row's own `Background Fade` - the element AoiYuki's mods read out of `m_iconDisplay` when they want the row rather
    // than the icon. It is found *once* per HUD (a recursive walk of that one icon display, inactive children included) and
    // both the hit and the real miss are cached, so a HUD that does not carry it costs one walk per level instead of one per
    // sample, and nothing here ever scans a scene.
    private static RectTransform _backgroundFade;
    // The cache behind that walk. A walk that *threw* is recorded as unreadable, never as missing, so one bad frame cannot
    // decide that this level has no background: the next attempt walks again, on the anchor's own bounded cadence.
    private static readonly AnchorElementLookup _backgroundFadeLookup = new AnchorElementLookup();
    // One line per measurement round when the fallback is really used, never one per sample.
    private static bool _loggedBackgroundFade;
    // And one line per round when the walk itself threw, for the same reason.
    private static bool _loggedBackgroundFadeLookupError;
    // Which of the two elements the rectangle being settled came from. The two do not share a position, so a change of source
    // restarts the settle window rather than being read as movement (and then locked on).
    private readonly AnchorSourceTracker _anchorSource = new AnchorSourceTracker();
    private LayoutRect _anchorRect;
    private readonly AnchorSamplePolicy _anchorPolicy = new AnchorSamplePolicy();
    // Whether the last frame this component saw was inside a level, so that entering one grants the anchor a
    // fresh measurement round without disturbing an anchor that has already locked.
    private bool _inLevel;

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
        _anchorFallback = false;
        _anchorSettle = 0f;
        _anchorSampled = 0f;
        _anchorLastX = float.NaN;
        _anchorLastY = float.NaN;
        // A re-anchor is a fresh measurement, so it gets a fresh retry budget and its own failure lines: `Restart`
        // forgets the attempt, the failure streak and the lines already written together, so the first line of the
        // new round says "attempt 1" again. A configuration reload or a resolution change is exactly the moment the
        // table is allowed to move, so the schedule that gave up before must not be inherited by it.
        _anchorPolicy.Restart();
        // The camera report is a property of this measurement round, so a round that starts here reports again.
        _loggedAnchorCamera = false;
        _anchorCameraNote = string.Empty;
        // The second source, its cache and the element the rectangle came from belong to the round in the same way: a
        // resolution change or a configuration reload may re-lay the row out, and a cached `RectTransform` from the previous
        // layout is not evidence about the new one.
        ForgetBackgroundFade();
        _loggedBackgroundFade = false;
        _anchorSource.Restart();
        // The resolution baseline is written *here*, and that is the whole of the defect this fixes.
        //
        // `_anchorScreenWidth/_anchorScreenHeight` used to be written in exactly one place: the branch in which the
        // anchor finally locked. An anchor that never locked therefore left both at 0, and `OnGUI` compares them
        // against `Screen` on every repaint; 0 never equals `Screen.width`, so every frame called this method,
        // which restarted the retry budget, cleared the failure latch and rewound the settle state. The budget
        // could then never be spent - all 9,528 failure lines of one recorded expedition read "attempt 1 of 12" -
        // the give-up line was unreachable, and the latch that bounds the logging was cleared four times a second.
        // Baseline and comparison now come from the same moment: a resolution change still re-anchors, which is
        // the one thing this method is *for*, and a measurement round now runs to its own end.
        _anchorScreenWidth = Screen.width;
        _anchorScreenHeight = Screen.height;
    }

    private void Update()
    {
        Plugin.PollHotReload();
        try
        {
            var state = GameStateManager.CurrentStateName;
            bool inLevel = state == eGameStateName.InLevel;
            if (inLevel != _inLevel)
            {
                _inLevel = inLevel;
                // A level is a different scene with its own HUD, so a verdict about the previous level's anchor
                // says nothing about this one: entering it grants the retry budget again and lifts a give-up. An
                // anchor that had already locked is left exactly where it was - only the budget and the verdict
                // about a failed one are reset here, so a table that works never moves for this.
                if (inLevel)
                {
                    _anchorPolicy.Restart();
                    _anchorUnavailable = false;
                    _anchorFallback = false;
                    // A different scene has its own HUD, its own canvas and its own camera: the report belongs to it.
                    _loggedAnchorCamera = false;
                    _anchorCameraNote = string.Empty;
                    // The sampling state of the previous level is not evidence about this one.
                    //
                    // `_anchorSettle` and `_anchorSampled` are *sampling* accumulators, not durations: each attempt adds
                    // one `SampleInterval` (0.25 s) to both, and the lock below fires at "one second still" or at "six
                    // seconds sampled". Carried over from the previous scene, a level that had been measured for a while
                    // without ever locking hands the next one a settle window that is already full - and with
                    // `_anchorLastX/_anchorLastY` still holding the previous scene's pixels, the first sample of the new
                    // level that lands within two pixels of them satisfies both the comparison and the accumulator, so the
                    // table freezes on a rectangle read while that HUD was still coming up. Resetting the four makes the
                    // new level earn its own window: it locks once its own samples agree, or it spends the retry budget
                    // and says so.
                    //
                    // A *locked* anchor is deliberately left alone: a lock is only ever taken from a settled rectangle, and
                    // a resolution change still unlocks it through `OnGUI`'s baseline comparison. `_anchorRect` is not
                    // reset either, for the same reason - it is only ever read while the anchor is locked.
                    _anchorSettle = 0f;
                    _anchorSampled = 0f;
                    _anchorLastX = float.NaN;
                    _anchorLastY = float.NaN;
                    // The previous level's `Background Fade` - and the element the settled rectangle came from - go with it:
                    // a level change destroys that HUD, so the cached `RectTransform` is a dead object and the tracked
                    // source says nothing about the new row.
                    ForgetBackgroundFade();
                    _loggedBackgroundFade = false;
                    _anchorSource.Restart();
                }
            }
            bool visible = inLevel ? Plugin.ShowPanel.Value : Plugin.ShowResultOverlay.Value;
            if (Plugin.ToggleKey.Value != KeyCode.None && Input.GetKeyDown(Plugin.ToggleKey.Value))
            {
                bool resultPage = state == eGameStateName.ExpeditionSuccess || state == eGameStateName.ExpeditionFail;
                if (resultPage)
                {
                    // F8 on a result page hides the settlement board the page is showing, and gives one to a page that is
                    // showing none. Which of the two this press is comes from the display itself - the last repaint's own
                    // "a board was drawn" fact - and never from a flag of the key's own: the override defaults to false, so
                    // a page whose fail-open board the mod had already put up by itself answered "not shown" and the press
                    // that should have hidden it drew the same board again. The show half is still the fail-open override,
                    // so nothing about the page's own gates may hold the board back - held back, never placed, deferred -
                    // and the key is handled even when this game build already binds it to something else: a key that
                    // silently stopped working would leave the player with a blank result page and no way to ask for the
                    // table. The hide half is the same visibility switch the in-level press writes, which is what the
                    // configuration's own description of this key promises on both screens.
                    bool shown = ResultsDisplay.ToggleForcedFallback();
                    visible = shown;
                    Plugin.Logger?.LogInfo($"[DIAG] F8 on a result page: board={(shown ? "shown" : "hidden")}; state={state}, "
                        + $"ShowResults={Plugin.ShowResults.Value}, ShowResultOverlay={Plugin.ShowResultOverlay.Value}, ShowImpactBoard={Plugin.ShowImpactBoard.Value}, "
                        + $"ToggleState={visible}; the answer was read from the board the page was really showing, and the show half "
                        + "depends on no settled title, no settled completion edge, no measured footer and no interaction proof.");
                }
                else
                {
                    visible = !visible;
                    Plugin.PersistToggle(visible);
                    Plugin.Logger?.LogInfo($"[DIAG] F8 visibility: state={state}, requested={visible}, ShowPanel={Plugin.ShowPanel.Value}, ShowResultOverlay={Plugin.ShowResultOverlay.Value}, ShowResults={Plugin.ShowResults.Value}, ShowImpactBoard={Plugin.ShowImpactBoard.Value}.");
                }
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
        // The wheel is the settlement board's own control and IMGUI hands it over as an event of its own, so it is
        // read here - before the Repaint gate every other event type is still refused by - and `Use()`d, so the page
        // under the pointer does not scroll as well. Nothing about the Repaint path below changes: it draws exactly
        // what it drew before, at whatever size the wheel has chosen for the page that is up.
        if (ResultsZoomEvent()) return;
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

    // The settlement board's wheel.
    //
    // A settlement page that is really showing, with the pointer inside the board's own rectangle, is the whole of
    // the trigger: the in-level table keeps the glyph size its own settings give it, and every other page ignores
    // the wheel exactly as it did before this existed. The page is decided from the same state the board itself
    // reads, so the success board and the failure board can only ever move their own preference.
    //
    // Once a notch has landed on the board the event is consumed here - `Use()` and handled - whatever writing the
    // new value does to the layout cache: `ResultsDisplay.ZoomAt` judges the gesture against the rectangle the
    // board was last drawn at and locks the wheel sequence for this frame, so the rest of one gesture cannot leak
    // to the page underneath while the board is being rebuilt at its new size.
    [HideFromIl2Cpp]
    private static bool ResultsZoomEvent()
    {
        Event current = Event.current;
        if (current == null || current.type != EventType.ScrollWheel) return false;
        try
        {
            var state = GameStateManager.CurrentStateName;
            bool succeeded = state == eGameStateName.ExpeditionSuccess;
            if (!succeeded && state != eGameStateName.ExpeditionFail) return false;
            if (!ResultsZoom.Accepts(current.delta.y)) return false;
            if (!ResultsDisplay.ZoomAt(current.mousePosition, succeeded, Time.frameCount)) return false;
            // 固定模式（1.19.45）：滚轮仍然被这块板吃掉（不漏给下面的页面），但它**不改字号**——玩家在 [Results]
            // FixedFontPixels 里指定的像素值就是这块板的字号，滚轮在这里悄悄乘一个倍率正是这个模式不允许的事。
            if (ResultsDisplay.FixedLayoutActive())
            {
                current.Use();
                if (!_loggedFixedWheel)
                {
                    _loggedFixedWheel = true;
                    Plugin.Logger?.LogInfo("[DIAG] settlement board wheel: this page is drawn with the fixed layout the player configured "
                        + "([Results] FixedLayout=true), so the wheel is consumed here and the glyph stays at [Results] FixedFontPixels; "
                        + "turn FixedLayout off to scale the adaptive board with the wheel again.");
                }
                return true;
            }
            ResultPageKind page = ResultsZoom.Page(succeeded);
            float scale = ResultsZoom.Next(Plugin.ResultFontScale(succeeded), current.delta.y);
            bool changed = Plugin.PersistResultFontScale(page, scale);
            if (changed)
                Plugin.Logger?.LogInfo($"[DIAG] settlement board wheel: the {page.ToString().ToLowerInvariant()} page's own font scale is now {scale:0.##}, and the board is rebuilt at that scale inside the strip this page measured - the button below it is never covered.");
            current.Use();
            return true;
        }
        catch (Exception error) { LogWheelOnce(error); return false; }
    }

    private static bool _loggedWheel;
    // 固定模式下滚轮被吃掉但不改字号：这条诊断一局只写一次，免得每滚一格就刷一行。
    private static bool _loggedFixedWheel;

    [HideFromIl2Cpp]
    private static void LogWheelOnce(Exception error)
    {
        if (_loggedWheel) return;
        _loggedWheel = true;
        Plugin.Logger?.LogWarning("[DIAG] the settlement board could not take the mouse wheel and keeps its size: " + error.Message);
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

        // 1.19.26: `_anchorUnavailable` is deliberately not part of this gate. It reports that the round's budget was spent;
        // it may not close the only door to `SampleAnchor`, because the policy's own clock is what decides from then on - the
        // settle cadence while it is measuring, and the low-rate watch when the budget is gone. Gating here as well is what
        // made a `Background Fade` that appeared after the twelve attempts unreachable without F8, a level change or a manual
        // re-anchor. A locked anchor is still never sampled again.
        if (Plugin.AnchorToFlashlight.Value && !_anchorLocked) SampleAnchor(layer, Time.unscaledTime);

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
    // sample that holds still is the one that is frozen.
    //
    // A sample that could not be read at all is a different thing from one that moved: the HUD's own camera
    // answers null for a moment while a level is coming up, which used to be read as "this icon does not
    // exist" and latched the table onto the configured position for the whole session. `_anchorPolicy` owns
    // the difference - a throttled, finite retry that ends either in a lock or in one explicit give-up line -
    // and the configured position is drawn from the first failed sample onward, so the table is never blank
    // while the retries are still running.
    [HideFromIl2Cpp]
    private void SampleAnchor(PlayerGuiLayer layer, float now)
    {
        // Never per frame: a retry delay that is still running, and a budget that is spent, both answer false
        // here, so a HUD that cannot be measured costs a handful of comparisons rather than a poll of the
        // camera sixty times a second.
        if (!_anchorPolicy.Due(now)) return;
        _anchorSampled += SampleInterval;
        // The whole attempt - the two HUD reads, the projection of the icon's own bounds and the verdict - is run by
        // the policy, so a read that *throws* spends one of its twelve measurements instead of leaving the budget
        // untouched. That read used to sit between `Began` and `Failed` here, where an exception skipped the failure
        // altogether: the retry delay never advanced and the HUD was re-read on every settle tick for the whole level,
        // which is the poll this budget exists to prevent.
        AnchorSample sample = _anchorPolicy.Attempt(now, () => ReadAnchor(layer), out bool gaveUp);
        if (!sample.Measured)
        {
            AnchorSampleFailure failure = _anchorPolicy.LastFailure;
            // One line per attempt, and the attempt is what gates it - never the tick. `Due` above already refused every
            // frame that was not an attempt, and `DueToReport` is true exactly once for each attempt this round really
            // took, so these lines follow the retry schedule (0.5 s, 1 s, 2 s and then the 4 s ceiling) and a HUD that
            // answers nothing costs the twelve lines of the budget instead of one per repaint. What they print are the
            // round's own figures - the real attempt number and the retries it still has left - so the log shows the
            // schedule walking towards the budget rather than the same first attempt for ever, which is what the
            // one-shot latch this replaces printed once per re-anchor in a recorded expedition.
            if (_anchorPolicy.DueToReport)
            {
                // 1.19.22: identical warnings are never written twice. `DueToReport` still owes one line per attempt
                // (the one-line-per-attempt contract is unchanged); `FreshWarning` answers whether this attempt's
                // failure differs from the last accounted one, so a HUD whose answer cannot change costs the level
                // two lines - its first failure, printed here with the reason, and the give-up below - never twelve
                // identical warnings. The attempt's line is spent either way, so nothing below can print it twice.
                if (_anchorPolicy.FreshWarning)
                {
                    string threw = string.IsNullOrEmpty(sample.Error) ? string.Empty : ", the read itself threw: " + sample.Error;
                    string camera = string.IsNullOrEmpty(_anchorCameraNote) ? string.Empty : ", " + _anchorCameraNote;
                    string background = _backgroundFadeLookup.Describe();
                    Plugin.Logger.LogWarning($"[DIAG] the flashlight anchor could not be measured yet ({AnchorSamplePolicy.Describe(failure)}{threw}{camera}, attempt {_anchorPolicy.Attempts} of {AnchorSamplePolicy.Budget}, triedBackgroundFade={background}, source=config); the table uses PositionX/PositionY meanwhile and the measurement is retried in {AnchorSamplePolicy.DelayAfter(_anchorPolicy.Failures):0.##}s, {Math.Max(0, AnchorSamplePolicy.Budget - _anchorPolicy.Failures)} retry(s) left.");
                }
                _anchorPolicy.Reported();
            }
            // The configured position is drawn while the retries continue: a HUD that is a moment late must not
            // blank the table, and an anchor that does arrive moves it once instead of never being used.
            _anchorFallback = true;
            if (gaveUp)
            {
                _anchorUnavailable = true;
                // The attempt that spent the last retry writes no failure line of its own: it is already `Abandoned` by
                // the time `DueToReport` is asked above, so this line speaks for it - same reason, same attempt count,
                // plus the waiting that went with it. 1.19.26: this is no longer the last word for the level. The round
                // keeps a low-rate watch (`AnchorSamplePolicy.WatchInterval`), the line says so, and a watch that measures
                // anything restarts the round and puts the table back under the native element. 1.19.27: the reason is
                // followed by the authorization the last attempt was working from (`AnchorCameraChoice.Describe`), so the
                // final line says how the anchor was being measured - a camera proof, the canvas' own geometry, or neither.
                string camera = string.IsNullOrEmpty(_anchorCameraNote) ? string.Empty : ", " + _anchorCameraNote;
                Plugin.Logger.LogWarning($"[DIAG] the flashlight anchor was given up on for this level after {_anchorPolicy.Attempts} attempt(s) and {_anchorPolicy.Waited:0.#}s of waiting ({AnchorSamplePolicy.Describe(_anchorPolicy.LastFailure)}{camera}, triedBackgroundFade={_backgroundFadeLookup.Describe()}, source=config); the table keeps the configured PositionX/PositionY. The HUD is still looked at once every {AnchorSamplePolicy.WatchInterval:0.#}s with no budget behind it, so an element or a camera that arrives later is still measured; a new level, a configuration reload or a resolution change starts a full round again.");
                _anchorPolicy.Reported();
            }
            return;
        }
        // A sample that measured anything ends any give-up: the table is about to move, so the state that said "do not look
        // for the anchor" may not survive it. 1.19.26: the revived round also starts its settle window again - the sampling time
        // the abandoned round spent waiting for a HUD that was not there belongs to that round, and counting it here would lock
        // on the very first sample after the watch found the row, with no stillness behind it. The lock below is what clears the
        // configured fallback.
        //
        // The *stability comparison* is reset with it, and that is the second half of the same fix (independent review F1):
        // `_anchorSettle` and the pixel pair it was measured against are the abandoned round's own evidence. Left standing, a
        // page that had already been still for 0.75 s when the element vanished locked on the very first sample after it came
        // back - a "still" comparison against a rectangle read from an object that no longer exists, at whatever coordinates it
        // had. A revived anchor owes a full second of fresh, consecutive stillness, exactly like a first-ever sample; the source
        // tracker is reset too, so the first sample of the revived round cannot be mistaken for "the same element as before".
        // `_anchorRect` is deliberately left alone: it is only ever read while the anchor is locked, and a locked anchor is never
        // sampled (so it can never be in this branch).
        if (_anchorUnavailable)
        {
            _anchorUnavailable = false;
            _anchorSampled = 0f;
            _anchorSettle = 0f;
            _anchorLastX = float.NaN;
            _anchorLastY = float.NaN;
            _anchorSource.Restart();
        }
        LayoutRect rect = sample.Rect;
        // A sample can come from either native source, so the rectangle one sample describes can jump when the source does: the
        // row's `Background Fade` is 66.65 x 6.45 local units away from the flashlight icon's own edges. That is not drift, and
        // it must not be measured as movement and then locked on the far side of it - the settle window starts again, while
        // `_anchorSampled` keeps running, because it counts the samples this round has spent and not this source.
        if (_anchorSource.Observe(sample.Source))
        {
            _anchorSettle = 0f;
            _anchorLastX = float.NaN;
            _anchorLastY = float.NaN;
        }
        bool still = !float.IsNaN(_anchorLastX) && Mathf.Abs(rect.X - _anchorLastX) < 2f && Mathf.Abs(rect.Y - _anchorLastY) < 2f;
        _anchorSettle = still ? _anchorSettle + SampleInterval : 0f;
        _anchorLastX = rect.X; _anchorLastY = rect.Y;
        _anchorRect = rect;
        if (_anchorSettle >= 1f || _anchorSampled >= 6f)
        {
            _anchorLocked = true;
            _anchorFallback = false;
            _anchorScreenWidth = Screen.width; _anchorScreenHeight = Screen.height;
            // The figure printed is the *sampling* time this round has spent - one `SampleInterval` per attempt - and never
            // wall clock: the retry delays between attempts are not part of it, so a round that waited 39.5 s between
            // twelve samples reads 3.00 s. The attempt count and the words "sampling time" are what make the line say
            // that; `AnchorSamplePolicy.Waited` is the field that measures the waiting itself, and the give-up line is the
            // one that prints it. The source is named on the same line, so "the table locked" can never be read without
            // saying which of the two native elements it locked onto.
            // 1.19.28: the line says *what rectangle* locked, not only which element of the row it came from. "anchor=flashlight"
            // used to be read as "the flashlight icon was measured", and that object is not what this build measures any more: the
            // first source is the icon row's own `PUI_InventoryIconDisplay` layout rectangle, the second is the row's `Background
            // Fade` sprite geometry, and `measuredFrom=` names which of the three answers produced the numbers.
            Plugin.Logger.LogInfo($"[DIAG] flashlight anchor locked after {_anchorPolicy.Attempts} sample(s) and {_anchorSampled:0.00}s of sampling time (not wall clock: the retry delays between samples are not counted) at ({rect.X:0},{rect.Y:0}) size {rect.Width:0}x{rect.Height:0} screen px, anchor={AnchorSourceLadder.Name(sample.Source)}, measuredFrom={AnchorSourceLadder.GeometryName(sample.Source)}, sampled through {(_anchorCameraNote.Length > 0 ? _anchorCameraNote : "the HUD's own camera")}; the table is positioned relative to this and then held.");
        }
    }

    // The HUD read itself: the two native elements, the camera ladder that may prove one of them, the element's own layout
    // geometry that may be projected instead, and the projection of whichever answered. Kept in one place so the attempt around
    // it has a single body to run, and free of any verdict of its own - each miss is a state the HUD passes through on its way up,
    // and a projection that throws is the unreadable rectangle it really is rather than evidence about the row.
    //
    // The camera is no longer a single field. `GuiLayerBase.GetWorldCamera()` is one candidate among several - and on this build it
    // always answers null, as does `m_layerCanvas.worldCamera`, because the HUD's canvas is the CellUI `m_cellUICanvas` and the
    // camera that draws it is that canvas' `m_UICamera`. The one that *wins* is still the first live camera whose culling mask
    // really contains the element's layer (the rule itself lives in `AnchorCameraLadder`): a camera that *would* project the
    // element is not the same thing as a camera that *draws* it, and the old read confounded the two - one recorded expedition's
    // log answered null there for about forty minutes while the element's renderer was present the entire time.
    //
    // The reads that may throw are caught below only where the caught value can *refuse* a measurement - a null sprite, a null
    // canvas, a layer of -1, a `catch { return false; }` inside a projection - and never where it would be a legal answer, because
    // a legal answer is a rectangle this build did not measure. A camera read that throws therefore reaches
    // `AnchorSamplePolicy.Attempt`, spends one of the twelve measurements and is named on the failure line
    // (`, the read itself threw: …`) before the round retries - and since 1.19.28 no read of a *null* camera can become a
    // rectangle at all: the geometry answer refuses a null camera outside a real screen-space overlay canvas.
    [HideFromIl2Cpp]
    private static AnchorSample ReadAnchor(PlayerGuiLayer layer)
    {
        _anchorCameraNote = string.Empty;
        SpriteRenderer sprite = null;
        RectTransform iconRect = null;
        AnchorIconSprite drawn = AnchorIconSprite.Enabled;
        try
        {
            var display = layer != null && layer.Inventory != null ? layer.Inventory.m_iconDisplay : null;
            var status = display != null ? display.FlashLightIcon : null;
            if (status != null)
            {
                // The renderer the ladder reads its layer and its canvas fact from. `Enabled` is never null on this prefab -
                // both renderers are created with it and `SetActiveSprite(bool)` only flips their own `enabled` flags - so
                // this expression always answers `Enabled`; `AnchorFlashlightIcon.FallbackReachable` says so out loud.
                sprite = status.Enabled != null ? status.Enabled : status.Disabled;
                bool enabledOn = false, disabledOn = false;
                try { enabledOn = status.Enabled != null && status.Enabled.enabled; } catch { enabledOn = false; }
                try { disabledOn = status.Disabled != null && status.Disabled.enabled; } catch { disabledOn = false; }
                // 1.19.28: which renderer the HUD is *drawing*, from the renderers' own flags. The recorded level had
                // `flashlightActive=false`, i.e. the `_Disabled` sprite was on screen - the object this build used to call
                // "the flashlight icon" and to measure. It is a diagnostic word now, not a source of geometry.
                drawn = AnchorFlashlightIcon.Drawn(enabledOn, disabledOn);
            }
            // 1.19.28: the icon *row's* own layout rectangle. `PUI_InventoryIconDisplay : RectTransformComp` inherits the
            // public `m_rectTrans` field, and that object is what the equipment row really is - 210.59 x 110.16 with its
            // pivot on the right edge. It is NOT the 65 px `SpriteRenderer` object (`FlashLightIcon_:Enabled` /
            // `_Disabled`, 100 x 100 laid out, 32.5 world units of sprite at 2 pixels per unit) that
            // `renderer.transform.TryCast<RectTransform>()` used to answer, and that rectangle is what this build must stop
            // treating as the anchor (`AnchorElementNames` + `AnchorSourceLadder.GeometryName` say what is measured now).
            if (display != null)
            {
                iconRect = display.m_rectTrans;
                if (iconRect == null) iconRect = display.transform.TryCast<RectTransform>();
            }
        }
        catch { sprite = null; iconRect = null; }
        Canvas canvas = null;
        try { canvas = layer != null && layer.GuiLayerBase != null ? layer.GuiLayerBase.m_layerCanvas : null; }
        catch { canvas = null; }
        RectTransform rowElement = BackgroundFade(layer);
        Renderer background = BackgroundFadeRenderer(layer);
        // 1.19.32: the object that really composites this HUD, read once per attempt: `GuiLayerBase.m_cellUICanvas` (a `UI_Canvas`,
        // not a `Canvas`), its `m_UICamera` and its `m_rectTransform`. Both elements are asked whether they live under that root,
        // so the answer is per element and never a scene-wide guess. See `AnchorManualCanvasFacts`.
        CellUiRead cellUi = CellUiRead.Read(layer);
        // 1.19.30: the nearest ancestor `Canvas` of each element - the object that really composites it. `m_layerCanvas` is null
        // for this HUD's whole life, so the render mode used to come from `GuiLayerBase.CanvasRenderMode` (an enum on the layer)
        // and the geometry camera from `m_cellUICanvas.m_UICamera` only. Both facts are read from the element's own canvas first
        // now (`AnchorElementCanvasPolicy`), because that is the canvas `RectTransformUtility.WorldToScreenPoint` documents a
        // camera for - and the only one that can make a null camera legitimate (a real screen-space overlay canvas).
        // Resolved to the *root* of that canvas hierarchy: Unity composites a nested canvas with its root's mode and camera, and a
        // nested canvas' own `worldCamera` is allowed to be null.
        //
        // 1.19.32: on this game's HUD that walk answers null for both elements - the HUD carries no uGUI `Canvas` at all - and the
        // manual canvas read above is then the authority instead (`AnchorElementCanvasPolicy.Project`'s six-argument form).
        Canvas iconCanvas = RootCanvas(AncestorCanvas(sprite));
        Canvas rowCanvas = RootCanvas(AncestorCanvas(background));
        AnchorCanvasMode mode = CanvasMode(layer, canvas, iconCanvas != null ? iconCanvas : rowCanvas);
        var candidates = new List<AnchorCameraCandidate>(8);
        List<Camera> cameras = Candidates(layer, canvas, candidates, sprite, background);
        // 1.19.31: there is no shared "geometry camera" any more. Each element's rectangle is projected by that element's own
        // canvas camera (or with no camera at all on a real overlay canvas), decided inside `MeasureElement` from the same
        // `Canvas` this attempt already resolved for it - so the mode and the camera can never come from two different canvases.
        // The two native sources, in the order this build prefers them, each measured on its own:
        //
        //   A. the icon row's own layout rectangle (`m_iconDisplay.m_rectTrans`), the element the table has always hung under.
        //      Its `PositionX/PositionY/OffsetX/OffsetY` arithmetic is untouched;
        //   B. the same equipment row's own `Background Fade` - the sibling AoiYuki's reference mods read out of
        //      `m_iconDisplay` - used *only* when A could not be measured at all. It is reported as itself, never as the row.
        //
        // Both are measured the same way round: the camera ladder first (a camera whose culling mask really contains the
        // element's layer still wins, unchanged), and - when none can be shown to draw it - the element's own geometry projected
        // by the camera its own canvas is composited by, or without one when that canvas is a genuine overlay canvas. 1.19.32: an
        // element with no ancestor canvas at all is projected by the HUD's own manual canvas instead, and only once every fact of
        // that chain has been read and matches the window (`AnchorManualCanvasFacts`); otherwise the round refuses as it did.
        AnchorElementMeasurement icon = MeasureElement(sprite, cameras, candidates, mode, iconRect, layoutIsGeometry: true, iconCanvas, cellUi);
        AnchorElementMeasurement row = MeasureElement(background, cameras, candidates, mode, rowElement, layoutIsGeometry: false, rowCanvas, cellUi);
        AnchorElementSource source = AnchorSourceLadder.First(icon.Measured, row.Measured);
        AnchorElementMeasurement decided = icon.Measured ? icon : row.Measured ? row : icon.Present ? icon : row;
        // 1.19.29: whether *this attempt* really produced a rectangle. Everything the log says about the source is derived from
        // it, so a rule the ladder merely authorized can never be printed as if it had answered (the recorded
        // `anchorSource=canvas-geometry` beside two `gate=refused`s and `iconSource=config`).
        bool measuredAnything = icon.Measured || row.Measured;
        // The note the failure and give-up lines print is the same authorization, told whether it answered: a refusal keeps its
        // reason, an answer keeps its sentence.
        _anchorCameraNote = AnchorCameraChoice.Describe(decided.Choice, decided.Measured);
        bool targetTexture = false;
        try
        {
            if (decided.Choice.Index >= 0 && decided.Choice.Index < candidates.Count)
                targetTexture = candidates[decided.Choice.Index].TargetTexture;
        }
        catch { targetTexture = false; }
        ReportCamera(decided.Choice, candidates, candidates.Count, mode, icon.Layer >= 0 ? icon.Layer : row.Layer,
            canvas, layer, source, measuredAnything,
            Facts(AnchorElementNames.Flashlight, icon, iconRect, drawn), Facts(AnchorElementNames.BackgroundFade, row, rowElement, drawn),
            targetTexture, icon, row, cameras, candidates, iconCanvas, rowCanvas);
        // Neither element exists on this HUD at all: that is the one case the log calls "no renderer". Everything else is a
        // projection that could not be shown, and the caller keeps the configured position and says why.
        if (!icon.Present && !row.Present) return AnchorSample.Missing(AnchorSampleFailure.NoIcon);
        if (icon.Measured) return AnchorSample.Of(icon.Rect, AnchorElementSource.Flashlight);
        if (row.Measured)
        {
            // Said once per measurement round, and only when the fallback is really used: the row's background is not the
            // icon row, and a reader of the log has to be able to see which of the two the table hung under - and that
            // the offset settings were left exactly as they were.
            if (!_loggedBackgroundFade)
            {
                _loggedBackgroundFade = true;
                // 1.19.29: the icon row is unmeasured here, so its authorization is printed as the refusal it was - the
                // single-argument sentence claimed a projection this branch exists because of *not* having.
                string reason = icon.Present
                    ? AnchorSamplePolicy.Describe(icon.Failure) + ", " + AnchorCameraChoice.Describe(icon.Choice, icon.Measured)
                    : "the icon row's layout rectangle could not be read at all";
                Plugin.Logger?.LogWarning($"[DIAG] the anchor could not be measured from the icon row ({reason}); anchoring on the same HUD "
                    + $"row's Background Fade at ({row.Rect.X:0},{row.Rect.Y:0}) size {row.Rect.Width:0}x{row.Rect.Height:0} screen px, "
                    + $"source={AnchorSourceLadder.Name(AnchorElementSource.BackgroundFade)}, measuredFrom={AnchorSourceLadder.GeometryName(AnchorElementSource.BackgroundFade)}"
                    + $" - this is the row's background SpriteRenderer, not the icon row's layout rectangle, and "
                    + "PositionX/PositionY/OffsetX/OffsetY keep the meaning they always had.");
            }
            return AnchorSample.Of(row.Rect, AnchorElementSource.BackgroundFade);
        }
        AnchorSampleFailure failure = icon.Present ? icon.Failure : row.Failure;
        return AnchorSample.Missing(failure == AnchorSampleFailure.None ? AnchorSampleFailure.Unmeasurable : failure);
    }

    // 1.19.31: there is no `GeometryCamera`/`CameraOf` any more. Both of them answered "which camera may project the canvas
    // geometry" from the ladder's candidate list, i.e. from cameras that had been offered for *the HUD in general* - and one of
    // them (the CellUI canvas' `m_UICamera`) is exactly the camera the recorded level shows was not alive. The geometry answer is
    // now projected by the element's own canvas or not at all (`AnchorElementCanvasPolicy`, used in `MeasureElement`), so there is
    // no second place where a camera could be chosen for it.

    // One native element's attempt: its own layer, whether a canvas really draws it, the camera ladder applied to those two facts
    // (with this element's own canvas mode), and the projection of its own geometry through the camera its own canvas names - or
    // with no camera at all on a genuine overlay canvas. The two elements share this body on purpose - two camera ladders would be
    // two answers to one question - but each one is authorized and projected with its *own* canvas, never with the other's.
    private readonly struct AnchorElementMeasurement
    {
        public readonly bool Present;
        public readonly int Layer;
        public readonly bool CanvasDrawn;
        public readonly AnchorCameraChoice Choice;
        public readonly bool Measured;
        public readonly LayoutRect Rect;
        public readonly AnchorSampleFailure Failure;
        // 1.19.28: the numbers this element's projection produced - the layout rectangle it read, the camera that projected it,
        // the raw window rectangle before the gate, and the gate's own word. Kept on the measurement (and not recomputed in
        // the log) so the one diagnostic line can print the figures the decision was really taken from, including the
        // refused ones. `null` when nothing was attempted.
        public readonly string Geometry;
        // 1.19.32: the manual-canvas chain as this element saw it - whether it really lives under
        // `GuiLayerBase.m_cellUICanvas.m_rectTransform`, whether that canvas' camera draws its layer, and the two pixel grids. Carried
        // on the measurement so the one-shot line prints the reads the decision was taken from instead of re-reading them.
        public readonly AnchorManualCanvasFacts Manual;
        // 1.19.35: whether that chain was read at all. `Manual` is the default struct both when the chain was read and found
        // absent (`m_cellUICanvas == null`) and when it was never read - a missing renderer returns before `Facts` - and the
        // line used to print `absent` for both, which claims a read of the HUD that never happened. `not-observed` is now its
        // own word.
        public readonly bool ManualObserved;

        public AnchorElementMeasurement(bool present, int layer, bool canvasDrawn, AnchorCameraChoice choice, bool measured,
            LayoutRect rect, AnchorSampleFailure failure, string geometry = null, AnchorManualCanvasFacts manual = default,
            bool manualObserved = false)
        {
            Present = present; Layer = layer; CanvasDrawn = canvasDrawn; Choice = choice;
            Measured = measured; Rect = rect; Failure = failure; Geometry = geometry; Manual = manual;
            ManualObserved = manualObserved;
        }
    }

    // One element's attempt, and the one place its geometry becomes window pixels (1.19.28).
    //
    // `element` is the object's own `RectTransform`: the icon row's `m_iconDisplay.m_rectTrans` for the first source and the
    // `Background Fade` object's own transform for the second. `layoutIsGeometry` says what that rectangle *is*: for the icon row
    // it is the drawn rectangle (Unity laid the row out and its background, its two sprite slots and its own pivot are all
    // inside it), while `Background Fade`'s layout rectangle (300 x 109.92) is not what its sliced sprite draws (250 x 30), so
    // for that element the sprite quad stays the geometry and the layout rectangle is only reported.
    //
    // The camera is decided by this element's *own* canvas and by nothing else (`AnchorElementCanvasPolicy`): a genuine overlay
    // canvas is composited in window pixels and must be projected with a `null` camera, a screen-space-camera canvas must be
    // projected with exactly that canvas' `worldCamera`, and anything else (world-space, an unreadable mode, a camera that is
    // switched off or renders into a RenderTexture) has no rectangle at all - the caller then keeps `PositionX/PositionY` rather
    // than a camera somebody else was using. `mode` is only the caller's fallback for a hierarchy that carries no canvas.
    //
    // 1.19.32: this HUD's hierarchy carries no `Canvas` at all - the recorded Test levels read `*ElementCanvas=missing` for both
    // elements while both `RectTransform`s were perfectly readable - so the fifth authority is the HUD's own *manual* canvas
    // (`GuiLayerBase.m_cellUICanvas : UI_Canvas`), whose `m_UICamera` renders that HUD into a texture of the window's own size. Its
    // chain is read per element and per attempt (`CellUiRead.Facts`) and it authorizes a projection only when every fact of it
    // matches this window; otherwise the element refuses with the one fact that was missing.
    [HideFromIl2Cpp]
    private static AnchorElementMeasurement MeasureElement(Renderer renderer, List<Camera> cameras,
        List<AnchorCameraCandidate> candidates, AnchorCanvasMode mode, RectTransform element, bool layoutIsGeometry,
        Canvas elementCanvas, CellUiRead cellUi)
    {
        if (renderer == null)
            return new AnchorElementMeasurement(false, -1, false, AnchorCameraChoice.Unusable("this HUD has no such renderer"),
                false, default, AnchorSampleFailure.NoIcon, "rect=none");
        int layer = -1;
        bool canvasDrawn = false;
        try
        {
            layer = renderer.gameObject.layer;
            // Whether Unity's own canvas draws this object. It decides the camera-less *overlay* rule and the
            // screen-space-camera branch of the ladder, and it is deliberately NOT the gate for the canvas-geometry rule:
            // both of this build's anchors are the prefab's own `SpriteRenderer`s and neither carries a `CanvasRenderer`, so
            // this is false on every frame of every level - which is exactly the reading that used to be mistaken for the
            // anchor being unmeasurable (1.19.27).
            canvasDrawn = renderer.gameObject.GetComponent<CanvasRenderer>() != null;
        }
        // Caught into the *refusing* values only - no layer, no CanvasRenderer fact - and each of them can only make the ladder
        // answer less: a layer of -1 proves no camera at all. The element's own `RectTransform` is read before this point and
        // is never replaced by a fallback here: a rectangle nobody could read must not become one.
        catch { layer = -1; canvasDrawn = false; }
        // 1.19.31: the *authorization* is asked with this element's own canvas mode, not with a mode shared with the other element:
        // the icon row and the row's `Background Fade` may sit under different canvases, and one of them being overlay must not
        // authorize the other, nor may a world-space canvas be authorized as "screen-space" on the other one's behalf.
        AnchorCanvasMode elementMode = AnchorElementCanvasPolicy.Mode(elementCanvas != null, CanvasModeOf(elementCanvas), mode);
        // 1.19.32: the manual canvas' chain, for *this* element: its own layer, its own ancestry, and the two pixel grids.
        AnchorManualCanvasFacts manual = cellUi.Facts(element, layer);
        AnchorCameraChoice choice = AnchorCameraLadder.Select(candidates, layer, elementMode, canvasDrawn, element != null, out int inspected);
        // 1.19.35: the ladder's *last word* on this HUD rests on `elementMode`, and with no ancestor `Canvas` that is the
        // layer's own render-mode property - a wish, not a fact about how anything composites the element. It answered
        // `screen-space-camera`, so the geometry rule was refused for the lack of a camera and the manual canvas was never
        // consulted at all: the recorded levels refused every attempt while the chain that really composites this HUD was
        // never even asked. A *verified* manual chain is the stronger fact and now authorizes the attempt
        // (`AnchorElementCanvasPolicy.ManualAuthorized`); an element under a real `Canvas`, and a chain that is not
        // verified, are refused exactly as before, and the projected rectangle still has to pass `ScreenBounds`.
        if (!choice.Usable && AnchorElementCanvasPolicy.ManualAuthorized(elementCanvas != null, manual))
            choice = AnchorCameraChoice.ManualCanvas();
        if (!choice.Usable)
        {
            // 1.19.35: the note says what was *not* looked at. The manual chain was read for both elements on this attempt,
            // and when it exists but cannot be shown to answer in this window's pixels, its own refusal is the fact a reader
            // needs - not the generic "the ladder answered no projection".
            string note = elementCanvas == null && manual.CanvasPresent
                ? "the ladder answered no projection, and this HUD's own manual canvas could not be shown to answer in this window's pixels either: "
                    + AnchorManualCanvasPolicy.Refusal(manual)
                : "the ladder answered no projection";
            return new AnchorElementMeasurement(true, layer, canvasDrawn, choice, false, default, AnchorSampleFailure.NoProjection,
                GeometryNote(element, null, default, "not-attempted", note), manual, manualObserved: true);
        }
        LayoutRect rect = default;
        AnchorScreenRect raw = default;
        bool measured = false;
        Camera used = null;
        string why = null;
        if (choice.Projection == AnchorProjection.Camera && choice.Index >= 0 && choice.Index < cameras.Count)
        {
            Camera camera = cameras[choice.Index];
            used = camera;
            if (layoutIsGeometry)
                measured = TryScreenRectFromLayout(element, camera, out rect, out raw, out why);
            // The renderer's own bounds first - a sprite is world geometry and its own bounds describe it - and only when those
            // cannot describe the drawn quad (an inactive `SpriteRenderer`) is the quad rebuilt from the sprite's own geometry.
            else measured = TryScreenRect(renderer, camera, out rect, out raw) || TryScreenRectFromSprite(renderer, camera, out rect, out raw);
        }
        if (!measured && choice.Projection == AnchorProjection.CameraLessOverlay)
            measured = TryScreenRectWithoutCamera(renderer, out rect);
        // 1.19.27's second authorization. The rectangle is this element's own four corners, and 1.19.31 hands the projection to
        // this element's own canvas and to nothing else (`AnchorElementCanvasPolicy`): `Overlay` means the `null` camera the
        // overlay canvas is composited with, `ElementCanvas` means exactly that canvas' `worldCamera`, and anything else has no
        // rectangle. A camera the ladder happened to accept is deliberately not substituted - it is a different object, it cannot
        // speak for this canvas, and on an overlay canvas passing one would project pixel coordinates a second time.
        if (!measured && choice.Projection == AnchorProjection.Rect)
        {
            Camera canvasCamera = null;
            bool canvasCameraPresent = false;
            try
            {
                if (elementCanvas != null) { canvasCamera = elementCanvas.worldCamera; canvasCameraPresent = canvasCamera != null; }
            }
            catch { canvasCamera = null; canvasCameraPresent = false; }
            AnchorElementCanvasPolicy.CameraOrigin origin = AnchorElementCanvasPolicy.Project(
                elementCanvas != null, elementMode, canvasCameraPresent, CameraAlive(canvasCamera), CameraWindowCamera(canvasCamera), manual);
            Camera projection = AnchorElementCanvasPolicy.PassesCamera(origin) ? canvasCamera : null;
            // 1.19.32: the manual canvas answers a different question - it is not a `Canvas`, and its camera is therefore never
            // reached through `Canvas.worldCamera`. It is used only where the canvas rule found no camera to name at all, so the
            // two authorities can never be mixed into one argument.
            if (origin == AnchorElementCanvasPolicy.CameraOrigin.ManualCanvas) projection = cellUi.Camera;
            used = projection;
            bool projectable = AnchorCanvasProjection.CameraLegitimate(projection != null, elementMode)
                && AnchorElementCanvasPolicy.CanProject(origin);
            if (!projectable)
                why = origin == AnchorElementCanvasPolicy.CameraOrigin.ManualCanvasRefused
                    ? "the element has no ancestor Canvas, and this HUD's own manual canvas could not be shown to answer in this"
                        + " window's pixels either: " + AnchorManualCanvasPolicy.Refusal(manual)
                    : "the element's canvas is " + AnchorCameraModeName(elementMode)
                        + " (mode from " + AnchorElementCanvasPolicy.SourceName(elementCanvas != null)
                        + ", camera: " + AnchorElementCanvasPolicy.Name(origin)
                        + "), so there is nothing that may project its rectangle: a null camera is only the identity projection inside"
                        + " a real screen-space overlay canvas, and a screen-space-camera canvas needs its own live window camera";
            else
            {
                if (layoutIsGeometry) measured = TryScreenRectFromLayout(element, projection, out rect, out raw, out why);
                else measured = TryScreenRectFromSprite(renderer, projection, out rect, out raw);
            }
        }
        string geometry = GeometryNote(element, used, raw, measured ? "ok" : "refused", why);
        if (!measured)
            return new AnchorElementMeasurement(true, layer, canvasDrawn, choice, false, default, AnchorSampleFailure.Unmeasurable,
                geometry, manual, manualObserved: true);
        return new AnchorElementMeasurement(true, layer, canvasDrawn, choice, true, rect, AnchorSampleFailure.None, geometry, manual,
            manualObserved: true);
    }

    // The numbers one attempt read, as the diagnostic line prints them (1.19.28). Every value was already read to *make* the
    // decision, so this can never move the anchor, and nothing here is a claim about whether the rectangle is usable - that is
    // the gate's own word at the end of the segment.
    //
    // It exists because the 1.19.27 log could say "no projection" and nothing else: a rectangle that was merely off the window
    // and one built out of NaN read identically, and so did "the RectTransform was never found" and "the camera was missing".
    [HideFromIl2Cpp]
    private static string GeometryNote(RectTransform element, Camera camera, AnchorScreenRect raw, string gate, string why)
    {
        string rect = "rect=none";
        if (element != null)
        {
            string read;
            try
            {
                Rect local = element.rect;
                Vector2 pivot = element.pivot;
                Vector3 position = element.position;
                read = "rect=(" + Number(local.xMin) + "," + Number(local.yMin) + "," + Number(local.width) + "," + Number(local.height) + ")"
                     + ",pivot=(" + Number(pivot.x) + "," + Number(pivot.y) + ")"
                     + ",position=(" + Number(position.x) + "," + Number(position.y) + "," + Number(position.z) + ")";
            }
            catch { read = "rect=unreadable"; }
            rect = read;
        }
        string used = camera == null ? "none" : (string.IsNullOrEmpty(camera.name) ? "unnamed" : camera.name);
        return "measuredFrom=" + rect + ",projectCamera=" + used + ",screen=" + raw.Describe() + ",gate=" + gate
            + (string.IsNullOrEmpty(why) ? string.Empty : "(" + why + ")");
    }

    [HideFromIl2Cpp]
    private static string Number(float value)
        => float.IsFinite(value) ? value.ToString("0.##") : (float.IsNaN(value) ? "NaN" : (value > 0f ? "+inf" : "-inf"));

    // The element's own `RectTransform`, as the window rectangle its four corners cover (1.19.28).
    //
    // `RectTransform.rect` is already relative to the element's pivot, and `TransformPoint` already carries every scale in the
    // chain (the element's own, its parents', and the canvas'), so projecting the four corners is the whole conversion - no
    // "pixels per local unit" number has to be invented, and a rotated element is as correct as an axis-aligned one. The camera
    // is the canvas' own or a proven one; `null` is passed only when the canvas is a real overlay canvas (`CameraLegitimate`),
    // in which case `RectTransformUtility` composites the corner in window pixels without a camera, exactly as Unity does.
    //
    // The corners are projected through `ProjectPoint` 1.19.32, which is `RectTransformUtility.WorldToScreenPoint(null, ...)` for
    // the overlay answer and the projection camera's own `WorldToScreenPoint` otherwise: those two are the same call when the camera
    // is a `Canvas.worldCamera` (that is all `RectTransformUtility` does with a non-null camera), and the manual canvas is not a
    // `Canvas` at all - its camera is the object that renders those corners, so it is the object that is asked.
    [HideFromIl2Cpp]
    private static bool TryScreenRectFromLayout(RectTransform element, Camera camera, out LayoutRect rect,
        out AnchorScreenRect raw, out string why)
    {
        rect = default;
        raw = default;
        why = null;
        if (element == null) { why = "the element carries no RectTransform"; return false; }
        Rect local;
        try { local = element.rect; } catch { why = "RectTransform.rect threw"; return false; }
        if (!float.IsFinite(local.xMin) || !float.IsFinite(local.xMax) || !float.IsFinite(local.yMin) || !float.IsFinite(local.yMax))
        { why = "the declared rect is not a number"; return false; }
        var xs = new List<float>(4);
        var ys = new List<float>(4);
        for (int corner = 0; corner < 4; corner++)
        {
            Vector3 world = element.TransformPoint(new Vector3(
                (corner & 1) == 0 ? local.xMin : local.xMax,
                (corner & 2) == 0 ? local.yMin : local.yMax, 0f));
            Vector2 screen = ProjectPoint(camera, world);
            if (!float.IsFinite(screen.x) || !float.IsFinite(screen.y)) { why = "a projected corner is not a number"; return false; }
            xs.Add(screen.x);
            // The canvas' y grows upwards and the window's grows downwards, so the corner's window y is the mirrored one.
            ys.Add(Screen.height - screen.y);
        }
        bool usable = AnchorRectProjection.TryFromCorners(xs, ys, Screen.width, Screen.height, out rect, out raw);
        if (!usable && string.IsNullOrEmpty(why)) why = "ScreenBounds refused the rectangle";
        return usable;
    }

    // 1.19.32: one corner, one projection. A `null` camera is Unity's camera-less overlay projection and is only ever passed for a
    // real overlay canvas; every other answer has a camera, and that camera - not a canvas lookup - is what projects the corner.
    // For a `Canvas.worldCamera` the two calls are the same call (`RectTransformUtility.WorldToScreenPoint` forwards to
    // `Camera.WorldToScreenPoint` the moment its camera is not null), so this changes nothing for an element under a real canvas; it
    // matters for the manual canvas, whose camera renders those corners into a texture the size of the window and which therefore
    // is the only object that can say where they land.
    [HideFromIl2Cpp]
    private static Vector2 ProjectPoint(Camera camera, Vector3 world)
    {
        if (camera == null) return RectTransformUtility.WorldToScreenPoint(null, world);
        Vector3 screen = camera.WorldToScreenPoint(world);
        return new Vector2(screen.x, screen.y);
    }

    [HideFromIl2Cpp]
    // What one of the two native elements answered on an attempt, in the words the camera line prints. Nothing here can move
    // the anchor: every value was already read to decide, and the line only says what was seen.
    //
    // 1.19.28 adds the two facts the previous round could not show: the element's own `RectTransform` numbers (its declared
    // `rect`, its `pivot` and its world `position`) - because "the RectTransform was found" said nothing about the rectangle it
    // declares - and which of the two flashlight sprites the HUD is drawing.
    private static AnchorElementFacts Facts(string name, AnchorElementMeasurement measurement, RectTransform element, AnchorIconSprite drawn)
    {
        bool active = false, hasRectTransform = false;
        float width = float.NaN, height = float.NaN;
        string layout = "none";
        try
        {
            // `activeInHierarchy` is asked of the element's own object: the row's `Background Fade` is switched off in the prefab,
            // so `false` here is a property of the prefab and not a reason the element cannot be measured.
            if (element != null)
            {
                GameObject target = element.gameObject;
                active = target.activeInHierarchy;
                hasRectTransform = true;
            }
            if (element != null)
            {
                Rect local = element.rect;
                Vector2 pivot = element.pivot;
                layout = "(" + Number(local.xMin) + "," + Number(local.yMin) + "," + Number(local.width) + "," + Number(local.height) + ")"
                       + "/pivot(" + Number(pivot.x) + "," + Number(pivot.y) + ")";
            }
            Renderer renderer = null;
            try { renderer = element != null ? element.GetComponent<Renderer>() : null; } catch { renderer = null; }
            if (renderer != null)
            {
                Bounds bounds = renderer.bounds;
                width = bounds.size.x;
                height = bounds.size.y;
            }
        }
        catch { active = false; hasRectTransform = false; width = float.NaN; height = float.NaN; layout = "unreadable"; }
        return new AnchorElementFacts(name, measurement.Present, measurement.Layer, measurement.CanvasDrawn,
            hasRectTransform, active, width, height, layout, drawn);
    }

    // The row's own `Background Fade`, found by a recursive walk of the one `PUI_InventoryIconDisplay` this inventory was
    // given (inactive children included - the prefab switches this object off), matching the exact name AoiYuki's mods
    // match. The walk is deliberately scoped to that display: `resources.assets` holds three objects called
    // `Background Fade`, and only the one under this display is the equipment row's. No scene scan ever happens.
    //
    // A hit is cached for the life of the display. A *miss* is not: it is a statement about a HUD that may still be coming
    // up, and this lookup is asked on the very first sampling attempt of a level. 1.19.26 fixes the recorded level that
    // logged `background-fade=missing` on attempt 1 and then answered `yes(missing)` from the cache for the rest of the
    // expedition, while the row - and with it the element this build falls back to - appeared a moment later:
    // `AnchorElementLookup` gives a miss `MissWalks` walks on the caller's own attempts, `RetryFrames` frames apart, and
    // retires it only when that budget is spent. Nothing here walks per frame, and the walk is still a single
    // `GetComponentsInChildren` of one display.
    //
    // A walk that *threw* is not a miss: it says nothing about whether the element is there. The cache records it as
    // unreadable, which is never answered from the cache - the next attempt walks again on the anchor's own bounded cadence
    // (one walk per retry tick, never a loop of its own), and the failure is named once per measurement round rather than once
    // per attempt. Caching it as a miss is what turned one transient exception into "this level has no Background Fade".
    private static RectTransform BackgroundFade(PlayerGuiLayer layer)
    {
        Transform owner = null;
        try
        {
            owner = layer != null && layer.Inventory != null && layer.Inventory.m_iconDisplay != null
                ? layer.Inventory.m_iconDisplay.transform
                : null;
        }
        catch { owner = null; }
        if (owner == null)
        {
            ForgetBackgroundFade();
            return null;
        }
        // A cached hit whose element Unity has since destroyed is not a hit any more: the display survived a HUD rebuild the
        // row did not. Forgetting it costs one walk, and a walk that misses again spends the miss budget like any other.
        if (_backgroundFade == null && _backgroundFadeLookup.Hit) _backgroundFadeLookup.Forget();
        if (!_backgroundFadeLookup.Due(owner, Time.frameCount)) return _backgroundFade;
        _backgroundFade = null;
        bool walked = false;
        try
        {
            var all = owner.GetComponentsInChildren<RectTransform>(true);
            walked = true;
            if (all != null)
                for (int index = 0; index < all.Length; index++)
                {
                    RectTransform candidate = all[index];
                    if (candidate == null) continue;
                    // 1.19.28: the *prefab's* name - `AnchorElementNames.BackgroundFadeObject` - and never the log token
                    // `AnchorElementNames.BackgroundFade` ("background-fade"). That one word was this build's whole
                    // `background-fade=missing`: the walk compared a display name against the word used in `[DIAG]` lines, so it
                    // could not hit on any frame of any level, nothing threw, and the failed-lookup line never printed. The
                    // comparison now lives in `AnchorElementMatch`, which the regression suite drives directly.
                    if (!AnchorElementMatch.IsBackgroundFade(candidate.name)) continue;
                    _backgroundFade = candidate;
                    break;
                }
        }
        catch (Exception error)
        {
            _backgroundFade = null;
            walked = false;
            if (!_loggedBackgroundFadeLookupError)
            {
                _loggedBackgroundFadeLookupError = true;
                Plugin.Logger?.LogWarning("[DIAG] the same HUD row's Background Fade could not be looked up - the walk of the icon "
                    + "display threw: " + error.Message + ". The exact flashlight icon is still measured on every attempt, the cache "
                    + "does not remember this as a miss, and the walk is retried on the anchor's own schedule.");
            }
        }
        _backgroundFadeLookup.Record(owner, _backgroundFade != null, walked);
        return _backgroundFade;
    }

    [HideFromIl2Cpp]
    private static Renderer BackgroundFadeRenderer(PlayerGuiLayer layer)
    {
        RectTransform element = BackgroundFade(layer);
        if (element == null) return null;
        try { return element.GetComponent<Renderer>(); } catch { return null; }
    }

    // The cached element belongs to the HUD that was up when it was found: a level change destroys it, and a resolution or
    // configuration change may re-lay the row out. Dropped wherever the anchor's own state is dropped, and nowhere else.
    [HideFromIl2Cpp]
    private static void ForgetBackgroundFade()
    {
        _backgroundFade = null;
        _backgroundFadeLookup.Forget();
        // The one line that names a failed walk belongs to the round as well, so a level whose walk threw once reports again
        // in the next level instead of staying silent for the rest of the session.
        _loggedBackgroundFadeLookupError = false;
    }

    // 1.19.30: the mode the element is really composited in.
    //
    // The element's own ancestor `Canvas` is asked first, because it is a real component whose `renderMode` Unity itself obeys
    // for that element; `m_layerCanvas` is null for this HUD's whole life, so the second read (`GuiLayerBase.CanvasRenderMode`)
    // is only a fallback for a hierarchy that carries no `Canvas` at all, and `Unknown` is still not `Overlay`: nothing is then
    // known, so only a validated camera may be used. `AnchorElementCanvasPolicy.Mode` is the decision itself, so the three cases
    // are asserted headlessly rather than inferred from a log line.
    [HideFromIl2Cpp]
    private static AnchorCanvasMode CanvasMode(PlayerGuiLayer layer, Canvas canvas, Canvas elementCanvas)
    {
        AnchorCanvasMode fallback = AnchorCanvasMode.Unknown;
        try { if (canvas != null) fallback = ModeOf(canvas.renderMode); } catch { }
        if (fallback == AnchorCanvasMode.Unknown)
        {
            try { if (layer != null) fallback = ModeOf(layer.CanvasRenderMode); } catch { }
        }
        AnchorCanvasMode ancestor = AnchorCanvasMode.Unknown;
        bool present = false;
        try { if (elementCanvas != null) { ancestor = ModeOf(elementCanvas.renderMode); present = true; } } catch { present = false; }
        return AnchorElementCanvasPolicy.Mode(present, ancestor, fallback);
    }

    // The render mode of one already-found `Canvas`, or `Unknown` when the read throws - the same rule `ModeOf` applies to a
    // `RenderMode` value, kept in one place so the element's canvas and the layer canvas are read the same way.
    [HideFromIl2Cpp]
    private static AnchorCanvasMode CanvasModeOf(Canvas canvas)
    {
        if (canvas == null) return AnchorCanvasMode.Unknown;
        try { return ModeOf(canvas.renderMode); } catch { return AnchorCanvasMode.Unknown; }
    }

    // "Alive" for a camera this build may project with: enabled and active in the hierarchy, the same two facts `Add` records for
    // every ladder candidate. A read that throws is not alive - it can only make the answer smaller.
    [HideFromIl2Cpp]
    private static bool CameraAlive(Camera camera)
    {
        if (camera == null) return false;
        try { return camera.enabled && camera.isActiveAndEnabled; } catch { return false; }
    }

    // "Answers in window pixels": a camera that renders into a RenderTexture answers in that texture's pixels, and nothing in this
    // build has verified a conversion from them. A read that throws is treated as a texture camera, i.e. refused.
    [HideFromIl2Cpp]
    private static bool CameraWindowCamera(Camera camera)
    {
        if (camera == null) return false;
        try { return camera.targetTexture == null; } catch { return false; }
    }

    // The root of a canvas hierarchy: Unity composites a nested `Canvas` with its root's render mode and camera, and a nested
    // canvas' own `worldCamera` may legitimately be null, so the root is the object that answers both questions. A read that
    // throws keeps the canvas that was found - it is still the nearest real canvas.
    [HideFromIl2Cpp]
    private static Canvas RootCanvas(Canvas canvas)
    {
        if (canvas == null) return null;
        try
        {
            if (canvas.isRootCanvas) return canvas;
            Canvas root = canvas.rootCanvas;
            return root != null ? root : canvas;
        }
        catch { return canvas; }
    }

    [HideFromIl2Cpp]
    private static AnchorCanvasMode ModeOf(RenderMode mode)
    {
        switch (mode)
        {
            case RenderMode.ScreenSpaceOverlay: return AnchorCanvasMode.Overlay;
            case RenderMode.ScreenSpaceCamera: return AnchorCanvasMode.ScreenSpaceCamera;
            case RenderMode.WorldSpace: return AnchorCanvasMode.WorldSpace;
            default: return AnchorCanvasMode.Unknown;
        }
    }

    // Every camera this build can honestly offer, in the ladder's own order: the HUD's world camera, the CellUI canvas' camera,
    // the icon canvas' worldCamera, the element's own ancestor canvas, the game's GUI camera, `Camera.main`, and then every other
    // camera. None of them is trusted on its name alone - `AnchorCameraLadder.Select` only accepts one whose culling mask contains
    // the element's layer.
    [HideFromIl2Cpp]
    private static List<Camera> Candidates(PlayerGuiLayer layer, Canvas canvas, List<AnchorCameraCandidate> candidates,
        SpriteRenderer sprite, Renderer background)
    {
        var cameras = new List<Camera>(8);
        Camera hud = null;
        try { hud = layer != null && layer.GuiLayerBase != null ? layer.GuiLayerBase.GetWorldCamera() : null; }
        catch { hud = null; }
        Add(cameras, candidates, AnchorCameraSource.HudWorldCamera, hud);
        // 1.19.28: the CellUI canvas' own camera - the camera the CellUI canvas *says* it renders with. `GuiLayerBase.m_layerCanvas`
        // (the candidate right below) is null for the whole life of this HUD and `GetWorldCamera()` answers the same null, so the two
        // older candidates can never contain a live camera at all. It is offered under its own source and proven by the same culling
        // mask as every other camera - nothing is taken on trust here, and the recorded level's `alive=3/candidates=4` is exactly why:
        // this camera can be present and switched off, so it is a candidate and never an assumption.
        Camera cellUi = null;
        try
        {
            cellUi = layer != null && layer.GuiLayerBase != null && layer.GuiLayerBase.m_cellUICanvas != null
                ? layer.GuiLayerBase.m_cellUICanvas.m_UICamera
                : null;
        }
        catch { cellUi = null; }
        Add(cameras, candidates, AnchorCameraSource.CellUiCamera, cellUi);
        Camera canvasCamera = null;
        try { canvasCamera = canvas != null ? canvas.worldCamera : null; }
        catch { canvasCamera = null; }
        Add(cameras, candidates, AnchorCameraSource.CanvasWorldCamera, canvasCamera);
        // 1.19.26: the element's own ancestor canvas. `m_layerCanvas` was null on the recorded 1.19.25 frame
        // (`layerCanvas=none`) while the icon was already there, so the only canvas that could name a camera was the one the
        // element really sits under. Offered as a *candidate* under its own source and validated by the same culling-mask rule
        // as every other world sprite: an ancestor's camera is not proof that it draws the element, and nothing here relaxes
        // the sprite/overlay rules - a sprite is still never projected through a null camera.
        Camera elementCanvas = null;
        try { elementCanvas = ElementCanvasCamera(sprite, background); } catch { elementCanvas = null; }
        Add(cameras, candidates, AnchorCameraSource.ElementCanvas, elementCanvas);
        Camera page = null;
        try { page = CM_PageBase.Camera; } catch { page = null; }
        Add(cameras, candidates, AnchorCameraSource.PageCamera, page);
        Camera main = null;
        try { main = Camera.main; } catch { main = null; }
        Add(cameras, candidates, AnchorCameraSource.MainCamera, main);
        try
        {
            var all = Camera.allCameras;
            if (all != null)
                for (int index = 0; index < all.Length; index++) Add(cameras, candidates, AnchorCameraSource.AllCameras, all[index]);
        }
        catch { }
        return cameras;
    }

    // 1.19.32: `cameras.Contains(camera)` is a *reference* comparison, and Il2CppInterop creates a new wrapper for every read of a
    // native object - so `Camera.main` and the same camera out of `Camera.allCameras` were both offered (`#1:main` and `#2:all` with
    // identical mask and depth on the recorded line). De-duplication is by native pointer now, which is the only identity two
    // wrappers of one camera share; an unreadable pointer (a collected object) is never treated as a duplicate, so nothing can be
    // dropped by a read that failed.
    [HideFromIl2Cpp]
    private static void Add(List<Camera> cameras, List<AnchorCameraCandidate> candidates, AnchorCameraSource source, Camera camera)
    {
        if (camera == null) return;
        long pointer = PointerOf(camera);
        if (pointer != 0L)
            for (int index = 0; index < cameras.Count; index++)
                if (AnchorCameraIdentity.Same(PointerOf(cameras[index]), pointer)) return;
        bool alive;
        try { alive = camera.enabled && camera.isActiveAndEnabled; }
        catch { alive = false; }
        int mask = 0, depth = 0;
        bool targetTexture = false;
        try { mask = camera.cullingMask; depth = Mathf.RoundToInt(camera.depth); targetTexture = camera.targetTexture != null; }
        catch { mask = 0; targetTexture = false; }
        cameras.Add(camera);
        // A camera that renders into a RenderTexture draws the HUD somewhere other than the window, and its
        // `WorldToScreenPoint` answers in that texture's pixels. It is offered to the ladder as what it is (a candidate that
        // can only be used once a conversion to window pixels has been verified - which none has), never silently treated as
        // a window camera; see `AnchorCameraLadder.Select`.
        candidates.Add(new AnchorCameraCandidate(source, alive, mask, depth, targetTexture));
    }

    // 1.19.32: one camera's native pointer, or 0 when it cannot be read (a collected wrapper, or a read that threw). `0` is the
    // "unknown" answer and never compares equal to anything - see `AnchorCameraIdentity`.
    [HideFromIl2Cpp]
    private static long PointerOf(Camera camera)
    {
        if (camera == null) return 0L;
        try { return camera.Pointer.ToInt64(); } catch { return 0L; }
    }

    // 1.19.32: every offered camera's native pointer, parallel to the candidate list, so an identity question can be answered by the
    // object itself instead of by the managed wrapper.
    [HideFromIl2Cpp]
    private static long[] PointersOf(List<Camera> cameras)
    {
        if (cameras == null) return new long[0];
        var pointers = new long[cameras.Count];
        for (int index = 0; index < cameras.Count; index++) pointers[index] = PointerOf(cameras[index]);
        return pointers;
    }

    // The `worldCamera` of the nearest ancestor `Canvas` of either native element (1.19.26). The HUD's own layer canvas can be
    // null for a frame while the icon is already there - the recorded 1.19.25 level logged `layerCanvas=none` - and the camera
    // that really draws the element is then only reachable through the element's own hierarchy.
    //
    // The canvas is asked of each level's *component*, with `GetComponent<Canvas>()`, and never cast to: a `Transform` is a
    // different component of the same GameObject, not a kind of `Canvas`, so `transform.TryCast<Canvas>()` asks the wrong
    // component and can only answer null. The `TryCast<RectTransform>()` this build uses elsewhere is not the same thing - a
    // `RectTransform` *is* the `Transform` (a subclass of the very component the walk holds), which is why that downcast works
    // and this one cannot. The walk goes up the parents, bounded, and a level whose object Unity has destroyed is simply the
    // end of the walk. Nothing is guessed: the camera is handed to the ladder as a candidate and must pass the same
    // culling-mask proof as every other world sprite.
    [HideFromIl2Cpp]
    private static Camera ElementCanvasCamera(SpriteRenderer icon, Renderer row)
    {
        Camera camera = AncestorCanvasWorldCamera(icon);
        if (camera != null) return camera;
        return AncestorCanvasWorldCamera(row);
    }

    // 1.19.31: the same canvas identity the geometry answer uses - the *root* of the element's canvas hierarchy - so the candidate
    // the ladder proves and the camera the geometry answer projects with can never be two different objects. A nested canvas'
    // own `worldCamera` may legitimately be null while the root carries one, and resolving to the root is what makes that case
    // work instead of refusing.
    [HideFromIl2Cpp]
    private static Camera AncestorCanvasWorldCamera(Component element)
    {
        Canvas canvas = RootCanvas(AncestorCanvas(element));
        if (canvas == null) return null;
        try { return canvas.worldCamera; } catch { return null; }
    }

    // The nearest ancestor `Canvas` of an element, or null when there is none (1.19.27).
    //
    // Both answers this build can take from a hierarchy need this one object: the camera the canvas is projected through, and
    // the geometry of the element the canvas laid out. It is asked level by level with `GetComponent<Canvas>()` and never cast
    // to - a `Transform` is a sibling component of the canvas, not a kind of it - and the walk is bounded, so a deep or partly
    // destroyed HUD costs at most thirty-two lookups and then answers null.
    [HideFromIl2Cpp]
    private static Canvas AncestorCanvas(Component element)
    {
        Transform node = null;
        try { node = element != null ? element.transform : null; }
        catch { node = null; }
        int guard = 0;
        while (node != null && guard++ < 32)
        {
            Canvas canvas = null;
            try { canvas = node.GetComponent<Canvas>(); } catch { canvas = null; }
            if (canvas != null) return canvas;
            try { node = node.parent; } catch { node = null; }
        }
        return null;
    }

    // 1.19.32: the HUD's own manual canvas, read once per attempt, and the per-element facts that decide whether it may project an
    // element. `GuiLayerBase.m_cellUICanvas` is a `UI_Canvas` - a game MonoBehaviour, not a `Canvas` - so it is never found by the
    // ancestor walk above and never answers `renderMode`; what it has is `m_UICamera`, `m_rectTransform` and its own declared pixel
    // grid (`m_pixelWidth/m_pixelHeight`). Those three, plus the camera's own mask, viewport and texture size, are the whole chain
    // between the HUD's world-space elements and the window - see `AnchorManualCanvasFacts` for the rule and for why `enabled` is
    // deliberately not part of it.
    //
    // Every read is caught into the refusing value and nothing is guessed: a read that throws leaves `m_*` unreadable, which makes
    // `AnchorManualCanvasFacts.Verified` false and the element refuse with the fact that could not be read.
    private readonly struct CellUiRead
    {
        public readonly bool CanvasPresent;
        public readonly Camera Camera;
        public readonly RectTransform Root;
        public readonly float PixelWidth, PixelHeight;
        public readonly int Mask;
        public readonly int PixelWidthOfCamera, PixelHeightOfCamera;
        public readonly float ViewportX, ViewportY, ViewportWidth, ViewportHeight;
        public readonly bool CameraEnabled, CameraActive, CameraOrthographic;
        public readonly float CameraOrthographicSize;

        private CellUiRead(bool canvasPresent, Camera camera, RectTransform root, float pixelWidth, float pixelHeight,
            int mask, int cameraPixelWidth, int cameraPixelHeight,
            float viewportX, float viewportY, float viewportWidth, float viewportHeight,
            bool cameraEnabled, bool cameraActive, bool cameraOrthographic, float cameraOrthographicSize)
        {
            CanvasPresent = canvasPresent; Camera = camera; Root = root;
            PixelWidth = pixelWidth; PixelHeight = pixelHeight; Mask = mask;
            PixelWidthOfCamera = cameraPixelWidth; PixelHeightOfCamera = cameraPixelHeight;
            ViewportX = viewportX; ViewportY = viewportY; ViewportWidth = viewportWidth; ViewportHeight = viewportHeight;
            CameraEnabled = cameraEnabled; CameraActive = cameraActive;
            CameraOrthographic = cameraOrthographic; CameraOrthographicSize = cameraOrthographicSize;
        }

        public static CellUiRead Read(PlayerGuiLayer layer)
        {
            UI_Canvas canvas = null;
            try { canvas = layer != null && layer.GuiLayerBase != null ? layer.GuiLayerBase.m_cellUICanvas : null; }
            catch { canvas = null; }
            if (canvas == null) return default;
            // Named apart from every other camera read in this file on purpose: this one answers "does the manual canvas name a
            // camera", and a read that throws is answered as `missing` - which the line prints and `AnchorManualCanvasPolicy.Refusal`
            // names as the fact that refused, rather than as a state of the HUD. It is not the anchor's own camera read, which may
            // never fold an exception into a null camera (that is what made the thrown-read suffix unreachable in 1.19.21).
            Camera cellUiCamera = null;
            RectTransform root = null;
            float pixelWidth = float.NaN, pixelHeight = float.NaN;
            try { cellUiCamera = canvas.m_UICamera; } catch { cellUiCamera = null; }
            try { root = canvas.m_rectTransform; } catch { root = null; }
            try { pixelWidth = canvas.m_pixelWidth; pixelHeight = canvas.m_pixelHeight; }
            catch { pixelWidth = float.NaN; pixelHeight = float.NaN; }
            int mask = 0, cameraPixelsWide = 0, cameraPixelsHigh = 0;
            float viewportX = float.NaN, viewportY = float.NaN, viewportWidth = float.NaN, viewportHeight = float.NaN;
            bool enabled = false, active = false, orthographic = false;
            float orthographicSize = float.NaN;
            if (cellUiCamera != null)
            {
                try
                {
                    mask = cellUiCamera.cullingMask;
                    cameraPixelsWide = cellUiCamera.pixelWidth;
                    cameraPixelsHigh = cellUiCamera.pixelHeight;
                    Rect viewport = cellUiCamera.rect;
                    viewportX = viewport.x; viewportY = viewport.y; viewportWidth = viewport.width; viewportHeight = viewport.height;
                }
                catch
                {
                    mask = 0; cameraPixelsWide = 0; cameraPixelsHigh = 0;
                    viewportX = viewportY = viewportWidth = viewportHeight = float.NaN;
                }
                try { enabled = cellUiCamera.enabled; } catch { enabled = false; }
                try { active = cellUiCamera.gameObject.activeInHierarchy; } catch { active = false; }
                try { orthographic = cellUiCamera.orthographic; orthographicSize = cellUiCamera.orthographicSize; }
                catch { orthographic = false; orthographicSize = float.NaN; }
            }
            return new CellUiRead(true, cellUiCamera, root, pixelWidth, pixelHeight, mask, cameraPixelsWide, cameraPixelsHigh,
                viewportX, viewportY, viewportWidth, viewportHeight, enabled, active, orthographic, orthographicSize);
        }

        // The chain for one element: whether *this* element really lives under the canvas' root (`Transform.IsChildOf`, which also
        // answers yes for the root itself), and whether the canvas' camera draws *this* element's layer. The two elements are asked
        // apart on purpose - the row's `Background Fade` is switched off in the prefab and the icon row is not, and one of them
        // being inside the canvas is not evidence about the other.
        public AnchorManualCanvasFacts Facts(RectTransform element, int layer)
        {
            if (!CanvasPresent) return AnchorManualCanvasFacts.None;
            bool inside = false;
            try { inside = element != null && Root != null && element.IsChildOf(Root); }
            catch { inside = false; }
            bool draws = Camera != null && AnchorCameraLadder.Renders(Mask, layer);
            return new AnchorManualCanvasFacts(CanvasPresent, Camera != null, inside, layer, draws, Mask,
                PixelWidthOfCamera, PixelHeightOfCamera, PixelWidth, PixelHeight, Screen.width, Screen.height,
                ViewportX, ViewportY, ViewportWidth, ViewportHeight,
                CameraEnabled, CameraActive, CameraOrthographic, CameraOrthographicSize);
        }
    }

    // The one line that says how the anchor was resolved, printed once per measurement round: the render mode of the canvas
    // the icon lives under, the icon's layer, how many cameras were offered and alive, and which one was chosen - or the reason
    // none could be. This is the read the old build never printed, which is why "camera=null" could sit in a log for forty
    // minutes with nothing to compare it against.
    //
    // 1.19.28 makes the line answer the question in one pass, because the previous round's line could not: it now carries the
    // *numbers* every attempt worked from. Each element's own `RectTransform` rectangle, pivot and position; the camera that
    // projected it (by name, or `none`); the raw screen rectangle the projection produced - printed even when it was refused, so
    // off-window and NaN are told apart - and the gate's own word. Plus the CellUI facts (`m_cellUICanvas`, `m_UICamera`, and
    // whether that camera passed the culling-mask proof) that decide whether this build has a camera at all today.
    [HideFromIl2Cpp]
    private static void ReportCamera(AnchorCameraChoice choice, List<AnchorCameraCandidate> candidates, int inspected,
        AnchorCanvasMode mode, int iconLayer, Canvas canvas, PlayerGuiLayer layer, AnchorElementSource source, bool measured,
        AnchorElementFacts flashlight, AnchorElementFacts background, bool targetTexture,
        AnchorElementMeasurement icon, AnchorElementMeasurement row, List<Camera> cameras, List<AnchorCameraCandidate> offered,
        Canvas iconCanvas, Canvas rowCanvas)
    {
        if (_loggedAnchorCamera) return;
        _loggedAnchorCamera = true;
        string canvasCamera = "none";
        try { if (canvas != null && canvas.worldCamera != null) canvasCamera = string.IsNullOrEmpty(canvas.worldCamera.name) ? "unnamed" : canvas.worldCamera.name; }
        catch { canvasCamera = "unreadable"; }
        Plugin.Logger?.LogInfo($"[DIAG] flashlight anchor camera: canvasRenderMode={AnchorCameraModeName(mode)}, renderModeSource={AnchorElementCanvasPolicy.SourceName(iconCanvas != null || rowCanvas != null)}, iconLayer={iconLayer}, "
            + $"canvasWorldCamera={canvasCamera}, {CellUiDiagnostic(layer, iconLayer, cameras, offered)}, "
            + $"{CanvasDiagnostic(layer, canvas, flashlight, background, choice, measured)}, {AnchorCameraLadder.Inspect(candidates)}, "
            + $"{AnchorCameraLadder.Detail(candidates, iconLayer)}, inspected={inspected}, "
            + $"{ManualCanvasDiagnostic(AnchorElementNames.Flashlight, icon.Manual, icon.ManualObserved)}, {ManualCanvasDiagnostic(AnchorElementNames.BackgroundFade, row.Manual, row.ManualObserved)}, "
            + $"{ElementCanvasDiagnostic(AnchorElementNames.Flashlight, iconCanvas)}, {ElementCanvasDiagnostic(AnchorElementNames.BackgroundFade, rowCanvas)}, "
            + $"iconSource={AnchorSourceLadder.Name(source)}, measuredFrom={AnchorSourceLadder.GeometryName(source)}, "
            + $"anchorSource={AnchorCameraChoice.Provenance(choice.Projection, measured)}, authorization={AnchorCameraChoice.Authorization(choice.Projection, measured)}, "
            + $"cameraTargetTexture={(targetTexture ? "Set" : "none")}, {AnchorCameraChoice.Describe(choice, measured)}. "
            + $"IconElement: {icon.Geometry}. RowElement: {row.Geometry}. "
            + "`anchorSource=` names the rectangle the table is *using*: a camera whose culling mask really contains the element's "
            + "layer (and which does not render into a RenderTexture) - `anchorSource=camera` - or the element's own RectTransform "
            + "layout rectangle projected into window pixels - `anchorSource=canvas-geometry` - and `anchorSource=config` when "
            + "neither answered, in which case the table keeps PositionX/PositionY. `authorization=` is the rule *this attempt* was "
            + "granted, and it carries `(refused)` when that rule did not produce a rectangle: an authorization is not an answer. "
            + "`each=` is every camera the ladder was offered, with its own source, liveness, culling mask, whether that mask "
            + "contains the icon's layer, depth and RenderTexture fact - the facts a count cannot name. "
            + "`cellUiMaskProof=no` used to mix three questions into one word (was the CellUI camera offered, was it alive, did its "
            + "mask contain the layer); the fields in its parentheses now answer them apart, and "
            + "`flashlightElementCanvas=`/`background-fadeElementCanvas=` are the real `Canvas` components above each element - the "
            + "only objects that can authorize a camera-less (`screen-space overlay`) projection, which `canvasRenderMode=` above "
            + "cannot decide because `GuiLayerBase.m_layerCanvas` is null for this HUD's whole life and that field then comes from "
            + "`GuiLayerBase.CanvasRenderMode`, an enum on the layer. `iconCanvasRenderer=false` is not a fault: both of this build's "
            + "anchors are the prefab's own SpriteRenderers and carry no CanvasRenderer, which is why that fact no longer gates "
            + "anything. `measuredFrom=` says what rectangle was measured - the icon row's PUI_InventoryIconDisplay layout "
            + "rectangle, the row's Background Fade SpriteRenderer geometry, or the configured position - and the row is never "
            + "called the flashlight icon. "
            + "`flashlightManualCanvas=`/`background-fadeManualCanvas=` are this game's own compositor, read per element and per "
            + "attempt: this in-level HUD carries no uGUI `Canvas` at all, and `GuiLayerBase.m_cellUICanvas` is a `UI_Canvas` whose "
            + "`m_UICamera` renders the whole HUD into a texture whose pixel size is printed as `target=` (and, for the whole HUD, as "
            + "`cellUiCameraPixel=` beside the canvas' declared `cellUiPixel=`). A projection is authorized from that chain only when "
            + "the element really lives under the canvas' root, that camera draws the element's layer, the camera fills the texture, "
            + "the texture is the window's pixel size and the canvas is laid out for that same size - and `origin=refused(<fact>)` "
            + "names the one fact that was missing when it is not. That equality makes the two pixel grids the same grid; it is not by "
            + "itself a proof that the final composite has no offset or flip, which only a real run can show. `enabled=no` on that "
            + "camera is not a fault: a manual canvas is rendered on demand, so its own liveness is never the question. "
            + "`not-observed` says the chain was never read on this attempt - a missing renderer returns before it - and is not the "
            + "same answer as `absent`, which is a HUD whose `m_cellUICanvas` really is null.");
    }

    // 1.19.28: the CellUI canvas' own camera, and whether it passed the same culling-mask proof as every other candidate. "Has a
    // camera", "that camera is alive" and "its mask contains the element's layer" are three different questions - the recorded line
    // folded them into one word - and the line answers all of them apart.
    [HideFromIl2Cpp]
    private static string CellUiDiagnostic(PlayerGuiLayer layer, int elementLayer, List<Camera> cameras,
        List<AnchorCameraCandidate> candidates)
    {
        string canvasState = "none";
        try { canvasState = layer != null && layer.GuiLayerBase != null && layer.GuiLayerBase.m_cellUICanvas != null ? "present" : "none"; }
        catch { canvasState = "unreadable"; }
        Camera cellUi = null;
        try { cellUi = layer != null && layer.GuiLayerBase != null && layer.GuiLayerBase.m_cellUICanvas != null ? layer.GuiLayerBase.m_cellUICanvas.m_UICamera : null; }
        catch { cellUi = null; }
        string cameraName = cellUi == null ? "none" : (string.IsNullOrEmpty(cellUi.name) ? "unnamed" : cellUi.name);
        // The proof is asked of the ladder's own rule and of the candidate list the ladder was handed, so this field can only ever
        // repeat the decision - never make one.
        bool proven = false, offered = false, alive = false, renders = false, texture = false;
        int mask = 0;
        long pointer = PointerOf(cellUi);
        // 1.19.32: this compared wrappers with `ReferenceEquals`, and Il2CppInterop hands out a *new* wrapper for every read of
        // `m_cellUICanvas.m_UICamera` - so the recorded line answered `offered=no,mask=0x00000000,texture=no` while `each=#0` on the
        // same line listed that very camera with `mask=0x00000020,texture=yes`. The identity is the native pointer now, and the
        // candidate's own source is the fallback only when the pointer cannot be read at all (`AnchorCameraIdentity`).
        if (cellUi != null && cameras != null && candidates != null)
        {
            int found = AnchorCameraIdentity.IndexOf(candidates, PointersOf(cameras), AnchorCameraSource.CellUiCamera, pointer);
            if (found >= 0)
            {
                offered = true;
                alive = candidates[found].Alive;
                mask = candidates[found].CullingMask;
                renders = AnchorCameraLadder.Renders(mask, elementLayer);
                texture = candidates[found].TargetTexture;
                proven = alive && renders;
            }
        }
        // 1.19.29: the three questions `cellUiMaskProof` used to fold into one word, answered apart - was this camera offered to
        // the ladder at all, was it alive, and does its culling mask contain the icon's layer - plus the two reads only Unity can
        // answer: `enabled` and `activeInHierarchy`, the two halves of "alive". A camera that is present and alive but answers no
        // mask proof, and a camera that is present and simply switched off, are two completely different repairs, and the recorded
        // 1.19.28 line could not tell them apart.
        // No delegate and no captured lambda here on purpose: this class is rewritten by the IL2CPP interop generator, and the two
        // reads are ordinary `try`/`catch` expressions that can only answer "the value, or unreadable".
        string cameraEnabled = "unreadable", cameraActive = "unreadable";
        try { cameraEnabled = cellUi != null && cellUi.enabled ? "yes" : "no"; } catch { cameraEnabled = "unreadable"; }
        try { cameraActive = cellUi != null && cellUi.gameObject.activeInHierarchy ? "yes" : "no"; } catch { cameraActive = "unreadable"; }
        string state = "offered=" + (offered ? "yes" : "no")
            + ",alive=" + (alive ? "yes" : "no")
            + ",enabled=" + cameraEnabled
            + ",activeInHierarchy=" + cameraActive
            + ",mask=0x" + mask.ToString("X8")
            + ",rendersLayer=" + (renders ? "yes" : "no")
            + ",texture=" + (texture ? "yes" : "no");
        return "cellUiCanvas=" + canvasState + ",cellUiCamera=" + cameraName + ",cellUiMaskProof=" + (proven ? "yes" : "no")
            + "(" + state + ")," + CellUiCanvasGeometry(layer);
    }

    // The CellUI canvas' own pixel size and laid-out rectangle (`UI_Canvas.m_pixelWidth/m_pixelHeight/m_rectTransform`). Printed
    // because they are what a camera-free pixel mapping would have to agree with: a `UI_Canvas` whose rectangle is the window is a
    // canvas whose local units can be compared against `Screen.width/height` at all, and one that is not is not.
    [HideFromIl2Cpp]
    private static string CellUiCanvasGeometry(PlayerGuiLayer layer)
    {
        float width = float.NaN, height = float.NaN;
        string rect = "none", target = "none";
        try
        {
            var cellUi = layer != null && layer.GuiLayerBase != null ? layer.GuiLayerBase.m_cellUICanvas : null;
            if (cellUi == null) return "cellUiPixel=none,cellUiRect=none,cellUiCameraPixel=none";
            width = cellUi.m_pixelWidth;
            height = cellUi.m_pixelHeight;
            RectTransform root = cellUi.m_rectTransform;
            if (root != null)
            {
                Rect local = root.rect;
                rect = "(" + Number(local.xMin) + "," + Number(local.yMin) + "," + Number(local.width) + "," + Number(local.height) + ")";
            }
            // 1.19.35: the camera's own render target, which is a *different* read from `m_pixelWidth/m_pixelHeight` above.
            // The manual-canvas authorization stands or falls on those two being equal to the window, and until this field
            // existed the one-shot line printed the canvas' declared size and the window's, but never the texture the camera
            // really renders into - so "the chain matches" could not be checked against a real run at all.
            Camera camera = cellUi.m_UICamera;
            if (camera != null) target = camera.pixelWidth + "x" + camera.pixelHeight;
        }
        catch { return "cellUiPixel=unreadable,cellUiRect=unreadable,cellUiCameraPixel=unreadable"; }
        return "cellUiPixel=" + (float.IsFinite(width) && float.IsFinite(height)
                ? width.ToString("0.#") + "x" + height.ToString("0.#") : "unreadable")
            + ",cellUiRect=" + rect
            + ",cellUiCameraPixel=" + target;
    }

    // 1.19.32: one element's manual-canvas chain, as the one-shot line prints it. `AnchorManualCanvasPolicy.Describe` is the pure
    // formatter, so this method only names the element and says `absent` when this HUD has no manual canvas at all - the case in which
    // the geometry answer keeps exactly the behaviour it had before this round.
    //
    // 1.19.35: `not-observed` is its own word. A missing renderer returns from `MeasureElement` before any read of the chain, so
    // the facts are the default struct - which is *also* what a HUD with no `m_cellUICanvas` answers. Printing `absent` for the
    // first case claimed a read that never happened; `observed` is the measurement's own record of whether it did.
    [HideFromIl2Cpp]
    private static string ManualCanvasDiagnostic(string element, AnchorManualCanvasFacts facts, bool observed)
        => element + "ManualCanvas=" + AnchorManualCanvasPolicy.Field(observed, facts);

    // The reads the canvas decision was made from, spelled out so that a single `canvasRenderMode=unknown` cannot be read two
    // reader needs to tell the three answers apart (a camera, the element's own geometry, the configured position). Every value
    // was already read to make the decision, so this line can never move the anchor. (The independent report's §8-2 asked for
    // the canvas reads; §7.4-1 asked for the source facts; 1.19.27 added the authorization that answered; 1.19.28 adds the
    // rectangle and pivot each element declares and which flashlight sprite is drawn.)
    [HideFromIl2Cpp]
    private static string CanvasDiagnostic(PlayerGuiLayer layer, Canvas canvas, AnchorElementFacts flashlight, AnchorElementFacts background,
        AnchorCameraChoice choice, bool measured)
    {
        string canvasState;
        try { canvasState = canvas == null ? "none" : "present"; } catch { canvasState = "unreadable"; }
        string canvasMode;
        try { canvasMode = canvas != null ? canvas.renderMode.ToString() : "not-read"; } catch { canvasMode = "unreadable"; }
        string layerMode;
        try { layerMode = layer != null ? layer.CanvasRenderMode.ToString() : "not-read"; } catch { layerMode = "unreadable"; }
        // Whether the layer carries a `CanvasScaler`. It is a *hint* and not a proof: uGUI's `CanvasScaler` is declared on a
        // GameObject that also carries a `Canvas`, so a non-null field is worth reading - but the interop assembly strips those
        // attributes, so that declaration cannot be checked from here and the authoritative read stays `*ElementCanvas=` below.
        string scaler;
        try { scaler = layer != null && layer.GuiLayerBase != null && layer.GuiLayerBase.m_canvasScaler != null ? "present" : "none"; }
        catch { scaler = "unreadable"; }
        return "layerCanvas=" + canvasState + ",layerCanvasRenderMode=" + canvasMode
            + ",layerCanvasRenderModeProperty=" + layerMode
            + ",layerCanvasScaler=" + scaler
            + ",iconHasRectTransform=" + (flashlight.HasRectTransform ? "true" : "false")
            + ",iconCanvasRenderer=" + (flashlight.CanvasDrawn ? "true" : "false") + "(not a gate: no CanvasRenderer is what this prefab has)"
            // 1.19.29: the rule this attempt was granted, and `(refused)` when it did not produce a rectangle. The field used to be
            // called `projection=` and printed the bare word, which is how a refused geometry attempt came to be read as
            // `anchorSource=canvas-geometry` in a line whose own gates said `refused`.
            + ",authorization=" + AnchorCameraChoice.Authorization(choice.Projection, measured)
            + "," + flashlight.Describe()
            + "," + background.Describe();
    }

    // 1.19.29/1.19.30: the real `Canvas` component above one of the two elements - the object that decides both which render
    // mode the element is composited in and whether a camera-less (screen-space overlay) projection is legitimate at all.
    //
    // The recorded line could not answer this: `canvasRenderMode=` came from `GuiLayerBase.CanvasRenderMode` (an enum on the
    // layer) because `GuiLayerBase.m_layerCanvas` is null for this HUD's whole life, and the *element's own* ancestor canvas -
    // which the code already walks to in `AncestorCanvas` - was never printed. Since 1.19.30 that same object is what the
    // geometry answer reads its mode and its camera from (`AnchorElementCanvasPolicy`), so this field is the input to the
    // decision and not only a description of it. Nothing here can move the anchor: every value is a read of an object the
    // attempt already looked at.
    [HideFromIl2Cpp]
    private static string ElementCanvasDiagnostic(string element, Canvas canvas)
    {
        string prefix = element + "ElementCanvas=";
        if (canvas == null) return prefix + "missing";
        string name = "unnamed", mode = "unreadable", world = "none", root = "unreadable", pixels = "unreadable";
        try { name = string.IsNullOrEmpty(canvas.name) ? "unnamed" : canvas.name; } catch { name = "unreadable"; }
        try { mode = canvas.renderMode.ToString(); } catch { mode = "unreadable"; }
        try
        {
            Camera camera = canvas.worldCamera;
            world = camera == null ? "none" : (string.IsNullOrEmpty(camera.name) ? "unnamed" : camera.name);
        }
        catch { world = "unreadable"; }
        try { root = canvas.isRootCanvas ? "yes" : "no"; } catch { root = "unreadable"; }
        try
        {
            Rect pixelRect = canvas.pixelRect;
            pixels = "(" + Number(pixelRect.xMin) + "," + Number(pixelRect.yMin) + "," + Number(pixelRect.width) + "," + Number(pixelRect.height) + ")";
        }
        catch { pixels = "unreadable"; }
        return prefix + "found(" + name + "),renderMode=" + mode + ",worldCamera=" + world + ",isRoot=" + root + ",pixelRect=" + pixels;
    }

    // What one of the two native elements answered on an attempt, in the words the camera line prints. Nothing here can
    // move the anchor: every value was already read to decide, and the line only says what was seen.
    private readonly struct AnchorElementFacts
    {
        private readonly string _name;
        private readonly bool _present, _canvasDrawn, _hasRectTransform, _active;
        private readonly int _layer;
        private readonly float _width, _height;
        private readonly string _layout;
        private readonly AnchorIconSprite _drawn;

        public AnchorElementFacts(string name, bool present, int layer, bool canvasDrawn, bool hasRectTransform, bool active,
            float width, float height, string layout = "none", AnchorIconSprite drawn = AnchorIconSprite.Enabled)
        {
            _name = string.IsNullOrEmpty(name) ? "element" : name;
            _present = present; _layer = layer; _canvasDrawn = canvasDrawn; _hasRectTransform = hasRectTransform;
            _active = active; _width = width; _height = height; _layout = layout ?? "none"; _drawn = drawn;
        }

        public bool HasRectTransform { get { return _hasRectTransform; } }
        public bool CanvasDrawn { get { return _canvasDrawn; } }

        // `flashlight=found,flashlightLayer=5,flashlightCanvasRenderer=false,flashlightRectTransform=true,...` - the element's
        // own name in every field, so a line that mentions both can never be read as if one of them were the other. The
        // CanvasRenderer fact is printed because it authorizes the camera-less *overlay* rule; the RectTransform fact because it
        // authorizes the geometry rule (1.19.27) - and 1.19.28 prints the rectangle and pivot that RectTransform declares, plus
        // which of the two flashlight sprites is on screen, because "found" said nothing about either.
        public string Describe()
            => _name + "=" + (_present ? "found" : "missing")
             + "," + _name + "Layer=" + _layer
             + "," + _name + "CanvasRenderer=" + (_canvasDrawn ? "true" : "false")
             + "," + _name + "RectTransform=" + (_hasRectTransform ? "true" : "false")
             + "," + _name + "Active=" + (_active ? "true" : "false")
             + "," + _name + "Rect=" + _layout
             + "," + _name + "Bounds=" + (float.IsFinite(_width) && float.IsFinite(_height)
                 ? _width.ToString("0.#") + "x" + _height.ToString("0.#")
                 : "unmeasurable")
             + (string.Equals(_name, AnchorElementNames.Flashlight, StringComparison.Ordinal)
                 ? "," + _name + "DrawnSprite=" + AnchorFlashlightIcon.Name(_drawn) : string.Empty);
    }

    [HideFromIl2Cpp]
    private static string AnchorCameraModeName(AnchorCanvasMode mode)
    {
        switch (mode)
        {
            case AnchorCanvasMode.Overlay: return "screen-space-overlay";
            case AnchorCanvasMode.ScreenSpaceCamera: return "screen-space-camera";
            case AnchorCanvasMode.WorldSpace: return "world-space";
            default: return "unknown";
        }
    }

    // A sprite's own renderer bounds, projected by the camera the ladder proved draws it (1.19.28 also answers the raw window
    // rectangle, so the line can print the numbers a refused measurement was refused for).
    [HideFromIl2Cpp]
    private static bool TryScreenRect(Renderer renderer, Camera camera, out LayoutRect rect, out AnchorScreenRect raw)
    {
        rect = default;
        raw = default;
        if (renderer == null || camera == null) return false;
        Bounds bounds = renderer.bounds;
        var xs = new List<float>(8);
        var ys = new List<float>(8);
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 point = bounds.center + Vector3.Scale(bounds.extents,
                new Vector3((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f));
            Vector3 screen = camera.WorldToScreenPoint(point);
            if (screen.z < 0f || !float.IsFinite(screen.x) || !float.IsFinite(screen.y)) return false;
            xs.Add(screen.x);
            ys.Add(Screen.height - screen.y);
        }
        return AnchorRectProjection.TryFromCorners(xs, ys, Screen.width, Screen.height, out rect, out raw);
    }

    // The drawn quad of a `SpriteRenderer` whose own `bounds` cannot describe it - the case an *inactive* sprite is in.
    // The row's `Background Fade` is switched off in the prefab (`m_IsActive=False`), so the element this build falls back
    // to is exactly the element whose bounds are least trustworthy.
    //
    // The quad is therefore rebuilt from the three facts that really describe what the page draws, and from nothing else:
    // the sprite's own local bounds (already in world units, already carrying the pivot), the renderer's `size` (the extent
    // sliced/tiled drawing uses - 250 x 30 for this gradient, not the 25 x 25 of the flashlight icons) and the sprite's flip
    // flags. The corners are taken through the element's own transform and projected by the same validated camera the
    // renderer-bounds path uses.
    //
    // The element's declared RectTransform is deliberately *not* what this method measures: its 300 x 109.92 is not the 250 x 30
    // this sprite draws, so measuring the layout rectangle would place the table somewhere the page never put the background. The
    // layout rectangle is still *reported* beside the sprite geometry - `AnchorSourceLadder.GeometryName` names the difference.
    [HideFromIl2Cpp]
    private static bool TryScreenRectFromSprite(Renderer source, Camera camera, out LayoutRect rect, out AnchorScreenRect raw)
    {
        rect = default;
        raw = default;
        if (source == null || camera == null) return false;
        SpriteRenderer renderer = null;
        try { renderer = source.TryCast<SpriteRenderer>(); } catch { return false; }
        if (renderer == null) return false;
        Sprite sprite = null;
        Vector2 drawn = default;
        bool flipX = false, flipY = false;
        Bounds local = default;
        try
        {
            sprite = renderer.sprite;
            drawn = renderer.size;
            flipX = renderer.flipX;
            flipY = renderer.flipY;
        }
        catch { return false; }
        if (sprite == null) return false;
        try { local = sprite.bounds; } catch { return false; }
        // Where the transform's origin sits inside the sprite: 1,0.5 for `UI_Gradient_64x64`, 0.5,0.5 for the flashlight
        // icons. Derived from the bounds instead of `Sprite.pivot`, which is in pixels and would need the pivot's own unit.
        float pivotX = 0.5f, pivotY = 0.5f;
        if (local.size.x > 0f) pivotX = (local.center.x - local.min.x) / local.size.x;
        if (local.size.y > 0f) pivotY = (local.center.y - local.min.y) / local.size.y;
        float width = drawn.x > 0f ? drawn.x : local.size.x;
        float height = drawn.y > 0f ? drawn.y : local.size.y;
        AnchorQuad quad = AnchorSpriteQuad.FromSprite(width, height, pivotX, pivotY, flipX, flipY);
        if (!quad.Valid) return false;
        var xs = new List<float>(4);
        var ys = new List<float>(4);
        Transform transform = null;
        try { transform = renderer.transform; } catch { return false; }
        if (transform == null) return false;
        for (int corner = 0; corner < 4; corner++)
        {
            Vector3 world = transform.TransformPoint(new Vector3(
                (corner & 1) == 0 ? quad.MinX : quad.MaxX,
                (corner & 2) == 0 ? quad.MinY : quad.MaxY, 0f));
            Vector3 screen = camera.WorldToScreenPoint(world);
            if (screen.z < 0f || !float.IsFinite(screen.x) || !float.IsFinite(screen.y)) return false;
            xs.Add(screen.x);
            ys.Add(Screen.height - screen.y);
        }
        return AnchorRectProjection.TryFromCorners(xs, ys, Screen.width, Screen.height, out rect, out raw);
    }

    // A UI element inside a screen-space overlay canvas: Unity composites that canvas without any camera, so
    // `RectTransformUtility.WorldToScreenPoint(null, world)` is the correct projection there and a null camera is the
    // legitimate answer - not a missing camera.
    //
    // This branch is only ever reached for an object Unity's own canvas draws, i.e. one that carries a `CanvasRenderer`
    // (`AnchorCameraLadder.Select` is handed that fact, not "has a RectTransform"). Both of this build's anchors - the
    // flashlight icon and the row's `Background Fade` - are `SpriteRenderer`s that *also* carry a RectTransform, and
    // projecting those through a null camera is exactly the false positive the ladder no longer allows.
    [HideFromIl2Cpp]
    private static bool TryScreenRectWithoutCamera(Renderer renderer, out LayoutRect rect)
    {
        rect = default;
        RectTransform element = null;
        try { element = renderer != null ? renderer.transform.TryCast<RectTransform>() : null; }
        catch { element = null; }
        if (element == null) return false;
        Rect local = element.rect;
        var xs = new List<float>(4);
        var ys = new List<float>(4);
        for (int corner = 0; corner < 4; corner++)
        {
            Vector3 world = element.TransformPoint(new Vector3(
                (corner & 1) == 0 ? local.xMin : local.xMax,
                (corner & 2) == 0 ? local.yMin : local.yMax, 0f));
            Vector2 screen = RectTransformUtility.WorldToScreenPoint(null, world);
            if (!float.IsFinite(screen.x) || !float.IsFinite(screen.y)) return false;
            xs.Add(screen.x);
            ys.Add(Screen.height - screen.y);
        }
        return ScreenBounds.TryFromCorners(xs, ys, Screen.width, Screen.height, out rect);
    }

    [HideFromIl2Cpp]
    private static List<PanelColumn> Columns()
        => Plugin.VisibleColumns.Count > 0 ? Plugin.VisibleColumns : PanelColumns.Compose(true, true, true, true, true);

    // With the flashlight anchor the table hangs below the icon, right-aligned with it, so it sits
    // in the right-hand gear column wherever that column happens to be. A negative PositionX/Y
    // measures from the right/bottom edge, which is the fallback used whenever the icon cannot be
    // measured - a sample that failed, or a budget that gave up - so the table is on screen either
    // way. Either way the whole table is clamped inside the window.
    [HideFromIl2Cpp]
    private bool TryResolveOrigin(PanelGrid grid, out LayoutRect origin)
    {
        origin = default;
        float x, y;
        bool anchored = Plugin.AnchorToFlashlight.Value && _anchorLocked;
        if (anchored)
        {
            x = _anchorRect.Right - grid.Width + Plugin.OffsetX.Value;
            y = _anchorRect.Bottom + 8f + Plugin.OffsetY.Value;
        }
        else if (Plugin.AnchorToFlashlight.Value && !_anchorUnavailable && !_anchorFallback)
        {
            return false;   // still settling: draw nothing rather than jump
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
