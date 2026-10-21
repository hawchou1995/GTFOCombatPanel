using System;
using System.Collections.Generic;
using Player;
using SNetwork;

namespace HawChow.GTFOCombatPanel;

internal static class SupplyTracker
{
    internal sealed class AmmoScope
    {
        internal AmmoScope Previous;
        internal AmmoScope Root;
        internal SNet_Player Giver;
        internal ulong Receiver;
        internal SNet_Player ReceiverOwner;
        internal IntPtr Storage;
        internal SupplyUse Use = new SupplyUse();
        internal bool Tool;
    }

    internal sealed class AmmoChange
    {
        internal AmmoScope Root;
        internal PlayerAmmoStorage Storage;
        internal AmmoType Type;
        internal double Before, Intended;
    }

    internal sealed class HealthChange
    {
        internal SNet_Player Giver;
        internal SNet_Player Receiver;
        internal double Before, Intended;
    }

    [ThreadStatic] private static AmmoScope _ammo;
    private static readonly HashSet<string> Logged = new HashSet<string>();

    internal static void Reset() { _ammo = null; Logged.Clear(); }

    internal static AmmoScope BeginAmmo(PlayerAgent receiver, PlayerAgent giver)
    {
        Note("ammo-fire", "the ammo give hook fired for the first time: the receiver's pack is measured around the give.");
        return BeginAmmo(receiver?.Owner, giver?.Owner);
    }

    private static AmmoScope BeginAmmo(SNet_Player receiver, SNet_Player giver)
    {
        try
        {
            if (GameStateManager.CurrentStateName != eGameStateName.InLevel || receiver == null ||
                (!CombatTracker.ShouldTrack(giver) && !CombatTracker.ShouldTrack(receiver))) return null;
            if (!PlayerBackpackManager.TryGetBackpack(receiver, out PlayerBackpack backpack) || backpack?.AmmoStorage == null)
            {
                Note("ammo-storage", "supply skipped: receiver ammo storage is unavailable.");
                return null;
            }
            var scope = new AmmoScope { Previous = _ammo, Giver = giver, Receiver = receiver.Lookup, ReceiverOwner = receiver, Storage = backpack.AmmoStorage.Pointer };
            scope.Root = _ammo != null && _ammo.Root.Giver?.Lookup == giver?.Lookup && _ammo.Root.Receiver == receiver.Lookup ? _ammo.Root : scope;
            _ammo = scope;
            return scope;
        }
        catch (Exception error) { Note("ammo-begin", "supply capture skipped: " + error.Message); return null; }
    }

    internal static AmmoScope BeginAmmoPacket(pAmmoGive data)
    {
        try
        {
            Note("ammo-packet-fire", "the ammo give packet callback fired for the first time: the giver comes from the replication sender inside this callback, never from proximity.");
            data.targetPlayer.GetPlayer(out SNet_Player receiver);
            if (receiver == null) return null;
            if (_ammo != null && _ammo.Root.Receiver == receiver.Lookup)
                return BeginAmmo(receiver, _ammo.Root.Giver);
            // On a client the packet sender can be a forwarding host, not the giver.
            // Receiver identity is explicit in the packet and remains usable for consumption.
            SNet_Player sender = null;
            if (SNet.IsMaster) SNet.Replication.TryGetLastSender(out sender);
            return BeginAmmo(receiver, sender);
        }
        catch (Exception error) { Note("ammo-packet", "supply packet skipped: " + error.Message); return null; }
    }

    internal static AmmoChange CaptureAmmo(PlayerAmmoStorage storage, AmmoType type, float intended)
    {
        if (_ammo == null || storage == null || storage.Pointer != _ammo.Root.Storage || !(intended > 0)) return null;
        if (type != AmmoType.Standard && type != AmmoType.Special && type != AmmoType.Class) return null;
        try
        {
            var root = _ammo.Root;
            root.Tool |= type == AmmoType.Class;
            return new AmmoChange { Root = root, Storage = storage, Type = type, Intended = intended, Before = AmmoTotal(storage, type) };
        }
        catch (Exception error)
        {
            _ammo.Root.Use.AddChange(double.NaN, 0, intended);
            Note("ammo-before", "supply read failed: " + error.Message);
            return null;
        }
    }

