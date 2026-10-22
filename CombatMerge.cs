using System;
using System.Collections.Generic;

namespace HawChow.GTFOCombatPanel;

// How a squad row is displayed when two machines each measured half of it, and how the two machines
// exchange what the other cannot see.
//
// The one rule that shapes everything here is that the mod never edits a measurement. `CombatTracker.Store`
// holds only what this machine observed itself. A received snapshot is cached raw, and a row is *projected*
// for display by cloning the local row and merging the cached remote figures into the clone. So:
//
//   * an authoritative host figure can never be overwritten by a later client estimate, because nothing
//     written here survives into the capture store that the next measurement is added to - the previous
//     release merged into the store, which is exactly how a host's measured damage could be replaced by a
//     client's estimate and then have the next real hit added on top of the wrong base;
//   * the client's own exact hit rate survives, because a locally exact rate is never replaced by a peer's
//     estimate;
//   * an unobserved field never erases a measured one.
//
// The pure predicates at the top are the parts the headless suite asserts directly; `Project`/`ApplyRemote`
// are the same rules applied to a cloned row.
public static class CombatMerge
{
    // True when the local (this machine's) value is the exact one for that field and must survive.
    private static bool LocalExact(bool observed, bool estimated) => observed && !estimated;

    // Which side supplies the displayed enemy damage: the host applied the blow, so its number is the blow.
    // A host that never observed the field leaves the local figure alone.
    public static bool HostSuppliesDamage(bool hostObserved) => hostObserved;

    // Which side supplies the displayed hit rate: a locally exact rate is kept; otherwise the host's figure
    // is used when it carries one and the local figure is kept when it does not.
    public static bool HostSuppliesAccuracy(bool localObserved, bool localEstimated, bool hostObserved, bool hostEstimated)
    {
        if (LocalExact(localObserved, localEstimated)) return false;
        return hostObserved || hostEstimated;
    }

    // A counted impact figure. The host's non-zero figure wins because the host saw the event; a host zero
    // means it never saw one, which must never erase a count the local machine recorded.
    public static long MergeCount(long local, long host) => host != 0 ? host : local;

    // Whether a snapshot counted any kill at all. A kill total and the state split of those same kills
    // are one figure, so this is the question that decides whether a group may replace the one already
    // held: a snapshot that counted nothing never erases a figure another machine counted.
    public static bool KillGroupReported(in SyncSnapshot snap)
        => AnyFigure(snap.KillMinor, snap.KillElite, snap.KillBoss,
            snap.SleepingMinorKills, snap.SleepingEliteKills, snap.SleepingBossKills,
            snap.UnknownStateMinorKills, snap.UnknownStateEliteKills, snap.UnknownStateBossKills);

    public static bool AnyFigure(params long[] figures)
    {
        if (figures == null) return false;
        foreach (long figure in figures) if (figure != 0) return true;
        return false;
    }

    public static double MergeAmount(double local, bool localObserved, double host, bool hostObserved)
    {
        if (hostObserved && double.IsFinite(host)) return host;
        return double.IsFinite(local) ? local : 0;
    }

    // ---------------------------------------------------------------- display projection

    // A throwaway copy of the local row. Everything the display path needs is copied; the capture store is
    // never handed out, so a caller cannot write through it.
    public static CombatPlayer Project(CombatPlayer local) => local == null ? null : local.Clone();

