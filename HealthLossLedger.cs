using System;
using System.Collections.Generic;
using System.Reflection;

namespace HawChow.GTFOCombatPanel;

// How much life a player has actually lost, accumulated from a watermark instead of from a pair of readings
// around one call.
//
// The previous release measured each damage method by reading health before the call and after it, and
// credited the difference. That is exact for the machine that applies the blow and blind for everybody else:
// a host sees a client's health arrive through the replicated health update as often as through a damage
// method, and a figure that moves outside one of the patched methods - a fall through a wrapper, a grab that
// applies its damage from a coroutine, a death handler - was never observed at all. That is the "blood loss
// missing" and the "blood loss too small" a host reports, and no formula on top of a per-call pair fixes it.
//
// A watermark has neither failure. Every observation of a player's health goes through `Observe`: the figure
// is compared with the last one this machine saw for that player, only a decrease is credited, and it is
// credited once no matter how many paths report the same drop. A heal or a revive raises the mark and credits
// nothing.
//
// The mark keeps absolute health points and the maximum they were read against, so a change of the ceiling
// cannot scale an old reading into a figure nobody lost. A ceiling that fell may be the reason a figure is
// lower - the player did not lose life, the maximum under them moved - so that observation only re-bases the
// mark. A ceiling that rose cannot have pressed a figure down, so a drop reported in that same observation is
// real damage and is credited: an infection that raises the maximum must not swallow the bullet that landed
// with it.
//
// The mark belongs to a (damage base, owner) pair, not to a bare native pointer: an object pool that hands the
// same address to another player must not inherit the previous occupant's life, and the first reading of the
// new occupant is a fresh mark rather than a drop against somebody else's figure.
//
// Nothing here touches Unity, SNet or Harmony, so every rule below is asserted headlessly.
internal sealed class HealthLossLedger
{
    private struct Mark
    {
        public double Health, Maximum;
        // 1.19.35 (review fix): this mark was planted by the pre-call reading and the call's own postfix has not observed it
        // yet. `Observe` never sets it, so a mark that still carries it when an observation arrives is a seat whose seeded
        // pair never completed - see `UnsettledSeeds`.
        public bool Seeded;
    }

    // A maximum that moved by less than this is the same maximum, so a float round trip in the replication
    // layer does not re-base the mark on every observation.
    private const double MaximumTolerance = 0.01;

    private readonly Dictionary<long, Mark> _mark = new Dictionary<long, Mark>();

    public int Marked { get { return _mark.Count; } }

    // 1.19.35 (review fix): seats that were seeded before a call and then re-based *upwards* by the next observation - the
    // pair's postfix never ran (the call threw, the player was torn down with it), so the blow that consumed the seeded life
    // is not knowable from here: a revive or a heal wrote the figure back up and nothing ever measured the drop. Such a seat
    // is counted here and never credited, because a figure inferred from "what the seat was seeded at" would be the
    // fabrication this metric may not take; the counter exists so that the loss shows up in the diagnostic line instead of
    // vanishing. It is a count of seats, not a measurement of life.
    private int _unsettledSeeds;
    public int UnsettledSeeds { get { return _unsettledSeeds; } }

    // One observation of a player's health and maximum. Returns the life lost since the last observation, in
    // absolute health points, and never a negative figure. The first observation of a player only sets the
    // mark: this machine did not see that player live before it, so it cannot claim to have seen it lost.
    internal double Observe(IntPtr target, ulong owner, double health, double maximum)
    {
        if (target == IntPtr.Zero) return 0;
        if (!double.IsFinite(health) || !double.IsFinite(maximum) || maximum <= 0) return 0;
        long key = Key(target, owner);
        double now = Math.Max(0, Math.Min(health, maximum));       // overkill is not life that was there
        if (!_mark.TryGetValue(key, out Mark previous))
        {
            _mark[key] = new Mark { Health = now, Maximum = maximum };
            return 0;
        }
        _mark[key] = new Mark { Health = now, Maximum = maximum };
        bool ceilingRose = maximum > previous.Maximum + MaximumTolerance;
        bool ceilingMoved = Math.Abs(previous.Maximum - maximum) > MaximumTolerance;
        // 1.19.35 (review fix): the seat was seeded before a call whose own postfix never observed it, and the figure has come
        // back *up* since - a revive or a heal wrote it back before anything measured the drop. The blow that consumed the
        // seeded life is therefore not knowable here, and it is counted rather than credited: crediting it would mean guessing
        // its size from the seed, and this metric may not guess. `>` and not `>=`: a seeded pair whose figure did not move is
        // an ordinary call that changed nothing, not a lost blow.
        if (previous.Seeded && now > previous.Health) _unsettledSeeds++;
        // The ceiling fell: the lower figure may be the ceiling's doing rather than damage, so nothing is
        // credited and the mark above is the re-base. Any other case is measured as plain blood.
        if (ceilingMoved && !ceilingRose) return 0;
        return now < previous.Health ? previous.Health - now : 0;
    }

