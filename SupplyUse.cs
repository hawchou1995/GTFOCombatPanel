using System;
using System.Collections.Generic;

namespace HawChow.GTFOCombatPanel;

// A resource-pack use can fill more than one weapon. Combine its native ammo-unit changes first,
// then count exactly one utilization ratio. Different uses are averaged by ImpactCounters.
public sealed class SupplyUse
{
    public double Intended { get; private set; }
    public double Effective { get; private set; }
    public int Changes { get; private set; }
    private bool _recorded;
    private bool _consumptionRecorded;

    public bool RecordConsumption(ImpactCounters receiver, bool ammo)
    {
        if (_consumptionRecorded || receiver == null || Invalid || Changes == 0 ||
            !double.IsFinite(Intended) || !double.IsFinite(Effective) || Intended <= 0 || Effective <= 0) return false;
        if (receiver.SelfSupplyUses < long.MaxValue) receiver.SelfSupplyUses++;
        if (ammo && receiver.SelfAmmoUses < long.MaxValue) receiver.SelfAmmoUses++;
        _consumptionRecorded = true;
        return true;
    }
    public bool Invalid { get; private set; }

    public void AddChange(double before, double after, double intended)
    {
        if (!double.IsFinite(before) || !double.IsFinite(after) || !double.IsFinite(intended) ||
            before < 0 || after < before - 0.0001 || intended <= 0)
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
