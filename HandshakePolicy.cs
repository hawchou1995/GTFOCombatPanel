using System;
using System.Collections.Generic;

namespace HawChow.GTFOCombatPanel;

// The hello/answer bookkeeping of the squad exchange, kept free of Unity and SNet so its cadence, its
// bounded retry, its staleness rules and its recovery are asserted headlessly.
//
// Three faults from the release before this one are answered here, each one a way the pair could go
// permanently silent:
//
//   * the first release remembered a peer the first time it sent a hello and then never spoke to it
//     again, so one hello lost in flight - or sent before the peer's plugin had finished loading -
//     meant the pair exchanged nothing for the rest of the session. Here a peer is retried on a
//     cadence until it answers and a peer that answers and then goes quiet is greeted afresh, so a
//     restart or a hot reload is picked up again within `SilenceSeconds`.
//   * the answer to a hello was sent once. A single lost answer - the case a busy lobby produces all
//     the time - left the asking side unanswered forever. `ShouldAnswer` is a rate limit, not a latch:
//     a repeated hello is answered again once the limit has passed, so the exchange still forms when
//     the first answer never arrives.
//   * the run counter was a one-way latch. A packet carrying an older counter than the one already
//     seen from that peer was refused for the rest of the session, which is the `refused a stale
//     snapshot` line the test log ends on: after it, nothing from that peer was ever accepted again.
//     A peer that keeps insisting on the same older counter is now taken at its word - it restarted
//     or hot reloaded, and its run counter really did begin again - after `StaleRepeats` refused
//     packets, or `StaleGraceSeconds` of refusing them, whichever comes first.
//
// The generation is the sender's run counter. It is bumped when a new expedition starts, so a packet
// still in flight from the previous run does not land in fresh counters - but only for as long as the
// sender is the same continuing session. The moment the sender insists otherwise, it is believed.
public sealed class HandshakePolicy
{
    // One hello every this many seconds until the peer answers.
    public const float RetrySeconds = 2f;
    // And no more than this many at that quick cadence before it drops to the keep-alive cadence.
    public const int MaxAttempts = 5;
    // How often a peer that has still not answered is greeted once the quick burst is spent. The greeting
    // never stops - see ShouldGreet.
    public const float KeepAliveSeconds = 10f;
    // The shortest gap between two answers to the same peer. It is what stops two peers that both
    // greet from answering each other in a loop, while still letting a lost answer be re-sent.
    public const float AnswerInterval = 1f;
    // A peer this quiet - although it had answered - is treated as new again: its restart, its reload
    // or its rejoin is greeted afresh instead of being written off for the session.
    public const float SilenceSeconds = 12f;
    // How many refused packets from one peer, or how long refusing them, before the older counter it
    // keeps sending is adopted as the peer's real one.
    public const int StaleRepeats = 3;
    public const float StaleGraceSeconds = 3f;

    private sealed class Peer
    {
        public int Attempts;
        public float NextTry;
        public float LastHeard = float.NegativeInfinity;
        public float LastAnswer = float.NegativeInfinity;
        public bool Answered;
        public bool EverAnswered;
        public uint Generation;
        public uint StaleGeneration;
        public int StaleCount;
        public float StaleSince;
    }

    private readonly Dictionary<ulong, Peer> _peers = new Dictionary<ulong, Peer>();

    public int PeerCount { get { return _peers.Count; } }

    public int AnsweredCount
    {
        get { int count = 0; foreach (Peer peer in _peers.Values) if (peer.Answered) count++; return count; }
    }

    private Peer Entry(ulong lookup)
    {
        if (!_peers.TryGetValue(lookup, out Peer peer)) { peer = new Peer(); _peers[lookup] = peer; }
        return peer;
    }

    // True when a hello should be sent to this peer right now: immediately the first time, then on the
    // quick cadence, and never again once the peer has answered - until it falls silent for
    // `SilenceSeconds`, at which point it is treated as new and greeted again.
    //
    // The quick cadence is a burst, not a budget: after `MaxAttempts` the greeting does not stop, it
    // slows to `KeepAliveSeconds`. Stopping was this build's own invention and it is the failure this
    // release removes: a peer whose plugin was still loading during those ten seconds - the ordinary case
    // when one machine is in the level and the other is still on the loading screen - or whose greetings
    // were dropped, was then written off for the entire run. The two machines exchanged nothing, which
    // from either screen is indistinguishable from a build that cannot share at all.
    public bool ShouldGreet(ulong lookup, float now)
    {
        if (lookup == 0UL) return false;
        Peer peer = Entry(lookup);
        if (peer.Answered)
        {
            if (now - peer.LastHeard < SilenceSeconds) return false;
            peer.Answered = false;
            peer.Attempts = 0;
            peer.NextTry = now;
        }
        if (peer.Attempts > 0 && now < peer.NextTry) return false;
        float interval = peer.Attempts >= MaxAttempts ? KeepAliveSeconds : RetrySeconds;
        if (peer.Attempts < int.MaxValue) peer.Attempts++;
        peer.NextTry = now + interval;
        return true;
    }

