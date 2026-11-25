using System;
using System.IO;
using HawChow.GTFOCombatPanel;

// 1.19.57 - the five point fixes an independent review of the 1.19.56 build found, each asserted where it lives:
//
//   A(1)  the decoration-block identity: a page's native field names a *component*, an ancestor walk arrives with that
//         node's `Transform`, and those are different native ids. 1.19.56 compared them directly, which could never be
//         true, so no node was ever "inside" a decoration block. The rule is now `ResultNodeIdentity.Same` (transform,
//         then GameObject) and the block set is decided by the player's three switches (`ResultVanillaBlockPolicy`).
//   A(2)  the classifier's order: registration outranks the block root and a renderer; `Unknown` is a wall and is never
//         `WholeBlock`; an unreadable `m_allItems` answers "registered"; and a mixed root gives up only branches that are
//         provably inside the page's own block, carry no action and really render.
//   D     the standing early board is kept across the retries of its own page and replaced once, when the measurement
//         really places the mature board.
//
// Everything that can be answered by a production policy is answered by that policy. The Unity side - which object is
// which - cannot be instantiated headlessly, so the wiring is read from the production source, as this suite does
// everywhere else.
internal static class Release11957Scenarios
{
    internal static void RunInto(Action<bool, string> check)
    {
        TheBlockIdentityIsTheNodeNotTheComponent(check);
        TheSwitchesDecideWhichBlocksAreDecorations(check);
        TheClassifierAnswersInTheOrderThePolicyNeeds(check);
        ThePlanNeverPutsAwayWhatItCannotDescribe(check);
        AnUnreadableRegistryIsNotAnUnregisteredEntry(check);
        TheMixedRootGivesUpOnlyProvenVisuals(check);
        TheEarlyBoardIsKeptOnItsOwnPage(check);
    }

    // ---------- A(1): which node is which ---------------------------------------------------------------

    private static void TheBlockIdentityIsTheNodeNotTheComponent(Action<bool, string> check)
    {
        // Three wrappers of ONE node carry three distinct native ids: the component the page's field names
        // (`m_artifactInfo_text` is a text, `m_ArtifactInventoryDisplay` a display), the `Transform` the ancestor walk
        // arrives with, and the `GameObject` both of them belong to.
        IntPtr component = new IntPtr(0x100), transform = new IntPtr(0x200), gameObject = new IntPtr(0x300);
        check(component != transform && transform != gameObject && component != gameObject,
            "a component, its transform and its object are three different native ids - the three 1.19.56 compared");
        IntPtr otherTransform = new IntPtr(0x500), otherObject = new IntPtr(0x600);
        check(!ResultNodeIdentity.Same(otherTransform, otherObject, component, gameObject),
            "a node is never identified by the component id of a sibling: that comparison is the 1.19.56 bug");
        check(ResultNodeIdentity.Same(transform, gameObject, transform, gameObject),
            "the same node matches on its transform");
        check(ResultNodeIdentity.Same(transform, gameObject, transform, IntPtr.Zero),
            "and the transform alone is enough - the walk does not have to read the object");
        check(ResultNodeIdentity.Same(component, gameObject, otherTransform, gameObject),
            "a field naming a component matches the walk's transform when both are on the same object");
        check(!ResultNodeIdentity.Same(IntPtr.Zero, IntPtr.Zero, transform, gameObject)
              && !ResultNodeIdentity.Same(transform, gameObject, IntPtr.Zero, gameObject),
            "an unreadable side never matches, so an unreadable block stays outside every decoration block");

        string display = ProductionText("ResultsDisplay.cs");
        string same = MethodBody(display, "private static bool SameNode(Component block, Transform node)");
        check(same.Contains("ResultNodeIdentity.Same(blockTransform, blockObject, nodeTransform, nodeObject)"),
            "the hide path asks that one rule instead of comparing a component pointer with an ancestor's");
        string decoration = MethodBody(display, "private static bool IsDecorationBlock(Transform node, Component page, bool succeeded)");
        check(decoration.Contains("SameNode(success.m_artifactInfo_text, node)")
              && decoration.Contains("SameNode(fail.m_ArtifactInventoryDisplay, node)"),
            "every block is named by its own native field and compared with the node the walk holds");
        check(!decoration.Contains("TryCast<Component>()"),
            "the 1.19.56 cast that turned the field into a different native object is gone from the block identity");
        string inside = MethodBody(display, "private static bool InsideDecorationBlock(Component node, Component page, bool succeeded, out bool containerRoot)");
        check(inside.Contains("IsDecorationBlock(transform, page, succeeded)"),
            "the walk asks the live page, not the `_page`/`_succeeded` cache `ResetPlacement` clears");
        check(!inside.Contains("_page") && !inside.Contains("_succeeded"),
            "and it holds no reference to that cache at all");
        check(ProductionText("ResultsDisplay.cs").Contains("Component vanillaPage = LivePage(out bool vanillaSucceeded);")
              && ProductionText("ResultsDisplay.cs").Contains("if (vanillaPage != null) ApplyVanillaBlocks(vanillaPage, vanillaSucceeded);"),
            "the draw path hands the hide the page it really has (`LivePage`), so a cleared cache cannot hide nothing");
    }

