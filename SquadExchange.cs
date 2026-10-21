using System;
using System.Collections.Generic;

namespace HawChow.GTFOCombatPanel;

// One slotted player as the transport describes it: the identity a packet is addressed to, the seat the
// row belongs in, and the flags that decide who may speak for whom.
public readonly struct SquadPeer
{
    public readonly ulong Lookup;
    public readonly int Slot;
    public readonly bool IsLocal;
    public readonly bool IsBot;
    public readonly bool IsMaster;

    public SquadPeer(ulong lookup, int slot, bool isLocal, bool isBot, bool isMaster)
    {
        Lookup = lookup; Slot = slot; IsLocal = isLocal; IsBot = isBot; IsMaster = isMaster;
    }

    // Only a human has a plugin on the other end that could answer.
    public bool IsHuman => !IsBot && Lookup != 0UL;
}

public enum SquadSendKind { Hello, Snapshot }

// One packet the exchange wants put on the wire this tick. The exchange never touches SNet itself, which
// is what lets the whole exchange be driven in memory by the headless suite.
public readonly struct SquadSend
{
    public readonly SquadSendKind Kind;
    public readonly ulong Target;
    public readonly SyncSnapshot Snapshot;
    public readonly uint Generation;

    public SquadSend(SquadSendKind kind, ulong target, in SyncSnapshot snapshot, uint generation)
    {
        Kind = kind; Target = target; Snapshot = snapshot; Generation = generation;
    }
}

// What the exchange decided about one received packet, with everything the diagnostic line needs: who it
// came from, which run it claimed, which run this machine is in, and why it was taken or refused.
public readonly struct SquadDecision
{
    public readonly bool Accepted;
    public readonly string Reason;
    public readonly ulong Peer;
    public readonly uint PeerGeneration;
    public readonly uint LocalGeneration;

    public SquadDecision(bool accepted, string reason, ulong peer, uint peerGeneration, uint localGeneration)
    {
        Accepted = accepted; Reason = reason; Peer = peer; PeerGeneration = peerGeneration; LocalGeneration = localGeneration;
    }

    public string Line(string what)
        => "[DIAG] squad sync " + what + ": " + (Accepted ? "accepted" : "refused")
            + "; peer=" + Peer + "; reason=" + Reason
            + "; peerGeneration=" + PeerGeneration + "; localGeneration=" + LocalGeneration + ";";
}

// The whole squad exchange, with no Unity, no SNet and no Harmony in sight: who may speak for whom, who
// has been heard from, which run a packet belongs to, what a peer's row says, and how this machine's own
// rows are projected for display.
//
// The shape of it, in three topologies:
//
//   * a host that runs this mod reads every player's fire, damage and backpack itself, so it sends one row
//     per slotted player to every peer that has spoken to it. Its rows are authoritative for damage.
//   * a client sends its own row - and only its own row - to every peer that has spoken to it. That row is
//     authoritative for its aim, its supplies and its scans, which no other machine can count.
//   * a lobby whose host runs no mod of its own still exchanges between the clients: every machine greets
//     every other one, so two clients with this mod see each other directly and the vanilla host, which
//     never answers, is simply never counted and never given a row.
//
// Authority is decided by the roster, not by trust in the packet: a machine may describe any player if it
// is the host, and otherwise only the player whose identity it actually is. A row about somebody else from
// a peer that is not the host is dropped.
internal sealed class SquadExchange
{
    // One row per player per second. The result screens are static, so this only has to keep up with play.
    public const float SnapshotSeconds = 1f;

    private readonly HandshakePolicy Handshake = new HandshakePolicy();
    private readonly SnapshotCache Cache = new SnapshotCache();
    private readonly List<SquadPeer> Peers = new List<SquadPeer>();
    // The lookups the previous roster held, so a seat that changed hands is noticed and forgotten.
    private readonly List<ulong> Known = new List<ulong>();

    private ulong _local;
    private bool _localIsMaster;
    private uint _generation = 1;
    private float _nextSnapshot;
    private ulong _master;
    private double? _squadTotal;
    // Whether the total above covers only the rows the host could measure. It travels with the figure: a
    // host that cannot describe a slotted player says so, and the shares built on its total are then shown
    // as estimates rather than as the squad's measurement.
    private bool _squadTotalPartial;
    private int _primarySlot, _specialSlot, _classSlot, _meleeSlot;

    // Every accepted and refused packet is published here. The runtime points it at the BepInEx logger; the
    // headless suite records it and asserts on the exact fields.
    public Action<string> Report = _ => { };

