namespace HawChow.GTFOCombatPanel;

// What the F8 key does to the configuration file. Kept free of Unity and BepInEx so the decision is
// asserted headlessly: the key flips both switches - the in-level table and the settlement board are
// one and the same overlay - and the change is written back to the .cfg and saved, so the panel comes
// back the way it was left instead of resetting on every restart.
public static class TogglePolicy
{
    public static ToggleAction Apply(bool visible) => new ToggleAction(visible, visible, true);
}

public readonly struct ToggleAction
{
    public readonly bool ShowPanel;
    public readonly bool ShowResultOverlay;
    public readonly bool Save;
    public ToggleAction(bool showPanel, bool showResultOverlay, bool save)
    { ShowPanel = showPanel; ShowResultOverlay = showResultOverlay; Save = save; }
}

// Whether this mod draws anything over a result page at all, and whether the two vanilla blocks the
// board stands in for are put away. Three switches have to agree before a single pixel is drawn - the
// master switch, the result overlay and the influence board - and the F8 toggle stands in front of all
// three. There is no older statistics table behind them: a page this refuses is the page the game
// drew, whole. The two block switches are positive and off by default, so a fresh install hides the
// artifact block and the prisoner report cards and the board owns the page.
public static class OverlayPolicy
{
    public static bool Draws(bool showResults, bool showOverlay, bool showBoard, bool toggled)
        => showResults && showOverlay && showBoard && toggled;

    public static bool HidesArtifacts(bool showArtifacts) => !showArtifacts;
    public static bool HidesReports(bool showReports) => !showReports;
}

// ---- F8 on a result page: hide the board that is up, give one to a page that has none ---------------------
//
// The key is one key with one meaning on both screens: it hides what the player is looking at and brings it back
// on the next press. What it may *not* be is a flag of its own - `Forced` defaults to false, says nothing about
// whether a board is on the screen, and a press on a result page whose fail-open board had already appeared
// therefore turned the override on and left the board exactly where it was (a live 1.19.25 page: fifteen presses
// that put a board back on a board, fourteen that took the page away for reasons the player could not see).
//
// So the next answer is read from the display: `BoardUp` is the last repaint's own "a board was drawn" fact, and
// `Next` is the whole of the decision. Nothing here knows about a title, a footer, a scan or a strip - those
// decide whether a board *can* be drawn, never whether the key was a hide or a show.
public enum ResultBoardToggle { Show = 0, Hide = 1 }

public static class ResultTogglePolicy
{
    // Whether a board is on the page right now, from the frame of the last repaint that really painted one.
    // `-1` is "the last repaint painted nothing", which is what every refused, held-back or hidden frame writes:
    // a board that was drawn once and taken away again is not a board the page is showing. The one-frame slack
    // covers `Update` running between a repaint and the next one on the same page.
    public static bool BoardUp(int drawnFrame, int currentFrame)
        => drawnFrame >= 0 && currentFrame >= drawnFrame && currentFrame - drawnFrame <= 1;

    // The press's whole answer: a page showing the board hides it, and a page showing none is given one.
    public static ResultBoardToggle Next(bool boardUp)
        => boardUp ? ResultBoardToggle.Hide : ResultBoardToggle.Show;

}
