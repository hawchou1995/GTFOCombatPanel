using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

namespace HawChow.GTFOCombatPanel.SupplyTests;

// Regression cover for the ammo supply giver that is missing on a client: `pAmmoGive` names the receiver
// only and the authority relays it, so the giver has to come from the give the pack user's own machine
// made. Everything SupplyTracker decides is delegated to SupplyUse.cs and is asserted here - including which kind a
// giver-sourced heal was, on a client whose tracking rule only ever covers its own row; the native callbacks themselves
// (GiveAmmoRel/ReceiveAmmoGive/PickupAmmo/ReceiveAddHealth) can only be exercised in a live session, so the lines that
// wire the health kind are read as source in the last scenario.
internal static class SupplyGiverScenarios
{
    private const ulong Client = 0x2BUL;
    private const ulong Host = 0x11UL;
    private const ulong Mate = 0x3CUL;

    private static int _assertions;

    private static void Main()
    {
        try
        {
            SenderRule();
            SelfUseOnAClientIsCredited();
            OneGiveIsOneUse();
            AnotherPlayersGiveIsNeverClaimed();
            NestedAuthorityScopeWinsAndRetiresTheCopy();
            RecordsExpire();
            SenderIsOnlyAnAuthorityFallback();
            UnusableGivesAreRefused();
            LedgerStaysBounded();
            OverflowAndBackwardsReadings();
            HealthKindFollowsThePacketNotTheObserver();
            OneApplicationIsOnePack();
            MedicalCreditGoesToTheReceiver();
            ClientCreditsARemoteTeammateRow();
            AGiveFromElsewhereIsNotThisPacksHealth();
            AnUnattributableApplicationStillCloses();
            NestedAndThrowingApplicationsReleaseTheirBracket();
            AGroupApplicationIsOneItemAndOnePackPerReceiver();
            AnEmptyApplicationIsNoPack();
            AHealWithoutAnApplicationIsNeverAPack();
            DisinfectionIsExcludedFromThePackCount();
            AnApplicationThatThrewIsNoPack();
            NativeUnitsFollowTheSlotCap();
            TheApplicationBracketIsWiredIntoTheTracker();
            TheHealthKindRuleIsWiredIntoTheTracker();
            // The registration itself: the pack bracket's finalizer has to reach Harmony's *finalizer* slot, not
            // its transpiler slot - the recorded Survival run's `resolved=2/4` and TargetParameterCountException.
            HarmonyPatchSlotScenarios.Run(Check);
            Console.WriteLine($"PASS {_assertions} supply giver assertions");
        }
        catch (Exception error)
        {
            Console.WriteLine(error.Message);
            Environment.Exit(1);
        }
    }

    // The delivery's sender is not a giver on a client, not the authority itself, and not a player who is
    // not in a slot.
    private static void SenderRule()
    {
        Check(!SupplyGiverRule.SenderAllowed(false, false, true), "a client never treats the master that forwarded the packet as the giver");
        Check(!SupplyGiverRule.SenderAllowed(true, true, true), "the authority never credits itself for an action it only performed or forwarded");
        Check(!SupplyGiverRule.SenderAllowed(true, false, false), "a sender that is not in a slot is not a player who could have opened a pack");
        Check(SupplyGiverRule.SenderAllowed(true, false, true), "the authority may read the asking client out of a delivery it did not forward");
    }

    // The reported defect: a remote pack user showed 47 own uses and no efficiency, because the packet
    // named only the receiver and the client had nothing else to go on.
    private static void SelfUseOnAClientIsCredited()
    {
        var ledger = new SupplyGiveLedger();
        var giver = new ImpactCounters();
        var receiver = new ImpactCounters();
        SupplySenderCandidate forwarded = new SupplySenderCandidate { Lookup = Host, Allowed = SupplyGiverRule.SenderAllowed(false, false, true) };

        Check(!ledger.TryResolve(Client, 0.5, 0.25, 0, 10.0, 0UL, forwarded, out ulong unknown, out SupplyGiverSource unknownSource)
              && unknown == 0UL && unknownSource == SupplyGiverSource.None,
            "a delivery this machine never gave resolves to no giver, so no efficiency can be credited");

        Check(ledger.Register(Client, Client, 0.5, 0.25, 0, 10.0), "the pack user's own machine records the give it made");
        Check(ledger.TryResolve(Client, 0.5, 0.25, 0, 10.05, 0UL, forwarded, out ulong resolved, out SupplyGiverSource source)
              && resolved == Client && source == SupplyGiverSource.Give,
            "the pickup the client applies to itself is matched back to that give instead of the forwarding host");

        var use = new SupplyUse();
        use.AddChange(100, 120, 20);   // standard: the full share arrived
        use.AddChange(30, 40, 20);     // special: half of the share fit
        Check(use.Record(giver), "the matched use is credited to the pack user");
        Check(use.RecordConsumption(receiver, SupplyPackKind.Ammo), "the benefiting player still earns the consumption credit");
        Check(giver.SupplyUses == 1 && Math.Abs(giver.SupplyEfficiency - 0.75) < 1e-9, "both channels of one give form one utilization sample");
        Check(receiver.SelfSupplyUses == 1 && receiver.SelfAmmoUses == 1 && receiver.SupplyUses == 0, "efficiency belongs to the giver, consumption to the receiver");
    }

