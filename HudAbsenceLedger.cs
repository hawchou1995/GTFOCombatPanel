using System;
using System.Collections.Generic;

namespace HawChow.GTFOCombatPanel;

// ---------------------------------------------------------------------------------------------------
// HudAbsenceLedger.cs - the "no proven place" family of the in-level panel: its closed vocabulary, and the one bounded
// ledger that decides which of those lines a round may still write.
//
// The family exists because the automatic ladder answers only with proofs (1.19.47) and a page with no proof is still drawn
// (1.19.48/1.19.49): the anchor that has not been measured yet, a column this build can only read in part, a page with no free
// rung, the conservative place that was drawn instead, and the one poll on which the table is too big for the window and
// nothing is drawn at all. Each of them is a *fact about the page*, so each is written in Info and latched on its meaning -
// never on its numbers: the recorded round wrote the same partial column four times, once per table width (459, 479, 471 and
// 482 px), because the note it latched on carried the rectangle.
//
// A latch that is keyed by meaning is only safe if it can never refuse a first line for a page that has a new fact to report,
// and a latch that is bounded is only safe if the bound cannot be reached by the family itself. Those two requirements meet in
// one place: the vocabulary is a *closed set* (ten keys, `Capacity`), so
//
//   * every legitimate key has its own slot - the ten keys fill the ledger exactly, and the last one in `SpotReasons` order,
//     or the deferral that is the only poll which draws nothing at all, can never be crowded out by the keys before it. A
//     count below the size of the vocabulary would do exactly that: with eight slots the ninth and tenth first lines were
//     silently dropped, and a round that began with an anchor absence and a partial column could lose the one line that says
//     "this poll drew nothing and here is why";
//   * a key outside the vocabulary is refused outright rather than counted, so a future call site that interpolates the
//     width, the window or the retry count into its key cannot grow the ledger at all - it simply does not print, and the
//     suite fails;
//   * a repeat of a key already claimed is refused, so one round of a 0.25 s poll costs the family at most `Capacity` lines.
//
// The ledger is emptied by the round (`PanelBehaviour.ForgetBandRound`: a new level, a configuration reload, a resolution
// change) and by nothing else - not by a recovery to the proven rung, not by an F8 press, not by an ordinary poll. That is
// what keeps a page that flips complete/partial from re-printing its absence at the poll rate.
//
// Free of Unity and BepInEx on purpose, so all of it is asserted headlessly in tests/GTFOCombatPanel.Smoke - including that
// all ten keys are really reachable through one ledger and that an eleventh is not.
public static class HudAbsence
{
    // The three answers of `PanelBehaviour.TryAnchoredOrigin`.
    public const string AnchorUnmeasured = "anchor:unmeasured";
    public const string AnchorPartial = "anchor:partial";
    public const string AnchorNoFreePlace = "anchor:no-free-place";
    // The two answers of `PanelBehaviour.DeferFallback` - the only poll of the family that draws nothing.
    public const string DeferOverlap = "defer:overlap";
    public const string DeferIncomplete = "defer:incomplete";
    // The candidate that answered `HudFallbackSpot.TryResolve`, one key per reason it can answer.
    public const string SpotPrefix = "spot:";
    public static readonly string[] SpotReasons =
    {
        "right-row", "below-measured", "above-measured", "right-column", "least-overlap"
    };

    // The size of the vocabulary, which is also the ledger's capacity: three anchor answers, two deferrals, five spot
    // reasons. Written as one expression so a key added to the family cannot silently outgrow the bound.
    public const int Capacity = 3 + 2 + 5;

    public static string Spot(string reason)
        => string.IsNullOrEmpty(reason) ? null : SpotPrefix + reason;

    // Whether `reason` is one of the five `HudFallbackSpot` can answer. `none` is deliberately not one of them: it is the
    // value TryResolve leaves behind when it refuses, and the caller only reports a place that was really answered.
    public static bool IsSpotReason(string reason)
    {
        if (string.IsNullOrEmpty(reason)) return false;
        for (int index = 0; index < SpotReasons.Length; index++)
        {
            if (string.Equals(SpotReasons[index], reason, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    // Whether `key` is in the family's vocabulary at all.
    public static bool IsKnown(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (string.Equals(key, AnchorUnmeasured, StringComparison.Ordinal)) return true;
        if (string.Equals(key, AnchorPartial, StringComparison.Ordinal)) return true;
        if (string.Equals(key, AnchorNoFreePlace, StringComparison.Ordinal)) return true;
        if (string.Equals(key, DeferOverlap, StringComparison.Ordinal)) return true;
        if (string.Equals(key, DeferIncomplete, StringComparison.Ordinal)) return true;
        if (!key.StartsWith(SpotPrefix, StringComparison.Ordinal)) return false;
        return IsSpotReason(key.Substring(SpotPrefix.Length));
    }
}

// One panel's ledger for one round. `Claim` answers true exactly once per key of the vocabulary, so a caller that asks for
// every line it has a fact for gets every one of them, in whatever order the page happens to produce them, and a caller that
// keeps asking gets silence.
public sealed class HudAbsenceLedger
{
    // Preallocated at the size of the vocabulary: claiming never allocates, and the list can never grow past `Capacity`.
    private readonly List<string> _claimed = new List<string>(HudAbsence.Capacity);

    // How many of the family's lines this round has really written. Diagnostics only.
    public int Count => _claimed.Count;

    // Whether this round has written any line of the family at all.
    public bool Any => _claimed.Count > 0;

    // A new round: a new level, a configuration reload or a resolution change. Nothing inside a round calls this.
    public void Clear()
    {
        if (_claimed.Count > 0) _claimed.Clear();
    }

    // Whether this round may still write the line `key` names. A key outside the vocabulary is refused - never counted - so
    // the bound is the vocabulary's own size and no future interpolation of a measurement into a key can widen it.
    public bool Claim(string key)
    {
        if (!HudAbsence.IsKnown(key)) return false;
        if (_claimed.Contains(key)) return false;
        if (_claimed.Count >= HudAbsence.Capacity) return false;
        _claimed.Add(key);
        return true;
    }
}