    // ---------- A(1): the player's switches --------------------------------------------------------------

    private static void TheSwitchesDecideWhichBlocksAreDecorations(Action<bool, string> check)
    {
        check(ResultVanillaBlockPolicy.IsDecoration(true, true, false, false, VanillaBlockKind.ArtifactInventory)
              && ResultVanillaBlockPolicy.IsDecoration(true, true, false, false, VanillaBlockKind.ArtifactSummary)
              && ResultVanillaBlockPolicy.IsDecoration(true, true, false, false, VanillaBlockKind.CheckpointUsage),
            "on the success page, `HideVanillaArtifacts` makes the artifact block, its summary and the checkpoint line decorations");
        check(!ResultVanillaBlockPolicy.IsDecoration(true, false, false, false, VanillaBlockKind.ArtifactSummary),
            "with `ShowSuccessArtifacts` on, the summary the player asked to see is NOT a decoration - it keeps the 1.19.54 fail-safe");
        check(!ResultVanillaBlockPolicy.IsDecoration(true, false, true, true, VanillaBlockKind.ArtifactInventory),
            "and the reports switch never turns the artifact block into a decoration");
        check(ResultVanillaBlockPolicy.IsDecoration(true, false, true, false, VanillaBlockKind.PlayerReport)
              && ResultVanillaBlockPolicy.IsDecoration(true, false, true, false, VanillaBlockKind.PlayerReportAnchor),
            "`ShowSuccessPlayerReports` has its own switch for the report cards and their anchors");
        check(ResultVanillaBlockPolicy.IsDecoration(false, false, false, true, VanillaBlockKind.ArtifactInventory)
              && ResultVanillaBlockPolicy.IsDecoration(false, false, false, true, VanillaBlockKind.ArtifactSummary),
            "on the failure page `HideVanillaFailBlocks` is the whole question");
        check(!ResultVanillaBlockPolicy.IsDecoration(false, true, true, false, VanillaBlockKind.ArtifactInventory),
            "the failure page does not inherit the success page's two switches");
        check(!ResultVanillaBlockPolicy.IsDecoration(false, true, true, true, VanillaBlockKind.CheckpointUsage)
              && !ResultVanillaBlockPolicy.IsDecoration(false, true, true, true, VanillaBlockKind.PlayerReport),
            "and a block the failure page does not have is never a decoration there");
    }

    // ---------- A(2): the order the classifier answers in ------------------------------------------------

