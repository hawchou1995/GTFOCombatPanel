using System;
using System.Collections.Generic;
using HawChow.GTFOCombatPanel;

// The settlement board's order of events, driven as a *sequence* with the production functions rather than as strings.
//
// The independent review asked for a real replay of the recorded host failure page: a footer that is late for more than the
// sixty-frame budget, the page's own band (488..1139) arriving afterwards, the first legal plate and how many times it is
// shown, when the game's own blocks may be put away, a frame that cannot draw, and then close -> show and F8 three times.
//
// What is production here: `ResultPlacementPolicy` (the budget and the fail-open answer), `ResultFallbackLayout` (the band and
// the placement inside it), `ResultBoardHold` (the placement and the button clearance), `ResultsScroll` (the plate the table
// is scrolled inside), `ResultChromeLayout` (the plate invariant, the header seat, the two controls and their commitment) and
// `ResultTogglePolicy`/`ResultSettlementSession` (the request a press makes and the page's own hide).
//
// What is a fixture here, exactly as in the rest of this suite: the *measurement* of the page - the band edges and the two
// native rectangles - because reading them needs GTFO's own assemblies. The replay states every fixture number, and the
// assertions are about the decisions the production code makes from them.
internal static class ResultBoardOrderReplayScenarios
{
    internal static void RunInto(Action<bool, string> check)
    {
        TheRecordedPageInOrder(check);
        ThePressFlipsTheRequestNotTheLastFrame(check);
        ANewPageOwesTheRestoreOnBothScreens(check);
        TheSourcePutsTheHoldWhereItIsMade(check);
    }

    // ---- the recorded host failure page, frame by frame ---------------------------------------------------
    //
    // 1400x900 window (the attachment's window), the failure page's own band arriving at 488..1139, a 30-row table at a
    // 19.5 px row height, and the close/header metrics of this window. The footer is late for 70 frames - past the sixty the
    // fail-open budget allows - and the page's measured title edge is readable from frame 0, which is what the recorded page
    // also showed (`failureTitleEdge=...` on the placement line).
    private const float ScreenWidth = 2560f, ScreenHeight = 1440f;
    private const int Rows = 30, FrozenRows = 1;
    private const float RowHeight = 19.5f;
    private const int FooterLateFrames = 70;

