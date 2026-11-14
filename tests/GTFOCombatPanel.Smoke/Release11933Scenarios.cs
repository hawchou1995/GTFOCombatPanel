using System;
using System.Collections.Generic;
using System.IO;
using HawChow.GTFOCombatPanel;

// 1.19.33: the first blow a machine sees for a player was credited to nobody.
//
// The ledger credits only a *decrease against a mark*, and the first observation of a (damage base, owner) pair only
// plants that mark ("this machine did not see that player live before it"). `Setup` is patched to plant it at spawn,
// but `Setup` runs when the damage base is set up - not at the moment this machine is allowed to track that owner - so
// the first event a run can observe for a player is the damage event that hits them, and that blow became the mark.
//
// The recorded 2026-11-13 Test run 3 has exactly that shape: `healthLost=23.7` in
// `Results\CombatPanel-results-20261113-155055-476-r3-...-success.json` for Dauda (阵亡 1 次) against a 25.0
// friendly-fire total made of 20 hits of ~1.25467 HP each - one first hit's worth of life missing. The JSON is a file
// fact; the "25.0 / 20 hits" half is the session's own reading, and the two only agree through this mechanism.
//
// The repair does not guess: the damage base's own `Health` is read *before* the call (`Ledger.Seed`) and used as the
// mark, so a player who came into the level hurt, joined mid-run or was revived at part health is seeded where they
// really were. `HealthMax` is never substituted - that would credit life nobody lost - and the seed never credits or
// moves an existing mark, so `Observe` stays the only source of a loss.
//
// What cannot be asserted here: Unity, Harmony and the real dispatch. `Dam_PlayerDamageBase`'s `Receive*` are overrides
// of `Dam_SyncedDamageBase` slots (verified in Modules-ASM.dll with Cecil: virtual, not newslot), the live component is
// `Dam_PlayerDamageLocal`, and whether a given derived override funnels into the base method is not observable from the
// interop assembly. The assertions below drive the ledger and the patch source those calls reach.
internal static class Release11933Scenarios
{
    private const ulong Lookup = 0xD00DAUL;
    private static readonly IntPtr Seat = (IntPtr)0x1A2B3C4D;

    public static void RunInto(Action<bool, string> check)
    {
        if (check == null) throw new ArgumentNullException(nameof(check));
        TheFirstBlowOfARunIsCredited(check);
        TheFirstMarkIsTheRealHealthNotTheCeiling(check);
        SeedingNeverOverwritesAndNeverCredits(check);
        ADyingBlowIsCreditedAgainstTheLifeThatWasThere(check);
        NestedCallsCreditOnce(check);
        HealingRevivingAndCeilingChangesAreNotDamage(check);
        ReadingsThatAreNotNumbersSeedNothing(check);
        EveryTrackedOwnerKeepsItsOwnMark(check);
        ANewExpeditionStartsFromTheRealHealthAgain(check);
        OnlyADamageReportMaySeed(check);
        AHealthWriteIsNeverCreditedFromAPooledSeatsMark(check);
        Wiring(check);
    }

    // ---- 1. the recorded run, replayed through the production ledger ------------------------------------------------------

    private static void TheFirstBlowOfARunIsCredited(Action<bool, string> check)
    {
        // The recorded sequence: 20 hits of ~1.25467 HP out of 25.0 life, the last one killing.
        double health = 25.0, credited = 0.0, first = -1.0;
        int creditedHits = 0;
        var ledger = new HealthLossLedger();
        for (int hit = 1; hit <= 20; hit++)
        {
            double before = health;
            health = Math.Max(0.0, before - 1.25467);
            ledger.Seed(Seat, Lookup, before, 25.0);                     // the prefix: the real pre-call health
            double loss = ledger.Observe(Seat, Lookup, health, 25.0);     // the postfix
            if (loss > 0) creditedHits++;
            if (hit == 1) first = loss;
            credited += loss;
        }
        check(Math.Abs(first - 1.25467) < 1e-6,
            "the very first blow of the run is credited in full (" + first.ToString("0.#####") + " HP) instead of only planting the mark");
        check(creditedHits == 20, "every one of the 20 hits is credited: " + creditedHits);
        check(Math.Abs(credited - 25.0) < 1e-9,
            "and the total is the whole 25.0 the player really lost, with the killing hit credited only for the life that was left: " + credited.ToString("0.#####"));

        // The same sequence without the pre-call reading, which is the defect this round repairs: the first hit plants
        // the mark and is worth nothing, so the total is exactly one hit short - 25.0 - 1.25467 = 23.74533, which is the
        // recorded `healthLost=23.7` at one decimal.
        double unseededHealth = 25.0, unseeded = 0.0;
        var blind = new HealthLossLedger();
        for (int hit = 1; hit <= 20; hit++)
        {
            unseededHealth = Math.Max(0.0, unseededHealth - 1.25467);
            unseeded += blind.Observe(Seat, Lookup, unseededHealth, 25.0);
        }
        check(Math.Abs(unseeded - 23.74533) < 1e-5 && unseeded < 25.0,
            "without it the same 20 hits credit exactly one hit less - the recorded 23.7 beside a 25.0 total: " + unseeded.ToString("0.#####"));
    }

