using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Player;
using SNetwork;

namespace HawChow.GTFOCombatPanel;

// The runtime half of the squad exchange: it turns the game's own slot list into a roster, hands the
// exchange the rows this machine measured, puts the packets the exchange asks for on the game's byte
// channel, and feeds the packets that come back in. Every decision - who may speak for whom, which run a
// packet belongs to, what a peer's row ends up saying - is made by `SquadExchange`, which has no reference
// to Unity, SNet or Harmony and is therefore asserted headlessly.
//
//   host   - reads every player's fire, damage and backpack itself, so it sends one row per slotted player
//            to every peer it may speak to. Its rows are authoritative for damage.
//   client - sends its own row, and only its own row, to every peer it may speak to, and shows the rows
//            the host sends it.
//   either - greets a peer on a cadence once that peer is known to run this build: either its lookup is
//            named in this machine's own configuration, or it has itself sent this machine one of these
//            packets. Nothing - a greeting included - is sent to a machine there is no evidence about, so
//            a host that runs no mod, a bot and a stranger in the lobby are never handed bytes they have
//            no handler for. This machine therefore does not promise to pair two modded clients behind a
//            vanilla host on its own: one of them has to name the other.
//
// Nothing a packet carries is ever written into the capture ledger. `CombatTracker.Store` holds only what
// this machine measured; a received snapshot is cached raw and a row is *projected* for display by cloning
// the local row and folding the cached figures into the clone. That is what stops a host's measured damage
// from being replaced by a client's estimate, stops the next real hit from being added on top of a figure
// that came off the wire, and stops a host that does not run this mod - and so sends nothing - from
// blanking a client's own row.
//
// The transport is `SquadNet`: the game's own byte channel, taken back in a prefix on
// `SNet_Replication.RecieveBytes`. `SquadNet.MaySend` is set here from the exchange's evidence, so the
// transport refuses on its own account anything the exchange should not have asked for, a greeting
// included.
internal static class CombatSync
{
    private static readonly SquadExchange Exchange = new SquadExchange();
    private static bool _armed;
    private static bool _wasMaster;
    private static bool _loggedUnarmed, _loggedSendError, _loggedOutbound;
    // Which peers answered with a protocol this build does not speak. One line per peer rather than one per
    // session: the session-wide latch named whichever peer replied first and then said nothing about the
    // rest, so a lobby where every peer was on an older build read as though only one of them was.
    private static readonly HashSet<ulong> Mismatched = new HashSet<ulong>();
    private static eGameStateName _lastState = eGameStateName.Lobby;
    // The level clock for the inbound diagnostic, and the two once-per-level lines it guards. A squad that
    // exchanges nothing looks identical, from either screen, to a build that cannot exchange anything, so
    // both facts are written down while the level is up rather than inferred afterwards.
    private const float NoInboundSeconds = 20f;
    private static float _levelSince = -1f;
    private static bool _loggedNoInbound, _loggedOutboundRows;
    // Which direction is known to work: a peer is heard here the moment any packet of its arrives, and this
    // machine is known to be heard by it the moment it sends again after this machine sent it something.
    private static readonly Dictionary<ulong, int> Heard = new Dictionary<ulong, int>();
    private static readonly HashSet<ulong> SentTo = new HashSet<ulong>();
    private static readonly HashSet<ulong> Confirmed = new HashSet<ulong>();
    // Who this machine is allowed to start a conversation with. The list is the operator's: lookups of
    // players known to run this build, read from this plugin's own configuration file. Everything else is
    // learned the only way a machine without a mod cannot imitate - by the peer speaking first.
    private static ConfigEntry<string> _allowlistEntry;
    private static string _allowlistText;
    private static readonly List<ulong> Allowlist = new List<ulong>();

    private static int PrimarySlot => (int)InventorySlot.GearStandard;
    private static int SpecialSlot => (int)InventorySlot.GearSpecial;
    private static int ClassSlot => (int)InventorySlot.GearClass;
    private static int MeleeSlot => (int)InventorySlot.GearMelee;

    private static float Now
    {
        get { try { return UnityEngine.Time.realtimeSinceStartup; } catch { return 0f; } }
    }

