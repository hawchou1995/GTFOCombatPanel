using System;
using System.IO;
using HawChow.GTFOCombatPanel;

// 1.19.38: the four `[DIAG]` shapes the Survival profile's log still carried.
//
// The evidence for this file is one real machine's log (36635 lines, GTFOCombatPanel 1.19.37, profile
// `Survival`, client role): revive warnings at 10589/13813/22539, mine refusals at 5678/5679/6079/6081 and
// 30198/30200, the supply refusal at 4521/19413/21819, and the five `draw board not drawn:
// reason=no-live-page, role=client` lines at 2023/2762/10755/19552/36529. What each of them turned out to be
// is written down in `reports/survival-11938-log-findings.md`; this file pins the parts of that answer a
// headless suite can actually hold down.
//
// Three of the four are decided by production types driven in memory here (`ReviveAttributionLedger`,
// `ToolImpactLedger`, `SupplyUse` over `ImpactCounters`). The fourth is a diagnostic-only change inside a
// Unity file this project cannot compile, so its two assertions are *static source checks* and say so in
// their own descriptions - they are not runtime proofs and do not pretend to be.
internal static partial class Program
{
    private static void RunRelease11938SurvivalLogScenarios()
    {
        Revive();
        Mines();
        Supply();
        DisplayWording();
        Console.WriteLine("WIRING: RELEASE_11938_SURVIVAL_LOG_FINDINGS (pagePresent/pageActive are static source checks; not a runtime behaviour test)");
    }

    // ---- 1: the revive whose reviver this machine never recorded ------------------------------------
    //
    // A completion with no replicated reviver state must credit nobody. That is not a missing feature: on a
    // client `ShouldTrack` is `isInSlot && (isMaster || isLocal)` (`ReviveAttributionLedger.CanTrackOwner`), so a
    // remote reviver can never reach this ledger at all, and the reviver's row belongs to the host's snapshot.
    // The game offers no local read to fall back on (see the report), so the refusal is the whole answer.
    private static void Revive()
    {
        // R1: a completion nobody preceded is refused, and no reviver is invented.
        var cold = new ReviveAttributionLedger();
        bool coldTaken = cold.TryTakeSuccessfulRevive(0x51UL, 10.0, out ulong coldReviver);
        Check(!coldTaken && coldReviver == 0UL,
            "R1: a successful revive completion with no observed reviver state credits nobody - TryTakeSuccessfulRevive answers false and names no player (the recorded lines 10589/13813/22539)");
        Check(cold.Count == 0,
            "R2: and the refusal leaves the ledger empty, so nothing is remembered to be spent on the next completion of the same target");

        // R3: the shape this client really produced - an in-slot remote player is not trackable there.
        Check(!ReviveAttributionLedger.CanTrackOwner(true, false, false),
            "R3: on a client (isMaster=false) an in-slot player who is not local is not trackable, which is why no remote reviver ever entered the ledger - `revive state observed:` printed 15 times in the recorded run and every one of them named the local player as the reviver");
        Check(ReviveAttributionLedger.CanTrackOwner(true, true, false) && ReviveAttributionLedger.CanTrackOwner(true, false, true),
            "R4: the host may track any seat and a local player may track itself, so the gate is `isInSlot && (isMaster || isLocal)` and not a blanket refusal");
        Check(!ReviveAttributionLedger.CanTrackOwner(false, true, true),
            "R5: a player with no seat is never tracked, host or not");

        // R6: what the gate does allow - a host observing a remote reviver - is credited once, from the state.
        var host = new ReviveAttributionLedger();
        bool observed = host.Observe(0x61UL, 0x62UL, true, 100.0);
        bool hostTaken = host.TryTakeSuccessfulRevive(0x61UL, 110.0, out ulong hostReviver);
        Check(observed && hostTaken && hostReviver == 0x62UL,
            "R6: a reviver state this machine did observe is credited to exactly that player when the completion arrives inside ActiveTimeoutSeconds (20s)");
        bool again = host.TryTakeSuccessfulRevive(0x61UL, 110.5, out ulong againReviver);
        Check(!again && againReviver == 0UL,
            "R7: and the same completion cannot be credited twice - the candidate is consumed by the first take");

        // R8: the release edge extends the window by ReleaseGraceSeconds and never by more.
        var release = new ReviveAttributionLedger();
        release.Observe(0x71UL, 0x72UL, true, 100.0);
        release.Observe(0x71UL, 0x72UL, false, 119.0);
        bool inGrace = release.TryTakeSuccessfulRevive(0x71UL, 121.0, out ulong graceReviver);
        Check(inGrace && graceReviver == 0x72UL,
            "R8: a released state (active=False) pushes the expiry to ReleaseGraceSeconds past the release, so a completion one second after the release is still the observed reviver");

        var stale = new ReviveAttributionLedger();
        stale.Observe(0x81UL, 0x82UL, true, 100.0);
        bool staleTaken = stale.TryTakeSuccessfulRevive(0x81UL, 124.0, out ulong staleReviver);
        Check(!staleTaken && staleReviver == 0UL,
            "R9: a candidate that expired (20s active + 3s grace) is refused rather than reused, so a late completion cannot inherit an old reviver");

        // R10: identities that can never be a rescue are refused by the ledger itself.
        var identities = new ReviveAttributionLedger();
        Check(!identities.Observe(0UL, 0x91UL, true, 1.0)
              && !identities.Observe(0x91UL, 0UL, true, 1.0)
              && !identities.Observe(0x91UL, 0x91UL, true, 1.0),
            "R10: a zero target, a zero reviver and a player reviving themselves are all refused instead of stored as a candidate");

        // R11/R12: the one credit path and the wording that names who owns the row (static source checks).
        string root = SurvivalSourceRoot();
        Check(root != null, "R11: the 1.19.38 survival-log suite locates the production sources");
        if (root == null) return;
        string tracker = File.ReadAllText(Path.Combine(root, "CombatTracker.cs"));
        Check(CountOf(tracker, "CreditRevive()") == 1
              && tracker.Contains("not assigning the credit to a guessed player"),
            "R12: combat revive credit has exactly one call site and the unmatched completion still says it will not name a guessed player (static source check; not a runtime proof)");
        Check(tracker.Contains("rowOwner=") && tracker.Contains("host-snapshot") && tracker.Contains("reviverReadableLocally=false"),
            "R13: the unmatched line now says which machine owns the row and that no local read exists, so a client's expected gap is not read as a defect (static source check; not a runtime proof)");
    }

