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
            // Every row of the board under the name it carries on screen, in board order.
            for (int row = 1; row < board.Rows; row++)
            {
                string label = Label(board, row);
                if (string.IsNullOrEmpty(label)) continue;
                text.Append("      \"").Append(Escape(label)).Append("\": \"").Append(Escape(board.Joined(row, column))).Append("\",\n");
            }
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
        text.Append("        \"healthLost\": ").Append(Decimal(counters.HealthLost)).Append("\n");
        text.Append("      },\n");
    }

    private static string Count(long value)
        => (value < 0 ? 0 : value).ToString(CultureInfo.InvariantCulture);

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
