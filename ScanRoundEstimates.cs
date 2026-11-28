using System;
using System.Collections.Generic;
using System.Globalization;

namespace HawChow.GTFOCombatPanel;

// 1.19.62: the host's estimate of how many Bio Tracker scan rounds produced at least one confirmed mark.
//
// Why an estimate exists at all: an activation is only readable on the machine that holds the scanner
// (`Gear.EnemyScanner.m_recharging` / `m_rechargeStartTime`), and the host has no scanner instance for a guest's
// row, so a guest's activation count stays unknown on the host's board. Printing a number there would be
// inventing one, and it is exactly the figure this release must not invent. What the host does have is the native
// mark confirmations themselves: each carries the player the native validation attributed it to
// (`ScanValidationPatch` / `ScanExecutedPatch`) and the monotonic stamp taken when the mark was *received*
// (`ScanTagPipeline`'s `Event.Time`), not the time the main-thread drain got round to it.
//
// One cluster of marks is one round:
//   * marks whose capture stamps sit within `ClusterGap` of the previous mark in the cluster are one round, so a
//     sweep that marks five enemies still adds one;
//   * a mark further than `ClusterGap` from the last one opens the next round, so the same enemy marked again by
//     a later sweep adds one more;
//   * neither the number of marks nor elapsed time is ever turned into rounds. An expedition that stays silent
//     for a minute adds nothing, and a cooldown that rolled over unobserved adds nothing either.
//
// Why 2.0 s: one sweep tags its enemies in a single native pass, so its confirmations arrive in the same batch,
// microseconds apart - and a backlogged drain compares *capture* stamps, so a delayed drain cannot spread them.
// same two-second window as the first one's marks. 2.0 s therefore sits between "all marks of one sweep" and
// "the earliest possible second sweep": it never splits one sweep and never merges two.
//
// That is a grouping choice with a reason, not a measurement. The board nevertheless prints the grouping *in the
// activation-count slot* and as a plain count - `扫描 2 次` / `scan 2x`, the exact form a measured figure prints in -
// because the host's grouped estimate is meant to stand in that one position rather than beside it: no `~`, no 约,
// no `（估算）`/`est.`, no second `轮次` clause and no `扫描 — 次` still showing underneath. The distinction is kept
// where it belongs, in the comparison line below and in the once-per-player diagnostic, not on the settlement board.
//
// What this ledger is not: it never writes `ImpactCounters.ScanUses`, `ScanFirstMarks`, `ScanMarks` or
// `ScanEstimated`, it is never serialised into a packet, and nothing merges or scores it. It is a host-local side
// computation, so the online protocol, the snapshot layout and the scoring/title paths are untouched.
public sealed class ScanRoundEstimateLedger
{
    // The grouping threshold explained above. Two seconds.
    public const double ClusterGap = 2.0;
    // Rows are looked up by player lookup; a squad is four seats, so this is only a guard against a runaway
    // roster. A lookup past it simply gets no estimate rather than evicting somebody else's.
    public const int Capacity = 64;

    private sealed class Row
    {
        public double Last;
        public long Rounds;
        // Kept for the one settlement comparison line only. It is never read by `Credit` to decide a round.
        public long Marks;
    }

    private readonly Dictionary<ulong, Row> _rows = new Dictionary<ulong, Row>();
    private readonly object _gate = new object();

    // One confirmed mark of one player, stamped with the monotonic time it was *received*. Answers true only
    // when this mark opened a new round, which is the one moment a line is worth printing. The caller runs on the
    // main thread (the confirmation drain); the lock exists so a read from any other thread is safe, and the
    // capture thread never touches this ledger at all - it only enqueues values.
    public bool Credit(ulong lookup, double receivedAt)
    {
        if (lookup == 0 || !double.IsFinite(receivedAt)) return false;
        lock (_gate)
        {
            if (!_rows.TryGetValue(lookup, out Row row))
            {
                if (_rows.Count >= Capacity) return false;
                _rows[lookup] = new Row { Last = receivedAt, Rounds = 1, Marks = 1 };
                return true;
            }
            row.Marks++;
            // A later mark inside the window continues this round. The stamp only ever moves forward, so an
            // event that arrives out of order can never drag the window back and split the next round in two.
            if (receivedAt - row.Last <= ClusterGap)
            {
                if (receivedAt > row.Last) row.Last = receivedAt;
                return false;
            }
            row.Last = receivedAt;
            row.Rounds++;
            return true;
        }
    }