    // Merge one cached remote snapshot into a *projected* row. `localLookup` is this machine's own player, so
    // a row that belongs to this machine keeps the support classification this machine derived from its own
    // scoring file, whatever role the sender happened to transmit.
    public static void ApplyRemote(CombatPlayer display, in SyncSnapshot snap,
        int primarySlot, int specialSlot, int classSlot, int meleeSlot, ulong localLookup)
    {
        if (display == null) return;
        // A snapshot about this machine's own row can never carry this machine's own counters back to it:
        // the sender's copy of them was built from what this machine broadcast in the first place. Damage
        // still comes through - the host applied the blows and it alone can say what they did - but aim,
        // supplies and scans on this row stay exactly what this machine measured, byte for byte.
        bool own = snap.Owner == localLookup;

        PlayerCombatStats total = display.Total;
        // An estimate never displaces an observation. A host that measured the blow keeps its figure when a
        // peer's health-loss estimate arrives, and that peer keeps nothing but the estimate until a measured
        // figure arrives from the machine that applied it.
        if (AcceptsDamage(total.EnemyDamageObserved, total.EnemyDamageEstimated,
                snap.Has(SyncFlags.EnemyDamageObserved), snap.Has(SyncFlags.EnemyDamageEstimated)))
            total.RestoreEnemyDamage(snap.EnemyDamage, snap.Has(SyncFlags.EnemyDamageObserved), snap.Has(SyncFlags.EnemyDamageEstimated));
        if (AcceptsDamage(total.FriendlyFireObserved, total.FriendlyFireEstimated,
                snap.Has(SyncFlags.FriendlyFireObserved), snap.Has(SyncFlags.FriendlyFireEstimated)))
            total.RestoreFriendlyFireDamage(snap.FriendlyFire, snap.Has(SyncFlags.FriendlyFireObserved), snap.Has(SyncFlags.FriendlyFireEstimated));
        if (!own)
            MergeAccuracy(total, snap.Shots, snap.Hits, snap.WeakHits,
                snap.Has(SyncFlags.AccuracyObserved), snap.Has(SyncFlags.AccuracyEstimated));

        display.StatisticsAvailable = display.StatisticsAvailable || snap.Has(SyncFlags.StatisticsAvailable);
        // Support is a local judgement about a local kit: never let a peer's bit overwrite this machine's own
        // classification of its own row.
        if (!own) display.SupportRole = display.SupportRole || snap.Has(SyncFlags.SupportRole);

        ImpactCounters impact = display.Impact;
        // The three tier totals and the asleep / unknown split of those same totals are one figure. Taking
        // a total from one machine and its split from another is how a cell ends up printing a subset
        // larger than the count in front of it, so the whole group moves together and only a snapshot that
        // counted at least one kill may replace a group this machine already holds.
        if (KillGroupReported(snap))
        {
            impact.MinorKills = snap.KillMinor;
            impact.EliteKills = snap.KillElite;
            impact.BossKills = snap.KillBoss;
            impact.SleepingMinorKills = snap.SleepingMinorKills;
            impact.SleepingEliteKills = snap.SleepingEliteKills;
            impact.SleepingBossKills = snap.SleepingBossKills;
            impact.UnknownStateMinorKills = snap.UnknownStateMinorKills;
            impact.UnknownStateEliteKills = snap.UnknownStateEliteKills;
            impact.UnknownStateBossKills = snap.UnknownStateBossKills;
        }
        if (snap.Has(SyncFlags.MeleeKillsObserved) || AnyFigure(snap.KillMelee, snap.SleepingMeleeKills, snap.UnknownStateMeleeKills))
        {
            impact.MeleeKills = snap.KillMelee;
            impact.MeleeKillsObserved = impact.MeleeKillsObserved || snap.Has(SyncFlags.MeleeKillsObserved);
            impact.SleepingMeleeKills = snap.SleepingMeleeKills;
            impact.UnknownStateMeleeKills = snap.UnknownStateMeleeKills;
        }
        if (snap.Has(SyncFlags.ScoutKillsObserved) || AnyFigure(snap.KillScout, snap.SleepingScoutKills, snap.UnknownStateScoutKills))
        {
            impact.ScoutKills = snap.KillScout;
            impact.ScoutKillsObserved = impact.ScoutKillsObserved || snap.Has(SyncFlags.ScoutKillsObserved);
            impact.SleepingScoutKills = snap.SleepingScoutKills;
            impact.UnknownStateScoutKills = snap.UnknownStateScoutKills;
        }
        // The Scout row's damage is a measurement this machine only has when it made the blow or when the
        // machine that did sent it. A row that reports the flag is taken whole - figure and marker together -
        // and a row that does not is left alone rather than read as zero.
        if (snap.Has2(SyncFlags2.ScoutDamageObserved))
        {
            impact.ScoutDamage = double.IsFinite(snap.ScoutEnemyDamage) && snap.ScoutEnemyDamage > 0 ? snap.ScoutEnemyDamage : 0;
            impact.ScoutDamageObserved = true;
            impact.ScoutDamageEstimated = snap.Has2(SyncFlags2.ScoutDamageEstimated);
        }
        impact.Stagger = MergeCount(impact.Stagger, snap.Stagger);
        impact.Revives = MergeCount(impact.Revives, snap.Revives);
        long downs = MergeCount(impact.Downs, snap.Downs);
        impact.Downs = downs > int.MaxValue ? int.MaxValue : (int)downs;
        long lone = MergeCount(impact.LoneSurvivor, snap.LoneSurvivor);
        impact.LoneSurvivor = lone > int.MaxValue ? int.MaxValue : (int)lone;
        long friendlyDeaths = MergeCount(impact.FriendlyFireDeaths, snap.FriendlyFireDeaths);
        impact.FriendlyFireDeaths = friendlyDeaths > int.MaxValue ? int.MaxValue : (int)friendlyDeaths;
        impact.SentryShots = MergeCount(impact.SentryShots, snap.SentryShots);
        impact.SentryHits = MergeCount(impact.SentryHits, snap.SentryHits);
        impact.SentryDamage = float.IsFinite(snap.SentryDamage) && snap.SentryDamage > 0 ? snap.SentryDamage : impact.SentryDamage;
        impact.MineDeployments = MergeCount(impact.MineDeployments, snap.MineDeployments);
        impact.MinePickups = MergeCount(impact.MinePickups, snap.MinePickups);
        impact.MineTriggers = MergeCount(impact.MineTriggers, snap.MineTriggers);
        impact.MineDamage = float.IsFinite(snap.MineDamage) && snap.MineDamage > 0 ? snap.MineDamage : impact.MineDamage;
        impact.GlueShots = MergeCount(impact.GlueShots, snap.GlueShots);
        impact.GlueHits = MergeCount(impact.GlueHits, snap.GlueHits);
        impact.GluedEnemies = MergeCount(impact.GluedEnemies, snap.GluedEnemies);
        if (!own)
        {
            impact.ScanUses = MergeCount(impact.ScanUses, snap.ScanUses);
            impact.ScanFirstMarks = MergeCount(impact.ScanFirstMarks, snap.ScanFirstMarks);
            impact.ScanMarks = MergeCount(impact.ScanMarks, snap.ScanMarks);
        }
        // The supply pair moves together: the number of uses and the sum they average over are one figure.
        // Taking one without the other is how an incoming row could report a mean built from a denominator
        // the receiving machine never had.
        if (!own && snap.SupplyUses != 0)
        {
            impact.SupplyUses = snap.SupplyUses;
            impact.SupplyEfficiencySum = snap.SupplyEfficiencySum;
        }
        impact.ScanEstimated |= snap.Has(SyncFlags.ScanEstimated);
        // v7: the state split of the kill counters, this player's own supply consumption and the health it
        // actually lost. The kill split follows the plain kill counters (a figure somebody counted travels and
        // a zero never erases one); the consumption pair belongs to the player's own machine, so a received
        // copy never overwrites the row of the machine that counted it; the health figure is an amount, and a
        // zero from a peer never erases one this machine or another peer reported.
        if (!own && snap.SelfSupplyUses != 0)
        {
            impact.SelfSupplyUses = snap.SelfSupplyUses;
            impact.SelfAmmoUses = snap.SelfAmmoUses;
        }
        if (own)
        {
            if (impact.HealthLost <= 0 && snap.HealthLost > 0 && float.IsFinite(snap.HealthLost)) impact.HealthLost = snap.HealthLost;
        }
        else if (snap.HealthLost > 0 && float.IsFinite(snap.HealthLost))
        {
            impact.HealthLost = snap.HealthLost;
        }

        MergeWeapon(display, snap, own, primarySlot, SyncFlags.PrimaryPresent,
            SyncFlags.PrimaryAccuracyObserved, SyncFlags.PrimaryAccuracyEstimated,
            SyncFlags.PrimaryDamageObserved, SyncFlags.PrimaryDamageEstimated,
            SyncFlags.PrimaryFriendlyFireObserved, SyncFlags.PrimaryFriendlyFireEstimated,
            snap.PrimaryArchetypeId, snap.PrimaryGearCategory, snap.GetPrimaryName(),
            snap.PrimaryShots, snap.PrimaryHits, snap.PrimaryWeakHits, snap.PrimaryEnemyDamage, snap.PrimaryFriendlyFire);

        MergeWeapon(display, snap, own, specialSlot, SyncFlags.SpecialPresent,
            SyncFlags.SpecialAccuracyObserved, SyncFlags.SpecialAccuracyEstimated,
            SyncFlags.SpecialDamageObserved, SyncFlags.SpecialDamageEstimated,
            SyncFlags.SpecialFriendlyFireObserved, SyncFlags.SpecialFriendlyFireEstimated,
            snap.SpecialArchetypeId, snap.SpecialGearCategory, snap.GetSpecialName(),
            snap.SpecialShots, snap.SpecialHits, snap.SpecialWeakHits, snap.SpecialEnemyDamage, snap.SpecialFriendlyFire);

        if (snap.Has(SyncFlags.ClassPresent)) display.Weapon(classSlot, snap.ClassArchetypeId, snap.GetClassName(), 0, true);
        if (snap.Has(SyncFlags.MeleePresent))
        {
            WeaponCombatStats melee = display.Weapon(meleeSlot, snap.MeleeArchetypeId, snap.GetMeleeName(), 0, true);
            if (AcceptsDamage(melee.Counters.EnemyDamageObserved, melee.Counters.EnemyDamageEstimated,
                    snap.Has(SyncFlags.MeleeDamageObserved), snap.Has(SyncFlags.MeleeDamageEstimated)))
                melee.Counters.RestoreEnemyDamage(snap.MeleeEnemyDamage,
                    snap.Has(SyncFlags.MeleeDamageObserved), snap.Has(SyncFlags.MeleeDamageEstimated));
        }
    }

