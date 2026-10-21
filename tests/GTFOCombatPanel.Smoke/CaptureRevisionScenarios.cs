using System;
using HawChow.GTFOCombatPanel;

internal static partial class Program
{
    private static void CaptureRevisionScenarios()
    {
        var tools = new ToolImpactLedger();
        var identity = new IntPtr(101);
        tools.TryDeployMine(identity, 1, false); tools.Clear();
        Check(tools.DeployedMineCount == 0, "reset clears counted mine identities as well as owners");
        tools.TryDeployMine(identity, 1, true);
        Check(!tools.TryRecoverMine(identity, out _) && !tools.TryGetMineOwner(identity, out _), "consumable recovery clears attribution without subtracting equipment deployments");
        Check(MineBlastAttribution.Resolve(true, 11, 22).OwnerLookup == 11, "recorded placer wins conflicting core owner");
        tools.BeginSentryWindow(identity);
        Check(tools.TrySentryHit(identity, 0) && !tools.TrySentryHit(identity, 0), "zero search id deduplicates contacts only within a firing window");
        tools.BeginSentryWindow(identity);
        Check(tools.TrySentryHit(identity, 0), "next firing window can hit again with the same zero id");
        tools.BeginGlueProjectile(identity);
        Check(tools.TryGlueHit(1, identity) && !tools.TryGlueHit(1, identity), "one glue launch credits one contact per owner/projectile");
        tools.BeginGlueProjectile(identity);
        Check(tools.TryGlueHit(1, identity), "pooled glue projectile reuse begins a new contact lifetime");
        // Sentry rounds are reported by two independent streams: the master FireBullet call (the only one
        // that knows doDamage) and the turret's native notification (which knows nothing). Each call owns a
        // scope token, and a notification raised inside that scope describes the same round.
        var sentry = new SentryShotLedger();
        IntPtr turret = new IntPtr(202);
        long total = 0;
        total += sentry.BeginCall(turret, true, out var both);
        total += sentry.ObserveEvent(turret);
        sentry.EndCall(both);
        Check(total == 1, "one round reported by both streams counts once");
        total += sentry.BeginCall(turret, true, out var paired);
        total += sentry.ObserveEvent(turret);
        sentry.EndCall(paired);
        total += sentry.ObserveEvent(turret);
        total += sentry.ObserveEvent(turret);
        total += sentry.BeginCall(turret, true, out var loneCall);
        sentry.EndCall(loneCall);
        Check(total == 5, "interleaved calls and notifications count every round once");
        for (int n = 0; n < 3; n++)
        {
            total += sentry.BeginCall(turret, true, out var modeCall);
            sentry.EndCall(modeCall);
        }
        Check(total == 8, "a call-only firing mode counts every round");
        for (int n = 0; n < 3; n++) total += sentry.ObserveEvent(turret);
        Check(total == 11, "notifications after the call stream stops are not swallowed by stale counts");
        total += sentry.BeginCall(turret, false, out var blank);
        total += sentry.ObserveEvent(turret);
        total += sentry.ObserveEvent(turret);
        sentry.EndCall(blank);
        Check(total == 11, "a round fired without damage and its notifications are not shots");
        total += sentry.BeginCall(turret, true, out var real);
        total += sentry.ObserveEvent(turret);
        sentry.EndCall(real);
        Check(total == 12, "the first damaging round after a false mode still counts once");
        total += sentry.BeginCall(turret, true, out var burst);
        total += sentry.ObserveEvent(turret);
        total += sentry.ObserveEvent(turret);
        total += sentry.ObserveEvent(turret);
        sentry.EndCall(burst);
        Check(total == 15, "a burst call adds the notifications beyond its own single round");
        Check(sentry.BeginCall(IntPtr.Zero, true, out var none) == 0 && !none.IsOpen
            && sentry.ObserveEvent(IntPtr.Zero) == 0, "an invalid turret never opens a scope or counts an event");
        sentry.EndCall(none);
        sentry.EndCall(default);
        sentry.EndCall(both);
        Check(total == 15, "closing an already closed or unknown scope changes nothing");
        var forgotten = new SentryShotLedger();
        forgotten.BeginCall(turret, false, out var stale);
        forgotten.Forget(turret);
        Check(forgotten.ObserveEvent(turret) == 1, "torn down turret state can no longer swallow notifications");
        forgotten.Clear();
        Check(forgotten.BeginCall(turret, true, out var fresh) == 1 && forgotten.ObserveEvent(turret) == 0,
            "a new expedition starts with no stale sentry scope");
        forgotten.EndCall(fresh);
        Check(forgotten.ObserveEvent(turret) == 1, "a new expedition still counts a notification the call hook cannot see");
        var scanner = new ScanSession(); var scanCounts = new ImpactCounters();
        scanner.ObserveTagging(true, scanCounts);
        Check(scanner.ObserveConfirmed(12, scanCounts, false) == ScanMark.Rejected && scanCounts.ScanFirstMarks == 0, "candidate or interrupted tag does not consume first reveal");
        scanner.ObserveTagging(false, scanCounts); scanner.ObserveTagging(true, scanCounts);
        Check(scanner.ObserveConfirmed(12, scanCounts, true) == ScanMark.First && scanCounts.ScanFirstMarks == 1, "later confirmed tag still earns the first reveal");
        Check(scanner.ObserveConfirmed(12, scanCounts, true) == ScanMark.Repeat && scanCounts.ScanMarks == 1, "duplicate completion cannot inflate one sweep");
        scanner.BeginBotTag(10, 1, scanCounts); scanner.EndBotTag(); scanner.BeginBotTag(12, 1, scanCounts);
        scanner.ObserveConfirmed(12, scanCounts, true, true);
        Check(scanCounts.ScanMarks == 2 && scanCounts.ScanFirstMarks == 1, "new bot sweep counts cumulative repeat without re-awarding first mark");
        var ledger = new EnemyDeathLedger();
        IntPtr enemy = new IntPtr(77);
        var scope = ledger.Begin(enemy, 11, DamageKind.Melee, true, 20);
        Check(ledger.IsReceiving(enemy) && !ledger.TryClaimUnscoped(enemy), "death inside receive is deferred, not credited with an unknown cause");
        Check(ledger.Complete(scope, 0, true) && scope.Cause == DamageKind.Melee && scope.Sleeping == true, "receive completion retains melee and pre-hit sleeping state");
        Check(!ledger.Complete(scope, 0, true) && !ledger.TryClaimUnscoped(enemy), "postfix/finalizer and two death callbacks credit once");
        ledger.Spawn(enemy);
        var recycled = ledger.Begin(enemy, 12, DamageKind.Bullet, false, 10);
        Check(ledger.Complete(recycled, 0, true), "spawn re-arms a recycled native pointer");
        ledger.Spawn(enemy);
        var nonlethal = ledger.Begin(enemy, 11, DamageKind.Melee, true, 20);
        Check(!ledger.Complete(nonlethal, 5, true), "nonlethal melee does not manufacture a kill");
        Check(ledger.TryClaimUnscoped(enemy), "later unscoped death can be counted only with unknown cause");
        ledger.Spawn(enemy);
        var failed = ledger.Begin(enemy, 11, DamageKind.Melee, null, 20);
        Check(!ledger.Complete(failed, 0, false) && !ledger.IsReceiving(enemy), "exception closes receive scope without false melee credit");
        ledger.Clear();
        Check(ledger.Begin(IntPtr.Zero, 1, DamageKind.Melee, true, 20) == null, "invalid enemy identity never opens a death scope");
        Check(ledger.Begin(enemy, 1, DamageKind.Melee, true, float.NaN) == null, "invalid health never opens a death scope");
        var outer = ledger.Begin(enemy, 1, DamageKind.Bullet, true, 20);
        var inner = ledger.Begin(enemy, 2, DamageKind.Explosion, false, 15);
        Check(ledger.Complete(inner, 0, true) && !ledger.Complete(outer, 0, true), "nested lethal damage credits inner cause once");

        var loss = new HealthLossLedger();
        var damage = loss.Begin(enemy, 100, 100);
        var nested = loss.Begin(enemy, 100, 100);
        Check(loss.Complete(nested, 75, 100, true) == 0 && loss.Complete(damage, 75, 100, true) == 25, "nested damage methods count actual health loss once");
        Check(loss.Complete(damage, 50, 100, true) == 0, "duplicate completion cannot add health loss");
        Check(loss.Complete(loss.Begin(enemy, 50, 100), 60, 100, true) == 0, "healing never subtracts or adds cumulative health loss");
        Check(loss.Complete(loss.Begin(enemy, 100, 100), 80, 80, true) == 0, "maximum health reduction is not blood loss");
        Check(loss.Complete(loss.Begin(enemy, 10, 100), -20, 100, true) == 10, "overkill counts only actual available life");
        Check(loss.Complete(loss.Begin(enemy, 50, 100), 20, 100, false) == 0, "failed callbacks do not invent health loss");
        Check(loss.Complete(loss.Begin(enemy, 50, 100), double.NaN, 100, true) == 0, "unknown health is not a measurement");

        var giver = new ImpactCounters(); var receiver = new ImpactCounters();
        var use = new SupplyUse(); use.AddChange(20, 40, 20); use.AddChange(10, 30, 20);
        Check(use.Record(giver) && use.RecordConsumption(receiver, true), "efficiency belongs to giver while consumption belongs to receiver");
        Check(giver.SelfSupplyUses == 0 && receiver.SelfSupplyUses == 1 && receiver.SelfAmmoUses == 1 && receiver.SupplyUses == 0, "giving to somebody else never credits giver self consumption");
        Check(!use.RecordConsumption(receiver, true) && receiver.SelfSupplyUses == 1, "nested ammo channels count one consumed use");
        var medical = new SupplyUse(); medical.AddChange(50, 70, 20); medical.RecordConsumption(receiver, false);
        Check(receiver.SelfSupplyUses == 2 && receiver.SelfAmmoUses == 1, "medical use increments total but not ammo uses");
        var full = new SupplyUse(); full.AddChange(100, 100, 20);
        Check(!full.RecordConsumption(receiver, true), "no received resource gain does not claim a consumed benefit");
        var invalid = new SupplyUse(); invalid.AddChange(double.NaN, 0, 20);
        Check(!invalid.RecordConsumption(receiver, true), "unobserved supply does not manufacture consumption");
    }
}