    // One native give, one use: the record is claimed once, and a re-delivered or duplicated packet cannot
    // add a second sample.
    private static void OneGiveIsOneUse()
    {
        var ledger = new SupplyGiveLedger();
        SupplySenderCandidate none = default;
        Check(ledger.Register(Client, Client, 1, 0, 0, 20.0), "one ammo pack give is recorded");
        Check(ledger.TryResolve(Client, 1, 0, 0, 20.01, 0UL, none, out _, out SupplyGiverSource first) && first == SupplyGiverSource.Give,
            "its pickup is claimed once");
        Check(!ledger.TryResolve(Client, 1, 0, 0, 20.02, 0UL, none, out ulong again, out SupplyGiverSource second)
              && again == 0UL && second == SupplyGiverSource.None,
            "a second delivery with no remembered give of its own is never credited as the same use");

        var counters = new ImpactCounters();
        var use = new SupplyUse();
        use.AddChange(10, 35, 25);
        Check(use.Record(counters) && !use.Record(counters) && counters.SupplyUses == 1, "one use cannot be committed twice");
        var fittedNowhere = new SupplyUse();
        fittedNowhere.AddChange(50, 50, 25);
        Check(!fittedNowhere.RecordConsumption(new ImpactCounters(), SupplyPackKind.Ammo),
            "a use in which the receiver gained nothing claims no consumption (a full player)");

        var tool = new SupplyGiveLedger();
        Check(tool.Register(Mate, Client, 0, 0, 1, 30.0), "a tool refill is a give of its own");
        Check(tool.TryResolve(Client, 0, 0, 1, 30.01, 0UL, none, out ulong toolGiver, out _) && toolGiver == Mate,
            "a tool refill credits the player who opened the tool pack");
    }

    // Two pack users in the same squad: matching on the receiver alone would hand one player's give to the
    // other, so the three shares have to agree as well.
    private static void AnotherPlayersGiveIsNeverClaimed()
    {
        var ledger = new SupplyGiveLedger();
        SupplySenderCandidate none = default;
        Check(ledger.Register(Client, Client, 0.5, 0.25, 0, 40.0), "the client's own give is remembered");
        Check(!ledger.TryResolve(Client, 0.25, 0, 0, 40.05, 0UL, none, out ulong other, out SupplyGiverSource source)
              && other == 0UL && source == SupplyGiverSource.None,
            "a different give to the same player does not claim it and is not credited to the earlier pack user");
        Check(ledger.Count == 1, "the remembered give is still there for the delivery it actually belongs to");
        Check(!ledger.TryResolve(Mate, 0.5, 0.25, 0, 40.06, 0UL, none, out _, out _), "a give is only ever matched to the receiver it named");
    }

    // On the authority the give and its application can be nested. The nested answer wins and the
    // remembered copy is retired, so a later packet cannot reuse it.
    private static void NestedAuthorityScopeWinsAndRetiresTheCopy()
    {
        var ledger = new SupplyGiveLedger();
        SupplySenderCandidate none = default;
        Check(ledger.Register(Host, Mate, 0.75, 0, 0, 50.0), "the authority records its own give");
        Check(ledger.TryResolve(Mate, 0.75, 0, 0, 50.0, Host, none, out ulong scoped, out SupplyGiverSource source)
              && scoped == Host && source == SupplyGiverSource.Scope,
            "the nested give that is still running names the giver");
        Check(ledger.Count == 0, "the remembered copy is retired with it");
        Check(!ledger.TryResolve(Mate, 0.75, 0, 0, 50.4, 0UL, none, out ulong later, out SupplyGiverSource laterSource)
              && later == 0UL && laterSource == SupplyGiverSource.None,
            "a later delivery cannot inherit the retired copy");
    }

    private static void RecordsExpire()
    {
        var ledger = new SupplyGiveLedger();
        SupplySenderCandidate none = default;
        Check(ledger.Register(Client, Client, 0.5, 0, 0, 60.0), "a give that is never applied is still remembered at first");
        Check(ledger.Register(Client, Client, 0.5, 0, 0, 60.0 + SupplyGiveLedger.MaxAgeSeconds + 0.01), "a stale record is pruned before the next one is kept");
        Check(ledger.Count == 1, "only the live record survives");
        Check(!ledger.TryResolve(Client, 0.5, 0, 0, 60.0 + 2 * SupplyGiveLedger.MaxAgeSeconds + 0.02, 0UL, none, out ulong expired, out SupplyGiverSource source)
              && expired == 0UL && source == SupplyGiverSource.None,
            "a delivery that arrives after the window is not credited from an old give");
    }