    public uint Generation => _generation;
    public int CachedRows => Cache.Count;
    public IReadOnlyList<SquadPeer> Roster => Peers;
    public double? SquadFromHost => _squadTotal;

    // True when the host's total covers only the rows that host could measure, so every share divided by it
    // is an estimate. A total that is absent answers false: there is nothing to qualify.
    public bool SquadFromHostPartial => _squadTotal.HasValue && _squadTotalPartial;

    // True only when the host of this squad has answered this machine. A host that runs this mod always
    // does; one that runs no mod never can, and that is the whole evidence there is for telling the two
    // apart.
    public bool HostAnswered => _master != 0UL && Handshake.IsAnswered(_master);

    // Whether any human peer at all has answered, host or not. A lobby whose host runs nothing still has
    // this machine's own row and the rows of the clients that did answer, which is a total worth dividing
    // by - as an estimate - rather than nothing at all.
    public bool AnyPeerAnswered
    {
        get
        {
            foreach (SquadPeer peer in Peers)
                if (peer.IsHuman && !peer.IsLocal && Handshake.IsAnswered(peer.Lookup)) return true;
            return false;
        }
    }

    internal void Configure(ulong local, int primarySlot, int specialSlot, int classSlot, int meleeSlot)
    {
        _local = local;
        _primarySlot = primarySlot; _specialSlot = specialSlot; _classSlot = classSlot; _meleeSlot = meleeSlot;
    }

    internal bool HasSnapshot(ulong lookup) => Cache.Has(lookup);
    internal uint GenerationOf(ulong lookup) => Handshake.GenerationOf(lookup);

    // A new expedition: every figure held for another player describes the run that just ended, and the run
    // counter moves so that a packet still in flight from it cannot land in the new one's counters.
    internal void ResetForExpedition()
    {
        Cache.Clear();
        _squadTotal = null; _squadTotalPartial = false;
        Handshake.Reset();
        unchecked { _generation++; }
        if (_generation == 0) _generation = 1;
        Report("[DIAG] squad sync run counter: this machine's run is now generation=" + _generation
            + "; every figure held for another player described the run that just ended and has been dropped.");
    }

    // The lobby was left, or this machine's role in it changed: who answered, how often they were asked,
    // which run they were in and every row they described all belong to the session that just ended.
    internal void ResetPeers()
    {
        Cache.Clear();
        _squadTotal = null; _squadTotalPartial = false;
        Handshake.Reset();
        Known.Clear();
        _nextSnapshot = 0f;
        Report("[DIAG] squad sync peers cleared: the lobby, the session or this machine's role in it changed, so every peer is fresh again.");
    }

    // The roster is rebuilt from the game's own slot list every frame. A peer that left is forgotten; the
    // host changing hands invalidates everything the previous host said.
    internal void SetRoster(IEnumerable<SquadPeer> peers, bool localIsMaster)
    {
        Peers.Clear();
        ulong master = 0;
        if (peers != null)
        {
            foreach (SquadPeer peer in peers)
            {
                if (peer.Lookup == 0UL) continue;
                Peers.Add(peer);
                if (peer.IsMaster) master = peer.Lookup;
            }
        }
        if (_local != 0UL && localIsMaster) master = _local;
        // A client whose roster does not name a master still has one: the vanilla host, which never answers.
        // Nothing is ever cached for it, but authority has to know that somebody else is the host.
        if (!localIsMaster && master == 0) master = 1UL;
        if (master != _master)
        {
            _master = master;
            Cache.Clear();
            _squadTotal = null; _squadTotalPartial = false;
            Handshake.Reset();
            Report("[DIAG] squad sync authority: this machine is " + (localIsMaster ? "the host" : "a client")
                + ", and the host is peer=" + master + "; every row the previous authority described has been dropped.");
        }
        _localIsMaster = localIsMaster;
        Prune();
    }

