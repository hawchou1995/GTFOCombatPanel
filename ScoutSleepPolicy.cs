using System;
using System.Collections.Generic;

namespace HawChow.GTFOCombatPanel;

// Scouts do not use ordinary Hibernate semantics. Patrol, tendril detection and scream charge-up
// remain sleeping until an actual Scream/Response/Done subphase is sampled from a damage context.
// The latch belongs to one spawn of the damage-base pointer; returning to detection cannot undo it.
// Ordinary enemies keep the caller's existing Hibernate reading. Capture is taken before damage,
// so a lethal hit that prevents the scream retains its pre-scream state.
internal enum ScoutSleepPhase
{
    Unknown = 0,
    // Captured as a Scout whose scream state has not been entered: asleep, whatever its tendrils or
    // its patrol are doing.
    Dormant = 1,
    // A tendril registered a player. The Scout is alert but has not screamed, so it is still asleep;
    // it is recorded apart from Dormant only so a log line can name the witness that produced it.
    Alerted = 2,
    // The actual scream began. Awake until the next spawn of this pointer.
    Screamed = 3,
}

internal static class ScoutSleepPolicy
{
    private struct Capture
    {
        internal ScoutSleepPhase Phase;
        internal int Stamp;
    }

    // The generation a capture belongs to. The game side writes `Time.frameCount` before every capture;
    // the tests write it themselves. It exists because one spawn runs several hooks - the agent's
    // setup, the damage base's, the state's - and their order inside that frame is not this mod's to
    // choose, so a spawn reset must not discard evidence the same frame already produced.
    internal static int Now;

    // Diagnostics out. The game side points this at Plugin.Logger.LogInfo; a test can collect it.
    internal static Action<string> Note;

    private static readonly Dictionary<IntPtr, Capture> Captured = new();
    private static bool _notedScream, _notedSleepingKill;

    internal static int Count => Captured.Count;

    // A fresh spawn owns the pointer from here on. Enemies are pooled and one damage base address is
    // handed to the next enemy, so the previous occupant's scream latch must not survive it - but a
    // capture made in this same generation does, because the game runs that spawn's setup in one frame
    // and the state hooks may fire before or after the agent's own setup.
    internal static void Spawn(IntPtr enemy)
    {
        if (enemy == IntPtr.Zero) return;
        if (Now != 0 && Captured.TryGetValue(enemy, out Capture seen) && seen.Stamp == Now) return;
        Captured.Remove(enemy);
    }

    // The enemy is a Scout and has not screamed: asleep. A Scout that already screamed is never marked
    // dormant again - after the scream the game returns it to `ScoutDetection` to keep looking, and that
    // re-entry must not read as a second sleep.
    internal static void Scout(IntPtr enemy) => Mark(enemy, ScoutSleepPhase.Dormant);

    // A tendril has registered a player: alert, still asleep.
    internal static void Alerted(IntPtr enemy) => Mark(enemy, ScoutSleepPhase.Alerted);

    internal static void Screamed(IntPtr enemy, string detail = null)
    {
        if (!Mark(enemy, ScoutSleepPhase.Screamed)) return;
        if (_notedScream) return;
        _notedScream = true;
        Note?.Invoke("[DIAG] a Scout started its actual scream"
            + (string.IsNullOrEmpty(detail) ? string.Empty : " (" + detail + ")")
            + "; from here on its kills are scored as awake, because a Scout's sleeping rule is \"has not screamed yet\".");
    }

    // The witness turned out to belong to an enemy that is not a Scout - a pooled pointer that changed
    // hands - so the captured identity is dropped rather than inherited.
    internal static void Forget(IntPtr enemy)
    {
        if (enemy != IntPtr.Zero) Captured.Remove(enemy);
    }

    private static bool Mark(IntPtr enemy, ScoutSleepPhase phase)
    {
        if (enemy == IntPtr.Zero) return false;
        if (Captured.TryGetValue(enemy, out Capture seen))
        {
            if (seen.Phase == ScoutSleepPhase.Screamed && phase != ScoutSleepPhase.Screamed) return false;
            if (seen.Phase == phase) { seen.Stamp = Now; Captured[enemy] = seen; return false; }
        }
        Captured[enemy] = new Capture { Phase = phase, Stamp = Now };
        return true;
    }

    internal static ScoutSleepPhase Phase(IntPtr enemy)
        => enemy != IntPtr.Zero && Captured.TryGetValue(enemy, out Capture seen) ? seen.Phase : ScoutSleepPhase.Unknown;

    // The rule itself, with no state of its own: a Scout that has not screamed is asleep, a Scout that
    // has screamed is awake, and an enemy this policy never captured is left with the game's own
    // answer - the Hibernate reading every other enemy is graded by.
    internal static bool? Sleeping(bool? gameSleeping, ScoutSleepPhase phase)
        => phase == ScoutSleepPhase.Screamed ? false
            : phase == ScoutSleepPhase.Dormant || phase == ScoutSleepPhase.Alerted ? true
            : gameSleeping;

    internal static bool? Resolve(IntPtr enemy, bool? gameSleeping)
    {
        ScoutSleepPhase phase = Phase(enemy);
        bool? sleeping = Sleeping(gameSleeping, phase);
        if (sleeping == true && gameSleeping != true && phase != ScoutSleepPhase.Unknown && !_notedSleepingKill)
        {
            _notedSleepingKill = true;
            Note?.Invoke("[DIAG] a Scout was struck before its actual scream; the damage scope captures sleeping=true although locomotion is not Hibernate.");
        }
        return sleeping;
    }

    // An expedition boundary. Each run spawns its own enemies, and a pointer from the last run means
    // nothing in this one.
    internal static void Clear()
    {
        Captured.Clear();
        _notedScream = _notedSleepingKill = false;
    }
}
