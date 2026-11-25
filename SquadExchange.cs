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

// 1.19.34: `SupplyKinds` carries the split of a row's own pack use, which travels beside the row rather than inside it so
// that a peer on the v8 protocol keeps reading every row this build sends.
public enum SquadSendKind { Hello, Snapshot, SupplyKinds }

// One packet the exchange wants put on the wire this tick. The exchange never touches SNet itself, which
// is what lets the whole exchange be driven in memory by the headless suite.
public readonly struct SquadSend
{
    public readonly SquadSendKind Kind;
    public readonly ulong Target;
    public readonly SyncSnapshot Snapshot;
    // 1.19.34: set for `SupplyKinds` only, where the snapshot is default.
    public readonly SyncProtocol.SupplyKinds Kinds;
    public readonly uint Generation;

    public SquadSend(SquadSendKind kind, ulong target, in SyncSnapshot snapshot, uint generation)
    {
        Kind = kind; Target = target; Snapshot = snapshot; Kinds = default; Generation = generation;
    }

    private SquadSend(SquadSendKind kind, ulong target, in SyncSnapshot snapshot, in SyncProtocol.SupplyKinds kinds, uint generation)
    {
        Kind = kind; Target = target; Snapshot = snapshot; Kinds = kinds; Generation = generation;
    }

    // 1.19.34: the kinds packet. A factory rather than an overload, so a caller that passes `default` for a body keeps
    // resolving to the snapshot constructor instead of becoming ambiguous.
    public static SquadSend OfSupplyKinds(ulong target, in SyncProtocol.SupplyKinds kinds, uint generation)
        => new SquadSend(SquadSendKind.SupplyKinds, target, default, kinds, generation);
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
// Nothing is put on the wire for a peer this machine has no evidence about - a hello included, because a
// hello is already a packet only this build can read, and because a machine that runs no mod is *not* silent
// about receiving one: GTFO-API answers every unregistered event with one `[Error]` line in that machine's own
// log, and a recorded run beside such a machine collected 10225 of them for this event. The evidence is
// `SquadCapability`: the operator named that player's lookup in the configuration, the peer itself sent this
// machine a packet of this build's protocol - which nothing but this build can produce, and which is therefore
// the discovery route that has to send nothing to find out - or, when this machine shares with its slot list,
// the `ProbeBudget` greetings an unproven peer is allowed and not one packet more. A bot, a stranger in the
// lobby and a peer on another protocol version are all met with silence.
//
// What the exchange does with a peer that *is* speaking to it:
//
//   * a host that runs this mod reads every player's fire, damage and backpack itself, so it sends one row
//     per slotted player to every peer that has proved it can read one - or that the operator named. Its rows
//     are authoritative for damage.
//   * a client sends its own row - and only its own row - to the same peers. That row is authoritative for
//     its aim, its supplies and its scans, which no other machine can count. The bounded probe state carries no
//     row at all: a peer that has not proved it runs this build has no handler for one.
//   * a lobby whose host runs no mod of its own exchanges between the clients only when one of them has
//     the other in its configuration or has already been spoken to by it. The vanilla host is probed a bounded
//     number of times and then left alone; it is never sent a row.
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
    // The gate on this machine's own transmissions: a peer with no evidence here is sent nothing at all.
    private readonly SquadCapability Discovery = new SquadCapability();
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
    // How many human peers the last tick sent nothing to, so the diagnostic says it once per change
    // rather than once per frame.
    private int _withheldPeers = -1;

    // 1.19.43: one late greeting for this expedition's host, from a client that has already spent its whole bounded
    // probe on it.
    //
    // The initial burst is spent inside the first seconds of an expedition, which is exactly when the host may still be
    // loading this build, and after that the loop below is silent for the rest of the run - a recorded session ended
    // with three greetings per peer and not one packet back, which is indistinguishable from a host that runs nothing.
    // This is the recovery, and it is deliberately narrow: a client only (a host owes nobody a greeting), this
    // expedition's host only (`SquadExchange._master` as the roster named it, never the placeholder a client without a
    // named host carries), asked for once and late, one packet per expedition (`SquadCapability.LateProbeBudget`),
    // handed over by the same wire gate as every other greeting, and never for data. The total a peer can ever cost is
    // therefore `ProbeBudget + LateProbeBudget` packets, and a host that runs no panel writes that many error lines at
    // most - not one per frame and not one per level.
    public const float LateHelloSeconds = 45f;
    private float _lateHelloDue = float.NaN;

    // A hello that arrived before the roster knew the player who sent it.
    //
    // The receive queue is drained on the main thread at the start of a tick and the roster is rebuilt from the
    // game's own slot list later in the same tick, so a player who joins and greets in one frame is judged
    // against the roster of the previous frame and refused as "not in this squad's roster" - the peer answers
    // on its own two-second cadence instead, and the row the pair would have exchanged in between is lost. The
    // packet is kept here for a bounded moment and re-judged by the caller right after it refreshes the roster:
    // the same policy, the same refusal for a sender that is still not in the squad, and no packet accepted
    // outside the roster this machine trusts.
    private struct DeferredHello { public ulong Sender; public uint Generation; public float ReceivedAt; }
    public const int MaxDeferredHellos = 8;
    // How long one of those hellos stays worth re-judging. It is the same order as the peer's own retry cadence,
    // so a hello that is kept longer would only duplicate what the peer already sends again.
    public const float DeferredHelloSeconds = 3f;
    private readonly List<DeferredHello> _deferred = new List<DeferredHello>();

    // How many hellos are waiting to be re-judged. Read by the headless suite and by the diagnostic line.
    public int DeferredHellos { get { return _deferred.Count; } }

    // Keeps one hello for the bounded retry above. A repeated hello from the same peer for the same run replaces
    // the timestamp instead of adding an entry, so a peer that keeps greeting cannot grow the list.
    internal void DeferHello(ulong sender, uint generation, float now)
    {
        if (sender == 0UL || sender == _local) return;
        for (int index = 0; index < _deferred.Count; index++)
        {
            if (_deferred[index].Sender != sender || _deferred[index].Generation != generation) continue;
            DeferredHello repeated = _deferred[index];
            repeated.ReceivedAt = now;
            _deferred[index] = repeated;
            return;
        }
        if (_deferred.Count >= MaxDeferredHellos) _deferred.RemoveAt(0);
        _deferred.Add(new DeferredHello { Sender = sender, Generation = generation, ReceivedAt = now });
    }

    // The oldest hello still worth re-judging, removed from the list. False once nothing is left or everything
    // left is older than `DeferredHelloSeconds`.
    internal bool TryTakeDeferredHello(float now, out ulong sender, out uint generation)
    {
        sender = 0UL; generation = 0U;
        while (_deferred.Count > 0)
        {
            DeferredHello next = _deferred[0];
            _deferred.RemoveAt(0);
            if (now - next.ReceivedAt > DeferredHelloSeconds) continue;
            sender = next.Sender; generation = next.Generation;
            return true;
        }
        return false;
    }

    // Every accepted and refused packet is published here. The runtime points it at the BepInEx logger; the
    // headless suite records it and asserts on the exact fields.
    public Action<string> Report = _ => { };

    public uint Generation => _generation;
    public int CachedRows => Cache.Count;
    public IReadOnlyList<SquadPeer> Roster => Peers;
    public double? SquadFromHost => _squadTotal;

    // The evidence this machine holds about who runs this build. Read-only for the display and for the
    // transport's own belt-and-braces gate; the exchange is what feeds it.
    public SquadCapability Capability => Discovery;

    // The configured list of players known to run this build, re-applied whenever the file is re-read.
    // It is local configuration: a new lobby clears what was learned in the old one, not this.
    internal void SetAllowlist(IEnumerable<ulong> lookups) => Discovery.SetAllowlist(lookups);

    // How many human peers this machine is deliberately not speaking to, for the diagnostic line.
    public int WithheldPeers { get { return _withheldPeers; } }

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
        // A peer that never answered is probed again: the machine on the other end may only now have finished
        // loading this build, and `ProbeBudget` packets per expedition is still bounded. The one late greeting goes with
        // the run it was asked in: the next expedition earns its own clock and its own reservation.
        Discovery.ResetProbes();
        _lateHelloDue = float.NaN;
        // A hello that was waiting to be re-judged describes the run that just ended: the roster is rebuilt for the
        // new one, so a packet kept from before it must never be re-judged against that new roster - the peer may not
        // even be in this expedition, and a hello is answered only for the session that received it.
        _deferred.Clear();
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
        // Who proved it runs this build belongs to the session that just ended. The configured list stays:
        // it names machines, not a lobby.
        Discovery.ResetEvidence();
        _withheldPeers = -1;
        // The late greeting is the session's as well: a new lobby is a new host to reach.
        _lateHelloDue = float.NaN;
        // A hello waiting for the roster is about the lobby that just ended, so it goes with it.
        _deferred.Clear();
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
            // The seat's next occupant is a stranger again: it proves itself or it is not spoken to.
            Discovery.Forget(lookup);
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

    // The packets this machine wants sent right now: a greeting to every peer it may greet and has not heard
    // from, and rows only to the peers that proved they can read them. A peer this machine holds no evidence
    // about appears in neither list - not even for a hello, because a hello is already a packet only this build
    // can read and a machine that runs no mod answers each one with an `[Error]` line in its own log. An
    // unproven peer that this machine shares with gets exactly its bounded probe: `ProbeBudget` greetings, no
    // rows, and then silence. The greeting cadence is only asked about peers that pass the gate, so the retry
    // budget is spent on machines that can answer rather than on a stranger.
    internal List<SquadSend> Tick(float now, IReadOnlyList<CombatPlayer> measured)
    {
        var sends = new List<SquadSend>();
        int withheld = 0, probing = 0;
        foreach (SquadPeer peer in Peers)
        {
            if (!peer.IsHuman || peer.IsLocal) continue;
            SquadEvidence evidence = Discovery.EvidenceOf(peer.Lookup);
            if (evidence == SquadEvidence.Nothing) { withheld++; continue; }
            if (evidence == SquadEvidence.Probe) probing++;
            if (!Handshake.ShouldGreet(peer.Lookup, now)) continue;
            // A greeting for an unproven peer costs one of the few packets that peer will ever be handed, and the
            // cost is charged *here*, where the packet is asked for: this is what bounds the queue itself, so a
            // decider that never reaches the transport still cannot probe a peer forever. The transport's gate
            // (`SquadCapability.MayPutOnWire`) then hands the reserved packet over - a gate that asked the
            // evidence alone refused exactly the greeting that had spent the last reservation, which is how an
            // unproven peer came to be handed two of the three. The line is written once, on the packet that
            // spends the last one, so a level's log carries one line per silent peer instead of one per frame.
            if (evidence == SquadEvidence.Probe && Discovery.NoteProbeSent(peer.Lookup))
                Report("[DIAG] squad sync discovery: peer=" + peer.Lookup + " has now been sent all "
                    + SquadCapability.ProbeBudget + " greeting packet(s) this machine sends it without proof; it is"
                    + " sent nothing more - no greeting and no row - until it answers, a new expedition starts or its"
                    + " seat changes hands. Each of those packets is one error line in that machine's own GTFO-API log"
                    + " when it runs no mod, which is why the probe is bounded.");
            sends.Add(new SquadSend(SquadSendKind.Hello, peer.Lookup, default(SyncSnapshot), _generation));
        }
        if (withheld != _withheldPeers)
        {
            _withheldPeers = withheld;
            // Written only when the roster holds a human peer: "0 of 0 human peer(s)" described nobody and read
            // like a fault of this machine's own, which is exactly the misleading line this replaces.
            if (HumanPeers > 0)
                Report("[DIAG] squad sync discovery: " + withheld + " of " + HumanPeers
                    + " human peer(s) are being sent nothing at all - no hello, no row - while " + probing
                    + " are inside the bounded probe of " + SquadCapability.ProbeBudget + " greeting packet(s) each and"
                    + " receive no row until they answer. A peer is spoken to when its lookup is named in"
                    + " AllowlistedPeers, or after it has itself sent this machine a packet of this build's protocol; a"
                    + " machine that runs no mod never produces one, and answers every packet it does receive with one"
                    + " GTFO-API error line in its own log, which is why the probe is bounded rather than open.");
        }
        // 1.19.43: the one late greeting, for this expedition's host only, from a client only.
        //
        // `EvidenceOf` answers `Nothing` for a peer whose bounded probe is spent, and that test is asked *before*
        // `ShouldGreet` in the loop above - deliberately, because the budget is what keeps an unmodded machine's log
        // short. The recovery therefore cannot ride that loop: it is asked here, once, against this exchange's own
        // knowledge of who the host is and of how long the expedition has run. The physical side is unchanged - the
        // packet is handed to the transport only through `SquadCapability.MayPutOnWire`, which counts it against
        // `LateProbeBudget` and pays for exactly one packet - so what a peer can cost stays bounded, and a send that
        // never reaches the transport spends the reservation rather than buying another attempt.
        if (LateHelloDue(now) && ClientHost(out ulong lateHost) && !Handshake.IsAnswered(lateHost)
            && Discovery.ProbesSent(lateHost) >= SquadCapability.ProbeBudget && Discovery.NoteLateProbe(lateHost))
        {
            sends.Add(new SquadSend(SquadSendKind.Hello, lateHost, default(SyncSnapshot), _generation));
            Report("[DIAG] squad sync discovery: host=" + lateHost + " has still not answered although this machine's"
                + " bounded probe for it was spent at the start of this expedition, so one late greeting is sent to it"
                + " now - the " + SquadCapability.LateProbeBudget + " late greeting a host costs per expedition. It is one"
                + " packet on this build's own channel rotation, it carries no row, and nothing else changes: an unproven"
                + " peer is still sent no data, the total a host can ever cost stays "
                + (SquadCapability.ProbeBudget + SquadCapability.LateProbeBudget) + " packets, and an answer - not this"
                + " greeting - is what makes it a peer this machine can read. This machine's own log can say only that no"
                + " usable host data has arrived; it cannot say which build the host runs.");
        }
        if (now < _nextSnapshot) return sends;
        _nextSnapshot = now + SnapshotSeconds;
        CombatPlayer own = Find(measured, _local);
        foreach (SquadPeer peer in Peers)
        {
            if (!peer.IsHuman || peer.IsLocal) continue;
            // Rows go only to a machine that has already said it can read this protocol: proof from the peer
            // itself, or the operator's own list. The bounded probe never carries a row - an unproven peer has no
            // handler for one. The receiver still decides what it will take (the sender must be in its roster, and
            // a row about somebody else is only taken from its host).
            if (!CarriesRows(Discovery.EvidenceOf(peer.Lookup))) continue;
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
            // 1.19.34: the split of this row's own pack use, beside it. Only the machine that owns the backpack can say
            // which kind a pack was, so this is the one packet a peer may send for its own row and no other.
            if (SyncProtocol.TryCaptureSupplyKinds(own, out SyncProtocol.SupplyKinds kinds))
                sends.Add(SquadSend.OfSupplyKinds(peer.Lookup, kinds, _generation));
        }
        return sends;
    }

    // Whether a peer in this state may be handed a row. Only a peer that proved it runs this build, or one the
    // operator named, has a handler for these bytes; the bounded probe state has none by definition.
    private static bool CarriesRows(SquadEvidence evidence)
        => evidence == SquadEvidence.Proven || evidence == SquadEvidence.Allowlisted;

    // Whether the one late greeting is due. The clock starts on the first tick of an expedition and the greeting is
    // asked for `LateHelloSeconds` later, far enough after the entry burst that it cannot be spent while the host is
    // still loading the level. Once the reservation exists (`SquadCapability.NoteLateProbe`) this answers true on every
    // later tick, and the reservation - not this clock - is what keeps it to one packet: a tick that arrives early costs
    // nothing and a tick that arrives late cannot lose the greeting.
    private bool LateHelloDue(float now)
    {
        if (!float.IsFinite(now)) return false;
        if (float.IsNaN(_lateHelloDue)) _lateHelloDue = now + LateHelloSeconds;
        return now >= _lateHelloDue;
    }

    // This expedition's host, when this machine is a client and that host is a real peer the roster named. The
    // placeholder master a client without a named host carries (`1UL`, "the vanilla host") is not a peer here and is
    // never greeted by this path: nothing is known about it, which is exactly what the roster said.
    private bool ClientHost(out ulong host)
    {
        host = 0UL;
        if (_localIsMaster || _master == 0UL || _master == _local) return false;
        if (!TryPeer(_master, out SquadPeer peer)) return false;
        if (!peer.IsHuman || peer.IsLocal) return false;
        host = _master;
        return true;
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
            // 1.19.34: the split of the row's own pack use, beside the row. 1.19.35: this is sent for *every* row the host forwards,
            // not only its own. The host counts each slotted player's packs itself - it is the machine that applies the give and the
            // heal to that player's receiver - so `TryCaptureSupplyKinds` answers for their rows as well, and a client that is not
            // connected to that player has no other way to show their kinds (review C2). The block keeps the described player as its
            // owner; the receiver accepts it from the host only for a player on its own roster, keeps the owner's own block as the
            // authority if that ever arrives, and applies it only to the exact pair the row already holds.
            if (SyncProtocol.TryCaptureSupplyKinds(row, out SyncProtocol.SupplyKinds kinds))
                sends.Add(SquadSend.OfSupplyKinds(target, kinds, _generation));
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
    internal SquadDecision OnHello(ulong sender, uint generation, float now, out bool answer, bool deferrable = true)
    {
        answer = false;
        if (sender == 0UL || sender == _local)
            return Refuse("hello", "a hello from nobody, or an echo of this machine's own packet", sender, generation);
        if (!TryPeer(sender, out _))
        {
            // Arriving a frame before the roster knew this player is not the same as not being in the squad: the
            // packet is kept for the bounded retry the caller runs right after it refreshes the roster, and it is
            // re-judged there by this very method. The refusal stands for now, and a sender that is still outside
            // the roster when it is re-judged is refused again and dropped.
            if (deferrable) DeferHello(sender, generation, now);
            return Refuse("hello", "the sender is not in this squad's roster", sender, generation);
        }
        // This packet is itself the evidence: only a machine running this build can produce one, and nothing
        // was sent to this peer to find that out. It is recorded before the run counter is judged, because a
        // stale generation still proves what the sender is, and it is what lets this machine answer at all.
        if (Discovery.NoteInbound(sender))
            Report("[DIAG] squad sync discovery: peer=" + sender + " proved it runs this build by sending this machine a hello;"
                + " it may now be answered and sent to, without this machine having probed for it.");
        Handshake.Heard(sender, now);
        if (!Handshake.AcceptsFrom(sender, generation, now, out bool recovered))
            return Refuse("hello", Handshake.DescribeStale(sender), sender, generation);
        // 1.19.35: one place does the cleanup a restart needs, because three packets can consume that recovery and all three must
        // leave the cache in the same state (`ForgetRestartedRun`).
        if (recovered) ForgetRestartedRun(sender);
        answer = Handshake.ShouldAnswer(sender, now);
        return Accept("hello", recovered
            ? "the sender restarted, its stale cached row was cleared, and its run counter is adopted"
            : "the peer speaks this protocol and is answered", sender, generation);
    }

    // 1.19.35: a peer restarted, so the run counter this machine held for it began again and every figure the *previous* run left
    // behind has to go with the counter. The recovery itself is granted once by `Handshake.AcceptsFrom`, and three packet kinds can be
    // the one that consumes it - a hello, a row, and the split packet. A hello did this inline and a row does it inside
    // `SnapshotCache.Store`; the split packet did not, and that is the defect review C1 found: the counter was adopted while the old
    // rows stayed cached, so the restarted peer's rows were then refused as "older than the one already held" for as long as its
    // counter stayed below the old one - a whole run of rows from that player, silenced by one split packet.
    //
    // The host's restart invalidates every row (they were all described by it); any other peer's restart invalidates only its own.
    private void ForgetRestartedRun(ulong sender)
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
        Report("[DIAG] squad sync cache cleared before accepting run recovery: peer=" + sender
            + "; hostRestart=" + remoteHostRestarted.ToString().ToLowerInvariant() + "; affectedRow="
            + (remoteHostRestarted ? "all host snapshots" : "the restarted peer") + ".");
    }

    // A row from a peer. Who may speak for whom is decided from the roster and nothing else.
    internal SquadDecision OnSnapshot(ulong sender, uint generation, in SyncSnapshot snapshot, float now)
    {
        if (sender == 0UL || sender == _local)
            return Refuse("snapshot", "a row from nobody, or an echo of this machine's own packet", sender, generation);
        if (!TryPeer(sender, out SquadPeer peer))
            return Refuse("snapshot", "the sender is not in this squad's roster", sender, generation);
        // Same evidence as a hello, and recorded before the body is judged: a row that speaks for the wrong
        // run, or for a player this machine cannot accept, still came from a machine running this build.
        if (Discovery.NoteInbound(sender))
            Report("[DIAG] squad sync discovery: peer=" + sender + " proved it runs this build by sending this machine a row;"
                + " it may now be answered and sent to, without this machine having probed for it.");
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

    // The peer answered with a protocol this build does not speak. It runs a build, so it is not a
    // stranger - but nothing this machine sends can be read by it, so every packet to it stops. Without
    // this, a mixed lobby kept putting both a hello and a row per second on the wire for a machine that
    // had already said it could not decode either.
    // 1.19.34: one player's split of their own pack use, sent beside that player's row. It is accepted from the machine that owns
    // the row, and - 1.19.35 - from the host relaying the split it counted as that player's receiver, which is the only way a client
    // that is not connected to that player can show their kinds (`HostRows` sends it, and a block about another player is only taken
    // from the host, exactly like a row about another player). Only this run is accepted, it is kept against the described owner,
    // and it is applied only where it describes the pair the row already holds.
    //
    // 1.19.35 (review C1): this is a third way into the handshake, and a recovery consumed here has to clean up exactly like a
    // recovery consumed by a hello. `AcceptsFrom` adopts the restarted peer's counter once, and while the old run's rows stayed in
    // the cache, every new row from that peer was refused as "older than the one already held" for as long as its counter stayed
    // below the old one - a split packet that happened to be the third stale packet could therefore silence the peer for a whole run.
    internal SquadDecision OnSupplyKinds(ulong sender, uint generation, in SyncProtocol.SupplyKinds kinds, float now)
    {
        if (sender == 0UL || sender == _local)
            return Refuse("supply kinds", "a split from nobody, or an echo of this machine's own packet", sender, generation);
        if (!TryPeer(sender, out SquadPeer peer))
            return Refuse("supply kinds", "the sender is not in this squad's roster", sender, generation);
        // A block about this machine's own row is refused: this machine counts its own packs, and `ApplySupplyKinds` would never
        // accept one for it anyway - refusing at the door keeps the diagnostic honest instead of reporting a cache write that does
        // nothing.
        if (kinds.Owner == _local)
            return Refuse("supply kinds", "the split describes this machine's own row, which this machine measures itself", sender, generation);
        bool fromOwner = kinds.Owner == sender;
        // Who may describe whose packs: the owner, or the machine this roster names as the host - the same authority rule a row
        // about another player already goes through (`OnSnapshot`). A non-host peer can never speak for somebody else.
        // The same predicate `OnSnapshot` uses for this sender's rows, and it is passed on with the block: the row a split
        // describes is the row this sender's own packets were written into, which is the host slot when the sender *is* the host
        // (1.19.35 fix - an owner that is also the host was filling in a peer slot that never held its row).
        bool fromHost = peer.IsMaster || (sender == _master && sender != _local);
        if (!fromOwner && !fromHost)
            return Refuse("supply kinds", "a split of another player's packs from a peer that does not own them", sender, generation);
        if (!TryPeer(kinds.Owner, out _))
            return Refuse("supply kinds", "the split describes a player outside the current squad roster", sender, generation);
        if (!kinds.IsUsable)
            return Refuse("supply kinds", "the split does not add up to the total it claims", sender, generation);
        if (Discovery.NoteInbound(sender))
            Report("[DIAG] squad sync discovery: peer=" + sender + " proved it runs this build by sending this machine a split of a pack use.");
        Handshake.Heard(sender, now);
        if (!Handshake.AcceptsFrom(sender, generation, now, out bool recovered))
            return Refuse("supply kinds", Handshake.DescribeStale(sender), sender, generation);
        if (recovered) ForgetRestartedRun(sender);
        if (!Cache.StoreSupplyKinds(kinds.Owner, generation, kinds, recovered, fromOwner, fromHost))
            return Refuse("supply kinds", "a split from an older run than the one already held for that player", sender, generation);
        return Accept("supply kinds", fromOwner
            ? "the split is cached and fills in that row's pack kinds"
            : "the host's split for that player is cached; the player's own block replaces it when it arrives", sender, generation);
    }

    internal void OnPeerIncompatible(ulong sender)
    {
        if (sender == 0UL) return;
        if (!Discovery.NoteIncompatible(sender)) return;
        Report("[DIAG] squad sync discovery: peer=" + sender + " speaks another protocol version, so nothing further is"
            + " sent to it - not a hello and not a row - until it comes back speaking this build's protocol.");
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
    //
    // 1.19.60: a row in the capture store is not by itself a row the public statistics may draw. The store also
    // holds the receiver of this machine's own medkit (`SupplyTracker.CandidateFor`), which is the right place
    // for that credit and the wrong reason to put a player on the board: on the recorded client page two seats
    // that run no mod stood there with a bare row and no figure in it. `SquadRowEligibility` answers that
    // question from evidence alone - this machine's own row, a row this run really received, or a seat this
    // machine is the authority for - and the credit stays in the store either way, so a qualified row that
    // arrives later merges on top of it.
    internal bool RoleIsMaster => _localIsMaster;

    // The two-argument form answers with this exchange's own record of the role, which is what `SetRoster` was last told.
    internal List<CombatPlayer> Project(IReadOnlyList<CombatPlayer> measured, ulong local)
        => Project(measured, local, _localIsMaster);

    // `localIsAuthority` is the live answer to "is this machine the host of this session", passed in by the caller
    // because only the caller can read it (this class is Unity-free). 1.19.60: that answer must not come from the
    // roster alone. `_localIsMaster` is written by `SetRoster`, so a host whose roster read throws for a few frames -
    // the exact frames in which the network is closed - would be read as a client and would drop every seat it
    // measured itself, which is "the host loses its squad because the network went away". The production call site
    // therefore asks the live fact and falls back to this exchange's own record, and the rule below is unchanged:
    // a seat is drawn when this machine measured it as the authority, or when this run received its row, or when it
    // is this machine's own row.
    //
    // `local` is the caller's reading of this machine's own lookup. A reading that came back as 0 - `SNet.LocalPlayer`
    // throwing for a frame - must not make this machine's own row look like somebody else's, so `_local` (the lookup
    // this exchange was configured with, the same one `Find(measured, _local)` uses) is the fallback for that and
    // nothing else: no row is admitted by it that the rule would otherwise have refused.
    internal List<CombatPlayer> Project(IReadOnlyList<CombatPlayer> measured, ulong local, bool localIsAuthority)
    {
        ulong self = local != 0UL ? local : _local;
        var rows = new List<CombatPlayer>();
        for (int index = 0; measured != null && index < measured.Count; index++)
        {
            CombatPlayer capture = measured[index];
            if (capture == null) continue;
            if (!SquadRowEligibility.Draws(capture.Lookup == self, Cache.Has(capture.Lookup), localIsAuthority)) continue;
            CombatPlayer display = CombatMerge.Project(capture);
            if (display != null) rows.Add(display);
        }
        foreach (ulong owner in Cache.Owners)
        {
            if (Contains(rows, owner)) continue;
            rows.Add(new CombatPlayer(owner) { DisplaySlot = int.MaxValue, Name = string.Empty, StatisticsAvailable = true });
        }
        foreach (CombatPlayer row in rows)
        {
            if (Cache.TryGet(row.Lookup, out SyncSnapshot snapshot))
                CombatMerge.ApplyRemote(row, snapshot, _primarySlot, _specialSlot, _classSlot, _meleeSlot, self);
            // 1.19.34: the split sent beside that row. It fills in the kinds only where the pair the row now holds is exactly the
            // pair the split describes, so a row is never assembled out of two machines' counts; a row whose pair came from
            // somewhere else keeps its kinds unknown, which is what the cell prints as `?`. 1.19.35: the cache also checks that the
            // block describes the *run* that row came from, so a split left over from the previous expedition cannot attach its
            // kinds to a new row that happens to hold the same total/ammo pair (review C4).
            Cache.TryApplySupplyKinds(row.Lookup, row.Impact, row.Lookup == self);
        }
        return rows;
    }
    private static bool Contains(List<CombatPlayer> rows, ulong lookup)
    {
        for (int index = 0; index < rows.Count; index++) if (rows[index].Lookup == lookup) return true;
        return false;
    }
}

// ---------------------------------------------------------------------------------------------------
// 1.19.60: which rows the public statistics may draw
// ---------------------------------------------------------------------------------------------------
//
// The recorded 2026-11-24 client session (a squad whose host runs no mod) put two bare rows on the level HUD
// halfway through the run: a weapon column of dashes, a kill column of zeros, no name, and - in the archive -
// one and five medical packs. They were the two seats that run no mod, and the only thing that had ever opened
// a row for them was this machine's own medkit: `SupplyTracker.CandidateFor` writes the receiver's credit into
// the capture store on purpose (a client's real application to a remote teammate must reach that teammate's
// row), and every row in that store was projected onto the board.
//
// The rule below is about evidence and nothing else. A row may be drawn when
//
//   own          this machine measures it itself (`lookup == local`), always;
//   described    this run received a row for it through the exchange - the receiver-side gate already refused
//                everything that is not the player's own row or the authoritative host's description of a
//                slotted player, so a cached row *is* qualified evidence, and a host's description of a
//                teammate who runs no mod, or of a bot, arrives this way;
//   authority    this machine is the host of this session and measured the seat itself, which is how a bot's
//                row and a slotted player's row exist on the authority at all.
//
// and *not* when the seat is still in the roster (a seat that left keeps no row here; a seat that stayed and
// was never measured gets none either), what a handshake answered, whether this machine happened to apply one
// medkit or five to that seat, whether every figure in the row is zero, or whether the row already has a name.
// Those are facts about this machine's own bookkeeping, never about the player's statistics, and a row admitted
// or refused by one of them is a row the player cannot account for.
//
// The receiver's raw supply ledger is untouched: the credit stays in the capture store exactly where it was
// written, so a qualified row for that seat that arrives later merges on top of it, a real zero on a drawn row
// stays a zero, and an unknown stays unknown. Nothing here deletes a figure or turns absence into a zero.
public static class SquadRowEligibility
{
    public static bool Draws(bool ownRow, bool describedByThisRun, bool measuredByThisAuthority)
        => ownRow || describedByThisRun || measuredByThisAuthority;
}