    private static void MergeAccuracy(PlayerCombatStats target, long hostShots, long hostHits, long hostWeak,
        bool hostObserved, bool hostEstimated)
    {
        if (target == null) return;
        if (!HostSuppliesAccuracy(target.AccuracyObserved, target.AccuracyEstimated, hostObserved, hostEstimated)) return;
        target.RestoreAccuracy(hostShots, hostHits, hostWeak, hostObserved, hostEstimated);
    }

    private static void MergeWeapon(CombatPlayer display, in SyncSnapshot snap, bool own, int slot, SyncFlags present,
        SyncFlags accuracyObserved, SyncFlags accuracyEstimated,
        SyncFlags damageObserved, SyncFlags damageEstimated,
        SyncFlags friendlyObserved, SyncFlags friendlyEstimated,
        uint archetypeId, uint gearCategoryId, string name,
        long shots, long hits, long weak, double damage, double friendly)
    {
        if (!snap.Has(present)) return;
        WeaponCombatStats weapon = display.Weapon(slot, archetypeId, name, gearCategoryId, true);
        if (weapon == null) return;
        PlayerCombatStats counters = weapon.Counters;
        if (AcceptsDamage(counters.EnemyDamageObserved, counters.EnemyDamageEstimated,
                snap.Has(damageObserved), snap.Has(damageEstimated)))
            counters.RestoreEnemyDamage(damage, snap.Has(damageObserved), snap.Has(damageEstimated));
        if (AcceptsDamage(counters.FriendlyFireObserved, counters.FriendlyFireEstimated,
                snap.Has(friendlyObserved), snap.Has(friendlyEstimated)))
            counters.RestoreFriendlyFireDamage(friendly, snap.Has(friendlyObserved), snap.Has(friendlyEstimated));
        if (!own) MergeAccuracy(counters, shots, hits, weak, snap.Has(accuracyObserved), snap.Has(accuracyEstimated));
    }

