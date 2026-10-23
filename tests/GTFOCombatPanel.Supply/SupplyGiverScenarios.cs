using System;
using HawChow.GTFOCombatPanel;

namespace HawChow.GTFOCombatPanel.SupplyTests;

// Regression cover for the ammo supply giver that is missing on a client: `pAmmoGive` names the receiver
// only and the authority relays it, so the giver has to come from the give the pack user's own machine
// made. Everything SupplyTracker decides is delegated to SupplyUse.cs and is asserted here; the native
// callbacks themselves (GiveAmmoRel/ReceiveAmmoGive/PickupAmmo) can only be exercised in a live session.
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
        Check(use.RecordConsumption(receiver, true), "the benefiting player still earns the consumption credit");
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
        Check(!fittedNowhere.RecordConsumption(new ImpactCounters(), true),
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

    private static void Check(bool condition, string description)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException("FAIL: " + description);
    }
}
