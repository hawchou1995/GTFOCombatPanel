using System;
using System.Collections.Generic;
using System.Linq;
using CellMenu;
using Il2CppInterop.Runtime.Attributes;
using Player;
using TMPro;
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
//
// 1.19.41: that row is the *anchor*, not the place. The table hangs below the whole equipment column the row belongs to -
// the weapon, its ammo figures, the tool, the resource pack and the consumable are separate rows stacked under it - so the
// column's rows are measured on the same chain, projected by the same proven camera and gated by the same `ScreenBounds`
// rule, and the table is placed one gap under the last of them (`HudAvoidance.cs`). A bottom edge that arrives late is
// honoured at once; a column that collapses has to stay collapsed for two readings before the table follows it back.
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
    // 1.19.41: the equipment *column* the table has to stay clear of, and the camera the anchor row was projected
    // with. The anchor row is one row of that column - it is not the column's bottom edge - so the rows are read from
    // the same `m_playerLayer -> Inventory` chain the anchor is read from (`m_iconDisplay` plus every slot the inventory
    // lists in `m_slotGUIOrder`), projected by the one camera this attempt proved, and gated by the same `ScreenBounds`
    // rule. `_obstacles` is the one preallocated buffer: the read never allocates anything sized by what it finds, never
    // walks a hierarchy and never scans a scene, so it costs the same on a full column as on an empty one.
    private readonly EquipmentBandWatch _bandWatch = new EquipmentBandWatch();
    private readonly List<HudObstacle> _obstacles = new List<HudObstacle>(EquipmentBand.MaxObstacles);
    // The camera `ReadAnchor` proved for the element it measured. Written by that read (a static one, like the note it
    // leaves behind) so the band's later re-reads use the same projection instead of looking for a camera of their own.
    private static Camera _bandCamera;
    // 1.19.45: whether `_bandCamera` was really proved for *this* round. The two are not the same fact: a `null` camera
    // here is the legitimate "the HUD's canvas is a real overlay canvas, project without one" answer, while "no round has
    // proved a camera since the level changed" must not be read as that same answer and must never re-use the previous
    // level's rectangle. Written with `_bandCamera` in the anchor read, cleared with the rest of the round below.
    private static bool _bandCameraProved;
    // 1.19.45: the *proof type* of `_bandCamera`. True only when this round proved it through the HUD's own manual canvas
    // (`AnchorCameraChoice.ManualCanvas` -> `AnchorProjection.Rect` with no source lens). That is the one camera Unity may
    // leave `enabled == false` and still composite into the window, so it is the only kind of disabled camera accepted.
    private static bool _bandCameraManualCanvas;
    // The obstacle list is re-read on the same 0.25 s poll the table itself refreshes on, which is four reads a second
    // while a level is running and nothing at all on a result page.
    private const float BandInterval = 0.25f;
    private float _nextBandRead;
    // The last placement this panel reported. A line is written only when it changes, so the log carries one entry per
    // real move instead of one per poll.
    private string _placementNote;
    // 1.19.49: one ledger for the whole "no proven place" family - the automatic anchor's missing measurement, a column
    // measured only in part, a page with no free rung, the conservative place that was drawn instead, and the one poll that
    // still draws nothing. A line joins it under a *semantic* key ("the column is partial", "the place is the right row"),
    // never under its own numbers, so the same absence is written once however long it lasts: the table's width, the spot's
    // coordinates, the attempt count, the retry delay, an F8 press and one ordinary poll after another all move inside a line
    // that has already been written.
    //
    // 1.19.50: the vocabulary and the bound live in `HudAbsenceLedger`, not here, because the two have to agree: the family has
    // ten legitimate keys and the ledger holds exactly ten, so every reason's first line is reportable - the previous cap of
    // eight silently refused the ninth and tenth, and a round that had already written its anchor absence, a partial column and
    // four spot answers could lose the one line that says "this poll drew nothing and here is why". A key outside the
    // vocabulary is refused rather than counted, so nothing can widen the bound either. The ledger is emptied with the round
    // (`ForgetBandRound`) - the one thing that makes an absence new again - and a *recovery* does not empty it, so a column that
    // flips complete/partial at the 0.25 s poll rate cannot turn one absence into a line per flip.
    private readonly HudAbsenceLedger _absence = new HudAbsenceLedger();
    // 1.19.48 (review): the conservative place is *sticky* once it is drawn, so a column that flips complete/partial at the
    // 0.25 s poll rate cannot move the table on every other poll. `_bandCompleteStreak` counts consecutive complete readings
    // and `_fallbackSpotDrawn` says this round has really drawn a conservative place.
    private bool _fallbackSpotDrawn;
    private int _bandCompleteStreak;
    private const int StableCompleteSamples = 2;
    // The one preallocated buffer of rectangles a conservative place has to clear: the rows this poll really measured, each
    // one on its own (the union of a column with a hole in it also covers the hole).
    private readonly List<LayoutRect> _fallbackObstacles = new List<LayoutRect>(EquipmentBand.MaxObstacles + 1);
    // What the last band read saw, printed beside the placement so a recorded frame can be compared with the HUD.
    private string _bandRows = "band=not-read";
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
        ForgetBandRound();
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

    // The equipment column's measurement round: the committed band, the camera it was projected with, the read schedule
    // and the three lines that reported it.
    //
    // 1.19.45: every one of those facts is about one HUD in one window, so they are dropped together. The defect this
    // replaces is the level change: the anchor branch below deliberately leaves a *locked* anchor alone (that is its
    // design and it is not touched here), but it also left the band behind - the committed rectangle, the camera that
    // projected it and the read clock. A new level destroys that HUD and its camera, so the panel went on answering
    // `_bandWatch.Has` from the previous level's rectangle, projected through a camera that no longer exists: the table
    // hung under a column that was not there, for the rest of the session. A configuration reload and a resolution change
    // already dropped exactly these fields (`UnlockAnchor`), so this is the same reset, in one place, called from both.
    private void ForgetBandRound()
    {
        _bandWatch.Restart();
        _bandCamera = null;
        _bandCameraProved = false;
        // The proof travels with the camera it was made for: a new level's camera has not been proved by anything yet, so a
        // manual-canvas authorization from the previous round must never make a disabled camera of this one look alive.
        _bandCameraManualCanvas = false;
        _nextBandRead = 0f;
        _bandRows = "band=not-read";
        // 1.19.50: the rows of the last read go with the commit. `ReadEquipmentBand` clears them at the start of its own read,
        // but between this reset and the next read `TryFallbackSpot` treats every drawn-and-measured row in `_obstacles` as an
        // obstacle - and the committed band's own window guard (`_anchorScreenWidth == Screen.width`) never covered the rows.
        // So a new level, a configuration reload or a resolution change left the *previous* scene's rectangles (and, after a
        // resize, the previous window's pixels) in the list: the conservative place was pushed around a column that no longer
        // existed, and the line counted it as "known row(s)". Retained geometry is still an obstacle inside one round, which is
        // what this list is for; it is only no longer inherited across one.
        _obstacles.Clear();
        _placementNote = null;
        // 1.19.49: every absence line of this round goes with the round - a new page, a new level, a configuration reload or a
        // resolution change is a new absence and earns its own line. `_fallbackSpotDrawn` and the streak belong to the round
        // too, so a new page starts from "no conservative place has been drawn" and earns its own recovery window.
        _absence.Clear();
        _fallbackSpotDrawn = false;
        // 1.19.46: the camera re-proof budget belongs to the round as well - a new level earns its own attempts.
        _bandCameraReproofs = 0;
        _bandCompleteStreak = 0;
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
                    // 1.19.45: and the equipment column's own round goes with it, for the same reason and one worse: the
                    // camera that projected those rows belonged to the HUD this line change destroyed, so the committed
                    // band below is a rectangle from a scene that is no longer loaded. It is dropped here through the one
                    // reset `UnlockAnchor` already uses, so the two paths can never disagree about what a round is. The
                    // locked anchor itself is deliberately left alone, exactly as before.
                    ForgetBandRound();
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
            // The settlement page's own two controls are polled here, on the frame, for the same reason the key is:
            // they are the page's pointer and the board must hide or come back on this frame's next repaint. It
            // reads the game's pointer and left button and answers for the two rectangles the last repaint really
            // drew; it never captures, wraps or suppresses any input, so a click that is not on one of them reaches
            // the page exactly as it did before.
            ResultsDisplay.PollChromeInput();
            // 1.19.59: the settlement board's wheel is settled here too, on the frame, from the page's own
            // `ScrollInput` first and the physical `Input.mouseScrollDelta` as the fallback - the routing the player's
            // other panel uses. `CM_PageBase.UpdateButtonPress` is the second caller and the router's frame latch keeps
            // the two to one settlement per frame. Nothing is consumed: a frame this mod does not use is still the page's.
            ResultsDisplay.PollWheel();
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
        // 1.19.59: `OnGUI` no longer reads the wheel. The settlement board's scroll is settled from the game's own
        // readings on the frame (`Update` below, and `CM_PageBase.UpdateButtonPress` as the second entry), so this method
        // is drawing only - and it no longer consumes an IMGUI event to keep the page from scrolling, because it never
        // took one: the page keeps its own `ScrollInput` (see `ResultWheel.cs`).
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

        // 1.19.26: `_anchorUnavailable` is deliberately not part of this gate. It reports that the round's budget was spent;
        // it may not close the only door to `SampleAnchor`, because the policy's own clock is what decides from then on - the
        // settle cadence while it is measuring, and the low-rate watch when the budget is gone. Gating here as well is what
        // made a `Background Fade` that appeared after the twelve attempts unreachable without F8, a level change or a manual
        // re-anchor. A locked anchor is still never sampled again.
        if (Plugin.AnchorToFlashlight.Value && !_anchorLocked) SampleAnchor(layer, Time.unscaledTime);
        // 1.19.41: the column is re-read on the same poll, and only once the anchor is locked: before that there is no
        // proven camera to project its rows with, and `ReadAnchor` writes that camera on the attempt that measures the
        // anchor. A locked anchor is frozen, but the column is *not* - the HUD expands a slot when a weapon is selected,
        // grows a row when a pack is picked up and adds one when a throwable is readied - so the band is watched for the
        // rest of the level, at this poll and never per frame.
        if (Plugin.AnchorToFlashlight.Value && _anchorLocked) WatchEquipmentBand(layer, Time.unscaledTime);
        var columns = Columns();
        var players = CombatSync.DisplayRows().OrderBy(p => p.DisplaySlot).Take(MaxPlayers).ToList();
        _content = BuildTable(columns, players);
        _grid = HudTable.BuildGrid(_content, FontSize(), Plugin.Density, Screen.width, Screen.height);
        if (_grid == null) { _content = null; return; }
        // 1.19.47: a poll that cannot place the table clears the rectangle this round was drawing it at. `_grid` going
        // null already stops the paint, but the drawn origin is a fact about *this* poll: leaving the previous one in the
        // field is what would let a stale rectangle be handed to anything that asks between two repaints (`Draw` also
        // tests `_grid`, so this is the second of the two locks, not the only one).
        if (!TryResolveOrigin(_grid, out var origin)) { _grid = null; _content = null; _origin = default; return; }   // the anchor is still settling, or the column is not proven: draw nothing rather than jump
        _origin = origin;
        if (!_loggedDraw)
        {
            _loggedDraw = true;
            // 1.19.49: `anchor=` names the source of the *drawn* rectangle, not the state of the anchor. The recorded 1.19.48
            // frame printed `anchor=flashlight icon` on a table that had been drawn at the conservative place, which is the one
            // thing that line must never be read as: a locked anchor is not a placement under it.
            string source = _fallbackSpotDrawn ? "conservative place (no proven rung)"
                : (_anchorLocked ? "flashlight icon" : "config PositionX/PositionY");
            Plugin.Logger.LogInfo($"[DIAG] table {_content.ColumnCount} columns x {_content.Rows} rows, {_grid.Width:0}x{_grid.Height:0} px at ({origin.X:0},{origin.Y:0}) of a {Screen.width}x{Screen.height} window, font {_grid.FontPixels:0}px, density={_grid.Density.Name}, anchor={source}, columns={string.Join("+", columns)}; drawn by IMGUI in screen space, so nothing in the game can move it.");
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
                    // 1.19.49: an automatic measurement that has not arrived yet is the mode's own waiting, not a fault of the
                    // build, so the line is Info. The one exception is a read that really threw: that message is a genuine error
                    // of this build or of this HUD, it stays a Warning, and it is what makes the two kinds distinguishable in the
                    // log again. `FreshWarning` above still bounds this line to the round's distinct failures.
                    string line = $"[DIAG] the flashlight anchor could not be measured yet ({AnchorSamplePolicy.Describe(failure)}{threw}{camera}, attempt {_anchorPolicy.Attempts} of {AnchorSamplePolicy.Budget}, triedBackgroundFade={background}); automatic HUD placement waits for proof and the measurement is retried in {AnchorSamplePolicy.DelayAfter(_anchorPolicy.Failures):0.##}s, {Math.Max(0, AnchorSamplePolicy.Budget - _anchorPolicy.Failures)} retry(s) left.";
                    if (string.IsNullOrEmpty(sample.Error)) Plugin.Logger.LogInfo(line);
                    else Plugin.Logger.LogWarning(line);
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
                // 1.19.49: Info, for the same reason as the failure line above - the automatic mode waiting for a proof is a state
                // of this HUD, and the warning channel is left for what really went wrong.
                Plugin.Logger.LogInfo($"[DIAG] the flashlight anchor fast retries ended after {_anchorPolicy.Attempts} attempt(s) and {_anchorPolicy.Waited:0.#}s of waiting ({AnchorSamplePolicy.Describe(_anchorPolicy.LastFailure)}{camera}, triedBackgroundFade={_backgroundFadeLookup.Describe()}); automatic HUD placement still waits for proof. The HUD is checked every {AnchorSamplePolicy.WatchInterval:0.#}s for late geometry; a new level, a configuration reload or a resolution change starts a full round again.");
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
            // wall clock: the retry delays between attempts are not part of it, so a round that backed off 35.5 s between
            // twelve samples reads 3.00 s. The attempt count and the words "sampling time" are what make the line say
            // that; `AnchorSamplePolicy.Waited` is the field that accumulates the backoff itself (eleven waited intervals,
            // 35.5 s for this budget - not a measured wall clock, and never the twelfth delay the spent budget does not
            // wait for), and the give-up line is the one that prints it. The source is named on the same line, so "the table
            // locked" can never be read without saying which of the two native elements it locked onto.
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
        // 1.19.41: the camera this attempt proved for the rectangle it is about to hang the table under. The equipment
        // column's rows are projected with it on this attempt and on every later re-read of that column, so the band and
        // the anchor can never be described in two different pixel grids. A sample that measured nothing writes nothing:
        // a refused attempt is not evidence about which camera draws the HUD.
        //
        // 1.19.45: `_bandCameraProved` travels with it, because a `null` camera is a *proved* answer (the HUD's canvas is a
        // real overlay canvas and Unity's camera-less projection is the right one) and is not the same fact as "no round
        // has proved a camera at all" - which is exactly the state a level change leaves behind.
        _bandCamera = measuredAnything ? decided.Camera : null;
        _bandCameraProved = measuredAnything;
        // The one fact `BandCameraAlive` needs to be able to tell a manual canvas' on-demand camera from any other disabled
        // one. `ManualCanvas()` is the only factory that hands out `Rect` with no source, and it only ever reaches an answer
        // through the chain `AnchorManualCanvasPolicy` verifies for this window.
        _bandCameraManualCanvas = measuredAnything
            && decided.Choice.Projection == AnchorProjection.Rect
            && decided.Choice.Source == AnchorCameraSource.None;
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
                // 1.19.49: Info, like the rest of the automatic-measurement family. The row's own background was found and the
                // table hangs under a measured rectangle; this line says *which* of the two elements it came from, which is a
                // diagnosis to keep and not a fault to warn about.
                Plugin.Logger?.LogInfo($"[DIAG] the anchor could not be measured from the icon row ({reason}); anchoring on the same HUD "
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
        // 1.19.41: the camera this element's rectangle was projected with - `null` for a genuine overlay canvas, which
        // is the camera-less identity projection Unity itself uses there. Carried on the measurement so the equipment
        // column's rows are projected by the one camera this attempt already proved, instead of a second lookup that
        // could answer a different camera (or none) for the same HUD.
        public readonly Camera Camera;

        public AnchorElementMeasurement(bool present, int layer, bool canvasDrawn, AnchorCameraChoice choice, bool measured,
            LayoutRect rect, AnchorSampleFailure failure, string geometry = null, AnchorManualCanvasFacts manual = default,
            bool manualObserved = false, Camera camera = null)
        {
            Present = present; Layer = layer; CanvasDrawn = canvasDrawn; Choice = choice;
            Measured = measured; Rect = rect; Failure = failure; Geometry = geometry; Manual = manual;
            ManualObserved = manualObserved; Camera = camera;
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
            manualObserved: true, camera: used);
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

    // Where the table stands.
    //
    // Anchored mode used to hang it under the *anchor row* (`anchorRow.Bottom + 8`). The recorded expedition shows what
    // that really is: the row measured (2336,294) size 211x110 on a 2560x1440 window, so its bottom edge is 404, and the
    // table (397x60) was placed at (2150,412)-(2547,472) - drawn straight over the weapon, its ammo figures, the tool,
    // the pack and the consumable, which are rows *below* that one. A later screenshot shows the damage table lying on
    // exactly those vanilla rows.
    //
    // 1.19.41 therefore places it below the whole measured equipment column instead (`HudPlacement`, fed by the rows
    // `ReadEquipmentBand` measured), with the same gap the mod has always used. A negative PositionX/Y still measures
    // from the right/bottom edge, and that configured position is still what a round draws while the anchor is settling
    // and what a round keeps when the anchor gave up - but it is no longer where an anchored table is pushed back to when
    // the column cannot be measured: a table placed at the anchor row's bottom edge *is* the overlap this round removes,
    // so a HUD whose column cannot be measured falls back to the player's own configured position (reported once per
    // round) instead. Either way the whole table is clamped inside the window.
    [HideFromIl2Cpp]
    private bool TryResolveOrigin(PanelGrid grid, out LayoutRect origin)
    {
        origin = default;
        // 1.19.48: assigned here, not only inside the branches. `TryAnchoredOrigin` is called through a short circuit, and an
        // `out` argument is only definitely assigned when the call itself ran - so `float x, y;` was a compile error, and
        // these initial values are also the honest answer for a poll that never asked.
        float x = 0f, y = 0f;
        bool automatic = Plugin.AnchorToFlashlight.Value;
        bool anchored = automatic && _anchorLocked;
        // 1.19.47: a rung is a proof only for a reading that proved the whole column, and 1.19.48 does not make one cheaper.
        // What 1.19.48 changes is the answer to "no proof": the table is drawn at a conservative place instead of not at all.
        //
        // 1.19.48 (review): the proof is also *sticky* once a conservative place was drawn. `recovered` requires
        // `StableCompleteSamples` consecutive complete readings before the ladder may place again, so a column that flips
        // complete/partial at the 0.25 s poll rate cannot hand the table back and forth between the rung and the spot - and
        // cannot write a line at every turn. A round that never fell back is unchanged: the rung is taken on the poll that
        // proves it.
        bool recovered = !_fallbackSpotDrawn || _bandCompleteStreak >= StableCompleteSamples;
        bool proven = anchored && recovered && TryAnchoredOrigin(grid, out x, out y);
        if (proven)
        {
            // Placed against the measured column; `TryAnchoredOrigin` has already reported which rung it used.
        }
        // 1.19.48: `_bandWatch.Has` is deliberately no longer an unconditional gate. It used to be `return false` for
        // every automatic poll without a measured band, and the recorded 1920x1080 -> 2560x1440 change is what that costs:
        // the HUD's manual canvas texture stayed 1080, the anchor round spent its twelve attempts, the column was never
        // measured - and the table was refused for the rest of the level, at the one moment the player is looking at the
        // board. A round that is still *settling* keeps its silence (a table may not jump once and then again); a round
        // that has given up, or an anchor that is locked over a column this build cannot prove, is answered below.
        else if (!anchored && automatic && !_anchorUnavailable && !_anchorFallback)
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
        // 1.19.42/1.19.47/1.19.48: three different silences, told apart.
        //
        //   * No band was ever measured: nothing here can say where the column is, so the configured position is the only
        //     answer anybody has - and, in the automatic mode, that is exactly why it may not be used (see below).
        //   * The column *was* measured and this placement covers it: the configured position is not a safe answer on
        //     this HUD - on the recorded pages `PositionY=0.365` lands inside the right-hand column - so it is not drawn
        //     there. Drawing it would be the very overlap this round removes, arrived at through the fallback instead of
        //     through the anchor.
        //   * The column was measured but only *partly*: same answer, for a stronger reason. The rows this build could not
        //     project are drawn somewhere this reading does not describe, so "does not touch the band" is a statement about
        //     the rows that were read and nothing about the ones that were not. `HudFallback.Clear` therefore refuses it.
        //
        // 1.19.48 (review): the automatic mode may only use the configured position when a *complete* reading clears it, and
        // only after the recovery gate above. The recorded 2K page is why: the configured (2024,526,459x120) misses the one
        // row that was read by 122 px in y, but it sits on that column's x range (2336..2546) where the eleven unread rows
        // are drawn - so a partial reading, or no reading at all (a canvas texture that stayed 1080), cannot call it safe.
        // Everything else is answered by `TryFallbackSpot`, whose candidates are all right-aligned to the window's right
        // margin (1.19.49) - the side the equipment column is drawn on and the edge a proven rung keeps.
        //
        // The manual mode (`AnchorToFlashlight=false`) never reaches either test: `automatic` is false there and the
        // configured position is the player's own explicit answer, overlap or not - that path is byte-for-byte unchanged.
        float fallbackOverlap = 0f;
        bool configuredSafe = !automatic || (recovered && _bandWatch.Has && _bandWatch.Complete
            && HudFallback.Clear(new LayoutRect(x, y, grid.Width, grid.Height), _bandWatch.Has,
                _bandWatch.Complete, _bandWatch.Committed, out fallbackOverlap));
        if (!configuredSafe)
        {
            if (!TryFallbackSpot(grid, y, out LayoutRect spot, out string spotWhy, out float spotOverlap))
            {
                // The one page nothing can be drawn on: a table larger than the window (or a window whose size could not be
                // read). That is a property of the table, not of the HUD, and it is still reported as it always was.
                DeferFallback(new LayoutRect(x, y, grid.Width, grid.Height), fallbackOverlap, _bandWatch.Complete);
                return false;
            }
            // 1.19.49: nothing re-arms an absence line here. The poll is answered with a place, and the line that says so is
            // written once per round under its own semantic key (`ReportFallbackSpot`), not once per answer.
            _fallbackSpotDrawn = true;
            ReportFallbackSpot(spot, spotWhy, spotOverlap, fallbackOverlap);
            origin = spot;
            return true;
        }
        // A placement this poll can answer for - a proven rung, or the configured position of a complete reading - is drawn. The
        // conservative state goes with it (the table is not on the spot any more), but the *lines* already written for this round
        // stay written: a page that flips between the two answers would otherwise re-print its absence at the poll rate (1.19.49).
        _fallbackSpotDrawn = false;
        origin = new LayoutRect(x, y, grid.Width, grid.Height);
        return true;
    }

    // 1.19.48 (review): the conservative place for a poll no rung of the ladder can pay for.
    //
    // The evidence handed over is what was really measured - one rectangle per row, never the band's union (a column whose
    // middle row could not be read is a union with a hole in it) - plus the band this round committed, and only while that
    // band belongs to the window on the screen now: retained geometry is an obstacle here, never a completeness claim and
    // never a prediction. `HudFallbackSpot` then right-aligns every candidate to the window's own right margin (1.19.49): the
    // side the equipment column is drawn on, so the place stands where the table belongs instead of on the far side of the
    // screen. Nothing here reads `_anchorRect`, a camera or the attempt budget.
    [HideFromIl2Cpp]
    private bool TryFallbackSpot(PanelGrid grid, float preferredY, out LayoutRect spot, out string reason, out float overlap)
    {
        _fallbackObstacles.Clear();
        for (int index = 0; index < _obstacles.Count; index++)
        {
            HudObstacle row = _obstacles[index];
            if (!row.Drawn || !row.Measured) continue;
            LayoutRect rect = row.Rect;
            if (!(rect.Width > 1f) || !(rect.Height > 1f)) continue;
            if (!float.IsFinite(rect.X) || !float.IsFinite(rect.Y)) continue;
            _fallbackObstacles.Add(rect);
        }
        if (_bandWatch.Has && _anchorScreenWidth == Screen.width && _anchorScreenHeight == Screen.height)
        {
            LayoutRect committed = _bandWatch.Committed;
            if (committed.Width > 1f && committed.Height > 1f && float.IsFinite(committed.X) && float.IsFinite(committed.Y))
                _fallbackObstacles.Add(committed);
        }
        return HudFallbackSpot.TryResolve(preferredY, _fallbackObstacles, grid.Width, grid.Height, Screen.width,
            Screen.height, out spot, out reason, out overlap);
    }

    // The family's latch lives in `HudAbsenceLedger` (1.19.50): a closed vocabulary of ten keys and a ledger that holds all
    // ten, so no reason's first line can be crowded out by the reasons before it. This file only names the key.

    // The conservative place costs one line per round, and the line says exactly what it is: the table is drawn, the place is
    // not a rung, not a proof and not a claim about the anchor - and it is Info, because it is the automatic mode's own
    // degraded answer rather than a fault of this build.
    [HideFromIl2Cpp]
    private void ReportFallbackSpot(LayoutRect spot, string reason, float overlap, float refusedOverlap)
    {
        // The key is the candidate that answered - `HudAbsence.Spot` accepts only the five `TryResolve` can answer, so a reason
        // string invented elsewhere cannot become a key, and the ledger's bound is the vocabulary's own size.
        if (!_absence.Claim(HudAbsence.Spot(reason))) return;
        // 1.19.50: `margin=` is the margin this place really kept, from the same calculation that placed it. The recorded line
        // printed the constant 24 while a 1400 px table on a 1440 px window kept 20, and a 600 px window kept 24 out of the 70 it
        // could have afforded - so the figure described neither the table nor the page.
        string note = "conservative=" + reason + " spot=(" + spot.X.ToString("0") + "," + spot.Y.ToString("0") + ","
            + spot.Width.ToString("0") + "x" + spot.Height.ToString("0") + ") covers=" + overlap.ToString("0")
            + " window=" + Screen.width + "x" + Screen.height + " margin="
            + HudFallbackSpot.EffectiveMargin(spot.Width, spot.Height, Screen.width, Screen.height).ToString("0.##")
            + " clearOf=" + _fallbackObstacles.Count + " known row(s) refusedCovers=" + refusedOverlap.ToString("0");
        Plugin.Logger?.LogInfo("[DIAG] no rung of the automatic ladder was proven this poll, so the table is drawn at a conservative place ("
            + note + "): " + _bandRows + "; every candidate is right-aligned to the window's own right margin - the equipment column's side "
            + "of the screen and the edge a proven rung keeps - and clears every row and retained band this reading knows of. It claims no "
            + "rung, no proof and no anchor, and the area it covers is printed beside it rather than hidden. The configured PositionX/PositionY "
            + "is only used when a complete reading clears it; the automatic ladder places the table again after " + StableCompleteSamples
            + " consecutive complete readings. One line per distinct candidate per round, and never one per poll.");
    }

    // The one line the deferral costs - the only poll of this family that still draws nothing - and it is written once per round
    // rather than once per poll. The table is asked for again on the next poll: a column that shrinks, a reading that gets
    // complete, or a window that grows brings it back without any input, and the manual mode was never in this path.
    //
    // The line names *which* of the two answers it is - an overlap it can see (`fallbackCovers=`, the configured position really
    // lies on the measured band) or a column whose completeness is missing (`complete=no`, the row that could not be projected
    // can be anywhere, so nothing is claimed about the area the band covers) - and it is Info for the same reason the rest of
    // the family is: this is a page this build cannot place on, not a failure of this build.
    [HideFromIl2Cpp]
    private void DeferFallback(LayoutRect fallback, float overlap, bool complete)
    {
        string reason = complete
            ? "covers=" + overlap.ToString("0") + " of the measured column"
            : "column-incomplete (the rows this build could not project are drawn where this reading cannot place them)";
        string note = $"fallbackCovers=" + overlap.ToString("0") + " fallbackComplete=" + (complete ? "yes" : "no") + " "
            + reason + " fallback=(" + fallback.X.ToString("0") + ","
            + fallback.Y.ToString("0") + "," + fallback.Width.ToString("0") + "x" + fallback.Height.ToString("0") + ") "
            + _bandRows;
        // 1.19.49: two fixed answers, so the same page cannot re-print this line by moving its rectangle or its areas; 1.19.50:
        // the key comes from the closed vocabulary, so this is the only poll of the family that can say "nothing was drawn".
        if (!_absence.Claim(complete ? HudAbsence.DeferOverlap : HudAbsence.DeferIncomplete)) return;
        Plugin.Logger?.LogInfo($"[DIAG] the table's configured position is not drawn this poll ({note}): a measured "
            + "column with no proven free place is not the same thing as a HUD nobody could measure, and the configured "
            + "position is not a safe answer on this page. It comes back on the next poll as soon as the column is proven "
            + "whole, shrinks, or the window leaves room; the manual mode (AnchorToFlashlight=false) is untouched.");
    }

    // The anchored placement: right-aligned with the anchor row and below the last row of the equipment column.
    //
    // Answers false - and says why, once per round - when nothing here can prove a free place: no band was ever measured
    // for this HUD, the column was measured but not completely, or every rung of the ladder covered the column. The caller
    // then draws the table at the conservative place (`TryFallbackSpot`) - the configured position when a complete reading
    // clears it, the window's right margin otherwise (1.19.49) - and never at the anchor row's bottom edge, which is the
    // overlap this round removes. A refusal here is a statement about proofs; it is no longer a statement about painting.
    [HideFromIl2Cpp]
    private bool TryAnchoredOrigin(PanelGrid grid, out float x, out float y)
    {
        x = 0f;
        y = 0f;
        if (!_bandWatch.Has)
        {
            // 1.19.49: one line per round, under a fixed semantic key, and in Info: a column that has not been measured yet is
            // the automatic mode's own state, not an error of this build. The window, the band's detail and the retry schedule
            // may all move while the absence lasts - none of them is a new absence.
            if (_absence.Claim(HudAbsence.AnchorUnmeasured))
            {
                Plugin.Logger?.LogInfo($"[DIAG] the equipment column could not be measured, so the table has no proven place "
                    + $"beside it: {_bandRows}; the automatic mode therefore does not draw the configured PositionX/PositionY - on the "
                    + "recorded pages that rectangle sits on the equipment column's own x range, and the rows this build cannot "
                    + "project are drawn there - and is drawn at the window's right conservative place instead (1.19.49), reported "
                    + "on its own line. The manual mode still uses the configured position exactly as before. The column is read "
                    + "again on every poll while the anchor stays locked, and a configuration reload or a resolution change starts "
                    + "a new round.");
            }
            return false;
        }
        PanelPlacement placement;
        bool resolved = HudPlacement.TryResolve(_bandWatch.Committed, _bandWatch.Complete, grid.Width, grid.Height, _anchorRect.Right,
            HudPlacement.Gap, Screen.width, Screen.height, out placement);
        // 1.19.47: a rung is a *proof* only for a reading that proved the whole column, and `HudPlacement.Proven` is the one
        // rule that says so. The recorded frame (log 2769) is why this is a gate of its own rather than a note in the
        // ladder: one row was measured, eleven were stated unread, and the rung above the column drew a 459x60 table at
        // (2088,226) - on top of the native equipment column the reading had just admitted it could not describe. `overlap=0`
        // on that line only ever meant "not on the one row that was read", so it is not evidence of anything.
        if (!resolved || !HudPlacement.Proven(placement, _bandWatch.Complete))
        {
            if (!_bandWatch.Complete)
            {
                // The column is drawn, and part of it is outside this reading. Nothing here may claim a place: not a rung,
                // and (in `TryResolveOrigin`) not the configured position either - that one is the same overlap through
                // another door. 1.19.48: the table is still drawn this poll, at the conservative place, and the line says so.
                // 1.19.49: the place is on the window's right edge, and the line is Info - a column this build can only read in
                // part is the state of this HUD, not an error of this build. The ledger bounds it to one line per round: the
                // window, the table's width, the band's own detail and the retry behind it may all move while it lasts.
                if (_absence.Claim(HudAbsence.AnchorPartial))
                {
                    Plugin.Logger?.LogInfo($"[DIAG] the equipment column was measured only in part on this {Screen.width}x{Screen.height} "
                        + $"window ({_bandRows}), so the {grid.Width:0}x{grid.Height:0} table has no proven place beside it: the rows "
                        + "this build could not project are drawn somewhere this reading does not describe, so 'below', 'above' and "
                        + "'beside' the band would each be measured from an edge it has not proved - and the configured "
                        + "PositionX/PositionY is inside that same column on this page. No rung is claimed here, so the table is "
                        + "drawn at the conservative place instead (right-aligned to the window's own right margin and clear of "
                        + "every row this reading really measured), reported on its own line; the column is re-read on every poll "
                        + "while the anchor stays locked, and the first reading that proves the whole column places the table by "
                        + "the automatic ladder again by itself.");
                }
                return false;
            }
            if (_absence.Claim(HudAbsence.AnchorNoFreePlace))
            {
                Plugin.Logger?.LogInfo($"[DIAG] no free place beside the equipment column on a {Screen.width}x{Screen.height} "
                    + $"window for a {grid.Width:0}x{grid.Height:0} table: {_bandRows}, complete={(_bandWatch.Complete ? "yes" : "no")}; "
                    + "below, above and beside the column all fail or cover the whole table, so no rung is claimed and the table "
                    + "is drawn at the window's right conservative place instead - right-aligned to the window's own right margin, "
                    + "never the configured PositionX/PositionY - which is reported on its own line as conservative and never as a "
                    + "proof, and the automatic ladder places the table again only after "
                    + StableCompleteSamples + " consecutive complete readings.");
            }
            return false;
        }
        // 1.19.49: a proven rung is the answer this whole file is for, and the conservative state is already cleared by the
        // caller that draws it (`TryResolveOrigin` sets `_fallbackSpotDrawn = false`). The absence lines written earlier in this
        // round stay written: a page that flips between a rung and a conservative place must not re-print its absence at the
        // poll rate, and the recovery itself is reported by `ReportPlacement` a few lines below.
        // 1.19.47: the offset settings still move the table (that is what they are for), but the rectangle that is really
        // drawn is `offset + clamp`, and that is the one that has to clear the column. An offset - or a clamp at the
        // window's edge - that pushes the table back onto the band is dropped: the proven rung is what is drawn, and the
        // line says which of the two happened. Nothing here guesses a place; it only refuses to leave the proven one.
        x = placement.Rect.X + Plugin.OffsetX.Value;
        y = placement.Rect.Y + Plugin.OffsetY.Value;
        LayoutRect final = HudPlacement.ClampInto(new LayoutRect(x, y, grid.Width, grid.Height), Screen.width, Screen.height);
        bool offsetKept = HudPlacement.OnWindow(final, Screen.width, Screen.height)
            && HudPlacement.Area(final, _bandWatch.Committed) <= 0f;
        if (!offsetKept) final = HudPlacement.ClampInto(placement.Rect, Screen.width, Screen.height);
        x = final.X;
        y = final.Y;
        ReportPlacement(placement, grid, final, offsetKept);
        return true;
    }

    // One line per real move, and only when the move (or the column it was measured from) changed. Bounded by what the
    // player did - the same placement is not reported again on the next poll - and it prints the rows the band was built
    // from, so a recorded frame can be checked against what the HUD really drew.
    //
    // 1.19.47: it prints the rectangle that is *really* drawn (`final=`, after the offset and the clamp) beside the rung's
    // own rectangle, and it says whether the offset survived that re-check. The earlier line printed the rung and the
    // window clamp only, so a page with a non-zero offset was described by a rectangle nothing painted.
    [HideFromIl2Cpp]
    private void ReportPlacement(PanelPlacement placement, PanelGrid grid, LayoutRect final, bool offsetKept)
    {
        string note = $"{placement.Describe()} final=({final.X:0.##},{final.Y:0.##},{final.Width:0.##}x{final.Height:0.##}) "
            + $"offsetKept={(offsetKept ? "yes" : "no")} gap={HudPlacement.Gap:0.#} screen={Screen.width}x{Screen.height} "
            + $"tablePixels={grid.Width:0}x{grid.Height:0} "
            + $"anchorRow=({_anchorRect.X:0},{_anchorRect.Y:0},{_anchorRect.Width:0}x{_anchorRect.Height:0}) "
            + _bandRows
            + "; the rung is " + PanelPlacement.Name(placement.Kind) + " of a column this reading proved whole, so every row "
            + "the vanilla HUD draws in that column stays visible.";
        if (note == _placementNote) return;
        _placementNote = note;
        Plugin.Logger?.LogInfo("[DIAG] " + note);
    }

    // The glyph size is a reference size at 1080p, scaled by the window height and the density,
    // so the table looks the same on any resolution.
    private static int FontSize()
    {
        float scale = Mathf.Clamp(Screen.height / 1080f, 0.6f, 2.5f);
        return Mathf.Max(6, Mathf.RoundToInt(Plugin.FontSize.Value * Plugin.Density.Font * scale));
    }

    internal static int FontSizeForSharing => FontSize();

    // Whether the camera this round proved can still project.
    //
    // `null` is a *proved* answer - the HUD's canvas is a real overlay canvas and Unity's camera-less projection is the
    // right one - so it is alive. A wrapper whose native object is gone (the level this HUD belonged to was unloaded)
    // throws on the first member access, and that is the only way to tell: the interop wrapper is not Unity's own
    // `Object == null` overload, so a destroyed camera still looks like a live reference until it is asked something.
    [HideFromIl2Cpp]
    // Whether the camera this round proved for the column can still project.
    //
    // A `null` camera is a *proved* answer - the HUD's canvas is a real overlay canvas and Unity's camera-less projection is
    // the right one - so it is alive. A wrapper whose native object is gone throws on the first member access, and that is
    // the only way to tell: the interop wrapper is not Unity's own `Object == null` overload, so a destroyed camera still
    // looks like a live reference until it is asked something.
    //
    // 1.19.45: `isActiveAndEnabled` was the whole test, and on this HUD it is the wrong one for exactly one kind of camera.
    // The recorded run prints the manual canvas' camera as `enabled=no, activeInHierarchy=yes` (log 2827); a manual canvas is
    // rendered on demand, so `enabled` says who calls `Render`, not whether the pixels land in the window - the same fact
    // `AnchorCameraLadder` already acts on ("What this deliberately does *not* use is `camera.enabled`"). Refusing it here made
    // the column unprovable for the whole level: every poll re-proved the camera (log 2837/2840-2846, eight attempts) and every
    // row was still recorded `unread` (log 2838), so the table fell back to the configured PositionX/PositionY (log 2839).
    //
    // The relaxation is therefore *typed*, never general: a camera is accepted while disabled only when this round proved it
    // through the manual-canvas chain (`_bandCameraManualCanvas`), and even then only while it is still active in the hierarchy
    // and still a live object. Any other disabled camera - a HUD torn down inside a level, a candidate the ladder refused, a
    // camera left over from another level - still answers false, and a destroyed or replaced wrapper is still refused.
    private static bool BandCameraAlive(Camera camera)
    {
        if (camera == null) return true;
        try
        {
            GameObject owner = camera.gameObject;
            return BandCameraLiveness.Alive(_bandCameraProved, _bandCameraManualCanvas,
                camera.isActiveAndEnabled, owner != null && owner.activeInHierarchy, readable: true);
        }
        catch
        {
            // A destroyed or replaced wrapper throws on the first member access: "could not be read" is an input of the
            // same rule, not a second copy of it, so the decision itself is what the headless scenarios assert.
            return BandCameraLiveness.Alive(_bandCameraProved, _bandCameraManualCanvas, false, false, readable: false);
        }
    }

    // The equipment column, read once per poll while the anchor is locked.
    //
    // The anchor itself is frozen after it settles, but the column is not: selecting a weapon expands its row, picking a
    // pack up shows one, readying a throwable adds another, and each of those moves the column's bottom edge after the
    // table was placed. `EquipmentBandWatch` decides what such a reading means - a band that *grew* is committed at once,
    // because a table further from the column cannot overlap it, and a band that *shrank* only once the column has really
    // stayed that way, so a row flickering open and shut never drags the table up and down.
    //
    // 1.19.46 (HUD review closure): the round's camera is re-proved here, from this level's own HUD, whenever the round has
    // none (`_bandCameraProved` false after a level change dropped it) or the one it had is gone. The locked anchor stays
    // locked by design, so no anchor read would ever run again - waiting for the player to reload the config or change
    // resolution was not a fix: the column would have stayed "unproved" for the rest of the session. `ReprooveBandCamera`
    // runs the one function that is allowed to decide which camera projects this HUD and takes only its camera.
    [HideFromIl2Cpp]
    private void WatchEquipmentBand(PlayerGuiLayer layer, float now)
    {
        if (!float.IsFinite(now) || now < _nextBandRead) return;
        _nextBandRead = now + BandInterval;
        if (!_bandCameraProved || !BandCameraAlive(_bandCamera)) ReprooveBandCamera(layer);
        EquipmentBandReading reading = ReadEquipmentBand(layer);
        _bandRows = Rows(reading);
        _bandWatch.Observe(reading);
        // 1.19.48 (review): the recovery gate counts consecutive *complete* readings. A poll that measured nothing, or only
        // part of the column, breaks the run - that is what keeps a flipping page from moving the table every other poll.
        if (_bandWatch.Has && _bandWatch.Complete) _bandCompleteStreak++;
        else _bandCompleteStreak = 0;
    }

    // 1.19.46: re-prove the column's camera from this level's own HUD, without touching the locked anchor.
    //
    // `ReadAnchor` is the one read that decides which camera may project this HUD, and it writes `_bandCamera` and
    // `_bandCameraProved` as part of that decision (a `null` camera it returns is a *proved* overlay answer, not a guess).
    // Running it here is that same measurement, used for its camera: the rectangle it returns is deliberately ignored, so
    // the anchor keeps exactly the place it was locked to. The attempts are bounded per round - a HUD that cannot prove a
    // camera at all must not be walked four times a second for the rest of the level - and the one line says which of the
    // two happened.
    private const int BandCameraReproofLimit = 8;
    private int _bandCameraReproofs;

    [HideFromIl2Cpp]
    private void ReprooveBandCamera(PlayerGuiLayer layer)
    {
        if (_bandCameraReproofs >= BandCameraReproofLimit) return;
        _bandCameraReproofs++;
        try { ReadAnchor(layer); } catch { }
        if (!_bandCameraProved) return;
        Plugin.Logger?.LogInfo("[DIAG] the equipment column has a proved camera again: it was measured on this level's own "
            + "HUD (" + (string.IsNullOrEmpty(_anchorCameraNote) ? "the HUD's own camera" : _anchorCameraNote) + ") after the "
            + "previous level's round was dropped, attempt " + _bandCameraReproofs + " of " + BandCameraReproofLimit
            + ". The column is read through it from this poll on, and the locked anchor keeps the place it had - the reset "
            + "drops the camera and the band, never the anchor.");
    }
    // Every row of the equipment column, as this HUD really lays it out.
    //
    // The chain is the one the anchor already trusts: `m_playerLayer -> Inventory`, and inside it the icon display the
    // anchor is measured from plus every slot the inventory lists in its own `m_slotGUIOrder`. That is a handful of
    // objects looked up by key - no hierarchy walk, no scene scan - and the list they are written to is the one
    // preallocated buffer, so the read costs the same on a full column as on an empty one and allocates nothing sized by
    // what it finds. Each row is projected by the camera this round proved for the anchor, through the same `ScreenBounds`
    // gate: a row that cannot be read is recorded as unread, which is what makes the band incomplete, and never as a row
    // at zero.
    //
    // 1.19.47: a row is no longer only its own `m_rectTrans` (`TryRowRect`). The recorded expedition read this column as
    // `rows=1 skipped=11` on a HUD whose anchor row measured perfectly (log 2769), because this build's
    // `PUI_InventoryItem` objects carry no rectangle of their own - the row's own native item name text does, and that
    // text is a rectangle the HUD really laid out for that row.
    [HideFromIl2Cpp]
    private EquipmentBandReading ReadEquipmentBand(PlayerGuiLayer layer)
    {
        _obstacles.Clear();
        Camera camera = _bandCamera;
        // 1.19.45: the rows are only projected through a camera this round really proved. Two facts are asked here and
        // they are not the same one: `_bandCameraProved` says a round has proved a projection at all (a level change
        // clears it, and the previous level's rectangle must never come back through it), and `BandCameraAlive` says the
        // object behind a proved camera still exists (a HUD torn down inside a level leaves a dead wrapper behind). A
        // `null` proved camera is the legitimate overlay answer and is *not* refused. A row that cannot be projected is
        // recorded as unread - which is what makes the band incomplete - and never as a row at zero.
        bool projectable = _bandCameraProved && BandCameraAlive(_bandCamera);
        RectTransform anchorRow = null;
        try
        {
            var display = layer != null && layer.Inventory != null ? layer.Inventory.m_iconDisplay : null;
            if (display != null) anchorRow = display.m_rectTrans != null ? display.m_rectTrans : display.transform.TryCast<RectTransform>();
        }
        catch { anchorRow = null; }
        if (anchorRow != null)
        {
            // The projection is caught on its own: a HUD object from a level that has already been torn down throws, and
            // an exception escaping this read would take the whole repaint with it. A throw here is one unread row.
            LayoutRect row = default;
            bool read = false;
            try { if (projectable) read = TryScreenRectFromLayout(anchorRow, camera, out row, out _, out _); }
            catch { read = false; }
            if (read) _obstacles.Add(new HudObstacle(AnchorElementNames.Flashlight, row, true));
            else _obstacles.Add(HudObstacle.Unread(AnchorElementNames.Flashlight, true));
        }
        try
        {
            var inventory = layer != null ? layer.Inventory : null;
            var order = inventory != null ? inventory.m_slotGUIOrder : null;
            var slots = inventory != null ? inventory.m_inventorySlots : null;
            int known = 0, named = 0;
            try { known = slots != null ? slots.Count : 0; } catch { known = 0; }
            if (order != null && slots != null)
            {
                // The buffer is bounded by construction: this HUD draws a handful of rows, and a build that ever answered
                // more is read no further than the ceiling instead of being trusted to be small.
                int count = order.Length;
                int ceiling = EquipmentBand.MaxObstacles - _obstacles.Count;
                if (count > ceiling) count = ceiling;
                for (int index = 0; index < count; index++)
                {
                    // 1.19.43: one row is isolated from every other row, and from the column.
                    //
                    // `order[index]`, `"slot:" + slot` and `slots.TryGetValue` all reach into the interop object, and a
                    // slot whose handle is gone throws on the first of them. Until this round that single throw escaped
                    // to the catch below and answered `EquipmentBandReading.None`: the whole column vanished - a
                    // recorded frame logged "band=none rows=0 skipped=0" beside a perfectly measurable flashlight row -
                    // so `HudPlacement` had nothing to avoid, `TryAnchoredOrigin` returned at its first line, and the
                    // table fell back to the configured PositionX/PositionY, which on this HUD lands on the right-hand
                    // column. That is the very overlap 1.19.41 was written to remove, still reachable through this
                    // seam. The anchor row above has been isolated like this since then; every slot row is isolated
                    // the same way now, so one bad row costs one unread row - the band stays measured and becomes
                    // partial, which (1.19.47) is a reading the panel refuses to place from and defers on - instead of
                    // the whole measurement.
                    string name = "slot#" + index;
                    // Until this build says otherwise the row is treated as drawn: a row nobody could read has to be
                    // able to keep the band open, never to shrink it to the anchor row. Treating an unreadable row as
                    // "not on the screen" is how a partial reading would be passed off as a complete one.
                    bool drawn = true;
                    try
                    {
                        InventorySlot slot = order[index];
                        name = "slot:" + slot;
                        PUI_InventoryItem item;
                        if (!slots.TryGetValue(slot, out item) || item == null) continue;   // this inventory has no such row
                        named++;
                        // An `IsVisible` that throws is a row this build cannot ask about, so it is kept as drawn.
                        try { drawn = item.IsVisible; } catch { drawn = true; }
                        if (projectable && TryRowRect(item, camera, out var slotRect, out var evidence))
                            _obstacles.Add(new HudObstacle(name + evidence, slotRect, drawn, fullRow: evidence != "(name-text)"));
                        else if (drawn) _obstacles.Add(HudObstacle.Unread(name, true));
                    }
                    catch
                    {
                        // The row is stated as unread and the loop moves on to the next one: the reading is partial, and a
                        // partial reading proves no edge of the column - not its bottom (`below-band`), not its top
                        // (`above-band`) and not its left (`beside-band`) - so the panel defers on it instead of drawing.
                        if (drawn) _obstacles.Add(HudObstacle.Unread(name, true));
                    }
                }
            }
            // 1.19.41: the inventory holds more rows than its GUI order named - an order list that has not been filled in
            // yet, or one a build answers differently. Those rows are on the screen and this build did not measure them,
            // so they are *stated* as unread rather than quietly dropped: dropping them would leave the band ending at the
            // anchor row, and a table placed below that is the very overlap this round removes.
            for (int index = named; index < known; index++)
            {
                if (_obstacles.Count >= EquipmentBand.MaxObstacles) break;
                _obstacles.Add(HudObstacle.Unread("slot:unnamed" + (index - named), true));
            }
        }
        catch
        {
            // A row list that throws is a column this build cannot describe, which is exactly what must not be read as
            // "the column ended here": the read stops and an unmeasured band is answered, so the caller keeps what it had.
            return EquipmentBandReading.None;
        }
        return EquipmentBand.Read(_obstacles, Screen.width, Screen.height);
    }

    // The rows one read saw, in the words the placement line prints.
    [HideFromIl2Cpp]
    private string Rows(EquipmentBandReading reading)
    {
        var text = new System.Text.StringBuilder(192);
        text.Append(reading.Describe()).Append(" rows:");
        for (int index = 0; index < _obstacles.Count; index++)
        {
            if (index > 0) text.Append(',');
            text.Append(_obstacles[index].Describe());
        }
        return text.ToString();
    }

    // One row of the equipment column, measured from the elements that row really draws (1.19.47).
    //
    // The row's own rectangle is the first answer: `item.m_rectTrans`, then the item's own transform when the interop
    // answers that field as null. The recorded expedition shows why that cannot be the only answer. On its own HUD the
    // anchor row measured (`flashlight=(2336.08,294.33,210.59x110.16)`) while every slot row came back unread
    // (`rows=1 skipped=11`, log 2769/2777), which is what a row without a `RectTransform` of its own looks like: this
    // build's `PUI_InventoryItem` is not the uGUI object, so the only rectangles its row has are the ones it draws.
    //
    // So the row is also read through its own native item name text - `m_selected_archetypeName` / `m_slim_archetypeName`,
    // the two `TextMeshPro`s this HUD alternates between, chosen by the same active-in-hierarchy rule the sibling HUD
    // reader already anchors its own labels with (`EquipmentTimers`, `Ui/TimerSlotRow.cs` and
    // `Ui/TimerController.ResolveName`; same game build, same interop profile, no member invented here). Both answers are
    // rectangles the HUD laid out for that row, projected through the camera this round proved and gated by the same
    // `ScreenBounds`; a row that answered both is the union of them. Nothing is derived from a screen fraction or a
    // per-row pixel constant.
    //
    // A name-only rectangle is useful obstacle evidence, not proof of the native row's full size.
    [HideFromIl2Cpp]
    private static bool TryRowRect(PUI_InventoryItem item, Camera camera, out LayoutRect rect, out string evidence)
    {
        rect = default;
        evidence = null;
        if (item == null) return false;
        LayoutRect row = default;
        bool rowRead = false;
        try
        {
            RectTransform own = item.m_rectTrans != null ? item.m_rectTrans : item.transform.TryCast<RectTransform>();
            if (own != null) rowRead = TryScreenRectFromLayout(own, camera, out row, out _, out _);
        }
        catch { rowRead = false; }
        LayoutRect name = default;
        bool nameRead = false;
        try
        {
            RectTransform label = NativeItemName(item);
            if (label != null) nameRead = TryScreenRectFromLayout(label, camera, out name, out _, out _);
        }
        catch { nameRead = false; }
        if (rowRead && nameRead)
        {
            rect = EquipmentBand.Union(row, name);
            evidence = "(row+name-text)";
            return true;
        }
        if (rowRead) { rect = row; evidence = "(row-rect)"; return true; }
        if (nameRead) { rect = name; evidence = "(name-text)"; return true; }
        return false;
    }

    // The native item name this row is really drawing, as an object that carries a rectangle.
    //
    // The rule is the default branch of the one `EquipmentTimers` resolves its own row anchors with: the selected name
    // while it is on, otherwise the slim one. Nothing here asks for the state word (`CurrentState`), because the question
    // this build needs answered is only "which of the two is on the screen" - and the two names are alternated by the HUD
    // itself, so the active one is the row's own drawn text.
    [HideFromIl2Cpp]
    private static RectTransform NativeItemName(PUI_InventoryItem item)
    {
        TextMeshPro selected = null, slim = null;
        try { selected = item.m_selected_archetypeName; } catch { selected = null; }
        try { slim = item.m_slim_archetypeName; } catch { slim = null; }
        if (Drawn(selected)) return RectOf(selected);
        if (Drawn(slim)) return RectOf(slim);
        return null;
    }

    // Whether a native text is on the screen this poll. A wrapper whose object is gone throws on the first member, and
    // "could not be read" is answered as "not drawn": a text this build cannot ask about must not push the table
    // anywhere, and the row it belongs to is stated as unread instead.
    [HideFromIl2Cpp]
    private static bool Drawn(TextMeshPro text)
    {
        if (text == null) return false;
        try { return text.gameObject != null && text.gameObject.activeInHierarchy; }
        catch { return false; }
    }

    [HideFromIl2Cpp]
    private static RectTransform RectOf(TextMeshPro text)
    {
        try { return text.transform.TryCast<RectTransform>(); }
        catch { return null; }
    }

    [HideFromIl2Cpp]
    private void LogOnce(Exception error)
    {
        if (_loggedError) return;
        _loggedError = true;
        Plugin.Logger?.LogError("Panel failed: " + error);
    }
}
