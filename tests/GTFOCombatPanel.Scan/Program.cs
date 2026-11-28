using System;
using System.Collections.Generic;
using System.Reflection;
using HawChow.GTFOCombatPanel;

internal static class Program
{
    private static int count;
    private static object Call(Type type, string name, params object[] args)
        => type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args);
    private static void Check(bool ok, string name)
    { if (!ok) throw new Exception("FAIL: " + name); count++; }
    private static void Execute(Enemies.EnemyAgent enemy, Exception error = null)
    {
        object[] args = { null };
        Call(typeof(ScanExecutedPatch), "Prefix", args);
        var state = args[0];
        var result = Call(typeof(ScanExecutedPatch), "Finalizer", new ToolSyncManager.pTagEnemy { enemy = new Agents.pEnemyAgent { Target = enemy } }, state, error);
        Check(ReferenceEquals(result, error), "finalizer preserves native exception");
    }
    private static void Request(Enemies.EnemyAgent enemy, Exception error = null)
    {
        object[] args = { enemy, IntPtr.Zero };
        Call(typeof(ScanRequestPatch), "Prefix", args);
        var result = Call(typeof(ScanRequestPatch), "Finalizer", args[1], error);
        Check(ReferenceEquals(result, error), "request finalizer preserves native exception");
    }
    private static void Reset(bool host)
    {
        ScanTagPipeline.Reset(); CombatTracker.Credits.Clear(); CombatTracker.ActiveHumanScanLookup = 0;
        SNetwork.SNet.IsMaster = host; CombatSync.Generation++;
        ScanValidationPatch.Owner = 0; ScanReceiveScopePatch.Depth = 0;
    }
    public static void Main()
    {
        var enemy = new Enemies.EnemyAgent { Pointer = new IntPtr(101), IsTagged = true };
        Reset(true); CombatTracker.ActiveHumanScanLookup = 11;
        Execute(enemy);
        Check(CombatTracker.Credits.Count == 0, "receive callback never touches counters before main-thread drain");
        ScanTagPipeline.Drain();
        Check(CombatTracker.Credits.Count == 1 && CombatTracker.Credits[0].Owner == 11, "host local scan execution retains its instance owner");

        Reset(true); ScanReceiveScopePatch.Depth = 1;
        SNetwork.SNet.Replication.Sender = new SNetwork.SNet_Player { Lookup = 22 };
        object[] validation = { 0UL }; Call(typeof(ScanValidationPatch), "Prefix", validation);
        Execute(enemy);
        Call(typeof(ScanValidationPatch), "Finalizer", validation[0], null);
        SNetwork.SNet.Replication.Sender = new SNetwork.SNet_Player { Lookup = 33 };
        ScanReceiveScopePatch.Depth = 0;
        ScanTagPipeline.Drain();
        Check(CombatTracker.Credits.Count == 1 && CombatTracker.Credits[0].Owner == 22,
            "remote scan needs no scanner instance and survives LastSender changing before main-thread drain");
        Check(ScanValidationPatch.Owner == 0, "validation owner cannot leak outside its call");

        Reset(true); SNetwork.SNet.Replication.Sender = new SNetwork.SNet_Player { Lookup = 44 };
        validation = new object[] { 0UL }; Call(typeof(ScanValidationPatch), "Prefix", validation);
        Execute(enemy); Call(typeof(ScanValidationPatch), "Finalizer", validation[0], null); ScanTagPipeline.Drain();
        Check(CombatTracker.Credits.Count == 0, "stale LastSender outside receive scope never attributes an execution");
        Reset(true); ScanReceiveScopePatch.Depth = 1;
        SNetwork.SNet.Replication.Sender = null;
        validation = new object[] { 0UL }; Call(typeof(ScanValidationPatch), "Prefix", validation);
        CombatTracker.ActiveHumanScanLookup = 11;
        Execute(enemy); Call(typeof(ScanValidationPatch), "Finalizer", validation[0], null); ScanTagPipeline.Drain();
        Check(CombatTracker.Credits.Count == 0, "unresolved receive sender never falls back to a concurrently scanning host");
        SNetwork.SNet.Replication.Sender = new SNetwork.SNet_Player { Lookup = 0 };
        validation = new object[] { 0UL }; Call(typeof(ScanValidationPatch), "Prefix", validation);
        Execute(enemy); Call(typeof(ScanValidationPatch), "Finalizer", validation[0], null); ScanTagPipeline.Drain();
        Check(CombatTracker.Credits.Count == 0, "zero receive lookup never falls back to the host scanner");

        Reset(false); CombatTracker.ActiveHumanScanLookup = 55;
        Request(enemy);
        CombatTracker.ActiveHumanScanLookup = 0;
        Execute(enemy); ScanTagPipeline.Drain();
        Check(CombatTracker.Credits.Count == 1 && CombatTracker.Credits[0].Owner == 55,
            "client late execution matches actual request after scanner scope and candidates are gone");
        Execute(enemy); ScanTagPipeline.Drain();
        Check(CombatTracker.Credits.Count == 1, "duplicate client confirmation cannot consume one request twice");

        Reset(false); Execute(enemy); ScanTagPipeline.Drain();
        Check(CombatTracker.Credits.Count == 0, "another player's tag without a local request stays unowned");
        CombatTracker.ActiveHumanScanLookup = 55; Request(enemy);
        CombatTracker.ActiveHumanScanLookup = 0;
        Execute(enemy, new InvalidOperationException("native failed")); ScanTagPipeline.Drain();
        Check(CombatTracker.Credits.Count == 0, "failed native execution never credits a pending request");

        Reset(true); CombatTracker.ActiveHumanScanLookup = 11; Execute(enemy); CombatSync.Generation++;
        ScanTagPipeline.Drain(); Check(CombatTracker.Credits.Count == 0, "queued confirmation from old run is rejected");
        Execute(enemy); ScanTagPipeline.Reset(); ScanTagPipeline.Drain();
        Check(CombatTracker.Credits.Count == 0, "reset drops queued confirmations");
        Reset(true); CombatTracker.ActiveHumanScanLookup = 11;
        Execute(new Enemies.EnemyAgent { Pointer = new IntPtr(102), IsTagged = false });
        Execute(null); ScanTagPipeline.Drain();
        Check(CombatTracker.Credits.Count == 0, "unresolved or untagged native targets are not fabricated marks");
        Reset(false); CombatTracker.ActiveHumanScanLookup = 55;
        Request(enemy, new InvalidOperationException("send failed"));
        CombatTracker.ActiveHumanScanLookup = 0; Execute(enemy); ScanTagPipeline.Drain();
        Check(CombatTracker.Credits.Count == 0, "failed send cancels request before an unrelated confirmation can credit it");
        Reset(false); CombatTracker.ActiveHumanScanLookup = 55;
        object[] rapid = { enemy, IntPtr.Zero }; Call(typeof(ScanRequestPatch), "Prefix", rapid);
        CombatTracker.ActiveHumanScanLookup = 0; Execute(enemy);
        Call(typeof(ScanRequestPatch), "Finalizer", rapid[1], null); ScanTagPipeline.Drain();
        Check(CombatTracker.Credits.Count == 1, "confirmation arriving before request returns still finds the prefix registration");
        Check(CombatTracker.Credits[0].Estimated, "client pairing never masquerades as exact scanner attribution");
        Reset(false); CombatTracker.ActiveHumanScanLookup = 55; Request(enemy);
        CombatTracker.ActiveHumanScanLookup = 0;
        Execute(new Enemies.EnemyAgent { Pointer = enemy.Pointer, IsTagged = true, Identity = 999 });
        ScanTagPipeline.Drain();
        Check(CombatTracker.Credits.Count == 0, "production pairing rejects a different Unity instance at the requested native address");
        // 1.19.62（评审）：机器人扫描与真人确认相遇时的“谁先”由顺序决定，而不是看一份只有客机才会填的请求账本。
        // 这里是真实的主机 `DoTagEnemy` 回调（生产的 `ScanExecutedPatch` 前缀/终结器）→ 生产机器人扫描前所做的同一次
        // `Drain` → 真人的入账 → 同一目标上的机器人快照。两条顺序都验，且两条顺序的计数总量相同。
        Reset(true); CombatTracker.ActiveHumanScanLookup = 11;
        {
            var squad = new SquadScanLedger();
            var humanCounts = new ImpactCounters();
            var botCounts = new ImpactCounters();
            Execute(enemy);
            Check(CombatTracker.Credits.Count == 0, "host DoTagEnemy is queued, not credited, until the drain");
            ScanTagPipeline.Drain();
            Check(CombatTracker.Credits.Count == 1 && CombatTracker.Credits[0].Owner == 11,
                "the drain credits the human's executed tag to its own owner");
            ScanMark humanMark = squad.For(CombatTracker.Credits[0].Owner).ObserveConfirmed(enemy.GetInstanceID(), humanCounts, true);
            Check(humanMark == ScanMark.First && humanCounts.ScanFirstMarks == 1,
                "the human's executed tag takes the squad's first reveal before the bot's snapshot is examined (mark="
                + humanMark + ", identity=" + enemy.GetInstanceID() + ", owner=" + CombatTracker.Credits[0].Owner
                + ", first=" + humanCounts.ScanFirstMarks + ")");
            ScanSession botsSweep = squad.For(0x22UL);
            botsSweep.BeginBotTag(1f, 8.5f, botCounts);
            Check(botsSweep.ObserveConfirmed(enemy.GetInstanceID(), botCounts, enemy.IsTagged, true) == ScanMark.Repeat
                && botCounts.ScanFirstMarks == 0 && botCounts.ScanMarks == 1 && botCounts.ScanUses == 0 && botCounts.ScanEstimated,
                "the bot's snapshot on the same target is a repeat; its cumulative mark, estimate and scan count are unchanged");
        }
        Reset(true);
        {
            // 反顺序：机器人真的先标到 —— 它保留首次，真人的落地成为重复。
            var squad = new SquadScanLedger();
            var botCounts = new ImpactCounters();
            var humanCounts = new ImpactCounters();
            ScanSession botsSweep = squad.For(0x22UL);
            botsSweep.BeginBotTag(2f, 8.5f, botCounts);
            Check(botsSweep.ObserveConfirmed(enemy.GetInstanceID(), botCounts, true, true) == ScanMark.First
                && botCounts.ScanFirstMarks == 1 && botCounts.ScanMarks == 1,
                "a bot that really tagged first keeps the squad's first reveal, and counts normally");
            CombatTracker.ActiveHumanScanLookup = 11;
            Execute(enemy);
            ScanTagPipeline.Drain();
            Check(CombatTracker.Credits.Count == 1, "and the human's later execution is still credited to its owner");
            Check(squad.For(CombatTracker.Credits[0].Owner).ObserveConfirmed(enemy.GetInstanceID(), humanCounts, true) == ScanMark.Repeat
                && humanCounts.ScanFirstMarks == 0 && humanCounts.ScanMarks == 1,
                "which is the repeat: the human keeps its own cumulative mark and takes no first mark");
            Check(botCounts.ScanMarks + humanCounts.ScanMarks == 2,
                "both orders credit the same two marks: only which side holds the single first mark follows the order");
        }
        Reset(true); CombatTracker.ActiveHumanScanLookup = 11;
        {
            var squad = new SquadScanLedger();
            var botCounts = new ImpactCounters();
            Execute(new Enemies.EnemyAgent { Pointer = new IntPtr(102), IsTagged = false });
            ScanTagPipeline.Drain();
            Check(CombatTracker.Credits.Count == 0, "a confirmation the drain refuses is never credited");
            Check(squad.For(0x22UL).ObserveConfirmed(9101, botCounts, true, true) == ScanMark.First,
                "so the bot's own sweep still takes the first reveal when nothing was really credited first");
        }
        Reset(true); CombatTracker.ActiveHumanScanLookup = 11;
        Execute(enemy); CombatSync.Generation++;
        ScanTagPipeline.Drain();
        Check(CombatTracker.Credits.Count == 0, "a confirmation captured in an older run is dropped by the drain");
        Reset(true); CombatTracker.ActiveHumanScanLookup = 11;
        Execute(enemy, new InvalidOperationException("native failed"));
        ScanTagPipeline.Drain();
        Check(CombatTracker.Credits.Count == 0, "a native failure is never queued, so it can never preempt a bot");
        // 1.19.41: the activation figure of one scanner is one independent native cooldown session, on the machine
        // that holds it. Repeated samples of one `m_rechargeStartTime` are one scan, a later start time is the next
        // one, and a cooldown this build cannot read is an unknown count rather than a precise 0 or 1.
        //
        // 1.19.70: the ledger needs a baseline before it may count anything. The recorded 1.19.69 Test archive entered
        // the level with `tagging=False`, detected=7 and `activations=1, completions=0, repeats=0` for a player who
        // had swept nothing: the one session it had counted was the cooldown already in progress when the level
        // began. An unseen busy cooldown is now that baseline, a read idle sample is a baseline too, and only a start
        // proven against one of them is an activation.
        var cooldown = new ScannerCooldownLedger();
        Check(!cooldown.Observable && cooldown.Activations == 0, "a ledger that never read a cooldown reports nothing observable");
        Check(cooldown.Observe(true, true, true, 100f) == ScannerCooldownSignal.Baseline
              && cooldown.Activations == 0 && cooldown.Observable && cooldown.SessionOpen,
            "the first cooldown this ledger ever saw was already busy: it is the baseline, so it is observable and it is no scan");
        Check(cooldown.Observe(true, true, true, 100f) == ScannerCooldownSignal.Continuing && cooldown.Activations == 0,
            "the same start time polled again is the same, still uncounted, session");
        Check(cooldown.Observe(true, false, true, 100f) == ScannerCooldownSignal.Completed && cooldown.Completions == 0,
            "the baseline ending is not the completion of a scan this ledger counted");
        Check(cooldown.Observe(true, false, true, 100f) == ScannerCooldownSignal.Idle, "an idle sample after the session closed proves nothing");
        Check(cooldown.Observe(true, true, true, 100f) == ScannerCooldownSignal.Continuing && cooldown.Activations == 0,
            "a scanner that leaves and re-enters the *same* cooldown is not a new scan: a mid-session reset of m_recharging never re-counts");
        Check(cooldown.Observe(true, true, true, 200f) == ScannerCooldownSignal.Started && cooldown.Activations == 1,
            "a later m_rechargeStartTime is the first independent activation");
        Check(cooldown.Observe(true, true, true, 150f) == ScannerCooldownSignal.Continuing && cooldown.Activations == 1,
            "an out-of-order older start time never adds an activation");
        Check(cooldown.Observe(false, false, false, float.NaN) == ScannerCooldownSignal.Unavailable && cooldown.Activations == 1 && cooldown.Unusable == 1,
            "an unreadable cooldown counts nothing and keeps the number it had");
        var risky = new ScannerCooldownLedger();
        Check(risky.Observe(true, true, false, float.NaN) == ScannerCooldownSignal.Unavailable && risky.Activations == 0 && risky.SessionOpen,
            "a busy scanner whose start time cannot be read names no session, so it is not counted and not called zero");
        Check(risky.Observe(true, true, true, 100f) == ScannerCooldownSignal.Baseline && risky.Activations == 0,
            "and when that same busy streak reads its start at last the start is the baseline it would always have been, not a new scan");
        Check(risky.Observe(true, false, false, float.NaN) == ScannerCooldownSignal.Completed && risky.Activations == 0,
            "the unreadable session still closes on the native finish, and still counts no scan");
        risky.ResetExpedition();
        Check(!risky.Observable && risky.Activations == 0 && risky.Completions == 0 && risky.Repeats == 0 && risky.Unusable == 0,
            "a new expedition starts from nothing");
        // 1.19.70: the same rule driven through the production `ScanSession`, which is what the tracker polls every
        // frame - no Unity and no scanner object, only the native sample the interop read would have produced.
        {
            var residual = new ScanSession();
            var residualCounters = new ImpactCounters();
            Check(residual.ObserveRecharge(true, true, true, 410.5f, residualCounters) == ScannerCooldownSignal.Baseline
                  && residualCounters.ScanUses == 0 && residualCounters.ScanUsesKnown
                  && residualCounters.ScanFirstMarks == 0 && residualCounters.ScanMarks == 0,
                "a level that begins on a recharging scanner credits no scan: the row reads a counted 0 and both mark figures stay 0");
            for (int poll = 0; poll < 120; poll++) residual.ObserveRecharge(true, true, true, 410.5f, residualCounters);
            Check(residualCounters.ScanUses == 0 && residual.CooldownRepeats == 120,
                "a whole cooldown of frame polls on that residue is one session and still no scan");
            Check(residual.ObserveRecharge(true, false, true, 410.5f, residualCounters) == ScannerCooldownSignal.Completed
                  && residualCounters.ScanUses == 0 && residual.CooldownCompletions == 0,
                "the residue running out is not a scan finishing either: nothing was counted, so nothing completes");
        }
        foreach (bool unreadableBetween in new[] { false, true })
        {
            var scanner = new ScanSession();
            var counters = new ImpactCounters();
            scanner.ObserveRecharge(true, true, true, 410.5f, counters);
            scanner.ObserveRecharge(true, false, true, 410.5f, counters);
            if (unreadableBetween)
                for (int poll = 0; poll < 3; poll++) scanner.ObserveRecharge(true, true, false, float.NaN, counters);
            Check(scanner.ObserveRecharge(true, true, true, 12f, counters) == ScannerCooldownSignal.Continuing
                  && counters.ScanUses == 0 && scanner.CooldownCompletions == 0,
                "idle followed by an older start cannot credit a scan (unreadable busy samples: " + unreadableBetween + ")");
            Check(scanner.ObserveRecharge(true, true, true, 410.5f, counters) == ScannerCooldownSignal.Continuing
                  && counters.ScanUses == 0,
                "an older sample does not lower the baseline and make the original cooldown count later");
            Check(scanner.ObserveRecharge(true, true, true, 420f, counters) == ScannerCooldownSignal.Started
                  && counters.ScanUses == 1,
                "the next strictly newer native start still counts once");
        }
        {
            var scanner = new ScanSession();
            var counters = new ImpactCounters();
            Check(scanner.ObserveRecharge(true, false, true, float.NaN, counters) == ScannerCooldownSignal.Idle
                  && counters.ScanUses == 0 && counters.ScanUsesKnown,
                "a scanner read idle before it ever scanned is a counted 0, not an unknown row");
            Check(scanner.ObserveRecharge(true, true, true, 12.25f, counters) == ScannerCooldownSignal.Started
                  && counters.ScanUses == 1,
                "the first real scan of that idle scanner is counted: the read idle is the baseline, not a suppression");
            for (int poll = 0; poll < 60; poll++) scanner.ObserveRecharge(true, true, true, 12.25f, counters);
            Check(scanner.ObserveRecharge(false, false, false, float.NaN, counters) == ScannerCooldownSignal.Unavailable
                  && counters.ScanUses == 1,
                "60 polls of that start and an unreadable sample beside it add no scan and drop none");
            Check(scanner.ObserveRecharge(true, false, true, 12.25f, counters) == ScannerCooldownSignal.Completed
                  && scanner.CooldownCompletions == 1 && counters.ScanUses == 1,
                "the counted cooldown ending is the one completion and not another scan");
            Check(scanner.ObserveRecharge(true, true, true, 12.25f, counters) == ScannerCooldownSignal.Continuing
                  && counters.ScanUses == 1,
                "the same start shown again after the session closed is the session already counted");
            Check(scanner.ObserveRecharge(true, true, true, 26.5f, counters) == ScannerCooldownSignal.Started
                  && counters.ScanUses == 2,
                "a second real scan is a second count");
            scanner.ObserveTagging(false, counters, true);
            Check(scanner.ObserveConfirmed(4401, counters, true) == ScanMark.First
                  && counters.ScanFirstMarks == 1 && counters.ScanMarks == 1,
                "the first sweep of the expedition reveals that enemy once");
            Check(scanner.ObserveRecharge(true, false, true, 26.5f, counters) == ScannerCooldownSignal.Completed
                  && counters.ScanUses == 2, "and its cooldown ending is still not a scan");
            Check(scanner.ObserveRecharge(true, true, true, 34.75f, counters) == ScannerCooldownSignal.Started
                  && counters.ScanUses == 3,
                "a scan that sweeps only an enemy the squad already has is a scan: the count is never capped by first marks");
            scanner.ObserveTagging(false, counters, true);
            Check(scanner.ObserveConfirmed(4401, counters, true) == ScanMark.Repeat
                  && counters.ScanFirstMarks == 1 && counters.ScanMarks == 2 && counters.ScanUses == 3,
                "the repeat costs a cumulative mark and no first mark, while the third real scan is still counted");
        }
        {
            var unreadableFirst = new ScanSession();
            var unreadableFirstCounters = new ImpactCounters();
            Check(unreadableFirst.ObserveRecharge(true, true, false, float.NaN, unreadableFirstCounters) == ScannerCooldownSignal.Unavailable
                  && unreadableFirstCounters.ScanUses == 0,
                "the level's first cooldown sample can be busy and unreadable: it names no session and counts no scan");
            Check(unreadableFirst.ObserveRecharge(true, true, true, 63f, unreadableFirstCounters) == ScannerCooldownSignal.Baseline
                  && unreadableFirstCounters.ScanUses == 0,
                "when that same busy streak reads its start at last it is the baseline, not a scan the player never ran");
            Check(unreadableFirst.ObserveRecharge(true, false, true, 63f, unreadableFirstCounters) == ScannerCooldownSignal.Completed
                  && unreadableFirstCounters.ScanUses == 0,
                "and the residue ending keeps the figure at the counted 0");
            Check(unreadableFirst.ObserveRecharge(true, true, true, 71f, unreadableFirstCounters) == ScannerCooldownSignal.Started
                  && unreadableFirstCounters.ScanUses == 1,
                "the real scan after that idle is still counted once");
        }
        {
            // Bots and remote rows are untouched by the baseline rule: a bot's own action start is still its one use,
            // and a row whose cooldown this machine cannot read still reports an unknown count rather than 0.
            var botUses = new ScanSession();
            var botUseCounters = new ImpactCounters();
            Check(botUses.ObserveBotScan(true, 77.5f, botUseCounters) == BotScanSignal.Started
                  && botUseCounters.ScanUses == 1 && botUseCounters.ScanUsesKnown,
                "a bot's own readable scan start is still exactly one use");
            Check(botUses.ObserveBotScan(true, 77.5f, botUseCounters) == BotScanSignal.Continuing && botUseCounters.ScanUses == 1,
                "and the same action start polled again is still that one use");
            var remoteUses = new ScanSession();
            var remoteCounters = new ImpactCounters();
            remoteUses.BeginConfirmWindow();
            remoteUses.ObserveConfirmed(5501, remoteCounters, true);
            Check(remoteCounters.ScanUses == 0 && !remoteCounters.ScanUsesKnown,
                "a remote row with no readable cooldown keeps its unknown count: the baseline proves a 0 for the local scanner only");
        }
        // 1.19.62: the host's scan-round estimate is fed the *capture* stamp of a confirmed mark, so a backlogged
        // drain must not move that stamp forward: one drain can then neither spread one sweep's marks apart nor
        // make the gap between two real sweeps look shorter than it really was.
        Reset(true); ScanReceiveScopePatch.Depth = 1;
        SNetwork.SNet.Replication.Sender = new SNetwork.SNet_Player { Lookup = 77 };
        validation = new object[] { 0UL }; Call(typeof(ScanValidationPatch), "Prefix", validation);
        double captured = Environment.TickCount64 / 1000.0;
        Execute(enemy); Call(typeof(ScanValidationPatch), "Finalizer", validation[0], null);
        ScanReceiveScopePatch.Depth = 0;
        System.Threading.Thread.Sleep(400);
        double drained = Environment.TickCount64 / 1000.0;
        ScanTagPipeline.Drain();
        Check(CombatTracker.Credits.Count == 1 && CombatTracker.Credits[0].Owner == 77,
            "a remote confirmation captured before a delayed drain is still credited to its sender");
        Check(CombatTracker.Credits[0].ReceivedAt >= captured - 0.05 && CombatTracker.Credits[0].ReceivedAt <= captured + 0.2,
            "the credited stamp is the monotonic receive time, not the later drain time: "
                + CombatTracker.Credits[0].ReceivedAt.ToString("0.###") + " against capture " + captured.ToString("0.###"));
        Check(drained - CombatTracker.Credits[0].ReceivedAt >= 0.2,
            "and the backlog that delayed the drain is not folded into that stamp");
        Console.WriteLine($"PASS: {count} scan pipeline assertions (managed replay, not Unity/Steam)");
    }
}

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)] public sealed class HarmonyPatch : Attribute { public HarmonyPatch(Type type, string method) { } }
    public sealed class HarmonyPrefix : Attribute { }
    public sealed class HarmonyPostfix : Attribute { }
    public sealed class HarmonyFinalizer : Attribute { }
}
namespace Enemies
{
    public sealed class EnemyAgent { public IntPtr Pointer; public bool IsTagged; public int Identity { get; set; } public int GetInstanceID() => Identity != 0 ? Identity : Pointer.ToInt32(); }
}
namespace Agents
{
    public struct pEnemyAgent
    {
        public Enemies.EnemyAgent Target;
        public bool TryGet(out Enemies.EnemyAgent enemy) { enemy = Target; return enemy != null; }
    }
}
namespace SNetwork
{
    public class SNet_Replication { }
    public sealed class SNet_Player { public ulong Lookup; public bool IsLocal { get; set; } public bool IsBot { get; set; } }
    public sealed class ReplicationStub
    {
        public SNet_Player Sender;
        public bool TryGetLastSender(out SNet_Player sender) { sender = Sender; return sender != null; }
    }
    public static class SNet { public static bool IsMaster; public static readonly ReplicationStub Replication = new(); }
}
public sealed class ToolSyncManager { public struct pTagEnemy { public Agents.pEnemyAgent enemy; } }
namespace HawChow.GTFOCombatPanel
{
    internal static class CombatSync { internal static uint Generation = 1; }
    internal static class CombatTracker
    {
        internal static ulong ActiveHumanScanLookup;
        internal static readonly List<(IntPtr Enemy, ulong Owner, bool Estimated, double ReceivedAt)> Credits = new();
        internal static void CreditExecutedScanTag(Enemies.EnemyAgent enemy, ulong owner, bool estimated, double receivedAt) => Credits.Add((enemy.Pointer, owner, estimated, receivedAt));
        internal static void LogToolError(string what, Exception error) => throw new Exception(what, error);
    }
}