    private static void SenderIsOnlyAnAuthorityFallback()
    {
        var ledger = new SupplyGiveLedger();
        Check(ledger.TryResolve(Mate, 0.4, 0, 0, 70.0, 0UL, new SupplySenderCandidate { Lookup = Client, Allowed = true }, out ulong giver, out SupplyGiverSource source)
              && giver == Client && source == SupplyGiverSource.Sender,
            "the authority credits the client that asked for the give");
        Check(!ledger.TryResolve(Mate, 0.4, 0, 0, 70.1, 0UL, new SupplySenderCandidate { Lookup = Host, Allowed = false }, out _, out SupplyGiverSource refused)
              && refused == SupplyGiverSource.None,
            "a refused sender resolves to nothing rather than to the host");

        Check(ledger.Register(Client, Mate, 0.4, 0, 0, 70.2), "the authority also remembers a give it made itself");
        Check(ledger.TryResolve(Mate, 0.4, 0, 0, 70.3, 0UL, new SupplySenderCandidate { Lookup = Host, Allowed = true }, out ulong own, out SupplyGiverSource ownSource)
              && own == Client && ownSource == SupplyGiverSource.Give,
            "the give this machine made outranks a stale delivery sender");
    }

    private static void UnusableGivesAreRefused()
    {
        var ledger = new SupplyGiveLedger();
        Check(!ledger.Register(0UL, Client, 1, 0, 0, 80.0), "a give without a giver is never remembered");
        Check(!ledger.Register(Client, 0UL, 1, 0, 0, 80.0), "a give without a receiver is never remembered");
        Check(!ledger.Register(Client, Client, 0, 0, 0, 80.0), "a give that filled nothing is never remembered");
        Check(!ledger.Register(Client, Client, double.NaN, 0, 0, 80.0), "an unreadable share is never remembered");
        Check(!ledger.Register(Client, Client, -1, 0, 0, 80.0), "a negative share is never remembered");
        Check(!ledger.Register(Client, Client, 1, 0, 0, double.NaN), "a give without a clock reading is never remembered");
        Check(ledger.Count == 0, "nothing unusable is kept");
        Check(!ledger.TryResolve(0UL, 1, 0, 0, 80.0, 0UL, default, out _, out SupplyGiverSource source) && source == SupplyGiverSource.None,
            "a delivery without a receiver resolves to nothing");
        Check(!ledger.TryResolve(Client, 1, 0, 0, double.NaN, 0UL, default, out _, out _), "a delivery without a clock reading resolves to nothing");
        ledger.Clear();
        Check(ledger.Count == 0, "a new expedition clears the remembered gives");
    }

    private static void LedgerStaysBounded()
    {
        var ledger = new SupplyGiveLedger();
        for (int i = 0; i < SupplyGiveLedger.Capacity * 3; i++) ledger.Register(Client, Client, 1, 0, 0, 90.0 + i * 0.001);
        Check(ledger.Count == SupplyGiveLedger.Capacity, "the remembered gives never grow without bound");
        SupplySenderCandidate none = default;
        Check(ledger.TryResolve(Client, 1, 0, 0, 90.06, 0UL, none, out ulong giver, out _) && giver == Client, "the oldest live give is claimed first");
    }

    // Effective amount is what actually moved, never what the packet asked for: overflow is 0% of the
    // share it could not use, and a reading that went backwards invalidates the whole use.
    private static void OverflowAndBackwardsReadings()
    {
        var full = new ImpactCounters();
        var overflow = new SupplyUse();
        overflow.AddChange(100, 100, 20);
        Check(overflow.Record(full) && full.SupplyUses == 1 && full.SupplyEfficiency == 0,
            "a share that fitted nowhere is one 0% sample, because the documented contract counts overflow as zero rather than as no use");

        var part = new ImpactCounters();
        var partial = new SupplyUse();
        partial.AddChange(90, 95, 20);
        Check(partial.Record(part) && Math.Abs(part.SupplyEfficiency - 0.25) < 1e-9, "only the units that actually moved count as effective");

        var broken = new ImpactCounters();
        var backwards = new SupplyUse();
        backwards.AddChange(40, 60, 20);
        backwards.AddChange(70, 50, 20);
        Check(!backwards.Record(broken) && broken.SupplyUses == 0, "one unreadable channel invalidates the use instead of overstating it");

        var mean = new ImpactCounters();
        var once = new SupplyUse(); once.AddChange(0, 20, 20);
        var half = new SupplyUse(); half.AddChange(0, 10, 20);
        once.Record(mean); half.Record(mean);
        Check(mean.SupplyUses == 2 && Math.Abs(mean.SupplyEfficiency - 0.75) < 1e-9, "uses are averaged, not summed");
    }

