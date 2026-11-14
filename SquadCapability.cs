using System;
using System.Collections.Generic;

namespace HawChow.GTFOCombatPanel;

// Who this machine is allowed to put a packet of its own protocol on the wire for.
//
// The squad exchange rides a transport the rest of the GTFO modding scene already uses for the same
// job: GTFO-API's free-sized network event (`SquadNet`). That event carries a fixed replicator key, a
// long ASCII magic and a version signature - and a machine that does not run this build is *not*
// silent about receiving one. GTFO-API's receive path looks the event name up in its own registry,
// finds nothing, and writes one line into that machine's BepInEx log for every packet it is handed:
//
//   [Error : GTFO-API] [NetworkAPI]: <id> invoked an event <name> which isn't registered
//
// A recorded run beside a machine that runs no mod produced 13051 log lines, 10249 of them that
// error, and 10225 of those for this event alone. Broadcasting is therefore neither free nor
// invisible: every packet this machine puts on the wire for a peer that runs no mod costs that peer
// one error line, for as long as this machine keeps sending. The rule below is written around that
// fact, not around the older belief that such a machine "silently drops" the bytes.
//
// What that leaves is a bounded probe rather than an open channel. A peer is spoken to for one of
// three reasons, strongest first:
//
//   * Proven: the peer sent this machine a packet of this build's own protocol. It is the only signal
//     that proves the *other* side runs this build, it is read and never asked for, and once given it
//     replaces everything below: such a peer keeps the greeting cadence and the rows.
//   * Allowlisted: the operator named this player's lookup in this machine's configuration. It is the
//     only route that can open a conversation between two machines that have never met while probing
//     is switched off, and it is checkable by hand.
//   * Probe: this machine shares with every human in its slot list, which is the `ShareWithEveryPlayer`
//     default. Such a peer is sent `ProbeBudget` greeting packets over the whole session and not one
//     byte more: no row ever travels in this state, and once the budget is spent the peer is met with
//     silence until it answers, a new expedition starts or the seat changes hands.
//
// One answer is silence in every case: a lookup of zero, a peer that answered with a protocol this
// build does not speak, and a probe target whose budget is spent.
//
// Nothing in this class touches the network, Unity, SNet or the configuration file, so every rule
// below is asserted headlessly.
public enum SquadEvidence
{
    // Nothing is known about this peer, or its probe budget is spent: it is never spoken to again.
    Nothing = 0,
    // Named in this machine's own configuration as a machine that runs this build.
    Allowlisted = 1,
    // Proved it by sending this machine a packet of this build's protocol, unprompted.
    Proven = 2,
    // This machine shares with the whole slot list and this peer is still inside its probe budget.
    Probe = 3
}


public sealed class SquadCapability
{
    // A squad is four seats; the ceiling leaves room for a list that still names old squadmates.
    public const int MaxAllowlistEntries = 64;
    // A 64-bit lookup is at most 20 decimal digits; anything longer cannot be one.
    public const int MaxLookupDigits = 20;

    private readonly Dictionary<ulong, SquadEvidence> _evidence = new Dictionary<ulong, SquadEvidence>();
    // How many greeting packets an unproven peer that this machine shares with may cost, for the whole
    // session. Three is enough for the ordinary case - a peer whose plugin is still loading when the
    // first hello arrives - and small enough that a machine which runs no mod pays three error lines
    // instead of the 10225 a recorded run collected from an unbounded broadcast. Public so the
    // headless suite can assert the number rather than a copy of it.
    public const int ProbeBudget = 3;
    // One entry per peer whose bounded probe has started: how many greeting packets this machine has decided
    // to spend on it. This is the *reservation*, written where the decision is made (`SquadExchange.Tick`),
    // which is what keeps a decider that never asks the gate below from probing a peer forever. Session
    // state, not configuration - `Forget` and `ResetEvidence` clear an entry, and `ResetProbes` renews every
    // one of them for a new expedition.
    private readonly Dictionary<ulong, int> _probes = new Dictionary<ulong, int>();
    // Of those reserved greetings, the ones the wire gate has actually handed to the transport.
    //
    // The two counters exist because one had two meanings and that is what this pair replaces: `Tick` wrote
    // the third greeting into `_probes` before the packet reached the transport, so the transport's own gate
    // saw `ProbesSent(peer) == ProbeBudget`, answered `Nothing`, and withheld the third of the three
    // packets - every probe was one packet shorter than the budget said. The reservation is what the
    // exchange bounds itself with; the delivery count is what the gate hands out, and it can never exceed
    // what was reserved, so a queue, a lost packet, a duplicate or a repeated call cannot buy a fourth.
    private readonly Dictionary<ulong, int> _delivered = new Dictionary<ulong, int>();
    private readonly HashSet<ulong> _allowlist = new HashSet<ulong>();
    private readonly HashSet<ulong> _incompatible = new HashSet<ulong>();
    // Whether this machine shares with every human in its slot list. Configuration, not session state,
    // so `ResetEvidence` leaves it standing.
    private bool _broadcast;

