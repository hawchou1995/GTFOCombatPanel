// Managed boundary doubles. Production EwcProjectileBridge.cs is linked unchanged;
// these cannot prove native Unity casts or live EWC/IL2CPP event delivery.
using System;
namespace Il2CppInterop.Runtime.InteropTypes
{
    public class Il2CppObjectBase
    {
        public T TryCast<T>() where T : class => this as T;
    }
}
namespace UnityEngine
{
    public class Component : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
    {
        public object SourceItem;
        public T GetComponent<T>() where T : class => SourceItem as T;
    }
    public static class Time { public static int frameCount = 100; }
}
namespace Gear
{
    public enum InventorySlot { GearStandard = 1, GearSpecial = 2, GearClass = 3 }
    public sealed class ItemEquippable { public HawChow.GTFOCombatPanel.WeaponCombatStats Stats; }
}
namespace Player
{
    public sealed class PlayerAgent { public SNetwork.SNet_Player Owner; }
}
public enum eLimbDamageType { Normal, Weakspot }
public class Dam_EnemyDamageBase : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase { public object Owner; }
public sealed class Dam_StaticEnemyDamage : Dam_EnemyDamageBase { }
public class Dam_EnemyDamageLimb : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
{
    public Dam_EnemyDamageBase m_base;
    public eLimbDamageType m_type;
}
namespace HawChow.GTFOCombatPanel
{
    internal sealed class FireCapture
    {
        internal CombatPlayer Player;
        internal WeaponCombatStats Entry;
        internal readonly ShotSample Sample = new ShotSample();
        internal bool ProjectileObserved;
    }
    internal static class CombatTracker
    {
        internal static FireCapture ActiveFire;
        internal static bool ShouldTrack(SNetwork.SNet_Player owner) => owner != null;
        internal static CombatPlayer ObservePlayer(SNetwork.SNet_Player owner) => owner.Stats;
        internal static WeaponCombatStats ResolveWeapon(CombatPlayer player, SNetwork.SNet_Player owner,
            Gear.InventorySlot slot, Gear.ItemEquippable item) => item.Stats;
        internal static void NoteAttack(ulong lookup, int slot) { }
    }
}