    private static void TheFirstMarkIsTheRealHealthNotTheCeiling(Action<bool, string> check)
    {
        // A player this machine meets at half health: the mark is 12.5, so the next blow is worth 1.5 - not 13.5, and
        // not 11.0. Substituting `HealthMax` here would be the invented loss this shape exists to prevent.
        var half = new HealthLossLedger();
        check(half.Seed(Seat, Lookup, 12.5, 25.0),
            "a player who is already hurt gets a mark from the life they really had");
        check(Near(half.Observe(Seat, Lookup, 11.0, 25.0), 1.5),
            "and only the drop of that call is credited: 12.5 -> 11.0 is 1.5 HP, never 12.0 and never 13.5");
        check(Near(half.Observe(Seat, Lookup, 11.0, 25.0), 0.0),
            "the same figure reported again by another path still credits nothing");

        // A player at full health is the ordinary case, and it is the same rule - the figure is read, not assumed.
        var full = new HealthLossLedger();
        full.Seed(Seat, Lookup, 100.0, 100.0);
        check(Near(full.Observe(Seat, Lookup, 98.74533, 100.0), 1.25467),
            "a full-health player's first blow is measured from 100, because 100 is what the game reported");
    }

    private static void SeedingNeverOverwritesAndNeverCredits(Action<bool, string> check)
    {
        var ledger = new HealthLossLedger();
        check(ledger.Seed(Seat, Lookup, 100.0, 100.0), "the first pre-call reading plants the mark");
        check(!ledger.Seed(Seat, Lookup, 100.0, 100.0), "and a second one for the same call changes nothing");
        check(Near(ledger.Observe(Seat, Lookup, 90.0, 100.0), 10.0), "the postfix then credits the drop");
        check(!ledger.Seed(Seat, Lookup, 95.0, 100.0),
            "a later pre-call reading never overwrites the mark - it is the postfix's watermark and nothing else may move it");
        check(Near(ledger.Observe(Seat, Lookup, 88.0, 100.0), 2.0),
            "so the next drop is measured from 90 and not from the reading the seed refused: " + ledger.Marked);
        check(ledger.Marked == 1, "one player, one mark: " + ledger.Marked);
    }

    private static void ADyingBlowIsCreditedAgainstTheLifeThatWasThere(Action<bool, string> check)
    {
        var killed = new HealthLossLedger();
        killed.Seed(Seat, Lookup, 100.0, 100.0);
        check(Near(killed.Observe(Seat, Lookup, 0.0, 100.0), 100.0),
            "a blow that kills on the first observation is credited in full");

        var overkill = new HealthLossLedger();
        overkill.Seed(Seat, Lookup, 10.0, 100.0);
        check(Near(overkill.Observe(Seat, Lookup, -20.0, 100.0), 10.0),
            "and one that overkills is credited only for the life that was there");
    }