    // The reported defect: on a client `CombatTracker.ShouldTrack` is `isInSlot && (isMaster || isLocal)`, so it is true
    // for the client's own row and for nobody else. `CompleteHealth` asked that test about the *giver* to decide the
    // pack's *kind*, so a teammate's health pack - a `pAddHealthData.source` that really does resolve to a
    // `PlayerAgent` - was counted as a pack of no name on the client, while the host called the same heal a medical
    // pack. One event, two answers. The kind rule's only input is what the packet said about its source, so the four
    // ownership cases a session produces are asserted against that one rule here: they cannot disagree any more.
    private static void HealthKindFollowsThePacketNotTheObserver()
    {
        // A client, a remote giver (host or teammate), the heal landing on the client's own row.
        var clientRow = new ImpactCounters();
        var teammateHeal = new SupplyUse();
        teammateHeal.AddChange(20.0, 50.0, 30.0);
        Check(teammateHeal.RecordHealthConsumption(clientRow, true),
            "a client counts the teammate's health pack it consumed");
        Check(clientRow.SelfSupplyUses == 1 && clientRow.SelfMedicalUses == 1 && clientRow.SelfAmmoUses == 0
              && clientRow.SelfToolUses == 0 && clientRow.SelfSupplyKindsKnown,
            "and the kind is the one the packet named: one medical pack whose split describes the total, not the `?` a client used to print for a giver outside its own row");

        // A client, its own giver: the ordinary path, which the fix must not disturb.
        var ownRow = new ImpactCounters();
        var ownHeal = new SupplyUse();
        ownHeal.AddChange(10.0, 40.0, 30.0);
        Check(ownHeal.RecordHealthConsumption(ownRow, true) && ownRow.SelfMedicalUses == 1 && ownRow.SelfSupplyKindsKnown,
            "the client's own health pack is still one medical pack with the split intact");

        // The authority, whose heal to a teammate also resolves to a player source: the same answer as the client.
        var hostRow = new ImpactCounters();
        var hostHeal = new SupplyUse();
        hostHeal.AddChange(5.0, 30.0, 30.0);
        Check(hostHeal.RecordHealthConsumption(hostRow, true) && hostRow.SelfMedicalUses == 1 && hostRow.SelfSupplyKindsKnown,
            "and the host answers the same way about the same heal, which is what makes the two machines agree at all");

        // The source resolved, but not to a player (a non-player healer): a use this machine really measured, of a kind
        // it cannot claim.
        var unnamedRow = new ImpactCounters();
        var nonPlayerHeal = new SupplyUse();
        nonPlayerHeal.AddChange(20.0, 50.0, 30.0);
        Check(nonPlayerHeal.RecordHealthConsumption(unnamedRow, false),
            "a heal whose source is not a player agent is a use this machine measured");
        Check(unnamedRow.SelfSupplyUses == 1 && unnamedRow.SelfMedicalUses == 0 && !unnamedRow.SelfSupplyKindsKnown,
            "so the total keeps it and no kind claims it: the unknown stays unknown instead of being promoted to a medical pack");
        string unnamedCell = CombatStrings.SupplyCell(CombatStrings.Placeholder, unnamedRow.SelfSupplyUses,
            unnamedRow.SelfAmmoUses, unnamedRow.SelfToolUses, unnamedRow.SelfMedicalUses, unnamedRow.SelfSupplyKindsKnown);
        Check(unnamedCell.EndsWith("/ 0 / " + CombatStrings.Unknown + " / " + CombatStrings.Unknown, StringComparison.Ordinal),
            "and the cell prints the ammo it knows (a counted zero) beside `?` for both kinds, never a zero that would claim no pack: " + unnamedCell);

        // A source that never resolved never reaches a measurement at all, so it consumes nothing.
        var unresolvedRow = new ImpactCounters();
        var unmeasured = new SupplyUse();
        Check(!unmeasured.RecordHealthConsumption(unresolvedRow, false) && unresolvedRow.SelfSupplyUses == 0
              && !unresolvedRow.SelfSupplyKindsKnown,
            "a heal whose source never resolved is not measured, so it counts no pack of any kind and fabricates no split");

        // One use, one pack: the second callback on the same use counts nothing, whichever answer it gave.
        Check(!nonPlayerHeal.RecordHealthConsumption(unnamedRow, false) && unnamedRow.SelfSupplyUses == 1,
            "one use is one pack: a repeated callback on an unnamed use cannot add a second");
        Check(!teammateHeal.RecordHealthConsumption(clientRow, true) && clientRow.SelfSupplyUses == 1,
            "and the same holds for a named one");

        // A row that counted both keeps the kind it can name and withdraws the split: the unknown is never written as 0.
        var mixedRow = new ImpactCounters();
        var named = new SupplyUse(); named.AddChange(10.0, 40.0, 30.0);
        var unnamed = new SupplyUse(); unnamed.AddChange(10.0, 40.0, 30.0);
        Check(named.RecordHealthConsumption(mixedRow, true) && unnamed.RecordHealthConsumption(mixedRow, false),
            "a row that saw a named heal and an unnamed one counts both");
        Check(mixedRow.SelfSupplyUses == 2 && mixedRow.SelfAmmoUses == 0 && mixedRow.SelfMedicalUses == 1
              && !mixedRow.SelfSupplyKindsKnown,
            "keeps the medical pack it can name and withdraws the split, instead of printing the unnamed pack as a zero of some kind: "
            + mixedRow.SelfSupplyUses + " uses beside " + mixedRow.SelfMedicalUses + " medical");
    }