    public bool TryGet(ulong lookup, out long rounds)
    {
        rounds = 0;
        if (lookup == 0) return false;
        lock (_gate)
        {
            if (!_rows.TryGetValue(lookup, out Row row) || row.Rounds <= 0) return false;
            rounds = row.Rounds;
            return true;
        }
    }

    public long Marks(ulong lookup)
    {
        lock (_gate) return _rows.TryGetValue(lookup, out Row row) ? row.Marks : 0;
    }

    public int Count { get { lock (_gate) return _rows.Count; } }

    public void Clear() { lock (_gate) _rows.Clear(); }

    // The display rule, beside the ledger it reads: a measured activation count this machine trusts ends the
    // estimate for that row. "Measured" is the holder's own readable cooldown (`ScanUsesKnown`, including a
    // *known zero*) or a positive count that player's machine reported - in both cases the owner's figure wins
    // even when it is smaller than the estimate this host had accumulated, and the estimate is simply not shown.
    // Only a row with no measured figure at all keeps the estimate, which is also why nothing ever needs to be
    // compared against an old estimate: the two figures never share a field.
    public long Display(ImpactCounters counters, ulong lookup)
    {
        if (counters == null) return 0;
        if (counters.ScanUsesKnown || counters.ScanUses > 0) return 0;
        return TryGet(lookup, out long rounds) ? rounds : 0;
    }
}

// The host's one live ledger, and the gate that decides whose mark may feed it.
//
// The gate is the whole eligibility rule of the feature and is written once, here, so it can be asserted
// directly: only the host estimates (`SNet.IsMaster`), only for another machine's human (`lookup != 0`,
// `!IsLocal`, `!IsBot`), and only from a confirmed native mark. An empty owner, a missing sender, this machine's
// own scanner, a bot and a client all estimate nothing at all - they simply leave the ledger untouched, which is
// what keeps every other row and every other machine's board exactly as it was.
public static class ScanRoundEstimates
{
    private static readonly ScanRoundEstimateLedger Live = new ScanRoundEstimateLedger();

    public static bool Credit(bool isMaster, bool isLocal, bool isBot, ulong lookup, double receivedAt)
        => isMaster && !isLocal && !isBot && Live.Credit(lookup, receivedAt);

    // The one gate a confirmed native mark passes before it may open a round, and the only entry the production
    // drain uses. `First` and `Repeat` both count: a sweep that marks enemies the squad already knows still cost a
    // real scan, so a second batch made entirely of old enemies - the case where neither the first-mark figure nor
    // the visible cumulative figure moves at all - is still the next round. Only `Rejected` counts nothing: the
    // radar was merely updating, or the session refused the mark, so no scan can be inferred from it.
    //
    // The gate deliberately never reads `ScanFirstMarks` or `ScanMarks`: those are totals, this is a count of
    // batches, and a rule that required either figure to rise would stop counting exactly the repeats above. It is
    // written as its own entry point so a caller can be asserted - and so a mutation that drops `ScanMark.Repeat`
    // fails a test instead of quietly shrinking the figure.
    public static bool CreditConfirmed(bool isMaster, bool isLocal, bool isBot, ulong lookup, ScanMark mark, double receivedAt)
        => mark != ScanMark.Rejected && Credit(isMaster, isLocal, isBot, lookup, receivedAt);

    // The figure the settlement board may print in the activation-count slot. 0 means "there is nothing to show",
    // never "the estimate is zero": a round exists only because at least one mark was confirmed.
    //
    // 1.19.62 (unreleased): `isMaster` is the caller's role read, taken at the moment the board is built. A machine
    // that is no longer the host - a host migration, a demotion, an unreadable interop role - answers 0 rather than
    // drawing a figure only the previous host could have grouped, so no board ever shows a stale estimate.
    public static long Display(ImpactCounters counters, ulong lookup, bool isMaster)
        => isMaster ? Live.Display(counters, lookup) : 0;


    // The estimate the ledger accumulated for one player, whatever that row's measured figure says. Used by the
    // one live diagnostic line and by the settlement comparison, which both want the host's own number.
    public static bool TryRounds(ulong lookup, out long rounds) => Live.TryGet(lookup, out rounds);

    public static int Count { get { return Live.Count; } }