    // Whether this machine shares with the human peers in its slot list, which is the `ShareWithEveryPlayer`
    // default. It buys an unproven peer a bounded probe (`ProbeBudget` greetings per session, no rows); it
    // does not buy a channel, because a machine that runs no mod writes one `[Error]` line into its own log
    // for every packet of this event it is handed. Off means the older rule - the configuration list and
    // proof from the peer, and not a single byte for anybody else.
    public bool Broadcast { get { return _broadcast; } }

    public void SetBroadcast(bool broadcast)
    {
        bool changed = _broadcast != broadcast;
        _broadcast = broadcast;
        if (broadcast || !changed) return;
        // Sharing switched off: a peer whose only evidence was the bounded probe is owed nothing, so the
        // greetings still reserved for it go with the switch. A proved or named peer keeps everything: its
        // evidence was never the probe.
        var owed = new List<ulong>();
        foreach (ulong peer in _probes.Keys)
        {
            if (_evidence.TryGetValue(peer, out SquadEvidence evidence) && evidence != SquadEvidence.Nothing) continue;
            if (_allowlist.Contains(peer)) continue;
            owed.Add(peer);
        }
        foreach (ulong peer in owed) { _probes.Remove(peer); _delivered.Remove(peer); }
    }

    // How many greeting packets this machine has *decided* to spend on an unproven peer. The packet that
    // spends the last one has not necessarily reached the transport yet - `ProbesDelivered` says which of
    // them did - so this is the figure the decision is bounded by, not a count of what the peer received.
    public int ProbesSent(ulong peer)
    {
        if (peer == 0UL) return 0;
        return _probes.TryGetValue(peer, out int sent) ? sent : 0;
    }

    // How many of those reserved greeting packets the wire gate has actually handed to the transport.
    public int ProbesDelivered(ulong peer)
    {
        if (peer == 0UL) return 0;
        return _delivered.TryGetValue(peer, out int delivered) ? delivered : 0;
    }

    // One greeting packet is about to be decided for this peer: reserve one of its `ProbeBudget`. Answers
    // true exactly once per peer, on the packet that reserves the last one, so the caller can write the
    // "budget spent, staying quiet until it answers" line once instead of once per frame.
    //
    // This is the only place the budget is charged, and it is charged where the packet is asked for. A
    // message that is decided and then never handed to the transport - a target that cannot be resolved, a
    // transport that is not armed - keeps its reservation rather than getting it back: a lost packet must
    // never buy a peer another probe, and the ceiling this enforces is what the other machine's log pays for.
    public bool NoteProbeSent(ulong peer)
    {
        if (peer == 0UL) return false;
        _probes.TryGetValue(peer, out int sent);
        if (sent >= ProbeBudget) return false;
        sent++;
        _probes[peer] = sent;
        return sent >= ProbeBudget;
    }

    // The gate the transport asks before every packet of this build's own protocol, and the only place a
    // reserved greeting is handed over. `Kind` is what the transport is about to put on the wire, and it is
    // required rather than inferred: the bounded probe is a greeting and nothing else, so the gate has to know
    // whether the packet it is judging is the greeting it may hand over or data it must refuse.
    //
    // The answer is the evidence that allowed the packet (`Nothing` is a refusal), because the transport needs to
    // tell a probe credit from a proved or named peer: a probe credit has to be handed to exactly one transport,
    // while a proved peer's packet may go out on both until its route is known.
    public SquadEvidence MayPutOnWire(ulong peer, SquadPacketKind kind) { return MayPutOnWire(peer, kind, out _); }