    internal static void CompleteAmmo(AmmoChange change)
    {
        if (change == null) return;
        try { change.Root.Use.AddChange(change.Before, AmmoTotal(change.Storage, change.Type), change.Intended); }
        catch (Exception error)
        {
            change.Root.Use.AddChange(double.NaN, 0, change.Intended);
            Note("ammo-after", "supply read failed: " + error.Message);
        }
    }

    private static double AmmoTotal(PlayerAmmoStorage storage, AmmoType type)
    {
        double total = storage.GetAmmoInPack(type);
        if (type == AmmoType.Standard || type == AmmoType.Special)
            total += storage.GetClipAmmoFromSlot(PlayerAmmoStorage.GetSlotFromAmmoType(type));
        return total;
    }

    internal static void EndAmmo(AmmoScope scope, Exception error)
    {
        if (scope == null) return;
        if (ReferenceEquals(_ammo, scope)) _ammo = scope.Previous;
        if (!ReferenceEquals(scope.Root, scope)) return; // Nested packet and sender are one use.
        if (error != null) return;
        try
        {
            Record(scope.Giver, scope.Use, scope.Tool ? "tool" : "ammo");
            if (CombatTracker.ShouldTrack(scope.ReceiverOwner))
                scope.Use.RecordConsumption(CombatTracker.ObservePlayer(scope.ReceiverOwner).Impact, !scope.Tool);
        }
        catch (Exception captureError) { Note("ammo-record", "supply result skipped: " + captureError.Message); }
    }

    internal static HealthChange CaptureHealth(Dam_PlayerDamageBase receiver, pAddHealthData data)
    {
        try
        {
            Note("health-fire", "the add health hook fired for the first time: a giver-sourced heal is measured around this callback.");
            if (GameStateManager.CurrentStateName != eGameStateName.InLevel || receiver == null) return null;
            if (!data.source.TryGet(out var agent)) return null;
            var giver = agent?.TryCast<PlayerAgent>()?.Owner;
            if (!CombatTracker.ShouldTrack(giver) && !CombatTracker.ShouldTrack(receiver.Owner?.Owner)) return null;
            // The game's health packet decodes absolute health units with a 100-unit range;
            // source-less regeneration and revive/set-health events do not enter this callback.
            double intended = data.health.Get(100f);
            if (!(intended > 0) || !double.IsFinite(intended)) return null;
            return new HealthChange { Giver = giver, Receiver = receiver.Owner?.Owner, Before = receiver.Health, Intended = intended };
        }
        catch (Exception error) { Note("health-before", "medical supply skipped: " + error.Message); return null; }
    }

    internal static void CompleteHealth(Dam_PlayerDamageBase receiver, HealthChange change)
    {
        if (change == null) return;
        try
        {
            var use = new SupplyUse();
            use.AddChange(change.Before, receiver.Health, change.Intended);
            Record(change.Giver, use, "medical");
            if (CombatTracker.ShouldTrack(change.Receiver))
                use.RecordConsumption(CombatTracker.ObservePlayer(change.Receiver).Impact, false);
        }
        catch (Exception error) { Note("health-after", "medical supply skipped: " + error.Message); }
    }

    private static void Record(SNet_Player giver, SupplyUse use, string kind)
    {
        if (!CombatTracker.ShouldTrack(giver)) return;
        var player = CombatTracker.ObservePlayer(giver);
        if (!use.Record(player.Impact))
        {
            Note(kind + "-unobserved", kind + " supply use was not measured completely; no efficiency was fabricated.");
            return;
        }
        Note(kind + "-credited", $"supply utilization: {kind}, giver={player.Name}, effective={use.Effective:0.###}, intended={use.Intended:0.###}, uses={player.Impact.SupplyUses}, mean={player.Impact.SupplyEfficiency:P1}; packet pickups excluded.");
    }

    private static void Note(string key, string message)
    {
        if (Logged.Add(key)) Plugin.Logger?.LogInfo("[DIAG] " + message);
    }
}
