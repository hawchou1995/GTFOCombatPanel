using System;
using HawChow.GTFOCombatPanel;

// 1.19.65: what a client's own blows are worth when the host applies them and this machine has no authority over
// the enemy - and what may be taken back again when the enemy's own reading proves an estimate wrong.
//
// The rules live in `EnemyHealthBudget` (beside `LocalOutgoingRule`, which still decides one blow's own figure) and
// every assertion below drives that production type through the same three places `CombatTracker` credits: the
// player's total, the weapon row (`RecordDamage`) and the Scout row (`Impact.AddScoutDamage`). Removing the shared
// balance, the withdrawal, or one of the three places it acts on turns these red.
//
// What is asserted:
//   * M1 the defect this round fixes: an enemy met at 20 whose stale 20 was estimated in full, then really read
//     20 -> 13, takes the unsupported 20 back, credits the 7 that happened and leaves 13 - total, weapon row and
//     Scout row alike. A legitimate earlier 7 with the enemy really reading 18 first is not deducted twice; a
//     credit this machine measured across a call is confirmed and is never taken back.
//   * the rules that are unchanged: 25 of 30 credits 25 and not the maximum, five pellets carrying 6.8 (34 in
//     theory) credit the 20 the enemy had, a lower reading aligns the balance without crediting anything, a
//     lagging or higher snapshot neither raises the balance nor takes a credit away, death / unreadable numbers /
//     another enemy / a reused pointer / a spawn edge each keep to the enemy's own lifecycle.
//   * M3 EWC is driven by the residual the production pairing computes (`DamageObservation.Uncredited`), in mixed
//     sequences with the native receive credit: no event is booked twice, a nested chain event is measured on its
//     own delta, and a correction never moves another player's damage onto this machine.
//   * M2 the pending-spawn ledger answers a spawn exactly once (`PendingSpawns.Take`), which is the gate the two
//     client-side damage entrances use before they touch anything keyed by a damage or owner pointer.
//
// What is NOT asserted here, and why: the il2cpp entry points themselves (`NoteLocalOutgoing`'s limb, the EWC
// bridge's pairing) need the game assemblies, so they are covered by the static wiring gates in `Verify.ps1`; and
// on a client with no host authority an unattributed health change and another player's damage are the same
// observation, which is exactly why such a reading only aligns the balance and never credits - or takes back -
// anything. The recorded Survival sample for this round is `healthBefore=20.0 / healthAfter=20.0 / dam=4.3`, a
// bounded sample; no assertion below claims a single blow of 34 was ever seen live.
internal static class Release11965Scenarios
{
    private static readonly IntPtr Enemy = new IntPtr(0x2000);
    private static readonly IntPtr Other = new IntPtr(0x3000);
    private static readonly IntPtr Owner = new IntPtr(0x4000);
    private static readonly IntPtr NextOwner = new IntPtr(0x5000);

    internal static void RunInto(Action<bool, string> check)
    {
        AKillCreditsTheHealthThatWasLeftNotTheMaximum(check);
        AReadableLossBeatsTheCarriedFigure(check);
        AShotgunStaysInsideTheOneBalance(check);
        ASyncDownIsNotDeductedTwice(check);
        AnEstimateTheEnemyNeverLostIsTakenBack(check);
        ACorrectionTakesBackEveryPlaceTheEstimateWent(check);
        AConfirmedCreditIsNeverWithdrawn(check);
        ALaggingHigherSnapshotRaisesAndTakesNothing(check);
        AnUnattributedDropCreditsAndTakesNothing(check);
        ADeadEnemyEarnsNothing(check);
        UnreadableNumbersCreditNothing(check);
        EachEnemyHasItsOwnBalance(check);
        AReusedPointerStartsFresh(check);
        TheSpawnEdgeClearsThatPointerOnly(check);
        TheEwcResidualIsSpentFromTheSameBalance(check);
        ThePendingSpawnIsTakenOnce(check);
        TheExpeditionClearsEverything(check);
        TheOldPerBlowRuleIsTheOneThatOverCredits(check);
    }