    // ---- 2: the mine that was attributed and not counted --------------------------------------------
    //
    // The recorded pair is `toolCategory=9; coreCategory=13`. Category 9 is the player's *class tool*, read only
    // to answer "is the carried tool the deployer?"; 13 is the mine's own category. The very same mine's
    // `MineDeployerFirstPerson.PlaceMine` line in that frame says `consumable=True`, i.e. it was a mine carried
    // in from the level and placed - which the three-branch rule (ToolStatsTracker.cs:344-351) never counts and
    // always attributes. So the answer is "by design", and it is pinned here.
    private static void Mines()
    {
        Check(!ToolImpactLedger.MineBelongsToSlot(9u, 13u),
            "M1: the recorded pair (slot tool category 9, mine category 13) is not the carried deployer, so MineBelongsToSlot answers false - that is the branch that prints `attributed but not counted`");
        Check(ToolImpactLedger.MineBelongsToSlot(13u, 13u),
            "M2: the tool a deployer really is (same category on both sides) belongs to the slot, which is the only shape that counts as a deployment");
        Check(ToolImpactLedger.MineBelongsToSlot(0u, 13u) && !ToolImpactLedger.MineBelongsToSlot(13u, 0u),
            "M3: an unreadable slot category is accepted (a bot exposes no first-person face) while a mine with no category of its own is never attributed");

        // The production three-branch outcome for the recorded shape: attributed to the placer, not counted.
        var ledger = new ToolImpactLedger();
        var mine = new IntPtr(0x1A00);
        var core = new IntPtr(0x1A00);
        // A synthetic lookup on purpose: the real SteamID64 from the field log is never written into a test.
        const ulong owner = 0x4E00000000000001UL;
        bool counted = ledger.TryDeployMine(mine, core, owner, true);
        bool attributed = ledger.TryGetMineOwner(core, out ulong attributedOwner);
        Check(!counted && attributed && attributedOwner == owner,
            "M4: a carried-in consumable mine (isConsumable=true) is attributed to the player who placed it and still returns false - the blast keeps its row, the deployment column does not grow");
        Check(!ledger.IsCountedMine(core) && ledger.DeployedMineCount == 0,
            "M5: and the counted set really is empty for it, so the deployment and trigger columns cannot be inflated by a level mine");
        Check(!ledger.TryDeployMine(mine, core, owner, true),
            "M6: the second registration of the same mine (spawn callback + OnSpawn both fire) answers false again without taking the attribution back");

        var toolMine = new IntPtr(0x1B00);
        var toolCore = new IntPtr(0x1B10);
        bool toolCounted = ledger.TryDeployMine(toolMine, toolCore, owner, false);
        Check(toolCounted && ledger.IsCountedMine(toolCore) && ledger.DeployedMineCount == 1,
            "M7: the deployer's own mine (isConsumable=false) is counted, and the core pointer resolves to it - the other side of the same three-branch rule");

        string root = SurvivalSourceRoot();
        if (root == null) return;
        string tools = File.ReadAllText(Path.Combine(root, "ToolStatsTracker.cs"));
        Check(tools.Contains("consumable={ToolImpactLedger.Name(evidence.Consumption)}")
              && tools.Contains("carriedIn={(evidence.CarriedIn ? \"true\" : \"unknown\")}")
              && !tools.Contains("carriedIn=true"),
            "M8: the refusal line prints `consumable=` as a tri-state and takes `carriedIn=` from the evidence, with no hard-coded `carriedIn=true` left in the file (static source check; not a runtime proof)");

        // M9-M11: the claims the line makes come from the same pure judgement as the count, so no reader can see a
        // line that calls a mine "carried in" while saying its face was never read - or said "not consumable".
        ToolImpactLedger.MineInstanceEvidence consumedMine = ToolImpactLedger.DescribeInstance(true, true, 9u, 13u);
        Check(consumedMine.Consumption == ToolImpactLedger.MineConsumableReading.Consumable
              && !consumedMine.Counted && consumedMine.CarriedIn && !consumedMine.SlotToolMatchesMine
              && ToolImpactLedger.Name(consumedMine.Consumption) == "true",
            "M9: the recorded shape (face readable and consumable, slot category 9 against mine 13) answers consumable=true and carriedIn=true - the one combination that proves a mine came in from the level - and still does not count it");

        ToolImpactLedger.MineInstanceEvidence botMine = ToolImpactLedger.DescribeInstance(false, false, 13u, 13u);
        Check(botMine.Consumption == ToolImpactLedger.MineConsumableReading.Unreadable && botMine.Counted
              && !botMine.CarriedIn && ToolImpactLedger.Name(botMine.Consumption) == "unreadable",
            "M10: a tool slot with no readable face (a bot) answers consumable=unreadable and carriedIn=unknown while still counting the deployment, so `carriedIn` is never asserted out of an absence");

        ToolImpactLedger.MineInstanceEvidence mismatchedMine = ToolImpactLedger.DescribeInstance(true, false, 9u, 13u);
        Check(mismatchedMine.Consumption == ToolImpactLedger.MineConsumableReading.Equipment && !mismatchedMine.Counted
              && !mismatchedMine.CarriedIn && ToolImpactLedger.Name(mismatchedMine.Consumption) == "false",
            "M11: a readable non-consumable face whose category does not match the mine answers consumable=false, counted=false and carriedIn=unknown - the shape where the old hard-coded `carriedIn=true` contradicted its own `firstPerson=True`");
    }

