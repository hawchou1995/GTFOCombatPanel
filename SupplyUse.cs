using System;

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
