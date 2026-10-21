using HawChow.GTFOCombatPanel;

internal static partial class Program
{
    private static void RunReviveAttributionScenarios()
    {
        var ledger = new ReviveAttributionLedger();
        Check(ReviveAttributionLedger.CanTrackOwner(true, true, false), "R: the host tracks any slotted owner, including a bot");
        Check(ReviveAttributionLedger.CanTrackOwner(true, false, true), "R: a client tracks its local owner");
        Check(!ReviveAttributionLedger.CanTrackOwner(true, false, false), "R: a client does not invent remote-owner measurements");
        Check(!ReviveAttributionLedger.CanTrackOwner(false, true, true), "R: an owner outside the roster is never credited");

        Check(!ledger.Observe(0, 4, true, 1), "R: a revive without a target is rejected");
        Check(!ledger.Observe(3, 0, true, 1), "R: a revive without a source is rejected");
        Check(!ledger.Observe(3, 3, true, 1), "R: self-revive does not become a teammate rescue");
        Check(!ledger.TryTakeSuccessfulRevive(3, 1, out _), "R: interaction alone never credits an incomplete rescue");
        Check(!ledger.Observe(3, 4, false, 1), "R: an unmatched release is not treated as a successful rescue");
        Check(ledger.Observe(3, 4, true, 2), "R: replicated active state stores target and rescuer separately");
        Check(ledger.Observe(5, 6, true, 2), "R: simultaneous revive candidates are keyed by target");
        Check(ledger.TryTakeSuccessfulRevive(3, 10, out ulong rescuer) && rescuer == 4,
            "R: confirmed recovery resolves to the actual rescuer");
        Check(!ledger.TryTakeSuccessfulRevive(3, 10, out _), "R: one downed-player completion can only be credited once");
        Check(ledger.Observe(5, 6, false, 12), "R: completed interaction state gets a short callback-order grace");
        Check(ledger.TryTakeSuccessfulRevive(5, 14, out rescuer) && rescuer == 6,
            "R: completion just after release uses the matching pending rescuer");
        Check(ledger.Observe(7, 8, true, 20), "R: cancellable revive candidate can be recorded");
        Check(!ledger.Observe(7, 9, false, 21), "R: another rescuer's unmatched release cannot rewrite ownership");
        Check(ledger.Observe(7, 8, false, 21), "R: the matching release starts the completion grace window");
        Check(!ledger.TryTakeSuccessfulRevive(7, 24.01, out _), "R: expired/cancelled interaction is not credited");
        Check(ledger.Observe(11, 12, true, 30), "R: a new active candidate replaces stale data for the same target");
        Check(ledger.Observe(11, 13, true, 31), "R: a later valid rescuer supersedes an earlier attempt");
        Check(ledger.TryTakeSuccessfulRevive(11, 32, out rescuer) && rescuer == 13,
            "R: a success is credited to the last active rescuer, not an earlier attempt");
        Check(ledger.Observe(15, 16, true, 40), "R: current expedition retains pending revive state");
        ledger.Clear();
        Check(!ledger.TryTakeSuccessfulRevive(15, 41, out _) && ledger.Count == 0,
            "R: expedition reset drops all pending reviver attribution");
    }
}