    // 1.19.41: one native pack application is one pack, however many teammates it heals. The recorded expedition
    // reported 1319 medical packs against ~1114 units of health that could possibly have been lost, because a
    // heal (`ReceiveAddHealth`) was counted as a pack. This is the shape that replaces it: a bracket, its gives,
    // and the receivers inside it deduplicated.
    private static void OneApplicationIsOnePack()
    {
        var packs = new PackApplyLedger();
        PackBracket bracket = packs.Begin(Client, false);
        Check(packs.NoteGive(bracket, Host, 0.2), "the first receiver of one application is one give");
        Check(packs.NoteGive(bracket, Mate, 0.2), "a second receiver is a second give of the same pack");
        Check(packs.NoteGive(bracket, 0x4DUL, 0.2), "a third receiver is a third give of the same pack");
        Check(!packs.NoteGive(bracket, Host, 0.2), "the same receiver inside one application is one give, not two");
        PackApplyResult result = packs.End(bracket, null);
        Check(result.Outcome == PackApplyOutcome.Applied, "an application that healed somebody is one applied pack");
        Check(packs.Packs == 1 && packs.GivesFor(Client) == 3 && packs.Duplicates == 1,
            "one pack, three receivers, one repeated give: the pack count is 1, not 3 (packs=" + packs.Packs + ", gives=" + packs.GivesFor(Client) + ", duplicates=" + packs.Duplicates + ")");
        Check(packs.PacksFor(Client) == 1 && packs.PacksFor(Host) == 0,
            "the item consumption is one application by the player who applied it; the packs it handed out are not his");
        Check(result.Receivers != null && result.Receivers.Count == 3, "the result names the three receivers it really healed");
        Check(Math.Abs(result.IntendedRel - 0.6) < 1e-9, "the application sums the relative amounts it really gave");
        Check(packs.ObservedBrackets == 1, "one application was observed, so a zero count on this machine would be a counted zero");
    }

    // 1.19.41 fix, the point of the review: the pack is credited to the player who was *healed*, exactly as the ammo
    // path credits the receiver of `GiveAmmoRel`. This exercises `MedicalPackCredit.Apply` - the function
    // `SupplyTracker.EndApply` calls - so the rule under test is the shipped one, not a parallel copy.
    private static void MedicalCreditGoesToTheReceiver()
    {
        var packs = new PackApplyLedger();
        var applier = new ImpactCounters();
        var receiver = new ImpactCounters();
        var rows = new Dictionary<ulong, ImpactCounters> { { Host, receiver } };
        var credit = new MedicalPackCredit();
        PackBracket bracket = packs.Begin(Client, false);
        packs.NoteGive(bracket, Host, 0.2);
        PackApplyResult applied = packs.End(bracket, null);

        Check(credit.Apply(applied, lookup => Candidate(rows, lookup)) == 1,
            "one applied pack credits exactly one receiver row");
        Check(applier.SelfSupplyUses == 0 && applier.SelfMedicalUses == 0 && !applier.SelfMedicalKnown,
            "the player who applied the pack is credited nothing: A giving B a medkit reads A = 0");
        Check(receiver.SelfSupplyUses == 1 && receiver.SelfMedicalUses == 1 && receiver.SelfMedicalKnown,
            "and B reads one medical pack, on a figure this machine proved");
        Check(credit.Apply(applied, lookup => Candidate(rows, lookup)) == 0
              && receiver.SelfSupplyUses == 1,
            "replaying the same result adds nothing: the same pack and the same receiver are one pack");

        PackBracket stranger = packs.Begin(Client, false);
        packs.NoteGive(stranger, Mate, 0.2);
        PackApplyResult unknownRow = packs.End(stranger, null);
        Check(credit.Apply(unknownRow, lookup => default(MedicalRowCandidate)) == 0 && applier.SelfSupplyUses == 0 && receiver.SelfSupplyUses == 1,
            "a receiver whose row this machine does not hold is skipped, never credited to the pack's user or to anyone else");
        Check(credit.LastSkipped == 1, "and the skip is counted, so the diagnostic can say a proven receiver was not shown");

        PackBracket empty = packs.Begin(Client, false);
        Check(credit.Apply(packs.End(empty, null), lookup => Candidate(rows, lookup)) == 0,
            "an application that healed nobody credits no medical pack anywhere");

        // The row that only saw ammo packs cannot claim "no medical pack" when this machine never confirmed one.
        string unconfirmed = CombatStrings.SupplyCell("50.0%", 1, 1, 0, 0, false, false);
        Check(unconfirmed.EndsWith("0 / " + CombatStrings.Unknown, StringComparison.Ordinal),
            "a row with ammo packs and no readable medical evidence prints `?` for the medical figure, never 0: " + unconfirmed);
        string confirmed = CombatStrings.SupplyCell("50.0%", 1, 1, 0, 0, false, true);
        Check(confirmed.EndsWith("0 / 0", StringComparison.Ordinal),
            "and a row whose medical packs this machine confirmed prints the counted zero: " + confirmed);
    }

