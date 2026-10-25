using System;
using System.Collections.Generic;
using System.Linq;
using CellMenu;
using HarmonyLib;
using Player;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HawChow.GTFOCombatPanel;

// The success and failure screens. The influence board is drawn by IMGUI in window pixels on top of
// the page: nothing is written into a vanilla text object and nothing inside a report card is
// repositioned, so the two can never fight, and because the board is in screen space it keeps every
// column on screen instead of being clipped by whatever page happens to be up.
//
// The vanilla blocks the board replaces are switched off in place, one at a time and each in its own
// guard, and only when the config says so: a field that is null on this game build is skipped and
// the board simply draws over it.
internal static class ResultsDisplay
{
    internal static CM_PageExpeditionSuccess Success;
    internal static CM_PageExpeditionFail Fail;

    private static TableContent _content;
    private static PanelGrid _grid;
    private static LayoutRect _origin;

    // The glyph floor the grid on screen was fitted with, so a refresh that has to re-measure it asks for
    // the same floor the placement used instead of quietly falling back to the absolute minimum.
    private static int _fontFloor = ResultsFontGrowth.MinimumReadablePixels;
    // The page the board is standing in for, kept so the vanilla blocks it replaces can be put away
    // again on every frame: the result pages animate their content in and switch it back on as they
    // go, which is what makes a one-off SetActive(false) blink back in.
    private static Component _page;
    private static bool _succeeded;
    private static bool _loggedError, _loggedDraw, _loggedHidden, _loggedMeasure;
    private static readonly HashSet<string> HeadingReports = new HashSet<string>();

    // The vanilla blocks this mod has moved, with the exact position and active state each had before it
    // touched them. Restoration is the inverse of hiding, one block at a time: the moment a flag is
    // switched off, the page changes or the results screen is left, every block goes back to exactly
    // where it was and exactly the state it was in. The previous release only ever moved them off the
    // page, so switching the flag off left them off the page for good.
    private sealed class HiddenBlock
    {
        public Transform Target;
        public GameObject Host;
        public Vector3 Position;
        public bool Active;
    }
    private static readonly Dictionary<IntPtr, HiddenBlock> Hidden = new Dictionary<IntPtr, HiddenBlock>();
    // What was hidden the last time the page was touched, so a flag that changed can be noticed and the
    // blocks it covered restored before the new set is applied.
    private static bool _appliedSuccessBlocks, _appliedPrisonerReports, _appliedFailBlocks, _appliedAny;
    // Clearance between the failure screen's own message and the board that stands under it.
    private const float MessageGap = 16f;
    // Tolerance for the page's own elements drifting a pixel or two between the measurement and the draw,
    // well inside the 16 px gap the strip leaves above the buttons.
    internal const float BoardSlack = 2f;

    internal static void Refresh(eGameStateName state)
    {
        bool succeeded = state == eGameStateName.ExpeditionSuccess;
        bool results = succeeded || state == eGameStateName.ExpeditionFail;
        // Leaving the results screens puts every vanilla block this mod moved back exactly where it was and
        // forgets everything that belonged to the page: the next run's page must never inherit this run's
        // hiding, its placement or its readiness.
        if (!results) { RestoreHidden(); ResetPlacement(); ForgetPage(); FailureAlignment.Forget(); return; }
        // Three switches have to agree before a single pixel is drawn, and the F8 toggle stands in front of
        // all three. With any of them off the result page is left exactly as the game drew it: there is no
        // older statistics table behind them to fall back to. What the page does keep is what it has already
        // earned - its placement, the strip it was measured in and the readiness of its completion section -
        // so turning the board back on is immediate instead of waiting for a section that is already drawn.
        bool overlay = ResultVisibility.Shows(
            Plugin.ShowResults == null || Plugin.ShowResults.Value,
            Plugin.ShowResultOverlay == null || Plugin.ShowResultOverlay.Value,
            Plugin.ShowImpactBoard == null || Plugin.ShowImpactBoard.Value,
            PanelBehaviour.ToggleState);
        if (!overlay)
        {
            if (_page != null) RestoreHidden();
            ResetPlacement();
            return;
        }
        // The placed board keeps its position and size while current cells fit its measured grid.
        // Polls refresh the text and archive; geometry is remeasured only when updated data outgrows it.
        if (_placed && _placedSucceeded == succeeded && _grid != null && _content != null &&
            _page != null && _page.gameObject.activeInHierarchy)
        {
            if (RefreshBoardData(_page, succeeded)) return;
        }
        Component previous = _page;
        ResetPlacement();
        if (previous != null) RestoreHidden();
        Component page = null;
        if (succeeded && Success != null && Success.gameObject.activeInHierarchy) page = Success;
        else if (!succeeded && Fail != null && Fail.gameObject.activeInHierarchy) page = Fail;
        if (page == null) return;
        // A different page is a different screen: the placement, the strip it was measured in and the
        // readiness of the previous one are dropped here and nowhere else. A configuration change, the F8
        // write it triggers and the hot reload that follows it all leave them standing.
        if (_holdPage != page.Pointer) ForgetPage();
        _holdPage = page.Pointer;
        _page = page; _succeeded = succeeded;
        Readiness.Bind(page.Pointer);
        try
        {
            Prepare(page, succeeded);
            if (_placed) ApplyVanillaBlocks(page, succeeded);
        }
        catch (Exception error)
        {
            if (!_loggedError) { _loggedError = true; Plugin.Logger?.LogError("Results display failed: " + error); }
        }
    }

    // The caches the settlement board holds: the measured grid, its position, and the page it belongs to.
    // Cleared whenever the page changes, the overlay is switched off, or a configuration change asks for a
    // remeasurement. An ordinary counter update never reaches here, so a board that has settled keeps the
    // size and position it was measured with while the figures under it change. Deliberately does not touch
    // the per-page hold below: a re-placement attempt for the same page is exactly what has to keep it.
    private static void ResetPlacement()
    {
        _grid = null; _content = null; _page = null; _succeeded = false; _placed = false; _placedSucceeded = false;
    }

    // Everything the board caches for one result page: the place it was measured into, the strip that place
    // came from and the readiness its completion section has already earned. None of it is dropped by
    // `ResetPlacement`, and that is the whole point of it: F8 writes the configuration file, that write comes
    // back through `Plugin.ApplyConfig` as a second invalidation a moment later, and the board must neither
    // wait again for a readiness it has already been given nor lose the strip it was measured against and be
    // refused by what is left of it. It is dropped when the page changes, when a different kind of result
    // screen comes up and when the results screens are left - and by nothing else.
    private static IntPtr _holdPage;
    private static bool _holdValid;
    private static LayoutRect _holdBoard;
    private static float _holdOpacity;
    // The page animates its footer in. A button measured mid-animation sits higher than the one the player
    // ends up pressing, and the board built against that edge is permanently too short - `TryHold` never
    // enlarges a board, by design, so a first measurement taken too early is never corrected. These wait
    // for the edge to stop moving before the one and only measurement is taken; after that the board is
    // fixed for the life of the page, which is what keeps a redraw or a hover from resizing it.
    private static float _seedButtonTop = float.NaN;
    private static int _seedSettleFrames, _seedAttempts, _seedButtonMisses;
    private const int ButtonSettleFrames = 2;
    private const int ButtonSettleAttempts = 120;
    private static readonly CompletionReadiness Readiness = new CompletionReadiness();

    private static void ForgetPage()
    {
        _holdPage = IntPtr.Zero;
        _holdValid = false;
        _holdBoard = default;
        _holdOpacity = 0f;
        _seedButtonTop = float.NaN; _seedSettleFrames = 0; _seedAttempts = 0; _seedButtonMisses = 0;
        Readiness.Forget();
        // A new result screen reports its own board: the layout line belongs to the screen, not to the
        // session. A page pointer is reused across a session's runs, so a once-per-session latch left every
        // result screen after the first without its own line and its own archive entry in the log.
        _loggedDraw = false;
    }

    // A configuration change (weights, thresholds, columns, the vanilla-block flags) may move the board or
    // the blocks it stands in for, so the caches are dropped and the next poll remeasures. Called on the
    // main thread from Plugin.ApplyConfig.
    internal static void Invalidate() => ResetPlacement();