    // ---- helpers: the same three places `CombatTracker` credits ------------------------------------------------
    private static CombatPlayer NewPlayer(ulong id, out WeaponCombatStats weapon)
    {
        var player = new CombatPlayer(id);
        weapon = player.Weapon(1, 100, "Shotgun");
        return player;
    }

    // One blow through the production composition: the per-blow rule decides what it is worth, the instance's
    // balance decides how much of that can still be true, and what the balance took back is reported.
    private static double Blow(EnemyHealthBudget budget, CombatPlayer player, WeaponCombatStats weapon, bool scout,
        float before, float after, float dam, out double withdrawn)
        => Credit(budget, player, weapon, scout, before, after, LocalOutgoingRule.Amount(before, after, dam), out withdrawn);

    private static double Credit(EnemyHealthBudget budget, CombatPlayer player, WeaponCombatStats weapon, bool scout,
        float before, float after, double wanted, out double withdrawn)
    {
        double amount = budget.Charge(Enemy, Owner, player, weapon, scout, before, after, wanted,
            LocalOutgoingRule.Measured(before, after), out withdrawn);
        if (amount > 0)
        {
            player.RecordDamage(weapon, amount, false, true);
            if (scout) player.Impact.AddScoutDamage(amount, true);
        }
        return amount;
    }

    // ---- 25 of 30, killed by 30 -------------------------------------------------------------------------------
    private static void AKillCreditsTheHealthThatWasLeftNotTheMaximum(Action<bool, string> check)
    {
        var stale = NewPlayer(1, out WeaponCombatStats staleGun);
        var staleBudget = new EnemyHealthBudget();
        check(near(Blow(staleBudget, stale, staleGun, false, 25f, 25f, 30f, out _), 25),
            "an enemy met with 25 left credits the 25 it had, not the 30 its maximum health would allow");
        check(stale.Total.EnemyDamage == 25 && staleBudget.Remaining(Enemy) == 0,
            "and the instance is spent: what it had is all it could give");

        var measured = NewPlayer(2, out WeaponCombatStats measuredGun);
        var measuredBudget = new EnemyHealthBudget();
        check(near(Blow(measuredBudget, measured, measuredGun, false, 25f, 0f, 30f, out _), 25),
            "the same blow with a readable loss (25 -> 0) credits that loss, again 25 and never 30");
        check(measuredBudget.Remaining(Enemy) == 0, "a dead enemy holds no balance");
    }

    // ---- the same event's real loss ----------------------------------------------------------------------------
    private static void AReadableLossBeatsTheCarriedFigure(Action<bool, string> check)
    {
        var player = NewPlayer(3, out WeaponCombatStats gun);
        var budget = new EnemyHealthBudget();
        check(near(Blow(budget, player, gun, false, 30f, 23f, 30f, out _), 7),
            "a blow whose call moved 7 health is worth 7, not the 30 the weapon carried in");
        check(near(Blow(budget, player, gun, false, 23f, 23f, 30f, out _), 23),
            "the next blow on that instance may still credit the 23 the balance holds");
        check(player.Total.EnemyDamage == 30 && budget.Remaining(Enemy) == 0,
            "the two blows together are the 30 the enemy had, and nothing more");
    }

