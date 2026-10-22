using System;
using System.Collections.Generic;

namespace HawChow.GTFOCombatPanel;

// Why this machine is allowed to put a packet of its own protocol on the wire for one peer.
//
// The squad channel is a private protocol riding the game's own byte channel: a machine that runs no
// mod has no handler for key 0xFFFE and cannot tell these bytes from a corrupt packet belonging to
// the game. So a peer is spoken to - a hello included, because a hello is already a packet only this
// build can produce - only after something outside this mod's protocol has said that peer runs this
// build. Two such signals exist, and neither of them is a probe:
//
//   * Allowlisted: the operator named this player's lookup in this machine's configuration. It is the
//     only route that can open a conversation between two machines that have never met, and it is
//     checkable by hand: the lookup is the same number the game's own lobby lists for that player.
//   * Proven: the peer sent this machine a packet of this build's own protocol. Nothing was sent to it
//     to find that out, so the signal is read-only - a machine without this mod never produces one.
//
// Anything else - no entry, a lookup of zero, a peer that answered with a protocol this build does not
// speak - is met with silence: no hello, no row, not one byte. The failure mode of a missing signal is
// a panel that shows what this machine measured itself, never a packet in a stranger's mailbox.
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
    Proven = 2
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

    // Two signals, one answer: a lookup the operator named is exactly as good as one that spoke first.
    // The order only decides which sentence the diagnostic uses - proof from the peer is reported in
    // preference to the operator's own note, because it is the stronger of the two and the only one
    // that cannot be a typo in a text file.
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
            default: return "there is no evidence it runs this build, so it is sent nothing";
        }
    }

    private static bool Contains(List<ulong> values, ulong lookup)
    {
        for (int index = 0; index < values.Count; index++) if (values[index] == lookup) return true;
        return false;
    }
}
