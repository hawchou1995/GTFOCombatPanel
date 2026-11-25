using System;
using System.Collections.Generic;

namespace HawChow.GTFOCombatPanel;

// What one sweep did about one enemy: Rejected when the radar was only updating, First when this
// sweep revealed the enemy for the squad, Repeat when somebody had already revealed it.
public enum ScanMark { Rejected, First, Repeat }

// 1.19.42: what one sample of a bot's own scan action proved about the scan that action is running.
//
//   Unavailable - the action's scan start could not be read. It names no scan, so it counts none: the figure
//                 stays unknown and is never estimated from a duration, a window or an enemy count.
//   Continuing  - the same scan as the one already counted: the update ran again, or `BotTag` returned again
//                 inside that one scan. Same start, same scan.
//   Started     - a scan start this bot had not shown before: exactly one use.
public enum BotScanSignal
{
    Unavailable = 0,
    Continuing = 1,
    Started = 2,
}

// One reveal namespace per expedition, shared by every scanner in the squad. The first sweep that
// puts an enemy on the bio tracker takes the credit; an enemy that is already revealed scores
// nothing for anybody afterwards, whoever sweeps it again and however many times they do.
public sealed class ScanReveals
{
    private readonly HashSet<int> _revealed = new HashSet<int>();

    public int Count { get { return _revealed.Count; } }

    // True only for the sweep that reveals this enemy first. Zero is never an enemy identity.
    public bool Reveal(int identity) { return identity != 0 && _revealed.Add(identity); }

    public void Clear() { _revealed.Clear(); }
}

// One scanner: its own tag-attempt edges, its own bot callback throttle, and the expedition's
// shared reveal ledger. Passive radar updates do not count as attempts. Bot static callbacks have
// no owner: attribution and timing remain estimates.
//
// The two mark figures answer two different questions and no longer share a gate:
//   - first marks  (`ScanFirstMarks`) are expedition-unique reveals: the sweep that first put an enemy
//     on the squad's bio tracker takes the credit, and nobody is credited for it again.
//   - cumulative marks (`ScanMarks`) are per-enemy marks inside the sweep window now under way, counted
//     again by every later sweep that marks the same enemy. One enemy contributes at most once to one
//     window, and a window is opened by the tag flag's rising edge, by a bot callback burst, by the
//     caller's own `bracket` (the native tag progress call) and by every native execution confirmation,
//     so a build - or a player - whose tag flag never reads true still counts cumulative marks instead of
//     mirroring the first marks.
public sealed class ScanSession
{
    private readonly ScanReveals _reveals;
    private readonly HashSet<int> _sweepMarks = new HashSet<int>();
    // 1.19.41: one activation figure per scanner, from the holder's own native cooldown. The tag flag only
    // ever opens a sweep window for the cumulative marks below; it no longer counts an activation, and neither
    // does a mark, a tag request or the local echo of one's own tag.
    private readonly ScannerCooldownLedger _cooldown = new ScannerCooldownLedger();
    private bool _tagging;
    private float _lastBotTag = float.NegativeInfinity;
    // 1.19.42: the identity of the one bot scan this session has already counted - the scan start its own action
    // published. A value the machine could not read is not an identity and never counts, so a build that cannot
    // read the action's scan start keeps the bot's activation figure unknown instead of counting windows.
    private float _lastBotScan = float.NaN;
    private long _botScanActivations, _botScanRepeats, _botScanUnusable;
    private bool _botScanObservable;
    public bool BotScanObservable => _botScanObservable;
    public long BotScanActivations => _botScanActivations;
    public long BotScanRepeats => _botScanRepeats;
    public long BotScanUnusable => _botScanUnusable;

    // A session on its own is its own ledger, which is what a single scanner's tests want. The
    // tracker hands every squad member's session the same expedition ledger instead.
    public ScanSession() : this(new ScanReveals()) { }

    public ScanSession(ScanReveals reveals) { _reveals = reveals ?? new ScanReveals(); }

    // The rising edge opens one sweep window for the cumulative marks. It is *not* an activation: the flag
    // flips many times inside one native scan (722 windows against a map ceiling of <=490 in the recorded
    // expedition), so counting it was counting windows. `bracket` is the caller's own window boundary - the
    // native tag-progress call - and only changes which marks belong to the current window.
    public bool ObserveTagging(bool active, ImpactCounters counters, bool bracket = false)
    {
        bool started = active && !_tagging;
        if (started || bracket) _sweepMarks.Clear();
        _tagging = active;
        return active && started;
    }

    // Each enemy contributes once to the sweep window now under way, while the first-mark score still
    // counts expedition-unique reveals. Neither a first mark nor a repeat contributes an activation: the
    // tag flag, a mark and the local echo of one's own tag are all in-sweep evidence, and the activation
    // figure comes from `ObserveRecharge` alone.
    public ScanMark Observe(int identity, ImpactCounters counters, bool bot = false)
    {
        if (counters == null || identity == 0) return ScanMark.Rejected;
        bool first = _reveals.Reveal(identity);
        if (_sweepMarks.Add(identity)) counters.CreditScanMark();
        if (!first) return ScanMark.Repeat;
        counters.ScanFirstMarks++;
        return ScanMark.First;
    }

