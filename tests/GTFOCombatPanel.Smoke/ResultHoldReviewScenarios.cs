using System;
using System.IO;
using System.Reflection;
using HawChow.GTFOCombatPanel;

// 1.19.59 (independent review of the round): the five findings, driven rather than read.
//
// What this file asserts, and how:
//   * H1, M3 and the drawing half's shape are asserted by RUNNING the production state machine
//     (`ResultVanillaHoldLedger` / `ResultVanillaHold`, the same objects `ResultsDisplay` uses). The defect was a state
//     transition - a record written by the drawing half and then reused by the move - so a state transition is what the
//     test drives: visual first, then a move, then the way back, and the answer must be the page's own non-zero position
//     and the page's own active state.
//   * H2's "no raycast flag" is asserted twice: on the type (no member of the production record can carry one) and on the
//     production source (the words must not appear at all, which is the only honest form of a "never" claim).
//   * L4 and L5 are asserted where they are decidable without a live game: the record's own answers, and the order of the
//     calls that make them (`HoldStillApplies` before the classifier, `EnsurePointer` before the wheel judges the pointer).
//     What those two cannot prove headlessly - that Unity's `GetComponentsInChildren` really returns a `Button` that
//     appeared later, that `Time.frameCount` really advances once per frame - is stated as a boundary in the round's
//     report instead of being claimed here.
internal static class ResultHoldReviewScenarios
{
    internal static void RunInto(Action<bool, string> check)
    {
        H1TheMoveIsTakenAtTheFirstMove(check);
        M3OnlyWhatWasTakenIsPutBack(check);
        H2TheDrawingHalfOwnsNoRaycastAndGivesAButtonBack(check);
        L4ATakenBlockIsNotMeasuredAgain(check);
        L5OnePointerSamplePerFrame(check);
    }

    // ---- H1: `MoveOffPage` hitting an existing `SuppressVisual` record ----------------------------------
    //
    // The recorded defect: a block whose drawing was switched off first left a record with `Target = null` and
    // `Position = default`; the move then reused it, so F8 / a page change / leaving the screens put the block back at
    // (0,0,0) - for the rest of the run, because the record is written once.
    private static void H1TheMoveIsTakenAtTheFirstMove(Action<bool, string> check)
    {
        var ledger = new ResultVanillaHoldLedger();
        IntPtr block = new IntPtr(0x4A11);
        ResultVanillaHold drawing = ledger.HoldVisual(block, null, 1f, new object[0], new bool[0], new object[0], new float[0]);
        check(drawing.VisualHeld && !drawing.MoveHeld,
            "H1: a block whose drawing was switched off first holds the drawing half only");
        check(!drawing.TryRestorePosition(out _, out _, out _) && !drawing.TryRestoreActive(out _),
            "H1: and a drawing-only record answers neither a position nor an active state, so it can no longer stand in for the move");

        ResultVanillaHold moved = ledger.HoldMove(block, "transform", "host", 12f, 340f, -7f, true);
        check(ReferenceEquals(moved, drawing) && moved.VisualHeld && moved.MoveHeld,
            "H1: the move that follows joins the same block's record and takes both halves");
        check(moved.TryRestorePosition(out float x, out float y, out float z) && Near(x, 12f) && Near(y, 340f) && Near(z, -7f),
            "H1: the way back is the page's own non-zero position (" + x.ToString("0") + "," + y.ToString("0") + "," + z.ToString("0")
                + "), not the (0,0,0) the old record answered with");
        check(moved.TryRestoreActive(out bool active) && active,
            "H1: and the page's own active state, taken at the same moment");

        // The next frame's move: the block is already off the page, so the position it reads now is this mod's own
        // displacement. The record must answer with the page's position, which is what makes the write idempotent.
        ResultVanillaHold again = ledger.HoldMove(block, "transform", "host", 0f, 6000f, 0f, false);
        check(again.TryRestorePosition(out x, out y, out z) && Near(x, 12f) && Near(y, 340f) && Near(z, -7f) && again.ActiveSelf,
            "H1: a second move in a later frame cannot overwrite the recorded position or active state");
        check(ledger.Count == 1, "H1: one block is one record, whatever halves it has carried");

        // The order the defect came from, the other way round: a block moved whole and then switched off at the drawing
        // keeps its move half, so the block still comes back where the page drew it.
        var reverse = new ResultVanillaHoldLedger();
        IntPtr whole = new IntPtr(0x4A12);
        reverse.HoldMove(whole, "t", "h", -3f, 512f, 4f, false);
        ResultVanillaHold both = reverse.HoldVisual(whole, null, 1f, new object[] { "r1" }, new bool[] { true }, new object[0], new float[0]);
        check(both.MoveHeld && both.VisualHeld && both.TryRestorePosition(out x, out y, out z) && Near(y, 512f) && !both.ActiveSelf,
            "H1: the drawing half cannot replace the move half either - both are held, and both are put back from what was really taken");
    }

