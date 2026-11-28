using System;
using System.Collections.Generic;
using System.Globalization;

namespace HawChow.GTFOCombatPanel;

// What one machine is allowed to say about the round's created-enemy figure (1.19.62).
//
//   Unknown  - this machine has no figure and no proof it could have one: it did not read the round's level
//              start, it observed nothing, or the evidence it does have cannot be placed in this round. The
//              settlement prints a dash and the archive writes `null`; a zero is never invented here.
//   Local    - this machine counted the enemies it saw created, and that count is only its own observation: it
//              is not the host, it joined late, it loaded mid-round or its role moved under the round. The
//              figure is a real lower bound and it says so instead of pretending to be the squad's total.
//   Squad    - this machine held the master role on the round's first in-level frame, it still holds it, every
//              creation it saw was inside the level and nothing else spoiled the attribution. That is the one
//              case where the number is the round's own total and may be read as such.
public enum SpawnScope
{
    Unknown = 0,
    Local = 1,
    Squad = 2,
}

// The round's created-enemy figure as the settlement reads it: one value for the whole squad, never one per
// player. `Created` is only meaningful where the scope says so; a caller prints `Created` for `Local` and `Squad`
// and a dash for `Unknown`.
public readonly struct SpawnTotal
{
    public readonly long Created;
    public readonly SpawnScope Scope;

    public SpawnTotal(long created, SpawnScope scope)
    {
        Created = created < 0 ? 0 : created;
        Scope = scope;
    }

    // True only for the figure that is the squad's own total.
    public bool Known { get { return Scope == SpawnScope.Squad; } }
    // True when this machine has a number at all, squad-wide or only its own observation.
    public bool Any { get { return Scope != SpawnScope.Unknown; } }

    public static SpawnTotal Unknown { get { return new SpawnTotal(0, SpawnScope.Unknown); } }
}

// The round's created-enemy ledger: one per expedition, squad-wide, and deliberately independent of kills, of
// scans and of who is still alive.
//
// What it counts: an enemy that was really created. The edge is `EnemyAgent.Setup`, the one lifecycle every
// enemy passes - a natural spawn, a command spawn, a wave - whatever happens to it afterwards. A figure that only
// moved when an enemy was hit or died would answer a different question, so this ledger is fed at creation only.
//
// The rules, each of them asserted in the suite:
//
//   1. One identity, once. A repeated `Setup` for the same enemy instance counts nothing: `EnemyAgent` objects
//      are pooled and re-set-up, and the identity is what makes the second call the same enemy rather than a
//      second one.
//   2. A new instance is a new enemy, whatever address it sits at. The identity is the instance id, never the
//      pointer, so a recycled pointer that now holds a new enemy counts as that enemy - which is the one thing a
//      pointer-keyed counter could not do.
//   3. The round starts on the game's own edge (`Begin`, from the expedition reset), and that is the *only* thing
//      that empties it. Generating -> InLevel is the same round and clears nothing; a settlement screen is read, never
//      reset.
//   4. Every creation of the round counts, including an enemy the level sets up *before* its first in-level frame: a
//      preset enemy and a wave spawned twenty minutes later are both this round's enemies. What decides whether the
//      figure may speak for the squad is not "was it in level" but "did this machine see the round begin" (`Begin`
//      with `fromGenerating`).
//   5. The total is the squad's only from a machine that owned the round's start: the master role read at that edge,
//      still held at settlement, never moving in between. A machine that only walked into a running level, a client,
//      a migrated machine and a late joiner keep their own observation and say so.
//   6. Silence is not zero. A machine with no round start and no observation answers Unknown; a proven round that has
//      created nothing yet answers a real 0.
public sealed class SquadSpawnLedger
{
    private readonly HashSet<int> _created = new HashSet<int>();
    private bool _armed;
    private bool _fromGenerating;
    private bool _masterAtStart;
    private bool _unproven;

    // How many distinct enemy instances this machine has seen created this round.
    public int Count { get { return _created.Count; } }
    // Whether this machine saw the round begin at all.
    public bool Armed { get { return _armed; } }
    // Whether this machine saw it begin where the level did: `Generating`, before any enemy existed.
    public bool FromGenerating { get { return _armed && _fromGenerating; } }
    // Whether something in this round spoiled the attribution.
    public bool Unproven { get { return _unproven; } }

    // The expedition's own start - the one edge every ledger in this build resets on, and the only one that empties this
    // figure. `fromGenerating` is which game state raised it:
    //
    //   true   the round began while this machine was waiting for the level (`Generating`), so every enemy that level
    //          goes on to create is counted here - including any it sets up before its first in-level frame.
    //   false  this machine walked into a level that was already running (a mid-round load or join). It begins here too,
    //          but its figure can only ever be its own observation: it cannot know what it did not see.
    //
    // The role read here is the one the total belongs to, because it is the role this machine held while the round began.
    public void Begin(bool isMaster, bool fromGenerating)
    {
        _created.Clear();
        _armed = true;
        _fromGenerating = fromGenerating;
        _masterAtStart = isMaster;
        _unproven = false;
    }

