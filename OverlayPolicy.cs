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
