using System;
using System.Collections.Generic;
using System.Text;

namespace HawChow.GTFOCombatPanel;

// How often one kind of squad packet may describe itself in the log.
//
// The recorded survival session (70,739 lines, 634 of them this mod's) has 161 `squad sync supply kinds` lines for
// the same accepted split of the same pack of the same four players, 49 `squad sync row` lines and 33 `squad sync
// snapshot` lines: those three shapes are per *packet*, and a packet repeats every couple of seconds for the whole
// expedition. They crowded out the lines that only happen once - the first packet of a peer, a refusal, a page's
// anchor. Nothing about the wire changes here; only what it is allowed to say.
//
// The rule, per kind of packet and per peer:
//
//   * the first line for an **object** of that peer is always written - and the object is a field of its own
//     (`objectKey`: the row's owner, the pack's owner), so it is never folded into the normalisation that collapses the
//     changing parts of a *reason*. `N3`: the state used to be `accepted;reason`, and the reviewer's case - one peer
//     forwarding four different decimal owners with the same reason - kept only the first owner's line. Folding digits
//     into `#` (which is right for a generation or a packet count) must not touch identity;
//   * a line is written again when the state of that object changes (accepted -> refused, another reason);
//   * an anomaly is written once per distinct state, and it has its own bounded budget per peer and per kind
//     (`AnomalyPerPeerBudget`, `AnomalyPerKindBudget`) so a fault is never dropped because ordinary packets spent the
//     budget - and never floods either; what it cannot print is counted;
//   * everything else is counted, not printed, and the counts are printed as one summary line when the expedition or
//     the lobby that produced them ends, so the totals remain recomputable from the log alone;
//   * the remembered state set per peer is bounded (`MaxStatesPerPeer`, oldest forgotten first). Because every written
//     line is also budget-bounded, a forgotten state cannot turn into a per-packet rewrite (`N5`);
//   * an exception that repeats once per packet is bucketed: first occurrence, then every doubling of the count, with
//     the true total in the line and in the summary - and the number of distinct buckets per kind is bounded, further
//     texts collapsing into one visible "(other failure text)" bucket rather than adding keys without limit (`N5`);
//   * the counts are only committed when the caller really wrote the line (`N2`): `Reserve`/`Commit`/`Cancel`.
//
// Free of Unity and BepInEx so the whole rule is asserted headlessly in tests/GTFOCombatPanel.Smoke.
public sealed class SyncLogPolicy
{
    // Lines one peer may spend on one kind of packet per expedition. A squad has at most four owners; twelve leaves room
    // for each owner's accepted and refused state plus a few changes, and everything past it is the repeat.
    public const int PerPeerKindBudget = 12;
    // Lines one kind of packet may spend per expedition across every peer.
    public const int PerKindBudget = 48;
    // Anomalies (refusals, failures) are bounded on their own, so the ordinary budget can never crowd them out and they
    // can never flood: eight distinct anomaly states per kind, four per peer.
    public const int AnomalyPerKindBudget = 8;
    public const int AnomalyPerPeerBudget = 4;
    // Distinct states remembered per peer and kind. Past this the oldest are forgotten, which can only cost a line
    // (a forgotten state looks new and is written once more, inside the budget), never hide a first occurrence for good.
    public const int MaxStatesPerPeer = 16;
    // Distinct exception texts remembered per kind. Past this every further text counts under one visible bucket.
    public const int MaxErrorKeysPerKind = 8;
    public const string OtherFailure = "(other failure text)";
    // The text of a state is capped here: a reason that quotes a generation or a long peer list still collapses to a
    // few words, and the summary stays one line.
    public const int MaxStateLength = 72;
    // The separator between the object key and the normalised state inside the remembered state key.
    private const char ObjectSeparator = '\u001f';

    private sealed class Entry
    {
        public string Last = string.Empty;
        public int Written, Anomalies;
        public readonly HashSet<string> States = new HashSet<string>();
        public readonly Queue<string> Order = new Queue<string>();
    }