    private static void NestedCallsCreditOnce(Action<bool, string> check)
    {
        // The outer call seeds; the inner call (same base, same owner, already marked) seeds nothing; the inner postfix
        // credits the drop; the outer postfix finds it already counted.
        var ledger = new HealthLossLedger();
        check(ledger.Seed(Seat, Lookup, 100.0, 100.0), "the outer call plants the mark");
        check(!ledger.Seed(Seat, Lookup, 100.0, 100.0), "the nested call finds it and leaves it alone");
        double inner = ledger.Observe(Seat, Lookup, 98.74533, 100.0);
        double outer = ledger.Observe(Seat, Lookup, 98.74533, 100.0);
        check(Near(inner, 1.25467) && Near(outer, 0.0),
            "nested receive calls credit the drop once: inner=" + inner.ToString("0.#####") + ", outer=" + outer.ToString("0.#####"));

        // Two different paths reporting the same new figure (the damage event and the replicated health update) are the
        // same case, and it is already the ledger's own rule.
        var both = new HealthLossLedger();
        both.Seed(Seat, Lookup, 50.0, 50.0);
        check(Near(both.Observe(Seat, Lookup, 40.0, 50.0), 10.0) && Near(both.Observe(Seat, Lookup, 40.0, 50.0), 0.0),
            "a drop reported twice is credited once");
    }

    private static void HealingRevivingAndCeilingChangesAreNotDamage(Action<bool, string> check)
    {
        var ledger = new HealthLossLedger();
        ledger.Seed(Seat, Lookup, 100.0, 100.0);
        check(Near(ledger.Observe(Seat, Lookup, 0.0, 100.0), 100.0), "the run's damage is credited");
        check(Near(ledger.Observe(Seat, Lookup, 20.0, 100.0), 0.0),
            "a revive at part health is a rise: no credit, and the mark re-bases to 20");
        check(!ledger.Seed(Seat, Lookup, 20.0, 100.0), "the revive's own call cannot re-seed over that mark either");
        check(Near(ledger.Observe(Seat, Lookup, 5.0, 100.0), 15.0),
            "so the first damage after the revive is measured from the 20 they were actually given");
        check(Near(ledger.Observe(Seat, Lookup, 5.0, 150.0), 0.0),
            "a maximum that rises is not damage: the reading only re-bases");
        check(Near(ledger.Observe(Seat, Lookup, 5.0, 80.0), 0.0),
            "and a maximum that drops is not damage either - the life did not go down, the ceiling did");
        check(Near(ledger.Observe(Seat, Lookup, 2.0, 80.0), 3.0), "the next drop is then measured against the new ceiling");

        // A healing call's own pair: seed at 40, postfix sees 80. A heal is never blood loss.
        IntPtr healed = (IntPtr)0x5151;
        var heal = new HealthLossLedger();
        heal.Seed(healed, Lookup, 40.0, 100.0);
        check(Near(heal.Observe(healed, Lookup, 80.0, 100.0), 0.0),
            "a healing receive call raises the mark and credits nothing");
        check(Near(heal.Observe(healed, Lookup, 70.0, 100.0), 10.0), "and the next damage after it is credited normally");
    }

    private static void ReadingsThatAreNotNumbersSeedNothing(Action<bool, string> check)
    {
        var ledger = new HealthLossLedger();
        check(!ledger.Seed(IntPtr.Zero, Lookup, 50.0, 100.0), "an unknown base identity never seeds");
        check(!ledger.Seed(Seat, Lookup, double.NaN, 100.0), "an unreadable health never seeds");
        check(!ledger.Seed(Seat, Lookup, 50.0, double.PositiveInfinity), "an unreadable maximum never seeds");
        check(!ledger.Seed(Seat, Lookup, 50.0, 0.0) && !ledger.Seed(Seat, Lookup, 50.0, -1.0),
            "a maximum of zero or less never seeds");
        check(ledger.Marked == 0, "none of those readings left a mark: " + ledger.Marked);
        check(Near(ledger.Observe(Seat, Lookup, 50.0, 100.0), 0.0),
            "so the first real reading is still the first reading - it seeds and claims nothing");
        check(Near(ledger.Observe(Seat, Lookup, 40.0, 100.0), 10.0), "and the drop after it is credited");
    }

