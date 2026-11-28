using System;
using System.Globalization;
using System.Text;

namespace HawChow.GTFOCombatPanel;

// The settlement board, written to disk exactly as it reads on screen. Nothing is recomputed and nothing is
// filtered: a figure the board showed as a dash or with a tilde is stored in that same form, so a file read
// months later cannot be mistaken for a measurement that was never made. Pure text building, no Unity and no
// file system, so the shape of the file is pinned by the smoke suite.
public static class ResultsLog
{
    // `run` is the squad exchange's run counter for the expedition this screen belongs to. It is written
    // with the figures so a file read months later says which run its numbers describe, which is the one
    // thing a straggler's row and a fresh row cannot be told apart by once the file is on disk.
    public static string Build(bool succeeded, DateTime writtenAt, string version, ImpactBoardLayout board, ImpactReport report, ExpeditionMetadata metadata = default, uint run = 0)
    {
        if (board == null || report == null || report.Entries.Count == 0) return string.Empty;
        var text = new StringBuilder(4096);
        text.Append("{\n");
        text.Append("  \"mod\": \"CombatPanel\",\n");
        text.Append("  \"version\": \"").Append(Escape(version)).Append("\",\n");
        text.Append("  \"writtenAt\": \"").Append(writtenAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("\",\n");
        text.Append("  \"result\": \"").Append(succeeded ? "success" : "failure").Append("\",\n");
        text.Append("  \"run\": ").Append(run.ToString(CultureInfo.InvariantCulture)).Append(",\n");
        text.Append("  \"rundown\": \"").Append(Escape(metadata.Rundown)).Append("\",\n");
        text.Append("  \"levelName\": \"").Append(Escape(metadata.LevelName)).Append("\",\n");
        // 1.19.62: the round's created-enemy figure - one value for the squad, written beside the player rows
        // instead of inside them, because it is not a player's figure. `created` is the number the settlement line
        // printed, and `null` when this machine could not prove one: an unproven figure is archived as unknown and
        // never as a guessed zero, which is what `scope` and `known` are there to say. No existing key, row label or
        // player figure moves or changes type because of this one.
        AppendSpawns(text, report.Spawns);
        text.Append("  \"players\": [\n");
        for (int index = 0; index < report.Entries.Count; index++)
        {
            ImpactEntry entry = report.Entries[index];
            int column = index + 1;
            if (column >= board.ColumnCount) break;
            text.Append("    {\n");
            text.Append("      \"place\": ").Append(entry.Rank.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            text.Append("      \"name\": \"").Append(Escape(entry.Name)).Append("\",\n");
            text.Append("      \"score\": ").Append(Decimal(entry.Score)).Append(",\n");
            // The new counters are written as the numbers they are, not only as the text of a cell: the cell
            // carries a merged rendering ("12 (3 ~sleeping)"), which cannot be read back into figures.
            AppendCounters(text, entry.Input == null ? null : entry.Input.Counters);
            // Every row of the board under the name it carries on screen, in board order - exactly one key per row.
            //
            // 1.19.60: the supply row used to be written twice. 1.19.34 added a second key under the released
            // three-field label (`补给效率 / 本人 / 弹药`, `Supply efficiency / self / ammo`) so that a reader of the
            // archived files kept finding the key it had always parsed. That alias was a *rebuilt* cell, never a second
            // measurement: its second field was the owner's total, where this build's cell has that player's ammo packs -
            // so the same player's two lines disagreed about what their own second field meant, and a reader could not
            // tell which shape it was holding (the archive carries no language field, so a Chinese and an English build
            // wrote two different alias keys for the same figures). Nothing in this mod ever read it back, and every
            // number the alias quoted is still written below: the total is `counters.selfSupplyUses`, and the
            // ammo/tool/med split is `counters.selfAmmoUses` / `selfToolUses` / `selfMedicalUses` beside their two
            // "known" flags. A script that parsed the old key moves to `counters`, which is the same measurement as
            // numbers rather than as text.
            for (int row = 1; row < board.Rows; row++)
            {
                string label = Label(board, row);
                if (string.IsNullOrEmpty(label)) continue;
                text.Append("      \"").Append(Escape(label)).Append("\": \"").Append(Escape(board.Joined(row, column))).Append("\",\n");
            }
            // 1.19.64: the rows of a slot that held more than one measured weapon, one entry each. Nothing above
            // changes: the board's own keys still carry the one row the slot drew, and this block carries the rest.
            AppendSlotWeapons(text, entry.Input);
            text.Append("      \"nameCell\": \"").Append(Escape(board.Joined(0, column))).Append("\"\n");
            text.Append(index + 1 == report.Entries.Count ? "    }\n" : "    },\n");
        }
        text.Append("  ]\n}\n");
        return text.ToString();
    }

    // The kill-state split, the player's own supply consumption and the health it actually lost, as figures.
    // A row that carries no counters at all writes none: nothing here is invented to fill a shape.
    private static void AppendCounters(StringBuilder text, ImpactCounters counters)
    {
        if (counters == null) return;
        text.Append("      \"counters\": {\n");
        text.Append("        \"sleepingMinorKills\": ").Append(Count(counters.SleepingMinorKills)).Append(",\n");
        text.Append("        \"sleepingEliteKills\": ").Append(Count(counters.SleepingEliteKills)).Append(",\n");
        text.Append("        \"sleepingBossKills\": ").Append(Count(counters.SleepingBossKills)).Append(",\n");
        text.Append("        \"sleepingMeleeKills\": ").Append(Count(counters.SleepingMeleeKills)).Append(",\n");
        text.Append("        \"sleepingScoutKills\": ").Append(Count(counters.SleepingScoutKills)).Append(",\n");
        text.Append("        \"unknownStateMinorKills\": ").Append(Count(counters.UnknownStateMinorKills)).Append(",\n");
        text.Append("        \"unknownStateEliteKills\": ").Append(Count(counters.UnknownStateEliteKills)).Append(",\n");
        text.Append("        \"unknownStateBossKills\": ").Append(Count(counters.UnknownStateBossKills)).Append(",\n");
        text.Append("        \"unknownStateMeleeKills\": ").Append(Count(counters.UnknownStateMeleeKills)).Append(",\n");
        text.Append("        \"unknownStateScoutKills\": ").Append(Count(counters.UnknownStateScoutKills)).Append(",\n");
        text.Append("        \"selfSupplyUses\": ").Append(Count(counters.SelfSupplyUses)).Append(",\n");
        text.Append("        \"selfAmmoUses\": ").Append(Count(counters.SelfAmmoUses)).Append(",\n");
        // 1.19.34: the split of those packs, and whether it was ever split at all. A row restored from a v8 peer writes the two
        // kinds as 0 with `selfSupplyKindsKnown` false: the pair above is that peer's, and the split is an absence the reader can
        // see rather than a zero it has to guess at.
        text.Append("        \"selfToolUses\": ").Append(Count(counters.SelfToolUses)).Append(",\n");
        text.Append("        \"selfMedicalUses\": ").Append(Count(counters.SelfMedicalUses)).Append(",\n");
        text.Append("        \"selfSupplyKindsKnown\": ").Append(counters.SelfSupplyKindsKnown ? "true" : "false").Append(",\n");
        // 1.19.41: `selfMedicalUses` is a native application this machine either confirmed or did not, so the archive
        // says which one it is. A row whose medkits were counted on somebody else's machine writes 0 beside
        // `selfMedicalKnown: false` - an absence a reader can see - instead of a zero it has to guess at. The key is
        // additive: every existing key and its type are unchanged, and the v8 protocol is not touched.
        text.Append("        \"selfMedicalKnown\": ").Append(counters.SelfMedicalKnown ? "true" : "false").Append(",\n");
        text.Append("        \"healthLost\": ").Append(Decimal(counters.HealthLost)).Append("\n");
        text.Append("      },\n");
    }
    // 1.19.64: one entry per measured row of a gear slot that held more than one weapon. The board draws a single row
    // per slot, so the others are written here with the figures they measured - `shown` marks the entry the slot's
    // cells belong to, and nothing in this block is a sum, a difference or a remainder. A player whose slots each
    // hold one weapon writes no block at all: the file is exactly the file it always was.
    //
    // Every figure keeps the observation it came from (review): a figure the row never measured is `null`, never a 0 a
    // reader would count as measured; a counted zero stays 0; damage and accuracy are estimated separately, each under
    // its own flag, so a row whose damage was estimated is not written as if its accuracy were too. A shot count of 0
    // beside positive hits was never read and is written as `null` with the hits kept.
    private static void AppendSlotWeapons(StringBuilder text, ImpactInput input)
    {
        if (input == null || input.SlotWeapons == null || input.SlotWeapons.Count == 0) return;
        text.Append("      \"weapons\": [\n");
        for (int index = 0; index < input.SlotWeapons.Count; index++)
        {
            SlotWeapon weapon = input.SlotWeapons[index];
            text.Append("        { \"slot\": ").Append(weapon.Slot.ToString(CultureInfo.InvariantCulture));
            text.Append(", \"name\": \"").Append(Escape(weapon.Name)).Append("\"");
            text.Append(", \"shown\": ").Append(weapon.Shown ? "true" : "false");
            text.Append(", \"damage\": ").Append(weapon.Damage.HasValue ? Decimal(weapon.Damage.Value) : "null");
            text.Append(", \"damageEstimated\": ").Append(weapon.DamageEstimated ? "true" : "false");
            text.Append(", \"shots\": ").Append(Count(weapon.Shots));
            text.Append(", \"hits\": ").Append(Count(weapon.Hits));
            text.Append(", \"weakspots\": ").Append(Count(weapon.Weakspots));
            text.Append(", \"accuracyEstimated\": ").Append(weapon.AccuracyEstimated ? "true" : "false");
            text.Append(index + 1 == input.SlotWeapons.Count ? " }\n" : " },\n");
        }
        text.Append("      ],\n");
    }


    // The round's created-enemy figure as the archive keeps it: the same number the settlement line printed, or the
    // explicit unknown. Written as an object of its own so a reader can never mistake it for a player row, and so the
    // shape of every existing key stays exactly as it was.
    //
    //   created - the figure, or `null` when this machine could not prove one (never a guessed 0)
    //   scope   - "squad" (this machine owned the round's level start and still hosts), "local" (its own
    //             observation only) or "unknown"
    //   known   - true only for "squad", the one case where the number is the round's total
    private static void AppendSpawns(StringBuilder text, SpawnTotal total)
    {
        text.Append("  \"").Append(SettlementSpawns.JsonKey).Append("\": {\n");
        text.Append("    \"created\": ");
        if (total.Any) text.Append(Count(total.Created)); else text.Append("null");
        text.Append(",\n");
        text.Append("    \"scope\": \"").Append(SettlementSpawns.ScopeName(total.Scope)).Append("\",\n");
        text.Append("    \"known\": ").Append(total.Known ? "true" : "false").Append("\n");
        text.Append("  },\n");
    }

    private static string Count(long value)
        => (value < 0 ? 0 : value).ToString(CultureInfo.InvariantCulture);

    // 1.19.64: a figure that was never observed is written as JSON null rather than as a 0 - the two are different
    // measurements, and a reader of the file must be able to tell them apart.
    private static string Count(long? value)
        => value.HasValue ? Count(value.Value) : "null";

    private static string Label(ImpactBoardLayout board, int row)
    {
        try
        {
            string[] cell = board.Cell(row, 0);
            return cell == null || cell.Length == 0 ? string.Empty : cell[0];
        }
        catch { return string.Empty; }
    }

    // A score is the one figure this file computes nothing about: it is whatever the board scored. The
    // culture is pinned so the file reads the same on any machine, and a non-finite value can never leak in.
    private static string Decimal(double value)
        => (double.IsFinite(value) ? value : 0).ToString("0.0", CultureInfo.InvariantCulture);

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var text = new StringBuilder(value.Length + 8);
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                default:
                    // Control characters would make the file unreadable as JSON; everything else is kept.
                    if (c < ' ') text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else text.Append(c);
                    break;
            }
        }
        return text.ToString();
    }
}