    // One reading taken *before* a damage-base call runs, used only to give a player who has no mark yet the life they
    // really had when the blow began (1.19.33), and only for a call that *is* a blow (1.19.35).
    //
    // The defect this exists for: a brand-new player's first observation used to be the postfix of the very blow that hit
    // them, and a first observation only seeds the mark - so that blow was credited to nobody. The recorded 2026-11-13 Test
    // run 3 is shaped exactly like that: `healthLost=23.7` beside a 25.0 friendly-fire figure made of 20 hits of
    // ~1.25467 HP, i.e. one first hit's worth of life missing. Seeding the mark at full health instead would be a guess:
    // a player may come into a level already hurt, join mid-run, or be revived at part health, and the ledger would then
    // invent a loss nobody took. So the entry point reads the real figure.
    //
    // 1.19.35: the entry point is now restricted to the calls that report a hit (`HealthLossTargets.Seedable`). A method
    // that *writes* a health figure - `Setup` for the next occupant of a pooled damage base, `OnRevive`, a replicated
    // `ReceiveSetHealth` - is not a blow, and seeding its pre-call figure credited the write itself: an object pool's old
    // 100 followed by a spawn write of 20 was reported as 80 HP of damage that was never taken. The cost of the
    // restriction is bounded and stated: the *first* figure a write reports for a player this machine has never seen only
    // plants the mark (as it did before 1.19.33) instead of being credited, because no local fact distinguishes a
    // replicated hit from a spawn/reset write behind that same method name.
    //
    // This never credits anything and never moves an existing mark: `Observe` stays the only place a loss comes from, and
    // the pre-call figure is the mark the call's own postfix is measured against. A call that heals only raises that mark
    // afterwards (no credit), a call that kills is credited against the life that was really there, and two nested calls
    // seed once and credit once.
    internal bool Seed(IntPtr target, ulong owner, double health, double maximum)
    {
        if (target == IntPtr.Zero) return false;
        if (!double.IsFinite(health) || !double.IsFinite(maximum) || maximum <= 0) return false;
        long key = Key(target, owner);
        if (_mark.ContainsKey(key)) return false;
        _mark[key] = new Mark { Health = Math.Max(0, Math.Min(health, maximum)), Maximum = maximum, Seeded = true };
        return true;
    }

    // A seat that changed hands, or a row that was pruned: nothing of the previous occupant survives.
    internal void Forget(IntPtr target, ulong owner)
    {
        if (target != IntPtr.Zero) _mark.Remove(Key(target, owner));
    }

    // 1.19.35 (review fix): a new expedition forgets the marks *and* the count of seats whose seeded pair never completed,
    // so the diagnostic line that reads it speaks about this run only.
    internal void Clear() { _mark.Clear(); _unsettledSeeds = 0; }

    private static long Key(IntPtr target, ulong owner)
    {
        long pointer = target.ToInt64();
        unchecked { return pointer * 397L ^ (long)owner; }
    }
}

