using System;
using System.Collections.Generic;

namespace HawChow.GTFOCombatPanel;

// 1.19.41: what one native resource-pack application proved. A pack is consumed by *applying* it - the
// bracket around `ResourcePackFirstPerson.ApplyPack()` / `ApplyPackBot(owner, receiver, item)` - and never
// by a heal arriving. `Applied` is a bracket that handed health to at least one distinct receiver, `Empty`
// is a bracket that handed it to nobody (nobody to give, so nothing was consumed in a way this machine can
// claim), and `Invalid` is a bracket whose owner was never readable: the ledger counts no pack for it either
// way, because a pack nobody can name is not a pack of this row.
public enum PackApplyOutcome
{
    Applied = 0,
    Empty = 1,
    Invalid = 2,
}

public struct PackApplyResult
{
    public PackApplyOutcome Outcome;
    public ulong Owner;
    public bool Bot;
    public int Gives;
    public int Duplicates;
    // Gives that named a giver other than this application's user. Rejected, never credited.
    public int Mismatches;
    public double IntendedRel;
    // The application's serial, so a credit rule can refuse to hand one pack out twice.
    public long PackId;
    // The receivers this application really gave health to, each once. The pack is *one* consumption of the
    // item; every one of these players received one medical pack, and the pack's user is among neither.
    public IReadOnlyList<ulong> Receivers;
}

// One open application bracket. The patches pass it around as their `__state`, so a nested application
// cannot close the wrong one.
public sealed class PackBracket
{
    public ulong Owner;
    public bool Bot;
    public bool Started;
    // Distinct receivers in the order they were given health. The order is the credit order, so two logs of one
    // application can be compared; the set is what deduplicates them.
    public readonly List<ulong> Order = new List<ulong>();
    public readonly HashSet<ulong> Receivers = new HashSet<ulong>();
    public int Gives;
    public int Duplicates;
    // Gives inside this bracket whose named giver was somebody other than the bracket's user. They are not
    // evidence for this pack and are excluded from `Gives`/`Order`.
    public int Mismatches;
    public double IntendedRel;
    public int Depth;
    public long Id;
}

// The pack-application ledger: the only place a pack is counted.
//
// Why this exists (see the expedition that produced 1319 "medical uses" against ~1114 units of health that
// could possibly have been lost): `Dam_PlayerDamageBase.ReceiveAddHealth` carries `pAddHealthData.source`,
// and the native `Dam_SyncedDamageBase.AddHealth(amount, source)` is the same channel medkits, syringes and
// every other giver-sourced heal travel on. It therefore cannot tell a health pack from a syringe, from
// disinfection, or from another mod's trickle heal - and "a heal happened near a pack" is not consumption.
// The pack is consumed on the *giver's* machine, by the application bracket, once per pack; this type counts
// that, deduplicates the receivers inside one bracket, and keeps every other giver-sourced heal in a bucket
// that is never called a pack.
//
// Nothing here binds a game type, so the whole rule is exercised without Unity.
public sealed class PackApplyLedger
{
    // The native slot-4 capacity the relative heal amount is relative to (`SupplyTracker`'s health scale is
    // 100 units = full health; 20 units is one original-pack use at the native 100 cap).
    public const double NativeSlotCap = 100.0;

    private sealed class Tally
    {
        public long Packs, EmptyBrackets, InvalidBrackets, Gives, Duplicates;
    }

    private readonly List<PackBracket> _open = new List<PackBracket>();
    private readonly Dictionary<ulong, Tally> _byOwner = new Dictionary<ulong, Tally>();

    private long _unbracketed;
    private long _excluded;
    private long _mismatchedGivers;
    private long _brackets;
    private long _nextId;

    // Applications this machine bracketed at all. A zero pack count is only a *counted* zero when this is
    // positive; otherwise the metric is unavailable and must be printed as unknown, not as 0.
    public long ObservedBrackets => _brackets;
    public bool Observed => _brackets > 0 || _unbracketed > 0 || _excluded > 0;

    public int Depth => _open.Count;
    public PackBracket Current => _open.Count > 0 ? _open[_open.Count - 1] : null;

    // Giver-sourced heals this machine saw with *no* application bracket around them: a syringe, another
    // mod, a future entry point. Never a pack, never dropped either - the count is kept so a reader can see
    // that something healed and that it was not claimed as a consumption.
    public long UnbracketedGives => _unbracketed;
    // Heals this machine saw inside a bracket that were explicitly *not* health packs (disinfection travels
    // the same slot with the same denominator). Recorded so the exclusion is provable, never counted.
    public long ExcludedGives => _excluded;
    // Gives inside a bracket that named a giver other than the pack's owner. The pack is still one pack; the
    // discrepancy is what a reader needs to see instead of a silently reassigned give.
    public long MismatchedGives => _mismatchedGivers;