    // Whether a hello that just arrived should be answered now. Deliberately not "once": the answer is
    // what tells the other side this machine speaks the protocol, and a single answer lost in a busy
    // lobby used to end the exchange before it began.
    public bool ShouldAnswer(ulong lookup, float now)
    {
        if (lookup == 0UL) return false;
        Peer peer = Entry(lookup);
        if (now - peer.LastAnswer < AnswerInterval) return false;
        peer.LastAnswer = now;
        return true;
    }

    // A packet arrived from this peer, whatever it carried and whether or not it was accepted: the peer
    // exists, it speaks the protocol, and it is talking to this machine right now.
    public void Heard(ulong lookup, float now)
    {
        if (lookup == 0UL) return;
        Peer peer = Entry(lookup);
        peer.Answered = true;
        peer.EverAnswered = true;
        peer.Attempts = 0;
        peer.NextTry = now;
        peer.LastHeard = now;
    }

    public void MarkAnswered(ulong lookup) => Heard(lookup, 0f);

    public bool IsAnswered(ulong lookup) => lookup != 0UL && _peers.TryGetValue(lookup, out Peer peer) && peer.Answered;
    // Whether this peer has *ever* been heard from in this session, whatever it has done since. This, not
    // `IsAnswered`, is the gate on sending: a peer proved it runs this mod once, and a machine that can
    // still receive has to keep being sent to even after its own uplink has been quiet for longer than
    // `SilenceSeconds`. Gating the relay on `IsAnswered` meant a guest whose own packets were lost for
    // twelve seconds silently stopped being sent the host's rows - a second way this build could go
    // silent for a whole run while both machines were perfectly able to talk.
    public bool EverAnswered(ulong lookup) => lookup != 0UL && _peers.TryGetValue(lookup, out Peer peer) && peer.EverAnswered;
    // A peer that is no longer in the squad: its counter, its answer and how far a refusal had got all go,
    // so the player who later sits in that seat is greeted as the newcomer it is.
    public void Forget(ulong lookup) { if (lookup != 0UL) _peers.Remove(lookup); }


    // Whether a packet from this sender belongs to the run this machine is in. The first packet from a
    // sender adopts its generation; a later packet is accepted when it matches or is newer, and refused
    // while it is older - but never for longer than `StaleRepeats` refusals or `StaleGraceSeconds`, so a
    // peer that restarted (and whose counter really did begin again) is taken at its word.
    public bool AcceptsFrom(ulong lookup, uint generation, float now) => AcceptsFrom(lookup, generation, now, out _);

    public bool AcceptsFrom(ulong lookup, uint generation, float now, out bool recovered)
    {
        recovered = false;
        if (lookup == 0UL) return false;
        Peer peer = Entry(lookup);
        if (peer.Generation == generation) { peer.StaleCount = 0; return true; }
        if (IsNewer(generation, peer.Generation))
        {
            peer.Generation = generation;
            peer.StaleCount = 0;
            return true;
        }
        // Older than the newest the peer has already spoken for.
        if (peer.StaleCount == 0 || peer.StaleGeneration != generation)
        {
            peer.StaleGeneration = generation;
            peer.StaleSince = now;
            peer.StaleCount = 0;
        }
        peer.StaleCount++;
        if (peer.StaleCount >= StaleRepeats || now - peer.StaleSince >= StaleGraceSeconds)
        {
            peer.Generation = generation;
            peer.StaleCount = 0;
            recovered = true;
            return true;
        }
        return false;
    }

    public bool GenerationKnown(ulong lookup) => _peers.TryGetValue(lookup, out Peer peer) && peer.Generation != 0;

    public uint GenerationOf(ulong lookup) => _peers.TryGetValue(lookup, out Peer peer) ? peer.Generation : 0u;

    // Why a packet was refused, for the diagnostic line: both counters and how far the refusal has got.
    public string DescribeStale(ulong lookup)
    {
        if (!_peers.TryGetValue(lookup, out Peer peer)) return "the sender is unknown here";
        return "it carries generation " + peer.StaleGeneration + " while this machine already saw " + peer.Generation
            + " from it (refusal " + peer.StaleCount + " of " + StaleRepeats + ")";
    }

    // A lobby, session or host transition invalidates everything: who has answered, how often they were
    // asked, which run their packets belonged to, and how far a refusal had got.
    public void Reset() => _peers.Clear();

    private static bool IsNewer(uint candidate, uint current)
        => candidate != current && unchecked(candidate - current) < 0x80000000u;
}