    internal static void Draw()
    {
        // F8 hides this mod's overlay on a result page exactly as it hides the table in a level, and the
        // influence-board switch holds it back on its own. The vanilla blocks go back on their own page, so
        // the screen is never left half-hidden.
        if (!PanelBehaviour.ToggleState || Plugin.ShowImpactBoard != null && !Plugin.ShowImpactBoard.Value)
        {
            if (_page != null) { RestoreHidden(); ResetPlacement(); }
            return;
        }

        // Nothing is touched until the board is really about to be drawn: the vanilla blocks belong to the
        // page and are only put away while a board stands in for them. Hiding them for a page whose board
        // never appears leaves the screen half-empty with nothing drawn over the gap.
        if (_grid == null || _content == null || _page == null || !_page.gameObject.activeInHierarchy) return;

        float opacity = 1f;
        if (!_succeeded && _content.Kinds != null)
        {
            if (_holdValid)
            {
                // The board is up and it stays up. Its strip was measured when it arrived, and re-measuring the
                // page's own message and buttons on every frame is exactly what used to take it away again:
                // those elements animate, a button's own bounds move by a pixel or two while it is hovered, and
                // a strip a fraction of a pixel shorter than the board refused it - which is why the board only
                // came back while the player happened to hover the page. The one edge that still has to hold is
                // the button the player has to press, so that is the only measurement made here, and the board
                // keeps its place unless it would really reach it.
                _holdOpacity = 1f; // Once shown, only the explicit visibility switches can hide this page's board.
                opacity = _holdOpacity;
                bool buttonMeasured = LobbyButtonTop(_page, out float buttonTop);
                if (!ResultBoardHold.TryHold(new BoardStrip(_holdBoard.Y, _holdBoard.Bottom, buttonMeasured, buttonTop), _holdBoard, Screen.height, out LayoutRect held)) return;
                if (Math.Abs(_origin.Y - held.Y) > 0.5f && HeadingReports.Add("failure-board-lifted"))
                    Plugin.Logger?.LogInfo($"[DIAG] failure board lifted clear of the lobby button: the board was at y={_origin.Y:0}, the button's own top edge is {(float.IsFinite(buttonTop) ? buttonTop.ToString("0") : "unmeasurable")}, and the board now stands at y={held.Y:0}; it is lifted rather than dropped.");
                _origin = held;
            }
            else
            {
                // A first placement still has to fit the strip the page measured: below the page's own message
                // and above the buttons the player has to press. A board that cannot fit is not drawn rather
                // than drawn over a button.
                if (!FailureStrip(_page, out float top, out float bottom, out opacity, out bool buttonMeasured, out float buttonTop, out float buttonHeight)) return;
                if (!ResultBoardHold.TryPlace(Screen.width, Screen.height, new BoardStrip(top, bottom, buttonMeasured, buttonTop, buttonHeight), _grid.Width, _grid.Height,
                    Plugin.ResultsOffsetX.Value, out LayoutRect fitted, BoardSlack))
                {
                    if (HeadingReports.Add("fail-no-room"))
                        Plugin.Logger?.LogInfo($"[DIAG] failure board held back: strip=({top:0}..{bottom:0}) cannot hold a {_grid.Width:0}x{_grid.Height:0} board, so the vanilla screen is left alone rather than a button covered.");
                    return;
                }
                _origin = fitted;
            }
        }
        // The page switches the blocks the board replaces back on as its own animation plays, so they are
        // put away again on every frame the board is up, not once per poll. This happens after every gate
        // that can still hold the board back, so a frame that draws nothing also hides nothing: the vanilla
        // blocks only ever make way for a board that is really about to be drawn. It is guarded on its own
        // so a page that has already gone away can never take the board down with it.
        try { ApplyVanillaBlocks(_page, _succeeded); }
        catch (Exception error)
        {
            _page = null;
            if (!_loggedMeasure)
            {
                _loggedMeasure = true;
                Plugin.Logger?.LogWarning("[DIAG] the result page went away while its blocks were being put back, so the board stops touching it: " + error.Message);
            }
        }
        if (_page == null) return;
        // The success board keeps the position it was measured at. Re-measuring it on every frame made the
        // table resize while the page was still animating, and a table that has settled must not move: it is
        // placed once, and placed again only when the results screen itself changes.
        HudTable.Draw(_grid, _origin.X, _origin.Y, _content, opacity);

        if (!_succeeded && HeadingReports.Add("failure-board-placed"))
            Plugin.Logger?.LogInfo($"[DIAG] failure board placed: board=({_origin.X:0},{_origin.Y:0},{_grid.Width:0}x{_grid.Height:0}), successBoardTop={(FailureAlignment.AnchorKnown(Screen.height) ? FailureAlignment.Anchor.ToString("0") : "unseen")}, the two result screens carry their board in the same strip and neither overlaps the lobby button.");
    }

    // The result pages animate their own content in over the first frames, so the board has to attach
    // on the frame the page exists rather than on the next poll: the quarter second the poll can take
    // is long enough for the vanilla blocks to be seen before they are put away.
    private static bool _placed;             // a placement has been computed for the screen now up
    private static bool _placedSucceeded;    // and it was computed for this kind of results screen
    internal static bool NeedsAttach()
    {
        if (!Plugin.ShowResults.Value) return false;
        var state = GameStateManager.CurrentStateName;
        if (state != eGameStateName.ExpeditionSuccess && state != eGameStateName.ExpeditionFail) return false;
        bool succeeded = state == eGameStateName.ExpeditionSuccess;
        // A placement belongs to the screen it was measured on. The success page measures its strip once, so a
        // failure placement carried onto a success page would leave the board on the failure coordinates -
        // straight over the completion icons. A different kind of results screen always re-attaches.
        return _page == null || _grid == null || !_placed || _placedSucceeded != succeeded;
    }

    // ---- the influence board -------------------------------------------------------------------

    private static void Prepare(Component page, bool succeeded)
    {
        if (!Placement(page, succeeded, out _, out float room, out BoardStrip strip)) return;
        ImpactReport report = BuildReport(succeeded);
        if (report.Entries.Count == 0)
        {
            if (HeadingReports.Add("empty-roster")) Plugin.Logger?.LogWarning("[DIAG] result board waiting: no retained player counters.");
            return;
        }
        ImpactBoardLayout board = ImpactBoard.Build(report, true, succeeded); // Settlement kill tiers are always explicit.
        // The initial accepted projection is archived; later snapshots refresh the same file in place.
        ResultsSaver.Save(page, succeeded, board, report, CombatSync.Generation);
        var content = new TableContent
        {
            Columns = board.Columns,
            Text = board.Text,
            Colors = Colors(board, report),
            FieldSeparator = ImpactBoard.ValueSeparator,
            Dividers = Dividers(board),
            Kinds = board.Kinds
        };
        if (room <= 0f) return;
        float boardWidth = Mathf.Max(320f, Screen.width - 48f);
        PanelGrid grid = FitBoard(content, room, boardWidth, strip.ButtonHeight);
        if (grid == null) return;
        Fit(strip, grid, content, board, report, succeeded);
        _placed = true; _placedSucceeded = succeeded;
    }

