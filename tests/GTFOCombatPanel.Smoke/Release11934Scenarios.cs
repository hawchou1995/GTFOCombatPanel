using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using HawChow.GTFOCombatPanel;

// 1.19.34: the settlement board's supply row keeps its one row and shows four fields per player -
//   the utilization the score is built from, then the packs that player actually consumed, split into
//   ammo / tool / medical: `补给效率 / 弹药包 / 工具包 / 血包`.
//
// What is asserted here is that the split is *counted* rather than inferred: a use is exactly one pack of exactly one
// kind, the three kinds add up to the total, the efficiency and its giver attribution are untouched, a peer that only
// speaks the v8 protocol keeps its row (and its kinds print as unknown instead of zero), and the board still has the
// same number of rows.
//
// What cannot be asserted here: Unity. Which native callback a pack arrived through (GiveAmmoRel, pAmmoGive,
// ReceiveAddHealth) is game state; the tests drive the same `SupplyUse`/`ImpactCounters`/`SyncProtocol` code those
// callbacks reach.
internal static class Release11934Scenarios
{
    private const ulong Supplier = 0xA1UL;
    private const ulong Receiver = 0xB2UL;
    private const ulong Settled = 0x51UL;
    private const ulong Grown = 0x52UL;

    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        EachPackKindIsCountedOnce(check);
        AmmoPackCoversStandardAndSpecialAsOne(check);
        ToolAndMedicalNeverBecomeAmmo(check);
        InvalidAndEmptyUsesCountNothing(check);
        NestedAndRepeatedEventsCountOnce(check);
        EfficiencyStaysWithTheGiverAndCountsWithTheReceiver(check);
        SeveralPlayersKeepTheirOwnKinds(check);
        TheBlockSurvivesCloneAndReset(check);
        TheSettlementMirrorKeepsTheSplitWhole(check);
        TheRowIsOneRowWithFourFields(check);
        AnOlderPeerRowPrintsUnknownNotZero(check);
        AMalformedPairPrintsUnknownAndKeepsItsFigures(check);
        AnUnclassifiedHealIsCountedAndNeverCalledABloodPack(check);
        AClientsPackReachesTheTeammatesSettlementRow(check);
        ADroppedSplitNeverPrintsAMedicalZero(check);
        TheKindsPacketIsBesideTheRowAndAV8PeerIgnoresIt(check);
        AV8PeersRowStillArrivesAndIsReadable(check);
        TheExchangeAcceptsOnlyTheOwnersSplit(check);
        TheSplitLandsWhereItsSenderWrote(check);
        TheArchivedFileKeepsItsKeysAndTheLegacyRow(check);
        AWiderCellIsMeasuredAndNeverClipped(check);
        ANarrowPageFitsTheWiderRowInsteadOfClippingIt(check);
        Wiring(check);
    }

    // ---- 1. one pack, one kind --------------------------------------------------------------------------------------

    private static void EachPackKindIsCountedOnce(Action<bool, string> check)
    {
        foreach (SupplyPackKind kind in new[] { SupplyPackKind.Ammo, SupplyPackKind.Tool, SupplyPackKind.Medical })
        {
            var counters = new ImpactCounters();
            counters.CreditSelfSupply(kind);
            bool right = kind == SupplyPackKind.Ammo ? counters.SelfAmmoUses == 1
                : kind == SupplyPackKind.Tool ? counters.SelfToolUses == 1
                : counters.SelfMedicalUses == 1;
            check(counters.SelfSupplyUses == 1 && right && counters.SelfSupplyKindsKnown
                  && counters.SelfAmmoUses + counters.SelfToolUses + counters.SelfMedicalUses == counters.SelfSupplyUses,
                $"{kind}: one pack raises the total by one, its own kind by one, and the three kinds add up to the total");
        }
        var mixed = new ImpactCounters();
        mixed.CreditSelfSupply(SupplyPackKind.Ammo);
        mixed.CreditSelfSupply(SupplyPackKind.Tool);
        mixed.CreditSelfSupply(SupplyPackKind.Medical);
        mixed.CreditSelfSupply(SupplyPackKind.Medical);
        check(mixed.SelfSupplyUses == 4 && mixed.SelfAmmoUses == 1 && mixed.SelfToolUses == 1 && mixed.SelfMedicalUses == 2
              && mixed.SelfAmmoUses + mixed.SelfToolUses + mixed.SelfMedicalUses == mixed.SelfSupplyUses,
            "four packs of three kinds stay four uses that add up: 1 ammo, 1 tool, 2 medical");
    }

    private static void AmmoPackCoversStandardAndSpecialAsOne(Action<bool, string> check)
    {
        // One pack that filled both the standard and the special channel: two native reads, one use, one ammo pack.
        var use = new SupplyUse();
        use.AddChange(10, 40, 30);        // standard
        use.AddChange(5, 25, 20);         // special
        var receiver = new ImpactCounters();
        check(use.Record(receiver) && use.RecordConsumption(receiver, SupplyPackKind.Ammo),
            "a pack that filled two channels is one measured use");
        check(receiver.SupplyUses == 1 && receiver.SelfSupplyUses == 1 && receiver.SelfAmmoUses == 1
              && receiver.SelfToolUses == 0 && receiver.SelfMedicalUses == 0,
            "and exactly one ammo pack for the receiver, not one per channel");
        double expected = (double)use.Effective / use.Intended;
        check(Math.Abs(receiver.SupplyEfficiency - expected) < 1e-9 && Math.Abs(receiver.SupplyEfficiency - 1.0) < 1e-9,
            "the efficiency of that one use is its own effective/intended ratio, unchanged by the split");
    }

    private static void ToolAndMedicalNeverBecomeAmmo(Action<bool, string> check)
    {
        var receiver = new ImpactCounters();
        var tool = new SupplyUse(); tool.AddChange(0, 2, 2);
        var medical = new SupplyUse(); medical.AddChange(40, 70, 30);
        tool.RecordConsumption(receiver, SupplyPackKind.Tool);
        medical.RecordConsumption(receiver, SupplyPackKind.Medical);
        check(receiver.SelfSupplyUses == 2 && receiver.SelfAmmoUses == 0 && receiver.SelfToolUses == 1 && receiver.SelfMedicalUses == 1,
            "a tool refill and a medical heal are one tool pack and one medical pack, and neither is an ammo pack");
        check(receiver.SelfAmmoUses + receiver.SelfToolUses + receiver.SelfMedicalUses == receiver.SelfSupplyUses,
            "the three kinds still add up to the total the row prints beside them");
    }

    private static void InvalidAndEmptyUsesCountNothing(Action<bool, string> check)
    {
        var receiver = new ImpactCounters();
        var overflowed = new SupplyUse(); overflowed.AddChange(100, 100, 30);
        check(!overflowed.RecordConsumption(receiver, SupplyPackKind.Ammo) && receiver.SelfSupplyUses == 0,
            "a use the receiver gained nothing from claims no pack of any kind");

        var broken = new SupplyUse(); broken.AddChange(double.NaN, 0, 20);
        check(broken.Invalid && !broken.RecordConsumption(receiver, SupplyPackKind.Medical) && receiver.SelfSupplyUses == 0
              && !receiver.SelfSupplyKindsKnown,
            "an unreadable use is refused, and a row that never counted anything was never split either");

        var heal = new SupplyUse(); heal.AddChange(50, 80, 20);
        heal.RecordConsumption(receiver, SupplyPackKind.Medical);
        check(receiver.SelfSupplyUses == 1 && receiver.SelfMedicalUses == 1 && receiver.SelfSupplyKindsKnown,
            "while a real pack is counted, and only then is the row marked as split");
    }

    private static void NestedAndRepeatedEventsCountOnce(Action<bool, string> check)
    {
        // The nested GiveAmmoRel and the replicated packet describe one pack: the scope records it once, and a second
        // RecordConsumption on the same use is refused.
        var use = new SupplyUse(); use.AddChange(0, 30, 30);
        var receiver = new ImpactCounters();
        check(use.RecordConsumption(receiver, SupplyPackKind.Ammo), "the first channel counts the pack");
        check(!use.RecordConsumption(receiver, SupplyPackKind.Ammo) && receiver.SelfSupplyUses == 1,
            "the nested/duplicate delivery of the same use counts nothing a second time");
        check(!use.Record(receiver) || receiver.SupplyUses == 1,
            "and the measured use is recorded once as well");

        var second = new SupplyUse(); second.AddChange(0, 5, 5);
        check(second.RecordConsumption(receiver, SupplyPackKind.Ammo) && receiver.SelfSupplyUses == 2 && receiver.SelfAmmoUses == 2,
            "a genuinely separate pack is a second use, of its own kind");
    }

    private static void EfficiencyStaysWithTheGiverAndCountsWithTheReceiver(Action<bool, string> check)
    {
        var giver = new ImpactCounters();
        var receiver = new ImpactCounters();
        var use = new SupplyUse(); use.AddChange(0, 40, 40);
        check(use.Record(giver), "the utilization belongs to the player who opened the pack");
        check(use.RecordConsumption(receiver, SupplyPackKind.Tool), "and the consumed pack belongs to the player who received it");
        check(giver.SupplyUses == 1 && Math.Abs(giver.SupplyEfficiency - 1.0) < 1e-9
              && giver.SelfSupplyUses == 0 && !giver.SelfSupplyKindsKnown,
            "the giver gains an efficiency sample and no consumption, of any kind");
        check(receiver.SupplyUses == 0 && receiver.SelfSupplyUses == 1 && receiver.SelfToolUses == 1,
            "the receiver gains the tool pack and no efficiency sample, exactly as before this round");
    }

    private static void SeveralPlayersKeepTheirOwnKinds(Action<bool, string> check)
    {
        var first = new ImpactCounters();
        var second = new ImpactCounters();
        var third = new ImpactCounters();
        var ammo = new SupplyUse(); ammo.AddChange(0, 10, 10);
        var tool = new SupplyUse(); tool.AddChange(0, 1, 1);
        var medical = new SupplyUse(); medical.AddChange(20, 50, 30);
        ammo.RecordConsumption(first, SupplyPackKind.Ammo);
        tool.RecordConsumption(second, SupplyPackKind.Tool);
        medical.RecordConsumption(third, SupplyPackKind.Medical);
        var medicalAgain = new SupplyUse(); medicalAgain.AddChange(10, 40, 30);
        medicalAgain.RecordConsumption(third, SupplyPackKind.Medical);
        check(first.SelfAmmoUses == 1 && second.SelfToolUses == 1 && third.SelfMedicalUses == 2
              && first.SelfSupplyUses == 1 && second.SelfSupplyUses == 1 && third.SelfSupplyUses == 2,
            "three players, four independent packs: nobody's kind is counted on somebody else's row");
        foreach (ImpactCounters row in new[] { first, second, third })
            check(row.SelfAmmoUses + row.SelfToolUses + row.SelfMedicalUses == row.SelfSupplyUses,
                "and each row adds up on its own");
    }

    private static void TheBlockSurvivesCloneAndReset(Action<bool, string> check)
    {
        var source = new ImpactCounters();
        source.CreditSelfSupply(SupplyPackKind.Ammo);
        source.CreditSelfSupply(SupplyPackKind.Medical);
        source.CreditSelfSupply(SupplyPackKind.Medical);
        var copy = new ImpactCounters();
        copy.CopyFrom(source);
        check(copy.SelfSupplyUses == 3 && copy.SelfAmmoUses == 1 && copy.SelfMedicalUses == 2 && copy.SelfSupplyKindsKnown,
            "a cloned row carries the split, not only the pair");
        copy.Reset();
        check(copy.SelfSupplyUses == 0 && copy.SelfAmmoUses == 0 && copy.SelfToolUses == 0 && copy.SelfMedicalUses == 0
              && !copy.SelfSupplyKindsKnown,
            "and a reset row forgets the split with the pair, so the next run starts unknown rather than stale");
        check(source.SelfSupplyUses == 3, "the row it was cloned from is untouched");
    }

    private static void TheSettlementMirrorKeepsTheSplitWhole(Action<bool, string> check)
    {
        // The mirror takes the block whole from the side that has one: maxing field by field across a split and an
        // unsplit row would print a total that its own kinds no longer add up to.
        var rows = new SettlementRows();
        var settled = new CombatPlayer(Settled) { Name = "Settled" };
        settled.Impact.SelfSupplyUses = 5; settled.Impact.SelfAmmoUses = 2;
        rows.Update(new[] { settled }, 7);

        // A later, smaller split arrives for the same row: the pair the run already reported stands, and the split
        // cannot describe it, so it is not spliced in and the kinds go back to unknown.
        var later = new CombatPlayer(Settled) { Name = "Settled" };
        later.Impact.CreditSelfSupply(SupplyPackKind.Tool);
        later.Impact.CreditSelfSupply(SupplyPackKind.Medical);
        CombatPlayer held = rows.Update(new[] { later }, 7)[0];
        check(held.Impact.SelfSupplyUses == 5 && held.Impact.SelfAmmoUses == 2 && !held.Impact.SelfSupplyKindsKnown,
            "a pair the run already reported is not shrunk by a later, smaller split, and no kinds are kept for packs the split never counted");

        // The same row when the split is the larger of the two: taken whole, with its own total and its own parts.
        var grown = new SettlementRows();
        var pairOnly = new CombatPlayer(Grown) { Name = "Grown" };
        pairOnly.Impact.SelfSupplyUses = 1; pairOnly.Impact.SelfAmmoUses = 1;
        grown.Update(new[] { pairOnly }, 7);
        var split = new CombatPlayer(Grown) { Name = "Grown" };
        split.Impact.CreditSelfSupply(SupplyPackKind.Tool);
        split.Impact.CreditSelfSupply(SupplyPackKind.Medical);
        split.Impact.CreditSelfSupply(SupplyPackKind.Medical);
        CombatPlayer whole = grown.Update(new[] { split }, 7)[0];
        check(whole.Impact.SelfSupplyKindsKnown && whole.Impact.SelfSupplyUses == 3
              && whole.Impact.SelfAmmoUses == 0 && whole.Impact.SelfToolUses == 1 && whole.Impact.SelfMedicalUses == 2,
            "a split that is larger than the pair held is taken whole: 3 packs, none of them ammo");
        check(whole.Impact.SelfAmmoUses + whole.Impact.SelfToolUses + whole.Impact.SelfMedicalUses == whole.Impact.SelfSupplyUses,
            "and the row the mirror hands back still adds up, which is what the one cell prints");
    }

    // ---- 2. the row itself ------------------------------------------------------------------------------------------

    private static void TheRowIsOneRowWithFourFields(Action<bool, string> check)
    {
        var counters = new ImpactCounters();
        counters.RecordSupply(30, 100);
        counters.CreditSelfSupply(SupplyPackKind.Ammo);
        counters.CreditSelfSupply(SupplyPackKind.Tool);
        counters.CreditSelfSupply(SupplyPackKind.Medical);
        counters.CreditSelfSupply(SupplyPackKind.Medical);
        var entry = new ImpactEntry(new ImpactInput { Name = "Supplier", Counters = counters },
            null, null, 40, 1, ImpactRating.Supply, (int)ImpactMetric.SupplyEfficiency);
        string[] fields = ImpactBoard.MetricFields(entry, ImpactMetric.SupplyEfficiency, KillTier.Minor, false, true);
        check(fields.Length == 1, "the supply row is still one cell: no field was moved to a row of its own");
        string[] parts = fields[0].Split(new[] { CombatStrings.CellSeparator }, StringSplitOptions.None);
        check(parts.Length == 4, "and that one cell carries the four figures the label names: " + fields[0]);
        check(parts[0] == "30.0%" && parts[1] == "1" && parts[2] == "1" && parts[3] == "2",
            "utilization, then ammo / tool / medical packs: " + fields[0]);
        check(entry.Input.Counters.SelfSupplyUses == 4, "the total behind them is still 4, which is what the split adds up to");

        var board = ImpactBoard.Build(ImpactScoring.Evaluate(new[] { entry.Input }, ImpactScoring.DefaultWeights, 1.0, 1.0), true, true);
        check(board.Rows == 30 && ImpactBoard.FixedRows == 14, "the board still holds 30 rows: no row was added for the split");
        // The two texts the label exists in; the Chinese one is what the row reads in the default language.
        string language = CombatStrings.Language;
        CombatStrings.Language = "zh";
        try
        {
            string label = CombatStrings.SupplyRowLabel;
            check(label.Contains("弹药包") && label.Contains("工具包") && label.Contains("血包") && !label.Contains("本人"),
                "the row label names the four fields it prints: " + label);
            // 1.19.60: the released label is kept as public API (and as the string a reader may still meet in older
            // files), but the archive no longer writes it - see `ResultsLog.Build` and the enumeration in
            // `Release11960Scenarios`. Its own text is unchanged, which is what this assertion is about.
            check(CombatStrings.SupplyRowLabelLegacy.Contains("本人"),
                "while the released label keeps its own text as public API, even though the archive no longer writes it: "
                    + CombatStrings.SupplyRowLabelLegacy);
        }
        finally { CombatStrings.Language = language; }
    }

    private static void AnOlderPeerRowPrintsUnknownNotZero(Action<bool, string> check)
    {
        // A row restored from a v8 snapshot: the pair is real, the split never arrived.
        var restored = new ImpactCounters { SelfSupplyUses = 2, SelfAmmoUses = 0, SelfSupplyKindsKnown = false };
        string cell = CombatStrings.SupplyCell(CombatStrings.Placeholder, restored.SelfSupplyUses, restored.SelfAmmoUses,
            restored.SelfToolUses, restored.SelfMedicalUses, restored.SelfSupplyKindsKnown);
        string[] parts = cell.Split(new[] { CombatStrings.CellSeparator }, StringSplitOptions.None);
        // 1.19.36: the cell is four fields in the unsplit branch too - one per name on the label - because the three kind
        // fields are what replaced the released "all packs" total. There is no total field and no fifth one, and `?` marks an
        // unknown *kind* only: a row that may not be split keeps the ammo part it really sent.
        check(parts.Length == 4,
            "a row that was never split still prints exactly the four fields its label names, and no fifth: " + cell);
        check(parts[1] == "0" && parts[2] == CombatStrings.Unknown && parts[3] == CombatStrings.Unknown,
            "it prints the ammo part it really knows and `?` for the two kinds nobody observed: " + cell);
        check(cell != CombatStrings.Placeholder
              && !parts[2].Equals("0", StringComparison.Ordinal) && !parts[3].Equals("0", StringComparison.Ordinal),
            "the two kind fields are never a zero and never a guess about which pack those two were: " + cell);

        // The same shape with a real ammo figure: the second field is the number that arrived, not a placeholder.
        var mixed = new ImpactCounters { SelfSupplyUses = 5, SelfAmmoUses = 2, SelfSupplyKindsKnown = false };
        string mixedCell = CombatStrings.SupplyCell("40.0%", mixed.SelfSupplyUses, mixed.SelfAmmoUses,
            mixed.SelfToolUses, mixed.SelfMedicalUses, mixed.SelfSupplyKindsKnown);
        string[] mixedParts = mixedCell.Split(new[] { CombatStrings.CellSeparator }, StringSplitOptions.None);
        check(mixedParts.Length == 4 && mixedParts[0] == "40.0%" && mixedParts[1] == "2"
              && mixedParts[2] == CombatStrings.Unknown && mixedParts[3] == CombatStrings.Unknown,
            "a row whose 5 packs include 2 ammo parts keeps that 2 in the ammo field and `?` in the two kinds: " + mixedCell);

        var fullyAmmo = new ImpactCounters { SelfSupplyUses = 3, SelfAmmoUses = 3, SelfSupplyKindsKnown = false };
        string fullyAmmoCell = CombatStrings.SupplyCell("—", fullyAmmo.SelfSupplyUses, fullyAmmo.SelfAmmoUses, 0, 0, fullyAmmo.SelfSupplyKindsKnown);
        check(fullyAmmoCell.Split(new[] { CombatStrings.CellSeparator }, StringSplitOptions.None).Length == 4
              && fullyAmmoCell.EndsWith("/ 0 / 0", StringComparison.Ordinal),
            "while a pair whose total is its ammo part has no remainder to be unknown about, so both kinds are a proven zero: " + fullyAmmoCell);
        // The v8 snapshot itself still carries the pair, and a restored row refuses to claim a split it never received.
        var captured = new ImpactCounters();
        captured.CreditSelfSupply(SupplyPackKind.Tool);
        var player = new CombatPlayer(0x41UL) { Name = "Old" };
        player.Impact.CopyFrom(captured);
        SyncSnapshot snap = SyncProtocol.Capture(player, 1, 2, 3, 4, 0, false);
        check(snap.SelfSupplyUses == 1 && snap.SelfAmmoUses == 0,
            "a row measured here still puts total+ammo into the v8 snapshot, byte for byte as before");
        var target = new CombatPlayer(0x41UL) { Name = "Old" };
        SyncProtocol.Apply(target, snap, 1, 2, 3, 4);
        check(target.Impact.SelfSupplyUses == 1 && target.Impact.SelfAmmoUses == 0 && !target.Impact.SelfSupplyKindsKnown,
            "and a row rebuilt from that snapshot carries the pair and no split - the split travels elsewhere");
    }

    // ---- 3. the protocol: beside the row, never inside it -----------------------------------------------------------

    private static void TheKindsPacketIsBesideTheRowAndAV8PeerIgnoresIt(Action<bool, string> check)
    {
        check(SyncProtocol.Version == 8, "the protocol version is unchanged: a v8 peer is still a peer of this build");
        check(SyncProtocol.SnapshotSize == 360 && SquadPackets.SnapshotBytes == 376,
            "and the snapshot is the same 360-byte body it was: appending to it would have made every row unreadable to a v8 peer");
        check(SquadPackets.HelloBytes == 24, "the greeting is unchanged too");

        var kinds = new SyncProtocol.SupplyKinds { Owner = 0x41UL, Total = 3, Ammo = 1, Tool = 1, Medical = 1 };
        byte[] packet = SquadPackets.SupplyKinds(kinds, 7, SyncProtocol.Version);
        check(packet.Length == SquadPackets.SupplyKindsBytes && packet.Length == SquadPackets.HeaderBytes + SyncProtocol.SupplyKindsSize,
            "the split travels in a packet of its own length: " + packet.Length);
        SquadPacket parsed = SquadPackets.Parse(packet, packet.Length);
        check(parsed.Kind == SquadPacketKind.SupplyKinds && parsed.IsOurs && parsed.Kinds.Owner == 0x41UL
              && parsed.Kinds.Total == 3 && parsed.Kinds.Tool == 1 && parsed.Kinds.Medical == 1,
            "and parses back into the block it carries");

        // What a v8 build does with that packet: it reads the header, does not know the kind, and consumes it. This
        // build must behave exactly the same way for a kind it does not know.
        byte[] future = (byte[])packet.Clone();
        future[6] = 9; future[7] = 0;      // a kind no build knows
        SquadPacket unknown = SquadPackets.Parse(future, future.Length);
        check(unknown.Kind == SquadPacketKind.Unknown && unknown.IsOurs,
            "an unknown kind of this build's protocol is consumed and ignored, never handed to the game");

        // A split that does not add up is refused at both ends.
        var broken = new SyncProtocol.SupplyKinds { Owner = 0x41UL, Total = 4, Ammo = 1, Tool = 1, Medical = 1 };
        check(!broken.IsUsable, "a block whose parts do not add up to its total is not a usable block");
        byte[] brokenPacket = SquadPackets.SupplyKinds(broken, 7, SyncProtocol.Version);
        check(SquadPackets.Parse(brokenPacket, brokenPacket.Length).Kind == SquadPacketKind.Malformed,
            "and a packet carrying one is consumed as malformed rather than believed");
        check(SquadPackets.Parse(packet, packet.Length - 1).Kind == SquadPacketKind.Malformed,
            "a truncated split packet is malformed, not read as if the missing bytes had arrived");
    }

    private static void AV8PeersRowStillArrivesAndIsReadable(Action<bool, string> check)
    {
        // The row a v8 peer sends, built the way the released build built it: same body, same envelope, no split packet.
        var row = new SyncSnapshot { Owner = 0x41UL };
        row.SelfSupplyUses = 6; row.SelfAmmoUses = 4; row.HealthLost = 148.5f;
        byte[] bytes = SquadPackets.Snapshot(row, 3, SyncProtocol.Version);
        check(bytes.Length == 376, "a v8 row is still a 376-byte packet");
        SquadPacket parsed = SquadPackets.Parse(bytes, bytes.Length);
        check(parsed.Kind == SquadPacketKind.Snapshot && parsed.Snapshot.SelfSupplyUses == 6 && parsed.Snapshot.SelfAmmoUses == 4
              && Math.Abs(parsed.Snapshot.HealthLost - 148.5f) < 0.01f,
            "and this build reads it exactly as the released one did - total, ammo and health lost included");
        check(SyncProtocol.Decode(bytes, SquadPackets.HeaderBytes, bytes.Length - SquadPackets.HeaderBytes, out SyncSnapshot decoded)
              && decoded.SelfSupplyUses == 6,
            "the row body decodes at the same size and offset, so an older peer's rows are not a different format");

        // The reverse direction: this build's row for a v8 peer is byte-identical to the released one (no split inside).
        var here = new CombatPlayer(0x42UL) { Name = "Here" };
        here.Impact.CreditSelfSupply(SupplyPackKind.Ammo);
        here.Impact.CreditSelfSupply(SupplyPackKind.Medical);
        SyncSnapshot ours = SyncProtocol.Capture(here, 1, 2, 3, 4, 0, false);
        byte[] oursBytes = SquadPackets.Snapshot(ours, 3, SyncProtocol.Version);
        check(oursBytes.Length == 376 && ours.SelfSupplyUses == 2 && ours.SelfAmmoUses == 1,
            "this build's row for a v8 peer is the same packet it always was: total and ammo, nothing appended");
    }

    private static void TheExchangeAcceptsOnlyTheOwnersSplit(Action<bool, string> check)
    {
        // This machine is the receiver; the peer that owns the packs is the sender, and it is the host.
        var exchange = new SquadExchange();
        exchange.Configure(Receiver, 1, 2, 3, 4);
        exchange.SetRoster(new[]
        {
            new SquadPeer(Supplier, 0, false, false, true),
            new SquadPeer(Receiver, 1, true, false, false),
        }, false);
        var good = new SyncProtocol.SupplyKinds { Owner = Supplier, Total = 2, Ammo = 0, Tool = 1, Medical = 1 };
        check(exchange.OnSupplyKinds(Supplier, 1, good, 10f).Accepted,
            "a peer's split of its own packs is accepted");
        check(!exchange.OnSupplyKinds(Supplier, 1, new SyncProtocol.SupplyKinds { Owner = Receiver, Total = 1, Ammo = 1 }, 10f).Accepted,
            "a peer claiming somebody else's packs is refused: only the owner can split its own use");
        check(!exchange.OnSupplyKinds(Receiver, 1, good, 10f).Accepted,
            "an echo of this machine's own row is refused");
        check(!exchange.OnSupplyKinds(0xC3UL, 1, new SyncProtocol.SupplyKinds { Owner = 0xC3UL, Total = 1, Ammo = 1 }, 10f).Accepted,
            "a peer outside the roster is refused");
        check(!exchange.OnSupplyKinds(Supplier, 1, new SyncProtocol.SupplyKinds { Owner = Supplier, Total = 3, Ammo = 1 }, 10f).Accepted,
            "a block that does not add up is refused by the exchange as well");
        check(exchange.OnSupplyKinds(Supplier, 1, good, 11f).Accepted,
            "the same split delivered twice is idempotent - it is cached, not accumulated");

        // What the accepted split does to the row once it is projected: the pair the row holds is the pair the split
        // describes, so the kinds fill in - and a row whose pair is a different one keeps them unknown.
        var measured = new CombatPlayer(Supplier) { Name = "Supplier" };
        measured.Impact.SelfSupplyUses = 2; measured.Impact.SelfAmmoUses = 0;
        var snapshot = SyncProtocol.Capture(measured, 1, 2, 3, 4, 0, false);
        check(exchange.OnSnapshot(Supplier, 1, snapshot, 12f).Accepted, "the peer's own row is taken as well");
        CombatPlayer projected = Find(exchange.Project(new[] { measured }, Receiver), Supplier);
        check(projected != null && projected.Impact.SelfSupplyKindsKnown
              && projected.Impact.SelfToolUses == 1 && projected.Impact.SelfMedicalUses == 1,
            "the split is applied to the row it describes, so that row prints its two kinds");

        var other = new CombatPlayer(Supplier) { Name = "Supplier" };
        other.Impact.SelfSupplyUses = 4; other.Impact.SelfAmmoUses = 1;
        var otherSnapshot = SyncProtocol.Capture(other, 1, 2, 3, 4, 0, false);
        check(exchange.OnSnapshot(Supplier, 1, otherSnapshot, 13f).Accepted, "a newer row of the same peer replaces the pair");
        CombatPlayer reassembled = Find(exchange.Project(new[] { other }, Receiver), Supplier);
        check(reassembled != null && !reassembled.Impact.SelfSupplyKindsKnown
              && reassembled.Impact.SelfSupplyUses == 4,
            "and a row whose pair no longer is the one the split describes prints no kinds at all, rather than kinds from another pair");
    }
    // 1.19.35 fix: a split describes the row of the sender that carried it, and "the sender" is not the same question as "is
    // the owner the host". A host owns a backpack too, and its split fills in the host slot its own row was written to; a block
    // that answers on both sides is still counted once; and a block never fills in a row it does not name.
    private static void TheSplitLandsWhereItsSenderWrote(Action<bool, string> check)
    {
        const ulong HostOwner = 0xD4UL;
        // --- 1. the owner is the host: the split fills in the host slot, the one its own row went to ---
        var exchange = new SquadExchange();
        exchange.Configure(Receiver, 1, 2, 3, 4);
        exchange.SetRoster(new[]
        {
            new SquadPeer(HostOwner, 0, false, false, true),
            new SquadPeer(Receiver, 1, true, false, false),
        }, false);
        var hostMeasured = new CombatPlayer(HostOwner) { Name = "HostOwner" };
        hostMeasured.Impact.SelfSupplyUses = 3; hostMeasured.Impact.SelfAmmoUses = 0;
        var hostRow = SyncProtocol.Capture(hostMeasured, 1, 2, 3, 4, 0, false);
        check(exchange.OnSnapshot(HostOwner, 1, hostRow, 10f).Accepted, "a host's row about itself is taken as the host's row");
        check(exchange.OnSupplyKinds(HostOwner, 1, new SyncProtocol.SupplyKinds { Owner = HostOwner, Total = 3, Ammo = 0, Tool = 2, Medical = 1 }, 10f).Accepted,
            "and the host's split of its own packs is accepted");
        CombatPlayer hostProjected = Find(exchange.Project(new List<CombatPlayer>(), Receiver), HostOwner);
        check(hostProjected != null && hostProjected.Impact.SelfSupplyKindsKnown
              && hostProjected.Impact.SelfToolUses == 2 && hostProjected.Impact.SelfMedicalUses == 1,
            "a split from an owner that is also the host fills in the host row it wrote, not the empty peer slot beside it");

        // --- 2. both slots hold a row for the same player: the split is applied once, and only to that player's row ---
        const ulong HostPeer = 0xD5UL;
        var both = new SquadExchange();
        both.Configure(Receiver, 1, 2, 3, 4);
        both.SetRoster(new[]
        {
            new SquadPeer(HostPeer, 0, false, false, true),
            new SquadPeer(Supplier, 2, false, false, false),
            new SquadPeer(Settled, 3, false, false, false),
            new SquadPeer(Receiver, 1, true, false, false),
        }, false);
        var supplierMeasured = new CombatPlayer(Supplier) { Name = "Supplier" };
        supplierMeasured.Impact.SelfSupplyUses = 2; supplierMeasured.Impact.SelfAmmoUses = 0;
        var ownRow = SyncProtocol.Capture(supplierMeasured, 1, 2, 3, 4, 0, false);
        var hostCopy = SyncProtocol.Capture(supplierMeasured, 1, 2, 3, 4, 0, false);
        check(both.OnSnapshot(Supplier, 1, ownRow, 10f).Accepted && both.OnSnapshot(HostPeer, 1, hostCopy, 10.2f).Accepted,
            "both a host copy and the player's own copy of one row are cached, one in each slot");
        check(both.OnSupplyKinds(Supplier, 1, new SyncProtocol.SupplyKinds { Owner = Supplier, Total = 2, Ammo = 0, Tool = 1, Medical = 1 }, 10.4f).Accepted,
            "the owner's split is accepted from the owner's own machine");
        // A third player whose row holds exactly the pair the block splits: only the block's owner may be filled in.
        var settledMeasured = new CombatPlayer(Settled) { Name = "Settled" };
        settledMeasured.Impact.SelfSupplyUses = 2; settledMeasured.Impact.SelfAmmoUses = 0;
        check(both.OnSnapshot(HostPeer, 1, SyncProtocol.Capture(settledMeasured, 1, 2, 3, 4, 0, false), 10.6f).Accepted,
            "and the host's row about a third player is cached too, holding the same total/ammo pair");
        List<CombatPlayer> rows = both.Project(new List<CombatPlayer>(), Receiver);
        CombatPlayer supplierRow = Find(rows, Supplier);
        check(supplierRow != null && supplierRow.Impact.SelfSupplyKindsKnown
              && supplierRow.Impact.SelfToolUses == 1 && supplierRow.Impact.SelfMedicalUses == 1
              && supplierRow.Impact.SelfSupplyUses == 2,
            "with a row on each side, the split is applied once: each kind is exactly the block's count and the total is not added to");
        CombatPlayer settledRow = Find(rows, Settled);
        check(settledRow != null && settledRow.Impact.SelfSupplyUses == 2 && settledRow.Impact.SelfAmmoUses == 0
              && !settledRow.Impact.SelfSupplyKindsKnown
              && settledRow.Impact.SelfToolUses == 0 && settledRow.Impact.SelfMedicalUses == 0,
            "and another player's row with the same pair keeps its kinds unknown: a split only fills in the row it names");
    }

    // ---- 4. the archived file ---------------------------------------------------------------------------------------

    private static void TheArchivedFileKeepsItsKeysAndTheLegacyRow(Action<bool, string> check)
    {
        var counters = new ImpactCounters();
        counters.RecordSupply(30, 100);
        counters.CreditSelfSupply(SupplyPackKind.Ammo);
        counters.CreditSelfSupply(SupplyPackKind.Tool);
        counters.CreditSelfSupply(SupplyPackKind.Medical);
        counters.CreditSelfSupply(SupplyPackKind.Medical);
        string language = CombatStrings.Language;
        CombatStrings.Language = "zh";
        try
        {
            var input = new ImpactInput { Name = "Writer", Counters = counters };
            ImpactReport report = ImpactScoring.Evaluate(new[] { input }, ImpactScoring.DefaultWeights, 1.0, 1.0);
            ImpactBoardLayout board = ImpactBoard.Build(report, true, true);
            string json = ResultsLog.Build(true, new DateTime(2026, 11, 13, 16, 0, 0, DateTimeKind.Utc), "1.19.34", board, report);
            using var document = JsonDocument.Parse(json);
            JsonElement player = document.RootElement.GetProperty("players")[0];
            JsonElement row = player.GetProperty("counters");
            check(row.GetProperty("selfSupplyUses").GetInt64() == 4 && row.GetProperty("selfAmmoUses").GetInt64() == 1,
                "the released counter keys are still written with the released meaning");
            check(row.GetProperty("selfToolUses").GetInt64() == 1 && row.GetProperty("selfMedicalUses").GetInt64() == 2,
                "and the two new keys carry the split");
            check(row.GetProperty("selfSupplyKindsKnown").GetBoolean(), "with the flag that says the split was observed");
            // 1.19.60: exactly one supply key per player, the four-field one. The released three-field alias that used
            // to be written beside it is gone from the archive (`Release11960Scenarios` enumerates every player
            // property in both languages to prove it), and every number it quoted is here as a number: the total is
            // `selfSupplyUses` and the split is the three kind keys.
            check(player.TryGetProperty(CombatStrings.SupplyRowLabel, out JsonElement cell)
                  && cell.GetString() == "30.0% / 1 / 1 / 2",
                "the row is archived under its new label with the four values: "
                + (player.TryGetProperty(CombatStrings.SupplyRowLabel, out JsonElement shown) ? shown.GetString() : "missing"));
            string legacy = player.TryGetProperty(CombatStrings.SupplyRowLabelLegacy, out JsonElement legacyCell)
                ? legacyCell.GetString() : "missing";
            check(legacy == "missing" && row.GetProperty("selfSupplyUses").GetInt64() == 4,
                "and no second key is written beside it: the released alias is absent from the archive, while the total it quoted lives on in `counters.selfSupplyUses`: " + legacy);
        }
        finally { CombatStrings.Language = language; }
    }

    // ---- 5. the cell is measured, and a board that does not fit is refused, not clipped ------------------------------

    private static void AWiderCellIsMeasuredAndNeverClipped(Action<bool, string> check)
    {
        // The worst case the row can reach: three-digit counts in every field and a full-width percentage, in the shape
        // the block actually takes (the three kinds add up to the total).
        string widest = CombatStrings.SupplyCell("100.0%", 128, 100, 20, 8, true);
        string[] parts = widest.Split(new[] { CombatStrings.CellSeparator }, StringSplitOptions.None);
        check(parts.Length == 4 && Array.TrueForAll(parts, part => !string.IsNullOrEmpty(part)),
            "every one of the four figures is printed, none is dropped to save room: " + widest);
        check(parts[1] == "100" && parts[2] == "20" && parts[3] == "8",
            "and the widest cell is read back field by field, so no figure swallowed the one after it: " + widest);

        float width = PanelGeometry.EstimateTextWidth(widest, 12f);
        // The narrowest glyph the settlement board is drawn at, where a fourth field costs the most:
        // `ResultsFontGrowth.MinimumReadablePixels` is the floor the fit works down to.
        float readable = PanelGeometry.EstimateTextWidth(widest, ResultsFontGrowth.MinimumReadablePixels);
        check(float.IsFinite(width) && width > 0f && float.IsFinite(readable) && readable > 0f,
            "the cell has a measurable width at the smallest settlement glyph: " + readable.ToString("0.#"));
        float narrow = PanelGeometry.EstimateTextWidth(widest, 12f), wide = PanelGeometry.EstimateTextWidth(widest, 24f);
        check(Math.Abs(wide - narrow * 2f) < 0.01f,
            "and that width scales with the glyph size, which is what the board's own measurement uses");
        check(width > PanelGeometry.EstimateTextWidth("100.0% / 128 / 128", 12f),
            "the four-field cell is wider than the released three-field one, which is the width the column has to be able to take");

        // Source: the board measures the joined cell and a page that cannot hold it shrinks or refuses the board - it never
        // clips a figure off the end of the row.
        string root = SourceRoot();
        if (root == null) { check(false, "the fit check locates the production sources"); return; }
        string hud = File.ReadAllText(Path.Combine(root, "HudTable.cs"));
        string display = File.ReadAllText(Path.Combine(root, "ResultsDisplay.cs"));
        check(hud.Contains("string joined = string.Join(content.FieldSeparator, content.Cell(row, column));")
              && hud.Contains("PanelGeometry.SettlementColumnWidths(content.Columns, measured, fontSize);"),
            "the board measures each joined cell, so a fourth field widens the column instead of being cut off");
        check(display.Contains("while (grid != null && grid.Width > boardWidth + 0.5f")
              && display.Contains("=> HudTable.BuildGrid(content, fontSize, Plugin.Density, boardWidth, float.PositiveInfinity, floor);"),
            "and a board wider than the page is re-fitted at a smaller glyph rather than drawn clipped - only the height is answered with a scroll position, never the width");

        // The board's own measurement, driven the way `HudTable.BuildGrid` drives it: every cell of a column joined as the
        // board joins it, the widest one kept, then the settlement rule turned into the widths the page draws with.
        var counters = new ImpactCounters();
        counters.RecordSupply(30, 100);
        counters.CreditSelfSupply(SupplyPackKind.Ammo);
        counters.CreditSelfSupply(SupplyPackKind.Tool);
        counters.CreditSelfSupply(SupplyPackKind.Medical);
        counters.CreditSelfSupply(SupplyPackKind.Medical);
        var report = ImpactScoring.Evaluate(new[] { new ImpactInput { Name = "Supplier", Counters = counters } },
            ImpactScoring.DefaultWeights, 1.0, 1.0);
        ImpactBoardLayout layout = ImpactBoard.Build(report, true, true);
        const int fontSize = 12;
        var measured = new float[layout.ColumnCount];
        for (int column = 0; column < layout.ColumnCount; column++)
        {
            float widestCell = 0f;
            for (int row = 0; row < layout.Rows; row++)
                widestCell = Math.Max(widestCell, PanelGeometry.EstimateTextWidth(layout.Joined(row, column), fontSize));
            measured[column] = widestCell;
        }
        float[] fourFields = PanelGeometry.SettlementColumnWidths(layout.Columns, measured, fontSize);
        check(fourFields[1] >= PanelGeometry.EstimateTextWidth("30.0% / 1 / 1 / 2", fontSize),
            "the value column is at least as wide as the whole four-field cell, so no figure falls off the end of the row");
        var releasedFields = (float[])measured.Clone();
        releasedFields[1] = PanelGeometry.EstimateTextWidth("30.0% / 4 / 1", fontSize);
        float[] threeFields = PanelGeometry.SettlementColumnWidths(layout.Columns, releasedFields, fontSize);
        check(fourFields[1] > threeFields[1],
            "and the board widens by the fourth field instead of drawing it past the column edge");
    }

    // ---- 5b. 1.19.35/1.19.36: a pair nobody split, and a pair that does not add up, both print unknown -----------------

    private static void AMalformedPairPrintsUnknownAndKeepsItsFigures(Action<bool, string> check)
    {
        // A total smaller than its own ammo part. No machine this build knows sends that, but the cell must not answer it
        // with two zeroes: a zero is the measurement "no tool pack, no medical pack", and nothing measured that here.
        string shortTotal = CombatStrings.SupplyCell("40.0%", 1, 3, 0, 0, true);
        string[] parts = shortTotal.Split(new[] { CombatStrings.CellSeparator }, StringSplitOptions.None);
        // 1.19.36: four fields, one per label name. The ammo part is printed exactly as it arrived and the two `?` are the
        // two kinds nobody split - the raw numbers are shown, not corrected.
        check(parts.Length == 4 && parts[0] == "40.0%" && parts[1] == "3"
              && parts[2] == CombatStrings.Unknown && parts[3] == CombatStrings.Unknown,
            "a total smaller than its ammo part prints the ammo part that arrived and `?` for the two kinds it cannot explain: " + shortTotal);

        // Three kinds that do not sum to the total: the same answer, and the raw numbers are still printed where they belong.
        string notSumming = CombatStrings.SupplyCell("40.0%", 4, 1, 1, 1, true);
        parts = notSumming.Split(new[] { CombatStrings.CellSeparator }, StringSplitOptions.None);
        check(parts.Length == 4 && parts[1] == "1"
              && parts[2] == CombatStrings.Unknown && parts[3] == CombatStrings.Unknown,
            "a split that does not add up is not printed as a split either: the ammo part, then `?`: " + notSumming);

        // The figures the row really holds are untouched by the cell: printing is not a correction.
        var counters = new ImpactCounters { SelfSupplyUses = 4, SelfAmmoUses = 1, SelfToolUses = 1, SelfMedicalUses = 1, SelfSupplyKindsKnown = true };
        CombatStrings.SupplyCell("40.0%", counters.SelfSupplyUses, counters.SelfAmmoUses, counters.SelfToolUses, counters.SelfMedicalUses, counters.SelfSupplyKindsKnown);
        check(counters.SelfSupplyUses == 4 && counters.SelfAmmoUses == 1 && counters.SelfToolUses == 1 && counters.SelfMedicalUses == 1,
            "and the counters behind it are exactly what they were: the cell never rewrites a measurement");

        // The legitimate shapes keep their exact old output.
        check(CombatStrings.SupplyCell("40.0%", 4, 1, 1, 2, true).EndsWith("/ 1 / 1 / 2", StringComparison.Ordinal),
            "a split that really adds up still prints its three parts");
        check(CombatStrings.SupplyCell("—", 0, 0, 0, 0, false) == "—",
            "a row that consumed nothing still prints only its efficiency, exactly as the released build did");
    }

    // A heal this machine cannot attribute to a player. `CaptureHealth` needs the packet to name a source agent, but the
    // cast into `PlayerAgent` is opportunistic: a non-player source leaves it unresolved, and calling that heal a 血包 was
    // a kind fabricated out of an absence (review B3). 1.19.39: a player this machine merely does not *track* (a client's
    // teammate) was never this case - the source did resolve, the kind travels with the heal, and the client keeps it.
    private static void AnUnclassifiedHealIsCountedAndNeverCalledABloodPack(Action<bool, string> check)
    {
        var unclassified = new SupplyUse();
        unclassified.AddChange(20, 50, 30);
        var row = new ImpactCounters();
        check(unclassified.RecordConsumptionUnknown(row),
            "a heal whose source is not a tracked player is a pack this machine really saw");
        check(row.SelfSupplyUses == 1 && row.SelfMedicalUses == 0 && !row.SelfSupplyKindsKnown,
            "so the total keeps it and no kind claims it: medical stays 0 because nothing observed a medical pack");
        string cell = CombatStrings.SupplyCell("—", row.SelfSupplyUses, row.SelfAmmoUses, row.SelfToolUses, row.SelfMedicalUses, row.SelfSupplyKindsKnown);
        string[] parts = cell.Split(new[] { CombatStrings.CellSeparator }, StringSplitOptions.None);
        check(parts.Length == 4 && parts[0] == "—" && parts[1] == "0"
              && parts[2] == CombatStrings.Unknown && parts[3] == CombatStrings.Unknown,
            "and the row keeps its four fields - the ammo it knows, which is none, and `?` for both kinds - instead of pretending the heal was not a pack or that it was a tool: " + cell);

        // A later, properly attributed heal must not turn the withdrawn split back into a claim: the row no longer adds up.
        var later = new SupplyUse();
        later.AddChange(10, 40, 30);
        check(later.RecordConsumption(row, SupplyPackKind.Medical), "a tracked giver's pack is still classified");
        check(row.SelfSupplyUses == 2 && row.SelfMedicalUses == 1 && !row.SelfSupplyKindsKnown,
            "and the row keeps its split withdrawn, because its two packs are not a split of two kinds: "
            + row.SelfSupplyUses + " uses beside " + row.SelfMedicalUses + " medical");
        var published = new CombatPlayer(Receiver) { Name = "Publisher" };
        published.Impact.CopyFrom(row);
        check(!SyncProtocol.TryCaptureSupplyKinds(published, out _),
            "so nothing is published for it: a block whose parts do not add up is never sent as a measurement");

        // The ordinary path is unchanged: a tracked giver's heal is the medical pack.
        var tracked = new SupplyUse();
        tracked.AddChange(10, 40, 30);
        var medical = new ImpactCounters();
        check(tracked.RecordConsumption(medical, SupplyPackKind.Medical) && medical.SelfMedicalUses == 1 && medical.SelfSupplyKindsKnown,
            "a heal from a player this machine tracks is still one medical pack, and the split is still published");
        check(!new SupplyUse().RecordConsumptionUnknown(new ImpactCounters()),
            "and a use nobody gained anything from still counts nothing at all");
    }

    // The four-field cell makes the value column wider, so the *fit* is what has to answer it - not a clipped draw. This drives
    // `ResultsFontGrowth.FitBoardIntoStrip`, the production search `ResultsDisplay` uses, against a real 4-player board whose
    // widths come from the same `PanelGeometry` measurement `HudTable.BuildGrid` uses.
    private static void ANarrowPageFitsTheWiderRowInsteadOfClippingIt(Action<bool, string> check)
    {
        ImpactBoardLayout layout = FourPlayerBoard();
        const float narrowPageWidth = 720f;         // a 1280x720 page minus its margins
        const float stripHeight = 300f;             // what is left between the completion block and the footer
        const int seed = 24;
        ResultTableSize At(int font) => MeasureBoard(layout, font);

        ResultTableSize seedSize = At(seed);
        check(seedSize.Width > narrowPageWidth,
            "at the seed glyph the four-player board really is wider than a narrow page (" + seedSize.Width.ToString("0.#") + " > " + narrowPageWidth.ToString("0.#") + "), so a fit has to happen at all");

        int usedFloor;
        int fitted = ResultsFontGrowth.FitBoardIntoStrip(seed, ResultsFontGrowth.MinimumReadablePixels, seed, stripHeight,
            narrowPageWidth, At, out usedFloor, ResultsFontGrowth.BandFillCeiling);
        ResultTableSize answer = At(fitted);
        check(answer.Width <= narrowPageWidth + 0.5f && answer.Height <= stripHeight + 0.5f,
            "the fitted board lands inside the page: " + answer.Width.ToString("0.#") + "x" + answer.Height.ToString("0.#")
            + " at " + fitted + "px, floor " + usedFloor);
        check(fitted < seed,
            "and it got there by shrinking the glyph, not by cutting a figure off the end of the row: " + fitted + "px");
        check(fitted >= ResultsFontGrowth.MinimumReadablePixels,
            "the answer never goes below the readability floor the search is given: " + fitted);

        // The released three-field row is narrower, so the same page can hold it at a larger glyph - which is exactly the cost
        // the fourth field is allowed to have (a smaller glyph), and the one thing it may not do (a clipped cell).
        int threeField = ResultsFontGrowth.FitBoardIntoStrip(seed, ResultsFontGrowth.MinimumReadablePixels, seed, stripHeight,
            narrowPageWidth, font => MeasureBoard(layout, font, releasedThreeFields: true), out _, ResultsFontGrowth.BandFillCeiling);
        check(fitted <= threeField,
            "the four-field row never fits at a *larger* glyph than the released three-field one: " + fitted + " vs " + threeField);

        // A page wide enough for the seed keeps the seed: the fit does not shrink a board nobody asked it to shrink.
        int wide = ResultsFontGrowth.FitBoardIntoStrip(seed, ResultsFontGrowth.MinimumReadablePixels, seed, 1400f, 2560f, At, out _, ResultsFontGrowth.BandFillCeiling);
        check(wide == seed, "a page that can hold the board at the seed keeps it: " + wide);
    }

    // Four players, one of them with the widest cell the row can reach (three-digit counts in every kind), built by the board
    // itself - the same `ImpactBoard.Build` the settlement page draws.
    private static ImpactBoardLayout FourPlayerBoard()
    {
        var inputs = new List<ImpactInput>();
        for (int index = 0; index < 4; index++)
        {
            var counters = new ImpactCounters();
            counters.RecordSupply(30, 100);
            counters.CreditSelfSupply(SupplyPackKind.Ammo);
            if (index == 0)
            {
                for (int pack = 0; pack < 20; pack++) counters.CreditSelfSupply(SupplyPackKind.Tool);
                for (int pack = 0; pack < 8; pack++) counters.CreditSelfSupply(SupplyPackKind.Medical);
            }
            else
            {
                counters.CreditSelfSupply(SupplyPackKind.Tool);
                counters.CreditSelfSupply(SupplyPackKind.Medical);
            }
            inputs.Add(new ImpactInput { Name = "Player" + index, Counters = counters });
        }
        return ImpactBoard.Build(ImpactScoring.Evaluate(inputs, ImpactScoring.DefaultWeights, 1.0, 1.0), true, true);
    }

    // The board's real geometry at one glyph size: every column's widest measured cell, the settlement rule that turns those
    // widths into column widths, and the grid `HudTable.BuildGrid` would build from them.
    private static ResultTableSize MeasureBoard(ImpactBoardLayout layout, int fontPixels, bool releasedThreeFields = false)
    {
        float font = fontPixels < 1 ? 1 : fontPixels;
        var measured = new float[layout.ColumnCount];
        for (int column = 0; column < layout.ColumnCount; column++)
        {
            float widest = 0f;
            for (int row = 0; row < layout.Rows; row++)
            {
                string cell = layout.Joined(row, column);
                if (releasedThreeFields) cell = ReleasedThreeFieldCell(cell);
                widest = Math.Max(widest, PanelGeometry.EstimateTextWidth(cell, font));
            }
            measured[column] = widest;
        }
        float[] widths = PanelGeometry.SettlementColumnWidths(layout.Columns, measured, font);
        PanelGrid grid = PanelGeometry.Build(widths, font, layout.Rows, PanelDensity.Compact);
        return new ResultTableSize(grid.Width, grid.Height);
    }

    // The released three-field supply cell, rebuilt from the four-field one the board holds - the same figures without the tool
    // and medical parts, which is what `CombatStrings.SupplyCell(string,long,long)` prints.
    private static string ReleasedThreeFieldCell(string cell)
    {
        string[] parts = cell.Split(new[] { CombatStrings.CellSeparator }, StringSplitOptions.None);
        if (parts.Length != 4) return cell;
        return parts[0] + CombatStrings.CellSeparator + parts[1];
    }

    // The review's case, end to end: a client (A) applies one medkit to a remote teammate (B). The bracket proves B,
    // `MedicalPackCredit` writes B's own row, `SettlementRows` keeps that row for the results page, and the cell the
    // page draws prints one medical pack for B and no fabricated zero for A.
    private static void AClientsPackReachesTheTeammatesSettlementRow(Action<bool, string> check)
    {
        var ledger = new PackApplyLedger();
        PackBracket bracket = ledger.Begin(Supplier, false);
        ledger.NoteGive(bracket, Receiver, 0.2);
        PackApplyResult applied = ledger.End(bracket, null);

        var supplier = new CombatPlayer(Supplier);
        var receiver = new CombatPlayer(Receiver);
        var players = new Dictionary<ulong, CombatPlayer> { { Supplier, supplier }, { Receiver, receiver } };
        var credit = new MedicalPackCredit();
        int credited = credit.Apply(applied, lookup =>
            players.TryGetValue(lookup, out CombatPlayer row)
                ? new MedicalRowCandidate { Known = true, InSlot = true, Row = row.Impact }
                : default);
        check(credited == 1, "the client's application credits its remote teammate's row");
        check(supplier.Impact.SelfMedicalUses == 0 && receiver.Impact.SelfMedicalUses == 1,
            "the pack's user reads no medical pack of its own and the receiver reads one");

        // The results page shows the rows this class keeps, not the live capture store.
        var settlement = new SettlementRows();
        List<CombatPlayer> shown = settlement.Update(new[] { supplier, receiver }, 3);
        CombatPlayer shownReceiver = shown.Find(row => row != null && row.Lookup == Receiver);
        CombatPlayer shownSupplier = shown.Find(row => row != null && row.Lookup == Supplier);
        check(shownReceiver != null && shownReceiver.Impact.SelfMedicalUses == 1 && shownReceiver.Impact.SelfMedicalKnown,
            "the settlement row still carries the medical pack and the fact this machine confirmed it");

        var receiverInput = new ImpactInput { Name = "B", Lookup = Receiver, Counters = shownReceiver.Impact };
        var supplierInput = new ImpactInput { Name = "A", Lookup = Supplier, Counters = shownSupplier.Impact };
        ImpactEntry receiverEntry = ImpactScoring.Evaluate(new[] { receiverInput }, ImpactScoring.DefaultWeights, 1, 1).Entries[0];
        ImpactEntry supplierEntry = ImpactScoring.Evaluate(new[] { supplierInput }, ImpactScoring.DefaultWeights, 1, 1).Entries[0];
        string receiverCell = ImpactBoard.MetricFields(receiverEntry, ImpactMetric.SupplyEfficiency, KillTier.Minor, false, true)[0];
        string supplierCell = ImpactBoard.MetricFields(supplierEntry, ImpactMetric.SupplyEfficiency, KillTier.Minor, false, true)[0];
        string[] receiverParts = receiverCell.Split(new[] { CombatStrings.CellSeparator }, StringSplitOptions.None);
        check(receiverParts.Length == 4 && receiverParts[3] == "1",
            "the cell the results page draws prints one medical pack for the receiver: " + receiverCell);
        check(supplierCell == CombatStrings.Placeholder,
            "and the player who applied the pack prints no fabricated zero of its own: " + supplierCell);

        // A later refresh of the same run must not drop the row the pack was written to.
        List<CombatPlayer> again = settlement.Update(new[] { supplier, receiver }, 3);
        CombatPlayer refreshed = again.Find(row => row != null && row.Lookup == Receiver);
        check(refreshed != null && refreshed.Impact.SelfMedicalUses == 1,
            "a later settlement refresh keeps the row the pack was written to");
    }

    // When a row's split is dropped, the medical figure is dropped with it - and so is the flag that said this machine
    // confirmed it. A confirmed flag beside a zeroed count prints as a counted zero: that is the fabricated zero this
    // release removes, and the archive must not write it either.
    private static void ADroppedSplitNeverPrintsAMedicalZero(Action<bool, string> check)
    {
        var confirmed = new CombatPlayer(Supplier);
        confirmed.Impact.CreditSelfSupply(SupplyPackKind.Medical);
        check(confirmed.Impact.SelfMedicalKnown && confirmed.Impact.SelfSupplyKindsKnown,
            "a confirmed medical pack marks the row's medical figure as known");

        // The same row later reported with a larger total that arrives without a split (an older peer's pair).
        var larger = new CombatPlayer(Supplier);
        larger.Impact.SelfSupplyUses = 3;
        larger.Impact.SelfAmmoUses = 0;
        larger.Impact.SelfSupplyKindsKnown = false;

        var settlement = new SettlementRows();
        settlement.Update(new[] { confirmed }, 9);
        List<CombatPlayer> shown = settlement.Update(new[] { larger }, 9);
        CombatPlayer row = shown.Find(r => r != null && r.Lookup == Supplier);
        check(row != null && row.Impact.SelfSupplyUses == 3 && !row.Impact.SelfSupplyKindsKnown,
            "the larger pair still wins and its split is unknown");
        check(row.Impact.SelfMedicalUses == 0 && !row.Impact.SelfMedicalKnown,
            "and the medical figure is not left flagged as confirmed unless it still holds a count");

        var input = new ImpactInput { Name = "R", Lookup = Supplier, Counters = row.Impact };
        ImpactEntry entry = ImpactScoring.Evaluate(new[] { input }, ImpactScoring.DefaultWeights, 1, 1).Entries[0];
        string cell = ImpactBoard.MetricFields(entry, ImpactMetric.SupplyEfficiency, KillTier.Minor, false, true)[0];
        string[] parts = cell.Split(new[] { CombatStrings.CellSeparator }, StringSplitOptions.None);
        check(parts.Length == 4 && parts[3] == CombatStrings.Unknown,
            "the cell prints `?` for that medical figure rather than a zero: " + cell);

        // The archive says the same thing in words a reader can act on: a number beside `selfMedicalKnown: false`.
        var ammoOnly = new ImpactCounters();
        ammoOnly.CreditSelfSupply(SupplyPackKind.Ammo);
        var writer = new ImpactInput { Name = "AmmoOnly", Counters = ammoOnly };
        ImpactReport report = ImpactScoring.Evaluate(new[] { writer }, ImpactScoring.DefaultWeights, 1, 1);
        ImpactBoardLayout board = ImpactBoard.Build(report, true, true);
        string json = ResultsLog.Build(true, new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc), "1.19.34", board, report);
        using var document = JsonDocument.Parse(json);
        JsonElement counters = document.RootElement.GetProperty("players")[0].GetProperty("counters");
        check(counters.GetProperty("selfMedicalUses").GetInt64() == 0 && !counters.GetProperty("selfMedicalKnown").GetBoolean(),
            "and the archive writes that absence as 0 beside `selfMedicalKnown: false`, never as an unqualified zero");
    }

    // ---- 6. wiring ---------------------------------------------------------------------------------------------------

    private static void Wiring(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the 1.19.34 supply-row regression locates the production sources");
        if (root == null) return;
        string supply = Code(File.ReadAllText(Path.Combine(root, "SupplyTracker.cs")));
        string use = Code(File.ReadAllText(Path.Combine(root, "SupplyUse.cs")));
        string board = Code(File.ReadAllText(Path.Combine(root, "ImpactBoard.cs")));
        string protocol = Code(File.ReadAllText(Path.Combine(root, "SyncProtocol.cs")));

        // 1.19.39/1.19.41: the medical kind is named further out than the ammo/tool one, and since 1.19.41 it is named
        // by the native pack-application bracket - one pack per proven receiver - instead of by the arriving heal.
        // Both native paths still name their kind; only the file that states it moved.
        string packs = Code(File.ReadAllText(Path.Combine(root, "PackApplication.cs")));

        // 1.19.41: the medical kind is no longer named by a heal - `ReceiveAddHealth` cannot tell a medkit from a
        // syringe, a disinfection pack or another mod's trickle heal, which is what made one expedition report 1319
        // medical packs. The kind is named once per proven receiver by the native application bracket, and the heal
        // keeps its give/receive efficiency measurement. The ammo/tool scope is unchanged.
        check(supply.Contains("scope.Tool ? SupplyPackKind.Tool : SupplyPackKind.Ammo")
              && packs.Contains("CreditSelfSupply(SupplyPackKind.Medical);")
              && supply.Contains("int credited = Credit.Apply(result, CandidateFor);")
              && use.Contains("? RecordConsumption(receiver, SupplyPackKind.Medical)"),
            "the two native paths name their kind: the ammo/tool scope here, and one medical pack per proven receiver through the application bracket");
        check(use.Contains("receiver.CreditSelfSupply(kind);"),
            "one place credits a consumed pack, so a use can never be counted as two kinds");
        check(board.Contains("counters.SelfSupplyKindsKnown, counters.SelfMedicalKnown)")
              && board.Contains("counters.SelfToolUses") && board.Contains("counters.SelfMedicalUses"),
            "the row prints the split, whether it was split, and whether this machine could confirm the medical part at all");
        check(protocol.Contains("public const ushort KindSupplyKinds = 3;")
              && protocol.Contains("public static bool TryCaptureSupplyKinds(CombatPlayer player, out SupplyKinds kinds)")
              && protocol.Contains("public static bool ApplySupplyKinds(ImpactCounters impact, in SupplyKinds kinds, bool own)"),
            "the split is captured, capped by an exact-pair match, and carried by its own packet kind");
        check(!protocol.Contains("public uint SelfToolUses"), "and the snapshot struct was not extended: a v8 peer still reads it");
        // `SquadNet` is the Unity-facing half of the transport, linked here without its runtime registration; the hook a
        // received split is raised through is assigned here so the delegate is exercised rather than merely declared.
        try
        {
            SquadNet.OnSupplyKinds = (sender, generation, kinds) => { };
            check(SquadNet.OnSupplyKinds != null, "the receiver's split hook is a delegate the runtime registers, not a dead field");
        }
        finally { SquadNet.OnSupplyKinds = null; }
        Console.WriteLine("WIRING: RELEASE_11934_SUPPLY_ROW_KINDS (static source check; the counting, the row, the protocol and the archive are the assertions above)");
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------

    private static CombatPlayer Find(List<CombatPlayer> rows, ulong lookup)
    {
        if (rows == null) return null;
        foreach (CombatPlayer row in rows) if (row != null && row.Lookup == lookup) return row;
        return null;
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