    internal static bool HasSnapshot(ulong lookup) => Exchange.HasSnapshot(lookup);
    internal static uint Generation => Exchange.Generation;
    internal static uint GenerationOf(ulong lookup) => Exchange.GenerationOf(lookup);

    // True when the host of this session is known to run this mod: it answered a greeting, which is the
    // only evidence there is. A host that runs no mod never answers, so its clients fall back to measuring
    // their own blows locally - the same split Dinorush's StatDisplay makes on MasterHasMod.
    internal static bool HostRunsMod => SNet.IsMaster || Exchange.HostAnswered;

    // The squad total the board divides by. A host that runs this mod states its own; a lobby whose host
    // runs none never will, so the total is then every row the exchange could actually reach - this
    // machine's own measurement plus each peer's own row - and it is an estimate, because a squadmate that
    // answered nothing is not in it. It is never a dash when there is something real to divide by, and
    // never presented as measured when it is not.
    internal static double? SquadTotal
    {
        get
        {
            if (SNet.IsMaster) return null;
            double? host = Exchange.SquadFromHost;
            if (host.HasValue) return host;
            if (!Exchange.AnyPeerAnswered) return null;
            double total = 0;
            foreach (CombatPlayer row in DisplayRows()) total += Math.Max(0, row.Total.EnemyDamage);
            return total;
        }
    }

    internal static void Register()
    {
        if (_armed) return;
        _armed = true;
        try
        {
            Exchange.Report = Line;
            SquadNet.OnHello = OnHello;
            SquadNet.OnSnapshot = OnSnapshot;
            SquadNet.OnProtocolMismatch = OnProtocolMismatch;
            // The transport's own gate. It answers from the same evidence the exchange does, so a packet the
            // exchange should never have asked for cannot reach the wire by any other route; before the
            // configuration is read the gate refuses everything, which is the safe answer.
            SquadNet.MaySend = MayBeSpokenTo;
            BindDiscoveryConfig();
            Plugin.Logger.LogInfo("[DIAG] squad sync armed: nothing is put on the wire - a hello included - for a peer this machine"
                + " has no evidence about. A peer is spoken to when its lookup is named in [Squad Sync] AllowlistedPeers, or after"
                + " it has itself sent this machine a packet of this build's protocol; the greeting then runs at its usual cadence"
                + " (2s burst, 10s keep-alive), the host sends its squad rows each second and a client sends only its own"
                + " measurements. SNet_Replication.RecieveBytes supplies the transport sender identity; snapshot Owner identifies"
                + " the player described, not the sender. Received rows are queued for main-thread authority checks and display"
                + " projection.");
        }
        catch (Exception error)
        {
            _armed = false;
            if (!_loggedUnarmed)
            {
                _loggedUnarmed = true;
                Plugin.Logger.LogWarning("squad sync could not be armed on this build and the panel keeps working from locally observed data only: " + error.GetType().Name + ": " + error.Message);
            }
        }
    }

    // The one signal that can start a conversation between two machines that have never met is this
    // machine's own configuration, so it is read from the plugin's config file rather than invented. The
    // keys live under [Squad Sync] beside the panel's own; a file that has never been edited simply names
    // nobody, and the panel then shares with machines that spoke first and with none else.
    private static void BindDiscoveryConfig()
    {
        try
        {
            ConfigFile config = Plugin.Instance != null ? Plugin.Instance.Config : null;
            if (config == null)
            {
                Plugin.Logger.LogWarning("[DIAG] squad sync discovery: the configuration file is not reachable yet, so no peer is"
                    + " allowlisted and this machine speaks to nobody until one of them speaks first.");
                return;
            }
            _allowlistEntry = config.Bind("Squad Sync", "AllowlistedPeers", string.Empty,
                "SteamID64 lookups of players known to run CombatPanel, separated by commas. Nothing is sent to a player who is"
                + " not named here until that player has itself sent this machine a packet of this mod's protocol, and a greeting"
                + " counts as one: a machine that runs no mod never produces one, so it is never sent anything to probe it."
                + " Leave this empty to share only with players who speak first. The number is the one the game's own lobby"
                + " associates with the player; a value that is not a plain decimal number is ignored rather than guessed at.");
            ApplyAllowlist(true);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning("squad sync discovery: the allowlist could not be read, so this machine speaks to nobody"
                + " until a peer speaks first: " + error.GetType().Name + ": " + error.Message);
        }
    }