    public long Packs { get { long total = 0; foreach (Tally tally in _byOwner.Values) total += tally.Packs; return total; } }
    public long EmptyBrackets { get { long total = 0; foreach (Tally tally in _byOwner.Values) total += tally.EmptyBrackets; return total; } }
    public long Gives { get { long total = 0; foreach (Tally tally in _byOwner.Values) total += tally.Gives; return total; } }
    public long Duplicates { get { long total = 0; foreach (Tally tally in _byOwner.Values) total += tally.Duplicates; return total; } }

    public long PacksFor(ulong owner) { return _byOwner.TryGetValue(owner, out Tally tally) ? tally.Packs : 0; }
    public long GivesFor(ulong owner) { return _byOwner.TryGetValue(owner, out Tally tally) ? tally.Gives : 0; }

    public void Clear()
    {
        _open.Clear();
        _byOwner.Clear();
        _unbracketed = _excluded = _mismatchedGivers = _brackets = 0;
    }

    // The bracket opens before the native application runs. An owner nobody could read is a bracket this
    // machine cannot attribute: it is kept (so its gives are still deduplicated and still not called packs)
    // and it counts no pack for any row.
    public PackBracket Begin(ulong owner, bool bot)
    {
        _brackets++;
        var bracket = new PackBracket { Owner = owner, Bot = bot, Started = true, Depth = _open.Count, Id = ++_nextId };
        _open.Add(bracket);
        return bracket;
    }

    // One `PlayerAgent.GiveHealth(giverAgent, amountRel)` inside a bracket. The receiver is the agent the
    // call landed on, so "same pack, same receiver" is one give however many times the native code repeats
    // it in one bracket. A relative amount that cannot be read claims nothing (no fabricated receiver), and a
    // give whose *named* giver disagrees with the bracket's own user is not this pack's health: it is counted as
    // a mismatch and never as a medical pack, because "somebody gave this player health" is only evidence for the
    // pack when the giver is the one who applied it.
    public bool NoteGive(PackBracket bracket, ulong receiver, double amountRel, bool giverAgrees = true)
    {
        if (bracket == null) return false;
        if (receiver == 0UL || !double.IsFinite(amountRel) || amountRel < 0)
        {
            return false;
        }
        if (!giverAgrees)
        {
            bracket.Mismatches++;
            _mismatchedGivers++;
            return false;
        }
        if (!bracket.Receivers.Add(receiver))
        {
            bracket.Duplicates++;
            TallyFor(bracket.Owner).Duplicates++;
            return false;
        }
        bracket.Gives++;
        bracket.IntendedRel += amountRel;
        bracket.Order.Add(receiver);
        return true;
    }

    // A giver-sourced heal with no bracket open on this machine.
    public bool NoteUnbracketedGive(ulong receiver, double amountRel)
    {
        if (receiver == 0UL || !double.IsFinite(amountRel) || !(amountRel > 0)) return false;
        _unbracketed++;
        return true;
    }

    // Disinfection, injection and every other non-health give that travels the same slot. Inside a bracket it
    // must not extend the bracket's receiver set, so it is handed here instead and counted apart.
    public void NoteExcludedGive(ulong receiver, double amountRel)
    {
        if (receiver == 0UL) return;
        _excluded++;
    }

    // A give inside the bracket that named somebody other than the pack's owner is rejected at `NoteGive`; this
    // counter is kept for callers that only observe the fact (nothing is credited either way).
    public void NoteGiverMismatch() { _mismatchedGivers++; }

    // The bracket closes. `Packs` grows by exactly one when at least one distinct receiver was given health
    // inside it, and by nothing at all when it was empty or unattributable.
    public PackApplyResult End(PackBracket bracket, Exception error)
    {
        if (bracket == null) return new PackApplyResult { Outcome = PackApplyOutcome.Invalid };
        _open.Remove(bracket);
        var result = new PackApplyResult
        {
            Owner = bracket.Owner,
            Bot = bracket.Bot,
            Gives = bracket.Gives,
            Duplicates = bracket.Duplicates,
            Mismatches = bracket.Mismatches,
            IntendedRel = bracket.IntendedRel,
            PackId = bracket.Id,
            Receivers = bracket.Order,
        };
        Tally tally = TallyFor(bracket.Owner);
        if (bracket.Owner == 0UL) { tally.InvalidBrackets++; result.Outcome = PackApplyOutcome.Invalid; return result; }
        // A native exception means the application did not complete: whatever the bracket saw is not a pack.
        if (error != null) { result.Outcome = PackApplyOutcome.Invalid; return result; }
        if (bracket.Gives > 0) { tally.Packs++; tally.Gives += bracket.Gives; result.Outcome = PackApplyOutcome.Applied; }
        else { tally.EmptyBrackets++; result.Outcome = PackApplyOutcome.Empty; }
        return result;
    }