    // The glyph size the settlement board is drawn at, and the grid that size produces.
    //
    // The strip between the page's completion section and the lobby button is usually far taller than the table
    // needs, and it is occasionally shorter than the sharing size wants, so the size is searched rather than
    // guessed: `ResultsFontGrowth.FitIntoStrip` measures the table at a candidate size and answers with the
    // largest one that fits - down as well as up. Measuring and then squeezing the table into the box, which
    // is what a climb that refuses to look downwards does, is how a board ended up drawn at ~9px with the top
    // of its strip left empty.
    //
    // `NaturalSize` is the measurement and the box is what is being chosen here, so the table is measured
    // with no box at all. The width the page offers then spreads the finished grid across the page through
    // `PanelGeometry.FillWidth`, which hands the room the figures did not need to the right-aligned value
    // columns and leaves the label column - and the grid's height, row height and glyph size - exactly where
    // they were. Neither step can push the board over the icons, the title or the button: the fit's own
    // ceiling stays below the strip and inside the width the page offers.
    private static PanelGrid FitBoard(TableContent content, float room, float boardWidth, float buttonHeight = float.NaN)
    {
        int seed = Mathf.Max(1, PanelBehaviour.FontSizeForSharing);
        // The readability floor is taken from the button the player has to press. A strip that is short
        // leaves the table no room to grow, but it may not shrink it to a size nobody can read while that
        // control is right underneath: the floor travels into the same build the search measures with, so
        // the glyph size and the row height the board is drawn at honour it. A page that hands no measurable
        // button height keeps the absolute minimum, and every figure here is what it was before.
        int requested = ResultsFontGrowth.ReadabilityFloor(buttonHeight);
        // The floor is a preference, not a licence to draw nothing. A strip too short to hold the table at
        // the readable size used to end up with a grid taller than the strip, which the placement then
        // refused - the board vanished instead of shrinking. The size is asked for from a search that
        // reports which floor it really used, and the grid that comes out of it is checked against the strip
        // as well: a board that still overruns is rebuilt at the absolute minimum, which is the same board
        // this page drew before the floor existed.
        int fontSize = ResultsFontGrowth.FitBoardIntoStrip(seed, requested, int.MaxValue, room, boardWidth,
            size => NaturalSize(content, size, requested), out int floor);
        PanelGrid grid = BoardGrid(content, fontSize, floor, room, boardWidth);
        if (!InStrip(grid, room, boardWidth) && floor > ResultsFontGrowth.MinimumReadablePixels)
        {
            floor = ResultsFontGrowth.MinimumReadablePixels;
            grid = BoardGrid(content, fontSize, floor, room, boardWidth);
        }
        // The floor the grid on screen was really built with, so a later refresh re-measures with that one
        // instead of asking for a floor this strip already proved it cannot honour.
        _fontFloor = floor;
        return grid;
    }

    private static PanelGrid BoardGrid(TableContent content, int fontSize, int floor, float room, float boardWidth)
    {
        PanelGrid grid = HudTable.BuildGrid(content, fontSize, Plugin.Density, boardWidth, room, floor);
        return grid == null ? null : PanelGeometry.FillWidth(grid, boardWidth);
    }

    // A board the placement will accept: no taller than the strip between the completion section and the
    // lobby button, and no wider than the page. The half pixel is the rounding a measured width or height
    // can leave behind; a grid a fraction over is still a board the result page refuses to draw.
    private static bool InStrip(PanelGrid grid, float room, float boardWidth)
        => grid != null && grid.Height <= room + 0.5f && grid.Width <= boardWidth + 0.5f;

    // The table's own extents at one glyph size, measured without a box to fit into: the box is what is being
    // chosen here, so measuring inside one would only report that box back instead of the table's real size.
    private static ResultTableSize NaturalSize(TableContent content, int fontSize, int minimumPixels = ResultsFontGrowth.MinimumReadablePixels)
    {
        PanelGrid measured = HudTable.BuildGrid(content, fontSize, Plugin.Density, float.PositiveInfinity, float.PositiveInfinity, minimumPixels);
        if (measured == null) return ResultTableSize.None;
        return new ResultTableSize(measured.Width, measured.Height);
    }

    // Result snapshots continue arriving after the page opens. Refresh the displayed board and its existing
    // archive entry from the same projection, while keeping its placement unless the current grid cannot fit.
    private static bool RefreshBoardData(Component page, bool succeeded)
    {
        if (page == null || _grid == null || _content == null) return false;
        ImpactReport report = BuildReport(succeeded);
        if (report.Entries.Count == 0) return true;
        ImpactBoardLayout board = ImpactBoard.Build(report, true, succeeded);
        ResultsSaver.Save(page, succeeded, board, report, CombatSync.Generation);
        if (board.Rows != _grid.Rows || board.ColumnCount != _grid.Columns) return false;
        var content = new TableContent
        {
            Columns = board.Columns,
            Text = board.Text,
            Colors = Colors(board, report),
            FieldSeparator = ImpactBoard.ValueSeparator,
            Dividers = Dividers(board),
            Kinds = board.Kinds
        };
        float boardWidth = Mathf.Max(320f, Screen.width - 48f);
        PanelGrid measured = HudTable.BuildGrid(content, Mathf.Max(1, Mathf.RoundToInt(_grid.FontPixels)), Plugin.Density, boardWidth, _grid.Height, _fontFloor);
        if (measured != null && !GridCovers(_grid, measured)
            && Placement(page, succeeded, out _, out float room, out BoardStrip strip) && room > 0f)
        {
            // The strip may have grown since the board arrived, so the same fit the first placement used is run
            // again rather than reusing the glyph size it happened to land on: a refreshed snapshot re-measures.
            PanelGrid resized = FitBoard(content, room, boardWidth, strip.ButtonHeight);
            if (resized != null) Fit(strip, resized, content, board, report, succeeded);
        }
        _content = content;
        return true;
    }

    private static bool GridCovers(PanelGrid current, PanelGrid required)
    {
        if (current == null || required == null || current.Rows != required.Rows || current.Columns != required.Columns
            || required.FontPixels < current.FontPixels - 0.5f || current.FieldWidths == null || required.FieldWidths == null)
            return false;
        for (int column = 0; column < current.Columns; column++)
        {
            float[] available = current.FieldWidths[column];
            float[] needed = required.FieldWidths[column];
            if (available == null || needed == null || available.Length != needed.Length) return false;
            for (int field = 0; field < available.Length; field++)
                if (needed[field] > available[field] + 0.5f) return false;
        }
        return true;
    }
    // Every player as the board sees them: the damage and accuracy counters that already existed, the
    // impact counters this release adds, and the four gear names.
    private static ImpactReport BuildReport(bool countLoneSurvivor)
    {
        var inputs = new List<ImpactInput>();
        foreach (CombatPlayer player in CombatTracker.SettlementView().OrderBy(entry => entry.DisplaySlot))
        {
            WeaponCombatStats primary = WeaponOf(player, InventorySlot.GearStandard);
            WeaponCombatStats secondary = WeaponOf(player, InventorySlot.GearSpecial);
            WeaponCombatStats melee = WeaponOf(player, InventorySlot.GearMelee);
            WeaponCombatStats tool = WeaponOf(player, InventorySlot.GearClass);
            var input = ImpactInput.From(player.Name, player.Total, player.Impact, player.SupportRole,
                primary != null ? primary.Counters : null,
                secondary != null ? secondary.Counters : null,
                melee != null ? melee.Counters : null,
                tool);
            input.Lookup = player.Lookup;
            input.PrimaryName = Name(primary);
            input.SecondaryName = Name(secondary);
            input.MeleeName = Name(melee);
            inputs.Add(input);
        }
        double firepower = Plugin.SupportRoleFirepowerBonus != null ? Plugin.SupportRoleFirepowerBonus.Value : 1.0;
        double utility = Plugin.SupportRoleUtilityBonus != null ? Plugin.SupportRoleUtilityBonus.Value : 1.0;
        // A failed expedition neither shows nor scores the last-one-standing metric, so the two halves
        // are decided together here rather than by hiding a figure the score still counted.
        return ImpactScoring.Evaluate(inputs, Plugin.ScoringWeights(), firepower, utility, countLoneSurvivor);
    }

    private static WeaponCombatStats WeaponOf(CombatPlayer player, InventorySlot slot)
    {
        foreach (WeaponCombatStats weapon in player.Weapons) if (weapon.Slot == (int)slot) return weapon;
        return null;
    }

    private static string Name(WeaponCombatStats weapon)
        => weapon == null || string.IsNullOrEmpty(weapon.Name) ? string.Empty : weapon.Name;