    // Re-read on every tick, but only re-applied when the text actually changed: the file can be edited
    // while the game runs, and a lookup added there has to take effect without a restart.
    private static void ApplyAllowlist(bool force)
    {
        if (_allowlistEntry == null) return;
        string text = _allowlistEntry.Value ?? string.Empty;
        if (!force && text == _allowlistText) return;
        _allowlistText = text;
        int accepted = SquadCapability.ParseAllowlist(text, Allowlist);
        Exchange.SetAllowlist(Allowlist);
        Plugin.Logger.LogInfo("[DIAG] squad sync discovery: " + accepted + " peer lookup(s) allowlisted by this machine's"
            + " configuration; every other peer is sent nothing until it has itself sent this machine a packet of this build's"
            + " protocol. Nothing here proves who is online, and no packet is sent to find out.");
    }

    // The transport's gate. Read from the main thread only, where every send of this mod is decided.
    private static bool MayBeSpokenTo(ulong lookup)
    {
        try { return Exchange.Capability.MaySend(lookup); }
        catch (Exception) { return false; }
    }

    // A new expedition starts from zero everywhere, so the figures held for other players describe the
    // previous run and must go, and the run counter moves so that a straggler from the old run is refused.
    internal static void ResetExpedition()
    {
        Heard.Clear(); SentTo.Clear(); Confirmed.Clear(); Mismatched.Clear();
        Exchange.Configure(OwnLookup(), PrimarySlot, SpecialSlot, ClassSlot, MeleeSlot);
        Exchange.ResetForExpedition();
    }