    // ---- five pellets, 34 in theory, 20 in fact ----------------------------------------------------------------
    private static void AShotgunStaysInsideTheOneBalance(Action<bool, string> check)
    {
        var player = NewPlayer(4, out WeaponCombatStats gun);
        var budget = new EnemyHealthBudget();
        for (int pellet = 0; pellet < 5; pellet++) Blow(budget, player, gun, false, 20f, 20f, 6.8f, out _);
        check(near(player.Total.EnemyDamage, 20, 1e-4),
            "five pellets carrying 6.8 each (34) credit the 20 the enemy had left: " + player.Total.EnemyDamage);
        check(player.Total.EnemyDamage <= 20 + 1e-4, "no pellet may push the instance past its own remaining health");
        check(near(player.Weapon(1, 100, "Shotgun").Counters.EnemyDamage, 20, 1e-4),
            "the weapon row carries the same figure as the player's total");
        check(Blow(budget, player, gun, false, 20f, 20f, 6.8f, out _) == 0,
            "a sixth pellet on the same stale snapshot credits nothing, because the balance is spent");

        // Two limbs of one enemy spend the same balance.
        var limbs = NewPlayer(5, out WeaponCombatStats limbGun);
        var limbBudget = new EnemyHealthBudget();
        double head = Blow(limbBudget, limbs, limbGun, false, 20f, 20f, 12f, out _);
        double body = Blow(limbBudget, limbs, limbGun, false, 20f, 20f, 12f, out _);
        check(near(head, 12) && near(body, 8), "the second limb's blow spends what the first one left: " + head + "+" + body);
    }

    // ---- the sync that already reflects the previous credit -----------------------------------------------------
    private static void ASyncDownIsNotDeductedTwice(Action<bool, string> check)
    {
        var player = NewPlayer(6, out WeaponCombatStats gun);
        var budget = new EnemyHealthBudget();
        check(near(Blow(budget, player, gun, false, 25f, 25f, 7f, out _), 7), "the first blow credits its 7 from the 25 the enemy had");
        // The sync has now made the enemy read 18, which already accounts for that 7, and this blow really moves it.
        double taken = Blow(budget, player, gun, false, 18f, 13f, 20f, out double withdrawn);
        check(withdrawn == 0, "a reading that already reflects the earlier 7 takes nothing back: " + withdrawn);
        check(near(taken, 5), "the blow is worth the 5 it moved, not the 20 the weapon carried in: " + taken);
        check(near(player.Total.EnemyDamage, 12) && near(budget.Remaining(Enemy), 13),
            "7 + 5 credited, 13 left - the earlier 7 is never deducted from the 18 a second time");
    }

    // ---- M1: the estimate the enemy never lost comes back -------------------------------------------------------
    private static void AnEstimateTheEnemyNeverLostIsTakenBack(Action<bool, string> check)
    {
        var player = NewPlayer(7, out WeaponCombatStats gun);
        var budget = new EnemyHealthBudget();
        // Old snapshot 20, the call moved nothing, so the weapon's 20 was estimated and it spent the whole balance.
        check(near(Blow(budget, player, gun, true, 20f, 20f, 20f, out _), 20),
            "the stale 20 is estimated from the carried figure and spends the enemy's whole 20");
        check(player.Total.EnemyDamage == 20 && budget.Remaining(Enemy) == 0,
            "the row reads 20 and the instance holds nothing");
        // The enemy really moves this time: 20 -> 13. The reading proves the 20 never left it.
        double taken = Blow(budget, player, gun, true, 20f, 13f, 20f, out double withdrawn);
        check(near(withdrawn, 20), "the unsupported 20 is taken back off the board: " + withdrawn);
        check(near(taken, 7), "this blow is worth the 7 the enemy really lost: " + taken);
        check(near(player.Total.EnemyDamage, 7), "the player's total is the 7 now, not 20 + 7: " + player.Total.EnemyDamage);
        check(near(player.Weapon(1, 100, "Shotgun").Counters.EnemyDamage, 7),
            "the weapon row took the same 20 back: " + player.Weapon(1, 100, "Shotgun").Counters.EnemyDamage);
        check(near(player.Impact.ScoutDamage, 7), "and so did the Scout row: " + player.Impact.ScoutDamage);
        check(near(budget.Remaining(Enemy), 13), "the enemy's remaining 13 is available to the rest of the fight");
        check(near(Blow(budget, player, gun, false, 13f, 13f, 99f, out _), 13),
            "and the next blow may spend exactly that 13, never the 20 that was taken back and never more");
    }