    private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
    private readonly Dictionary<string, int> _written = new Dictionary<string, int>();
    private readonly Dictionary<string, int> _suppressed = new Dictionary<string, int>();
    private readonly Dictionary<string, int> _anomalies = new Dictionary<string, int>();
    private readonly Dictionary<string, Dictionary<string, int>> _errors = new Dictionary<string, Dictionary<string, int>>();
    private readonly Dictionary<string, HashSet<string>> _errorKeys = new Dictionary<string, HashSet<string>>();

    // A reserved line: nothing is counted until the caller says it really wrote it.
    public readonly struct Ticket
    {
        internal readonly string Kind, Key, StateKey;
        internal readonly bool Anomaly;
        internal Ticket(string kind, string key, string stateKey, bool anomaly)
        {
            Kind = kind; Key = key; StateKey = stateKey; Anomaly = anomaly;
        }
        public bool Valid { get { return Kind != null; } }
    }

    public Ticket Reserve(string kind, ulong peer, string state, bool anomaly)
        => Reserve(kind, peer, objectKey: null, state, anomaly);

    // `objectKey` is the stable identity the line is about (a row's owner, a pack's owner). It is kept apart from
    // `state`, so the normalisation that collapses a changing reason can never merge two objects into one (`N3`).
    public Ticket Reserve(string kind, ulong peer, string objectKey, string state, bool anomaly)
    {
        if (string.IsNullOrEmpty(kind)) return default;
        string key = kind + "|" + peer.ToString("X16");
        if (!_entries.TryGetValue(key, out Entry entry)) _entries[key] = entry = new Entry();
        string stateKey = (string.IsNullOrEmpty(objectKey) ? string.Empty : objectKey) + ObjectSeparator + Normalize(state);
        if (entry.States.Contains(stateKey)) { Count(_suppressed, kind); return default; }
        if (anomaly)
        {
            if (entry.Anomalies >= AnomalyPerPeerBudget || Anomalies(kind) >= AnomalyPerKindBudget)
            {
                Count(_suppressed, kind);
                return default;
            }
            return new Ticket(kind, key, stateKey, anomaly: true);
        }
        if (entry.Written > 0 && entry.Last == stateKey) { Count(_suppressed, kind); return default; }
        if (entry.Written >= PerPeerKindBudget || Written(kind) >= PerKindBudget) { Count(_suppressed, kind); return default; }
        return new Ticket(kind, key, stateKey, anomaly: false);
    }

    public void Commit(Ticket ticket)
    {
        if (!ticket.Valid) return;
        if (!_entries.TryGetValue(ticket.Key, out Entry entry)) _entries[ticket.Key] = entry = new Entry();
        entry.Last = ticket.StateKey;
        if (ticket.Anomaly)
        {
            entry.Anomalies++;
            Count(_anomalies, ticket.Kind);
        }
        else entry.Written++;
        Count(_written, ticket.Kind);
        if (entry.States.Contains(ticket.StateKey)) return;
        entry.States.Add(ticket.StateKey);
        entry.Order.Enqueue(ticket.StateKey);
        while (entry.Order.Count > MaxStatesPerPeer) entry.States.Remove(entry.Order.Dequeue());
    }

    // The caller could not write the line (its own exact-text dedupe, or its cap): nothing is claimed for it.
    public void Cancel(Ticket ticket)
    {
        if (!ticket.Valid) return;
        Count(_suppressed, ticket.Kind);
    }

    // Reserve + Commit for callers that own the line themselves (the smoke suite drives this directly).
    public bool Write(string kind, ulong peer, string state, bool anomaly)
        => Write(kind, peer, objectKey: null, state, anomaly);

    public bool Write(string kind, ulong peer, string objectKey, string state, bool anomaly)
    {
        Ticket ticket = Reserve(kind, peer, objectKey, state, anomaly);
        if (!ticket.Valid) return false;
        Commit(ticket);
        return true;
    }