    // A peer that is no longer slotted is dropped from the bookkeeping, so a player who later sits in that
    // seat is greeted as the newcomer it is rather than inheriting the previous occupant's run counter.
    private void Prune()
    {
        bool removedCachedRow = false;
        for (int index = Known.Count - 1; index >= 0; index--)
        {
            ulong lookup = Known[index];
            bool present = false;
            foreach (SquadPeer peer in Peers) if (peer.Lookup == lookup) { present = true; break; }
            if (present) continue;
            Known.RemoveAt(index);
            Handshake.Forget(lookup);
            bool hadCachedRow = Cache.Has(lookup);
            Cache.Remove(lookup);
            removedCachedRow |= hadCachedRow;
            Report("[DIAG] squad roster departed: lookup=" + lookup + "; cachedRowRemoved="
                + hadCachedRow.ToString().ToLowerInvariant() + "; handshake state removed.");
        }
        if (removedCachedRow)
        {
            if (Cache.TryGetSquadTotal(out double remainingSquadTotal, out bool remainingPartial))
            {
                _squadTotal = remainingSquadTotal;
                _squadTotalPartial = remainingPartial;
            }
            else _squadTotal = null;
        }
        foreach (SquadPeer peer in Peers)
        {
            bool known = false;
            foreach (ulong lookup in Known) if (lookup == peer.Lookup) { known = true; break; }
            if (!known) Known.Add(peer.Lookup);
        }
    }

    private bool TryPeer(ulong lookup, out SquadPeer peer)
    {
        foreach (SquadPeer candidate in Peers)
        {
            if (candidate.Lookup != lookup) continue;
            peer = candidate;
            return true;
        }
        peer = default;
        return false;
    }

    // ---------------------------------------------------------------- what goes out

    // The packets this machine wants sent right now: a hello to every peer it has not heard from, and one row
    // per player (as host) or this machine's own row (as client) to every peer it has.
    internal List<SquadSend> Tick(float now, IReadOnlyList<CombatPlayer> measured)
    {
        var sends = new List<SquadSend>();
        foreach (SquadPeer peer in Peers)
        {
            if (!peer.IsHuman || peer.IsLocal) continue;
            if (Handshake.ShouldGreet(peer.Lookup, now))
                sends.Add(new SquadSend(SquadSendKind.Hello, peer.Lookup, default, _generation));
        }
        if (now < _nextSnapshot) return sends;
        _nextSnapshot = now + SnapshotSeconds;
        CombatPlayer own = Find(measured, _local);
        foreach (SquadPeer peer in Peers)
        {
            if (!peer.IsHuman || peer.IsLocal) continue;
            // Every human peer is sent to, whether or not it has answered, and no handshake gates this.
            // The proven mods on this game push their figures over this channel with no handshake at all,
            // and gating delivery on one was this build's own invention: it made the data depend on a
            // *return* path, and the client log this release was cut from shows that path silent for a whole
            // session - every greeting unanswered, not one packet received - while both machines were
            // running this mod. A peer that cannot route these bytes simply ignores them; it is the receiver
            // that decides what it will take (`OnSnapshot`: the sender must be in the roster, and a row about
            // somebody else is only taken from the host), so sending early can never put a wrong row on
            // anyone's screen.
            // The gate is gone on purpose: every human peer is spoken to, answered or not (see the comment
            // above). The line below is what it replaced.
            // if (!Handshake.IsAnswered(peer.Lookup)) continue;
            if (_localIsMaster)
            {
                HostRows(measured, peer.Lookup, sends);
                continue;
            }
            if (own == null) continue;
            // A client sends no squad total: only the host can add the four columns up, and a share of a
            // total this machine guessed at would be a lie printed as a percentage.
            sends.Add(new SquadSend(SquadSendKind.Snapshot, peer.Lookup,
                SyncProtocol.Capture(own, _primarySlot, _specialSlot, _classSlot, _meleeSlot, 0, false), _generation));
        }
        return sends;
    }

    // For the diagnostic lines only: how many human peers the roster holds, and how many of them have
    // proved they run this mod. A machine that is in a level with human peers and has never heard from one
    // of them cannot be sharing anything, and that has to be visible in the log rather than inferred.
    public int HumanPeers
    {
        get { int count = 0; foreach (SquadPeer peer in Peers) if (peer.IsHuman && !peer.IsLocal) count++; return count; }
    }

    public int EverAnsweredPeers
    {
        get { int count = 0; foreach (SquadPeer peer in Peers) if (peer.IsHuman && !peer.IsLocal && Handshake.EverAnswered(peer.Lookup)) count++; return count; }
    }