    // ---- M3: a drawing-only record must not switch a block on, and nothing may be added to the game's objects ----
    private static void M3OnlyWhatWasTakenIsPutBack(Action<bool, string> check)
    {
        var ledger = new ResultVanillaHoldLedger();
        // (a) a block the page had switched off, whose drawing alone is replaced: the way back must not switch it on.
        ResultVanillaHold off = ledger.HoldVisual(new IntPtr(0x4B01), null, 1f, new object[0], new bool[0], new object[0], new float[0]);
        check(!off.TryRestoreActive(out bool active),
            "M3: a block the page left switched off is never switched on by the way back - the drawing half holds no active state at all");
        check(!active, "M3: and the state it does report for a drawing-only hold is the page's own (off), never a default \"on\"");
        // (b) the same block, moved later while still switched off: the recorded state is the page's, i.e. off.
        off = ledger.HoldMove(new IntPtr(0x4B01), "t", "h", 5f, 6f, 7f, false);
        check(off.TryRestoreActive(out active) && !active,
            "M3: a block that was already switched off when it was moved is put back switched off");
        // (c) the game switches the block on afterwards: the record still describes the page before this mod touched it.
        off = ledger.HoldMove(new IntPtr(0x4B01), "t", "h", 5f, 6f, 7f, true);
        check(off.TryRestoreActive(out active) && !active,
            "M3: and a state the page changed afterwards cannot rewrite what the block looked like before the first move");

        // (d) the three group cases the review asked for: an existing group, no group at all, and - the one that must
        // never exist again - a group this mod added.
        object group = new object();
        ResultVanillaHold withGroup = ledger.HoldVisual(new IntPtr(0x4B02), group, 0.8f, new object[0], new bool[0], new object[0], new float[0]);
        check(withGroup.TryRestoreGroup(out float alpha) && Near(alpha, 0.8f) && ReferenceEquals(withGroup.Group, group),
            "M3: a group the block already had is recorded with its own alpha, and only its alpha");
        ResultVanillaHold withoutGroup = ledger.HoldVisual(new IntPtr(0x4B03), null, 1f, new object[0], new bool[0], new object[0], new float[0]);
        check(!withoutGroup.TryRestoreGroup(out _) && withoutGroup.VisualHeld,
            "M3: a block with no CanvasGroup records no group and is hidden through its renderers alone, so nothing is added to the game's object");
        bool adds = false;
        foreach (FieldInfo field in RecordMembers())
        {
            string name = field.Name.ToLowerInvariant();
            if (name.Contains("added") || name.Contains("created") || name.Contains("destroy")) adds = true;
        }
        check(!adds, "M3: the production record has no member that could describe a component this mod added, which is the state that needed a Destroy to undo");

        // (e) only what was really taken is put back: a renderer that was already off is not part of the record.
        var partial = new ResultVanillaHoldLedger();
        ResultVanillaHold kept = partial.HoldVisual(new IntPtr(0x4B04), null, 1f,
            new object[] { "the one that was drawing" }, new bool[] { true }, new object[] { "canvas" }, new float[] { 0.35f });
        check(kept.Renderers.Length == 1 && kept.RendererEnabled[0] && Near(kept.CanvasAlpha[0], 0.35f),
            "M3: the record holds the renderers that were really drawing and the alpha that was really there");
    }