    // One bounded line for an exception that repeats once per packet (`N4`/`N5`): the first occurrence of that bucket
    // always prints, and so does every doubling of the count, so a growing fault stays visible without printing itself
    // per packet. Distinct texts are bucketed: past `MaxErrorKeysPerKind` every further text counts under one visible
    // "(other failure text)" bucket, so the keys - and the lines - stay bounded while nothing is hidden silently.
    public bool Error(string kind, string message, out int occurrences)
    {
        string bucketKind = string.IsNullOrEmpty(kind) ? "squad" : kind;
        if (!_errorKeys.TryGetValue(bucketKind, out HashSet<string> keys)) _errorKeys[bucketKind] = keys = new HashSet<string>();
        if (!_errors.TryGetValue(bucketKind, out Dictionary<string, int> counts)) _errors[bucketKind] = counts = new Dictionary<string, int>();
        string bucket = Normalize(message);
        if (!keys.Contains(bucket))
        {
            bool room = keys.Count < MaxErrorKeysPerKind;
            if (room) keys.Add(bucket);
            else bucket = OtherFailure;
        }
        counts.TryGetValue(bucket, out int count);
        count++;
        counts[bucket] = count;
        occurrences = count;
        return count == 1 || (count & (count - 1)) == 0;
    }

    public int Written(string kind)
        => kind != null && _written.TryGetValue(kind, out int value) ? value : 0;

    public int Suppressed(string kind)
        => kind != null && _suppressed.TryGetValue(kind, out int value) ? value : 0;

    public int Anomalies(string kind)
        => kind != null && _anomalies.TryGetValue(kind, out int value) ? value : 0;

    public int Errors(string kind)
    {
        int total = 0;
        foreach (KeyValuePair<string, Dictionary<string, int>> pair in _errors)
        {
            if (kind != null && pair.Key != kind) continue;
            foreach (int count in pair.Value.Values) total += count;
        }
        return total;
    }

    // How many distinct exception buckets this kind kept (a bound, not a count of occurrences).
    public int ErrorBuckets(string kind)
        => kind != null && _errorKeys.TryGetValue(kind, out HashSet<string> keys) ? keys.Count : 0;

    // The one line the counts are printed as, or null when nothing was held back: "written=N suppressed=M".
    public string Summary()
    {
        var kinds = new List<string>(_written.Keys);
        foreach (string kind in _suppressed.Keys) if (!kinds.Contains(kind)) kinds.Add(kind);
        kinds.Sort(StringComparer.Ordinal);
        var text = new StringBuilder();
        foreach (string kind in kinds)
        {
            int written = Written(kind), suppressed = Suppressed(kind);
            if (written <= 0 && suppressed <= 0) continue;
            if (text.Length > 0) text.Append("; ");
            text.Append(kind).Append(" written=").Append(written).Append(" suppressed=").Append(suppressed);
            int anomalies = Anomalies(kind);
            if (anomalies > 0) text.Append(" anomalies=").Append(anomalies);
        }
        int errors = Errors(null);
        if (errors > 0)
        {
            if (text.Length > 0) text.Append("; ");
            text.Append("errors=").Append(errors);
        }
        return text.Length == 0 ? null : text.ToString();
    }

    // A new expedition, or a new lobby: the peers and their states belong to the run that just ended.
    public void Reset()
    {
        _entries.Clear();
        _written.Clear();
        _suppressed.Clear();
        _anomalies.Clear();
        _errors.Clear();
        _errorKeys.Clear();
    }

    // The changing part of a line - a reason, a generation, a packet count - with every digit run collapsed and the text
    // capped (`N5`). Identity is **not** normalised here: callers pass that separately as `objectKey` (`N3`).
    public static string Normalize(string state)
    {
        if (string.IsNullOrEmpty(state)) return string.Empty;
        var text = new StringBuilder(Math.Min(state.Length, MaxStateLength));
        bool digits = false;
        foreach (char glyph in state)
        {
            if (char.IsDigit(glyph)) { if (!digits) { text.Append('#'); digits = true; } continue; }
            digits = false;
            if (char.IsControl(glyph)) continue;
            if (text.Length >= MaxStateLength) break;
            text.Append(glyph);
        }
        return text.ToString().Trim();
    }

    private static void Count(Dictionary<string, int> counters, string kind)
    {
        if (string.IsNullOrEmpty(kind)) return;
        counters[kind] = counters.TryGetValue(kind, out int value) ? value + 1 : 1;
    }
}