    // Every row a host can describe, each one addressed to `target`. A peer's own row carries what that peer
    // measured about itself, which the host only has second hand, so the peer's exact aim, supplies and scans
    // are folded into what the host sends on about it.
    private void HostRows(IReadOnlyList<CombatPlayer> measured, ulong target, List<SquadSend> sends)
    {
        double total = 0;
        for (int index = 0; measured != null && index < measured.Count; index++)
            total += Math.Max(0, measured[index].Total.EnemyDamage);
        // A total that adds up only the rows this host holds is a *partial* squad total: a slotted player this
        // machine has no row for - a client still loading, one whose row was pruned when its slot changed - is
        // not in it. Sending that figure as the squad's measurement is how a client ends up dividing real
        // damage by a denominator short of the squad it is describing. It is still sent, because it is the only
        // total there is, but it is marked, and the receiving side prints every share built on it as an
        // estimate.
        bool partial = !RosterCoversAll(measured);
        foreach (SquadPeer peer in Peers)
        {
            // Every slotted player this host can name goes out, bots included. `IsHuman` answers "can this
            // peer be talked TO on this channel", which is why the recipient loop in `Tick` uses it - but it
            // is the wrong question for whose row is worth sending. A bot never answers and never runs this
            // mod, while the host measures its damage, staggers, kills and revives exactly as it measures a
            // human's; a client can measure none of that for itself. Sending only the human rows is why a
            // client showed the host and itself and nobody else. A roster entry with no lookup cannot be
            // described at all, and the receiver refuses `Owner == 0` anyway, so that is all that is skipped.
            if (peer.Lookup == 0UL) continue;
            CombatPlayer row = Find(measured, peer.Lookup);
            if (row == null) continue;
            SyncSnapshot snapshot = SyncProtocol.Capture(row, _primarySlot, _specialSlot, _classSlot, _meleeSlot, total, true, partial);
            if (Cache.TryGet(peer.Lookup, out SyncSnapshot fromPeer))
                snapshot = CombatMerge.PreferClientOwned(snapshot, fromPeer);
            sends.Add(new SquadSend(SquadSendKind.Snapshot, target, snapshot, _generation));
        }
    }

    // Whether this machine holds a row for every player on the roster. Row presence, not "somebody already
    // dealt damage": a seat with no row here is exactly the one the total cannot include, while a seat whose
    // row exists but carries no figure yet genuinely contributed nothing so far - marking the whole board as an
    // estimate until the last player's first shot would be a marker with no meaning. A roster entry with no
    // lookup cannot be described by anybody, so it is not a gap either.
    private bool RosterCoversAll(IReadOnlyList<CombatPlayer> measured)
    {
        foreach (SquadPeer peer in Peers)
        {
            if (peer.Lookup == 0UL) continue;
            if (Find(measured, peer.Lookup) == null) return false;
        }
        return true;
    }

    private static CombatPlayer Find(IReadOnlyList<CombatPlayer> rows, ulong lookup)
    {
        for (int index = 0; rows != null && index < rows.Count; index++)
            if (rows[index] != null && rows[index].Lookup == lookup) return rows[index];
        return null;
    }

    // ---------------------------------------------------------------- what comes in

    // A peer offering the protocol. Accepting it is what puts the pair in touch; the answer is a hello back,
    // and it is re-sent on a cadence, because the first answer is exactly the packet a busy lobby drops.
    internal SquadDecision OnHello(ulong sender, uint generation, float now, out bool answer)
    {
        answer = false;
        if (sender == 0UL || sender == _local)
            return Refuse("hello", "a hello from nobody, or an echo of this machine's own packet", sender, generation);
        if (!TryPeer(sender, out _))
            return Refuse("hello", "the sender is not in this squad's roster", sender, generation);
        Handshake.Heard(sender, now);
        if (!Handshake.AcceptsFrom(sender, generation, now, out bool recovered))
            return Refuse("hello", Handshake.DescribeStale(sender), sender, generation);
        if (recovered)
        {
            bool remoteHostRestarted = sender == _master && sender != _local;
            if (remoteHostRestarted)
            {
                Cache.Clear();
                _squadTotal = null; _squadTotalPartial = false;
            }
            else
            {
                Cache.Remove(sender);
                if (Cache.TryGetSquadTotal(out double remainingTotal, out bool remainingPartial))
                {
                    _squadTotal = remainingTotal;
                    _squadTotalPartial = remainingPartial;
                }
                else _squadTotal = null;
            }
            Report("[DIAG] squad sync cache cleared before accepting Hello-first run recovery: peer=" + sender
                + "; hostRestart=" + remoteHostRestarted.ToString().ToLowerInvariant() + "; affectedRow="
                + (remoteHostRestarted ? "all host snapshots" : "the restarted peer") + ".");
        }
        answer = Handshake.ShouldAnswer(sender, now);
        return Accept("hello", recovered
            ? "the sender restarted, its stale cached row was cleared, and its run counter is adopted"
            : "the peer speaks this protocol and is answered", sender, generation);
    }