    // The review's case: a client whose own pack healed a remote teammate. `ShouldTrack` is `isInSlot && (isMaster ||
    // isLocal)`, so it answers true for the client's own row only - gating the credit on it dropped the very row the
    // settlement shows for that teammate. The bracket proved the receiver; the row only has to exist and still belong
    // to a slotted player. Written through `MedicalPackCredit.Apply`, the function `SupplyTracker.CandidateFor` feeds.
    private static void ClientCreditsARemoteTeammateRow()
    {
        var packs = new PackApplyLedger();
        var clientRow = new ImpactCounters();
        var teammateRow = new ImpactCounters();
        var rows = new Dictionary<ulong, ImpactCounters> { { Client, clientRow }, { Mate, teammateRow } };
        var credit = new MedicalPackCredit();
        PackBracket bracket = packs.Begin(Client, false);   // the client is the pack user
        packs.NoteGive(bracket, Mate, 0.2);                 // ... and the teammate is the receiver
        PackApplyResult applied = packs.End(bracket, null);

        // The candidate a client builds for a remote teammate: a row exists and the lookup is in a slot. It is *not*
        // "trackable" here - that is the point.
        Check(credit.Apply(applied, lookup => Candidate(rows, lookup)) == 1 && teammateRow.SelfSupplyUses == 1,
            "a client's real application is written to the remote teammate's own row, which is the row the settlement shows");
        Check(clientRow.SelfSupplyUses == 0 && !clientRow.SelfMedicalKnown,
            "and the client's own row is not credited for a pack it gave away");

        // The same receiver after the seat is gone: the pack still happened, but there is no row to write now.
        var left = new ImpactCounters();
        var staleRows = new Dictionary<ulong, ImpactCounters> { { Mate, left } };
        PackBracket second = packs.Begin(Client, false);
        packs.NoteGive(second, Mate, 0.2);
        PackApplyResult afterSeat = packs.End(second, null);
        Check(credit.Apply(afterSeat, lookup => Candidate(staleRows, lookup, inSlot: false)) == 0
              && left.SelfSupplyUses == 0,
            "a lookup that no longer belongs to a slotted player is not written to, and nothing is re-attributed to another row");
    }

    // A give whose named giver is somebody other than the pack's user is not this pack's health. It is rejected at
    // the ledger (never a receiver, never a credited pack) and counted as a mismatch.
    private static void AGiveFromElsewhereIsNotThisPacksHealth()
    {
        var packs = new PackApplyLedger();
        PackBracket bracket = packs.Begin(Client, false);
        Check(!packs.NoteGive(bracket, Host, 0.2, giverAgrees: false), "a mismatching give is refused by the ledger");
        Check(bracket.Mismatches == 1 && bracket.Gives == 0 && bracket.Order.Count == 0, "and it is not a receiver of this pack");
        Check(packs.NoteGive(bracket, Mate, 0.2), "a give from the pack's own user is still counted");
        PackApplyResult applied = packs.End(bracket, null);
        Check(applied.Outcome == PackApplyOutcome.Applied && applied.Gives == 1 && applied.Mismatches == 1,
            "the pack is the one real give, and the mismatch travels with the result instead of being silently credited");
        Check(applied.Receivers.Count == 1 && applied.Receivers[0] == Mate, "only the agreeing receiver is credited");
        Check(packs.MismatchedGives == 1, "the ledger counts the refusal, so an exclusion is evidence rather than a comment");
    }

    // An application whose user could not be read must still close: the prefix returns a real bracket so the finalizer
    // pops exactly what it pushed. Otherwise the window stays open and the next heal - with no bracket of its own -
    // would be counted inside a pack nobody can name.
    private static void AnUnattributableApplicationStillCloses()
    {
        var packs = new PackApplyLedger();
        PackBracket anonymous = packs.Begin(0UL, true);
        Check(anonymous != null && ReferenceEquals(packs.Current, anonymous), "the unowned bracket is real and is the current one");
        Check(packs.NoteGive(anonymous, Host, 0.2), "a give inside it is seen (and deduplicated)");
        PackApplyResult result = packs.End(anonymous, null);
        Check(result.Outcome == PackApplyOutcome.Invalid && packs.Packs == 0, "closing it credits no pack to any row");
        Check(packs.Current == null, "and the stack is empty again: no window is left open for the next heal");

        Check(packs.NoteUnbracketedGive(Mate, 0.2) && packs.UnbracketedGives == 1 && packs.Packs == 0,
            "a heal that arrives with no bracket at all is an unbracketed heal, never a pack");
    }

    // Nested applications close in order, and an application that threw releases its bracket the same way.
    private static void NestedAndThrowingApplicationsReleaseTheirBracket()
    {
        var packs = new PackApplyLedger();
        PackBracket outer = packs.Begin(Client, false);
        PackBracket inner = packs.Begin(Host, false);
        Check(ReferenceEquals(packs.Current, inner) && packs.Depth == 2, "the innermost application is the one a give belongs to");
        Check(packs.NoteGive(inner, Mate, 0.2), "the give lands inside the inner application");
        PackApplyResult innerResult = packs.End(inner, null);
        Check(innerResult.Outcome == PackApplyOutcome.Applied && packs.PacksFor(Host) == 1 && packs.PacksFor(Client) == 0,
            "the inner pack is counted for its own user");
        Check(ReferenceEquals(packs.Current, outer), "closing the inner application returns to the outer one");

        PackApplyResult broken = packs.End(outer, new InvalidOperationException("native failed"));
        Check(broken.Outcome == PackApplyOutcome.Invalid && packs.Packs == 1, "an application that threw is no pack");
        Check(packs.Current == null && packs.Depth == 0, "and it leaves no bracket behind");
    }

    private static MedicalRowCandidate Candidate(Dictionary<ulong, ImpactCounters> rows, ulong lookup, bool inSlot = true)
    {
        if (!rows.TryGetValue(lookup, out ImpactCounters row)) return default;
        return new MedicalRowCandidate { Known = true, InSlot = inSlot, Row = row };
    }