    // ---- M1: every place the estimate went, and several estimates at once ---------------------------------------
    private static void ACorrectionTakesBackEveryPlaceTheEstimateWent(Action<bool, string> check)
    {
        var first = NewPlayer(8, out WeaponCombatStats firstGun);
        var second = NewPlayer(9, out WeaponCombatStats secondGun);
        var budget = new EnemyHealthBudget();
        Blow(budget, first, firstGun, false, 20f, 20f, 10f, out _);
        budget.Charge(Enemy, Owner, second, secondGun, true, 20f, 20f, 10, false, out _);
        second.RecordDamage(secondGun, 10, false, true); second.Impact.AddScoutDamage(10, true);
        check(near(first.Total.EnemyDamage, 10) && near(second.Total.EnemyDamage, 10) && near(second.Impact.ScoutDamage, 10),
            "two estimates on one enemy, each one where its owner put it");
        // The enemy then really reads 20 -> 18: neither 10 is supported, and both are withdrawn.
        double taken = Blow(budget, first, firstGun, false, 20f, 18f, 2f, out double withdrawn);
        check(near(withdrawn, 20), "both estimates are taken back, newest first: " + withdrawn);
        check(near(first.Total.EnemyDamage, 2), "the older estimate left this player's total at what the reading supports");
        check(second.Total.EnemyDamage == 0 && second.Impact.ScoutDamage == 0,
            "the newer estimate - and its Scout row, in another player's row - are gone: " + second.Total.EnemyDamage);
        check(near(taken, 2) && near(budget.Remaining(Enemy), 18), "and the 2 that really happened is what stands");
    }

    // ---- a measurement is not an estimate ----------------------------------------------------------------------
    private static void AConfirmedCreditIsNeverWithdrawn(Action<bool, string> check)
    {
        var player = NewPlayer(10, out WeaponCombatStats gun);
        var budget = new EnemyHealthBudget();
        check(near(Blow(budget, player, gun, false, 30f, 20f, 10f, out _), 10), "a credit measured across the call is confirmed");
        check(near(budget.Remaining(Enemy), 20), "and it spent 10 of the enemy's 30");
        // A later reading that is higher than the balance predicts can only be a lagging snapshot. It is still an
        // attributable event (this blow really moved 30 -> 25), so it would be allowed to correct an estimate - but
        // the 10 on the board is a measurement, not an estimate, so nothing is taken back.
        double taken = Blow(budget, player, gun, false, 30f, 25f, 99f, out double withdrawn);
        check(withdrawn == 0, "a confirmed credit is never taken back, whatever a later reading says: " + withdrawn);
        check(near(taken, 5), "the blow is worth the 5 it moved, not the 99 the weapon carried in: " + taken);
        check(near(player.Total.EnemyDamage, 15) && near(budget.Remaining(Enemy), 15),
            "10 + 5 credited, 15 left: " + player.Total.EnemyDamage + " / " + budget.Remaining(Enemy));
    }

    // ---- a higher snapshot is neither a refund nor a correction -------------------------------------------------
    private static void ALaggingHigherSnapshotRaisesAndTakesNothing(Action<bool, string> check)
    {
        var player = NewPlayer(11, out WeaponCombatStats gun);
        var budget = new EnemyHealthBudget();
        Blow(budget, player, gun, true, 25f, 25f, 7f, out _);
        check(near(budget.Observe(Enemy, Owner, 25f), 18), "a lagging 25 does not refill a balance that holds 18");
        check(near(budget.Observe(Enemy, Owner, 30f), 18), "nor does a higher one, and it takes nothing back either");
        check(near(player.Total.EnemyDamage, 7) && near(player.Impact.ScoutDamage, 7),
            "the board is untouched by either reading: " + player.Total.EnemyDamage);
        check(near(Blow(budget, player, gun, false, 25f, 25f, 20f, out _), 18),
            "the next blow on that stale snapshot is still capped at the 18 that is really left");
    }