    // A row from a peer. Who may speak for whom is decided from the roster and nothing else.
    internal SquadDecision OnSnapshot(ulong sender, uint generation, in SyncSnapshot snapshot, float now)
    {
        if (sender == 0UL || sender == _local)
            return Refuse("snapshot", "a row from nobody, or an echo of this machine's own packet", sender, generation);
        if (!TryPeer(sender, out SquadPeer peer))
            return Refuse("snapshot", "the sender is not in this squad's roster", sender, generation);
        if (snapshot.Owner == 0UL)
            return Refuse("snapshot", "the row describes no player", sender, generation);
        if (!TryPeer(snapshot.Owner, out _))
            return Refuse("snapshot", "the row describes a player outside the current squad roster", sender, generation);
        // Who may describe another player: the host, or the machine this roster names as the host. The roster
        // is the authority here - a peer's own `IsMaster` flag is only one way of saying it - and a host whose
        // flag did not say so had every row about a third player refused while its rows about itself went
        // through, which reads on screen as one client seeing itself and the host and nobody else.
        bool fromHost = peer.IsMaster || (sender == _master && sender != _local);
        if (!fromHost && snapshot.Owner != sender)
            return Refuse("snapshot", "a row about another player from a peer that is not the host", sender, generation);
        Handshake.Heard(sender, now);
        if (!Handshake.AcceptsFrom(sender, generation, now, out bool recovered))
            return Refuse("snapshot", Handshake.DescribeStale(sender), sender, generation);
        if (!Cache.Store(snapshot.Owner, generation, snapshot, fromHost, recovered))
            return Refuse("snapshot", "a row from an older run than the one already held for that player", sender, generation);
        // The total and its "covers only what this host could measure" bit always move together, so a share
        // divided by a partial denominator is printed as the estimate it is.
        if (fromHost && snapshot.Has(SyncFlags.SquadDamageKnown))
        {
            _squadTotal = snapshot.SquadEnemyDamage;
            _squadTotalPartial = snapshot.Has(SyncFlags.SquadDamagePartial);
        }
        return Accept("snapshot", (recovered
            ? "the sender restarted and its run counter began again, so the counter it insists on is adopted"
            : "the row is cached for display")
            + (snapshot.Owner == _local ? "; it describes this machine's own row, which keeps its own exact counters" : ""),
            sender, generation);
    }

    private SquadDecision Accept(string what, string reason, ulong sender, uint generation)
        => Publish(what, new SquadDecision(true, reason, sender, generation, _generation));

    private SquadDecision Refuse(string what, string reason, ulong sender, uint generation)
        => Publish(what, new SquadDecision(false, reason, sender, generation, _generation));

    private SquadDecision Publish(string what, SquadDecision decision)
    {
        try { Report(decision.Line(what)); }
        catch (Exception) { /* a diagnostic sink must never take the exchange down with it */ }
        return decision;
    }

    // ---------------------------------------------------------------- display rows

    // Every row the display may draw: this machine's own measurements, each projected through the cached
    // rows, plus a row for a player this machine never measured but a host described. The capture store is
    // read, never written, so a later measurement can never be added on top of a figure that came off the
    // wire, and a figure that came off the wire can never be added into the ledger.
    internal List<CombatPlayer> Project(IReadOnlyList<CombatPlayer> measured, ulong local)
    {
        var rows = new List<CombatPlayer>();
        for (int index = 0; measured != null && index < measured.Count; index++)
        {
            CombatPlayer display = CombatMerge.Project(measured[index]);
            if (display != null) rows.Add(display);
        }
        foreach (ulong owner in Cache.Owners)
        {
            if (Contains(rows, owner)) continue;
            rows.Add(new CombatPlayer(owner) { DisplaySlot = int.MaxValue, Name = string.Empty, StatisticsAvailable = true });
        }
        foreach (CombatPlayer row in rows)
        {
            if (!Cache.TryGet(row.Lookup, out SyncSnapshot snapshot)) continue;
            CombatMerge.ApplyRemote(row, snapshot, _primarySlot, _specialSlot, _classSlot, _meleeSlot, local);
        }
        return rows;
    }

    private static bool Contains(List<CombatPlayer> rows, ulong lookup)
    {
        for (int index = 0; index < rows.Count; index++) if (rows[index].Lookup == lookup) return true;
        return false;
    }
}
