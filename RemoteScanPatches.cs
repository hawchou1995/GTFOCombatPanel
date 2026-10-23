using System;
using System.Collections.Generic;
using Agents;
using HarmonyLib;
using SNetwork;

namespace HawChow.GTFOCombatPanel;

// Also used by supply attribution. LastSender is valid only inside packet dispatch.
[HarmonyPatch(typeof(SNet_Replication), "RecieveBytes")]
internal static class ScanReceiveScopePatch
{
    [ThreadStatic] internal static int Depth;
    [HarmonyPrefix]
    private static void Prefix(out int __state) { __state = Depth; Depth++; }
    [HarmonyFinalizer]
    private static Exception Finalizer(int __state, Exception __exception) { Depth = __state; return __exception; }
}

// A native validation forwards this exact tag request to DoTagEnemy synchronously.
[HarmonyPatch(typeof(ToolSyncManager), "DoTagEnemyValidation")]
internal static class ScanValidationPatch
{
    [ThreadStatic] internal static ulong Owner;
    [HarmonyPrefix]
    private static void Prefix(out ulong __state)
    {
        __state = Owner; Owner = 0;
        try
        {
            if (SNet.IsMaster && ScanReceiveScopePatch.Depth > 0
                && SNet.Replication.TryGetLastSender(out SNet_Player sender)
                && sender != null && !sender.IsLocal && !sender.IsBot)
                Owner = sender.Lookup;
        }
        catch (Exception error) { CombatTracker.LogToolError("scan validation", error); }
    }
    [HarmonyFinalizer]
    private static Exception Finalizer(ulong __state, Exception __exception)
    { Owner = __state; return __exception; }
}

[HarmonyPatch(typeof(ToolSyncManager), "WantToTagEnemy")]
internal static class ScanRequestPatch
{
    [HarmonyPrefix]
    private static void Prefix(Enemies.EnemyAgent enemy, out IntPtr __state)
    {
        __state = IntPtr.Zero;
        try
        {
            ulong owner = CombatTracker.ActiveHumanScanLookup;
            if (!SNet.IsMaster && owner != 0 && enemy != null)
            {
                __state = enemy.Pointer;
                ScanTagPipeline.Request(__state, owner, enemy.GetInstanceID());
            }
        }
        catch (Exception error) { CombatTracker.LogToolError("scan request", error); }
    }
    [HarmonyFinalizer]
    private static Exception Finalizer(IntPtr __state, Exception __exception)
    {
        if (__exception != null && __state != IntPtr.Zero) ScanTagPipeline.Cancel(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(ToolSyncManager), "DoTagEnemy")]
internal static class ScanExecutedPatch
{
    internal readonly struct Capture
    {
        internal readonly ulong Owner;
        internal readonly uint Generation;
        internal Capture(ulong owner, uint generation) { Owner = owner; Generation = generation; }
    }
    [HarmonyPrefix]
    private static void Prefix(out Capture __state)
        => __state = new Capture(SNet.IsMaster
            ? (ScanValidationPatch.Owner != 0 ? ScanValidationPatch.Owner
                : ScanReceiveScopePatch.Depth == 0 ? CombatTracker.ActiveHumanScanLookup : 0UL)
            : 0UL, CombatSync.Generation);
    [HarmonyFinalizer]
    private static Exception Finalizer(ToolSyncManager.pTagEnemy data, Capture __state, Exception __exception)
    {
        if (__exception == null) ScanTagPipeline.Confirmed(data.enemy, __state.Owner, __state.Generation);
        return __exception;
    }
}

// Packet callbacks enqueue only values. Entity resolution and all counters stay on the main thread.
internal static class ScanTagPipeline
{
    private readonly struct Event
    {
        internal readonly pEnemyAgent Enemy;
        internal readonly ulong Owner;
        internal readonly uint Generation;
        internal readonly double Time;
        internal Event(pEnemyAgent enemy, ulong owner, uint generation)
        { Enemy = enemy; Owner = owner; Generation = generation; Time = Environment.TickCount64 / 1000.0; }
    }
    private static readonly object Gate = new();
    private static readonly Queue<Event> Events = new();
    private static readonly ScanTagLedger Pending = new();
    private static void Enqueue(Event value)
    {
        lock (Gate)
        {
            if (Events.Count >= ScanTagLedger.Capacity) Events.Dequeue();
            Events.Enqueue(value);
        }
    }
    // Request/Cancel run only inside the local scanner's main-thread call bracket.
    internal static void Request(IntPtr enemy, ulong owner, int identity)
        => Pending.Request(enemy, owner, Environment.TickCount64 / 1000.0, identity);
    internal static void Cancel(IntPtr enemy) => Pending.Cancel(enemy);
    internal static void Confirmed(pEnemyAgent enemy, ulong owner, uint generation)
        => Enqueue(new Event(enemy, owner, generation));
    internal static void Reset()
    {
        lock (Gate) Events.Clear();
        Pending.Clear();
    }
    internal static void Drain()
    {
        while (true)
        {
            Event entry;
            lock (Gate) { if (Events.Count == 0) return; entry = Events.Dequeue(); }
            if (entry.Generation != CombatSync.Generation) continue;
            try
            {
                if (SNet.IsMaster && entry.Owner == 0) continue;
                var target = entry.Enemy;
                if (!target.TryGet(out var enemy) || enemy == null || !enemy.IsTagged) continue;
                ulong owner = Pending.Confirm(enemy.Pointer, entry.Owner, entry.Time, enemy.GetInstanceID());
                if (owner != 0) CombatTracker.CreditExecutedScanTag(enemy, owner, entry.Owner == 0);
            }
            catch (Exception error) { CombatTracker.LogToolError("scan confirmation", error); }
        }
    }
}