    private static void TheClassifierAnswersInTheOrderThePolicyNeeds(Action<bool, string> check)
    {
        // 1.19.59: inside one of the page's own named blocks, registration is *not* "the page dispatches to a playable
        // button". `m_allItems` is the list the page draws hover entries for, and the recorded pages hang registered
        // `CM_Item`s inside their own presentational blocks - the artifact summary's `i` and its `1.0` line are the ones the
        // player named. The page's real controls (`m_btnGoToLobby`, `m_btnRestartCheckpoint`, `m_btnLeaveExpedition`) live
        // outside these blocks, and a `Button` inside one is still an action wherever it is.
        // The block's *own* node keeps the old answer when the page registered it: that is a page control, not wallpaper.
        var rootRegisteredVisual = new VanillaNodeFacts(true, true, true, true, false, true);
        check(ResultVanillaClassify.Of(rootRegisteredVisual) == VanillaNodeKind.Action,
            "a registered container root inside a declared block is a control the page dispatches to, so it is left alone");
        var rootUnregistered = new VanillaNodeFacts(true, true, false, true, false, true);
        check(ResultVanillaClassify.Of(rootUnregistered) == VanillaNodeKind.Visual,
            "while an unregistered root is the plate the block is drawn on: hideable and never a wall");
        var visualRegistered = new VanillaNodeFacts(true, false, true, true, false, true);
        check(ResultVanillaClassify.Of(visualRegistered) == VanillaNodeKind.Visual,
            "and so is a registered visual-only entry: the block's internal registration is not a button the player presses");
        var registeredButton = new VanillaNodeFacts(true, false, true, true, true, true);
        check(ResultVanillaClassify.Of(registeredButton) == VanillaNodeKind.Action,
            "while a registered entry that really carries a `Button` is an action, inside the block or out");
        var rootOnly = new VanillaNodeFacts(true, true, false, true, false, false);
        check(ResultVanillaClassify.Of(rootOnly) == VanillaNodeKind.Visual,
            "an unregistered block root is the page's own drawing, hideable and never a wall");
        var plainInside = new VanillaNodeFacts(true, false, false, true, false, false);
        check(ResultVanillaClassify.Of(plainInside) == VanillaNodeKind.Unknown,
            "inside a block, an entry that neither is the root nor renders anything is Unknown: a wall, never hidden");
        var buttonBelow = new VanillaNodeFacts(true, true, false, true, true, true);
        check(ResultVanillaClassify.Of(buttonBelow) == VanillaNodeKind.Action,
            "a `Button` anywhere in the subtree is an action, whatever else the node is");
        var outsideActive = new VanillaNodeFacts(false, false, false, true, false, true);
        check(ResultVanillaClassify.Of(outsideActive) == VanillaNodeKind.Action,
            "outside the decoration blocks the 1.19.54 fail-safe is unchanged: an active entry is a wall");
        var outsideIdle = new VanillaNodeFacts(false, false, false, false, false, false);
        check(ResultVanillaClassify.Of(outsideIdle) == VanillaNodeKind.Unknown,
            "and an entry nobody can describe stays one");
        check(ResultVanillaClassify.Hideable(ResultVanillaClassify.Of(rootOnly))
              && !ResultVanillaClassify.Hideable(ResultVanillaClassify.Of(plainInside))
              && ResultVanillaClassify.IsWall(ResultVanillaClassify.Of(plainInside)),
            "only a proven visual is hideable, and every unknown entry is a wall");
    }