    private static void EveryTrackedOwnerKeepsItsOwnMark(Action<bool, string> check)
    {
        // A host tracks every slotted owner (its own seat, a client's and a bot's), and each one is a separate mark on
        // its own damage base. `ReviveAttributionLedger.CanTrackOwner` is what the patch gates on, and it is asserted in
        // the revive scenarios; what is asserted here is that the ledger never mixes their lives up.
        IntPtr hostSeat = (IntPtr)0x11, clientSeat = (IntPtr)0x22, botSeat = (IntPtr)0x33;
        var ledger = new HealthLossLedger();
        ledger.Seed(hostSeat, 0xA1UL, 100.0, 100.0);
        ledger.Seed(clientSeat, 0xB2UL, 40.0, 100.0);
        ledger.Seed(botSeat, 0xC3UL, 80.0, 100.0);
        check(Near(ledger.Observe(hostSeat, 0xA1UL, 90.0, 100.0), 10.0)
              && Near(ledger.Observe(clientSeat, 0xB2UL, 30.0, 100.0), 10.0)
              && Near(ledger.Observe(botSeat, 0xC3UL, 70.0, 100.0), 10.0),
            "the host's own row, a client's row and a bot's row are three independent marks");
        check(ledger.Marked == 3, "three players, three marks: " + ledger.Marked);
        check(Near(ledger.Observe(clientSeat, 0xB2UL, 30.0, 100.0), 0.0),
            "and a repeated figure on one of them cannot touch another one");
    }

    private static void ANewExpeditionStartsFromTheRealHealthAgain(Action<bool, string> check)
    {
        var ledger = new HealthLossLedger();
        ledger.Seed(Seat, Lookup, 100.0, 100.0);
        check(Near(ledger.Observe(Seat, Lookup, 90.0, 100.0), 10.0), "the run credits its own damage");
        ledger.Clear();     // CombatTracker's per-expedition reset
        check(ledger.Marked == 0, "a new expedition forgets every mark");
        check(Near(ledger.Observe(Seat, Lookup, 80.0, 100.0), 0.0),
            "so the first reading of the new run claims nothing - the previous run's life is not carried over");
        check(Near(ledger.Observe(Seat, Lookup, 55.0, 100.0), 25.0),
            "and the damage after it is credited from that first reading: 80 -> 55 is 25 HP");

        // The first blow of a new run, through the pre-call reading this round adds: the mark is the life that blow found,
        // so a player who starts the second expedition hurt is measured from where they really are.
        var next = new HealthLossLedger();
        check(next.Seed(Seat, Lookup, 60.0, 100.0), "the first blow's pre-call reading plants the new run's mark");
        check(Near(next.Observe(Seat, Lookup, 55.0, 100.0), 5.0), "whose drop is then credited normally: 60 -> 55 is 5 HP");
    }

    // ---- 1b. 1.19.35: the pre-call mark belongs to the calls that report a hit -------------------------------------------------

    // The two native method families, read out of the interop assembly (`Dam_PlayerDamageBase`), one name at a time. A
    // health write sets a figure and is never a blow; a damage report is the blow itself. This is the whole rule the two
    // patch target lists are built from, so it is asserted against the real names rather than against a shape.
    private static readonly string[] DamageReports =
    {
        "ReceiveBulletDamage", "ReceiveExplosionDamage", "ReceiveMeleeDamage", "ReceiveFireDamage", "ReceivePushDamage",
        "ReceiveTentacleAttackDamage", "ReceiveTentacleTrapGrabDamage", "ReceiveParasiteTrapDamage",
        "ReceiveTentacleTankGrabDamage", "ReceiveTentacleGrabStop", "ReceiveShooterProjectileDamage", "ReceiveFallDamage",
        "ReceiveNoAirDamage", "ReceiveStickyDamage"
    };

    private static readonly string[] HealthWrites =
    {
        "Setup", "OnRevive", "ReceiveSetHealth", "ReceiveAddHealth", "ReceiveSetDead", "ReceiveModifyInfection",
        "OnApplyDamageOverTimeModifier", "OnKilledByTankTentacle"
    };

    private static void OnlyADamageReportMaySeed(Action<bool, string> check)
    {
        foreach (string name in DamageReports)
            check(HealthLossTargets.Seedable(name) && HealthLossTargets.Accepts(name),
                name + " is a hit: it is observed and it may plant the pre-call mark");
        foreach (string name in HealthWrites)
            check(!HealthLossTargets.Seedable(name) && HealthLossTargets.Accepts(name),
                name + " writes a health figure: it is still observed, and it may never seed - which is what stops its before/after pair from being credited as damage");
        check(!HealthLossTargets.Accepts(HealthLossTargets.ParasiteDamage)
              && !HealthLossTargets.Seedable(HealthLossTargets.ParasiteDamage),
            "the parasite-damage exclusion still removes that call from both lists");
        check(!HealthLossTargets.Seedable(null) && !HealthLossTargets.Seedable(string.Empty) && !HealthLossTargets.Seedable("Update"),
            "a method nobody named, and one that only ticks, are neither observed nor seeded");

        // The nesting rule as the runtime has it: `Seedable` is a subset of `Accepts`, so the prefix can never cover a
        // method the postfix does not observe.
        bool subset = true;
        foreach (string name in DamageReports) subset &= !HealthLossTargets.Seedable(name) || HealthLossTargets.Accepts(name);
        check(subset, "every seeded method is also observed, so the seed can only ever prepare a mark the postfix then uses");
    }

