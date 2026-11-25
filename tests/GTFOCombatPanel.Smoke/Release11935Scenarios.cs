using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// 1.19.35 (review fixes): the four defects a code review found in the supply row, the settlement mirror and the blood-loss
// observation, each asserted against the production code rather than against a shape:
//
//   1. the supply cell is four fields in every branch, one per name on its label: a cell that may not be split keeps the
//      ammo part that really arrived and withdraws only the two kinds with `?` - the released "all packs" total is no
//      longer a field of its own, so there is no fifth one (1.19.36);
//   2. the released one-bit supply overload read `false` as a *tool* pack, so a medical pack counted as a tool pack. The
//      overload is gone and the kind is named at every call site;
//   3. the settlement mirror maxed an unsplit pair field by field, which could hand back a pair neither machine reported.
//      One side's pair is taken whole now, the larger total winning, and an ammo part can never exceed its own total;
//   4. the ledger counts the seats whose seeded pair never completed (the blow's postfix did not run) and reports them on a
//      bounded diagnostic line instead of guessing at their size.
internal static class Release11935Scenarios
{
    private const ulong Owner = 0xD1UL;
    private const ulong Mirrored = 0x53UL;

    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        AnUnsplitCellPrintsItsAmmoAndTwoUnknownKinds(check);
        AMedicalPackIsNeverAToolPack(check);
        TheSettlementMirrorTakesOneSideWhole(check);
        ASeededSeatWithoutItsObservationIsCountedNotGuessed(check);
        Wiring(check);
    }

    // ---- 1. the cell that cannot be split keeps its four fields and prints `?` for the two kinds -------------------------

    private static void AnUnsplitCellPrintsItsAmmoAndTwoUnknownKinds(Action<bool, string> check)
    {
        // The released client, and every row rebuilt from its v8 snapshot: it sent the total and the ammo part and nothing
        // else. 1.19.36: the cell carries four fields anyway - the three kind fields are what replaced the old total - so the
        // row keeps the ammo figure it really sent in the second field and withdraws the two kinds it cannot name.
        var old = new ImpactCounters { SelfSupplyUses = 2, SelfAmmoUses = 0, SelfSupplyKindsKnown = false };
        string cell = CombatStrings.SupplyCell(CombatStrings.Placeholder, old.SelfSupplyUses, old.SelfAmmoUses,
            old.SelfToolUses, old.SelfMedicalUses, old.SelfSupplyKindsKnown);
        string[] parts = cell.Split(new[] { CombatStrings.CellSeparator }, StringSplitOptions.None);
        check(parts.Length == 4 && parts[1] == "0",
            "an older client's row still prints the four fields its label names, with the ammo part it sent in the second: " + cell);
        check(parts[2] == CombatStrings.Unknown && parts[3] == CombatStrings.Unknown,
            "`?` stands in the two kind fields and nowhere else - never where a figure that arrived belongs: " + cell);

        // Five packs, two of them ammo, kinds unknown: the ammo figure is on the line and only the split is withdrawn.
        var partial = new ImpactCounters { SelfSupplyUses = 5, SelfAmmoUses = 2, SelfSupplyKindsKnown = false };
        string mixed = CombatStrings.SupplyCell("40.0%", partial.SelfSupplyUses, partial.SelfAmmoUses,
            partial.SelfToolUses, partial.SelfMedicalUses, partial.SelfSupplyKindsKnown);
        string[] mixedParts = mixed.Split(new[] { CombatStrings.CellSeparator }, StringSplitOptions.None);
        check(mixedParts.Length == 4 && mixedParts[1] == "2"
              && mixedParts[2] == CombatStrings.Unknown && mixedParts[3] == CombatStrings.Unknown,
            "a row that consumed 5 packs, 2 of them ammo, keeps a real 2 in the ammo field and withdraws only the split: " + mixed);

        // The split that does add up keeps the released four-field cell, character for character: this fix added no field.
        var known = new ImpactCounters();
        known.CreditSelfSupply(SupplyPackKind.Ammo);
        known.CreditSelfSupply(SupplyPackKind.Tool);
        known.CreditSelfSupply(SupplyPackKind.Medical);
        known.CreditSelfSupply(SupplyPackKind.Medical);
        check(known.SelfSupplyKindsKnown && known.SelfSupplyUses == 4
              && CombatStrings.SupplyCell("30.0%", known.SelfSupplyUses, known.SelfAmmoUses, known.SelfToolUses,
                     known.SelfMedicalUses, known.SelfSupplyKindsKnown) == "30.0% / 1 / 1 / 2",
            "a split that describes its total still prints the four fields the label names, in the released order");

        // One cell, one row: what the board prints is the cell above, and the board keeps its 14 fixed rows.
        var entry = new ImpactEntry(new ImpactInput { Name = "Old", Counters = old }, null, null, 40, 1,
            ImpactRating.Supply, (int)ImpactMetric.SupplyEfficiency);
        string[] fields = ImpactBoard.MetricFields(entry, ImpactMetric.SupplyEfficiency, KillTier.Minor, false, true);
        check(fields.Length == 1 && fields[0] == cell,
            "the total arrives inside the row's existing cell: no field was moved to a row of its own");
        var board = ImpactBoard.Build(ImpactScoring.Evaluate(new[] { entry.Input }, ImpactScoring.DefaultWeights, 1.0, 1.0), true, true);
        check(board.Rows == 30 && ImpactBoard.FixedRows == 14,
            "and the board still holds 14 fixed rows and 30 in all: this fix added none");
    }

    // ---- 2. a medical pack is never a tool pack -----------------------------------------------------------------------

    private static void AMedicalPackIsNeverAToolPack(Action<bool, string> check)
    {
        // A giver-sourced heal: the pack is consumed by the receiver, and it is a medical pack because that is the kind the
        // observer named - not because a bit meant "not ammo".
        var receiver = new ImpactCounters();
        var heal = new SupplyUse();
        heal.AddChange(40, 70, 30);
        check(heal.RecordConsumption(receiver, SupplyPackKind.Medical),
            "one heal, one consumed pack, and the kind is the one the caller named");
        check(receiver.SelfSupplyUses == 1 && receiver.SelfMedicalUses == 1
              && receiver.SelfToolUses == 0 && receiver.SelfAmmoUses == 0,
            "a medical pack lands in medical alone: total 1, medical 1, tool 0, ammo 0");
        check(receiver.SelfSupplyKindsKnown,
            "and the split still describes its total, which is what the row is allowed to print");
        string cell = CombatStrings.SupplyCell("50.0%", receiver.SelfSupplyUses, receiver.SelfAmmoUses,
            receiver.SelfToolUses, receiver.SelfMedicalUses, receiver.SelfSupplyKindsKnown);
        check(cell == "50.0% / 0 / 0 / 1",
            "so the cell reads ammo 0, tool 0, medical 1: a heal is never reported as a tool pack: " + cell);

        var tool = new ImpactCounters();
        tool.CreditSelfSupply(SupplyPackKind.Tool);
        check(tool.SelfToolUses == 1 && tool.SelfMedicalUses == 0 && tool.SelfAmmoUses == 0,
            "while a class-channel refill counts as the tool pack, and only as that");
    }

    // ---- 3. an unsplit pair moves as one block -----------------------------------------------------------------------

    private static void TheSettlementMirrorTakesOneSideWhole(Action<bool, string> check)
    {
        // (total 3, ammo 3) and (total 2, ammo 0) on two machines: the answer has to be one of those two pairs, never a
        // mixture of them - `(3, 0)` is a pair neither machine ever reported.
        var rows = new SettlementRows();
        var three = new CombatPlayer(Mirrored) { Name = "Three" };
        three.Impact.SelfSupplyUses = 3; three.Impact.SelfAmmoUses = 3;
        rows.Update(new[] { three }, 9);

        var two = new CombatPlayer(Mirrored) { Name = "Three" };
        two.Impact.SelfSupplyUses = 2; two.Impact.SelfAmmoUses = 0;
        CombatPlayer merged = rows.Update(new[] { two }, 9)[0];
        check(merged.Impact.SelfSupplyUses == 3 && merged.Impact.SelfAmmoUses == 3,
            "the pair with the larger total is taken whole: 3 / 3, not the (3, 0) neither machine reported");
        check(merged.Impact.SelfAmmoUses == 3 || merged.Impact.SelfAmmoUses == 0,
            "and it is one machine's pair, not one field from each: " + merged.Impact.SelfSupplyUses + "/" + merged.Impact.SelfAmmoUses);

        // The other direction: a stored pair smaller than the one that arrives.
        var again = new SettlementRows();
        var small = new CombatPlayer(Mirrored) { Name = "Three" };
        small.Impact.SelfSupplyUses = 2; small.Impact.SelfAmmoUses = 0;
        again.Update(new[] { small }, 9);
        var bigger = new CombatPlayer(Mirrored) { Name = "Three" };
        bigger.Impact.SelfSupplyUses = 3; bigger.Impact.SelfAmmoUses = 3;
        CombatPlayer grown = again.Update(new[] { bigger }, 9)[0];
        check(grown.Impact.SelfSupplyUses == 3 && grown.Impact.SelfAmmoUses == 3,
            "and a pair that grows while nobody split it is taken whole in that direction too");

        // The mixture the field-by-field max used to build: (3, 0) arriving over a stored (2, 2) became a (3, 2) nobody sent.
        var mixed = new SettlementRows();
        var stored = new CombatPlayer(Mirrored) { Name = "Mixed" };
        stored.Impact.SelfSupplyUses = 2; stored.Impact.SelfAmmoUses = 2;
        mixed.Update(new[] { stored }, 9);
        var arriving = new CombatPlayer(Mirrored) { Name = "Mixed" };
        arriving.Impact.SelfSupplyUses = 3; arriving.Impact.SelfAmmoUses = 0;
        CombatPlayer taken = mixed.Update(new[] { arriving }, 9)[0];
        check(taken.Impact.SelfSupplyUses == 3 && taken.Impact.SelfAmmoUses == 0,
            "a pair that arrives with a larger total replaces the stored one whole instead of being spliced into it: 3 / 0");
        check(taken.Impact.SelfAmmoUses <= taken.Impact.SelfSupplyUses,
            "the mirror never hands back a pair whose ammo part exceeds its own total");

        // A pair whose own ammo part is impossible cannot survive the mirror either.
        var broken = new SettlementRows();
        var corrupt = new CombatPlayer(Mirrored) { Name = "Corrupt" };
        corrupt.Impact.SelfSupplyUses = 1; corrupt.Impact.SelfAmmoUses = 3;
        broken.Update(new[] { corrupt }, 9);
        var later = new CombatPlayer(Mirrored) { Name = "Corrupt" };
        later.Impact.SelfSupplyUses = 1; later.Impact.SelfAmmoUses = 3;
        CombatPlayer cut = broken.Update(new[] { later }, 9)[0];
        check(cut.Impact.SelfAmmoUses <= cut.Impact.SelfSupplyUses,
            "and a pair whose ammo part was larger than its total is cut to the total the row prints: "
            + cut.Impact.SelfSupplyUses + "/" + cut.Impact.SelfAmmoUses);
    }

    // ---- 4. a seeded seat whose observation never came is counted, never guessed at ------------------------------------

    private static void ASeededSeatWithoutItsObservationIsCountedNotGuessed(Action<bool, string> check)
    {
        IntPtr seat = (IntPtr)0x9001;
        // The shape the review found: the pre-call reading is taken, the blow's own postfix never runs (the call threw, the
        // player was torn down with it), and the next observation is the revive that writes the figure back up. `Observe`
        // credits nothing on a rise - correctly - so that blow is not in `HealthLost`. What must not happen is that the loss
        // disappears without a trace, or that its size is invented from what the seat was seeded at.
        var ledger = new HealthLossLedger();
        check(ledger.Seed(seat, Owner, 40.0, 100.0), "the blow's pre-call reading plants the mark at the life it found");
        check(ledger.Observe(seat, Owner, 100.0, 100.0) == 0.0,
            "a revive that writes the figure back up credits nothing: a rise is never blood loss");
        check(ledger.UnsettledSeeds == 1,
            "while the seat itself is counted: a seeded pair whose observation never came is one seat, not one guess");
        check(ledger.Observe(seat, Owner, 90.0, 100.0) == 10.0 && ledger.UnsettledSeeds == 1,
            "and it is counted once: the next ordinary blow is credited normally and adds nothing to that count");

        // The control that matters: an ordinary pair - the seed and the postfix of the same call - is never counted, so the
        // diagnostic cannot drift into a running total of every hit.
        var ordinary = new HealthLossLedger();
        ordinary.Seed(seat, Owner, 100.0, 100.0);
        check(ordinary.Observe(seat, Owner, 75.0, 100.0) == 25.0 && ordinary.UnsettledSeeds == 0,
            "the call's own postfix credits the drop and counts no unsettled seat");
        // A seeded call that changed nothing is an ordinary call, not a lost blow.
        var quiet = new HealthLossLedger();
        quiet.Seed(seat, Owner, 100.0, 100.0);
        check(quiet.Observe(seat, Owner, 100.0, 100.0) == 0.0 && quiet.UnsettledSeeds == 0,
            "a pair whose figure did not move is not counted either: nothing about it is unsettleable");
        // And a rise after a *completed* pair is simply a heal: that mark is no longer a seed.
        check(ordinary.Observe(seat, Owner, 100.0, 100.0) == 0.0 && ordinary.UnsettledSeeds == 0,
            "a heal that follows a credited blow is a heal, not an unobserved one");
        // The count belongs to the expedition, like every mark.
        ledger.Clear();
        check(ledger.UnsettledSeeds == 0 && ledger.Marked == 0,
            "a new expedition starts with no marks and no such seats");
    }

    // ---- 5. wiring, read from the production source --------------------------------------------------------------------

    private static void Wiring(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the 1.19.35 review fixes locate the production sources");
        if (root == null) return;
        string ledgerFile = Code(File.ReadAllText(Path.Combine(root, "HealthLossLedger.cs")));
        string patches = Code(File.ReadAllText(Path.Combine(root, "HealthLossPatches.cs")));
        check(ledgerFile.Contains("if (previous.Seeded && now > previous.Health) _unsettledSeeds++;"),
            "the ledger counts a seeded seat re-based upwards, and only upwards: `>` keeps an unchanged pair out of the count");
        check(patches.Contains("int unsettled = Ledger.UnsettledSeeds;") && !patches.Contains("UnsettledSeeds = "),
            "the patch reads that count and can never write it");
        string note = Between(patches, "private static void NoteUnsettledSeeds()", "\n    }");
        check(note.Length > 0 && note.Contains("Plugin.Logger?.LogInfo") && !note.Contains("HealthLost"),
            "the report is one diagnostic line at 1/2/4/8... seats, and it credits nothing");
        check(patches.Contains("NoteUnsettledSeeds();") && patches.Contains("if (!(loss > 0)) return;"),
            "and the postfix calls it before the credit gate, because the seats it counts are the ones that credit nothing");

        string supply = Code(File.ReadAllText(Path.Combine(root, "SupplyUse.cs")));
        string metrics = Code(File.ReadAllText(Path.Combine(root, "ImpactMetrics.cs")));
        check(!supply.Contains("bool ammo") && !metrics.Contains("CreditSelfSupply(bool ammo)") && !metrics.Contains("SupplyPackKind.Tool)"),
            "no one-bit overload is left to read `false` as a tool pack: a kind is named at the call site");
        check(metrics.Contains("case SupplyPackKind.Medical:")
              && metrics.Contains("if (SelfMedicalUses < long.MaxValue) SelfMedicalUses++;")
              && metrics.Contains("SelfMedicalKnown = true;"),
            "and a medical pack has a field of its own, so it cannot land in the tool one - and crediting it records that this machine could confirm it");

        string rows = Code(File.ReadAllText(Path.Combine(root, "SettlementRows.cs")));
        check(rows.Contains("if (b.SelfSupplyUses > a.SelfSupplyUses)")
              && rows.Contains("if (a.SelfAmmoUses > a.SelfSupplyUses) a.SelfAmmoUses = a.SelfSupplyUses;"),
            "the mirror takes one side's pair whole and never hands on an ammo part larger than its total");

        string verify = File.ReadAllText(Path.Combine(root, "scripts", "Verify.ps1"));
        check(verify.Contains("return name == \"ReceiveTentacleGrabStop\";")
              && verify.Contains("if (name == ParasiteDamage) return false;")
              && verify.Contains("HealthLossPatches.SeedBefore(__instance)"),
            "and the release gate reads the two policy bodies it guards, so it cannot pass on a comment");
        Console.WriteLine("WIRING: RELEASE_11935_REVIEW_FIXES (static source check; the counting, the cell, the mirror and the ledger are the assertions above)");
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private static string Between(string text, string from, string to)
    {
        if (text == null) return string.Empty;
        int start = text.IndexOf(from, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        int end = text.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        return end < 0 ? text.Substring(start) : text.Substring(start, end - start);
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj"))) directory = directory.Parent;
        return directory != null ? directory.FullName : null;
    }

    // The text with its comment lines removed, so a comment may name a call this build must not make.
    private static string Code(string text)
    {
        var kept = new List<string>();
        if (text == null) return string.Empty;
        foreach (string line in text.Split('\n'))
            if (!line.TrimStart().StartsWith("//", StringComparison.Ordinal)) kept.Add(line);
        return string.Join("\n", kept);
    }
}