    // ---- H2: visual suppression must never become a press -------------------------------------------------
    private static void H2TheDrawingHalfOwnsNoRaycastAndGivesAButtonBack(Action<bool, string> check)
    {
        bool raycast = false;
        foreach (FieldInfo field in RecordMembers())
        {
            string name = field.Name.ToLowerInvariant();
            if (name.Contains("raycast") || name.Contains("interact")) raycast = true;
        }
        check(!raycast, "H2: the production record cannot carry a raycast or interactable flag, so the restore cannot write one");

        string root = SourceRoot();
        if (root == null) { check(false, "H2: the round's scenarios locate the production sources"); return; }
        string display = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        check(!display.Contains(".interactable =") && !display.Contains(".blocksRaycasts ="),
            "H2: and no raycast or interactable flag is written anywhere in the production source - the drawing half owns alpha and renderer enablement alone");
        check(!display.Contains("AddComponent<CanvasGroup>"),
            "M3: and no CanvasGroup is added to the game's object anywhere, so there is nothing to destroy a frame late");
        check(display.Contains("if (HasButton(block))") && display.Contains("GiveBack(block, record, what);"),
            "H2: a block that really carries a control is left visible where the page put it, and one that turns out to carry one after all is given back");
        check(!display.Contains("record.Group.interactable") && !display.Contains("record.BlocksRaycasts"),
            "H2: the old raycast writes are gone from the restore as well as from the hide");

        // The mixed subtree: the block keeps its hit and its visibility. A block whose own body carries a `Button` is never
        // answered with the drawing-only plan, and a mixed block still gives up only the branches that carry no action.
        check(ResultVanillaClassify.PlanForBlock(VanillaNodeKind.Visual, 1, 0, 0, true) == VanillaHidePlan.Nothing
              && ResultVanillaClassify.PlanForBlock(VanillaNodeKind.Visual, 1, 0, 0) == VanillaHidePlan.VisualOnly,
            "H2: a block that really carries a `Button` is never answered with the drawing-only plan - invisible but pressable is not an improvement - while the same block without one gives up its drawing");
        check(ResultVanillaClassify.PlanForBlock(VanillaNodeKind.Visual, 1, 2, 1) == VanillaHidePlan.SafeBranches,
            "H2: and a mixed block still gives up only its proven-visual branches, so the branch that carries the action keeps its hit and its visibility");
        check(ResultVanillaClassify.PlanForBlock(VanillaNodeKind.Action, 0, 0, 0) == VanillaHidePlan.Nothing,
            "H2: and a block that is itself the page's control is left exactly where the page put it");

        // The give-back itself, as the record sees it: the hold is dropped, so the next frame asks the classifier again
        // instead of going on hiding a block that now holds the player's control.
        var ledger = new ResultVanillaHoldLedger();
        IntPtr block = new IntPtr(0x4C01);
        ResultVanillaHold held = ledger.HoldVisual(block, null, 0.5f, new object[] { "r" }, new bool[] { true }, new object[0], new float[0]);
        check(ledger.TryGetValue(block, out ResultVanillaHold found) && ReferenceEquals(held, found),
            "H2: a block that was switched off at the drawing is held while nothing inside it can be pressed");
        check(ledger.Drop(block) && !ledger.TryGetValue(block, out _) && ledger.Count == 0,
            "H2: giving it back drops the record entirely, which is what makes the next frame classify the block again");
    }

    // ---- L4: a taken block is not measured again, and the one safety check survives ----------------------
    private static void L4ATakenBlockIsNotMeasuredAgain(Action<bool, string> check)
    {
        string root = SourceRoot();
        if (root == null) { check(false, "L4: the round's scenarios locate the production sources"); return; }
        string display = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        string hide = MethodBody(display, "private static void Hide(Component block, string what, Component page, bool succeeded, bool visualOnly = false)");
        check(hide.Length > 0, "L4: `Hide` is where the test found it");
        int cheap = hide.IndexOf("if (HoldStillApplies(block, what)) return;", StringComparison.Ordinal);
        int classify = hide.IndexOf("VanillaHidePlan plan = PlanOfBlock(block, page, succeeded);", StringComparison.Ordinal);
        int branches = hide.IndexOf("HideSafeBranches(block, what, page, succeeded)) return;", StringComparison.Ordinal);
        check(cheap > 0 && classify > cheap && branches > classify,
            "L4: the record is asked before the classifier and before the branch walk, so an already taken block costs one lookup and one button check");
        check(!hide.Contains("HashSet"), "L4: and there is no permanent skip list - the record is skipped, and only while it is still true");

        string still = MethodBody(display, "private static bool HoldStillApplies(Component block, string what)");
        check(still.Length > 0, "L4: the cheap path is where the test found it");
        int button = still.IndexOf("if (HasButton(block))", StringComparison.Ordinal);
        int move = still.IndexOf("if (record.MoveHeld)", StringComparison.Ordinal);
        int drawing = still.IndexOf("if (record.VisualHeld)", StringComparison.Ordinal);
        check(button > 0 && move > button && drawing > move,
            "L4: the block is asked for the player's own control first, before anything is re-applied");
        check(still.Contains("target.localPosition.y != AwayY") && still.Contains("if (host != null && host.activeSelf) host.SetActive(false);"),
            "L4: a block that is still away and still switched off is verified with two reads and written by neither");
        check(still.Contains("GiveBack(block, record, what);"),
            "L4: and a block whose answer cannot be read is given back rather than left hidden on the strength of a failed read");
        check(MethodBody(display, "private static void GiveBack(Component block, ResultVanillaHold record, string what)").Contains("PutBack(record);")
              && MethodBody(display, "private static void GiveBack(Component block, ResultVanillaHold record, string what)").Contains("Hidden.Drop(record.Key);"),
            "L4: giving a block back puts everything back from the record and then forgets it");
        check(MethodBody(display, "private static void ReapplyDrawing(ResultVanillaHold record)").Contains("CanvasRenderer canvas"),
            "L4: the drawing half is re-applied from the record's own arrays, with no search of the block and no classifier");

        // The record's answers, which are what the cheap path reads.
        var ledger = new ResultVanillaHoldLedger();
        IntPtr taken = new IntPtr(0x4D01);
        check(!ledger.TryGetValue(taken, out _), "L4: a block this mod has not taken has no record, so nothing is skipped on its behalf");
        ResultVanillaHold held = ledger.HoldMove(taken, "t", "h", 1f, 2f, 3f, true);
        check(ledger.TryGetValue(taken, out ResultVanillaHold same) && ReferenceEquals(held, same) && held.MoveHeld,
            "L4: a taken block answers from the record alone, which is the whole point of the cheap path");
        ledger.HoldVisual(new IntPtr(0x4D02), null, 1f, new object[0], new bool[0], new object[0], new float[0]);
        check(ledger.Count == 2, "L4: one record per block, whatever halves it carries");
        check(ledger.Holds != null, "L4: and the restore still walks every record in one pass");
    }

