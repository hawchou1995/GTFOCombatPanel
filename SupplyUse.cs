using System;
using System.Collections.Generic;

namespace HawChow.GTFOCombatPanel;

// Which resource pack a use was (1.19.34). The settlement row prints the three kinds separately, and a use is
// exactly one of them: an ammo pack is Standard/Special, a tool pack refilled the class channel, and a medical
// pack is the giver-sourced `ReceiveAddHealth`. Nothing else counts as a pack.
public enum SupplyPackKind
{
    Ammo = 0,
    Tool = 1,
    Medical = 2,
}

// 1.19.38: which half of a use this machine could not read. The recorded expedition printed one sentence -
// "ammo supply use was not measured completely" - for three different failures (the before-read threw, the
// after-read threw, the change never arrived), and a reader could not tell an unreadable page from a packet
// this machine never saw. The flags below are what `AddChange` really compared; the log prints them.
[Flags]
public enum SupplyUseGap
{
    None = 0,
    Before = 1,
    After = 2,
    Intended = 4,
    Applied = 8,
}

// A resource-pack use can fill more than one weapon. Combine its native ammo-unit changes first,
// then count exactly one utilization ratio. Different uses are averaged by ImpactCounters.
public sealed class SupplyUse
{
    public double Intended { get; private set; }
    public double Effective { get; private set; }
    public int Changes { get; private set; }
    private SupplyUseGap _gap;
    private bool _recorded;
    private bool _consumptionRecorded;


    public bool RecordConsumption(ImpactCounters receiver, SupplyPackKind kind)
    {
        if (_consumptionRecorded || receiver == null || Invalid || Changes == 0 ||
            !double.IsFinite(Intended) || !double.IsFinite(Effective) || Intended <= 0 || Effective <= 0) return false;
        receiver.CreditSelfSupply(kind);
        _consumptionRecorded = true;
        return true;
    }
    // 1.19.35 (review fix): the released one-bit overload (`true` = ammo pack, `false` = "not an ammo pack") is gone.
    // `false` was read as a *tool* pack, but the medical path used it too - one bit cannot answer a row with three
    // kinds, and a medical pack passed through it was counted as a tool pack. Every caller names its kind now, so the
    // compiler keeps a pack of an unnamed kind out of this method instead of a comment doing it.

    // 1.19.35: the same one-pack-once accounting for a use whose *kind* this machine cannot name - a heal whose native
    // `pAddHealthData.source` did not resolve to a player agent at all. The pack really was consumed, so the row's total
    // keeps it; the split is what cannot be claimed, and calling that pack a 血包 was the fabrication this exists to stop. The
    // validity rule is `RecordConsumption`'s own, so a use nobody gained anything from still counts nothing at all.
    // 1.19.39: "did not resolve to a player" is the whole test. A player this machine merely does not *track* is not this
    // case - see `RecordHealthConsumption`, which is what the heal path calls now.
    public bool RecordConsumptionUnknown(ImpactCounters receiver)
    {
        if (_consumptionRecorded || receiver == null || Invalid || Changes == 0 ||
            !double.IsFinite(Intended) || !double.IsFinite(Effective) || Intended <= 0 || Effective <= 0) return false;
        receiver.CreditSelfSupplyUnknown();
        _consumptionRecorded = true;
        return true;
    }

    // 1.19.39: a giver-sourced heal answers two questions and the packet settles only one of them. What the heal *was*
    // comes from `pAddHealthData.source`: it resolved to a player agent, so a player gave a health pack. Whose row this
    // machine keeps is a different fact - a client keeps its own row and the host publishes the rest - and it used to
    // decide the kind as well: `SupplyTracker` asked its tracking rule about the giver, so the same heal was one 血包 on
    // the host and a pack of no name on a client. The kind is read off the packet here; ownership still decides only
    // where the *efficiency* is credited, which is the tracking test in `SupplyTracker.Record`.
    public bool RecordHealthConsumption(ImpactCounters receiver, bool sourceResolvedToPlayer)
        => sourceResolvedToPlayer
            ? RecordConsumption(receiver, SupplyPackKind.Medical)
            : RecordConsumptionUnknown(receiver);

    public bool Invalid { get; private set; }

    // The gap this use cannot close, named field by field. `Applied` is the case with no unreadable number
    // at all: the scope ended without a single applied change reaching it, so there is no after-read to
    // blame and nothing to average - which is a different fact from `After` (a read that ran and failed).
    public SupplyUseGap Unmeasured => _gap == SupplyUseGap.None && Changes == 0 ? SupplyUseGap.Applied : _gap;

    // The same gap as the words the diagnostic line prints, in a fixed order so two logs compare.
    public string UnmeasuredFields => Describe(Unmeasured);

    public static string Describe(SupplyUseGap gap)
    {
        if (gap == SupplyUseGap.None) return "none";
        var parts = new List<string>(4);
        if ((gap & SupplyUseGap.Before) != 0) parts.Add("before");
        if ((gap & SupplyUseGap.After) != 0) parts.Add("after");
        if ((gap & SupplyUseGap.Intended) != 0) parts.Add("intended");
        if ((gap & SupplyUseGap.Applied) != 0) parts.Add("applied");
        return string.Join("+", parts);
    }

    public void AddChange(double before, double after, double intended)
    {
        bool beforeRead = double.IsFinite(before) && before >= 0;
        bool intendedRead = double.IsFinite(intended) && intended > 0;
        // The after side is only blamed when there was a before reading to compare it with: an unreadable
        // before already explains why no difference exists, and naming both would send a reader looking for a
        // second failure that never happened.
        bool afterRead = double.IsFinite(after) && after >= before - 0.0001;
        if (!beforeRead) _gap |= SupplyUseGap.Before;
        else if (!afterRead) _gap |= SupplyUseGap.After;
        if (!intendedRead) _gap |= SupplyUseGap.Intended;
        if (!beforeRead || !afterRead || !intendedRead)
        {
            Invalid = true;
            return;
        }
        Intended += intended;
        Effective += Math.Min(intended, Math.Max(0, after - before));
        Changes++;
    }

