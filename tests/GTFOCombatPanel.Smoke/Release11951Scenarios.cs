using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HawChow.GTFOCombatPanel;

// ---------------------------------------------------------------------------------------------------
// Release11951Scenarios.cs - the recorded 1080p success page that showed eight of its thirty rows
// ---------------------------------------------------------------------------------------------------
//
// The recording (`Survival_LogOutput.log` L4174/L4811/L4814/L4812/L4837, plus the 1K/2K failure pages of
// the same run) says, in canvas units on a 1920x1080 reference:
//
//   completion section (icon row, captions included)   233   -> band top 249 (plus the 16 px clearance)
//   the page's own leave/return button                  y 973, x 752..1192 (the rect the toggle logged)
//   the waiting-status line under it                    y 982
//   the only interaction in between                     y 455, x 217..720  (the left-hand booster notice)
//   what the board really got                           249..439 = 190 px
//   what the page really offered                        249..966 = 717 px
//   the grid                                            330x540, 30 rows, 12 px glyph, 18 px row pitch
//   the plate that clipped it                           330x186 (= 144 px of rows + the 42 px header) -> 8 rows
//
// The cause is one arithmetic step and not a rendering bug: `ResultInteractionStripPolicy.TryResolve` took the
// *topmost* measured interaction below the completion section as the strip's lower wall without ever asking
// whether the board could be painted over it. The left-hand notice cannot be, and its edge still ended the band.
//
// Everything below runs on the production policies that decide it (`ResultInteractionStripPolicy`,
// `LayoutColumn`, `ResultsBounds`, `ResultsFontGrowth`, `ResultsBandFit`, `ResultsScroll`, `ResultsPlacement`).
// The 1.19.50 arithmetic is reproduced by handing the same function `LayoutColumn.None`, which is the
// fail-closed default and answers "every rectangle reaches" - i.e. the old code path, byte for byte, inside the
// same build. The first scenario therefore asserts the *old* answer (8 of 30) and the *new* one (30 of 30) from
// the same page data: it goes red on the old rule and green only when the column is honoured.
internal static class Release11951Scenarios
{
    // ---- the recorded page, in the canvas units every success-page line is measured in ----------------
    private const float ScreenWidth = 1920f;
    private const float ScreenHeight = 1080f;
    private const float Completion = 233f;          // band top 249 = 233 + ResultsBounds.Clearance(1080)
    private const float Gap = 16f;                  // ResultsDisplay.MessageGap
    private const float Header = 42f;               // the settlement header band the plate reserves
    private const int Rows = 30;                    // one player's metric block, not thirty players
    private const float Glyph = 12f;                // the automatic mode's target at 1080
    // The left-hand booster / artifact notice: the one interaction between the icons and the button. Its upper
    // edge is the 455 the recorded band ended above (439 + the same 16 px clearance).
    private static readonly LayoutRect Notice = new LayoutRect(217f, 455f, 503f, 60f);
    // The page's own leave/return button, as the toggle's own overlap line recorded it.
    private static readonly LayoutRect LeaveButton = new LayoutRect(752f, 973f, 440f, 80f);
    private const float WaitingText = 982f;
    // The column the board really stands in: centred, 330 px wide.
    private const float BoardWidth = 330f;
    private const float BoardX = (ScreenWidth - BoardWidth) * 0.5f;   // 795

    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        TheRecordedPageShowsEightOfThirtyRowsUnderTheOldRuleAndThirtyUnderTheNewOne(check);
        TheSamePageAt1440AlsoShowsEveryRow(check);
        AControlThatReallySpansTheColumnStillEndsTheBand(check);
        ASmallWindowStillShowsEveryRow(check);
        AWiderBoardWidensTheColumnSoNothingIsCovered(check);
        AnUnreadableColumnAnswersExactlyTheOldBand(check);
        TheFailureAndFixedPagesAreUntouchedAndTheLineStatesTheRowsItReallyShows(check);
        TheVersionCarrierIsThisFix(check);
    }

    // ---------- 1. the recorded page: 8/30 before, 30/30 after ----------

    private static void TheRecordedPageShowsEightOfThirtyRowsUnderTheOldRuleAndThirtyUnderTheNewOne(Action<bool, string> check)
    {
        var controls = new List<ResultActionBounds>
        {
            new ResultActionBounds(Notice),
            new ResultActionBounds(LeaveButton)
        };
        float rowHeight = ResultsBandFit.RowHeight(Glyph, PanelDensity.Compact);
        check(Near(rowHeight, 18f),
            "the recorded 12 px glyph is an 18 px row on the compact density, which is the pitch the log's 540/30 implies: "
                + rowHeight.ToString("0.##"));

        // ---- 1.19.50: no column, so every rectangle is a wall --------------------------------------------
        check(ResultInteractionStripPolicy.TryResolve(Completion, Gap, controls, out ResultInteractionStrip before),
            "the recorded scan resolves a strip");
        check(Near(before.FooterTop, Notice.Y),
            "under the old rule the strip's lower wall is the *left-hand notice* (y=" + before.FooterTop.ToString("0")
                + "), not the page's leave button at " + LeaveButton.Y.ToString("0"));
        check(before.BeyondColumn == 0,
            "and nothing is set aside, because the rule that sets anything aside did not exist yet");
        Check(ResultsBounds.TryResolve(ScreenHeight, Completion, ResultBoundSource.IconRow,
            before.FooterTop, WaitingText, ResultBoundSource.FooterTextControl, before.FooterTop,
            out ResultsBand oldBand), check, "the recorded page resolves a band");
        check(Near(oldBand.Top, 249f) && Near(oldBand.Bottom, 439f) && Near(oldBand.Height, 190f),
            "which is the recorded band 249..439 = 190 px (log L4814), while the page offered 249..966 = 717 px");
        Plate(oldBand.Height, Header, Rows, rowHeight, out int oldVisible, out float oldPlate);
        check(oldVisible == 8 && Near(oldPlate, 144f) && Near(oldPlate + Header, 186f),
            "and the plate that clips it is " + oldPlate.ToString("0") + " px of rows + the "
                + Header.ToString("0") + " px header = " + (oldPlate + Header).ToString("0")
                + " px, i.e. " + oldVisible + " of " + Rows + " rows - the recorded 330x186 plate and the eight rows"
                + " a player photographed");
        check(!ResultBoardHold.TryPlace(ScreenWidth, ScreenHeight,
                new BoardStrip(oldBand.Top, oldBand.Bottom, true, LeaveButton.Y, LeaveButton.Height),
                BoardWidth, Rows * rowHeight + Header, 0f, out _, 2f, bottomAligned: true),
            "the 540+42 px board never fitted that band at all: the plate was clamped and the table kept its rows behind it");

        // ---- 1.19.51: the column the board really stands in ----------------------------------------------
        var column = new LayoutColumn(LeaveButton.X, LeaveButton.Right);
        check(ResultInteractionStripPolicy.TryResolve(Completion, Gap, controls, out ResultInteractionStrip after, column),
            "the same scan resolves a strip with the board's column");
        check(after.BeyondColumn == 1 && Near(after.FooterTop, LeaveButton.Y),
            "the notice stands beside the board's column and no longer ends the band, so the wall is the page's own "
                + "leave button at " + after.FooterTop.ToString("0") + " (besideColumn=" + after.BeyondColumn + ")");
        check(Near(after.BoardTop, before.BoardTop),
            "the strip's upper edge is untouched: the completion section is still the inviolable bound ("
                + after.BoardTop.ToString("0") + ")");
        Check(ResultsBounds.TryResolve(ScreenHeight, Completion, ResultBoundSource.IconRow,
            after.FooterTop, WaitingText, ResultBoundSource.FooterTextControl, after.FooterTop,
            out ResultsBand band), check, "the page resolves a band against the real wall");
        check(Near(band.Top, 249f) && Near(band.Bottom, 957f) && Near(band.Height, 708f),
            "and it is 249..957 = " + band.Height.ToString("0") + " px - the page's own waiting line is 966, the "
                + "button's clearance line is 957, so 957 is the wall that really holds");
        Plate(band.Height, Header, Rows, rowHeight, out int visible, out float plate);
        check(visible == Rows && Near(plate, Rows * rowHeight),
            "the plate now holds all " + visible + " of " + Rows + " rows: " + plate.ToString("0")
                + " px of rows plus the header = " + (plate + Header).ToString("0") + " px");
        Check(ResultBoardHold.TryPlace(ScreenWidth, ScreenHeight,
            new BoardStrip(band.Top, band.Bottom, true, LeaveButton.Y, LeaveButton.Height),
            BoardWidth, plate + Header, 0f, out LayoutRect placed, 2f, bottomAligned: true),
            check, "and the whole board fits it, bottom-aligned as the runtime places it");
        check(Near(placed.X, BoardX) && Near(placed.Right, BoardX + BoardWidth)
              && Near(placed.Bottom, band.Bottom + 2f) && Near(placed.Y, band.Bottom + 2f - (plate + Header)),
            "the board it places is (" + placed.X.ToString("0") + "," + placed.Y.ToString("0") + ","
                + placed.Width.ToString("0") + "x" + placed.Height.ToString("0") + "), bottom "
                + placed.Bottom.ToString("0"));

        // ---- the separation that makes the rule legal ---------------------------------------------------
        check(!column.Reaches(Notice) && Notice.Right < placed.X - 0.5f,
            "the notice's right edge (" + Notice.Right.ToString("0") + ") is left of the board's own left edge ("
                + placed.X.ToString("0") + "), so the table can never be painted over it - which is the whole "
                + "argument for not ending the band on it");
        check(placed.Bottom <= LeaveButton.Y - ResultBoardHold.ButtonClearance + 0.5f,
            "and the board's foot (" + placed.Bottom.ToString("0") + ") keeps the " + ResultBoardHold.ButtonClearance
                + " px clearance above the button the player has to press (y=" + LeaveButton.Y.ToString("0") + ")");
        check(column.Reaches(LeaveButton),
            "the button itself does reach the column, so it is still a wall: the rule only subtracts walls the "
                + "board provably cannot cover");
    }

    // ---------- 2. the same page at 1440 ----------

    private static void TheSamePageAt1440AlsoShowsEveryRow(Action<bool, string> check)
    {
        // The same session ran at 2560x1440 with the UI measured on the 1920x1080 canvas, so the page is the same
        // page at 4/3: 12 px glyph -> 16 px, 18 px rows -> 24 px, 42 px header -> 56 px.
        const float scale = 4f / 3f;
        float rowHeight = ResultsBandFit.RowHeight(ResultAutoBoardContract.TargetGlyphPixels(1440f), PanelDensity.Compact);
        check(ResultAutoBoardContract.TargetGlyphPixels(1440f) == 16 && Near(rowHeight, 24f),
            "a 1440-high window aims at a 16 px glyph, i.e. a 24 px row: " + rowHeight.ToString("0.##"));

        var controls = new List<ResultActionBounds>
        {
            new ResultActionBounds(new LayoutRect(Notice.X * scale, Notice.Y * scale, Notice.Width * scale, Notice.Height * scale)),
            new ResultActionBounds(new LayoutRect(LeaveButton.X * scale, LeaveButton.Y * scale,
                LeaveButton.Width * scale, LeaveButton.Height * scale))
        };
        var column = new LayoutColumn(LeaveButton.X * scale, LeaveButton.Right * scale);
        check(ResultInteractionStripPolicy.TryResolve(Completion * scale, Gap * scale, controls,
                out ResultInteractionStrip strip, column) && strip.BeyondColumn == 1,
            "the left-hand notice is set aside at 1440 too (besideColumn=" + strip.BeyondColumn + ")");
        Check(ResultsBounds.TryResolve(1440f, Completion * scale, ResultBoundSource.IconRow, strip.FooterTop,
            WaitingText * scale, ResultBoundSource.FooterTextControl, strip.FooterTop, out ResultsBand band),
            check, "the 1440 page resolves a band");
        Plate(band.Height, Header * scale, Rows, rowHeight, out int visible, out float plate);
        check(visible == Rows && Near(plate, Rows * rowHeight),
            "and it holds every row: " + visible + " of " + Rows + ", " + plate.ToString("0") + " px of rows + "
                + (Header * scale).ToString("0") + " px header in a " + band.Height.ToString("0") + " px band");
        check(ResultBoardHold.TryPlace(2560f, 1440f,
                new BoardStrip(band.Top, band.Bottom, true, LeaveButton.Y * scale, LeaveButton.Height * scale),
                BoardWidth * scale, plate + Header * scale, 0f, out LayoutRect placed, 2f, bottomAligned: true)
              && placed.Bottom <= LeaveButton.Y * scale - ResultBoardHold.ButtonClearance + 0.5f,
            "the board it places clears the button at 1440 as well: bottom " + placed.Bottom.ToString("0")
                + " against " + (LeaveButton.Y * scale).ToString("0"));
    }

    // ---------- 3. a control that really is in the board's way ----------

    private static void AControlThatReallySpansTheColumnStillEndsTheBand(Action<bool, string> check)
    {
        // The same notice, moved under the board's own column (or a page whose middle control really is
        // full-width): it must keep ending the band, and the wheel must still reach the last row.
        var middle = new LayoutRect(700f, 455f, 500f, 60f);
        var column = new LayoutColumn(LeaveButton.X, LeaveButton.Right);
        check(column.Reaches(middle), "a control under the board's column is still a wall");
        var controls = new List<ResultActionBounds> { new ResultActionBounds(middle), new ResultActionBounds(LeaveButton) };
        check(ResultInteractionStripPolicy.TryResolve(Completion, Gap, controls, out ResultInteractionStrip strip, column)
              && strip.BeyondColumn == 0 && Near(strip.FooterTop, middle.Y),
            "so the band ends above it (y=" + strip.FooterTop.ToString("0") + ", besideColumn=0)");
        float rowHeight = ResultsBandFit.RowHeight(Glyph, PanelDensity.Compact);
        Check(ResultsBounds.TryResolve(ScreenHeight, Completion, ResultBoundSource.IconRow, strip.FooterTop,
            WaitingText, ResultBoundSource.FooterTextControl, strip.FooterTop, out ResultsBand band),
            check, "and the page resolves that shorter band");
        Plate(band.Height, Header, Rows, rowHeight, out int visible, out _);
        check(visible < Rows,
            "the plate then holds " + visible + " of " + Rows + " rows - too few for the table, which is the case "
                + "the wheel exists for");

        // The wheel reaches the last row, which is the legal degradation rather than a cropped table.
        ScrollViewport view = ResultsScroll.Viewport(Rows, ResultsScroll.FrozenRows, band.Height - Header, rowHeight,
            ResultsScroll.FrozenRows);
        int first = view.First;
        for (int notch = 0; notch < 200 && !view.AtEnd; notch++)
        {
            first = ResultsScroll.Next(first, Rows, ResultsScroll.FrozenRows, view.ScrollSlots, 1f);
            view = view.At(first);
        }
        check(view.AtEnd && view.RowAt(view.Visible - 1) == Rows - 1,
            "and a bounded number of wheel notches really reaches the table's last row (" + (Rows - 1) + "): the plate "
                + "shows rows " + view.First + ".." + (view.First + view.Visible - 1));

        // A late conflict is the per-repaint collision set, not the band: the same control is seen by `OverlapsBoard`
        // against the board's own rectangle, so the frame is deferred instead of the control being covered.
        var board = new LayoutRect(BoardX, band.Top, BoardWidth, Rows * rowHeight + Header);
        check(ResultInteractionStripPolicy.OverlapsBoard(middle, board, ResultBoardHold.ButtonClearance),
            "and that control is still rejected by the per-repaint overlap check, so a board that would reach it is "
                + "deferred (fail-closed) rather than painted over it");
        check(!ResultInteractionStripPolicy.OverlapsBoard(Notice, board, ResultBoardHold.ButtonClearance),
            "while the left-hand notice, which really is beside the board, is not an overlap: that is the difference "
                + "the band's column reads and `OverlapsBoard` already read");
    }

    // ---------- 4. a small window ----------

    private static void ASmallWindowStillShowsEveryRow(Action<bool, string> check)
    {
        // 1280x720 is the same page at 2/3; the automatic target there is 8 px with 12 px rows.
        const float scale = 2f / 3f;
        float rowHeight = ResultsBandFit.RowHeight(ResultAutoBoardContract.TargetGlyphPixels(720f), PanelDensity.Compact);
        check(ResultAutoBoardContract.TargetGlyphPixels(720f) == 8 && Near(rowHeight, 12f),
            "a 720-high window aims at an 8 px glyph and a 12 px row: " + rowHeight.ToString("0.##"));
        var controls = new List<ResultActionBounds>
        {
            new ResultActionBounds(new LayoutRect(Notice.X * scale, Notice.Y * scale, Notice.Width * scale, Notice.Height * scale)),
            new ResultActionBounds(new LayoutRect(LeaveButton.X * scale, LeaveButton.Y * scale,
                LeaveButton.Width * scale, LeaveButton.Height * scale))
        };
        var column = new LayoutColumn(LeaveButton.X * scale, LeaveButton.Right * scale);
        Check(ResultInteractionStripPolicy.TryResolve(Completion * scale, Gap * scale, controls,
            out ResultInteractionStrip strip, column), check, "the small window resolves a strip");
        Check(ResultsBounds.TryResolve(720f, Completion * scale, ResultBoundSource.IconRow, strip.FooterTop,
            WaitingText * scale, ResultBoundSource.FooterTextControl, strip.FooterTop, out ResultsBand band),
            check, "and a band");
        Plate(band.Height, Header * scale, Rows, rowHeight, out int visible, out float plate);
        check(visible == Rows && Near(plate, Rows * rowHeight),
            "the small window still shows every row: " + visible + " of " + Rows + ", " + plate.ToString("0")
                + " px of rows + " + (Header * scale).ToString("0") + " px header in " + band.Height.ToString("0")
                + " px - the board shrinks with the window instead of being clipped by it");
    }

    // ---------- 5. a wider board ----------

    private static void AWiderBoardWidensTheColumnSoNothingIsCovered(Action<bool, string> check)
    {
        // A four-player table is both taller and wider. Its own column is the board's, and the union with the page's
        // centred footer control only ever *adds* walls - so a page whose notice is beside the narrow board but under
        // the wide one is answered with the wall again, never with a covered control.
        var narrow = new LayoutColumn(LeaveButton.X, LeaveButton.Right);
        var wide = Wide(LeaveButton, width: 640f);
        check(!narrow.Reaches(Notice) && wide.Reaches(Notice),
            "the same notice is beside the 330 px board and under the 640 px one: narrow="
                + narrow.Reaches(Notice) + ", wide=" + wide.Reaches(Notice));
        var controls = new List<ResultActionBounds> { new ResultActionBounds(Notice), new ResultActionBounds(LeaveButton) };
        check(ResultInteractionStripPolicy.TryResolve(Completion, Gap, controls, out ResultInteractionStrip strip, wide)
              && Near(strip.FooterTop, Notice.Y) && strip.BeyondColumn == 0,
            "so a wider table keeps the band above the notice instead of being drawn across it (y="
                + strip.FooterTop.ToString("0") + ")");
        // And the union really is a union: the wide column contains the narrow one.
        check(wide.Left <= narrow.Left && wide.Right >= narrow.Right,
            "the column only ever grows (" + wide.Left.ToString("0") + ".." + wide.Right.ToString("0") + " contains "
                + narrow.Left.ToString("0") + ".." + narrow.Right.ToString("0") + ")");
    }

    // ---------- 6. the fail-closed default ----------

    private static void AnUnreadableColumnAnswersExactlyTheOldBand(Action<bool, string> check)
    {
        check(!LayoutColumn.None.Known && LayoutColumn.None.Reaches(Notice) && LayoutColumn.None.Reaches(LeaveButton),
            "an unknown column fills every rectangle, i.e. it answers the band this build answered before 1.19.51");
        check(!new LayoutColumn(float.NaN, 100f).Known && !new LayoutColumn(100f, float.NaN).Known
              && !new LayoutColumn(100f, 100f).Known,
            "and so does a column nobody could read: one unmeasurable edge or an empty extent is not a column");
        var controls = new List<ResultActionBounds> { new ResultActionBounds(Notice), new ResultActionBounds(LeaveButton) };
        check(ResultInteractionStripPolicy.TryResolve(Completion, Gap, controls, out ResultInteractionStrip unknown,
                LayoutColumn.None)
              && Near(unknown.FooterTop, Notice.Y) && unknown.BeyondColumn == 0,
            "the recorded scan with no column is byte for byte the 1.19.50 answer (y=" + unknown.FooterTop.ToString("0")
                + "), which is what the first scenario asserts is the broken one");

        // A page whose scan cannot read one rectangle is still refused outright - the column must not soften that.
        var broken = new List<ResultActionBounds> { new ResultActionBounds(new LayoutRect(217f, 455f, 0f, 0f)) };
        check(!ResultInteractionStripPolicy.TryResolve(Completion, Gap, broken, out ResultInteractionStrip refused,
                new LayoutColumn(752f, 1192f)),
            "a zero-box control still refuses the strip rather than being treated as 'beside the column'");

        // And the column never touches the strip's upper edge: the completion section is still the bound.
        check(Near(unknown.BoardTop, Completion + Gap),
            "the completion section stays the strip's upper edge with or without a column ("
                + unknown.BoardTop.ToString("0") + ")");
    }

    // ---------- 7. the pages that must not move, and the line that has to say what it drew ----------

    private static void TheFailureAndFixedPagesAreUntouchedAndTheLineStatesTheRowsItReallyShows(Action<bool, string> check)
    {
        string display = ProductionText("ResultsDisplay.cs");
        string layout = ProductionText("LayoutPolicy.cs");
        check(layout.Contains("out ResultInteractionStrip strip, LayoutColumn column = default)"),
            "the column is an optional parameter of the production policy, so every existing caller compiles to the "
                + "old rule");
        check(layout.Contains("if (!column.Reaches(rect)) continue;"),
            "and the one place it acts is the two wall decisions of the strip");
        check(display.Contains("boardColumn = BoardColumn();")
              && display.Contains("controls = ScanFooterControls(page, completion, MessageGap, null, board, inspectBoard, boardColumn);"),
            "the success region is the only place that reads a column, from the page's own leave button");
        string successRegion = MethodBody(display, "private static bool SuccessRegion(");
        check(Tally(successRegion, "ScanFooterControls(") == 1,
            "and it passes it once, to the one scan it owns");
        check(!MethodBody(display, "private static bool FailureStrip(").Contains("boardColumn")
              && Tally(display, "ScanFooterControls(") == 6,
            "while the failure page's strip, the failure wall reader, the fixed-mode scan and the fail-open guess keep "
                + "the default: six occurrences of the scan (the declaration and five call sites), "
                + Tally(display, "ScanFooterControls(") + " counted");
        check(Tally(display, "boardColumn)") == 1,
            "and exactly one call site passes a column - the success region's - so no other page's arithmetic moved "
                + "(" + Tally(display, "boardColumn)") + ")");
        check(display.Contains("rows shown {Math.Min(scrollVisible, grid.Rows):0} of {grid.Rows} in a {plateHeight:0}px plate"),
            "and the placed board's line states the rows the plate really holds and the plate's own height, so a "
                + "`284% of the strip` figure can no longer read as `thirty rows are on the page`");
        check(display.Contains("boardColumn=({boardColumn.Left:0}..{boardColumn.Right:0})")
              && display.Contains("besideColumn={controls.BeyondColumn}")
              && display.Contains("wallControl={wallControl}"),
            "the success region's line states the column it used, how many interactions stood beside it, and which "
                + "interaction ended the band (type, object name and its own rectangle) - the recorded page could say "
                + "none of the three");
    }

    // ---------- 8. the version this fix ships under ----------

    private static void TheVersionCarrierIsThisFix(Action<bool, string> check)
    {
        string info = ProductionText("PluginInfo.cs");
        const string carrier = "internal const string Version = \"";
        int at = info.IndexOf(carrier, StringComparison.Ordinal);
        string version = at < 0 ? string.Empty
            : info.Substring(at + carrier.Length, info.IndexOf('"', at + carrier.Length) - at - carrier.Length);
        check(version.Length > 0 && char.IsDigit(version[0]) && version != "1.19.50",
            "this fix's carrier is its own patch number, not the 1.19.50 it was cut from: " + version);
        check(ProductionText(Path.Combine("Thunderstore", "manifest.json")).Contains("\"version_number\": \"" + version + "\""),
            "the manifest carries it");
        check(ProductionText(Path.Combine("Thunderstore", "CHANGELOG.md")).Contains("## " + version),
            "the changelog has this build's own section");
        check(ProductionText("README.md").Contains("源码版本为 **" + version + "**"),
            "and the README's current state names it");
    }

    // ---------- helpers ----------

    // `ResultsDisplay.PlateFor` in the units this file works in: the header is reserved first, the plate is the whole
    // rows the rest holds, and the table itself keeps every row it was built with.
    private static void Plate(float room, float header, int rows, float rowHeight, out int visible, out float plate)
    {
        float tableRoom = room - header;
        if (!(tableRoom > 0f)) tableRoom = room;
        ScrollViewport view = ResultsScroll.Viewport(rows, ResultsScroll.FrozenRows, tableRoom, rowHeight,
            ResultsScroll.FrozenRows);
        visible = view.Visible;
        plate = Math.Min(rows * rowHeight, ResultsScroll.PlateHeight(view.Visible, rowHeight, tableRoom));
    }

    // The column a board of `width` occupies, centred and widened by the page's own footer control - the union
    // `ResultsDisplay.BoardColumn` builds.
    private static LayoutColumn Wide(LayoutRect footerControl, float width)
    {
        float x = (ScreenWidth - width) * 0.5f;
        return new LayoutColumn(Math.Min(x, footerControl.X), Math.Max(x + width, footerControl.Right));
    }

    private static bool Near(float left, float right) => Math.Abs(left - right) <= 0.01f;

    private static void Check(bool condition, Action<bool, string> check, string description)
        => check(condition, description);

    private static int Tally(string text, string needle)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(needle)) return 0;
        int count = 0, at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
        return count;
    }

    private static string MethodBody(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        if (at < 0) return string.Empty;
        int open = source.IndexOf('{', at);
        if (open < 0) return string.Empty;
        int depth = 0;
        for (int index = open; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0) return source.Substring(open, index - open + 1);
            }
        }
        return string.Empty;
    }

    private static string ProductionText(string name)
    {
        string root = SourceRoot();
        if (root == null) throw new InvalidOperationException("the production source root was not found");
        return File.ReadAllText(Path.Combine(root, name));
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj")))
            directory = directory.Parent;
        return directory != null ? directory.FullName : null;
    }
}