    // ---- L5: one pointer sample per frame, shared by the wheel and the press ------------------------------
    private static void L5OnePointerSamplePerFrame(Action<bool, string> check)
    {
        var cache = new ResultPointerFrameCache();
        // The repaint draws, then the page's own dispatch settles the wheel and then the press, all in frame 1200.
        check(cache.Claim(1200) && cache.Samples == 1 && cache.Frame == 1200,
            "L5: the first entry in a frame takes that frame's sample");
        check(!cache.Claim(1200) && !cache.Claim(1200) && cache.Samples == 1,
            "L5: the second and third entry in the same frame read the same sample - the wheel can no longer judge a point the press does not agree with");
        check(cache.Sampled(1200) && !cache.Sampled(1201),
            "L5: and the sample is this frame's, not the previous one's");
        check(cache.Claim(1201) && cache.Samples == 2 && cache.Frame == 1201,
            "L5: the next frame always samples again, so a cursor that entered or left the board is seen at once");
        check(!cache.Claim(1201), "L5: including on the frame the pointer entered or left the board, where only one answer exists");
        // A page that lost its sample (a hide, a new page) must sample again even in the same frame.
        cache.Forget();
        check(!cache.Sampled(1201) && cache.Claim(1201) && cache.Samples == 3,
            "L5: a forgotten sample is retaken in the same frame, which is what a hide or a new page asks for");
        // A frame number that repeats after a reset is not mistaken for a cached one.
        var zero = new ResultPointerFrameCache();
        check(zero.Claim(0) && !zero.Claim(0), "L5: frame 0 is a real frame, not the \"no sample\" state");

        string root = SourceRoot();
        if (root == null) { check(false, "L5: the round's scenarios locate the production sources"); return; }
        string display = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        string ensure = MethodBody(display, "private static void EnsurePointer()");
        check(ensure.Contains("PointerFrame.Claim(frame)") && ensure.Contains("_pointer = SamplePagePointer();"),
            "L5: the frame's sample is claimed once and taken once");
        string poll = MethodBody(display, "internal static void PollWheel()");
        int sample = poll.IndexOf("EnsurePointer();", StringComparison.Ordinal);
        int gate = poll.IndexOf("WheelApplies(out bool succeeded", StringComparison.Ordinal);
        check(sample > 0 && gate > sample,
            "L5: the wheel takes the frame's sample before it judges the pointer against the plate, so it can never read the previous frame's cursor");
        string pointer = MethodBody(display, "private static bool ChromePointer(out float x, out float y)");
        check(pointer.Contains("EnsurePointer();"),
            "L5: and the repaint and the press dispatch read that same sample instead of sampling again");
        check(display.Contains("PointerFrame.Forget();"),
            "L5: a page that loses its pointer sample forgets the frame with it, so the next ask samples again");
        string panel = File.ReadAllText(Path.Combine(root, "PanelBehaviour.cs"));
        check(panel.Contains("ResultsDisplay.PollWheel();") && display.Contains("PollWheel();"),
            "L5: both entries - the frame driver and the page's press dispatch - go through the same gate");
    }

    // ---- helpers ----------------------------------------------------------------------------------------
    private static FieldInfo[] RecordMembers()
    {
        Type type = typeof(ResultVanillaHold);
        return type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) <= 0.001f;

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

    private static string SourceRoot()
    {
        try
        {
            var folder = new DirectoryInfo(AppContext.BaseDirectory);
            for (int step = 0; step < 8 && folder != null; step++)
            {
                string candidate = Path.Combine(folder.FullName, "ResultsDisplay.cs");
                if (File.Exists(candidate)) return folder.FullName;
                folder = folder.Parent;
            }
        }
        catch { }
        return null;
    }
}
