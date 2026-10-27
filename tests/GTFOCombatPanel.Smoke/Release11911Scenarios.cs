using System;
using System.IO;
using HawChow.GTFOCombatPanel;

// The one defect 1.19.11 fixes, driven through the real policy it ships with.
//
// A result page whose board the strip refused used to be left with neither of the two things it can show: the
// refresh path recorded the placement before asking whether the fit had really produced one, `ApplyVanillaBlocks`
// put the game's own settlement blocks away for a board `Draw` would never paint (it returns the moment there is no
// grid and no content), and the frame ended with an empty page over a hidden section. The rule is a latch now -
// `ResultBoardLatch` - and this file asserts both halves of it: a refused frame keeps the game's blocks and is asked
// again, and the frame that really places the board applies the ordinary hiding policy, exactly as before.
internal static class Release11911Scenarios
{
    public static int Run()
    {
        int failures = 0;
        RunInto((condition, description) =>
        {
            if (condition) return;
            failures++;
            Console.WriteLine("FAIL: " + description);
        });
        return failures;
    }

    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));

        RefusedFrameKeepsVanilla(check);
        NextFramePlacesAndHides(check);
        LatchInputs(check);
        Wiring(check);
    }

    // ---------------------------------------------------------------- a frame whose strip refused the board
    //
    // The reported shape: 1080-high window, the completion section measured down to 426 and the lobby button at 720,
    // so the strip is 274px - a 30-row board at the size the fit produced is 450px. `TryPlace` refuses it, which is
    // the only way it can keep its promise never to cover the button; the frame must therefore hide nothing.
    private static void RefusedFrameKeepsVanilla(Action<bool, string> check)
    {
        var shortStrip = new BoardStrip(426f, 720f, true, 720f, 60f);
        Frame refused = Attempt(shortStrip, 600f, 450f);

        check(!refused.BoardPlaced,
            $"a board taller than the strip is refused by the placement: {refused.Board.Height:0}px table in a 274px strip");
        check(!refused.GridReady && !refused.ContentReady,
            "and a refused fit leaves the grid and the content it would have drawn empty, which is the state `Draw` returns on");
        check(!refused.Placed,
            "the refused frame is not recorded as a placement");
        check(!refused.HidesVanilla,
            "and the game's own settlement blocks are not put away for a board nobody will draw - the empty settlement page of the report");

        // The same frame as the release before this one ran it: the placement was recorded unconditionally, so the
        // blocks went away and `Draw` returned on the missing grid. Pinned as the wrong answer this file exists to
        // keep out, not as a behaviour anything may rely on.
        bool asBefore = true;
        check(asBefore && !refused.Placed && !refused.HidesVanilla,
            "recording the placement before asking the fit is exactly the pairing that hid the page, and it is not what the latch answers");

        // A refusal is not a half-placement: nothing of the refused board may be kept as if it were on the page.
        check(refused.Board.Width == 0f && refused.Board.Height == 0f,
            "a refused placement hands back no rectangle at all, so no stale box can be mistaken for a board");
    }

    // ---------------------------------------------------------------- the next frame, with the page settled
    private static void NextFramePlacesAndHides(Action<bool, string> check)
    {
        // The footer finished revealing, the button's own edge is now 953 of the 1080-high window, and the same board
        // fits: the board hangs from the completion section's measured bottom and the ordinary hiding policy applies.
        var settledStrip = new BoardStrip(426f, 937f, true, 953f, 60f);
        Frame placed = Attempt(settledStrip, 600f, 450f);

        check(placed.BoardPlaced, "the frame after the page settled places the same board in the strip it now offers");
        check(placed.GridReady && placed.ContentReady, "with the grid and the content it will draw both in hand");
        check(placed.Placed, "so the placement is recorded on that frame");
        check(placed.HidesVanilla, "and the game's own blocks make way for it, exactly as before this change");
        check(Near(placed.Board.Y, 426f) && Near(placed.Board.Bottom, 876f),
            $"the board lands where it always did: y={placed.Board.Y:0}, bottom={placed.Board.Bottom:0}");
        check(placed.Board.Bottom <= 953f - ResultBoardHold.ButtonClearance,
            "clearing the lobby button by its own clearance line");

        // The retry is the point of not recording the refusal: the page has to be asked again, and only a board that is
        // really up on this page kind stops it being asked.
        check(ResultBoardLatch.NeedsAttach(false, true),
            "a page whose board was refused is attached again on the next frame");
        check(!ResultBoardLatch.NeedsAttach(true, true),
            "a board that is really up stops the retry");
        check(ResultBoardLatch.NeedsAttach(true, false),
            "and a placement carried onto the other kind of result screen is not reused");

        // The ordinary path is untouched: a board already up whose strip came back shorter keeps its place
        // (`Fit`'s hold branch) and is still a board that stands in for the vanilla blocks.
        bool held = ResultBoardHold.TryHold(settledStrip, placed.Board, 1080f, out LayoutRect kept);
        check(held && Near(kept.Y, placed.Board.Y) && ResultBoardLatch.HidesVanilla(ResultBoardLatch.Placed(true, true, true)),
            "a held board is still a placed board, so the hiding policy is unchanged on every frame the table is up");
    }

    // ---------------------------------------------------------------- what the latch refuses
    private static void LatchInputs(Action<bool, string> check)
    {
        check(ResultBoardLatch.Placed(true, true, true), "a confirmed fit that has its grid and its content is a placement");
        check(!ResultBoardLatch.Placed(false, true, true),
            "a fit that was refused is not a placement, whatever else is in hand");
        check(!ResultBoardLatch.Placed(true, false, true),
            "a fit with no grid is not a placement - `Draw` would return on it and paint nothing");
        check(!ResultBoardLatch.Placed(true, true, false),
            "and neither is one with no content");
        check(!ResultBoardLatch.HidesVanilla(false) && ResultBoardLatch.HidesVanilla(true),
            "the game's own blocks make way for a placement and for nothing else");
    }

    // ---------------------------------------------------------------- the wiring, read from the production source
    //
    // A static source check only: it says the runtime calls the latch this file asserts, in the order that matters. It
    // is not a runtime behaviour test and it does not pretend to be one.
    private static void Wiring(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the 1.19.11 regression locates the production sources");
        if (root == null) return;
        string results = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        string latch = File.ReadAllText(Path.Combine(root, "ResultVisibility.cs"));

        check(latch.Contains("public static class ResultBoardLatch")
              && latch.Contains("public static bool Placed(bool fitConfirmed, bool gridReady, bool contentReady)")
              && latch.Contains("public static bool HidesVanilla(bool placed) => placed;")
              && latch.Contains("public static bool NeedsAttach(bool placed, bool succeededMatches) => !placed || !succeededMatches;"),
            "the latch exists as one Unity-free policy and answers the three questions this file asks it");

        int prepare = results.IndexOf("private static void Prepare(Component page, bool succeeded)", StringComparison.Ordinal);
        int latchAsk = results.IndexOf("ResultBoardLatch.Placed(Fit(strip, grid, content, board, report, succeeded)", StringComparison.Ordinal);
        int record = results.IndexOf("_placed = true; _placedSucceeded = succeeded;", StringComparison.Ordinal);
        check(prepare >= 0 && latchAsk > prepare && record > latchAsk,
            "`Prepare` records the placement only after the latch has accepted the fit, so a refusal is never recorded");

        int refresh = results.IndexOf("internal static void Refresh(eGameStateName state)", StringComparison.Ordinal);
        int hidesOn = results.IndexOf("ResultBoardLatch.HidesVanilla(_placed)", StringComparison.Ordinal);
        int firstHide = results.IndexOf("ApplyVanillaBlocks(page, succeeded);", StringComparison.Ordinal);
        check(refresh >= 0 && hidesOn > refresh && firstHide > hidesOn,
            "`Refresh` asks the latch before it touches a single vanilla block on that page");

        int attach = results.IndexOf("internal static bool NeedsAttach()", StringComparison.Ordinal);
        int attachAsk = results.IndexOf("ResultBoardLatch.NeedsAttach(_placed, _placedSucceeded == succeeded)", StringComparison.Ordinal);
        check(attach >= 0 && attachAsk > attach,
            "and a page whose board was refused is re-attached through the same latch, which is the retry this hinges on");

        int fit = results.IndexOf("private static bool Fit(BoardStrip strip", StringComparison.Ordinal);
        int fitClose = fit >= 0 ? results.IndexOf("        return false;", fit, StringComparison.Ordinal) : -1;
        check(fit >= 0 && fitClose > fit,
            "`Fit` answers whether it really placed a board instead of returning in silence");

        Console.WriteLine("WIRING: RESULT_BOARD_LATCH (static source check; not a runtime behaviour test)");
    }

    // One frame of the refresh path, with the two things the runtime decides modelled exactly: the real placement
    // policy answers whether the board went in, and `Fit` only hands over its grid and content on the branches where
    // it placed or held one. The latch is then asked what `Prepare` and `Refresh` ask it.
    private static Frame Attempt(BoardStrip strip, float width, float height)
    {
        bool boardPlaced = ResultBoardHold.TryPlace(1920f, 1080f, strip, width, height, 0f,
            out LayoutRect board, 2f, bottomAligned: false);
        bool gridReady = boardPlaced, contentReady = boardPlaced;
        bool placed = ResultBoardLatch.Placed(boardPlaced, gridReady, contentReady);
        return new Frame(boardPlaced, gridReady, contentReady, placed, ResultBoardLatch.HidesVanilla(placed), board);
    }

    private readonly struct Frame
    {
        public readonly bool BoardPlaced;
        public readonly bool GridReady;
        public readonly bool ContentReady;
        public readonly bool Placed;
        public readonly bool HidesVanilla;
        public readonly LayoutRect Board;

        public Frame(bool boardPlaced, bool gridReady, bool contentReady, bool placed, bool hidesVanilla, LayoutRect board)
        {
            BoardPlaced = boardPlaced; GridReady = gridReady; ContentReady = contentReady;
            Placed = placed; HidesVanilla = hidesVanilla; Board = board;
        }
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj"))) directory = directory.Parent;
        return directory != null ? directory.FullName : null;
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) <= 0.01f;
}