    // ---- a drop this machine did not cause ---------------------------------------------------------------------
    private static void AnUnattributedDropCreditsAndTakesNothing(Action<bool, string> check)
    {
        var player = NewPlayer(12, out WeaponCombatStats gun);
        var budget = new EnemyHealthBudget();
        Blow(budget, player, gun, true, 25f, 25f, 7f, out _);
        check(near(budget.Observe(Enemy, Owner, 12f), 12),
            "a drop to 12 this machine did not credit only aligns the balance down: " + budget.Remaining(Enemy));
        check(near(player.Total.EnemyDamage, 7) && near(player.Impact.ScoutDamage, 7),
            "no part of that drop becomes this player's damage: " + player.Total.EnemyDamage);
        check(near(Blow(budget, player, gun, false, 12f, 12f, 20f, out _), 12),
            "the next blow is capped at the 12 that remains - the other 6 is not this machine's to book");
        check(near(budget.Observe(Enemy, Owner, 0f), 0) && budget.Remaining(Enemy) == 0, "a reading of zero closes the instance");
    }

    // ---- death, one enemy, pointer reuse, spawn ----------------------------------------------------------------
    private static void ADeadEnemyEarnsNothing(Action<bool, string> check)
    {
        var player = NewPlayer(13, out WeaponCombatStats gun);
        var budget = new EnemyHealthBudget();
        check(near(Blow(budget, player, gun, false, 5f, 0f, 5f, out _), 5), "the killing blow credits the health that was there");
        check(Blow(budget, player, gun, false, 5f, 5f, 30f, out _) == 0, "a blow on a corpse credits nothing");
        check(budget.Observe(Enemy, Owner, 20f) == 0,
            "a later higher reading of the same instance does not reopen a closed balance on its own");
    }

    private static void UnreadableNumbersCreditNothing(Action<bool, string> check)
    {
        var player = NewPlayer(14, out WeaponCombatStats gun);
        var budget = new EnemyHealthBudget();
        check(Blow(budget, player, gun, false, float.NaN, 10f, 30f, out _) == 0, "an unreadable health before the call credits nothing");
        check(budget.Remaining(Enemy) == 0, "and it does not open a balance out of a number nobody read");
        check(Blow(budget, player, gun, false, float.PositiveInfinity, float.PositiveInfinity, 30f, out _) == 0,
            "an infinite health reading credits nothing either");
        check(Blow(budget, player, gun, false, 20f, float.NaN, 6f, out _) == 0,
            "an unreadable health after the call credits nothing: the per-blow rule answers no figure at all");
        check(near(budget.Remaining(Enemy), 20), "and the 20 the enemy was read at is still there");
        check(Blow(budget, player, gun, false, 20f, 20f, float.NaN, out _) == 0 && Blow(budget, player, gun, false, 20f, 20f, -4f, out _) == 0,
            "an unreadable or negative carried figure credits nothing");
        check(!LocalOutgoingRule.Measured(float.NaN, 1f) && !LocalOutgoingRule.Measured(20f, 20f) && LocalOutgoingRule.Measured(20f, 13f),
            "only a reading that really moved is attributable");
    }

    private static void EachEnemyHasItsOwnBalance(Action<bool, string> check)
    {
        var player = NewPlayer(15, out WeaponCombatStats gun);
        var budget = new EnemyHealthBudget();
        check(near(Blow(budget, player, gun, false, 20f, 20f, 20f, out _), 20), "the first enemy spends its own 20");
        check(near(budget.Charge(Other, Owner, player, gun, false, 20f, 20f, 20, false, out _), 20), "a different enemy still has its own 20");
        check(budget.Remaining(Enemy) == 0 && budget.Remaining(Other) == 0, "both balances are spent and neither borrowed from the other");
    }