    private static void ThePlanNeverPutsAwayWhatItCannotDescribe(Action<bool, string> check)
    {
        check(ResultVanillaClassify.PlanForBlock(VanillaNodeKind.Unknown, 0, 0, 0) == VanillaHidePlan.Nothing
              && ResultVanillaClassify.PlanForBlock(VanillaNodeKind.Unknown, 4, 2, 2) == VanillaHidePlan.Nothing,
            "a root nobody can describe is never put away whole - 1.19.56 answered WholeBlock for exactly this block");
        check(ResultVanillaClassify.PlanForBlock(VanillaNodeKind.Action, 0, 2, 0) == VanillaHidePlan.Nothing,
            "a root that is a control is left where the game put it");
        check(ResultVanillaClassify.PlanForBlock(VanillaNodeKind.Visual, 0, 0, 0) == VanillaHidePlan.WholeBlock,
            "a block with no action anywhere is the page's own drawing and is put away as it always was");
        check(ResultVanillaClassify.PlanForBlock(VanillaNodeKind.Visual, 3, 2, 2) == VanillaHidePlan.SafeBranches,
            "a mixed root gives up only its proven visuals - a `Button` in one branch no longer makes the whole block Nothing");
        check(ResultVanillaClassify.PlanForBlock(VanillaNodeKind.Visual, 3, 0, 1) == VanillaHidePlan.VisualOnly,
            "and with no proven visual there is nothing that may be moved - the drawing is switched off instead (1.19.59)");

        string display = ProductionText("ResultsDisplay.cs");
        string plan = MethodBody(display, "private static VanillaHidePlan PlanOfBlock(Component block, Component page, bool succeeded)");
        check(plan.Contains("VanillaNodeKind root = RootKindOf(block, page, succeeded);"),
            "the production plan asks the block's own kind, not its subtree's");
        string root = MethodBody(display, "private static VanillaNodeKind RootKindOf(Component block, Component page, bool succeeded)");
        check(root.Contains("bool button = OwnButton(block);") && !root.Contains("HasButton(block)"),
            "and that kind is the block's own `Button`, so a mixed root is not read as one control");
        check(plan.Contains("else actions++;"),
            "an entry nobody can describe is counted as an action, never as a part a mixed block may put away");
    }

    // ---------- A(2): the registry --------------------------------------------------------------

    private static void AnUnreadableRegistryIsNotAnUnregisteredEntry(Action<bool, string> check)
    {
        string display = ProductionText("ResultsDisplay.cs");
        string registered = MethodBody(display, "private static bool RegisteredInPage(CM_Item item, Component page)");
        check(registered.Contains("var registered = menu != null ? menu.m_allItems : null;"),
            "the registry is the page's own `m_allItems`");
        check(registered.Contains("if (registered == null) return true;"),
            "an unreadable registry answers \"registered\": 1.19.56 answered false here, which turned every entry into a decoration");
        check(!registered.Contains("if (registered == null) return false;"),
            "and the 1.19.56 answer is gone");
        check(registered.Contains("catch { return true; }"),
            "a read that throws is protected the same way");
        check(registered.Contains("if (item == null) return true;"),
            "a missing entry is protected as well");
    }

    // ---------- A(2): the branch-by-branch hide ---------------------------------------------------------

