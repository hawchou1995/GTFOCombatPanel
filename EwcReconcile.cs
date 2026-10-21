using System;

namespace HawChow.GTFOCombatPanel;

// One weapon's figures, in the shape both sides of the EWC reconciliation speak: how many pellets were
// fired, how many of them landed, how many of those were weakspots. A delta carries the same three
// fields and may be negative, which is how a retraction travels.
public readonly struct AccuracyCounts
{
    public readonly int Fired;
    public readonly int Hits;
    public readonly int Weak;

    public AccuracyCounts(int fired, int hits, int weak) { Fired = fired; Hits = hits; Weak = weak; }
}

// A weapon ExtraWeaponCustomization has taken over, turned into the row this panel draws. No Unity and no
// EWC in sight, so the headless suite drives the exact rules the live bridge calls.
//
// What EWC actually reports, read out of its own build rather than assumed:
//
//   * `AccuracyManager.InitShot` publishes one delta when a weapon fires, and it carries the shot count.
//   * `AccuracyManager.AddHit` publishes a *separate* delta when a pellet lands, and it carries hits and
//     crits with `Count = 0`. A hit is therefore normally announced by an update that fired nothing.
//   * `AccuracyManager.CancelShot` publishes a negative count to take a shot back.
//   * every delta is added into a cumulative per-weapon total (`StatInfo.Count/Hits/Crits`) *before* the
//     callback runs, so the cumulative figures are the truth and the deltas are only how it is delivered.
//   * the accuracy API does **not** cover every weapon EWC manages. Its own other streams do:
//     `FireShotAPI.PreShotFired` carries the shot of a weapon the accuracy API never reports, and
//     `ProjectileAPI.OnProjectileHit` carries the hit of a projectile weapon. A weapon with neither of
//     those counted is on the row as nothing at all - which is what a 涡轮电浆枪 showed: 0 fired / 0 hit /
//     0 damage while it was being used.
//
// Reading each update as "one complete shot" and skipping the ones that fired nothing is what left an EWC
// weapon showing shots fired and never a hit, and it is the defect this file replaces. The cumulative
// figures are consumed instead, which makes a zero-count hit update ordinary, makes a retraction a
// correction rather than noise, and makes a repeated update harmless.
//
// The row reads exactly one of EWC's three levels - the pellet level (`Shots`) or the whole-shot level
// (`FullShots`) - and stays on it, so the pellets counted and the hits counted are always the same kind
// of thing.
public static class EwcReconcile
{
    // Which of EWC's levels a weapon's figures are read from. Decided once, from the first update the
    // weapon produces, and then held: a level that is chosen per update could take pellets from one
    // level and hits from another, and a rate built from two different counters is not a rate.
    public static bool PreferFullShot(int shotsCount, int fullCount, bool known, bool previous)
    {
        if (known) return previous;
        return fullCount > shotsCount;
    }

    // The figures the row should end up showing: EWC's own cumulative counts, with this machine's
    // observations used only to fill what EWC did not count. Both sides are cumulative, so the
    // reconciliation for each field is a `max` - a shot or a hit that both sides counted is counted
    // once, and a shot or a hit only one side counted still reaches the row. `filled` says the row is no
    // longer purely EWC's, which is what marks it as an estimate.
    //
    // The observed side carries a shot count as well as a hit count, because EWC's accuracy API does not
    // cover every weapon it manages: `FireShotAPI.PreShotFired` is where the shots of those weapons are
    // seen, and a weapon whose shots arrive that way has to be able to report them.
    public static AccuracyCounts Resolve(AccuracyCounts ewc, AccuracyCounts observed, out bool filled)
    {
        int reportedFired = Math.Max(0, ewc.Fired);
        int seenFired = Math.Max(0, observed.Fired);
        int fired = Math.Max(reportedFired, seenFired);
        int reported = Bound(ewc.Hits, fired);
        int seen = Bound(observed.Hits, fired);
        int hits = Math.Max(reported, seen);
        filled = hits > reported || fired > reportedFired;
        int weak = Math.Max(Bound(ewc.Weak, hits), filled ? Bound(observed.Weak, hits) : 0);
        return new AccuracyCounts(fired, hits, Math.Min(weak, hits));
    }

    // What still has to be handed to the panel for a row to reach `target`. Negative fields are
    // retractions and are passed through: the counter API floors a row at zero by itself.
    public static AccuracyCounts Delta(AccuracyCounts applied, AccuracyCounts target)
        => new AccuracyCounts(target.Fired - applied.Fired, target.Hits - applied.Hits, target.Weak - applied.Weak);

    public static bool IsEmpty(AccuracyCounts counts) => counts.Fired == 0 && counts.Hits == 0 && counts.Weak == 0;

    // A projectile announces an impact, not a hit: it fires for the wall, the floor and the ceiling exactly as
    // it fires for an enemy. Only an enemy impact is a hit, and the scenery rules the damage path already
    // uses apply here too, so a container lock or a door lock cannot become a hit either.
    public static bool ProjectileHitCounts(bool targetIsEnemyDamage, bool targetHasOwner, bool targetIsStaticScenery)
        => targetIsEnemyDamage && targetHasOwner && !targetIsStaticScenery;

    // What a row EWC has never reported may still be handed to the panel: this machine's own observations,
    // reconciled exactly the way EWC's cumulative totals are. `applied` is what has already been handed
    // over, so a second call hands over nothing and no pellet is ever counted twice.
    //
    // A row EWC does report hands over nothing here. Its own update is the authority, and it already folds
    // these same observations in with the `max` above - which is why the fallback cannot be a second source
    // of numbers, only the one that moves a row EWC never touches.
    public static AccuracyCounts FallbackHandover(bool ewcReports, AccuracyCounts applied, AccuracyCounts observed)
    {
        if (ewcReports) return default;
        AccuracyCounts target = Resolve(new AccuracyCounts(0, 0, 0), observed, out _);
        return Delta(applied, target);
    }

    private static int Bound(int value, int ceiling)
    {
        if (value < 0) return 0;
        return value > ceiling ? ceiling : value;
    }
}
