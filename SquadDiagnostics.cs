using System;

namespace HawChow.GTFOCombatPanel;

// ---------------------------------------------------------------------------------------------------
// SquadDiagnostics.cs - the level clock the inbound diagnostic is measured against, and the two lines a
// level owes once.
//
// `CombatSync` writes one line about the outbound direction (something was put on the wire) and, when the
// exchange is still blind, one warning about the inbound direction (nothing was heard from anybody). Both
// are facts about *one level*: the greeting cadence, the roster and the rows all belong to the expedition
// on the screen. The build this replaces kept the level clock and the two latches in three separate
// fields, restarted them only on the InLevel branch and left them alone on every other state - so a level
// that was entered after a lobby inherited the clock of the level before it. A machine that entered a
// level one lobby and one generation after the previous one - `NoLobby --> InLobby`, then `Generating`,
// then `InLevel` - reported "1416 s in the level with 3 human peer(s) and not one packet heard" on the
// first frame of the new level, and then went on to accept the hellos that proved it wrong six lines
// later. The outbound latch had the mirror-image failure: it was cleared by the same unreachable branch,
// so a session printed that line once and never again.
//
// What replaces it: one object that owns the phase, the clock and the two latches, and one transition
// rule - the clock, the latches and the evidence they are decided on all start and end together. The
// phase is mapped from the game's own state name by the caller (`CombatSync.Phase`), so this file needs
// no Unity type and the whole of it is asserted headlessly in tests/GTFOCombatPanel.Smoke.
// ---------------------------------------------------------------------------------------------------

// The three phases a tick of the squad exchange belongs to. `Result` is a real phase rather than part of
// `Outside` because the exchange keeps ticking on a result page: a row can still go out there, and the
// level's own figures are still the ones being quoted.
public enum LevelPhase { Outside = 0, InLevel = 1, Result = 2 }

// The level clock, and the two once-per-level lines it guards.
//
// `Enter` is called on every tick with the phase that tick belongs to, before anything else is decided.
// It answers true exactly on the tick a new level begins - the caller's cue to drop the per-level
// evidence it holds (`CombatSync.Heard`), so the clock and the evidence cannot describe different levels
// - and it stops the clock on the tick the level ends. A phase that repeats changes nothing, so a level
// that lasts an hour is one level.
public sealed class LevelDiagnosticClock
{
    // How long a level may run with human peers in the roster and nothing heard from any of them before
    // the inbound warning is written. Twenty seconds is several greeting cadences of the exchange, so a
    // peer that is merely slow to answer is never the subject of the line.
    public const float NoInboundSeconds = 20f;

    private LevelPhase _phase = LevelPhase.Outside;
    private float _since = -1f;
    private bool _noInboundReported, _outboundReported;

    public LevelPhase Phase { get { return _phase; } }
    public bool InLevel { get { return _phase == LevelPhase.InLevel; } }
    // Whether a level clock is running at all. False outside a level, on a result page, and while the level that is
    // up has never handed over a readable start time - an unstarted clock is not a clock reading zero.
    public bool Running { get { return _since >= 0f; } }
    public bool NoInboundReported { get { return _noInboundReported; } }
    public bool OutboundReported { get { return _outboundReported; } }

    // How long the level that is up now has been up. Zero when no level clock is running - which includes a level
    // whose start was never readable - so a caller can print it without asking whether it may.
    public float Seconds(float now)
    {
        if (!float.IsFinite(_since) || _since < 0f || !float.IsFinite(now) || now < _since) return 0f;
        return now - _since;
    }

    // One tick enters its phase. Answers true when this tick began a new level.
    public bool Enter(LevelPhase phase, float now)
    {
        if (phase == _phase)
        {
            // A level whose first frame handed over no readable clock starts at the first frame that does. The phase
            // is what says the level is up; the only thing missing is a wall-clock reading. This is deliberately not
            // a new level - the caller's evidence is not dropped for it - and nothing is invented: `_since` stays
            // unstarted until a readable reading arrives, so no age is ever measured from a zero that was not one.
            if (phase == LevelPhase.InLevel && !float.IsFinite(_since) && float.IsFinite(now)) _since = now;
            return false;
        }
        _phase = phase;
        if (phase != LevelPhase.InLevel)
        {
            // The level is over - a lobby, a generation, a result page. The clock stops where it was; the
            // latches are left alone because a result page still quotes the level that produced it, and
            // the next `InLevel` clears them itself.
            _since = -1f;
            return false;
        }
        // `now` is whatever the caller's clock answered, and a build whose clock is not up yet answers NaN for a
        // frame. Writing 0 there - which is what this line used to do - made the clock claim the level began at
        // t=0: the next frame with a readable clock then measured a whole session's worth of level, and the
        // inbound warning fired on a level that had only just begun, which is exactly the false alarm the
        // per-level clock was written to remove. Unstarted is not zero: a clock that cannot be read starts no
        // clock at all, and says so through `Running` and `Seconds`.
        _since = float.IsFinite(now) ? now : float.NaN;
        _noInboundReported = false;
        _outboundReported = false;
        return true;
    }

    // The outbound line is owed on the first row put on the wire in this level - and, because a result
    // page still carries rows, there too if the level itself never put one out.
    public bool OutboundOwed { get { return _phase != LevelPhase.Outside && !_outboundReported; } }

    public void OutboundWritten() { _outboundReported = true; }

    // The inbound warning is owed when the level has been up for `NoInboundSeconds`, the roster holds at
    // least one human peer who is not this machine, and not one packet from any of them has been handled
    // since this level began. Answers true once; the latch is what keeps it to one line per level.
    public bool NoInboundOwed(float now, int humanPeers, int heardPeers)
    {
        if (_noInboundReported || !InLevel || !float.IsFinite(_since) || _since < 0f) return false;
        if (humanPeers <= 0 || heardPeers > 0) return false;
        if (!float.IsFinite(now) || now < _since) return false;
        if (now - _since < NoInboundSeconds) return false;
        _noInboundReported = true;
        return true;
    }

    // A new expedition starts from nothing, the same way `CombatSync.ResetExpedition` empties the
    // evidence it was decided on.
    public void Forget()
    {
        _phase = LevelPhase.Outside;
        _since = -1f;
        _noInboundReported = false;
        _outboundReported = false;
    }
}