    // The reported fabrication, replayed: an object pool hands one damage base to the next player, so the seat's first
    // call for its new owner is the spawn write. The out-going occupant's last figure (100) is what a seed would have
    // planted, and the write's own 20 is what the postfix sees - 80 HP nobody lost.
    private static void AHealthWriteIsNeverCreditedFromAPooledSeatsMark(Action<bool, string> check)
    {
        IntPtr pooledSeat = (IntPtr)0x7777;
        ulong previous = 0xA1UL, next = 0xB2UL;
        var ledger = new HealthLossLedger();
        check(Near(Call(ledger, "ReceiveBulletDamage", pooledSeat, previous, 100.0, 99.0, 100.0), 1.0),
            "the out-going occupant's own damage is measured from their own life");

        // The seat changes hands and the new owner is initialized to 20 by the game's own setup call.
        check(Near(Call(ledger, "Setup", pooledSeat, next, 100.0, 20.0, 100.0), 0.0),
            "the new occupant's spawn write is not damage: 100 -> 20 credits nothing instead of 80 HP");
        check(Near(Call(ledger, "ReceiveSetHealth", pooledSeat, next, 20.0, 20.0, 100.0), 0.0),
            "and the replicated health write that follows it credits nothing either");
        check(Near(Call(ledger, "ReceiveBulletDamage", pooledSeat, next, 20.0, 18.74533, 100.0), 1.25467),
            "while the first real blow the new occupant takes is measured from the 20 they were really given");

        // A revive and a heal are writes too: neither can fabricate a loss, and neither re-seeds over a live mark.
        var revived = new HealthLossLedger();
        check(Near(Call(revived, "OnRevive", (IntPtr)0x8888, next, 0.0, 20.0, 100.0), 0.0),
            "a revive that brings a player back at 20 credits nothing");
        check(Near(Call(revived, "ReceiveAddHealth", (IntPtr)0x8888, next, 20.0, 80.0, 100.0), 0.0),
            "and the heal behind it is a rise, so it credits nothing and only re-bases the mark");
        check(Near(Call(revived, "ReceiveBulletDamage", (IntPtr)0x8888, next, 80.0, 75.0, 100.0), 5.0),
            "the next hit is then measured from the 80 the player really had");

        // The ordinary case is untouched: the recorded run's 20 hits of ~1.25467 HP are credited in full, first one
        // included, because the method that reports them is one of the damage reports.
        var run = new HealthLossLedger();
        double total = 0.0, first = -1.0, health = 25.0;
        for (int hit = 1; hit <= 20; hit++)
        {
            double before = health;
            health = Math.Max(0.0, before - 1.25467);
            double loss = Call(run, "ReceiveBulletDamage", Seat, Lookup, before, health, 25.0);
            if (hit == 1) first = loss;
            total += loss;
        }
        check(Near(first, 1.25467) && Near(total, 25.0),
            "the recorded run still credits all 25.0 across its 20 hits, the first blow included: first="
            + first.ToString("0.#####") + ", total=" + total.ToString("0.#####"));
    }

    // The two patch bodies in the order the runtime runs them, which is what makes the cases above the runtime's own:
    // `SeedBefore` only for a method `HealthLossTargets.Seedable` accepts, then the postfix's `Observe` for every
    // observed call. The policy is the production one - a name the seed patch class would not be attached to cannot seed
    // here either.
    private static double Call(HealthLossLedger ledger, string method, IntPtr seat, ulong owner,
        double before, double after, double maximum)
    {
        if (HealthLossTargets.Seedable(method)) ledger.Seed(seat, owner, before, maximum);
        return ledger.Observe(seat, owner, after, maximum);
    }

    // ---- 2. wiring, read from the production source ----------------------------------------------------------------------