// Which of a player's damage-base methods this mod observes for blood loss, and which of them may plant the pre-call mark.
//
// Everything that reports a figure is observed, because the watermark ledger only credits a real decrease and
// therefore needs no exclusion list to stay correct. There is exactly one exception, and it is a crash
// mitigation rather than a change of policy: `ReceiveParasiteDamage` is left unpatched so the game's own
// method carries no detour of this mod at all. Two sessions on one machine - one host, one client, the same
// rundown layout - ended in the same `Stack overflow; Repeat 1526 times; DMD<ReceiveParasiteDamage> <->
// IL2CPP runtime invoke` signature, and this mod's only detour anywhere near that call is the postfix below,
// which observes every `Receive*` of `Dam_PlayerDamageBase`. The link is a correlation and not a proof: no
// callback frame of this mod appears in those stacks. Leaving that single method alone is the smallest change
// that removes the correlation. The game's damage is untouched and every other `Receive*` still reports, but the
// cost is real and it is not merely a delay: the ledger only credits a decrease it *observes*, so a drop caused by
// a parasite tick that no other observed event reports is not sampled at all. Whether the next observation recovers
// it depends on what that observation is - a later decrease (any other `Receive*`, `Setup`, `OnRevive`,
// `OnApplyDamageOverTimeModifier`, `OnKilledByTankTentacle`) credits the accumulated difference, but a heal, a revive
// or a maximum-health change lands first often enough, and its watermark re-bases upward, after which the parasite
// damage is gone from `HealthLost` for good. The metric is therefore allowed to under-count for that one damage kind;
// no line anywhere may promise that the next observation recovers it.
//
// Nothing else may be added here: a re-entry guard was considered and rejected, because the reported stack
// never returns to this postfix - the loop lives inside the IL2CPP bridge, where no postfix gets a frame.
//
// 1.19.35 adds the second list, `Seedable`. `Dam_PlayerDamageBase`'s own methods (read from the interop assembly) split
// into two kinds, and only one of them is a blow:
//
//   damage reports   ReceiveBulletDamage, ReceiveExplosionDamage, ReceiveMeleeDamage, ReceiveFireDamage,
//                    ReceivePushDamage, ReceiveTentacleAttackDamage, ReceiveTentacleTrapGrabDamage,
//                    ReceiveParasiteTrapDamage, ReceiveTentacleTankGrabDamage, ReceiveTentacleGrabStop,
//                    ReceiveShooterProjectileDamage, ReceiveFallDamage, ReceiveNoAirDamage, ReceiveStickyDamage,
//                    ReceiveParasiteDamage (excluded above)          each one *is* the hit, so its before/after pair
//                                                                     is damage and its pre-call figure may be the mark;
//   health writes    Setup, OnRevive, ReceiveSetHealth, ReceiveAddHealth, ReceiveSetDead, ReceiveModifyInfection,
//                    OnApplyDamageOverTimeModifier                     each one *sets* a figure (spawn, revive, heal,
//                                                                     death, infection); its pair is a state change, and
//                                                                     seeding it is how a spawn write of 20 after the
//                                                                     pooled seat's old 100 became 80 HP of "damage".
//
// The rule is a name suffix rather than a hand-kept list of the fourteen, so a damage report this build has never seen
// is still seeded (the game names all of them `...Damage`), and the writes above are excluded by construction - none of
// them ends in `Damage`. `ReceiveTentacleGrabStop` is the one exception in the other direction (it carries a
// `pMiniDamageData` and does not follow the suffix), so it is named on its own.
public static class HealthLossTargets
{
    // The one method this mod deliberately does not detour.
    public const string ParasiteDamage = "ReceiveParasiteDamage";

    // Every `Receive*` of the damage base is observed, except that one, and the named events below are
    // observed for the reasons the ledger documents (a heal, a revive, a tentacle kill and the spawn seed).
    public static bool Accepts(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name == ParasiteDamage) return false;
        if (name.StartsWith("Receive", StringComparison.Ordinal)) return true;
        return name == "OnApplyDamageOverTimeModifier" || name == "OnRevive"
            || name == "OnKilledByTankTentacle" || name == "Setup";
    }

    // Whether a call of this method may plant the pre-call mark (1.19.35). True only for the accepted methods whose
    // before/after pair really is a hit; a health write is observed by the postfix but never seeded, so it can move the
    // watermark and can never be credited from a mark this build invented for it.
    public static bool Seedable(string name)
    {
        if (!Accepts(name)) return false;
        if (name.EndsWith("Damage", StringComparison.Ordinal)) return true;
        // The one damage report whose name does not follow the suffix (`pMiniDamageData`).
        return name == "ReceiveTentacleGrabStop";
    }
}

// ---------------------------------------------------------------------------------------------------
// Which declaring types the observation is attached to, and what a missing one costs
// ---------------------------------------------------------------------------------------------------
//
// 1.19.37: the damage base a live player really carries is `Dam_PlayerDamageLocal : Dam_PlayerDamageBase`, and it is
// the only subclass in the assembly. It declares nineteen methods of its own - `Setup`, `OnRevive`, `OnGodModeUpdated`,
// `ReceiveSetHealth`, `ReceiveSetDead` and every `Receive*Damage` except `ReceiveAddHealth`, `ReceiveModifyInfection`,
// `ReceiveTentacleGrabStop`, `OnKilledByTankTentacle` and `SetupPackages` - and each of those is a *virtual override*
// of a base slot. A detour on the base implementation is therefore never executed for a live player: the call
// dispatches to the subclass' own native method, which is the recorded defect (the host's own row kept `healthLost=0`
// for the whole run while all three bots - whose seats the base's replicated writes do reach - showed 25.0, and the
// patch receipt named only `Dam_PlayerDamageBase.*`).
//
// So the target list is read from *both* declaring types, each with `DeclaredOnly`: the two sets are disjoint by
// construction, which is what makes "one method, one detour" cheap to keep.
//
// The subclass is resolved by name rather than referenced at compile time, because the interop assembly belongs to the
// profile that is being launched: a profile whose interop does not carry the subclass must still build and still
// observe everything the base declares. `DeclaringTypes` is that degradation, and it is a plain function of two
// `Type`s so a headless suite can drive it (`Dam_PlayerDamageLocal` does not exist outside a modded runtime).
public static class HealthLossSources
{
    // The subclass this mod looks for, by the simple name the interop gives it.
    public const string LocalTypeName = "Dam_PlayerDamageLocal";