    private static void TheMixedRootGivesUpOnlyProvenVisuals(Action<bool, string> check)
    {
        string display = ProductionText("ResultsDisplay.cs");
        string branches = MethodBody(display, "private static bool HideSafeBranches(Component block, string what, Component page, bool succeeded)");
        check(branches.Length > 0, "`HideSafeBranches` is where the test found it");
        int shared = branches.IndexOf("if (SharedOrRoot(node)) continue;", StringComparison.Ordinal);
        int action = branches.IndexOf("if (CarriesAction(node, page, succeeded)) continue;", StringComparison.Ordinal);
        int live = branches.IndexOf("if (LiveActionIn(node, page)) continue;", StringComparison.Ordinal);
        int insideBlock = branches.IndexOf("if (!InsideDecorationBlock(node, page, succeeded, out _)) continue;", StringComparison.Ordinal);
        int visual = branches.IndexOf("if (!ProvenVisual(node, page, succeeded)) continue;", StringComparison.Ordinal);
        int record = branches.IndexOf("Hidden.HoldMove(", StringComparison.Ordinal);
        int move = branches.IndexOf("branch.localPosition = new Vector3", StringComparison.Ordinal);
        int off = branches.IndexOf("host.SetActive(false);", move, StringComparison.Ordinal);
        check(shared >= 0 && action > shared && live > action && insideBlock > live && visual > insideBlock
              && record > visual && move > record && off > move,
            "a branch is refused - before anything is recorded or moved - when it is the page's own root, when its own node"
                + " is an action, when anything below it can be dispatched to (`LiveActionIn`), when it is not proven to be"
                + " inside the page's declared block, or when it renders nothing");
        check(branches.Contains("if (LiveActionIn(node, page)) continue;") && !branches.Contains("if (LiveActionIn(node)) continue;"),
            "the registered-descendant guard is the production call with the live page, not a cache-reading shortcut");
        // 1.19.59: the two cases the guard exists for, answered by the production classifier. A decorative branch whose own
        // node is a plate with a renderer but which holds a *registered* `CM_Item` below it is now the page's own drawing too
        // - registration inside a declared block is not a playable button - so that branch may move like any other proven
        // visual. What `LiveActionIn` still refuses, and must always refuse, is a subtree it cannot read: an unreadable
        // registry answers "registered", so a branch this build cannot account for can only ever stay where the game put it.
        var registeredGrandchild = new VanillaNodeFacts(true, false, true, true, false, true);
        check(ResultVanillaClassify.Of(registeredGrandchild) == VanillaNodeKind.Visual
              && ResultVanillaClassify.Hideable(ResultVanillaClassify.Of(registeredGrandchild)),
            "a registered `CM_Item` with a renderer inside a declared block is the block's own drawing: the branch above it may be"
                + " put away, which is what stopped the red-crystal summary from being hidden (1.19.59)");
        var visualSibling = new VanillaNodeFacts(true, false, false, true, false, true);
        check(ResultVanillaClassify.Of(visualSibling) == VanillaNodeKind.Visual
              && ResultVanillaClassify.Hideable(ResultVanillaClassify.Of(visualSibling)),
            "and the plain visual sibling - unregistered, no Button - is the branch that may move");
        var unreadable = new VanillaNodeFacts(true, false, false, true, false, false);
        check(ResultVanillaClassify.Of(unreadable) == VanillaNodeKind.Unknown
              && ResultVanillaClassify.IsWall(ResultVanillaClassify.Of(unreadable)),
            "while an entry this build cannot describe stays a wall and is never hidden");
        check(ResultVanillaHidePolicy.IsLiveAction(false, true) && !ResultVanillaHidePolicy.IsLiveAction(false, false),
            "`LiveActionIn`'s own question is registration: a switched-off but registered entry is still live");
        string liveBody = MethodBody(display, "private static bool LiveActionIn(Component node, Component page)");
        check(liveBody.Contains("RegisteredInPage(items[index], page)") && liveBody.Contains("catch { return true; }"),
            "and it answers true when the subtree or the registry cannot be read, so an unreadable read can only ever leave"
                + " a branch where the game put it");
        check(branches.Contains("host != null && host.activeSelf") && branches.Contains("Vector3 at = branch.localPosition;"),
            "and its own position and active state are read at the first move - not at the first touch of any kind - so `RestoreHidden` puts it back where the page drew it");
        string hide = MethodBody(display, "private static void Hide(Component block, string what, Component page, bool succeeded, bool visualOnly = false)");
        check(hide.Contains("if (!visualOnly && plan == VanillaHidePlan.WholeBlock)")
              && hide.Contains("if (!visualOnly && plan == VanillaHidePlan.SafeBranches && HideSafeBranches(block, what, page, succeeded)) return;")
              && hide.Contains("SuppressVisual(block, what);"),
            "the whole-block move happens only for the plan that asked for it, a mixed block is answered branch by branch, and"
                + " a block the early frame may not move is switched off at the drawing instead (1.19.59)");
        // A button the page adds later must not have been carried away: only a branch with no `Button`, no registered entry
        // anywhere in it and no action of its own is ever moved, and the page's own root is refused outright.
        string proven = MethodBody(display, "private static bool ProvenVisual(Component node, Component page, bool succeeded)");
        check(proven.Contains("GetComponentsInChildren<Renderer>(true)") && proven.Contains("GetComponentsInChildren<CanvasRenderer>(true)"),
            "`ProvenVisual` asks the renderers the branch really has, never its name, its text or its position");
    }

    // ---------- D: the early board across its own page's retries -----------------------------------------