    // Whether an incoming figure may replace the one already held: only when it is more exact than what is
    // there. Equal precision never displaces a measurement this machine already holds.
    private static bool AcceptsDamage(bool localPresent, bool localEstimated, bool incomingPresent, bool incomingEstimated)
        => Exactness(incomingPresent, incomingEstimated) > Exactness(localPresent, localEstimated);

    // How exact a figure is. `Observed` on a row in this codebase means "this field has a figure in it", and
    // `Estimated` is the qualifier that says where the figure came from - so a figure that carries both is an
    // estimate, not a measurement, and that distinction is the whole of this method.
    public static int Exactness(bool present, bool estimated) => !present ? 0 : estimated ? 1 : 2;

    // What the host sends about a peer it cannot fully observe itself. It keeps its own measured damage -
    // the host applied every blow and its figure is the authoritative one - and takes from the client only
    // the three things only the client can count exactly: its hit rate, its supply uses and its scans.
    public static SyncSnapshot PreferClientOwned(in SyncSnapshot hostRow, in SyncSnapshot clientRow)
    {
        SyncSnapshot merged = hostRow;
        // The client's own rate is exact: only it counted its rays. The host's copy of that row is built from
        // the shot count the client broadcast, which is an estimate.
        if (clientRow.Has(SyncFlags.AccuracyObserved))
        {
            merged.Shots = clientRow.Shots; merged.Hits = clientRow.Hits; merged.WeakHits = clientRow.WeakHits;
            merged.Set(SyncFlags.AccuracyObserved, true);
            merged.Set(SyncFlags.AccuracyEstimated, clientRow.Has(SyncFlags.AccuracyEstimated));
        }
        if (clientRow.ScanUses != 0 || clientRow.ScanFirstMarks != 0 || clientRow.ScanMarks != 0)
        {
            merged.ScanUses = clientRow.ScanUses; merged.ScanFirstMarks = clientRow.ScanFirstMarks; merged.ScanMarks = clientRow.ScanMarks;
            merged.Set(SyncFlags.ScanEstimated, clientRow.Has(SyncFlags.ScanEstimated));
        }
        if (clientRow.SupplyUses != 0)
        {
            merged.SupplyUses = clientRow.SupplyUses; merged.SupplyEfficiencySum = clientRow.SupplyEfficiencySum;
        }
        // The v7 pair the host cannot count for anybody else: what that player actually consumed and how
        // much of it was ammo. It is folded in with the supply pair above and for the same reason.
        if (clientRow.SelfSupplyUses != 0)
        {
            merged.SelfSupplyUses = clientRow.SelfSupplyUses; merged.SelfAmmoUses = clientRow.SelfAmmoUses;
        }
        return merged;
    }
}

