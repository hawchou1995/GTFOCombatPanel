using System;
using System.Collections.Generic;
using HawChow.GTFOCombatPanel;

internal static partial class Program
{
    // The Scout's sleeping rule is its own: it is asleep for as long as it has not screamed, and awake
    // from the moment its scream state is entered. These scenarios pin that rule, the capture it reads,
    // and the two boundaries that keep a capture from outliving what it describes - the pooled pointer
    // handed to the next enemy, and the expedition that ends.
    private static void RunScoutSleepScenarios()
    {
        ScoutSleepPolicy.Clear();
        ScoutSleepPolicy.Note = null;

        // The rule on its own. The game answers `false` for every Scout, because a Scout never stands in
        // `ES_StateEnum.Hibernate`; the Scout rule replaces that answer and nothing else.
        Check(ScoutSleepPolicy.Sleeping(false, ScoutSleepPhase.Dormant) == true,
            "SC: a Scout that has not screamed is asleep even though its locomotion state is not Hibernate");
        Check(ScoutSleepPolicy.Sleeping(false, ScoutSleepPhase.Alerted) == true,
            "SC: a tendril that registered a player is alert but still a sleeping kill, because the Scout has not screamed");
        Check(ScoutSleepPolicy.Sleeping(null, ScoutSleepPhase.Dormant) == true,
            "SC: an unreadable locomotion state does not turn a dormant Scout's kill into an awake one");
        Check(ScoutSleepPolicy.Sleeping(false, ScoutSleepPhase.Screamed) == false,
            "SC: a Scout that screamed is an awake kill");
        Check(ScoutSleepPolicy.Sleeping(true, ScoutSleepPhase.Screamed) == false,
            "SC: the Scout rule wins over a Hibernate reading that cannot belong to a Scout");
        Check(ScoutSleepPolicy.Sleeping(true, ScoutSleepPhase.Unknown) == true
            && ScoutSleepPolicy.Sleeping(false, ScoutSleepPhase.Unknown) == false
            && ScoutSleepPolicy.Sleeping(null, ScoutSleepPhase.Unknown) == null,
            "SC: an enemy the policy never captured keeps the game's own sleeping answer, unchanged");

        // The latch: entering the scream state is the end of the sleep, and nothing the game does
        // afterwards - returning the Scout to `ScoutDetection` to keep looking, or a second detection -
        // may read as a second sleep.
        ScoutSleepPolicy.Clear();
        IntPtr scout = new IntPtr(0x51);
        IntPtr other = new IntPtr(0x52);
        ScoutSleepPolicy.Now = 10;
        ScoutSleepPolicy.Scout(scout);
        Check(ScoutSleepPolicy.Phase(scout) == ScoutSleepPhase.Dormant, "SC: detection captures the Scout as dormant");
        ScoutSleepPolicy.Alerted(scout);
        Check(ScoutSleepPolicy.Phase(scout) == ScoutSleepPhase.Alerted, "SC: a detected player is captured as alert");
        ScoutSleepPolicy.Screamed(scout);
        Check(ScoutSleepPolicy.Phase(scout) == ScoutSleepPhase.Screamed, "SC: the scream state captures the Scout as awake");
        ScoutSleepPolicy.Scout(scout);
        ScoutSleepPolicy.Alerted(scout);
        Check(ScoutSleepPolicy.Phase(scout) == ScoutSleepPhase.Screamed,
            "SC: re-entering detection after the scream cannot restore the sleeping state");
        Check(ScoutSleepPolicy.Phase(other) == ScoutSleepPhase.Unknown,
            "SC: one Scout's scream latch is not another enemy's");
        ScoutSleepPolicy.Screamed(IntPtr.Zero);
        Check(ScoutSleepPolicy.Phase(IntPtr.Zero) == ScoutSleepPhase.Unknown && ScoutSleepPolicy.Count == 1,
            "SC: a missing enemy identity is never captured");

        // The ledger asks the policy when a blow opens, which is the moment before the blow lands - a
        // Scout killed by a shot never screams, and stays a sleeping kill.
        ScoutSleepPolicy.Clear();
        ScoutSleepPolicy.Now = 20;
        var ledger = new EnemyDeathLedger();
        ScoutSleepPolicy.Scout(scout);
        var dormant = ledger.Begin(scout, 7, DamageKind.Bullet, false, 40);
        Check(dormant != null && dormant.Sleeping == true,
            "SC: the death ledger scores a captured, not-yet-screaming Scout as a sleeping kill");
        Check(ledger.Complete(dormant, 0, true), "SC: a sleeping Scout still counts as exactly one kill");
        ScoutSleepPolicy.Screamed(scout);
        ScoutSleepPolicy.Now = 21;
        var awake = ledger.Begin(scout, 7, DamageKind.Melee, false, 40);
        Check(awake != null && awake.Sleeping == false, "SC: the same Scout after its scream is an awake kill");
        ledger.Complete(awake, 0, true);

        // A pooled pointer is handed to the next enemy: the previous occupant's scream latch is not
        // inherited, and the enemy under it is graded by the game's own reading again.
        ScoutSleepPolicy.Now = 30;
        ledger.Spawn(scout);
        Check(ScoutSleepPolicy.Count == 0, "SC: a spawn drops the capture the previous occupant left on the pointer");
        var recycled = ledger.Begin(scout, 7, DamageKind.Bullet, false, 40);
        Check(recycled != null && recycled.Sleeping == false,
            "SC: a recycled pointer is no longer the screaming Scout it used to be");
        ledger.Complete(recycled, 0, true);

        // A spawn's own reset runs inside the frame that identified the Scout, and their order is not
        // this mod's to choose: evidence captured in the same generation survives it.
        ScoutSleepPolicy.Clear();
        ScoutSleepPolicy.Now = 40;
        ScoutSleepPolicy.Scout(scout);
        ScoutSleepPolicy.Spawn(scout);
        Check(ScoutSleepPolicy.Phase(scout) == ScoutSleepPhase.Dormant,
            "SC: a spawn reset inside the generation that captured the Scout does not discard the capture");
        ScoutSleepPolicy.Now = 41;
        ScoutSleepPolicy.Spawn(scout);
        Check(ScoutSleepPolicy.Phase(scout) == ScoutSleepPhase.Unknown,
            "SC: the next generation's spawn reset does discard it");

        // An expedition boundary: every pointer from the last run means nothing in this one.
        ScoutSleepPolicy.Clear();
        ScoutSleepPolicy.Now = 50;
        ScoutSleepPolicy.Scout(scout);
        ScoutSleepPolicy.Screamed(other);
        ledger.Clear();
        Check(ScoutSleepPolicy.Count == 0, "SC: a new expedition starts with no Scout capture at all");
        var nextRun = ledger.Begin(other, 7, DamageKind.Bullet, false, 40);
        Check(nextRun != null && nextRun.Sleeping == false,
            "SC: a pointer reused across expeditions is graded by the game's reading, not by the last run's capture");
        ledger.Complete(nextRun, 0, true);

        // The two diagnostics say why a Scout was scored the way it was, once each, and re-arm with the
        // expedition rather than once per process.
        var notes = new List<string>();
        ScoutSleepPolicy.Clear();
        ScoutSleepPolicy.Note = notes.Add;
        ScoutSleepPolicy.Now = 60;
        ScoutSleepPolicy.Screamed(scout);
        ScoutSleepPolicy.Screamed(scout);
        ScoutSleepPolicy.Screamed(other);
        Check(notes.Count == 1 && notes[0].Contains("scream"),
            "SC: the scream is reported once, not once per Scout and not once per callback");
        var policyLedger = new EnemyDeathLedger();
        IntPtr sleeper = new IntPtr(0x53);
        ScoutSleepPolicy.Scout(sleeper);
        policyLedger.Begin(sleeper, 7, DamageKind.Bullet, false, 40);
        policyLedger.Begin(sleeper, 8, DamageKind.Bullet, false, 40);
        Check(notes.Count == 2 && notes[1].Contains("sleeping=true"),
            "SC: the first sleeping Scout damage scope is reported once without claiming an unobserved kill");
        ScoutSleepPolicy.Clear();
        Check(notes.Count == 2 && ScoutSleepPolicy.Count == 0, "SC: an expedition boundary re-arms the diagnostics");
        ScoutSleepPolicy.Now = 61;
        ScoutSleepPolicy.Screamed(scout);
        Check(notes.Count == 3, "SC: the next expedition reports its own first scream");

        // A witness that turns out to belong to an enemy that is not a Scout is dropped rather than
        // inherited: `IsScout` is what the kill itself is graded by.
        ScoutSleepPolicy.Clear();
        ScoutSleepPolicy.Now = 70;
        ScoutSleepPolicy.Scout(scout);
        ScoutSleepPolicy.Forget(scout);
        Check(ScoutSleepPolicy.Phase(scout) == ScoutSleepPhase.Unknown,
            "SC: a pointer proven not to be a Scout carries no Scout identity");
        var ordinary = ledger.Begin(scout, 7, DamageKind.Bullet, true, 40);
        Check(ordinary != null && ordinary.Sleeping == true,
            "SC: an ordinary hibernating enemy keeps its own sleeping reading after a dropped Scout witness");
        ledger.Complete(ordinary, 0, true);
        ScoutSleepPolicy.Note = null;
        ScoutSleepPolicy.Clear();
    }
}