    private static void TheEarlyBoardIsKeptOnItsOwnPage(Action<bool, string> check)
    {
        check(ResultEarlyBoardPolicy.KeepsStanding(true, true, true, true, true, true, true),
            "an early board that is placed, live, on the same page and flavour, with a grid and content, is kept");
        check(!ResultEarlyBoardPolicy.KeepsStanding(false, true, true, true, true, true, true),
            "a mature board is not \"kept\": it goes through the ordinary reset");
        check(!ResultEarlyBoardPolicy.KeepsStanding(true, false, true, true, true, true, true),
            "nothing placed, nothing to keep");
        check(!ResultEarlyBoardPolicy.KeepsStanding(true, true, false, true, true, true, true),
            "another page is a different screen, so the kept grid would belong to the old one");
        check(!ResultEarlyBoardPolicy.KeepsStanding(true, true, true, false, true, true, true),
            "and the other flavour is a different screen too");
        check(!ResultEarlyBoardPolicy.KeepsStanding(true, true, true, true, false, true, true),
            "a page that is not live keeps nothing");
        check(!ResultEarlyBoardPolicy.KeepsStanding(true, true, true, true, true, false, true)
              && !ResultEarlyBoardPolicy.KeepsStanding(true, true, true, true, true, true, false),
            "and a board with no grid or no content has nothing to keep");

        string display = ProductionText("ResultsDisplay.cs");
        string refresh = MethodBody(display, "internal static void Refresh(eGameStateName state)");
        check(refresh.Length > 0, "`Refresh` is where the test found it");
        int ask = refresh.IndexOf("bool keepStanding = EarlyBoardStanding(succeeded, out IntPtr standingPage);", StringComparison.Ordinal);
        int guard = refresh.IndexOf("if (!keepStanding)", StringComparison.Ordinal);
        int reset = guard >= 0 ? refresh.IndexOf("ResetPlacement();", guard, StringComparison.Ordinal) : -1;
        int restore = reset >= 0 ? refresh.IndexOf("RestoreHidden();", reset, StringComparison.Ordinal) : -1;
        check(ask >= 0 && guard > ask && reset > guard && restore > reset,
            "the reset and the restore of the page's own blocks happen only when nothing is being kept");
        int flavour = refresh.IndexOf("if (keepStanding && page.Pointer != standingPage)", StringComparison.Ordinal);
        int dropReset = flavour >= 0 ? refresh.IndexOf("ResetPlacement();", flavour, StringComparison.Ordinal) : -1;
        check(flavour > restore && dropReset > flavour,
            "a board kept for one page is dropped the moment the measurement is about another page");
        int overlayOff = refresh.IndexOf("if (!overlay)", StringComparison.Ordinal);
        int offReset = overlayOff >= 0 ? refresh.IndexOf("ResetPlacement();", overlayOff, StringComparison.Ordinal) : -1;
        int offHit = overlayOff >= 0 ? refresh.IndexOf("ClearHit();", overlayOff, StringComparison.Ordinal) : -1;
        check(overlayOff >= 0 && offReset > overlayOff && offHit > offReset && overlayOff < ask,
            "the F8/switch-off branch still resets and clears the hit rectangle before any of this, so a board that was"
                + " switched off is not drawn again and none of its state survives");
        check(refresh.Contains("\"early-board-kept\"") && refresh.Contains("ResultReportPolicyStrict.OncePerPage"),
            "and the kept frame says so once per page instead of once per retry");
        string standing = MethodBody(display, "private static bool EarlyBoardStanding(bool succeeded, out IntPtr pagePointer)");
        check(standing.Contains("ResultVisibility.SamePage(_holdPage, current)")
              && standing.Contains("_placedSucceeded == succeeded")
              && standing.Contains("_grid != null, _content != null"),
            "`EarlyBoardStanding` asks the page's own objects: the placement's recorded page, the flavour, the grid and the content");
    }

    // ---------- helpers ---------------------------------------------------------------------------------

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
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj")))
            directory = directory.Parent;
        if (directory == null) throw new InvalidOperationException("the production source root was not found");
        return File.ReadAllText(Path.Combine(directory.FullName, name));
    }
}