    // The declaring types to read, in order: the base - which this build names directly and which always exists - and
    // then the subclass when it is a real subclass. A null, absent or unrelated answer is the degraded set of one, and
    // nothing is thrown: the degradation costs the machine's own seat its observations, never the whole ledger.
    public static Type[] DeclaringTypes(Type baseType, Type localType)
    {
        if (baseType == null) return Array.Empty<Type>();
        bool usable = localType != null && localType != baseType && baseType.IsAssignableFrom(localType);
        return usable ? new[] { baseType, localType } : new[] { baseType };
    }

    // Every method of those types this mod detours: the type's own declarations (`DeclaredOnly`), the void ones the
    // name rule accepts, each at most once.
    //
    // The dedupe key is the `MethodBase` itself, held in a set whose identity is the method's declaring type plus its
    // metadata token: two declaring types never share a token, so a repeated type in the list - or a future interop
    // that lists an override beside its base - still produces one detour per method. Patching one method twice is not a
    // cosmetic fault: the second prefix would find the mark the first planted and the second postfix would credit the
    // drop once, so the loss itself is safe, but Harmony counts two patches and a removal leaves one behind.
    public static IEnumerable<MethodBase> DeclaredMethods(IEnumerable<Type> types, Func<string, bool> accept)
    {
        if (types == null || accept == null) yield break;
        var seen = new HashSet<MethodBase>(MethodIdentity.Instance);
        foreach (Type type in types)
        {
            if (type == null) continue;
            MethodInfo[] declared;
            try
            {
                declared = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly);
            }
            catch (Exception) { continue; }     // a type this runtime will not enumerate contributes nothing
            foreach (MethodInfo method in declared)
            {
                if (method == null || method.IsAbstract || method.ContainsGenericParameters) continue;
                if (!ReturnsVoid(method)) continue;
                // One shared name rule for both declaring types: `Accepts`/`Seedable` never learn which type they are
                // being asked about, so `ReceiveParasiteDamage` is excluded on the subclass exactly as on the base.
                if (!accept(method.Name)) continue;
                if (!seen.Add(method)) continue;
                yield return method;
            }
        }
    }

    private static bool ReturnsVoid(MethodInfo method)
    {
        try { return method.ReturnType == typeof(void); }
        catch (Exception) { return false; }
    }

    // The name a line may print for the set it read: `base` alone is the degraded answer, and it says so.
    public static string Describe(IEnumerable<Type> types)
    {
        var names = new List<string>();
        if (types != null)
            foreach (Type type in types)
                if (type != null) names.Add(type.Name);
        if (names.Count == 0) return "none";
        return names.Count == 1 ? names[0] + " only" : string.Join(" + ", names);
    }

    // Two methods are one method when their declaring type and their metadata token agree. That is the metadata
    // identity Harmony itself keys a patch on, and it does not depend on the reflection objects being the same instance.
    private sealed class MethodIdentity : IEqualityComparer<MethodBase>
    {
        internal static readonly IEqualityComparer<MethodBase> Instance = new MethodIdentity();

        public bool Equals(MethodBase left, MethodBase right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return false;
            if (left.DeclaringType != right.DeclaringType) return false;
            return Token(left) == Token(right);
        }

        public int GetHashCode(MethodBase method)
        {
            if (method == null) return 0;
            int type = method.DeclaringType != null ? method.DeclaringType.GetHashCode() : 0;
            unchecked { return type * 397 ^ Token(method); }
        }

        private static int Token(MethodBase method)
        {
            // A dynamic method has no metadata token; it has no declaring type to patch either, so 0 cannot collide with
            // a real token of the same declaring type (which is always at least 0x06000000).
            try { return method.MetadataToken; } catch (Exception) { return 0; }
        }
    }
}