    private static void AReusedPointerStartsFresh(Action<bool, string> check)
    {
        var player = NewPlayer(16, out WeaponCombatStats gun);
        var budget = new EnemyHealthBudget();
        Blow(budget, player, gun, false, 20f, 20f, 20f, out _);
        check(budget.Remaining(Enemy) == 0, "the pointer is spent under its first occupant");
        check(near(budget.Observe(Enemy, NextOwner, 30f), 30),
            "a different enemy agent behind the same pointer opens a fresh balance: " + budget.Remaining(Enemy));
        check(near(Blow(budget, player, gun, false, 30f, 30f, 30f, out _), 30), "and that new occupant's blows draw on its own 30");
        check(near(player.Total.EnemyDamage, 50), "the credit already on the board is not taken back by the new occupant");
    }

    private static void TheSpawnEdgeClearsThatPointerOnly(Action<bool, string> check)
    {
        var player = NewPlayer(17, out WeaponCombatStats gun);
        var budget = new EnemyHealthBudget();
        Blow(budget, player, gun, false, 20f, 20f, 20f, out _);
        budget.Charge(Other, Owner, player, gun, false, 15f, 15f, 5, false, out _);
        budget.Spawn(Enemy);
        check(budget.Remaining(Enemy) == 0 && near(budget.Observe(Enemy, Owner, 20f), 20),
            "the enemy's own spawn edge gives this pointer a fresh balance");
        check(near(budget.Remaining(Other), 10), "another enemy's balance is untouched by that spawn");
    }

    // ---- M3: EWC's paired stream, on the residual the pairing really computes ----------------------------------
    private static void TheEwcResidualIsSpentFromTheSameBalance(Action<bool, string> check)
    {
        var player = NewPlayer(18, out WeaponCombatStats gun);
        var budget = new EnemyHealthBudget();
        // One explosion on a 30-health enemy: the native receive path already booked 12 of the 20 it lost, so the
        // residual the EWC pairing hands over is the 8 nobody has booked yet.
        double residual = DamageObservation.Uncredited(30f, 10f, 0, 12);
        check(near(residual, 8), "the pairing's residual for that event is 8: " + residual);
        check(DamageObservation.Uncredited(30f, 10f, 0, 20) == 0,
            "when the native path booked the whole loss the residual is 0, so the same event is never booked twice");
        double taken = Credit(budget, player, gun, false, 30f, 10f, residual, out double withdrawn);
        check(near(taken, 8) && withdrawn == 0, "the residual is credited once and takes no estimate back: " + taken + " / " + withdrawn);
        check(near(budget.Remaining(Enemy), 10), "the 10 the enemy really has left is what the balance holds: " + budget.Remaining(Enemy));

        // A chain event nested inside the same frame reads its own before/after, so the outer event's loss does not
        // enter its delta: 10 -> 4 is 6, whatever the native credit already holds.
        double nested = DamageObservation.Uncredited(10f, 4f, 20, 20);
        check(near(nested, 6), "the nested event's own delta is 6, not the outer event's 20: " + nested);
        check(near(Credit(budget, player, gun, false, 10f, 4f, nested, out _), 6), "and it is credited once");
        check(near(player.Total.EnemyDamage, 14) && near(budget.Remaining(Enemy), 4),
            "8 + 6 credited on this enemy, 4 left: " + player.Total.EnemyDamage + " / " + budget.Remaining(Enemy));

        // The same balance also takes an estimate back for the EWC path: a stale 4 estimated first, then the enemy
        // really reads 4 -> 0 - the estimate is withdrawn and only the measured 4 stands.
        var stalePlayer = NewPlayer(19, out WeaponCombatStats staleGun);
        var staleBudget = new EnemyHealthBudget();
        staleBudget.Charge(Enemy, Owner, stalePlayer, staleGun, false, 4f, 4f, 4, false, out _);
        stalePlayer.RecordDamage(staleGun, 4, false, true);
        double loss = DamageObservation.Uncredited(4f, 0f, 0, 0);
        check(near(loss, 4), "the killing event's own loss is 4: " + loss);
        check(near(Credit(staleBudget, stalePlayer, staleGun, false, 4f, 0f, loss, out double staleWithdrawn), 4),
            "the measured 4 is credited");
        check(near(staleWithdrawn, 4), "and the stale 4 that the reading did not support is taken back first: " + staleWithdrawn);
        check(near(stalePlayer.Total.EnemyDamage, 4), "the row holds the real 4 once, not 8: " + stalePlayer.Total.EnemyDamage);
    }