    // Proof from the peer and the operator's own list are answered as the evidence answers them, for a row and a
    // pack-kind block as much as for a greeting; everything else has to be a *greeting* the exchange reserved,
    // and is handed over against that reservation.
    //
    // Two rules live here, and both are the repair of a defect the headless suite found:
    //
    //   * the greeting that spends the last reservation is *already* the reason `EvidenceOf` answers `Nothing`
    //     (the budget is reserved out before the packet is handed over), so a gate that asked the evidence alone
    //     refused the third of the three packets and the peer was silently handed two while the exchange had
    //     counted three;
    //   * the reservation is the greeting's, so it pays for a greeting only. A row or a pack-kind block is data
    //     the contract allows solely to a peer that proved it runs this build or one the operator named, and a
    //     gate that only asked "is there a reservation left" would let both through in the window between the
    //     greeting being decided and being sent - the queue happening not to ask for one is not a rule.
    //
    // One credit is one *physical* packet: `ProbeBudget` is what the other machine's log pays for, and it does not
    // matter to that machine whether this build reached it over GTFO-API or over the game's own channel. Which of
    // those two compatible channels a credit buys is the transport's decision, because only the transport knows
    // whether the preferred one is bound at all.
    //
    // Nothing here adds a packet: data for an unproved peer is refused out of hand and spends nothing, and a
    // greeting can still be handed over exactly as often as it was reserved.
    public SquadEvidence MayPutOnWire(ulong peer, SquadPacketKind kind, out SquadEvidence why)
    {
        why = EvidenceOf(peer);
        if (why == SquadEvidence.Proven || why == SquadEvidence.Allowlisted) return why;
        // The bounded probe is a greeting. A row, a pack-kind block, or anything else this build may add later is
        // not paid for by it.
        if (kind != SquadPacketKind.Hello) return SquadEvidence.Nothing;
        // Reserved greetings are only owed while this machine still shares with its slot list, and never to a
        // peer that answered with a protocol this build does not speak.
        if (peer == 0UL || !_broadcast || _incompatible.Contains(peer)) return SquadEvidence.Nothing;
        if (ProbesDelivered(peer) >= ProbesSent(peer)) return SquadEvidence.Nothing;
        _delivered[peer] = ProbesDelivered(peer) + 1;
        why = SquadEvidence.Probe;
        return SquadEvidence.Probe;
    }

    // A new expedition: a peer that has still not answered gets its bounded probe again, because the
    // machine on the other end may only now have finished loading this build.
    public void ResetProbes()
    {
        _probes.Clear();
        _delivered.Clear();
    }

    public int Allowlisted { get { return _allowlist.Count; } }

    public int Proven
    {
        get
        {
            int count = 0;
            foreach (SquadEvidence evidence in _evidence.Values) if (evidence != SquadEvidence.Nothing) count++;
            return count;
        }
    }

    // The configured list, replaced wholesale whenever the configuration is re-read. It is local
    // configuration and not session state, so `ResetEvidence` leaves it standing while a new lobby
    // clears everything learned inside the old one.
    public void SetAllowlist(IEnumerable<ulong> lookups)
    {
        _allowlist.Clear();
        if (lookups == null) return;
        foreach (ulong lookup in lookups)
        {
            if (lookup == 0UL) continue;
            if (_allowlist.Count >= MaxAllowlistEntries) break;
            _allowlist.Add(lookup);
        }
    }

    // "123, 456" -> the two lookups. Anything that is not a plain
    // unsigned decimal number - a name, a sign, an empty slot, a value too large for 64 bits - is
    // dropped rather than guessed at, because a wrong lookup is a packet sent to the wrong machine.
    public static int ParseAllowlist(string text, List<ulong> into)
    {
        if (into == null) return 0;
        into.Clear();
        if (string.IsNullOrEmpty(text)) return 0;
        int accepted = 0;
        int start = 0;
        for (int index = 0; index <= text.Length; index++)
        {
            char separator = index == text.Length ? ',' : text[index];
            if (separator != ',' && separator != ';' && !char.IsWhiteSpace(separator)) continue;
            string token = text.Substring(start, index - start);
            start = index + 1;
            if (token.Length == 0) continue;
            if (!TryParseLookup(token, out ulong lookup)) continue;
            if (Contains(into, lookup)) continue;
            if (into.Count >= MaxAllowlistEntries) break;
            into.Add(lookup);
            accepted++;
        }
        return accepted;
    }