    private Tally TallyFor(ulong owner)
    {
        if (!_byOwner.TryGetValue(owner, out Tally tally)) { tally = new Tally(); _byOwner[owner] = tally; }
        return tally;
    }

    // `amountRel` is a fraction of the receiver's *slot-4 native capacity*, not a number of uses: 0.2 at the
    // native 100 cap is the 20 units of one pack use, and 0.0333 at a raised 600 cap is the same 20 units.
    // An unreadable cap or amount yields NaN - the caller then measures nothing instead of inventing units.
    public static double Units(double amountRel, double slotCap)
    {
        if (!double.IsFinite(amountRel) || amountRel < 0) return double.NaN;
        if (!double.IsFinite(slotCap) || slotCap <= 0) return double.NaN;
        return amountRel * slotCap;
    }

    // The same relation the other way round, for a caller that measured units and has to report a share.
    public static double Relative(double units, double slotCap)
    {
        if (!double.IsFinite(units) || units < 0) return double.NaN;
        if (!double.IsFinite(slotCap) || slotCap <= 0) return double.NaN;
        return units / slotCap;
    }
}

// 1.19.41 fix: who a medical pack is credited to.
//
// The settlement row `SelfSupplyUses` is the packs a player *received* - the ammo path already credits the
// receiver of `GiveAmmoRel`, never the pack's user - and the medical pack is the same kind of fact. So one
// successful application hands each of its proven receivers exactly one medical pack, and the player who
// applied it gets nothing: A giving B one medkit must read A = 0 / B = 1, whatever A's own row says.
//
// The item is still consumed once per application (`PackApplyLedger.Packs`), so a single pack that heals three
// teammates is one pack used and three packs received - two different questions, kept apart on purpose.
//
// The row a proven medical pack may be written to, as this machine can see it.
//
// `Known` means this machine holds a row for that lookup at all; `InSlot` means the lookup still belongs to a
// slotted player (a seat that left or was renumbered is not somebody this pack healed *now*). Note what is
// deliberately absent: nothing here asks whether this machine may *track* that player. On a client
// `CombatTracker.ShouldTrack` answers true for its own row only, so gating the credit on it dropped a real
// application to a remote teammate instead of showing it on that teammate's row. The network permission that
// clause protects is untouched - nothing is published or attributed to another machine here.
public struct MedicalRowCandidate
{
    public bool Known;
    public bool InSlot;
    public ImpactCounters Row;
}

// `SupplyTracker.EndApply` calls exactly this method, so the credit rule is what the shipped path runs and not a
// parallel copy: the harness exercises this function and the tracker's call site. The object remembers which
// applications it already credited, so a finalizer that runs twice cannot hand one pack out twice.
public sealed class MedicalPackCredit
{
    private readonly HashSet<long> _credited = new HashSet<long>();

    // Receivers whose row this machine could not write (unknown, left their slot, or had no row). Counted so the
    // diagnostic can say how many of a pack's proven receivers were shown, and never retried onto another row.
    public int LastSkipped { get; private set; }

    // Returns how many rows were credited. A receiver whose row this machine does not hold, or whose lookup is no
    // longer a slotted player, is skipped - it is never credited to somebody else, and the pack's user is never a
    // stand-in.
    public int Apply(PackApplyResult result, Func<ulong, MedicalRowCandidate> candidate)
    {
        LastSkipped = 0;
        if (result.Outcome != PackApplyOutcome.Applied || candidate == null) return 0;
        IReadOnlyList<ulong> receivers = result.Receivers;
        if (receivers == null) return 0;
        // One application, one credit: a replay of the same pack adds nothing, whatever it claims to have healed.
        if (result.PackId != 0L && !_credited.Add(result.PackId)) return 0;
        int credited = 0;
        var seen = new HashSet<ulong>();
        foreach (ulong receiver in receivers)
        {
            if (receiver == 0UL || !seen.Add(receiver)) continue;
            MedicalRowCandidate row = candidate(receiver);
            // The bracket already proved the receiver; the row only has to exist and belong to a slotted player.
            if (!row.Known || !row.InSlot || row.Row == null) { LastSkipped++; continue; }
            row.Row.CreditSelfSupply(SupplyPackKind.Medical);
            credited++;
        }
        return credited;
    }

    public void Clear() => _credited.Clear();
}
