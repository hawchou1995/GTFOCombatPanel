using System;
using System.Collections.Generic;

namespace HawChow.GTFOCombatPanel;

// Who this machine is allowed to put a packet of its own protocol on the wire for.
//
// The squad exchange rides a transport the rest of the GTFO modding scene already uses for the same
// job: GTFO-API's free-sized network event (`SquadNet`). That event carries a fixed replicator key, a
// long ASCII magic and a version signature, and every machine that does not run this build drops the
// packet without a handler being involved - which is why StatDisplay, AccuracyTracker and every other
// mod built on that API can broadcast in a lobby that also holds vanilla players. Broadcasting is
// therefore the default here, and the previous release's rule - speak only to a peer that is named in
// a configuration file or that spoke first - is what left two modded machines greeting nothing at each
// other and sharing nothing at all, which is the failure this build repairs.
//
// Three signals decide one peer, strongest first:
//
//   * Proven: the peer sent this machine a packet of this build's own protocol. It is the only signal
//     that proves the *other* side runs this build, and it is read, never probed for.
//   * Broadcast: this machine shares with every human in its slot list, which is the default and the
//     behaviour every other mod on this transport has. The bytes are gated by the transport's own key
//     and magic, so a machine that runs no mod ignores them exactly as it ignores every other mod
//     packet it already receives.
//   * Allowlisted: the operator named this player's lookup in this machine's configuration. With
//     broadcast off, this is the only route that can open a conversation between two machines that
//     have never met, and it is checkable by hand.
//
// One answer is silence in every case: a lookup of zero, and a peer that answered with a protocol this
// build does not speak. Nothing this build sends can be read by such a machine, so nothing is sent.
//
// Nothing in this class touches the network, Unity, SNet or the configuration file, so every rule
// below is asserted headlessly.
public enum SquadEvidence
{
    // Nothing is known about this peer: it is never spoken to.
    Nothing = 0,
    // Named in this machine's own configuration as a machine that runs this build.
    Allowlisted = 1,
    // Proved it by sending this machine a packet of this build's protocol, unprompted.
    Proven = 2,
    // This machine shares with the whole slot list, which is the default.
    Broadcast = 3
}


public sealed class SquadCapability
{
    // A squad is four seats; the ceiling leaves room for a list that still names old squadmates.
    public const int MaxAllowlistEntries = 64;
    // A 64-bit lookup is at most 20 decimal digits; anything longer cannot be one.
    public const int MaxLookupDigits = 20;

    private readonly Dictionary<ulong, SquadEvidence> _evidence = new Dictionary<ulong, SquadEvidence>();
    private readonly HashSet<ulong> _allowlist = new HashSet<ulong>();
    private readonly HashSet<ulong> _incompatible = new HashSet<ulong>();
    // Whether this machine shares with every human in its slot list. Configuration, not session state,
    // so `ResetEvidence` leaves it standing.
    private bool _broadcast;

    // Sharing with the whole slot list is the default this build repairs to: the previous release
    // required a peer to prove itself before a greeting could go out, and two machines that both waited
    // therefore never spoke. Off means the older rule - the configuration list and proof from the peer.
    public bool Broadcast { get { return _broadcast; } }

    public void SetBroadcast(bool broadcast) { _broadcast = broadcast; }

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

    // "76561198000000001, 76561198000000002" -> the two lookups. Anything that is not a plain
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

    // The seat changed hands: the next occupant proves itself again rather than inheriting an entry.
    public void Forget(ulong peer)
    {
        if (peer == 0UL) return;
        _evidence.Remove(peer);
        _incompatible.Remove(peer);
    }
    // A new lobby or session: everything learned inside the old one goes. The configured list stays,
    // because it describes machines rather than a session.
    public void ResetEvidence()
    {
        _evidence.Clear();
        _incompatible.Clear();
    }

    public bool MaySend(ulong peer) { return MaySend(peer, out _); }

    // One answer for the wire. Proof from the peer is reported in preference to everything else - it is
    // the only signal that cannot be a typo or a stale list - then the broadcast this build defaults to,
    // then the operator's own note. A peer on another protocol version, or a lookup of zero, is refused
    // before any of that is consulted.
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
        if (_broadcast) return SquadEvidence.Broadcast;
        return _allowlist.Contains(peer) ? SquadEvidence.Allowlisted : SquadEvidence.Nothing;
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
            case SquadEvidence.Broadcast: return "this machine shares with every player in its slot list";
            default: return "there is no evidence it runs this build, so it is sent nothing";
        }
    }

    private static bool Contains(List<ulong> values, ulong lookup)
    {
        for (int index = 0; index < values.Count; index++) if (values[index] == lookup) return true;
        return false;
    }
}