    public static void Reset() { Live.Clear(); }
}

// The one comparison line per player and expedition, written when the settlement board is saved. It exists so a
// host can judge the estimate against the figure the player's own machine reported - especially at settlement,
// when that figure has finally arrived. Deliberately restrained: no line per enemy, no line per repaint, and a
// line is printed again only when the numbers on it really changed.
public static class ScanRoundComparison
{
    private static readonly Dictionary<ulong, string> Written = new Dictionary<ulong, string>();
    private static uint _run;
    private static bool _started;

    public static void Reset() { Written.Clear(); _run = 0; _started = false; }

    // One line per player this host estimated, or none at all when it estimated nobody.
    //
    // 1.19.62 (review finding): the figure on this line is the ledger's own number (`TryRounds`), never the board's
    // answer. The board refuses the estimate as soon as a row has a measured figure of its own, and that refusal is
    // exactly what this line must not inherit: the case the line exists for is "the owner's own figure has finally
    // arrived", and asking the board (`Display` / the old `LedgerFigure`) made the line disappear in precisely that
    // case - `ScanUsesKnown || ScanUses > 0` answers 0, so the one comparison a host cannot make any other way was
    // never written. The board still prints the measured figure first; only this diagnostic reads the grouped count
    // independently of it.
    //
    // `isMaster` is the same role the board uses. A machine that is no longer the host (host migration, a demotion,
    // an unreadable role) prints no line rather than the previous host's figure, and it leaves the per-run latch
    // alone - so a machine that becomes host again does not re-print a line it already wrote.
    public static List<string> Lines(ImpactReport report, uint run, bool isMaster)
    {
        var lines = new List<string>();
        if (report == null || !isMaster) return lines;
        if (!_started || _run != run) { Written.Clear(); _run = run; _started = true; }
        for (int index = 0; index < report.Entries.Count; index++)
        {
            ImpactEntry entry = report.Entries[index];
            if (entry == null || entry.Input == null) continue;
            // The ledger's accumulated figure for that player, whatever the row's measured figure says. 0 means the
            // host grouped nothing for this row, which is the only reason to print nothing.
            long estimate = ScanRoundEstimates.TryRounds(entry.Input.Lookup, out long grouped) ? grouped : 0;
            if (estimate <= 0) continue;
            string measured = Measured(entry.Counters);
            // 1.19.64: the line used to print `marks=` with the first-mark figure behind it, which read as if it were
            // the cumulative total. Both figures are printed now, each under its own name, and neither number changes:
            // the two counts are the ones the board draws, and the estimate above is untouched.
            string line = "[DIAG] scan rounds (host estimate vs measured): " + Label(entry.Name) + "; estimate="
                + Count(estimate) + "; measured=" + measured + "; firstMarks=" + Count(FirstMarks(entry.Counters))
                + "; cumulativeMarks=" + Count(CumulativeMarks(entry.Counters))
                + "; estimated rounds are a grouping of confirmed marks, never the native activation count.";
            if (Written.TryGetValue(entry.Input.Lookup, out string previous)
                && string.Equals(previous, line, StringComparison.Ordinal)) continue;
            Written[entry.Input.Lookup] = line;
            lines.Add(line);
        }
        return lines;
    }

    // The row's measured activation figure as the line prints it: the holder's readable cooldown including a
    // counted zero, a positive count that player's machine reported, or the dash that says nothing measured it.
    private static string Measured(ImpactCounters counters)
    {
        if (counters == null) return "— (unknown)";
        if (counters.ScanUsesKnown) return Count(counters.ScanUses) + " (holder's own cooldown)";
        if (counters.ScanUses > 0) return Count(counters.ScanUses) + " (owner-reported)";
        return "— (unknown)";
    }

    // 1.19.64: the two mark figures the board draws, kept apart by name so no diagnostic can present one as the
    // other again. Neither value is computed here: they are the row's own counters, exactly as the cells read them.
    private static long FirstMarks(ImpactCounters counters)
        => counters == null ? 0 : counters.ScanFirstMarks;

    private static long CumulativeMarks(ImpactCounters counters)
        => counters == null ? 0 : counters.ScanMarks;

    private static string Count(long value)
        => (value < 0 ? 0 : value).ToString(CultureInfo.InvariantCulture);

    private static string Label(string name) => string.IsNullOrEmpty(name) ? "(unnamed)" : name;
}