    // ---- M2: the pending-spawn gate ----------------------------------------------------------------------------
    private static void ThePendingSpawnIsTakenOnce(Action<bool, string> check)
    {
        var pending = new PendingSpawns();
        IntPtr agent = new IntPtr(0x6000), other = new IntPtr(0x7000);
        pending.Add(agent); pending.Add(agent); pending.Add(IntPtr.Zero);
        check(pending.Count == 1, "one spawn per enemy is pending, and a zero pointer never is: " + pending.Count);
        check(pending.Take(agent), "the first caller that can see the enemy's damage base consumes its spawn");
        check(!pending.Take(agent), "a second caller for the same spawn finds nothing, so the reset is idempotent");
        check(pending.Count == 0 && !pending.Take(other) && !pending.Take(IntPtr.Zero),
            "another enemy's pointer and a zero pointer consume nothing");
        pending.Add(agent); pending.Add(other);
        check(pending.Count == 2 && pending.Take(other) && pending.Count == 1, "each spawn is still its own entry");
        pending.Clear();
        check(pending.Count == 0 && !pending.Take(agent), "the expedition reset drops every pending spawn");
    }

    private static void TheExpeditionClearsEverything(Action<bool, string> check)
    {
        var player = NewPlayer(20, out WeaponCombatStats gun);
        var budget = new EnemyHealthBudget();
        Blow(budget, player, gun, false, 20f, 20f, 20f, out _);
        budget.Clear();
        check(budget.Remaining(Enemy) == 0 && budget.Remaining(Other) == 0, "a new expedition starts from no balances");
        check(near(Blow(budget, player, gun, false, 20f, 20f, 20f, out _), 20), "and the next enemy met at 20 has its 20 again");
    }

    // ---- the shape this round removed, by its own numbers ------------------------------------------------------
    private static void TheOldPerBlowRuleIsTheOneThatOverCredits(Action<bool, string> check)
    {
        // The pre-1.19.65 composition: every blow on its own, with no cross-blow budget and no correction.
        double withoutBalance = 0;
        for (int pellet = 0; pellet < 5; pellet++) withoutBalance += LocalOutgoingRule.Amount(20f, 20f, 6.8f);
        check(near(withoutBalance, 34, 1e-4), "the per-blow rule alone credits 34 for a 20-health enemy: " + withoutBalance);
        check(withoutBalance > 20, "which is exactly the overflow the shared balance removes");

        var player = NewPlayer(21, out WeaponCombatStats gun);
        var budget = new EnemyHealthBudget();
        double withBalance = 0;
        for (int pellet = 0; pellet < 5; pellet++) withBalance += Blow(budget, player, gun, false, 20f, 20f, 6.8f, out _);
        check(withBalance < withoutBalance && near(withBalance, 20, 1e-4), "with the instance's balance the same pellets credit 20: " + withBalance);

        // And the shape M1 removes: the estimate alone, with the real loss never able to replace it.
        check(near(LocalOutgoingRule.Amount(20f, 20f, 20f) + LocalOutgoingRule.Amount(20f, 13f, 20f), 27),
            "without the correction the stale 20 and the real 7 would both stand, and the remaining 13 could never be credited");
    }

    private static bool near(double a, double b) => near(a, b, 1e-6);
    private static bool near(double a, double b, double tolerance) => Math.Abs(a - b) <= tolerance;
}