    // The tone the board decided is turned into a colour here, which is the only place in the mod that
    // knows what gold or copper look like.
    private static Color[,] Colors(ImpactBoardLayout board, ImpactReport report)
    {
        int rows = board.Rows, columns = board.ColumnCount;
        var colors = new Color[rows, columns];
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++) colors[row, column] = ToneColor(board.Tone(row, column), column, report);
        }
        return colors;
    }

    private static Color ToneColor(ImpactTone tone, int column, ImpactReport report)
    {
        switch (tone)
        {
            case ImpactTone.PlayerColor:
                {
                    int index = column - 1;
                    if (report != null && index >= 0 && index < report.Entries.Count) return CombatTracker.PlayerColor(report.Entries[index].Input.Lookup);
                    return Color.white;
                }
            case ImpactTone.Gold: return new Color(1f, 0.84f, 0f);
            case ImpactTone.Silver: return new Color(0.75f, 0.75f, 0.75f);
            case ImpactTone.Copper: return new Color(0.8f, 0.5f, 0.2f);
            default: return Color.white;
        }
    }

    private static void Fit(BoardStrip strip, PanelGrid grid, TableContent content, ImpactBoardLayout board, ImpactReport report, bool succeeded)
    {
        // The board is placed inside the strip its own measurement produced, not merely inside the window: a
        // board taller than that strip used to be accepted here and then refused at draw time, which is how a
        // whole table was dropped for a fraction of a pixel of overflow.
        if (ResultBoardHold.TryPlace(Screen.width, Screen.height, strip, grid.Width, grid.Height,
            Plugin.ResultsOffsetX.Value, out LayoutRect origin, BoardSlack, bottomAligned: true))
        {
            _origin = origin;
            _content = content; _grid = grid;
            // The place the board was measured into, kept for as long as this page is up: the F8 write, the hot
            // reload that follows it and the page's own button animation all come back through here, and none of
            // them may cost the board the strip it was already given.
            _holdValid = true; _holdBoard = origin; _holdOpacity = 0f;
            float x = origin.X, y = origin.Y;
            if (!_loggedDraw)
            {
                _loggedDraw = true;
                float stripHeight = Math.Max(1f, strip.Bottom - strip.Top);
                Plugin.Logger.LogInfo($"[DIAG] influence board {board.ColumnCount} columns x {board.Rows} rows ({report.Entries.Count} players), {grid.Width:0}x{grid.Height:0} px at ({x:0},{y:0}) of a {Screen.width}x{Screen.height} window, {(succeeded ? "success" : "failure")} screen, glyph {grid.FontPixels:0}px filling {grid.Height * 100f / stripHeight:0}% of the {stripHeight:0}px strip between the completion section and the lobby button ({grid.Width * 100f / Math.Max(1f, Screen.width - 48f):0}% of the {Screen.width - 48}x width it was fitted to); drawn by IMGUI, no vanilla report card was modified.");
            }
            return;
        }
        // The strip came back shorter than the board. The result pages animate their own buttons and a button's
        // own bounds move by a pixel or two while it is hovered, so a board measured against one frame can be
        // refused by the next - which is exactly how the failure board disappeared and only came back while the
        // player happened to hover the page. A board that is already up on this page keeps the place it was
        // measured into; `Draw` holds it there and lifts it off the button if the button really did move onto it.
        if (_holdValid && _page != null && ResultVisibility.SamePage(_holdPage, _page.Pointer))
        {
            _origin = _holdBoard;
            _content = content; _grid = grid;
            if (HeadingReports.Add("board-held"))
                Plugin.Logger?.LogInfo($"[DIAG] result board kept: the strip measured {strip.Top:0}..{strip.Bottom:0} cannot hold the {grid.Width:0}x{grid.Height:0} board that is already up, so it keeps the place it was measured into at ({_holdBoard.X:0},{_holdBoard.Y:0}), and only the lobby button is read from here on.");
            return;
        }
        if (HeadingReports.Add("fit-failed")) Plugin.Logger?.LogWarning($"[DIAG] result board cannot fit: top={strip.Top:0}, bottom={strip.Bottom:0}, grid={grid.Width:0}x{grid.Height:0}, screen={Screen.width}x{Screen.height}.");
    }

    // Where the board's top edge goes and how much room it has, together with the strip that produced it and
    // the lobby button that bounds it, so that place can be held afterwards. On the failure screen the board
    // waits for the page's own message to be measurable and then sits under it: a board that landed before the
    // message would be seen on its own and jump as the message faded in, which is the flicker that was
    // reported. On the success screen the board measures the strip between the expedition's completion section
    // and the button that sends the squad home, and the table scales its font and row spacing to that strip, so
    // all four players fit without covering either element.
    private static bool Placement(Component page, bool succeeded, out float top, out float room, out BoardStrip strip)
    {
        top = 0f; room = 0f; strip = default;
        if (!succeeded)
        {
            if (!FailureStrip(page, out top, out float bottom, out _, out bool buttonMeasured, out float buttonTop, out float buttonHeight)) return false;
            // The footer animates in, and a button measured mid-animation sits higher than the one the
            // player ends up pressing. A held board is never enlarged, so a first measurement taken too early
            // fixes the table permanently below the room the settled page actually offers. Wait for the edge
            // to stop moving (bounded, in case a hover keeps nudging it), then measure once and fix it for
            // the life of the page - which is also why nothing here ever enlarges it afterwards.
            if (!ButtonSettled(buttonMeasured, buttonTop)) return false;
            strip = new BoardStrip(top, bottom, buttonMeasured, buttonTop, buttonHeight);
            room = Math.Min(bottom, Screen.height - 8f) - top;
            return room > 0f;
        }
        ResultsSafeArea area;
        // The success screen waits for its own completion section: the sector name and the sector icon have to
        // be *drawn*, and the wait does not end until they are. There is no elapsed-time bound under it and
        // nothing that counts as "the page stopped changing" - a page that never finishes drawing never gets its
        // board, and this is asked again on every frame the board is not yet up.
        if (!CompletionReady(page)) return false;
        bool measuredButton;
        float measuredTop;
        float measuredHeight;
        if (!SuccessRegion(page, out area, out measuredButton, out measuredTop, out measuredHeight))
        {
            // Neither edge could be read on this build, so the board keeps the historical fraction of the
            // window. Only the top of the page is cleared in that case, never a measured element drawn over.
            // The readiness gate above has already established that the completion section is really on screen;
            // what is unreadable here is its rectangle, not whether it has been drawn.
            area = ResultsRegion.Resolve(false, 0f, false, 0f, Screen.height, MessageGap);
            measuredButton = false;
            measuredTop = float.NaN;
            measuredHeight = float.NaN;
            if (HeadingReports.Add("success-region-fallback"))
                Plugin.Logger?.LogInfo("[DIAG] success region: the completion section and the lobby button could not both be measured, so the board falls back to the historical fraction of the window; both elements remain above and below it regardless.");
        }
        // The success page animates its footer in too, so its one measurement is taken against a settled
        // button edge as well. A strip read mid-reveal would be permanently shorter than the page really
        // offers, and the board is measured once and then kept for the life of the page.
        // The footer is revealed from zero alpha, and a control nobody can see is deliberately not a bound - so
        // the first frames of a freshly opened page offer no edge at all. Waiting for the reveal is what buys
        // the board the height that is really there above the button; the wait is bounded, so a page whose
        // button never becomes acceptable is still laid out with the historical fraction of the window.
        else if (!measuredButton)
        {
            if (++_seedButtonMisses < ButtonSettleAttempts) return false;
        }
        else if (!ButtonSettled(measuredButton, measuredTop)) return false;
        if (!area.Fits) return false;
        top = Math.Max(0f, area.Top) + Mathf.Max(0f, Plugin.ResultsOffsetY.Value);
        room = Math.Min(area.Bottom, Screen.height - 8f) - top;
        // The measured button travels with the strip: it is the edge the board may never reach, on either page.
        // The measured button travels with the strip: it is the edge the board may never reach, on either
        // page, and its own height is the size the board's readability floor is measured against.
        strip = new BoardStrip(top, top + room, measuredButton, measuredTop, measuredHeight);
        return room > 0f;
    }

    // The footer of a result page animates in, so the button's own top edge is only trusted once it has
    // stopped moving. A page whose button could not be measured has nothing to wait for, and the wait is
    // bounded so a hover that keeps nudging the edge cannot hold the board back for ever.
    private static bool ButtonSettled(bool measured, float top)
    {
        if (!measured || !float.IsFinite(top)) return true;
        bool steady = float.IsFinite(_seedButtonTop) && Math.Abs(top - _seedButtonTop) <= 1f;
        _seedSettleFrames = steady ? _seedSettleFrames + 1 : 0;
        _seedButtonTop = top;
        _seedAttempts++;
        return _seedSettleFrames >= ButtonSettleFrames || _seedAttempts >= ButtonSettleAttempts;
    }

    private static string _completionState;

    internal static void BeginSuccessPage(CM_PageExpeditionSuccess page)
    {
        RestoreHidden();
        ResetPlacement();
        ForgetPage();
        HeadingReports.Clear();
        _completionState = null;
        Success = page;
        CompletionTargets.BeginPage();
    }

    // Blink completion is native state, not an inferred alpha or a camera culling flag.
    private static bool CompletionReady(Component page)
    {
        if (page == null) return false;
        Readiness.Bind(page.Pointer);
        if (Readiness.Ready) return true;
        var ledger = CompletionTargets.Ledger;
        var evidence = new CompletionEvidence(ledger.Artwork > 0, ledger.NamesReady,
            ledger.Artwork > 0, ledger.ArtworkReady, ledger.Artwork > 0);
        bool ready = Readiness.Observe(evidence);
        string state = $"completed={ledger.Completed}/{ledger.Count}, names={ledger.Names}, artwork={ledger.Artwork}, ready={ready}";
        if (state != _completionState)
        {
            _completionState = state;
            Plugin.Logger?.LogInfo("[DIAG] success native reveal: " + state);
        }
        if (ready) Plugin.Logger?.LogInfo("[DIAG] success board released after the game's sector BlinkIn routines completed; no timeout or alpha threshold bypass.");
        return ready;
    }

    // The success page's completion section and its lobby button, reduced to the strip between them. Both
    // are read from the page itself: the sector box, its header and the four cleared-sector icons make up
    // the completion section, and the deepest edge the page actually drew is the one the board must clear.
    private static bool SuccessRegion(Component page, out ResultsSafeArea area, out bool buttonMeasured, out float buttonTop, out float buttonHeight)
    {
        area = default;
        buttonMeasured = false;
        buttonTop = float.NaN;
        buttonHeight = float.NaN;
        var success = page != null ? page.TryCast<CM_PageExpeditionSuccess>() : null;
        if (success == null) return false;
        float completion = float.NaN, button = float.NaN;
        // The lobby button's rectangle is measured inside the guarded block and its height is needed after
        // it, so both are declared here and only filled there.
        LayoutRect lobbyRect = default;
        bool lobbyRead = false;
        float lobbyTop = float.NaN;
        try
        {
            completion = ResultsRegion.LowestBottom(Bottom(success.m_sectorsWhiteBox), completion);
            completion = ResultsRegion.LowestBottom(Bottom(success.m_sectorHeader), completion);
            completion = ResultsRegion.LowestBottom(Bottom(success.m_sectorIconMain), completion);
            completion = ResultsRegion.LowestBottom(Bottom(success.m_sectorIconSecond), completion);
            completion = ResultsRegion.LowestBottom(Bottom(success.m_sectorIconThird), completion);
            completion = ResultsRegion.LowestBottom(Bottom(success.m_sectorIconAllCompleted), completion);
            // The strip's lower bound is the button that sends the squad home, and only that button: on the
            // success page the controls the page has drawn include ones far above the footer, and clearing
            // the highest of them cost the board a band of the page it then could not use. `ButtonBand`
            // answers for every button that has actually been drawn - including the one this build does not
            // know by name - and is kept as the fallback for a page whose own lobby button cannot be read.
            // The lobby button is measured once, as a rectangle: its upper edge bounds the strip, and its own
            // height is the size of the control the player has to press - which is what the board's readability
            // floor is derived from. A page whose button cannot be read at all keeps the drawn band below.
            lobbyRect = default;
            lobbyRead = VisibleRect(success.m_btnLeaveExpedition, out lobbyRect);
            lobbyTop = lobbyRead ? lobbyRect.Y : float.NaN;
            bool lobbyMeasured = float.IsFinite(lobbyTop);
            float bandTop = ButtonBand(page, float.NaN);
            button = ResultsRegion.LobbyEdge(lobbyMeasured, lobbyTop, bandTop, Screen.height);
        }
        catch (Exception error)
        {
            if (HeadingReports.Add("success-region-error"))
                Plugin.Logger?.LogWarning("[DIAG] success region could not be measured; the board keeps the fallback: " + error.Message);
            return false;
        }
        bool completionMeasured = float.IsFinite(completion);

        // The failure page has no completion section of its own, so the line this one ends on is what the
        // failure board is aligned to later in the same window. Recorded from the same measurement the
        // success board itself uses, so the two can never disagree.
        if (completionMeasured) FailureAlignment.RememberCompletion(completion, Screen.height);
        buttonMeasured = float.IsFinite(button);
        buttonTop = buttonMeasured ? button : float.NaN;
        // The height of that same button, and only of that button: when the bound came from the drawn band
        // there is no single control to measure, and the board keeps its absolute readability floor.
        buttonHeight = buttonMeasured && lobbyRead && button == lobbyTop ? lobbyRect.Height : float.NaN;
        area = ResultsRegion.Resolve(completionMeasured, completion, buttonMeasured, buttonTop, Screen.height, MessageGap);
        if (HeadingReports.Add("success-region"))
            Plugin.Logger?.LogInfo($"[DIAG] success region: completion={(completionMeasured ? completion.ToString("0") : "unmeasurable")}, lobbyButton={(buttonMeasured ? buttonTop.ToString("0") : "unmeasurable")}, boardTop={area.Top:0}, room={area.Height:0} of a {Screen.height} window; the strip runs from the completion section to the visible button's own upper edge, not to a hidden, transparent or off-page control, and all four players scale into it rather than being trimmed to it.");
        return area.Fits;
    }

    // The screen-space top and bottom edge of one page element, or NaN when it cannot be read.
    // The deepest edge of an element, and of everything under it. The completion section's figures carry
    // their captions as children, so measuring only the object itself reported a bottom 100-odd pixels
    // above what the player can actually see - which is how the board ended up over the icons and their
    // labels. Children are measured through the same routine, so the sanity checks still apply to each.
    private static float Bottom(Component element)
    {
        if (element == null) return float.NaN;
        float bottom = TryScreenRect(element, out LayoutRect rect) ? rect.Bottom : float.NaN;
        try
        {
            var renderers = element.GetComponentsInChildren<Renderer>(true);
            if (renderers != null)
                foreach (Renderer child in renderers)
                {
                    if (child == null || !child.enabled) continue;
                    if (TryScreenRect(child, out LayoutRect nested)) bottom = ResultsRegion.LowestBottom(nested.Bottom, bottom);
                }
        }
        catch { }
        return bottom;
    }
    private static float Bottom(GameObject host) => host == null ? float.NaN : Bottom(host.transform);
    private static float Top(Component element) => TryScreenRect(element, out LayoutRect rect) ? rect.Y : float.NaN;

    // The whole screen-space rectangle of the part of a page control the player can actually see, or false
    // when the control is not on this page at all. Three things are asked of it, and all three have cost
    // the board height before: the control has to be switched on, it has to be drawn (its canvas group and
    // its own graphics are not faded out), and it has to lie on the frame the player is looking at. The
    // rectangle spans the highest and lowest renderer the control draws - the real edges of the button, not
    // the container's padded rect - so the strip can run from the heading down to the button the player
    // presses, and that button's own height says how large the control being pressed really is.
    private static bool VisibleRect(Component element, out LayoutRect rect)
    {
        rect = default;
        if (element == null) return false;
        bool active;
        try { active = element.gameObject.activeInHierarchy; } catch { return false; }
        if (!active) return false;
        float top = float.NaN, bottom = float.NaN;
        try
        {
            var renderers = element.GetComponentsInChildren<Renderer>(true);
            if (renderers != null)
                foreach (Renderer child in renderers)
                {
                    if (child == null || !child.enabled) continue;
                    if (!TryScreenRect(child, out LayoutRect nested)) continue;
                    top = float.IsFinite(top) ? Math.Min(top, nested.Y) : nested.Y;
                    bottom = float.IsFinite(bottom) ? Math.Max(bottom, nested.Bottom) : nested.Bottom;
                }
        }
        catch { }
        // A control that draws nothing measurable is read from its own rect instead.
        if (!float.IsFinite(top) && TryScreenRect(element, out LayoutRect own)) { top = own.Y; bottom = own.Bottom; }
        if (!float.IsFinite(top) || !float.IsFinite(bottom)) return false;
        if (!ResultsButtonPolicy.Visible(active, ControlAlpha(element), top, bottom, Screen.width, Screen.height))
            return false;
        float x = 0f, width = Screen.width;
        if (TryScreenRect(element, out LayoutRect bounds) && float.IsFinite(bounds.X) && float.IsFinite(bounds.Width) && bounds.Width > 0f)
        {
            x = bounds.X; width = bounds.Width;
        }
        rect = new LayoutRect(x, top, width, Math.Max(0f, bottom - top));
        return true;
    }

    // The visible top edge alone: the edge the strip's lower bound is built from, and the only thing most
    // callers need. It is the same measurement, so the two can never disagree.
    private static float VisibleTop(Component element) => VisibleRect(element, out LayoutRect rect) ? rect.Y : float.NaN;

    // How visible a control is, as the product of every canvas group above it and the strongest colour
    // alpha among its own graphics. An alpha that cannot be read is reported as 1 so a control nobody could
    // measure is protected rather than dropped.
    private static float ControlAlpha(Component element)
    {
        if (element == null) return 1f;
        float alpha = 1f;
        try
        {
            Transform node = element.transform;
            int guard = 0;
            while (node != null && guard++ < 32)
            {
                var group = node.TryCast<CanvasGroup>();
                if (group != null && float.IsFinite(group.alpha)) alpha *= Mathf.Clamp01(group.alpha);
                node = node.parent;
            }
            float drawn = float.NaN;
            foreach (Graphic graphic in element.GetComponentsInChildren<Graphic>(true))
            {
                if (graphic == null || !float.IsFinite(graphic.color.a)) continue;
                drawn = float.IsFinite(drawn) ? Math.Max(drawn, graphic.color.a) : graphic.color.a;
            }
            if (float.IsFinite(drawn)) alpha *= Mathf.Clamp01(drawn);
            return Mathf.Clamp01(alpha);
        }
        catch { return 1f; }
    }

    // The rect of a button component can include layout padding above the control a player actually sees,
    // and a footer group can own several controls. Neither is the wall the board must clear, so the raw
    // numbers behind the measured edge are reported once - a strip that stopped short of the button is
    // otherwise indistinguishable from a page that really had no room.
    private static string DescribeButton(Component element)
    {
        if (element == null) return "missing";
        var text = new System.Text.StringBuilder();
        text.Append(TryScreenRect(element, out LayoutRect own) ? $"container={own.Y:0}..{own.Bottom:0}" : "container=unreadable");
        try
        {
            var renderers = element.GetComponentsInChildren<Renderer>(true);
            if (renderers != null)
            {
                float top = float.NaN, bottom = float.NaN;
                int count = 0;
                foreach (Renderer child in renderers)
                {
                    if (child == null || !child.enabled) continue;
                    if (!TryScreenRect(child, out LayoutRect nested)) continue;
                    count++;
                    top = float.IsFinite(top) ? Math.Min(top, nested.Y) : nested.Y;
                    bottom = float.IsFinite(bottom) ? Math.Max(bottom, nested.Bottom) : nested.Bottom;
                }
                text.Append(count == 0 ? "; visuals=none" : $"; visuals={top:0}..{bottom:0} of {count}");
            }
        }
        catch (Exception error) { text.Append("; visuals failed: " + error.GetType().Name); }
        return text.ToString();
    }
    private static float Top(GameObject host) => host == null ? float.NaN : Top(host.transform);

    // Rows that carry a hairline above them: the metric block's own header and the start of each group,
    // so the board reads in sections instead of as one wall of rows.
    private static bool[] Dividers(ImpactBoardLayout board)
    {
        var dividers = new bool[board.Rows];
        for (int row = 0; row < board.Rows; row++) dividers[row] = board.Kinds[row] == ImpactRowKind.Separator;
        Mark(dividers, (int)ImpactRowKind.PrimaryWeapon);
        Mark(dividers, (int)ImpactRowKind.PrimaryDamage);
        Mark(dividers, (int)ImpactRowKind.SecondaryDamage);
        return dividers;
    }

    private static void Mark(bool[] dividers, int row)
    {
        if (row > 0 && row < dividers.Length) dividers[row] = true;
    }

    // The strip of the failure page the board may use: below the page's own message and above the higher
    // of the two footer buttons this build knows by name - "send every prisoner back to the lobby" and
    // "restart checkpoint". Both edges are measured from the page itself, and this is the same rule the
    // success screen uses (ResultsRegion.Resolve), so the board of either screen stands in the same safe
    // strip instead of each having its own idea of "below the heading". The edge that bounds it is chosen by
    // `ResultsRegion.LobbyEdge` - that button when it can be measured, the band of everything the page drew
    // when it cannot - and measurement failure degrades to a screen-space fallback that still clears that
    // band, never to permission to cover a button.
    //
    // The button's own top edge comes back with the strip: the button is the one edge a board that is
    // already up is still held to, so the draw path can keep that promise without re-measuring the whole
    // strip it was placed in.
    private static bool FailureStrip(Component page, out float top, out float bottom, out float opacity, out bool buttonMeasured, out float buttonTop, out float buttonHeight)
    {
        top = Screen.height * 0.34f;
        bottom = Screen.height - 8f;
        opacity = 1f;
        buttonMeasured = false;
        buttonTop = float.NaN;
        buttonHeight = float.NaN;
        try
        {
            var fail = page != null ? page.TryCast<CM_PageExpeditionFail>() : null;
            var heading = fail?.m_missionFailed_text;
            bool present = heading != null;
            bool active = present && heading.gameObject.activeInHierarchy;
            float alpha = present ? heading.alpha : float.NaN;
            LayoutRect rect = default;
            bool measured = active && TryScreenRect(heading, out rect);
            if (present && (!active || float.IsFinite(alpha) && alpha <= 0.001f)) return false;
            opacity = present && float.IsFinite(alpha) ? Mathf.Clamp01(alpha) : 1f;

            // The buttons the board must never cover are the two this build knows by name: "send every
            // prisoner back to the lobby", `m_btnGoToLobby`, and "restart checkpoint",
            // `m_btnRestartCheckpoint`. The higher of the two - the smaller finite top edge - is the strip's
            // lower bound, so a page that puts the restart button above the lobby button cannot be covered
            // either. A result page also carries controls that are switched on and drawn yet sit far above
            // the footer, and those are deliberately not part of this input: taking the highest of everything
            // the page drew cost the board ~180px of the page and made it draw small with a band of empty
            // screen above the button.
            //
            // The band of every button the page has drawn is only the fallback for a page whose named
            // buttons cannot be read at all: a host's fail page carries a different footer from a client's,
            // and a page re-laid out by another mod moves them, so a page whose own buttons cannot be read
            // still keeps the board above whatever it did draw. That is the whole of the rule: while a named
            // button is readable, a control the page shows but this build cannot name cannot cost the board
            // its height, and when none of them is readable the band still clears every one of them.
            // The height of the very button the strip's lower bound came from is read from the same
            // rectangle as its edge, so the board's readability floor describes the control the player
            // actually presses. A page whose bound came from the drawn band instead has no such control.
            LayoutRect goRect = default, restartRect = default;
            float restartTop = fail != null && VisibleRect(fail.m_btnRestartCheckpoint, out restartRect) ? restartRect.Y : float.NaN;
            float goTop = fail != null && VisibleRect(fail.m_btnGoToLobby, out goRect) ? goRect.Y : float.NaN;
            float lobbyTop = ResultsRegion.HighestTop(restartTop, goTop);
            bool lobbyMeasured = float.IsFinite(lobbyTop);
            float bandTop = ButtonBand(page, float.NaN);
            buttonTop = ResultsRegion.LobbyEdge(lobbyMeasured, lobbyTop, bandTop, Screen.height);
            buttonMeasured = float.IsFinite(buttonTop);
            buttonHeight = lobbyTop == restartTop ? restartRect.Height
                : lobbyTop == goTop ? goRect.Height
                : float.NaN;
            // The message is moved so its bottom edge is the line the success page's completion section
            // ended on, and the board starts the same gap under it - the very rule the success board uses.
            float aligned;
            if (!FailureAlignment.Resolve(measured, measured ? rect.Bottom : float.NaN, Screen.height, Plugin.ResultsOffsetY.Value,
                MessageGap, buttonMeasured, buttonTop, out aligned, out ResultsSafeArea strip)) return false;
            if (measured) AlignHeading(heading, rect, aligned);
            top = strip.Top;
            bottom = Math.Min(strip.Bottom, Screen.height - 8f);

            // The figures this rests on, once per session, so the alignment and the clearance can be
            // checked against a real page instead of being taken on trust.
            LayoutRect board = new LayoutRect(0f, top, Screen.width, Math.Max(0f, bottom - top));
            // The forbidden band is everything from the highest button's top edge downwards: a board that
            // reaches into it is the overlap this exists to prevent, whichever button is down there.
            LayoutRect lobby = float.IsFinite(buttonTop) && buttonTop < Screen.height
                ? new LayoutRect(0f, buttonTop, Screen.width, Screen.height - buttonTop)
                : default;
            bool overlapsLobby = lobby.Height > 1f && board.Overlaps(lobby);
            if (HeadingReports.Add("failure-strip"))
                Plugin.Logger?.LogInfo($"[DIAG] failure strip: messageBottom={(measured ? rect.Bottom.ToString("0") : "unmeasurable")}, alignedMessageBottom={aligned:0}, completionAnchor={(FailureAlignment.AnchorKnown(Screen.height) ? FailureAlignment.Anchor.ToString("0") : "unseen")}, board=({board.Y:0}..{board.Bottom:0}), lobbyButtonTop={(float.IsFinite(buttonTop) ? buttonTop.ToString("0") : "unmeasurable")}, screen={Screen.width}x{Screen.height}, lobby={DescribeButton(fail?.m_btnGoToLobby)}, restart={DescribeButton(fail?.m_btnRestartCheckpoint)}, boardOverlapsLobbyButton={overlapsLobby.ToString().ToLowerInvariant()}.");
            return true;
        }
        catch (Exception error)
        {
            if (HeadingReports.Add("title-read-error")) Plugin.Logger?.LogWarning("[DIAG] failure strip read failed; using visible screen-space fallback: " + error.Message);
            buttonMeasured = false;
            buttonTop = float.NaN;
            buttonHeight = float.NaN;
            return true;
        }
    }

    // The failure page's own message alpha: the figure the board has always been drawn with, so the two
    // arrive together. Read on its own so a board that is already up never has to re-measure the strip
    // around it.
    private static float HeadingOpacity(Component page)
    {
        try
        {
            var fail = page != null ? page.TryCast<CM_PageExpeditionFail>() : null;
            TextMeshPro heading = fail != null ? fail.m_missionFailed_text : null;
            return heading != null && float.IsFinite(heading.alpha) ? Mathf.Clamp01(heading.alpha) : 1f;
        }
        catch { return 1f; }
    }

    // The lobby button's own top edge, on its own: the one edge that still moves a board already in place.
    //
    // It is read by the same rule the strip is measured with - the higher of the two named buttons
    // ("send every prisoner back to the lobby" and "restart checkpoint") when either can be measured, the
    // band of everything the page drew when neither can - so the hold that keeps an already placed board
    // off the footer can never pull it up against a control the strip itself was never asked to clear.
    private static bool LobbyButtonTop(Component page, out float top)
    {
        top = float.NaN;
        try
        {
            var fail = page != null ? page.TryCast<CM_PageExpeditionFail>() : null;
            float lobbyTop = fail != null
                ? ResultsRegion.HighestTop(VisibleTop(fail.m_btnRestartCheckpoint), VisibleTop(fail.m_btnGoToLobby))
                : float.NaN;
            bool lobbyMeasured = float.IsFinite(lobbyTop);
            float bandTop = ButtonBand(page, float.NaN);
            top = ResultsRegion.LobbyEdge(lobbyMeasured, lobbyTop, bandTop, Screen.height);
        }
        catch { return false; }
        return float.IsFinite(top);
    }

    // Every button the page has actually drawn. The fields this build knows by name are the start of the
    // set, not the whole of it: on the fail page the host's own "send every prisoner back to the lobby" is
    // a button a client never sees, a page re-laid out by another mod moves them, and a page whose button
    // this build cannot read by name is the page where the board was drawn over it. The highest top edge
    // among them all is the edge the board has to stay above, so asking the page what it drew is the only
    // version of this rule that survives a page this build has never seen.
    private static float ButtonBand(Component page, float best)
    {
        try
        {
            if (page == null) return best;
            foreach (Button button in page.GetComponentsInChildren<Button>(true))
            {
                if (button == null) continue;
                var element = button.TryCast<Component>();
                if (element == null) continue;
                // Only a button the player can really see bounds the board: a control this page keeps in its
                // hierarchy switched off, faded to nothing or laid out past the frame is not one of them, and
                // the edge taken is the button's own visible upper edge rather than its padded container.
                best = ResultsRegion.HighestTop(VisibleTop(element), best);
            }
        }
        catch (Exception error)
        {
            if (HeadingReports.Add("button-band-error"))
                Plugin.Logger?.LogWarning("[DIAG] the result page's buttons could not all be read; the board keeps to the ones that could: " + error.Message);
        }
        return best;
    }

    // The failure page's own message, moved so that its bottom edge is the line the success page's
    // completion section ended on. Only ever moved while its rectangle could be measured, only by the
    // difference between that measurement and the line, and never by so much that the message leaves the
    // page: a message that would be pushed off the top or down onto the buttons is left alone instead.
    private static void AlignHeading(Component heading, LayoutRect rect, float alignedBottom)
    {
        if (heading == null || !float.IsFinite(alignedBottom) || rect.Height <= 0f) return;
        try
        {
            float delta = alignedBottom - rect.Bottom;
            if (!float.IsFinite(delta) || Math.Abs(delta) < 0.5f) return;
            float movedTop = rect.Y + delta;
            if (movedTop < 0f || movedTop + rect.Height > Screen.height - 8f) return;
            Transform transform = heading.transform;
            if (transform == null) return;
            if (transform is RectTransform)
            {
                // A screen-space panel: the shift is taken directly on its own vertical position.
                Vector3 local = transform.localPosition;
                transform.localPosition = new Vector3(local.x, local.y + delta, local.z);
                return;
            }
            // A world-space TextMeshPro: the shift is turned from screen pixels into world units on the
            // menu camera's own scale, so the same number of pixels moves the message the same distance at
            // any resolution, and the move is skipped entirely when that camera cannot be read.
            Camera camera = CM_PageBase.Camera;
            if (camera == null) return;
            float screenHeight = Mathf.Max(1f, Screen.height);
            float worldPerPixel = camera.orthographic
                ? 2f * camera.orthographicSize / screenHeight
                : 2f * Math.Abs(transform.position.z - camera.transform.position.z) * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) / screenHeight;
            if (!float.IsFinite(worldPerPixel) || worldPerPixel <= 0f) return;
            Vector3 position = transform.position;
            transform.position = new Vector3(position.x, position.y + delta * worldPerPixel, position.z);
        }
        catch (Exception error)
        {
            if (HeadingReports.Add("heading-align-error")) Plugin.Logger?.LogWarning("[DIAG] the failure message could not be moved onto the success line and stays where the game put it: " + error.Message);
        }
    }
    // A Unity UI element's screen rectangle. An overlay canvas is measured with no camera at all,
    // which is what RectTransformUtility expects, and a canvas drawn by a camera is measured through
    // that camera, so either way the figure is the rectangle the player actually sees. A measurement
    // that does not come back sane is discarded rather than trusted.
    private static bool TryScreenRect(Component component, out LayoutRect rect)
    {
        rect = default;
        if (component == null) return false;
        try
        {
            var element = component.transform.TryCast<RectTransform>();
            if (element == null) return false;
            Camera camera = CM_PageBase.Camera;
            var canvas = component.GetComponentInParent<Canvas>();
            if (canvas != null)
            {
                if (canvas.renderMode == RenderMode.ScreenSpaceOverlay) camera = null;
                else camera = canvas.worldCamera != null ? canvas.worldCamera : camera;
            }
            if (camera == null && (canvas == null || canvas.renderMode != RenderMode.ScreenSpaceOverlay))
            {
                // The page's own camera is not always assigned on the frame the board attaches - the menu
                // camera comes up with the page - and the completion section's icons are world-space objects
                // with no canvas above them, so this used to be read as "unmeasurable" and the board fell back
                // to a fraction of the window, straight over them. A missing camera is looked for instead.
                camera = Camera.main;
                if (camera == null)
                {
                    var cameras = Camera.allCameras;
                    if (cameras != null)
                        foreach (Camera candidate in cameras)
                        {
                            if (candidate == null || !candidate.isActiveAndEnabled) continue;
                            camera = candidate; break;
                        }
                }
                if (camera == null) return false;
            }
            Rect local = element.rect;
            var renderer = component.GetComponent<Renderer>();
            bool mesh = renderer != null && camera != null && renderer.bounds.size.sqrMagnitude > 0.0001f;
            Bounds bounds = mesh ? renderer.bounds : default;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int corner = 0; corner < (mesh ? 8 : 4); corner++)
            {
                Vector3 world;
                if (mesh)
                    world = bounds.center + Vector3.Scale(bounds.extents, new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1));
                else
                    world = element.TransformPoint(new Vector3((corner & 1) == 0 ? local.xMin : local.xMax, (corner & 2) == 0 ? local.yMin : local.yMax, 0f));
                if (camera != null && camera.WorldToScreenPoint(world).z <= 0f) return false;
                var screen = RectTransformUtility.WorldToScreenPoint(camera, world);
                if (!float.IsFinite(screen.x) || !float.IsFinite(screen.y)) return false;
                float y = Screen.height - screen.y;
                x0 = Mathf.Min(x0, screen.x); x1 = Mathf.Max(x1, screen.x);
                y0 = Mathf.Min(y0, y); y1 = Mathf.Max(y1, y);
            }
            float width = x1 - x0, height = y1 - y0;
            if (!(width > 1f) || !(height > 1f)) return false;
            if (x1 <= 0f || x0 >= Screen.width || y1 <= 0f || y0 >= Screen.height) return false;
            if (width >= Screen.width && height >= Screen.height) return false;
            rect = new LayoutRect(x0, y0, width, height);
            return true;
        }
        catch (Exception error)
        {
            if (!_loggedMeasure)
            {
                _loggedMeasure = true;
                Plugin.Logger?.LogWarning("[DIAG] failure heading measurement failed; using screen-space fallback: " + error.Message);
            }
            return false;
        }
    }

    // ---- the vanilla blocks the board replaces ---------------------------------------------------

    // The blocks the board stands in for, put away again on every frame the page is up.
    //
    // Switching a block off is not enough on its own: the result pages animate their content in over
    // several seconds and switch it back on as they go, which is why a block that is only deactivated
    // blinks back in. The blocks are therefore moved right out of the page, which survives whatever the
    // page does to them, and deactivated too in case a layout group is what drives their position. One
    // block at a time, each in its own guard, and only when the config asks for it: a field that is null
    // on this game build is skipped and the board simply draws over it.
    //
    // The artifact blocks and the four prisoner report cards are two separate switches, and both are off
    // by default (建议关闭显示以免影响观感), so this mod's board owns the page. Whenever a switch is
    // turned back on, the page changes or the results screen is left, every block this mod moved is put
    // back at the exact position and in the exact active state it had before - which is what the previous
    // release could not do, because it only ever moved them off the page.
    private static void ApplyVanillaBlocks(Component page, bool succeeded)
    {
        if (page == null) return;
        // Both switches are positive and off by default: the block is put away unless the player asked for
        // it back.
        bool successBlocks = succeeded && Plugin.HideVanillaArtifacts;
        bool prisonerReports = succeeded && Plugin.HideVanillaReports;
        bool failBlocks = !succeeded && Plugin.HideVanillaFailBlocks != null && Plugin.HideVanillaFailBlocks.Value;
        // A switch that changed is a set of blocks that has to be put back before the new set is applied.
        if (_appliedAny && (successBlocks != _appliedSuccessBlocks || prisonerReports != _appliedPrisonerReports || failBlocks != _appliedFailBlocks))
            RestoreHidden();
        _appliedSuccessBlocks = successBlocks; _appliedPrisonerReports = prisonerReports; _appliedFailBlocks = failBlocks;
        _appliedAny = successBlocks || prisonerReports || failBlocks;
        if (!_appliedAny) return;

        if (succeeded)
        {
            var success = page.TryCast<CM_PageExpeditionSuccess>();
            if (success == null) return;
            if (successBlocks)
            {
                Hide(success.m_ArtifactInventoryDisplay, "artifact inventory");
                Hide(success.m_artifactInfo_text, "artifact summary");
                Hide(success.m_checkpointUsage, "checkpoint usage");
            }
            if (prisonerReports)
            {
                var reports = success.m_playerReports;
                int count = reports == null ? 0 : reports.Length;
                for (int index = 0; index < count; index++) Hide(reports[index], "prisoner report " + index);
                var anchors = success.m_playerReportAligns;
                int anchors_ = anchors == null ? 0 : anchors.Length;
                for (int index = 0; index < anchors_; index++) Hide(anchors[index], "prisoner report anchor " + index);
            }
        }
        else
        {
            var fail = page.TryCast<CM_PageExpeditionFail>();
            if (fail == null) return;
            Hide(fail.m_ArtifactInventoryDisplay, "artifact inventory");
            Hide(fail.m_artifactInfo_text, "artifact summary");
        }
    }

    // How far off the page a hidden block is put: past any resolution, still a valid position.
    private const float AwayY = 6000f;

    // Everything this mod moved goes back, exactly. An entry is written the first time a block is touched
    // and never rewritten, so the recorded position is the page's own and not this mod's own displacement.
    private static void RestoreHidden()
    {
        foreach (KeyValuePair<IntPtr, HiddenBlock> entry in Hidden)
        {
            HiddenBlock block = entry.Value;
            try
            {
                if (block.Target != null) block.Target.localPosition = block.Position;
                if (block.Host != null) block.Host.SetActive(block.Active);
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("[DIAG] a vanilla block could not be put back where it was: " + error.Message);
            }
        }
        Hidden.Clear();
        _appliedSuccessBlocks = _appliedPrisonerReports = _appliedFailBlocks = _appliedAny = false;
    }

    private static void Hide(Component block, string what)
    {
        if (block == null) return;
        try
        {
            var target = block.transform;
            var host = block.gameObject;
            IntPtr key = block.Pointer;
            if (!Hidden.TryGetValue(key, out HiddenBlock record))
            {
                // First touch: this is the page's own position and state, recorded before anything moves.
                record = new HiddenBlock
                {
                    Target = target, Host = host,
                    Position = target != null ? target.localPosition : default,
                    Active = host != null && host.activeSelf
                };
                Hidden[key] = record;
            }
            if (target != null) target.localPosition = new Vector3(record.Position.x, AwayY, record.Position.z);
            if (host != null && host.activeSelf) host.SetActive(false);
            if (!_loggedHidden)
            {
                _loggedHidden = true;
                Plugin.Logger.LogInfo("[DIAG] the vanilla " + what + " block was moved off the page and switched off so the board has the room; its exact original position and active state are remembered and put back the moment hiding is switched off, the page changes or the results screen is left. The mod never touches the text inside it.");
            }
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("[DIAG] the vanilla " + what + " block could not be hidden: " + error.Message);
        }
    }
}

[HarmonyPatch(typeof(CM_PageExpeditionSuccess), "OnEnable")]
internal static class SuccessResultsPatch
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(CM_PageExpeditionSuccess __instance) => ResultsDisplay.BeginSuccessPage(__instance);

    [HarmonyPostfix]
    private static void Postfix(CM_PageExpeditionSuccess __instance) => ResultsDisplay.Success = __instance;
}

[HarmonyPatch(typeof(CM_PageExpeditionFail), "OnEnable")]
internal static class FailureResultsPatch
{
    [HarmonyPostfix]
    private static void Postfix(CM_PageExpeditionFail __instance) => ResultsDisplay.Fail = __instance;
}