    // The item and the received packs are two different questions: one pack used, one pack received by each of the
    // three teammates it healed - and one teammate given health twice in the same application is still one pack.
    private static void AGroupApplicationIsOneItemAndOnePackPerReceiver()
    {
        var packs = new PackApplyLedger();
        var rows = new Dictionary<ulong, ImpactCounters>
        {
            { Host, new ImpactCounters() },
            { Mate, new ImpactCounters() },
            { 0x4DUL, new ImpactCounters() },
        };
        var credit = new MedicalPackCredit();
        PackBracket bracket = packs.Begin(Client, false);
        packs.NoteGive(bracket, Host, 0.2);
        packs.NoteGive(bracket, Host, 0.2);
        packs.NoteGive(bracket, Mate, 0.2);
        packs.NoteGive(bracket, 0x4DUL, 0.2);
        PackApplyResult result = packs.End(bracket, null);
        Check(packs.Packs == 1, "a pack that heals three teammates is still one item consumed");
        Check(credit.Apply(result, lookup => Candidate(rows, lookup)) == 3,
            "each of the three receivers is credited one pack");
        Check(rows[Host].SelfMedicalUses == 1 && rows[Mate].SelfMedicalUses == 1 && rows[0x4DUL].SelfMedicalUses == 1,
            "the teammate given health twice inside the one application receives it once");
        Check(rows[Host].SelfSupplyUses == 1 && rows[Mate].SelfSupplyUses == 1 && rows[0x4DUL].SelfSupplyUses == 1,
            "and every receiver's own total grows by exactly one");
    }

    private static void AnEmptyApplicationIsNoPack()
    {
        var packs = new PackApplyLedger();
        PackBracket bracket = packs.Begin(Client, false);
        Check(packs.End(bracket, null).Outcome == PackApplyOutcome.Empty && packs.Packs == 0,
            "an application that healed nobody consumed nothing this machine can claim");
        Check(packs.ObservedBrackets == 1 && packs.EmptyBrackets == 1,
            "and the zero is a counted zero: the application was observed, it simply gave nobody health");

        var unowned = new PackApplyLedger();
        PackBracket anonymous = unowned.Begin(0UL, true);
        unowned.NoteGive(anonymous, Host, 0.2);
        Check(unowned.End(anonymous, null).Outcome == PackApplyOutcome.Invalid && unowned.Packs == 0,
            "an application whose user could not be read is credited to no row at all, instead of to a guessed one");
    }

    private static void AHealWithoutAnApplicationIsNeverAPack()
    {
        var packs = new PackApplyLedger();
        Check(packs.NoteUnbracketedGive(Host, 0.3), "a giver-sourced heal with no application window is still recorded");
        Check(packs.Packs == 0 && packs.UnbracketedGives == 1,
            "and it is never counted as a health pack: this is the bucket the 1319 recorded heals belong in");
        Check(!packs.NoteUnbracketedGive(0UL, 0.3) && !packs.NoteUnbracketedGive(Host, double.NaN) && !packs.NoteUnbracketedGive(Host, 0),
            "a heal without a receiver, without an amount or with a zero amount proves nothing");
    }

    private static void DisinfectionIsExcludedFromThePackCount()
    {
        var packs = new PackApplyLedger();
        PackBracket bracket = packs.Begin(Client, false);
        packs.NoteExcludedGive(Host, 0.2);
        Check(packs.End(bracket, null).Outcome == PackApplyOutcome.Empty && packs.Packs == 0,
            "a disinfection give inside an application window is not a health pack and does not make the window one");
        Check(packs.ExcludedGives == 1, "the exclusion is a number in the ledger, so it is evidence and not a comment beside a patch");
    }

    private static void AnApplicationThatThrewIsNoPack()
    {
        var packs = new PackApplyLedger();
        PackBracket bracket = packs.Begin(Client, false);
        packs.NoteGive(bracket, Host, 0.2);
        Check(packs.End(bracket, new InvalidOperationException("native failed")).Outcome == PackApplyOutcome.Invalid && packs.Packs == 0,
            "an application that threw did not complete, so the gives it already saw do not become a pack");
    }

    private static void NativeUnitsFollowTheSlotCap()
    {
        Check(Math.Abs(PackApplyLedger.Units(0.2, 100) - 20) < 1e-9, "one original pack use at the native 100 cap is 20 units");
        Check(Math.Abs(PackApplyLedger.Units(0.0333, 600) - 19.98) < 1e-6,
            "the same 20 units at a raised 600 cap is a relative 0.0333, which is the `intended=3.331` the fixed report measured");
        Check(Math.Abs(PackApplyLedger.Relative(20, 600) - 1.0 / 30) < 1e-9, "the relation is invertible, which is what the efficiency scale relies on");
        Check(double.IsNaN(PackApplyLedger.Units(0.2, 0)) && double.IsNaN(PackApplyLedger.Units(double.NaN, 100))
              && double.IsNaN(PackApplyLedger.Relative(20, -1)),
            "an unreadable cap or amount yields no unit count instead of an invented one");
    }