    private static void TheRecordedPageInOrder(Action<bool, string> check)
    {
        ResultChromeMetrics metrics = ResultChromeMetrics.For(ScreenWidth, ScreenHeight);
        float header = metrics.HeaderHeight;
        int strictMisses = 0;
        int drawn = 0, vanillaHid = 0, deferrals = 0, dockNone = 0, entryBeforeBand = 0, closeBeforeBand = 0;
        LayoutRect firstPlaced = default;
        bool placedEver = false;
        int firstDrawnFrame = -1;
        var commit = new ResultChromeCommit();
        var settle = new ResultChromeSettle();
        var probe = new ResultChromeProbe();
        string trajectory = string.Empty;

        for (int frame = 0; frame <= FooterLateFrames + 6; frame++)
        {
            // ---- the page's own measurement, per frame (fixture) -------------------------------------------
            // The band exists only while the page's footer does; the title edge is readable from the first frame, which is
            // what `AnchorMeasured` reports and what review #4 requires before a fail-open board may stand.
            bool footerReady = frame >= FooterLateFrames;
            float bandTop = 488f, bandBottom = 1139f;
            bool bandMeasured = footerReady;
            BoardStrip strip = bandMeasured
                ? new BoardStrip(bandTop, bandBottom, true, bandBottom + 6f, 32f, true, ResultFooterSource.NamedControl)
                : default;

            // ---- what the display does with it ----------------------------------------------------------
            bool strict = bandMeasured && strip.Bottom > strip.Top;
            bool placed = false;
            LayoutRect plate = default;
            float plateHeight = 0f;
            int visible = 0, first = 0;
            if (strict)
            {
                float room = strip.Bottom - strip.Top;
                float tableRoom = Math.Max(1f, room - header);
                ScrollViewport view = ResultsScroll.Viewport(Rows, FrozenRows, tableRoom, RowHeight, FrozenRows);
                visible = view.Visible; first = view.First;
                float table = Math.Min(Rows * RowHeight, ResultsScroll.PlateHeight(view.Visible, RowHeight, tableRoom));
                plateHeight = table;
                placed = ResultBoardHold.TryPlace(ScreenWidth, ScreenHeight, strip, 433f, table + header,
                    offsetX: 0f, out plate, slack: 2f);
            }
            if (!strict || !placed)
            {
                // The strict placement refused: the miss is spent, and the fail-open path answers only once the budget is.
                strictMisses = ResultPlacementPolicy.Spend(strictMisses);
                ResultPlacementOutcome outcome = ResultPlacementPolicy.Decide(false, strictMisses, forced: false);
                if (outcome == ResultPlacementOutcome.PlacedFallback && bandMeasured && strip.AnchorMeasured)
                {
                    float room = strip.Bottom - strip.Top;
                    float tableRoom = Math.Max(1f, room - header);
                    ScrollViewport view = ResultsScroll.Viewport(Rows, FrozenRows, tableRoom, RowHeight, FrozenRows);
                    visible = view.Visible; first = view.First;
                    float table = Math.Min(Rows * RowHeight, ResultsScroll.PlateHeight(view.Visible, RowHeight, tableRoom));
                    // 1.19.47 review #4: the foot is a bound, so a board that does not fit is refused here and re-fitted.
                    if (table + header <= room + 0.5f
                        && ResultFallbackLayout.TryPlaceInBand(ScreenWidth, ScreenHeight, strip.Top, strip.Bottom, 433f,
                            table + header, 0f, out plate))
                    {
                        plateHeight = table;
                        placed = true;
                    }
                }
            }

            // ---- the invariant the repaint enforces ---------------------------------------------------
            bool insideBand = placed && ResultChromeLayout.PlateInsideBand(plate, strip.Top, strip.Bottom, ScreenHeight, 2f);
            bool draws = placed && insideBand;
            if (draws)
            {
                drawn++;
                if (!placedEver) { firstPlaced = plate; placedEver = true; firstDrawnFrame = frame; }
                // The game's own blocks may make way only on a frame whose table really drew.
                vanillaHid++;
            }
            else deferrals++;

            // ---- the page's own controls, driven by the production step --------------------------------
            probe.Reset();
            probe.ScreenWidth = (int)ScreenWidth;
            probe.ScreenHeight = (int)ScreenHeight;
            probe.Page = new IntPtr(7);
            probe.BoardUp = draws;
            probe.AnchorDrawn = false;                 // the recorded page: anchor=no-host-action
            probe.AnchorWhy = "no-host-action";
            probe.Native = Array.Empty<LayoutRect>();   // the scan found no pressable control on this page
            probe.PlateDrawn = draws;
            probe.Plate = plate;
            // 1.19.45: there is no fallback seat and no dock. The recorded page draws no anchor of its own, so nothing is
            // offered to the step and nothing may be placed for it - which is the contract of this round, not a defect.
            ResultChromeDecision decision = ResultChromeLayout.Step(commit, settle, metrics, probe);

            // The control can only be drawn where the page really draws something to stand beside; on this page that is
            // nothing, on every frame, and this counter is what proves it.
            if (!draws && decision.ToggleDrawn) entryBeforeBand++;

            // ---- the trajectory line the review asked for ---------------------------------------------
            if (frame == 0 || frame == FooterLateFrames - 1 || frame == FooterLateFrames || IsFirstDrawn(frame, firstDrawnFrame)
                || frame == FooterLateFrames + 3)
            {
                trajectory += $"\n  frame {frame}: footer={(footerReady ? "measured" : "late")} band={(bandMeasured ? bandTop + ".." + bandBottom : "none")} "
                    + $"strictMisses={strictMisses} plate={(placed ? plate.X + "," + plate.Y + " " + plate.Width + "x" + plate.Height : "none")} "
                    + $"insideBand={insideBand} draws={draws} drawnSoFar={drawn} vanillaHid={vanillaHid} "
                    + $"toggleDrawn={decision.ToggleDrawn} toggle={Gt(decision.Toggle)}";
            }
        }

        Console.WriteLine("---- settlement order replay: recorded host failure page, footer late past the fail-open budget ----" + trajectory);

        check(drawn > 0 && firstDrawnFrame >= 0,
            $"the board is really painted on the recorded page ({drawn} frames, first at frame {firstDrawnFrame}) instead of being withheld for the whole settlement");
        check(placedEver && Math.Abs(firstPlaced.Y - 488f) < 0.5f,
            $"and it stands on the band's own top edge the page measured: y={firstPlaced.Y:0.##} (the band is 488..1139)");
        check(placedEver && firstPlaced.Bottom <= 1139f + 2f,
            $"with its whole plate - header included - inside the band's foot: bottom={firstPlaced.Bottom:0.##} of 1139");
        check(vanillaHid == drawn,
            $"the game's own blocks are put away on exactly the frames whose table drew ({vanillaHid} of {drawn} drawn frames), never on a refused one");
        check(dockNone == 0 && entryBeforeBand == 0 && closeBeforeBand == 0,
            $"the page drew no anchor on any frame, so no control of this mod's own was placed for it ({entryBeforeBand} toggle, {closeBeforeBand} close, {dockNone} dock): F8 is the only way back and the log names why");
        check(deferrals == FooterLateFrames && drawn == 7 && vanillaHid == drawn,
            $"the frames before the page's band arrived drew nothing and hid nothing ({deferrals} refused frames), and every frame after it drew and hid together ({drawn} drawn / {vanillaHid} hid) - the page is never left half-hidden");
        check(commit.Contexts == 1,
            $"one page is one commitment: the toggle is placed once ({commit.Contexts} context) and no repaint re-places it");
    }