// The raw snapshots this machine has received, kept apart from the capture store. Nothing here is ever
// added to the ledger; a projection reads it and that is all. Two machines can describe the same player -
// the host, which applied the blow, and the player's own machine, which fired the ray - so a second
// description of a row is folded into the one already held field by field (`SyncProtocol.Combine`)
// instead of displacing it. A row from a run older than the one already held is refused here rather than
// being handed to a projection.
public sealed class SnapshotCache
{
    // The run counter is kept per source: the host and the player's own machine count their expeditions
    // independently, so a counter from one of them says nothing about the other's packets.
    private struct Row
    {
        // Keep each source's latest cumulative row separately. A merged snapshot has field-level
        // provenance, so one "last sender" bit cannot safely describe all of its fields.
        public SyncSnapshot HostSnapshot, PeerSnapshot, Snapshot;
        public uint HostGeneration, PeerGeneration;
        public bool HasHost, HasPeer;
    }
    private readonly Dictionary<ulong, Row> _rows = new Dictionary<ulong, Row>();
    // The squad total one host has described, kept as one figure with the run counter it came from.
    // Reading it out of whichever cached row happened to be visited last made the same player's share
    // depend on dictionary order, because every row carries that owner's copy of a different snapshot.
    private double _squadTotal;
    private uint _squadGeneration;
    private bool _squadPartial, _hasSquadTotal;

    public int Count { get { return _rows.Count; } }

