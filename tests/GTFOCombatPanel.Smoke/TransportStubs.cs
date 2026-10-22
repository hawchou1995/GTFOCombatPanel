// Managed boundary doubles only. SquadNet.cs itself is linked unchanged into this suite;
// these doubles do not simulate native IL2CPP marshalling or Steam transport. What they do record is
// how many byte runs actually reached `SNet.Core.SendBytes`, which is the only way a unit test can tell
// "the exchange asked for this" from "this machine put it on the wire".
using System;
namespace Il2CppInterop.Runtime.InteropTypes.Arrays
{
    public sealed class Il2CppStructArray<T>
    {
        private readonly T[] _data;
        public Il2CppStructArray(T[] data) { _data = data; }
        public static implicit operator Il2CppStructArray<T>(T[] data) => data == null ? null : new Il2CppStructArray<T>(data);
        public static implicit operator T[](Il2CppStructArray<T> data) => data?._data;
    }
}
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)] public sealed class HarmonyPatch : Attribute { public HarmonyPatch(Type type, string method) { } }
    [AttributeUsage(AttributeTargets.Method)] public sealed class HarmonyPrefix : Attribute { }
    [AttributeUsage(AttributeTargets.Method)] public sealed class HarmonyPriority : Attribute { public HarmonyPriority(int priority) { } }
    [AttributeUsage(AttributeTargets.Method)] public sealed class HarmonyWrapSafe : Attribute { }
    public static class Priority { public const int High = 600; }
}
namespace SNetwork
{
    public sealed class SNet_Player
    {
        public ulong Lookup;
        public bool IsLocal = true;
        public bool IsBot;
        public HawChow.GTFOCombatPanel.CombatPlayer Stats;
    }
    public sealed class SNet_Replication { }
    public enum SNet_ChannelType { GameOrderCritical }
    public enum SNet_SendGroup { Others }
    public enum SNet_SendQuality { Reliable }
    public sealed class TestCore
    {
        // Every byte run that actually left this machine, and for whom. The send gate is asserted on these.
        public int SendCount;
        public ulong LastTarget;
        public void SendBytes(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte> bytes, SNet_SendQuality quality, int channel, SNet_Player target)
        {
            SendCount++;
            LastTarget = target == null ? 0UL : target.Lookup;
        }
    }
    public static class SNet
    {
        public static readonly TestCore Core = new TestCore();
        public static bool IsMaster = true;
        public static void GetSendSettings(ref SNet_ChannelType type, out SNet_SendGroup group, out SNet_SendQuality quality, out int channel)
        { group = SNet_SendGroup.Others; quality = SNet_SendQuality.Reliable; channel = 2; }
    }
}
namespace HawChow.GTFOCombatPanel
{
    internal static class Plugin { internal static readonly TestLogger Logger = new TestLogger(); }
    internal sealed class TestLogger
    {
        internal void LogWarning(string message) => Console.WriteLine("TRANSPORT_WARNING: " + message);
        internal void LogInfo(string message) => Console.WriteLine(message);
    }
}
