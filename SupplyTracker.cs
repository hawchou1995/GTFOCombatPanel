using System;
using System.Collections.Generic;
using Player;
using SNetwork;

namespace HawChow.GTFOCombatPanel;

// A supply use is a request/apply pair, not one call. The machine that opens the pack names the receiver
// and the three shares (`PlayerAgent.GiveAmmoRel`, whose first argument is the pack user), and the
// pickup that actually moves the numbers is applied later from the replicated `pAmmoGive`. That packet
// carries the receiver and the three shares and no giver at all - unlike `pAddHealthData`, which carries
// its `source` agent - and the authority relays it, so on a client the delivery's sender is the master
// that forwarded it. The giver is therefore remembered here, on the machine that made the give, and each
// delivered pickup is matched back to it. A delivery this machine cannot match is measured and its
// consumption is credited, but its efficiency is not invented: neither the forwarding host, nor the
// local player, nor a stale sender ever stands in for a giver.
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

    // The gives this machine made and has not seen applied yet, plus the player object each giver lookup
    // belongs to (the ledger itself stays plain numbers so its policy can be exercised without the game).
    private static readonly SupplyGiveLedger Gives = new SupplyGiveLedger();
    private static readonly Dictionary<ulong, SNet_Player> PackUsers = new Dictionary<ulong, SNet_Player>();

    [ThreadStatic] private static AmmoScope _ammo;
    private static readonly HashSet<string> Logged = new HashSet<string>();

    internal static void Reset() { _ammo = null; Gives.Clear(); PackUsers.Clear(); Logged.Clear(); }

    // Every give the pack user's own machine makes, in the order it makes them. This is the only place the
    // ammo path still knows who the pack belonged to.
    internal static void NoteGive(PlayerAgent receiver, PlayerAgent giver, float standard, float special, float tool)
    {
        try
        {
            if (GameStateManager.CurrentStateName != eGameStateName.InLevel) return;
            SNet_Player giverOwner = giver != null ? giver.Owner : null;
            if (giverOwner == null)
            {
                Note("ammo-give-null", "an ammo give arrived without a giver agent, so no giver can be named for it: its efficiency is not invented.");
                return;
            }
            SNet_Player receiverOwner = receiver != null ? receiver.Owner : null;
            if (receiverOwner == null) return;
            if (!CombatTracker.ShouldTrack(giverOwner) && !CombatTracker.ShouldTrack(receiverOwner)) return;
            if (!Gives.Register(giverOwner.Lookup, receiverOwner.Lookup, standard, special, tool, Now())) return;
            PackUsers[giverOwner.Lookup] = giverOwner;
            Note("ammo-give", $"an ammo give was recorded on the pack user's machine: giver={giverOwner.Lookup:X}, receiver={receiverOwner.Lookup:X}, shares={standard:0.###}/{special:0.###}/{tool:0.###}; the replicated pickup names only the receiver, so this pair is what will name the giver.");
        }
        catch (Exception error) { Note("ammo-give-error", "supply give capture skipped: " + error.Message); }
    }

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
            Note("ammo-packet-fire", "the ammo give packet callback fired for the first time: the receiver is explicit in the packet and the giver is the pack user this machine remembered, never the machine that forwarded the packet.");
            data.targetPlayer.GetPlayer(out SNet_Player receiver);
            if (receiver == null) return null;
            // A nested native callback - the authority applying the action it just performed - already named
            // the giver; that is the strongest answer and is never overridden by a remembered one.
            SNet_Player scoped = _ammo != null && _ammo.Root.Receiver == receiver.Lookup ? _ammo.Root.Giver : null;
            SNet_Player relayed = null;
            SupplySenderCandidate sender = default;
            if (SNet.IsMaster && ScanReceiveScopePatch.Depth > 0)
            {
                try
                {
                    if (SNet.Replication.TryGetLastSender(out SNet_Player last) && last != null)
                    {
                        relayed = last;
                        sender = new SupplySenderCandidate
                        {
                            Lookup = last.Lookup,
                            Allowed = SupplyGiverRule.SenderAllowed(true, last.IsLocal, last.IsInSlot),
                        };
                    }
                }
                catch (Exception) { }
            }
            Gives.TryResolve(receiver.Lookup, data.ammoStandardRel, data.ammoSpecialRel, data.ammoClassRel, Now(),
                scoped != null ? scoped.Lookup : 0UL, sender, out ulong giverLookup, out SupplyGiverSource source);
            SNet_Player giver = GiverFor(source, scoped, relayed, giverLookup);
            if (Gives.Count == 0) PackUsers.Clear();
            switch (source)
            {
                case SupplyGiverSource.Scope:
                    Note("ammo-giver-scope", "the ammo pickup was named by the nested give that is still running: the giver is the agent that call carried.");
                    break;
                case SupplyGiverSource.Give:
                    Note("ammo-giver-give", "the ammo pickup was named by the give this machine remembered, so a client credits the pack user instead of the master that forwarded the packet.");
                    break;
                case SupplyGiverSource.Sender:
                    Note("ammo-giver-sender", "the ammo pickup was named by the delivery sender on the authority machine, which is the player that asked for the give.");
                    break;
                default:
                    Note("ammo-giver-unknown", "an ammo pickup arrived that this machine never gave and cannot attribute: it is measured, its consumption is credited to the receiver, and no efficiency is fabricated for a giver that is not known here.");
                    break;
            }
            return BeginAmmo(receiver, giver);
        }
        catch (Exception error) { Note("ammo-packet", "supply packet skipped: " + error.Message); return null; }
    }

    private static SNet_Player GiverFor(SupplyGiverSource source, SNet_Player scoped, SNet_Player relayed, ulong lookup)
    {
        switch (source)
        {
            case SupplyGiverSource.Scope: return scoped;
            case SupplyGiverSource.Give: return PackUsers.TryGetValue(lookup, out SNet_Player remembered) ? remembered : null;
            case SupplyGiverSource.Sender: return relayed;
            default: return null;
        }
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
            // The game's health packet decodes absolute health units with a 100-unit range, and unlike the
            // ammo packet it carries the giver's agent in `source`, so the medical path needs no recovery.
            // Source-less regeneration and revive/set-health events do not enter this callback.
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

    private static double Now()
    {
        try { return UnityEngine.Time.realtimeSinceStartup; }
        catch (Exception) { return 0.0; }
    }

    private static void Note(string key, string message)
    {
        if (Logged.Add(key)) Plugin.Logger?.LogInfo("[DIAG] " + message);
    }
}
