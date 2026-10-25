using HawChow.GTFOCombatPanel;

internal static partial class Program
{
    // The rule behind `CombatTracker.NoteLocalOutgoing`: what a blow this machine dealt is worth. The health
    // the enemy lost across the call is the applied damage and always wins; the figure the weapon carried in
    // is the fallback for the one topology where the two reads cannot differ - a client whose host applies the
    // blow and reports it later. Every case below is a way that fallback could credit the wrong number.
    private static void RunLocalOutgoingScenarios()
    {
        Check(LocalOutgoingRule.Amount(100f, 58f, 30f) == 42,
            "readable health loss is the applied damage and is not lowered to the figure the weapon carried in");
        Check(LocalOutgoingRule.Amount(100f, 40f, 999f) == 60,
            "a larger carried figure cannot inflate a readable health loss");
        Check(LocalOutgoingRule.Amount(100f, 58f, float.NaN) == 42,
            "an unreadable carried figure does not veto a readable health loss");
        // This is the reported defect: the host applies the blow, so the client's own call returns the health
        // unchanged and the fallback is the only figure left.
        Check(LocalOutgoingRule.Amount(100f, 100f, 30f) == 30,
            "an unchanged limb health falls back to the carried figure, which is the local damage fallback itself");
        Check(LocalOutgoingRule.Amount(20f, 20f, 300f) == 20,
            "the fallback is capped by the health the limb still had");
        Check(LocalOutgoingRule.Amount(0f, 0f, 30f) == 0 && LocalOutgoingRule.Amount(-50f, -50f, 30f) == 0,
            "a limb already at zero or behind it earns no damage, so a corpse is never credited");
        Check(LocalOutgoingRule.Amount(100f, -100f, 30f) == 100,
            "overkill is bounded by the health the limb had when the blow landed");
        Check(LocalOutgoingRule.Amount(100f, 130f, 25f) == 25,
            "a limb that gained health during the call is not negative damage and still caps the fallback");
        Check(LocalOutgoingRule.Amount(float.NaN, 0f, 30f) == 0 && LocalOutgoingRule.Amount(100f, float.PositiveInfinity, 30f) == 0,
            "a health reading that could not be taken credits nothing");
        Check(LocalOutgoingRule.Amount(100f, 100f, float.NaN) == 0 && LocalOutgoingRule.Amount(100f, 100f, float.PositiveInfinity) == 0,
            "a carried figure that is not a finite number credits nothing");
        Check(LocalOutgoingRule.Amount(100f, 100f, -5f) == 0 && LocalOutgoingRule.Amount(100f, 100f, 0f) == 0,
            "a negative or zero carried figure never subtracts and never fabricates damage");

        // The local fallback records its own blow as an estimate (`RecordDamage(..., estimated: true)`), so the
        // row it lands on must print the marker every other estimate uses, never a measured figure.
        var fallbackRow = new PlayerCombatStats();
        fallbackRow.AddEnemyDamage(37.5, true);
        Check(CombatStatsFormatter.DamageText(fallbackRow) == "~37.5",
            "a locally measured blow is printed as an estimate rather than as a measured figure");
        Check(CombatStatsFormatter.Value(fallbackRow, PanelColumn.DamageAndShare, 75) == "~37.5 ~50.0%",
            "the fallback damage and the share it feeds both keep the estimate marker");
    }
}