    // ---- 3: the supply use that was not measured completely ------------------------------------------
    //
    // The sentence stays a refusal - no efficiency is invented - but it now names the half that is missing.
    // Four different failures produced it in the recorded run and all four read identically.
    private static void Supply()
    {
        // S1: the before-read threw (`CaptureAmmo`'s catch path is AddChange(NaN, 0, intended)).
        var noBefore = new SupplyUse();
        noBefore.AddChange(double.NaN, 0.0, 10.0);
        var noBeforeCounters = new ImpactCounters();
        bool noBeforeRecorded = noBefore.Record(noBeforeCounters);
        Check(!noBeforeRecorded && noBefore.UnmeasuredFields == "before",
            "S1: a use whose before-read failed says `missing=before` (the real 4521/19413/21819 lines carried no such field)");
        Check(noBeforeCounters.SupplyUses == 0 && noBeforeCounters.SupplyEfficiency == 0.0,
            "S2: and it produces no efficiency at all - the row is left exactly as it was, which is the point of refusing");

        // S3: the after-read went backwards or failed.
        var noAfter = new SupplyUse();
        noAfter.AddChange(10.0, 5.0, 10.0);
        var noAfterCounters = new ImpactCounters();
        Check(!noAfter.Record(noAfterCounters) && noAfter.UnmeasuredFields == "after"
              && noAfterCounters.SupplyUses == 0 && noAfterCounters.SupplyEfficiency == 0.0,
            "S3: a use whose after-read came back below the before says `missing=after` and credits nothing");

        // S4: the intended share never arrived.
        var noIntended = new SupplyUse();
        noIntended.AddChange(10.0, 12.0, 0.0);
        var noIntendedCounters = new ImpactCounters();
        Check(!noIntended.Record(noIntendedCounters) && noIntended.UnmeasuredFields == "intended"
              && noIntendedCounters.SupplyUses == 0,
            "S4: a use with no intended share says `missing=intended` and credits nothing");

        // S5: the scope closed without one applied change - a different fact from a failed read.
        var nothingApplied = new SupplyUse();
        var nothingCounters = new ImpactCounters();
        Check(!nothingApplied.Record(nothingCounters) && nothingApplied.UnmeasuredFields == "applied"
              && nothingApplied.Changes == 0 && nothingCounters.SupplyUses == 0,
            "S5: a use that never received an applied change says `missing=applied`, which is not the same fact as a read that failed");

        // S6: two gaps in one change are both named, in one fixed order.
        var both = new SupplyUse();
        both.AddChange(10.0, 5.0, 0.0);
        Check(both.UnmeasuredFields == "after+intended",
            "S6: a change that lost its after-read and its share names both in a fixed order, so two logs compare field by field");

        // S7: a complete use is still measured and still averaged exactly as before.
        var good = new SupplyUse();
        good.AddChange(10.0, 20.0, 10.0);
        var goodCounters = new ImpactCounters();
        bool goodRecorded = good.Record(goodCounters);
        Check(goodRecorded && good.UnmeasuredFields == "none" && good.Changes == 1,
            "S7: a use that was measured completely records as it always did and reports `missing=none`");
        Check(Math.Abs(goodCounters.SupplyEfficiency - 1.0) < 0.0001 && goodCounters.SupplyUses == 1,
            "S8: its efficiency is the ratio the game supplied (10 of 10 delivered = 1.0) - the fix names gaps, it never alters a measured figure");

        // S9: the diagnostic line is where the field is printed.
        string root = SurvivalSourceRoot();
        if (root == null) return;
        string tracker = File.ReadAllText(Path.Combine(root, "SupplyTracker.cs"));
        Check(tracker.Contains("\" supply use was not measured completely; missing=\" + use.UnmeasuredFields"),
            "S9: the refusal line prints `missing=` from SupplyUse.UnmeasuredFields, so the field and the words cannot drift apart (static source check; not a runtime proof)");
        Check(tracker.Contains("no efficiency was fabricated"),
            "S10: and the refusal still says out loud that no efficiency was fabricated - not inventing a number is the design, not the bug (static source check; not a runtime proof)");
    }