    // The rule above only decides anything if the native path asks it. `SupplyTracker` and the reflection-installed
    // hooks bind game types and cannot be linked here, so their lines are read as source - the same static wiring
    // check the release suites use, with the comments stripped so a comment may still name a call this build must
    // not make.
    private static void TheApplicationBracketIsWiredIntoTheTracker()
    {
        string root = SourceRoot();
        if (root == null) return;
        string tracker = Code(File.ReadAllText(Path.Combine(root, "SupplyTracker.cs")));
        string hooks = Code(File.ReadAllText(Path.Combine(root, "PackApplicationPatches.cs")));
        Check(tracker.Contains("if (!Packs.NoteGive(bracket, receiverOwner.Lookup, amountRel, giverAgrees))"),
            "a give inside the application window reaches the ledger, deduplicated by receiver and gated on the named giver");
        Check(tracker.Contains("bool giverAgrees = giverOwner == null || bracket.Owner == 0UL || giverOwner.Lookup == bracket.Owner;"),
            "a give whose named giver is known to be somebody else is refused instead of being credited as this pack's health");
        Check(tracker.Contains("int credited = Credit.Apply(result, CandidateFor);"),
            "the production write goes through the one credit rule the suite exercises, not a copy of it");
        Check(!tracker.Contains("PackUsers.TryGetValue(result.Owner") && tracker.Contains("private static MedicalRowCandidate CandidateFor(ulong lookup)"),
            "the pack's user is never credited: only the receivers' own rows are resolved, and a row this machine does not hold is skipped");
        // The review's gate: the credit path must not ask whether this machine may *track* that player. On a client
        // `ShouldTrack` is true for its own row only, which dropped a real application to a remote teammate.
        Check(!tracker.Contains("ShouldTrack(owner)"),
            "the receiver row is not gated on ShouldTrack, so a client's application still reaches the teammate's own settlement row");
        Check(tracker.Contains("return new MedicalRowCandidate { Known = row != null, InSlot = owner.IsInSlot, Row = row };"),
            "what is required of a receiver row is only that it exists and belongs to a slotted player");
        // An application whose user could not be read still returns its bracket, so the finalizer can pop it.
        Check(tracker.Contains("return Packs.Begin(0UL, bot);"),
            "an unowned application returns the bracket it opened instead of leaving a stale window open");
        Check(tracker.Contains("if (Packs.NoteUnbracketedGive(receiverOwner.Lookup, amountRel))"),
            "a heal with no bracket open is recorded as an unbracketed heal, never as a pack");
        Check(tracker.Contains("Packs.NoteExcludedGive("),
            "disinfection is handed to the exclusion counter instead of the give counter");
        Check(hooks.Contains("SupplyTracker.BeginApply(") && hooks.Contains("SupplyTracker.EndApply("),
            "the bracket is opened and closed around the native application, bot entry point included");
        Check(hooks.Contains("nameof(ResourcePackFirstPerson.ApplyPackBot)") && hooks.Contains("nameof(PlayerAgent.GiveDisinfection)"),
            "both the bot application and the disinfection call are bound statically by name, so neither path is silently lost");
        // The interop exposes ApplyPackBot as a *static* method; a prefix that took `__instance` from it would be
        // patched onto an instance method and silently never run.
        Check(hooks.Contains("private static void ApplyPackBotPrefix(PlayerAgent ownerAgent, PlayerAgent receiverAgent, ref PackBracket __state)"),
            "the bot application prefix takes its arguments and no instance, which is what the static native signature has");
        Check(!hooks.Contains("TypeByName") && !hooks.Contains("GetField("),
            "nothing in the pack hooks is resolved by reflection: the type, the method and its parameters are all static");
        Check(!tracker.Contains("ShouldTrack(change.Giver)"),
            "the tracked-player test still decides only where efficiency is credited, never what the heal was");
    }

    private static void TheHealthKindRuleIsWiredIntoTheTracker()
    {
        string root = SourceRoot();
        if (root == null) return;
        string tracker = Code(File.ReadAllText(Path.Combine(root, "SupplyTracker.cs")));
        Check(tracker.Contains("if (!data.source.TryGet(out var agent)) return null;"),
            "the tracker measures a heal only when the packet named a source agent at all, so a heal with no resolvable source never becomes a pack");
        Check(tracker.Contains("bool sourceIsPlayer = giver != null;"),
            "the tracker records whether the packet's source resolved to a player agent at all");
        Check(tracker.Contains("Record(change.Giver, use, \"medical\");"),
            "the heal keeps its give/receive efficiency measurement, which is the semantic this release must not lose");
        Check(!tracker.Contains("RecordHealthConsumption"),
            "and a giver-sourced heal is never turned into a pack of any kind by the tracker: `ReceiveAddHealth` is the channel medkits, syringes, disinfection and other mods' heals all travel on");
    }

    private static string Code(string text)
    {
        if (text == null) return string.Empty;
        var kept = new List<string>(text.Length / 40 + 8);
        foreach (string line in text.Split('\n'))
            if (!line.TrimStart().StartsWith("//", StringComparison.Ordinal)) kept.Add(line);
        return string.Join("\n", kept);
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj"))) directory = directory.Parent;
        return directory != null ? directory.FullName : null;
    }

    private static void Check(bool condition, string description)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException("FAIL: " + description);
    }
}