    internal static void Tick(eGameStateName state, float now)
    {
        // Draining first, and unconditionally: the receive prefix runs on the replication layer's own thread
        // and only parks a decoded packet, so the main thread is where every handler that reads game state
        // must run - and a packet must be taken out of the queue even before the exchange is armed.
        SquadNet.Drain();
        if (!_armed) return;
        try
        {
            bool master = SNet.IsMaster;
            if (master != _wasMaster)
            {
                _wasMaster = master;
                Plugin.Logger.LogInfo(master
                    ? "[DIAG] squad sync: this machine is the host now, so every row is measured here and a received row is only ever merged into a projection."
                    : "[DIAG] squad sync: authority moved to another player; the rows this machine did not measure itself are cleared and the new host is greeted afresh until it speaks.");
            }
            if (state != _lastState)
            {
                _lastState = state;
                if (state == eGameStateName.Lobby || state == eGameStateName.NoLobby)
                {
                    Heard.Clear(); SentTo.Clear(); Confirmed.Clear();
                    Exchange.ResetPeers();
                }
            }
            if (state != eGameStateName.InLevel && state != eGameStateName.ExpeditionSuccess && state != eGameStateName.ExpeditionFail) return;
            ulong local = OwnLookup();
            if (local == 0UL) return;
            Exchange.Configure(local, PrimarySlot, SpecialSlot, ClassSlot, MeleeSlot);
            Exchange.SetRoster(Roster(local), master);
            // A lookup added to the file while the game runs takes effect here, without a restart.
            ApplyAllowlist(false);

            // One line each way, once per level. Together they say whether the exchange is running at all and
            // in which direction it is still blind, which is the pair of facts every "nothing is shared"
            // report has to be answered with and neither of which can be read off a screenshot.
            if (state == eGameStateName.InLevel && _levelSince < 0f)
            {
                _levelSince = now; _loggedNoInbound = false; _loggedOutboundRows = false;
            }
            if (state != eGameStateName.InLevel) _levelSince = -1f;

            int rows = 0;
            ulong firstRowPeer = 0UL;
            foreach (SquadSend send in Exchange.Tick(now, CombatTracker.Store.Snapshot()))
            {
                if (!TryPlayer(send.Target, out SNet_Player target) || target == null) continue;
                SentTo.Add(send.Target);
                if (send.Kind == SquadSendKind.Hello) { SquadNet.SendHello(target, local, send.Generation); continue; }
                SquadNet.SendSnapshot(target, send.Snapshot, send.Generation);
                rows++;
                if (firstRowPeer == 0UL) firstRowPeer = send.Target;
            }
            if (rows > 0 && !_loggedOutboundRows)
            {
                _loggedOutboundRows = true;
                Plugin.Logger.LogInfo($"[DIAG] squad sync outbound: {rows} row(s) put on the wire; firstTarget={firstRowPeer}; {Exchange.EverAnsweredPeers} of {Exchange.HumanPeers} human peer(s) have proved they run this build and {Exchange.WithheldPeers} are being sent nothing. A row goes only to a peer with that evidence, and this send receipt alone does not prove remote acceptance.");
            }
            if (!_loggedNoInbound && _levelSince >= 0f && now - _levelSince >= NoInboundSeconds
                && Exchange.HumanPeers > 0 && Heard.Count == 0)
            {
                _loggedNoInbound = true;
                Plugin.Logger.LogWarning($"[DIAG] squad sync inbound: {now - _levelSince:0}s in the level with {Exchange.HumanPeers} human peer(s) in the roster and not one packet heard from any of them, so no other player's row can appear on this screen. {Exchange.WithheldPeers} of them are being sent nothing because this machine holds no evidence that they run this build; name a player's lookup in [Squad Sync] AllowlistedPeers to start the exchange, or wait for it to greet this machine first. Greetings that were allowed keep going on the keep-alive cadence, and a peer on another protocol version answers with its version and is then left alone on purpose, which is what this line exists to show.");
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning("squad sync tick failed once and was skipped: " + error.Message);
        }
    }

    // The squad as the game's own slot list describes it, which is the only authority this mod trusts.
    private static List<SquadPeer> Roster(ulong local)
    {
        var peers = new List<SquadPeer>();
        try
        {
            SNet_PlayerSlotManager slots = SNet.Slots;
            if (slots == null || slots.SlottedPlayers == null) return peers;
            foreach (SNet_Player player in slots.SlottedPlayers)
            {
                if (player == null) continue;
                peers.Add(new SquadPeer(player.Lookup, player.PlayerSlotIndex(), player.Lookup == local, player.IsBot, player.IsMaster));
            }
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("the squad roster could not be read this frame: " + error.Message);
        }
        return peers;
    }

    // ---------------------------------------------------------------- the packets that come back

    private static void OnHello(ulong sender, uint generation, ulong owner)
    {
        try
        {
            if (owner != sender)
            {
                Plugin.Logger.LogWarning($"[DIAG] squad sync hello: refused; peer={sender}; claimedOwner={owner}; reason=hello identity does not match transport sender;");
                return;
            }
            SquadDecision decision = Exchange.OnHello(sender, generation, Now, out bool answer);
            if (decision.Accepted) Note(sender);
            if (!answer) return;
            if (!TryPlayer(sender, out SNet_Player peer) || peer == null) return;
            SquadNet.SendHello(peer, OwnLookup(), Exchange.Generation);
            SentTo.Add(sender);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning("squad sync hello could not be answered: " + error.Message);
        }
    }

    private static void OnSnapshot(ulong sender, uint generation, SyncSnapshot snapshot)
    {
        try
        {
            SquadDecision decision = Exchange.OnSnapshot(sender, generation, snapshot, Now);
            if (decision.Accepted) Note(sender);
            Plugin.Logger.LogInfo($"[DIAG] squad sync row: accepted={decision.Accepted}; peer={sender}; owner={snapshot.Owner}; peerGeneration={generation}; localGeneration={Exchange.Generation};");
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning("squad sync snapshot could not be applied: " + error.Message);
        }
    }

    private static void OnProtocolMismatch(ulong senderLookup, uint version)
    {
        // One line per peer, not one per session: a session-wide latch named whichever peer answered first and
        // said nothing about the rest, which is why a session whose every peer was on an older build produced
        // one mismatch line next to a diagnostic saying no peer had been heard at all.
        if (!Mismatched.Add(senderLookup)) return;
        // A peer that speaks another protocol is not a mod machine this build can talk to: every packet
        // already put on the wire for it was unreadable, so the exchange stops sending and says so.
        Exchange.OnPeerIncompatible(senderLookup);
        Plugin.Logger.LogWarning($"[DIAG] squad sync peer {senderLookup} speaks protocol {version} and this build speaks {SyncProtocol.Version}: the two stay silent rather than decoding each other's fields; one of them has to be updated. This line is written once per peer.");
    }

    // Which direction is known to work. One line for the first packet heard from a peer - that direction is
    // usable - and one more when a peer speaks again after this machine sent it something, which is the only
    // evidence there is that this machine can be heard at all.
    private static void Note(ulong peer)
    {
        Heard.TryGetValue(peer, out int count);
        Heard[peer] = count + 1;
        if (count == 0)
            Plugin.Logger.LogInfo($"[DIAG] squad sync direction: peer={peer} can be heard here; this is the first packet of its that arrived, so the peer-to-this-machine direction works.");
        if (count > 0 && SentTo.Contains(peer) && Confirmed.Add(peer))
        {
            if (!_loggedOutbound)
            {
                _loggedOutbound = true;
                Plugin.Logger.LogInfo($"[DIAG] squad sync direction: peer={peer} heard this machine; it sent packet {count + 1} after this machine sent it one, so the outbound direction works too.");
            }
        }
    }

    // ---------------------------------------------------------------- display rows

    // Every row the display may draw: this machine's own measurements, each projected through the cached
    // remote rows. The capture store is read, never written.
    internal static List<CombatPlayer> DisplayRows()
    {
        List<CombatPlayer> rows = Exchange.Project(CombatTracker.Store.Snapshot(), OwnLookup());
        // A share is only a measurement when the denominator is the squad's. Three denominators are not: a
        // host total that says it covers only the rows that host could measure, the sum of the rows this
        // machine happened to reach, and this machine's own store when it is the host and a slotted player has
        // no damage figure in it yet. Each of those makes every share on the screen an estimate, which the
        // panel prints with its own marker, so a partial denominator is never read as the whole squad.
        bool estimated = SharesAreEstimated(rows);
        for (int index = 0; index < rows.Count; index++)
            if (rows[index] != null && rows[index].Total != null) rows[index].Total.SquadDamageEstimated = estimated;
        return rows;
    }

    private static bool SharesAreEstimated(List<CombatPlayer> rows)
    {
        if (SNet.IsMaster) return !CoversRoster(rows);
        if (Exchange.SquadFromHost.HasValue) return Exchange.SquadFromHostPartial;
        return Exchange.AnyPeerAnswered;
    }

    // Whether every slotted player this machine knows of has a row here at all. Row presence, for the same
    // reason the host's own total is marked on presence: a seat with no row is exactly the one the sum cannot
    // include, while a seat whose row exists but has no figure yet contributed nothing so far. A roster entry
    // with no lookup cannot be described at all, so it is not a gap.
    private static bool CoversRoster(List<CombatPlayer> rows)
    {
        bool any = false;
        foreach (SquadPeer peer in Exchange.Roster)
        {
            if (peer.Lookup == 0UL) continue;
            any = true;
            if (FindRow(rows, peer.Lookup) == null) return false;
        }
        return any;
    }

    private static CombatPlayer FindRow(List<CombatPlayer> rows, ulong lookup)
    {
        for (int index = 0; rows != null && index < rows.Count; index++)
        {
            CombatPlayer row = rows[index];
            if (row != null && row.Lookup == lookup) return row;
        }
        return null;
    }

    // The raw row this machine would put on the wire about itself: the original capture, never the display
    // projection, so what a client reports back is its own measurement and nothing a host merely told it.
    internal static bool TryOwnMeasurement(out CombatPlayer own) => CombatTracker.Store.TryGet(OwnLookup(), out own) && own != null;

    // ---------------------------------------------------------------- helpers

    private static void Line(string text)
    {
        try { Plugin.Logger.LogInfo(text); }
        catch (Exception error) { if (!_loggedSendError) { _loggedSendError = true; Plugin.Logger?.LogWarning("a squad diagnostic line could not be written: " + error.Message); } }
    }

    private static ulong OwnLookup()
    {
        try
        {
            SNet_Player local = SNet.LocalPlayer;
            return local == null ? 0UL : local.Lookup;
        }
        catch { return 0UL; }
    }

    private static bool TryPlayer(ulong lookup, out SNet_Player player)
    {
        try { return SNet.TryGetPlayer(lookup, out player) && player != null; }
        catch { player = null; return false; }
    }
}