    // ---- 4: the client frame where the result state had arrived but the page was not active ----------
    //
    // Five lines in 36635, once per page, from the draw path's own early return. Nothing geometric was
    // refused, so nothing here changes what is drawn: the two fields only say which of the two facts it was.
    private static void DisplayWording()
    {
        string root = SurvivalSourceRoot();
        if (root == null) return;
        string results = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        Check(results.Contains("+ \",pagePresent=\" + (_page != null) + \",pageActive=\" + PageActive(_page)")
              && results.Contains("ReportHeldBack(\"draw\", _page == null || !PageActive(_page) ? \"no-live-page\" : \"no-placement\""),
            "D1: the draw early-exit line carries `pagePresent=` and `pageActive=` beside the reason it already printed (static check, not a runtime proof)");
        Check(results.Contains("+ \",pagePresent=False,pageActive=False\");"),
            "D2: the refresh early exit - the branch only reached when no page object was live at all - says so in both fields (static check, not a runtime proof)");
        Check(results.Contains("the vanilla result page is left exactly as the game drew it and the page is measured again"),
            "D3: the held-back line still promises only what it did before, so the wording change adds a fact instead of a claim (static check, not a runtime proof)");
    }

    private static int CountOf(string text, string needle)
    {
        int count = 0, at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
        return count;
    }

    private static string SurvivalSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj"))) directory = directory.Parent;
        return directory != null ? directory.FullName : null;
    }
}