    public bool Has(ulong owner) => owner != 0UL && _rows.ContainsKey(owner);

    // Each source sends a cumulative row, so replace that source's previous copy and recombine the two
    // originals. Re-merging a previously merged row with a new packet loses which source won each field;
    // an equal-precision client update could then masquerade as the host and erase host damage.
    public bool Store(ulong owner, uint generation, in SyncSnapshot snapshot, bool fromHost, bool recovered)
    {
        if (owner == 0UL) return false;
        SyncSnapshot incoming = snapshot;
        if (_rows.TryGetValue(owner, out Row existing))
        {
            uint previous = fromHost ? existing.HostGeneration : existing.PeerGeneration;
            bool had = fromHost ? existing.HasHost : existing.HasPeer;
            if (had && !recovered && !SyncProtocol.IsNewerOrSame(generation, previous)) return false;

            // A sender restart starts a new run; do not retain either source's figures from the old run.
            if (had && recovered) existing = default;
            if (fromHost)
            {
                existing.HostSnapshot = incoming;
                existing.HostGeneration = generation;
                existing.HasHost = true;
            }
            else
            {
                existing.PeerSnapshot = incoming;
                existing.PeerGeneration = generation;
                existing.HasPeer = true;
            }
            existing.Snapshot = Compose(existing);
            _rows[owner] = existing;
            ObserveSquadTotal(incoming, generation, fromHost, recovered);
            return true;
        }

        var row = new Row();
        if (fromHost)
        {
            row.HostSnapshot = incoming;
            row.HostGeneration = generation;
            row.HasHost = true;
        }
        else
        {
            row.PeerSnapshot = incoming;
            row.PeerGeneration = generation;
            row.HasPeer = true;
        }
        row.Snapshot = Compose(row);
        _rows[owner] = row;
        ObserveSquadTotal(incoming, generation, fromHost, recovered);
        return true;
    }

    private static SyncSnapshot Compose(in Row row)
    {
        if (row.HasHost && row.HasPeer)
            return SyncProtocol.Combine(row.HostSnapshot, true, row.PeerSnapshot, false);
        return row.HasHost ? row.HostSnapshot : row.PeerSnapshot;
    }

    public bool TryGet(ulong owner, out SyncSnapshot snapshot)
    {
        if (_rows.TryGetValue(owner, out Row row)) { snapshot = row.Snapshot; return true; }
        snapshot = default;
        return false;
    }

    // The newest squad total a host has described, if one has, and whether that total covers the whole
    // squad or only the rows the host could measure. The second answer is what lets every share built on
    // the figure be printed as an estimate instead of being mistaken for the squad's measurement.
    public bool TryGetSquadTotal(out double total) => TryGetSquadTotal(out total, out _);

    public bool TryGetSquadTotal(out double total, out bool partial)
    {
        total = _squadTotal;
        partial = _squadPartial;
        return _hasSquadTotal;
    }

    // Only a host can add up a squad, so only a host row may move this figure. An older run counter is
    // a straggler and never replaces a newer total; a restart (recovered) resets the counter and is
    // therefore taken as newer than whatever the previous run left behind.
    private void ObserveSquadTotal(in SyncSnapshot snapshot, uint generation, bool fromHost, bool recovered)
    {
        if (!fromHost || !snapshot.Has(SyncFlags.SquadDamageKnown)) return;
        if (_hasSquadTotal && !recovered && !SyncProtocol.IsNewerOrSame(generation, _squadGeneration)) return;
        _squadTotal = snapshot.SquadEnemyDamage;
        _squadPartial = snapshot.Has(SyncFlags.SquadDamagePartial);
        _squadGeneration = generation;
        _hasSquadTotal = true;
    }
    public void Remove(ulong owner) { if (owner != 0UL) _rows.Remove(owner); }
    public void Clear() { _rows.Clear(); _hasSquadTotal = false; _squadTotal = 0; _squadPartial = false; _squadGeneration = 0; }
    public IEnumerable<ulong> Owners { get { foreach (ulong owner in _rows.Keys) yield return owner; } }
}