    // The role is sampled every in-level frame, not only when something is created and not only at the two ends of the
    // round. A host that was demoted and promoted again inside one round (`host -> guest -> host`) has the same role at
    // the start and at settlement, so comparing only those two would let it publish a "complete" total for a round it
    // spent part of as somebody else's client. One disagreeing sample is enough.
    public void NoteRole(bool isMaster)
    {
        if (!_armed) return;
        if (isMaster != _masterAtStart) _unproven = true;
    }

    // One enemy the game really set up this round. Answers true only for an instance this round had not counted yet.
    public bool Observe(bool isMaster, int identity)
    {
        if (identity == 0) return false;                    // no identity, no enemy
        if (!_armed) { _unproven = true; return false; }     // no round start on this machine
        NoteRole(isMaster);                                 // the role moved under the round
        return _created.Add(identity);
    }

    // The figure as settlement may print it, resolved against the role this machine holds *now* so a demoted or
    // migrated machine can never publish the previous host's total. The final read is one more role sample.
    public SpawnTotal Total(bool isMaster)
    {
        NoteRole(isMaster);
        if (_armed && _fromGenerating && !_unproven && _masterAtStart && isMaster)
            return new SpawnTotal(_created.Count, SpawnScope.Squad);
        if (_created.Count > 0) return new SpawnTotal(_created.Count, SpawnScope.Local);
        return SpawnTotal.Unknown;
    }

    // A fresh session: nothing observed, no round start, nothing claimed.
    public void Reset()
    {
        _created.Clear();
        _armed = false;
        _fromGenerating = false;
        _masterAtStart = false;
        _unproven = false;
    }
}

// The settlement's own line for the figure above, written once per expedition beside the board it belongs to.
//
// It exists because the created-enemy figure is not a column of the board and must not become one: the board's
// columns are players, the rows are that player's figures, and every existing cell keeps exactly the content it
// had. The figure is one number for the round, so it is written once, from the one global ledger, and the archive
// carries the same value under its own `spawns` object.
//
// This line is never a kill count and never a scan count: `ScanFirstMarks`, `ScanMarks` and `ScanUses` answer
// their own questions and this figure answers "how many enemies were created this round". The cumulative mark
// figure is never called a spawn count anywhere in this build.
public static class SettlementSpawns
{
    // The archive key this figure is written under, beside `players`.
    public const string JsonKey = "spawns";

    private static string _written;
    private static uint _run;
    private static bool _started;

    public static void Reset() { _written = null; _run = 0; _started = false; }

    // One line per expedition, written again only when the figure on it really changed. An empty string means
    // there is nothing to say: no report, or the same line already written for this run.
    public static string Line(ImpactReport report, uint run)
    {
        if (report == null) return string.Empty;
        if (!_started || _run != run) { _written = null; _run = run; _started = true; }
        string line = Describe(report.Spawns);
        if (string.Equals(_written, line, StringComparison.Ordinal)) return string.Empty;
        _written = line;
        return line;
    }

    // The line itself, from the figure alone - the shape a test can assert without a page, a board or a log.
    public static string Describe(SpawnTotal total)
    {
        switch (total.Scope)
        {
            case SpawnScope.Squad:
                return "[DIAG] settlement spawns: created=" + Count(total.Created)
                    + "; scope=squad; source=the round's own EnemyAgent.Setup edge, counted once per created instance"
                    + " (natural and command spawns alike), independent of kills, scans and who is still alive;"
                    + " this is the round's created-enemy figure, never a kill count and never a scan count.";
            case SpawnScope.Local:
                return "[DIAG] settlement spawns: created=" + Count(total.Created)
                    + "; scope=local; this machine is not the host that owned this round's level start - it joined"
                    + " late, loaded mid-round or its role moved - so the figure is this machine's own observation"
                    + " and never the squad's total; no number is guessed for the rest of the round.";
            default:
                return "[DIAG] settlement spawns: created=" + Placeholder
                    + "; scope=unknown; this machine read no round start and observed no creation it can place in"
                    + " this round, so the figure is unknown rather than zero.";
        }
    }

    // The figure as the archive and the line print it: a number, or the dash that says nothing measured it.
    public static string CountText(SpawnTotal total)
        => total.Any ? Count(total.Created) : Placeholder;

    // The one line the settlement board carries on its header: `累计刷怪量：21` for the squad's own total, the same
    // figure with `（本机观测）` when this machine can only offer its own observation, and `累计刷怪量：—（未知）`
    // when it has no figure at all. One string for the whole board - it is not built per player and it never appears
    // once per column, which is what makes it one number on a four-player page.
    public static string Figure(SpawnTotal total)
    {
        if (total.Scope == SpawnScope.Squad) return CombatStrings.SpawnFigureLabel + Count(total.Created);
        if (total.Scope == SpawnScope.Local)
            return CombatStrings.SpawnFigureLabel + Count(total.Created) + CombatStrings.SpawnFigureLocal;
        return CombatStrings.SpawnFigureLabel + Placeholder + CombatStrings.SpawnFigureUnknown;
    }

    public static string ScopeName(SpawnScope scope)
    {
        switch (scope)
        {
            case SpawnScope.Squad: return "squad";
            case SpawnScope.Local: return "local";
            default: return "unknown";
        }
    }

    private static string Placeholder { get { return CombatStrings.Placeholder; } }

    private static string Count(long value)
        => (value < 0 ? 0 : value).ToString(CultureInfo.InvariantCulture);
}