    private static void Wiring(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the 1.19.33 blood-loss regression locates the production sources");
        if (root == null) return;

        string patches = File.ReadAllText(Path.Combine(root, "HealthLossPatches.cs"));
        string code = Code(patches);
        check(CountOf(code, "[HarmonyPrefix]") == 1,
            "exactly one prefix exists, so the damage base carries one more detour and no more");
        check(CountOf(code, "HealthLossTargets.Accepts") == 1 && CountOf(code, "HealthLossTargets.Seedable") == 1,
            "the observation list and the seed list are each named once, so there is one rule per question");
        check(code.Contains("HealthLossPatches.DamageBaseMethods(HealthLossTargets.Seedable)"),
            "the prefix is attached to the damage reports and to nothing else (1.19.35): a health write can never plant a mark");
        // 1.19.37: this assertion used to pin the target list to `typeof(Dam_PlayerDamageBase)` alone, i.e. it protected the
        // defect - a live player's seat carries `Dam_PlayerDamageLocal`, whose overrides the base's detour never sees. It now
        // asserts the coverage instead: both declaring types, through the one enumerator, for both patch classes.
        check(code.Contains("private static IEnumerable<MethodBase> TargetMethods() => DamageBaseMethods(HealthLossTargets.Accepts);")
              && code.Contains("=> HealthLossSources.DeclaredMethods(DeclaringTypes(), accept);")
              && code.Contains("HealthLossSources.DeclaringTypes(typeof(Dam_PlayerDamageBase), LocalDamageType())")
              && code.Contains("HealthLossPatches.DamageBaseMethods(HealthLossTargets.Seedable)"),
            "the postfix observes every accepted method of both declaring types - the base and the subclass a live player carries - so the watermark keeps moving on a write too, and the machine's own seat is measured at all");
        check(code.Contains("HealthLossPatches.SeedBefore(__instance)") && code.Contains("internal static void SeedBefore(Dam_PlayerDamageBase instance)"),
            "both halves reach the one ledger through one seeded entry point, so the two classes cannot drift apart");
        check(code.Contains("Ledger.Seed(instance.Pointer, lookup, instance.Health, instance.HealthMax);"),
            "the prefix hands the ledger the game's own health *and* maximum from before the call");
        check(code.Contains("if (!CombatTracker.ShouldTrack(owner)) return;"),
            "the seed is gated on the same owner rule the credit uses - host, client and bot rows all go through it");
        string prefix = Between(code, "[HarmonyPrefix]", "[HarmonyPostfix]");
        check(prefix.Length > 0 && !prefix.Contains("HealthLost"),
            "the prefix never adds to `HealthLost`: the credit stays in the postfix");
        check(prefix.Contains("Ledger.Seed(") && !prefix.Contains("InLevel"),
            "and it is not gated on the level state, so entering a level does not cost the first blow");
        int gate = code.IndexOf("GameStateManager.CurrentStateName != eGameStateName.InLevel", StringComparison.Ordinal);
        int credit = code.IndexOf("counters.HealthLost = Math.Min", StringComparison.Ordinal);
        check(gate >= 0 && credit > gate && CountOf(code, "counters.HealthLost = Math.Min") == 1,
            "the one place that credits sits behind the level gate, and there is exactly one of it");

        string ledger = File.ReadAllText(Path.Combine(root, "HealthLossLedger.cs"));
        string ledgerCode = Code(ledger);
        check(ledgerCode.Contains("internal bool Seed(IntPtr target, ulong owner, double health, double maximum)"),
            "the ledger owns the seeding rule, Unity-free, so every case above is the runtime's own");
        check(ledgerCode.Contains("if (_mark.ContainsKey(key)) return false;"),
            "it refuses to move an existing mark");
        check(ledgerCode.Contains("if (!double.IsFinite(health) || !double.IsFinite(maximum) || maximum <= 0) return false;"),
            "and refuses a reading that is not a number");

        string targets = Code(File.ReadAllText(Path.Combine(root, "HealthLossLedger.cs")));
        check(targets.Contains("if (name == ParasiteDamage) return false;"),
            "the parasite-damage exclusion is untouched, so the crash mitigation still holds for both halves");
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------------

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 1e-9;

    private static int CountOf(string text, string token)
        => text == null || token == null ? 0 : text.Split(new[] { token }, StringSplitOptions.None).Length - 1;

    private static string Between(string text, string from, string to)
    {
        int start = text.IndexOf(from, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        int end = text.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        return end < 0 ? text.Substring(start) : text.Substring(start, end - start);
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