    public ScanMark ObserveConfirmed(int identity, ImpactCounters counters, bool confirmed, bool bot = false)
        => confirmed ? Observe(identity, counters, bot) : ScanMark.Rejected;

    // One native cooldown sample of the scanner this session belongs to - and the one place an activation is
    // counted. The ledger decides; this only credits the row that holds the scanner. A sample this machine
    // could not read marks the figure *observable-but-unknown*, never a counted zero, and never an activation.
    public ScannerCooldownSignal ObserveRecharge(bool rechargingKnown, bool recharging, bool startKnown, float startTime, ImpactCounters counters)
    {
        ScannerCooldownSignal signal = _cooldown.Observe(rechargingKnown, recharging, startKnown, startTime);
        if (counters != null)
        {
            if (_cooldown.Observable) counters.ScanUsesKnown = true;
            if (signal == ScannerCooldownSignal.Started) counters.CreditScan(0);
        }
        return signal;
    }

    public bool CooldownObservable => _cooldown.Observable;
    public long CooldownActivations => _cooldown.Activations;
    public long CooldownCompletions => _cooldown.Completions;
    public long CooldownRepeats => _cooldown.Repeats;
    public long CooldownUnusable => _cooldown.Unusable;

    // One native execution confirmation, one sweep window. A guest's scanner is never readable here - only
    // the local human's `BeginHumanScan` runs, and the host has no scanner instance for a guest's row at all
    // - so on the host that row kept a single window for the whole expedition and its cumulative figure could
    // never leave the first-mark figure. Opening the window at every confirmation is what makes the
    // confirmation itself the boundary, whoever the player is. Only the sweep window is cleared: a
    // confirmation still cannot invent an activation, which is why the remote row's count stays unknown.
    public void BeginConfirmWindow() => _sweepMarks.Clear();

    public void EndBotTag() => _tagging = false;

    public bool MarkEnemy(int identity, ImpactCounters counters, bool bot = false)
        => Observe(identity, counters, bot) == ScanMark.First;


    public bool BeginBotTag(float time, float duration, ImpactCounters counters)
    {
        if (counters == null || !float.IsFinite(time)) return false;
        // A callback burst within the game's tag duration is one estimated window. A bot's scanner is not
        // readable on this machine, so the window opens marks and marks are what bots are credited for; no
        // activation is invented for a cooldown nobody here can see.
        float interval = float.IsFinite(duration) && duration > 0 ? Math.Max(0.25f, duration) : 1f;
        if (time - _lastBotTag < interval) { _tagging = true; return false; }
        _lastBotTag = time;
        _sweepMarks.Clear();
        _tagging = true;
        // The marks a bot's sweep produces are attributed by the bot's own scan action, so they are estimates;
        // the estimate flag is set here without inventing an activation beside it.
        counters.ScanEstimated = true;
        return true;
    }

    // 1.19.42: one bot's scan, counted only where the evidence names exactly one scan.
    //
    // A bot's sweep used to produce marks and no activation at all: `BotTag` is the only native call that tags,
    // it is static, and this session has no scanner instance to read a cooldown from - so the row kept its
    // activation figure unknown for the whole expedition however many sweeps the bot really ran.
    //
    // The evidence that one scan happened is the scan start the bot's own action publishes. Native
    // `PlayerBotActionUseEnemyScanner.StartScanning` (RVA 0x17DB430) assigns the action's `m_scanStartTime` from
    // `Time.time` as it enters the scan, and the action's `UpdateStateScan` (RVA 0x17DBA90) subtracts that same
    // value from `Time.time` for its elapsed test. The scan logic can revisit its tag state, so use the action's
    // start value at the tagged commit, never the raw callback count.
    //
    // Therefore: one distinct readable start is one scan. A repeated sample of it, a second `BotTag` inside it,
    // a mark, the `BotGetScanResult` query and a start this machine could not read all add nothing. No duration
    // and no window is ever turned into a number of scans.
    public BotScanSignal ObserveBotScan(bool startKnown, float start, ImpactCounters counters)
    {
        if (!startKnown || !float.IsFinite(start))
        {
            _botScanUnusable++;
            return BotScanSignal.Unavailable;
        }
        _botScanObservable = true;
        if (!float.IsNaN(_lastBotScan) && start <= _lastBotScan)
        {
            _botScanRepeats++;
            return BotScanSignal.Continuing;
        }
        _lastBotScan = start;
        _botScanActivations++;
        if (counters != null)
        {
            counters.ScanUsesKnown = true;
            counters.CreditScan(0);
        }
        return BotScanSignal.Started;
    }
}