    public static bool TryParseLookup(string text, out ulong lookup)
    {
        lookup = 0UL;
        if (string.IsNullOrEmpty(text) || text.Length > MaxLookupDigits) return false;
        for (int index = 0; index < text.Length; index++)
        {
            char digit = text[index];
            if (digit < '0' || digit > '9') return false;
        }
        if (!ulong.TryParse(text, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out lookup)) return false;
        return lookup != 0UL;
    }

    // The peer spoke this build's own protocol. Returns true only the first time, so the caller can
    // write one diagnostic line per peer instead of one per packet.
    public bool NoteInbound(ulong peer)
    {
        if (peer == 0UL) return false;
        if (_evidence.TryGetValue(peer, out SquadEvidence current) && current != SquadEvidence.Nothing) return false;
        _evidence[peer] = SquadEvidence.Proven;
        return true;
    }

    // The peer answered with a protocol this build does not speak: it runs a build, but not this one,
    // so there is nothing to say to it and nothing further is sent - even if it is also on the list.
    public bool NoteIncompatible(ulong peer)
    {
        if (peer == 0UL) return false;
        return _incompatible.Add(peer);
    }

    public bool IsIncompatible(ulong peer) { return peer != 0UL && _incompatible.Contains(peer); }

    // The seat changed hands: the next occupant proves itself again rather than inheriting an entry - and
    // it gets its own bounded probe rather than the previous occupant's spent one.
    public void Forget(ulong peer)
    {
        if (peer == 0UL) return;
        _evidence.Remove(peer);
        _incompatible.Remove(peer);
        _probes.Remove(peer);
        _delivered.Remove(peer);
    }
    // A new lobby or session: everything learned inside the old one goes, the probe budgets included. The
    // configured list stays, because it describes machines rather than a session.
    public void ResetEvidence()
    {
        _evidence.Clear();
        _incompatible.Clear();
        _probes.Clear();
        _delivered.Clear();
    }

    public bool MaySend(ulong peer) { return MaySend(peer, out _); }

    // The policy question - "is there evidence that lets this machine speak to that peer at all" - answered
    // without spending anything. Proof from the peer is reported in preference to everything else, then the
    // operator's own note, then the remaining probe budget. The transport does not ask this one: it asks
    // `MayPutOnWire`, which answers the same question and then hands over the greeting it was reserved.
    public bool MaySend(ulong peer, out SquadEvidence why)
    {
        why = EvidenceOf(peer);
        return why != SquadEvidence.Nothing;
    }

    public SquadEvidence EvidenceOf(ulong peer)
    {
        if (peer == 0UL) return SquadEvidence.Nothing;
        if (_incompatible.Contains(peer)) return SquadEvidence.Nothing;
        if (_evidence.TryGetValue(peer, out SquadEvidence evidence) && evidence != SquadEvidence.Nothing)
            return evidence;
        if (_allowlist.Contains(peer)) return SquadEvidence.Allowlisted;
        // The bounded probe: sharing with the slot list buys an unproven peer a few greetings, never a
        // channel. Once the last one is spent this answers `Nothing`, which is what stops the greeting
        // cadence from turning into the unbounded broadcast that cost an unmodded peer 10225 error lines.
        if (_broadcast && ProbesSent(peer) < ProbeBudget) return SquadEvidence.Probe;
        return SquadEvidence.Nothing;
    }

    // For the diagnostic line: why this peer is or is not spoken to.
    public string Describe(ulong peer)
    {
        if (peer == 0UL) return "a lookup of zero names nobody";
        if (_incompatible.Contains(peer)) return "it speaks another protocol version, so nothing is sent to it";
        switch (EvidenceOf(peer))
        {
            case SquadEvidence.Allowlisted: return "it is named in this machine's AllowlistedPeers";
            case SquadEvidence.Proven: return "it proved it runs this build by sending this machine its own packet";
            case SquadEvidence.Probe: return "this machine shares with every player in its slot list, and this peer is"
                + " still inside the bounded probe of " + ProbeBudget + " greeting(s)";
            default: return _broadcast && ProbesSent(peer) >= ProbeBudget
                ? "the bounded probe of " + ProbeBudget + " greeting(s) for this peer is spent, so nothing more is sent"
                    + " until it answers, a new expedition starts or the seat changes hands"
                : "there is no evidence it runs this build, so it is sent nothing";
        }
    }

    private static bool Contains(List<ulong> values, ulong lookup)
    {
        for (int index = 0; index < values.Count; index++) if (values[index] == lookup) return true;
        return false;
    }
}