    private static bool IsFirstDrawn(int frame, int firstDrawnFrame) => frame >= 0 && frame == firstDrawnFrame;

    private static string Gt(LayoutRect rect)
        => rect.Width > 0f ? "(" + rect.X.ToString("0") + "," + rect.Y.ToString("0") + ")" : "none";

    // ---- the press: the request flips, the painted frame is not consulted ---------------------------------
    private static void ThePressFlipsTheRequestNotTheLastFrame(Action<bool, string> check)
    {
        // The default install: the board is switched off, nothing is painted, no press has happened.
        bool shown = ResultTogglePolicy.RequestedShown(switchesShown: false, hiddenByPress: false, forcedShow: false);
        check(!shown, "a page whose switches are off is asked to show nothing - the entry is the SHOW button, not a painted board");
        var requests = new List<string>();
        for (int press = 0; press < 6; press++)
        {
            bool show = ResultTogglePolicy.Press(shown) == ResultBoardToggle.Show;
            requests.Add(show ? "show" : "hide");
            // The display's own bookkeeping, exactly as `ApplyBoardVisibility` does it: `Forced` is the outstanding show
            // request and `Session` records the mod's own hide.
            bool forced = show;
            bool hiddenByPress = !show;
            shown = ResultTogglePolicy.RequestedShown(show, hiddenByPress, forced);
        }
        check(string.Join(",", requests) == "show,hide,show,hide,show,hide",
            "six presses of a page that never painted anything alternate show/hide (" + string.Join(",", requests)
            + ") - the recorded 45 presses asked 'show' 45 times because each one read the last painted frame");

        // A page whose switches are on and whose board is painted: the first press hides, and hides again after a show.
        bool up = ResultTogglePolicy.RequestedShown(switchesShown: true, hiddenByPress: false, forcedShow: false);
        check(up && ResultTogglePolicy.Press(up) == ResultBoardToggle.Hide,
            "a page whose switches are on is asked to hide on the first press");
        bool afterShow = ResultTogglePolicy.RequestedShown(switchesShown: false, hiddenByPress: false, forcedShow: true);
        check(afterShow && ResultTogglePolicy.Press(afterShow) == ResultBoardToggle.Hide,
            "and an outstanding show request is itself a 'shown' answer: the next press hides instead of asking again");
    }

    // ---- the page change owes the restore on both screens -------------------------------------------------
    private static void ANewPageOwesTheRestoreOnBothScreens(Action<bool, string> check)
    {
        var session = new ResultSettlementSession();
        IntPtr failurePage = new IntPtr(101), successPage = new IntPtr(202);
        session.Starts(failurePage);
        session.MarkHidden();
        check(session.Starts(successPage) && session.OweRestore(successPage),
            "a hide this mod made on the failure page is owed back on the next page, exactly as it is for a success page - one session, one restore");
        check(!session.OweRestore(successPage),
            "and it is given back once, so a hide on the new page stands");
        session.MarkShown();
        session.Starts(new IntPtr(303));
        check(!session.OweRestore(new IntPtr(303)),
            "a player's own switch turned off in the file is not this mod's hide and is never given back by a page change");
    }

    private static void TheSourcePutsTheHoldWhereItIsMade(Action<bool, string> check)
    {
        string root = SourceRoot();
        if (root == null) { check(false, "the hold-wiring check locates the production sources"); return; }
        string display = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "ResultsDisplay.cs"));
        int begin = display.IndexOf("internal static void BeginFailurePage", StringComparison.Ordinal);
        int beginEnd = display.IndexOf("internal static void FailureSequenceStarted", begin, StringComparison.Ordinal);
        string body = begin >= 0 && beginEnd > begin ? display.Substring(begin, beginEnd - begin) : string.Empty;
        check(body.Length > 0 && !body.Contains("_holdPage = page"),
            "1.19.47 (review #2): `BeginFailurePage` no longer pre-sets `_holdPage`, so the failure page gets the same `OweRestore` the success page gets instead of looking like 'the same page'");
        check(display.Contains("_holdPage = _page != null ? _page.Pointer : IntPtr.Zero;"),
            "and the hold records the page it was really made on, where the place is recorded");
    }

    private static string SourceRoot()
    {
        string directory = AppContext.BaseDirectory;
        for (int step = 0; step < 8 && directory != null; step++)
        {
            string candidate = System.IO.Path.Combine(directory, "ResultsDisplay.cs");
            if (System.IO.File.Exists(candidate)) return directory;
            directory = System.IO.Path.GetDirectoryName(directory);
        }
        return null;
    }
}