    public bool Record(ImpactCounters counters)
    {
        if (_recorded || counters == null || Invalid || Changes == 0 || !double.IsFinite(Intended) || !double.IsFinite(Effective)) return false;
        counters.RecordSupply(Effective, Intended);
        _recorded = true;
        return true;
    }
}

// Where a pickup's giver identity came from. `Scope` is a nested native callback that named the giver
// itself, `Give` is the pack use this machine remembered (the only source a client has), and `Sender` is
// the delivery's sender, which only the authority machine may read.
public enum SupplyGiverSource
{
    None = 0,
    Scope = 1,
    Give = 2,
    Sender = 3,
}

// The sender of a replicated delivery, as one machine can read it: who it was, and whether that machine
// may treat it as the giver.
public struct SupplySenderCandidate
{
    public ulong Lookup;
    public bool Allowed;
}

// `pAmmoGive` carries the receiver and the three shares and nothing else, and the authority relays it
// (`SNet_AuthorativeAction.Ask` on the pack user's machine, `Do` on the master, which broadcasts the
// settled copy). The sender of the copy a client receives is therefore the master that forwarded it, and
// the sender of the copy the master applies to its own action is whatever packet it handled last. A
// sender is only ever a giver on the authority machine, only when it is another player, and only when
// that player is in a slot - never the local player, which is exactly what a forwarded copy looks like.
public static class SupplyGiverRule
{
    public static bool SenderAllowed(bool isMaster, bool senderIsLocal, bool senderInSlot)
        => isMaster && !senderIsLocal && senderInSlot;
}

// The ammo give as the machine that opened the pack knows it. The pickup that moves the numbers is
// applied from the replicated packet on the machines that own the receivers, so the pair has to be
// remembered here rather than read from the delivery: one record per native give, claimed once, and
// retired by age so a delivery that never arrives leaves nothing behind. Records are matched on the
// receiver and on the three shares, which is what tells one pack use from another in the same frame.
public sealed class SupplyGiveLedger
{
    public const double MaxAgeSeconds = 1.5;
    public const double ShareTolerance = 0.0001;
    public const int Capacity = 32;

    private struct Give
    {
        public ulong Giver;
        public ulong Receiver;
        public double Standard, Special, Tool;
        public double ExpiresAt;
    }

    private readonly List<Give> _pending = new List<Give>();

    public int Count { get { return _pending.Count; } }

    // One native give. Unusable shares are refused instead of stored as a guess: a record that can never
    // match does not credit anybody, and a zero share means the pack did not touch that channel.
    public bool Register(ulong giver, ulong receiver, double standard, double special, double tool, double now)
    {
        if (giver == 0UL || receiver == 0UL || !double.IsFinite(now)) return false;
        if (!Usable(standard) || !Usable(special) || !Usable(tool)) return false;
        if (!(standard > 0) && !(special > 0) && !(tool > 0)) return false;
        Prune(now);
        while (_pending.Count >= Capacity) _pending.RemoveAt(0);
        _pending.Add(new Give
        {
            Giver = giver,
            Receiver = receiver,
            Standard = standard,
            Special = special,
            Tool = tool,
            ExpiresAt = now + MaxAgeSeconds,
        });
        return true;
    }

    // Name the giver of one delivered give. A nested scope that already named the giver wins and retires
    // the remembered copy (so a later packet cannot reuse it), then the oldest remembered give for the
    // same receiver and shares is claimed, then - authority machine only - the sender. Nothing is
    // resolved from a guess: the answer is `false` with `None` when this machine cannot name the giver.
    public bool TryResolve(ulong receiver, double standard, double special, double tool, double now,
        ulong scopedGiver, SupplySenderCandidate sender, out ulong giver, out SupplyGiverSource source)
    {
        giver = 0UL;
        source = SupplyGiverSource.None;
        if (receiver == 0UL || !double.IsFinite(now)) return false;
        Prune(now);
        if (scopedGiver != 0UL)
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                if (_pending[i].Giver != scopedGiver || _pending[i].Receiver != receiver) continue;
                if (!Matches(_pending[i], standard, special, tool)) continue;
                _pending.RemoveAt(i);
                break;
            }
            giver = scopedGiver;
            source = SupplyGiverSource.Scope;
            return true;
        }
        for (int i = 0; i < _pending.Count; i++)
        {
            if (_pending[i].Receiver != receiver || !Matches(_pending[i], standard, special, tool)) continue;
            giver = _pending[i].Giver;
            source = SupplyGiverSource.Give;
            _pending.RemoveAt(i);
            return true;
        }
        if (sender.Allowed && sender.Lookup != 0UL)
        {
            giver = sender.Lookup;
            source = SupplyGiverSource.Sender;
            return true;
        }
        return false;
    }

    public void Clear() { _pending.Clear(); }

    private static bool Usable(double share) { return double.IsFinite(share) && share >= 0; }

    private static bool Matches(Give give, double standard, double special, double tool)
        => Near(give.Standard, standard) && Near(give.Special, special) && Near(give.Tool, tool);

    private static bool Near(double remembered, double delivered)
        => double.IsFinite(delivered) && Math.Abs(remembered - delivered) <= ShareTolerance;

    private void Prune(double now)
    {
        for (int i = _pending.Count - 1; i >= 0; i--)
            if (now > _pending[i].ExpiresAt) _pending.RemoveAt(i);
    }
}
