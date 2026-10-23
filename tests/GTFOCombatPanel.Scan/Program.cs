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
        internal static readonly List<(IntPtr Enemy, ulong Owner, bool Estimated)> Credits = new();
        internal static void CreditExecutedScanTag(Enemies.EnemyAgent enemy, ulong owner, bool estimated) => Credits.Add((enemy.Pointer, owner, estimated));
        internal static void LogToolError(string what, Exception error) => throw new Exception(what, error);
    }
}
